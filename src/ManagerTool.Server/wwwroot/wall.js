// ============================================================================
//  Multi-PC Wall — view several hosts at once, and pop any one out into its own
//  window (drag to a second monitor). Additive module: the single-screen view in
//  app.js is untouched. One hub connection (owned by app.js) feeds everything;
//  pop-out windows are dumb renderers fed over a same-origin BroadcastChannel, so
//  extra monitors cost zero extra server bandwidth.
//
//  Viewing lifecycle is reference-counted: a grid tile counts as one viewer, each
//  open pop-out counts as one. StartViewing fires on 0->1, StopViewing on ->0, so
//  the agent streams a host exactly once no matter how many places show it.
// ============================================================================
(function () {
  const $ = (id) => document.getElementById(id);
  const chan = ("BroadcastChannel" in window) ? new BroadcastChannel("mt-wall") : null;

  const want = new Map();        // hostId -> viewer refcount (tiles + pop-outs)
  const tiles = new Map();       // hostId -> { img, el }
  const popoutHosts = new Map(); // hostId -> count of open pop-outs (subset of want)
  let wallOpen = false;

  function ensureViewing(hostId) {
    const n = (want.get(hostId) || 0) + 1;
    want.set(hostId, n);
    if (n === 1) window.MT.invoke("StartViewing", hostId);
  }
  function releaseViewing(hostId) {
    const n = (want.get(hostId) || 0) - 1;
    if (n <= 0) { want.delete(hostId); window.MT.invoke("StopViewing", hostId); }
    else want.set(hostId, n);
  }

  // Draw a whole JPEG onto a thumbnail canvas, sizing its buffer to the frame.
  function paintFull(canvas, base64, w, h) {
    const img = new Image();
    img.onload = () => {
      if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
      canvas.getContext("2d").drawImage(img, 0, 0, w, h);
    };
    img.src = "data:image/jpeg;base64," + base64;
  }
  // Draw a tile update onto a thumbnail canvas (keyframe resizes; delta paints changed cells).
  function paintTiles(canvas, f) {
    const ctx = canvas.getContext("2d");
    if (f.keyframe && (canvas.width !== f.frameWidth || canvas.height !== f.frameHeight)) {
      canvas.width = f.frameWidth; canvas.height = f.frameHeight;
    }
    for (const t of f.tiles) {
      const col = t.index % f.cols, row = (t.index / f.cols) | 0;
      const x = col * f.tileSize, y = row * f.tileSize;
      const img = new Image();
      img.onload = () => ctx.drawImage(img, x, y);
      img.src = "data:image/jpeg;base64," + t.jpegBytes;
    }
  }

  // ---- Frame fan-out: called by app.js for every FrameReceived (legacy whole-screen) ----
  function onFrame(f) {
    const t = tiles.get(f.hostId);
    if (t && f.monitorIndex === 0) {           // grid shows primary monitor
      paintFull(t.img, f.jpegBytes, f.width, f.height);
      t.img.classList.remove("mt-blank");
    }
    if (chan && popoutHosts.get(f.hostId)) {
      chan.postMessage({ t: "frame", host: f.hostId, jpeg: f.jpegBytes, w: f.width, h: f.height, mi: f.monitorIndex });
    }
  }

  // ---- Tile fan-out: called by app.js for every TilesReceived (agents >= 1.1) ----
  function onTiles(f) {
    const t = tiles.get(f.hostId);
    if (t && f.monitorIndex === 0) {
      paintTiles(t.img, f);
      t.img.classList.remove("mt-blank");
    }
    if (chan && popoutHosts.get(f.hostId)) {
      chan.postMessage({ t: "tiles", host: f.hostId, frame: f });
    }
  }
  // ---- Watcher badge: show when a colleague is on the same host ----
  function onViewers(hostId, list) {
    const t = tiles.get(hostId);
    if (!t) return;
    const others = (list || []).length - 1;
    t.badge.textContent = others > 0 ? "👁 +" + others : "";
    t.badge.title = others > 0 ? "Also watching: " + (list || []).join(", ") : "";
  }

  // Access to this host was withdrawn — pull its tile without calling StopViewing, since the
  // server has already released our hold and would reject the call.
  function dropHost(hostId) {
    const t = tiles.get(hostId);
    if (t) { t.el.remove(); tiles.delete(hostId); relayout(); syncPicker(); }
    want.delete(hostId);
    popoutHosts.delete(hostId);
    if (chan) chan.postMessage({ t: "revoked", host: hostId });
  }

  // After a hub reconnect the per-host group memberships are gone; re-assert StartViewing for
  // every host the wall is still showing so its tiles resume instead of freezing. The refcount in
  // `want` is already correct — we only need to re-issue the subscription, not touch the count.
  function resubscribe() {
    for (const hostId of want.keys()) window.MT.invoke("StartViewing", hostId);
  }

  window.MTWall = { onFrame, onTiles, onViewers, dropHost, resubscribe };

  // ---- Pop-out coordination (messages from view.html windows) ----
  if (chan) chan.onmessage = (e) => {
    const m = e.data || {};
    if (m.t === "want") {
      popoutHosts.set(m.host, (popoutHosts.get(m.host) || 0) + 1);
      ensureViewing(m.host);
    } else if (m.t === "unwant") {
      const n = (popoutHosts.get(m.host) || 0) - 1;
      if (n <= 0) popoutHosts.delete(m.host); else popoutHosts.set(m.host, n);
      releaseViewing(m.host);
    }
  };

  function popOut(hostId) {
    const label = window.MT.labelFor(hostId);
    const url = "view.html#h=" + encodeURIComponent(hostId) + "&label=" + encodeURIComponent(label);
    window.open(url, "mtview_" + hostId, "width=1000,height=640,menubar=no,toolbar=no,location=no,status=no");
    // view.html announces itself with a 'want' over the channel once loaded.
  }

  // ---- Tile management ----
  function addTile(hostId) {
    if (tiles.has(hostId)) return;
    const el = document.createElement("div");
    el.className = "mt-tile";
    el.innerHTML =
      '<div class="mt-tilebar"><span class="mt-tilelabel"></span>' +
      '<span class="mt-watchers"></span>' +
      '<span style="flex:1"></span>' +
      '<button class="mt-pop" title="Open in its own window (drag to another monitor)">⧉ Pop out</button>' +
      '<button class="mt-close" title="Remove from wall">✕</button></div>' +
      '<div class="mt-tilebody"><canvas class="mt-img mt-blank" aria-label="live screen"></canvas></div>';
    el.querySelector(".mt-tilelabel").textContent = window.MT.labelFor(hostId);
    el.querySelector(".mt-pop").addEventListener("click", () => popOut(hostId));
    el.querySelector(".mt-close").addEventListener("click", () => removeTile(hostId));
    $("mt-grid").appendChild(el);
    tiles.set(hostId, { img: el.querySelector(".mt-img"), badge: el.querySelector(".mt-watchers"), el });
    onViewers(hostId, window.MT.viewersOf(hostId));
    ensureViewing(hostId);
    relayout();
    syncPicker();
  }
  function removeTile(hostId) {
    const t = tiles.get(hostId);
    if (!t) return;
    t.el.remove();
    tiles.delete(hostId);
    releaseViewing(hostId);
    relayout();
    syncPicker();
  }
  function relayout() {
    const n = tiles.size || 1;
    const cols = n <= 1 ? 1 : n <= 4 ? 2 : 3;   // 1,2x2, then 3-wide up to 6
    $("mt-grid").style.gridTemplateColumns = "repeat(" + cols + ", 1fr)";
    $("mt-empty").style.display = tiles.size ? "none" : "";
  }

  // ---- Host picker (which online hosts are on the wall) ----
  function syncPicker() {
    const box = $("mt-picker");
    if (!box) return;
    const list = window.MT.getHosts();
    box.innerHTML = "";
    if (!list.length) { box.innerHTML = '<span style="color:var(--ink3);font-size:.8rem">No hosts online.</span>'; return; }
    list.forEach((h) => {
      const on = tiles.has(h.hostId);
      const b = document.createElement("button");
      b.className = "mt-pick" + (on ? " on" : "");
      b.textContent = (on ? "✓ " : "+ ") + window.MT.labelFor(h.hostId);
      b.title = h.machineName + " · " + h.userName;
      b.addEventListener("click", () => on ? removeTile(h.hostId) : addTile(h.hostId));
      box.appendChild(b);
    });
  }

  // ---- Open / close the wall overlay ----
  function openWall() {
    wallOpen = true;
    $("mt-wall").style.display = "flex";
    syncPicker();
    relayout();
  }
  function closeWall() {
    wallOpen = false;
    $("mt-wall").style.display = "none";
    // Tiles keep streaming only if you leave them; simplest + safest is to tear the wall
    // down so we never leave agents streaming to a hidden page.
    Array.from(tiles.keys()).forEach(removeTile);
  }

  // ---- Inject UI (button in the screenbar + the overlay) once DOM is ready ----
  function injectUI() {
    const bar = document.querySelector(".screenbar");
    if (bar && !$("mt-wallBtn")) {
      const btn = document.createElement("button");
      btn.id = "mt-wallBtn";
      btn.className = "btn";
      btn.style.cssText = "padding:6px 12px";
      btn.title = "Watch several PCs at once; pop any onto another monitor";
      btn.textContent = "▦ Wall";
      btn.addEventListener("click", openWall);
      bar.appendChild(btn);
    }
    if (!$("mt-wall")) {
      const ov = document.createElement("div");
      ov.id = "mt-wall";
      ov.innerHTML =
        '<div class="mt-head">' +
          '<strong style="font-size:.9rem">Wall — multiple PCs</strong>' +
          '<span style="color:var(--ink3);font-size:.78rem">Pick up to 6. Click a tile\'s “Pop out” to send it to another monitor.</span>' +
          '<div id="mt-picker" class="mt-picker"></div>' +
          '<span style="flex:1"></span>' +
          '<button id="mt-closeWall" class="btn ghost" style="padding:6px 12px">✕ Close wall</button>' +
        '</div>' +
        '<div id="mt-grid" class="mt-grid"></div>' +
        '<div id="mt-empty" style="margin:auto;color:var(--ink3)">Pick a workstation above to place it on the wall.</div>';
      document.body.appendChild(ov);
      $("mt-closeWall").addEventListener("click", closeWall);
    }
    injectStyle();
  }

  function injectStyle() {
    if ($("mt-style")) return;
    const css = document.createElement("style");
    css.id = "mt-style";
    css.textContent = `
      #mt-wall{position:fixed;inset:0;z-index:30;background:var(--bg,#0b0f14);display:none;flex-direction:column;padding:12px;gap:10px}
      .mt-head{display:flex;align-items:center;gap:12px;flex-wrap:wrap}
      .mt-picker{display:flex;gap:6px;flex-wrap:wrap}
      .mt-pick{font-size:.78rem;padding:3px 9px;border-radius:6px;border:1px solid var(--line,#2a3441);background:transparent;color:var(--ink,#dfe7ef);cursor:pointer}
      .mt-pick.on{background:var(--accent,#37b6c4);color:#00252b;border-color:transparent}
      .mt-grid{flex:1;display:grid;gap:8px;min-height:0}
      .mt-tile{display:flex;flex-direction:column;background:#000;border:1px solid var(--line,#2a3441);border-radius:8px;overflow:hidden;min-height:0}
      .mt-tilebar{display:flex;align-items:center;gap:6px;padding:4px 8px;background:var(--panel,#111821);font-size:.78rem}
      .mt-tilelabel{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .mt-watchers{font-size:.7rem;color:var(--accent,#37b6c4);white-space:nowrap}
      .mt-pop,.mt-close{background:none;border:1px solid var(--line,#2a3441);color:var(--ink2,#aeb9c6);border-radius:5px;font-size:.72rem;padding:2px 7px;cursor:pointer}
      .mt-pop:hover,.mt-close:hover{color:var(--ink,#fff);border-color:var(--accent,#37b6c4)}
      .mt-tilebody{flex:1;display:flex;align-items:center;justify-content:center;min-height:0;background:#000}
      .mt-img{max-width:100%;max-height:100%;object-fit:contain}
      .mt-img.mt-blank{opacity:.25}
    `;
    document.head.appendChild(css);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", injectUI);
  else injectUI();
})();
