// PatternActionDialog：模态对话框，TabControl 展示某 Pattern 下所有可执行 Action 方法
// 参照原版 ControlPatternDialog（accessibility-insights-windows）
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App.Controls;

public partial class PatternActionDialog : Window
{
    private readonly A11yElement _element;
    private readonly A11yPattern _pattern;

    public PatternActionDialog(A11yElement element, A11yPattern pattern)
    {
        InitializeComponent();
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));

        Title = $"{pattern.Name} Actions()";

        // 获取所有带 [PatternMethodAttribute] 的方法，每个方法一个 Tab
        var methods = PatternActionRunner.GetActionMethods(pattern);
        if (methods.Length == 0)
        {
            TcActions.Visibility = Visibility.Collapsed;
            Content = new TextBlock
            { Text = $"No executable actions found on pattern '{pattern.Name}'.", Margin = new Thickness(12) };
            return;
        }

        // 代码创建 TabItem：每个 Tab = 一个 PatternActionView 实例
        foreach (var method in methods)
        {
            var view = new PatternActionView(_element, _pattern, method);
            TcActions.Items.Add(new TabItem
            {
                Header = $"{method.Name}()",
                Content = view,
            });
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
