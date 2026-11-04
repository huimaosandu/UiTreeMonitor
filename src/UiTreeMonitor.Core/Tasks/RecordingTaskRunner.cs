// 录制任务执行引擎：树形步骤遍历（action/loop/delay/timer/trigger），STA 后台线程，可取消
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>任务运行状态。</summary>
public enum RecTaskRunState
{
    Pending,      // 待启动
    Running,      // 运行中
    WaitingTimer, // 等待定时时刻
    WaitingTrigger, // 等待元素触发条件
    Completed,    // 全部步骤完成
    Failed,       // 某步骤失败中止
    Cancelled,    // 用户停止
}

/// <summary>运行状态快照（UI 线程轮询/事件刷新用）。</summary>
public sealed class TaskRunSnapshot
{
    public RecTaskRunState State { get; init; }
    public string CurrentStep { get; init; } = "";
    public int CurrentIndex { get; init; }      // 扁平序号（进度显示）
    public int TotalSteps { get; init; }        // 启用的扁平步骤数
    public int LoopRound { get; init; }         // 当前循环轮次（无循环=0）
    public string? LastError { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
}

/// <summary>
/// 录制任务执行器：在 STA 线程上按树遍历执行步骤。
/// - action：经 elementResolver 重定位后 PatternActionRunner 执行（Input/coord 记录无需元素）
/// - loop：子步骤重复 N 次（轮间隔可配）
/// - delay：可取消等待
/// - timer：等待到指定时刻（已过则立即继续）
/// - trigger：轮询元素属性，条件满足后执行子步骤（无子步骤=纯等待点）；超时失败
/// </summary>
public sealed class RecordingTaskRunner : IDisposable
{
    private readonly Func<string, A11yElement?> _elementResolver;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private volatile TaskRunSnapshot _snapshot = new() { State = RecTaskRunState.Pending };
    private readonly ActionReplayer _replayer = new();

    public RecordingTask Task { get; }
    public Guid TaskId => Task.Id;
    public TaskRunSnapshot Snapshot => _snapshot;
    /// <summary>状态变化回调（后台 STA 线程触发，UI 需自行调度）。</summary>
    public event Action<TaskRunSnapshot>? StateChanged;

