"""要送給裝置的請求（指令碼 + payload）。用 Session.build 或 packet.build 轉成封包。

靜態分析結果，尚未實機驗證。設定類請求會改變裝置狀態，實機測試前先確認。
"""
from dataclasses import dataclass
from typing import Optional

from . import commands as C
from . import eq as _eq
from .packet import Session, build


@dataclass(frozen=True)
class Request:
    cmd: int
    payload: bytes = b""

    def to_bytes(self, session: Session) -> bytes:
        return build(session, self.cmd, self.payload)


def query(cmd: int) -> Request:
    """查詢類指令（空 payload），例如 query(C.BATTERY_QUERY)。"""
    return Request(cmd)


def set_anc(mode: int, group: Optional[int] = None, db: Optional[int] = None) -> Request:
    """0xC1。給 group 為 v2 格式 [group, mode, db?]，不給為 v1 格式 [mode, db?]。
    官方 App 只在 db > 0 時才附帶。"""
    payload = bytes([mode]) if group is None else bytes([group, mode])
    if db is not None and db > 0:
        payload += bytes([db])
    return Request(C.ANC_SET, payload)


def set_eq(rr: int, calibration: Optional[_eq.EqCalibration] = None) -> Request:
    """0xC4：[rr] + 校正欄位（可省略）。"""
    payload = bytes([rr]) + (calibration.to_bytes() if calibration else b"")
    return Request(C.EQ_SET, payload)


def set_power_save(on: bool) -> Request:
    """0xB2：省電模式開關，payload 1 byte（1 開、0 關）。官方 App 的「省電模式」頁（ConneX 1.0.31，靜態分析，未實機驗證）。
    開著時裝置閒置（無音訊、無操作）15 或 30 分鐘後進入省電待機或自動關機，依機種而異；關掉就不會。"""
    return Request(C.SAVING_MODE_SET, bytes([1 if on else 0]))


def set_auto_shutdown_no_input(on: bool) -> Request:
    """0xD6：無音訊輸入自動關機開關，payload 1 byte（1 開、0 關）。只有功能旗標 b8 為 1 的機種有（MP330 為 0，不支援）。靜態分析，未實機驗證。"""
    return Request(C.AUTO_SHUTDOWN_WITHOUT_AUDIO_SET, bytes([1 if on else 0]))


def set_custom_eq_band(eq_index: int, index: int, freq: int, gain: int, **kw) -> Request:
    """0x44：單一頻段。kw 可帶 byte0、filter、q。"""
    return Request(C.CUSTOM_EQ_SET, _eq.encode_set_band(eq_index, index, freq, gain, **kw))


def write_custom_eq(eq: _eq.CustomEq) -> Request:
    """0x46：整組頻段 + 時間戳 + 名稱。"""
    return Request(C.CUSTOM_EQ_SWITCH, _eq.encode_write(eq))


def rename_custom_eq(name: str, timestamp: bytes) -> Request:
    """0x47：[4 byte 時間戳] + UTF-8 名稱。"""
    return Request(C.CUSTOM_EQ_NAME_SET, _eq.check_timestamp(timestamp) + name.encode("utf-8"))


def reset_custom_eq() -> Request:
    return Request(C.CUSTOM_EQ_RESET)
