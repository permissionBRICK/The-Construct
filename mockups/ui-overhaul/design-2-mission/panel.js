/* Control panel: Mission (3-column), Projects, Usage, Settings, Danger zone. */
'use strict';

const PANEL = {
  tab: 'mission',
  inst: 'agent-vm',
  agent: 'claude',
  feed: 'all',
  project: 'construct',
  usageView: 'chart',
  usagePeriod: 'today',
  setSec: 'general',
  dirty: new Set(),
  projDirty: false,
  projSel: { construct: true, omniloop: true, gitgudlab: true, 'emili-simulator': false, jarvis: true },
  bells: POP.bells,
  removed: false,
};

const PANEL_TABS = [
  ['mission', 'Mission'],
  ['projects', 'Projects'],
  ['usage', 'Usage'],
  ['settings', 'Settings'],
  ['danger', 'Danger zone'],
];

function panelScenario() {
  return [
    { label: 'agent-vm · Claude working', active: PANEL.inst === 'agent-vm', run: () => { PANEL.inst = 'agent-vm'; PANEL.tab = 'mission'; } },
    { label: 'dev-2 saved', active: PANEL.inst === 'dev-2', run: () => { PANEL.inst = 'dev-2'; PANEL.tab = 'mission'; } },
    { label: 'win-test child VM', active: PANEL.inst === 'win-test', run: () => { PANEL.inst = 'win-test'; PANEL.tab = 'mission'; } },
    { label: 'Danger: reinstall confirm', active: false, run: () => { PANEL.inst = 'agent-vm'; PANEL.tab = 'danger'; setTimeout(() => dangerAction('reinstall'), 60); } },
  ];
}

function renderPanelSurface(stage, sub) {
  if (sub && PANEL_TABS.some(([id]) => id === sub)) PANEL.tab = sub;
  const inst = INSTANCES.find((i) => i.id === PANEL.inst);
  stage.innerHTML = `
  <div class="win" id="pwin">
    <div class="titlebar">${GLYPH}<span class="title">The Construct — ${inst.id}</span><span class="t4">· Control panel</span>
      <div class="wbtns"><span><svg viewBox="0 0 10 10"><path d="M1 5h8"/></svg></span><span><svg viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7"/></svg></span><span class="x"><svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1L1 9"/></svg></span></div>
    </div>
    <div class="appbar">
      <button class="btn ghost" id="instPick" data-menu-anchor style="font-weight:600;margin-right:8px">
        <span class="dot ${inst.state === 'saved' ? 'saved' : 'running'}"></span>${inst.id}${ic('chevDown')}
      </button>
      <nav class="tabs" role="tablist">
        ${PANEL_TABS.map(([id, label]) => `<button role="tab" data-ptab="${id}" aria-selected="${PANEL.tab === id}" class="${id === 'danger' ? 'danger-tab' : ''}">${label}${id === 'projects' ? '<span class="count">1 new</span>' : ''}${id === 'mission' ? `<span class="count ${unreadCount() ? 'warn' : ''}" data-tip="Unread notices">${unreadCount() || '0'}</span>` : ''}</button>`).join('')}
      </nav>
      <div class="right">
        <button class="btn sm" id="pUpdate" data-menu-anchor>${ic('up')} Construct update · 3a91f0e</button>
        <a class="btn sm ghost" href="#host">${ic('host')} Host admin</a>
      </div>
    </div>
    <div class="win-body" id="pbody"></div>
  </div>`;
  $$('[data-ptab]').forEach((b) => b.addEventListener('click', () => { PANEL.tab = b.dataset.ptab; history.replaceState(null, '', '#panel/' + PANEL.tab); renderPanelSurface(stage); refreshScenario(); }));
  const ip = $('#instPick');
  ip.addEventListener('click', () => {
    const m = openMenu(ip, `<div class="head eyebrow">Instances on PC-1</div>
      ${INSTANCES.map((i) => `<button data-i="${i.id}" style="${i.child ? 'padding-left:24px' : ''}"><span class="check-mark">${i.id === PANEL.inst ? ic('check') : ''}</span><span class="dot ${i.state === 'saved' ? 'saved' : 'running'}"></span>${i.id}<span class="hint">${i.state === 'saved' ? 'saved' : i.child ? 'child · 5h20m' : i.behind ? '2 behind' : 'running'}</span></button>`).join('')}
      <div class="sep"></div><button>${ic('plus')} Add instance…</button><button>${ic('link')} Register a VM by address…</button>`, $('#pwin'));
    m.querySelectorAll('[data-i]').forEach((b) => b.addEventListener('click', () => { PANEL.inst = b.dataset.i; closeMenu(); renderPanelSurface(stage); refreshScenario(); }));
  });
  $('#pUpdate').addEventListener('click', (e) => {
    const m = openMenu(e.currentTarget, `<div style="padding:8px 10px;max-width:330px;display:grid;gap:5px"><div class="t1" style="font-weight:600">Update Construct · b5c4348 → 3a91f0e</div>
      <ul style="margin:0;padding-left:16px;font-size:var(--fs-11);color:var(--t3)"><li>Default VM CPU allocation to the host allowance</li><li>Initialize vTPM identity before capturing reusable VM baselines</li><li>Clarify optional CPU sizing in the child VM design</li><li>Add opt-in Hyper-V license machine reuse</li></ul>
      <div style="font-size:var(--fs-11);color:var(--t3)">Updates the Companion and scripts on this PC. VMs keep running. Afterwards each VM shows "behind" until you reprovision it.</div></div>
      <div class="sep"></div><button id="doUpd">${ic('download')} Update now</button>`, $('#pwin'), { alignRight: true });
    $('#doUpd', m).addEventListener('click', () => { closeMenu(); toast($('#pwin'), { title: 'Updating Construct…', msg: 'Step 1/5 · downloading 3a91f0e (checksum verified). The Companion restarts itself at the end.', icon: 'download' }); });
  });
  const body = $('#pbody');
  ({ mission: renderMission, projects: renderProjects, usage: renderUsage, settings: renderSettings, danger: renderDanger })[PANEL.tab](body);
}

/* ── Mission ────────────────────────────────────────────────────────── */
function renderMission(body) {
  const inst = INSTANCES.find((i) => i.id === PANEL.inst);
  body.innerHTML = `<div class="mission">
    <aside class="m-left">${missionLeft(inst)}</aside>
    <section class="m-center" id="mCenter">${inst.state === 'saved' ? missionSaved(inst) : inst.child ? missionChild(inst) : missionCenter()}</section>
    <aside class="m-right" id="mRight">${inst.state === 'saved' ? inspectorSaved() : inst.child ? inspectorChild() : inspector(PANEL.agent)}</aside>
  </div>`;
  bindMission(body, inst);
  if (inst.id === 'agent-vm') drawLanes();
}

