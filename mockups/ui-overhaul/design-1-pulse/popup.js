/* Surface 1: Windows desktop, tray popup and tray context menu */
"use strict";
(function () {
  const P = {
    active: "agent-vm", busy: true, power: "running", armed: false, mic: "armed",
    sheet: null, target: "vscode", unread: 3, popupOpen: true, ctxOpen: true, reprov: 0, behind: 2, whatsNew: false,
  };
  const TARGETS = {
    vscode: { icon: "code", label: "Open in VS Code", sub: "Remote-SSH · agent-vm · construct" },
    t3: { icon: "globe", label: "Open T3 Code", sub: "Web UI · 2 threads active" },
    console: { icon: "window", label: "Open browser console", sub: "VS Code serve-web · port 8000" },
    ssh: { icon: "terminal", label: "Open terminal (SSH)", sub: "root@agent-vm" },
  };
  const scen = () => P.active === "dev-2" ? "saved" : P.power === "off" ? "off" : P.busy ? "working" : "idle";
  function setScenario(s) {
    P.sheet = null; P.reprov = 0;
    if (s === "saved") { P.active = "dev-2"; P.power = "running"; P.busy = true; }
    else { P.active = "agent-vm"; P.power = s === "off" ? "off" : "running"; P.busy = s === "working"; }
    if (s !== "working") P.armed = false;
  }

  /* ---------- next action ---------- */
  function nextAction() {
    if (P.active === "dev-2") return { icon: "play", label: "Resume dev-2", sub: "Saved by idle policy 41 min ago · about 20 s, no UAC", act: "resume", caret: true };
    if (P.power === "off") return { icon: "power", label: "Start & connect", sub: "One UAC prompt · then opens VS Code", act: "start", caret: true };
    if (P.reprov) return { icon: "refresh", label: `Reprovisioning… ${P.reprov}%`, sub: "Installing agents · step 4 of 7 · data kept", act: "none", disabled: true };
    if (!P.busy && P.behind) return { icon: "refresh", label: "Reprovision now", sub: `${P.behind} commits behind host · all agents idle · about 6 min`, act: "reprov", caret: true };
    const t = TARGETS[P.target];
    return { icon: t.icon, label: t.label, sub: t.sub, act: "open", caret: true };
  }

  /* ---------- rows ---------- */
  function rowAgentVm(active) {
    const running = P.power === "running";
    const dot = !running ? "off" : P.reprov ? "warn" : "run pulse";
    let meta = !running ? "Shut down · 2 h ago" : P.reprov ? "Reprovisioning" : `Up 3h 12m · ${P.busy ? "Claude working" : "agents idle"}`;
    return `<button class="inst-row" data-inst="agent-vm" aria-expanded="${active}">
      <span class="dot ${dot}"></span>
      <span><span class="nm">agent-vm <span class="badge">Local</span></span><span class="meta">${meta}</span></span>
      <span class="cost">${running ? money(4.12) : money(0)}<small>today</small></span></button>`;
  }
  function rowDev2(active) {
    const meta = P.resumed ? "Up 1 min · resumed" : "Saved 41 min ago · idle policy";
    return `<button class="inst-row" data-inst="dev-2" aria-expanded="${active}">
      <span class="dot ${P.resumed ? "run" : "saved"}"></span>
      <span><span class="nm">dev-2 <span class="badge">buildbox</span></span><span class="meta">${meta}</span></span>
      <span class="cost">${money(0.62)}<small>today</small></span></button>`;
  }

  function nextBlock() {
    const n = nextAction();
    const caret = n.caret ? `<button class="btn accent" data-menu="next" aria-label="More open options">${ic("chevd")}</button>` : "";
    let then = "";
    if (P.active === "agent-vm" && P.power === "running" && !P.reprov) {
      if (P.busy && P.behind) {
        then = P.armed
          ? `<div class="then armed">${ic("timer")}<span class="grow"><b>Reprovision armed.</b> It starts when Claude finishes this turn.</span><button class="link" data-act="disarm">Cancel</button></div>`
          : `<div class="then">${ic("timer")}<span class="grow">Then reprovision, once Claude finishes</span><button class="link" data-act="arm">Arm</button></div>`;
      } else if (!P.busy && P.behind) {
        then = `<div class="then">${ic("code")}<span class="grow">Or keep working: <button class="link" data-act="open">Open in VS Code</button></span></div>`;
      }
    }
    if (P.reprov) then = `<div class="progress"><i style="width:${P.reprov}%"></i></div>`;
    return `<div class="next"><div class="split"><button class="btn accent" data-act="${n.act}" ${n.disabled ? "disabled" : ""}>${ic(n.icon, "s20")}<span class="next-lbl"><b>${n.label}</b><small>${n.sub}</small></span></button>${caret}</div>${then}</div>`;
  }

  function micPill() {
    const m = { off: ["", "Mic off"], armed: ["armed", "Mic armed · Shure MV7"], live: ["live", "Mic live"] }[P.mic];
    return `<button class="pill ${m[0]}" data-act="mic" title="Microphone passthrough. Click to cycle off / armed / live">${ic("mic", "s12")}<span class="mic-dot"></span>${m[1]}</button>`;
  }

  function activeAgentVm() {
    const running = P.power === "running";
    const claude = P.busy
      ? `<div class="lead"><svg class="work-ind spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M12 3.5a8.5 8.5 0 1 1-8.5 8.5" /></svg><b>Claude Code</b><span class="muted">working · 6 min · construct</span></div><div class="task">“Refactoring forwarder retry backoff”</div>`
      : `<div class="lead">${ic("okc")}<b>Claude Code</b><span class="muted">finished 2 min ago · construct</span></div><div class="task">Last: “Test suite finished, 3 failures”</div>`;
    const flags = `<div class="inst-flags">
        ${P.behind ? `<button class="chip warn" data-act="maint" title="Installed b5c4348, provisioned at c21978b">${ic("refresh", "s12")}${P.behind} commits behind host</button>` : `<span class="chip ok">${ic("check", "s12")}Up to date</span>`}
        <span class="chip warn" title="146 GB disk, 88% used">${ic("disk", "s12")}Disk 88%</span>
        <span class="chip" title="Child VM win-test">${ic("child", "s12")}1 child</span></div>`;
    if (!running) {
      return `<div class="inst-body"><div class="muted" style="font-size:12.5px">agent-vm is shut down. Forwards and the mic come back once it starts.</div>${nextBlock()}</div>`;
    }
    const fw = D.forwards.map((f) => f.state === "open"
      ? `<span class="fwd" title="${esc(f.label)} · opened by ${f.by}${f.note ? " · " + f.note : ""}"><span class="fl"><span class="dot run sm"></span><b>:${f.vm}${f.pc !== f.vm ? "→" + f.pc : ""}</b><span>${f.label}</span></span><button class="fo" data-act="openfwd" data-port="${f.pc}" aria-label="Open localhost:${f.pc}">${ic("ext", "s12")}</button></span>`
      : `<span class="fwd queued" title="Queued: ${f.note}"><span class="fl">${ic("hourglass", "s12")}<b>:${f.vm}</b><span>queued</span></span></span>`).join("");
    return `<div class="inst-body">
      ${flags}
      <div class="agentline">${claude}<div class="others">Codex idle 48 min · OpenCode :4096 · T3 Code 2 threads</div></div>
      <div class="live"><div class="live-h">Live</div>
        <div class="live-chips">${fw}</div>
        <div class="live-chips">${micPill()}<button class="pill" data-act="notif">${ic("bell", "s12")}${P.unread ? `<b>${P.unread}</b> new notifications` : "No new notifications"}</button></div>
      </div>
      ${nextBlock()}
      <div class="child-row">${ic("child")}<span class="dot run sm"></span><b>win-test</b><span>Windows 11 eval · 4 GB</span><span class="grow"></span><span title="Lease expires at 19:28">${ic("clock", "s12")} 5h 20m left</span></div>
    </div>`;
  }
  function activeDev2() {
    return `<div class="inst-body">
      <div class="inst-flags"><span class="chip">${ic("pause", "s12")}Saved (RAM on disk)</span><span class="chip">${ic("server", "s12")}buildbox.example.local</span><span class="chip ok">${ic("check", "s12")}Up to date</span></div>
      <div class="muted" style="font-size:12.5px">The idle policy saved it after 60 min with no agent activity. Resuming restores open editors and agent sessions exactly where they were.</div>
      ${nextBlock()}
    </div>`;
  }

  /* ---------- sheets ---------- */
  function maintSheet() {
    const a = P.active;
    const isA = a === "agent-vm";
    const rep = isA && P.behind
      ? (P.busy && P.power === "running"
        ? `<div class="mx"><button class="btn sm accent" data-act="arm">${ic("timer", "s12")}When Claude finishes</button><button class="btn sm" data-act="reprov">Now, interrupting Claude</button></div>`
        : `<div class="mx"><button class="btn sm accent" data-act="reprov">Reprovision now</button></div>`)
      : `<div class="mx"><button class="btn sm" data-act="reprov-any">Reprovision anyway</button></div>`;
    const commits = P.whatsNew ? `<ul style="margin:4px 0 0;padding-left:16px;font-size:12px;color:var(--text-2)">${D.update.commits.map((c) => `<li>${c}</li>`).join("")}</ul>` : "";
    return `<div class="sheet-scrim" data-act="close"></div><div class="sheet" role="dialog" aria-label="Maintenance">
      <div class="sheet-h">${ic("gear")}<b>Maintenance · ${a}</b><button class="iconbtn" data-act="close" data-esc aria-label="Close">${ic("x")}</button></div>
      <div class="sheet-b">
        <div class="mrow">${ic("refresh")}<div><div class="mt">Reprovision</div><div class="md">${isA && P.behind ? `${P.behind} commits behind host (provisioned <span class="mono">c21978b</span>, host has <span class="mono">b5c4348</span>). Keeps your data and rebuilds tooling. About 6 min.` : "Up to date with the host. Reprovision re-applies projects and settings."}</div></div><span></span>${rep}</div>
        <div class="mrow">${ic("upd")}<div><div class="mt">Construct update</div><div class="md"><span class="mono">b5c4348 → 3a91f0e</span> · 4 commits. Updates the Companion and the host scripts, and the VM picks it up at the next reprovision.${commits}</div></div><span></span><div class="mx"><button class="btn sm" data-act="upd">Update Construct</button><button class="btn sm subtle" data-act="whatsnew">${P.whatsNew ? "Hide changes" : "What changed"}</button></div></div>
        <div class="mrow">${ic("spark")}<div><div class="mt">Agent updates</div><div class="md">Claude Code 2.4.1 → 2.4.3. Installs after the running turn ends.</div></div><span></span><div class="mx"><button class="btn sm" data-act="agentupd">Update Claude Code</button></div></div>
        <div class="mrow">${ic("sync")}<div><div class="mt">Config sync</div><div class="md">Synced 3 min ago · 1 new profile from the VM (jarvis)</div></div><span></span><div class="mx"><button class="btn sm" data-act="sync">Sync now</button><button class="btn sm subtle" data-go="panel/projects">Review</button></div></div>
        <div class="mrow">${ic("power")}<div><div class="mt">Power</div><div class="md">${a === "dev-2" ? "Saved on buildbox. The host keeps it on disk." : P.power === "off" ? "Shut down." : "Running · 3h 12m. Save keeps RAM on disk and resumes in seconds."}</div></div><span></span>
          <div class="mx">${P.power === "off" && isA ? `<button class="btn sm" data-act="start">${ic("play", "s12")}Start</button>` : a === "dev-2" ? `<button class="btn sm" data-act="resume">${ic("play", "s12")}Resume</button><button class="btn sm">${ic("power", "s12")}Shut down</button>` : `<button class="btn sm" data-act="pw" data-m="Restarting agent-vm">${ic("restart", "s12")}Restart</button><button class="btn sm" data-act="pw" data-m="Saving agent-vm">${ic("save", "s12")}Save state</button><button class="btn sm" data-act="pw" data-m="Shutting down agent-vm">${ic("power", "s12")}Shut down</button>`}</div></div>
        <div class="row" style="padding:6px 4px 0;font-size:12px;color:var(--text-3)">${ic("shield", "s12")}<span class="grow">Reinstall, redownload and remove live in the control panel, under Danger zone.</span><button class="link" data-go="panel/danger">Open</button></div>
      </div></div>`;
  }
  function notifSheet() {
    return `<div class="sheet-scrim" data-act="close"></div><div class="sheet" role="dialog" aria-label="Notifications">
      <div class="sheet-h">${ic("bell")}<b>Notifications</b><button class="btn sm subtle" data-act="readall">Mark all read</button><button class="iconbtn" data-act="close" data-esc aria-label="Close">${ic("x")}</button></div>
      <div class="sheet-b" style="gap:0">${D.notifs.map((n, i) => `<div class="notif" data-act="jump" tabindex="0">${ic(n.lv === "error" ? "err" : "info", "lv-" + n.lv)}<div><div class="nt">${n.msg}</div><div class="nm">${n.agent} · ${n.repo}${i < P.unread ? ' · <b style="color:var(--accent-text)">new</b>' : ""}</div></div><span class="ntime">${n.t}</span></div>`).join("")}
      <div class="row" style="padding:8px 10px 0;font-size:12px;color:var(--text-3)"><span class="grow">Sent by agents with <span class="mono">construct notify</span>. History is kept in the control panel.</span><button class="link" data-go="panel/agents">All</button></div></div></div>`;
  }

  /* ---------- tray context menu ---------- */
  function ctxMenu() {
    return `<div style="position:absolute;right:456px;bottom:60px;display:grid;gap:8px;justify-items:start">
      <span class="callout-lbl" style="position:static">Tray icon · right-click</span>
      <div class="ctx" style="position:relative;right:auto;bottom:auto" role="menu" aria-label="Tray menu">
      <div class="ctx-h">${ic("logo", "s20")}<div><b>Construct</b><span>agent-vm ${P.power === "running" ? "running" : "shut down"} · ${P.busy ? "Claude working" : "agents idle"}</span></div></div>
      <div class="sep"></div>
      <div class="mi" tabindex="0"><span class="tick">${ic("panel")}</span>Open control panel</div>
      <div class="mi" tabindex="0"><span class="tick">${ic("code")}</span>Open in VS Code<span class="k">agent-vm</span></div>
      <div class="mi" tabindex="0"><span class="tick">${ic("fwd")}</span>Open forward<span class="k">2 ${ic("chevr", "s12")}</span>
        <div class="sub leftward">${D.forwards.map((f) => `<div class="mi" tabindex="0"><span class="tick"><span class="dot ${f.state === "open" ? "run" : "off"} sm"></span></span><span class="mono">:${f.vm}${f.pc !== f.vm ? "→" + f.pc : ""}</span><span class="k">${f.label || "queued"}</span></div>`).join("")}</div></div>
      <div class="mi" tabindex="0"><span class="tick">${ic("server")}</span>Switch instance<span class="k">${ic("chevr", "s12")}</span>
        <div class="sub leftward">
          <div class="mi" tabindex="0"><span class="tick">${ic("check", "s12")}</span>agent-vm<span class="k"><span class="dot run sm"></span>running</span></div>
          <div class="mi" tabindex="0"><span class="tick"></span>dev-2<span class="k"><span class="dot saved sm"></span>saved</span></div>
          <div class="mi" tabindex="0"><span class="tick"></span>win-test<span class="k">child · 5h 20m</span></div>
          <div class="sep"></div><div class="mi" tabindex="0"><span class="tick">${ic("plus", "s12")}</span>Add instance…</div></div></div>
      <div class="sep"></div>
      <div class="mi" tabindex="0" data-act="mic"><span class="tick">${P.mic !== "off" ? ic("check", "s12") : ""}</span>Microphone passthrough<span class="k">${P.mic}</span></div>
      <div class="mi" tabindex="0" data-act="notif"><span class="tick">${ic("bell")}</span>Notifications<span class="k">${P.unread || ""}</span></div>
      <div class="mi" tabindex="0"><span class="tick">${ic("power")}</span>Power<span class="k">${ic("chevr", "s12")}</span>
        <div class="sub leftward"><div class="mi" tabindex="0"><span class="tick">${ic("save", "s12")}</span>Save state</div><div class="mi" tabindex="0"><span class="tick">${ic("restart", "s12")}</span>Restart</div><div class="mi" tabindex="0"><span class="tick">${ic("power", "s12")}</span>Shut down</div></div></div>
      <div class="sep"></div>
      <div class="mi" tabindex="0"><span class="tick">${ic("server")}</span>Host panel · buildbox</div>
      <div class="mi" tabindex="0"><span class="tick">${ic("check", "s12")}</span>Start with Windows</div>
      <div class="mi" tabindex="0"><span class="tick"></span>Quit Companion</div>
    </div></div>`;
  }

  /* ---------- render ---------- */
  function popup() {
    const aActive = P.active === "agent-vm";
    return `<div class="popup" id="popup" role="dialog" aria-label="Construct">
      <div class="pp-head">${ic("logo", "s24")}<div class="t"><b>Construct</b><span>${D.pc} · 3 instances · ${money(4.74)} today</span></div>
        <button class="iconbtn" data-act="notif" title="Notifications">${ic("bell")}${P.unread ? `<span class="badge-n">${P.unread}</span>` : ""}</button>
        <button class="iconbtn ${P.sheet === "maint" ? "on" : ""}" data-act="maint" title="Maintenance">${ic("dots")}</button>
        <button class="iconbtn" data-go="panel" title="Open control panel">${ic("panel")}</button></div>
      <div class="pp-body">
        <div class="inst ${aActive ? "active" : ""}">${rowAgentVm(aActive)}${aActive ? activeAgentVm() : ""}</div>
        <div class="inst ${!aActive ? "active" : ""}">${rowDev2(!aActive)}${!aActive ? activeDev2() : ""}</div>
      </div>
      <div class="pp-foot">
        <div class="row" style="color:var(--text-2)">${ic("upd")}<span class="grow">Construct <span class="mono">3a91f0e</span> is available · 4 commits</span><button class="link" data-act="maint">Details</button></div>
        <div class="row"><button class="btn sm subtle" data-go="panel">${ic("panel", "s12")}Control panel</button><button class="btn sm subtle" data-go="host">${ic("server", "s12")}Host</button><span class="grow"></span><span title="Status refreshes every 5 s">Updated 4 s ago</span></div>
      </div>
      ${P.sheet === "maint" ? maintSheet() : P.sheet === "notif" ? notifSheet() : ""}
    </div>`;
  }

  function render() {
    const s = scen();
    const sc = [["working", "Agent working"], ["idle", "Agents idle, behind host"], ["saved", "dev-2 active (saved)"], ["off", "agent-vm shut down"]];
    return `<div class="desk" id="desk">
      <div class="desk-icons">
        <div class="desk-icon"><i>${ic("folder", "s20")}</i>Projects</div>
        <div class="desk-icon"><i>${ic("code", "s20")}</i>VS Code</div>
        <div class="desk-icon"><i>${ic("trash", "s20")}</i>Recycle Bin</div>
      </div>
      <div class="scenario" style="left:110px;right:auto"><span class="lbl">Mockup scenario</span>
        <div class="seg" id="scenSeg">${sc.map(([k, l]) => `<button data-scen="${k}" class="${k === s ? "on" : ""}">${l}</button>`).join("")}</div></div>
      ${P.ctxOpen ? ctxMenu() : ""}
      ${P.popupOpen ? `<div class="popwrap"><span class="callout-lbl">Tray icon · left-click</span>${popup()}</div>` : ""}
      <div class="taskbar">
        <div class="tb-center">
          <span class="tb-app">${ic("win", "s20")}</span><span class="tb-app">${ic("search", "s20")}</span>
          <span class="tb-app run">${ic("folder", "s20")}</span><span class="tb-app run">${ic("code", "s20")}</span><span class="tb-app run">${ic("terminal", "s20")}</span><span class="tb-app">${ic("globe", "s20")}</span>
        </div>
        <div class="tb-tray">
          <button class="tb-t">${ic("upc")}</button>
          <button class="tb-t tray-ico ${P.popupOpen ? "on" : ""}" id="trayIco" title="Construct · agent-vm running · Claude working (left-click: popup, right-click: menu)">${ic("logo", "s20")}<span class="tray-state" style="${P.power === "off" && P.active === "agent-vm" ? "background:var(--neutral-dot)" : ""}"></span></button>
          <button class="tb-t">${ic("wifi")}${ic("vol")}${ic("batt")}</button>
          <button class="tb-t tb-clock">14:08<br>23.09.2026</button>
          <button class="tb-t">${ic("bell")}</button>
        </div>
      </div>
    </div>`;
  }

  function nextMenu() {
    if (P.active === "dev-2") return `<div class="mh">After resume</div><button class="mi">${ic("code")}Resume &amp; open in VS Code</button><button class="mi">${ic("play")}Resume only</button><button class="mi">${ic("globe")}Resume &amp; open T3 Code</button>`;
    if (P.power === "off") return `<button class="mi">${ic("code")}Start &amp; open VS Code</button><button class="mi">${ic("play")}Start only</button>`;
    return `<div class="mh">Open target (remembered)</div>` + Object.entries(TARGETS).map(([k, t]) => `<button class="mi ${k === P.target ? "sel" : ""}" data-target="${k}">${ic(t.icon)}${t.label.replace("Open ", "").replace(/^in /, "")}<span class="mi-sub">${k === P.target ? "default" : ""}</span></button>`).join("") + (P.behind && !P.reprov ? `<div class="sep"></div><button class="mi" data-target="__reprov">${ic("refresh")}Reprovision…</button>` : "");
  }

  function mount(root) {
    const desk = $("#desk", root);
    desk.addEventListener("click", (e) => {
      const g = e.target.closest("[data-go]");
      if (g) { const [s, sub] = g.dataset.go.split("/"); return go(s, sub); }
      const sc = e.target.closest("[data-scen]");
      if (sc) { setScenario(sc.dataset.scen); P.resumed = false; return rerender(); }
      if (e.target.closest("#trayIco")) { P.popupOpen = !P.popupOpen; return rerender(); }
      const mt = e.target.closest("[data-target]");
      if (mt) { closeMenus(); if (mt.dataset.target === "__reprov") { P.sheet = "maint"; } else P.target = mt.dataset.target; return rerender(); }
      const mb = e.target.closest("[data-menu='next']");
      if (mb) { e.stopPropagation(); openMenu(mb, $("#popup", root), nextMenu(), "right"); return; }
      const inst = e.target.closest("[data-inst]");
      if (inst) { P.active = inst.dataset.inst; P.sheet = null; return rerender(); }
      const a = e.target.closest("[data-act]");
      if (!a) return;
      const act = a.dataset.act, pop = $("#popup", root) || desk;
      switch (act) {
        case "mic": P.mic = { off: "armed", armed: "live", live: "off" }[P.mic]; rerender(); break;
        case "notif": P.sheet = "notif"; rerender(); break;
        case "maint": P.sheet = P.sheet === "maint" ? null : "maint"; rerender(); break;
        case "close": P.sheet = null; rerender(); break;
        case "readall": P.unread = 0; rerender(); break;
        case "whatsnew": P.whatsNew = !P.whatsNew; rerender(); break;
        case "arm": P.armed = true; P.sheet = null; rerender(); toast($("#popup", root), "Reprovision armed. It starts when Claude goes idle.", "timer"); break;
        case "disarm": P.armed = false; rerender(); break;
        case "open": toast(pop, "Opening VS Code · Remote-SSH agent-vm…", "code"); break;
        case "openfwd": toast(pop, `Opening http://localhost:${a.dataset.port}`, "ext"); break;
        case "jump": toast(pop, "Would open Agents › Claude Code · construct", "info"); break;
        case "sync": toast(pop, "Config synced · jarvis profile pulled from VM"); break;
        case "upd": toast(pop, "Downloading Construct 3a91f0e…", "upd"); break;
        case "agentupd": toast(pop, "Claude Code 2.4.3 queued. Installs after the current turn.", "spark"); break;
        case "pw": toast(pop, a.dataset.m + "…", "power"); break;
        case "resume": P.resumed = true; P.sheet = null; rerender(); toast($("#popup", root), "dev-2 resumed in 18 s. Opening VS Code…", "play"); break;
        case "start": P.power = "running"; P.busy = false; P.sheet = null; rerender(); toast($("#popup", root), "UAC approved · agent-vm started", "power"); break;
        case "reprov": case "reprov-any":
          P.sheet = null; P.armed = false; P.busy = false; P.reprov = 12; rerender();
          const tick = setInterval(() => {
            if (!P.reprov) return clearInterval(tick);
            P.reprov = Math.min(100, P.reprov + 22);
            if (P.reprov >= 100) { clearInterval(tick); P.reprov = 0; P.behind = 0; }
            if (App.surface === "popup") { rerender(); if (!P.reprov) toast($("#popup"), "Reprovision finished · agent-vm is up to date"); }
          }, 1100);
          break;
      }
    });
    desk.addEventListener("contextmenu", (e) => { if (e.target.closest("#trayIco")) { e.preventDefault(); P.ctxOpen = !P.ctxOpen; rerender(); } });
    $$(".ctx .mi", root).forEach((m) => m.addEventListener("click", (e) => { if (!m.dataset.act) { e.stopPropagation(); toast(desk, "Tray menu: " + m.firstChild.nextSibling.textContent.trim(), "info"); } }));
  }

  App.register("popup", { render: (sub) => { if (sub && sub !== P._sub) { P._sub = sub; setScenario(sub); } return render(); }, mount });
})();
