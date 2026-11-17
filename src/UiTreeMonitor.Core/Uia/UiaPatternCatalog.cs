// UIA Pattern 目录：全量 pattern 清单 + "点击"语义可执行动作的优先级表
//
// ★ 为什么 id 是手写的常量而不是引用 UIAutomationClient.UIA_PatternIds：
//   本项目以 EmbedInteropTypes=True 引用 lib\Interop.UIAutomationClient.dll，该模式下
//   只能嵌入接口，不能嵌入静态类（UIA_PatternIds），引用即 CS1752。
//
// ★ 为什么手写也不怕出错：下面这 32 个值是用反射从 Interop.UIAutomationClient.dll 的
//   UIAutomationClient.UIA_PatternIds 里逐个读出来的（GetRawConstantValue），并已核对顺序：
//     10000 Invoke          10001 Selection       10002 Value            10003 RangeValue
//     10004 Scroll          10005 ExpandCollapse  10006 Grid             10007 GridItem
//     10008 MultipleView    10009 Window          10010 SelectionItem    10011 Dock
//     10012 Table           10013 TableItem       10014 Text             10015 Toggle
//     10016 Transform       10017 ScrollItem      10018 LegacyIAccessible 10019 ItemContainer
//     10020 VirtualizedItem 10021 SynchronizedInput 10022 ObjectModel     10023 Annotation
//     10025 Styles          10026 Spreadsheet     10027 SpreadsheetItem  10029 TextChild
//     10030 Drag            10031 DropTarget      10032 TextEdit         10033 CustomNavigation
//   （注意：10002 是 ValuePatternId、10005 才是 ExpandCollapsePatternId；
//     曾把 10002 当 ExpandCollapse 写死，导致"只有 ExpandCollapse 的菜单项"回放分支永远命中不到。）
using System;
using System.Collections.Generic;
using Axe.Windows.Core.Bases;
using Axe.Windows.Desktop.UIAutomation;
using UIAutomationClient;

namespace UiTreeMonitor.Core.Uia;

/// <summary>UIA Pattern id 常量（值来自 Interop.UIAutomationClient 的 UIA_PatternIds，见文件头对照表）。</summary>
public static class PatternIds
{
    public const int Invoke = 10000;
    public const int Selection = 10001;
    public const int Value = 10002;
    public const int RangeValue = 10003;
    public const int Scroll = 10004;
    public const int ExpandCollapse = 10005;
    public const int Grid = 10006;
    public const int GridItem = 10007;
    public const int MultipleView = 10008;
    public const int Window = 10009;
    public const int SelectionItem = 10010;
    public const int Dock = 10011;
    public const int Table = 10012;
    public const int TableItem = 10013;
    public const int Text = 10014;
    public const int Toggle = 10015;
    public const int Transform = 10016;
    public const int ScrollItem = 10017;
    public const int LegacyIAccessible = 10018;
    public const int ItemContainer = 10019;
    public const int VirtualizedItem = 10020;
    public const int SynchronizedInput = 10021;
    public const int ObjectModel = 10022;
    public const int Annotation = 10023;
    public const int Styles = 10025;
    public const int Spreadsheet = 10026;
    public const int SpreadsheetItem = 10027;
    public const int TextChild = 10029;
    public const int Drag = 10030;
    public const int DropTarget = 10031;
    public const int TextEdit = 10032;
    public const int CustomNavigation = 10033;
}

public static class UiaPatternCatalog
{
    /// <summary>点击语义动作种类（决定录制端要不要补读状态、回放端调用哪个接口方法）。</summary>
    public enum ClickKind
    {
        Invoke,
        DoDefaultAction,
        Toggle,
        Select,
        ExpandCollapse,
    }

    /// <summary>一个可执行"点击语义"的 Pattern 动作。</summary>
    /// <param name="PatternId">UIA Pattern id（取自 PatternIds）</param>
    /// <param name="PatternName">脚本里记录的 Pattern 名（Axe.Windows 的 A11yPattern.Name 同款）</param>
    /// <param name="MethodName">默认方法名（ExpandCollapse 会按状态改写成 Expand / Collapse）</param>
    /// <param name="Kind">动作种类</param>
    public sealed record ClickAction(int PatternId, string PatternName, string MethodName, ClickKind Kind);

    /// <summary>
    /// 点击语义可执行的动作，按优先级从高到低。
    /// UIA 里其余 Pattern（Value / RangeValue / Scroll / Text / Grid / Window / Transform …）
    /// 没有"点击"这个语义（它们只能读值、滚动、定位），所以不进这张表；
    /// 但它们仍会被 SupportedNames() 列入"元素支持的能力清单"，用于诊断与脚本记录。
    /// </summary>
    public static readonly IReadOnlyList<ClickAction> ClickActions = new[]
    {
        new ClickAction(PatternIds.Invoke, "InvokePattern", "Invoke", ClickKind.Invoke),
        new ClickAction(PatternIds.LegacyIAccessible, "LegacyIAccessiblePattern", "DoDefaultAction", ClickKind.DoDefaultAction),
        new ClickAction(PatternIds.Toggle, "TogglePattern", "Toggle", ClickKind.Toggle),
        new ClickAction(PatternIds.SelectionItem, "SelectionItemPattern", "Select", ClickKind.Select),
        new ClickAction(PatternIds.ExpandCollapse, "ExpandCollapsePattern", "Expand", ClickKind.ExpandCollapse),
    };

