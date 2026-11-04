// 步骤编辑弹窗：按 Kind 显示循环/延时/定时/触发器配置区，写回 StepNode
// 触发器目标元素由蓝框烧杯捕获（随鼠标捕获/录制中悬停点烧杯），弹窗只读展示 + 实时读值验证
using System;
using System.Windows;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.App.Controls;

/// <summary>步骤节点编辑弹窗（循环体/延时/定时器/元素触发器）。</summary>
public partial class StepEditDialog : Window
{
    private readonly StepNode _node;
    private readonly string _elementSpec;              // 烧杯捕获的目标元素 spec（spec:v1|…）
    private readonly string _elementName;              // 目标元素显示名
    private readonly Func<string, string?>? _readProperty; // 属性名 → 实时值（"读取当前值"用）

    public bool Saved { get; private set; }

    /// <summary>触发器节点须传入烧杯捕获的元素定位；循环/延时/定时元素参数可传空。</summary>
    public StepEditDialog(StepNode node, string elementSpec = "", string elementName = "",
        Func<string, string?>? readProperty = null)
    {
        InitializeComponent();
        _node = node;
        _elementSpec = elementSpec;
        _elementName = elementName;
        _readProperty = readProperty;

        TxtTitle.Text = node.Kind switch
        {
            StepKinds.Loop => "编辑循环体",
            StepKinds.Delay => "编辑延时",
            StepKinds.Timer => "编辑定时器",
            StepKinds.Trigger => "编辑元素触发器",
            StepKinds.Screenshot => "编辑截图步骤",
            _ => "编辑步骤",
        };

        switch (node.Kind)
        {
            case StepKinds.Loop:
                PnlLoop.Visibility = Visibility.Visible;
                TxtLoopCount.Text = node.LoopCount.ToString();
                TxtLoopInterval.Text = node.LoopIntervalMs.ToString();
                break;
            case StepKinds.Delay:
                PnlDelay.Visibility = Visibility.Visible;
                // 反填：从大到小选第一个"整除不丢精度"的人类单位（h→min→s→ms），小数最多 3 位去尾零
                if (node.DelayMs >= 3_600_000 && node.DelayMs % 3_600_000 == 0)
                { CboDelayUnit.SelectedIndex = 3; TxtDelayValue.Text = (node.DelayMs / 3_600_000).ToString(); }
                else if (node.DelayMs >= 3_600_000)
                { CboDelayUnit.SelectedIndex = 3; TxtDelayValue.Text = (node.DelayMs / 3_600_000.0).ToString("0.###"); }
                else if (node.DelayMs >= 60_000)
                { CboDelayUnit.SelectedIndex = 2; TxtDelayValue.Text = (node.DelayMs / 60_000.0).ToString("0.###"); }
                else if (node.DelayMs >= 1_000)
                { CboDelayUnit.SelectedIndex = 1; TxtDelayValue.Text = (node.DelayMs / 1_000.0).ToString("0.###"); }
                else
                { CboDelayUnit.SelectedIndex = 0; TxtDelayValue.Text = node.DelayMs.ToString(); }
                break;
            case StepKinds.Timer:
                PnlTimer.Visibility = Visibility.Visible;
                TxtTriggerAt.Text = node.TriggerAt == default
                    ? DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss")
                    : node.TriggerAt.ToString("yyyy-MM-dd HH:mm:ss");
                break;
            case StepKinds.Trigger:
                PnlTrigger.Visibility = Visibility.Visible;
                var t = node.Trigger;
                // 目标元素：新建=烧杯刚捕获的元素；编辑=已保存的触发器配置
                var spec = t is not null && !string.IsNullOrEmpty(t.ElementSpec) ? t.ElementSpec : _elementSpec;
                var name = t is not null && !string.IsNullOrEmpty(t.ElementName) ? t.ElementName : _elementName;
                LblElementName.Text = string.IsNullOrEmpty(name) ? "(未指定)" : name;
                LblElementSpec.Text = spec;
                _elementSpecUsed = spec;
                CboProperty.SelectedIndex = (t?.Property ?? "Value") switch
                {
                    "Name" => 1, "RangeValue" => 2, "ToggleState" => 3, "ExpandCollapseState" => 4, "IsEnabled" => 5, _ => 0,
                };
                CboCondition.SelectedIndex = (t?.Condition ?? "Equals") switch
                {
                    "NotEquals" => 1, "Contains" => 2, "Changed" => 3, "Greater" => 4, "Less" => 5, _ => 0,
                };
                TxtExpected.Text = t?.ExpectedValue ?? "";
                TxtTimeout.Text = (t?.TimeoutSec ?? 60).ToString();
                break;

            case StepKinds.Screenshot:
                PnlScreenshot.Visibility = Visibility.Visible;
                // 目标元素：新建=剪刀刚捕获的元素；编辑=已保存的截图配置（spec 不可改，重捕获=删除后重新插入）
                var shotSpec = !string.IsNullOrEmpty(node.ScreenshotSpec) ? node.ScreenshotSpec : _elementSpec;
                var shotName = !string.IsNullOrEmpty(node.ScreenshotName) ? node.ScreenshotName : _elementName;
                LblShotElementName.Text = string.IsNullOrEmpty(shotName) ? "(未指定)" : shotName;
                LblShotElementSpec.Text = shotSpec;
                _elementSpecUsed = shotSpec;
                TxtShotDir.Text = node.ScreenshotDir;
                break;
        }
    }

    /// <summary>实际使用的元素 spec（编辑已有触发器时优先其保存值）。</summary>
    private string _elementSpecUsed = "";

