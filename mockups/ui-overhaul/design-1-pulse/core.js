/* Construct - Design 1 "Pulse" : shared icons, sample data, shell, helpers */
"use strict";

/* ---------------- icons (24px grid, stroke) ---------------- */
const ICONS = {
  logo: '<path d="M12 2.8 20 7.4v9.2l-8 4.6-8-4.6V7.4z"/><path d="M12 12 20 7.4M12 12 4 7.4M12 12v9.2"/>',
  code: '<path d="m8.5 7-5 5 5 5M15.5 7l5 5-5 5M13.5 4.5l-3 15"/>',
  terminal: '<rect x="3" y="4.5" width="18" height="15" rx="2"/><path d="m7 9.5 3 2.5-3 2.5M12.5 15h4.5"/>',
  globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.5 2.6 3.6 5.3 3.6 8.5S14.5 17.9 12 20.5C9.5 17.9 8.4 15.2 8.4 12S9.5 6.1 12 3.5z"/>',
  window: '<rect x="3" y="4.5" width="18" height="15" rx="2"/><path d="M3 9h18"/>',
  panel: '<rect x="3" y="4.5" width="18" height="15" rx="2"/><path d="M9 4.5v15"/>',
  dots: '<circle cx="6" cy="12" r="1.2" fill="currentColor"/><circle cx="12" cy="12" r="1.2" fill="currentColor"/><circle cx="18" cy="12" r="1.2" fill="currentColor"/>',
  bell: '<path d="M6.5 16.5V11a5.5 5.5 0 0 1 11 0v5.5l1.5 2H5z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>',
  mic: '<rect x="9" y="3.5" width="6" height="11" rx="3"/><path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v2.5"/>',
  play: '<path d="M7.5 5v14l11-7z"/>',
  pause: '<path d="M8.5 5.5v13M15.5 5.5v13"/>',
  power: '<path d="M12 3.5V11"/><path d="M7.2 6.5a7 7 0 1 0 9.6 0"/>',
  restart: '<path d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3L4.5 9"/><path d="M4.5 4.5V9H9"/>',
  save: '<path d="M5 4.5h11l3 3V19a.5.5 0 0 1-.5.5h-13A.5.5 0 0 1 5 19z"/><path d="M8.5 4.5v4h6v-4M8 19.5v-5h8v5"/>',
  ext: '<path d="M13.5 4.5h6v6M19.5 4.5 11 13"/><path d="M17 14v4.5a1 1 0 0 1-1 1H5.5a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1H10"/>',
  chevd: '<path d="m6 9.5 6 6 6-6"/>',
  chevr: '<path d="m9.5 6 6 6-6 6"/>',
  chevl: '<path d="m14.5 6-6 6 6 6"/>',
  check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  warn: '<path d="M12 4 21 19.5H3z"/><path d="M12 10v4.5M12 17.2v.3"/>',
  err: '<circle cx="12" cy="12" r="8.5"/><path d="m9 9 6 6M15 9l-6 6"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5M12 7.8v.3"/>',
  okc: '<circle cx="12" cy="12" r="8.5"/><path d="m8.2 12.3 2.6 2.6 5-5.2"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
  timer: '<circle cx="12" cy="13" r="7.5"/><path d="M12 9v4l2.5 1.5M9.5 2.5h5"/>',
  bolt: '<path d="M13 3 5 13.5h6L10 21l8-10.5h-6z"/>',
  refresh: '<path d="M19.5 12a7.5 7.5 0 0 1-13.2 4.9M4.5 12a7.5 7.5 0 0 1 13.2-4.9"/><path d="M18 3.5v3.8h-3.8M6 20.5v-3.8h3.8"/>',
  cpu: '<rect x="6.5" y="6.5" width="11" height="11" rx="1.5"/><path d="M9.5 3.5v3M14.5 3.5v3M9.5 17.5v3M14.5 17.5v3M3.5 9.5h3M3.5 14.5h3M17.5 9.5h3M17.5 14.5h3"/>',
  ram: '<rect x="3" y="7" width="18" height="9" rx="1.5"/><path d="M7 10v3M11 10v3M15 10v3M6 16v2.5M18 16v2.5"/>',
  disk: '<ellipse cx="12" cy="6.5" rx="7.5" ry="3"/><path d="M4.5 6.5v11c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3v-11M4.5 12c0 1.7 3.4 3 7.5 3s7.5-1.3 7.5-3"/>',
  cloud: '<path d="M7 18.5a4.5 4.5 0 0 1-.5-9 6 6 0 0 1 11.5 1.5 3.8 3.8 0 0 1-.5 7.5z"/>',
  server: '<rect x="4" y="4" width="16" height="7" rx="1.5"/><rect x="4" y="13" width="16" height="7" rx="1.5"/><path d="M7.5 7.5h.01M7.5 16.5h.01"/>',
  user: '<circle cx="12" cy="8.5" r="4"/><path d="M4.5 20a7.5 7.5 0 0 1 15 0"/>',
  users: '<circle cx="9" cy="8.5" r="3.5"/><path d="M2.5 19.5a6.5 6.5 0 0 1 13 0M16 5.2a3.5 3.5 0 0 1 0 6.6M18 13.8a6.5 6.5 0 0 1 3.5 5.7"/>',
  dollar: '<path d="M12 3v18M16.5 7.5c-.7-1.4-2.5-2.2-4.5-2.2-2.5 0-4.3 1.3-4.3 3.1 0 4.3 9 2.3 9 6.8 0 1.9-2 3.3-4.7 3.3-2.1 0-3.9-.8-4.6-2.4"/>',
  chart: '<path d="M4 20h16M7 20v-6M12 20V8M17 20v-9"/>',
  shield: '<path d="M12 3.5 19 6v5.5c0 4.5-3 7.7-7 9-4-1.3-7-4.5-7-9V6z"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="m10.8 12.2 8.7-8.7M16.5 6.5l2.5 2.5M14 9l2 2"/>',
  folder: '<path d="M3.5 6.5a1 1 0 0 1 1-1h5l2 2h8a1 1 0 0 1 1 1V18a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1z"/>',
  branch: '<circle cx="7" cy="5.5" r="2"/><circle cx="7" cy="18.5" r="2"/><circle cx="17" cy="8.5" r="2"/><path d="M7 7.5v9M17 10.5c0 4-6 3-9.2 6.5"/>',
  sync: '<path d="M4.5 10a7.5 7.5 0 0 1 13.6-3.2L20 9M19.5 14a7.5 7.5 0 0 1-13.6 3.2L4 15"/><path d="M20 4.5V9h-4.5M4 19.5V15h4.5"/>',
  trash: '<path d="M4.5 6.5h15M9.5 6.5V4.5h5v2M6.5 6.5l1 13h9l1-13M10 10.5v5.5M14 10.5v5.5"/>',
  home: '<path d="M4 10.5 12 4l8 6.5V19a1 1 0 0 1-1 1h-4.5v-5.5h-5V20H5a1 1 0 0 1-1-1z"/>',
  grid: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>',
  spark: '<path d="M12 3.5c.6 4.2 2.3 5.9 6.5 6.5-4.2.6-5.9 2.3-6.5 6.5-.6-4.2-2.3-5.9-6.5-6.5 4.2-.6 5.9-2.3 6.5-6.5zM18.5 15.5c.3 1.9 1 2.6 2.5 3-1.5.4-2.2 1.1-2.5 3-.3-1.9-1-2.6-2.5-3 1.5-.4 2.2-1.1 2.5-3z"/>',
  fwd: '<path d="M4 8h13.5M14 4.5 17.5 8 14 11.5M20 16H6.5M10 12.5 6.5 16l3.5 3.5"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 3.5v2.2M12 18.3v2.2M20.5 12h-2.2M5.7 12H3.5M18 6l-1.6 1.6M7.6 16.4 6 18M18 18l-1.6-1.6M7.6 7.6 6 6"/>',
  search: '<circle cx="10.5" cy="10.5" r="6"/><path d="m15 15 5 5"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.8v2M12 19.2v2M21.2 12h-2M4.8 12h-2M18.5 5.5l-1.4 1.4M6.9 17.1l-1.4 1.4M18.5 18.5l-1.4-1.4M6.9 6.9 5.5 5.5"/>',
  moon: '<path d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z"/>',
  auto: '<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5v17a8.5 8.5 0 0 0 0-17z" fill="currentColor" stroke="none"/>',
  list: '<path d="M8.5 6.5h11M8.5 12h11M8.5 17.5h11M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01"/>',
  sliders: '<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>',
  upd: '<circle cx="12" cy="12" r="8.5"/><path d="M12 16.5v-9M8.5 11 12 7.5l3.5 3.5"/>',
  pulse: '<path d="M3 12h4l2.5-6 5 12 2.5-6h4"/>',
  min: '<path d="M6 12h12"/>',
  max: '<rect x="6" y="6" width="12" height="12" rx="1.5"/>',
  copy: '<rect x="8.5" y="8.5" width="11" height="11" rx="1.5"/><path d="M15.5 8.5V5.5a1 1 0 0 0-1-1h-9a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h3"/>',
  lock: '<rect x="5" y="10.5" width="14" height="10" rx="1.5"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5"/>',
  disc: '<circle cx="12" cy="12" r="8.5"/><circle cx="12" cy="12" r="2.5"/>',
  child: '<rect x="3.5" y="4" width="10" height="7" rx="1.5"/><rect x="10.5" y="13" width="10" height="7" rx="1.5"/><path d="M7 11v5.5h3.5"/>',
  audit: '<path d="M6 3.5h9l4 4V20a.5.5 0 0 1-.5.5h-12.5A.5.5 0 0 1 5.5 20V4a.5.5 0 0 1 .5-.5z"/><path d="M9 11h7M9 14.5h7M9 18h4"/>',
  danger: '<path d="M12 3.5 21 19.5H3z"/><path d="M12 10v4.5M12 17.2v.3"/>',
  export: '<path d="M12 15V3.5M7.5 8 12 3.5 16.5 8"/><path d="M5 13.5V19a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-5.5"/>',
  import: '<path d="M12 3.5V15M7.5 10.5 12 15l4.5-4.5"/><path d="M5 13.5V19a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-5.5"/>',
  share: '<circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="6" r="2.5"/><circle cx="18" cy="18" r="2.5"/><path d="m8.2 10.8 7.6-3.6M8.2 13.2l7.6 3.6"/>',
  edit: '<path d="M4.5 19.5 5.3 15.7 16 5a2 2 0 0 1 2.9 2.9L8.3 18.7z"/><path d="M14 7l3 3"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
  wifi: '<path d="M3 9.5a13 13 0 0 1 18 0M6 12.8a8.5 8.5 0 0 1 12 0M9 16a4 4 0 0 1 6 0"/><circle cx="12" cy="19" r=".8" fill="currentColor"/>',
  vol: '<path d="M4.5 9.5h3l4.5-4v13l-4.5-4h-3z"/><path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11"/>',
  batt: '<rect x="3" y="7.5" width="16" height="9" rx="2"/><path d="M21 10.5v3"/><rect x="5" y="9.5" width="10" height="5" rx="1" fill="currentColor" stroke="none"/>',
  win: '<path d="M4 4h7.5v7.5H4zM12.5 4H20v7.5h-7.5zM4 12.5h7.5V20H4zM12.5 12.5H20V20h-7.5z" fill="currentColor" stroke="none"/>',
  upc: '<path d="m6 14.5 6-6 6 6"/>',
  pin: '<path d="M9 3.5h6l-1 6 3 3.5H7l3-3.5z"/><path d="M12 13v7.5"/>',
  hourglass: '<path d="M6.5 3.5h11M6.5 20.5h11M7.5 3.5c0 5 9 5 9 8.5s-9 3.5-9 8.5M16.5 3.5c0 5-9 5-9 8.5s9 3.5 9 8.5"/>',
};
function ic(name, cls = "") { return `<svg class="i ${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ""}</svg>`; }

