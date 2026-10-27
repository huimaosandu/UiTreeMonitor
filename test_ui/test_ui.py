#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""test_ui.py — 检测数据展示面板（按参考图布局）。

布局（对照参考图）：
  ┌──────────────┬──────────────────────────────┐
  │  组 1         │  深度/μm：   679.296          │
  │  组 2         │  峰值坐标：  67               │
  │  组 3         │  最大峰值：  0.0817962        │
  │  组 4         │                              │
  └──────────────┴──────────────────────────────┘

数据由随机数生成，每 500ms 实时变动刷新，无其它交互。

运行：
    uv run test_ui.py          # 需在 test_ui 目录下
    uv run test_ui/test_ui.py  # 或从工程根目录
"""

from __future__ import annotations

import random
import tkinter as tk
from tkinter import ttk

APP_TITLE = "检测数据展示"          # 窗口标题
WINDOW_W, WINDOW_H = 640, 360      # 窗口尺寸
BG = "#F5F5F5"                      # 浅色背景（贴近参考图）

# 4 个组标签
GROUP_LABELS = ["组 1", "组 2", "组 3", "组 4"]
# 参数项：(显示名, 单位, 随机值生成函数, 小数位)
PARAMETERS = [
    ("深度/μm",   "μm", lambda: round(random.uniform(600, 760), 3), 3),
    ("峰值坐标",  "",    lambda: random.randint(0, 200),              0),
    ("最大峰值",  "",    lambda: round(random.uniform(0.0, 0.2), 7),  7),
]


def gen_data() -> dict[str, dict[str, str]]:
    """为 4 组各生成一组参数，返回 {组名: {参数名: 数值字符串}}。"""
    data: dict[str, dict[str, str]] = {}
    for g in GROUP_LABELS:
        row: dict[str, str] = {}
        for name, unit, fn, digits in PARAMETERS:
            val = fn()
            if digits == 0:
                row[name] = str(val)
            else:
                row[name] = f"{val:.{digits}f}"
        data[g] = row
    return data


class MeasurementPanel(tk.Tk):
    """检测数据展示面板主窗口（数据实时随机变动）。"""

    REFRESH_MS = 500  # 每 500ms 刷新一次

    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry(f"{WINDOW_W}x{WINDOW_H}")
        self.resizable(False, False)
        self.configure(bg=BG)
        self._value_labels: dict[str, dict[str, tk.Label]] = {}
        self._build_ui()
        self._refresh()  # 立即显示首批数据
        self.after(self.REFRESH_MS, self._tick)

    def _tick(self) -> None:
        """周期刷新：重新生成随机数据并更新显示。"""
        self._refresh()
        self.after(self.REFRESH_MS, self._tick)

    def _refresh(self) -> None:
        data = gen_data()
        for g, row in data.items():
            for name, value in row.items():
                lbl = self._value_labels.get(g, {}).get(name)
                if lbl is not None:
                    unit = dict((n, u) for n, u, _, _ in PARAMETERS)[name]
                    lbl.configure(text=value + (f" {unit}" if unit else ""))

    def _build_ui(self) -> None:
        # ---- 顶部标题栏 ----
        title = tk.Label(self, text=APP_TITLE, font=("Microsoft YaHei", 14, "bold"),
                         bg=BG, fg="#222222", anchor="w", padx=16, pady=12)
        title.pack(side="top", fill="x")

        # ---- 主体：左（组）+ 右（参数）----
        body = tk.Frame(self, bg=BG)
        body.pack(side="top", fill="both", expand=True, padx=16, pady=(0, 16))

        # 左侧：4 组标签
        left = tk.Frame(body, bg=BG)
        left.pack(side="left", fill="y", padx=(0, 20))
        for i, g in enumerate(GROUP_LABELS):
            cell = tk.Label(left, text=g, font=("Microsoft YaHei", 11),
                            bg=BG, fg="#222222", width=8, anchor="w", pady=10)
            cell.pack(side="top", anchor="w")

        # 右侧：参数区（白底卡片）
        right = tk.Frame(body, bg="#FFFFFF", highlightthickness=1,
                         highlightbackground="#DDDDDD")
        right.pack(side="left", fill="both", expand=True)

        unit_map = {n: u for n, u, _, _ in PARAMETERS}

        # 每组一个横向参数块
        for i, g in enumerate(GROUP_LABELS):
            block = tk.Frame(right, bg="#FFFFFF")
            block.pack(fill="x", expand=True)

            # 组名（沿用左侧样式，加深分隔）
            sep = tk.Frame(block, bg="#F0F0F0", height=1)
            sep.pack(fill="x", padx=12)

            # 参数行：深度 / 峰值坐标 / 最大峰值
            for name, unit, _, digits in PARAMETERS:
                row = tk.Frame(block, bg="#FFFFFF")
                row.pack(fill="x", padx=16, pady=2)
                tk.Label(row, text=f"{name}：", font=("Microsoft YaHei", 10),
                         bg="#FFFFFF", fg="#444444", width=10, anchor="w").pack(side="left")
                val_label = tk.Label(row, text="", font=("Consolas", 10, "bold"),
                                     bg="#FFFFFF", fg="#111111")
                val_label.pack(side="left")
                self._value_labels.setdefault(g, {})[name] = val_label


def main() -> None:
    app = MeasurementPanel()
    app.mainloop()


if __name__ == "__main__":
    main()
