import pytest

from edifier_reverse.d8 import D8
from edifier_reverse.d8_flags import FLAGS


def _d8(**bytes_at):
    raw = bytearray(30)
    for n, v in bytes_at.items():
        raw[int(n[1:])] = v
    return D8(bytes(raw))


def test_flag_reads_the_documented_bit():
    # b18 bit2 = LDAC、bit1 = LHDC
    d8 = _d8(b18=0b0000_0100)
    assert d8.flag("b18_isSupportLDAC")
    assert not d8.flag("b18_isSupportLHDC")


def test_flag_is_false_when_response_is_too_short():
    # 官方 App：回應長度不足的欄位保持預設（不支援）
    assert not D8(bytes(5)).flag("b18_isSupportLDAC")


def test_unknown_flag_name_is_an_error_not_silently_false():
    with pytest.raises(KeyError):
        _d8().flag("b18_isSupportTeleport")


def test_same_field_name_in_different_bytes_is_kept_apart():
    # isSupportTimeCalibration 同時出現在位元組 20、21、26
    names = [k for k in FLAGS if k.endswith("isSupportTimeCalibration")]
    assert sorted(names) == ["b20_isSupportTimeCalibration", "b21_isSupportTimeCalibration",
                             "b26_isSupportTimeCalibration"]
    d8 = _d8(b21=0b0000_0001)
    assert d8.flag("b21_isSupportTimeCalibration")
    assert not d8.flag("b20_isSupportTimeCalibration")


def test_byte0_low_nibble_is_anc_value_and_high_bits_are_separate_flags():
    d8 = _d8(b0=0b1010_0011)
    assert d8.anc_value == 3 and d8.supports_anc
    assert d8.flag("b0_isTWS") and d8.flag("b0_isRChannel") and not d8.flag("b0_isPeer")


def test_no_anc_when_low_nibble_is_zero():
    assert not _d8(b0=0b1110_0000).supports_anc


def test_eq_scheme_is_the_whole_byte_13():
    assert _d8(b13=0x23).eq_scheme == 0x23
    assert _d8().eq_scheme == 0


def test_name_limit_mapping():
    assert [_d8(b3=v).name_limit for v in (1, 2, 3, 4, 0, 9)] == [24, 30, 29, 35, None, None]


def test_whole_byte_flags_need_exactly_one():
    d8 = _d8(b2=1, b4=2)
    assert d8.is_one(2) and not d8.is_one(4)


def test_shows_battery_when_byte1_is_nonzero():
    assert _d8(b1=1).shows_battery and not _d8().shows_battery


def test_flag_table_has_no_bit_outside_a_byte():
    assert all(0 <= bit <= 7 for _, bit in FLAGS.values())
