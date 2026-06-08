# Build & Deploy Assessment — HyperVManagerTray ↔ LenovoPowerTray

_Date: 2026-06-07 · App version at time of writing: 2.1.8_

This document records (1) the build/deploy improvements just applied to HyperVManagerTray,
(2) a fresh comparison showing where HyperVManagerTray now leads LenovoPowerTray, (3) an
analysis of a shared build/test/release framework for these two apps and future ones, and
(4) prioritized suggestions.

---

## 1. Improvements applied (closing the original gaps)

The original comparison flagged four gaps in HyperVManagerTray. All are now closed:

| # | Gap | Resolution |
|---|-----|------------|
| 1 | No "Auto update in background" installer task | Added the `autoupdate` Inno task + `RegisterAutoUpdateTask` / `RemoveAutoUpdateTask` (non-elevated `winget upgrade` logon task, 5-min delay), wired into install/uninstall. |
| 2 | No winget manifests | Added `installer/winget/` with the three 1.6.0 YAML files for `0z00z0.HyperVManagerTray`. Passes `winget validate`. |
| 3 | No automated release pipeline | Added `.github/workflows/release.yml`: tag-triggered build → **unit tests** → sign → SHA256 → patch manifests → validate → release → post-release re-validate. |
| 4 | `sign.ps1` at repo root (layout drift) | Moved to `scripts/sign.ps1`; updated the csproj `SignOutput` target, `build-installer.ps1`, and the script's own default-path resolution. |

README updated with winget install/upgrade instructions and a note on the automated release flow.

---

## 2. Reverse comparison — where HyperVManagerTray now leads

After the changes, the two pipelines are at parity on installer mechanics, signing, winget
manifests, and release automation. HyperVManagerTray is now **ahead** in these areas:

| Area | HyperVManagerTray | LenovoPowerTray | Why HyperV is better |
|------|-------------------|-----------------|----------------------|
| **Unit tests** | 77 tests across 6 files; **release is gated on them** in CI | **None** | Releases can't ship if logic regresses; Lenovo has no automated correctness check at all. |
| **Installer signing integrity (CI)** | Re-compiles **and re-signs** the installer *after* the app exe is signed | Signs `publish\*.exe` *after* the installer is already built | Lenovo's released installer embeds an **unsigned** app exe; HyperV's never does. |
| **Version management** | `build-installer.ps1` auto-bumps the patch from `<Version>` and writes it back | Requires explicit `-Version` | Frictionless local releases; one source of truth. |
| **Config preservation** | `config.json` installed `onlyifdoesntexist`; user edits survive upgrades | n/a (no user config) | Real upgrade-safety concern handled correctly. |
| **Uninstall hygiene** | `[UninstallDelete]` removes runtime-generated icons (incl. legacy v1 names) | Not needed | Leaves no orphaned files behind. |
| **Solution hygiene** | Dedicated `Tests/` project linking only the source files under test | — | Test isolation without bloating the app build. |

Both still share: identical `sign.ps1` logic, the same self-signed `CN=Zero Zero Software`
cert, the same per-user Inno model (`PrivilegesRequired=lowest`, `/RL HIGHEST` startup task,
`CloseApplications=yes`/`RestartApplications=no`), and a near-identical `release.yml` shape.

LenovoPowerTray still has one thing HyperV doesn't need: a native C++ bridge build
(`native\build.cmd` → `LenPower.dll`) with MSVC setup in CI. HyperV is pure-managed.

---

## 3. A common framework for tests / build / publish / deploy

**Verdict: yes — recommended, and largely low-risk.** The two apps already share ~90% of
their build/deploy surface by copy-paste, which is exactly the drift risk the original report
warned about (e.g. `sign.ps1` can diverge silently). A shared framework removes the duplication
and makes a third app nearly free to onboard.

### What is genuinely common vs. per-app

**Common (extract once):**
- `sign.ps1` — identical except `FriendlyName` and a temp filename (both parameterizable).
- The Inno `[Code]` block (startup task, auto-update task, launch/uninstall logic) — identical.
- `release.yml` shape — build → test → sign → SHA → patch manifests → validate → release.
- The csproj `SignOutput` MSBuild target and common metadata (Company, Authors, Copyright,
  `InvariantGlobalization`, platform/runtime).
- The winget manifest trio (pure templating: id, name, version, url, sha, description, tags).

**Per-app (stays local, small):**
- App name, exe name, winget id, AppId GUID, project path.
- Whether a native pre-build step is needed (Lenovo yes, HyperV no).
- Whether there's a config file to preserve.
- Test project path (or "no tests").
- Icon assets and `[UninstallDelete]` entries.

### Recommended shape (in priority order)

