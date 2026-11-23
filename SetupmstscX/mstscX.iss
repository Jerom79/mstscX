#define MyAppName "mstscX"
#define MyAppExeName "mstscX.exe"
; Preserve installer/Windows identities so this updates MSTSC_MAUI in place.
#define MyAppUserModelID "MSTSC_MAUI.RdpClient"
#define MyAppId "{{92A56F9F-6E76-4CC5-87DA-E327F64E451B}"
#define MyAppMutex "Local\MSTSC_MAUI.RdpClient"

#ifndef DistribDir
  #define DistribDir SourcePath + "Distrib"
#endif

#define MyAppVersion GetVersionNumbersString(AddBackslash(DistribDir) + MyAppExeName)

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\mstscX
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes
DisableStartupPrompt=yes
AppMutex={#MyAppMutex}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
OutputDir=Setup
OutputBaseFilename=mstscX-Setup-{#MyAppVersion}
SetupIconFile={#DistribDir}\appicon.ico
SolidCompression=yes
WizardStyle=modern
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistribDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Only obsolete application binaries and shortcuts; never user data.
Type: files; Name: "{app}\MSTSC_MAUI.exe"
Type: files; Name: "{app}\MSTSC_MAUI.dll"
Type: files; Name: "{app}\MSTSC_MAUI.deps.json"
Type: files; Name: "{app}\MSTSC_MAUI.runtimeconfig.json"
Type: files; Name: "{app}\MSTSC_MAUI.pri"
Type: files; Name: "{app}\MSTSC_MAUI.pdb"
Type: files; Name: "{autoprograms}\MSTSC_MAUI.lnk"
Type: files; Name: "{autodesktop}\MSTSC_MAUI.lnk"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: {#MyAppUserModelID}
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: {#MyAppUserModelID}; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.rdpx"; ValueType: string; ValueName: ""; ValueData: "MSTSC_MAUI.ServerList"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.rdpx\OpenWithProgids"; ValueType: string; ValueName: "MSTSC_MAUI.ServerList"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\MSTSC_MAUI.ServerList"; ValueType: string; ValueName: ""; ValueData: "Список серверов mstscX"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\MSTSC_MAUI.ServerList\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\appicon.ico"
Root: HKA; Subkey: "Software\Classes\MSTSC_MAUI.ServerList\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;
  LegacyInstallDir, RenamedInstallDir: String;
  InstallFolderRenamed, InstallationCompleted: Boolean;

procedure InitializeWizard();
var
  UninstallKey, ExistingDir: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
    ExpandConstant('{#MyAppId}') + '_is1';
  RegQueryStringValue(HKA, UninstallKey, 'InstallLocation', ExistingDir);
  ExistingDir := RemoveBackslashUnlessRoot(ExistingDir);
  if (CompareText(ExtractFileName(ExistingDir), 'MSTSC_MAUI') = 0) and DirExists(ExistingDir) then
  begin
    LegacyInstallDir := ExistingDir;
    RenamedInstallDir := AddBackslash(ExtractFileDir(ExistingDir)) + 'mstscX';
    WizardForm.DirEdit.Text := RenamedInstallDir;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if (LegacyInstallDir = '') or InstallFolderRenamed then Exit;
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue()), RenamedInstallDir) <> 0 then
  begin
    Result := 'Для обновления с переименованием папки выберите ' + RenamedInstallDir;
    Exit;
  end;
  if CheckForMutexes('{#MyAppMutex}') then
  begin
    Result := 'Закройте приложение перед переименованием папки установки.';
    Exit;
  end;
  if DirExists(RenamedInstallDir) or FileExists(RenamedInstallDir) then
  begin
    Result := 'Папка назначения уже существует: ' + RenamedInstallDir +
      '. Переименование остановлено, существующие файлы сохранены.';
    Exit;
  end;
  if not RenameFile(LegacyInstallDir, RenamedInstallDir) then
  begin
    Result := 'Не удалось переименовать ' + LegacyInstallDir + ' в ' + RenamedInstallDir +
      '. Проверьте права доступа и закройте программы, использующие эту папку.';
    Exit;
  end;
  InstallFolderRenamed := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then InstallationCompleted := True;
end;

procedure DeinitializeSetup();
begin
  if InstallFolderRenamed and not InstallationCompleted then
    if not RenameFile(RenamedInstallDir, LegacyInstallDir) then
      SuppressibleMsgBox('Не удалось вернуть прежнее имя папки. Файлы сохранены в ' +
        RenamedInstallDir, mbError, MB_OK, IDOK);
end;

function GetFileAttributes(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function UserDataRoot(): String;
begin
  Result := ExpandConstant('{localappdata}\User Name\com.companyname.mstsc_maui');
end;

function ConfirmUserDataRemoval(): Boolean;
begin
  Result := False;
  if DirExists(UserDataRoot()) then
    Result := MsgBox('Удалить сохранённые настройки текущего пользователя? ' +
      'Будут удалены профили серверов, сохранённые пароли и параметры приложения. ' +
      'Нажмите «Нет», чтобы сохранить их для следующей установки.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure DeleteUserData();
var
  Path: String;
begin
  Path := UserDataRoot();
  if not DirExists(Path) then
    Exit;

  { Do not traverse a redirected publisher directory or application directory. }
  if ((GetFileAttributes(ExtractFileDir(Path)) and $400) <> 0) or
     ((GetFileAttributes(Path) and $400) <> 0) then
    MsgBox('Данные приложения находятся в перенаправленной папке и были сохранены.',
      mbError, MB_OK)
  else if not DelTree(Path, True, True, True) then
    MsgBox('Не удалось полностью удалить настройки приложения.', mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
var
  UninstallKey, UninstallCommand, InstalledVersionText, InstallDir, ActionText: String;
  InstalledVersion, SetupVersion: Int64;
  Choice, ExitCode, Comparison: Integer;
  DeleteSettings, Downgrade: Boolean;
begin
  Result := True;
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
    ExpandConstant('{#MyAppId}') + '_is1';
  if not RegKeyExists(HKA, UninstallKey) then
    Exit;
  RegQueryStringValue(HKA, UninstallKey, 'InstallLocation', InstallDir);


  { Compare numeric components, not strings (1.10 is newer than 1.9).
    HKA follows the selected per-user/all-users installation scope. }
  RegQueryStringValue(HKA, UninstallKey, 'DisplayVersion', InstalledVersionText);
  if not StrToVersion(InstalledVersionText, InstalledVersion) then
  begin
    RegQueryStringValue(HKA, UninstallKey, 'InstallLocation', InstallDir);
    if (InstallDir = '') or
       (not GetPackedVersion(AddBackslash(InstallDir) + '{#MyAppExeName}', InstalledVersion) and
        not GetPackedVersion(AddBackslash(InstallDir) + 'MSTSC_MAUI.exe', InstalledVersion)) then
    begin
      Result := False;
      SuppressibleMsgBox('Не удалось определить версию установленного приложения. ' +
        'Удалите текущую копию перед установкой.', mbError, MB_OK, IDOK);
      Exit;
    end;
    InstalledVersionText := VersionToStr(InstalledVersion);
  end;
  if not StrToVersion('{#MyAppVersion}', SetupVersion) then
  begin
    Result := False;
    Exit;
  end;
  Comparison := ComparePackedVersion(SetupVersion, InstalledVersion);
  Downgrade := Comparison < 0;
  Log('Installed version: ' + InstalledVersionText + '; setup version: {#MyAppVersion}');

  { Unattended installation must never remove a newer version automatically. }
  if WizardSilent() then
  begin
    Result := not Downgrade;
    if Downgrade then
      Log('Downgrade blocked: uninstall the newer version first.');
    Exit;
  end;

  if not WizardSilent() and Downgrade then
  begin
    Result := False;
    Choice := TaskDialogMsgBox(
      'Установлена более новая версия',
      'Установлена версия ' + InstalledVersionText + ', в установщике — {#MyAppVersion}. ' +
      'Установка старой версии поверх новой запрещена. Можно удалить текущую версию ' +
      'и затем продолжить установку {#MyAppVersion}.',
      mbConfirmation, MB_YESNO, ['Удалить текущую и установить старую', 'Отмена'], 0);
    if Choice <> IDYES then
      Exit;
  end
  else if not WizardSilent() then
  begin
    if Comparison = 0 then
      ActionText := 'Переустановить'
    else
      ActionText := 'Обновить';
    Choice := TaskDialogMsgBox(
      '{#MyAppName} уже установлен',
      'Установлена версия ' + InstalledVersionText + ', в установщике — {#MyAppVersion}.',
      mbConfirmation, MB_YESNOCANCEL, [ActionText, 'Удалить', 'Отмена'], 0);
    if Choice = IDYES then Exit;
    Result := False;
    if Choice <> IDNO then Exit;
  end;

  Result := False;
  if not RegQueryStringValue(HKA, UninstallKey, 'UninstallString', UninstallCommand) or
     (Trim(UninstallCommand) = '') then
  begin
    MsgBox('Команда удаления не найдена. Удалите текущую версию вручную.', mbError, MB_OK);
    Exit;
  end;
  if CheckForMutexes('{#MyAppMutex}') then
  begin
    MsgBox('Закройте все окна {#MyAppName} перед удалением.', mbInformation, MB_OK);
    Exit;
  end;

  DeleteSettings := ConfirmUserDataRemoval();
  if not Exec('>', UninstallCommand + ' /SILENT /NORESTART', '', SW_SHOWNORMAL,
    ewWaitUntilTerminated, ExitCode) then
  begin
    MsgBox('Не удалось запустить удаление. ' + SysErrorMessage(ExitCode), mbError, MB_OK);
    Exit;
  end;
  if ExitCode <> 0 then
  begin
    MsgBox('Удаление завершилось с кодом ' + IntToStr(ExitCode) + '.', mbError, MB_OK);
    Exit;
  end;
  if RegKeyExists(HKA, UninstallKey) then
  begin
    MsgBox('Текущая версия не удалена полностью. Установка остановлена.', mbError, MB_OK);
    Exit;
  end;
  if DeleteSettings then
    DeleteUserData();
  { A plain removal ends setup; a confirmed downgrade continues only after removal. }
  Result := Downgrade;
end;

function InitializeUninstall(): Boolean;
begin
  if not UninstallSilent() then
    RemoveUserData := ConfirmUserDataRemoval();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
    DeleteUserData();
end;
