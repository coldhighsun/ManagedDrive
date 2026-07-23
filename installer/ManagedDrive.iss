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

#define WinFspMsiName "winfsp-2.2.26194.msi"

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

[Run]
Filename: "{app}\ManagedDrive.exe"; Description: "Launch ManagedDrive"; Flags: nowait postinstall skipifsilent

[Code]
const
  WinFspRegKey32 = 'SOFTWARE\WOW6432Node\WinFsp';
  WinFspRegKey64 = 'SOFTWARE\WinFsp';
  DotNetDesktopRuntimeRegKey = 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  // Microsoft's stable "evergreen" redirect - always resolves to the current latest win-x64
  // Desktop Runtime installer for the 10.0 channel, so we never have to chase patch versions.
  DotNetDesktopRuntimeEvergreenUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  DotNetDownloadPageUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if not IsWinFspInstalled() then
      InstallWinFspSilently()
    else
      Log('WinFsp 2.2.x already installed; skipping.');
  end;

  if CurStep = ssPostInstall then
  begin
    if not IsDotNetDesktopRuntime10Installed() then
      InstallDotNetDesktopRuntimeSilently()
    else
      Log('.NET 10 Desktop Runtime already installed; skipping.');
  end;
end;
