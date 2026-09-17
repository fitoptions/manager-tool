"use strict";
// Manager Tool web admin — talks to the same API + SignalR hub as the desktop console.

const $ = (id) => document.getElementById(id);
let token = sessionStorage.getItem("dw_token") || null;
let hub = null;
let hosts = [];
let selectedHostId = null;
let viewingHostId = null;
let selectedMonitor = 0;
let knownMonitors = 0;
let recording = false;
let unack = 0;
let latestAgentVersion = null;   // the version the server offers for install; hosts behind it are flagged
let updateStatus = {};           // hostId -> latest UpdateStatus (stage/version/detail/utc)

// ---------- API ----------
async function api(path, opts = {}) {
  const headers = Object.assign({ "Authorization": "Bearer " + token }, opts.headers || {});
  const res = await fetch(path, Object.assign({}, opts, { headers }));
  if (res.status === 401) { logout(); throw new Error("unauthorized"); }
  return res;
}
async function apiJson(path) { const r = await api(path); return r.ok ? r.json() : null; }

// Compare dotted version strings: <0 if a<b, 0 if equal, >0 if a>b. Missing parts count as 0.
function cmpVersions(a, b) {
  const pa = String(a).split(".").map((n) => parseInt(n, 10) || 0);
  const pb = String(b).split(".").map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d < 0 ? -1 : 1;
  }
  return 0;
}
// Coarse "x ago" for a UTC timestamp; used for the connected-since tooltip.
function relTime(utc) {
  const t = new Date(utc).getTime();
  if (isNaN(t)) return "";
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 90) return "just now";
  const m = s / 60; if (m < 90) return Math.round(m) + " min ago";
  const h = m / 60; if (h < 36) return Math.round(h) + " h ago";
  return Math.round(h / 24) + " d ago";
}

// ---------- Login ----------
$("loginForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  $("loginErr").textContent = "";
  $("loginErr").style.color = "";   // clear the green "password updated" state from a reset
  $("loginBtn").disabled = true;
  try {
    const res = await fetch("/api/auth/login", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: $("username").value.trim(), password: $("password").value })
    });
    if (!res.ok) { $("loginErr").textContent = "Invalid username or password."; return; }
    const data = await res.json();
    token = data.accessToken;
    sessionStorage.setItem("dw_token", token);
    await startApp();
  } catch (ex) {
    $("loginErr").textContent = "Could not connect: " + ex.message;
  } finally {
    $("loginBtn").disabled = false;
  }
});

function logout() {
  token = null;
  sessionStorage.removeItem("dw_token");
  if (hub) { hub.stop(); hub = null; }
  $("app").classList.add("hidden");
  $("login").classList.remove("hidden");
}
$("logoutBtn").addEventListener("click", logout);

// Username from the JWT subject claim.
function currentUser() {
  try { return JSON.parse(atob(token.split(".")[1].replace(/-/g,"+").replace(/_/g,"/"))).sub || "admin"; }
  catch { return "admin"; }
}

// ---------- Forgot password (anonymous) ----------
// The response is deliberately identical whether or not the account exists, and the wording says so,
// so nobody can use this form to discover which usernames are real.
$("showForgot").addEventListener("click", (e) => { e.preventDefault(); showCard("forgotForm"); });
$("backToLogin2").addEventListener("click", (e) => { e.preventDefault(); showCard("loginForm"); });
// Remembered between the two steps so the user doesn't retype it into the code form.
let resetUsername = "";

function showCard(id) {
  ["loginForm", "forgotForm", "otpForm"].forEach((c) => $(c).classList.toggle("hidden", c !== id));
}
$("backToLogin3").addEventListener("click", (e) => { e.preventDefault(); showCard("loginForm"); });

async function requestResetCode(username, msg) {
  const res = await fetch("/api/auth/reset-request", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username })
  });
  if (res.status === 429) {
    msg.style.color = "var(--warn)";
    msg.textContent = "Too many requests just now — try again in a minute.";
    return null;
  }
  return await res.json();   // { codeSent } — says nothing about whether the account exists
}

$("forgotForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const msg = $("forgotMsg");
  const username = $("forgotUser").value.trim();
  if (!username) { msg.style.color = "var(--crit)"; msg.textContent = "Enter your username."; return; }

  $("forgotBtn").disabled = true;
  try {
    const result = await requestResetCode(username, msg);
    if (!result) return;

    if (result.codeSent) {
      // Move to the code step. Deliberately phrased "if that account exists" — we are not told,
      // and must not imply, whether an email actually went anywhere.
      resetUsername = username;
      $("otpSub").textContent =
        `If ${username} is a registered account, a 6-digit code is on its way to that address. It expires in 10 minutes.`;
      $("otpCode").value = ""; $("otpNewPass").value = ""; $("otpNewPass2").value = ""; $("otpMsg").textContent = "";
      showCard("otpForm");
      $("otpCode").focus();
    } else {
      msg.style.color = "var(--ok)";
      msg.textContent = "If that account exists, the owner has been notified. Contact them to collect your new password.";
      $("forgotUser").value = "";
    }
  } catch {
    msg.style.color = "var(--crit)";
    msg.textContent = "Could not reach the server.";
  } finally {
    $("forgotBtn").disabled = false;
  }
});

$("otpResend").addEventListener("click", async (e) => {
  e.preventDefault();
  const msg = $("otpMsg");
  const result = await requestResetCode(resetUsername, msg);
  if (!result) return;
  msg.style.color = "var(--ok)";
  msg.textContent = "A new code has been sent. The previous one no longer works.";
});

