// 进程宿主：用独立进程隔离执行监控任务（可选模式，用于硬隔离）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Hosting;

/// <summary>
/// 进程宿主：把监控任务放到独立 worker 进程中执行，与主进程硬隔离。
/// worker 协议：`UiTreeMonitor.App --worker --config <json路径> --out <输出目录>`
/// worker 负责采集并落盘，宿主负责启动/监控进程存活。
/// </summary>
public sealed class ProcessHost : IMonitorHost
{
    private readonly MonitorTaskConfig _config;
    private readonly string _workerExe;
    private Process? _process;
    private volatile bool _stopping;

    public Guid Id => _config.Id;
    public string Name => _config.Name;
    public TaskRunState State { get; private set; } = TaskRunState.Idle;

    public event EventHandler<MonitorTaskEventArgs>? SampleTaken;
    public event EventHandler<TaskRunState>? StateChanged;

    public ProcessHost(MonitorTaskConfig config, string workerExe)
    {
        _config = config;
        _workerExe = workerExe;
    }

    public void Start()
    {
        if (_process is { HasExited: false }) return;

        // worker 配置文件：任务配置 + 输出目录
        var tempCfg = Path.Combine(Path.GetTempPath(), $"uitm_{Id:N}.json");
        File.WriteAllText(tempCfg, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));

        var psi = new ProcessStartInfo
        {
            FileName = _workerExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"--worker --config \"{tempCfg}\" --out \"{_config.OutputDirectory}\"",
        };
        State = TaskRunState.Running;
        _process = Process.Start(psi);
        _stopping = false;
        StateChanged?.Invoke(this, State);

        // 监视进程退出
        var monitor = new Thread(() =>
        {
            _process?.WaitForExit();
            if (!_stopping)
            {
                State = TaskRunState.Completed;
                StateChanged?.Invoke(this, State);
            }
        }) { IsBackground = true };
        monitor.Start();
    }

    public void RequestStop()
    {
        _stopping = true;
        try { _process?.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        State = TaskRunState.Stopped;
        StateChanged?.Invoke(this, State);
    }

    /// <summary>重新启动 worker 进程恢复采样（进程模式）。</summary>
    public void Resume()
    {
        if (_process is not null && !_process.HasExited) return; // 还在跑
        Start(); // 重新启动进程
    }

    public void SetCrossTreeProvider(Func<Guid, string, string?> provider)
    {
        // 进程模式下跨树联动依赖 worker 内部实现，此处暂不支持
    }

    public IReadOnlyList<SampleRecord> Records => Array.Empty<SampleRecord>();

    public void Dispose()
    {
        RequestStop();
        _process?.Dispose();
        GC.SuppressFinalize(this);
    }
}