1. **Reusable GitHub Actions workflow** (`on: workflow_call`) in a central repo
   (`0z00z0/.github` or `0z00z0/build-tools`). Each app's `release.yml` becomes a ~15-line
   caller passing inputs: `app_name`, `exe_name`, `winget_id`, `project_path`,
   `test_project` (optional), `native_build_cmd` (optional). This is the highest-leverage
   change — CI logic lives in one place and every app inherits fixes (like the
   re-sign-installer ordering) automatically.

2. **`Directory.Build.props`** shared via a small internal NuGet package (or a pinned file):
   centralizes the common `<PropertyGroup>` metadata and the `SignOutput` target so signing
   behaviour can't drift between apps.

3. **Parameterized `build-installer.ps1`** driven by a tiny per-repo `build.config.json`
   (`{ name, exe, wingetId, project, nativeBuildCmd, testProject, preserveConfig }`). One
   script, data-driven per app. Distribute via the same `build-tools` repo (git submodule or
   a CI checkout step).

4. **Shared Inno include** (`common.iss`) with the `[Code]` procedures, `#include`d by each
   app's `.iss`, which only defines its `#define`s and app-specific `[Files]`/`[Tasks]`.

5. **A GitHub template repository** for new tray apps that wires all of the above together,
   so app #3 starts with tests + signing + winget + release on day one.

### Trade-offs
- Reusable workflows and `Directory.Build.props` are the cleanest (native to the toolchains,
  no submodule friction). Start there.
- A shared `build-tools` repo consumed via submodule adds a little checkout friction but is
  the most flexible for the PowerShell + Inno pieces.
- Don't over-abstract: keep the per-app `build.config.json` tiny and readable.

---

## 4. Prioritized suggestions

> **Status update (2026-06-08):** the signing and CI-hygiene findings below have been applied.
> See `docs/SIGNING.md` for the signing details.

### High — required for real winget distribution
1. ✅ **CI signing secrets added.** The local cert was exported to a password-protected PFX and
   stored as `CODE_SIGN_PFX` / `CODE_SIGN_PASSWORD`, so tag-built CI releases are now signed
   **identically to local builds**. Also fixed a latent bug where the sign step's `if:` read a
   step-level env (always empty) → it would have skipped signing; secrets are now job-level.
2. ✅ **Signing hardened as far as a free cert allows** — SHA-256 + RFC-3161 timestamp (survives
   cert expiry), app exe signed before the installer is (re)compiled + signed, public cert
   shipped (`scripts/ZeroZeroSoftware.cer`) so users/admins can opt into trust, honest docs in
   `docs/SIGNING.md`. **Remaining (needs money or an OSS cert):** a publicly-*trusted* chain to
   remove SmartScreen for everyone — best free path is **SignPath Foundation** (OSS), else
   **Azure Trusted Signing** (~$10/mo) or an OV/EV CA cert. Any of these drops into the same
   `CODE_SIGN_PFX` secret.
3. ⏳ **winget delivery channel — documented, not auto-submitted.** In-repo manifests + release
   assets don't make `winget install 0z00z0.HyperVManagerTray` work from the public source; that
   needs a `microsoft/winget-pkgs` PR (`wingetcreate`). Their automated validation will flag a
   self-signed installer, so public submission is deferred until the cert is publicly trusted
   (item 2). Today: install via `winget install --manifest installer\winget`.

### Medium — quality & maintainability
4. ✅ **Added `ci.yml`** — build + 77 tests on every push/PR to `master`.
5. ⏳ **Add unit tests to LenovoPowerTray** (different repo; out of scope for this pass). The
   HyperV `Tests/` project is the template.
6. ⏳ **Extract the shared framework** per Section 3 — deferred (cross-repo effort).
7. ✅ **Enabled Dependabot** for NuGet + GitHub Actions (`.github/dependabot.yml`).

### Low — cosmetic / nice-to-have
8. ⏳ Align `LenovoPowerTray.iss` to a shared `common.iss` once extracted.
9. ✅ Release body already emits the installer SHA-256 (release.yml step 8 → step 11).

---

## Appendix — files changed in this pass

- `installer/HyperVManagerTray.iss` — auto-update task + procedures.
- `installer/winget/0z00z0.HyperVManagerTray.{installer,locale.en-US,}.yaml` — new manifests.
- `.github/workflows/release.yml` — new automated release pipeline (with test gate).
- `scripts/sign.ps1` — moved from repo root; default-path fix.
- `HyperVManagerTray.csproj` — `SignOutput` path → `scripts\sign.ps1`.
- `installer/build-installer.ps1` — two `sign.ps1` references → `scripts\sign.ps1`.
- `README.md` — winget install/upgrade + release-flow note.
