"""Edifier 機種目錄與裝置辨識（資料見 catalog.json，由 scripts/gen_catalog.py 從官方 App 的產品表產生）。

官方 App 的判定方式：每個機種有一個 BLE 搜尋 UUID，廣播的 service UUID 含它就是該機種；廠商資料（廠商 ID 0x07E0）
帶經典藍牙位址、bleVersion 與 encryption。所以這裡用兩個條件辨識 Edifier 裝置：
  1. 廣播含目錄內任一機種的搜尋 UUID（知道是哪個機種）；
  2. 或廠商資料的廠商 ID 是 0x07E0 且長度足夠（確定是 Edifier，但機種不在目錄裡，例如新機種）。
"""
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional

EDIFIER_COMPANY_ID = 0x07E0  # 2016
GENERIC_MODEL = "Edifier"    # 確定是 Edifier 但不在目錄內
# 目錄內所有機種共用的 GATT 特徵值與 SPP UUID（產品表 102 筆全部相同）
READ_UUID = "48090001-1a48-11e9-ab14-d663bd873d93"
WRITE_UUID = "48090002-1a48-11e9-ab14-d663bd873d93"
SPP_UUID = "EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF"


@dataclass(frozen=True)
class Model:
    model: str
    search: str
    service: str
    spp: bool
    type: Optional[int] = None
    tws: bool = False
    published: bool = True


def _load() -> List[Model]:
    raw = json.loads(Path(__file__).with_name("catalog.json").read_text(encoding="utf-8"))
    return [Model(**m) for m in raw]


MODELS: List[Model] = _load()
BY_SEARCH: Dict[str, Model] = {m.search: m for m in MODELS}
BY_NAME: Dict[str, Model] = {m.model: m for m in MODELS}


def identify(service_uuids: Iterable[str], manufacturer_data: Dict[int, bytes]) -> Optional[Model]:
    """判定一則 BLE 廣播是不是 Edifier 裝置。回傳機種；不是 Edifier 回傳 None；
    是 Edifier 但機種不在目錄回傳 GENERIC_MODEL 的 Model。"""
    for u in service_uuids:
        m = BY_SEARCH.get(u.lower())
        if m is not None:
            return m
    data = manufacturer_data.get(EDIFIER_COMPANY_ID)
    if data is not None and len(data) >= 8:
        return Model(GENERIC_MODEL, "", "", spp=True)  # 未知機種：SPP UUID 全機種共用，先試 SPP
    return None
