# 研究結論總整理

最後更新：2026-10-27（加入 ES850NB 實機驗證）。本文件彙整截至目前所有分析結論，細節請看各專題文件（見最後的索引）。

## 一句話現況

已從官方 App（ConneX 1.0.31）還原出 ES850NB 與 MP330 的藍牙連線方式、封包格式、指令碼與主要指令的 payload，並寫成有 224 個單元測試的 Python 函式庫與命令列工具（含 BLE 與 SPP 兩種傳輸層）。**ES850NB 已完成實機驗證**（封包格式、SPP 與 BLE 連線、全部只讀查詢、ANC 五種模式、EQ 預設切換與自訂 EQ 寫入，見第 0 節）；**MP330 尚未實機測試**，其餘結論仍是靜態分析（讀反編譯碼）。

## 可信度怎麼讀

| 標記 | 意義 |
|------|------|
| 已驗證 | 本機實際做過、結果可重現（例如產品表解密後確實是合法 JSON） |
| 程式碼事實 | 反編譯碼直接寫出的行為，但沒有實機證實 |
| 推測 | 由程式碼推論，文件中會標「推測」 |
| 未知 | 目前查不到 |

「已驗證」包含實機驗證：標示「實機」者是在 ES850NB 上實際送出並讀回的結果（2026-10-27）。MP330 沒有任何實機結果。

## 0. ES850NB 實機驗證結果（2026-10-27）

環境：Windows 11、TP-Link Bluetooth USB Adapter；耳機韌體版本 1.0.1（`0xC6`）。逐步紀錄見 `../DEVLOG.md`。

**連線與封包（實機已驗證）**
- 廣播：廠商 ID `0x07E0`，前 6 bytes 確實是 A2DP 位址（與 Windows 配對位址一致），`bleVersion=2`、`encryption=16`。
- SPP：Winsock `port=0` + UUID 查詢**可行**（連線方式為「UUID 查詢」，不需直連 channel 1）；連續 3 次結果逐位元組相同。
- BLE GATT：**ES850NB 也可用**，12 個查詢的回應與 SPP 完全相同。先前「ES850NB 的 BLE 未必可用」不成立；官方 App 走 SPP 只是選路規則。
- 封包 v2 header／長度／CRC／XOR `0xA5` 加密皆正確。
- 12 個只讀查詢中 11 個有回應，僅 `0xF2` 無回應；電量用 `0xD0`（實測 17）。

**只讀查詢的實測值**：名稱 `EDIFIER ES850NB`、MAC `AA:BB:CC:11:22:33`（與廣播位址一致）、ANC `group=16 mode=1`、EQ 預設編號 `0`、LDAC `1`、LHDC `0`、`0x91` 為 `index=12, ldac=1, lhdc=-1`（無 LHDC）。D8 位元組 13 = `0x2A`（EQ 方案編號）；功能旗標含 HiRes、遊戲模式、高清編碼等。

**ANC（實機已驗證）**
- group 16 的五個模式皆可設定且讀回一致：1 深度降噪、2 舒適降噪、3 防風噪、4 可調增益環境音、5 降噪關。
- 設定回應 payload 為 `[group, mode, 06]`；mode 4 的查詢回應多一個尾端 `06`（推測為環境音程度預設值，未驗證）。
- 設定成功後裝置會主動推送一則 `0xCC`（內容同新狀態）。
- 第三個 byte `DB` 實機驗證：mode 4 帶 db=1、3、9、12 皆被接受並讀回（`10 04 <db>`）；不帶時預設 6。非環境音模式不回報 db。
- **兩次設定指令間隔限制**：自上次回應起算 ≤ 1.0 秒，第二次被靜默忽略（無回應、狀態不變）；≥ 1.25 秒才成功。門檻在 1.0–1.25 秒之間。查詢不受影響（設定後 0.3 秒查詢正常）。Client 目前沒有節流或重送。

