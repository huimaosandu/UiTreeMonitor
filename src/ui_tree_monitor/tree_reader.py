"""结构树读取与节点定位 — 基于 Microsoft UI Automation (UIA)。

对外提供：
- list_windows()          枚举顶层窗口（进程名 / 标题 / 句柄）
- build_tree(hwnd)        抓取某窗口的完整结构树
- locate_by_path(hwnd, p) 按路径重新定位控件（供监控轮询使用）
- read_attribute(ctrl, attr) 读取单个属性/值
- element_from_point(x, y) 获取光标下控件
- window_from_element(ctrl) 由控件上溯顶层窗口
- locate_in_tree(root, ctrl) 在已抓取的树中定位光标元素节点
- ATTRIBUTES             可监控属性清单
"""

from __future__ import annotations

import ctypes
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


# ---------------------------------------------------------------------------
# 随光标捕获（Element Inspector）
# ---------------------------------------------------------------------------

class _Point(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def cursor_position() -> tuple[int, int]:
    """返回当前屏幕坐标下的光标位置 (x, y)。"""
    p = _Point()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def element_from_point(x: int, y: int) -> Optional[auto.Control]:
    """获取指定屏幕坐标下的 UIA 控件；失败返回 None。"""
    try:
        ctrl = auto.ControlFromPoint(x, y)
        return ctrl if ctrl is not None else None
    except Exception:
        return None


def window_from_element(ctrl: auto.Control) -> Optional[WindowInfo]:
    """由任意控件上溯到其所属顶层窗口，返回 WindowInfo；失败返回 None。"""
    try:
        top = ctrl.GetTopLevelControl()
        if top is None:
            return None
        hwnd = int(top.NativeWindowHandle or 0)
        if hwnd <= 0:
            return None
        try:
            title = top.Name or ""
        except Exception:
            title = ""
        try:
            pid = int(top.ProcessId or 0)
        except Exception:
            pid = 0
        return WindowInfo(title=title, pid=pid, hwnd=hwnd,
                          process_name=_pid_to_name(pid))
    except Exception:
        return None


def _seg_from_ctrl(ctrl: auto.Control) -> PathSegment:
    try:
        ctype = ctrl.ControlTypeName or ""
    except Exception:
        ctype = ""
    try:
        aid = ctrl.AutomationId or ""
    except Exception:
        aid = ""
    try:
        name = ctrl.Name or ""
    except Exception:
        name = ""
    # index：找父下的位置（尽力而为，获取失败用 -1 表示未知）
    index = -1
    try:
        parent = ctrl.GetParentControl()
        if parent is not None:
            for i, ch in enumerate(parent.GetChildren()):
                try:
                    if (ch.ControlTypeName or "") == ctype and (ch.AutomationId or "") == aid \
                       and (ch.Name or "") == name:
                        index = i
                        break
                except Exception:
                    continue
    except Exception:
        pass
    return PathSegment(control_type=ctype, automation_id=aid, name=name, index=index)


def _path_to_root(ctrl: auto.Control) -> list[PathSegment]:
    """从光标控件向上收集到顶层窗口的路径（顶层在前，光标元素在后）。"""
    segs: list[PathSegment] = []
    cur: Optional[auto.Control] = ctrl
    visited = 0
    while cur is not None and visited < 64:
        seg = _seg_from_ctrl(cur)
        segs.insert(0, seg)
        visited += 1
        try:
            if _is_top_window(cur):
                break
            parent = cur.GetParentControl()
            if parent is None or parent is cur:
                break
            cur = parent
        except Exception:
            break
    return segs


def _is_top_window(ctrl: auto.Control) -> bool:
    try:
        return bool(ctrl.IsTopLevel)
    except Exception:
        return False


def locate_in_tree(root: TreeNode, ctrl: auto.Control) -> Optional[TreeNode]:
    """在已抓取的树中定位光标元素对应的节点。

    思路：取光标控件到顶层窗口的路径段（顶层在前），在 root 树上逐层下钻匹配。
    每层优先精确匹配 (type+id+name)，其次放宽到 (type+name)，再按 index 兜底。
    """
    path = _path_to_root(ctrl)
    if not path:
        return None
    # 顶层段应即 root 本身，跳过；若匹配不上则整体失败
    current: Optional[TreeNode] = root
    for seg in path[1:]:
        current = _match_tree_child(current, seg)
        if current is None:
            return None
    return current


def _match_tree_child(node: TreeNode, seg: PathSegment) -> Optional[TreeNode]:
    children = node.children
    # 1) 精确：type + id + name
    for ch in children:
        if seg.control_type and ch.control_type and ch.control_type == seg.control_type and \
           seg.automation_id and ch.automation_id and seg.automation_id == ch.automation_id and \
           seg.name and ch.name and seg.name == ch.name:
            return ch
    # 2) 放宽：type + name
    for ch in children:
        if seg.control_type and ch.control_type and ch.control_type == seg.control_type and \
           seg.name and ch.name and ch.name == seg.name:
            return ch
    # 3) 按 index 兜底
    if 0 <= seg.index < len(children):
        return children[seg.index]
    return None
