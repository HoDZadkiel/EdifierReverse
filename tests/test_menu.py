import uuid
import winreg

import pytest

from edifier_reverse import autostart
from edifier_reverse.flyout import DARK
from edifier_reverse.i18n import pick_font
from edifier_reverse.menu import (HEADER, ITEM, SEPARATOR, TrayMenu, build_menu, place_menu)
from edifier_reverse.responses import AncState
from edifier_reverse.service import DeviceState


def conn(mode=1, group=16, cooling=False):
    return DeviceState(True, "EDIFIER ES850NB", "1.0.1", 16, AncState(group, mode), cooling=cooling)


def by_id(entries, item_id):
    return next(e for e in entries if e.id == item_id)


def test_connected_menu_has_all_quick_actions_and_marks_the_current_mode():
    m = build_menu(conn(mode=3), 50, False, False)
    # 與 C# 版 TrayMenuModel 的代號與順序一致
    assert [e.id for e in m if e.id] == ["anc:1", "anc:2", "anc:3", "anc:4", "anc:5", "mute", "vol+", "vol-",
                                         "rescan", "open", "reconnect", "autostart", "quit"]
    assert [e.id for e in m if e.id.startswith("anc:") and e.checked] == ["anc:3"]
    assert all(e.enabled for e in m if e.kind == ITEM and e.id)  # 沒有裝置時的提示列本來就是停用的
    assert "電量 16%" in m[0].detail and "防風噪" in m[0].detail


def test_mute_item_flips_label_and_icon_and_shows_volume():
    on, off = by_id(build_menu(conn(), 35, False, False), "mute"), by_id(build_menu(conn(), 35, True, False), "mute")
    assert (on.text, on.detail) == ("靜音", "35%")
    assert off.text == "取消靜音" and on.glyph != off.glyph


def test_disconnected_disables_anc_and_volume_but_keeps_reconnect_and_quit():
    m = build_menu(DeviceState(), None, False, False)
    assert not any(by_id(m, i).enabled for i in ("anc:1", "anc:5", "mute", "vol+", "vol-"))
    assert all(by_id(m, i).enabled for i in ("open", "reconnect", "autostart", "quit"))
    assert m[0].kind == HEADER and m[0].detail == "未連線"
    assert not any(e.checked for e in m if e.id.startswith("anc:"))


def test_cooldown_disables_anc_items_but_still_shows_the_current_mode():
    # 冷卻期內裝置會靜默忽略設定：項目停用，但目前模式的色條仍要顯示
    m = build_menu(conn(mode=2, cooling=True), 50, False, False)
    assert not any(e.enabled for e in m if e.id.startswith("anc:"))
    assert by_id(m, "anc:2").checked
    assert by_id(m, "mute").enabled  # 音量不受冷卻影響


def test_unverified_group_keeps_anc_disabled_but_volume_works():
    m = build_menu(conn(group=18), 50, False, False)
    assert not by_id(m, "anc:2").enabled and by_id(m, "vol+").enabled
    assert not any(e.checked for e in m if e.id.startswith("anc:"))


def test_missing_volume_endpoint_disables_only_volume_items():
    m = build_menu(conn(), None, False, False)
    assert not by_id(m, "mute").enabled and by_id(m, "anc:2").enabled


def test_autostart_item_reflects_the_setting():
    assert by_id(build_menu(conn(), 50, False, True), "autostart").checked


@pytest.mark.parametrize("cx,cy,area", [(3200, 2085, (0, 0, 3840, 2088)), (300, 10, (0, 40, 1920, 1080)),
                                        (1910, 1070, (0, 0, 1920, 1040)), (0, 500, (0, 0, 1920, 1080))])
def test_placement_stays_inside_the_work_area(cx, cy, area):
    left, top = place_menu(cx, cy, 408, 828, area)
    assert area[0] <= left <= area[2] - 408 and area[1] <= top <= area[3] - 828


