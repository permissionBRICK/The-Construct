/* Surface 2: per-VM control panel (Windows Settings frame) */
"use strict";
(function () {
  const C = {
    page: "overview", inst: "agent-vm", pending: new Set(), dialog: null, drawer: null, period: "7d",
    notifFilter: "all", mic: "armed", custom: "save", dz: null, dzStep: 0, projects: D.projects.map((p) => ({ ...p })), armed: false,
  };
  const NAV = [
    ["overview", "home", "Overview"], ["agents", "spark", "Agents"], ["forwards", "fwd", "Forwards"], ["projects", "folder", "Projects"],
    ["usage", "chart", "Usage"], ["machine", "cpu", "Machine"], ["access", "shield", "Access & services"],
  ];
  const isSaved = () => C.inst === "dev-2";

  /* ---------------- frame ---------------- */
  function navHtml() {
    const i = D.instances[C.inst];
    const cnt = { agents: '<span class="cnt w">1 update</span>', forwards: `<span class="cnt">${isSaved() ? "" : "2 open"}</span>`, projects: '<span class="cnt b">1</span>' };
    return `<nav class="nav" aria-label="Sections">
      <button class="inst-switch" data-menu="inst" aria-haspopup="menu">
        <span class="avatar">${ic(isSaved() ? "server" : "logo", "s20")}<span class="dot ${isSaved() ? "saved" : "run"}"></span></span>
        <span class="t"><b>${C.inst}</b><span>${isSaved() ? "buildbox · Saved" : "Local Hyper-V · Running"}</span></span>${ic("chevd")}</button>
      <div class="search"><input class="input" placeholder="Find a setting" aria-label="Find a setting" id="findSetting">${ic("search")}</div>
      ${NAV.map(([k, icn, l]) => `<button class="nav-item ${C.page === k ? "on" : ""}" data-page="${k}">${ic(icn)}${l}${cnt[k] || ""}</button>`).join("")}
      <div class="nav-sep"></div>
      <button class="nav-item danger ${C.page === "danger" ? "on" : ""}" data-page="danger">${ic("danger")}Danger zone</button>
      <div class="nav-spacer"></div>
      <button class="nav-item ${C.page === "companion" ? "on" : ""}" data-page="companion">${ic("gear")}Companion settings</button>
      <button class="nav-item" data-go="host">${ic("server")}Host panel · buildbox<span class="cnt">${ic("ext", "s12")}</span></button>
    </nav>`;
  }
  function render(sub) {
    if (sub) {
      if (sub === "saved") { C.inst = "dev-2"; C.page = "overview"; }
      else if (sub === "confirm") { C.page = "danger"; C.dialog = "reinstall"; C.dzStep = 0; }
      else if (sub === "edit") { C.page = "projects"; C.drawer = "construct"; }
      else if (sub === "pending") { C.page = "access"; C.pending = new Set(["tunnel", "gitcreds"]); }
      else C.page = sub;
      App.sub = null; history.replaceState(null, "", "#panel/" + C.page);
    }
    const i = D.instances[C.inst];
    return `<div class="win" id="pwin">
      <div class="titlebar">${ic("logo")}<span class="ttl">Construct <span>· ${C.inst}</span></span>
        <span class="caps"><button class="cap" aria-label="Minimize">${ic("min", "s12")}</button><button class="cap" aria-label="Maximize">${ic("max", "s12")}</button><button class="cap x" aria-label="Close">${ic("x", "s12")}</button></span></div>
      <div class="win-body">${navHtml()}<div class="content" id="pcontent">${PAGES[C.page]()}</div>
        ${C.drawer ? drawerHtml() : ""}${C.dialog ? dialogHtml() : ""}</div>
    </div>`;
  }
  const header = (title, right = "", crumb = "") => `<div class="page-h"><div class="grow">${crumb ? `<div class="crumb">${crumb}</div>` : ""}<h1>${title}</h1></div>${right}</div>`;
  const srow = (icon, title, desc, ctrl, cls = "") => `<div class="srow ${cls}"><span class="si">${icon ? ic(icon, "s20") : ""}</span><div><div class="st">${title}</div>${desc ? `<div class="sd">${desc}</div>` : ""}</div><div class="sc">${ctrl}</div></div>`;
  const secH = (t, right = "") => `<div class="sec-h"><h2>${t}</h2><span class="grow"></span>${right}</div>`;

  /* ---------------- overview ---------------- */
  function nextBtn() {
    if (isSaved()) return `<div class="split"><button class="btn accent" data-act="resume">${ic("play", "s20")}<span class="next-lbl"><b>Resume dev-2</b><small>About 20 s · restores open sessions</small></span></button><button class="btn accent" data-menu="open" aria-label="More">${ic("chevd")}</button></div>`;
    return `<div class="split"><button class="btn accent" data-act="open">${ic("code", "s20")}<span class="next-lbl"><b>Open in VS Code</b><small>Remote-SSH · construct</small></span></button><button class="btn accent" data-menu="open" aria-label="Other ways to open">${ic("chevd")}</button></div>`;
  }
  function overview() {
    if (isSaved()) return overviewSaved();
    const agents = D.agents.map((a) => `<div class="mini">${a.state === "working" ? `<svg class="work-ind spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5"/></svg>` : `<span class="dot sm ${a.state === "ui" || a.serve ? "run" : ""}" style="margin:0 4px"></span>`}<span class="grow"><b style="font-weight:600">${a.name}</b> <small>${a.state === "working" ? "working · 6 min" : a.state === "ui" ? "2 threads" : a.serve ? "serving " + a.serve : "idle · " + a.last}</small></span>${a.upd ? `<span class="badge warn">${a.upd}</span>` : ""}</div>`).join("");
    const fw = D.forwards.map((f) => `<div class="mini"><span class="dot sm ${f.state === "open" ? "run" : "off"}"></span><span class="mono">:${f.vm}${f.pc !== f.vm ? "→" + f.pc : ""}</span><span class="grow"><small>${f.label || "queued · host LAN"}</small></span>${f.state === "open" ? `<button class="btn sm icon" data-act="openfwd" data-port="${f.pc}" title="Open localhost:${f.pc}">${ic("ext", "s12")}</button>` : `<span class="badge">queued</span>`}</div>`).join("");
    return `<div class="page">
      ${header("Overview", `<span class="faint" style="font-size:12px;padding-bottom:6px">Status refreshed 4 s ago</span>`)}
      <div class="hero">
        <div><div class="state"><span class="dot run pulse" style="width:14px;height:14px"></span><h2>Running</h2></div>
          <div class="sub">Local Hyper-V · Ubuntu 26.04 LTS · up 3h 12m · 8 vCPU · 16 GB</div>
          <div class="flags"><span class="chip ok"><span class="dot run sm"></span>Claude working · 6 min</span><button class="chip warn" data-page="machine">${ic("refresh", "s12")}2 commits behind host</button><button class="chip warn" data-page="machine">${ic("disk", "s12")}Disk 88%</button><span class="chip info">${ic("upd", "s12")}Construct 3a91f0e available</span></div></div>
        <div class="acts">${nextBtn()}<button class="btn icon lg" data-menu="power" title="Power" style="width:44px;height:44px">${ic("power")}</button></div>
      </div>
      ${C.armed
        ? `<div class="infobar ok"><span class="ib-ic">${ic("timer")}</span><div class="ib-body"><b>Reprovision armed.</b> It starts as soon as Claude finishes the current turn. Nothing else needs you.</div><div class="ib-actions"><button class="btn sm" data-act="disarm">Cancel</button></div></div>`
        : `<div class="infobar warn"><span class="ib-ic">${ic("refresh")}</span><div class="ib-body"><b>2 commits behind the host.</b> Provisioned at <span class="mono">c21978b</span>; this PC has <span class="mono">b5c4348</span>. Reprovision keeps your data and takes about 6 min. Claude is mid-turn, so the safe moment is when it finishes.</div><div class="ib-actions"><button class="btn sm accent" data-act="arm">${ic("timer", "s12")}When Claude finishes</button><button class="btn sm" data-act="dlg" data-d="reprovision">Now…</button></div></div>`}
      <div class="grid3">
        <div class="card clickable" data-page="agents"><div class="card-h">${ic("spark")}Agents<span class="grow"></span><span class="link">Details</span></div>${agents}</div>
        <div class="card clickable" data-page="forwards"><div class="card-h">${ic("fwd")}Forwards<span class="grow"></span><span class="faint" style="font-weight:400">2 open · 1 queued</span></div>${fw}</div>
        <div class="card clickable" data-page="usage"><div class="card-h">${ic("dollar")}Spend today<span class="grow"></span><span class="faint" style="font-weight:400">updated 1 min ago</span></div>
          <div class="row" style="align-items:flex-end"><span class="bigv">${money(4.12)}</span><span class="faint" style="font-size:12px;padding-bottom:6px">month ${money(86.3)}</span></div>
          <div class="row" style="justify-content:space-between;font-size:12px;color:var(--text-2)"><span>Claude ${money(3.4)}</span><span>Codex ${money(0.58)}</span><span>OpenCode ${money(0.14)}</span></div></div>
        <div class="card clickable" data-page="agents"><div class="card-h">${ic("bell")}Notifications<span class="grow"></span><span class="badge ok">3 new</span></div>
          ${D.notifs.map((n) => `<div class="mini">${ic(n.lv === "error" ? "err" : "info", "lv-" + n.lv)}<span class="grow">${n.msg}</span><small class="num">${n.t}</small></div>`).join("")}</div>
        <div class="card clickable" data-page="machine"><div class="card-h">${ic("cpu")}Machine<span class="grow"></span><span class="faint" style="font-weight:400">sampled 20 s ago</span></div>
          <div class="mini"><span class="grow">Memory</span><small class="num">11.2 of 16 GB</small></div><div class="meter"><i style="width:70%"></i></div>
          <div class="mini"><span class="grow">Disk <span class="badge warn">${ic("warn", "s12")} 88%</span></span><small class="num">128 of 146 GB</small></div><div class="meter warn"><i style="width:88%"></i></div></div>
        <div class="card clickable" data-page="projects"><div class="card-h">${ic("folder")}Projects &amp; sync<span class="grow"></span>${when("repro")}</div>
          <div class="mini"><span class="grow">4 of 5 profiles in the next reprovision</span></div>
          <div class="mini">${ic("sync")}<span class="grow">Synced 3 min ago</span></div>
          <div class="mini">${ic("plus")}<span class="grow"><b style="font-weight:600">jarvis</b> found on the VM</span><span class="badge new">new</span></div></div>
      </div>
      <div class="grid2">
        <div class="card"><div class="card-h">${ic("child")}Child VMs<span class="grow"></span><button class="link" data-page="machine">Manage</button></div>
          <div class="mini"><span class="dot run sm"></span><span class="grow"><b style="font-weight:600">win-test</b> <small>Windows 11 eval · 4 GB</small></span><span class="chip">${ic("clock", "s12")}lease 5h 20m</span><button class="btn sm">Console</button></div></div>
        <div class="card"><div class="card-h">${ic("mic")}Microphone passthrough<span class="grow"></span>${when("now")}</div>
          <div class="row"><div class="seg" data-seg="mic">${["off", "armed", "live"].map((m) => `<button data-v="${m}" class="${C.mic === m ? "on" : ""}">${m[0].toUpperCase() + m.slice(1)}</button>`).join("")}</div><span class="faint" style="font-size:12px">Shure MV7 · ${C.mic === "live" ? "streaming to /voice" : C.mic === "armed" ? "starts when /voice listens" : "not captured"}</span></div></div>
      </div>
    </div>`;
  }
  function overviewSaved() {
    return `<div class="page">
      ${header("Overview", `<span class="faint" style="font-size:12px;padding-bottom:6px">Reported by buildbox 12 s ago</span>`)}
      <div class="hero"><div><div class="state"><span class="dot saved" style="width:14px;height:14px"></span><h2>Saved</h2></div>
        <div class="sub">Remote · buildbox.example.local:7462 · saved by idle policy 41 min ago · 4 vCPU · 12 GB</div>
        <div class="flags"><span class="chip">${ic("pause", "s12")}RAM on disk, nothing running</span><span class="chip ok">${ic("check", "s12")}Up to date with host</span><span class="chip">${ic("clock", "s12")}Idle policy: save after 60 min</span></div></div>
        <div class="acts">${nextBtn()}<button class="btn icon lg" data-menu="power" title="Power" style="width:44px;height:44px">${ic("power")}</button></div></div>
      <div class="grid3">
        <div class="card"><div class="card-h">${ic("spark")}Agents</div><div class="muted" style="font-size:13px">Paused with the VM. Last activity: Codex finished a turn 1h 41m ago.</div></div>
        <div class="card"><div class="card-h">${ic("fwd")}Forwards</div><div class="muted" style="font-size:13px">None open. Agents create forwards with <span class="mono">construct expose</span>, and they show up here.</div></div>
        <div class="card"><div class="card-h">${ic("dollar")}Spend today</div><span class="bigv">${money(0.62)}</span><span class="faint" style="font-size:12px">month ${money(31.8)}</span></div>
      </div></div>`;
  }

  /* ---------------- agents ---------------- */
  function agents() {
    const extra = [{ t: "Yesterday 22:10", lv: "info", msg: "Nightly benchmark done", agent: "codex", repo: "construct" }, { t: "Yesterday 17:48", lv: "warn", msg: "Usage limit reached — resuming at 18:30", agent: "claude", repo: "omniloop" }];
    const list = D.notifs.concat(extra).filter((n) => C.notifFilter === "all" || n.lv !== "info");
    return `<div class="page">
      ${header("Agents", `<button class="btn accent" data-act="toast" data-m="Claude Code 2.4.3 queued; installs when the current turn ends">${ic("upd")}Update all (1)</button>`)}
      <div class="sec">${secH("Coding agents on agent-vm", when("now"))}
        ${srow("spark", `Claude Code <span class="badge ok"><span class="dot run sm"></span>working</span>`, "construct · turn started 6 min ago · “Refactoring forwarder retry backoff”", `<span class="faint num">2.4.1</span><span class="chip warn">2.4.3 available</span><button class="btn sm" data-act="toast" data-m="Update queued; waits for the running turn">Update</button>`, "group-top")}
        ${srow("code", "Codex", "Idle · last turn 48 min ago · gitgudlab", `<span class="faint num">0.61.0</span><span class="chip ok">${ic("check", "s12")}Up to date</span>`, "group-mid")}
        ${srow("terminal", "OpenCode", "Idle · serve running on :4096", `<span class="faint num">1.9.2</span><span class="chip ok">${ic("check", "s12")}Up to date</span>`, "group-mid")}
        ${srow("globe", "T3 Code", "Web UI enabled · 2 threads active", `<span class="faint num mono">0.9.14-construct.3</span><button class="btn sm">${ic("ext", "s12")}Open</button>`, "group-bot")}
      </div>
      <div class="sec">${secH("Agent options")}
        ${srow("pulse", `Claude Code live streaming ${when("repro")}`, "Streams thinking and replies over Remote-SSH as they generate. If it's off, the panel looks frozen until each turn ends.", sw("o-stream", true))}
        ${srow("terminal", `OpenCode background watcher ${when("repro")}`, "Adds the background, background_output and background_kill tools", sw("o-watch", false))}
        ${srow("window", `Patched T3 Code + Desktop ${when("repro")}`, "Builds T3 once for the VM server and Windows Desktop: voice input, usage-limit recovery, OpenCode monitoring", sw("o-t3p", true))}
      </div>
      <div class="sec">${secH("Notifications", `<div class="seg" data-seg="nf"><button data-v="all" class="${C.notifFilter === "all" ? "on" : ""}">All</button><button data-v="err" class="${C.notifFilter !== "all" ? "on" : ""}">Warnings &amp; errors</button></div>`)}
        <div class="card" style="padding:4px">${list.map((n) => `<div class="notif">${ic(n.lv === "error" ? "err" : n.lv === "warn" ? "warn" : "info", "lv-" + n.lv)}<div><div class="nt">${n.msg}</div><div class="nm">${n.agent} · ${n.repo} · sent with construct notify</div></div><span class="ntime">${n.t}</span></div>`).join("")}</div>
        ${srow("bell", `Show as Windows notifications ${when("now")}`, "Messages stay here either way", sw("o-toast", true))}
      </div></div>`;
  }

  /* ---------------- forwards ---------------- */
  function forwards() {
    return `<div class="page">
      ${header("Forwards", `<button class="btn" data-act="dlg" data-d="expose">${ic("plus")}Expose a port…</button>`)}
      <div class="infobar info"><span class="ib-ic">${ic("info")}</span><div class="ib-body">Agents open forwards with <span class="mono">construct expose &lt;port&gt;</span>. <b>Client</b> forwards land on this PC; <b>host</b> forwards stay on buildbox's LAN. Changes here apply immediately.</div></div>
      <table class="t"><thead><tr><th>State</th><th>VM port</th><th>On this PC</th><th>Label</th><th>Scope</th><th>Opened by</th><th class="r"></th></tr></thead><tbody>
      ${D.forwards.map((f) => `<tr><td>${f.state === "open" ? `<span class="chip ok"><span class="dot run sm"></span>Open</span>` : `<span class="chip">${ic("hourglass", "s12")}Queued</span>`}</td>
        <td class="mono">${f.vm}</td><td class="mono">${f.scope === "host" ? "—" : "localhost:" + f.pc}${f.pc !== f.vm ? `<div class="faint" style="font-family:var(--font);font-size:11.5px">remapped · ${f.note}</div>` : ""}</td>
        <td>${f.label || '<span class="faint">none</span>'}</td><td>${f.scope === "host" ? "Host · buildbox LAN" : "Client"}</td><td class="muted">${f.by}</td>
        <td class="r"><div class="row" style="justify-content:flex-end">${f.state === "open" ? `<button class="btn sm" data-act="openfwd" data-port="${f.pc}">${ic("ext", "s12")}Open</button><button class="btn sm icon" title="Copy link">${ic("copy", "s12")}</button>` : `<span class="faint" style="font-size:12px">waits for host</span>`}<button class="btn sm icon subtle" title="Close forward">${ic("x", "s12")}</button></div></td></tr>`).join("")}
      </tbody></table>
      <div class="sec">${secH("Microphone passthrough", when("now"))}
        ${srow("mic", "Passthrough state", "Armed means the mic opens only while <span class='mono'>/voice</span> listens. Live streams right now.", `<div class="seg" data-seg="mic">${["off", "armed", "live"].map((m) => `<button data-v="${m}" class="${C.mic === m ? "on" : ""}">${m[0].toUpperCase() + m.slice(1)}</button>`).join("")}</div>`, "group-top")}
        ${srow("", "Capture device", "", `<select class="select"><option>Shure MV7</option><option>Microphone Array (Realtek)</option></select>`, "group-bot")}
      </div></div>`;
  }

  /* ---------------- projects ---------------- */
  function projects() {
    const n = C.projects.filter((p) => p.on).length;
    return `<div class="page">
      ${header("Projects", `<button class="btn">${ic("plus")}New profile</button>`)}
      <div class="sec">${secH(`Profiles · ${n} of ${C.projects.length} in the next reprovision`, when("repro"))}
        ${C.projects.map((p, i) => `<div class="srow ${i === 0 ? "group-top" : i === C.projects.length - 1 ? "group-bot" : "group-mid"}"><label class="check" title="Include in next reprovision"><input type="checkbox" data-proj="${p.id}" ${p.on ? "checked" : ""}></label>
          <div><div class="st"><b style="font-weight:600">${p.id}</b>${p.isNew ? '<span class="badge new">new · found on VM</span>' : ""}${!p.on ? '<span class="badge">skipped</span>' : ""}</div><div class="sd">${p.repos} repo${p.repos > 1 ? "s" : ""} · ${p.sdks}${p.mcp ? ` · ${p.mcp} MCP server${p.mcp > 1 ? "s" : ""}` : ""}</div></div>
          <div class="sc"><button class="btn sm" data-drawer="${p.id}">${ic("edit", "s12")}Edit</button></div></div>`).join("")}
      </div>
      <div class="sec">${secH("Config sync", `<span class="stale">last synced 3 min ago</span>`)}
        ${srow("sync", "Sync with the VM", "Profiles, instructions and memory go both ways. 1 new profile came from the VM (jarvis).", `<button class="btn" data-act="toast" data-m="Synced · nothing new">${ic("sync")}Sync now</button>`, "group-top")}
        ${srow("export", "Export config", "Bundle profiles, instructions and settings into a folder or zip", `<button class="btn">Export…</button>`, "group-mid")}
        ${srow("import", "Add config source", "A git URL or a local path to merge on the next reprovision", `<button class="btn">Add…</button>`, "group-bot")}
      </div>
      <div class="sec">${secH("Remote config repos")}
        ${srow("branch", "github.com/permissionBRICK/construct-config", "main · pulled 3 min ago", `<span class="chip ok">${ic("check", "s12")}In sync</span>`, "group-top")}
        ${srow("branch", "gitlab.example.local/team/shared-config", "main · 1 conflict in <span class='mono'>projects/omniloop.json</span>", `<span class="chip warn">${ic("warn", "s12")}Conflict</span><button class="btn sm">Resolve…</button>`, "group-bot warnrow")}
      </div></div>`;
  }

  /* ---------------- usage ---------------- */
  function usage() {
    const max = 3.4;
    return `<div class="page">
      ${header("Usage", `<div class="seg" data-seg="period">${[["today", "Today"], ["7d", "7 days"], ["month", "Month"], ["all", "All time"]].map(([k, l]) => `<button data-v="${k}" class="${C.period === k ? "on" : ""}">${l}</button>`).join("")}</div><button class="btn">${ic("export")}Export CSV</button>`)}
      <div class="grid3">
        <div class="card"><div class="card-h">Today</div><span class="hero-v">${money(4.12)}</span><span class="faint" style="font-size:12px">so far · 2.43M tokens · updated 1 min ago</span></div>
        <div class="card"><div class="card-h">This month</div><span class="bigv">${money(86.3)}</span><span class="faint" style="font-size:12px">Sep 1–23 · avg ${money(3.75)}/day</span></div>
        <div class="card"><div class="card-h">All time</div><span class="bigv">${money(412.77)}</span><span class="faint" style="font-size:12px">since the Jun 2 install</span></div>
      </div>
      <div class="grid2" style="grid-template-columns:1.35fr 1fr">
        <div class="card" id="costChart"><div class="card-h">Cost per day · last 7 days<span class="grow"></span><span class="faint" style="font-weight:400">agent-vm · all agents</span></div>${columnChart(D.usage7, { w: 500, h: 250 })}</div>
        <div class="card"><div class="card-h">By agent · today</div>
          ${D.agents.filter((a) => a.cost).map((a) => `<div class="hbar" data-tip="<b>${a.name}</b> · ${money(a.cost)} · ${a.tokens} tokens"><span>${a.name}</span><span class="track"><i style="width:${(a.cost / max) * 100}%"></i></span><span class="v">${money(a.cost)}</span></div>`).join("")}
          <div class="hr"></div>
          <table class="t" style="border:0;background:transparent"><thead><tr><th>Agent</th><th class="r">Tokens</th><th class="r">Cost</th></tr></thead><tbody>
          ${D.agents.filter((a) => a.cost).map((a) => `<tr><td>${a.name}</td><td class="r">${a.tokens}</td><td class="r">${money(a.cost)}</td></tr>`).join("")}</tbody></table>
        </div>
      </div></div>`;
  }

  /* ---------------- machine ---------------- */
  function machine() {
    const saved = isSaved();
    return `<div class="page">
      ${header("Machine")}
      <div class="sec">${secH("Power", when("now"))}
        ${srow("power", saved ? "Saved on buildbox" : "Running · up 3h 12m", saved ? "RAM is on the host's disk. Resume takes about 20 s." : "Save keeps RAM on disk and resumes in seconds. Shut down closes forwards and the mic.",
          saved ? `<button class="btn accent" data-act="resume">${ic("play")}Resume</button><button class="btn">Shut down</button>` : `<button class="btn">${ic("restart")}Restart</button><button class="btn">${ic("save")}Save state</button><button class="btn">${ic("power")}Shut down</button>`)}
      </div>
      <div class="sec">${secH("Resources", `${when("restart")}<span class="stale">sampled 20 s ago</span>`)}
        ${srow("ram", "Memory", `11.2 GB in use of 16 GB <div class="meter" style="width:220px;margin-top:6px"><i style="width:70%"></i></div>`, `<input class="input num-in" value="16" data-res> <span class="muted">GB</span>`, "group-top")}
        ${srow("cpu", "Virtual processors", "Defaults to your host allowance. A remote VM gets this count from its host service.", `<input class="input num-in" value="8" data-res> <span class="muted">cores</span>`, "group-mid")}
        <div class="srow group-bot nested"><div class="sd" id="resNote">No changes. Applying shuts the VM down, resizes it and starts it again, with no reinstall. A local VM asks for one UAC prompt.</div><div class="sc"><button class="btn" id="resApply" disabled>${ic("restart")}Save &amp; restart</button></div></div>
      </div>
      <div class="sec">${secH("Storage &amp; OS")}
        ${srow("disk", `Disk <span class="badge warn">${ic("warn", "s12")}88% used</span>`, `128 of 146 GB. Grows on demand up to the size below. <div class="meter warn" style="width:220px;margin-top:6px"><i style="width:88%"></i></div>`, `${when("reinstall")}<input class="input num-in" value="146"> <span class="muted">GB</span>`, "group-top warnrow")}
        ${srow("disc", "Ubuntu release for redownload", "Installed: Ubuntu 26.04 LTS", `${when("reinstall")}<select class="select"><option>26.04 LTS (latest)</option><option>24.04.3 LTS</option></select>`, "group-mid")}
        ${srow("save", "Automatic checkpoints", "Hyper-V snapshot at every start. Off by default, because on a disposable VM it grows the disk and slows I/O.", `${when("now")}${sw("ckpt", false)}`, "group-bot")}
      </div>
      <div class="sec">${secH("Idle policy", saved ? when("now") : "")}
        ${saved ? srow("clock", "When dev-2 is idle", "Enforced by buildbox, so it works with this PC switched off. A VM stays up while an agent is working, even with nobody connected.", `<span class="muted">after</span><input class="input num-in" value="60"><span class="muted">min</span><select class="select"><option>Save (RAM to disk)</option><option>Shut down</option><option>Never</option></select>`)
          : srow("clock", "Only for remote instances", "The host service enforces idle policy, so a local VM has nothing to set here. dev-2 saves after 60 min.", `<button class="btn sm" data-inst="dev-2">Open dev-2</button>`)}
      </div>
      <div class="sec">${secH("Child VMs", `<button class="btn sm">${ic("plus", "s12")}New child VM…</button>`)}
        ${saved ? `<div class="empty-note">dev-2 has no child VMs.</div>` : srow("child", `win-test <span class="badge ok"><span class="dot run sm"></span>running</span>`, "Windows 11 eval · 4 GB · lease expires 19:28 (in 5h 20m)", `<button class="btn sm">Extend…</button><button class="btn sm">${ic("window", "s12")}Console</button><button class="btn sm icon" data-menu="child" title="More">${ic("dots", "s12")}</button>`)}
      </div>
      <div class="sec">${secH("Construct")}
        ${srow("upd", `Version ${saved ? "" : '<span class="badge warn">2 behind</span>'}`, saved ? "Up to date with buildbox" : "Installed <span class='mono'>b5c4348</span> · VM provisioned at <span class='mono'>c21978b</span> · latest <span class='mono'>3a91f0e</span> (4 commits)", `<button class="btn">Update Construct</button>${saved ? "" : `<button class="btn" data-act="dlg" data-d="reprovision">${ic("refresh")}Reprovision…</button>`}`)}
      </div></div>`;
  }

  /* ---------------- access & services ---------------- */
  function access() {
    const r = (id, icon, title, desc, on, tag = "repro", pos = "") => srow(icon, `${title} ${when(tag)}`, desc, sw("a-" + id, on), pos);
    return `<div class="page">
      ${header("Access &amp; services")}
      <div class="sec">${secH("Services")}
        ${r("serve", "code", "VS Code serve-web", "Browser IDE on port 8000, token-gated", true, "repro", "group-top")}
        ${r("tunnel", "cloud", "VS Code tunnel", "vscode.dev with no inbound port", false, "repro", "group-mid")}
        ${r("smb", "folder", "SMB workspace share", "Mapped as drive Z: on this PC", true, "repro", "group-mid")}
        ${r("t3", "globe", "T3 Code web GUI", "Enabling installs it on the VM now and opens the web UI. Settings, chat history and auth survive a reinstall.", true, "now", "group-mid")}
        <div class="srow group-bot nested"><div class="sd">Channel and port</div><div class="sc"><select class="select"><option>stable</option><option>nightly (may break)</option></select><input class="input num-in" value="5177"></div></div>
      </div>
      <div class="sec">${secH("Identity &amp; credentials", when("repro"))}
        ${srow("user", "Git user name", "", `<input class="input" value="permissionBRICK" style="width:240px" data-pend="gitname">`, "group-top")}
        ${srow("", "Git email", "", `<input class="input" value="dana@example.com" style="width:240px" data-pend="gitmail">`, "group-mid")}
        ${srow("key", "Store git credentials on the VM", "Saved in plaintext (~/.git-credentials). An agent that falls for a prompt injection could read them.", sw("a-gitcreds", true), "group-mid")}
        ${srow("lock", "Agent login password", "Fallback console login only; normal access is root over SSH. Set a custom one in a direct console run.", `<span class="muted mono">agent</span>`, "group-bot")}
      </div>
      ${C.pending.size ? `<div class="pending-bar">${ic("refresh")}<span class="grow"><b>${C.pending.size} change${C.pending.size > 1 ? "s" : ""} saved</b>, applied at the next reprovision. Claude is working, so you can arm it for when the turn ends.</span><button class="btn subtle" data-act="undo">Undo</button><button class="btn" data-act="arm">When Claude finishes</button><button class="btn accent" data-act="dlg" data-d="reprovision">Reprovision now…</button></div>` : ""}
    </div>`;
  }

  /* ---------------- danger zone ---------------- */
  function danger() {
    return `<div class="page">
      ${header('<span style="color:var(--err-text)">Danger zone</span>', "", `${C.inst} · rare and destructive actions`)}
      <p class="muted" style="margin:-8px 0 0;font-size:13px">These rebuild or delete the VM. Each one shows what will happen to your work before it runs, and asks you to type the instance name.</p>
      <div class="sec">
        ${srow("restart", "Reinstall", "Rebuild the VM from the cached Ubuntu image. Config (auth, git credentials, profiles, memory) is backed up first and restored after.", `<button class="btn danger" data-act="dlg" data-d="reinstall">Reinstall…</button>`, "group-top dz")}
        ${srow("import", "Redownload &amp; reinstall", "Download Ubuntu 26.04 LTS fresh (about 3 GB), then reinstall with backup and restore", `<button class="btn danger" data-act="dlg" data-d="redownload">Redownload…</button>`, "group-mid dz")}
        <details class="adv" style="border-radius:0;border-top:0"><summary>${ic("sliders", "s20")}<div><div style="font-size:14px">Custom reinstall</div><div class="sd" style="font-size:12px;color:var(--text-2)">A one-off rebuild with its own backup choice. Regular reinstall behaviour is unchanged.</div></div>${ic("chevd", "chev")}</summary>
          <div class="adv-b">
            <label class="radio"><input type="radio" name="cr" value="save" ${C.custom === "save" ? "checked" : ""}><div><b>Save &amp; restore</b><span>Back up now, restore onto the fresh VM</span></div></label>
            <label class="radio"><input type="radio" name="cr" value="restore" ${C.custom === "restore" ? "checked" : ""}><div><b>Restore an existing backup</b><span>Skip the new backup. <select class="select" style="height:26px;font-size:12px;margin-left:6px"><option>2026-09-22 18:04 · 412 MB</option><option>2026-09-15 09:30 · 398 MB</option></select></span></div></label>
            <label class="radio"><input type="radio" name="cr" value="wipe" ${C.custom === "wipe" ? "checked" : ""}><div><b style="color:var(--err-text)">Clean wipe</b><span>No backup, no restore. Settings, auth and history are lost.</span></div></label>
            <div class="row" style="justify-content:flex-end"><button class="btn danger" data-act="dlg" data-d="custom-redl">Redownload with these settings…</button><button class="btn danger" data-act="dlg" data-d="custom">Reinstall with these settings…</button></div>
          </div></details>
        ${srow("trash", "Remove instance", "Delete the VM, its disk, forwards and registry entry from this PC. Backups stay in Documents\\Construct\\backups.", `<button class="btn danger" data-act="dlg" data-d="remove">Remove…</button>`, "group-bot dz")}
      </div>
      <div class="sec">${secH("Share")}
        ${srow("share", "Share this PC as a host", "Install the Construct host service, adopt agent-vm with its data and settings, and become the host administrator. Other people can then run VMs here.", `<button class="btn">${ic("ext")}Make this PC a host…</button>`)}
      </div></div>`;
  }

  /* ---------------- companion settings ---------------- */
  function companion() {
    return `<div class="page">${header("Companion settings", "", "This PC · applies to all instances")}
      <div class="sec">${secH("Appearance", when("now"))}
        ${srow("auto", "Theme", "Follows Windows by default", `<div class="seg" data-seg="theme">${[["auto", "System"], ["light", "Light"], ["dark", "Dark"]].map(([k, l]) => `<button data-v="${k}" class="${App.themePref === k ? "on" : ""}">${l}</button>`).join("")}</div>`, "group-top")}
        ${srow("pulse", "Show agent activity on the tray icon", "A small dot shows when an agent is working, and a badge counts unread notifications", sw("c-tray", true), "group-bot")}
      </div>
      <div class="sec">${secH("Behaviour", when("now"))}
        ${srow("power", "Start with Windows", "", sw("c-start", true), "group-top")}
        ${srow("code", "Default open target", "Used by the Next-action button when a VM is running", `<select class="select"><option>VS Code · Remote-SSH</option><option>T3 Code web</option><option>Browser console</option><option>Terminal (SSH)</option></select>`, "group-mid")}
        ${srow("timer", "Wake me when an agent finishes", "Show a notification when a working agent goes idle, even if it never called construct notify", sw("c-wake", false), "group-bot")}
      </div></div>`;
  }

  const PAGES = { overview, agents, forwards, projects, usage, machine, access, danger, companion };

  /* ---------------- drawer: project editor ---------------- */
  function drawerHtml() {
    const p = C.projects.find((x) => x.id === C.drawer) || C.projects[0];
    const repos = p.id === "construct" ? [["github.com/permissionBRICK/construct", "main"], ["github.com/permissionBRICK/construct-ui-mockups", "main"]] : [[`github.com/permissionBRICK/${p.id}`, "main"]];
    return `<div class="drawer-scrim" data-act="closedrawer"></div><aside class="drawer" role="dialog" aria-label="Edit project">
      <div class="drawer-head"><div class="grow"><div class="faint" style="font-size:12px">Project profile</div><h2>${p.id}</h2><div class="muted">${when("repro")} Saved now, applied at the next reprovision</div></div><button class="iconbtn" data-act="closedrawer" data-esc aria-label="Close">${ic("x")}</button></div>
      <div class="drawer-body">
        <div class="field"><label>Repositories</label>${repos.map(([u, b]) => `<div class="row"><input class="input grow mono" value="${u}"><input class="input" value="${b}" style="width:80px"><button class="btn icon subtle" title="Remove">${ic("x", "s12")}</button></div>`).join("")}<button class="btn sm" style="justify-self:start">${ic("plus", "s12")}Add repository</button></div>
        <div class="field"><label>Runtimes (SDKs)</label><div class="row wrap">${p.sdks.split(", ").map((s) => `<span class="chip">${s} ${ic("x", "s12")}</span>`).join("")}<button class="chip">${ic("plus", "s12")}Add</button></div></div>
        <div class="field"><label>MCP servers</label>${p.mcp ? `<div class="srow" style="min-height:48px;grid-template-columns:20px 1fr auto"><span>${ic("server")}</span><div><div class="st mono" style="font-size:13px">playwright</div><div class="sd">npx @playwright/mcp · stdio</div></div><div class="sc">${sw("mcp1", true)}</div></div>` : ""}<button class="btn sm" style="justify-self:start">${ic("plus", "s12")}Add MCP server</button></div>
        <div class="field"><label>Setup commands <span class="faint">(run on every provision; keep them incremental)</span></label><textarea class="code">cd ~/repos/${p.id}\n${p.sdks.includes(".NET") ? "dotnet restore\n" : ""}npm install</textarea></div>
      </div>
      <div class="drawer-foot"><button class="btn subtle danger" style="margin-right:auto">${ic("trash")}Delete profile</button><button class="btn" data-act="closedrawer">Cancel</button><button class="btn accent" data-act="savedrawer">Save</button></div></aside>`;
  }

  /* ---------------- dialogs ---------------- */
  const DZ = {
    reinstall: { verb: "Reinstall", title: "Reinstall agent-vm?", time: "about 25 min" },
    redownload: { verb: "Redownload", title: "Redownload and reinstall agent-vm?", time: "about 35 min" },
    custom: { verb: "Reinstall", title: "Custom reinstall of agent-vm?", time: "about 25 min" },
    "custom-redl": { verb: "Redownload", title: "Custom redownload of agent-vm?", time: "about 35 min" },
    remove: { verb: "Remove", title: "Remove agent-vm from this PC?", time: "about 1 min" },
  };
  function impact(kind) {
    const wipe = kind.startsWith("custom") && C.custom === "wipe";
    const rows = [];
    if (kind === "remove") {
      rows.push(["bad", "trash", "Deleted: the Hyper-V VM, its 146 GB disk, 2 forwards and the <span class='mono'>agent-vm</span> registry entry"]);
      rows.push(["bad", "child", "Child VM <b>win-test</b> is deleted too"]);
    } else {
      rows.push(["bad", "disk", `The VM disk is wiped and rebuilt${kind.includes("redownload") || kind === "custom-redl" ? " from a freshly downloaded Ubuntu 26.04 LTS" : ""}. This takes ${DZ[kind].time}.`]);
      rows.push(wipe ? ["bad", "x", "<b>No backup.</b> Auth, git credentials, profiles, memory and T3 history are lost."] : ["ok", "save", kind === "custom" && C.custom === "restore" ? "Restores the backup from 2026-09-22 18:04 (412 MB)" : "Config is backed up now (auth, git credentials, profiles, memory) and restored afterwards"]);
    }
    rows.push(["bad", "branch", "<b>Work only on the VM is lost:</b> construct has 3 uncommitted files; omniloop has 1 unpushed commit on <span class='mono'>feat/poll-budget</span>"]);
    rows.push(["warn", "spark", "Claude Code is mid-turn (6 min) and gets interrupted"]);
    rows.push(["warn", "fwd", "Forwards :5173 and :8080→18800 close"]);
    return `<div class="impact">${rows.map(([c, i, t]) => `<div class="ir ${c}">${ic(i)}<span>${t}</span></div>`).join("")}</div>`;
  }
  function dialogHtml() {
    const d = C.dialog;
    if (d === "expose") return `<div class="scrim"><div class="dialog" role="dialog" aria-label="Expose a port"><div class="dialog-body"><h2>Expose a port</h2><p class="muted" style="margin:0 0 16px;font-size:13px">Same as running <span class="mono">construct expose</span> in the VM.</p>
      <div class="grid2"><div class="field"><label>VM port</label><input class="input" value="4321"></div><div class="field"><label>Label</label><input class="input" placeholder="optional"></div></div>
      <div class="field" style="margin-top:12px"><label>Where</label><div class="seg"><button class="on">This PC (client)</button><button>buildbox LAN (host)</button></div></div></div>
      <div class="dialog-foot"><button class="btn" data-act="closedlg" data-esc>Cancel</button><button class="btn accent" data-act="closedlg" data-toast="Forward :4321 opened on localhost:4321">Expose</button></div></div></div>`;
    if (d === "reprovision") return `<div class="scrim"><div class="dialog" role="dialog" aria-label="Reprovision"><div class="dialog-body"><h2>Reprovision agent-vm now?</h2>
      <p class="muted" style="margin:0;font-size:13px">Rebuilds tooling from <span class="mono">b5c4348</span> and applies pending settings. Your VM disk, repos and history are kept.</p>
      <div class="impact"><div class="ir ok">${ic("check")}<span>Keeps data: repos, auth, agent history</span></div><div class="ir ok">${ic("refresh")}<span>Picks up 2 host commits${C.pending.size ? ` and ${C.pending.size} pending setting${C.pending.size > 1 ? "s" : ""}` : ""}</span></div><div class="ir warn">${ic("spark")}<span>Claude Code is mid-turn. Reprovisioning now interrupts it.</span></div></div></div>
      <div class="dialog-foot"><button class="btn" data-act="closedlg" data-esc>Cancel</button><button class="btn" data-act="arm">${ic("timer")}When Claude finishes</button><button class="btn accent" data-act="closedlg" data-toast="Reprovision started · about 6 min">Reprovision now</button></div></div></div>`;
    const z = DZ[d];
    if (C.dzStep > 0) {
      const steps = d === "remove" ? ["Stopping agent-vm", "Deleting child VM win-test", "Deleting VM and disk", "Cleaning registry"] : ["Backing up config", "Stopping agent-vm", "Rebuilding disk", "Installing Ubuntu 26.04", "Provisioning & restoring"];
      return `<div class="scrim"><div class="dialog" role="dialog" aria-label="${z.verb} in progress"><div class="dialog-body"><h2>${z.verb} in progress</h2>
        <div class="stack" style="margin-top:12px">${steps.map((s, i) => `<div class="row" style="font-size:13.5px">${i < C.dzStep ? ic("okc", "lv-ok") : i === C.dzStep ? `<svg class="i spin" viewBox="0 0 24 24" style="color:var(--accent-text)"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5"/></svg>` : ic("clock", "faint")}<span class="${i > C.dzStep ? "faint" : ""}">${s}</span></div>`).join("")}</div>
        <div class="progress" style="margin-top:16px"><i style="width:${Math.round((C.dzStep / steps.length) * 100)}%"></i></div>
        <p class="faint" style="font-size:12px;margin:10px 0 0">You can close this window. Progress stays in the tray popup, and you get a notification when it's done.</p></div>
        <div class="dialog-foot"><button class="btn" data-act="closedlg" data-esc>Run in background</button></div></div></div>`;
    }
    return `<div class="scrim"><div class="dialog" role="alertdialog" aria-label="${z.title}"><div class="dialog-body">
      <div class="row" style="gap:12px;align-items:flex-start"><span style="color:var(--err-text);margin-top:4px">${ic("danger", "s24")}</span><div class="grow"><h2>${z.title}</h2><p class="muted" style="margin:0;font-size:13px">Here's what happens, based on the VM's state right now (checked 3 s ago):</p></div></div>
      ${impact(d)}
      <div class="field"><label for="confirmIn">Type <b class="mono" style="color:var(--text)">agent-vm</b> to confirm</label><input id="confirmIn" class="input confirm-in" autocomplete="off" spellcheck="false" placeholder="agent-vm"></div></div>
      <div class="dialog-foot"><button class="btn subtle" style="margin-right:auto" data-act="closedlg">Push my work first</button><button class="btn" data-act="closedlg" data-esc>Cancel</button><button class="btn danger-fill" id="confirmGo" disabled>${z.verb} agent-vm</button></div></div></div>`;
  }

  /* ---------------- events ---------------- */
  function menuFor(kind) {
    if (kind === "inst") return `<div class="mh">Instances on PC-1</div>
      <button class="mi ${C.inst === "agent-vm" ? "sel" : ""}" data-inst="agent-vm"><span class="dot run sm"></span>agent-vm<span class="mi-sub">Local · running · 2 behind</span></button>
      <button class="mi ${C.inst === "dev-2" ? "sel" : ""}" data-inst="dev-2"><span class="dot saved sm"></span>dev-2<span class="mi-sub">buildbox · saved</span></button>
      <button class="mi" data-inst="agent-vm" data-page="machine" style="padding-left:28px"><span class="dot run sm"></span>win-test<span class="mi-sub">child of agent-vm</span></button>
      <div class="sep"></div><button class="mi">${ic("plus")}Add instance…</button><button class="mi">${ic("server")}Connect to a host…</button>`;
    if (kind === "open") return isSaved() ? `<button class="mi">${ic("code")}Resume &amp; open VS Code</button><button class="mi">${ic("play")}Resume only</button>` : `<div class="mh">Open target</div><button class="mi sel">${ic("code")}VS Code · Remote-SSH<span class="mi-sub">default</span></button><button class="mi">${ic("globe")}T3 Code web</button><button class="mi">${ic("window")}Browser console</button><button class="mi">${ic("terminal")}Terminal (SSH)</button>`;
    if (kind === "power") return isSaved() ? `<button class="mi">${ic("play")}Resume</button><button class="mi">${ic("power")}Shut down</button>` : `<button class="mi">${ic("save")}Save state<span class="mi-sub">resumes in seconds</span></button><button class="mi">${ic("restart")}Restart</button><button class="mi">${ic("power")}Shut down</button><div class="sep"></div><button class="mi" data-act="dlg" data-d="reprovision">${ic("refresh")}Reprovision…</button>`;
    if (kind === "child") return `<button class="mi">${ic("save")}Save</button><button class="mi">${ic("power")}Shut down</button><div class="sep"></div><button class="mi danger">${ic("trash")}Delete win-test…</button>`;
  }
  function mount(root) {
    const win = $("#pwin", root);
    const body = $(".win-body", win);
    wireTips(win, body);
    win.addEventListener("click", (e) => {
      const g = e.target.closest("[data-go]"); if (g) return go(g.dataset.go);
      const m = e.target.closest("[data-menu]");
      if (m) { e.stopPropagation(); openMenu(m, body, menuFor(m.dataset.menu), m.dataset.menu === "inst" ? "left" : "right"); return; }
      const inst = e.target.closest("[data-inst]");
      if (inst) { closeMenus(); C.inst = inst.dataset.inst; if (inst.dataset.page) C.page = inst.dataset.page; return rerender(); }
      const pg = e.target.closest("[data-page]");
      if (pg && !e.target.closest("[data-act]")) { C.page = pg.dataset.page; C.drawer = null; rerender(); $("#pcontent") && ($("#pcontent").scrollTop = 0); return; }
      const dr = e.target.closest("[data-drawer]"); if (dr) { C.drawer = dr.dataset.drawer; return rerender(); }
      const sg = e.target.closest("[data-seg] button");
      if (sg) {
        const k = sg.parentElement.dataset.seg, v = sg.dataset.v;
        if (k === "mic") C.mic = v; else if (k === "nf") C.notifFilter = v; else if (k === "period") C.period = v; else if (k === "theme") { setTheme(v); }
        return rerender();
      }
      const a = e.target.closest("[data-act]"); if (!a) return;
      const act = a.dataset.act;
      if (act === "dlg") { closeMenus(); C.dialog = a.dataset.d; C.dzStep = 0; rerender(); const ci = $("#confirmIn"); ci && ci.focus(); return; }
      if (act === "closedlg") { const t = a.dataset.toast; C.dialog = null; C.dzStep = 0; clearInterval(C.timer); rerender(); if (t) toast($(".win-body"), t); return; }
      if (act === "arm") { C.armed = true; C.dialog = null; C.pending.clear(); rerender(); toast($(".win-body"), "Reprovision armed. It starts when Claude goes idle.", "timer"); return; }
      if (act === "disarm") { C.armed = false; return rerender(); }
      if (act === "undo") { C.pending.clear(); return rerender(); }
      if (act === "closedrawer") { C.drawer = null; return rerender(); }
      if (act === "savedrawer") { C.drawer = null; rerender(); return toast($(".win-body"), "Profile saved · applies at next reprovision"); }
      if (act === "resume") { toast(body, "Resuming dev-2 on buildbox…", "play"); return; }
      if (act === "open") return toast(body, "Opening VS Code · Remote-SSH agent-vm", "code");
      if (act === "openfwd") return toast(body, "Opening http://localhost:" + a.dataset.port, "ext");
      if (act === "toast") return toast(body, a.dataset.m, "info");
    });
    win.addEventListener("change", (e) => {
      const t = e.target;
      if (t.matches("[data-proj]")) { const p = C.projects.find((x) => x.id === t.dataset.proj); p.on = t.checked; return rerender(); }
      if (t.matches("input[name=cr]")) { C.custom = t.value; return; }
      if (C.page === "access" && t.type === "checkbox" && t.id !== "a-t3") { C.pending.add(t.id); rerender(); }
      if (C.page === "access" && t.id === "a-t3") toast(body, t.checked ? "Installing T3 Code on agent-vm now…" : "T3 Code web GUI stopped", "bolt");
    });
    win.addEventListener("input", (e) => {
      const t = e.target;
      if (t.id === "confirmIn") { $("#confirmGo").disabled = t.value.trim() !== "agent-vm"; }
      if (t.matches("[data-res]")) { const b = $("#resApply"); b.disabled = false; $("#resNote").innerHTML = "<b>Pending:</b> saving shuts agent-vm down, resizes it and starts it again. One UAC prompt. Claude's turn gets interrupted."; }
      if (t.matches("[data-pend]")) { if (!C.pending.has(t.dataset.pend)) { C.pending.add(t.dataset.pend); const pos = t.selectionStart; rerender(); const n = $(`[data-pend=${t.dataset.pend}]`); n.focus(); n.setSelectionRange(pos, pos); } }
    });
    const go_ = $("#confirmGo");
    if (go_) go_.addEventListener("click", () => {
      C.dzStep = 1; rerender();
      C.timer = setInterval(() => { C.dzStep++; if (C.dzStep > 4) clearInterval(C.timer); if (App.surface === "panel" && C.dialog) rerender(); }, 1400);
    });
  }
  App.register("panel", { render, mount });
})();
