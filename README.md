# ui-tree-monitor — Windows UI 结构树监控工具

基于 Microsoft UI Automation (UIA) 的 Windows 桌面应用：读取任意 Windows 应用的可访问性结构树，对任意元素的属性/数值创建**多任务并发监控**，在示例值上**圈选标记即可提取多值**（免写正则），实时落盘 CSV，一键导出**交互式数据走势图**报告。

- 数据获取完全走 UIA 官方能力，**不依赖底层协议**（Win32 消息、Hook、驱动）
- 界面**仅中文**；C# / .NET 8 / WPF
- 采集内核基于微软开源工程 [accessibility-insights-windows](https://github.com/microsoft/accessibility-insights-windows)（`Axe.Windows 2.4.2`），鼠标捕获与高亮框与原版一致

## 功能特性

### 采集与定位
- **窗口枚举**：列出当前系统所有可见顶层窗口（进程名 / 标题 / PID / 句柄）
- **结构树抓取**：一键抓取目标窗口完整 UIA 结构树（类型 / 名称 / AutomationId / ClassName 等）
- **随鼠标捕获**：开启后移动鼠标，自动加载光标下窗口的结构树，目标元素上绘制**蓝色高亮框 + 悬浮信息卡**，左侧树同步**逐层展开到该节点 + 整行黄色高亮 + 滚动到位**——无需手动选窗口
- **捕获即得结构位置**：每次捕获都会在状态栏与 Details 标题显示 **`路径=0/4/1/3`**（结构索引路径，与任务/脚本里的 `ElementPath` 同一套编码，可直接复制复用）。定位顺序为 **RuntimeId（元素身份证，精确）→ 矩形完全相等 → 矩形 8px 容差 → 精确属性**；`via=runtime` 表示捕获元素与树节点是同一个元素，路径可信；其它方式会在状态栏标注「建议刷新结构树」
- **树过期自愈**：定位落空且树已过期时，同一进程下自动后台重抓一次（3 秒节流），抓完自动补定位并展开，不用手动点「抓取结构树」
- **结构树关键词检索**：树上方搜索框按 **Name / AutomationId / ClassName / ControlType** 检索（空格分隔多关键词取「同时满足」），命中的节点浅绿标记并显示 `当前/总数`，`Enter` / 「下一个」循环跳转、`Shift+Enter` / 「上一个」反向跳转、`Esc` 或「清空」复位；抓树后若检索框有内容会自动重算
- **Details 面板**：实时显示选中元素的 **Properties**（17 项）与 **Patterns**（Value / RangeValue / Toggle 等），**右键任意属性 →「添加为监控对象」**即可创建任务

### 监控任务
- **多任务并发**：每任务独立线程（可选进程隔离），任务列表实时显示当前值 / 采样数 / 状态
- **任务生命周期**：「确定添加」仅创建不启动 → 右键「启动采集」开始（限时任务从真正启动起算）→ 停止 / 继续 / 移除（移除自动落盘剩余数据）
- **监控目标下拉**：弹窗中可从当前元素的全部属性中切换目标，已圈选的提取规则**自动迁移**
- **重名保护**：任务名重复弹窗内提示，不丢失已编辑内容
- **完整任务模型**：目标 / 属性 / 过滤正则 / 采集间隔 / 触发器（立刻 / 定时 / 值变化 / 阈值 / 跨树联动）/ 运行时长 / 结束条件
- **落盘策略**：按点保存（每 N 点）或按时间保存（每 T 秒 / 分钟 / 小时），内存缓冲批量写盘

### 值提取（核心特色）

在示例值上用标记（默认 `@@`，可设置）**圈选要提取的部分**，无需写正则：

| 规则行示例（示例值 601.582） | 提取结果 | 说明 |
|---|---|---|
| `Value = "@@601.582@@"` | `601.582` | 圈住全部 |
| `Value = "@@601@@.582"` | `601` | 整数部分 |
| `Value = "@@6@@01.582"` | `6` | 按圈取位（1 位） |
| `Value = "@@66@@1.582"` | `66` | 按圈取位（2 位） |
| `Value = "@@Value @@=..."` | `Value ` | 文本同样可采集 |

- **锚点数字模糊化**：示例中的数字视为占位符（`@@603@@.561` 对实际值 `605.123` 仍提取 `605`），数值波动不失配
- **多条规则 → 值1, 值2, 值3…**：任务列表与 CSV 列按此展开
- **类型自动识别**：数值 / 文本，预览实时显示（如 `提取预览：601（数值）`）；仅数值列可绘图
- **高级模式**：`re:(\d+\.?\d*)` 手写正则，第一捕获组优先

### 数据导出
- **实时落盘 CSV**（行式：`监控对象,值1..值N,时间戳`）
- **生成数据图**：弹窗勾选要绘制的数值线、**自定义图例文字**，渲染一图多线 PNG
- **HTML 报告内嵌交互式走势图**（原生 JS + Canvas，离线可用）：**点击图例显示/隐藏任意曲线，Y 轴自动适配**，高分屏适配、窗口缩放自动重绘
- 同目录导出 CSV / JSON / HTML / PNG 完整报告包

## 下载与运行

### 方式一：下载发布包（推荐）

从本仓库 **Releases** 页面下载 `UiTreeMonitor_win-x64_*.zip`，解压后双击 `UiTreeMonitor.exe` 即可——**独立单文件、无需安装 .NET**。

> 系统要求：Windows 10 / 11 x64。

### 方式二：从源码运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```powershell
git clone <本仓库地址>
cd ui-tree-monitor
dotnet run --project src\UiTreeMonitor.App
```

Windows 下也可直接双击 `run.cmd`。

### 打包独立 exe

双击 `publish.cmd`（或 `powershell -ExecutionPolicy Bypass -File publish.ps1`），自动发布 self-contained 单文件并压缩，产物在 `dist\`（支持 `-Runtime win-arm64` 参数换平台）。

## 快速上手

1. **定位元素**：顶部下拉选目标窗口 →「抓取结构树」；或点「随鼠标捕获」，把鼠标移到目标控件（蓝色高亮框出现），点击后 Details 面板锁定该元素
2. **添加监控**：Details 面板右键属性 →「添加为监控对象」→ 弹窗中下拉选监控目标、在示例值中点「插入标记」圈出要提取的部分（预览实时显示提取值与类型）、设间隔 / 时长 / 保存策略 →「确定添加」
3. **启动采集**：任务列表右键任务 →「**启动采集**」，当前值列实时显示 值1, 值2…
4. **看历史**：切到「采样历史」页
5. **生成数据图**：右键任务 →「生成数据图」→ 勾选曲线（可改图例）→ 导出完整报告包

### 数据与配置位置

| 内容 | 位置 |
|------|------|
| 采样 CSV 与导出报告 | `D:\uitreemonitor\{yyyyMMdd}\{任务名}_{HHmmss}\` |
| 全局设置（截取标记等） | `%USERPROFILE%\.ui-tree-monitor\settings.json` |

## 从源码构建与测试

```powershell
dotnet build
dotnet test     # 30 个单元测试：值提取（圈选/模糊化/按圈取位/正则）、过滤、触发器、时间器、目标解析
```

### 项目结构

```
ui-tree-monitor\
├── src\
│   ├── UiTreeMonitor.Core\        # 采集内核：窗口枚举、鼠标捕获、UIA 会话、树采样、
│   │                              #   正则过滤、值提取器 ValueExtractor、任务模型
│   ├── UiTreeMonitor.Hosting\     # 宿主抽象：线程宿主（默认）/ 进程宿主（隔离）
│   ├── UiTreeMonitor.Scheduler\   # 任务调度器：注册（可仅注册不启动）/启停/跨树值引用
│   ├── UiTreeMonitor.Persistence\ # 设置 settings.json + 采样 CSV 批量落盘器
│   ├── UiTreeMonitor.Export\      # 导出：CSV / JSON / HTML（内嵌交互式走势图）
│   ├── UiTreeMonitor.App\         # WPF 桌面界面（数据图渲染器 + 应用图标）
│   ├── UiTreeMonitor.A11ySharedUx\    # 原版悬停高亮链路源码移植
│   ├── UiTreeMonitor.A11yCommonUx\    # 原版公共控件移植
│   └── UiTreeMonitor.A11yWin32\       # 原版 Win32 工具移植
├── tests\UiTreeMonitor.Tests\     # xUnit 单元测试
├── run.cmd                        # 开发一键启动
└── publish.cmd / publish.ps1      # 打包脚本：独立单文件 exe → dist\
```

### 技术实现要点

- **采集内核**：`Axe.Windows` 提供属性读取模型与鼠标捕获内核（`SelectAction` / `GetDataAction`）；树遍历用 UIA COM `CUIAutomation` 的 `ControlViewWalker`
- **值提取语义**：标记圈选编译为锚点正则——锚点数字段模糊为 `\d+` 通配，数字对数字交界以圈住位数为下限；任务保存编译后的正则，自包含不受全局标记设置影响
- **线程模型**：UIA COM 仅在独立 STA 线程使用，UI 线程通过队列 + `DispatcherTimer` 消费结果
- **批量落盘**：采样进内存缓冲，后台线程批量写 CSV；任务结束 / 移除 / 导出前自动 Flush
- **正则安全**：编译带 1 秒超时防灾难性回溯；用户正则非法时仅该列置空，不影响其他列

## 被测测试面板 test_ui（Python）

`test_ui\` 提供两个**用于被监测的测试界面**（检测数据展示面板），专供本工具与 Accessibility Insights 抓取验证：

| 脚本 | 数值承载 | UIA 暴露 | 适用验证场景 |
|---|---|---|---|
| `test_ui.py` | 只读 `QLineEdit` | **Edit 控件，Value 属性 = 实时数值** | 捕获 Value 属性（`IsReadOnly=True`，SetValue 会被拒绝属预期） |
| `test_ui_name.py` | `QLabel` | **Text 控件，Name 属性 = 实时数字** | 捕获 Name 属性持续变化（模拟真实检测软件 QLabel 读数） |

两者结构一致：**组1 → 组2 → 组3 → 组4** 层层嵌套（UIA 嵌套 Group），右侧 **深度/μm、峰值坐标、最大峰值** 每 **500ms** 随机波动；`test_ui_name.py` 另含顶部大号读数与「输入数值…」按钮（点击弹出数字输入框 QDoubleSpinBox，确定后写入顶部读数并同步 Name）。基于 PySide6（Qt6）——Tkinter 的 UIA/MSAA 桥有缺陷（Label 文本不暴露、Entry 不暴露为 Edit），故被测面板使用 Qt。

```powershell
cd test_ui
uv sync                       # 首次运行安装 PySide6
uv run test_ui.py             # 或 test_ui_name.py（Name 动态版）

# 从仓库根目录运行：
uv run --project .\test_ui .\test_ui\test_ui_name.py
```

> 注意：PySide6 声明在 `test_ui\pyproject.toml`，**不要**在根目录直接
> `uv run .\test_ui\test_ui.py`（uv 会在根目录找依赖声明而找不到 PySide6）。

打开面板后，用「随鼠标捕获」指向数字控件即可看到结构树与蓝色高亮框；Details 面板中 Edit 的 **Value**（或 Text 的 **Name**）即为实时数值，右键「添加为监控对象」即可建任务采集。

## 致谢

- [accessibility-insights-windows](https://github.com/microsoft/accessibility-insights-windows)（MIT License）——鼠标捕获与高亮技术来源
- [Axe.Windows](https://www.nuget.org/packages/Axe.Windows)（MS-PL）

## 更新记录

- 2026-10-28：发布 C#/.NET 8 + WPF 重构版：采集内核、任务模型、线程/进程宿主、调度器、持久化、导出、桌面 UI；鼠标捕获采用 accessibility-insights-windows 原生 `SelectAction` 技术
- 2026-10-29：Details 面板右键添加监控；标记圈选值提取（锚点数字模糊化 / 按圈取位 / 类型自动识别）；多规则多值与批量落盘；任务默认不启动 + 右键启动；报告走势图 PNG 与 HTML 交互式走势图；应用图标与独立单文件 exe 打包
- 2026-11-17：结构树关闭虚拟化（容器真实生成），修复「随鼠标捕获」在深层节点上只高亮不展开的问题；高亮改为整行底色 + 加粗 + 左边框并自动选中/滚动；结构树新增关键词检索（多关键词 AND、命中计数、上一个/下一个、Esc 清空）
- 2026-11-17：捕获定位改为「结构位置唯一确定」：树节点采集 UIA RuntimeId，捕获先按 RuntimeId 精确命中再算结构索引路径（`路径=0/4/1/3`），矩形/属性仅作兜底并在状态栏标注匹配方式；同进程定位落空自动重抓树（3s 节流）。实测 44 节点 RuntimeId 覆盖 100%，矩形偏移 40px 仍精确命中原路径
- 2026-11-17：修复结构树「全是无名称」——根因是 `A11yElement.Name` 取的是 Axe.Windows 缓存字段，对 Chromium 子元素几乎不填充（实测 msedge 242 节点仅 11% 有值）；改为直读 UIA 实时属性（`CurrentName` / `CurrentAutomationId` / `CurrentClassName` / `CurrentControlType`），覆盖率升到 79%（余下为真无名的容器），同时纠正了「树里显示 Button、实际是 MenuItem」的类型错位。新增进程树缓存（12 秒 / 最多 4 个进程）：在几个应用间来回移动鼠标时直接复用树，不必每次重等 UIA 抓树（Chromium 240 节点约 2.9 秒）
- 2026-11-17：抓树提速 10 倍——瓶颈是 `new DesktopElement(..., setMembers: true)` 每节点约 10ms（msedge 190 节点批量抓树 2699ms）；新增 `TreeSampler.CaptureWindowFast`，直接用 UIA COM walker 遍历并只读必要属性，同窗口 **269ms**，Name / RuntimeId 结果与旧路径完全一致。鼠标捕获跟随改走快速路径；监控任务仍用带 Pattern 的完整路径
- 2026-11-17：修复 Actions 执行报 `0x80131509`——捕获元素的 `A11yElement` 由 SelectAction 的 POI context 持有，下一次捕获会替换并清理它，缓存里的 Pattern 引用随之失效；改为执行前用元素矩形中心 `ElementFromPoint` 重取实时元素并按 Pattern 名匹配（RuntimeId / 矩形校验同一元素），失败时把 UIA 错误码翻成可操作的中文提示。树节点选中时也会用同样方式把快照节点「复活」成实时元素，Details 的 Patterns 与 Actions 随之可用
- 2026-11-17：抓树串行化（切应用时多个捕获结果并发抓树会互相覆盖，表现就是「第一次反应不过来、要试一两次」）；抓树完成后只在鼠标仍停在该元素矩形内时才补高亮，避免高亮跳到用户已离开的元素
- 2026-11-17：录制端改为**直接读元素实际支持的 Pattern 全集**（`GetSupportedPatterns()`），不再固定枚举 4 类。新增 `UiaPatternCatalog`：全量 32 个 UIA Pattern 清单 + 「点击语义」可执行动作优先级表（Invoke → LegacyIAccessible.DoDefaultAction → Toggle → SelectionItem.Select → ExpandCollapse.Expand/Collapse）。所有 pattern id 与 interop 的 `UIA_PatternIds` 常量逐项核对——此前手写 `10002` 当 ExpandCollapse，实际那是 `ValuePatternId`（真正的 ExpandCollapse 是 `10005`），导致「只有 ExpandCollapse 的菜单项」这条回放分支永远命中不到。录制记录里同时写入该元素的能力清单 `patterns=[…]`；回放坐标探测失败时改为报「元素 'X' 没有点击语义（支持: …）」，不再笼统说「无可 Invoke 元素」；Toggle 另记点击后状态
- 2026-11-17：修两处——①Patterns 树里**属性子行也长出 Actions 按钮**：`TreeView.ItemTemplate` 是全局模板，子属性行（`PropertyItem`）套用同一模板时 `HasActions` 绑定路径失败，WPF 回退到 Button 默认值 `Visible`，于是每一行都画按钮；改为按数据类型分模板（Pattern 行才有 Actions，并加 `FallbackValue=Collapsed`；属性行只显示「属性名 = 值」），顺带修掉「HorizontalScrollable　HorizontalScrollable = False」的名字重复。②回放坐标语义探测报 `0x80131509`（Operation is not valid due to the current state of the object）：原来整段一个大 try，Invoke 一抛异常就整体失败、后面 4 档根本没试；改为每档独立容错 + 瞬态错误（0x80131509）重取元素重试 3 次（间隔 80ms），失败信息改为列出该元素实际支持的 Pattern 清单与各档失败原因
- 2026-11-17：回放元素定位不稳（日志里 `ResolveBySpec: no match`，明明元素在也失配）——spec 评分原来读 `el.Name` / `el.AutomationId` 这类 **Axe 缓存字段**，对 Chromium 元素大面积为空，元素在也 score=0；改为「缓存优先、缺了用 `TreeSampler` 直读 COM」，避免每节点 4 次直读拖慢 3000 节点遍历。spec 同时新增 `rid=`（UIA RuntimeId，同一元素实例内唯一），回放优先按 RuntimeId 精确命中，不再受输入框 placeholder 变成输入内容这类变化影响
- 2026-11-17：步骤树加**层级序号**（根级 1、2、3，子级 1.1、1.1.1）；**回放与单步执行按颜色标注进度**——已执行完浅绿、当前执行中黄底加粗、失败浅红，标记按 `NodeId` 记录并在 `RefreshTree` 重建后还原（循环回绕、选中跳转都不会错色）
- 2026-11-17：「全部回放」运行中按钮变「■ 停止回放」，点击后立即显示「■ 停止中…」并临时禁用按钮（防连点又启动一轮），执行器每步前检查取消、停止后不再发起新动作；`Dispose` 改为只取消不释放 token，避免后台线程抛 `ObjectDisposedException` 被记成「失败」
- 2026-11-17：回放提速——元素定位每步都要做一次，原来走 `UiaSession` 的 `GetFirstChild/GetNextSibling`，**每返回一个节点都 `new DesktopElement(setMembers:true)`≈10ms**，Edge Copilot 树 229 节点就是 2.3 秒/步。新增 `Core/Uia/SpecResolver`：raw `CUIAutomation` + `ControlViewWalker` 直接遍历、只读 4 个必要属性（≈0.1ms/节点），命中后**只包装命中的那一个**元素，229 节点降到 ~0.2 秒；`RuntimeId` 优先命中保留
- 2026-11-17：单步执行看不到黄色——`BtnStep_Click` 在 UI 线程同步跑 `ExecuteOne`，阻塞期间界面不重绘，黄色来不及画就变绿。改为 `async void` + `MarkStep` 之后 `await Dispatcher.Yield(DispatcherPriority.Render)` 先出一帧，再执行动作；并统一规则：**每次点「全部回放」或「单步」都先清空上一轮颜色**，只把当前这一步标成黄色（单步不再累积历史绿）；`RevealNode` 只滚动到可视区、不选中该行（选中态容器高亮会盖掉进度色）
- 2026-11-17：步骤右键新增「**复制结果**」：复制该步骤最近一次执行的结果/错误文本（含序号、状态、元素名），Shift 多选时复制全部选中行，右键点中哪行就复制哪行。为此执行器改为**把执行结果写回 `StepNode.Action`**（原来只写日志，列表详情列永远是录制时的旧 Result），回放快照到达时同时 `RefreshDetail()` 刷新详情列
- 2026-11-17：录制浏览器操作**全部退化成坐标**的修复——点击时的竞态校验拿 `WindowFromPoint` 的句柄（Chromium 给的是**子窗口**）去比 UIA 元素向上爬到的**顶层**句柄，两者本来就不等，于是每次点击都被判成"窗口已关闭、元素在背后"而丢掉，整场录制只剩 `(coord:auto).Click`。改为各自 `GetAncestor(GA_ROOT)` 归一到顶层再比；元素完全拿不到句柄时退化为"同进程"判断。竞态防护本身保留（弹窗关掉后 FromPoint 落到别的顶层窗口仍会丢弃）
- 2026-11-17：回放坐标处命中**容器**时自动下钻——Chromium 弹出菜单的 `ElementFromPoint` 返回的是容器 `List`（支持 ScrollItem/TextChild，没有点击语义），直接报"无可执行语义"必然失败。现在命中 List/Menu/Pane/Group/Custom 这类容器时，沿"包含点击点"的子元素往下钻最多 6 层，由深到浅逐个试 5 档点击语义，真正被点的 MenuItem 才被命中
- 2026-11-17：切换软件后 Properties/Patterns「第一次刷不出来」的修复——两条原因：① `DrainCaptureQueue` 逐条处理积压的结果，会把若干帧之前的中间态刷出来；改为**只处理最新一条**。② 捕获结果只带 `ElementContext` id，而 context 会被下一次捕获替换/清理，取不到元素就直接跳过 → 面板停在上一软件的内容；现在取不到时按捕获矩形中心 `FromPoint` 重取一次（RuntimeId / 矩形相等校验），仍取不到则清空面板并把标题置为「正在识别…」；异步抓树完成后也会用最新悬停结果补刷一次
- 2026-11-17：弹出菜单项录成**空 spec** 的修复——`BuildSpec` 原来用 `elem.Current` 整包读属性，Chromium 弹出菜单项只要有一个属性读不到就整包抛异常，`catch` 后返回 `spec:v1|` 空壳，回放必然报「元素未找到: spec:v1|」。现在**逐属性独立读取**（AutomationId / Name / ControlType / ClassName / BoundingRectangle / RuntimeId 各自 try），任一属性失败不影响其余；若定位信息一条都读不到，直接改录**坐标延迟定位**，回放时按坐标探测实际语义（比录一条回放必死的空步强）
- 2026-11-17：回放 spec 定位增加**短重试**——弹出菜单/对话框是异步出现的，上一步刚 `Expand` 开菜单时，菜单项要 200~500ms 才进 UIA 树，单次探测会直接判"元素未找到"。现在命中即返回（零额外开销），未命中才重试 2 次 × 200ms
- 2026-11-17：元素定位 spec 改为**在点击瞬间抓取**——Chromium 弹出菜单项点完菜单就关，几百毫秒后再读该元素，`name`/`type`/`class`/`rect` 全空，只剩 `RuntimeId` 侥幸可读（实测 spec 变成 `autoId=|name=|type=|class=|rect=|rid=42.…`），回放必然"元素未找到"。现在 `OnInputAction` 一拿到元素就抓一份 spec 传给语义化流程，语义记录直接用它；同时 `BuildSpec` 改为**逐字段读** `AutomationElementInformation`（单个属性抛异常不再拖垮整包），定位信息全无时仍降级为坐标记录
- 2026-11-17：修复"**点一下按钮自动录出上百个动作**"——两个原因：① `StartUiaListener` 直接 `new EventListenerCore` 覆盖旧对象，**旧监听没有 Stop/Dispose**，切换目标/重新挂载后多个监听并行回调，同一个 UIA 事件被录成多条（现在是覆盖前先释放旧监听）。② `OnAutoEvent` 用 `Dispatcher.Invoke` 同步等 UI 线程，UI 一忙事件就排队，卡顿后成批涌出来；改为 `BeginInvoke`，并加**事件洪水节流**：同元素（spec 忽略 rect）同 Pattern 同方法在 400ms 内重复触发只留一条（值类 `ReplacePrevious` 事件不走节流，仍靠 merge 保留最终值）
- 2026-11-17：录制改为**鼠标按下瞬间抓元素快照**——弹出菜单/页内弹层在"抬起"时就被目标关闭，等 Up 回调再 `FromPoint` 要么拿到 null（丢步），要么拿到跳转后的新页面元素（录成错误语义）。现在 `InputHookCore` 在 `WM_LBUTTONDOWN` 额外发一条 `MouseLeftDown` 预热通知（不产生步骤），`RecordingPanel` 用 `DispatcherPriority.Send` 立即抓元素 + spec 缓存，Up 回调优先采用这份快照（坐标 ≤4px、3 秒内有效）
- 2026-11-17：UIA 事件加**「输入锚」过滤**，根治"点一下录出上百个动作"——属性变化事件是操作**结果**，一次展开会连带触发全后代的连锁事件（日志实测：一次操作 139 条 `ExpandCollapse Changed`，元素名甚至是 `AccessKey: Property does not exist`、`AriaProperties` 这类属性行文本）。现在只有「与最近一次用户输入命中同一元素（RuntimeId 相同）」且「距该输入 ≤1500ms」的事件才录成步骤，其余丢弃并记 `[anchor]` 日志；`UiaEventRecord`/事件 spec 同时补上 `RuntimeId`，回放定位更准
- 2026-11-17：修复**回放报「元素未找到: spec:v1|…」但 spec 其实是对的**——根因是回放要求"目标窗口"非空，而录制用"随鼠标捕获"（静默捕获）时根本不会设置目标窗口，`_currentTarget` 一直是 null，于是每一步都直接判"无目标窗口 → 元素未找到"（日志 `ResolveBySpec: 无目标窗口`；UWP 计算器的 `num5Button` 就是这么失败的）。现在目标窗口不再是硬前提，`ResolveBySpec` 加了两级兜底：① 按 spec 的矩形中心点 `WindowFromPoint`（+ `GetAncestor(GA_ROOT)`）反查该按钮当前所在窗口，再在里面评分匹配——按钮屏幕位置通常不变；② autoId 全局查询 `SpecResolver.FindGlobalByAutomationId`（raw UIA `CreatePropertyCondition(30011)` + `FindFirst`，在 Core 里做，因为回放跑后台 MTA 线程而 WPF 的 `AutomationElement` 要 STA；也因为在 App 项目里无法 `new CUIAutomation`），跨进程重启/窗口移动都能认回元素。
- 2026-11-17：修复**点了菜单项/树项却没有步骤**（两处）——① `FromPoint` 在 Qt 与 Chromium 上常返回最上层的 `Text`（Qt 树项文字、网页链接文字），真正可交互的是它的祖先。现在点击语义探测失败时沿父链向上最多 6 层找第一个"高置信可点击"祖先（Invoke / SelectionItem / ExpandCollapse / Toggle，刻意不含几乎容器都报的 LegacyIAccessible）再试，并改用祖先现读的 spec 定位。② 命中元素"活着但没有可执行语义"时（Chromium 弹出菜单项抬起即关，`GetSupportedPatterns()` 返回**空数组且不抛异常**）以前直接丢弃，现在留一条坐标步骤由回放按坐标探测真实语义；只有纯文本叶子（Text/StaticText）不录
