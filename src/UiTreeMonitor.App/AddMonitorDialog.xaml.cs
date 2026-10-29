using System;
using System.Windows;
using System.Windows.Controls;

namespace UiTreeMonitor.App;

/// <summary>"添加监控对象"弹窗（右键 Details 属性/Pattern 时弹出）。</summary>
public partial class AddMonitorDialog : Window
{
    public string TaskName { get; private set; } = "";
    public string MonitorProperty { get; private set; } = "";
    public string ProcessName { get; private set; } = "";
    public int IntervalMs { get; private set; } = 500;
    public int DurationSec { get; private set; } = 0; // 0=无限
    /// <summary>保存策略：每 N 个采样点批量写盘（1=每点即存）。</summary>
    public int SaveBatchPoints { get; private set; } = 1;
    /// <summary>保存策略：最长 T 秒写盘一次（0=不按时间）。与点数条件先到先触发。</summary>
    public int SaveIntervalSec { get; private set; } = 5;
    public bool Confirmed { get; private set; }

    public AddMonitorDialog(string monitorProperty, string processName)
    {
        InitializeComponent();
        TxtMonitorProperty.Text = monitorProperty;
        TxtProcessName.Text = processName;
        MonitorProperty = monitorProperty;
        ProcessName = processName;
        // 任务名默认：进程-属性（属性中的 . 换成 -，可自由编辑）
        TxtTaskName.Text = $"{processName}-{monitorProperty.Replace('.', '-')}";
    }

    /// <summary>取"数字 + 单位下拉"组合的换算系数（ComboBoxItem.Tag）。</summary>
    private static double UnitFactor(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag is string s && double.TryParse(s, out var f) ? f : 1;

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        // 任务名称（空则回退默认）
        TaskName = string.IsNullOrWhiteSpace(TxtTaskName.Text)
            ? $"{ProcessName}-{MonitorProperty.Replace('.', '-')}"
            : TxtTaskName.Text.Trim();

        // 采集间隔：数字 × 单位（最小 100ms）
        if (!double.TryParse(TxtIntervalValue.Text.Trim(), out var iv)) iv = 500;
        IntervalMs = (int)Math.Max(100, iv * UnitFactor(CmbIntervalUnit));

        // 监控时长：数字 × 单位（0=无限）
        if (!double.TryParse(TxtDurationValue.Text.Trim(), out var dv)) dv = 0;
        DurationSec = (int)Math.Max(0, dv * UnitFactor(CmbDurationUnit));

        // 保存策略
        if (!int.TryParse(TxtSavePoints.Text.Trim(), out var pts)) pts = 1;
        SaveBatchPoints = Math.Max(1, pts);
        if (!int.TryParse(TxtSaveSeconds.Text.Trim(), out var secs)) secs = 5;
        SaveIntervalSec = Math.Max(0, secs);

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
