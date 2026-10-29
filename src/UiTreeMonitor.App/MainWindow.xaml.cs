using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public string State { get; set; } = "Idle";
    public int SampleCount { get; set; }
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
    private readonly SampleCsvWriter _csvWriter;
    private readonly ReportExporter _exporter;
    private readonly MouseCapture _mouseCapture = new(pollMs: 120);

    private TreeSnapshot? _currentTree;
    private Dictionary<int, TreeNodeViewModel> _nodeLookup = new();
    private TargetWindowInfo? _currentTarget;
    private DispatcherTimer? _drainTimer;
    private bool _capturing;
    private MouseCaptureResult? _lastHoverResult; // 当前悬停选中的元素（烧杯按钮回调用）
    private TreeNodeViewModel? _selectedNode; // 当前树选中节点（Details 面板联动）
    private object? _rightClickedItem; // 右键点击的属性/Pattern 项（ContextMenu 回调用）
    private bool _detailsLocked; // 烧杯按钮点击后锁定 Details 不再刷新

    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ui-tree-monitor");
    private static readonly string TaskConfigDir = Path.Combine(AppDataDir, "tasks");
    private static readonly string OutputDir = Path.Combine(AppDataDir, "exports");

    public MainWindow()
    {
        InitializeComponent();
        _configStore = new TaskConfigStore(TaskConfigDir);
        _csvWriter = new SampleCsvWriter(OutputDir);
        _exporter = new ReportExporter(OutputDir);

        TreeView.ItemsSource = _treeRoots;
        TaskList.ItemsSource = _taskItems;

        RefreshWindows();
        LoadSavedTasks();

        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _drainTimer.Tick += (_, _) => DrainCaptureQueue();
        _drainTimer.Start();
        Closed += (_, _) =>
        {
            _mouseCapture.Stop();
            HollowHighlightDriver.ClearAllHighlighters(); // 原版退出时清理全部高亮器
            _scheduler.Dispose();
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

        // ★ 烧杯按钮也弹出"添加监控"对话框（与右键菜单统一体验）
        var dlg = new AddMonitorDialog("Name", result.ProcessName ?? "");
        dlg.Owner = this;
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        CreateAndStartMonitorTask(
            monitorProp: dlg.MonitorProperty,
            processName: dlg.ProcessName,
            intervalMs: dlg.IntervalMs,
            durationSec: dlg.DurationSec,
            taskName: $"{result.ProcessName ?? "app"}-{dlg.MonitorProperty}");
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

        // 弹出"添加监控"对话框
        var dlg = new AddMonitorDialog(propName, procName);
        dlg.Owner = this;
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        // 创建监控任务
        CreateAndStartMonitorTask(
            monitorProp: dlg.MonitorProperty,
            processName: dlg.ProcessName,
            intervalMs: dlg.IntervalMs,
            durationSec: dlg.DurationSec,
            taskName: $"{procName}-{propName.Replace('.', '-')}");
    }

    /// <summary>创建并启动监控任务（统一入口）。</summary>
    private void CreateAndStartMonitorTask(string monitorProp, string processName, int intervalMs, int durationSec, string taskName)
    {
        if (_currentTarget is null)
        {
            TxtStatus.Text = "请先抓取目标窗口的结构树";
            return;
        }

        var config = new MonitorTaskConfig
        {
            Name = taskName,
            Target = new MonitorTarget
            {
                Kind = TargetKind.Process,
                Value = processName,
                ElementControlType = _selectedNode?.ControlType,
                ElementName = _selectedNode?.Name,
            },
            Properties = new[] { monitorProp },
            Timer = new TimerSpec { IntervalMs = intervalMs },
            OutputDirectory = OutputDir,
            Trigger = new ImmediateTriggerSpec(),
        };

        _scheduler.Register(config);
        var item = new TaskListItem
        {
            Config = config,
            MonitorProperty = monitorProp,
            TargetProcessName = processName,
            IntervalMs = intervalMs,
            DurationSec = durationSec,
            State = "Running",
        };
        _taskItems.Add(item);

        // ★ 启动实时值刷新定时器
        StartValueRefreshTimer(item);

        TxtStatus.Text = $"已添加监控任务 [{taskName}]，每 {intervalMs}ms 采样一次{(durationSec > 0 ? $", 共 {durationSec} 秒" : "")}";
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
        CreateAndStartMonitorTask(
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
        
        // 订阅采样事件 → 更新采样数
        var host = _scheduler.GetHost(item.Config.Id);
        if (host is not null)
        {
            host.SampleTaken += (_, ev) =>
            {
                Dispatcher.Invoke(() =>
                {
                    item.SampleCount++;
                    item.LastSampleTime = DateTime.Now.ToString("HH:mm:ss.fff");
                    _csvWriter.Append(item.Config.Name, item.Config.Id, ev.Sample.Records);
                });
            };
            host.StateChanged += (_, s) => Dispatcher.Invoke(() => { item.State = s.ToString(); });
            if (item.DurationSec > 0)
            {
                // 限时任务：到达时长后自动停止
                var stopAt = DateTime.Now.AddSeconds(item.DurationSec);
                var checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                checkTimer.Tick += (_, _) =>
                {
                    if (DateTime.Now >= stopAt)
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

    /// <summary>刷新单个任务的监控值（重新抓取目标元素属性）。</summary>
    private void RefreshMonitorValue(TaskListItem item)
    {
        try
        {
            if (_currentTarget is null || _currentTree is null) return;
            
            // 在后台线程抓取目标窗口树，找到匹配元素后读取属性值
            var thread = new Thread(() =>
            {
                try
                {
                    using var session = UiaSession.Create();
                    var snap = new TreeSampler().CaptureWindow(session, _currentTarget);
                    if (snap?.NodeCount == 0) return;
                    
                    // 找到与监控目标匹配的节点（按进程名+控件类型+名称）
                    var matchNode = snap.Nodes.Values.FirstOrDefault(n =>
                        n.ProcessId == _currentTarget.ProcessId &&
                        (n.ControlType == item.Config.Target.ElementControlType || 
                         string.IsNullOrEmpty(item.Config.Target.ElementControlType)) &&
                        (n.Name == item.Config.Target.ElementName ||
                         string.IsNullOrEmpty(item.Config.Target.ElementName)));
                    
                    if (matchNode is not null)
                    {
                        // 从 PatternDataList 中查找监控属性的值
                        var propParts = item.MonitorProperty.Split('.');
                        string? foundVal = null;
                        
                        // 先查离散字段
                        foundVal = matchNode.GetProperty(propParts[0]);
                        
                        // 再查 Pattern 数据
                        if (string.IsNullOrEmpty(foundVal) || foundVal == "")
                        {
                            foreach (var pd in matchNode.PatternDataList)
                            {
                                if (pd.Name == propParts[0])
                                {
                                    foundVal = pd.DisplayValue;
                                    break;
                                }
                                foreach (var spp in pd.SubProps)
                                {
                                    if (spp.Name == propParts.ElementAtOrDefault(1))
                                    { foundVal = spp.Value; break; }
                                }
                                if (!string.IsNullOrEmpty(foundVal) && foundVal != "(null)" && foundVal != "(supported, no current value)") 
                                    break;
                            }
                        }
                        
                        Dispatcher.Invoke(() => item.CurrentValue = foundVal ?? "-");
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

    private void BtnStopTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskListItem item)
        {
            _scheduler.RequestStop(item.Config.Id);
            item.State = "Stopped";
            TaskList.Items.Refresh();
        }
    }

    private void BtnResumeTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskListItem item)
        {
            _scheduler.Resume(item.Config.Id);
            item.State = "Running";
            TaskList.Items.Refresh();
            TxtStatus.Text = $"任务 [{item.Name}] 已恢复采样";
        }
    }

    private void BtnRemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskListItem item)
        {
            _scheduler.Remove(item.Config.Id);
            _configStore.Delete(item.Config.Id);
            _taskItems.Remove(item);
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskListItem item)
        {
            TxtStatus.Text = "请先选中一个任务";
            return;
        }
        var host = _scheduler.GetHost(item.Config.Id);
        var records = host is not null ? host.Records.ToList() : new List<SampleRecord>();
        var files = _exporter.ExportAll(item.Config.Name, item.Config.Id, item.Config,
            records, DateTime.Now.AddSeconds(-60), DateTime.Now);
        TxtStatus.Text = $"已导出：{string.Join(" | ", files)}";
        MessageBox.Show($"报告已导出到：\n{string.Join("\n", files)}", "导出完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is TaskListItem item)
        {
            HistoryGrid.ItemsSource = GetHistory(item.Config.Id);
        }
    }

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
