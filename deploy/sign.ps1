<#
  Code-signs the Manager Tool binaries + installer (audit H-2).

  You need a signing certificate first — either:
    * a .pfx file (export of your internal/self-signed cert, or a purchased cert), OR
    * a cert already in your CurrentUser\My or LocalMachine\My store (pass its -Thumbprint).

  For a closed office fleet + anonymity, an INTERNAL self-signed cert is the right choice:
    # one-time, on the build machine — creates a cert whose subject is a neutral name:
    $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Manager Tool" `
         -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
         -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
    # export the PUBLIC cert (.cer) to push to the fleet's Trusted Publishers via GPO:
    Export-Certificate -Cert $c -FilePath .\ManagerTool-public.cer
    # then sign with:  ./sign.ps1 -Thumbprint $c.Thumbprint

  Timestamping (default DigiCert RFC-3161) is important: it keeps the signature valid AFTER
  the cert expires. Timestamping contacts a public server but reveals nothing identifying.

  Run AFTER ./publish.ps1 -Agent and AFTER compiling the installer (ISCC).
#>
param(
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $PfxPath -and -not $Thumbprint) {
    throw "Provide either -PfxPath (+ -PfxPassword) or -Thumbprint. See the comment block for how to mint an internal cert."
}

# --- locate signtool.exe from the Windows 10/11 SDK ---
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK (or Visual Studio 'Desktop C++' workload)." }

# --- files to sign (skip any that don't exist yet) ---
$targets = @(
    (Join-Path $root 'out/agent/ManagerTool.exe'),
    (Join-Path $root 'out/agent/ManagerToolSvc.exe'),
    (Join-Path $root 'Output/ManagerToolAgentSetup.exe')
) | Where-Object { Test-Path $_ }

if (-not $targets) { throw "Nothing to sign. Run ./publish.ps1 -Agent and compile the installer first." }

foreach ($f in $targets) {
    $args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($Thumbprint) {
        $args += @('/sha1', $Thumbprint)
    } else {
        $args += @('/f', $PfxPath)
        if ($PfxPassword) { $args += @('/p', $PfxPassword) }
    }
    $args += $f
    Write-Host "Signing $f ..." -ForegroundColor Cyan
    & $signtool @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $f (exit $LASTEXITCODE)." }
}

# --- verify ---
foreach ($f in $targets) { & $signtool verify /pa /v $f | Out-Null; Write-Host "verified: $f" -ForegroundColor Green }
Write-Host "All signed + timestamped." -ForegroundColor Green
