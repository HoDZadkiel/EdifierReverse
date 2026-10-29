"""螢幕顯示（OSD）：工作區下方置中的小提示條，顯示一下就自己消失。不搶焦點、滑鼠可穿透。"""
import ctypes
import tkinter as tk
from typing import Optional

from .flyout import DARK, Theme, style_window, work_area

SHOW_MS = 1800
BOTTOM_GAP = 72   # 與工作區底緣的距離（96 DPI 的像素）


class Osd:
    def __init__(self, root: tk.Tk, theme: Theme, font_face: str):
        self.root = root
        self.scale = max(1.0, root.winfo_fpixels("1i") / 96.0)
        self._job: Optional[str] = None
        self.win = tk.Toplevel(root)
        self.win.withdraw()
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.configure(bg=theme.border)
        self.label = tk.Label(self.win, font=(font_face, -round(16 * self.scale)), fg=theme.text, bg=theme.bg,
                              padx=round(24 * self.scale), pady=round(12 * self.scale))
        self.label.pack(padx=1, pady=1)
        self._dark = theme == DARK

    def show(self, text: str) -> None:
        self.label.configure(text=text)
        self.win.update_idletasks()
        w, h = self.win.winfo_reqwidth(), self.win.winfo_reqheight()
        left, _, right, bottom = work_area()
        self.win.geometry(f"+{(left + right - w) // 2}+{bottom - h - round(BOTTOM_GAP * self.scale)}")
        self.win.deiconify()
        self.win.lift()
        style_window(self.win, self._dark)
        self._no_focus()
        if self._job is not None:
            self.win.after_cancel(self._job)
        self._job = self.win.after(SHOW_MS, self.hide)

    def hide(self) -> None:
        self._job = None
        self.win.withdraw()

    def _no_focus(self) -> None:
        """不搶焦點（NOACTIVATE）、不出現在 Alt+Tab（TOOLWINDOW）、滑鼠穿透（TRANSPARENT）。失敗不影響顯示。"""
        try:
            u32 = ctypes.windll.user32
            hwnd = u32.GetParent(self.win.winfo_id()) or self.win.winfo_id()
            style = u32.GetWindowLongW(hwnd, -20)
            u32.SetWindowLongW(hwnd, -20, style | 0x08000000 | 0x80 | 0x20)
        except Exception:
            pass
