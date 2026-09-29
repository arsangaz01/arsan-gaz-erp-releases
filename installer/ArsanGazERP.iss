#ifndef AppVersion
  #define AppVersion "5.2.0"
#endif

#define AppName "Arsan Gaz ERP"
#define AppPublisher "Arsan Gaz"
#define AppExeName "ArsanGazERP.exe"

[Setup]
AppId={{8A3A8CE0-99D8-4EE5-B8CF-8E240CB4E501}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Arsan Gaz ERP
DefaultGroupName=Arsan Gaz ERP
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ArsanGazERPSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Arsan Gaz ERP"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Arsan Gaz ERP"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start Arsan Gaz ERP"; Flags: nowait postinstall skipifsilent