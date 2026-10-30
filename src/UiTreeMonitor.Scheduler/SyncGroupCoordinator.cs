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
using System.Linq;
using System.Threading;
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
                    Log($"节拍落后 {waitMs}ms（抓树耗时超过间隔？），跳过 {lagSkipped} 个 tick 保持对齐");
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

            // 每窗口一个 STA 抓取线程并行抓树（同窗口任务共享结果）
            var snapshots = new ConcurrentDictionary<long, TreeSnapshot>();
            var workers = new List<Thread>();
            foreach (var (key, group) in groups)
            {
                var info = group.Info;
                var th = new Thread(() =>
                {
                    try
                    {
                        using var session = UiaSession.Create();
                        var sampler = new TreeSampler();
                        snapshots[key] = sampler.CaptureWindow(session, info);
                    }
                    catch
                    {
                        // 单窗口抓取失败：该窗口本轮不推送
                    }
                })
                { IsBackground = true, Name = $"SyncCapture-{info.ProcessId}" };
                th.SetApartmentState(ApartmentState.STA); // UIA COM 要求
                workers.Add(th);
                th.Start();
            }
            foreach (var th in workers)
                th.Join(10000); // 抓树保护超时：超时窗口本轮放弃

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
        Log($"=== 同步组结束: 共 {ticks} 轮, 跳拍 {lagSkipped} 次 ===");
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
        AppLog.Write($"SYNC-{GroupId.ToString("N")[..6]}", message);
    }

    public void Dispose() => Stop();
}
