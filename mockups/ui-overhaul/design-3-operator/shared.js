/* Operator Line: shared icons, sample data, fuzzy palette engine, toasts. */
(function () {
  "use strict";

  /* ---------------- icons ---------------- */
  const P = {
    search: '<circle cx="7" cy="7" r="4.5"/><path d="M10.5 10.5 14 14"/>',
    forward: '<path d="M2 8h8.5M7.5 5l3 3-3 3"/><path d="M13.5 3v10"/>',
    bell: '<path d="M4 11V7a4 4 0 0 1 8 0v4l1.2 1.5H2.8z"/><path d="M6.5 14.2h3"/>',
    cycle: '<path d="M13.2 8.5a5.2 5.2 0 1 1-1.6-4.2"/><path d="M13 2v3h-3"/>',
    power: '<path d="M8 1.8v6"/><path d="M4.6 4.2a5 5 0 1 0 6.8 0"/>',
    play: '<path d="M5 3.3v9.4l7.4-4.7z"/>',
    stop: '<rect x="4" y="4" width="8" height="8" rx="1.2"/>',
    pause: '<path d="M6 4v8M10 4v8"/>',
    mic: '<rect x="6" y="1.8" width="4" height="7.4" rx="2"/><path d="M3.8 7.5a4.2 4.2 0 0 0 8.4 0M8 11.8v2.4"/>',
    gear: '<circle cx="8" cy="8" r="2.2"/><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4"/>',
    cpu: '<rect x="4" y="4" width="8" height="8" rx="1"/><path d="M6 1.5v2M10 1.5v2M6 12.5v2M10 12.5v2M1.5 6h2M1.5 10h2M12.5 6h2M12.5 10h2"/>',
    vm: '<rect x="1.8" y="2.8" width="12.4" height="8.4" rx="1.2"/><path d="M5.5 14h5M8 11.2V14"/>',
    user: '<circle cx="8" cy="5.4" r="2.6"/><path d="M2.8 14a5.2 5.2 0 0 1 10.4 0"/>',
    users: '<circle cx="6" cy="5.5" r="2.3"/><path d="M1.8 13.5a4.2 4.2 0 0 1 8.4 0"/><path d="M10.5 3.4a2.3 2.3 0 0 1 0 4.3M12 9.6a4.2 4.2 0 0 1 2.2 3.9"/>',
    ext: '<path d="M9 2.5h4.5V7M13.5 2.5 7.5 8.5"/><path d="M11.5 9.5v3.8a.7.7 0 0 1-.7.7H3.2a.7.7 0 0 1-.7-.7V5.2a.7.7 0 0 1 .7-.7H7"/>',
    x: '<path d="M4 4l8 8M12 4l-8 8"/>',
    check: '<path d="M3 8.5 6.5 12 13 4.5"/>',
    down: '<path d="M4 6l4 4 4-4"/>',
    right: '<path d="M6 4l4 4-4 4"/>',
    left: '<path d="M10 4 6 8l4 4"/>',
    dollar: '<path d="M8 1.5v13"/><path d="M11 4.6c-.5-1-1.7-1.6-3-1.6-1.8 0-3 .9-3 2.3 0 3.2 6 1.7 6 4.9 0 1.4-1.3 2.3-3 2.3-1.4 0-2.6-.6-3.1-1.6"/>',
    folder: '<path d="M2 4.5V12a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1H8L6.5 3.5H3A1 1 0 0 0 2 4.5z"/>',
    alert: '<path d="M8 2.2 1.6 13.4h12.8z"/><path d="M8 6.6v3.2M8 11.6v.1"/>',
    trash: '<path d="M2.8 4.5h10.4M6.3 4.5V2.8h3.4v1.7M4.3 4.5l.7 9h6l.7-9"/>',
    key: '<circle cx="5" cy="10.8" r="2.6"/><path d="M6.9 8.9 13.2 2.6M11.2 4.6l1.6 1.6"/>',
    disk: '<ellipse cx="8" cy="4" rx="5.5" ry="2"/><path d="M2.5 4v8c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2V4"/><path d="M2.5 8c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2"/>',
    clock: '<circle cx="8" cy="8" r="6"/><path d="M8 4.6V8l2.4 1.5"/>',
    copy: '<rect x="5.2" y="5.2" width="8.3" height="8.3" rx="1.2"/><path d="M3 10.6V3.2c0-.4.3-.7.7-.7h7.1"/>',
    commit: '<circle cx="8" cy="8" r="2.5"/><path d="M1.5 8h4M10.5 8h4"/>',
    download: '<path d="M8 2v8.2M4.6 7 8 10.4 11.4 7M2.8 13.6h10.4"/>',
    server: '<rect x="2" y="2.4" width="12" height="4.8" rx="1"/><rect x="2" y="8.8" width="12" height="4.8" rx="1"/><path d="M4.6 4.8h.1M4.6 11.2h.1"/>',
    agent: '<path d="M8 1.6 9.5 6.5l4.9 1.5-4.9 1.5L8 14.4 6.5 9.5 1.6 8l4.9-1.5z"/>',
    layers: '<path d="M8 2 1.5 5.5 8 9l6.5-3.5z"/><path d="M1.5 8.5 8 12l6.5-3.5"/>',
    sun: '<circle cx="8" cy="8" r="2.8"/><path d="M8 1.2v1.6M8 13.2v1.6M1.2 8h1.6M13.2 8h1.6M3.2 3.2l1.1 1.1M11.7 11.7l1.1 1.1M3.2 12.8l1.1-1.1M11.7 4.3l1.1-1.1"/>',
    moon: '<path d="M13.2 9.6A5.6 5.6 0 1 1 6.4 2.8a4.5 4.5 0 0 0 6.8 6.8z"/>',
    panel: '<rect x="1.8" y="2.5" width="12.4" height="11" rx="1.2"/><path d="M1.8 10h12.4"/>',
    sidebar: '<rect x="1.8" y="2.5" width="12.4" height="11" rx="1.2"/><path d="M6 2.5v11"/>',
    filter: '<path d="M2 3h12l-4.6 5.5V13l-2.8-1.4V8.5z"/>',
    terminal: '<rect x="1.5" y="2.5" width="13" height="11" rx="1.2"/><path d="M4.5 6.2l2 1.8-2 1.8M8.2 10.2h3.3"/>',
    dots: '<circle cx="3.5" cy="8" r=".8"/><circle cx="8" cy="8" r=".8"/><circle cx="12.5" cy="8" r=".8"/>',
    child: '<rect x="1.8" y="1.8" width="7" height="5" rx="1"/><rect x="7.2" y="9.2" width="7" height="5" rx="1"/><path d="M5 6.8v4.7h2.2"/>',
    lock: '<rect x="3" y="7" width="10" height="7" rx="1.2"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/>',
    save: '<path d="M3 2.5h8l2.5 2.5v8.5H3z"/><path d="M5.5 2.5v3.5h5V2.5M5.5 13.5v-4h5v4"/>',
    link: '<path d="M6.6 9.4a2.8 2.8 0 0 0 4 0l2.2-2.2a2.8 2.8 0 0 0-4-4l-.9.9"/><path d="M9.4 6.6a2.8 2.8 0 0 0-4 0L3.2 8.8a2.8 2.8 0 0 0 4 4l.9-.9"/>',
    chart: '<path d="M2 13.5h12"/><path d="M4 11V8M7 11V4.5M10 11V6.5M13 11V3"/>',
    upload: '<path d="M8 10.5V2.3M4.6 5.6 8 2.2l3.4 3.4M2.8 13.6h10.4"/>',
    shield: '<path d="M8 1.8 2.8 3.8v4c0 3.2 2.3 5.4 5.2 6.4 2.9-1 5.2-3.2 5.2-6.4v-4z"/>',
    code: '<path d="M5.5 4.5 2 8l3.5 3.5M10.5 4.5 14 8l-3.5 3.5"/>',
    command: '<path d="M5.5 5.5h5v5h-5z"/><path d="M5.5 5.5V4a1.5 1.5 0 1 0-1.5 1.5zM10.5 5.5V4A1.5 1.5 0 1 1 12 5.5zM5.5 10.5V12A1.5 1.5 0 1 1 4 10.5zM10.5 10.5V12a1.5 1.5 0 1 0 1.5-1.5z"/>',
    plus: '<path d="M8 3v10M3 8h10"/>',
    wifi: '<path d="M1.8 6a9 9 0 0 1 12.4 0M4 8.4a6 6 0 0 1 8 0M6.2 10.8a3 3 0 0 1 3.6 0"/><circle cx="8" cy="13" r=".6"/>',
    vol: '<path d="M2.5 6h2.5l3.5-3v10L5 10H2.5z"/><path d="M11 5.5a3.5 3.5 0 0 1 0 5M12.8 3.8a6 6 0 0 1 0 8.4"/>',
    battery: '<rect x="1.5" y="4.5" width="11.5" height="7" rx="1.2"/><path d="M14.5 7v2"/><path d="M3.5 6.5h6v3h-6z"/>',
    up: '<path d="M4 10l4-4 4 4"/>',
    eye: '<path d="M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8z"/><circle cx="8" cy="8" r="2"/>',
    history: '<path d="M2.5 8a5.5 5.5 0 1 0 1.6-3.9"/><path d="M2 2.5v2.8h2.8M8 5v3l2 1.4"/>',
    hash: '<path d="M3 6h10.5M2.5 10H13M6.5 2.5l-1 11M10.5 2.5l-1 11"/>',
    sparkline: '<path d="M1.5 11 5 7l3 2.5L14.5 3"/>',
    home: '<path d="M2.5 7.5 8 3l5.5 4.5V13a.5.5 0 0 1-.5.5H3a.5.5 0 0 1-.5-.5z"/>',
    upd: '<path d="M8 13.5V5M4.5 8.2 8 4.7l3.5 3.5M3 2.5h10"/>',
    box: '<path d="M8 1.8 13.8 5v6L8 14.2 2.2 11V5z"/><path d="M2.2 5 8 8.2 13.8 5M8 8.2v6"/>',
  };
  function icon(name, cls) {
    return '<svg class="i' + (cls ? " " + cls : "") + '" viewBox="0 0 16 16" aria-hidden="true">' + (P[name] || P.dots) + "</svg>";
  }

  /* ---------------- sample data (BUILD-BRIEF sample-data.md) ---------------- */
  const D = {
    now: "14:18",
    instances: [
      { id: "agent-vm", backend: "hyperv-local", state: "running", uptime: "3h 12m", vcpu: 8, ram: 16, ramUsed: 11.2, disk: 146, diskPct: 88, os: "Ubuntu 26.04 LTS", provisioned: "c21978b", installed: "b5c4348", behind: 2, host: "agent-vm.mshome.net" },
      { id: "win-test", backend: "child of agent-vm", parent: "agent-vm", state: "running", uptime: "1h 40m", vcpu: 2, ram: 4, os: "Windows 11 eval", lease: "5h 20m" },
      { id: "dev-2", backend: "hyperv-remote", remote: "buildbox.example.local:7462", state: "saved", savedAgo: "41 min", vcpu: 4, ram: 12, os: "Ubuntu 26.04 LTS", idle: "save after 60 min", provisioned: "b5c4348", installed: "b5c4348", behind: 0 },
    ],
    update: { installed: "b5c4348", latest: "3a91f0e", commits: [
      ["3a91f0e", "Default VM CPU allocation to the host allowance"],
      ["8e02d1a", "Initialize vTPM identity before capturing reusable VM baselines"],
      ["51c7f90", "Clarify optional CPU sizing in the child VM design"],
      ["d4b61e2", "Add opt-in Hyper-V license machine reuse"],
    ] },
    agents: [
      { id: "claude", name: "Claude Code", ver: "2.4.1", latest: "2.4.3", state: "working", repo: "construct", since: "6 min", turn: "Refactoring forwarder retry backoff", today: 3.40, tokens: "1.92M", extra: "CLI + VS Code extension" },
      { id: "codex", name: "Codex", ver: "0.61.0", latest: null, state: "idle", repo: "gitgudlab", since: "48 min", turn: "last turn 48 min ago", today: 0.58, tokens: "311k", extra: "app-server :4500" },
      { id: "opencode", name: "OpenCode", ver: "1.9.2", latest: null, state: "serving", repo: "omniloop", since: "", turn: "serve :4096 · idle", today: 0.14, tokens: "88k", extra: "serve :4096" },
      { id: "t3", name: "T3 Code", ver: "0.9.14-construct.3", latest: null, state: "active", repo: "", since: "", turn: "web UI · 2 threads active", today: 0, tokens: "via claude/codex", extra: "https://agent-vm.mshome.net:3773" },
    ],
    forwards: [
      { id: "f5173", vm: "agent-vm", vmPort: 5173, local: "localhost:5173", scope: "client", state: "open", label: "vite dev server", by: "claude", ago: "12 min", url: "http://localhost:5173" },
      { id: "f8080", vm: "agent-vm", vmPort: 8080, local: "localhost:18800", remap: true, scope: "client", state: "open", label: "api preview", by: "codex", ago: "20 min", url: "http://localhost:18800", note: "8080 busy on this PC, remapped to 18800" },
      { id: "f3000", vm: "dev-2", vmPort: 3000, local: "buildbox LAN :3000", scope: "host", state: "queued", label: "storybook", by: "claude", ago: "44 min", url: "http://buildbox.example.local:3000", note: "Opens when dev-2 resumes" },
    ],
    notifications: [
      { id: "n1", t: "14:02", level: "info", msg: "Test suite finished — 3 failures", agent: "claude", repo: "construct", unread: true },
      { id: "n2", t: "13:40", level: "error", msg: "Deploy failed, rolling back", agent: "codex", repo: "gitgudlab", unread: true },
      { id: "n3", t: "12:15", level: "info", msg: "PR #31 opened", agent: "claude", repo: "omniloop", unread: false },
    ],
    mic: { armed: true, device: "Shure MV7", streaming: false },
    projects: [
      { id: "construct", on: true, repos: 2, runtimes: ".NET 10, Node 22", mcp: 2, status: "" },
      { id: "omniloop", on: true, repos: 1, runtimes: "Node 22", mcp: 1, status: "" },
      { id: "gitgudlab", on: true, repos: 3, runtimes: "Go 1.25, Node 22", mcp: 0, status: "" },
      { id: "emili-simulator", on: false, repos: 1, runtimes: "Python 3.13", mcp: 0, status: "" },
      { id: "jarvis", on: true, repos: 1, runtimes: "Python 3.13", mcp: 3, status: "new" },
    ],
    usage: { today: 4.12, month: 86.30, all: 412.77, days: [["Thu", 9.8], ["Fri", 12.4], ["Sat", 6.1], ["Sun", 14.0], ["Mon", 11.3], ["Tue", 8.7], ["Wed", 4.1]],
      split: [["Claude Code", 3.40, "1.92M"], ["Codex", 0.58, "311k"], ["OpenCode", 0.14, "88k"]] },
  };

  /* timeline events per instance (newest first). kind: forward|notify|agent|lifecycle|update */
  D.events = {
    "agent-vm": [
      { id: "e1", t: "14:12", kind: "agent", title: "Claude Code started a turn", detail: "“Refactoring forwarder retry backoff” · construct", entity: ["agent", "claude"], acts: [["Wake me", "bell"]] },
      { id: "e2", t: "14:06", kind: "forward", title: "expose <code>:5173</code> → <code>localhost:5173</code>", detail: "“vite dev server” · by claude", entity: ["forward", "f5173"], acts: [["Open", "ext", "keep"], ["Close", "x"]] },
      { id: "e3", t: "14:02", kind: "notify", level: "info", title: "“Test suite finished — 3 failures”", detail: "notify · claude · construct", entity: ["notify", "n1"], unread: true, acts: [["Ack", "check", "keep"]] },
      { id: "e4", t: "13:58", kind: "forward", title: "expose <code>:8080</code> → <code>:18800</code> remapped", detail: "“api preview” · 8080 busy on this PC", entity: ["forward", "f8080"], acts: [["Open", "ext", "keep"], ["Close", "x"]] },
      { id: "e5", t: "13:40", kind: "notify", level: "error", title: "“Deploy failed, rolling back”", detail: "notify · codex · gitgudlab", entity: ["notify", "n2"], unread: true, acts: [["Ack", "check", "keep"]] },
      { id: "e6", t: "13:37", kind: "lifecycle", vm: "dev-2", title: "dev-2 saved by idle policy", detail: "idle 60 min → save · resume on start", entity: ["instance", "dev-2"], acts: [["Resume", "play"]] },
      { id: "e7", t: "13:30", kind: "agent", title: "Codex finished a turn", detail: "gitgudlab · 14 files changed", entity: ["agent", "codex"], acts: [] },
      { id: "e8", t: "12:38", kind: "lifecycle", vm: "win-test", title: "Child <code>win-test</code> created", detail: "Windows 11 eval · lease 8h", entity: ["instance", "win-test"], acts: [["Console", "terminal"]] },
      { id: "e9", t: "12:15", kind: "notify", level: "info", title: "“PR #31 opened”", detail: "notify · claude · omniloop", entity: ["notify", "n3"], acts: [["Open", "ext"]] },
      { id: "e10", t: "11:20", kind: "update", title: "Construct <code>3a91f0e</code> available", detail: "4 commits ahead of installed b5c4348", entity: ["update", "construct"], acts: [["Update", "upd", "keep"]] },
      { id: "e11", t: "11:06", kind: "lifecycle", title: "agent-vm started", detail: "Hyper-V · UAC approved · boot 38 s", entity: ["instance", "agent-vm"], acts: [] },
      { id: "e12", t: "Yday", kind: "lifecycle", title: "Provisioned <code>@c21978b</code>", detail: "now 2 commits behind installed b5c4348", entity: ["provision", "agent-vm"], acts: [["Reprovision", "cycle", "keep"]] },
    ],
    "dev-2": [
      { id: "d1", t: "13:37", kind: "lifecycle", title: "Saved by idle policy", detail: "no agent heartbeat for 60 min · RAM written to disk", entity: ["instance", "dev-2"], acts: [["Resume", "play", "keep"]] },
      { id: "d2", t: "13:34", kind: "forward", title: "expose <code>:3000</code> queued", detail: "host forward on buildbox LAN · opens when dev-2 resumes", entity: ["forward", "f3000"], acts: [["Cancel", "x"]] },
      { id: "d3", t: "12:37", kind: "agent", title: "Claude Code went idle", detail: "last turn “Add storybook stories” · 12 files", entity: ["agent", "claude"], acts: [] },
      { id: "d4", t: "12:30", kind: "lifecycle", title: "Host job queued: reprovision dev-2", detail: "buildbox · waits for the running create job", entity: ["job", "reprovision-dev-2"], acts: [["Cancel", "x"]] },
      { id: "d5", t: "09:12", kind: "lifecycle", title: "dev-2 resumed", detail: "from saved state · 9 s", entity: ["instance", "dev-2"], acts: [] },
      { id: "d6", t: "Mon", kind: "lifecycle", title: "Provisioned <code>@b5c4348</code>", detail: "up to date with installed Construct", entity: ["provision", "dev-2"], acts: [] },
    ],
    "win-test": [
      { id: "w1", t: "13:10", kind: "lifecycle", title: "Windows activation: evaluation", detail: "90-day eval · no key used", entity: ["instance", "win-test"], acts: [] },
      { id: "w2", t: "12:38", kind: "lifecycle", title: "Child created by agent-vm", detail: "construct vm create win-test --lease 8h", entity: ["instance", "win-test"], acts: [] },
      { id: "w3", t: "12:38", kind: "lifecycle", title: "Lease set: 8 h", detail: "expires 20:38 · 5h 20m left", entity: ["instance", "win-test"], acts: [["Extend", "clock", "keep"]] },
    ],
  };

  /* ---------------- fuzzy matching ---------------- */
  function scoreToken(tok, hay) {
    const i = hay.indexOf(tok);
    if (i >= 0) {
      const wordStart = i === 0 || /[\s:/\-.@>→(]/.test(hay[i - 1]);
      return { s: (wordStart ? 100 : 60) - Math.min(i, 40) * 0.3 + tok.length * 2, at: i };
    }
    // subsequence
    let h = 0, gaps = 0, first = -1;
    for (const c of tok) {
      const j = hay.indexOf(c, h);
      if (j < 0) return null;
      if (first < 0) first = j;
      gaps += j - h;
      h = j + 1;
    }
    if (gaps > tok.length * 4) return null;
    return { s: 25 - gaps, at: -1 };
  }
  function fuzzy(q, cmds) {
    q = q.trim().toLowerCase();
    const toks = q.split(/\s+/).filter(Boolean);
    const out = [];
    for (const c of cmds) {
      const hay = (c.title.replace(/<[^>]+>/g, "") + " " + (c.kw || "") + " " + (c.group || "")).toLowerCase();
      let total = 0, ok = true;
      for (const t of toks) { const r = scoreToken(t, hay); if (!r) { ok = false; break; } total += r.s; }
      if (ok) out.push({ c, s: total + (c.boost || 0) });
    }
    out.sort((a, b) => b.s - a.s);
    return out.map((o) => o.c);
  }
  function highlight(title, q) {
    const toks = q.trim().toLowerCase().split(/\s+/).filter((t) => t.length > 0);
    if (!toks.length || /<[^>]+>/.test(title)) return title;
    const low = title.toLowerCase();
    const marks = new Array(title.length).fill(false);
    toks.forEach((t) => { const i = low.indexOf(t); if (i >= 0) for (let k = i; k < i + t.length; k++) marks[k] = true; });
    let s = "", open = false;
    for (let k = 0; k < title.length; k++) {
      if (marks[k] && !open) { s += '<span class="hl">'; open = true; }
      if (!marks[k] && open) { s += "</span>"; open = false; }
      s += esc(title[k]);
    }
    if (open) s += "</span>";
    return s;
  }
  function esc(s) { return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]); }

  /* ---------------- palette controller ----------------
     opts: { input, list, crumb?, getCommands(q) -> cmds, maxPerGroup, onClose, onState(mode) }
     A command may define: run(ctx) returning undefined | {sub:{title, cmds}} | {confirm:{...}} | {keep:true} */
  function Palette(opts) {
    const self = { q: "", idx: 0, items: [], stack: [], confirm: null };
    const input = opts.input, list = opts.list;

    function currentCmds() {
      if (self.stack.length) return self.stack[self.stack.length - 1].cmds;
      return opts.getCommands(self.q);
    }
    function render() {
      if (self.confirm) return renderConfirm();
      const q = self.q;
      let cmds = currentCmds();
      let items;
      if (!q.trim()) items = cmds.filter((c) => self.stack.length || c.suggest);
      else items = fuzzy(q, cmds);
      // dynamic generators (ports, ram N, cpu N)
      if (!self.stack.length && opts.dynamic) items = opts.dynamic(q).concat(items);
      items = items.slice(0, opts.max || 40);
      self.items = items;
      if (self.idx >= items.length) self.idx = Math.max(0, items.length - 1);
      let html = "";
      if (self.stack.length) {
        html += '<div class="pal-crumb">' + self.stack.map((s) => "<b>" + esc(s.title) + "</b>").join(" " + icon("right", "sm") + " ") + ' <span class="spacer"></span><kbd>Backspace</kbd> back</div>';
      }
      if (!items.length) {
        html += '<div class="pal-empty">No command matches <span class="mono">“' + esc(q) + '”</span>. Try a port number, <span class="mono">rep</span>, <span class="mono">mic</span> or <span class="mono">switch</span>.</div>';
      }
      let lastG = null;
      items.forEach((c, i) => {
        const g = self.stack.length ? null : (q.trim() ? (i === 0 ? "Best match" : c.group) : c.group);
        if (g && g !== lastG) { html += '<div class="pal-group">' + esc(g) + "</div>"; lastG = g; }
        html += '<div class="pal-item' + (i === self.idx ? " active" : "") + (c.danger ? " danger" : "") + '" data-i="' + i + '" role="option" aria-selected="' + (i === self.idx) + '">' +
          '<span class="p-ico">' + icon(c.icon || "command", "sm") + "</span>" +
          '<span style="min-width:0"><div class="p-t">' + highlight(c.title, q) + "</div>" + (c.sub ? '<div class="p-s">' + c.sub + "</div>" : "") + "</span>" +
          '<span class="p-r">' + (c.hint ? '<span>' + c.hint + "</span>" : "") + (c.danger ? "<kbd>type name</kbd>" : c.subMenu ? "<kbd>→</kbd>" : "<kbd>↵</kbd>") + "</span></div>";
      });
      list.innerHTML = '<div class="pal-results">' + html + "</div>";
      const act = list.querySelector(".pal-item.active");
      if (act && act.scrollIntoView) act.scrollIntoView({ block: "nearest" });
      opts.onState && opts.onState("list");
    }
    function renderConfirm() {
      const c = self.confirm;
      const typed = input.value.trim();
      const ok = typed === c.name;
      list.innerHTML = '<div class="confirm">' +
        '<div class="c-head">' + icon(c.icon || "alert", "lg") + esc(c.title) + "</div>" +
        '<div class="c-sub">' + c.consequence + "</div>" +
        '<dl class="impact">' + c.impact.map((r) => "<div><dt>" + esc(r[0]) + "</dt><dd>" + r[1] + "</dd></div>").join("") + "</dl>" +
        '<div class="c-type">Type <code>' + esc(c.name) + "</code> in the bar above to confirm." + (ok ? ' <span class="match-ok">' + icon("check", "sm") + " matches</span>" : "") + "</div>" +
        '<div class="c-actions"><button class="btn ghost" data-act="cancel">Cancel <kbd>Esc</kbd></button><button class="btn danger solid" data-act="go"' + (ok ? "" : " disabled") + ">" + esc(c.verb) + " <kbd>↵</kbd></button></div></div>";
      opts.onState && opts.onState("confirm");
    }
    function run(c, shift) {
      if (!c) return;
      if (c.danger && c.confirm) {
        self.confirm = c.confirm;
        input.value = "";
        input.placeholder = "Type " + c.confirm.name + " to confirm";
        render();
        input.focus();
        return;
      }
      const r = c.run ? c.run({ shift, palette: self }) : undefined;
      if (r && r.sub) {
        self.stack.push(r.sub); input.value = ""; self.q = ""; self.idx = 0;
        input.placeholder = r.sub.placeholder || "Choose…"; render(); return;
      }
      if (r && r.keep) { render(); return; }
      self.close();
    }
    self.close = function () {
      self.stack = []; self.confirm = null; input.value = ""; self.q = ""; self.idx = 0;
      input.placeholder = opts.placeholder || "";
      opts.onClose && opts.onClose();
    };
    self.reset = function () { self.stack = []; self.confirm = null; self.idx = 0; input.placeholder = opts.placeholder || ""; };
    self.set = function (q) { self.reset(); input.value = q; self.q = q; self.idx = 0; render(); };
    self.openConfirm = function (c) { self.reset(); self.confirm = c.confirm; input.value = ""; input.placeholder = "Type " + c.confirm.name + " to confirm"; render(); };
    self.render = render;

    input.addEventListener("input", () => { self.q = input.value; self.idx = 0; render(); });
    input.addEventListener("keydown", (e) => {
      if (self.confirm) {
        if (e.key === "Escape") { e.preventDefault(); self.close(); }
        if (e.key === "Enter" && input.value.trim() === self.confirm.name) { e.preventDefault(); const cf = self.confirm; self.close(); cf.run && cf.run(); }
        return;
      }
      if (e.key === "ArrowDown") { e.preventDefault(); self.idx = Math.min(self.items.length - 1, self.idx + 1); render(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); self.idx = Math.max(0, self.idx - 1); render(); }
      else if (e.key === "Enter") { e.preventDefault(); run(self.items[self.idx], e.shiftKey); }
      else if (e.key === "Escape") { e.preventDefault(); if (self.stack.length) { self.stack.pop(); render(); } else self.close(); }
      else if (e.key === "Backspace" && !input.value && self.stack.length) { self.stack.pop(); input.placeholder = opts.placeholder || ""; render(); }
    });
    input.addEventListener("keyup", () => { if (self.confirm) renderConfirm(); });
    list.addEventListener("mousemove", (e) => {
      const it = e.target.closest(".pal-item"); if (!it) return;
      const i = +it.dataset.i; if (i !== self.idx) { self.idx = i; list.querySelectorAll(".pal-item").forEach((n) => n.classList.toggle("active", +n.dataset.i === i)); }
    });
    list.addEventListener("click", (e) => {
      const it = e.target.closest(".pal-item");
      if (it) { run(self.items[+it.dataset.i], e.shiftKey); return; }
      const a = e.target.closest("[data-act]");
      if (a && a.dataset.act === "cancel") self.close();
      if (a && a.dataset.act === "go" && !a.disabled) { const cf = self.confirm; self.close(); cf.run && cf.run(); }
    });
    return self;
  }

  /* ---------------- toast ---------------- */
  function toast(host, msg, kind, undo) {
    if (!host) return;
    const el = document.createElement("div");
    el.className = "toast " + (kind || "");
    const ico = { ok: "check", warn: "alert", err: "alert" }[kind] || "command";
    el.innerHTML = '<span class="t-ico">' + icon(ico) + "</span><span>" + msg + "</span>" + (undo ? '<button class="undo">Undo</button>' : "");
    host.appendChild(el);
    if (undo) el.querySelector(".undo").onclick = () => { undo(); el.remove(); };
    setTimeout(() => { el.style.transition = "opacity .25s"; el.style.opacity = "0"; setTimeout(() => el.remove(), 260); }, 3200);
  }

  /* ---------------- dropdown ---------------- */
  let openDD = null;
  function dropdown(anchor, html, onPick, align) {
    closeDD();
    const host = anchor.closest(".win, .desk, .popup") || document.body;
    const dd = document.createElement("div");
    dd.className = "dd";
    dd.innerHTML = html;
    host.appendChild(dd);
    const hr = host.getBoundingClientRect(), ar = anchor.getBoundingClientRect();
    const scale = hr.width / host.offsetWidth || 1;
    let left = (ar.left - hr.left) / scale, top = (ar.bottom - hr.top) / scale + 4;
    if (align === "right") left = (ar.right - hr.left) / scale - dd.offsetWidth;
    if (align === "up") top = (ar.top - hr.top) / scale - dd.offsetHeight - 4;
    dd.style.left = Math.max(6, left) + "px"; dd.style.top = top + "px";
    dd.addEventListener("click", (e) => { const i = e.target.closest("[data-v]"); if (i) { onPick && onPick(i.dataset.v); closeDD(); } });
    openDD = dd;
    setTimeout(() => document.addEventListener("mousedown", outside), 0);
    function outside(e) { if (!dd.contains(e.target)) { closeDD(); } }
    dd._outside = outside;
    return dd;
  }
  function closeDD() { if (openDD) { document.removeEventListener("mousedown", openDD._outside); openDD.remove(); openDD = null; } }

  window.OL = { icon, D, fuzzy, highlight, esc, Palette, toast, dropdown, closeDD };
})();
