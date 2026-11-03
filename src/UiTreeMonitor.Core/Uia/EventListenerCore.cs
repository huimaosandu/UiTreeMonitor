// UIA 事件自动监听引擎：基于 WPF System.Windows.Automation 原生 API
// 对目标元素（或其子树/后代）注册 UIA 事件监听，自动捕获用户操作
// （按钮点击、值变化、焦点切换、选择变更等），转换为 ActionRecord 统一格式
//
// ★ 设计决策：不使用 Axe.Windows.ListenAction（依赖 DesktopElementContext 初始化）
//   改用 WPF 原生 Automation.AddAutomationEventHandler + AutomationEvent
//   优点：无需额外初始化、STA 线程直接可用、与 UiaSession 同款 interop
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation; // ★ WPF 原生 UIA（需 csproj 中 <UseWPF>true</UseWPF>）
using System.Windows.Threading;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Uia;

/// <summary>
/// UIA 事件监听模式（对应原版 RecorderSetting.ListenScope）。
/// </summary>
public enum EventListenScope
{
    Element,
    Subtree,
    Descendants
}

/// <summary>单条 UIA 事件的录制记录。</summary>
public sealed record UiaEventRecord
{
    public DateTime Timestamp { get; init; }
    public int EventId { get; init; }
    public string EventName { get; init; } = "";
    public int ElementUniqueId { get; init; }
    public string ElementName { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string? ElementPath { get; init; }        // 语义记录=元素定位 spec（回放时在目标窗口树中重定位）
    public IList<object>? ChangedProperties { get; init; }

    // ==== 语义化（可回放）字段：UIA 事件/属性变化 → 对应 Pattern 操作 ====
    /// <summary>回放 Pattern 名（如 ValuePattern/InvokePattern，与 Axe.Windows A11yPattern.Name 对齐）；null=不可回放仅记录。</summary>
    public string? PlaybackPattern { get; init; }
    /// <summary>回放方法名（如 SetValue/Invoke/Select）。</summary>
    public string? PlaybackMethod { get; init; }
    /// <summary>回放参数（如 SetValue 的新值）。</summary>
    public object?[]? PlaybackParameters { get; init; }
    /// <summary>true=替换脚本中上一条同元素同 Pattern 记录（连续输入/拖动每步都触发变化，只保留最终值）。</summary>
    public bool ReplacePrevious { get; init; }

    /// <summary>转换为 ActionRecord（统一录制格式；可回放记录 PatternName=真实 Pattern 名，回放走 PatternActionRunner）。</summary>
    public ActionRecord ToActionRecord() => new()
    {
        Timestamp = Timestamp,
        ElementPath = ElementPath ?? "",
        ElementName = ElementName,
        PatternName = PlaybackPattern ?? $"UIA_Event_{EventId}",
        MethodName = PlaybackMethod ?? EventName,
        Parameters = PlaybackParameters,
        Success = true,
        Result = ChangedProperties?.Count > 0
            ? string.Join("; ", ChangedProperties.Select(p => p.ToString() ?? ""))
            : null
    };
}

/// <summary>
/// UIA 事件自动监听器：使用 WPF 原生 Automation.AddAutomationEventListener 实现对窗口/元素的操作自动捕获。
///
/// 线程要求：Start/Stop 需在 STA 线程调用（UIA 要求）；回调来自后台线程。
/// </summary>
public sealed class EventListenerCore : IDisposable
{
    private bool _isDisposed;
    private bool _isListening;
    private readonly int _targetElementId;
    private readonly EventListenScope _scope;
    private readonly Action<UiaEventRecord>? _onEvent;
    private readonly IntPtr _targetWindowHandle;  // 目标窗口句柄：≠0 时监听挂到该窗口子树（不再全桌面）
    private readonly int _targetProcessId;        // 目标进程：≠0 时回调按进程过滤，只录目标应用的操作
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly Action<bool>? _onStartCompleted; // 注册完成回调（管道线程调用；通知 UI "监听就绪可操作"）
    private AutomationElement? _hookElement;      // 实际挂载的元素（Stop 注销必须与注册同一元素，否则泄漏）
    private bool _structureRegistered;            // 结构事件是否已注册（独立 API，独立注销）
    private bool _propertiesRegistered;           // 属性变化是否已注册（独立 API，独立注销）
    private volatile bool _detached;              // Dispose 后立即停止转发（后台注销最长数秒，期间不再重复记事件）
    private volatile bool _stopRequested;         // Stop/Dispose 请求：DoStart 注册循环每步检查立即中断（曾 14.5s 注册期点停止还要排队等完）

