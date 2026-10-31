"""裝置目錄：已知裝置、選用的裝置、掃描，以及連到選用裝置的連線器。

多台裝置時，使用者從選單選一台；選擇存在設定檔，下次啟動沿用。位址與 Session 來自 BLE 廣播
（前 6 bytes 是經典藍牙位址，倒數兩個 byte 是版本與加密），掃描要數秒，所以掃過的裝置都記住。
一次只連一台（耳機的 SPP 一次只服務一個控制端）。
"""
from typing import Awaitable, Callable, List, Optional, Tuple

from . import catalog
from . import settings as S
from .i18n import tr
from .packet import Session
from .service import Connector
from .transport import Transport


# 沒有廣播就取不到 bleVersion／encryption，退回實機驗證過的值：ES850NB 是 (2, 16)，MP330 是 (2, 0)。
# 其他機種沒驗證過，先用 (2, 0)。
FALLBACK_SESSION = {"ES850NB": Session(2, 16), "MP330": Session(2, 0)}


def fallback_ble_address(a2dp: str) -> str:
    """從經典藍牙位址推導 BLE 位址：第三個 byte 的低 4 bit 清零（實機：ES850NB、MP330 皆為 B3 → B0）。
    只是推導；連線時會驗證 GATT service，錯了就連不上。"""
    parts = a2dp.upper().split(":")
    parts[2] = f"{int(parts[2], 16) & 0xF0:02X}"
    return ":".join(parts)


