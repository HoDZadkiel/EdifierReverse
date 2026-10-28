"""Windows 11 風格的右鍵選單（Tkinter）。內容由 build_menu 產生（純資料，可測試），TrayMenu 負責畫出來。

項目與代號和 C# 版（src/EdifierTray/Ui/TrayMenuModel.cs）一致：anc:1..5、mute、vol+、vol-、dev:<位址>、
rescan、theme:system|light|dark、lang:<代碼>、open、reconnect、autostart、quit。
"""
import time
import tkinter as tk
import tkinter.font as tkfont
from dataclasses import dataclass
from typing import Callable, List, Optional, Sequence, Tuple

from . import i18n
from .flyout import DARK, LIGHT, Theme, style_window, system_is_dark, work_area
from .i18n import tr
from .logo import make_logo
from .service import DeviceState

VOLUME_STEP = 10
MODE_IDS = (1, 2, 3, 4, 5)

HEADER, LABEL, ITEM, SEPARATOR, SEGMENTED = "header", "label", "item", "separator", "segmented"


@dataclass(frozen=True)
class MenuEntry:
    kind: str
    text: str = ""
    glyph: str = ""      # Segoe Fluent Icons 的字元
    detail: str = ""     # 右側次要文字，例如目前音量
    checked: bool = False
    enabled: bool = True
    id: str = ""
    options: Tuple[Tuple[str, str, bool], ...] = ()  # 分段按鈕：(代號, 文字, 是否選中)


def build_menu(state: DeviceState, volume: Optional[int], muted: bool, autostart: bool,
               devices: Sequence[Tuple[str, str, bool]] = (), theme: str = "system", language: str = "zh-TW",
               scanning: bool = False, fallback_name: str = "Edifier") -> List[MenuEntry]:
    """降噪項目在未連線、group 未驗證、或冷卻期內都停用（冷卻期內裝置會忽略設定）。
    devices：(位址, 顯示名稱, 是否為目前選用) 的清單。"""
    can_anc = state.connected and state.anc_supported and not state.cooling
    selectable = state.connected and state.anc_supported
    has_volume = volume is not None
    sep = MenuEntry(SEPARATOR)
    # 標頭用簡短的模式名稱，長名稱（例如英文）會把整個選單撐寬
    mode_name = tr(f"anc.s.{state.anc.mode}") if selectable and state.anc is not None else state.anc_label
    detail = tr("m.header_detail", b=state.battery, mode=mode_name) if state.connected else tr("m.not_connected")
    items: List[MenuEntry] = [MenuEntry(HEADER, state.name or fallback_name, detail=detail), sep,
                              MenuEntry(LABEL, tr("m.anc"))]
    for mode in MODE_IDS:
        items.append(MenuEntry(ITEM, tr(f"anc.s.{mode}"), "",
                               checked=selectable and state.anc is not None and state.anc.mode == mode,
                               enabled=can_anc, id=f"anc:{mode}"))
    items += [
        sep,
        MenuEntry(ITEM, tr("m.unmute") if muted else tr("m.mute"), "" if muted else "",
                  detail=f"{volume}%" if has_volume else "", enabled=has_volume, id="mute"),
        MenuEntry(ITEM, tr("m.vol_up", n=VOLUME_STEP), "", enabled=has_volume, id="vol+"),
        MenuEntry(ITEM, tr("m.vol_down", n=VOLUME_STEP), "", enabled=has_volume, id="vol-"),
        sep,
        MenuEntry(LABEL, tr("m.devices")),
    ]
    for a2dp, label, selected in devices:
        items.append(MenuEntry(ITEM, label, "", checked=selected, id=f"dev:{a2dp}"))
    if not devices:
        items.append(MenuEntry(ITEM, tr("m.no_devices"), "", enabled=False))
    items += [
        MenuEntry(ITEM, tr("m.scanning") if scanning else tr("m.rescan"), "", enabled=not scanning, id="rescan"),
        sep,
        MenuEntry(LABEL, tr("m.appearance")),
        MenuEntry(SEGMENTED, options=tuple((f"theme:{t}", tr(f"m.theme.{t}"), t == theme)
                                           for t in ("system", "light", "dark"))),
        MenuEntry(LABEL, tr("m.language")),
        MenuEntry(SEGMENTED, options=tuple((f"lang:{c}", i18n.SHORT[c], c == language) for c in i18n.codes())),
        sep,
        MenuEntry(ITEM, tr("m.open"), "", id="open"),
        MenuEntry(ITEM, tr("m.reconnect"), "", id="reconnect"),
        sep,
        MenuEntry(ITEM, tr("m.autostart"), "", checked=autostart, id="autostart"),
        MenuEntry(ITEM, tr("m.quit"), "", id="quit"),
    ]
    return items


