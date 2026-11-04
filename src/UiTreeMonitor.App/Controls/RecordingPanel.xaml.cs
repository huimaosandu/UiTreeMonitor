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
//   通道2 主动：输入钩子 + 元素 Pattern 语义化（点击→InvokePattern.Invoke；文本键→防抖 pending
//              停顿 800ms/回车/鼠标动作时读终值 SetValue(Value/RangeValue，merge)——低级钩子先于
//              目标处理按键，KeyDown 即时读值必为旧值；拖动→RangeValuePattern 终值；回车无值类→Invoke/键盘兜底）
//   两通道同元素同方法记录经 merge 合并（spec 定位忽略 rect 抖动），不会重复
//
// 回放：Pattern 记录经 ElementPath 重定位后调用 PatternActionRunner；
//       Input 记录（Input_Mouse/Input_Keyboard）直接 SendInput 模拟。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Axe.Windows.Core.Bases;
using UiTreeMonitor.Core;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Core.Uia;

namespace UiTreeMonitor.App.Controls;

public partial class RecordingPanel : UserControl
{
    private ActionScript _script = new();          // 旧格式兼容（导出 JSON 用；树形 _steps 为主数据）
    private readonly List<StepNode> _steps = new(); // ★ 步骤树（action/loop/delay/timer/trigger，可嵌套）
    private RecordingTask? _currentTask;            // 当前打开的录制任务（保存任务按钮用）
    private bool _isRecording;
    private bool _isAutoMode;           // true=自动(输入钩子+可选UIA事件), false=手动(Actions执行)
    private int _stepIndex = -1;
    private StepVm? _selectedVm;        // 步骤树当前选中节点（右键操作目标）
    private StepVm? _anchorVm;          // Shift 范围多选锚点（上次单选项）
    private bool _triggerPickSolo;      // 触发器捕获由右键插入发起（非录制中）：完成/取消后停止静默捕获
    private bool _screenshotPickSolo;   // 截图捕获由右键插入发起（非录制中）：完成/取消后停止静默捕获并恢复烧杯
    private StepNode? _insertAnchor;    // "插入动作"录制锚点：新录动作依次插到该节点之后（null=根级末尾追加）
    private StepNode? _lastAppendedNode; // 录制链路上最后追加/合并的动作节点（merge 判定与锚点插入用）
    private RecordingTaskRunner? _playRunner; // 面板内"全部回放"的执行器（可停止）
    private EventListenerCore? _autoListener; // UIA 事件监听器（设置开启时）
    private InputHookCore? _inputHook;        // 低级输入钩子（默认：鼠标左右键+键盘）
    private int _listenerPid;           // 当前监听器锁定/瞄准的进程（0=自由模式：全桌面+排除自己）
    private int _hoverProcessId;        // 最近一次悬停捕获的进程（补挂/切换监听用）
    private Guid? _hoverEcId;           // 最近一次悬停元素的 ElementContext id（窗口级挂载：爬到顶层窗口）
    private DateTime _lastAttach = DateTime.MinValue; // 上次挂载时刻（重挂防抖）
    private bool _uiaSemanticMode;      // UIA 语义模式录制中（双通道：事件监听+输入语义化）
    private readonly Dictionary<string, byte> _patternProbeLogged = new(); // 语义化失败探针日志节流（同元素只打一次 pattern 列表）

    // ★ 文本键防抖终值（键盘录制核心机制）：
    //   低级键盘钩子先于目标进程处理按键，KeyDown 时刻读值必为旧值（敲"65"读第二键时只有"6"）。
    //   连续文本键进入 pending，800ms 停顿/回车/非文本键/鼠标动作触发 flush——此刻所有键已被目标处理，
    //   读到的是终值，生成一条 SetValue(merge)，既解决时序又天然合并连击。
    private DispatcherTimer? _keyDebounce;     // 防抖定时器（停顿 800ms → flush）
    private AutomationElement? _pendingKeyElem; // 防抖中的键入目标元素（null=解析失败，flush 时重试解析链）
    private string _pendingKeyName = "", _pendingKeyProc = "", _pendingKeyType = "";
    private DateTime _pendingKeyAt = DateTime.MinValue; // 最近一次文本键时刻

    // ★ KeyDown 焦点解析失败回退链缓存：FocusedElement 对 Qt 等自绘窗口实测返回 null（不抛异常），
    //   回退"最近一次鼠标动作命中的元素"（键入前刚点击过输入框，命中率极高）
    private AutomationElement? _recentClickElem;
    private DateTime _recentClickTick = DateTime.MinValue;

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

    /// <summary>启动触发器目标捕获（MainWindow 注入：开始随鼠标捕获显示蓝框烧杯；false=正常捕获占用无法启动）。</summary>
    public Func<bool>? StartTriggerCapture { get; set; }

    /// <summary>停止触发器目标捕获（MainWindow 注入：停静默捕获——右键插入且未录制时用）。</summary>
    public Action? StopTriggerCapture { get; set; }

    /// <summary>启动截图目标捕获（MainWindow 注入：蓝框右上角烧杯换剪刀按钮；点剪刀→选目录→插入截图步骤）。</summary>
    public Func<bool>? StartScreenshotCapture { get; set; }

