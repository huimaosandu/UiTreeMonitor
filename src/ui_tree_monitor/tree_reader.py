"""结构树读取与节点定位 — 基于 Microsoft UI Automation (UIA)。

对外提供：
- list_windows()          枚举顶层窗口（进程名 / 标题 / 句柄）
- build_tree(hwnd)        抓取某窗口的完整结构树
- locate_by_path(hwnd, p) 按路径重新定位控件（供监控轮询使用）
- read_attribute(ctrl, attr) 读取单个属性/值
- ATTRIBUTES             可监控属性清单
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any, Optional

import uiautomation as auto

MAX_DEPTH = 24          # 遍历最大深度，防止失控
MAX_CHILDREN = 4000     # 单节点子元素上限

# ---------------------------------------------------------------------------
# 可监控的属性清单：key -> (显示用的读取函数, 是否属于"值类")
# 值类属性变化才适合做数值/状态监控告警。
# ---------------------------------------------------------------------------


def _read_name(ctrl: auto.Control) -> str:
    return ctrl.Name or ""


def _read_value(ctrl: auto.Control) -> str:
    try:
        vp = ctrl.GetValuePattern()
        if vp:
            return str(vp.Value)
    except Exception:
        pass
    return ""


def _read_range_value(ctrl: auto.Control) -> str:
    try:
        rp = ctrl.GetRangeValuePattern()
        if rp:
            return str(rp.Value)
    except Exception:
        pass
    return ""


def _read_toggle(ctrl: auto.Control) -> str:
    try:
        tp = ctrl.GetTogglePattern()
        if tp:
            return str(tp.ToggleState)
    except Exception:
        pass
    return ""


def _read_enabled(ctrl: auto.Control) -> str:
    return str(ctrl.IsEnabled)


def _read_offscreen(ctrl: auto.Control) -> str:
    return str(ctrl.IsOffscreen)


def _read_automation_id(ctrl: auto.Control) -> str:
    return ctrl.AutomationId or ""


def _read_class_name(ctrl: auto.Control) -> str:
    return ctrl.ClassName or ""


def _read_control_type(ctrl: auto.Control) -> str:
    return ctrl.ControlTypeName or ""


# (key, 默认显示标签, 读取函数, 值类)
ATTRIBUTES: list[tuple[str, str, Any, bool]] = [
    ("Name",            "Name",            _read_name,         True),
    ("Value",           "Value",           _read_value,        True),
    ("RangeValue",      "RangeValue",      _read_range_value,  True),
    ("ToggleState",     "ToggleState",     _read_toggle,       True),
    ("IsEnabled",       "IsEnabled",       _read_enabled,      False),
    ("IsOffscreen",     "IsOffscreen",     _read_offscreen,    False),
    ("AutomationId",    "AutomationId",    _read_automation_id, False),
    ("ClassName",       "ClassName",       _read_class_name,   False),
    ("ControlType",     "ControlType",     _read_control_type, False),
]
ATTR_VALUE_KEYS = {key for key, _, _, is_value in ATTRIBUTES if is_value}


def read_attribute(ctrl: auto.Control, attr: str) -> str:
    """读取指定属性值，读不到返回空串。"""
    for key, _, fn, _ in ATTRIBUTES:
        if key == attr:
            return fn(ctrl)
    return ""


# ---------------------------------------------------------------------------
# 节点与路径模型
# ---------------------------------------------------------------------------


@dataclass
class TreeNode:
    """结构树中的一个 UI 节点。"""

    name: str
    control_type: str
    automation_id: str
    class_name: str
    is_enabled: bool
    depth: int
    index: int                 # 在父节点子列表中的位置
    children: list["TreeNode"] = field(default_factory=list)

    @property
    def display(self) -> str:
        label = f"[{self.control_type}]"
        if self.name:
            label += f" {self.name}"
        if self.automation_id:
            label += f" (id={self.automation_id})"
        return label


@dataclass
class PathSegment:
    """定位控件的一层描述：优先按 (control_type, automation_id, name) 精确匹配，
    其次按 index 兜底。这样即使兄弟节点顺序变动也能尽量命中。"""

    control_type: str
    automation_id: str
    name: str
    index: int


@dataclass
class WindowInfo:
    """顶层窗口信息。"""

    title: str
    pid: int
    hwnd: int
    process_name: str


# ---------------------------------------------------------------------------
# 顶层窗口枚举
# ---------------------------------------------------------------------------

def list_windows() -> list[WindowInfo]:
    """枚举所有可见顶层窗口（含进程名）。"""
    wins: list[WindowInfo] = []
    root = auto.GetRootControl()
    if root is None:
        return wins
    for w in root.GetChildren():
        try:
            if w.ControlTypeName != "WindowControl" and w.ControlTypeName != "PaneControl":
                continue
            if not w.IsOffscreen and w.Name:
                try:
                    pid = w.ProcessId
                except Exception:
                    pid = 0
                proc = _pid_to_name(pid)
                wins.append(WindowInfo(title=w.Name, pid=pid, hwnd=w.NativeWindowHandle, process_name=proc))
        except Exception:
            continue
    # 去重，按标题+pid 排序，便于挑选
    seen: set[tuple] = set()
    dedup: list[WindowInfo] = []
    for w in sorted(wins, key=lambda x: (x.process_name.lower(), x.title.lower())):
        k = (w.pid, w.title, w.hwnd)
        if k in seen:
            continue
        seen.add(k)
        dedup.append(w)
    return dedup


def _pid_to_name(pid: int) -> str:
    try:
        import ctypes
        from ctypes import wintypes
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        h = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not h:
            return ""
        buf = ctypes.create_unicode_buffer(260)
        size = wintypes.DWORD(260)
        ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size))
        ctypes.windll.kernel32.CloseHandle(h)
        return buf.value.rsplit("\\", 1)[-1]
    except Exception:
        return ""


# ---------------------------------------------------------------------------
# 结构树抓取
# ---------------------------------------------------------------------------

def build_tree(hwnd: int) -> Optional[TreeNode]:
    """从窗口句柄抓取完整结构树，返回根节点；失败返回 None。"""
    ctrl = auto.ControlFromHandle(hwnd)
    if ctrl is None:
        return None
    root = _convert(ctrl, depth=0, index=0)
    _walk(ctrl, root, depth=1)
    return root


def _convert(ctrl: auto.Control, depth: int, index: int) -> TreeNode:
    try:
        name = ctrl.Name or ""
    except Exception:
        name = ""
    try:
        ctype = ctrl.ControlTypeName or ""
    except Exception:
        ctype = ""
    try:
        aid = ctrl.AutomationId or ""
    except Exception:
        aid = ""
    try:
        cls = ctrl.ClassName or ""
    except Exception:
        cls = ""
    try:
        enabled = bool(ctrl.IsEnabled)
    except Exception:
        enabled = False
    return TreeNode(
        name=name, control_type=ctype, automation_id=aid,
        class_name=cls, is_enabled=enabled, depth=depth, index=index,
    )


def _walk(ctrl: auto.Control, node: TreeNode, depth: int) -> None:
    if depth > MAX_DEPTH:
        return
    try:
        children = ctrl.GetChildren()
    except Exception:
        children = []
    if len(children) > MAX_CHILDREN:
        children = children[:MAX_CHILDREN]
    for i, child in enumerate(children):
        sub = _convert(child, depth, i)
        node.children.append(sub)
        _walk(child, sub, depth + 1)


# ---------------------------------------------------------------------------
# 控件定位（供监控轮询）与路径生成
# ---------------------------------------------------------------------------

def build_path(root: TreeNode, target: TreeNode) -> list[PathSegment]:
    """生成从根到 target 的定位路径。"""
    segs: list[PathSegment] = []

    def find(parent: TreeNode, want: TreeNode, acc: list[PathSegment]) -> bool:
        for ch in parent.children:
            if ch is want:
                acc.append(PathSegment(
                    control_type=ch.control_type, automation_id=ch.automation_id,
                    name=ch.name, index=ch.index))
                return True
            if find(ch, want, acc):
                acc.append(PathSegment(
                    control_type=ch.control_type, automation_id=ch.automation_id,
                    name=ch.name, index=ch.index))
                return True
        return False

    find(root, target, segs)
    segs.reverse()
    return segs


def locate_by_path(hwnd: int, path: list[PathSegment]) -> Optional[auto.Control]:
    """按路径重新定位控件。每一层优先精确匹配，失败按 index 兜底。"""
    ctrl = auto.ControlFromHandle(hwnd)
    if ctrl is None:
        return None
    for seg in path:
        found = _match_child(ctrl, seg)
        if found is None:
            return None
        ctrl = found
    return ctrl


def _match_child(ctrl: auto.Control, seg: PathSegment) -> Optional[auto.Control]:
    try:
        children = ctrl.GetChildren()
    except Exception:
        children = []
    # 优先精确匹配
    for i, ch in enumerate(children):
        try:
            ctype = ch.ControlTypeName or ""
            aid = ch.AutomationId or ""
            name = ch.Name or ""
        except Exception:
            continue
        if seg.control_type and ctype and ctype == seg.control_type and \
           seg.automation_id and aid and aid == seg.automation_id and \
           seg.name and name and name == seg.name:
            return ch
    # 兜底：按 index
    try:
        if 0 <= seg.index < len(children):
            return children[seg.index]
    except Exception:
        pass
    return None
