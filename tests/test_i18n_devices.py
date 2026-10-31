import asyncio
import re
from types import SimpleNamespace

import pytest

from edifier_reverse import i18n
from edifier_reverse import settings as S
from edifier_reverse.devices import DeviceDirectory
from edifier_reverse.menu import SEGMENTED, build_menu
from edifier_reverse.responses import AncState
from edifier_reverse.service import DeviceState, ServiceError


@pytest.fixture(autouse=True)
def reset_language():
    yield
    i18n.set_language(i18n.DEFAULT)


# ---- 字串表 ----
def test_every_language_has_exactly_the_same_keys():
    keys = {c: set(i18n._TABLE[c]) for c in i18n.codes()}
    base = keys["zh-TW"]
    for code, k in keys.items():
        assert k == base, f"{code} 缺少 {sorted(base - k)}，多出 {sorted(k - base)}"


def test_placeholders_match_across_languages():
    # 譯文漏掉或寫錯 {參數} 會在畫面上出現亂碼或例外
    ph = lambda t: sorted(re.findall(r"\{(\w+)\}", t))
    for key, text in i18n._TABLE["zh-TW"].items():
        for code in i18n.codes():
            assert ph(i18n._TABLE[code][key]) == ph(text), f"{code}:{key}"


def test_translations_are_actually_translated_for_ui_strings():
    # 防止整批複製繁中當作簡中／日文／英文
    for key in ("p.anc", "m.mute", "e.not_connected", "m.theme.dark"):
        vals = {c: i18n._TABLE[c][key] for c in i18n.codes()}
        assert len(set(vals.values())) >= 3, (key, vals)
    assert i18n._TABLE["en"]["p.anc"] == "Noise cancelling" and i18n._TABLE["ja"]["m.mute"] == "ミュート"


def test_tr_formats_and_falls_back():
    i18n.set_language("en")
    assert i18n.tr("m.header_detail", b=16, mode="X") == "Battery 16%　·　X"
    assert i18n.tr("no.such.key") == "no.such.key"           # 缺字不讓畫面出錯
    assert i18n.set_language("fr") == "zh-TW"                  # 未知語言退回預設
    assert i18n.tr("m.mute") == "靜音"
    assert i18n.tr("m.vol_up") == "音量 +{n}%"                  # 缺參數時保留原文而不是丟例外


def test_service_errors_and_anc_labels_follow_the_language():
    st = DeviceState(True, "X", "1", 80, AncState(16, 2))
    assert st.anc_label == "舒適降噪"
    i18n.set_language("ja")
    assert st.anc_label == "コンフォートノイズキャンセリング"
    assert "未検証" in DeviceState(True, "X", "1", 80, AncState(18, 1)).anc_label
    i18n.set_language("zh-CN")
    assert "尚未连接" in str(ServiceError("e.not_connected"))
    i18n.set_language("en")
    assert str(ServiceError("e.strength_range", lo=1, hi=12)) == "Level must be between 1 and 12"


def test_font_follows_the_language_and_keeps_serif_fonts_out():
    fams = ["Segoe UI", "Segoe UI Variable Text", "Microsoft JhengHei UI", "Microsoft YaHei UI", "Yu Gothic UI", "MingLiU"]
    assert i18n.pick_font(fams, "zh-TW") == "Microsoft JhengHei UI"
    assert i18n.pick_font(fams, "zh-CN") == "Microsoft YaHei UI"
    assert i18n.pick_font(fams, "ja") == "Yu Gothic UI"
    assert i18n.pick_font(fams, "en") == "Segoe UI Variable Text"
    assert i18n.pick_font(["MingLiU"], "zh-TW") == "Segoe UI"  # 只剩細明體時也不選它


# ---- 設定檔 ----
def test_settings_roundtrip_and_corrupt_or_unknown_values(tmp_path):
    p = tmp_path / "settings.json"
    assert S.load(p) == S.Settings()                         # 沒有檔案
    s = S.Settings(theme="dark", language="ja", selected="AA:BB:CC:11:22:33")
    s.remember(S.KnownDevice("AA:BB:CC:11:22:33", "AA:BB:CC:11:22:34", 2, 16, "EDIFIER ES850NB"))
    S.save(s, p)
    assert S.load(p) == s
    p.write_text("{壞掉", encoding="utf-8")
    assert S.load(p) == S.Settings()                         # 壞檔不能讓程式起不來
    p.write_text('{"theme": "neon", "devices": [{"a2dp": "AA", "bogus": 1}, 5]}', encoding="utf-8")
    loaded = S.load(p)
    assert loaded.theme == "system" and [d.a2dp for d in loaded.devices] == ["AA"]


def test_remember_keeps_a_known_name_when_the_scan_has_none():
    s = S.Settings()
    s.remember(S.KnownDevice("AA:AA:AA:AA:AA:01", name="EDIFIER ES850NB", version=2, encryption=16))
    s.remember(S.KnownDevice("AA:AA:AA:AA:AA:01", ble="BLE1", version=2, encryption=16))  # 重新掃描，沒有名稱
    assert len(s.devices) == 1 and s.devices[0].name == "EDIFIER ES850NB" and s.devices[0].ble == "BLE1"


