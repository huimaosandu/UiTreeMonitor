// 数据模型：结构树节点快照与属性
using System;
using System.Collections.Generic;

namespace UiTreeMonitor.Core.Models;

/// <summary>可监控/可采集的 UIA 属性（Axe.Windows A11yElement 的属性映射）。</summary>
public static class UiProperties
{
    // A11yElement 直接属性
    public const string Name = "Name";
    public const string ControlType = "ControlType";
    public const string AutomationId = "AutomationId";
    public const string ClassName = "ClassName";
    public const string IsEnabled = "IsEnabled";
    public const string IsOffScreen = "IsOffScreen";
    public const string BoundingRectangle = "BoundingRectangle";
    public const string Framework = "Framework";
    public const string ProcessId = "ProcessId";
    public const string ItemStatus = "ItemStatus";
    public const string HelpText = "HelpText";
    public const string AccessKey = "AccessKey";

    // 需要 Pattern 的附加属性
    public const string Value = "Value";                 // ValuePattern
    public const string RangeValue = "RangeValue";       // RangeValuePattern
    public const string ToggleState = "ToggleState";     // TogglePattern
    public const string IsSelected = "IsSelected";       // SelectionItemPattern
    public const string IsExpandCollapsed = "ExpandCollapseState"; // ExpandCollapsePattern

    /// <summary>全部可采集属性清单（供 UI 勾选）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Name, ControlType, AutomationId, ClassName, IsEnabled, IsOffScreen,
        BoundingRectangle, Framework, ProcessId, ItemStatus, HelpText, AccessKey,
        Value, RangeValue, ToggleState, IsSelected, IsExpandCollapsed,
    };
}

/// <summary>单个 Pattern 的原始数据（从 el.Patterns 提取，用于 Details 面板显示）。</summary>
public sealed record PatternData(string Name, string DisplayValue, IReadOnlyList<PatternPropData> SubProps);

/// <summary>Pattern 的子属性。</summary>
public sealed record PatternPropData(string Name, string Value);

/// <summary>结构树的一个节点快照（采集结果）。</summary>
public sealed record ElementNode
{
    public int Id { get; init; }                 // 树内唯一 id
    public int ParentId { get; init; } = -1;     // -1 = 根
    public int Depth { get; init; }
    public string? Name { get; init; }
    public string ControlType { get; init; } = "";
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public string? Framework { get; init; }
    public int ProcessId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsOffScreen { get; init; }
    public string? Value { get; init; }          // ValuePattern 当前值（便捷访问）
    public string? RangeValue { get; init; }     // RangeValuePattern 当前值
    public string? ToggleState { get; init; }
    public string? BoundingRectangle { get; init; }
    public string? ItemStatus { get; init; }
    public string? HelpText { get; init; }
    public string? AccessKey { get; init; }
    public string? IsSelected { get; init; }
    public string? ExpandCollapseState { get; init; }
    /// <summary>该节点支持的 Pattern 完整数据（原版 el.Patterns → NodeValue 同款格式）。</summary>
    public IReadOnlyList<PatternData> PatternDataList { get; init; } = Array.Empty<PatternData>();
    /// <summary>该节点支持的 Pattern 名称列表（即使当前值为空也列入，保留兼容）。</summary>
    public IReadOnlyList<string> SupportedPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> Children { get; init; } = Array.Empty<int>();

    /// <summary>按属性名取值（供监控与过滤统一使用）。支持 "PatternName.PropName" 带点路径（如 ValuePattern.Value）。</summary>
    public string? GetProperty(string name)
    {
        // 带点路径：查 PatternDataList（TreeSampler 存的 pd.Name 如 "ValuePattern"，子属性如 "Value"）
        if (name.Contains('.'))
        {
            var dot = name.IndexOf('.');
            var patName = name[..dot];
            var propName = name[(dot + 1)..];
            foreach (var pd in PatternDataList)
            {
                if (!string.Equals(pd.Name, patName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var sp in pd.SubProps)
                    if (sp.Name == propName) return sp.Value;
            }
            return null;
        }
        return name switch
    {
        UiProperties.Name => Name,
        UiProperties.ControlType => ControlType,
        UiProperties.AutomationId => AutomationId,
        UiProperties.ClassName => ClassName,
        UiProperties.Framework => Framework,
        UiProperties.ProcessId => ProcessId.ToString(),
        UiProperties.IsEnabled => IsEnabled.ToString(),
        UiProperties.IsOffScreen => IsOffScreen.ToString(),
        UiProperties.Value => Value,
        UiProperties.RangeValue => RangeValue,
        UiProperties.ToggleState => ToggleState,
        UiProperties.BoundingRectangle => BoundingRectangle,
        UiProperties.ItemStatus => ItemStatus,
        UiProperties.HelpText => HelpText,
        UiProperties.AccessKey => AccessKey,
        UiProperties.IsSelected => IsSelected,
        UiProperties.IsExpandCollapsed => ExpandCollapseState,
        _ => null,
        };
    }
}

/// <summary>一次完整树采集的结果。</summary>
public sealed class TreeSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int RootId { get; init; }
    public IReadOnlyDictionary<int, ElementNode> Nodes { get; init; } = new Dictionary<int, ElementNode>();
    public IReadOnlyList<int> Order { get; init; } = Array.Empty<int>(); // 前序顺序
    public int NodeCount => Nodes.Count;
    public TargetWindowInfo? Target { get; init; }
}

public sealed record TargetWindowInfo(int ProcessId, IntPtr Handle, string ProcessName, string Title);
