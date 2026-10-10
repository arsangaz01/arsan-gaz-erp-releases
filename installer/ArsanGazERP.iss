#define MyAppName "Arsan Gaz ERP"
#define MyAppVersion "9.0.0"
#define MyAppPublisher "Arsan Gaz"
#define MyAppExeName "ArsanGazERP.exe"
#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\artifacts\app"
#endif
[Setup]
AppId={{87C4E7B7-4F39-4ED6-8B4F-ARSANGAZ7900}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Arsan Gaz ERP
DefaultGroupName=Arsan Gaz ERP
OutputDir=..\artifacts\installer
OutputBaseFilename=ArsanGazERP_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Dirs]
Name: "{commondocs}\ArsanGazERP\CRM"; Permissions: users-modify
[Icons]
Name: "{autoprograms}\Arsan Gaz ERP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\Arsan Gaz AgentWatcher"; Filename: "{app}\AgentWatcher\ArsanGazERP.AgentWatcher.exe"; Parameters: "--background"; WorkingDir: "{app}\AgentWatcher"
Name: "{autodesktop}\Arsan Gaz ERP"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Arsan Gaz AgentWatcher"; Filename: "{app}\AgentWatcher\ArsanGazERP.AgentWatcher.exe"; Parameters: "--background"; WorkingDir: "{app}\AgentWatcher"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Arsan Gaz ERP'yi başlat"; Flags: nowait postinstall skipifsilent