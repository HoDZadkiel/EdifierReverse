"""BLE 通知的封包重組。行為對照官方 App 的 ConnectManager.dataCheck / subPackageOfNew。

- 開頭是 0xBB / 0xCC 的通知視為新封包開頭，丟棄舊的殘缺資料；否則附加到緩衝區。
- 一筆通知可能夾帶多個封包，依長度欄位依序切開。
- 已知限制（官方 App 同樣如此）：後續分片若恰好以 0xBB / 0xCC 開頭，會被誤認為新封包。
- 與官方的差異：CRC 錯誤的封包直接丟棄並繼續處理後面的資料，官方則會保留快取等下一個 header。

靜態分析結果，尚未實機驗證。
"""
from typing import List

from .packet import HEADERS_RECV, Packet, PacketError, Session, expected_length, parse


class Reassembler:
    def __init__(self, session: Session):
        self.session = session
        self._buf = bytearray()
        self.dropped = 0  # 因 CRC / appCode 等錯誤被丟棄的封包數，除錯用

    def feed(self, chunk: bytes) -> List[Packet]:
        if chunk and chunk[0] in HEADERS_RECV:
            self._buf = bytearray(chunk)
        else:
            self._buf += chunk
        out: List[Packet] = []
        while self._buf:
            if self._buf[0] not in HEADERS_RECV:
                self._buf.clear()  # 開頭不是 header，永遠湊不成封包
                break
            total = expected_length(self.session, self._buf)
            if total is None or len(self._buf) < total:
                break  # 還在等後續分片
            raw = bytes(self._buf[:total])
            del self._buf[:total]
            try:
                out.append(parse(self.session, raw))
            except PacketError:
                self.dropped += 1
        return out
