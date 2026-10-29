// 监控任务运行时：单个任务的执行循环（触发→采集→过滤→记录→结束判断）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UiTreeMonitor.Core.Filters;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>任务运行状态。</summary>
public enum TaskRunState { Idle, Running, Paused, Completed, Stopped, Error }

/// <summary>一次采集产生的事件（供 UI/导出消费）。</summary>
public sealed record MonitorSample(
    DateTime Timestamp,
    int NodeId,
    string NodePath,
    IReadOnlyDictionary<string, string?> Values,   // 属性→值（过滤后）
    IReadOnlyList<SampleRecord> Records);           // 展开的记录行

/// <summary>监控任务运行时事件。</summary>
public sealed class MonitorTaskEventArgs : EventArgs
{
    public required MonitorSample Sample { get; init; }
}

/// <summary>监控任务运行时：在独立 STA 线程中执行。</summary>
public sealed class MonitorTask : IDisposable
{
    private readonly MonitorTaskConfig _config;
    private Thread _thread; // 非 readonly：Resume 需要重建线程
    private volatile bool _stopRequested;
    private TaskRunState _state = TaskRunState.Idle;

    private readonly object _lock = new();
    private readonly List<MonitorSample> _samples = new();
    private readonly List<SampleRecord> _records = new();

    public Guid Id => _config.Id;
    public string Name => _config.Name;
    public TaskRunState State { get { lock (_lock) return _state; } private set { lock (_lock) _state = value; } }
    public IReadOnlyList<MonitorSample> Samples { get { lock (_lock) return _samples.ToList(); } }
    public IReadOnlyList<SampleRecord> Records { get { lock (_lock) return _records.ToList(); } }
    public int SampleCount { get { lock (_lock) return _samples.Count; } }
    public DateTime StartedAt { get; private set; }

    public event EventHandler<MonitorTaskEventArgs>? SampleTaken;
    public event EventHandler<TaskRunState>? StateChanged;

    /// <summary>跨树值提供器（由调度器注入：查询其他任务最新值）。</summary>
    public Func<Guid, string, string?>? CrossTreeValueProvider { get; set; }

