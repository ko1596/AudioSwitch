; Inno Setup 腳本。建置: ISCC.exe /DAppVersion=1.1.0 installer\AudioSwitch.iss  (先執行 build.bat)
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{B5A9C1D2-7E3F-4A6B-9C0D-5E1F2A3B4C5D}
AppName=AudioSwitch
AppVersion={#AppVersion}
AppPublisher=ko1596
AppPublisherURL=https://github.com/ko1596/AudioSwitch
AppSupportURL=https://github.com/ko1596/AudioSwitch/issues
; 安裝到使用者自己的資料夾, 不需要系統管理員權限
PrivilegesRequired=lowest
DefaultDirName={autopf}\AudioSwitch
DisableProgramGroupPage=yes
OutputDir=..\dist-installer
OutputBaseFilename=AudioSwitch-Setup-{#AppVersion}
SetupIconFile=..\src\icon.ico
UninstallDisplayIcon={app}\AudioSwitch-Tray.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "開機時自動啟動托盤圖示 / Start the tray icon when Windows starts"

[Files]
Source: "..\dist\AudioSwitch-*.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AudioSwitch Profile Editor"; Filename: "{app}\AudioSwitch-Editor.exe"
Name: "{autoprograms}\AudioSwitch Tray"; Filename: "{app}\AudioSwitch-Tray.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AudioSwitchTray"; ValueData: """{app}\AudioSwitch-Tray.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\AudioSwitch-Tray.exe"; Description: "立即啟動 AudioSwitch / Start AudioSwitch now"; Flags: nowait postinstall skipifsilent

[Code]
// 更新或解除安裝前先關掉執行中的托盤程式, 否則檔案被佔用
procedure StopTray;
var R: Integer;
begin
  Exec('taskkill.exe', '/F /IM AudioSwitch-Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopTray;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopTray;
end;
