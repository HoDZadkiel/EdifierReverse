# CLAUDE.md — EdifierReverse

本檔給進入這個專案的 AI agent 看，**內容自足，不依賴任何特定機器上的設定**。執行環境若另有全域指示，那是額外的；照本檔做就能正確參與這個專案。給人看的說明在 [README.md](README.md)，兩邊不要互相搬內容。

## 專案目標

逆向 Edifier 藍牙耳機（ES850NB）通訊協定，在 Windows 上解鎖完整功能。

背景：Edifier 的進階功能（ANC、EQ、模式切換等）只有手機 App 能控制，Windows 無法使用。目標是還原通訊協定，做出 Windows 端的 Python 函式庫／CLI 與托盤小工具，完整操作 ES850NB。MP330 喇叭原本不在目標內；2026-10-31 起已能被托盤發現並以 BLE 連線讀取狀態（Python 與 C# 皆是），並部分實機驗證（連線、查詢、省電模式讀寫），仍不是主要驗證對象。C# 版托盤功能領先 Python 版（階層選單、輸出裝置切換、防自動關機只有 C# 有）。素材：ES850NB 實機、Android 手機、官方 APK。

## 專案性質與技術棧

| 項目 | 內容 |
|------|------|
| 類型 | `software`（純軟體） |
| 技術棧 | Python 3.12+（uv、bleak、pystray、Pillow、pycaw）；C# / .NET 8 / WPF（NAudio）；逆向工具 jadx、Android HCI snoop log、Wireshark |
| 實機驗證範圍 | 只有 ES850NB；其他機種可辨識但未測試 |

## 資料夾用途

```
EdifierReverse/
├── README.md  CLAUDE.md  DEVLOG.md  LICENSE  .gitignore  pyproject.toml  uv.lock
├── src/
│   ├── edifier_reverse/        # Python 函式庫、CLI、Python 版托盤
│   │   ├── strings.json        # 四種語言字串表（C# 嵌入同一份）
│   │   └── catalog.json        # 機種目錄（scripts/gen_catalog.py 產生，勿手改）
│   └── EdifierTray/            # C# / WPF 版托盤（Protocol/ Transport/ Service/ Ui/）
├── tests/                      # Python 測試（離線）
│   └── EdifierTray.Tests/      # C# 測試（xUnit）
├── docs/                       # 設計文件與研究結論（apk/、captures/ 不進版控）
│   └── images/                 # 文件用圖（README 的截圖）
├── scripts/                    # 一鍵安裝（install.cmd/.ps1）、Setup.exe 安裝檔（build-installer.ps1 + installer/）、解密產品表、產生常數表／目錄、抽取 ANC／D8 表
├── examples/                   # 可執行的使用範例（目前空）
└── .github/workflows/          # release.yml：建置安裝檔，推 v* 標籤時上傳到 Release
```

寫東西前先確認要寫進哪個目錄。不確定就問，不要自己開新的頂層目錄。

## 開發約定

- Python 環境（選用：把 venv 放專案目錄外）：`$env:UV_PROJECT_ENVIRONMENT = "C:\Work\edifier-venv"` 後 `uv sync --extra tray`；C# 建置前設 `EDIFIER_BUILD_ROOT`（選用：把 bin/obj 放專案目錄外，專案在雲端同步資料夾時用）
- `commands.py`、`d8_flags.py`、`catalog.json` 由 `scripts/gen_*.py` 產生，不手改
- **兩份協定實作要一致**：改封包、設定規則、字串、機種目錄時，Python 與 C# 都要改，並用同樣的測試向量守住
- 字串一律走 `strings.json`（鍵名、四種語言、`{參數}` 都要齊，有測試檢查）；UI 文字不要寫死
- 設定指令（ANC）一律經節流與冷卻（≥ 2 秒，實機測得裝置約 1.5 秒內會靜默忽略）；介面在冷卻期停用切換
- 測試不得讀寫使用者真正的 `%APPDATA%` 設定或開機項目（Python 的 conftest 已隔離；C# 用暫存路徑）
- 驗證 UI 時不要用螢幕截圖（會抓到螢幕上的其他內容）；用把視窗／元素渲染成圖片的方式
- 協定結論寫在 `docs/protocol-*.md`；研究與設計結論寫在 `docs/findings.md`、`docs/tool-design.md`

## 版控

本專案使用 git 版控（遠端為 GitHub 公開倉庫）。push 前逐項確認：`.gitignore` 涵蓋完整、`.env` 與金鑰檔未被追蹤、程式碼與文件內沒有 token／密碼／連線字串／硬編碼 IP 或裝置位址、README 沒有過期、沒有誤加大型二進位檔。push 前使用 `safe-push` skill。

## 禁止事項

- 不得 push 官方 APK、反編譯原始碼、HCI snoop log（`docs/apk/`、`docs/captures/` 已 gitignore）；snoop log 與探測紀錄含藍牙 MAC 與個資
- 不得對裝置送出未經確認的韌體更新（OTA / DFU）指令，可能變磚（函式庫層已封鎖）
- 只送已在抓包或實機驗證中觀察過的指令；實驗新指令前先說明風險並取得同意，測完要還原裝置狀態
- 協定結論只寫已驗證的事實，並標明推測與已驗證的區別

## 變更紀錄

每次涉及檔案修改、除錯或決策的對話結束前，把工作記錄追加到專案根目錄的 `DEVLOG.md`（不存在就建立）。純唯讀的問答可略過。

格式如下 —— 用當天日期當標題，當天已有記錄就往其下追加，不重複建標題：

```markdown
## YYYY-MM-DD

### 修改內容（Changes）
- 改了哪些檔案、做了什麼

### 問題與解法（Problems & Solutions）
- 遇到的障礙與最終採用的解法

### 待辦事項（TODO）
- 未完成或需要後續跟進的事項
```

只寫條列，不寫段落。