# ---- 裝置目錄（多裝置） ----
def adverts(*items):
    async def scanner(seconds):
        return [SimpleNamespace(a2dp_address=a, ble_address=b, session=SimpleNamespace(version=2, encryption=16), rssi=-60)
                for a, b in items]
    return scanner


def directory(scan_items=(), settings=None, unpaired=(), paired_list=()):
    """unpaired：視為沒配對的位址；其餘都視為已配對。paired_list：Windows 已配對的 (位址, 名稱)，預設空（不碰真實系統）。"""
    saves = []

    async def lister():
        return list(paired_list)

    async def is_paired(a2dp):
        return a2dp not in unpaired

    d = DeviceDirectory("ES850NB", settings or S.Settings(), save=lambda s: saves.append(1),
                        scanner=adverts(*scan_items), is_paired=is_paired, paired_lister=lister)
    return d, saves


TWO = [("AA:AA:AA:AA:AA:01", "B1"), ("AA:AA:AA:AA:AA:02", "B2")]


def test_scan_adds_all_devices_and_does_not_duplicate_on_rescan():
    d, saves = directory(TWO)
    assert [x.a2dp for x in asyncio.run(d.scan())] == ["AA:AA:AA:AA:AA:01", "AA:AA:AA:AA:AA:02"]
    asyncio.run(d.scan())
    assert len(d.devices) == 2 and saves


def test_selection_defaults_to_the_first_device_and_follows_select():
    d, _ = directory(TWO)
    assert d.current() is None
    asyncio.run(d.scan())
    assert d.current().a2dp.endswith("01")
    assert d.select("aa:aa:aa:aa:aa:02") and d.current().a2dp.endswith("02")   # 位址不分大小寫
    assert not d.select("FF:FF:FF:FF:FF:FF") and d.current().a2dp.endswith("02")  # 未知位址不改變選擇


def test_selected_device_missing_from_the_list_falls_back_to_the_first():
    s = S.Settings(selected="DE:AD:BE:EF:00:00", devices=[S.KnownDevice("AA:AA:AA:AA:AA:01")])
    assert s.current_device().a2dp.endswith("01")


def test_labels_tell_same_model_units_apart_by_address_tail():
    d, _ = directory(TWO)
    asyncio.run(d.scan())
    d.remember_name("AA:AA:AA:AA:AA:01", "EDIFIER ES850NB")
    labels = [d.label(x) for x in d.devices]
    assert labels[0] == "ES850NB · AA:01"           # 連過：用裝置名稱（去掉品牌前綴）
    assert labels[1] == "ES850NB · AA:02"           # 沒連過：用辨識出的機種名稱
    assert d.label(S.KnownDevice("BB:BB:BB:BB:BB:09", model="Edifier")).endswith("· BB:09")  # 新機種


def test_remember_name_saves_only_when_it_changes():
    d, saves = directory([TWO[0]])
    asyncio.run(d.scan())
    n = len(saves)
    d.remember_name("AA:AA:AA:AA:AA:01", "X")
    d.remember_name("AA:AA:AA:AA:AA:01", "X")
    assert len(saves) == n + 1


# ---- 選單：裝置、外觀、語言 ----
def conn():
    return DeviceState(True, "EDIFIER ES850NB", "1.0.1", 16, AncState(16, 1))


def test_menu_lists_devices_with_the_selected_one_checked_and_a_rescan_item():
    m = build_menu(conn(), 50, False, False, [("A1", "ES850NB · 01", False), ("A2", "ES850NB · 02", True)])
    assert [(e.id, e.checked) for e in m if e.id.startswith("dev:")] == [("dev:A1", False), ("dev:A2", True)]
    assert next(e for e in m if e.id == "rescan").enabled
    busy = build_menu(conn(), 50, False, False, [], scanning=True)
    assert not next(e for e in busy if e.id == "rescan").enabled
    assert not any(e.id.startswith("dev:") for e in busy)           # 沒有裝置時只有停用的提示列


def test_menu_has_theme_and_language_segments_marking_the_current_choice():
    m = build_menu(conn(), 50, False, False, theme="dark", language="ja")
    segs = [e for e in m if e.kind == SEGMENTED]
    assert [[o[0] for o in s.options] for s in segs] == [["theme:system", "theme:light", "theme:dark"],
                                                        ["lang:zh-TW", "lang:zh-CN", "lang:en", "lang:ja"]]
    assert [o[0] for o in segs[0].options if o[2]] == ["theme:dark"]
    assert [o[0] for o in segs[1].options if o[2]] == ["lang:ja"]


def test_menu_text_follows_the_language():
    i18n.set_language("en")
    m = build_menu(conn(), 50, True, False)
    assert next(e for e in m if e.id == "mute").text == "Unmute"
    assert next(e for e in m if e.id == "quit").text == "Quit"
    i18n.set_language("ja")
    assert next(e for e in build_menu(conn(), 50, False, False) if e.id == "open").text == "パネルを開く"

