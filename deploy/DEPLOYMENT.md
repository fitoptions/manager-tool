# ManagerTool — Deployment Guide

How to deploy across office PCs that each sit on their **own leased line** (not a shared
LAN). All monitoring data travels over the **internet** to one central server.

---

## 1. Network topology (internet, not LAN)

```
   Office PC #1 ──┐   (outbound HTTPS/WSS :443)
   Office PC #2 ──┤
   Office PC #3 ──┼────────── internet ──────────►  ManagerTool Server
        ...       │                                 (public domain + TLS cert)
   Office PC #N ──┘                                 e.g. https://monitor.yourfirm.com
                                                            ▲
                                             Admin Console ─┘ (you sign in from anywhere)
```

- **Agents only make OUTBOUND connections.** Nothing needs to be opened inbound on any
  leased line / office PC. This works regardless of each PC being on a different network.
- **The server needs ONE public inbound port: 443.** Put it on a small VPS or an on-prem
  box with a public DNS name. Open **only** 443 inbound.
- SignalR uses WebSockets over 443 (falls back to long-polling if a proxy blocks WS).

---

## 2. Credentials (generated for this deployment)

> Keep these secret. Store them in your password manager and delete this file from disk
> once saved. Anyone with the admin password can view/record screens; anyone with the
> enrollment key can register an agent.

| Item | Value |
|------|-------|
| Admin console username | `admin` |
| Admin console password | `<REDACTED — server-side only, rotate>` |
| Agent enrollment key | `<ENROLLMENT_KEY>` |
| JWT signing key | *(already baked into `appsettings.Production.json`)* |

- The password is stored on the server **only as a PBKDF2 hash** (never plaintext).
- To change the admin password later:
  ```
  ManagerTool.Server.exe hash-password "new-password"
  ```
  then paste the hash into `Auth:Admins[0].PasswordHash` and restart the service.
- To add more admins, add more objects to the `Auth:Admins` array.

---

## 3. Server setup (once)

On the public server host (run PowerShell as Administrator):

1. **Publish** (on your build machine): `./deploy/publish.ps1 -Server` → copy `deploy/out/server` to the server.
2. **Config**: `appsettings.Production.json` ships with your secrets already filled in.
   Set the TLS certificate:
   - **Option A — Kestrel terminates TLS:** put your PFX at
     `C:\ManagerTool\certs\managertool.pfx` and set its password in
     `Kestrel:Certificates:Default:Password`.
   - **Option B — reverse proxy (recommended):** run nginx/Caddy in front for TLS
     (auto Let's Encrypt), proxy to the server on loopback, and change the Kestrel
     endpoint to `http://127.0.0.1:5281`.
3. **Install as an auto-start service:**
   ```
   ./deploy/install-server.ps1
   ```
   This registers `ManagerToolServer` (start=auto, auto-restart on crash) so it comes up on
   every boot with no manual step.
4. **Firewall:** allow inbound TCP **443** only. Verify: browse to
   `https://monitor.yourfirm.com/` → "ManagerTool server running".

---

## 4. Agent rollout (every office PC)

**Auto-start:** the agent registers under `HKLM\...\Run`, so it launches automatically at
**every user logon**, in that user's interactive session. There is no manual step after
install. (It starts at logon rather than pre-login because capturing a user's screen and
showing the disclosure notice both require an interactive desktop — a pre-login service
cannot do either. This is the correct fully-automatic behaviour.)

**Build the package once** (on your build machine):
```
./deploy/publish.ps1 -Agent
```
→ produces `deploy/out/agent` (self-contained; target PCs need no .NET installed).

**Then install on each PC** — pick one:

- **Script / RMM / GPO startup script** (elevated):
  ```
  ./deploy/install-agent.ps1 -ServerUrl "https://monitor.yourfirm.com" -EnrollmentKey "<ENROLLMENT_KEY>"
  ```
- **Double-click installer EXE:** compile `deploy/ManagerToolAgent.iss` with
  [Inno Setup](https://jrsoftware.org/isinfo.php) (edit the `ServerUrl` / `EnrollmentKey`
  `#define`s first) → produces `ManagerToolAgentSetup.exe`. Hand that to IT or push it.

On first launch each agent shows the monitoring disclosure, enrolls with the key to get a
token, and connects. It appears in your admin console within seconds.

**To uninstall a PC:** `./deploy/uninstall-agent.ps1` (elevated).

---

## 5. Admin console

The console is a desktop app — run it from your own machine (it also connects over the
internet to the same server URL):
```
dotnet run --project src/ManagerTool.Admin      # or publish it too and run the exe
```
Sign in with the admin credentials above and the server URL. Select a workstation to see
its live activity timeline; **View** streams its screen (pick the monitor from the
dropdown); **● Rec** records the session to disk on the server.

---

## 6. Operational notes

- **Data over the internet is TLS-encrypted.** Do not set `AllowInvalidServerCert` on
  agents/console in production — you want the real cert verified. (It defaults to `false`
  in `install-agent.ps1` and the Inno installer.)
- **Retention:** activity 90 days, recordings 30 days (configurable in
  `appsettings.Production.json`). Recorded screens are sensitive — keep the window tight.
- **Token revocation** is not implemented — a leaked agent token is valid until it expires
  (30 days). Rotate the enrollment key + signing key if you suspect exposure (this forces
  re-enrollment on next token refresh).
- **Bandwidth:** activity events are tiny. Screen streaming is a few hundred KB/s per
  viewed monitor at 4 fps / quality 55 — it only flows while you're viewing or recording a
  given host, not continuously for all PCs.
