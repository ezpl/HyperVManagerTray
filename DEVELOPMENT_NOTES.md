# Development Notes & Lessons Learned

> A memo of the non-obvious findings from building this app, and the reasons behind the
> design choices — so the same mistakes aren't re-made. Read this before changing how the
> app talks to Hyper-V or the host network.

_Last updated: 2026-06-02._

---

## TL;DR — invariants to preserve

1. **Filter out Hyper-V virtual NICs and WFP/NDIS filter adapters** before matching rules or
   picking the "primary" adapter. They share MAC/IP with the real NIC but lie about identity.
2. **Rebind the switch with the two-step `AllowManagementOS $false` → `$true`**, never a single
   `Set-VMSwitch -NetAdapterName … -AllowManagementOS $true`. The single form orphans host vNICs.
3. **Every Hyper-V write must be idempotent** — check current state first, skip if it already
   matches. Otherwise the host network flickers on every launch.
4. **Evaluation is single-flight** (one at a time, coalesced). Don't let switch changes run
   concurrently.
5. **Auto-start is a Scheduled Task, not a `Run` key.** An elevated app can't start from `Run`.
6. **Talk to Hyper-V via `powershell.exe -EncodedCommand`,** not the PowerShell SDK.

---

## Hyper-V + Windows networking gotchas

### 1. `AllowManagementOS=$true` moves the physical NIC's IP onto a virtual NIC
When an External switch shares the adapter with the host, Windows creates a
`vEthernet (<switch>)` "Hyper-V Virtual Ethernet Adapter" and the **physical NIC loses its
IPv4 address** to it.

Consequences that bit us:
- `GetBestInterface` (iphlpapi) returns the **virtual** adapter's index, so naive "primary
  adapter" detection shows `Hyper-V Virtual Ethernet Adapter #N` instead of the real NIC.
- Rule CIDR matching fails against the physical NIC (it has no IP) — the IP is on the vNIC.

**Fix:** `AdapterMatcher.SplitAdapters()` separates physical vs Hyper-V-virtual adapters. When
the physical NIC has no IPv4, fall back to the virtual NIC's IP/gateway/DNS for matching and
display, while still using the **physical NIC's alias** for `Set-VMSwitch`. The bridged physical
NIC is identified as: Up + valid 6-byte MAC + no IPv4 (when `GetBestInterface` returned a vNIC).

