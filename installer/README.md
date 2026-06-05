# Installer (Inno Setup)

A **per-user** Inno Setup installer for Hyper-V Manager Tray — the same kind the sibling
LenovoTray app uses.

## Build it

```powershell
# one-time prerequisites (on a fresh dev machine):
winget install JRSoftware.InnoSetup   # Inno Setup compiler
.\sign.ps1 -Setup                     # create + trust the self-signed code-signing cert

.\build-installer.ps1 -Version 2.0.1
```

`build-installer.ps1`:
1. Auto-generates `Assets\app.ico` if it is missing (calls `Generate-AppIcon.ps1`).
2. Publishes the app fully self-contained (win-x64, Windows App SDK + .NET bundled, no trimming).
3. Compiles `HyperVManagerTray.iss` with `ISCC.exe`.

Output: `installer\Output\HyperVManagerTray-Setup.exe` (and its SHA-256). Installer
wizard shows the app icon (`app.ico`) in the `[Setup]` `SetupIconFile` entry.

## How elevation works

- **Installing needs no admin.** `PrivilegesRequired=lowest` → installs under
  `%LocalAppData%\Programs\HyperVManagerTray`, no UAC for the install itself.
- **The app is `requireAdministrator`** and elevates itself at runtime (the one UAC prompt it
  needs for Hyper-V).
- **"Run at startup"** (optional, off by default) creates a `/RL HIGHEST /SC ONLOGON` scheduled
  task via an elevated `schtasks` (one UAC prompt, only if you tick it). That task then starts
  the elevated app at every sign-in with **no** boot-time prompt. It uses the same task name as
  the app's tray **Run on startup** toggle, so the two stay in sync.
- **Post-install launch** runs the logon task if it exists (no prompt), otherwise shell-launches
  the exe (the single UAC prompt). It never uses a `[Run]` entry — `CreateProcess` can't start a
  `requireAdministrator` exe.
- **Uninstall** stops the running app and deletes the startup task in one elevated step (at most
  one UAC prompt), then removes the files.

User config (`config.json`) is preserved across upgrades (installed `onlyifdoesntexist`).

## Code signing

The Release build is automatically code-signed via the `SignOutput` MSBuild target in
`HyperVManagerTray.csproj`. It calls `sign.ps1` after each Release build.

The certificate is self-signed (`CN=Zero Zero Software`) — the same cert used by the sibling
LenovoTray project. On a fresh dev machine, run `.\sign.ps1 -Setup` once to create it and
register it as a trusted root + trusted publisher (eliminates "Unknown Publisher" on UAC prompts).

- No `.pfx` file is stored in the repo — the cert lives in `Cert:\CurrentUser\My`.
- If the cert is absent, signing is skipped gracefully (the build still succeeds).
- The `app.ico` file (in `Assets\`) is embedded in the exe's PE resources (Start Menu,
  taskbar, Explorer) and is also bundled next to the exe so the installer shortcut displays
  the correct icon. It is regenerated automatically if absent by `build-installer.ps1`.
- The installer wizard itself also shows `app.ico` (`SetupIconFile` in `[Setup]`).
