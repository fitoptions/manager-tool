<#
  Manager Tool - self-update (runs as SYSTEM via the ManagerToolUpdate scheduled task).

  Safe by construction: it never discards the working agent until a freshly installed one has
  proven it can reach the server. The sequence is

      back up current install  ->  stop agent + watchdog  ->  install new  ->
      relaunch the new agent IN THE USER SESSION  ->  wait for it to report a live connection  ->
      keep it, or, if that never happens, restore the backup and bring the old one back.

  Every stage is reported to the server (POST /api/agent/update-status) so the dashboard shows
  exactly where an update is - no more silent black box. It is ALSO logged locally to update.log
  and the final outcome written to last-update.json.

  Three historical failure modes this fixes:
    1. The running agent (ManagerTool.exe) was never stopped, so the installer could not overwrite
       the locked exe and the update silently no-opped.  -> we taskkill it before installing.
    2. No verification at all - a broken update left the PC unmonitored with no signal.
       -> heartbeat gate + automatic rollback + status file + server reporting.
    3. Relaunch relied on the watchdog's session-launch, which is fragile. -> we relaunch the new
       agent ourselves via a transient user-context scheduled task, and start the watchdog too.
#>
$ErrorActionPreference = 'Stop'

$Mgmt   = 'C:\ProgramData\ManagerToolMgmt'
$AppDir = 'C:\Program Files\Manager Tool'
$Health = 'C:\ProgramData\ManagerTool\health\alive.json'
$Log    = Join-Path $Mgmt 'update.log'
$Status = Join-Path $Mgmt 'last-update.json'
$Backup = Join-Path $Mgmt 'rollback'
$Setup  = Join-Path $Mgmt 'setup.exe'

# Set once we know them, so the reporter can attribute stages to the right host.
$script:ServerUrl = $null
$script:Token     = $null
$script:HostId    = $env:COMPUTERNAME

