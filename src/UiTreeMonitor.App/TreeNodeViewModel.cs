// 树节点视图模型 + Details 面板数据项
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Axe.Windows.Core.Bases;
using Axe.Windows.Desktop.UIAutomation;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;

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

    /// <summary>从 ElementNode 构建 Pattern 列表（直接来自 el.Patterns 数据）。</summary>
    private static IReadOnlyList<PatternItem> BuildPatterns(ElementNode node)
    {
        var list = new List<PatternItem>();
        var hasValue = new HashSet<string>();

        // 有具体值的 Pattern（高优先级）——来自 el.Patterns 的 Properties
        if (!string.IsNullOrEmpty(node.Value)) { list.Add(new PatternItem { Name = "ValuePattern", Value = node.Value }); hasValue.Add("ValuePattern"); }
        if (!string.IsNullOrEmpty(node.RangeValue)) { list.Add(new PatternItem { Name = "RangeValuePattern", Value = node.RangeValue }); hasValue.Add("RangeValuePattern"); }
        if (!string.IsNullOrEmpty(node.ToggleState)) { list.Add(new PatternItem { Name = "TogglePattern", Value = node.ToggleState }); hasValue.Add("TogglePattern"); }
        if (!string.IsNullOrEmpty(node.IsSelected)) { list.Add(new PatternItem { Name = "SelectionItemPattern", Value = node.IsSelected }); hasValue.Add("SelectionItemPattern"); }
        if (!string.IsNullOrEmpty(node.ExpandCollapseState)) { list.Add(new PatternItem { Name = "ExpandCollapsePattern", Value = node.ExpandCollapseState }); hasValue.Add("ExpandCollapsePattern"); }

        // 支持但当前无值的 Pattern
        foreach (var sp in node.SupportedPatterns)
        {
            if (!hasValue.Contains(sp))
                list.Add(new PatternItem { Name = sp, Value = "(supported, no current value)" });
        }

        return list;
    }

    /// <summary>
    /// 从 A11yElement 直接构建 Details 数据（用于鼠标捕获场景，无 ElementNode）。
    /// 原版 PatternInfoControl/PatternViewModel 同款方式：el.Patterns → A11yPattern.Properties → NodeValue
    /// </summary>
    public static (IReadOnlyList<PropertyItem> Properties, IReadOnlyList<PatternItem> Patterns) FromA11yElement(A11yElement el, string? processName)
    {
        // ★ 关键：必须调用此方法从 UIA 获取实时 Pattern 数据填充到 el.Patterns
        // 不调用的话 el.Patterns 永远为 null/空 → Details 面板 Patterns 区域空白
        try { el.PopulateAllPropertiesWithLiveData(); } catch { /* 某些元素可能不支持 */ }

        var props = new List<PropertyItem>();
        var pats = new List<PatternItem>();
        var hasValue = new HashSet<string>();

        // === Properties（A11yElement 直接属性）===
        var propMap = new (string Name, Func<string?> Getter)[]
        {
            ("Name", () => el.Name),
            ("ControlType", () => TreeSampler.ControlTypeName(el.ControlTypeId)),
            ("AutomationId", () => el.AutomationId),
            ("ClassName", () => el.ClassName),
            ("Framework", () => el.Framework),
            ("ProcessId", () => el.ProcessId.ToString()),
            ("ProcessName", () => processName),
            ("IsEnabled", () => el.IsEnabled.ToString()),
            ("IsOffScreen", () => el.IsOffScreen.ToString()),
            ("HelpText", () => el.HelpText),
            ("AccessKey", () => el.AccessKey),
            ("ItemStatus", () => el.ItemStatus),
        };
        foreach (var (name, getter) in propMap)
        {
            try
            {
                var val = getter();
                if (!string.IsNullOrEmpty(val))
                {
                    var display = val.Length > 120 ? val.Substring(0, 117) + "..." : val;
                    props.Add(new PropertyItem { Name = name, Value = display, FullValue = val });
                }
            }
            catch { /* 某些属性可能抛异常 */ }
        }

        // BoundingRectangle
        try { var r = el.BoundingRectangle; props.Add(new PropertyItem { Name = "BoundingRectangle", Value = $"{r.Left},{r.Top},{r.Right},{r.Bottom}", FullValue = $"{r.Left},{r.Top},{r.Right},{r.Bottom}" }); } catch { }

        // === Patterns（原版 PatternViewModel 同款：遍历 el.Patterns）===
        try
        {
            if (el.Patterns is not null)
                foreach (var p in el.Patterns)
                {
                    if (p.Properties is not null && p.Properties.Count > 0)
                    {
                        // 有属性值：显示每个子属性
                        var subProps = new List<PropertyItem>();
                        foreach (var pp in p.Properties)
                        {
                            var nodeVal = pp.NodeValue ?? "(null)";
                            var display = nodeVal.Length > 120 ? nodeVal.Substring(0, 117) + "..." : nodeVal;
                            subProps.Add(new PropertyItem { Name = pp.Name, Value = display, FullValue = nodeVal });
                        }
                        // 用第一个属性的值作为 Pattern 的主显示值
                        var mainVal = p.Properties[0].NodeValue ?? "(null)";
                        pats.Add(new PatternItem { Name = p.Name, Value = mainVal, SubProperties = subProps });
                        hasValue.Add(p.Name);
                    }
                    else
                    {
                        pats.Add(new PatternItem { Name = p.Name, Value = "(supported, no current value)" });
                        hasValue.Add(p.Name);
                    }
                }
        }
        catch { /* Patterns 读取异常 */ }

        return (props, pats);
    }
}