/* ---------------- sample data (from research/sample-data.md) ---------------- */
const D = {
  pc: "PC-1",
  instances: {
    "agent-vm": { name: "agent-vm", backend: "Local Hyper-V", backendShort: "Local", host: null, cpu: 8, ram: 16, ramUsed: 11.2, disk: 146, diskPct: 88, os: "Ubuntu 26.04 LTS", uptime: "3h 12m", provisioned: "c21978b", installed: "b5c4348", latest: "3a91f0e", behind: 2, todayCost: 4.12 },
    "dev-2": { name: "dev-2", backend: "Remote · buildbox", backendShort: "buildbox", host: "buildbox.example.local:7462", cpu: 4, ram: 12, ramUsed: null, disk: 80, diskPct: 41, os: "Ubuntu 26.04 LTS", savedAgo: "41 min", idle: "Save after 60 min idle", todayCost: 0.62, behind: 0 },
    "win-test": { name: "win-test", parent: "agent-vm", kind: "Windows 11 eval", ram: 4, lease: "5h 20m" },
  },
  agents: [
    { id: "claude", name: "Claude Code", ver: "2.4.1", upd: "2.4.3", state: "working", repo: "construct", since: "6 min", task: "Refactoring forwarder retry backoff", cost: 3.40, tokens: "1.92M" },
    { id: "codex", name: "Codex", ver: "0.61.0", upd: null, state: "idle", last: "48 min ago", cost: 0.58, tokens: "412K" },
    { id: "opencode", name: "OpenCode", ver: "1.9.2", upd: null, state: "idle", serve: ":4096", cost: 0.14, tokens: "96K" },
    { id: "t3", name: "T3 Code", ver: "0.9.14-construct.3", upd: null, state: "ui", threads: 2, cost: 0, tokens: "via agents" },
  ],
  forwards: [
    { vm: 5173, pc: 5173, label: "vite dev server", scope: "client", state: "open", by: "Claude · 12 min ago" },
    { vm: 8080, pc: 18800, label: "api preview", scope: "client", state: "open", by: "Claude · 1 h ago", note: "8080 is busy on this PC" },
    { vm: 3000, pc: 3000, label: "", scope: "host", state: "queued", by: "Codex · 2 min ago", note: "host forward on buildbox LAN" },
  ],
  notifs: [
    { t: "14:02", lv: "info", msg: "Test suite finished — 3 failures", agent: "claude", repo: "construct" },
    { t: "13:40", lv: "error", msg: "Deploy failed, rolling back", agent: "codex", repo: "gitgudlab" },
    { t: "12:15", lv: "info", msg: "PR #31 opened", agent: "claude", repo: "omniloop" },
  ],
  projects: [
    { id: "construct", on: true, repos: 2, sdks: ".NET 10, Node 22", mcp: 2 },
    { id: "omniloop", on: true, repos: 1, sdks: "Node 22", mcp: 1 },
    { id: "gitgudlab", on: true, repos: 3, sdks: "Go 1.25, Node 22", mcp: 0 },
    { id: "emili-simulator", on: false, repos: 1, sdks: "Python 3.13", mcp: 0 },
    { id: "jarvis", on: true, repos: 1, sdks: "Python 3.13", mcp: 1, isNew: true },
  ],
  usage7: [ ["Thu 17", 9.8], ["Fri 18", 12.4], ["Sat 19", 6.1], ["Sun 20", 14.0], ["Mon 21", 11.3], ["Tue 22", 8.7], ["Wed 23", 4.12] ],
  update: { from: "b5c4348", to: "3a91f0e", commits: ["Default VM CPU allocation to the host allowance", "Initialize vTPM identity before capturing reusable VM baselines", "Add opt-in Hyper-V license machine reuse", "Clarify optional CPU sizing in the child VM design"] },
};

