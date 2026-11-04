// 元素截图：目标元素包围矩形（屏幕物理像素）区域 → PNG 保存指定目录（录制截图步骤与捕获插入"试截一张"共用）
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace UiTreeMonitor.Core.Uia;

/// <summary>
/// 屏幕区域截图。坐标使用屏幕物理像素（与 UIA BoundingRectangle 同源），
/// Graphics.CopyFromScreen 亦按物理像素工作，无需 DPI 换算。
/// </summary>
public static class ScreenCapture
{
    /// <summary>
    /// 截取矩形区域并保存为 {prefix}-yyyyMMdd_HHmmssfff.png 于 dir（目录不存在则创建）。
    /// 返回 (true, 完整文件路径) 或 (false, 失败原因)。
    /// </summary>
    public static (bool Ok, string File) CaptureRegion(Rectangle rect, string dir, string prefix = "img")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
                return (false, "保存目录未配置");
            if (rect.Width <= 0 || rect.Height <= 0)
                return (false, $"截图区域无效（{rect.Width}x{rect.Height}）");

            // 裁剪到虚拟屏幕范围（越界区域截图会取到空白/抛异常）
            var vs = SystemInformation.VirtualScreen;
            var clip = Rectangle.Intersect(rect, new Rectangle(vs.X, vs.Y, vs.Width, vs.Height));
            if (clip.Width <= 0 || clip.Height <= 0)
                return (false, $"截图区域在屏幕外（{rect.Left},{rect.Top} {rect.Width}x{rect.Height}）");

            Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(clip.Width, clip.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(clip.Left, clip.Top, 0, 0, new Size(clip.Width, clip.Height));
            }
            var file = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
            bmp.Save(file, ImageFormat.Png);
            return (true, file);
        }
        catch (Exception ex)
        {
            AppLog.Write("SHOT", $"CaptureRegion FAILED ({rect.Left},{rect.Top} {rect.Width}x{rect.Height} → {dir}): {ex.Message}");
            return (false, ex.Message);
        }
    }
}
