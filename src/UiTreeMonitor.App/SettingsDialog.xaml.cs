using System.Windows;
using UiTreeMonitor.Persistence;

namespace UiTreeMonitor.App;

/// <summary>设置弹窗：当前仅"截取字符"一项，留扩展空间。</summary>
public partial class SettingsDialog : Window
{
    /// <summary>确认后的截取标记（已规范；点取消为 null）。</summary>
    public string? ExtractMarker { get; private set; }

    public SettingsDialog(string currentMarker)
    {
        InitializeComponent();
        TxtExtractMarker.Text = currentMarker;
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = AppSettings.NormalizeMarker(TxtExtractMarker.Text);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = null;
        DialogResult = false;
        Close();
    }
}