### 2. WFP/NDIS filter-layer adapters are decoys
Windows exposes extra `NetworkInterface` objects like
`Ethernet-WFP Native MAC Layer LightWeight Filter-0000`. They share the real NIC's MAC and IP
but are **not valid `Set-VMSwitch -NetAdapterName` targets** — binding to one hangs (see #6).

**Fix:** `IsFilterLayerAdapter()` excludes anything whose name/description contains `-WFP `,
`-NDIS `, or `LightWeight Filter`. `FriendlyAdapterName()` also strips those suffixes for display.

### 3. Orphaned `vEthernet (<switch>)` NICs accumulate on rebind  ⚠️ the big one
Running `Set-VMSwitch -Name X -NetAdapterName <new> -AllowManagementOS $true` to **rebind a
shared switch to a different adapter** leaves the previous host management vNIC behind. Over
time these pile up (we found **5**). Symptoms:
- Hyper-V Manager **locks the switch settings**: _"Some of the settings cannot be changed
  because you have multiple network adapters for the management operating system."_
- The dead vNICs keep **stale metric-0 default routes**, which **intermittently black-hole
  host traffic** (the host "randomly" can't reach the network).

**Fix (`UpdateSwitchBindingAsync`):** two-step rebind — `Set-VMSwitch -AllowManagementOS $false`
(Windows removes the old vNIC) **then** `Set-VMSwitch -NetAdapterName <new> -AllowManagementOS
$true`. Exactly one management vNIC survives.

**Cleanup recipe** if ghosts already exist (elevated): set `-AllowManagementOS $false`, remove
every leftover `vEthernet (<switch>)*` via `pnputil /remove-device <PnPDeviceID>`, then set
`-AllowManagementOS $true` to recreate a single clean one.

### 4. The two-step rebind is slow (~25 s) and non-atomic
The toggle takes ~22–28 s. Two follow-on hazards:
- **It raced the 30 s PowerShell timeout.** On overrun the process was killed *between*
  `$false` and `$true`, leaving the host adapter with **no vNIC and no IP** until the next
  successful bind. → The bind now uses a **120 s** timeout (`BindTimeout`).
- **A kill/crash mid-sequence strands the host adapter.** Don't `Stop-Process` the app during a
  rebind. Recovery: re-enable "Allow management operating system to share this adapter" on the
  switch (Hyper-V Manager, or `Set-VMSwitch -AllowManagementOS $true`).

### 5. The rebind causes self-induced `NetworkChange` churn (the "double-flip")
Dropping the host vNIC during the toggle fires `NetworkChange` events. Mid-rebind the IP is
gone, so evaluation matches **Fallback** and flips the VM Bridged→Default→Bridged. Overlapping
`async void` timer callbacks made it worse by running concurrently.

**Fix (`NetworkMonitor`):** single-flight evaluation with coalescing — a `SemaphoreSlim(1,1)`
plus an `_evaluatePending` flag. Only one evaluate/apply runs at a time; events that arrive
during it collapse into exactly one follow-up pass that settles correctly.

> Note: reverting to a single-command rebind would remove the double-flip **but** brings back
> #3 (ghost NICs). The two-step + coalescing is the accepted trade-off.

### 6. A bad `Set-VMSwitch` target hangs and wedges everything
Binding to a WFP filter adapter (#2) made `powershell.exe` hang on a WMI/DCOM lookup that never
timed out. Because Hyper-V calls are serialized behind one semaphore, the hang blocked
**all** later calls — the VM never switched and the tray icon never updated.

**Fix:** filter WFP adapters (#2) **and** `ProcessRunner` kills the process tree after a timeout.

### 7. Network flicker on every app launch (idempotency)
The in-memory "skip if unchanged" guards (`_lastApplied`, `_lastBoundAdapterInterface`) start
**empty** on launch, so the first evaluation always re-applied — running the disruptive toggle
and `Connect-VMNetworkAdapter` even when nothing had changed.

**Fix:** both Hyper-V writes check current state first and **skip** when it already matches
(switch already External+sharing+bound; VM NIC already on the target switch). If the check
can't confirm a match it falls through to applying (fail-safe).

---

## .NET / packaging gotchas

### 8. `Microsoft.PowerShell.SDK` breaks self-contained single-file
The in-process runspace calls `PSSnapInReader`, which reads a registry key that is **absent in
self-contained deployments**, returning null and failing to initialise.

**Fix:** spawn `powershell.exe` (Windows PowerShell 5.1, always present) with a Base64
`-EncodedCommand` (sidesteps all quoting). See `HyperVManager` + `ProcessRunner`.

### 9. `HKCU\…\Run` cannot auto-start an elevated app
The app is `requireAdministrator`. Windows launches `Run`-key items with a **standard token**
and silently skips apps that demand elevation — so the old "Run on startup" never actually
started it (and didn't even show in Task Manager until reopened).

**Fix (`StartupManager`):** a Scheduled Task with `/SC ONLOGON /RL HIGHEST`. It runs in the
user's interactive session (tray icon appears) with **no UAC prompt**. The obsolete `Run` value
is cleaned up on toggle (`StartupManager`).

### 10. Pre-`Application.Run()` there is no WinForms `SynchronizationContext`
`SynchronizationContext.Current` is null before the message loop starts, so posting UI updates
via a captured context goes to the thread pool and races the first paint (empty popup on first
click). **Fix (historical, WinForms):** the old `TrayApplication` pre-populated the popup
synchronously. Under WinUI this is gone — the dashboard refreshes itself before `AppWindow.Show()`.

---

## WinUI 3 migration (v2.0 — the app moved off WinForms)

The UI was rewritten in **WinUI 3 / Windows App SDK** (unpackaged, `WindowsPackageType=None`) to
match the sibling LenovoTray app: Mica dashboard, tray icon via **H.NotifyIcon.WinUI**, per-VM
control cards. The network/Hyper-V core (`NetworkMonitor`, `AdapterMatcher`, `HyperVManager`,
`ConfigManager`, `ProcessRunner`, `StartupManager`) ported **unchanged** — it was already UI-agnostic.

Gotchas hit (and how they're handled):
- **The `.pri` resource index.** An unpackaged WinUI publish must ship `<App>.pri` next to the exe,
  or `Microsoft.UI.Xaml.dll` throws a stowed exception **0xC000027B** at startup. The `.csproj` has
  a `CopyAppPriToPublish` target; `installer\build-installer.ps1` verifies it landed.
- **WinForms can't host a WinUI 3 window**, so this was a full UI migration, not a bolt-on. A hidden
  `MainWindow` keeps the app alive while only the tray icon + popup are visible.
- **Native tray menu caveat (H.NotifyIcon):** the right-click menu is rebuilt as a native Win32 menu
  each time; XAML `Click`/`Opening` events do NOT fire — bind `Command` (`RelayCommand`) instead, and
  resync dynamic items in `TrayMenu.RefreshState()` before it opens.
- **UI thread marshaling** is now `DispatcherQueue.TryEnqueue` (was `SynchronizationContext.Post`).
  `NetworkMonitor.SwitchApplied` still fires on a background thread.
- **No `MessageBox`** in WinUI — small `MessageBoxW` P/Invokes in `NativeMethods` cover errors/confirms.
- **Publish is a folder, not a single file.** `PublishSingleFile`/`EnableCompressionInSingleFile`
  don't apply; the per-user Inno Setup installer copies the folder (config.json installed
  `onlyifdoesntexist` so user edits survive upgrades). `PublishTrimmed=false`
  (WinUI + reflection-y JSON trim poorly) — so reflection-based `System.Text.Json` is kept;
  `PublishReadyToRun=true` on Release for faster startup.
- **Dashboard polling** (CPU/mem/VHD via `Get-VM`) runs on a `DispatcherTimer` **only between
  `Activated` and hide/close** — preserving the zero-idle property when the dashboard is shut.

---

## Resource notes (reviewed 2026-06-02; WinForms-era figures)

The app is already cheap: **~0 idle CPU** (fully event-driven — `NetworkChange` +
`FileSystemWatcher`, no polling) and **~13–16 MB private memory**.

| Lever | Verdict |
|---|---|
| `InvariantGlobalization=true` | **Kept.** ICU not loaded → ~16→13 MB RAM. No localized UI, all formatting/parsing is ordinal/invariant. Does **not** shrink the self-contained file without trimming. |
| Remove unused `Logging.Console` pkg | **Kept.** Only the custom file sink is wired up. |
| Cache GDI fonts/brushes in `StatusPopupForm` | **Kept.** No per-repaint allocation. |
| `EnableCompressionInSingleFile` | **Rejected.** Shrinks exe 112→49 MB but decompresses into memory → **+40 MB RAM**. For an always-on tray app, RAM > disk. |
| `PublishTrimmed` | Not attempted — WinForms trims poorly (reflection). Risky. |

---

## Testing / ops cautions (for whoever runs this next)

- **Don't kill the app mid-rebind** (~25 s window) — it can strand the host adapter (#4).
- **Launch the elevated exe in the foreground.** `Start-Process` from a background/non-interactive
  context gets its UAC prompt auto-cancelled ("operation was canceled by the user").
- **Don't do unprompted elevated host-network surgery.** Reconfiguring a live `Set-VMSwitch` is
  the user's call; offer the commands or the Hyper-V Manager steps instead.
- `config.json` rules match on **MAC + CIDR**. WiFi on the same subnet as a bridged cable will
  **not** match a cable rule (different MAC) — that's intended; it falls back to NAT.
- Verify a healthy bridge with: one `Up` `vEthernet (Bridged)` carrying the LAN IP, and no
  numbered `vEthernet (Bridged) N` siblings.
