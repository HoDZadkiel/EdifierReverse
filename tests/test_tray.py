import time
from types import SimpleNamespace

import pytest

pytest.importorskip("pystray")
pytest.importorskip("PIL")

from edifier_reverse import commands as C  # noqa: E402
from edifier_reverse.flyout import (DARK, LIGHT, flyout_position, value_from_x,  # noqa: E402
                                    x_from_value)
from edifier_reverse.logo import battery_color, make_logo  # noqa: E402
from edifier_reverse.packet import Session  # noqa: E402
from edifier_reverse.responses import AncState  # noqa: E402
from edifier_reverse.service import DeviceState  # noqa: E402
from edifier_reverse.sim import SimulatedDevice  # noqa: E402
from edifier_reverse.tray import TrayApp, tooltip_text  # noqa: E402
from edifier_reverse.volume import VolumeControl  # noqa: E402

SESSION = Session(2, 16)


def connected(battery=80, mode=1, db=None):
    return DeviceState(True, "EDIFIER ES850NB", "1.0.1", battery, AncState(16, mode, db))


# ---- 提示文字與圖示 ----
def test_tooltip_disconnected_shows_the_error():
    t = tooltip_text(DeviceState(False, error="ConnectionError: 掃不到"))
    assert t.startswith("Edifier：未連線") and "掃不到" in t  # 沒有裝置資訊時不假設是哪個機種
    assert tooltip_text(DeviceState(False), "ES850NB").startswith("ES850NB：未連線")  # 有選用裝置時用它的機種／名稱


def test_tooltip_shows_battery_mode_and_ambient_strength_only_in_ambient_mode():
    assert "電量 80%" in tooltip_text(connected()) and "深度降噪" in tooltip_text(connected())
    assert "強度 8" in tooltip_text(connected(mode=4, db=8))
    assert "強度" not in tooltip_text(connected(mode=2, db=6))  # 非環境音模式的 db 不顯示


@pytest.mark.parametrize("battery,expect", [(100, (108, 203, 95)), (50, (108, 203, 95)), (49, (255, 170, 40)),
                                            (20, (255, 170, 40)), (19, (255, 99, 97)), (0, (255, 99, 97))])
def test_ring_color_follows_battery_thresholds(battery, expect):
    assert battery_color(battery, True) == expect


def test_disconnected_ring_is_gray_regardless_of_old_battery():
    assert battery_color(90, False) == (120, 120, 120) and battery_color(None, True) == (120, 120, 120)


def test_logo_is_rgba_and_the_ring_length_follows_the_battery():
    a, b = make_logo(64, 25, True), make_logo(64, 75, True)
    assert a.size == (64, 64) and a.mode == "RGBA"
    # 右側（3 點鐘方向）的環：25% 時還沒走到那裡，75% 時已經經過且是綠色
    px = lambda im: im.getpixel((60, 32))
    close = lambda c, ref: all(abs(x - y) <= 12 for x, y in zip(c[:3], ref))  # 縮小時邊緣會有些微混色
    assert close(px(b), (108, 203, 95)) and not close(px(a), (108, 203, 95))


def test_logo_corners_are_transparent():
    assert make_logo(64, 50, True).getpixel((0, 0))[3] == 0


# ---- 面板的純計算 ----
def test_slider_value_mapping_clamps_and_rounds():
    assert value_from_x(50, 0, 100, 0, 100) == 50
    assert value_from_x(-30, 0, 100, 0, 100) == 0 and value_from_x(500, 0, 100, 0, 100) == 100
    assert value_from_x(10, 10, 10, 3, 9) == 3  # 軌道寬度為 0 不能除以零


def test_slider_x_is_the_inverse_of_value():
    for v in (0, 25, 100):
        x = x_from_value(v, 11, 300, 0, 100)
        assert value_from_x(x, 11, 300, 0, 100) == v


def test_flyout_sits_in_the_bottom_right_of_the_work_area_and_never_off_screen():
    assert flyout_position((0, 0, 1920, 1040), 340, 400, 12) == (1568, 628)
    assert flyout_position((0, 0, 200, 300), 340, 400, 12) == (0, 0)  # 螢幕比面板小：貼左上，不出界


