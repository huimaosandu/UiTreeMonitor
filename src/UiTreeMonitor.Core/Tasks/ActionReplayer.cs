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
            var name = TrySemanticClick(x, y, out var why);
            if (name is not null)
            {
                AppLog.Write("ACTION", $"ReplayCoordInvoke: ({x},{y}) → 语义 {name} (cursor untouched)");
                return new ActionResult { Success = true, ReturnValue = $"语义 {name} @({x},{y})（未动光标）" };
            }
            AppLog.Write("ACTION", $"ReplayCoordInvoke: ({x},{y}) → FAILED: {why}");
            return new ActionResult { Success = false, Error = $"坐标 ({x},{y}) 处无法执行语义动作：{why ?? "未知原因"}" };
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

    /// <summary>UIA "当前状态无效"：0x80131509 Operation is not valid due to the current state of the object。
    /// Chromium 元素在重绘/焦点切换的瞬间读 Pattern 会抛它，属瞬态错误，重取元素再试通常就好。</summary>
    private const int HResultInvalidState = unchecked((int)0x80131509);

    /// <summary>坐标处元素的语义点击（带瞬态重试）：FromPoint 命中元素 → 按 UiaPatternCatalog 的优先级执行最贴切的
    /// UIA 动作（光标不动）。顺序 Invoke → LegacyIAccessible.DoDefaultAction → Toggle → SelectionItem.Select →
    /// ExpandCollapse.Expand/Collapse。浏览器（Chromium）的菜单项常常只有 ExpandCollapse、没有 Invoke。
    /// 返回"动作+元素名"；都不可用返回 null，并把该元素**实际支持的 Pattern 清单 / 各档失败原因**写进 <paramref name="failureDetail"/>。</summary>
    private static string? TrySemanticClick(int x, int y, out string? failureDetail)
    {
        // ★ 瞬态重试：日志实测同一坐标时好时坏（(1568,152) 这次成功、下次 0x80131509）。
        //   每轮都重新 ElementFromPoint，避免复用已经失效的 COM 引用。
        failureDetail = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ok = TrySemanticClickOnce(x, y, out var detail, out var transient);
            if (ok is not null) return ok;
            failureDetail = detail;
            if (!transient) return null; // 确定性失败（无元素/自己 UI/确实没有点击语义）→ 不必重试
            if (attempt < 2) Thread.Sleep(80);
        }
        return null;
    }

    private static string? TrySemanticClickOnce(int x, int y, out string? failureDetail, out bool transient)
    {
        failureDetail = null;
        transient = false;
        try
        {
            var uia = (IUIAutomation)new CUIAutomation();
            var el = uia.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (el is null) { failureDetail = "该坐标没有元素"; return null; }
            int pid;
            try { pid = el.CurrentProcessId; }
            catch { failureDetail = "元素正在销毁"; transient = true; return null; }
            if (pid == Environment.ProcessId) { failureDetail = "该坐标落在录制工具自身界面"; return null; } // 自己 UI → 不语义化

            var name = "";
            try { name = el.CurrentName ?? ""; } catch { /* 名字读不到不碍事 */ }
            var label = name.Length > 0 ? name : $"pid {pid}";

            var errors = new List<string>();
            var sawTransient = false;

            // ★ ① 先对 FromPoint 命中的元素按点击优先级试 5 档
            var (hit, transientHit) = TryPatternsOn(el, errors);
            if (hit is not null) return hit;
            sawTransient |= transientHit;

            // ★ ② 命中容器（菜单 List / 弹层 Pane / 分组）时向点击点下钻：
            //   Chromium 的弹出菜单 FromPoint 常返回**容器 List**，对它 Invoke 必然失败，
            //   真正被点的 MenuItem 是它的子元素（日志实测：坐标处支持 ScrollItem/TextChild，无点击语义）
            var ctrlId = 0;
            try { ctrlId = el.CurrentControlType; } catch { }
            if (IsContainerControlType(ctrlId))
            {
                foreach (var cand in CandidatesAtPoint(uia, el, x, y))
                {
                    var (deepHit, deepTransient) = TryPatternsOn(cand, null);
                    if (deepHit is not null) return deepHit;
                    sawTransient |= deepTransient;
                }
            }

            // 都没有成功 → 把元素实际支持的 Pattern 清单 + 各档失败原因报出来
            var supported = UiaPatternCatalog.SupportedNames(el);
            var ctrl = ctrlId == 0 ? "" : $" ControlType={ctrlId}";
            failureDetail = $"元素 '{label}'{ctrl} 没有可执行的点击语义（支持: " +
                (supported.Count > 0 ? string.Join(",", supported) : "无任何 Pattern") + "）" +
                (errors.Count > 0 ? "；失败档: " + string.Join(" | ", errors) : "");
            transient = sawTransient;
            return null;
        }
        catch (Exception ex)
        {
            failureDetail = $"语义探测失败: {ex.Message}";
            transient = ex.HResult == HResultInvalidState;
            return null;
        }
    }

    /// <summary>在一个元素上按点击优先级试 5 档（Invoke → LegacyIAccessible → Toggle → SelectionItem → ExpandCollapse）。
    /// 每档独立容错：某一种 Pattern 读失败不能中断后面的能力探测（以前整段一个大 try，
    /// Invoke 抛 0x80131509 就直接整体失败，后面 4 档根本没试）。</summary>
    private static (string? Hit, bool Transient) TryPatternsOn(IUIAutomationElement el, List<string>? errors)
    {
        var sawTransient = false;
        var label = "";
        try { label = el.CurrentName ?? ""; } catch { /* 名字读不到 */ }
        if (label.Length == 0)
        {
            try { label = $"pid {el.CurrentProcessId}"; } catch { label = "?"; }
        }

        try
        {
            if (el.GetCurrentPattern(PatternIds.Invoke) is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return ($"Invoke '{label}'", sawTransient);
            }
        }
        catch (Exception ex) { Note(ex, "Invoke"); }

        try
        {
            if (el.GetCurrentPattern(PatternIds.LegacyIAccessible) is IUIAutomationLegacyIAccessiblePattern legacy)
            {
                legacy.DoDefaultAction();
                return ($"DoDefaultAction '{label}'", sawTransient);
            }
        }
        catch (Exception ex) { Note(ex, "DoDefaultAction"); }

        try
        {
            if (el.GetCurrentPattern(PatternIds.Toggle) is IUIAutomationTogglePattern toggle)
            {
                toggle.Toggle();
                return ($"Toggle '{label}'", sawTransient);
            }
        }
        catch (Exception ex) { Note(ex, "Toggle"); }

        try
        {
            if (el.GetCurrentPattern(PatternIds.SelectionItem) is IUIAutomationSelectionItemPattern selectItem)
            {
                selectItem.Select();
                return ($"Select '{label}'", sawTransient);
            }
        }
        catch (Exception ex) { Note(ex, "Select"); }

        try
        {
            if (el.GetCurrentPattern(PatternIds.ExpandCollapse) is IUIAutomationExpandCollapsePattern expandCollapse)
            {
                // 点击语义 = 打开/收起（菜单/树节点）：已展开时再点应收起
                var state = expandCollapse.CurrentExpandCollapseState;
                if (state == ExpandCollapseState.ExpandCollapseState_Expanded)
                {
                    expandCollapse.Collapse();
                    return ($"Collapse '{label}'", sawTransient);
                }
                expandCollapse.Expand();
                return ($"Expand '{label}'", sawTransient);
            }
        }
        catch (Exception ex) { Note(ex, "Expand"); }

        return (null, sawTransient);

        void Note(Exception ex, string stage)
        {
            if (ex.HResult == HResultInvalidState)
            {
                // 元素状态瞬时无效：值得重试，也值得报出来（Chromium 重绘瞬间的典型错误）
                sawTransient = true;
                errors?.Add($"{stage}: 元素状态瞬时无效(0x80131509)");
            }
            else if (ex.Message.Contains("support", StringComparison.OrdinalIgnoreCase))
            {
                // "不支持该 Pattern"是正常情况（元素就没这个能力），不算失败原因
            }
            else errors?.Add($"{stage}={ex.Message}");
        }
    }

    /// <summary>容器类 ControlType（命中它才值得向点击点下钻）。50008=List、50009=Menu、50010=MenuBar、
    /// 50026=Group、50033=Pane、50025=Custom、50030=Document、50032=Window。</summary>
    private static bool IsContainerControlType(int ctrlType)
        => ctrlType is 50008 or 50009 or 50010 or 50025 or 50026 or 50030 or 50032 or 50033;

    /// <summary>从 root 起向点击点方向逐层取"包含该点"的子元素，返回 深→浅 的候选链（含 root 兜底）。
    /// 用途：FromPoint 只给最上层元素，Chromium 弹出菜单给的是容器 List，真正要点的 MenuItem 在它下面。</summary>
    private static List<IUIAutomationElement> CandidatesAtPoint(IUIAutomation uia, IUIAutomationElement root, int x, int y)
    {
        var chain = new List<IUIAutomationElement>();
        IUIAutomationElement? cur = root;
        try
        {
            var walker = uia.ControlViewWalker;
            for (var depth = 0; depth < 6; depth++)
            {
                IUIAutomationElement? next = null;
                try
                {
                    var child = walker.GetFirstChildElement(cur);
                    while (child is not null)
                    {
                        try
                        {
                            var r = child.CurrentBoundingRectangle;
                            if (r.right > r.left && r.bottom > r.top
                                && x >= r.left && x < r.right && y >= r.top && y < r.bottom
                                && child.CurrentIsOffscreen == 0)
                            {
                                next = child;
                                break;
                            }
                        }
                        catch { /* 子元素正在销毁 */ }
                        child = walker.GetNextSiblingElement(child);
                    }
                }
                catch { break; }
                if (next is null) break;
                cur = next;
            }

            // 由深到浅收集（最深子元素才是真正被点的那个）
            IUIAutomationElement? p = cur;
            var guard = 0;
            while (p is not null && guard++ < 8)
            {
                chain.Add(p);
                try { p = walker.GetParentElement(p); } catch { break; }
            }
        }
        catch { /* 下钻失败就只试 root */ }
        chain.Add(root);
        return chain;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
