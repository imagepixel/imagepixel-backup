; Setup program for Imagepixel Backup. Built by build-installer.ps1 with Inno Setup 6.
; It installs the application with everything it needs: the .NET runtime is part of
; the published files, restic is included, and WinFsp is installed if it is missing.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef WinFspMsi
  #define WinFspMsi "winfsp-2.1.25156.msi"
#endif

#define AppName "Imagepixel Backup"
#define AppExe "ImagepixelBackup.exe"

[Setup]
AppId={{B0A3E7C4-5D1F-4E0B-9A77-2C6F4D1E8A35}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Imagepixel
AppPublisherURL=https://github.com/imagepixel/imagepixel-backup
AppSupportURL=https://github.com/imagepixel/imagepixel-backup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; No licence page: the application asks once at its first start to confirm the
; notes on use and liability, the same way on every system.
OutputDir=..\artifacts\installer
OutputBaseFilename=ImagepixelBackup-Setup-{#AppVersion}
SetupIconFile=..\src\ImagepixelBackup.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; WinFsp contains a driver, which needs administrator rights to install
PrivilegesRequired=admin
MinVersion=10.0.17763
; the application holds this mutex while it runs, so setup can ask to close it
AppMutex=ImagepixelBackup.SingleInstance
CloseApplications=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.AutoStart=Beim Start von Windows im Hintergrund starten (für automatische Sicherungen)
english.AutoStart=Start in the background when Windows starts (for automatic backups)
german.Options=Optionen:
english.Options=Options:
german.InstallingWinFsp=WinFsp wird installiert (zeigt das Backup als Laufwerk an) ...
english.InstallingWinFsp=Installing WinFsp (shows the backup as a drive) ...

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Options}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NUTZUNGSHINWEISE.md"; DestDir: "{app}"; Flags: ignoreversion
; the unmodified installer of the WinFsp project
Source: "..\.build\winfsp\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ImagepixelBackup"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\{#WinFspMsi}"" /qn /norestart"; StatusMsg: "{cm:InstallingWinFsp}"; Check: NeedsWinFsp; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// WinFsp records its installation folder in the 32-bit view of the registry.
function NeedsWinFsp: Boolean;
var
  InstallDir: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', InstallDir) then
    Result := not FileExists(AddBackslash(InstallDir) + 'bin\winfsp-x64.dll');
end;
