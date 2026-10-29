using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UiTreeMonitor.Core.Filters;
using UiTreeMonitor.Persistence;

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
        set
        {
            _sample = value;
            // 必须同时通知 Sample（程序赋值时 TextBox 回读）与 Preview（预览刷新）
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sample)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    /// <summary>
    /// 编译后的提取规则：re: 开头 → 剥前缀存正则；含成对标记 → 锚点正则；均否 → null（无效行，不保存）。
    /// </summary>
    public string? Rule => Sample.TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase)
        ? Sample.Trim()[3..]
        : ValueExtractor.CompileFromMarkedSample(Sample, _marker);

    /// <summary>
    /// 提取预览（对示例剥离标记后的原文应用规则），随 Sample 变化实时刷新。
    /// 自动识别提取值类型并标注：数值（可画数据图）/ 文本。
    /// </summary>
    public string Preview
    {
        get
        {
            var rule = Rule;
            if (rule is null)
                return $"（未识别规则：请在值中用 {_marker}…{_marker} 圈出要提取的部分，或以 re: 开头写正则）";
            var plain = Sample.Replace(_marker, ""); // 示例去标记 = 原始值形态（预览不含标记字符）
            var result = ValueExtractor.ExtractOne(plain, rule);
            if (result.Length == 0) return "提取预览：（空，请检查标记位置或正则）";
            var type = double.TryParse(result.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _) ? "数值" : "文本";
            return $"提取预览：{result}（{type}）";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>监控目标下拉选项（Properties/Patterns 属性名 + 当前值示例）。</summary>
public sealed record TargetOption(string Name, string? Value);

/// <summary>"添加监控对象"弹窗（右键 Details 属性/Pattern 时弹出）。</summary>
public partial class AddMonitorDialog : Window
{
    private readonly ObservableCollection<ExtractRuleItem> _rules = new();
    private string _marker = "@@"; // 当前截取标记（构造时按全局设置初始化）
    private readonly Func<string, bool>? _nameExists; // 重名校验（弹窗内提示，不关弹窗）
    private readonly string _monitorProperty; // 下拉初始选中项（兜底：下拉无此项时用）
    private readonly bool _viewMode; // 只读查看模式（任务右键"查看监控配置"）：禁全部编辑与确定添加

    public string TaskName { get; private set; } = "";
    /// <summary>监控目标：下拉选择（Properties 属性名 / Pattern.子属性 路径）。</summary>
    public string MonitorProperty => (CmbMonitorProperty.SelectedItem as TargetOption)?.Name ?? _monitorProperty;
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
        string? sampleValue = null, Func<string, bool>? nameExists = null,
        IReadOnlyList<TargetOption>? targetOptions = null,
        TaskListItem? viewItem = null, SavePolicy? savePolicy = null)
    {
        InitializeComponent();
        // 打开即完整显示全部内容（超出屏幕时仍可滚动/拉伸：MaxHeight 限制 + 用户拖动后自动转手动尺寸）
        MaxHeight = SystemParameters.WorkArea.Height - 60;
        _nameExists = nameExists;
        _monitorProperty = monitorProperty;
        TxtProcessName.Text = processName;
        ProcessName = processName;
        TxtSampleValue.Text = sampleValue ?? "";

        // 监控目标下拉：当前元素 Properties + Patterns 的全部可监控属性；初始选中右键项
        var options = targetOptions is { Count: > 0 }
            ? targetOptions.ToList()
            : new List<TargetOption> { new(monitorProperty, sampleValue) };
        if (options.All(o => o.Name != monitorProperty))
            options.Insert(0, new TargetOption(monitorProperty, sampleValue));
        CmbMonitorProperty.ItemsSource = options;
        CmbMonitorProperty.SelectedItem = options.First(o => o.Name == monitorProperty);
        CmbMonitorProperty.SelectionChanged += CmbMonitorProperty_SelectionChanged;

        // 任务名默认：进程-属性（属性中的 . 换成 -，可自由编辑）
        TxtTaskName.Text = $"{processName}-{monitorProperty.Replace('.', '-')}";
        // 默认一条规则：预填当前值（用户在其中插入标记圈出要提取的部分）
        _marker = UiTreeMonitor.Persistence.AppSettings.NormalizeMarker(defaultMarker);
        _rules.Add(new ExtractRuleItem(_marker, TxtSampleValue.Text));
        ExtractRules.ItemsSource = _rules;
        TxtExtractHint.Text = $"规则：点「插入标记」在示例值中要提取部分的前后各插一次 {_marker}" +
            $"（如 Value = \"{_marker}614.466{_marker}\" → 614.466，程序解析两处标记位置生成规则）；" +
            "或把文本改为 re:(\\d+\\.?\\d*) 形式的正则（取第一捕获组）。多条规则依次产出 值1、值2…；删除全部规则则记录原始值。" +
            "标记夹在任意位置均可提取，值类型自动识别（数值可画数据图，文本/布尔仅采集）。";

        // 保存策略候选联动：切换单选时启用/禁用对应输入
        RbSaveByPoints.Checked += (_, _) => SetSaveModeEnabled(byPoints: true);
        RbSaveByTime.Checked += (_, _) => SetSaveModeEnabled(byPoints: false);

        // 只读查看模式（任务右键"查看监控配置"）：回填任务实际参数，禁全部编辑与"确定添加"
        _viewMode = viewItem is not null;
        if (viewItem is not null)
            PrefillForView(viewItem, savePolicy);
    }

    /// <summary>
    /// 查看任务配置（只读）：回填任务的实际参数（目标/间隔/时长/提取规则/保存策略），
    /// 禁用全部输入与"确定添加"按钮——仅供查看，不创建新任务。
    /// </summary>
    private void PrefillForView(TaskListItem item, SavePolicy? savePolicy)
    {
        Title = $"监控配置（只读）- {item.Name}";
        BtnOK.IsEnabled = false;
        BtnOK.ToolTip = "查看模式：配置不可修改";
        BtnCancel.Content = "关闭";

        TxtTaskName.Text = item.Name;
        // 目标下拉只保留该任务的监控属性；最近值只读参考
        var opt = new TargetOption(item.MonitorProperty, item.CurrentValue);
        CmbMonitorProperty.ItemsSource = new List<TargetOption> { opt };
        CmbMonitorProperty.SelectedItem = opt;
        TxtSampleValue.Text = item.CurrentValue;

        // 间隔/时长：以基础单位（ms / s）回显
        TxtIntervalValue.Text = item.IntervalMs.ToString();
        CmbIntervalUnit.SelectedIndex = 0;
        TxtDurationValue.Text = item.DurationSec.ToString();
        CmbDurationUnit.SelectedIndex = 0;

        // 值提取规则：任务保存的是编译后的正则，以 re: 形式只读展示；无规则=记录原始值
        _rules.Clear();
        if (item.Config.Extractors is { Count: > 0 })
            foreach (var rx in item.Config.Extractors)
                _rules.Add(new ExtractRuleItem(_marker, $"re:{rx}"));
        else
            _rules.Add(new ExtractRuleItem(_marker, item.CurrentValue));

        // 保存策略回显（BatchPoints=int.MaxValue 表示按时间保存）
        if (savePolicy is { } sp)
        {
            if (sp.BatchPoints != int.MaxValue)
            {
                RbSaveByPoints.IsChecked = true;
                TxtSavePoints.Text = sp.BatchPoints.ToString();
            }
            else
            {
                RbSaveByTime.IsChecked = true;
                TxtSaveSeconds.Text = Math.Max(1, sp.IntervalSec).ToString();
                CmbSaveTimeUnit.SelectedIndex = sp.IntervalSec >= 3600 ? 2 : sp.IntervalSec >= 60 ? 1 : 0;
            }
        }

        // 统一禁用全部编辑控件（只读展示）
        foreach (var c in new Control[] { TxtTaskName, CmbMonitorProperty, TxtIntervalValue, CmbIntervalUnit,
                TxtDurationValue, CmbDurationUnit, ExtractRules, BtnAddRule, RbSaveByPoints, TxtSavePoints,
                RbSaveByTime, TxtSaveSeconds, CmbSaveTimeUnit, BtnOK })
            c.IsEnabled = false;
    }

    /// <summary>
    /// 切换监控目标：所有规则行示例跟随新目标的当前值。
    /// re: 正则行与值无关 → 保留；带标记行 → 用旧锚点在新值上重新定位标记（迁移失败则重置为纯新值）。
    /// </summary>
    private void CmbMonitorProperty_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewMode) return; // 只读查看模式：不联动改写规则行
        if (CmbMonitorProperty.SelectedItem is not TargetOption opt || opt.Value is null) return;
        string newSample = opt.Value;
        TxtSampleValue.Text = newSample;
        foreach (var r in _rules)
        {
            if (r.Sample.TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase))
                continue; // 正则规则与具体值无关，保留
            r.Sample = RemarkSample(newSample, r.Sample) ?? newSample;
        }
    }

    /// <summary>
    /// 标记迁移：用旧示例（带标记）编译出的锚点正则在新值上定位捕获组位置，
    /// 在新值对应位置重插标记对。迁移成功返回新示例（带标记）；锚点匹配失败返回 null（调用方重置为纯新值）。
    /// </summary>
    private string? RemarkSample(string newSample, string oldMarkedSample)
    {
        var pattern = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(oldMarkedSample, _marker);
        if (pattern is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(newSample, pattern);
        if (!m.Success || m.Groups.Count < 2 || !m.Groups[1].Success) return null;

        var g = m.Groups[1];
        return newSample[..g.Index] + _marker + g.Value + _marker + newSample[(g.Index + g.Length)..];
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
        if (sender is not FrameworkElement fe) return;
        // 向上找到本规则行的 ContentPresenter（ItemsControl 项容器），再向下找其中唯一的示例 TextBox
        // （多行布局后按钮与文本框不在同一 Panel，不能再用 Parent.Children 定位）
        DependencyObject? cur = fe;
        while (cur is not null and not ContentPresenter)
            cur = VisualTreeHelper.GetParent(cur);
        var box = FindDescendantTextBox(cur);
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

    /// <summary>在可视化子树中深度查找第一个 TextBox（规则行内唯一示例框）。</summary>
    private static TextBox? FindDescendantTextBox(DependencyObject? root)
    {
        if (root is null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb) return tb;
            var deeper = FindDescendantTextBox(child);
            if (deeper is not null) return deeper;
        }
        return null;
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

        // ★ 重名检查：弹窗内提示并留在本弹窗（保留已编辑内容），用户改完再点确定
        if (_nameExists?.Invoke(TaskName) == true)
        {
            MessageBox.Show(this, $"任务名称 \"{TaskName}\" 已存在。\n请修改任务名称后重新点击\"确定添加\"。",
                "任务名称重复", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtTaskName.Focus();
            TxtTaskName.SelectAll();
            return;
        }

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
