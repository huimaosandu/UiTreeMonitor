// UIA 底层封装：直接基于 UIAutomationClient COM + Axe.Windows DesktopElement 遍历树
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Axe.Windows.Core.Bases;
using Axe.Windows.Desktop.UIAutomation;
using UIAutomationClient;

namespace UiTreeMonitor.Core.Uia;

/// <summary>
/// Axe.Windows 桌面 UIA 封装。
/// 用法：必须在 STA 线程内调用（每个监控任务/捕获线程各自 new 一个实例）。
/// 采用 UIAutomationClient COM（与 Axe.Windows 同款 interop 程序集）遍历，
/// 元素用 Axe.Windows DesktopElement 包装为 A11yElement 读取属性。
/// </summary>
public sealed class UiaSession : IDisposable
{
    private readonly CUIAutomation _comRoot;
    private readonly IUIAutomation _uia;
    private readonly IUIAutomationTreeWalker _walker;
    private readonly DesktopDataContext? _context;

    private UiaSession(CUIAutomation comRoot, IUIAutomation uia, DesktopDataContext? context)
    {
        _comRoot = comRoot;
        _uia = uia;
        _walker = uia.ControlViewWalker;
        _context = context;
    }

    /// <summary>当前线程必须是 STA（UIA COM 要求）。</summary>
    public static UiaSession Create()
    {
        var thread = Thread.CurrentThread;
        if (thread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("UiaSession 必须在 STA 线程创建（UIA COM 要求）");

        var comRoot = new CUIAutomation();
        IUIAutomation uia = (IUIAutomation)comRoot;
        return new UiaSession(comRoot, uia, context: null);
    }

    /// <summary>从窗口句柄获取根元素（包装为 A11yElement）。</summary>
    public A11yElement? GetRootFromHandle(IntPtr hwnd)
    {
        try
        {
            var uiaElement = _uia.ElementFromHandle(hwnd);
            if (uiaElement == null) return null;
            return new DesktopElement(uiaElement, keepElement: true, setMembers: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取指定元素在 ControlView 中的第一个子元素。</summary>
    public A11yElement? GetFirstChild(A11yElement parent)
    {
        var uiaParent = parent.PlatformObject as IUIAutomationElement;
        if (uiaParent == null) return null;
        try
        {
            var child = _walker.GetFirstChildElement(uiaParent);
            return child == null ? null : new DesktopElement(child, keepElement: true, setMembers: true);
        }
        catch { return null; }
    }

    /// <summary>获取指定元素的下一个兄弟元素。</summary>
    public A11yElement? GetNextSibling(A11yElement element)
    {
        var uiaEl = element.PlatformObject as IUIAutomationElement;
        if (uiaEl == null) return null;
        try
        {
            var next = _walker.GetNextSiblingElement(uiaEl);
            return next == null ? null : new DesktopElement(next, keepElement: true, setMembers: true);
        }
        catch { return null; }
    }

    /// <summary>获取指定元素的父元素。</summary>
    public A11yElement? GetParent(A11yElement element)
    {
        var uiaEl = element.PlatformObject as IUIAutomationElement;
        if (uiaEl == null) return null;
        try
        {
            var parent = _walker.GetParentElement(uiaEl);
            return parent == null ? null : new DesktopElement(parent, keepElement: true, setMembers: true);
        }
        catch { return null; }
    }

    /// <summary>从屏幕坐标获取该点处的元素（鼠标捕获用）。</summary>
    public A11yElement? GetElementFromPoint(int x, int y)
    {
        try
        {
            var pt = new tagPOINT { x = x, y = y };
            var uiaElement = _uia.ElementFromPoint(pt);
            if (uiaElement == null) return null;
            return new DesktopElement(uiaElement, keepElement: true, setMembers: true);
        }
        catch (COMException)
        {
            // 该点无 UIA 元素（如桌面/遮挡），返回 null
            return null;
        }
    }

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(_comRoot); } catch { /* 已释放 */ }
        GC.SuppressFinalize(this);
    }
}
