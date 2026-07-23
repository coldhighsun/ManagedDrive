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
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

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
function IsDotNetDesktopRuntime10Installed(): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM, DotNetDesktopRuntimeRegKey, Versions) then
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
  if MsgBox('ManagedDrive requires the .NET 10 Desktop Runtime, which was not found on this ' +
    'computer. Open the official download page now? You can also install it later and ' +
    '.NET-related features will start working once it is installed.',
    mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
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
      PromptForDotNetDesktopRuntime()
    else
      Log('.NET 10 Desktop Runtime already installed; skipping.');
  end;
end;
