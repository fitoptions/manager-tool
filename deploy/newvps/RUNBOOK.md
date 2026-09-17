# Manager Tool — New VPS Deployment Runbook (anonymity-hardened)

Fresh, neutral re-host of the Manager Tool server onto a new Hostinger VPS + new domain,
with NordVPN admin-lock and optional Cloudflare. Corrects three errors in the original plan.

> **Golden rule:** do NOT touch the old box (`your-old-server.example.com`, IP `YOUR_OLD_SERVER_IP`) until
> the new one is fully verified. You must never be without a working console.

---

## 0. What the prep step already did on your PC (done)

| Artifact | Location | Status |
|---|---|---|
| Dedicated SSH keypair (ed25519, no passphrase, neutral comment `app-admin`) | `C:\Users\prave\.ssh\newvps` / `.pub` | ✅ created |
| Linux server build (framework-dependent linux-x64, 46 files, 3.7 MB) | `deploy/out/server-linux/` | ✅ built + verified |
| Fresh `SigningKey`, `AgentEnrollmentKey`, admin password + hash | `deploy/newvps/SECRETS.txt` | ✅ generated |
| Linux-correct `appsettings.Production.json` (loopback :5281, Linux paths, secrets baked) | `deploy/newvps/appsettings.Production.json` | ✅ generated |
| systemd unit | `deploy/newvps/managertool.service` | ✅ |
| nginx site (public + NordVPN-lock variants) + proxy snippet | `deploy/newvps/nginx-*.conf`, `snippets-mt-proxy.conf` | ✅ |
| Hardening + NordVPN ufw scripts | `deploy/newvps/01-harden.sh`, `02-ufw-nordvpn.sh` | ✅ |
| New agent installer script (new enrollment key baked, domain placeholder) | `deploy/newvps/ManagerToolAgent.newvps.iss` | ✅ |

**Your SSH public key (paste into Hostinger's VPS "SSH Keys" field):**
```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDR7eaisQUKqeY4SvvM/m9UrX+KiHgKkOwRW0X62A+gH app-admin
```

### Corrections applied to the original plan
1. **Publish target:** use `./deploy/publish.ps1 -ServerLinux` (Linux, framework-dependent) — the
   plan's `-Server` builds a **Windows** binary that won't run on Ubuntu.
2. **Config rewrite:** the build ships the *Windows* `appsettings.Production.json` (binds `0.0.0.0:443`,
   Windows PFX + `C:\ManagerTool\...` paths). It has been replaced with a Linux one that binds
   `http://127.0.0.1:5281` (nginx terminates TLS) and uses `/opt/app/...` paths. **Overwrite the
   shipped file with `deploy/newvps/appsettings.Production.json` after scp.**
3. **hash-password command** is `ManagerTool.Server` (project was renamed from DeskWatch). Already
   run for you — the hash is in `SECRETS.txt`.

---

## Your chosen path (locked in from Q&A, 2026-08-19)

- **VPS:** provisioned — `srv1913531.hstgr.cloud` @ `YOUR_SERVER_IP` (KVM 2, Ubuntu). Domain `example.com`, app subdomain `your-server.example.com`. All values are already baked into this kit.
- **TLS/DNS:** **Cloudflare + auto-renew.** Use **Part F** for DNS + certs — it *replaces* Phase 3's
  Hostinger DNS and Phase 6's manual certbot. Order: Part A/Phase 2 (VPS) → Part F 1–2 (Cloudflare DNS,
  proxied) → Part B Phases 4–5 (harden + deploy) → Part F 3 (auto-renew wildcard) → Phase 7 (verify).
- **Admin lock:** **skipped for now** — use `nginx-managertool-public.conf` (Variant A). Part C,
  `02-ufw-nordvpn.sh`, and the `-vpnlock` nginx file stay staged for when you add a NordVPN Dedicated IP.
- **Origin hardening (recommended with Cloudflare):** since only Cloudflare should reach your origin,
  restrict inbound 443 to Cloudflare's IP ranges (ufw/nginx `allow`) so nobody can hit `YOUR_SERVER_IP`
  directly and bypass Cloudflare. Ask me and I'll generate that allow-list.

## Values (already filled into the whole `deploy/newvps/` kit)

| Item | Value |
|---|---|
| App subdomain (`server_name`, agent `ServerUrl`, cert SAN) | `your-server.example.com` |
| Registered domain (cert lineage / Cloudflare zone) | `example.com` |
| VPS public IP | `YOUR_SERVER_IP` (`srv1913531.hstgr.cloud`, KVM 2, Ubuntu) |
| NordVPN dedicated IP | `NORD_IP` — deferred; only needed if you later enable the admin-lock (Part C) |

Nothing left to find/replace except `NORD_IP`, and only if you turn on the NordVPN lock later.

---

## Part A — Hostinger: domain, VPS, DNS  (you, in the browser — I can't purchase/change your account)

