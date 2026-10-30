// 任务调度器：注册/启动/停止多个监控任务，维护跨树引用
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Hosting;

namespace UiTreeMonitor.Scheduler;

/// <summary>任务调度器：管理一组监控宿主。</summary>
public sealed class TaskScheduler : IDisposable
{
    private readonly ConcurrentDictionary<Guid, IMonitorHost> _hosts = new();
    private readonly ConcurrentDictionary<Guid, SyncGroupCoordinator> _syncGroups = new();
    private readonly object _latestLock = new();
    private readonly Dictionary<Guid, Dictionary<string, string?>> _latestValues = new();
    private readonly string? _workerExe; // 非空则进程模式可用

    public TaskScheduler(string? workerExe = null)
    {
        _workerExe = workerExe;
    }

    /// <summary>注册任务。hostMode: thread=默认线程；process=进程隔离（需 workerExe）。start=false 时仅创建不启动（后续用 Start 启动）。</summary>
    public Guid Register(MonitorTaskConfig config, string hostMode = "thread", bool start = true)
    {
        IMonitorHost host = hostMode switch
        {
            "process" when _workerExe is not null => new ProcessHost(config, _workerExe),
            _ => new ThreadHost(config),
        };

        host.SampleTaken += (_, e) =>
        {
            lock (_latestLock)
            {
                _latestValues[host.Id] = new Dictionary<string, string?>(e.Sample.Values);
            }
        };
        host.SetCrossTreeProvider(GetLatestValue);

        if (!_hosts.TryAdd(config.Id, host))
            throw new InvalidOperationException($"任务 {config.Name} 已存在");

        if (start) host.Start();
        return config.Id;
    }

    /// <summary>启动已注册但未启动的任务（Idle → Running）。</summary>
    public void Start(Guid id)
    {
        if (_hosts.TryGetValue(id, out var h)) h.Start();
    }

    /// <summary>同步启动多个任务（同步采集组）：统一节拍、时间戳严格同刻、同窗口共享一次抓树。
    /// intervalMs/durationSec 为组统一参数（durationSec=0 无限）；要求 ≥2 个线程模式任务且均不在运行。
    /// 返回组 id；不满足条件返回 null。</summary>
    public Guid? StartSync(IReadOnlyList<Guid> taskIds, int? intervalMs = null, int durationSec = 0)
    {
        if (taskIds is null || taskIds.Count < 2) return null;
        var hosts = taskIds.Select(GetHost).OfType<ThreadHost>().ToList();
        if (hosts.Count != taskIds.Count) return null; // 同步组仅支持线程模式
        if (hosts.Any(h => h.State is TaskRunState.Running or TaskRunState.Paused)) return null;

        var tasks = hosts.Select(h => h.Task).ToList();
        var interval = intervalMs ?? tasks.Max(t => t.IntervalMs);
        foreach (var t in tasks)
        {
            t.ExternallyDriven = true;
            t.StartExternal();
        }
        var coord = new SyncGroupCoordinator(tasks, interval, durationSec);
        _syncGroups[coord.GroupId] = coord;
        coord.Start();
        return coord.GroupId;
    }

    /// <summary>查询任务所属的同步组（未成组返回 null）。</summary>
    public SyncGroupCoordinator? GetSyncGroupOf(Guid taskId)
    {
        foreach (var g in _syncGroups.Values)
            if (g.Tasks.Any(t => t.Id == taskId))
                return g;
        return null;
    }

    /// <summary>查询其他任务最新值（跨树触发器用）。</summary>
    public string? GetLatestValue(Guid taskId, string property)
    {
        lock (_latestLock)
            return _latestValues.TryGetValue(taskId, out var d) && d.TryGetValue(property, out var v) ? v : null;
    }

    public IReadOnlyList<IMonitorHost> Hosts => _hosts.Values.ToList();

    public IMonitorHost? GetHost(Guid id) => _hosts.TryGetValue(id, out var h) ? h : null;

    public void RequestStop(Guid id)
    {
        if (_hosts.TryGetValue(id, out var h)) h.RequestStop();
    }

    public void Resume(Guid id)
    {
        if (_hosts.TryGetValue(id, out var h)) h.Resume();
    }

    public void RequestStopAll()
    {
        foreach (var h in _hosts.Values) h.RequestStop();
    }

    public void Remove(Guid id)
    {
        if (_hosts.TryRemove(id, out var h))
        {
            h.RequestStop();
            h.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var g in _syncGroups.Values) g.Dispose();
        _syncGroups.Clear();
        RequestStopAll();
        foreach (var h in _hosts.Values) h.Dispose();
        _hosts.Clear();
        GC.SuppressFinalize(this);
    }
}
