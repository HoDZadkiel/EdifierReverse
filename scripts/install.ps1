# 一鍵安裝 C# 托盤小工具：建置單檔 exe -> 複製到安裝資料夾 -> 設定開機自動啟動 -> 啟動。
# 需要 .NET 8 SDK（winget install Microsoft.DotNet.SDK.8）。不需要系統管理員權限。
#   scripts\install.cmd                雙擊即可（呼叫本腳本）
#   scripts\install.ps1 -Uninstall     移除 exe 與開機項目（設定檔 %APPDATA%\EdifierTray 保留）
param(
    [switch]$Uninstall,
    [switch]$NoAutostart,
    [switch]$NoLaunch,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'EdifierTray'),
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'   # 測試時可換成獨立機碼
)

$ErrorActionPreference = 'Stop'
$valueName = 'EdifierTray'   # 與 src/EdifierTray/Autostart.cs 的 ValueName 相同，選單的開關才會對得上
$exe = Join-Path $InstallDir 'EdifierTray.exe'

function Stop-Installed {
    Get-Process -Name EdifierTray -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
}

function Remove-Autostart {
    if (Test-Path $RunKey) { Remove-ItemProperty -Path $RunKey -Name $valueName -ErrorAction SilentlyContinue }
}

if ($Uninstall) {
    Stop-Installed
    Remove-Autostart
    if (Test-Path $exe) { Remove-Item $exe -Force }
    # 只刪我們放的檔案；資料夾若還有別的東西就不動它
    if ((Test-Path $InstallDir) -and -not (Get-ChildItem $InstallDir -Force)) { Remove-Item $InstallDir -Force }
    Write-Host "已移除。設定檔仍在 $env:APPDATA\EdifierTray（要清掉請自行刪除）。"
    return
}

$sdk = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $sdk -or -not ((& dotnet --list-sdks) -match '^8\.')) {
    throw "找不到 .NET 8 SDK。請先執行：winget install Microsoft.DotNet.SDK.8"
}

$repo = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $env:TEMP 'EdifierTray-publish'
$env:EDIFIER_BUILD_ROOT = Join-Path $env:TEMP 'EdifierTray-build'   # bin/obj 放暫存資料夾，不污染原始碼目錄

Write-Host '建置中 ...'
& dotnet publish (Join-Path $repo 'src\EdifierTray') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $staging --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗（結束碼 $LASTEXITCODE）" }

Stop-Installed
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item (Join-Path $staging 'EdifierTray.exe') $exe -Force
Write-Host "已安裝到 $exe"

if ($NoAutostart) {
    Remove-Autostart
} else {
    if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }   # 只在機碼不存在時才建立
    Set-ItemProperty -Path $RunKey -Name $valueName -Value "`"$exe`""   # 路徑加引號，含空白也安全
    Write-Host '已設定開機自動啟動。'
}

if (-not $NoLaunch) { Start-Process $exe; Write-Host '已啟動，請看工作列右下角的托盤圖示。' }