/* ---------------- tiny helpers ---------------- */
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const money = (v) => "$" + v.toFixed(2);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const when = (k) => ({
  now: `<span class="when now" title="Takes effect immediately">${ic("bolt")}Applies now</span>`,
  repro: `<span class="when repro" title="Saved now, takes effect at the next reprovision">${ic("refresh")}Next reprovision</span>`,
  restart: `<span class="when restart" title="The VM restarts to apply this">${ic("restart")}Restart to apply</span>`,
  reinstall: `<span class="when reinstall" title="Only a reinstall or redownload applies this">${ic("warn")}Reinstall only</span>`,
}[k]);
function sw(id, on, label = "") { return `<label class="switch">${label ? `<span>${label}</span>` : ""}<input type="checkbox" id="${id}" ${on ? "checked" : ""}><span class="track"></span></label>`; }

/* ---------------- shell: theme, routing, fit, notes ---------------- */
const App = {
  surface: "popup", sub: null, themePref: "auto", surfaces: {},
  register(name, mod) { this.surfaces[name] = mod; },
};

function resolvedTheme() {
  if (App.themePref !== "auto") return App.themePref;
  return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}
function applyTheme() {
  document.documentElement.dataset.theme = resolvedTheme();
  $$("#themeSeg button").forEach((b) => b.classList.toggle("on", b.dataset.t === App.themePref));
}
matchMedia("(prefers-color-scheme: dark)").addEventListener("change", applyTheme);

