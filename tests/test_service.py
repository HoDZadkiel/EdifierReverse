import asyncio
import time

import pytest

from edifier_reverse import commands as C
from edifier_reverse.packet import Session, build
from edifier_reverse.service import DeviceService, ServiceError
from edifier_reverse.sim import SimulatedDevice

SESSION = Session(2, 16)


def run(coro):
    return asyncio.run(coro)


def make(group=16, mode=1, **dev_kw):
    """回傳 (devices, connector)。每次連線都產生新的模擬裝置，方便測重連。"""
    devices = []

    async def connector():
        dev = SimulatedDevice(SESSION, **dev_kw)
        dev.state[C.ANC_QUERY] = bytes([group, mode])
        devices.append(dev)
        return dev, SESSION

    return devices, connector


async def started(connector, **kw):
    svc = DeviceService(connector, backoff=(0.01,), **kw)
    svc.start()
    await svc.wait_connected(2)
    return svc


def anc_sets(dev):
    return [w for w in dev.written if len(w) > 2 and w[2] == C.ANC_SET]


def test_connect_fills_state():
    async def go():
        _, connector = make()
        svc = await started(connector)
        try:
            return svc.state
        finally:
            await svc.stop()

    st = run(go())
    assert st.connected and st.name == "SIM-DEVICE" and st.version == "1.0.2"
    assert st.battery == 80 and (st.anc.group, st.anc.mode) == (16, 1)
    assert st.anc_label == "深度降噪"


def test_back_to_back_sets_are_spaced_so_the_device_never_ignores_them():
    # 實機：兩次設定間隔 <= 1.0 秒第二次會被靜默忽略。這裡的裝置規則是 0.3 秒，節流是 0.35 秒
    async def go():
        devices, connector = make(min_set_interval=0.3, push_anc=True)
        svc = await started(connector, min_set_interval=0.35)
        try:
            await svc.set_anc(2)
            await svc.set_anc(3)
            await svc.set_anc(5)
        finally:
            await svc.stop()
        return devices[0], svc.state

    dev, st = run(go())
    assert dev.ignored_sets == 0 and len(anc_sets(dev)) == 3
    assert st.anc.mode == 5


def test_without_throttle_the_same_sequence_fails_which_shows_the_throttle_matters():
    async def go():
        devices, connector = make(min_set_interval=0.3)
        svc = await started(connector, min_set_interval=0.0, set_timeout=0.05)
        try:
            await svc.set_anc(2)
            with pytest.raises(ServiceError, match="沒有回應"):
                await svc.set_anc(3)
        finally:
            await svc.stop()
        return devices[0]

    assert run(go()).ignored_sets == 2  # 第一次與重送都被忽略


def test_a_set_the_device_ignored_is_resent_once_and_then_succeeds():
    async def go():
        devices, connector = make(min_set_interval=0.2)
        svc = await started(connector, min_set_interval=0.02, set_timeout=0.25)
        try:
            await svc.set_anc(2)
            new = await svc.set_anc(3)  # 太快 -> 被忽略 -> 逾時 -> 0.2 秒已過 -> 重送成功
        finally:
            await svc.stop()
        return devices[0], new

    dev, new = run(go())
    assert dev.ignored_sets == 1 and new.mode == 3


def test_unverified_group_is_status_only_and_nothing_is_sent():
    async def go():
        devices, connector = make(group=18)
        svc = await started(connector)
        try:
            with pytest.raises(ServiceError, match="尚未驗證"):
                await svc.set_anc(2)
            return devices[0], svc.state
        finally:
            await svc.stop()

    dev, st = run(go())
    assert anc_sets(dev) == [] and not st.anc_supported
    assert "未驗證" in st.anc_label


@pytest.mark.parametrize("mode,db,msg", [(9, None, "沒有 mode"), (2, 5, "只有可調增益"),
                                         (4, 0, "必須在"), (4, 13, "必須在")])
def test_invalid_requests_are_rejected_before_sending(mode, db, msg):
    async def go():
        devices, connector = make()
        svc = await started(connector)
        try:
            with pytest.raises(ServiceError, match=msg):
                await svc.set_anc(mode, db)
            return devices[0]
        finally:
            await svc.stop()

    assert anc_sets(run(go())) == []


def test_ambient_mode_sends_db_as_third_byte():
    async def go():
        devices, connector = make(min_set_interval=0.0)
        svc = await started(connector)
        try:
            await svc.set_anc(4, 9)
        finally:
            await svc.stop()
        return devices[0]

    dev = run(go())
    # 加密 0xA5：payload [16, 4, 9]
    assert dev.state[C.ANC_QUERY] == bytes([16, 4, 9])


