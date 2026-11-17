// 鼠标捕获：直接使用 accessibility-insights-windows 的捕获技术（Axe.Windows.SelectAction）
// 机制（对照 axe-windows 2.4.2 源码 + 原版 Timers.cs/Initialize.cs）：
//  1) sa.IsMouseSelectOn / IntervalMouseSelector(毫秒) / Scope 只是配置；
//  2) sa.Start() 才会启动 MouseTracker（System.Timers.Timer 周期取光标下元素，
//     且要求鼠标在连续两个 tick 间停住才产生候选——"悬停"语义，同一位置只取一次）；
//  3) sa.Select() 把 tracker 异步塞入的候选元素转正为 POIElementContext 并返回 true；
//  4) 通过 GetDataAction.GetElementContext(sa.SelectedElementContextId).Element 取回 A11yElement，
//     与上一个 POI 用 IsSameUIElement 比较去重，过滤桌面根元素。
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using Axe.Windows.Actions;
using Axe.Windows.Actions.Enums;
using Axe.Windows.Core.Bases;
using Axe.Windows.Core.Misc;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Core.Targeting;

/// <summary>一次鼠标捕获结果。</summary>
public sealed record MouseCaptureResult(
    int X, int Y,
    string? Name, string ControlType, string? AutomationId, string? ClassName,
    int ProcessId, string? ProcessName,
    Rectangle? BoundingRectangle,        // 屏幕物理像素坐标（UIA 原生）
    bool? IsKeyboardFocusable,           // 是否可键盘聚焦（高亮信息卡用）
    int? TreeNodeId,                     // 在已加载树中定位到的节点 id（可能为 null）
    bool IsValid,
    Guid? ElementContextId = null,       // SelectAction 的 POI ElementContext id（供 UI 取 A11yElement 驱动原版高亮）
    string? RuntimeId = null);           // 捕获元素的 UIA RuntimeId（"a.b.c"）：在树快照里精确定位

/// <summary>
/// 鼠标捕获器（Element Inspector）。
/// 直接复用 accessibility-insights-windows 的捕获技术：Axe.Windows.Actions.SelectAction
/// （GetDefaultInstance / IntervalMouseSelector / IsMouseSelectOn / Select / SelectedElementContextId
///  + GetDataAction.GetElementContext），在独立 STA 线程轮询，结果经事件回传 UI 线程。
/// </summary>
public sealed class MouseCapture : IDisposable
{
    private volatile bool _running;
    private Thread? _thread;
    private readonly int _pollMs;

    /// <summary>每次捕获到新元素时触发（供 UI 订阅）。</summary>
    public event Action<MouseCaptureResult>? ResultProduced;

    /// <summary>最近一次捕获元素的指纹（用于过滤重复结果）。</summary>
    private string? _lastFingerprint;

    public MouseCapture(int pollMs = 120)
    {
        _pollMs = Math.Max(40, pollMs);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MouseCapture" };
        _thread.SetApartmentState(ApartmentState.STA); // Axe.Windows 要求 STA
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        try
        {
            // 关掉 SelectAction 的鼠标选择器，避免后台持续占用
            SelectAction.GetDefaultInstance().IsMouseSelectOn = false;
            SelectAction.GetDefaultInstance().Stop();
        }
        catch { /* 单例可能已被其他调用方释放 */ }
    }

    private void Loop()
    {
        var sa = SelectAction.GetDefaultInstance();
        // 配置照原版 Initialize.cs：
        //   sa.IntervalMouseSelector = AppConfig.MouseSelectionDelayMilliSeconds;  ← 单位是毫秒
        //   sa.IsMouseSelectOn = AppConfig.SelectionByMouse;
        //   sa.Scope = Element/App
        // 注意：IsMouseSelectOn 只是属性开关，MouseTracker(内部 System.Timers.Timer 轮询光标)
        // 必须由 sa.Start() 真正启动，否则永远没有候选元素（此前 bug 根因）。
        sa.IntervalMouseSelector = _pollMs;                 // 毫秒，不是 /1000
        sa.IsMouseSelectOn = true;                          // 开启鼠标选择
        sa.Scope = SelectionScope.Element;                  // 原版默认 Element 范围
        sa.Start();                                         // ★ 启动 MouseTracker

        // 取结果模式照原版 Timers.cs(OntimerSelectorElapsedEvent)：
        // sa.Select() 只是把 tracker 异步塞入的候选元素转正（内部有 _candidateEC），
        // 需与上一个 POI 比较 IsSameUIElement 判定"是否新选择"，并过滤桌面根元素。
        // ★ 严禁 Dispose 新取回的 ElementContext：它由 SelectAction/DataManager 持有，
        //   POIElementContext 被替换时 setter 会自动清理，外部 Dispose 会毒化单例状态。
        while (_running)
        {
            try
            {
                var cecId = sa.SelectedElementContextId;
                var cecEl = cecId.HasValue ? GetDataAction.GetElementContext(cecId.Value)?.Element : null;

                if (sa.Select())
                {
                    var ecId = sa.SelectedElementContextId;
                    var el = ecId.HasValue ? GetDataAction.GetElementContext(ecId.Value)?.Element : null;

                    if (el is not null
                        && (cecEl is null || !cecEl.IsSameUIElement(el))
                        && !el.IsRootElement())
                    {
                        var fp = Fingerprint(el);
                        if (fp != _lastFingerprint) // 防御性去重（tracker 内部亦有 RuntimeId 去重）
                        {
                            _lastFingerprint = fp;
                            var r = new MouseCaptureResult(
                                X: 0, Y: 0,   // SelectAction 不直接暴露光标像素坐标，树定位用矩形/属性
                                TreeSampler.ReadName(el),        // ★ 走 COM（el.Name 对 Chromium 元素常为空）
                                TreeSampler.ControlTypeName(TreeSampler.ReadControlTypeId(el)),
                                TreeSampler.ReadAutomationId(el),
                                TreeSampler.ReadClassName(el),
                                SafeGetInt(() => el.ProcessId),   // ★ 安全读取，COM 可能返回 null
                                Safe(el.ProcessName),
                                NormalizeRect(el.BoundingRectangle),
                                SafeBool(() => el.IsKeyboardFocusable),  // ★ 安全读取，COM 可能返回 null
                                TreeNodeId: null,
                                IsValid: true,
                                ElementContextId: ecId,
                                RuntimeId: TreeSampler.ReadRuntimeId(el));
                            ResultProduced?.Invoke(r);
                        }
                    }
                }
            }
            catch
            {
                // 单次轮询失败不影响后续
            }
            Thread.Sleep(_pollMs);
        }

        try { sa.Stop(); } catch { /* 忽略 */ }
    }

