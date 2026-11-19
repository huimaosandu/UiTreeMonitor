// 树节点视图模型 + Details 面板数据项
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Axe.Windows.Core.Bases;
using Axe.Windows.Desktop.UIAutomation;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Targeting;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App;

/// <summary>UIA 文本压成单行：Name 可能是整篇文档（Notepad++ 的正文 Pane 就是全文，
/// 自带上千个换行），原样塞进 TextBlock 会被渲染成上千行，把所在的 Auto 行撑到几千像素、
/// 整个窗口布局被挤成这样。所有"要显示给人看"的 UIA 文本都过这里。</summary>
public static class TextFit
{
    public static string OneLine(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Regex.Replace(text, @"[\r\n\t\f\v]+", " ");
        s = Regex.Replace(s, @" {2,}", " ").Trim();
        return s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
    }
}

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

    /// <summary>该 Pattern 是否有可执行的 Actions（仅 FromA11yElement 场景可检测）。</summary>
    public bool HasActions { get; init; }

    /// <summary>该 Pattern 在 el.Patterns 列表中的索引（用于 Actions 执行时定位）。</summary>
    public int PatternIndex { get; init; }
}

/// <summary>结构树节点 VM（用于 TreeView 显示 + 高亮 + Details 面板）。
/// 实现 INPC：IsHighlighted 变化时经 XAML DataTrigger 刷新节点背景（鼠标捕获实时高亮）。</summary>
public sealed class TreeNodeViewModel : INotifyPropertyChanged
{
    public int Id { get; init; }
    /// <summary>树行显示文本：压成单行并截断（见 TextFit.OneLine 的说明），否则整篇文档会撑坏布局。</summary>
    public string Display => $"[{ControlType}] {TextFit.OneLine(BaseLabel)}";
    /// <summary>Name/AutomationId/ClassName 里第一个非空的，作为节点主标识。</summary>
    public string BaseLabel => !string.IsNullOrEmpty(Name) ? Name!
        : !string.IsNullOrEmpty(AutomationId) ? AutomationId!
        : !string.IsNullOrEmpty(ClassName) ? ClassName! : "(无名称)";
    public string? Name { get; init; }
    public string ControlType { get; init; } = "";
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public int ProcessId { get; init; }
    public string? ProcessName { get; init; } // Details 面板右键添加目标时用

