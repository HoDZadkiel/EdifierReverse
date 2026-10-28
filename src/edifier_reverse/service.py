"""裝置服務：連線、狀態、ANC 設定的節流佇列、斷線重連。不含任何 GUI，托盤與 CLI 共用。

規則都來自 ES850NB 實機驗證（見 docs/findings.md 第 0 節）：
- 兩次 ANC 設定間隔 <= 1.0 秒時，第二次被裝置靜默忽略，所以設定指令一律經節流（預設 2.0 秒）。
- 設定成功後裝置會主動推送 0xCC，狀態以該推送與查詢為準。
- 工具只送查詢與 0xC1（ANC）；不送 EQ、OTA 等其他指令。
"""
import asyncio
import time
from dataclasses import dataclass, replace
from typing import Awaitable, Callable, Dict, Optional, Tuple

from . import commands as C
from .client import Client, ResponseTimeout
from .i18n import tr
from .packet import Packet, PacketError, Session
from .requests import query, set_anc
from .responses import AncState, parse_anc, parse_battery, parse_name, parse_version
from .transport import Transport

# 只有實機驗證過的 group 才開放設定；其餘 group 只顯示狀態
# 模式名稱在 strings.json（anc.n.<mode>），依介面語言顯示
ANC_MODES: Dict[int, Tuple[int, ...]] = {
    16: (1, 2, 3, 4, 5),
}
AMBIENT_MODE = {16: 4}   # group -> 可調增益環境音的 mode（才有 DB）
DB_RANGE = (1, 12)       # 實機驗證過 1、3、9、12

Connector = Callable[[], Awaitable[Tuple[Transport, Session]]]


class ServiceError(Exception):
    """訊息以字串表的鍵產生（依建立當下的介面語言），介面直接顯示 str(e)。"""

    def __init__(self, key: str, **kw):
        self.key, self.kw = key, kw
        super().__init__(tr(key, **kw))


@dataclass(frozen=True)
class DeviceState:
    connected: bool = False
    name: str = ""
    version: str = ""
    battery: Optional[int] = None
    anc: Optional[AncState] = None
    error: str = ""
    cooling: bool = False  # ANC 設定送出後的冷卻期：介面應暫停切換（裝置在間隔內會靜默忽略設定）

    @property
    def anc_supported(self) -> bool:
        return self.anc is not None and self.anc.group in ANC_MODES

    @property
    def anc_label(self) -> str:
        if self.anc is None:
            return tr("anc.unknown")
        modes = ANC_MODES.get(self.anc.group)
        if modes is None:
            return tr("anc.unverified", g=self.anc.group, m=self.anc.mode)
        return tr(f"anc.n.{self.anc.mode}") if self.anc.mode in modes else tr("anc.mode_n", m=self.anc.mode)


