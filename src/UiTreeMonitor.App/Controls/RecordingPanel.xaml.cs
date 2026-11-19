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
    private StepVm? _contextVm;         // 右键命中的行（"复制结果"用它，未命中则回落到 _selectedVm）
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

    // ★ 鼠标"按下"瞬间的元素快照（弹出菜单/页内弹层在"抬起"时就被目标关闭，
    //   等 Up 回调再解析必然拿到 null 或新页面元素 —— Chromium 弹出菜单录制丢步的根因）
    private AutomationElement? _primedElem;
    private string _primedName = "", _primedCtrlType = "";
    private int _primedPid;
    private string? _primedSpec;
    // ★ 按下时刻探测到的点击语义（与 spec 同理：Chromium 菜单项抬手即关，
    //   Up 时 GetSupportedPatterns() 返回空数组且不抛异常 → 只能退化成坐标）。
    private string? _primedPattern, _primedMethod;
    private int _primedX, _primedY;
    private DateTime _primedTick = DateTime.MinValue;

    // ★ "输入锚"：最近一次用户输入（点击/键入/拖动）命中的元素 RuntimeId。
    //   UIA 属性变化事件是**结果**而不是动作：一次操作会连带触发大量后代元素的连锁事件
    //   （实测展开一行 → 139 条 ExpandCollapse Changed，元素名甚至是 "AccessKey: Property does not exist"
    //    这种属性行文本）。只有与最近输入命中同一元素的事件才是用户真正做的动作。
    private string _lastInputRuntimeId = "";
    private DateTime _lastInputTick = DateTime.MinValue;

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
        // ★ 覆盖前必须先释放旧监听：多个 EventListenerCore 同时活着会并行回调，
        //   同一个 UIA 事件被录成多条 —— 这是"点一下按钮自动录出上百个动作"的直接原因
        //   （每次 TryAttachAutoListener 重挂/切换目标都会重新 new，旧对象若不 Stop 会一直监听）。
        if (_autoListener is not null)
        {
            try { _autoListener.Stop(); } catch { /* 已丢弃 */ }
            try { _autoListener.Dispose(); } catch { /* 已丢弃 */ }
            _autoListener = null;
            AppLog.Write("RECORD", "StartUiaListener: 释放旧 UIA 监听（避免多监听并行导致事件重复录制）");
        }

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

    /// <summary>鼠标"按下"瞬间抓一份元素快照（不录步骤）。窗口标题栏/弹层按钮/弹出菜单项此刻都还在，
    /// 是这一整次点击里唯一可靠的取样时刻——抬起后 Chromium 常常已经把菜单关掉或已经跳转页面。</summary>
    private void PrimeClickElement(InputActionRecord input)
    {
        try
        {
            var elem = AutomationElement.FromPoint(new Point(input.X, input.Y));
            if (elem is null) return;

            _primedPid = elem.Current.ProcessId;
            if (_primedPid == Environment.ProcessId) { _primedElem = null; return; } // 自己 UI 不预热
            _primedName = elem.Current.Name ?? "";
            _primedCtrlType = elem.Current.ControlType?.ProgrammaticName ?? "";
            _primedSpec = BuildSpec(elem);
            // ★ 点击语义也要在"按下"时刻探测完：同一个 Chromium 菜单项，Up 后
            //   GetSupportedPatterns() 返回空数组且不抛异常（实测连续三次录制，
            //   只有第一次 supported=[InvokePattern] 成功，其余都是 []→坐标）。
            (_primedPattern, _primedMethod) = ProbeClickAction(elem);
            _primedElem = elem;
            _primedX = input.X;
            _primedY = input.Y;
            _primedTick = DateTime.Now;
        }
        catch { _primedElem = null; /* 预热失败不影响正式录制（Up 回调还会再解析一次） */ }
    }

    /// <summary>
    /// 输入动作回调（钩子线程 → UI 线程）：UIA 定位动作目标元素（鼠标=坐标命中、键盘=当前焦点），
    /// 过滤本软件自己的 UI。UIA 语义模式下先尝试 Pattern 语义化（点击→Invoke、文本键→SetValue、
    /// 拖动→RangeValue 终值），不支持 Pattern 的控件回退 Input_Mouse/Input_Keyboard 坐标记录。
    /// </summary>
    private void OnInputAction(InputActionRecord input)
    {
        // ★ 按下预热：只抓元素快照，绝不产生录制步骤。
        //   用 Send 优先级立即执行——从"按下"到"目标处理并关掉菜单"通常只有几十毫秒，
        //   排到普通优先级队列里往往就晚了（那时菜单已经关了，抓到的是新页面元素）。
        if (input.Kind == InputActionKind.MouseLeftDown)
        {
            if (!_isRecording) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => PrimeClickElement(input)));
            return;
        }

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
            string? specAtClick = null;   // ★ 元素新鲜时刻的定位 spec（见下方说明）
            string? primedPattern = null, primedMethod = null;  // ★ 按下时刻探测到的点击语义
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

                    // ★ 定位 spec 必须在这里抓（点击后**立刻**，元素还新鲜）。
                    //   Chromium 弹出菜单：点完菜单项菜单就关，几百毫秒后再去读该元素的
                    //   Name/ControlType/ClassName/BoundingRectangle 全是空，只剩 RuntimeId 侥幸可读
                    //   （实测 spec 变成 autoId=|name=|type=|class=|rect=|rid=42.…），回放必然"元素未找到"。
                    specAtClick = BuildSpec(elem);
                }
            }
            catch { /* 元素可能已销毁/进程已退出；已读到的部分继续用 */ }

            // ★ 抬起时刻解析的元素经常已经不对了：Chromium 弹出菜单/页内弹层在"抬起"时就关闭，
            //   此刻 FromPoint 要么 null（走坐标兜底），要么落到跳转后的新页面元素（会录成错误语义）。
            //   按下那一刻的屏幕内容一定还是原样，所以 Down 回调里抓的快照最可信，优先用它。
            var primedFresh = input.Kind == InputActionKind.MouseLeftClick
                              && _primedElem is not null
                              && (DateTime.Now - _primedTick).TotalSeconds <= 3.0
                              && Math.Abs(input.X - _primedX) <= 4
                              && Math.Abs(input.Y - _primedY) <= 4;
            if (primedFresh)
            {
                elem = _primedElem;
                pid = _primedPid;
                elemName = _primedName;
                ctrlType = _primedCtrlType;
                specAtClick = _primedSpec;
                primedPattern = _primedPattern;
                primedMethod = _primedMethod;
                try { procName = Process.GetProcessById(pid).ProcessName; } catch { }
                AppLog.Write("RECORD", $"Input {input.Kind}: 采用按下时刻的元素快照 elem='{TruncLog(elemName, 40)}'" +
                    $" type={ctrlType} pid={pid}（抬起后元素已失效/已切换页面时以此为准）");
                _primedElem = null;   // 用后即清，避免连续点击复用旧快照
                _primedPattern = null;
                _primedMethod = null;
            }

            // ★ 鼠标动作命中元素缓存（KeyDown 焦点解析失败的回退源：键入前刚点过输入框）
            if (input.Kind != InputActionKind.KeyDown && elem is not null && pid != 0)
            {
                _recentClickElem = elem;
                _recentClickTick = DateTime.Now;
            }

            // ★ 更新输入锚：UIA 事件只认"与本次输入同一元素"的那些（过滤连锁噪声）
            if (elem is not null && pid != 0)
            {
                try
                {
                    var ids = elem.GetRuntimeId();
                    _lastInputRuntimeId = ids is { Length: > 0 } ? string.Join(".", ids) : "";
                }
                catch { _lastInputRuntimeId = ""; }
                _lastInputTick = DateTime.Now;
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
            //
            // ★ 但 Chromium（Edge/Chrome）会误杀：WindowFromPoint 拿到的是**子窗口**
            //   （Chrome_RenderWidgetHostHWND 之类），而 UIA 元素向上只能爬到**顶层**窗口句柄，
            //   两者本来就不相等 → 每次点击都被判成"窗口已关闭"，整场录制全变 coord。
            //   所以先各自 GetAncestor(GA_ROOT) 归一到顶层再比；元素拿不到 hwnd 时退化为"同进程"判断。
            if (elem is not null && input.Hwnd != IntPtr.Zero && pid != 0)
            {
                var topHwnd = TopLevelHwndOf(elem);
                var sameWindow = topHwnd != IntPtr.Zero
                                 && GetAncestor(input.Hwnd, GA_ROOT) == GetAncestor(topHwnd, GA_ROOT);
                if (!sameWindow && topHwnd == IntPtr.Zero)
                {
                    // 元素没有任何原生窗口句柄（Chromium 自绘层常见）：只能按进程判断
                    GetWindowThreadProcessId(input.Hwnd, out var clickPid);
                    sameWindow = clickPid != 0 && (int)clickPid == pid;
                }
                if (!sameWindow)
                {
                    AppLog.Write("RECORD", $"Input {input.Kind}: FromPoint top-level {GetAncestor(topHwnd, GA_ROOT)} = {topHwnd}" +
                        $" ≠ click-time window {input.Hwnd} root {GetAncestor(input.Hwnd, GA_ROOT)}" +
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
                HandleUiaSemanticInput(input, elem, pid, elemName, procName, ctrlType, specAtClick,
                    primedPattern, primedMethod);
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
        int pid, string elemName, string procName, string ctrlType, string? specAtClick,
        string? primedPattern, string? primedMethod)
    {
        switch (input.Kind)
        {
            case InputActionKind.MouseLeftClick:
            {
                // ★ clickProbeFailed：语义探测**本身**失败（元素已失效/属性读不出来），
                //   区别于"元素活着但确实没有点击语义"（点空白/纯文本展示）。只有前者才兜底坐标——
                //   否则每次点页面空白都会留一条回放垃圾步骤。
                bool clickProbeFailed = false;
                if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, specAtClick,
                        primedPattern, primedMethod, out clickProbeFailed, out var semantic, out var replacePrev))
                {
                    // ★ 语义探测成功但**元素定位属性全读空**：这样的语义步骤回放时无从定位元素，
                    //   必然报"元素未找到: spec:v1|"。改录坐标延迟定位——回放按坐标探测实际语义动作，
                    //   命中容器时自动下钻到真正被点的菜单项。
                    if (string.IsNullOrEmpty(semantic.ElementPath))
                    {
                        AppendRecord(MakeCoordRecord(input, "元素定位属性读取失败"), replacePrevious: false,
                            source: "input-semantic coord-fallback");
                        AppLog.Write("RECORD", $"OnInputAction→coord-fallback @({input.X},{input.Y}): " +
                            $"语义已探测但定位属性全空，改录坐标（原语义 {semantic.PatternName}.{semantic.MethodName} elem={elemName} type={ctrlType} pid={pid}）");
                        return;
                    }
                    AppendRecord(semantic, replacePrev, source: $"input-semantic {input.Kind}");
                    AppLog.Write("RECORD", $"OnInputAction→semantic: {semantic.PatternName}.{semantic.MethodName}" +
                        $"({string.Join(", ", semantic.Parameters ?? Array.Empty<object?>())}) elem={elemName} type={ctrlType} pid={pid} proc={procName}");
                    return;
                }
                if (elem is null || clickProbeFailed)
                {
                    // ★ 点击即关窗竞态（Qt accept()/tk destroy）、或 Chromium 弹出菜单"点完即关"导致
                    //   元素在回调时已经失效：录制时读不到语义，但**点击真实发生了**，该坐标回放时会
                    //   命中重新打开的菜单项/弹窗按钮 → coord:v1 延迟定位，回放按坐标探测实际语义。
                    //   宁可留一条坐标步骤，也不能静默丢步（丢步用户根本不知道漏了什么）。
                    var coord = MakeCoordRecord(input, elem is null ? "录制时元素已消失" : "点击瞬间元素已失效");
                    AppendRecord(coord, replacePrevious: false, source: "input-semantic coord-deferred");
                    AppLog.Write("RECORD", $"OnInputAction→coord-deferred @({input.X},{input.Y}) " +
                        $"[{ (elem is null ? "录制时元素已消失" : "语义探测失败/元素已失效") }] 回放按坐标探测实际语义动作");
                    return;
                }
                // 元素活着但探测不到点击语义的情况已在 TryBuildClickSemantic 里兜底成坐标步骤
                //（唯一例外是纯文本叶子），能走到这里说明是 Text/StaticText 或窗口级点击，不录
                return;
            }

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
                    if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, specAtClick,
                            primedPattern, primedMethod, out _, out var keyRecord, out var keyMerge))
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
                if (elem is not null && TryBuildSemanticRecord(input, elem, elemName, procName, ctrlType, specAtClick,
                        primedPattern, primedMethod, out _, out var dragRecord, out var dragMerge))
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
        // ★ 用 BeginInvoke 而不是 Invoke：UIA 回调在管道线程，Invoke 会让它同步等 UI 线程，
        //   UI 一忙（刷新列表/抓树）事件就排队，卡顿后成批涌出来，看起来就是"点一下刷出很多动作"。
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording) return;

            var record = uiaEvent.ToActionRecord();

            // ★ 输入锚过滤（最关键的一道）：属性变化事件是"结果"，一次用户操作会连带触发大量
            //   后代元素的连锁事件——实测展开一行刷出 139 条 ExpandCollapse Changed，元素名甚至是
            //   "AccessKey: Property does not exist" 这种属性行文本，全录下来就是"点一下录上百个动作"。
            //   只有"与最近一次用户输入命中同一元素"的事件才可能是真正要录的动作。
            var sinceInput = (DateTime.Now - _lastInputTick).TotalMilliseconds;
            if (sinceInput > 1500)
            {
                AppLog.Write("RECORD", $"[anchor] 丢弃无输入锚事件 {uiaEvent.EventName} " +
                    $"elem='{TruncLog(uiaEvent.ElementName, 30)}'（距上次用户输入 {sinceInput:F0}ms > 1500ms）");
                return;
            }
            if (!string.IsNullOrEmpty(_lastInputRuntimeId) && !string.IsNullOrEmpty(uiaEvent.RuntimeId)
                && uiaEvent.RuntimeId != _lastInputRuntimeId)
            {
                AppLog.Write("RECORD", $"[anchor] 丢弃非本元素事件 {uiaEvent.EventName} " +
                    $"elem='{TruncLog(uiaEvent.ElementName, 30)}' rid={uiaEvent.RuntimeId} ≠ 输入锚 {_lastInputRuntimeId}");
                return;
            }
            // ★ 事件洪水节流：同元素（spec 忽略 rect 抖动）同 Pattern 同方法在 400ms 内重复触发只留一条。
            //   部分控件一次点击会连带触发多条属性事件，这些不是用户操作，回放也是无意义的重复动作。
            //   值类（ReplacePrevious=true，逐字符输入/拖动）不走节流，靠 merge 保留最终值。
            if (!uiaEvent.ReplacePrevious
                && _lastAppendedNode is { Kind: StepKinds.Action } tail
                && tail.Action is not null
                && tail.Action.PatternName == record.PatternName
                && tail.Action.MethodName == record.MethodName
                && MergeKey(tail.Action) == MergeKey(record)
                && (record.Timestamp - tail.Action.Timestamp).TotalMilliseconds < 400)
            {
                AppLog.Write("RECORD", $"[throttle] 丢弃重复事件 {record.PatternName}.{record.MethodName} elem={record.ElementName}");
                return;
            }

            var detail = record.Parameters is { Length: > 0 }
                ? $"{record.MethodName}({string.Join(", ", record.Parameters)})"
                : record.MethodName;

            // ★ 双通道合流：与输入语义化记录共用 merge（同元素同 Pattern 同方法只留一条）
            AppendRecord(record, uiaEvent.ReplacePrevious, source: $"uia {uiaEvent.EventName}",
                statusText: $"● 已捕获 {_steps.Count} 个动作: {detail} ({TextFit.OneLine(uiaEvent.ElementName, 60)})");
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
                         (record.ElementName != "" ? $" ({TextFit.OneLine(record.ElementName, 60)})" : "") +
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
        string elemName, string procName, string ctrlType, string? specOverride,
        string? primedPattern, string? primedMethod,
        out bool clickProbeFailed, out ActionRecord record, out bool replacePrevious)
    {
        record = null!;
        replacePrevious = false;
        clickProbeFailed = false;
        if (elem is null) return false;
        try
        {
            switch (input.Kind)
            {
                case InputActionKind.MouseLeftClick:
                {
                    // ★ 窗口级元素点击（标题栏/空白客户区 FromPoint 命中 Window）：Qt 窗口误报 InvokePattern，
                    //   记录只会成为回放垃圾步骤（日志实锤：点主窗口空白 → Invoke on Window 被回放）→ 不录
                    if (ctrlType.EndsWith(".Window", StringComparison.Ordinal))
                    {
                        var wkey = $"{procName}|{elemName}|click-window";
                        if (_patternProbeLogged.TryAdd(wkey, 1))
                            AppLog.Write("RECORD", $"[semantic-probe] click-on-window: elem='{TruncLog(elemName, 40)}' 窗口级点击无控件语义（激活窗口）→ 不记录");
                        break;
                    }
                    // ★ 按点击语义优先级探测真实 Pattern（不能默认 Invoke）：
                    //   Chromium 的菜单项/自绘按钮常常只有 ExpandCollapse、没有 Invoke，
                    //   以前一律只认 Invoke → 这些点击要么被丢弃、要么回放必然失败。
                    if (TryBuildClickSemantic(elem, elemName, procName, ctrlType, input.Timestamp, specOverride,
                            primedPattern, primedMethod, out clickProbeFailed, out record))
                    {
                        replacePrevious = false;
                        return true;
                    }

                    // ★ 语义化失败也不能静默丢步：Chromium 弹出菜单项"抬起即关"，此时
                    //   GetSupportedPatterns() 返回**空数组且不抛异常**（日志实锤：MenuItem 'New issue'
                    //   supported=[]）。以前这里直接 break → 用户明明点了菜单项，第 2 步却没有。
                    //   现在一律留一条坐标步骤，回放时按坐标探测该位置实际支持的语义动作。
                    //   唯一例外：命中纯文本叶子（Text/StaticText），点它没有可执行语义，录下来只是噪声。
                    var clickShortType = ShortType(ctrlType);
                    if (clickShortType is "Text" or "StaticText")
                    {
                        LogPatternProbe(elem, elemName, ctrlType, procName, "click-text-no-op");
                        break;
                    }
                    LogPatternProbe(elem, elemName, ctrlType, procName, "click-no-semantic");
                    record = MakeCoordRecord(input, $"点击未探测到可执行语义（{clickShortType}）");
                    replacePrevious = false;
                    return true;
                }

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
                                result: $"{procName} {ShortType(ctrlType)} → \"{value}\"", elemName, input.Timestamp, specOverride);
                            return true;
                        }
                    }
                    if (elem.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rpo) && rpo is RangeValuePattern rp && !rp.Current.IsReadOnly)
                    {
                        var value = rp.Current.Value;
                        replacePrevious = true;
                        record = BuildSemantic(elem, "RangeValuePattern", "SetValue", new object?[] { value },
                            result: $"{procName} {ShortType(ctrlType)} → {value}", elemName, input.Timestamp, specOverride);
                        return true;
                    }
                    // 回车且焦点无值类 Pattern（按钮焦点）→ Invoke（表单提交语义）
                    if (elem.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                    {
                        replacePrevious = false;
                        record = BuildSemantic(elem, "InvokePattern", "Invoke", null,
                            result: $"{procName} {ShortType(ctrlType)} Invoke(Enter)", elemName, input.Timestamp, specOverride);
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
                            result: $"{procName} {ShortType(ctrlType)} → {value}", elemName, input.Timestamp, specOverride);
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

    /// <param name="specOverride">**点击发生瞬间**抓到的定位 spec（见 OnInputAction）。Chromium 弹出菜单项
    /// 在点击后几百毫秒就失效，此时才去读 Name/ControlType/Rect 全是空（实测只剩 RuntimeId 侥幸可读），
    /// 所以必须在元素还新鲜的那一刻先抓一份，语义化时直接用。传 null 表示没抓到，按当下现读。</param>
    private static ActionRecord BuildSemantic(AutomationElement elem, string pattern, string method,
        object?[]? parameters, string result, string elemName, DateTime timestamp,
        string? specOverride = null) => new()
    {
        Timestamp = timestamp,
        ElementName = elemName,
        ElementPath = specOverride ?? BuildSpec(elem),   // spec 定位（回放经 ResolveBySpec 重定位，不依赖坐标）
        PatternName = pattern,
        MethodName = method,
        Parameters = parameters,
        Success = true,
        Result = result,
    };

    /// <summary>
    /// 点击语义探测：**直接读元素实际支持的 Pattern 全集**（`GetSupportedPatterns()`），
    /// 再按 UiaPatternCatalog.ClickActions 的优先级挑一个"点一下这个元素"最贴切的动作用，
    /// 并把真实 Pattern 名 + 该元素支持的全部 Pattern 写进脚本。
    /// 之所以不能固定枚举几类：浏览器/Chromium 的自绘控件、Win32 老控件、Qt 自绘控件的
    /// 能力组合千差万别（只有 ExpandCollapse、只有 LegacyIAccessible、只有 SelectionItem…），
    /// 固定探测链必然漏；能力全集同时进 Result，回放失败时一眼能看出该元素到底能干什么。
    /// </summary>
    private bool TryBuildClickSemantic(AutomationElement elem, string elemName, string procName,
        string ctrlType, DateTime ts, string? specOverride,
        string? primedPattern, string? primedMethod,
        out bool probeFailed, out ActionRecord record)
    {
        record = null!;
        var failed = false;

        // ① 先试 FromPoint 命中的元素本身
        var hit = TryOn(elem, elemName, ctrlType, specOverride);
        if (hit is not null) { record = hit; probeFailed = failed; return true; }
        if (failed) { probeFailed = true; return false; }  // 元素已失效 → 调用方走坐标兜底

        // ② FromPoint 在 Qt / Chromium 上常常返回最上层的 **Text**（Qt 树项的文字、网页里的链接文字），
        //    真正可交互的是它的祖先（TreeItem / ListItem / Hyperlink）。沿父链向上找第一个"高置信
        //    可点击"的祖先再试一次——否则点树项永远录不出步骤
        //    （日志实锤：elem='树项目' type=ControlType.Text，而它父链上的 TreeItem 有 SelectionItem）。
        AutomationElement? ancestor = null;
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var cur = elem;
            for (var i = 0; i < 6 && cur is not null; i++)
            {
                cur = walker.GetParent(cur);
                if (cur is null) break;
                if (HasHighConfidenceClickPattern(cur)) { ancestor = cur; break; }
            }
        }
        catch { /* 父链读不到就放弃 */ }

        if (ancestor is not null)
        {
            var ancName = elemName;
            var ancType = ctrlType;
            try { var n = ancestor.Current.Name; if (!string.IsNullOrEmpty(n)) ancName = n; } catch { }
            try { var t = ancestor.Current.ControlType?.ProgrammaticName; if (!string.IsNullOrEmpty(t)) ancType = t; } catch { }

            // ★ 祖先必须现读 spec：叶子的 spec 会定位到 Text 上，回放点它等于没点
            hit = TryOn(ancestor, ancName, ancType, null);
            if (hit is not null)
            {
                record = hit;
                AppLog.Write("RECORD", $"[semantic-probe] click 上溯到可交互祖先: '{TruncLog(elemName, 30)}'({ShortType(ctrlType)})" +
                    $" → '{TruncLog(ancName, 30)}'({ShortType(ancType)}) {record.PatternName}.{record.MethodName}");
                probeFailed = failed;
                return true;
            }
        }

        // ③ 按下时刻缓存的语义：Up 时刻 Chromium 菜单项已销毁，GetSupportedPatterns() 返回空数组
        //   （实测连续三次录制 New issue，只有第一次 supported=[InvokePattern]，其余全 []→坐标）。
        //   Down 那一刻元素一定还在，那次探测就是唯一可靠的依据。
        if (primedPattern is not null)
        {
            var method = primedMethod ?? "Invoke";
            record = BuildSemantic(elem, primedPattern, method, null,
                $"{procName} {ShortType(ctrlType)} {method}　patterns=[按下时刻探测]", elemName, ts, specOverride);
            AppLog.Write("RECORD", $"[semantic-probe] 采用按下时刻缓存的语义: {primedPattern}.{method} " +
                $"elem='{TruncLog(elemName, 30)}'（抬起时元素已失效，现场探测不可用）");
            probeFailed = failed;
            return true;
        }

        LogPatternProbe(elem, elemName, ctrlType, procName, "click-no-semantic");
        probeFailed = failed;
        return false;

        /// <summary>在某一个元素上按 ClickActions 优先级挑点击语义（成功返回记录，否则 null）。
        /// 每个 Pattern 独立容错：Toggle 状态 / ExpandCollapse 状态读不到不影响其它能力。</summary>
        ActionRecord? TryOn(AutomationElement target, string targetName, string targetType, string? spec)
        {
            var shortType = ShortType(targetType);
            try
            {
                // ★ 元素支持的 Pattern 全集（WAF 内部同样是拿全表逐个试，这里一次拿到，不再自己维护探测链）
                var supported = target.GetSupportedPatterns();
                var ids = new HashSet<int>();
                var names = new List<string>();
                foreach (var pat in supported)
                {
                    try { ids.Add(pat.Id); names.Add(pat.ProgrammaticName); } catch { /* 单个 pattern 读不到 */ }
                }
                var capability = names.Count > 0 ? string.Join(",", names) : "(none)";

                foreach (var action in UiaPatternCatalog.ClickActions)
                {
                    if (!ids.Contains(action.PatternId)) continue;

                    var method = action.MethodName;
                    var extra = "";
                    if (action.Kind == UiaPatternCatalog.ClickKind.Toggle)
                    {
                        // Toggle 是翻转：把点击后的状态写进脚本，回放时能核对（UIA 没有 SetState，无法自动纠偏）
                        try
                        {
                            if (target.TryGetCurrentPattern(TogglePattern.Pattern, out var tObj) && tObj is TogglePattern tp)
                                extra = $"→{tp.Current.ToggleState}";
                        }
                        catch { /* 状态读不到 */ }
                    }
                    else if (action.Kind == UiaPatternCatalog.ClickKind.ExpandCollapse)
                    {
                        // ★ 录制回调在点击生效之后取元素，读到的展开状态已是"点击造成的结果"，
                        //   所以动作取"达到该状态的那个动作"，不能取反（取反会把"打开菜单"录成 Collapse）。
                        var collapsed = false;
                        try
                        {
                            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecObj) && ecObj is ExpandCollapsePattern ecp)
                                collapsed = ecp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed;
                        }
                        catch { /* 状态读不到按展开处理 */ }
                        method = collapsed ? "Collapse" : "Expand";
                    }

                    return BuildSemantic(target, action.PatternName, method, null,
                        $"{procName} {shortType} {method}{extra}　patterns=[{capability}]", targetName, ts, spec);
                }
            }
            catch { failed = true; /* 元素已失效/属性读不出来 → 调用方用坐标兜底，不静默丢步 */ }
            return null;
        }
    }

    /// <summary>高置信"点击语义"Pattern：Invoke(10000) / SelectionItem(10010) / ExpandCollapse(10005) / Toggle(10015)。
    /// ★ 刻意不含 LegacyIAccessible(10018)：Pane/Window/Custom 这类容器几乎都报它，沿父链上会误判到容器上。</summary>
    private static bool HasHighConfidenceClickPattern(AutomationElement el)
    {
        try
        {
            foreach (var p in el.GetSupportedPatterns())
            {
                try
                {
                    if (p.Id is 10000 or 10010 or 10005 or 10015) return true;
                }
                catch { /* 单个 pattern 读不到 */ }
            }
        }
        catch { /* 元素已销毁 */ }
        return false;
    }

    /// <summary>按下时刻探测一次"点击语义"（与 UP 现场探测共用同一张优先级表），
    /// 供 UP 后元素已销毁时降级使用——Chromium 弹出菜单项抬手即关，
    /// `GetSupportedPatterns()` 会返回空数组且不抛异常，此时只有按下时刻的结果可信。
    /// ★ ExpandCollapse 的方向取"点击前状态的反面"：点击会翻转展开状态，
    ///   而 UP 现场探测是"点击后读状态直接取"，两者等价，这里不再受"点击后元素消失"影响。</summary>
    private static (string? Pattern, string? Method) ProbeClickAction(AutomationElement el)
    {
        try
        {
            var ids = new HashSet<int>();
            foreach (var p in el.GetSupportedPatterns())
            {
                try { if (p.Id > 0) ids.Add(p.Id); } catch { /* 单个 pattern 读不到 */ }
            }
            if (ids.Count == 0) return (null, null);

            foreach (var action in UiaPatternCatalog.ClickActions)
            {
                if (!ids.Contains(action.PatternId)) continue;
                var method = action.MethodName;
                if (action.Kind == UiaPatternCatalog.ClickKind.ExpandCollapse)
                {
                    // WPF 的 ExpandCollapseState 没有 Indeterminate 成员，用布尔折叠三种状态
                    var expanded = false;
                    try
                    {
                        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var o) && o is ExpandCollapsePattern ecp)
                            expanded = ecp.Current.ExpandCollapseState == ExpandCollapseState.Expanded;
                    }
                    catch { /* 状态读不到按展开处理 */ }
                    // 点击前 Expanded → 点击后收起 → Collapse；其余（Collapsed/PartiallyExpanded/Leaf）→ Expand
                    method = expanded ? "Collapse" : "Expand";
                }
                return (action.PatternName, method);
            }
        }
        catch { /* 元素已失效：交给坐标兜底 */ }
        return (null, null);
    }

    /// <summary>文本类虚拟键（会产生字符输入/删除的键）：主键区 0-9、A-Z、小键盘数字与运算符、空格、Back、OEM 标点。
    /// 回车在 KeyDown 分支单独处理（Value 优先、按钮焦点时 Invoke）；Tab/方向/F 键等无语义不录。
    /// ★ 小键盘 VK_NUMPAD0-9(0x60-0x69) 必须算文本键——NumLock 开时产生数字字符，
    ///   数字输入（QDoubleSpinBox 等）高频走小键盘，漏掉即"键入完全丢失"（日志实锤：captured 计数含键、零 KeyQueue）。</summary>
    private static bool IsTextVk(uint vk) =>
        vk is >= 0x30 and <= 0x5A    // 0-9、A-Z（主键区）
        or >= 0x60 and <= 0x6F       // 小键盘：NUMPAD0-9(0x60-69)、* + - . / 等运算符(0x6A-6F)
        or 0x20 or 0x08              // Space、Back（删除也改变值，读实时值即最终值）
        or >= 0xBA and <= 0xC0       // OEM 标点 ;=,-./`
        or >= 0xDB and <= 0xDE;      // OEM 标点 [\]'

    private static string ShortType(string programmaticName) =>
        programmaticName.StartsWith("ControlType.", StringComparison.Ordinal)
            ? programmaticName["ControlType.".Length..]
            : programmaticName;

    /// <summary>坐标延迟定位记录（元素已消失 / 元素定位属性读不出时的兜底）：
    /// 回放时按坐标探测该位置元素**实际支持的**语义动作（Invoke → LegacyIAccessible → Toggle → Select
    /// → ExpandCollapse），命中容器（菜单 List / 弹层 Pane）会自动下钻到真正被点的子元素，光标不动。</summary>
    private static ActionRecord MakeCoordRecord(InputActionRecord input, string why) => new()
    {
        Timestamp = input.Timestamp,
        ElementName = why.Contains("消失", StringComparison.Ordinal) ? "(窗口已关闭·坐标延迟定位)" : "(坐标延迟定位)",
        ElementPath = $"coord:v1|{input.X},{input.Y}",
        // ★ 不要写死 InvokePattern：此刻不知道回放时该位置是什么元素、支持什么
        PatternName = "(coord:auto)",
        MethodName = "Click",
        Parameters = null,
        Success = true,
        Result = $"按坐标探测语义动作 @({input.X},{input.Y})（{why}）",
    };

    /// <summary>与 EventListenerCore.BuildSpec（COM 版）同格式的 WPF 元素 spec（回放 ResolveBySpec 通用）。
    /// ★ 必须**逐字段读** `AutomationElementInformation`：Chromium 弹出菜单项只要有一个属性读不到，
    ///   整包 `elem.Current` 就抛异常（原来 catch 后只剩 "spec:v1|" 空壳；实测只剩 RuntimeId 可读，
    ///   回放报「元素未找到: spec:v1|autoId=|name=|type=|class=|rect=|rid=42.…」）。</summary>
    private static string BuildSpec(AutomationElement elem)
    {
        string autoId = "", name = "", type = "", cls = "", rectStr = "", rid = "";

        try { autoId = elem.Current.AutomationId ?? ""; } catch { }
        try { name = elem.Current.Name ?? ""; } catch { }
        try { type = ShortType(elem.Current.ControlType?.ProgrammaticName ?? ""); } catch { }
        try { cls = elem.Current.ClassName ?? ""; } catch { }
        try
        {
            var r = elem.Current.BoundingRectangle;
            if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                rectStr = $"{(int)r.Left},{(int)r.Top},{(int)r.Right},{(int)r.Bottom}";
        }
        catch { }
        try
        {
            var ids = elem.GetRuntimeId();
            if (ids is { Length: > 0 }) rid = string.Join(".", ids);
        }
        catch { }

        // 定位信息一条都读不到 → 返回空串，调用方改录坐标（绝不录成 "spec:v1|" 这种回放必死的空步）
        if (autoId.Length == 0 && name.Length == 0 && type.Length == 0
            && cls.Length == 0 && rectStr.Length == 0 && rid.Length == 0) return "";

        return $"spec:v1|autoId={Esc(autoId) ?? ""}|name={Esc(name) ?? ""}|type={type}" +
               $"|class={Esc(cls) ?? ""}|rect={rectStr}" +
               (rid.Length > 0 ? $"|rid={rid}" : "");
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
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private const uint GA_ROOT = 2;

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
            // 立即给反馈：按钮先显示"停止中"并临时禁用，避免连点又启动一轮
            BtnPlayAll.Content = "■ 停止中…";
            BtnPlayAll.IsEnabled = false;
            TxtStatus.Text = "■ 正在停止回放…（当前动作执行完即停，后续步骤不再继续）";
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
        ClearRunMarks(); // 新一轮回放：先清掉上一轮的颜色标注
        BtnPlayAll.Content = "■ 停止回放";
        BtnPlayAll.IsEnabled = true;
        BtnStep.IsEnabled = false;
        TxtStatus.Text = "▶ 回放中…";

        _playRunner = new RecordingTaskRunner(task, ResolveElement ?? (_ => null));
        _playRunner.StateChanged += snap => Dispatcher.BeginInvoke(() =>
        {
            // ★ 颜色标注：当前步骤黄、已执行完绿、失败红（按 NodeId 找行，循环回绕也不会错）
            if (snap.State is RecTaskRunState.Running or RecTaskRunState.WaitingTrigger or RecTaskRunState.WaitingTimer)
            {
                if (snap.CurrentNodeId is Guid runId)
                {
                    SettleRunningMarks(exceptNodeId: runId); // 上一步到此已完成
                    if (_vmById.TryGetValue(runId, out var runVm))
                    {
                        MarkStep(runVm.Node, StepMark.Running);
                        runVm.RefreshDetail(); // 上一步写回的结果/错误立即刷新到该行的详情列
                    }
                }
            }

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
                switch (snap.State)
                {
                    case RecTaskRunState.Completed:
                        foreach (var vm in _vmById.Values)
                            if (vm.Node.IsEnabled) MarkStep(vm.Node, StepMark.Done);
                        break;
                    case RecTaskRunState.Failed:
                        SettleRunningMarks(exceptNodeId: snap.CurrentNodeId);
                        if (snap.CurrentNodeId is Guid failId && _vmById.TryGetValue(failId, out var failVm))
                            MarkStep(failVm.Node, StepMark.Failed);
                        break;
                    default: // Cancelled：被打断的那步不标完成，也不标失败（保持原色）
                        SettleRunningMarks(exceptNodeId: snap.CurrentNodeId);
                        if (snap.CurrentNodeId is Guid stopId && _vmById.TryGetValue(stopId, out var stopVm) && stopVm.IsRunning)
                            MarkStep(stopVm.Node, StepMark.None);
                        break;
                }

                TxtStatus.Text = snap.State switch
                {
                    RecTaskRunState.Completed => $"✓ 回放完成（{snap.TotalSteps} 步）",
                    RecTaskRunState.Cancelled => "■ 回放已停止",
                    _ => $"✗ 回放失败：{snap.LastError}",
                };
                _playRunner?.Dispose();
                _playRunner = null;
                RefreshTree();  // 回写结果显示 + 重放颜色标记
                RefreshUI();    // 按钮文案/可用状态复位
            }
        });
        _playRunner.Start();
    }

    private async void BtnStep_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0 || ResolveElement is null) return;
        if (_playRunner is not null) { TxtStatus.Text = "回放进行中，先停止再单步"; return; }

        _stepIndex++;
        if (_stepIndex >= _steps.Count)
        {
            _stepIndex = -1;
            TxtStatus.Text = "单步回放已到达末尾，重新开始";
            return;
        }

        var node = _steps[_stepIndex];
        if (node.Kind != StepKinds.Action)
        {
            TxtStatus.Text = $"→ [{_stepIndex + 1}] {node.DisplayText}：控制节点请在'全部回放'中执行";
            return;
        }
        var record = node.Action!;

        // ★ 每次单步都先清空全部颜色（与"全部回放"同规则），只把当前这一步标成"执行中"
        ClearRunMarks();
        TxtStatus.Text = $"→ 单步 [{_stepIndex + 1}/{_steps.Count}] {record.MethodName}()...";
        MarkStep(node, StepMark.Running);
        // ★ 关键：单步是在 UI 线程同步执行，不先让出一帧的话黄色根本来不及画出来
        //   （执行完立刻变成绿/红，看起来就是"当前步骤不是黄色"）
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        RevealNode(node);

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
            MarkStep(node, ok ? StepMark.Done : StepMark.Failed);
            RefreshTree();
            TxtStatus.Text = ok
                ? $"→ [{_stepIndex + 1}] {record.MethodName}() ✓"
                : $"→ [{_stepIndex + 1}] {record.MethodName}() ✗ {result}";
        }
        catch (Exception ex)
        {
            MarkStep(node, StepMark.Failed);
            RefreshTree();
            TxtStatus.Text = $"→ [{_stepIndex + 1}] 错误: {ex.Message}";
        }
    }

    /// <summary>把某步骤行滚到可视区（不选中——选中态的容器高亮会盖掉进度颜色）。</summary>
    private void RevealNode(StepNode node)
    {
        try
        {
            if (!_vmById.TryGetValue(node.Id, out var vm)) return;
            TvSteps.UpdateLayout();
            var item = FindItemContainer(TvSteps, vm);
            item?.BringIntoView();
        }
        catch { /* 容器未生成（虚拟化）时忽略 */ }
    }

    private static TreeViewItem? FindItemContainer(ItemsControl parent, object dataContext)
    {
        foreach (var o in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(o) is not TreeViewItem item) continue;
            if (ReferenceEquals(item.DataContext, dataContext)) return item;
            var nested = FindItemContainer(item, dataContext);
            if (nested is not null) return nested;
        }
        return null;
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

    /// <summary>步骤行的执行状态（颜色标注用）。</summary>
    public enum StepMark { None, Running, Done, Failed }

    /// <summary>步骤树行 ViewModel（绑定 DisplayText/Detail/DisabledMark/Children；IsMultiSelected=Shift 范围多选高亮；
    /// IsRunning/IsDone/IsFailed=回放进度颜色标注）。</summary>
    public sealed class StepVm : System.ComponentModel.INotifyPropertyChanged
    {
        public required StepNode Node { get; init; }
        public List<StepVm> Children { get; init; } = new();
        public string DisplayText => Node.DisplayText;
        public string Detail => Node.DetailText;
        public string DisabledMark => Node.IsEnabled ? "Collapsed" : "Visible";
        public StepVm? Parent { get; init; }

        /// <summary>步骤序号：根级 1、2、3…；子级 1.1、1.1.1…（层级点分）。</summary>
        public string IndexText { get; init; } = "";

        private bool _isMultiSelected;
        /// <summary>Shift 范围多选标记（背景淡蓝；Tab 批量移入容器作用于多选集）。</summary>
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set => SetField(ref _isMultiSelected, value, nameof(IsMultiSelected));
        }

        private bool _isRunning;
        /// <summary>正在执行（黄底加粗）。</summary>
        public bool IsRunning
        {
            get => _isRunning;
            private set => SetField(ref _isRunning, value, nameof(IsRunning));
        }

        private bool _isDone;
        /// <summary>已执行完成（浅绿）。</summary>
        public bool IsDone
        {
            get => _isDone;
            private set => SetField(ref _isDone, value, nameof(IsDone));
        }

        private bool _isFailed;
        /// <summary>执行失败（浅红）。</summary>
        public bool IsFailed
        {
            get => _isFailed;
            private set => SetField(ref _isFailed, value, nameof(IsFailed));
        }

        /// <summary>设置该行的执行标记（互斥：同一时刻只有一种颜色）。</summary>
        public void SetMark(StepMark mark)
        {
            IsRunning = mark == StepMark.Running;
            IsDone = mark == StepMark.Done;
            IsFailed = mark == StepMark.Failed;
        }

        /// <summary>回放/单步把结果写回 node.Action 后，通知详情列重新读（DetailText 是计算属性）。</summary>
        public void RefreshDetail() =>
            PropertyChanged?.Invoke(this, new(nameof(Detail)));

        private void SetField(ref bool field, bool value, string name)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(name));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>执行状态标记（NodeId → 标记）：RefreshTree 会重建 VM，靠它把颜色还原回去。</summary>
    private readonly Dictionary<Guid, StepMark> _runMarks = new();

    /// <summary>当前显示标记的步骤行索引（NodeId → VM），供回放快照快速找行。</summary>
    private readonly Dictionary<Guid, StepVm> _vmById = new();

    private void RefreshTree()
    {
        _vmById.Clear();
        TvSteps.ItemsSource = BuildVmListWithMarks(_steps, "");
    }

    /// <summary>构建步骤行 VM：层级序号（1 / 1.1 / 1.1.1）+ 还原执行标记。</summary>
    private List<StepVm> BuildVmListWithMarks(List<StepNode> nodes, string prefix)
    {
        var list = new List<StepVm>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var idx = prefix.Length == 0 ? $"{i + 1}" : $"{prefix}{i + 1}";
            var vm = new StepVm
            {
                Node = n,
                Parent = null,
                IndexText = idx,
                Children = BuildVmListWithMarks(n.Children, idx + "."),
            };
            vm.SetMark(_runMarks.TryGetValue(n.Id, out var m) ? m : StepMark.None);
            _vmById[n.Id] = vm;
            list.Add(vm);
        }
        return list;
    }

    /// <summary>给某个步骤行打标记（同时记进 _runMarks，RefreshTree 后仍保留颜色）。</summary>
    private void MarkStep(StepNode node, StepMark mark)
    {
        if (mark == StepMark.None) _runMarks.Remove(node.Id);
        else _runMarks[node.Id] = mark;
        _vmById.TryGetValue(node.Id, out var vm);
        vm?.SetMark(mark);
    }

    /// <summary>把当前所有"执行中"（黄色）的行落定为已完成；失败行保持红色。</summary>
    private void SettleRunningMarks(Guid? exceptNodeId = null)
    {
        foreach (var (id, vm) in _vmById)
        {
            if (exceptNodeId is not null && id == exceptNodeId.Value) continue;
            if (!vm.IsRunning) continue;
            MarkStep(vm.Node, StepMark.Done);
        }
    }

    /// <summary>清空全部执行标记（新一轮回放/单步开始时）。</summary>
    private void ClearRunMarks()
    {
        _runMarks.Clear();
        foreach (var vm in _vmById.Values) vm.SetMark(StepMark.None);
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
        // 但"复制结果"要复制用户右键点中的那一行，所以单独记一份命中行
        _contextVm = VmFromPoint(e.GetPosition(TvSteps));
    }

    /// <summary>右键"复制结果"：把步骤最近一次执行的结果/错误文本复制到剪贴板（多选时复制全部）。</summary>
    private void MenuCopyResult_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var vms = FlattenVms(TvSteps.ItemsSource as List<StepVm>)
                .Where(v => v.IsMultiSelected)
                .ToList();
            if (vms.Count == 0)
            {
                var one = _contextVm ?? _selectedVm;
                if (one is null) { TxtStatus.Text = "先在步骤树里选中一行（或多选）再复制结果"; return; }
                vms.Add(one);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var vm in vms)
            {
                var n = vm.Node;
                var idx = (vm.IndexText ?? "").Trim();
                sb.AppendLine($"{(idx.Length > 0 ? idx + " " : "")}{n.DisplayText}");
                if (n.Kind == StepKinds.Action && n.Action is { } a)
                {
                    sb.AppendLine($"  状态: {(a.Success ? "成功" : "失败")}");
                    if (a.ElementName.Length > 0) sb.AppendLine($"  元素: {a.ElementName}");
                    sb.AppendLine($"  结果: {(string.IsNullOrWhiteSpace(a.Result) ? "(无返回值)" : a.Result)}");
                }
                else
                {
                    var d = n.DetailText;
                    if (!string.IsNullOrWhiteSpace(d)) sb.AppendLine($"  结果: {d}");
                }
                sb.AppendLine();
            }

            var text = sb.ToString().TrimEnd();
            if (text.Length == 0) { TxtStatus.Text = "该步骤还没有可复制的内容"; return; }
            System.Windows.Clipboard.SetText(text);
            TxtStatus.Text = $"已复制 {vms.Count} 个步骤的结果到剪贴板（{text.Length} 字符）";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"复制结果失败: {ex.Message}";
        }
    }

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
