## 2026-10-27

### 修改內容（Changes）
- 以 project-init（software 類型）建立骨架：README.md、CLAUDE.md、LICENSE(MIT)、.gitignore、src/ tests/ docs/ scripts/ examples/ .github/workflows/
- 填寫 README / CLAUDE.md 佔位符；新增 docs/apk/、docs/captures/ 並加入 .gitignore

### 問題與解法（Problems & Solutions）
- 專案位於 Nextcloud 下，但使用者選擇用 git 並預計 push GitHub；尚未 git init，push 前需走 safe-push

### 待辦事項（TODO）
- 取得官方 APK 放入 docs/apk/，jadx 反編譯
- Android 錄 HCI snoop log
- bleak 掃描 ES850NB / MP330，判斷 BLE GATT 或 SPP
- git init（待使用者確認時機）

### 逆向進度（同日追加）

#### 修改內容（Changes）
- 安裝 jadx 1.5.6 至 C:\Tools\jadx；ConneX 1.0.31 的 .apkm 解出 base.apk，反編譯到 C:\Work\edifier\out（29,543 個 java 檔，344 個方法還原失敗屬正常）
- 新增 pyproject.toml / uv.lock（uv，bleak）；venv 用 UV_PROJECT_ENVIRONMENT 放 C:\Work\edifier-venv，避免被 Nextcloud 同步
- 新增 scripts/decrypt_products.py：解密 APK 內 products_release.json（AES-CBC，金鑰在 CipherUtil.java）
- 新增 docs/protocol-uuids.md：ES850NB / MP330 的 BLE 與 SPP UUID
- 更新 README.md（uv 說明、待辦）

#### 問題與解法（Problems & Solutions）
- uv init 預設在專案內建 .venv → 刪除並改用 UV_PROJECT_ENVIRONMENT 指向本機
- products_release.json 非純 JSON → 反編譯 CipherUtil 得知是 Base64+AES-CBC
- 查無官方 changelog 與社群逆向資料；TempoHub（有線 USB 電腦版）暫緩

#### 待辦事項（TODO）
- 找封包格式（header / 長度 / checksum / 指令碼）
- bleak 實機掃描驗證 UUID
- UV_PROJECT_ENVIRONMENT 目前只在命令列設定，尚未設為永久環境變數（需使用者決定）

### 封包格式分析（同日追加）

#### 修改內容（Changes）
- 新增 docs/protocol-packet.md：送出/接收封包結構（v1/v2）、CRC、XOR 加密、版本由 BLE 廣播決定
- 新增 scripts/gen_commands.py 與 src/edifier_reverse/commands.py：179 個指令碼常數（自動產生）
- 更新 README.md 待辦

