#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
[Setup]
AppId={{6F1B7C52-3A1E-4C8E-9B55-0A1B2C3D4E5F}
AppName=Vertushka
AppVersion={#AppVersion}
AppPublisher=Роман
DefaultDirName={autopf}\Vertushka
DefaultGroupName=Vertushka
OutputDir=Output
OutputBaseFilename=Vertushka - установка {#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=Vertushka
UninstallDisplayIcon={app}\Vertushka.exe
SetupIconFile=..\Vertushka\app.ico
VersionInfoVersion={#AppVersion}
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "..\build\app\Vertushka.exe"; DestDir: "{app}"; Flags: ignoreversion
; значок отдельным файлом: ярлыки ссылаются на него, и Windows не берёт старый значок из кэша
Source: "..\Vertushka\app.ico"; DestDir: "{app}"; DestName: "vertushka-icon.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\Vertushka"; Filename: "{app}\Vertushka.exe"; IconFilename: "{app}\vertushka-icon.ico"
Name: "{autodesktop}\Vertushka"; Filename: "{app}\Vertushka.exe"; IconFilename: "{app}\vertushka-icon.ico"; Tasks: desktopicon

[InstallDelete]
; убираем ярлыки прежнего названия «Моя вертушка»
Type: files; Name: "{autodesktop}\Моя вертушка.lnk"
Type: files; Name: "{group}\Моя вертушка.lnk"

[Run]
; сбрасываем кэш значков Windows, чтобы на рабочем столе и в панели задач появился новый значок
Filename: "{sys}\ie4uinit.exe"; Parameters: "-ClearIconCache"; Flags: runhidden skipifdoesntexist
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden skipifdoesntexist
Filename: "{app}\Vertushka.exe"; Description: "Запустить Vertushka"; Flags: nowait postinstall skipifsilent