$("otpForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const msg = $("otpMsg");
  msg.style.color = "var(--crit)";
  const code = $("otpCode").value.trim();
  const pw = $("otpNewPass").value, pw2 = $("otpNewPass2").value;

  if (!code) { msg.textContent = "Enter the code from your email."; return; }
  if (pw.length < 8) { msg.textContent = "New password must be at least 8 characters."; return; }
  if (pw !== pw2) { msg.textContent = "The two new passwords do not match."; return; }

  $("otpBtn").disabled = true;
  try {
    const res = await fetch("/api/auth/reset-password", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: resetUsername, code, newPassword: pw })
    });
    if (res.status === 429) {
      msg.style.color = "var(--warn)";
      msg.textContent = "Too many attempts just now — try again in a minute.";
      return;
    }
    if (!res.ok) { msg.textContent = await errorText(res, "That code is invalid or has expired."); return; }

    showCard("loginForm");
    $("username").value = resetUsername;
    $("password").value = "";
    $("password").focus();
    $("loginErr").style.color = "var(--ok)";
    $("loginErr").textContent = "Password updated — sign in with your new password.";
  } catch {
    msg.textContent = "Could not reach the server.";
  } finally {
    $("otpBtn").disabled = false;
  }
});

// ---------- Account drawer ----------
$("accountBtn").addEventListener("click", async () => {
  $("whoami").textContent = currentUser();
  $("passMsg").textContent = "";
  $("accountDrawer").style.display = "block";
  await loadAdmins();
});
$("accountClose").addEventListener("click", () => $("accountDrawer").style.display = "none");

// ---------- Updates / rollout drawer ----------
$("rolloutBtn").addEventListener("click", async () => {
  $("rolloutMsg").textContent = "";
  $("rolloutDrawer").style.display = "block";
  await refreshUpdateStatus();
  await loadRollout();
  renderRolloutStatuses();
});
$("rolloutClose").addEventListener("click", () => $("rolloutDrawer").style.display = "none");

// Friendly name for a host id: nickname, else its connected machine name, else the raw id.
function hostName(hostId) {
  if (hostLabels[hostId]) return hostLabels[hostId];
  const h = hosts.find((x) => x.hostId === hostId);
  return h ? h.machineName : hostId;
}
function stageColor(stage) {
  if (stage === "ok") return "var(--ok)";
  if (stage === "failed" || stage === "rolledback" || stage === "error") return "var(--crit)";
  return "var(--warn)";   // triggered | downloading | installing | verifying
}

async function refreshUpdateStatus() {
  const list = await apiJson("/api/hosts/update-status");
  if (!list) return;
  updateStatus = {};
  list.forEach((s) => { updateStatus[s.hostId] = s; });
}

async function loadRollout() {
  const p = await apiJson("/api/rollout");
  if (!p) return;
  const fleet = p.fleetOpen
    ? `<b style="color:var(--ok)">fleet open</b>`
    : `<b style="color:var(--warn)">held — waiting on canary</b>`;
  const canary = p.canaryHostId ? hostName(p.canaryHostId) : "none (all hosts)";
  $("rolloutState").innerHTML =
    `Target <b style="color:var(--ink)">v${p.targetVersion || "—"}</b> · canary <b style="color:var(--ink)">${canary}</b> · ${fleet}`;
  if (!$("rolloutTarget").value) $("rolloutTarget").value = p.targetVersion || "";

  // Canary options = connected hosts, plus the currently-set canary even if offline.
  const sel = $("rolloutCanary");
  sel.innerHTML = `<option value="">All hosts at once (no canary)</option>`;
  const seen = new Set();
  hosts.forEach((h) => { seen.add(h.hostId); sel.appendChild(new Option(hostName(h.hostId), h.hostId)); });
  if (p.canaryHostId && !seen.has(p.canaryHostId))
    sel.appendChild(new Option(hostName(p.canaryHostId) + " (offline)", p.canaryHostId));
  sel.value = p.canaryHostId || "";
}

$("rolloutSave").addEventListener("click", async () => {
  const msg = $("rolloutMsg"); msg.textContent = ""; msg.style.color = "var(--crit)";
  const targetVersion = $("rolloutTarget").value.trim();
  const canaryHostId = $("rolloutCanary").value || null;
  if (!targetVersion) { msg.textContent = "Enter a target version."; return; }
  const res = await api("/api/rollout", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ targetVersion, canaryHostId })
  });
  if (res.status === 403) { msg.textContent = "Only the owner can set a rollout."; return; }
  if (!res.ok) { msg.textContent = await errorText(res, "Could not save rollout."); return; }
  msg.style.color = "var(--ok)"; msg.textContent = "Rollout saved.";
  await loadRollout();
});

function renderRolloutStatuses() {
  const box = $("rolloutStatuses");
  if (!box) return;
  const items = Object.values(updateStatus)
    .sort((a, b) => new Date(b.utc) - new Date(a.utc));
  if (items.length === 0) { box.innerHTML = `<div style="color:var(--ink3)">No update activity yet.</div>`; return; }
  box.innerHTML = "";
  items.forEach((s) => {
    const row = document.createElement("div");
    row.style.cssText = "padding:8px 0;border-bottom:1px solid var(--surface2)";
    row.innerHTML =
      `<b></b> <span style="font-family:var(--mono);font-size:.72rem"></span>` +
      `<div style="color:var(--ink3);font-size:.76rem;margin-top:2px"></div>`;
    row.querySelector("b").textContent = hostName(s.hostId);
    const chip = row.querySelector("span");
    chip.textContent = s.stage + (s.version ? ` v${s.version}` : "");
    chip.style.color = stageColor(s.stage);
    row.querySelector("div").textContent =
      (s.detail || "") + (s.utc ? ` · ${relTime(s.utc)}` : "");
    box.appendChild(row);
  });
}

$("changePassBtn").addEventListener("click", async () => {
  const msg = $("passMsg"); msg.textContent = ""; msg.style.color = "var(--crit)";
  const cur = $("curPass").value, nw = $("newPass").value;
  if (nw.length < 8) { msg.textContent = "New password must be at least 8 characters."; return; }
  const res = await api("/api/auth/change-password", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ currentPassword: cur, newPassword: nw })
  });
  if (res.status === 204) { msg.style.color = "var(--ok)"; msg.textContent = "Password updated."; $("curPass").value = ""; $("newPass").value = ""; }
  else { msg.textContent = await errorText(res, "Could not change password."); }
});

