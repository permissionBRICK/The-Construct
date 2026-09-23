/* Shared data, icons and UI helpers for Design 2 · Mission Control. */
'use strict';

const NOW = { hh: 14, mm: 18 }; // mock "now": 14:18, Wed 23 Sep 2026
const NOW_MIN = NOW.hh * 60 + NOW.mm;

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const hm = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const t = (s) => { const [h, m] = s.split(':').map(Number); return h * 60 + m; };
const money = (v) => '$' + v.toFixed(2);

/* ── Icons (24-grid stroke icons) ───────────────────────────────────── */
const ICON_PATHS = {
  bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 20a2 2 0 0 0 4 0"/>',
  bellOn: '<path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z" fill="currentColor" fill-opacity=".18"/><path d="M10 20a2 2 0 0 0 4 0"/><path d="M3 8a8 8 0 0 1 2.5-4.5M21 8a8 8 0 0 0-2.5-4.5"/>',
  mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21"/>',
  micOff: '<path d="M15 10V6a3 3 0 0 0-5.7-1.3M9 9v2a3 3 0 0 0 4.8 2.4M5.5 11a6.5 6.5 0 0 0 10.7 5M18.5 11a6.4 6.4 0 0 1-.6 2.7M12 17.5V21M3 3l18 18"/>',
  panel: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18M9 9v11"/>',
  more: '<circle cx="5" cy="12" r="1.2" fill="currentColor"/><circle cx="12" cy="12" r="1.2" fill="currentColor"/><circle cx="19" cy="12" r="1.2" fill="currentColor"/>',
  chevDown: '<path d="M6 9l6 6 6-6"/>',
  chevRight: '<path d="M9 6l6 6-6 6"/>',
  ext: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  play: '<path d="M7 5l12 7-12 7z"/>',
  power: '<path d="M12 3v8M6.3 6.3a8 8 0 1 0 11.4 0"/>',
  pause: '<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>',
  restart: '<path d="M4 12a8 8 0 1 0 2.4-5.7M4 4v5h5"/>',
  refresh: '<path d="M20 12a8 8 0 1 1-2.4-5.7M20 4v5h-5"/>',
  terminal: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9l3 3-3 3M13 15h4"/>',
  code: '<path d="M8 7l-5 5 5 5M16 7l5 5-5 5"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  warn: '<path d="M12 3l10 18H2z"/><path d="M12 10v4M12 17.5h.01"/>',
  error: '<circle cx="12" cy="12" r="9"/><path d="M9 9l6 6M15 9l-6 6"/>',
  ok: '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.5 2.5L16 9.5"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3"/>',
  trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
  shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/>',
  host: '<rect x="3" y="4" width="18" height="7" rx="1.5"/><rect x="3" y="13" width="18" height="7" rx="1.5"/><path d="M7 7.5h.01M7 16.5h.01"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6.5 6.5 0 0 1 3.5 6"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M16 7l3 3M14 9l2 2"/>',
  disk: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 14h18M7 17h.01"/>',
  chip: '<rect x="6" y="6" width="12" height="12" rx="1.5"/><path d="M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3"/>',
  up: '<path d="M12 19V5M6 11l6-6 6 6"/>',
  arrowRight: '<path d="M5 12h14M13 6l6 6-6 6"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4-4"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  sync: '<path d="M4 10a8 8 0 0 1 14-4l2 2M20 14a8 8 0 0 1-14 4l-2-2M20 4v4h-4M4 20v-4h4"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  fwd: '<path d="M4 12h11M11 7l5 5-5 5M20 5v14"/>',
  flag: '<path d="M5 21V4M5 4h11l-2 4 2 4H5"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  box: '<path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4M12 11v10"/>',
  lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  child: '<rect x="3" y="3" width="10" height="8" rx="1.5"/><rect x="11" y="13" width="10" height="8" rx="1.5"/><path d="M8 11v6h3"/>',
  spark: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5L18 18M6 18l2.5-2.5M15.5 8.5L18 6"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  table: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18M3 15h18M9 4v16"/>',
  chart: '<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
};
function ic(name, cls = 'ic') {
  return `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICON_PATHS[name] || ''}</svg>`;
}
const GLYPH = `<svg class="glyph" viewBox="0 0 20 20" aria-hidden="true"><rect x=".5" y=".5" width="19" height="19" rx="5" fill="#08130d" stroke="#2b5139"/><path d="M5.5 6v8M8.5 4.5v11M11.5 7v6M14.5 5.5v9" stroke="#5fc58a" stroke-width="1.3" stroke-linecap="round"/><circle cx="11.5" cy="7" r="1.4" fill="#37ff8b"/></svg>`;

