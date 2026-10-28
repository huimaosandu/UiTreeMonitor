// 树节点视图模型 + Details 面板数据项
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.App;

/// <summary>Details 面板的单个属性条目（Properties 列表用）。</summary>
public sealed class PropertyItem
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string FullValue { get; init; } = ""; // 完整值（ToolTip 用，长文本截断时保留原文）
}

/// <summary>Details 面板的单个 Pattern 条目（Patterns 树用）。</summary>
public sealed class PatternItem
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public IReadOnlyList<PropertyItem> SubProperties { get; init; } = Array.Empty<PropertyItem>();
}

/// <summary>结构树节点 VM（用于 TreeView 显示 + 高亮 + Details 面板）。</summary>
public sealed class TreeNodeViewModel
{
    public int Id { get; init; }
    public string Display => $"[{ControlType}] {Name ?? AutomationId ?? ClassName ?? "(无名称)"}";
    public string? Name { get; init; }
    public string ControlType { get; init; } = "";
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public int ProcessId { get; init; }
    public string? ProcessName { get; init; } // Details 面板右键添加目标时用
    public bool IsHighlighted { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    /// <summary>Details 面板：全部 UIA 属性列表。</summary>
    public IReadOnlyList<PropertyItem> Properties { get; init; } = Array.Empty<PropertyItem>();

    /// <summary>Details 面板：Pattern 列表（有值的 pattern 才出现）。</summary>
    public IReadOnlyList<PatternItem> Patterns { get; init; } = Array.Empty<PatternItem>();

    public static TreeNodeViewModel FromNode(ElementNode node, string? processName = null)
    {
        var props = BuildProperties(node);
        var pats = BuildPatterns(node);
        return new()
        {
            Id = node.Id,
            Name = node.Name,
            ControlType = node.ControlType,
            AutomationId = node.AutomationId,
            ClassName = node.ClassName,
            ProcessId = node.ProcessId,
            ProcessName = processName,
            Properties = props,
            Patterns = pats,
        };
    }

    /// <summary>从 ElementNode 构建属性列表（按 UiProperties.All 顺序）。</summary>
    private static IReadOnlyList<PropertyItem> BuildProperties(ElementNode node)
    {
        var list = new List<PropertyItem>();
        foreach (var propName in UiProperties.All)
        {
            var val = node.GetProperty(propName);
            if (val is not null)
            {
                var display = val.Length > 120 ? val.Substring(0, 117) + "..." : val;
                list.Add(new PropertyItem { Name = propName, Value = display, FullValue = val });
            }
        }
        return list;
    }

    /// <summary>从 ElementNode 构建 Pattern 列表（只包含有值的 pattern）。</summary>
    private static IReadOnlyList<PatternItem> BuildPatterns(ElementNode node)
    {
        var list = new List<PatternItem>();

        if (node.Value is not null)
            list.Add(new PatternItem { Name = "ValuePattern", Value = node.Value });

        if (node.RangeValue is not null)
            list.Add(new PatternItem { Name = "RangeValuePattern", Value = node.RangeValue });

        if (node.ToggleState is not null)
            list.Add(new PatternItem { Name = "TogglePattern", Value = node.ToggleState });

        if (node.IsSelected is not null)
            list.Add(new PatternItem { Name = "SelectionItemPattern", Value = node.IsSelected });

        if (node.ExpandCollapseState is not null)
            list.Add(new PatternItem { Name = "ExpandCollapsePattern", Value = node.ExpandCollapseState });

        return list;
    }
}
