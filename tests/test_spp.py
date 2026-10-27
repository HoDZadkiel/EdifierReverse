import asyncio
import socket
import threading

import pytest

from edifier_reverse import commands as C
from edifier_reverse import spp
from edifier_reverse.ble import (PROFILES, EDIFIER_COMPANY_ID, Advert,
                                 a2dp_address_from_manufacturer_data, choose_advert)
from edifier_reverse.cli import PlanError, plan_connection
from edifier_reverse.client import Client, ResponseTimeout
from edifier_reverse.packet import PacketError, Session, build
from edifier_reverse.probe import probe
from edifier_reverse.requests import query
from edifier_reverse.sim import SimulatedDevice, serve_socket
from edifier_reverse.spp import SocketTransport


def run(coro):
    return asyncio.run(coro)


def _wire_req(session, cmd, payload=b""):
    return build(session, cmd, payload)  # App 送出的封包，header 0xAA


# ---- 模擬裝置的串流語意 ----------------------------------------------------

def test_device_handles_two_requests_glued_into_one_read():
    s = Session(2, 0)
    dev = SimulatedDevice(s)
    out = dev.feed(_wire_req(s, C.BATTERY_QUERY) + _wire_req(s, C.ANC_QUERY))
    assert len(out) == 2  # 串流不保證一次 read 一個封包


def test_device_waits_for_the_rest_of_a_split_request():
    s = Session(2, 0)
    dev, wire = SimulatedDevice(s), _wire_req(s, C.BATTERY_QUERY)
    assert dev.feed(wire[:4]) == []
    assert len(dev.feed(wire[4:])) == 1


def test_device_ignores_a_request_with_bad_crc():
    s = Session(2, 0)
    wire = bytearray(_wire_req(s, C.BATTERY_QUERY))
    wire[-1] ^= 0xFF
    assert SimulatedDevice(s).feed(bytes(wire)) == []


# ---- SocketTransport：用真的 socket 對跑 ---------------------------------

def _pair(session, chunk=None):
    dev = SimulatedDevice(session, chunk_size=chunk)
    app_side, dev_side = socket.socketpair()
    t = threading.Thread(target=serve_socket, args=(dev, dev_side), daemon=True)
    t.start()
    return dev, app_side, dev_side, t


@pytest.mark.parametrize("session", [Session(2, 0), Session(2, 16), Session(1, 0)])
@pytest.mark.parametrize("chunk", [None, 1, 3])
def test_request_response_over_a_real_socket(session, chunk):
    async def go():
        dev, app_side, dev_side, _ = _pair(session, chunk)
        tr = SocketTransport(app_side)
        try:
            return await Client(tr, session).request(query(C.BATTERY_QUERY))
        finally:
            await tr.close()
            dev_side.close()

    assert run(go()).payload == bytes([80])


def test_full_probe_over_socket_matches_probe_over_direct_simulation():
    async def direct():
        s = Session(2, 0)
        return await probe(Client(SimulatedDevice(s), s), timeout=0.3)

    async def over_socket():
        s = Session(2, 0)
        _, app_side, dev_side, _ = _pair(s, chunk=4)
        tr = SocketTransport(app_side)
        try:
            return await probe(Client(tr, s), timeout=0.3)
        finally:
            await tr.close()
            dev_side.close()

    a, b = run(direct()), run(over_socket())
    assert [(r["cmd"], r["response_payload"], r["parsed"]) for r in a] == \
           [(r["cmd"], r["response_payload"], r["parsed"]) for r in b]


def test_close_stops_the_reader_thread_and_is_idempotent():
    async def go():
        s = Session(2, 0)
        _, app_side, dev_side, _ = _pair(s)
        tr = SocketTransport(app_side)
        await tr.close()
        await tr.close()  # 第二次不能出錯
        alive = tr._thread.is_alive()
        with pytest.raises(ConnectionError):
            await tr.write(b"x")
        dev_side.close()
        return alive

    assert run(go()) is False


def test_peer_hanging_up_does_not_hang_the_client():
    async def go():
        s = Session(2, 0)
        app_side, dev_side = socket.socketpair()
        tr = SocketTransport(app_side)
        dev_side.close()  # 裝置端直接斷線
        try:
            await Client(tr, s, timeout=0.3).request(query(C.BATTERY_QUERY))
        finally:
            thread_alive = tr._thread.is_alive()
            await tr.close()
        return thread_alive

    # 可能在寫入時就收到重設（ConnectionError），也可能寫入成功後逾時；兩者都不能卡住
    try:
        thread_alive = run(go())
    except (ResponseTimeout, ConnectionError):
        return
    assert thread_alive is False