    /// <summary>停止截图目标捕获（MainWindow 注入：恢复烧杯图标；右键插入且未录制时用）。</summary>
    public Action? StopScreenshotCapture { get; set; }

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
        _steps.Add(new StepNode { Kind = StepKinds.Action, Action = record });
        RefreshTree();
        TxtStatus.Text = $"已录制 {_steps.Count} 条操作（手动模式）";
    }

    // ==== 录制控制 ====

    private void BtnToggleRecord_Click(object sender, RoutedEventArgs e)
    {
        _isAutoMode = ((RadioButton)RbModeAuto).IsChecked == true;

        if (!_isRecording)
        {
            // 开始录制
            _script = new ActionScript { CreatedAt = DateTime.Now };
            _steps.Clear();
            _currentTask = null; // 新录制会话脱离已打开任务（保存为新任务）
            _stepIndex = -1;
            _insertAnchor = null;      // 新录制会话重置插入锚点
            _lastAppendedNode = null;
            RefreshTree();

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
            // 停止录制（先把防抖中的键入终值落盘——最后一段输入不能因停止而丢失）
            FlushPendingKeys(reason: "stop");
            StopInputHook();
            StopUiaListenerQuiet();
            _uiaSemanticMode = false;

            _isRecording = false;
            _insertAnchor = null;      // 停止录制退出插入模式
            _lastAppendedNode = null;
            BtnToggleRecord.Content = "● 开始录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Black;
            TxtStatus.Text = $"录制停止，共 {_steps.Count} 条操作" +
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
            // ★ 语义模式钩子全开（左键/右键/键盘）：全部语义化处理，无语义的操作不录入（模式不混合）
            StartInputHook(opt with { MouseLeft = true, MouseRight = true, Keyboard = true });
            StartUiaListener();
            TxtAutoHint.Text = "自动模式（UIA 语义·纯语义录制）：点击按钮、输入文字（含回车提交）、拖动滑块、选择、开关等" +
                               "只录制为语义步骤（Invoke/SetValue，回放不依赖坐标、不动光标）；无法语义化的控件操作不会录入（详见日志）。";
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

            // ★ 鼠标动作先落盘防抖中的键入终值（顺序保证：输入的值先记录，点击动作后追加；
            //   典型场景：数字框键入 → 点 OK → OK 的 Invoke 记录前必须已有 SetValue(终值)）
            if (input.Kind != InputActionKind.KeyDown) FlushPendingKeys(reason: "mouse");

            // ★ 元素解析全程防御（FromPoint 也不例外）：点击"关闭弹窗"类按钮时目标窗口瞬间销毁，
            //   FromPoint 对销毁中元素直接抛 ElementNotAvailable——若不捕获会吞掉整个动作
            //   （连坐标兜底都丢）。解析失败 → elem=null/pid=0 → 语义化跳过、坐标记录保底。
            // ★ KeyDown 走回退链：FocusedElement 对 Qt 等自绘窗口实测返回 null（不抛异常）→
            //   历史上在此静默丢弃（日志零痕迹）；依次回退：最近鼠标命中元素(15s) → 鼠标位置 FromPoint
            AutomationElement? elem = null;
            int pid = 0;
            string elemName = "", ctrlType = "", procName = "";
            try
            {
                elem = input.Kind == InputActionKind.KeyDown
                    ? TryResolveKeyTarget()
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

            // ★ 鼠标动作命中元素缓存（KeyDown 焦点解析失败的回退源：键入前刚点过输入框）
            if (input.Kind != InputActionKind.KeyDown && elem is not null && pid != 0)
            {
                _recentClickElem = elem;
                _recentClickTick = DateTime.Now;
            }

            // ★ 自己 UI 的动作最先拦（点"停止录制"按钮本身也是一次左键点击）——必须在校验丢弃之前：
            //   若先做 hwnd 校验把 pid 清零，own 过滤失效，自己 UI 的点击会被当成"元素销毁"误录
            if (pid != 0 && pid == Environment.ProcessId)
            {
                AppLog.Write("RECORD", $"Input {input.Kind} on own UI → filtered");
                return;
            }

            // ★ 竞态校验：点击"确定/关闭弹窗"类按钮时目标窗口被瞬间销毁，晚到的 FromPoint 会落到
            //   其后的窗口（曾把弹窗 OK 的 Invoke 记到主界面无关 Text 上）。钩子线程在 WM_LBUTTONUP
            //   时刻快照了真实目标顶层窗口句柄（input.Hwnd）——FromPoint 结果的顶层窗口不一致 → 元素不可信，丢弃
            if (elem is not null && input.Hwnd != IntPtr.Zero && pid != 0)
            {
                var topHwnd = TopLevelHwndOf(elem);
                if (topHwnd != input.Hwnd)
                {
                    AppLog.Write("RECORD", $"Input {input.Kind}: FromPoint window {topHwnd} ≠ click-time window {input.Hwnd}" +
                        $" (clicked window vanished, elem='{elemName}' is BEHIND it) → 丢弃元素，语义化走 coord 定位");
                    elem = null;
                    pid = 0; elemName = ""; ctrlType = ""; procName = "";
                }
            }

            // ★ 模式严格二选一（设置即铁律，不混合）：
            //   UIA 语义模式 → 只产语义记录（Pattern 方法 / coord:v1 延迟定位），绝不产 Input_* 坐标记录
            //   鼠标键盘模式 → 只产坐标记录（SendInput 回放），绝不语义化
            if (_uiaSemanticMode)
            {
                HandleUiaSemanticInput(input, elem, pid, elemName, procName, ctrlType);
                return;
            }

            // 鼠标键盘模式：坐标记录（Input 模式主路径）
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
            AppLog.Write("RECORD", $"OnInputAction #{_steps.Count}: {record.MethodName} elem={elemName} type={ctrlType} pid={pid} proc={procName} at=({input.X},{input.Y})" +
                (record.MethodName is InputSimulator.DragMethod or InputSimulator.RightDragMethod ? $"→({input.EndX},{input.EndY})" : "") +
                (pid == 0 ? " [element vanished → coord only]" : ""));
        });
    }

    /// <summary>
    /// UIA 语义模式输入处理（严格纯语义，零坐标记录）：
    /// - 左键点击：元素有 InvokePattern → Invoke；元素销毁（点击即关窗的 OK/确定按钮竞态）→
    ///   coord:v1 延迟定位的 Invoke（回放时弹窗已被前置步骤重新打开，FromPoint 命中该按钮语义执行，光标不动）；
    ///   元素无 InvokePattern → 不记录（探针日志注明控件无语义能力）
    /// - 文本键：防抖 pending（键入中不即时读值——低级钩子先于目标处理按键，即时读必为旧值），
    ///   800ms 停顿/回车/非文本键/鼠标动作时读终值 → SetValue(merge)；Value（文本）/RangeValue（数字框）双通道
    /// - 回车：flush pending 后即时语义化——值类 SetValue(终值) / 按钮焦点 Invoke；全无 → 键盘 Enter 兜底记录
    /// - 左键拖动：起点元素 RangeValuePattern 终值 → SetValue(merge)；无 → 不记录
    /// - 右键/右键拖动/其他键（Tab/方向/F 键等）：无 UIA 语义等价，不记录（Tab 先 flush 键入）
    /// </summary>
    private void HandleUiaSemanticInput(InputActionRecord input, AutomationElement? elem,
        int pid, string elemName, string procName, string ctrlType)
    {
        switch (input.Kind)
        {
            case InputActionKind.MouseLeftClick:
                if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, out var semantic, out var replacePrev))
                {
                    AppendRecord(semantic, replacePrev, source: $"input-semantic {input.Kind}");
                    AppLog.Write("RECORD", $"OnInputAction→semantic: {semantic.PatternName}.{semantic.MethodName}" +
                        $"({string.Join(", ", semantic.Parameters ?? Array.Empty<object?>())}) elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                    return;
                }
                if (elem is null)
                {
                    // ★ 点击即关窗竞态（Qt accept()/tk destroy）：录制时元素已销毁，但点击真实发生且
                    //   该坐标回放时会命中重新打开的弹窗按钮 → coord:v1 延迟定位，保持纯语义不动光标
                    var coord = new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = "(窗口已关闭·坐标延迟定位)",
                        ElementPath = $"coord:v1|{input.X},{input.Y}",
                        PatternName = "InvokePattern",
                        MethodName = "Invoke",
                        Parameters = null,
                        Success = true,
                        Result = $"Invoke @({input.X},{input.Y})（录制时窗口瞬时关闭）",
                    };
                    AppendRecord(coord, replacePrevious: false, source: "input-semantic coord-deferred");
                    AppLog.Write("RECORD", $"OnInputAction→semantic: InvokePattern.Invoke @({input.X},{input.Y}) [coord-deferred: 点击即关窗，回放按坐标语义定位]");
                    return;
                }
                // 元素活着但无 InvokePattern：探针日志已注明（click-no-invoke），UIA 模式不录无语义动作
                return;

            case InputActionKind.KeyDown:
            {
                // ★ 键盘录制三分类（Qt/tkinter 等自绘框架的数字框/文本框唯一录入通道）：
                //   1) 回车：即时处理——先 flush 防抖键入（前面字符已处理完，读到终值），
                //      再按 值类 SetValue / Invoke（表单提交）语义化；全无 → 键盘动作兜底（回放真实按键，
                //      值类 SetValue 无法触发 Qt 弹窗 accept 关闭，回车必须真实送达）
                //   2) 文本键（数字/字母/退格/空格/标点）：进入防抖 pending，停顿 800ms/回车/
                //      非文本键/鼠标动作时读终值 → 一条 SetValue(merge)——KeyDown 时刻按键尚未被
                //      目标处理，读值必为旧值，这是"输入数字变成旧值 65"的根因之一
                //   3) 其他键（Tab/方向/F 键）：无值语义，但 Tab/回车切换焦点 → flush pending 后不录
                var isEnter = input.VkCode == 0x0D;
                var isText = IsTextVk(input.VkCode);
                if (isEnter)
                {
                    FlushPendingKeys(reason: "enter");
                    if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, out var keyRecord, out var keyMerge))
                    {
                        AppendRecord(keyRecord, keyMerge, source: $"input-semantic {input.Kind}");
                        AppLog.Write("RECORD", $"OnInputAction→semantic: {keyRecord.PatternName}.{keyRecord.MethodName}" +
                            $"({string.Join(", ", keyRecord.Parameters ?? Array.Empty<object?>())}) elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                        return;
                    }
                    // 回车兜底：键盘动作记录（UIA 语义模式唯一允许的 Input_Keyboard 记录——回车承载
                    // "提交"语义不可丢；回放 SendKeyCombo 真实按键）
                    var enter = new ActionRecord
                    {
                        Timestamp = input.Timestamp,
                        ElementName = elemName,
                        PatternName = InputSimulator.KeyboardPattern,
                        MethodName = "Enter",
                        Parameters = null,
                        Success = true,
                        Result = $"{procName} {ShortType(ctrlType)} Enter(回车兜底)",
                    };
                    AppendRecord(enter, replacePrevious: false, source: "input-semantic key-enter-fallback");
                    AppLog.Write("RECORD", $"OnInputAction→semantic: 键盘 Enter 兜底 elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                    return;
                }
                if (isText)
                {
                    QueuePendingKey(elem, elemName, procName, ctrlType);
                    return;
                }
                // Tab/方向/F 键等：切焦点信号 → 先落盘键入终值，本身无语义不录
                FlushPendingKeys(reason: "non-text-key");
                return;
            }

            case InputActionKind.MouseLeftDrag:
                if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, out var dragRecord, out var dragMerge))
                {
                    AppendRecord(dragRecord, dragMerge, source: $"input-semantic {input.Kind}");
                    AppLog.Write("RECORD", $"OnInputAction→semantic: {dragRecord.PatternName}.{dragRecord.MethodName}" +
                        $"({string.Join(", ", dragRecord.Parameters ?? Array.Empty<object?>())}) elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                    return;
                }
                return; // 无 RangeValuePattern 的拖动：探针日志已注明（drag-no-rangevalue），不录

            default:
                // 右键点击/右键拖动：右键菜单无 UIA 可回放语义，UIA 模式不录
                AppLog.Write("RECORD", $"UIA mode: {input.Kind} @({input.X},{input.Y}) 无语义等价（右键菜单），不记录");
                return;
        }
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
                statusText: $"● 已捕获 {_steps.Count} 个动作: {detail} ({uiaEvent.ElementName})");
        });
    }

    /// <summary>统一追加记录（UIA 事件/输入语义化/坐标回退共用）：
    /// replacePrevious=true 时若链路上最后一条同元素（spec 忽略 rect 抖动）同 Pattern 同方法 → 原位替换（值类合并去抖）。
    /// ★ 树形步骤为 UI 主数据（_steps）；_script.Records 同步维护（旧格式导出/计数兼容）。
    /// ★ "插入动作"锚点（_insertAnchor）生效时：新动作依次插到锚点之后（选中行上方连续插入），并推进锚点。</summary>
    private void AppendRecord(ActionRecord record, bool replacePrevious, string source, string? statusText = null)
    {
        var merged = false;
        if (replacePrevious && _lastAppendedNode is { Kind: StepKinds.Action } lastNode
            && lastNode.Action is not null && MergeKey(lastNode.Action) == MergeKey(record))
        {
            lastNode.Action = record;
            if (_script.Records.Count > 0) _script.Records[^1] = record;
            merged = true;
        }
        else
        {
            var node = new StepNode { Kind = StepKinds.Action, Action = record };
            InsertRecordedNode(node);
            _script.Records.Add(record);
        }

        RefreshTree();
        var detail = record.Parameters is { Length: > 0 }
            ? $"{record.MethodName}({string.Join(", ", record.Parameters)})"
            : record.MethodName;
        TxtStatus.Text = statusText ?? $"● 已捕获 {_steps.Count} 个动作: {detail}" +
                         (record.ElementName != "" ? $" ({record.ElementName})" : "") +
                         (_insertAnchor is not null ? "（插入模式）" : "");
        if (merged)
            AppLog.Write("RECORD", $"AppendRecord merge[{source}]: {record.PatternName}.{record.MethodName}" +
                $"({string.Join(", ", record.Parameters ?? Array.Empty<object?>())}) elem={record.ElementName}");
    }

    /// <summary>录制动作落树：锚点模式下插到锚点节点之后（连续保持录制顺序），否则根级末尾追加。</summary>
    private void InsertRecordedNode(StepNode node)
    {
        if (_insertAnchor is not null)
        {
            var list = FindParentList(_insertAnchor, _steps, out var idx);
            if (list is not null)
            {
                list.Insert(idx + 1, node);
                _insertAnchor = node;
                _lastAppendedNode = node;
                return;
            }
            _insertAnchor = null; // 锚点被外部删除（如清空）→ 回落根级末尾
        }
        _steps.Add(node);
        _lastAppendedNode = node;
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

                case InputActionKind.KeyDown:
                {
                    // ★ 仅回车走此分支（文本键已分流到防抖 pending——KeyDown 时刻按键尚未被目标处理，
                    //   即时读值必为旧值；回车时刻前面字符均已处理完，读到的是终值）。
                    //   回车语义：值类控件 → SetValue 终值（Qt 数字框回车=提交编辑，值不变）；
                    //   按钮焦点 → Invoke（表单提交）。RangeValue 是 Qt QDoubleSpinBox/QSlider 等
                    //   数字控件的关键通道（它们没有 ValuePattern），此前缺失导致数字框回车无记录。
                    if (input.VkCode != 0x0D) break;
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
                    if (elem.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rpo) && rpo is RangeValuePattern rp && !rp.Current.IsReadOnly)
                    {
                        var value = rp.Current.Value;
                        replacePrevious = true;
                        record = BuildSemantic(elem, "RangeValuePattern", "SetValue", new object?[] { value },
                            result: $"{procName} {ShortType(ctrlType)} → {value}", elemName, input.Timestamp);
                        return true;
                    }
                    // 回车且焦点无值类 Pattern（按钮焦点）→ Invoke（表单提交语义）
                    if (elem.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                    {
                        replacePrevious = false;
                        record = BuildSemantic(elem, "InvokePattern", "Invoke", null,
                            result: $"{procName} {ShortType(ctrlType)} Invoke(Enter)", elemName, input.Timestamp);
                        return true;
                    }
                    LogPatternProbe(elem, elemName, ctrlType, procName, "key-no-value");
                    break;
                }

                case InputActionKind.MouseLeftDrag:
                {
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
    /// 回车在 KeyDown 分支单独处理（Value 优先、按钮焦点时 Invoke）；Tab/方向/F 键等无语义不录。</summary>
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

    /// <summary>取元素所属顶层窗口句柄：沿控件视图向上，返回第一个非零 NativeWindowHandle。
    /// ★ 控件视图里最近的窗口句柄祖先就是所属窗口（弹窗内按钮→QDialog 句柄；主窗口按钮→主窗口句柄），
    ///   与钩子快照的 WindowFromPoint 同源可比。
    /// ★ 不能"爬到 parent 为 null 才停"——那样会爬过顶层窗口到达桌面根：日志实锤根元素返回伪句柄 65548
    ///   （恒定非零），与真实窗口句柄永不相等，导致所有正常点击被误判"窗口已销毁"全变 coord 定位。</summary>
    private static IntPtr TopLevelHwndOf(AutomationElement e)
    {
        try
        {
            var cur = e;
            for (int i = 0; i < 16; i++)
            {
                try
                {
                    var h = cur.Current.NativeWindowHandle;
                    if (h != IntPtr.Zero) return h;
                }
                catch { /* 元素销毁中，继续向父级找 */ }
                var parent = TreeWalker.ControlViewWalker.GetParent(cur);
                if (parent is null) break;
                cur = parent;
            }
            return IntPtr.Zero;
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

    // ==== 键盘防抖终值（文本键 → pending → 停顿/回车/鼠标动作时读终值一条 SetValue） ====

    /// <summary>文本键进入防抖 pending：重置 800ms 定时器，停顿到期 flush 读终值。
    /// elem 为 null（焦点解析失败）也 pending——flush 时重试解析链（此时目标可能已可读）。</summary>
    private void QueuePendingKey(AutomationElement? elem, string elemName, string procName, string ctrlType)
    {
        _pendingKeyElem = elem;
        _pendingKeyName = elemName; _pendingKeyProc = procName; _pendingKeyType = ctrlType;
        _pendingKeyAt = DateTime.Now;
        _keyDebounce ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
        _keyDebounce.Tick -= OnKeyDebounceTick;   // 幂等订阅（防重入叠加）
        _keyDebounce.Tick += OnKeyDebounceTick;
        _keyDebounce.Stop();
        _keyDebounce.Start();
        TxtStatus.Text = $"● 键入中（{elemName}）…停顿后记录终值" + (_insertAnchor is not null ? "（插入模式）" : "");
        AppLog.Write("RECORD", $"KeyQueue: elem={elemName} type={ctrlType} pending（800ms 停顿/回车/鼠标动作时读终值）");
    }

    private void OnKeyDebounceTick(object? sender, EventArgs e) => FlushPendingKeys(reason: "debounce");

    /// <summary>落盘防抖中的键入终值：此刻所有已敲字符均已被目标进程处理，读到的是最终值。
    /// 值类 Pattern 优先 Value（文本）再 RangeValue（数字框/滑块），生成 SetValue(merge=true)——
    /// 与拖动误捕的同元素旧值记录经 MergeKey 原位替换（拖选文本误捕的 SetValue(65) 被键入终值覆盖）。
    /// 触发时机：800ms 停顿 / 回车 / 非文本键（Tab 切焦点）/ 任何鼠标动作 / 停止录制。</summary>
    private void FlushPendingKeys(string reason)
    {
        var elem = _pendingKeyElem;
        var name = _pendingKeyName; var proc = _pendingKeyProc; var type = _pendingKeyType;
        if (elem is null && name == "") return; // 无 pending
        _keyDebounce?.Stop();
        _pendingKeyElem = null; _pendingKeyName = ""; _pendingKeyProc = ""; _pendingKeyType = "";
        if (!_isRecording) return;

        // pending 时元素解析失败（FocusedElement=null 且当时无缓存）→ 此刻重试解析链
        if (elem is null)
        {
            elem = TryResolveKeyTarget();
            if (elem is not null)
            {
                try
                {
                    name = elem.Current.Name ?? ""; type = elem.Current.ControlType?.ProgrammaticName ?? "";
                    proc = Process.GetProcessById(elem.Current.ProcessId).ProcessName;
                }
                catch { }
            }
        }
        if (elem is null)
        {
            AppLog.Write("RECORD", $"KeyFlush[{reason}]: element resolve still null → 键入值放弃（无值语义载体）");
            return;
        }

        try
        {
            // 值类终值：Value 优先（文本框），RangeValue 次之（QDoubleSpinBox/QSlider 等数字控件）
            if (elem.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo) && vpo is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                var value = vp.Current.Value;
                if (value is not null)
                {
                    var rec = BuildSemantic(elem, "ValuePattern", "SetValue", new object?[] { value },
                        result: $"{proc} {ShortType(type)} → \"{value}\"", name, _pendingKeyAt);
                    AppendRecord(rec, replacePrevious: true, source: $"input-semantic key-flush({reason})");
                    AppLog.Write("RECORD", $"KeyFlush[{reason}]→semantic: ValuePattern.SetValue(\"{value}\") elem={name} type={type}");
                    return;
                }
            }
            if (elem.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rpo) && rpo is RangeValuePattern rp && !rp.Current.IsReadOnly)
            {
                var value = rp.Current.Value;
                var rec = BuildSemantic(elem, "RangeValuePattern", "SetValue", new object?[] { value },
                    result: $"{proc} {ShortType(type)} → {value}", name, _pendingKeyAt);
                AppendRecord(rec, replacePrevious: true, source: $"input-semantic key-flush({reason})");
                AppLog.Write("RECORD", $"KeyFlush[{reason}]→semantic: RangeValuePattern.SetValue({value}) elem={name} type={type}");
                return;
            }
            // 无值类 Pattern（纯文本展示区等）：键入无语义载体 → 探针日志注明
            LogPatternProbe(elem, name, type, proc, "key-no-value");
        }
        catch (Exception ex)
        {
            // 元素已销毁（弹窗被关）：键入值随窗口消失，无从落盘
            AppLog.Write("RECORD", $"KeyFlush[{reason}]: element gone ({ex.GetType().Name}: {ex.Message}) → 键入值放弃");
        }
    }

    /// <summary>KeyDown 焦点元素解析链：FocusedElement → 最近鼠标命中元素(15s) → 鼠标位置 FromPoint。
    /// ★ Qt/tkinter 等自绘窗口 FocusedElement 实测返回 null（不抛异常）——正是键盘输入整体丢失的根因：
    ///   历史代码 elem=null 时静默 return，日志零痕迹。键入场景鼠标通常刚点过输入框/仍停在其上，回退命中率高。</summary>
    private AutomationElement? TryResolveKeyTarget()
    {
        AutomationElement? elem = null;
        try { elem = AutomationElement.FocusedElement; } catch { /* 焦点窗口销毁等，走回退 */ }
        if (elem is not null) return elem;

        if (_recentClickElem is not null && (DateTime.Now - _recentClickTick).TotalSeconds < 15)
        {
            try { _ = _recentClickElem.Current.ProcessId; return _recentClickElem; }
            catch { _recentClickElem = null; } // 元素已销毁，继续回退
        }
        try
        {
            GetCursorPos(out var pt);
            var byCursor = AutomationElement.FromPoint(new Point(pt.X, pt.Y));
            AppLog.Write("RECORD", "KeyDown: FocusedElement null → 回退 FromPoint(鼠标位置)");
            return byCursor;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WinPoint pt);
    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X, Y; }

    private static string TruncLog(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void BtnClearRecord_Click(object sender, RoutedEventArgs e)
    {
        // 先停止输入钩子与自动监听
        StopInputHook();
        StopUiaListenerQuiet();
        _uiaSemanticMode = false;
        _patternProbeLogged.Clear();

        _script.Records.Clear();
        _steps.Clear();
        _currentTask = null;
        _stepIndex = -1;
        _isRecording = false;
        _insertAnchor = null;
        _lastAppendedNode = null;
        TxtAutoHint.Visibility = Visibility.Collapsed;
        RefreshTree();
        TxtStatus.Text = "已清空";
        BtnToggleRecord.Content = "● 开始录制";
        BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Black;
        RefreshUI();
    }

    // ==== 模式切换（★录制中也可切换：手动=停自动通道只录 Actions 对话框；自动=重启事件/输入捕获） ====

    private void RbModeManual_Checked(object sender, RoutedEventArgs e)
    {
        _isAutoMode = false;
        TxtAutoHint.Visibility = Visibility.Collapsed;
        if (_isRecording)
        {
            StopInputHook();
            StopUiaListenerQuiet();
            _uiaSemanticMode = false;
            TxtStatus.Text = "● 已切换为手动模式录制…执行 Actions 将自动记录（自动捕获已停止）";
            AppLog.Write("RECORD", "Mode switched to MANUAL while recording (auto channels stopped)");
        }
    }

    private void RbModeAuto_Checked(object sender, RoutedEventArgs e)
    {
        _isAutoMode = true;
        TxtAutoHint.Visibility = Visibility.Visible;
        if (_isRecording)
        {
            TxtStatus.Text = "● 已切换为自动模式录制…（自动捕获通道重启中）";
            AppLog.Write("RECORD", "Mode switched to AUTO while recording (auto channels restarting)");
            StartAutoRecording();
        }
    }

    // ==== 回放控制（★树形步骤执行：循环/延时/定时/触发器由 RecordingTaskRunner 驱动） ====

    private void BtnPlayAll_Click(object sender, RoutedEventArgs e)
    {
        // 再点一次 = 停止回放（触发器/定时器等待中可中断）
        if (_playRunner is not null)
        {
            _playRunner.Stop();
            return;
        }
        if (_steps.Count == 0) return;

        var task = _currentTask != null
            ? new RecordingTask { Name = $"{_currentTask.Name}（面板回放）", Steps = _steps, Mode = _currentTask.Mode }
            : new RecordingTask { Name = "面板回放", Steps = _steps, Mode = _uiaSemanticMode ? "uia" : "input" };
        StartPlayback(task);
    }

    /// <summary>启动回放执行器（"全部回放"与右键"运行此步骤"共用；单步=只含选中节点的单根任务）。</summary>
    private void StartPlayback(RecordingTask task)
    {
        if (_playRunner is not null) return;
        BtnPlayAll.Content = "■ 停止回放";
        BtnStep.IsEnabled = false;
        TxtStatus.Text = "▶ 回放中…";

        _playRunner = new RecordingTaskRunner(task, ResolveElement ?? (_ => null));
        _playRunner.StateChanged += snap => Dispatcher.BeginInvoke(() =>
        {
            var stateText = snap.State switch
            {
                RecTaskRunState.WaitingTrigger => "⏳ 等待触发条件",
                RecTaskRunState.WaitingTimer => "⏰ 等待定时时刻",
                _ => "▶",
            };
            TxtStatus.Text = $"{stateText} 回放中 ({snap.CurrentIndex + 1}/{snap.TotalSteps})：{snap.CurrentStep}" +
                (snap.LoopRound > 0 ? $" [第 {snap.LoopRound} 轮]" : "");
            if (snap.State is RecTaskRunState.Completed or RecTaskRunState.Failed or RecTaskRunState.Cancelled)
            {
                TxtStatus.Text = snap.State switch
                {
                    RecTaskRunState.Completed => $"✓ 回放完成（{snap.TotalSteps} 步）",
                    RecTaskRunState.Cancelled => "■ 回放已停止",
                    _ => $"✗ 回放失败：{snap.LastError}",
                };
                _playRunner.Dispose();
                _playRunner = null;
                BtnPlayAll.Content = "▶ 全部回放";
                BtnStep.IsEnabled = !_isRecording;
                RefreshTree(); // 回写结果显示
            }
        });
        _playRunner.Start();
    }

    private void BtnStep_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0 || ResolveElement is null) return;

        _stepIndex++;
        if (_stepIndex >= _steps.Count)
        {
            _stepIndex = -1;
            TxtStatus.Text = "单步回放已到达末尾，重新开始";
            return;
        }

        var node = _steps[_stepIndex];
        SelectVmByNode(node);
        if (node.Kind != StepKinds.Action)
        {
            TxtStatus.Text = $"→ [{_stepIndex + 1}] {node.DisplayText}：控制节点请在'全部回放'中执行";
            return;
        }
        var record = node.Action!;
        TxtStatus.Text = $"→ 单步 [{_stepIndex + 1}/{_steps.Count}] {record.MethodName}()...";

        try
        {
            using var replayer = new ActionReplayer();
            // 不需要元素重定位的记录：Input 记录（直接 SendInput）、coord:v1 延迟定位（回放时 FromPoint
            // 语义执行）、UIA 事件记录（不可回放仅提示）——传 spec 会误走"窗口根兜底"解析（误导日志）
            var isInput = record.PatternName is InputSimulator.MousePattern or InputSimulator.KeyboardPattern
                || (record.ElementPath?.StartsWith("coord:v1|", StringComparison.Ordinal) ?? false)
                || record.PatternName.StartsWith("UIA_Event_", StringComparison.OrdinalIgnoreCase);
            var targetElem = isInput ? null
                : ResolveElement(record.ElementPath ?? "")
                  ?? throw new Exception($"元素路径 [{record.ElementPath}] 未找到");
            var (ok, result) = replayer.ExecuteOne(record, targetElem);

            node.Action = record with { Success = ok, Result = result };
            RefreshTree();
            SelectVmByNode(_steps[_stepIndex]);
            TxtStatus.Text = ok
                ? $"→ [{_stepIndex + 1}] {record.MethodName}() ✓"
                : $"→ [{_stepIndex + 1}] {record.MethodName}() ✗ {result}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"→ [{_stepIndex + 1}] 错误: {ex.Message}";
        }
    }

    // ==== 录制任务（JSON 存于程序目录 RecordingTasks\） ====

    private static string TaskDir
    {
        get
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "RecordingTasks");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>保存为新任务：输入名称 → 步骤树快照存 JSON。</summary>
    private void BtnCreateTask_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0) return;
        var name = PromptDialog.Show("创建录制任务", "任务名称：", _currentTask?.Name ?? $"录制任务_{DateTime.Now:yyyyMMdd_HHmmss}");
        if (string.IsNullOrWhiteSpace(name)) return;

        var task = new RecordingTask
        {
            Name = name.Trim(),
            Mode = _uiaSemanticMode ? "uia" : "input",
            Steps = CloneSteps(_steps),
        };
        try
        {
            var file = System.IO.Path.Combine(TaskDir, SafeFileName(task.Name) + ".json");
            System.IO.File.WriteAllText(file, task.ToJson());
            _currentTask = task;
            RefreshUI();
            RefreshTree();
            TxtStatus.Text = $"✓ 已创建录制任务 '{task.Name}' → {file}";
            AppLog.Write("RECORD", $"RecordingTask created: '{task.Name}' ({_steps.Count} steps) → {file}");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"✗ 创建任务失败: {ex.Message}";
        }
    }

    /// <summary>打开任务：载入 JSON → 继续编辑（右键插控制节点/上移下移/删除）。</summary>
    private void BtnOpenTask_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "录制任务 JSON|*.json",
            InitialDirectory = TaskDir,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            var task = RecordingTask.FromJson(json);
            if (task is null || task.Steps.Count == 0)
            { TxtStatus.Text = "打开失败：无效的录制任务文件"; return; }

            _currentTask = task;
            _steps.Clear();
            _steps.AddRange(task.Steps);
            _stepIndex = -1;
            RefreshTree();
            RefreshUI();
            TxtStatus.Text = $"已打开任务 '{task.Name}'（{task.Steps.Count} 个顶层步骤）— 可右键编辑，回放按树形语义执行";
            AppLog.Write("RECORD", $"RecordingTask opened: '{task.Name}' ({task.Steps.Count} top steps)");
        }
        catch (Exception ex) { TxtStatus.Text = $"打开任务失败: {ex.Message}"; }
    }

    /// <summary>保存当前任务的修改（未打开任务时等同另存）。</summary>
    private void BtnSaveTask_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0) return;
        RecordingTask task;
        string file;
        if (_currentTask is not null)
        {
            task = _currentTask;
            task.Steps = CloneSteps(_steps);
            task.UpdatedAt = DateTime.Now;
            file = System.IO.Path.Combine(TaskDir, SafeFileName(task.Name) + ".json");
        }
        else
        {
            BtnCreateTask_Click(sender, e);
            return;
        }
        try
        {
            System.IO.File.WriteAllText(file, task.ToJson());
            RefreshTree();
            TxtStatus.Text = $"✓ 任务 '{task.Name}' 已保存 → {file}";
        }
        catch (Exception ex) { TxtStatus.Text = $"✗ 保存失败: {ex.Message}"; }
    }

    // ==== 导入/导出（旧平铺格式兼容） ====

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "JSON 脚本|*.json",
            FileName = $"action_script_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 旧格式导出：树形展平为动作序列（控制节点不进旧格式；完整版用"保存为新任务"）
            _script = new ActionScript
            {
                CreatedAt = DateTime.Now,
                Records = FlattenActions(_steps).ToList(),
            };
            System.IO.File.WriteAllText(dlg.FileName, _script.ToJson());
            TxtStatus.Text = $"已导出 { _script.Records.Count} 条动作（旧平铺格式；控制节点请用任务保存）: {dlg.FileName}";
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
            // 新任务格式优先
            var task = RecordingTask.FromJson(json);
            if (task is not null && task.Steps.Count > 0)
            {
                _currentTask = task;
                _steps.Clear();
                _steps.AddRange(task.Steps);
                _stepIndex = -1;
                RefreshTree();
                RefreshUI();
                TxtStatus.Text = $"已导入任务 '{task.Name}'（{task.Steps.Count} 个顶层步骤）";
                return;
            }
            var imported = ActionScript.FromJson(json);
            if (imported is null || imported.Records.Count == 0)
            { TxtStatus.Text = "导入失败：无效的脚本文件"; return; }

            _script = imported;
            _currentTask = null;
            _steps.Clear();
            foreach (var r in imported.Records)
                _steps.Add(new StepNode { Kind = StepKinds.Action, Action = r });
            _stepIndex = -1;
            RefreshTree();
            TxtStatus.Text = $"已导入 {imported.Records.Count} 条操作（来自 {dlg.FileName}）";
            RefreshUI();
        }
        catch (Exception ex) { TxtStatus.Text = $"导入失败: {ex.Message}"; }
    }

    // ==== 内部辅助 ====

    private static IEnumerable<ActionRecord> FlattenActions(IEnumerable<StepNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Kind == StepKinds.Action && n.Action is not null) yield return n.Action;
            foreach (var a in FlattenActions(n.Children)) yield return a;
        }
    }

    /// <summary>深拷贝步骤树（保存任务时快照，回放不污染编辑中的树）。</summary>
    private static List<StepNode> CloneSteps(IEnumerable<StepNode> nodes)
    {
        var result = new List<StepNode>();
        foreach (var n in nodes)
        {
            result.Add(new StepNode
            {
                Id = n.Id,
                Kind = n.Kind,
                IsEnabled = n.IsEnabled,
                Action = n.Action,
                LoopCount = n.LoopCount,
                LoopIntervalMs = n.LoopIntervalMs,
                DelayMs = n.DelayMs,
                TriggerAt = n.TriggerAt,
                Trigger = n.Trigger,
                Children = CloneSteps(n.Children),
            });
        }
        return result;
    }

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
        var hasRecords = _steps.Count > 0;
        BtnClearRecord.IsEnabled = hasRecords;
        BtnPlayAll.IsEnabled = hasRecords && !_isRecording;
        BtnPlayAll.Content = _playRunner is not null ? "■ 停止回放" : "▶ 全部回放";
        BtnStep.IsEnabled = hasRecords && !_isRecording && _playRunner is null;
        BtnExport.IsEnabled = hasRecords;
        BtnCreateTask.IsEnabled = hasRecords;
        BtnSaveTask.IsEnabled = _currentTask is not null;
        // ★ 录制中允许切换模式（RbMode 事件处理热切换），不再禁用 RadioButton
    }

    // ==== 步骤树（TreeView 绑定 + 右键编辑） ====

    /// <summary>步骤树行 ViewModel（绑定 DisplayText/Detail/DisabledMark/Children；IsMultiSelected=Shift 范围多选高亮）。</summary>
    public sealed class StepVm : System.ComponentModel.INotifyPropertyChanged
    {
        public required StepNode Node { get; init; }
        public List<StepVm> Children { get; init; } = new();
        public string DisplayText => Node.DisplayText;
        public string Detail => Node.DetailText;
        public string DisabledMark => Node.IsEnabled ? "Collapsed" : "Visible";
        public StepVm? Parent { get; init; }

        private bool _isMultiSelected;
        /// <summary>Shift 范围多选标记（背景淡蓝；Tab 批量移入容器作用于多选集）。</summary>
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected != value) { _isMultiSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsMultiSelected))); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private void RefreshTree()
    {
        TvSteps.ItemsSource = BuildVmList(_steps, null);
    }

    private static List<StepVm> BuildVmList(List<StepNode> nodes, StepVm? parent)
    {
        var list = new List<StepVm>();
        foreach (var n in nodes)
            list.Add(new StepVm { Node = n, Parent = parent, Children = BuildVmList(n.Children, null) });
        return list;
    }

    private void TvSteps_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedVm = e.NewValue as StepVm;
        if (_selectedVm is not null)
        {
            _anchorVm = _selectedVm; // Shift 范围多选的锚点跟随最近一次单选
            ClearMultiSelect();
            var n = _selectedVm.Node;
            TxtStatus.Text = n.Kind == StepKinds.Action && n.Action is not null
                ? $"[{n.Action.PatternName}.{n.Action.MethodName}] 路径={n.Action.ElementPath}"
                : $"{n.DisplayText} {n.DetailText}";
        }
    }

    // ==== Shift 范围多选 + Tab 批量移入容器 ====

    /// <summary>Shift+点击：从锚点（上次单选）到点击行的可见范围全部标记多选（阻止默认单选跳变）。</summary>
    private void TvSteps_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Shift) return;
        var vm = VmFromPoint(e.GetPosition(TvSteps));
        if (vm is null) return;
        e.Handled = true; // 阻止 TreeView 默认单选改变（保持多选集）
        SelectRange(_anchorVm ?? _selectedVm, vm);
    }

    /// <summary>Tab：把多选（或单选）步骤批量移入上方最近的循环/触发器容器。</summary>
    private void TvSteps_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Tab) return;
        var targets = MultiOrSingleNodes();
        if (targets.Count == 0) return;
        e.Handled = true;
        MoveNodesIntoContainer(targets);
    }

    /// <summary>命中坐标处的行 ViewModel（沿可视树向上找 TreeViewItem.Header）。</summary>
    private StepVm? VmFromPoint(Point p)
    {
        var dep = TvSteps.InputHitTest(p) as DependencyObject;
        while (dep is not null && !ReferenceEquals(dep, TvSteps))
        {
            if (dep is TreeViewItem tvi && tvi.Header is StepVm vm) return vm;
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    /// <summary>标记锚点→目标之间的全部行为多选（可见顺序扁平化取范围）。</summary>
    private void SelectRange(StepVm? anchor, StepVm target)
    {
        var flat = FlattenVms(TvSteps.ItemsSource as List<StepVm>);
        int a = flat.IndexOf(anchor ?? target);
        int b = flat.IndexOf(target);
        if (a < 0 || b < 0) return;
        for (int i = 0; i < flat.Count; i++)
            flat[i].IsMultiSelected = i >= Math.Min(a, b) && i <= Math.Max(a, b);
    }

    private void ClearMultiSelect()
    {
        foreach (var vm in FlattenVms(TvSteps.ItemsSource as List<StepVm>))
            vm.IsMultiSelected = false;
    }

    /// <summary>可见顺序扁平化全部行 VM（含子级，用于范围多选与多选收集）。</summary>
    private static List<StepVm> FlattenVms(List<StepVm>? roots)
    {
        var list = new List<StepVm>();
        void Walk(List<StepVm> vms)
        {
            foreach (var vm in vms) { list.Add(vm); Walk(vm.Children); }
        }
        if (roots is not null) Walk(roots);
        return list;
    }

    private void TvSteps_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // TreeView 右键默认不改变选中：保持 _selectedVm（未选中时操作目标=根级末尾）
    }

    private void SelectVmByNode(StepNode node) { /* 选中展开由 RefreshTree 后 ItemsSource 重建，保持简单 */ }

    // ---- 右键菜单：插入控制节点（插入到选中步骤上方；未选中→根级末尾） ----

    private (List<StepNode> List, int Index) ResolveInsertPosition()
    {
        if (_selectedVm is null)
            return (_steps, _steps.Count); // 未选中：根级末尾（录制中新动作会续在其后）
        // 选中任意节点（含容器）：插到它父列表中它的上方（同级之前）
        var parentList = FindParentList(_selectedVm.Node, _steps, out var idx);
        return parentList is not null ? (parentList, idx) : (_steps, _steps.Count);
    }

    private static List<StepNode>? FindParentList(StepNode node, List<StepNode> list, out int index)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], node)) { index = i; return list; }
            var sub = FindParentList(node, list[i].Children, out index);
            if (sub is not null) return sub;
        }
        index = -1;
        return null;
    }

    private void InsertNode(string kind)
    {
        var (list, index) = ResolveInsertPosition();
        var node = new StepNode { Kind = kind };
        if (kind == StepKinds.Timer)
            node.TriggerAt = DateTime.Now.AddHours(1); // 默认 1 小时后
        list.Insert(index, node);

        // 新控制节点立即弹编辑（循环/延时/定时可改默认值；取消=放弃插入）
        if (EditNodeDialog(node))
        {
            RefreshTree();
            RefreshUI();
            TxtStatus.Text = $"已插入 {node.DisplayText}（共 {_steps.Count} 个顶层步骤）";
        }
        else
        {
            list.RemoveAt(index); // 取消编辑=放弃插入
        }
    }

    private bool EditNodeDialog(StepNode node)
    {
        // 触发器/截图：目标元素来自已保存配置（重配置元素=删除后重新插入捕获）；读值经 ResolveElement 实时解析
        string spec = node.Kind == StepKinds.Screenshot ? node.ScreenshotSpec : node.Trigger?.ElementSpec ?? "";
        string name = node.Kind == StepKinds.Screenshot ? node.ScreenshotName : node.Trigger?.ElementName ?? "";
        var dlg = new StepEditDialog(node, spec, name,
            prop => RecordingTaskRunner.TryReadProperty(ResolveElement, spec, prop, out var v) ? v : null)
        { Owner = Window.GetWindow(this) };
        return dlg.ShowDialog() == true;
    }

    /// <summary>烧杯捕获完成（MainWindow OnSnapshotAddTarget 调用，UI 线程）：
    /// 以捕获的悬停元素为目标弹触发器编辑，确认后按"上方插入"规则加入步骤树。</summary>
    internal void OnTriggerElementCaptured(string spec, string elementName)
    {
        try
        {
            var node = new StepNode { Kind = StepKinds.Trigger };
            var dlg = new StepEditDialog(node, spec, elementName,
                prop => RecordingTaskRunner.TryReadProperty(ResolveElement, spec, prop, out var v) ? v : null)
            { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() == true)
            {
                var (list, index) = ResolveInsertPosition();
                list.Insert(index, node);
                RefreshTree();
                RefreshUI();
                TxtStatus.Text = $"已插入触发器：{node.DisplayText}（共 {_steps.Count} 个顶层步骤）";
            }
            else
                TxtStatus.Text = "已取消触发器插入";
        }
        finally
        {
            // 右键插入发起的捕获（非录制中）：完成后停止静默捕获；录制中点烧杯则录制继续不受影响
            if (_triggerPickSolo)
            {
                _triggerPickSolo = false;
                StopTriggerCapture?.Invoke();
            }
        }
    }

    private void MenuInsertLoop_Click(object sender, RoutedEventArgs e) => InsertNode(StepKinds.Loop);
    private void MenuInsertDelay_Click(object sender, RoutedEventArgs e) => InsertNode(StepKinds.Delay);
    private void MenuInsertTimer_Click(object sender, RoutedEventArgs e) => InsertNode(StepKinds.Timer);

    /// <summary>插入触发器：不立即弹窗——启动随鼠标捕获（蓝框+烧杯），用户点烧杯选定元素后再弹编辑。</summary>
    private void MenuInsertTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (StartTriggerCapture?.Invoke() != true)
        {
            TxtStatus.Text = "无法启动捕获（正常\"随鼠标捕获\"进行中？先停止它再插入触发器）";
            return;
        }
        _triggerPickSolo = !_isRecording; // 录制中静默捕获本就在跑（用完不停）；未录制则为插入而启动（完成即停）
        TxtStatus.Text = "⚗ 触发器：把鼠标移到要监控的元素上，点蓝框右上角 🧪 烧杯按钮选定目标…";
    }

    /// <summary>插入截图步骤：启动随鼠标捕获（蓝框右上角=剪刀按钮），点剪刀→选保存目录→试截一张→上方插入。</summary>
    private void MenuInsertScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenshotCapture?.Invoke() != true)
        {
            TxtStatus.Text = "无法启动捕获（正常\"随鼠标捕获\"进行中？先停止它再插入截图步骤）";
            return;
        }
        _screenshotPickSolo = !_isRecording; // 录制中静默捕获本就在跑（用完不停）；未录制则为插入而启动（完成即停）
        TxtStatus.Text = "📷 截图：把鼠标移到要截图的元素上，点蓝框右上角 ✂ 剪刀按钮选定目标…";
    }

    /// <summary>剪刀捕获完成（MainWindow OnSnapshotAddTarget 截图分叉调用，UI 线程）：
    /// 弹目录选择 → 立即按捕获矩形试截一张 img-时间戳.png → 按"上方插入"规则加入截图步骤。</summary>
    internal void OnScreenshotCaptured(string spec, string elementName, System.Drawing.Rectangle captureRect)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择截图保存目录（任务运行时自动保存到该目录，不再弹窗）",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true)
            {
                TxtStatus.Text = "已取消截图步骤插入";
                return;
            }
            var dir = dlg.FolderName;

            // 立即试截一张（img-时间戳.png 存入所选目录）：验证截图链路并给用户即时产物
            var (ok, file) = ScreenCapture.CaptureRegion(captureRect, dir);

            var node = new StepNode
            {
                Kind = StepKinds.Screenshot,
                ScreenshotSpec = spec,
                ScreenshotName = elementName,
                ScreenshotDir = dir,
            };
            var (list, index) = ResolveInsertPosition();
            list.Insert(index, node);
            RefreshTree();
            RefreshUI();
            TxtStatus.Text = ok
                ? $"📷 已插入截图步骤（预览已保存 → {file}）"
                : $"📷 已插入截图步骤（预览失败：{file}；运行时将按元素定位重试）";
            AppLog.Write("RECORD", ok
                ? $"Screenshot step inserted (elem='{elementName}'), preview → {file}"
                : $"Screenshot step inserted (elem='{elementName}'), preview FAILED: {file}");
        }
        finally
        {
            // 右键插入发起的捕获（非录制中）：完成后停止静默捕获并恢复烧杯图标；录制中则录制继续
            if (_screenshotPickSolo)
            {
                _screenshotPickSolo = false;
                StopScreenshotCapture?.Invoke();
            }
        }
    }

    /// <summary>插入动作：未录制时自动开启录制（★保留当前步骤树不清空），此后录制的动作依次插到选中行上方。</summary>
    private void MenuInsertAction_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            // 与"开始录制"按钮启动分支一致，但不清空步骤树（插入场景=往现有脚本中插）
            _isAutoMode = ((RadioButton)RbModeAuto).IsChecked == true;
            _script = new ActionScript { CreatedAt = DateTime.Now }; // 计数重置，_steps 保留
            _stepIndex = -1;
            _isRecording = true;
            _lastAppendedNode = null;
            if (_isAutoMode)
                StartAutoRecording();
            else
                TxtStatus.Text = "● 手动录制中…执行 Actions 将自动记录";
            BtnToggleRecord.Content = "■ 停止录制";
            BtnToggleRecord.Foreground = System.Windows.Media.Brushes.Red;
            RefreshUI();
            RecordingStateChanged?.Invoke(true); // 触发 MainWindow 启动静默捕获
        }
        // 锚点=当前选中节点（录制中再次点此菜单=换插入位置；未选中→根级末尾追加）
        _insertAnchor = _selectedVm?.Node;
        _lastAppendedNode = _insertAnchor; // merge 基点跟随锚点（非动作节点时 merge 判定自动跳过）
        TxtStatus.Text = _insertAnchor is not null
            ? $"● 插入动作：录制中，新动作将依次插到「{_insertAnchor.DisplayText}」上方（停止录制自动退出插入模式）"
            : "● 插入动作：录制中，新动作将追加到末尾（先选中一行可插到它上方）";
    }

    /// <summary>右键运行选中步骤（含子树：循环/触发器按展开语义执行；运行中再点=停止）。</summary>
    private void MenuRunStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm is null) { TxtStatus.Text = "请先选中要运行的步骤"; return; }
        if (_isRecording) { TxtStatus.Text = "录制中不能运行步骤（先停止录制）"; return; }
        if (_playRunner is not null) { _playRunner.Stop(); return; }
        var node = _selectedVm.Node;
        if (!node.IsEnabled) { TxtStatus.Text = $"该步骤已禁用（⏸）：{node.DisplayText}"; return; }
        var task = new RecordingTask { Name = $"单步运行：{node.DisplayText}", Steps = new List<StepNode> { node } };
        StartPlayback(task);
        TxtStatus.Text = $"▶ 运行此步骤：{node.DisplayText}…";
    }

    private void MenuEditStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm is null) return;
        var node = _selectedVm.Node;
        if (node.Kind == StepKinds.Action) { TxtStatus.Text = "动作步骤由录制产生，无编辑项"; return; }
        if (EditNodeDialog(node)) { RefreshTree(); TxtStatus.Text = $"已更新 {node.DisplayText}"; }
    }

    private void MenuDeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm is null) return;
        var list = FindParentList(_selectedVm.Node, _steps, out var idx);
        if (list is null) return;
        var desc = _selectedVm.Node.DisplayText;
        list.RemoveAt(idx);
        _selectedVm = null;
        RefreshTree();
        RefreshUI();
        TxtStatus.Text = $"已删除 {desc}";
    }

    private void MenuMoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MenuMoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (_selectedVm is null) return;
        var list = FindParentList(_selectedVm.Node, _steps, out var idx);
        if (list is null) return;
        var target = idx + delta;
        if (target < 0 || target >= list.Count) return;
        (list[idx], list[target]) = (list[target], list[idx]);
        RefreshTree();
    }

    /// <summary>把选中（或多选）步骤移入各自上方最近的循环/触发器容器（右键与 Tab 键共用；保持原有顺序）。</summary>
    private void MenuMoveIntoContainer_Click(object sender, RoutedEventArgs e) => MoveNodesIntoContainer(MultiOrSingleNodes());

    /// <summary>参与批量操作的目标节点：Shift 多选优先，否则当前单选。</summary>
    private List<StepNode> MultiOrSingleNodes()
    {
        var multi = FlattenVms(TvSteps.ItemsSource as List<StepVm>)
            .Where(vm => vm.IsMultiSelected)
            .Select(vm => vm.Node)
            .ToList();
        if (multi.Count > 0) return multi;
        return _selectedVm is not null ? new List<StepNode> { _selectedVm.Node } : new List<StepNode>();
    }

    /// <summary>逐个把节点移入其父列表上方最近的循环/触发器容器（按列表顺序处理保持进入容器的先后）。</summary>
    private void MoveNodesIntoContainer(List<StepNode> nodes)
    {
        if (nodes.Count == 0) return;
        int moved = 0, blocked = 0;
        foreach (var node in nodes)
        {
            var list = FindParentList(node, _steps, out var idx);
            if (list is null) { blocked++; continue; }
            for (int i = idx - 1; i >= 0; i--)
            {
                if (list[i].Kind is StepKinds.Loop or StepKinds.Trigger && !IsDescendantOrSelf(list[i], node))
                {
                    list.RemoveAt(idx);
                    list[i].Children.Add(node);
                    moved++;
                    break;
                }
            }
        }
        RefreshTree();
        TxtStatus.Text = moved > 0
            ? $"已把 {moved} 个步骤移入上方容器{(blocked > 0 ? $"（{blocked} 个无上方容器/不可移）" : "")}（Tab=移入，多选生效）"
            : "上方没有循环/触发器容器";
    }

    /// <summary>candidate 是否为 node 自身或其子孙（防把容器移进自己的子树）。</summary>
    private static bool IsDescendantOrSelf(StepNode candidate, StepNode node)
    {
        if (ReferenceEquals(candidate, node)) return true;
        foreach (var c in node.Children)
            if (IsDescendantOrSelf(candidate, c)) return true;
        return false;
    }

    private void MenuToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVm is null) return;
        _selectedVm.Node.IsEnabled = !_selectedVm.Node.IsEnabled;
        RefreshTree();
        TxtStatus.Text = _selectedVm.Node.IsEnabled
            ? $"已启用 {_selectedVm.Node.DisplayText}"
            : $"已禁用（⏸）{_selectedVm.Node.DisplayText} — 回放时跳过";
    }
}

