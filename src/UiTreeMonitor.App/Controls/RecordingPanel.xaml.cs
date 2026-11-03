// RecordingPanel：操作录制与回放控制面板
// 
// 双录制模式：
//   1. 手动模式（默认）：用户通过 Actions 对话框执行的操作自动记录到当前脚本
//   2. 自动模式：基于 Axe.Windows.ListenAction 监听目标窗口 UIA 事件（点击/输入/焦点变化等）自动捕获
//
// 回放：按脚本顺序重放操作（通过 ElementPath 在当前 UIA 树中重定位元素后调用 PatternActionRunner）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App.Controls;

public partial class RecordingPanel : UserControl
{
    private ActionScript _script = new();
    private bool _isRecording;
    private bool _isAutoMode;           // true=自动(UIA事件监听), false=手动(Actions执行)
    private int _stepIndex = -1;
    private EventListenerCore? _autoListener; // 自动模式的事件监听器
    private int _listenerPid;           // 当前监听器锁定/瞄准的进程（0=自由模式：全桌面+排除自己）
    private int _hoverProcessId;        // 最近一次悬停捕获的进程（补挂/切换监听用）
    private DateTime _lastAttach = DateTime.MinValue; // 上次挂载时刻（重挂防抖）

    /// <summary>录制状态变化通知。</summary>
    public event Action<bool>? RecordingStateChanged;

    /// <summary>请求解析元素的回调（MainWindow 注入：ElementPath → 实时 A11yElement）。</summary>
    public Func<string, A11yElement?>? ResolveElement { get; set; }

    /// <summary>获取当前目标元素的 UniqueId（自动模式需要，MainWindow 注入）。</summary>
    public Func<int?>? GetTargetElementUniqueId { get; set; }

    /// <summary>获取当前目标 A11yElement 引用（自动模式+手动录制都需要，MainWindow 注入）。</summary>
    public Func<Axe.Windows.Core.Bases.A11yElement?>? GetCurrentA11yElement { get; set; }

    /// <summary>获取当前目标窗口信息（MainWindow 注入：自动模式监听挂到目标窗口子树 + 按进程过滤）。</summary>
    public Func<TargetWindowInfo?>? GetTargetWindowInfo { get; set; }

    public RecordingPanel()
    {
        InitializeComponent();
        _isAutoMode = true; // ★ 默认自动模式
        RefreshUI();
    }

    /// <summary>追加一条录制记录（手动模式：由 PatternActionDialog 执行后调用）。</summary>
    public void RecordAction(ActionRecord record)
    {
        if (!_isRecording || _isAutoMode) return; // 自动模式不使用此入口
        _script.Records.Add(record);
        RefreshList();
        TxtStatus.Text = $"已录制 {_script.Records.Count} 条操作（手动模式）";
    }

    // ==== 录制控制 ====

    private void BtnToggleRecord_Click(object sender, RoutedEventArgs e)
    {
        _isAutoMode = ((RadioButton)RbModeAuto).IsChecked == true;

        if (!_isRecording)
        {
            // 开始录制
            _script = new ActionScript { CreatedAt = DateTime.Now };
            _stepIndex = -1;

            // ★ 先统一设置 _isRecording=true（确保 RecordingStateChanged 传递 true → 启动静默捕获）
            // 无论手动/自动模式，静默捕获（鼠标跟踪+树高亮）都是必需的
            _isRecording = true;

            if (_isAutoMode)
            {
                StartAutoRecording();
            }
            else
            {
                TxtStatus.Text = "● 手动录制中…执行 Actions 将自动记录";
            }

            BtnToggleRecord.Content = "■ 停止录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            // 停止录制
            if (_isAutoMode && _autoListener is not null)
            {
                _autoListener.Stop();
                _autoListener.Dispose();
                _autoListener = null;
                _listenerPid = 0;
            }

            _isRecording = false;
            BtnToggleRecord.Content = "● 开始录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Black;
            TxtStatus.Text = $"录制停止，共 {_script.Records.Count} 条操作" +
                             (_isAutoMode ? "（自动模式）" : "（手动模式）");
        }

        RefreshUI();
        RecordingStateChanged?.Invoke(_isRecording);
    }

