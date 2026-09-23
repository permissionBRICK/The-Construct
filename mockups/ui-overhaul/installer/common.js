/* The Construct installer / updater — shared helpers: icons, code rain, log view, demo bridge */
(function () {
  'use strict';

  // ---------- icons ----------
  const P = (d, extra = '') => `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" ${extra}>${d}</svg>`;
  const ICON = {
    check: P('<path d="M3.5 8.5 6.5 11.5 12.5 4.5"/>'),
    x: P('<path d="M4 4l8 8M12 4l-8 8"/>'),
    warn: P('<path d="M8 2.2 14.6 13.5H1.4Z"/><path d="M8 6.5v3.2M8 11.6v.1"/>'),
    err: P('<circle cx="8" cy="8" r="6.2"/><path d="M8 4.8v3.6M8 10.9v.1"/>'),
    info: P('<circle cx="8" cy="8" r="6.2"/><path d="M8 7.3v3.8M8 5v.1"/>'),
    chevDown: P('<path d="M4 6l4 4 4-4"/>'),
    chevUp: P('<path d="M4 10l4-4 4 4"/>'),
    chevRight: P('<path d="M6 4l4 4-4 4"/>'),
    copy: P('<rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/><path d="M10.5 5.5V3.8A1.3 1.3 0 0 0 9.2 2.5H3.8A1.3 1.3 0 0 0 2.5 3.8v5.4a1.3 1.3 0 0 0 1.3 1.3h1.7"/>'),
    save: P('<path d="M8 2.5v7.5M4.8 7 8 10.2 11.2 7"/><path d="M2.5 11.5v1.2a.8.8 0 0 0 .8.8h9.4a.8.8 0 0 0 .8-.8v-1.2"/>'),
    folder: P('<path d="M1.8 4.2a1 1 0 0 1 1-1h3.4l1.5 1.6h5.5a1 1 0 0 1 1 1v6.9a1 1 0 0 1-1 1H2.8a1 1 0 0 1-1-1Z"/>'),
    popout: P('<path d="M9 2.5h4.5V7M13.5 2.5 7.5 8.5"/><path d="M11.5 9.5v3a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1h3"/>'),
    down: P('<path d="M8 3v10M3.5 8.5 8 13l4.5-4.5"/>'),
    retry: P('<path d="M13 8a5 5 0 1 1-1.5-3.6"/><path d="M13 2.5v3h-3"/>'),
    ext: P('<path d="M9 2.5h4.5V7M13.5 2.5 7.5 8.5"/><path d="M11.5 9.5v3a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1h3"/>'),
    shield: P('<path d="M8 1.8 13 3.8v3.9c0 3.1-2.1 5.5-5 6.5-2.9-1-5-3.4-5-6.5V3.8Z"/><path d="M5.8 8.1 7.4 9.7 10.4 6.5"/>'),
    lock: P('<rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5.3 7V5a2.7 2.7 0 0 1 5.4 0v2"/>'),
    restart: P('<path d="M3 8a5 5 0 1 0 1.5-3.6"/><path d="M3 2.5v3h3"/>'),
    clock: P('<circle cx="8" cy="8" r="6.2"/><path d="M8 4.8V8l2.2 1.4"/>'),
    key: P('<circle cx="5.5" cy="10.5" r="3"/><path d="M7.7 8.3 13.5 2.5M11.5 4.5l1.6 1.6M10 6l1.2 1.2"/>'),
    globe: P('<circle cx="8" cy="8" r="6.2"/><path d="M1.8 8h12.4M8 1.8c1.8 2 2.6 4 2.6 6.2S9.8 12.2 8 14.2C6.2 12.2 5.4 10.2 5.4 8S6.2 3.8 8 1.8Z"/>'),
    pc: P('<rect x="1.8" y="2.5" width="12.4" height="8.5" rx="1.2"/><path d="M5.5 13.5h5M8 11v2.5"/>'),
    server: P('<rect x="2.2" y="2.2" width="11.6" height="4.6" rx="1"/><rect x="2.2" y="9.2" width="11.6" height="4.6" rx="1"/><path d="M4.8 4.5h.1M4.8 11.5h.1"/>'),
    edit: P('<path d="M10.5 2.8 13.2 5.5 5.5 13.2H2.8v-2.7Z"/>'),
    trash: P('<path d="M2.8 4.3h10.4M6.3 4.3V2.8h3.4v1.5M4.3 4.3l.6 9h6.2l.6-9"/>'),
    spark: P('<path d="M8 1.8 9.4 6.6 14.2 8 9.4 9.4 8 14.2 6.6 9.4 1.8 8 6.6 6.6Z"/>'),
    vscode: `<svg viewBox="0 0 16 16"><path fill="#0078d4" d="M11.6 1 5.4 6.7 2.4 4.4 1 5.1v5.8l1.4.7 3-2.3 6.2 5.7L15 13.5V2.5ZM2.6 10V6l2 2Zm8.9 1.4L8 8l3.5-3.4Z"/></svg>`,
    tray: P('<path d="M2 11.5h12M4.5 11.5V14M11.5 11.5V14"/><rect x="5.5" y="3" width="5" height="5" rx="1"/>'),
    play: P('<path d="M5 3.5v9l7-4.5Z"/>'),
    pause: P('<path d="M5.5 3.5v9M10.5 3.5v9"/>'),
    dot: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="3.2" fill="currentColor"/></svg>',
    skip: P('<path d="M3.5 8h9"/>'),
    minus: P('<path d="M3.5 8h9"/>'),
    box: P('<path d="M8 1.8 13.8 5v6L8 14.2 2.2 11V5Z"/><path d="M2.2 5 8 8.2 13.8 5M8 8.2v6"/>'),
    search: P('<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3.5 3.5"/>'),
    user: P('<circle cx="8" cy="5.5" r="2.8"/><path d="M2.8 14c.6-2.8 2.7-4.3 5.2-4.3s4.6 1.5 5.2 4.3"/>')
  };
  const CAPTION = {
    min: '<svg viewBox="0 0 10 10"><path d="M0 5h10" stroke="currentColor" stroke-width="1"/></svg>',
    max: '<svg viewBox="0 0 10 10"><rect x=".5" y=".5" width="9" height="9" fill="none" stroke="currentColor" stroke-width="1"/></svg>',
    close: '<svg viewBox="0 0 10 10"><path d="M0 0l10 10M10 0 0 10" stroke="currentColor" stroke-width="1"/></svg>'
  };
  const LOGO = `<svg class="logo" viewBox="0 0 32 32" aria-hidden="true">
    <defs><linearGradient id="lg-top" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3fe39a"/><stop offset="1" stop-color="#1fbf75"/></linearGradient></defs>
    <rect x=".75" y=".75" width="30.5" height="30.5" rx="7.5" fill="#0b1d14" stroke="#1fbf75" stroke-opacity=".45"/>
    <path d="M16 6.2 24.6 11 16 15.8 7.4 11Z" fill="url(#lg-top)" opacity=".9"/>
    <path d="M7.4 11v10L16 25.8V15.8Z" fill="#1fbf75" opacity=".28"/>
    <path d="M24.6 11v10L16 25.8V15.8Z" fill="#1fbf75" opacity=".5"/>
    <path d="M16 6.2 24.6 11v10L16 25.8 7.4 21V11Z M7.4 11 16 15.8 24.6 11 M16 15.8v10" fill="none" stroke="#3fe39a" stroke-width="1.1" stroke-linejoin="round"/>
  </svg>`;

  function wordmark(ver) {
    return `<div class="wordmark">${LOGO}<span class="wm">THE <b>CONSTRUCT</b></span>${ver ? `<span class="ver">${ver}</span>` : ''}</div>`;
  }
  function captionButtons(opts = {}) {
    return `<div class="caption-btns">
      <button title="Minimize" tabindex="-1">${CAPTION.min}</button>
      ${opts.max ? `<button title="Maximize" tabindex="-1">${CAPTION.max}</button>` : ''}
      <button class="close" title="Close" tabindex="-1">${CAPTION.close}</button></div>`;
  }

  // ---------- code rain ----------
  function CodeRain(canvas, opts = {}) {
    const ctx = canvas.getContext('2d');
    const glyphs = 'ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉ0123456789ABCDEF<>/=$#{}'.split('');
    const size = opts.size || 12;
    let w = 0, h = 0, cols = [], dpr = 1, last = 0, running = true;
    let seed = 7; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
    function resize() {
      dpr = Math.min(2, window.devicePixelRatio || 1);
      w = canvas.clientWidth; h = canvas.clientHeight;
      canvas.width = Math.max(1, w * dpr); canvas.height = Math.max(1, h * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      const n = Math.ceil(w / size);
      cols = Array.from({ length: n }, (_, i) => cols[i] || { y: rnd() * h * 1.5 - h * .5, v: .25 + rnd() * .6, a: rnd() < .55 ? 0 : .35 + rnd() * .5 });
      // pre-fill so a static screenshot already looks like rain
      ctx.fillStyle = '#050d09'; ctx.fillRect(0, 0, w, h);
      for (let k = 0; k < 60; k++) step(true);
    }
    function step(prefill) {
      ctx.fillStyle = 'rgba(5,13,9,0.16)';
      ctx.fillRect(0, 0, w, h);
      ctx.font = `${size}px ${getComputedStyle(document.body).getPropertyValue('--mono') || 'monospace'}`;
      cols.forEach((c, i) => {
        if (!c.a) { if (rnd() < .002) c.a = .35 + rnd() * .5; return; }
        const ch = glyphs[(rnd() * glyphs.length) | 0];
        const x = i * size;
        ctx.fillStyle = `rgba(160,255,200,${c.a})`;
        ctx.fillText(ch, x, c.y);
        ctx.fillStyle = `rgba(31,191,117,${c.a * .6})`;
        ctx.fillText(glyphs[(rnd() * glyphs.length) | 0], x, c.y - size);
        c.y += size * c.v;
        if (c.y > h + size * 4) { c.y = -rnd() * h * .6; c.v = .25 + rnd() * .6; if (rnd() < .3) c.a = 0; }
      });
    }
    function loop(t) {
      if (!running) return;
      if (t - last > 70) { step(); last = t; }
      requestAnimationFrame(loop);
    }
    resize();
    window.addEventListener('resize', resize);
    if (!window.matchMedia('(prefers-reduced-motion: reduce)').matches) requestAnimationFrame(loop);
    return { resize, stop() { running = false; } };
  }

  // ---------- simulated clock ----------
  const Clock = {
    base: 14 * 3600 + 12 * 60 + 3, // 14:12:03
    t: 0,
    fmt(sec) {
      const s = Math.floor(this.base + (sec == null ? this.t : sec));
      const hh = String(Math.floor(s / 3600) % 24).padStart(2, '0');
      const mm = String(Math.floor(s / 60) % 60).padStart(2, '0');
      const ss = String(s % 60).padStart(2, '0');
      return `${hh}:${mm}:${ss}`;
    }
  };

  // ---------- log view ----------
  const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  function maskSecrets(s) {
    return s
      .replace(/(glpat-)[A-Za-z0-9_\-]{6,}/g, '$1••••••••')
      .replace(/(ghp_)[A-Za-z0-9]{6,}/g, '$1••••••••')
      .replace(/(tkn=)[A-Za-z0-9]{6,}/g, '$1••••••••');
  }
  const ANSI = { '1': 'font-weight:700', '2': 'opacity:.7', '31': 'color:#ff7b72', '32': 'color:#3ddc84', '33': 'color:#f5c451', '34': 'color:#79b8ff', '35': 'color:#d2a8ff', '36': 'color:#56d4dd', '90': 'color:#6e7f77', '97': 'color:#fff' };
  function ansiToHtml(s) {
    let out = '', open = 0;
    const parts = s.split(/\x1b\[([\d;]*)m/);
    for (let i = 0; i < parts.length; i++) {
      if (i % 2 === 0) { out += esc(parts[i]); continue; }
      const codes = parts[i].split(';').filter(Boolean);
      if (!codes.length || codes.includes('0')) { out += '</span>'.repeat(open); open = 0; continue; }
      const st = codes.map(c => ANSI[c]).filter(Boolean).join(';');
      if (st) { out += `<span style="${st}">`; open++; }
    }
    return out + '</span>'.repeat(open);
  }
  const stripAnsi = s => s.replace(/\x1b\[[\d;]*m/g, '');

  function LogView(root, opts = {}) {
    const self = this;
    this.lines = [];
    this.follow = true;
    this.filter = 'all';
    this.query = '';
    this.onLine = opts.onLine || null;
    this.fileName = opts.fileName || 'install.log';
    this.folder = opts.folder || '%LOCALAPPDATA%\\Construct\\logs';
    root.classList.add('logview');
    root.innerHTML = `
      <div class="log-toolbar">
        <button class="chip" data-f="all" aria-pressed="true">All <span class="n" data-n="all">0</span></button>
        <button class="chip" data-f="steps" aria-pressed="false">Steps <span class="n" data-n="steps">0</span></button>
        <button class="chip" data-f="warn" aria-pressed="false">Warnings <span class="n" data-n="warn">0</span></button>
        <button class="chip" data-f="err" aria-pressed="false">Errors <span class="n" data-n="err">0</span></button>
        <span class="spacer"></span>
        <input class="search" type="search" placeholder="Search log" aria-label="Search log">
        <button class="icon-btn" data-a="copy" title="Copy selection, or the whole log if nothing is selected">${ICON.copy}</button>
        <button class="icon-btn" data-a="save" title="Save log as…">${ICON.save}</button>
        <button class="icon-btn" data-a="folder" title="Open log folder">${ICON.folder}</button>
        ${opts.popout ? `<button class="icon-btn" data-a="popout" title="Pop out into its own window">${ICON.popout}</button>` : ''}
      </div>
      <div class="log-body" role="log" aria-live="off" tabindex="0"></div>
      <button class="jump-live">${ICON.down} Jump to live</button>
      <div class="log-toast"></div>`;
    this.root = root;
    this.body = root.querySelector('.log-body');
    this.jump = root.querySelector('.jump-live');
    this.toastEl = root.querySelector('.log-toast');
    root.querySelectorAll('[data-f]').forEach(b => b.addEventListener('click', () => self.setFilter(b.dataset.f)));
    root.querySelector('.search').addEventListener('input', e => { self.query = e.target.value.trim().toLowerCase(); self.applyVisibility(); });
    root.querySelector('[data-a=copy]').addEventListener('click', () => self.copy());
    root.querySelector('[data-a=save]').addEventListener('click', () => self.save());
    root.querySelector('[data-a=folder]').addEventListener('click', () => self.toast(`Opened ${self.folder} in Explorer`));
    const po = root.querySelector('[data-a=popout]');
    if (po) po.addEventListener('click', () => opts.popout());
    this.jump.addEventListener('click', () => { self.follow = true; self.scrollBottom(); self.jump.classList.remove('show'); });
    let programmatic = false;
    this._prog = v => (programmatic = v);
    this.body.addEventListener('scroll', () => {
      if (programmatic) return;
      const atBottom = self.body.scrollHeight - self.body.scrollTop - self.body.clientHeight < 8;
      self.follow = atBottom;
      self.jump.classList.toggle('show', !atBottom);
    });
    this.counts = { all: 0, steps: 0, warn: 0, err: 0 };
  }
  LogView.prototype.add = function (kind, text, ts) {
    text = maskSecrets(text);
    const line = { kind, text, ts: ts || Clock.fmt(), id: this.lines.length };
    this.lines.push(line);
    const el = document.createElement('div');
    el.className = 'log-line ' + kind;
    el.dataset.id = line.id;
    line.el = el;
    this.renderLine(line);
    this.body.appendChild(el);
    this.counts.all++;
    if (kind === 'step' || kind === 'vmstep') this.counts.steps++;
    if (kind === 'warn') this.counts.warn++;
    if (kind === 'err') this.counts.err++;
    this.updateCounts();
    if (!this.visible(line)) el.style.display = 'none';
    if (this.follow) this.scrollBottom();
    if (this.onLine) this.onLine(line);
    return line;
  };
  LogView.prototype.renderLine = function (line) {
    let html = ansiToHtml(line.text);
    if (line.kind === 'vm') html = html.replace(/^(\[vm\])/, '<span class="src">$1</span>');
    html = html.replace(/((?:glpat-|ghp_|tkn=)••••••••)/g, '<span class="secret" title="masked before it reached the log">$1</span>');
    if (this.query) {
      const q = this.query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      html = html.replace(new RegExp(`(?![^<]*>)(${q})`, 'gi'), '<mark>$1</mark>');
    }
    line.el.innerHTML = `<span class="ts">${line.ts}</span><span class="tx">${html}</span>`;
  };
  LogView.prototype.visible = function (line) {
    const f = this.filter;
    if (f === 'steps' && !(line.kind === 'step' || line.kind === 'vmstep')) return false;
    if (f === 'warn' && line.kind !== 'warn') return false;
    if (f === 'err' && line.kind !== 'err') return false;
    if (this.query && !stripAnsi(line.text).toLowerCase().includes(this.query)) return false;
    return true;
  };
  LogView.prototype.applyVisibility = function () {
    this.lines.forEach(l => { l.el.style.display = this.visible(l) ? '' : 'none'; this.renderLine(l); });
    if (this.follow) this.scrollBottom();
  };
  LogView.prototype.setFilter = function (f) {
    this.filter = f;
    this.root.querySelectorAll('[data-f]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.f === f)));
    this.applyVisibility();
  };
  LogView.prototype.updateCounts = function () {
    for (const k in this.counts) {
      const n = this.root.querySelector(`[data-n="${k}"]`);
      if (n) n.textContent = this.counts[k];
    }
    this.root.querySelector('[data-f=warn]').classList.toggle('has-warn', this.counts.warn > 0);
    this.root.querySelector('[data-f=err]').classList.toggle('has-err', this.counts.err > 0);
  };
  LogView.prototype.scrollBottom = function () {
    this._prog(true);
    this.body.scrollTop = this.body.scrollHeight;
    requestAnimationFrame(() => this._prog(false));
  };
  LogView.prototype.focusLine = function (line) {
    if (!line) return;
    this.setFilter('all');
    this.follow = false;
    this.lines.forEach(l => l.el.classList.remove('focus'));
    line.el.classList.add('focus');
    this._prog(true);
    this.body.scrollTop = Math.max(0, line.el.offsetTop - this.body.clientHeight * .45);
    requestAnimationFrame(() => this._prog(false));
    this.jump.classList.add('show');
  };
  LogView.prototype.clear = function () {
    this.lines = []; this.body.innerHTML = ''; this.counts = { all: 0, steps: 0, warn: 0, err: 0 }; this.updateCounts(); this.follow = true; this.jump.classList.remove('show');
  };
  LogView.prototype.text = function () {
    return this.lines.map(l => `${l.ts}  ${stripAnsi(l.text)}`).join('\r\n');
  };
  LogView.prototype.copy = function () {
    const sel = String(window.getSelection());
    const txt = sel || this.text();
    try { navigator.clipboard && navigator.clipboard.writeText(txt); } catch (e) { /* offline mockup */ }
    this.toast(sel ? 'Copied selection' : `Copied ${this.lines.length} lines (secrets masked)`);
  };
  LogView.prototype.save = function () {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([this.text()], { type: 'text/plain' }));
    a.download = this.fileName; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    this.toast(`Saved ${this.fileName}`);
  };
  LogView.prototype.toast = function (msg) {
    this.toastEl.textContent = msg; this.toastEl.classList.add('show');
    clearTimeout(this._tt); this._tt = setTimeout(() => this.toastEl.classList.remove('show'), 1800);
  };

  // ---------- demo bridge (index <-> page) ----------
  const embedded = window.parent !== window;
  document.documentElement.classList.add(embedded ? 'is-embedded' : 'is-standalone');
  document.addEventListener('DOMContentLoaded', () => document.body.classList.add(embedded ? 'embedded' : 'standalone'));
  const Bridge = {
    embedded,
    post(msg) { if (embedded) try { window.parent.postMessage(Object.assign({ from: 'construct-installer' }, msg), '*'); } catch (e) { } },
    listen(fn) { window.addEventListener('message', e => { if (e.data && e.data.to === 'construct-installer') fn(e.data); }); },
    params() {
      const q = new URLSearchParams(location.search);
      const h = location.hash.replace(/^#/, '');
      h.split('&').forEach(kv => { if (!kv) return; const [k, v] = kv.split('='); q.set(k, v == null ? '1' : decodeURIComponent(v)); });
      return q;
    }
  };

  window.CX = { ICON, LOGO, wordmark, captionButtons, CodeRain, LogView, Clock, Bridge, esc };
})();
