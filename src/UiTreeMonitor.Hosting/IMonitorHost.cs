// 宿主抽象：监控任务的执行载体（线程 or 进程）
using System;
using System.Collections.Generic;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Hosting;

/// <summary>监控宿主接口：启动/停止/状态查询。</summary>
public interface IMonitorHost : IDisposable
{
    Guid Id { get; }
    string Name { get; }
    TaskRunState State { get; }
    event EventHandler<MonitorTaskEventArgs>? SampleTaken;
    event EventHandler<TaskRunState>? StateChanged;
    void Start();
    void RequestStop();
    void Resume(); // 从 Stopped 恢复采样

    /// <summary>注入跨树值提供器（跨树触发器需要）。</summary>
    void SetCrossTreeProvider(Func<Guid, string, string?> provider);

    /// <summary>已采集的记录（导出/历史用）。</summary>
    IReadOnlyList<SampleRecord> Records { get; }
}

/// <summary>线程宿主：每个任务一个独立 STA 线程（默认模式）。</summary>
public sealed class ThreadHost : IMonitorHost
{
    private readonly MonitorTask _task;

    public Guid Id => _task.Id;
    public string Name => _task.Name;
    public TaskRunState State => _task.State;

    public event EventHandler<MonitorTaskEventArgs>? SampleTaken;
    public event EventHandler<TaskRunState>? StateChanged;

    public ThreadHost(MonitorTaskConfig config)
    {
        _task = new MonitorTask(config);
        _task.SampleTaken += (_, e) => SampleTaken?.Invoke(this, e);
        _task.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
    }

    /// <summary>内部任务运行时（同步采集组需要直接驱动外部模式采样）。</summary>
    public MonitorTask Task => _task;

    /// <summary>注入跨树值提供器。</summary>
    public void SetCrossTreeProvider(Func<Guid, string, string?> provider)
    {
        _task.CrossTreeValueProvider = provider;
    }

    public void Start() => _task.Start();
    public void RequestStop() => _task.RequestStop();
    public void Resume() => _task.Resume();
    public IReadOnlyList<SampleRecord> Records => _task.Records;
    public void Dispose() => _task.Dispose();
}