def test_pushed_anc_change_from_the_device_updates_the_state():
    # 例如使用者按耳機按鍵切換 ANC：沒有對應請求，只有主動推送
    async def go():
        devices, connector = make()
        svc = await started(connector)
        try:
            pkt = build(SESSION, C.ANC_QUERY, bytes([16, 3]), header=0xBB)
            devices[0].on_data(pkt)
            await asyncio.sleep(0.05)
            return svc.state
        finally:
            await svc.stop()

    assert run(go()).anc.mode == 3


def test_disconnect_is_detected_and_the_service_reconnects():
    async def go():
        devices, connector = make()
        seen = []
        svc = DeviceService(connector, backoff=(0.01,), on_change=lambda s: seen.append(s.connected))
        svc.start()
        await svc.wait_connected(2)
        try:
            devices[0].disconnect()
            for _ in range(100):
                if len(devices) == 2 and svc.state.connected:
                    break
                await asyncio.sleep(0.02)
            return len(devices), svc.state.connected, seen
        finally:
            await svc.stop()

    n, connected, seen = run(go())
    assert n == 2 and connected
    assert False in seen  # 中間確實有過未連線狀態


def test_connect_failure_is_reported_and_retried():
    async def go():
        calls = []

        async def connector():
            calls.append(1)
            if len(calls) < 3:
                raise ConnectionError("掃不到裝置")
            dev = SimulatedDevice(SESSION)
            dev.state[C.ANC_QUERY] = bytes([16, 1])
            return dev, SESSION

        errors = []
        svc = DeviceService(connector, backoff=(0.01,), on_change=lambda s: errors.append(s.error))
        svc.start()
        try:
            await svc.wait_connected(2)
            return len(calls), errors
        finally:
            await svc.stop()

    n, errors = run(go())
    assert n == 3 and any("掃不到裝置" in e for e in errors) and errors[-1] == ""


def test_set_when_not_connected_is_an_error():
    async def go():
        _, connector = make()
        svc = DeviceService(connector)
        with pytest.raises(ServiceError, match="尚未連上"):
            await svc.set_anc(2)

    run(go())


def test_cli_status_and_anc_print_results(capsys):
    import argparse

    from edifier_reverse.cli import run_service

    async def go(args):
        _, connector = make(min_set_interval=0.0)
        await run_service(connector, args)

    run(go(argparse.Namespace(cmd="status", timeout=2.0)))
    out = capsys.readouterr().out
    assert "SIM-DEVICE" in out and "電量：80%" in out and "深度降噪" in out

    run(go(argparse.Namespace(cmd="anc", timeout=2.0, mode=3, db=None)))
    assert "已切換：防風噪" in capsys.readouterr().out

    with pytest.raises(SystemExit, match="只有可調增益"):
        run(go(argparse.Namespace(cmd="anc", timeout=2.0, mode=2, db=5)))

def test_device_available_notice_cuts_the_backoff_short():
    async def go():
        calls = []
        up = []

        async def connector():
            calls.append(1)
            if not up:
                raise ConnectionError("裝置還沒開")
            dev = SimulatedDevice(SESSION)
            dev.state[C.ANC_QUERY] = bytes([16, 1])
            return dev, SESSION

        svc = DeviceService(connector, backoff=(30.0,))  # 不通知的話要等 30 秒
        svc.start()
        try:
            for _ in range(100):
                if calls:
                    break
                await asyncio.sleep(0.01)
            await asyncio.sleep(0.1)  # 讓服務進入退避等待
            up.append(1)
            svc.notify_device_available()
            await svc.wait_connected(3)
            return len(calls)
        finally:
            await svc.stop()

    assert run(go()) == 2


def test_device_available_notice_does_not_drop_a_healthy_connection():
    async def go():
        devices, connector = make()
        svc = await started(connector)
        try:
            svc.notify_device_available()  # 已連線：必須忽略，否則會把好的連線斷掉重連
            await asyncio.sleep(0.3)
            return len(devices), svc.state.connected
        finally:
            await svc.stop()

    assert run(go()) == (1, True)


def test_has_anc_is_false_when_the_device_never_answers_the_anc_query_and_true_again_after_it_does():
    # MP330：沒有 ANC，查詢無回應；電量查詢有回應所以連線仍成立
    async def go():
        devices = []

        async def connector():
            dev = SimulatedDevice(SESSION)
            del dev.state[C.ANC_QUERY]
            devices.append(dev)
            return dev, SESSION

        svc = DeviceService(connector, backoff=(0.01,), query_timeout=0.2)
        svc.start()
        try:
            st = await svc.wait_connected(5)
            assert (st.connected, st.anc, st.has_anc) == (True, None, False)
            devices[0].state[C.ANC_QUERY] = bytes([16, 2])   # 之後有回應（例如暫時逾時後恢復）就不再隱藏
            await svc._refresh()
            return svc.state
        finally:
            await svc.stop()

    assert run(go()).has_anc is True
