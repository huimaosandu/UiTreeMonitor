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
    public MonitorTarget Target => _config.Target; // 同步组协调器按目标窗口分组用
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
        if (ExternallyDriven) { StartExternal(); return; } // 同步组任务：不起采集线程
        if (_state != TaskRunState.Idle) return;
        StartedAt = DateTime.Now;
        State = TaskRunState.Running;
        _thread.Start();
        RaiseState();
    }

    /// <summary>从 Stopped 状态恢复采样（创建新线程）。外部驱动模式不适用（同步组需重新成组启动）。</summary>
    public void Resume()
    {
        if (ExternallyDriven) return;
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
            MLog($"Target: Kind={_config.Target.Kind}, Value={_config.Target.Value}, ElemPath={_config.Target.ElementPath ?? "(无)"}, ElemName={_config.Target.ElementName}, ElemAutoId={_config.Target.ElementAutomationId ?? "(无)"}, ElemType={_config.Target.ElementControlType}");
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

                // 定位监控节点：结构路径优先（唯一锁定，Name 变化不影响）+ 精确属性兜底；
                // 不做放宽兜底（取错节点比漏采更糟），未命中跳过本轮并留诊断日志
                var (nodeId, nodePath, values, matchLevel) = SelectMonitoredValues(tree, _config);
                if (nodeId < 0)
                {
                    if (loopCount % 5 == 0)
                    {
                        MLog($"SelectMonitoredValues 未匹配节点! ElemPath=[{_config.Target.ElementPath ?? "(无)"}] ElemName=[{_config.Target.ElementName}] ElemType=[{_config.Target.ElementControlType}]" +
                             (string.IsNullOrEmpty(_config.Target.ElementPath) && _config.Target.ElementName is not null
                                 ? "（任务无结构路径且 Name 已变化：重建任务可自动记录路径）" : ""));
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
                // 定位诊断（首次成功 + 首次降级各记一次）：打印 AutomationId 与候选重复计数
                LogMatchDiagnostics(tree, nodeId, matchLevel);

                // 更新触发上下文（值变化/阈值用）
                foreach (var (k, v) in values)
                    lastValues[k] = v;

                // 记录：应用值提取规则（@@标记对 / re:正则），有规则时展开为 值1..值N；
                // lastValues（触发器上下文）保持原始值不受提取影响
                var records = BuildRecords(now, nodeId, nodePath, values, _config);
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

    // ==== 日志与定位诊断 ====
    // 独立/同步两种模式共用（同步模式无 Run 循环，诊断同样必须落到日志）。

    private bool _firstMatchLogged;  // 首次定位成功诊断（每任务一次）
    private bool _degradedLogged;    // 首次定位降级诊断（每任务一次）

    /// <summary>任务日志（AppLog：源码运行写工程根\logs，发布版写 exe\logs；失败不影响采集）。</summary>
    private void MLog(string m) => AppLog.Write($"TASK-{Name}", m);

    /// <summary>定位诊断（首次定位 / 首次路径失效各记一次，不刷屏）：打印期望约束与实际匹配节点完整对照（含 AutomationId）。
    /// 级别语义：0=结构路径（唯一锁定，Name 变化不影响）/ 1=精确属性（Name+AutoId+Type 全条件）。
    /// 两级均未命中的场景由调用方的"未匹配"诊断覆盖（打印 ElemPath/ElemName/同 Type 候选节点）。</summary>
    private void LogMatchDiagnostics(TreeSnapshot tree, int nodeId, int matchLevel)
    {
        var t = _config.Target;
        if (!tree.Nodes.TryGetValue(nodeId, out var mn)) return;

        if (matchLevel == 0 && !_firstMatchLogged)
        {
            _firstMatchLogged = true;
            MLog($"首次定位: 级别0(结构路径 {t.ElementPath}) 唯一锁定 节点[{nodeId}] Name=[{mn.Name}] AutoId=[{mn.AutomationId}] Type=[{mn.ControlType}] Depth={mn.Depth}");
        }
        else if (matchLevel == 1)
        {
            if (!_firstMatchLogged)
            {
                _firstMatchLogged = true;
                MLog($"首次定位: 级别1(属性精确 Name+AutoId+Type) 节点[{nodeId}] Name=[{mn.Name}] AutoId=[{mn.AutomationId}] Type=[{mn.ControlType}] Depth={mn.Depth}" +
                     $"{(string.IsNullOrEmpty(t.ElementPath) ? "（任务无结构路径，Name 变化后将无法定位——重建任务可自动记录路径）" : "")}");
            }
            else if (!_degradedLogged && !string.IsNullOrEmpty(t.ElementPath))
            {
                // 路径曾命中过，本轮结构变化失效 → 降级精确属性仍能定位（Name 未变时）
                _degradedLogged = true;
                MLog($"⚠ 结构路径 [{t.ElementPath}] 失效（UI 结构变化：增删/重排兄弟节点），降级为属性精确匹配 节点[{nodeId}] Name=[{mn.Name}] AutoId=[{mn.AutomationId}] Type=[{mn.ControlType}] | " +
                     $"期望: ElemName=[{t.ElementName}] ElemAutoId=[{t.ElementAutomationId ?? "(无)"}] ElemType=[{t.ElementControlType}]");
            }
        }
    }

    /// <summary>在树中定位监控节点（结构路径优先 + 精确属性兜底，不做放宽），返回 (nodeId, path, 属性值表, 匹配级别)。</summary>
    private static (int nodeId, string path, Dictionary<string, string?> values, int matchLevel) SelectMonitoredValues(
        TreeSnapshot tree, MonitorTaskConfig config)
    {
        var t = config.Target;
        var (node, level) = ElementMatcher.FindNode(tree, t.ElementPath, t.ElementName, t.ElementAutomationId, t.ElementControlType);

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

    // ==== 同步组外部驱动模式 ====
    // 独立模式（默认）：任务自己的线程循环采集（触发→抓树→记录→结束判断）。
    // 同步模式：SyncGroupCoordinator 统一节拍抓树，经 RecordExternalSample 把同一时刻的
    // 快照推给组内各任务记录——时间戳统一为理论 tick 时刻，跨任务严格同刻。

    /// <summary>外部驱动标记：true 时 Start 不起采集线程，快照由同步组协调器推送。</summary>
    public bool ExternallyDriven { get; set; }

    /// <summary>是否已请求停止（同步组协调器据此跳过/剔除任务）。</summary>
    public bool IsStopRequested => _stopRequested;

    /// <summary>配置的采集间隔（毫秒）。</summary>
    public int IntervalMs => _config.Timer.IntervalMs;

    /// <summary>外部驱动模式启动：置 Running 但不起采集线程（Idle/Stopped 均可启动）。</summary>
    public void StartExternal()
    {
        if (State is TaskRunState.Running or TaskRunState.Paused) return;
        if (State is TaskRunState.Completed) return;
        _stopRequested = false;
        StartedAt = DateTime.Now;
        State = TaskRunState.Running;
        RaiseState();
    }

    /// <summary>外部模式停止落地：由协调器在检测到停止请求后调用（Run 循环不在场，需代为置状态）。</summary>
    public void MarkStopped()
    {
        if (State is TaskRunState.Stopped or TaskRunState.Completed or TaskRunState.Error) return;
        State = TaskRunState.Stopped;
        RaiseState();
    }

    /// <summary>外部模式完成落地：由协调器在组监控时长到期时调用（全组同时完成）。</summary>
    public void MarkCompleted()
    {
        if (State is TaskRunState.Stopped or TaskRunState.Completed or TaskRunState.Error) return;
        State = TaskRunState.Completed;
        RaiseState();
    }

    /// <summary>记录一次外部快照采样（同步组协调器在抓树线程调用）：定位→提取→记录→事件→结束判断。
    /// 时间戳 = 统一 tick 时刻（严格同刻），与本任务实际处理完成时刻无关。</summary>
    public void RecordExternalSample(DateTime tickAt, TreeSnapshot snapshot)
    {
        if (_stopRequested) { MarkStopped(); return; }
        if (State != TaskRunState.Running) return;
        try
        {
            var (nodeId, nodePath, values, matchLevel) = SelectMonitoredValues(snapshot, _config);
            if (nodeId < 0) return; // 本轮未定位到节点：跳过不记空点

            LogMatchDiagnostics(snapshot, nodeId, matchLevel); // 同步模式同样记录定位诊断（一次）

            var records = BuildRecords(tickAt, nodeId, nodePath, values, _config);
            var sample = new MonitorSample(tickAt, nodeId, nodePath, values, records);
            lock (_lock)
            {
                _samples.Add(sample);
                _records.AddRange(records);
            }
            SampleTaken?.Invoke(this, new MonitorTaskEventArgs { Sample = sample });

            // 结束条件判断（与独立模式同款语义）
            var stopCond = StopConditionRuntimeFactory.Create(_config.StopCondition);
            var stopCtx = new StopContext
            {
                Now = tickAt,
                TaskStartedAt = StartedAt,
                SampleCount = SampleCount,
                LastValues = values,
                PreviousValue = null,
            };
            if (stopCond.ShouldStop(stopCtx)) Complete();
        }
        catch
        {
            // 单轮外部采样失败不终止任务
        }
    }

    /// <summary>构造采样记录行：有提取规则时展开为 值1..值N，否则逐属性记录（独立/同步模式共用）。</summary>
    private static List<SampleRecord> BuildRecords(
        DateTime now, int nodeId, string nodePath, Dictionary<string, string?> values, MonitorTaskConfig config)
    {
        var records = new List<SampleRecord>();
        if (config.Extractors is { Count: > 0 })
        {
            var raw = values.Values.FirstOrDefault();
            var extracted = ValueExtractor.ExtractAll(raw, config.Extractors);
            for (int i = 0; i < extracted.Count; i++)
                records.Add(new SampleRecord(now, nodeId, nodePath, $"值{i + 1}", extracted[i]));
        }
        else
        {
            foreach (var (k, v) in values)
                records.Add(new SampleRecord(now, nodeId, nodePath, k, v ?? ""));
        }
        return records;
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
