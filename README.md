# ManagerTool

Standalone, **transparent** workstation-monitoring system for a company-owned trading
desk. Two things are captured on firm hardware, with the employee notified:

1. **Activity stream** — the focused application, window title, and (for browsers) the
   URL, timestamped and tagged. Always on while signed in.
2. **Screen viewing** — an admin can view or record a host's display **on demand**.
   The agent only captures its screen while an admin is actively watching.

**Keystrokes are never captured.** There is no keylogger. Window/app/URL activity plus
on-demand screen view provides compliance enforcement without turning the log store into
a credential honeypot. This is a deliberate design decision, not a missing feature.

## Transparency by design

- The agent shows a **persistent tray icon** ("this workstation is monitored") and a
  **first-run disclosure** the employee acknowledges (`MonitoringNotice` in Shared).
- Runs `asInvoker` (no elevation), in the logged-in user's session — not a hidden service.
- Screen capture is reference-counted: no admin viewing ⇒ no capture.

## Projects

| Project              | What it is                                                        |
|----------------------|-------------------------------------------------------------------|
| `ManagerTool.Shared`   | DTOs + SignalR hub contracts + the disclosure text.               |
| `ManagerTool.Server`   | ASP.NET Core + SignalR relay, SQLite activity store, query API.   |
| `ManagerTool.Agent`    | WPF tray app: notice, active-window/URL watcher, on-demand screen.|
| `ManagerTool.Admin`    | WPF console: host list, activity timeline, live screen viewer.    |

## Security: authentication + TLS

All transport is **HTTPS**, and both the SignalR hub and the query API require a
**JWT**. Two roles, enforced on every hub method and API route:

- **admin** — obtained via `POST /api/auth/login` (username + password). Can list hosts,
  read activity, and start/stop screen viewing.
- **agent** — obtained via `POST /api/auth/enroll` (pre-shared enrollment key). Can only
  register and push activity/frames. An agent token cannot view other hosts.

Verified role matrix: no token → 401; agent calling an admin route → 403; admin → 200.

### One-time server setup

1. **Generate an admin password hash** (PBKDF2, never store plaintext):
   ```bash
   dotnet run --project src/ManagerTool.Server -- hash-password "your-admin-password"
   ```
   Paste the output into `Auth:Admins[0].PasswordHash` in `appsettings.json`.

2. **Set the secrets** in `appsettings.json` (or environment variables / a secret store):
   - `Auth:SigningKey` — a random string of **at least 32 bytes**.
   - `Auth:AgentEnrollmentKey` — the shared key you put in each agent's `agent.config.json`.

3. **TLS certificate:**
   - *Dev:* run `dotnet dev-certs https --trust` once; leave `Kestrel:Certificates:Default`
     blank. Agent and admin default to `AllowInvalidServerCert: true` for the dev cert.
   - *Production:* set `Kestrel:Certificates:Default:Path` to your PFX and `Password`,
     and set `AllowInvalidServerCert: false` on agent and admin so the cert is verified.

## Reliability: offline buffering + retention

**Offline buffering (agent).** Every activity event is written to a durable, bounded
JSONL buffer (`%ProgramData%\ManagerTool\activity-buffer.jsonl`) *before* being sent. If the
server is unreachable — or the agent crashes or the PC reboots — nothing is lost: on
reconnect the agent drains the buffer **oldest-first**, removing only what the server
acknowledged (single-flight, order-preserving). The buffer is capped
(`maxEvents`, default 50,000); once full it drops the **oldest** events so disk use stays
bounded. Screen frames are live-only and never buffered.

**Retention (server).** A background sweep (`RetentionService`) deletes activity older
than the configured window. Configure under `ManagerTool:Retention` in `appsettings.json`:

```json
"Retention": { "Days": 90, "SweepHours": 6 }
```

`Days: 0` keeps activity indefinitely. Set the window to match your monitoring policy —
keep it only as long as compliance review actually needs.

Both paths are covered by tests (retention cutoff correctness; buffer durability across
restart, oldest-first drain order, partial-drain persistence, and cap eviction).

## Multi-monitor capture + on-disk recording

**Multi-monitor.** The agent captures **every attached display** each tick and sends one
frame per monitor, tagged `MonitorIndex` / `MonitorCount`. In the admin console the
**Monitor** dropdown auto-populates from the host's monitor count; pick which screen to
watch. (Only the selected monitor is rendered; all monitors still stream while viewing.)

**On-disk recording.** While viewing a host, click **● Rec** to persist its live frames to
disk on the **server**. Each recording is a self-contained folder:

```
recordings/{hostId}/{startUtc}/
    frame-000001-mon0.jpg
    frame-000001-mon1.jpg
    ...
    manifest.json          # hostId, startedBy, start/end UTC, frameCount, complete
```

Recording holds a viewer reference, so the agent keeps streaming even if you look away;
click **■ Stop Rec** (or Stop) to finalize the manifest. Recording is **off by default** —
nothing is written to disk unless an admin explicitly starts it.

Configure under `ManagerTool:Recording`:

```json
"Recording": { "Directory": "recordings", "RetentionDays": 30 }
```

Recorded sessions are swept by the same retention service (`RetentionDays`; 0 = keep
indefinitely). An in-progress recording is never purged (purge keys off last-write time).

> Recorded screens are far more sensitive than the activity log. Keep `RetentionDays`
> tight and treat the `recordings/` folder as restricted storage.

## Run it (dev, all on one box)

```bash
# 1. Server (HTTPS on https://0.0.0.0:5281)
dotnet run --project src/ManagerTool.Server

# 2. Agent  (reads agent.config.json → ServerUrl + EnrollmentKey)
dotnet run --project src/ManagerTool.Agent

# 3. Admin console (a login window prompts for server URL + credentials)
dotnet run --project src/ManagerTool.Admin
```

Sign in to the admin console, select a workstation to see its live activity timeline,
then click **View** to start an on-demand screen stream and **Stop** to end it.

## Data flow

```
Agent ──activity (always)──▶ Server ──▶ SQLite  ──▶ /api query
  ▲                            │
  └──screen (only when ────────┘──frames──▶ Admin console (live)
     an admin is viewing)
```

## Deliberate scope limits (scaffold)

These are stubbed or intentionally omitted; wire them up per your policy before production:

- **Token revocation** — JWTs are stateless; a leaked agent token stays valid until it
  expires. Add a revocation list / rotation if your threat model needs it.
- **Recording playback UI** — recorded sessions are written as JPEG sequences + a
  manifest; there's no in-console player yet (view the folder, or build a playback view).
- **Deployment** — for reliability you'll likely run the agent from the user's Startup
  (or a per-user scheduled task) so it stays visible in-session, not as a SYSTEM service.
```
