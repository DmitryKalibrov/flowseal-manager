#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef BuildVersion
  #error BuildVersion is required
#endif
#ifndef SourceRoot
  #error SourceRoot is required
#endif
#ifndef OutputRoot
  #error OutputRoot is required
#endif

#define AppName "Flowseal Manager"
#define AppExeName "FlowsealManager.exe"
#define AppId "{{7B3DB846-8FE9-4A97-A63C-098F6F4E67F7}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#ReleaseVersion}
AppPublisher=DmitryKalibrov
AppPublisherURL=https://github.com/DmitryKalibrov/flowseal-manager
AppSupportURL=https://github.com/DmitryKalibrov/flowseal-manager/issues
AppUpdatesURL=https://github.com/DmitryKalibrov/flowseal-manager/releases/latest
DefaultDirName={autopf}\Flowseal Manager
DefaultGroupName=Flowseal Manager
DisableProgramGroupPage=yes
LicenseFile={#SourcePath}\..\LICENSE
OutputDir={#OutputRoot}
OutputBaseFilename=FlowsealManager-Setup
SetupIconFile={#SourcePath}\..\src\FlowsealManager.App\Assets\FlowsealManager.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
CloseApplications=force
RestartApplications=no
MinVersion=10.0.17763
VersionInfoVersion={#BuildVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#ReleaseVersion}
VersionInfoDescription=Установщик Flowseal Manager
VersionInfoCompany=DmitryKalibrov
VersionInfoCopyright=Copyright (C) 2026 Flowseal Manager contributors

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked
Name: "tcpautotuning"; Description: "Включить нормальную автонастройку TCP (рекомендуется для загрузок с GitHub)"; GroupDescription: "Сетевые настройки:"

[Files]
Source: "{#SourceRoot}\publish-win-x64\{#AppExeName}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion; Check: not IsArm64
Source: "{#SourceRoot}\publish-win-arm64\{#AppExeName}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion; Check: IsArm64

[Icons]
Name: "{group}\Flowseal Manager"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Flowseal Manager"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "int tcp set global autotuninglevel=normal"; StatusMsg: "Настройка автонастройки TCP…"; Flags: runhidden waituntilterminated; Tasks: tcpautotuning
Filename: "{app}\{#AppExeName}"; Description: "Запустить Flowseal Manager"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: ShouldStartManager
Filename: "{app}\{#AppExeName}"; Parameters: "--minimized"; Flags: nowait skipifnotsilent; Check: ShouldStartManager

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN FlowsealManager"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteStartupTask"

[Code]
function ShouldStartManager: Boolean;
begin
  Result := ExpandConstant('{param:NOAUTORUN|0}') <> '1';
end;
