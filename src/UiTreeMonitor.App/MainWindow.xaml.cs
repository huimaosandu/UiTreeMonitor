using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
public sealed class TaskListItem
{
    public required MonitorTaskConfig Config { get; init; }
    public string Name => Config.Name;
    public string State { get; set; } = "Idle";
    public int SampleCount { get; set; }
    public string TargetText => Config.Target.ToString() ?? "";
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

        if (result is null || !result.IsValid)
        {
            TxtStatus.Text = "未选中有效元素，未添加监测目标";
            return;
        }

        // 填入新表单：监控目标默认 Name（最常用属性）
        CmbMonitorProperty.Text = "Name";
        TxtTaskName.Text = $"{result.ProcessName}-{result.ControlType}";
        TxtStatus.Text = $"已添加监测目标：{result.ProcessName} — {result.Name}，点击『添加并启动』开始监控";
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
    /// 右键菜单："添加为监测目标"——从 Properties 或 Patterns 中选中一行后，
    /// 自动填入右侧任务表单（目标值/属性/正则过滤/任务名）。
    /// </summary>
    private void MenuItem_AddAsTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            TxtStatus.Text = "请先在结构树中选中一个节点";
            return;
        }

        // 判断来源：PropertyItem 还是 PatternItem
        string propName;
        string propValue;
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
        }
        else if (source?.PlacementTarget is TreeView) // 来自 Patterns
        {
            var pat = (PatternsTree.SelectedItem ?? _rightClickedItem) as PatternItem;
            if (pat is null)
            {
                TxtStatus.Text = "请先选中一个 Pattern";
                return;
            }
            propName = pat.Name; // 如 "ValuePattern", "TogglePattern"
            propValue = pat.Value;
        }
        else
        {
            // 兜底：用节点自身信息
            propName = "Name";
            propValue = _selectedNode.Name ?? "";
        }

        // 填入右侧表单（新表单：监控目标=属性名）
        var propText = CmbMonitorProperty.Text;
        if (!string.IsNullOrEmpty(propName) && string.IsNullOrEmpty(propText))
            CmbMonitorProperty.Text = propName;

        TxtTaskName.Text = $"{_selectedNode.ProcessName ?? "app"}-{_selectedNode.ControlType}-{propName}";

        // 正则过滤：文本类属性自动生成模式
        if (IsTextProperty(propName) && !string.IsNullOrEmpty(propValue))
        {
            TxtFilter.Text = $"{propName}~^{Regex.Escape(propValue)}";
        }
        else if (string.IsNullOrEmpty(TxtFilter.Text))
        {
            TxtFilter.Text = $"{propName}~.*";
        }
        TxtStatus.Text = $"已填入监测目标：{propName}={propValue}，点击『添加并启动任务』启动监控";

        // 切到"任务管理" tab 引导用户操作（沿 VisualTree 找 TabControl）
        var parent = TaskList.Parent;
        while (parent is not null and not TabControl)
            parent = LogicalTreeHelper.GetParent(parent);
        if (parent is TabControl tc)
            tc.SelectedIndex = 0;
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
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not TreeViewItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is TreeViewItem tvi && tvi.DataContext is PatternItem)
            _rightClickedItem = tvi.DataContext;
    }

    /// <summary>判断属性是否为文本类（适合用正则提取值变化）。</summary>
    private static bool IsTextProperty(string name) => name switch
    {
        "Name" or "Value" or "HelpText" or "ItemStatus" or "AccessKey" or "ToggleState" or "RangeValue" => true,
        _ => false,
    };

    // ========== 任务管理 ==========
    private void BtnAddTask_Click(object sender, RoutedEventArgs e)
    {
        // 监控目标（属性名）不能为空
        var monitorProp = CmbMonitorProperty.Text.Trim();
        if (string.IsNullOrEmpty(monitorProp))
        {
            TxtStatus.Text = "请填写监控目标（属性名，如 Name、Value、ToggleState）";
            return;
        }

        var config = new MonitorTaskConfig
        {
            Name = string.IsNullOrWhiteSpace(TxtTaskName.Text) ? "任务" : TxtTaskName.Text.Trim(),
            Target = new MonitorTarget
            {
                Kind = TargetKind.Process, // 固定：监控基于已抓取的窗口树
                Value = _currentTarget?.ProcessName ?? "",
                ElementControlType = _selectedNode?.ControlType,
                ElementName = _selectedNode?.Name,
            },
            Properties = new[] { monitorProp }, // 监控目标=单个属性
            Timer = new TimerSpec { IntervalMs = int.TryParse(TxtInterval.Text, out var iv) ? Math.Max(50, iv) : 1000 },
            OutputDirectory = OutputDir,
        };

        // 正则过滤：支持 Name~pattern 语法
        var filterText = TxtFilter.Text.Trim();
        if (!string.IsNullOrEmpty(filterText))
        {
            var rule = new FilterRule { Property = monitorProp, Pattern = filterText };
            if (filterText.Contains("~"))
            {
                var parts = filterText.Split('~', 2);
                rule.Property = parts[0].Trim();
                rule.Pattern = parts[1].Trim();
            }
            if (rule.Pattern.Contains("(?<") || rule.Pattern.Contains("(?P<"))
                rule.IsExtract = true;
            config.Filter = new FilterSet { Rules = new[] { rule }, IsAnd = true };
        }

        // 触发器
        var triggerTag = (CmbTrigger.SelectedItem as ComboBoxItem)?.Tag as string;
        config.Trigger = triggerTag switch
        {
            "Schedule" => new ScheduleTriggerSpec { RepeatIntervalMs = config.Timer.IntervalMs },
            "ValueChange" => new ValueChangeTriggerSpec { Property = monitorProp },
            "Threshold" => new ThresholdTriggerSpec { Property = monitorProp, Threshold = 0, Op = ComparisonOp.GreaterOrEqual },
            _ => new ImmediateTriggerSpec(),
        };

        _scheduler.Register(config);
        var item = new TaskListItem { Config = config };
        item.State = "Running";
        _taskItems.Add(item);
        TaskList.SelectedItem = item;

        // 订阅采样 → 更新列表 + 历史
        var host = _scheduler.GetHost(config.Id);
        if (host is not null)
        {
            host.SampleTaken += (_, ev) =>
            {
                Dispatcher.Invoke(() =>
                {
                    item.SampleCount++;
                    _csvWriter.Append(config.Name, config.Id, ev.Sample.Records);
                    if (TaskList.SelectedItem is TaskListItem sel && sel.Config.Id == config.Id)
                    {
                        HistoryGrid.ItemsSource = null;
                        HistoryGrid.ItemsSource = GetHistory(config.Id);
                    }
                    TxtHistoryStatus.Text = $"历史记录（实时追加）";
                });
            };
            host.StateChanged += (_, s) => Dispatcher.Invoke(() => { item.State = s.ToString(); TaskList.Items.Refresh(); });
        }

        _configStore.Save(config);
        TxtStatus.Text = $"任务 [{config.Name}] 已启动，监控 [{monitorProp}]";
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
