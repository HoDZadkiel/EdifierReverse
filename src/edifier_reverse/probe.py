"""實機探測：依序送出白名單內的查詢指令，記錄原始回應與解析結果。

只含查詢類指令，不送任何設定、重置或韌體更新指令。
"""
from typing import Callable, Dict, List

from . import commands as C
from .client import Client, ResponseTimeout
from .d8 import D8
from .d8_flags import FLAGS
from .eq import decode_custom_eq, decode_eq
from .requests import query
from .responses import (parse_anc, parse_battery, parse_device_state, parse_hd_audio_codec,
                        parse_ldac, parse_lhdc, parse_mac, parse_name, parse_version)

SAFE_QUERIES = [
    C.DEVICE_FUNCTION_QUERY,  # 先問功能旗標，其餘結果才好對照
    C.BATTERY_QUERY,
    C.DEVICE_STATE_QUERY,
    C.VERSION_QUERY,
    C.MAC_QUERY,
    C.NAME_QUERY,
    C.ANC_QUERY,
    C.EQ_QUERY,
    C.CUSTOM_EQ_QUERY,
    # 裝置端的高音質編碼狀態（只讀）。注意：這只是裝置端的設定，實際用哪種編碼取決於電腦端能否送出
    C.LDAC_STATE_QUERY,
    C.LHDC_STATE_QUERY,
    C.QUERY_HD_AUDIO_CODEC,
]
_NAMES = {v: k for k, v in vars(C).items() if k.isupper() and isinstance(v, int)}


def _describe_d8(payload: bytes) -> str:
    d8 = D8(payload)
    on = [k for k in FLAGS if d8.flag(k)]
    return (f"len={len(payload)} anc_value={d8.anc_value} eq_scheme=0x{d8.eq_scheme:02X} "
            f"name_limit={d8.name_limit} flags_on={on}")


def _parsers(version: int) -> Dict[int, Callable[[bytes], object]]:
    return {
        C.DEVICE_FUNCTION_QUERY: _describe_d8,
        C.BATTERY_QUERY: parse_battery,
        C.DEVICE_STATE_QUERY: parse_device_state,
        C.VERSION_QUERY: parse_version,
        C.MAC_QUERY: parse_mac,
        C.NAME_QUERY: parse_name,
        C.ANC_QUERY: lambda p: parse_anc(p, version),
        C.EQ_QUERY: decode_eq,
        C.CUSTOM_EQ_QUERY: lambda p: decode_custom_eq(p, version),
        C.LDAC_STATE_QUERY: parse_ldac,
        C.LHDC_STATE_QUERY: parse_lhdc,
        C.QUERY_HD_AUDIO_CODEC: parse_hd_audio_codec,
    }


async def probe(client: Client, timeout: float = 2.0) -> List[dict]:
    parsers = _parsers(client.session.version)
    results = []
    for cmd in SAFE_QUERIES:
        entry = {"cmd": cmd, "name": _NAMES.get(cmd, "?"),
                 "request": query(cmd).to_bytes(client.session).hex(" "),
                 "response_payload": None, "parsed": None}
        try:
            pkt = await client.request(query(cmd), timeout=timeout)
            entry["response_payload"] = pkt.payload.hex(" ")
            try:
                entry["parsed"] = str(parsers[cmd](pkt.payload))
            except Exception as e:  # 解析失敗本身就是要回報的資訊
                entry["parsed"] = f"解析失敗：{type(e).__name__}: {e}"
        except ResponseTimeout:
            entry["parsed"] = "（無回應）"
        results.append(entry)
    return results
