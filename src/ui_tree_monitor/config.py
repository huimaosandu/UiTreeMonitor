"""配置管理：语言、轮询间隔等用户设置，持久化到用户目录 JSON。"""

from __future__ import annotations

import json
import os

APP_DIR = os.path.join(os.path.expanduser("~"), ".ui-tree-monitor")
CONFIG_PATH = os.path.join(APP_DIR, "config.json")


def _defaults() -> dict:
    return {
        "language": "zh",
        "interval": 1.0,
        "last_window": "",
    }


def load() -> dict:
    cfg = _defaults()
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            for k, v in _defaults().items():
                if k in data:
                    cfg[k] = data[k]
    except Exception:
        pass
    return cfg


def save(cfg: dict) -> None:
    try:
        os.makedirs(APP_DIR, exist_ok=True)
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception:
        pass
