"""桌面 GUI 主程序（Tkinter）。

布局：
- 顶部工具栏：语言切换、目标窗口下拉、刷新窗口、轮询间隔、抓取结构树、开始/停止
- 左侧：结构树视图（选中节点 → 右侧可添加监控）
- 右侧：已监控项表格 + 添加/移除/清空操作
- 底部：变化日志 + 状态栏
"""

from __future__ import annotations

import datetime
import tkinter as tk
from tkinter import ttk, messagebox
from typing import Optional

from . import i18n, config
from .tree_reader import (WindowInfo, list_windows, build_tree,
                          TreeNode, ATTRIBUTES)
from .monitor import Monitor, MonitoredNode, ChangeEvent, make_monitored

# 树视图中节点映射：iid -> TreeNode
# 监控表格中：行 id -> MonitoredNode


class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.cfg = config.load()
        self.lang = self.cfg.get("language", "zh")
        self._monitor = Monitor(interval=self.cfg.get("interval", 1.0))
        self._windows: list[WindowInfo] = []
        self._selected_window: Optional[WindowInfo] = None
        self._tree_root: Optional[TreeNode] = None
        self._node_map: dict[str, TreeNode] = {}          # tree iid -> node
        self._monitor_items: dict[str, MonitoredNode] = {}  # table iid -> monitored

        # 属性下拉映射：显示文本 -> key
        self._attr_display_to_key: dict[str, str] = {}
        self._attr_key_to_display: dict[str, str] = {}

        self.title("")
        self.geometry("1180x720")
        self.minsize(960, 600)

        self._build_attr_maps()
        self._build_ui()
        self._apply_language()
        self._monitor.on_change = self._on_change
        self._monitor.on_poll = self._on_poll
        self._monitor.on_error = self._on_monitor_error

        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ------------------------------------------------------------------
    # 属性映射
    # ------------------------------------------------------------------
    def _build_attr_maps(self) -> None:
        for key, display, _, _ in ATTRIBUTES:
            translated = i18n.translate(self.lang, f"attr_{key.lower()}")
            self._attr_display_to_key[translated] = key
            self._attr_key_to_display[key] = translated

    # ------------------------------------------------------------------
    # UI 构建
    # ------------------------------------------------------------------
    def _build_ui(self) -> None:
        pad = {"padx": 6, "pady": 4}

        # ---- 顶部工具栏 ----
        toolbar = ttk.Frame(self, padding=8)
        toolbar.pack(side="top", fill="x")

        ttk.Label(toolbar, text="Lang:").pack(side="left")
        self.lang_var = tk.StringVar(value=self.lang)
        self.lang_combo = ttk.Combobox(
            toolbar, textvariable=self.lang_var, width=6, state="readonly",
            values=i18n.available_languages())
        self.lang_combo.pack(side="left", padx=(4, 12))
        self.lang_combo.bind("<<ComboboxSelected>>", self._on_lang_change)

        self.lbl_window = ttk.Label(toolbar, text="")
        self.lbl_window.pack(side="left")
        self.win_var = tk.StringVar()
        self.win_combo = ttk.Combobox(toolbar, textvariable=self.win_var, width=48, state="readonly")
        self.win_combo.pack(side="left", padx=4)
        self.btn_refresh = ttk.Button(toolbar, command=self._refresh_windows)
        self.btn_refresh.pack(side="left", padx=2)
        self.btn_load = ttk.Button(toolbar, command=self._load_tree)
        self.btn_load.pack(side="left", padx=2)

        ttk.Separator(toolbar, orient="vertical").pack(side="left", fill="y", padx=8)

        self.lbl_interval = ttk.Label(toolbar, text="")
        self.lbl_interval.pack(side="left")
        self.interval_var = tk.StringVar(value=str(self.cfg.get("interval", 1.0)))
        self.interval_entry = ttk.Entry(toolbar, textvariable=self.interval_var, width=6)
        self.interval_entry.pack(side="left", padx=4)

        self.btn_start = ttk.Button(toolbar, command=self._start_monitor)
        self.btn_start.pack(side="left", padx=2)
        self.btn_stop = ttk.Button(toolbar, command=self._stop_monitor, state="disabled")
        self.btn_stop.pack(side="left", padx=2)

        # ---- 主体（左右分栏）----
        main = ttk.PanedWindow(self, orient="horizontal")
        main.pack(side="top", fill="both", expand=True)

        # 左：结构树
        left = ttk.Frame(main)
        main.add(left, weight=3)
        self.lbl_tree = ttk.Label(left, text="")
        self.lbl_tree.pack(anchor="w", **pad)
        tree_frame = ttk.Frame(left)
        tree_frame.pack(fill="both", expand=True, **pad)
        self.tree = ttk.Treeview(tree_frame, columns=("type", "id"), show="tree headings")
        self.tree.heading("#0", text="")
        self.tree.heading("type", text="")
        self.tree.heading("id", text="")
        self.tree.column("#0", width=360)
        self.tree.column("type", width=110, anchor="w")
        self.tree.column("id", width=110, anchor="w")
        vsb = ttk.Scrollbar(tree_frame, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")

        # 属性选择行
        attr_bar = ttk.Frame(left)
        attr_bar.pack(fill="x", **pad)
        self.lbl_attribute = ttk.Label(attr_bar, text="")
        self.lbl_attribute.pack(side="left")
        self.attr_var = tk.StringVar()
        self.attr_combo = ttk.Combobox(attr_bar, textvariable=self.attr_var,
                                       values=list(self._attr_display_to_key.keys()),
                                       state="readonly", width=24)
        self.attr_combo.pack(side="left", padx=4)
        self.btn_add = ttk.Button(attr_bar, command=self._add_monitor)
        self.btn_add.pack(side="left", padx=4)

        # 右：监控项
        right = ttk.Frame(main)
        main.add(right, weight=2)
        self.lbl_monitored = ttk.Label(right, text="")
        self.lbl_monitored.pack(anchor="w", **pad)
        mon_frame = ttk.Frame(right)
        mon_frame.pack(fill="both", expand=True, **pad)
        self.mon_table = ttk.Treeview(
            mon_frame, columns=("attr", "last", "changes"),
            show="headings", selectmode="extended")
        self.mon_table.heading("attr", text="")
        self.mon_table.heading("last", text="")
        self.mon_table.heading("changes", text="")
        self.mon_table.column("attr", width=170, anchor="w")
        self.mon_table.column("last", width=120, anchor="w")
        self.mon_table.column("changes", width=70, anchor="center")
        mvsb = ttk.Scrollbar(mon_frame, orient="vertical", command=self.mon_table.yview)
        self.mon_table.configure(yscrollcommand=mvsb.set)
        self.mon_table.pack(side="left", fill="both", expand=True)
        mvsb.pack(side="right", fill="y")

        mon_btns = ttk.Frame(right)
        mon_btns.pack(fill="x", **pad)
        self.btn_remove = ttk.Button(mon_btns, command=self._remove_monitor)
        self.btn_remove.pack(side="left", padx=2)
        self.btn_clear = ttk.Button(mon_btns, command=self._clear_monitors)
        self.btn_clear.pack(side="left", padx=2)

        # ---- 底部日志 ----
        self.lbl_log = ttk.Label(self, text="")
        self.lbl_log.pack(anchor="w", **pad)
        log_frame = ttk.Frame(self)
        log_frame.pack(fill="both", expand=False, **pad)
        self.log_text = tk.Text(log_frame, height=7, state="disabled", wrap="word")
        lvsb = ttk.Scrollbar(log_frame, orient="vertical", command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=lvsb.set)
        self.log_text.pack(side="left", fill="both", expand=True)
        lvsb.pack(side="right", fill="y")

        # ---- 状态栏 ----
        self.status_var = tk.StringVar()
        ttk.Label(self, textvariable=self.status_var, anchor="w",
                  relief="sunken").pack(side="bottom", fill="x")

    # ------------------------------------------------------------------
    # 语言应用
    # ------------------------------------------------------------------
    def _apply_language(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        self.title(t("app_title"))
        self.lbl_window.configure(text=t("label_window"))
        self.btn_refresh.configure(text=t("btn_refresh"))
        self.btn_load.configure(text=t("btn_load_tree"))
        self.lbl_interval.configure(text=t("label_interval"))
        self.btn_start.configure(text=t("btn_start"))
        self.btn_stop.configure(text=t("btn_stop"))
        self.lbl_tree.configure(text=t("label_tree"))
        self.lbl_attribute.configure(text=t("label_attribute"))
        self.lbl_monitored.configure(text=t("label_monitored"))
        self.lbl_log.configure(text=t("label_log"))
        self.tree.heading("#0", text=t("col_node"))
        self.tree.heading("type", text=t("attr_controltype"))
        self.tree.heading("id", text=t("attr_automationid"))
        self.mon_table.heading("attr", text=t("col_attribute"))
        self.mon_table.heading("last", text=t("col_last"))
        self.mon_table.heading("changes", text=t("col_changed"))
        self.btn_remove.configure(text=t("btn_remove"))
        self.btn_clear.configure(text=t("btn_clear"))
        self._set_status(t("status_ready"))
        # 刷新属性下拉映射与当前值
        self._build_attr_maps()
        if self.attr_var.get():
            # 保留已选属性（按 key 映射回新的显示文本）
            key = self._attr_display_to_key.get(self.attr_var.get())
            if key:
                self.attr_var.set(self._attr_key_to_display[key])
        self.attr_combo.configure(values=list(self._attr_display_to_key.keys()))
        self._refresh_monitor_table()

    def _on_lang_change(self, _ev=None) -> None:
        self.lang = self.lang_var.get()
        self.cfg["language"] = self.lang
        config.save(self.cfg)
        self._apply_language()

    # ------------------------------------------------------------------
    # 窗口枚举 / 结构树
    # ------------------------------------------------------------------
    def _refresh_windows(self) -> None:
        self._windows = list_windows()
        labels = [f"{w.process_name} — {w.title} [pid={w.pid}]" for w in self._windows]
        self.win_combo.configure(values=labels)
        if labels:
            self.win_combo.current(0)
            self._selected_window = self._windows[0]
            self._set_status(i18n.translate(self.lang, "status_ready"))

    def _selected_win(self) -> Optional[WindowInfo]:
        idx = self.win_combo.current()
        if 0 <= idx < len(self._windows):
            return self._windows[idx]
        return None

    def _load_tree(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        win = self._selected_win()
        if win is None:
            messagebox.showwarning(t("msg_error_title"), t("msg_need_window"))
            return
        self._selected_window = win
        self._tree_root = build_tree(win.hwnd)
        self._node_map.clear()
        self.tree.delete(*self.tree.get_children())
        if self._tree_root is None:
            messagebox.showerror(t("msg_error_title"), t("status_tree_fail"))
            self._set_status(t("status_tree_fail"))
            return
        self._insert_node("", self._tree_root)
        count = self._count_nodes(self._tree_root)
        self._set_status(t("status_tree_ok").format(count=count))

    def _insert_node(self, parent: str, node: TreeNode) -> None:
        iid = f"n{len(self._node_map)}"
        self._node_map[iid] = node
        self.tree.insert(parent, "end", iid=iid,
                         text=node.name or node.control_type or "(unnamed)",
                         values=(node.control_type, node.automation_id),
                         open=False)
        for child in node.children:
            self._insert_node(iid, child)

    @staticmethod
    def _count_nodes(root: TreeNode) -> int:
        return 1 + sum(App._count_nodes(c) for c in root.children)

    # ------------------------------------------------------------------
    # 监控项管理
    # ------------------------------------------------------------------
    def _add_monitor(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        sel = self.tree.selection()
        if not sel:
            messagebox.showinfo(t("msg_info_title"), t("msg_no_node"))
            return
        attr_display = self.attr_var.get()
        attr_key = self._attr_display_to_key.get(attr_display)
        if not attr_key:
            messagebox.showinfo(t("msg_info_title"), t("msg_no_attr"))
            return
        if self._selected_window is None or self._tree_root is None:
            messagebox.showinfo(t("msg_info_title"), t("msg_need_window"))
            return
        for iid in sel:
            node = self._node_map.get(iid)
            if node is None:
                continue
            mon = make_monitored(self._selected_window.hwnd,
                                 self._selected_window.title,
                                 self._tree_root, node, attr_key)
            self._monitor.add(mon)
            table_iid = f"m{len(self._monitor_items)}"
            self._monitor_items[table_iid] = mon
            self.mon_table.insert("", "end", iid=table_iid,
                                  values=(f"{mon.label} · {attr_display}", "", "0"))
        self._set_status(t("status_added").format(label=sel[0]))
        self._refresh_monitor_table()

    def _remove_monitor(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        sel = self.mon_table.selection()
        if not sel:
            return
        for iid in sel:
            mon = self._monitor_items.pop(iid, None)
            if mon:
                self._monitor.remove(mon)
            self.mon_table.delete(iid)
        self._set_status(t("status_removed"))

    def _clear_monitors(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        if not self._monitor_items:
            return
        if messagebox.askyesno(t("msg_info_title"), t("msg_confirm_clear")):
            self._monitor.clear()
            self._monitor_items.clear()
            self.mon_table.delete(*self.mon_table.get_children())
            self._set_status(t("status_cleared"))

    def _refresh_monitor_table(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        for iid, mon in self._monitor_items.items():
            attr_display = self._attr_key_to_display.get(mon.attribute, mon.attribute)
            last = mon.last_value if mon.last_value is not None else ""
            self.mon_table.item(iid, values=(f"{mon.label} · {attr_display}", last, mon.changed_count))

    # ------------------------------------------------------------------
    # 监控启停
    # ------------------------------------------------------------------
    def _start_monitor(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        if not self._monitor_items:
            messagebox.showinfo(t("msg_info_title"), t("msg_no_monitored"))
            return
        try:
            interval = float(self.interval_var.get())
        except ValueError:
            interval = 1.0
        self._monitor.set_interval(interval)
        self.cfg["interval"] = interval
        config.save(self.cfg)
        self._monitor.start()
        self.btn_start.configure(state="disabled")
        self.btn_stop.configure(state="normal")
        self._set_status(t("status_start").format(interval=interval))

    def _stop_monitor(self) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        self._monitor.stop()
        self.btn_start.configure(state="normal")
        self.btn_stop.configure(state="disabled")
        self._set_status(t("status_stop"))

    # ------------------------------------------------------------------
    # 监控回调（线程 → 主线程）
    # ------------------------------------------------------------------
    def _on_change(self, ev: ChangeEvent) -> None:
        self.after(0, lambda: self._handle_change_ui(ev))

    def _handle_change_ui(self, ev: ChangeEvent) -> None:
        t = lambda k: i18n.translate(self.lang, k)
        attr_display = self._attr_key_to_display.get(ev.node.attribute, ev.node.attribute)
        ts = datetime.datetime.fromtimestamp(ev.timestamp).strftime("%H:%M:%S")
        line = t("log_change").format(attr=attr_display, old=ev.old_value, new=ev.new_value)
        self.log_text.configure(state="normal")
        self.log_text.insert("end", f"[{ts}] {line}\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")
        # 更新表格当前值
        for iid, mon in self._monitor_items.items():
            if mon is ev.node:
                self.mon_table.item(iid, values=(
                    f"{mon.label} · {attr_display}",
                    mon.last_value, mon.changed_count))
                break

    def _on_poll(self) -> None:
        # 周期性刷新表格中的当前值
        self.after(0, self._refresh_monitor_table)

    def _on_monitor_error(self, msg: str) -> None:
        self.after(0, lambda: self._set_status(f"⚠ {msg}"))

    # ------------------------------------------------------------------
    # 杂项
    # ------------------------------------------------------------------
    def _set_status(self, text: str) -> None:
        self.status_var.set(text)

    def _on_close(self) -> None:
        self._monitor.stop()
        self.destroy()


def run() -> None:
    app = App()
    app.mainloop()


if __name__ == "__main__":
    run()
