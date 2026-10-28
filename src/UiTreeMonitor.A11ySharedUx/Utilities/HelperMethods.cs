// 节选自原版 accessibility-insights-windows/src/AccessibilityInsights.SharedUx/Utilities/HelperMethods.cs
// 原文件其余成员依赖 FileIssue/Settings(ConfigurationManager/IssueReporter) 等本仓库未搬入的部分，
// 故仅按原文照抄 Win32SnapshotButton 所需的 GetDPI 相关方法（代码与原版一致）。
using AccessibilityInsights.Win32;
using System.Drawing;

namespace AccessibilityInsights.SharedUx.Utilities
{
    public static class HelperMethods
    {
        /// <summary>
        /// Get DPI
        /// </summary>
        /// <param name="rc"></param>
        /// <returns></returns>
        internal static double GetDPI(this Rectangle rc)
        {
            return GetDPI(rc.Left, rc.Top);
        }

        /// <summary>
        /// Get DPI with left/top
        /// </summary>
        /// <param name="left"></param>
        /// <param name="top"></param>
        /// <returns></returns>
        internal static double GetDPI(int left, int top)
        {
            NativeMethods.GetDpi(new System.Drawing.Point(left, top), DpiType.Effective, out uint dpiX, out _);

            return GetDPIRate(dpiX);
        }

        /// <summary>
        /// turn raw DPI value to DPI rate
        /// </summary>
        /// <param name="dpiX"></param>
        /// <returns></returns>
        private static double GetDPIRate(uint dpi)
        {
            return dpi / 96.0; // 96 is 100% scale.
        }

        /// <summary>
        /// Gets the DPI that WPF seems to use internally
        /// when positioning windows; scale by this DPI
        /// when setting Window.Left / Window.Top before calling show()
        /// </summary>
        /// <returns></returns>
        internal static double GetWPFWindowPositioningDPI()
        {
            return new Rectangle().GetDPI();
        }
    }
}
