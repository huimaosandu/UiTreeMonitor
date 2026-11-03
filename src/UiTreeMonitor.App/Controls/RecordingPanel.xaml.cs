// RecordingPanel：操作录制与回放控制面板
// 录制：用户通过 Actions 对话框执行的操作自动记录到当前脚本
// 回放：按脚本顺序重放操作（通过 ElementPath 重定位元素 + PatternActionRunner 执行）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App.Controls;

public partial class RecordingPanel : UserControl
{
    private ActionScript _script = new();
    private bool _isRecording;
    private int _stepIndex = -1; // 单步回放位置

    /// <summary>录制状态变化通知。</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>请求解析元素的回调（MainWindow 注入：ElementPath → A11yElement）。</summary>
    public Func<string, A11yElement?>? ResolveElement { get; set; }

    /// <summary>获取当前录制中的目标窗口信息（用于回放时抓树定位元素）。</summary>
    public Func<object>? GetCurrentTarget { get; set; }

    public RecordingPanel()
    {
        InitializeComponent();
        RefreshUI();
    }

    /// <summary>追加一条录制记录（由 PatternActionDialog 执行后调用）。</summary>
    public void RecordAction(ActionRecord record)
    {
        if (!_isRecording) return;
        _script.Records.Add(record);
        RefreshList();
        TxtStatus.Text = $"已录制 {_script.Records.Count} 条操作";
    }

    // ==== 录制控制 ====

    private void BtnToggleRecord_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = !_isRecording;
        if (_isRecording)
        {
            // 开始新录制
            _script = new ActionScript { CreatedAt = DateTime.Now };
            _stepIndex = -1;
            BtnToggleRecord.Content = "■ 停止录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Red;
            TxtStatus.Text = "● 录制中…执行 Actions 将自动记录";
            RefreshList();
        }
        else
        {
            BtnToggleRecord.Content = "● 开始录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Black;
            TxtStatus.Text = $"录制停止，共 {_script.Records.Count} 条操作";
        }
        RefreshUI();
        RecordingStateChanged?.Invoke(_isRecording);
    }

    private void BtnClearRecord_Click(object sender, RoutedEventArgs e)
    {
        _script.Records.Clear();
        _stepIndex = -1;
        RefreshList();
        TxtStatus.Text = "已清空";
        RefreshUI();
    }

    // ==== 回放控制 ====

    private void BtnPlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (_script.Records.Count == 0) return;
        var speed = GetSpeedMultiplier();

        BtnPlayAll.IsEnabled = false;
        BtnStep.IsEnabled = false;
        TxtStatus.Text = "▶ 回放中…";

        try
        {
            using var replayer = new ActionReplayer();
            var results = replayer.Playback(_script,
                ResolveElement ?? (_ => null),
                speed,
                default,
                new Progress<PlaybackProgress>(p =>
                {
                    if (!p.IsComplete)
                        Dispatcher.Invoke(() => TxtStatus.Text =
                            $"▶ 回放中… ({p.CurrentIndex + 1}/{p.TotalCount}) {p.CurrentRecord.MethodName}()");
                    else
                        Dispatcher.Invoke(() =>
                        {
                            TxtStatus.Text = $"✓ 回放完成 ({p.TotalCount} 条)";
                            BtnPlayAll.IsEnabled = true;
                            BtnStep.IsEnabled = true;
                        });
                }));

            // 更新结果列
            for (int i = 0; i < Math.Min(results.Count, _script.Records.Count); i++)
            {
                _script.Records[i] = _script.Records[i] with
                {
                    Success = results[i].Success,
                    Result = results[i].DisplayText
                };
            }
            RefreshList();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"✗ 回放失败: {ex.Message}";
            BtnPlayAll.IsEnabled = true;
            BtnStep.IsEnabled = true;
        }
    }

    private void BtnStep_Click(object sender, RoutedEventArgs e)
    {
        if (_script.Records.Count == 0 || ResolveElement is null) return;

        _stepIndex++;
        if (_stepIndex >= _script.Records.Count)
        {
            _stepIndex = -1;
            TxtStatus.Text = "单步回放已到达末尾，重新开始";
            return;
        }

        var record = _script.Records[_stepIndex];
        DgRecords.SelectedIndex = _stepIndex;
        DgRecords.ScrollIntoView(DgRecords.SelectedItem);
        TxtStatus.Text = $"→ 单步 [{_stepIndex + 1}/{_script.Records.Count}] {record.MethodName}()...";

        try
        {
            using var replayer = new ActionReplayer();
            var (ok, result) = replayer.ExecuteOne(record, ResolveElement(record.ElementPath)
                ?? throw new Exception($"元素路径 [{record.ElementPath}] 未找到"));

            _script.Records[_stepIndex] = record with { Success = ok, Result = result };
            RefreshList();
            DgRecords.SelectedIndex = _stepIndex;
            TxtStatus.Text = ok
                ? $"→ [{_stepIndex + 1}] {record.MethodName}() ✓"
                : $"→ [{_stepIndex + 1}] {record.MethodName}() ✗ {result}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"→ [{_stepIndex + 1}] 错误: {ex.Message}";
        }
    }

    // ==== 导入/导出 ====

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_script.Records.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "JSON 脚本|*.json",
            FileName = $"action_script_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, _script.ToJson());
            TxtStatus.Text = $"已导出: {dlg.FileName}";
        }
        catch (Exception ex) { TxtStatus.Text = $"导出失败: {ex.Message}"; }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON 脚本|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            var imported = ActionScript.FromJson(json);
            if (imported is null || imported.Records.Count == 0)
            { TxtStatus.Text = "导入失败：无效的脚本文件"; return; }

            _script = imported;
            _stepIndex = -1;
            RefreshList();
            TxtStatus.Text = $"已导入 {imported.Records.Count} 条操作（来自 {dlg.FileName}）";
            RefreshUI();
        }
        catch (Exception ex) { TxtStatus.Text = $"导入失败: {ex.Message}"; }
    }

    // ==== 内部辅助 ====

    private double GetSpeedMultiplier() => CboSpeed.SelectedIndex switch
    {
        0 => 0.5,
        1 => 1.0,
        2 => 2.0,
        3 => 5.0,
        _ => 1.0
    };

    private void RefreshUI()
    {
        var hasRecords = _script.Records.Count > 0;
        BtnClearRecord.IsEnabled = hasRecords;
        BtnPlayAll.IsEnabled = hasRecords && !_isRecording;
        BtnStep.IsEnabled = hasRecords && !_isRecording;
        BtnExport.IsEnabled = hasRecords;
    }

    private void RefreshList()
    {
        DgRecords.ItemsSource = _script.Records.Select((r, i) => new { Id = i + 1, r }).ToList();
    }

    private void DgRecords_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中行时显示详情
        if (DgRecords.SelectedItem is not null)
        {
            dynamic? item = DgRecords.SelectedItem;
            if (item?.r is ActionRecord rec)
                TxtStatus.Text = $"[{rec.PatternName}.{rec.MethodName}()] 路径={rec.ElementPath} 参数={rec.Parameters?.Length ?? 0}个";
        }
    }
}
