// 通用元素定位：结构路径锁定 + 精确属性匹配（不做放宽兜底——宁漏勿错）
using System;
using System.Collections.Generic;
using System.Linq;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>
/// 树内目标节点定位（两级：结构路径 + 精确属性）。
///   级别0 结构索引路径（ElementPath，如 "0/3/1/5"）：创建任务时记录，纯结构下钻唯一锁定——
///         不看 Name/AutoId，被监控的 Name 持续变化、AutomationId 缺失/重复（列表模板控件常见）均不影响；
///   级别1 精确属性匹配（Name + AutomationId + ControlType 全条件）：路径未配置（旧任务）或
///         结构变化失效时的兜底，条件齐备才命中。
/// 不做"忽略 Name / 仅 ControlType"等放宽兜底：放宽会取到非目标节点（错数据比漏数据更糟），
/// 两级均未命中时返回 null，由调用方跳过本轮采集并在日志留诊断（Name 变化场景由结构路径覆盖）。
/// </summary>
public static class ElementMatcher
{
    /// <summary>完整定位：结构路径优先，未命中/未配置时精确属性匹配。返回 (节点, 匹配级别 0/1)；未命中 (null, -1)。</summary>
    public static (ElementNode? Node, int Level) FindNode(
        TreeSnapshot tree, string? elementPath, string? elementName, string? elementAutomationId, string? elementControlType)
    {
        // 级别0：结构索引路径（唯一锁定；结构变化失效时降级精确属性）
        if (!string.IsNullOrEmpty(elementPath))
        {
            var pn = FindByPath(tree, elementPath);
            if (pn is not null) return (pn, 0);
        }

        // 级别1：精确属性匹配（全条件，null 条件跳过）
        var exact = FindExact(tree.Order.Select(id => tree.Nodes[id]), elementName, elementAutomationId, elementControlType);
        return exact is not null ? (exact, 1) : (null, -1);
    }

    /// <summary>结构索引路径下钻：从根开始按每级子索引逐层取子节点；越界/格式错误返回 null。</summary>
    public static ElementNode? FindByPath(TreeSnapshot tree, string? elementPath)
    {
        if (string.IsNullOrEmpty(elementPath)) return null;
        if (!tree.Nodes.TryGetValue(tree.RootId, out var cur)) return null;

        foreach (var seg in elementPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(seg, out var idx) || idx < 0 || idx >= cur.Children.Count) return null;
            if (!tree.Nodes.TryGetValue(cur.Children[idx], out cur)) return null;
        }
        return cur;
    }

    /// <summary>计算节点的结构索引路径（从根到节点每级子索引，"0/3/1/5"）；节点不在树内返回 null。</summary>
    public static string? BuildPath(TreeSnapshot tree, int nodeId)
    {
        if (!tree.Nodes.ContainsKey(nodeId) || !tree.Nodes.ContainsKey(tree.RootId)) return null;
        var parts = new List<int>();
        int cur = nodeId;
        while (cur != tree.RootId)
        {
            if (!tree.Nodes.TryGetValue(cur, out var n)) return null;
            var parent = n.ParentId;
            if (!tree.Nodes.TryGetValue(parent, out var p)) return null;
            var idx = IndexOf(p.Children, cur);
            if (idx < 0) return null;
            parts.Add(idx);
            cur = parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>按屏幕矩形定位节点（悬停/烧杯捕获场景）：矩形字符串 "L,T,R,B" 与 ControlType 同时相等
    /// 才命中（UIA 物理像素同源，矩形稳定唯一——Name 是实时变化值、AutomationId 常缺失/重复，均不可靠）；
    /// 多命中取深度最深。矩形为空或未命中返回 null。</summary>
    public static ElementNode? FindByRect(TreeSnapshot tree, string? boundingRect, string? controlType)
    {
        if (string.IsNullOrEmpty(boundingRect)) return null;
        ElementNode? best = null;
        foreach (var node in tree.Nodes.Values)
        {
            if (!string.Equals(node.BoundingRectangle, boundingRect, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(controlType) && node.ControlType != controlType) continue;
            if (best is null || node.Depth > best.Depth) best = node;
        }
        return best;
    }

    /// <summary>矩形容差匹配：ControlType 相同且两个矩形中心距 ≤ tolerancePx（取距离最近、深度最深）。
    /// 用于窗口缩放/滚动/DPI 变化造成 1-8px 偏移的场景（精确相等会全部落空）。</summary>
    public static ElementNode? FindByRectNear(TreeSnapshot tree, string? boundingRect, string? controlType, int tolerancePx)
    {
        if (string.IsNullOrEmpty(boundingRect)) return null;
        if (!TryParseRect(boundingRect, out var l, out var t, out var r, out var b)) return null;
        var cx = (l + r) / 2.0;
        var cy = (t + b) / 2.0;

        ElementNode? best = null;
        var bestDist = double.MaxValue;
        foreach (var node in tree.Nodes.Values)
        {
            if (!string.IsNullOrEmpty(controlType) && node.ControlType != controlType) continue;
            if (!TryParseRect(node.BoundingRectangle, out var nl, out var nt, out var nr, out var nb)) continue;
            var dx = (nl + nr) / 2.0 - cx;
            var dy = (nt + nb) / 2.0 - cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > tolerancePx) continue;
            if (dist < bestDist || (dist == bestDist && best is not null && node.Depth > best.Depth))
            {
                best = node;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>"L,T,R,B" → 四个整数（解析失败返回 false）。</summary>
    private static bool TryParseRect(string? rect, out int l, out int t, out int r, out int b)
    {
        l = t = r = b = 0;
        if (string.IsNullOrEmpty(rect)) return false;
        var parts = rect.Split(',');
        if (parts.Length != 4) return false;
        return int.TryParse(parts[0], out l) && int.TryParse(parts[1], out t)
            && int.TryParse(parts[2], out r) && int.TryParse(parts[3], out b);
    }

    /// <summary>精确属性匹配：全部非空条件同时满足才命中（条件为 null 表示不约束），取前序第一个。</summary>
    public static ElementNode? FindExact(
        IEnumerable<ElementNode> nodes, string? elementName, string? elementAutomationId, string? elementControlType)
    {
        foreach (var n in nodes)
        {
            if (elementName is not null && n.Name != elementName) continue;
            if (elementAutomationId is not null && n.AutomationId != elementAutomationId) continue;
            if (elementControlType is not null && n.ControlType != elementControlType) continue;
            return n;
        }
        return null;
    }

    /// <summary>IReadOnlyList 的 IndexOf（无原生扩展）：找不到返回 -1。</summary>
    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
