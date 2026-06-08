# Code signing

Hyper-V Manager Tray is Authenticode-signed — both the application executable and the
installer — with the publisher's certificate **`CN=ZeroZero software`**.

## What we do (and why it's as strong as a free cert allows)

| Property | Status | Why it matters |
|----------|--------|----------------|
| SHA-256 digest | ✅ | Modern hash; SHA-1 is deprecated. |
| RFC-3161 timestamp | ✅ (`timestamp.digicert.com`) | The signature stays **valid after the certificate expires** — installers don't "rot". |
| App exe **and** installer signed | ✅ | The installer never embeds an unsigned exe (CI signs the exe first, then recompiles + signs the installer). |
| Same cert locally and in CI | ✅ | The cert is delivered to GitHub Actions via the `CODE_SIGN_PFX` / `CODE_SIGN_PASSWORD` secrets, so tag-built releases are signed identically to local builds. |
| Publicly *trusted* chain | ❌ (self-signed) | A paid/managed cert is required for this — see below. |

## The honest limitation

The certificate is **self-signed**. Windows only treats the signature as "trusted" on machines
that have explicitly imported it. On any other machine, the signature is *present and valid* but
the publisher is *untrusted*, so you'll still see **SmartScreen / "Unknown Publisher"** on first
run. Signing with a self-signed cert does **not** remove those prompts for the general public —
nothing free fully does.

What it *does* give you: tamper-evidence (the SHA-256 in each release), a stable publisher
identity, non-expiring signatures (timestamping), and the option for users/admins to trust the
publisher once (below).

## Trust the publisher (optional, per machine)

The public certificate (public key only — safe to share) is shipped at
[`scripts/ZeroZeroSoftware.cer`](../scripts/ZeroZeroSoftware.cer) and attached to each release.
To make Windows trust binaries signed by this publisher on your machine:

```powershell
# Per-user (no admin): trust as a root + publisher for the current user
Import-Certificate -FilePath .\ZeroZeroSoftware.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\ZeroZeroSoftware.cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

Verify a downloaded file matches the published thumbprint before trusting it:

```powershell
(Get-AuthenticodeSignature .\HyperVManagerTray-Setup.exe).SignerCertificate.Thumbprint
# Expected: 434427CF682F692A0CF3D621A9C24013C375993B
```

> Only import a code-signing certificate you trust. Importing it as a root means your machine
> will trust **anything** signed by that publisher.

## Upgrade paths (to remove the prompts for everyone)

When public, prompt-free distribution becomes worth it:

1. **SignPath Foundation** — free Authenticode certificates for qualifying open-source projects.
   The best $0 option for a publicly-trusted signature.
2. **Azure Trusted Signing** — ~$10/month, Microsoft-managed cert, integrates cleanly with CI.
3. A traditional **OV/EV code-signing certificate** from a CA (DigiCert, Sectigo, …).

Any of these drops in by replacing the `CODE_SIGN_PFX` secret (or swapping the signtool call for
the provider's action); the rest of the pipeline is unchanged.

## For maintainers — rotating the CI signing secret

```powershell
# Export the local cert (with key) to a temp PFX, base64 it, push as repo secrets.
$cert = Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Subject -eq 'CN=ZeroZero software' -and
                 $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' } |
  Sort-Object NotAfter -Descending | Select-Object -First 1
$pw  = -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 48 | ForEach-Object {[char]$_})
$pfx = Join-Path $env:TEMP 'zzs-codesign.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)) | gh secret set CODE_SIGN_PFX --repo 0z00z0/HyperVManagerTray
gh secret set CODE_SIGN_PASSWORD --body $pw --repo 0z00z0/HyperVManagerTray
Remove-Item $pfx -Force
```

The one-time local setup that creates + trusts the cert is `scripts\sign.ps1 -Setup`.
