// UIA 事件自动监听引擎（COM 直连版）
// ★ 设计决策演进：
//   v1 WPF System.Windows.Automation.AddAutomationEventHandler 挂 RootElement+Descendants
//      —— 实测跨进程事件一条都收不到（seen=0，而同管线 AddAutomationFocusChangedEvent 可收，
//        证明管道线程/消息泵正常，是 WPF 客户端包装挂元素注册的问题）；
//   v2 改用 Interop.UIAutomationClient COM 直连（IUIAutomation.AddAutomationEventHandler），
//      与 UiaSession / 官方 accessibility-insights（axe-windows EventListenerFactory）同款路径。
// 挂载元素三档（注册耗时与覆盖范围权衡）：
//   ① 目标窗口句柄 hwnd → ElementFromHandle（精准 ~0.1s）
//   ② 悬停元素 ElementContext → 沿 RawViewWalker 爬到顶层窗口（~0.5s，覆盖整窗）
//   ③ 桌面根 GetRootElement + Descendants（free mode 兜底，跨进程握手较慢；
//      悬停目标后经 RecordingPanel.TryAttachAutoListener 升级/就地切换 pid 过滤）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Axe.Windows.Actions;
using Axe.Windows.Core.Bases;
using UIAutomationClient;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;

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
/// UIA 事件自动监听器（COM 直连：Interop.UIAutomationClient IUIAutomation）。
/// 线程要求：注册/注销在专用 STA 管道线程执行（UIA COM 要求注册线程常驻+消息泵）。
/// </summary>
public sealed class EventListenerCore : IDisposable
{
    private bool _isDisposed;
    private bool _isListening;
    private readonly int _targetElementId;
    private readonly EventListenScope _scope;
    private readonly Action<UiaEventRecord>? _onEvent;
    private readonly Action<bool>? _onStartCompleted; // 注册完成回调（管道线程调用；通知 UI "监听就绪可操作"）
    private readonly IntPtr _targetWindowHandle;      // 目标窗口句柄：≠0 时挂该窗口子树（挂载档①）
    private readonly Guid? _hoverElementContextId;    // 悬停元素 ElementContext id：爬顶层窗口挂载（挂载档②）
    private volatile int _targetProcessId;            // 目标进程：≠0 时回调按进程过滤；volatile 支持 UpdateTargetProcess 就地切换
    private readonly int _ownProcessId = Environment.ProcessId;

    // COM 直连对象（管道 STA 线程创建；注销必须与注册同一元素同一 handler 配对）
    private CUIAutomation? _comRoot;
    private IUIAutomation? _uia;
    private IUIAutomationElement? _hookElement;
    private int _hookScopeKind;                       // 0=未挂 7=Subtree 4=Descendants（仅日志）
    private bool _propertiesRegistered;
    private readonly List<int> _registeredEventIds = new();
    private UiaAutomationEventHandler? _eventHandler;
    private UiaPropertyChangeHandler? _propertyHandler;

    private volatile bool _detached;              // Dispose 后立即停止转发（后台注销最长数秒，期间不再重复记事件）
    private volatile bool _stopRequested;         // Stop/Dispose 请求：DoStart 注册循环每步检查立即中断

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
    private long _skippedNull;   // 被跳过：属性变化 newValue=null（provider 不填值，无回放意义）
    private int _lastFilteredPid;

    /// <summary>是否正在监听中。</summary>
    public bool IsListening => _isListening;

    /// <summary>是否桌面根挂载（free mode 起步）：true 时可用 UpdateTargetProcess 就地切换过滤目标（不重启）。</summary>
    public bool IsDesktopScope => _targetWindowHandle == IntPtr.Zero && _hoverElementContextId is null;

