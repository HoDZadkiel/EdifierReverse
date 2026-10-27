"""請求／回應客戶端。回應以指令碼比對（官方 App 同樣以 cmd 移除待回應佇列）。

不做重送：官方 App 有 ResendLooper，這裡等實機行為明確後再決定。
"""
import asyncio
from typing import Callable, Dict, Optional

from .packet import Packet, Session
from .reassembly import Reassembler
from .requests import Request
from .transport import Transport


class ResponseTimeout(TimeoutError):
    pass


class Client:
    def __init__(self, transport: Transport, session: Session, timeout: float = 3.0,
                 on_event: Optional[Callable[[Packet], None]] = None):
        self.transport = transport
        self.session = session
        self.timeout = timeout
        self.on_event = on_event  # 沒有對應請求的主動回報（例如裝置端切換 ANC）
        self._reassembler = Reassembler(session)
        self._waiting: Dict[int, asyncio.Future] = {}
        transport.on_data = self._on_data

    def _on_data(self, chunk: bytes) -> None:
        for pkt in self._reassembler.feed(chunk):
            fut = self._waiting.pop(pkt.cmd, None)
            if fut is not None and not fut.done():
                fut.set_result(pkt)
            elif self.on_event is not None:
                self.on_event(pkt)

    async def request(self, req: Request, timeout: Optional[float] = None) -> Packet:
        if req.cmd in self._waiting:
            raise RuntimeError(f"指令 0x{req.cmd:02X} 已有請求在等待回應")
        fut = asyncio.get_running_loop().create_future()
        self._waiting[req.cmd] = fut
        try:
            await self.transport.write(req.to_bytes(self.session))
            return await asyncio.wait_for(fut, timeout or self.timeout)
        except asyncio.TimeoutError:
            raise ResponseTimeout(f"指令 0x{req.cmd:02X} 逾時") from None
        finally:
            self._waiting.pop(req.cmd, None)
