"""connect_rfcomm：不碰真的藍牙，用假 socket 與假 ws2_32 驗證連線流程。"""
import asyncio
import ctypes
import socket
import struct
import types
import uuid

import pytest

from edifier_reverse import spp
from edifier_reverse.spp import SPP_UUID, connect_rfcomm, sockaddr_bth

ADDR = "AA:BB:CC:DD:EE:FF"
WSAEWOULDBLOCK, WSAENETDOWN, WSAECONNREFUSED = 10035, 10050, 10061

windows_only = pytest.mark.skipif(not hasattr(ctypes, "set_last_error"), reason="需要 Windows 的 ctypes")


def run(coro):
    return asyncio.run(coro)


class FakeSock:
    created = []

    def __init__(self, family, type_, proto, *, connect_error=None, so_error=0):
        self.args = (family, type_, proto)
        self.connected_to = None
        self.timeouts = []
        self.closed = False
        self.connect_error = connect_error
        self.so_error = so_error
        FakeSock.created.append(self)

    def fileno(self):
        return 4242

    def settimeout(self, t):
        self.timeouts.append(t)

    def connect(self, addr):
        if self.connect_error:
            raise self.connect_error
        self.connected_to = addr

    def getsockopt(self, level, opt):
        return self.so_error

    def recv(self, n):
        return b""  # 讓讀取執行緒立刻結束

    def shutdown(self, how):
        pass

    def close(self):
        self.closed = True


class FakeWs2:
    """假的 ws2_32.connect：記錄收到的 handle 與結構位元組，依設定回傳結果。"""

    def __init__(self, rc=0, last_error=0):
        self.rc, self.last_error = rc, last_error
        self.calls = []

    def connect(self, handle, ptr, size):
        self.calls.append((handle, bytes(ptr._obj), size))
        if self.rc != 0:
            ctypes.set_last_error(self.last_error)
        return self.rc


def install(monkeypatch, *, ws2=None, connect_error=None, so_error=0, select_result=None,
            with_bluetooth=True):
    """只替換 spp 模組看到的名稱；不能改全域 socket.socket，asyncio 自己也要用它。"""
    FakeSock.created.clear()
    ns = types.SimpleNamespace(
        SOCK_STREAM=socket.SOCK_STREAM, SHUT_RDWR=socket.SHUT_RDWR,
        SOL_SOCKET=socket.SOL_SOCKET, SO_ERROR=socket.SO_ERROR,
        socket=lambda *a: FakeSock(*a, connect_error=connect_error, so_error=so_error))
    if with_bluetooth:
        ns.AF_BLUETOOTH, ns.BTPROTO_RFCOMM = socket.AF_BLUETOOTH, socket.BTPROTO_RFCOMM
    monkeypatch.setattr(spp, "socket", ns)
    monkeypatch.setattr(spp, "_ws2_32", lambda: ws2)
    if select_result is not None:
        monkeypatch.setattr(spp, "_select", lambda r, w, x, t: select_result(w, x))
    return ns


def connect(**kw):
    async def go():
        tr = await connect_rfcomm(ADDR, **kw)
        await tr.close()
        return tr
    return run(go())


# ---- 結構 --------------------------------------------------------------

def test_sockaddr_bth_matches_the_winsock_layout():
    s = sockaddr_bth(ADDR, SPP_UUID)
    assert ctypes.sizeof(s) == 30  # 2 + 8 + 16 + 4，緊密排列
    # 預期值獨立用 struct 組出來：family、48 位元位址、GUID（前三段小端序）、port
    expected = struct.pack("<HQ16sI", socket.AF_BLUETOOTH, 0xAABBCCDDEEFF,
                           uuid.UUID(SPP_UUID).bytes_le, 0)
    assert bytes(s) == expected


def test_sockaddr_bth_port_zero_is_what_triggers_the_sdp_lookup():
    assert sockaddr_bth(ADDR, SPP_UUID).port == 0
    assert sockaddr_bth(ADDR, channel=5).port == 5


@pytest.mark.parametrize("bad", ["AA:BB", "AABBCCDDEEFF", "AA:BB:CC:DD:EE:GG", "A:BB:CC:DD:EE:FF", ""])
def test_sockaddr_bth_rejects_malformed_addresses(bad):
    with pytest.raises(ValueError):
        sockaddr_bth(bad, SPP_UUID)


# ---- 預設：先 UUID 查詢 --------------------------------------------------

@windows_only
def test_default_tries_the_uuid_lookup_first(monkeypatch):
    ws2 = FakeWs2(rc=0)
    install(monkeypatch, ws2=ws2)
    tr = connect()
    handle, raw, size = ws2.calls[0]
    assert handle == 4242 and size == 30
    assert raw == bytes(sockaddr_bth(ADDR, SPP_UUID))
    assert "UUID" in tr.how
    assert len(FakeSock.created) == 1 and FakeSock.created[0].connected_to is None  # 沒用到 channel


