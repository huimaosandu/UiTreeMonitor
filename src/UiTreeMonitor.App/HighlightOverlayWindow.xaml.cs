// 高亮 Overlay：在目标窗口元素上绘制蓝色高亮框 + 悬浮信息卡（模拟 accessibility-insights-windows）
using System;
using System.Drawing;
using System.Windows;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Targeting;

namespace UiTreeMonitor.App;

/// <summary>
/// 置顶透明 Overlay 窗口：跟随鼠标捕获到的元素矩形绘制蓝色边框，
/// 让用户直观看到当前选中的是哪个控件。不拦截鼠标、不抢焦点。
/// </summary>
public partial class HighlightOverlayWindow : Window
{
    /// <summary>边框额外外扩（物理像素→DIP 换算后），让框线正好压在元素边界上。</summary>
    private const double BorderInsetDip = 1.0;

    public HighlightOverlayWindow()
    {
        InitializeComponent();
        // 全屏透明窗口无法用 WPF 默认方法定位到任意屏幕，这里始终手动设置位置
        Loaded += (_, _) => { };
    }

    /// <summary>
    /// 把高亮框移动到元素矩形处。rect 为屏幕物理像素（UIA 原生坐标系），
    /// dpiScale 为 DIP 换算系数（物理像素 ÷ 96 缩放）。
    /// </summary>
    public void MoveTo(System.Drawing.Rectangle rect, double dpiScale, MouseCaptureResult? info)
    {
        if (dpiScale <= 0) dpiScale = 1.0;

        double leftDip = rect.Left / dpiScale - BorderInsetDip;
        double topDip = rect.Top / dpiScale - BorderInsetDip;
        double widthDip = rect.Width / dpiScale + BorderInsetDip * 2;
        double heightDip = rect.Height / dpiScale + BorderInsetDip * 2;

        Left = leftDip;
        Top = topDip;
        Width = Math.Max(4, widthDip);
        Height = Math.Max(4, heightDip);

        if (info is not null && info.IsValid)
        {
            InfoType.Text = info.ControlType;
            InfoName.Text = info.Name ?? info.AutomationId ?? info.ClassName ?? "(无名称)";
            var focus = info.IsKeyboardFocusable switch
            {
                true => "可聚焦",
                false => "不可聚焦",
                null => "聚焦状态未知",
            };
            var extra = info.AutomationId is not null ? $"AutomationId: {info.AutomationId}" : "";
            if (info.ProcessName is not null)
                extra = string.IsNullOrEmpty(extra) ? info.ProcessName : $"{extra}  |  {info.ProcessName}";
            InfoExtra.Text = $"{focus}  |  {extra}".TrimEnd(' ', '|');
            InfoCard.Visibility = Visibility.Visible;
        }
        else
        {
            InfoCard.Visibility = Visibility.Collapsed;
        }

        if (!IsVisible) Show();
    }

    /// <summary>隐藏高亮框（停止捕获 / 捕获失效时调用）。</summary>
    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }
}
