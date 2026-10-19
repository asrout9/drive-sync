; Inno Setup Script for DriveSync - Two-Way Synchronizer for Windows
; Defines full modern installer with icons, AppUserModelId for taskbar grouping, shortcuts, and auto-start support.

#define MyAppName "DriveSync"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DriveSync Technologies"
#define MyAppURL "https://github.com/drivesync/drivesync"
#define MyAppExeName "DriveSync.App.exe"
#define MyAppId "{{8F3D9EB9-168C-4804-A537-0547343B8ADE}"
#define MyAppUserModelId "DriveSync.TwoWaySynchronizer.1.0"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist\installer
OutputBaseFilename=DriveSync-Setup-v1.0
SetupIconFile=..\src\DriveSync.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=force
CloseApplicationsFilter=*.exe
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=DriveSync - Two-Way Synchronizer Installer
VersionInfoCopyright=Copyright (C) 2026 DriveSync Team
VersionInfoProductName=DriveSync
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Launch DriveSync automatically when Windows boots (minimized to system tray)"; GroupDescription: "System Integration:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut with explicit AppUserModelID for Windows 10/11 taskbar & Start pinning
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelId}"; Comment: "DriveSync Two-Way Folder, Cloud, and SSD Synchronizer"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
; Desktop shortcut
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "{#MyAppUserModelId}"; Comment: "DriveSync Two-Way Folder, Cloud, and SSD Synchronizer"

[Registry]
; Optional autostart entry in CurrentUser Run key
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DriveSync"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
