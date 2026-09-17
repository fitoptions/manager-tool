<#
  Publishes self-contained, single-file builds of the agent and server so target
  machines do NOT need the .NET runtime installed.

  Usage:
    ./deploy/publish.ps1                 # publishes every target to ./deploy/out
    ./deploy/publish.ps1 -Agent          # agent + watchdog only
    ./deploy/publish.ps1 -Server         # Windows server only
    ./deploy/publish.ps1 -ServerLinux    # Linux server only (this is what production runs)
    ./deploy/publish.ps1 -Admin          # desktop admin console only

  Each target is wiped before publishing so a renamed output can never leave a
  stale, differently-named binary behind (the installer ships out/agent/* verbatim).
#>
param(
    [switch]$Agent,
    [switch]$Server,
    [switch]$ServerLinux,
    [switch]$Admin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $PSScriptRoot 'out'
$all  = -not ($Agent -or $Server -or $ServerLinux -or $Admin)

function Reset-Dir([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

if ($Agent -or $all) {
    Reset-Dir "$out/agent"
    Write-Host "Publishing Agent (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish "$root/src/ManagerTool.Agent/ManagerTool.Agent.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$out/agent"

    Write-Host "Publishing Watchdog service (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish "$root/src/ManagerTool.Watchdog/ManagerTool.Watchdog.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$out/agent"   # same output dir so the installer ships ManagerTool.exe + ManagerToolSvc.exe together
}

if ($Server -or $all) {
    Reset-Dir "$out/server"
    Write-Host "Publishing Server (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish "$root/src/ManagerTool.Server/ManagerTool.Server.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -o "$out/server"
}

if ($ServerLinux -or $all) {
    Reset-Dir "$out/server-linux"
    # Framework-dependent: the production VPS has the .NET 8 runtime installed and this is
    # what gets rsynced to /opt/managertool/app. See LIVE-DEPLOYMENT.md.
    Write-Host "Publishing Server (linux-x64, framework-dependent)..." -ForegroundColor Cyan
    dotnet publish "$root/src/ManagerTool.Server/ManagerTool.Server.csproj" `
        -c Release -r linux-x64 --self-contained false `
        -o "$out/server-linux"
}

if ($Admin -or $all) {
    Reset-Dir "$out/admin"
    Write-Host "Publishing Admin console (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
    dotnet publish "$root/src/ManagerTool.Admin/ManagerTool.Admin.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "$out/admin"
}

Write-Host "Done. Output in $out" -ForegroundColor Green