    /// <summary>元素指纹：进程 + 关键属性 + 屏幕矩形，用于判定"是否已换元素"。</summary>
    private static string Fingerprint(A11yElement el)
        => $"{el.ProcessId}|{el.Name}|{el.AutomationId}|{el.ControlTypeId}|{el.ClassName}|{el.BoundingRectangle}";

    /// <summary>一次"捕获 → 树位置"的定位结果。Path 是结构索引路径（如 "0/4/1/3"），
    /// 与任务/脚本里的 ElementPath 同一套编码，可直接复制使用。</summary>
    public sealed record TreeHit(int NodeId, string Via, string? Path);

    /// <summary>把一次捕获结果定位到已加载树，返回节点 id + 结构路径 + 命中方式。
    /// 顺序：RuntimeId（元素身份证，精确）→ 矩形完全相等 → 矩形容差 → 精确属性。
    /// 只有 Via=runtime 才是"同一元素"的铁证；其余方式命中会在 UI 上标注，提示刷新结构树。</summary>
    public static TreeHit? LocateInTree(TreeSnapshot tree, MouseCaptureResult capture)
    {
        if (!capture.IsValid || tree is null || tree.Nodes.Count == 0) return null;

        TreeHit Make(int id, string via) => new(id, via, ElementMatcher.BuildPath(tree, id));

        // 第0优先：RuntimeId 精确相等 —— 捕获元素与树快照同源 UIA，RuntimeId 是元素实例的身份证，
        // 命中即同一元素，结构路径也就是唯一确定的位置（不受缩放/滚动/DPI/Name 变化影响）
        if (!string.IsNullOrEmpty(capture.RuntimeId))
        {
            foreach (var node in tree.Nodes.Values)
            {
                if (node.RuntimeId is not null && node.RuntimeId == capture.RuntimeId) return Make(node.Id, "runtime");
            }
        }

        // 第1优先：屏幕矩形 + ControlType 完全相等（元素不动则矩形不变，与值变化无关）
        var rectStr = RectString(capture.BoundingRectangle);
        if (rectStr is not null)
        {
            var byRect = ElementMatcher.FindByRect(tree, rectStr, capture.ControlType);
            if (byRect is not null) return Make(byRect.Id, "rect");
            // 第1.5：容差匹配（窗口缩放/滚动/DPR 造成 1-8px 偏移时，精确相等会全盘落空）
            var near = ElementMatcher.FindByRectNear(tree, rectStr, capture.ControlType, 8);
            if (near is not null) return Make(near.Id, "rect-near");
        }

        // 第2优先：属性匹配（Name+Type+AutoId 全条件，空条件跳过；取深度最深）
        var capName = NormalizeName(capture.Name);
        ElementNode? best = null;
        foreach (var (id, node) in tree.Nodes)
        {
            if (node.ProcessId != 0 && capture.ProcessId != 0 && node.ProcessId != capture.ProcessId)
                continue;
            bool nameOk = capName is null || NormalizeName(node.Name) == capName;
            bool typeOk = node.ControlType == capture.ControlType;
            bool autoOk = string.IsNullOrEmpty(capture.AutomationId) || node.AutomationId == capture.AutomationId;
            if (nameOk && typeOk && autoOk)
            {
                // 取深度最深的匹配（最具体）
                if (best is null || node.Depth > best.Depth) best = node;
            }
        }
        return best is null ? null : Make(best.Id, "attr");
    }

    /// <summary>Name 归一化：去掉零宽字符/不换行空格并压缩空白（网页元素 Name 常夹 \u200B，精确比较会莫名不相等）。</summary>
    private static string? NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "")
                       .Replace("\uFEFF", "").Replace("\u00A0", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Rectangle → 树节点同款 "L,T,R,B" 字符串（空/无效矩形返回 null）。</summary>
    private static string? RectString(Rectangle? rect)
        => rect is { } r && r.Width > 0 && r.Height > 0 ? $"{r.Left},{r.Top},{r.Right},{r.Bottom}" : null;

    private static string? Safe(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>安全读取 int 属性（COM 可能返回 null/失败导致异常）。</summary>
    private static int SafeGetInt(Func<int> getter, int fallback = 0)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    /// <summary>安全读取 bool 属性（COM 可能返回 null/失败导致异常）。</summary>
    private static bool? SafeBool(Func<bool> getter, bool? fallback = null)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    /// <summary>空矩形（0 尺寸或异常值）返回 null，避免高亮窗口无效定位。</summary>
    private static Rectangle? NormalizeRect(Rectangle r)
        => r.Width > 0 && r.Height > 0 ? r : null;

    public void Dispose() => Stop();
}