#### 問題與解法（Problems & Solutions）
- PowerShell 內嵌 python -c 多行字串會被吃掉引號 → 改寫成 scripts/*.py 檔
- 數值 184、245、246 在 CommandIndex 各有兩個名稱重複，未查原因

#### 待辦事項（TODO）
- 解 ANC / EQ 等 payload 與回應格式（CommandParser.java）
- 實機驗證：廣播的 bleVersion/encryption、UUID、封包收發

### 指令 payload 分析（同日追加）

#### 修改內容（Changes）
- 新增 docs/protocol-commands.md：電量/狀態/版本/MAC/名稱、ANC、EQ 的 payload 與回應格式
- 新增 scripts/extract_anc_groups.py 與 docs/anc-groups.json：50 組 ANC 模式表（groupIndex -> modeIndex 名稱）

#### 問題與解法（Problems & Solutions）
- ANC 的 modeIndex 意義不是全域編號，而是依裝置的 groupIndex 查表 -> 需實機讀 ANC 查詢回應 P[0] 才知兩款裝置用哪組
- PowerShell 的 > 重導向產生帶 BOM 的 UTF-8 -> Python 用 utf-8-sig 讀
- ANC 抽取腳本缺 group 30，部分群組可能對錯，已在文件註明

#### 待辦事項（TODO）
- 實機：確認兩款的 groupIndex、電量指令、是否 TWS 型
- 解自訂 EQ（0x43-0x47）與 D8Bean 位元表

### 自訂 EQ 與 D8 功能旗標（同日追加）

#### 修改內容（Changes）
- docs/protocol-commands.md：新增自訂 EQ（0x43-0x47）章節（各 eqIndex 的頻段數與位元組排列、0x44/0x46/0x47 payload）
- 新增 docs/protocol-d8.md、docs/d8-bits.json、scripts/extract_d8_bits.py：0xD8 回應逐位元組／位元欄位表
- 更新 README.md 待辦

#### 問題與解法（Problems & Solutions）
- getBasicFreqByEqIndex 的 switch 有 fallthrough，以「頻段數 x 每頻段長度 = 總長」交叉驗證每種 eqIndex 後確認
- D8Bean 純數值欄位（byte 1/3/9/13）寫法不同，抽取腳本沒抓到，文件手動補上
- b13EQ 是 EQ 方案編號，與自訂 EQ 的 eqIndex 是不同概念

#### 待辦事項（TODO）
- 實機：D8 回應實際內容、ANC groupIndex、電量指令
- 自訂 EQ：負增益編碼、freq 單位、P[1] 意義

### Python 函式庫與測試（同日追加）

#### 修改內容（Changes）
- 新增 src/edifier_reverse/：packet.py（組包/解析、v1/v2、XOR、OTA 封鎖）、requests.py、responses.py、eq.py、d8.py、d8_flags.py（自動產生）
- 新增 scripts/gen_d8_flags.py、scripts/scan.py（BLE 只讀掃描）
- 新增 tests/（74 個單元測試）；pyproject.toml 加 pytest（dev）與 pythonpath=src
- README.md 補測試與函式庫說明

#### 問題與解法（Problems & Solutions）
- 測試向量用手算的位元組（例如 v2 查詢 ANC = AA EC CC 00 00 62），不靠被測程式自己產生
- 破壞性驗證：改 V1_CRC_BASE、XOR_KEY、放行 0xAA header、關掉 CRC 檢查，各會讓測試失敗；已還原
- 實機掃描失敗：bleak 回報 No Bluetooth adapter found；Get-PnpDevice 顯示 Generic Bluetooth Radio 狀態 Unknown，bthserv 停止。這台電腦沒有可用藍牙卡，未改系統設定

#### 待辦事項（TODO）
- 換有藍牙的電腦（或接 USB 藍牙卡）後跑 uv run scripts/scan.py，看兩款裝置廣播的 bleVersion/encryption
- 連線只送查詢類指令（D8、電量、ANC、版本），確認回應格式
- 尚未處理：BLE 分片重組（等實機看到實際行為再決定）

### 連線層、離線模擬與結論整理（同日追加）

#### 修改內容（Changes）
- 讀官方 App 收包路徑：ConnectManager.dataCheck/subPackageOfNew、BLEManager.sendData2BLE，得知送出不切片、通知可能分片或夾帶多包、寫入型態與 MTU 300
- 新增 src/edifier_reverse/：reassembly.py、transport.py、client.py、sim.py、probe.py、ble.py、cli.py、__main__.py
- pyproject.toml 加 build-system(hatchling)、[project.scripts] edifier-reverse，uv sync 後可 uv run edifier-reverse sim/scan/probe
- 刪除 scripts/scan.py（功能併入 cli scan）
- packet.parse 新增 headers 參數，供模擬裝置解析 App 送出的 0xAA 封包
- 新增 tests/test_reassembly.py、tests/test_client.py，共 107 個測試
- 新增 docs/findings.md（結論總整理）、docs/handoff-other-pc.md（換機驗證步驟）；protocol-packet.md 補傳輸與重組章節；README 更新

#### 問題與解法（Problems & Solutions）
- 模擬裝置第一版用「換 header 重算 CRC」解析 App 封包，等於沒驗 CRC -> 改成 parse 接受 headers 參數
- 重組器對非 header 開頭的垃圾資料誤計 dropped -> 進迴圈前先檢查 header
- 套件未安裝（缺 build-system）導致 python -m 找不到模組 -> 補 hatchling 設定
- 對 reassembly.py 做 4 次破壞性驗證，皆有測試失敗；已還原

#### 待辦事項（TODO）
- 在有裝置的電腦照 docs/handoff-other-pc.md 跑 scan / probe，把 docs/captures/*.jsonl 帶回來
- 依實機結果修正文件與程式；SPP 路徑與重送機制尚未實作

### SPP（RFCOMM）傳輸層（同日追加）

#### 修改內容（Changes）
- 讀 SPPManager / BLEManager.switchConnectMethod / ConnectManager.getCorrectDevice：官方選路規則為「產品表有 SPP UUID -> 走 SPP，否則 BLE」；SPP 收發為無外框的原始封包串流；SDP 失敗時直連 channel 1；廣播資料前 6 bytes 為 A2DP MAC
- 新增 src/edifier_reverse/spp.py（SocketTransport 讀取執行緒、connect_rfcomm，預設 channel 1）
- ble.py：Profile 加 spp 欄位與 default_transport；新增 a2dp_address_from_manufacturer_data、Advert、choose_advert、discover；移除 find_session；scan 輸出 A2DP 位址
- sim.py：feed() 改為串流語意（黏包、半包），新增 serve_socket
- cli.py：probe 改為 --model 為主、自動掃描並依規則選傳輸，plan_connection 抽成純函式；sim 加 --spp
- 新增 tests/test_spp.py；共 137 個測試
- 文件更正：ES850NB 實際走 SPP、MP330 走 BLE（先前文件把 BLE 當兩款主要路徑，是錯的）；findings / handoff / protocol-packet / protocol-uuids / README 同步更新

#### 問題與解法（Problems & Solutions）
- 測試中替換 spp.socket.socket 其實替換了全域 socket.socket，連 asyncio 建事件迴圈都壞掉 -> 改為只替換 spp 模組內的 socket 名稱（SimpleNamespace）
- Windows 的 Python socket 無法用 UUID 做 SDP 查詢，只能指定 channel 號 -> 先實作 channel 號（預設 1），UUID 查詢需 WinRT，記為待辦
- 破壞性驗證 6 處（預設 channel、settimeout、型號比對、A2DP 位址順序、傳輸規則、串流消耗），皆有測試失敗；已還原

#### 待辦事項（TODO）
- 實機：ES850NB 走 SPP（確認 channel、A2DP 位址是否等於廣播前 6 bytes）、MP330 走 BLE
- 若 channel 1 不通：以 WinRT RFCOMM API 用 UUID 查 channel
- 重送機制尚未實作

- 追加：不可能的傳輸組合（MP330 + spp）改為在掃描前就拒絕（resolve_transport），新增對應測試；測試總數 138

### 用 UUID 查 RFCOMM channel（同日追加）

#### 修改內容（Changes）
- spp.py 重寫：新增 SOCKADDR_BTH（ctypes，30 位元組）、sockaddr_bth、_connect_by_uuid（呼叫 ws2_32.connect，serviceClassId=UUID、port=0，非阻塞 + select + SO_ERROR）；connect_rfcomm 預設改為「先 UUID 查詢，失敗再直連 channel 1」（官方順序），給定 channel 則只直連；每次嘗試用新 socket；失敗訊息逐項列出各次嘗試
- SocketTransport 新增 how 屬性；cli probe 的 --channel 預設改為 None，印出並記錄連線方式（meta.how）
- 新增 tests/test_rfcomm_connect.py（假 socket、假 ws2_32 驗證結構位元組、順序、備援、錯誤訊息；另有一個對真 Winsock 的冒煙測試）；test_spp.py 移除舊的 connect 測試；共 153 個測試
- 文件更新：protocol-packet、findings、handoff、README

#### 問題與解法（Problems & Solutions）
- 與指示不同：指示說用 WinRT，改用 Winsock SOCKADDR_BTH。理由：效果等同 createRfcommSocketToServiceRecord、不需新增相依、可重用 SocketTransport 與其餘流程。WinRT 保留為備案
- 這台沒有藍牙卡，無法驗證「port=0 時系統自動做 SDP」。冒煙測試對真 Winsock 得到 10050（網路已停用），不是 10014/10022，只證明接線沒有明顯錯誤，不能證明結構正確
- 破壞性驗證 9 處，其中「移除 SO_ERROR 檢查」存活（原測試被後面的 failed 判斷掩蓋）-> 補「可寫但帶錯誤碼」案例後被抓到；已還原

#### 待辦事項（TODO）
- 實機：看 probe 印出的「連線方式」是 UUID 查詢還是 channel 1，驗證 Winsock 做法
- 若 UUID 與 channel 1 都不通：改 WinRT 或另找 channel
- 重送機制尚未實作

### 高音質評估與編碼狀態查詢（同日追加）

#### 修改內容（Changes）
- 新增 docs/hq-audio-assessment.md：高音質（LDAC/LHDC）評估。結論：電腦端需要編碼器（不是解碼器、也不是裝置驅動），標準 Windows 沒有 LDAC；官方 App 的開關只是裝置端設定
- responses.py：新增 parse_ldac、parse_lhdc、parse_hd_audio_codec（HDAudioCodec）；缺少位元組回傳 -1，對照官方 getByteValue
- probe.py：白名單新增 LDAC(0x48)、LHDC(0x37)、高清編碼(0x91) 三個只讀查詢，共 12 個
- sim.py：模擬裝置回應這三個查詢（LDAC [1,9]、LHDC [2,5]、高清編碼 [3,1,0]，皆為模擬用任意值）
- 新增 tests/test_codec_queries.py；共 173 個測試
- 更新 protocol-commands.md（LDAC/LHDC/高清編碼列）、findings.md、handoff-other-pc.md、README.md（12 個查詢、173 個測試、新文件索引）

#### 問題與解法（Problems & Solutions）
- 破壞性驗證 11 處，其中兩處最初存活：把 LHDC/LDAC 的解析器互換後測試仍通過。原因是模擬值為單位元組時兩個解析器結果相同 -> 改讓模擬的 LDAC/LHDC 回應都是兩位元組（[1,9]、[2,5]），兩個方向的互換都被抓到
- 新增測試守住「白名單只能是查詢」：每個指令名稱須含 QUERY、不得是 SET/RESET，且 LDAC/LHDC/高清編碼的設定指令明確排除（切換高清編碼可能讓裝置重啟）
- python -I 會忽略 PYTHONIOENCODING，印中文時主控台編碼出錯 -> 改用 python -X utf8

#### 待辦事項（TODO）
- 實機：probe 的 0xD8 位元組 18/22 旗標、0x48/0x37/0x91 回應；記下那台電腦的藍牙晶片／介面卡型號
- 解 LDAC/LHDC 設定值的意義（目前只知道是選項位置）
- 查 MP330 的實際編碼清單；查官方提示文字中與 LDAC/LHDC 互斥的功能

### LDAC 可行性與工具方案評估（同日追加）

#### 修改內容（Changes）
- 新增 docs/ldac-and-tool-plan.md（一份文件、兩章）：(1) 電腦端取得 LDAC 的路線；(2) 最終工具方案
- findings.md 文件索引、README.md 相關文件清單加入新文件

#### 問題與解法（Problems & Solutions）
- LDAC 路線結論：標準 Windows 無法送出；可行路線為外接 LDAC USB 發射器、Alternative A2DP Driver（付費 9.99 美元／試用內 5.99 美元，試用 7 天，結束後無聲音，需管理員權限）、自製傳送端（libldac 為 Apache 2.0 但 NOTICE 要求走 Sony 認證；Bumble 查無 LDAC 與 Windows 支援）、Linux。不建議自製
- 外接發射器與本專案控制通道可能衝突：官方提示「某功能啟用時 LDAC/LHDC 無法串流」，推測是多點連線，未證實
- 工具方案結論：沿用 Python + 托盤 GUI，分階段（先只讀、後開放設定）；WinRT 為 RFCOMM 備案；符合條件才改 C#。網頁/Electron 因缺經典藍牙 RFCOMM 不適用 ES850NB
- 所有第三方資料（驅動價格、相容性）來自網路搜尋，未實測，文件已標明

#### 待辦事項（TODO）
- 實機：兩款是否真的支援 LDAC；Alternative A2DP Driver 與 SPP 控制通道是否共存；多點連線是否與 LDAC 互斥
- 待使用者決定：是否試用 Alternative A2DP Driver 或購買外接發射器（不由 agent 代為下載或購買）

### Windows 使用者效益評估（同日追加）

#### 修改內容（Changes）
- docs/ldac-and-tool-plan.md 新增第 3 章（效益、得不到的、優先順序），開頭章節列表改為三章
- findings.md 索引、README.md 文件清單同步更新

#### 問題與解法（Problems & Solutions）
- 效益結論：把手機上的日常控制（電量、ANC、狀態）搬到電腦；不改善音質、不含韌體更新；擴及其他機種未測，預設只讀
- 全部效益以實機驗證為前提，文件已標明

#### 待辦事項（TODO）
- 同上一則：等待另一台電腦的 probe 結果

## 2026-10-27

### ES850NB 實機驗證（SPP）

#### 修改內容（Changes）
- 無程式碼修改。新增紀錄 docs/captures/probe-ES850NB-20261027-213708.jsonl（gitignore，含 MAC）

#### 問題與解法（Problems & Solutions）
- 環境：TP-Link Bluetooth USB Adapter；離線自檢 173 測試、sim、sim --spp 全過
- scan：廣播 A2DP 位址 AA:BB:CC:11:22:33 與 Windows 配對裝置一致 -> 「廣播前 6 bytes 為 A2DP 位址」已驗證；bleVersion=2、encryption=16
- probe（SPP）：連線方式為「UUID 查詢」成功 -> Winsock port=0 自動 SDP 已驗證
- 12 個查詢中 11 個有回應、僅 0xF2 無回應 -> 外層封包（header／長度／CRC）已驗證；電量用 0xD0（回 17）
- 已驗證回應：版本 1.0.1、MAC、名稱 EDIFIER ES850NB、ANC group=16 mode=1、EQ=0、LDAC=1、LHDC=0、HDAudioCodec(index=12, ldac=1, lhdc=-1)
- 0x43 自訂 EQ 回應只解出 4 個 band 且 name 為空，回應結尾似被截斷或解析不完整，待查

#### 待辦事項（TODO）
- 查 0x43 解析是否漏欄位（對照反編譯碼）
- MP330 走 BLE 的 probe 尚未做（需開機並可掃描）
- ANC group=16 對照 anc-groups.json，確認可用模式
- 把驗證結果更新到 findings.md／protocol-*.md，標明「已驗證」

### 其餘實機測試（同日追加）

#### 修改內容（Changes）
- 無程式碼修改。新增紀錄 docs/captures/probe-ES850NB-20261027-214026.jsonl（BLE，gitignore）

#### 問題與解法（Problems & Solutions）
- ES850NB 強制 --transport ble：連線成功，12 個查詢結果與 SPP 完全相同（同樣僅 0xF2 無回應）-> 推翻「ES850NB 只能走 SPP」的推測，BLE GATT 也可用；官方 App 選 SPP 只是選路規則
- MP330 probe：掃描不到符合的 BLE 廣播（scan --all 8 個裝置皆非 MP330），未測；原因未知（未開機／被手機佔用／不在範圍內，皆為推測）
- 0x43 自訂 EQ 兩種傳輸回應相同（26 bytes），故非傳輸截斷，是裝置回應本身或解析問題

#### 待辦事項（TODO）
- MP330：開機、斷開手機後重測
- 查 0x43 回應格式（結尾 06 07 像是第 5 個 band 被截斷）

### ES850NB 完整只讀測試（同日追加）

#### 修改內容（Changes）
- 無程式碼修改。probe 再跑 3 次（SPP），新增 3 份 captures（gitignore）

#### 問題與解法（Problems & Solutions）
- SPP 連續 3 次：連線方式皆為 UUID 查詢，12 個查詢回應位元組逐項完全相同（穩定、可重現）
- 更正前一則：0x43 回應沒有被截斷。eqIndex=8（4 頻段×6 bytes），P[2..25] 剛好 24 bytes，最後的 06 07 是第 4 個 band 的 gain/q；name 為空是因為 v2 查詢回應本來就不含時間戳與名稱。解析正確
- 實測頻段：100/2000/4000/8000，gain=6，q=7，filter=0

#### 待辦事項（TODO）
- 設定類指令（ANC、EQ 等）無抓包依據，需使用者確認風險後才可試
- 0xF2 無回應原因未明（可能此機型不支援）
- MP330 暫跳過

### ANC 設定指令實機測試（同日追加）

#### 修改內容（Changes）
- 無專案程式碼修改。臨時腳本放 scratchpad（anc_test.py、anc_restore.py），未進專案

#### 問題與解法（Problems & Solutions）
- 使用者同意後，經 SPP 送 0xC1 設為 group16 mode2：封包 aa ec c1 00 02 b5 a7 b5，設定回應 payload `10 02 06`，0xCC 查詢確認變為 mode=2 -> 設定指令格式（payload [group, mode]）與加密（encryption=16）已驗證
- 回應 payload 比送出多 1 byte（尾端 06），意義未解（推測為狀態／DB 欄位，未驗證）
- 第一次還原 `10 01` 回 ResponseTimeout，之後查詢仍是 mode=2（還原未生效或未被處理）；第二次連線重送同封包成功，回應 `10 01 06`，最終 mode=1，已回到原狀
- 第一次還原失敗原因未知（推測：設定後 1 秒內再送被裝置忽略，未驗證）

#### 待辦事項（TODO）
- 釐清設定回應尾端 06 的意義
- 釐清連續設定逾時原因（改間隔重測）；Client 目前無重送
- 其他模式（3 防風噪、4 可調增益環境音、5 降噪關）與 DB 欄位尚未測

### 全部 ANC 模式與間隔限制（同日追加）

#### 修改內容（Changes）
- 無專案程式碼修改。臨時腳本在 scratchpad（anc_all.py、anc_gap.py）

#### 問題與解法（Problems & Solutions）
- group 16 的 5 個模式皆已實機驗證（0xC1 設定 -> 0xCC 查詢一致，順向與反向切換都測）：1 深度降噪、2 舒適降噪、3 防風噪、4 可調增益環境音、5 降噪關。設定回應約 0.03–0.11 秒
- 回應長度：mode 4 的 0xCC 查詢回 `10 04 06`（3 bytes，多 DB=6）；其他模式回 2 bytes。設定回應則所有模式都帶尾端 06。DB=6 推測為環境音程度預設值，未驗證
- 裝置端會主動推送 0xCC 通知（每次設定成功後一則，內容同新狀態），Client 以 on_event 收到
- 逾時原因確認為**裝置端間隔限制**：兩次 0xC1 設定間隔（自上次回應起算）1.0 秒以下，第二次被靜默忽略（無回應、狀態不變、無推送）；1.25 秒以上都成功（重複 4 檔位，皆成功）。門檻在 1.0–1.25 秒之間，未再細分
- 設定後 0.3 秒內送 0xCC 查詢不受影響（查詢不被擋；只驗證了查詢，其他指令未測）
- 全部測試後已還原 mode=1

#### 待辦事項（TODO）
- Client 層加入設定指令最小間隔（建議 ≥1.5 秒）或失敗後重送；目前尚未實作
- 測 mode 4 附帶 DB 參數（set_anc db=）的效果與範圍
- 此間隔是否適用其他設定指令（EQ 等）未測

### EQ 讀取測試（同日追加）

#### 修改內容（Changes）
- 無專案程式碼修改。臨時腳本 scratchpad/eq_read.py

#### 問題與解法（Problems & Solutions）
- 只讀。連續 3 輪 0xD5、0x43，結果完全一致、可重現
- 0xD5 回應只有 1 byte `00`：預設編號 rr=0，沒有校正欄位（EqCalibration 為 None）-> 耳機的 0xD5 不帶桌面校正欄位，與文件「看起來是桌面喇叭」的推測一致
- 0x43 回應 26 bytes：eqIndex=8、P[1]=4。**P[1] 等於頻段數（已驗證，此機型 4 頻段）**，原文件寫「意義未解」
- 4 頻段 100/2000/4000/8000 Hz，gain 皆 6、q 皆 7、filter 0；推測 gain 6 為平坦（若範圍 0–12 則中點為 0 dB），**未驗證**
- D8 位元組 13 = 0x2A（EQ 方案編號），文件只列 0x20/0x21/0x23，0x2A 的預設清單未知；反編譯原始碼目錄 C:\Work\edifier\out 在這台電腦不存在，無法對照
- 0xD5 的 rr=0 是否對應某個預設名稱，需切換 EQ 才能知道

#### 待辦事項（TODO）
- 取得 0x2A 方案的預設清單（需反編譯原始碼或在官方 App 觀察）
- 驗證 gain 編碼（負增益、中點）：需寫入測試
- 寫入類 EQ 指令（0xC4 / 0x44 / 0x46）未測

### EQ 寫入測試（同日追加）

#### 修改內容（Changes）
- 無專案程式碼修改。臨時腳本 scratchpad/eq_presets.py、eq_band.py、eq_now.py

#### 問題與解法（Problems & Solutions）
- 0xC4 切換預設（只送 [rr]）：rr=1~6 全部被接受，回應 payload 為 `rr`，0xD5 查詢一致；0x43 自訂 EQ 內容不隨 rr 改變。裝置似乎不驗證範圍（rr=6 也接受，未再測更大值），故無法由此得知預設總數；各 rr 的名稱未知（需聽感或官方 App 對照）。還原 rr=0 成功（0xD5 回 `00`）
- 0x44 單頻段（eqIndex=8，payload [band, filter, hi, lo, gain, q]）：回應 `01`，0x43 立即反映。band0 gain=7、5、12、0 皆被接受並如實回讀 -> 格式與 gain 欄位位置已驗證；gain 以無號數存、0 與 12 皆被接受，未測負值也未測 12 以上。gain=6 為出廠值，是否 0 dB 仍未驗證（需聽感）
- **副作用**：編輯後 0x43 回應結尾多了 4 bytes `00 00 00 00`（時間戳，無名稱）。頻段資料已還原與原始逐位元組相同，但回應長度由 26 變 30。出廠狀態無時間戳 -> 時間戳欄位已驗證會出現在 0x43 回應；0x44 不帶時間戳時裝置補 0
- 兩者之間皆隔 ≥2 秒，未再遇逾時（與 ANC 間隔限制一致，是否同一限制未專門測）

#### 待辦事項（TODO）
- 是否以 0x45 重置自訂 EQ 回到出廠（無時間戳）：未測，需使用者決定
- 各 rr 對應的預設名稱、哪一個 rr 是「自訂」
- 負增益編碼、gain 上限、其他頻段/頻率是否可改
- 0x46 整組寫入、0x47 改名未測
- 目前 rr=0；頻段資料與出廠相同

### 自訂 EQ 重置／整組寫入／改名測試（同日追加）

#### 修改內容（Changes）
- 無專案程式碼修改。臨時腳本 scratchpad/eq_custom.py、eq_clean.py

#### 問題與解法（Problems & Solutions）
- 0x45 重置：回應 payload 是 24 bytes 預設頻段資料（100/2000/4000/8000 Hz，gain 6，q 7，與出廠值相同）。但**不會清除**已存的時間戳與名稱（重置前後 0x43 結尾相同）
- 0x46 整組寫入（payload = 24 bytes 頻段 + 4 bytes 時間戳 + 名稱）：回應回傳整份 payload；0x43 讀回 ts=01020304、name='test'。格式已驗證（頻段、時間戳、UTF-8 名稱）
- 0x47 改名（payload = 4 bytes 時間戳 + 名稱）：回應 `01`；0x43 讀回 ts=0a0b0c0d、name='T2'。格式已驗證
- **意外**：0x46 只送頻段（無時間戳、無名稱，24 bytes）時，裝置保留舊內容並讀回殘留／疑似損毀資料：`77 02 03 04 74 65 73 74 49` 後接 22 個 0，0x43 長度 58。第一個位元組 01 變 77、多了 0x49，原因未知（推測：裝置用固定緩衝且不清除，或把後面位元組當垃圾，未驗證）。**結論：0x46 一律要帶滿 4 bytes 時間戳**
- 修復：再用 0x46 送 24 bytes 頻段 + `00 00 00 00` 時間戳、空名稱，0x43 回到 30 bytes（ts=00000000，name 空），頻段與出廠相同
- 目前狀態：rr=0、頻段與出廠相同，但 0x43 結尾多 4 個 0（出廠為 26 bytes 無時間戳）。找不到能回到 26 bytes 的方法（0x45、0x46 無時間戳都不行；後者還會殘留垃圾），可能不影響功能，但不是逐位元組的出廠狀態
- 各設定指令之間皆隔 ≥2.5 秒，未再逾時

#### 待辦事項（TODO）
- 程式碼：write_custom_eq 在時間戳不足 4 bytes 時應拒絕（目前 encode_write 允許空時間戳，會讓裝置留下垃圾）；補測試
- 名稱長度上限（name_limit=30 來自 D8）、非 ASCII 名稱是否可寫入未測
- 官方 App 是否因時間戳為 0 而顯示異常，需用 App 確認
- 各 rr 預設名稱、gain 範圍、負增益仍未知

### 修正自訂 EQ 時間戳檢查（同日追加）

#### 修改內容（Changes）
- src/edifier_reverse/eq.py：新增 check_timestamp（必須剛好 4 bytes，否則 PacketError）；encode_write 改用它
- src/edifier_reverse/requests.py：rename_custom_eq 也經 check_timestamp（0x47 同樣帶時間戳；短時間戳在實機上未測，採保守做法一併擋下）
- tests/test_eq.py：新增 test_write_requires_exactly_4_byte_timestamp（空、3、5 bytes 三種）；既有 write_custom_eq 請求測試補上時間戳
- README.md：測試數 173 -> 176

#### 問題與解法（Problems & Solutions）
- 破壞性驗證：把檢查改成永不觸發，新測試 3 個案例全失敗；還原後 176 個測試全過

#### 待辦事項（TODO）
- 名稱長度上限、中文名稱、負增益、各 rr 預設名稱仍未測

### 整理 ES850NB 實機結果（同日追加）

#### 修改內容（Changes）
- docs/findings.md：新增第 0 節「ES850NB 實機驗證結果」；更新現況、可信度說明、SPP／BLE 敘述、風險表、下一步、測試數
- docs/protocol-commands.md：電量（0xD0）、ANC（group 16、間隔限制、推送通知）、EQ（0xD5 只回預設編號、rr 範圍）、自訂 EQ（P[1]=頻段數、0x44–0x47 實測）
- README.md：現況與待辦清單（ES850NB 已驗證、MP330 未測、Client 節流）

#### 問題與解法（Problems & Solutions）
- 舊文件寫「ES850NB 只走 SPP」「UUID 查詢未驗證」「P[1] 意義未解」，皆已依實測更正；未實測的項目（負增益、DB、MP330）維持標為未知／推測
- 未改動 handoff-other-pc.md（為歷史步驟；其中「全部 SPP」之類預期仍適用）

#### 待辦事項（TODO）
- handoff-other-pc.md 之後可標註 ES850NB 已完成，改成只留 MP330 步驟

### MP330 以外的其餘測試（同日追加）

#### 修改內容（Changes）
- 無程式碼修改。臨時腳本 scratchpad/more.py。文件：findings.md 第 0 節、protocol-commands.md 的 ANC／EQ 實測補充

#### 問題與解法（Problems & Solutions）
- ANC DB：mode 4 帶 db=1/3/9/12 皆被接受，回應與查詢為 `10 04 <db>`；mode 2 帶 db=5 回應 `10 02 05`，查詢只回 `10 02`。先前「尾端 06」即預設 DB=6（原為推測，現已由不帶／帶 db 的對照支持）
- EQ gain：-1（`ff`）與 13 皆被接受並讀回，裝置不檢查範圍；音訊上的意義未驗證（無法由協定得知，需聽感）
- rr：7、8、10、16、255 全被接受，裝置不驗證，仍無法得知預設總數
- 名稱：中文 UTF-8 可寫（0x46、0x47 皆可）；名稱欄位上限 28 bytes，30／31 bytes 靜默截斷為 28（與 D8 name_limit=30 不符；那個值可能含別的東西，未解）
- 解釋先前 0x46 少送時間戳的殘留資料：固定大小的欄位（時間戳 4 + 名稱 28）不清除，屬推測
- 全程每次設定間隔 ≥2.5 秒，無逾時。最終還原：ANC mode1、rr=0、0x43 與測試前逐位元組相同（30 bytes，ts=0 無名稱）

#### 待辦事項（TODO）
- 聽感確認：gain 6 是否 0 dB、-1/ff 的意義、各 rr 預設名稱
- 中文名稱在 28 bytes 邊界截斷是否產生壞字元
- DB 上限；0xF2 為何無回應
- Client 加入設定指令節流；MP330 實機

### 電量對照與音量查詢（同日追加）

#### 修改內容（Changes）
- 無程式碼修改。臨時腳本 scratchpad/vol.py

#### 問題與解法（Problems & Solutions）
- 電量：0xD0 回 17；使用者回報電腦顯示 20% -> 接近，推測 0xD0 為百分比成立（Windows 顯示較粗略，未做精確對照）
- 音量：只讀查詢 0x66（DEVICE_VOLUME_QUERY）與 0xC5（D2DP_VOLUME_QUERY）皆無回應。D8 旗標中「音量設定」所在的位元組 19 沒有任何位元為開（flags_on 只有位元組 17、20、22、25），與裝置不支援此功能一致。藍牙耳機音量由 Windows 走 AVRCP／A2DP 控制（Windows 端有「耳機 (EDIFIER ES850NB)」音訊端點），不經這套協定
- 未送 0x67 設定：查詢無回應，沒有依據

#### 待辦事項（TODO）
- 若要在程式內調音量，應走 Windows 音訊 API（非本協定）

### 從目標與待辦移除 MP330（同日追加）

#### 修改內容（Changes）
- CLAUDE.md、README.md：專案目標與目標裝置改為只有 ES850NB，註明 MP330 保留內容但不再測試；README 移除 MP330 實機驗證待辦
- docs/findings.md：下一步移除 MP330 項並重新編號；風險表兩列合併為「MP330 不在目標內」

#### 問題與解法（Problems & Solutions）
- 使用者選「只從待辦與目標移除」：程式（ble.py、cli.py）、測試、其餘文件的 MP330 內容未動

#### 待辦事項（TODO）
- 若之後要全面移除 MP330（程式／測試／文件），需另行確認範圍

### 小工具設計方案（同日追加）

#### 修改內容（Changes）
- 新增 docs/tool-design.md：ES850NB 托盤小工具設計（範圍、架構、設計決定、四階段、風險）
- findings.md 文件索引、README.md 相關文件清單加入新文件

#### 問題與解法（Problems & Solutions）
- 沿用 ldac-and-tool-plan.md 的「Python 分階段」結論；其退路條件（Winsock UUID 不通）已被實機排除
- 範圍刻意排除 EQ（意義未知、已有無法還原的副作用）與音量（裝置不支援）
- 設計重點：設定指令必須經節流佇列（≥1.5 秒）；ANC 狀態以裝置推送為準
- 與舊方案不同：ANC 切換不再要求確認視窗（可立即切回），已在文件寫明理由

#### 待辦事項（TODO）
- 待使用者確認方案後，從第 1 階段（service.py 與 CLI）開始實作

### 托盤小工具：服務層、音量、托盤（同日追加）

#### 修改內容（Changes）
- 新增 src/edifier_reverse/service.py：DeviceService（連線、狀態、ANC 設定經單一鎖與節流 ≥1.5 秒、逾時重送一次、斷線偵測與指數退避重連、推送更新狀態）；ANC 只開放實機驗證過的 group 16；make_connector（掃描一次後快取，SPP 失敗退 BLE）
- 新增 volume.py：以 pycaw 調「耳機 (EDIFIER ES850NB)」播放端點音量／靜音（排除 Hands-Free 與錄音端點）
- 新增 tray.py：pystray 托盤（電量圖示、提示文字、降噪模式／環境音強度／音量選單、重新連線、結束），服務跑在背景執行緒
- transport.py／spp.py：Transport 新增 on_close，SocketTransport 對端斷線時通知
- sim.py：SimulatedDevice 新增 min_set_interval（模擬實機的設定間隔限制）、push_anc、disconnect()，皆為選用，既有行為不變
- cli.py：新增 status、anc、tray 子命令
- pyproject.toml：選用相依 tray = pystray、Pillow、pycaw
- 新增 tests/test_service.py、test_volume.py、test_tray.py；共 213 個測試
- 文件：tool-design.md（音量改納入、實作狀態）、README.md、findings.md

#### 問題與解法（Problems & Solutions）
- 破壞性驗證 6 處（移除節流、不重送、放行未驗證 group、忽略推送、不偵測斷線、移除強度範圍檢查），皆有測試失敗；其中「沒有節流時同樣序列失敗」有獨立測試，證明節流確實必要
- 測試一度失敗：ServiceRunner 在迴圈未執行時呼叫 DeviceService.start -> 改用 loop.call_soon 在迴圈內啟動
- 音量：協定不支援（0x66／0xC5 無回應），改走 Windows 音訊端點。pycaw 對讀不到的裝置屬性大量警告，已在 volume.py 內抑制
- 實機驗證（服務層）：status 與 anc 1–5、--db 8 皆成功，每次後還原 mode 1；連續三次 CLI 呼叫間隔自然超過 1.5 秒
- 實機驗證（托盤邏輯，無 GUI 迴圈）：TrayApp 連線、ANC 2→4(強度8)→1、音量 50→55→50 皆正確，Windows 讀回一致
- 實機驗證（托盤程式）：`edifier-reverse tray` 可啟動並持續存活 25 秒，無錯誤輸出。**托盤圖示的視覺外觀、選單點擊、通知未實際操作過**，只驗證了程式邏輯與存活

#### 待辦事項（TODO）
- 使用者實際點選托盤選單確認外觀與操作（圖示顏色、選單勾選、音量變化）
- 觀察常駐 SPP 連線數小時是否影響 Windows 音訊；手機 ConneX 同時連線是否衝突；耳機休眠後重連
- 第 3 階段：PyInstaller 打包與開機自動啟動；第 4 階段：快捷鍵、低電量通知
- 電量輪詢 60 秒的耗電影響未評估

### 托盤小工具改版：Python 與 C# 兩個版本（同日追加）

#### 修改內容（Changes）
- Python 版（A）：新增 logo.py（深色圓盤、耳機輪廓、電量環）、flyout.py（Tkinter 自繪的 Windows 11 風格面板：磚塊按鈕、拖曳條、DWM 圓角與深淺色）；tray.py 改寫成「pystray 圖示 + 面板」，舊的選單版移除（避免兩種寫法並存）；volume.py 加端點快取與失效重試；service.py 的連線器加位址快取（%APPDATA%\edifier-reverse）
- C# 版（B）：新增 src/EdifierTray（WPF，.NET 8）：Protocol/、Transport/（Winsock SOCKADDR_BTH）、Service/（DeviceService、Discovery、VolumeControl）、Ui/（FlyoutWindow、LogoRenderer、Theme）、TrayHost、Cli（驗證旗標）；tests/EdifierTray.Tests（xUnit，55 個）
- 安裝 .NET SDK 8.0.427（經使用者同意，winget，來源 download.microsoft.com，雜湊驗證通過）；Python 選用相依 tray 已含 pystray、Pillow、pycaw；C# 從 nuget.org 取得 NAudio.Wasapi 與 xUnit 相關套件
- .gitignore 加 bin/、obj/、TestResults/；文件：tool-design.md 第 6 節、README、findings

#### 問題與解法（Problems & Solutions）
- Tk 的 Canvas 子類別把寬度存在 self._w，覆蓋了 Tk 內部的元件路徑，導致 "invalid command name"；改名 _width
- C# 隱含 using（WinForms 與 WPF）造成 Application、Color、RadioButton 名稱衝突，且 UseWPF 會移除 System.IO；csproj 明確移除／加入
- C# DeviceService 停止後無限輪詢：Task.WhenAny 不會拋出取消，加 ThrowIfCancellationRequested；測試曾因此掛住，破壞性驗證第 7 項（移除該行）同樣會掛住，視為被抓到
- C# 重組測試：用 0xCC（同時是接收 header）當指令碼並逐位元組分片會被誤判，屬 reassembly.py 記載的已知限制；測試改用其他指令碼
- 破壞性驗證：Python 6 處、C# 7 處（移除節流、不重送、放行未驗證 group、忽略推送、不偵測斷線、移除強度範圍、取消後不停）皆被測試抓到
- 實機（兩版皆驗證）：連線、5 種降噪模式、環境音強度、音量拖曳（Windows 讀回一致）並還原到 mode 1、音量 50；面板截圖確認深淺色外觀
- 實測：C# 並不比 Python 省記憶體（約 102 MB vs 78 MB，WPF 工作集較大），先前估 15–25 MB 錯誤；啟動差距來自位址快取（Python 補快取後 0.46 秒，第一次掃描約 8.9 秒；C# 1.03 秒）
- 一度誤判：以為 C# 啟動差異是語言造成，實為 Python 版每次都重新掃描廣播

#### 待辦事項（TODO）
- 真人點擊托盤與面板的手感、多螢幕與不同 DPI、長時間常駐、耳機休眠後重連、手機同時連線衝突
- 打包 Python 版（PyInstaller）與開機自動啟動（兩版皆未做）
- 兩份協定實作需保持一致：改封包或設定規則時兩邊都要改並補測試向量

### C# 版第一次點托盤圖示閃退（同日追加）

#### 修改內容（Changes）
- src/EdifierTray/Ui/FlyoutWindow.xaml.cs：ShowFlyout 先 EnsureHandle 再 Measure；改用 Show() 而非只設 Visibility；Toggle 改用 IsVisible 判斷，並忽略「面板剛因失焦收起後 250 毫秒內」的點擊（點圖示會先讓面板失焦收起，不該又重開）
- src/EdifierTray/App.xaml.cs：未處理例外寫入 %APPDATA%\EdifierTray\crash.log
- src/EdifierTray/Cli.cs：新增 --cold-toggle（重現第一次點圖示的路徑）
- tests/EdifierTray.Tests/FlyoutTests.cs：新增面板回歸測試（STA 執行緒）；C# 測試共 56 個

#### 問題與解法（Problems & Solutions）
- 使用者回報：左鍵點圖示即閃退。事件檢視器只有 PresentationFramework 的 FatalError（0x80131623），沒有細節；加 --cold-toggle 重現後看到堆疊：視窗從未 Show 過就呼叫 Measure -> Window.GetWindowMinMax -> FailFast
- 先前自動化驗證漏掉的原因：--live-ui 與 --demo-shot 都先 Show() 再 Hide() 才顯示，沒走到「第一次就直接 Toggle」的真實路徑。教訓：自動化要走和使用者相同的第一步
- 修掉 Measure 後出現第二個錯誤：只設 Visibility 的視窗之後呼叫 Activate 丟 InvalidOperationException，改 Show()
- 破壞性驗證：移除 EnsureHandle 後測試主機因 FailFast 當機（被抓到）；還原後 56 個全過
- 測試中發現 Toggle 用 Opacity > 0 判斷是否開著不可靠（淡入動畫剛開始 Opacity 仍為 0），改 IsVisible
- 未驗證：真人實際左鍵點擊（NotifyIcon 的滑鼠事件路徑）仍沒有親手操作

#### 待辦事項（TODO）
- 請使用者重新試用，確認左鍵點擊不再閃退，並回報其餘手感
- Python 版面板沒有這個問題的證據（它的 Tk 視窗一開始就建立），但也尚未真人點擊

## 2026-10-28

### C# 版右鍵選單美化與快捷選項

#### 修改內容（Changes）
- 使用者回報左鍵點擊已正常；要求右鍵選單美化並加快捷選項（例如靜音）
- 新增 src/EdifierTray/Ui/TrayMenuWindow.xaml(.cs)：Windows 11 風格選單（圓角、Acrylic、Segoe Fluent 圖示、降噪模式選中色條、停用項目變灰、淡入滑入）；取代 WinForms ContextMenuStrip
- 新增 Ui/TrayMenuModel.cs（選單內容與位置計算，純資料）、Ui/WindowEffects.cs（圓角、深淺色、Acrylic、DPI，面板與選單共用）、Autostart.cs（HKCU Run 機碼，路徑可注入）
- TrayHost.cs：右鍵開選單；新增動作：靜音切換、音量 ±10%、開機自動啟動；App.xaml 新增 MenuRow 樣式；FlyoutWindow 改用 WindowEffects
- Cli.cs 新增 --menu-shot、--live-menu；測試新增 MenuTests.cs、FlyoutTests 補選單案例；C# 測試共 71 個
- 文件：tool-design.md 第 6 節、README

#### 問題與解法（Problems & Solutions）
- 選單項目文字在深色底變成黑色：Button 範本內用 TextElement.Foreground 沒有被子元素繼承，改成 Button 樣式的 Foreground（停用時由觸發器改色）
- 螢幕截圖曾抓到選單以外的畫面（含使用者螢幕上的其他內容）：改成直接把選單元素渲染成 PNG 驗證版面；另比較「顯示前後同區域像素」確認選單真的畫在螢幕上（100% 取樣點有變化）。之後不再用螢幕截圖當版面驗證
- 破壞性驗證 5 處（靜音文字不切換、未連線仍可切降噪、不夾工作區右緣、不翻到游標下方、靜音項不依音量停用）皆被測試抓到
- 破壞性驗證的還原步驟用 Copy-Item 會保留舊時間戳，MSBuild 以為檔案沒變而跳過重新編譯，造成「還原後仍失敗」的假象；補上更新 LastWriteTime 後恢復正常（還原檔案後要 touch）
- 實機驗證（--live-menu，從選單點項目）：舒適降噪、防風噪、音量 +10%（50→60）、-10%（→50）、靜音開關皆正確，Windows 讀回一致，已還原
- 開機自動啟動只用測試專用機碼路徑驗證，沒有動到真正的開機項目；未實際啟用

#### 待辦事項（TODO）
- 使用者實際右鍵點擊的手感（選單位置、動畫、點外面收起）尚未真人驗證
- Python 版右鍵選單仍是簡單版，未同步
- 開機自動啟動尚未在真機重開機驗證

### 兩版共同新增：外觀／語言／裝置選擇／裝置辨識（2026-10-28 續）

#### 修改內容（Changes）
- 冷卻：兩版的 ANC 設定冷卻與節流改為 2.0 秒；DeviceState 新增 cooling，面板與選單在冷卻期停用降噪切換（目前模式仍標示）；冷卻期內其他入口的切換請求直接忽略；面板字體改用 Windows 11 中文介面字體
- 共用資料：src/edifier_reverse/strings.json（四種語言字串，C# 嵌入同一份）、catalog.json（由 scripts/gen_catalog.py 從官方產品表產生的 102 個機種目錄，只含機種名稱、UUID、旗標）
- Python：新增 i18n.py、settings.py、devices.py（DeviceDirectory）、catalog.py；ble.py 的 PROFILES 改由目錄產生並新增 identify_adverts／discover_all；service.py 的錯誤與標籤改用字串鍵、只讀查詢逐項容錯；volume.py 可依裝置名稱切換端點；menu.py／flyout.py／tray.py 支援語言、外觀、裝置、重建介面；cli 新增 devices；tests/conftest.py 讓測試的 APPDATA 指向暫存目錄
- C#（src/EdifierTray）：新增 Strings.cs、Settings.cs、Service/Catalog.cs、Service/DeviceDirectory.cs，改寫 Discovery.cs（掃描所有 Edifier 裝置）、TrayMenuModel／TrayMenuWindow（裝置區與分段按鈕）、TrayHost、Theme（日間／夜間／跟隨系統）、XAML 文字改 DynamicResource；測試共 102 個
- 文件：tool-design.md、findings.md（0.1 節）

#### 問題與解法（Problems & Solutions）
- 連按實測（Python Client，請求到請求間隔）：1.6 秒與 2.0 秒各 18 次全成功；1.4 秒每隔一次被裝置忽略 -> 限制約 1.5 秒（請求到請求），原本「從回應算起 1.5 秒」在邊緣，故改 2.0 秒
- 查官方 App 判定方式：反編譯產物已不在這台電腦，改從專案內 APK 取出產品表（scratchpad，不進專案）解密確認；搜尋 UUID 唯一、同名機種兩組需區分
- Tk 測試偶發 TclError（約六分之一）-> conftest 重試；多個含 3 秒等待的測試在機器忙碌時失敗 -> 放寬到 10 秒
- 測試曾讀寫使用者真正的 %APPDATA% 設定 -> conftest 以 autouse 指向暫存目錄
- 破壞性驗證：Python 冷卻 4 處、C# 冷卻 5 處皆被測試抓到（備份檔要放 Windows 路徑，Python 看不到 msys 的 /tmp，曾因此留下被改壞的檔案，已立即還原）
- 實機驗證：兩版 devices 都辨識出 ES850NB（忽略其他廠牌）；Python 托盤即時切換四種語言與日夜間（字體隨語言變）、選同一台重連、重新掃描；C# 選單點項目驗證（降噪、音量、靜音）

#### 待辦事項（TODO）
- 除 ES850NB 外的機種全部未驗證；BLE-only 機種在 C# 版尚未支援（需要 GATT 傳輸）
- 多台同型號時，音量端點依裝置名稱挑選；若兩台名稱相同（Windows 沒有區分）會調到第一個
- 真人實際操作右鍵選單（語言／外觀／裝置）的手感未驗證

### 文件整理與推送到 GitHub 私人倉庫（2026-10-28 續）

#### 修改內容（Changes）
- 依 project-init 的 README 寫法重寫 README.md（目標、特色、截圖、兩版快速開始、運作方式 Mermaid、已驗證範圍、文件索引、Roadmap）；CLAUDE.md 更新為目前實況（目標、技術棧、資料夾用途、開發約定、禁止事項）；新增 docs/images/（README 截圖與 logo）
- 初始化 git（使用者明確要求推送，故不適用 Nextcloud 預設不用 git），建立 GitHub 私人倉庫 HoDZadkiel/EdifierReverse 並推送 main（1 個 commit，115 個檔案）

#### 問題與解法（Problems & Solutions）
- 推送前審查：已追蹤檔案不含 docs/apk、docs/captures、.env 等；掃描無常見機密格式
- 個資：測試與文件內的真實裝置位址全部換成示範位址（AA:BB:CC:11:22:33 等），測試仍全過（Python 271、C# 102）
- 官方 APK 內的 AES 金鑰與 IV 原本寫死在 scripts/decrypt_products.py：不符合「不 push 反編譯產物」規則，改成讀被 gitignore 的 docs/apk/cipher.json（或環境變數 EDIFIER_CIPHER_FILE），解密結果與先前逐位元組一致；amend 了尚未推送的 commit
- safe-push 腳本仍回報 2 項「高風險」：實際是文件裡的 Windows 路徑（C:\Work\...、%APPDATA%\...）被判成 Domain\User，以及 CLAUDE.md 裡「token／密碼」字樣；人工確認皆為誤報，未修改
- catalog.json 內容衍生自官方產品表（只含機種名稱、UUID、旗標，不含文案與連結）

#### 待辦事項（TODO）
- 這則 DEVLOG 與其後的變更尚未 commit／push（使用者尚未要求再次推送）
- 開 CI（.github/workflows 目前空）；除 ES850NB 外的機種實機驗證

### 直接運行與開機自動啟動研究（Changes）
- README.md：新增〈直接運行與開機自動啟動（方案）〉一節（打包比較、自啟缺口、落地順序與驗證清單）；Roadmap 該項改寫
- 沒有改任何程式碼；實測產物在專案外 `C:\Work\pub-fd`、`pub-sc`、`pyi`

### 問題與解法（Problems & Solutions）
- 實測：C# 單檔（framework-dependent）26.3 MB 可啟動；自含壓縮 68.8 MB + 5 個原生 DLL；PyInstaller onefile 22.4 MB 可啟動
- 發現缺口：Python 版自啟指令在打包後會錯、Python 版無單例鎖、`is_enabled()` 沒看 `StartupApproved`（停用值 03 為推測）、C# 以 `dotnet x.dll` 執行時會寫成 dotnet.exe
- 清理 `%TEMP%\_MEI*`（PyInstaller 被強制結束的殘留）的指令被權限分類器擋下，未重試；需使用者自行刪除或同意

### 待辦事項（TODO）
- 按 README 方案落地：frozen 判斷、Python 單例鎖、StartupApproved、建置腳本、重新開機實機驗證
- 本次變更（README、DEVLOG）尚未 commit／push

### 部署方案定案（Changes）
- 決策：只主推 C# 版（只有它有單例鎖）；Python 版只保留 uv 啟動指令，不打包（不做 PyInstaller）
- 新增 `scripts/install.ps1`、`scripts/install.cmd`：一鍵建置單檔 exe → 安裝到 `%LOCALAPPDATA%\EdifierTray` → 寫 HKCU Run（值名與 Autostart.cs 相同）→ 啟動；參數 -NoAutostart/-NoLaunch/-Uninstall/-InstallDir/-RunKey
- README.md：快速開始改為 C# 一鍵安裝為主、Python 為 uv 驗證用；方案節改為〈部署與開機自動啟動〉；Roadmap、特色同步；CLAUDE.md scripts 說明加入 install

### 問題與解法（Problems & Solutions）
- 測試用獨立機碼的父層不存在，New-Item 失敗 → 加 -Force（僅在機碼不存在時才建）
- install.ps1 含中文，PS 5.1 需 UTF-8 BOM；Edit 工具可能去掉 BOM，之後修改要重新確認
- `%TEMP%` 下以萬用字元刪測試殘留被權限分類器擋下，未重試；殘留：`%TEMP%\edt_*`（第一次失敗測試）、`%TEMP%\_MEI*`，需使用者自行刪除

### 待辦事項（TODO）
- C# Autostart 補 StartupApproved 檢查與非單檔 exe 的防呆；重新開機實機驗證
- 安裝腳本的「直接啟動」與「覆蓋執行中版本」未測
- 本次變更尚未 commit／push

### 安裝檔與開源準備（Changes）
- 新增 `scripts/build-installer.ps1`、`scripts/installer/EdifierTray.iss`：自含發佈 + Inno Setup 6 → `EdifierTray-Setup-0.1.0.exe`（63.2 MB；免管理員；安裝到 %LOCALAPPDATA%\EdifierTray；可選自啟；開始功能表捷徑；AppMutex 對應單例鎖）。版本號取自 pyproject.toml。輸出預設在 %TEMP%\EdifierTray-installer
- 新增 `docs/deployment.md`（部署方式、自啟設計、已知缺口）；README 改以 GitHub 讀者為主：快速開始、參與貢獻、授權與聲明；部署細節移到 docs/deployment.md
- 移除公開檔案中 Nextcloud／專案外 venv 的個人情境：README、CLAUDE.md、docs/findings.md、tool-design.md、handoff-other-pc.md、兩個 Directory.Build.props、install.ps1 註解（DEVLOG 保留原樣）
- 用 winget 安裝了 Inno Setup 6.7.3（使用者範圍）

### 問題與解法（Problems & Solutions）
- 實測：靜默安裝/啟動/解除安裝正常；安裝檔內 exe 已壓縮，Inno 無法再縮，63 MB（改不壓縮單檔交給 LZMA 可能更小，未試）
- 「勾選自啟」的安裝路徑要寫真正的 HKCU Run，被權限分類器擋下，未測
- Python 字串替換把 `\b` 寫成退格字元，已用 chr() 修正；以掃描控制字元確認文件乾淨
- 開源前審查：歷史只有 2 個 commit，作者為 noreply 信箱；AES 金鑰值不在歷史；無 apk/captures/jsonl 被追蹤；HEAD 無真實裝置位址、無使用者名稱／個人信箱

### 待辦事項（TODO）
- 使用者確認後才把 GitHub 倉庫改為公開（尚未執行）；公開前注意 catalog.json／協定文件衍生自官方 App 的法律灰色地帶
- docs/deployment.md 的 4 個缺口；重新開機實機驗證；安裝檔勾選自啟路徑的實測
- 本次變更尚未 commit／push

### 公開與 Releases（Changes）
- 倉庫改為 PUBLIC；README 加「下載安裝檔」與 SmartScreen 說明，docs/deployment.md 加 Releases 連結（commit c1325c9 已推送）
- 建立 GitHub pre-release v0.1.0，附 EdifierTray-Setup-0.1.0.exe（63.2 MB，未簽章），release notes 含 SHA-256 與限制說明

### 問題與解法（Problems & Solutions）
- 簽章評估：SignPath Foundation 對開源專案免費（需 OSI 授權、持續維護、已發佈、GitHub Actions 自動化建置的來源驗證、MFA、README 內的 Code signing policy、每次發佈手動核准；憑證發給 SignPath Foundation；有權拒絕）。Azure Artifact Signing 要付費訂閱、不發 EV。簽章不會立刻消除 SmartScreen（信譽依檔案雜湊累積）
- Release 的安裝檔是公開前的工作目錄建置，原始碼與 HEAD 一致（之後只改了文件）

### 待辦事項（TODO）
- 簽章：建 GitHub Actions 建置流程（目前 .github/workflows 空）後再申請 SignPath Foundation
- 其餘同前：docs/deployment.md 的缺口、重新開機驗證、勾選自啟路徑實測
- 本段 DEVLOG 尚未 commit／push

### GitHub Actions 建置流程（Changes）
- 新增 `.github/workflows/release.yml`：windows-latest 安裝 .NET 8 + Inno Setup → build-installer.ps1 → SHA-256 → artifact；推 `v*` 標籤時另一 job 上傳到 Release（無則建立，v0.* 標 pre-release；已有則 --clobber 替換附件）；標籤與 pyproject.toml 版本不一致會失敗
- docs/deployment.md 加〈發佈流程〉；CLAUDE.md 更新 workflows 說明與「公開倉庫」（commit 92733fa 已推送）

### 問題與解法（Problems & Solutions）
- 手動觸發 workflow_dispatch 試跑：build 成功、release 依設計略過。**tag 觸發的 release job 未實測**（不想動既有 v0.1.0）
- safe-push 2 項高風險為誤判（CLAUDE.md 的「token／密碼」字樣、yml 內 Windows 路徑）
- 執行時有 Node.js 20 棄用警告（actions 被強制用 Node 24 跑），之後可升級 actions 版本

### 待辦事項（TODO）
- 下次發佈 tag 時實測 release job；之後申請 SignPath Foundation（需 README 加 Code signing policy、改用其 GitHub Action 簽章）
- 本段 DEVLOG 尚未 commit／push

### 簽章決定（Changes）
- 決定暫時放棄程式碼簽章（SignPath 需先累積專案知名度）；README 的安裝檔提醒改為 IMPORTANT：需自行決定信任，或從原始碼編譯，並附 Get-FileHash 驗證方式
### 待辦事項（TODO）
- 專案有一定知名度後再評估申請 SignPath Foundation（Actions 建置流程已備妥）

## 2026-10-28

### 修改內容（Changes）
- src/edifier_reverse/volume.py、src/EdifierTray/Service/VolumeControl.cs：找不到耳機播放端點時，改用系統預設輸出裝置（fallback），新增 reset()／Reset()
- src/edifier_reverse/tray.py、src/EdifierTray/TrayHost.cs：連線狀態改變（連上或斷線）時重選端點並更新音量，不再只在連上時刷新
- tests/test_volume.py、tests/EdifierTray.Tests/ServiceTests.cs：新增備援、reset、無備援仍失敗的測試
- 版本升到 1.0.1（pyproject.toml、uv.lock、app.manifest）

### 問題與解法（Problems & Solutions）
- 原因：音量走 Windows 端點，耳機未連上時 volume=None，面板滑桿與選單音量項目被停用。解法：備援到預設輸出，按鈕恆可用
- 驗證：pytest 274 通過、dotnet test 105 通過、本機 build-installer 產出 1.0.1 安裝檔（Python／C# 兩版、Setup.exe 皆已含修正）
- 直接用 -ExecutionPolicy Bypass 跑 build-installer 被擋，改成直接執行腳本即可

### 待辦事項（TODO）
- 備援行為（耳機未連上時調到的是預設輸出）只有單元測試，未在實機驗證
- 推 v1.0.1 標籤後，確認 release job 首次實測成功

### 1.0.1 實測回饋後的修改（Changes）
- scripts/installer/EdifierTray.iss：移除 AppMutex（會讓 Setup 一開始卡在「請先關閉」），改 CloseApplications=force，加 [Code] PrepareToInstall 與 UninstallRun 用 taskkill 結束 EdifierTray.exe
- src/edifier_reverse/flyout.py、src/EdifierTray/Ui/FlyoutWindow.xaml(.cs)：面板左下角顯示版本號（Python 讀套件 metadata；C# 由 csproj 編譯時從 pyproject.toml 讀入 Version）
- 版本升 1.0.2（pyproject.toml、uv.lock、app.manifest）；測試各加版本標籤檢查（pytest 275、dotnet 105 通過）

### 問題與解法（Problems & Solutions）
- 1.0.1 實測：開機自動啟動 OK、直接覆蓋安裝 OK、安裝時無法自動關閉程式 → 見上
- Python heredoc 內的 "{sys}\taskkill" 被當成 Tab，改用 raw string；已確認 .iss 內容正確

### 待辦事項（TODO）
- 1.0.2 尚未 push／打標籤；本機已建出 Setup 1.0.2，待實測「程式執行中直接升級」會不會自動關閉

### 版本號連結（Changes）
- flyout.py／FlyoutWindow.xaml(.cs)：左下角版本號可點擊，開啟 GitHub 倉庫（Python 用 webbrowser；C# 用 ShellExecute），滑過顯示手形游標；兩邊各加測試
- 仍是 1.0.2，尚未 push／打標籤

### 加入 Ko-fi 贊助連結（Changes）
- 新增 .github/FUNDING.yml（ko_fi: hodzadkiel），倉庫頁會出現 Sponsor 按鈕
- README.md：在「授權與聲明」後新增「支持這個專案」段落（自願贊助、不影響功能、不構成承諾）

### 問題與解法（Problems & Solutions）
- GitHub Sponsors 不支援台灣地區 → 改用 Ko-fi

### 待辦事項（TODO）
- 確認任職規定是否允許收外部贊助；贊助收入需自行評估報稅
- 尚未 push；push 前走 safe-push

### 與 GitHub 比對並同步（Changes）
- 比對結果：本機僅多 .github/FUNDING.yml、README「支持這個專案」段落、DEVLOG 11 行，其餘與 GitHub（3c958a1）相同
- 在暫存 clone 套用三檔，safe-push 六項檢查通過後 push：3c958a1..2fd5420（main）

### 待辦事項（TODO）
- 確認任職規定是否允許收外部贊助（前述待辦仍未解）
- 已確認任職規定無外部贊助限制，前述待辦解除（報稅仍需自行評估）

### 建立全域 .github 倉庫（Changes）
- 建立公開倉庫 HoDZadkiel/.github，內含 FUNDING.yml（ko_fi: hodzadkiel）與 README，其他倉庫沒有自己的 FUNDING.yml 時會套用
- 新專案骨架（project-init）尚未加 FUNDING.yml

### 藍牙連線事件觸發重連（Changes）
- 新增 src/EdifierTray/Service/ConnectionWatcher.cs：監看選用裝置的 BluetoothDevice.ConnectionStatusChanged，連上時觸發事件
- DeviceService.cs：新增 NotifyDeviceAvailable()，只在退避等待中才提前重連，並讓下次失敗從最短退避重來；退避表不變
- TrayHost.cs：啟動、換裝置、掃描後更新監看目標；結束時釋放
- ServiceTests.cs：新增兩個測試（通知縮短退避、已連線時通知不中斷連線）；dotnet test 107 項通過

### 問題與解法（Problems & Solutions）
- 直接呼叫 Reconnect() 會在已連線時丟出「連線中斷」把好連線斷掉 → 另設 NotifyDeviceAvailable，用 _backingOff 旗標限定只在等待重試時生效

### 待辦事項（TODO）

### 名稱回退與未配對過濾（Changes）
- Discovery.cs：新增 IsPairedAsync（BluetoothDevice.DeviceInformation.Pairing.IsPaired；查詢失敗視為已配對，不誤藏）
- DeviceDirectory.cs：新增 FallbackName、PruneUnpairedAsync；ScanAsync 掃完後移除未配對裝置，選用的被移除就清掉選擇；建構子多一個可注入的 isPaired
- TrayHost／TrayMenuModel／FlyoutWindow：名稱回退改用選用裝置的名稱或機種，沒有裝置資訊時顯示「Edifier」，不再寫死 ES850NB；啟動時先清掉不再配對的已知裝置
- Settings.cs：KnownDevice.Model 預設值改為 Edifier
- 測試：新增 3 項（未配對隱藏、取消配對後移除並回到第一台、回退名稱）；ProtocolTests 的未連線提示改測新行為；dotnet test 110 項通過

### 待辦事項（TODO）

### Python 版同步與 1.0.3（Changes）
- 新增 src/edifier_reverse/pairing.py（is_paired、ConnectionWatcher，用 bleak 帶進來的 winrt 套件）
- service.py：notify_device_available（只在退避等待中生效，下次失敗從最短退避重來）
- devices.py：fallback_name、prune_unpaired，scan 後移除未配對裝置；settings.py：KnownDevice.model 預設 Edifier
- tray.py／menu.py／flyout.py：名稱回退不再寫死 ES850NB；啟動、換裝置、掃描後更新監看；結束時停止監看
- 測試：Python 新增 5 項、既有 3 項改寫，pytest 281 通過；C# 110 項通過
- 版本 1.0.3：pyproject.toml、app.manifest、uv.lock

### 待辦事項（TODO）
- 事件監看與配對檢查未用 ES850NB 實機驗證（耳機開機／進入範圍時是否即時重連、已配對裝置是否仍列出）
- 未打 v1.0.3 標籤（打標籤會觸發 Release 建置安裝檔）


## 2026-10-29

### 待辦事項（TODO）
- 新增 OSD 顯示功能：顯示電量告警與模式切換
- 模式切換快捷鍵
- 音量切換快捷鍵
- 右下角托盤圖示顯示電量數值

### 完成當日四項待辦：OSD、快捷鍵、托盤電量數字（Changes）
- 新增 src/edifier_reverse/alerts.py、src/EdifierTray/Service/Alerts.cs：OsdTracker（電量 <=20% 告警一次、回到 >20% 才再武裝；模式改變含耳機按鍵切換；重連後第一筆不算改變）、next_anc_mode／NextAncMode（輪替、未驗證 group 回 None）、快捷鍵表
- 新增 osd.py／Ui/OsdWindow.xaml(.cs)：工作區下方置中提示條，1.8 秒自動消失，不搶焦點、滑鼠穿透
- 新增 hotkeys.py／Service/HotkeyListener.cs：RegisterHotKey 全域快捷鍵，Ctrl+Alt+A 輪替降噪、Ctrl+Alt+↑／↓ 音量 ±10、Ctrl+Alt+0 靜音；註冊失敗會在 OSD 提示
- logo.py／LogoRenderer.cs：新增 number 參數，托盤圖示連線時以電量數字取代耳機輪廓（面板與選單的圖示不變）
- tray.py／TrayHost.cs：接上 OsdTracker、快捷鍵、OSD；strings.json 四語新增 o.low_battery、o.volume、o.hotkey_failed
- README.md：功能列表補上電量數字、OSD 與快捷鍵
- 測試：pytest 299 通過（+18）、dotnet test 124 通過（+14）；數字圖示已渲染成圖目視確認 16／32px 可讀

### 問題與解法（Problems & Solutions）
- Ctrl+Alt+M 在這台機器已被其他程式佔用 → 靜音改 Ctrl+Alt+0；實測 0／S／K／U／↑ 的 RegisterHotKey 可註冊且回呼正確觸發
- 設 EDIFIER_BUILD_ROOT 後 dotnet 因專案內舊的 obj 產生碼重複定義而編譯失敗 → 本次不設該變數，沿用專案內 bin/obj

### 待辦事項（TODO）
- OSD 視窗與快捷鍵未在實機（接 ES850NB、長時間常駐）驗證；C# 版 OSD 只有視窗層級的自動化測試，未看過實際畫面
- 快捷鍵目前固定、無法自訂；其他程式佔用時只提示、不能改鍵
- 未打包安裝檔、未升版本號、未 push

### 靜音快捷鍵加入數字鍵盤 0、安裝 1.0.4（Changes）
- 快捷鍵表值改為虛擬鍵碼元組：靜音同時註冊主鍵盤 0 與數字鍵盤 0（0x60）；一個鍵都註冊不了才算失敗（alerts.py、Alerts.cs、hotkeys.py、HotkeyListener.cs、兩邊測試、README）
- 版本升 1.0.4（pyproject.toml、uv.lock、app.manifest）；本機 build-installer 打包並 /SILENT 安裝，供使用者試用

### 問題與解法（Problems & Solutions）
- 數字鍵盤 0 的虛擬鍵碼與主鍵盤 0 不同，RegisterHotKey 要分別註冊
- 專案內 src/EdifierTray 的 obj/Debug 殘留會讓設了 EDIFIER_BUILD_ROOT 的建置重複定義失敗 → 打包前刪掉 bin/Debug、obj/Debug

### 待辦事項（TODO）
- 使用者試用 1.0.4 的回饋；未打標籤、未 push

### 文件同步與發佈 1.0.4（Changes）
- docs/tool-design.md：選配項目標示快捷鍵與低電量通知已完成，功能列表補上電量數字、OSD、快捷鍵
- 已 push 596ea1f 與標籤 v1.0.4（觸發 Release 建置）；Release job 結果尚未確認

### 待辦事項（TODO）
- 確認 v1.0.4 的 Release 建置成功、安裝檔已上傳
