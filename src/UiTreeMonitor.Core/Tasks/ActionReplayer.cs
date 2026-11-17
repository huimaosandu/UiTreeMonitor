using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axe.Windows.Core.Bases;
using UIAutomationClient;
using UiTreeMonitor.Core;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Uia;

/// <summary>回放进度信息。</summary>
public sealed class PlaybackProgress
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public ActionRecord CurrentRecord { get; init; } = null!;
    public bool IsComplete { get; init; }
}

/// <summary>
/// Action 回放引擎：按脚本顺序依次执行 ActionRecord，
/// 每条记录通过 ElementPath 在当前 UIA 树中重定位目标元素后调用 PatternActionRunner。
/// 支持：速度倍率、取消令牌、进度回调、单步执行。
/// </summary>
public sealed class ActionReplayer : IDisposable
{
    private bool _disposed;

    /// <summary>按脚本顺序回放（STA 线程内执行，支持速度倍率与取消）。</summary>
    /// <param name="script">要回放的脚本</param>
    /// <param name="elementResolver">元素解析器：通过 ElementPath 找到实时 A11yElement（需在 STA 线程）</param>
    /// <param name="speedMultiplier">速度倍率（1.0=原速，2.0=两倍速；影响步骤间延迟）</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="progress">进度回调</param>
    /// <returns>所有执行结果</returns>
    public List<ActionResult> Playback(
        ActionScript script,
        Func<string, A11yElement?> elementResolver,
        double speedMultiplier = 1.0,
        CancellationToken ct = default,
        IProgress<PlaybackProgress>? progress = null)
    {
        if (script is null) throw new ArgumentNullException(nameof(script));
        if (elementResolver is null) throw new ArgumentNullException(nameof(elementResolver));
        if (_disposed) throw new ObjectDisposedException(nameof(ActionReplayer));

        var results = new List<ActionResult>();
        var records = script.Records;
        var total = records.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var record = records[i];

            // ★ 输入动作记录（Input_Mouse/Input_Keyboard）：直接 SendInput 模拟，不依赖元素重定位
            if (record.PatternName is InputSimulator.MousePattern or InputSimulator.KeyboardPattern)
            {
                progress?.Report(new PlaybackProgress
                {
                    CurrentIndex = i, TotalCount = total, CurrentRecord = record, IsComplete = false
                });
                results.Add(ReplayInput(record));
            }
            // ★ 跳过自动录制的 UIA 事件记录（PatternName 以 "UIA_Event_" 开头）
            //   这些是事件监听日志，不是用户操作脚本，无法通过 Pattern.Invoke 回放
            else if (record.PatternName.StartsWith("UIA_Event_", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ActionResult
                {
                    Success = false,
                    Error = $"[事件监听] {record.MethodName}（不可回放，仅用于记录/导出）"
                });
            }
            // ★ coord:v1 延迟定位的语义 Invoke（UIA 模式"点击即关窗"场景）：
            //   录制时元素已销毁只存坐标；回放时弹窗已被前置步骤重新打开，FromPoint 命中该按钮
            //   → InvokePattern.Invoke 语义执行（光标不动）；不可语义化 → 该步失败（不真点击，保持纯语义）
            else if (record.ElementPath is string coordPath && coordPath.StartsWith("coord:v1|", StringComparison.Ordinal))
            {
                progress?.Report(new PlaybackProgress
                {
                    CurrentIndex = i, TotalCount = total, CurrentRecord = record, IsComplete = false
                });
                results.Add(ReplayCoordInvoke(coordPath));
            }
            else
            {
                // 报告进度
                progress?.Report(new PlaybackProgress
                {
                    CurrentIndex = i,
                    TotalCount = total,
                    CurrentRecord = record,
                    IsComplete = false
                });

                // 通过 ElementPath 重定位目标元素
                var element = elementResolver(record.ElementPath);
                if (element is null)
                {
                    results.Add(new ActionResult { Success = false, Error = $"元素路径 [{record.ElementPath}] 未找到（UI 结构可能已变化）" });
                    continue;
                }

                // 找到对应 Pattern
                A11yPattern? targetPattern = null;
                if (element.Patterns is not null)
                {
                    targetPattern = element.Patterns.FirstOrDefault(p =>
                        string.Equals(p.Name, record.PatternName, StringComparison.OrdinalIgnoreCase));
                }

                if (targetPattern is null)
                {
                    results.Add(new ActionResult { Success = false, Error = $"Pattern [{record.PatternName}] 不存在于目标元素上" });
                    continue;
                }

                // 执行 Action
                results.Add(PatternActionRunner.Run(element, targetPattern, record.MethodName, record.Parameters));
            }

            // 步骤间延迟（原录制时间间隔 × 速度倍率，最小 100ms；Input 模拟动作本身自带节奏，此处同样适用）
            if (i < total - 1 && speedMultiplier > 0)
            {
                var nextTs = records[i + 1].Timestamp;
                var gapMs = (nextTs - record.Timestamp).TotalMilliseconds / speedMultiplier;
                var waitMs = Math.Max(100, (int)gapMs);
                Thread.Sleep(waitMs);
            }
        }

