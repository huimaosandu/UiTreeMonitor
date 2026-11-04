// 步骤编辑弹窗：按 Kind 显示循环/延时/定时/触发器配置区，写回 StepNode
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.App.Controls;

/// <summary>步骤节点编辑弹窗（循环体/延时/定时器/元素触发器）。</summary>
public partial class StepEditDialog : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point pt);

    private struct Win32Point { public int X; public int Y; }


    private readonly StepNode _node;
    private readonly List<(string Name, string Spec)> _candidates;
    private AutomationElement? _pickedElement; // 最近一次点选捕获的元素（读取当前值用）
    private string _pickedSpec = "";
    private DispatcherTimer? _pickTimer;
    private int _pickCountdown;

    public bool Saved { get; private set; }

    public StepEditDialog(StepNode node, IEnumerable<(string Name, string Spec)> elementCandidates)
    {
        InitializeComponent();
        _node = node;
        _candidates = elementCandidates?.Distinct().ToList() ?? new List<(string, string)>();

        TxtTitle.Text = node.Kind switch
        {
            StepKinds.Loop => "编辑循环体",
            StepKinds.Delay => "编辑延时",
            StepKinds.Timer => "编辑定时器",
            StepKinds.Trigger => "编辑元素触发器",
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
                TxtDelayMs.Text = node.DelayMs.ToString();
                break;
            case StepKinds.Timer:
                PnlTimer.Visibility = Visibility.Visible;
                TxtTriggerAt.Text = node.TriggerAt == default
                    ? DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss")
                    : node.TriggerAt.ToString("yyyy-MM-dd HH:mm:ss");
                break;
            case StepKinds.Trigger:
                PnlTrigger.Visibility = Visibility.Visible;
                foreach (var (name, spec) in _candidates)
                    CboElement.Items.Add(new ElementCandidateItem(name, spec));
                var t = node.Trigger ?? new ElementTriggerSpec();
                if (!string.IsNullOrEmpty(t.ElementSpec))
                {
                    CboElement.Text = t.ElementName;
                    _pickedSpec = t.ElementSpec;
                }
                CboProperty.SelectedIndex = t.Property switch
                {
                    "Name" => 1, "RangeValue" => 2, "ToggleState" => 3, "ExpandCollapseState" => 4, "IsEnabled" => 5, _ => 0,
                };
                CboCondition.SelectedIndex = t.Condition switch
                {
                    "NotEquals" => 1, "Contains" => 2, "Changed" => 3, "Greater" => 4, "Less" => 5, _ => 0,
                };
                TxtExpected.Text = t.ExpectedValue ?? "";
                TxtTimeout.Text = t.TimeoutSec.ToString();
                break;
        }
    }

    private sealed record ElementCandidateItem(string Name, string Spec)
    {
        public override string ToString() => Name.Length > 0 ? Name : Spec;
    }

    private string CurrentSpec =>
        (CboElement.SelectedItem as ElementCandidateItem)?.Spec ?? _pickedSpec;

    /// <summary>🎯 点选元素：点击后 3 秒倒计时，用户把鼠标移到目标（悬停高亮处），到点 FromPoint 捕获。</summary>
    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        BtnPick.IsEnabled = false;
        _pickCountdown = 3;
        TxtError.Text = "";
        _pickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pickTimer.Tick += (s, _) =>
        {
            if (_pickCountdown > 0)
            {
                TxtError.Foreground = System.Windows.Media.Brushes.DarkOrange;
                TxtError.Text = $"{_pickCountdown} 秒后捕获鼠标位置元素…请把鼠标移到目标上（本窗口自动失效保护自己）";
                _pickCountdown--;
                return;
            }
            _pickTimer.Stop();
            try
            {
                GetCursorPos(out var pos); // Win32：非托管依赖，无需 WinForms
                var el = AutomationElement.FromPoint(new Point(pos.X, pos.Y));
                int pid;
                try { pid = el.Current.ProcessId; }
                catch { TxtError.Text = "元素读取失败（窗口可能已关闭）"; return; }
                if (pid == Environment.ProcessId)
                {
                    TxtError.Text = "捕获到本软件自己的界面，请把鼠标移到目标应用元素上重试";
                    return;
                }
                _pickedElement = el;
                _pickedSpec = RecordingPanel.BuildSpecPublic(el);
                var name = "";
                try { name = el.Current.Name ?? ""; } catch { }
                CboElement.Text = string.IsNullOrEmpty(name) ? $"pid {pid} 元素" : name;
                TxtError.Foreground = System.Windows.Media.Brushes.Green;
                TxtError.Text = $"已捕获元素：{CboElement.Text}（可点'读取当前值'验证）";
            }
            catch (Exception ex)
            {
                TxtError.Text = $"捕获失败: {ex.Message}";
            }
            finally
            {
                BtnPick.IsEnabled = true;
                TxtError.Foreground = System.Windows.Media.Brushes.Red;
            }
        };
        _pickTimer.Start();
    }

    /// <summary>读取当前值：优先用刚点选捕获的元素实时读取，验证定位与条件配置。</summary>
    private void BtnReadCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_pickedElement is null)
        {
            TxtError.Text = "请先'点选元素'（当前值从捕获的元素实时读取）";
            return;
        }
        try
        {
            var prop = ((System.Windows.Controls.ComboBoxItem)CboProperty.SelectedItem).Content.ToString();
            string? value = prop switch
            {
                var p when p!.StartsWith("Name") => _pickedElement.Current.Name,
                var p when p!.StartsWith("Value") => (_pickedElement.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern)?.Current.Value,
                var p when p!.StartsWith("RangeValue") => (_pickedElement.GetCurrentPattern(RangeValuePattern.Pattern) as RangeValuePattern)?.Current.Value.ToString(),
                var p when p!.StartsWith("ToggleState") => (_pickedElement.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern)?.Current.ToggleState.ToString(),
                var p when p!.StartsWith("ExpandCollapseState") => (_pickedElement.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern)?.Current.ExpandCollapseState.ToString(),
                var p when p!.StartsWith("IsEnabled") => _pickedElement.Current.IsEnabled.ToString(),
                _ => null,
            };
            TxtExpected.Text = value ?? "";
            TxtError.Foreground = System.Windows.Media.Brushes.Green;
            TxtError.Text = $"当前值已填入期望值：{value ?? "(null)"}";
        }
        catch (Exception ex)
        {
            TxtError.Text = $"读取失败: {ex.Message}";
        }
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
                if (!int.TryParse(TxtDelayMs.Text, out var dms) || dms < 0) { TxtError.Text = "延时时长须为 ≥0 的整数（毫秒）"; return; }
                _node.DelayMs = dms;
                break;

            case StepKinds.Timer:
                if (!DateTime.TryParse(TxtTriggerAt.Text, out var at)) { TxtError.Text = "时刻格式无效，应为 yyyy-MM-dd HH:mm:ss"; return; }
                _node.TriggerAt = at;
                break;

            case StepKinds.Trigger:
                var spec = CurrentSpec;
                if (string.IsNullOrEmpty(spec)) { TxtError.Text = "请选择或点选目标元素"; return; }
                if (!int.TryParse(TxtTimeout.Text, out var to) || to < 0) { TxtError.Text = "超时须为 ≥0 的整数（秒）"; return; }
                var cond = CboCondition.SelectedIndex switch
                {
                    1 => "NotEquals", 2 => "Contains", 3 => "Changed", 4 => "Greater", 5 => "Less", _ => "Equals",
                };
                var propName = CboProperty.SelectedIndex switch
                {
                    1 => "Name", 2 => "RangeValue", 3 => "ToggleState", 4 => "ExpandCollapseState", 5 => "IsEnabled", _ => "Value",
                };
                var expected = TxtExpected.Text;
                if (cond is "Equals" or "NotEquals" or "Contains" or "Greater" or "Less" && string.IsNullOrEmpty(expected)
                    && propName is not "ToggleState" and not "ExpandCollapseState" and not "IsEnabled")
                { TxtError.Text = "该条件需要填写期望值"; return; }
                _node.Trigger = new ElementTriggerSpec
                {
                    ElementSpec = spec,
                    ElementName = string.IsNullOrEmpty(CboElement.Text) ? "(元素)" : CboElement.Text,
                    Property = propName,
                    Condition = cond,
                    ExpectedValue = expected,
                    TimeoutSec = to,
                };
                break;
        }
        Saved = true;
        DialogResult = true;
        Close();
    }
}
