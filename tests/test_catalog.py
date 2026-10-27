import asyncio
import re

import pytest

from edifier_reverse import catalog
from edifier_reverse import commands as C
from edifier_reverse.ble import PROFILES, identify_adverts, profile_for
from edifier_reverse.packet import Session
from edifier_reverse.service import DeviceService
from edifier_reverse.sim import SimulatedDevice
from edifier_reverse.volume import VolumeControl

UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
# 實機掃描 ES850NB 的廣播（2026-10-27）：廠商 0x07E0 資料 = A2DP 位址 + bleVersion + encryption
REAL_MFR = {0x07E0: bytes.fromhex("aabbcc1122330210")}
ES850_UUID = "00002101-0000-1000-8000-00805f9b34fb"


def test_catalog_is_well_formed_and_search_uuids_are_unique():
    assert len(catalog.MODELS) >= 100
    searches = [m.search for m in catalog.MODELS]
    assert len(set(searches)) == len(searches), "兩個機種不能共用搜尋 UUID，否則無法分辨"
    assert all(UUID.match(m.search) and UUID.match(m.service) for m in catalog.MODELS)
    assert len({m.model for m in catalog.MODELS}) == len(catalog.MODELS)


def test_known_models_match_what_was_verified_on_the_real_devices():
    es, mp = catalog.BY_NAME["ES850NB"], catalog.BY_NAME["MP330"]
    assert (es.search, es.spp) == (ES850_UUID, True)             # 實機驗證：走 SPP
    assert (mp.search, mp.spp) == ("00004c01-0000-1000-8000-00805f9b34fb", False)  # 靜態分析：走 BLE
    assert PROFILES["ES850NB"].spp == catalog.SPP_UUID and PROFILES["MP330"].spp == ""
    assert PROFILES["ES850NB"].service == "48092101-1a48-11e9-ab14-d663bd873d93"


def test_identify_by_search_uuid_names_the_model():
    assert catalog.identify([ES850_UUID], {}).model == "ES850NB"
    assert catalog.identify(["0000180F-0000-1000-8000-00805F9B34FB", ES850_UUID.upper()], REAL_MFR).model == "ES850NB"


def test_identify_by_company_id_alone_gives_a_generic_edifier_device():
    # 新機種不在目錄裡，但廠商 ID 是 Edifier：仍要認得出來
    m = catalog.identify(["0000fff0-0000-1000-8000-00805f9b34fb"], REAL_MFR)
    assert m.model == catalog.GENERIC_MODEL and m.spp


@pytest.mark.parametrize("services,mfr", [
    ([], {}),                                                    # 完全無關的裝置
    (["0000180f-0000-1000-8000-00805f9b34fb"], {0x004C: bytes(8)}),  # 別家廠商
    ([], {0x07E0: bytes(5)}),                                    # 廠商 ID 對但資料太短，取不到位址
])
def test_identify_rejects_non_edifier_advertisements(services, mfr):
    assert catalog.identify(services, mfr) is None


def test_identify_adverts_lists_every_edifier_device_strongest_first_and_skips_others():
    other = bytes.fromhex("c0ffee000001" "0210")
    entries = [
        ("11:11", -80, [ES850_UUID], {0x07E0: other}),                                  # 同型號第二台
        ("22:22", -50, [], {0x004C: bytes(8)}),                                         # 別人的裝置
        ("33:33", -60, [ES850_UUID], REAL_MFR),                                          # 同型號第一台
        ("44:44", -70, [ES850_UUID], {}),                                                # 有 UUID 但沒廠商資料：取不到位址
        ("55:55", -65, ["0000fff0-0000-1000-8000-00805f9b34fb"], {0x07E0: other}),       # 新機種
    ]
    found = identify_adverts(entries)
    assert [(a.ble_address, a.model) for a in found] == [("33:33", "ES850NB"), ("55:55", "Edifier"), ("11:11", "ES850NB")]
    assert found[0].a2dp_address == "AA:BB:CC:11:22:33" and found[0].session == Session(2, 16)


def test_profile_for_unknown_model_falls_back_to_spp():
    assert profile_for("NoSuchModel").spp == catalog.SPP_UUID
    assert profile_for("MP330").spp == ""


# ---- 其他機種：只讀查詢逐項容錯 ----
SESSION = Session(2, 16)


def run_connect(drop):
    async def go():
        async def connector():
            d = SimulatedDevice(SESSION)
            for cmd in drop:
                d.state.pop(cmd, None)
            return d, SESSION

        svc = DeviceService(connector, backoff=(0.01,), query_timeout=0.15)
        svc.start()
        try:
            await asyncio.sleep(1.2)
            return svc.state
        finally:
            await svc.stop()

    return asyncio.run(go())


def test_device_without_anc_still_connects_with_battery_and_status():
    st = run_connect([C.ANC_QUERY])
    assert st.connected and st.battery == 80 and st.anc is None and not st.anc_supported


def test_device_without_battery_still_connects_with_anc():
    st = run_connect([C.BATTERY_QUERY])
    assert st.connected and st.battery is None and st.anc is not None


def test_device_answering_neither_battery_nor_anc_is_not_treated_as_connected():
    assert not run_connect([C.BATTERY_QUERY, C.ANC_QUERY]).connected


# ---- 多台裝置：音量要調目前連線那一台 ----
def test_volume_target_switches_endpoint_by_device_name():
    opened = []

    class EP:
        scalar, mute = 0.5, 0

        def GetMasterVolumeLevelScalar(self):
            return self.scalar

    v = VolumeControl("EDIFIER", opener=lambda n: opened.append(n) or EP())
    v.get()
    v.get()
    v.set_target("EDIFIER ES850NB")   # 換成另一台：重新尋找端點
    v.get()
    v.set_target("EDIFIER ES850NB")   # 同名不重找
    v.get()
    assert opened == ["EDIFIER", "EDIFIER ES850NB"]
