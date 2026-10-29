using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UiTreeMonitor.Core.Filters;

namespace UiTreeMonitor.App;

/// <summary>
/// 值提取规则行 VM：Sample 预填当前值，用户在其中插入截取标记（或改为 re:正则）；
/// Preview 实时显示提取结果；Rule 编译为锚点正则（存入任务配置）。
/// </summary>
public sealed class ExtractRuleItem : INotifyPropertyChanged
{
    private readonly string _marker;
    private string _sample = "";

    public ExtractRuleItem(string marker, string sample)
    {
        _marker = marker;
        _sample = sample;
    }

    /// <summary>示例文本（含用户插入的标记对；或 re: 开头的正则）。</summary>
    public string Sample
    {
        get => _sample;
        set { _sample = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview))); }
    }

    /// <summary>
    /// 编译后的提取规则：re: 开头 → 剥前缀存正则；含成对标记 → 锚点正则；均否 → null（无效行，不保存）。
    /// </summary>
    public string? Rule => Sample.TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase)
        ? Sample.Trim()[3..]
        : ValueExtractor.CompileFromMarkedSample(Sample, _marker);

    /// <summary>提取预览（对示例自身应用规则），随 Sample 变化实时刷新。</summary>
    public string Preview
    {
        get
        {
            var rule = Rule;
            if (rule is null)
                return $"（未识别规则：请在值中用 {_marker}…{_marker} 圈出要提取的部分，或以 re: 开头写正则）";
            var result = ValueExtractor.ExtractOne(Sample, rule);
            return result.Length > 0 ? $"提取预览：{result}" : "提取预览：（空，请检查标记位置或正则）";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
    /// <summary>值提取规则（锚点正则 / 正则），空=记录原始值。</summary>
    public IReadOnlyList<string> Extractors { get; private set; } = Array.Empty<string>();
    /// <summary>保存策略：每 N 个采样点批量写盘（未按点保存时=int.MaxValue，即不启用）。</summary>
    public int SaveBatchPoints { get; private set; } = 1;
    /// <summary>保存策略：最长 T 秒写盘一次（未按时间保存时=0，即不启用）。</summary>
    public int SaveIntervalSec { get; private set; } = 0;
    public bool Confirmed { get; private set; }

    public AddMonitorDialog(string monitorProperty, string processName, string? defaultMarker = null,
        string? sampleValue = null)
    {
        InitializeComponent();
        TxtMonitorProperty.Text = monitorProperty;
        TxtProcessName.Text = processName;
        MonitorProperty = monitorProperty;
        ProcessName = processName;
        TxtSampleValue.Text = sampleValue ?? "";
        // 任务名默认：进程-属性（属性中的 . 换成 -，可自由编辑）
        TxtTaskName.Text = $"{processName}-{monitorProperty.Replace('.', '-')}";
        // 默认一条规则：预填当前值（用户在其中插入标记圈出要提取的部分）
        _marker = UiTreeMonitor.Persistence.AppSettings.NormalizeMarker(defaultMarker);
        _rules.Add(new ExtractRuleItem(_marker, TxtSampleValue.Text));
        ExtractRules.ItemsSource = _rules;
        TxtExtractHint.Text = $"规则：点「插入标记」在示例值中要提取部分的前后各插一次 {_marker}" +
            $"（如 Value = \"{_marker}614.466{_marker}\" → 614.466，程序解析两处标记位置生成规则）；" +
            "或把文本改为 re:(\\d+\\.?\\d*) 形式的正则（取第一捕获组）。多条规则依次产出 值1、值2…；删除全部规则则记录原始值。";

        // 保存策略候选联动：切换单选时启用/禁用对应输入
        RbSaveByPoints.Checked += (_, _) => SetSaveModeEnabled(byPoints: true);
        RbSaveByTime.Checked += (_, _) => SetSaveModeEnabled(byPoints: false);
    }

    private void SetSaveModeEnabled(bool byPoints)
    {
        TxtSavePoints.IsEnabled = byPoints;
        TxtSaveSeconds.IsEnabled = !byPoints;
        CmbSaveTimeUnit.IsEnabled = !byPoints;
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
        => _rules.Add(new ExtractRuleItem(_marker, TxtSampleValue.Text));

    private void BtnRemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExtractRuleItem item)
            _rules.Remove(item);
    }

    /// <summary>
    /// 在规则行文本框中插入截取标记：选中文本 → 两端包裹标记；未选中 → 光标处插入一个标记
    /// （用户在要提取部分的前后各插一次，程序解析两处标记位置生成锚点规则）。
    /// </summary>
    private void BtnInsertMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || VisualTreeHelper.GetParent(btn) is not Panel panel) return;
        var box = panel.Children.OfType<TextBox>().FirstOrDefault();
        if (box is null) return;

        if (box.SelectionLength > 0)
        {
            int start = box.SelectionStart;
            string sel = box.SelectedText;
            box.Text = box.Text.Remove(start, box.SelectionLength)
                .Insert(start, _marker + sel + _marker);
            box.Select(start + _marker.Length, sel.Length);
        }
        else
        {
            int caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, _marker);
            box.CaretIndex = caret + _marker.Length;
        }
        box.Focus();
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

        // 值提取规则：编译为锚点正则（无效行剔除，保持顺序，去重）
        Extractors = _rules.Select(r => r.Rule)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct()
            .ToList();

        // 保存策略（候选二选一）：按点 或 按时间（单位换算成秒）
        if (RbSaveByPoints.IsChecked == true)
        {
            if (!int.TryParse(TxtSavePoints.Text.Trim(), out var pts)) pts = 1;
            SaveBatchPoints = Math.Max(1, pts);
            SaveIntervalSec = 0;
        }
        else
        {
            if (!int.TryParse(TxtSaveSeconds.Text.Trim(), out var secs)) secs = 5;
            SaveIntervalSec = Math.Max(1, (int)Math.Round(secs * UnitFactor(CmbSaveTimeUnit)));
            SaveBatchPoints = int.MaxValue; // 不按点数（缓冲点数永不达到）
        }

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
