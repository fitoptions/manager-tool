# ManagerTool — Complete User Guide

A transparent, company-owned workstation-monitoring system for a trading desk. This guide
takes you from zero to a running deployment and day-to-day use.

**What it does:** streams each PC's **activity** (focused app / window / browser URL,
timestamped) continuously, and lets an admin **view or record any PC's screen on demand**
over the internet. **It does not log keystrokes.**

**Three parts:**
| Part | What it is | Runs where |
|------|-----------|-----------|
| **Server** | Central relay + database | One public host (VPS / on-prem box) |
| **Agent** | Monitoring app | Every office PC |
| **Admin Console** | Your control panel | Your own machine |

---

## Table of contents

1. [Before you start (prerequisites)](#1-before-you-start)
2. [Quick local test (all on one PC)](#2-quick-local-test)
3. [Production install — Server](#3-production-install--server)
4. [Production install — Agents (office PCs)](#4-production-install--agents)
5. [Using the Admin Console](#5-using-the-admin-console)
6. [Managing credentials](#6-managing-credentials)
7. [Retention & storage](#7-retention--storage)
8. [Updating & uninstalling](#8-updating--uninstalling)
9. [Troubleshooting](#9-troubleshooting)
10. [FAQ](#10-faq)

---

## 1. Before you start

### On your build machine (where you compile)
- **Windows 10/11 64-bit**
- **.NET 8 SDK** — download from https://dotnet.microsoft.com/download/dotnet/8.0
  (verify with `dotnet --version` → should print `8.x`)
- *(Optional)* **Inno Setup** if you want a double-click installer EXE:
  https://jrsoftware.org/isinfo.php

### For production you also need
- A **server host** reachable from the internet with a **public DNS name**
  (e.g. `monitor.yourfirm.com`) and the ability to open **inbound port 443**.
- A **TLS certificate** for that domain (from your CA, or free via Let's Encrypt).

### One-time housekeeping before deploying
- Confirm every trader has acknowledged your written monitoring policy (the agent also
  shows an on-screen disclosure on first run).
- Have your credentials handy — see [section 6](#6-managing-credentials). They were
  generated during setup and are recorded in `deploy/DEPLOYMENT.md`.

---

## 2. Quick local test

Do this first, on a single PC, to see all three pieces working before touching production.
It uses the built-in dev TLS certificate.

**Step 1 — trust the dev certificate** (once):
```bash
dotnet dev-certs https --trust
```
Click **Yes** on the prompt.

**Step 2 — create a throwaway admin password + config.** The shipped `appsettings.json`
has placeholder secrets. Generate a password hash:
```bash
dotnet run --project src/ManagerTool.Server -- hash-password "test123"
```
Copy the output line and paste it into `src/ManagerTool.Server/appsettings.json` at
`Auth → Admins[0] → PasswordHash`. In the same file set `Auth:SigningKey` to any 32+
character string and `Auth:AgentEnrollmentKey` to any value (e.g. `test-key`).

**Step 3 — start the three parts** in three separate terminals:
```bash
dotnet run --project src/ManagerTool.Server
```
```bash
dotnet run --project src/ManagerTool.Agent
```
```bash
dotnet run --project src/ManagerTool.Admin
```

**Step 4 — sign in.** In the Admin Console login window:
- Server URL: `https://localhost:5281`
- Username: `admin`
- Password: `test123`

You should see your PC appear in the workstation list, its activity timeline filling in as
you switch windows, and **View** should show your screen. That confirms the whole chain
works. Now do the real deployment below.

---

## 3. Production install — Server

Run everything here **as Administrator** on the public server host.

### 3.1 Build the server
On your build machine:
```bash
./deploy/publish.ps1 -Server
```
This creates `deploy/out/server` — a self-contained build (the server host needs no .NET
installed). Copy that folder to the server, e.g. to `C:\ManagerTool\server`.

### 3.2 Configure
`appsettings.Production.json` already contains your generated secrets (signing key,
enrollment key, admin password hash). You only need to set up TLS. Pick one:

- **Option A — Kestrel handles TLS directly:**
  Put your certificate at `C:\ManagerTool\certs\managertool.pfx` and set its password in
  `appsettings.Production.json` → `Kestrel → Certificates → Default → Password`.

- **Option B — reverse proxy (recommended):**
  Run **Caddy** or **nginx** in front to handle TLS (Caddy auto-obtains a Let's Encrypt
  cert). Point it at the server on loopback, and change the Kestrel endpoint in
  `appsettings.Production.json` to `http://127.0.0.1:5281`. Example Caddyfile:
  ```
  monitor.yourfirm.com {
      reverse_proxy 127.0.0.1:5281
  }
  ```

### 3.3 Install as an auto-start service
```bash
./deploy/install-server.ps1
```
This registers the **ManagerToolServer** Windows service (starts on boot, auto-restarts on
crash) and starts it. It also creates `C:\ManagerTool\data`, `\recordings`, and `\certs`.

### 3.4 Open the firewall & verify
- Allow **inbound TCP 443** on the server — and **only** that.
- In a browser go to `https://monitor.yourfirm.com/` → you should see
  *"ManagerTool server running"*. The server is ready.

---

## 4. Production install — Agents

The agent installs to every office PC and **starts automatically at every logon** — no
manual step after install. It shows a tray icon + a first-run monitoring notice (this is
deliberate; monitoring is disclosed, not hidden).

> **Why logon, not boot?** Capturing a user's screen and showing the notice both require
> that user's interactive desktop, which doesn't exist before someone logs in. Starting at
> logon is the correct fully-automatic behaviour — from the user's perspective it's "always
> on".

### 4.1 Build the agent package (once)
On your build machine:
```bash
./deploy/publish.ps1 -Agent
```
→ produces `deploy/out/agent` (self-contained; office PCs need no .NET).

### 4.2 Install on each PC — choose one method

**Method A — PowerShell (good for RMM, GPO startup scripts, or manual):**
Run as Administrator on the PC (with `deploy/out/agent` reachable):
```bash
./deploy/install-agent.ps1 -ServerUrl "https://monitor.yourfirm.com" -EnrollmentKey "<ENROLLMENT_KEY>"
```
It copies files to `C:\Program Files\ManagerTool\Agent`, writes the config, registers
auto-start, and launches the agent for the current session.

**Method B — double-click installer EXE (good for handing to IT):**
1. Edit the three `#define` lines at the top of `deploy/ManagerToolAgent.iss`
   (`ServerUrl`, `EnrollmentKey`).
2. Open the `.iss` in Inno Setup and click **Compile** → produces
   `ManagerToolAgentSetup.exe`.
3. Run that EXE on each PC (or push it via your deployment tool). It does everything
   Method A does.

### 4.3 Confirm it worked
Within a few seconds of the agent starting, the PC appears in your Admin Console
workstation list. On the PC itself you'll see the ManagerTool tray icon ("this workstation
is monitored").

---

## 5. Using the Admin Console

The console is a desktop app you run from **your own machine**. It connects to the same
server URL over the internet.

Run it (or publish it once and run the exe):
```bash
dotnet run --project src/ManagerTool.Admin
```

### 5.1 Sign in
- **Server URL:** `https://monitor.yourfirm.com`
- **Username:** `admin`
- **Password:** *(your admin password — see section 6)*

### 5.2 The layout
```
┌────────────┬─────────────────────────────┬──────────────────────────┐
│ Workstations│  Activity timeline          │  Live Screen             │
│            │  (focused app / window / URL)│  [View][Stop][● Rec]     │
│  • PC-01   │  09:41:02  chrome   nseindia │  Monitor [#1 ▼]          │
│  • PC-02   │  09:41:20  Terminal  Orders  │                          │
│  • PC-03   │  09:42:05  chrome   gmail    │   (screen appears here)  │
└────────────┴─────────────────────────────┴──────────────────────────┘
```

### 5.3 Watch activity
Click a workstation in the left list. The middle panel fills with its **timestamped
activity** — every time the focused app, window title, or browser URL changes, a new row
appears (newest on top). This is your compliance signal: *what app / site was in focus, and
when.*

### 5.4 View a screen (live)
1. Select a workstation.
2. Click **View**. The agent starts streaming and its screen appears on the right.
3. **Multi-monitor:** if the PC has more than one display, the **Monitor** dropdown lists
   them (`#1`, `#2`, …). Pick which one to watch.
4. Click **Stop** when done. Streaming stops on that PC (the agent only captures its screen
   while you're actually viewing).

### 5.5 Record a session (to disk)
1. While viewing a workstation, click **● Rec**. The button changes to **■ Stop Rec**.
2. Frames are now saved on the **server** under
   `C:\ManagerTool\recordings\{hostId}\{timestamp}\` as a numbered image sequence plus a
   `manifest.json` (who started it, start/end time, frame count).
3. Click **■ Stop Rec** (or **Stop**) to finish and finalize the recording.

> Recording keeps the agent streaming even if you look away, so a long session is captured
> fully. Nothing is recorded unless you explicitly start it.

To review a recording, open its folder on the server and page through the JPEG frames.
(There's no in-console playback player yet.)

---

## 6. Managing credentials

Your deployment credentials (generated during setup, recorded in `deploy/DEPLOYMENT.md`):

| Item | Default |
|------|---------|
| Admin username | `admin` |
| Admin password | `<REDACTED — server-side only, rotate>` |
| Agent enrollment key | `<ENROLLMENT_KEY>` |

**Change the admin password:**
```bash
ManagerTool.Server.exe hash-password "your-new-password"
```
Paste the output into `appsettings.Production.json` → `Auth:Admins[0]:PasswordHash`, then
restart the service:
```bash
Restart-Service ManagerToolServer
```

**Add another admin:** add another `{ "Username": ..., "PasswordHash": ... }` object to the
`Auth:Admins` array and restart.

**Rotate the enrollment key / signing key** (e.g. if you suspect exposure): change
`Auth:AgentEnrollmentKey` and/or `Auth:SigningKey`, restart the server, and re-run the
agent installer with the new key. This invalidates old tokens on their next refresh.

> The admin password is stored **only as a PBKDF2 hash** on the server. Keep the plaintext
> in your password manager. Treat `appsettings.Production.json` and `DEPLOYMENT.md` as
> secret.

---

## 7. Retention & storage

Configured in `appsettings.Production.json` → `ManagerTool`:

| Data | Setting | Default | Note |
|------|---------|---------|------|
| Activity events | `Retention:Days` | 90 | `0` = keep forever |
| Recorded sessions | `Recording:RetentionDays` | 30 | `0` = keep forever |
| Sweep frequency | `Retention:SweepHours` | 6 | how often old data is purged |

A background sweep deletes anything past its window. An in-progress recording is never
purged. Recorded screens are the most sensitive data in the system — keep
`Recording:RetentionDays` as short as your compliance process allows, and restrict access
to `C:\ManagerTool\recordings`.

**Offline resilience:** if an agent temporarily loses its internet connection, activity is
buffered on that PC (`%ProgramData%\ManagerTool\activity-buffer.jsonl`) and replayed in order
when it reconnects — nothing is lost. Screen frames are live-only and not buffered.

---

## 8. Updating & uninstalling

**Update an agent:** re-run `install-agent.ps1` (or the installer EXE) with the new build;
it overwrites in place and keeps the config.

**Update the server:** copy the new `deploy/out/server` over the install folder and
`Restart-Service ManagerToolServer`. (Stop the service first if files are locked.)

**Uninstall an agent** (elevated, on the PC):
```bash
./deploy/uninstall-agent.ps1
```
Stops the agent, removes auto-start, deletes files and local buffer.

**Remove the server service:**
```bash
Stop-Service ManagerToolServer; sc.exe delete ManagerToolServer
```

---

## 9. Troubleshooting

| Symptom | Likely cause & fix |
|---------|-------------------|
| PC doesn't appear in console | Agent can't reach server. On the PC, open `https://monitor.yourfirm.com/` in a browser — if it fails, DNS/firewall/cert issue. Check `ServerUrl` in `C:\Program Files\ManagerTool\Agent\agent.config.json`. |
| Agent connects but console shows nothing | You're signed in but haven't selected a workstation. Click one. |
| "Invalid username or password" | Password mismatch. Re-hash and update `appsettings.Production.json`, restart service. |
| Agent won't enroll (no token) | Enrollment key mismatch between agent config and `Auth:AgentEnrollmentKey`. |
| Cert error on connect | In production the cert must be valid for the domain. Don't enable `AllowInvalidServerCert` in production. For local testing only, that flag trusts the dev cert. |
| Screen view is black / empty | The PC's session is locked or on the UAC secure desktop — capture resumes when the user is back at their desktop. |
| Browser URL column empty | Some browsers/PWA windows don't expose the address bar to UI Automation; window title still logged. |
| Server service won't start | Check `ASPNETCORE_ENVIRONMENT=Production` is set (the installer sets it), the PFX path/password are correct, and port 443 isn't already in use. Look at Windows Event Viewer → Application. |
| High bandwidth while viewing | Expected — screen streaming is a few hundred KB/s per monitor. It only flows while you view/record; idle PCs send only tiny activity events. |

---

## 10. FAQ

**Does it log keystrokes / passwords?**
No. By design there is no keylogger. It records the focused app, window title, and browser
URL — plus screen view/recording on demand. This avoids ever capturing typed credentials.

**Do the office PCs need to be on the same network?**
No. Each PC connects **outbound** over the internet to the central server, so PCs on
separate leased lines work fine. Only the server needs a public address (inbound 443).

**Can employees see they're monitored?**
Yes — that's intentional. The agent shows a first-run disclosure and a persistent tray
icon. This is disclosed monitoring on company hardware, not covert surveillance.

**Does it need admin rights on the PC?**
The **installer** needs admin (to write Program Files + the auto-start key). The **agent
itself runs as the normal logged-in user** — no elevation, no driver, no kernel hooks.

**What happens if the server is down?**
Agents keep buffering activity locally and reconnect automatically; buffered events replay
in order. Live screen viewing is unavailable until the server is back.

**Where is the data stored?**
Activity in a SQLite database (`C:\ManagerTool\data\managertool.db`); recordings as image
folders under `C:\ManagerTool\recordings`. Both on the server, both governed by retention.

**Can I have more than one admin?**
Yes — add entries to `Auth:Admins`. Each signs in with their own username/password.

---

*For network topology and the security notes your ISA will review, see
[`deploy/DEPLOYMENT.md`](deploy/DEPLOYMENT.md). For architecture and design decisions, see
[`README.md`](README.md).*
