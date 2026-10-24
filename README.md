# ui-tree-monitor

Windows 应用 **UI 结构树数值/属性变化监控工具**。

基于 Microsoft UI Automation (UIA)，读取任意 Windows 应用的可访问性结构树，勾选其中的节点与属性，用**定时器周期轮询**监控其数值/属性变化并记录历史。全程走 UIA 官方能力，**无需通过底层协议（Win32 消息、Hook、驱动等）**获取数据。

---

## 功能特性

- **窗口枚举**：列出当前系统所有可见顶层窗口（进程名 / 标题 / PID / 句柄）。
- **结构树抓取**：一键抓取目标窗口完整 UIA 结构树（类型 / 名称 / AutomationId）。
- **随光标捕获（Element Inspector）**：开启后移动光标，自动加载光标下窗口的结构树（自动展开前两层），并用**黄色高亮**标记光标所指的节点；状态栏实时显示命中的窗口与控件。无需手动选窗口。
- **节点监控**：在树中选中任意节点 + 属性，加入监控列表。
- **定时轮询**：可调轮询间隔（秒），独立线程周期抓取属性值。
- **变化检测**：对比新旧值，记录每次变化的旧值→新值、时间戳与变化次数。
- **历史日志**：所有变化事件实时写入日志区。
- **多语言**：中 / 英 / 日 / 韩 / 俄 5 种语言，界面一键切换并持久化。
- **免底层协议**：数据获取完全依赖 UIA 的控件属性 / 值模式（Value、RangeValue、Toggle 等），不触碰 Win32 底层消息或钩子。

### 可监控属性

| 属性 | 说明 | 读取方式 |
|------|------|----------|
| `Name` | 控件名称 | 直接属性 |
| `Value` | 文本值（文本框等） | ValuePattern |
| `RangeValue` | 数值（滑块、进度条等） | RangeValuePattern |
| `ToggleState` | 开关状态 | TogglePattern |
| `IsEnabled` | 可用状态 | 直接属性 |
| `IsOffscreen` | 是否在屏幕外 | 直接属性 |
| `AutomationId` | 自动化标识 | 直接属性 |
| `ClassName` | 类名 | 直接属性 |
| `ControlType` | 控件类型 | 直接属性 |

---

## 环境要求

