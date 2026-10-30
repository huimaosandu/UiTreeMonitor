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
    Guid? ElementContextId = null);      // SelectAction 的 POI ElementContext id（供 UI 取 A11yElement 驱动原版高亮）

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
                                Safe(el.Name),
                                TreeSampler.ControlTypeName(el.ControlTypeId),
                                Safe(el.AutomationId),
                                Safe(el.ClassName),
                                el.ProcessId,
                                Safe(el.ProcessName),
                                NormalizeRect(el.BoundingRectangle),
                                el.IsKeyboardFocusable,
                                TreeNodeId: null,
                                IsValid: true,
                                ElementContextId: ecId);
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

    /// <summary>把一次捕获结果定位到已加载树：返回匹配节点 id。
    /// 矩形优先（悬停元素与树节点同源 UIA，"L,T,R,B" 物理像素矩形稳定唯一——Name 是实时变化值、
    /// AutomationId 常缺失/重复，属性匹配不可靠）；矩形未命中再属性匹配兜底。</summary>
    public static int? LocateInTree(TreeSnapshot tree, MouseCaptureResult capture)
    {
        if (!capture.IsValid || tree is null || tree.Nodes.Count == 0) return null;

        // 第1优先：屏幕矩形 + ControlType 完全相等（元素不动则矩形不变，与值变化无关）
        var rectStr = RectString(capture.BoundingRectangle);
        if (rectStr is not null)
        {
            var byRect = ElementMatcher.FindByRect(tree, rectStr, capture.ControlType);
            if (byRect is not null) return byRect.Id;
        }

        // 第2优先：属性匹配（Name+Type+AutoId 全条件，空条件跳过；取深度最深）
        ElementNode? best = null;
        foreach (var (id, node) in tree.Nodes)
        {
            if (node.ProcessId != 0 && capture.ProcessId != 0 && node.ProcessId != capture.ProcessId)
                continue;
            bool nameOk = string.IsNullOrEmpty(capture.Name) || node.Name == capture.Name;
            bool typeOk = node.ControlType == capture.ControlType;
            bool autoOk = string.IsNullOrEmpty(capture.AutomationId) || node.AutomationId == capture.AutomationId;
            if (nameOk && typeOk && autoOk)
            {
                // 取深度最深的匹配（最具体）
                if (best is null || node.Depth > best.Depth) best = node;
            }
        }
        return best?.Id;
    }

    /// <summary>Rectangle → 树节点同款 "L,T,R,B" 字符串（空/无效矩形返回 null）。</summary>
    private static string? RectString(Rectangle? rect)
        => rect is { } r && r.Width > 0 && r.Height > 0 ? $"{r.Left},{r.Top},{r.Right},{r.Bottom}" : null;

    private static string? Safe(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>空矩形（0 尺寸或异常值）返回 null，避免高亮窗口无效定位。</summary>
    private static Rectangle? NormalizeRect(Rectangle r)
        => r.Width > 0 && r.Height > 0 ? r : null;

    public void Dispose() => Stop();
}
