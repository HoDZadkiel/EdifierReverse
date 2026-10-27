# 連線 UUID（來源：ConneX 1.0.31 靜態分析）

狀態：**推測，尚未用實機驗證**。來源為 APK 內加密的 `assets/products_release.json`，以 `scripts/decrypt_products.py` 解密。

## ES850NB（耳機）

| 項目 | 值 |
|------|----|
| BLE 廣播搜尋 UUID | `00002101-0000-1000-8000-00805f9b34fb` |
| BLE Service | `48092101-1a48-11e9-ab14-d663bd873d93` |
| BLE Read（notify） | `48090001-1a48-11e9-ab14-d663bd873d93` |
| BLE Write | `48090002-1a48-11e9-ab14-d663bd873d93` |
| SPP（RFCOMM） | `EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF` |

## MP330（喇叭）

| 項目 | 值 |
|------|----|
| BLE 廣播搜尋 UUID | `00004c01-0000-1000-8000-00805f9b34fb` |
| BLE Service | `48094c01-1a48-11e9-ab14-d663bd873d93` |
| BLE Read（notify） | `48090001-1a48-11e9-ab14-d663bd873d93` |
| BLE Write | `48090002-1a48-11e9-ab14-d663bd873d93` |
| SPP | 無（產品表為空） |

## 觀察

- 兩款的 Read/Write 特徵值 UUID 相同，只有 Service UUID 不同 → 疑似共用同一套封包協定。
- 只有 ES850NB 有 SPP 通道，MP330 只走 BLE。
- **官方 App 的選路規則是「有 SPP UUID 就走 SPP」，所以 ES850NB 實際走 SPP，MP330 走 BLE GATT**（見 `protocol-packet.md`）。ES850NB 表中的 BLE GATT UUID 官方 App 不用，是否可用未知。

## 程式出處（反編譯碼，位於本機 `C:\Work\edifier\out`）

- `com/edifier/lib_connect/connect/ble/BLEManager.java`、`BoxBleManager.java`：BLE 連線與 GATT
- `com/edifier/lib_connect/connect/spp/SPPManager.java`：SPP 連線
- `com/edifier/lib_common/utils/CipherUtil.java`：產品表解密

## 待辦

- 找封包格式（header、長度、checksum、指令碼）
- 用 bleak 實機驗證 Service/Characteristic 是否存在
