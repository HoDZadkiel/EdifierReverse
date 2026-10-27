"""傳輸層抽象。實機用 ble.BleakTransport，離線測試用 sim.SimulatedDevice。"""
from abc import ABC, abstractmethod
from typing import Callable, Optional


class Transport(ABC):
    """on_data 由傳輸層在收到通知時呼叫，參數為原始位元組（可能是分片）。
    on_close 在對端主動斷線時呼叫（自己 close() 不會觸發）；不是每種傳輸都支援。"""

    on_data: Optional[Callable[[bytes], None]] = None
    on_close: Optional[Callable[[], None]] = None

    @abstractmethod
    async def write(self, data: bytes) -> None:
        ...

    async def close(self) -> None:
        pass