**EQ（實機已驗證，除另標明）**
- `0xD5` 只回 1 byte（預設編號），沒有桌面校正欄位。`0xC4` 只送 `[rr]`：rr=1–6 全被接受並讀回，**裝置不檢查範圍**，所以預設總數與各編號名稱未知；切換預設不改變自訂 EQ 內容。
- `0x43` 回應 26 bytes：`P[0]`=eqIndex（8）、**`P[1]`=頻段數（4）**、其後 4 頻段×6 bytes。出廠值：100／2000／4000／8000 Hz，gain 6，q 7，filter 0（gain 6 是否為 0 dB 未驗證，推測）。
- `0x44` 單頻段：gain 0、5、7、12 皆被接受並讀回（回應 `01`）；送 -1（`ff`）與 13 也被接受，**裝置不檢查範圍**，負增益的實際意義需聽感確認。
- `0x45` 重置：回應預設頻段資料（同出廠值），**不清除時間戳與名稱**。
- `0x46` 整組寫入與 `0x47` 改名：格式正確，時間戳與 UTF-8 名稱讀回一致。
- **`0x46` 必須帶滿 4 bytes 時間戳**：只送頻段時，裝置讀回殘留／損毀資料（`77 02 03 04 test 49` 加一串 0，長度 58）。函式庫已在送出前擋下（`check_timestamp`）。
- 名稱欄位最多 28 bytes（30、31 bytes 都被截斷為 28）；UTF-8 中文可寫入。`0xC4` 的 rr 到 255 全被接受。
- 副作用：第一次 `0x44` 之後，`0x43` 結尾多了 4 個 `00`（時間戳）。目前 ES850NB 的頻段與出廠相同，但回應是 30 bytes 而非出廠 26 bytes，找不到方法還原。

## 0.1 官方 App 如何判定 Edifier 裝置（2026-10-28，由 APK 產品表重新確認）

官方 App 內建加密的產品表 `assets/products_release.json`（解密腳本 `scripts/decrypt_products.py`），共 103 筆。判定流程：
1. 每個機種有 `ProductSearchUuid`（BLE 搜尋 UUID，102 筆互不重複）當掃描過濾條件，廣播的 service UUID 含它就是該機種；
2. 廠商資料（廠商 ID `0x07E0`）帶經典藍牙位址、`bleVersion`、`encryption`；
3. 傳輸：`ProductSPPUuid` 非空（64 筆，全是同一個 UUID）走 SPP，否則走 BLE GATT；GATT 的 Read／Write 特徵值 102 筆全部相同。

本專案據此產生 `catalog.json`，並用「搜尋 UUID 命中」或「廠商 ID 命中」兩個條件辨識裝置（見 `catalog.py`、`Catalog.cs`）。有兩組機種名稱重複（`D32`、`W800BT Plus`，硬體版本不同、搜尋 UUID 不同），目錄裡加上 UUID 片段區分。實機驗證：附近一堆 BLE 裝置中只辨識出 ES850NB，且機種、位址、版本、加密設定與先前實測一致。

## 1. 素材與工具

| 項目 | 位置／說明 |
|------|------------|
| 官方 APK | `docs/apk/`（`.apkm` 分包，ConneX 1.0.31，已 gitignore） |
| jadx 1.5.6 | `C:\Tools\jadx`（只在這台電腦，可重新下載） |
| 反編譯輸出 | `C:\Work\edifier\out\sources`（29,543 個 java，344 個方法無法還原屬正常；只在這台電腦） |
| 解出的資產 | `C:\Work\edifier\apk_x`、`C:\Work\edifier\products.json`（產品表解密結果，只在這台電腦） |
| Python 環境 | uv；venv 在 `C:\Work\edifier-venv`（刻意放在 Nextcloud 外，避免同步數千個檔案） |

換電腦不需要 jadx 與反編譯輸出——所有需要的結論都已落在 `docs/` 與 `src/`。

## 2. 連線方式

| | ES850NB（耳機） | MP330（喇叭） |
|---|---|---|
| BLE 廣播搜尋 UUID | `00002101-0000-1000-8000-00805f9b34fb` | `00004c01-0000-1000-8000-00805f9b34fb` |
| BLE Service | `48092101-1a48-11e9-ab14-d663bd873d93` | `48094c01-1a48-11e9-ab14-d663bd873d93` |
| BLE Notify（讀） | `48090001-1a48-11e9-ab14-d663bd873d93` | 同左 |
| BLE Write | `48090002-1a48-11e9-ab14-d663bd873d93` | 同左 |
| SPP（RFCOMM） | `EDF00000-EDFE-DFED-FEDF-EDFEDFEDFEDF` | 無 |

