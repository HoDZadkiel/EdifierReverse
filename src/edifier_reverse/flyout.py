"""Windows 11 風格的彈出面板（Tkinter）：電量、降噪模式、環境音強度、音量拖曳條。

只負責畫面與互動；裝置與音量的實際動作由呼叫端透過回呼處理。
拖曳條、磚塊按鈕都用 Canvas 自繪，才能做到 Fluent 的圓角與配色。
"""
import ctypes
import sys
import tkinter as tk
import tkinter.font as tkfont
from dataclasses import dataclass
from typing import Callable, Dict, Optional, Tuple

from .i18n import pick_font, tr
from .logo import battery_color, make_logo
from .service import AMBIENT_MODE, ANC_MODES, DB_RANGE, DeviceState

GROUP = 16


@dataclass(frozen=True)
class Theme:
    bg: str
    surface: str
    hover: str
    border: str
    text: str
    sub: str
    accent: str
    accent_text: str
    track: str
    disabled: str
    error: str


DARK = Theme(bg="#202020", surface="#2D2D2D", hover="#383838", border="#404040", text="#FFFFFF",
             sub="#B0B0B0", accent="#60CDFF", accent_text="#000000", track="#6B6B6B",
             disabled="#5A5A5A", error="#FF99A4")
LIGHT = Theme(bg="#F3F3F3", surface="#FFFFFF", hover="#F0F0F0", border="#E0E0E0", text="#1B1B1B",
              sub="#5E5E5E", accent="#005FB8", accent_text="#FFFFFF", track="#8A8A8A",
              disabled="#B5B5B5", error="#C42B1C")


def style_window(win: tk.Misc, dark: bool) -> None:
    """Windows 11：圓角與系統深淺色。失敗（舊版 Windows）就維持直角，不影響功能。"""
    try:
        win.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(win.winfo_id()) or win.winfo_id()
        dwm = ctypes.windll.dwmapi.DwmSetWindowAttribute
        dwm(hwnd, 33, ctypes.byref(ctypes.c_int(2)), 4)                          # 圓角
        dwm(hwnd, 20, ctypes.byref(ctypes.c_int(1 if dark else 0)), 4)           # 深色模式
    except Exception:
        pass


def system_is_dark() -> bool:
    """讀 Windows 的「應用程式模式」；讀不到時當作深色。"""
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            return winreg.QueryValueEx(k, "AppsUseLightTheme")[0] == 0
    except Exception:
        return True


def work_area() -> Tuple[int, int, int, int]:
    """主螢幕工作區（扣掉工作列），回傳 (left, top, right, bottom)。"""
    class RECT(ctypes.Structure):
        _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("r", ctypes.c_long), ("b", ctypes.c_long)]

    r = RECT()
    ctypes.windll.user32.SystemParametersInfoW(0x30, 0, ctypes.byref(r), 0)  # SPI_GETWORKAREA
    return r.l, r.t, r.r, r.b


def flyout_position(area: Tuple[int, int, int, int], w: int, h: int, margin: int) -> Tuple[int, int]:
    """面板貼在工作區右下角，與邊緣保留 margin。"""
    left, top, right, bottom = area
    return max(left, right - w - margin), max(top, bottom - h - margin)


def value_from_x(x: float, left: float, right: float, lo: int, hi: int) -> int:
    """拖曳條：x 座標換成整數值，超出軌道時夾在範圍內。"""
    if right <= left:
        return lo
    frac = max(0.0, min(1.0, (x - left) / (right - left)))
    return int(round(lo + frac * (hi - lo)))


def x_from_value(v: float, left: float, right: float, lo: int, hi: int) -> float:
    if hi <= lo:
        return left
    return left + (max(lo, min(hi, v)) - lo) / (hi - lo) * (right - left)


def _rrect(c: tk.Canvas, x1, y1, x2, y2, r, **kw):
    r = min(r, (x2 - x1) / 2, (y2 - y1) / 2)
    pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2, x2 - r, y2,
           x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
    return c.create_polygon(pts, smooth=True, **kw)


