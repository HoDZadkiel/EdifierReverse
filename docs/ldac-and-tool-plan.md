# LDAC 可行性與工具方案評估

評估日期：2026-10-27。本文件有三個章節：

1. 電腦端有沒有辦法使用 LDAC
2. 最終做成工具要用什麼方案
3. 專案完成後，Windows 使用者能得到什麼

前兩章都建立在一個**尚未驗證的前提**上：ES850NB、MP330 真的支援 LDAC。ES850NB 的外部資料說有 LDAC（見 [hq-audio-assessment.md](hq-audio-assessment.md)），MP330 查不到可靠資料。實機 `probe` 的 `0xD8` 旗標（位元組 18 的 bit2）與 `0x48` 回應出來之前，MP330 的 LDAC 問題可能根本不存在。

背景知識（電腦端需要的是編碼器、標準 Windows 沒有 LDAC）不在這裡重複，見 [hq-audio-assessment.md](hq-audio-assessment.md)。

---

## 1. 電腦端有沒有辦法使用 LDAC

### 結論

標準 Windows 11 無法送出 LDAC。可行的路線有四條，沒有一條是本專案能直接提供的。

| 路線 | 可行性 | 主要代價與風險 |
|------|--------|----------------|
| 外接 LDAC USB 發射器 | 最穩 | 要買硬體；耳機改和發射器配對，可能與我們的控制通道衝突 |
| Alternative A2DP Driver | 可行，但付費、替換系統驅動 | 見下方 |
| 自己做 LDAC 傳送端 | 理論上可行，工程量大 | 不建議 |
| Linux（原生或 WSL） | 原生可行，WSL 脆弱 | 要換系統；WSL 要自建核心，音訊回送 Windows 沒找到做法 |

### 外接 LDAC USB 發射器

- 編碼由發射器自己做，Windows 只看到一張 USB 音效卡，不必換驅動（依 [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5560825/bluetooth-codecs-supported-in-windows-11) 與相關指南）。
- 我沒有查證具體產品，也沒有實測。
- **與本專案控制通道的衝突是推論，未證實**：音訊改走發射器後，電腦還得另外連上耳機做控制。ES850NB 有多點連線，理論上可同時連兩個裝置；但官方 App 有一條提示說「某功能啟用時 LDAC／LHDC 會因藍牙頻寬限制無法串流」，我沒查到是哪個功能，**推測**可能就是多點連線。若是，這條路會讓 LDAC 失效。MP330 的控制走 BLE，這個衝突較不明顯。

### Alternative A2DP Driver（BluetoothGoodies）

