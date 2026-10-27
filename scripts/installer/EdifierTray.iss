; Inno Setup 腳本：把自含發佈的 EdifierTray 包成 Setup.exe。由 scripts/build-installer.ps1 呼叫，
; 參數 /DAppVersion=、/DPublishDir=、/DIconFile= 由它傳入。
; 安裝位置與開機項目的名稱都和 scripts/install.ps1、src/EdifierTray/Autostart.cs 一致。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F0D6B7E-3C3E-4B0B-9A52-5D2E8E1F7C41}
AppName=Edifier Tray
AppVersion={#AppVersion}
AppPublisher=EdifierReverse
DefaultDirName={localappdata}\EdifierTray
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\EdifierTray.exe
OutputBaseFilename=EdifierTray-Setup-{#AppVersion}
; 升級時自動關閉執行中的 EdifierTray，不再停在「請先關閉」的提示
; （AppMutex 會讓 Setup 一開始就卡住等使用者手動關，所以不用；改由下面的 PrepareToInstall 強制結束）
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start Edifier Tray when I sign in to Windows"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\Edifier Tray"; Filename: "{app}\EdifierTray.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EdifierTray"; \
    ValueData: """{app}\EdifierTray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\EdifierTray.exe"; Description: "Launch Edifier Tray"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM EdifierTray.exe"; Flags: runhidden; RunOnceId: "StopEdifierTray"

[Code]
// 安裝前結束執行中的 EdifierTray（托盤程式沒有一般視窗，Restart Manager 的關閉要求常常無效）
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM EdifierTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