function missionLeft(inst) {
  const list = INSTANCES.map((i) => `<button class="inst ${i.child ? 'child' : ''}" data-inst="${i.id}" aria-current="${i.id === inst.id}">
      <span class="dot ${i.state === 'saved' ? 'saved' : i.id === 'agent-vm' ? 'live' : 'running'}"></span>
      <span class="n">${i.id}</span>
      ${i.behind ? '<span class="tag warn">2 behind</span>' : i.child ? '<span class="tag">child</span>' : '<span></span>'}
      <span class="d">${i.label}</span></button>`).join('');
  let vit = '';
  if (inst.id === 'agent-vm') {
    vit = `
    <div class="vital"><div class="row"><span>Memory</span><span class="num">11.2 <small>/ 16 GB</small></span></div><div class="meter"><i style="width:70%"></i></div></div>
    <div class="vital"><div class="row"><span>Disk <span class="tag warn" style="margin-left:4px">${ic('warn')} 88%</span></span><span class="num">128 <small>/ 146 GB</small></span></div><div class="meter"><i class="warn" style="width:88%"></i></div></div>
    <dl class="kv"><dt>vCPU</dt><dd>8</dd><dt>Uptime</dt><dd>3h 12m</dd><dt>OS</dt><dd>Ubuntu 26.04 LTS</dd><dt>Address</dt><dd>agent-vm.mshome.net</dd></dl>
    <div class="behind-box">
      <div style="display:flex;gap:6px;align-items:center;color:var(--warn);font-weight:600">${ic('warn')} Behind host by 2 commits</div>
      <div class="commits">provisioned <b>c21978b</b> · installed <b>b5c4348</b></div>
      <div>Claude is mid-turn. Reprovisioning restarts agent services.</div>
      <div style="display:flex;gap:6px"><button class="btn sm primary" id="rpAfter">${ic('clock')} After Claude</button><button class="btn sm" id="rpNowP">Now</button></div>
    </div>
    <div><div class="eyebrow" style="margin-bottom:6px">Power</div>
      <div class="power-row"><button class="btn sm" data-power="save">${ic('pause')} Save</button><button class="btn sm" data-power="restart">${ic('restart')} Restart</button><button class="btn sm" data-power="stop">${ic('power')} Stop</button></div>
      <div class="stale" style="margin-top:5px">Local VM · each power action needs one UAC prompt</div></div>
    <div class="divider"></div>
    <div style="display:flex;align-items:center;gap:8px">
      <span style="color:${POP.mic ? 'var(--live)' : 'var(--t3)'}">${ic(POP.mic ? 'mic' : 'micOff')}</span>
      <div style="flex:1"><div class="t1" style="font-size:var(--fs-12);font-weight:500">Mic passthrough</div><div class="stale">${POP.mic ? 'armed · Shure MV7 · not streaming' : 'off'}</div></div>
      <button class="switch live" role="switch" aria-checked="${POP.mic}" id="micSw" aria-label="Mic passthrough"></button>
    </div>
    <button class="btn sm ghost" style="justify-content:flex-start">${ic('terminal')} Browser console</button>
    <div class="stale">sampled 20 s ago</div>`;
  } else if (inst.state === 'saved') {
    vit = `<dl class="kv"><dt>Backend</dt><dd>hyperv-remote</dd><dt>Host</dt><dd>buildbox:7462</dd><dt>Size</dt><dd>4 vCPU · 12 GB</dd><dt>Idle policy</dt><dd>save after 60 min</dd><dt>Saved</dt><dd>13:37 (41 min ago)</dd></dl>
      <button class="btn primary" style="justify-content:center" id="resumeL">${ic('play')} Resume</button>
      <div class="stale">No UAC: the host service resumes it</div>`;
  } else {
    vit = `<dl class="kv"><dt>Parent</dt><dd>agent-vm</dd><dt>Guest</dt><dd>Windows 11 eval</dd><dt>Memory</dt><dd>4 GB</dd><dt>Lease</dt><dd>5h 20m left</dd><dt>Created by</dt><dd>construct vm create</dd></dl>`;
  }
  return `<div class="col-h"><span class="eyebrow">Instances</span><button class="icon-btn right" data-tip="Add instance" aria-label="Add instance">${ic('plus')}</button></div>
    ${list}
    <div class="col-h" style="margin-top:6px"><span class="eyebrow">Machine · ${inst.id}</span></div>
    <div class="vitals">${vit}</div>`;
}

function missionCenter() {
  return `<div class="lanes-wrap">
    <div class="lanes-head">
      <h2>Agent lanes · today</h2>
      <div class="legend">
        <span><svg width="18" height="8"><rect width="18" height="8" rx="2" fill="var(--t3)"/></svg>turn</span>
        <span><svg width="10" height="10"><path d="M5 0l5 5-5 5-5-5z" fill="var(--info)"/></svg>notify</span>
        <span><svg width="10" height="10"><path d="M5 0l5 5-5 5-5-5z" fill="var(--crit)"/></svg>notify · error</span>
        <span><svg width="10" height="10"><rect x="1" y="1" width="8" height="8" rx="1" fill="none" stroke="var(--t2)" stroke-width="1.5"/></svg>forward opened</span>
      </div>
      <span class="stale" style="margin-left:auto">08:00 – now · live</span>
    </div>
    <div class="lanes" id="lanes">
      ${AGENT_ORDER.map((id) => { const a = AGENTS[id]; const working = a.state === 'working';
        return `<div class="lane" data-lane="${id}" aria-selected="${PANEL.agent === id}" tabindex="0">
          <div class="lane-label"><span class="sw" style="background:${a.color}"></span><div><span class="nm">${a.name}</span><span class="st ${working ? 'live' : ''}">${working ? 'working · 6 min' : id === 'codex' ? 'idle · 48 min' : id === 'opencode' ? 'idle · :4096' : 'web · 2 threads'}</span></div></div>
          <svg data-svg="${id}"></svg></div>`; }).join('')}
      <div class="axis"><div></div><svg id="axisSvg"></svg></div>
    </div>
  </div>
  <div style="padding:6px 18px 0">
    <div class="card">
      <div class="card-h"><h3>Forwards</h3><span class="tag live when-live">2 open</span><span class="tag">1 queued</span>
        <div class="right"><span class="stale">agents open these with construct expose</span></div></div>
      <table class="tbl fwd-table"><thead><tr><th>Link</th><th>From VM</th><th>Label</th><th>Opened by</th><th></th></tr></thead><tbody>
      ${FORWARDS.map((f) => `<tr>
        <td>${f.state === 'open' ? `<a class="url open" href="#panel">localhost:${f.local}</a>` : `<span class="url" style="color:var(--t3)">${f.where}</span>`}${f.note ? `<span class="sub">${f.note}</span>` : ''}</td>
        <td class="mono" style="font-size:var(--fs-11)">vm:${f.vm}</td>
        <td>${f.label}</td>
        <td><span style="display:inline-flex;align-items:center;gap:6px"><span class="aw" style="width:8px;height:8px;border-radius:2px;background:${AGENTS[f.agent].color}"></span>${AGENTS[f.agent].name}</span><span class="sub">${f.opened} · ${f.ago}</span></td>
        <td class="r">${f.state === 'open' ? `<button class="btn xs">${ic('ext')} Open</button> <button class="btn xs ghost" data-tip="Close forward">${ic('x')}</button>` : `<span class="tag">queued</span> <button class="btn xs ghost" data-tip="Cancel">${ic('x')}</button>`}</td></tr>`).join('')}
      </tbody></table>
    </div>
  </div>
  <div style="padding:0 18px 18px">
    <div class="feed-tabs"><span class="eyebrow" style="margin-right:6px">Activity</span>
      ${[['all', 'All'], ['notify', 'Notifies'], ['forward', 'Forwards'], ['turn', 'Turns'], ['machine', 'Machine']].map(([k, l]) => `<button class="chip" data-feed="${k}" aria-pressed="${PANEL.feed === k}">${l}</button>`).join('')}
    </div>
    <div class="feed" id="feed">${feedRows()}</div>
  </div>`;
}

const FEED = [
  { time: '14:12', kind: 'turn', agent: 'claude', html: '<b>Claude Code</b> started a turn · "Refactoring forwarder retry backoff"', repo: 'construct', live: true },
  { time: '14:06', kind: 'forward', agent: 'claude', html: '<b>Claude Code</b> exposed vm:5173 → <span class="mono">localhost:5173</span> "vite dev server"', repo: 'construct' },
  { time: '14:02', kind: 'notify', level: 'info', agent: 'claude', html: '<b>Test suite finished — 3 failures</b>', repo: 'construct' },
  { time: '13:40', kind: 'notify', level: 'error', agent: 'codex', html: '<b>Deploy failed, rolling back</b>', repo: 'gitgudlab' },
  { time: '13:30', kind: 'turn', agent: 'codex', html: '<b>Codex</b> finished a turn (40 min)', repo: 'gitgudlab' },
  { time: '13:05', kind: 'forward', agent: 'opencode', html: '<b>OpenCode</b> requested host forward vm:3000 · queued on buildbox LAN', repo: 'omniloop' },
  { time: '12:15', kind: 'notify', level: 'info', agent: 'claude', html: '<b>PR #31 opened</b>', repo: 'omniloop' },
  { time: '11:20', kind: 'forward', agent: 'codex', html: '<b>Codex</b> exposed vm:8080 → <span class="mono">localhost:18800</span> (8080 busy) "api preview"', repo: 'gitgudlab' },
  { time: '11:06', kind: 'machine', html: 'Construct update 3a91f0e became available', repo: '' },
  { time: '11:02', kind: 'machine', html: 'Config sync pulled 1 new profile from the VM: <span class="mono">jarvis</span>', repo: '' },
  { time: '08:01', kind: 'machine', html: 'agent-vm started · provisioned at c21978b', repo: '' },
];
function feedRows() {
  const rows = FEED.filter((f) => PANEL.feed === 'all' || f.kind === PANEL.feed);
  return rows.map((f) => {
    let icon;
    if (f.kind === 'notify') icon = levelIcon(f.level);
    else if (f.kind === 'forward') icon = `<span style="color:var(--t2)">${ic('fwd')}</span>`;
    else if (f.kind === 'turn') icon = f.live ? '<span class="dot live pulse" style="margin-left:4px"></span>' : `<span class="t3">${ic('clock')}</span>`;
    else icon = `<span class="t4">${ic('host')}</span>`;
    const who = f.agent ? `<span class="aw" style="display:inline-block;width:7px;height:7px;border-radius:2px;margin-right:6px;background:${AGENTS[f.agent].color}"></span>` : '';
    return `<div class="feed-row"><span class="time">${f.time}</span><span class="ico">${icon}</span><span class="txt">${who}${f.html} ${f.repo ? `<span class="repo">· ${f.repo}</span>` : ''}</span>
      <span>${f.kind === 'notify' ? '<button class="btn xs ghost">Jump to agent</button>' : f.kind === 'forward' && f.agent !== 'opencode' ? `<button class="btn xs ghost">${ic('ext')} Open</button>` : ''}</span></div>`;
  }).join('') || '<div class="feed-row"><span></span><span></span><span class="txt t3">Nothing of this kind today.</span><span></span></div>';
}

