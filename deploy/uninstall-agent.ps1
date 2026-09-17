<#
  Removes the Manager Tool agent from a PC. RUN AS ADMINISTRATOR.
  Stops the running agent, removes the auto-start entry, the SYSTEM tasks, and all files.
#>
$ErrorActionPreference = 'SilentlyContinue'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This uninstaller must be run as Administrator."
}

Write-Host "Stopping watchdog service..." -ForegroundColor Cyan
sc.exe stop   ManagerToolSvc *> $null
sc.exe delete ManagerToolSvc *> $null
Get-Process -Name "ManagerToolSvc" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Stopping agent..." -ForegroundColor Cyan
Get-Process -Name "ManagerTool" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Removing auto-start entry..." -ForegroundColor Cyan
Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "ManagerTool" -ErrorAction SilentlyContinue

Write-Host "Removing management tasks..." -ForegroundColor Cyan
schtasks /Delete /TN ManagerToolRemove /F 2>$null | Out-Null
schtasks /Delete /TN ManagerToolUpdate /F 2>$null | Out-Null

Write-Host "Removing files..." -ForegroundColor Cyan
Remove-Item -Path "C:\Program Files\Manager Tool" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "C:\ProgramData\ManagerToolMgmt" -Recurse -Force -ErrorAction SilentlyContinue

# All per-machine agent data (host id, activity buffer). This build keeps no local
# consent/install log — disclosure is handled out-of-band via the signed employment agreement.
Remove-Item -Path (Join-Path $env:ProgramData 'ManagerTool') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Uninstalled." -ForegroundColor Green
