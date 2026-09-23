/* Tray popup + tray right-click menu. */
'use strict';

const POP = {
  vm: 'agent-vm',
  bells: { claude: true, codex: false, opencode: false, t3: false },
  mic: true,
  openTarget: 'VS Code',
  claudeDone: false,
  notifies: NOTIFIES.map((n) => ({ ...n })),
};

function popupScenario() {
  return [
    { id: 'running', label: 'agent-vm running', active: POP.vm === 'agent-vm' && !POP.claudeDone, run: () => { POP.vm = 'agent-vm'; POP.claudeDone = false; } },
    { id: 'saved', label: 'dev-2 saved by idle policy', active: POP.vm === 'dev-2', run: () => { POP.vm = 'dev-2'; } },
    { id: 'done', label: 'Claude finishes (bell armed)', active: POP.claudeDone, run: () => { POP.vm = 'agent-vm'; POP.claudeDone = true; POP.bells.claude = true; setTimeout(fireWake, 350); } },
    { id: 'read', label: 'Inbox: mark all read', active: POP.notifies.every((n) => n.read), run: () => { POP.notifies.forEach((n) => { n.read = true; }); } },
  ];
}

function fireWake() {
  const desk = $('.desktop');
  if (!desk) return;
  toast(desk, { title: 'Claude Code finished · agent-vm', msg: 'Idle after 7 min on construct: "Refactoring forwarder retry backoff". You asked to be woken.', icon: 'bellOn', cls: 'live', ms: 9000 });
}

function renderPopupSurface(stage) {
  stage.innerHTML = `
  <div class="desktop" id="desktop">
    <div class="desk-icons"><div><i></i>Recycle Bin</div><div><i></i>Construct</div><div><i></i>Projects</div></div>
    <div class="desk-bgwin"><div class="bar">construct — Visual Studio Code [SSH: agent-vm]</div>
<pre><span style="color:#c586c0">export</span> <span style="color:#569cd6">function</span> <span style="color:#dcdcaa">retryDelay</span>(attempt: <span style="color:#4ec9b0">number</span>) {
  <span style="color:#6a9955">// exponential backoff with full jitter, capped at 30 s</span>
  <span style="color:#569cd6">const</span> base = Math.<span style="color:#dcdcaa">min</span>(<span style="color:#b5cea8">30_000</span>, <span style="color:#b5cea8">250</span> * <span style="color:#b5cea8">2</span> ** attempt);
  <span style="color:#c586c0">return</span> Math.<span style="color:#dcdcaa">random</span>() * base;
}</pre></div>
    <div class="popup-label">Left-click: Companion popup</div>
    <div class="ctx-wrap"><div class="ctx-label">Right-click: tray menu</div><div id="ctxMenu"></div></div>
    <section class="popup" id="popup" aria-label="Construct Companion popup"></section>
    <div class="taskbar">
      <div class="tb-app"><i></i></div><div class="tb-app"><i style="background:#555;border-radius:50%"></i></div><div class="tb-app"><i></i></div><div class="tb-app"><i></i></div>
      <div class="tray">
        <div class="ti" style="font-size:10px">⌃</div>
        <div class="ti active" data-tip="<b>The Construct</b><br>agent-vm running · Claude working 6 min<br>2 unread notices">
          <div class="tray-construct">${GLYPH.replace('class="glyph"', 'width="18" height="18"')}<span class="badge ${POP.vm === 'dev-2' ? 'saved' : ''}"></span></div>
        </div>
        <div class="ti">${ic('mic')}</div>
        <div class="ti" style="font-size:11px">ENG</div>
        <div class="clock">14:18<br>23.09.2026</div>
      </div>
    </div>
  </div>`;
  renderCtxMenu();
  renderPopup();
}

