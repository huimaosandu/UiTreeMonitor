// 同步采集组协调器：一个节拍线程驱动多个任务，实现跨任务"严格同刻"采样。
// 设计要点：
//  1) 时间戳 = 理论 tick 时刻（起点 + k*间隔），与本任务实际处理完成时刻无关——
//     组内所有任务同一轮采样的 Timestamp 完全相等，导出合并图时时间轴严格对齐。
//  2) 同窗口（同句柄）的任务共享同一次 CaptureWindow 结果——N 个任务只抓 1 次树，
//     既省开销又保证同窗口多元素的值来自同一份快照（天然同一时刻）。
//  3) 不同窗口并行抓树（每窗口一个 STA 线程，UIA COM 要求），互不阻塞。
//  4) 落后超过一个间隔时跳到未来最近的 tick（丢点保对齐），不追赶补采。
//  5) 各任务保持独立：单独 RequestStop 的任务被剔除出组，其余任务继续。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UiTreeMonitor.Core;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.Scheduler;

/// <summary>同步采集组：统一节拍、同窗口共享抓树、时间戳严格同刻。</summary>
public sealed class SyncGroupCoordinator : IDisposable
{
    private readonly List<MonitorTask> _tasks;
    private readonly int _intervalMs;
    private readonly int _durationSec; // 组统一监控时长（0=无限）：到期全组同时完成
    private readonly Action<string>? _log;
    private readonly Dictionary<long, WindowCaptureWorker> _workers = new(); // 窗口句柄 → 常驻抓取线程（复用 STA+UiaSession）
    private Thread? _thread;
    private volatile bool _stopRequested;

    public Guid GroupId { get; }
    public bool IsAlive => _thread?.IsAlive == true;
    public IReadOnlyList<MonitorTask> Tasks => _tasks;

    public SyncGroupCoordinator(IEnumerable<MonitorTask> tasks, int intervalMs, int durationSec = 0,
        Action<string>? log = null)
    {
        _tasks = tasks.ToList();
        _intervalMs = Math.Max(50, intervalMs);
        _durationSec = Math.Max(0, durationSec);
        _log = log;
        GroupId = Guid.NewGuid();
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stopRequested = false;
        _thread = new Thread(Run) { IsBackground = true, Name = $"SyncGroup-{GroupId:N}" };
        _thread.SetApartmentState(ApartmentState.STA); // 目标解析/兜底操作需 STA
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        _thread?.Join(5000);
        _thread = null;
    }

