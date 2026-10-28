// 目标定位：进程/窗口枚举 + 鼠标捕获
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UiTreeMonitor.Core.Targeting;

/// <summary>一个可监控的目标窗口。</summary>
public sealed record TargetWindow(int ProcessId, IntPtr Handle, string ProcessName, string Title)
{
    public override string ToString() => $"{ProcessName} [{ProcessId}] — {Title}";
}

public static class WindowEnumerator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>枚举所有可见、有标题、有有效尺寸的顶层窗口。</summary>
    public static IReadOnlyList<TargetWindow> EnumerateWindows()
    {
        var result = new List<TargetWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return true; // 无标题的窗口不纳入
            GetWindowRect(hwnd, out var r);
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;

            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);

            string procName = "?";
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { /* 进程可能已退出 */ }

            result.Add(new TargetWindow((int)pid, hwnd, procName, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