function Log($m) {
    try { "$([DateTime]::UtcNow.ToString('o'))  $m" | Out-File -FilePath $Log -Append -Encoding utf8 } catch {}
}
function WriteStatus($state, $version, $detail) {
    try {
        @{ status = $state; version = $version; detail = $detail; utc = [DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json -Compress | Out-File -FilePath $Status -Encoding utf8
    } catch {}
}
# Report a lifecycle stage to the server so the dashboard can show it live. Best-effort.
function Report($stage, $version, $detail) {
    if (-not $script:ServerUrl -or -not $script:Token) { return }
    try {
        $body = @{ hostId = $script:HostId; stage = $stage; version = $version; detail = $detail;
                   utc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json
        Invoke-RestMethod -UseBasicParsing -Uri "$($script:ServerUrl)/api/agent/update-status" `
            -Method Post -ContentType 'application/json' -Body $body `
            -Headers @{ Authorization = "Bearer $($script:Token)" } -TimeoutSec 15 | Out-Null
    } catch { }
}
function StopAgent {
    & "$env:SystemRoot\System32\sc.exe" stop ManagerToolSvc | Out-Null
    & "$env:SystemRoot\System32\taskkill.exe" /F /IM ManagerTool.exe 2>$null | Out-Null
    Start-Sleep -Seconds 2
}
# Relaunch the agent in the interactively logged-on user's session WITHOUT relying on the watchdog.
# A transient scheduled task with an Interactive-logon principal runs as that user, needs no password,
# and launches into their desktop session - the reliable SYSTEM -> active-session hop.
function LaunchAgentInSession {
    try {
        $user = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
        if ($user) {
            $action    = New-ScheduledTaskAction -Execute "$AppDir\ManagerTool.exe"
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName 'ManagerToolLaunch' -Action $action -Principal $principal -Force | Out-Null
            Start-ScheduledTask -TaskName 'ManagerToolLaunch'
            Start-Sleep -Seconds 2
            Unregister-ScheduledTask -TaskName 'ManagerToolLaunch' -Confirm:$false -ErrorAction SilentlyContinue
            Log "relaunched agent in session of $user"
        } else {
            Log "no interactive user logged on; leaving relaunch to watchdog/logon"
        }
    } catch { Log "session relaunch failed: $_" }
    # Belt and suspenders: the watchdog should also be up to relaunch on crash.
    & "$env:SystemRoot\System32\sc.exe" start ManagerToolSvc | Out-Null
}

try {
    $cfg = Get-Content (Join-Path $AppDir 'agent.config.json') -Raw | ConvertFrom-Json
    $script:ServerUrl = $cfg.ServerUrl

    # The real host id + the version running BEFORE the update come from the heartbeat the agent
    # writes. Reporting as the real host is what lets the dashboard attach these stages to the PC.
    $oldVersion = $null
    if (Test-Path $Health) {
        try {
            $h = Get-Content $Health -Raw | ConvertFrom-Json
            if ($h.hostId)  { $script:HostId = $h.hostId }
            if ($h.version) { $oldVersion   = $h.version }
        } catch { }
    }

    # --- authenticate to the server (on-box enrollment key -> short-lived token) ---
    $enrollBody = @{ enrollmentKey = $cfg.EnrollmentKey; hostId = $script:HostId; machineName = $env:COMPUTERNAME } | ConvertTo-Json
    $script:Token = (Invoke-RestMethod -UseBasicParsing -Uri "$($script:ServerUrl)/api/auth/enroll" -Method Post `
                        -ContentType 'application/json' -Body $enrollBody).accessToken
    $headers = @{ Authorization = "Bearer $($script:Token)" }

    # --- the version we expect to be running after this update, so we verify the RIGHT build ---
    $targetVersion = (Invoke-RestMethod -UseBasicParsing -Uri "$($script:ServerUrl)/api/agent/version" -Headers $headers).version
    Log "starting update -> target version $targetVersion (was $oldVersion)"

    # --- download the installer into the locked management dir ---
    Report 'downloading' $targetVersion 'fetching installer'
    Invoke-WebRequest -UseBasicParsing -Uri "$($script:ServerUrl)/api/agent/installer" -Headers $headers -OutFile $Setup
    $size = (Get-Item $Setup).Length
    if ($size -lt 1MB) { throw "downloaded installer is only $size bytes - refusing to run it" }
    Log "downloaded installer ($size bytes)"

    # --- back up the current install so a bad update can be undone locally, offline ---
    if (Test-Path $Backup) { Remove-Item $Backup -Recurse -Force }
    & "$env:SystemRoot\System32\robocopy.exe" $AppDir $Backup /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    Log "backed up current install"

    # --- stop the agent (releases the locked exe) and the watchdog, then install ---
    Report 'installing' $targetVersion 'stopping old agent and installing'
    StopAgent
    $installStart = [DateTime]::UtcNow
    Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/NORESTART' -Wait
    Log "installer finished; relaunching and waiting for the new agent to connect"

    # --- relaunch the new agent ourselves (do not depend on the watchdog), then verify ---
    Report 'verifying' $targetVersion 'relaunching and waiting for a connected heartbeat'
    LaunchAgentInSession

    # Healthy = the NEW version posts a connected heartbeat stamped AFTER the install began. The
    # version test (target, or simply different from the old one) rejects a failed overwrite that
    # left the old agent running, even if the server forgot to bump its advertised version.
    $healthy = $false
    for ($i = 0; $i -lt 30 -and -not $healthy; $i++) {
        Start-Sleep -Seconds 2
        if (Test-Path $Health) {
            try {
                $h = Get-Content $Health -Raw | ConvertFrom-Json
                $stamp = [DateTimeOffset]::Parse($h.utc).UtcDateTime
                $isNewBuild = ($h.version -eq $targetVersion) -or ($h.version -ne $oldVersion)
                if ($h.connected -and $stamp -gt $installStart -and $isNewBuild) { $healthy = $true }
            } catch { }
        }
    }

    if ($healthy) {
        Log "SUCCESS: $targetVersion is connected"
        Remove-Item $Backup -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $Setup  -Force -ErrorAction SilentlyContinue
        WriteStatus 'ok' $targetVersion 'new agent connected'
        Report 'ok' $targetVersion 'new agent connected'
        exit 0
    }

    # --- rollback: the new build never confirmed a connection ---
    Log "FAILED health check - rolling back to the previous version"
    StopAgent
    & "$env:SystemRoot\System32\robocopy.exe" $Backup $AppDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    LaunchAgentInSession   # bring the old agent back in the user's session
    Log "rolled back; old agent relaunched"
    WriteStatus 'rolledback' $targetVersion 'new agent did not connect within the health window'
    Report 'rolledback' $oldVersion 'new agent did not connect within the health window; restored previous'
    exit 1
}
catch {
    Log "ERROR: $_"
    # Last-ditch: restore the backup if we have one, and make sure SOMETHING relaunches an agent.
    try {
        if (Test-Path $Backup) {
            StopAgent
            & "$env:SystemRoot\System32\robocopy.exe" $Backup $AppDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        }
        LaunchAgentInSession
    } catch { }
    WriteStatus 'error' '' "$_"
    Report 'failed' '' "$_"
    exit 2
}
