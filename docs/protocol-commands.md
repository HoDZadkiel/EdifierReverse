# 指令 payload 與回應格式（來源：ConneX 1.0.31 靜態分析）

狀態：**全部來自反編譯碼，尚未實機驗證**。封包外層結構見 `protocol-packet.md`，指令碼見 `src/edifier_reverse/commands.py`。

出處（`C:\Work\edifier\out\sources\com\edifier\`）：
- `lib_connect/parser/CommandParser.java`：回應解析
- `lib_connect/manager/CommandManager.java`：送出指令組裝
- `lib_edifier_device/manager/CommandPayloadManager.java`、`lib_edifier_device/config/ProtocolConfigurations.java`：新一層、設定驅動的 ANC 等對照

回應的 `cmd` 與查詢/設定的指令碼相同（例如送 `0xD0`，回應 cmd 也是 `0xD0`）。以下 `P[n]` 指 payload 第 n 個位元組（已解 XOR）。

## 電量與狀態

| 項目 | 送出 | 回應 payload |
|------|------|--------------|
| 電量 | `0xD0` 空 payload | `P[0]` = 電量（當有號 byte 讀，推測 0–100 百分比） |
| 裝置狀態 | `0xF2`（242） | 見下 |
| 版本 | `0xC6`（198） | 每個 byte 轉十進位用 `.` 連接，例如 `1.0.2` |
| MAC | `0xC8`（200） | 每個 byte 轉 hex 用 `:` 連接 |
| 名稱 | `0xC9`（201） | UTF-8 字串 |
| 功能旗標 | `0xD8`（216） | 整包交給 `D8Bean`，是各功能支援與否的位元表（未解） |

裝置狀態（`0xF2`），`P[0]` 決定型態：
- `P[0] == 4`（單體，如喇叭／頭戴）：`P[3]` = 電量，`P[4] == 1` 表示充電中。
- 其他（TWS 型）：`P[1]` 左耳、`P[2]` 右耳、`P[3]` 充電盒電量，`P[4] == 1` 充電中。
- 長度不足會回 null，所以務必先檢查長度。

**ES850NB 實機驗證**：`0xD0` 有回應（`P[0]`=17），`0xF2` 無回應，電量用 `0xD0`。MP330 尚未測。

## ANC

查詢 `0xCC`（204）空 payload。設定 `0xC1`（193）。

bleVersion = 2 的回應 / 設定 payload：

| 位置 | 欄位 | 說明 |
|------|------|------|
| `P[0]` | `groupIndex` | 這台裝置採用哪一組 ANC 模式表 |
| `P[1]` | `modeIndex` | 該群組內的模式編號（從 1 起） |
| `P[2]` | `DB` | 可調增益環境音的程度，選填；送出時進度 > 0 才附帶 |

bleVersion ≤ 1：payload 沒有 `groupIndex`，只有 `[modeIndex, (DB)]`，群組改由 D8 功能旗標決定。

`groupIndex` → 模式名稱的對照表在 `docs/anc-groups.json`（50 組，由腳本 `scripts/extract_anc_groups.py` 自動抽出）。例：

| group | 1 | 2 | 3 | 4 | 5 |
|-------|---|---|---|---|---|
| 5 | 降噪關 | 深度降噪 | 舒適降噪 | 可調增益環境音 | |
| 18 | 深度降噪 | 舒適降噪 | 可調增益環境音 | 降噪關 | |
| 25 | 深度 | 舒適 | 自適應 | 防風噪 | 可調增益環境音（6=降噪關） |

注意：
- **ES850NB 與 MP330 各是哪一組，必須從實機的 ANC 查詢回應 `P[0]` 讀出**，不能從 APK 推。ES850NB 實測為 **group 16**（`10 01`），模式表為 1 深度降噪、2 舒適降噪、3 防風噪、4 可調增益環境音、5 降噪關；MP330 未測。
- 腳本把 `case` 標籤對到後面第一個列表，缺 group 30，且少數群組可能對錯，使用前請對照原始碼。
- 設定時 `modeIndex` 是「該群組表中的編號」，不是模式的全域編號。

**ES850NB 實機驗證（2026-10-27）**：
- 五個模式皆可設定，`0xCC` 讀回一致；設定回應 payload 為 `[group, mode, 06]`（尾端 `06` 意義未解，推測為 DB 預設值）。mode 4 的 `0xCC` 查詢回應為 `10 04 06`，其他模式為 2 bytes。
- 設定成功後裝置主動推送一則 `0xCC`（無對應請求）。
- **設定指令間隔限制**：兩次 `0xC1` 間隔（自上次回應起算）≤ 1.0 秒時，第二次被靜默忽略；≥ 1.25 秒成功。查詢不受影響。
- **`DB` 參數（第三個 byte）已驗證**：mode 4 帶 db=1、3、9、12 皆被接受，回應與 `0xCC` 查詢都是 `10 04 <db>`（db=12 回 `0c`）；範圍上限未測。mode 2 帶 db=5 回應 `10 02 05`，但之後 `0xCC` 查詢只回 `10 02`（非環境音模式不回報 db）。不帶 db 時回應尾端為 `06`，即預設 DB=6。

## EQ

查詢 `0xD5`（213）空 payload。設定 `0xC4`（196）。

| 位置 | 欄位 |
|------|------|
| `P[0]` | `rr`：EQ 預設編號 |
| `P[1]` | `index` |
| `P[2]` | `byte0`（意義不明） |
| `P[3]` | `lowCutoffFreq` 低頻截止頻率 |
| `P[4]` | `lowCutoffSlope` 低頻截止斜率 |
| `P[5]` | `acousticSpace` 聲學空間 |
| `P[6]` | `desktopControl` 桌面控制 |

- 回應長度 6 時沒有 `byte0`，欄位依序往前一格（`P[2]` 起為 lowCutoffFreq）。
- 設定 payload 為 `[rr, index, byte0, lowCutoffFreq, lowCutoffSlope, acousticSpace, desktopControl]`，順序與回應一致。
- 參數範圍常數：`lowCutoffFreq` 20–100 步進 5，`lowCutoffSlope` 6–24 步進 6（`EqCalibrationBean` 預設值）。
- 這組欄位看起來是**桌面喇叭的校正**。ES850NB 實機驗證：`0xD5` 只回 1 byte（預設編號，出廠 `00`），沒有校正欄位；`0xC4` 只送 `[rr]`，rr=1–6 皆被接受並讀回（裝置不檢查範圍，名稱未知），不影響 `0x43` 的自訂 EQ。耳機的頻段調整走自訂 EQ（`0x43`–`0x47`）。

## 自訂 EQ（`0x43`–`0x47`）

| 指令 | 十六進位 | 用途 |
|------|----------|------|
| `CUSTOM_EQ_QUERY` | `0x43` | 查詢目前自訂 EQ（空 payload） |
| `CUSTOM_EQ_SET` | `0x44` | 設定單一頻段 |
| `CUSTOM_EQ_RESET` | `0x45` | 重置（空 payload） |
| `CUSTOM_EQ_SWITCH` | `0x46` | 一次寫入整組頻段＋時間戳＋名稱（CommandManager 內叫 `changeCustomEq`） |
| `CUSTOM_EQ_NAME_SET` | `0x47` | 只改名稱 |

**EQ 型態 `eqIndex`** 決定頻段數與每頻段的位元組排列。v2 的 `0x43` 回應 `P[0]` 就是 `eqIndex`，頻段資料從 `P[2]` 開始（`P[1]` 是頻段數，ES850NB 實機驗證：eqIndex=8、`P[1]`=4）。v1 固定視為 `eqIndex = 1`，資料從 `P[0]` 開始。

| `eqIndex` | 頻段數 | 頻段資料總長 | 每頻段長度 | 每頻段排列 |
|-----------|--------|--------------|------------|------------|
| 1、4、5、6、8 及其他 | 4 | 24 | 6 | `[band, filter, freq_hi, freq_lo, gain, q]` |
| 2 | 6 | 24 | 4 | `[band, freq_hi, freq_lo, gain]` |
| 3 | 6 | 36 | 6 | 同 1（6 bytes） |
| 7 | 6 | 25 | 4 | 先 1 byte `byte0`，之後每頻段 `[band, freq_hi, freq_lo, gain]` |
| 12 | 9 | 37 | 4 | 同 7（先 `byte0`） |
| 16 | 9 | 36 | 4 | `[band, freq_hi, freq_lo, gain]` |
| 17 | 10 | 40 | 4 | 同 16 |

- `freq` 為 16 位元大端序（單位未查，推測 Hz）；`gain`、`q` 各 1 byte，讀取時當無號數處理，**負增益如何編碼未知**（可能是補數，也可能有偏移）。
- `band` 是頻段序號，從 0 開始。

**ES850NB 實機驗證（2026-10-27）**：
- 出廠回應 26 bytes（無時間戳與名稱）：100／2000／4000／8000 Hz、gain 6、q 7、filter 0。
- `0x44`：回應 `01`；gain 0、5、7、12 皆被接受並讀回。第一次 `0x44` 後，`0x43` 回應結尾多 4 bytes 時間戳（`00 00 00 00`），之後無法還原。
- `0x45`：回應為預設頻段資料（24 bytes），**不清除時間戳與名稱**。
- `0x46`：回應回傳整份 payload，讀回一致。**時間戳必須剛好 4 bytes**；只送頻段時裝置留下殘留／損毀資料。
- `0x47`：回應 `01`，時間戳與名稱讀回一致。
- **gain 編碼**：gain=-1 送出 `ff`、gain=13 送出 `0d`，兩者都被接受並如實讀回（裝置不檢查範圍）；`-1 -> 0xFF` 是函式庫的 `& 0xFF` 遮罩，**裝置如何解讀（補數或偏移）仍需聽感確認**，gain 6 是否為 0 dB 同樣未驗證。
- **名稱**：UTF-8 中文可寫入（`測試EQ` 8 bytes、`測試改名` 皆讀回一致），`0x46` 與 `0x47` 都可。名稱欄位**最多 28 bytes**：送 30 或 31 bytes 都只讀回前 28 bytes，被靜默截斷（D8 的 name_limit=30 與此不符）。中文以 3 bytes 計，28 bytes 可能在字元中間截斷（未測）。0x43 回應長度最大 58 bytes（26 + 4 時間戳 + 28 名稱）。
- `0xC4` 的 rr：7、8、10、16、255 全被接受並讀回，裝置完全不驗證範圍。

頻段資料之後（如果回應比「頻段資料總長」多 4 byte 以上）：
- 前 4 byte：時間戳（回應解析成 hex 字串；送出名稱時用目前時間，位元組順序是反過來的）
- 其餘：UTF-8 的自訂名稱

各指令的 payload：
- **`0x44` 單頻段設定**：依 `eqIndex` 取表中「每頻段排列」，`eqIndex = 7/12` 要在前面多放 `byte0`；`q` 沒給時填 0。
- **`0x46` 整組寫入**：`[頻段資料（總長如上表，7/12 型含 byte0）] + [4 byte 時間戳] + [名稱 UTF-8]`。**不含** `eqIndex` 前綴。
- **`0x47` 改名**：`[4 byte 時間戳] + [名稱 UTF-8]`。
- **`0x45` 重置**、**`0x43` 查詢**：空 payload。

## D8 功能旗標（`0xD8`）

見 `protocol-d8.md`。`D8` 位元組 13 是 EQ **方案編號**（如 `0x20`、`0x21`、`0x23`），與這裡的 `eqIndex`（頻段排列型態）是不同的東西。

## 其他簡單指令（回應皆讀 `P[0]`，`1` 代表開）

| 功能 | 指令 | 備註 |
|------|------|------|
| LED | `0xF7`/`0xF8`（247/248） | `P[0]==1` 開 |
| 降噪模式 | `0x7F`/`0x80`（127/128） | `P[0]==1` 開 |
| 風噪 | `0x5C`/`0x5D`（92/93） | `P[0]==1` 開 |
| LDAC | `0x48`/`0x49`（72/73） | `P[0]` 為設定值（缺少時官方視為 -1）；數值意義未解 |
| LHDC | `0x37`/`0x38`（55/56） | 回應長度 2 時取 `P[1]`，否則取 `P[0]`；設定時另有 `[2, 值]` 變體 |
| 高清編碼 | `0x91`/`0x92`（145/146） | `[index, ldac, lhdc]`；長度 2 且 index 為 7、10、13 時 `P[1]` 是 LHDC、沒有 LDAC；-1 表示裝置沒有該選項。**切換可能讓裝置重啟**，見 [hq-audio-assessment.md](hq-audio-assessment.md) |
| 設定結果 | 各 SET 指令 | `P[index] == 1` 表示成功 |

## 閒置自動關機／省電模式（靜態分析，未實機驗證）

來源：官方 App ConneX 1.0.31 反編譯碼（`CommandManager`、`CommandParser`、`PowerSaveModeActivity`）與 App 內的說明文字。

| 設定 | 指令碼 | payload | 回應 | 顯示條件（D8 旗標） |
|------|--------|---------|------|---------------------|
| 省電模式 | 查詢 `0xB1`（177）／設定 `0xB2`（178） | 查詢空；設定 1 byte（1 開、0 關） | `P[0]==1` 為開，兩個指令碼共用同一個解析 | `b23_isSupportSavingMode` |
| 無音訊自動關機 | 查詢 `0xD7`（215）／設定 `0xD6`（214） | 查詢空；設定 1 byte（1 開、0 關） | `P[0]==1` 為開 | `b8IsSupportAutoShutdownWithoutAudioInput` |

- App 的省電模式說明（依機種的產品表備註選用不同版本）：開啟時，藍牙裝置已連上但閒置 15 分鐘（無音訊、無操作）會進入省電待機，約 1 秒內可恢復；沒有任何藍牙裝置連上閒置 15 分鐘會關機；有版本寫明只在電池供電時閒置 30 分鐘自動關機。**因此關閉省電模式應可避免閒置自動關機**，但這是從說明文字推得的，尚未在裝置上驗證。
- MP330 的 D8 回應：`b23_isSupportSavingMode` 為 1（有省電模式設定）、`b8` 為 0（沒有無音訊自動關機設定）。
- 實作：`requests.set_power_save`／`set_auto_shutdown_no_input`、`responses.parse_power_save`／`parse_auto_shutdown_no_input`、命令列 `power-save`（預設只讀；`--set on|off` 須加 `--yes`，設定前印出原值與還原指令，設定後讀回確認）。

## 未解

- 兩款裝置的 `groupIndex`、電量用哪個指令、是否 TWS 型
- 自訂 EQ：`P[1]` 的意義、負增益編碼、`freq` 單位、`eqIndex` 與裝置型號的對應
- 長封包分片與 MTU
