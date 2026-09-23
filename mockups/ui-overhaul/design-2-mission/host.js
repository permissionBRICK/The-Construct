/* Host panel: Roster (people first) + host-wide tabs. */
'use strict';

const HOST = { tab: 'roster', person: null, sort: 'cost', jobResolved: false, vmFilter: 'all', dirty: 0, tokenShown: false, updateStaged: false };

const HOST_TABS = [['roster', 'Roster'], ['capacity', 'Capacity'], ['jobs', 'Jobs & audit'], ['media', 'Media & licences'], ['service', 'Service'], ['config', 'Config']];

const DEFAULTS = { dev: { prim: 2, child: 2, vcpu: 8, ram: 24 }, contractor: { prim: 1, child: 1, vcpu: 4, ram: 16 }, admin: { prim: 2, child: 2, vcpu: 8, ram: 24 } };
const USERS = [
  { id: 'alice', role: 'dev', prim: [2, 3], child: [1, 4], vcpu: [12, 16], ram: [28, 32], cost: 212.40, prev: 188.10, flags: [],
    vms: [['work-vm', 'running', 16, 'busy'], ['bench', 'idle', 12, 'idle 2h'], ['win11-eval', 'creating', 12, 'child · creating 62%']] },
  { id: 'bob', role: 'dev', prim: [1, 2], child: [2, 2], vcpu: [8, 8], ram: [20, 24], cost: 54.10, prev: 71.30, flags: [['lease overdue', 'crit']],
    vms: [['api-vm', 'running', 12, 'running'], ['win-qa', 'overdue', 8, 'child · lease 2h over'], ['ci-child', 'off', 4, 'child · off']] },
  { id: 'dana', role: 'admin', prim: [2, null], child: [1, null], vcpu: [12, null], ram: [28, null], cost: 86.30, prev: 97.80, flags: [],
    vms: [['agent-vm-2', 'running', 16, 'running'], ['dev-2', 'saved', 12, 'saved · idle policy'], ['tpm-probe', 'running', 4, 'child']] },
  { id: 'mara', role: 'contractor', prim: [1, 1], child: [1, 1], vcpu: [4, 4], ram: [16, 16], cost: 139.90, prev: 102.40, flags: [['legacy credential', 'warn']],
    vms: [['scratch-3', 'pressure', 16, 'saved by memory pressure 13:48'], ['ui-snap', 'off', 4, 'child · off']] },
  { id: 'jonas', role: 'dev', prim: [0, 2], child: [0, 2], vcpu: [0, 8], ram: [0, 24], cost: 0, prev: 31.20, flags: [['disabled', '']], disabled: true, vms: [] },
];

function seeded(seed) { let s = seed; return () => { s = (s * 16807) % 2147483647; return (s - 1) / 2147483646; }; }
function dailySeries(u) {
  const r = seeded(u.id.charCodeAt(0) * 97 + u.id.length);
  const raw = Array.from({ length: 23 }, (_, i) => (u.disabled ? 0 : (0.4 + r()) * ((i % 7 === 5 || i % 7 === 6) ? 0.45 : 1)));
  const sum = raw.reduce((a, b) => a + b, 0) || 1;
  return raw.map((v) => (v / sum) * u.cost);
}

function hostScenario() {
  return [
    { label: 'Roster', active: HOST.tab === 'roster' && !HOST.person, run: () => { HOST.tab = 'roster'; HOST.person = null; } },
    { label: 'Edit alice (one save)', active: HOST.person === 'alice', run: () => { HOST.tab = 'roster'; HOST.person = 'alice'; } },
    { label: 'Onboard wizard', active: false, run: () => { HOST.tab = 'roster'; HOST.person = null; setTimeout(() => onboard(0), 60); } },
    { label: 'Offboard mara · cascade', active: false, run: () => { HOST.tab = 'roster'; HOST.person = null; setTimeout(() => offboard('mara', 1), 60); } },
    { label: 'Retry failed job → resolved', active: HOST.jobResolved, run: () => { HOST.tab = 'jobs'; HOST.jobResolved = true; } },
  ];
}

function stateDot(st) {
  return { running: 'running', idle: 'running', creating: 'creating', saved: 'saved', pressure: 'saved', overdue: 'crit', off: 'off' }[st] || 'off';
}

function renderHostSurface(stage, sub) {
  if (sub && HOST_TABS.some(([id]) => id === sub)) HOST.tab = sub;
  stage.innerHTML = `
  <div class="win" id="hwin" style="grid-template-rows:34px auto auto 1fr">
    <div class="titlebar">${GLYPH}<span class="title">buildbox — Host administration</span><span class="t4">· signed in as dana (admin)</span>
      <div class="wbtns"><span><svg viewBox="0 0 10 10"><path d="M1 5h8"/></svg></span><span><svg viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7"/></svg></span><span class="x"><svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1L1 9"/></svg></span></div>
    </div>
    <div class="appbar">
      <nav class="tabs" role="tablist">
        ${HOST_TABS.map(([id, l]) => `<button role="tab" data-htab="${id}" aria-selected="${HOST.tab === id}">${l}${id === 'jobs' && !HOST.jobResolved ? '<span class="count warn">1 failed</span>' : ''}${id === 'service' ? '<span class="count">1.15.0</span>' : ''}</button>`).join('')}
      </nav>
      <div class="right">
        <div style="position:relative"><input class="input search" placeholder="Find a person, VM, job…" style="width:220px;padding-left:28px"><span style="position:absolute;left:8px;top:7px" class="t3">${ic('search')}</span></div>
        <a class="btn sm ghost" href="#panel">${ic('panel')} My control panel</a>
      </div>
    </div>
    <div class="host-strip">
      <span class="it"><span class="dot running"></span><b>buildbox</b> constructd 1.14.2</span>
      <span class="it" data-tip="<b>Health</b><br>inventory incomplete for 1 VM<br>1 lease overdue (bob/win-qa, 2h)"><span class="dot warn"></span>Health: <b style="color:var(--warn)">2 issues</b></span>
      <span class="it">Capacity <b>enforce</b></span>
      <span class="it" data-tip="<b>Memory pressure elevated</b><br>Last pressure save 22 min ago: mara/scratch-3"><span class="dot warn"></span>Memory pressure <b style="color:var(--warn)">elevated</b></span>
      <span class="it ram"><span>RAM</span><div class="meter mini" style="width:140px"><i style="width:75%"></i></div><b>96</b>/128 GB · committed <b style="color:var(--warn)">142</b> (111%)</span>
      <span class="stale" style="margin-left:auto">sampled 12 s ago</span>
    </div>
    <div class="win-body" id="hbody"></div>
  </div>`;
  $$('[data-htab]').forEach((b) => b.addEventListener('click', () => { HOST.tab = b.dataset.htab; HOST.person = null; history.replaceState(null, '', '#host/' + HOST.tab); renderHostSurface(stage); refreshScenario(); }));
  const body = $('#hbody');
  ({ roster: renderRoster, capacity: renderCapacity, jobs: renderJobs, media: renderMedia, service: renderService, config: renderConfig })[HOST.tab](body);
}

