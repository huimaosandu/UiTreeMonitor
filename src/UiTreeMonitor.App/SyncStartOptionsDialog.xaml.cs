// 同步采集组启动配置弹窗：间隔/时长/保存方式 三参数一次性设置，全组强制统一。
using System;
using System.Windows;
using System.Windows.Controls;

namespace UiTreeMonitor.App;

/// <summary>弹窗结果：组内统一应用的采集参数。</summary>
public sealed record SyncStartOptions(
    bool Confirmed,
    int IntervalMs,      // 统一采集间隔（≥100）
    int DurationSec,     // 统一监控时长（0=无限）
    int SaveBatchPoints, // 按点保存的点数（按时间保存时=int.MaxValue）
    int SaveIntervalSec);// 按时间保存的秒数（按点保存时=0，即不启用）

public partial class SyncStartOptionsDialog : Window
{
    /// <summary>最终结果（确定后有效）。</summary>
    public SyncStartOptions Result { get; private set; } = new(false, 500, 0, 10, 0);

    public SyncStartOptionsDialog()
    {
        InitializeComponent();
        // 保存方式候选联动：切换单选时启用/禁用对应输入（与新建任务弹窗同款交互）
        RbSaveByPoints.Checked += (_, _) => SetSaveModeEnabled(byPoints: true);
        RbSaveByTime.Checked += (_, _) => SetSaveModeEnabled(byPoints: false);
    }

    private void SetSaveModeEnabled(bool byPoints)
    {
        TxtSavePoints.IsEnabled = byPoints;
        TxtSaveSeconds.IsEnabled = !byPoints;
        CmbSaveTimeUnit.IsEnabled = !byPoints;
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        // 间隔（最小 100ms）
        if (!int.TryParse(TxtIntervalValue.Text.Trim(), out var iv)) iv = 500;
        iv = Math.Max(100, (int)(iv * UnitFactor(CmbIntervalUnit)));

        // 时长（0=无限）
        if (!double.TryParse(TxtDurationValue.Text.Trim(), out var dv)) dv = 0;
        var durationSec = (int)Math.Max(0, dv * UnitFactor(CmbDurationUnit));

        // 保存策略（二选一）：按点 → IntervalSec=0；按时间 → BatchPoints=int.MaxValue（不启用按点）
        int batchPoints = int.MaxValue, intervalSec = 0;
        if (RbSaveByPoints.IsChecked == true)
        {
            if (!int.TryParse(TxtSavePoints.Text.Trim(), out var pts)) pts = 10;
            batchPoints = Math.Max(1, pts);
            intervalSec = 0;
        }
        else
        {
            if (!int.TryParse(TxtSaveSeconds.Text.Trim(), out var secs)) secs = 5;
            intervalSec = Math.Max(1, (int)(secs * UnitFactor(CmbSaveTimeUnit)));
        }

        Result = new SyncStartOptions(true, iv, durationSec, batchPoints, intervalSec);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>取"数字 + 单位下拉"组合的换算系数（ComboBoxItem.Tag）。</summary>
    private static double UnitFactor(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag is string s && double.TryParse(s, out var f) ? f : 1;
}
