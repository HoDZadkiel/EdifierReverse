"""0xD8 功能旗標回應。欄位表見 docs/protocol-d8.md（靜態分析，尚未實機驗證）。"""
from dataclasses import dataclass
from typing import Optional

from .d8_flags import FLAGS

NAME_LIMITS = {1: 24, 2: 30, 3: 29, 4: 35}


@dataclass(frozen=True)
class D8:
    raw: bytes

    def byte(self, n: int) -> Optional[int]:
        """第 n 個位元組；回應長度不足時回傳 None（官方 App 視為不支援）。"""
        return self.raw[n] if n < len(self.raw) else None

    def flag(self, name: str) -> bool:
        """name 形如 "b18_isSupportLDAC"，完整清單見 d8_flags.FLAGS。"""
        if name not in FLAGS:
            raise KeyError(f"未知旗標：{name}")
        n, bit = FLAGS[name]
        value = self.byte(n)
        return value is not None and (value >> bit) & 1 == 1

    @property
    def anc_value(self) -> int:
        return (self.raw[0] & 0x0F) if self.raw else 0

    @property
    def supports_anc(self) -> bool:
        return self.anc_value > 0

    @property
    def eq_scheme(self) -> int:
        """EQ 方案編號（如 0x20、0x21、0x23）；0 表示不支援 EQ。"""
        return self.byte(13) or 0

    @property
    def name_limit(self) -> Optional[int]:
        return NAME_LIMITS.get(self.byte(3))

    @property
    def shows_battery(self) -> bool:
        return (self.byte(1) or 0) != 0

    def is_one(self, n: int) -> bool:
        """整位元組欄位（2、4、5、6、7、8、10、11）：值為 1 表示支援。"""
        return self.byte(n) == 1