    /// <summary>读取当前值：按烧杯捕获的元素定位实时读取，验证定位与条件配置。</summary>
    private void BtnReadCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_readProperty is null)
        {
            TxtError.Text = "无可用的元素解析通道（开始录制或随鼠标捕获后再试）";
            return;
        }
        try
        {
            var prop = SelectedPropertyName;
            var value = _readProperty(prop);
            if (value is null)
            {
                // 元素未找到（可能已关闭/移位）或该属性无值——区分由调用方闭包折叠，此处统一提示
                TxtError.Foreground = System.Windows.Media.Brushes.DarkOrange;
                TxtError.Text = "未读到值：元素未找到（可能已关闭/移位）或该属性无值";
                return;
            }
            TxtExpected.Text = value;
            TxtError.Foreground = System.Windows.Media.Brushes.Green;
            TxtError.Text = $"当前值已填入期望值：{value}";
        }
        catch (Exception ex)
        {
            TxtError.Foreground = System.Windows.Media.Brushes.Red;
            TxtError.Text = $"读取失败: {ex.Message}";
        }
    }

    /// <summary>监控属性下拉短名（Value/Name/RangeValue/ToggleState/ExpandCollapseState/IsEnabled）。</summary>
    private string SelectedPropertyName => CboProperty.SelectedIndex switch
    {
        1 => "Name", 2 => "RangeValue", 3 => "ToggleState", 4 => "ExpandCollapseState", 5 => "IsEnabled", _ => "Value",
    };

    /// <summary>选择截图保存目录（运行时自动保存 img-时间戳.png 到该目录）。</summary>
    private void BtnShotBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = System.IO.Directory.Exists(TxtShotDir.Text) ? TxtShotDir.Text : null,
        };
        if (dlg.ShowDialog(this) == true)
            TxtShotDir.Text = dlg.FolderName;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Foreground = System.Windows.Media.Brushes.Red;
        TxtError.Text = "";
        switch (_node.Kind)
        {
            case StepKinds.Loop:
                if (!int.TryParse(TxtLoopCount.Text, out var lc) || lc < 1) { TxtError.Text = "循环次数须为 ≥1 的整数"; return; }
                if (!int.TryParse(TxtLoopInterval.Text, out var li) || li < 0) { TxtError.Text = "轮间隔须为 ≥0 的整数（毫秒）"; return; }
                _node.LoopCount = lc;
                _node.LoopIntervalMs = li;
                break;

            case StepKinds.Delay:
                if (!double.TryParse(TxtDelayValue.Text.Trim(), out var dv) || dv < 0)
                { TxtError.Text = "延时时长须为 ≥0 的数值"; return; }
                // 按所选单位换算毫秒写回（0.### 格式反填可无损还原）
                var unitMs = CboDelayUnit.SelectedIndex switch { 1 => 1_000.0, 2 => 60_000.0, 3 => 3_600_000.0, _ => 1.0 };
                var totalMs = (int)Math.Round(dv * unitMs);
                if (totalMs < 0) { TxtError.Text = "延时时长数值过大"; return; }
                _node.DelayMs = totalMs;
                break;

            case StepKinds.Timer:
                if (!DateTime.TryParse(TxtTriggerAt.Text, out var at)) { TxtError.Text = "时刻格式无效，应为 yyyy-MM-dd HH:mm:ss"; return; }
                _node.TriggerAt = at;
                break;

            case StepKinds.Trigger:
                if (string.IsNullOrEmpty(_elementSpecUsed)) { TxtError.Text = "目标元素未捕获（录制/捕获中点蓝框烧杯按钮）"; return; }
                if (!int.TryParse(TxtTimeout.Text, out var to) || to < 0) { TxtError.Text = "超时须为 ≥0 的整数（秒）"; return; }
                var cond = CboCondition.SelectedIndex switch
                {
                    1 => "NotEquals", 2 => "Contains", 3 => "Changed", 4 => "Greater", 5 => "Less", _ => "Equals",
                };
                var propName = SelectedPropertyName;
                var expected = TxtExpected.Text;
                if (cond is "Equals" or "NotEquals" or "Contains" or "Greater" or "Less" && string.IsNullOrEmpty(expected)
                    && propName is not "ToggleState" and not "ExpandCollapseState" and not "IsEnabled")
                { TxtError.Text = "该条件需要填写期望值"; return; }
                _node.Trigger = new ElementTriggerSpec
                {
                    ElementSpec = _elementSpecUsed,
                    ElementName = string.IsNullOrEmpty(_elementName) && string.IsNullOrEmpty(LblElementName.Text)
                        ? "(元素)" : (string.IsNullOrEmpty(_elementName) ? LblElementName.Text : _elementName),
                    Property = propName,
                    Condition = cond,
                    ExpectedValue = expected,
                    TimeoutSec = to,
                };
                break;

            case StepKinds.Screenshot:
                if (string.IsNullOrEmpty(_elementSpecUsed)) { TxtError.Text = "目标元素未捕获（捕获中点蓝框右上角剪刀按钮）"; return; }
                var dir = TxtShotDir.Text.Trim();
                if (dir.Length == 0) { TxtError.Text = "请填写或选择截图保存目录"; return; }
                _node.ScreenshotSpec = _elementSpecUsed;
                _node.ScreenshotName = string.IsNullOrEmpty(_elementName)
                    ? (string.IsNullOrEmpty(LblShotElementName.Text) || LblShotElementName.Text == "(未指定)" ? "(元素)" : LblShotElementName.Text)
                    : _elementName;
                _node.ScreenshotDir = dir;
                break;
        }
        Saved = true;
        DialogResult = true;
        Close();
    }
}