/* ── Sample data (from research/sample-data.md) ─────────────────────── */
const AGENTS = {
  claude: { id: 'claude', name: 'Claude Code', short: 'CL', color: 'var(--a-claude)', hex: '#d95926', version: '2.4.1', latest: '2.4.3',
    state: 'working', repo: 'construct', branch: 'fix/forwarder-retry', task: 'Refactoring forwarder retry backoff', since: t('14:12'),
    cost: 3.40, tokens: '2.91M', turns: 9, endpoint: 'CLI + VS Code extension' },
  codex: { id: 'codex', name: 'Codex', short: 'CX', color: 'var(--a-codex)', hex: '#3987e5', version: '0.61.0', latest: null,
    state: 'idle', repo: 'gitgudlab', branch: 'main', task: 'Last: deploy pipeline retry', idleSince: t('13:30'),
    cost: 0.58, tokens: '0.64M', turns: 4, endpoint: 'app-server :4500' },
  opencode: { id: 'opencode', name: 'OpenCode', short: 'OC', color: 'var(--a-opencode)', hex: '#c98500', version: '1.9.2', latest: null,
    state: 'idle', repo: 'omniloop', branch: 'main', task: 'serve :4096 · idle', idleSince: t('13:12'),
    cost: 0.14, tokens: '0.19M', turns: 2, endpoint: 'serve :4096' },
  t3: { id: 't3', name: 'T3 Code', short: 'T3', color: 'var(--a-t3)', hex: '#d55181', version: '0.9.14-construct.3', latest: null,
    state: 'web', repo: '—', task: 'Web UI · 2 threads active', cost: null, tokens: '—', turns: null, endpoint: 'web UI :5177' },
};
const AGENT_ORDER = ['claude', 'codex', 'opencode', 't3'];

const TURNS = {
  claude: [['08:40', '09:25'], ['09:50', '10:40'], ['11:05', '11:30'], ['11:55', '12:15'], ['12:40', '13:10'], ['13:25', '14:02'], ['14:04', '14:09'], ['14:12', 'now']],
  codex: [['09:10', '10:05'], ['10:30', '11:45'], ['12:50', '13:30']],
  opencode: [['10:15', '10:35'], ['13:00', '13:12']],
  t3: [],
};
const T3_SESSIONS = [['09:02', 'now']];

const NOTIFIES = [
  { id: 'n1', time: '14:02', level: 'info', text: 'Test suite finished — 3 failures', agent: 'claude', repo: 'construct', read: false },
  { id: 'n2', time: '13:40', level: 'error', text: 'Deploy failed, rolling back', agent: 'codex', repo: 'gitgudlab', read: false },
  { id: 'n3', time: '12:15', level: 'info', text: 'PR #31 opened', agent: 'claude', repo: 'omniloop', read: true },
];

const FORWARDS = [
  { id: 'f1', vm: 5173, local: 5173, label: 'vite dev server', where: 'client', state: 'open', agent: 'claude', opened: '14:06', ago: '12 min ago' },
  { id: 'f2', vm: 8080, local: 18800, label: 'api preview', where: 'client', state: 'open', agent: 'codex', opened: '11:20', ago: '2 h 58 min ago', note: '8080 busy on this PC → remapped' },
  { id: 'f3', vm: 3000, local: null, label: 'host forward', where: 'host · buildbox LAN', state: 'queued', agent: 'opencode', opened: '13:05', ago: '1 h 13 min ago', note: 'waiting for host service' },
];

const INSTANCES = [
  { id: 'agent-vm', backend: 'hyperv-local', state: 'running', label: 'running · 3h 12m', vcpu: 8, ram: 16, ramUsed: 11.2, disk: 146, diskPct: 88,
    os: 'Ubuntu 26.04 LTS', provisioned: 'c21978b', installed: 'b5c4348', behind: 2 },
  { id: 'dev-2', backend: 'hyperv-remote', host: 'buildbox.example.local:7462', state: 'saved', label: 'saved 41 min ago · idle policy', vcpu: 4, ram: 12,
    os: 'Ubuntu 26.04 LTS', idle: 'save after 60 min' },
  { id: 'win-test', backend: 'child of agent-vm', state: 'running', label: 'child · lease 5h 20m left', ram: 4, os: 'Windows 11 eval', child: true },
];

