"""bleak 傳輸層與掃描。機種與 UUID 來自 catalog.json（官方產品表），辨識規則見 catalog.py。"""
from dataclasses import dataclass
from typing import Optional

from . import catalog
from .packet import PacketError, Session
from .transport import Transport

EDIFIER_COMPANY_ID = catalog.EDIFIER_COMPANY_ID


@dataclass(frozen=True)
class Profile:
    search_uuid: str
    service: str
    read: str
    write: str
    spp: str = ""

    @property
    def default_transport(self) -> str:
        """官方 App 的規則（BLEManager.switchConnectMethod）：產品表有 SPP UUID 就走 SPP，否則走 BLE。"""
        return "spp" if self.spp else "ble"


def _profile(m: catalog.Model) -> Profile:
    return Profile(m.search, m.service, catalog.READ_UUID, catalog.WRITE_UUID, spp=catalog.SPP_UUID if m.spp else "")


# 目錄內全部機種（官方產品表）。ES850NB 與 MP330 的值和先前手寫的一致（有測試守住）
PROFILES = {m.model: _profile(m) for m in catalog.MODELS}


def profile_for(model: str) -> Profile:
    """機種的連線設定；不在目錄的機種（確定是 Edifier 的新機種）用通用設定，先試 SPP。"""
    return PROFILES.get(model) or _profile(catalog.Model(catalog.GENERIC_MODEL, "", "", spp=True))


def session_from_manufacturer_data(data: bytes) -> Session:
    """倒數第 2 byte = bleVersion，最後 1 byte = encryption（BLEManager.java:188-189）。"""
    if len(data) < 2:
        raise PacketError("Manufacturer Data 太短")
    return Session(version=data[-2], encryption=data[-1])


def a2dp_address_from_manufacturer_data(data: bytes) -> str:
    """前 6 個位元組是 A2DP（經典藍牙）MAC，官方 App 用它對應已連線的 A2DP 裝置（BLEManager.java:143-162）。"""
    if len(data) < 6:
        raise PacketError("Manufacturer Data 太短，沒有 A2DP 位址")
    return ":".join(f"{b:02X}" for b in data[:6])


@dataclass(frozen=True)
class Advert:
    ble_address: str
    rssi: int
    session: Session
    a2dp_address: str
    model: str = ""


def choose_advert(entries, profile: Profile, address: Optional[str] = None) -> Optional[Advert]:
    """從掃描結果挑出符合型號的廣播。entries 為 (ble_address, rssi, service_uuids, manufacturer_data)。
    service UUID 要含該型號的搜尋 UUID（官方 App 的掃描過濾條件）；有給 address 時還要位址相符。
    多台符合時取訊號最強的。"""
    best = None
    for ble_addr, rssi, services, mfr in entries:
        if address and ble_addr.lower() != address.lower():
            continue
        if profile.search_uuid not in [u.lower() for u in services]:
            continue
        data = mfr.get(EDIFIER_COMPANY_ID)
        if data is None or len(data) < 8:
            continue
        if best is None or rssi > best.rssi:
            best = Advert(ble_addr, rssi, session_from_manufacturer_data(data),
                          a2dp_address_from_manufacturer_data(data))
    return best


def identify_adverts(entries) -> list:
    """從掃描結果挑出所有 Edifier 裝置（任何機種，同機種多台也各一筆），依訊號強弱排序。
    entries 為 (ble_address, rssi, service_uuids, manufacturer_data)。辨識規則見 catalog.identify。"""
    found = []
    for ble_addr, rssi, services, mfr in entries:
        m = catalog.identify(services, mfr)
        data = mfr.get(EDIFIER_COMPANY_ID)
        if m is None or data is None or len(data) < 8:
            continue  # 沒有廠商資料就取不到位址與 Session，無法連線
        found.append(Advert(ble_addr, rssi, session_from_manufacturer_data(data),
                            a2dp_address_from_manufacturer_data(data), m.model))
    return sorted(found, key=lambda a: -a.rssi)


async def discover_all(seconds: float = 6.0) -> list:
    from bleak import BleakScanner

    found = await BleakScanner.discover(timeout=seconds, return_adv=True)
    return identify_adverts([(dev.address, adv.rssi, adv.service_uuids, adv.manufacturer_data)
                             for dev, adv in found.values()])


async def discover(profile: Profile, address: Optional[str] = None,
                   seconds: float = 8.0) -> Optional[Advert]:
    from bleak import BleakScanner

    found = await BleakScanner.discover(timeout=seconds, return_adv=True)
    return choose_advert(
        [(dev.address, adv.rssi, adv.service_uuids, adv.manufacturer_data) for dev, adv in found.values()],
        profile, address)


async def scan(seconds: float = 10.0, show_all: bool = False) -> None:
    """列出 Edifier 裝置的廣播（只讀，不連線）。"""
    from bleak import BleakScanner

    known = {p.search_uuid: name for name, p in PROFILES.items()}
    found = await BleakScanner.discover(timeout=seconds, return_adv=True)
    shown = 0
    for dev, adv in sorted(found.values(), key=lambda x: -x[1].rssi):
        mfr = adv.manufacturer_data.get(EDIFIER_COMPANY_ID)
        matches = [known[u.lower()] for u in adv.service_uuids if u.lower() in known]
        if not (show_all or mfr is not None or matches):
            continue
        shown += 1
        print(f"{dev.address}  rssi={adv.rssi}  name={adv.local_name or dev.name!r}")
        for cid, data in adv.manufacturer_data.items():
            print(f"    manufacturer 0x{cid:04X}: {data.hex(' ')}")
            if cid == EDIFIER_COMPANY_ID and len(data) >= 8:
                s = session_from_manufacturer_data(data)
                print(f"    -> A2DP 位址={a2dp_address_from_manufacturer_data(data)}  "
                      f"bleVersion={s.version}  encryption={s.encryption}")
        if adv.service_uuids:
            print(f"    services: {adv.service_uuids}")
        if matches:
            print(f"    matches: {matches}")
    print(f"\n{len(found)} 個裝置，顯示 {shown} 個")


class BleakTransport(Transport):
    def __init__(self, client, write_char, with_response: bool):
        self._client = client
        self._write_char = write_char
        self._with_response = with_response

    @classmethod
    async def connect(cls, address: str, profile: Profile) -> "BleakTransport":
        from bleak import BleakClient

        client = BleakClient(address)
        await client.connect()
        svc = client.services.get_service(profile.service)
        if svc is None:
            uuids = [s.uuid for s in client.services]
            await client.disconnect()
            raise RuntimeError(f"找不到 service {profile.service}；裝置提供的 service：{uuids}")
        read = svc.get_characteristic(profile.read)
        write = svc.get_characteristic(profile.write)
        if read is None or write is None:
            await client.disconnect()
            raise RuntimeError("找不到 read / write characteristic")
        # 與官方 App 一致：支援 write-without-response 就用它，否則用 write
        with_response = "write-without-response" not in write.properties
        self = cls(client, write, with_response)
        await client.start_notify(read, lambda _c, data: self.on_data and self.on_data(bytes(data)))
        return self

    async def write(self, data: bytes) -> None:
        await self._client.write_gatt_char(self._write_char, data, response=self._with_response)

    async def close(self) -> None:
        await self._client.disconnect()