class SliderBar(tk.Canvas):
    """Fluent 拖曳條：細軌道、強調色已填段、外白內色的圓形把手。"""

    def __init__(self, master, theme: Theme, S: Callable[[float], int], lo: int, hi: int, value: int,
                 on_change: Callable[[int, bool], None], width: int):
        super().__init__(master, width=width, height=S(28), bg=theme.bg, highlightthickness=0, bd=0)
        self.t, self.S, self.lo, self.hi, self.value = theme, S, lo, hi, value
        self.on_change, self._width = on_change, width
        self.enabled, self._drag = True, False
        self.bind("<ButtonPress-1>", self._press)
        self.bind("<B1-Motion>", self._move)
        self.bind("<ButtonRelease-1>", self._release)
        self.draw()

    def _span(self):
        pad = self.S(11)
        return pad, self._width - pad

    def draw(self) -> None:
        S, t = self.S, self.t
        self.delete("all")
        left, right = self._span()
        cy = S(14)
        x = x_from_value(self.value, left, right, self.lo, self.hi)
        _rrect(self, left, cy - S(2), right, cy + S(2), S(2), fill=t.track if self.enabled else t.disabled)
        if x > left:
            _rrect(self, left, cy - S(2), x, cy + S(2), S(2), fill=t.accent if self.enabled else t.disabled)
        r = S(10)
        self.create_oval(x - r, cy - r, x + r, cy + r, fill=t.surface, outline=t.border)
        ri = S(6) if not self._drag else S(5)
        self.create_oval(x - ri, cy - ri, x + ri, cy + ri, fill=t.accent if self.enabled else t.disabled, outline="")

    def set_value(self, v: int) -> None:
        if not self._drag and v != self.value:
            self.value = v
            self.draw()

    def set_enabled(self, on: bool) -> None:
        if on != self.enabled:
            self.enabled = on
            self.draw()

    def _apply(self, e, final: bool) -> None:
        left, right = self._span()
        v = value_from_x(e.x, left, right, self.lo, self.hi)
        changed = v != self.value
        self.value = v
        self.draw()
        if changed or final:
            self.on_change(v, final)

    def _press(self, e):
        if self.enabled:
            self._drag = True
            self._apply(e, False)

    def _move(self, e):
        if self._drag:
            self._apply(e, False)

    def _release(self, e):
        if self._drag:
            self._apply(e, True)
            self._drag = False
            self.draw()


class Tile(tk.Canvas):
    """降噪模式的磚塊按鈕。選中時填強調色。"""

    def __init__(self, master, theme: Theme, S, text: str, font, width: int, height: int,
                 on_click: Callable[[], None]):
        super().__init__(master, width=width, height=height, bg=theme.bg, highlightthickness=0, bd=0)
        self.t, self.S, self.text, self.font = theme, S, text, font
        self.w, self.h, self.on_click = width, height, on_click
        self.selected, self.enabled, self._hover = False, True, False
        self.bind("<Enter>", lambda e: self._set_hover(True))
        self.bind("<Leave>", lambda e: self._set_hover(False))
        self.bind("<ButtonRelease-1>", lambda e: self.enabled and self.on_click())
        self.draw()

    def _set_hover(self, h: bool) -> None:
        self._hover = h
        self.draw()

    def draw(self) -> None:
        t = self.t
        self.delete("all")
        if self.selected:  # 停用（例如冷卻期）時仍顯示目前模式，只是不能點
            fill, fg, outline = t.accent, t.accent_text, t.accent
        else:
            fill = t.hover if (self._hover and self.enabled) else t.surface
            fg = t.text if self.enabled else t.disabled
            outline = t.border
        _rrect(self, 1, 1, self.w - 1, self.h - 1, self.S(6), fill=fill, outline=outline)
        self.create_text(self.w / 2, self.h / 2, text=self.text, fill=fg, font=self.font)

    def configure_state(self, selected: bool, enabled: bool) -> None:
        if (selected, enabled) != (self.selected, self.enabled):
            self.selected, self.enabled = selected, enabled
            self.draw()


