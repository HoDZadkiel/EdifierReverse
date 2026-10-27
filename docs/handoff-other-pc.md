# 換電腦實機驗證步驟

目標：在有 ES850NB / MP330 與藍牙的電腦上，**只讀**探測兩款裝置，把紀錄帶回來修正文件與程式。全程只送 12 個查詢指令，不送任何設定或韌體指令。

## 前置條件

- Windows，有可用的藍牙無線電（本機的 `Get-PnpDevice -Class Bluetooth` 要有狀態 OK 的項目；另一台電腦預期有）。
- 已安裝 [uv](https://docs.astral.sh/uv/)。
- 專案已複製到那台電腦。`docs/apk/`（官方 APK，約 120 MB）不進版控，換電腦探測用不到，不需要帶。

## 1. 準備環境

虛擬環境可放在專案目錄外（選用，專案在雲端同步資料夾時建議）：

```powershell
cd <專案路徑>
$env:UV_PROJECT_ENVIRONMENT = "C:\Work\edifier-venv"
uv sync
```

若忘了設環境變數，`uv sync` 會在專案內建 `.venv`，請刪掉重來。

## 2. 離線自檢（不需要裝置）

```powershell
uv run pytest
uv run edifier-reverse sim --chunk 5
```

預期：173 個測試全過；`sim` 印出 12 個查詢，其中 `0xF2`、`0xD5`、`0x43` 為「（無回應）」是正常的（模擬裝置沒有實作它們）；LDAC／LHDC／高清編碼三個查詢模擬裝置有回應，數值是模擬用的任意值。

再用真的 socket 對跑一次 SPP 傳輸層（不需要藍牙，只驗證 Python 的 socket／執行緒在那台電腦上運作正常）：

```powershell
uv run edifier-reverse sim --spp --chunk 3
```

預期輸出與上面的 `sim` 相同。任何一項失敗，先不要碰裝置，把輸出帶回來。

## 兩款裝置走不同的傳輸

官方 App 的規則：產品表有 SPP UUID 就走 SPP，否則走 BLE。所以：

| 型號 | 傳輸 | 前提 |
|------|------|------|
| ES850NB | **SPP**（經典藍牙 RFCOMM） | 耳機已與這台電腦**配對並連上 A2DP**（官方 App 也是這樣才處理廣播） |
| MP330 | **BLE GATT** | 喇叭開機、可被掃描 |

兩款都要先掃 BLE 廣播：版本、加密設定和（ES850NB 要連的）經典藍牙位址都在廣播資料裡。

## 3. 掃描廣播（只讀，不連線）

```powershell
uv run edifier-reverse scan --seconds 12
```

重點看每個 Edifier 裝置印出的兩行：

```
manufacturer 0x07E0: ...
-> A2DP 位址=AA:BB:CC:DD:EE:FF  bleVersion=?  encryption=?
```

「A2DP 位址」應該就是耳機在 Windows 藍牙設定裡看到的那個經典藍牙位址；請核對是否一致，**不一致代表廣播資料的前 6 bytes 不是 A2DP 位址，推測有誤**。

把**整段輸出**記下來。若找不到裝置：
- 先試 `scan --all` 看有沒有名稱相近的裝置。
- 裝置可能正與手機連線而不再廣播（**推測**，未驗證）；暫時關掉手機藍牙或 ConneX App 再掃一次。
- 兩款裝置請分開測，一次只開一台，方便對應。

## 4. 探測（會連線，只送查詢）

不需要再手動帶位址，工具會自己掃描、挑出符合型號的裝置，並依型號自動選傳輸（ES850NB → SPP，MP330 → BLE）。**兩款分開測，一次只開一台。**

```powershell
uv run edifier-reverse probe --model MP330
uv run edifier-reverse probe --model ES850NB
```

常用選項：

| 選項 | 用途 |
|------|------|
| `--transport ble\|spp` | 強制指定傳輸（預設 auto）。例如想試 ES850NB 的 BLE GATT：`--transport ble` |
| `--address <BLE 位址>` | 附近有多台時限定掃描結果 |
| `--a2dp-address <經典藍牙位址>` | 略過廣播，直接指定 SPP 的連線目標 |
| `--channel N` | 只直連指定的 RFCOMM channel。不指定時照官方順序：先用 UUID 查詢，失敗再試 channel 1 |
| `--version N --encryption N` | 略過廣播推導，直接指定 |

會依序送出 `0xD8`、`0xD0`、`0xF2`、`0xC6`、`0xC8`、`0xC9`、`0xCC`、`0xD5`、`0x43`，以及高音質編碼狀態的 `0x48`（LDAC）、`0x37`（LHDC）、`0x91`（高清編碼）共 12 個查詢，並把送出的封包、回應 payload 與解析結果印出來，同時寫入 `docs/captures/probe-<型號>-<時間>.jsonl`。

- 沒給 `--version` 時，工具會先掃描取得 `bleVersion`／`encryption`；找不到廣播資料則退回預設（版本 2、不加密）並印出警告。
- 若掃描取得的版本與預設不同，這本身就是重要資訊，請記下。
- 若報「找不到 service」（BLE），錯誤訊息會列出裝置實際提供的 service UUID，**請整段複製回來**——這代表 UUID 與推測不符。
- 若報「RFCOMM 連線失敗」（SPP），訊息會逐項列出「UUID 查詢」與「channel 1」兩次嘗試各自的失敗原因（含 WinSock 錯誤碼）。先確認耳機已與這台電腦配對且 A2DP 已連上；兩項都失敗請把**完整訊息**帶回來。常見碼：10050 網路已停用（沒有可用藍牙卡）、10060 逾時、10061 被拒、10014／10022 代表送給系統的參數有問題（我們的結構可能不對）。
- 成功時開頭會印出「連線方式：UUID 查詢 …」或「直連 channel 1」，紀錄檔的 `meta.how` 也有；**請記下是哪一種**——這直接驗證「UUID 查詢」是否可行。
- 工具會在開頭印出實際選用的傳輸、目標位址與 Session，請一併記下。

## 5. 帶回什麼

1. 步驟 2～4 的完整終端輸出。
2. `docs/captures/` 內的 `.jsonl`（已 gitignore；內含裝置 MAC，不要 push）。
3. 做 probe 的那台電腦的藍牙晶片／介面卡型號（評估電腦端能否送出 LDAC 時要用）。
4. 觀察到的異常，例如：哪些查詢逾時、回應長度與預期不符、連線失敗訊息。

## 預期與判讀

| 觀察 | 代表 |
|------|------|
| `0xD8` 有回應 | 協定外層（header／長度／CRC／appCode）基本正確 |
| `0xD8` 沒回應但其他有 | D8 可能不被該裝置支援，其餘仍可用 |
| 全部逾時 | 先懷疑 `bleVersion`／`encryption`、寫入型態、UUID 或 channel 不符；對照廣播資料 |
| ES850NB 用 SPP 有回應、用 `--transport ble` 沒有 | 符合官方 App 的選路規則（ES850NB 只走 SPP） |
| 回應 CRC 錯誤被丟棄 | 重組或 CRC 規則與推測不同；`Reassembler.dropped` 會增加（目前不會自動印出，必要時加除錯輸出） |
| `0xCC` 回應的第 1 byte | 該裝置的 ANC `group`，用來查 `anc-groups.json` |
| `0xD0` 與 `0xF2` 哪個有回應 | 決定電量該用哪個指令 |
| `0x48` / `0x37` / `0x91` 的回應 | 裝置端的高音質編碼狀態。`0x37` 無回應或 `0x91` 的 `lhdc` 為 -1 表示沒有 LHDC（ES850NB 預期如此）。判讀見 [hq-audio-assessment.md](hq-audio-assessment.md) |

## 安全提醒

- `probe` 不含任何設定類指令，不要自行在命令列加。
- 不要嘗試任何韌體更新相關操作；`packet.build` 也會拒絕這類指令。
- MP330 的產品表標示支援韌體更新（`LastFirmwareVersion` 1.0.2），這只是資料，**不代表可以去碰**。
- 探測紀錄含 MAC，只留在 `docs/captures/`，不要貼到公開地方。