    // ★ UIA 专用 STA 管道线程：注册/注销（尤其全桌面 RootElement）是重量级跨进程操作，
    //   每个事件 ~200-700ms、一次 Stop 曾耗时 8 秒——绝不能在 UI 线程同步执行（悬停触发重挂时 UI 冻结）。
    //   UIA 线程模型还要求注册线程常驻+消息泵（Dispatcher.Run），否则回调丢失。
    private Thread? _pipe;
    private Dispatcher? _pipeDispatcher;

    // 诊断统计（Stop 时输出汇总；排查"录不到动作"时看事件是否被过滤卡住）
    private long _seen;          // 回调收到总数
    private long _recorded;      // 通过过滤并转发
    private long _filteredOwn;   // 被过滤：自己进程
    private long _filteredOther; // 被过滤：非目标进程
    private int _lastFilteredPid;

    /// <summary>是否正在监听中。</summary>
    public bool IsListening => _isListening;

    // ★ 核心 UIA 事件定义（仅使用 WPF Automation 类中实际存在的静态字段）
    //   ID 一律运行时取 AutomationEvent.Id（曾硬编码数字与 WPF 实际 ID 错位，
    //   导致 EventNames 映射失败、日志/脚本里出现 Event_20009 等未知名）
    //   注意：AutomationFocusChangedEvent 不能经 AddAutomationEventHandler 注册（抛 ArgumentException
    //   "eventId 无效"，曾让整个监听启动失败→无法捕获动作）
    //   ★ 焦点事件按需求不录制（录制动作=鼠标左键/右键点击+键盘按键，由 InputHookCore 负责），
    //     也不经独立 API 注册
    private static readonly (AutomationEvent Event, string Name)[] CoreEvents =
    {
        // 用户交互类（最常用）
        (InvokePattern.InvokedEvent, "Invoke Invoked"),
        (SelectionItemPattern.ElementSelectedEvent, "Element Selected"),
        (SelectionItemPattern.ElementAddedToSelectionEvent, "Added To Selection"),
        (SelectionItemPattern.ElementRemovedFromSelectionEvent, "Removed From Selection"),

        // 文本类
        (TextPattern.TextChangedEvent, "Text Changed"),

        // 菜单/提示类
        (AutomationElement.MenuOpenedEvent, "Menu Opened"),
        (AutomationElement.MenuClosedEvent, "Menu Closed"),
        (AutomationElement.ToolTipOpenedEvent, "ToolTip Opened"),
        (AutomationElement.ToolTipClosedEvent, "ToolTip Closed"),

        // 窗口类
        (WindowPattern.WindowOpenedEvent, "Window Opened"),
        (WindowPattern.WindowClosedEvent, "Window Closed"),
    };

    /// <summary>结构变化事件（同样不能经 AddAutomationEventHandler——抛 ArgumentException，独立 API 注册）。</summary>
    private static readonly AutomationEvent StructureEvent = AutomationElement.StructureChangedEvent;

    // ==== 语义映射：UIA 事件 → 可回放 Pattern 操作（Pattern 名与 Axe.Windows A11yPattern.Name 对齐） ====
    // 事件类
    private static readonly (AutomationEvent Event, string Pattern, string Method)[] SemanticEvents =
    {
        (InvokePattern.InvokedEvent, "InvokePattern", "Invoke"),                                // 按钮点击
        (SelectionItemPattern.ElementSelectedEvent, "SelectionItemPattern", "Select"),          // 选中（单选/下拉）
        (SelectionItemPattern.ElementAddedToSelectionEvent, "SelectionItemPattern", "AddToSelection"),
        (SelectionItemPattern.ElementRemovedFromSelectionEvent, "SelectionItemPattern", "RemoveFromSelection"),
    };

    // 属性变化类（AddAutomationPropertyChangedEventHandler，含输入文字/滑块拖动等值变化）
    //  ★ 连续变化（逐字符输入、拖动过程）ReplacePrevious=true：脚本中只保留最终值，回放 SetValue 幂等
    private static readonly AutomationProperty[] SemanticProperties =
    {
        ValuePattern.ValueProperty,                    // 输入框文字 → ValuePattern.SetValue
        RangeValuePattern.ValueProperty,          // 滑块/进度拖动 → RangeValuePattern.SetValue
        TogglePattern.ToggleStateProperty,             // 复选/开关 → TogglePattern.Toggle
        ExpandCollapsePattern.ExpandCollapseStateProperty, // 展开/折叠 → ExpandCollapsePattern.Expand/Collapse
    };

