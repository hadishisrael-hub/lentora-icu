; Lentora ICU per-user installer (Inno Setup 6). Built by scripts\package.ps1, which passes
; /DAppVersion=<x.y.z> /DSourceDir=<publish folder> /DOutputDir=<folder>.
#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{6E1B7C2A-4F3D-4B8E-9A61-2C5D7E0F9A14}
AppName=Lentora ICU
AppVersion={#AppVersion}
AppVerName=Lentora ICU {#AppVersion}
AppPublisher=Lentora
AppCopyright=Copyright (C) 2026 Daniel / Lentora. AGPL-3.0-or-later.
DefaultDirName={autopf}\Lentora ICU
DefaultGroupName=Lentora ICU
DisableProgramGroupPage=yes
; No administrator rights needed to install. Installing an ICC into Windows asks for elevation at that step.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=LentoraICU-Setup-{#AppVersion}
SetupIconFile=..\PrinterColorProfiler\Assets\Brand\icu.ico
UninstallDisplayIcon={app}\PrinterColorProfiler.exe
UninstallDisplayName=Lentora ICU
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes

[Languages]
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Lentora ICU"; Filename: "{app}\PrinterColorProfiler.exe"
Name: "{group}\{cm:UninstallProgram,Lentora ICU}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Lentora ICU"; Filename: "{app}\PrinterColorProfiler.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PrinterColorProfiler.exe"; Description: "{cm:LaunchProgram,Lentora ICU}"; Flags: nowait postinstall skipifsilent

; Profiles in %LOCALAPPDATA%\PrinterColorProfiler and ICC files installed into Windows are user data
; and are intentionally left in place on uninstall.