class DeviceService:
    def __init__(self, connector: Connector, *, min_set_interval: float = 2.0,
                 poll_interval: float = 60.0, set_timeout: float = 3.0,
                 query_timeout: float = 3.0, backoff=(1.0, 2.0, 5.0, 10.0, 30.0),
                 on_change: Optional[Callable[[DeviceState], None]] = None):
        self._connector = connector
        self.min_set_interval = min_set_interval
        self.poll_interval = poll_interval
        self.set_timeout = set_timeout
        self.query_timeout = query_timeout
        self._backoff = backoff
        self._on_change = on_change
        self.state = DeviceState()
        self._client: Optional[Client] = None
        self._set_lock = asyncio.Lock()
        self._last_set = float("-inf")
        self._pending_sets = 0
        self._cooldown_task: Optional[asyncio.Task] = None
        self._lost = asyncio.Event()
        self._wake = asyncio.Event()
        self._task: Optional[asyncio.Task] = None
        self._backing_off = False
        self._fast_retry = False

    # ---- 生命週期 ----
    def start(self) -> None:
        if self._task is None:
            self._task = asyncio.get_running_loop().create_task(self._run())

    async def stop(self) -> None:
        if self._cooldown_task is not None:
            self._cooldown_task.cancel()
            self._cooldown_task = None
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            self._task = None

    async def wait_connected(self, timeout: float = 30.0) -> DeviceState:
        deadline = time.monotonic() + timeout
        while not self.state.connected:
            if time.monotonic() >= deadline:
                raise ServiceError("e.wait_timeout", err=self.state.error or tr("e.not_connected"))
            await asyncio.sleep(0.05)
        return self.state

    def reconnect(self) -> None:
        """要求立刻重連（略過退避等待）。"""
        self._lost.set()
        self._wake.set()

    def notify_device_available(self) -> None:
        """藍牙事件通知「裝置已連上電腦」（要在事件迴圈的執行緒呼叫）：只在斷線等待重試時才提前重連，
        並讓下次失敗從最短退避重來（事件觸發時 SPP 可能還沒就緒）。已連線或正在連線時忽略，不會中斷健康的連線。"""
        if not self._backing_off:
            return
        self._fast_retry = True
        self._wake.set()

    # ---- 狀態 ----
    def _update(self, **changes) -> None:
        self.state = replace(self.state, **changes)
        if self._on_change is not None:
            self._on_change(self.state)

    def _on_event(self, pkt: Packet) -> None:
        if pkt.cmd == C.ANC_QUERY:  # 裝置主動推送（含別處切換，如耳機按鍵）
            try:
                self._update(anc=parse_anc(pkt.payload))
            except Exception:
                pass

    async def _ask(self, cmd: int) -> bytes:
        assert self._client is not None
        return (await self._client.request(query(cmd), timeout=self.query_timeout)).payload

    async def _optional(self, cmd: int, parse):
        """查詢並解析；機種不支援（沒有回應或回應格式不符）時回傳 None，不當成連線失敗。
        目錄內的其他機種不一定有 ANC 或同樣的電量指令，只讀查詢逐項容錯才能連上它們。"""
        try:
            return parse(await self._ask(cmd))
        except (ResponseTimeout, PacketError):
            return None

    async def _refresh_static(self) -> None:
        name = await self._optional(C.NAME_QUERY, parse_name)
        version = await self._optional(C.VERSION_QUERY, parse_version)
        if name is None and version is None:
            raise ResponseTimeout("裝置沒有回應名稱與版本查詢")
        self._update(name=name or "", version=version or "")

    async def _refresh(self) -> None:
        battery = await self._optional(C.BATTERY_QUERY, parse_battery)
        anc = await self._optional(C.ANC_QUERY, parse_anc)
        if battery is None and anc is None:
            raise ResponseTimeout("裝置沒有回應電量與 ANC 查詢")  # 兩個都沒回應才視為連線已壞
        self._update(battery=battery, anc=anc)

    # ---- 主迴圈 ----
    async def _run(self) -> None:
        failures = 0
        while True:
            transport = None
            try:
                transport, session = await self._connector()
                self._lost.clear()
                transport.on_close = self._lost.set
                self._client = Client(transport, session, on_event=self._on_event)
                await self._refresh_static()
                await self._refresh()
                self._update(connected=True, error="")
                failures = 0
                while True:
                    try:
                        await asyncio.wait_for(self._lost.wait(), self.poll_interval)
                        raise ConnectionError("連線中斷")
                    except asyncio.TimeoutError:
                        pass
                    await self._refresh()
            except asyncio.CancelledError:
                raise
            except Exception as e:
                self._update(connected=False, error=f"{type(e).__name__}: {e}")
            finally:
                self._client = None
                if transport is not None:
                    try:
                        await transport.close()
                    except Exception:
                        pass
            if self._fast_retry:
                self._fast_retry = False
                failures = 0
            delay = self._backoff[min(failures, len(self._backoff) - 1)]
            failures += 1
            self._wake.clear()
            self._backing_off = True
            try:
                await asyncio.wait_for(self._wake.wait(), delay)
            except asyncio.TimeoutError:
                pass
            finally:
                self._backing_off = False

    # ---- 設定 ----
    async def set_anc(self, mode: int, db: Optional[int] = None) -> AncState:
        """切換 ANC 模式。所有設定經同一把鎖與節流；逾時（被忽略或封包遺失）重送一次。"""
        anc = self.state.anc
        if not self.state.connected or self._client is None:
            raise ServiceError("e.not_connected")
        if anc is None or anc.group not in ANC_MODES:
            raise ServiceError("e.unverified_group", g=None if anc is None else anc.group)
        if mode not in ANC_MODES[anc.group]:
            raise ServiceError("e.no_mode", g=anc.group, m=mode)
        if db is not None:
            if mode != AMBIENT_MODE.get(anc.group):
                raise ServiceError("e.ambient_only")
            if not DB_RANGE[0] <= db <= DB_RANGE[1]:
                raise ServiceError("e.strength_range", lo=DB_RANGE[0], hi=DB_RANGE[1])
        req = set_anc(mode, anc.group, db)
        # 冷卻期：從第一個設定進來，到最後一個設定完成後再過 min_set_interval 為止（介面據此停用切換）
        self._pending_sets += 1
        if self._cooldown_task is not None:
            self._cooldown_task.cancel()
            self._cooldown_task = None
        if not self.state.cooling:
            self._update(cooling=True)
        try:
            return await self._send_anc(req)
        finally:
            self._pending_sets -= 1
            if self._pending_sets == 0:
                delay = max(0.0, self._last_set + self.min_set_interval - time.monotonic())
                self._cooldown_task = asyncio.get_running_loop().create_task(self._end_cooldown(delay))

    async def _end_cooldown(self, delay: float) -> None:
        await asyncio.sleep(delay)
        self._cooldown_task = None
        if self._pending_sets == 0:
            self._update(cooling=False)

    async def _send_anc(self, req) -> AncState:
        async with self._set_lock:
            for attempt in range(2):
                wait = self._last_set + self.min_set_interval - time.monotonic()
                if wait > 0:
                    await asyncio.sleep(wait)
                client = self._client
                if client is None:
                    raise ServiceError("e.conn_lost_plain")
                try:
                    resp = await client.request(req, timeout=self.set_timeout)
                except ResponseTimeout:
                    self._last_set = time.monotonic()
                    if attempt == 1:
                        raise ServiceError("e.no_response") from None
                    continue
                except (ConnectionError, OSError) as e:
                    self.reconnect()
                    raise ServiceError("e.conn_lost", err=e) from None
                self._last_set = time.monotonic()
                new = parse_anc(resp.payload)
                self._update(anc=new)
                return new
        raise ServiceError("e.set_failed")  # 不會到這裡，保險
