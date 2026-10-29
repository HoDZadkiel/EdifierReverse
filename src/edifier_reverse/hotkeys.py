"""全域快捷鍵（Win32 RegisterHotKey）。註冊與訊息迴圈都在同一條背景執行緒（熱鍵綁定在註冊它的執行緒）。
按下時呼叫 on_hotkey(名稱)，由呼叫端自己轉回主執行緒。"""
import ctypes
import threading
from ctypes import wintypes
from typing import Callable, Dict, List, Tuple

MOD_ALT, MOD_CONTROL, MOD_NOREPEAT = 0x1, 0x2, 0x4000
WM_HOTKEY, WM_QUIT = 0x0312, 0x0012


class HotkeyListener:
    def __init__(self, keys: Dict[str, Tuple[int, ...]], on_hotkey: Callable[[str], None]):
        self._keys, self._cb = keys, on_hotkey
        self._ready = threading.Event()
        self._tid = 0
        self.failed: List[str] = []   # 註冊失敗（多半是被別的程式佔用）的名稱
        self._thread = threading.Thread(target=self._main, name="hotkeys", daemon=True)

    def start(self) -> List[str]:
        """開始監聽，回傳註冊失敗的名稱清單。"""
        self._thread.start()
        self._ready.wait(3)
        return self.failed

    def _main(self) -> None:
        u32, k32 = ctypes.windll.user32, ctypes.windll.kernel32
        self._tid = k32.GetCurrentThreadId()
        ids = {}
        i = 0
        for name, vks in self._keys.items():
            ok = False
            for vk in vks:
                i += 1
                if u32.RegisterHotKey(None, i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk):
                    ids[i] = name
                    ok = True
            if not ok:  # 一個鍵都註冊不了才算失敗
                self.failed.append(name)
        self._ready.set()
        msg = wintypes.MSG()
        while u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_HOTKEY and msg.wParam in ids:
                self._cb(ids[msg.wParam])
        for i in ids:
            u32.UnregisterHotKey(None, i)

    def stop(self) -> None:
        if self._tid:
            ctypes.windll.user32.PostThreadMessageW(self._tid, WM_QUIT, 0, 0)