（程式碼事實，來源：APK 內加密的產品表，解密腳本 `scripts/decrypt_products.py`。）

- **官方 App 的選路規則：產品表有 SPP UUID 就走 SPP，否則走 BLE GATT**（`BLEManager.switchConnectMethod`）。所以 **ES850NB 實際走 SPP，MP330 走 BLE GATT**。ES850NB 表中的 BLE GATT UUID 官方 App 不用，但實機驗證可用（第 0 節）。（早期版本的文件曾把 BLE 當成兩款的主要路徑，這是錯的，已更正。）
- 兩款共用同一組讀寫特徵值，**推測**共用同一套封包協定。
- 產品表中其他欄位：ES850NB `HeadsetType=2`、`Series=[12]`、`IsFirmwareUpdate=false`；MP330 `HeadsetType=4`、`Series=[18]`、`IsFirmwareUpdate=true`、`LastFirmwareVersion="1.0.2"`。
- 兩款的 USB `ProductId` / `VendorId` 欄位皆為空，App 的 USB 路徑看起來與這兩款無關。
- SPP 傳輸已實作（`spp.py`），連**經典藍牙位址**的 RFCOMM。連線順序與官方相同：**先用 UUID 查詢（Winsock `SOCKADDR_BTH`，`serviceClassId` 填 UUID、`port=0` 讓系統做 SDP），失敗再直連 channel 1**；也可用 `--channel N` 直連指定 channel。UUID 查詢已實機驗證可行（見第 0 節），WinRT 備案不需要。
- SPP 收發沒有額外外框，就是原始封包的位元組串流；與 BLE 共用同一套重組。

## 3. 版本、加密與經典藍牙位址都來自 BLE 廣播

BLE 廣播的 Manufacturer Specific Data（廠商 ID `0x07E0`，資料不含廠商 ID）（程式碼事實，`BLEManager.java:143-189`）：

| 位置 | 意義 |
|------|------|
| 前 6 bytes | A2DP（經典藍牙）MAC，依序排列 |
| 倒數第 2 byte | `bleVersion`（預設 2） |
| 最後 1 byte | `encryption` |

- **走 SPP 的 ES850NB 也要先掃這則 BLE 廣播**：SPP 沒有廣播資料，版本、加密設定和要連的經典藍牙位址都從這裡來。
- 官方 App 只在 A2DP 已連線時處理這則廣播，所以裝置要先與電腦連上 A2DP。
- 實機驗證的第一步就是看兩款裝置各廣播什麼；後面的封包格式依 `bleVersion`／`encryption` 分支。

## 4. 封包格式

詳見 [protocol-packet.md](protocol-packet.md)。

**送出**（header `0xAA`），預設 v2：

```
0xAA | 0xEC | cmd | len_hi | len_lo | payload | crc
```

- `len` = payload 長度（16 位元大端序）；`crc` = 前面所有位元組總和 & 0xFF。
- v1：`0xAA | len(=payload+1) | cmd | payload | crc16`，crc16 = 總和 + 8217（`0x2019`），大端序。
- 加密：`encryption == 16` 時 payload 逐位元組 XOR `0xA5`；CRC 算在加密之後。
- **接收**只接受 header `0xBB` 或 `0xCC`，並檢查 `appCode == 0xEC`（v2）。

**BLE 傳輸**：整包一次寫入（App 不依 MTU 切片）；通知可能分片，也可能一筆夾帶多包，重組規則見 protocol-packet.md，已實作於 `reassembly.py`。

## 5. 指令總覽

179 個指令碼在 `src/edifier_reverse/commands.py`（由 `CommandIndex.java` 自動產生）。已解 payload 的：

