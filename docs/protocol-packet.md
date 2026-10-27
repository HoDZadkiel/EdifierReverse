# 封包格式（來源：ConneX 1.0.31 靜態分析）

狀態：**以下全部來自反編譯碼，尚未用實機或抓包驗證**。標「推測」的是我的推論，其餘是程式碼直接寫出的行為。

出處（本機 `C:\Work\edifier\out\sources\com\edifier\lib_connect\`）：
- `model/CommandBean.java`：組包、解包
- `manager/CommandManager.java`：`addCRC`、`checkCRC`、`checkDataLength`、`encryptionPayload`
- `constant/CommandIndex.java`：指令碼（已轉成 `src/edifier_reverse/commands.py`）
- `connect/ble/BLEManager.java:186-195`：從廣播取得版本與加密設定

## 版本由裝置決定

兩個參數都從 **BLE 廣播的 Manufacturer Specific Data** 取得（廠商 ID = `2016` = `0x07E0`）：

| 位置 | 意義 |
|------|------|
| 資料前 6 bytes | **A2DP（經典藍牙）MAC**，依序排列（官方 App 用它對應已連線的 A2DP 裝置，`BLEManager.java:143-162,182`） |
| 資料最後 1 byte | `encryption` |
| 資料倒數第 2 byte | `bleVersion` |

（「資料」指不含 2 byte 廠商 ID 之後的內容，與 bleak 的 `manufacturer_data[0x07E0]` 相同。）

- **走 SPP 的裝置也一樣靠這則 BLE 廣播取得版本與加密設定**——SPP 本身沒有廣播資料。
- 官方 App 只在「A2DP 已連線」時才處理這則廣播（`ConnectManager.getCorrectDevice`），所以裝置要先與電腦連上 A2DP。
- 廠商 ID 不符時：`encryption = 0`，`bleVersion` 取預設值。
- `CommandManager` 預設 `bleVersion = 2`。
- USB 路徑固定 `bleVersion = 2`、`encryption = 0`；指令 101 的回應 payload 也含這兩個值（`CommandParser.java:103-107`）。

## 送出封包（App → 裝置）

header 固定 `0xAA`（`CommandBean` 建構時 `i2 == 1`）。

### bleVersion ≥ 2（預設）

```
+------+---------+-------+---------+---------+---------------+------+
| 0xAA | appCode | cmd   | len_hi  | len_lo  | payload       | crc  |
+------+---------+-------+---------+---------+---------------+------+
  1B      1B       1B      1B        1B        len bytes       1B
