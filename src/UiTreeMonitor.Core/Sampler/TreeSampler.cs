// 树采集：用 UiaSession + Axe.Windows A11yElement 遍历，转成 ElementNode 快照
using System;
using System.Collections.Generic;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.Core.Sampler;

/// <summary>从 UIA 窗口抓取结构树并转成快照。</summary>
public sealed class TreeSampler
{
    // 常见 UIA ControlTypeId → 可读名称（参考 UIAutomationClient 常量）
    private static readonly IReadOnlyDictionary<int, string> ControlTypes = new Dictionary<int, string>
    {
        [50000] = "Button", [50001] = "Calendar", [50002] = "CheckBox", [50003] = "ComboBox",
        [50004] = "Edit", [50005] = "Hyperlink", [50006] = "Image", [50007] = "ListItem",
        [50008] = "List", [50009] = "Menu", [50010] = "MenuBar", [50011] = "MenuItem",
        [50012] = "ProgressBar", [50013] = "RadioButton", [50014] = "ScrollBar", [50015] = "Slider",
        [50016] = "Spinner", [50017] = "StatusBar", [50018] = "Tab", [50019] = "TabItem",
        [50020] = "Text", [50021] = "ToolBar", [50022] = "ToolTip", [50023] = "Tree",
        [50024] = "TreeItem", [50025] = "Custom", [50026] = "Group", [50027] = "Thumb",
        [50028] = "DataGrid", [50029] = "DataItem", [50030] = "Document", [50031] = "SplitButton",
        [50032] = "Window", [50033] = "Pane", [50034] = "Header", [50035] = "HeaderItem",
        [50036] = "Table", [50037] = "TitleBar", [50038] = "Separator", [50039] = "SemanticZoom",
        [50040] = "AppBar",
    };

    public static string ControlTypeName(int controlTypeId) =>
        ControlTypes.TryGetValue(controlTypeId, out var n) ? n : $"UIA_{controlTypeId}";

    /// <summary>从窗口句柄抓取整棵树（STA 线程内调用）。</summary>
    public TreeSnapshot CaptureWindow(UiaSession session, TargetWindowInfo target, int maxDepth = 64, int maxNodes = 5000)
    {
        var root = session.GetRootFromHandle(target.Handle);
        if (root is null)
            return new TreeSnapshot { RootId = -1, Nodes = new Dictionary<int, ElementNode>(), Order = Array.Empty<int>(), Target = target };

        var nodes = new Dictionary<int, ElementNode>();
        var order = new List<int>();
        int idCounter = 0;

        int Walk(A11yElement el, int parentId, int depth)
        {
            if (depth > maxDepth || nodes.Count >= maxNodes) return -1;

            int id = ++idCounter;
            var childIds = new List<int>();

            // 遍历子树：第一个子元素 → 兄弟链
            var child = session.GetFirstChild(el);
            while (child is not null)
            {
                if (nodes.Count >= maxNodes) break;
                int childId = Walk(child, id, depth + 1);
                if (childId >= 0) childIds.Add(childId);
                child = session.GetNextSibling(child);
            }

            var node = BuildNode(id, parentId, depth, el);
            nodes[id] = node with { Children = childIds };
            order.Add(id);
            return id;
        }

        int rootId = Walk(root, -1, 0);
        return new TreeSnapshot { RootId = rootId, Nodes = nodes, Order = order, Target = target };
    }

    private static ElementNode BuildNode(int id, int parentId, int depth, A11yElement el)
    {
        string? rectStr = null;
        try
        {
            var rect = el.BoundingRectangle;
            rectStr = $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
        }
        catch { /* 矩形可能无效 */ }

        // ★ 用 el.Patterns 直接获取 Pattern 数据（原版 PatternInfoControl 同款方式）
        // A11yPattern 结构: Id, Name, Properties(IList<A11yPatternProperty>: Name, Value)
        var patternNames = new List<string>();
        var patternValues = new Dictionary<string, string?>(); // key = "PatternName.PropertyName"

        try
        {
            if (el.Patterns is not null)
                foreach (var p in el.Patterns)
                {
                    patternNames.Add(p.Name);
                    if (p.Properties is not null)
                        foreach (var pp in p.Properties)
                            patternValues[$"{p.Name}.{pp.Name}"] = pp.Value;
                    else
                        patternValues[p.Name] = "(no properties)";
                }
        }
        catch { /* Patterns 可能为空或异常 */ }

        // 从 patternValues 中提取已知 Pattern 的主属性值
        string? GetVal(string patName, string propName) =>
            patternValues.TryGetValue($"{patName}.{propName}", out var v) ? v : null;

        return new ElementNode
        {
            Id = id,
            ParentId = parentId,
            Depth = depth,
            Name = Safe(el.Name),
            ControlType = ControlTypeName(el.ControlTypeId),
            AutomationId = Safe(el.AutomationId),
            ClassName = Safe(el.ClassName),
            Framework = Safe(el.Framework),
            ProcessId = el.ProcessId,
            IsEnabled = el.IsEnabled,
            IsOffScreen = el.IsOffScreen,
            Value = GetVal("ValuePattern", "Value"),
            RangeValue = GetVal("RangeValuePattern", "Value"),
            ToggleState = GetVal("TogglePattern", "ToggleState"),
            BoundingRectangle = rectStr,
            ItemStatus = Safe(el.ItemStatus),
            HelpText = Safe(el.HelpText),
            AccessKey = Safe(el.AccessKey),
            IsSelected = GetVal("SelectionItemPattern", "IsSelected"),
            ExpandCollapseState = GetVal("ExpandCollapsePattern", "ExpandCollapseState"),
            SupportedPatterns = patternNames,
            Children = Array.Empty<int>(),
        };
    }

    private static string? Safe(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
