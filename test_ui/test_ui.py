"""检测数据展示面板（被测测试 UI）。

用途：作为 UI 结构树监控工具（C#/WPF 平台）的"被测目标"。界面按检测软件结构树设计：
组1 → 组2 → 组3 → 组4 层层嵌套，最深层含状态项；右侧参数数值每 500ms 随机波动。

框架选型：PySide6（Qt6）——Tkinter 的 UIA/MSAA 桥有缺陷（ttk/经典 Label 文本
不暴露为 UIA Name，Entry 不暴露为 Edit），无法被 Accessibility Insights 抓到；
Qt 在 Windows 上有完整 UIA 桥：
  - QGroupBox → UIA Group（Name=组标题，ContentView 可见）
  - QLabel     → UIA Text（Name=文本，RawView 可见）
  - QLineEdit  → UIA Edit（Name=setAccessibleName，Value=实时数值，ValuePattern 可读）

运行：uv run test_ui.py   （依赖 PySide6，见 pyproject.toml）
"""
import random
import sys

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QFont
from PySide6.QtWidgets import (
    QApplication,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QVBoxLayout,
    QWidget,
)

PARAM_ROWS = [
    ("深度/μm：", "0.000"),
    ("峰值坐标：", "0"),
    ("最大峰值：", "0.0000000"),
]


class TestPanel(QMainWindow):
    """检测数据展示面板。"""

    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("检测数据展示")
        self.resize(720, 380)
        self.setMinimumSize(560, 320)
        self._build_ui()
        self._timer = QTimer(self)
        self._timer.timeout.connect(self._tick)
        self._timer.start(500)

    # ---------- UI（Qt 控件，保证 UIA 暴露） ----------
    def _build_ui(self) -> None:
        central = QWidget()
        self.setCentralWidget(central)
        layout = QHBoxLayout(central)
        layout.setContentsMargins(10, 10, 10, 10)

        # 左侧：嵌套组（组1→组2→组3→组4），模拟检测软件的结构树层级
        # 显式 setAccessibleName：确保 Qt UIA 桥把每个组都暴露为 Group(Name=组标题)
        left = QGroupBox("结构树")
        left.setAccessibleName("结构树")
        layout.addWidget(left, 1)
        lv = QVBoxLayout(left)

        g1 = QGroupBox("组1")
        g1.setAccessibleName("组1")
        lv.addWidget(g1, 1)
        v1 = QVBoxLayout(g1)

        g2 = QGroupBox("组2")
        g2.setAccessibleName("组2")
        v1.addWidget(g2, 1)
        v2 = QVBoxLayout(g2)

        g3 = QGroupBox("组3")
        g3.setAccessibleName("组3")
        v2.addWidget(g3, 1)
        v3 = QVBoxLayout(g3)

        g4 = QGroupBox("组4")
        g4.setAccessibleName("组4")
        v3.addWidget(g4, 1)
        v4 = QVBoxLayout(g4)

        # 组4 内：状态指示（QLabel，UIA Text）
        v4.addWidget(QLabel("运行中…"))

        # 右侧：实时参数（参数名=QLabel，数值=只读 QLineEdit → UIA Value 可读）
        right = QGroupBox("参数")
        right.setAccessibleName("参数")
        layout.addWidget(right, 1)
        rv = QVBoxLayout(right)

        self._edits: list[QLineEdit] = []
        for label, init in PARAM_ROWS:
            row = QHBoxLayout()
            row.addWidget(QLabel(label))
            edit = QLineEdit(init)
            edit.setReadOnly(True)
            edit.setAlignment(Qt.AlignRight)
            edit.setAccessibleName(label)  # UIA Name（Accessibility Insights 属性面板可见）
            # 加宽数字框 + 等宽大字体，保证 7 位小数的数值完整显示
            edit.setMinimumWidth(170)
            edit.setFont(QFont("Consolas", 14))
            row.addWidget(edit)
            rv.addLayout(row)
            self._edits.append(edit)

        # 底部提示
        status = QLabel("测试面板：数值每 500ms 实时波动，可用结构树监控工具抓取本窗口验证")
        status.setStyleSheet("color:#888888;")
        layout.addWidget(status, 0, Qt.AlignBottom)

    # ---------- 数值波动 ----------
    def _tick(self) -> None:
        """每 500ms 刷新随机数，模拟实时测量值。"""
        values = [
            f"{random.uniform(600, 700):.3f}",
            str(random.randint(0, 100)),
            f"{random.uniform(0, 0.1):.7f}",
        ]
        for edit, v in zip(self._edits, values):
            edit.setText(v)


def main() -> None:
    app = QApplication(sys.argv)
    panel = TestPanel()
    panel.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
