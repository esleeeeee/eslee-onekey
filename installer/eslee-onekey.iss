; eslee OneKey installer script (Inno Setup 6).
; Build:
;   installer\Build-Installer.ps1 -Version 0.1.7 -SourceDir <self-contained publish folder>
;
; 설정·로그·계정 세션 백업은 %LOCALAPPDATA%\eslee OneKey에 있고 제거해도 지우지 않는다.
; 그래서 재설치하거나 포터블에서 옮겨 와도 설정이 그대로 남는다.

#ifndef AppVersion
  #define AppVersion "0.1.7"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppDisplayName "eslee OneKey"
#define MainExeName "Eslee.OneKey.App.exe"
; 앱의 StartupRegistrationService와 같은 값 이름이어야 앱 설정의 체크 상태와 맞는다.
#define RunValueName "eslee OneKey"

[Setup]
AppId={{6E2C4B1A-8F3D-4A7E-9C25-1B7D0E4F8A63}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} v{#AppVersion}
AppPublisher=eslee
AppPublisherURL=https://github.com/esleeeeee/eslee-onekey
AppUpdatesURL=https://github.com/esleeeeee/eslee-onekey/releases
DefaultDirName={autopf}\eslee OneKey
DefaultGroupName=eslee OneKey
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MainExeName}
UninstallDisplayName={#AppDisplayName}
SetupIconFile=..\src\Eslee.OneKey.App\Assets\eslee-onekey.ico
OutputDir={#OutputDir}
OutputBaseFilename=eslee-OneKey-v{#AppVersion}-win-x64-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes

[Languages]
#if FileExists(CompilerPath + "\Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#elif FileExists(CompilerPath + "\Languages\Unofficial\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Unofficial\Korean.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[CustomMessages]
LaunchApp=eslee OneKey 실행
DesktopIconTask=바탕화면 바로가기 만들기
AutoStartTask=Windows 로그인 시 자동 실행 (트레이로 시작)
StillRunning=eslee OneKey가 아직 실행 중입니다.%n트레이 아이콘 메뉴에서 OneKey를 종료한 뒤 설치를 다시 실행하세요.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartTask}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\eslee OneKey"; Filename: "{app}\{#MainExeName}"
Name: "{autodesktop}\eslee OneKey"; Filename: "{app}\{#MainExeName}"; Tasks: desktopicon

[Registry]
; 포터블에서 등록한 값이 있으면 같은 이름으로 덮어써 설치 경로를 가리키게 한다.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; ValueData: """{app}\{#MainExeName}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MainExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ShutdownEventName = 'Local\eslee.OneKey.Shutdown';
  SingleInstanceMutexName = 'Local\eslee.OneKey.SingleInstance';
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function IsOneKeyRunning(): Boolean;
var
  Handle: THandle;
begin
  Handle := OpenMutex(SYNCHRONIZE, False, SingleInstanceMutexName);
  Result := Handle <> 0;
  if Result then
    CloseHandle(Handle);
end;

{ 실행 중인 OneKey에 종료 신호를 보내고 끝날 때까지 기다린다. 앱이 직접 종료해야
  바꿔 둔 오디오 장치 복원 같은 정리 작업이 끝난다. 신호를 모르는 이전 버전은 False. }
function StopRunningOneKey(): Boolean;
var
  Handle: THandle;
  Waited: Integer;
begin
  Result := not IsOneKeyRunning();
  if Result then
    Exit;

  Handle := OpenEvent(EVENT_MODIFY_STATE, False, ShutdownEventName);
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;

  Waited := 0;
  while IsOneKeyRunning() and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not IsOneKeyRunning();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunningOneKey() then
    Result := CustomMessage('StillRunning');
end;

{ 자동 실행을 끄고 설치했는데 이전(포터블) 경로를 가리키는 값이 남아 있으면 지운다.
  남겨 두면 로그인할 때 옛 포터블 OneKey가 먼저 떠서 설치본이 실행되지 않는다. }
procedure RemoveStaleAutoStart();
var
  Value: String;
begin
  if WizardIsTaskSelected('autostart') then
    Exit;
  if RegQueryStringValue(HKCU, RunKey, '{#RunValueName}', Value) then
    if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) = 0 then
      RegDeleteValue(HKCU, RunKey, '{#RunValueName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RemoveStaleAutoStart();
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not StopRunningOneKey() then
  begin
    MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  { 앱 설정에서 켠 자동 실행도 같은 값 이름을 쓰므로, 이 설치본을 가리킬 때만 함께 지운다. }
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, RunKey, '{#RunValueName}', Value) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, RunKey, '{#RunValueName}');
end;