// ---------- Agent download password (owner) ----------
$("dlPassBtn").addEventListener("click", async () => {
  const msg = $("dlMsg"); msg.textContent = ""; msg.style.color = "var(--crit)";
  const pw = $("dlPass").value;
  if (pw.length < 8) { msg.textContent = "Password must be at least 8 characters."; return; }
  const res = await api("/api/settings/download-password", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ currentPassword: "", newPassword: pw })
  });
  if (res.status === 204) { msg.style.color = "var(--ok)"; msg.textContent = "Download password updated."; $("dlPass").value = ""; }
  else { msg.textContent = await errorText(res, "Could not set password."); }
});

// ASP.NET returns these refusal strings as JSON ("..."), so reading the body raw would show the
// quotes verbatim to the operator. Unwrap a JSON string; fall back to the body, then the default.
async function errorText(res, fallback) {
  const raw = (await res.text()).trim();
  if (!raw) return fallback;
  try {
    const parsed = JSON.parse(raw);
    if (typeof parsed === "string") return parsed;
    if (parsed && typeof parsed.title === "string") return parsed.title;
  } catch { /* not JSON — show it as-is */ }
  return raw;
}

// ---------- Issuing viewer accounts (owner only) ----------

// Ambiguity-free alphabet: no O/0, l/1/I. These get read off a screen and typed by hand.
function generatePassword(len = 14) {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
  return Array.from(crypto.getRandomValues(new Uint32Array(len)))
    .map((n) => alphabet[n % alphabet.length]).join("");
}

$("genViewerPass").addEventListener("click", (e) => {
  e.preventDefault();
  $("newViewerPass").value = generatePassword();
});

$("createViewerBtn").addEventListener("click", async (e) => {
  e.preventDefault();
  const msg = $("newViewerMsg");
  const username = $("newViewerUser").value.trim();
  const password = $("newViewerPass").value;
  msg.style.color = "var(--crit)";

  if (!username) { msg.textContent = "Username is required."; return; }
  if (password.length < 8) { msg.textContent = "Password must be at least 8 characters."; return; }

  const res = await api("/api/admins", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password })
  });
  if (!res.ok) { msg.textContent = await errorText(res, "Could not create that viewer."); return; }

  msg.style.color = "var(--ok)";
  msg.textContent = `Created “${username}”. Give them this password now — it is not shown again.`;
  $("newViewerUser").value = "";
  $("newViewerPass").value = "";
  await loadAdmins();   // also refreshes the access list so the new viewer is restrictable at once
});

// ---------- Admin management (owner only) ----------
async function loadAdmins() {
  const res = await api("/api/admins");
  if (res.status !== 200) { $("adminMgmt").classList.add("hidden"); return; }  // not the owner
  $("adminMgmt").classList.remove("hidden");
  const admins = await res.json();
  const box = $("adminList"); box.innerHTML = "";
  admins.forEach((a) => {
    const el = document.createElement("div");
    el.style.cssText = "display:flex;align-items:center;gap:8px;padding:9px 0;border-bottom:1px solid var(--surface2)";
    const pill = a.isOwner ? `<span style="font-family:var(--mono);font-size:.66rem;color:var(--accent);border:1px solid #1e4750;padding:1px 6px;border-radius:5px">OWNER</span>`
      : a.status === "pending" ? `<span style="font-family:var(--mono);font-size:.66rem;color:var(--warn);border:1px solid #4a3a1a;padding:1px 6px;border-radius:5px">PENDING</span>`
      : `<span style="font-family:var(--mono);font-size:.66rem;color:var(--ok);border:1px solid #1c4a35;padding:1px 6px;border-radius:5px">ACTIVE</span>`;
    el.innerHTML = `<span style="flex:1">${a.username}</span> ${pill}`;
    if (!a.isOwner) {
      if (a.status === "pending") {
        const ap = document.createElement("button"); ap.className = "btn"; ap.style.cssText = "padding:3px 10px;font-size:.75rem"; ap.textContent = "Approve";
        ap.onclick = async () => { await api(`/api/admins/${encodeURIComponent(a.username)}/approve`, { method: "POST" }); loadAdmins(); };
        el.appendChild(ap);
      }
      const rs = document.createElement("button"); rs.className = "btn ghost"; rs.style.cssText = "padding:3px 10px;font-size:.75rem"; rs.textContent = "Reset pw";
      rs.onclick = async () => {
        const np = prompt(`Set a new password for “${a.username}” (min 8 chars):`);
        if (!np) return;
        if (np.length < 8) { alert("Password must be at least 8 characters."); return; }
        const r = await api(`/api/admins/${encodeURIComponent(a.username)}/reset-password`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ currentPassword: "", newPassword: np })
        });
        alert(r.status === 204 ? `Password reset for ${a.username}.` : "Could not reset password.");
      };
      el.appendChild(rs);
      const rm = document.createElement("button"); rm.className = "btn ghost"; rm.style.cssText = "padding:3px 10px;font-size:.75rem;color:var(--crit)"; rm.textContent = "Remove";
      rm.onclick = async () => { if (confirm(`Remove admin “${a.username}”?`)) { await api(`/api/admins/${encodeURIComponent(a.username)}`, { method: "DELETE" }); loadAdmins(); } };
      el.appendChild(rm);
    }
    box.appendChild(el);
  });
  await loadResetQueue();
  await refreshResetBadge();
  await loadAccess();
}