    public MonitorTask(MonitorTaskConfig config)
    {
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = $"MonitorTask-{config.Name}" };
        _thread.SetApartmentState(ApartmentState.STA); // UIA COM 要求
    }

    public void Start()
    {
        if (_state != TaskRunState.Idle) return;
        StartedAt = DateTime.Now;
        State = TaskRunState.Running;
        _thread.Start();
        RaiseState();
    }

    /// <summary>从 Stopped 状态恢复采样（创建新线程）。</summary>
    public void Resume()
    {
        if (_state != TaskRunState.Stopped) return;
        _stopRequested = false;
        State = TaskRunState.Running;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Monitor:{Name}" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        RaiseState();
    }

    public void RequestStop()
    {
        _stopRequested = true;
    }

    private void RaiseState()
    {
        var s = State;
        StateChanged?.Invoke(this, s);
    }

    private void Run()
    {
        try
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uitm_monitor.log");
            void MLog(string m) { try { System.IO.File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} [TASK-{Name}] {m}{System.Environment.NewLine}"); } catch { } };

            MLog("=== Run() 开始 ===");
            using var session = UiaSession.Create();
            var sampler = new TreeSampler();
            var trigger = TriggerRuntimeFactory.Create(_config.Trigger);
            var stopCond = StopConditionRuntimeFactory.Create(_config.StopCondition);
            var timeBox = new TimeBoxRuntime(_config.TimeBox);

            var lastValues = new Dictionary<string, string?>();
            string? prevPrimaryValue = null;
            int samplesTaken = 0;
            int loopCount = 0;
            var lastFire = DateTime.MinValue;

            var stopCtx = new StopContext { TaskStartedAt = StartedAt };
            MLog($"Target: Kind={_config.Target.Kind}, Value={_config.Target.Value}, ElemName={_config.Target.ElementName}, ElemType={_config.Target.ElementControlType}");
            MLog($"Properties: [{string.Join(",", _config.Properties)}], Interval={_config.Timer.IntervalMs}ms");

            while (!_stopRequested)
            {
                loopCount++; // 放在循环开头：continue 分支也要推进，否则节流日志（loopCount % N）恒命中刷屏
                var now = DateTime.Now;

                // 时间器推进（Loop 模式）
                timeBox.AdvanceLoop(now);
                if (timeBox.IsExpired(now))
                {
                    Complete();
                    return;
                }
                if (timeBox.IsInPause)
                {
                    Thread.Sleep(Math.Min(200, _config.Timer.IntervalMs));
                    continue;
                }

                // 目标解析（每次采集前重新解析，窗口关闭后自动重试）
                var target = TargetResolver.Resolve(_config.Target);
                if (target is null)
                {
                    if (loopCount % 10 == 0) MLog($"TargetResolver.Resolve 返回 null (loop={loopCount})");
                    Thread.Sleep(Math.Min(500, _config.Timer.IntervalMs));
                    continue;
                }

                // 触发判断
                var ctx = new TriggerContext
                {
                    Now = now,
                    TaskStartedAt = StartedAt,
                    LastFireAt = lastFire == DateTime.MinValue ? null : lastFire,
                    LastValues = lastValues,
                    CrossTreeValue = CrossTreeValueProvider,
                };
                if (!trigger.ShouldFire(ctx))
                {
                    if (loopCount % 50 == 0) MLog($"ShouldFire=false 未触发采集 (loop={loopCount}, trigger={_config.Trigger.Kind})");
                    Thread.Sleep(Math.Min(100, _config.Timer.IntervalMs));
                    continue;
                }
                lastFire = now;

                // 采集树
                var tree = sampler.CaptureWindow(session, target);
                if (tree.NodeCount == 0)
                {
                    if (loopCount % 10 == 0) MLog($"CaptureWindow 返回空树 (loop={loopCount})");
                    Thread.Sleep(Math.Min(200, _config.Timer.IntervalMs));
                    continue;
                }
                if (loopCount % 10 == 0) MLog($"CaptureWindow 成功: {tree.NodeCount}个节点");

                // 过滤：定位监控节点（属性级）
                var (nodeId, nodePath, values) = SelectMonitoredValues(tree, _config);
                if (nodeId < 0)
                {
                    if (loopCount % 5 == 0)
                    {
                        MLog($"SelectMonitoredValues 未匹配节点! ElemName=[{_config.Target.ElementName}] ElemType=[{_config.Target.ElementControlType}]");
                        // 打印前3个节点供诊断
                        foreach (var id in tree.Order.Take(3))
                        {
                            var n = tree.Nodes[id]; MLog($"  节点[{id}]: Name=[{n.Name}] Type=[{n.ControlType}] AutoId=[{n.AutomationId}]");
                        }
                    }
                    Thread.Sleep(Math.Min(200, _config.Timer.IntervalMs));
                    continue;
                }

                // 更新触发上下文（值变化/阈值用）
                foreach (var (k, v) in values)
                    lastValues[k] = v;

                // 记录
                var records = values.Select(kv => new SampleRecord(now, nodeId, nodePath, kv.Key, kv.Value ?? "")).ToList();
                var sample = new MonitorSample(now, nodeId, nodePath, values, records);
                lock (_lock)
                {
                    _samples.Add(sample);
                    _records.AddRange(records);
                    samplesTaken = _samples.Count;
                }
                SampleTaken?.Invoke(this, new MonitorTaskEventArgs { Sample = sample });
                if (samplesTaken % 10 == 0) MLog($"采样成功! 总计={samplesTaken}, 值=[{string.Join(",", values.Values)}]");

                // 结束条件判断
                stopCtx.Now = now;
                stopCtx.SampleCount = samplesTaken;
                stopCtx.LastValues = lastValues;
                stopCtx.PreviousValue = prevPrimaryValue;
                prevPrimaryValue = values.Values.FirstOrDefault();
                if (stopCond.ShouldStop(stopCtx))
                {
                    Complete();
                    return;
                }

                // 等待下一个采集周期
                Thread.Sleep(Math.Max(50, _config.Timer.IntervalMs));
            }
            State = TaskRunState.Stopped;
            RaiseState();
        }
        catch (Exception ex)
        {
            State = TaskRunState.Error;
            RaiseState();
            System.Diagnostics.Debug.WriteLine($"[MonitorTask:{Name}] {ex}");
        }
    }

    private void Complete()
    {
        State = TaskRunState.Completed;
        RaiseState();
    }

    /// <summary>在树中按目标约束定位监控节点，返回 (nodeId, path, 属性值表)。</summary>
    private static (int nodeId, string path, Dictionary<string, string?> values) SelectMonitoredValues(
        TreeSnapshot tree, MonitorTaskConfig config)
    {
        var t = config.Target;
        ElementNode? node = null;

        foreach (var id in tree.Order)
        {
            var n = tree.Nodes[id];
            if (t.ElementName is not null && n.Name != t.ElementName) continue;
            if (t.ElementAutomationId is not null && n.AutomationId != t.ElementAutomationId) continue;
            if (t.ElementControlType is not null && n.ControlType != t.ElementControlType) continue;
            node = n;
            break; // 取第一个匹配
        }

        if (node is null) return (-1, "", new Dictionary<string, string?>());

        // 属性采集 + 提取性过滤
        var values = new Dictionary<string, string?>();
        foreach (var p in config.Properties)
            values[p] = node.GetProperty(p);

        foreach (var (k, v) in config.Filter.ExtractValues(node))
            values[k] = v;

        // 路径
        var path = BuildPath(tree, node.Id);
        return (node.Id, path, values);
    }

    private static string BuildPath(TreeSnapshot tree, int id)
    {
        var parts = new List<string>();
        int cur = id;
        while (cur != -1 && tree.Nodes.TryGetValue(cur, out var n))
        {
            parts.Add($"{n.ControlType}[{n.Name ?? n.AutomationId ?? n.Id.ToString()}]");
            cur = n.ParentId;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    public void Dispose()
    {
        RequestStop();
        if (_thread.IsAlive) _thread.Join(1000);
        GC.SuppressFinalize(this);
    }
}
