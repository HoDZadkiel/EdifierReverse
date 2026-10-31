"""藍牙配對狀態與連線事件（Windows 的 WinRT，bleak 已帶進這些套件）。

- is_paired：這台電腦是否已配對該裝置（經典藍牙位址）。查不到時回傳 True：寧可多顯示，也不因 API 失敗把裝置藏起來。
- paired_edifier_devices：列出已配對、名稱以 EDIFIER 開頭的裝置。已用經典藍牙連著電腦的裝置不再發 BLE 廣播，
  掃描找不到，這份清單是備援來源（見 devices.DeviceDirectory.scan）。
- is_connected：目前是否已連著電腦（藍牙連線狀態），沒明確選過裝置時用來挑要連的那台。
- ConnectionWatcher：監看所有已知裝置，連上電腦（耳機開機或進入範圍）時呼叫 on_connected，讓服務不必等退避計時。
  沒配對的裝置取不到 BluetoothDevice，就不監看，仍靠退避輪詢。與 C# 版（Service/ConnectionWatcher.cs）一致。
"""
from typing import Callable, List, Tuple


def _address(a2dp: str) -> int:
    parts = a2dp.split(":")
    if len(parts) != 6 or any(len(p) != 2 for p in parts):
        raise ValueError(f"藍牙位址格式錯誤：{a2dp}")
    return int("".join(parts), 16)


async def is_paired(a2dp: str) -> bool:
    try:
        from winrt.windows.devices.bluetooth import BluetoothDevice

        dev = await BluetoothDevice.from_bluetooth_address_async(_address(a2dp))
        if dev is None:
            return False
        try:
            return bool(dev.device_information.pairing.is_paired)
        finally:
            dev.close()
    except Exception:
        return True


async def paired_edifier_devices() -> List[Tuple[str, str]]:
    """回傳 (經典藍牙位址, 名稱)。查不到就回傳空清單。"""
    try:
        from winrt.windows.devices.bluetooth import BluetoothDevice
        from winrt.windows.devices.enumeration import DeviceInformation

        selector = BluetoothDevice.get_device_selector_from_pairing_state(True)
        found = []
        for d in await DeviceInformation.find_all_async_aqs_filter(selector):
            if d.name.upper().startswith("EDIFIER ") and "-" in d.id:
                found.append((d.id.rsplit("-", 1)[1].upper(), d.name))
        return found
    except Exception:
        return []


async def is_connected(a2dp: str) -> bool:
    """這台電腦目前是否已用經典藍牙連著該裝置（開機並連上）。查不到回傳 False。"""
    try:
        from winrt.windows.devices.bluetooth import BluetoothConnectionStatus, BluetoothDevice

        dev = await BluetoothDevice.from_bluetooth_address_async(_address(a2dp))
        if dev is None:
            return False
        try:
            return dev.connection_status == BluetoothConnectionStatus.CONNECTED
        finally:
            dev.close()
    except Exception:
        return False


class ConnectionWatcher:
    def __init__(self, on_connected: Callable[[], None]):
        """on_connected 會在 WinRT 的執行緒上被呼叫，呼叫端要自己切回需要的執行緒。
        監看所有已知裝置（不只選用的那台）：沒明確選過裝置時，新連上的裝置（例如 MP330）才能被偵測到。"""
        self._on_connected = on_connected
        self._devices: list = []  # [(BluetoothDevice, token)]
        self._generation = 0

    async def watch(self, addresses) -> None:
        """改為監看指定的位址；空清單或取不到裝置時只停止監看。"""
        self._generation += 1
        gen = self._generation
        self.stop()
        for a2dp in dict.fromkeys(a.upper() for a in addresses if a):
            try:
                from winrt.windows.devices.bluetooth import BluetoothConnectionStatus, BluetoothDevice

                dev = await BluetoothDevice.from_bluetooth_address_async(_address(a2dp))
                if dev is None:
                    continue
                if gen != self._generation:  # 等待期間又換了清單
                    dev.close()
                    return

                def changed(sender, _args):
                    if sender.connection_status == BluetoothConnectionStatus.CONNECTED:
                        self._on_connected()

                self._devices.append((dev, dev.add_connection_status_changed(changed)))
                if dev.connection_status == BluetoothConnectionStatus.CONNECTED:  # 監看前就已連上
                    self._on_connected()
            except Exception:
                pass  # 取不到裝置不影響功能：退避輪詢照常運作

    def stop(self) -> None:
        old, self._devices = self._devices, []
        for dev, token in old:
            try:
                dev.remove_connection_status_changed(token)
                dev.close()
            except Exception:
                pass