/* ── Roster ─────────────────────────────────────────────────────────── */
function bullet(label, used, cap, def, unit = '') {
  if (cap == null) {
    return `<div class="bullet"><span>${label}</span><div class="bb unl" data-tip="<b>${label}</b>: ${used}${unit} used · no limit (admin)"><div class="used" style="width:${Math.min(100, (used / (def * 1.6)) * 100)}%"></div></div><span class="v">${used}${unit}/∞</span></div>`;
  }
  const max = Math.max(cap, def, used) * 1.12 || 1;
  const full = cap > 0 && used >= cap;
  return `<div class="bullet"><span>${label}</span>
    <div class="bb" data-tip="<b>${label}</b><br>used <span class='num'>${used}${unit}</span> · allowance <span class='num'>${cap}${unit}</span><br>role default <span class='num'>${def}${unit}</span>${full ? '<br><span style=color:var(--warn)>at limit</span>' : ''}">
      <div class="used ${full ? 'full' : ''}" style="width:${(used / max) * 100}%"></div>
      <div class="def" style="left:${(def / max) * 100}%"></div>
      <div class="cap" style="left:calc(${(cap / max) * 100}% - 1px)"></div></div>
    <span class="v ${full ? 'full' : ''}">${used}/${cap}${unit}</span></div>`;
}
function sparkline(u) {
  const d = dailySeries(u);
  const W = 128, H = 30, max = Math.max(...d, 1);
  const pts = d.map((v, i) => [2 + (i / (d.length - 1)) * (W - 4), H - 3 - (v / max) * (H - 8)]);
  const path = pts.map((p, i) => (i ? 'L' : 'M') + p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' ');
  const last = pts[pts.length - 1];
  const delta = u.prev ? Math.round(((u.cost / 23 * 30) - u.prev) / u.prev * 100) : 0;
  return `<svg class="spark" width="${W}" height="${H}" data-tip="<b>${u.id}</b> · daily cost, 1–23 Sep<br>peak <span class='num'>${money(max)}</span> · today <span class='num'>${money(d[d.length - 1])}</span><br>month pace vs August: ${delta >= 0 ? '+' : ''}${delta}%">
    <line x1="2" x2="${W - 2}" y1="${H - 3}" y2="${H - 3}" stroke="var(--line-2)"/>
    <path d="${path}" fill="none" stroke="var(--t2)" stroke-width="1.5" stroke-linejoin="round"/>
    <circle cx="${last[0]}" cy="${last[1]}" r="2.5" fill="var(--t1)"/></svg>`;
}
function personCard(u) {
  const dflt = DEFAULTS[u.role];
  const delta = u.prev ? Math.round(((u.cost / 23 * 30) - u.prev) / u.prev * 100) : 0;
  return `<button class="person ${u.disabled ? 'disabled' : ''}" data-person="${u.id}">
    <div class="p-head"><span class="avatar">${u.id[0].toUpperCase()}</span><span class="nm">${u.id}</span>
      <div class="p-cost"><div class="v">${money(u.cost)}</div><div class="l">this month</div></div>
      <span class="role">${u.role}${u.flags.map(([f, c]) => ` · <span class="${c === 'crit' ? 'crit' : c === 'warn' ? 'warn' : ''}">${f}</span>`).join('')}</span></div>
    <div class="bullets">
      ${bullet('Primaries', u.prim[0], u.prim[1], dflt.prim)}
      ${bullet('Children', u.child[0], u.child[1], dflt.child)}
      ${bullet('vCPU', u.vcpu[0], u.vcpu[1], dflt.vcpu)}
      ${bullet('RAM', u.ram[0], u.ram[1], dflt.ram, ' GB')}
    </div>
    <div style="display:flex;align-items:end;gap:10px">${sparkline(u)}<span class="stale">${u.disabled ? 'no spend' : `pace ${delta >= 0 ? '+' : ''}${delta}% vs Aug`}</span></div>
    <div class="p-foot">${u.vms.length ? u.vms.map(([n, st, ram, d]) => `<span class="vmchip" data-tip="<b>${u.id}/${n}</b><br>${d} · ${ram} GB"><span class="dot ${stateDot(st)}" style="width:7px;height:7px"></span>${n}</span>`).join('') : '<span class="stale">no VMs</span>'}</div>
  </button>`;
}
function renderRoster(body) {
  const users = [...USERS].sort((a, b) => HOST.sort === 'cost' ? b.cost - a.cost : HOST.sort === 'ram' ? b.ram[0] - a.ram[0] : a.id.localeCompare(b.id));
  body.innerHTML = `<div class="view"><div class="roster">
    <div class="attn">
      ${HOST.jobResolved ? `<span class="attn-chip ok">${ic('ok')} child-delete bob/win-qa retried · resolved</span>` : `<button class="attn-chip crit" data-goto="jobs"><span class="crit">${ic('error')}</span>Job failed: delete child bob/win-qa · access denied <span class="act">Retry →</span></button>`}
      ${HOST.jobResolved ? '' : `<button class="attn-chip crit" data-person-open="bob"><span class="crit">${ic('clock')}</span>Lease overdue: bob/win-qa · 2h <span class="act">Open bob →</span></button>`}
      <button class="attn-chip warn" data-goto="media"><span class="warn">${ic('key')}</span>Activation failed 0xC004C008 · 2 days ago <span class="act">Licences →</span></button>
      <button class="attn-chip warn" data-goto="capacity"><span class="warn">${ic('info')}</span>Inventory incomplete for 1 VM <span class="act">Capacity →</span></button>
    </div>
    <div class="roster-tools">
      <h2 style="margin:0;font:600 var(--fs-18) var(--sans);color:var(--t1)">People</h2><span class="t3" style="font-size:var(--fs-12)">5 users · 4 active · $492.70 this month</span>
      <div style="margin-left:auto;display:flex;gap:6px;align-items:center"><span class="stale">sort</span>
        ${[['cost', 'Cost'], ['ram', 'RAM'], ['name', 'Name']].map(([k, l]) => `<button class="chip" data-sort="${k}" aria-pressed="${HOST.sort === k}">${l}</button>`).join('')}
        <span style="width:8px"></span>
        <button class="btn sm" id="offBtn">${ic('user')} Offboard…</button>
        <button class="btn sm primary" id="onBtn">${ic('plus')} Onboard</button></div>
    </div>
    <div class="people">${users.map(personCard).join('')}
      <div class="card" style="padding:14px;display:grid;gap:8px;align-content:start">
        <span class="eyebrow">How to read the bars</span>
        <div class="bullet"><span>RAM</span><div class="bb"><div class="used" style="width:60%"></div><div class="def" style="left:72%"></div><div class="cap" style="left:calc(80% - 1px)"></div></div><span class="v">used / allow</span></div>
        <div style="font-size:var(--fs-11);color:var(--t3);display:grid;gap:3px"><span><span style="display:inline-block;width:14px;height:6px;background:var(--calm);border-radius:2px"></span> used · <span class="warn">amber</span> when at the limit</span><span><span style="display:inline-block;width:2px;height:10px;background:var(--t2)"></span> that person's allowance</span><span><span style="display:inline-block;width:1px;height:10px;background:var(--t4)"></span> role default</span><span>Hatched track: no limit (admin)</span></div>
        <div class="stale">Sparkline: daily cost this month. Needs the service's new daily usage buckets.</div>
      </div>
    </div>
  </div></div>${HOST.person ? personDrawer(USERS.find((u) => u.id === HOST.person)) : ''}`;
  body.querySelectorAll('[data-person]').forEach((b) => b.addEventListener('click', () => { HOST.person = b.dataset.person; HOST.dirty = 0; HOST.tokenShown = false; renderRoster(body); refreshScenario(); }));
  body.querySelectorAll('[data-person-open]').forEach((b) => b.addEventListener('click', () => { HOST.person = b.dataset.personOpen; renderRoster(body); }));
  body.querySelectorAll('[data-sort]').forEach((b) => b.addEventListener('click', () => { HOST.sort = b.dataset.sort; renderRoster(body); }));
  body.querySelectorAll('[data-goto]').forEach((b) => b.addEventListener('click', () => { HOST.tab = b.dataset.goto; renderHostSurface($('#stage')); refreshScenario(); }));
  $('#onBtn').addEventListener('click', () => onboard(0));
  $('#offBtn').addEventListener('click', () => offboard('jonas', 0));
  if (HOST.person) bindDrawer(body);
}