- Windows（本工具为 Windows 专属）
- Python ≥ 3.13（开发基于 anaconda 3.13，需含 Tkinter；**不要用不含 Tk 的裸 Python 运行时**）
- [uv](https://docs.astral.sh/uv/)（环境与依赖管理）
- 可选：`git`（分步提交）

---

## 安装与启动

```powershell
# 1. 进入工程目录
cd E:\ui-tree-monitor

# 2. 用系统 Python 创建虚拟环境并安装依赖（必须带完整 Tk 的解释器，如 anaconda）
uv venv --python D:\ProgramData\anaconda3\python.exe
uv sync

# 3. 启动 GUI
uv run main.py
# 或
uv run python -m ui_tree_monitor
```

> 若 `uv venv` 后启动报 `Can't find a usable init.tcl`，说明虚拟环境基于不含 Tk 的 Python 创建，请删除 `.venv` 并用带完整 Tk 的解释器（如 anaconda）重建。

---

## 使用步骤

1. 顶部工具栏 **选择目标窗口**（下拉框已按进程+标题排序，含 PID）。
2. 点击 **抓取结构树**，左侧显示目标窗口的完整 UI 树。
   - 也可点击 **随光标捕获**：开启后把光标移到任意窗口，工具会自动加载该窗口的结构树并高亮光标所指节点（相当于 Element Inspector），无需手动选窗口；再点一次即停止。
3. 在树中 **选中一个节点**，在右侧 **选择要监控的属性**（默认 Name）。
4. 点击 **添加监控**，节点出现在下方“已监控项”表格。
5. 设置 **轮询间隔（秒）**（默认 1.0）。
6. 点击 **开始监控** → 底部日志实时打印每次变化，表格显示当前值与变化次数。
7. 点击 **停止监控** 结束；**移除选中 / 清空全部** 管理监控列表。
8. 右上角 **Lang** 下拉可切换 中/英/日/韩/俄。

---

## 工程结构

```
E:\ui-tree-monitor\
├── main.py                         # 启动入口
├── pyproject.toml                  # 工程与依赖声明（uv）
├── README.md                       # 本文档
├── src\
│   └── ui_tree_monitor\
│       ├── __init__.py
│       ├── app.py                  # 桌面 GUI（Tkinter）
│       ├── tree_reader.py          # 结构树读取 / 窗口枚举 / 路径定位
│       ├── monitor.py              # 监控引擎（定时轮询 + 变化检测）
│       ├── config.py               # 用户配置持久化
│       ├── i18n.py                 # 多语言加载
│       └── locales\
│           ├── zh.json             # 中文
│           ├── en.json             # English
│           ├── ja.json             # 日本語
│           ├── ko.json             # 한국어
│           └── ru.json             # Русский
```

### 模块说明

| 模块 | 职责 |
|------|------|
| `tree_reader.py` | `list_windows()` 枚举窗口；`build_tree(hwnd)` 抓树；`build_path()` / `locate_by_path()` 节点定位；`read_attribute()` 读属性；`cursor_position()` / `element_from_point()` / `window_from_element()` / `locate_in_tree()` 随光标捕获；`ATTRIBUTES` 可监控属性清单 |
| `monitor.py` | `Monitor` 定时轮询引擎：`set_interval` / `add` / `remove` / `start` / `stop`；`MonitoredNode` 监控项；`ChangeEvent` 变化事件 |
| `app.py` | Tkinter 主界面：工具栏 / 结构树 / 监控表格 / 日志 / 状态栏；监控与光标捕获回调经 `after()` + 线程安全队列更新 UI |
| `config.py` | 语言、轮询间隔、上次窗口等设置，持久化到 `~/.ui-tree-monitor/config.json` |
| `i18n.py` | 按语言取翻译，缺失回退中文 |

---

## 技术说明

- **节点定位**：每个监控项保存从窗口根到目标节点的路径（每层记录 控件类型 / AutomationId / 名称 / 子索引）。轮询时按路径重新定位，**优先精确匹配**（类型+ID+名称），找不到再按索引兜底，对兄弟节点顺序变动有一定鲁棒性。
- **值读取**：对不同属性走对应 UIA Pattern（Value / RangeValue / Toggle），读不到返回空串，不会抛异常中断轮询。
- **线程与 COM 初始化**：uiautomation 在子线程使用前必须先 `CoInitialize`。监控轮询线程与随光标捕获线程均用 `auto.UIAutomationInitializerInThread(True)` 上下文包裹。
- **线程安全**：监控轮询与光标捕获在独立后台线程执行；线程内**绝不直接操作 Tk**。变化回调经 `after(0, ...)` 回主线程；随光标捕获的结果先写入 `queue.Queue`，主线程用定时器 `_drain_cursor_queue()` 周期取走并更新界面，避免跨线程 Tk 访问崩溃。
- **光标捕获竞态处理**：光标线程**自带最新树引用**（`_cursor_current_root`），窗口切换后立即用它定位，避免与主线程加载树的时序竞态导致定位到旧树、高亮落空。
- **高亮可见性**：命中节点用 `cursor_hl` tag 渲染**黄色背景**高亮，加载树时自动展开前两层并 `see()` 滚动到目标，确保光标指向的节点始终可见；状态栏实时显示命中的窗口与控件。
- **历史长度**：单监控项保留最近 500 条变化记录，防止内存膨胀。

### 依赖

- `uiautomation`（含 `comtypes`）— 唯一第三方依赖，封装 Microsoft UIAutomation。

---

## 验证

工程内含一次性自检脚本（运行后删除，非交付物）：

```powershell
uv run python _verify_cursor.py       # i18n 键一致性 + 光标捕获冒烟（窗口识别/抓树/定位）
uv run python _verify_cursor_gui.py   # GUI 光标捕获全流程（开启→自动加载→高亮→停止）
```

已实测通过：窗口枚举、结构树抓取、路径重定位、监控引擎轮询、随光标捕获（自动加载光标下窗口树并高亮节点，如 “德擎标光测试平台”）、5 语言键一致均正常。

---

## 后续可扩展

- 变化事件追加导出 CSV / 写入日志文件。
- 阈值告警（数值越界 / 状态翻转提醒）。
- 监控方案（窗口+节点+属性+间隔）保存与加载。
- 通过 `seedance` / 智能体定时任务实现无人值守巡检。

---

## 许可

仅供内部使用。
