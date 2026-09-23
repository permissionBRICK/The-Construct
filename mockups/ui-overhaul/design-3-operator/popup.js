/* Operator Line: operator command set (shared by popup + control panel) and the tray popup. */
(function () {
  "use strict";
  const { icon, D, esc, Palette, toast, dropdown } = window.OL;

  /* ---------- shared operator state ---------- */
  const S = window.OL.S = {
    active: "agent-vm",
    mic: true,
    acked: new Set(),
    closed: new Set(),
    staged: null,            // "after-claude" when a reprovision is staged
    wake: false,
    vmState: { "agent-vm": "running", "dev-2": "saved", "win-test": "running" },
    listeners: [],
    emit() { this.listeners.forEach((f) => f()); },
  };
  window.OL.inst = (id) => D.instances.find((i) => i.id === id);

  function dangerConfirm(verb, vm, extra) {
    const impactByVerb = {
      Reinstall: [
        ["Unsaved work", '<b>construct</b> 2 unpushed commits on <span class="mono">fix/hyperv-late-windows-activation</span> · <b>gitgudlab</b> 1 uncommitted file'],
        ["Backup", "agent config (Claude, Codex, SSH keys) captured first, restored after"],
        ["Running agents", "Claude Code is mid-turn (6 min) and will be stopped"],
        ["Downtime", "about 12 min · 1 UAC prompt"],
      ],
      Redownload: [
        ["Download", "Ubuntu 26.04 LTS server ISO, about 3 GB, SHA256 checked"],
        ["Unsaved work", '<b>construct</b> 2 unpushed commits · <b>gitgudlab</b> 1 uncommitted file'],
        ["Backup", "agent config captured first, restored after"],
        ["Downtime", "about 18 min · 1 UAC prompt"],
      ],
      "Clean-wipe reinstall": [
        ["Unsaved work", '<b>lost</b>: construct 2 unpushed commits, gitgudlab 1 uncommitted file'],
        ["Backup", "<b>none</b>: no config is restored, agents need sign-in again"],
        ["Keeps", "project profiles on this PC, instance settings"],
        ["Downtime", "about 14 min · 1 UAC prompt"],
      ],
      Remove: [
        ["Hyper-V VM", '<span class="mono">agent-vm</span> and its 146 GB VHDX'],
        ["Child VMs", '<span class="mono">win-test</span> (lease 5h 20m) is deleted too'],
        ["This PC", "SSH config entry, VS Code remote entry, T3 CA certificate, registry entry"],
        ["Unsaved work", '<b>lost</b>: construct 2 unpushed commits'],
      ],
    };
    const cons = {
      Reinstall: "Deletes the VM and rebuilds it from the current Ubuntu ISO. Anything not pushed to a remote is lost.",
      Redownload: "Fetches a fresh Ubuntu ISO, then deletes and rebuilds the VM. Anything not pushed to a remote is lost.",
      "Clean-wipe reinstall": "Rebuilds from scratch without restoring the saved agent config. Anything not pushed to a remote is lost.",
      Remove: "Removes the instance from this PC for good. The VM, its disk and its child VMs are deleted.",
    };
    return {
      name: vm, verb, icon: verb === "Remove" ? "trash" : "alert",
      title: verb + " " + vm, consequence: cons[verb], impact: impactByVerb[verb],
      run: extra,
    };
  }
  window.OL.dangerConfirm = dangerConfirm;

  /* ---------- the operator command list ----------
     env: { surface: "popup"|"panel", toastHost(), openPanel(tab), danger(verb) } */
  function operatorCommands(env) {
    const T = (m, k, u) => toast(env.toastHost(), m, k, u);
    const a = S.active, inst = window.OL.inst(a);
    const cmds = [];
    const add = (c) => cmds.push(c);

    // forwards
    D.forwards.filter((f) => !S.closed.has(f.id)).forEach((f) => {
      const lbl = ":" + f.vmPort + (f.remap ? " → " + f.local.split(":")[1] : "");
      add({ group: "Forwards", icon: "forward", title: "Open " + lbl + " · " + f.label, sub: f.state === "queued" ? "queued · " + f.note : f.url + " · " + f.vm + " · by " + f.by, kw: f.vmPort + " " + f.local + " forward expose port open " + f.label, hint: f.state === "queued" ? '<span class="tag">queued</span>' : '<span class="tag ok">open</span>', suggest: f.state === "open", boost: 5,
        run: () => T(f.state === "queued" ? "dev-2 is saved. The forward opens when it resumes." : "Opened <span class='mono'>" + f.url + "</span> in your browser", f.state === "queued" ? "warn" : "ok") });
      add({ group: "Forwards", icon: "copy", title: "Copy URL " + f.url, kw: f.vmPort + " copy link url", run: () => T("Copied " + f.url, "ok") });
      add({ group: "Forwards", icon: "x", title: "Close forward :" + f.vmPort, sub: f.label + " · the agent can open it again with construct expose", kw: f.vmPort + " close unexpose stop forward", run: () => { S.closed.add(f.id); S.emit(); T("Closed forward :" + f.vmPort, "ok", () => { S.closed.delete(f.id); S.emit(); }); } });
    });

    // open / connect
    const running = S.vmState[a] === "running";
    if (running) {
      add({ group: "Open", icon: "code", title: "Open " + a + " in VS Code", sub: "Remote-SSH · remembered default", kw: "vscode code connect ssh open", suggest: true, hint: "", run: () => T("Opening VS Code on " + a + "…", "ok") });
      add({ group: "Open", icon: "ext", title: "Open T3 Code web UI", sub: "https://agent-vm.mshome.net:3773 · 2 threads", kw: "t3 web ui browser threads", run: () => T("Opened T3 Code in your browser", "ok") });
      add({ group: "Open", icon: "terminal", title: "Open browser console", sub: "Hyper-V console for " + a, kw: "console serial screen", run: () => T("Opened console", "ok") });
      add({ group: "Open", icon: "code", title: "Open VS Code serve-web", sub: "http://agent-vm.mshome.net:8000", kw: "serve web browser ide 8000", run: () => T("Opened serve-web", "ok") });
    }

    // lifecycle
    D.instances.forEach((i) => {
      if (i.parent) return;
      if (i.behind || i.id === "agent-vm") {
        add({ group: "Lifecycle", icon: "cycle", title: "Reprovision " + i.id, sub: i.behind ? i.behind + " commits behind · keeps all data" + (i.id === "agent-vm" ? " · Claude is working" : "") : "re-run setup · keeps all data", kw: "reprovision rebuild provision update setup rep", suggest: i.id === a && !!i.behind, subMenu: true, boost: i.id === a ? 8 : 0,
          run: () => ({ sub: { title: "Reprovision " + i.id, placeholder: "When should it run?", cmds: [
            { icon: "clock", title: "When Claude Code finishes its turn", sub: "recommended · waits for the agent to go idle, then runs", run: () => { S.staged = "after-claude"; S.emit(); T("Reprovision staged: runs when Claude goes idle", "ok", () => { S.staged = null; S.emit(); }); } },
            { icon: "cycle", title: "Now, and interrupt Claude Code", sub: "the running turn is lost · about 6 min · keeps all data", run: () => T("Reprovisioning " + i.id + "…", "warn") },
            { icon: "eye", title: "Show what changed (c21978b → b5c4348)", sub: "2 commits", run: () => { env.openPanel("timeline"); } },
          ] } }) });
      }
    });
    add({ group: "Power", icon: running ? "stop" : "play", title: (running ? "Stop " : S.vmState[a] === "saved" ? "Resume " : "Start ") + a, sub: running ? "graceful shutdown · agents stop" : S.vmState[a] === "saved" ? "resume from saved state · about 9 s" : "one UAC prompt", kw: "power start stop resume boot shutdown", suggest: !running,
      run: () => { S.vmState[a] = running ? "off" : "running"; S.emit(); T(a + (running ? " is shutting down" : " is resuming"), "ok"); } });
    if (running) add({ group: "Power", icon: "cycle", title: "Restart " + a, kw: "reboot restart power", run: () => T("Restarting " + a, "ok") });
    add({ group: "Update", icon: "upd", title: "Update Construct to 3a91f0e", sub: "installed b5c4348 · 4 commits", kw: "update upgrade construct companion", run: () => T("Construct update downloading…", "ok") });

    // instances
    D.instances.filter((i) => i.id !== a).forEach((i) => {
      add({ group: "Switch instance", icon: i.parent ? "child" : "vm", title: "Switch to " + i.id, sub: S.vmState[i.id] + (i.parent ? " · child of " + i.parent + " · lease " + i.lease : i.remote ? " · " + i.remote : ""), kw: "switch instance vm go " + i.id, run: () => { S.active = i.id; S.emit(); } });
    });

    // mic / agents
    add({ group: "Voice", icon: "mic", title: S.mic ? "Mic passthrough: disarm" : "Mic passthrough: arm", sub: "capture device " + D.mic.device + " · vm:8767 → host mic", kw: "mic voice microphone audio on off", suggest: false, run: () => { S.mic = !S.mic; S.emit(); T("Mic passthrough " + (S.mic ? "armed" : "disarmed"), "ok"); } });
    add({ group: "Agents", icon: "bell", title: "Wake me when Claude Code finishes", sub: "toast when the current turn ends", kw: "wake notify done bell claude", run: () => { S.wake = true; S.emit(); T("You'll get a toast when Claude goes idle", "ok"); } });
    add({ group: "Agents", icon: "download", title: "Update Claude Code 2.4.1 → 2.4.3", sub: "applies after the current turn", kw: "agent update claude upgrade", run: () => T("Claude Code update queued for after this turn", "ok") });
    add({ group: "Agents", icon: "download", title: "Update all agents", sub: "1 update available", kw: "agents update all upgrade", run: () => T("1 agent update queued", "ok") });

    // usage
    add({ group: "Usage", icon: "dollar", title: "Usage today · $4.12", sub: "claude $3.40 · codex $0.58 · opencode $0.14", kw: "usage cost tokens spend today", run: () => env.openPanel("usage") });
    add({ group: "Usage", icon: "chart", title: "Usage this month · $86.30", sub: "all time $412.77", kw: "usage month cost", run: () => env.openPanel("usage") });
    add({ group: "Usage", icon: "download", title: "Export usage as JSON", kw: "usage export json", run: () => T("Saved usage-2026-09-23.json", "ok") });

    // settings & config
    add({ group: "Settings", icon: "cpu", title: "Set RAM…", sub: "type e.g. ram 16 · restart to apply", kw: "ram memory resources", run: () => env.openPanel("settings", "ram") });
    add({ group: "Settings", icon: "cpu", title: "Set vCPUs…", sub: "type e.g. cpu 6 · restart to apply", kw: "cpu vcpu cores resources", run: () => env.openPanel("settings", "cpu") });
    add({ group: "Settings", icon: "clock", title: "Idle policy for dev-2", sub: "save after 60 min · host enforces", kw: "idle policy save sleep", run: () => { S.active = "dev-2"; S.emit(); env.openPanel("settings", "idle"); } });
    add({ group: "Settings", icon: "upload", title: "Export config", sub: "zip of projects, settings and agent config", kw: "export config backup", run: () => T("Exported construct-config.zip", "ok") });
    add({ group: "Settings", icon: "cycle", title: "Sync config now", sub: "last synced 3 min ago · 1 new profile from VM", kw: "sync config git", run: () => T("Config synced · jarvis profile pulled", "ok") });
    add({ group: "Settings", icon: "folder", title: "Projects for next reprovision", sub: "4 of 5 selected · jarvis is new", kw: "projects profiles select", run: () => env.openPanel("projects") });
    add({ group: "Settings", icon: "gear", title: "Open settings", kw: "settings preferences config", run: () => env.openPanel("settings") });
    add({ group: "Settings", icon: "moon", title: "Toggle light / dark theme", kw: "theme dark light appearance", run: () => document.getElementById("themeBtn").click() });
    add({ group: "Open", icon: "sidebar", title: "Open control panel", kw: "panel window control", run: () => env.openPanel("timeline") });
    add({ group: "Open", icon: "server", title: "Open host panel · buildbox", sub: "you are an admin on buildbox", kw: "host admin buildbox", run: () => { location.hash = "#host"; } });

    // children
    add({ group: "Child VMs", icon: "clock", title: "Extend win-test lease +4h", sub: "lease 5h 20m left", kw: "child lease extend win-test", run: () => T("win-test lease extended to 9h 20m", "ok") });
    add({ group: "Child VMs", icon: "terminal", title: "Open win-test console", kw: "child console win-test windows", run: () => T("Opened win-test console", "ok") });

    // danger (never executed from the popup)
    [["Reinstall", "rebuild from current ISO"], ["Redownload", "fresh Ubuntu ISO, then rebuild"], ["Clean-wipe reinstall", "custom reinstall · nothing restored"], ["Remove", "delete the instance from this PC"]].forEach(([v, s]) => {
      add({ group: "Danger zone", icon: v === "Remove" ? "trash" : "alert", title: v + " " + a + "…", sub: s + (env.surface === "popup" ? " · opens in the control panel" : " · type the instance name"), kw: "danger destructive " + v.toLowerCase() + " delete wipe", danger: env.surface !== "popup", boost: -20,
        confirm: env.surface !== "popup" ? dangerConfirm(v, a, () => env.dangerDone && env.dangerDone(v)) : null,
        run: () => env.danger(v) });
    });
    add({ group: "Danger zone", icon: "server", title: "Make this PC a Construct host…", sub: "adopt agent-vm, other users can create VMs here", kw: "host share pc make", boost: -20, run: () => env.openPanel("danger") });
    return cmds;
  }
  window.OL.operatorCommands = operatorCommands;

  function dynamicCommands(q, env) {
    const T = (m, k) => toast(env.toastHost(), m, k);
    q = q.trim().toLowerCase();
    let m;
    if ((m = q.match(/^:?(\d{2,5})$/))) {
      const port = +m[1];
      if (D.forwards.some((f) => String(f.vmPort).startsWith(m[1]) && !S.closed.has(f.id))) return [];
      return [{ group: "Forwards", icon: "plus", title: "Expose vm:" + port + " on this PC", sub: "open a forward the same way construct expose does", run: () => T("Forward vm:" + port + " → localhost:" + port + " opening…", "ok") }];
    }
    if ((m = q.match(/^ram\s+(\d{1,2})$/))) return [{ group: "Settings", icon: "cpu", title: "Set RAM of " + S.active + " to " + m[1] + " GB", sub: "currently 16 GB · save & restart to apply · 1 UAC prompt", run: () => T("RAM set to " + m[1] + " GB, applies on restart", "ok") }];
    if ((m = q.match(/^cpu\s+(\d{1,2})$/))) return [{ group: "Settings", icon: "cpu", title: "Set vCPUs of " + S.active + " to " + m[1], sub: "currently 8 · restart to apply", run: () => T("vCPUs set to " + m[1] + ", applies on restart", "ok") }];
    return [];
  }
  window.OL.dynamicCommands = dynamicCommands;

  /* ================= popup rendering ================= */
  const root = document.getElementById("popup");
  if (!root) return;
  const state = { filter: "all", sel: null };
  const env = {
    surface: "popup",
    toastHost: () => root.querySelector(".pp-toast"),
    openPanel: (tab, focus) => { location.hash = "#panel/" + (tab || "timeline"); window.OL.panelFocus && window.OL.panelFocus(focus); },
    danger: (verb) => { location.hash = "#panel/danger"; setTimeout(() => window.OL.panelDanger && window.OL.panelDanger(verb), 60); },
  };

  root.innerHTML = `
    <div class="pp-head" id="ppHead"></div>
    <div class="pp-cmd">
      <label class="cmdbar">${icon("search")}<input id="ppInput" placeholder="Type a command or port…" autocomplete="off" spellcheck="false" aria-label="Command bar"><span class="kbds"><kbd>Win</kbd><kbd>Alt</kbd><kbd>C</kbd></span></label>
    </div>
    <div class="pp-sugg" id="ppSugg"><span class="lbl">try</span><button data-q="5173">5173</button><button data-q="rep">rep</button><button data-q="mic">mic</button><button data-q="switch dev">switch dev</button><button data-q="ram 12">ram 12</button><button data-q="reinstall">reinstall</button></div>
    <div class="pp-body">
      <div class="pp-results" id="ppResults"></div>
      <div id="ppMain" style="display:flex;flex-direction:column;flex:1;min-height:0"></div>
    </div>
    <div class="pp-foot"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>↵</kbd> run</span><span><kbd>Tab</kbd> filter</span><span><kbd>Ctrl</kbd><kbd>P</kbd> panel</span><span class="spacer"></span><span id="ppSample">sampled 8 s ago</span></div>
    <div class="pp-toast"></div>`;

  const input = root.querySelector("#ppInput"), results = root.querySelector("#ppResults");
  const pal = Palette({
    input, list: results, placeholder: "Type a command or port…", max: 30,
    getCommands: () => operatorCommands(env),
    dynamic: (q) => dynamicCommands(q, env),
    onClose: () => { results.classList.remove("on"); input.blur(); },
    onState: () => {},
  });
  input.addEventListener("focus", () => { results.classList.add("on"); pal.render(); });
  input.addEventListener("blur", () => setTimeout(() => { if (!input.value && !pal.stack.length && document.activeElement !== input) results.classList.remove("on"); }, 150));
  root.querySelector("#ppSugg").addEventListener("click", (e) => { const b = e.target.closest("[data-q]"); if (b) typeInto(b.dataset.q); });

  function typeInto(q) {
    input.focus(); pal.set("");
    results.classList.add("on");
    let i = 0;
    clearInterval(typeInto.t);
    typeInto.t = setInterval(() => { i++; input.value = q.slice(0, i); pal.q = input.value; pal.idx = 0; pal.render(); if (i >= q.length) clearInterval(typeInto.t); }, 70);
  }
  window.OL.popupType = typeInto;

  function renderHead() {
    const h = root.querySelector("#ppHead");
    h.innerHTML = D.instances.map((i) => {
      const st = S.vmState[i.id];
      return `<button class="pp-inst" data-inst="${i.id}" aria-pressed="${S.active === i.id}" title="${i.id} · ${st}">${dot(st)}${i.id}${i.parent ? '<span class="child">child</span>' : ""}</button>`;
    }).join("") + `<span class="spacer"></span><span class="cost" title="Token spend today, all agents">$4.12<small>today</small></span>
      <button class="btn sm icon ghost" id="ppMic" title="Mic passthrough ${S.mic ? "armed" : "off"}" style="${S.mic ? "color:var(--accent)" : ""}">${icon("mic")}</button>
      <button class="btn sm icon ghost" id="ppPanel" title="Open control panel (Ctrl+P)">${icon("sidebar")}</button>`;
    h.onclick = (e) => {
      const b = e.target.closest("[data-inst]"); if (b) { S.active = b.dataset.inst; S.emit(); return; }
      if (e.target.closest("#ppMic")) { S.mic = !S.mic; S.emit(); toast(env.toastHost(), "Mic passthrough " + (S.mic ? "armed · Shure MV7" : "disarmed"), "ok"); }
      if (e.target.closest("#ppPanel")) env.openPanel("timeline");
    };
  }
  function dot(st) { return `<span class="dot ${st === "running" ? "running" : st === "saved" ? "saved" : "off"}"></span>`; }

  function renderMain() {
    const a = S.active, i = window.OL.inst(a), st = S.vmState[a];
    const m = root.querySelector("#ppMain");
    let html = '<div class="pp-vm">';
    // identity line
    html += `<div class="pp-vm-row">${dot(st)}<span class="nm">${a}</span><span class="meta">${
      st === "running" ? (i.parent ? "child of " + i.parent + " · up " + i.uptime : "running · up " + i.uptime + " · 11.2/16 GB · " + i.backend) :
      st === "saved" ? "saved by idle policy " + i.savedAgo + " ago · buildbox" : "stopped"}</span></div>`;
    // badges
    const badges = [];
    if (a === "agent-vm") {
      if (S.staged) badges.push(`<span class="tag acc" data-b="staged">${icon("clock", "sm")} reprovision when Claude idles</span>`);
      else badges.push(`<span class="tag warn" data-b="behind" title="Provisioned at c21978b, installed Construct is b5c4348">${icon("cycle", "sm")} behind 2 · reprovision</span>`);
      badges.push(`<span class="tag info" data-b="update">${icon("upd", "sm")} update 3a91f0e</span>`);
      badges.push(`<span class="tag warn" data-b="disk">${icon("disk", "sm")} disk 88%</span>`);
    } else if (a === "dev-2") {
      badges.push(`<span class="tag saved">${icon("save", "sm")} saved 41 min</span><span class="tag outline">idle → save after 60 min</span><span class="tag outline">4 vCPU · 12 GB</span>`);
    } else {
      badges.push(`<span class="tag acc">${icon("clock", "sm")} lease 5h 20m</span><span class="tag outline">Windows 11 eval</span><span class="tag outline">4 GB</span>`);
    }
    html += `<div class="pp-badges">${badges.join("")}</div>`;
    // live row
    if (st === "running" && a !== "win-test") {
      const fw = D.forwards.filter((f) => f.vm === a && !S.closed.has(f.id));
      html += `<div class="pp-live"><span class="split"><button class="btn primary" id="ppOpen">${icon("code")}Open in VS Code</button><button class="btn primary" id="ppOpenDD" aria-label="Choose open target">${icon("down", "sm")}</button></span>` +
        fw.map((f) => `<button class="fwd-chip" data-fw="${f.id}" title="${f.label} · ${f.url}"><span class="dot running"></span>:${f.vmPort}${f.remap ? "→" + f.local.split(":")[1] : ""}${icon("ext", "sm")}</button>`).join("") + `</div>`;
    } else if (st === "running") {
      html += `<div class="pp-live"><button class="btn primary">${icon("terminal")}Open console <kbd>↵</kbd></button><button class="btn">${icon("clock")}Extend lease +4h</button></div>`;
    } else {
      html += `<div class="pp-live"><button class="btn primary" id="ppResume">${icon("play")}Resume ${a} <kbd>↵</kbd></button><span class="dim" style="font-size:11px">about 9 s · no UAC (remote)</span>` +
        D.forwards.filter((f) => f.vm === a).map((f) => `<button class="fwd-chip queued" data-fw="${f.id}" title="Queued · opens when dev-2 resumes">:${f.vmPort} queued</button>`).join("") + `</div>`;
    }
    html += "</div>";

    // agents
    if (a !== "win-test") {
      html += `<div class="pp-sec">Agents<span class="spacer"></span>${a === "agent-vm" ? '<span style="text-transform:none;letter-spacing:0;font-weight:500">' + (S.wake ? '<span style="color:var(--accent)">' + icon("bell", "sm") + " wake armed</span>" : "1 update") + "</span>" : ""}</div><div class="pp-agents">`;
      if (st !== "running") {
        html += D.agents.slice(0, 2).map((g) => `<div class="agent-row paused"><span class="dot off"></span><span class="an">${g.name}</span><span class="aa">paused with the VM</span><span class="ar"></span></div>`).join("");
      } else {
        html += D.agents.map((g) => {
          const d = g.state === "working" ? "busy" : g.state === "active" ? "running" : "off";
          const act = g.state === "working" ? `<b>working</b> · ${g.repo} · “${g.turn}”` : g.turn;
          const r = g.state === "working" ? g.since : g.latest ? '<span class="tag info" style="height:16px">' + g.latest + "</span>" : "";
          return `<div class="agent-row" title="${g.name} ${g.ver}"><span class="dot ${d}"></span><span class="an">${g.name}</span><span class="aa">${act}</span><span class="ar">${r}</span></div>`;
        }).join("");
      }
      html += "</div>";
    }

    // events
    const evs = (D.events[a] || []);
    const unread = evs.filter((e) => e.unread && !S.acked.has(e.id)).length;
    html += `<div class="pp-events"><div class="pp-sec">Events<span class="spacer"></span><span class="seg" style="text-transform:none;letter-spacing:0" id="ppSeg">
      <button aria-pressed="${state.filter === "all"}" data-f="all">All</button>
      <button aria-pressed="${state.filter === "notify"}" data-f="notify">Notify${unread ? ' <span class="n">' + unread + "</span>" : ""}</button>
      <button aria-pressed="${state.filter === "forward"}" data-f="forward">Forwards</button></span></div><div class="ev-list scroll" id="ppEv">`;
    const list = evs.filter((e) => state.filter === "all" || e.kind === state.filter);
    if (!list.length) html += '<div class="pp-empty">Nothing here yet.</div>';
    list.forEach((e) => html += evRow(e));
    html += "</div></div>";
    m.innerHTML = html;
    root.querySelector("#ppSample").textContent = st === "running" ? "sampled 8 s ago" : a === "dev-2" ? "host state · 20 s ago" : "sampled 8 s ago";
  }

  function evRow(e) {
    const acked = S.acked.has(e.id), closed = e.entity[0] === "forward" && S.closed.has(e.entity[1]);
    const staged = e.id === "e12" && S.staged;
    const cls = ["ev", e.unread && !acked ? "unread" : "", e.level === "error" ? "error" : "", acked || closed ? "done" : ""].join(" ");
    const kic = { forward: "forward", notify: "bell", agent: "agent", lifecycle: e.title.includes("rovision") ? "commit" : "vm", update: "upd" }[e.kind];
    let acts = (e.acts || []).filter(() => !closed && !acked).map((x) => `<button class="btn sm ghost ${x[2] || ""}" data-ev="${e.id}" data-a="${x[0]}" title="${x[0]}">${icon(x[1], "sm")}${x[2] ? x[0] : ""}</button>`).join("");
    if (staged) acts = `<span class="tag acc">staged</span>`;
    if (acked) acts = `<span class="dim" style="font-size:10.5px">acked</span>`;
    if (closed) acts = `<span class="dim" style="font-size:10.5px">closed</span>`;
    return `<div class="${cls}" data-id="${e.id}"><span class="t">${e.t}</span><span class="k ${e.kind} ${e.level || ""}">${icon(kic)}</span>
      <span class="m"><span class="x">${e.title}${e.vm && e.vm !== S.active ? '<span class="vmtag">' + e.vm + "</span>" : ""}</span><span class="y">${e.detail}</span></span><span class="acts">${acts}</span></div>`;
  }

  root.addEventListener("click", (e) => {
    const seg = e.target.closest("#ppSeg [data-f]");
    if (seg) { state.filter = seg.dataset.f; renderMain(); return; }
    const act = e.target.closest("[data-ev]");
    if (act) {
      const ev = Object.values(D.events).flat().find((x) => x.id === act.dataset.ev), a = act.dataset.a;
      const T = (m, k, u) => toast(env.toastHost(), m, k, u);
      if (a === "Ack") { S.acked.add(ev.id); S.emit(); T("Acknowledged", "ok", () => { S.acked.delete(ev.id); S.emit(); }); }
      else if (a === "Close") { S.closed.add(ev.entity[1]); S.emit(); T("Forward closed", "ok", () => { S.closed.delete(ev.entity[1]); S.emit(); }); }
      else if (a === "Open") T("Opened in your browser", "ok");
      else if (a === "Resume") { S.vmState["dev-2"] = "running"; S.emit(); T("dev-2 resuming · forward :3000 will open", "ok"); }
      else if (a === "Reprovision") { typeInto("rep"); }
      else if (a === "Wake me") { S.wake = true; S.emit(); T("You'll get a toast when Claude goes idle", "ok"); }
      else if (a === "Update") T("Construct update downloading…", "ok");
      else T(a + " · done", "ok");
      return;
    }
    const fw = e.target.closest("[data-fw]");
    if (fw) { const f = D.forwards.find((x) => x.id === fw.dataset.fw); toast(env.toastHost(), f.state === "queued" ? "Queued until dev-2 resumes" : "Opened <span class='mono'>" + f.url + "</span>", f.state === "queued" ? "warn" : "ok"); return; }
    if (e.target.closest("#ppOpen")) { toast(env.toastHost(), "Opening VS Code on agent-vm…", "ok"); return; }
    if (e.target.closest("#ppOpenDD")) {
      dropdown(e.target.closest("#ppOpenDD"), `<div class="dd-h">Open agent-vm with</div>
        <div class="dd-i active" data-v="VS Code">${icon("check")}<span>VS Code (Remote-SSH)<small>default · remembered</small></span><span class="r">↵</span></div>
        <div class="dd-i" data-v="T3 Code"><span></span><span>T3 Code web UI<small>:3773 · 2 threads</small></span></div>
        <div class="dd-i" data-v="serve-web"><span></span><span>VS Code serve-web<small>browser IDE :8000</small></span></div>
        <div class="dd-i" data-v="console"><span></span><span>Hyper-V console</span></div>`, (v) => toast(env.toastHost(), "Opening " + v + " · now the default", "ok"));
      return;
    }
    if (e.target.closest("#ppResume")) { S.vmState[S.active] = "running"; S.emit(); toast(env.toastHost(), S.active + " resumed in 9 s", "ok"); return; }
    const b = e.target.closest("[data-b]");
    if (b) {
      if (b.dataset.b === "behind") typeInto("rep");
      if (b.dataset.b === "staged") { S.staged = null; S.emit(); toast(env.toastHost(), "Staged reprovision cancelled", "ok"); }
      if (b.dataset.b === "update") typeInto("update construct");
      if (b.dataset.b === "disk") env.openPanel("settings", "disk");
    }
  });
  root.addEventListener("keydown", (e) => {
    if (e.key === "Tab" && document.activeElement !== input && e.target.closest(".pp-events")) {
      e.preventDefault(); const order = ["all", "notify", "forward"]; state.filter = order[(order.indexOf(state.filter) + 1) % 3]; renderMain();
    }
  });

  /* ---------- tray right-click menu ---------- */
  function renderTray() {
    const tm = document.getElementById("traymenu");
    const a = S.active, st = S.vmState[a];
    tm.innerHTML = `<div class="tm-cap">${icon("right", "sm")} right-click on the tray icon</div>
      <div class="tm-head"><span class="cx">${icon("terminal")}</span><div><b>The Construct</b><small>${a} · ${st}${a === "agent-vm" ? " · Claude working" : ""}</small></div></div>
      <div class="tm-sep"></div>
      <div class="tm-item" data-t="palette">${icon("search")}<span>Command bar…</span><span class="r kbds"><kbd>Win</kbd><kbd>Alt</kbd><kbd>C</kbd></span></div>
      <div class="tm-item" data-t="panel">${icon("sidebar")}<span>Open control panel</span><span class="r"></span></div>
      <div class="tm-item" data-t="vscode">${icon("code")}<span>Open ${a} in VS Code</span><span class="r"></span></div>
      <div class="tm-sep"></div>
      <div class="tm-item">${icon("vm")}<span>Instance</span><span class="r">${a} ${icon("right", "sm")}</span>
        <div class="sub">${D.instances.map((i) => `<div class="tm-item" data-inst="${i.id}">${S.active === i.id ? icon("check") : "<span></span>"}<span>${i.id}</span><span class="r">${dot(S.vmState[i.id])}${S.vmState[i.id]}</span></div>`).join("")}</div></div>
      <div class="tm-item">${icon("forward")}<span>Forwards</span><span class="r">${D.forwards.filter((f) => f.state === "open" && !S.closed.has(f.id)).length} open ${icon("right", "sm")}</span>
        <div class="sub">${D.forwards.map((f) => `<div class="tm-item" data-t="fw">${icon(f.state === "open" ? "ext" : "clock")}<span class="mono" style="font-size:11.5px">:${f.vmPort}${f.remap ? "→18800" : ""}</span><span class="r">${f.state === "open" ? f.label : "queued"}</span></div>`).join("")}</div></div>
      <div class="tm-item" data-t="mic">${S.mic ? icon("check") : "<span></span>"}<span>Mic passthrough</span><span class="r">${S.mic ? "armed · MV7" : "off"}</span></div>
      <div class="tm-item" data-t="dnd"><span></span><span>Pause notifications for 1 h</span><span class="r"></span></div>
      <div class="tm-sep"></div>
      <div class="tm-item" data-t="power">${icon(st === "running" ? "stop" : "play")}<span>${st === "running" ? "Stop" : "Resume"} ${a}</span><span class="r"></span></div>
      <div class="tm-item" data-t="host">${icon("server")}<span>Host panel · buildbox</span><span class="r">${icon("ext", "sm")}</span></div>
      <div class="tm-sep"></div>
      <div class="tm-item" data-t="quit"><span></span><span>Quit Companion</span><span class="r"></span></div>`;
  }
  document.getElementById("traymenu").addEventListener("click", (e) => {
    const it = e.target.closest("[data-inst]"); if (it) { S.active = it.dataset.inst; S.emit(); return; }
    const t = e.target.closest("[data-t]"); if (!t) return;
    const k = t.dataset.t;
    if (k === "palette") input.focus();
    else if (k === "panel") env.openPanel("timeline");
    else if (k === "host") location.hash = "#host";
    else if (k === "mic") { S.mic = !S.mic; S.emit(); }
    else if (k === "power") { S.vmState[S.active] = S.vmState[S.active] === "running" ? "off" : "running"; S.emit(); }
    else toast(env.toastHost(), t.textContent.trim(), "ok");
  });

  function renderAll() {
    renderHead(); renderMain(); renderTray();
    const b = document.getElementById("trayBadge");
    if (b) b.className = "badge" + (S.vmState[S.active] === "running" ? "" : " saved");
    if (results.classList.contains("on")) pal.render();
  }
  S.listeners.push(renderAll);
  renderAll();
  window.OL.popup = { pal, input, results, state, renderAll };
})();
