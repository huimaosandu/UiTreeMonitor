// UIA 事件自动监听引擎：基于 WPF System.Windows.Automation 原生 API
// 对目标元素（或其子树/后代）注册 UIA 事件监听，自动捕获用户操作
// （按钮点击、值变化、焦点切换、选择变更等），转换为 ActionRecord 统一格式
//
// ★ 设计决策：不使用 Axe.Windows.ListenAction（依赖 DesktopElementContext 初始化）
//   改用 WPF 原生 Automation.AddAutomationEventHandler + AutomationEvent
//   优点：无需额外初始化、STA 线程直接可用、与 UiaSession 同款 interop
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation; // ★ WPF 原生 UIA（需 csproj 中 <UseWPF>true</UseWPF>）
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
    private bool _focusRegistered;                // 焦点事件是否已注册（独立 API，独立注销）

    /// <summary>是否正在监听中。</summary>
    public bool IsListening => _isListening;

    // ★ 核心 UIA 事件定义（仅使用 WPF Automation 类中实际存在的静态字段）
    //   ID 一律运行时取 AutomationEvent.Id（曾硬编码数字与 WPF 实际 ID 错位，
    //   导致 EventNames 映射失败、日志/脚本里出现 Event_20009 等未知名）
    //   注意：AutomationFocusChangedEvent 不能经 AddAutomationEventHandler 注册（抛 ArgumentException
    //   "eventId 无效"，曾让整个监听启动失败→无法捕获动作），由 Start 用独立 API 单独处理，不放本表
    private static readonly (AutomationEvent Event, string Name)[] CoreEvents =
    {
        // 用户交互类（最常用）
        (InvokePattern.InvokedEvent, "Invoke Invoked"),
        (SelectionItemPattern.ElementSelectedEvent, "Element Selected"),
        (SelectionItemPattern.ElementAddedToSelectionEvent, "Added To Selection"),
        (SelectionItemPattern.ElementRemovedFromSelectionEvent, "Removed From Selection"),

        // 文本类
        (TextPattern.TextChangedEvent, "Text Changed"),

        // 结构类
        (AutomationElement.StructureChangedEvent, "Structure Changed"),

        // 菜单/提示类
        (AutomationElement.MenuOpenedEvent, "Menu Opened"),
        (AutomationElement.MenuClosedEvent, "Menu Closed"),
        (AutomationElement.ToolTipOpenedEvent, "ToolTip Opened"),
        (AutomationElement.ToolTipClosedEvent, "ToolTip Closed"),

        // 窗口类
        (WindowPattern.WindowOpenedEvent, "Window Opened"),
        (WindowPattern.WindowClosedEvent, "Window Closed"),
    };

    /// <summary>焦点事件（独立 API 注册，ID 同样运行时取）。</summary>
    private static readonly AutomationEvent FocusEvent = AutomationElement.AutomationFocusChangedEvent;

    /// <summary>默认监听的事件 ID 列表。</summary>
    public static readonly IReadOnlyList<int> DefaultEventIds = CoreEvents
        .Select(e => e.Event.Id)
        .Append(FocusEvent.Id)
        .ToList();

    /// <summary>事件 ID → 可读名称映射表。</summary>
    public static readonly IReadOnlyDictionary<int, string> EventNames = CoreEvents
        .Select(e => new KeyValuePair<int, string>(e.Event.Id, e.Name))
        .Append(new KeyValuePair<int, string>(FocusEvent.Id, "Focus Changed"))
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

    /// <summary>开始监听 UIA 事件。必须在 STA 线程调用。</summary>
    public void Start(IEnumerable<int>? eventIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        if (_isListening) throw new InvalidOperationException("Already listening, call Stop first");

        var ids = new HashSet<int>(eventIds ?? DefaultEventIds);
        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}, hwnd={_targetWindowHandle}, pid={_targetProcessId}");

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
                hook = AutomationElement.RootElement;
                treeScope = TreeScope.Descendants;
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
                // ★ 单个事件注册失败只跳过该事件，不中断整个监听
                //   （此前一个事件失败曾让 Start 整体抛异常 → 全部事件未注册 → 无法捕获动作）
                failed++;
                AppLog.Write("RECORD", $"  Register FAILED (skipped): {name} (ID={evt.Id}): {ex.Message}");
            }
        }

        // 焦点事件：必须走独立 API（AddAutomationEventHandler 对 AutomationFocusChangedEvent 抛 ArgumentException）
        if (ids.Contains(FocusEvent.Id))
        {
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);
                _focusRegistered = true;
                registered++;
                AppLog.Write("RECORD", $"  Registered: Focus Changed (ID={FocusEvent.Id}, global + pid filter)");
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Write("RECORD", $"  Register FAILED (skipped): Focus Changed: {ex.Message}");
            }
        }

        _isListening = registered > 0;
        if (registered == 0)
            throw new InvalidOperationException($"EventListenerCore.Start → all {failed} event registrations failed");
        AppLog.Write("RECORD", $"EventListenerCore.Start → OK (scope={treeScope}, {registered} registered, {failed} skipped)");
    }

    /// <summary>停止监听并释放资源。</summary>
    public void Stop()
    {
        if (!_isListening || _isDisposed) return;

        try
        {
            // ★ 注销必须用与注册相同的挂载元素（此前固定用 RootElement，
            //   挂在目标窗口上时注销无效 → 处理器泄漏、旧监听器继续触发事件）
            var hook = _hookElement ?? AutomationElement.RootElement;
            foreach (var (evt, _) in CoreEvents)
            {
                try { Automation.RemoveAutomationEventHandler(evt, hook, OnAutomationEvent); }
                catch { /* 忽略移除失败 */ }
            }
            if (_focusRegistered)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(OnFocusChanged); }
                catch { /* 忽略移除失败 */ }
                _focusRegistered = false;
            }

            AppLog.Write("RECORD", "EventListenerCore.Stop → OK");
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

    /// <summary>焦点变化回调（独立 API，sender 为获得焦点的元素）。</summary>
    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
        => HandleEvent(sender, FocusEvent.Id, "Focus Changed");

    /// <summary>公共处理：进程过滤 → 读取元素信息 → 转发 UiaEventRecord。</summary>
    private void HandleEvent(object sender, int eventId, string eventName)
    {
        try
        {
            if (sender is not AutomationElement elem) return;

            // ★ 进程过滤：绝不录自己（录制列表每刷一次就触发 UIA 事件，又被录下 → 自我反馈死循环，
            //   录出来的全是自己 UI 的垃圾记录）；指定了目标进程时只录目标进程
            int pid;
            try { pid = elem.Current.ProcessId; }
            catch { return; /* 元素已销毁 */ }
            if (pid == _ownProcessId) return;
            if (_targetProcessId != 0 && pid != _targetProcessId) return;

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
            _onEvent?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"OnAutomationEvent → exception (ignored): {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
