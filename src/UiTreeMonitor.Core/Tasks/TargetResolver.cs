// 目标解析：把 MonitorTarget 解析为实际窗口
using System;
using System.Globalization;
using System.Linq;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Targeting;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>目标解析结果。</summary>
public sealed record ResolvedTarget(TargetWindowInfo Info, bool IsWindowAlive);

public static class TargetResolver
{
    /// <summary>解析监控目标为窗口信息。窗口不存在时返回 null。</summary>
    public static TargetWindowInfo? Resolve(MonitorTarget target)
    {
        switch (target.Kind)
        {
            case TargetKind.Process:
            {
                var windows = WindowEnumerator.EnumerateWindows();
                var w = windows.FirstOrDefault(x =>
                    x.ProcessName.Equals(target.Value, StringComparison.OrdinalIgnoreCase));
                return w is null ? null : new TargetWindowInfo(w.ProcessId, w.Handle, w.ProcessName, w.Title);
            }
            case TargetKind.WindowHandle:
            {
                long handle;
                var v = target.Value.Trim();
                if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(v.AsSpan(2), NumberStyles.HexNumber, null, out handle)) return null;
                }
                else if (!long.TryParse(v, out handle)) return null;

                var windows = WindowEnumerator.EnumerateWindows();
                var w = windows.FirstOrDefault(x => x.Handle.ToInt64() == handle);
                return w is null ? null : new TargetWindowInfo(w.ProcessId, w.Handle, w.ProcessName, w.Title);
            }
            case TargetKind.WindowTitle:
            {
                var windows = WindowEnumerator.EnumerateWindows();
                var w = windows.FirstOrDefault(x =>
                    x.Title.Contains(target.Value, StringComparison.OrdinalIgnoreCase));
                return w is null ? null : new TargetWindowInfo(w.ProcessId, w.Handle, w.ProcessName, w.Title);
            }
            default:
                return null;
        }
    }
}