    /// <summary>语义模式注册集：仅 4 个可回放事件（+属性变化回调，DoStart 固定注册）。
    /// ★ 不含 Menu/ToolTip/Window/Text/Structure——无回放价值，且全桌面每多注册一个事件
    ///   就多 1~2 秒注册窗口期（曾 11 次注册 14.5s，期间用户操作全部漏录 seen=0）。</summary>
    public static readonly IReadOnlyList<int> SemanticEventIds = SemanticEvents
        .Select(s => s.Event.Id)
        .ToList();

    /// <summary>高频低价值事件（自由模式=无目标全桌面时排除，防噪声刷屏）。</summary>
    private static readonly HashSet<AutomationEvent> NoisyEvents = new()
    {
        TextPattern.TextChangedEvent,
        AutomationElement.StructureChangedEvent,
    };

    /// <summary>默认监听的事件 ID 列表。</summary>
    public static readonly IReadOnlyList<int> DefaultEventIds = CoreEvents
        .Select(e => e.Event.Id)
        .Append(StructureEvent.Id)
        .ToList();

    /// <summary>交互类事件（无 Text/Structure 高频噪声）。</summary>
    public static readonly IReadOnlyList<int> InteractiveEventIds = CoreEvents
        .Where(e => !NoisyEvents.Contains(e.Event))
        .Select(e => e.Event.Id)
        .ToList();

    /// <summary>事件 ID → 可读名称映射表。</summary>
    public static readonly IReadOnlyDictionary<int, string> EventNames = CoreEvents
        .Select(e => new KeyValuePair<int, string>(e.Event.Id, e.Name))
        .Append(new KeyValuePair<int, string>(StructureEvent.Id, "Structure Changed"))
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>创建事件监听器。targetWindowHandle/targetProcessId 来自当前目标窗口（MainWindow 注入），用于限定监听范围；
    /// onStartCompleted 在注册完成/失败后回调（管道线程）。</summary>
    public EventListenerCore(int targetElementId, EventListenScope scope = EventListenScope.Descendants, Action<UiaEventRecord>? onEvent = null,
        IntPtr targetWindowHandle = default, int targetProcessId = 0, Action<bool>? onStartCompleted = null)
    {
        _targetElementId = targetElementId;
        _scope = scope;
        _onEvent = onEvent;
        _targetWindowHandle = targetWindowHandle;
        _targetProcessId = targetProcessId;
        _onStartCompleted = onStartCompleted;
    }

    /// <summary>
    /// 开始监听（异步）：注册派发到专用 STA 管道线程执行（全桌面注册耗时数秒，不阻塞 UI 线程），
    /// 完成与否看日志 "Start → OK" 或 onStartCompleted 回调；IsListening 在注册完成后才为 true。
    /// </summary>
    public void Start(IEnumerable<int>? eventIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        var ids = new HashSet<int>(eventIds ?? DefaultEventIds);
        _stopRequested = false; // 新一轮注册（Stop→Start 复用同实例时复位中断标志）
        EnsurePipe().BeginInvoke((Action)(() => DoStart(ids)));
    }

    /// <summary>停止监听（异步）：置中断标志让注册循环立即跳出（不等十几秒注册完），注销在管道线程执行。</summary>
    public void Stop()
    {
        _stopRequested = true;
        var d = _pipeDispatcher;
        if (d is null) { _isListening = false; return; }
        d.BeginInvoke((Action)DoStop);
    }

    /// <summary>确保 UIA 专用 STA 管道线程就绪（常驻 + Dispatcher 消息泵，UIA 回调要求）。</summary>
    private Dispatcher EnsurePipe()
    {
        if (_pipeDispatcher is not null) return _pipeDispatcher;
        var ready = new ManualResetEventSlim();
        _pipe = new Thread(() =>
        {
            _pipeDispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run(); // 消息泵：UIA 在注册线程上回调，线程必须常驻
        })
        { IsBackground = true, Name = "UIAEventPipe" };
        _pipe.SetApartmentState(ApartmentState.STA);
        _pipe.Start();
        ready.Wait(2000);
        return _pipeDispatcher!;
    }

