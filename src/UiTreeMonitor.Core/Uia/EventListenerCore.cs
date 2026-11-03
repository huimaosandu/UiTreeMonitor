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

    /// <summary>是否正在监听中。</summary>
    public bool IsListening => _isListening;

    // ★ 核心 UIA 事件定义（仅使用 WPF Automation 类中实际存在的静态字段）
    //   格式：(事件ID, 名称, AutomationEvent 实例)
    private static readonly (int Id, string Name, AutomationEvent? Event)[] CoreEvents =
    {
        // 用户交互类（最常用）
        (20000, "Invoke Invoked", InvokePattern.InvokedEvent),
        (20004, "Element Selected", SelectionItemPattern.ElementSelectedEvent),
        (20005, "Added To Selection", SelectionItemPattern.ElementAddedToSelectionEvent),
        (20006, "Removed From Selection", SelectionItemPattern.ElementRemovedFromSelectionEvent),
        (20008, "Focus Changed", AutomationElement.AutomationFocusChangedEvent),

        // 文本类
        (20011, "Text Changed", TextPattern.TextChangedEvent),

        // 结构类
        (20013, "Structure Changed", AutomationElement.StructureChangedEvent),

        // 菜单/提示类
        (20017, "Menu Opened", AutomationElement.MenuOpenedEvent),
        (20018, "Menu Closed", AutomationElement.MenuClosedEvent),
        (20019, "ToolTip Opened", AutomationElement.ToolTipOpenedEvent),
        (20020, "ToolTip Closed", AutomationElement.ToolTipClosedEvent),

        // 窗口类
        (20024, "Window Opened", WindowPattern.WindowOpenedEvent),
        (20025, "Window Closed", WindowPattern.WindowClosedEvent),
    };

    /// <summary>默认监听的事件 ID 列表。</summary>
    public static readonly IReadOnlyList<int> DefaultEventIds = CoreEvents
        .Where(e => e.Event is not null)
        .Select(e => e.Id)
        .ToList();

    /// <summary>事件 ID → 可读名称映射表。</summary>
    public static readonly IReadOnlyDictionary<int, string> EventNames = CoreEvents
        .ToDictionary(e => e.Id, e => e.Name);

    /// <summary>创建事件监听器。</summary>
    public EventListenerCore(int targetElementId, EventListenScope scope = EventListenScope.Descendants, Action<UiaEventRecord>? onEvent = null)
    {
        _targetElementId = targetElementId;
        _scope = scope;
        _onEvent = onEvent;
    }

    /// <summary>开始监听 UIA 事件。必须在 STA 线程调用。</summary>
    public void Start(IEnumerable<int>? eventIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        if (_isListening) throw new InvalidOperationException("Already listening, call Stop first");

        var ids = (eventIds ?? DefaultEventIds).ToList();
        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}");

        try
        {
            var root = AutomationElement.RootElement;
            var treeScope = TreeScope.Descendants;

            int registered = 0;
            foreach (var (evtId, evtName, evt) in CoreEvents)
            {
                if (evt is null) continue;
                if (!ids.Contains(evtId)) continue;

                Automation.AddAutomationEventHandler(evt, root, treeScope, OnAutomationEvent);
                registered++;
                AppLog.Write("RECORD", $"  Registered: {evtName} (ID={evtId})");
            }

            _isListening = true;
            AppLog.Write("RECORD", $"EventListenerCore.Start → OK (WPF native, {registered} events registered)");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Start → EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>停止监听并释放资源。</summary>
    public void Stop()
    {
        if (!_isListening || _isDisposed) return;

        try
        {
            var root = AutomationElement.RootElement;

            foreach (var (_, _, evt) in CoreEvents)
            {
                if (evt is not null)
                {
                    try { Automation.RemoveAutomationEventHandler(evt, root, OnAutomationEvent); }
                    catch { /* 忽略移除失败 */ }
                }
            }

            AppLog.Write("RECORD", "EventListenerCore.Stop → OK");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Stop → exception (ignored): {ex.Message}");
        }

        _isListening = false;
    }

    /// <summary>WPF 原生 UIA 事件回调。</summary>
    private void OnAutomationEvent(object sender, AutomationEventArgs e)
    {
        try
        {
            if (sender is not AutomationElement elem) return;

            // 通过 EventId 查找名称
            var eventName = EventNames.TryGetValue(e.EventId.Id, out var name) ? name : $"Event_{e.EventId.Id}";

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
                EventId = e.EventId.Id,
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
