"""Windows 托盤小工具（Python 版）：托盤圖示、Windows 11 風格的彈出面板與右鍵選單。

DeviceService 跑在背景執行緒的 asyncio 迴圈；托盤圖示（pystray）在自己的執行緒；
Tkinter 面板、選單與音量控制（COM）都在主執行緒。跨執行緒的工作一律丟進佇列，由主執行緒處理。
音量走 Windows 音訊端點（volume.py），不經 Edifier 協定。需要選用相依：uv sync --extra tray

外觀（日間／夜間／跟隨系統）、語言（繁中／簡中／英／日）與選用的裝置存在設定檔（settings.py），
改變時重建面板與選單（Tk 視窗的文字與顏色在建立時就定了，重建最單純）。
"""
import asyncio
import concurrent.futures
import queue
import threading
from typing import Callable, Optional

from . import autostart as _autostart
from . import i18n
from . import settings as S
from .alerts import HOTKEY_LABELS, HOTKEYS, OsdTracker, next_anc_mode
from .i18n import tr
from .logo import make_logo
from .menu import VOLUME_STEP, build_menu
from .osd import Osd
from .service import AMBIENT_MODE, DeviceService, DeviceState, ServiceError
from .volume import VolumeControl, VolumeError

MODEL_GROUP = 16  # 只有 group 16 的模式表經實機驗證


def tooltip_text(state: DeviceState, fallback_name: str = "Edifier") -> str:
    name = state.name or fallback_name
    if not state.connected:
        return tr("t.disconnected", name=name) + (f"\n{state.error}" if state.error else "")
    mode = state.anc_label
    if state.anc and state.anc.db and state.anc.mode == AMBIENT_MODE.get(state.anc.group):
        mode += tr("t.strength", n=state.anc.db)
    return "\n".join([name, tr("t.battery", b=state.battery), tr("t.anc", mode=mode)])[:120]  # 托盤提示上限約 128 字


class ServiceRunner:
    """在背景執行緒跑 asyncio 迴圈與 DeviceService。"""

    def __init__(self, connector, on_change: Callable[[DeviceState], None]):
        self._connector = connector
        self._on_change = on_change
        self._ready = threading.Event()
        self.loop: Optional[asyncio.AbstractEventLoop] = None
        self.service: Optional[DeviceService] = None
        self._thread = threading.Thread(target=self._main, name="device-service", daemon=True)

    def _main(self) -> None:
        self.loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self.loop)
        self.service = DeviceService(self._connector, on_change=self._on_change)
        self.loop.call_soon(self._begin)  # DeviceService.start 需要在執行中的迴圈裡呼叫
        self.loop.run_forever()

    def _begin(self) -> None:
        self.service.start()
        self._ready.set()

    def start(self) -> None:
        self._thread.start()
        self._ready.wait(5)

    def submit(self, coro) -> concurrent.futures.Future:
        return asyncio.run_coroutine_threadsafe(coro, self.loop)

    def stop(self) -> None:
        if self.loop is None:
            return
        try:
            self.submit(self.service.stop()).result(3)
        except Exception:
            pass
        self.loop.call_soon_threadsafe(self.loop.stop)