    // ==== 语义映射：UIA 事件/属性变化 → 可回放 Pattern 操作（Pattern 名与 Axe.Windows A11yPattern.Name 对齐） ====
    // ★ 事件/属性 ID 用标准 UIA 数字常量（Interop 枚举 UIA_EventIds/UIA_PropertyIds 在 NoPIA 嵌入模式下不可用 CS1752）
    private static readonly (int EventId, string Name, string Pattern, string Method)[] SemanticEvents =
    {
        (20009, "Invoke Invoked", "InvokePattern", "Invoke"),                             // UIA_Invoke_InvokedEventId 按钮点击
        (20012, "Element Selected", "SelectionItemPattern", "Select"),                    // UIA_SelectionItem_ElementSelectedEventId 选中（单选/下拉）
        (20010, "Added To Selection", "SelectionItemPattern", "AddToSelection"),          // UIA_SelectionItem_ElementAddedToSelectionEventId
        (20011, "Removed From Selection", "SelectionItemPattern", "RemoveFromSelection"), // UIA_SelectionItem_ElementRemovedFromSelectionEventId
    };

    // 属性变化类（AddPropertyChangedEventHandler，含输入文字/滑块拖动等值变化）
    //  ★ 连续变化（逐字符输入、拖动过程）ReplacePrevious=true：脚本中只保留最终值，回放 SetValue 幂等
    private static readonly (int PropertyId, string Pattern, string Method)[] SemanticProperties =
    {
        (30045, "ValuePattern", "SetValue"),                 // UIA_ValueValuePropertyId 输入框文字
        (30036, "RangeValuePattern", "SetValue"),            // UIA_RangeValueValuePropertyId 滑块/进度拖动
        (30016, "TogglePattern", "Toggle"),                  // UIA_ToggleToggleStatePropertyId 复选/开关
        (30010, "ExpandCollapsePattern", "Expand/Collapse"), // UIA_ExpandCollapseExpandCollapseStatePropertyId 展开/折叠
    };

    /// <summary>语义模式事件注册集：仅 4 个可回放事件（+属性变化，DoStart 固定注册）。</summary>
    public static readonly IReadOnlyList<int> SemanticEventIds = SemanticEvents
        .Select(e => e.EventId)
        .ToList();

    /// <summary>语义模式属性注册集。</summary>
    public static readonly IReadOnlyList<int> SemanticPropertyIds = SemanticProperties
        .Select(p => p.PropertyId)
        .ToList();

    /// <summary>创建事件监听器。targetWindowHandle（挂载①）/hoverElementContextId（挂载②）来自当前目标或悬停元素，
    /// targetProcessId 用于回调过滤（桌面根挂载时是唯一限定手段）。</summary>
    public EventListenerCore(int targetElementId, EventListenScope scope = EventListenScope.Descendants, Action<UiaEventRecord>? onEvent = null,
        IntPtr targetWindowHandle = default, int targetProcessId = 0, Action<bool>? onStartCompleted = null,
        Guid? hoverElementContextId = null)
    {
        _targetElementId = targetElementId;
        _scope = scope;
        _onEvent = onEvent;
        _targetWindowHandle = targetWindowHandle;
        _targetProcessId = targetProcessId;
        _onStartCompleted = onStartCompleted;
        _hoverElementContextId = hoverElementContextId;
    }

