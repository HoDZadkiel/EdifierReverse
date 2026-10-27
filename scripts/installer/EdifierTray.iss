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
; 程式執行中時，安裝／解除安裝會先要求關閉（單例鎖的名稱，與 App.xaml.cs 相同）
AppMutex=EdifierTray.SingleInstance

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
