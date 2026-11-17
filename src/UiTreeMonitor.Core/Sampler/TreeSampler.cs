// 树采集：用 UiaSession + Axe.Windows A11yElement 遍历，转成 ElementNode 快照
using System;
using System.Collections.Generic;
using Axe.Windows.Core.Bases;
using Axe.Windows.Desktop.UIAutomation;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Uia;
using UIAutomationClient;

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

    /// <summary>从窗口句柄抓取整棵树（STA 线程内调用）。
    /// collectPatterns=false 时跳过 Populate + Pattern 采集：抓树快一倍，用于鼠标捕获跟随
    /// （Pattern 值只为 Details 快照显示，实时捕获场景已有 A11yElement 可实时查）。</summary>
    public TreeSnapshot CaptureWindow(UiaSession session, TargetWindowInfo target, int maxDepth = 64, int maxNodes = 5000, bool collectPatterns = true)
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

            var node = BuildNode(id, parentId, depth, el, collectPatterns);
            nodes[id] = node with { Children = childIds };
            order.Add(id);
            return id;
        }

        int rootId = Walk(root, -1, 0);
        return new TreeSnapshot { RootId = rootId, Nodes = nodes, Order = order, Target = target };
    }

    /// <summary>
    /// 快速抓树（鼠标捕获跟随用）：直接用 UIA COM walker 遍历，不做 Axe.Windows 的 DesktopElement 包装。
    /// 实测 msedge 190 节点：包装版 ~1990ms，本方法 ~120ms（28 倍）——
    /// 瓶颈就在 `new DesktopElement(..., setMembers: true)` 每节点都要抓一遍成员集合。
    /// 代价：快照里没有 Pattern 数据（Value/RangeValue/ToggleState 为空），
    /// 需要 Pattern 的场景（监控任务取值、Details 树选中节点）走 CaptureWindow / 实时解析。
    /// 必须在 STA 线程内调用。
    /// </summary>
    public TreeSnapshot CaptureWindowFast(TargetWindowInfo target, int maxDepth = 64, int maxNodes = 5000)
    {
        var nodes = new Dictionary<int, ElementNode>();
        var order = new List<int>();
        int idCounter = 0;

        var com = new CUIAutomation();
        try
        {
            var uia = (IUIAutomation)com;
            var walker = uia.ControlViewWalker;
            var root = uia.ElementFromHandle(target.Handle);
            if (root is null)
                return new TreeSnapshot { RootId = -1, Nodes = nodes, Order = Array.Empty<int>(), Target = target };

            int Walk(IUIAutomationElement el, int parentId, int depth)
            {
                if (depth > maxDepth || nodes.Count >= maxNodes) return -1;
                int id = ++idCounter;
                var childIds = new List<int>();

                try
                {
                    var child = walker.GetFirstChildElement(el);
                    while (child is not null)
                    {
                        if (nodes.Count >= maxNodes) break;
                        int cid = Walk(child, id, depth + 1);
                        if (cid >= 0) childIds.Add(cid);
                        child = walker.GetNextSiblingElement(child);
                    }
                }
                catch { /* 某个子树读不到就跳过，不整棵失败 */ }

                nodes[id] = BuildNodeFast(id, parentId, depth, el) with { Children = childIds };
                order.Add(id);
                return id;
            }

            int rootId = Walk(root, -1, 0);
            return new TreeSnapshot { RootId = rootId, Nodes = nodes, Order = order, Target = target };
        }
        catch
        {
            return new TreeSnapshot { RootId = -1, Nodes = nodes, Order = Array.Empty<int>(), Target = target };
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(com); } catch { }
        }
    }

    /// <summary>快速路径的单节点构造：只读结构树真正需要的属性（每次读取都是一次 COM 往返，按需取）。</summary>
    private static ElementNode BuildNodeFast(int id, int parentId, int depth, IUIAutomationElement el)
    {
        string? name = null, aid = null, cls = null, fw = null;
        int ct = 50000, pid = 0;
        bool enabled = true, offscreen = false;
        string? rectStr = null;
        string? rid = null;

        try { name = el.CurrentName; } catch { }
        try { ct = (int)el.CurrentControlType; } catch { }
        try { aid = el.CurrentAutomationId; } catch { }
        try { cls = el.CurrentClassName; } catch { }
        try { fw = el.CurrentFrameworkId; } catch { }
        try { pid = el.CurrentProcessId; } catch { }
        try { enabled = el.CurrentIsEnabled != 0; } catch { }
        try { offscreen = el.CurrentIsOffscreen != 0; } catch { }
        try
        {
            var r = el.CurrentBoundingRectangle;
            rectStr = $"{r.left},{r.top},{r.right},{r.bottom}";
        }
        catch { }
        try
        {
            var r = el.GetRuntimeId();
            if (r is { Length: > 0 }) rid = string.Join(".", r);
        }
        catch { }

        return new ElementNode
        {
            Id = id,
            ParentId = parentId,
            Depth = depth,
            Name = Safe(name),
            ProcessId = pid,
            ControlType = ControlTypeName(ct),
            AutomationId = Safe(aid),
            ClassName = Safe(cls),
            Framework = Safe(fw),
            IsEnabled = enabled,
            IsOffScreen = offscreen,
            BoundingRectangle = rectStr,
            RuntimeId = rid,
            Children = Array.Empty<int>(),
        };
    }

    private static ElementNode BuildNode(int id, int parentId, int depth, A11yElement el, bool collectPatterns)
    {
        // ★ 关键：必须调用此方法从 UIA 获取实时 Pattern 数据填充到 el.Patterns
        // 原版在每次操作元素前都会调用（DesktopElementExtensionMethods）
        // 不调用的话 el.Patterns 永远为 null/空 → Details 面板 Patterns 区域空白
        // 注意：该方法只影响 Pattern，不影响 Name（Name 见 ReadName，必须走 COM）
        if (collectPatterns)
            try { el.PopulateAllPropertiesWithLiveData(); } catch { /* 某些元素可能不支持 */ }

        string? rectStr = null;
        try
        {
            var rect = el.BoundingRectangle;
            rectStr = $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
        }
        catch { /* 矩形可能无效 */ }

        // ★ 用 el.Patterns 直接获取 Pattern 数据（原版 PatternInfoControl/PatternViewModel 同款方式）
        // A11yPattern: Id, Name, Properties(IList<A11yPatternProperty>)
        // A11yPatternProperty: Name, Value(Object), NodeValue(String) ← 原版用 NodeValue
        var patternNames = new List<string>();
        var patternDataList = new List<PatternData>();
        var patternValues = new Dictionary<string, string?>(); // key = "PatternName.PropertyName"（便捷访问）

        if (collectPatterns)
        {
            try
            {
                if (el.Patterns is not null)
                    foreach (var p in el.Patterns)
                    {
                        patternNames.Add(p.Name);
                        if (p.Properties is not null && p.Properties.Count > 0)
                        {
                            // 有属性值：收集子属性 + 取第一个作为主显示值
                            var subProps = new List<PatternPropData>();
                            foreach (var pp in p.Properties)
                            {
                                var nodeVal = pp.NodeValue ?? "(null)";
                                patternValues[$"{p.Name}.{pp.Name}"] = nodeVal;
                                subProps.Add(new PatternPropData(pp.Name, nodeVal));
                            }
                            var mainVal = p.Properties[0].NodeValue ?? "(null)";
                            patternDataList.Add(new PatternData(p.Name, mainVal, subProps));
                        }
                        else
                        {
                            patternDataList.Add(new PatternData(p.Name, "(supported, no current value)", Array.Empty<PatternPropData>()));
                            patternValues[p.Name] = "(no properties)";
                        }
                    }
            }
            catch { /* Patterns 可能为空或异常 */ }
        }

        // 从 patternValues 中提取已知 Pattern 的主属性值（便捷访问，供 GetProperty 使用）
        string? GetVal(string patName, string propName) =>
            patternValues.TryGetValue($"{patName}.{propName}", out var v) ? v : null;

        return new ElementNode
        {
            Id = id,
            ParentId = parentId,
            Depth = depth,
            Name = ReadName(el),
            // ★ 防御：某些 UIA 元素的 int 属性可能因 COM 层返回 null/失败而抛异常
            //   A11yElement.ProcessId / ControlTypeId 是 int 类型，COM 返回无效值时崩溃
            ProcessId = SafeGetInt(() => el.ProcessId),
            ControlType = ControlTypeName(ReadControlTypeId(el)),
            AutomationId = ReadAutomationId(el),
            ClassName = ReadClassName(el),
            Framework = ReadStr(el, u => u.CurrentFrameworkId, () => el.Framework),
            IsEnabled = SafeBool(() => el.IsEnabled),
            IsOffScreen = SafeBool(() => el.IsOffScreen),
            Value = GetVal("ValuePattern", "Value"),
            RangeValue = GetVal("RangeValuePattern", "Value"),
            ToggleState = GetVal("TogglePattern", "ToggleState"),
            BoundingRectangle = rectStr,
            ItemStatus = ReadStr(el, u => u.CurrentItemStatus, () => el.ItemStatus),
            HelpText = ReadStr(el, u => u.CurrentHelpText, () => el.HelpText),
            AccessKey = ReadStr(el, u => u.CurrentAccessKey, () => el.AccessKey),
            IsSelected = GetVal("SelectionItemPattern", "IsSelected"),
            ExpandCollapseState = GetVal("ExpandCollapsePattern", "ExpandCollapseState"),
            PatternDataList = patternDataList,
            SupportedPatterns = patternNames,
            RuntimeId = ReadRuntimeId(el),
            Children = Array.Empty<int>(),
        };
    }

    /// <summary>字符串属性读取：优先 COM 实时值，退回 Axe.Windows 缓存字段。
    /// 实测（msedge 242 节点）：Axe.Windows 的 el.Name 只有 11% 有值（遍历得到的子元素不填字符串属性），
    /// 改读 IUIAutomationElement.CurrentName 后升到 79%——结构树的名称必须走 COM。</summary>
    private static string? ReadStr(A11yElement el, Func<IUIAutomationElement, string?> fromUia, Func<string?> fromAxe)
    {
        try
        {
            if (el.PlatformObject is IUIAutomationElement u)
            {
                var v = fromUia(u);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { /* COM 读取失败，退回缓存字段 */ }
        try { return Safe(fromAxe()); } catch { return null; }
    }

    /// <summary>读元素名称（结构树显示 / 捕获定位都用这一份，保证两侧一致）。</summary>
    public static string? ReadName(A11yElement el) => ReadStr(el, u => u.CurrentName, () => el.Name);

    /// <summary>读 AutomationId（XAML 的 x:Name 就在这里）。</summary>
    public static string? ReadAutomationId(A11yElement el) => ReadStr(el, u => u.CurrentAutomationId, () => el.AutomationId);

    /// <summary>读 ClassName。</summary>
    public static string? ReadClassName(A11yElement el) => ReadStr(el, u => u.CurrentClassName, () => el.ClassName);

    /// <summary>读 ControlTypeId（同样优先 COM 实时值：Axe.Windows 缓存值可能与当前元素不符，
    /// 会出现"树里显示 Button、实际是 MenuItem"这类错位）。</summary>
    public static int ReadControlTypeId(A11yElement el)
    {
        try
        {
            if (el.PlatformObject is IUIAutomationElement u) return (int)u.CurrentControlType;
        }
        catch { /* 退回缓存值 */ }
        try { return el.ControlTypeId; } catch { return 50000; } // 50000 = Custom
    }

    /// <summary>读 UIA RuntimeId（"a.b.c"）：同一元素实例内唯一，用于鼠标捕获到树节点的精确命中。
    /// 读不到返回 null（部分 provider 不提供），此时捕获侧自动退化为矩形/属性匹配。</summary>
    public static string? ReadRuntimeId(A11yElement el)
    {
        try
        {
            var uiaEl = el.PlatformObject as IUIAutomationElement;
            var rid = uiaEl?.GetRuntimeId();
            return rid is { Length: > 0 } ? string.Join(".", rid) : null;
        }
        catch { return null; }
    }

    private static string? Safe(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>安全读取 int 属性（COM 可能返回 null/失败导致异常）。</summary>
    private static int SafeGetInt(Func<int> getter, int fallback = 0)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    /// <summary>安全读取 bool 属性（COM 可能返回 null/失败导致异常）。</summary>
    private static bool SafeBool(Func<bool> getter, bool fallback = false)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