function personDrawer(u) {
  const cap = (v) => v == null ? '' : v;
  return `<div class="drawer" id="drawer" role="dialog" aria-label="Edit ${u.id}">
    <header><span class="avatar" style="width:40px;height:40px">${u.id[0].toUpperCase()}</span>
      <div><h2>${u.id}</h2><div class="stale">member since 3 Feb 2026 · ${u.vms.length} VMs · ${money(u.cost)} this month</div></div>
      <button class="icon-btn" style="margin-left:auto" id="drClose" aria-label="Close">${ic('x')}</button></header>
    <div class="dscroll">
      <div class="fs"><h4 class="eyebrow">Account</h4>
        <div class="form-grid">
          <div class="field"><label>Role</label><select class="input" data-f><option ${u.role === 'dev' ? 'selected' : ''}>dev</option><option ${u.role === 'contractor' ? 'selected' : ''}>contractor</option><option ${u.role === 'admin' ? 'selected' : ''}>admin</option></select></div>
          <div class="field"><label>Display name</label><input class="input" value="${u.id[0].toUpperCase() + u.id.slice(1)}" data-f></div>
          <div class="field"><label>Status</label><div style="display:flex;align-items:center;gap:8px;height:30px"><button class="switch" role="switch" aria-checked="${!u.disabled}" data-fsw aria-label="Enabled"></button><span class="t2" style="font-size:var(--fs-12)">${u.disabled ? 'disabled' : 'enabled'}</span></div></div>
        </div></div>
      <div class="fs"><h4 class="eyebrow">Allowance <span class="hint">empty = role default (${u.role})</span>
          <span style="margin-left:auto;display:flex;gap:4px"><button class="btn xs ghost" data-tpl="contractor">contractor</button><button class="btn xs ghost" data-tpl="dev">dev</button><button class="btn xs ghost" data-tpl="senior">senior</button></span></h4>
        <div class="form-grid">
          <div class="field"><label>Primary VMs</label><input class="input num" value="${cap(u.prim[1])}" placeholder="∞" data-f data-a="prim"></div>
          <div class="field"><label>Child VMs</label><input class="input num" value="${cap(u.child[1])}" placeholder="∞" data-f data-a="child"></div>
          <div class="field"><label>vCPU total</label><input class="input num" value="${cap(u.vcpu[1])}" placeholder="∞" data-f data-a="vcpu"></div>
          <div class="field"><label>RAM total (GB)</label><input class="input num" value="${cap(u.ram[1])}" placeholder="∞" data-f data-a="ram"></div>
          <div class="field"><label>Storage (GB)</label><input class="input num" value="${u.role === 'contractor' ? 200 : 400}" data-f></div>
          <div class="field"><label>Max child lease</label><select class="input" data-f><option>12 h</option><option selected>3 days</option><option>7 days</option></select></div>
        </div>
        <div style="display:flex;gap:16px;margin-top:10px;font-size:var(--fs-12)"><label class="check"><input type="checkbox" checked data-f> Host forwards (LAN)</label><label class="check"><input type="checkbox" ${u.role !== 'contractor' ? 'checked' : ''} data-f> Windows child VMs</label><label class="check"><input type="checkbox" data-f> Public VMs</label></div>
        <div class="card" style="margin-top:10px;padding:10px 12px"><div class="bullets">
          ${bullet('Primaries', u.prim[0], u.prim[1], DEFAULTS[u.role].prim)}${bullet('Children', u.child[0], u.child[1], DEFAULTS[u.role].child)}${bullet('vCPU', u.vcpu[0], u.vcpu[1], DEFAULTS[u.role].vcpu)}${bullet('RAM', u.ram[0], u.ram[1], DEFAULTS[u.role].ram, ' GB')}
        </div><div class="stale" style="margin-top:6px">Effective now · host headroom: 22 GB RAM below the admission line</div></div>
      </div>
      <div class="fs"><h4 class="eyebrow">VMs &amp; per-VM overrides</h4>
        <table class="tbl"><thead><tr><th>VM</th><th>State</th><th class="r">RAM</th><th>Override</th><th class="r">Month</th><th></th></tr></thead><tbody>
        ${u.vms.map(([n, st, ram, d], i) => `<tr><td class="t1 mono" style="font-size:var(--fs-11)">${n}</td><td><span style="display:inline-flex;gap:6px;align-items:center"><span class="dot ${stateDot(st)}"></span>${d}</span></td><td class="r num">${ram} GB</td>
          <td>${i === 1 && u.id === 'alice' ? '<span class="tag calm">idle: save 30 min</span>' : st === 'overdue' ? '<span class="tag crit">lease +2h</span>' : '<span class="t4" style="font-size:var(--fs-11)">—</span>'}</td>
          <td class="r num">${money(u.cost * [0.62, 0.28, 0.10][i] || 0)}</td><td class="r"><button class="icon-btn" data-vmmenu="${u.id}/${n}" data-menu-anchor aria-label="VM actions" style="width:24px;height:24px">${ic('more')}</button></td></tr>`).join('') || '<tr><td colspan="6" class="t3">No VMs</td></tr>'}
        </tbody></table></div>
      <div class="fs"><h4 class="eyebrow">Tokens</h4>
        <div class="list-rows">
          <div class="lr"><div><span class="mono">${u.id}-pc-1</span><div class="stale">issued 3 Feb · last used 4 min ago · from 203.0.113.21</div></div><span style="display:flex;gap:4px"><button class="btn xs" data-rotate>Rotate</button><button class="btn xs danger">Revoke</button></span></div>
          ${u.flags.some(([f]) => f === 'legacy credential') ? `<div class="lr"><div><span class="mono warn">legacy password credential</span><div class="stale">no expiry · replace with a token, then remove</div></div><button class="btn xs danger">Remove</button></div>` : ''}
        </div>
        <div id="tokenOnce" style="margin-top:8px">${HOST.tokenShown ? tokenOnceBox(u.id) : `<button class="btn sm" id="issueTok">${ic('key')} Issue new token</button>`}</div></div>
      <div class="stale">Audit: "dana rotated token for mara · 13:22" and 6 earlier entries for ${u.id} · <a href="#host/jobs" style="color:var(--calm)">open audit</a></div>
    </div>
    <footer><span class="msg" id="drMsg">${HOST.dirty ? `<span class="dot warn"></span>${HOST.dirty} unsaved change${HOST.dirty > 1 ? 's' : ''}` : 'No changes'}</span>
      <button class="btn danger sm" id="drOff">Offboard ${u.id}…</button>
      <button class="btn ghost" id="drDiscard" ${HOST.dirty ? '' : 'disabled'}>Discard</button>
      <button class="btn primary" id="drSave" ${HOST.dirty ? '' : 'disabled'}>Save changes</button></footer>
  </div>`;
}
function tokenOnceBox(id) {
  return `<div class="token-once"><div style="display:flex;align-items:center;gap:8px"><span style="color:var(--live)">${ic('key')}</span><b class="t1" style="font-size:var(--fs-12)">New token for ${id}. Shown once.</b></div>
    <code>ctk_${id}_7Hq2fW9xR4mLpZs0Nc3VbT8eYkJa61Ud</code><div style="display:flex;gap:6px"><button class="btn sm primary">${ic('copy')} Copy</button><span class="stale" style="align-self:center">It is stored hashed. Close this and it cannot be shown again.</span></div></div>`;
}
function bindDrawer(body) {
  const u = USERS.find((x) => x.id === HOST.person);
  const dr = $('#drawer', body);
  const upd = () => { $('#drMsg').innerHTML = HOST.dirty ? `<span class="dot warn"></span>${HOST.dirty} unsaved change${HOST.dirty > 1 ? 's' : ''} · one save applies all` : 'No changes'; $('#drSave').disabled = !HOST.dirty; $('#drDiscard').disabled = !HOST.dirty; };
  dr.querySelectorAll('[data-f]').forEach((f) => f.addEventListener(f.tagName === 'INPUT' && f.type === 'checkbox' ? 'change' : 'input', () => { if (!f.classList.contains('changed')) { HOST.dirty++; f.classList.add('changed'); } upd(); }));
  dr.querySelectorAll('select[data-f]').forEach((f) => f.addEventListener('change', () => { if (!f.classList.contains('changed')) { HOST.dirty++; f.classList.add('changed'); } upd(); }));
  dr.querySelectorAll('[data-fsw]').forEach((s) => s.addEventListener('click', () => { s.setAttribute('aria-checked', s.getAttribute('aria-checked') !== 'true'); HOST.dirty++; upd(); }));
  dr.querySelectorAll('[data-tpl]').forEach((b) => b.addEventListener('click', () => {
    const tp = { contractor: { prim: 1, child: 1, vcpu: 4, ram: 16 }, dev: { prim: 2, child: 2, vcpu: 8, ram: 24 }, senior: { prim: 3, child: 4, vcpu: 16, ram: 32 } }[b.dataset.tpl];
    dr.querySelectorAll('[data-a]').forEach((i) => { const v = String(tp[i.dataset.a]); if (i.value !== v) { i.value = v; if (!i.classList.contains('changed')) { HOST.dirty++; i.classList.add('changed'); } } });
    upd();
  }));
  $('#drClose').addEventListener('click', () => { HOST.person = null; renderRoster(body); refreshScenario(); });
  $('#drDiscard').addEventListener('click', () => { HOST.dirty = 0; renderRoster(body); });
  $('#drSave').addEventListener('click', () => { HOST.dirty = 0; renderRoster(body); toast($('#hwin'), { title: `Saved ${u.id}`, msg: 'Role, allowance and overrides applied in one change. Audit entry written.', icon: 'ok' }); });
  $('#drOff').addEventListener('click', () => offboard(u.id, 0));
  const it = $('#issueTok'); if (it) it.addEventListener('click', () => { HOST.tokenShown = true; $('#tokenOnce').innerHTML = tokenOnceBox(u.id); });
  dr.querySelectorAll('[data-rotate]').forEach((b) => b.addEventListener('click', () => { HOST.tokenShown = true; $('#tokenOnce').innerHTML = tokenOnceBox(u.id); }));
  dr.querySelectorAll('[data-vmmenu]').forEach((b) => b.addEventListener('click', () => vmMenu(b, b.dataset.vmmenu)));
}
function vmMenu(anchor, name) {
  const m = openMenu(anchor, `<div class="head eyebrow">${name}</div>
    <button>${ic('gear')} VM settings &amp; overrides</button><button>${ic('restart')} Restart</button><button>${ic('clock')} Change lifetime…</button><button>${ic('link')} Make public…</button>
    <button>${ic('key')} Rotate VM token</button><div class="sep"></div><button>${ic('power')} Shut down</button><button style="color:#ffb3b3" id="vmDel">${ic('trash')} Delete…</button>`, $('#hwin'), { alignRight: true });
  $('#vmDel', m).addEventListener('click', () => { closeMenu(); confirmDanger($('#hwin'), { title: `Delete ${name}?`, sub: 'The VM and its disks are deleted on buildbox.', name, action: 'Delete VM', impact: [{ icon: 'trash', cls: 'crit', k: 'VM and disks', r: 'deleted', rc: 'crit' }, { icon: 'user', k: 'Owner is notified in their Companion' }], onConfirm: () => toast($('#hwin'), { title: 'Delete queued', msg: `Job "delete ${name}" added.`, icon: 'trash' }) }); });
  m.querySelectorAll('button:not(#vmDel)').forEach((b) => b.addEventListener('click', closeMenu));
}

