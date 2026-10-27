"""離線模擬裝置：依 docs/ 記載的格式回應查詢。

注意：它只證明「函式庫與我們對格式的理解自洽」，不證明真實裝置也是這樣回應。
feed() 用串流語意處理輸入（多個請求可能黏在一起、也可能分次到達），BLE 與 SPP 共用同一套邏輯。
"""
import asyncio
import time
from typing import Dict, List, Optional

from . import commands as C
from .packet import HEADER_SEND, PacketError, Session, build, expected_length, parse
from .transport import Transport


class SimulatedDevice(Transport):
    """chunk_size 不為 None 時，把回應切成小片送出，用來測試重組。

    min_set_interval > 0 時模擬實機行為：兩次 ANC 設定間隔不足就靜默忽略（無回應、狀態不變）。
    push_anc 為 True 時，ANC 設定成功後多推送一則 0xCC，對應實機的主動通知。
    """

    def __init__(self, session: Session = Session(2, 0), chunk_size: Optional[int] = None,
                 min_set_interval: float = 0.0, push_anc: bool = False):
        self.session = session
        self.chunk_size = chunk_size
        self.min_set_interval = min_set_interval
        self.push_anc = push_anc
        self.ignored_sets = 0
        self.connected = True
        self._last_set = float("-inf")
        self.written = []  # 收到的原始寫入，供測試檢查
        self._rx = bytearray()
        self.state: Dict[int, bytes] = {
            C.DEVICE_FUNCTION_QUERY: bytes([0x03, 1, 1, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0x20]),
            C.BATTERY_QUERY: bytes([80]),
            C.VERSION_QUERY: bytes([1, 0, 2]),
            C.MAC_QUERY: bytes.fromhex("A1B2C3040506"),
            C.NAME_QUERY: "SIM-DEVICE".encode(),
            C.ANC_QUERY: bytes([18, 4]),
            # 以下三個數值是模擬用的任意值，不代表真實裝置的編碼設定
            # 刻意用兩位元組，讓 LDAC（取 P[0]）與 LHDC（長度 2 取 P[1]）的解析規則會得到不同結果，
            # 測試才分得出兩個解析器是否被接錯
            C.LDAC_STATE_QUERY: bytes([1, 9]),
            C.LHDC_STATE_QUERY: bytes([2, 5]),
            C.QUERY_HD_AUDIO_CODEC: bytes([3, 1, 0]),
        }

    def feed(self, data: bytes) -> List[bytes]:
        """餵入收到的位元組，回傳要送回去的資料片（已依 chunk_size 切好）。"""
        self.written.append(bytes(data))
        self._rx += data
        out: List[bytes] = []
        while self._rx:
            if self._rx[0] != HEADER_SEND:
                self._rx.clear()  # 開頭不是 App 的 header，永遠湊不成封包
                break
            total = expected_length(self.session, self._rx)
            if total is None or len(self._rx) < total:
                break
            raw = bytes(self._rx[:total])
            del self._rx[:total]
            try:
                req = parse(self.session, raw, headers=(HEADER_SEND,))
            except PacketError:
                continue  # 真實裝置對壞封包大概也不回，這裡一樣靜默
            reply = self._handle(req.cmd, req.payload)
            if reply is None:
                continue
            frames = [(req.cmd, reply)]
            if req.cmd == C.ANC_SET and self.push_anc:
                frames.append((C.ANC_QUERY, self.state[C.ANC_QUERY]))
            for cmd, body in frames:
                wire = build(self.session, cmd, body, header=0xBB)
                step = self.chunk_size or len(wire)
                out += [wire[i:i + step] for i in range(0, len(wire), step)]
        return out

    def disconnect(self) -> None:
        """模擬對端斷線：之後的寫入會失敗，並通知 on_close。"""
        self.connected = False
        if self.on_close is not None:
            self.on_close()

    async def write(self, data: bytes) -> None:
        if not self.connected:
            raise ConnectionError("模擬裝置已斷線")
        for chunk in self.feed(data):
            await asyncio.sleep(0)
            if self.on_data is not None:
                self.on_data(chunk)

    def _handle(self, cmd: int, payload: bytes) -> Optional[bytes]:
        if cmd == C.ANC_SET:
            now = time.monotonic()
            if now - self._last_set < self.min_set_interval:
                self.ignored_sets += 1
                return None
            self._last_set = now
            self.state[C.ANC_QUERY] = payload
            return payload
        return self.state.get(cmd)


def serve_socket(device: SimulatedDevice, sock) -> None:
    """在已連線的 socket 另一端扮演裝置（阻塞式，放在執行緒裡跑）。對端關閉時結束。"""
    while True:
        try:
            data = sock.recv(1024)
        except OSError:
            return
        if not data:
            return
        try:
            for chunk in device.feed(data):
                sock.sendall(chunk)
        except OSError:
            return
