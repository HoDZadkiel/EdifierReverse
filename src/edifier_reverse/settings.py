"""使用者設定：外觀、語言、選用的裝置與已知裝置清單。存在 %APPDATA% 底下的 edifier-reverse/settings.json
（內含裝置位址，不放專案目錄）。讀不到或檔案壞掉就用預設值，不讓程式起不來。"""
import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

THEMES = ("system", "light", "dark")


@dataclass
class KnownDevice:
    a2dp: str                 # 經典藍牙位址（SPP 連線目標，也是裝置的唯一識別）
    ble: str = ""
    version: int = 2
    encryption: int = 0
    name: str = ""            # 連上後由 0xC9 取得；沒連過是空字串
    model: str = "ES850NB"   # 辨識出的機種（目錄名稱）；不在目錄的 Edifier 裝置為 "Edifier"

    @property
    def tail(self) -> str:
        """位址後四碼，用來區分同型號的多台裝置。"""
        return self.a2dp[-5:]


@dataclass
class Settings:
    theme: str = "system"
    language: str = ""        # 空字串＝第一次執行，依系統語言決定
    selected: str = ""        # 選用裝置的 a2dp 位址；空字串＝用第一台
    devices: List[KnownDevice] = field(default_factory=list)

    def device(self, a2dp: str) -> Optional[KnownDevice]:
        return next((d for d in self.devices if d.a2dp.upper() == a2dp.upper()), None)

    def remember(self, dev: KnownDevice) -> None:
        """新增或更新（保留已知的名稱）。"""
        old = self.device(dev.a2dp)
        if old is None:
            self.devices.append(dev)
        else:
            old.ble = dev.ble or old.ble
            old.version, old.encryption = dev.version, dev.encryption
            old.name = dev.name or old.name

    def current_device(self) -> Optional[KnownDevice]:
        return (self.device(self.selected) if self.selected else None) or (self.devices[0] if self.devices else None)


def default_path() -> Path:
    return Path(os.environ.get("APPDATA", str(Path.home()))) / "edifier-reverse" / "settings.json"


def load(path: Optional[Path] = None) -> Settings:
    try:
        d = json.loads((path or default_path()).read_text(encoding="utf-8"))
        s = Settings(theme=d.get("theme", "system") if d.get("theme") in THEMES else "system",
                     language=str(d.get("language", "")), selected=str(d.get("selected", "")))
        s.devices = [KnownDevice(**{k: v for k, v in x.items() if k in KnownDevice.__dataclass_fields__})
                     for x in d.get("devices", []) if isinstance(x, dict) and "a2dp" in x]
        return s
    except (OSError, ValueError, TypeError):
        return Settings()


def save(s: Settings, path: Optional[Path] = None) -> None:
    try:
        p = path or default_path()
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(asdict(s), ensure_ascii=False, indent=1), encoding="utf-8")
    except OSError:
        pass  # 寫不進去不影響功能（只是下次不記得設定）
