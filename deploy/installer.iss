; Huaxiazi Inno Setup installer script
; Builds Huaxiazi-Setup.exe from out\publish\win-x64\
;
; Usage:
;   1) Publish the self-contained build first (publish.ps1 or dotnet publish).
;   2) Compile with Inno Setup Compiler:  iscc deploy\installer.iss
;
; The resulting setup is placed in release\Huaxiazi-Setup.exe.

#define MyAppName      "话匣子"
#define MyAppVersion   "2.0.0"
#define MyAppPublisher "Huaxiazi"
#define MyAppExeName   "Huaxiazi.exe"

[Setup]
; AppId is a stable GUID used for upgrade/ uninstall identification.
AppId={{8F4C9B2D-1A6E-4C3B-9F7A-2D5E8C0B1A34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
SetupIconFile=..\Resources\Brand\Huaxiazi.ico
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\release
OutputBaseFilename=Huaxiazi-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Install per-user by default (no admin prompt); uninstall key goes to HKCU.
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
; Inno writes the uninstall registry entry automatically under
; HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Keep the contributed translation in the repository so local and CI builds
; do not depend on files installed on a particular developer machine.
Name: "chinesesimplified"; MessagesFile: ".\Languages\ChineseSimplified.isl"

[Files]
; Source is relative to this .iss file (deploy\), so the published output is
Source: "..\out\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
