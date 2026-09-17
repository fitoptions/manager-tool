<#
  Installs the Manager Tool agent on an office PC and configures it to start
  automatically at every user logon. RUN AS ADMINISTRATOR (once per machine,
  or push via GPO / RMM / Intune).

  What it does:
    1. Copies the published agent to  C:\Program Files\Manager Tool
    2. Writes agent.config.json with your public server URL + enrollment key
    3. Registers an HKLM "Run" entry so the agent launches at every logon,
       in the user's interactive session (needed so screen capture works).

  The agent is headless (no tray icon, no on-screen notice). Employee disclosure /
  consent is handled out-of-band (signed employment agreement + ledger).

  Auto-start note: the agent starts at LOGON (not pre-login), because capturing a
  user's screen requires their interactive desktop. This is the correct, fully-automatic
  behaviour for per-user monitoring — no manual step.

  Usage (elevated PowerShell):
    ./install-agent.ps1 -ServerUrl "https://monitor.yourfirm.com" -EnrollmentKey "<ENROLLMENT_KEY>"
#>
param(
    [Parameter(Mandatory = $true)] [string]$ServerUrl,
    [Parameter(Mandatory = $true)] [string]$EnrollmentKey,
    [string]$SourceDir = (Join-Path $PSScriptRoot 'out/agent'),
    [switch]$AllowInvalidServerCert   # DEV ONLY; leave off in production (real cert required)
)

$ErrorActionPreference = 'Stop'

# --- must be elevated (HKLM + Program Files) ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This installer must be run as Administrator."
}

$installDir = "C:\Program Files\Manager Tool"
$exeName    = "ManagerTool.exe"

Write-Host "Installing Manager Tool to $installDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $installDir -Recurse -Force

# --- write agent config ---
$config = [ordered]@{
    ServerUrl              = $ServerUrl
    EnrollmentKey          = $EnrollmentKey
    AllowInvalidServerCert = [bool]$AllowInvalidServerCert
}
$config | ConvertTo-Json | Set-Content -Path (Join-Path $installDir 'agent.config.json') -Encoding UTF8

# --- register auto-start at every user logon (machine-wide) ---
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$exePath = Join-Path $installDir $exeName
Set-ItemProperty -Path $runKey -Name "ManagerTool" -Value "`"$exePath`""

Write-Host "Auto-start registered (HKLM\...\Run\ManagerTool)." -ForegroundColor Green

# --- locked management directory for the SYSTEM tasks (security hardening) ---
# The remove/update scripts run as SYSTEM, so they must live somewhere standard users
# CANNOT write — otherwise a user could swap a script (or drop their own update payload)
# and gain SYSTEM. %ProgramData% lets authenticated users create files by default, so we
# use a dedicated directory whose inheritance is broken and Users get read+execute only.
$mgmt = "C:\ProgramData\ManagerToolMgmt"
New-Item -ItemType Directory -Force -Path $mgmt | Out-Null
icacls "$mgmt" /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

$cadw      = Join-Path $env:ProgramData 'ManagerTool'   # volatile agent state (host id, activity buffer)
$removeCmd = Join-Path $mgmt 'remove.cmd'
$updatePs1 = Join-Path $mgmt 'update.ps1'

# remove.cmd (SYSTEM): stop agent, drop autostart, delete install + all agent data.
# This build keeps no local consent/install log (disclosure is out-of-band), so removal
# leaves nothing of the agent's own behind.
@"
@echo off
sc stop ManagerToolSvc >nul 2>&1
sc delete ManagerToolSvc >nul 2>&1
taskkill /F /IM ManagerToolSvc.exe >nul 2>&1
taskkill /F /IM $exeName >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v ManagerTool /f >nul 2>&1
timeout /t 2 /nobreak >nul
rmdir /s /q "$installDir" >nul 2>&1
rmdir /s /q "$cadw" >nul 2>&1
schtasks /Delete /TN ManagerToolRemove /f >nul 2>&1
schtasks /Delete /TN ManagerToolUpdate /f >nul 2>&1
"@ | Set-Content -Path $removeCmd -Encoding ASCII

# update.ps1 (SYSTEM): enroll with the on-box key, download the installer INTO the locked
# dir, then run it. No user-writable file is ever executed with elevation.
@"
`$ErrorActionPreference = 'Stop'
`$cfg = Get-Content '$installDir\agent.config.json' | ConvertFrom-Json
`$b = @{ enrollmentKey = `$cfg.EnrollmentKey; hostId = 'updater'; machineName = `$env:COMPUTERNAME } | ConvertTo-Json
`$tok = (Invoke-RestMethod -Uri "`$(`$cfg.ServerUrl)/api/auth/enroll" -Method Post -ContentType 'application/json' -Body `$b).accessToken
`$dest = '$mgmt\setup.exe'
Invoke-WebRequest -Uri "`$(`$cfg.ServerUrl)/api/agent/installer" -Headers @{ Authorization = "Bearer `$tok" } -OutFile `$dest
Start-Process -FilePath `$dest -ArgumentList '/VERYSILENT','/NORESTART' -Wait
Remove-Item `$dest -Force -ErrorAction SilentlyContinue
"@ | Set-Content -Path $updatePs1 -Encoding UTF8

# Create the SYSTEM tasks pointing at the locked scripts; let users trigger (not edit) them.
schtasks /Create /TN ManagerToolRemove /TR "cmd /c `"$removeCmd`"" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F | Out-Null
icacls "$env:SystemRoot\System32\Tasks\ManagerToolRemove" /grant "*S-1-5-32-545:RX" | Out-Null
Write-Host "Remote-uninstall task registered (ManagerToolRemove, SYSTEM)." -ForegroundColor Green

schtasks /Create /TN ManagerToolUpdate /TR "powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$updatePs1`"" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F | Out-Null
icacls "$env:SystemRoot\System32\Tasks\ManagerToolUpdate" /grant "*S-1-5-32-545:RX" | Out-Null
Write-Host "Remote-update task registered (ManagerToolUpdate, SYSTEM)." -ForegroundColor Green

# --- watchdog service: keeps the agent alive; a standard user cannot stop it (needs admin) ---
# Visible in services.msc as "Manager Tool"; auto-start on boot; SCM restarts it on failure.
$svcExe = Join-Path $installDir 'ManagerToolSvc.exe'
sc.exe stop   ManagerToolSvc *> $null
sc.exe delete ManagerToolSvc *> $null
Start-Sleep -Seconds 1
New-Service -Name ManagerToolSvc -BinaryPathName "`"$svcExe`"" -DisplayName "Manager Tool" `
    -StartupType Automatic -Description "Keeps Manager Tool running." | Out-Null
# SCM auto-recovery: restart on every failure, never reset the failure counter.
sc.exe failure ManagerToolSvc reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service ManagerToolSvc
Write-Host "Watchdog service registered (ManagerToolSvc, SYSTEM, auto-start)." -ForegroundColor Green

Write-Host "Installed. The agent will start at the next logon; starting it now for the current session..." -ForegroundColor Green

# --- start immediately for the current interactive session (best-effort) ---
try { Start-Process -FilePath $exePath | Out-Null } catch { Write-Warning "Could not start now: $_" }

Write-Host "Done." -ForegroundColor Green
