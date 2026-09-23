/* Router, scenario bar and design notes. */
'use strict';

const NOTES = {
  popup: `
    <h3>Tray popup · agents first</h3>
    <p>Why this layout: the question you open the tray with is “is Claude still working, or done?”, not “is the VM up?”. So agent cards take the middle of the popup, and the machine shrinks to one status line.</p>
    <h4>Glanceable (&lt; 1 s)</h4>
    <ul><li>State of <b>every</b> VM in the chip row (dot + “2 behind” / “saved” / lease)</li>
    <li>Which agent is working: bright pulse, repo, task, minutes</li>
    <li>Open forwards (bright dot) and who opened them</li>
    <li>Mic armed, today's cost, disk warning over 85%</li>
    <li>Unread notify count</li></ul>
    <h4>One click</h4>
    <ul><li>🔔 per agent: a toast when that agent goes idle, with no agent-side <span class="mono">notify</span> needed</li>
    <li>“Behind host” badge → reprovision now or <b>after Claude finishes</b></li>
    <li>Open target split button (VS Code / T3 / console / SSH), remembered</li>
    <li>Inbox item → marks read; ⋯ → Control panel tabs, Host admin</li></ul>
    <h4>Deliberately absent</h4>
    <ul><li>Reinstall, Redownload, Remove: they live only in the Control panel's Danger zone</li></ul>
    <h4>Colour</h4>
    <p>Bright <span style="color:var(--live)">#37ff8b</span> appears only on live things: the working pulse, open forwards, the armed mic, an armed bell. Agent identity uses a validated 4-hue set (adjacent CVD ΔE ≥ 8), always next to a name, never alone.</p>
    <h4>Try</h4>
    <ul><li>Scenario bar: “dev-2 saved” shows the empty/hero state (the only place with code rain), “Claude finishes” fires the wake-me toast</li>
    <li>Hover the tray right-click menu: Switch instance and Forwards open submenus</li></ul>`,
  panel: `
    <h3>Control panel · mission layout</h3>
    <p>Three columns, read left to right as machine → agents → detail. The centre is the product's reason to exist: what each agent did today.</p>
    <h4>Left: machine</h4>
    <ul><li>Instance list with state, “behind” and lease for every VM</li>
    <li>Vitals with honest meters and “sampled 20 s ago”</li>
    <li>Reprovision with the agent-busy caveat and “after Claude”</li>
    <li>Power (UAC warning), mic, browser console</li></ul>
    <h4>Centre: agent lanes</h4>
    <ul><li>One lane per agent on a shared 08:00 → now axis. Bars = turns, ◆ = notify (red = error), ▢ = forward opened. Hover any mark.</li>
    <li>Forwards table: the one authoritative list, with remaps and queued host forwards</li>
    <li>Activity feed with filters; it keeps notify history, which Windows toasts don't</li></ul>
    <h4>Right: inspector</h4>
    <ul><li>Click a lane: version/update (waits for the turn), cost today, repo, its forwards and notifies, wake-me bell</li></ul>
    <h4>Tabs</h4>
    <ul><li><b>Projects</b>: profile list with next-reprovision checkboxes, a single-form editor, config sync and remote repos</li>
    <li><b>Usage</b>: tiles plus 7-day stacked bars per agent (y axis from $0, totals labelled, table toggle)</li>
    <li><b>Settings</b>: every row tagged <i>live</i> / <i>restart</i> / <i>next reprovision</i> / <i>reinstall</i>; the save bar spells out what happens</li>
    <li><b>Danger zone</b>: reinstall, redownload, custom reinstall, remove, share-as-host. Each shows an impact preview and asks you to type the name.</li></ul>`,
  host: `
    <h3>Host panel · Roster</h3>
    <p>For a team lead the unit of work is a person: “can Alice have more RAM?”, “offboard Mara”. The roster answers those at a glance, and host-wide concerns get their own tabs.</p>
    <h4>Glanceable</h4>
    <ul><li>Host strip on every tab: health, capacity mode, memory pressure, resident/committed RAM</li>
    <li>Attention chips: each one links to its fix (retry, person, licences)</li>
    <li>Per person: bullet bars (used · allowance tick · role-default tick; amber at limit, hatched = unlimited), month cost + daily sparkline, VM chips by state, flags</li></ul>
    <h4>One click</h4>
    <ul><li>Card → drawer with <b>one form and one save</b>: role, status, allowance (templates), per-VM overrides, tokens (shown once)</li>
    <li>Onboard wizard: register → template → token</li>
    <li>Offboard: disable → cascade preview (VMs, shared children, keys, tokens) → typed confirm</li></ul>
    <h4>Tabs</h4>
    <ul><li><b>Capacity</b>: RAM chart on a single GB axis (physical line, admission line, committed over-commit), VM table with filters and row menus, idle list, lease timeline</li>
    <li><b>Jobs &amp; audit</b>: retrying a failed job turns it into a resolved audit entry</li>
    <li><b>Media &amp; licences</b>: MAK activation cells, guests, retained identities, the ISO catalog</li>
    <li><b>Service</b>: shows blocking jobs before apply; recovery is folded away</li>
    <li><b>Config</b>: typed policy forms with an effect preview; raw JSON kept for experts</li></ul>
    <h4>Data gap</h4>
    <p>Sparklines need daily usage buckets from the service. Today the API only returns today, month and all time.</p>`,
};

