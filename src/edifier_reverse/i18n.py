"""介面語言：繁體中文、簡體中文、英文、日文。字串表在 strings.json，C# 版（src/EdifierTray）嵌入同一份檔案，
兩邊共用，所以不會有一邊漏翻。找不到的鍵會退回繁體中文，再找不到就回傳鍵名（不讓畫面因缺字而出錯）。"""
import json
from pathlib import Path
from typing import Dict, List, Tuple

DEFAULT = "zh-TW"
# (代碼, 原生語言名稱) —— 語言名稱永遠用該語言自己的寫法，不翻譯，使用者才找得到自己的語言
LANGUAGES: List[Tuple[str, str]] = [("zh-TW", "繁體中文"), ("zh-CN", "简体中文"), ("en", "English"), ("ja", "日本語")]
SHORT: Dict[str, str] = {"zh-TW": "繁體", "zh-CN": "简体", "en": "EN", "ja": "日本語"}  # 選單分段按鈕用
# 各語言的 Windows 11 介面字體（英文與數字另用 Segoe UI Variable）
FONTS: Dict[str, Tuple[str, ...]] = {
    "zh-TW": ("Microsoft JhengHei UI", "Microsoft JhengHei"),
    "zh-CN": ("Microsoft YaHei UI", "Microsoft YaHei"),
    "ja": ("Yu Gothic UI", "Meiryo UI", "Yu Gothic"),
    "en": ("Segoe UI Variable Text", "Segoe UI"),
}

_TABLE: Dict[str, Dict[str, str]] = json.loads((Path(__file__).with_name("strings.json")).read_text(encoding="utf-8"))
_current = DEFAULT


def codes() -> List[str]:
    return [c for c, _ in LANGUAGES]


def set_language(code: str) -> str:
    """設定目前語言；未知代碼退回預設。回傳實際採用的代碼。"""
    global _current
    _current = code if code in _TABLE else DEFAULT
    return _current


def language() -> str:
    return _current


def tr(key: str, **kw) -> str:
    text = _TABLE.get(_current, {}).get(key) or _TABLE[DEFAULT].get(key) or key
    try:
        return text.format(**kw)
    except (KeyError, IndexError):
        return text


def detect_system_language() -> str:
    """第一次執行時依 Windows 介面語言挑：zh-TW/HK/MO -> 繁中、zh-* -> 簡中、ja -> 日文，其餘英文。"""
    try:
        import ctypes

        buf = ctypes.create_unicode_buffer(85)
        ctypes.windll.kernel32.GetUserDefaultLocaleName(buf, 85)
        name = buf.value
    except Exception:
        return "en"
    low = name.lower()
    if low.startswith("zh"):
        return "zh-TW" if any(t in low for t in ("tw", "hk", "mo", "hant")) else "zh-CN"
    return "ja" if low.startswith("ja") else "en"


def pick_font(families, lang: str = None) -> str:
    """依語言挑 Windows 11 的介面字體（Tk 一個字體只能指定一個家族，不指定中文會落到有襯線的細明體）。"""
    available = set(families)
    for name in FONTS.get(lang or _current, FONTS[DEFAULT]) + FONTS["en"]:
        if name in available:
            return name
    return "Segoe UI"