const USAGE_7D = [
  { d: 'Thu 17', claude: 7.6, codex: 1.8, opencode: 0.4 },
  { d: 'Fri 18', claude: 9.9, codex: 2.1, opencode: 0.4 },
  { d: 'Sat 19', claude: 4.9, codex: 1.0, opencode: 0.2 },
  { d: 'Sun 20', claude: 11.2, codex: 2.3, opencode: 0.5 },
  { d: 'Mon 21', claude: 8.8, codex: 2.1, opencode: 0.4 },
  { d: 'Tue 22', claude: 6.9, codex: 1.5, opencode: 0.3 },
  { d: 'Wed 23', claude: 3.40, codex: 0.58, opencode: 0.14, today: true },
];

/* ── Tooltip ────────────────────────────────────────────────────────── */
const tipEl = () => $('#tip');
function showTip(html, x, y) {
  const el = tipEl();
  el.innerHTML = html;
  el.classList.add('show');
  const r = el.getBoundingClientRect();
  let left = x + 14, top = y + 14;
  if (left + r.width > innerWidth - 8) left = x - r.width - 14;
  if (top + r.height > innerHeight - 8) top = y - r.height - 14;
  el.style.left = left + 'px';
  el.style.top = top + 'px';
}
function hideTip() { tipEl().classList.remove('show'); }
document.addEventListener('mousemove', (e) => {
  const target = e.target.closest('[data-tip]');
  if (target) showTip(target.getAttribute('data-tip'), e.clientX, e.clientY);
  else hideTip();
});
document.addEventListener('focusin', (e) => {
  const target = e.target.closest('[data-tip]');
  if (target) { const r = target.getBoundingClientRect(); showTip(target.getAttribute('data-tip'), r.right, r.bottom); }
});
document.addEventListener('focusout', hideTip);

/* ── Menus ──────────────────────────────────────────────────────────── */
let openMenuEl = null;
function closeMenu() { if (openMenuEl) { openMenuEl.remove(); openMenuEl = null; } }
function openMenu(anchor, html, container, opts = {}) {
  closeMenu();
  const m = document.createElement('div');
  m.className = 'menu';
  m.setAttribute('role', 'menu');
  m.innerHTML = html;
  container.appendChild(m);
  const cr = container.getBoundingClientRect();
  const z = cr.width / container.offsetWidth || 1; // mockup may be zoomed to fit
  const ar = anchor.getBoundingClientRect();
  const mw = m.offsetWidth, mh = m.offsetHeight, cw = container.offsetWidth, ch = container.offsetHeight;
  let left = ((opts.alignRight ? ar.right : ar.left) - cr.left) / z - (opts.alignRight ? mw : 0);
  let top = (ar.bottom - cr.top) / z + 4;
  if (opts.above || top + mh > ch - 6) top = (ar.top - cr.top) / z - mh - 4;
  left = Math.max(6, Math.min(left, cw - mw - 6));
  m.style.left = left + 'px';
  m.style.top = top + 'px';
  openMenuEl = m;
  const first = m.querySelector('button');
  if (first) first.focus({ preventScroll: true });
  return m;
}
document.addEventListener('mousedown', (e) => {
  if (openMenuEl && !openMenuEl.contains(e.target) && !e.target.closest('[data-menu-anchor]')) closeMenu();
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (openMenuEl) { closeMenu(); return; }
    const scrim = $$('.scrim').pop();
    if (scrim) scrim.remove();
  }
});

/* ── Dialogs ────────────────────────────────────────────────────────── */
function openDialog(container, html, cls = '') {
  const s = document.createElement('div');
  s.className = 'scrim';
  s.innerHTML = `<div class="dialog ${cls}" role="dialog" aria-modal="true">${html}</div>`;
  container.appendChild(s);
  s.addEventListener('mousedown', (e) => { if (e.target === s) s.remove(); });
  s.addEventListener('click', (e) => { if (e.target.closest('[data-close]')) s.remove(); });
  const f = s.querySelector('input, select, .btn.primary, .btn');
  if (f) setTimeout(() => f.focus(), 30);
  return s;
}

