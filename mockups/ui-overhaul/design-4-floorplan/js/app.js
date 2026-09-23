/* Shared shell: icons, routing, theme, notes, scale-to-fit, dialogs, menus, toasts. */
(function () {
  const P = {
    pc: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
    vm: '<rect x="4" y="3" width="16" height="18" rx="3"/><path d="M8 7h8M8 11h8"/><circle cx="12" cy="16.5" r="1.2"/>',
    mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21"/>',
    ext: '<path d="M14 4h6v6M20 4l-9 9"/><path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    play: '<path d="M7 4.5v15l12-7.5z"/>',
    power: '<path d="M12 3v8"/><path d="M6.3 6.8a8 8 0 1 0 11.4 0"/>',
    lock: '<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>',
    unlock: '<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 7.6-1.7"/>',
    gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
    bell: '<path d="M6 8a6 6 0 0 1 12 0c0 7 3 8 3 8H3s3-1 3-8"/><path d="M10.3 20a1.9 1.9 0 0 0 3.4 0"/>',
    chev: '<path d="M6 9l6 6 6-6"/>',
    chevr: '<path d="M9 6l6 6-6 6"/>',
    chevl: '<path d="M15 6l-6 6 6 6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    x: '<path d="M6 6l12 12M18 6L6 18"/>',
    refresh: '<path d="M20 11a8 8 0 0 0-14.8-4M4 4v4h4"/><path d="M4 13a8 8 0 0 0 14.8 4M20 20v-4h-4"/>',
    term: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M7 9l3 3-3 3M12 15h5"/>',
    code: '<path d="M8 7l-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/>',
    cloud: '<path d="M7 18a4.5 4.5 0 0 1-.6-9A6 6 0 0 1 18 9.5a4.2 4.2 0 0 1-.5 8.5z"/>',
    server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    warn: '<path d="M12 4l9 16H3z"/><path d="M12 10v4M12 17h.01"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3M15 8l2 2"/>',
    user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
    users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6.5 6.5 0 0 1 3.5 6"/>',
    disk: '<ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6"/><path d="M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>',
    cpu: '<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4"/>',
    save: '<path d="M12 3v11M7 9.5l5 5 5-5"/><path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/>',
    moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    note: '<path d="M5 4h10l4 4v12H5z"/><path d="M14 4v5h5M8 13h8M8 17h5"/>',
    copy: '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/>',
    trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
    dots: '<circle cx="5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="19" cy="12" r="1.3"/>',
    arrowr: '<path d="M5 12h14M13 6l6 6-6 6"/>',
    child: '<path d="M6 3v8a4 4 0 0 0 4 4h8"/><path d="M15 12l3 3-3 3"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.2-4.2"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    zzz: '<path d="M4 8h6l-6 7h6M14 4h6l-6 7h6"/>',
    spark: '<path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z"/>',
    home: '<path d="M4 11l8-7 8 7v9H4z"/><path d="M10 20v-6h4v6"/>',
    box: '<path d="M21 8l-9-5-9 5 9 5z"/><path d="M3 8v8l9 5 9-5V8M12 13v8"/>',
    list: '<path d="M8 6h13M8 12h13M8 18h13M3.5 6h.01M3.5 12h.01M3.5 18h.01"/>',
    map: '<rect x="3" y="3" width="8" height="10" rx="1.5"/><rect x="13" y="3" width="8" height="6" rx="1.5"/><rect x="13" y="11" width="8" height="10" rx="1.5"/><rect x="3" y="15" width="8" height="6" rx="1.5"/>',
    wave: '<path d="M3 12h2M7 8v8M11 5v14M15 9v6M19 11v2"/>',
    layers: '<path d="M12 3l9 5-9 5-9-5z"/><path d="M3 13l9 5 9-5"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5M12 8v4l3 2"/>',
    sliders: '<path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
    windows: '<path d="M3 5.5l7.5-1v7H3zM12 4.3L21 3v8.5h-9zM3 12.5h7.5v7L3 18.5zM12 12.5h9V21l-9-1.3z"/>',
    wifi: '<path d="M2 8.5a15 15 0 0 1 20 0M5 12a10 10 0 0 1 14 0M8.5 15.5a5 5 0 0 1 7 0M12 19h.01"/>',
    vol: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
    up: '<path d="M6 15l6-6 6 6"/>',
    download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
    link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>'
  };
  window.ic = function (name, cls) { return '<svg class="i ' + (cls || '') + '" viewBox="0 0 24 24" aria-hidden="true">' + (P[name] || '') + '</svg>'; };
  window.$ = (s, r) => (r || document).querySelector(s);
  window.$$ = (s, r) => Array.from((r || document).querySelectorAll(s));
  window.money = v => '$' + v.toFixed(2);

  /* ---------- theme ---------- */
  const root = document.documentElement;
  function setTheme(t) {
    root.dataset.theme = t;
    const b = $('#themeBtn');
    if (b) b.innerHTML = ic(t === 'dark' ? 'sun' : 'moon') + (t === 'dark' ? 'Light' : 'Dark');
    try { localStorage.setItem('fp-theme', t); } catch (e) {}
    document.dispatchEvent(new CustomEvent('themechange'));
  }
  window.setTheme = setTheme;

  /* ---------- toasts ---------- */
  window.toast = function (msg, scope) {
    const host = (scope && scope.querySelector('.toasts')) || $('.surface.on .toasts');
    if (!host) return;
    const t = document.createElement('div');
    t.className = 'toast';
    t.innerHTML = msg;
    host.appendChild(t);
    setTimeout(() => { t.style.transition = 'opacity .3s'; t.style.opacity = '0'; }, 2600);
    setTimeout(() => t.remove(), 3000);
  };

  /* ---------- menus ---------- */
  window.openMenu = function (menu, anchor, opts) {
    closeMenus();
    menu.classList.add('open');
    const container = menu.offsetParent || menu.parentElement;
    const a = rel(anchor, container);
    const alignRight = opts && opts.right;
    menu.style.top = (opts && opts.above ? a.y - menu.offsetHeight - 6 : a.y + anchor.offsetHeight + 6) + 'px';
    menu.style.left = (alignRight ? a.x + anchor.offsetWidth - menu.offsetWidth : a.x) + 'px';
    const first = menu.querySelector('.mi');
    if (first) first.focus({ preventScroll: true });
  };
  window.closeMenus = function () { $$('.menu.open').forEach(m => m.classList.remove('open')); };
  document.addEventListener('click', e => {
    if (!e.target.closest('.menu') && !e.target.closest('[data-menu]')) closeMenus();
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') { closeMenus(); $$('.scrim.open').forEach(s => s.classList.remove('open')); }
  });

  /* position of el relative to container in unscaled frame pixels (frame is CSS-scaled) */
  function rel(el, container) {
    const a = el.getBoundingClientRect(), c = container.getBoundingClientRect(), k = window.SCALE || 1;
    return { x: (a.left - c.left) / k + container.scrollLeft - container.clientLeft, y: (a.top - c.top) / k + container.scrollTop - container.clientTop, w: a.width / k, h: a.height / k };
  }
  window.rel = rel;

  /* ---------- dialogs ---------- */
  window.openDialog = function (scrim, html, onMount) {
    scrim.innerHTML = '<div class="dialog" role="dialog" aria-modal="true">' + html + '</div>';
    scrim.classList.add('open');
    scrim.onclick = e => { if (e.target === scrim || e.target.closest('[data-close]')) scrim.classList.remove('open'); };
    if (onMount) onMount(scrim.firstChild);
    const f = scrim.querySelector('input, .btn:not([disabled])');
    if (f) f.focus({ preventScroll: true });
  };

  /* Typed-name confirmation, shared by all surfaces */
  window.confirmDanger = function (scrim, o) {
    const rows = (o.impact || []).map(r =>
      '<div class="ir ' + r[0] + '"><div class="ic">' + ic(r[0] === 'lose' ? 'warn' : r[0] === 'keep' ? 'check' : 'info') + '</div><div><b>' + r[1] + '</b><span>' + r[2] + '</span></div></div>').join('');
    openDialog(scrim,
      '<div class="eyebrow" style="color:var(--coral-ink)">' + (o.eyebrow || 'Destructive action') + '</div>' +
      '<h2>' + o.title + '</h2><p class="lead">' + o.lead + '</p>' +
      (rows ? '<div class="impact">' + rows + '</div>' : '') + (o.extra || '') +
      '<div class="field"><label for="dcType">Type <span class="typed">' + o.name + '</span> to confirm</label>' +
      '<input id="dcType" class="inp typed" autocomplete="off" placeholder="' + o.name + '"></div>' +
      '<div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn danger" id="dcGo" disabled>' + o.cta + '</button></div>',
      d => {
        const inp = d.querySelector('#dcType'), go = d.querySelector('#dcGo');
        inp.addEventListener('input', () => { go.disabled = inp.value.trim() !== o.name; });
        go.addEventListener('click', () => { scrim.classList.remove('open'); o.onConfirm && o.onConfirm(); });
      });
  };

  /* ---------- routing / scale ---------- */
  const surfaces = ['popup', 'panel', 'host'];
  function route() {
    const h = (location.hash || '#popup').slice(1).split('/');
    const s = surfaces.includes(h[0]) ? h[0] : 'popup';
    $$('.surface').forEach(el => el.classList.toggle('on', el.id === 's-' + s));
    $$('#switcher button').forEach(b => b.setAttribute('aria-selected', b.dataset.s === s));
    $$('.note-block').forEach(n => n.classList.toggle('hidden', n.dataset.s !== s));
    fit();
    document.dispatchEvent(new CustomEvent('route', { detail: { surface: s, sub: h.slice(1) } }));
  }
  function fit() {
    const stage = $('.stage'), wrap = $('.frame-wrap'), frame = $('.frame');
    if (!stage) return;
    const w = stage.clientWidth - 28, h = stage.clientHeight - 28;
    const sc = Math.min(1, w / 1280, h / 800);
    window.SCALE = sc;
    frame.style.transform = 'scale(' + sc + ')';
    wrap.style.width = 1280 * sc + 'px';
    wrap.style.height = 800 * sc + 'px';
  }
  window.fit = fit;
  window.addEventListener('hashchange', route);
  window.addEventListener('resize', fit);

  document.addEventListener('DOMContentLoaded', () => {
    let t = 'light';
    try { t = localStorage.getItem('fp-theme') || 'light'; } catch (e) {}
    if (/[?&]theme=dark/.test(location.search)) t = 'dark';
    setTheme(t);
    $('#themeBtn').addEventListener('click', () => setTheme(root.dataset.theme === 'dark' ? 'light' : 'dark'));
    $$('#switcher button').forEach(b => b.addEventListener('click', () => { location.hash = b.dataset.s; }));
    const nb = $('#notesBtn'), notes = $('.notes');
    nb.addEventListener('click', () => {
      const open = !notes.classList.contains('open');
      notes.classList.toggle('open', open);
      nb.setAttribute('aria-pressed', open);
      setTimeout(fit, 260);
    });
    if (/[?&]notes=1/.test(location.search)) { notes.classList.add('open'); nb.setAttribute('aria-pressed', 'true'); }
    notes.addEventListener('transitionend', fit);
    route();
  });
})();