class IconButton(tk.Canvas):
    """喇叭圖示按鈕（靜音切換）。"""

    def __init__(self, master, theme: Theme, S, on_click: Callable[[], None]):
        size = S(32)
        super().__init__(master, width=size, height=size, bg=theme.bg, highlightthickness=0, bd=0)
        self.t, self.S, self.size, self.on_click = theme, S, size, on_click
        self.muted, self._hover = False, False
        self.bind("<Enter>", lambda e: self._h(True))
        self.bind("<Leave>", lambda e: self._h(False))
        self.bind("<ButtonRelease-1>", lambda e: self.on_click())
        self.draw()

    def _h(self, v):
        self._hover = v
        self.draw()

    def set_muted(self, m: bool) -> None:
        if m != self.muted:
            self.muted = m
            self.draw()

    def draw(self) -> None:
        S, t, fg = self.S, self.t, self.t.text
        self.delete("all")
        if self._hover:
            _rrect(self, 1, 1, self.size - 1, self.size - 1, S(6), fill=t.hover, outline="")
        cx, cy = self.size / 2, self.size / 2
        self.create_polygon(cx - S(8), cy - S(3), cx - S(4), cy - S(3), cx + S(1), cy - S(8), cx + S(1), cy + S(8),
                            cx - S(4), cy + S(3), cx - S(8), cy + S(3), fill=fg, outline="")
        if self.muted:
            self.create_line(cx + S(4), cy - S(4), cx + S(11), cy + S(4), fill=fg, width=max(1, S(1.5)))
            self.create_line(cx + S(11), cy - S(4), cx + S(4), cy + S(4), fill=fg, width=max(1, S(1.5)))
        else:
            for r in (S(5), S(9)):
                self.create_arc(cx - r + S(2), cy - r, cx + r + S(2), cy + r, start=-40, extent=80,
                                style="arc", outline=fg, width=max(1, S(1.5)))


REPO_URL = "https://github.com/HoDZadkiel/EdifierReverse"


def app_version() -> str:
    """程式版本（來自套件 metadata，也就是 pyproject.toml 的 version）；讀不到時回空字串。"""
    from importlib.metadata import PackageNotFoundError, version

    try:
        return version("edifier-reverse")
    except PackageNotFoundError:
        return ""


