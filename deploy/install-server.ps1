<#
  Installs the ManagerTool server as a Windows Service so it auto-starts on boot and
  restarts on failure. RUN AS ADMINISTRATOR on the server host (VPS / on-prem box that
  is reachable from the internet).

  Prerequisites:
    1. Run  ./publish.ps1 -Server   to produce ./out/server
    2. Place your real TLS certificate at the path referenced by appsettings.Production.json
       (Kestrel:Certificates:Default:Path), or terminate TLS at a reverse proxy.
    3. Open inbound 443 on the server firewall ONLY. Office PCs need no inbound rules.

  Usage (elevated PowerShell):
    ./install-server.ps1
#>
param(
    [string]$SourceDir  = (Join-Path $PSScriptRoot 'out/server'),
    [string]$InstallDir = "C:\ManagerTool\server",
    [string]$ServiceName = "ManagerToolServer"
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run as Administrator."
}

Write-Host "Copying server to $InstallDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path "C:\ManagerTool\data","C:\ManagerTool\recordings","C:\ManagerTool\certs" | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir 'ManagerTool.Server.exe'

# Force Production config so appsettings.Production.json is loaded.
[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service exists; stopping and removing before reinstall..." -ForegroundColor Yellow
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $ServiceName ..." -ForegroundColor Cyan
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "ManagerTool Server" | Out-Null
sc.exe description $ServiceName "ManagerTool monitoring server (activity + screen relay)." | Out-Null
# Restart automatically on crash.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service $ServiceName

Write-Host "Server installed and running as a service (auto-start on boot)." -ForegroundColor Green