function drawLanes() {
  const x0 = t('08:00'), x1 = t('14:40');
  const anySvg = $('[data-svg="claude"]');
  if (!anySvg) return;
  const W = anySvg.clientWidth - 14;
  const X = (m) => 6 + ((m - x0) / (x1 - x0)) * W;
  const nowX = X(NOW_MIN);
  const grid = [8, 9, 10, 11, 12, 13, 14].map((h) => `<line x1="${X(h * 60)}" x2="${X(h * 60)}" y1="0" y2="46" stroke="var(--line)" />`).join('');
  AGENT_ORDER.forEach((id) => {
    const svg = $(`[data-svg="${id}"]`);
    const a = AGENTS[id];
    let marks = grid;
    TURNS[id].forEach(([s, e]) => {
      const sm = t(s), em = e === 'now' ? NOW_MIN : t(e);
      const live = e === 'now';
      const dur = em - sm;
      marks += `<rect x="${X(sm)}" y="17" width="${Math.max(3, X(em) - X(sm))}" height="12" rx="3" fill="${a.hex}" ${live ? 'stroke="var(--live)" stroke-width="1.5"' : ''}
        data-tip="<b>${a.name}</b> · turn<br><span class='num'>${s}–${live ? 'now' : e}</span> · ${dur} min${live ? '<br>' + a.task : ''}" />`;
    });
    if (id === 't3') {
      T3_SESSIONS.forEach(([s]) => { marks += `<rect x="${X(t(s))}" y="21" width="${nowX - X(t(s))}" height="4" rx="2" fill="${a.hex}" opacity=".8" data-tip="<b>T3 Code web UI</b><br>open since ${s} · drives Claude + Codex threads" />`; });
      marks += `<text x="${X(t('09:10'))}" y="15">web UI session · 2 threads</text>`;
    }
    NOTIFIES.filter((n) => n.agent === id).forEach((n) => {
      const x = X(t(n.time));
      const col = n.level === 'error' ? 'var(--crit)' : 'var(--info)';
      marks += `<g data-tip="<b>notify · ${n.level}</b><br>${esc(n.text)}<br><span class='num'>${n.time}</span> · ${n.repo}"><rect x="${x - 8}" y="0" width="16" height="16" fill="transparent"/><path d="M${x} 2l5 5-5 5-5-5z" fill="${col}" stroke="var(--ink)" stroke-width="1.5"/></g>`;
    });
    FORWARDS.filter((f) => f.agent === id).forEach((f) => {
      const x = X(t(f.opened));
      marks += `<g data-tip="<b>forward ${f.state}</b><br>vm:${f.vm} → ${f.local ? 'localhost:' + f.local : f.where}<br>${f.label} · ${f.opened}"><rect x="${x - 8}" y="30" width="16" height="16" fill="transparent"/><rect x="${x - 4}" y="33" width="8" height="8" rx="1.5" fill="var(--ink)" stroke="${f.state === 'open' ? 'var(--live)' : 'var(--t2)'}" stroke-width="1.5"/><text x="${x + 7}" y="41">:${f.vm}</text></g>`;
    });
    marks += `<line x1="${nowX}" x2="${nowX}" y1="0" y2="46" stroke="var(--live)" stroke-opacity=".55" stroke-dasharray="2 3"/>`;
    svg.innerHTML = marks;
  });
  const ax = $('#axisSvg');
  ax.innerHTML = [8, 9, 10, 11, 12, 13, 14].map((h) => `<text x="${X(h * 60)}" y="15" text-anchor="middle">${String(h).padStart(2, '0')}:00</text>`).join('')
    + `<text x="${nowX}" y="24" text-anchor="middle" style="fill:var(--live)">now</text>`;
}

function inspector(id) {
  const a = AGENTS[id];
  const working = a.state === 'working';
  const fw = FORWARDS.filter((f) => f.agent === id);
  const nt = NOTIFIES.filter((n) => n.agent === id);
  const bell = id !== 't3' ? `<button class="bell" aria-pressed="${PANEL.bells[id]}" id="inspBell" style="margin-left:auto" data-tip="Wake me when ${a.name} is done">${ic(PANEL.bells[id] ? 'bellOn' : 'bell')}</button>` : '';
  let now;
  if (working) now = `<div class="insp-now"><div style="display:flex;align-items:center;gap:7px"><span class="dot live pulse"></span><span class="eyebrow" style="color:var(--live)">Working · 6 min</span>${bell}</div>
      <div class="task">${a.task}</div><div class="stale">turn started 14:12 · 9 turns today · last tool call 8 s ago</div></div>`;
  else if (id === 't3') now = `<div class="insp-now idle"><div style="display:flex;align-items:center;gap:7px"><span class="eyebrow">Web UI enabled · stable</span></div><div class="task">2 threads active</div><div class="stale">https://agent-vm:5177 · drives Claude and Codex</div></div>`;
  else now = `<div class="insp-now idle"><div style="display:flex;align-items:center;gap:7px"><span class="dot" style="background:#5b7a67"></span><span class="eyebrow">Idle · ${id === 'codex' ? '48 min' : '66 min'}</span>${bell}</div><div class="task">${id === 'codex' ? 'Last turn: deploy pipeline retry' : 'serve :4096 running, no session'}</div><div class="stale">${id === 'codex' ? 'ended 13:30 · 3 turns today' : 'last turn 13:00–13:12'}</div></div>`;
  return `<div class="insp">
    <div class="insp-head">${avatar(id)}<div><h3>${a.name}</h3><div class="stale">${a.endpoint}</div></div></div>
    ${now}
    ${a.cost != null ? `<div class="stat-row"><div class="stat"><div class="v">${money(a.cost)}</div><div class="l">cost today</div></div><div class="stat"><div class="v">${a.tokens}</div><div class="l">tokens</div></div><div class="stat"><div class="v">${a.turns}</div><div class="l">turns</div></div></div>` : ''}
    <div class="insp-sec"><h4 class="eyebrow">Version</h4>
      <div class="upd"><span class="ver">${a.version}</span>${a.latest ? `<span class="t3">→</span><span class="ver t1">${a.latest}</span><button class="btn sm primary" style="margin-left:auto" id="agentUpd" ${working ? 'data-tip="Waits for the current turn to finish"' : ''}>${working ? 'Update after turn' : 'Update'}</button>` : '<span class="tag calm" style="margin-left:auto">up to date</span>'}</div>
      <button class="btn xs ghost" style="margin-top:6px">Update all agents</button></div>
    ${a.repo !== '—' ? `<div class="insp-sec"><h4 class="eyebrow">Repo</h4>
      <dl class="kv"><dt>Project</dt><dd>${a.repo}</dd><dt>Branch</dt><dd>${a.branch}</dd><dt>Working tree</dt><dd>${id === 'claude' ? '4 files changed' : 'clean'}</dd></dl></div>` : ''}
    <div class="insp-sec"><h4 class="eyebrow">Forwards it opened</h4>
      ${fw.length ? fw.map((f) => `<div class="fwd-row" style="margin-bottom:5px"><a class="fwd-chip ${f.state}" href="#panel"><span class="dot"></span>:${f.vm}${f.local && f.local !== f.vm ? '→' + f.local : ''} <span class="by">${f.label}</span></a></div>`).join('') : '<div class="stale">none today</div>'}</div>
    <div class="insp-sec"><h4 class="eyebrow">Notifies today</h4>
      ${nt.length ? nt.map((n) => `<div style="display:grid;grid-template-columns:16px 1fr auto;gap:8px;font-size:var(--fs-12);margin-bottom:5px;align-items:start"><span>${levelIcon(n.level)}</span><span class="t1">${esc(n.text)}<span class="stale" style="display:block">${n.repo}</span></span><span class="stale">${n.time}</span></div>`).join('') : '<div class="stale">none today</div>'}</div>
    <div style="display:flex;gap:6px;flex-wrap:wrap">
      <button class="btn sm">${ic('terminal')} Attach terminal</button>
      <button class="btn sm ghost">${ic('ext')} Open in T3</button>
    </div>
  </div>`;
}

