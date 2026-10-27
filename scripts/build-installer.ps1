# 產生 Setup.exe：自含發佈（使用者不需要安裝 .NET）-> 用 Inno Setup 打包。
# 需要 .NET 8 SDK 與 Inno Setup 6（winget install JRSoftware.InnoSetup）。
#   scripts\build-installer.ps1 [-OutDir <資料夾>]
param(
    [string]$OutDir = (Join-Path $env:TEMP 'EdifierTray-installer')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw '找不到 Inno Setup 6。請先執行：winget install JRSoftware.InnoSetup' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not ((& dotnet --list-sdks) -match '^8\.')) {
    throw '找不到 .NET 8 SDK。請先執行：winget install Microsoft.DotNet.SDK.8'
}

# 版本號跟 pyproject.toml 同一份，兩邊不各自維護
$version = (Select-String -Path (Join-Path $repo 'pyproject.toml') -Pattern '^version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value

$publish = Join-Path $env:TEMP 'EdifierTray-publish-sc'
$env:EDIFIER_BUILD_ROOT = Join-Path $env:TEMP 'EdifierTray-build-sc'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "建置 $version（自含單檔）..."
& dotnet publish (Join-Path $repo 'src\EdifierTray') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗（結束碼 $LASTEXITCODE）" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host '打包安裝檔 ...'
& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DIconFile=$(Join-Path $repo 'src\EdifierTray\Assets\app.ico')" `
    "/O$OutDir" (Join-Path $PSScriptRoot 'installer\EdifierTray.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC 失敗（結束碼 $LASTEXITCODE）" }

$setup = Join-Path $OutDir "EdifierTray-Setup-$version.exe"
Write-Host ("完成：{0}（{1:N1} MB）" -f $setup, ((Get-Item $setup).Length / 1MB))
