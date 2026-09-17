# Manager Tool — Watchdog + Signing Setup

Makes the agent **un-killable by a standard user, auto-starting on boot, self-restarting** —
while staying **visible** (services.msc + Task Manager) and **removable by an admin**.

> ⚠ **Status: written but NOT yet built or tested.** The watchdog uses Win32 session/token
> interop that could not be compiled in the session where it was written. **Build and smoke-test
> it once before fleet rollout** (steps below). Do not trust it in production until you've seen
> it relaunch the agent on a real machine.

## Architecture (why two pieces)

| Piece | Runs as | Where | Job |
|---|---|---|---|
| `ManagerToolSvc.exe` (service) | SYSTEM, auto-start | Session 0 | Guardian: relaunch the agent if it's killed. Un-killable by non-admins. |
| `ManagerTool.exe` (agent) | logged-in user | user session | Actual screen/activity capture. |

A service can't capture the user's screen (Session 0 has no desktop), so the service only
*launches* the agent into the active session and keeps it alive. Killing the agent → the service
relaunches it within ~10s. Stopping the **service** needs admin rights, so a trader can't disable it.
A single-instance mutex in the agent prevents the logon Run key + the service from double-launching.

## Build order (in a fresh session where builds work)

```powershell
# 1. add the new project to the solution (so verification builds include it)
dotnet sln ManagerTool.sln add src/ManagerTool.Watchdog/ManagerTool.Watchdog.csproj

# 2. build everything
dotnet build ManagerTool.sln -c Release

# 3. publish agent + watchdog (both land in deploy/out/agent)
./deploy/publish.ps1 -Agent

# 4. (optional, recommended) sign before packaging — see sign.ps1
./deploy/sign.ps1 -Thumbprint <your-cert-thumbprint>

# 5. compile the installer
& "$HOME/AppData/Local/Programs/Inno Setup 6/ISCC.exe" deploy/ManagerToolAgent.iss

# 6. (optional) sign the installer too
./deploy/sign.ps1 -Thumbprint <your-cert-thumbprint>
```

## Smoke test (must pass before rollout)

On a test VM/PC, run `ManagerToolAgentSetup.exe` as admin, then:

1. `Get-Service ManagerToolSvc` → **Running**, StartType **Automatic**.
2. Task Manager → `ManagerTool.exe` present in your session.
3. **Kill the agent** (`Stop-Process -Name ManagerTool -Force`). Within ~10s, confirm it's back.
4. As a **standard (non-admin) user**, try `Stop-Service ManagerToolSvc` → must be **denied**.
5. Reboot → both come back automatically.
6. Admin removal works: run `uninstall-agent.ps1` (or the console remote-uninstall) → service gone,
   agent gone, no relaunch.

If step 3 fails, the token/session interop in `Program.cs` needs debugging (check the service's
Application event-log entries; common causes: `WTSQueryUserToken` privilege, wrong `lpDesktop`).

## Known caveats to check

- **Interop is unverified** (above).
- **Single interactive user assumed.** The guardian targets the *active console session*. On a
  multi-user / RDP box it only keeps the console session's agent alive. Fine for single-seat office PCs.
- **In-place update.** `update.ps1` stops the service before running the new installer so the binary
  isn't locked; the new installer re-creates + restarts it. Verify an end-to-end remote update once.

## Fleet hardening — so it survives AV and runs for years

These are applied on **your** infrastructure (GPO / Intune / Defender), on company-owned PCs:

1. **Sign it** with an internal code-signing cert (see `sign.ps1` header for the one-liner to mint one).
2. **Distribute the public cert** to the fleet's **Trusted Publishers** store via GPO
   (Computer Config → Policies → Windows Settings → Security Settings → Public Key Policies).
   Reveals only the neutral subject name you chose (e.g. `CN=Manager Tool`).
3. **Add a Defender exclusion** for `C:\Program Files\Manager Tool\` (path) and/or the publisher,
   via GPO/Intune, so a future AV definition update can't quarantine the agent.
4. **Keep the server + its TLS cert alive** (certbot auto-renews the 90-day Let's Encrypt cert;
   alert if <20 days to expiry).

## Boundary (kept intentionally)

Un-killable by the **trader** — yes. Un-removable by **you** — no: an admin can always
`Stop-Service`/`sc delete`, run `uninstall-agent.ps1`, or use the console's remote-uninstall.
The service and process are **visible**; this is tamper-resistance, not concealment.