function missionSaved(inst) {
  return `<div style="padding:18px">
    <div class="hero-saved" style="margin:0;min-height:260px;display:grid">
      <canvas id="rainCanvas"></canvas>
      <div class="inner" style="align-self:end;padding:26px 24px">
        <span class="eyebrow">Suspended by idle policy · 13:37</span>
        <h3 style="font-size:var(--fs-24)">dev-2 is asleep. Its agents are paused, not stopped.</h3>
        <p>Idle for 60 minutes with no agent working and nobody connected, so buildbox saved its RAM to disk. Resume restores the exact state in about 10 s.</p>
        <div style="display:flex;gap:8px;margin-top:6px"><button class="btn primary" id="resumeC">${ic('play')} Resume dev-2</button><button class="btn ghost" data-go-set="idle">${ic('gear')} Idle policy</button></div>
      </div>
    </div>
    <div class="card" style="margin-top:14px"><div class="card-h"><h3>Last known agent activity</h3><span class="stale right">as of 13:37</span></div>
      <div class="feed" style="border:0;border-radius:0 0 11px 11px">
        <div class="feed-row"><span class="time">12:31</span><span class="ico"><span class="t3">${ic('clock')}</span></span><span class="txt"><b>Claude Code</b> finished a turn on <span class="mono">emili-simulator</span></span><span></span></div>
        <div class="feed-row"><span class="time">11:02</span><span class="ico"><span class="t3">${ic('clock')}</span></span><span class="txt"><b>Codex</b> finished a turn on <span class="mono">jarvis</span></span><span></span></div>
      </div></div>
  </div>`;
}
function inspectorSaved() {
  return `<div class="insp"><div class="eyebrow">Idle policy · dev-2</div>
    <div class="set-rows"><div class="set-row"><div><div class="l">Save after</div><div class="d">idle = no agent turn, no connection</div></div><div class="ctl"><span class="num t1">60 min</span></div></div>
    <div class="set-row"><div><div class="l">Action</div></div><div class="ctl"><span class="tag saved">save RAM to disk</span></div></div></div>
    <div class="stale">Enforced by the host service, so it works with this PC switched off. A VM stays up while an agent works, even with nobody connected.</div>
    <button class="btn sm" data-go-set="idle">Change idle policy</button></div>`;
}
function missionChild() {
  return `<div class="pad"><div class="section-title"><h2>win-test</h2><p>Child VM of agent-vm · Windows 11 eval</p></div>
    <div class="card"><div class="card-h"><h3>Lease</h3><span class="right stale">expires 19:38</span></div><div class="card-b">
      <svg width="100%" height="44" viewBox="0 0 640 44" preserveAspectRatio="none" aria-label="Lease timeline: 2h 40m used, 5h 20m left of 8h">
        <rect x="0" y="12" width="640" height="12" rx="3" fill="var(--ink)" stroke="var(--line-2)"/>
        <rect x="0" y="12" width="213" height="12" rx="3" fill="var(--calm)"/>
      </svg>
      <div style="display:flex;justify-content:space-between" class="stale"><span>created 11:38</span><span>now 14:18 · 5h 20m left</span><span>19:38</span></div>
    </div></div>
    <div style="display:flex;gap:8px;margin-top:14px"><button class="btn primary">${ic('terminal')} Open browser console</button><button class="btn">${ic('clock')} Extend lease</button><button class="btn">${ic('power')} Shut down</button><button class="btn danger" id="delChild">${ic('trash')} Delete…</button></div></div>`;
}
function inspectorChild() {
  return `<div class="insp"><div class="eyebrow">Created by</div><div class="insp-head">${avatar('claude')}<div><h3 style="font-size:var(--fs-13)">Claude Code</h3><div class="stale">construct vm create · 11:38</div></div></div>
    <div class="stale">Children are disposable test boxes. Deleting one never touches agent-vm.</div></div>`;
}

function bindMission(body, inst) {
  const stage = $('#stage');
  body.querySelectorAll('[data-inst]').forEach((b) => b.addEventListener('click', () => { PANEL.inst = b.dataset.inst; renderPanelSurface(stage); refreshScenario(); }));
  body.querySelectorAll('[data-lane]').forEach((l) => {
    const pick = () => { PANEL.agent = l.dataset.lane; $$('[data-lane]').forEach((x) => x.setAttribute('aria-selected', x === l)); $('#mRight').innerHTML = inspector(PANEL.agent); bindInspector(); };
    l.addEventListener('click', pick);
    l.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); } });
  });
  body.querySelectorAll('[data-feed]').forEach((c) => c.addEventListener('click', () => { PANEL.feed = c.dataset.feed; $$('[data-feed]').forEach((x) => x.setAttribute('aria-pressed', x === c)); $('#feed').innerHTML = feedRows(); }));
  const mic = $('#micSw', body); if (mic) mic.addEventListener('click', () => { POP.mic = !POP.mic; renderMission(body); });
  body.querySelectorAll('[data-power]').forEach((b) => b.addEventListener('click', () => {
    const k = b.dataset.power;
    if (k === 'stop') confirmDanger($('#pwin'), { title: 'Stop agent-vm?', sub: 'Claude Code is mid-turn on construct. Stopping ends that turn.', name: 'agent-vm', action: 'Stop VM',
      impact: [{ icon: 'warn', cls: 'warn', k: 'Claude Code turn is interrupted', v: '"Refactoring forwarder retry backoff" · uncommitted changes stay on disk' }, { icon: 'fwd', k: '2 open forwards close', v: ':5173 vite dev server, :8080→18800 api preview' }, { icon: 'shield', k: 'One UAC prompt', v: 'Hyper-V needs elevation on this PC' }],
      onConfirm: () => toast($('#pwin'), { title: 'Stopping agent-vm', msg: 'Approve the Windows UAC prompt to continue.', icon: 'shield', cls: 'warn' }) });
    else toast($('#pwin'), { title: k === 'save' ? 'Saving agent-vm' : 'Restarting agent-vm', msg: 'Approve the Windows UAC prompt. Claude\'s turn will pause.', icon: 'shield', cls: 'warn' });
  }));
  const rpA = $('#rpAfter', body); if (rpA) rpA.addEventListener('click', () => { rpA.closest('.behind-box').innerHTML = `<div style="display:flex;gap:6px;align-items:center;color:var(--warn);font-weight:600">${ic('clock')} Reprovision queued</div><div>Runs when Claude Code goes idle. Keeps all data.</div><button class="btn sm ghost" style="justify-self:start">Cancel</button>`; });
  const rpN = $('#rpNowP', body); if (rpN) rpN.addEventListener('click', () => toast($('#pwin'), { title: 'Reprovisioning agent-vm', msg: 'Step 2/5 · checking SSH and the saved root key.', icon: 'refresh' }));
  ['#resumeL', '#resumeC'].forEach((s) => { const r = $(s, body); if (r) r.addEventListener('click', () => toast($('#pwin'), { title: 'Resuming dev-2', msg: 'buildbox is restoring 12 GB of RAM. About 10 s.', icon: 'play', cls: 'live' })); });
  body.querySelectorAll('[data-go-set]').forEach((b) => b.addEventListener('click', () => { PANEL.tab = 'settings'; PANEL.setSec = 'idle'; renderPanelSurface(stage); }));
  const dc = $('#delChild', body); if (dc) dc.addEventListener('click', () => confirmDanger($('#pwin'), { title: 'Delete child VM win-test?', sub: 'The Windows 11 eval guest and its disk are removed from agent-vm.', name: 'win-test', action: 'Delete child VM',
    impact: [{ icon: 'trash', cls: 'crit', k: 'Disk deleted', v: '38 GB VHDX, no backup' }, { icon: 'key', k: 'Evaluation licence released', v: 'no retail or MAK key involved' }], onConfirm: () => toast($('#pwin'), { title: 'win-test deleted', msg: 'Child removed. agent-vm untouched.', icon: 'ok' }) }));
  const canvas = $('#rainCanvas', body); if (canvas) codeRain(canvas);
  bindInspector();
}
function bindInspector() {
  const b = $('#inspBell'); if (b) b.addEventListener('click', () => { PANEL.bells[PANEL.agent] = !PANEL.bells[PANEL.agent]; $('#mRight').innerHTML = inspector(PANEL.agent); bindInspector(); });
  const u = $('#agentUpd'); if (u) u.addEventListener('click', () => { u.textContent = 'Queued after turn'; u.disabled = true; });
}