        // 报告完成
        progress?.Report(new PlaybackProgress
        {
            CurrentIndex = total,
            TotalCount = total,
            IsComplete = true
        });

        return results;
    }

    /// <summary>单步执行一条 Record（Input 记录不需要目标元素，element 可为 null）。</summary>
    /// <returns>(是否成功, 结果文本)</returns>
    public (bool Success, string? Result) ExecuteOne(ActionRecord record, A11yElement? element)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (_disposed) throw new ObjectDisposedException(nameof(ActionReplayer));

        // ★ 输入动作记录：直接 SendInput 模拟（鼠标点击/键盘组合），无需元素
        if (record.PatternName is InputSimulator.MousePattern or InputSimulator.KeyboardPattern)
        {
            var input = ReplayInput(record);
            return (input.Success, input.Error);
        }

        // ★ UIA 事件记录不可回放（与 Playback 一致；此前单步回放报 "Pattern [UIA_Event_xxx] 不存在"）
        if (record.PatternName.StartsWith("UIA_Event_", StringComparison.OrdinalIgnoreCase))
            return (false, $"[事件监听] {record.MethodName}（不可回放，仅用于记录/导出）");

        // ★ coord:v1 延迟定位的语义 Invoke（单步回放同 Playback）
        if (record.ElementPath is string coordPath1 && coordPath1.StartsWith("coord:v1|", StringComparison.Ordinal))
        {
            var r = ReplayCoordInvoke(coordPath1);
            return (r.Success, r.DisplayText);
        }

        if (element is null) throw new ArgumentNullException(nameof(element));

        // 找 Pattern
        A11yPattern? targetPattern = null;
        if (element.Patterns is not null)
        {
            targetPattern = element.Patterns.FirstOrDefault(p =>
                string.Equals(p.Name, record.PatternName, StringComparison.OrdinalIgnoreCase));
        }

        if (targetPattern is null)
            return (false, $"Pattern [{record.PatternName}] 不存在");

        var result = PatternActionRunner.Run(element, targetPattern, record.MethodName, record.Parameters);
        return (result.Success, result.DisplayText);
    }

    /// <summary>回放一条输入动作记录（Input_Mouse：点击/拖动 SendInput；Input_Keyboard：组合键 SendInput）。</summary>
    private static ActionResult ReplayInput(ActionRecord record)
    {
        try
        {
            if (record.PatternName == InputSimulator.MousePattern)
            {
                var coords = (record.Parameters ?? Array.Empty<object?>())
                    .Select(p => p?.ToString() ?? "").ToList();

                // 拖动：两个坐标参数（起点、终点）
                if (record.MethodName is InputSimulator.DragMethod or InputSimulator.RightDragMethod)
                {
                    if (coords.Count >= 2
                        && TryParseCoord(coords[0], out var sx, out var sy)
                        && TryParseCoord(coords[1], out var ex, out var ey))
                    {
                        InputSimulator.MouseDrag(sx, sy, ex, ey, record.MethodName == InputSimulator.RightDragMethod);
                        return new ActionResult { Success = true };
                    }
                    return new ActionResult { Success = false, Error = $"拖动坐标无效: {string.Join(" | ", coords)}" };
                }

                // 点击：一个坐标参数（鼠标键盘模式=纯 SendInput 真点击，绝不语义化——模式不混合）
                if (coords.Count >= 1 && TryParseCoord(coords[0], out var x, out var y))
                {
                    InputSimulator.MouseClick(x, y, record.MethodName == InputSimulator.RightClickMethod);
                    return new ActionResult { Success = true };
                }
                return new ActionResult { Success = false, Error = $"鼠标坐标无效: {string.Join(" | ", coords)}" };
            }

            if (InputSimulator.TryParseCombo(record.MethodName, out var vk, out var ctrl, out var shift, out var alt))
            {
                InputSimulator.SendKeyCombo(vk, ctrl, shift, alt);
                return new ActionResult { Success = true };
            }
            return new ActionResult { Success = false, Error = $"无法解析按键组合: {record.MethodName}" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>执行 coord:v1|x,y 延迟定位的语义 Invoke：FromPoint 命中可 Invoke 元素 → Invoke（光标不动）。
    /// 坐标处无可 Invoke 元素（弹窗未打开/位置变化）→ 该步失败（不真点击，UIA 语义模式保持纯语义）。</summary>
    private static ActionResult ReplayCoordInvoke(string coordPath)
    {
        var xy = coordPath["coord:v1|".Length..].Split(',');
        if (xy.Length == 2 && int.TryParse(xy[0].Trim(), out var x) && int.TryParse(xy[1].Trim(), out var y))
        {
            var name = TrySemanticClick(x, y);
            if (name is not null)
            {
                AppLog.Write("ACTION", $"ReplayCoordInvoke: ({x},{y}) → 语义 {name} (cursor untouched)");
                return new ActionResult { Success = true, ReturnValue = $"语义 {name} @({x},{y})（未动光标）" };
            }
            AppLog.Write("ACTION", $"ReplayCoordInvoke: ({x},{y}) → FAILED (no semantic pattern at point)");
            return new ActionResult { Success = false, Error = $"坐标 ({x},{y}) 处元素没有可执行的语义动作（Invoke/DoDefaultAction/Toggle/Select/Expand 全不可用，或弹窗未打开/页面已变化）" };
        }
        return new ActionResult { Success = false, Error = $"coord:v1 路径无效: {coordPath}" };
    }

    /// <summary>"x,y" 坐标字符串解析。</summary>
    private static bool TryParseCoord(string s, out int x, out int y)
    {
        x = y = 0;
        var parts = s.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out x)
            && int.TryParse(parts[1].Trim(), out y);
    }

    /// <summary>坐标处元素的语义点击：FromPoint 命中元素 → 按能力优先级执行最贴切的 UIA 动作（光标不动）。
    /// 顺序 Invoke → LegacyIAccessible.DoDefaultAction → Toggle → SelectionItem.Select → ExpandCollapse.Expand。
    /// 浏览器（Chromium）的菜单项常常只有 ExpandCollapse、没有 Invoke，只认 Invoke 会假失败。
    /// 返回"动作+元素名"；都不可用（无 Pattern/是自己 UI/元素销毁/COM 失败）返回 null，由调用方记为该步失败。</summary>
    private static string? TrySemanticClick(int x, int y)
    {
        try
        {
            var uia = (IUIAutomation)new CUIAutomation();
            var el = uia.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (el is null) return null;
            int pid;
            try { pid = el.CurrentProcessId; }
            catch { return null; } // 元素销毁中
            if (pid == Environment.ProcessId) return null; // 点击点是自己 UI → 不语义化，忠实真点击

            var name = "";
            try { name = el.CurrentName ?? ""; } catch { /* 名字读不到不碍事 */ }
            var label = name.Length > 0 ? name : $"pid {pid}";

            if (el.GetCurrentPattern(10000) is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return $"Invoke '{label}'";
            }
            if (el.GetCurrentPattern(10018) is IUIAutomationLegacyIAccessiblePattern legacy)
            {
                legacy.DoDefaultAction();
                return $"DoDefaultAction '{label}'";
            }
            if (el.GetCurrentPattern(10015) is IUIAutomationTogglePattern toggle)
            {
                toggle.Toggle();
                return $"Toggle '{label}'";
            }
            if (el.GetCurrentPattern(10010) is IUIAutomationSelectionItemPattern selectItem)
            {
                selectItem.Select();
                return $"Select '{label}'";
            }
            if (el.GetCurrentPattern(10002) is IUIAutomationExpandCollapsePattern expandCollapse)
            {
                // 点击语义 = 打开/收起（菜单/树节点）：已展开时再点应收起
                var state = expandCollapse.CurrentExpandCollapseState;
                if (state == ExpandCollapseState.ExpandCollapseState_Expanded)
                {
                    expandCollapse.Collapse();
                    return $"Collapse '{label}'";
                }
                expandCollapse.Expand();
                return $"Expand '{label}'";
            }
            return null; // 四种语义能力都没有 → 不做盲目真点击
        }
        catch
        {
            return null; // 任何 COM 失败 → 该步失败
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
