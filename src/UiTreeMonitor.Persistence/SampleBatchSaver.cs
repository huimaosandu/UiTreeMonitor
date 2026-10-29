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

    /// <summary>注册/更新任务落盘策略（创建任务时调用）。</summary>
    public void SetPolicy(Guid taskId, string taskName, SavePolicy policy)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(taskId, out var buf))
            {
                _buffers[taskId] = new TaskBuffer { TaskName = taskName, Policy = policy };
            }
            else
            {
                buf.TaskName = taskName;
                buf.Policy = policy;
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
        List<(string name, List<SampleRecord> records)>? ready = null;
        lock (_lock)
        {
            foreach (var buf in _buffers.Values)
            {
                if (buf.Pending.Count == 0) continue;
                bool duePoints = Force || buf.Pending.Count >= buf.Policy.BatchPoints;
                bool dueTime = Force
                    || (buf.Policy.IntervalSec > 0 && (DateTime.Now - buf.LastFlush).TotalSeconds >= buf.Policy.IntervalSec);
                if (!duePoints && !dueTime) continue;

                ready ??= new List<(string, List<SampleRecord>)>();
                ready.Add((buf.TaskName, buf.Pending.ToList()));
                buf.Pending.Clear();
                buf.LastFlush = DateTime.Now;
            }
        }

        if (ready is null) return;
        foreach (var (name, records) in ready)
        {
            try { AppendCsv(name, records); }
            catch { /* 单次写盘失败丢弃该批，避免写盘线程崩溃（后续批次仍会尝试） */ }
        }
    }

    /// <summary>立即写出全部剩余数据（任务停止/移除/程序退出时调用）。</summary>
    public void FlushAll()
    {
        FlushDue(Force: true);
    }

    /// <summary>移除任务登记并写出其剩余数据。</summary>
    public void RemoveTask(Guid taskId)
    {
        (string name, List<SampleRecord> records)? pending = null;
        lock (_lock)
        {
            if (_buffers.Remove(taskId, out var buf))
            {
                if (buf.Pending.Count > 0)
                    pending = (buf.TaskName, buf.Pending.ToList());
            }
        }
        if (pending is { } p)
        {
            try { AppendCsv(p.name, p.records); } catch { /* 忽略 */ }
        }
    }

    /// <summary>追加写 CSV 到当日目录（按当前日期滚动子目录）。</summary>
    private void AppendCsv(string taskName, IReadOnlyList<SampleRecord> records)
    {
        var dayDir = Path.Combine(_dataRoot, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dayDir);
        var file = Path.Combine(dayDir, $"{Sanitize(taskName)}_{DateTime.Now:yyyyMMdd}.csv");

        var sb = new StringBuilder();
        bool exists = File.Exists(file);
        if (!exists) sb.AppendLine("时间戳,属性,值");
        foreach (var r in records)
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{CsvEscape(r.Property)},{CsvEscape(r.Value)}");

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
