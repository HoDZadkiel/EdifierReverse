import pytest

from edifier_reverse import commands as C
from edifier_reverse.packet import Session, build
from edifier_reverse.reassembly import Reassembler

V2 = Session(2, 0)
V1 = Session(1, 0)


def _wire(session, cmd, payload, header=0xBB):
    return build(session, cmd, payload, header=header)


def test_single_chunk_yields_one_packet():
    r = Reassembler(V2)
    pkts = r.feed(_wire(V2, C.BATTERY_QUERY, b"\x50"))
    assert [(p.cmd, p.payload) for p in pkts] == [(C.BATTERY_QUERY, b"\x50")]


@pytest.mark.parametrize("step", [1, 2, 3, 5])
def test_packet_split_into_fragments_is_reassembled(step):
    wire = _wire(V2, C.NAME_QUERY, b"hello-world")
    r = Reassembler(V2)
    out = []
    for i in range(0, len(wire), step):
        out += r.feed(wire[i:i + step])
    assert [p.payload for p in out] == [b"hello-world"]


def test_nothing_is_emitted_until_the_packet_is_complete():
    wire = _wire(V2, C.NAME_QUERY, b"abc")
    r = Reassembler(V2)
    assert r.feed(wire[:-1]) == []
    assert len(r.feed(wire[-1:])) == 1


def test_two_packets_in_one_notification_are_both_returned():
    # 官方 App 的 subPackageOfNew：一筆通知可夾帶多包
    a, b = _wire(V2, C.BATTERY_QUERY, b"\x50"), _wire(V2, C.ANC_QUERY, b"\x12\x04")
    pkts = Reassembler(V2).feed(a + b)
    assert [p.cmd for p in pkts] == [C.BATTERY_QUERY, C.ANC_QUERY]


def test_one_complete_packet_followed_by_the_start_of_another():
    a, b = _wire(V2, C.BATTERY_QUERY, b"\x50"), _wire(V2, C.ANC_QUERY, b"\x12\x04")
    r = Reassembler(V2)
    assert [p.cmd for p in r.feed(a + b[:4])] == [C.BATTERY_QUERY]
    assert [p.cmd for p in r.feed(b[4:])] == [C.ANC_QUERY]


def test_new_header_discards_a_stale_partial_packet():
    stale = _wire(V2, C.NAME_QUERY, b"abcdef")[:6]  # 永遠不會補齊
    fresh = _wire(V2, C.BATTERY_QUERY, b"\x50")
    r = Reassembler(V2)
    assert r.feed(stale) == []
    assert [p.cmd for p in r.feed(fresh)] == [C.BATTERY_QUERY]


def test_bad_crc_packet_is_dropped_and_counted_without_poisoning_the_next():
    bad = bytearray(_wire(V2, C.BATTERY_QUERY, b"\x50"))
    bad[-1] ^= 0xFF
    good = _wire(V2, C.ANC_QUERY, b"\x12\x04")
    r = Reassembler(V2)
    pkts = r.feed(bytes(bad) + good)
    assert [p.cmd for p in pkts] == [C.ANC_QUERY]
    assert r.dropped == 1


def test_garbage_without_a_header_is_ignored():
    r = Reassembler(V2)
    assert r.feed(b"\x01\x02\x03\x04\x05\x06\x07") == []
    assert r.dropped == 0
    assert len(r.feed(_wire(V2, C.BATTERY_QUERY, b"\x50"))) == 1


def test_known_limitation_continuation_starting_with_header_byte_resets():
    # 官方 App 同樣的弱點：後續分片恰好以 0xBB 開頭會被當成新封包。
    # 這個測試把限制固定下來，若日後改善演算法需同時更新文件。
    wire = _wire(V2, C.NAME_QUERY, bytes([0x41, 0xBB, 0x42, 0x43]))
    cut = wire.index(0xBB, 1)  # payload 內的 0xBB
    r = Reassembler(V2)
    r.feed(wire[:cut])
    assert r.feed(wire[cut:]) == []


def test_v1_fragments_and_multi_packet():
    a = _wire(V1, C.BATTERY_QUERY, b"\x50", header=0xCC)
    b = _wire(V1, C.ANC_QUERY, b"\x03", header=0xCC)
    r = Reassembler(V1)
    assert [p.cmd for p in r.feed(a[:3])] == []
    assert [p.cmd for p in r.feed(a[3:] + b)] == [C.BATTERY_QUERY, C.ANC_QUERY]


def test_encrypted_payload_is_decrypted_after_reassembly():
    s = Session(2, 16)
    wire = _wire(s, C.BATTERY_QUERY, b"\x50")
    r = Reassembler(s)
    out = r.feed(wire[:3]) + r.feed(wire[3:])
    assert out[0].payload == b"\x50"
