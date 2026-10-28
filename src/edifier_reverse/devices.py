"""裝置目錄：已知裝置、選用的裝置、掃描，以及連到選用裝置的連線器。

多台裝置時，使用者從選單選一台；選擇存在設定檔，下次啟動沿用。位址與 Session 來自 BLE 廣播
（前 6 bytes 是經典藍牙位址，倒數兩個 byte 是版本與加密），掃描要數秒，所以掃過的裝置都記住。
一次只連一台（耳機的 SPP 一次只服務一個控制端）。
"""
from typing import Awaitable, Callable, List, Optional, Tuple

from . import settings as S
from .i18n import tr
from .packet import Session
from .service import Connector
from .transport import Transport


class DeviceDirectory:
    def __init__(self, model: str = "ES850NB", settings: Optional[S.Settings] = None,
                 save: Optional[Callable[[S.Settings], None]] = None,
                 scanner: Optional[Callable[[float], "object"]] = None,
                 is_paired: Optional[Callable[[str], Awaitable[bool]]] = None):
        self.model = model
        self.settings = settings if settings is not None else S.load()
        self._save = save or S.save
        self._scanner = scanner  # 測試可注入；預設用 bleak
        self._is_paired = is_paired  # 測試可注入；預設問 Windows 這台電腦有沒有配對

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
        await self.prune_unpaired()
        seen = [d for d in (self.settings.device(a.a2dp_address) for a in adverts) if d is not None]
        if seen:
            self._save(self.settings)
        return seen

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
            dev = self.current()
            if dev is None:
                await self.scan(scan_seconds)
                dev = self.current()
                if dev is None:
                    raise ConnectionError(tr("e.not_found", model=self.model))
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