class TrayApp:
    def __init__(self, connector, volume: Optional[VolumeControl] = None, headless: bool = False,
                 directory=None, settings: Optional[S.Settings] = None):
        """headless=True 不建立 Tk 與托盤圖示（測試用，只驅動服務與動作邏輯）。
        directory：DeviceDirectory（裝置選擇與掃描）；測試可不給。"""
        self.settings = settings if settings is not None else S.load()
        if not self.settings.language:
            self.settings.language = i18n.detect_system_language()
            S.save(self.settings)
        i18n.set_language(self.settings.language)
        self.directory = directory
        self.volume = volume or VolumeControl()
        self.volume_percent: Optional[int] = None
        self.muted = False
        self.scanning = False
        self.headless = headless
        self._tasks: "queue.Queue[Callable[[], None]]" = queue.Queue()
        self.watcher = None
        self.tracker = OsdTracker()
        self.hotkeys = None
        self.runner = ServiceRunner(connector, lambda s: self.post(lambda: self._apply_state(s)))
        self.root = self.flyout = self.icon = self.menu = self.osd = None

    # ---- 執行緒橋接 ----
    def post(self, fn: Callable[[], None]) -> None:
        """從任何執行緒安排一件事到主執行緒（Tk）執行。"""
        self._tasks.put(fn)

    def _pump(self) -> None:
        while True:
            try:
                self._tasks.get_nowait()()
            except queue.Empty:
                break
        if self.root is not None:
            self.root.after(50, self._pump)

    @property
    def state(self) -> DeviceState:
        return self.runner.service.state if self.runner.service else DeviceState()

    # ---- 外觀與語言 ----
    def theme(self):
        from .flyout import DARK, LIGHT, system_is_dark

        t = self.settings.theme
        return DARK if t == "dark" else LIGHT if t == "light" else (DARK if system_is_dark() else LIGHT)

    def _build_ui(self) -> None:
        """依目前的主題與語言建立面板與選單（也用於切換後重建）。"""
        from .flyout import Flyout
        from .menu import TrayMenu

        theme = self.theme()
        self.flyout = Flyout(self.root, {
            "anc": self.set_anc, "ambient": self.set_ambient, "volume": self.set_volume,
            "mute": self.toggle_mute, "reconnect": lambda: self.runner.service.reconnect(), "quit": self.quit,
        }, theme)
        self.menu = TrayMenu(self.root, lambda i: self.post(lambda: self.on_menu_item(i)), self.flyout.font_face, theme)
        self.osd = Osd(self.root, theme, self.flyout.font_face)

    def _rebuild_ui(self) -> None:
        for w in (self.flyout, self.menu, self.osd):
            if w is not None:
                w.win.destroy()
        self._build_ui()
        self._render()

    def set_theme(self, theme: str) -> None:
        if theme not in S.THEMES:
            return
        self.settings.theme = theme
        S.save(self.settings)
        if self.root is not None:
            self._rebuild_ui()

    def set_language(self, code: str) -> None:
        self.settings.language = i18n.set_language(code)
        S.save(self.settings)
        if self.root is not None:
            self._rebuild_ui()

    # ---- 裝置 ----
    def select_device(self, a2dp: str) -> None:
        if self.directory is None or not self.directory.select(a2dp):
            return
        self._watch_current()
        self.runner.service.reconnect()

    # ---- 連線事件：已配對的耳機連上電腦時提前重連 ----
    def _watch_current(self) -> None:
        if self.directory is None or self.runner.loop is None:
            return
        if self.watcher is None:
            from .pairing import ConnectionWatcher

            loop, svc = self.runner.loop, self.runner.service
            self.watcher = ConnectionWatcher(lambda: loop.call_soon_threadsafe(svc.notify_device_available))
        cur = self.directory.current()
        self.runner.submit(self.watcher.watch(cur.a2dp if cur is not None else ""))

    async def _prune_and_watch(self) -> None:
        """啟動時清掉已不再配對的已知裝置，再開始監看；失敗不影響其他功能。"""
        try:
            await self.directory.prune_unpaired()
        except Exception:
            pass
        self.post(self._watch_current)

    @property
    def fallback_name(self) -> str:
        from .devices import DeviceDirectory

        return DeviceDirectory.fallback_name(self.directory.current() if self.directory is not None else None)

    def rescan_devices(self) -> None:
        if self.directory is None or self.scanning:
            return
        self.scanning = True
        fut = self.runner.submit(self.directory.scan())

        def done(f):
            self.scanning = False
            try:
                found = f.result()
                msg = tr("m.found_n", n=len(found))
            except Exception as e:
                msg = tr("e.scan_failed", err=e)
            self.post(lambda: (self._say(msg), self._watch_current()))  # 第一次掃到裝置時才有選用裝置可監看

        fut.add_done_callback(done)

    def _device_rows(self):
        if self.directory is None:
            return []
        cur = self.directory.current()
        return [(d.a2dp, self.directory.label(d), cur is not None and d.a2dp == cur.a2dp) for d in self.directory.devices]

    # ---- 狀態 ----
    def _apply_state(self, state: DeviceState) -> None:
        was = getattr(self, "_was_connected", False)
        self._was_connected = state.connected
        if state.connected and state.name and state.name != getattr(self, "_volume_target", None):
            self._volume_target = state.name
            self.volume.set_target(state.name)  # 多台 Edifier 裝置時，調的是目前連線那一台的播放端點
            was = False
        if state.connected != was:  # 連上／斷線時重選端點：連上用耳機端點，斷線改用備援的預設輸出
            self.volume.reset()
            self.refresh_volume()
        if state.connected and state.name and self.directory is not None and self.directory.current() is not None:
            self.directory.remember_name(self.directory.current().a2dp, state.name)
        for msg in self.tracker.update(state):
            self.show_osd(msg)
        self._render(state)

    def _render(self, state: Optional[DeviceState] = None) -> None:
        state = state or self.state
        if self.icon is not None:
            self.icon.icon = make_logo(64, state.battery, state.connected, number=True)
            self.icon.title = tooltip_text(state, self.fallback_name)
        if self.flyout is not None:
            self.flyout.update(state, self.volume_percent, self.muted, self.fallback_name)

    def refresh_volume(self) -> None:
        try:
            self.volume_percent = self.volume.get()
            self.muted = self.volume.muted()
        except VolumeError:
            self.volume_percent = None

    # ---- 面板的動作 ----
    def set_anc(self, mode: int, db: Optional[int] = None) -> None:
        if self.state.cooling:  # 冷卻期內不送（面板已停用按鈕；這裡擋住選單等其他入口，避免排隊卡住）
            return
        fut = self.runner.submit(self.runner.service.set_anc(mode, db))

        def done(f):
            try:
                f.result()
            except ServiceError as e:
                msg = str(e)
            except Exception as e:
                msg = f"{type(e).__name__}: {e}"
            else:
                return
            self.post(lambda: (self._say(msg), self._render()))  # 失敗時把面板校正回真實狀態

        fut.add_done_callback(done)

    def show_osd(self, text: str) -> None:
        if self.osd is not None:
            self.osd.show(text)

    def _say(self, msg: str) -> None:
        if self.flyout is not None:
            self.flyout.show_message(msg)

    def set_ambient(self, db: int) -> None:
        self.set_anc(AMBIENT_MODE[MODEL_GROUP], db)

    def set_volume(self, percent: int, final: bool = True) -> None:
        try:
            self.volume_percent = self.volume.set(percent)
        except VolumeError as e:
            self._say(str(e))
            return
        if final:
            self.refresh_volume()
            self._render()

    def step_volume(self, delta: int) -> None:
        try:
            self.volume_percent = self.volume.step(delta)
        except VolumeError as e:
            self._say(str(e))
            return
        self.refresh_volume()
        self._render()

    # ---- 全域快捷鍵（Ctrl+Alt+…）----
    def on_hotkey(self, name: str) -> None:
        if name == "anc":
            mode = next_anc_mode(self.state)
            if mode is None:
                self.show_osd(tr("e.not_connected"))
            else:
                self.set_anc(mode)  # 成功後裝置推送新狀態，由 OsdTracker 顯示新模式；冷卻期內 set_anc 會自己略過
            return
        if name == "mute":
            self.toggle_mute()
        else:
            self.step_volume(VOLUME_STEP if name == "vol+" else -VOLUME_STEP)
        if self.volume_percent is None:
            self.show_osd(tr("e.vol_access"))
        else:
            self.show_osd(tr("m.mute") if self.muted else tr("o.volume", n=self.volume_percent))

    def _start_hotkeys(self) -> None:
        from .hotkeys import HotkeyListener

        self.hotkeys = HotkeyListener(HOTKEYS, lambda n: self.post(lambda: self.on_hotkey(n)))
        failed = self.hotkeys.start()
        if failed:
            self.post(lambda: self.show_osd(tr("o.hotkey_failed", keys=", ".join(HOTKEY_LABELS[k] for k in failed))))

    def toggle_autostart(self) -> None:
        try:
            _autostart.set_enabled(not _autostart.is_enabled())
        except OSError as e:
            self._say(tr("e.autostart", err=e))

    def toggle_mute(self) -> None:
        try:
            self.volume.set_mute(not self.volume.muted())
        except VolumeError as e:
            self._say(str(e))
        self.refresh_volume()
        self._render()

    # ---- 右鍵選單 ----
    def show_menu_at(self, x: int, y: int) -> None:
        """依目前狀態重建選單並在指定位置（裝置像素）切換顯示。"""
        entries = build_menu(self.state, self.volume_percent, self.muted, _autostart.is_enabled(),
                             self._device_rows(), self.settings.theme, i18n.language(), self.scanning, self.fallback_name)
        self.menu.populate(entries, self.state)
        self.menu.toggle_at(x, y)

    def on_menu_item(self, item_id: str) -> None:
        kind, _, arg = item_id.partition(":")
        if kind == "anc":
            self.set_anc(int(arg))
        elif kind == "dev":
            self.select_device(arg)
        elif kind == "theme":
            self.set_theme(arg)
        elif kind == "lang":
            self.set_language(arg)
        else:
            {
                "mute": self.toggle_mute,
                "vol+": lambda: self.step_volume(+VOLUME_STEP),
                "vol-": lambda: self.step_volume(-VOLUME_STEP),
                "rescan": self.rescan_devices,
                "open": self.flyout.show,
                "reconnect": lambda: self.runner.service.reconnect(),
                "autostart": self.toggle_autostart,
                "quit": self.quit,
            }[item_id]()

    def quit(self) -> None:
        self.runner.stop()
        if self.icon is not None:
            self.icon.stop()
        if self.watcher is not None:
            self.watcher.stop()
        if self.hotkeys is not None:
            self.hotkeys.stop()
        if self.root is not None:
            self.root.quit()

    # ---- 啟動 ----
    def run(self) -> None:
        import tkinter as tk

        import pystray

        from .flyout import enable_dpi_awareness

        enable_dpi_awareness()
        self.root = tk.Tk()
        self.root.withdraw()
        self._build_ui()
        # 左鍵＝開啟面板（pystray 的預設項目）；右鍵改由自己的選單視窗處理，不用 pystray 的原生選單
        menu = pystray.Menu(pystray.MenuItem(tr("m.open"), lambda icon, item: self.post(lambda: self.flyout.toggle()),
                                             default=True))
        self.icon = _make_icon("edifier-reverse", make_logo(64, None, False),
                               tr("t.connecting", name=self.fallback_name), menu,
                               lambda x, y: self.post(lambda: self.show_menu_at(x, y)))
        self.runner.start()
        self._start_hotkeys()
        if self.directory is not None:
            self.runner.submit(self._prune_and_watch())
        self.refresh_volume()
        self._render()
        self.icon.run_detached()
        self.root.after(50, self._pump)
        try:
            self.root.mainloop()
        finally:
            self.runner.stop()


def _make_icon(name, image, title, menu, on_right_click):
    """pystray 的圖示，但右鍵不顯示原生選單，改呼叫 on_right_click(x, y)（裝置像素）。"""
    import ctypes
    from ctypes import wintypes

    import pystray
    from pystray._util import win32

    class _Icon(pystray.Icon):
        def _on_notify(self, wparam, lparam):
            if lparam == win32.WM_RBUTTONUP:
                pt = wintypes.POINT()
                ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
                on_right_click(pt.x, pt.y)
                return
            super()._on_notify(wparam, lparam)

    return _Icon(name, image, title, menu)


def main(model: str = "ES850NB") -> None:
    from .devices import DeviceDirectory

    directory = DeviceDirectory(model)
    TrayApp(directory.connector(), directory=directory, settings=directory.settings).run()