/* Onboard wizard */
function onboard(step) {
  const win = $('#hwin');
  $$('.scrim', win).forEach((s) => s.remove());
  const steps = ['Register', 'Allowance', 'Token'];
  const rail = `<div class="steps">${steps.map((s, i) => `<div class="s ${i < step ? 'done' : i === step ? 'cur' : ''}"><span class="n">${i < step ? '✓' : i + 1}</span>${s}</div>`).join('')}</div>`;
  let content, next;
  if (step === 0) {
    content = `<div class="form-grid two"><div class="field"><label>Username</label><input class="input mono" value="lena" id="obName"></div><div class="field"><label>Display name</label><input class="input" value="Lena Hofer"></div>
      <div class="field"><label>Role</label><select class="input"><option>dev</option><option>contractor</option><option>admin</option></select></div><div class="field"><label>Sign-in</label><select class="input"><option>API token (recommended)</option><option>Domain account</option></select></div></div>`;
    next = 'Next: allowance';
  } else if (step === 1) {
    content = `<div class="tpl-cards">
      ${[['contractor', '1 primary · 1 child · 4 vCPU · 16 GB', false], ['dev', '2 primaries · 2 children · 8 vCPU · 24 GB', true], ['senior', '3 primaries · 4 children · 16 vCPU · 32 GB', false]].map(([n, d, c]) => `<label class="radio-card"><input type="radio" name="tpl" ${c ? 'checked' : ''}><b>${n}</b><span>${d}</span></label>`).join('')}</div>
      <div class="card" style="padding:10px 12px;font-size:var(--fs-11);color:var(--t3)">Host impact: allowances total 128 GB of RAM on a 128 GB host after this (over-commit is fine, admission still applies at 118 GB resident). You can fine-tune per person later.</div>`;
    next = 'Create lena and issue token';
  } else {
    content = `${tokenOnceBox('lena')}<div style="font-size:var(--fs-12);color:var(--t3)">Send lena the one-liner: <span class="mono t1">install.ps1 -Backend remote -Host buildbox.example.local:7462</span>. The installer asks for this token.</div>`;
    next = 'Done';
  }
  const s = openDialog(win, `<header><div class="danger-ic" style="background:var(--calm-bg);color:var(--calm);border-color:var(--line-3)">${ic('user')}</div><div><h2>Onboard a developer</h2><p>Three steps. Nothing is created until step 2.</p></div></header>
    <div class="dbody">${rail}${content}</div>
    <footer>${step > 0 && step < 2 ? '<button class="btn ghost" id="obBack">Back</button>' : '<button class="btn ghost" data-close>Cancel</button>'}<button class="btn primary" id="obNext">${next}</button></footer>`, 'wide');
  $('#obNext', s).addEventListener('click', () => { if (step < 2) onboard(step + 1); else { s.remove(); toast(win, { title: 'lena onboarded', msg: 'dev allowance · token issued · audit entry written', icon: 'ok' }); } });
  const bk = $('#obBack', s); if (bk) bk.addEventListener('click', () => onboard(step - 1));
}