    /// <summary>UIA 全部 Pattern（id + 名称）。UIA 没有"查询支持哪些 pattern"的 API，
    /// 客户端只能拿一张全表逐个试（WPF 的 GetSupportedPatterns / Axe.Windows 的 Populate 都是这么做的）。</summary>
    public static readonly IReadOnlyList<KeyValuePair<int, string>> All = new[]
    {
        new KeyValuePair<int, string>(PatternIds.Invoke, "InvokePattern"),
        new KeyValuePair<int, string>(PatternIds.Selection, "SelectionPattern"),
        new KeyValuePair<int, string>(PatternIds.Value, "ValuePattern"),
        new KeyValuePair<int, string>(PatternIds.RangeValue, "RangeValuePattern"),
        new KeyValuePair<int, string>(PatternIds.Scroll, "ScrollPattern"),
        new KeyValuePair<int, string>(PatternIds.ExpandCollapse, "ExpandCollapsePattern"),
        new KeyValuePair<int, string>(PatternIds.Grid, "GridPattern"),
        new KeyValuePair<int, string>(PatternIds.GridItem, "GridItemPattern"),
        new KeyValuePair<int, string>(PatternIds.MultipleView, "MultipleViewPattern"),
        new KeyValuePair<int, string>(PatternIds.Window, "WindowPattern"),
        new KeyValuePair<int, string>(PatternIds.SelectionItem, "SelectionItemPattern"),
        new KeyValuePair<int, string>(PatternIds.Dock, "DockPattern"),
        new KeyValuePair<int, string>(PatternIds.Table, "TablePattern"),
        new KeyValuePair<int, string>(PatternIds.TableItem, "TableItemPattern"),
        new KeyValuePair<int, string>(PatternIds.Text, "TextPattern"),
        new KeyValuePair<int, string>(PatternIds.Toggle, "TogglePattern"),
        new KeyValuePair<int, string>(PatternIds.Transform, "TransformPattern"),
        new KeyValuePair<int, string>(PatternIds.ScrollItem, "ScrollItemPattern"),
        new KeyValuePair<int, string>(PatternIds.LegacyIAccessible, "LegacyIAccessiblePattern"),
        new KeyValuePair<int, string>(PatternIds.ItemContainer, "ItemContainerPattern"),
        new KeyValuePair<int, string>(PatternIds.VirtualizedItem, "VirtualizedItemPattern"),
        new KeyValuePair<int, string>(PatternIds.SynchronizedInput, "SynchronizedInputPattern"),
        new KeyValuePair<int, string>(PatternIds.ObjectModel, "ObjectModelPattern"),
        new KeyValuePair<int, string>(PatternIds.Annotation, "AnnotationPattern"),
        new KeyValuePair<int, string>(PatternIds.Styles, "StylesPattern"),
        new KeyValuePair<int, string>(PatternIds.Spreadsheet, "SpreadsheetPattern"),
        new KeyValuePair<int, string>(PatternIds.SpreadsheetItem, "SpreadsheetItemPattern"),
        new KeyValuePair<int, string>(PatternIds.TextChild, "TextChildPattern"),
        new KeyValuePair<int, string>(PatternIds.Drag, "DragPattern"),
        new KeyValuePair<int, string>(PatternIds.DropTarget, "DropTargetPattern"),
        new KeyValuePair<int, string>(PatternIds.TextEdit, "TextEditPattern"),
        new KeyValuePair<int, string>(PatternIds.CustomNavigation, "CustomNavigationPattern"),
    };

    /// <summary>
    /// 该元素**实际支持**的 Pattern 名单（全表逐个探测）。
    /// 只在诊断/失败报告时调用（要包装一次 A11yElement 并填充，约 10ms）。
    /// </summary>
    public static IReadOnlyList<string> SupportedNames(IUIAutomationElement el)
    {
        var names = new List<string>();
        if (el is null) return names;
        try
        {
            var element = new Axe.Windows.Desktop.UIAutomation.DesktopElement(el, keepElement: true, setMembers: true);
            try { element.PopulateAllPropertiesWithLiveData(); } catch { /* 部分元素不支持 */ }
            if (element.Patterns is not null)
                foreach (var p in element.Patterns)
                {
                    var n = p?.Name;
                    if (!string.IsNullOrEmpty(n)) names.Add(n!);
                }
        }
        catch { /* 元素已销毁 */ }
        return names;
    }
}