function setTheme(t) { App.themePref = t; try { localStorage.setItem("pulse-theme", t); } catch (e) {} applyTheme(); }

function parseHash() {
  const h = location.hash.replace(/^#/, "");
  const [surface, sub] = h.split("/");
  return { surface: ["popup", "panel", "host"].includes(surface) ? surface : "popup", sub: sub || null };
}
function go(surface, sub) {
  const h = "#" + surface + (sub ? "/" + sub : "");
  if (location.hash !== h) history.replaceState(null, "", h);
  route();
}
function route() {
  const { surface, sub } = parseHash();
  App.surface = surface; App.sub = sub;
  $$("#surfSeg button").forEach((b) => b.classList.toggle("on", b.dataset.s === surface));
  const mod = App.surfaces[surface];
  const fit = $("#fit");
  fit.innerHTML = mod.render(sub);
  mod.mount && mod.mount(fit, sub);
  $("#notesBody").innerHTML = NOTES[surface];
  fitStage();
}
window.addEventListener("hashchange", route);

function fitStage() {
  const stage = $("#stage"), fit = $("#fit"), box = $("#fitBox");
  const w = fit.firstElementChild ? fit.firstElementChild.offsetWidth : 1280;
  const h = fit.firstElementChild ? fit.firstElementChild.offsetHeight : 800;
  const aw = stage.clientWidth - 32, ah = stage.clientHeight - 32;
  const s = Math.min(1, aw / w, ah / h);
  fit.style.transform = `scale(${s})`;
  box.style.width = w * s + "px"; box.style.height = h * s + "px";
  App.scale = s;
}
window.addEventListener("resize", fitStage);

/* re-render the current surface keeping local state (surfaces keep their own state objects) */
function rerender() { const mod = App.surfaces[App.surface]; const fit = $("#fit"); fit.innerHTML = mod.render(App.sub); mod.mount && mod.mount(fit, App.sub); }

/* ---------------- shared overlay helpers (scoped to a container) ---------------- */
function closeMenus() { $$(".menu[data-float]").forEach((m) => m.remove()); }
document.addEventListener("click", (e) => {
  if (!e.target.closest(".menu") && !e.target.closest("[data-menu]")) closeMenus();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    if ($(".menu[data-float]")) return closeMenus();
    const esc = $("[data-esc]"); if (esc) esc.click();
  }
});
/* open a menu anchored to el, inside container (position relative) */
function openMenu(anchor, container, html, align = "left") {
  closeMenus();
  const m = document.createElement("div");
  m.className = "menu"; m.dataset.float = "1"; m.setAttribute("role", "menu"); m.innerHTML = html;
  container.appendChild(m);
  const s = App.scale || 1;
  const cr = container.getBoundingClientRect(), ar = anchor.getBoundingClientRect();
  let top = (ar.bottom - cr.top) / s + 4 + container.scrollTop;
  let left = align === "right" ? (ar.right - cr.left) / s - m.offsetWidth : (ar.left - cr.left) / s;
  if (top + m.offsetHeight > container.scrollHeight - 4) top = (ar.top - cr.top) / s - m.offsetHeight - 4 + container.scrollTop;
  m.style.top = Math.max(4, top) + "px"; m.style.left = Math.max(4, left) + "px";
  const first = m.querySelector(".mi"); first && first.focus({ preventScroll: true });
  return m;
}
function toast(container, msg, icon = "okc") {
  $$(".toast", container).forEach((t) => t.remove());
  const t = document.createElement("div"); t.className = "toast"; t.innerHTML = ic(icon) + `<span>${msg}</span>`;
  container.appendChild(t); setTimeout(() => t.remove(), 3600);
}
/* chart tooltip */
function tipAt(container, e, html) {
  let t = $(".tip", container);
  if (!t) { t = document.createElement("div"); t.className = "tip"; container.appendChild(t); }
  t.innerHTML = html;
  const s = App.scale || 1, cr = container.getBoundingClientRect();
  t.style.left = (e.clientX - cr.left) / s + 12 + container.scrollLeft + "px";
  t.style.top = (e.clientY - cr.top) / s - 36 + container.scrollTop + "px";
}
function tipOff(container) { $$(".tip", container).forEach((t) => t.remove()); }

