"""裝置端高音質編碼狀態的只讀查詢：LDAC(0x48)、LHDC(0x37)、高清編碼(0x91)。"""
import asyncio

import pytest

from edifier_reverse import commands as C
from edifier_reverse.client import Client
from edifier_reverse.packet import Session, build
from edifier_reverse.probe import SAFE_QUERIES, probe
from edifier_reverse.requests import query
from edifier_reverse.responses import (HDAudioCodec, parse_hd_audio_codec, parse_ldac,
                                       parse_lhdc)
from edifier_reverse.sim import SimulatedDevice

V2 = Session(2, 0)


def run(coro):
    return asyncio.run(coro)


# ---- 封包：手算向量（v2 CRC = 總和 & 0xFF）--------------------------------

@pytest.mark.parametrize("cmd,wire", [
    (C.LDAC_STATE_QUERY, "AA EC 48 00 00 DE"),        # 170+236+72  = 478 -> 0xDE
    (C.LHDC_STATE_QUERY, "AA EC 37 00 00 CD"),        # 170+236+55  = 461 -> 0xCD
    (C.QUERY_HD_AUDIO_CODEC, "AA EC 91 00 00 27"),    # 170+236+145 = 551 -> 0x27
])
def test_query_packets_match_hand_computed_bytes(cmd, wire):
    assert query(cmd).to_bytes(V2) == bytes.fromhex(wire)


def test_command_codes_are_the_documented_ones():
    assert (C.LDAC_STATE_QUERY, C.LHDC_STATE_QUERY, C.QUERY_HD_AUDIO_CODEC) == (0x48, 0x37, 0x91)


# ---- 回應解析 ---------------------------------------------------------

def test_ldac_is_the_first_byte_unsigned_or_minus1_when_missing():
    assert parse_ldac(b"\x01") == 1
    assert parse_ldac(b"\xFF") == 255  # 官方 getByteValue 是無號數，不是 -1
    assert parse_ldac(b"") == -1


def test_lhdc_reads_byte1_only_when_the_response_has_exactly_two_bytes():
    assert parse_lhdc(b"\x05") == 5
    assert parse_lhdc(b"\x02\x05") == 5     # 長度 2：取第 2 個位元組
    assert parse_lhdc(b"\x02\x05\x09") == 2  # 長度 3：回到第 1 個位元組
    assert parse_lhdc(b"") == -1


def test_hd_audio_codec_normal_layout():
    assert parse_hd_audio_codec(bytes([12, 1, 2])) == HDAudioCodec(12, 1, 2)


@pytest.mark.parametrize("index", [7, 10, 13])
def test_hd_audio_codec_two_byte_special_case_means_lhdc_only(index):
    # 官方 App：長度 2 且 index 為 7、10、13 時，P[1] 是 LHDC，沒有 LDAC
    got = parse_hd_audio_codec(bytes([index, 3]))
    assert got == HDAudioCodec(index, ldac=-1, lhdc=3)


def test_hd_audio_codec_two_bytes_with_other_index_is_not_special():
    # index 不在特例裡：P[1] 仍是 LDAC，缺的 LHDC 為 -1
    assert parse_hd_audio_codec(bytes([8, 3])) == HDAudioCodec(8, ldac=3, lhdc=-1)


def test_hd_audio_codec_special_case_needs_exactly_two_bytes():
    # 長度 3 時即使 index 是 7 也走一般排列
    assert parse_hd_audio_codec(bytes([7, 3, 4])) == HDAudioCodec(7, 3, 4)


def test_hd_audio_codec_empty_and_short_responses():
    assert parse_hd_audio_codec(b"") == HDAudioCodec(-1, -1, -1)
    assert parse_hd_audio_codec(bytes([12, 1])) == HDAudioCodec(12, 1, -1)


def test_minus1_means_the_device_does_not_offer_that_codec():
    # 官方 App 以 ">= 0" 決定要不要顯示該編碼選項
    got = parse_hd_audio_codec(bytes([7, 3]))
    assert got.lhdc >= 0 and not got.ldac >= 0


# ---- probe 白名單 ------------------------------------------------------

def test_probe_whitelist_now_includes_the_three_codec_queries():
    assert {C.LDAC_STATE_QUERY, C.LHDC_STATE_QUERY, C.QUERY_HD_AUDIO_CODEC} <= set(SAFE_QUERIES)


def test_every_whitelisted_command_is_a_query_never_a_setter():
    # 同值多名稱時只要有一個是 QUERY 即可；但任何一個名稱都不能是設定／重置類
    for cmd in SAFE_QUERIES:
        candidates = [k for k, v in vars(C).items() if k.isupper() and v == cmd]
        assert any("QUERY" in n for n in candidates), (cmd, candidates)
        assert not any(n.endswith(("_SET", "_SATE_SET", "_RESET")) for n in candidates), (cmd, candidates)


def test_codec_setters_are_not_in_the_whitelist():
    # 切換高清編碼可能讓裝置重啟（官方提示文字），絕不能出現在探測流程
    assert not {C.LDAC_STATE_SET, C.LHDC_SATE_SET, C.SET_HD_AUDIO_CODEC} & set(SAFE_QUERIES)


def test_probe_returns_parsed_codec_state_from_the_simulated_device():
    async def go():
        res = await probe(Client(SimulatedDevice(V2), V2), timeout=0.05)
        return {r["cmd"]: r for r in res}

    res = run(go())
    # 模擬的 LDAC 回應是 [1, 9]：LDAC 規則取 P[0] 得 1；若誤用 LHDC 規則（長度 2 取 P[1]）會得 9
    assert res[C.LDAC_STATE_QUERY]["parsed"] == "1"
    assert res[C.LDAC_STATE_QUERY]["response_payload"] == "01 09"
    # 模擬的 LHDC 回應是 [2, 5]：只有用 LHDC 的解析規則（長度 2 取第 2 個）才會得到 5
    assert res[C.LHDC_STATE_QUERY]["parsed"] == "5"
    assert res[C.LHDC_STATE_QUERY]["response_payload"] == "02 05"
    assert res[C.QUERY_HD_AUDIO_CODEC]["parsed"] == str(HDAudioCodec(3, 1, 0))
    assert res[C.QUERY_HD_AUDIO_CODEC]["response_payload"] == "03 01 00"


def test_probe_only_sent_empty_payload_queries_for_the_codec_commands():
    async def go():
        dev = SimulatedDevice(V2)
        await probe(Client(dev, V2), timeout=0.05)
        return dev.written

    sent = run(go())
    assert all(len(w) == 6 for w in sent)  # v2 空 payload 的封包長度
    assert len(sent) == len(SAFE_QUERIES)


def test_unsupported_codec_query_is_reported_as_no_response_not_an_error():
    # 裝置若沒有這些編碼（例如 ES850NB 沒有 LHDC），查詢可能沒有回應；探測要記錄而不是中斷
    async def go():
        dev = SimulatedDevice(V2)
        del dev.state[C.LHDC_STATE_QUERY]
        res = await probe(Client(dev, V2), timeout=0.05)
        return {r["cmd"]: r for r in res}

    res = run(go())
    assert res[C.LHDC_STATE_QUERY]["parsed"] == "（無回應）"
    assert res[C.LDAC_STATE_QUERY]["parsed"] == "1"  # 其他查詢不受影響
