# 部署與開機自動啟動

只主推 C# 版，因為只有它有單例鎖（`Local\EdifierTray.SingleInstance`），開機自啟後再手動開一次也不會出現兩個托盤、兩條連線。Python 版只保留 uv 啟動指令，不打包。下表的「實測」是在 Windows 11 + ES850NB 上跑過的結果。

## 取得與安裝

預建的安裝檔放在 [Releases](https://github.com/HoDZadkiel/EdifierReverse/releases)（目前為 pre-release，未簽章）。下表是各種取得方式。

| 方式 | 說明 | 狀態 |
|------|------|------|
| **安裝檔** `scripts\build-installer.ps1` | 自含發佈（使用者不需要裝 .NET）+ Inno Setup，產生 `EdifierTray-Setup-<版本>.exe`（約 63 MB）。免系統管理員，安裝到 `%LOCALAPPDATA%\EdifierTray`，附「登入時啟動」選項、開始功能表捷徑與解除安裝程式 | 實測：靜默安裝、啟動、解除安裝（exe 與捷徑都移除）；未勾選自啟時不寫開機項目。**勾選自啟的路徑沒有實測**（寫入的機碼與 `install.ps1` 相同，後者以獨立機碼測過） |
| **一鍵腳本** `scripts\install.cmd` | 在本機建置單檔 exe 並安裝、設自啟、啟動。需要 .NET 8 SDK。參數：`-NoAutostart`、`-NoLaunch`、`-Uninstall`、`-InstallDir` | 實測：獨立資料夾（路徑含空白）與獨立機碼跑過安裝、`-NoAutostart`、`-Uninstall`。「安裝後直接啟動」與「覆蓋執行中的版本」未測 |
| 手動單檔發佈 | `dotnet publish src/EdifierTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o <輸出>`：1 個 exe，26.3 MB，約 6 秒建完，常駐工作集約 96 MB。執行端需要 [.NET 8 桌面執行環境](https://dotnet.microsoft.com/download/dotnet/8.0) | 實測 |

安裝檔的尺寸：自含單檔 exe 已經壓縮過（68.8 MB，旁邊另有 5 個 WPF 原生 DLL），Inno 再壓也縮不了多少。改成不壓縮的單檔再交給 Inno 的 LZMA，也許能更小，**未嘗試**。安裝程式本身是英文介面（Inno Setup 沒有官方中文語言檔）。exe 與安裝檔都沒有簽章，從網路下載後 SmartScreen 會警告。

## 發佈流程（GitHub Actions）

`.github/workflows/release.yml`（windows-latest）：安裝 .NET 8 與 Inno Setup → `scripts/build-installer.ps1` → 算 SHA-256 → 上傳 artifact。推 `v*` 標籤時，再由另一個 job 把安裝檔與 `.sha256` 傳到同名 Release（沒有就建立，`v0.*` 標為 pre-release；已有就只替換附件）。

發佈新版：改 `pyproject.toml` 的 `version` → commit → `git tag v<版本>` → `git push --tags`。標籤與 `pyproject.toml` 版本不一致時建置會失敗。手動執行（Actions → Run workflow）只建置、留下 artifact，不發佈。

這個流程也是申請 [SignPath Foundation](https://signpath.org/) 免費簽章的前提：它要求安裝檔由倉庫原始碼自動建置（origin verification）。目前**尚未簽章**，也還沒申請。

## 開機自動啟動怎麼做的

寫入目前使用者的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（值名 `EdifierTray`，路徑加引號）。安裝檔、`install.ps1` 與托盤右鍵選單的開關寫的是同一個值，所以彼此對得上。

不採用的替代方案：

- **啟動資料夾捷徑**：效果等同 `Run`，但寫捷徑要用 COM，比寫一個機碼複雜。
- **工作排程器（登入時觸發）**：可設延遲、可重試，但要處理的程式與權限更多；只在下面第 2 項實測證明需要時才改用。
- **Windows 服務**：服務跑在 Session 0，不能顯示托盤與面板，不適用。

## 已知缺口與待驗證

| # | 項目 | 依據 | 處理 |
|---|------|------|------|
| 1 | 使用者在工作管理員「啟動應用程式」停用它時，`Run` 的值還在，只是 `Explorer\StartupApproved\Run` 底下同名值被標為停用；目前選單的開關只看 `Run`，會誤顯示「已啟用」 | 抽查 5 個啟用中的項目，首位元組為 `02`；停用時為 `03` 是依 Windows 行為推測，**未實測** | 待做：`Autostart.IsEnabled` 一併檢查 `StartupApproved`，啟用時寫回 `02` |
| 2 | 登入當下藍牙可能還沒就緒；服務本身有指數退避重連，但「登入／休眠喚醒後的重連」**未驗證** | — | 待驗證：重新開機一次，確認登入後自動出現托盤並連上耳機 |
| 3 | 若用 `dotnet EdifierTray.dll` 執行，`Environment.ProcessPath` 是 `dotnet.exe`，會寫成錯的開機項目 | 讀 `Autostart.cs` | 待做：非單檔 exe 時提示而不寫入。用安裝檔或安裝腳本安裝不會遇到 |
| 4 | 重新安裝時若不勾選自啟，舊的開機項目不會被移除（`install.ps1 -NoAutostart` 會移除） | 讀 `EdifierTray.iss` | 待做 |

驗證清單（皆未做）：重新開機後自動啟動；快速啟動與休眠喚醒；在工作管理員停用再啟用後選單狀態是否正確。