/* ---------------- a single-series column chart (cost per day) ---------------- */
function columnChart(data, { w = 420, h = 150, fmt = money, cur = data.length - 1 } = {}) {
  const pad = { l: 34, r: 8, t: 12, b: 22 };
  const max = Math.ceil(Math.max(...data.map((d) => d[1])) / 5) * 5;
  const iw = w - pad.l - pad.r, ih = h - pad.t - pad.b, band = iw / data.length, bw = Math.min(24, band * 0.56);
  let g = "";
  for (let v = 0; v <= max; v += 5) { const y = pad.t + ih - (v / max) * ih; g += `<line class="gl" x1="${pad.l}" x2="${w - pad.r}" y1="${y}" y2="${y}"/><text class="ax" x="${pad.l - 6}" y="${y + 4}" text-anchor="end">$${v}</text>`; }
  data.forEach(([lbl, v], i) => {
    const x = pad.l + i * band + (band - bw) / 2, bh = (v / max) * ih, y = pad.t + ih - bh, r = Math.min(4, bh);
    const path = `M${x},${pad.t + ih} V${y + r} Q${x},${y} ${x + r},${y} H${x + bw - r} Q${x + bw},${y} ${x + bw},${y + r} V${pad.t + ih} Z`;
    g += `<g class="col" data-tip="<b>${lbl}</b> · ${fmt(v)}${i === cur ? " (so far)" : ""}"><rect class="hit" x="${pad.l + i * band}" y="${pad.t}" width="${band}" height="${ih}"/><path class="bar ${i === cur ? "cur" : ""}" d="${path}"/><text class="ax" x="${pad.l + i * band + band / 2}" y="${h - 6}" text-anchor="middle">${lbl.split(" ")[0]}</text></g>`;
  });
  const lastX = pad.l + cur * band + band / 2, lastY = pad.t + ih - (data[cur][1] / max) * ih;
  g += `<text class="ax" x="${lastX}" y="${lastY - 6}" text-anchor="middle" style="fill:var(--text);font-weight:600">${fmt(data[cur][1])}</text>`;
  return `<div class="chart"><svg width="${w}" height="${h}" role="img" aria-label="Cost per day, last 7 days">${g}</svg></div>`;
}
function wireTips(root, container) {
  $$("[data-tip]", root).forEach((el) => {
    el.addEventListener("mousemove", (e) => tipAt(container, e, el.dataset.tip));
    el.addEventListener("mouseleave", () => tipOff(container));
  });
}

