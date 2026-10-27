"""EQ 相關結構。格式見 docs/protocol-commands.md（靜態分析，尚未實機驗證）。

負增益的編碼方式未知：解碼保留原始的 0–255，編碼時只做 & 0xFF。
"""
from dataclasses import dataclass
from typing import List, Optional

from .packet import PacketError


@dataclass(frozen=True)
class Layout:
    bands: int
    length: int      # 頻段資料總長（含 byte0）
    stride: int      # 每頻段位元組數
    has_byte0: bool  # 頻段資料前多 1 個 byte0
    wide: bool       # 6 bytes 排列：[band, filter, freq_hi, freq_lo, gain, q]


LAYOUTS = {
    2: Layout(6, 24, 4, False, False),
    3: Layout(6, 36, 6, False, True),
    7: Layout(6, 25, 4, True, False),
    12: Layout(9, 37, 4, True, False),
    16: Layout(9, 36, 4, False, False),
    17: Layout(10, 40, 4, False, False),
}
DEFAULT_LAYOUT = Layout(4, 24, 6, False, True)  # eqIndex 1、4、5、6、8 及其他


def layout_for(eq_index: int) -> Layout:
    return LAYOUTS.get(eq_index, DEFAULT_LAYOUT)


@dataclass(frozen=True)
class Band:
    index: int
    freq: int
    gain: int
    filter: int = 0
    q: int = 0


@dataclass
class CustomEq:
    eq_index: int
    bands: List[Band]
    byte0: int = 0
    timestamp: bytes = b""
    name: str = ""


def decode_custom_eq(payload: bytes, version: int = 2) -> CustomEq:
    """解析 0x43 的回應。v2：P[0]=eqIndex、頻段資料從 P[2] 起；v1：eqIndex 固定 1、從 P[0] 起。"""
    if version >= 2:
        if len(payload) < 2:
            raise PacketError("自訂 EQ 回應過短")
        eq_index, start = payload[0], 2
    else:
        eq_index, start = 1, 0
    lay = layout_for(eq_index)
    data = payload[start:start + lay.length]
    if len(data) < lay.length:
        raise PacketError(f"自訂 EQ 頻段資料不足：需要 {lay.length}，實際 {len(data)}")

    base = 1 if lay.has_byte0 else 0
    bands = []
    for i in range(lay.bands):
        o = base + i * lay.stride
        if lay.wide:
            bands.append(Band(data[o], (data[o + 2] << 8) | data[o + 3],
                              data[o + 4], filter=data[o + 1], q=data[o + 5]))
        else:
            bands.append(Band(data[o], (data[o + 1] << 8) | data[o + 2], data[o + 3]))
    tail = payload[start + lay.length:]
    timestamp = bytes(tail[:4]) if len(tail) >= 4 else b""
    name = tail[4:].decode("utf-8", errors="replace") if len(tail) > 4 else ""
    return CustomEq(eq_index, bands, data[0] if lay.has_byte0 else 0, timestamp, name)


def encode_bands(eq: CustomEq) -> bytes:
    """0x46 整組寫入的頻段資料。頻段序號用迴圈順序，與官方 App 的 changeCustomEq 一致。"""
    lay = layout_for(eq.eq_index)
    if len(eq.bands) != lay.bands:
        raise PacketError(f"eqIndex {eq.eq_index} 需要 {lay.bands} 個頻段，收到 {len(eq.bands)}")
    out = bytearray(lay.length)
    base = 1 if lay.has_byte0 else 0
    if lay.has_byte0:
        out[0] = eq.byte0 & 0xFF
    for i, b in enumerate(eq.bands):
        o = base + i * lay.stride
        hi, lo = (b.freq >> 8) & 0xFF, b.freq & 0xFF
        if lay.wide:
            out[o:o + 6] = bytes([i, b.filter & 0xFF, hi, lo, b.gain & 0xFF, b.q & 0xFF])
        else:
            out[o:o + 4] = bytes([i, hi, lo, b.gain & 0xFF])
    return bytes(out)


def check_timestamp(timestamp: bytes) -> bytes:
    """時間戳必須剛好 4 bytes。實機（ES850NB）上 0x46 少送時間戳，裝置會留下殘留／損毀的資料。"""
    if len(timestamp) != 4:
        raise PacketError(f"時間戳必須是 4 bytes，收到 {len(timestamp)}")
    return timestamp


def encode_write(eq: CustomEq) -> bytes:
    """0x46 的 payload：頻段資料 + 時間戳 + 名稱。不含 eqIndex 前綴。"""
    return encode_bands(eq) + check_timestamp(eq.timestamp) + eq.name.encode("utf-8")


def encode_set_band(eq_index: int, index: int, freq: int, gain: int,
                    byte0: int = 0, filter: int = 0, q: Optional[int] = None) -> bytes:
    """0x44 單頻段設定的 payload。"""
    hi, lo, g = (freq >> 8) & 0xFF, freq & 0xFF, gain & 0xFF
    if eq_index in (2, 16, 17):
        return bytes([index, hi, lo, g])
    if eq_index in (7, 12):
        return bytes([byte0, index, hi, lo, g])
    return bytes([index, filter & 0xFF, hi, lo, g, (q or 0) & 0xFF])


@dataclass(frozen=True)
class EqCalibration:
    """0xD5 的校正欄位（看起來是桌面喇叭用）。byte0 為 None 代表回應只有 6 bytes。"""

    index: int
    byte0: Optional[int]
    low_cutoff_freq: int
    low_cutoff_slope: int
    acoustic_space: int
    desktop_control: int

    def to_bytes(self) -> bytes:
        b0 = 0xFF if self.byte0 is None else self.byte0
        return bytes([self.index, b0, self.low_cutoff_freq, self.low_cutoff_slope,
                      self.acoustic_space, self.desktop_control])


def decode_eq(payload: bytes):
    """解析 0xD5：回傳 (預設編號 rr, EqCalibration 或 None)。"""
    if not payload:
        raise PacketError("EQ 回應為空")
    rr = payload[0]
    if len(payload) == 6:
        return rr, EqCalibration(payload[1], None, *payload[2:6])
    if len(payload) >= 7:
        return rr, EqCalibration(payload[1], payload[2], *payload[3:7])
    return rr, None
