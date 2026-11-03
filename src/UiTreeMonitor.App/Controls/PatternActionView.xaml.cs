// PatternActionView：单个 Pattern Action 的执行视图（参数编辑 + Delay + Run + 结果显示）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App.Controls;

/// <summary>参数 VM 基类</summary>
public class ParamVM
{
    public string Name { get; init; } = "";
    public object? Value { get; set; }
}

/// <summary>枚举参数 VM</summary>
public class EnumParamVM : ParamVM
{
    public Array EnumValues { get; init; } = Array.Empty<object>();
}

public partial class PatternActionView : UserControl
{
    private readonly A11yElement _element;
    private readonly A11yPattern _pattern;
    private readonly MethodInfo _method;
    private readonly DispatcherTimer _timer;
    private int _countdown;
    private bool _executed;
    private readonly List<ParamVM> _paramVMs = new();

    /// <summary>执行完成回调。</summary>
    public event Action<ActionResult>? ActionExecuted;

    public PatternActionView(A11yElement element, A11yPattern pattern, MethodInfo method)
    {
        InitializeComponent();
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _method = method ?? throw new ArgumentNullException(nameof(method));

        TxtMethodName.Text = $"{method.Name}()";

        // 构建参数 UI（代码构建，避免 XAML local: 类型解析问题）
        var paramInfos = method.GetParameters();
        if (paramInfos.Length > 0)
        {
            foreach (var pi in paramInfos)
            {
                object? defaultVal = GetDefaultForType(pi.ParameterType);
                if (pi.ParameterType.IsEnum)
                    _paramVMs.Add(new EnumParamVM { Name = $"{pi.Name} ({pi.ParameterType.Name})", Value = defaultVal, EnumValues = Enum.GetValues(pi.ParameterType) });
                else
                    _paramVMs.Add(new ParamVM { Name = $"{pi.Name} ({pi.ParameterType.Name})", Value = defaultVal });
            }

            // 构建 StackPanel：每行 Label + 输入控件
            for (int i = 0; i < _paramVMs.Count; i++)
            {
                var vm = _paramVMs[i];
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = vm.Name, Width = 140, VerticalAlignment = VerticalAlignment.Center });
                if (vm is EnumParamVM evm)
                    row.Children.Add(new ComboBox { ItemsSource = evm.EnumValues, SelectedItem = evm.Value, Width = 200, VerticalAlignment = VerticalAlignment.Center });
                else
                    row.Children.Add(new TextBox { Text = vm.Value?.ToString() ?? "", Width = 200, VerticalAlignment = VerticalAlignment.Center });
                PanelParams.Children.Add(row);
            }
            PanelParams.Visibility = Visibility.Visible;
        }

        // 倒计时 DispatcherTimer（WPF UI 线程 Timer，Tick 事件自动在 UI 线程触发）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TbDelay.Text, out var delay) || delay < 0)
        { TxtResult.Text = "Delay must be a non-negative integer."; return; }

        BtnRun.IsEnabled = false;

        if (delay >= 1)
        {
            _countdown = delay;
            TxtResult.Text = $"Executing in {_countdown}s...";
            _timer.Start();
        }
        else
        {
            ExecuteNow();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _countdown--;
        if (_countdown <= 0)
        {
            _timer.Stop();
            ExecuteNow();
            BtnRun.IsEnabled = true;
        }
        else
        {
            TxtResult.Text = $"Executing in {_countdown}s...";
        }
    }

    private void ExecuteNow()
    {
        if (_executed) return;
        _executed = true;

        // 收集参数值
        object?[]? paramsArray = null;
        if (_paramVMs.Count > 0)
            paramsArray = _paramVMs.Select(vm => vm.Value).ToArray();

        // 执行 Action
        var result = PatternActionRunner.Run(_element, _pattern, _method.Name, paramsArray);

        // 显示结果
        TxtResult.Text = result.Success
            ? $"\u2713 {result.DisplayText}"
            : $"\u2717 {result.DisplayText}";
        TxtResult.Foreground = result.Success
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Red;

        ActionExecuted?.Invoke(result);
    }

    private static object? GetDefaultForType(Type t)
    {
        if (t == typeof(int)) return 0;
        if (t == typeof(double)) return 0.0;
        if (t == typeof(string)) return string.Empty;
        if (t == typeof(bool)) return false;
        if (t.IsEnum) return Enum.ToObject(t, 0);
        return null;
    }

    private void TbDelay_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"[0-9]");
    }
}