def test_unpaired_devices_found_nearby_are_hidden():
    d, _ = directory(TWO, unpaired={TWO[1][0]})
    assert [x.a2dp for x in asyncio.run(d.scan())] == [TWO[0][0]]   # 沒配對的連不上，不列出
    assert [x.a2dp for x in d.devices] == [TWO[0][0]]


def test_a_device_that_is_no_longer_paired_is_dropped_and_the_selection_cleared():
    unpaired = set()
    d, _ = directory(TWO, unpaired=unpaired)
    asyncio.run(d.scan())
    d.select(TWO[1][0])
    unpaired.add(TWO[1][0])                                          # 使用者在系統裡移除了配對
    asyncio.run(d.prune_unpaired())
    assert [x.a2dp for x in d.devices] == [TWO[0][0]]
    assert d.current().a2dp == TWO[0][0]                             # 選擇回到仍存在的第一台


def test_fallback_name_uses_the_known_device_not_a_hardcoded_model():
    assert DeviceDirectory.fallback_name(None) == "Edifier"
    assert DeviceDirectory.fallback_name(S.KnownDevice("A", model="MP330")) == "MP330"
    assert DeviceDirectory.fallback_name(S.KnownDevice("A", model="MP330", name="EDIFIER X")) == "EDIFIER X"


# ---- 已連著電腦的裝置不再廣播：用 Windows 已配對清單補上（MP330 實機情境） ----
MP = ("AA:BB:B3:11:22:33", "EDIFIER MP330")


def test_paired_device_without_advert_is_added_with_derived_ble_address_and_verified_session():
    from edifier_reverse.devices import fallback_ble_address

    assert fallback_ble_address("AA:BB:B3:11:22:33") == "AA:BB:B0:11:22:33"   # 格式與實機相同：第三個 byte B3 → B0（MP330）
    assert fallback_ble_address("AA:BB:B3:44:55:66") == "AA:BB:B0:44:55:66"   # 格式與實機相同（ES850NB）
    d, saves = directory(paired_list=[MP])
    got = asyncio.run(d.scan())
    assert [(x.a2dp, x.ble, x.model, x.name) for x in got] == [(MP[0], "AA:BB:B0:11:22:33", "MP330", MP[1])]
    assert (got[0].version, got[0].encryption) == (2, 0)   # MP330 實機不加密；退回值不能是 ES850NB 的 16
    assert saves


def test_fallback_session_for_es850nb_is_encrypted_and_unknown_models_are_generic():
    d, _ = directory(paired_list=[("AA:BB:B3:44:55:66", "EDIFIER ES850NB"), ("AA:AA:AA:AA:AA:09", "EDIFIER ZZ999")])
    a, b = asyncio.run(d.scan())
    assert (a.model, a.encryption) == ("ES850NB", 16)
    assert (b.model, b.encryption) == ("Edifier", 0)


def test_fallback_does_not_override_advertised_or_known_devices_and_skips_unpaired():
    d, _ = directory([("AA:BB:B3:11:22:33", "AA:BB:B0:11:22:33")], paired_list=[MP])
    got = asyncio.run(d.scan())
    assert len(got) == 1 and got[0].encryption == 16   # 有廣播就用廣播的 Session
    d, _ = directory(paired_list=[MP], unpaired=[MP[0]])
    assert asyncio.run(d.scan()) == []


# ---- 以藍牙連線狀態自動挑裝置（沒明確選過時） ----
def two_models(selected, connected):
    s = S.Settings(selected=selected)
    s.devices = [S.KnownDevice("AA:BB:B3:44:55:66", "AA:BB:B0:44:55:66", 2, 16, model="ES850NB"),
                 S.KnownDevice("AA:BB:B3:11:22:33", "AA:BB:B0:11:22:33", 2, 0, model="MP330")]

    async def lister():
        return []

    async def is_connected(a2dp):
        return a2dp in connected

    return DeviceDirectory("ES850NB", s, save=lambda x: None, paired_lister=lister, is_connected=is_connected)


def test_without_an_explicit_choice_the_device_connected_over_bluetooth_is_used():
    # 使用者的實際情況：清單第一台是沒開機的 ES850NB，MP330 已連上電腦 → 應挑 MP330，不去等 ES850NB 的 SPP
    d = two_models("", {"AA:BB:B3:11:22:33"})
    assert [x.model for x in asyncio.run(d.candidates())] == ["MP330"]
    assert [x.model for x in asyncio.run(two_models("", set()).candidates())] == ["ES850NB"]   # 都沒連著：退回第一台


def test_an_explicit_choice_is_kept_even_when_another_device_is_the_one_connected():
    d = two_models("AA:BB:B3:11:22:33", {"AA:BB:B3:44:55:66"})
    assert [x.model for x in asyncio.run(d.candidates())] == ["MP330"]   # 不擅自改選


def test_add_paired_adds_new_paired_devices_without_a_scan():
    d, saves = directory(paired_list=[MP])
    assert asyncio.run(d.add_paired()) is True and [x.model for x in d.devices] == ["MP330"]
    assert asyncio.run(d.add_paired()) is False   # 已在清單內：不重複、不再存檔
