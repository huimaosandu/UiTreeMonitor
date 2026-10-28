// 鼠标捕获：直接使用 accessibility-insights-windows 的捕获技术（Axe.Windows.SelectAction）
// SelectAction 是原版"随光标捕获"内核：内部维护鼠标选择器，Select() 时把光标下元素
// 登记到 ElementContext，通过 GetDataAction.GetElementContext(ecId).Element 取回 A11yElement。
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using Axe.Windows.Actions;
using Axe.Windows.Actions.Enums;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;

namespace UiTreeMonitor.Core.Targeting;

/// <summary>一次鼠标捕获结果。</summary>
public sealed record MouseCaptureResult(
    int X, int Y,
    string? Name, string ControlType, string? AutomationId, string? ClassName,
    int ProcessId, string? ProcessName,
    Rectangle? BoundingRectangle,        // 屏幕物理像素坐标（UIA 原生）
    bool? IsKeyboardFocusable,           // 是否可键盘聚焦（高亮信息卡用）
    int? TreeNodeId,                     // 在已加载树中定位到的节点 id（可能为 null）
    bool IsValid);

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
        sa.IsMouseSelectOn = true;                       // 开启鼠标选择
        sa.IntervalMouseSelector = _pollMs / 1000.0;     // 原版: MouseSelectionDelayMilliSeconds
        sa.Scope = SelectionScope.Element;               // 原版默认 Element 范围

        while (_running)
        {
            try
            {
                if (!sa.Select()) { Thread.Sleep(_pollMs); continue; }

                var ecId = sa.SelectedElementContextId;
                if (ecId is not Guid id) { Thread.Sleep(_pollMs); continue; }

                using var ec = GetDataAction.GetElementContext(id);
                var el = ec.Element;
                if (el is null) { Thread.Sleep(_pollMs); continue; }

                var fp = Fingerprint(el);
                if (fp == _lastFingerprint) { Thread.Sleep(_pollMs); continue; } // 未变化不重复触发
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
                    IsValid: true);
                ResultProduced?.Invoke(r);
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

    /// <summary>把一次捕获结果定位到已加载树：返回匹配节点 id。</summary>
    public static int? LocateInTree(TreeSnapshot tree, MouseCaptureResult capture)
    {
        if (!capture.IsValid || tree is null || tree.Nodes.Count == 0) return null;

        // 优先精确匹配：Name + ControlType + AutomationId 全同（且进程一致）
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

    private static string? Safe(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>空矩形（0 尺寸或异常值）返回 null，避免高亮窗口无效定位。</summary>
    private static Rectangle? NormalizeRect(Rectangle r)
        => r.Width > 0 && r.Height > 0 ? r : null;

    public void Dispose() => Stop();
}
