"""i18n 多语言支持：中 / 英 / 日 / 韩 / 俄 5 种语言。

翻译文件位于 locales/*.json，GUI 全部文案通过 translate(key) 取值。
"""

from __future__ import annotations

import json
import os
from typing import Optional

LANGUAGES = {
    "zh": "中文",
    "en": "English",
    "ja": "日本語",
    "ko": "한국어",
    "ru": "Русский",
}

DEFAULT_LANG = "zh"

_locales_dir = os.path.join(os.path.dirname(__file__), "locales")

_cache: dict[str, dict] = {}


def _load(lang: str) -> dict:
    if lang not in _cache:
        path = os.path.join(_locales_dir, f"{lang}.json")
        try:
            with open(path, "r", encoding="utf-8") as f:
                _cache[lang] = json.load(f)
        except Exception:
            _cache[lang] = {}
    return _cache[lang]


def available_languages() -> list[str]:
    return list(LANGUAGES.keys())


def language_name(lang: str) -> str:
    return LANGUAGES.get(lang, lang)


def translate(lang: str, key: str) -> str:
    """按语言取翻译；缺失时回退中文，再缺失回退 key 本身。"""
    table = _load(lang)
    if key in table and table[key]:
        return table[key]
    if lang != DEFAULT_LANG:
        fallback = _load(DEFAULT_LANG)
        if key in fallback and fallback[key]:
            return fallback[key]
    return key


def load(lang: str) -> dict:
    return _load(lang)
