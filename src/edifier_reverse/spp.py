"""SPP（RFCOMM）傳輸層。行為對照官方 App 的 SPPManager（靜態分析，尚未實機驗證）。

官方做法：對已連線的 A2DP 裝置，用 UUID EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF 做 SDP 查詢後連 RFCOMM
（createRfcommSocketToServiceRecord）；失敗時改直接連 channel 1。收發都是原始封包位元組串流，沒有額外外框，
每次最多讀 1024 位元組，讀到的資料交給與 BLE 相同的重組流程。

Windows 上 Python 的 socket 模組只能指定 channel 號，所以「用 UUID 查詢」改走 Winsock 的 SOCKADDR_BTH：
結構裡的 serviceClassId 填 UUID、port 填 0，由系統對該 UUID 做 SDP 查詢後連線（等同 Android 的做法）。
這裡直接用 ctypes 呼叫 ws2_32.connect，連上後 socket 物件照常使用。
「port=0 時系統會自動做 SDP」依據的是對微軟文件的理解，尚未實機驗證，所以保留官方的備援順序。
"""
import asyncio
import ctypes
import select
import socket
import threading
import uuid as _uuid
from typing import Callable, List, Optional

from .transport import Transport

SPP_UUID = "EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF"
DEFAULT_CHANNEL = 1
READ_SIZE = 1024
WSAEWOULDBLOCK = 10035

_select = select.select  # 測試時可替換


class SOCKADDR_BTH(ctypes.Structure):
    """Winsock 的藍牙位址結構（緊密排列，共 30 位元組）。"""

    _pack_ = 1
    _fields_ = [
        ("addressFamily", ctypes.c_ushort),
        ("btAddr", ctypes.c_ulonglong),
        ("serviceClassId", ctypes.c_ubyte * 16),
        ("port", ctypes.c_uint32),
    ]


def sockaddr_bth(address: str, service_uuid: Optional[str] = None, channel: int = 0) -> SOCKADDR_BTH:
    """address 形如 "AA:BB:CC:DD:EE:FF"。給 service_uuid 且 channel=0 時由系統做 SDP 查詢。"""
    parts = address.split(":")
    if len(parts) != 6 or not all(len(p) == 2 for p in parts):
        raise ValueError(f"藍牙位址格式錯誤：{address!r}")
    s = SOCKADDR_BTH()
    s.addressFamily = socket.AF_BLUETOOTH
    s.btAddr = int("".join(parts), 16)
    if service_uuid:
        s.serviceClassId = (ctypes.c_ubyte * 16).from_buffer_copy(_uuid.UUID(service_uuid).bytes_le)
    s.port = channel
    return s


def _ws2_32():
    """載入 ws2_32 並設定 connect 的簽章；非 Windows 沒有 WinDLL。"""
    if not hasattr(ctypes, "WinDLL"):
        raise RuntimeError("用 UUID 查詢 RFCOMM channel 只支援 Windows")
    dll = ctypes.WinDLL("ws2_32", use_last_error=True)
    dll.connect.argtypes = [ctypes.c_size_t, ctypes.c_void_p, ctypes.c_int]
    dll.connect.restype = ctypes.c_int
    return dll


def _winsock_error(code: int) -> OSError:
    return OSError(code, f"WinSock 錯誤碼 {code}")


