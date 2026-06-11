; Inno Setup script for Hyper-V Manager Tray.
;
; Per-user install (no admin required). The app itself is requireAdministrator and
; elevates at runtime; the installer does not. The optional "Run at startup" task is
; the ONLY thing that elevates, and only if the user ticks it (see RegisterStartupTask).
;
; Build via installer\build-installer.ps1, which publishes the app and passes
; /DPublishDir and /DAppVersion to ISCC.

#define AppName       "Hyper-V Manager Tray"
#define AppExe        "HyperVManagerTray.exe"
#define AppPublisher  "ZeroZero Software"
#define AppUrl        "https://github.com/0z00z0/HyperVManagerTray"
; Matches the task name used by the app's in-tray "Run on startup" toggle (StartupManager),
; so the installer option and the tray toggle control the exact same logon task.
#define TaskName      "HyperVManagerTray"
#define WingetId      "0z00z0.HyperVManagerTray"

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; AppId uniquely identifies this app for upgrades/uninstall — do not change it.
AppId={{B7A4F0E2-1C93-4A65-9D8E-3F2A6C0B5E47}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
; {autopf} under PrivilegesRequired=lowest resolves to %LocalAppData%\Programs.
DefaultDirName={autopf}\HyperVManagerTray
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Per-user: installs under %LocalAppData%\Programs, no UAC for the install itself.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=HyperVManagerTray-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\AppIcon.ico
; Let a silent (background) update close the running tray app and replace its files.
; Do NOT auto-restart it afterwards — the app is requireAdministrator, so relaunching
; would pop a UAC prompt out of nowhere. It returns at the next sign-in / manual launch.
CloseApplications=yes
RestartApplications=no

[Files]
; Everything except config.json is overwritten on upgrade…
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "config.json"; Flags: recursesubdirs createallsubdirs ignoreversion
; …config.json is installed only if absent, so a user-edited config is never clobbered.
Source: "{#PublishDir}\config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
; Flat shortcut in Start Menu → Programs (no sub-folder) so the app is searchable by name.
; IconFilename points to AppIcon.ico (ZeroZero Software brand icon) shipped next to the exe
; so the Start Menu shortcut shows the correct icon immediately.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\AppIcon.ico"; Comment: "Hyper-V VM network and power manager"

[Tasks]
Name: "runstartup"; Description: "Run {#AppName} automatically at sign-in (starts elevated without a UAC prompt at boot)"; Flags: unchecked
Name: "autoupdate"; Description: "Auto update in background (checks for updates via winget after each sign-in)"

; Generated at runtime by the app — remove on uninstall so the folder can be cleaned up.
[UninstallDelete]
Type: files;      Name: "{app}\icon-unknown-v3.ico"
Type: files;      Name: "{app}\icon-bridged-v3.ico"
Type: files;      Name: "{app}\icon-fallback-v3.ico"
Type: files;      Name: "{app}\AppIcon.ico"
Type: files;      Name: "{app}\app.ico"
; Legacy names — clean up if upgrading from an older install
Type: files;      Name: "{app}\icon-bridged-v2.ico"
Type: files;      Name: "{app}\icon-fallback-v2.ico"
Type: files;      Name: "{app}\switch-blue.ico"
Type: files;      Name: "{app}\switch-grey.ico"
Type: dirifempty; Name: "{app}"

; NOTE: launching the app is handled in [Code] (LaunchApp), not [Run]. A [Run] entry uses
; CreateProcess, which CANNOT start a requireAdministrator exe (fails with "elevation
; required"). LaunchApp starts it correctly — via the elevated logon task if one exists
; (no extra prompt), otherwise via ShellExec (the single UAC prompt the app needs).

[Code]
const
  TaskName       = '{#TaskName}';
  UpdateTaskName = '{#TaskName} AutoUpdate';

// ── .NET 10 Desktop Runtime prerequisite check ─────────────────────────────
// The app is published framework-dependent and requires .NET 10 Desktop Runtime
// (Microsoft.WindowsDesktop.App 10.x).
//
// Detection strategy (investigated on-machine — see findings below):
//
// The registry path commonly documented for per-framework detection
// (SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App)
// does NOT exist on all machines.  On machines where only the windowsdesktop-runtime
// bundle is used (not the SDK), only the 'sharedhost' subkey is written:
//   HKLM64\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost
//     Path    = C:\Program Files\dotnet\
//     Version = 10.0.x
//
// The most reliable indicator for Desktop Runtime specifically is the filesystem:
// the runtime unpacks its payload to {DotNetPath}\shared\Microsoft.WindowsDesktop.App\{version}\
// and this directory always exists when the runtime is installed, regardless of
// which registry keys were written.
//
// Algorithm:
//   1. Read the dotnet install path from HKLM64\sharedhost\Path (avoids hardcoding
//      C:\Program Files\dotnet\ for non-default installs).
//   2. Fall back to {pf64}\dotnet\ if the registry key is absent.
//   3. FindFirst for a 10.* subdirectory under shared\Microsoft.WindowsDesktop.App\.
function IsDotNet10DesktopInstalled: Boolean;
var
  DotNetPath: string;
  FindRec: TFindRec;
begin
  Result := False;

  if not RegQueryStringValue(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost',
      'Path', DotNetPath) then
    DotNetPath := ExpandConstant('{pf64}\dotnet\');

  // Ensure trailing backslash before appending the sub-path.
  if (Length(DotNetPath) > 0) and (DotNetPath[Length(DotNetPath)] <> '\') then
    DotNetPath := DotNetPath + '\';

  if FindFirst(DotNetPath + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    FindClose(FindRec);
    Result := True;
  end;
end;

// urlmon.dll — synchronous HTTP(S) download to a local file; no external dependencies.
function URLDownloadToFileW(pCaller: IUnknown; URL, FileName: String;
  Reserved: LongWord; lpfnCB: IUnknown): HResult;
  external 'URLDownloadToFileW@urlmon.dll stdcall';

function InitializeSetup: Boolean;
var
  ResultCode: Integer;
  TempExe: String;
begin
  Result := True;
  if IsDotNet10DesktopInstalled then Exit;

  if MsgBox(
      '.NET 10 Desktop Runtime is required but was not found on this machine.'
      + #13#10#13#10
      + 'Click OK to download and install it automatically (~55 MB).'
      + #13#10
      + 'Setup will be unresponsive for 30–60 seconds while downloading,'
      + #13#10
      + 'then the .NET installer will appear and complete on its own.'
      + #13#10#13#10
      + 'Click Cancel to abort.',
      mbInformation, MB_OKCANCEL) <> IDOK then
  begin
    Result := False;
    Exit;
  end;

  TempExe := ExpandConstant('{tmp}\dotnet-windowsdesktop-runtime.exe');

  if URLDownloadToFileW(nil,
      'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe',
      TempExe, 0, nil) <> 0 then
  begin
    MsgBox('Download failed. Check your internet connection and try again.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

  // /passive: shows a minimal progress window, no user interaction required.
  // /norestart: suppress any reboot prompt (we handle it below if needed).
  // ewWaitUntilTerminated: blocks until the runtime install finishes.
  if not Exec(TempExe, '/install /passive /norestart', '',
              SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to launch the .NET installer. Please install .NET 10 Desktop Runtime manually.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

  // Exit code 0 = success, 3010 = success + reboot pending (we suppress the reboot).
  // Trust the installer's own exit code — do not re-check the registry.  The MSI
  // chain inside the bootstrapper may not have flushed the registry key yet in the
  // same process session, causing the re-check to return a false negative.
  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    MsgBox('The .NET installer exited with code ' + IntToStr(ResultCode) + '.'
           + #13#10 + 'Please install .NET 10 Desktop Runtime manually and try again.',
           mbError, MB_OK);
    Result := False;
  end;
end;

function ScheduledTaskExists(): Boolean;
var
  ResultCode: Integer;
begin
  // Querying does not require elevation; exit code 0 = the task exists.
  Result := Exec('schtasks.exe', '/Query /TN "' + TaskName + '"', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure RegisterStartupTask();
var
  ResultCode: Integer;
  Params: string;
begin
  // A logon task with RL HIGHEST lets the elevated app auto-start with no boot-time UAC
  // prompt. Creating a HIGHEST task needs admin, so this one step elevates via 'runas'
  // (exactly one UAC prompt — and only because the user ticked "Run at startup").
  Params := '/Create /TN "' + TaskName + '" /TR "\"' + ExpandConstant('{app}\{#AppExe}') +
            '\"" /SC ONLOGON /RL HIGHEST /F';
  if not ShellExec('runas', 'schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    MsgBox('Could not create the startup task. You can still enable "Run on startup" '
           + 'from the app''s tray menu later.', mbInformation, MB_OK);
end;

function AppIsRunning(): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist|find: exit 0 only when the process is present. Works without elevation
  // (the image name is visible even for an elevated process).
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C tasklist /FI "IMAGENAME eq {#AppExe}" /NH | find /I "{#AppExe}"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure StopAppAndRemoveStartupTask();
var
  ResultCode: Integer;
begin
  // Stopping the running (elevated) app and deleting its RL HIGHEST logon task both need
  // admin, so do them together in one elevated cmd -> at most ONE UAC prompt on uninstall.
  ShellExec('runas', ExpandConstant('{cmd}'),
            '/C taskkill /IM "{#AppExe}" /F & schtasks /Delete /TN "' + TaskName + '" /F',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RegisterAutoUpdateTask();
var
  ResultCode: Integer;
  Params: string;
begin
  // Per-user, NON-elevated logon task (runs 5 min after sign-in) that lets winget pull
  // any newer published version silently. No /RL HIGHEST -> creating it needs no admin,
  // so the "Auto update in background" option never triggers a UAC prompt.
  Params := '/Create /TN "' + UpdateTaskName + '" /TR "winget upgrade --id {#WingetId} '
          + '--silent --accept-package-agreements --accept-source-agreements" /SC ONLOGON '
          + '/DELAY 0005:00 /F';
  Exec('schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveAutoUpdateTask();
var
  ResultCode: Integer;
begin
  // Non-elevated; harmless if the task doesn't exist.
  Exec('schtasks.exe', '/Delete /TN "' + UpdateTaskName + '" /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure LaunchApp();
var
  ResultCode: Integer;
begin
  if ScheduledTaskExists() then
    // The elevated logon task exists -> run it on demand to start the app elevated
    // with NO extra UAC prompt (scheduled tasks bypass the consent prompt).
    Exec('schtasks.exe', '/Run /TN "' + TaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    // No task -> launch via the shell so requireAdministrator triggers the single UAC
    // prompt the app needs (a [Run]/CreateProcess launch would just fail here).
    ShellExec('open', ExpandConstant('{app}\{#AppExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('runstartup') then RegisterStartupTask();
    if WizardIsTaskSelected('autoupdate') then RegisterAutoUpdateTask();
    // Auto-launch only on an interactive install (not silent installs). Runs after task
    // creation so a freshly-created startup task is used for a prompt-free launch.
    if not WizardSilent() then LaunchApp();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall fires just BEFORE files are removed — stop the app first so its files
  // aren't locked, otherwise the uninstall leaves the exe behind and the app keeps running.
  if CurUninstallStep = usUninstall then
  begin
    // Elevate once only if there's something elevated to do (app running or HIGHEST task).
    if AppIsRunning() or ScheduledTaskExists() then
      StopAppAndRemoveStartupTask();

    RemoveAutoUpdateTask();   // non-elevated, no prompt
  end;
end;