/* ── Projects ───────────────────────────────────────────────────────── */
const PROJECTS = {
  construct: { repos: [['github.com/permissionBRICK/construct', 'main'], ['github.com/permissionBRICK/construct-ui-mockups', 'main']], sdks: ['dotnet 9.0', 'node 22 LTS', 'pwsh 7.5'], pkgs: 'jq shellcheck', mcp: ['omniloop (stdio)', 'github (http)'], cmds: 'cd ~/repos/construct/extension && npm install\ncd ~/repos/construct/companion && dotnet restore' },
  omniloop: { repos: [['github.com/permissionBRICK/omniloop', 'main']], sdks: ['node 22 LTS'], pkgs: '', mcp: [], cmds: 'cd ~/repos/omniloop && npm install' },
  gitgudlab: { repos: [['gitlab.example.local/tools/gitgudlab', 'main']], sdks: ['python 3.13'], pkgs: 'libpq-dev', mcp: ['gitlab (http)'], cmds: 'uv sync' },
  'emili-simulator': { repos: [['github.com/permissionBRICK/emili-simulator', 'dev']], sdks: ['dotnet 9.0'], pkgs: '', mcp: [], cmds: '' },
  jarvis: { repos: [['github.com/permissionBRICK/jarvis', 'main']], sdks: ['node 22 LTS'], pkgs: '', mcp: [], cmds: 'npm install', isNew: true },
};
function renderProjects(body) {
  const p = PROJECTS[PANEL.project];
  body.innerHTML = `<div class="projects">
    <aside class="proj-list">
      <div class="col-h" style="padding-top:0"><span class="eyebrow">Profiles · next reprovision</span><button class="icon-btn right" aria-label="Add project" data-tip="Add project">${ic('plus')}</button></div>
      ${Object.keys(PROJECTS).map((k) => `<div class="proj ${PANEL.projSel[k] ? '' : 'deselected'}" data-proj="${k}" aria-current="${k === PANEL.project}" tabindex="0">
        <input type="checkbox" ${PANEL.projSel[k] ? 'checked' : ''} data-sel="${k}" aria-label="Include ${k}" style="accent-color:var(--calm)">
        <span class="n">${k}</span>${PROJECTS[k].isNew ? '<span class="tag calm">new</span>' : PANEL.projSel[k] ? '' : '<span class="tag">off</span>'}
        <span class="d">${PROJECTS[k].repos.length} repo${PROJECTS[k].repos.length > 1 ? 's' : ''} · ${PROJECTS[k].sdks.join(', ')}</span></div>`).join('')}
      <div style="padding:14px;display:grid;gap:10px">
        <div class="card" style="padding:10px 12px;display:grid;gap:6px">
          <div style="display:flex;align-items:center;gap:6px"><span class="t1" style="font-weight:600;font-size:var(--fs-12)">Config sync</span><span class="tag calm right" style="margin-left:auto">synced 3 min ago</span></div>
          <div style="font-size:var(--fs-11);color:var(--t3)">1 new profile from the VM (<span class="mono">jarvis</span>, auto-discovered).</div>
          <div style="display:flex;gap:6px"><button class="btn sm">${ic('sync')} Sync now</button><button class="btn sm ghost">${ic('folder')} Open config repo</button></div>
        </div>
        <div class="card" style="padding:10px 12px;display:grid;gap:6px">
          <span class="t1" style="font-weight:600;font-size:var(--fs-12)">Remote config repos</span>
          <div class="mono" style="font-size:var(--fs-10);color:var(--t2)">origin · github.com/permissionBRICK/construct-config</div>
          <div class="stale">pushed 3 min ago · no conflicts</div>
          <div style="display:flex;gap:4px;flex-wrap:wrap"><button class="btn xs">+ Add remote</button><button class="btn xs ghost">Import…</button><button class="btn xs ghost">Share…</button><button class="btn xs ghost">${ic('download')} Export config</button></div>
        </div>
      </div>
    </aside>
    <section class="editor" id="projEditor">
      <div class="section-title"><h2>${PANEL.project}</h2><span class="tag ${PANEL.projSel[PANEL.project] ? 'calm' : ''}">${PANEL.projSel[PANEL.project] ? 'included in next reprovision' : 'not included'}</span>${p.isNew ? '<span class="tag calm">auto-discovered on the VM</span>' : ''}</div>
      <p class="t3" style="margin:-4px 0 16px;font-size:var(--fs-12)">Profiles describe what a fresh VM needs for this project. Changes apply on the next reprovision.</p>
      <div class="editor-grid">
        <div class="fs" style="grid-column:1/-1"><h4 class="eyebrow">Repositories</h4>
          <div class="list-rows">${p.repos.map(([u, b]) => `<div class="lr"><span class="mono">${u}</span><span style="display:flex;gap:8px;align-items:center"><span class="tag">${b}</span><button class="icon-btn" aria-label="Remove repo" style="width:22px;height:22px">${ic('x')}</button></span></div>`).join('')}
          <div class="lr"><input class="input mono" placeholder="https://… or git@…" data-dirty style="height:26px"><button class="btn xs">Add</button></div></div></div>
        <div class="fs"><h4 class="eyebrow">Runtimes (sdks)</h4><div class="list-rows">${p.sdks.map((s) => `<div class="lr"><span class="mono">${s}</span><button class="icon-btn" style="width:22px;height:22px" aria-label="Remove">${ic('x')}</button></div>`).join('')}<div class="lr"><select class="input" data-dirty style="height:26px"><option>+ add runtime…</option><option>go 1.24</option><option>rust stable</option><option>java 21</option></select><span></span></div></div></div>
        <div class="fs"><h4 class="eyebrow">MCP servers</h4><div class="list-rows">${(p.mcp.length ? p.mcp : ['— none —']).map((s) => `<div class="lr"><span class="mono">${s}</span>${s.startsWith('—') ? '<span></span>' : `<button class="btn xs ghost">Edit</button>`}</div>`).join('')}<div class="lr"><span class="t3" style="font-size:var(--fs-11)">stdio or http server</span><button class="btn xs">+ Add</button></div></div></div>
        <div class="field"><label>Host packages (apt)</label><input class="input mono" value="${p.pkgs}" data-dirty></div>
        <div class="field"><label>Git identity override</label><input class="input" placeholder="use the global identity" data-dirty></div>
        <div class="field" style="grid-column:1/-1"><label>Provision commands · run on every reprovision, keep them incremental</label><textarea class="input" rows="4" data-dirty>${p.cmds}</textarea></div>
      </div>
      <div class="savebar ${PANEL.projDirty ? '' : 'hidden'}" id="projSave"><span class="dotw"></span>Unsaved profile changes · apply on next reprovision<button class="btn sm ghost" id="projDiscard">Discard</button><button class="btn sm primary" id="projSaveBtn">Save profile</button></div>
    </section></div>`;
  body.querySelectorAll('[data-proj]').forEach((r) => r.addEventListener('click', (e) => { if (e.target.matches('input')) return; PANEL.project = r.dataset.proj; PANEL.projDirty = false; renderProjects(body); }));
  body.querySelectorAll('[data-sel]').forEach((c) => c.addEventListener('change', () => { PANEL.projSel[c.dataset.sel] = c.checked; renderProjects(body); }));
  body.querySelectorAll('[data-dirty]').forEach((i) => i.addEventListener('input', () => { PANEL.projDirty = true; i.classList.add('changed'); $('#projSave').classList.remove('hidden'); }));
  $('#projDiscard').addEventListener('click', () => { PANEL.projDirty = false; renderProjects(body); });
  $('#projSaveBtn').addEventListener('click', () => { PANEL.projDirty = false; renderProjects(body); toast($('#pwin'), { title: 'Profile saved', msg: 'Committed to the config repo. Applies on the next reprovision.', icon: 'ok' }); });
}