class DeviceDirectory:
    def __init__(self, model: str = "ES850NB", settings: Optional[S.Settings] = None,
                 save: Optional[Callable[[S.Settings], None]] = None,
                 scanner: Optional[Callable[[float], "object"]] = None,
                 is_paired: Optional[Callable[[str], Awaitable[bool]]] = None,
                 paired_lister: Optional[Callable[[], Awaitable[List[Tuple[str, str]]]]] = None,
                 is_connected: Optional[Callable[[str], Awaitable[bool]]] = None):
        self.model = model
        self.settings = settings if settings is not None else S.load()
        self._save = save or S.save
        self._scanner = scanner  # 測試可注入；預設用 bleak
        self._is_paired = is_paired  # 測試可注入；預設問 Windows 這台電腦有沒有配對
        self._paired_lister = paired_lister  # 測試可注入；預設列出 Windows 已配對的 Edifier 裝置
        self._is_connected = is_connected  # 測試可注入；預設問 Windows 目前有沒有連著

    # ---- 查詢 ----
    @property
    def devices(self) -> List[S.KnownDevice]:
        return list(self.settings.devices)

    def current(self) -> Optional[S.KnownDevice]:
        return self.settings.current_device()

    @staticmethod
    def fallback_name(dev: Optional[S.KnownDevice]) -> str:
        """還沒連上、讀不到裝置名稱時顯示的名稱：連過的用裝置名稱，否則用機種名稱，不知道機種就用 Edifier。"""
        return "Edifier" if dev is None else dev.name or dev.model

    def label(self, dev: S.KnownDevice) -> str:
        """選單上的名稱：連過的有裝置名稱，沒連過的顯示「裝置 + 後四碼」；一律附後四碼以區分同型號多台。"""
        base = dev.name[8:] if dev.name.upper().startswith("EDIFIER ") else dev.name
        if not base:  # 沒連過：用辨識出的機種名稱；不在目錄的新機種才用通用名稱
            base = dev.model if dev.model != "Edifier" else tr("d.unnamed", tail="").strip()
        return f"{base} · {dev.tail}"  # 去掉品牌前綴，選單才放得下

    # ---- 變更 ----
    def select(self, a2dp: str) -> bool:
        if self.settings.device(a2dp) is None:
            return False
        self.settings.selected = self.settings.device(a2dp).a2dp
        self._save(self.settings)
        return True

    def remember_name(self, a2dp: str, name: str) -> None:
        dev = self.settings.device(a2dp)
        if dev is not None and name and dev.name != name:
            dev.name = name
            self._save(self.settings)

    async def scan(self, seconds: float = 6.0) -> List[S.KnownDevice]:
        """掃描廣播，把找到的裝置併入已知清單並存檔；回傳這次掃到的裝置。"""
        if self._scanner is not None:
            adverts = await self._scanner(seconds)
        else:
            from .ble import discover_all

            adverts = await discover_all(seconds)
        for a in adverts:
            self.settings.remember(S.KnownDevice(a.a2dp_address, a.ble_address, a.session.version, a.session.encryption,
                                                 model=getattr(a, "model", "") or self.model))
        fallback = await self._remember_paired_without_advert({a.a2dp_address.upper() for a in adverts})
        await self.prune_unpaired()
        seen = [d for d in (self.settings.device(a.a2dp_address) for a in adverts) if d is not None]
        seen += [d for d in fallback if d in self.settings.devices]
        if seen:
            self._save(self.settings)
        return seen

    async def _remember_paired_without_advert(self, advertised: set) -> List[S.KnownDevice]:
        """已用經典藍牙連著電腦的裝置不再廣播，掃描看不到。改用 Windows 的已配對清單補上，
        機種由名稱對應目錄；BLE 位址與 Session 用推導／實機驗證的預設值。"""
        if self._paired_lister is None:
            from .pairing import paired_edifier_devices

            self._paired_lister = paired_edifier_devices
        added = []
        for a2dp, name in await self._paired_lister():
            if a2dp.upper() in advertised or self.settings.device(a2dp) is not None:
                continue
            model = name[8:] if name.upper().startswith("EDIFIER ") else name
            model = model if model in catalog.BY_NAME else catalog.GENERIC_MODEL
            session = FALLBACK_SESSION.get(model, Session(2, 0))
            dev = S.KnownDevice(a2dp.upper(), fallback_ble_address(a2dp), session.version, session.encryption,
                                name=name, model=model)
            self.settings.remember(dev)
            added.append(dev)
        return added

    async def add_paired(self) -> bool:
        """把 Windows 已配對、但清單裡還沒有的 Edifier 裝置加進來（不靠廣播）。啟動時與每次連線前都呼叫，新配對的裝置才找得到。"""
        added = await self._remember_paired_without_advert(set())
        if added:
            self._save(self.settings)
        return bool(added)

    async def candidates(self) -> List[S.KnownDevice]:
        """要嘗試連線的裝置。使用者明確選過就只連那一台；沒選過時，以藍牙連線狀態偵測：
        有已連著電腦的裝置就只連它們（例如 MP330 連上後自動切過去），都沒連著才連第一台。"""
        chosen = self.settings.device(self.settings.selected) if self.settings.selected else None
        if chosen is not None:
            return [chosen]
        if self._is_connected is None:
            from .pairing import is_connected

            self._is_connected = is_connected
        connected = [d for d in self.settings.devices if await self._is_connected(d.a2dp)]
        if connected:
            return connected
        first = self.current()
        return [first] if first is not None else []

    async def prune_unpaired(self) -> None:
        """把這台電腦沒有配對的裝置從清單移除（廣播會掃到附近所有 Edifier 裝置，沒配對的連不上）。
        選用的被移除就清掉選擇。"""
        if self._is_paired is None:
            from .pairing import is_paired

            self._is_paired = is_paired
        changed = False
        for dev in list(self.settings.devices):
            if await self._is_paired(dev.a2dp):
                continue
            self.settings.devices.remove(dev)
            if self.settings.selected.upper() == dev.a2dp.upper():
                self.settings.selected = ""
            changed = True
        if changed:
            self._save(self.settings)

    # ---- 連線 ----
    def connector(self, scan_seconds: float = 8.0) -> Connector:
        """連到目前選用的裝置（沒選就用第一台；一台都沒有就先掃描）。SPP 優先，失敗退回 BLE。"""
        from .ble import BleakTransport, profile_for
        from .spp import connect_rfcomm

        async def connect() -> Tuple[Transport, Session]:
            await self.add_paired()
            if self.current() is None:
                await self.scan(scan_seconds)
            candidates = await self.candidates()
            if not candidates:
                raise ConnectionError(tr("e.not_found", model=self.model))
            last: Optional[Exception] = None
            for dev in candidates:
                try:
                    return await connect_one(dev)
                except ConnectionError as e:
                    last = e
            raise last

        async def connect_one(dev: S.KnownDevice) -> Tuple[Transport, Session]:
            session = Session(dev.version, dev.encryption)
            profile = profile_for(dev.model)  # 官方規則：有 SPP UUID 的機種走 SPP，否則走 BLE GATT
            errors = []
            if profile.spp:
                try:
                    return await connect_rfcomm(dev.a2dp), session
                except OSError as e:
                    errors.append(f"SPP：{e}")
            if dev.ble:
                try:
                    return await BleakTransport.connect(dev.ble, profile), session
                except Exception as e:
                    errors.append(f"BLE：{e}")
            raise ConnectionError("；".join(errors))

        return connect


def make_connector(model: str = "ES850NB") -> Connector:
    """命令列用：依設定檔的選用裝置連線。"""
    return DeviceDirectory(model).connector()
