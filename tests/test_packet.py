import pytest

from edifier_reverse import commands as C
from edifier_reverse.packet import (Packet, PacketError, Session, build, checksum,
                                    expected_length, parse, xor_payload)

V2 = Session(version=2, encryption=0)
V1 = Session(version=1, encryption=0)
V2_XOR = Session(version=2, encryption=16)


# 向量皆為手算：v2 CRC = 總和 & 0xFF；v1 CRC = (總和 + 8217) 16 bit 大端序
def test_build_v2_query_matches_hand_computed_bytes():
    # 0xAA + 0xEC + 0xCC = 610 -> 0x262 -> CRC 0x62
    assert build(V2, C.ANC_QUERY) == bytes.fromhex("AA EC CC 00 00 62")


def test_build_v1_query_adds_8217_to_checksum():
    # 0xAA + 0x01 + 0xCC = 375; 375 + 8217 = 8592 = 0x2190
    assert build(V1, C.ANC_QUERY) == bytes.fromhex("AA 01 CC 21 90")


def test_v1_length_byte_counts_the_command_byte():
    pkt = build(V1, C.ANC_SET, b"\x02")
    assert pkt[1] == 2  # cmd + 1 byte payload
    assert expected_length(V1, pkt) == len(pkt)


def test_v2_length_field_is_big_endian_payload_size():
    pkt = build(V2, C.CUSTOM_EQ_SWITCH, bytes(300))
    assert pkt[3:5] == (300).to_bytes(2, "big")
    assert expected_length(V2, pkt) == len(pkt)


def test_parse_v2_battery_response():
    # 187 + 236 + 208 + 0 + 1 + 80 = 712 -> 0xC8
    pkt = parse(V2, bytes.fromhex("BB EC D0 00 01 50 C8"))
    assert pkt == Packet(0xBB, C.BATTERY_QUERY, b"\x50", 0xEC)


def test_xor_payload_is_symmetric_and_only_for_encryption_16():
    data = b"\x00\x50\xFF"
    assert xor_payload(xor_payload(data, 16), 16) == data
    assert xor_payload(data, 16) == bytes([0xA5, 0xF5, 0x5A])
    assert xor_payload(data, 0) == data
    assert xor_payload(data, 99) == data  # 未知加密碼：官方 App 同樣原樣放行


def test_crc_covers_the_encrypted_payload_not_the_plain_one():
    # payload 0x50 加密後為 0xF5；CRC 以 0xF5 計算：632 + 245 = 877 -> 0x6D
    wire = bytes.fromhex("BB EC D0 00 01 F5 6D")
    assert parse(V2_XOR, wire).payload == b"\x50"
    # 若 CRC 誤以明文 0x50 計算（0xC8），必須被拒絕
    with pytest.raises(PacketError, match="CRC"):
        parse(V2_XOR, bytes.fromhex("BB EC D0 00 01 F5 C8"))


def test_build_then_parse_roundtrip_with_encryption():
    wire = build(V2_XOR, C.ANC_SET, b"\x05\x02", header=0xBB)
    assert parse(V2_XOR, wire).payload == b"\x05\x02"


def test_build_then_parse_roundtrip_v1():
    wire = build(V1, C.ANC_SET, b"\x02", header=0xCC)
    assert parse(V1, wire) == Packet(0xCC, C.ANC_SET, b"\x02")


def test_send_header_0xAA_is_not_accepted_as_a_response():
    # 官方 App 的 checkCommand 只接受 0xBB / 0xCC
    with pytest.raises(PacketError, match="header"):
        parse(V2, build(V2, C.BATTERY_QUERY))


def test_wrong_app_code_is_rejected_in_v2():
    body = bytes.fromhex("BB ED D0 00 00")
    wire = body + checksum(body, 2)
    with pytest.raises(PacketError, match="appCode"):
        parse(V2, wire)


def test_truncated_and_oversized_packets_are_rejected():
    good = bytes.fromhex("BB EC D0 00 01 50 C8")
    with pytest.raises(PacketError, match="長度"):
        parse(V2, good[:-1])
    with pytest.raises(PacketError, match="長度"):
        parse(V2, good + b"\x00")
    with pytest.raises(PacketError, match="資料不足"):
        parse(V2, good[:3])


def test_expected_length_needs_enough_bytes():
    assert expected_length(V2, b"\xBB\xEC\xD0\x00") is None
    assert expected_length(V1, b"\xBB") is None


@pytest.mark.parametrize("cmd", [C.OTA_READY, C.OTA_START_MASTER, C.OTA_START_SUB])
def test_firmware_update_commands_cannot_be_built(cmd):
    # CLAUDE.md 禁止事項：OTA 可能讓裝置變磚
    with pytest.raises(PacketError, match="韌體更新"):
        build(V2, cmd)


def test_oversized_payloads_are_rejected():
    with pytest.raises(PacketError):
        build(V1, C.ANC_SET, bytes(255))  # length 欄位只有 1 byte
    with pytest.raises(PacketError):
        build(V2, C.ANC_SET, bytes(0x10000))