    public RecordingTaskRunner(RecordingTask task, Func<string, A11yElement?> elementResolver)
    {
        Task = task;
        _elementResolver = elementResolver;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"RecTask:{Task.Name}",
        };
        _thread.SetApartmentState(ApartmentState.STA); // UIA COM + PatternActionRunner 要求 STA
        _thread.Start();
    }

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _replayer.Dispose();
        _cts.Dispose();
    }

    // ==== 内部执行 ====

    private void Run()
    {
        Set(RecTaskRunState.Running, "启动");
        var flat = FlattenEnabled(Task.Steps).ToList();
        var index = 0;
        try
        {
            foreach (var step in Task.Steps)
            {
                if (_cts.IsCancellationRequested) break;
                if (!step.IsEnabled) continue;
                ExecuteNode(step, flat, ref index, round: 0);
            }
            Set(_cts.IsCancellationRequested ? RecTaskRunState.Cancelled : RecTaskRunState.Completed,
                _cts.IsCancellationRequested ? "已停止" : "完成");
        }
        catch (OperationCanceledException)
        {
            Set(RecTaskRunState.Cancelled, "已停止");
        }
        catch (Exception ex)
        {
            AppLog.Write("TASK", $"RecordingTask '{Task.Name}' FAILED: {ex.Message}");
            Set(RecTaskRunState.Failed, "失败", error: ex.Message);
        }
    }

    private void ExecuteNode(StepNode node, List<StepNode> flat, ref int index, int round)
    {
        _cts.Token.ThrowIfCancellationRequested();
        if (!node.IsEnabled) return;
        index = Math.Max(index, 0);
        var myIndex = flat.IndexOf(node);
        if (myIndex >= 0) index = myIndex;

        switch (node.Kind)
        {
            case StepKinds.Action:
                Set(RecTaskRunState.Running, node.DisplayText, index: index, round: round);
                ExecuteAction(node);
                break;

            case StepKinds.Delay:
                Set(RecTaskRunState.Running, node.DisplayText, index: index, round: round);
                Sleep(node.DelayMs);
                break;

            case StepKinds.Timer:
                Set(RecTaskRunState.WaitingTimer, node.DisplayText, index: index, round: round);
                var wait = node.TriggerAt - DateTime.Now;
                if (wait > TimeSpan.Zero)
                {
                    AppLog.Write("TASK", $"Timer step: waiting {wait.TotalSeconds:F0}s until {node.TriggerAt:HH:mm:ss}");
                    Sleep((int)Math.Min(wait.TotalMilliseconds, int.MaxValue));
                }
                break;

            case StepKinds.Loop:
            {
                // 三种结束条件（StepNode.LoopEndMode）：count=次数 / duration=运行时长 / deadline=截至时刻。
                // ★ 每轮开始前判定（含第 1 轮）——截至时刻已过=跳过循环体（与定时步骤"已过立即继续"同语义）；
                //   轮间隔计入时长/截至判定（Sleep 后再判下一轮）。
                var start = DateTime.Now;
                int i = 0;
                while (node.LoopHasMoreRounds(i, start))
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    i++;
                    Set(RecTaskRunState.Running, $"{node.DisplayText} 第 {i} 轮", index: index, round: i);
                    foreach (var child in node.Children)
                        ExecuteNode(child, flat, ref index, round: i);
                    if (node.LoopIntervalMs > 0 && node.LoopHasMoreRounds(i, start))
                        Sleep(node.LoopIntervalMs);
                }
                AppLog.Write("TASK", $"Loop finished: {i} rounds (mode={node.LoopEndMode})");
                break;
            }

            case StepKinds.Trigger:
                ExecuteTrigger(node, flat, ref index, round);
                break;

            case StepKinds.Screenshot:
                Set(RecTaskRunState.Running, node.DisplayText, index: index, round: round);
                ExecuteScreenshot(node);
                break;
        }
    }

    private void ExecuteAction(StepNode node)
    {
        var record = node.Action;
        if (record is null) return;
        try
        {
            var needsElement = record.PatternName is not InputSimulator.MousePattern
                and not InputSimulator.KeyboardPattern
                && !(record.ElementPath?.StartsWith("coord:v1|", StringComparison.Ordinal) ?? false)
                && !record.PatternName.StartsWith("UIA_Event_", StringComparison.OrdinalIgnoreCase);
            A11yElement? element = null;
            if (needsElement)
            {
                element = _elementResolver(record.ElementPath ?? "");
                if (element is null)
                {
                    AppLog.Write("TASK", $"Action step FAILED: element not found [{record.ElementPath}]");
                    throw new InvalidOperationException($"元素未找到: {record.ElementPath}");
                }
            }
            var (ok, result) = _replayer.ExecuteOne(record, element);
            AppLog.Write("TASK", $"Action step {(ok ? "OK" : "FAIL")}: {record.PatternName}.{record.MethodName} → {result}");
            if (!ok) throw new InvalidOperationException(result ?? "执行失败");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"步骤 [{node.DisplayText}] 失败: {ex.Message}", ex);
        }
    }

    /// <summary>截图步骤：按 spec 重定位目标元素 → 对其包围矩形（屏幕物理像素）截图存 img-时间戳.png 到配置目录。</summary>
    private void ExecuteScreenshot(StepNode node)
    {
        try
        {
            if (string.IsNullOrEmpty(node.ScreenshotSpec))
                throw new InvalidOperationException("截图步骤未配置目标元素");
            if (string.IsNullOrEmpty(node.ScreenshotDir))
                throw new InvalidOperationException("截图步骤未配置保存目录");

            var el = _elementResolver(node.ScreenshotSpec);
            if (el is null || el.BoundingRectangle.IsEmpty)
                throw new InvalidOperationException($"截图目标元素未找到: {node.ScreenshotName}");

            var (ok, file) = ScreenCapture.CaptureRegion(el.BoundingRectangle, node.ScreenshotDir);
            if (!ok)
                throw new InvalidOperationException($"截图失败: {file}");
            AppLog.Write("TASK", $"Screenshot step OK: '{node.ScreenshotName}' → {file}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"步骤 [{node.DisplayText}] 失败: {ex.Message}", ex);
        }
    }

    private void ExecuteTrigger(StepNode node, List<StepNode> flat, ref int index, int round)
    {
        var t = node.Trigger;
        if (t is null || string.IsNullOrEmpty(t.ElementSpec))
            throw new InvalidOperationException("触发器未配置目标元素");

        Set(RecTaskRunState.WaitingTrigger, node.DisplayText, index: index, round: round);
        AppLog.Write("TASK", $"Trigger step: waiting '{t.ElementName}' {t.Property} {t.Condition} {t.ExpectedValue} (timeout {t.TimeoutSec}s)");
        var deadline = t.TimeoutSec > 0 ? DateTime.Now.AddSeconds(t.TimeoutSec) : (DateTime?)null;
        string? lastValue = null;
        bool hasBaseline = false;

        while (true)
        {
            _cts.Token.ThrowIfCancellationRequested();

            var current = ReadElementProperty(t);
            if (current.IsAvailable)
            {
                var value = current.Value ?? "";
                bool met = t.Condition switch
                {
                    "Equals" => value == (t.ExpectedValue ?? ""),
                    "NotEquals" => value != (t.ExpectedValue ?? ""),
                    "Contains" => value.Contains(t.ExpectedValue ?? "", StringComparison.Ordinal),
                    "Changed" => hasBaseline && value != lastValue,
                    "Greater" => double.TryParse(value, out var g) && double.TryParse(t.ExpectedValue, out var ge) && g > ge,
                    "Less" => double.TryParse(value, out var l) && double.TryParse(t.ExpectedValue, out var le) && l < le,
                    _ => false,
                };
                if (t.Condition == "Changed" && !hasBaseline) { /* 首读作基线，不触发 */ }
                if (met)
                {
                    AppLog.Write("TASK", $"Trigger MET: '{t.ElementName}' {t.Property} = '{value}' → 执行子步骤 {node.Children.Count} 个");
                    foreach (var child in node.Children)
                        ExecuteNode(child, flat, ref index, round: round);
                    return;
                }
                lastValue = value;
                hasBaseline = true;
            }

            if (deadline is not null && DateTime.Now > deadline)
                throw new InvalidOperationException($"触发器等待超时（{t.TimeoutSec}s）：'{t.ElementName}' {t.Property} {t.Condition} {t.ExpectedValue}");
            Sleep(Math.Max(100, t.PollIntervalMs));
        }
    }

    /// <summary>读取触发器目标元素的监控属性（找不到/读取失败 → IsAvailable=false，继续轮询）。</summary>
    private (bool IsAvailable, string? Value) ReadElementProperty(ElementTriggerSpec t)
        => TryReadProperty(_elementResolver, t.ElementSpec, t.Property, out var v) ? (true, v) : (false, null);

    /// <summary>按 spec 解析元素并读取监控属性（触发器编辑弹窗"读取当前值"复用）。
    /// 返回 false=元素未找到/解析器为空；true 时 value 可能为 null（属性无值）。</summary>
    public static bool TryReadProperty(Func<string, A11yElement?>? resolver, string spec, string property, out string? value)
    {
        value = null;
        try
        {
            var el = resolver?.Invoke(spec);
            if (el is null) return false;
            value = property switch
            {
                "Name" => el.Name,
                "IsEnabled" => el.IsEnabled ? "True" : "False",
                _ => PatternValue(el, property),
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>从元素 Patterns 中取属性值（"Value"→ValuePattern.Value 等便捷名，或 "Pattern.Prop" 全名）。</summary>
    private static string? PatternValue(A11yElement el, string property)
    {
        try
        {
            if (el.Patterns is null) return null;
            foreach (var p in el.Patterns)
            {
                if (p.Properties is null || p.Properties.Count == 0) continue;
                foreach (var pp in p.Properties)
                {
                    var full = $"{p.Name}.{pp.Name}";
                    if (string.Equals(pp.Name, property, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(full, property, StringComparison.OrdinalIgnoreCase)
                        || (property == "Value" && full is "ValuePattern.Value" or "LegacyIAccessiblePattern.Value")
                        || (property == "RangeValue" && full == "RangeValuePattern.Value")
                        || (property == "ToggleState" && full == "TogglePattern.ToggleState")
                        || (property == "ExpandCollapseState" && full == "ExpandCollapsePattern.ExpandCollapseState"))
                        return pp.NodeValue;
                }
            }
            return null;
        }
        catch { return null; }
    }

    private void Sleep(int ms)
    {
        // 分片可取消等待
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            _cts.Token.ThrowIfCancellationRequested();
            Thread.Sleep(Math.Min(200, (int)(end - DateTime.UtcNow).TotalMilliseconds));
        }
    }

    private static IEnumerable<StepNode> FlattenEnabled(IEnumerable<StepNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (!n.IsEnabled) continue;
            yield return n;
            foreach (var c in FlattenEnabled(n.Children)) yield return c;
        }
    }

    private void Set(RecTaskRunState state, string step, int? index = null, int? round = null, string? error = null)
    {
        _snapshot = new TaskRunSnapshot
        {
            State = state,
            CurrentStep = step,
            CurrentIndex = index ?? _snapshot.CurrentIndex,
            TotalSteps = _snapshot.TotalSteps == 0 ? FlattenEnabled(Task.Steps).Count() : _snapshot.TotalSteps,
            LoopRound = round ?? _snapshot.LoopRound,
            LastError = error ?? _snapshot.LastError,
            StartedAt = _snapshot.StartedAt == default ? DateTime.Now : _snapshot.StartedAt,
            EndedAt = state is RecTaskRunState.Completed or RecTaskRunState.Failed or RecTaskRunState.Cancelled
                ? DateTime.Now
                : _snapshot.EndedAt,
        };
        StateChanged?.Invoke(_snapshot);
    }
}
