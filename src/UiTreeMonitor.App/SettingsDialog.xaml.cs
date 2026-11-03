using System.Windows;
using UiTreeMonitor.Persistence;

namespace UiTreeMonitor.App;

/// <summary>设置弹窗：截取字符 + 自动录制捕获选项（鼠标左右键/键盘/UIA 事件）。</summary>
public partial class SettingsDialog : Window
{
    /// <summary>确认后的截取标记（已规范；点取消为 null）。</summary>
    public string? ExtractMarker { get; private set; }

    /// <summary>确认后的录制捕获选项（MouseLeft/MouseRight/Keyboard/UiaEvents；点取消为 null）。</summary>
    public (bool MouseLeft, bool MouseRight, bool Keyboard, bool UiaEvents)? CaptureOptions { get; private set; }

    public SettingsDialog(string currentMarker,
        bool recordMouseLeft = true, bool recordMouseRight = true,
        bool recordKeyboard = true, bool recordUiaEvents = false)
    {
        InitializeComponent();
        TxtExtractMarker.Text = currentMarker;
        CkRecordMouseLeft.IsChecked = recordMouseLeft;
        CkRecordMouseRight.IsChecked = recordMouseRight;
        CkRecordKeyboard.IsChecked = recordKeyboard;
        CkRecordUiaEvents.IsChecked = recordUiaEvents;
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = AppSettings.NormalizeMarker(TxtExtractMarker.Text);
        CaptureOptions = (
            CkRecordMouseLeft.IsChecked == true,
            CkRecordMouseRight.IsChecked == true,
            CkRecordKeyboard.IsChecked == true,
            CkRecordUiaEvents.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ExtractMarker = null;
        CaptureOptions = null;
        DialogResult = false;
        Close();
    }
}
