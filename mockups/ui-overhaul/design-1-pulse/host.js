/* Surface 3: shared host admin panel ("Watchtower" frame) */
"use strict";
(function () {
  const FEED = [
    { id: "job", sev: "err", icon: "err", title: "Child delete failed", ent: "bob/win-qa", desc: "delete child bob/win-qa failed at 09:14: access denied on the VHDX (file still in use by vmwp.exe)", fixes: [["Retry delete", "Retried delete child bob/win-qa · succeeded"]], open: ["job", "delete-win-qa"], t: "09:14" },
    { id: "lease", sev: "warn", icon: "clock", title: "Lease overdue by 2 h", ent: "bob/win-qa", desc: "Child VM, 8 GB, still running. bob was notified at 12:08 and hasn't replied.", fixes: [["Shut down", "Shut down bob/win-qa (lease overdue)"], ["Extend 4 h", "Extended lease of bob/win-qa by 4 h"]], open: ["vm", "bob/win-qa"], t: "12:08" },
    { id: "pressure", sev: "warn", icon: "ram", title: "Memory pressure saved a VM", ent: "mara/scratch-3", desc: "Resident 96 GB of 128, committed 142 GB (111%). Next candidate: alice/bench, idle 2 h, 12 GB.", fixes: [["Save alice/bench · frees 12 GB", "Saved alice/bench, freeing 12 GB · pressure back to normal"], ["Acknowledge", "Acknowledged pressure save of mara/scratch-3"]], open: ["vm", "mara/scratch-3"], t: "13:48" },
    { id: "act", sev: "err", icon: "key", title: "Windows activation failed", ent: "alice/win11-eval", desc: "0xC004C008: key A exceeded its unlock limit (2 days ago). The guest is in grace, 29 days left. The rebuild in progress will retry key A.", fixes: [["Assign key B", "Assigned key B to alice/win11-eval (3 of 5 used)"]], open: ["lic", ""], t: "2 d" },
    { id: "cred", sev: "warn", icon: "shield", title: "Legacy credential still accepted", ent: "mara", desc: "A scoped token was issued at 13:22, but the old shared credential still works.", fixes: [["Revoke legacy credential", "Revoked legacy credential for mara"]], open: ["user", "mara"], t: "13:22" },
    { id: "inv", sev: "info", icon: "info", title: "Inventory incomplete", ent: "dana/agent-vm-2", desc: "The guest agent hasn't reported disks or IP for 40 min. Health stays OK.", fixes: [["Rescan", "Rescanned inventory of dana/agent-vm-2 · complete"]], open: ["vm", "dana/agent-vm-2"], t: "13:28" },
    { id: "upd", sev: "info", icon: "upd", title: "constructd 1.15.0 is available", ent: "service", desc: "Installed 1.14.2. One running job would block Apply right now. VMs keep running during the update.", fixes: [], go: "service", t: "today" },
  ];
  const VMS = [
    { id: "alice/work-vm", owner: "alice", state: "running", ram: 16, act: "agent busy", kind: "primary", cost: 168.2 },
    { id: "alice/bench", owner: "alice", state: "idle", ram: 12, act: "idle 2 h · next pressure save", kind: "primary", cost: 44.2 },
    { id: "alice/win11-eval", owner: "alice", state: "creating", ram: 8, act: "creating · 62%", kind: "child", lease: "3 d", cost: 0 },
    { id: "bob/api-vm", owner: "bob", state: "running", ram: 12, act: "agent idle 14 min", kind: "primary", cost: 54.1 },
    { id: "bob/win-qa", owner: "bob", state: "overdue", ram: 8, act: "lease overdue 2 h", kind: "child", lease: "−2 h", cost: 0 },
    { id: "dana/dev-2", owner: "dana", state: "saved", ram: 12, act: "saved by idle policy 41 min ago", kind: "primary", cost: 31.8 },
    { id: "dana/agent-vm-2", owner: "dana", state: "running", ram: 16, act: "inventory incomplete", kind: "primary", cost: 54.5 },
    { id: "mara/scratch-3", owner: "mara", state: "saved-p", ram: 16, act: "saved by memory pressure 22 min ago", kind: "primary", cost: 139.9 },
  ];
  const USERS = [
    { id: "alice", role: "dev", prim: [2, 3], child: [1, 4], ram: [28, 32], cpu: [12, 16], cost: 212.4, flags: "" },
    { id: "bob", role: "dev", prim: [1, 2], child: [2, 2], ram: [20, 24], cpu: [8, 8], cost: 54.1, flags: "lease overdue" },
    { id: "dana", role: "admin", prim: [2, null], child: [1, null], ram: [28, null], cpu: [12, null], cost: 86.3, flags: "" },
    { id: "mara", role: "contractor", prim: [1, 1], child: [1, 1], ram: [16, 16], cpu: [4, 4], cost: 139.9, flags: "legacy credential" },
    { id: "jonas", role: "dev", prim: [0, 2], child: [0, 2], ram: [0, 24], cpu: [0, 8], cost: 0, flags: "disabled" },
  ];
  const AUDIT = [
    ["13:51", "alice", "Created child win11-eval", "alice/win11-eval"],
    ["13:48", "system", "Memory pressure saved mara/scratch-3", "mara/scratch-3"],
    ["13:22", "dana", "Rotated token for mara", "mara"],
    ["12:08", "system", "Lease overdue notice sent to bob", "bob/win-qa"],
    ["09:14", "system", "Job failed: delete child bob/win-qa (access denied)", "bob/win-qa"],
    ["08:02", "dana", "Set capacity mode to enforce", "host"],
  ];
  const H = { page: "attention", resolved: [], resolving: null, drawer: null, mtab: "vms", vmFilter: "all", svc: 1, svcMode: "wait", period: "month", dialog: null, policyDirty: false, idleMin: 60, afilter: "all" };
  const NAV = [["attention", "pulse", "Attention"], ["machines", "server", "Machines"], ["people", "users", "People"], ["spend", "dollar", "Spend"], ["licences", "key", "Licences"], ["policy", "sliders", "Policy"], ["service", "upd", "Service"], ["audit", "audit", "Audit"]];
  const open = () => FEED.filter((f) => !H.resolved.find((r) => r.id === f.id));

  const stateChip = (s) => ({
    running: `<span class="chip ok"><span class="dot run sm"></span>Running</span>`,
    idle: `<span class="chip"><span class="dot run sm" style="opacity:.5"></span>Idle</span>`,
    creating: `<span class="chip info"><svg class="i s12 spin" viewBox="0 0 24 24"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5"/></svg>Creating</span>`,
    overdue: `<span class="chip err">${ic("clock", "s12")}Lease overdue</span>`,
    saved: `<span class="chip"><span class="dot saved sm"></span>Saved</span>`,
    "saved-p": `<span class="chip warn"><span class="dot saved sm"></span>Saved · pressure</span>`,
  }[s]);

  /* ---------------- frame ---------------- */
  function render(sub) {
    if (sub) {
      if (sub === "resolved") { H.resolved = [{ id: "job", text: FEED[0].fixes[0][1], t: "14:06" }, { id: "inv", text: FEED[5].fixes[0][1], t: "14:07" }]; H.page = "attention"; }
      else if (sub === "drawer") { H.page = "people"; H.drawer = ["user", "alice"]; }
      else if (sub === "vm") { H.page = "machines"; H.drawer = ["vm", "bob/win-qa"]; }
      else if (sub === "apply") { H.page = "service"; H.svc = 2; }
      else H.page = sub;
      App.sub = null; history.replaceState(null, "", "#host/" + H.page);
    }
    const n = open().length;
    return `<div class="win" id="hwin">
      <div class="titlebar">${ic("server")}<span class="ttl">Construct Host <span>· buildbox</span></span>
        <span class="caps"><button class="cap">${ic("min", "s12")}</button><button class="cap">${ic("max", "s12")}</button><button class="cap x">${ic("x", "s12")}</button></span></div>
      <div class="win-body host">
        <nav class="nav">
          <div class="inst-switch" style="cursor:default"><span class="avatar">${ic("server", "s20")}<span class="dot warn"></span></span><span class="t"><b>buildbox</b><span>constructd 1.14.2 · 128 GB</span></span></div>
          <div class="search"><input class="input" id="hsearch" placeholder="Search VMs, people, jobs" autocomplete="off">${ic("search")}</div>
          ${NAV.map(([k, i, l]) => `<button class="nav-item ${H.page === k ? "on" : ""}" data-page="${k}">${ic(i)}${l}${k === "attention" && n ? `<span class="cnt b">${n}</span>` : k === "machines" ? '<span class="cnt">8</span>' : k === "people" ? '<span class="cnt">5</span>' : k === "service" && H.svc < 4 ? '<span class="cnt w">1.15.0</span>' : k === "licences" && !H.resolved.find((r) => r.id === "act") ? '<span class="cnt w">1 failed</span>' : ""}</button>`).join("")}
          <div class="nav-spacer"></div>
          <div class="faint" style="font-size:12px;padding:8px 12px">Signed in as <b style="color:var(--text-2)">dana</b> · admin</div>
        </nav>
        <div class="content" id="hcontent">${PAGES[H.page]()}</div>
        ${H.drawer ? drawer() : ""}${H.dialog ? dialog() : ""}
      </div></div>`;
  }
  const header = (t, right = "", crumb = "") => `<div class="page-h"><div class="grow">${crumb ? `<div class="crumb">${crumb}</div>` : ""}<h1>${t}</h1></div>${right}</div>`;
  const secH = (t, right = "") => `<div class="sec-h"><h2>${t}</h2><span class="grow"></span>${right}</div>`;

  function ramCard(compact) {
    const S = 150, pct = (v) => (v / S) * 100;
    return `<div class="card ram"><div class="card-h">${ic("ram")}Memory<span class="grow"></span><span class="chip warn">${ic("warn", "s12")}Pressure elevated</span><span class="faint" style="font-weight:400">sampled 15 s ago</span></div>
      <div class="ram-bar" role="img" aria-label="96 GB resident of 128 GB, committed 142 GB, admission line 118 GB">
        <span class="phys" style="width:${pct(128)}%" data-tip="<b>Physical RAM</b> · 128 GB"></span>
        <span class="res" style="width:${pct(96)}%" data-tip="<b>VMs resident</b> · 96 GB"></span>
        <span class="over" style="left:${pct(128)}%;width:${pct(14)}%" data-tip="<b>Over-committed</b> · 14 GB beyond physical"></span>
        <span class="tick" style="left:${pct(118)}%"><span>admission 118</span></span>
        <span class="tick commit" style="left:${pct(142)}%"><span>committed 142 (111%)</span></span>
      </div>
      <div class="legend"><span><i style="background:var(--series)"></i>VMs resident 96 GB</span><span><i style="background:var(--series-soft)"></i>Free 32 GB</span><span><i style="background:repeating-linear-gradient(135deg,var(--warn) 0 2px,transparent 2px 5px)"></i>Committed beyond physical</span>${compact ? "" : '<span>Last pressure save 22 min ago (mara/scratch-3)</span>'}</div></div>`;
  }

  /* ---------------- attention ---------------- */
  function attention() {
    const items = open();
    return `<div class="page">
      ${header("Attention", `<span class="faint" style="font-size:12px;padding-bottom:6px">buildbox · updated 5 s ago</span>`)}
      <div class="strip">
        <button class="tile" data-page="machines"><span class="tl">${ic("okc", "s12")}Health</span><span class="tv"><span class="dot run sm"></span>OK</span><span class="ts">${H.resolved.find((r) => r.id === "inv") ? "no notes" : "1 note: inventory incomplete"}</span></button>
        <button class="tile" data-page="service"><span class="tl">${ic("upd", "s12")}Service</span><span class="tv">${H.svc >= 4 ? "1.15.0" : "1.14.2"}${H.svc < 4 ? '<span class="badge new">1.15.0 ready</span>' : ""}</span><span class="ts">constructd · Hyper-V</span></button>
        <button class="tile" data-page="policy"><span class="tl">${ic("shield", "s12")}Capacity mode</span><span class="tv">Enforce</span><span class="ts">admission line 118 GB</span></button>
        <button class="tile" data-page="machines"><span class="tl">${ic("disk", "s12")}Disk D:</span><span class="tv">61% used</span><span class="ts">3.6 TB · 1.4 TB free</span></button>
      </div>
      ${ramCard()}
      <div class="sec">${secH(`Needs attention · ${items.length}`, `<span class="stale">sorted by severity, then age</span>`)}
        <div class="stack">${items.length ? items.map(card).join("") : `<div class="infobar ok"><span class="ib-ic">${ic("okc")}</span><div class="ib-body"><b>All clear.</b> Nothing on buildbox needs you right now.</div></div>`}</div></div>
      <div class="grid2" style="grid-template-columns:1.2fr 1fr">
        <div class="sec">${secH("Running jobs", `<button class="link" data-page="machines" data-mtab="jobs">All jobs</button>`)}
          <div class="card" style="padding:0">
            <div class="job"><svg class="i spin" viewBox="0 0 24 24" style="color:var(--accent-text)"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5"/></svg><span>Create child <b>win11-eval</b> for alice</span><div class="progress"><i style="width:62%"></i></div><span class="faint num">62%</span></div>
            <div class="job">${ic("hourglass", "faint")}<span>Reprovision <b>dev-2</b> for dana</span><span class="faint" style="font-size:12px">queued behind 1 job</span><button class="btn sm subtle">Cancel</button></div>
          </div></div>
        <div class="sec">${secH("Resolved today", `<button class="link" data-page="audit">Audit log</button>`)}
          <div class="card" style="padding:4px">${H.resolved.length ? H.resolved.slice().reverse().map((r) => `<div class="resolved">${ic("okc")}<span>${r.text}<span class="faint"> · you</span></span><span class="rt">${r.t}</span></div>`).join("") : `<div class="empty-note" style="padding:12px">When you fix something in the feed, it shows up here and in the audit log.</div>`}
          <div class="resolved">${ic("okc")}<span>Rotated token for mara<span class="faint"> · dana</span></span><span class="rt">13:22</span></div></div></div>
      </div></div>`;
  }
  function card(f) {
    return `<div class="att ${f.sev} ${H.resolving === f.id ? "resolving" : ""}" data-feed="${f.id}">
      <span class="aic">${ic(f.icon)}</span>
      <div><div class="at">${f.title}<button class="chip" data-open="${f.open ? f.open.join("|") : ""}" ${f.go ? `data-page="${f.go}"` : ""}>${f.ent}</button></div><div class="ad">${f.desc} <span class="faint">· ${f.t}</span></div></div>
      <div class="aa">${f.fixes.map(([l], i) => `<button class="btn sm" data-fix="${f.id}" data-i="${i}">${l}</button>`).join("")}${f.go ? `<button class="btn sm accent" data-page="${f.go}">Review update</button>` : ""}<button class="btn sm icon subtle" data-open="${f.open ? f.open.join("|") : ""}" ${f.go ? `data-page="${f.go}"` : ""} title="Open details">${ic("chevr", "s12")}</button></div></div>`;
  }

  /* ---------------- machines ---------------- */
  function machines() {
    const tabs = `<div class="tabs">${[["vms", "Virtual machines"], ["jobs", "Jobs"], ["media", "Media"]].map(([k, l]) => `<button data-mtab="${k}" class="${H.mtab === k ? "on" : ""}">${l}</button>`).join("")}</div>`;
    let body = "";
    if (H.mtab === "vms") {
      const F = { all: () => true, running: (v) => ["running", "idle", "overdue"].includes(v.state), saved: (v) => v.state.startsWith("saved"), child: (v) => v.kind === "child", attn: (v) => ["overdue", "saved-p"].includes(v.state) || v.act.includes("incomplete") };
      const cnt = (k) => VMS.filter(F[k]).length;
      body = `${ramCard(true)}
        <div class="filters">${[["all", "All"], ["running", "Running"], ["saved", "Saved"], ["child", "Children"], ["attn", "Needs attention"]].map(([k, l]) => `<button class="chip ${H.vmFilter === k ? "ok" : ""}" data-vf="${k}">${l} · ${cnt(k)}</button>`).join("")}<span class="grow"></span><select class="select" style="height:28px;font-size:12.5px"><option>All owners</option>${USERS.map((u) => `<option>${u.id}</option>`).join("")}</select><select class="select" style="height:28px;font-size:12.5px"><option>Sort: RAM</option><option>Sort: next idle action</option><option>Sort: name</option></select></div>
        <table class="t"><thead><tr><th>VM</th><th>State</th><th class="r">RAM</th><th>Activity</th><th>Lease</th><th class="r">Month</th><th></th></tr></thead><tbody>
        ${VMS.filter(F[H.vmFilter]).sort((a, b) => b.ram - a.ram).map((v) => `<tr class="click" data-open="vm|${v.id}"><td><span class="cellname">${v.kind === "child" ? ic("child", "faint") : ic("server", "faint")}${v.id.split("/")[1]}<small>${v.owner}</small></span></td><td>${stateChip(v.state)}</td><td class="r">${v.ram} GB</td><td class="muted">${v.act}</td><td class="${v.state === "overdue" ? "" : "muted"}" style="${v.state === "overdue" ? "color:var(--err-text);font-weight:600" : ""}">${v.lease || "—"}</td><td class="r">${money(v.cost)}</td><td class="r">${ic("chevr", "faint")}</td></tr>`).join("")}</tbody></table>`;
    } else if (H.mtab === "jobs") {
      body = `<table class="t"><thead><tr><th>Job</th><th>Owner</th><th>State</th><th>Started</th><th class="r"></th></tr></thead><tbody>
        <tr><td>Create child win11-eval</td><td>alice</td><td><div class="row"><div class="progress" style="width:120px"><i style="width:62%"></i></div><span class="num">62%</span></div></td><td class="muted">13:51</td><td class="r"><button class="btn sm">Cancel</button></td></tr>
        <tr><td>Reprovision dev-2</td><td>dana</td><td><span class="chip">${ic("hourglass", "s12")}Queued</span></td><td class="muted">14:01</td><td class="r"><button class="btn sm">Cancel</button></td></tr>
        <tr><td>Delete child bob/win-qa</td><td>bob</td><td>${H.resolved.find((r) => r.id === "job") ? `<span class="chip ok">${ic("check", "s12")}Succeeded on retry</span>` : `<span class="chip err">${ic("err", "s12")}Failed · access denied</span>`}</td><td class="muted">09:14</td><td class="r">${H.resolved.find((r) => r.id === "job") ? "" : `<button class="btn sm accent" data-fix="job" data-i="0">Retry</button>`}</td></tr>
        <tr><td>Memory-pressure save mara/scratch-3</td><td>system</td><td><span class="chip ok">${ic("check", "s12")}Done</span></td><td class="muted">13:48</td><td></td></tr></tbody></table>`;
    } else {
      body = `${secH("Primary ISO catalog", `<button class="btn sm">${ic("plus", "s12")}Add ISO</button>`)}
        <table class="t"><thead><tr><th>Image</th><th>Status</th><th class="r">Size</th><th class="r"></th></tr></thead><tbody>
        <tr><td class="cellname">${ic("disc")}Ubuntu 26.04 LTS server</td><td><span class="chip ok">Current default</span></td><td class="r">3.1 GB</td><td class="r"><button class="btn sm subtle">${ic("dots", "s12")}</button></td></tr>
        <tr><td class="cellname">${ic("disc")}Ubuntu 24.04.3 LTS server</td><td class="muted">Available</td><td class="r">2.9 GB</td><td class="r"><button class="btn sm">Make default</button></td></tr></tbody></table>
        ${secH("Child media", `<button class="btn sm">${ic("plus", "s12")}Add media</button>`)}
        <table class="t"><thead><tr><th>Media</th><th>Used by</th><th>Licensing</th><th class="r"></th></tr></thead><tbody>
        <tr><td class="cellname">${ic("win")}Windows 11 24H2 eval</td><td>alice/win11-eval, bob/win-qa</td><td><button class="link" data-page="licences">Key pool · 2 MAK, 1 retail</button></td><td class="r"><button class="btn sm subtle">${ic("dots", "s12")}</button></td></tr>
        <tr><td class="cellname">${ic("win")}Windows Server 2025 eval</td><td class="muted">none</td><td class="muted">Evaluation (180 days)</td><td class="r"><button class="btn sm subtle">${ic("dots", "s12")}</button></td></tr></tbody></table>`;
    }
    return `<div class="page">${header("Machines", `<button class="btn">${ic("plus")}New VM for…</button>`)}${tabs}${body}</div>`;
  }

  /* ---------------- people ---------------- */
  const bullet = (u, cap) => cap == null ? `<div class="bcell"><small>${u} · no cap</small></div>` : `<div class="bcell"><div class="bullet"><i class="${u >= cap && cap ? "full" : ""}" style="width:${cap ? Math.min(100, (u / cap) * 100) : 0}%"></i></div><small>${u} / ${cap}</small></div>`;
  function people() {
    return `<div class="page">${header("People", `<button class="btn accent" data-dlg="onboard">${ic("plus")}Add person</button>`)}
      <table class="t"><thead><tr><th>Person</th><th>Role</th><th>Primaries</th><th>Children</th><th>RAM (GB)</th><th>vCPU</th><th class="r">Month</th><th>Flags</th></tr></thead><tbody>
      ${USERS.map((u) => `<tr class="click" data-open="user|${u.id}" style="${u.flags === "disabled" ? "opacity:.6" : ""}"><td><span class="cellname">${ic("user", "faint")}${u.id}</span></td><td class="muted">${u.role}</td><td>${bullet(...u.prim)}</td><td>${bullet(...u.child)}</td><td>${bullet(...u.ram)}</td><td>${bullet(...u.cpu)}</td><td class="r">${money(u.cost)}</td>
        <td>${u.flags ? `<span class="badge ${u.flags === "disabled" ? "" : "warn"}">${u.flags}</span>` : ""}</td></tr>`).join("")}</tbody></table>
      <div class="legend"><span><i style="background:var(--series)"></i>used</span><span><i style="background:var(--series-soft)"></i>allowance</span><span><i style="background:#c98a00"></i>at allowance</span></div></div>`;
  }

  /* ---------------- spend ---------------- */
  function spend() {
    const us = USERS.slice().sort((a, b) => b.cost - a.cost), max = us[0].cost;
    const tools = [["Claude Code", 318.2], ["Codex", 121.4], ["OpenCode", 53.1]];
    return `<div class="page">${header("Spend", `<div class="seg" data-hseg="period">${[["today", "Today"], ["month", "This month"], ["all", "All time"]].map(([k, l]) => `<button data-v="${k}" class="${H.period === k ? "on" : ""}">${l}</button>`).join("")}</div><button class="btn">${ic("export")}Export CSV</button>`)}
      <div class="grid3"><div class="card"><div class="card-h">This month · all people</div><span class="hero-v">${money(492.7)}</span><span class="faint" style="font-size:12px">Sep 1–23 · 5 people · 8 VMs</span></div>
        <div class="card"><div class="card-h">Today</div><span class="bigv">${money(21.36)}</span><span class="faint" style="font-size:12px">updated 1 min ago</span></div>
        <div class="card"><div class="card-h">All time</div><span class="bigv">${money(2841.9)}</span><span class="faint" style="font-size:12px">since constructd 1.9 (Mar 2026)</span></div></div>
      <div class="grid2" style="grid-template-columns:1.3fr 1fr">
        <div class="card" id="spendUsers"><div class="card-h">By person · this month</div>${us.map((u) => `<div class="hbar" data-tip="<b>${u.id}</b> · ${money(u.cost)}" data-open="user|${u.id}" style="cursor:pointer"><span>${u.id}</span><span class="track"><i style="width:${(u.cost / max) * 100}%"></i></span><span class="v">${money(u.cost)}</span></div>`).join("")}
          <div class="faint" style="font-size:12px">No daily trend yet: constructd 1.14 only reports today, month and all time.</div></div>
        <div class="card"><div class="card-h">By tool · this month</div><table class="t" style="border:0;background:transparent"><tbody>${tools.map(([t, v]) => `<tr><td>${t}</td><td class="r">${money(v)}</td><td class="r faint">${Math.round((v / 492.7) * 100)}%</td></tr>`).join("")}</tbody></table></div>
      </div>
      <div class="sec">${secH("By VM · this month")}<table class="t"><thead><tr><th>VM</th><th>Owner</th><th class="r">Cost</th></tr></thead><tbody>${VMS.filter((v) => v.cost).sort((a, b) => b.cost - a.cost).map((v) => `<tr class="click" data-open="vm|${v.id}"><td>${v.id.split("/")[1]}</td><td class="muted">${v.owner}</td><td class="r">${money(v.cost)}</td></tr>`).join("")}</tbody></table></div></div>`;
  }

  /* ---------------- licences ---------------- */
  function licences() {
    const fixed = H.resolved.find((r) => r.id === "act");
    const key = (n, t, u, c, note) => `<div class="card"><div class="card-h">${ic("key")}${n}<span class="grow"></span><span class="badge">${t}</span></div><div class="row"><span class="bigv">${u}</span><span class="faint">of ${c} activations</span></div><div class="meter ${u / c >= 0.7 ? "warn" : ""}"><i style="width:${(u / c) * 100}%"></i></div><span class="faint" style="font-size:12px">${note}</span></div>`;
    return `<div class="page">${header("Licences", `<button class="btn">${ic("plus")}Add key</button>`)}
      ${fixed ? "" : `<div class="infobar err"><span class="ib-ic">${ic("err")}</span><div class="ib-body"><b>Activation failed 2 days ago</b> · alice/win11-eval · <span class="mono">0xC004C008</span>: key A has exceeded its unlock limit. The guest runs in grace (29 days left).</div><div class="ib-actions"><button class="btn sm accent" data-fix="act" data-i="0">Assign key B</button></div></div>`}
      <div class="grid3">${key("Key A", "MAK", 7, 10, "3 left · last attempt failed (unlock limit)")}${key("Key B", "MAK", fixed ? 3 : 2, 5, fixed ? "assigned to alice/win11-eval" : "3 left")}${key("Retail key", "Retail", 1, 1, "held by 1 retained machine identity")}</div>
      <div class="sec">${secH("Windows guests")}<table class="t"><thead><tr><th>Guest</th><th>Media</th><th>State</th><th>Key</th><th class="r"></th></tr></thead><tbody>
        <tr><td class="cellname">alice/win11-eval</td><td class="muted">Win 11 24H2 eval</td><td>${fixed ? `<span class="chip ok">${ic("check", "s12")}Activating</span>` : `<span class="chip warn">${ic("clock", "s12")}Grace · 29 days</span>`}</td><td>${fixed ? "Key B" : "Key A · failed"}</td><td class="r"><button class="btn sm">Activate again</button></td></tr>
        <tr><td class="cellname">bob/win-qa</td><td class="muted">Win 11 24H2 eval</td><td><span class="chip ok">${ic("check", "s12")}Activated</span></td><td>Key A</td><td class="r"><button class="btn sm subtle">Release on delete</button></td></tr></tbody></table></div>
      <div class="sec">${secH("Retained machine identities")}<div class="srow"><span class="si">${ic("lock", "s20")}</span><div><div class="st">win-bench-2025</div><div class="sd">Kept from a deleted child so the next build reuses its activation · retail key</div></div><div class="sc"><button class="btn sm">Release…</button></div></div></div></div>`;
  }

  /* ---------------- policy ---------------- */
  function policy() {
    const would = H.idleMin <= 120 ? `Would save <b>1 VM</b> right now: alice/bench (idle 2 h, 12 GB).` : "Would save nothing right now.";
    const row = (icon, t, d, c, pos) => `<div class="srow ${pos}"><span class="si">${ic(icon, "s20")}</span><div><div class="st">${t}</div><div class="sd">${d}</div></div><div class="sc">${c}</div></div>`;
    return `<div class="page">${header("Policy")}
      <div class="sec">${secH("Idle defaults")}
        ${row("clock", "Save idle VMs after", "Default for new VMs. People can choose a shorter time. An agent working counts as busy, even with nobody connected.", `<input class="input num-in" id="idleMin" value="${H.idleMin}" data-pol><span class="muted">min</span><select class="select" data-pol><option>Save</option><option>Shut down</option></select>`, "group-top")}
        ${row("", "Longest idle time a person may set", "", `<input class="input num-in" value="240" data-pol><span class="muted">min</span>`, "group-mid")}
        <div class="srow nested group-bot"><div class="sd" id="idlePrev">${ic("eye", "s12")} Preview: ${would}</div><div></div></div></div>
      <div class="sec">${secH("Memory pressure", `<span class="chip warn">${ic("warn", "s12")}Elevated now</span>`)}
        ${row("ram", "Start saving idle VMs at", "Resident RAM on the host. VMs are saved in order of longest idle first.", `<input class="input num-in" value="110" data-pol><span class="muted">GB</span>`, "group-top")}
        ${row("", "Stop when resident drops below", "", `<input class="input num-in" value="100" data-pol><span class="muted">GB</span>`, "group-mid")}
        ${row("", "Only VMs idle for at least", "", `<input class="input num-in" value="30" data-pol><span class="muted">min</span>`, "group-bot")}</div>
      <div class="sec">${secH("Capacity")}
        ${row("shield", "Admission mode", "Enforce refuses new VMs and resizes above the admission line. Observe only logs them.", `<div class="seg"><button>Observe</button><button class="on">Enforce</button></div>`, "group-top")}
        ${row("", "Admission line", "Committed RAM allowed before admission refuses", `<input class="input num-in" value="118" data-pol><span class="muted">GB</span>`, "group-bot")}</div>
      <div class="sec">${secH("Defaults for new people")}
        <div class="card"><div class="grid4">${[["Primaries", 2], ["Children", 2], ["RAM (GB)", 24], ["vCPU", 8]].map(([l, v]) => `<div class="field"><label>${l}</label><input class="input" value="${v}" data-pol></div>`).join("")}</div>
        <div class="grid4">${[["Storage (GB)", 400], ["Max child lease", "3 d"]].map(([l, v]) => `<div class="field"><label>${l}</label><input class="input" value="${v}" data-pol></div>`).join("")}<label class="check" style="grid-column:span 2;align-self:end;padding-bottom:6px"><input type="checkbox" checked data-pol>Allow host forwards</label></div></div></div>
      <details class="adv"><summary>${ic("code", "s20")}Raw host configuration (JSON)<span class="faint" style="font-size:12px">for fields the form doesn't cover · each section is replaced whole</span>${ic("chevd", "chev")}</summary><div class="adv-b"><select class="select" style="width:220px"><option>memoryPressure</option><option>idle</option><option>capacity</option><option>userDefaults</option><option>forwards</option></select><textarea class="code">{\n  "startSavingAtGb": 110,\n  "stopSavingBelowGb": 100,\n  "minIdleMinutes": 30\n}</textarea></div></details>
      ${H.policyDirty ? `<div class="pending-bar" style="border-color:var(--stroke-2)">${ic("sliders")}<span class="grow"><b>Unsaved policy changes.</b> <span class="muted">${would}</span></span><button class="btn subtle" data-act="poldiscard">Discard</button><button class="btn accent" data-act="polsave">Save policy</button></div>` : ""}
    </div>`;
  }

  /* ---------------- service ---------------- */
  function service() {
    const s = H.svc; // 1 checked, 2 staged, 3 applying, 4 done
    const st = (i, l) => `<div class="step ${s > i || s === 4 ? "done" : s === i ? "cur" : ""}"><i>${s > i || s === 4 ? "✓" : i + 1}</i>${l}</div>`;
    const blocking = s === 2 ? `<div class="sec">${secH("Before Apply")}
        <div class="card" style="padding:0">
          <div class="job" style="grid-template-columns:20px 1fr auto">${ic("warn", "lv-warn")}<span><b>Blocks apply:</b> create child win11-eval for alice · running 62%, about 4 min left</span><span class="chip warn">blocking</span></div>
          <div class="job" style="grid-template-columns:20px 1fr auto">${ic("hourglass", "faint")}<span>Reprovision dev-2 for dana · queued, held until the update finishes</span><span class="chip">held</span></div>
          <div class="job" style="grid-template-columns:20px 1fr auto">${ic("okc", "lv-ok")}<span>8 VMs keep running. The API and Companion connections drop for about 40 s.</span><span></span></div>
        </div>
        <label class="radio"><input type="radio" name="svm" value="wait" ${H.svcMode === "wait" ? "checked" : ""}><div><b>Wait for running jobs, then apply</b><span>Starts automatically in about 4 min. Queued jobs are held.</span></div></label>
        <label class="radio"><input type="radio" name="svm" value="cancel" ${H.svcMode === "cancel" ? "checked" : ""}><div><b>Cancel blocking jobs and apply now</b><span>alice's win11-eval creation is cancelled and has to be started again.</span></div></label>
      </div>` : "";
    const act = s === 1 ? `<button class="btn accent" data-act="stage">Stage 1.15.0</button>` : s === 2 ? `<button class="btn accent" data-act="apply">${H.svcMode === "wait" ? "Apply when jobs finish" : "Cancel jobs & apply"}</button>` : s === 3 ? `<button class="btn" disabled><svg class="i spin" viewBox="0 0 24 24"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5"/></svg>Applying…</button>` : "";
    return `<div class="page">${header("Service")}
      <div class="hero" style="grid-template-columns:1fr auto"><div><div class="state">${ic("upd", "s24")}<h2>${s === 4 ? "constructd 1.15.0" : "constructd 1.14.2 → 1.15.0"}</h2></div>
        <div class="sub">${s === 4 ? "Updated at 14:12 · all 8 VMs kept running · recorded in audit" : "Checked 2 min ago · released 2026-09-21 · package verified"}</div></div><div class="acts">${act}</div></div>
      ${s === 4 ? `<div class="infobar ok"><span class="ib-ic">${ic("okc")}</span><div class="ib-body"><b>Update complete.</b> Held jobs resumed: reprovision dev-2 is running.</div></div>` : ""}
      <div class="card"><div class="stepper">${st(0, "Check")}<span class="step-line ${s > 1 || s === 4 ? "done" : ""}"></span>${st(1, "Stage")}<span class="step-line ${s > 2 ? "done" : ""}"></span>${st(2, "Apply")}<span class="step-line ${s > 3 ? "done" : ""}"></span>${st(3, "Verify")}</div>
        ${s === 3 ? `<div class="progress indet"><i></i></div><div class="faint" style="font-size:12px">Phase 2 of 4: swapping service binaries. VMs aren't touched.</div>` : ""}</div>
      ${blocking}
      <div class="sec">${secH("What's new in 1.15.0")}<div class="card"><ul style="margin:0;padding-left:18px;font-size:13px;line-height:22px">
        <li>Daily cost buckets per user and VM (enables spend trends)</li><li>Initialize vTPM identity before capturing reusable VM baselines</li><li>Default VM CPU allocation follows the host allowance</li><li>Opt-in Hyper-V licence machine reuse for Windows children</li></ul></div></div>
      <details class="adv"><summary>${ic("warn", "s20")}If an update gets stuck${ic("chevd", "chev")}</summary><div class="adv-b"><div class="row"><button class="btn">Resume update</button><button class="btn">Cancel staged update</button><button class="btn danger">Roll back to 1.14.2…</button></div><span class="faint" style="font-size:12px">Every choice is written to the audit log.</span></div></details>
      <details class="adv"><summary>${ic("list", "s20")}Backend capabilities · Hyper-V${ic("chevd", "chev")}</summary><div class="adv-b"><table class="t"><tbody>
        ${[["Save state (RAM to disk)", 1], ["Child VMs", 1], ["vTPM / Windows 11 guests", 1], ["Live RAM resize", 0], ["Nested virtualisation", 1], ["GPU partitioning", 0], ["Host forwards on LAN", 1]].map(([k, v]) => `<tr><td>${k}</td><td class="r">${v ? `<span class="chip ok">${ic("check", "s12")}Supported</span>` : `<span class="chip">Not supported</span>`}</td></tr>`).join("")}</tbody></table></div></details>
      <div class="sec">${secH("Update history")}<table class="t"><tbody><tr><td>1.14.2</td><td class="muted">2026-09-02 · dana</td><td class="r"><span class="chip ok">Applied</span></td></tr><tr><td>1.14.1</td><td class="muted">2026-08-19 · dana</td><td class="r"><span class="chip ok">Applied</span></td></tr><tr><td>1.14.0</td><td class="muted">2026-08-11 · dana</td><td class="r"><span class="chip warn">Rolled back, then re-applied</span></td></tr></tbody></table></div></div>`;
  }

  /* ---------------- audit ---------------- */
  function audit() {
    const mine = H.resolved.map((r) => [r.t, "dana", r.text, "attention feed"]);
    const rows = mine.reverse().concat(AUDIT).filter((r) => H.afilter === "all" || (H.afilter === "system" ? r[1] === "system" : r[1] !== "system"));
    return `<div class="page">${header("Audit", `<div class="seg" data-hseg="afilter">${[["all", "All"], ["people", "People"], ["system", "System"]].map(([k, l]) => `<button data-v="${k}" class="${H.afilter === k ? "on" : ""}">${l}</button>`).join("")}</div><button class="btn">${ic("export")}Export</button>`)}
      <table class="t"><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th></tr></thead><tbody>
      ${rows.map((r) => `<tr><td class="num muted">${r[0]}</td><td>${r[1] === "system" ? `<span class="badge">system</span>` : r[1]}</td><td>${r[2]}</td><td class="muted">${r[3]}</td></tr>`).join("")}</tbody></table></div>`;
  }
  const PAGES = { attention, machines, people, spend, licences, policy, service, audit };

  /* ---------------- drawers ---------------- */
  function drawer() {
    const [kind, id] = H.drawer;
    let head = "", body = "", foot = "";
    if (kind === "vm") {
      const v = VMS.find((x) => x.id === id) || VMS[0];
      head = `<div class="grow"><div class="faint" style="font-size:12px">${v.kind === "child" ? "Child VM" : "Primary VM"} · owner <button class="link" data-open="user|${v.owner}">${v.owner}</button></div><h2>${v.id.split("/")[1]}</h2><div class="row" style="margin-top:6px">${stateChip(v.state)}<span class="faint" style="font-size:12px">sampled 18 s ago</span></div></div>`;
      body = `<div class="row wrap">${v.state.startsWith("saved") ? `<button class="btn sm accent">${ic("play", "s12")}Start</button>` : `<button class="btn sm">${ic("save", "s12")}Save</button><button class="btn sm">${ic("power", "s12")}Shut down</button>`}<button class="btn sm">${ic("clock", "s12")}Change lifetime…</button><button class="btn sm icon" data-menu="vmmore" title="More">${ic("dots", "s12")}</button></div>
        <dl class="kv"><dt>RAM</dt><dd>${v.ram} GB assigned · ${v.state.startsWith("saved") ? "0 GB resident" : Math.round(v.ram * 0.7) + " GB demand"}</dd><dt>vCPU</dt><dd>${v.ram >= 16 ? 8 : 4}</dd><dt>Activity</dt><dd>${v.act}</dd><dt>Lease</dt><dd>${v.lease ? v.lease + (v.state === "overdue" ? " (expired 12:08)" : "") : "none, primary"}</dd><dt>Idle policy</dt><dd>save after 60 min (host default)</dd><dt>Cost this month</dt><dd>${money(v.cost)}</dd></dl>
        <div class="sec">${secH("Overrides for this VM")}<div class="card"><div class="grid2">
          <div class="field"><label>RAM cap (GB)</label><input class="input" value="${v.ram}"></div><div class="field"><label>vCPU</label><input class="input" value="${v.ram >= 16 ? 8 : 4}"></div>
          <div class="field"><label>Idle action</label><select class="select"><option>Host default</option><option>Save after 30 min</option><option>Never</option></select></div><div class="field"><label>Max lease</label><input class="input" value="${v.kind === "child" ? "3 d" : "—"}"></div></div></div></div>
        <div class="sec">${secH("Related")}<div class="stack">
          <button class="srow clickable" style="min-height:44px;text-align:left" data-page="audit"><span class="si">${ic("audit")}</span><div class="st">3 audit entries</div><div class="sc">${ic("chevr")}</div></button>
          ${v.id === "bob/win-qa" ? `<button class="srow clickable" style="min-height:44px;text-align:left" data-page="licences"><span class="si">${ic("key")}</span><div class="st">Holds key A activation</div><div class="sc">${ic("chevr")}</div></button>` : ""}</div></div>`;
      foot = `<button class="btn danger" style="margin-right:auto" data-dlg="delete|${v.id}">${ic("trash")}Delete VM…</button><button class="btn" data-act="closedrawer">Close</button><button class="btn accent" data-act="closedrawer" data-toast="Overrides saved for ${v.id}">Save overrides</button>`;
    } else if (kind === "user") {
      const u = USERS.find((x) => x.id === id) || USERS[0];
      head = `<div class="grow"><div class="faint" style="font-size:12px">Person</div><h2>${u.id}</h2><div class="row" style="margin-top:6px"><span class="badge">${u.role}</span>${u.flags ? `<span class="badge warn">${u.flags}</span>` : ""}<span class="faint" style="font-size:12px">${money(u.cost)} this month</span></div></div>`;
      const f = (l, v, cap) => `<div class="field"><label>${l}</label><input class="input" value="${cap == null ? "no cap" : cap}"><small class="faint" style="font-size:11.5px">using ${v}</small></div>`;
      body = `<div class="field"><label>Role</label><select class="select"><option ${u.role === "dev" ? "selected" : ""}>dev</option><option ${u.role === "contractor" ? "selected" : ""}>contractor</option><option ${u.role === "admin" ? "selected" : ""}>admin</option></select></div>
        <div class="sec">${secH("Allowance", `<select class="select" style="height:26px;font-size:12px"><option>Apply template…</option><option>senior</option><option>contractor</option></select>`)}<div class="card"><div class="grid2">${f("Primaries", u.prim[0], u.prim[1])}${f("Children", u.child[0], u.child[1])}${f("RAM (GB)", u.ram[0], u.ram[1])}${f("vCPU", u.cpu[0], u.cpu[1])}${f("Storage (GB)", 310, 400)}<div class="field"><label>Max child lease</label><input class="input" value="3 d"><small class="faint" style="font-size:11.5px">host cap 7 d</small></div></div>
          <label class="check" style="font-size:13px"><input type="checkbox" checked>Host forwards on the LAN</label><div class="faint" style="font-size:12px">Host headroom: 22 GB below the admission line</div></div></div>
        <div class="sec">${secH("Tokens", `<button class="btn sm" data-dlg="token|${u.id}">${ic("plus", "s12")}Issue token</button>`)}
          <div class="srow" style="min-height:52px"><span class="si">${ic("key")}</span><div><div class="st">${u.id}-laptop</div><div class="sd">issued 2026-08-02 · last used 2 min ago</div></div><div class="sc"><button class="btn sm">Rotate</button><button class="btn sm subtle danger">Revoke</button></div></div></div>
        <div class="sec">${secH("VMs")}<div class="row wrap">${VMS.filter((v) => v.owner === u.id).map((v) => `<button class="chip" data-open="vm|${v.id}">${v.id.split("/")[1]} · ${v.ram} GB</button>`).join("") || '<span class="empty-note">No VMs</span>'}</div></div>`;
      foot = `<button class="btn danger" style="margin-right:auto" data-dlg="offboard|${u.id}">${u.flags === "disabled" ? "Offboard…" : "Disable…"}</button><button class="btn" data-act="closedrawer">Cancel</button><button class="btn accent" data-act="closedrawer" data-toast="Saved ${u.id}: role, allowance and tokens">Save</button>`;
    } else if (kind === "job") {
      head = `<div class="grow"><div class="faint" style="font-size:12px">Job · #4812</div><h2>Delete child bob/win-qa</h2><div class="row" style="margin-top:6px"><span class="chip err">${ic("err", "s12")}Failed 09:14</span></div></div>`;
      body = `<pre class="code" style="margin:0;white-space:pre-wrap;font-family:var(--mono);font-size:12px;padding:12px;border-radius:6px;background:var(--surface);border:1px solid var(--stroke)">09:13:58 stop VM bob/win-qa … ok\n09:14:02 release key A activation … ok\n09:14:03 remove VHDX D:\\vms\\bob\\win-qa.vhdx\n09:14:03 ERROR access denied (file in use by vmwp.exe pid 7712)</pre><div class="infobar info"><span class="ib-ic">${ic("info")}</span><div class="ib-body">The worker process has exited since then, so a retry should succeed.</div></div>`;
      foot = `<button class="btn" data-act="closedrawer">Close</button><button class="btn accent" data-fix="job" data-i="0">Retry delete</button>`;
    } else if (kind === "lic") { H.drawer = null; H.page = "licences"; return ""; }
    return `<div class="drawer-scrim" data-act="closedrawer"></div><aside class="drawer" role="dialog"><div class="drawer-head">${head}<button class="iconbtn" data-act="closedrawer" data-esc aria-label="Close">${ic("x")}</button></div><div class="drawer-body">${body}</div><div class="drawer-foot">${foot}</div></aside>`;
  }

  /* ---------------- dialogs ---------------- */
  function dialog() {
    const [kind, id] = H.dialog;
    if (kind === "onboard" || kind === "token") return `<div class="scrim"><div class="dialog"><div class="dialog-body"><h2>${kind === "onboard" ? "Add a person" : "New token for " + id}</h2>
      ${kind === "onboard" ? `<div class="grid2" style="margin-top:12px"><div class="field"><label>User name</label><input class="input" value="nina"></div><div class="field"><label>Role</label><select class="select"><option>dev</option><option>contractor</option></select></div></div><div class="field" style="margin-top:12px"><label>Allowance template</label><select class="select"><option>dev · 2 primaries, 2 children, 24 GB, 8 vCPU</option><option>contractor · 1 primary, 1 child, 16 GB, 4 vCPU</option></select></div>` : ""}
      <div class="infobar warn" style="margin-top:16px"><span class="ib-ic">${ic("key")}</span><div class="ib-body"><b>The token is shown once.</b> Copy it now: <span class="mono">ctk_9f2c…a71e</span></div><div class="ib-actions"><button class="btn sm">${ic("copy", "s12")}Copy</button></div></div></div>
      <div class="dialog-foot"><button class="btn" data-act="closedlg" data-esc>Cancel</button><button class="btn accent" data-act="closedlg" data-toast="${kind === "onboard" ? "nina added · token copied" : "Token issued"}">Done</button></div></div></div>`;
    const isDel = kind === "delete", name = isDel ? id.split("/")[1] : id;
    return `<div class="scrim"><div class="dialog" role="alertdialog"><div class="dialog-body"><div class="row" style="gap:12px;align-items:flex-start"><span style="color:var(--err-text);margin-top:4px">${ic("danger", "s24")}</span><div><h2>${isDel ? `Delete ${id}?` : `Offboard ${id}?`}</h2><p class="muted" style="margin:0;font-size:13px">Cascade preview, based on the host's state right now:</p></div></div>
      <div class="impact">${isDel ? `<div class="ir bad">${ic("trash")}<span>VM and its 146 GB disk are deleted. Repos without a remote are lost.</span></div><div class="ir warn">${ic("key")}<span>Key A activation is released (7 → 6 of 10)</span></div><div class="ir ok">${ic("save")}<span>Owner ${id.split("/")[0]} is notified. Audit entry written.</span></div>`
        : `<div class="ir warn">${ic("lock")}<span>Sign-in disabled and 1 token revoked</span></div><div class="ir bad">${ic("server")}<span>${VMS.filter((v) => v.owner === id).length} VMs and their children are deleted</span></div><div class="ir ok">${ic("key")}<span>Windows keys held by their children are released</span></div>`}</div>
      <div class="field"><label>Type <b class="mono" style="color:var(--text)">${name}</b> to confirm</label><input class="input confirm-in" id="hConfirm" data-name="${name}" autocomplete="off"></div></div>
      <div class="dialog-foot"><button class="btn" data-act="closedlg" data-esc>Cancel</button><button class="btn danger-fill" id="hConfirmGo" disabled data-act="closedlg" data-toast="${isDel ? id + " deleted" : id + " offboarded"}">${isDel ? "Delete" : "Offboard"}</button></div></div></div>`;
  }

  /* ---------------- events ---------------- */
  function resolve(id, i) {
    const f = FEED.find((x) => x.id === id);
    H.resolving = id; H.drawer = null; rerender();
    setTimeout(() => {
      const d = new Date(), t = `14:${String(8 + H.resolved.length).padStart(2, "0")}`;
      H.resolved.push({ id, text: f.fixes[i][1], t }); H.resolving = null;
      if (App.surface === "host") { rerender(); toast($(".win-body"), f.fixes[i][1] + " · logged to audit"); }
    }, 450);
  }
  function mount(root) {
    const win = $("#hwin", root), body = $(".win-body", win);
    wireTips(win, body);
    win.addEventListener("click", (e) => {
      const m = e.target.closest("[data-menu]");
      if (m) { e.stopPropagation(); openMenu(m, body, `<button class="mi">${ic("globe")}Make public</button><button class="mi">${ic("key")}Rotate VM token</button><button class="mi">${ic("restart")}Restart</button>`, "right"); return; }
      const fx = e.target.closest("[data-fix]"); if (fx) return resolve(fx.dataset.fix, +fx.dataset.i);
      const dl = e.target.closest("[data-dlg]"); if (dl) { H.dialog = dl.dataset.dlg.split("|"); return rerender(); }
      const op = e.target.closest("[data-open]");
      if (op && op.dataset.open) { const [k, id] = op.dataset.open.split("|"); if (k === "lic") { H.page = "licences"; H.drawer = null; } else H.drawer = [k, id]; closeMenus(); return rerender(); }
      const mt = e.target.closest("[data-mtab]"); if (mt) { H.mtab = mt.dataset.mtab; if (mt.dataset.page) H.page = mt.dataset.page; return rerender(); }
      const pg = e.target.closest("[data-page]"); if (pg) { H.page = pg.dataset.page; H.drawer = null; closeMenus(); rerender(); $("#hcontent").scrollTop = 0; return; }
      const vf = e.target.closest("[data-vf]"); if (vf) { H.vmFilter = vf.dataset.vf; return rerender(); }
      const hs = e.target.closest("[data-hseg] button"); if (hs) { H[hs.parentElement.dataset.hseg] = hs.dataset.v; return rerender(); }
      const a = e.target.closest("[data-act]"); if (!a) return;
      const act = a.dataset.act;
      if (act === "closedrawer" || act === "closedlg") { if (act === "closedrawer") H.drawer = null; else H.dialog = null; rerender(); if (a.dataset.toast) toast($(".win-body"), a.dataset.toast); return; }
      if (act === "stage") { H.svc = 2; return rerender(); }
      if (act === "apply") { H.svc = 3; rerender(); setTimeout(() => { H.svc = 4; if (App.surface === "host") rerender(); }, 2600); return; }
      if (act === "polsave") { H.policyDirty = false; rerender(); return toast($(".win-body"), "Policy saved · logged to audit"); }
      if (act === "poldiscard") { H.policyDirty = false; H.idleMin = 60; return rerender(); }
    });
    win.addEventListener("change", (e) => { if (e.target.name === "svm") { H.svcMode = e.target.value; rerender(); } if (e.target.matches("[data-pol]") && !H.policyDirty) { H.policyDirty = true; rerender(); } });
    win.addEventListener("input", (e) => {
      const t = e.target;
      if (t.id === "hConfirm") $("#hConfirmGo").disabled = t.value.trim() !== t.dataset.name;
      if (t.id === "idleMin") { H.idleMin = +t.value || 0; const p = $("#idlePrev"); if (p) p.innerHTML = ic("eye", "s12") + " Preview: " + (H.idleMin <= 120 ? "Would save <b>1 VM</b> right now: alice/bench (idle 2 h, 12 GB)." : "Would save nothing right now."); if (!H.policyDirty) { H.policyDirty = true; const pos = t.selectionStart; rerender(); const n = $("#idleMin"); n.focus(); n.setSelectionRange(pos, pos); } }
      if (t.id === "hsearch") {
        const q = t.value.trim().toLowerCase(); closeMenus(); if (!q) return;
        const hits = VMS.filter((v) => v.id.includes(q)).map((v) => `<button class="mi" data-open="vm|${v.id}">${ic("server")}${v.id}<span class="mi-sub">${v.state}</span></button>`)
          .concat(USERS.filter((u) => u.id.includes(q)).map((u) => `<button class="mi" data-open="user|${u.id}">${ic("user")}${u.id}<span class="mi-sub">${u.role}</span></button>`));
        const m = openMenu(t, body, hits.length ? hits.join("") : `<div class="mh">No matches for “${esc(q)}”</div>`);
        m.style.width = "300px"; t.focus();
      }
    });
  }
  App.register("host", { render, mount });
})();