@windows_only
def test_uuid_lookup_waits_for_the_nonblocking_connect_to_finish(monkeypatch):
    ws2 = FakeWs2(rc=-1, last_error=WSAEWOULDBLOCK)
    install(monkeypatch, ws2=ws2, select_result=lambda w, x: ([], list(w), []))
    tr = connect(timeout=7)
    assert "UUID" in tr.how
    assert FakeSock.created[0].timeouts == [7, None]  # 連線期間有逾時，連上後恢復阻塞


# ---- UUID 失敗 → 官方的備援：直連 channel 1 ---------------------------------

@windows_only
@pytest.mark.parametrize("name,ws2,select_result,so_error", [
    ("系統拒絕", FakeWs2(rc=-1, last_error=WSAENETDOWN), None, 0),
    ("逾時", FakeWs2(rc=-1, last_error=WSAEWOULDBLOCK), lambda w, x: ([], [], []), 0),
    ("連線被拒", FakeWs2(rc=-1, last_error=WSAEWOULDBLOCK), lambda w, x: ([], [], list(x)), WSAECONNREFUSED),
    # 可寫不代表成功：必須再檢查 SO_ERROR，否則會把失敗的連線當成成功
    ("可寫但帶錯誤碼", FakeWs2(rc=-1, last_error=WSAEWOULDBLOCK), lambda w, x: ([], list(w), []), WSAECONNREFUSED),
])
def test_uuid_failure_falls_back_to_channel_1_on_a_fresh_socket(monkeypatch, name, ws2, select_result, so_error):
    install(monkeypatch, ws2=ws2, select_result=select_result, so_error=so_error)
    tr = connect()
    first, second = FakeSock.created
    assert first.closed and first is not second  # 失敗的 socket 不能重用
    assert second.connected_to == (ADDR, 1)
    assert tr.how == "直連 channel 1"


def test_missing_winsock_support_also_falls_back_to_channel_1(monkeypatch):
    def boom():
        raise RuntimeError("用 UUID 查詢 RFCOMM channel 只支援 Windows")

    install(monkeypatch)
    monkeypatch.setattr(spp, "_ws2_32", boom)
    assert connect().how == "直連 channel 1"


# ---- 指定 channel：不做 UUID 查詢 ----------------------------------------

def test_explicit_channel_skips_the_uuid_lookup(monkeypatch):
    class Forbidden:
        def connect(self, *a):
            raise AssertionError("指定 channel 時不該呼叫 ws2_32")

    install(monkeypatch, ws2=Forbidden())
    tr = connect(channel=3, timeout=5)
    sock = FakeSock.created[0]
    assert sock.args == (socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    assert sock.connected_to == (ADDR, 3) and sock.timeouts == [5, None]
    assert tr.how == "直連 channel 3"


# ---- 全部失敗 -----------------------------------------------------------

@windows_only
def test_all_attempts_failing_reports_each_attempt_and_what_to_check(monkeypatch):
    install(monkeypatch, ws2=FakeWs2(rc=-1, last_error=WSAENETDOWN), connect_error=OSError("被拒"))
    with pytest.raises(ConnectionError) as e:
        run(connect_rfcomm(ADDR))
    msg = str(e.value)
    assert "UUID 查詢" in msg and "10050" in msg  # 第一次嘗試與原因
    assert "channel 1" in msg and "被拒" in msg   # 備援與原因
    assert "配對" in msg and "--channel" in msg    # 下一步怎麼做
    assert all(s.closed for s in FakeSock.created)  # 沒有漏關的 socket


def test_explicit_channel_failure_mentions_only_that_channel(monkeypatch):
    install(monkeypatch, ws2=None, connect_error=OSError("被拒"))
    with pytest.raises(ConnectionError) as e:
        run(connect_rfcomm(ADDR, channel=7))
    assert "channel 7" in str(e.value) and "UUID" not in str(e.value)


def test_without_bluetooth_sockets_says_so(monkeypatch):
    install(monkeypatch, with_bluetooth=False)
    with pytest.raises(RuntimeError, match="RFCOMM"):
        run(connect_rfcomm(ADDR))


# ---- 對真的 Winsock 的冒煙測試 -------------------------------------------

@windows_only
def test_real_winsock_accepts_the_call_without_a_malformed_argument_error():
    """沒有藍牙卡或裝置時一定失敗；這裡只確認失敗原因不是參數或位址格式錯誤
    （WSAEFAULT 10014、WSAEINVAL 10022）。注意：沒有藍牙卡時系統可能在檢查結構前就失敗，
    所以這只是接線的基本檢查，不能證明結構正確。"""
    try:
        s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    except OSError:
        pytest.skip("這台電腦無法建立 RFCOMM socket")
    try:
        with pytest.raises(OSError) as e:
            spp._connect_by_uuid(s, ADDR, SPP_UUID, timeout=0.5)
        assert e.value.errno not in (10014, 10022)
    finally:
        s.close()
