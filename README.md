# ui-tree-monitor — UI 结构树监控平台

Windows 桌面应用：基于 Microsoft UI Automation (UIA)，读取任意 Windows 应用的可访问性结构树，对任意元素的属性/数值创建**多任务并发监控**，支持**示例标记圈选提取多值**、实时落盘 CSV、交互式数据图导出。数据获取完全走 UIA 官方能力，**无需底层协议（Win32 消息、Hook、驱动等）**。

基于微软开源工程 [accessibility-insights-windows](https://github.com/microsoft/accessibility-insights-windows)（其采集内核为 NuGet 包 `Axe.Windows 2.4.2`）与 UIA COM 接口重构，语言 **C# / .NET 8 / WPF**，界面**仅中文**。可一键打包为**免安装 .NET 的独立单文件 exe** 分发。

---

## 功能特性

### 采集与定位
- **窗口枚举**：列出当前系统所有可见顶层窗口（进程名 / 标题 / PID / 句柄）。
- **结构树抓取**：一键抓取目标窗口完整 UIA 结构树（类型 / 名称 / AutomationId / ClassName / 进程等）。
- **随鼠标捕获（常用）**：复用 accessibility-insights-windows 原生捕获技术（Axe.Windows `SelectAction`），开启后移动鼠标自动加载光标下窗口的结构树，并在目标元素上绘制**原版同款蓝色高亮框**（#0C5186 + 悬浮信息卡），左侧树黄色高亮对应节点。
- **Details 面板**：左下方实时显示选中元素的 **Properties**（17 项直接属性）与 **Patterns**（Value / RangeValue / Toggle 等 Pattern 子属性），**右键任意属性 →「添加为监控对象」**即可创建任务。

### 监控任务
- **多任务并发**：每任务独立 STA 线程（可选进程隔离），任务列表实时显示当前值 / 采样数 / 状态 / 上次采样时间。
- **任务生命周期**：**「确定添加」仅创建不启动**（状态 Idle）→ 右键任务行「**启动采集**」开始（限时任务从真正启动起算）→「停止采集」/「继续采集」/「移除任务」（移除自动落盘剩余数据）。
- **监控目标下拉**：添加弹窗中可从当前元素的 Properties / Patterns 全部属性中下拉切换目标，已圈选的提取规则**自动标记迁移**到新值。
- **重名保护**：任务名重复在弹窗内提示且**不关闭弹窗**（保留已编辑内容）。
- **七要素模型**：目标 / 属性 / 过滤（正则）/ 定时器（间隔）/ 触发器（立刻 / 定时 / 值变化 / 阈值 / 跨树联动）/ 时间器（时长 / 时间窗）/ 结束条件。
- **落盘策略（二选一）**：按点保存（每 N 点写盘）或按时间保存（每 T 秒 / 分钟 / 小时写盘），内存缓冲批量落盘。

### 值提取（核心特色）
在示例值上用标记（默认 `@@`，可设置）**圈选要提取的部分**，无需写正则：

| 规则行示例（示例值 601.582） | 提取结果 | 说明 |
|---|---|---|
| `Value = "@@601.582@@"` | `601.582` | 圈住全部 |
| `Value = "@@601@@.582"` | `601` | 整数部分 |
| `Value = "@@6@@01.582"` | `6` | 按圈取位（1 位） |
| `Value = "@@66@@1.582"` | `66` | 按圈取位（2 位） |
| `Value = "@@Value @@="601.582""` | `Value ` | 文本同样可采集 |

- **锚点数字模糊化**：示例中的数字视为占位符（`@@603@@.561` 对实际值 `605.123` 仍提取 `605`），数值波动不失配。
- **按圈取位**：圈住 N 位就取 N 位（交界两侧都是数字时以圈住位数为下限自适应）。
- **多条规则 → 值1, 值2, 值3…**：任务列表当前值列、CSV 列均按此展开。
- **类型自动识别**：数值 / 文本（布尔按文本采集），规则行预览实时显示 `提取预览：601（数值）`；仅数值列可勾选绘制数据图。
- **高级模式**：`re:(\d+\.?\d*)` 手写正则，第一捕获组优先。

### 数据导出
- **实时落盘 CSV**（行式：`监控对象,值1..值N,时间戳`）按保存策略批量写出。
- **生成数据图**（右键任务）：弹窗勾选要绘制的数值线、**自定义图例文字**，渲染**一图多线 PNG**（统一时间轴、Y 轴 5 段刻度、8 色系列、单点画圆点）。
- **HTML 报告内嵌交互式走势图**（原生 JS + Canvas，离线可用）：**点击图例显示/隐藏任意曲线，Y 轴自动适配可见曲线范围**，高分屏适配、窗口缩放自动重绘；非数值列图例置灰。
- 同目录导出 CSV / JSON / HTML / chart.png 完整报告包。

---

## 运行

### 开发运行（重要：dotnet 路径）

本机 .NET SDK 安装在 **`D:\dotnet`**（8.0.427）。PATH 中 `C:\Program Files\dotnet` 只有运行时、**没有 SDK**，直接 `dotnet` 会报 `No .NET SDKs were found`：

```powershell
# 方式一：双击运行（推荐）
.\run.cmd

# 方式二：指定 SDK 路径运行
D:\dotnet\dotnet.exe run --project src\UiTreeMonitor.App

# 方式三：直接运行已构建产物
.\src\UiTreeMonitor.App\bin\Debug\net8.0-windows\UiTreeMonitor.App.exe
```

### 打包发布（独立单文件 exe）

双击 **`publish.cmd`**（或 `powershell -ExecutionPolicy Bypass -File publish.ps1`），自动完成：

- `dotnet publish` Release / win-x64 / **self-contained single-file**（含压缩与原生库内嵌）；
- 产物：**`dist\UiTreeMonitor.exe`**（约 70 MB，**目标机无需安装 .NET，双击即用**）与 `dist\UiTreeMonitor_win-x64_<时间戳>.zip` 分发包；
- 脚本自动定位带 SDK 的 dotnet、停止运行中的实例防止文件占用；也支持 `-Runtime win-arm64` 参数换目标平台。

应用图标（结构树 + 波形，程序化绘制于 `src\UiTreeMonitor.App\Assets\make-icon.ps1`，可改样式重新生成）。

### 使用流程

1. **定位元素**：顶部下拉选目标窗口 →「抓取结构树」；或点「随鼠标捕获」把鼠标移到目标控件（蓝色高亮框出现），点击后 Details 面板锁定该元素。
2. **添加监控**：Details 面板右键属性（Properties 行或 Patterns 子项）→「添加为监控对象」→ 弹窗中下拉选监控目标、在示例值中「插入标记」圈出要提取的部分（预览实时显示提取值与类型）、设间隔 / 时长 / 保存策略 →「确定添加」（任务创建但不启动）。
3. **启动采集**：任务管理列表右键任务 →「**启动采集**」；当前值列实时显示 值1, 值2…。
4. **看历史**：切到「采样历史」页查看实时采样记录。
5. **生成数据图**：右键任务 →「生成数据图」→ 勾选曲线（可改图例）→ 导出 CSV / JSON / HTML（内嵌交互图）/ PNG 完整报告包。

### 数据与配置位置

| 内容 | 位置 |
|------|------|
| 采样 CSV（实时落盘）与导出报告 | `D:\uitreemonitor\{yyyyMMdd}\{任务名}_{HHmmss}\` |
| 全局设置（截取标记等） | `%USERPROFILE%\.ui-tree-monitor\settings.json` |
| 任务配置存档 | `%USERPROFILE%\.ui-tree-monitor\tasks` |

---

## 被测测试面板 test_ui（Python）

`test_ui\test_ui.py` 是一个**用于被监测的测试界面**（检测数据展示面板），专供监控工具抓取验证：

- 结构树按检测软件层级设计：**组1 → 组2 → 组3 → 组4** 层层嵌套（UIA 中为嵌套 Group），最深层含状态项；
- 右侧参数 **深度/μm、峰值坐标、最大峰值** 数值每 **500ms 随机波动**；
- 基于 **PySide6（Qt6）**：Qt 在 Windows 有完整 UIA 桥，数值控件为只读 `QLineEdit`，UIA 中暴露为 **Edit 控件，Value 属性 = 实时数值**；
- 中文界面。

```powershell
cd test_ui
uv sync          # 首次运行安装 PySide6
uv run test_ui.py
```

> 注意：PySide6 声明在 `test_ui\pyproject.toml`，**不要**在根目录直接 `uv run .\test_ui\test_ui.py`（uv 找不到依赖会报 `ModuleNotFoundError`）。

打开面板后，「随鼠标捕获」指向任意参数数字框 → 右键 Value →「添加为监控对象」→ 规则行示例值中圈出数值部分（如 `Value = "@@601.582@@"`）→ 确定添加 → 右键「启动采集」即可持续采集波动数值。

---

## 测试

```powershell
D:\dotnet\dotnet.exe test
```

现有 **30 个单元测试**覆盖：值提取（标记圈选 / 锚点数字模糊 / 按圈取位 / 多规则 / 正则模式 / 特殊字符转义）、正则过滤（含命名组提取）、触发器（立刻 / 定时 / 值变化 / 阈值）、时间器（时长到期）、目标解析。

---

## 工程结构

```
E:\ui-tree-monitor\
├── UiTreeMonitor.sln
├── run.cmd                        # 开发一键启动（固定用 D:\dotnet\dotnet.exe，先结束旧实例）
├── publish.cmd / publish.ps1      # 打包脚本：独立单文件 exe → dist\
├── src\
│   ├── UiTreeMonitor.Core\        # 采集内核：窗口枚举、鼠标捕获、UIA 会话、树采样、
│   │   │                          #   正则过滤、值提取器 ValueExtractor、七要素任务模型
│   │   └── lib\Interop.UIAutomationClient.dll   # UIA COM 互操作（随工程分发）
│   ├── UiTreeMonitor.Hosting\     # 宿主抽象：线程宿主（默认）/ 进程宿主（隔离）
│   ├── UiTreeMonitor.Scheduler\   # 任务调度器：注册（可仅注册不启动）/启停/跨树值引用
│   ├── UiTreeMonitor.Persistence\ # 设置 settings.json + 采样 CSV 批量落盘器
│   ├── UiTreeMonitor.Export\      # 导出：CSV / JSON / HTML（内嵌交互式 JS 走势图）
│   ├── UiTreeMonitor.App\         # WPF 桌面界面（仅中文；数据图渲染器 + 应用图标）
│   │   └── Assets\                # app.ico / app_icon_1024.png / make-icon.ps1（图标生成）
│   ├── UiTreeMonitor.A11ySharedUx\    # 原版悬停高亮链路源码移植（Highlighter 等）
│   ├── UiTreeMonitor.A11yCommonUx\    # 原版公共控件移植
│   └── UiTreeMonitor.A11yWin32\       # 原版 Win32 工具移植
├── test_ui\                       # 被测测试面板（Python/PySide6）：嵌套组 + 实时波动参数
├── tests\UiTreeMonitor.Tests\     # xUnit 单元测试（30 个）
├── dist\                          # 打包产物（.gitignore 排除）
├── accessibility-insights-windows\  # 外部开源工程（已 .gitignore 排除，仅作参考）
└── 重构设计方案.md                # 需求分析与重构方案（已确认）
```

### 分层依赖

```
App (WPF) → Scheduler → Hosting → Core
         → Persistence → Core
         → Export → Core
         → A11ySharedUx → A11yWin32 / A11yCommonUx
Tests → Core / Scheduler / Persistence / Export
```

---

## 设计要点

- **采集内核**：`Axe.Windows`（netstandard2.0）用于属性读取模型（`A11yElement`）与鼠标捕获内核（`SelectAction`/`GetDataAction`，与原版 accessibility-insights-windows 完全一致）；树遍历用 UIA COM `CUIAutomation` 的 `ControlViewWalker`。
- **鼠标捕获**：直接调用原版 `SelectAction.GetDefaultInstance()`；高亮框复用原版配色（边框 #0C5186、信息卡浅黄底），点击穿透、不抢焦点。
- **值提取语义**：规则行预填监控属性当前值作示例，用户插标记圈选 → 编译为锚点正则（锚点数字段模糊为 `\d+` 通配；数字对数字交界以圈住位数为下限）；任务配置存储编译后的正则（自包含，改全局标记不影响已有任务）。
- **线程要求**：UIA COM 必须在 STA 线程使用——采集、捕获均在独立 STA 线程执行，UI 线程仅通过队列 + `DispatcherTimer` 消费结果。
- **批量落盘**：采样进内存缓冲，后台线程按「每 N 点或每 T 秒先到先写」批量写 CSV，不占 UI/采集线程；任务结束 / 移除 / 导出前自动 Flush。
- **交互式报告**：HTML 报告走势图为原生 JS + Canvas（无外部依赖、离线可用），数据以 JSON 内嵌，图例点击显隐 + Y 轴自适应。
- **正则安全**：过滤正则编译带 1 秒超时，防灾难性回溯；用户正则非法时该列置空不影响其他列。
- **任务生命周期**：每任务独立线程，`State` = Idle（未启动）/ Running / Paused / Completed / Stopped / Error；限时任务从真正启动起算。
- **git 提示**：`accessibility-insights-windows` 已从版本库排除（.gitignore + gitlink 移除），仅作本地参考。

---

## 更新记录

- 2026-10-28：按已确认方案完成 C#/.NET 8 + WPF 重构（删除原 Python 工程）；实现采集内核、七要素任务模型、线程/进程宿主、调度器、持久化、导出、桌面 UI；鼠标捕获改用 accessibility-insights-windows 原生 `SelectAction` 技术（蓝色高亮框 + 悬浮信息卡）。
- 2026-10-29：Details 面板（Properties/Patterns）右键添加监控；值提取规则行示例交互（标记圈选 / 锚点数字模糊化 / 按圈取位 / 类型自动识别）；多规则多值（值1..值N）与行式 CSV 落盘（按点/按时间策略）；任务默认不启动 + 右键启动采集；重名弹窗内提示不关窗；监控目标下拉 + 标记迁移；报告走势图 PNG（选线/自定义图例）与 HTML 交互式走势图（图例显隐 + Y 轴自适应）；应用图标与独立单文件 exe 打包脚本；单元测试扩充至 30 个。
