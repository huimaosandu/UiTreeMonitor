// RecordingPanel：操作录制与回放控制面板
//
// 双录制模式：
//   1. 手动模式：用户通过 Actions 对话框执行的操作自动记录到当前脚本
//   2. 自动模式（默认）：
//      a. 输入动作捕获（InputHookCore 低级钩子）：鼠标左键/右键点击 + 键盘按键（可 SendInput 回放）
//      b. UIA 事件监听（EventListenerCore，可选）：目标窗口 Invoke/菜单/窗口等事件（仅记录）
//      两类均由设置（BtnSettings）控制开关，默认 a 开 b 关
//
// ★ UIA 语义模式双通道（tkinter/Qt 等自绘框架不推送 UIA 事件，纯监听收不到）：
//   通道1 被动：EventListenerCore COM 事件监听（UIA-aware 应用：原生 Win32/WPF/WinForms）
//   通道2 主动：输入钩子 + 元素 Pattern 语义化（点击→InvokePattern.Invoke；文本键→焦点元素
//              ValuePattern 实时值 SetValue；拖动→RangeValuePattern 终值 SetValue；不支持则坐标回退）
//   两通道同元素同方法记录经 merge 合并（spec 定位忽略 rect 抖动），不会重复
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
    private bool _uiaSemanticMode;      // UIA 语义模式录制中（双通道：事件监听+输入语义化）
    private readonly Dictionary<string, byte> _patternProbeLogged = new(); // 语义化失败探针日志节流（同元素只打一次 pattern 列表）

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
            _uiaSemanticMode = false;

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
    /// ① UIA 语义模式（默认，双通道）：
    ///    通道1 被动监听：EventListenerCore COM 事件（Invoke/Value/RangeValue/Toggle/ExpandCollapse）；
    ///    通道2 主动语义化：输入钩子命中元素 Pattern（点击→Invoke、文本键→SetValue、拖动→RangeValue 终值）。
    ///    ★ tkinter/Qt 等自绘框架不推送 UIA 事件，通道2 是它们唯一的语义录制来源；
    ///    不支持任何 Pattern 的控件自动回退坐标记录（回放 SendInput）。
    ///    挂载目标：抓过树（hwnd）→ 目标窗口子树；悬停（ecId）→ 爬顶层窗口子树；
    ///    无目标 → 不挂监听（避免桌面根洪水），悬停后 TryAttachAutoListener 秒级锁定
    /// ② 输入动作模式：InputHookCore 低级钩子捕获鼠标左/右键、长按拖动与键盘按键（坐标回放）
    /// </summary>
    private void StartAutoRecording()
    {
        var opt = GetCaptureOptions?.Invoke() ?? new CaptureOptions();
        if (opt.IsUiaMode)
        {
            _uiaSemanticMode = true;
            AppLog.Write("RECORD", "StartAutoRecording: mode=uia (dual-channel: COM events + input semanticization)");
            // ★ 语义模式钩子全开（左键/右键/键盘）：Pattern 语义化优先，无 Pattern 控件自动坐标回退
            StartInputHook(opt with { MouseLeft = true, MouseRight = true, Keyboard = true });
            StartUiaListener();
            TxtAutoHint.Text = "自动模式（UIA 语义）：在目标窗口中点击按钮、输入文字、拖动滑块、选择、切换开关等，" +
                               "录制为可回放语义步骤（不依赖窗口位置）；不支持语义化的控件自动回退坐标记录。";
            // 监听注册异步完成（目标窗口 <1s），就绪回调 OnListenerStarted 会再次更新文案；
            // 即使监听未就绪，输入钩子通道已可录制（语义化/坐标）
            TxtStatus.Text = "● 录制中…（UIA 监听启动中；若未抓取过目标树，将鼠标移到目标窗口上即自动锁定）";
        }
        else
        {
            _uiaSemanticMode = false;
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
        // ★ 悬停 ec 仅在悬停进程有效且非自己时使用：
        //   点"开始录制"按钮时鼠标在自己 UI 上，最近悬停 POI 已是自己面板元素，爬窗会挂到自己窗口
        var hoverEc = _hoverProcessId != 0 && _hoverProcessId != Environment.ProcessId ? _hoverEcId : null;

        if (hwnd == IntPtr.Zero && pid == 0 && hoverEc is null)
        {
            // ★ 无任何目标不挂监听（桌面根=自己进程事件洪水 150条/s + 注册 1.6s）：
            //   输入钩子通道已可录制；鼠标悬停到目标窗口后 TryAttachAutoListener 会秒级锁定挂载
            AppLog.Write("RECORD", "StartUiaListener: no target (no hwnd/hover) → skip mount, waiting for hover attach (desktop flood avoided)");
            TxtStatus.Text = "● 录制中…请将鼠标移到目标窗口上，悬停后自动锁定并开始 UIA 监听";
            return;
        }

        var listenMode = hwnd != IntPtr.Zero
            ? $"window-subtree (hwnd={hwnd}, pid={pid}, proc={target?.ProcessName})"
            : hoverEc is not null
                ? $"hover-poi → climb top-level window (pid={pid})"
                : $"pid-target (pid={pid} → attach process window subtree)";
        AppLog.Write("RECORD", $"StartUiaListener: uniqueId={uniqueId}, listen={listenMode}");

        try
        {
            // ★ 语义模式统一只注册可回放事件（4 事件+属性变化；Menu/ToolTip/Window/Text/Structure 无回放价值已砍）
            var eventIds = EventListenerCore.SemanticEventIds;
            // 挂载解析（EventListenerCore 内部）：hwnd（抓树目标）→ 悬停元素爬顶层窗口 → 进程顶层窗口
            _autoListener = new EventListenerCore(uniqueId ?? 0,
                EventListenScope.Descendants, OnAutoEvent,
                hwnd, pid, OnListenerStarted,
                hoverElementContextId: hwnd == IntPtr.Zero ? hoverEc : null);
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
    /// 过滤本软件自己的 UI。UIA 语义模式下先尝试 Pattern 语义化（点击→Invoke、文本键→SetValue、
    /// 拖动→RangeValue 终值），不支持 Pattern 的控件回退 Input_Mouse/Input_Keyboard 坐标记录。
    /// </summary>
    private void OnInputAction(InputActionRecord input)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording) return;

            // ★ 元素解析全程防御（FromPoint 也不例外）：点击"关闭弹窗"类按钮时目标窗口瞬间销毁，
            //   FromPoint 对销毁中元素直接抛 ElementNotAvailable——若不捕获会吞掉整个动作
            //   （连坐标兜底都丢）。解析失败 → elem=null/pid=0 → 语义化跳过、坐标记录保底。
            AutomationElement? elem = null;
            int pid = 0;
            string elemName = "", ctrlType = "", procName = "";
            try
            {
                elem = input.Kind == InputActionKind.KeyDown
                    ? AutomationElement.FocusedElement
                    : AutomationElement.FromPoint(new Point(input.X, input.Y)); // 拖动取按下点元素
            }
            catch (Exception ex)
            {
                AppLog.Write("RECORD", $"Input {input.Kind}: element resolve failed (window closed by click?) → 坐标保底: {ex.Message}");
            }
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
            catch { /* 元素可能已销毁/进程已退出；已读到的部分继续用 */ }

            // ★ 竞态校验：点击"确定/关闭弹窗"类按钮时目标窗口被瞬间销毁，晚到的 FromPoint 会落到
            //   其后的窗口（曾把弹窗 OK 的 Invoke 记到主界面无关 Text 上）。钩子线程在 WM_LBUTTONUP
            //   时刻快照了真实目标顶层窗口句柄（input.Hwnd）——FromPoint 结果的顶层窗口不一致 → 元素不可信，丢弃改坐标保底
            if (elem is not null && input.Hwnd != IntPtr.Zero && pid != 0)
            {
                var topHwnd = TopLevelHwndOf(elem);
                if (topHwnd != input.Hwnd)
                {
                    AppLog.Write("RECORD", $"Input {input.Kind}: FromPoint window {topHwnd} ≠ click-time window {input.Hwnd}" +
                        $" (clicked window vanished, elem='{elemName}' is BEHIND it) → 丢弃元素，坐标保底");
                    elem = null;
                    pid = 0; elemName = ""; ctrlType = ""; procName = "";
                }
            }

            // 自己窗口上的动作不录（点"停止录制"按钮本身也是一次左键点击；元素销毁 pid=0 时不算自己）
            if (pid != 0 && pid == Environment.ProcessId)
            {
                AppLog.Write("RECORD", $"Input {input.Kind} on own UI → filtered");
                return;
            }

            // ★ 通道2 主动语义化（UIA 语义模式）：命中可回放 Pattern → 语义记录（回放走 Pattern，不依赖坐标）。
            //   tkinter/Qt 等自绘框架不推送 UIA 事件，这是它们唯一的语义录制来源；
            //   与通道1（COM 事件）同元素同方法的记录经 merge 合并，不会重复
            if (_uiaSemanticMode && elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, out var semantic, out var replacePrev))
            {
                AppendRecord(semantic, replacePrev, source: $"input-semantic {input.Kind}");
                AppLog.Write("RECORD", $"OnInputAction→semantic: {semantic.PatternName}.{semantic.MethodName}" +
                    $"({string.Join(", ", semantic.Parameters ?? Array.Empty<object?>())}) elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                return;
            }

            // 坐标回退（Input 模式主路径 / 语义模式无 Pattern 控件兜底 / 元素已销毁的点击——弹窗"确定/关闭"典型）
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

            AppendRecord(record, replacePrevious: false, source: "input-coord");
            AppLog.Write("RECORD", $"OnInputAction #{_script.Records.Count}: {record.MethodName} elem={elemName} type={ctrlType} pid={pid} proc={procName} at=({input.X},{input.Y})" +
                (record.MethodName is InputSimulator.DragMethod or InputSimulator.RightDragMethod ? $"→({input.EndX},{input.EndY})" : "") +
                (pid == 0 ? " [element vanished → coord only]" : ""));
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
            var detail = record.Parameters is { Length: > 0 }
                ? $"{record.MethodName}({string.Join(", ", record.Parameters)})"
                : record.MethodName;

            // ★ 双通道合流：与输入语义化记录共用 merge（同元素同 Pattern 同方法只留一条）
            AppendRecord(record, uiaEvent.ReplacePrevious, source: $"uia {uiaEvent.EventName}",
                statusText: $"● 已捕获 {_script.Records.Count} 个动作: {detail} ({uiaEvent.ElementName})");
        });
    }

    /// <summary>统一追加记录（UIA 事件/输入语义化/坐标回退共用）：
    /// replacePrevious=true 时若上一条同元素（spec 忽略 rect 抖动）同 Pattern 同方法 → 原位替换（值类合并去抖）。</summary>
    private void AppendRecord(ActionRecord record, bool replacePrevious, string source, string? statusText = null)
    {
        if (replacePrevious && _script.Records.Count > 0)
        {
            var last = _script.Records[^1];
            if (MergeKey(last) == MergeKey(record))
            {
                _script.Records[^1] = record;
                RefreshList();
                TxtStatus.Text = statusText ?? $"● 已捕获 {_script.Records.Count} 个动作: {record.MethodName}({string.Join(", ", record.Parameters ?? Array.Empty<object?>())})";
                AppLog.Write("RECORD", $"AppendRecord merge[{source}]: {record.PatternName}.{record.MethodName}" +
                    $"({string.Join(", ", record.Parameters ?? Array.Empty<object?>())}) elem={record.ElementName}");
                return;
            }
        }

        _script.Records.Add(record);
        RefreshList();
        var detail = record.Parameters is { Length: > 0 }
            ? $"{record.MethodName}({string.Join(", ", record.Parameters)})"
            : record.MethodName;
        TxtStatus.Text = statusText ?? $"● 已捕获 {_script.Records.Count} 个动作: {detail}" +
                         (record.ElementName != "" ? $" ({record.ElementName})" : "");
    }

    /// <summary>merge 判定键：spec 去掉 rect 段（双通道对同一元素读 rect 时机不同会差 1~2px）+ Pattern.方法。</summary>
    private static string MergeKey(ActionRecord r)
    {
        var path = r.ElementPath ?? "";
        var cut = path.IndexOf("|rect=", StringComparison.Ordinal);
        if (cut > 0) path = path[..cut];
        return $"{path}|{r.PatternName}.{r.MethodName}";
    }

    /// <summary>
    /// 输入动作 → 元素 Pattern 语义化（UIA 语义模式通道2）：
    /// 左键点击 → InvokePattern.Invoke；文本类按键 → 焦点元素 ValuePattern 实时完整值 SetValue（merge）；
    /// 左键拖动 → 起点元素 RangeValuePattern 抬起时刻终值 SetValue（merge）。
    /// 元素不支持对应 Pattern 或读取失败 → 返回 false（调用方回退坐标记录）。
    /// </summary>
    private bool TryBuildSemanticRecord(InputActionRecord input, AutomationElement? elem,
        string elemName, string procName, string ctrlType, out ActionRecord record, out bool replacePrevious)
    {
        record = null!;
        replacePrevious = false;
        if (elem is null) return false;
        try
        {
            switch (input.Kind)
            {
                case InputActionKind.MouseLeftClick:
                    if (elem.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                    {
                        replacePrevious = false;
                        record = BuildSemantic(elem, "InvokePattern", "Invoke", null,
                            result: $"{procName} {ShortType(ctrlType)} Invoke", elemName, input.Timestamp);
                        return true;
                    }
                    LogPatternProbe(elem, elemName, ctrlType, procName, "click-no-invoke");
                    break;

                case InputActionKind.KeyDown when IsTextVk(input.VkCode):
                    // 文本键：读焦点元素当前完整值（回放 SetValue 幂等；连续按键 merge 只留最终值）
                    if (elem.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo) && vpo is ValuePattern vp && !vp.Current.IsReadOnly)
                    {
                        var value = vp.Current.Value;
                        if (value is not null)
                        {
                            replacePrevious = true;
                            record = BuildSemantic(elem, "ValuePattern", "SetValue", new object?[] { value },
                                result: $"{procName} {ShortType(ctrlType)} → \"{value}\"", elemName, input.Timestamp);
                            return true;
                        }
                    }
                    break;

                case InputActionKind.MouseLeftDrag:
                    // 拖动：抬起时刻起点元素（滑块）的 RangeValue 已是终值（merge 合并拖动过程）
                    if (elem.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rpo) && rpo is RangeValuePattern rp && !rp.Current.IsReadOnly)
                    {
                        var value = rp.Current.Value;
                        replacePrevious = true;
                        record = BuildSemantic(elem, "RangeValuePattern", "SetValue", new object?[] { value },
                            result: $"{procName} {ShortType(ctrlType)} → {value}", elemName, input.Timestamp);
                        return true;
                    }
                    LogPatternProbe(elem, elemName, ctrlType, procName, "drag-no-rangevalue");
                    break;
            }
        }
        catch { /* pattern 查询/读值失败 → 坐标回退 */ }
        return false;
    }

    private static ActionRecord BuildSemantic(AutomationElement elem, string pattern, string method,
        object?[]? parameters, string result, string elemName, DateTime timestamp) => new()
    {
        Timestamp = timestamp,
        ElementName = elemName,
        ElementPath = BuildSpec(elem),          // spec 定位（回放经 ResolveBySpec 重定位，不依赖坐标）
        PatternName = pattern,
        MethodName = method,
        Parameters = parameters,
        Success = true,
        Result = result,
    };

    /// <summary>文本类虚拟键（会产生字符输入/删除的键）：0-9、A-Z、空格、Back、OEM 标点。
    /// 回车/Tab/方向/F 键等不语义化（行为语义由 SendInput 回放更合适）。</summary>
    private static bool IsTextVk(uint vk) =>
        vk is >= 0x30 and <= 0x5A    // 0-9、A-Z
        or 0x20 or 0x08              // Space、Back（删除也改变值，读实时值即最终值）
        or >= 0xBA and <= 0xC0       // OEM 标点 ;=,-./`
        or >= 0xDB and <= 0xDE;      // OEM 标点 [\]'

    private static string ShortType(string programmaticName) =>
        programmaticName.StartsWith("ControlType.", StringComparison.Ordinal)
            ? programmaticName["ControlType.".Length..]
            : programmaticName;

    /// <summary>与 EventListenerCore.BuildSpec（COM 版）同格式的 WPF 元素 spec（回放 ResolveBySpec 通用）。</summary>
    private static string BuildSpec(AutomationElement elem)
    {
        try
        {
            var c = elem.Current;
            var r = c.BoundingRectangle;
            var rect = r.IsEmpty || r.Width <= 0 || r.Height <= 0
                ? "" : $"{(int)r.Left},{(int)r.Top},{(int)r.Right},{(int)r.Bottom}";
            return $"spec:v1|autoId={Esc(c.AutomationId)}|name={Esc(c.Name)}|type={ShortType(c.ControlType?.ProgrammaticName ?? "")}" +
                   $"|class={Esc(c.ClassName)}|rect={rect}";
        }
        catch { return "spec:v1|"; }
    }

    private static string? Esc(string? s) => string.IsNullOrEmpty(s) ? "" : Uri.EscapeDataString(s);

    /// <summary>沿控件视图爬到顶层窗口元素并读其 NativeWindowHandle（与钩子快照的 WindowFromPoint 结果同源可比）。
    /// 读取失败返回 IntPtr.Zero（调用方视为无法校验、不拦截）。</summary>
    private static IntPtr TopLevelHwndOf(AutomationElement e)
    {
        try
        {
            var cur = e;
            for (int i = 0; i < 16; i++)
            {
                var parent = TreeWalker.ControlViewWalker.GetParent(cur);
                if (parent is null) break;
                cur = parent;
            }
            return cur.Current.NativeWindowHandle;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>语义化失败探针：同元素（pid+name）只打一次其支持的 Pattern 列表（诊断 tk/Qt 控件能力用）。</summary>
    private void LogPatternProbe(AutomationElement elem, string elemName, string ctrlType, string procName, string tag)
    {
        try
        {
            var key = $"{procName}|{elemName}";
            if (_patternProbeLogged.ContainsKey(key)) return;
            _patternProbeLogged[key] = 1;
            var patterns = string.Join(",", elem.GetSupportedPatterns().Select(p => p.ProgrammaticName));
            AppLog.Write("RECORD", $"[semantic-probe] {tag}: elem='{TruncLog(elemName, 40)}' type={ctrlType} proc={procName} supported=[{patterns}] → 坐标回退");
        }
        catch { /* 探针失败无碍 */ }
    }

    private static string TruncLog(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void BtnClearRecord_Click(object sender, RoutedEventArgs e)
    {
        // 先停止输入钩子与自动监听
        StopInputHook();
        StopUiaListenerQuiet();
        _uiaSemanticMode = false;
        _patternProbeLogged.Clear();

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
