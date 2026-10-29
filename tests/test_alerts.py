import pytest

from edifier_reverse import i18n
from edifier_reverse.alerts import HOTKEYS, LOW_BATTERY, OsdTracker, next_anc_mode
from edifier_reverse.responses import AncState
from edifier_reverse.service import DeviceState


@pytest.fixture(autouse=True)
def zh_tw():
    i18n.set_language("zh-TW")


def st(battery=80, mode=1, connected=True, group=16):
    return DeviceState(connected, "EDIFIER ES850NB", "1.0.1", battery, AncState(group, mode))


# ---- 電量告警：只在往下穿過門檻時跳一次，回到門檻以上才重新武裝 ----
def test_low_battery_alerts_once_when_crossing_the_threshold():
    t = OsdTracker()
    assert t.update(st(battery=LOW_BATTERY + 1)) == []
    assert t.update(st(battery=LOW_BATTERY)) == ["電量偏低：20%"]
    assert t.update(st(battery=15)) == []      # 還在低電量，不重複跳
    assert t.update(st(battery=LOW_BATTERY + 1)) == []
    assert t.update(st(battery=LOW_BATTERY)) == ["電量偏低：20%"]  # 充電回到門檻以上後再掉下去，會再告警


def test_low_battery_is_reported_when_connecting_with_an_already_low_battery():
    assert OsdTracker().update(st(battery=8)) == ["電量偏低：8%"]


def test_reconnecting_while_still_low_does_not_alert_again():
    t = OsdTracker()
    t.update(st(battery=10))
    assert t.update(st(battery=10, connected=False)) == []
    assert t.update(st(battery=10)) == []       # 斷線重連不是新的告警


def test_unknown_battery_never_alerts():
    assert OsdTracker().update(st(battery=None)) == []


# ---- 模式改變 ----
def test_mode_change_shows_the_new_mode_including_changes_made_on_the_headset():
    t = OsdTracker()
    assert t.update(st(mode=1)) == []            # 第一筆是現況，不是改變
    assert t.update(st(mode=1)) == []            # 電量、冷卻旗標等其他更新不重複顯示
    assert t.update(st(mode=2)) == ["降噪：舒適降噪"]


def test_first_state_after_reconnect_is_not_a_mode_change():
    t = OsdTracker()
    t.update(st(mode=1))
    t.update(st(mode=1, connected=False))
    assert t.update(st(mode=3)) == []            # 斷線期間耳機可能被切過，重連的現況不當成改變
    assert t.update(st(mode=4)) == ["降噪：可調增益環境音"]


def test_ambient_strength_change_alone_is_not_a_mode_change():
    t = OsdTracker()
    t.update(DeviceState(True, "X", "1", 80, AncState(16, 4, 3)))
    assert t.update(DeviceState(True, "X", "1", 80, AncState(16, 4, 9))) == []


# ---- 快捷鍵輪替 ----
@pytest.mark.parametrize("mode,expect", [(1, 2), (2, 3), (4, 5), (5, 1)])
def test_next_anc_mode_cycles_and_wraps(mode, expect):
    assert next_anc_mode(st(mode=mode)) == expect


def test_next_anc_mode_refuses_when_it_cannot_set():
    assert next_anc_mode(st(connected=False)) is None
    assert next_anc_mode(st(group=18)) is None           # 未驗證的 group 不開放設定
    assert next_anc_mode(DeviceState(True, "X", "1", 80, None)) is None
    assert next_anc_mode(st(mode=9)) == 1                # 不在表內（韌體新增的模式）就從頭開始


def test_hotkeys_are_unique_virtual_keys():
    vks = [vk for group in HOTKEYS.values() for vk in group]
    assert len(set(vks)) == len(vks) == 5 and len(HOTKEYS) == 4
    assert {0x30, 0x60} == set(HOTKEYS["mute"])  # 靜音：主鍵盤 0 與數字鍵盤 0 都能用
