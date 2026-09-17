# ManagerTool — Live Deployment (production)

The server is **deployed and running**. This file records the live setup.

> **Server-side paths are still the legacy `deskwatch` ones — this is deliberate, not drift.**
> The 2026-08-18 source rename was applied to the *build* only. At the 2026-08-24 deploy the
> live box was migrated in place: same paths, same unit name, same database. Renaming them
> would have meant moving data and repointing nginx on a box that also serves other sites,
> for zero user-visible gain (the public URL is unchanged). See "Naming" below.

## Server (Hostinger VPS)

| Item | Value |
|------|-------|
| Public URL | **https://your-old-server.example.com** |
| VPS IP | <SERVER_IP> (Hostinger KVM 1, Ubuntu 24.04, Mumbai) |
| TLS | Let's Encrypt via certbot, auto-renewing (expires 2026-11-14) |
| Reverse proxy | nginx → `127.0.0.1:5281` (site: `/etc/nginx/sites-available/deskwatch`) |
| App service | systemd `deskwatch.service`, Description "Manager Tool Server" (auto-start, restart on crash) |
| App files | `/opt/deskwatch/app` (entry assembly `ManagerTool.Server.dll`) |
| Database | `/opt/deskwatch/data/deskwatch.db` |
| Recordings | `/opt/deskwatch/recordings` |
| Config | `/opt/deskwatch/app/appsettings.Production.json` (section `ManagerTool:*`) |

## Naming — read before touching config or auth

- The config **section** is `ManagerTool:*` (the code reads that). It was renamed from
  `DeskWatch:*` at the 2026-08-24 deploy; the pre-change file is kept as
  `appsettings.Production.json.pre-managertool`.
- **`Auth:Issuer` and `Auth:Audience` are deliberately still `"DeskWatch"`.** They are opaque
  strings. Changing them invalidates every issued agent and admin JWT, and the agent does **not**
  re-enroll after a rejected token — it retries the dead one until it expires (up to
  `AgentTokenDays`, 30). That would silently stop monitoring on every PC. Leave them alone unless
  you are prepared to re-run the installer on each endpoint.
- `ManagerTool:AgentInstallerPath` points at the installer already on the box
  (`agent-installer/DeskWatchAgentSetup.exe`), and `AgentVersion` stays `1.0.2` to match it, so
  remote update remains a deliberate no-op until a new installer is built and uploaded.

Coexists with the existing sites on this VPS (backtest-hub, oi-monitor, vega-tool) —
ManagerTool was added as a separate nginx site and did not modify them.

## Credentials

| Item | Value |
|------|-------|
| Admin username | `admin` |
| Admin password | `<REDACTED — server-side only, rotate>` |
| Agent enrollment key | `<REDACTED — server-side only, rotate>` |

SSH to the box (key stored on the build machine at `~/.ssh/<ssh-key>`):
```
ssh -i ~/.ssh/<ssh-key> root@<SERVER_IP>
```

## Verified working (over the internet, real cert)

- `GET https://your-old-server.example.com/` → 200
- `http://…` → 301 redirect to https
- `POST /api/auth/login` (admin) → 200 + token
- `POST /api/auth/enroll` (enrollment key) → 200 + token
- `/hub` (SignalR) reachable, 401 without token (WebSocket upgrade headers in place)

## Common server commands

```bash
systemctl status managertool          # app status
journalctl -u managertool -f          # live app logs
systemctl restart managertool         # restart after a config change
nginx -t && systemctl reload nginx  # after nginx edits
certbot certificates                # cert status
```

## Redeploying the server build

1. `./deploy/publish.ps1 -ServerLinux`
2. **Stage the payload and strip two things that must never ship:**
   - `appsettings.json` and `appsettings.Production.json` — the published copies are Windows
     placeholders (`C:\ManagerTool\...`, `REPLACE_WITH_...`, Kestrel on `0.0.0.0:443` with a PFX
     path). Copying them over live replaces the real signing key and stops the service.
   - `wwwroot/*.bak*` — `wwwroot` is a public web root, so stale dashboard copies would be served
     unauthenticated. (Two such files were live until 2026-08-24 and have been removed.)
3. `scp` the staged files to `/opt/deskwatch/app` (leave `agent-installer/` in place).
4. `systemctl restart deskwatch`

Backups from the 2026-08-24 cutover are in `/root/backups/` (DB, full app tar, unit file, nginx
site). Rollback = restore the tar, revert `ExecStart` to `DeskWatch.Server.dll`, restart.

## Agent installer

`deploy/Output/ManagerToolAgentSetup.exe` is compiled with `ServerUrl = https://your-old-server.example.com`
and the enrollment key above. Run it (as admin) on each office PC — it installs, writes the
config, and auto-starts the agent at every logon.

**Current live build: v1.0.2 (security-hardened + headless)**
- SHA-256: `7B1EDCCB9710FE555F644AEACB76B09B5C79C8E291C10D4C391D6C6EF2E3FCDA`
- Served from `/opt/managertool/app/agent-installer/` and mirrored to `/var/www/pmbv-downloads/`.
- **Headless:** no tray icon, no on-screen notice, no local consent/install log, and **no
  Add/Remove Programs entry** (`Uninstallable=no`). Disclosure/consent is handled out-of-band via
  the signed employment agreement + hard-copy ledger. Removal = admin console remote-uninstall
  (SYSTEM `ManagerToolRemove` task) or `uninstall-agent.ps1`.
- Prior builds backed up on server: 1.0.1 `392D62C3…` (`*.bak-1.0.1-392d62c3`) and the original
  vulnerable 1.0.0 `F6033381…` (`*.bak-f6033381`).
- Security hardening applied to the privileged update path.

## Reminders

- VPS auto-renewal is **On**; keep a valid payment method so it doesn't lapse.
- Recordings are sensitive — `/opt/managertool/recordings`, 30-day retention.
- Token revocation isn't implemented; rotate the enrollment/signing keys if exposed.
- The agent/installer are **not code-signed** yet (audit H-1/H-2). This needs an EV or
  internal-CA cert before wide rollout;
