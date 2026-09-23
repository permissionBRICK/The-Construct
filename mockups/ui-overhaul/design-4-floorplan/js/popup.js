/* Tray popup: pocket floorplan */
(function () {
  window.GLYPH = '<svg class="glyph" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="4"/><path d="M11 3v8H3M11 11h4v10M15 15h6"/></svg>';

  window.ringSvg = function (used, total, opts) {
    opts = opts || {};
    const r = opts.r || 19, sw = opts.sw || 5, c = 2 * Math.PI * r, size = (r + sw / 2 + 1) * 2;
    const f = total ? Math.min(1, used / total) : 0;
    const col = opts.color || (f > .9 ? 'var(--coral)' : f > .8 ? 'var(--amber)' : 'var(--teal)');
    return '<svg viewBox="0 0 ' + size + ' ' + size + '" aria-hidden="true"><circle cx="' + size / 2 + '" cy="' + size / 2 + '" r="' + r + '" stroke="' + (opts.track || 'var(--teal-soft)') + '" stroke-width="' + sw + '" fill="none"/>' +
      '<circle cx="' + size / 2 + '" cy="' + size / 2 + '" r="' + r + '" stroke="' + col + '" stroke-width="' + sw + '" fill="none" stroke-linecap="round" stroke-dasharray="' + (c * f) + ' ' + c + '"/></svg>';
  };

  const S = { vm: 'running', attn: true, open: null, mic: true, micLive: false, target: 'VS Code', read: false };
  const root = $('#s-popup');

  function shell() {
    root.innerHTML =
      '<div class="desk">' +
      '<svg class="hills" viewBox="0 0 1280 380" preserveAspectRatio="none"><path d="M0 250 C200 160 380 230 560 170 S900 90 1280 190 V380 H0z"/><path d="M0 300 C220 230 420 300 640 250 S1000 200 1280 260 V380 H0z"/><path d="M0 340 C260 300 520 350 760 320 S1100 300 1280 330 V380 H0z"/></svg>' +
      '<div class="desk-icons"><div><i></i>Recycle Bin</div><div><i></i>VS Code</div><div><i></i>T3 Code</div></div>' +
      '<div class="scenario" id="scn"><h4>Scenario</h4><p>Switch states to see the popup react. The popup is live-sized; nothing here is real.</p>' +
      '<div class="srow">agent-vm<div class="seg sm" id="scVm"><button data-v="running">Running</button><button data-v="off">Stopped</button></div></div>' +
      '<div class="srow">Attention item<div class="seg sm" id="scAt"><button data-v="1">Open</button><button data-v="0">Resolved</button></div></div>' +
      '<div class="srow">Microphone<div class="seg sm" id="scMic"><button data-v="armed">Armed</button><button data-v="live">Live</button><button data-v="off">Off</button></div></div></div>' +
      '<div class="caption" style="right:600px;bottom:452px">Tray icon · right-click menu</div>' +
      rightMenu() +
            '<div class="pp" id="pp"></div>' +
      '<div class="taskbar"><div class="tb-apps">' +
      '<span>' + ic('windows', 'l') + '</span><span>' + ic('search', 'l') + '</span>' +
      '<span><b style="background:#e8b64c"></b></span><span><b style="background:#2f7fd1"></b></span><span><b style="background:#3a9b6f"></b></span><span><b style="background:#7a5bc9"></b></span></div>' +
      '<div class="tray"><span>' + ic('up', 's') + '</span><span class="ct" title="The Construct">' + GLYPH + '<i class="bdg"></i></span><span>' + ic('wifi', 's') + '</span><span>' + ic('vol', 's') + '</span><span class="clock">14:18<br>23.09.2026</span><span>' + ic('bell', 's') + '</span></div></div>' +
      '<div class="menu inbox" id="ppInbox"></div><div class="menu" id="ppTarget"></div>' +
      '<div class="toasts"></div></div>';
    $('#scVm').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.vm = b.dataset.v; render(); } });
    $('#scAt').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.attn = b.dataset.v === '1'; render(); } });
    $('#scMic').addEventListener('click', e => { const b = e.target.closest('button'); if (b) { S.mic = b.dataset.v !== 'off'; S.micLive = b.dataset.v === 'live'; render(); } });
  }

  function rightMenu() {
    const f = D.forwards;
    return '<div class="rm" role="menu" aria-label="Tray menu">' +
      '<div class="hd">Construct · agent-vm</div>' +
      '<div class="it"><span class="ck"></span><b style="font-weight:600">Open VS Code</b></div>' +
      '<div class="it" data-go="panel"><span class="ck"></span>Control Panel</div>' +
      '<div class="it" data-go="host"><span class="ck"></span>Host Administration</div>' +
      '<div class="sp"></div>' +
      '<div class="it"><span class="ck"></span>Instance<span class="arr">' + ic('chevr', 's') + '</span><div class="sub">' +
      '<div class="it"><span class="ck">' + ic('check', 's') + '</span>agent-vm<span class="k"><span class="led on"></span> running</span></div>' +
      '<div class="it"><span class="ck"></span>dev-2<span class="k">saved · buildbox</span></div>' +
      '<div class="sp"></div><div class="it"><span class="ck"></span>Register a VM…</div><div class="it"><span class="ck"></span>Create a remote VM…</div></div></div>' +
      '<div class="it force"><span class="ck"></span>Forwards<span class="k">2 open</span><span class="arr" style="margin-left:6px">' + ic('chevr', 's') + '</span><div class="sub">' +
      '<div class="it"><span class="ck">' + ic('ext', 's') + '</span>localhost:5173<span class="k">vite dev server</span></div>' +
      '<div class="it"><span class="ck">' + ic('ext', 's') + '</span>localhost:18800<span class="k">api preview · vm:8080</span></div>' +
      '<div class="it dim"><span class="ck"></span>buildbox:3000<span class="k">queued · dev-2</span></div>' +
      '<div class="sp"></div><div class="it"><span class="ck"></span>Close all forwards</div></div></div>' +
      '<div class="it"><span class="ck">' + ic('check', 's') + '</span>Microphone passthrough<span class="k">Shure MV7</span></div>' +
      '<div class="it"><span class="ck"></span>Notifications<span class="k">3</span></div>' +
      '<div class="sp"></div>' +
      '<div class="it"><span class="ck"></span>Shutdown agent-vm</div>' +
      '<div class="it"><span class="ck"></span>Update to 3a91f0e<span class="k">4 commits</span></div>' +
      '<div class="it"><span class="ck"></span>Settings<span class="arr">' + ic('chevr', 's') + '</span><div class="sub">' +
      '<div class="it"><span class="ck">' + ic('check', 's') + '</span>Start with Windows</div><div class="it"><span class="ck"></span>Logs</div><div class="it"><span class="ck"></span>About</div></div></div>' +
      '<div class="sp"></div><div class="it"><span class="ck"></span>Quit</div></div>';
  }

  function agentAvs(sleep) {
    return D.agents.map(a => {
      const cls = sleep ? 'sleep' : a.state === 'working' ? 'working' : '';
      return '<span class="av ' + cls + '" title="' + a.name + ' · ' + (sleep ? 'VM not running' : a.state === 'working' ? 'working' : a.state === 'web' ? 'web UI' : 'idle') + '">' + a.mark + (a.update && !sleep ? '<i class="upd" title="Update available"></i>' : '') + '</span>';
    }).join('');
  }

  function tileAgent() {
    const v = D.instances['agent-vm'], on = S.vm === 'running', open = S.open === 'agent-vm';
    const fw = D.forwards.filter(f => f.inst === 'agent-vm');
    return '<div class="tile ' + (on ? '' : 'off') + (open ? ' open' : '') + '" data-tile="agent-vm" tabindex="0" aria-expanded="' + open + '">' +
      '<span class="port ' + (on ? '' : 'off') + '" data-wire="f5173" style="top:24px"></span>' +
      '<span class="port ' + (on ? '' : 'off') + '" data-wire="f8080" style="top:56px"></span>' +
      '<span class="port mic" data-wire="mic" style="top:88px"></span>' +
      '<div class="top"><div class="ring">' + ringSvg(on ? v.ramUsed : 0, v.ram, { color: on ? null : 'var(--stone)' }) + '<div class="rv">' + (on ? v.ramUsed : '—') + '<small>/' + v.ram + ' GB</small></div></div>' +
      '<div style="min-width:0"><div class="nm">agent-vm</div>' +
      '<div class="st">' + (on ? '<span class="led on"></span>Running · ' + v.uptime : '<span class="led"></span>Stopped · 2 min ago') + '</div>' +
      '<div class="where">local Hyper-V · 8 vCPU · ' + (on ? v.ramUsed + ' of ' : '') + '16 GB RAM</div></div></div>' +
      '<div class="avs">' + agentAvs(!on) + '<span class="act">' + (on ? '<b style="font-weight:600;color:var(--teal-ink)">Claude</b> · refactoring forwarder…' : 'agents stopped') + '</span></div>' +
      '<div class="badges"><span class="pill amber" title="Provisioned at c21978b; installed Construct is b5c4348">' + ic('refresh', 's') + '2 behind host</span><span class="pill amber">' + ic('disk', 's') + 'disk 88%</span></div>' +
      '<div class="prim">' + (on
        ? '<button class="btn primary" data-act="open">' + ic('code', 's') + 'Open in ' + S.target + '</button><button class="btn primary caret" data-menu data-act="target" aria-label="Choose what Open does">' + ic('chev', 's') + '</button>'
        : '<button class="btn primary" data-act="start">' + ic('play', 's') + 'Start &amp; connect</button>') + '</div>' +
      (on ? '' : '<div class="uac">' + ic('info', 's') + 'Starting a local VM needs one Windows UAC prompt.</div>') +
      '<div class="more">' + (on ? moreAgentVm(fw) : '<div class="fr muted">Forwards and agents come back when the VM starts. The mic link stays armed.</div><div class="links"><button class="btn sm" data-act="panel">Control panel</button></div>') + '</div>' +
      '</div>';
  }

  function moreAgentVm(fw) {
    const ag = D.agents.map(a => '<div class="ar"><span class="av ' + (a.state === 'working' ? 'working' : '') + '">' + a.mark + '</span><div><b>' + a.name + '</b> <span class="muted" style="font-size:11px">' + a.version + '</span>' +
      '<span class="d">' + (a.state === 'working' ? 'Working · ' + a.repo + ' · ' + a.since + ' — ' + a.task : a.state === 'idle' ? (a.last ? 'Idle · last turn ' + a.last : 'Idle · ' + a.task) : a.task) + '</span></div>' +
      (a.state === 'working' ? '<button class="bellbtn" data-act="wake" aria-pressed="false" title="Notify me when Claude goes idle">' + ic('bell', 's') + '</button>' : a.update ? '<span class="pill amber" style="font-size:10px">' + a.update + '</span>' : '<span></span>') + '</div>').join('');
    const fr = fw.map(f => '<div class="fr"><span class="led on"></span><span class="mono">localhost:' + f.local + '</span><span class="muted trunc grow">' + f.label + '</span><button class="btn sm" data-fwd="' + f.id + '">' + ic('ext', 's') + 'Open</button></div>').join('');
    return '<h5>Agents</h5>' + ag + '<h5>Forwards</h5>' + fr +
      '<div class="tip-box"><b>2 commits behind host.</b> Provisioned at c21978b, installed b5c4348. Claude is mid-turn, so reprovision can wait until it finishes.<div style="margin-top:6px;display:flex;gap:6px"><button class="btn sm" data-act="reprov-later">' + ic('clock', 's') + 'When Claude finishes</button><button class="btn sm ghost" data-act="why">What changes?</button></div></div>' +
      '<div class="links"><button class="btn sm ghost" data-act="t3">' + ic('ext', 's') + 'T3 Code</button><button class="btn sm ghost" data-act="console">' + ic('term', 's') + 'Console</button><button class="btn sm ghost" data-act="panel">' + ic('map', 's') + 'Control panel</button><button class="btn sm ghost" data-act="shutdown">' + ic('power', 's') + 'Shut down</button></div>';
  }

  function tileChild() {
    const c = D.child, open = S.open === 'win-test';
    return '<div class="tile child' + (open ? ' open' : '') + '" data-tile="win-test" tabindex="0" aria-expanded="' + open + '">' +
      '<div class="top" style="grid-template-columns:34px 1fr"><div class="ring" style="width:34px;height:34px"><div style="width:34px;height:34px">' + ringSvg(2.6, 4, { r: 13, sw: 4, color: 'var(--plum)', track: 'var(--plum-soft)' }).replace('<svg', '<svg style="width:34px;height:34px;transform:rotate(-90deg)"') + '</div></div>' +
      '<div style="min-width:0"><div class="nm" style="font-size:14px">win-test <span class="pill plum" style="font-family:var(--sans);font-size:10px">child</span></div>' +
      '<div class="st"><span class="led on"></span>Windows 11 eval · 4 GB · lease 5h 20m</div></div></div>' +
      '<div class="prim" style="margin-top:8px"><button class="btn sm" data-act="console" style="flex:1;border-radius:9px">' + ic('term', 's') + 'Console</button></div>' +
      '<div class="more"><div class="fr"><span class="grow">Lease</span><span class="muted">5h 20m left of 8h</span></div><div class="meter" style="margin:4px 0 8px;background:var(--plum-soft)"><i style="width:67%;background:var(--plum)"></i></div>' +
      '<div class="links"><button class="btn sm ghost" data-act="extend">' + ic('clock', 's') + 'Extend 4h</button><button class="btn sm ghost" data-act="panel">' + ic('map', 's') + 'Manage in panel</button></div></div></div>';
  }

  function tileDev2() {
    const v = D.instances['dev-2'], open = S.open === 'dev-2';
    return '<div class="tile saved' + (open ? ' open' : '') + '" data-tile="dev-2" tabindex="0" aria-expanded="' + open + '">' +
      '<span class="port off" data-wire="dev2" style="top:24px"></span>' +
      '<div class="top"><div class="ring">' + ringSvg(0, v.ram, { color: 'var(--stone)', track: 'var(--stone-soft)' }) + '<div class="rv">' + ic('zzz', 's') + '<small>12 GB</small></div></div>' +
      '<div style="min-width:0"><div class="nm">dev-2</div>' +
      '<div class="st"><span class="led"></span>Saved by idle policy · 41 min</div>' +
      '<div class="where">on buildbox · 4 vCPU · 12 GB · up to date</div></div></div>' +
      '<div class="badges"><span class="pill outline" title="Host forward on the buildbox LAN, opens when dev-2 resumes">buildbox:3000 queued</span></div>' +
      '<div class="prim"><button class="btn primary" data-act="resume">' + ic('play', 's') + 'Resume</button></div>' +
      '<div class="more"><div class="fr"><span class="grow">Idle policy</span><span class="muted">save after 60 min idle</span></div>' +
      '<div class="fr"><span class="grow">Resume time</span><span class="muted">about 20 s, state is kept</span></div>' +
      '<div class="links"><button class="btn sm ghost" data-act="panel-dev2">' + ic('map', 's') + 'Control panel</button><button class="btn sm ghost" data-act="shutdown">' + ic('power', 's') + 'Shut down instead</button></div></div></div>';
  }

  function render() {
    const pp = $('#pp');
    const unread = S.read ? 0 : 3;
    pp.innerHTML =
      '<div class="pp-h"><div class="pp-mark">' + GLYPH + '</div><div class="grow"><div class="pp-title">Construct</div><div class="pp-sub">PC-1 · 2 VMs + 1 child · updated 5 s ago</div></div>' +
      '<button class="tb-icon" data-menu data-act="inbox" title="Notifications">' + ic('bell') + (unread ? '<span class="dot">' + unread + '</span>' : '') + '</button>' +
      '<button class="tb-icon" data-act="panel" title="Open control panel">' + ic('map') + '</button></div>' +
      (S.attn ? '<div class="pp-attn" role="alert"><span>' + ic('warn') + '</span><div><b>Deploy failed, rolling back</b><span>Codex · gitgudlab · 13:40</span></div><div class="row" style="gap:2px"><button class="btn sm" data-act="show-n">Show</button><button class="x" data-act="dismiss" aria-label="Dismiss">' + ic('x', 's') + '</button></div></div>' : '') +
      '<div class="topo"><div class="topo-in" id="topoIn"><svg class="wires" id="wires"></svg>' +
      '<div class="rail" title="PC-1 · Windows 11 · 64 GB">' + ic('pc') + '<div class="vt">PC-1 <span>· you</span></div></div>' +
      '<div class="tiles">' + tileAgent() + tileChild() + tileDev2() + '</div></div></div>' +
      '<div class="pp-f"><div class="spend"><div class="eyebrow">Spent today</div><div class="big">$4.12</div><div class="sub">$86.30 this month</div></div>' +
      '<div class="spark" title="Last 7 days of token spend">' + D.usage.week.map((d, i) => '<i class="' + (i === 6 ? 'today' : '') + '" style="height:' + Math.round(d[1] / 14 * 24 + 1) + 'px" title="' + d[0] + ' $' + d[1].toFixed(2) + '"><span>' + d[0][0] + '</span></i>').join('') + '</div>' +
      '<div class="right"><button class="pill teal" style="border:0;cursor:pointer" data-act="update" title="Installed b5c4348, latest 3a91f0e">' + ic('download', 's') + 'Update · 4 commits</button><span class="muted" style="font-size:11px">claude $3.40 · codex $0.58</span></div></div>';
    // scenario buttons
    $$('#scVm button').forEach(b => b.setAttribute('aria-selected', b.dataset.v === S.vm));
    $$('#scAt button').forEach(b => b.setAttribute('aria-selected', (b.dataset.v === '1') === S.attn));
    $$('#scMic button').forEach(b => b.setAttribute('aria-selected', b.dataset.v === (S.mic ? (S.micLive ? 'live' : 'armed') : 'off')));
    requestAnimationFrame(wires);
  }

  function wires() {
    const inn = $('#topoIn'); if (!inn || !inn.offsetParent) return;
    const svg = $('#wires'), rail = $('.rail', inn);
    const W = inn.scrollWidth, H = inn.scrollHeight;
    svg.setAttribute('width', W); svg.setAttribute('height', H);
    const x1 = rail.offsetLeft + rail.offsetWidth;
    $$('.wl', inn).forEach(n => n.remove());
    let paths = '';
    const on = S.vm === 'running';
    $$('.port', inn).forEach(p => {
      const r = rel(p, inn), y = r.y + r.h / 2, x2 = r.x + 1, id = p.dataset.wire;
      let cls = 'w-fwd', label = '', lcls = '', title = '', act = '';
      if (id === 'f5173' || id === 'f8080') {
        const f = D.forwards.find(q => q.id === id);
        cls = on ? 'w-fwd' : 'w-off';
        label = f.local === f.vm ? ':' + f.vm : f.vm + '→' + f.local;
        lcls = on ? '' : 'dim';
        title = 'vm:' + f.vm + ' → localhost:' + f.local + ' · ' + f.label + (on ? ' · click to open' : ' · closed while stopped');
        act = 'fwd:' + id;
        label += on ? ' ' + ic('ext', 's') : '';
      } else if (id === 'mic') {
        cls = S.mic ? 'w-mic' : 'w-off';
        lcls = 'mic' + (S.mic ? '' : ' offm');
        label = ic('mic', 's') + (S.mic ? (S.micLive ? 'live' : 'armed') : 'off');
        title = 'Microphone passthrough · Shure MV7 · click to ' + (S.mic ? 'turn off' : 'arm');
        act = 'mic';
      } else if (id === 'dev2') {
        cls = 'w-remote'; lcls = 'remote'; label = ic('cloud', 's') + 'buildbox'; title = 'dev-2 runs on buildbox.example.local:7462'; act = 'host';
      }
      paths += '<path class="' + cls + '" d="M' + x1 + ' ' + y + ' H' + x2 + '"/>';
      if (cls === 'w-fwd') paths += '<path class="flow" d="M' + x1 + ' ' + y + ' H' + x2 + '"/>';
      if (id === 'mic' && S.micLive) paths += '<path class="flow" style="stroke:var(--coral)" d="M' + x2 + ' ' + y + ' H' + x1 + '"/>';
      paths += '<circle cx="' + x1 + '" cy="' + y + '" r="3.5" style="stroke:' + (cls === 'w-fwd' ? 'var(--teal)' : cls === 'w-mic' ? 'var(--amber)' : 'var(--stone)') + '"/>';
      const b = document.createElement('button');
      b.className = 'wl ' + lcls; b.innerHTML = label; b.title = title; b.dataset.act = act;
      if (S.micLive && id === 'mic') { b.style.borderColor = 'var(--coral)'; b.style.color = 'var(--coral-ink)'; }
      b.style.left = ((x1 + x2) / 2) + 'px'; b.style.top = y + 'px';
      inn.appendChild(b);
    });
    // child connector
    const pt = $('[data-tile="agent-vm"]', inn), ch = $('[data-tile="win-test"]', inn);
    if (pt && ch) {
      const a = rel(pt, inn), c = rel(ch, inn);
      const x = c.x - 11, ya = a.y + a.h, yc = c.y + 24;
      paths += '<path class="w-child" d="M' + x + ' ' + ya + ' V' + (yc - 8) + ' Q' + x + ' ' + yc + ' ' + (x + 8) + ' ' + yc + ' H' + c.x + '"/>';
      paths += '<circle cx="' + x + '" cy="' + ya + '" r="3" style="stroke:var(--plum)"/>';
    }
    svg.innerHTML = paths;
  }

  function inboxMenu(anchor) {
    const m = $('#ppInbox');
    m.innerHTML = '<div class="mh row between"><span class="eyebrow">Notifications</span><button class="link" data-act="markread" style="font-size:12px">Mark all read</button></div>' +
      D.inbox.map(n => '<div class="ni ' + (n.level === 'error' ? 'err' : 'inf') + (S.read ? ' read' : '') + '"><span class="ico">' + ic(n.level === 'error' ? 'warn' : 'info', 's') + '</span><div><b>' + n.text + '</b><span>' + n.agent + ' · ' + n.repo + '</span></div><span class="t">' + n.t + '</span></div>').join('') +
      '<div class="sep"></div><button class="mi" data-act="panel">' + ic('map', 's') + 'Full history in the control panel</button>';
    const r = rel(anchor, $('.desk'));
    m.classList.add('open');
    m.style.left = (r.x + r.w - m.offsetWidth) + 'px';
    m.style.top = (r.y + r.h + 6) + 'px';
  }
  function targetMenu(anchor) {
    const m = $('#ppTarget');
    m.innerHTML = '<div class="mh eyebrow">Open agent-vm in</div>' + ['VS Code', 'T3 Code', 'serve-web', 'Console'].map(t =>
      '<button class="mi" data-target="' + t + '">' + (t === S.target ? ic('check', 's') : '<span style="width:13px"></span>') + t + '<span class="sub">' + ({ 'VS Code': 'Remote-SSH', 'T3 Code': ':5177', 'serve-web': ':8000', 'Console': 'Hyper-V' })[t] + '</span></button>').join('') +
      '<div class="sep"></div><div class="mh muted" style="font-size:11.5px">The choice is remembered for this VM.</div>';
    const r = rel(anchor, $('.desk'));
    m.classList.add('open');
    m.style.left = (r.x + r.w - m.offsetWidth) + 'px';
    m.style.top = (r.y + r.h + 6) + 'px';
  }

  function onClick(e) {
    const go = e.target.closest('[data-go]');
    if (go) { location.hash = go.dataset.go; return; }
    const t = e.target.closest('[data-act],[data-fwd],[data-target],[data-tile]');
    if (!t) return;
    if (t.dataset.target) { S.target = t.dataset.target; closeMenus(); render(); return; }
    if (t.dataset.fwd) { e.stopPropagation(); const f = D.forwards.find(q => q.id === t.dataset.fwd); toast('Opening <b>http://localhost:' + f.local + '</b> · ' + f.label, root); return; }
    const a = t.dataset.act;
    if (!a && t.dataset.tile) {
      if (e.target.closest('.more') || e.target.closest('button')) return;
      S.open = S.open === t.dataset.tile ? null : t.dataset.tile; render(); return;
    }
    e.stopPropagation();
    if (a === 'inbox') { closeMenus(); inboxMenu(t); return; }
    if (a === 'target') { closeMenus(); targetMenu(t); return; }
    if (a === 'markread') { S.read = true; closeMenus(); render(); return; }
    if (a === 'dismiss') { S.attn = false; render(); toast('Moved to notification history', root); return; }
    if (a === 'show-n') { S.open = 'agent-vm'; render(); toast('Codex · gitgudlab: "Deploy failed, rolling back" at 13:40', root); return; }
    if (a && a.startsWith('fwd:')) { const f = D.forwards.find(q => q.id === a.slice(4)); toast(S.vm === 'running' ? 'Opening <b>http://localhost:' + f.local + '</b> · ' + f.label : 'Start agent-vm to reopen this forward', root); return; }
    if (a === 'mic') { S.mic = !S.mic; S.micLive = false; render(); toast(S.mic ? 'Mic passthrough armed · Shure MV7' : 'Mic passthrough off', root); return; }
    if (a === 'panel') { location.hash = 'panel'; return; }
    if (a === 'panel-dev2') { location.hash = 'panel/dev-2'; return; }
    if (a === 'update') { location.hash = 'panel/update'; return; }
    if (a === 'why') { location.hash = 'panel/lifecycle'; return; }
    if (a === 'host') { toast('dev-2 runs on buildbox.example.local:7462 · constructd 1.14.2', root); return; }
    if (a === 'open') { toast('Opening agent-vm in ' + S.target + '…', root); return; }
    if (a === 'start') { S.vm = 'running'; render(); toast('Approve the Windows prompt to start agent-vm', root); return; }
    if (a === 'resume') { toast('Resuming dev-2 on buildbox · about 20 s', root); return; }
    if (a === 'wake') { const on = t.getAttribute('aria-pressed') !== 'true'; t.setAttribute('aria-pressed', on); toast(on ? 'You will get a toast when Claude goes idle' : 'Wake-up cancelled', root); return; }
    if (a === 'reprov-later') { toast(ic('clock', 's') + ' Reprovision queued: runs when Claude finishes its turn', root); return; }
    if (a === 'console') { toast('Opening the Hyper-V console…', root); return; }
    if (a === 't3') { toast('Opening T3 Code at https://agent-vm:5177', root); return; }
    if (a === 'extend') { toast('Lease for win-test extended to 9h 20m', root); return; }
    if (a === 'shutdown') { toast('Shut down from the control panel (VM power menu)', root); return; }
  }

  shell();
  root.addEventListener('click', onClick);
  root.addEventListener('keydown', e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.dataset && e.target.dataset.tile) { e.preventDefault(); S.open = S.open === e.target.dataset.tile ? null : e.target.dataset.tile; render(); } });
  document.addEventListener('route', e => {
    if (e.detail.surface !== 'popup') return;
    const sub = e.detail.sub[0];
    if (sub === 'stopped') S.vm = 'off';
    if (sub === 'resolved') S.attn = false;
    if (sub === 'expanded') S.open = 'agent-vm';
    render();
  });
  window.addEventListener('resize', () => requestAnimationFrame(wires));
  document.addEventListener('themechange', () => requestAnimationFrame(wires));
})();
