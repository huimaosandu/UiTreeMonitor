// 鼠标捕获：光标移动时实时定位结构树节点（Element Inspector）
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.Core.Targeting;

/// <summary>一次鼠标捕获结果。</summary>
public sealed record MouseCaptureResult(
    int X, int Y,
    string? Name, string ControlType, string? AutomationId, string? ClassName,
    int ProcessId, string? ProcessName,
    int? TreeNodeId,           // 在已加载树中定位到的节点 id（可能为 null）
    bool IsValid);

/// <summary>
/// 鼠标捕获器：独立 STA 线程内轮询光标位置，通过 UIA ElementFromPoint 取元素。
/// 结果经线程安全队列回传 UI 线程（避免跨线程操作 Tk/WPF）。
/// </summary>
public sealed class MouseCapture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private readonly ConcurrentQueue<MouseCaptureResult> _results = new();
    private volatile bool _running;
    private Thread? _thread;
    private readonly int _pollMs;

    /// <summary>每次捕获到新元素时触发（供 UI 订阅）。</summary>
    public event Action<MouseCaptureResult>? ResultProduced;

    /// <summary>最近一次捕获的原始 UIA 元素句柄信息（用于后续建树/定位）。</summary>
    private IntPtr _lastHandle = IntPtr.Zero;
    private int _lastProcessId;

    public MouseCapture(int pollMs = 120)
    {
        _pollMs = Math.Max(40, pollMs);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MouseCapture" };
        _thread.SetApartmentState(ApartmentState.STA); // UIA COM 要求 STA
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    private void Loop()
    {
        using var session = UiaSession.Create();
        while (_running)
        {
            try
            {
                GetCursorPos(out var pt);
                var el = session.GetElementFromPoint(pt.X, pt.Y);
                if (el is null)
                {
                    _results.Enqueue(new MouseCaptureResult(pt.X, pt.Y, null, "", null, null, 0, null, null, false));
                }
                else
                {
                    _lastHandle = el.NativeWindowHandle;
                    _lastProcessId = el.ProcessId;
                    var r = new MouseCaptureResult(
                        pt.X, pt.Y,
                        Safe(el.Name),
                        TreeSampler.ControlTypeName(el.ControlTypeId),
                        Safe(el.AutomationId),
                        Safe(el.ClassName),
                        el.ProcessId,
                        Safe(el.ProcessName),
                        TreeNodeId: null,
                        IsValid: true);
                    _results.Enqueue(r);
                    ResultProduced?.Invoke(r);
                }
            }
            catch
            {
                // 单次轮询失败不影响后续
            }
            Thread.Sleep(_pollMs);
        }
    }

    public bool TryDequeue(out MouseCaptureResult result) => _results.TryDequeue(out result!);

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

    public void Dispose() => Stop();
}