function renderCtxMenu() {
  const el = $('#ctxMenu');
  const saved = POP.vm === 'dev-2';
  el.innerHTML = `<div class="ctx-menu" role="menu" aria-label="Tray menu">
    <div class="cap">The Construct · ${POP.vm}</div>
    <div class="ci"><span class="ck"></span>Open popup<span class="k">Click</span></div>
    <div class="ci"><span class="ck"></span>Open Control panel<span class="k">Ctrl+Shift+C</span></div>
    <div class="sepl"></div>
    <div class="ci"><span class="ck"></span>Switch instance<span class="k">▸</span>
      <div class="sub">
        ${INSTANCES.map((i) => `<div class="ci"><span class="ck">${i.id === POP.vm ? '✓' : ''}</span><span class="dot ${i.state === 'saved' ? 'saved' : 'running'}"></span>${i.id}<span class="k">${i.state === 'saved' ? 'saved' : i.child ? 'child' : 'running'}</span></div>`).join('')}
      </div>
    </div>
    <div class="ci ${saved ? 'dim' : ''}"><span class="ck"></span>Forwards (2 open)<span class="k">▸</span>
      <div class="sub">
        ${FORWARDS.map((f) => `<div class="ci ${f.state !== 'open' ? 'dim' : ''}"><span class="ck"></span>:${f.vm}${f.local && f.local !== f.vm ? ' → ' + f.local : ''} · ${f.label}<span class="k">${f.state === 'open' ? 'open ↗' : 'queued'}</span></div>`).join('')}
      </div>
    </div>
    <div class="ci ${saved ? 'dim' : ''}"><span class="ck">${POP.mic ? '✓' : ''}</span>Microphone passthrough<span class="k">${POP.mic ? 'armed' : 'off'}</span></div>
    <div class="ci ${saved ? 'dim' : ''}"><span class="ck">${POP.bells.claude ? '✓' : ''}</span>Wake me when Claude finishes</div>
    <div class="sepl"></div>
    ${saved
      ? '<div class="ci"><span class="ck"></span>Resume dev-2</div>'
      : '<div class="ci"><span class="ck"></span>Reprovision agent-vm…<span class="k">2 behind</span></div>'}
    <div class="ci"><span class="ck"></span>Update Construct (3a91f0e)</div>
    <div class="ci"><span class="ck"></span>Host administration · buildbox</div>
    <div class="sepl"></div>
    <div class="ci"><span class="ck"></span>Quit Companion<span class="k">VMs keep running</span></div>
  </div>`;
}

function unreadCount() { return POP.notifies.filter((n) => !n.read).length; }

function renderPopup() {
  const el = $('#popup');
  if (!el) return;
  const inst = INSTANCES.find((i) => i.id === POP.vm);
  const saved = inst.state === 'saved';
  el.innerHTML = `
    <div class="pp-head">
      ${GLYPH}
      <div><div class="name">${inst.id}</div><div class="sub">${inst.backend}${inst.host ? ' · buildbox' : ''}</div></div>
      <div class="right">
        <button class="icon-btn" data-tip="Open Control panel" aria-label="Open Control panel" id="ppOpenPanel">${ic('panel')}</button>
        <button class="icon-btn" data-menu-anchor aria-label="More" id="ppMore">${ic('more')}</button>
      </div>
    </div>
    <div class="vm-chips" role="group" aria-label="Instances">
      ${INSTANCES.map((i) => `<button class="vm-chip" aria-pressed="${i.id === POP.vm}" data-vm="${i.id}"
          data-tip="<b>${i.id}</b><br>${i.label}">
          <span class="dot ${i.state === 'saved' ? 'saved' : 'running'}"></span>${i.id}
          <span class="m ${i.behind ? 'warn' : ''}">${i.state === 'saved' ? 'saved' : i.child ? '5h20m' : i.behind ? '2 behind' : 'up'}</span></button>`).join('')}
    </div>
    <div class="pp-scroll">
      ${saved ? savedBody(inst) : runningBody(inst)}
    </div>
    <div class="pp-foot">
      ${saved
        ? `<button class="btn primary" style="height:32px;justify-content:center" id="ppResume">${ic('play')} Resume dev-2</button>`
        : `<div class="split"><button class="btn primary" id="ppOpen">${ic('code')} Open in ${POP.openTarget}</button><button class="btn primary" data-menu-anchor id="ppOpenMenu" aria-label="Choose open target">${ic('chevDown')}</button></div>`}
      <button class="mic-btn" aria-pressed="${POP.mic && !saved}" id="ppMic" ${saved ? 'disabled style="opacity:.5"' : ''}
        data-tip="${POP.mic ? '<b>Mic armed</b> · Shure MV7<br>Live only while /voice records. Click to disarm.' : 'Mic passthrough off. Click to arm.'}">${ic(POP.mic ? 'mic' : 'micOff')}${POP.mic ? 'Armed' : 'Off'}</button>
      <span class="stale" data-tip="Status sampled from the VM every 20 s">20 s ago</span>
    </div>`;
  bindPopup(el);
}