// ---------- Locked-out viewers awaiting an owner reset ----------
async function loadResetQueue() {
  const tickets = await apiJson("/api/admins/reset-requests");
  const wrap = $("resetQueueWrap"), box = $("resetQueue");
  if (!tickets || tickets.length === 0) { wrap.classList.add("hidden"); box.innerHTML = ""; return; }
  wrap.classList.remove("hidden");
  box.innerHTML = "";

  tickets.forEach((t) => {
    const el = document.createElement("div");
    el.style.cssText = "display:flex;align-items:center;gap:8px;padding:9px 0;border-bottom:1px solid var(--surface2)";
    const when = new Date(t.requestedUtc).toLocaleString();
    el.innerHTML = `<span style="flex:1">${t.username}<span style="color:var(--ink3);font-size:.75rem"> — ${when}</span></span>`;

    const set = document.createElement("button");
    set.className = "btn"; set.style.cssText = "padding:3px 10px;font-size:.75rem"; set.textContent = "Set password";
    set.onclick = async () => {
      const np = prompt(`New password for “${t.username}” (min 8 chars):`, generatePassword());
      if (!np) return;
      if (np.length < 8) { alert("Password must be at least 8 characters."); return; }
      const r = await api(`/api/admins/${encodeURIComponent(t.username)}/reset-password`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ currentPassword: "", newPassword: np })
      });
      alert(r.status === 204
        ? `Password set for ${t.username}. Give it to them now — it is not shown again.`
        : "Could not reset that password.");
      loadAdmins();   // the ticket clears itself once actioned
    };
    el.appendChild(set);

    const dismiss = document.createElement("button");
    dismiss.className = "btn ghost"; dismiss.style.cssText = "padding:3px 10px;font-size:.75rem"; dismiss.textContent = "Dismiss";
    dismiss.onclick = async () => {
      if (!confirm(`Dismiss the reset request from “${t.username}” without changing their password?`)) return;
      await api(`/api/admins/reset-requests/${encodeURIComponent(t.username)}`, { method: "DELETE" });
      loadAdmins();
    };
    el.appendChild(dismiss);

    box.appendChild(el);
  });
}

// ---------- Workstation access per viewer (owner only) ----------
// Deny-list: no blocks means the viewer sees everything, now and in future. Clicking a PC chip
// toggles it and saves immediately — the server is the enforcer, this is only the control surface.
async function loadAccess() {
  const box = $("accessList");
  const data = await apiJson("/api/access");
  if (!data) { box.innerHTML = ""; return; }

  box.innerHTML = "";
  if (!data.viewers.length) {
    box.innerHTML = `<div style="color:var(--ink3);font-size:.85rem">No viewer accounts yet. Approved admins appear here.</div>`;
    return;
  }
  if (!data.hosts.length) {
    box.innerHTML = `<div style="color:var(--ink3);font-size:.85rem">No workstations have enrolled yet.</div>`;
    return;
  }

  data.viewers.forEach((v) => box.appendChild(accessRow(v, data.hosts)));
}

function accessRow(viewer, allHosts) {
  const denied = new Set(viewer.deniedHostIds || []);

  const el = document.createElement("div");
  el.className = "acc-viewer";
  el.innerHTML = `<div class="acc-name"><span></span></div><div class="acc-chips"></div><div class="acc-sum"></div>`;
  el.querySelector(".acc-name span").textContent = viewer.username;
  if (viewer.status === "pending") {
    const p = document.createElement("span");
    p.style.cssText = "font-family:var(--mono);font-size:.66rem;color:var(--warn);border:1px solid #4a3a1a;padding:1px 6px;border-radius:5px";
    p.textContent = "PENDING";
    el.querySelector(".acc-name").appendChild(p);
  }

  const chips = el.querySelector(".acc-chips");
  const summary = el.querySelector(".acc-sum");

  function paintSummary() {
    summary.textContent = denied.size === 0
      ? `Sees all ${allHosts.length} workstation(s), including any enrolled later.`
      : `Blocked from ${denied.size} of ${allHosts.length} workstation(s).`;
  }

  allHosts.forEach((h) => {
    const chip = document.createElement("button");
    chip.className = "acc-chip" + (denied.has(h.hostId) ? " blocked" : "");
    chip.innerHTML = `<span class="st${h.online ? " up" : ""}"></span><span class="lbl"></span>`;
    chip.querySelector(".lbl").textContent = h.nickname || h.machineName;
    chip.title = `${h.machineName} · ${h.online ? "online" : "last seen " + new Date(h.lastSeenUtc).toLocaleString()}`;
    chip.addEventListener("click", async () => {
      if (denied.has(h.hostId)) denied.delete(h.hostId); else denied.add(h.hostId);
      chip.classList.toggle("blocked", denied.has(h.hostId));
      paintSummary();
      const res = await api(`/api/access/${encodeURIComponent(viewer.username)}`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deniedHostIds: Array.from(denied) })
      });
      if (res.status !== 204) { alert("Could not save access change."); loadAccess(); }
    });
    chips.appendChild(chip);
  });

  paintSummary();
  return el;
}

// ---------- App startup ----------
async function startApp() {
  $("login").classList.add("hidden");
  $("app").classList.remove("hidden");
  await connectHub();
  await loadLabels();
  await refreshLatestVersion();
  await refreshUpdateStatus();
  await refreshHosts();
  await refreshAlertBadge();
  setInterval(refreshHosts, 5000);

  // Only the owner can read the lockout queue, so only poll it for them — otherwise every viewer
  // session would log a 403 every minute. Lockouts are rare, so a slow poll is plenty.
  if (await refreshResetBadge())
    setInterval(refreshResetBadge, 60000);
}

// Surfaces the locked-out count on the Account button, so the owner sees it without opening the
// drawer.
// Returns false when this account may not read the queue (i.e. is not the owner).
async function refreshResetBadge() {
  const res = await api("/api/admins/reset-requests");
  if (!res.ok) { $("resetBadge").textContent = ""; return false; }
  const tickets = await res.json();
  $("resetBadge").textContent = tickets.length ? `  ${tickets.length}` : "";
  return true;
}

// Admin-assigned host nicknames (hostId -> friendly name).
let hostLabels = {};
async function loadLabels() { hostLabels = (await apiJson("/api/host-labels")) || {}; }
function hostName(h) { return hostLabels[h.hostId] || h.machineName; }
async function renameHost(h) {
  const cur = hostLabels[h.hostId] || "";
  const name = prompt(`Nickname for ${h.machineName} (blank to clear):`, cur);
  if (name === null) return;  // cancelled
  await api(`/api/hosts/${encodeURIComponent(h.hostId)}/label`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ nickname: name })
  });
  await loadLabels();
  refreshHosts();
  if (fillHostSelect) { fillHostSelect($("repHost")); fillHostSelect($("recHost")); }
}

