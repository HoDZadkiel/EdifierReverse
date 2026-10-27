import asyncio

import pytest

from edifier_reverse import commands as C
from edifier_reverse.client import Client, ResponseTimeout
from edifier_reverse.packet import Session, build
from edifier_reverse.probe import SAFE_QUERIES, probe
from edifier_reverse.requests import Request, query, set_anc
from edifier_reverse.responses import parse_anc
from edifier_reverse.sim import SimulatedDevice
from edifier_reverse.ble import PROFILES, session_from_manufacturer_data
from edifier_reverse.packet import PacketError


def run(coro):
    return asyncio.run(coro)


@pytest.mark.parametrize("session", [Session(2, 0), Session(2, 16), Session(1, 0)])
@pytest.mark.parametrize("chunk", [None, 1, 4])
def test_request_response_roundtrip_with_fragmentation(session, chunk):
    async def go():
        dev = SimulatedDevice(session, chunk_size=chunk)
        return await Client(dev, session).request(query(C.BATTERY_QUERY))

    assert run(go()).payload == bytes([80])


def test_the_device_actually_received_the_documented_wire_bytes():
    async def go():
        dev = SimulatedDevice(Session(2, 0))
        await Client(dev, Session(2, 0)).request(query(C.ANC_QUERY))
        return dev.written

    assert run(go()) == [bytes.fromhex("AA EC CC 00 00 62")]


def test_set_anc_changes_state_and_query_reads_it_back():
    async def go():
        s = Session(2, 0)
        c = Client(SimulatedDevice(s), s)
        await c.request(set_anc(mode=2, group=18))
        return (await c.request(query(C.ANC_QUERY))).payload

    assert parse_anc(run(go())).mode == 2


def test_unanswered_command_times_out_with_a_clear_error():
    async def go():
        s = Session(2, 0)
        await Client(SimulatedDevice(s), s, timeout=0.05).request(query(C.EQ_QUERY))

    with pytest.raises(ResponseTimeout, match="0xD5"):
        run(go())


def test_unsolicited_packet_goes_to_on_event_not_to_a_waiter():
    async def go():
        s = Session(2, 0)
        dev, events = SimulatedDevice(s), []
        Client(dev, s, on_event=events.append)
        dev.on_data(build(s, C.ANC_QUERY, b"\x12\x03", header=0xBB))  # 裝置端自己切換 ANC
        return events

    assert [(e.cmd, e.payload) for e in run(go())] == [(C.ANC_QUERY, b"\x12\x03")]


def test_two_requests_for_the_same_command_are_refused():
    async def go():
        s = Session(2, 0)
        c = Client(SimulatedDevice(s), s)
        c._waiting[C.BATTERY_QUERY] = asyncio.get_running_loop().create_future()
        await c.request(query(C.BATTERY_QUERY))

    with pytest.raises(RuntimeError):
        run(go())


def test_firmware_update_cannot_be_sent_through_the_client():
    async def go():
        s = Session(2, 0)
        await Client(SimulatedDevice(s), s).request(Request(C.OTA_START_MASTER))

    with pytest.raises(PacketError, match="韌體更新"):
        run(go())


def test_probe_only_sends_whitelisted_read_only_queries():
    async def go():
        s = Session(2, 0)
        dev = SimulatedDevice(s)
        res = await probe(Client(dev, s), timeout=0.05)
        return dev, res

    dev, res = run(go())
    assert [r["cmd"] for r in res] == SAFE_QUERIES
    # 每個送出的封包都是空 payload 的查詢（v2 封包長度 6）
    assert all(len(w) == 6 for w in dev.written)
    forbidden = {C.ANC_SET, C.EQ_SET, C.CUSTOM_EQ_SET, C.CUSTOM_EQ_SWITCH, C.CUSTOM_EQ_RESET,
                 C.CUSTOM_EQ_NAME_SET, C.DEVICE_RESET, C.SHUTDOWN, C.CLEAR_PAIRING_RECORD}
    assert not forbidden & set(SAFE_QUERIES)


def test_probe_records_timeouts_and_parse_results():
    async def go():
        s = Session(2, 0)
        return {r["cmd"]: r for r in await probe(Client(SimulatedDevice(s), s), timeout=0.05)}

    res = run(go())
    assert res[C.BATTERY_QUERY]["parsed"] == "80"
    assert res[C.EQ_QUERY]["parsed"] == "（無回應）" and res[C.EQ_QUERY]["response_payload"] is None


def test_session_comes_from_the_last_two_manufacturer_bytes():
    assert session_from_manufacturer_data(bytes([1, 2, 3, 2, 16])) == Session(2, 16)
    with pytest.raises(PacketError):
        session_from_manufacturer_data(b"\x01")


def test_ble_profiles_match_the_documented_uuids():
    es, mp = PROFILES["ES850NB"], PROFILES["MP330"]
    assert es.service == "48092101-1a48-11e9-ab14-d663bd873d93"
    assert mp.service == "48094c01-1a48-11e9-ab14-d663bd873d93"
    assert es.read == mp.read and es.write == mp.write  # 兩款共用讀寫特徵值
