import pytest

from edifier_reverse import commands as C
from edifier_reverse import requests as R
from edifier_reverse.packet import PacketError
from edifier_reverse.responses import (AncState, DeviceState, parse_anc, parse_battery,
                                       parse_device_state, parse_mac, parse_name,
                                       parse_version)


def test_battery_is_read_as_signed_byte_like_the_official_app():
    assert parse_battery(b"\x50") == 80
    assert parse_battery(b"\xFF") == -1  # 官方 App 不做無號轉換，異常值要看得出來


def test_battery_empty_payload_is_an_error():
    with pytest.raises(PacketError):
        parse_battery(b"")


def test_version_mac_name():
    assert parse_version(bytes([1, 0, 2])) == "1.0.2"
    assert parse_mac(bytes.fromhex("A1B2C3040506")) == "A1:B2:C3:04:05:06"
    assert parse_name("ES850NB".encode()) == "ES850NB"


def test_anc_v2_has_group_mode_and_optional_db():
    assert parse_anc(bytes([5, 2]), version=2) == AncState(5, 2, None)
    assert parse_anc(bytes([5, 4, 12]), version=2) == AncState(5, 4, 12)


def test_anc_v1_has_no_group():
    # v1 的群組要從 D8 位元組 0 的低 4 bit 取得，所以這裡必須是 None
    assert parse_anc(bytes([3, 7]), version=1) == AncState(None, 3, 7)


def test_anc_too_short_is_an_error():
    with pytest.raises(PacketError):
        parse_anc(bytes([5]), version=2)
    with pytest.raises(PacketError):
        parse_anc(b"", version=1)


def test_set_anc_roundtrips_through_parse_anc():
    req = R.set_anc(mode=3, group=18, db=9)
    assert req.cmd == C.ANC_SET
    assert parse_anc(req.payload, 2) == AncState(18, 3, 9)


def test_set_anc_omits_db_when_zero_like_the_official_app():
    assert R.set_anc(mode=2, group=5, db=0).payload == bytes([5, 2])
    assert R.set_anc(mode=2, db=0).payload == bytes([2])  # v1 格式


def test_single_device_state():
    st = parse_device_state(bytes([4, 0, 0, 77, 1]))
    assert st == DeviceState(is_tws=False, battery=77, charging=True)


def test_tws_state_zeroes_the_missing_side():
    # P[0]==2：只有右耳，左耳電量視為 0；P[0]==1：只有左耳
    only_right = parse_device_state(bytes([2, 11, 62, 90, 0]))
    assert (only_right.left_battery, only_right.right_battery) == (0, 62)
    only_left = parse_device_state(bytes([1, 55, 22, 90, 0]))
    assert (only_left.left_battery, only_left.right_battery) == (55, 0)
    assert only_left.box_battery == 90 and only_left.is_tws


def test_device_state_too_short_returns_none():
    assert parse_device_state(bytes([4, 0])) is None
