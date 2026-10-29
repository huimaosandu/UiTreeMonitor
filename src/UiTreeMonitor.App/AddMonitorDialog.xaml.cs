using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UiTreeMonitor.App;

/// <summary>值提取规则行 VM（弹窗动态行用）。</summary>
public sealed class ExtractRuleItem
{
    public string Rule { get; set; } = "@@";
}

/// <summary>"添加监控对象"弹窗（右键 Details 属性/Pattern 时弹出）。</summary>
public partial class AddMonitorDialog : Window
{
    private readonly ObservableCollection<ExtractRuleItem> _rules = new();
    private string _marker = "@@"; // 当前截取标记（构造时按全局设置初始化）

    public string TaskName { get; private set; } = "";
    public string MonitorProperty { get; private set; } = "";
    public string ProcessName { get; private set; } = "";
    public int IntervalMs { get; private set; } = 500;
    public int DurationSec { get; private set; } = 0; // 0=无限
    /// <summary>值提取规则（@@标记对 / re:正则），空=记录原始值。</summary>
    public IReadOnlyList<string> Extractors { get; private set; } = Array.Empty<string>();
    /// <summary>保存策略：每 N 个采样点批量写盘（1=每点即存）。</summary>
    public int SaveBatchPoints { get; private set; } = 1;
    /// <summary>保存策略：最长 T 秒写盘一次（0=不按时间）。与点数条件先到先触发。</summary>
    public int SaveIntervalSec { get; private set; } = 5;
    public bool Confirmed { get; private set; }

    public AddMonitorDialog(string monitorProperty, string processName, string? defaultMarker = null)
    {
        InitializeComponent();
        TxtMonitorProperty.Text = monitorProperty;
        TxtProcessName.Text = processName;
        MonitorProperty = monitorProperty;
        ProcessName = processName;
        // 任务名默认：进程-属性（属性中的 . 换成 -，可自由编辑）
        TxtTaskName.Text = $"{processName}-{monitorProperty.Replace('.', '-')}";
        // 默认一条截取标记规则（全局设置可改，默认 @@）
        _marker = UiTreeMonitor.Persistence.AppSettings.NormalizeMarker(defaultMarker);
        _rules.Add(new ExtractRuleItem { Rule = _marker });
        ExtractRules.ItemsSource = _rules;
        TxtExtractHint.Text = $"规则示例：{_marker} 截取 {_marker}…{_marker} 之间的值" +
            $"（如 {_marker}614.466{_marker} → 614.466）；re:(\\d+\\.?\\d*) 直接写正则取第一捕获组。" +
            "多条规则依次产出 值1、值2…；删除全部规则则记录原始值。";
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
        => _rules.Add(new ExtractRuleItem { Rule = _marker });

    private void BtnRemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExtractRuleItem item)
            _rules.Remove(item);
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

        // 值提取规则（去空行，保持顺序）
        Extractors = _rules.Select(r => r.Rule.Trim()).Where(s => s.Length > 0).Distinct().ToList();

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