let currentSurface = null;
function route() {
  const [surface, sub] = (location.hash.replace('#', '') || 'popup').split('/');
  const s = ['popup', 'panel', 'host'].includes(surface) ? surface : 'popup';
  currentSurface = s;
  closeMenu();
  $$('.switcher a').forEach((a) => a.setAttribute('aria-current', a.dataset.surface === s ? 'page' : 'false'));
  const stage = $('#stage');
  if (s === 'popup') renderPopupSurface(stage);
  if (s === 'panel') renderPanelSurface(stage, sub);
  if (s === 'host') renderHostSurface(stage, sub);
  $('#notesBody').innerHTML = NOTES[s];
  refreshScenario();
}

function refreshScenario() {
  const bar = $('#scenario');
  const list = currentSurface === 'popup' ? popupScenario() : currentSurface === 'panel' ? panelScenario() : hostScenario();
  bar.innerHTML = `<span class="lbl">States</span>${list.map((x, i) => `<button data-sc="${i}" aria-pressed="${!!x.active}">${x.label}</button>`).join('')}
    <span class="spacer"></span><span>Mock time: Wed 23 Sep 2026, 14:18 · all data is fake</span>`;
  bar.querySelectorAll('[data-sc]').forEach((b) => b.addEventListener('click', () => {
    list[+b.dataset.sc].run();
    const stage = $('#stage');
    if (currentSurface === 'popup') renderPopupSurface(stage);
    if (currentSurface === 'panel') renderPanelSurface(stage);
    if (currentSurface === 'host') renderHostSurface(stage);
    refreshScenario();
  }));
}

$('#notesToggle').addEventListener('click', () => {
  const n = $('#notes');
  n.classList.toggle('open');
  $('#notesToggle').setAttribute('aria-expanded', n.classList.contains('open'));
  if (currentSurface === 'panel' && PANEL.tab === 'mission') setTimeout(drawLanes, 220);
});
document.addEventListener('keydown', (e) => {
  if (e.target.matches('input, textarea, select')) return;
  if (e.key === '1') location.hash = '#popup';
  if (e.key === '2') location.hash = '#panel';
  if (e.key === '3') location.hash = '#host';
});
addEventListener('hashchange', route);

/* Zoom the mockup down when the stage is narrower than the 1280 px window (e.g. notes open). */
function fitStage() {
  const stage = $('#stage');
  const el = stage.firstElementChild;
  if (!el) return;
  el.style.zoom = 1;
  const avail = stage.clientWidth - 40;
  const z = Math.min(1, avail / el.offsetWidth);
  el.style.zoom = z < 0.999 ? z.toFixed(3) : 1;
}
addEventListener('resize', fitStage);
new MutationObserver(fitStage).observe($('#stage'), { childList: true });
$('#notes').addEventListener('transitionend', fitStage);
if (new URLSearchParams(location.search).has('notes')) { $('#notes').style.transition = 'none'; $('#notes').classList.add('open'); $('#notesToggle').setAttribute('aria-expanded', 'true'); }
route();
/* ?state=N pre-selects a scenario (used for screenshots and deep links). */
const preState = new URLSearchParams(location.search).get('state');
if (preState != null) { const b = $(`[data-sc="${preState}"]`); if (b) b.click(); }
