"""检测数据展示面板（被测测试 UI）。

用途：作为 UI 结构树监控工具的"被测目标"。界面按检测软件结构树设计：
组1 → 组2 → 组3 → 组4 层层嵌套（UIA 中为嵌套 Group），最深层含参数项；
右侧参数（深度/μm、峰值坐标、最大峰值）数值每 500ms 随机波动，便于观测。

运行：uv run test_ui.py   （或 python test_ui.py，仅依赖标准库）
"""
import random
import tkinter as tk
from tkinter import ttk

FONT_VALUE = ("Consolas", 16, "bold")
FONT_LABEL = ("Microsoft YaHei", 10)


class TestPanel:
    """检测数据展示面板。"""

    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        root.title("检测数据展示")
        root.geometry("640x380")
        root.minsize(560, 320)
        self._build_ui()
        self._tick()

    # ---------- UI ----------
    def _build_ui(self) -> None:
        main = ttk.Frame(self.root, padding=10)
        main.pack(fill="both", expand=True)

        # 左侧：嵌套组（组1→组2→组3→组4），模拟检测软件的结构树层级
        left = ttk.LabelFrame(main, text="结构树", padding=6)
        left.pack(side="left", fill="both", expand=True, padx=(0, 8))

        g1 = ttk.LabelFrame(left, text="组1", padding=6)
        g1.pack(fill="both", expand=True)

        g2 = ttk.LabelFrame(g1, text="组2", padding=6)
        g2.pack(fill="both", expand=True)

        g3 = ttk.LabelFrame(g2, text="组3", padding=6)
        g3.pack(fill="both", expand=True)

        g4 = ttk.LabelFrame(g3, text="组4", padding=6)
        g4.pack(fill="both", expand=True)

        # 组4 内：状态指示（文本项）
        self._status = ttk.Label(g4, text="运行中…", font=FONT_LABEL, foreground="#0C5186")
        self._status.pack(anchor="w", pady=(2, 0))

        # 右侧：实时参数
        right = ttk.LabelFrame(main, text="参数", padding=14)
        right.pack(side="right", fill="both", expand=True)

        self._depth_var = tk.StringVar(value="--")
        self._pos_var = tk.StringVar(value="--")
        self._peak_var = tk.StringVar(value="--")

        self._add_param_row(right, 0, "深度/μm：", self._depth_var)
        self._add_param_row(right, 1, "峰值坐标：", self._pos_var)
        self._add_param_row(right, 2, "最大峰值：", self._peak_var)

        # 底部提示
        tip = ttk.Label(
            self.root,
            text="测试面板：数值每 500ms 实时波动，可用结构树监控工具抓取本窗口验证",
            foreground="#888888",
        )
        tip.pack(side="bottom", pady=(0, 6))

    def _add_param_row(self, parent: ttk.Frame, row: int, label: str, var: tk.StringVar) -> None:
        frame = ttk.Frame(parent)
        frame.grid(row=row, column=0, sticky="w", pady=8)
        ttk.Label(frame, text=label, font=FONT_LABEL).pack(side="left")
        value = ttk.Label(frame, textvariable=var, font=FONT_VALUE, width=14, anchor="e")
        value.pack(side="left", padx=(8, 0))

    # ---------- 数值波动 ----------
    def _tick(self) -> None:
        """每 500ms 刷新随机数，模拟实时测量值。"""
        self._depth_var.set(f"{random.uniform(600, 700):.3f}")
        self._pos_var.set(str(random.randint(0, 100)))
        self._peak_var.set(f"{random.uniform(0, 0.1):.7f}")
        self.root.after(500, self._tick)


def main() -> None:
    root = tk.Tk()
    TestPanel(root)
    root.mainloop()


if __name__ == "__main__":
    main()
