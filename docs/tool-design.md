# ES850NB 小工具設計方案

目標：把手機 ConneX 上最常用的控制搬到 Windows 托盤——看電量、切 ANC。沿用 [ldac-and-tool-plan.md](ldac-and-tool-plan.md) 的結論（Python、分階段），其前提「Winsock UUID 查詢是否可行」已由實機驗證成立（見 [findings.md](findings.md) 第 0 節），不需改走 C#。

## 1. 範圍

| 功能 | 納入 | 依據（實機已驗證） |
|------|------|--------------------|
| 電量顯示 | 第 1 階段 | `0xD0` 回 17，電腦顯示 20%，大致吻合 |
| ANC 狀態與切換（5 模式、環境音 DB） | 第 1 階段 | 五種模式與 DB 皆讀回一致；裝置會主動推送變更 |
| 連線狀態、裝置名稱、韌體版本 | 第 1 階段 | `0xC9`、`0xC6` |
| 編碼狀態（LDAC 等） | 唯讀顯示，第 2 階段 | `0x48`／`0x91` |
| EQ | **不納入** | 預設編號名稱與 gain 的實際意義未知，且自訂 EQ 已有無法還原的副作用 |
| 音量 | 第 1 階段（走 Windows，不走協定） | 協定查詢 `0x66`、`0xC5` 無回應、D8 音量位元未開；改調 Windows 的「耳機 (EDIFIER ES850NB)」播放端點（pycaw），不動預設裝置與其他裝置 |
| 韌體更新（OTA／DFU） | 永久排除 | 函式庫層已強制 |

## 2. 架構

```
tray.py（pystray，執行緒 A）  <->  DeviceService（asyncio，執行緒 B）  <->  Client / SPP（現有函式庫）
cli.py 的 status / anc 子命令  ----^（同一個 DeviceService，不碰 GUI）
```

新增模組（皆在 `src/edifier_reverse/`）：

- `service.py`：`DeviceService`，不含任何 GUI，可用 `sim` 離線測試。
  - 連線：掃描廣播取位址與 Session，走 SPP；失敗退回 BLE（實機驗證兩者皆可）。
  - 狀態：`connected`、`battery`、`anc(group, mode, db)`、`name`、`version`；變更時呼叫回呼。
  - ANC 狀態以裝置推送的 `0xCC` 為準，開機與重連時主動查一次。
  - 電量：連線後查一次，之後每 60 秒輪詢（官方推送機制未驗證，先用輪詢）。
  - **設定指令佇列與節流**：所有設定指令經單一佇列，兩次之間至少 2.0 秒（實機：請求到請求間隔 1.4 秒會每隔一次被靜默忽略，1.6 秒以上 18 次全成功；從回應算起留餘裕取 2.0 秒）；送出後若 3 秒沒收到回應或推送，重送一次，再失敗就回報錯誤。查詢不受節流。
  - 斷線偵測與自動重連（指數退避，上限 30 秒）。
- `tray.py`：托盤圖示，圖示用電量數字或顏色；選單為「ANC 模式（單選）」「環境音強度（僅 mode 4）」「重新連線」「結束」。滑鼠停留顯示名稱、電量、韌體版本。
- `cli.py` 新增 `status`（印一次狀態）、`anc <模式>`（切換後等推送確認，印結果）。

相依：`pystray`、`Pillow` 放 optional extra（`uv sync --extra tray`），核心函式庫維持只依賴 bleak。打包用 PyInstaller（第 3 階段）。

## 3. 設計決定

| 決定 | 理由 |
|------|------|
| ANC 切換不再跳確認視窗 | 點選單本身就是明確操作，且可立即切回；舊方案的「一律確認」只對不可逆指令保留（目前工具沒有這類指令） |
| 只做模式表 group 16 | 其他群組只有靜態分析；未知 group 時 ANC 選單停用、只顯示狀態 |
| 設定檔放 `%APPDATA%\edifier-reverse\config.json` | 內含裝置位址（MAC），不放專案目錄，避免被 push |
| 工具不碰電腦端藍牙音訊驅動 | 不改善音質，LDAC 另案 |
| 不使用未驗證的指令 | 工具能送的指令是固定清單：查詢類、`0xC1` |

## 4. 階段

> **實作狀態（2026-10-27）**：第 1、2 階段已實作並在實機驗證（`service.py`、`volume.py`、`tray.py`、`status`／`anc`／`tray` 子命令）；第 3、4 階段未做。