/* Typed-name destructive confirm. impact: [{icon, cls, k, v}] */
function confirmDanger(container, { title, sub, impact, name, action, onConfirm, extra = '' }) {
  const s = openDialog(container, `
    <header><div class="danger-ic">${ic('warn')}</div><div><h2>${title}</h2><p>${sub}</p></div></header>
    <div class="dbody">
      <div>
        <div class="eyebrow" style="margin-bottom:6px">What happens</div>
        <div class="impact">${impact.map((i) => `<div class="row"><span class="${i.cls || 't3'}">${ic(i.icon)}</span><div><div class="k">${i.k}</div>${i.v ? `<div class="v">${i.v}</div>` : ''}</div>${i.r ? `<span class="tag ${i.rc || ''}">${i.r}</span>` : '<span></span>'}</div>`).join('')}</div>
      </div>
      ${extra}
      <div class="typed">
        <label for="typedName">Type <code>${esc(name)}</code> to confirm</label>
        <input id="typedName" class="input mono" autocomplete="off" spellcheck="false" placeholder="${esc(name)}">
      </div>
    </div>
    <footer><span class="left">Esc cancels. Nothing runs until you confirm.</span>
      <button class="btn ghost" data-close>Cancel</button>
      <button class="btn danger solid" id="dangerGo" disabled>${action}</button>
    </footer>`, 'wide');
  const inp = $('#typedName', s), go = $('#dangerGo', s);
  inp.addEventListener('input', () => { go.disabled = inp.value.trim() !== name; });
  inp.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !go.disabled) go.click(); });
  go.addEventListener('click', () => { s.remove(); onConfirm && onConfirm(); });
  return s;
}

/* ── Toasts ─────────────────────────────────────────────────────────── */
function toast(container, { title, msg, icon = 'info', cls = 't3', ms = 6000 }) {
  let stack = container.querySelector(':scope > .toast-stack');
  if (!stack) { stack = document.createElement('div'); stack.className = 'toast-stack'; container.appendChild(stack); }
  const el = document.createElement('div');
  el.className = 'toast';
  el.innerHTML = `<span class="${cls}">${ic(icon)}</span><div><div class="ttl">${title}</div><div class="msg">${msg}</div></div><button class="icon-btn" aria-label="Dismiss" style="width:22px;height:22px">${ic('x')}</button>`;
  el.querySelector('button').onclick = () => el.remove();
  stack.appendChild(el);
  if (ms) setTimeout(() => el.remove(), ms);
}

/* ── Faint code rain (hero / empty states only) ─────────────────────── */
const rains = new Set();
function codeRain(canvas) {
  const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const ctx = canvas.getContext('2d');
  const glyphs = 'アカサタナハマヤラ0123456789ｦｱｳｴｵｶｷｸｹｺ<>{}=+*'.split('');
  const size = 12;
  let cols, drops, w, h;
  function resize() {
    w = canvas.width = canvas.clientWidth * devicePixelRatio;
    h = canvas.height = canvas.clientHeight * devicePixelRatio;
    ctx.scale(devicePixelRatio, devicePixelRatio);
    cols = Math.ceil(canvas.clientWidth / size);
    drops = Array.from({ length: cols }, () => Math.random() * -40);
  }
  resize();
  function frame() {
    if (!canvas.isConnected) { rains.delete(canvas); return; }
    ctx.fillStyle = 'rgba(3, 9, 6, 0.16)';
    ctx.fillRect(0, 0, canvas.clientWidth, canvas.clientHeight);
    ctx.font = `${size}px Consolas, monospace`;
    for (let i = 0; i < cols; i++) {
      const ch = glyphs[(Math.random() * glyphs.length) | 0];
      const y = drops[i] * size;
      ctx.fillStyle = Math.random() > 0.96 ? 'rgba(55,255,139,.75)' : 'rgba(55,255,139,.28)';
      ctx.fillText(ch, i * size, y);
      if (y > canvas.clientHeight && Math.random() > 0.975) drops[i] = 0;
      drops[i] += 0.5;
    }
    if (!reduce && !warm) setTimeout(() => requestAnimationFrame(frame), 55);
  }
  let warm = true;
  for (let k = 0; k < 70; k++) frame();
  warm = false;
  if (!reduce) frame();
  rains.add(canvas);
}

/* Agent avatar */
function avatar(id, extra = '') {
  const a = AGENTS[id];
  return `<span class="agent-av" style="background:${a.color}">${a.short}${extra}</span>`;
}
function levelIcon(level) {
  if (level === 'error') return `<span class="crit" title="error">${ic('error')}</span>`;
  if (level === 'warn') return `<span class="warn" title="warning">${ic('warn')}</span>`;
  return `<span style="color:var(--info)" title="info">${ic('info')}</span>`;
}
