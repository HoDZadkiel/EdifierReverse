"""裝置回應的 payload 解析（已解 XOR 的 payload）。格式見 docs/protocol-commands.md。

靜態分析結果，尚未實機驗證。
"""
from dataclasses import dataclass
from typing import Optional

from .packet import PacketError


def _signed(b: int) -> int:
    return b - 256 if b > 127 else b


def parse_battery(payload: bytes) -> int:
    """0xD0。官方 App 把第 0 個位元組當有號數讀，推測為 0–100 百分比。"""
    if not payload:
        raise PacketError("電量回應為空")
    return _signed(payload[0])


def parse_power_save(payload: bytes) -> bool:
    """0xB1／0xB2：第 0 個位元組為 1 表示省電模式開啟（官方 App 的 parsePowerSaveMode，兩個指令碼共用）。"""
    if not payload:
        raise PacketError("省電模式回應為空")
    return payload[0] == 1


def parse_auto_shutdown_no_input(payload: bytes) -> bool:
    """0xD7／0xD6：第 0 個位元組為 1 表示無音訊自動關機開啟。"""
    if not payload:
        raise PacketError("無音訊自動關機回應為空")
    return payload[0] == 1


def parse_version(payload: bytes) -> str:
    """0xC6：每個位元組轉十進位，以 "." 連接。"""
    return ".".join(str(_signed(b)) for b in payload)


def parse_mac(payload: bytes) -> str:
    """0xC8：每個位元組轉 hex，以 ":" 連接。"""
    return ":".join(f"{b:02X}" for b in payload)


def parse_name(payload: bytes) -> str:
    """0xC9：UTF-8 字串。"""
    return payload.decode("utf-8", errors="replace")


@dataclass(frozen=True)
class AncState:
    group: Optional[int]  # v1 沒有 groupIndex
    mode: int
    db: Optional[int] = None


def parse_anc(payload: bytes, version: int = 2) -> AncState:
    """0xCC / 0xC1。v2：[group, mode, db?]；v1：[mode, db?]。"""
    if version >= 2:
        if len(payload) < 2:
            raise PacketError("ANC 回應過短")
        return AncState(payload[0], payload[1], payload[2] if len(payload) > 2 else None)
    if not payload:
        raise PacketError("ANC 回應為空")
    return AncState(None, payload[0], payload[1] if len(payload) > 1 else None)


def _byte_or_minus1(payload: bytes, i: int) -> int:
    """對照官方 EDUtils.getByteValue：回傳無號數值，位元組不存在時回傳 -1。"""
    return payload[i] if 0 <= i < len(payload) else -1


def parse_ldac(payload: bytes) -> int:
    """0x48：裝置端 LDAC 設定值（P[0]）；-1 表示回應沒有資料。數值各代表什麼尚未解。"""
    return _byte_or_minus1(payload, 0)


def parse_lhdc(payload: bytes) -> int:
    """0x37：裝置端 LHDC 設定值。回應長度為 2 時取 P[1]，否則取 P[0]（與官方 App 相同）。"""
    return _byte_or_minus1(payload, 1 if len(payload) == 2 else 0)


@dataclass(frozen=True)
class HDAudioCodec:
    index: int
    ldac: int  # -1 表示裝置沒有提供 LDAC 選項（官方 App 以 >= 0 決定是否顯示）
    lhdc: int  # 同上


def parse_hd_audio_codec(payload: bytes) -> HDAudioCodec:
    """0x91：[index, ldac, lhdc]。回應長度為 2 且 index 為 7、10、13 時，P[1] 是 LHDC、沒有 LDAC。"""
    index = _byte_or_minus1(payload, 0)
    ldac = _byte_or_minus1(payload, 1)
    lhdc = _byte_or_minus1(payload, 2)
    if len(payload) == 2 and index in (7, 10, 13):
        lhdc, ldac = _byte_or_minus1(payload, 1), -1
    return HDAudioCodec(index, ldac, lhdc)


@dataclass(frozen=True)
class DeviceState:
    is_tws: bool
    battery: Optional[int] = None  # 單體裝置
    left_battery: Optional[int] = None
    right_battery: Optional[int] = None
    box_battery: Optional[int] = None
    charging: bool = False


def parse_device_state(payload: bytes) -> Optional[DeviceState]:
    """0xF2。P[0] == 4 為單體裝置，其餘視為 TWS。長度不足 3 回傳 None（與官方 App 相同）。"""
    if len(payload) < 3:
        return None
    if payload[0] == 4:
        return DeviceState(
            is_tws=False,
            battery=_signed(payload[3]) if len(payload) >= 4 else 0,
            charging=len(payload) >= 5 and payload[4] == 1,
        )
    return DeviceState(
        is_tws=True,
        left_battery=0 if payload[0] == 2 else _signed(payload[1]),
        right_battery=0 if payload[0] == 1 else _signed(payload[2]),
        box_battery=_signed(payload[3]) if len(payload) >= 4 else None,
        charging=len(payload) >= 5 and payload[4] == 1,
    )