# ---- 面板本身（需要顯示環境） ----
def make_flyout(root, theme=DARK):
    from edifier_reverse.flyout import Flyout

    calls = []
    cb = {"anc": lambda m: calls.append(("anc", m)), "ambient": lambda v: calls.append(("ambient", v)),
          "volume": lambda v, f: calls.append(("volume", v, f)), "mute": lambda: calls.append(("mute",)),
          "reconnect": lambda: calls.append(("reconnect",)), "quit": lambda: calls.append(("quit",))}
    return Flyout(root, cb, theme), calls


def test_flyout_reflects_state_and_disables_controls_when_not_applicable(tk_root):
    f, _ = make_flyout(tk_root)
    f.update(connected(16, mode=4, db=8), 50, False)
    assert f.tiles[4].selected and not f.tiles[1].selected and f.tiles[1].enabled
    assert f.amb_slider.enabled and f.amb_slider.value == 8
    assert f.vol_slider.enabled and f.vol_slider.value == 50 and f.vol_val.cget("text") == "50%"
    assert f.battery_lbl.cget("text") == "16%"

    f.update(connected(16, mode=2), 50, True)
    assert not f.amb_slider.enabled  # 只有環境音模式可調強度
    assert f.mute_btn.muted

    f.update(DeviceState(False, error="x"), None, False)
    assert not any(t.enabled for t in f.tiles.values()) and not f.vol_slider.enabled
    assert f.battery_lbl.cget("text") == "–"

    f.update(DeviceState(True, "X", "1", 80, AncState(18, 1)), 50, False)  # 未驗證的 group
    assert not any(t.enabled for t in f.tiles.values())


def test_flyout_interactions_call_back_with_the_right_values(tk_root):
    f, calls = make_flyout(tk_root, LIGHT)
    f.update(connected(60, mode=4, db=6), 30, False)

    f._anc_click(3)
    assert calls[-1] == ("anc", 3) and f.tiles[3].selected  # 樂觀更新

    left, right = f.vol_slider._span()
    x = left + (right - left) * 0.8
    f.vol_slider._press(SimpleNamespace(x=x))
    f.vol_slider._release(SimpleNamespace(x=x))
    vols = [c for c in calls if c[0] == "volume"]
    assert vols[-1] == ("volume", 80, True) and any(not c[2] for c in vols)  # 拖曳中與放開都有回報

    calls.clear()
    left, right = f.amb_slider._span()
    f.amb_slider._press(SimpleNamespace(x=right))  # 拖到最右 = 12
    assert not [c for c in calls if c[0] == "ambient"]  # 拖曳中不送設定（裝置有節流）
    f.amb_slider._release(SimpleNamespace(x=right))
    assert calls[-1] == ("ambient", 12)


def test_disabled_slider_ignores_input(tk_root):
    f, calls = make_flyout(tk_root)
    f.update(connected(60, mode=1), 30, False)  # 非環境音：強度條停用
    f.amb_slider._press(SimpleNamespace(x=100))
    f.amb_slider._release(SimpleNamespace(x=100))
    assert calls == []


# ---- 應用程式邏輯（不開視窗） ----
class FakeEndpoint:
    scalar, mute = 0.5, 0

    def GetMasterVolumeLevelScalar(self):
        return self.scalar

    def SetMasterVolumeLevelScalar(self, v, ctx):
        self.scalar = v

    def GetMute(self):
        return self.mute

    def SetMute(self, m, ctx):
        self.mute = m


def wait_for(cond, timeout=10.0):  # 機器忙碌（同步、編譯）時 3 秒不夠
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if cond():
            return True
        time.sleep(0.02)
    return False