# ---- 廣播資料、傳輸選擇 ---------------------------------------------------

MFR = bytes.fromhex("A1B2C3D4E5F6") + b"\x07" + bytes([2, 16])  # A2DP MAC + 其他 + version + encryption


def _entry(addr, rssi, uuid, mfr=MFR, company=EDIFIER_COMPANY_ID):
    return (addr, rssi, [uuid], {company: mfr})


def test_a2dp_address_is_the_first_six_bytes_in_order():
    assert a2dp_address_from_manufacturer_data(MFR) == "A1:B2:C3:D4:E5:F6"
    with pytest.raises(PacketError):
        a2dp_address_from_manufacturer_data(b"\x01\x02")


def test_choose_advert_matches_model_by_search_uuid():
    es, mp = PROFILES["ES850NB"], PROFILES["MP330"]
    entries = [_entry("11:11", -50, es.search_uuid)]
    adv = choose_advert(entries, es)
    assert adv == Advert("11:11", -50, Session(2, 16), "A1:B2:C3:D4:E5:F6")
    assert choose_advert(entries, mp) is None  # 另一款不能被誤抓


def test_choose_advert_prefers_strongest_signal_and_honours_address_filter():
    es = PROFILES["ES850NB"]
    entries = [_entry("11:11", -80, es.search_uuid), _entry("22:22", -40, es.search_uuid)]
    assert choose_advert(entries, es).ble_address == "22:22"
    assert choose_advert(entries, es, address="11:11").ble_address == "11:11"
    assert choose_advert(entries, es, address="33:33") is None


def test_choose_advert_ignores_wrong_company_or_short_data():
    es = PROFILES["ES850NB"]
    assert choose_advert([_entry("1", -1, es.search_uuid, company=0x004C)], es) is None
    assert choose_advert([_entry("1", -1, es.search_uuid, mfr=bytes(5))], es) is None


def test_profiles_follow_the_official_transport_rule():
    # 官方 App：產品表有 SPP UUID 就走 SPP，否則走 BLE
    assert PROFILES["ES850NB"].default_transport == "spp"
    assert PROFILES["MP330"].default_transport == "ble"
    assert PROFILES["ES850NB"].spp == spp.SPP_UUID


ADV = Advert("11:11", -50, Session(2, 16), "A1:B2:C3:D4:E5:F6")


def test_plan_auto_picks_spp_for_es850nb_and_uses_the_a2dp_address():
    p = plan_connection("ES850NB", "auto", ADV, None, None, None, 0)
    assert (p.transport, p.target, p.session) == ("spp", "A1:B2:C3:D4:E5:F6", Session(2, 16))


def test_plan_auto_picks_ble_for_mp330_and_uses_the_ble_address():
    p = plan_connection("MP330", "auto", ADV, None, None, None, 0)
    assert (p.transport, p.target) == ("ble", "11:11")


def test_plan_refuses_spp_for_a_model_without_spp_uuid():
    with pytest.raises(PlanError, match="MP330"):
        plan_connection("MP330", "spp", ADV, None, None, None, 0)


def test_impossible_transport_is_rejected_before_any_scanning(monkeypatch):
    # 沒有藍牙卡的機器上，錯誤必須是 PlanError，而不是掃描時才冒出的藍牙錯誤
    from edifier_reverse import ble
    from edifier_reverse.cli import _probe_real
    import argparse

    async def boom(*a, **k):
        raise AssertionError("不該走到掃描")

    monkeypatch.setattr(ble, "discover", boom)
    args = argparse.Namespace(model="MP330", transport="spp", address=None, a2dp_address=None,
                              version=None, encryption=0, channel=1, log=None)
    with pytest.raises(PlanError):
        run(_probe_real(args))


def test_plan_warns_when_forcing_ble_on_a_spp_model():
    p = plan_connection("ES850NB", "ble", ADV, None, None, None, 0)
    assert p.transport == "ble" and any("SPP" in w for w in p.warnings)


def test_plan_explicit_version_overrides_the_advertisement():
    p = plan_connection("ES850NB", "auto", ADV, None, None, 1, 0)
    assert p.session == Session(1, 0)


def test_plan_without_advert_needs_an_explicit_target_and_warns_about_default_session():
    with pytest.raises(PlanError, match="A2DP"):
        plan_connection("ES850NB", "auto", None, None, None, None, 0)
    p = plan_connection("ES850NB", "auto", None, None, "AA:BB:CC:DD:EE:FF", None, 0)
    assert p.session == Session() and p.warnings
