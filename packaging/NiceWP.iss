#define MyAppName "NiceWP"
#define MyAppVersion "0.0.17"
#define MyAppPublisher "NiceWP Team"
#define MyAppURL "https://github.com/NiceWP"
#define MyAppExeName "NiceWP.App.exe"
#define MyDistSource "..\\.artifacts\\dist\\NiceWP-0.0.17-win-x64"

[Setup]
AppId={{E680E74C-0F3E-4BC3-8FA9-8E9F6DF6740B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\\.artifacts\\installer
OutputBaseFilename=NiceWP-0.0.17-win-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\NiceWP.App\Assets\Brand\nicewp-app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\Assets\Brand\nicewp-app.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyDistSource}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\Brand\nicewp-app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\Brand\nicewp-app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Clean uninstaller: Preserves %LocalAppData%\NiceWP user settings and library data without prompt.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Explicitly do not delete %LocalAppData%\NiceWP user library or settings.
end;
