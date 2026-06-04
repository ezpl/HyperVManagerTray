<#
.SYNOPSIS
    Builds the per-user Inno Setup installer for HyperV Network Switcher.

.DESCRIPTION
    1. Publishes the app fully self-contained (win-x64, Windows App SDK bundled, no trimming —
       trimming breaks WinUI 3).
    2. Compiles installer\HyperVNetworkSwitcher.iss with Inno Setup (ISCC.exe).

    Output: installer\Output\HyperVNetworkSwitcher-Setup.exe (per-user, no admin to install;
    the app elevates itself at runtime).

    Requires Inno Setup (ISCC). If missing, install it once:
        winget install JRSoftware.InnoSetup

.EXAMPLE
    .\build-installer.ps1 -Version 2.0.0
#>
[CmdletBinding()]
param(
    [string] $Version = "2.0.0"
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$root         = Split-Path $installerDir -Parent
$proj         = Join-Path $root "HyperVNetworkSwitcher.csproj"
$publishDir   = Join-Path $root "publish"
$iss          = Join-Path $installerDir "HyperVNetworkSwitcher.iss"

# ── 1. Publish the app (fully self-contained, no trim) ───────────────────────
Write-Host "==> Publishing app (self-contained win-x64, Windows App SDK bundled)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj `
    -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishReadyToRun=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path (Join-Path $publishDir "HyperVNetworkSwitcher.pri"))) {
    throw "HyperVNetworkSwitcher.pri missing from publish output — WinUI would crash at startup (0xC000027B)."
}

# ── 2. Locate Inno Setup compiler ────────────────────────────────────────────
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",     # winget per-user install
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it once with:  winget install JRSoftware.InnoSetup"
}

# ── 3. Compile the installer ─────────────────────────────────────────────────
Write-Host "==> Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$setup = Join-Path $installerDir "Output\HyperVNetworkSwitcher-Setup.exe"
Write-Host ""
Write-Host "Done -> $setup" -ForegroundColor Green
if (Test-Path $setup) {
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash
    Write-Host "SHA256: $sha"
}