依 [官網](https://www.bluetoothgoodies.com/a2dp/)、[購買頁](https://www.bluetoothgoodies.com/a2dp/purchase/)、[FAQ](https://www.bluetoothgoodies.com/a2dp/faq/)：

| 項目 | 內容 |
|------|------|
| 作法 | 取代 Windows 10／11 內建的 A2DP 驅動，可調編碼參數 |
| 編碼 | SBC、AAC、aptX、aptX HD、aptX Low Latency、LDAC（AAC 要另購含 AAC 的版本） |
| 價格 | 授權 9.99 美元；試用期內購買 5.99 美元 |
| 試用 | 7 天；結束後耳機仍會連上，但**聽不到聲音** |
| 前提 | 裝置須以經典藍牙 A2DP 配對（不是 LE Audio）；安裝需要系統管理員權限 |

- 有一個來源主張「沒有合法的軟體途徑」，與上述資料矛盾；這類資料多半是部落格，細節要保守看待。
- **沒查到**它與 ES850NB／MP330 的相容性，**也沒查到解除安裝的步驟**。
- **未驗證**它的驅動替換是否會影響我們的 SPP（RFCOMM）控制通道；兩者理論上是不同的服務，但沒有實測。
- 這是替換系統驅動的第三方商用軟體，要不要購買、安裝由使用者決定。試用前建議先建立系統還原點。

### 自己做 LDAC 傳送端

- **編碼器**：libldac 的 [AOSP 原始碼](https://android.googlesource.com/platform/external/libldac/+/master/src/ldaclib.c)是 Sony 以 Apache 2.0 釋出；但其 NOTICE 說在產品中使用 LDAC 需要走 Sony 的認證流程。我沒核對 Sony 目前的條款，個人使用與散布是否有差別要自行確認。
- **藍牙堆疊**：可考慮 Google 的 [Bumble](https://github.com/google/bumble)，它支援 A2DP 與 USB HCI。但我**沒找到 Bumble 有 LDAC 實作，也沒找到 Windows 支援的文件**。Windows 上大概要用 Zadig 換成 WinUSB 驅動（此為 BTstack 文件的做法，不一定適用 Bumble），那張藍牙卡就不能再給 Windows 用。
- **還得自己做**：Windows 音訊擷取、LDAC 的 A2DP 能力協商與封包。
- 結論：工程量大、風險高，對「解鎖控制功能」的目標幫助有限。**不建議**，除非本來就想做這個專案。

### Linux

依 [Level1Techs 的討論](https://forum.level1techs.com/t/ldac-linux-audio-driver-on-windows-via-wsl/181269)，在 WSL 遇到 Hyper-V 擋藍牙、虛擬機凍結等問題，LDAC 只在原生 Linux 上成功。我沒找到把 WSL 的音訊串流送回 Windows 的做法。

### 建議順序

1. 先用 `probe` 確認兩款是否真的支援 LDAC。
2. 想最快驗證效果：Alternative A2DP Driver 的 7 天試用，先建系統還原點，並確認它對你的藍牙卡可用。
3. 想零系統改動：外接發射器，但先釐清多點連線與 LDAC 是否互斥。
4. 不建議自己做傳送端。

### 需要實機才能知道

- 兩款裝置是否真的支援 LDAC、裝置端目前是否啟用。
- Alternative A2DP Driver 與我們的 SPP 控制通道能否共存。
- 多點連線是否就是與 LDAC 互斥的那個功能。

---

## 2. 最終做成工具，用什麼方案

### 先界定範圍

這個工具是「PC 版 ConneX」：管電量、ANC、EQ、編碼狀態。**音訊編碼不屬於它**，工具只負責顯示裝置端狀態與提示，不碰電腦端驅動。

影響選擇的已知事實：
- 兩款走不同傳輸：ES850NB 走 SPP（經典藍牙 RFCOMM），MP330 走 BLE GATT。
- Python 端 BLE 用 bleak 沒問題；RFCOMM 目前靠 Winsock 的 ctypes 做法，**未經實機驗證**。
- 協定層是純函式，不依賴傳輸；已有 173 個離線測試與完整協定文件。

### 方案比較

| 方案 | 優點 | 缺點 |
|------|------|------|
| **A. 沿用 Python + 輕量托盤 GUI** | 完全重用現有函式庫與測試；你當初選的語言；改動最小 | 打包成 exe 體積較大；RFCOMM 的 Winsock 做法尚未驗證 |
| B. 全改 C#／.NET | Windows 藍牙 API（BLE、RFCOMM）是一等公民；托盤與安裝包成熟 | 要重寫並移植測試；與 Python 偏好不同 |
| C. Rust + Tauri | 執行檔小 | 重寫成本最高、學習曲線陡 |
| D. 網頁或 Electron | 介面彈性大 | 網頁藍牙沒有經典藍牙 RFCOMM，ES850NB 走不通；Electron 又很重 |

### 推薦：A，分階段

1. **現在到實機驗證**：維持 Python，不動架構。
2. **實機證實後**：做 CLI 加托盤小工具。
   - 顯示電量、ANC 狀態，**先做只讀**。
   - 確認格式後才開放切換 ANC。
   - EQ 要等負增益編碼確認後才開放。
   - 托盤用 pystray 加簡單視窗；打包用 PyInstaller。
3. **保留的退路**：Winsock 的 UUID 查詢若實機不通，Python 這邊還有 WinRT 的 RFCOMM 套件可用（bleak 本來就在用 WinRT），不必因此離開 Python。
4. **改走 C# 的條件**：
   - 要發給不熟技術的人、需要簽章安裝包；
   - 或 Python 打包在目標電腦上遇到防毒誤判；
   - 或決定要自己做 LDAC 傳送端（那部分需要原生元件，另當別論）。

### 無論選哪個都不變的規則

- OTA／DFU 永久排除（函式庫層已強制）。
- 設定類指令一律要確認視窗，且只有在實機驗證過後才開放。
- 探測紀錄含裝置 MAC，不 push。
- 工具不管理電腦端藍牙音訊驅動。

### 會改變這個建議的事

| 實機結果 | 影響 |
|----------|------|
| Winsock UUID 查詢與 channel 1 都不通，WinRT 也難用 | 傾向改走 C# |
| 兩款其實都不支援 LDAC | 第 1 章變成不必要，工具範圍更單純 |
| 想把工具散布給別人 | 簽章、安裝包、防毒誤判的成本上升，傾向 C# |

---

## 3. 專案完成後，Windows 使用者能得到什麼

**前提：以下好處全部要等實機驗證通過才成立。** 兩款裝置目前都還沒碰過。

### 現況缺口

官方電腦版 TempoHub 是有線專用，ConneX 是手機版（出處為官方頁面說法，未逐項核對）。藍牙連 Windows 時沒有官方控制工具，ANC、EQ 等設定得靠手機。專案的價值就在補這個缺口。

### 可能得到的好處

| 好處 | 說明 | 把握度 |
|------|------|--------|
| 不用手機就能控制 | 在電腦上用藍牙看電量、切換 ANC | 協定已解，待實機驗證 |
| 藍牙下的控制 | 補上 TempoHub 沒涵蓋的藍牙情境 | 同上 |
| 電腦獨有的用法 | 托盤顯示電量、快捷鍵切 ANC、腳本自動化（例如開會時切換模式） | 我的點子，官方 App 沒有，需另外開發 |
| 協定文件公開 | 搜尋沒找到 Edifier 的公開逆向資料，這份文件對其他開發者有參考價值 | 「查無」不代表不存在 |
| 可能擴及其他機種 | 產品表有 103 個機種，指令集與功能旗標看起來共用，函式庫依旗標判斷能力 | 只有兩款裝置，其他機種完全沒測 |

擴及其他機種要小心：把指令送給沒驗證過的機種有風險，未知機種預設應只讀。

### 得不到的

- **LDAC 或更高音質**：電腦端需要編碼器，工具只能顯示裝置端狀態（見第 1 章）。
- **韌體更新**：刻意排除，可能讓裝置變磚，仍要靠 TempoHub 或 ConneX。
- **依賴雲端的功能**：產品表有翻譯、問答、AI EQ 之類旗標，未研究是否依賴官方伺服器，不要預設能用。
- **官方支援**：非官方工具，裝置韌體更新後協定可能改變。

### 依價值與可行性的優先順序

1. 電量顯示：最簡單、風險最低。
2. ANC 切換：日常最有用，但要先實機確認每款用哪一組模式表。
3. 裝置名稱與狀態查詢。
4. EQ：價值中等，但負增益編碼未解，風險最高，排最後。
5. 編碼狀態顯示：資訊性質，不改善音質。

### 一句話

好處集中在「把手機上的日常控制搬到電腦」，對只用電腦的 ES850NB／MP330 使用者最實用；不會改善音質，也不取代官方韌體更新。

---

## 來源

- [Bluetooth Codecs supported in Windows 11 — Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5560825/bluetooth-codecs-supported-in-windows-11)
- [Alternative A2DP Driver](https://www.bluetoothgoodies.com/a2dp/)
- [Purchase Alternative A2DP Driver License](https://www.bluetoothgoodies.com/a2dp/purchase/)
- [Alternative A2DP Driver FAQ](https://www.bluetoothgoodies.com/a2dp/faq/)
- [libldac — AOSP](https://android.googlesource.com/platform/external/libldac/+/master/src/ldaclib.c)
- [google/bumble](https://github.com/google/bumble)
- [LDAC linux audio driver on windows via WSL? — Level1Techs](https://forum.level1techs.com/t/ldac-linux-audio-driver-on-windows-via-wsl/181269)
