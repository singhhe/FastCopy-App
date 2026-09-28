; Inno Setup script for FastCopy. Compile with ISCC.exe (Inno Setup 6).
; Expects the self-contained win-x64 publish output at ..\publish\FastCopy\

#define MyAppName "FastCopy"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Heera"
#define MyAppExeName "FastCopy.App.exe"
#define MyAppURL "https://www.thefastcopy.com"

[Setup]
AppId={{6C6E7D1E-6A9C-4F1A-9C2C-6B7B7E9B7B10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; No code-signing certificate yet - unsigned installers trigger a Windows SmartScreen warning
; ("Windows protected your PC") on first run. Worth revisiting once/if a cert is obtained.
OutputDir=..\publish\installer
OutputBaseFilename=FastCopy-Setup-{#MyAppVersion}
SetupIconFile=..\src\FastCopy.App\Assets\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\FastCopy\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
