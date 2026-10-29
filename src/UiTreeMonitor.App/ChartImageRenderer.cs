using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiTreeMonitor.App;

/// <summary>一个绘图系列：图例名 + 采样点（时间, 数值）。</summary>
public sealed record ChartSeries(string Legend, IReadOnlyList<(DateTime Time, double Value)> Points);

/// <summary>
/// 用 WPF DrawingVisual 自绘多系列折线图并导出 PNG（时间戳-值，一图多线、统一坐标轴、右上角图例）。
/// </summary>
public static class ChartImageRenderer
{
    private static readonly SolidColorBrush AxisBrush = new(Color.FromRgb(90, 90, 90));
    private static readonly SolidColorBrush GridBrush = new(Color.FromRgb(225, 228, 232));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(60, 60, 60));

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x1F, 0x77, 0xB4), Color.FromRgb(0xD6, 0x27, 0x28), Color.FromRgb(0x2C, 0xA0, 0x2C),
        Color.FromRgb(0xFF, 0x7F, 0x0E), Color.FromRgb(0x94, 0x67, 0xBD), Color.FromRgb(0x8C, 0x56, 0x4B),
        Color.FromRgb(0x00, 0x89, 0x9C), Color.FromRgb(0xE3, 0x77, 0xC2),
    };

    /// <summary>渲染折线图并保存为 PNG。</summary>
    /// <param name="pngPath">输出文件路径（.png）。</param>
    /// <param name="series">系列集合（至少一条；各自点数可不同）。</param>
    /// <param name="title">图表标题（任务名）。</param>
    public static void RenderPng(string pngPath, IReadOnlyList<ChartSeries> series, string? title = null,
        int width = 1100, int height = 560)
    {
        if (series is not { Count: > 0 } || series.All(s => s.Points.Count == 0))
            throw new ArgumentException("至少需要一条含采样点的曲线。", nameof(series));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            DrawChart(dc, series, title, width, height);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(pngPath);
        encoder.Save(fs);
    }

    private static void DrawChart(DrawingContext dc, IReadOnlyList<ChartSeries> series, string? title,
        int width, int height)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        double left = 92, top = title is null ? 24 : 56, right = width - 20, bottom = height - 56;
        var plot = new Rect(left, top, Math.Max(60, right - left), Math.Max(60, bottom - top));

        // 统一时间/数值范围（全部系列合并）
        var allPoints = series.SelectMany(s => s.Points).ToList();
        var allTimes = allPoints.Select(p => p.Time).ToList();
        DateTime tMin = allTimes.Min(), tMax = allTimes.Max();
        double vMin = allPoints.Min(p => p.Value), vMax = allPoints.Max(p => p.Value);
        if (Math.Abs(vMax - vMin) < 1e-12) { vMin -= 1; vMax += 1; }   // 恒值线时给出上下界
        double tSpan = Math.Max(1, (tMax - tMin).TotalMilliseconds);

        DrawTitle(dc, title, width);
        DrawGridAndAxes(dc, plot, tMin, tMax, vMin, vMax);
        DrawSeriesLines(dc, series, plot, tMin, tSpan, vMin, vMax);
        DrawLegend(dc, series, width);
    }

    private static void DrawTitle(DrawingContext dc, string? title, int width)
    {
        if (title is null) return;
        var ft = MakeText(title, 18, bold: true);
        dc.DrawText(ft, new Point((width - ft.Width) / 2, 16));
    }

    private static void DrawGridAndAxes(DrawingContext dc, Rect plot, DateTime tMin, DateTime tMax,
        double vMin, double vMax)
    {
        var gridPen = new Pen(GridBrush, 1);
        var axisPen = new Pen(AxisBrush, 1.5);

        // Y 轴 5 段水平网格线 + 刻度值
        for (int i = 0; i <= 5; i++)
        {
            double y = plot.Bottom - plot.Height * i / 5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            double v = vMin + (vMax - vMin) * i / 5;
            var ft = MakeText(FormatValue(v), 11);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }

        // X 轴 3 个时间刻度（首/中/末）+ 竖网格线
        var ticks = new[] { tMin, tMin + TimeSpan.FromMilliseconds((tMax - tMin).TotalMilliseconds / 2), tMax };
        foreach (var t in ticks)
        {
            double x = MapX(t, plot, tMin, Math.Max(1, (tMax - tMin).TotalMilliseconds));
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = MakeText(t.ToString("HH:mm:ss"), 11);
            dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 8));
        }

        // 坐标轴框
        dc.DrawRectangle(null, axisPen, plot);
    }

    private static void DrawSeriesLines(DrawingContext dc, IReadOnlyList<ChartSeries> series, Rect plot,
        DateTime tMin, double tSpan, double vMin, double vMax)
    {
        for (int s = 0; s < series.Count; s++)
        {
            var color = Palette[s % Palette.Length];
            var pen = new Pen(new SolidColorBrush(color), 2);
            pen.Freeze();

            var pts = series[s].Points;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool first = true;
                foreach (var (time, value) in pts)
                {
                    double x = plot.Left + (time - tMin).TotalMilliseconds / tSpan * plot.Width;
                    double y = plot.Bottom - (value - vMin) / (vMax - vMin) * plot.Height;
                    if (first) { ctx.BeginFigure(new Point(x, y), false, false); first = false; }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geometry.Freeze();
            if (pts.Count == 1)   // 单点画圆点
                dc.DrawEllipse(new SolidColorBrush(color), null, MapPoint(pts[0], plot, tMin, tSpan, vMin, vMax), 3, 3);
            else
                dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static Point MapPoint((DateTime Time, double Value) p, Rect plot, DateTime tMin, double tSpan,
        double vMin, double vMax) => new(
        plot.Left + (p.Time - tMin).TotalMilliseconds / tSpan * plot.Width,
        plot.Bottom - (p.Value - vMin) / (vMax - vMin) * plot.Height);

    private static void DrawLegend(DrawingContext dc, IReadOnlyList<ChartSeries> series, int width)
    {
        const float fontSize = 12;
        var texts = series.Select(s => MakeText(s.Legend, fontSize)).ToList();
        double boxW = texts.Max(t => t.Width) + 44;
        double boxH = series.Count * 22 + 14;
        double boxX = width - boxW - 28, boxY = 64;

        dc.DrawRectangle(Brushes.White, new Pen(GridBrush, 1), new Rect(boxX, boxY, boxW, boxH));

        for (int i = 0; i < series.Count; i++)
        {
            double y = boxY + 10 + i * 22;
            var color = Palette[i % Palette.Length];
            var linePen = new Pen(new SolidColorBrush(color), 3);
            linePen.Freeze();
            dc.DrawLine(linePen, new Point(boxX + 10, y + 8), new Point(boxX + 30, y + 8));
            dc.DrawText(texts[i], new Point(boxX + 38, y));
        }
    }

    private static double MapX(DateTime t, Rect plot, DateTime tMin, double tSpan)
        => plot.Left + (t - tMin).TotalMilliseconds / tSpan * plot.Width;

    private static FormattedText MakeText(string text, float fontSize, bool bold = false)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            fontSize, TextBrush, pixelsPerDip: 1.0);

    private static string FormatValue(double v)
    {
        double a = Math.Abs(v);
        return a >= 1e6 || a <= 1e-4
            ? v.ToString("E2", CultureInfo.InvariantCulture)
            : Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }
}
