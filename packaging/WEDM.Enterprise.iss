; Inno Setup 6 — enterprise installer template for WEDM (self-contained publish output).
; Prerequisites: Inno Setup 6, code-signing certificate (optional), publish folder from packaging/publish-release.ps1
; Build: ISCC.exe /DMyAppVersion=1.1.0 /DPublishDir=C:\path\to\publish WEDM.Enterprise.iss

#define MyAppName "WebLogic Enterprise Deployment Manager"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\wedm-" + MyAppVersion + "-Stable-win-x64"
#endif
#define MyAppPublisher "WEDM"
#define MyAppExeName "WEDM.exe"

[Setup]
AppId=7CF2D901-B2A3-4C4D-9E6F-0123456789AB
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf64}\WEDM
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=WEDM-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
