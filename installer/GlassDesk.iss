#ifndef AppVersion
  #define AppVersion "0.2.1.3beta"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\GlassDesk"
#endif

[Setup]
AppId={{85C98BA3-7C50-4C1A-8627-E611BC6251AF}
AppName=GlassDesk
AppVersion={#AppVersion}
VersionInfoVersion=0.2.1.3
VersionInfoProductVersion=0.2.1.3
AppPublisher=GlassDesk
AppCopyright=MIT, GlassDesk contributors
AppPublisherURL=https://github.com/SanctuS-awa/GlassDesk
AppSupportURL=https://github.com/SanctuS-awa/GlassDesk/issues
AppUpdatesURL=https://github.com/SanctuS-awa/GlassDesk/releases
AppMutex=Local\GlassDesk.Resident.v1.643A4E18
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
UninstallDisplayName=GlassDesk 0.2.1.3 beta
CloseApplications=no
RestartApplications=no
SetupLogging=yes
LicenseFile=..\licenses\installer-third-party-terms.txt

[Languages]
#ifexist "compiler:Languages\ChineseSimplified.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "qa-*,*.pdb,createdump.exe,preview-data\*,smoke-data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

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
var
  UserDataPath: String;
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GlassDesk');

  if CurUninstallStep = usPostUninstall then begin
    UserDataPath := ExpandConstant('{localappdata}\GlassDesk');
    if DirExists(UserDataPath) and
       (MsgBox('是否同时删除当前用户的 GlassDesk 设置与数据？' + #13#10 +
         '选择“否”会保留，以便日后重新安装。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      if not DelTree(UserDataPath, True, True, True) then
        MsgBox('未能完整删除用户数据目录：' + #13#10 + UserDataPath, mbError, MB_OK);
  end;
end;