class SocketTransport(Transport):
    """把已連線的阻塞式 socket 包成 Transport：背景執行緒讀、寫用 executor。"""

    def __init__(self, sock: socket.socket, loop: Optional[asyncio.AbstractEventLoop] = None):
        self._sock = sock
        self._loop = loop or asyncio.get_running_loop()
        self._closed = False
        self.how = ""  # 這條連線是怎麼建立的，供紀錄用（connect_rfcomm 會填）
        self._thread = threading.Thread(target=self._read_loop, name="spp-reader", daemon=True)
        self._thread.start()

    def _read_loop(self) -> None:
        try:
            while not self._closed:
                try:
                    data = self._sock.recv(READ_SIZE)
                except OSError:
                    return
                if not data:
                    return  # 對端關閉
                self._loop.call_soon_threadsafe(self._deliver, data)
        except RuntimeError:
            return  # event loop 已關閉
        finally:
            if not self._closed:
                try:
                    self._loop.call_soon_threadsafe(self._notify_close)
                except RuntimeError:
                    pass

    def _deliver(self, data: bytes) -> None:
        if self.on_data is not None:
            self.on_data(data)

    def _notify_close(self) -> None:
        if not self._closed and self.on_close is not None:
            self.on_close()

    async def write(self, data: bytes) -> None:
        if self._closed:
            raise ConnectionError("SPP 連線已關閉")
        await self._loop.run_in_executor(None, self._sock.sendall, data)

    async def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        try:
            self._sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self._sock.close()
        await self._loop.run_in_executor(None, self._thread.join, 2.0)


def _connect_by_channel(sock, address: str, channel: int, timeout: float) -> None:
    sock.settimeout(timeout)
    sock.connect((address, channel))
    sock.settimeout(None)


def _connect_by_uuid(sock, address: str, service_uuid: str, timeout: float) -> None:
    """用 SOCKADDR_BTH（serviceClassId + port=0）直接呼叫 ws2_32.connect，讓系統做 SDP 查詢。"""
    addr = sockaddr_bth(address, service_uuid)
    sock.settimeout(timeout)  # 底層變成非阻塞：connect 會立刻回傳 WSAEWOULDBLOCK
    if _ws2_32().connect(sock.fileno(), ctypes.byref(addr), ctypes.sizeof(addr)) != 0:
        err = ctypes.get_last_error()
        if err != WSAEWOULDBLOCK:
            raise _winsock_error(err)
        _, writable, failed = _select([], [sock], [sock], timeout)
        if not writable and not failed:
            raise TimeoutError("RFCOMM 連線逾時")
        code = sock.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR)
        if code:
            raise _winsock_error(code)
        if failed and not writable:
            raise OSError("RFCOMM 連線失敗")
    sock.settimeout(None)


async def _open(address: str, connect: Callable[[socket.socket], None], how: str) -> SocketTransport:
    """每次嘗試都用新的 socket（連線失敗的 socket 不能重用）。"""
    sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    loop = asyncio.get_running_loop()
    try:
        await loop.run_in_executor(None, connect, sock)
    except BaseException:
        sock.close()
        raise
    transport = SocketTransport(sock, loop)
    transport.how = how
    return transport


async def connect_rfcomm(address: str, channel: Optional[int] = None, timeout: float = 10.0,
                         service_uuid: str = SPP_UUID) -> SocketTransport:
    """連到經典藍牙位址（A2DP MAC，形如 "AA:BB:CC:DD:EE:FF"）的 RFCOMM。

    channel 為 None（預設）：照官方 App 的順序，先用 UUID 查詢，失敗再直連 channel 1。
    channel 給定：只直連該 channel，不做 UUID 查詢。
    """
    if not hasattr(socket, "AF_BLUETOOTH") or not hasattr(socket, "BTPROTO_RFCOMM"):
        raise RuntimeError("這個 Python 沒有 RFCOMM socket 支援（需要 Windows 上 Python 3.9 以上）")

    errors: List[str] = []
    if channel is None:
        try:
            return await _open(address, lambda s: _connect_by_uuid(s, address, service_uuid, timeout),
                               f"UUID 查詢 {service_uuid}")
        except (OSError, RuntimeError) as e:
            errors.append(f"UUID 查詢：{e}")
        channel = DEFAULT_CHANNEL
    try:
        return await _open(address, lambda s: _connect_by_channel(s, address, channel, timeout),
                           f"直連 channel {channel}")
    except OSError as e:
        errors.append(f"channel {channel}：{e}")
    raise ConnectionError(
        f"RFCOMM 連線失敗（{address}）。嘗試結果：" + "；".join(errors)
        + "。確認耳機已與這台電腦配對並連上 A2DP，或用 --channel 指定其他 channel 試試")
