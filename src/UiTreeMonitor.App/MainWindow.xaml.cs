using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

    private HighlightOverlayWindow? _overlay;

    private TreeSnapshot? _currentTree;
    private Dictionary<int, TreeNodeViewModel> _nodeLookup = new();
    private TargetWindowInfo? _currentTarget;
    private DispatcherTimer? _drainTimer;
    private bool _capturing;

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
        CmbTargetKind.SelectedIndex = 0;

        RefreshWindows();
        LoadSavedTasks();

        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _drainTimer.Tick += (_, _) => DrainCaptureQueue();
        _drainTimer.Start();
        Closed += (_, _) => { _mouseCapture.Stop(); _scheduler.Dispose(); };
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
        var root = snap.Nodes[snap.RootId];
        var rootVm = TreeNodeViewModel.FromNode(root);
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
                var cvm = TreeNodeViewModel.FromNode(cn);
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
        _capturing = !_capturing;
        if (_capturing)
        {
            _overlay ??= new HighlightOverlayWindow();
            _mouseCapture.Start();
            _mouseCapture.ResultProduced += OnCaptureResult;
            BtnMouseCapture.Content = "停止捕获";
            TxtCaptureStatus.Text = "捕获中：移动鼠标到目标控件上…";
        }
        else
        {
            _mouseCapture.ResultProduced -= OnCaptureResult;
            _mouseCapture.Stop();
            _overlay?.HideOverlay();
            BtnMouseCapture.Content = "随鼠标捕获";
            TxtCaptureStatus.Text = "";
        }
    }

    private void OnCaptureResult(MouseCaptureResult result)
    {
        _captureQueue.Enqueue(result);
    }

    private void DrainCaptureQueue()
    {
        while (_captureQueue.TryDequeue(out var result))
        {
            if (!result.IsValid)
            {
                _overlay?.HideOverlay();
                continue;
            }

            // 无论树是否已加载，先画蓝框：让用户直观看到当前选中的元素
            UpdateOverlay(result);

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

    /// <summary>把高亮蓝框移动到捕获元素矩形处（UIA 物理像素 → DIP 换算）。</summary>
    private void UpdateOverlay(MouseCaptureResult result)
    {
        if (_overlay is null || result.BoundingRectangle is not System.Drawing.Rectangle rect) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _overlay.MoveTo(rect, dpi.DpiScaleX, result);
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

    // ========== 任务管理 ==========
    private void CmbTargetKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbTargetKind.SelectedItem is ComboBoxItem cbi)
            TxtTargetValue.Text = (string)cbi.Tag switch
            {
                "Process" => "notepad",
                "WindowHandle" => "",
                "WindowTitle" => "",
                _ => TxtTargetValue.Text,
            };
    }

    private void BtnAddTask_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtTargetValue.Text))
        {
            TxtStatus.Text = "请填写目标值";
            return;
        }

        var config = new MonitorTaskConfig
        {
            Name = string.IsNullOrWhiteSpace(TxtTaskName.Text) ? "任务" : TxtTaskName.Text.Trim(),
            Target = new MonitorTarget
            {
                Kind = ParseTargetKind(),
                Value = TxtTargetValue.Text.Trim(),
            },
            Properties = TxtProperties.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Timer = new TimerSpec { IntervalMs = int.TryParse(TxtInterval.Text, out var iv) ? Math.Max(50, iv) : 1000 },
            OutputDirectory = OutputDir,
        };

        // 过滤：支持 Name~pattern 语法
        var filterText = TxtFilter.Text.Trim();
        if (!string.IsNullOrEmpty(filterText))
        {
            var rule = new FilterRule { Property = "Name", Pattern = filterText };
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
            "ValueChange" => new ValueChangeTriggerSpec { Property = "Value" },
            "Threshold" => new ThresholdTriggerSpec { Property = "Value", Threshold = 0, Op = ComparisonOp.GreaterOrEqual },
            _ => new ImmediateTriggerSpec(),
        };

        // 时间器
        if (double.TryParse(TxtTimeBox.Text, out var sec) && sec > 0)
            config.TimeBox = new TimeBoxSpec { Boxes = new[] { new DurationTimeBoxSpec { Seconds = sec } } };

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
        TxtStatus.Text = $"任务 [{config.Name}] 已启动";
    }

    private TargetKind ParseTargetKind()
    {
        var tag = (CmbTargetKind.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "WindowHandle" => TargetKind.WindowHandle,
            "WindowTitle" => TargetKind.WindowTitle,
            _ => TargetKind.Process,
        };
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
