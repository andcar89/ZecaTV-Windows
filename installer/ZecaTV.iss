; ZecaTV installer script, for Inno Setup (free): https://jrsoftware.org/isdl.php
;
; Two ways to build it:
;   1. Run build-installer.ps1 (publishes the app, then compiles this script), or
;   2. Run publish.ps1 yourself first, then open this file in the Inno Setup app and click Compile.
;
; This script only packages what's already in ..\publish\win-x64 — it doesn't build ZecaTV itself,
; so the publish step always has to happen first (both scripts above do that for you).

#define MyAppName "ZecaTV"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "Ze"
#define MyAppExeName "ZecaTV.exe"
#define MyPublishDir "..\publish\win-x64"

[Setup]
; Don't change this once a version has been installed somewhere — Inno Setup uses it to recognize
; an upgrade (rather than a separate side-by-side install) and to know what to remove on uninstall.
AppId={{152A3F07-E3AD-40E7-BB03-046510673977}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Installs for the current user only, under their own AppData — no admin prompt, matching where
; ZecaTV already keeps its own settings (see SettingsStore.AppDataDir).
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=ZecaTV-Setup-{#MyAppVersion}
SetupIconFile=..\ZecaTV\Assets\ZecaTV.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; %AppData%\ZecaTV (your playlists, favorites, settings — see SettingsStore.AppDataDir) is
; deliberately left alone on uninstall, so removing/reinstalling ZecaTV never loses your data.
; Delete that folder by hand afterward if you want a completely clean slate.
