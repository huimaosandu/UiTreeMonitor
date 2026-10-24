"""监控引擎：对指定控件按定时器周期抓取属性/值，检测变化并记录历史。

全程基于 tree_reader 的 UIA 定位，不需要任何底层协议。
线程安全：每个 Monitor 独立运行；轮询在独立线程执行，回调通过
threading 回调返回给调用方（由调用方决定如何更新 UI）。
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

from . import tree_reader
from .tree_reader import PathSegment, TreeNode, read_attribute, locate_by_path


@dataclass
class MonitoredNode:
    """一个被监控的 (窗口, 节点路径, 属性) 组合。"""

    hwnd: int
    window_title: str
    path: list[PathSegment]
    attribute: str              # 例如 "Name" / "Value" / "RangeValue"
    label: str                  # 展示用节点名
    last_value: Optional[str] = None
    changed_count: int = 0
    first_seen: float = field(default_factory=time.time)
    last_change_time: Optional[float] = None
    history: list[tuple[float, str]] = field(default_factory=list)  # (时间戳, 值)


@dataclass
class ChangeEvent:
    """一次属性变化事件。"""

    node: MonitoredNode
    timestamp: float
    old_value: str
    new_value: str


class Monitor:
    """定时轮询监控器。

    用法：
        mon = Monitor(interval=1.0)
        mon.add(node)
        mon.on_change = lambda ev: ...
        mon.start()
        ...
        mon.stop()
    """

    def __init__(self, interval: float = 1.0) -> None:
        self.interval = float(interval)
        self._nodes: list[MonitoredNode] = []
        self._lock = threading.RLock()
        self._stop_ev = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self.on_change: Optional[Callable[[ChangeEvent], None]] = None
        self.on_poll: Optional[Callable[[], None]] = None   # 每轮轮询完成回调
        self.on_error: Optional[Callable[[str], None]] = None

    # -- 配置 --------------------------------------------------------------
    def set_interval(self, seconds: float) -> None:
        with self._lock:
            self.interval = max(0.1, float(seconds))

    def add(self, node: MonitoredNode) -> None:
        with self._lock:
            self._nodes.append(node)

    def remove(self, node: MonitoredNode) -> None:
        with self._lock:
            if node in self._nodes:
                self._nodes.remove(node)

    def clear(self) -> None:
        with self._lock:
            self._nodes.clear()

    def nodes(self) -> list[MonitoredNode]:
        with self._lock:
            return list(self._nodes)

    # -- 生命周期 ----------------------------------------------------------
    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop_ev.clear()
        self._thread = threading.Thread(target=self._run, name="ui-monitor", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop_ev.set()
        if self._thread:
            self._thread.join(timeout=3.0)
            self._thread = None

    # -- 轮询循环 ----------------------------------------------------------
    def _run(self) -> None:
        while not self._stop_ev.is_set():
            start = time.time()
            try:
                self._poll_once()
            except Exception as e:  # 单轮失败不中断
                if self.on_error:
                    self.on_error(f"poll error: {e}")
            if self.on_poll:
                try:
                    self.on_poll()
                except Exception:
                    pass
            # 精确睡眠，避免累积漂移
            elapsed = time.time() - start
            remain = self.interval - elapsed
            self._stop_ev.wait(timeout=max(0.0, remain))

    def _poll_once(self) -> None:
        with self._lock:
            nodes = list(self._nodes)
        now = time.time()
        for node in nodes:
            ctrl = locate_by_path(node.hwnd, node.path)
            if ctrl is None:
                continue  # 窗口可能暂不可用，跳过本轮
            try:
                value = read_attribute(ctrl, node.attribute)
            except Exception:
                continue
            value = value if value is not None else ""
            with self._lock:
                if node.last_value is None:
                    node.last_value = value
                    node.history.append((now, value))
                    continue
                if value != node.last_value:
                    old = node.last_value
                    node.last_value = value
                    node.changed_count += 1
                    node.last_change_time = now
                    node.history.append((now, value))
                    if len(node.history) > 500:      # 限制历史长度
                        node.history = node.history[-500:]
                    ev = ChangeEvent(node=node, timestamp=now, old_value=old, new_value=value)
                    if self.on_change:
                        try:
                            self.on_change(ev)
                        except Exception:
                            pass


# ---------------------------------------------------------------------------
# 便捷：从已抓取的结构树构建监控项
# ---------------------------------------------------------------------------

def make_monitored(hwnd: int, window_title: str, root: TreeNode, target: TreeNode,
                   attribute: str) -> MonitoredNode:
    """根据结构树中选中的节点生成监控项（自动计算路径与展示名）。"""
    path = tree_reader.build_path(root, target)
    label = target.display if target.display else f"[{target.control_type}]"
    return MonitoredNode(
        hwnd=hwnd,
        window_title=window_title,
        path=path,
        attribute=attribute,
        label=label,
    )
