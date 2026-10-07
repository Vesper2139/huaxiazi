; Huaxiazi Inno Setup installer script
; Builds Huaxiazi-Setup.exe from out\publish\win-x64\
;
; Usage:
;   1) Publish the self-contained build first (publish.ps1 or dotnet publish).
;   2) Compile with Inno Setup Compiler:  iscc deploy\installer.iss
;
; The resulting setup is placed in release\Huaxiazi-Setup.exe.

#if Ver < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 or newer is required for security-hardened release builds.
#endif

#define MyAppName      "话匣子"
#define MyAppPublisher "Huaxiazi"
#define MyAppExeName   "Huaxiazi.exe"

#ifndef MySourceDir
  #define MySourceDir "..\out\publish\win-x64"
#endif

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString(AddBackslash(MySourceDir) + MyAppExeName)
#endif

[Setup]
; AppId is a stable GUID used for upgrade/ uninstall identification.
AppId={{8F4C9B2D-1A6E-4C3B-9F7A-2D5E8C0B1A34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
SetupIconFile=..\Resources\Brand\Huaxiazi.ico
AppPublisher={#MyAppPublisher}
; Per-user install matches the working historical releases and avoids UAC
; virtualization or an elevated setup process changing the user's TEMP token.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=no
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
OutputDir=..\release
OutputBaseFilename=Huaxiazi-Setup
; Keep payload outside the setup executable: this avoids the temporary
; is-*.tmp self-extraction path that has failed under restricted TEMP policies.
; Ship Huaxiazi-Setup.exe together with all generated Huaxiazi-Setup-*.bin files.
UseSetupLdr=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Do not elevate: the default target is writable by the current user.
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
; Source can be overridden by publish.ps1 with /DMySourceDir=...
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
