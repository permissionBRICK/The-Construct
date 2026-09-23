/* Operator Line: control panel (per-VM operator window). */
(function () {
  "use strict";
  const { icon, D, esc, Palette, toast, dropdown } = window.OL;
  const S = window.OL.S;
  const win = document.getElementById("cpWin");
  if (!win) return;

  const TABS = [
    ["timeline", "Timeline", "t", "history"],
    ["agents", "Agents", "a", "agent"],
    ["forwards", "Forwards", "f", "forward"],
    ["projects", "Projects", "p", "folder"],
    ["usage", "Usage", "u", "dollar"],
    ["settings", "Settings", "s", "gear"],
    ["danger", "Danger", "d", "alert"],
  ];
  const P = {
    tab: "timeline", tl: "all", tlScope: "vm", sel: { timeline: ["forward", "f5173", "e2"], agents: ["agent", "claude"], forwards: ["forward", "f5173"], projects: ["project", "jarvis"], usage: ["day", 6], settings: ["setting", "vm.memoryGB"], danger: ["danger", "Reinstall"] },
    q: "", scope: "all", mod: {}, period: "daily", extraEvents: [],
  };

  const SETTINGS = [
    ["VM resources", "vm.memoryGB", "Memory (RAM)", "16 GB assigned · 11.2 GB in use", "num", 16, "restart", "GB"],
    ["VM resources", "vm.vcpus", "vCPUs", "a local VM gets all host cores unless set", "num", 8, "restart"],
    ["VM resources", "vm.diskGB", "Disk size", "146 GB · 88% used · grows on demand", "num", 146, "reinstall", "GB"],
    ["Idle policy", "idle.action", "When idle", "remote instances only · the host enforces it", "sel", "save after 60 min", "live", null, ["save after 60 min", "shut down after 60 min", "never"], true],
    ["Access & services", "services.serveWeb", "VS Code serve-web", "browser IDE on :8000", "bool", true, "reprovision"],
    ["Access & services", "services.vscodeTunnel", "VS Code tunnel", "needs a GitHub sign-in on first use", "bool", false, "reprovision"],
    ["Access & services", "services.smbShare", "SMB share", "\\\\agent-vm\\work", "bool", true, "reprovision"],
    ["Access & services", "services.smbDrive", "Map share to drive", "drive letter on this PC", "sel", "Z:", "reprovision", null, ["Z:", "Y:", "W:", "none"]],
    ["Access & services", "services.t3", "T3 Code", "web UI on :3773", "bool", true, "reprovision"],
    ["Access & services", "services.t3Https", "T3 HTTPS", "local CA, trusted on this PC", "bool", true, "reprovision"],
    ["Access & services", "services.t3Patched", "Patched T3 + Desktop", "much longer provision", "bool", false, "reprovision"],
    ["Access & services", "services.opencodeWatcher", "OpenCode watcher", "restarts serve :4096", "bool", true, "reprovision"],
    ["Access & services", "services.checkpoints", "Automatic checkpoints", "Hyper-V checkpoint before each reprovision", "bool", false, "reprovision"],
    ["Access & services", "services.claudeStreaming", "Claude partial streaming", "", "bool", true, "reprovision"],
    ["Coding agents", "agents.enabled", "Installed agents", "chosen at reprovision", "text", "claude, codex, opencode, t3", "reprovision"],
    ["Identity & credentials", "git.userName", "Git user name", "default: this PC's git identity", "text", "Dana A.", "reprovision"],
    ["Identity & credentials", "git.email", "Git email", "", "text", "dana@example.com", "reprovision"],
    ["Identity & credentials", "git.storeCredentials", "Store git credentials on VM", "plaintext in ~/.git-credentials", "bool", true, "reprovision"],
    ["Identity & credentials", "agent.password", "Agent user password", "fallback credential only", "text", "••••••••", "reprovision"],
    ["Voice", "mic.enabled", "Mic passthrough", "vm:8767 → this PC's mic, live only while recording", "bool", true, "live"],
    ["Voice", "mic.device", "Capture device", "", "sel", "Shure MV7", "live", null, ["Shure MV7", "Realtek Mic Array", "Windows default"]],
    ["Companion", "ui.theme", "Theme", "", "sel", "Graphite dark", "live", null, ["Graphite dark", "Light", "Follow Windows"]],
    ["Companion", "ui.hotkey", "Command bar hotkey", "global, works over any app", "text", "Win+Alt+C", "live"],
    ["Companion", "ui.trayClick", "Tray icon click", "", "sel", "Open popup", "live", null, ["Open popup", "Open command bar", "Open control panel"]],
    ["Companion", "ui.openWith", "Default Open target", "remembered from the last pick", "sel", "VS Code (Remote-SSH)", "live", null, ["VS Code (Remote-SSH)", "T3 Code", "serve-web", "Console"]],
    ["Companion", "ui.notify", "construct notify", "Windows toast plus the timeline", "sel", "Toast + timeline", "live", null, ["Toast + timeline", "Timeline only", "Off"]],
    ["Companion", "ui.startWithWindows", "Start with Windows", "", "bool", true, "live"],
    ["Instance", "instance.backend", "Backend", "read-only", "ro", "hyperv-local", "live"],
    ["Instance", "instance.vmName", "Hyper-V VM name", "read-only", "ro", "agent-vm", "live"],
    ["Instance", "instance.configRepo", "Config repo", "local git, synced", "ro", "~/.construct/config", "live"],
  ].map((r) => ({ g: r[0], k: r[1], l: r[2], d: r[3], type: r[4], v: r[5], scope: r[6], unit: r[7], opts: r[8], remoteOnly: r[9] }));
  const SCOPE_TXT = {
    live: "Takes effect immediately.",
    restart: "Saved now. Applies at the next full stop/start of the VM. Use Save & restart (1 UAC prompt on a local VM).",
    reprovision: "Saved now. Applies at the next Reprovision, which keeps all data.",
    reinstall: "Applies only when the VM is rebuilt (Reinstall or Redownload).",
  };

  /* ---------- skeleton ---------- */
  win.innerHTML = `
    <div class="win-title"><span class="cx">${icon("terminal")}</span><span>The Construct — <b id="cpTitle">agent-vm</b></span>
      <div class="win-ctl"><span>${icon("pause").replace('d="M6 4v8M10 4v8"', 'd="M3 8h10"')}</span><span><svg class="i" viewBox="0 0 16 16"><rect x="3.5" y="3.5" width="9" height="9"/></svg></span><span class="x">${icon("x")}</span></div></div>
    <div class="w-top">
      <button class="inst-btn" id="cpInst"></button>
      <div class="cmdbar" id="cpCmd" tabindex="0" role="button" aria-label="Open command palette">${icon("search")}<span class="ph">Search or run a command…  <span class="mono dim" style="font-size:11px">5173 · rep · ram 12 · reinstall</span></span><span class="kbds"><kbd>Ctrl</kbd><kbd>K</kbd></span></div>
      <span id="cpBadges" style="display:flex;gap:6px"></span>
      <button class="btn ghost sm" id="cpHost" title="Open host panel">${icon("server", "sm")}buildbox ${icon("ext", "sm")}</button>
    </div>
    <div class="w-tabs" role="tablist" id="cpTabs"></div>
    <div class="cp-grid">
      <nav class="rail" id="cpRail" aria-label="Instances"></nav>
      <div class="cp-content"><div class="vm-head" id="cpHead"></div><div id="cpViews" style="flex:1;min-height:0;display:flex;flex-direction:column"></div></div>
      <aside class="insp scroll" id="cpInsp" aria-label="Inspector"></aside>
    </div>
    <div class="w-status" id="cpStatus"></div>
    <div class="overlay" id="cpOverlay"><div class="pal-modal"><div class="pm-in" id="cpPmIn">${icon("search", "lg")}<input id="cpPalInput" placeholder="Search or run a command…" autocomplete="off" spellcheck="false"><kbd>Esc</kbd></div><div class="pm-list scroll" id="cpPalList"></div>
      <div class="pm-foot"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>↵</kbd> run</span><span><kbd>→</kbd> options</span><span><kbd>Backspace</kbd> back</span><span class="spacer"></span><span>destructive verbs ask for the instance name</span></div></div></div>
    <div class="toast-host" id="cpToast"></div>`;

  const $ = (s) => win.querySelector(s);
  const T = (m, k, u) => toast($("#cpToast"), m, k, u);

  /* ---------- palette ---------- */
  const env = {
    surface: "panel", toastHost: () => $("#cpToast"),
    openPanel: (tab, focus) => { closePal(); go(tab); if (focus) focusSetting(focus); },
    danger: (verb) => openDanger(verb),
    dangerDone: (verb) => {
      closePal();
      P.extraEvents.unshift({ id: "x" + Date.now(), t: "14:18", kind: "lifecycle", title: verb + " of " + S.active + " started", detail: "backup captured · waiting for UAC", entity: ["instance", S.active], acts: [], vm: S.active });
      go("timeline"); T(verb + " of " + S.active + " started · backup captured", "warn");
    },
  };
  const pal = Palette({
    input: $("#cpPalInput"), list: $("#cpPalList"), placeholder: "Search or run a command…",
    getCommands: () => window.OL.operatorCommands(env), dynamic: (q) => window.OL.dynamicCommands(q, env),
    onClose: () => closePal(),
    onState: (m) => $("#cpPmIn").classList.toggle("danger", m === "confirm"),
  });
  function openPal(q) { $("#cpOverlay").classList.add("on"); pal.set(q || ""); $("#cpPalInput").focus(); }
  function closePal() { $("#cpOverlay").classList.remove("on"); $("#cpPmIn").classList.remove("danger"); }
  function openDanger(verb) {
    $("#cpOverlay").classList.add("on");
    const map = { "Custom reinstall": "Clean-wipe reinstall", "Remove instance": "Remove" };
    const v = map[verb] || verb;
    pal.openConfirm({ confirm: window.OL.dangerConfirm(v, S.active, () => env.dangerDone(v)) });
    $("#cpPalInput").focus();
  }
  window.OL.panelDanger = (v) => { go("danger"); P.sel.danger = ["danger", v === "Clean-wipe reinstall" ? "Custom reinstall" : v === "Remove" ? "Remove instance" : v]; render(); openDanger(v); };
  window.OL.panelFocus = (f) => { if (f) setTimeout(() => focusSetting(f), 30); };
  window.OL.panelPalette = openPal;
  $("#cpOverlay").addEventListener("mousedown", (e) => { if (e.target.id === "cpOverlay") { pal.close(); } });
  $("#cpCmd").addEventListener("click", () => openPal());

  function focusSetting(f) {
    const map = { ram: "vm.memoryGB", cpu: "vm.vcpus", disk: "vm.diskGB", idle: "idle.action" };
    go("settings"); P.q = ""; P.sel.settings = ["setting", map[f] || f]; render();
    const row = win.querySelector('.set-r[data-k="' + (map[f] || f) + '"]'); if (row) row.scrollIntoView({ block: "center" });
  }

  function go(tab) { if (!TABS.find((t) => t[0] === tab)) return; P.tab = tab; if (location.hash.startsWith("#panel")) history.replaceState(null, "", "#panel/" + tab); render(); }
  window.OL.panelGo = go;

  /* ---------- render pieces ---------- */
  function dotFor(st) { return `<span class="dot ${st === "running" ? "running" : st === "saved" ? "saved" : "off"}"></span>`; }

  function renderTop() {
    const a = S.active, st = S.vmState[a];
    $("#cpTitle").textContent = a;
    $("#cpInst").innerHTML = `${dotFor(st)}${a}<span class="meta">${st}</span>${icon("down", "sm")}`;
    $("#cpBadges").innerHTML = a === "agent-vm" ? (S.staged ? `<button class="tag acc" data-go="rep" style="border:0">${icon("clock", "sm")} reprovision staged</button>` : `<button class="tag warn" data-go="rep" style="border:0;cursor:pointer" title="Provisioned c21978b · installed b5c4348">${icon("cycle", "sm")} behind 2</button>`) + `<button class="tag info" data-go="upd" style="border:0;cursor:pointer">${icon("upd", "sm")} update 3a91f0e</button>` : "";
    $("#cpTabs").innerHTML = TABS.map(([id, l, k, ic]) => {
      const cnt = id === "agents" ? '<span class="cnt">1↑</span>' : id === "forwards" ? '<span class="cnt">' + D.forwards.filter((f) => f.vm === a && f.state === "open" && !S.closed.has(f.id)).length + "</span>" : id === "timeline" ? (unreadCount() ? '<span class="cnt" style="color:var(--info)">' + unreadCount() + "</span>" : "") : "";
      return `<button class="w-tab${id === "danger" ? " danger" : ""}" role="tab" aria-selected="${P.tab === id}" data-tab="${id}">${icon(ic, "sm")}${l}${cnt}<span class="kbds"><kbd>g</kbd><kbd>${k}</kbd></span></button>`;
    }).join("") + `<span class="right">${icon("clock", "sm")} sampled 8 s ago</span>`;
  }
  function unreadCount() { return Object.values(D.events).flat().filter((e) => e.unread && !S.acked.has(e.id)).length; }

  function renderRail() {
    const r = $("#cpRail");
    const item = (i) => {
      const st = S.vmState[i.id];
      const sub = i.parent ? "lease " + i.lease + " · " + i.ram + " GB" : st === "running" ? "up " + i.uptime + " · " + i.ramUsed + "/" + i.ram + " GB" : st === "saved" ? "saved " + i.savedAgo + " · buildbox" : "stopped";
      const flag = i.behind ? '<span class="flag tag warn" style="height:15px;padding:0 4px">↓' + i.behind + "</span>" : "";
      return `<div class="rail-i${i.parent ? " child" : ""}${S.active === i.id ? " active" : ""}" data-inst="${i.id}" tabindex="0">${dotFor(st)}<div style="min-width:0"><div class="nm">${i.id}</div><div class="s">${sub}</div></div>${flag}</div>`;
    };
    const vm = window.OL.inst;
    r.innerHTML = `<div class="rail-h">Instances <span class="mono" style="letter-spacing:0">3</span><button class="btn sm icon ghost" title="New instance">${icon("plus", "sm")}</button></div>
      ${item(vm("agent-vm"))}${item(vm("win-test"))}${item(vm("dev-2"))}
      <div class="rail-h" style="margin-top:8px">Jump <span class="spacer"></span></div>
      <div class="rail-i" data-go="notify">${icon("bell", "sm")}<div class="nm" style="font-weight:500">Unread notify</div><span class="mono dim" style="font-size:11px">${unreadCount()}</span></div>
      <div class="rail-i" data-go="behind">${icon("cycle", "sm")}<div class="nm" style="font-weight:500">To reprovision</div><span class="mono dim" style="font-size:11px">1</span></div>
      <div class="rail-foot">
        <div class="rf">${icon("mic")}<span>Mic ${S.mic ? "armed · MV7" : "off"}</span><span class="spacer"></span><button class="toggle" id="cpMic" role="switch" aria-checked="${S.mic}" aria-label="Mic passthrough"></button></div>
        <div class="rf">${icon("dollar")}<span>Today</span><span class="spacer"></span><b>$4.12</b></div>
        <div class="rf">${icon("cycle")}<span>Config synced 3 min ago</span></div>
      </div>`;
  }

  function renderHead() {
    const a = S.active, i = window.OL.inst(a), st = S.vmState[a];
    let facts, acts;
    if (a === "agent-vm") {
      facts = `<span>${i.backend}</span><span>${icon("cpu", "sm")}8 vCPU</span><span>11.2/16 GB</span><span class="w">${icon("disk", "sm")}disk 88%</span><span>up 3h 12m</span><span>${icon("commit", "sm")}c21978b</span>`;
    } else if (a === "dev-2") {
      facts = `<span>hyperv-remote · buildbox.example.local:7462</span><span>4 vCPU</span><span>12 GB</span><span>saved 41 min ago</span>`;
    } else {
      facts = `<span>child of agent-vm</span><span>Windows 11 eval</span><span>4 GB</span><span>${icon("clock", "sm")}lease 5h 20m</span>`;
    }
    if (st === "running") acts = `<span class="split"><button class="btn primary" data-do="open">${icon("code")}Open in VS Code</button><button class="btn primary" data-do="opendd">${icon("down", "sm")}</button></span><button class="btn" data-do="power">${icon("stop")}Stop</button>${a === "agent-vm" ? `<button class="btn" data-do="rep">${icon("cycle")}Reprovision…</button>` : ""}`;
    else acts = `<button class="btn primary" data-do="power">${icon("play")}${st === "saved" ? "Resume" : "Start"} ${a}</button>`;
    $("#cpHead").innerHTML = `${dotFor(st)}<span class="nm">${a}</span><span class="facts">${facts}</span><span class="acts">${acts}</span>`;
  }

  /* ---------- views ---------- */
  function allEvents() {
    const base = P.tlScope === "vm" ? (D.events[S.active] || []).map((e) => Object.assign({ vm: e.vm || S.active }, e)) :
      Object.entries(D.events).flatMap(([vm, list]) => list.map((e) => Object.assign({ vm: e.vm || vm }, e))).sort((x, y) => { const k = (e) => (/^\d/.test(e.t) ? e.t : "00:00"); return k(y) > k(x) ? 1 : k(y) < k(x) ? -1 : 0; });
    return P.extraEvents.filter((e) => P.tlScope === "all" || e.vm === S.active).concat(base);
  }
  function viewTimeline() {
    const evs = allEvents();
    const kinds = [["all", "All", "1"], ["agent", "Agents", "2"], ["forward", "Forwards", "3"], ["lifecycle", "Lifecycle", "4"], ["notify", "Notify", "5"]];
    const count = (k) => evs.filter((e) => k === "all" || e.kind === k || (k === "lifecycle" && e.kind === "update")).length;
    const list = evs.filter((e) => P.tl === "all" || e.kind === P.tl || (P.tl === "lifecycle" && e.kind === "update") || (P.tl === "unread" && e.unread && !S.acked.has(e.id)));
    const kicon = { forward: "forward", notify: "bell", agent: "agent", lifecycle: "vm", update: "upd" };
    const ktag = { forward: "acc", notify: "info", agent: "ok", lifecycle: "saved", update: "warn" };
    let rows = "";
    let lastDay = null;
    list.forEach((e) => {
      const day = /^\d/.test(e.t) ? "Today · Wed 23 Sep" : "Earlier";
      if (day !== lastDay) { rows += `<div class="day">${day}</div>`; lastDay = day; }
      const acked = S.acked.has(e.id), closed = e.entity[0] === "forward" && S.closed.has(e.entity[1]);
      const sel = P.sel.timeline && P.sel.timeline[2] === e.id;
      const acts = acked ? '<span class="dim" style="font-size:11px">acked</span>' : closed ? '<span class="dim" style="font-size:11px">closed</span>' : (e.acts || []).map((x) => `<button class="btn sm ${x[2] ? "" : "ghost"}" data-ev="${e.id}" data-a="${x[0]}">${icon(x[1], "sm")}${x[0]}</button>`).join("");
      rows += `<div class="tl-row${sel ? " sel" : ""}${e.unread && !acked ? " unread" : ""}${acked || closed ? " done" : ""}" data-e="${e.id}"><span class="t">${e.t}</span><span><span class="tag ${e.level === "error" ? "err" : ktag[e.kind]}">${icon(kicon[e.kind], "sm")}${e.kind}</span></span>
        <span class="m">${e.title}<span class="by">${e.detail}${P.tlScope === "all" || e.vm !== S.active ? " · " + e.vm : ""}</span></span><span class="acts">${acts}</span></div>`;
    });
    if (!list.length) rows = '<div class="pal-empty">No events for this filter.</div>';
    return `<div class="tl"><div class="tl-filters">
        <div class="fh">Show</div>
        ${kinds.map(([k, l, n]) => `<button class="tl-f" aria-pressed="${P.tl === k}" data-tl="${k}">${l}<span class="n">${count(k)}</span><kbd>${n}</kbd></button>`).join("")}
        <button class="tl-f" aria-pressed="${P.tl === "unread"}" data-tl="unread">${icon("bell", "sm")}Unread<span class="n">${unreadCount()}</span></button>
        <div class="fh" style="margin-top:8px">Scope</div>
        <button class="tl-f" aria-pressed="${P.tlScope === "vm"}" data-scope="vm">${S.active}</button>
        <button class="tl-f" aria-pressed="${P.tlScope === "all"}" data-scope="all">All instances</button>
        <div class="fh" style="margin-top:8px">Source</div>
        <div style="padding:0 8px;font-size:11px;color:var(--fg-3);line-height:1.5">construct expose, construct notify, agent heartbeats, lifecycle and host jobs. Kept 7 days.</div>
      </div><div class="tl-log scroll">${rows}</div></div>`;
  }

  function viewAgents() {
    const running = S.vmState[S.active] === "running";
    return `<div class="view-bar"><span class="ttl">Coding agents</span><span class="dim">on ${S.active} · heartbeat 8 s ago</span><span class="spacer"></span><button class="btn" data-do="wake">${icon("bell")}Wake me when Claude is done</button><button class="btn primary" data-do="updall">${icon("download")}Update all <span class="mono" style="opacity:.8">1</span></button></div>
      <div class="tscroll scroll"><table class="grid"><thead><tr><th>Agent</th><th>Version</th><th>Update</th><th>Activity</th><th>Repo</th><th class="num">Tokens</th><th class="num">Today</th></tr></thead><tbody>
      ${D.agents.map((g) => {
        const d = !running ? "off" : g.state === "working" ? "busy" : g.state === "active" ? "running" : "off";
        const act = !running ? "paused with the VM" : g.state === "working" ? `<b>working</b> ${g.since} · “${g.turn}”` : g.turn;
        return `<tr class="row${P.sel.agents[1] === g.id ? " sel" : ""}" data-sel="agent:${g.id}"><td><span style="display:inline-flex;gap:8px;align-items:center"><span class="dot ${d}"></span><b style="font-weight:600">${g.name}</b></span><div class="sub" style="margin-left:16px">${g.extra}</div></td>
          <td class="mono">${g.ver}</td><td>${g.latest ? '<span class="tag info">' + g.latest + "</span>" : g.id === "t3" ? '<span class="dim">pinned build</span>' : '<span class="dim">up to date</span>'}</td>
          <td style="max-width:300px;overflow:hidden;text-overflow:ellipsis">${act}</td><td class="mono">${g.repo || "—"}</td><td class="num">${g.tokens}</td><td class="num">${g.today ? "$" + g.today.toFixed(2) : "—"}</td></tr>`;
      }).join("")}</tbody></table>
      <div class="insp-note" style="margin:14px">${icon("alert")}<span>Agent updates wait for the current turn. Which agents are installed is chosen at reprovision (<a class="linkish" data-go-set="agents.enabled">agents.enabled</a>).</span></div></div>`;
  }

  function viewForwards() {
    const fw = D.forwards;
    return `<div class="view-bar"><span class="ttl">Forwards &amp; tunnels</span><span class="dim">what agents opened with <span class="mono">construct expose</span></span><span class="spacer"></span><button class="btn" data-do="expose">${icon("plus")}Expose a port… <kbd>e</kbd></button></div>
      <div class="tscroll scroll"><table class="grid"><thead><tr><th>VM port</th><th>On this PC</th><th>Scope</th><th>State</th><th>Label</th><th>Opened by</th><th class="num">Age</th><th></th></tr></thead><tbody>
      ${fw.map((f) => {
        const closed = S.closed.has(f.id);
        return `<tr class="row${P.sel.forwards[1] === f.id ? " sel" : ""}" data-sel="forward:${f.id}" style="${closed ? "opacity:.5" : ""}"><td class="mono"><b>${f.vm}:${f.vmPort}</b></td><td class="mono">${f.local}${f.remap ? ' <span class="tag warn">remapped</span>' : ""}</td><td>${f.scope}</td>
          <td>${closed ? '<span class="tag">closed</span>' : f.state === "open" ? '<span class="tag ok">open</span>' : '<span class="tag">queued</span>'}</td><td>${f.label}</td><td>${f.by}</td><td class="num">${f.ago}</td>
          <td style="text-align:right">${closed ? "" : f.state === "open" ? `<button class="btn sm" data-fo="${f.id}">${icon("ext", "sm")}Open</button> <button class="btn sm ghost icon" data-fc="${f.id}" title="Close">${icon("x", "sm")}</button>` : '<span class="dim" style="font-size:11px">on resume</span>'}</td></tr>`;
      }).join("")}
      <tr><td colspan="8" style="height:28px;background:var(--bg-0);font-size:10.5px;font-weight:600;letter-spacing:.07em;text-transform:uppercase;color:var(--fg-3)">Reverse tunnels</td></tr>
      <tr class="row${P.sel.forwards[1] === "mic" ? " sel" : ""}" data-sel="forward:mic"><td class="mono"><b>vm:8767</b></td><td class="mono">mic · ${D.mic.device}</td><td>reverse</td><td>${S.mic ? '<span class="tag acc">armed</span>' : '<span class="tag">off</span>'}</td><td>mic passthrough for /voice</td><td>you</td><td class="num">—</td><td style="text-align:right"><button class="toggle" data-do="mic" role="switch" aria-checked="${S.mic}" aria-label="Mic passthrough"></button></td></tr>
      <tr class="row" data-sel="forward:ssh"><td class="mono"><b>vm:22</b></td><td class="mono">agent-vm.mshome.net</td><td>ssh</td><td><span class="tag ok">up</span></td><td>Remote-SSH, T3 HTTPS</td><td>system</td><td class="num">3h 12m</td><td></td></tr>
      </tbody></table></div>`;
  }

  function viewProjects() {
    return `<div class="proj-sync">${icon("cycle")}<span>Config sync: last synced <b>3 min ago</b> · <span style="color:var(--info)">1 new profile from the VM</span> (jarvis)</span><span class="spacer"></span><button class="btn sm" data-do="sync">${icon("cycle", "sm")}Sync now</button><button class="btn sm ghost" data-do="export">${icon("upload", "sm")}Export config</button><button class="btn sm ghost">${icon("folder", "sm")}Open folder</button></div>
      <div class="view-bar"><span class="ttl">Project profiles</span><span class="tag scope-reprovision">next reprovision</span><span class="dim">${D.projects.filter((p) => p.on).length} of ${D.projects.length} selected</span><span class="spacer"></span><button class="btn">${icon("plus")}Add project</button></div>
      <div class="tscroll scroll"><table class="grid"><thead><tr><th class="cb"></th><th>Profile</th><th class="num">Repos</th><th>Runtimes</th><th class="num">MCP</th><th>Status</th></tr></thead><tbody>
      ${D.projects.map((p) => `<tr class="row${P.sel.projects[1] === p.id ? " sel" : ""}" data-sel="project:${p.id}"><td class="cb"><input type="checkbox" class="cbx" data-proj="${p.id}" ${p.on ? "checked" : ""} aria-label="select ${p.id}"></td><td class="mono"><b>${p.id}</b></td><td class="num">${p.repos}</td><td>${p.runtimes}</td><td class="num">${p.mcp}</td><td>${p.status === "new" ? '<span class="tag info">new · auto-discovered</span>' : p.on ? '<span class="tag ok">selected</span>' : '<span class="tag">skipped</span>'}</td></tr>`).join("")}
      <tr><td colspan="6" style="height:28px;background:var(--bg-0);font-size:10.5px;font-weight:600;letter-spacing:.07em;text-transform:uppercase;color:var(--fg-3)">Remote config repos</td></tr>
      <tr class="row" data-sel="remote:team"><td class="cb"></td><td class="mono">team-config</td><td colspan="2" class="mono dim">git@gitgudlab:construct/config.git</td><td></td><td><span class="tag ok">pulled 3 min ago</span></td></tr>
      <tr class="row" data-sel="remote:personal"><td class="cb"></td><td class="mono">personal</td><td colspan="2" class="mono dim">github.com/dana/construct-config</td><td></td><td><span class="tag">published</span></td></tr>
      </tbody></table></div>`;
  }

  function viewUsage() {
    const days = D.usage.days, max = 15, W = 540, H = 150, bw = 46, gap = (W - bw * 7) / 6;
    const bars = days.map(([d, v], i) => {
      const h = (v / max) * (H - 30), x = i * (bw + gap), y = H - 18 - h;
      return `<g class="bar-h${i === 6 ? " today" : ""}${P.sel.usage[1] === i ? " sel" : ""}" data-day="${i}"><rect x="${x}" y="0" width="${bw}" height="${H - 18}" fill="transparent"/><rect class="b" x="${x}" y="${y}" width="${bw}" height="${h}" rx="3"/><text x="${x + bw / 2}" y="${y - 5}" text-anchor="middle" style="fill:var(--fg-2)">$${v.toFixed(1)}</text><text x="${x + bw / 2}" y="${H - 3}" text-anchor="middle">${d}${i === 6 ? " (so far)" : ""}</text></g>`;
    }).join("");
    const tot = D.usage.split.reduce((s, r) => s + r[1], 0);
    return `<div class="view-bar"><span class="ttl">Token usage &amp; cost</span><span class="dim">${S.active} · ccusage · sampled 2 min ago</span><span class="spacer"></span><span class="seg" id="cpPeriod">${["daily", "monthly", "total"].map((p) => `<button aria-pressed="${P.period === p}" data-per="${p}">${p}</button>`).join("")}</span><button class="btn" data-do="export-usage">${icon("download")}Export JSON</button></div>
      <div class="kpis"><div class="kpi"><span class="l">Today</span><span class="v">$4.12</span><span class="s">claude $3.40 · codex $0.58</span></div><div class="kpi"><span class="l">This month</span><span class="v">$86.30</span><span class="s">23 days · $3.75 per day</span></div><div class="kpi"><span class="l">All time</span><span class="v">$412.77</span><span class="s">since provisioning in May</span></div><div class="kpi"><span class="l">Last 7 days</span><span class="v">$66.40</span><span class="s">busiest: Sun $14.00</span></div></div>
      <div class="chart-box"><div class="ch">Daily cost, last 7 days<span class="dim" style="font-weight:400;font-size:11px">click a day</span></div><svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Daily cost bars">${bars}</svg></div>
      <div class="tscroll scroll"><table class="grid"><thead><tr><th>Agent</th><th>Share today</th><th class="num">Tokens</th><th class="num">Cost</th></tr></thead><tbody>
        ${D.usage.split.map((r) => `<tr class="row" data-sel="agent:${r[0] === "Claude Code" ? "claude" : r[0] === "Codex" ? "codex" : "opencode"}"><td>${r[0]}</td><td><span class="share"><span class="meter"><i style="width:${(r[1] / tot) * 100}%"></i></span><span class="mono dim" style="font-size:11px">${Math.round((r[1] / tot) * 100)}%</span></span></td><td class="num">${r[2]}</td><td class="num">$${r[1].toFixed(2)}</td></tr>`).join("")}
        <tr><td><b>Total</b></td><td></td><td class="num">2.32M</td><td class="num"><b>$4.12</b></td></tr></tbody></table>
        <div class="dim" style="padding:10px 14px;font-size:11px">Token counts are exact. Cost is an estimate from ccusage model pricing.</div></div>`;
  }

  function settingRows() {
    const q = P.q.trim().toLowerCase();
    let scopeQ = null, textQ = q;
    const m = q.match(/scope:(\w+)/); if (m) { scopeQ = m[1]; textQ = q.replace(m[0], "").trim(); }
    return SETTINGS.filter((s) => (P.scope === "all" || s.scope === P.scope) && (!scopeQ || s.scope.startsWith(scopeQ)) &&
      (!textQ || (s.k + " " + s.l + " " + s.d + " " + s.g).toLowerCase().includes(textQ)));
  }
  function valueOf(s) { return s.k in P.mod ? P.mod[s.k] : s.k === "mic.enabled" ? S.mic : s.v; }
  function control(s) {
    const v = valueOf(s);
    const na = s.remoteOnly && S.active !== "dev-2";
    if (na) return '<span class="dim" style="font-size:11px">remote instances only</span>';
    if (s.type === "bool") return `<button class="toggle" role="switch" aria-checked="${!!v}" data-set="${s.k}" aria-label="${s.l}"></button>`;
    if (s.type === "sel") return `<select class="input" data-set="${s.k}">${s.opts.map((o) => `<option${o === v ? " selected" : ""}>${o}</option>`).join("")}</select>`;
    if (s.type === "ro") return `<span class="mono dim">${esc(v)}</span>`;
    return `<input class="input${s.type === "num" || s.k.startsWith("ui.hotkey") ? " mono" : ""}" data-set="${s.k}" value="${esc(v)}" ${s.type === "num" ? 'style="max-width:80px;text-align:right"' : ""}>${s.unit ? '<span class="dim" style="font-size:11px">' + s.unit + "</span>" : ""}`;
  }
  function viewSettings() {
    const rows = settingRows();
    let html = "", g = null;
    rows.forEach((s) => {
      if (s.g !== g) { html += `<div class="set-g">${s.g}</div>`; g = s.g; }
      const na = s.remoteOnly && S.active !== "dev-2";
      html += `<div class="set-r${s.k in P.mod ? " mod" : ""}${na ? " na" : ""}${P.sel.settings[1] === s.k ? " sel" : ""}" data-k="${s.k}" data-sel="setting:${s.k}"><span class="k">${s.k}</span><span class="l">${s.l}${s.d ? "<small>" + esc(s.d) + "</small>" : ""}</span><span class="v">${control(s)}</span><span class="s"><span class="tag scope-${s.scope}">${s.scope}</span></span></div>`;
    });
    if (!rows.length) html = `<div class="pal-empty">No setting matches “${esc(P.q)}”.</div>`;
    const mods = Object.keys(P.mod).map((k) => SETTINGS.find((s) => s.k === k));
    const cnt = (sc) => mods.filter((s) => s.scope === sc).length;
    const pend = mods.length ? `<div class="pending">${icon("save")}<span><b>${mods.length} unsaved change${mods.length > 1 ? "s" : ""}</b> · ${[["restart", "on restart"], ["reprovision", "on next reprovision"], ["reinstall", "on reinstall"]].filter(([k]) => cnt(k)).map(([k, t]) => '<span class="tag scope-' + k + '">' + cnt(k) + " " + t + "</span>").join(" ")}</span><span class="spacer"></span><button class="btn ghost" data-do="discard">Discard</button>${cnt("restart") ? `<button class="btn" data-do="saverestart">${icon("cycle")}Save &amp; restart</button>` : ""}<button class="btn primary" data-do="savemods">Save <kbd>Ctrl</kbd><kbd>S</kbd></button></div>` : "";
    return `<div class="view-bar"><label class="cmdbar" style="height:28px;flex:1 1 240px;max-width:320px">${icon("search")}<input id="cpSetQ" placeholder="Filter… t3, ram, scope:live" value="${esc(P.q)}" spellcheck="false"><kbd>/</kbd></label>
      <span class="seg" id="cpScope">${["all", "live", "restart", "reprovision", "reinstall"].map((s) => `<button aria-pressed="${P.scope === s}" data-sc="${s}">${s}<span class="n">${s === "all" ? SETTINGS.length : SETTINGS.filter((x) => x.scope === s).length}</span></button>`).join("")}</span></div>
      <div class="set-list scroll">${html}</div>${pend}`;
  }

  const DANGER = [
    ["Reinstall", "alert", "Rebuild from the current Ubuntu ISO. Agent config is backed up and restored.", "Reinstall"],
    ["Redownload", "download", "Fetch a fresh Ubuntu ISO (about 3 GB), then rebuild.", "Redownload"],
    ["Custom reinstall", "alert", "Clean wipe. Nothing is restored, and agents need to sign in again.", "Clean-wipe reinstall"],
    ["Remove instance", "trash", "Delete the VM, its disk, its child VMs and every trace on this PC.", "Remove"],
    ["Make this PC a Construct host", "server", "Adopt agent-vm, keep its data, and let other users create VMs here. You become the host admin.", null],
    ["Resolve config repo conflict", "layers", "No conflict right now. Shown here because resolving one can overwrite local profiles.", null],
  ];
  function viewDanger() {
    return `<div class="danger-list scroll"><div class="danger-banner">${icon("shield", "lg")}<span>Nothing on this tab runs from one click, and none of it is in the tray. Each verb opens the palette with an impact preview and asks you to type <b class="mono">${S.active}</b>.</span></div>
      ${DANGER.map(([t, ic, d, v]) => `<div class="dz${P.sel.danger[1] === t ? " sel" : ""}" data-sel="danger:${t}"><span class="di${v ? "" : " n"}">${icon(ic)}</span><div><b>${t}</b><p>${d}</p></div>${v ? `<button class="btn danger" data-dz="${v}">${t.split(" ")[0]}… <kbd>type name</kbd></button>` : `<button class="btn" data-do="${t.startsWith("Make") ? "makehost" : "conflict"}">${t.startsWith("Make") ? "Set up…" : "Open repo"}</button>`}</div>`).join("")}</div>`;
  }

  /* ---------- inspector ---------- */
  function pr(k, v) { return `<div class="pr"><span>${k}</span><span>${v}</span></div>`; }
  function insp() {
    const sel = P.sel[P.tab] || [];
    const [type, id] = sel;
    const el = $("#cpInsp");
    let h = "";
    const head = (kind, ttl, sub) => `<div class="insp-head"><span class="kind">${kind}</span><span class="ttl">${ttl}</span>${sub ? `<span class="sub">${sub}</span>` : ""}</div>`;
    const acts = (a) => `<div class="insp-acts">${a}</div>`;
    const grp = (t) => `<div class="pg">${t}</div>`;
    if (type === "forward" && id !== "mic" && id !== "ssh") {
      const f = D.forwards.find((x) => x.id === id);
      const closed = S.closed.has(f.id);
      h = head(icon("forward", "sm") + " Forward", `<span class="mono">${f.vm}:${f.vmPort}</span>` + (closed ? '<span class="tag">closed</span>' : f.state === "open" ? '<span class="tag ok">open</span>' : '<span class="tag">queued</span>'), f.label) +
        acts(closed ? `<button class="btn" data-fr="${f.id}">${icon("cycle")}Reopen</button>` : f.state === "open" ? `<button class="btn primary" data-fo="${f.id}">${icon("ext")}Open <kbd>↵</kbd></button><button class="btn" data-copy="${f.url}">${icon("copy")}Copy URL</button><button class="btn ghost" data-fc="${f.id}">${icon("x")}Close</button>` : `<button class="btn" data-do="power">${icon("play")}Resume dev-2</button><button class="btn ghost">${icon("x")}Cancel</button>`) +
        `<div class="props">${grp("Forward")}${pr("VM port", '<span class="mono">' + f.vmPort + "</span>")}${pr("On this PC", '<span class="mono">' + f.local + "</span>")}${pr("URL", '<a class="mono">' + f.url + "</a>")}${pr("Scope", f.scope === "host" ? "host forward · buildbox LAN" : "client · this PC only")}${pr("State", f.state)}${grp("Origin")}${pr("Opened by", f.by + " · " + f.ago + " ago")}${pr("Command", '<span class="mono">construct expose ' + f.vmPort + ' --label "' + f.label + '"</span>')}${pr("Instance", f.vm)}</div>` +
        (f.note ? `<div class="insp-note${f.remap ? " warn" : ""}">${icon("alert")}<span>${f.note}.</span></div>` : "");
    } else if (type === "forward") {
      h = id === "mic" ? head(icon("mic", "sm") + " Reverse tunnel", '<span class="mono">vm:8767</span>' + (S.mic ? '<span class="tag acc">armed</span>' : '<span class="tag">off</span>'), "Microphone passthrough for /voice") + acts(`<button class="btn" data-do="mic">${S.mic ? "Disarm" : "Arm"}</button><button class="btn ghost" data-go-set="mic.device">${icon("gear")}Device</button>`) +
        `<div class="props">${grp("Passthrough")}${pr("Device", D.mic.device)}${pr("Streaming", "no · live only while recording")}${pr("Shim", "rec/arecord installed ✓")}${pr("Chat mic button", "gate patched ✓")}${pr("Setting scope", '<span class="tag scope-live">live</span>')}</div>`
        : head(icon("terminal", "sm") + " Tunnel", '<span class="mono">vm:22</span>', "SSH used by Remote-SSH and the T3 HTTPS tunnel") + `<div class="props">${pr("Host", '<span class="mono">agent-vm.mshome.net</span>')}${pr("Key", "~/.ssh/construct_agent-vm")}</div>`;
    } else if (type === "notify") {
      const n = D.notifications.find((x) => x.id === id), ev = Object.values(D.events).flat().find((e) => e.entity[1] === id);
      const acked = S.acked.has(ev.id);
      h = head(icon("bell", "sm") + " Notification", esc(n.msg), `<span class="tag ${n.level === "error" ? "err" : "info"}">${n.level}</span> ${n.t} · ${acked ? "acknowledged" : "unread"}`) +
        acts(`${acked ? "" : `<button class="btn primary" data-ev="${ev.id}" data-a="Ack">${icon("check")}Acknowledge <kbd>a</kbd></button>`}<button class="btn" data-selagent="${n.agent}">${icon("agent")}Go to ${n.agent}</button>`) +
        `<div class="props">${grp("Message")}${pr("Text", esc(n.msg))}${pr("Level", n.level)}${pr("From", n.agent + " · repo " + n.repo)}${pr("Received", "today " + n.t + " · also shown as a Windows toast")}${pr("Command", '<span class="mono">construct notify "' + esc(n.msg) + '"' + (n.level === "error" ? " --level error" : "") + "</span>")}</div>`;
    } else if (type === "agent") {
      const g = D.agents.find((x) => x.id === id);
      h = head(icon("agent", "sm") + " Coding agent", g.name + (g.state === "working" ? ' <span class="tag acc">working</span>' : ""), g.extra) +
        acts(g.id === "claude" ? `<button class="btn primary" data-do="wake">${icon("bell")}${S.wake ? "Wake armed" : "Wake me when done"}</button><button class="btn" data-do="upd-claude">${icon("download")}Update after turn</button>` : g.id === "t3" ? `<button class="btn primary">${icon("ext")}Open web UI</button>` : `<button class="btn">${icon("code")}Open repo</button>`) +
        `<div class="props">${grp("Version")}${pr("Installed", '<span class="mono">' + g.ver + "</span>")}${pr("Latest", g.latest ? '<span class="mono">' + g.latest + '</span> <span class="tag info">update</span>' : "up to date")}${grp("Activity")}${pr("State", g.state)}${pr("Now", g.turn)}${g.repo ? pr("Repo", '<span class="mono">' + g.repo + "</span>") : ""}${grp("Today")}${pr("Tokens", g.tokens)}${pr("Cost", g.today ? "$" + g.today.toFixed(2) : "—")}</div>`;
    } else if (type === "instance") {
      const i = window.OL.inst(id), st = S.vmState[id];
      h = head(icon(i.parent ? "child" : "vm", "sm") + (i.parent ? " Child VM" : " Instance"), id + " " + dotFor(st), i.backend + (i.remote ? " · " + i.remote : "")) +
        acts(st === "running" ? `<button class="btn" data-switch="${id}">${icon("right")}Switch to</button>${i.parent ? `<button class="btn">${icon("clock")}Extend lease</button><button class="btn danger">${icon("trash")}Delete…</button>` : ""}` : `<button class="btn primary" data-resume="${id}">${icon("play")}Resume</button><button class="btn" data-switch="${id}">${icon("right")}Switch to</button>`) +
        `<div class="props">${grp("Machine")}${pr("State", st + (i.savedAgo ? " · by idle policy " + i.savedAgo + " ago" : ""))}${pr("OS", i.os)}${pr("vCPU / RAM", i.vcpu + " · " + i.ram + " GB")}${i.lease ? pr("Lease", i.lease + " left · expires 20:38") : ""}${i.idle ? pr("Idle policy", i.idle) : ""}${i.provisioned ? pr("Provisioned", '<span class="mono">@' + i.provisioned + "</span>") : ""}</div>`;
    } else if (type === "provision" || type === "update") {
      const isU = type === "update";
      h = head(icon(isU ? "upd" : "commit", "sm") + (isU ? " Construct update" : " Provision state"), isU ? '<span class="mono">b5c4348 → 3a91f0e</span>' : '<span class="mono">@c21978b</span> <span class="tag warn">behind 2</span>', isU ? "4 commits · updates the Companion and the provisioning scripts" : "Installed Construct is b5c4348. A reprovision brings agent-vm up to it.") +
        acts(isU ? `<button class="btn primary">${icon("upd")}Update Construct</button>` : (S.staged ? `<span class="tag acc">${icon("clock", "sm")} staged: runs when Claude idles</span><button class="btn ghost" data-do="unstage">Cancel</button>` : `<button class="btn primary" data-do="rep">${icon("cycle")}Reprovision…</button>`)) +
        `<div class="props">${grp(isU ? "Commits" : "What changes")}${(isU ? D.update.commits : D.update.commits.slice(1, 3)).map((c) => pr('<span class="mono">' + c[0] + "</span>", c[1])).join("")}${!isU ? grp("Safety") + pr("Keeps", "all data, repos, agent config") + pr("Downtime", "about 6 min") + pr("Right now", '<span style="color:var(--warn)">Claude Code is mid-turn</span>') : ""}</div>` +
        (!isU ? `<div class="insp-note">${icon("clock")}<span>Waiting costs you nothing urgent: the 2 commits change vTPM baselines and CPU defaults, which matter for child VMs.</span></div>` : "");
    } else if (type === "job") {
      h = head(icon("server", "sm") + " Host job", "reprovision dev-2", "queued on buildbox") + `<div class="props">${pr("Queued", "12:30")}${pr("Waits for", "create child win11-eval for alice (62%)")}</div>`;
    } else if (type === "setting") {
      const s = SETTINGS.find((x) => x.k === id);
      const v = valueOf(s);
      h = head(icon("gear", "sm") + " Setting · " + s.g, `<span class="mono">${s.k}</span>`, s.l) +
        `<div class="props">${grp("Value")}${pr("Current", '<span class="mono">' + esc(String(v)) + (s.unit ? " " + s.unit : "") + "</span>" + (s.k in P.mod ? ' <span class="tag acc">modified</span>' : ""))}${pr("Saved", '<span class="mono">' + esc(String(s.v)) + "</span>")}${pr("Scope", '<span class="tag scope-' + s.scope + '">' + s.scope + "</span>")}${pr("Stored in", '<span class="mono">instances/' + S.active + ".json</span>")}${s.d ? pr("Note", esc(s.d)) : ""}</div>` +
        `<div class="insp-note">${icon("clock")}<span>${SCOPE_TXT[s.scope]}</span></div>` +
        (s.k === "git.storeCredentials" ? `<div class="insp-note warn">${icon("alert")}<span>Stored in plaintext on the VM. An agent that follows injected instructions could read it.</span></div>` : "") +
        (s.k === "vm.diskGB" ? `<div class="insp-note warn">${icon("alert")}<span>The disk is 88% full. A full disk breaks provisioning in ways that look like permission errors.</span></div>` : "") +
        (s.remoteOnly && S.active !== "dev-2" ? `<div class="insp-note">${icon("server")}<span>The idle policy lives on the host service, so it only exists for remote instances. <a class="linkish" data-switch="dev-2">Switch to dev-2</a>.</span></div>` : "");
    } else if (type === "danger") {
      const d = DANGER.find((x) => x[0] === id);
      if (d && d[3]) {
        const c = window.OL.dangerConfirm(d[3], S.active);
        h = head(icon("alert", "sm") + " Impact preview", d[0] + " " + S.active, c.consequence) + acts(`<button class="btn danger" data-dz="${d[3]}">${icon("alert")}Stage in palette… <kbd>type name</kbd></button>`) +
          `<div class="props">${c.impact.map((r) => pr(r[0], r[1])).join("")}${grp("Checked")}${pr("Repos scanned", "construct, omniloop, gitgudlab, jarvis")}${pr("Scanned", "20 s ago")}</div>`;
      } else {
        h = head(icon("server", "sm") + " Rare", d ? d[0] : "", d ? d[2] : "") + `<div class="props">${pr("Needs", "admin rights · 1 UAC prompt")}${pr("Keeps", "agent-vm and its data")}</div>`;
      }
    } else if (type === "project") {
      const p = D.projects.find((x) => x.id === id);
      h = head(icon("folder", "sm") + " Project profile", `<span class="mono">${p.id}</span>` + (p.status === "new" ? ' <span class="tag info">new</span>' : ""), p.status === "new" ? "Auto-discovered on the VM and pulled by config sync." : "projects/" + p.id + ".json") +
        acts(`<button class="btn">${icon("code")}Edit JSON</button><button class="btn ghost">${icon("copy")}Duplicate</button>`) +
        `<div class="props">${grp("Next reprovision")}${pr("Selected", p.on ? "yes" : "no")}${grp("Repos")}${pr("1", '<span class="mono">git@gitgudlab:' + p.id + "/" + p.id + ".git</span>")}${p.repos > 1 ? pr("2", '<span class="mono">…' + (p.repos - 1) + " more</span>") : ""}${grp("Runtimes")}${pr("SDKs", p.runtimes)}${grp("MCP servers")}${pr("Count", p.mcp)}</div>` +
        `<div class="insp-note">${icon("clock")}<span>Profile changes apply on the next reprovision.</span></div>`;
    } else if (type === "day") {
      const d = D.usage.days[id];
      h = head(icon("chart", "sm") + " Day", d[0] + (id === 6 ? " · today so far" : ""), '<span class="mono">$' + d[1].toFixed(2) + "</span> across all agents") +
        `<div class="props">${grp("By agent")}${pr("Claude Code", "$" + (d[1] * 0.82).toFixed(2))}${pr("Codex", "$" + (d[1] * 0.14).toFixed(2))}${pr("OpenCode", "$" + (d[1] * 0.04).toFixed(2))}</div>`;
    } else {
      h = head("Remote config repo", id || "", "") + `<div class="props">${pr("Last pull", "3 min ago")}${pr("Conflicts", "none")}</div>`;
    }
    el.innerHTML = h;
  }

  function renderStatus() {
    const a = S.active, st = S.vmState[a];
    $("#cpStatus").innerHTML = `<button data-do="power">${dotFor(st)}${a} ${st}</button>` +
      (a === "agent-vm" && st === "running" ? `<button data-selagent="claude"><span class="dot busy"></span>Claude working 6m</button><button data-tabgo="forwards">${icon("forward", "sm")}2 forwards</button>` : "") +
      `<button data-do="mic">${icon("mic", "sm")}${S.mic ? "mic armed" : "mic off"}</button><button data-tabgo="projects">${icon("cycle", "sm")}synced 3m ago</button>` +
      (a === "agent-vm" ? `<button data-do="rep">${icon("commit", "sm")}c21978b · behind 2</button>` : "") +
      `<span class="spacer"></span><button data-tabgo="usage">${icon("dollar", "sm")}$4.12 today</button><button class="acc" data-go="upd">${icon("upd", "sm")}Construct 3a91f0e available</button><button data-do="pal"><span class="kbds"><kbd>Ctrl</kbd><kbd>K</kbd></span></button>`;
  }

  function render() {
    renderTop(); renderRail(); renderHead(); renderStatus();
    const v = { timeline: viewTimeline, agents: viewAgents, forwards: viewForwards, projects: viewProjects, usage: viewUsage, settings: viewSettings, danger: viewDanger }[P.tab]();
    const keepQ = document.activeElement && document.activeElement.id === "cpSetQ";
    const pos = keepQ ? document.activeElement.selectionStart : 0;
    $("#cpViews").innerHTML = `<div class="view on" style="flex:1">${v}</div>`;
    if (keepQ) { const q = $("#cpSetQ"); q.focus(); q.setSelectionRange(pos, pos); }
    insp();
  }
  window.OL.panelRender = render;

  /* ---------- events ---------- */
  win.addEventListener("click", (e) => {
    const t = e.target;
    const tab = t.closest("[data-tab]"); if (tab) return go(tab.dataset.tab);
    const tg = t.closest("[data-tabgo]"); if (tg) return go(tg.dataset.tabgo);
    const ri = t.closest(".rail-i[data-inst]"); if (ri) { S.active = ri.dataset.inst; P.extraEvents = P.extraEvents; S.emit(); return; }
    const rg = t.closest(".rail-i[data-go]"); if (rg) { if (rg.dataset.go === "notify") { P.tl = "unread"; P.tlScope = "all"; go("timeline"); } else { S.active = "agent-vm"; P.sel.timeline = ["provision", "agent-vm", "e12"]; go("timeline"); S.emit(); } return; }
    if (t.closest("#cpInst")) {
      dropdown(t.closest("#cpInst"), `<div class="dd-h">Switch instance</div>` + D.instances.map((i) => `<div class="dd-i${S.active === i.id ? " active" : ""}" data-v="${i.id}">${dotFor(S.vmState[i.id])}<span>${i.id}<small>${i.parent ? "child of " + i.parent : i.backend}</small></span><span class="r">${S.vmState[i.id]}</span></div>`).join("") + `<div class="dd-sep"></div><div class="dd-i" data-v="+">${icon("plus")}<span>New instance…</span><span class="r"></span></div>`, (v) => { if (v !== "+") { S.active = v; S.emit(); } else T("Opens the installer wizard", "ok"); });
      return;
    }
    if (t.closest("#cpHost")) { location.hash = "#host"; return; }
    const g = t.closest("[data-go]");
    if (g && g.dataset.go === "rep") { openPal("rep"); return; }
    if (g && g.dataset.go === "upd") { P.sel.timeline = ["update", "construct", "e10"]; go("timeline"); return; }
    const gs = t.closest("[data-go-set]"); if (gs) { focusSetting(gs.dataset.goSet); return; }
    const tl = t.closest("[data-tl]"); if (tl) { P.tl = tl.dataset.tl; return render(); }
    const sc = t.closest("[data-scope]"); if (sc) { P.tlScope = sc.dataset.scope; return render(); }
    const per = t.closest("[data-per]"); if (per) { P.period = per.dataset.per; T("Showing " + P.period + " totals", "ok"); return render(); }
    const scp = t.closest("[data-sc]"); if (scp) { P.scope = scp.dataset.sc; return render(); }
    const sw = t.closest("[data-switch]"); if (sw) { S.active = sw.dataset.switch; S.emit(); return; }
    const rs = t.closest("[data-resume]"); if (rs) { S.vmState[rs.dataset.resume] = "running"; S.emit(); T(rs.dataset.resume + " resumed in 9 s", "ok"); return; }
    const sa = t.closest("[data-selagent]"); if (sa) { P.sel.agents = ["agent", sa.dataset.selagent]; go("agents"); return; }
    const dz = t.closest("[data-dz]"); if (dz) { openDanger(dz.dataset.dz); return; }
    const fo = t.closest("[data-fo]"); if (fo) { const f = D.forwards.find((x) => x.id === fo.dataset.fo); T("Opened <span class='mono'>" + f.url + "</span>", "ok"); return; }
    const fc = t.closest("[data-fc]"); if (fc) { const id = fc.dataset.fc; S.closed.add(id); S.emit(); T("Forward closed", "ok", () => { S.closed.delete(id); S.emit(); }); return; }
    const fr = t.closest("[data-fr]"); if (fr) { S.closed.delete(fr.dataset.fr); S.emit(); return; }
    const cp = t.closest("[data-copy]"); if (cp) { T("Copied " + cp.dataset.copy, "ok"); return; }
    const setT = t.closest("button.toggle[data-set]");
    if (setT) { const s = SETTINGS.find((x) => x.k === setT.dataset.set); applySetting(s, !(valueOf(s))); return; }
    const ev = t.closest("[data-ev]");
    if (ev) {
      const a = ev.dataset.a, e2 = allEvents().find((x) => x.id === ev.dataset.ev);
      if (a === "Ack") { S.acked.add(e2.id); S.emit(); T("Acknowledged", "ok", () => { S.acked.delete(e2.id); S.emit(); }); }
      else if (a === "Close") { S.closed.add(e2.entity[1]); S.emit(); T("Forward closed", "ok", () => { S.closed.delete(e2.entity[1]); S.emit(); }); }
      else if (a === "Reprovision") openPal("rep");
      else if (a === "Resume") { S.vmState["dev-2"] = "running"; S.emit(); T("dev-2 resuming", "ok"); }
      else if (a === "Wake me") { S.wake = true; S.emit(); T("You'll get a toast when Claude goes idle", "ok"); }
      else T(a + " · done", "ok");
      return;
    }
    const d = t.closest("[data-do]");
    if (d) {
      const k = d.dataset.do;
      if (k === "pal") openPal();
      else if (k === "rep") openPal("rep");
      else if (k === "unstage") { S.staged = null; S.emit(); }
      else if (k === "mic") { S.mic = !S.mic; S.emit(); T("Mic passthrough " + (S.mic ? "armed" : "disarmed"), "ok"); }
      else if (k === "wake") { S.wake = true; S.emit(); T("You'll get a toast when Claude goes idle", "ok"); }
      else if (k === "power") { const a = S.active; const st = S.vmState[a]; S.vmState[a] = st === "running" ? "off" : "running"; S.emit(); T(a + (st === "running" ? " is shutting down" : " is starting"), "ok"); }
      else if (k === "open") T("Opening VS Code on " + S.active + "…", "ok");
      else if (k === "opendd") dropdown(d, `<div class="dd-h">Open with</div><div class="dd-i active" data-v="VS Code">${icon("check")}<span>VS Code (Remote-SSH)<small>remembered</small></span><span class="r">↵</span></div><div class="dd-i" data-v="T3 Code"><span></span><span>T3 Code web UI</span></div><div class="dd-i" data-v="serve-web"><span></span><span>serve-web :8000</span></div><div class="dd-i" data-v="console"><span></span><span>Hyper-V console</span></div>`, (v) => T("Opening " + v, "ok"), "right");
      else if (k === "discard") { P.mod = {}; render(); T("Changes discarded", "ok"); }
      else if (k === "savemods" || k === "saverestart") { const n = Object.keys(P.mod).length; SETTINGS.forEach((s) => { if (s.k in P.mod) s.v = P.mod[s.k]; }); P.mod = {}; render(); T(k === "saverestart" ? "Saved · agent-vm restarts to apply (UAC)" : n + " setting(s) saved · applied at the listed time", "ok"); }
      else if (k === "expose") openPal("3001");
      else if (k === "updall" || k === "upd-claude") T("Claude Code 2.4.3 queued for after this turn", "ok");
      else if (k === "sync") T("Config synced · jarvis pulled", "ok");
      else if (k === "makehost") T("Opens the host setup wizard (admin)", "ok");
      else T(k, "ok");
      return;
    }
    const day = t.closest("[data-day]"); if (day) { P.sel.usage = ["day", +day.dataset.day]; render(); return; }
    const selEl = t.closest("[data-sel]");
    if (selEl && !t.closest("input,select,button")) {
      const [ty, id] = selEl.dataset.sel.split(":");
      P.sel[P.tab] = [ty, id];
      if (P.tab === "timeline") P.sel.timeline[2] = selEl.dataset.e;
      render(); return;
    }
    const row = t.closest(".tl-row");
    if (row && !t.closest("button")) {
      const e2 = allEvents().find((x) => x.id === row.dataset.e);
      P.sel.timeline = [e2.entity[0], e2.entity[1], e2.id];
      render();
    }
  });
  win.addEventListener("change", (e) => {
    const s = e.target.closest("[data-set]"); if (s && s.tagName !== "BUTTON") { const st = SETTINGS.find((x) => x.k === s.dataset.set); applySetting(st, st.type === "num" ? +s.value : s.value); }
    const pj = e.target.closest("[data-proj]"); if (pj) { const p = D.projects.find((x) => x.id === pj.dataset.proj); p.on = pj.checked; render(); T(p.id + (p.on ? " selected" : " skipped") + " for the next reprovision", "ok"); }
  });
  win.addEventListener("input", (e) => { if (e.target.id === "cpSetQ") { P.q = e.target.value; render(); } });
  function applySetting(s, v) {
    if (s.scope === "live") {
      if (s.k === "mic.enabled") { S.mic = v; S.emit(); }
      else s.v = v;
      if (s.k === "ui.theme") { document.documentElement.dataset.theme = v === "Light" ? "light" : "dark"; }
      P.sel.settings = ["setting", s.k]; render(); T(s.l + " applied now", "ok"); return;
    }
    if (v === s.v) delete P.mod[s.k]; else P.mod[s.k] = v;
    P.sel.settings = ["setting", s.k]; render();
  }

  /* keyboard: g-sequences, Ctrl+K, 1-5 filters, / to search */
  let gPending = 0;
  window.OL.panelKey = (e) => {
    const typing = /INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName);
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openPal(); return; }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && P.tab === "settings") { e.preventDefault(); if (Object.keys(P.mod).length) win.querySelector('[data-do="savemods"]').click(); return; }
    if (typing || $("#cpOverlay").classList.contains("on")) return;
    if (e.key === "g") { gPending = Date.now(); return; }
    if (Date.now() - gPending < 900) {
      const t = TABS.find((x) => x[2] === e.key); gPending = 0;
      if (t) { e.preventDefault(); go(t[0]); return; }
    }
    if (P.tab === "timeline" && /^[1-5]$/.test(e.key)) { P.tl = ["all", "agent", "forward", "lifecycle", "notify"][+e.key - 1]; render(); }
    if (P.tab === "settings" && e.key === "/") { e.preventDefault(); $("#cpSetQ").focus(); }
    if (e.key === "e" && P.tab === "forwards") openPal("3001");
  };

  S.listeners.push(() => render());
  render();
})();