async function connectHub() {
  hub = new signalR.HubConnectionBuilder()
    .withUrl("/hub", { accessTokenFactory: () => token })
    .withAutomaticReconnect()
    .build();

  hub.on("HostConnected", () => refreshHosts());
  hub.on("HostDisconnected", () => refreshHosts());
  hub.on("FrameReceived", (f) => onFrame(f));
  hub.on("FrameReceived", (f) => window.MTWall && window.MTWall.onFrame(f));
  hub.on("TilesReceived", (f) => onTiles(f));
  hub.on("TilesReceived", (f) => window.MTWall && window.MTWall.onTiles(f));
  hub.on("AlertReceived", (a) => onLiveAlert(a));
  hub.on("ViewersChanged", (hostId, list) => onViewersChanged(hostId, list));
  hub.on("AccessChanged", (denied) => onAccessChanged(denied || []));
  hub.on("UpdateStatusChanged", (s) => { updateStatus[s.hostId] = s; renderRolloutStatuses(); refreshHosts(); });

  // Frames are relayed to a per-host group that a connection joins only inside StartViewing.
  // withAutomaticReconnect() reconnects with a NEW connection id after any transient drop
  // (idle WebSocket, proxy timeout, network blip), which silently drops that group membership —
  // the feed then freezes on its last frame until StartViewing is called again. Re-subscribe
  // everything we were watching so the stream heals itself instead of needing a manual Stop/View.
  hub.onreconnected(() => resubscribeViews());

  try { await hub.start(); } catch (e) { console.warn("hub start failed", e); }
}

// Re-issue StartViewing for everything currently on screen after a reconnect: the single live
// view, and every host the wall still wants. Safe to call when nothing is being viewed.
async function resubscribeViews() {
  try {
    if (viewingHostId) {
      await hub.invoke("StartViewing", viewingHostId);
      if (selectedMonitor > 0) await hub.invoke("SelectMonitor", viewingHostId, selectedMonitor);
    }
    if (window.MTWall && window.MTWall.resubscribe) window.MTWall.resubscribe();
  } catch (e) {
    console.warn("re-subscribe after reconnect failed", e);
  }
}

// ---------- Hosts ----------
// The version the server currently offers for install (from /api/agent/version). Hosts running an
// older build are badged amber. Fetched once at startup; it only changes on a server deploy.
async function refreshLatestVersion() {
  const v = await apiJson("/api/agent/version");
  if (v && v.version) latestAgentVersion = v.version;
}

async function refreshHosts() {
  const list = await apiJson("/api/hosts");
  if (!list) return;
  hosts = list;
  $("hostCount").textContent = list.length + " online";
  const box = $("hosts");
  box.innerHTML = "";
  list.forEach((h) => {
    const el = document.createElement("div");
    el.className = "host" + (h.hostId === selectedHostId ? " sel" : "");
    const nick = hostLabels[h.hostId];
    el.innerHTML = `<span class="dot"></span><div style="flex:1;min-width:0"><div class="nm"></div><div class="u"></div></div>` +
      `<span class="ver"></span>` +
      `<button class="renameBtn" title="Rename this PC" style="background:none;border:none;color:var(--ink3);font-size:.9rem;padding:2px 6px">✎</button>`;
    el.querySelector(".nm").textContent = nick || h.machineName;
    // When a nickname is set, show the real machine name + user as the subtitle for reference.
    el.querySelector(".u").textContent = nick ? `${h.machineName} · ${h.userName}` : h.userName;
    // Agent version badge — amber when the host is behind the installer the server currently offers.
    const ver = el.querySelector(".ver");
    const v = h.agentVersion || "?";
    ver.textContent = "v" + v;
    if (latestAgentVersion && v !== "?" && cmpVersions(v, latestAgentVersion) < 0) {
      ver.classList.add("old");
      ver.title = `Update available → v${latestAgentVersion}`;
    } else {
      ver.title = latestAgentVersion ? "Up to date" : "";
    }
    // Connected-since surfaces on the status dot without cluttering the row.
    if (h.connectedAtUtc) el.querySelector(".dot").title = "Connected " + relTime(h.connectedAtUtc);
    el.querySelector(".renameBtn").addEventListener("click", (e) => { e.stopPropagation(); renameHost(h); });
    el.addEventListener("click", () => selectHost(h.hostId));
    box.appendChild(el);
  });
}

function selectHost(id) {
  selectedHostId = id;
  refreshHosts();
  $("viewBtn").disabled = false;
  $("removeBtn").disabled = false;
  $("updateBtn").disabled = false;
}

// ---------- Live screen ----------
$("viewBtn").addEventListener("click", async () => {
  if (!selectedHostId) return;
  viewingHostId = selectedHostId;
  selectedMonitor = 0; knownMonitors = 0;
  $("monitor").innerHTML = "";
  await hub.invoke("StartViewing", viewingHostId);
  $("stopBtn").disabled = false; $("recBtn").disabled = false; $("viewBtn").disabled = true; $("fsBtn").disabled = false;
  $("viewStatus").textContent = "connecting…";
  paintViewers();
});
$("stopBtn").addEventListener("click", stopView);
async function stopView() {
  if (!viewingHostId) return;
  if (recording) await toggleRecord();
  await hub.invoke("StopViewing", viewingHostId);
  viewingHostId = null;
  $("screen").classList.add("hidden");
  $("screenHint").classList.remove("hidden");
  $("stopBtn").disabled = true; $("recBtn").disabled = true; $("viewBtn").disabled = !selectedHostId; $("fsBtn").disabled = true;
  $("viewStatus").textContent = "";
  $("viewers").textContent = "";
}

