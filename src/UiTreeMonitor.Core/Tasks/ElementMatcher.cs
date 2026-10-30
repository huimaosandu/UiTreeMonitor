// 通用元素定位：三级降级匹配，保证被监控属性值变化时定位不失效
using System.Collections.Generic;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>
/// 树内目标节点定位（通用降级匹配）。
/// 被监控的属性值可能随时变化（最典型：元素 Name 本身就是监控的变化值，如实时数值文本），
/// 创建任务时记录的 ElementName 快照很快过期——因此按约束强度逐级放宽：
///   级别0 精确匹配（Name + AutomationId + ControlType）
///   级别1 忽略 Name（Name 是变化值时仍可按 AutomationId + ControlType 定位）
///   级别2 仅 ControlType 兜底（取第一个同类型节点）
/// 无论监控 Properties（Name/Value/…）还是 Patterns（ValuePattern.Value 等），定位均不受值变化影响。
/// </summary>
public static class ElementMatcher
{
    /// <summary>按给定节点顺序定位，返回 (节点, 匹配级别)；三级均未命中返回 (null, -1)。</summary>
    public static (ElementNode? Node, int Level) FindNode(
        IEnumerable<ElementNode> nodes, string? elementName, string? elementAutomationId, string? elementControlType)
    {
        // 级别0：全条件精确匹配
        var node = Find(nodes, elementName, elementAutomationId, elementControlType);
        if (node is not null) return (node, 0);

        // 级别1：忽略 Name（被监控的 Name 变化后，创建时的快照值再也匹配不上）
        if (elementName is not null)
        {
            node = Find(nodes, null, elementAutomationId, elementControlType);
            if (node is not null) return (node, 1);
        }

        // 级别2：仅 ControlType 兜底（树内取第一个同类型节点）
        if (elementControlType is not null)
        {
            node = Find(nodes, null, null, elementControlType);
            if (node is not null) return (node, 2);
        }

        return (null, -1);
    }

    private static ElementNode? Find(IEnumerable<ElementNode> nodes, string? name, string? autoId, string? type)
    {
        foreach (var n in nodes)
        {
            if (name is not null && n.Name != name) continue;
            if (autoId is not null && n.AutomationId != autoId) continue;
            if (type is not null && n.ControlType != type) continue;
            return n;
        }
        return null;
    }
}
