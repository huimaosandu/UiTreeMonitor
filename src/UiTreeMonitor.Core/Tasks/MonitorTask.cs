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
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", DateTime.Now.ToString("yyyyMMdd") + ".log");
            void MLog(string m) { try { var dir = System.IO.Path.GetDirectoryName(logPath); if (dir is not null) System.IO.Directory.CreateDirectory(dir); System.IO.File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} [TASK-{Name}] {m}{System.Environment.NewLine}"); } catch { } };

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
            bool degradedLogged = false; // 定位降级只提示一次，避免刷屏

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

                // 过滤：定位监控节点（属性级；降级匹配保证被监控值变化后仍能定位）
                var (nodeId, nodePath, values, matchLevel) = SelectMonitoredValues(tree, _config);
                if (nodeId < 0)
                {
                    if (loopCount % 5 == 0)
                    {
                        MLog($"SelectMonitoredValues 未匹配节点! ElemName=[{_config.Target.ElementName}] ElemType=[{_config.Target.ElementControlType}]");
                        // 打印同 ControlType 的节点供诊断（比固定前3个根节点更有用）
                        foreach (var n in tree.Order.Select(id => tree.Nodes[id])
                                     .Where(n => _config.Target.ElementControlType is null || n.ControlType == _config.Target.ElementControlType)
                                     .Take(3))
                        {
                            MLog($"  节点[{n.Id}]: Name=[{n.Name}] Type=[{n.ControlType}] AutoId=[{n.AutomationId}]");
                        }
                    }
                    Thread.Sleep(Math.Min(200, _config.Timer.IntervalMs));
                    continue;
                }
                if (matchLevel > 0 && !degradedLogged)
                {
                    degradedLogged = true;
                    MLog($"定位降级(级别{matchLevel}): 精确匹配未命中（被监控值变化导致 Name 快照过期），" +
                         (matchLevel == 1 ? "已按 AutomationId+ControlType 继续定位" : "已按 ControlType 兜底定位") +
                         "——值变化监控不受影响");
                }

                // 更新触发上下文（值变化/阈值用）
                foreach (var (k, v) in values)
                    lastValues[k] = v;

                // 记录：应用值提取规则（@@标记对 / re:正则），有规则时展开为 值1..值N；
                // lastValues（触发器上下文）保持原始值不受提取影响
                var records = new List<SampleRecord>();
                if (_config.Extractors is { Count: > 0 })
                {
                    var raw = values.Values.FirstOrDefault();
                    var extracted = ValueExtractor.ExtractAll(raw, _config.Extractors);
                    for (int i = 0; i < extracted.Count; i++)
                        records.Add(new SampleRecord(now, nodeId, nodePath, $"值{i + 1}", extracted[i]));
                }
                else
                {
                    foreach (var (k, v) in values)
                        records.Add(new SampleRecord(now, nodeId, nodePath, k, v ?? ""));
                }
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

                // 等待下一个采集周期（分片睡眠：移除/停止请求时立即中断，确保采集线程及时退出）
                InterruptibleSleep(Math.Max(50, _config.Timer.IntervalMs));
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

    /// <summary>在树中按目标约束定位监控节点（通用降级匹配），返回 (nodeId, path, 属性值表, 匹配级别)。</summary>
    private static (int nodeId, string path, Dictionary<string, string?> values, int matchLevel) SelectMonitoredValues(
        TreeSnapshot tree, MonitorTaskConfig config)
    {
        var t = config.Target;
        // 按树的前序顺序遍历，保持"取第一个匹配"的稳定语义
        var nodes = tree.Order.Select(id => tree.Nodes[id]);
        var (node, level) = ElementMatcher.FindNode(nodes, t.ElementName, t.ElementAutomationId, t.ElementControlType);

        if (node is null) return (-1, "", new Dictionary<string, string?>(), -1);

        // 属性采集 + 提取性过滤
        var values = new Dictionary<string, string?>();
        foreach (var p in config.Properties)
            values[p] = node.GetProperty(p);

        foreach (var (k, v) in config.Filter.ExtractValues(node))
            values[k] = v;

        // 路径
        var path = BuildPath(tree, node.Id);
        return (node.Id, path, values, level);
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

    /// <summary>分片睡眠：50ms 粒度检查停止请求，请求后立即返回（替代整段 Thread.Sleep）。</summary>
    private void InterruptibleSleep(int totalMs)
    {
        var until = Environment.TickCount64 + totalMs;
        while (!_stopRequested && Environment.TickCount64 < until)
            Thread.Sleep(50);
    }

    public void Dispose()
    {
        RequestStop();
        // 等待采集线程真正退出（抓树中的 UIA 调用可能阻塞数秒），确保移除任务后不再有采集活动
        if (_thread.IsAlive) _thread.Join(3000);
        GC.SuppressFinalize(this);
    }
}
