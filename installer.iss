#define MyAppName "Codex Usage Overlay"
#define MyAppVersion "1.2.1"
#define MyAppExeName "CodexUsageOverlay.exe"

[Setup]
AppId={{D2B9D79A-9A45-4A32-85A4-F49D40E4D594}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Codex Usage Overlay
DefaultDirName={localappdata}\Programs\Codex Usage Overlay
DefaultGroupName=Codex Usage Overlay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=CodexUsageOverlay-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=installer-assets\app-icon.ico
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "bin\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer-assets\usage-cache.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\Codex 用量显示"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\显示设置"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"
Name: "{userdesktop}\Codex 用量显示"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userstartup}\Codex Usage Overlay"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 Codex 用量显示"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden skipifdoesntexist