    /// <summary>
    /// 开始监听（异步）：注册派发到专用 STA 管道线程执行，完成与否看日志 "Start → OK" 或 onStartCompleted 回调。
    /// </summary>
    public void Start(IEnumerable<int>? eventIds = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(EventListenerCore));
        var ids = new HashSet<int>(eventIds ?? SemanticEventIds);
        _stopRequested = false; // 新一轮注册（Stop→Start 复用同实例时复位中断标志）
        EnsurePipe().BeginInvoke((Action)(() => DoStart(ids)));
    }

    /// <summary>停止监听（异步）：置中断标志让注册循环立即跳出，注销在管道线程执行。</summary>
    public void Stop()
    {
        _stopRequested = true;
        var d = _pipeDispatcher;
        if (d is null) { _isListening = false; return; }
        d.BeginInvoke((Action)DoStop);
    }

    /// <summary>★ 就地切换进程过滤目标——不重启监听（零真空期；pid 过滤在回调层做，改字段即生效）。</summary>
    public void UpdateTargetProcess(int pid)
    {
        var old = _targetProcessId;
        if (old == pid) return;
        _targetProcessId = pid;
        AppLog.Write("RECORD", $"EventListenerCore.UpdateTargetProcess: {old} → {pid} (in-place, listener stays)");
    }

    /// <summary>确保 UIA 专用 STA 管道线程就绪（常驻 + Dispatcher 消息泵，COM 回调要求）。</summary>
    private Dispatcher EnsurePipe()
    {
        if (_pipeDispatcher is not null) return _pipeDispatcher;
        var ready = new ManualResetEventSlim();
        _pipe = new Thread(() =>
        {
            _pipeDispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run(); // 消息泵：UIA COM 在注册线程上回调，线程必须常驻
        })
        { IsBackground = true, Name = "UIAEventPipe" };
        _pipe.SetApartmentState(ApartmentState.STA);
        _pipe.Start();
        ready.Wait(2000);
        return _pipeDispatcher!;
    }

    // TreeScope 枚举值（interop 生成名带前缀，直接用数值强转避免成员名差异）
    private const int ScopeSubtree = 7;      // Self + Descendants（挂具体窗口）
    private const int ScopeDescendants = 4;  // 仅后代（挂桌面根：根自身无意义）

    /// <summary>实际注册（管道线程执行）。_stopRequested 每步检查立即中止。</summary>
    private void DoStart(HashSet<int> ids)
    {
        if (_isDisposed || _stopRequested) return;
        var sw = Stopwatch.StartNew();
        AppLog.Write("RECORD", $"EventListenerCore.Start: scope={_scope}, targetElementId={_targetElementId}, eventCount={ids.Count}, hwnd={_targetWindowHandle}, pid={_targetProcessId}, hoverEc={_hoverElementContextId}");

        try
        {
            _comRoot = new CUIAutomation();
            _uia = (IUIAutomation)_comRoot;

            // ★ 挂载元素三档解析（见文件头注释）
            var hook = ResolveMountElement(_uia, out var mountDesc);
            if (hook is null)
            {
                _isListening = false;
                AppLog.Write("RECORD", "EventListenerCore.Start → FAILED (no mount element resolved)");
                _onStartCompleted?.Invoke(false);
                return;
            }
            var desktopMount = IsDesktopScope; // 兜底桌面根（ResolveMountElement 返回 root）
            var treeScope = (TreeScope)(desktopMount ? ScopeDescendants : ScopeSubtree);
            _hookElement = hook;
            _hookScopeKind = desktopMount ? ScopeDescendants : ScopeSubtree;

            int registered = 0, failed = 0;

            // ★ 属性变化最先注册：一次调用覆盖 4 属性（输入文字/滑块拖动/开关/展开折叠），价值密度最高
            if (!_stopRequested)
            {
                try
                {
                    _propertyHandler = new UiaPropertyChangeHandler(OnUiaPropertyChanged);
                    _uia.AddPropertyChangedEventHandler(hook, treeScope, null, _propertyHandler,
                        SemanticPropertyIds.ToArray());
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

            foreach (var id in ids)
            {
                if (_stopRequested) { AppLog.Write("RECORD", "  Start aborted (stop requested during register)"); break; }
                try
                {
                    _eventHandler ??= new UiaAutomationEventHandler(OnUiaEvent);
                    _uia.AddAutomationEventHandler(id, hook, treeScope, null, _eventHandler);
                    _registeredEventIds.Add(id);
                    registered++;
                    var name = SemanticEvents.FirstOrDefault(s => s.EventId == id).Name;
                    AppLog.Write("RECORD", $"  Registered: {name ?? $"Event_{id}"} (ID={id})");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write("RECORD", $"  Register FAILED (skipped): Event {id}: {ex.Message}");
                }
            }

            bool ok = registered > 0 && !_stopRequested;
            _isListening = ok;
            AppLog.Write("RECORD", $"EventListenerCore.Start → {(_stopRequested ? "ABORTED (stop requested)" : ok ? "OK" : "FAILED")} (mount={mountDesc}, scope={_hookScopeKind}, {registered} registered, {failed} skipped, {sw.ElapsedMilliseconds}ms)");
            _onStartCompleted?.Invoke(ok);
        }
        catch (Exception ex)
        {
            _isListening = false;
            AppLog.Write("RECORD", $"EventListenerCore.Start → EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _onStartCompleted?.Invoke(false);
        }
    }

    /// <summary>解析挂载元素：①hwnd ②悬停 ElementContext 爬顶层窗口 ②b pid 找进程顶层窗口 ③桌面根兜底。
    /// 返回 null 仅当 COM 初始化异常。②b 是关键降级档：MouseCapture 轮询 120ms 就替换悬停 POI 的 ec，
    /// TryAttach 传来的 ecId 到注册时经常已失效——此时退到桌面根会注册 5s+洪水，按 pid 找顶层窗口与 hwnd 档同快。</summary>
    private IUIAutomationElement? ResolveMountElement(IUIAutomation uia, out string mountDesc)
    {
        // ① 目标窗口句柄（抓过树的目标）
        if (_targetWindowHandle != IntPtr.Zero)
        {
            try
            {
                var el = uia.ElementFromHandle(_targetWindowHandle);
                if (el is not null)
                {
                    mountDesc = $"hwnd {_targetWindowHandle} → Subtree";
                    return el;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("RECORD", $"  ElementFromHandle({_targetWindowHandle}) failed: {ex.Message}");
            }
        }

        // ② 悬停元素 → 爬到顶层窗口（覆盖整窗；POI 若已被替换/失效则走兜底）
        if (_hoverElementContextId is Guid ecId)
        {
            try
            {
                var a11y = GetDataAction.GetElementContext(ecId)?.Element;
                if (a11y?.PlatformObject is IUIAutomationElement poi)
                {
                    var top = ClimbToTopLevelWindow(uia, poi, out var climbed);
                    if (top is not null)
                    {
                        mountDesc = climbed
                            ? $"hover poi → top-level window \"{SafeName(uia, top)}\" → Subtree"
                            : $"hover poi (already top-level) \"{SafeName(uia, top)}\" → Subtree";
                        return top;
                    }
                }
                else
                {
                    AppLog.Write("RECORD", $"  hover ElementContext {ecId} unavailable (replaced/disposed) → try pid window");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("RECORD", $"  resolve hover element failed ({ecId}) → try pid window: {ex.Message}");
            }
        }

        // ②b 有目标 pid → 桌面根 Children 中找该进程顶层窗口（注册与 hwnd 档同快；避免桌面根 5s+洪水）
        if (_targetProcessId != 0)
        {
            try
            {
                var cond = uia.CreatePropertyCondition(30002, _targetProcessId); // UIA_ProcessIdPropertyId
                var win = uia.GetRootElement().FindFirst((TreeScope)5, cond);    // 5=Children（仅顶层窗口层）
                if (win is not null)
                {
                    mountDesc = $"pid {_targetProcessId} top window \"{SafeName(uia, win)}\" → Subtree";
                    return win;
                }
                AppLog.Write("RECORD", $"  no top-level window for pid {_targetProcessId} → desktop fallback");
            }
            catch (Exception ex)
            {
                AppLog.Write("RECORD", $"  find process window (pid={_targetProcessId}) failed → desktop fallback: {ex.Message}");
            }
        }

        // ③ 桌面根兜底（free mode；注册较慢，悬停后由 RecordingPanel 就地切换 pid 过滤或重挂）
        mountDesc = "desktop root → Descendants (free mode)";
        return uia.GetRootElement();
    }

    /// <summary>沿 RawViewWalker 向上爬到顶层窗口元素（父为桌面根/无父时停）。上限 16 层防异常深树。
    /// 判定"父为桌面根"用 RuntimeId 数组对比（元素唯一标识；COM 包装对象每次返回不同实例，不能比引用）。</summary>
    private static IUIAutomationElement? ClimbToTopLevelWindow(IUIAutomation uia, IUIAutomationElement start, out bool climbed)
    {
        climbed = false;
        IUIAutomationElement root, cur = start;
        int[] rootRid;
        try
        {
            root = uia.GetRootElement();
            rootRid = root.GetRuntimeId();
        }
        catch { return start; }
        for (int i = 0; i < 16; i++)
        {
            IUIAutomationElement? parent;
            try { parent = uia.RawViewWalker.GetParentElement(cur); }
            catch { break; }
            if (parent is null) break; // 无父：cur 已是顶层
            bool isRoot = false;
            try
            {
                var pRid = parent.GetRuntimeId();
                isRoot = rootRid.Length == pRid.Length && rootRid.SequenceEqual(pRid);
            }
            catch { /* RuntimeId 读取失败按非根处理 */ }
            if (isRoot) break; // 父为桌面根：cur 是顶层窗口
            cur = parent;
            climbed = true;
        }
        return cur;
    }

    private static string SafeName(IUIAutomation uia, IUIAutomationElement el)
    {
        try { var n = el.CurrentName; return string.IsNullOrEmpty(n) ? "(no name)" : Trunc(n, 40); }
        catch { return "(unreadable)"; }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>实际注销（管道线程执行）。必须与注册同一元素、同一 handler 配对，否则泄漏。</summary>
    private void DoStop()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var hook = _hookElement;
            if (hook is not null && _uia is not null)
            {
                if (_eventHandler is not null)
                {
                    foreach (var id in _registeredEventIds)
                    {
                        try { _uia.RemoveAutomationEventHandler(id, hook, _eventHandler); }
                        catch { /* 忽略移除失败 */ }
                    }
                    _registeredEventIds.Clear();
                }
                if (_propertiesRegistered && _propertyHandler is not null)
                {
                    try { _uia.RemovePropertyChangedEventHandler(hook, _propertyHandler); }
                    catch { /* 忽略移除失败 */ }
                    _propertiesRegistered = false;
                }
            }

            AppLog.Write("RECORD", $"EventListenerCore.Stop → OK (seen={_seen}, recorded={_recorded}, filteredOwn={_filteredOwn}, filteredOther={_filteredOther}, skippedNull={_skippedNull}, lastFilteredPid={_lastFilteredPid}, {sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"EventListenerCore.Stop → exception (ignored): {ex.Message}");
        }

        _isListening = false;
        _hookElement = null;
        _hookScopeKind = 0;
        _eventHandler = null;
        _propertyHandler = null;
        _uia = null;
        if (_comRoot is not null) { try { Marshal.ReleaseComObject(_comRoot); } catch { /* 已释放 */ } _comRoot = null; }
    }

    // ==== COM 回调（管道线程；异常必须就地吞掉，绝不能向 COM 边界抛出） ====

    /// <summary>普通事件回调：命中语义表则产出可回放记录。</summary>
    private void OnUiaEvent(IUIAutomationElement sender, int eventId)
    {
        var semantic = SemanticEvents.FirstOrDefault(s => s.EventId == eventId);
        if (semantic != default)
            HandleEvent(sender, eventId, semantic.Name, semantic.Pattern, semantic.Method, null, replacePrevious: false);
    }

    /// <summary>属性变化回调：输入文字/滑块拖动/开关/展开折叠 → 可回放 SetValue/Toggle/Expand。</summary>
    private void OnUiaPropertyChanged(IUIAutomationElement sender, int propertyId, object newValue)
    {
        switch (propertyId)
        {
            case 30045: // UIA_ValueValuePropertyId
                // ★ 部分应用 provider 不填新值（VT_NULL）：SetValue(null) 无回放意义，跳过防脏记录
                if (newValue is null) { _skippedNull++; return; }
                HandleEvent(sender, propertyId, "Value Changed", "ValuePattern", "SetValue", new object?[] { newValue }, replacePrevious: true);
                break;
            case 30036: // UIA_RangeValueValuePropertyId
                if (newValue is null) { _skippedNull++; return; }
                HandleEvent(sender, propertyId, "RangeValue Changed", "RangeValuePattern", "SetValue", new object?[] { newValue }, replacePrevious: true);
                break;
            case 30016: // UIA_ToggleToggleStatePropertyId
                HandleEvent(sender, propertyId, "Toggle State Changed", "TogglePattern", "Toggle");
                break;
            case 30010: // UIA_ExpandCollapseExpandCollapseStatePropertyId
                var expanded = false;
                try { expanded = Convert.ToInt32(newValue) == 1; } // ExpandedCollapseState: 0=Collapsed 1=Expanded 2=Partial
                catch { /* 非数值忽略倾向 */ }
                HandleEvent(sender, propertyId, "ExpandCollapse Changed", "ExpandCollapsePattern", expanded ? "Expand" : "Collapse");
                break;
        }
    }

    /// <summary>公共处理：进程过滤 → 读取元素信息 → 转发 UiaEventRecord（带语义映射时可回放）。</summary>
    private void HandleEvent(IUIAutomationElement elem, int eventId, string eventName,
        string? pattern = null, string? method = null, object?[]? parameters = null, bool replacePrevious = false)
    {
        try
        {
            if (_detached) return; // 已弃用：后台注销完成前不再转发
            _seen++;

            // ★ 进程过滤：绝不录自己（录制列表每刷一次就触发 UIA 事件，又被录下 → 自我反馈死循环）；
            //   桌面根挂载时按目标 pid 过滤；窗口级挂载天然限定，此处为防御
            int pid;
            try { pid = elem.CurrentProcessId; }
            catch { return; /* 元素已销毁 */ }
            if (pid == _ownProcessId) { _filteredOwn++; LogFilterStats(); return; }
            if (_targetProcessId != 0 && pid != _targetProcessId) { _filteredOther++; _lastFilteredPid = pid; LogFilterStats(); return; }

            // 读取元素信息（元素可能已销毁，逐项防御）
            string elemName = "", autoId = "", className = "", typeName = "", rectStr = "";
            int ctrlTypeId = 0;
            try
            {
                elemName = elem.CurrentName ?? "";
                autoId = elem.CurrentAutomationId ?? "";
                className = elem.CurrentClassName ?? "";
                ctrlTypeId = elem.CurrentControlType;
                var r = elem.CurrentBoundingRectangle;
                if (r.right > r.left && r.bottom > r.top)
                    rectStr = $"{r.left},{r.top},{r.right},{r.bottom}";
            }
            catch { /* 元素可能已销毁 */ }
            try { typeName = TreeSampler.ControlTypeName(ctrlTypeId); } catch { /* 未知类型 */ }

            var record = new UiaEventRecord
            {
                Timestamp = DateTime.Now,
                EventId = eventId,
                EventName = eventName,
                ElementUniqueId = 0,
                ElementName = elemName,
                ControlType = typeName,
                ElementPath = pattern is null
                    ? null
                    : $"spec:v1|autoId={Esc(autoId)}|name={Esc(elemName)}|type={typeName}|class={Esc(className)}|rect={rectStr}",
                ChangedProperties = null,
                PlaybackPattern = pattern,
                PlaybackMethod = method,
                PlaybackParameters = parameters,
                ReplacePrevious = replacePrevious
            };

            AppLog.Write("RECORD", $"OnAutomationEvent: {eventName} elem=\"{Trunc(elemName, 40)}\" type={typeName} pid={pid}" +
                (pattern is not null ? $" → {pattern}.{method}({string.Join(", ", parameters ?? Array.Empty<object?>())}){(replacePrevious ? " [merge]" : "")}" : ""));
            _recorded++;
            _onEvent?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Write("RECORD", $"OnAutomationEvent → exception (ignored): {ex.Message}");
        }
    }

    private static string? Esc(string? s) => string.IsNullOrEmpty(s) ? "" : Uri.EscapeDataString(s);

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
        _detached = true;       // 立即停止事件转发（注销在后台慢慢完成）
        _stopRequested = true;  // 注册循环立即中断
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

/// <summary>COM 事件回调包装（IUIAutomationEventHandler 实现转普通委托；异常不得穿越 COM 边界）。</summary>
internal sealed class UiaAutomationEventHandler : IUIAutomationEventHandler
{
    private readonly Action<IUIAutomationElement, int> _onEvent;
    public UiaAutomationEventHandler(Action<IUIAutomationElement, int> onEvent) => _onEvent = onEvent;
    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId)
    {
        try { _onEvent(sender, eventId); }
        catch { /* 吞掉：COM 回调抛异常会中断 UIA 分发 */ }
    }
}

/// <summary>COM 属性变化回调包装（IUIAutomationPropertyChangedEventHandler）。</summary>
internal sealed class UiaPropertyChangeHandler : IUIAutomationPropertyChangedEventHandler
{
    private readonly Action<IUIAutomationElement, int, object> _onPropertyChanged;
    public UiaPropertyChangeHandler(Action<IUIAutomationElement, int, object> onPropertyChanged) => _onPropertyChanged = onPropertyChanged;
    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue)
    {
        try { _onPropertyChanged(sender, propertyId, newValue); }
        catch { /* 吞掉：COM 回调抛异常会中断 UIA 分发 */ }
    }
}