/* Offboard wizard */
function offboard(id, step) {
  const win = $('#hwin');
  $$('.scrim', win).forEach((s) => s.remove());
  const u = USERS.find((x) => x.id === id);
  const steps = ['Choose & disable', 'Cascade preview', 'Confirm'];
  const rail = `<div class="steps ${step === 2 ? 'crit' : ''}">${steps.map((s, i) => `<div class="s ${i < step ? 'done' : i === step ? 'cur' : ''}"><span class="n">${i < step ? '✓' : i + 1}</span>${s}</div>`).join('')}</div>`;
  let content;
  if (step === 0) {
    content = `<div class="field"><label>Person</label><select class="input" id="offWho">${USERS.filter((x) => x.id !== 'dana').map((x) => `<option ${x.id === id ? 'selected' : ''}>${x.id}</option>`).join('')}</select></div>
      <div class="card" style="padding:10px 12px;font-size:var(--fs-12)">Step 1 disables ${u.id} right away: tokens stop working and no new VMs can be created. Running VMs keep running until step 3. You can stop here and re-enable later.</div>`;
  } else {
    const rows = [
      ...u.vms.map(([n, st, ram, d]) => ({ icon: n.includes('win') || d.startsWith('child') ? 'child' : 'box', cls: 'crit', k: `${u.id}/${n}`, v: `${d} · ${ram} GB`, r: 'deleted', rc: 'crit' })),
      ...(u.id === 'mara' ? [{ icon: 'users', cls: 'warn', k: 'Shared child alice/ui-review (mara is co-owner)', v: 'alice stays owner', r: 'unshared', rc: 'warn' }] : []),
      { icon: 'key', k: 'Windows keys to release', v: u.id === 'bob' ? 'key A activation freed (bob/win-qa)' : 'none held', r: u.id === 'bob' ? 'released' : '—' },
      { icon: 'lock', k: `Tokens and credentials (${u.flags.some(([f]) => f === 'legacy credential') ? '1 token + legacy credential' : '1 token'})`, r: 'revoked', rc: 'crit' },
      { icon: 'shield', cls: 'calm', k: 'Usage history and audit entries', r: 'kept', rc: 'calm' },
    ];
    if (!u.vms.length) rows.unshift({ icon: 'ok', cls: 'calm', k: 'No VMs to delete' });
    content = `<div class="impact">${rows.map((i) => `<div class="row"><span class="${i.cls || 't3'}">${ic(i.icon)}</span><div><div class="k">${i.k}</div>${i.v ? `<div class="v">${i.v}</div>` : ''}</div>${i.r ? `<span class="tag ${i.rc || ''}">${i.r}</span>` : '<span></span>'}</div>`).join('')}</div>
      <div class="stale">Frees ${u.ram[0]} GB RAM and ${u.vcpu[0]} vCPU. ${u.id} is disabled already (step 1 done).</div>
      ${step === 2 ? `<div class="typed"><label for="offTyped">Type <code>${u.id}</code> to delete everything listed</label><input id="offTyped" class="input mono" autocomplete="off" placeholder="${u.id}"></div>` : ''}`;
  }
  const s = openDialog(win, `<header><div class="danger-ic">${ic('user')}</div><div><h2>Offboard ${u.id}</h2><p>Disable first, then see exactly what goes before anything is deleted.</p></div></header>
    <div class="dbody">${rail}${content}</div>
    <footer><span class="left">${step === 2 ? 'This deletes VMs and cannot be undone.' : ''}</span>
      <button class="btn ghost" data-close>${step === 0 ? 'Cancel' : 'Stop here (stay disabled)'}</button>
      ${step === 2 ? `<button class="btn danger solid" id="offGo" disabled>Delete ${u.vms.length} VMs and offboard</button>` : `<button class="btn ${step === 0 ? 'danger' : 'primary'}" id="offNext">${step === 0 ? `Disable ${u.id}` : 'Continue to confirm'}</button>`}</footer>`, 'wide');
  const who = $('#offWho', s); if (who) who.addEventListener('change', () => offboard(who.value, 0));
  const nx = $('#offNext', s); if (nx) nx.addEventListener('click', () => offboard(id, step + 1));
  const inp = $('#offTyped', s), go = $('#offGo', s);
  if (inp) { inp.addEventListener('input', () => { go.disabled = inp.value.trim() !== u.id; }); setTimeout(() => inp.focus(), 40); }
  if (go) go.addEventListener('click', () => { s.remove(); toast(win, { title: `Offboarding ${u.id}`, msg: `${u.vms.length} delete jobs queued. Progress in Jobs & audit.`, icon: 'trash' }); });
}

