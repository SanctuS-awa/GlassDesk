#ifndef AppVersion
  #define AppVersion "0.2.0.1beta"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{85C98BA3-7C50-4C1A-8627-E611BC6251AF}
AppName=GlassDesk
AppVersion={#AppVersion}
VersionInfoVersion=0.2.0.1
AppPublisher=GlassDesk
AppPublisherURL=https://github.com/Sheng-awa/GlassDesk
AppSupportURL=https://github.com/Sheng-awa/GlassDesk/issues
AppUpdatesURL=https://github.com/Sheng-awa/GlassDesk/releases
DefaultDirName={localappdata}\Programs\GlassDesk
DefaultGroupName=GlassDesk
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=GlassDesk-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\GlassDesk.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes
LicenseFile=..\licenses\installer-third-party-terms.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "qa-*.log,qa-*.txt,*.pdb,preview-data\*,smoke-data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassDesk"; Filename: "{app}\GlassDesk.exe"
Name: "{autodesktop}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GlassDesk.exe"; Description: "启动 GlassDesk"; Flags: nowait postinstall skipifsilent

[Code]
function StopResident(): Boolean;
var
  ExitCode, Attempt: Integer;
  ProgramPath: String;
begin
  Result := True;
  ProgramPath := ExpandConstant('{app}\GlassDesk.exe');
  if FileExists(ProgramPath) then begin
    if not Exec(ProgramPath, '--quit', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
      Result := False;
      Exit;
    end;
    for Attempt := 1 to 100 do begin
      if not CheckForMutexes('Local\GlassDesk.Resident.v1.643A4E18') then Exit;
      Sleep(100);
    end;
    Result := not CheckForMutexes('Local\GlassDesk.Resident.v1.643A4E18');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not StopResident() then
    Result := 'GlassDesk 尚未退出。请从托盘退出程序后重试。'
  else Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopResident();
  if not Result then MsgBox('请先从托盘退出 GlassDesk，再卸载。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GlassDesk');
  { The user configuration in LocalAppData\GlassDesk is intentionally retained. }
end;

