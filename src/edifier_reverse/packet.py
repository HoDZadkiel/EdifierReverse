"""封包組裝與解析。格式見 docs/protocol-packet.md（靜態分析，尚未實機驗證）。"""
from dataclasses import dataclass
from typing import Optional

from . import commands as C

HEADER_SEND = 0xAA
HEADERS_RECV = (0xBB, 0xCC)
APP_CODE = 0xEC
V1_CRC_BASE = 8217
ENCRYPTION_XOR = 16
XOR_KEY = 0xA5

# 韌體更新指令可能讓裝置變磚（見 CLAUDE.md 禁止事項），函式庫層直接擋下
BLOCKED_COMMANDS = frozenset({C.OTA_READY, C.OTA_START_MASTER, C.OTA_START_SUB})


class PacketError(ValueError):
    pass


@dataclass(frozen=True)
class Session:
    """版本與加密設定，來自 BLE 廣播 Manufacturer Data 的最後兩個位元組。"""

    version: int = 2
    encryption: int = 0


@dataclass(frozen=True)
class Packet:
    header: int
    cmd: int
    payload: bytes
    app_code: Optional[int] = None  # v1 沒有此欄位


def xor_payload(payload: bytes, encryption: int) -> bytes:
    """encryption == 16 時每個位元組 XOR 0xA5，其餘原樣。XOR 對稱，收送共用。"""
    if encryption != ENCRYPTION_XOR:
        return bytes(payload)
    return bytes(b ^ XOR_KEY for b in payload)


def checksum(data: bytes, version: int) -> bytes:
    """v2：總和低 8 bit；v1：總和加 8217，16 bit 大端序。"""
    if version <= 1:
        return ((sum(data) + V1_CRC_BASE) & 0xFFFF).to_bytes(2, "big")
    return bytes([sum(data) & 0xFF])


def build(session: Session, cmd: int, payload: bytes = b"", header: int = HEADER_SEND) -> bytes:
    if cmd in BLOCKED_COMMANDS:
        raise PacketError(f"指令 0x{cmd:02X} 為韌體更新，已封鎖")
    if not 0 <= cmd <= 0xFF:
        raise PacketError(f"指令碼超出範圍：{cmd}")
    if session.version <= 1:
        length = len(payload) + 1
        if length > 0xFF:
            raise PacketError("v1 封包 payload 過長")
        body = bytes([header, length, cmd]) + payload
    else:
        if len(payload) > 0xFFFF:
            raise PacketError("v2 封包 payload 過長")
        body = (bytes([header, APP_CODE, cmd]) + len(payload).to_bytes(2, "big")
                + xor_payload(payload, session.encryption))
    return body + checksum(body, session.version)


def expected_length(session: Session, data: bytes) -> Optional[int]:
    """依封包開頭算出完整長度；資料還不夠判斷時回傳 None。"""
    if session.version <= 1:
        return data[1] + 4 if len(data) >= 2 else None
    return int.from_bytes(data[3:5], "big") + 6 if len(data) >= 5 else None


def parse(session: Session, data: bytes, headers=HEADERS_RECV) -> Packet:
    """解析裝置回應。送出用的 0xAA 不是合法的接收 header，預設會被拒絕；
    模擬裝置解析 App 送出的封包時才傳入 headers=(HEADER_SEND,)。"""
    total = expected_length(session, data)
    if total is None:
        raise PacketError("資料不足，無法判斷長度")
    if data[0] not in headers:
        raise PacketError(f"header 錯誤：0x{data[0]:02X}")
    if len(data) != total:
        raise PacketError(f"長度不符：預期 {total}，實際 {len(data)}")
    body, crc = data[:-2], data[-2:]
    if session.version > 1:
        body, crc = data[:-1], data[-1:]
    if checksum(body, session.version) != crc:
        raise PacketError("CRC 錯誤")
    if session.version <= 1:
        return Packet(data[0], data[2], bytes(data[3:-2]))
    if data[1] != APP_CODE:
        raise PacketError(f"appCode 錯誤：0x{data[1]:02X}")
    return Packet(data[0], data[2], xor_payload(data[5:-1], session.encryption), data[1])