/* ── Capacity ───────────────────────────────────────────────────────── */
const ALL_VMS = [
  ['alice', 'work-vm', 'running', 16, 6, 'busy', '—'], ['alice', 'bench', 'idle', 12, 4, 'idle 2h · save in 28 min', '—'], ['alice', 'win11-eval', 'creating', 12, 4, 'creating 62%', '3 d'],
  ['bob', 'api-vm', 'running', 12, 4, 'busy', '—'], ['bob', 'win-qa', 'overdue', 8, 4, 'lease 2h over', 'overdue'],
  ['dana', 'agent-vm-2', 'running', 16, 8, 'busy', '—'], ['dana', 'dev-2', 'saved', 12, 4, 'saved · idle policy', '—'],
  ['mara', 'scratch-3', 'pressure', 16, 4, 'saved by memory pressure', '—'], ['—', 'unknown-7f3a', 'running', 6, 2, 'unmanaged · inventory incomplete', '—'],
];
function renderCapacity(body) {
  const f = HOST.vmFilter;
  const vms = ALL_VMS.filter((v) => f === 'all' || (f === 'running' && ['running', 'idle', 'creating', 'overdue'].includes(v[2])) || (f === 'saved' && ['saved', 'pressure'].includes(v[2])) || (f === 'attention' && ['overdue', 'pressure'].includes(v[2]) || (f === 'attention' && v[0] === '—')));
  body.innerHTML = `<div class="view"><div class="cap-grid">
    <div style="display:grid;gap:14px;align-content:start">
      <div class="card"><div class="card-h"><h3>Host memory</h3><span class="tag warn">${ic('warn')} pressure elevated</span><span class="right stale">sampled 12 s ago</span></div>
        <div class="card-b ram-chart" id="ramChart"></div></div>
      <div class="card"><div class="card-h"><h3>All VMs</h3><span class="t3" style="font-size:var(--fs-11)">${ALL_VMS.length} on buildbox</span>
        <div class="right">${[['all', 'All'], ['running', 'Running'], ['saved', 'Saved'], ['attention', 'Needs attention']].map(([k, l]) => `<button class="chip" data-vf="${k}" aria-pressed="${f === k}">${l}</button>`).join('')}</div></div>
        <table class="tbl"><thead><tr><th>Owner</th><th>VM</th><th>State</th><th class="r">RAM</th><th class="r">vCPU</th><th>Lease</th><th></th></tr></thead><tbody>
        ${vms.map(([o, n, st, ram, cpu, d, lease]) => `<tr><td>${o === '—' ? '<span class="t4">unmanaged</span>' : `<button class="btn xs ghost" data-person-go="${o}">${o}</button>`}</td><td class="mono t1" style="font-size:var(--fs-11)">${n}</td>
          <td><span style="display:inline-flex;gap:6px;align-items:center"><span class="dot ${stateDot(st)}"></span><span class="${st === 'overdue' ? 'crit' : st === 'pressure' ? 'warn' : ''}">${d}</span></span></td>
          <td class="r num">${ram} GB</td><td class="r num">${cpu}</td><td>${lease === 'overdue' ? '<span class="tag crit">2h over</span>' : `<span class="t3 num" style="font-size:var(--fs-11)">${lease}</span>`}</td>
          <td class="r"><button class="icon-btn" data-vmmenu="${o}/${n}" data-menu-anchor aria-label="Actions" style="width:24px;height:24px">${ic('more')}</button></td></tr>`).join('')}
        </tbody></table></div>
    </div>
    <div style="display:grid;gap:14px;align-content:start">
      <div class="card"><div class="card-h"><h3>Next saved under pressure</h3></div><div class="card-b" style="display:grid;gap:8px;font-size:var(--fs-12)">
        <div class="stale">Idle VMs, soonest first. Pressure saves pick from this list.</div>
        <div style="display:grid;grid-template-columns:1fr auto;gap:2px 8px"><span class="t1 mono" style="font-size:var(--fs-11)">alice/bench</span><span class="num t1" style="font-size:var(--fs-11)">28 min</span><span class="stale">idle 2h · last heartbeat 12:14 · 12 GB</span></div>
        <div style="display:grid;grid-template-columns:1fr auto;gap:2px 8px"><span class="t1 mono" style="font-size:var(--fs-11)">bob/win-qa</span><span class="num t1" style="font-size:var(--fs-11)">now</span><span class="stale">lease overdue · 8 GB</span></div>
        <div class="divider"></div><div class="stale">Last pressure save: mara/scratch-3 at 13:48 (22 min ago), freed 16 GB.</div></div></div>
      <div class="card"><div class="card-h"><h3>Child leases</h3><span class="right stale">now 14:18</span></div><div class="card-b ram-chart" id="leaseChart"></div></div>
      <div class="card"><div class="card-h"><h3>Host</h3></div><div class="card-b"><dl class="kv"><dt>Cores</dt><dd>32 · 38 vCPU assigned</dd><dt>Disk D:</dt><dd>3.6 TB · 61% used</dd><dt>Capacity mode</dt><dd>enforce</dd><dt>Admission line</dt><dd>118 GB resident</dd></dl></div></div>
    </div></div></div>`;
  body.querySelectorAll('[data-vf]').forEach((b) => b.addEventListener('click', () => { HOST.vmFilter = b.dataset.vf; renderCapacity(body); }));
  body.querySelectorAll('[data-vmmenu]').forEach((b) => b.addEventListener('click', () => vmMenu(b, b.dataset.vmmenu)));
  body.querySelectorAll('[data-person-go]').forEach((b) => b.addEventListener('click', () => { HOST.tab = 'roster'; HOST.person = b.dataset.personGo; renderHostSurface($('#stage')); }));
  drawRam(); drawLeases();
}
function drawRam() {
  const host = $('#ramChart');
  const W = host.clientWidth - 28, L = 86, R = 20, max = 150;
  const X = (v) => L + (v / max) * (W - L - R);
  const shades = { alice: '#5fc58a', bob: '#4a9d6e', dana: '#3a7d57', mara: '#2d6345', system: '#51606f', shared: '#6f7f8e' };
  const seg = (rows, y, h) => { let acc = 0; return rows.map(([k, v, lab]) => { const x = X(acc), w = X(acc + v) - X(acc) - 2; acc += v; return `<rect x="${x}" y="${y}" width="${Math.max(0, w)}" height="${h}" rx="2" fill="${shades[k]}" data-tip="<b>${lab}</b><br><span class='num'>${v} GB</span>"/>${w > 34 ? `<text x="${x + 5}" y="${y + h / 2 + 3.5}" style="fill:#04120a;font-weight:600">${k === 'system' ? 'host+unm.' : k === 'shared' ? 'shared ch.' : k}</text>` : ''}`; }).join(''); };
  const resident = [['alice', 28, 'alice · resident'], ['bob', 20, 'bob · resident'], ['dana', 28, 'dana · resident'], ['system', 20, 'host OS 8 GB + unmanaged 12 GB']];
  const committed = [['alice', 40, 'alice · assigned (incl. creating win11-eval)'], ['bob', 20, 'bob · assigned'], ['dana', 28, 'dana · assigned (dev-2 saved)'], ['mara', 16, 'mara · assigned (scratch-3 saved)'], ['system', 20, 'host OS + unmanaged'], ['shared', 18, 'shared children']];
  let s = `<svg width="${W}" height="150" role="img" aria-label="Host memory: 96 GB resident of 128 GB physical; 142 GB committed; admission line at 118 GB">`;
  [0, 25, 50, 75, 100, 125, 150].forEach((v) => { s += `<line class="gridl" x1="${X(v)}" x2="${X(v)}" y1="14" y2="118" stroke="var(--line)"/><text x="${X(v)}" y="134" text-anchor="middle">${v}</text>`; });
  s += `<text x="${W - R}" y="148" text-anchor="end">GB</text>`;
  s += `<text x="0" y="42">Resident</text><text x="0" y="55" style="font-size:10px">96 GB</text>` + seg(resident, 28, 28);
  s += `<text x="0" y="86">Committed</text><text x="0" y="99" style="font-size:10px;fill:var(--warn)">142 GB · 111%</text>` + seg(committed, 72, 28);
  s += `<line x1="${X(128)}" x2="${X(128)}" y1="14" y2="118" stroke="var(--t1)" stroke-width="1.5"/><text class="v" x="${X(128) + 4}" y="12" style="fill:var(--t1)">physical 128</text>`;
  s += `<line x1="${X(118)}" x2="${X(118)}" y1="20" y2="118" stroke="var(--warn)" stroke-width="1.5" stroke-dasharray="3 3"/><text x="${X(118) - 4}" y="12" text-anchor="end" style="fill:var(--warn)">admission 118</text>`;
  s += '</svg>';
  host.innerHTML = s + `<div class="stale" style="display:flex;gap:14px;margin-top:4px"><span>Free physical: <span class="t1">32 GB</span></span><span>Headroom to admission: <span class="t1">22 GB</span></span><span>Shades = owners; hover a segment</span></div>`;
}
function drawLeases() {
  const host = $('#leaseChart');
  const W = host.clientWidth - 28, L = 132, x0 = -24, x1 = 72; // hours relative to now
  const X = (h) => L + ((h - x0) / (x1 - x0)) * (W - L - 6);
  const rows = [['bob/win-qa', -20, -2, true], ['dana/tpm-probe', -3, 5.3], ['alice/win11-eval', 0, 72], ['alice/ui-review', -30, 30]];
  let s = `<svg width="${W}" height="${rows.length * 22 + 22}">`;
  rows.forEach(([n, a, b, over], i) => {
    const y = 4 + i * 22;
    s += `<text x="0" y="${y + 11}">${n}</text><rect x="${X(Math.max(a, x0))}" y="${y + 3}" width="${X(Math.min(b, x1)) - X(Math.max(a, x0))}" height="10" rx="2" fill="${over ? 'var(--crit)' : 'var(--calm)'}" opacity="${over ? 1 : .8}" data-tip="<b>${n}</b><br>${over ? 'expired 2h ago' : 'expires in ' + (b >= 24 ? Math.round(b / 24) + ' d' : b.toFixed(1) + ' h')}"/>`;
    if (over) s += `<text x="${X(b) + 4}" y="${y + 11}" style="fill:var(--crit)">2h over</text>`;
  });
  const y = rows.length * 22 + 6;
  s += `<line x1="${X(0)}" x2="${X(0)}" y1="0" y2="${y}" stroke="var(--t1)" stroke-dasharray="2 3"/><text x="${X(0)}" y="${y + 12}" text-anchor="middle">now</text><text x="${X(24)}" y="${y + 12}" text-anchor="middle">+1 d</text><text x="${X(48)}" y="${y + 12}" text-anchor="middle">+2 d</text></svg>`;
  host.innerHTML = s;
}