| 功能 | 指令 | 摘要 |
|------|------|------|
| 功能旗標 | `0xD8` | 逐位元組／位元描述裝置能力，見 [protocol-d8.md](protocol-d8.md) |
| 高音質編碼 | LDAC `0x48`、LHDC `0x37`、高清編碼 `0x91` | 只讀查詢已加入 probe；**電腦端需要編碼器，標準 Windows 沒有 LDAC**，詳見 [hq-audio-assessment.md](hq-audio-assessment.md) |
| 電量 | `0xD0` | `P[0]` = 電量（有號 byte，**推測**為 0–100） |
| 裝置狀態 | `0xF2` | `P[0]==4` 單體；其餘為 TWS（左右耳＋充電盒） |
| 版本／MAC／名稱 | `0xC6`／`0xC8`／`0xC9` | 十進位以 `.` 連接／hex 以 `:` 連接／UTF-8 |
| ANC | 查 `0xCC`、設 `0xC1` | v2 payload `[group, mode, db?]` |
| EQ（校正） | 查 `0xD5`、設 `0xC4` | `[rr, index, byte0, 低頻截止, 斜率, 聲學空間, 桌面控制]`，看起來是桌面喇叭用 |
| 自訂 EQ | `0x43`–`0x47` | 頻段數與排列依 `eqIndex` 而異（1–17），見 [protocol-commands.md](protocol-commands.md) |

重點觀念：
- **ANC 的 `mode` 不是全域編號**，要依裝置的 `group` 查表；50 組模式表在 [anc-groups.json](anc-groups.json)。兩款裝置各用哪組，只能從實機的 ANC 查詢回應 `P[0]` 得知。
- **D8 位元組 13 是 EQ 方案編號**（如 `0x20`、`0x21`、`0x23`），與自訂 EQ 的 `eqIndex` 是兩個不同概念。
- 負增益的編碼、頻率單位、自訂 EQ 回應的 `P[1]` 意義都還不知道。

## 6. 外部資訊調查

- **官方更新頁**（edifier.com 的 software-update 政策頁）：MP330（型號代碼 EDF100094）支援到 2033-12-31，更新途徑為 ConneX 1.0.21 以上。ES850NB 的支援頁有「Documents & Firmwares」，但沒有公開版本號或更新說明。**兩款都查不到公開的 changelog。**
- **社群**：沒有找到任何 Edifier 專屬的逆向專案、GATT 文件或討論，這個專案很可能是第一手逆向。
- **TempoHub**（官方電腦版，有線專用，韌體升級只能用它）：**暫緩**。你最終要用藍牙，且有線模式會停用藍牙；等 BLE 協定有缺口時再看它是否共用指令格式。若要研究，需自行確認官方下載來源。

## 7. 安全規則

寫在 [CLAUDE.md](../CLAUDE.md)，函式庫層有強制：

- 不送 OTA／DFU 指令（`OTA_READY`、`OTA_START_MASTER`、`OTA_START_SUB`）——`packet.build` 會直接拒絕，有測試。
- 命令列 `probe` 只送白名單內的 12 個查詢指令，不暴露任何設定類指令；有測試確認白名單不含設定、重置、關機、清除配對，且白名單內每個指令的名稱都是 QUERY、沒有任何一個是設定類（含 LDAC／LHDC／高清編碼的設定指令，因為切換高清編碼可能讓裝置重啟）。
- 不 push APK、反編譯碼、探測紀錄（`docs/apk/`、`docs/captures/` 已 gitignore；紀錄含裝置 MAC）。
- 實驗新指令前先說明風險；協定結論只寫已驗證的事實並標明推測。

## 8. 程式碼庫

```
src/edifier_reverse/
  packet.py      組包／解析（v1/v2、XOR、OTA 封鎖）
  reassembly.py  通知／串流重組（分片、夾帶多包），BLE 與 SPP 共用
  requests.py    要送的請求
  responses.py   回應解析（電量、版本、ANC、裝置狀態…）
  eq.py          EQ 校正與自訂 EQ
  d8.py          功能旗標（d8_flags.py 自動產生）
  commands.py    指令碼（自動產生）
  transport.py   傳輸抽象
  client.py      請求／回應（以 cmd 比對、逾時，未做重送）
  probe.py       白名單查詢探測
  ble.py         bleak 掃描與傳輸、廣播解析與型號挑選、兩款裝置的 UUID 與傳輸規則
  spp.py         SPP（RFCOMM）傳輸：socket 包裝、連線
  sim.py         離線模擬裝置（串流語意；只證明自洽，不證明符合真實裝置）
  cli.py         scan / probe / sim / status / anc / tray，含連線規劃（plan_connection）
  service.py     DeviceService：連線、狀態、ANC 設定節流佇列（≥2 秒）與重送、斷線重連
  volume.py      耳機音量（Windows 端點，pycaw；非協定）
  logo.py        托盤與面板的 logo（電量環）
  flyout.py      Windows 11 風格彈出面板（Tkinter 自繪）
  tray.py        托盤小工具（pystray 加面板），選用相依 tray
（C# 版在 src/EdifierTray，見 tool-design.md 第 6 節）
scripts/         解密產品表、產生常數表、抽取 ANC / D8 表
tests/           224 個單元測試（C# 的 55 個在 tests/EdifierTray.Tests）（離線，不需藍牙）
```