def test_placement_prefers_above_the_cursor_unless_there_is_no_room():
    assert place_menu(1000, 1000, 272, 400, (0, 0, 1920, 1080))[1] < 1000
    assert place_menu(1000, 20, 272, 400, (0, 0, 1920, 1080))[1] > 20


def test_font_prefers_the_windows_11_chinese_ui_font():
    # Tk 不指定家族時中文會落到細明體（有襯線），所以必須明確挑「微軟正黑體 UI」
    assert pick_font(["Segoe UI", "Microsoft JhengHei UI", "MingLiU"]) == "Microsoft JhengHei UI"
    assert pick_font(["Segoe UI", "Microsoft JhengHei"]) == "Microsoft JhengHei"
    assert pick_font(["Arial"]) == "Segoe UI"  # 都沒有時的最後退路


def test_autostart_enable_writes_the_command_and_disable_removes_it():
    root = f"Software\\EdifierReverseTest_{uuid.uuid4().hex}"  # 獨立測試路徑，不碰真正的開機項目
    key = root + "\\Run"
    try:
        assert not autostart.is_enabled(key)
        autostart.set_enabled(True, key, "T", '"C:\\Program Files\\py thon\\pythonw.exe" -m edifier_reverse tray')
        assert autostart.is_enabled(key, "T")
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as k:
            assert winreg.QueryValueEx(k, "T")[0].endswith("-m edifier_reverse tray")
        autostart.set_enabled(False, key, "T")
        assert not autostart.is_enabled(key, "T")
        autostart.set_enabled(False, key, "T")  # 重複停用不能出錯
    finally:
        try:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, key)
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, root)
        except OSError:
            pass


def test_default_autostart_command_uses_a_quoted_windowless_interpreter():
    cmd = autostart.default_command()
    assert cmd.startswith('"') and cmd.endswith("-m edifier_reverse tray")


def test_menu_window_builds_rows_and_invokes_only_enabled_items(tk_root):
    invoked = []
    menu = TrayMenu(tk_root, invoked.append, "Segoe UI", DARK)
    menu.populate(build_menu(conn(mode=2), 50, False, False), conn(mode=2))
    assert [e.id for _, e in menu._rows if e.checked and e.id.startswith("anc:")] == ["anc:2"]

    menu.show_at(1200, 1000)  # 第一次就直接開（視窗從未顯示過）
    tk_root.update()          # 讓視窗真的對應到畫面，事件才送得進去
    assert menu.visible
    # 模擬點選「靜音」那一列的文字標籤
    row = next(r for r, e in menu._rows if e.id == "mute")
    label = [c for c in row.winfo_children() if c.cget("text") == "靜音"][0]
    label.event_generate("<ButtonRelease-1>")
    tk_root.update()
    assert invoked == ["mute"] and not menu.visible

    # 冷卻期：降噪項目不回應點擊
    menu.populate(build_menu(conn(mode=2, cooling=True), 50, False, False), conn(mode=2, cooling=True))
    menu.show_at(1200, 1000)
    tk_root.update()
    row = next(r for r, e in menu._rows if e.id == "anc:3")
    [c for c in row.winfo_children() if c.cget("text") == "防風噪"][0].event_generate("<ButtonRelease-1>")
    tk_root.update()
    assert invoked == ["mute"]
    menu.hide()


def test_menu_toggle_ignores_the_click_right_after_a_focus_loss_hide(tk_root):
    menu = TrayMenu(tk_root, lambda i: None, "Segoe UI", DARK)
    menu.populate(build_menu(conn(), 50, False, False), conn())
    menu.toggle_at(1200, 1000)
    assert menu.visible
    menu.toggle_at(1200, 1000)
    assert not menu.visible
    menu.toggle_at(1200, 1000)          # 緊接著的點擊視為「點圖示造成的收起」，不重開
    assert not menu.visible
