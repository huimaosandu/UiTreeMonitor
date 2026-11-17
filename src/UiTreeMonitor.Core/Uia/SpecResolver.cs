// spec 快速定位：直接用 raw UIA walker 遍历（不包装 A11yElement）
//
// ★ 为什么不用 UiaSession：它的 GetFirstChild / GetNextSibling 每返回一个节点都会
//   `new DesktopElement(..., setMembers: true)`，实测约 10ms/节点。Edge 的 Copilot 树 229 个节点
//   就要 2.3 秒——回放每一步之前都要做一次定位，这就是"播放很慢"的根因。
//   这里只读几个必要属性（每个约 0.1ms），229 节点约 150-250ms；命中后只把**那一个**元素
//   包装成 A11yElement（10ms），交给 PatternActionRunner 执行。
using System;
using System.Collections.Generic;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Sampler;
using UIAutomationClient;

namespace UiTreeMonitor.Core.Uia;

/// <summary>spec 查询条件（由 "spec:v1|autoId=…|name=…|type=…|class=…|rect=…|rid=…" 解析而来）。</summary>
public sealed class SpecQuery
{
    public string AutomationId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string RuntimeId { get; init; } = "";
    public bool HasRect { get; init; }
    public int RectLeft { get; init; }
    public int RectTop { get; init; }
}

/// <summary>在一个窗口（hwnd）子树里按 spec 评分查找元素（RuntimeId 优先，命中即返回）。</summary>
public static class SpecResolver
{
    private const int Threshold = 40;   // 最低分：autoId(50) 或 type+name(40) 级别
    private const int MaxNodes = 3000;

    /// <summary>查找并包装成 A11yElement；找不到返回 null，detail 带 visited/score 供日志排查。</summary>
    public static A11yElement? Find(IntPtr hwnd, SpecQuery q, out string detail)
    {
        detail = "";
        if (hwnd == IntPtr.Zero) return null;

        IUIAutomation? uia;
        try { uia = (IUIAutomation)new CUIAutomation(); } catch { return null; }

        IUIAutomationTreeWalker? walker;
        try { walker = uia.ControlViewWalker; } catch { return null; }

        IUIAutomationElement? root;
        try { root = uia.ElementFromHandle(hwnd); } catch { return null; }
        if (root is null || walker is null) return null;

        var bestEl = (IUIAutomationElement?)null;
        var bestScore = 0;
        var visited = 0;
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(root);

        while (stack.Count > 0 && visited < MaxNodes)
        {
            var el = stack.Pop();
            visited++;
            try
            {
                // RuntimeId 优先：同一元素实例内唯一，不受 Name/AutomationId 变化影响
                if (q.RuntimeId.Length > 0)
                {
                    var ids = el.GetRuntimeId();
                    if (ids is { Length: > 0 } && string.Join(".", ids) == q.RuntimeId)
                    {
                        detail = $"hit by runtimeId (visited={visited})";
                        return Wrap(el);
                    }
                }

                string autoId = "", name = "", cls = "";
                int ct = 0;
                try { autoId = el.CurrentAutomationId ?? ""; } catch { /* 元素销毁 */ }
                try { name = el.CurrentName ?? ""; } catch { }
                try { cls = el.CurrentClassName ?? ""; } catch { }
                try { ct = (int)el.CurrentControlType; } catch { }

                var score = 0;
                var type = TreeSampler.ControlTypeName(ct);
                if (q.AutomationId.Length > 0 && autoId == q.AutomationId) score += 50;
                if (q.ControlType.Length > 0 && type == q.ControlType) score += 20;
                if (q.Name.Length > 0 && name == q.Name) score += 20;
                else if (q.Name.Length > 0 && name.Length > 0) score -= 10; // 名字不同只轻惩罚（动态名）
                if (q.ClassName.Length > 0 && cls == q.ClassName) score += 5;
                if (q.HasRect)
                {
                    try
                    {
                        var r = el.CurrentBoundingRectangle;
                        if (r.right > r.left && Math.Abs(r.left - q.RectLeft) <= 40 && Math.Abs(r.top - q.RectTop) <= 40)
                            score += 15;
                    }
                    catch { /* 矩形读不到 */ }
                }

                if (score > bestScore) { bestScore = score; bestEl = el; }
                if (score >= 90) break; // 满分（autoId+type+name）提前结束
            }
            catch { /* 元素已销毁，跳过 */ }

            try
            {
                var child = walker.GetFirstChildElement(el);
                while (child is not null)
                {
                    stack.Push(child);
                    child = walker.GetNextSiblingElement(child);
                }
            }
            catch { /* 子树遍历失败 */ }
        }

        if (bestEl is not null && bestScore >= Threshold)
        {
            detail = $"hit score={bestScore} (visited={visited})";
            return Wrap(bestEl);
        }
        detail = $"no match (visited={visited}, bestScore={bestScore})";
        return null;
    }

    /// <summary>把命中的 raw 元素包装成 A11yElement（PatternActionRunner 需要它）。只包装命中的一个。</summary>
    private static A11yElement? Wrap(IUIAutomationElement el)
    {
        try { return new Axe.Windows.Desktop.UIAutomation.DesktopElement(el, keepElement: true, setMembers: true); }
        catch { return null; }
    }

    /// <summary>全桌面按 AutomationId 精确找元素（跨进程重启、窗口移动都不受影响）。
    /// UIA 内部按 provider 分发查询，不逐节点 walk，比逐窗口遍历快得多。
    /// ★ App 项目不能 new CUIAutomation（EmbedInteropTypes 限制），所以这个能力必须放在 Core。
    /// ★ 回放跑在后台 MTA 线程，不能用 WPF 的 AutomationElement（它要求 STA）——这里走 raw COM。</summary>
    public static A11yElement? FindGlobalByAutomationId(string autoId, out string detail)
    {
        detail = "";
        if (string.IsNullOrEmpty(autoId)) return null;
        IUIAutomation? uia;
        try { uia = (IUIAutomation)new CUIAutomation(); }
        catch (Exception ex) { detail = $"CUIAutomation init failed: {ex.GetType().Name}"; return null; }
        try
        {
            // 30011 = UIA_AutomationIdPropertyId（不能引用静态常量类 UIA_PropertyIds，只能写字面量）
            var cond = uia.CreatePropertyCondition(30011, autoId);
            var root = uia.GetRootElement();
            // 7 = Subtree（Element 1 | Children 2 | Descendants 4），与录制监听的 scope 一致
            var hit = root.FindFirst((TreeScope)7, cond);
            if (hit is null) { detail = $"global autoId '{autoId}' not found"; return null; }
            var wrapped = Wrap(hit);
            detail = wrapped is null ? $"global hit for '{autoId}' but wrap failed" : $"hit by global autoId '{autoId}'";
            return wrapped;
        }
        catch (Exception ex)
        {
            detail = $"global search {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
