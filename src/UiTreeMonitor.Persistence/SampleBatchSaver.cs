// 批量落盘器：采样点先进内存缓冲，后台线程按任务自己的策略（每 N 点 / 每 T 秒，先到先写）批量追加 CSV。
// 设计目标：Enqueue O(1) 无阻塞（不占用采集线程与 UI 线程）；落盘目录按天滚动：{dataRoot}\{yyyyMMdd}\{任务名}_{yyyyMMdd}.csv
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Persistence;

/// <summary>单个任务的落盘策略。</summary>
public sealed record SavePolicy(int BatchPoints, int IntervalSec);

/// <summary>
/// 采样批量落盘器：内存缓冲 + 后台写盘线程。
/// CSV 格式：时间戳,属性,值（UTF-8 with BOM，Excel 可直接打开）。
/// </summary>
public sealed class SampleBatchSaver : IDisposable
{
    private readonly string _dataRoot;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, TaskBuffer> _buffers = new(); // taskKey → 待写缓冲

    private sealed class TaskBuffer
    {
        public required string TaskName;
        public required SavePolicy Policy;
        public string TargetLabel = "";        // CSV 第一列：监控对象（属性路径）
        public int ValueCount = 1;             // 每采样点的值列数（提取规则数；无规则=1）
        public DateTime CreatedAt = DateTime.Now; // 文件名中的"创建时间"
        public readonly List<SampleRecord> Pending = new();
        public DateTime LastFlush = DateTime.Now;
    }

    private Thread? _writer;
    private volatile bool _running;
    private readonly AutoResetEvent _wake = new(false);

    public SampleBatchSaver(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    /// <summary>注册/更新任务落盘策略（创建任务时调用）。monitorTarget=CSV 首列监控对象名；valueCount=每采样点值列数。</summary>
    public void SetPolicy(Guid taskId, string taskName, SavePolicy policy, string monitorTarget = "", int valueCount = 1)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(taskId, out var buf))
            {
                _buffers[taskId] = new TaskBuffer
                {
                    TaskName = taskName,
                    Policy = policy,
                    TargetLabel = monitorTarget,
                    ValueCount = Math.Max(1, valueCount),
                    CreatedAt = DateTime.Now,
                };
            }
            else
            {
                buf.TaskName = taskName;
                buf.Policy = policy;
                buf.TargetLabel = monitorTarget;
                buf.ValueCount = Math.Max(1, valueCount);
            }
        }
    }

    /// <summary>采样入队（采集线程/UI 线程调用，O(1) 无阻塞）。</summary>
    public void Enqueue(Guid taskId, string taskName, IEnumerable<SampleRecord> records)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(taskId, out var buf))
                _buffers[taskId] = buf = new TaskBuffer { TaskName = taskName, Policy = new SavePolicy(1, 5) };
            buf.Pending.AddRange(records);
        }
        _wake.Set();
    }

    /// <summary>启动后台写盘线程。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SampleBatchSaver" };
        _writer.Start();
    }

    private void WriterLoop()
    {
        while (_running)
        {
            // 无数据时低频等待；有数据最多 200ms 检查一次条件
            _wake.WaitOne(200);
            FlushDue(Force: false);
        }
    }

    /// <summary>写出满足条件的任务缓冲；Force=true 时全部写出。</summary>
    private void FlushDue(bool Force)
    {
        List<(BufferMeta meta, List<SampleRecord> records)>? ready = null;
        lock (_lock)
        {
            foreach (var buf in _buffers.Values)
            {
                if (buf.Pending.Count == 0) continue;
                bool duePoints = Force || buf.Pending.Count >= buf.Policy.BatchPoints;
                bool dueTime = Force
                    || (buf.Policy.IntervalSec > 0 && (DateTime.Now - buf.LastFlush).TotalSeconds >= buf.Policy.IntervalSec);
                if (!duePoints && !dueTime) continue;

                ready ??= new List<(BufferMeta, List<SampleRecord>)>();
                ready.Add((new BufferMeta(buf), buf.Pending.ToList()));
                buf.Pending.Clear();
                buf.LastFlush = DateTime.Now;
            }
        }

        if (ready is null) return;
        foreach (var (meta, records) in ready)
        {
            try { AppendCsv(meta, records); }
            catch { /* 单次写盘失败丢弃该批，避免写盘线程崩溃（后续批次仍会尝试） */ }
        }
    }

    /// <summary>立即写出全部剩余数据（任务停止/移除/程序退出时调用）。</summary>
    public void FlushAll()
    {
        FlushDue(Force: true);
    }

    /// <summary>查询任务落盘策略（查看配置用）；未注册返回 null。</summary>
    public SavePolicy? GetPolicy(Guid taskId)
    {
        lock (_lock)
            return _buffers.TryGetValue(taskId, out var buf) ? buf.Policy : null;
    }

    /// <summary>移除任务登记并写出其剩余数据。</summary>
    public void RemoveTask(Guid taskId)
    {
        (BufferMeta meta, List<SampleRecord> records)? pending = null;
        lock (_lock)
        {
            if (_buffers.Remove(taskId, out var buf))
            {
                if (buf.Pending.Count > 0)
                    pending = (new BufferMeta(buf), buf.Pending.ToList());
            }
        }
        if (pending is { } p)
        {
            try { AppendCsv(p.meta, p.records); } catch { /* 忽略 */ }
        }
    }

    /// <summary>任务缓冲快照（离开锁后写盘用）。</summary>
    private readonly record struct BufferMeta(string TaskName, string TargetLabel, int ValueCount, DateTime CreatedAt)
    {
        public BufferMeta(TaskBuffer b) : this(b.TaskName, b.TargetLabel, b.ValueCount, b.CreatedAt) { }
    }

    /// <summary>
    /// 追加写行式 CSV：监控对象,值1,值2,...,时间戳。
    /// 文件名 {任务名}_{创建时间:yyyyMMdd_HHmmss}.csv；按 ValueCount 条记录切一行；目录按当天滚动。
    /// </summary>
    private void AppendCsv(in BufferMeta meta, IReadOnlyList<SampleRecord> records)
    {
        var dayDir = Path.Combine(_dataRoot, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dayDir);
        var file = Path.Combine(dayDir, $"{Sanitize(meta.TaskName)}_{meta.CreatedAt:yyyyMMdd_HHmmss}.csv");

        var n = meta.ValueCount;
        var sb = new StringBuilder();
        bool exists = File.Exists(file);
        if (!exists)
        {
            var heads = new List<string> { "监控对象" };
            for (int i = 1; i <= n; i++) heads.Add($"值{i}");
            heads.Add("时间戳");
            sb.AppendLine(string.Join(",", heads));
        }

        // 每 n 条记录 = 一个采样点（值1..值N 共用同一时间戳），横排一行
        for (int i = 0; i + n <= records.Count; i += n)
        {
            var ts = records[i].Timestamp;
            var vals = records.Skip(i).Take(n).Select(r => CsvEscape(r.Value));
            sb.AppendLine($"{CsvEscape(meta.TargetLabel)},{string.Join(",", vals)},{ts:yyyy-MM-dd HH:mm:ss.fff}");
        }

        // UTF-8 with BOM：Excel/记事本可直接识别
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (exists) fs.Write(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        else fs.Write(bytes);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string CsvEscape(string? s)
    {
        if (s is null) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _writer?.Join(2000);
        FlushAll();
        _wake.Dispose();
    }
}