    private void Run()
    {
        Log($"=== 同步组启动: {_tasks.Count} 个任务 [{string.Join(", ", _tasks.Select(t => t.Name))}], 间隔 {_intervalMs}ms ===");
        var startAt = DateTime.Now;
        long k = 0;
        long ticks = 0, lagSkipped = 0;
        long lastCaptureMs = 0; // 上一轮抓树耗时（落后日志诊断用）
        var lastLagLogAt = DateTime.MinValue;

        while (!_stopRequested)
        {
            k++;
            var tickAt = startAt.AddMilliseconds(k * (double)_intervalMs); // 理论 tick 时刻（全组统一时间戳）
            var waitMs = (long)(tickAt - DateTime.Now).TotalMilliseconds;

            if (waitMs > 0)
            {
                // 分片睡眠到 tick：停止请求立即响应
                var until = Environment.TickCount64 + waitMs;
                while (!_stopRequested && Environment.TickCount64 < until)
                    Thread.Sleep((int)Math.Min(50, Math.Max(1, waitMs)));
            }
            else if (waitMs < -_intervalMs)
            {
                // 落后超过一个间隔（窗口抓树超时等）：跳到未来最近的 tick——丢点保序列严格对齐
                lagSkipped++;
                if ((DateTime.Now - lastLagLogAt).TotalSeconds > 30)
                {
                    lastLagLogAt = DateTime.Now;
                    Log($"节拍落后 {waitMs}ms，跳到未来最近 tick 保持对齐。上一轮抓树 {lastCaptureMs}ms × {_workers.Count} 个窗口" +
                        (lastCaptureMs >= _intervalMs
                            ? $"——抓树耗时已超过间隔 {_intervalMs}ms（UIA 跨进程逐节点取值的物理下限），继续采集会持续跳拍，建议把组间隔调大到 ≥ {Math.Max(1000, lastCaptureMs + 200)}ms"
                            : "（抓树耗时超过间隔？）"));
                }
                var now = DateTime.Now;
                k += (long)((now - tickAt).TotalMilliseconds / _intervalMs);
                continue;
            }

            if (_stopRequested) break;

            // 组统一监控时长：到期全组同时完成（各任务状态一致，时间轴严格同刻到最后一轮）
            if (_durationSec > 0 && (DateTime.Now - startAt).TotalSeconds >= _durationSec)
            {
                Log($"监控时长 {_durationSec}s 到期，全组完成");
                foreach (var t in _tasks)
                    t.MarkCompleted();
                break;
            }

            // 剔除已停止任务（单独停止的任务退出组，其余继续）；
            // ExternallyDriven=false 的任务已被单独"启动/继续"为独立模式，不再由本组驱动
            var active = new List<MonitorTask>();
            foreach (var t in _tasks)
            {
                if (t.IsStopRequested) { t.MarkStopped(); continue; }
                if (t.State == TaskRunState.Running && t.ExternallyDriven) active.Add(t);
            }
            if (active.Count == 0) break;

            // 按目标窗口分组（每 tick 重新解析：窗口可能关闭/重开）；解析失败的任务本轮跳过
            var groups = new Dictionary<long, (TargetWindowInfo Info, List<MonitorTask> Tasks)>();
            foreach (var t in active)
            {
                var info = TargetResolver.Resolve(t.Target);
                if (info is null) continue;
                var key = info.Handle.ToInt64();
                if (!groups.TryGetValue(key, out var g))
                {
                    g = (info, new List<MonitorTask>());
                    groups[key] = g;
                }
                g.Tasks.Add(t);
            }
            if (groups.Count == 0) continue;

            // 每窗口一个常驻 STA 抓取线程并行抓树（同窗口任务共享结果）：
            // 线程 + UiaSession 跨轮复用（此前每轮新建线程/COM 初始化有固定开销；
            // 实测单窗口 23 节点抓树本身需数百 ms——间隔低于抓树耗时是跳拍的根因，见落后日志）
            var snapshots = new ConcurrentDictionary<long, TreeSnapshot>();
            var pending = new List<(long Key, Task<TreeSnapshot?> Task)>();
            var captureSw = Stopwatch.StartNew();
            foreach (var (key, group) in groups)
            {
                if (!_workers.TryGetValue(key, out var worker))
                {
                    worker = new WindowCaptureWorker(key);
                    _workers[key] = worker;
                }
                pending.Add((key, worker.CaptureAsync(group.Info)));
            }
            foreach (var (key, task) in pending)
            {
                // 抓树保护超时：超时窗口本轮放弃（worker 继续抓，完成后自动处理下轮请求）
                if (task.Wait(10000) && task.Result is { } snap && snap.NodeCount > 0)
                    snapshots[key] = snap;
            }
            var captureMs = captureSw.ElapsedMilliseconds;
            lastCaptureMs = captureMs;

            // 推送：时间戳统一为 tickAt（严格同刻）；串行推送避免事件线程风暴
            foreach (var (key, group) in groups)
            {
                if (!snapshots.TryGetValue(key, out var snap) || snap.NodeCount == 0) continue;
                foreach (var t in group.Tasks)
                    t.RecordExternalSample(tickAt, snap);
            }
            ticks++;
        }

        // 组结束兜底：仍处于 Running 的任务置 Stopped（正常路径是全部任务单独停止/完成）
        foreach (var t in _tasks)
        {
            if (t.IsStopRequested || t.State == TaskRunState.Running) t.MarkStopped();
        }
        foreach (var w in _workers.Values) w.Dispose();
        _workers.Clear();
        Log($"=== 同步组结束: 共 {ticks} 轮, 跳拍 {lagSkipped} 次 ===");
    }

    /// <summary>常驻窗口抓取线程（STA）：复用 UiaSession 跨轮抓树，请求经阻塞队列串行处理。
    /// 此前每轮新建线程 + COM 初始化有固定开销；且窗口抓取彼此隔离，多窗口互不阻塞。</summary>
    private sealed class WindowCaptureWorker : IDisposable
    {
        private readonly BlockingCollection<(TargetWindowInfo Info, TaskCompletionSource<TreeSnapshot?> Tcs)> _queue = new();
        private readonly Thread _thread;

        public WindowCaptureWorker(long windowKey)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var session = UiaSession.Create(); // 一个会话服务该窗口的全部轮次
                    var sampler = new TreeSampler();
                    foreach (var (info, tcs) in _queue.GetConsumingEnumerable())
                    {
                        try { tcs.SetResult(sampler.CaptureWindow(session, info)); }
                        catch { tcs.SetResult(null); } // 单窗口失败：本轮放弃，不影响其他窗口
                    }
                }
                catch { /* 会话初始化失败：后续请求全部 null */ }
            })
            { IsBackground = true, Name = $"SyncCapture-{windowKey}" };
            _thread.SetApartmentState(ApartmentState.STA); // UIA COM 要求
            _thread.Start();
        }

        /// <summary>提交一次抓树请求（异步；同 worker 串行执行，超时轮的请求完成后自动排下一个）。</summary>
        public Task<TreeSnapshot?> CaptureAsync(TargetWindowInfo info)
        {
            var tcs = new TaskCompletionSource<TreeSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((info, tcs));
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(2000);
        }
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
        AppLog.Write($"SYNC-{GroupId.ToString("N")[..6]}", message);
    }

    public void Dispose() => Stop();
}
