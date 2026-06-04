# Installer (Inno Setup)

A **per-user** Inno Setup installer for HyperV Network Switcher — the same kind the sibling
LenovoTray app uses.

## Build it

```powershell
# one-time, if Inno Setup is missing:
winget install JRSoftware.InnoSetup

.\build-installer.ps1 -Version 2.0.0
```

`build-installer.ps1`:
1. Publishes the app fully self-contained (win-x64, Windows App SDK + .NET bundled, no trimming).
2. Compiles `HyperVNetworkSwitcher.iss` with `ISCC.exe`.

Output: `installer\Output\HyperVNetworkSwitcher-Setup.exe` (and its SHA-256).

## How elevation works

- **Installing needs no admin.** `PrivilegesRequired=lowest` → installs under
  `%LocalAppData%\Programs\HyperVNetworkSwitcher`, no UAC for the install itself.
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