// ---------- Collapse side panel + full screen ----------
$("collapseBtn").addEventListener("click", () => {
  const on = $("app").classList.toggle("collapsed");
  $("collapseBtn").textContent = on ? "⟩⟨ Expand" : "⟨⟩ Collapse";
});
$("fsBtn").addEventListener("click", () => {
  const el = $("screenwrap");
  if (document.fullscreenElement) document.exitFullscreen();
  else if (el.requestFullscreen) el.requestFullscreen();
});
document.addEventListener("fullscreenchange", () => {
  $("fsBtn").textContent = document.fullscreenElement ? "⛶ Exit full screen" : "⛶ Full screen";
});
// Double-clicking the screen also toggles full screen.
$("screen").addEventListener("dblclick", () => $("fsBtn").click());
$("monitor").addEventListener("change", async () => {
  selectedMonitor = parseInt($("monitor").value, 10) || 0;
  if (viewingHostId) await hub.invoke("SelectMonitor", viewingHostId, selectedMonitor);
});
$("recBtn").addEventListener("click", toggleRecord);
async function toggleRecord() {
  if (!viewingHostId) return;
  if (!recording) { await hub.invoke("StartRecording", viewingHostId); recording = true; $("recBtn").textContent = "■ Stop Rec"; }
  else { await hub.invoke("StopRecording", viewingHostId); recording = false; $("recBtn").textContent = "● Rec"; }
}

// ---------- Who else is watching ----------
// Several admins can watch one host at once; the agent still captures only once. The server
// pushes the current watcher list per host so nobody is observed-by-committee unknowingly.
let viewersByHost = {};

function onViewersChanged(hostId, list) {
  viewersByHost[hostId] = list || [];
  if (hostId === viewingHostId) paintViewers();
  if (window.MTWall && window.MTWall.onViewers) window.MTWall.onViewers(hostId, viewersByHost[hostId]);
}

function viewersOf(hostId) { return viewersByHost[hostId] || []; }

// The owner changed this account's access. Tear down anything showing a now-blocked PC rather
// than leaving a frozen last frame, then re-read the host list — PCs may also have been unblocked.
async function onAccessChanged(denied) {
  denied.forEach(dropBlockedHost);
  await loadLabels();
  await refreshHosts();
}

function dropBlockedHost(hostId) {
  delete viewersByHost[hostId];
  if (window.MTWall && window.MTWall.dropHost) window.MTWall.dropHost(hostId);
  if (viewingHostId === hostId) {
    viewingHostId = null;
    $("screen").classList.add("hidden");
    $("screenHint").classList.remove("hidden");
    $("screenHint").textContent = "Access to this workstation was withdrawn.";
    $("stopBtn").disabled = true; $("recBtn").disabled = true; $("fsBtn").disabled = true;
    $("viewStatus").textContent = ""; $("viewers").textContent = "";
  }
  if (selectedHostId === hostId) { selectedHostId = null; $("viewBtn").disabled = true; }
}

function paintViewers() {
  const box = $("viewers");
  if (!viewingHostId) { box.textContent = ""; return; }
  const list = viewersOf(viewingHostId);
  const others = list.filter((v) => v.toLowerCase() !== currentUser().toLowerCase());
  if (!others.length) { box.innerHTML = "👁 only you"; return; }
  box.innerHTML = `👁 <b>${list.length} watching</b> — ${others.map(esc).join(", ")} and you`;
}

let lastFrameUtc = 0, lastFrameKey = "";
// ---- Live screen rendering (canvas) ----
// Both paths paint the same canvas: legacy whole-screen JPEGs (agents < 1.1) and tile deltas
// (agents >= 1.1). Tiles let a static trading screen send only the cells that changed, so updates
// arrive far sooner while staying sharp.
function screenCanvas() { return $("screen"); }
function fitCanvas(w, h) {
  const c = screenCanvas();
  if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
}
function showScreen() { $("screenHint").classList.add("hidden"); screenCanvas().classList.remove("hidden"); }

// Legacy whole-screen frame.
function onFrame(f) {
  if (f.hostId !== viewingHostId) return;
  if (f.monitorCount !== knownMonitors) populateMonitors(f.monitorCount);
  if (f.monitorIndex !== selectedMonitor) return;
  // Whole frames can arrive out of order after a transport hiccup — never paint an older one over
  // a newer one. (Deltas are not dropped this way; SignalR delivers them in order and a keyframe
  // recovers any gap.)
  const key = f.hostId + "#" + f.monitorIndex;
  const ts = Date.parse(f.timestampUtc) || 0;
  if (key === lastFrameKey && ts && ts < lastFrameUtc) return;
  lastFrameKey = key;
  lastFrameUtc = ts;
  showScreen();
  const img = new Image();
  img.onload = () => { fitCanvas(f.width, f.height); screenCanvas().getContext("2d").drawImage(img, 0, 0, f.width, f.height); };
  img.src = "data:image/jpeg;base64," + f.jpegBytes;   // SignalR sends byte[] as base64
  $("viewStatus").textContent = f.width + "×" + f.height;
}

// Tile update — a keyframe (all cells, resets the canvas) or a delta (changed cells only).
function onTiles(f) {
  if (f.hostId !== viewingHostId) return;
  if (f.monitorCount !== knownMonitors) populateMonitors(f.monitorCount);
  if (f.monitorIndex !== selectedMonitor) return;
  showScreen();
  const ctx = screenCanvas().getContext("2d");
  if (f.keyframe) { fitCanvas(f.frameWidth, f.frameHeight); $("viewStatus").textContent = f.frameWidth + "×" + f.frameHeight; }
  for (const t of f.tiles) {
    const col = t.index % f.cols, row = (t.index / f.cols) | 0;
    const x = col * f.tileSize, y = row * f.tileSize;
    const img = new Image();
    img.onload = () => ctx.drawImage(img, x, y);
    img.src = "data:image/jpeg;base64," + t.jpegBytes;
  }
}
function populateMonitors(count) {
  knownMonitors = count;
  const sel = $("monitor");
  const prev = selectedMonitor;
  sel.innerHTML = "";
  for (let i = 0; i < count; i++) { const o = document.createElement("option"); o.value = i; o.textContent = "#" + (i + 1); sel.appendChild(o); }
  sel.value = prev < count ? prev : 0;
}

