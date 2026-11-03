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
    public string? ElementPath { get; init; }
    public IList<object>? ChangedProperties { get; init; }

    /// <summary>转换为 ActionRecord（统一录制格式）。</summary>
    public ActionRecord ToActionRecord() => new()
    {
        Timestamp = Timestamp,
        ElementPath = ElementPath ?? "",
        ElementName = ElementName,
        PatternName = $"UIA_Event_{EventId}",
        MethodName = EventName,
        Parameters = null,
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
    private AutomationElement? _hookElement;      // 实际挂载的元素（Stop 注销必须与注册同一元素，否则泄漏）
    private bool _structureRegistered;            // 结构事件是否已注册（独立 API，独立注销）
    private volatile bool _detached;              // Dispose 后立即停止转发（后台注销最长数秒，期间不再重复记事件）

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

    /// <summary>创建事件监听器。targetWindowHandle/targetProcessId 来自当前目标窗口（MainWindow 注入），用于限定监听范围。</summary>
    public EventListenerCore(int targetElementId, EventListenScope scope = EventListenScope.Descendants, Action<UiaEventRecord>? onEvent = null,
        IntPtr targetWindowHandle = default, int targetProcessId = 0)
    {
        _targetElementId = targetElementId;
        _scope = scope;
        _onEvent = onEvent;
        _targetWindowHandle = targetWindowHandle;
        _targetProcessId = targetProcessId;
    }

    /// <summary>
    /// 开始监听（异步）：注册派发到专用 STA 管道线程执行（全桌面注册耗时 ~2s，不阻塞 UI 线程），
    /// 完成与否看日志 "Start → OK"；IsListening 在注册完成后才为 true。
    /// </summary>
    public void Start(IEnumerable<int>? eventIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        var ids = new HashSet<int>(eventIds ?? DefaultEventIds);
        EnsurePipe().BeginInvoke((Action)(() => DoStart(ids)));
    }

    /// <summary>停止监听（异步）：注销同样派发到管道线程（全桌面注销曾耗时 8s，绝不能阻塞 UI）。</summary>
    public void Stop()
    {
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

    /// <summary>实际注册（管道线程执行）。单个事件失败只跳过，不中断整体。</summary>
    private void DoStart(HashSet<int> ids)
    {
        if (_isDisposed) return;
        var sw = Stopwatch.StartNew();
        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}, hwnd={_targetWindowHandle}, pid={_targetProcessId}");

        try
        {
            // 挂载点：优先目标窗口子树（精准，系统层只推该窗口事件，也不会录到自己）；
            // 无句柄/句柄失效时退回桌面根 Descendants + 回调按进程过滤
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
            _hookElement = hook;

            int registered = 0, failed = 0;
            foreach (var (evt, name) in CoreEvents)
            {
                if (!ids.Contains(evt.Id)) continue;
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
            if (ids.Contains(StructureEvent.Id))
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

            _isListening = registered > 0;
            AppLog.Write("RECORD", $"EventListenerCore.Start → {(registered > 0 ? "OK" : "FAILED")} (scope={treeScope}, {registered} registered, {failed} skipped, {sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            _isListening = false;
            AppLog.Write("RECORD", $"EventListenerCore.Start → EXCEPTION: {ex.GetType().Name}: {ex.Message}");
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

            AppLog.Write("RECORD", $"EventListenerCore.Stop → OK (seen={_seen}, recorded={_recorded}, filteredOwn={_filteredOwn}, filteredOther={_filteredOther}, lastFilteredPid={_lastFilteredPid}, {sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Stop → exception (ignored): {ex.Message}");
        }

        _isListening = false;
        _hookElement = null;
    }

    /// <summary>WPF 原生 UIA 事件回调。</summary>
    private void OnAutomationEvent(object sender, AutomationEventArgs e)
        => HandleEvent(sender, e.EventId.Id,
            EventNames.TryGetValue(e.EventId.Id, out var name) ? name : $"Event_{e.EventId.Id}");

    /// <summary>结构变化回调（独立 API）。</summary>
    private void OnStructureChanged(object sender, StructureChangedEventArgs e)
        => HandleEvent(sender, StructureEvent.Id, "Structure Changed");

    /// <summary>公共处理：进程过滤 → 读取元素信息 → 转发 UiaEventRecord。</summary>
    private void HandleEvent(object sender, int eventId, string eventName)
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
                ElementPath = null,
                ChangedProperties = null
            };

            AppLog.Write("RECORD", $"OnAutomationEvent: {eventName} elem={elemName} type={ctrlType}");
            _recorded++;
            _onEvent?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"OnAutomationEvent → exception (ignored): {ex.Message}");
        }
    }

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
        _detached = true; // 立即停止事件转发（注销在后台慢慢完成）
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
