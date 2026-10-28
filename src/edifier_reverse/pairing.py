"""藍牙配對狀態與連線事件（Windows 的 WinRT，bleak 已帶進這些套件）。

- is_paired：這台電腦是否已配對該裝置（經典藍牙位址）。查不到時回傳 True：寧可多顯示，也不因 API 失敗把裝置藏起來。
- ConnectionWatcher：監看已配對裝置，連上電腦（耳機開機或進入範圍）時呼叫 on_connected，讓服務不必等退避計時。
  沒配對的裝置取不到 BluetoothDevice，就不監看，仍靠退避輪詢。與 C# 版（Service/ConnectionWatcher.cs）一致。
"""
from typing import Callable


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


class ConnectionWatcher:
    def __init__(self, on_connected: Callable[[], None]):
        """on_connected 會在 WinRT 的執行緒上被呼叫，呼叫端要自己切回需要的執行緒。"""
        self._on_connected = on_connected
        self._device = None
        self._token = None
        self._generation = 0

    async def watch(self, a2dp: str) -> None:
        """改為監看指定位址；空字串或取不到裝置時只停止監看。"""
        self._generation += 1
        gen = self._generation
        self.stop()
        if not a2dp:
            return
        try:
            from winrt.windows.devices.bluetooth import BluetoothConnectionStatus, BluetoothDevice

            dev = await BluetoothDevice.from_bluetooth_address_async(_address(a2dp))
            if dev is None:
                return
            if gen != self._generation:  # 等待期間又換了裝置
                dev.close()
                return

            def changed(sender, _args):
                if sender.connection_status == BluetoothConnectionStatus.CONNECTED:
                    self._on_connected()

            self._token = dev.add_connection_status_changed(changed)
            self._device = dev
            if dev.connection_status == BluetoothConnectionStatus.CONNECTED:  # 監看前就已連上
                self._on_connected()
        except Exception:
            pass  # 取不到裝置不影響功能：退避輪詢照常運作

    def stop(self) -> None:
        dev, token, self._device, self._token = self._device, self._token, None, None
        if dev is None:
            return
        try:
            dev.remove_connection_status_changed(token)
            dev.close()
        except Exception:
            pass
