using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccessibilityInsights.SharedUx.Enums;
using AccessibilityInsights.SharedUx.Highlighting;
using Axe.Windows.Actions;
using UiTreeMonitor.Core.Filters;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Sampler;
using UiTreeMonitor.Core.Targeting;
using UiTreeMonitor.Core.Tasks;
using UiTreeMonitor.Core.Uia;
using UiTreeMonitor.Export;
using UiTreeMonitor.Persistence;
using UiTreeMonitor.Scheduler;
using TaskScheduler = UiTreeMonitor.Scheduler.TaskScheduler;

namespace UiTreeMonitor.App;

/// <summary>任务列表项 VM。</summary>
public sealed class TaskListItem : INotifyPropertyChanged
{
    public required MonitorTaskConfig Config { get; init; }
    public string Name => Config.Name;

    private string _state = "Idle";
    public string State
    {
        get => _state;
        set { _state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); }
    }

    private int _sampleCount;
    public int SampleCount
    {
        get => _sampleCount;
        set { _sampleCount = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SampleCount))); }
    }

    public string TargetText => Config.Target.ToString() ?? "";
    
    /// <summary>监控目标属性名（如 ValuePattern.Value、Name 等）</summary>
    public string MonitorProperty { get; init; } = "";
    
    /// <summary>目标进程名</summary>
    public string TargetProcessName { get; init; } = "";
    
    /// <summary>采集间隔（毫秒）</summary>
    public int IntervalMs { get; init; } = 1000;
    
    /// <summary>监控时长（秒），0=无限</summary>
    public int DurationSec { get; init; } = 0;
    
    /// <summary>当前采样的实时值（定时刷新显示）</summary>
    private string _currentValue = "-";
    public string CurrentValue 
    { 
        get => _currentValue; 
        set { _currentValue = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue))); }
    }
    
    /// <summary>上次采样时间</summary>
    private string _lastSampleTime = "";
    public string LastSampleTime
    {
        get => _lastSampleTime;
        set { _lastSampleTime = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastSampleTime))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TreeNodeViewModel> _treeRoots = new();
    private readonly ObservableCollection<TaskListItem> _taskItems = new();
    private readonly ConcurrentQueue<MouseCaptureResult> _captureQueue = new();
    private readonly TaskScheduler _scheduler = new(workerExe: null);
    private readonly TaskConfigStore _configStore;
    private readonly SampleBatchSaver _saver;      // 批量落盘器（内存缓冲+后台写盘，不影响采集速率）
    private readonly ReportExporter _exporter;
    private readonly MouseCapture _mouseCapture = new(pollMs: 120);

    private TreeSnapshot? _currentTree;
    private Dictionary<int, TreeNodeViewModel> _nodeLookup = new();
    private TargetWindowInfo? _currentTarget;
    private DispatcherTimer? _drainTimer;
    private bool _capturing;
    private MouseCaptureResult? _lastHoverResult; // 当前悬停选中的元素（烧杯按钮回调用）
    private TreeNodeViewModel? _selectedNode; // 当前树选中节点（Details 面板联动）
    private readonly DispatcherTimer? _historyTimer; // 采样历史页激活期间自动刷新
    private object? _rightClickedItem; // 右键点击的属性/Pattern 项（ContextMenu 回调用）
    private bool _detailsLocked; // 烧杯按钮点击后锁定 Details 不再刷新
    // Details 面板当前显示元素的定位约束（右键添加监控时作目标元素，两条路径都会更新：
    // 树选中节点 / 鼠标捕获悬停元素；_selectedNode 为 null 时兜底用它）
    private string? _detailsElemName;
    private string? _detailsElemControlType;
    private string? _detailsElemAutomationId;

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ui-tree-monitor");
    private static readonly string TaskConfigDir = Path.Combine(AppDataDir, "tasks");
    private static readonly string SettingsFilePath = Path.Combine(AppDataDir, "settings.json");

    /// <summary>全局设置（截取字符等），构造时加载、设置弹窗保存。</summary>
    private AppSettings _settings = new();

    /// <summary>数据保存根目录（需求：默认 D:\uitreemonitor，不存在则新建；实际数据写入 当日日期 子目录）。</summary>
    private static readonly string DataRootDir = @"D:\uitreemonitor";

    /// <summary>当日数据子目录（启动时创建；跨天后由 saver/导出器按当天日期滚动）。</summary>
    private static string TodayDataDir => Path.Combine(DataRootDir, DateTime.Now.ToString("yyyyMMdd"));

    // ========== 日志：写到 E:\ui-tree-monitor\monitor.log ==========
    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(@"E:\ui-tree-monitor\monitor.log", $"{DateTime.Now:HH:mm:ss.fff} {msg}{System.Environment.NewLine}"); } catch { }
}

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(SettingsFilePath);
        _configStore = new TaskConfigStore(TaskConfigDir);
        _saver = new SampleBatchSaver(DataRootDir);
        _saver.Start();
        _exporter = new ReportExporter(DataRootDir);
        Directory.CreateDirectory(TodayDataDir); // 启动即创建当日数据目录

        TreeView.ItemsSource = _treeRoots;
        TaskList.ItemsSource = _taskItems;

        RefreshWindows();
        LoadSavedTasks();

        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _drainTimer.Tick += (_, _) => DrainCaptureQueue();
        _drainTimer.Start();

        // 采样历史页：激活期间自动刷新（选中任务的记录实时追加可见）
        _historyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _historyTimer.Tick += (_, _) => RefreshHistory();
        Closed += (_, _) =>
        {
            _mouseCapture.Stop();
            HollowHighlightDriver.ClearAllHighlighters(); // 原版退出时清理全部高亮器
            _scheduler.Dispose();
            _saver.Dispose(); // 退出前写出全部剩余采样数据
        };
    }

    // ========== 窗口 ==========
    private void RefreshWindows()
    {
        var windows = WindowEnumerator.EnumerateWindows();
        WindowCombo.ItemsSource = windows;
        WindowCombo.DisplayMemberPath = null;
        WindowCombo.SelectedIndex = windows.Count > 0 ? 0 : -1;
    }

    private void BtnRefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    // ========== 抓树 ==========
    private void BtnCaptureTree_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not TargetWindow w)
        {
            TxtStatus.Text = "请先选择目标窗口";
            return;
        }
        CaptureTree(new TargetWindowInfo(w.ProcessId, w.Handle, w.ProcessName, w.Title));
    }

    private void CaptureTree(TargetWindowInfo target)
    {
        TxtStatus.Text = $"正在抓取 {target.ProcessName} 的结构树…";
        _currentTarget = target;

        var thread = new Thread(() =>
        {
            TreeSnapshot? snap = null;
            try
            {
                using var session = UiaSession.Create();
                snap = new TreeSampler().CaptureWindow(session, target);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => TxtStatus.Text = $"抓取失败: {ex.Message}");
                return;
            }
            Dispatcher.Invoke(() => ApplyTree(snap));
        })
        { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    private void ApplyTree(TreeSnapshot? snap)
    {
        if (snap is null || snap.NodeCount == 0)
        {
            TxtStatus.Text = "未获取到结构树（窗口可能不支持 UIA）";
            return;
        }
        _currentTree = snap;
        _nodeLookup = new Dictionary<int, TreeNodeViewModel>();

        // 构建树（只展开前两层）
        _treeRoots.Clear();
        var procName = snap.Target?.ProcessName;
        var root = snap.Nodes[snap.RootId];
        var rootVm = TreeNodeViewModel.FromNode(root, procName);
        _nodeLookup[rootVm.Id] = rootVm;
        _treeRoots.Add(rootVm);

        var stack = new Stack<(ElementNode, TreeNodeViewModel)>();
        stack.Push((root, rootVm));
        while (stack.Count > 0)
        {
            var (node, vm) = stack.Pop();
            foreach (var childId in node.Children)
            {
                if (!snap.Nodes.TryGetValue(childId, out var cn)) continue;
                var cvm = TreeNodeViewModel.FromNode(cn, procName);
                _nodeLookup[cvm.Id] = cvm;
                vm.Children.Add(cvm);
                stack.Push((cn, cvm));
            }
        }
        // 默认展开前两层
        TreeView.UpdateLayout();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            ExpandRoots(2);
            TxtStatus.Text = $"结构树已加载：{snap.NodeCount} 个节点（{snap.Target?.ProcessName}）";
        }));
    }

    private void ExpandRoots(int depth)
    {
        foreach (var root in TreeView.Items.OfType<TreeViewItem>())
            ExpandToDepth(root, depth);
    }

    private static void ExpandToDepth(TreeViewItem? item, int depth)
    {
        if (item is null || depth <= 0) return;
        item.IsExpanded = true;
        item.UpdateLayout();
        foreach (var child in item.Items.OfType<TreeViewItem>())
            ExpandToDepth(child, depth - 1);
    }

    // ========== 鼠标捕获 ==========
    private void BtnMouseCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            StopMouseCapture();
            return;
        }

        _capturing = true;
        _detailsLocked = false; // 开始捕获时解锁 Details 跟随刷新
        // 原版 HollowHighlightDriver：默认实例自带烧杯按钮（Highlighter 构造 hasSnapshot:true）
        var driver = HollowHighlightDriver.GetDefaultInstance();
        driver.HighlighterMode = HighlighterMode.HighlighterBeakerTooltip; // 原版 ConfigurationModel 默认模式：边框+烧杯+文字提示
        driver.SetCallBackForSnapshot(new Action(OnSnapshotAddTarget));    // 原版 Initialize.cs:165 同款接线
        driver.IsEnabled = true;

        _mouseCapture.Start();
        _mouseCapture.ResultProduced += OnCaptureResult;
        BtnMouseCapture.Content = "停止捕获";
        TxtCaptureStatus.Text = "捕获中：悬停选中元素后，点蓝框右上角烧杯按钮将其添加为监测目标…";
    }

    private void StopMouseCapture()
    {
        _capturing = false;
        _mouseCapture.ResultProduced -= OnCaptureResult;
        _mouseCapture.Stop();
        HollowHighlightDriver.GetDefaultInstance().Clear();
        BtnMouseCapture.Content = "随鼠标捕获";
        TxtCaptureStatus.Text = "";
    }

    /// <summary>
    /// 烧杯按钮点击回调（原版 Win32SnapshotButton.Clicked，经 SetCallBackForSnapshot 接线，
    /// 在 UI 线程触发）：把当前悬停元素填入右侧"新建监控任务"表单作为监测目标，并退出随鼠标捕获。
    /// </summary>
    private void OnSnapshotAddTarget()
    {
        var result = _lastHoverResult;
        StopMouseCapture(); // 点击后退出随鼠标捕获
        _detailsLocked = true; // ★ 锁定 Details 不再刷新，用户可右键操作

        if (result is null || !result.IsValid)
        {
            TxtStatus.Text = "未选中有效元素，未添加监测目标";
            return;
        }

        // ★ 烧杯按钮也弹出"添加监控"对话框（与右键菜单统一体验）；传入当前值作提取规则示例
        var dlg = new AddMonitorDialog("Name", result.ProcessName ?? "", _settings.ExtractMarker, result.Name,
            nameExists: TaskNameExists, targetOptions: GetTargetOptionsFromDetails());
        dlg.Owner = this;
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        CreateMonitorTask(
            monitorProp: dlg.MonitorProperty,
            processName: dlg.ProcessName,
            intervalMs: dlg.IntervalMs,
            durationSec: dlg.DurationSec,
            taskName: dlg.TaskName,
            saveBatchPoints: dlg.SaveBatchPoints,
            saveIntervalSec: dlg.SaveIntervalSec,
            extractors: dlg.Extractors);
    }

    private void OnCaptureResult(MouseCaptureResult result)
    {
        _captureQueue.Enqueue(result);
    }

    private void DrainCaptureQueue()
    {
        var driver = HollowHighlightDriver.GetDefaultInstance();

        while (_captureQueue.TryDequeue(out var result))
        {
            // 原版高亮链路：HollowHighlightDriver.SetElement(A11yElement)
            // 内部完成 SetLocation(蓝框) + SetText(Glimpse + Focusable/NotFocusable) + 显隐控制
            if (!result.IsValid || result.ElementContextId is not Guid ecId
                || GetDataAction.GetElementContext(ecId)?.Element is not { } el)
            {
                driver.Clear();
                continue;
            }
            driver.SetElement(el); // el 归 DataManager/SelectAction 管理，严禁 Dispose（原版同款）
            _lastHoverResult = result; // 供烧杯按钮回调（OnSnapshotAddTarget）取当前悬停元素

            // ★ Details 跟随鼠标捕获刷新（未锁定时）
            if (!_detailsLocked)
            {
                var (props, pats) = TreeNodeViewModel.FromA11yElement(el, result.ProcessName);
                TxtDetailsTitle.Text = $"[{result.ControlType}] {result.Name ?? ""}";
                PropertiesGrid.ItemsSource = props;
                PatternsTree.ItemsSource = pats;
                // 记录当前 Details 元素约束（右键添加监控时作目标元素定位）
                _detailsElemName = result.Name;
                _detailsElemControlType = result.ControlType;
                _detailsElemAutomationId = result.AutomationId;
            }

            // 未加载树或目标进程变化 → 自动抓新窗口树
            if (_currentTree is null || _currentTarget is null || _currentTarget.ProcessId != result.ProcessId)
            {
                var win = WindowEnumerator.EnumerateWindows()
                    .FirstOrDefault(x => x.ProcessId == result.ProcessId);
                if (win is not null)
                    CaptureTree(new TargetWindowInfo(win.ProcessId, win.Handle, win.ProcessName, win.Title));
                continue;
            }

            // 在已加载树中定位
            int? nodeId = MouseCapture.LocateInTree(_currentTree, result);
            ClearHighlight();
            if (nodeId is int id && _nodeLookup.TryGetValue(id, out var vm))
            {
                Highlight(vm);
                TxtCaptureStatus.Text = $"{result.ProcessName} — {vm.Display}";
            }
            else
            {
                TxtCaptureStatus.Text = $"{result.ProcessName} — 未在树中定位 ({result.ControlType})";
            }
        }
    }

    private void ClearHighlight()
    {
        foreach (var vm in _nodeLookup.Values)
            vm.IsHighlighted = false;
        // TreeView 颜色更新：通过刷新 ItemContainer 实现（简化：重绑根节点显示）
        RefreshTreeVisuals();
    }

    private void Highlight(TreeNodeViewModel vm)
    {
        vm.IsHighlighted = true;
        RefreshTreeVisuals();
        // 展开到该节点
        ExpandPath(vm.Id);
    }

    private void ExpandPath(int id)
    {
        if (_currentTree is null) return;
        // 收集从根到目标的路径 id
        var path = new Stack<int>();
        int cur = id;
        while (cur != -1 && _currentTree.Nodes.TryGetValue(cur, out var n))
        {
            path.Push(cur);
            cur = n.ParentId;
        }

        // 沿路径展开（先找到根 TreeViewItem）
        TreeViewItem? item = FindItemInItems(_treeRoots, path.Pop());
        while (item is not null && path.Count > 0)
        {
            item.IsExpanded = true;
            item.UpdateLayout();
            item = FindItemInItems(item.Items, path.Pop());
        }
        item?.BringIntoView();
    }

    private static TreeViewItem? FindItemInItems(System.Collections.IEnumerable items, int id)
    {
        foreach (var o in items)
        {
            if (o is TreeViewItem tvi && tvi.DataContext is TreeNodeViewModel vm && vm.Id == id)
                return tvi;
        }
        return null;
    }

    private void RefreshTreeVisuals()
    {
        // 更新高亮背景：遍历所有 TreeViewItem
        foreach (var root in TreeView.Items.OfType<TreeViewItem>())
            UpdateItemRecursive(root);
    }

    private void UpdateItemRecursive(TreeViewItem item)
    {
        if (item.DataContext is TreeNodeViewModel vm)
        {
            var brush = vm.IsHighlighted
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0x66)) // 黄色高亮
                : System.Windows.Media.Brushes.Transparent;
            item.Background = brush;
        }
        foreach (var child in item.Items.OfType<TreeViewItem>())
            UpdateItemRecursive(child);
    }

    // ========== Details 面板 ==========
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _detailsLocked = false; // 用户手动选中树节点 → 解锁 Details
        if (e.NewValue is TreeNodeViewModel vm)
        {
            _selectedNode = vm;
            TxtDetailsTitle.Text = vm.Display;
            PropertiesGrid.ItemsSource = vm.Properties;
            PatternsTree.ItemsSource = vm.Patterns;
            // 记录当前 Details 元素约束（右键添加监控时作目标元素定位）
            _detailsElemName = vm.Name;
            _detailsElemControlType = vm.ControlType;
            _detailsElemAutomationId = vm.AutomationId;
        }
        else
        {
            _selectedNode = null;
            TxtDetailsTitle.Text = "选中节点查看详情";
            PropertiesGrid.ItemsSource = null;
            PatternsTree.ItemsSource = null;
        }
    }

    /// <summary>
    /// 右键菜单："添加为监控对象"——弹出窗口让用户输入间隔和时长。
    /// </summary>
    private void MenuItem_AddAsTarget_Click(object sender, RoutedEventArgs e)
    {
        // 判断来源：PropertyItem 还是 PatternItem
        string propName;
        string propValue;
        string procName = "";
        
        var source = (sender as MenuItem)?.Parent as ContextMenu;
        if (source?.PlacementTarget is DataGrid) // 来自 Properties
        {
            if (PropertiesGrid.SelectedItem is not PropertyItem pi)
            {
                TxtStatus.Text = "请先选中一个属性";
                return;
            }
            propName = pi.Name;
            propValue = pi.FullValue;
            procName = _selectedNode?.ProcessName ?? _currentTarget?.ProcessName ?? "";
        }
        else if (source?.PlacementTarget is TreeView) // 来自 Patterns（或其子项）
        {
            // ★ 可能是 PatternItem（右键 Pattern 本身）或 PropertyItem（右键子属性如 Value）
            var clicked = PatternsTree.SelectedItem ?? _rightClickedItem;
            if (clicked is PatternItem pat)
            {
                // 右键了 Pattern 本身（如 ValuePattern）→ 用其第一个子属性作为监控目标
                propName = pat.SubProperties.Count > 0
                    ? $"{pat.Name}.{pat.SubProperties[0].Name}"  // 如 "ValuePattern.Value"
                    : pat.Name;
                propValue = pat.Value;
            }
            else if (clicked is PropertyItem pi)
            {
                // ★ 右键了 Pattern 的子属性（如 ValuePattern 下的 Value）
                // 需要找到父 Pattern 名称来构造完整属性路径
                propName = pi.Name;  // 如 "Value"
                propValue = pi.FullValue;
                // 尝试从父 TreeViewItem 获取 Pattern 名称
                var parentPat = FindParentPatternItem();
                if (!string.IsNullOrEmpty(parentPat))
                    propName = $"{parentPat}.{pi.Name}";  // 如 "ValuePattern.Value"
            }
            else
            {
                TxtStatus.Text = "请先选中一个 Pattern 或其子属性";
                return;
            }
            procName = _selectedNode?.ProcessName ?? _currentTarget?.ProcessName ?? "";
        }
        else
        {
            if (_selectedNode is null) { TxtStatus.Text = "请先选中节点"; return; }
            propName = "Name";
            propValue = _selectedNode.Name ?? "";
            procName = _selectedNode.ProcessName ?? _currentTarget?.ProcessName ?? "";
        }

        // 弹出"添加监控"对话框（传入当前值作提取规则示例）
        var dlg = new AddMonitorDialog(propName, procName, _settings.ExtractMarker, propValue,
            nameExists: TaskNameExists, targetOptions: GetTargetOptionsFromDetails());
        dlg.Owner = this;
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        // 创建监控任务
        CreateMonitorTask(
            monitorProp: dlg.MonitorProperty,
            processName: dlg.ProcessName,
            intervalMs: dlg.IntervalMs,
            durationSec: dlg.DurationSec,
            taskName: dlg.TaskName,
            saveBatchPoints: dlg.SaveBatchPoints,
            saveIntervalSec: dlg.SaveIntervalSec,
            extractors: dlg.Extractors);
    }

    /// <summary>任务重名校验（传给 AddMonitorDialog 在弹窗内提示，不关闭弹窗）。</summary>
    private bool TaskNameExists(string name)
        => _taskItems.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从 Details 面板当前元素构建监控目标下拉选项：
    /// Properties 的属性名 + Patterns 的 "Pattern.子属性" 路径（含各自当前值作示例）。
    /// </summary>
    private List<TargetOption> GetTargetOptionsFromDetails()
    {
        var options = new List<TargetOption>();
        if (PropertiesGrid.ItemsSource is IEnumerable<PropertyItem> props)
            options.AddRange(props.Select(p => new TargetOption(p.Name, p.FullValue)));
        if (PatternsTree.ItemsSource is IEnumerable<PatternItem> pats)
        {
            foreach (var pat in pats)
            {
                if (pat.SubProperties.Count > 0)
                    options.AddRange(pat.SubProperties.Select(sp => new TargetOption($"{pat.Name}.{sp.Name}", sp.FullValue)));
                else
                    options.Add(new TargetOption(pat.Name, pat.Value));
            }
        }
        return options;
    }

    /// <summary>创建监控任务（统一入口）。autoStart=false（默认）：仅创建不启动，右键任务行"启动采集"开始。</summary>
    private void CreateMonitorTask(string monitorProp, string processName, int intervalMs, int durationSec,
        string taskName, int saveBatchPoints = 10, int saveIntervalSec = 5,
        IReadOnlyList<string>? extractors = null, bool autoStart = false)
    {
        if (_currentTarget is null)
        {
            TxtStatus.Text = "请先抓取目标窗口的结构树";
            return;
        }

        // ★ 重名检查：任务栏里已存在同名任务则提示并拒绝创建
        if (_taskItems.Any(t => string.Equals(t.Name, taskName, StringComparison.OrdinalIgnoreCase)))
        {
            TxtStatus.Text = $"任务名称 [{taskName}] 已存在，请修改任务名称后重试";
            MessageBox.Show($"任务名称 \"{taskName}\" 已存在。\n请在弹窗中修改任务名称后重新添加。", "任务名称重复",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ★ 日志：任务创建参数
        Log($"=== 创建任务: {taskName} ===");
        Log($"  目标={monitorProp}, 进程={processName}, 间隔={intervalMs}ms, 时长={durationSec}s");
        Log($"  _currentTarget={_currentTarget?.ProcessName}/{_currentTarget?.ProcessId}");
        Log($"  _selectedNode={_selectedNode?.ControlType}/{_selectedNode?.Name}");

        var config = new MonitorTaskConfig
        {
            Name = taskName,
            Target = new MonitorTarget
            {
                Kind = TargetKind.Process,
                Value = processName,
                // ★ 元素约束：树选中节点优先，鼠标捕获的 Details 元素兜底
                // （此前只取 _selectedNode，烧杯/悬停路径下为 null → 任务匹配到树的第一个节点）
                ElementControlType = _selectedNode?.ControlType ?? _detailsElemControlType,
                ElementName = _selectedNode?.Name ?? _detailsElemName,
                ElementAutomationId = _selectedNode?.AutomationId ?? _detailsElemAutomationId,
            },
            Properties = new[] { monitorProp },
            Timer = new TimerSpec { IntervalMs = intervalMs },
            OutputDirectory = TodayDataDir,
            // 值提取规则（@@标记对 / re:正则）：空=记录原始值
            Extractors = extractors ?? Array.Empty<string>(),
            // 定时触发：立即首采 + 每隔 intervalMs 重复（ImmediateTriggerSpec 只触发一次，任务会停在首次采样后空转）
            Trigger = new ScheduleTriggerSpec { Repeat = true, RepeatIntervalMs = intervalMs },
        };

        // 注册落盘策略：内存缓冲，后台按"每N点或每T秒（先到先写）"批量写 CSV（行式：监控对象,值1..值N,时间戳）
        _saver.SetPolicy(config.Id, taskName, new SavePolicy(saveBatchPoints, saveIntervalSec),
            monitorTarget: monitorProp, valueCount: config.Extractors.Count > 0 ? config.Extractors.Count : 1);

        // ★ 日志：注册前检查
        System.Diagnostics.Debug.WriteLine($"[MONITOR] 准备Register: config.Id={config.Id}, Properties=[{string.Join(",", config.Properties)}]");
        try { _scheduler.Register(config, start: autoStart); System.Diagnostics.Debug.WriteLine($"[MONITOR] Register成功 start={autoStart}"); }
        catch (Exception ex) { TxtStatus.Text = $"注册任务失败: {ex.Message}"; System.Diagnostics.Debug.WriteLine($"[MONITOR] Register失败: {ex}"); return; }
        var item = new TaskListItem
        {
            Config = config,
            MonitorProperty = monitorProp,
            TargetProcessName = processName,
            IntervalMs = intervalMs,
            DurationSec = durationSec,
            State = autoStart ? "Running" : "Idle",
        };
        _taskItems.Add(item);

        // ★ 启动实时值刷新定时器
        StartValueRefreshTimer(item);

        // 保存策略描述（按点 或 按时间 二选一：另一维度被禁用时不在消息里显示）
        var saveDesc = saveBatchPoints == int.MaxValue
            ? $"每 {saveIntervalSec} 秒落盘"
            : $"每 {saveBatchPoints} 点落盘";
        TxtStatus.Text = autoStart
            ? $"已添加监控任务 [{taskName}]，每 {intervalMs}ms 采样一次" +
              $"{(durationSec > 0 ? $"，共 {durationSec} 秒" : "")}；数据{saveDesc} → {TodayDataDir}"
            : $"已创建监控任务 [{taskName}]（未启动），右键任务行 →「启动采集」开始采样；数据{saveDesc} → {TodayDataDir}";
    }

    private void PropertiesGrid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 确保右键点击的行被选中
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row && row.Item is PropertyItem)
        {
            PropertiesGrid.SelectedItem = row.Item;
            _rightClickedItem = row.Item;
        }
    }

    private void PatternsTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 找到右键点击的 TreeViewItem（TreeView.SelectedItem 是只读的，用临时变量传给 ContextMenu）
        // ★ Pattern 的子项是 PropertyItem（HierarchicalDataTemplate ItemsSource=SubProperties）
        // 用户可能右键 Pattern 本身，也可能右键其子属性（如 ValuePattern 下的 Value）
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not TreeViewItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is TreeViewItem tvi)
        {
            if (tvi.DataContext is PatternItem || tvi.DataContext is PropertyItem)
                _rightClickedItem = tvi.DataContext;
        }
    }

    /// <summary>判断属性是否为文本类（适合用正则提取值变化）。</summary>
    private static bool IsTextProperty(string name) => name switch
    {
        "Name" or "Value" or "HelpText" or "ItemStatus" or "AccessKey" or "ToggleState" or "RangeValue" => true,
        _ => false,
    };

    /// <summary>
    /// 在 PatternsTree 中查找右键项的父 PatternItem 名称。
    /// 用于构造完整属性路径（如 "ValuePattern.Value"）。
    /// </summary>
    private string? FindParentPatternItem()
    {
        // 遍历 PatternsTree 的 TreeViewItem，找 DataContext 为 PropertyItem 且匹配 _rightClickedItem 的
        // 其父级 TreeViewItem 的 DataContext 就是 PatternItem
        foreach (var root in PatternsTree.Items.OfType<object>())
        {
            var result = FindParentPatternRecursive(PatternsTree.ItemContainerGenerator.ContainerFromItem(root) as TreeViewItem);
            if (result is not null) return result;
        }
        return null;
    }

    private string? FindParentPatternRecursive(TreeViewItem? item)
    {
        if (item is null) return null;
        // 检查子项中是否有 _rightClickedItem
        foreach (var child in item.Items.OfType<object>())
        {
            var childTvi = item.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem;
            if (childTvi?.DataContext == _rightClickedItem && item.DataContext is PatternItem pat)
                return pat.Name;
            if (childTvi is not null)
            {
                var deep = FindParentPatternRecursive(childTvi);
                if (deep is not null) return deep;
            }
        }
        return null;
    }

    // ========== 任务管理 ==========
    private void BtnAddTask_Click(object sender, RoutedEventArgs e)
    {
        // 旧入口保留兼容：如果没有选中节点，提示用户用右键
        if (_selectedNode is null)
        {
            TxtStatus.Text = "请先在左侧 Details 面板右键属性/Pattern →「添加为监控对象」";
            return;
        }
        // 兜底：直接用当前选中节点的 Name 属性
        CreateMonitorTask(
            monitorProp: "Name",
            processName: _selectedNode.ProcessName ?? _currentTarget?.ProcessName ?? "",
            intervalMs: 1000,
            durationSec: 0,
            taskName: $"{_selectedNode.ProcessName ?? "app"}-Name");
    }

    /// <summary>启动实时值刷新定时器（每个运行中的任务一个）。</summary>
    private readonly Dictionary<Guid, DispatcherTimer> _refreshTimers = new();
    
    private void StartValueRefreshTimer(TaskListItem item)
    {
        if (_refreshTimers.ContainsKey(item.Config.Id)) return;
        
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(item.IntervalMs) };
        timer.Tick += (_, _) => RefreshMonitorValue(item);
        _refreshTimers[item.Config.Id] = timer;
        timer.Start();
        
        // ★ 日志：订阅采样事件
        var host = _scheduler.GetHost(item.Config.Id);
        System.Diagnostics.Debug.WriteLine($"[MONITOR] StartValueRefresh: task={item.Name}, host={(host?.GetType().Name ?? "null")}, host.State={host?.State}");
        if (host is not null)
        {
            host.SampleTaken += (_, ev) =>
            {
                // 仅刷新 UI（INPC 实时生效）；写盘走 saver 内存缓冲（后台批量，不占 UI/采集线程）
                Dispatcher.Invoke(() =>
                {
                    item.SampleCount++;
                    item.LastSampleTime = DateTime.Now.ToString("HH:mm:ss.fff");
                    // ★ 当前值列实时显示采样值（Records 已应用提取规则 → 值1, 值2, 值3…）
                    var vals = string.Join(", ", ev.Sample.Records.Select(r => r.Value));
                    if (vals.Length > 0) item.CurrentValue = vals;
                });
                _saver.Enqueue(item.Config.Id, item.Config.Name, ev.Sample.Records);
            };
            System.Diagnostics.Debug.WriteLine($"[MONITOR] 已订阅SampleTaken事件: task={item.Name}");
            host.StateChanged += (_, s) => Dispatcher.Invoke(() =>
            {
                item.State = s.ToString();
                if (s is TaskRunState.Stopped or TaskRunState.Completed or TaskRunState.Error)
                    _saver.FlushAll(); // 任务结束时立即写出该任务剩余数据
            });
            if (item.DurationSec > 0)
            {
                // 限时任务：从任务真正进入 Running（首次启动）起算，Idle（未启动）不计时
                DateTime? startedAt = null;
                var checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                checkTimer.Tick += (_, _) =>
                {
                    if (string.Equals(item.State, "Idle", StringComparison.OrdinalIgnoreCase)) return; // 未启动：等待
                    startedAt ??= DateTime.Now; // 首次启动时刻
                    if (DateTime.Now - startedAt.Value >= TimeSpan.FromSeconds(item.DurationSec))
                    {
                        checkTimer.Stop();
                        StopValueRefreshTimer(item.Config.Id);
                        _scheduler.RequestStop(item.Config.Id);
                        item.State = "Completed";
                        TxtStatus.Text = $"任务 [{item.Name}] 已完成（{item.DurationSec} 秒）";
                    }
                };
                checkTimer.Start();
            }
        }
        
        _configStore.Save(item.Config);
    }

    /// <summary>刷新单个任务的监控值（重新抓取目标元素属性）。停止/完成/出错后不再刷新（当前值保持最后采样值）。</summary>
    private void RefreshMonitorValue(TaskListItem item)
    {
        try
        {
            // ★ 停止采集后当前值不再变动（需求1）：仅 Running 状态刷新
            if (!string.Equals(item.State, "Running", StringComparison.OrdinalIgnoreCase)) return;

            if (_currentTarget is null || _currentTree is null)
            {
                System.Diagnostics.Debug.WriteLine($"[MONITOR] RefreshValue跳过: _currentTarget={_currentTarget is null}");
                return;
            }
            
            // 在后台线程抓取目标窗口树，找到匹配元素后读取属性值
            var thread = new Thread(() =>
            {
                try
                {
                    using var session = UiaSession.Create();
                    var snap = new TreeSampler().CaptureWindow(session, _currentTarget);
                    if (snap is null || snap.NodeCount == 0) 
                    { 
                        System.Diagnostics.Debug.WriteLine($"[MONITOR] CaptureWindow返回空: task={item.Name}"); 
                        return; 
                    }
                    
                    // 找到与监控目标匹配的节点（按进程名+控件类型+名称+AutomationId，null 约束不参与匹配）
                    var t = item.Config.Target;
                    var matchNode = snap.Nodes.Values.FirstOrDefault(n =>
                        n.ProcessId == _currentTarget.ProcessId &&
                        (t.ElementControlType is null || n.ControlType == t.ElementControlType) &&
                        (t.ElementName is null || n.Name == t.ElementName) &&
                        (t.ElementAutomationId is null || n.AutomationId == t.ElementAutomationId));
                    
                    if (matchNode is not null)
                    {
                        // GetProperty 已支持 "ValuePattern.Value" 带点路径（ElementNode.GetProperty）
                        var foundVal = matchNode.GetProperty(item.MonitorProperty);
                        // ★ 多规则任务：当前值列显示 值1, 值2, 值3…（应用提取规则后的各值）
                        var display = foundVal is not null && item.Config.Extractors is { Count: > 0 }
                            ? string.Join(", ", ValueExtractor.ExtractAll(foundVal, item.Config.Extractors))
                            : foundVal ?? "-";
                        Dispatcher.Invoke(() => item.CurrentValue = display);
                    }
                }
                catch { /* 单次刷新失败不影响后续 */ }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch { /* 定时器异常 */ }
    }

    private void StopValueRefreshTimer(Guid configId)
    {
        if (_refreshTimers.TryGetValue(configId, out var timer))
        {
            timer.Stop();
            _refreshTimers.Remove(configId);
        }
    }

    // ========== 任务操作（右键菜单 + 全局按钮） ==========

    /// <summary>右键任务行：先选中该行再弹菜单（菜单命令作用于选中项）。</summary>
    private void TaskList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not ListViewItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListViewItem row && row.DataContext is TaskListItem item)
            TaskList.SelectedItem = item;
    }

    /// <summary>启动采集：未启动（Idle）→ 首次启动；已停止（Stopped）→ 恢复采样。</summary>
    private void MenuStartTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item) return;
        if (string.Equals(item.State, "Running", StringComparison.OrdinalIgnoreCase)) return; // 已在运行
        if (string.Equals(item.State, "Idle", StringComparison.OrdinalIgnoreCase))
            _scheduler.Start(item.Config.Id);
        else
            _scheduler.Resume(item.Config.Id);
        item.State = "Running";
        TxtStatus.Text = $"任务 [{item.Name}] 已启动采集（每 {item.IntervalMs}ms 采样一次）";
    }

    private void MenuStopTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item) return;
        _scheduler.RequestStop(item.Config.Id);
        item.State = "Stopped";
    }

    private void MenuResumeTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item) return;
        _scheduler.Resume(item.Config.Id);
        item.State = "Running";
        TxtStatus.Text = $"任务 [{item.Name}] 已恢复采样";
    }

    private void MenuRemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item) return;
        _scheduler.Remove(item.Config.Id);
        _configStore.Delete(item.Config.Id);
        _saver.RemoveTask(item.Config.Id); // 写出剩余数据并注销
        _taskItems.Remove(item);
        StopValueRefreshTimer(item.Config.Id);
        TxtStatus.Text = $"任务 [{item.Name}] 已移除，数据已落盘";
    }

    /// <summary>右键任务行：只读打开"监控配置"弹窗（与添加监控弹窗同款，全部编辑与"确定添加"禁用）。</summary>
    private void MenuViewTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item) return;
        var dlg = new AddMonitorDialog(item.MonitorProperty, item.TargetProcessName, _settings.ExtractMarker,
            item.CurrentValue, viewItem: item, savePolicy: _saver.GetPolicy(item.Config.Id))
        { Owner = this };
        dlg.ShowDialog();
    }

    private void MenuExportTask_Click(object sender, RoutedEventArgs e) => ExportSelectedTask();

    /// <summary>导出选中任务报告（CSV/JSON/HTML + 走势图 PNG：一图多线、可选系列、自定义图例）。</summary>
    private void ExportSelectedTask()
    {
        if (TaskList.SelectedItem is not TaskListItem item)
        {
            TxtStatus.Text = "请先选中一个任务";
            return;
        }
        _saver.FlushAll(); // 先把内存缓冲写盘，报告与实时数据一致
        var host = _scheduler.GetHost(item.Config.Id);
        var records = host is not null ? host.Records.ToList() : new List<SampleRecord>();
        if (records.Count == 0)
        {
            MessageBox.Show("该任务还没有采样数据，无法导出。", "导出报告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (cancelled, chartPath) = BuildChartPngIfWanted(item, records);
        if (cancelled)
        {
            TxtStatus.Text = "已取消导出";
            return;
        }

        try
        {
            var files = _exporter.ExportAll(item.Config.Name, item.Config.Id, item.Config,
                records, DateTime.Now.AddSeconds(-60), DateTime.Now, chartPath);
            TxtStatus.Text = $"已导出：{string.Join(" | ", files)}";
            MessageBox.Show($"报告已导出到：\n{string.Join("\n", files)}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"导出失败: {ex.Message}";
        }
        finally
        {
            if (chartPath is not null)
                try { File.Delete(chartPath); } catch { /* 临时文件清理失败可忽略 */ }
        }
    }

    /// <summary>
    /// 检测各值列是否可绘制（≥2 个可解析数值点），可绘则弹窗让用户勾选系列、修改图例，
    /// 渲染 PNG 到临时文件。返回 (是否取消整个导出, PNG 路径或 null)。
    /// </summary>
    private (bool Cancelled, string? ChartPath) BuildChartPngIfWanted(TaskListItem item, List<SampleRecord> records)
    {
        int n = item.Config.Extractors.Count > 0 ? item.Config.Extractors.Count : 1;

        // 与实时落盘同规则切行：(时间戳, 值1..值N)
        var rows = new List<(DateTime Ts, string?[] Vals)>();
        for (int i = 0; i + n <= records.Count; i += n)
        {
            var vals = new string?[n];
            for (int c = 0; c < n; c++) vals[c] = records[i + c].Value;
            rows.Add((records[i].Timestamp, vals));
        }

        // 每列统计可解析数值点数
        var numericCounts = new int[n];
        foreach (var (_, vals) in rows)
            for (int c = 0; c < n; c++)
                if (double.TryParse(vals[c], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    numericCounts[c]++;

        if (!numericCounts.Any(c => c >= 2))
        {
            TxtStatus.Text = "未检测到可绘图的数值列（时间戳/值1 均需数值且≥2个点），仅导出数据";
            return (false, null);
        }

        // 弹窗：勾选要画的曲线 + 编辑图例（非数值列禁选）
        var seriesItems = Enumerable.Range(0, n).Select(c => new ChartSeriesItem
        {
            ColumnIndex = c,
            Key = $"值{c + 1}",
            IsNumeric = numericCounts[c] >= 2,
            Selected = numericCounts[c] >= 2,   // 数值列默认全选
            Legend = $"值{c + 1}",
        }).ToList();

        var dlg = new ChartOptionsDialog(seriesItems) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.Result.Confirmed)
            return (true, null);
        if (!dlg.Result.GenerateChart)
            return (false, null);

        // 组装选中系列（跳过该列不可解析的点）
        var series = new List<ChartSeries>();
        foreach (var (index, _, legend) in dlg.Result.SelectedSeries)
        {
            var pts = rows
                .Where(r => double.TryParse(r.Vals[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(r => (r.Ts, V: double.Parse(r.Vals[index]!, NumberStyles.Float, CultureInfo.InvariantCulture)))
                .ToList();
            if (pts.Count > 0)
                series.Add(new ChartSeries(legend, pts));
        }
        if (series.Count == 0)
            return (false, null);

        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"uitm_chart_{Guid.NewGuid():N}.png");
            ChartImageRenderer.RenderPng(tmp, series, item.Name);
            return (false, tmp);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"走势图生成失败（仅导出数据）: {ex.Message}";
            return (false, null);
        }
    }

    /// <summary>启动所有任务：Idle → 首次启动；Stopped/其他非运行态 → 恢复采样；Running 跳过。</summary>
    private void BtnStartAllTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_taskItems.Count == 0)
        {
            TxtStatus.Text = "当前没有监控任务";
            return;
        }
        int started = 0;
        foreach (var item in _taskItems)
        {
            if (string.Equals(item.State, "Running", StringComparison.OrdinalIgnoreCase)) continue; // 已在运行
            if (string.Equals(item.State, "Idle", StringComparison.OrdinalIgnoreCase))
                _scheduler.Start(item.Config.Id);
            else
                _scheduler.Resume(item.Config.Id);
            item.State = "Running";
            started++;
        }
        TxtStatus.Text = started == 0
            ? "全部任务均在运行中，无需启动"
            : $"已启动 {started} 个任务（共 {_taskItems.Count} 个）";
    }

    private void BtnStopAllTasks_Click(object sender, RoutedEventArgs e)
    {
        _scheduler.RequestStopAll();
        foreach (var item in _taskItems) item.State = "Stopped";
        TxtStatus.Text = $"已停止全部任务（{_taskItems.Count} 个）";
    }

    private void BtnRemoveAllTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_taskItems.Count == 0) return;
        var count = _taskItems.Count;
        foreach (var item in _taskItems.ToList())
        {
            _scheduler.Remove(item.Config.Id);
            _configStore.Delete(item.Config.Id);
            _saver.RemoveTask(item.Config.Id); // 写出剩余数据并注销
            StopValueRefreshTimer(item.Config.Id);
        }
        _taskItems.Clear();
        TxtStatus.Text = $"已移除全部任务（{count} 个），剩余数据已落盘";
    }

    /// <summary>打开数据保存文件夹（不存在则新建，默认当日子目录）。</summary>
    private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TodayDataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = TodayDataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"打开数据目录失败: {ex.Message}";
        }
    }

    /// <summary>设置弹窗：修改截取字符（默认 @@）等全局选项，保存后立即生效。</summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settings.ExtractMarker) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ExtractMarker is null) return;
        _settings.ExtractMarker = dlg.ExtractMarker;
        _settings.Save(SettingsFilePath);
        TxtStatus.Text = $"设置已保存：截取字符 = {_settings.ExtractMarker}（新建任务的提取规则默认使用它）";
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshHistory();

    /// <summary>切换右侧页签：进入「采样历史」立即刷新并启动自动刷新；切走则停止。</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(MainTabs.SelectedItem, TabHistory))
        {
            RefreshHistory();
            _historyTimer?.Start();
        }
        else
        {
            _historyTimer?.Stop();
        }
    }

    /// <summary>刷新采样历史页：显示当前选中任务的全部采样记录（无选中/无记录时给出提示）。
    /// 仅在任务或记录数变化时重绑（避免周期重设导致滚动/选中丢失），重绑后滚动到最新一条。</summary>
    private void RefreshHistory()
    {
        if (TaskList.SelectedItem is not TaskListItem item)
        {
            HistoryGrid.ItemsSource = null;
            _historyTaskId = Guid.Empty;
            _historyCount = -1;
            TxtHistoryStatus.Text = "请先在「任务管理」页选中一个任务，此处显示其采样记录";
            return;
        }
        var records = GetHistory(item.Config.Id);
        if (item.Config.Id != _historyTaskId || records.Count != _historyCount)
        {
            _historyTaskId = item.Config.Id;
            _historyCount = records.Count;
            HistoryGrid.ItemsSource = records;
            if (records.Count > 0) HistoryGrid.ScrollIntoView(records[^1]); // 跟随最新记录
        }
        TxtHistoryStatus.Text = records.Count == 0
            ? $"任务 [{item.Name}]：暂无采样记录（启动采集后此处实时显示）"
            : $"任务 [{item.Name}]：{records.Count} 条记录（最新：{records[^1].Timestamp:HH:mm:ss.fff}）";
    }

    private Guid _historyTaskId = Guid.Empty; // 上次刷新的任务（变化时重绑）
    private int _historyCount = -1; // 上次刷新的记录数（变化时重绑）

    private List<SampleRecord> GetHistory(Guid? taskId = null)
    {
        if (taskId is Guid id && _scheduler.GetHost(id) is { } h)
            return h.Records.ToList();
        return new List<SampleRecord>();
    }

    private void LoadSavedTasks()
    {
        foreach (var cfg in _configStore.LoadAll())
        {
            var item = new TaskListItem { Config = cfg, State = "Idle", SampleCount = 0 };
            _taskItems.Add(item);
            // 不自动启动，仅展示
        }
    }
}
