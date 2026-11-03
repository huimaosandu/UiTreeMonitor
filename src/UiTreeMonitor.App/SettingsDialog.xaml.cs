using System.Windows;
using UiTreeMonitor.Persistence;

namespace UiTreeMonitor.App;

/// <summary>设置弹窗：截取字符 + 自动录制模式（UIA 语义事件=默认 / 鼠标键盘动作）。</summary>
public partial class SettingsDialog : Window
{
    /// <summary>确认后的截取标记（已规范；点取消为 null）。</summary>
    public string? ExtractMarker { get; private set; }

    /// <summary>确认后的录制模式（"uia"/"input"，已规范；点取消为 null）。</summary>
    public string? RecordMode { get; private set; }

    /// <summary>确认后的鼠标键盘细项（MouseLeft/MouseRight/Keyboard；点取消为 null）。</summary>
    public (bool MouseLeft, bool MouseRight, bool Keyboard)? InputOptions { get; private set; }

    public SettingsDialog(string currentMarker,
        string recordMode = "uia",
        bool recordMouseLeft = true, bool recordMouseRight = true,
        bool recordKeyboard = true)
    {
        InitializeComponent();
        TxtExtractMarker.Text = currentMarker;
        var mode = AppSettings.NormalizeRecordMode(recordMode);
        (mode == "uia" ? RbModeUia : RbModeInput).IsChecked = true;
        CkRecordMouseLeft.IsChecked = recordMouseLeft;
        CkRecordMouseRight.IsChecked = recordMouseRight;
        CkRecordKeyboard.IsChecked = recordKeyboard;
    }

    private void RbModeUia_Checked(object sender, RoutedEventArgs e) => PnlInputOptions.IsEnabled = false;

    private void RbModeInput_Checked(object sender, RoutedEventArgs e) => PnlInputOptions.IsEnabled = true;

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = AppSettings.NormalizeMarker(TxtExtractMarker.Text);
        RecordMode = RbModeInput.IsChecked == true ? "input" : "uia";
        InputOptions = (
            CkRecordMouseLeft.IsChecked == true,
            CkRecordMouseRight.IsChecked == true,
            CkRecordKeyboard.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = null;
        RecordMode = null;
        InputOptions = null;
        DialogResult = false;
        Close();
    }
}