測試品質：封包測試的向量是手算的位元組；對 `packet.py`、`reassembly.py`、`spp.py`、`ble.py`、`sim.py` 做過破壞性驗證（改常數、關檢查、顛倒順序），各次都有測試失敗。SPP 傳輸層用真的 socket（`socketpair`）對模擬裝置測過，但沒有碰過真正的藍牙。

## 9. 未解與風險

| 項目 | 風險／影響 |
|------|------------|
| MP330 不在目標內 | 未實機驗證，相關結論維持推測，不再測試 |
| SPP 連線的前提 | 裝置須先與電腦配對並連上 A2DP（ES850NB 實機如此做成功；未配對的情形未測） |
| 設定指令間隔限制 | 已知 ANC 約需 ≥ 1.25 秒；Client 尚未節流或重送，EQ 等其他設定指令是否同一限制未專門測 |
| `0xF2` 無回應 | ES850NB 不回應，原因未明（可能不支援） |
| 自訂 EQ 殘留 4 bytes 時間戳 | ES850NB 的 `0x43` 回應由 26 變 30 bytes，無法還原；官方 App 是否顯示異常未確認 |
| 重組的已知弱點 | 後續分片以 `0xBB`/`0xCC` 開頭會被誤判 |
| 負增益編碼、gain 上限、頻率單位 | 裝置不檢查範圍（-1、13 皆被接受），實際音訊意義未知 |
| EQ 預設編號 | rr 0–6 被接受但名稱未知；D8 方案 `0x2A` 的清單查不到 |
| 握手順序 | 只確認 `0xD8` 回應會被特別處理，其餘未查 |
| Windows 上 BLE 連線行為 | 例如裝置是否被手機占用、需不需要先配對，皆未知 |

## 10. 下一步（依優先序）

1. Client 加入設定指令的最小間隔（≥ 2 秒）或逾時重送。
2. 找出各預設編號名稱與 gain 的實際意義（需聽感或官方 App 對照）；中文名稱在 28 bytes 邊界截斷的行為。
3. 在 Android 開 HCI snoop log 對照官方 App 的實際封包。

## 11. 文件索引

| 檔案 | 內容 |
|------|------|
| [protocol-uuids.md](protocol-uuids.md) | 連線 UUID |
| [protocol-packet.md](protocol-packet.md) | 封包格式、加密、BLE 傳輸與重組 |
| [protocol-commands.md](protocol-commands.md) | 電量、狀態、ANC、EQ、自訂 EQ 的 payload |
| [protocol-d8.md](protocol-d8.md) | `0xD8` 功能旗標位元表 |
| [anc-groups.json](anc-groups.json) | 50 組 ANC 模式表（自動抽出，缺 group 30，少數群組可能有誤） |
| [d8-bits.json](d8-bits.json) | D8 欄位的機器可讀版 |
| [handoff-other-pc.md](handoff-other-pc.md) | 換電腦實機驗證步驟 |
| [hq-audio-assessment.md](hq-audio-assessment.md) | 高音質（LDAC／LHDC）評估：電腦端需要編碼器，標準 Windows 沒有 LDAC |
| [tool-design.md](tool-design.md) | ES850NB 托盤小工具設計方案：範圍、架構（DeviceService 加節流佇列）、階段與風險 |
| [ldac-and-tool-plan.md](ldac-and-tool-plan.md) | 三章：電腦端取得 LDAC 的可行路線（外接發射器、Alternative A2DP Driver、自製、Linux）；最終工具方案（推薦沿用 Python 加托盤 GUI，分階段）；專案完成後 Windows 使用者的效益與限制 |
| `../DEVLOG.md` | 逐步開發紀錄 |
