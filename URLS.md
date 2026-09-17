# ManagerTool — URL Reference

Complete list of every ManagerTool URL and what it's for.

---

## 🌐 Public URLs (what people actually use)

| URL | Purpose | Auth |
|-----|---------|------|
| **https://your-old-server.example.com** | **Web admin dashboard** — sign in to monitor desks (live screen, activity, reports, audit, recordings, alerts, account) from any browser or device | Admin login |
| **https://example.com** | **Marketing landing page** — the sales dossier for prospective buyers | Public |
| **https://www.example.com** | Same marketing page (redirects to `example.com`) | Public |
| **https://example.com/agent/ManagerToolAgentSetup.exe** | **Protected agent installer download** — hand to IT to install on trader PCs (embeds the enrollment key, so never public) | Basic auth: user `managertool` |

**Day-to-day, you only need two:** `your-old-server.example.com` (to monitor) and `example.com` (public/sales face).

---

## 🔌 API / system endpoints

The apps use these behind the scenes — you don't visit them directly. All under `https://your-old-server.example.com`.

| Endpoint | Purpose |
|----------|---------|
| `/hub` | SignalR real-time hub — live screen frames, activity stream, alerts (agents + consoles) |
| `/api/auth/login` | Admin sign-in → JWT |
| `/api/auth/change-password` | Self-service password change (any signed-in account, incl. viewers) |
| `/api/admins` | `GET` list accounts · `POST` owner issues a viewer sign-in (no public self-registration) |
| `/api/access` | `GET` the viewer × workstation matrix · `PUT /{user}` set one viewer's blocked PCs (owner only) |
| `/api/auth/reset-request` | Anonymous "I'm locked out". Username only, rate limited, sets nothing. Emails a one-time code when the username is email-shaped **and SMTP is configured**; otherwise queues for the owner. Returns `{codeSent}`, which reveals nothing about the account |
| `/api/auth/reset-password` | Anonymous. Completes the emailed-code reset (`{username, code, newPassword}`). One generic failure message; code is single-use, expires in 10 min, dies after 5 wrong attempts |
| `/api/admins/reset-requests` | `GET` the lockout queue · `DELETE /{user}` dismiss one (owner only) |

> **The emailed-code path needs SMTP.** Without `ManagerTool:Notifications:Email` (`Host`, `From`,
> and usually `User`/`Password`), `codeSent` is always false and every lockout falls back to the
> owner's manual queue. Production currently has no `Notifications` section, so the OTP path is
> dormant there until those settings are added.
| `/api/auth/enroll` | Agents obtain their token (with the enrollment key) |
| `/api/hosts` | Connected workstations |
| `/api/hosts/{id}/activity` | Activity history + search (date/text/pagination) |
| `/api/alerts` | Compliance alerts (list / acknowledge) |
| `/api/rules` | Alert rule management (watchlists) |
| `/api/categories` | App/website productivity categories |
| `/api/reports/summary` | Reporting aggregations (top apps/sites, productivity split) |
| `/api/reports/export` | Activity CSV export |
| `/api/audit` | Admin audit trail |
| `/api/recordings` | Recorded-session list |
| `/api/recordings/{host}/{session}/frames` | Frame list for a session |
| `/api/recordings/{host}/{session}/frame/{name}` | A single recorded frame (playback) |
| `/api/admins` | Owner-only: list admins |
| `/api/admins/{user}/approve` | Owner-only: approve a pending admin |
| `/api/admins/{user}` (DELETE) | Owner-only: remove an admin |
| `/api/agent/installer` | Remote-update: agents pull the latest installer |
| `/api/agent/version` | Latest agent version + availability |
| `/health` | Server liveness check |

---

## 🖥️ Infrastructure (admin / ops only)

| What | Where |
|------|-------|
| Server (SSH) | Hostinger VPS `<SERVER_IP>` (root, SSH key) — app at `/opt/managertool/app` |
| Marketing site files | `/var/www/pmbv` on the VPS |
| Hostinger control panel | https://hpanel.hostinger.com — VPS, DNS, domain management |

---

## 🔑 Where credentials live (not in this file, by design)

- **Admin password** → root-only file on server: `/root/managertool-admin-password.txt` (owner login: `<OWNER_EMAIL>`). Change via dashboard → ⚙ Account.
- **Agent download password** → `/root/managertool-download-credentials.txt` on the server.
- **Agent enrollment key** → baked into the installer + server config.

*Retrieve server-side files via SSH or the Hostinger Web console (`cat <path>`).*