    /// <summary>UIA RuntimeId（"a.b.c"）与屏幕矩形（"L,T,R,B"）：树选中节点时用来实时重取该元素，
    /// 补全 Pattern / 让 Actions 可用（快速抓树快照本身不含 Pattern）。</summary>
    public string? RuntimeId { get; init; }
    public string? BoundingRectangle { get; init; }

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(); } }
    }

    private bool _isMatched;
    /// <summary>关键词检索命中（XAML DataTrigger 画浅绿底色，与鼠标捕获的黄色高亮区分）。</summary>
    public bool IsMatched
    {
        get => _isMatched;
        set { if (_isMatched != value) { _isMatched = value; OnPropertyChanged(); } }
    }

    private bool _isExpanded;
    /// <summary>是否展开（TreeViewItem.IsExpanded 双向绑定到此）。
    /// 抓树后默认展开前两层走这里赋值，不依赖 TreeViewItem 容器是否已生成。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    /// <summary>检索用的小写拼接串（Name/AutomationId/ClassName/ControlType），构建时算一次，避免每次输入都重拼。</summary>
    public string SearchKey { get; init; } = "";

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
            RuntimeId = node.RuntimeId,
            BoundingRectangle = node.BoundingRectangle,
            Properties = props,
            Patterns = pats,
            SearchKey = $"{node.Name} {node.AutomationId} {node.ClassName} {node.ControlType}".ToLowerInvariant(),
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
                // 先压单行再截断：DataGrid 行高固定，但带换行的单元格仍会撑出纵向内容
                var one = TextFit.OneLine(val, 400);
                var display = one.Length > 120 ? string.Concat(one.AsSpan(0, 117), "...") : one;
                list.Add(new PropertyItem { Name = propName, Value = display, FullValue = one });
            }
        }
        return list;
    }

    /// <summary>
    /// 统一的 Pattern 子属性行文本："属性名 = 值"。
    /// Axe.Windows 的 NodeValue 本身常常已经是 "HorizontalScrollable = False" 这种形式，
    /// 不补判会显示成「HorizontalScrollable　HorizontalScrollable = False」的重复（原版截图里没重复）。
    /// </summary>
    private static PropertyItem MakeSubProperty(string name, string? raw)
    {
        var v = TextFit.OneLine(raw ?? "(null)", 300);
        if (!v.StartsWith(name + " = ", StringComparison.Ordinal)) v = $"{name} = {v}";
        return new PropertyItem
        {
            Name = name,
            Value = v.Length > 120 ? string.Concat(v.AsSpan(0, 117), "...") : v,
            FullValue = v,
        };
    }

    /// <summary>从 ElementNode 构建 Pattern 列表（使用 PatternDataList，与 FromA11yElement 统一格式）。</summary>
    private static IReadOnlyList<PatternItem> BuildPatterns(ElementNode node)
    {
        var list = new List<PatternItem>();

        // 统一使用 PatternDataList（与 FromA11yElement 同款数据源和格式）
        foreach (var pd in node.PatternDataList)
        {
            var subProps = pd.SubProps.Select(sp => MakeSubProperty(sp.Name, sp.Value)).ToList();
            list.Add(new PatternItem { Name = pd.Name, Value = TextFit.OneLine(pd.DisplayValue, 160), SubProperties = subProps,
                HasActions = false, PatternIndex = list.Count }); // 快照无 A11yPattern 引用，不支持 Actions
        }

        return list;
    }

    /// <summary>
    /// 从 A11yElement 直接构建 Details 数据（用于鼠标捕获场景，无 ElementNode）。
    /// 原版 PatternInfoControl/PatternViewModel 同款方式：el.Patterns → A11yPattern.Properties → NodeValue
    /// </summary>
    public static (IReadOnlyList<PropertyItem> Properties, IReadOnlyList<PatternItem> Patterns) FromA11yElement(
        A11yElement el, string? processName, MouseCaptureResult? cap = null)
    {
        // ★ 关键：必须调用此方法从 UIA 获取实时 Pattern 数据填充到 el.Patterns
        // 不调用的话 el.Patterns 永远为 null/空 → Details 面板 Patterns 区域空白
        try { el.PopulateAllPropertiesWithLiveData(); } catch { /* 某些元素可能不支持 */ }

        var props = new List<PropertyItem>();
        var pats = new List<PatternItem>();
        var hasValue = new HashSet<string>();

        // === Properties（A11yElement 直接属性）===
        // 捕获场景直接用捕获结果里已读好的值，避免在 UI 线程上重复 COM 往返（每次 ~4ms）
        var propMap = new (string Name, Func<string?> Getter)[]
        {
            ("Name", () => cap?.Name ?? TreeSampler.ReadName(el)),
            ("ControlType", () => cap?.ControlType ?? TreeSampler.ControlTypeName(TreeSampler.ReadControlTypeId(el))),
            ("AutomationId", () => cap?.AutomationId ?? TreeSampler.ReadAutomationId(el)),
            ("ClassName", () => cap?.ClassName ?? TreeSampler.ReadClassName(el)),
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
                    var one = TextFit.OneLine(val, 400);
                    var display = one.Length > 120 ? string.Concat(one.AsSpan(0, 117), "...") : one;
                    props.Add(new PropertyItem { Name = name, Value = display, FullValue = one });
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
            {
                int patIdx = 0;
                foreach (var p in el.Patterns)
                {
                    var hasActions = PatternActionRunner.HasActions(p);
                    if (p.Properties is not null && p.Properties.Count > 0)
                    {
                        // 有属性值：显示每个子属性（统一成 "属性名 = 值"，见 MakeSubProperty）
                        var subProps = new List<PropertyItem>();
                        foreach (var pp in p.Properties)
                            subProps.Add(MakeSubProperty(pp.Name, pp.NodeValue));
                        // 用第一个属性的值作为 Pattern 的主显示值（原版 PatternInfoControl 同款）
                        var mainVal = subProps.Count > 0 ? subProps[0].Value : "(null)";
                        pats.Add(new PatternItem { Name = p.Name, Value = mainVal, SubProperties = subProps,
                            HasActions = hasActions, PatternIndex = patIdx });
                    }
                    else
                    {
                        pats.Add(new PatternItem { Name = p.Name, Value = "(supported, no current value)",
                            HasActions = hasActions, PatternIndex = patIdx });
                    }
                    patIdx++;
                    hasValue.Add(p.Name);
                }
            }
        }
        catch { /* Patterns 读取异常 */ }

        return (props, pats);
    }
}
