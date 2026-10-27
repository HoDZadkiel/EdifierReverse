import pytest

from edifier_reverse import commands as C
from edifier_reverse import requests as R
from edifier_reverse.eq import (Band, CustomEq, EqCalibration, decode_custom_eq, decode_eq,
                                encode_bands, encode_set_band, encode_write, layout_for)
from edifier_reverse.packet import PacketError


@pytest.mark.parametrize("eq_index", [1, 2, 3, 4, 7, 12, 16, 17])
def test_layout_length_equals_bands_times_stride_plus_byte0(eq_index):
    # 這個不變量是從反編譯碼的 (頻段數, 總長, 每頻段長度) 三個常數交叉驗證出來的，
    # 若表抄錯，這裡會失敗
    lay = layout_for(eq_index)
    assert lay.length == lay.bands * lay.stride + (1 if lay.has_byte0 else 0)


def _eq(eq_index, n, **kw):
    return CustomEq(eq_index, [Band(i, 100 * (i + 1), 10 + i, filter=1, q=7) for i in range(n)], **kw)


@pytest.mark.parametrize("eq_index,n", [(1, 4), (2, 6), (3, 6), (7, 6), (12, 9), (16, 9), (17, 10)])
def test_encode_bands_has_the_layout_length(eq_index, n):
    assert len(encode_bands(_eq(eq_index, n, byte0=3))) == layout_for(eq_index).length


def test_encode_wrong_band_count_is_rejected():
    with pytest.raises(PacketError, match="頻段"):
        encode_bands(_eq(1, 3))


def test_wide_layout_bytes_for_default_eq():
    eq = CustomEq(1, [Band(0, 0x0123, 5, filter=2, q=9)] + [Band(i, 0, 0) for i in range(1, 4)])
    # [band, filter, freq_hi, freq_lo, gain, q]
    assert encode_bands(eq)[:6] == bytes([0, 2, 0x01, 0x23, 5, 9])


def test_eq7_puts_byte0_before_the_bands():
    data = encode_bands(_eq(7, 6, byte0=0x42))
    assert data[0] == 0x42
    assert data[1:5] == bytes([0, 0x00, 100, 10])  # band0: idx, freq=100, gain=10


@pytest.mark.parametrize("eq_index,n", [(1, 4), (2, 6), (3, 6), (7, 6), (12, 9), (16, 9), (17, 10)])
def test_write_then_decode_roundtrip(eq_index, n):
    src = _eq(eq_index, n, byte0=4 if eq_index in (7, 12) else 0,
              timestamp=bytes.fromhex("01020304"), name="我的EQ")
    # 回應 = [eqIndex, ?] + 寫入內容；P[1] 意義未解，測試固定為 0
    response = bytes([eq_index, 0]) + encode_write(src)
    got = decode_custom_eq(response, version=2)
    assert got.eq_index == eq_index
    assert got.timestamp == src.timestamp and got.name == src.name
    assert got.byte0 == src.byte0
    short = not layout_for(eq_index).wide
    for a, b in zip(got.bands, src.bands):
        assert (a.freq, a.gain) == (b.freq, b.gain)
        if not short:  # 短排列沒有 filter / q
            assert (a.filter, a.q) == (b.filter, b.q)


def test_decode_v1_assumes_eq_index_1_and_starts_at_payload_0():
    src = _eq(1, 4, timestamp=bytes(4), name="a")
    got = decode_custom_eq(encode_write(src), version=1)
    assert got.eq_index == 1 and len(got.bands) == 4


def test_decode_without_tail_gives_empty_name_and_timestamp():
    response = bytes([2, 0]) + encode_bands(_eq(2, 6))
    got = decode_custom_eq(response, version=2)
    assert got.timestamp == b"" and got.name == ""


def test_decode_truncated_band_data_is_an_error():
    with pytest.raises(PacketError, match="不足"):
        decode_custom_eq(bytes([2, 0]) + bytes(10), version=2)


def test_set_band_payload_depends_on_eq_index():
    assert encode_set_band(2, 1, 0x0102, 5) == bytes([1, 1, 2, 5])
    assert encode_set_band(16, 1, 0x0102, 5) == bytes([1, 1, 2, 5])
    assert encode_set_band(7, 1, 0x0102, 5, byte0=9) == bytes([9, 1, 1, 2, 5])
    assert encode_set_band(1, 1, 0x0102, 5, filter=3, q=8) == bytes([1, 3, 1, 2, 5, 8])
    assert encode_set_band(1, 1, 0x0102, 5)[-1] == 0  # q 沒給時填 0


def test_negative_gain_is_masked_to_a_byte():
    # 負增益的真實編碼未知；目前只保證不會因超出範圍而丟例外
    assert encode_set_band(2, 0, 100, -3)[-1] == 0xFD


def test_custom_eq_requests_use_the_documented_commands():
    assert R.set_custom_eq_band(2, 0, 100, 5).cmd == C.CUSTOM_EQ_SET
    assert R.write_custom_eq(_eq(2, 6, timestamp=bytes(4))).cmd == C.CUSTOM_EQ_SWITCH
    assert R.reset_custom_eq().cmd == C.CUSTOM_EQ_RESET
    req = R.rename_custom_eq("abc", bytes.fromhex("0a0b0c0d"))
    assert req.cmd == C.CUSTOM_EQ_NAME_SET and req.payload == bytes.fromhex("0a0b0c0d") + b"abc"


def test_eq_response_with_7_bytes_has_byte0():
    rr, cal = decode_eq(bytes([3, 1, 2, 30, 12, 4, 5]))
    assert rr == 3
    assert cal == EqCalibration(1, 2, 30, 12, 4, 5)


def test_eq_response_with_6_bytes_has_no_byte0():
    # 官方 App 此時把 byte0 設為 -1
    rr, cal = decode_eq(bytes([3, 1, 30, 12, 4, 5]))
    assert cal == EqCalibration(1, None, 30, 12, 4, 5)
    assert cal.to_bytes()[1] == 0xFF


def test_eq_set_payload_matches_the_query_layout():
    cal = EqCalibration(1, 2, 30, 12, 4, 5)
    req = R.set_eq(3, cal)
    assert req.cmd == C.EQ_SET
    rr, back = decode_eq(req.payload)
    assert (rr, back) == (3, cal)


def test_eq_response_too_short_has_no_calibration():
    assert decode_eq(bytes([3, 1])) == (3, None)
    with pytest.raises(PacketError):
        decode_eq(b"")


@pytest.mark.parametrize("ts", [b"", b"", bytes(5)])
def test_write_requires_exactly_4_byte_timestamp(ts):
    # 實機：0x46 不帶時間戳時，ES850NB 讀回殘留／損毀資料（見 DEVLOG 2026-10-27），所以必須擋在送出前
    with pytest.raises(PacketError, match="時間戳"):
        encode_write(_eq(2, 6, timestamp=ts, name="x"))
    with pytest.raises(PacketError, match="時間戳"):
        R.write_custom_eq(_eq(2, 6, timestamp=ts))
    with pytest.raises(PacketError, match="時間戳"):
        R.rename_custom_eq("x", ts)