/* ── Usage ──────────────────────────────────────────────────────────── */
function renderUsage(body) {
  const periods = { today: { claude: 3.40, codex: 0.58, opencode: 0.14, tok: ['2.91M', '0.64M', '0.19M'] }, month: { claude: 71.20, codex: 12.40, opencode: 2.70, tok: ['61.2M', '13.8M', '3.1M'] }, all: { claude: 338.90, codex: 61.30, opencode: 12.57, tok: ['290M', '66.1M', '14.0M'] } };
  const P = periods[PANEL.usagePeriod];
  const tot = P.claude + P.codex + P.opencode;
  body.innerHTML = `<div class="view"><div class="pad">
    <div class="section-title"><h2>Usage &amp; cost · agent-vm</h2><p>ccusage estimates: token counts are exact, cost uses model list prices.</p>
      <div style="margin-left:auto;display:flex;gap:6px"><button class="btn sm ghost">${ic('download')} Export JSON</button></div></div>
    <div class="tiles">
      <div class="tile"><div class="l">Today</div><div class="v">$4.12</div><div class="s">3 agents · 3.74M tokens</div></div>
      <div class="tile"><div class="l">This month</div><div class="v">$86.30</div><div class="s">23 days · avg $3.75/day</div></div>
      <div class="tile"><div class="l">All time</div><div class="v">$412.77</div><div class="s">since 12 Jun 2026</div></div>
      <div class="tile"><div class="l">Last 7 days</div><div class="v">$66.42</div><div class="s">peak Sun 20 · $14.00</div></div>
    </div>
    <div class="card chart-card">
      <div class="card-h"><h3>Daily cost by agent · last 7 days</h3>
        <div class="legend" style="margin-left:14px">${['claude', 'codex', 'opencode'].map((k) => `<span><svg width="10" height="10"><rect width="10" height="10" rx="2" fill="${AGENTS[k].hex}"/></svg>${AGENTS[k].name}</span>`).join('')}</div>
        <div class="right"><button class="chip" data-uv="chart" aria-pressed="${PANEL.usageView === 'chart'}">${ic('chart')} Chart</button><button class="chip" data-uv="table" aria-pressed="${PANEL.usageView === 'table'}">${ic('table')} Table</button></div></div>
      <div class="card-b chart" id="usageChart">${PANEL.usageView === 'chart' ? '' : usageTable()}</div>
    </div>
    <div class="card" style="margin-top:14px">
      <div class="card-h"><h3>By agent</h3>
        <div class="right">${[['today', 'Today'], ['month', 'This month'], ['all', 'All time']].map(([k, l]) => `<button class="chip" data-up="${k}" aria-pressed="${PANEL.usagePeriod === k}">${l}</button>`).join('')}</div></div>
      <table class="tbl"><thead><tr><th>Agent</th><th style="width:40%">Share of cost</th><th class="r">Tokens</th><th class="r">Cost</th></tr></thead><tbody>
      ${['claude', 'codex', 'opencode'].map((k, i) => `<tr><td><span style="display:inline-flex;gap:8px;align-items:center"><span style="width:9px;height:9px;border-radius:2px;background:${AGENTS[k].hex}"></span><span class="t1">${AGENTS[k].name}</span></span></td>
        <td><div style="display:flex;align-items:center;gap:8px"><div class="meter" style="flex:1;height:8px"><i style="width:${(P[k] / tot) * 100}%;background:${AGENTS[k].hex}"></i></div><span class="num t3" style="width:36px;text-align:right;font-size:var(--fs-11)">${Math.round((P[k] / tot) * 100)}%</span></div></td>
        <td class="r num">${P.tok[i]}</td><td class="r num t1">${money(P[k])}</td></tr>`).join('')}
      <tr><td class="t3">T3 Code</td><td class="t4" style="font-size:var(--fs-11)">drives the agents above; no own spend</td><td class="r num t4">—</td><td class="r num t4">—</td></tr>
      <tr><td class="t1" style="font-weight:600">Total</td><td></td><td class="r"></td><td class="r num t1" style="font-weight:600">${money(tot)}</td></tr>
      </tbody></table>
    </div>
  </div></div>`;
  body.querySelectorAll('[data-uv]').forEach((c) => c.addEventListener('click', () => { PANEL.usageView = c.dataset.uv; renderUsage(body); }));
  body.querySelectorAll('[data-up]').forEach((c) => c.addEventListener('click', () => { PANEL.usagePeriod = c.dataset.up; renderUsage(body); }));
  if (PANEL.usageView === 'chart') drawUsageChart();
}
function usageTable() {
  return `<table class="tbl"><thead><tr><th>Day</th>${['claude', 'codex', 'opencode'].map((k) => `<th class="r">${AGENTS[k].name}</th>`).join('')}<th class="r">Total</th></tr></thead><tbody>
    ${USAGE_7D.map((d) => `<tr><td>${d.d}${d.today ? ' <span class="tag">so far</span>' : ''}</td><td class="r num">${money(d.claude)}</td><td class="r num">${money(d.codex)}</td><td class="r num">${money(d.opencode)}</td><td class="r num t1">${money(d.claude + d.codex + d.opencode)}</td></tr>`).join('')}</tbody></table>`;
}
function drawUsageChart() {
  const host = $('#usageChart');
  const W = host.clientWidth - 28, H = 250, L = 40, B = 26, T = 22;
  const max = 15;
  const Y = (v) => T + (H - T - B) * (1 - v / max);
  const n = USAGE_7D.length, band = (W - L) / n, bw = Math.min(56, band * 0.5);
  let s = `<svg width="${W}" height="${H}" role="img" aria-label="Stacked bar chart of daily cost by agent over the last 7 days">`;
  [0, 5, 10, 15].forEach((v) => { s += `<line class="${v === 0 ? 'base' : 'gridl'}" x1="${L}" x2="${W}" y1="${Y(v)}" y2="${Y(v)}"/><text x="${L - 8}" y="${Y(v) + 3.5}" text-anchor="end">$${v}</text>`; });
  USAGE_7D.forEach((d, i) => {
    const cx = L + band * i + band / 2, x = cx - bw / 2;
    const total = d.claude + d.codex + d.opencode;
    let acc = 0;
    s += `<g class="bargroup" data-tip="<b>${d.d}${d.today ? ' · so far' : ''}</b><br>${['claude', 'codex', 'opencode'].map((k) => `<span style='color:${AGENTS[k].hex}'>■</span> ${AGENTS[k].name} <span class='num'>${money(d[k])}</span>`).join('<br>')}<br>Total <span class='num'>${money(total)}</span>">`;
    s += `<rect x="${cx - band / 2}" y="${T}" width="${band}" height="${H - T - B}" fill="transparent"/>`;
    ['claude', 'codex', 'opencode'].forEach((k, j) => {
      const h = Y(acc) - Y(acc + d[k]);
      const top = j === 2;
      const y = Y(acc + d[k]);
      const gap = acc > 0 ? 2 : 0;
      s += `<rect class="seg" x="${x}" y="${y}" width="${bw}" height="${Math.max(0, h - gap)}" rx="${top ? 3 : 0}" fill="${AGENTS[k].hex}" ${d.today ? 'opacity=".85"' : ''}/>`;
      acc += d[k];
    });
    s += `<text class="val" x="${cx}" y="${Y(total) - 6}" text-anchor="middle">${money(total)}</text>`;
    s += `<text x="${cx}" y="${H - 8}" text-anchor="middle" ${d.today ? 'style="fill:var(--t1)"' : ''}>${d.d}${d.today ? '*' : ''}</text></g>`;
  });
  s += '</svg>';
  host.innerHTML = s + '<div class="stale" style="margin-top:4px">* today so far, sampled 20 s ago. Hover a day for the split.</div>';
  host.classList.add('dim-others');
}

