"""OSD 要顯示什麼、快捷鍵要做什麼：純邏輯，不碰視窗與系統 API（C# 版對應 Service/Alerts.cs，兩邊用同樣的測試向量）。"""
from typing import List, Optional, Tuple

from .i18n import tr
from .service import ANC_MODES, DeviceState

LOW_BATTERY = 20  # 電量 <= 這個值時告警一次；回到高於此值才會再次告警（避免在門檻上下抖動時一直跳）

# 全域快捷鍵（一律 Ctrl+Alt+按鍵）：名稱 -> 虛擬鍵碼（可多個，任一個按下都算；靜音同時接主鍵盤的 0 與數字鍵盤的 0）。名稱與托盤選單的項目 id 一致（anc 是輪替下一個降噪模式）
HOTKEYS = {"anc": (0x41,), "vol+": (0x26,), "vol-": (0x28,), "mute": (0x30, 0x60)}   # A、↑、↓、0／數字鍵盤 0
HOTKEY_LABELS = {"anc": "Ctrl+Alt+A", "vol+": "Ctrl+Alt+↑", "vol-": "Ctrl+Alt+↓", "mute": "Ctrl+Alt+0"}


class OsdTracker:
    """比對連續的裝置狀態，產生該顯示的 OSD 訊息：電量掉到門檻以下、降噪模式改變（含耳機按鍵或手機切換）。"""

    def __init__(self) -> None:
        self._low = False
        self._mode: Optional[Tuple[int, int]] = None

    def update(self, state: DeviceState) -> List[str]:
        if not state.connected:
            self._mode = None  # 重連後的第一筆是「現況」，不是「改變」
            return []          # 電量告警旗標刻意不重設：充飽前反覆斷線重連不會一直跳
        msgs: List[str] = []
        if state.battery is not None:
            if state.battery > LOW_BATTERY:
                self._low = False
            elif not self._low:
                self._low = True
                msgs.append(tr("o.low_battery", b=state.battery))
        if state.anc is not None:
            cur = (state.anc.group, state.anc.mode)
            if self._mode is not None and cur != self._mode:
                msgs.append(tr("t.anc", mode=state.anc_label))
            self._mode = cur
        return msgs


def next_anc_mode(state: DeviceState) -> Optional[int]:
    """快捷鍵輪替用：目前模式的下一個（最後一個接回第一個）；目前模式不在表內就從第一個開始。
    沒連線、沒有 ANC 資料或 group 未驗證（不開放設定）時回傳 None。"""
    if not state.connected or not state.anc_supported:
        return None
    modes = ANC_MODES[state.anc.group]
    if state.anc.mode not in modes:
        return modes[0]
    return modes[(modes.index(state.anc.mode) + 1) % len(modes)]
