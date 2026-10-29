using System.Windows;

namespace UiTreeMonitor.App;

/// <summary>"添加监控对象"弹窗（右键 Details 属性/Pattern 时弹出）。</summary>
public partial class AddMonitorDialog : Window
{
    public string MonitorProperty { get; private set; } = "";
    public string ProcessName { get; private set; } = "";
    public int IntervalMs { get; private set; } = 1000;
    public int DurationSec { get; private set; } = 0; // 0=无限
    public bool Confirmed { get; private set; }

    public AddMonitorDialog(string monitorProperty, string processName)
    {
        InitializeComponent();
        TxtMonitorProperty.Text = monitorProperty;
        TxtProcessName.Text = processName;
        MonitorProperty = monitorProperty;
        ProcessName = processName;
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        // 采集间隔
        if (!string.IsNullOrEmpty(TxtCustomInterval.Text) && int.TryParse(TxtCustomInterval.Text, out var customInt))
            IntervalMs = customInt;
        else if (RbInterval500.IsChecked == true) IntervalMs = 500;
        else if (RbInterval1000.IsChecked == true) IntervalMs = 1000;
        else if (RbInterval3000.IsChecked == true) IntervalMs = 3000;
        else IntervalMs = 1000;

        // 监控时长
        if (!string.IsNullOrEmpty(TxtCustomDuration.Text) && int.TryParse(TxtCustomDuration.Text, out var customDur))
            DurationSec = customDur;
        else if (RbDuration60.IsChecked == true) DurationSec = 60;
        else if (RbDuration300.IsChecked == true) DurationSec = 300;
        else DurationSec = 0;

        if (IntervalMs < 100) IntervalMs = 100;
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