/* ── Jobs & audit ───────────────────────────────────────────────────── */
function renderJobs(body) {
  body.innerHTML = `<div class="view"><div class="two-col">
    <div><div class="section-title"><h2>Jobs</h2><p>1 running · 1 queued · ${HOST.jobResolved ? '0' : '1'} failed</p></div>
      <div class="jobs">
        ${HOST.jobResolved
          ? `<div class="job resolved"><span style="color:var(--calm)">${ic('ok')}</span><span class="t">delete child bob/win-qa</span><span class="tag calm">done 14:18</span><span class="m">retried by dana · took 41 s · key A activation released</span></div>`
          : `<div class="job failed"><span class="crit">${ic('error')}</span><span class="t">delete child bob/win-qa</span><span style="display:flex;gap:6px"><button class="btn sm primary" id="retryJob">${ic('refresh')} Retry</button><button class="btn sm ghost">Log</button></span><span class="m">failed 09:14 · access denied: VHDX locked by vmwp.exe (a checkpoint merge was running)</span></div>`}
        <div class="job"><span style="color:var(--calm)">${ic('refresh')}</span><span class="t">create child win11-eval for alice</span><span class="num t1" style="font-size:var(--fs-11)">62%</span><span class="m">step 5/8 · applying licence baseline · started 13:51</span><div class="prog"><i style="width:62%"></i></div></div>
        <div class="job"><span class="t3">${ic('clock')}</span><span class="t">reprovision dev-2</span><span style="display:flex;gap:6px"><span class="tag">queued</span><button class="btn xs ghost">Cancel</button></span><span class="m">waits for dev-2 to resume · requested by dana 13:55</span></div>
      </div></div>
    <div><div class="section-title"><h2>Audit</h2><p>Who did what, newest first</p><div style="margin-left:auto"><select class="input" style="height:26px;width:130px"><option>Everyone</option><option>alice</option><option>bob</option><option>dana</option><option>mara</option><option>system</option></select></div></div>
      <div class="card">
        ${HOST.jobResolved ? '<div class="audit-row new"><span class="t">14:18</span><span><b class="t1">dana</b> retried delete child bob/win-qa · succeeded</span><span class="tag calm">resolved</span></div>' : ''}
        <div class="audit-row"><span class="t">13:51</span><span><b class="t1">alice</b> created child win11-eval</span><span class="tag">vm</span></div>
        <div class="audit-row"><span class="t">13:48</span><span><b class="t1">system</b> memory-pressure saved mara/scratch-3</span><span class="tag warn">pressure</span></div>
        <div class="audit-row"><span class="t">13:22</span><span><b class="t1">dana</b> rotated token for mara</span><span class="tag">token</span></div>
        <div class="audit-row"><span class="t">12:40</span><span><b class="t1">system</b> lease expired for bob/win-qa (not shut down: owner override)</span><span class="tag crit">lease</span></div>
        <div class="audit-row"><span class="t">09:14</span><span><b class="t1">bob</b> delete child win-qa · failed (access denied)</span><span class="tag crit">job</span></div>
        <div class="audit-row"><span class="t">08:02</span><span><b class="t1">dana</b> changed idle default 90 → 60 min</span><span class="tag">config</span></div>
      </div></div>
  </div></div>`;
  const r = $('#retryJob', body);
  if (r) r.addEventListener('click', () => { r.innerHTML = `${ic('refresh')} Retrying…`; r.disabled = true; setTimeout(() => { HOST.jobResolved = true; renderHostSurface($('#stage')); refreshScenario(); }, 700); });
}

/* ── Media & licences ───────────────────────────────────────────────── */
function cells(used, total) { return `<div class="cells">${Array.from({ length: total }, (_, i) => `<i class="${i < used ? 'u' + (used / total >= 0.7 ? ' hi' : '') : ''}"></i>`).join('')}</div>`; }
function renderMedia(body) {
  body.innerHTML = `<div class="view"><div class="two-col">
    <div style="display:grid;gap:14px;align-content:start">
      <div class="card"><div class="card-h"><h3>Windows licensing</h3><span class="right stale">checked 6 min ago</span></div>
        <div class="card-b" style="padding:0">
          <div class="keymeter"><div class="top"><b>MAK key A</b><span class="mono t3" style="font-size:var(--fs-10)">…-7QK4M</span><span class="num">7 of 10 activations</span></div>${cells(7, 10)}</div>
          <div class="keymeter"><div class="top"><b>MAK key B</b><span class="mono t3" style="font-size:var(--fs-10)">…-P2W9D</span><span class="num">2 of 5 activations</span></div>${cells(2, 5)}</div>
          <div class="keymeter"><div class="top"><b>Retail key</b><span class="mono t3" style="font-size:var(--fs-10)">…-H8C3T</span><span class="num">1 machine · unassigned</span></div></div>
        </div>
        <div style="padding:6px 14px 14px"><div class="job failed" style="grid-template-columns:22px 1fr"><span class="crit">${ic('error')}</span><span class="t">0xC004C008 · key has exceeded its unlock limit</span><span class="m">2 days ago · alice/win11-eval tried an old key C (since removed). Now on evaluation grace.</span></div>
          <div style="display:flex;gap:6px;margin-top:8px"><button class="btn sm">Activate again with key B</button><button class="btn sm ghost">Dismiss</button><button class="btn sm ghost" style="margin-left:auto">${ic('plus')} Add key</button></div></div></div>
      <div class="card"><div class="card-h"><h3>Windows guests</h3></div>
        <table class="tbl"><thead><tr><th>Guest</th><th>State</th><th>Key</th></tr></thead><tbody>
          <tr><td class="mono t1" style="font-size:var(--fs-11)">alice/win11-eval</td><td><span class="tag warn">grace · 29 days</span></td><td class="t3">evaluation</td></tr>
          <tr><td class="mono t1" style="font-size:var(--fs-11)">bob/win-qa</td><td><span class="tag calm">activated</span></td><td>key A</td></tr>
          <tr><td class="mono t3" style="font-size:var(--fs-11)">retained identity · win-qa-base</td><td><span class="tag">retained</span></td><td class="t3">reusable machine ID <button class="btn xs ghost">Release</button></td></tr>
        </tbody></table></div>
    </div>
    <div style="display:grid;gap:14px;align-content:start">
      <div class="card"><div class="card-h"><h3>Ubuntu ISO catalog</h3><div class="right"><button class="btn sm ghost">${ic('refresh')} Check for new</button></div></div>
        <div class="list-rows" style="margin:0 14px 14px">
          <div class="lr"><div><span class="t1">Ubuntu 26.04 LTS server</span><div class="stale">sha256 ok · 3.1 GB · default for new VMs</div></div><span class="tag calm">current</span></div>
          <div class="lr"><div><span class="t1">Ubuntu 24.04.3 LTS server</span><div class="stale">sha256 ok · 2.9 GB · 2 VMs still on it</div></div><button class="btn xs ghost">Retire…</button></div>
        </div></div>
      <div class="card"><div class="card-h"><h3>Child media</h3><div class="right"><button class="btn sm ghost">${ic('plus')} Add media</button></div></div>
        <div class="list-rows" style="margin:0 14px 14px">
          <div class="lr"><div><span class="t1">Windows 11 24H2 evaluation</span><div class="stale">baseline captured with vTPM identity · 90-day eval</div></div><span class="tag">2 children</span></div>
          <div class="lr"><div><span class="t1">Windows Server 2025 evaluation</span><div class="stale">baseline captured · 180-day eval</div></div><span class="tag">0 children</span></div>
        </div></div>
    </div></div></div>`;
}