    /// <summary>
    /// 启动自动模式 UIA 事件监听。目标解析降级链（保证"直接点录制也能录到"）：
    /// ① 抓过树（目标窗口 hwnd+pid）→ 监听挂目标窗口子树（全事件）
    /// ② 无 hwnd 但知道目标 pid（悬停捕获）→ 全桌面 + 按进程过滤（全事件）
    /// ③ 什么都没有 → 全桌面 + 仅排除自己（交互类事件，防噪声）
    /// ④ 悬停到目标后由 MainWindow 调 TryAttachAutoListener 自动升级/切换到 ①②
    /// </summary>
    private void StartAutoRecording()
    {
        var uniqueId = GetTargetElementUniqueId?.Invoke();
        var hasElement = GetCurrentA11yElement?.Invoke() is not null;
        var target = GetTargetWindowInfo?.Invoke();

        var hwnd = target?.Handle ?? IntPtr.Zero;
        var pid = target?.ProcessId ?? 0;
        if (pid == 0 && _hoverProcessId != 0) pid = _hoverProcessId; // 无抓树目标时用悬停进程
        var listenMode = hwnd != IntPtr.Zero
            ? $"window-subtree (hwnd={hwnd}, pid={pid}, proc={target?.ProcessName})"
            : pid != 0
                ? $"desktop + pid-filter (pid={pid})"
                : "desktop + exclude-self (free mode, interactive events only)";
        AppLog.Write("RECORD", $"StartAutoRecording: uniqueId={uniqueId}, hasDetails={hasElement}, listen={listenMode}");

        try
        {
            // 自由模式只监听交互类事件（无 Text/Structure 高频噪声）；有目标时监听全事件
            var eventIds = hwnd != IntPtr.Zero || pid != 0
                ? EventListenerCore.DefaultEventIds
                : EventListenerCore.InteractiveEventIds;
            _autoListener = new EventListenerCore(uniqueId ?? 0,
                EventListenScope.Descendants, OnAutoEvent,
                hwnd, pid);
            _autoListener.Start(eventIds);
            _listenerPid = pid;

            TxtAutoHint.Visibility = pid == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Text = pid == 0
                ? "● 自动录制中…（自由模式）悬停目标窗口后将自动聚焦监听"
                : "● 自动录制中…正在监听目标窗口的 UIA 事件";
            AppLog.Write("RECORD", $"StartAutoRecording → SUCCESS: {listenMode}, element {uniqueId}");
        }
        catch (Exception ex)
        {
            // UIA 监听失败不影响录制状态（静默捕获仍工作），只记录日志
            _autoListener?.Dispose();
            _autoListener = null;
            _listenerPid = 0;
            TxtStatus.Text = "● 自动录制中…UIA 事件监听启动失败（鼠标捕获仍正常）";
            AppLog.Write("RECORD", $"StartAutoRecording → FAILED (recording continues): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 自动录制补挂/切换监听（MainWindow 静默捕获链路在悬停元素有效时调用）。
    /// 自由模式 → 悬停后升级为按进程过滤；已锁定进程 → 悬停到别的进程 2 秒以上切换目标。
    /// </summary>
    public void TryAttachAutoListener(int hoverProcessId)
    {
        if (!_isRecording || !_isAutoMode) return;
        _hoverProcessId = hoverProcessId;

        bool needAttach = _autoListener is null
            || (_listenerPid == 0 && hoverProcessId != 0)                                  // 自由模式 → 升级
            || (_listenerPid != 0 && hoverProcessId != 0 && hoverProcessId != _listenerPid); // 切换目标进程
        if (!needAttach) return;
        if ((DateTime.Now - _lastAttach).TotalSeconds < 2) return; // 防抖：鼠标扫过窗口不疯狂重挂

        if (_autoListener is not null)
        {
            AppLog.Write("RECORD", $"AutoListener re-attach: pid {_listenerPid} → {hoverProcessId} (hover target changed)");
            _autoListener.Stop();
            _autoListener.Dispose();
            _autoListener = null;
        }
        else
        {
            AppLog.Write("RECORD", $"AutoListener attach by hover: pid={hoverProcessId} (listener was not running)");
        }
        _lastAttach = DateTime.Now;
        StartAutoRecording();
    }

    /// <summary>自动模式事件回调：UIA 事件 → UiaEventRecord → ActionRecord → 追加到脚本。</summary>
    private void OnAutoEvent(UiaEventRecord uiaEvent)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_isRecording) return;

            var record = uiaEvent.ToActionRecord();
            _script.Records.Add(record);
            RefreshList();
            TxtStatus.Text = $"● 已捕获 {_script.Records.Count} 个事件: {uiaEvent.EventName} ({uiaEvent.ElementName})";
            AppLog.Write("RECORD", $"OnAutoEvent #{_script.Records.Count}: {uiaEvent.EventName} elem={uiaEvent.ElementName} id={uiaEvent.ElementUniqueId}");
        });
    }

    private void BtnClearRecord_Click(object sender, RoutedEventArgs e)
    {
        // 先停止自动监听
        if (_autoListener is not null)
        {
            _autoListener.Stop();
            _autoListener.Dispose();
            _autoListener = null;
            _listenerPid = 0;
        }

        _script.Records.Clear();
        _stepIndex = -1;
        _isRecording = false;
        TxtAutoHint.Visibility = Visibility.Collapsed;
        RefreshList();
        TxtStatus.Text = "已清空";
        BtnToggleRecord.Content = "● 开始录制";
        BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Black;
        RefreshUI();
    }

    // ==== 模式切换 ====

    private void RbModeManual_Checked(object sender, RoutedEventArgs e)
    {
        // ★ 录制中忽略模式切换（RadioButton 已禁用，但防御性检查）
        if (_isRecording) return;
        _isAutoMode = false;
        TxtAutoHint.Visibility = Visibility.Collapsed;
    }

    private void RbModeAuto_Checked(object sender, RoutedEventArgs e)
    {
        // ★ 录制中忽略模式切换（RadioButton 已禁用，但防御性检查）
        if (_isRecording) return;
        _isAutoMode = true;
        TxtAutoHint.Visibility = Visibility.Visible;
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
            var targetElem = ResolveElement(record.ElementPath)
                ?? throw new Exception($"元素路径 [{record.ElementPath}] 未找到");
            var (ok, result) = replayer.ExecuteOne(record, targetElem);

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

        // ★ 录制中禁用模式切换（RadioButton），防止录制过程中切换模式导致状态混乱
        RbModeManual.IsEnabled = !_isRecording;
        RbModeAuto.IsEnabled = !_isRecording;
    }

    private void RefreshList()
    {
        // ★ 扁平化匿名类型：将 ActionRecord 属性展开到根级别，匹配 XAML Binding 路径
        // 旧代码：new { Id, r } → Binding="{Binding r.Timestamp}" 无法解析（WPF 不支持子属性路径对匿名类型的反射）
        DgRecords.ItemsSource = _script.Records.Select((r, i) => new
        {
            Id = i + 1,
            r.Timestamp,
            r.ElementPath,
            r.ElementName,
            r.PatternName,
            r.MethodName,
            r.DisplayText,
            Record = r // 保留完整引用供 SelectionChanged 使用
        }).ToList();
    }

    private void DgRecords_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DgRecords.SelectedItem is not null)
        {
            dynamic? item = DgRecords.SelectedItem;
            // ★ 适配新的扁平化数据结构（Record 属性保存完整 ActionRecord 引用）
            if (item?.Record is ActionRecord rec)
                TxtStatus.Text = $"[{rec.PatternName}.{rec.MethodName}] 路径={rec.ElementPath} 参数={rec.Parameters?.Length ?? 0}个";
        }
    }
}