def test_app_actions_drive_the_service_and_the_volume():
    ep = FakeEndpoint()

    async def connector():
        d = SimulatedDevice(SESSION, push_anc=True)
        d.state[C.ANC_QUERY] = bytes([16, 1])
        return d, SESSION

    app = TrayApp(connector, VolumeControl("X", opener=lambda n: ep), headless=True)
    app.runner.start()
    try:
        assert wait_for(lambda: app.state.connected)
        app.set_anc(3)
        assert wait_for(lambda: app.state.anc.mode == 3)
        assert wait_for(lambda: not app.state.cooling, timeout=4)  # 冷卻期內新的切換會被忽略，要等結束
        app.set_ambient(8)
        assert wait_for(lambda: (app.state.anc.mode, app.state.anc.db) == (4, 8))

        app.refresh_volume()
        assert app.volume_percent == 50
        app.set_volume(25, final=False)
        assert ep.scalar == 0.25 and app.volume_percent == 25
        app.toggle_mute()
        assert app.muted and ep.mute == 1
    finally:
        app.runner.stop()


def test_failed_anc_request_is_queued_for_the_main_thread():
    async def connector():
        d = SimulatedDevice(SESSION)
        d.state[C.ANC_QUERY] = bytes([18, 1])  # 未驗證 group：service 會拒絕
        return d, SESSION

    app = TrayApp(connector, VolumeControl("X", opener=lambda n: FakeEndpoint()), headless=True)
    app.runner.start()
    try:
        assert wait_for(lambda: app.state.connected), app.state
        before = app._tasks.qsize()
        app.set_anc(2)
        assert wait_for(lambda: app._tasks.qsize() > before)  # 失敗訊息已排進主執行緒佇列
    finally:
        app.runner.stop()


def test_flyout_disables_switching_during_the_cooldown_but_keeps_showing_the_current_mode(tk_root):
    from dataclasses import replace

    f, calls = make_flyout(tk_root)
    cool = replace(connected(60, mode=4, db=6), cooling=True)
    f.update(cool, 50, False)
    assert f.tiles[4].selected                                   # 仍標示目前模式
    assert not any(t.enabled for t in f.tiles.values())          # 但全部不能點
    assert not f.amb_slider.enabled
    f.tiles[2].event_generate("<ButtonRelease-1>")
    f.amb_slider._press(SimpleNamespace(x=100))
    f.amb_slider._release(SimpleNamespace(x=100))
    assert calls == []                                           # 冷卻期內點擊與拖曳都不會送出

    f.update(replace(cool, cooling=False), 50, False)            # 冷卻結束：恢復
    assert all(t.enabled for t in f.tiles.values()) and f.amb_slider.enabled


def test_app_ignores_anc_requests_during_the_cooldown():
    async def connector():
        d = SimulatedDevice(SESSION, push_anc=True)
        d.state[C.ANC_QUERY] = bytes([16, 1])
        return d, SESSION

    app = TrayApp(connector, VolumeControl("X", opener=lambda n: FakeEndpoint()), headless=True)
    app.runner.start()
    try:
        assert wait_for(lambda: app.state.connected)
        app.set_anc(2)
        assert wait_for(lambda: app.state.cooling)
        app.set_anc(3)                     # 冷卻中：直接忽略，不排隊
        assert wait_for(lambda: not app.state.cooling, timeout=4)
        assert app.state.anc.mode == 2
    finally:
        app.runner.stop()


def test_flyout_shows_the_app_version_in_the_bottom_left(tk_root):
    # 版本號來自 pyproject.toml（套件 metadata），面板左下角要看得到
    from importlib.metadata import version

    f, _ = make_flyout(tk_root)
    assert f.version_lbl.cget("text") == f"v{version('edifier-reverse')}"


def test_clicking_the_version_opens_the_github_repo(tk_root, monkeypatch):
    import webbrowser

    opened = []
    monkeypatch.setattr(webbrowser, "open", opened.append)
    f, _ = make_flyout(tk_root)
    f.show()  # 沒顯示（unmapped）的元件收不到事件
    f.win.update()
    f.version_lbl.event_generate("<ButtonRelease-1>")
    f.win.update()
    assert opened == ["https://github.com/HoDZadkiel/EdifierReverse"]