class Flyout:
    """彈出面板。callbacks: anc(mode)、ambient(db)、volume(percent, final)、mute()、reconnect()、quit()。"""

    WIDTH, PAD = 340, 16

    def __init__(self, root: tk.Tk, callbacks: Dict[str, Callable], theme: Optional[Theme] = None):
        self.root, self.cb = root, callbacks
        self.theme = theme or (DARK if system_is_dark() else LIGHT)
        self.scale = max(1.0, root.winfo_fpixels("1i") / 96.0)
        S = self.S = lambda v: int(round(v * self.scale))
        self.state = DeviceState()
        self.volume: Optional[int] = None
        self.muted = False
        self._msg_job = None
        self._shown_at = 0.0

        families = set(tkfont.families(root))
        face = self.font_face = pick_font(families)
        px = lambda n, bold=False: tkfont.Font(root=root, family=face, size=-S(n), weight="bold" if bold else "normal")
        self.f_title, self.f_body, self.f_small = px(15, True), px(13), px(12)

        t = self.theme
        self.win = tk.Toplevel(root)
        self.win.withdraw()
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.configure(bg=t.border)
        body = tk.Frame(self.win, bg=t.bg)
        body.pack(fill="both", expand=True, padx=1, pady=1)
        W, P = S(self.WIDTH), S(self.PAD)
        inner = W - 2 * P - 2
        self.inner_w = inner

        # 標頭：logo、名稱與狀態、電量
        head = tk.Frame(body, bg=t.bg)
        head.pack(fill="x", padx=P, pady=(P, S(10)))
        self._logo_img = None
        self.logo_lbl = tk.Label(head, bg=t.bg, bd=0)
        self.logo_lbl.pack(side="left")
        names = tk.Frame(head, bg=t.bg)
        names.pack(side="left", padx=(S(12), 0))
        self.name_lbl = tk.Label(names, text="ES850NB", font=self.f_title, fg=t.text, bg=t.bg, anchor="w")
        self.name_lbl.pack(anchor="w")
        self.status_lbl = tk.Label(names, text=tr("p.connecting"), font=self.f_small, fg=t.sub, bg=t.bg, anchor="w")
        self.status_lbl.pack(anchor="w")
        self.battery_lbl = tk.Label(head, text="", font=px(20, True), fg=t.text, bg=t.bg)
        self.battery_lbl.pack(side="right")

        # 降噪模式：3 + 2 的磚塊
        tk.Label(body, text=tr("p.anc"), font=self.f_small, fg=t.sub, bg=t.bg, anchor="w").pack(
            fill="x", padx=P, pady=(S(4), S(6)))
        grid = tk.Frame(body, bg=t.bg)
        grid.pack(fill="x", padx=P)
        gap = S(8)
        tw = (inner - 2 * gap) // 3
        th = S(40)
        self.tiles: Dict[int, Tile] = {}
        for i, mode in enumerate(ANC_MODES[GROUP]):
            tile = Tile(grid, t, S, tr(f"anc.s.{mode}"), self.f_body, tw, th, (lambda m: lambda: self._anc_click(m))(mode))
            tile.grid(row=i // 3, column=i % 3, padx=(0 if i % 3 == 0 else gap, 0),
                      pady=(0 if i < 3 else gap, 0))
            self.tiles[mode] = tile

        # 環境音強度
        row = tk.Frame(body, bg=t.bg)
        row.pack(fill="x", padx=P, pady=(S(14), 0))
        self.amb_title = tk.Label(row, text=tr("p.ambient"), font=self.f_small, fg=t.sub, bg=t.bg)
        self.amb_title.pack(side="left")
        self.amb_val = tk.Label(row, text="", font=self.f_small, fg=t.text, bg=t.bg)
        self.amb_val.pack(side="right")
        self.amb_slider = SliderBar(body, t, S, DB_RANGE[0], DB_RANGE[1], 6, self._ambient_change, inner)
        self.amb_slider.pack(padx=P, pady=(S(2), 0))

        # 音量
        tk.Label(body, text=tr("p.volume"), font=self.f_small, fg=t.sub, bg=t.bg, anchor="w").pack(
            fill="x", padx=P, pady=(S(10), 0))
        vrow = tk.Frame(body, bg=t.bg)
        vrow.pack(fill="x", padx=P)
        self.mute_btn = IconButton(vrow, t, S, lambda: self.cb["mute"]())
        self.mute_btn.pack(side="left")
        self.vol_val = tk.Label(vrow, text="", font=self.f_small, fg=t.text, bg=t.bg, width=5, anchor="e")
        self.vol_val.pack(side="right")
        self.vol_slider = SliderBar(vrow, t, S, 0, 100, 0, self._volume_change, inner - S(32) - S(46))
        self.vol_slider.pack(side="left", padx=(S(4), 0))

        # 頁尾：訊息與動作
        foot = tk.Frame(body, bg=t.bg)
        foot.pack(fill="x", padx=P, pady=(S(10), P))
        self.version_lbl = tk.Label(foot, text=f"v{app_version()}" if app_version() else "", font=self.f_small,
                                    fg=t.sub, bg=t.bg, cursor="hand2")
        self.version_lbl.pack(side="left", padx=(0, S(10)))
        self.version_lbl.bind("<ButtonRelease-1>", lambda e: self.open_repo())  # 版本號連到 GitHub 倉庫
        self.msg_lbl = tk.Label(foot, text="", font=self.f_small, fg=t.error, bg=t.bg, anchor="w")
        self.msg_lbl.pack(side="left", fill="x", expand=True)
        for text, key in ((tr("p.quit"), "quit"), (tr("p.reconnect"), "reconnect")):
            b = tk.Label(foot, text=text, font=self.f_small, fg=t.accent, bg=t.bg, cursor="hand2")
            b.pack(side="right", padx=(S(12), 0))
            b.bind("<ButtonRelease-1>", (lambda k: lambda e: self.cb[k]())(key))

        self.win.bind("<Escape>", lambda e: self.hide())
        self.win.bind("<FocusOut>", self._focus_out)
        self.win.update_idletasks()
        self.w, self.h = self.win.winfo_reqwidth(), self.win.winfo_reqheight()
        self._style_window()
        self.update(self.state, None, False)

    # ---- 視窗 ----
    def _style_window(self) -> None:
        style_window(self.win, self.theme is DARK)

    def _focus_out(self, _e) -> None:
        import time

        if time.monotonic() - self._shown_at > 0.3:  # 剛顯示時的焦點切換不要收起
            self.win.after(80, self._hide_if_unfocused)

    def _hide_if_unfocused(self) -> None:
        try:
            if self.win.focus_displayof() is None:
                self.hide()
        except KeyError:
            self.hide()

    @property
    def visible(self) -> bool:
        return self.win.state() == "normal"

    def show(self) -> None:
        import time

        x, y = flyout_position(work_area(), self.w, self.h, self.S(12))
        self.win.geometry(f"{self.w}x{self.h}+{x}+{y}")
        self._shown_at = time.monotonic()
        self.win.deiconify()
        self.win.lift()
        self.win.focus_force()

    def hide(self) -> None:
        self.win.withdraw()

    def toggle(self) -> None:
        self.hide() if self.visible else self.show()

    # ---- 互動 ----
    def _anc_click(self, mode: int) -> None:
        for m, tile in self.tiles.items():  # 先樂觀更新，裝置推送會校正
            tile.configure_state(m == mode, tile.enabled)
        self.cb["anc"](mode)

    def _ambient_change(self, v: int, final: bool) -> None:
        self.amb_val.config(text=str(v))
        if final:  # 設定指令有 ≥2 秒節流，只在放開時送一次
            self.cb["ambient"](v)

    def _volume_change(self, v: int, final: bool) -> None:
        self.vol_val.config(text=f"{v}%")
        self.cb["volume"](v, final)

    def open_repo(self) -> None:
        import webbrowser

        webbrowser.open(REPO_URL)

    def show_message(self, text: str, ms: int = 4000) -> None:
        self.msg_lbl.config(text=text)
        if self._msg_job:
            self.win.after_cancel(self._msg_job)
        self._msg_job = self.win.after(ms, lambda: self.msg_lbl.config(text=""))

    # ---- 狀態 ----
    def update(self, state: DeviceState, volume: Optional[int], muted: bool) -> None:
        from PIL import ImageTk

        self.state, self.volume, self.muted = state, volume, muted
        t = self.theme
        self._logo_img = ImageTk.PhotoImage(make_logo(self.S(48), state.battery, state.connected))
        self.logo_lbl.config(image=self._logo_img)
        self.name_lbl.config(text=state.name or "ES850NB")
        if state.connected:
            self.status_lbl.config(text=tr("p.connected_fw", v=state.version))
            # 淺色底上的淺綠／橘字不易讀，淺色主題改用一般文字色（電量色已在 logo 的環上）
            self.battery_lbl.config(text=f"{state.battery}%",
                                    fg=battery_hex(state.battery) if self.theme is DARK else t.text)
        else:
            self.status_lbl.config(text=tr("p.disconnected_err", err=state.error[:28]) if state.error else tr("p.disconnected"), fg=t.sub)
            self.battery_lbl.config(text="–", fg=t.sub)

        can_set = state.connected and state.anc_supported
        cur = state.anc.mode if state.anc else None
        # 冷卻期：裝置在兩次設定的間隔內會靜默忽略，連按只會卡住，所以暫停切換
        usable = can_set and not state.cooling
        for m, tile in self.tiles.items():
            tile.configure_state(can_set and m == cur, usable)

        ambient_on = can_set and cur == AMBIENT_MODE[GROUP]
        self.amb_slider.set_enabled(ambient_on and not state.cooling)
        if ambient_on and state.anc and state.anc.db:
            self.amb_slider.set_value(state.anc.db)
        self.amb_val.config(text=str(self.amb_slider.value) if ambient_on else "")
        self.amb_title.config(fg=t.sub if ambient_on else t.disabled)

        self.vol_slider.set_enabled(volume is not None)
        if volume is not None:
            self.vol_slider.set_value(volume)
        self.vol_val.config(text="" if volume is None else f"{volume}%")
        self.mute_btn.set_muted(muted)


def battery_hex(battery: Optional[int]) -> str:
    r, g, b = battery_color(battery, True)
    return f"#{r:02X}{g:02X}{b:02X}"


def enable_dpi_awareness() -> None:
    """讓 Tk 以實際 DPI 繪製（否則在縮放 125%/150% 的螢幕上會模糊）。要在建立 Tk() 之前呼叫。"""
    if sys.platform != "win32":
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass
