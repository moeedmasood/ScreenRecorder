; Inno Setup 6 script. Build with installer\build.ps1 (passes AppVersion, SourceDir, OutputDir).
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6F3C2A55-8E0B-4C1A-9C57-5D5B3E1F7A21}
AppName=Screen Recorder
AppVersion={#AppVersion}
AppPublisher=ScreenRecorder
DefaultDirName={autopf}\Screen Recorder
DefaultGroupName=Screen Recorder
OutputDir={#OutputDir}
OutputBaseFilename=ScreenRecorder-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\ScreenRecorder.App.exe
LicenseFile={#SourceDir}\EULA.md
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Screen Recorder"; Filename: "{app}\ScreenRecorder.App.exe"
Name: "{autodesktop}\Screen Recorder"; Filename: "{app}\ScreenRecorder.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\ScreenRecorder.App.exe"; Description: "Start Screen Recorder"; Flags: nowait postinstall skipifsilent

; User data (settings, licence, logs, recordings) is deliberately left in place on uninstall.