/* ── Settings ───────────────────────────────────────────────────────── */
const WHEN = {
  live: '<span class="tag calm" data-tip="Takes effect immediately">live</span>',
  restart: '<span class="tag info" data-tip="Saves, then restarts the VM to apply">restart</span>',
  reprov: '<span class="tag warn" data-tip="Recorded now, applied on the next reprovision">next reprovision</span>',
  reinst: '<span class="tag crit" data-tip="Only applies when the VM is rebuilt">reinstall</span>',
};
const SET_SECS = [
  ['general', 'Companion & UI'],
  ['notify', 'Notifications'],
  ['idle', 'Idle policy'],
  ['resources', 'VM resources'],
  ['access', 'Access & services'],
  ['agents', 'Coding agents'],
  ['identity', 'Identity & credentials'],
];
function srow(label, desc, when, ctl) {
  return `<div class="set-row"><div><div class="l">${label} ${when}</div>${desc ? `<div class="d">${desc}</div>` : ''}</div><div class="ctl">${ctl}</div></div>`;
}
const sw = (on, id = '') => `<button class="switch" role="switch" aria-checked="${on}" data-sw ${id ? `id="${id}"` : ''}></button>`;
function renderSettings(body) {
  body.innerHTML = `<div class="settings">
    <nav class="set-nav">${SET_SECS.map(([id, l]) => `<button data-sec="${id}" aria-current="${PANEL.setSec === id}">${l}</button>`).join('')}
      <div class="grp eyebrow">Elsewhere</div>
      <button data-jump="projects">${ic('folder')} Projects</button><button data-jump="danger" style="color:#d08b8b">${ic('warn')} Danger zone</button></nav>
    <div class="set-body" id="setBody">
      <div class="when-legend"><span>Every setting says when it applies:</span>${WHEN.live}${WHEN.restart}${WHEN.reprov}${WHEN.reinst}</div>
      <section class="set-sec" id="sec-general"><header><h3>Companion &amp; UI</h3></header><div class="set-rows">
        ${srow('Start with Windows', 'Tray icon appears at sign-in', WHEN.live, sw(true))}
        ${srow('Global hotkey', 'Opens the popup from any app', WHEN.live, '<span class="kbd">Win</span>+<span class="kbd">Alt</span>+<span class="kbd">C</span> <button class="btn xs ghost">Change</button>')}
        ${srow('Popup opens on', null, WHEN.live, '<select class="input" style="width:170px" data-dirty><option>Agents (default)</option><option>Forwards</option><option>Inbox</option></select>')}
        ${srow('Default “Open” target', 'The primary button in the popup', WHEN.live, '<select class="input" style="width:170px" data-dirty><option>VS Code (Remote-SSH)</option><option>T3 Code</option><option>Browser console</option></select>')}
        ${srow('Reduce motion', 'Turns off the working pulse and code rain', WHEN.live, sw(false))}
      </div></section>
      <section class="set-sec" id="sec-notify"><header><h3>Notifications</h3></header><div class="set-rows">
        ${srow('Show construct notify as Windows toasts', 'They are kept in the inbox either way', WHEN.live, '<select class="input" style="width:170px" data-dirty><option>info and above</option><option>errors only</option><option>never</option></select>')}
        ${srow('Arm “wake me” for new Claude turns', 'Bell turns on by itself when a turn starts', WHEN.live, sw(false))}
        ${srow('Keep inbox for', null, WHEN.live, '<div class="input-unit"><input class="input num" style="width:64px" value="14" data-dirty><span>days</span></div>')}
      </div></section>
      <section class="set-sec" id="sec-idle"><header><h3>Idle policy</h3><span class="tag">remote instances</span></header>
        <p>The host service enforces this, so it works with this PC switched off. A VM stays up while an agent is working, even with nobody connected. agent-vm is local and has no idle policy.</p><div class="set-rows">
        ${srow('dev-2 · when idle for', 'buildbox allows 15–240 min', WHEN.live, '<div class="input-unit"><input class="input num" style="width:64px" value="60" data-dirty><span>min</span></div><select class="input" style="width:150px" data-dirty><option>save (RAM → disk)</option><option>shut down</option><option>never</option></select>')}
      </div></section>
      <section class="set-sec" id="sec-resources"><header><h3>VM resources · agent-vm</h3></header><div class="set-rows">
        ${srow('Memory', 'currently 16 GB · 11.2 GB in use · PC-1 has 64 GB', WHEN.restart, '<div class="input-unit"><input class="input num" style="width:64px" value="16" data-dirty><span>GB</span></div>')}
        ${srow('vCPUs', 'empty = all host cores', WHEN.restart, '<div class="input-unit"><input class="input num" style="width:64px" value="8" data-dirty><span>cores</span></div>')}
        ${srow('Disk size', 'grows on demand; resize means rebuild', WHEN.reinst, '<div class="input-unit"><input class="input num" style="width:64px" value="146" data-dirty><span>GB</span></div>')}
        ${srow('Automatic checkpoints', 'Off by default: on a disposable VM they only grow the disk', WHEN.live, sw(false))}
        ${srow('Ubuntu release for redownload', null, WHEN.reinst, '<select class="input" style="width:170px" data-dirty><option>26.04 LTS (current)</option><option>24.04.3 LTS</option></select>')}
      </div></section>
      <section class="set-sec" id="sec-access"><header><h3>Access &amp; services</h3></header><div class="set-rows">
        ${srow('VS Code serve-web', 'browser IDE, token-gated port 8000', WHEN.reprov, sw(false))}
        ${srow('VS Code tunnel', 'vscode.dev, no inbound port', WHEN.reprov, sw(false))}
        ${srow('SMB workspace share', 'mapped to drive Z:', WHEN.reprov, sw(true))}
        ${srow('Microphone passthrough', 'Arm/disarm from the popup any time; this installs the tunnel', WHEN.reprov, sw(true))}
        ${srow('Claude Code live streaming', 'Stream thinking and replies over Remote-SSH', WHEN.reprov, sw(true))}
        ${srow('OpenCode background watcher', 'adds background, background_output and background_kill tools', WHEN.reprov, sw(false))}
        ${srow('T3 Code web GUI', 'installs now and opens :5177 · settings survive a reinstall', WHEN.live, '<select class="input" style="width:110px" data-dirty><option>stable</option><option>nightly</option></select>' + sw(true))}
        ${srow('Build patched T3 Code + Desktop', 'voice input, usage-limit recovery, OpenCode monitoring', WHEN.reprov, sw(false))}
      </div></section>
      <section class="set-sec" id="sec-agents"><header><h3>Coding agents</h3></header><div class="set-rows">
        ${srow('Update agents automatically', 'Only between turns, never during one', WHEN.live, sw(false))}
        ${srow('Installed agents', 'Claude Code · Codex · OpenCode · T3 Code', WHEN.reprov, '<button class="btn sm">Choose…</button>')}
      </div></section>
      <section class="set-sec" id="sec-identity"><header><h3>Identity &amp; credentials</h3></header><div class="set-rows">
        ${srow('Git user name', null, WHEN.reprov, '<input class="input" style="width:220px" value="permissionBRICK" data-dirty>')}
        ${srow('Git email', null, WHEN.reprov, '<input class="input" style="width:220px" value="dana@example.com" data-dirty>')}
        ${srow('Store git credentials on the VM', '<span class="warn">Plaintext in ~/.git-credentials, readable by agents</span>', WHEN.reprov, sw(true))}
        ${srow('Agent login password', 'fallback console login only; normal access is root over SSH', WHEN.reinst, '<button class="btn sm ghost">Set…</button>')}
      </div></section>
      <div class="savebar hidden" id="setSave"><span class="dotw"></span><span id="setSaveTxt">Unsaved changes</span><button class="btn sm ghost" id="setDiscard">Discard</button><button class="btn sm primary" id="setSaveBtn">Save</button></div>
    </div></div>`;
  const sb = $('#setBody');
  const scrollTo = (id) => { const el = $('#sec-' + id); if (el) sb.scrollTop = el.offsetTop - 10; };
  body.querySelectorAll('[data-sec]').forEach((b) => b.addEventListener('click', () => { PANEL.setSec = b.dataset.sec; $$('[data-sec]').forEach((x) => x.setAttribute('aria-current', x === b)); scrollTo(b.dataset.sec); }));
  body.querySelectorAll('[data-jump]').forEach((b) => b.addEventListener('click', () => { PANEL.tab = b.dataset.jump; renderPanelSurface($('#stage')); }));
  const markDirty = (el) => {
    const when = el.closest('.set-row').querySelector('.tag').textContent;
    PANEL.dirty.add(when);
    const parts = [];
    if (PANEL.dirty.has('live')) parts.push('some apply now');
    if (PANEL.dirty.has('restart')) parts.push('RAM/vCPU need a restart');
    if (PANEL.dirty.has('next reprovision')) parts.push('some wait for reprovision');
    if (PANEL.dirty.has('reinstall')) parts.push('disk waits for a reinstall');
    $('#setSaveTxt').textContent = 'Unsaved · ' + parts.join(' · ');
    $('#setSave').classList.remove('hidden');
    $('#setSaveBtn').textContent = PANEL.dirty.has('restart') ? 'Save & restart VM' : 'Save';
  };
  body.querySelectorAll('[data-sw]').forEach((s) => s.addEventListener('click', () => { s.setAttribute('aria-checked', s.getAttribute('aria-checked') !== 'true'); markDirty(s); }));
  body.querySelectorAll('[data-dirty]').forEach((i) => i.addEventListener('input', () => { i.classList.add('changed'); markDirty(i); }));
  $('#setDiscard').addEventListener('click', () => { PANEL.dirty.clear(); renderSettings(body); });
  $('#setSaveBtn').addEventListener('click', () => { const r = PANEL.dirty.has('restart'); PANEL.dirty.clear(); renderSettings(body); toast($('#pwin'), { title: 'Settings saved', msg: r ? 'agent-vm restarts to resize (one UAC prompt). Claude\'s turn pauses.' : 'Live settings applied. The rest is recorded for the next reprovision.', icon: 'ok' }); });
  setTimeout(() => scrollTo(PANEL.setSec), 0);
}

