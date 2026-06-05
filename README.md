# HyperV Network Switcher

A Windows system-tray application that automatically connects your Hyper-V virtual machine to the correct virtual network switch based on which physical network the host is connected to.

> ### 🤖 100% vibe coded
> This project was written **entirely** by an AI agent ([Claude](https://www.anthropic.com/claude), via Claude Code) through conversational prompts — design, implementation, debugging, deployment scripts, and this README. No line of the source was hand-written by a human. It's shared as-is, as an experiment in what end-to-end "vibe coding" produces. Read the code with that in mind, and review before relying on it.

---

## What it does

When you move between networks — office LAN, home Wi-Fi, mobile hotspot — the virtual machine needs a different network connection:

| Host network | VM should use |
|---|---|
| Office LAN (`10.0.0.0/23`, adapter `AA:BB:CC:DD:EE:FF`) | **Bridged** switch (full LAN access) |
| Anything else | **Default Switch** (NAT, always works) |

The app watches for network changes in the background. The moment the host connects to a recognised network, the VM's NIC is silently reconnected to the right Hyper-V virtual switch. If no rule matches, it falls back to the Default Switch automatically.

It also includes a **WinUI 3 dashboard** (left-click the tray icon) that shows the live host-network/switch status and a control card per configured VM — state, CPU / memory / VHD-size meters, and power buttons (Start, Shutdown, Pause, Resume, Save, Connect, Start & Connect). A rule can optionally **auto-start** its VMs when its network becomes active.

---

## Requirements

- Windows 11 host with **Hyper-V** enabled
- The user account must be a member of the **Hyper-V Administrators** group (or run as Administrator)
- The two virtual switches **Bridged** and **Default Switch** must exist in Hyper-V Virtual Switch Manager
- To **build** from source: the .NET 10 SDK with the Windows App SDK workload. The published app is **self-contained** — no .NET or Windows App Runtime install required on the target.

---

## Setup

1. Edit `config.json` (lives next to the `.exe`) to describe your networks and VMs — see [Configuration](#configuration) below.
2. Run or publish the application (see below).
3. A tray icon appears. **Left-click** it for the status dashboard + VM controls; **right-click** for the menu (manual override, add a network rule, etc.).

---

## Running

### Development / debug run

```powershell
dotnet run
```

> The app requires elevation (UAC prompt) because it controls Hyper-V switches.

### Install (recommended)

A **per-user Inno Setup installer** builds from `installer\` (no admin needed to install — the
app elevates itself at runtime):

```powershell
# one-time, if Inno Setup is missing:
winget install JRSoftware.InnoSetup

.\installer\build-installer.ps1 -Version 2.0.0
```

This publishes the app fully self-contained (.NET + Windows App SDK bundled, no runtime install
needed on the target) and compiles `installer\Output\HyperVNetworkSwitcher-Setup.exe`. Run that
to install to `%LocalAppData%\Programs\HyperVNetworkSwitcher`. The setup offers an optional
**Run at startup** (a `/RL HIGHEST` logon task — one UAC prompt, only if ticked) and preserves
any existing `config.json` on upgrade. See [`installer/README.md`](installer/README.md) for how
the elevation is handled.

### Publish manually (no installer)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true
```

Output folder: `bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\` (run `HyperVNetworkSwitcher.exe` from there; the `.pri` next to it is required).

### Auto-start with Windows

Right-click the tray icon → **Run on startup** to toggle auto-start at login.

Because this app requires elevation (UAC), it **cannot** auto-start from a `HKCU\...\Run` entry — Windows launches Run-key items with a standard token and silently skips apps that demand administrator rights. Instead, the toggle creates a **Scheduled Task** with "Run with highest privileges" and a logon trigger. The task runs in your interactive session, so the tray icon still appears, with no UAC prompt at logon.

The toggle is equivalent to:

```powershell
# Enable
schtasks /Create /TN "HyperVNetworkSwitcher" /TR "\"%LOCALAPPDATA%\Programs\HyperVNetworkSwitcher\HyperVNetworkSwitcher.exe\"" /SC ONLOGON /RL HIGHEST /F
# Disable
schtasks /Delete /TN "HyperVNetworkSwitcher" /F
```

**Where it's stored** — the task named `HyperVNetworkSwitcher` lives in:

| | |
|---|---|
| **Task Scheduler** | Task Scheduler Library → `HyperVNetworkSwitcher` |
| **On disk** | `C:\Windows\System32\Tasks\HyperVNetworkSwitcher` (XML definition) |
| **Registry** | `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\HyperVNetworkSwitcher` |

To inspect or verify it from PowerShell:

```powershell
schtasks /Query /TN "HyperVNetworkSwitcher" /V /FO LIST
```

> **Migration note:** older versions wrote a value named `HyperVNetworkSwitcher` under `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`. That entry never worked for this elevated app and is now removed automatically the first time you toggle **Run on startup**.

---

## Dashboard (left-click the tray icon)

A borderless Mica popup titled **"Hyper-V Manager"** near the tray:

- **HOST NETWORK** — Adapter, IP, Gateway, DNS of the active host network.
- **Per-VM cards** (one per configured VM) — switch name and active rule shown as a subtitle; state (Running/Off/Paused/Saved); CPU / memory / VHD-size meters when running; power buttons appropriate to the state: **Start**, **Shutdown**, **Pause**, **Resume**, **Save**, **Connect**, **Start & Connect**.

Metrics refresh every ~2 s **only while the dashboard is open**, so a closed dashboard costs no CPU.

## Context menu (right-click the tray icon)

| Item | Description |
|---|---|
| **Force Re-evaluate** | Re-runs rule matching and applies any change immediately |
| **VM Power ▶** | Per-VM submenu: Start, Start & Connect, Shutdown, Pause, Resume, Save |
| **Manual Override ▶** | Force a specific VM → switch combination |
| **Add current network as bridged** | Detects the current adapter, confirms, and appends a new Bridged rule to `config.json` |
| **Open config.json** | Opens the config file in your default `.json` editor (falls back to Explorer) |
| **Open log file** | Opens `switcher.log` |
| **Reload config** | Hot-reloads `config.json` without restarting |
| **Run on startup** | Toggle auto-start at Windows login |
| **Exit** | Stops the application |

---

## Configuration

`config.json` is loaded from the same directory as the executable. It is watched for changes — edits take effect immediately without a restart.

```jsonc
{
  "virtualMachines": [
    {
      "name":          "MyVM",             // Hyper-V VM name (exact)
      "nicName":       "Network Adapter",  // NIC name inside Hyper-V manager
      "defaultSwitch": "Default Switch"    // Fallback switch for this VM
    }
  ],
  "rules": [
    {
      "name":          "Office LAN",       // Shown in the tray status
      "priority":      1,                  // Lower = evaluated first
      "conditions": {
        "adapterMac":  "AA:BB:CC:DD:EE:FF", // Host NIC MAC (optional)
        "ipCidr":      "10.0.0.0/23"         // Host IP must fall in this range (optional)
      },
      "virtualSwitch": "Bridged",          // Hyper-V switch to connect to
      "targetVms":     ["MyVM"],           // VMs to reconnect
      "autoStart":     false               // start/resume targetVms when this rule activates
    }
  ],
  "fallback": {
    "virtualSwitch": "Default Switch",     // Used when no rule matches
    "targetVms":     ["MyVM"]
  }
}
```

### Adding a new network rule

**Option A — from the tray:** Connect to the network, then right-click the tray icon → **Add current network as bridged**. The app reads the current adapter MAC and subnet automatically.

**Option B — manually:** Add an object to the `rules` array in `config.json`. Both `adapterMac` and `ipCidr` are optional; omitting both means the rule matches any active adapter.

---

## Project structure

```
HyperVNetworkSwitcher/
├─ App.xaml(.cs)            WinUI app entry point — owns services, tray icon, dashboard
├─ MainWindow.xaml(.cs)     hidden host window (keeps the app alive)
├─ Services/                UI-agnostic logic (no WinUI dependency)
│  ├─ NetworkMonitor.cs        watches NICs, debounces, drives switch changes
│  ├─ AdapterMatcher.cs        rule evaluation (MAC + CIDR), adapter selection
│  ├─ HyperVManager.cs         runs Hyper-V cmdlets via powershell.exe; VM power + metrics
│  ├─ ConfigManager.cs         loads/watches config.json
│  ├─ StartupManager.cs        "run at startup" scheduled task
│  ├─ ProcessRunner.cs         shared process-spawning helper (timeout, stream capture)
│  └─ FileLogger.cs            minimal ILogger file sink
├─ Models/                  POCOs: AppConfig, NetworkRule, VmTarget, VmStatus
├─ UI/                      DashboardWindow (Mica popup + VM cards), TrayMenu
├─ Helpers/                 AppColors, IconGenerator, NativeMethods, RelayCommand
├─ Tests/                   xUnit tests (links the pure Services/Models sources)
├─ installer/              per-user Inno Setup installer (.iss + build script)
└─ config.json             sample config (shipped next to the exe)
```

## Tests

```powershell
dotnet test
```

`Tests/` is a small xUnit project covering the pure logic — CIDR/MAC matching (`AdapterMatcher`),
the VM status maths (`VmStatus`), and the `config.json` contract (including `autoStart`). It
**links** the relevant source files rather than referencing the WinUI app, so `dotnet test` needs
no Windows App SDK runtime. The UI/Hyper-V layers are exercised by building and running the app.

## Development notes

Non-obvious Hyper-V / Windows-networking gotchas and the reasoning behind the design choices
are written up in [`DEVELOPMENT_NOTES.md`](DEVELOPMENT_NOTES.md) — read it before changing how
the app talks to Hyper-V or the host network.

---

## Built with

- **Language / UI:** C# on **.NET 10**, **WinUI 3 / Windows App SDK** (`net10.0-windows10.0.26100.0`, unpackaged, Mica backdrop)
- **OS integration:** Win32 P/Invoke (`iphlpapi.dll` `GetBestInterface`, `user32.dll`/`Shcore.dll` for popup positioning + message boxes), and the Windows-bundled `powershell.exe` (Hyper-V cmdlets) and `schtasks.exe` (auto-start task)

### External libraries

| Package | Version | Author | Purpose | License |
|---|---|---|---|---|
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) | 2.1.3 | Microsoft | WinUI 3 framework (windowing, XAML, Mica) | MIT |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | 10.0.28000.1839 | Microsoft | Windows SDK build tooling for the App SDK | MIT |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | Dmitry Kolchev (HavenDV) | System-tray icon + native context menu for WinUI 3 | MIT |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | 10.0.8 | Microsoft | Renders the tray `.ico` at runtime | MIT |
| [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging) | 10.0.8 | Microsoft | Logging abstraction; output goes to a small custom file sink | MIT |

All are MIT-licensed. The only non-Microsoft dependency is **H.NotifyIcon.WinUI** (used for the tray icon, the same way the sibling LenovoTray app does).

---

## License

MIT — see [`LICENSE`](LICENSE).

---

## Logging

Logs are written to:
```
%APPDATA%\HyperVNetworkSwitcher\switcher.log
```

Each switch change, rule evaluation, and error is recorded there.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| UAC prompt on every launch | Normal — required for Hyper-V access. Enable **Run on startup** for a prompt-free elevated auto-start. |
| Status shows "Fallback" on the office LAN | MAC or CIDR in the rule does not match — check `switcher.log` |
| VM card shows "Unknown" / no CPU·memory meters | The `config.json` VM name doesn't match a VM on the host, or the VM isn't running (only running VMs report metrics) |
| Switch change fails silently | User account lacks Hyper-V Administrator rights |
| Dashboard opens blank / `0xC000027B` at startup | The `.pri` resource index isn't next to the exe — re-run the installer or copy the whole publish folder |
