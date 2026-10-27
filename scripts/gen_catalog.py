"""由解密後的產品表（scripts/decrypt_products.py 的輸出）產生 Edifier 機種目錄 src/edifier_reverse/catalog.json。

官方 App 判定「這是不是 Edifier 裝置」的方式：以產品表每個機種的 ProductSearchUuid 當 BLE 掃描過濾條件
（廣播的 service UUID 含該值就是該機種），再讀廠商資料（廠商 ID 0x07E0）取得經典藍牙位址、版本與加密設定。
傳輸規則：產品表有 ProductSPPUuid 就走 SPP，否則走 BLE GATT。

這裡只保留判定與連線需要的事實（機種名稱、UUID、旗標），不含官方的文案、圖片與連結。
用法：python scripts/gen_catalog.py <解密後的 products.json> [輸出路徑]
"""
import json
import sys
from pathlib import Path


def build(items: list) -> list:
    out = []
    for it in items:
        search = it.get("ProductSearchUuid", "").lower()
        if not search:
            continue  # 沒有搜尋 UUID 的機種（USB 等）無法從 BLE 廣播辨識
        out.append({
            "model": it["ProductModel"],
            "search": search,
            "service": it.get("ProductServiceUuid", "").lower(),
            "spp": bool(it.get("ProductSPPUuid")),
            "type": it.get("HeadsetType"),
            "tws": bool(it.get("IsTWS")),
            "published": bool(it.get("IsPublish")),
        })
    # 同名機種（不同硬體版本、搜尋 UUID 不同）加上 UUID 片段區分，機種名稱才能當唯一鍵
    names = [m["model"] for m in out]
    for m in out:
        if names.count(m["model"]) > 1:
            m["model"] = f'{m["model"]} ({m["search"][4:8]})'
    return sorted(out, key=lambda x: x["model"].lower())


if __name__ == "__main__":
    src = Path(sys.argv[1])
    dst = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "src/edifier_reverse/catalog.json"
    models = build(json.loads(src.read_text(encoding="utf-8")))
    # 一機種一行，好讀好比對
    dst.write_text("[\n" + ",\n".join(json.dumps(m, ensure_ascii=False) for m in models) + "\n]\n", encoding="utf-8")
    print(f"{len(models)} 個機種 -> {dst}")
