# ui-tree-monitor — UI 结构树监控平台

Windows 桌面应用：基于 Microsoft UI Automation (UIA)，读取任意 Windows 应用的可访问性结构树，按**七要素**配置监控任务，多任务并发采集属性/数值变化，支持正则过滤、鼠标捕获、数据导出。数据获取完全走 UIA 官方能力，**无需通过底层协议（Win32 消息、Hook、驱动等）**。

基于微软开源工程 [accessibility-insights-windows](https://github.com/microsoft/accessibility-insights-windows)（其采集内核为 NuGet 包 `Axe.Windows 2.4.2`）与 UIA COM 接口重构，语言 **C# / .NET 8 / WPF**，界面**仅中文**。

---

## 功能特性

- **窗口枚举**：列出当前系统所有可见顶层窗口（进程名 / 标题 / PID / 句柄）。
- **结构树抓取**：一键抓取目标窗口完整 UIA 结构树（类型 / 名称 / AutomationId / ClassName / 进程等）。
- **随鼠标捕获（常用功能）**：**直接复用 accessibility-insights-windows 的捕获技术**（Axe.Windows `SelectAction` 鼠标选择内核），开启后移动鼠标，自动加载光标下窗口的结构树，并在**目标窗口元素上绘制原版同款蓝色高亮框**（#0C5186 + 悬浮信息卡，显示类型/名称/聚焦状态），同时左侧树黄色高亮对应节点；无需手动选窗口。
- **七要素监控任务**：每个任务独立配置以下要素，可多任务并发：
  1. **目标**：进程名 / 窗口句柄 / 窗口标题
  2. **属性**：Name / Value / RangeValue / ToggleState / IsEnabled 等 17 项
  3. **过滤**：正则表达式（包含性 + 命名组提取）
  4. **定时器**：采集间隔（毫秒）
  5. **触发器**：立刻 / 定时 / 值变化 / 阈值 / 跨树联动
  6. **时间器**：运行时长 / 到指定时刻 / 时间窗口 / 间歇循环
  7. **结束条件**：定时长 / 采样数 / 阈值 / 值变化或不变 / 永不
- **并发模型**：默认**线程模式**（每任务独立 STA 线程）；可选**进程模式**（独立 worker 进程硬隔离）。
- **数据导出**：CSV + JSON + HTML 三格式报告。
- **免底层协议**：数据获取完全依赖 UIA 的控件属性 / 值模式（Value、RangeValue、Toggle 等）。

### 可监控属性

| 属性 | 说明 | 读取方式 |
|------|------|----------|
| `Name` | 控件名称 | 直接属性 |
| `Value` | 文本值（文本框等） | ValuePattern |
| `RangeValue` | 数值（滑块、进度条等） | RangeValuePattern |
| `ToggleState` | 开关状态 | TogglePattern |
| `IsSelected` | 选中状态 | SelectionItemPattern |
| `ExpandCollapseState` | 展开/折叠状态 | ExpandCollapsePattern |
| `IsEnabled` / `IsOffScreen` | 可用 / 屏幕外状态 | 直接属性 |
| `AutomationId` / `ClassName` / `Framework` / `ProcessId` | 身份与归属 | 直接属性 |
| `BoundingRectangle` / `ItemStatus` / `HelpText` / `AccessKey` | 几何与辅助信息 | 直接属性 |

---

## 运行

```powershell
# 前置：.NET 8 SDK（本机已装于 D:\dotnet，已入 PATH）
dotnet run --project src\UiTreeMonitor.App
```

或直接运行已构建产物：

```powershell
.\src\UiTreeMonitor.App\bin\Debug\net8.0-windows\UiTreeMonitor.App.exe
```

### 使用流程

1. **选窗口**：顶部下拉框选目标窗口 → 「刷新窗口」→ 「抓取结构树」。
2. **鼠标捕获**：点「随鼠标捕获」，移动鼠标到目标控件，左侧树自动加载并高亮。
3. **建任务**：右侧「任务管理」填七要素 → 「添加并启动任务」。
4. **看历史**：切到「采样历史」页，选任务查看实时采样记录。
5. **导出**：选中任务 → 「导出选中任务报告」，生成 CSV / JSON / HTML 到 `%USERPROFILE%\.ui-tree-monitor\exports`。

### 配置与产物位置

| 内容 | 位置 |
|------|------|
| 任务配置存档 | `%USERPROFILE%\.ui-tree-monitor\tasks` |
| 采样历史 CSV（实时追加） | `%USERPROFILE%\.ui-tree-monitor\exports` |
| 导出报告（CSV/JSON/HTML） | `%USERPROFILE%\.ui-tree-monitor\exports\{任务名}_{时间戳}` |

---

## 测试

```powershell
dotnet test
```

现有 11 个单元测试覆盖：正则过滤（含命名组提取）、触发器（立刻/定时/值变化/阈值）、时间器（时长到期）、目标解析。

---

## 工程结构

```
E:\ui-tree-monitor\
├── UiTreeMonitor.sln
├── src\
│   ├── UiTreeMonitor.Core\        # 采集内核：窗口枚举、鼠标捕获、UIA 会话、树采样、
│   │   │                          #   正则过滤、七要素任务模型（Trigger/Timer/TimeBox/StopCond/MonitorTask）
│   │   └── lib\Interop.UIAutomationClient.dll   # UIA COM 互操作（随工程分发）
│   ├── UiTreeMonitor.Hosting\     # 宿主抽象：线程宿主（默认）/ 进程宿主（隔离）
│   ├── UiTreeMonitor.Scheduler\   # 任务调度器：注册/启停/跨树值引用
│   ├── UiTreeMonitor.Persistence\ # 任务配置 JSON 存档 + 采样 CSV 落盘
│   ├── UiTreeMonitor.Export\      # 导出：CSV / JSON / HTML 报告
│   └── UiTreeMonitor.App\         # WPF 桌面界面（仅中文）
├── tests\UiTreeMonitor.Tests\     # xUnit 单元测试
├── accessibility-insights-windows\  # 外部开源工程（已 .gitignore 排除，仅作参考）
└── 重构设计方案.md                # 需求分析与重构方案（已确认）
```

### 分层依赖

```
App (WPF) → Scheduler → Hosting → Core
         → Persistence → Core
         → Export → Core
Tests → Core / Scheduler / Persistence / Export
```

---

## 设计要点

- **采集内核**：`Axe.Windows`（netstandard2.0）用于属性读取模型（`A11yElement`）与鼠标捕获内核（`SelectAction`/`GetDataAction`，与原版 accessibility-insights-windows 完全一致）；树遍历用 UIA COM `CUIAutomation` 的 `ControlViewWalker`，避免 Axe.Windows 私有构造的不可用路径。
- **鼠标捕获**：直接调用原版 `SelectAction.GetDefaultInstance()`（`IsMouseSelectOn`/`IntervalMouseSelector`/`Select`/`SelectedElementContextId`）→ `GetDataAction.GetElementContext(ecId).Element` 取回选中元素；高亮框复用原版配色（边框 #0C5186、信息卡浅黄底），覆盖在目标元素矩形上，点击穿透、不抢焦点。
- **线程要求**：UIA COM 必须在 STA 线程使用——采集、捕获均在独立 STA 线程执行，UI 线程仅通过队列 + `DispatcherTimer` 消费结果。
- **正则安全**：所有过滤正则编译时带 1 秒超时，防灾难性回溯。
- **任务生命周期**：每任务独立线程，`State` = Idle / Running / Paused / Completed / Stopped / Error。
- **git 提示**：`accessibility-insights-windows` 已从版本库排除（.gitignore + gitlink 移除），仅作本地参考。

---

## 更新记录

- 2026-10-28：按已确认方案完成 C#/.NET 8 + WPF 重构（删除原 Python 工程）；实现采集内核、七要素任务模型、线程/进程宿主、调度器、持久化、导出、桌面 UI；11 项单元测试通过。
- 2026-10-28：鼠标捕获改用 accessibility-insights-windows 原生 `SelectAction` 捕获技术；新增目标窗口上的蓝色高亮框（#0C5186）+ 悬浮信息卡（类型/名称/聚焦状态），与左侧树黄色高亮联动。
