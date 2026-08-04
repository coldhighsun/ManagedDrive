; ManagedDrive Inno Setup script.
;
; Build (from repo root, after publishing framework-dependent output into
; installer\publish-fx and dropping the WinFsp MSI into installer\):
;   iscc.exe /DAppVersion=1.2.3 installer\ManagedDrive.iss
;
; AppVersion defaults to a placeholder so the script can still be compiled
; for local smoke-testing without passing /DAppVersion.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define WinFspMsiName "winfsp-2.2.26215.msi"
#define HelperServiceName "ManagedDriveHelper"
#define HelperServiceExeName "ManagedDriveHelper.exe"
; Must match App.xaml.cs::SingleInstanceMutexName exactly - this is how Setup detects a running
; instance without shelling out to tasklist/wmic.
#define AppMutexName "Global\ManagedDrive-4A7C2E1B-9F3D-4B8A-A1C5-3E6D2F0B8C9A"

[Setup]
AppId={{9B6F0F1A-6E0D-4A6B-8C7E-6C6D9B0E5A11}
AppName=ManagedDrive
AppVersion={#AppVersion}
AppPublisher=ManagedDrive
AppPublisherURL=https://github.com/coldhighsun/ManagedDrive
DefaultDirName={autopf}\ManagedDrive
DefaultGroupName=ManagedDrive
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ManagedDrive-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\ManagedDrive.exe
SetupIconFile=..\src\ManagedDrive.App\ManagedDrive.ico
WizardStyle=modern
; Restrict Restart Manager's file-in-use detection to our own exe rather than every
; *.exe/*.dll/*.chm file Setup installs (the default filter). Must be a bare filename
; wildcard, not a path — {app} cannot be used here since this is evaluated before the
; install directory is determined. This is a fallback path only - InitializeSetup()
; below already asks the running app to close itself gracefully (via "mdrive.exe exit",
; which saves every mounted disk's image) before this page is ever evaluated, so in the
; common case there is nothing left for Restart Manager to find here.
CloseApplicationsFilter=ManagedDrive.exe
CloseApplications=yes
RestartApplications=yes
; Setup adds {app} to the machine-wide PATH (see AddDirToPath/RemoveDirFromPath below) so this
; must be declared for Setup's "changes will take effect after restart" messaging to be accurate.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "publish-fx\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#WinFspMsiName}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\ManagedDrive"; Filename: "{app}\ManagedDrive.exe"
Name: "{group}\Uninstall ManagedDrive"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ManagedDrive"; Filename: "{app}\ManagedDrive.exe"; Tasks: desktopicon

[Code]
const
  WinFspRegKey32 = 'SOFTWARE\WOW6432Node\WinFsp';
  WinFspRegKey64 = 'SOFTWARE\WinFsp';
  DotNetDesktopRuntimeRegKey = 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  // Microsoft's stable "evergreen" redirect - always resolves to the current latest win-x64
  // Desktop Runtime installer for the 10.0 channel, so we never have to chase patch versions.
  DotNetDesktopRuntimeEvergreenUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  DotNetDownloadPageUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  // Used by CloseManagedDriveGracefully() below - Pascal Script only supports local `var`
  // sections inside functions/procedures, not local `const`.
  GracefulExitTimeoutMs = 30000;
  GracefulExitPollIntervalMs = 500;

var
  // Manually-created "Launch ManagedDrive" checkbox on the Finished page - see
  // CurPageChanged/CurStepChanged below. Not a [Run] entry: a normal [Run] "postinstall"
  // checkbox would go through Inno's own de-elevation path, which is exactly what
  // LaunchAppAsUser()'s explorer.exe workaround below exists to avoid.
  LaunchAppCheckBox: TNewCheckBox;

// Mirrors ManagedDrive.App's App.xaml.cs::CheckWinFspPrerequisite() detection:
// HKLM InstallDir -> <InstallDir>\bin\winfsp-msil.dll must exist with a 2.2.x file version.
function IsWinFspInstalled(): Boolean;
var
  InstallDir, DllPath: string;
  VersionMS, VersionLS: Cardinal;
  Major, Minor: Integer;
begin
  Result := False;

  if not RegQueryStringValue(HKLM, WinFspRegKey32, 'InstallDir', InstallDir) then
    RegQueryStringValue(HKLM, WinFspRegKey64, 'InstallDir', InstallDir);

  if InstallDir = '' then
    exit;

  DllPath := AddBackslash(InstallDir) + 'bin\winfsp-msil.dll';
  if not FileExists(DllPath) then
    exit;

  if not GetVersionNumbers(DllPath, VersionMS, VersionLS) then
    exit;

  Major := VersionMS shr 16;
  Minor := VersionMS and $FFFF;
  Result := (Major = 2) and (Minor = 2);
end;

// Mirrors the check dotnet's own bootstrapper uses to detect an installed
// shared framework version, avoiding any dependency on dotnet.exe being on PATH.
// Installed versions are recorded as REG_DWORD *values* under this key (name = version
// string, data = 1), not as subkeys - RegGetValueNames is required, not RegGetSubkeyNames.
function IsDotNetDesktopRuntime10Installed(): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetValueNames(HKLM, DotNetDesktopRuntimeRegKey, Versions) then
    exit;

  for I := 0 to GetArrayLength(Versions) - 1 do
  begin
    if Copy(Versions[I], 1, 3) = '10.' then
    begin
      Result := True;
      exit;
    end;
  end;
end;

procedure InstallWinFspSilently();
var
  MsiPath: string;
  ResultCode: Integer;
begin
  ExtractTemporaryFile('{#WinFspMsiName}');
  MsiPath := ExpandConstant('{tmp}\{#WinFspMsiName}');

  if not Exec('msiexec.exe', Format('/i "%s" /qn /norestart', [MsiPath]), '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Failed to launch msiexec for WinFsp: ' + SysErrorMessage(ResultCode));
    exit;
  end;

  if ResultCode <> 0 then
    Log(Format('WinFsp silent install returned exit code %d; continuing install regardless ' +
      '- ManagedDrive itself re-checks WinFsp on first launch.', [ResultCode]))
  else
    Log('WinFsp installed successfully.');
end;

procedure PromptForDotNetDesktopRuntime();
var
  ErrorCode: Integer;
begin
  // Silent runs (winget, CI, /VERYSILENT) have no one to answer a blocking MsgBox - just log it,
  // same as ReportAbortReason() does elsewhere in this script.
  if WizardSilent() then
  begin
    Log('.NET 10 Desktop Runtime could not be installed automatically; skipping interactive ' +
      'prompt because Setup is running silently. .NET-related features will not work until it ' +
      'is installed manually from ' + DotNetDownloadPageUrl);
    exit;
  end;

  if MsgBox('ManagedDrive requires the .NET 10 Desktop Runtime, which could not be installed ' +
    'automatically. Open the official download page now? You can also install it later and ' +
    '.NET-related features will start working once it is installed.',
    mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNetDownloadPageUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

// Silently downloads and runs the official .NET 10 Desktop Runtime installer via the evergreen
// link, mirroring InstallWinFspSilently() - unlike the WinFsp MSI this can't be bundled ahead of
// time since "latest patch" changes over time and the installer is tens of MB. Falls back to
// PromptForDotNetDesktopRuntime() if the download or the silent install itself fails, so a user
// is never left with a silent no-op.
procedure InstallDotNetDesktopRuntimeSilently();
var
  DownloadPath, ScriptPath, ScriptContent: string;
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Downloading .NET 10 Desktop Runtime...';

  // Written to a temp .ps1 file (rather than passed inline via -Command) to avoid the quoting
  // headaches of nesting a PowerShell string literal inside a Pascal Script string literal.
  DownloadPath := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  ScriptPath := ExpandConstant('{tmp}\download-dotnet-runtime.ps1');
  ScriptContent := Format('Invoke-WebRequest -Uri "%s" -OutFile "%s"', [DotNetDesktopRuntimeEvergreenUrl, DownloadPath]);
  SaveStringToFile(ScriptPath, ScriptContent, False);

  if not Exec('powershell.exe', Format('-NoProfile -ExecutionPolicy Bypass -File "%s"', [ScriptPath]), '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) or not FileExists(DownloadPath) then
  begin
    Log('Failed to download the .NET 10 Desktop Runtime installer; falling back to manual prompt.');
    PromptForDotNetDesktopRuntime();
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing .NET 10 Desktop Runtime...';

  if not Exec(DownloadPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Failed to launch the .NET 10 Desktop Runtime installer: ' + SysErrorMessage(ResultCode));
    PromptForDotNetDesktopRuntime();
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Log(Format('.NET 10 Desktop Runtime silent install returned exit code %d; falling back to manual prompt.', [ResultCode]));
    PromptForDotNetDesktopRuntime();
  end
  else
    Log('.NET 10 Desktop Runtime installed successfully.');
end;

// Adds/removes {app} on the machine-wide PATH (HKLM, not HKCU) so "wingetx" resolves from any shell
// without a full path, matching PrivilegesRequired=admin. Best-effort, same as the helper service
// below: only Log()s on failure rather than aborting setup/uninstall.
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function SendMessageTimeout(hWnd: Longint; Msg: Longint; wParam: Longint; lParam: string;
  fuFlags, uTimeout: Longint; var lpdwResult: Longint): Longint;
  external 'SendMessageTimeoutA@user32.dll stdcall';

// Broadcasts WM_SETTINGCHANGE so already-running processes (e.g. Explorer) notice the PATH
// change immediately, mirroring the same broadcast ManagedDrive.App's TempDirResetService does
// for HKCU\Environment - already-open shells still won't see it until reopened, same as any
// other PATH change on Windows.
procedure BroadcastEnvironmentChange();
var
  BroadcastResult: Longint;
begin
  SendMessageTimeout($FFFF {HWND_BROADCAST}, $001A {WM_SETTINGCHANGE}, 0, 'Environment',
    2 {SMTO_ABORTIFHUNG}, 5000, BroadcastResult);
end;

procedure AddDirToPath(const Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Paths) then
    Paths := '';

  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
  begin
    Log('"' + Dir + '" is already on PATH; skipping.');
    exit;
  end;

  if Paths = '' then
    Paths := Dir
  else
    Paths := Paths + ';' + Dir;

  if RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Paths) then
  begin
    Log('Added "' + Dir + '" to PATH.');
    BroadcastEnvironmentChange();
  end
  else
    Log('Failed to add "' + Dir + '" to PATH.');
end;

// Removes exactly Dir from PATH, leaving every other entry untouched - deliberately not done via
// [Registry]'s uninsdeletevalue, which would wipe the entire shared PATH value on uninstall.
// Operates on a ';'-padded copy throughout (rather than deleting from the unpadded value with a
// P-1 offset, which underflows to index 0 when Dir is the very first entry) so the leading-entry
// case is handled the same way as every other position.
procedure RemoveDirFromPath(const Dir: string);
var
  Padded: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Padded) then
    exit;

  Padded := ';' + Padded + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Padded));
  if P = 0 then
  begin
    Log('"' + Dir + '" not found on PATH; nothing to remove.');
    exit;
  end;

  Delete(Padded, P, Length(Dir) + 1);
  Delete(Padded, 1, 1);
  if Length(Padded) > 0 then
    Delete(Padded, Length(Padded), 1);

  if RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Padded) then
  begin
    Log('Removed "' + Dir + '" from PATH.');
    BroadcastEnvironmentChange();
  end
  else
    Log('Failed to remove "' + Dir + '" from PATH.');
end;

// The optional SYSTEM helper service (cross-session RAM-disk symlink visibility - see
// CLAUDE.md). Best-effort only: ManagedDrive itself works fine without it, so every
// step here only Log()s on failure rather than aborting setup.
function IsHelperServiceInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#HelperServiceName}');
end;

procedure StopHelperService();
var
  ResultCode: Integer;
begin
  if not Exec('sc.exe', 'stop {#HelperServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Failed to launch sc.exe to stop the helper service: ' + SysErrorMessage(ResultCode))
  else if ResultCode <> 0 then
    Log(Format('"sc stop {#HelperServiceName}" returned exit code %d (service may already be stopped).', [ResultCode]));
end;

procedure StartHelperService();
var
  ResultCode: Integer;
begin
  if not Exec('sc.exe', 'start {#HelperServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Failed to launch sc.exe to start the helper service: ' + SysErrorMessage(ResultCode))
  else if ResultCode <> 0 then
    Log(Format('"sc start {#HelperServiceName}" returned exit code %d.', [ResultCode]))
  else
    Log('Helper service started.');
end;

procedure InstallHelperServiceSilently();
var
  BinPath: string;
  ResultCode: Integer;
begin
  BinPath := '"' + ExpandConstant('{app}') + '\{#HelperServiceExeName}"';

  if not Exec('sc.exe', Format('create {#HelperServiceName} binPath= %s start= auto', [BinPath]), '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Failed to launch sc.exe to create the helper service: ' + SysErrorMessage(ResultCode));
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Log(Format('"sc create {#HelperServiceName}" returned exit code %d; skipping start. Cross-session ' +
      'RAM-disk visibility will not be available, but ManagedDrive itself is unaffected.', [ResultCode]));
    exit;
  end;

  Log('Helper service registered.');
  StartHelperService();
end;

// Launches ManagedDrive.exe post-install as the logged-in user rather than via Setup's own
// Exec(), which - on this elevated install path - would internally go through Inno Setup's
// de-elevation "spawn server" (CallSpawnServer) to hand the process off to the unprivileged
// user session. That mechanism has been observed to fail with "CallSpawnServer: Unexpected
// status: 1" on machines where UAC's admin consent prompt is configured to elevate silently
// (ConsentPromptBehaviorAdmin=0), because Setup never goes through its own unelevated-then-
// elevated self-relaunch, so no unelevated companion process exists to act as that spawn
// server. Asking the user's own already-unelevated explorer.exe to open the file sidesteps
// the de-elevation step entirely - explorer.exe hosts the launch under the user's normal
// token however Setup itself is running.
procedure LaunchAppAsUser();
var
  ResultCode: Integer;
begin
  if not Exec('explorer.exe', ExpandConstant('"{app}\ManagedDrive.exe"'), '', SW_SHOWNORMAL,
    ewNoWait, ResultCode) then
    Log('Failed to launch ManagedDrive.exe via explorer.exe: ' + SysErrorMessage(ResultCode));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if not IsWinFspInstalled() then
      InstallWinFspSilently()
    else
      Log('WinFsp 2.2.x already installed; skipping.');

    // Reinstall/upgrade: stop the running service *before* [Files] copies the new
    // ManagedDriveHelper.exe over it. CloseApplicationsFilter/Restart Manager only cover
    // ManagedDrive.exe (see the [Setup] comment above) and know nothing about services, so
    // without this the file copy silently fails to overwrite the locked exe and the old
    // binary stays in place even after the service is restarted in ssPostInstall below.
    if IsHelperServiceInstalled() then
    begin
      Log('Helper service already registered; stopping before files are copied so the exe can be overwritten.');
      StopHelperService();
    end;
  end;

  if CurStep = ssPostInstall then
  begin
    AddDirToPath(ExpandConstant('{app}'));

    if not IsDotNetDesktopRuntime10Installed() then
      InstallDotNetDesktopRuntimeSilently()
    else
      Log('.NET 10 Desktop Runtime already installed; skipping.');

    if not IsHelperServiceInstalled() then
      InstallHelperServiceSilently()
    else
    begin
      // Already stopped in ssInstall above; start it again now that the new binary is in place.
      Log('Starting helper service with the updated binary.');
      StartHelperService();
    end;
  end;

  // Mirrors the old [Run] entry's "postinstall skipifsilent" semantics, for the silent case only:
  // launch unconditionally when running silently (winget/CI have no one to see it, and no
  // Finished page - hence no LaunchAppCheckBox - is ever shown to opt out with). The interactive
  // case is handled in NextButtonClick below instead, since ssDone fires while still on the
  // wpInstalling page, before wpFinished (and LaunchAppCheckBox) exists.
  if (CurStep = ssDone) and WizardSilent() then
    LaunchAppAsUser();
end;

// Handles the "Finish" click on the Finished page. Deliberately not done from CurStepChanged's
// ssDone branch above: ssDone fires before the wizard advances to wpFinished, so
// LaunchAppCheckBox wouldn't exist there yet.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (CurPageID = wpFinished) and Assigned(LaunchAppCheckBox) and LaunchAppCheckBox.Checked then
    LaunchAppAsUser();
end;

// Checks whether the user-level TEMP variable currently points at a drive letter that a saved
// ManagedDrive disk profile uses as its mount point. Mirrors TempDirCompatChecker.IsTempOnAnyDisk
// (App layer), but reads the persisted profile list from settings.json instead of live
// DiskViewModels, since neither Setup nor the uninstaller can assume the app is running.
// settings.json is written via JsonSerializer.Serialize with default (indented, PascalCase)
// options, so a plain substring search for the "MountPoint": "<Letter>: pattern is reliable
// without a JSON parser. Shared by InitializeSetup and InitializeUninstall below.
function IsTempOnManagedDriveMountPoint(): Boolean;
var
  TempValue, SettingsPath, NeedlePrefix, DriveLetter: string;
  SettingsContent: AnsiString;
begin
  Result := False;

  if not RegQueryStringValue(HKCU, 'Environment', 'TEMP', TempValue) then
    exit;

  if (Length(TempValue) < 2) or (TempValue[2] <> ':') then
    exit;

  DriveLetter := Uppercase(Copy(TempValue, 1, 1));

  SettingsPath := ExpandConstant('{userappdata}\ManagedDrive\settings.json');
  if not LoadStringFromFile(SettingsPath, SettingsContent) then
    exit;

  NeedlePrefix := '"MountPoint": "' + DriveLetter + ':';
  Result := Pos(NeedlePrefix, SettingsContent) > 0;
end;

// Surfaces the TEMP-on-RAM-disk abort reason appropriately for the run mode: an interactive
// MsgBox when a user is present to read it, or a Log(...) entry when running silently (e.g.
// winget install --silent, /VERYSILENT), where a blocking MsgBox would never be seen/dismissed
// but the reason should still be diagnosable via Setup's /LOG= output.
procedure ReportAbortReason(const EnMessage, ZhMessage: string);
begin
  if WizardSilent() then
    Log('Aborting: ' + EnMessage)
  else if ActiveLanguage() = 'chinesesimplified' then
    MsgBox(ZhMessage, mbError, MB_OK)
  else
    MsgBox(EnMessage, mbError, MB_OK);
end;

function IsManagedDriveRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppMutexName}');
end;

// Resolves the directory a previous install (if any) landed in, by reading the uninstall entry
// Inno Setup itself writes on every install. Needed because InitializeSetup() runs before {app}
// is resolved at all - ExpandConstant('{app}') throws "constant expanded before initialized" if
// called this early. Uses HKLM64 explicitly since ArchitecturesInstallIn64BitMode=x64compatible
// writes the uninstall key to the 64-bit registry view. Returns '' on a fresh install, where
// there is no previous instance to be running in the first place.
function GetInstalledAppDir(): string;
begin
  if not RegQueryStringValue(HKLM64,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9B6F0F1A-6E0D-4A6B-8C7E-6C6D9B0E5A11}_is1',
    'Inno Setup: App Path', Result) then
    Result := '';
end;

// Asks an already-running ManagedDrive instance to exit via "mdrive.exe exit" - the same CLI
// command the tray icon's own Exit menu item ultimately triggers (MainViewModel.ExitRequested ->
// App.ShutdownAsync), which saves every mounted disk's image before the process terminates. This
// is deliberately run up front (from InitializeSetup, before the wizard shows any page) rather
// than left to Restart Manager on the "Preparing to Install" page: RmShutdown's graceful mode has
// its own timeout unrelated to how long saving a large/compressed/encrypted disk can take, and its
// forced mode (TerminateProcess) skips saving entirely. Returns True once the app has exited (or
// was never running); False if it is still running after the timeout below, so the caller can
// decide whether to continue (falling back to Restart Manager later in the wizard) or abort.
function CloseManagedDriveGracefully(): Boolean;
var
  AppDir, MdrivePath: string;
  ResultCode, Elapsed: Integer;
begin
  Result := True;

  if not IsManagedDriveRunning() then
    exit;

  AppDir := GetInstalledAppDir();
  if AppDir = '' then
  begin
    Log('ManagedDrive appears to be running but its installed directory could not be resolved ' +
      'from the registry; cannot request a graceful exit.');
    Result := False;
    exit;
  end;

  MdrivePath := AddBackslash(AppDir) + 'mdrive.exe';
  if not FileExists(MdrivePath) then
  begin
    Log('ManagedDrive appears to be running but mdrive.exe was not found at "' + MdrivePath +
      '"; cannot request a graceful exit.');
    Result := False;
    exit;
  end;

  Log('ManagedDrive is running; requesting a graceful exit via "mdrive.exe exit" so it can save ' +
    'mounted disks before Setup replaces its files.');

  if not Exec(MdrivePath, 'exit', '', SW_HIDE, ewNoWait, ResultCode) then
    Log('Failed to launch "mdrive.exe exit": ' + SysErrorMessage(ResultCode));

  Elapsed := 0;
  while IsManagedDriveRunning() and (Elapsed < GracefulExitTimeoutMs) do
  begin
    Sleep(GracefulExitPollIntervalMs);
    Elapsed := Elapsed + GracefulExitPollIntervalMs;
  end;

  Result := not IsManagedDriveRunning();

  if Result then
    Log('ManagedDrive exited gracefully.')
  else
    Log(Format('ManagedDrive is still running after waiting %d ms for a graceful exit.', [GracefulExitTimeoutMs]));
end;

function InitializeSetup(): Boolean;
var
  ConfirmCloseMessage: string;
begin
  Result := True;

  if IsTempOnManagedDriveMountPoint() then
  begin
    ReportAbortReason(
      'TEMP is currently set to a ManagedDrive RAM disk. Installing or upgrading now may close ' +
      'ManagedDrive and unmount that disk while Setup still needs a working TEMP directory for ' +
      'its own files, which can make Setup fail partway through.'#13#10#13#10 +
      'Please open ManagedDrive and reset TEMP to its default location (Tray menu > Reset TEMP ' +
      'Dirs, or untoggle TEMP on the disk), then run this installer again.',
      '当前 TEMP 目录设置在了 ManagedDrive 内存盘上。现在安装或升级可能会关闭 ManagedDrive 并卸载该' +
      '盘，而安装程序自身仍需要一个可用的 TEMP 目录来存放临时文件，这会导致安装过程中途失败。'#13#10#13#10 +
      '请先打开 ManagedDrive，将 TEMP 还原为系统默认设置（托盘菜单 > 重置 TEMP 目录，或取消该磁盘' +
      '的 TEMP 设置），然后再次运行本安装程序。');
    Result := False;
    exit;
  end;

  if not IsManagedDriveRunning() then
    exit;

  // Running silently (e.g. winget/CI), there is no one to ask - just close it ourselves via
  // mdrive.exe and abort if that doesn't work out, same as the interactive Yes path below.
  if WizardSilent() then
  begin
    if not CloseManagedDriveGracefully() then
    begin
      ReportAbortReason(
        'ManagedDrive is still running and could not be closed automatically in time to save its ' +
        'RAM disk contents. Please close it manually (tray icon menu > Exit) and run Setup again.',
        'ManagedDrive 仍在运行，未能在超时时间内自动关闭以保存内存盘内容。请通过托盘图标菜单手动退出' +
        '后再重新运行安装程序。');
      Result := False;
    end;
    exit;
  end;

  // Interactive: let the user choose, rather than closing the app out from under them
  // unannounced. Yes = Setup closes it via "mdrive.exe exit" (saves every mounted disk first);
  // No = abort Setup so the user can close it manually (tray icon menu > Exit) and re-run.
  if ActiveLanguage() = 'chinesesimplified' then
    ConfirmCloseMessage :=
      'ManagedDrive 当前正在运行，需要先关闭才能继续安装。'#13#10#13#10 +
      '点击"是"让安装程序自动关闭它（会先保存所有已挂载的内存盘）。点击"否"退出安装程序，自行手动关闭' +
      'ManagedDrive 后再重新运行安装程序。'
  else
    ConfirmCloseMessage :=
      'ManagedDrive is currently running. It needs to be closed before Setup can continue.'#13#10#13#10 +
      'Click Yes to let Setup close it automatically (it will save all mounted RAM disks first). ' +
      'Click No to exit Setup so you can close it yourself.';

  if MsgBox(ConfirmCloseMessage, mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    exit;
  end;

  if not CloseManagedDriveGracefully() then
  begin
    ReportAbortReason(
      'ManagedDrive is still running and could not be closed automatically in time to save its ' +
      'RAM disk contents. Please close it manually (tray icon menu > Exit) and run Setup again.',
      'ManagedDrive 仍在运行，未能在超时时间内自动关闭以保存内存盘内容。请通过托盘图标菜单手动退出' +
      '后再重新运行安装程序。');
    Result := False;
  end;
end;

// Appends an explanation to Setup's built-in "Preparing to Install" page whenever it lists
// ManagedDrive as needing to be closed. This should be rare in practice - InitializeSetup already
// closes ManagedDrive gracefully before this page is ever reached - but it covers the edge case of
// the app being relaunched between that check and this page (e.g. its own [Run]-less auto-restart
// logic, or the user starting it again by hand), where Restart Manager's own closing offer would
// otherwise appear with no context.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpPreparing) and IsManagedDriveRunning() then
  begin
    if ActiveLanguage() = 'chinesesimplified' then
      WizardForm.PreparingLabel.Caption := WizardForm.PreparingLabel.Caption + #13#10#13#10 +
        'ManagedDrive 将会被自动关闭；关闭前会先将所有已挂载内存盘的内容保存到镜像文件，不会丢失数据。'
    else
      WizardForm.PreparingLabel.Caption := WizardForm.PreparingLabel.Caption + #13#10#13#10 +
        'ManagedDrive will be closed automatically; it saves the contents of every mounted RAM ' +
        'disk to its image file before exiting, so no data will be lost.';
  end;

  // There is no [Run] entry to drive Inno's usual "Launch xxx" Finished-page checkbox - see the
  // LaunchAppCheckBox comment above for why. Create an equivalent checkbox by hand instead, so the
  // user can opt out of the auto-launch in CurStepChanged above. Guarded so it's only created once.
  if (CurPageID = wpFinished) and not WizardSilent() and not Assigned(LaunchAppCheckBox) then
  begin
    LaunchAppCheckBox := TNewCheckBox.Create(WizardForm);
    LaunchAppCheckBox.Parent := WizardForm.FinishedPage;
    LaunchAppCheckBox.Left := WizardForm.FinishedLabel.Left;
    LaunchAppCheckBox.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
    LaunchAppCheckBox.Width := WizardForm.FinishedLabel.Width;
    if ActiveLanguage() = 'chinesesimplified' then
      LaunchAppCheckBox.Caption := '启动 ManagedDrive'
    else
      LaunchAppCheckBox.Caption := 'Launch ManagedDrive';
    LaunchAppCheckBox.Checked := True;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  if IsTempOnManagedDriveMountPoint() then
  begin
    ReportAbortReason(
      'TEMP is currently set to a ManagedDrive RAM disk. Uninstalling now will leave TEMP ' +
      'pointing at a drive that no longer exists.'#13#10#13#10 +
      'Please open ManagedDrive and reset TEMP to its default location (Tray menu > Reset TEMP ' +
      'Dirs, or untoggle TEMP on the disk), then run this uninstaller again.',
      '当前 TEMP 目录设置在了 ManagedDrive 内存盘上。现在卸载会导致 TEMP 指向一个不存在的驱动器。'#13#10#13#10 +
      '请先打开 ManagedDrive，将 TEMP 还原为系统默认设置（托盘菜单 > 重置 TEMP 目录，或取消该磁盘' +
      '的 TEMP 设置），然后再次运行本卸载程序。');
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if IsHelperServiceInstalled() then
    begin
      StopHelperService();
      if not Exec('sc.exe', 'delete {#HelperServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('Failed to launch sc.exe to delete the helper service: ' + SysErrorMessage(ResultCode))
      else if ResultCode <> 0 then
        Log(Format('"sc delete {#HelperServiceName}" returned exit code %d.', [ResultCode]))
      else
        Log('Helper service removed.');
    end
    else
      Log('Helper service not registered; nothing to remove.');
  end;

  if CurUninstallStep = usPostUninstall then
    RemoveDirFromPath(ExpandConstant('{app}'));
end;
