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
// DETECTION — Microsoft's recommended method (two-level with filesystem fallback):
//
// Primary: HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\{version}
//   In a 32-bit Inno Setup process, HKLM reads the WOW6432Node hive, which is
//   also where the .NET installer writes the 32-bit-accessible view of these keys.
//   HKLM64 accesses the native 64-bit view.  Both are checked for compatibility.
//   Source: https://learn.microsoft.com/en-us/dotnet/core/install/how-to-detect-installed-versions
//
// Fallback: filesystem check via the dotnet install path from HKLM64\sharedhost\Path.
//   On some machines (e.g. windowsdesktop-runtime bundle installs) the sharedfx
//   subkeys are not written.  The runtime payload directory is always present.
function IsDotNet10DesktopInstalled: Boolean;
var
  SubKeyNames: TArrayOfString;
  I: Integer;
  SharedfxPath, DotNetPath: string;
  FindRec: TFindRec;
begin
  Result := False;
  SharedfxPath := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

  // Check 1: 32-bit registry view (WOW6432Node — what HKLM gives a 32-bit process)
  if RegGetSubkeyNames(HKLM, SharedfxPath, SubKeyNames) then
    for I := 0 to GetArrayLength(SubKeyNames) - 1 do
      if Copy(SubKeyNames[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;

  // Check 2: 64-bit registry view (native hive via HKLM64)
  if RegGetSubkeyNames(HKLM64, SharedfxPath, SubKeyNames) then
    for I := 0 to GetArrayLength(SubKeyNames) - 1 do
      if Copy(SubKeyNames[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;

  // Check 3: filesystem fallback — runtime payload directory under the dotnet root.
  // Read the install path from the registry; fall back to the default Program Files
  // location so non-standard installs are still detected.
  if not RegQueryStringValue(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost',
      'Path', DotNetPath) then
    DotNetPath := ExpandConstant('{pf64}\dotnet\');
  if (Length(DotNetPath) > 0) and (DotNetPath[Length(DotNetPath)] <> '\') then
    DotNetPath := DotNetPath + '\';
  if FindFirst(DotNetPath + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    FindClose(FindRec);
    Result := True;
  end;
end;

// urlmon.dll — synchronous HTTPS download; fallback when winget is unavailable.
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
      + 'Click OK to install it automatically (requires internet access).'
      + #13#10
      + 'Click Cancel to abort.',
      mbInformation, MB_OKCANCEL) <> IDOK then
  begin
    Result := False;
    Exit;
  end;

  // Preferred path: Windows Package Manager (winget).
  // winget handles the download and installation silently — no explicit download
  // step, no separate installer window.  Available on Windows 10 21H1+ / Windows 11.
  // Source: https://learn.microsoft.com/en-us/windows/package-manager/winget/
  if Exec('winget.exe',
      'install --id Microsoft.DotNet.DesktopRuntime.10 --silent ' +
      '--accept-package-agreements --accept-source-agreements',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // 0 = installed; -1978335189 (0x8A150013) = already installed (concurrent install race)
    if (ResultCode = 0) or (ResultCode = -1978335189) then Exit;
    // Any other code: fall through to direct-download fallback.
  end;

  // Fallback: winget is unavailable or reported an error.
  // Download the official bootstrapper and run it with /passive so the user
  // sees a minimal progress window without manual interaction.
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

  if not Exec(TempExe, '/install /passive /norestart', '',
              SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to launch the .NET installer. Please install .NET 10 Desktop Runtime manually.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

  // Trust the bootstrapper's exit code (0 = success, 3010 = success + reboot pending).
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

  // Give the app a moment to elevate and initialise, then verify it is running.
  // If the user dismissed the UAC prompt the app will not appear — that is not an
  // error, but we still show the note so they know how to launch it manually.
  Sleep(3000);
  if not AppIsRunning() then
    MsgBox('Hyper-V Manager Tray was installed but did not start automatically.'
           + #13#10#13#10
           + 'You can launch it from the Start Menu at any time.'
           + #13#10
           + 'The app requires administrator privileges — Windows will prompt once on first launch.',
           mbInformation, MB_OK);
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