def place_menu(cursor_x: float, cursor_y: float, width: float, height: float,
               area: Tuple[int, int, int, int], gap: int = 8) -> Tuple[int, int]:
    """選單位置：優先放在游標上方（工作列在下方時），放不下就放下方；水平貼著游標並夾在工作區內。
    Tk 已是 DPI 感知，座標全是裝置像素，不需要換算。"""
    left_a, top_a, right_a, bottom_a = area
    left = max(min(cursor_x - gap, right_a - width - gap), left_a + gap)
    top = cursor_y - height - gap
    if top < top_a + gap:
        top = cursor_y + gap
    top = max(min(top, bottom_a - height - gap), top_a + gap)
    return int(left), int(top)


class TrayMenu:
    WIDTH = 272

    def __init__(self, root: tk.Tk, on_invoke: Callable[[str], None], font_face: str,
                 theme: Optional[Theme] = None):
        self.root, self.on_invoke = root, on_invoke
        self.theme = theme or (DARK if system_is_dark() else LIGHT)
        self.scale = max(1.0, root.winfo_fpixels("1i") / 96.0)
        self.S = lambda v: int(round(v * self.scale))
        families = set(tkfont.families(root))
        icon_face = "Segoe Fluent Icons" if "Segoe Fluent Icons" in families else "Segoe MDL2 Assets"
        mk = lambda face, n, bold=False: tkfont.Font(root=root, family=face, size=-self.S(n),
                                                     weight="bold" if bold else "normal")
        self.f_title, self.f_body, self.f_small = mk(font_face, 14, True), mk(font_face, 13), mk(font_face, 12)
        self.f_icon, self.f_check = mk(icon_face, 15), mk(icon_face, 14)
        self.entries: List[MenuEntry] = []
        self._rows: List[Tuple[tk.Frame, MenuEntry]] = []
        self._logo_img = None
        self._shown_at = self._hidden_at = 0.0

        t = self.theme
        self.win = tk.Toplevel(root)
        self.win.withdraw()
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.configure(bg=t.border)
        self.body = tk.Frame(self.win, bg=t.bg)
        self.body.pack(fill="both", expand=True, padx=1, pady=1)
        self.win.bind("<Escape>", lambda e: self.hide())
        self.win.bind("<FocusOut>", self._focus_out)

    # ---- 內容 ----
    def populate(self, entries: Sequence[MenuEntry], state: DeviceState) -> None:
        from PIL import ImageTk

        t, S = self.theme, self.S
        self.entries = list(entries)
        for child in self.body.winfo_children():
            child.destroy()
        self._rows.clear()
        self._logo_img = ImageTk.PhotoImage(make_logo(S(36), state.battery, state.connected))
        for e in entries:
            if e.kind == HEADER:
                head = tk.Frame(self.body, bg=t.bg)
                head.pack(fill="x", padx=S(8), pady=(S(8), S(4)))
                tk.Label(head, image=self._logo_img, bg=t.bg, bd=0).pack(side="left", padx=(0, S(10)))
                box = tk.Frame(head, bg=t.bg)
                box.pack(side="left")
                tk.Label(box, text=e.text, font=self.f_title, fg=t.text, bg=t.bg, anchor="w").pack(anchor="w")
                tk.Label(box, text=e.detail, font=self.f_small, fg=t.sub, bg=t.bg, anchor="w").pack(anchor="w")
            elif e.kind == LABEL:
                tk.Label(self.body, text=e.text, font=self.f_small, fg=t.sub, bg=t.bg, anchor="w").pack(
                    fill="x", padx=(S(12), 0), pady=(S(6), S(2)))
            elif e.kind == SEPARATOR:
                tk.Frame(self.body, bg=t.border, height=1).pack(fill="x", padx=S(8), pady=S(4))
            elif e.kind == SEGMENTED:
                self._build_segmented(e)
            else:
                self._build_item(e)
        self.win.update_idletasks()

    def _build_item(self, e: MenuEntry) -> None:
        t, S = self.theme, self.S
        fg = t.text if e.enabled else t.disabled
        row = tk.Frame(self.body, bg=t.bg, height=S(34))
        row.pack(fill="x", padx=S(2), pady=1)
        row.pack_propagate(False)
        row.grid_columnconfigure(2, weight=1)
        kids: List[tk.Widget] = []
        is_mode = e.id.startswith("anc:")
        if e.checked and is_mode:  # 降噪模式用 Windows 11 導覽列那種小色條
            tk.Frame(row, bg=t.accent, width=3, height=S(16)).place(x=S(2), rely=0.5, anchor="w")
        if e.glyph:
            lbl = tk.Label(row, text=e.glyph, font=self.f_icon, fg=fg, bg=t.bg)
            lbl.place(x=S(10), width=S(28), rely=0.5, anchor="w")
            kids.append(lbl)
        lbl = tk.Label(row, text=e.text, font=self.f_body, fg=fg, bg=t.bg, anchor="w")
        lbl.place(x=S(44), rely=0.5, anchor="w")
        kids.append(lbl)
        if e.detail:
            lbl = tk.Label(row, text=e.detail, font=self.f_small, fg=t.sub if e.enabled else t.disabled, bg=t.bg)
            lbl.place(relx=1.0, x=-S(34), rely=0.5, anchor="e")
            kids.append(lbl)
        if e.checked and not is_mode:
            lbl = tk.Label(row, text="", font=self.f_check, fg=t.accent, bg=t.bg)
            lbl.place(relx=1.0, x=-S(14), rely=0.5, anchor="e")
            kids.append(lbl)
        self._rows.append((row, e))

        def paint(color: str) -> None:
            row.configure(bg=color)
            for k in kids:
                k.configure(bg=color)

        for w in [row, *kids]:
            if e.enabled:
                w.bind("<Enter>", lambda _e: paint(t.hover))
                w.bind("<Leave>", lambda _e: paint(t.bg))
                w.bind("<ButtonRelease-1>", lambda _e, i=e.id: self._invoke(i))
                w.configure(cursor="hand2")

    def _build_segmented(self, e: MenuEntry) -> None:
        """一排並列的小按鈕（外觀、語言），選中的填強調色。"""
        t, S = self.theme, self.S
        row = tk.Frame(self.body, bg=t.bg)
        row.pack(fill="x", padx=S(10), pady=(S(2), S(4)))
        for i, (opt_id, text, selected) in enumerate(e.options):
            row.grid_columnconfigure(i, weight=1, uniform="seg")
            fill, fg = (t.accent, t.accent_text) if selected else (t.surface, t.text)
            lbl = tk.Label(row, text=text, font=self.f_small, fg=fg, bg=fill, cursor="hand2", height=1,
                           highlightthickness=1, highlightbackground=t.accent if selected else t.border)
            lbl.grid(row=0, column=i, sticky="nsew", padx=(0 if i == 0 else S(4), 0), ipady=S(5))
            if not selected:
                lbl.bind("<Enter>", lambda _e, w=lbl: w.configure(bg=t.hover))
                lbl.bind("<Leave>", lambda _e, w=lbl: w.configure(bg=t.surface))
            lbl.bind("<ButtonRelease-1>", lambda _e, i=opt_id: self._invoke(i))
        self._rows.append((row, e))

    def _invoke(self, item_id: str) -> None:
        self.hide()
        self.on_invoke(item_id)

    # ---- 顯示 ----
    @property
    def visible(self) -> bool:
        return self.win.state() == "normal"

    def show_at(self, x: int, y: int) -> None:
        self.win.update_idletasks()
        w, h = self.win.winfo_reqwidth(), self.win.winfo_reqheight()
        px, py = place_menu(x, y, w, h, work_area())
        self.win.geometry(f"{w}x{h}+{px}+{py}")
        style_window(self.win, self.theme is DARK)
        self._shown_at = time.monotonic()
        self.win.deiconify()
        self.win.lift()
        self.win.focus_force()

    def hide(self) -> None:
        self._hidden_at = time.monotonic()
        self.win.withdraw()

    def toggle_at(self, x: int, y: int) -> None:
        """右鍵點圖示：開著就收起；剛因失焦收起的那一下點擊不重開（點圖示會先讓選單失焦）。"""
        if self.visible:
            self.hide()
        elif time.monotonic() - self._hidden_at >= 0.25:
            self.show_at(x, y)

    def _focus_out(self, _e) -> None:
        if time.monotonic() - self._shown_at > 0.3:
            self.win.after(80, self._hide_if_unfocused)

    def _hide_if_unfocused(self) -> None:
        try:
            if self.win.focus_displayof() is None:
                self.hide()
        except KeyError:
            self.hide()
