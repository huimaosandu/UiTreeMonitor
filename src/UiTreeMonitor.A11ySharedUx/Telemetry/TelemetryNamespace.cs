// 适配垫片（非原版文件）：原样复制的 Highlighting 文件引用了
// AccessibilityInsights.SharedUx.Telemetry 命名空间（Win32SnapshotButton 顶部的 using）
// 并调用其中的 Logger.ReportException(this Exception) 扩展（原版 Telemetry/Logger.cs:103）。
// 原版该方法在遥测未启用时即为 no-op 直接返回；本仓库未接入遥测管线，
// 此处提供等价的 no-op 实现，使原版 Highlighting 文件无需任何修改。
using System;

namespace AccessibilityInsights.SharedUx.Telemetry
{
    /// <summary>
    /// Use this class to send telemetry from anywhere within
    /// the Accessibility Insights code. This does not include extensions,
    /// which should use the EventTelemetrySink mechanism.
    /// </summary>
    public static class Logger
    {
        /// <summary>
        /// Report an Exception into the pipeline
        /// </summary>
        /// <param name="e">The Exception to report</param>
        public static void ReportException(this Exception e)
        {
            // 与原版"遥测未启用"时行为一致：no-op。
        }
    }
}