/* ── Service ────────────────────────────────────────────────────────── */
function renderService(body) {
  body.innerHTML = `<div class="view"><div class="two-col">
    <div style="display:grid;gap:14px;align-content:start">
      <div class="card"><div class="card-h"><h3>Service update</h3><span class="tag info">1.15.0 available</span></div>
        <div class="card-b" style="display:grid;gap:10px">
          <dl class="kv"><dt>Installed</dt><dd>constructd 1.14.2</dd><dt>Latest</dt><dd>1.15.0 · released 19 Sep</dd><dt>Effect on VMs</dt><dd>keep running</dd></dl>
          <div class="behind-box" style="color:var(--t2)"><div style="display:flex;gap:6px;align-items:center;color:var(--warn);font-weight:600">${ic('clock')} 1 job blocks apply</div>
            <div>create child win11-eval for alice · 62%. Apply waits for it, or you cancel it.</div></div>
          <div class="steps">${['Check', 'Stage', 'Apply', 'Verify'].map((s, i) => `<div class="s ${i === 0 || (HOST.updateStaged && i === 1) ? 'done' : (!HOST.updateStaged && i === 1) || (HOST.updateStaged && i === 2) ? 'cur' : ''}"><span class="n">${i + 1}</span>${s}</div>`).join('')}</div>
          <div style="display:flex;gap:6px">${HOST.updateStaged ? `<button class="btn primary" disabled data-tip="Waiting for 1 blocking job">Apply when jobs finish</button><button class="btn ghost" id="unstage">Unstage</button>` : `<button class="btn primary" id="stage">${ic('download')} Stage 1.15.0</button>`}</div>
          <details class="adv"><summary>Recovery (resume, cancel, resolve, roll back)</summary>
            <div style="display:flex;gap:6px;flex-wrap:wrap;padding:6px 0"><button class="btn sm">Resume</button><button class="btn sm">Cancel update</button><button class="btn sm">Resolve: keep new</button><button class="btn sm">Resolve: keep old</button><button class="btn sm danger">Roll back to 1.14.2</button></div></details>
        </div></div>
      <div class="card"><div class="card-h"><h3>Health</h3></div><div class="card-b" style="display:grid;gap:6px;font-size:var(--fs-12)">
        ${[['ok', 'Hyper-V backend reachable'], ['ok', 'TLS certificate valid · 211 days'], ['ok', 'Disk D: 61% used'], ['warn', 'Inventory incomplete for 1 VM (unknown-7f3a)'], ['warn', '1 lease overdue (bob/win-qa)']].map(([s, t]) => `<div style="display:flex;gap:8px;align-items:center"><span class="${s === 'ok' ? '' : 'warn'}" style="${s === 'ok' ? 'color:var(--calm)' : ''}">${ic(s === 'ok' ? 'ok' : 'warn')}</span>${t}</div>`).join('')}</div></div>
    </div>
    <div class="card" style="align-self:start"><div class="card-h"><h3>Backend capabilities</h3><span class="right stale">reference</span></div>
      <details class="adv" style="padding:0 14px 12px" open><summary>hyperv · constructd 1.14.2</summary>
      <div class="caps">${[['Child VMs (Windows, Linux)', 'yes'], ['Memory-pressure save', 'yes'], ['Idle policy save / shut down', 'yes'], ['Host forwards on LAN', 'yes'], ['Live vCPU resize', 'no · restart'], ['Live RAM resize', 'no · restart'], ['vTPM baselines', 'yes'], ['Public VMs', 'yes'], ['Windows licence reuse', 'opt-in']].map(([k, v]) => `<div>${k}</div><div class="mono t1" style="text-align:right">${v}</div><div></div>`).join('')}</div></details></div>
  </div></div>`;
  const st = $('#stage', body); if (st) st.addEventListener('click', () => { HOST.updateStaged = true; renderService(body); });
  const un = $('#unstage', body); if (un) un.addEventListener('click', () => { HOST.updateStaged = false; renderService(body); });
}

/* ── Config ─────────────────────────────────────────────────────────── */
function renderConfig(body) {
  body.innerHTML = `<div class="view"><div class="set-body" style="padding:18px 22px 90px;overflow:visible" id="cfgBody">
    <div class="section-title"><h2>Host policy</h2><p>Every change shows its effect before you save.</p></div>
    <section class="set-sec"><header><h3>Capacity</h3></header><div class="set-rows">
      <div class="set-row"><div><div class="l">Mode</div><div class="d">observe logs over-commit; enforce refuses starts above the admission line</div></div><div class="ctl"><button class="chip" aria-pressed="false" data-c>observe</button><button class="chip" aria-pressed="true" data-c>enforce</button></div></div>
      <div class="set-row"><div><div class="l">Admission line</div><div class="d">resident RAM above which new starts wait</div></div><div class="ctl input-unit"><input class="input num" style="width:70px" value="118" data-c><span>GB of 128</span></div></div></div>
      <div class="cfg-preview" id="pvCap">Now: <b>96 GB</b> resident, <b>22 GB</b> headroom.</div></section>
    <section class="set-sec"><header><h3>Memory pressure</h3></header><div class="set-rows">
      <div class="set-row"><div><div class="l">Elevated at</div></div><div class="ctl input-unit"><input class="input num" style="width:70px" value="72" data-c><span>% resident</span></div></div>
      <div class="set-row"><div><div class="l">Save idle VMs after</div><div class="d">while pressure is elevated</div></div><div class="ctl input-unit"><input class="input num" style="width:70px" value="30" data-c id="pressIdle"><span>min idle</span></div></div></div>
      <div class="cfg-preview" id="pvPress">With these values: <b>1 VM</b> would be saved next (alice/bench, 12 GB, in 28 min).</div></section>
    <section class="set-sec"><header><h3>Idle defaults &amp; caps</h3></header><div class="set-rows">
      <div class="set-row"><div><div class="l">Default idle action</div></div><div class="ctl"><div class="input-unit"><input class="input num" style="width:64px" value="60" data-c><span>min →</span></div><select class="input" style="width:120px" data-c><option>save</option><option>shut down</option></select></div></div>
      <div class="set-row"><div><div class="l">Longest idle a user may choose</div></div><div class="ctl input-unit"><input class="input num" style="width:64px" value="240" data-c><span>min</span></div></div></div></section>
    <section class="set-sec"><header><h3>User defaults &amp; caps</h3></header><div class="set-rows">
      <div class="set-row"><div><div class="l">Role default · dev</div></div><div class="ctl mono t2" style="font-size:var(--fs-11)">2 primaries · 2 children · 8 vCPU · 24 GB <button class="btn xs ghost">Edit</button></div></div>
      <div class="set-row"><div><div class="l">Role default · contractor</div></div><div class="ctl mono t2" style="font-size:var(--fs-11)">1 · 1 · 4 vCPU · 16 GB <button class="btn xs ghost">Edit</button></div></div>
      <div class="set-row"><div><div class="l">Hard cap per person</div></div><div class="ctl mono t2" style="font-size:var(--fs-11)">16 vCPU · 48 GB <button class="btn xs ghost">Edit</button></div></div></div></section>
    <details class="adv"><summary>Raw configuration (JSON, 10 sections) · advanced</summary>
      <textarea class="input" rows="8" style="margin-top:6px">{\n  "capacity": { "mode": "enforce", "admissionGB": 118 },\n  "memoryPressure": { "elevatedPct": 72, "saveIdleMinutes": 30 },\n  "idle": { "defaultMinutes": 60, "action": "save", "maxMinutes": 240 }\n}</textarea></details>
    <div class="savebar hidden" id="cfgSave"><span class="dotw"></span>Unsaved policy changes · affect running VMs at the next check (≤ 60 s)<button class="btn sm ghost" id="cfgDiscard">Discard</button><button class="btn sm primary" id="cfgSaveBtn">Save policy</button></div>
  </div></div>`;
  const show = () => $('#cfgSave').classList.remove('hidden');
  body.querySelectorAll('input[data-c], select[data-c]').forEach((i) => i.addEventListener('input', () => { i.classList.add('changed'); show(); }));
  body.querySelectorAll('button[data-c]').forEach((b) => b.addEventListener('click', () => { b.parentElement.querySelectorAll('button').forEach((x) => x.setAttribute('aria-pressed', x === b)); show(); $('#pvCap').innerHTML = b.textContent === 'observe' ? 'Observe: starts above <b>118 GB</b> are logged, not refused.' : 'Now: <b>96 GB</b> resident, <b>22 GB</b> headroom.'; }));
  $('#pressIdle').addEventListener('input', (e) => { const v = +e.target.value || 0; $('#pvPress').innerHTML = v <= 15 ? 'With these values: <b>2 VMs</b> would be saved now (alice/bench 12 GB, bob/win-qa 8 GB), freeing <b>20 GB</b>.' : v >= 120 ? 'With these values: <b>0 VMs</b> qualify; bench is idle 2h (120 min) — borderline.' : `With these values: <b>1 VM</b> would be saved next (alice/bench, 12 GB, in ${Math.max(0, v - 2)} min).`; });
  $('#cfgDiscard').addEventListener('click', () => renderConfig(body));
  $('#cfgSaveBtn').addEventListener('click', () => { renderConfig(body); toast($('#hwin'), { title: 'Policy saved', msg: 'Audit entry written. Applies at the next policy check.', icon: 'ok' }); });
}
