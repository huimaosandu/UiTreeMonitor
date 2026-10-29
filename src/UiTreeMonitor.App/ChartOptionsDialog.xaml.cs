using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace UiTreeMonitor.App;

/// <summary>走势图系列行 VM（勾选=画线，图例可编辑；非数值列禁选）。</summary>
public sealed class ChartSeriesItem
{
    public int ColumnIndex { get; init; }              // 对应 CSV 列（0=值1）
    public string Key { get; init; } = "";             // 原列名（值1/值2…）
    public bool IsNumeric { get; init; }               // 是否可绘制折线
    public bool Selected { get; set; }
    public string Legend { get; set; } = "";
    public string Note => IsNumeric ? "" : "（非数值，不可绘制）";
}

/// <summary>用户的选择结果。</summary>
public sealed class ChartOptionsResult
{
    /// <summary>选中的系列：列索引 + 原列名 → 自定义图例名（顺序保留）。</summary>
    public IReadOnlyList<(int Index, string Key, string Legend)> SelectedSeries { get; init; }
        = new List<(int, string, string)>();
    public bool GenerateChart { get; init; }   // true=生成图；false=跳过图仅导出
    public bool Confirmed { get; init; }       // false=取消整个导出
}

/// <summary>走势图选项弹窗：选系列 + 改图例。</summary>
public partial class ChartOptionsDialog : Window
{
    private readonly List<ChartSeriesItem> _items;

    public ChartOptionsResult Result { get; private set; } = new() { Confirmed = false };

    public ChartOptionsDialog(IReadOnlyList<ChartSeriesItem> items)
    {
        InitializeComponent();
        _items = items.ToList();
        SeriesList.ItemsSource = _items;
    }

    private void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsNumeric && i.Selected)
            .Select(i => (i.ColumnIndex, i.Key, string.IsNullOrWhiteSpace(i.Legend) ? i.Key : i.Legend.Trim()))
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请至少勾选一条数值曲线。", "数据图选项", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = new ChartOptionsResult { SelectedSeries = selected, GenerateChart = true, Confirmed = true };
        DialogResult = true;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Result = new ChartOptionsResult { GenerateChart = false, Confirmed = true };
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new ChartOptionsResult { GenerateChart = false, Confirmed = false };
        DialogResult = false;
        Close();
    }
}