**Phase 1 — Domain privacy.** Domain → turn ON domain privacy / WHOIS protection.

**Phase 2 — VPS.** Order a KVM VPS, **Ubuntu 24.04 LTS**. In the SSH-key field paste the public key
above. Note the public IP → that's `YOUR_SERVER_IP`.

**Phase 3 — DNS (anonymity-critical).**
- `A` record: `your-server.example.com` → `YOUR_SERVER_IP`. (Bland name; do not reuse `monitor`.)
- Turn on **DNSSEC** (Hostinger domain → DNS/DNSSEC).
- `CAA` record: `0 issue "letsencrypt.org"`.

> If you go Cloudflare (Part F), DNS moves to Cloudflare and these are set there instead.

---

## Part B — Harden + deploy  (you, on the VPS via SSH)

Connect: `ssh -i C:\Users\prave\.ssh\newvps root@YOUR_SERVER_IP`

**Phase 4 — base + firewall.** Copy `01-harden.sh` up and run it (installs nginx, certbot,
**aspnetcore-runtime-8.0** [required — the app is framework-dependent], ufw allow 22+443, creates
`/opt/app/{app,data,recordings}`). This does NOT touch SSH reachability yet.

**Phase 5 — deploy the app.** From your PC (PowerShell/scp):
```
scp -i C:\Users\prave\.ssh\newvps -r deploy\out\server-linux\*            root@YOUR_SERVER_IP:/opt/app/app/
scp -i C:\Users\prave\.ssh\newvps    deploy\newvps\appsettings.Production.json root@YOUR_SERVER_IP:/opt/app/app/
scp -i C:\Users\prave\.ssh\newvps    deploy\newvps\Output\ManagerToolAgentSetup.exe root@YOUR_SERVER_IP:/opt/app/app/agent-installer/
scp -i C:\Users\prave\.ssh\newvps    deploy\newvps\managertool.service      root@YOUR_SERVER_IP:/etc/systemd/system/
scp -i C:\Users\prave\.ssh\newvps    deploy\newvps\snippets-mt-proxy.conf   root@YOUR_SERVER_IP:/etc/nginx/snippets/mt-proxy.conf
scp -i C:\Users\prave\.ssh\newvps    deploy\newvps\nginx-managertool-public.conf root@YOUR_SERVER_IP:/etc/nginx/sites-available/managertool
```
Then on the VPS:
```
# 5c. least-privilege owner for the app tree
useradd -r -s /usr/sbin/nologin www-data 2>/dev/null || true   # usually already exists
chown -R www-data:www-data /opt/app
# 5d. enable + start service (auto-start on boot, restart on crash)
# (nginx site is already filled with your-server.example.com — no edit needed)
ln -s /etc/nginx/sites-available/managertool /etc/nginx/sites-enabled/managertool
systemctl daemon-reload
systemctl enable --now managertool
systemctl status managertool          # expect: active (running)
curl -s http://127.0.0.1:5281/health  # expect: "Manager Tool server running..."
```

**Phase 6 — Wildcard TLS.** Two paths — pick ONE:

*Path 1 — manual DNS-01 (works today, but you re-do the TXT every 90 days):*
```
certbot certonly --manual --preferred-challenges dns -d "*.example.com" -d "example.com"
# add the TXT record it prints at your DNS host, wait for propagation, press Enter.
# cert lands in /etc/letsencrypt/live/example.com/  (named after the FIRST -d).
```
*Path 2 — Cloudflare DNS plugin (hands-off 90-day auto-renew):* see **Part F**.

The `-public.conf` already points nginx at `/etc/letsencrypt/live/example.com/…` (the
wildcard cert lineage — Part F pins it with `--cert-name example.com`). Once the cert exists:
```
nginx -t && systemctl reload nginx
```

**Phase 7 — verify over the internet.**
```
curl -I https://your-server.example.com/                       # 200, real cert
curl -s https://your-server.example.com/health                 # server-running text
```
Open `https://your-server.example.com/` in a browser and log in with the admin creds from `SECRETS.txt`
(username `admin@example.com`). Confirm a live screen view works once an agent is on.

---

## Part C — NordVPN admin-lock  (reach the server through NordVPN; server only trusts you)

**Requirement: NordVPN _Dedicated IP_ add-on** (consumer NordVPN rotates its exit IP, so it can't
be allow-listed). Buy it, connect to that server, note the static IP → `NORD_IP`.

The design (correcting the "put the VPS on NordVPN" instinct): the **server stays public on 443**
so agents can reach it; **you** connect out through NordVPN and the box only accepts **admin** access
from `NORD_IP`.

**C1 — lock SSH to NordVPN.** ⚠️ Before running, test Hostinger's **browser-VNC console** (that's
your recovery path if NordVPN ever won't connect) and be connected to the dedicated IP now. Then run
`02-ufw-nordvpn.sh` (opens 443, allows 22 only from `NORD_IP`, deletes the world-open 22). Verify in a
second terminal that SSH still works before closing the first.