/* ── Danger zone ────────────────────────────────────────────────────── */
function renderDanger(body) {
  body.innerHTML = `<div class="view"><div class="pad">
    <div class="section-title"><h2>Danger zone · agent-vm</h2><p>Each action shows what it destroys and asks you to type the instance name. None of these is reachable from the tray.</p></div>
    <div class="dz">
      <div class="dz-item"><div><h4>Reinstall</h4><p>Rebuild the VM from the current Ubuntu ISO. Saves and restores agent config (auth, git creds, profiles, memory). Repos without a remote are lost.</p></div><button class="btn danger" data-dz="reinstall">Reinstall…</button></div>
      <div class="dz-item"><div><h4>Redownload</h4><p>Download a fresh Ubuntu 26.04 LTS ISO (about 3 GB), then reinstall as above.</p></div><button class="btn danger" data-dz="redownload">Redownload…</button></div>
      <div class="dz-item"><div style="display:grid;gap:8px"><h4>Custom reinstall <span class="tag">one-time</span></h4><p>Pick the backup behaviour for this one run. The regular Reinstall always saves and restores.</p>
        <div class="radio-cards" style="max-width:560px">
          <label class="radio-card"><input type="radio" name="cr" checked><b>Save &amp; restore</b><span>Back up now, restore onto the fresh VM</span></label>
          <label class="radio-card"><input type="radio" name="cr"><b>Restore an existing backup</b><span>using: backup-2026-09-21-1904 (agent-vm, 1.2 GB)</span></label>
          <label class="radio-card"><input type="radio" name="cr"><b>Clean wipe</b><span class="crit">No backup, no restore. Auth, settings and history are gone.</span></label>
        </div></div><button class="btn danger" data-dz="custom" style="align-self:end">Run custom reinstall…</button></div>
      <div class="dz-item"><div><h4>Remove instance</h4><p>Forget agent-vm on this PC and delete the Hyper-V VM and its disks. win-test (child) goes with it.</p></div><button class="btn danger" data-dz="remove">Remove instance…</button></div>
      <div class="eyebrow" style="margin-top:10px">Rare</div>
      <div class="dz-item rare"><div><h4>Share this PC as a Construct host</h4><p>Install the host service, adopt agent-vm with its data and settings, and become the host administrator. Other people can then run VMs here.</p></div><button class="btn" data-dz="host">${ic('host')} Make this PC a host…</button></div>
    </div></div></div>`;
  body.querySelectorAll('[data-dz]').forEach((b) => b.addEventListener('click', () => dangerAction(b.dataset.dz)));
}
function dangerAction(kind) {
  const win = $('#pwin');
  const common = [
    { icon: 'warn', cls: 'warn', k: 'Claude Code is mid-turn', v: 'The turn is killed. 4 uncommitted files in construct are backed up with the repo.', r: 'busy', rc: 'warn' },
    { icon: 'folder', k: '2 repos with unpushed commits', v: 'construct (fix/forwarder-retry, 3 ahead) · omniloop (main, 1 ahead)', r: 'push first?', rc: 'warn' },
    { icon: 'shield', cls: 'calm', k: 'Backup: auth, git creds, profiles, instructions, memory', v: 'restored onto the fresh VM', r: 'kept', rc: 'calm' },
    { icon: 'fwd', k: '2 forwards close, mic tunnel reconnects after install' },
  ];
  if (kind === 'reinstall' || kind === 'custom') confirmDanger(win, { title: 'Reinstall agent-vm?', sub: 'Deletes the VM and builds a fresh one from the current ISO. About 10 minutes.', name: 'agent-vm', action: 'Reinstall agent-vm', impact: [...common, { icon: 'trash', cls: 'crit', k: 'Everything else on the VM disk', v: 'caches, build outputs, repos without a remote', r: 'deleted', rc: 'crit' }], onConfirm: () => toast(win, { title: 'Reinstall started', msg: 'Step 1/8 · saving config backup. Follow progress in the installer window.', icon: 'refresh' }) });
  if (kind === 'redownload') confirmDanger(win, { title: 'Redownload and reinstall agent-vm?', sub: 'Fetches Ubuntu 26.04 LTS (~3 GB), then reinstalls.', name: 'agent-vm', action: 'Redownload & reinstall', impact: [{ icon: 'download', k: 'Downloads ~3.1 GB', v: 'SHA256-verified; the old ISO is replaced' }, ...common], onConfirm: () => toast(win, { title: 'Redownload started', msg: 'Downloading ISO · 0 of 3.1 GB', icon: 'download' }) });
  if (kind === 'remove') confirmDanger(win, { title: 'Remove instance agent-vm?', sub: 'Forget it on this PC and delete the VM. This cannot be undone.', name: 'agent-vm', action: 'Remove instance',
    impact: [{ icon: 'trash', cls: 'crit', k: 'Hyper-V VM agent-vm and its 146 GB disk', r: 'deleted', rc: 'crit' }, { icon: 'child', cls: 'crit', k: 'Child VM win-test (Windows 11 eval)', r: 'deleted', rc: 'crit' }, common[1], { icon: 'key', k: 'Registry entry, SSH host key, VS Code remote entry', r: 'removed' }, { icon: 'shield', cls: 'calm', k: 'Config repo and backups in %LOCALAPPDATA%\\Construct', r: 'kept', rc: 'calm' }],
    onConfirm: () => toast(win, { title: 'agent-vm removed', msg: 'Switched to dev-2.', icon: 'ok' }) });
  if (kind === 'host') openDialog(win, `<header><div class="danger-ic" style="background:var(--calm-bg);color:var(--calm);border-color:var(--line-3)">${ic('host')}</div><div><h2>Make PC-1 a Construct host</h2><p>Other people get VMs here within the allowances you set.</p></div></header>
      <div class="dbody"><div class="impact">
        <div class="row"><span class="t3">${ic('download')}</span><div><div class="k">Installs constructd 1.15.0 as a Windows service</div><div class="v">listens on :7462 · TLS with a self-signed pin</div></div><span></span></div>
        <div class="row"><span class="t3">${ic('box')}</span><div><div class="k">Adopts agent-vm with its data</div><div class="v">it becomes dana/agent-vm; no rebuild</div></div><span></span></div>
        <div class="row"><span class="t3">${ic('user')}</span><div><div class="k">You become the first administrator</div></div><span></span></div>
      </div></div><footer><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close>Continue in setup wizard</button></footer>`);
}