/* ---------------- design notes per surface ---------------- */
const NOTES = {
  popup: `
  <h2>Tray popup: one glance, one action</h2>
  <p>The popup answers "is it up, is the agent done, where's my link" in the first 200 px. Each instance gets one row: state dot, name, backend, uptime or "saved 41 min ago", and today's cost. Only the active VM expands.</p>
  <h3>Glanceable</h3>
  <ul><li>State of <em>every</em> VM, not just the active one</li><li>Agent working/idle, with the task line</li><li>Open forwards as chips, each with its own open button</li><li>Mic armed/live, unread notifications, behind-host and disk flags as badges</li><li>Today's cost</li></ul>
  <h3>One click deep</h3>
  <ul><li><b>Next action</b>: one button whose label follows state (Open in VS Code, Reprovision now, Resume dev-2, Start &amp; connect). The caret keeps the remembered open target.</li><li>The "Then" line queues a reprovision for when Claude finishes</li><li>⋯ Maintenance sheet: reprovision, updates, config sync, power</li></ul>
  <h3>Deliberately absent</h3>
  <p>Reinstall, redownload and remove aren't in the popup at all. The maintenance sheet links to the Danger zone in the control panel.</p>
  <h3>Try</h3>
  <ul><li>Switch the scenario (top right)</li><li>Click dev-2 to make it active</li><li>Arm "reprovision when Claude finishes"</li><li>Click the mic pill to cycle off / armed / live</li><li>Hover the tray menu's submenus</li></ul>`,
  panel: `
  <h2>Control panel: Windows Settings for one VM</h2>
  <p>It follows the Settings frame on purpose. The left nav is stable, the instance switcher sits on top where the account card would be, and each page is a stack of setting rows.</p>
  <h3>Overview = the popup, enlarged</h3>
  <p>The same hero with the same Next-action button, then status cards that each link to their page. It keeps a fixed layout: an empty Forwards card still shows up, so nothing reflows when an agent exposes a port.</p>
  <h3>"When does this apply?"</h3>
  <p>Every setting carries one tag: <b>Applies now</b>, <b>Restart to apply</b>, <b>Next reprovision</b> or <b>Reinstall only</b>. When you change a reprovision-time setting, a sticky bar counts the pending changes and offers "Reprovision now".</p>
  <h3>Deep</h3>
  <p>Danger zone holds reinstall, redownload, custom reinstall, remove and "share this PC as a host". Each one opens an impact preview built from real state (dirty repos, busy agent, backup used), then asks you to type the instance name.</p>
  <h3>Try</h3>
  <ul><li>Instance switcher → dev-2 (saved state)</li><li>Access &amp; services → flip a toggle</li><li>Danger zone → Reinstall</li><li>Projects → Edit a profile (drawer)</li><li>Usage → hover the bars</li></ul>`,
  host: `
  <h2>Host panel: Watchtower</h2>
  <p>Home is the <b>attention feed</b>, not a dashboard. Each card is one thing that is wrong, and its fix sits right on the card. Fixing it turns the card into a resolved line with who did it and when, so the feed doubles as the admin's work log (and it lands in Audit).</p>
  <h3>Glanceable</h3>
  <ul><li>Health, service version, capacity mode and memory pressure in the top strip</li><li>RAM bar with the admission line and the over-commit, sampled time shown</li><li>Running jobs with progress</li></ul>
  <h3>One click deep</h3>
  <ul><li>Every VM, person and key opens in a right-side <b>drawer</b>, so you keep your place in the list</li><li>The person drawer has one form and one Save for role, allowance and tokens</li><li>Policy edits preview their effect before you save</li></ul>
  <h3>Deep</h3>
  <p>Service update is a stepper. Before Apply it lists the jobs that block it, lets you choose between waiting and cancelling, and says that VMs keep running. Backend capabilities and raw JSON config sit in collapsed sections.</p>
  <h3>Try</h3>
  <ul><li>Retry the failed job, then look at "Resolved today"</li><li>Machines → click a row</li><li>People → alice</li><li>Service → Check, Stage, Apply</li></ul>`,
};

/* ---------------- boot ---------------- */
document.addEventListener("DOMContentLoaded", () => {
  const qs = new URLSearchParams(location.search);
  try { App.themePref = qs.get("theme") || localStorage.getItem("pulse-theme") || "auto"; } catch (e) { App.themePref = qs.get("theme") || "auto"; }
  applyTheme();
  $("#themeSeg").addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) setTheme(b.dataset.t); });
  $("#surfSeg").addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) go(b.dataset.s); });
  const notes = $("#notes");
  if (qs.get("notes") === "1") notes.classList.add("open");
  notes.addEventListener("transitionend", (e) => { if (e.propertyName === "width") fitStage(); });
  $("#notesOpen").addEventListener("click", () => { notes.classList.add("open"); setTimeout(fitStage, 260); });
  $("#notesClose").addEventListener("click", () => { notes.classList.remove("open"); setTimeout(fitStage, 260); });
  route();
  setTimeout(fitStage, 300);
});
