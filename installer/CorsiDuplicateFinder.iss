; Inno Setup script for CorsiDuplicate Finder.
; Packages the self-contained publish output (src/CorsiDuplicate.UI publish-installer folder)
; so the target machine needs nothing pre-installed — no .NET runtime, no ffmpeg on PATH.
;
; Build:
;   1. dotnet publish src/CorsiDuplicate.UI/CorsiDuplicate.UI.csproj -c Release -r win-x64 --self-contained true -o publish-installer
;   2. "ISCC.exe" installer\CorsiDuplicateFinder.iss
;   Output lands in installer\Output\CorsiDuplicateFinderSetup.exe

#define MyAppName "CorsiDuplicate Finder"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Ricardo Corsi Paulino Cesar"
#define MyAppExeName "CorsiDuplicate.UI.exe"
#define MyPublishDir "..\publish-installer"

[Setup]
AppId={{E6C9C6A5-3E0E-4C1B-9C5A-6B2C1E2C6A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=CorsiDuplicateFinderSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
SetupIconFile=..\src\CorsiDuplicate.UI\app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