/// <summary>简易文本输入弹窗（任务命名等；代码构造，无需 XAML）。</summary>
public static class PromptDialog
{
    public static string? Show(string title, string label, string defaultValue = "")
    {
        var input = new TextBox { Text = defaultValue, Padding = new Thickness(4) };
        string? result = null;
        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(12), Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children =
                    {
                        new System.Windows.Controls.Button { Content = "确定", Width = 70, Padding = new Thickness(0, 4, 0, 4), IsDefault = true },
                        new System.Windows.Controls.Button { Content = "取消", Width = 70, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true },
                    },
                },
            }},
        };
        // 简易事件接线（代码构造按钮无 Click 处理器）：Enter=确定 Esc=取消 + 按钮行为
        var okBtn = ((StackPanel)win.Content).Children.OfType<StackPanel>().First()
            .Children.OfType<System.Windows.Controls.Button>().First(b => b.Content is "确定");
        var cancelBtn = ((StackPanel)win.Content).Children.OfType<StackPanel>().First()
            .Children.OfType<System.Windows.Controls.Button>().First(b => b.Content is "取消");
        okBtn.Click += (_, _) => { result = input.Text; win.Close(); };
        cancelBtn.Click += (_, _) => win.Close();
        win.ShowDialog();
        return result;
    }
}
