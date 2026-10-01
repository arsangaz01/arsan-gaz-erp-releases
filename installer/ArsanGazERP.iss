#define MyAppName "Arsan Gaz ERP"
#define MyAppVersion "8.1.0"
#define MyAppPublisher "Arsan Gaz"
#define MyAppExeName "ArsanGazERP.exe"
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
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\Arsan Gaz ERP"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Arsan Gaz ERP"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Tasks]
Name: desktopicon; Description: "Masaüstü kısayolu oluştur"; Flags: unchecked
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Arsan Gaz ERP'yi başlat"; Flags: nowait postinstall skipifsilent