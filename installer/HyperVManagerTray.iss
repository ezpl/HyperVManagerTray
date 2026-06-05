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
#define AppPublisher  "Zero Zero Software"
#define AppUrl        "https://github.com/ezpl/HyperVManagerTray"
; Matches the task name used by the app's in-tray "Run on startup" toggle (StartupManager),
; so the installer option and the tray toggle control the exact same logon task.
#define TaskName      "HyperVManagerTray"

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
OutputBaseFilename=HyperVManagerTray-Setup
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
; IconFilename points to AppIcon.ico (Zero Zero Software brand icon) shipped next to the exe
; so the Start Menu shortcut shows the correct icon immediately.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\AppIcon.ico"; Comment: "Hyper-V VM network and power manager"

[Tasks]
Name: "runstartup"; Description: "Run {#AppName} automatically at sign-in (starts elevated without a UAC prompt at boot)"; Flags: unchecked

; Generated at runtime by the app — remove on uninstall so the folder can be cleaned up.
[UninstallDelete]
Type: files;      Name: "{app}\icon-bridged-v2.ico"
Type: files;      Name: "{app}\icon-fallback-v2.ico"
Type: files;      Name: "{app}\AppIcon.ico"
Type: files;      Name: "{app}\app.ico"
; v1 names — clean up if upgrading from an older install
Type: files;      Name: "{app}\switch-blue.ico"
Type: files;      Name: "{app}\switch-grey.ico"
Type: dirifempty; Name: "{app}"

; NOTE: launching the app is handled in [Code] (LaunchApp), not [Run]. A [Run] entry uses
; CreateProcess, which CANNOT start a requireAdministrator exe (fails with "elevation
; required"). LaunchApp starts it correctly — via the elevated logon task if one exists
; (no extra prompt), otherwise via ShellExec (the single UAC prompt the app needs).

[Code]
const
  TaskName = '{#TaskName}';

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
  end;
end;