1. **服務層與 CLI**：`service.py`（連線、狀態、節流佇列、重連）、`status`／`anc` 子命令。驗收：離線測試涵蓋節流與重送（用 `sim` 模擬「間隔太短被忽略」）；實機 `anc` 切換五種模式各成功一次、連續呼叫不再逾時。
2. **托盤**：`tray.py`，電量與 ANC 選單、重連。驗收：實機關閉再開啟耳機，托盤自動恢復；用手機或耳機按鍵切 ANC，托盤同步更新。
3. **打包**：PyInstaller 單檔 exe，開機自動啟動選項。驗收：乾淨環境雙擊可用，防毒不誤判。
4. **選配**：全域快捷鍵切 ANC（已於 1.0.4 完成，含音量與靜音）、低電量通知（已於 1.0.4 完成，以 OSD 顯示）、編碼狀態顯示（未做）。

## 5. 待實機確認的風險

| 風險 | 處理 |
|------|------|
| 常駐 SPP 連線是否影響 Windows 音訊或其他藍牙行為 | 第 1 階段實機長時間（數小時）連線測試 |
| 手機 ConneX 同時連線時是否衝突（多點） | 未測；先假設一次一個控制端，衝突時顯示錯誤 |
| 耳機休眠或離線後，電腦能否自動重連 | 第 2 階段驗收項目 |
| 電量輪詢頻率對耳機耗電的影響 | 先 60 秒，必要時拉長 |
| 裝置推送電量／ANC 以外的事件 | `on_event` 目前只收到 `0xCC`；其他事件出現時先記錄不處理 |
| 韌體更新後協定改變 | 工具啟動時讀 `0xC6` 版本；非 1.0.1 時在托盤提示「未驗證版本」 |

## 6. 兩個實作版本（2026-10-27）

同一份設計做了兩個版本，功能相同：電量與 ANC 狀態、5 種降噪模式、環境音強度拖曳條、音量拖曳條與靜音、托盤圖示（電量環 logo，連線時中央顯示電量數字）、電量過低與降噪模式改變的 OSD 提示、全域快捷鍵（Ctrl+Alt+A 輪替降噪、Ctrl+Alt+↑／↓ 音量、Ctrl+Alt+0 靜音；Python 與 C# 的鍵表與測試向量一致）、Windows 11 風格彈出面板（圓角、深淺色跟隨系統）。兩版都只送查詢與 `0xC1`，設定指令經節流（≥ 2 秒）與重送，只開放實機驗證過的 group 16。

| | A. Python（`edifier-reverse tray`） | B. C#／WPF（`src/EdifierTray`） |
|---|---|---|
| 介面 | Tkinter 自繪（磚塊、拖曳條用 Canvas），DWM 圓角與深淺色 | WPF 自訂 Fluent 樣式，DWM 圓角、Acrylic 背景材質、系統強調色、淡入滑入動畫 |
| 協定與傳輸 | 沿用 `src/edifier_reverse`（Winsock SOCKADDR_BTH） | 移植成 C#（`Protocol/`、`Transport/`），同樣用 Winsock SOCKADDR_BTH，不依賴 WinRT 的 RFCOMM |
| 位址探索 | bleak 掃描廣播，結果快取到 `%APPDATA%\edifier-reverse` | WinRT 廣播監聽，快取到 `%APPDATA%\EdifierTray` |
| 音量 | pycaw | NAudio（兩者都調「耳機 (EDIFIER ES850NB)」播放端點） |
| 測試 | 224 個 pytest（含協定、服務、面板、音量） | 71 個 xUnit（含選單內容、位置、開機啟動、視窗行為；協定向量與 Python 版相同，另含實機抓到的 `aa ec c1 00 02 b5 a7 b5` 逐位元組比對） |
| 執行需求 | Python 環境（uv）；尚未打包 | .NET 8 桌面執行環境（本機已有；他機需安裝）；單檔發佈 25 MB |

**實測數字（這台電腦，ES850NB，2026-10-27）**

| | Python | C# |
|---|---|---|
| 啟動到取得狀態（有快取） | 0.46 秒（第一次需掃描約 8.9 秒） | 1.03 秒 |
| 托盤常駐記憶體（工作集） | 約 78 MB（3 個行程，含 uv 啟動器） | 約 102 MB（私有記憶體 42 MB） |

