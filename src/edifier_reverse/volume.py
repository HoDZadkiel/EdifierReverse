"""耳機音量：走 Windows 音訊端點（pycaw），不是 Edifier 協定。

實機（ES850NB）對協定的音量查詢 0x66、0xC5 都沒有回應，D8 的音量位元也沒開，
所以音量由 Windows 負責。這個模組只調整「耳機 (EDIFIER ES850NB)」這個播放端點，
不動預設輸出裝置、不動其他裝置。pycaw 屬於選用相依（`uv sync --extra tray`）。
"""
import warnings
from typing import Callable, Optional

from .i18n import tr


class VolumeError(Exception):
    pass


def _open_endpoint(name_part: str):
    """找出名稱含 name_part 的作用中「播放」端點（id 以 {0.0.0 開頭；{0.0.1 是錄音／麥克風），
    回傳 IAudioEndpointVolume。排除 Hands-Free（通話用）端點。每次呼叫都在目前執行緒重新取得。"""
    try:
        import comtypes
        from pycaw.pycaw import AudioUtilities
    except ImportError as e:
        raise VolumeError(tr("e.no_pycaw")) from e
    try:
        comtypes.CoInitialize()
    except OSError:
        pass  # 此執行緒已初始化
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")  # pycaw 對讀不到的裝置屬性會大量警告
        for dev in AudioUtilities.GetAllDevices():
            name = dev.FriendlyName or ""
            if (dev.id.startswith("{0.0.0") and name_part in name and "Hands-Free" not in name
                    and "Active" in str(dev.state)):
                return dev.EndpointVolume
    raise VolumeError(tr("e.vol_endpoint", name=name_part))


class VolumeControl:
    """端點物件會快取（列舉所有音訊裝置很慢，拖曳音量時不能每次重找）；COM 是以執行緒為單位，
    所以一個實例只能在建立它的執行緒使用。操作失敗（例如耳機斷線後端點失效）會重新尋找並重試一次。"""

    def __init__(self, name_part: str = "EDIFIER",
                 opener: Optional[Callable[[str], object]] = None):
        self._name = name_part
        self._open = opener or _open_endpoint
        self._ep = None

    def set_target(self, name_part: str) -> None:
        """換成另一台裝置的播放端點（名稱含 name_part）。多台 Edifier 裝置時，依目前連線裝置的名稱挑對的那一個。"""
        if name_part and name_part != self._name:
            self._name = name_part
            self._ep = None

    def _call(self, fn):
        for attempt in (0, 1):
            if self._ep is None:
                self._ep = self._open(self._name)
            try:
                return fn(self._ep)
            except VolumeError:
                raise
            except Exception:
                self._ep = None  # 端點失效，重新尋找
                if attempt == 1:
                    raise VolumeError(tr("e.vol_access")) from None

    def get(self) -> int:
        """目前音量 0–100。"""
        return self._call(lambda ep: round(ep.GetMasterVolumeLevelScalar() * 100))

    def set(self, percent: int) -> int:
        """設定音量，超出 0–100 時夾在範圍內；回傳實際設定值。"""
        p = max(0, min(100, int(percent)))
        self._call(lambda ep: ep.SetMasterVolumeLevelScalar(p / 100.0, None))
        return p

    def step(self, delta: int) -> int:
        """相對調整 delta 個百分點（正負皆可），回傳新音量。"""
        def go(ep):
            p = max(0, min(100, round(ep.GetMasterVolumeLevelScalar() * 100) + delta))
            ep.SetMasterVolumeLevelScalar(p / 100.0, None)
            return p
        return self._call(go)

    def muted(self) -> bool:
        return bool(self._call(lambda ep: ep.GetMute()))

    def set_mute(self, mute: bool) -> None:
        self._call(lambda ep: ep.SetMute(1 if mute else 0, None))