    /// <summary>实际注册（管道线程执行）。单个事件失败只跳过，不中断整体；_stopRequested 每步检查立即中止。</summary>
    private void DoStart(HashSet<int> ids)
    {
        if (_isDisposed || _stopRequested) return;
        var sw = Stopwatch.StartNew();
        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}, hwnd={_targetWindowHandle}, pid={_targetProcessId}");

        try
        {
            // ★ 挂载点三档（注册耗时差异巨大，直接决定"注册窗口期"内用户操作是否漏录）：
            //   ① 目标窗口子树（hwnd 精准，~0.1s）
            //   ② 目标进程顶层窗口子树（pid → FindAll 顶层窗口，~0.5s；
            //     曾长期有 pid 仍挂全桌面+回调过滤——11 次全桌面注册 14.5s，用户操作全漏 seen=0）
            //   ③ 桌面根 Descendants（free mode 兜底；每事件 1~2s 跨进程握手，悬停目标后会升级为 ②）
            var hook = AutomationElement.RootElement;
            var treeScope = TreeScope.Descendants;
            if (_targetWindowHandle != IntPtr.Zero)
            {
                try
                {
                    hook = AutomationElement.FromHandle(_targetWindowHandle);
                    treeScope = TreeScope.Subtree;
                }
                catch (Exception ex)
                {
                    AppLog.Write("RECORD", $"  FromHandle({_targetWindowHandle}) failed → fallback to desktop root: {ex.Message}");
                }
            }
            else if (_targetProcessId != 0)
            {
                try
                {
                    var wins = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, _targetProcessId));
                    AutomationElement? pick = null;
                    string pickName = "";
                    foreach (AutomationElement w in wins)
                    {
                        try
                        {
                            if (w.Current.BoundingRectangle.IsEmpty) continue; // 跳过不可见窗口
                            pick = w;
                            pickName = w.Current.Name ?? "";
                            break;
                        }
                        catch { /* 元素已销毁 */ }
                    }
                    if (pick is not null)
                    {
                        hook = pick;
                        treeScope = TreeScope.Subtree;
                        AppLog.Write("RECORD", $"  Attached: process top window (pid={_targetProcessId}, name=\"{pickName}\") → Subtree (fast path)");
                    }
                    else
                        AppLog.Write("RECORD", $"  No visible top-level window for pid={_targetProcessId} → fallback desktop root");
                }
                catch (Exception ex)
                {
                    AppLog.Write("RECORD", $"  Find process window failed (pid={_targetProcessId}) → fallback desktop root: {ex.Message}");
                }
            }
            _hookElement = hook;

            int registered = 0, failed = 0;

            // ★ 属性变化最先注册：一次调用覆盖 4 属性（输入文字/滑块拖动/开关/展开折叠），价值密度最高
            if (!_stopRequested)
            {
                try
                {
                    Automation.AddAutomationPropertyChangedEventHandler(hook, treeScope, OnPropertyChanged,
                        ValuePattern.ValueProperty, RangeValuePattern.ValueProperty,
                        TogglePattern.ToggleStateProperty, ExpandCollapsePattern.ExpandCollapseStateProperty);
                    _propertiesRegistered = true;
                    registered++;
                    AppLog.Write("RECORD", "  Registered: Property Changed (Value/RangeValue/ToggleState/ExpandCollapse → SetValue/Toggle/Expand)");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write("RECORD", $"  Register FAILED (skipped): Property Changed: {ex.Message}");
                }
            }

            foreach (var (evt, name) in CoreEvents)
            {
                if (!ids.Contains(evt.Id)) continue;
                if (_stopRequested) { AppLog.Write("RECORD", "  Start aborted (stop requested during register)"); break; }
                try
                {
                    Automation.AddAutomationEventHandler(evt, hook, treeScope, OnAutomationEvent);
                    registered++;
                    AppLog.Write("RECORD", $"  Registered: {name} (ID={evt.Id})");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write("RECORD", $"  Register FAILED (skipped): {name} (ID={evt.Id}): {ex.Message}");
                }
            }

            // 结构变化事件：同样必须走独立 API
            if (!_stopRequested && ids.Contains(StructureEvent.Id))
            {
                try
                {
                    Automation.AddStructureChangedEventHandler(hook, treeScope, OnStructureChanged);
                    _structureRegistered = true;
                    registered++;
                    AppLog.Write("RECORD", $"  Registered: Structure Changed (ID={StructureEvent.Id})");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write("RECORD", $"  Register FAILED (skipped): Structure Changed: {ex.Message}");
                }
            }