// ---------- Update agent ----------
$("updateBtn").addEventListener("click", async () => {
  const h = hosts.find((x) => x.hostId === selectedHostId);
  if (!h) return;
  if (!confirm(`Update the Manager Tool agent on “${h.machineName}”?

The agent downloads the current installer and reinstalls itself silently. Monitoring drops for a few seconds.`)) return;
  await hub.invoke("RemoteUpdate", selectedHostId);
  alert(`Update command sent to “${h.machineName}”.`);
});

// ---------- Remove agent ----------
$("removeBtn").addEventListener("click", async () => {
  const h = hosts.find((x) => x.hostId === selectedHostId);
  if (!h) return;
  if (!confirm(`Permanently uninstall the Manager Tool agent from “${h.machineName}”?\n\nThis stops monitoring and deletes the agent from that PC. It cannot be undone remotely.`)) return;
  await hub.invoke("RemoteUninstall", selectedHostId);
  alert(`Uninstall command sent to “${h.machineName}”.`);
});

// ---------- Alerts ----------
$("alertsBtn").addEventListener("click", () => { $("alertsDrawer").classList.add("open"); loadAlerts(); });
$("alertsClose").addEventListener("click", () => $("alertsDrawer").classList.remove("open"));
$("ackAllBtn").addEventListener("click", async () => {
  const btn = $("ackAllBtn");
  btn.disabled = true;
  await api("/api/alerts/ack-all", { method: "POST" });   // acknowledge every open alert in one call
  unack = 0; paintBadge();                                 // clear the header badge
  await loadAlerts();                                      // repaint: rows now show acknowledged, no ack buttons
  btn.disabled = false;
});

async function loadAlerts() {
  const list = await apiJson("/api/alerts?limit=200");
  const box = $("alerts");
  box.innerHTML = "";
  if (!list || list.length === 0) { box.innerHTML = `<div class="alert"><div class="body" style="color:var(--ink3)">No alerts.</div></div>`; return; }
  list.forEach((a) => box.appendChild(alertRow(a)));
}
function alertRow(a) {
  const el = document.createElement("div");
  el.className = "alert";
  el.innerHTML = `<span class="sev"></span><div class="body"><div class="msg"></div><div class="meta"></div></div>`;
  el.querySelector(".sev").className = "sev " + a.severity;
  el.querySelector(".sev").textContent = a.severity.toUpperCase();
  el.querySelector(".msg").textContent = a.machineName + " — " + a.message;
  el.querySelector(".meta").textContent = new Date(a.timestampUtc).toLocaleString();
  if (!a.acknowledged) {
    const b = document.createElement("button");
    b.className = "ackbtn"; b.textContent = "Acknowledge";
    b.addEventListener("click", async () => { await api(`/api/alerts/${a.id}/ack`, { method: "POST" }); b.remove(); unack = Math.max(0, unack - 1); paintBadge(); });
    el.appendChild(b);
  }
  return el;
}
function onLiveAlert(a) {
  if (!a.acknowledged) { unack++; paintBadge(); }
  if ($("alertsDrawer").classList.contains("open")) $("alerts").insertBefore(alertRow(a), $("alerts").firstChild);
}
async function refreshAlertBadge() {
  const list = await apiJson("/api/alerts?acknowledged=false&limit=500");
  unack = list ? list.length : 0;
  paintBadge();
}
function paintBadge() { $("alertBadge").textContent = unack > 0 ? unack : ""; }

// ---------- View helpers ----------
function openView(id) { $(id).classList.add("open"); }
function closeView(id) { $(id).classList.remove("open"); }
function fillHostSelect(sel) {
  const cur = sel.value;
  sel.innerHTML = '<option value="">All workstations</option>';
  hosts.forEach((h) => { const o = document.createElement("option"); o.value = h.hostId; o.textContent = hostName(h); sel.appendChild(o); });
  sel.value = cur;
}
function isoStart(v) { return v ? new Date(v + "T00:00:00").toISOString() : null; }
function isoEnd(v) { return v ? new Date(v + "T23:59:59").toISOString() : null; }

// ---------- Reports ----------
$("reportsBtn").addEventListener("click", () => { fillHostSelect($("repHost")); openView("reportsView"); runReport(); });
$("reportsClose").addEventListener("click", () => closeView("reportsView"));
$("repRun").addEventListener("click", runReport);

function repQuery() {
  const p = [];
  if ($("repHost").value) p.push("host=" + encodeURIComponent($("repHost").value));
  const f = isoStart($("repFrom").value), t = isoEnd($("repTo").value);
  if (f) p.push("from=" + encodeURIComponent(f));
  if (t) p.push("to=" + encodeURIComponent(t));
  return p.length ? "?" + p.join("&") : "";
}

async function runReport() {
  const d = await apiJson("/api/reports/summary" + repQuery());
  if (!d) return;
  const total = d.totalEvents || 0;
  const c = d.byClassification || {};
  const sum = (c.productive || 0) + (c.unproductive || 0) + (c.neutral || 0) || 1;
  const pct = (n) => Math.round((n / sum) * 100);
  $("repKpis").innerHTML =
    kpi(total.toLocaleString(), "activity events") +
    kpi(pct(c.productive) + "%", "productive") +
    kpi(pct(c.unproductive) + "%", "unproductive") +
    kpi(pct(c.neutral) + "%", "neutral");
  $("repApps").innerHTML = bars(d.topApps || []);
  $("repUrls").innerHTML = bars(d.topUrls || []);
}
function kpi(v, l) { return `<div class="kpi"><div class="v">${v}</div><div class="l">${l}</div></div>`; }
function bars(rows) {
  if (!rows.length) return `<div style="color:var(--ink3);font-size:.85rem">No data in range.</div>`;
  const max = Math.max(...rows.map((r) => r.count)) || 1;
  return rows.map((r) => {
    const cls = r.classification || "neutral";
    const nm = (r.name || "(none)").replace(/</g, "&lt;");
    return `<div class="brow"><span class="nm" title="${nm}">${nm}</span><span class="track"><i class="${cls}" style="width:${Math.round((r.count / max) * 100)}%"></i></span><span class="ct">${r.count}</span></div>`;
  }).join("");
}
$("repExport").addEventListener("click", async () => {
  const r = await api("/api/reports/export" + repQuery());
  if (!r.ok) return;
  const blob = await r.blob(), url = URL.createObjectURL(blob);
  const a = document.createElement("a"); a.href = url; a.download = "managertool-activity.csv"; document.body.appendChild(a); a.click(); a.remove();
  URL.revokeObjectURL(url);
});

