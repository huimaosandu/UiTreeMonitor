// UIA 事件自动监听引擎：基于 Axe.Windows.ListenAction 封装
// 对目标元素（或其子树/后代）注册 UIA 事件监听，自动捕获用户操作
// （按钮点击、值变化、焦点切换、选择变更等），转换为 ActionRecord 统一格式
using System;
using System.Collections.Generic;
using System.Linq;
using Axe.Windows.Actions;
using Axe.Windows.Desktop.Types;
using Axe.Windows.Desktop.UIAutomation.EventHandlers;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Uia;

/// <summary>
/// UIA 事件监听模式（对应原版 RecorderSetting.ListenScope）。
/// </summary>
public enum EventListenScope
{
    /// <summary>仅监听目标元素本身的事件</summary>
    Element,
    /// <summary>监听目标元素及其直接/间接子树</summary>
    Subtree,
    /// <summary>监听目标元素的所有后代</summary>
    Descendants
}

/// <summary>单条 UIA 事件的录制记录（从 EventMessage 转换而来）。</summary>
public sealed record UiaEventRecord
{
    public DateTime Timestamp { get; init; }
    public int EventId { get; init; }           // EventType 枚举值
    public string EventName { get; init; } = ""; // 可读名称（如 "Invoke Invoked"）
    public int ElementUniqueId { get; init; }     // 触发元素的 UniqueId
    public string ElementName { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string? ElementPath { get; init; }     // 结构路径（可选，需外部填充）
    public IList<object>? ChangedProperties { get; init; } // 属性变化详情

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
/// UIA 事件自动监听器：基于 Axe.Windows.ListenAction 实现对目标窗口/元素的操作自动捕获。
/// 
/// 使用方式：
///   1. 创建实例（传入目标元素 UniqueId 和监听范围）
///   2. 调用 Start() 开始监听（内部创建 ListenAction 并注册事件回调）
///   3. 每条事件通过 EventRecorded 回调推送 UiaEventRecord
///   4. 调用 Stop() 停止监听并释放资源
///
/// 线程要求：Start/Stop 需在 STA 线程调用（UIA 要求）；回调来自后台线程。
/// 
/// 前置条件：使用前需确保 DesktopElementContext 已初始化（否则 CreateInstance 会抛 NullReferenceException）
/// 可通过调用 EnsureDesktopElementContextInitialized() 或在 MainWindow 启动时调用 InitDesktopElementContext()
/// </summary>
public sealed class EventListenerCore : IDisposable
{
    private Guid? _listenerId;
    private bool _isDisposed;
    private readonly int _targetElementId;         // 目标元素 UniqueId (A11yElement.UniqueId)，用于日志显示
    private readonly EventListenScope _scope;   // 监听范围
    private readonly Action<UiaEventRecord>? _onEvent;

    /// <summary>是否正在监听中。</summary>
    public bool IsListening => _listenerId.HasValue;

    /// <summary>默认监听的事件 ID 列表（覆盖常见用户操作场景）。</summary>
    public static readonly IReadOnlyList<int> DefaultEventIds = new[]
    {
        // 用户交互类
        EventType.UIA_Invoke_InvokedEventId,                    // 按钮点击/菜单选择
        EventType.UIA_SelectionItem_ElementSelectedEventId,      // 列表项选中
        EventType.UIA_AutomationFocusChangedEventId,             // 焦点切换
        // 值变化类
        EventType.UIA_AutomationPropertyChangedEventId,            // 属性变化（含 ValuePattern.Value 变化）
        EventType.UIA_Text_TextChangedEventId,                   // 文本变化
        EventType.UIA_TextEdit_TextChangedEventId,               // 编辑框文本变化
        // 结构变化类
        EventType.UIA_StructureChangedEventId,                  // UI 结构变化（增删节点）
        EventType.UIA_Window_WindowOpenedEventId,                // 窗口打开
        EventType.UIA_Window_WindowClosedEventId,                // 窗口关闭
        // 其他
        EventType.UIA_MenuOpenedEventId,                        // 菜单打开
        EventType.UIA_MenuClosedEventId,                        // 菜单关闭
        EventType.UIA_ToolTipOpenedEventId,                     // 工具提示
        EventType.UIA_ToolTipClosedEventId,                     // 工具提示关闭
    };

    /// <summary>默认不监听的属性 ID 列表（避免噪声）。</summary>
    public static readonly IReadOnlyList<int> DefaultPropertyIds = Array.Empty<int>();

    /// <summary>事件 ID → 可读名称映射表。</summary>
    public static readonly IReadOnlyDictionary<int, string> EventNames = new Dictionary<int, string>
    {
        { EventType.UIA_Invoke_InvokedEventId, "Invoke Invoked" },
        { EventType.UIA_SelectionItem_ElementSelectedEventId, "Element Selected" },
        { EventType.UIA_SelectionItem_ElementAddedToSelectionEventId, "Added To Selection" },
        { EventType.UIA_SelectionItem_ElementRemovedFromSelectionEventId, "Removed From Selection" },
        { EventType.UIA_AutomationFocusChangedEventId, "Focus Changed" },
        { EventType.UIA_AutomationPropertyChangedEventId, "Property Changed" },
        { EventType.UIA_Text_TextChangedEventId, "Text Changed" },
        { EventType.UIA_TextEdit_TextChangedEventId, "Edit Text Changed" },
        { EventType.UIA_StructureChangedEventId, "Structure Changed" },
        { EventType.UIA_Window_WindowOpenedEventId, "Window Opened" },
        { EventType.UIA_Window_WindowClosedEventId, "Window Closed" },
        { EventType.UIA_MenuOpenedEventId, "Menu Opened" },
        { EventType.UIA_MenuClosedEventId, "Menu Closed" },
        { EventType.UIA_ToolTipOpenedEventId, "ToolTip Opened" },
        { EventType.UIA_ToolTipClosedEventId, "ToolTip Closed" },
        { EventType.UIA_AsyncContentLoadedEventId, "Async Content Loaded" },
        { EventType.UIA_LayoutInvalidatedEventId, "Layout Invalidated" },
        { EventType.UIA_LiveRegionChangedEventId, "Live Region Changed" },
        { EventType.UIA_NotificationEventId, "Notification" },
        { EventType.UIA_Drag_DragStartEventId, "Drag Start" },
        { EventType.UIA_Drag_DragCompleteEventId, "Drag Complete" },
        { EventType.UIA_DropTarget_DroppedEventId, "Dropped" },
    };

    /// <summary>创建事件监听器。</summary>
    /// <param name="targetElementId">目标元素的 A11yElement.UniqueId（仅用于日志，监听器自身用 Guid 标识）</param>
    /// <param name="scope">监听范围（Element/Subtree/Descendants）</param>
    /// <param name="onEvent">事件回调（每条 UIA 事件触发时调用，可能在后台线程）</param>
    public EventListenerCore(int targetElementId, EventListenScope scope = EventListenScope.Descendants, Action<UiaEventRecord>? onEvent = null)
    {
        _targetElementId = targetElementId;
        _scope = scope;
        _onEvent = onEvent;
    }

    /// <summary>开始监听 UIA 事件。必须在 STA 线程调用。</summary>
    /// <param name="eventIds">要监听的事件 ID 列表（null=使用 DefaultEventIds）</param>
    /// <param name="propertyIds">要监听的属性 ID 列表（null=使用 DefaultPropertyIds）</param>
    public void Start(IEnumerable<int>? eventIds = null, IEnumerable<int>? propertyIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        if (_listenerId.HasValue) throw new InvalidOperationException("已在监听中，请先 Stop");

        var ids = (eventIds ?? DefaultEventIds).ToList();
        var pids = (propertyIds ?? DefaultPropertyIds).ToList();

        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}, propCount={pids.Count}");

        // 将 EventListenScope 转为 Axe.Windows 的 ListenScope
        var axScope = _scope switch
        {
            EventListenScope.Element => ListenScope.Element,
            EventListenScope.Subtree => ListenScope.Subtree,
            EventListenScope.Descendants => ListenScope.Descendants,
            _ => ListenScope.Descendants
        };

        try
        {
            _listenerId = Guid.NewGuid();
            
            // ★ ListenAction.CreateInstance 内部依赖 DesktopElementContext.Current.GetDesktopElement()
            //   若未初始化会抛 NullReferenceException
            //   根因：MainWindow 启动时未调用 Axe.Windows 的桌面元素初始化
            //   修复：在 MainWindow 构造函数中调用 InitDesktopElementContext() 即可解决
            try
            {
                ListenAction.CreateInstance(axScope, _listenerId.Value, OnEventMessage);
                ListenAction.GetInstance(_listenerId.Value).Start(ids, pids);
                AppLog.Write("RECORD", $"EventListenerCore.Start → OK: listenerId={_listenerId}");
            }
            catch (NullReferenceException ex)
            {
                AppLog.Write("RECORD", $"EventListenerCore.Start → FATAL: DesktopElementContext not initialized!");
                AppLog.Write("RECORD", $"  Fix: Call EventListenerCore.InitDesktopElementContext() in MainWindow constructor");
                _listenerId = null; // 清理无效状态
                throw;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Start → EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _listenerId = null;
            throw;
        }
    }

    /// <summary>停止监听并释放资源。</summary>
    public void Stop()
    {
        if (!_listenerId.HasValue || _isDisposed) return;

        try
        {
            var la = ListenAction.GetInstance(_listenerId.Value);
            la.Stop();
            ListenAction.ReleaseInstance(_listenerId.Value);
            AppLog.Write("RECORD", $"EventListenerCore.Stop: listenerId={_listenerId}");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Stop → exception (ignored): {ex.Message}");
        }

        _listenerId = null;
    }

    /// <summary>ListenAction 事件回调：将 EventMessage 转换为 UiaEventRecord 并通知上层。</summary>
    private void OnEventMessage(EventMessage message)
    {
        try
        {
            var eventName = EventNames.TryGetValue(message.EventId, out var name) ? name : $"Event_{message.EventId}";
            var elem = message.Element;

            var record = new UiaEventRecord
            {
                Timestamp = DateTime.TryParse(message.TimeStamp, out var ts) ? ts : DateTime.Now,
                EventId = message.EventId,
                EventName = eventName,
                ElementUniqueId = elem?.UniqueId ?? 0,
                ElementName = elem?.Name ?? "",
                ControlType = elem?.ControlTypeId > 0 ? elem.ControlTypeId.ToString() : "",
                ChangedProperties = message.Properties?.Cast<object>().ToList(),
            };

            AppLog.Write("RECORD", $"OnEventMessage: {eventName} elem={elem?.Name ?? "(null)"} id={elem?.UniqueId ?? -1}");
            _onEvent?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"OnEventMessage → exception (ignored): {ex.Message}");
        }
    }

    /// <summary>初始化 DesktopElementContext（必须在 STA 线程调用，通常在 MainWindow 构造函数中调用一次即可）。</summary>
    /// <remarks>
    /// ListenAction.CreateInstance 内部依赖此上下文获取桌面元素。
    /// 若未初始化，Start() 会抛 NullReferenceException。
    /// 
    /// 此方法需要在 App 层（MainWindow）中调用，因为需要 WPF/UIA 引用。
    /// </remarks>
    public static void InitDesktopElementContext()
    {
        // ★ 此方法的实现已移至 MainWindow.InitDesktopElementContext()
        //   因为 UiTreeMonitor.Core 是纯类库项目，无法引用 System.Windows.Automation
        //   实际初始化逻辑见 MainWindow.xaml.cs 中的同名方法
        AppLog.Write("RECORD", "InitDesktopElementContext: stub (see MainWindow implementation)");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