結論（依實測，與先前估計不同）：**C# 版並沒有比較省記憶體**——WPF 的工作集比 Python 加 Tkinter 還大；先前我估 15–25 MB 是錯的。兩版的啟動速度差距主要來自「有沒有快取位址」，不是語言（Python 版補上快取後也在 0.5 秒內）。C# 的實際好處是介面質感（Acrylic、動畫、字型渲染）、單一 exe 發佈、日後簽章與安裝包較成熟；代價是協定要維護兩份（以兩邊共用的測試向量守住一致）。

**已驗證**：兩版都在實機上完成連線、五種模式切換、環境音強度、音量拖曳（Windows 讀回一致）並還原；面板截圖確認深色與淺色外觀。**未驗證**：真人點擊托盤與選單的手感、Acrylic 在不同主題與多螢幕下的表現、長時間常駐、耳機休眠後重連、手機 ConneX 同時連線時的衝突、高 DPI 縮放以外的情境。

建置與執行（C#）：

```powershell
$env:EDIFIER_BUILD_ROOT = "C:\Work\edifier-tray-build"   # bin/obj 放專案目錄外（選用）
dotnet build src/EdifierTray -c Release
dotnet test tests/EdifierTray.Tests -c Release
# 單檔發佈：dotnet publish src/EdifierTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o <輸出資料夾>
```

**右鍵選單（C# 版，2026-10-28）**：不再用 WinForms 的傳統選單，改成與彈出面板同風格的 WPF 選單（圓角、Acrylic、圖示、選中指示條）。內容：標頭（logo、名稱、電量與目前降噪）、降噪模式 5 項（目前模式有色條）、靜音／取消靜音（右側顯示音量）、音量 +10%／-10%、開啟面板、重新連線、開機自動啟動（打勾；寫入目前使用者的 Run 機碼，只在使用者點選時才改）、結束。未連線時降噪與音量項目停用；選單位置貼著游標並夾在工作區內（工作列在上或下都能放）。選單內容由 `TrayMenuModel` 產生（純資料，有單元測試），`TrayMenuWindow` 負責畫出來。Python 版仍是只有「開啟面板、重新連線、結束」的簡單選單。

**外觀、語言、多裝置（2026-10-28，兩版皆有）**
- **外觀**：右鍵選單的「日間／夜間／跟隨系統」，即時切換，存進設定檔。
- **語言**：繁體中文、简体中文、English、日本語。第一次執行依 Windows 介面語言決定；字串表是兩版共用的 `src/edifier_reverse/strings.json`（C# 嵌入同一份檔案），有測試守住「四種語言鍵相同、`{參數}` 一致」。字體依語言挑 Windows 11 的介面字體（微軟正黑體 UI／微軟雅黑 UI／Yu Gothic UI，英數用 Segoe UI Variable），避免中文落到有襯線的細明體。
- **裝置辨識**：依官方 App 的判定方式，從官方產品表產生機種目錄 `src/edifier_reverse/catalog.json`（`scripts/gen_catalog.py`，102 個機種）：廣播的 service UUID 含某機種的搜尋 UUID 就是該機種；廠商 ID `0x07E0` 的廣播即使機種不在目錄也算 Edifier 裝置（通用機種）。`edifier-reverse devices`／`EdifierTray --cli devices` 列出附近所有被辨識的裝置（實測：附近的 Sony 耳機等被正確忽略，只列出 ES850NB）。
- **裝置選擇**：選單的「裝置」區列出已知裝置（連過的顯示裝置名稱，附位址後四碼區分同型號多台）與「重新掃描裝置」；選一台就重新連線，選擇存進設定檔。一次只連一台。音量調的是「名稱與目前連線裝置相符」的播放端點。
- **其他機種**：只讀查詢（電量、名稱、版本、ANC）逐項容錯，機種不支援的項目不會被當成連線失敗；ANC 設定仍只開放實機驗證過的 group 16。**除 ES850NB 外的機種都沒有實機驗證**。C# 版目前只支援有 SPP 通道的機種（BLE-only 機種會顯示提示並請改用 Python 版）。
- 設定檔：Python 在 `%APPDATA%\edifier-reverse\settings.json`，C# 在 `%APPDATA%\EdifierTray\settings.json`（含裝置位址，不在專案目錄；兩版各自獨立）。

驗證用旗標（C# 版）：`--cli status`、`--cli anc N [強度]`、`--demo-shot 路徑 [dark|light]`、`--live-ui 目錄`（連真的耳機、程式化操作元件、截圖並還原）、`--cold-toggle 路徑`（重現第一次點圖示）、`--menu-shot 路徑 [dark|light] [ok|off|muted]`（渲染選單）、`--live-menu`（連真的耳機，從選單「點」各項目並還原）。