// ---------- Audit ----------
$("auditBtn").addEventListener("click", () => { openView("auditView"); loadAudit(); });
$("auditClose").addEventListener("click", () => closeView("auditView"));
$("auditRefresh").addEventListener("click", loadAudit);
async function loadAudit() {
  const rows = await apiJson("/api/audit?limit=300");
  const box = $("auditRows"); box.innerHTML = "";
  if (!rows || !rows.length) { box.innerHTML = `<tr><td colspan="5" style="color:var(--ink3)">No audit entries.</td></tr>`; return; }
  rows.forEach((a) => {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td style="white-space:nowrap;font-family:var(--mono);font-size:.76rem">${new Date(a.timestampUtc).toLocaleString()}</td>
      <td>${esc(a.actor)}</td><td><span class="act">${esc(a.action)}</span></td>
      <td>${esc(a.targetHost || "—")}</td><td>${esc(a.detail)}</td>`;
    box.appendChild(tr);
  });
}
function esc(s) { return (s || "").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }

// ---------- Recordings + playback ----------
let recFrames = [], recIdx = 0, recTimer = null, recCache = {}, recCtx = null;
$("recordingsBtn").addEventListener("click", () => { fillHostSelect($("recHost")); openView("recordingsView"); showSessions(); loadRecordings(); });
$("recordingsClose").addEventListener("click", () => { stopPlay(); closeView("recordingsView"); });
$("recRefresh").addEventListener("click", loadRecordings);
$("recHost").addEventListener("change", loadRecordings);
$("recBack").addEventListener("click", () => { stopPlay(); showSessions(); });

function showSessions() { $("recList").classList.remove("hidden"); $("recPlayer").classList.add("hidden"); $("recBack").style.display = "none"; }
async function loadRecordings() {
  const host = $("recHost").value;
  const list = await apiJson("/api/recordings" + (host ? "?host=" + encodeURIComponent(host) : ""));
  const box = $("recList"); box.innerHTML = "";
  if (!list || !list.length) { box.innerHTML = `<div style="color:var(--ink3)">No recordings yet. Record a session from the live screen view.</div>`; return; }
  list.forEach((s) => {
    const el = document.createElement("div"); el.className = "sess";
    const dur = s.endUtc ? Math.round((new Date(s.endUtc) - new Date(s.startUtc)) / 1000) + "s" : "in progress";
    el.innerHTML = `<div style="flex:1"><div class="mn">${esc(s.hostId)}</div><div class="meta">${new Date(s.startUtc).toLocaleString()} · ${s.frameCount} frames · ${dur}${s.complete ? "" : " · ●"}</div></div><span style="color:var(--accent)">▶ Play</span>`;
    el.onclick = () => openSession(s.hostId, s.sessionId);
    box.appendChild(el);
  });
}
async function openSession(host, session) {
  recCtx = { host, session }; recCache = {}; recIdx = 0;
  recFrames = await apiJson(`/api/recordings/${encodeURIComponent(host)}/${encodeURIComponent(session)}/frames`) || [];
  $("recList").classList.add("hidden"); $("recPlayer").classList.remove("hidden"); $("recBack").style.display = "";
  $("recSeek").max = Math.max(0, recFrames.length - 1); $("recSeek").value = 0;
  if (recFrames.length) showRecFrame(0); else $("recPos").textContent = "0 / 0";
}
async function frameUrl(i) {
  if (recCache[i]) return recCache[i];
  const r = await api(`/api/recordings/${encodeURIComponent(recCtx.host)}/${encodeURIComponent(recCtx.session)}/frame/${encodeURIComponent(recFrames[i])}`);
  if (!r.ok) return null;
  const u = URL.createObjectURL(await r.blob()); recCache[i] = u; return u;
}
async function showRecFrame(i) {
  if (i < 0 || i >= recFrames.length) return;
  recIdx = i; $("recSeek").value = i; $("recPos").textContent = `${i + 1} / ${recFrames.length}`;
  const u = await frameUrl(i); if (u) $("recFrame").src = u;
}
$("recSeek").addEventListener("input", () => { stopPlay(); showRecFrame(parseInt($("recSeek").value, 10)); });
$("recPlay").addEventListener("click", () => { if (recTimer) stopPlay(); else startPlay(); });
function startPlay() {
  if (!recFrames.length) return;
  $("recPlay").textContent = "⏸ Pause";
  recTimer = setInterval(() => {
    if (recIdx + 1 >= recFrames.length) { stopPlay(); return; }
    showRecFrame(recIdx + 1);
  }, 250);  // ~4 fps playback
}
function stopPlay() { if (recTimer) { clearInterval(recTimer); recTimer = null; } $("recPlay").textContent = "▶ Play"; }

// ---------- Resume session if token present ----------
if (token) { startApp().catch(() => logout()); }


// ---------- Bridge for the multi-PC Wall module (wall.js). Additive; does not
// alter single-view behaviour. Exposes just enough for the wall to drive viewing. ----------
window.MT = {
  invoke: (method, ...args) => (hub ? hub.invoke(method, ...args) : Promise.resolve()),
  getHosts: () => hosts.slice(),
  labelFor: (id) => (hostLabels[id] || (hosts.find(h => h.hostId === id) || {}).machineName || id),
  viewersOf: (id) => viewersOf(id),
};