```

- `appCode = 0xEC`（236，`ECCommand.CommandAppCode.EC_CODE`）；另有 `EC_CODE_ED = 0xED`（237），用途未查。
- `len` = payload 位元組數（不含 cmd），16-bit big-endian。
- payload 先經 `encryptionPayload` 處理再放入。
- `crc` = 前面所有位元組（header 到 payload 末）的總和 `& 0xFF`。
- 總長 = `len + 6`（`checkDataLength`）。

### bleVersion ≤ 1

```
+------+-----------+-------+---------------+---------+---------+
| 0xAA | len       | cmd   | payload       | crc_hi  | crc_lo  |
+------+-----------+-------+---------------+---------+---------+
```

- `len` = payload 長度 + 1（把 cmd 算進去）。
- `crc` = 前面所有位元組總和 **+ 8217（0x2019）**，16-bit big-endian。
- 總長 = `len + 4`。

## 收到封包（裝置 → App）

- `checkCommand` 接受 header `0xBB`（187）或 `0xCC`（204）；送出用的 `0xAA` 不在其中。
- v2 另外檢查 `appCode == 0xEC`。
- 欄位排列與送出相同，CRC 驗證方式與計算方式相同。
- v2 解包時 `length` 只取低 8 bit（`CommandBean.java:53`），但 `checkDataLength` 用完整 16 bit，兩處不一致，是 App 端的小瑕疵，不影響送出。

## 加密

`encryptionPayload` 對 payload 逐位元組處理：

| `encryption` | 行為 |
|--------------|------|
| `0`（Default） | 原樣 |
| `16`（Encryption10） | 每個 byte `XOR 0xA5`（165） |
| 其他 | 原樣 |

- XOR 是對稱的，送出與接收用同一函式。
- CRC 計算在加密之後（送出時），所以 CRC 涵蓋的是加密後的 payload。
- `0xA5` 這個值是反編譯器把常數 `STARTDOWNLOAD_6` 內嵌進來的結果（`TbsListener.java:89`），應與 `EncryptionCode.java` 的原始碼數值一致，實機驗證時再確認。

## 選用哪條傳輸（官方 App 行為）

`BLEManager.switchConnectMethod`：產品表的 `ProductSPPUuid` **非空 → 走 SPP，否則走 BLE GATT**。因此：

- **ES850NB 走 SPP**（產品表有 SPP UUID）。它的 BLE GATT UUID 雖然在產品表裡，但官方 App 不用，是否可用未知。
- **MP330 走 BLE GATT**（沒有 SPP UUID）。

## SPP 傳輸（官方 App 行為）

出處：`connect/spp/SPPManager.java`。已實作於 `src/edifier_reverse/spp.py`。

- 對已連線的 A2DP 裝置，以 UUID `EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF` 做 SDP 查詢後連 RFCOMM（`createRfcommSocketToServiceRecord`）；失敗則改直接連 **channel 1**（`createRfcommSocket(1)`）。
- 收發都是**原始封包位元組串流，沒有額外外框**：寫入就是 `outputStream.write(封包)`；讀取每次最多 1024 位元組，直接交給與 BLE 相同的重組流程。
- 因為是串流，多個請求或回應可能黏在一起、也可能被切開——重組器處理的就是這個。
- Windows 的 Python socket 只能指定 channel 號，所以函式庫對「用 UUID 查詢」改走 Winsock 的 `SOCKADDR_BTH`：結構裡的 `serviceClassId` 填 UUID、`port` 填 0，由系統對該 UUID 做 SDP 查詢後連線（效果等同 Android 的 `createRfcommSocketToServiceRecord`）。用 ctypes 直接呼叫 `ws2_32.connect`，連上後 socket 物件照常使用，不需要 WinRT 或額外相依。
- 連線順序與官方相同：**先 UUID 查詢，失敗再直連 channel 1**；也可用 `--channel N` 只直連指定的 channel。
- `port=0` 時系統會自動做 SDP 查詢，依據的是對微軟文件的理解，**尚未實機驗證**。若實機證明這條不通，備案是 WinRT 的 `RfcommDeviceService` / `StreamSocket`（需新增相依並換一種 socket 型別）。

## BLE 傳輸與重組（官方 App 行為）

出處：`connect/ble/BLEManager.java`、`manager/ConnectManager.java`（`dataCheck`、`subPackageOfNew`、`subPackageOfOld`）。已實作於 `src/edifier_reverse/reassembly.py`。

**送出**
- 整包封包一次寫入 Write 特徵值，**App 端沒有依 MTU 切片**（`sendData2BLE`）。
- 寫入型態：特徵值支援 write-without-response（屬性 0x04）就用它，否則用 write（0x08）。
- App 連線後請求 MTU 300（型號 QR65 為 512）。

**接收**（Read 特徵值的 notify，每筆通知交給 `appReceiveDataListener`）
1. 通知開頭是 `0xBB` 或 `0xCC` → 視為新封包，**覆蓋**緩衝區；否則附加到緩衝區。
2. 若單筆通知長度超過其長度欄位算出的封包長度 → 視為「一筆夾帶多包」，依序切開，每包驗 CRC。
3. 其餘情況：緩衝區長度與 CRC 都正確才算一包，並清空緩衝區；不正確就繼續等後續分片（沒有逾時機制）。

**已知弱點**：後續分片若恰好以 `0xBB`/`0xCC` 開頭，會被誤認為新封包。函式庫沿用此行為，測試中已固定為「已知限制」。

與官方的差異：函式庫遇到 CRC 錯誤的封包會直接丟棄並繼續處理後面的資料；官方則保留快取等下一個 header。

**回應比對**：官方以回應的 `cmd` 從待回應佇列移除對應請求（`CommandFIFOManager.removeCommandByCommandIndex`），所以回應的 cmd 與請求相同。另有重送機制（`ResendLooperImpl`），函式庫尚未實作。

## 已看到的指令例子

指令碼完整表見 `src/edifier_reverse/commands.py`（179 個，其中數值 184、245、246 各有兩個名稱重複）。

| 動作 | cmd | payload | 備註 |
|------|-----|---------|------|
| 查詢 ANC | `0xCC`（204） | 空 | `getANC()` |
| 設定 ANC | `0xC1`（193） | v2：`[ancIndex, ancValue]`，進度>0 時再加 `[ancProgress]`；v1：`[ancValue]`，進度>0 時再加 `[ancProgress]` | `setANC()`；各值意義未查 |
| 查詢 EQ | `0xD5`（213） | 空 | `getEq()` |
| 設定 EQ | `0xC4`（196） | `[rr] + EqCalibrationBean.toCmdByteArray()` | 結構未解 |
| 查詢電量 | `0xD0`（208） | — | 未讀實作 |
| 查詢版本 | `0xC6`（198） | — | 未讀實作 |
| 查詢功能 | `0xD8`（216） | — | `D8Bean` 很大，是功能旗標表 |

## 未解

- `ancIndex` / `ancValue` 的值意義（降噪、通透、關閉各是多少）
- EQ 的 `rr` 與 `EqCalibrationBean` 欄位順序
- 回應封包 payload 的格式（`CommandParser.java`，約 87 KB）
- 重送機制的條件與次數（`ResendLooperImpl`）
- 連線後 App 首先送哪些指令（握手順序）：只確認 `0xD8` 的回應會被特別處理（`ConnectManager.java:557`），其餘未查
- 真實裝置是否會把回應分片、分片大小（需實機觀察）
- ES850NB 與 MP330 實際回報的 `bleVersion` / `encryption`（需實機看廣播）
- OTA 相關指令（`OTA_READY` 等）：**不要碰**，見 `CLAUDE.md` 禁止事項