**C2 — (stronger) lock the admin dashboard too, at nginx.** Swap the site file to the lock variant:
```
scp -i C:\Users\prave\.ssh\newvps deploy\newvps\nginx-managertool-vpnlock.conf root@YOUR_SERVER_IP:/etc/nginx/sites-available/managertool
# on the VPS: set your-server.example.com and NORD_IP in that file, then:
nginx -t && systemctl reload nginx
```
This keeps `/hub`, `/api/auth/enroll`, `/api/agent/`, `/dl/`, `/health` public (agents need them) and
locks the dashboard + admin APIs to `NORD_IP`. Test: an agent can still enroll/connect, and you can
still log in **while on NordVPN**.

Your admin workflow from now on: connect NordVPN (dedicated IP) → SSH or open `https://your-server.example.com`.

---

## Part D — Cutover  (you, once the new box is verified)

1. Agent installer is **already built** for the new server:
   `deploy/newvps/Output/ManagerToolAgentSetup.exe` — ServerUrl `https://your-server.example.com`,
   new enrollment key baked in. (To rebuild later: `.\deploy\publish.ps1 -Agent`, then
   `& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /O"deploy\newvps\Output" .\deploy\ManagerToolAgent.newvps.iss`.)
2. Re-install the agent on each monitored PC (`DESKTOP-Q3BQ628`). They re-enroll against the new
   server (new Issuer/Audience + new key invalidate old tokens — re-enroll is expected). Confirm they
   appear on the new dashboard.
3. **Only then** decommission the old box: `systemctl stop managertool` (old name: `deskwatch`) on
   `YOUR_OLD_SERVER_IP`, remove its DNS, and destroy the old VPS after final verification.

---

## Part E — Optional PTR
Ask Hostinger support to set a neutral reverse-DNS (PTR) on `YOUR_SERVER_IP`.

---

## Part F — Cloudflare + auto-renew (the part people get stuck on)

Goal: origin IP never appears in DNS or CT logs, and TLS auto-renews with zero manual TXT edits.

1. Add the domain to Cloudflare (free); at your registrar (Hostinger) change the **nameservers** to
   the two Cloudflare gives you. Wait for "Active".
2. In Cloudflare DNS: `A` `your-server.example.com` → `YOUR_SERVER_IP`, **proxied (orange cloud)**. This hides `YOUR_SERVER_IP`.
3. Auto-renewing wildcard via the Cloudflare DNS plugin (on the VPS):
   ```
   apt install -y python3-certbot-dns-cloudflare
   # create a scoped API token in Cloudflare (Zone.DNS: Edit for this zone only), then:
   install -m 600 /dev/stdin /root/cf.ini <<'EOF'
   dns_cloudflare_api_token = PASTE_SCOPED_TOKEN
   EOF
   certbot certonly --dns-cloudflare --dns-cloudflare-credentials /root/cf.ini \
     --cert-name example.com -d "*.example.com" -d "example.com"
   ```
   certbot installs a systemd timer that renews automatically — no more manual TXT records.
4. Cloudflare SSL/TLS mode → **Full (strict)** so CF↔origin uses your real cert.

**Interaction with the NordVPN admin-lock (important):** once traffic is proxied by Cloudflare,
nginx sees Cloudflare's IP, so `allow NORD_IP` would deny everyone. To keep BOTH, uncomment the
`set_real_ip_from` / `real_ip_header CF-Connecting-IP` block in `nginx-managertool-vpnlock.conf` and
list Cloudflare's IP ranges — that restores your true client IP so `allow NORD_IP` matches again.
(Trade-off, honestly: Cloudflare terminates TLS and can see traffic — including screen data. Weigh
that. If you'd rather Cloudflare never sees admin traffic, keep the admin host DNS-only/grey-cloud and
proxy only a separate public agent host.)

---

## Verification checklist (all must pass before decommissioning old box)
- [ ] `systemctl status managertool` = active (running); survives `reboot`
- [ ] `curl -I https://your-server.example.com/` = 200 with a valid (non-staging) cert
- [ ] Admin login works at `https://your-server.example.com/` with `SECRETS.txt` creds
- [ ] Agent on `DESKTOP-Q3BQ628` enrolls + appears; live screen view works
- [ ] (If NordVPN-lock on) admin dashboard refuses connections when NOT on NordVPN; agents still connect
- [ ] Browser-VNC console tested and reachable (SSH lockout recovery path)
- [ ] `SECRETS.txt` copied into your password manager, then deleted from disk

## Rollback safety
The old box keeps running untouched throughout. If anything on the new box fails, agents on the old
installer still report to `your-old-server.example.com`. Nothing is destroyed until the final step of Part D.