            bool ok = registered > 0 && !_stopRequested;
            _isListening = ok;
            AppLog.Write("RECORD", $"EventListenerCore.Start → {(_stopRequested ? "ABORTED (stop requested)" : ok ? "OK" : "FAILED")} (scope={treeScope}, {registered} registered, {failed} skipped, {sw.ElapsedMilliseconds}ms)");
            _onStartCompleted?.Invoke(ok);
        }
        catch (Exception ex)
        {
            _isListening = false;
            AppLog.Write("RECORD", $"EventListenerCore.Start → EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _onStartCompleted?.Invoke(false);
        }
    }

    /// <summary>实际注销（管道线程执行）。注销必须用与注册相同的挂载元素。</summary>
    private void DoStop()
    {
        if (_isDisposed && _pipeDispatcher is null) return;
        var sw = Stopwatch.StartNew();
        try
        {
            var hook = _hookElement ?? AutomationElement.RootElement;
            foreach (var (evt, _) in CoreEvents)
            {
                try { Automation.RemoveAutomationEventHandler(evt, hook, OnAutomationEvent); }
                catch { /* 忽略移除失败 */ }
            }
            if (_structureRegistered)
            {
                try { Automation.RemoveStructureChangedEventHandler(hook, OnStructureChanged); }
                catch { /* 忽略移除失败 */ }
                _structureRegistered = false;
            }
            if (_propertiesRegistered)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(hook, OnPropertyChanged); }
                catch { /* 忽略移除失败 */ }
                _propertiesRegistered = false;
            }

            AppLog.Write("RECORD", $"EventListenerCore.Stop → OK (seen={_seen}, recorded={_recorded}, filteredOwn={_filteredOwn}, filteredOther={_filteredOther}, lastFilteredPid={_lastFilteredPid}, {sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Stop → exception (ignored): {ex.Message}");
        }

        _isListening = false;
        _hookElement = null;
    }

    /// <summary>WPF 原生 UIA 事件回调：命中语义表则产出可回放记录（Invoke/Select 等）。</summary>
    private void OnAutomationEvent(object sender, AutomationEventArgs e)
    {
        var name = EventNames.TryGetValue(e.EventId.Id, out var n) ? n : $"Event_{e.EventId.Id}";
        var semantic = SemanticEvents.FirstOrDefault(s => s.Event == e.EventId);
        if (semantic != default)
            HandleEvent(sender, e.EventId.Id, name, semantic.Pattern, semantic.Method);
        else
            HandleEvent(sender, e.EventId.Id, name); // 仅记录（Menu/Window/Text 等）
    }

    /// <summary>结构变化回调（独立 API；仅记录）。</summary>
    private void OnStructureChanged(object sender, StructureChangedEventArgs e)
        => HandleEvent(sender, StructureEvent.Id, "Structure Changed");

    /// <summary>属性变化回调（独立 API）：输入文字/滑块拖动/开关/展开折叠 → 可回放 SetValue/Toggle/Expand。</summary>
    private void OnPropertyChanged(object sender, AutomationPropertyChangedEventArgs e)
    {
        if (e.Property == ValuePattern.ValueProperty)
            HandleEvent(sender, e.Property.Id, "Value Changed",
                "ValuePattern", "SetValue", new object?[] { e.NewValue }, replacePrevious: true);
        else if (e.Property == RangeValuePattern.ValueProperty)
            HandleEvent(sender, e.Property.Id, "RangeValue Changed",
                "RangeValuePattern", "SetValue", new object?[] { e.NewValue }, replacePrevious: true);
        else if (e.Property == TogglePattern.ToggleStateProperty)
            HandleEvent(sender, e.Property.Id, "Toggle State Changed", "TogglePattern", "Toggle");
        else if (e.Property == ExpandCollapsePattern.ExpandCollapseStateProperty)
            HandleEvent(sender, e.Property.Id, "ExpandCollapse Changed", "ExpandCollapsePattern",
                e.NewValue?.ToString() == "Expanded" ? "Expand" : "Collapse");
    }

    /// <summary>公共处理：进程过滤 → 读取元素信息 → 转发 UiaEventRecord（带语义映射时可回放）。</summary>
    private void HandleEvent(object sender, int eventId, string eventName,
        string? pattern = null, string? method = null, object?[]? parameters = null, bool replacePrevious = false)
    {
        try
        {
            if (_detached) return; // 已弃用：后台注销完成前不再转发（防新旧监听器窗口期重复记录）
            if (sender is not AutomationElement elem) return;

            // ★ 进程过滤：绝不录自己（录制列表每刷一次就触发 UIA 事件，又被录下 → 自我反馈死循环，
            //   录出来的全是自己 UI 的垃圾记录）；指定了目标进程时只录目标进程
            _seen++;
            int pid;
            try { pid = elem.Current.ProcessId; }
            catch { return; /* 元素已销毁 */ }
            if (pid == _ownProcessId) { _filteredOwn++; LogFilterStats(); return; }
            if (_targetProcessId != 0 && pid != _targetProcessId) { _filteredOther++; _lastFilteredPid = pid; LogFilterStats(); return; }

            // 轻量级读取元素信息
            string elemName = "";
            string ctrlType = "";
            try
            {
                elemName = elem.Current.Name ?? "";
                ctrlType = elem.Current.ControlType.ProgrammaticName ?? "";
            }
            catch { /* 元素可能已销毁 */ }

            var record = new UiaEventRecord
            {
                Timestamp = DateTime.Now,
                EventId = eventId,
                EventName = eventName,
                ElementUniqueId = 0,
                ElementName = elemName,
                ControlType = ctrlType,
                ElementPath = pattern is null ? null : BuildSpec(elem), // 可回放记录附带定位 spec
                ChangedProperties = null,
                PlaybackPattern = pattern,
                PlaybackMethod = method,
                PlaybackParameters = parameters,
                ReplacePrevious = replacePrevious
            };

            AppLog.Write("RECORD", $"OnAutomationEvent: {eventName} elem={elemName} type={ctrlType}" +
                (pattern is not null ? $" → {pattern}.{method}({string.Join(", ", parameters ?? Array.Empty<object?>())}){(replacePrevious ? " [merge]" : "")}" : ""));
            _recorded++;
            _onEvent?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"OnAutomationEvent → exception (ignored): {ex.Message}");
        }
    }

    // ==== 元素定位 spec：录制时现场构建（AutoId/Name/Type/Class/矩形），回放时在目标窗口树中重定位 ====
    //  格式：spec:v1|autoId=..|name=..|type=..|class=..|rect=L,T,R,B（值 URL 编码，可含 | = 等字符）
    /// <summary>从 WPF AutomationElement 现场构建定位 spec（回放端 MainWindow.ResolveElementForPlayback 解析）。</summary>
    public static string BuildSpec(AutomationElement elem)
    {
        try
        {
            var c = elem.Current;
            var r = c.BoundingRectangle;
            var rect = r.IsEmpty || r.Width <= 0 || r.Height <= 0 ? "" : $"{(int)r.Left},{(int)r.Top},{(int)r.Right},{(int)r.Bottom}";
            return $"spec:v1|autoId={Esc(c.AutomationId)}|name={Esc(c.Name)}|type={TypeShort(c.ControlType?.ProgrammaticName)}|class={Esc(c.ClassName)}|rect={rect}";
        }
        catch { return "spec:v1|"; }
    }

    private static string? Esc(string? s) => string.IsNullOrEmpty(s) ? "" : Uri.EscapeDataString(s);

    /// <summary>"ControlType.Button" → "Button"（与树采样 ControlTypeName 同格式）。</summary>
    private static string TypeShort(string? programmaticName)
        => string.IsNullOrEmpty(programmaticName) ? "" : programmaticName.Replace("ControlType.", "");

    /// <summary>过滤统计节流日志：每累计 50 条被过滤事件输出 1 次（避免高频事件刷爆日志）。</summary>
    private void LogFilterStats()
    {
        var filtered = _filteredOwn + _filteredOther;
        if (filtered % 50 != 0) return;
        AppLog.Write("RECORD", $"  [filter-stats] seen={_seen}, recorded={_recorded}, filteredOwn={_filteredOwn}, filteredOther={_filteredOther}, lastFilteredPid={_lastFilteredPid}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _detached = true;     // 立即停止事件转发（注销在后台慢慢完成）
        _stopRequested = true; // 注册循环立即中断（不等全桌面逐事件注册完）
        var d = _pipeDispatcher;
        if (d is null) return;
        // 异步注销+关闭管道线程（注销可能耗时数秒，不阻塞调用方/UI 线程）
        d.BeginInvoke((Action)(() =>
        {
            DoStop();
            d.InvokeShutdown();
        }));
    }
}
