// RecordingPanel：操作录制与回放控制面板
//
// 双录制模式：
//   1. 手动模式：用户通过 Actions 对话框执行的操作自动记录到当前脚本
//   2. 自动模式（默认）：
//      a. 输入动作捕获（InputHookCore 低级钩子）：鼠标左键/右键点击 + 键盘按键（可 SendInput 回放）
//      b. UIA 事件监听（EventListenerCore，可选）：目标窗口 Invoke/菜单/窗口等事件（仅记录）
//      两类均由设置（BtnSettings）控制开关，默认 a 开 b 关
//
// 回放：Pattern 记录经 ElementPath 重定位后调用 PatternActionRunner；
//       Input 记录（Input_Mouse/Input_Keyboard）直接 SendInput 模拟。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
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
    private bool _isAutoMode;           // true=自动(输入钩子+可选UIA事件), false=手动(Actions执行)
    private int _stepIndex = -1;
    private EventListenerCore? _autoListener; // UIA 事件监听器（设置开启时）
    private InputHookCore? _inputHook;        // 低级输入钩子（默认：鼠标左右键+键盘）
    private int _listenerPid;           // 当前监听器锁定/瞄准的进程（0=自由模式：全桌面+排除自己）
    private int _hoverProcessId;        // 最近一次悬停捕获的进程（补挂/切换监听用）
    private Guid? _hoverEcId;           // 最近一次悬停元素的 ElementContext id（窗口级挂载：爬到顶层窗口）
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

    /// <summary>获取录制捕获选项（MainWindow 注入：设置持久化的鼠标/键盘/UIA 开关）。</summary>
    public Func<CaptureOptions>? GetCaptureOptions { get; set; }

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
            StopInputHook();
            StopUiaListenerQuiet();

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
    /// 启动自动模式录制（按设置的模式）：
    /// ① UIA 语义模式（默认）：EventListenerCore 监听语义事件/属性变化 → 可回放记录
    ///    （按钮 Invoke、输入文字 SetValue、选择 Select、滑块拖动 SetValue、开关/展开折叠）；
    ///    目标解析降级链：抓过树（hwnd+pid）→ 挂目标窗口子树；仅悬停 pid → 全桌面+进程过滤；
    ///    无目标 → 全桌面+排除自己（交互类事件）；悬停后 TryAttachAutoListener 升级
    /// ② 输入动作模式：InputHookCore 低级钩子捕获鼠标左/右键、长按拖动与键盘按键（坐标回放）
    /// </summary>
    private void StartAutoRecording()
    {
        var opt = GetCaptureOptions?.Invoke() ?? new CaptureOptions();
        if (opt.IsUiaMode)
        {
            StopInputHook();
            AppLog.Write("RECORD", $"StartAutoRecording: mode=uia (semantic events)");
            StartUiaListener();
            TxtAutoHint.Text = "自动模式（UIA 事件）：在目标窗口中点击按钮、输入文字、选择、拖动滑块、切换开关等，" +
                               "操作将被录制为可回放的语义步骤（不依赖窗口位置）。";
            // 监听注册异步完成（全桌面 ~5s / 目标窗口 <1s），就绪回调 OnListenerStarted 会再次更新文案
            TxtStatus.Text = "● UIA 监听启动中…（提示就绪后再操作；先在目标窗口悬停片刻可加速锁定）";
        }
        else
        {
            StopUiaListenerQuiet();
            var caps = new List<string>();
            if (opt.MouseLeft) caps.Add("左键");
            if (opt.MouseRight) caps.Add("右键");
            if (opt.Keyboard) caps.Add("键盘");
            AppLog.Write("RECORD", $"StartAutoRecording: mode=input (left={opt.MouseLeft}, right={opt.MouseRight}, keys={opt.Keyboard})");
            if (caps.Count > 0)
                StartInputHook(opt);
            else
                AppLog.Write("RECORD", "InputHook disabled by settings (no capture kind checked)");

            var capsText = caps.Count > 0 ? string.Join("/", caps) : "无";
            TxtAutoHint.Text = $"自动模式（鼠标键盘动作）：正在捕获 {capsText} 输入动作（含长按拖动）。按记录时的屏幕坐标回放。";
            TxtStatus.Text = $"● 自动录制中…捕获 {capsText}（坐标模式）";
        }
    }

    /// <summary>静默停掉 UIA 监听（模式切换/停止时；忽略未运行）。</summary>
    private void StopUiaListenerQuiet()
    {
        if (_autoListener is null) return;
        _autoListener.Stop();
        _autoListener.Dispose();
        _autoListener = null;
        _listenerPid = 0;
    }

    /// <summary>启动低级输入钩子（按设置开关过滤左右键/键盘）。</summary>
    private void StartInputHook(CaptureOptions opt)
    {
        StopInputHook();
        _inputHook = new InputHookCore
        {
            CaptureMouseLeft = opt.MouseLeft,
            CaptureMouseRight = opt.MouseRight,
            CaptureKeyboard = opt.Keyboard,
        };
        _inputHook.ActionCaptured += OnInputAction;
        _inputHook.Start();
    }

    private void StopInputHook()
    {
        if (_inputHook is null) return;
        _inputHook.ActionCaptured -= OnInputAction;
        _inputHook.Dispose();
        _inputHook = null;
    }

    /// <summary>启动 UIA 事件监听（设置开启时；挂载目标解析降级链见类注释）。</summary>
    private void StartUiaListener()
    {
        var uniqueId = GetTargetElementUniqueId?.Invoke();
        var target = GetTargetWindowInfo?.Invoke();

        var hwnd = target?.Handle ?? IntPtr.Zero;
        var pid = target?.ProcessId ?? 0;
        if (pid == 0 && _hoverProcessId != 0) pid = _hoverProcessId; // 无抓树目标时用悬停进程
        var listenMode = hwnd != IntPtr.Zero
            ? $"window-subtree (hwnd={hwnd}, pid={pid}, proc={target?.ProcessName})"
            : pid != 0
                ? $"pid-target (pid={pid} → attach process window subtree, desktop fallback)"
                : "desktop + exclude-self (free mode; hover target window to upgrade fast path)";
        AppLog.Write("RECORD", $"StartUiaListener: uniqueId={uniqueId}, listen={listenMode}");

        try
        {
            // ★ 语义模式统一只注册可回放事件（4 事件+属性变化；Menu/ToolTip/Window/Text/Structure 无回放价值已砍）
            var eventIds = EventListenerCore.SemanticEventIds;
            // 挂载解析（EventListenerCore 内部）：hwnd（抓树目标）→ 悬停元素爬顶层窗口 → 桌面根兜底
            _autoListener = new EventListenerCore(uniqueId ?? 0,
                EventListenScope.Descendants, OnAutoEvent,
                hwnd, pid, OnListenerStarted,
                hoverElementContextId: hwnd == IntPtr.Zero ? _hoverEcId : null);
            _autoListener.Start(eventIds);
            _listenerPid = pid;
            // 注册在 UIA 管道线程异步执行（窗口级 <1s / 全桌面数秒）；
            // 完成后 OnListenerStarted 回调更新状态文案（此时才真正开始捕获）
            AppLog.Write("RECORD", $"StartUiaListener → listener starting (async): {listenMode}, element {uniqueId}");
        }
        catch (Exception ex)
        {
            // UIA 监听失败不影响录制（输入钩子仍工作），只记录日志
            _autoListener?.Dispose();
            _autoListener = null;
            _listenerPid = 0;
            TxtStatus.Text = "● 自动录制中…UIA 事件监听启动失败（输入捕获仍正常）";
            AppLog.Write("RECORD", $"StartUiaListener → FAILED (input capture continues): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>监听注册完成回调（管道线程 → UI 线程）：明确告知用户何时可以开始操作（注册期操作会漏录）。</summary>
    private void OnListenerStarted(bool ok)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording) return;
            TxtStatus.Text = ok
                ? "● UIA 监听就绪：在目标窗口中点击/输入/拖动即可录制"
                : "● UIA 事件监听启动失败（详见日志）";
            AppLog.Write("RECORD", $"ListenerStarted → {(ok ? "ready" : "failed")} (pid={_listenerPid})");
        });
    }

    /// <summary>
    /// 输入动作回调（钩子线程 → UI 线程）：UIA 定位动作目标元素（鼠标=坐标命中、键盘=当前焦点），
    /// 过滤本软件自己的 UI，转换为 ActionRecord（Input_Mouse/Input_Keyboard，可 SendInput 回放）。
    /// </summary>
    private void OnInputAction(InputActionRecord input)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording) return;
            try
            {
                var elem = input.Kind == InputActionKind.KeyDown
                    ? AutomationElement.FocusedElement
                    : AutomationElement.FromPoint(new Point(input.X, input.Y)); // 拖动取按下点元素

                int pid = 0;
                string elemName = "", ctrlType = "", procName = "";
                try
                {
                    if (elem is not null)
                    {
                        pid = elem.Current.ProcessId;
                        elemName = elem.Current.Name ?? "";
                        ctrlType = elem.Current.ControlType?.ProgrammaticName ?? "";
                        procName = Process.GetProcessById(pid).ProcessName;
                    }
                }
                catch { /* 元素可能已销毁/进程已退出 */ }

                // 自己窗口上的动作不录（点"停止录制"按钮本身也是一次左键点击）
                if (pid == Environment.ProcessId)
                {
                    AppLog.Write("RECORD", $"Input {input.Kind} on own UI → filtered");
                    return;
                }

                ActionRecord record = input.Kind switch
                {
                    InputActionKind.MouseLeftClick => new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = elemName,
                        PatternName = InputSimulator.MousePattern,
                        MethodName = InputSimulator.LeftClickMethod,
                        Parameters = new object?[] { $"{input.X},{input.Y}" },
                        Success = true,
                        Result = $"{procName} {ctrlType} ({input.X},{input.Y})",
                    },
                    InputActionKind.MouseRightClick => new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = elemName,
                        PatternName = InputSimulator.MousePattern,
                        MethodName = InputSimulator.RightClickMethod,
                        Parameters = new object?[] { $"{input.X},{input.Y}" },
                        Success = true,
                        Result = $"{procName} {ctrlType} ({input.X},{input.Y})",
                    },
                    InputActionKind.MouseLeftDrag or InputActionKind.MouseRightDrag => new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = elemName,
                        PatternName = InputSimulator.MousePattern,
                        MethodName = input.Kind == InputActionKind.MouseLeftDrag
                            ? InputSimulator.DragMethod
                            : InputSimulator.RightDragMethod,
                        Parameters = new object?[] { $"{input.X},{input.Y}", $"{input.EndX},{input.EndY}" },
                        Success = true,
                        Result = $"{procName} {ctrlType} ({input.X},{input.Y})→({input.EndX},{input.EndY})",
                    },
                    _ => new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = elemName,
                        PatternName = InputSimulator.KeyboardPattern,
                        MethodName = InputSimulator.ComboText(input.Ctrl, input.Shift, input.Alt, input.VkCode),
                        Success = true,
                        Result = $"{procName} {ctrlType}",
                    },
                };

                _script.Records.Add(record);
                RefreshList();
                TxtStatus.Text = $"● 已捕获 {_script.Records.Count} 个动作: {record.MethodName}" +
                                 (elemName != "" ? $" ({elemName})" : "");
                AppLog.Write("RECORD", $"OnInputAction #{_script.Records.Count}: {record.MethodName} elem={elemName} type={ctrlType} pid={pid} proc={procName} at=({input.X},{input.Y})" +
                    (record.MethodName is InputSimulator.DragMethod or InputSimulator.RightDragMethod ? $"→({input.EndX},{input.EndY})" : ""));
            }
            catch (Exception ex)
            {
                AppLog.Write("RECORD", $"OnInputAction → exception (ignored): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 自动录制补挂/切换监听（MainWindow 静默捕获链路在悬停元素有效时调用）。
    /// ★ 桌面根挂载：就地切换 pid 过滤（零真空期）；窗口级挂载：同 pid 不动、换 pid 重挂（窗口级注册 &lt;1s）。
    /// </summary>
    public void TryAttachAutoListener(int hoverProcessId, Guid? hoverElementContextId = null)
    {
        if (!_isRecording || !_isAutoMode) return;
        // 仅 UIA 语义模式需要按目标挂监听（输入钩子不依赖目标窗口）
        if ((GetCaptureOptions?.Invoke() ?? new CaptureOptions()).IsUiaMode != true) return;
        // 悬停自己 UI（如点停止按钮）不升级：挂到自己窗口子树后所有事件都会被"排除自己"过滤，录不到任何东西
        if (hoverProcessId == Environment.ProcessId) return;
        _hoverProcessId = hoverProcessId;
        if (hoverElementContextId.HasValue) _hoverEcId = hoverElementContextId;

        if (_autoListener is not null)
        {
            if (_listenerPid == hoverProcessId) return; // 目标未变（含窗口级/桌面根通用）

            if (_autoListener.IsDesktopScope)
            {
                // ★ 就地切换过滤目标：桌面根监听在岗时只改 pid，绝不重启（零真空期）
                _autoListener.UpdateTargetProcess(hoverProcessId);
                _listenerPid = hoverProcessId;
                return;
            }
            // 窗口级挂载范围固定，切进程需重挂（注册 <1s，窗口期小）
        }
        // 监听未启动（首次悬停）或窗口级需换目标 → 新起/重挂
        if ((DateTime.Now - _lastAttach).TotalSeconds < 2) return; // 防抖：鼠标扫过窗口不疯狂重挂

        if (_autoListener is not null)
        {
            AppLog.Write("RECORD", $"AutoListener re-attach: pid {_listenerPid} → {hoverProcessId} (window-subtree scope, must re-attach)");
            _autoListener.Stop();
            _autoListener.Dispose();
            _autoListener = null;
        }
        else
        {
            AppLog.Write("RECORD", $"AutoListener attach by hover: pid={hoverProcessId} (listener was not running)");
        }
        _lastAttach = DateTime.Now;
        StartUiaListener(); // 只重挂 UIA 监听（输入钩子不依赖目标，无需重启）
    }

    /// <summary>自动模式事件回调：UIA 事件 → UiaEventRecord → ActionRecord → 追加到脚本。
    /// 值类连续变化（逐字符输入/拖动过程）ReplacePrevious：替换上一条同元素同 Pattern 记录，只保留最终值。</summary>
    private void OnAutoEvent(UiaEventRecord uiaEvent)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_isRecording) return;

            var record = uiaEvent.ToActionRecord();

            // 连续值变化合并：上一条同元素同 Pattern 同方法 → 替换（输入 "abc" 只留最终 "abc"，回放幂等）
            if (uiaEvent.ReplacePrevious && _script.Records.Count > 0)
            {
                var last = _script.Records[^1];
                if (last.PatternName == record.PatternName
                    && last.MethodName == record.MethodName
                    && last.ElementPath == record.ElementPath)
                {
                    _script.Records[^1] = record;
                    RefreshList();
                    TxtStatus.Text = $"● 已捕获 {_script.Records.Count} 个动作: {record.MethodName}({string.Join(", ", record.Parameters ?? Array.Empty<object?>())})";
                    AppLog.Write("RECORD", $"OnAutoEvent merge: {uiaEvent.EventName} → {record.MethodName}({string.Join(", ", record.Parameters ?? Array.Empty<object?>())}) elem={uiaEvent.ElementName}");
                    return;
                }
            }

            _script.Records.Add(record);
            RefreshList();
            var detail = record.Parameters is { Length: > 0 }
                ? $"{record.MethodName}({string.Join(", ", record.Parameters)})"
                : record.MethodName;
            TxtStatus.Text = $"● 已捕获 {_script.Records.Count} 个动作: {detail} ({uiaEvent.ElementName})";
            AppLog.Write("RECORD", $"OnAutoEvent #{_script.Records.Count}: {uiaEvent.EventName} elem={uiaEvent.ElementName} id={uiaEvent.ElementUniqueId} → {record.PatternName}.{record.MethodName}");
        });
    }

    private void BtnClearRecord_Click(object sender, RoutedEventArgs e)
    {
        // 先停止输入钩子与自动监听
        StopInputHook();
        StopUiaListenerQuiet();

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
            // 输入动作记录（Input_Mouse/Input_Keyboard）不需要元素重定位，直接 SendInput 回放
            var isInput = record.PatternName is InputSimulator.MousePattern or InputSimulator.KeyboardPattern;
            var targetElem = isInput ? null
                : ResolveElement(record.ElementPath)
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