function runningBody(inst) {
  const unread = unreadCount();
  return `
    <div class="pp-status">
      <span><span class="dot running" style="margin-right:6px"></span><span class="t1">Running</span> <span class="num">3h 12m</span></span><span class="sep"></span>
      <span>RAM <span class="num">11.2/16 GB</span></span><span class="sep"></span>
      <span class="warn" data-tip="<b>Disk 88% used</b> (128 of 146 GB)<br>Above 85% provisioning can fail.">${ic('disk')} <span class="num" style="color:var(--warn)">88%</span></span><span class="sep"></span>
      <span data-tip="Estimated from ccusage pricing">Today <span class="num">$4.12</span></span>
    </div>
    <div class="pp-badges">
      <button class="pp-badge" id="ppBehind" data-menu-anchor>${ic('warn')} Behind host · 2 commits ${ic('chevDown')}</button>
      <button class="pp-badge neutral" id="ppUpdate" data-menu-anchor>${ic('up')} Construct 3a91f0e</button>
    </div>
    <div class="pp-sec">
      <div class="pp-sec-h"><span class="eyebrow">Agents</span><span class="right stale">bell = wake me when idle</span></div>
      ${AGENT_ORDER.map(agentCard).join('')}
    </div>
    <div class="pp-sec">
      <div class="pp-sec-h"><span class="eyebrow">Forwards</span><span class="right stale">construct expose</span></div>
      <div class="fwd-row">
        ${FORWARDS.map((f) => `<a class="fwd-chip ${f.state}" href="#popup" data-tip="<b>${f.label}</b><br>vm:${f.vm} → ${f.local ? 'localhost:' + f.local : f.where}<br>${f.state === 'open' ? 'Open' : 'Queued'} · opened by ${AGENTS[f.agent].name} ${f.ago}${f.note ? '<br>' + f.note : ''}">
          <span class="dot"></span>:${f.vm}${f.local && f.local !== f.vm ? '→' + f.local : ''} <span class="by"><span class="aw" style="background:${AGENTS[f.agent].color}"></span> ${f.state === 'queued' ? 'queued' : f.label}</span>${f.state === 'open' ? ic('ext') : ''}</a>`).join('')}
      </div>
    </div>
    <div class="pp-sec" style="padding-bottom:10px">
      <div class="pp-sec-h"><span class="eyebrow">Inbox</span>${unread ? `<span class="tag calm">${unread} new</span>` : '<span class="tag">all read</span>'}
        <button class="btn xs ghost right" id="ppReadAll" style="margin-left:auto" ${unread ? '' : 'disabled'}>Mark all read</button></div>
      ${POP.notifies.map((n) => `<div class="inbox-item ${n.read ? 'read' : ''}" data-nid="${n.id}" tabindex="0">
          <span class="lvl">${levelIcon(n.level)}</span>
          <span class="msg">${esc(n.text)}${n.read ? '' : '<span class="unread"></span>'}</span>
          <span class="time">${n.time}</span>
          <span class="meta"><span class="aw" style="display:inline-block;width:6px;height:6px;border-radius:2px;background:${AGENTS[n.agent].color}"></span> ${n.agent} · ${n.repo}</span>
        </div>`).join('')}
    </div>`;
}

