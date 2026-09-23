/* Operator Line: host admin panel (ops console for buildbox). */
(function () {
  "use strict";
  const { icon, esc, Palette, toast, dropdown } = window.OL;
  const win = document.getElementById("hpWin");
  if (!win) return;

  const VMS = [
    { id: "alice/work-vm", owner: "alice", kind: "primary", state: "running", act: "busy", ram: 16, demand: 13.8, vcpu: 6, idle: 0, lease: null, cost: 148.2, flags: [] },
    { id: "alice/bench", owner: "alice", kind: "primary", state: "running", act: "idle", ram: 12, demand: 3.1, vcpu: 4, idle: 125, lease: null, cost: 64.2, flags: ["idle"] , next: "saves in 55 min" },
    { id: "alice/win11-eval", owner: "alice", kind: "child", parent: "alice/work-vm", state: "creating", act: "", ram: 8, demand: 0, vcpu: 2, idle: 0, lease: "72h", cost: 0, flags: ["grace 29d"] },
    { id: "bob/api-vm", owner: "bob", kind: "primary", state: "running", act: "idle", ram: 12, demand: 9.4, vcpu: 4, idle: 12, lease: null, cost: 41.3, flags: [] },
    { id: "bob/win-qa", owner: "bob", kind: "child", parent: "bob/api-vm", state: "running", act: "idle", ram: 8, demand: 6.1, vcpu: 4, idle: 190, lease: "overdue 2h", overdue: true, cost: 12.8, flags: ["lease overdue", "delete failed"] },
    { id: "dana/dev-2", owner: "dana", kind: "primary", state: "saved", why: "idle policy", act: "", ram: 12, demand: 0, vcpu: 4, idle: 41, lease: null, cost: 31.1, flags: [] },
    { id: "dana/agent-vm-2", owner: "dana", kind: "primary", state: "running", act: "busy", ram: 16, demand: 12.2, vcpu: 8, idle: 0, lease: null, cost: 55.2, flags: ["inventory incomplete"] },
    { id: "mara/scratch-3", owner: "mara", kind: "primary", state: "saved", why: "memory pressure", act: "", ram: 16, demand: 0, vcpu: 4, idle: 70, lease: null, cost: 139.9, flags: ["saved by pressure"] },
  ];
  const USERS = [
    { id: "alice", role: "dev", prim: "2/3", child: "1/4", ram: 28, ramA: 32, cpu: "12/16", cost: 212.4, last: 188.1, flags: [] },
    { id: "bob", role: "dev", prim: "1/2", child: "2/2", ram: 20, ramA: 24, cpu: "8/8", cost: 54.1, last: 61.0, flags: ["lease overdue"] },
    { id: "dana", role: "admin", prim: "2/∞", child: "1/∞", ram: 28, ramA: null, cpu: "12", cost: 86.3, last: 92.4, flags: [] },
    { id: "mara", role: "contractor", prim: "1/1", child: "1/1", ram: 16, ramA: 16, cpu: "4/4", cost: 139.9, last: 97.2, flags: ["legacy credential"] },
    { id: "jonas", role: "dev", prim: "0/2", child: "0/2", ram: 0, ramA: 24, cpu: "0/8", cost: 0, last: 12.3, flags: ["disabled"] },
  ];
  const JOBS = [
    { id: "j1", t: "13:51", title: "create child win11-eval for alice", state: "running", pct: 62, phase: "Windows setup · 2 of 4" },
    { id: "j2", t: "12:30", title: "reprovision dev-2", owner: "dana", state: "queued", pct: 0, phase: "waits for j1" },
    { id: "j3", t: "09:14", title: "delete child bob/win-qa", state: "failed", pct: 0, phase: "access denied (0x80070005) on VHDX" },
    { id: "j4", t: "08:02", title: "re-inventory host", state: "done", pct: 100, phase: "1 VM incomplete" },
  ];
  const AUDIT = [
    ["13:51", "alice", "created child win11-eval", "alice/win11-eval"],
    ["13:48", "policy", "memory-pressure saved mara/scratch-3", "mara/scratch-3"],
    ["13:37", "policy", "idle policy saved dana/dev-2", "dana/dev-2"],
    ["13:22", "dana", "rotated token for mara", "mara"],
    ["12:30", "dana", "queued reprovision dev-2", "dana/dev-2"],
    ["11:02", "bob", "extended lease win-qa +8h (last)", "bob/win-qa"],
    ["09:14", "bob", "delete child win-qa → failed", "bob/win-qa"],
  ];
  const H = {
    tab: "machines", filter: "all", q: "", sort: ["ram", -1], checked: new Set(), open: "bob/win-qa", dtab: "overview",
    bottom: true, btab: "jobs", pressure: "elevated", openUser: "mara", period: "month", idlePolicy: "3h",
  };
  const problems = () => [
    JOBS.find((j) => j.id === "j3").state === "failed" && { k: "err", t: "Job failed: delete child bob/win-qa", d: "access denied on VHDX · 09:14", fix: "Retry", id: "j3" },
    VMS.find((v) => v.id === "bob/win-qa").overdue && { k: "warn", t: "Lease overdue: bob/win-qa", d: "2h over · 8 GB held", fix: "Shut down", id: "bob/win-qa" },
    { k: "warn", t: "Inventory incomplete: dana/agent-vm-2", d: "guest did not report disk usage", fix: "Re-inventory", id: "inv" },
    { k: "err", t: "Windows activation failed", d: "0xC004C008 key has exceeded its unlock limit · 2 days ago", fix: "Open key", id: "lic" },
  ].filter(Boolean);

  const TABS = [["machines", "Machines", "v", "vm"], ["users", "Users", "u", "users"], ["usage", "Usage", "$", "dollar"], ["media", "Media & licensing", "m", "disk"], ["policy", "Policy", "c", "gear"], ["service", "Service", "s", "server"]];

  win.innerHTML = `
    <div class="win-title"><span class="cx">${icon("server")}</span><span>Construct Host — <b>buildbox</b></span><span class="tag outline" style="margin-left:6px">admin · dana</span>
      <div class="win-ctl"><span><svg class="i" viewBox="0 0 16 16"><path d="M3 8h10"/></svg></span><span><svg class="i" viewBox="0 0 16 16"><rect x="3.5" y="3.5" width="9" height="9"/></svg></span><span class="x">${icon("x")}</span></div></div>
    <div class="w-top">
      <button class="inst-btn" id="hpHost"><span class="dot running"></span>buildbox<span class="meta">constructd 1.14.2</span>${icon("down", "sm")}</button>
      <div class="cmdbar" id="hpCmd" tabindex="0" role="button">${icon("search")}<span class="ph">Search VMs, users, jobs, actions…  <span class="mono dim" style="font-size:11px">bob · overdue · retry</span></span><span class="kbds"><kbd>Ctrl</kbd><kbd>K</kbd></span></div>
      <span class="dim" style="font-size:11px;display:inline-flex;gap:5px;align-items:center">${icon("clock", "sm")}sampled 20 s ago</span>
      <button class="btn sm ghost icon" id="hpRefresh" title="Refresh">${icon("cycle", "sm")}</button>
      <button class="btn sm" id="hpUpd" style="color:var(--info)">${icon("upd", "sm")}1.15.0 available</button>
    </div>
    <div class="w-tabs" id="hpTabs"></div>
    <div class="hp-kpi" id="hpKpi"></div>
    <div id="hpMain" style="flex:1;min-height:0;display:flex;flex-direction:column"></div>
    <div class="bpanel" id="hpBottom"></div>
    <div class="w-status" id="hpStatus"></div>
    <div class="overlay" id="hpOverlay"><div class="pal-modal"><div class="pm-in" id="hpPmIn">${icon("search", "lg")}<input id="hpPalInput" placeholder="Search VMs, users, jobs, actions…" autocomplete="off" spellcheck="false"><kbd>Esc</kbd></div><div class="pm-list scroll" id="hpPalList"></div>
      <div class="pm-foot"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>↵</kbd> open</span><span class="spacer"></span><span>owner:alice · state:saved · idle&gt;1h work in the VM filter too</span></div></div></div>
    <div class="toast-host" id="hpToast" style="bottom:${"220px"}"></div>`;
  const $ = (s) => win.querySelector(s);
  const T = (m, k, u) => toast($("#hpToast"), m, k, u);

  /* ---------- palette ---------- */
  function hostCommands() {
    const c = [];
    VMS.forEach((v) => c.push({ group: "VMs", icon: v.kind === "child" ? "child" : "vm", title: v.id, sub: v.state + (v.why ? " (" + v.why + ")" : "") + " · " + v.ram + " GB" + (v.lease ? " · lease " + v.lease : ""), kw: "vm " + v.owner + " " + v.state + " " + v.flags.join(" "), suggest: v.overdue || v.state === "saved", run: () => openVm(v.id) }));
    USERS.forEach((u) => c.push({ group: "Users", icon: "user", title: u.id, sub: u.role + " · RAM " + u.ram + (u.ramA ? "/" + u.ramA : "") + " GB · $" + u.cost.toFixed(2) + " this month" + (u.flags.length ? " · " + u.flags.join(", ") : ""), kw: "user " + u.role + " " + u.flags.join(" "), run: () => { H.tab = "users"; H.openUser = u.id; render(); } }));
    JOBS.forEach((j) => c.push({ group: "Jobs", icon: "layers", title: j.title, sub: j.state + " · " + j.t + " · " + j.phase, kw: "job " + j.state, suggest: j.state === "failed", run: () => { H.bottom = true; H.btab = "jobs"; render(); } }));
    c.push({ group: "Actions", icon: "cycle", title: "Retry failed job: delete child bob/win-qa", kw: "retry failed job", run: () => retryJob() });
    c.push({ group: "Actions", icon: "filter", title: "Filter: idle > 1h", kw: "filter idle saved", run: () => { H.tab = "machines"; H.filter = "idle"; render(); } });
    c.push({ group: "Actions", icon: "filter", title: "Filter: overdue leases", kw: "filter overdue lease", run: () => { H.tab = "machines"; H.filter = "overdue"; render(); } });
    c.push({ group: "Actions", icon: "filter", title: "Filter: saved by pressure", kw: "filter pressure saved memory", run: () => { H.tab = "machines"; H.filter = "pressure"; render(); } });
    c.push({ group: "Actions", icon: "upd", title: "Update host service to 1.15.0", sub: "1 running job blocks Apply", kw: "update service upgrade", run: () => { H.tab = "service"; render(); } });
    c.push({ group: "Actions", icon: "server", title: "Re-inventory host", kw: "inventory refresh", run: () => T("Re-inventory queued", "ok") });
    c.push({ group: "Actions", icon: "user", title: "Register user…", kw: "user add register onboard", run: () => { H.tab = "users"; render(); T("Register form opened in the users pane", "ok"); } });
    c.push({ group: "Actions", icon: "key", title: "Windows license key pool", kw: "license mak key windows activation", run: () => { H.tab = "media"; render(); } });
    c.push({ group: "Actions", icon: "panel", title: "Toggle bottom panel", kw: "panel jobs audit toggle", run: () => { H.bottom = !H.bottom; render(); } });
    return c;
  }
  const pal = Palette({ input: $("#hpPalInput"), list: $("#hpPalList"), placeholder: "Search VMs, users, jobs, actions…", getCommands: hostCommands, onClose: () => $("#hpOverlay").classList.remove("on"), onState: (m) => $("#hpPmIn").classList.toggle("danger", m === "confirm") });
  function openPal(q) { $("#hpOverlay").classList.add("on"); pal.set(q || ""); $("#hpPalInput").focus(); }
  window.OL.hostPalette = openPal;
  $("#hpOverlay").addEventListener("mousedown", (e) => { if (e.target.id === "hpOverlay") pal.close(); });
  function confirmDelete(names, after) {
    $("#hpOverlay").classList.add("on");
    const name = names.length === 1 ? names[0] : "delete " + names.length + " vms";
    pal.openConfirm({ confirm: { name, verb: "Delete", icon: "trash", title: "Delete " + (names.length === 1 ? names[0] : names.length + " VMs"),
      consequence: "The VMs and their disks are deleted on buildbox. Owners get an audit entry and a notification.",
      impact: [["VMs", names.map((n) => '<span class="mono">' + n + "</span>").join(", ")], ["RAM freed", names.reduce((s, n) => s + VMS.find((v) => v.id === n).ram, 0) + " GB committed"], ["Children", names.includes("bob/api-vm") ? "bob/win-qa is deleted too" : "none"], ["Licences", names.includes("bob/win-qa") ? "key A activation released" : "none"]],
      run: () => { after && after(); } } });
    $("#hpPalInput").focus();
  }

  /* ---------- helpers ---------- */
  const fmtIdle = (v) => v.state === "creating" ? "—" : v.act === "busy" ? '<span style="color:var(--accent)">busy</span>' : v.idle >= 60 ? Math.floor(v.idle / 60) + "h " + String(v.idle % 60).padStart(2, "0") + "m" : v.idle + "m";
  function stateTag(v) {
    if (v.state === "running") return v.act === "busy" ? '<span class="tag acc"><span class="dot busy" style="width:6px;height:6px;box-shadow:none"></span>busy</span>' : '<span class="tag ok">running</span>';
    if (v.state === "saved") return '<span class="tag saved">saved · ' + (v.why === "memory pressure" ? "pressure" : "idle") + "</span>";
    if (v.state === "creating") return '<span class="tag info">creating 62%</span>';
    return '<span class="tag">' + v.state + "</span>";
  }
  const FILTERS = [
    ["all", "All VMs", "vm", () => true],
    ["running", "Running", "play", (v) => v.state === "running"],
    ["idle", "Idle > 1h", "clock", (v) => v.state === "running" && v.idle >= 60],
    ["overdue", "Overdue leases", "alert", (v) => v.overdue],
    ["pressure", "Saved by pressure", "save", (v) => v.why === "memory pressure"],
    ["saved", "Saved or off", "pause", (v) => v.state === "saved" || v.state === "off"],
    ["children", "Child VMs", "child", (v) => v.kind === "child"],
    ["mine", "Mine", "user", (v) => v.owner === "dana"],
  ];
  function visibleVms() {
    const f = FILTERS.find((x) => x[0] === H.filter)[3];
    let list = VMS.filter(f);
    const q = H.q.trim().toLowerCase();
    if (q) q.split(/\s+/).forEach((tok) => {
      let m;
      if ((m = tok.match(/^owner:(\w+)/))) list = list.filter((v) => v.owner.startsWith(m[1]));
      else if ((m = tok.match(/^state:(\w+)/))) list = list.filter((v) => v.state.startsWith(m[1]) || (v.act || "").startsWith(m[1]));
      else if ((m = tok.match(/^idle>(\d+)h/))) list = list.filter((v) => v.state === "running" && v.idle >= +m[1] * 60);
      else list = list.filter((v) => (v.id + " " + v.flags.join(" ") + " " + v.state).toLowerCase().includes(tok));
    });
    const [k, dir] = H.sort;
    const key = { vm: (v) => v.id, owner: (v) => v.owner, state: (v) => v.state, ram: (v) => v.ram, idle: (v) => v.idle, lease: (v) => (v.overdue ? 999 : v.lease ? 1 : 0), cost: (v) => v.cost }[k];
    return list.slice().sort((a, b) => (key(a) > key(b) ? 1 : key(a) < key(b) ? -1 : 0) * dir);
  }

  /* ---------- render ---------- */
  function renderTabs() {
    $("#hpTabs").innerHTML = TABS.map(([id, l, k, ic]) => `<button class="w-tab" role="tab" aria-selected="${H.tab === id}" data-tab="${id}">${icon(ic, "sm")}${l}${id === "machines" ? '<span class="cnt">' + VMS.length + "</span>" : id === "users" ? '<span class="cnt">' + USERS.length + "</span>" : id === "media" ? '<span class="cnt" style="color:var(--err)">1</span>' : ""}<span class="kbds"><kbd>g</kbd><kbd>${k}</kbd></span></button>`).join("") +
      `<span class="right"><button class="btn sm ghost" data-do="bottom">${icon("panel", "sm")}${H.bottom ? "Hide" : "Show"} panel <span class="kbds"><kbd>Ctrl</kbd><kbd>J</kbd></span></button></span>`;
  }
  function renderKpi() {
    const pr = problems();
    const elevated = H.pressure === "elevated";
    const scale = 150, pct = (g) => (g / scale) * 100;
    $("#hpKpi").innerHTML = `
      <div class="hk" data-k="health"><span class="l">Health</span><span class="v">${pr.length ? '<span class="dot warn"></span>' + pr.length + " issues" : '<span class="dot running"></span>ok'}</span><span class="s">${pr.length ? "overdue lease · inventory · licence" : "all checks pass"}</span></div>
      <div class="hk" data-k="version"><span class="l">Service</span><span class="v mono">1.14.2 <span class="tag info">1.15.0</span></span><span class="s">capacity mode: enforce</span></div>
      <div class="hk" data-k="ram" style="cursor:default"><span class="l">RAM · 128 GB physical <span class="spacer"></span><span class="mono" style="letter-spacing:0;text-transform:none;font-weight:500;color:var(--fg-2)">committed 142 GB · 111%</span></span>
        <div class="rambar" title="Resident 96 GB · admission line 118 GB · physical 128 GB · committed 142 GB">
          <div class="track"><div class="seg-o" style="width:${pct(40)}%;background:var(--accent);border-radius:3px 0 0 3px"></div><div class="seg-o" style="width:${pct(28)}%;background:color-mix(in srgb,var(--accent) 70%,var(--bg-4))"></div><div class="seg-o" style="width:${pct(20)}%;background:color-mix(in srgb,var(--accent) 45%,var(--bg-4))"></div><div class="seg-o" style="width:${pct(8)}%;background:var(--fg-3)"></div></div>
          <div class="over" style="left:${pct(128)}%;width:${pct(14)}%"></div>
          <div class="tick adm" style="left:${pct(118)}%"></div><div class="phys" style="left:${pct(128)}%"></div><div class="tick" style="left:${pct(142)}%;width:2px;background:var(--err)"></div>
        </div>
        <div class="ram-legend"><span><i style="background:var(--accent)"></i>VMs busy 40</span><span><i style="background:color-mix(in srgb,var(--accent) 70%,var(--bg-4))"></i>running idle 28</span><span><i style="background:color-mix(in srgb,var(--accent) 45%,var(--bg-4))"></i>children 20</span><span><i style="background:var(--fg-3)"></i>host 8</span><span><i style="background:var(--warn)"></i>admission 118</span><span><i style="background:var(--hatch)"></i>over-commit</span></div></div>
      <div class="hk" data-k="pressure"><span class="l">Memory pressure</span><span class="v" style="color:${elevated ? "var(--warn)" : "var(--ok)"}">${elevated ? "elevated" : "normal"}</span><span class="s">${elevated ? "last save 22 min ago · mara/scratch-3" : "resident 84 GB · below 85%"}</span></div>
      <div class="hk" data-k="disk"><span class="l">Disk D:</span><span class="v mono">61%</span><div class="meter" style="margin-top:3px"><i style="width:61%"></i></div><span class="s">1.4 TB free of 3.6 TB</span></div>
      <div class="hk" data-k="jobs"><span class="l">Jobs</span><span class="v">${JOBS.filter((j) => j.state === "running").length} running · ${JOBS.filter((j) => j.state === "queued").length} queued</span><span class="s" style="color:${JOBS.some((j) => j.state === "failed") ? "var(--err)" : "var(--fg-2)"}">${JOBS.filter((j) => j.state === "failed").length} failed today</span></div>`;
  }

  function viewMachines() {
    const list = visibleVms();
    const allChecked = list.length && list.every((v) => H.checked.has(v.id));
    const someChecked = list.some((v) => H.checked.has(v.id));
    const th = (k, l, cls) => `<th class="sort ${cls || ""}" data-sort="${k}">${l}${H.sort[0] === k ? '<span class="ar">' + (H.sort[1] > 0 ? "▲" : "▼") + "</span>" : ""}</th>`;
    const bar = H.checked.size ? `<div class="bulk"><b>${H.checked.size} selected</b><button class="btn sm" data-bulk="save">${icon("save", "sm")}Save</button><button class="btn sm" data-bulk="shutdown">${icon("power", "sm")}Shut down</button><button class="btn sm" data-bulk="start">${icon("play", "sm")}Start</button><button class="btn sm" data-bulk="lease">${icon("clock", "sm")}Extend lease +8h</button><button class="btn sm" data-bulk="idle">${icon("gear", "sm")}Idle policy…</button><span class="spacer"></span><button class="btn sm danger" data-bulk="delete">${icon("trash", "sm")}Delete… <kbd>type</kbd></button><button class="btn sm ghost" data-bulk="clear">Clear <kbd>Esc</kbd></button></div>`
      : `<div class="tbar"><label class="q">${icon("filter", "sm")}<input id="hpQ" placeholder="owner:alice state:running idle>1h" value="${esc(H.q)}" spellcheck="false"><kbd>/</kbd></label><span class="dim" style="font-size:11px">${list.length} of ${VMS.length} · sorted by ${H.sort[0]}</span><span class="spacer"></span><button class="btn sm ghost" data-do="savefilter">${icon("plus", "sm")}Save filter</button><button class="btn sm">${icon("plus", "sm")}New VM for…</button></div>`;
    const rows = list.map((v) => `<tr class="row${H.open === v.id ? " sel" : ""}${H.checked.has(v.id) ? " checked" : ""}" data-vm="${v.id}">
      <td class="cb"><input type="checkbox" class="cbx" data-chk="${v.id}" ${H.checked.has(v.id) ? "checked" : ""} aria-label="select ${v.id}"></td>
      <td><span style="display:inline-flex;align-items:center;gap:6px">${v.kind === "child" ? '<span class="dim">' + icon("child", "sm") + "</span>" : ""}<b class="mono" style="font-weight:600">${v.id.split("/")[1]}</b></span>${(() => { const fl = v.flags.filter((f) => f !== "lease overdue" && f !== "saved by pressure" && f !== "idle"); const bits = (v.parent ? ["child of " + v.parent.split("/")[1]] : []).concat(fl.map((f) => '<span style="color:var(--' + (f.includes("failed") ? "err" : "warn") + ')">' + f + "</span>")); return bits.length ? '<div class="sub">' + bits.join(" · ") + "</div>" : ""; })()}</td>
      <td><span class="owner-l" data-owner="${v.owner}">${v.owner}</span></td>
      <td>${stateTag(v)}</td>
      <td class="num"><span class="ram-cell">${v.ram} GB<span class="meter${v.demand / v.ram > 0.85 ? " warn" : ""}"><i style="width:${(v.demand / v.ram) * 100}%"></i></span></span></td>
      <td class="num">${fmtIdle(v)}${v.next ? '<div class="sub" style="font-family:var(--font-ui)">' + v.next + "</div>" : ""}</td>
      <td>${v.overdue ? '<span class="tag err">' + v.lease + "</span>" : v.lease ? '<span class="mono" style="font-size:11.5px">' + v.lease + "</span>" : '<span class="dim">—</span>'}</td>
      <td class="num">$${v.cost.toFixed(2)}</td></tr>`).join("");
    return `<div class="hp-grid${H.open ? " detail" : ""}">
      <nav class="sf" aria-label="Saved filters"><div class="tl-filters" style="border:0;padding:2px 0"><div class="fh">Saved filters</div>
        ${FILTERS.map(([k, l, ic, f]) => `<button class="tl-f" aria-pressed="${H.filter === k}" data-f="${k}">${icon(ic, "sm")}${l}<span class="n">${VMS.filter(f).length}</span></button>`).join("")}
        <div class="fh" style="margin-top:6px">Owners</div>
        ${USERS.map((u) => `<button class="tl-f" data-owner="${u.id}" aria-pressed="${H.q === "owner:" + u.id}">${icon("user", "sm")}${u.id}<span class="n">${VMS.filter((v) => v.owner === u.id).length}</span></button>`).join("")}
      </div></nav>
      <div class="hp-mainc">${bar}<div class="tscroll scroll"><table class="grid"><thead><tr><th class="cb"><input type="checkbox" class="cbx" id="hpAll" ${allChecked ? "checked" : ""} aria-label="select all"></th>${th("vm", "VM")}${th("owner", "Owner")}${th("state", "State")}${th("ram", "RAM · demand", "num")}${th("idle", "Idle for", "num")}${th("lease", "Lease")}${th("cost", "Month", "num")}</tr></thead><tbody>${rows || '<tr><td colspan="8" class="dim" style="height:60px;text-align:center">No VM matches this filter.</td></tr>'}</tbody></table></div></div>
      ${H.open ? detailPane() : ""}</div>`;
  }
  function pr(k, v) { return `<div class="pr"><span>${k}</span><span>${v}</span></div>`; }
  function detailPane() {
    const v = VMS.find((x) => x.id === H.open);
    if (!v) return "";
    const job = v.id === "bob/win-qa" && JOBS.find((j) => j.id === "j3");
    let body = "";
    if (H.dtab === "overview") {
      body = `<div class="props"><div class="pg">Machine</div>${pr("Owner", '<a data-owner="' + v.owner + '">' + v.owner + "</a>")}${pr("Kind", v.kind + (v.parent ? ' of <a data-vm-open="' + v.parent + '">' + v.parent + "</a>" : ""))}${pr("State", stateTag(v))}${pr("vCPU / RAM", v.vcpu + " · " + v.ram + " GB assigned")}${pr("RAM demand", v.demand ? v.demand + " GB · sampled 20 s ago" : "—")}${pr("Idle for", fmtIdle(v))}${v.lease ? pr("Lease", v.overdue ? '<span style="color:var(--err)">expired 2h ago</span> · max 8h' : v.lease) : ""}
        <div class="pg">Guest</div>${v.id.includes("win") ? pr("Windows", v.id === "bob/win-qa" ? "activated · MAK key A" : "evaluation · grace 29 days") : pr("Ubuntu", "26.04 LTS")}${pr("Network", "relayed · host default")}${pr("Token", '<span class="mono">vm_…4f2a</span> · <a data-do="rotate">rotate</a>')}
        <div class="pg">Month</div>${pr("Cost", "$" + v.cost.toFixed(2))}${pr("Tokens", v.cost ? (v.cost * 21000).toLocaleString("en-US") : "—")}</div>` +
        (job && job.state === "failed" ? `<div class="insp-note err">${icon("alert")}<span><b>delete child bob/win-qa</b> failed at 09:14: access denied on the VHDX. <button class="linkish" data-do="retry">Retry job</button></span></div>` : "") +
        (v.overdue ? `<div class="insp-note warn">${icon("clock")}<span>Lease ran out 2h ago. The owner was notified at expiry. Shut it down or extend the lease.</span></div>` : "") +
        (v.why === "memory pressure" ? `<div class="insp-note warn">${icon("save")}<span>Saved by memory pressure 22 min ago (resident above 85%). It resumes when mara starts it and there is headroom under the admission line.</span></div>` : "");
    } else if (H.dtab === "overrides") {
      body = `<div class="insp-note">${icon("shield")}<span>Restrict-only. An override can only tighten what ${v.owner}'s allowance grants. Empty means no override.</span></div><div class="props">` +
        [["Max RAM", "16 GB (allowance)", ""], ["Max vCPU", "8", ""], ["Idle action", "save after 3h (host)", "save after 60 min"], ["Lease max", "8h", ""], ["Host forwards", "allowed", "refused"]].map(([k, inh, ov]) => `<div class="pr" style="grid-template-columns:96px 1fr"><span>${k}</span><span><input class="input mono" style="width:100%;height:24px" placeholder="${inh}" value="${ov}"></span></div>`).join("") +
        `</div><div class="insp-acts" style="border:0"><button class="btn primary" data-do="saveov">Save overrides</button><button class="btn ghost">Clear all</button></div>`;
    } else if (H.dtab === "usage") {
      body = `<div class="props"><div class="pg">This month</div>${pr("Claude Code", "$" + (v.cost * 0.7).toFixed(2))}${pr("Codex", "$" + (v.cost * 0.24).toFixed(2))}${pr("OpenCode", "$" + (v.cost * 0.06).toFixed(2))}<div class="pg">Resources</div>${pr("Disk", "64 GB VHDX incl. checkpoints")}${pr("Uptime", "3d 4h")}</div>`;
    } else {
      body = `<div>${AUDIT.filter((a) => a[3] === v.id || a[3] === v.owner).map((a) => `<div class="log-l" style="grid-template-columns:44px 70px 1fr"><span class="t">${a[0]}</span><span class="who">${a[1]}</span><span>${a[2]}</span></div>`).join("") || '<div class="pal-empty">No audit entries today.</div>'}</div>`;
    }
    const acts = v.state === "running" ? `<button class="btn sm" data-vmact="save">${icon("save", "sm")}Save</button><button class="btn sm" data-vmact="shutdown">${icon("power", "sm")}Shut down</button>${v.lease ? `<button class="btn sm" data-vmact="lease">${icon("clock", "sm")}+8h</button>` : ""}` :
      v.state === "creating" ? `<button class="btn sm" data-vmact="cancel">${icon("x", "sm")}Cancel job</button>` : `<button class="btn sm primary" data-vmact="start">${icon("play", "sm")}Start</button>`;
    return `<aside class="dpane"><div class="insp-head"><span class="kind">${icon(v.kind === "child" ? "child" : "vm", "sm")} ${v.kind} VM<span class="spacer"></span><button class="btn sm ghost icon" data-do="closedetail" title="Close (Esc)">${icon("x", "sm")}</button></span><span class="ttl mono">${v.id}</span></div>
      <div class="insp-acts">${acts}<button class="btn sm ghost icon" data-do="vmmore" title="More">${icon("dots", "sm")}</button></div>
      <div class="dtabs">${["overview", "overrides", "usage", "audit"].map((t) => `<button aria-selected="${H.dtab === t}" data-dtab="${t}">${t}</button>`).join("")}</div>
      <div class="scroll" style="flex:1;min-height:0">${body}</div></aside>`;
  }

  function viewUsers() {
    const u = USERS.find((x) => x.id === H.openUser);
    const rows = USERS.map((x) => `<tr class="row${H.openUser === x.id ? " sel" : ""}" data-user="${x.id}"><td><b>${x.id}</b></td><td>${x.role === "admin" ? '<span class="tag acc">admin</span>' : x.role === "contractor" ? '<span class="tag">contractor</span>' : '<span class="tag outline">dev</span>'}</td><td class="num">${x.prim}</td><td class="num">${x.child}</td>
      <td><span style="display:inline-flex;gap:8px;align-items:center"><span class="bullet"><i class="${x.ramA && x.ram >= x.ramA ? "full" : ""}" style="width:${x.ramA ? (x.ram / x.ramA) * 100 : 40}%"></i></span><span class="mono" style="font-size:11px">${x.ram}${x.ramA ? "/" + x.ramA : ""} GB</span></span></td>
      <td class="num">${x.cpu}</td><td class="num">$${x.cost.toFixed(2)}</td><td>${x.flags.map((f) => '<span class="tag ' + (f === "disabled" ? "" : "warn") + '">' + f + "</span>").join(" ")}</td></tr>`).join("");
    const form = u ? `<aside class="dpane"><div class="insp-head"><span class="kind">${icon("user", "sm")} User<span class="spacer"></span><button class="btn sm ghost icon" data-do="closeuser">${icon("x", "sm")}</button></span><span class="ttl">${u.id} ${u.flags.map((f) => '<span class="tag warn">' + f + "</span>").join("")}</span><span class="sub">One form, one save. Empty fields inherit the host default.</span></div>
      <div class="scroll" style="flex:1;min-height:0"><div class="props"><div class="pg">Identity</div>
      <div class="pr"><span>Role</span><span><select class="input" style="width:100%;height:24px"><option ${u.role === "dev" ? "selected" : ""}>dev</option><option ${u.role === "contractor" ? "selected" : ""}>contractor</option><option ${u.role === "admin" ? "selected" : ""}>admin</option></select></span></div>
      <div class="pg">Allowance</div>
      ${[["Primaries", u.prim.split("/")[1]], ["Children", u.child.split("/")[1]], ["RAM", (u.ramA || "∞") + " GB"], ["vCPU", u.cpu.split("/")[1] || "∞"], ["Lease max", "8h"], ["Host forwards", "allowed"]].map(([k, v]) => `<div class="pr"><span>${k}</span><span><input class="input mono" style="width:100%;height:24px" value="${v}"></span></div>`).join("")}
      <div class="pg">Tokens</div>${pr('<span class="mono">usr_…91c0</span>', "issued 13:22 today")}${u.flags.includes("legacy credential") ? pr('<span class="mono">legacy</span>', '<span style="color:var(--warn)">shared secret, revoke after rotation</span>') : ""}</div>
      <div class="insp-acts" style="border-top:1px solid var(--line)"><button class="btn primary" data-do="saveuser">Save ${u.id} <kbd>Ctrl</kbd><kbd>S</kbd></button><button class="btn" data-do="issuetoken">${icon("key")}Issue token</button><button class="btn danger" data-do="offboard">Offboard…</button></div></div></aside>` : "";
    return `<div class="hp-grid${u ? " detail" : ""}" style="grid-template-columns:${u ? "1fr 340px" : "1fr"}"><div class="hp-mainc"><div class="tbar"><span class="ttl" style="font-weight:600">Users</span><span class="dim" style="font-size:11px">allowance bars: used vs budget</span><span class="spacer"></span><button class="btn sm" data-do="register">${icon("plus", "sm")}Register user</button></div>
      <div class="tscroll scroll"><table class="grid"><thead><tr><th>User</th><th>Role</th><th class="num">Primaries</th><th class="num">Children</th><th>RAM used / allowance</th><th class="num">vCPU</th><th class="num">Month</th><th>Flags</th></tr></thead><tbody>${rows}</tbody></table></div></div>${form}</div>`;
  }

  function viewUsage() {
    const max = Math.max(...USERS.map((u) => u.cost));
    return `<div class="hp-mainc" style="flex:1"><div class="tbar"><span style="font-weight:600">Token usage &amp; cost</span><span class="seg">${["today", "month", "all time"].map((p) => `<button aria-pressed="${H.period === p}" data-per="${p}">${p}</button>`).join("")}</span><span class="dim" style="font-size:11px">guests report every 15 min · cost is an estimate from the collector's price table</span></div>
      <div class="hcards"><div class="card"><div class="card-h">By user <span class="muted">vs last month</span></div><div class="card-b">${USERS.slice().sort((a, b) => b.cost - a.cost).map((u) => { const d = u.cost - u.last; return `<div class="kv"><span>${u.id}<span class="sub"><span class="meter" style="width:180px;display:inline-block;vertical-align:middle"><i style="width:${(u.cost / max) * 100}%"></i></span></span></span><span class="mono" style="text-align:right">$${u.cost.toFixed(2)}<span class="sub" style="color:${d > 0 ? "var(--warn)" : "var(--fg-3)"}">${d > 0 ? "+" : ""}${d.toFixed(2)}</span></span></div>`; }).join("")}</div></div>
      <div class="card"><div class="card-h">By VM</div><div class="card-b">${VMS.slice().sort((a, b) => b.cost - a.cost).map((v) => `<div class="kv"><span class="mono" style="font-size:12px">${v.id}</span><span class="mono">$${v.cost.toFixed(2)}</span></div>`).join("")}</div></div>
      <div class="card span2"><div class="card-h">By tool, host total</div><div class="card-b"><div class="kv"><span>Claude Code</span><span class="mono">$392.10</span></div><div class="kv"><span>Codex</span><span class="mono">$118.40</span></div><div class="kv"><span>OpenCode</span><span class="mono">$22.20</span></div></div></div></div></div>`;
  }

  function viewMedia() {
    const key = (n, used, cap, extra) => `<div class="kv"><span>${n}<span class="sub">${extra}</span></span><span style="display:flex;gap:8px;align-items:center"><span class="bullet" style="width:100px"><i class="${used / cap >= 0.7 ? "full" : ""}" style="width:${(used / cap) * 100}%"></i></span><span class="mono" style="font-size:11px">${used}/${cap}</span></span></div>`;
    return `<div class="hcards scroll" style="flex:1">
      <div class="card"><div class="card-h">${icon("disk")}Primary ISO catalog<span class="spacer"></span><span class="muted">read-only · built on the host</span></div><div class="card-b"><div class="kv"><span>Ubuntu 26.04 LTS server<span class="sub">last build 3 days ago · sha256 ok</span></span><span class="tag ok">current</span></div><div class="kv"><span>Ubuntu 24.04.3 LTS<span class="sub">kept for older instances</span></span><span class="tag">available</span></div></div></div>
      <div class="card"><div class="card-h">${icon("child")}Child media<span class="spacer"></span><button class="btn sm">${icon("plus", "sm")}Acquire…</button><button class="btn sm ghost">Run cleanup</button></div><div class="card-b"><div class="kv"><span>Windows 11 24H2 eval<span class="sub">prepared · 6.1 GB · used by 2 guests</span></span><span class="tag ok">ready</span></div><div class="kv"><span>Windows Server 2025 eval<span class="sub">prepared · 5.4 GB</span></span><span class="tag ok">ready</span></div></div></div>
      <div class="card"><div class="card-h">${icon("key")}License key pool<span class="spacer"></span><button class="btn sm">${icon("plus", "sm")}Add key</button></div><div class="card-b">${key("MAK key A · Windows 11 Pro", 7, 10, "…QK7-8JH2 · 7 activations used")}${key("MAK key B · Windows 11 Pro", 2, 5, "…M4X-T9PD")}<div class="kv"><span>Retail key<span class="sub">…YV3-2BQF · single machine</span></span><span class="tag">unassigned</span></div></div></div>
      <div class="card"><div class="card-h">${icon("shield")}Guests &amp; activation</div><div class="card-b">
        <div class="kv"><span class="mono" style="font-size:12px">alice/win11-eval<span class="sub" style="font-family:var(--font-ui)">evaluation · grace 29 days</span></span><span class="tag info">grace</span></div>
        <div class="kv"><span class="mono" style="font-size:12px">bob/win-qa<span class="sub" style="font-family:var(--font-ui)">activated with key A</span></span><span class="tag ok">activated</span></div>
        <div class="kv"><span>1 retained machine identity<span class="sub">reusable baseline · vTPM kept</span></span><button class="btn sm ghost">Release</button></div>
        <div class="kv" style="background:var(--err-soft)"><span><b style="color:var(--err)">0xC004C008</b> key has exceeded its unlock limit<span class="sub">2 days ago · key B was tried, then fell back</span></span><button class="btn sm" data-do="lic">Retry with key A</button></div></div></div></div>`;
  }

  function viewPolicy() {
    const preview = { "30m": "would save <b>alice/bench</b> (12 GB) and <b>bob/win-qa</b> (8 GB) now", "60m": "would save <b>alice/bench</b> (12 GB) now", "3h": "no VM affected now · alice/bench saves in 55 min", never: "nothing is saved for idleness; pressure saves still apply" }[H.idlePolicy];
    const row = (k, l, ctl, scope, prev) => `<div class="set-r" style="grid-template-columns:220px 1fr 200px 80px"><span class="k">${k}</span><span class="l">${l}${prev ? "<small style='white-space:normal;color:var(--accent)'>" + prev + "</small>" : ""}</span><span class="v">${ctl}</span><span class="s"><span class="tag scope-live">${scope}</span></span></div>`;
    return `<div class="hp-mainc" style="flex:1"><div class="tbar"><label class="q">${icon("search", "sm")}<input placeholder="Filter host policy…" spellcheck="false"></label><span class="dim" style="font-size:11px">each change previews who it affects before you save</span><span class="spacer"></span><button class="btn sm ghost">${icon("code", "sm")}Edit raw JSON section…</button><button class="btn sm primary">Save</button></div>
      <div class="set-list scroll"><div class="set-g">Idle</div>
      ${row("idle.defaultAction", "Save idle VMs after", `<select class="input" id="hpIdle">${["30m", "60m", "3h", "never"].map((o) => `<option ${H.idlePolicy === o ? "selected" : ""}>${o}</option>`).join("")}</select>`, "live", preview)}
      ${row("idle.userMax", "Longest idle a user may choose", '<input class="input mono" value="12h">', "live")}
      <div class="set-g">Memory pressure</div>
      ${row("pressure.elevatedAt", "Elevated when resident exceeds", '<input class="input mono" value="85%">', "live", "now: resident 96 GB = 75% of 128 · committed 111%")}
      ${row("pressure.saveIdleAfter", "When elevated, save VMs idle for", '<input class="input mono" value="30m">', "live")}
      ${row("capacity.mode", "Capacity admission", '<select class="input"><option selected>enforce</option><option>observe</option></select>', "live", "enforce: new VMs refused above the 118 GB admission line")}
      ${row("capacity.admissionGB", "Admission line", '<input class="input mono" value="118">', "live")}
      <div class="set-g">User defaults</div>
      ${row("users.default.ramGB", "RAM allowance", '<input class="input mono" value="24">', "live", "4 users inherit this")}
      ${row("users.default.vcpu", "vCPU allowance", '<input class="input mono" value="8">', "live")}
      ${row("leases.default", "Child lease default / max", '<input class="input mono" value="8h / 72h">', "live")}
      ${row("forwards.hostDefault", "Host forwards", '<select class="input"><option selected>allowed</option><option>refused</option></select>', "live")}</div></div>`;
  }

  function viewService() {
    const blocking = JOBS.filter((j) => j.state === "running");
    return `<div class="hcards scroll" style="flex:1">
      <div class="card span2"><div class="card-h">${icon("upd")}Host service update<span class="spacer"></span><span class="mono muted">1.14.2 → 1.15.0</span></div>
        <div class="phase"><span class="done">✓ Check</span><span class="cur">Stage</span><span>Apply</span><span>Verify</span></div>
        <div class="card-b"><div class="kv"><span>Blocking jobs<span class="sub">Apply waits for these, or cancels them if you choose</span></span><span>${blocking.length ? blocking.map((j) => '<span class="tag info">' + j.title + " · " + j.pct + "%</span>").join(" ") : '<span class="tag ok">none</span>'}</span></div>
        <div class="kv"><span>VMs keep running<span class="sub">the service restarts for about 20 s, then this window reconnects</span></span><span class="tag ok">safe</span></div></div>
        <div class="insp-acts" style="border-top:1px solid var(--line)"><button class="btn primary" data-do="stage">${icon("download")}Stage 1.15.0</button><button class="btn" ${blocking.length ? "disabled" : ""}>Apply</button><span class="spacer"></span><button class="btn ghost sm">Resume</button><button class="btn ghost sm">Cancel</button><button class="btn ghost sm">Resolve…</button></div></div>
      <div class="card"><div class="card-h">${icon("history")}Update history</div><div class="card-b"><div class="kv"><span class="mono">1.14.2</span><span class="muted">Sep 02 · ok</span></div><div class="kv"><span class="mono">1.14.1</span><span class="muted">Aug 20 · rolled back, then ok</span></div><div class="kv"><span class="mono">1.14.0</span><span class="muted">Aug 11 · ok</span></div></div></div>
      <div class="card"><div class="card-h">${icon("server")}Host</div><div class="card-b"><div class="kv"><span>CPU</span><span class="mono">32 cores</span></div><div class="kv"><span>Nested virtualization</span><span class="tag ok">available</span></div><div class="kv"><span>Config</span><span class="mono muted">C:\\ProgramData\\constructd</span></div></div>
        <details class="caps"><summary>${icon("right", "sm")}Backend capabilities (14)</summary><div class="card-b">${["child VMs", "Windows guests", "save state", "memory pressure save", "host forwards", "relayed network", "direct network", "checkpoints", "vTPM", "license reuse"].map((c) => `<div class="kv"><span>${c}</span><span class="tag ok">yes</span></div>`).join("")}</div></details></div></div>`;
  }

  function renderBottom() {
    const b = $("#hpBottom");
    b.classList.toggle("off", !H.bottom);
    if (!H.bottom) return;
    const pr2 = problems();
    const tabs = [["jobs", "Jobs", JOBS.filter((j) => j.state !== "done").length, JOBS.some((j) => j.state === "failed")], ["audit", "Audit", AUDIT.length], ["problems", "Problems", pr2.length, true]];
    let body = "";
    if (H.btab === "jobs") body = JOBS.map((j) => `<div class="job"><span>${j.state === "running" ? '<span class="dot creating"></span>' : j.state === "failed" ? '<span class="dot err"></span>' : j.state === "done" ? '<span class="dot running" style="box-shadow:none"></span>' : '<span class="dot off"></span>'}</span>
      <span><b style="font-weight:550">${j.title}</b> <span class="dim" style="font-size:11px">· ${j.phase}</span></span><span>${j.state === "running" ? '<span class="meter"><i style="width:' + j.pct + '%"></i></span>' : ""}</span><span class="t">${j.state} ${j.state === "running" ? j.pct + "%" : ""} · ${j.t}</span>
      <span>${j.state === "failed" ? `<button class="btn sm" data-do="retry">${icon("cycle", "sm")}Retry</button> <button class="btn sm ghost">Log</button>` : j.state === "queued" ? '<button class="btn sm ghost">Cancel</button>' : j.state === "running" ? '<button class="btn sm ghost">Log</button>' : ""}</span></div>`).join("");
    else if (H.btab === "audit") body = AUDIT.map((a) => `<div class="log-l"><span class="t">${a[0]}</span><span class="who">${a[1]}</span><span>${a[2]}</span><span><button class="btn sm ghost" data-vm-open="${a[3]}">${a[3]}</button></span></div>`).join("");
    else body = pr2.map((p) => `<div class="job" style="grid-template-columns:18px 1fr auto"><span style="color:var(--${p.k})">${icon("alert", "sm")}</span><span><b style="font-weight:550">${p.t}</b> <span class="dim" style="font-size:11px">· ${p.d}</span></span><span><button class="btn sm" data-fix="${p.id}">${p.fix}</button></span></div>`).join("") || '<div class="pal-empty">No problems. Health is ok.</div>';
    b.innerHTML = `<div class="bp-tabs">${tabs.map(([id, l, n, e]) => `<button class="t" aria-selected="${H.btab === id}" data-btab="${id}">${l}<span class="cnt${e && n ? " err" : ""}">${n}</span></button>`).join("")}<span class="r"><span class="dim" style="font-size:11px">live · 5 s</span><button class="btn sm ghost icon" data-do="bottom" title="Hide panel (Ctrl+J)">${icon("x", "sm")}</button></span></div><div class="bp-body scroll">${body}</div>`;
  }

  function renderStatus() {
    const pr2 = problems();
    $("#hpStatus").innerHTML = `<button data-btabgo="problems">${pr2.length ? '<span class="dot warn"></span>' + pr2.length + " problems" : '<span class="dot running"></span>healthy'}</button><button data-btabgo="jobs">${icon("layers", "sm")}${JOBS.filter((j) => j.state === "running").length} running · ${JOBS.filter((j) => j.state === "queued").length} queued</button><span>${icon("vm", "sm")}${VMS.filter((v) => v.state === "running").length} running · ${VMS.filter((v) => v.state === "saved").length} saved</span><span>pressure: ${H.pressure}</span><span class="spacer"></span><span>buildbox.example.local:7462 · pinned cert</span><button class="acc" data-do="svc">${icon("upd", "sm")}constructd 1.15.0 available</button>`;
  }

  function render() {
    renderTabs(); renderKpi(); renderBottom(); renderStatus();
    const keep = document.activeElement && document.activeElement.id === "hpQ";
    $("#hpMain").innerHTML = { machines: viewMachines, users: viewUsers, usage: viewUsage, media: viewMedia, policy: viewPolicy, service: viewService }[H.tab]();
    if (keep) { const q = $("#hpQ"); if (q) { q.focus(); q.setSelectionRange(q.value.length, q.value.length); } }
    const all = $("#hpAll"); if (all) { const l = visibleVms(); all.indeterminate = l.some((v) => H.checked.has(v.id)) && !l.every((v) => H.checked.has(v.id)); }
  }
  window.OL.hostRender = render;
  window.OL.H = H;

  function openVm(id) { H.tab = "machines"; H.open = id; H.dtab = "overview"; if (!visibleVms().some((v) => v.id === id)) { H.filter = "all"; H.q = ""; } render(); }
  window.OL.hostOpenVm = openVm;
  function retryJob() {
    const j = JOBS.find((x) => x.id === "j3");
    j.state = "running"; j.pct = 10; j.phase = "retrying with host credentials"; render(); T("Retrying delete child bob/win-qa", "ok");
    let p = 10; const t = setInterval(() => { p += 30; j.pct = Math.min(p, 100); if (p >= 100) { clearInterval(t); j.state = "done"; j.phase = "deleted · key A activation released"; const v = VMS.findIndex((x) => x.id === "bob/win-qa"); if (v >= 0) VMS.splice(v, 1); if (H.open === "bob/win-qa") H.open = null; H.checked.delete("bob/win-qa"); T("bob/win-qa deleted · lease problem resolved", "ok"); } render(); }, 700);
  }
  window.OL.hostRetry = retryJob;

  win.addEventListener("click", (e) => {
    const t = e.target;
    const tab = t.closest("[data-tab]"); if (tab) { H.tab = tab.dataset.tab; return render(); }
    if (t.closest("#hpCmd")) return openPal();
    if (t.closest("#hpRefresh")) return T("Refreshed · sampled just now", "ok");
    if (t.closest("#hpUpd")) { H.tab = "service"; return render(); }
    if (t.closest("#hpHost")) { dropdown(t.closest("#hpHost"), `<div class="dd-h">Hosts you administer</div><div class="dd-i active" data-v="buildbox"><span class="dot running"></span><span>buildbox<small>buildbox.example.local:7462</small></span><span class="r">1.14.2</span></div><div class="dd-sep"></div><div class="dd-i" data-v="add">${icon("plus")}<span>Connect another host…</span><span></span></div>`, () => {}); return; }
    const k = t.closest(".hk[data-k]");
    if (k) { const m = { health: () => { H.bottom = true; H.btab = "problems"; }, version: () => { H.tab = "service"; }, pressure: () => { H.tab = "machines"; H.filter = "pressure"; }, jobs: () => { H.bottom = true; H.btab = "jobs"; }, disk: () => { H.tab = "service"; } }[k.dataset.k]; if (m) { m(); render(); } return; }
    const f = t.closest("[data-f]"); if (f) { H.filter = f.dataset.f; H.q = ""; return render(); }
    const ow = t.closest("[data-owner]"); if (ow) { if (H.tab !== "machines" || t.closest(".props")) { H.tab = "users"; H.openUser = ow.dataset.owner; } else { H.filter = "all"; H.q = H.q === "owner:" + ow.dataset.owner ? "" : "owner:" + ow.dataset.owner; } return render(); }
    const so = t.closest("[data-sort]"); if (so) { H.sort = [so.dataset.sort, H.sort[0] === so.dataset.sort ? -H.sort[1] : -1]; return render(); }
    if (t.id === "hpAll") { const l = visibleVms(); if (t.checked) l.forEach((v) => H.checked.add(v.id)); else l.forEach((v) => H.checked.delete(v.id)); return render(); }
    const ck = t.closest("[data-chk]"); if (ck) { if (ck.checked) H.checked.add(ck.dataset.chk); else H.checked.delete(ck.dataset.chk); return render(); }
    const bk = t.closest("[data-bulk]");
    if (bk) {
      const ids = [...H.checked], a = bk.dataset.bulk;
      if (a === "clear") { H.checked.clear(); return render(); }
      if (a === "delete") return confirmDelete(ids, () => { ids.forEach((id) => { const i = VMS.findIndex((v) => v.id === id); if (i >= 0) VMS.splice(i, 1); }); H.checked.clear(); if (ids.includes(H.open)) H.open = null; render(); T("Deleted " + ids.length + " VM(s) · audit entry written", "warn"); });
      if (a === "save") { ids.forEach((id) => { const v = VMS.find((x) => x.id === id); if (v.state === "running") { v.state = "saved"; v.why = "admin"; v.act = ""; } }); H.checked.clear(); render(); return T("Saved " + ids.length + " VM(s) · owners notified", "ok"); }
      if (a === "shutdown") { ids.forEach((id) => { const v = VMS.find((x) => x.id === id); v.state = "off"; v.act = ""; }); H.checked.clear(); render(); return T("Shut down " + ids.length + " VM(s)", "ok"); }
      if (a === "lease") { ids.forEach((id) => { const v = VMS.find((x) => x.id === id); if (v.lease) { v.lease = "8h"; v.overdue = false; } }); render(); return T("Leases extended", "ok"); }
      return T(a + " for " + ids.length + " VM(s)", "ok");
    }
    const dt = t.closest("[data-dtab]"); if (dt) { H.dtab = dt.dataset.dtab; return render(); }
    const bt = t.closest("[data-btab]"); if (bt) { H.btab = bt.dataset.btab; return render(); }
    const bg = t.closest("[data-btabgo]"); if (bg) { H.bottom = true; H.btab = bg.dataset.btabgo; return render(); }
    const vo = t.closest("[data-vm-open]"); if (vo) { const id = vo.dataset.vmOpen; if (VMS.some((v) => v.id === id)) openVm(id); else if (USERS.some((u) => u.id === id)) { H.tab = "users"; H.openUser = id; render(); } return; }
    const fx = t.closest("[data-fix]");
    if (fx) { const id = fx.dataset.fix; if (id === "j3") retryJob(); else if (id === "bob/win-qa") openVm(id); else if (id === "lic") { H.tab = "media"; render(); } else { T("Re-inventory queued for dana/agent-vm-2", "ok"); } return; }
    const va = t.closest("[data-vmact]");
    if (va) { const v = VMS.find((x) => x.id === H.open), a = va.dataset.vmact;
      if (a === "save") { v.state = "saved"; v.why = "admin"; v.act = ""; } if (a === "shutdown") { v.state = "off"; v.act = ""; } if (a === "start") { v.state = "running"; v.act = "idle"; v.idle = 0; v.why = null; v.flags = v.flags.filter((x) => x !== "saved by pressure"); } if (a === "lease") { v.lease = "8h"; v.overdue = false; }
      render(); return T(v.id + ": " + a + " done", "ok"); }
    const pe = t.closest("[data-per]"); if (pe) { H.period = pe.dataset.per; return render(); }
    const u = t.closest("tr[data-user]"); if (u) { H.openUser = u.dataset.user; return render(); }
    const d = t.closest("[data-do]");
    if (d) {
      const a = d.dataset.do;
      if (a === "bottom") H.bottom = !H.bottom;
      else if (a === "closedetail") H.open = null;
      else if (a === "closeuser") H.openUser = null;
      else if (a === "retry") return retryJob();
      else if (a === "svc") H.tab = "service";
      else if (a === "vmmore") { dropdown(d, `<div class="dd-i" data-v="t">${icon("key")}<span>Rotate VM token</span><span></span></div><div class="dd-i" data-v="p">${icon("link")}<span>Make public</span><span></span></div><div class="dd-i" data-v="s">${icon("gear")}<span>VM settings (network, CPU)…</span><span></span></div><div class="dd-i" data-v="l">${icon("clock")}<span>Change lifetime…</span><span></span></div><div class="dd-sep"></div><div class="dd-i" data-v="del" style="color:var(--err)">${icon("trash")}<span>Delete… (type name)</span><span></span></div>`, (v) => { if (v === "del") confirmDelete([H.open], () => { const i = VMS.findIndex((x) => x.id === H.open); VMS.splice(i, 1); H.open = null; render(); T("VM deleted · audit entry written", "warn"); }); else T("Done", "ok"); }, "right"); return; }
      else if (a === "offboard") { $("#hpOverlay").classList.add("on"); pal.openConfirm({ confirm: { name: H.openUser, verb: "Offboard", icon: "user", title: "Offboard " + H.openUser, consequence: "Disables the user, revokes every token, and deletes their VMs and children after the cascade preview below.", impact: [["VMs", VMS.filter((v) => v.owner === H.openUser).map((v) => '<span class="mono">' + v.id + "</span>").join(", ") || "none"], ["Tokens", "all revoked on the next request"], ["Licences", "keys held by their Windows guests are released"], ["Kept", "usage history and audit entries"]], run: () => { const us = USERS.find((x) => x.id === H.openUser); us.flags = ["disabled"]; render(); T(H.openUser + " offboarded", "warn"); } } }); $("#hpPalInput").focus(); return; }
      else if (a === "issuetoken") return T("Token issued: <span class='mono'>usr_7f3c…</span> shown once · copied", "ok");
      else if (a === "saveuser") return T("Saved role, allowance and flags for " + H.openUser, "ok");
      else if (a === "lic") return T("Activation retried with key A · 8/10 used", "ok");
      else if (a === "stage") return T("1.15.0 downloaded and verified · Apply waits for 1 job", "ok");
      else if (a === "savefilter") return T("Filter saved as “" + (H.q || H.filter) + "”", "ok");
      else if (a === "rotate") return T("VM token rotated", "ok");
      else return T(a, "ok");
      return render();
    }
    const row = t.closest("tr.row[data-vm]");
    if (row && !t.closest("input,button")) { H.open = H.open === row.dataset.vm ? null : row.dataset.vm; H.dtab = "overview"; render(); }
  });
  win.addEventListener("input", (e) => { if (e.target.id === "hpQ") { H.q = e.target.value; render(); } });
  win.addEventListener("change", (e) => { if (e.target.id === "hpIdle") { H.idlePolicy = e.target.value; render(); } });

  let gP = 0;
  window.OL.hostKey = (e) => {
    const typing = /INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName);
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); return openPal(); }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "j") { e.preventDefault(); H.bottom = !H.bottom; return render(); }
    if (typing || $("#hpOverlay").classList.contains("on")) return;
    if (e.key === "Escape") { if (H.checked.size) H.checked.clear(); else H.open = null; return render(); }
    if (e.key === "/") { const q = $("#hpQ"); if (q) { e.preventDefault(); q.focus(); } return; }
    if (e.key === "g") { gP = Date.now(); return; }
    if (Date.now() - gP < 900) { const tb = TABS.find((x) => x[2] === e.key); gP = 0; if (tb) { H.tab = tb[0]; render(); } }
  };
  render();
})();