function agentCard(id) {
  const a = AGENTS[id];
  const working = a.state === 'working' && !POP.claudeDone;
  const minutes = working ? NOW_MIN - a.since : null;
  let stateTxt, line;
  if (id === 'claude') {
    stateTxt = working ? `working · ${minutes} min` : 'idle · just now';
    line = `<span class="repo">${a.repo}</span><span>·</span><span class="task">${working ? a.task : 'Done: ' + a.task}</span>`;
  } else if (id === 'codex') {
    stateTxt = 'idle · 48 min';
    line = `<span class="repo">${a.repo}</span><span>·</span><span class="task" style="color:var(--crit)">notify: Deploy failed</span>`;
  } else if (id === 'opencode') {
    stateTxt = 'idle';
    line = `<span class="repo">serve :4096</span><span>·</span><span class="task">last turn 13:12 on omniloop</span>`;
  } else {
    stateTxt = 'web UI';
    line = `<span class="task">2 threads active · :5177</span>`;
  }
  const upd = a.latest ? `<span class="tag warn" data-tip="Claude Code ${a.version} → ${a.latest}<br>Updates apply between turns.">${a.latest}</span>` : '';
  const bellable = id !== 't3';
  return `<div class="agent-card ${working ? 'working' : ''}">
    <span class="agent-av" style="background:${a.color}">${a.short}<span class="st ${working ? 'live' : 'idle'} ${working ? 'pulse' : ''}"></span></span>
    <div class="agent-name">${a.name} <span class="state ${working ? 'live' : ''}">${stateTxt}</span> ${upd}</div>
    <div class="agent-actions">
      ${id === 't3' ? `<button class="btn sm ghost" data-tip="Open T3 Code web UI">${ic('ext')} Open</button>` : ''}
      ${bellable ? `<button class="bell" aria-pressed="${POP.bells[id]}" data-bell="${id}" aria-label="Wake me when ${a.name} is done"
          data-tip="${POP.bells[id] ? '<b>Armed</b>: toast when ' + a.name + ' goes idle' : 'Wake me when ' + a.name + ' is done'}">${ic(POP.bells[id] ? 'bellOn' : 'bell')}</button>` : ''}
    </div>
    <div class="agent-line">${line}</div>
  </div>`;
}

function savedBody(inst) {
  return `
    <div class="pp-status">
      <span><span class="dot saved" style="margin-right:6px"></span><span class="t1">Saved</span> 41 min ago by idle policy</span><span class="sep"></span>
      <span>buildbox · <span class="num">4 vCPU · 12 GB</span></span>
    </div>
    <div class="hero-saved">
      <canvas id="rainCanvas"></canvas>
      <div class="inner">
        <span class="eyebrow">Suspended · RAM on disk</span>
        <h3>dev-2 is asleep. Nothing is lost.</h3>
        <p>The host saved it after 60 idle minutes. Resume restores memory in about 10 s, including agent sessions. No UAC prompt: the host service does it.</p>
      </div>
    </div>
    <div class="pp-sec">
      <div class="pp-sec-h"><span class="eyebrow">Agents at save time</span></div>
      ${['claude', 'codex'].map((id) => { const a = AGENTS[id]; return `<div class="agent-card" style="opacity:.72">
        <span class="agent-av" style="background:${a.color};filter:saturate(.5)">${a.short}<span class="st idle"></span></span>
        <div class="agent-name">${a.name} <span class="state">idle when saved</span></div>
        <div class="agent-actions"><button class="bell" aria-pressed="false" aria-label="Wake me">${ic('bell')}</button></div>
        <div class="agent-line"><span class="repo">${id === 'claude' ? 'emili-simulator' : 'jarvis'}</span><span>·</span><span class="task">last turn ${id === 'claude' ? '12:31' : '11:02'}</span></div>
      </div>`; }).join('')}
    </div>
    <div class="pp-sec">
      <div class="pp-sec-h"><span class="eyebrow">Other instances</span></div>
      <button class="inbox-item" style="width:100%;text-align:left;background:var(--panel);border-color:var(--line)" data-vm="agent-vm">
        <span class="lvl"><span class="dot live" style="margin-top:3px"></span></span>
        <span class="msg">agent-vm: Claude working 6 min</span><span class="time">switch ${ic('arrowRight')}</span>
        <span class="meta">2 new notices · 2 forwards open</span>
      </button>
    </div>`;
}

function bindPopup(el) {
  const desk = $('#desktop');
  el.querySelectorAll('[data-vm]').forEach((b) => b.addEventListener('click', () => { POP.vm = b.dataset.vm === 'win-test' ? 'agent-vm' : b.dataset.vm; if (b.dataset.vm === 'win-test') toast(desk, { title: 'win-test is a child VM', msg: 'Child VMs open in their browser console. Opened in the Control panel.', icon: 'child' }); refreshScenario(); renderPopupSurface($('#stage')); }));
  el.querySelectorAll('[data-bell]').forEach((b) => b.addEventListener('click', () => {
    const id = b.dataset.bell; POP.bells[id] = !POP.bells[id]; renderPopup(); renderCtxMenu();
    if (POP.bells[id]) toast(desk, { title: `Watching ${AGENTS[id].name}`, msg: 'You get a Windows notification when it goes idle. No agent-side notify needed.', icon: 'bellOn', cls: 'live', ms: 3500 });
  }));
  const mic = $('#ppMic', el); if (mic) mic.addEventListener('click', () => { POP.mic = !POP.mic; renderPopup(); renderCtxMenu(); });
  const ra = $('#ppReadAll', el); if (ra) ra.addEventListener('click', () => { POP.notifies.forEach((n) => { n.read = true; }); renderPopup(); refreshScenario(); });
  el.querySelectorAll('[data-nid]').forEach((r) => r.addEventListener('click', () => { const n = POP.notifies.find((x) => x.id === r.dataset.nid); n.read = true; renderPopup(); refreshScenario(); }));
  const op = $('#ppOpenPanel', el); if (op) op.addEventListener('click', () => { location.hash = '#panel'; });
  const res = $('#ppResume', el); if (res) res.addEventListener('click', () => { res.innerHTML = `${ic('refresh')} Resuming… restoring 12 GB`; res.disabled = true; setTimeout(() => { toast(desk, { title: 'dev-2 resumed', msg: 'Back in 9 s. Agents reattached.', icon: 'ok', cls: 'live' }); }, 1200); });
  const om = $('#ppOpenMenu', el);
  if (om) om.addEventListener('click', () => {
    const m = openMenu(om, `<div class="head eyebrow">Open agent-vm in</div>
      ${['VS Code', 'T3 Code', 'Browser console', 'Terminal (SSH)'].map((x) => `<button data-t="${x}"><span class="check-mark">${x === POP.openTarget ? ic('check') : ''}</span>${x}<span class="hint">${x === 'VS Code' ? 'Remote-SSH' : x === 'T3 Code' ? ':5177' : x === 'Browser console' ? 'Hyper-V' : 'root@'}</span></button>`).join('')}
      <div class="sep"></div><div class="head stale">The choice is remembered for this button.</div>`, desk, { alignRight: false, above: true });
    m.querySelectorAll('[data-t]').forEach((b) => b.addEventListener('click', () => { POP.openTarget = b.dataset.t; closeMenu(); renderPopup(); }));
  });
  const behind = $('#ppBehind', el);
  if (behind) behind.addEventListener('click', () => {
    const working = !POP.claudeDone;
    const m = openMenu(behind, `<div style="padding:8px 10px;max-width:320px;display:grid;gap:6px">
        <div class="t1" style="font-weight:600">agent-vm is 2 commits behind the host</div>
        <div class="stale">provisioned c21978b · installed b5c4348</div>
        <div style="font-size:var(--fs-11);color:var(--t3)">Missing: "Default VM CPU allocation to the host allowance", "Initialize vTPM identity before capturing reusable VM baselines". Reprovision keeps all data and takes about a minute.</div>
        ${working ? '<div style="font-size:var(--fs-11);color:var(--warn)">Claude is mid-turn. Reprovisioning now restarts its services.</div>' : ''}
      </div><div class="sep"></div>
      ${working ? `<button id="rpWait">${ic('clock')} Reprovision when Claude finishes<span class="hint">recommended</span></button>` : ''}
      <button id="rpNow">${ic('refresh')} Reprovision now</button>
      <button id="rpDetails">${ic('panel')} Details in Control panel</button>`, desk);
    $('#rpWait', m)?.addEventListener('click', () => { closeMenu(); behind.innerHTML = `${ic('clock')} Reprovision queued · after Claude`; behind.classList.add('neutral'); toast(desk, { title: 'Reprovision queued', msg: 'Runs automatically when Claude Code goes idle. Cancel from the same badge.', icon: 'clock' }); });
    $('#rpNow', m).addEventListener('click', () => { closeMenu(); toast(desk, { title: 'Reprovisioning agent-vm', msg: 'Step 3/5: uploading repo. Data and agents\' auth are kept.', icon: 'refresh' }); });
    $('#rpDetails', m).addEventListener('click', () => { closeMenu(); location.hash = '#panel'; });
  });
  const upd = $('#ppUpdate', el);
  if (upd) upd.addEventListener('click', () => {
    const m = openMenu(upd, `<div style="padding:8px 10px;max-width:310px;display:grid;gap:5px">
      <div class="t1" style="font-weight:600">Construct update: b5c4348 → 3a91f0e</div>
      <div class="stale">4 commits</div>
      <ul style="margin:2px 0 0;padding-left:16px;font-size:var(--fs-11);color:var(--t3)"><li>Default VM CPU allocation to the host allowance</li><li>Initialize vTPM identity before capturing reusable VM baselines</li><li>+2 more</li></ul>
      <div style="font-size:var(--fs-11);color:var(--t3)">Updates this PC only. VMs keep running; reprovision later to bring them along.</div></div>
      <div class="sep"></div><button>${ic('download')} Update Construct</button><button>${ic('eye')} Full changelog</button>`, desk);
    m.querySelectorAll('button').forEach((b) => b.addEventListener('click', closeMenu));
  });
  const more = $('#ppMore', el);
  more.addEventListener('click', () => {
    const m = openMenu(more, `
      <button data-go="#panel">${ic('panel')} Control panel</button>
      <button data-go="#panel/usage">${ic('chart')} Usage &amp; cost</button>
      <button data-go="#panel/projects">${ic('folder')} Projects</button>
      <button data-go="#panel/settings">${ic('gear')} Settings</button>
      <div class="sep"></div>
      <button data-go="#host">${ic('host')} Host administration<span class="hint">buildbox</span></button>
      <div class="sep"></div>
      <div class="head stale">Reinstall, redownload and remove live in the Control panel's Danger zone.</div>`, desk, { alignRight: true });
    m.querySelectorAll('[data-go]').forEach((b) => b.addEventListener('click', () => { closeMenu(); location.hash = b.dataset.go; }));
  });
  const canvas = $('#rainCanvas', el); if (canvas) codeRain(canvas);
}
