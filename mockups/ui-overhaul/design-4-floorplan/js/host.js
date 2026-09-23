/* Host panel: floorplan treemap of RAM, what-if, and secondary tabs */
(function () {
  const root = $('#s-host');
  const H = D.host;
  const S = { tab: 'floorplan', measure: 'committed', view: 'map', zoom: null, selVm: null, whatIdx: 0, autoNote: false, sort: 'gb', sortDir: -1,
    resolved: [], jobFailed: true, leaseOverdue: true, inventory: true, selUser: null, spend: 'month', updPhase: 2, applyQueued: false };
  const STOPS = [null, 15, 30, 60, 90, 120, 240];
  const STATE_LABEL = { busy: 'running · busy', idle: 'running · idle', creating: 'creating', overdue: 'lease overdue', 'saved-idle': 'saved · idle policy', 'saved-pressure': 'saved · memory pressure', unmanaged: 'unmanaged', unknown: 'inventory incomplete', reserve: 'host reserve', off: 'off' };
  const SWATCH = { busy: 'st-busy', idle: 'st-idle', creating: 'st-creating', overdue: 'st-overdue', 'saved-idle': 'st-saved-idle', 'saved-pressure': 'st-saved-pressure', unmanaged: 'st-unmanaged', unknown: 'st-unknown', reserve: 'st-reserve' };
  const TOOLS = [['claude', 'var(--teal)'], ['codex', 'var(--amber)'], ['opencode', 'var(--plum)']];
  const fmtIdle = m => m == null ? '' : m < 60 ? m + ' min' : Math.floor(m / 60) + 'h ' + (m % 60) + 'm';
  const N = () => STOPS[S.whatIdx];
  const wouldSave = v => N() != null && v.idle != null && v.idle >= N() && (v.state === 'idle' || v.state === 'overdue') && v.resident;
  const visible = v => S.measure === 'committed' ? true : v.resident && !wouldSave(v);
  const vmState = v => v.state;

  function shell() {
    root.innerHTML = `
    <div class="win hp">
      <div class="titlebar">
        <div class="appmark">${GLYPH.replace('width="18" height="18"', 'width="14" height="14"')}</div>
        <div class="apptitle">buildbox <span>· Host administration</span></div>
        <button class="pill teal vchip" data-tab="service" style="border:0;cursor:pointer">constructd 1.14.2 · 1.15.0 available</button>
        <div class="spacer"></div>
        <div class="hsearch">${ic('search', 's')}<input id="hSearch" placeholder="Find a VM, person or job" aria-label="Search"></div>
        <span class="muted" style="font-size:11.5px">sampled 20 s ago</span>
        <div class="wctl"><span><svg class="i s" viewBox="0 0 24 24"><path d="M5 12h14"/></svg></span><span><svg class="i s" viewBox="0 0 24 24"><rect x="5" y="5" width="14" height="14" rx="1"/></svg></span><span class="x">${ic('x', 's')}</span></div>
      </div>
      <div class="body">
        <nav class="tabs" role="tablist" id="hTabs"></nav>
        <div style="min-height:0;display:grid">
          <section class="tabpane fp" data-pane="floorplan"><div class="fp-main" id="fpMain"></div><aside class="insp" id="insp"></aside></section>
          <section class="tabpane pane" data-pane="people"></section>
          <section class="tabpane pane" data-pane="spend"></section>
          <section class="tabpane pane" data-pane="licences"></section>
          <section class="tabpane pane" data-pane="jobs"></section>
          <section class="tabpane pane" data-pane="media"></section>
          <section class="tabpane pane" data-pane="service"></section>
          <section class="tabpane pane" data-pane="config"></section>
        </div>
      </div>
      <aside class="drawer" id="uDrawer"></aside>
      <div class="menu" id="hSearchMenu" style="width:300px"></div>
      <div class="tip" id="hTip"></div>
      <div class="scrim" id="hScrim"></div>
      <div class="toasts"></div>
    </div>`;
  }

  function renderTabs() {
    const t = [['floorplan', 'Floorplan', ''], ['people', 'People', ''], ['spend', 'Spend', ''], ['licences', 'Licences', '<span class="cnt">1</span>'],
      ['jobs', 'Jobs &amp; audit', S.jobFailed ? '<span class="cnt">1 failed</span>' : ''], ['media', 'Media', ''], ['service', 'Service', '<span class="cnt teal">update</span>'], ['config', 'Config', '']];
    $('#hTabs').innerHTML = t.map(x => `<button role="tab" data-tab="${x[0]}" aria-selected="${S.tab === x[0]}" class="${x[0] === 'floorplan' ? 'first' : ''}">${x[0] === 'floorplan' ? ic('map', 's') : ''}${x[1]}${x[2]}</button>`).join('');
    $$('.tabpane', root).forEach(p => p.classList.toggle('on', p.dataset.pane === S.tab));
  }

  /* ---------------- floorplan ---------------- */
  function issues() { return (S.jobFailed ? 1 : 0) + (S.leaseOverdue ? 1 : 0) + (S.inventory ? 1 : 0); }
  function fpMain() {
    $('#fpMain').innerHTML = `
      <div class="statusrow">
        <button class="pill ${issues() ? 'amber' : 'good'}" data-feed="1">${ic(issues() ? 'warn' : 'check', 's')}Health ok · ${issues()} issue${issues() === 1 ? '' : 's'}</button>
        <button class="pill coral" data-measure="resident">${ic('wave', 's')}Pressure elevated · saved mara/scratch-3 22 min ago</button>
        <span class="pill">Capacity: enforce</span>
                <span class="pill">Disk D: 61% of 3.6 TB</span>
        <span class="stale">${ic('clock', 's')}sampled 20 s ago</span>
      </div>
      <div class="herorow">
        <div class="hero" id="heroNum"></div>
        <div class="sent" id="heroSent"></div>
        <div class="ctl">
          <div class="seg sm" id="segMeasure" role="tablist" aria-label="Measure"><button data-measure="committed">Committed</button><button data-measure="resident">Resident</button></div>
          <div class="seg sm" id="segView" aria-label="View"><button data-view="map">${ic('map', 's')}Map</button><button data-view="list">${ic('list', 's')}List</button></div>
        </div>
      </div>
      <div class="crumbs" id="crumbs"></div>
      <div id="mapArea"><div class="tmwrap" style="padding-top:14px"><div class="tm" id="tm"></div><div class="axis" id="axis"></div></div></div>
      <div id="listArea" class="hidden"><div class="listwrap"><table class="vtable" id="vtable"></table></div></div>
      <div class="legend" id="legend"></div>
      <div class="whatif" id="whatif"></div>`;
    buildTm();
  }

  function buildTm() {
    const tm = $('#tm');
    tm.innerHTML = '<div class="zone over" id="zOver"><span class="zl">over-commit</span></div><div class="zone freed" id="zFreed"><span class="zl"></span></div>' +
      D.owners.map(o => `<button class="own ${o.kind === 'system' ? 'sys' : ''}" data-owner="${o.id}" id="own-${o.id}" title="${o.kind === 'user' ? 'Zoom into ' + o.id : o.label}">${o.label}<span></span></button>`).join('') +
      D.hvms.map(v => `<button class="blk" data-vm="${v.id}" id="b-${v.id.replace('/', '_')}"><span class="n">${v.name}</span><span class="g">${v.gb} GB</span><span class="tg"></span></button>`).join('') +
      '<div class="vline adm left" id="lAdm"><span class="vl">admission 118 GB</span></div><div class="vline" id="lPhys"><span class="vl">physical 128 GB</span></div><div class="vline pres left" id="lPres"><span class="vl">pressure: elevated from 85 GB</span></div><div class="vline allow" id="lAllow"><span class="vl"></span></div>';
  }

  function layout() {
    const tm = $('#tm'); if (!tm || !tm.offsetParent) return;
    const W = tm.clientWidth, Ht = tm.clientHeight, HEAD = 22;
    let domain = 144, owners = D.owners;
    const ownerVms = o => D.hvms.filter(v => v.owner === o);
    if (S.zoom) {
      owners = D.owners.filter(o => o.id === S.zoom);
      const o = owners[0], tot = ownerVms(o.id).reduce((a, v) => a + v.gb, 0);
      domain = Math.ceil(Math.max(tot, o.allowRam || 0) * 1.15 / 4) * 4;
    }
    const k = W / domain;
    let x = 0;
    const placed = new Set();
    owners.forEach(o => {
      const vms = ownerVms(o.id), tot = vms.reduce((a, v) => a + (visible(v) ? v.gb : 0), 0), w = tot * k;
      const oe = $('#own-' + o.id);
      Object.assign(oe.style, { left: x + 'px', top: '0px', width: Math.max(0, w - 2) + 'px', opacity: w > 34 ? 1 : 0 });
      oe.querySelector('span').textContent = tot ? tot + ' GB' : '';
      let y = HEAD, xx = x;
      vms.forEach(v => {
        const el = $('#b-' + v.id.replace('/', '_'));
        placed.add(v.id);
        const vis = visible(v);
        let L, T, Wd, Hh;
        if (S.zoom) { const ww = vis ? v.gb * k : 0; L = xx; T = HEAD; Wd = ww; Hh = Ht - HEAD; xx += ww; }
        else { const hh = vis && tot ? (Ht - HEAD) * v.gb / tot : 0; L = x; T = y; Wd = w; Hh = hh; y += hh; }
        Object.assign(el.style, { left: (L + 1) + 'px', top: (T + 1) + 'px', width: Math.max(0, Wd - 2) + 'px', height: Math.max(0, Hh - 2) + 'px' });
        el.className = 'blk ' + SWATCH[vmState(v)] + (vis ? '' : ' gone') + (S.measure === 'committed' && wouldSave(v) ? ' ws' : '') + (S.selVm === v.id ? ' sel' : '') +
          ((Wd < 34 || Hh < 22) ? ' tiny' : (Hh < 50 || Wd < 64) ? ' short' : '');
        el.querySelector('.tg').textContent = tagFor(v);
        el.setAttribute('aria-label', `${v.id}, ${v.gb} GB, ${STATE_LABEL[vmState(v)]}`);
        el.tabIndex = vis ? 0 : -1;
      });
      x += w;
    });
    D.owners.forEach(o => { if (!owners.includes(o)) { $('#own-' + o.id).style.opacity = 0; $('#own-' + o.id).style.width = '0px'; } });
    D.hvms.forEach(v => { if (!placed.has(v.id)) { const el = $('#b-' + v.id.replace('/', '_')); el.classList.add('gone'); el.style.width = '0px'; } });

    const resNow = 96, freed = D.hvms.filter(wouldSave).reduce((a, v) => a + v.gb, 0), after = resNow - freed;
    const line = (id, gb, show, text) => { const e = $(id); e.style.left = (gb * k) + 'px'; e.style.opacity = show ? 1 : 0; if (text) e.querySelector('.vl').textContent = text; };
    const host = !S.zoom, com = S.measure === 'committed';
    line('#lAdm', 118, host && com);
    line('#lPhys', 128, host);
    line('#lPres', 85, host && !com);
    const o = S.zoom && D.owners.find(q => q.id === S.zoom);
    line('#lAllow', o && o.allowRam ? o.allowRam : 0, !!(o && o.allowRam), o && o.allowRam ? o.id + "'s allowance " + o.allowRam + ' GB' : '');
    const zo = $('#zOver'); Object.assign(zo.style, { left: 128 * k + 'px', width: (host && com ? 16 * k : 0) + 'px', opacity: host && com ? 1 : 0 });
    zo.querySelector('.zl').textContent = '+14 GB over';
    const zf = $('#zFreed'); const showF = host && !com && freed > 0;
    Object.assign(zf.style, { left: after * k + 'px', width: (showF ? freed * k : 0) + 'px', opacity: showF ? 1 : 0 });
    zf.querySelector('.zl').textContent = 'frees ' + freed + ' GB';

    // axis
    const step = S.zoom ? (domain > 40 ? 8 : 4) : 16;
    let ax = '';
    for (let g = 0; g <= domain; g += step) ax += `<span style="left:${g * k}px">${g}${g === 0 ? '' : ''}</span>`;
    $('#axis').innerHTML = ax + `<span style="left:${W}px;transform:translateX(-100%);top:3px;opacity:0"></span>`;
    heroText(freed, after);
  }

  function tagFor(v) {
    if (v.state === 'busy') return 'busy · demand ' + v.demand + ' GB';
    if (v.state === 'idle') return 'idle ' + fmtIdle(v.idle);
    if (v.state === 'overdue') return 'lease ' + (v.lease || 'overdue');
    if (v.state === 'creating') return 'creating · ' + v.job + '%';
    if (v.state === 'saved-idle') return 'saved · idle policy';
    if (v.state === 'saved-pressure') return 'saved · pressure';
    if (v.state === 'unmanaged') return 'not Construct';
    if (v.state === 'unknown') return 'inventory incomplete';
    if (v.state === 'reserve') return 'OS + service';
    return '';
  }

  function heroText(freed, after) {
    const com = S.measure === 'committed';
    if (S.zoom) {
      const o = D.owners.find(q => q.id === S.zoom), vms = D.hvms.filter(v => v.owner === S.zoom), tot = vms.reduce((a, v) => a + v.gb, 0);
      $('#heroNum').innerHTML = tot + '<small>GB · ' + o.label + '</small>';
      $('#heroSent').innerHTML = o.allowRam ? `<b>${tot} of ${o.allowRam} GB</b> allowance${o.id === 'alice' ? ' (4 GB still being created)' : ''}. ${vms.length} VMs. Click a block for its inspector.` : `No RAM cap (admin). ${vms.length} VMs.`;
    } else if (com) {
      $('#heroNum').innerHTML = '142<small>GB committed</small>';
      $('#heroSent').innerHTML = `on <b>128 GB physical</b> · <b>111%</b> over-commit · admission line 118 GB · <b>96 GB</b> resident right now`;
    } else {
      $('#heroNum').innerHTML = after + '<small>GB resident</small>';
      $('#heroSent').innerHTML = freed ? `after the what-if, down from <b>96 GB</b>; pressure would be <b>${after >= 110 ? 'critical' : after >= 85 ? 'elevated' : 'normal'}</b>. Saved VMs leave the floor but keep their commitment.` : `of <b>128 GB physical</b> · pressure <b>elevated</b> above 85 GB · saved VMs are off the floor`;
    }
    $$('#segMeasure button').forEach(b => b.setAttribute('aria-selected', b.dataset.measure === S.measure));
    $$('#segView button').forEach(b => b.setAttribute('aria-selected', b.dataset.view === S.view));
    $('#crumbs').innerHTML = S.zoom ? `<button data-zoom="">${ic('chevl', 's')}buildbox</button><span class="muted">›</span><span class="cur">${S.zoom}</span>` : `<span class="muted">buildbox · grouped by owner · area = assigned RAM · click an owner to zoom</span>`;
    $('#legend').innerHTML = [['st-busy', 'running, busy'], ['st-idle', 'running, idle'], ['st-creating', 'creating'], ['st-overdue', 'lease overdue'], ['st-saved-idle', 'saved (idle policy)'], ['st-saved-pressure', 'saved (memory pressure)'], ['st-unmanaged', 'unmanaged'], ['st-unknown', 'unknown'], ['st-reserve', 'host reserve']]
      .map(l => `<span><i class="${l[0]}"></i>${l[1]}</span>`).join('') + (N() != null && S.measure === 'committed' ? `<span><i style="background:var(--hatch-teal),var(--card);box-shadow:0 0 0 1.5px var(--teal) inset"></i>would be saved</span>` : '');
  }

  function renderWhatif() {
    const n = N(), aff = D.hvms.filter(wouldSave), freed = aff.reduce((a, v) => a + v.gb, 0), after = 96 - freed;
    const pres = after >= 110 ? 'critical' : after >= 85 ? 'elevated' : 'normal';
    const people = [...new Set(aff.map(v => v.owner))];
    $('#whatif').innerHTML = `
      <div>
        <h4>${ic('sliders', 's')}What if</h4>
        <div class="q">Save every VM idle longer than <b>${n == null ? '— (off)' : fmtIdle(n)}</b>. A preview only; nothing changes until you apply.</div>
        <input type="range" min="0" max="${STOPS.length - 1}" step="1" value="${S.whatIdx}" id="wiRange" aria-label="Idle threshold for the what-if">
        <div class="stops">${STOPS.map(s => `<span>${s == null ? 'off' : s < 60 ? s + 'm' : s / 60 + 'h'}</span>`).join('')}</div>
        ${S.autoNote ? `<div class="muted" style="font-size:11.5px;margin-top:6px">${ic('info', 's')} Switched the map to <b>Resident</b>: saving frees resident RAM, not committed RAM.</div>` : ''}
      </div>
      <div class="wres">
        ${n == null ? `<div class="big">—</div><div class="who">Drag the slider to see which VMs would be saved, how much RAM comes back, and who is affected.</div>` : `
        <div class="big">${freed} GB <small>freed · resident ${96} → ${after} GB</small></div>
        <div class="bullet" title="Resident after the what-if, on 128 GB physical"><span class="f" style="width:${after / 128 * 100}%"></span><span class="ghost" style="left:${after / 128 * 100}%;width:${freed / 128 * 100}%"></span><span class="tick" style="left:${85 / 128 * 100}%;background:var(--coral)" title="pressure threshold 85 GB"></span></div>
        <div class="who">Pressure <b>elevated → ${pres}</b>. ${aff.length ? `Affects <b>${aff.length} VM${aff.length > 1 ? 's' : ''}</b> of <b>${people.join(', ')}</b>: ${aff.map(v => `${v.id} (idle ${fmtIdle(v.idle)})`).join(', ')}.` : 'No VM is idle that long.'}</div>
        <div class="row"><button class="btn sm primary" data-act="wi-save" ${aff.length ? '' : 'disabled'}>Save these now…</button><button class="btn sm" data-act="wi-policy">Make it the idle default…</button></div>`}
      </div>`;
  }

  function renderList() {
    const cols = [['id', 'VM'], ['owner', 'Owner'], ['state', 'State'], ['gb', 'RAM', 'r'], ['idle', 'Idle', 'r'], ['demand', 'Demand', 'r'], ['cost', 'Month', 'r']];
    const rows = D.hvms.slice().sort((a, b) => {
      const x = a[S.sort], y = b[S.sort];
      return (x == null ? -1 : y == null ? 1 : x > y ? 1 : x < y ? -1 : 0) * S.sortDir;
    });
    $('#vtable').innerHTML = `<thead><tr>${cols.map(c => `<th class="${c[2] || ''}" data-sort="${c[0]}">${c[1]}${S.sort === c[0] ? (S.sortDir > 0 ? ' ▲' : ' ▼') : ''}</th>`).join('')}</tr></thead><tbody>` +
      rows.map(v => `<tr data-vm="${v.id}" class="${S.selVm === v.id ? 'sel' : ''}"><td><b style="font-weight:600">${v.name}</b></td><td>${v.owner}</td><td><span class="sw ${SWATCH[v.state]}"></span>${STATE_LABEL[v.state]}${wouldSave(v) ? ' <span class="pill teal" style="font-size:10px">would save</span>' : ''}</td><td class="r">${v.gb} GB</td><td class="r">${v.idle != null ? fmtIdle(v.idle) : '—'}</td><td class="r">${v.demand != null ? v.demand + ' GB' : '—'}</td><td class="r">${v.cost != null ? money(v.cost) : '—'}</td></tr>`).join('') + '</tbody>';
  }

  /* ---------------- inspector ---------------- */
  function feedCards() {
    const cards = [];
    if (S.jobFailed) cards.push(`<div class="fc bad"><div class="ic">${ic('x', 's')}</div><div><b>Job failed: delete child bob/win-qa</b><p>09:14 · Access denied (0x80070005) removing the VHDX. A backup agent held a handle.</p><div class="row"><button class="btn sm primary" data-act="retry">${ic('refresh', 's')}Retry</button><button class="btn sm ghost" data-tab="jobs">Details</button></div></div></div>`);
    if (S.leaseOverdue) cards.push(`<div class="fc bad"><div class="ic">${ic('clock', 's')}</div><div><b>Lease overdue: bob/win-qa</b><p>2h over. 8 GB, idle 1h 10m. Bob was notified at 12:18.</p><div class="row"><button class="btn sm" data-act="extend">Extend 4h</button><button class="btn sm" data-act="shutqa">Shut down</button><button class="btn sm ghost" data-vm="bob/win-qa">Show</button></div></div></div>`);
    cards.push(`<div class="fc warn"><div class="ic">${ic('wave', 's')}</div><div><b>Memory pressure elevated</b><p>Resident 96 GB is above 85 GB. The policy saved mara/scratch-3 (16 GB) 22 min ago.</p><div class="row"><button class="btn sm" data-vm="mara/scratch-3">Show on map</button><button class="btn sm ghost" data-act="wi-60">Try what-if 60m</button></div></div></div>`);
    if (S.inventory) cards.push(`<div class="fc warn"><div class="ic">${ic('info', 's')}</div><div><b>Inventory incomplete: old-runner</b><p>Its configuration file is unreadable, so its 18 GB counts as committed.</p><div class="row"><button class="btn sm" data-act="rescan">Rescan</button><button class="btn sm ghost" data-vm="unmanaged/old-runner">Show</button></div></div></div>`);
    cards.push(`<div class="fc warn"><div class="ic">${ic('key', 's')}</div><div><b>Activation failed 2 days ago</b><p>0xC004C008: MAK key A has used its unlock limit. alice/win11-eval is in grace (29 days).</p><div class="row"><button class="btn sm" data-tab="licences">Licences</button></div></div></div>`);
    cards.push(`<div class="fc info"><div class="ic">${ic('download', 's')}</div><div><b>constructd 1.15.0 available</b><p>VMs keep running during the update. 1 running job blocks Apply.</p><div class="row"><button class="btn sm" data-tab="service">Review</button></div></div></div>`);
    return cards.join('');
  }

  function renderInsp() {
    const el = $('#insp');
    if (S.selVm) {
      const v = D.hvms.find(x => x.id === S.selVm), sys = v.owner === 'host' || v.owner === 'unmanaged';
      const prim = v.state === 'overdue' ? ['Shut down', 'power'] : v.state.startsWith('saved') ? ['Start', 'play'] : v.state === 'busy' || v.state === 'idle' ? ['Save now', 'save'] : v.state === 'creating' ? ['Open job', 'history'] : ['Rescan', 'refresh'];
      el.innerHTML = `<div class="sh"><div class="eyebrow">${sys ? v.owner === 'host' ? 'Host' : 'Unmanaged VM' : 'VM · ' + (v.kind === 'child' ? 'child of ' + v.parent.split('/')[1] : 'primary')}</div><button class="tb-icon" data-close-insp style="grid-row:1/3;grid-column:2" title="Close">${ic('x')}</button><h3>${v.id}</h3></div>
      <div class="sb">
        <div class="row" style="margin-bottom:10px"><span class="sw ${SWATCH[v.state]}" style="width:14px;height:14px"></span><b>${STATE_LABEL[v.state]}</b>${wouldSave(v) ? '<span class="pill teal">would save in what-if</span>' : ''}</div>
        <dl class="kv">
          <dt>Assigned RAM</dt><dd><b>${v.gb} GB</b>${v.demand != null ? ` · demand ${v.demand} GB <span class="muted">(20 s ago)</span>` : ''}</dd>
          ${v.vcpu ? `<dt>vCPU</dt><dd>${v.vcpu}</dd>` : ''}
          ${v.idle != null ? `<dt>Idle for</dt><dd>${fmtIdle(v.idle)}${v.state === 'busy' ? '' : ''}</dd>` : ''}
          ${!sys ? `<dt>Idle policy</dt><dd>${v.id === 'bob/api-vm' ? 'override: save after 30 min' : 'host default: save after 60 min'}</dd>` : ''}
          ${v.lease ? `<dt>Lease</dt><dd style="color:var(--coral-ink);font-weight:600">${v.lease}</dd>` : v.kind === 'child' ? `<dt>Lease</dt><dd>8h, 6h 40m left</dd>` : ''}
          ${v.cost != null ? `<dt>This month</dt><dd>${money(v.cost)}</dd>` : ''}
          ${v.note ? `<dt>Note</dt><dd>${v.note}</dd>` : ''}
          ${v.id === 'alice/win11-eval' ? `<dt>Licence</dt><dd>grace, 29 days · <button class="link" data-tab="licences">licences</button></dd>` : ''}
          ${v.id === 'bob/win-qa' ? `<dt>Licence</dt><dd>activated · MAK key A</dd>` : ''}
        </dl>
        ${v.id === 'mara/scratch-3' ? `<div class="callout" style="margin-top:10px">Saved by memory pressure at 13:48 (longest idle at the time, 64 min). Mara was notified. It resumes on her next connect.</div>` : ''}
        ${!sys ? `<h5>Actions</h5><div class="actlist">
          <button data-act="toast" data-msg="Restarting ${v.id}…">${ic('refresh', 's')}Restart</button>
          <button data-act="toast" data-msg="Shutting down ${v.id}…">${ic('power', 's')}Shut down</button>
          <button data-act="toast" data-msg="Lifetime dialog">${ic('clock', 's')}Change lifetime</button>
          <button data-act="toast" data-msg="${v.id} is now reachable on the host LAN">${ic('link', 's')}Make public<span class="sub">host LAN</span></button>
          <button data-act="override" data-vm-o="${v.id}">${ic('sliders', 's')}Overrides<span class="sub">idle, RAM cap</span></button>
          <button data-act="toast" data-msg="New VM token issued; shown once in the dialog">${ic('key', 's')}Rotate VM token</button>
          <button class="bad" data-act="delvm" data-vm-o="${v.id}">${ic('trash', 's')}Delete…</button></div>
        <h5>Related</h5><div class="row" style="flex-wrap:wrap"><button class="btn sm ghost" data-user="${v.owner}">${ic('user', 's')}${v.owner}</button><button class="btn sm ghost" data-tab="jobs">${ic('history', 's')}Jobs &amp; audit</button><button class="btn sm ghost" data-tab="spend">$ Spend</button></div>` : ''}
      </div>
      <div class="sf"><button class="btn primary" data-act="toast" data-msg="${prim[0]}: ${v.id}">${ic(prim[1], 's')}${prim[0]}</button></div>`;
      return;
    }
    if (S.zoom) {
      const u = D.users.find(x => x.id === S.zoom), vms = D.hvms.filter(v => v.owner === S.zoom);
      el.innerHTML = `<div class="sh"><div class="eyebrow">Owner</div><h3>${S.zoom} <span class="pill">${u.role}</span></h3></div>
      <div class="sb">${allowBullets(u)}
        <h5>VMs</h5>${vms.map(v => `<button class="nrow" data-vm="${v.id}" style="width:100%;border:0;background:none;text-align:left"><span class="sw ${SWATCH[v.state]}" style="margin-top:3px"></span><div><b>${v.name}</b><span>${STATE_LABEL[v.state]} · ${v.gb} GB</span></div><span class="t">${v.cost != null ? money(v.cost) : ''}</span></button>`).join('')}
        <h5>This month</h5><div style="font:600 24px var(--serif)">${money(u.cost)}</div></div>
      <div class="sf"><button class="btn" data-user="${u.id}">${ic('user', 's')}Open in People</button></div>`;
      return;
    }
    el.innerHTML = `<div class="sh"><div class="eyebrow">Needs attention</div><h3>Attention</h3></div>
      <div class="sb"><div class="feed">${feedCards()}</div>
      ${S.resolved.length ? `<h5 style="margin-top:14px">Resolved today</h5><div class="feed">${S.resolved.map(r => `<div class="fc done"><div class="ic">${ic('check', 's')}</div><div><b>${r[0]}</b><p style="margin-bottom:0">${r[1]}</p></div></div>`).join('')}</div>` : ''}</div>`;
  }

  function allowBullets(u) {
    const m = (lbl, used, cap, unit, def) => {
      const max = cap || def || used || 1, pct = Math.min(100, used / Math.max(max, used) * 100);
      return `<div style="margin:8px 0"><div class="row between" style="font-size:12px"><span class="muted">${lbl}</span><span><b>${used}${unit}</b> <span class="muted">/ ${cap == null ? '∞' : cap + unit}</span></span></div>
      <div class="bullet" style="margin-top:4px"><span class="f" style="width:${pct}%;background:${cap != null && used >= cap ? 'var(--amber)' : 'var(--teal)'}"></span>${def ? `<span class="tick" style="left:${Math.min(100, def / Math.max(max, used) * 100)}%" title="host default ${def}${unit}"></span>` : ''}</div></div>`;
    };
    return m('RAM', u.ram[0], u.ram[1], ' GB', D.userDefaults.ram) + m('vCPU', u.vcpu[0], u.vcpu[1], '', D.userDefaults.vcpu) + m('Primary VMs', u.prim[0], u.prim[1], '') + m('Child VMs', u.child[0], u.child[1], '');
  }

  /* ---------------- secondary tabs ---------------- */
  function vmChip(v) {
    const c = { busy: 'teal', idle: 'teal', creating: 'plum', overdue: 'coral', 'saved-idle': '', 'saved-pressure': 'coral' }[v.state];
    return `<span class="vmchip pill ${c || ''}" style="font-size:10.5px">${v.name}</span>`;
  }
  function renderPeople() {
    const p = $('[data-pane="people"]');
    const bul = (used, cap, def, unit) => {
      const max = Math.max(cap || def, used, 1);
      return `<div class="lb"><span><b>${used}</b>${unit} used</span><span>${cap == null ? 'no cap' : 'of ' + cap + unit}</span></div><div class="bullet"><span class="f" style="width:${used / max * 100}%;background:${cap != null && used >= cap ? 'var(--amber)' : 'var(--teal)'}"></span>${cap != null ? `<span class="tick" style="left:${cap / max * 100 - .5}%;background:var(--ink)"></span>` : ''}<span class="tick amber" style="left:${def / max * 100 - .5}%" title="host default"></span></div>`;
    };
    p.innerHTML = `<div class="phead"><div class="grow"><h2>People</h2><p class="lede" style="margin-bottom:0">Allowance bullets: the bar is what they use, the dark tick is their cap, the amber tick is the host default (24 GB, 8 vCPU).</p></div>
      <button class="btn" data-act="toast" data-msg="Offboard: pick a person first">Offboard…</button><button class="btn primary" data-act="onboard">${ic('plus', 's')}Onboard developer</button></div>
      <div class="phdr"><span>Person</span><span>RAM</span><span>vCPU</span><span>Primaries</span><span>Children</span><span style="text-align:right">Month</span><span>VMs</span></div>
      <div class="people">${D.users.map(u => `<div class="prow ${u.flags.includes('disabled') ? 'dis' : ''} ${S.selUser === u.id ? 'sel' : ''}" data-user="${u.id}" tabindex="0">
        <div class="who"><b>${u.id}</b><div class="row"><span class="pill">${u.role}</span>${u.flags.map(f => `<span class="pill ${f === 'disabled' ? '' : 'coral'}">${f}</span>`).join('')}</div></div>
        <div class="m">${bul(u.ram[0], u.ram[1], D.userDefaults.ram, ' GB')}</div>
        <div class="m">${bul(u.vcpu[0], u.vcpu[1], D.userDefaults.vcpu, '')}</div>
        <div class="cnt"><b>${u.prim[0]}</b> / ${u.prim[1] == null ? '∞' : u.prim[1]}</div>
        <div class="cnt"><b>${u.child[0]}</b> / ${u.child[1] == null ? '∞' : u.child[1]}</div>
        <div class="cost">${money(u.cost)}</div>
        <div class="chips">${D.hvms.filter(v => v.owner === u.id).map(vmChip).join('') || '<span class="muted" style="font-size:11.5px">none</span>'}</div></div>`).join('')}</div>`;
  }
  function openUser(id) {
    S.selUser = id; renderPeople();
    const u = D.users.find(x => x.id === id), dr = $('#uDrawer');
    dr.innerHTML = `<div class="sh" style="padding:14px 16px 10px;border-bottom:1px solid var(--line);display:grid;grid-template-columns:1fr auto"><div><div class="eyebrow">Person · one form, one save</div><h3 style="margin:0;font:600 22px var(--serif)">${u.id}</h3></div><button class="tb-icon" data-act="closeuser">${ic('x')}</button></div>
      <div class="sb" style="overflow:auto;padding:12px 16px">
        ${u.flags.includes('legacy credential') ? `<div class="callout">${ic('warn', 's')} <b>Legacy credential.</b> Mara still signs in with a shared password. <button class="link" data-act="toast" data-msg="Token issued; old credential disabled">Issue a token and retire it</button></div>` : ''}
        <div class="row"><div class="field grow"><label>Role</label><select class="inp"><option ${u.role === 'dev' ? 'selected' : ''}>dev</option><option ${u.role === 'contractor' ? 'selected' : ''}>contractor</option><option ${u.role === 'admin' ? 'selected' : ''}>admin</option></select></div><div class="field grow"><label>Template</label><select class="inp"><option>custom</option><option>senior</option><option>contractor</option></select></div></div>
        <h5 style="font-size:10.5px;letter-spacing:.09em;text-transform:uppercase;color:var(--muted);margin:12px 0 4px">Allowance</h5>
        <div class="grid2" style="gap:8px">
          <div class="field" style="margin:4px 0"><label>RAM (GB)</label><input class="inp" type="number" value="${u.ram[1] ?? ''}" placeholder="no cap"></div>
          <div class="field" style="margin:4px 0"><label>vCPU</label><input class="inp" type="number" value="${u.vcpu[1] ?? ''}" placeholder="no cap"></div>
          <div class="field" style="margin:4px 0"><label>Primary VMs</label><input class="inp" type="number" value="${u.prim[1] ?? ''}" placeholder="no cap"></div>
          <div class="field" style="margin:4px 0"><label>Child VMs</label><input class="inp" type="number" value="${u.child[1] ?? ''}" placeholder="no cap"></div>
          <div class="field" style="margin:4px 0"><label>Max child lease</label><select class="inp"><option>12 hours</option><option>3 days</option></select></div>
          <div class="field" style="margin:4px 0"><label>Host forwards</label><select class="inp"><option>allowed</option><option>not allowed</option></select></div>
        </div>
        <div class="muted" style="font-size:11.5px">Effective now: ${u.ram[0]} of ${u.ram[1] ?? '∞'} GB, ${u.vcpu[0]} of ${u.vcpu[1] ?? '∞'} vCPU. Host headroom: 10 GB under the admission line.</div>
        <h5 style="font-size:10.5px;letter-spacing:.09em;text-transform:uppercase;color:var(--muted);margin:14px 0 4px">Per-VM overrides</h5>
        ${D.hvms.filter(v => v.owner === id).map(v => `<div class="row" style="padding:5px 0;border-bottom:1px solid var(--line)"><span class="sw ${SWATCH[v.state]}"></span><b class="grow" style="font-weight:600">${v.name}</b><span class="muted" style="font-size:12px">${v.id === 'bob/api-vm' ? 'idle: save after 30 min' : 'defaults'}</span><button class="btn sm ghost">Edit</button></div>`).join('') || '<div class="muted">No VMs</div>'}
        <h5 style="font-size:10.5px;letter-spacing:.09em;text-transform:uppercase;color:var(--muted);margin:14px 0 4px">Access token</h5>
        <div class="row"><span class="muted grow" style="font-size:12px">${id === 'mara' ? 'rotated by dana at 13:22' : 'issued Aug 30 · last used 12 min ago'}</span><button class="btn sm" data-act="rotate" data-u="${id}">${ic('key', 's')}Rotate</button></div>
        <h5 style="font-size:10.5px;letter-spacing:.09em;text-transform:uppercase;color:var(--muted);margin:14px 0 4px">Leave</h5>
        <div class="row"><button class="btn sm">${u.flags.includes('disabled') ? 'Enable' : 'Disable'}</button><button class="btn sm ghost" style="color:var(--coral-ink)" data-act="offboard" data-u="${id}">Offboard…</button></div>
      </div>
      <div class="sf" style="padding:10px 16px;border-top:1px solid var(--line);display:flex;justify-content:flex-end;gap:8px;background:var(--card-2)"><button class="btn ghost" data-act="closeuser">Cancel</button><button class="btn primary" data-act="saveuser">Save ${id}</button></div>`;
    dr.classList.add('open');
  }

  function renderSpend() {
    const p = $('[data-pane="spend"]'), mul = { today: 0.031, month: 1, all: 4.6 }[S.spend];
    const us = D.users.map(u => ({ id: u.id, tools: u.tools.map(t => t * mul), cost: u.cost * mul })).sort((a, b) => b.cost - a.cost);
    const max = us[0].cost, total = us.reduce((a, u) => a + u.cost, 0);
    p.innerHTML = `<div class="phead"><div class="grow"><h2>Spend</h2><p class="lede" style="margin-bottom:0">Token cost by person, split by tool. One period filter scopes everything on this page.</p></div></div>
      <div class="row" style="margin-bottom:12px"><div class="seg sm" id="spendSeg">${[['today', 'Today'], ['month', 'This month'], ['all', 'All time']].map(x => `<button data-spend="${x[0]}" aria-selected="${S.spend === x[0]}">${x[1]}</button>`).join('')}</div><span class="muted" style="font-size:12px;margin-left:8px">Trend lines need daily buckets from the service; not available yet.</span></div>
      <div class="grid2" style="grid-template-columns:1.3fr 1fr">
        <div class="box"><h4>By person <span class="r">${money(total)} total</span></h4>
          <div class="lg" style="margin-bottom:6px">${TOOLS.map(t => `<span><i style="background:${t[1]}"></i>${t[0]}</span>`).join('')}</div>
          ${us.map(u => `<div class="hbar"><span class="nm">${u.id}</span><div class="tr" style="width:${Math.max(0.5, u.cost / max * 100)}%">${u.tools.map((t, i) => t > 0 ? `<i style="flex:${t};background:${TOOLS[i][1]}" data-tipv="${u.id} · ${TOOLS[i][0]}: ${money(t)}"></i>` : '').join('')}</div><span class="v">${money(u.cost)}</span></div>`).join('')}
        </div>
        <div class="box"><h4>Table view</h4><table class="tbl"><thead><tr><th>Person</th>${TOOLS.map(t => `<th class="r">${t[0]}</th>`).join('')}<th class="r">Total</th></tr></thead><tbody>
          ${us.map(u => `<tr><td>${u.id}</td>${u.tools.map(t => `<td class="r">${money(t)}</td>`).join('')}<td class="r"><b>${money(u.cost)}</b></td></tr>`).join('')}</tbody></table></div>
      </div>
      <div class="box" style="margin-top:14px"><h4>By VM <span class="r">this month</span></h4><table class="tbl"><thead><tr><th>VM</th><th>Owner</th><th>State</th><th class="r">Cost</th></tr></thead><tbody>
        ${D.hvms.filter(v => v.cost != null).sort((a, b) => b.cost - a.cost).map(v => `<tr><td>${v.name}</td><td>${v.owner}</td><td><span class="sw ${SWATCH[v.state]}"></span>${STATE_LABEL[v.state]}</td><td class="r">${money(v.cost * mul)}</td></tr>`).join('')}</tbody></table></div>`;
  }

  function renderLicences() {
    const L = D.licences, p = $('[data-pane="licences"]');
    p.innerHTML = `<div class="phead"><div class="grow"><h2>Windows licences</h2><p class="lede" style="margin-bottom:0">MAK keys have a fixed number of activations at Microsoft. Each square is one.</p></div><button class="btn">${ic('plus', 's')}Add key</button></div>
      <div class="callout coral" style="display:flex;gap:10px;align-items:center">${ic('warn')}<div class="grow"><b>Activation failed ${L.failure.when}: ${L.failure.code}.</b> Key A has used its unlock limit at Microsoft, so alice/win11-eval stayed in grace. Key B has 3 activations left.</div><button class="btn sm primary" data-act="toast" data-msg="Activating alice/win11-eval with MAK key B…">Retry with key B</button></div>
      <div class="grid3" style="margin-top:12px">${L.keys.map(k => `<div class="box keycard ${k.used / k.of >= .7 ? 'hot' : ''}"><h4>Key ${k.id} <span class="pill">${k.type}</span><span class="r">${k.of - k.used} left</span></h4>
        <div class="big">${k.used}<span style="font:500 14px var(--sans);color:var(--muted)"> of ${k.of} used</span></div>
        <div class="meterbig">${Array.from({ length: k.of }, (_, i) => `<i class="${i < k.used ? 'u' : ''}"></i>`).join('')}</div>
        <div class="muted" style="font-size:12px">${k.bound.length ? 'Held by ' + k.bound.join(', ') : 'Not assigned'}</div></div>`).join('')}</div>
      <div class="box" style="margin-top:14px"><h4>Guests</h4><table class="tbl"><thead><tr><th>VM</th><th>State</th><th>Key</th><th></th></tr></thead><tbody>
        ${L.guests.map(g => `<tr><td>${g.vm}</td><td><span class="pill ${g.state === 'activated' ? 'good' : g.state === 'grace' ? 'amber' : ''}">${g.state}</span> <span class="muted">${g.detail}</span></td><td>${g.key}</td><td class="r">${g.state === 'grace' ? '<button class="btn sm">Activate</button>' : g.state === 'retained' ? '<button class="btn sm ghost">Release</button>' : ''}</td></tr>`).join('')}</tbody></table>
        <p class="muted" style="font-size:12px;margin:8px 0 0">Evaluation media: Windows 11 24H2 (90 days), Server 2025 (180 days). Machine-identity reuse is opt-in per user.</p></div>`;
  }

  function renderJobs() {
    const p = $('[data-pane="jobs"]');
    const jobs = D.jobs.map(j => j.id === 'j3' && !S.jobFailed ? Object.assign({}, j, { state: 'done', err: null }) : j);
    p.innerHTML = `<div class="grid2" style="grid-template-columns:1.1fr 1fr;align-items:start">
      <div><h2>Jobs</h2><p class="lede">Running, queued and failed. Each links to its VM.</p>
      ${jobs.map(j => `<div class="box" style="margin-bottom:10px;padding:12px 14px"><div class="row"><span class="pill ${j.state === 'running' ? 'teal' : j.state === 'failed' ? 'coral' : j.state === 'done' ? 'good' : ''}">${j.state}</span><b class="grow" style="font-weight:600">${j.text}</b><span class="muted" style="font-size:12px">${j.started}</span></div>
        ${j.state === 'running' ? `<div class="meter" style="margin-top:8px"><i style="width:${j.pct}%"></i></div><div class="muted" style="font-size:11.5px;margin-top:4px">${j.pct}% · installing Windows · blocks the service update</div>` : ''}
        ${j.err ? `<div style="font-size:12px;color:var(--coral-ink);margin-top:6px">${j.err}</div>` : ''}
        <div class="row" style="margin-top:8px"><button class="btn sm ghost" data-vm="${j.vm}">${ic('map', 's')}${j.vm}</button>${j.state === 'failed' ? `<button class="btn sm primary" data-act="retry">${ic('refresh', 's')}Retry</button>` : j.state === 'queued' || j.state === 'running' ? '<button class="btn sm ghost">Cancel</button>' : ''}</div></div>`).join('')}
      </div>
      <div><h2>Audit</h2><p class="lede">Who did what. Newest first.</p><input class="inp" placeholder="Filter by person, VM or action" id="auditFilter" style="margin-bottom:8px">
        <div class="box" style="padding:4px 14px" id="auditList">${auditRows('')}</div></div></div>`;
  }
  function auditRows(f) {
    return D.audit.filter(a => !f || a.join(' ').toLowerCase().includes(f.toLowerCase())).map(a => `<div class="row" style="padding:8px 0;border-bottom:1px solid var(--line);align-items:flex-start"><span class="muted mono" style="width:44px;flex:none">${a[0]}</span><span><b style="font-weight:600">${a[1]}</b> ${a[2]}</span></div>`).join('') || '<div class="muted" style="padding:10px 0">No entries</div>';
  }

  function renderMedia() {
    const m = D.media, p = $('[data-pane="media"]');
    const list = (rows, kind) => rows.map(r => `<div class="row" style="padding:9px 0;border-bottom:1px solid var(--line)">${ic(kind === 'iso' ? 'disk' : 'windows')}<div class="grow"><b style="font-weight:600">${r[0]}</b><div class="muted" style="font-size:12px">${r[1]}${r[2] ? ' · ' + r[2] : ''}</div></div>${r[2] === 'current' ? '<span class="pill good">current</span>' : kind === 'iso' ? '<button class="btn sm">Make current</button>' : '<button class="btn sm ghost">Replace</button>'}<button class="btn sm ghost" aria-label="Remove">${ic('trash', 's')}</button></div>`).join('');
    p.innerHTML = `<h2>Media</h2><p class="lede">Install images for new VMs and child VMs.</p><div class="grid2">
      <div class="box"><h4>Ubuntu ISO catalog <span class="r">new primaries use the current one</span></h4>${list(m.isos, 'iso')}<button class="btn sm" style="margin-top:10px">${ic('download', 's')}Fetch an ISO…</button></div>
      <div class="box"><h4>Child media <span class="r">Windows guests</span></h4>${list(m.child, 'child')}<button class="btn sm" style="margin-top:10px">${ic('plus', 's')}Upload media…</button></div></div>`;
  }

  function renderService() {
    const p = $('[data-pane="service"]'), ph = S.updPhase;
    const phases = [['Check', 'manifest and checksum'], ['Stage', 'download and unpack'], ['Apply', 'swap the service'], ['Verify', 'health checks']];
    p.innerHTML = `<div class="grid2" style="grid-template-columns:1.2fr 1fr;align-items:start"><div>
      <h2>Service update</h2><p class="lede">Updating constructd restarts the service only. VMs keep running and reconnect.</p>
      <div class="box"><div class="row"><span class="mono">1.14.2</span>${ic('arrowr', 's')}<span class="mono" style="color:var(--teal-ink);font-weight:700">1.15.0</span><span class="pill teal" style="margin-left:auto">staged 08:30</span></div>
        <div class="phases">${phases.map((x, i) => `<div class="phase ${i < ph ? 'done' : i === ph ? 'cur' : ''}"><span class="d"></span><b>${x[0]}</b><span>${x[1]}</span></div>`).join('')}</div>
        <div class="callout"><b>1 job blocks Apply:</b> "Create child win11-eval for alice" (62%). Wait for it or cancel it.</div>
        <div class="row"><button class="btn primary" data-act="apply-upd" ${S.applyQueued ? 'disabled' : ''}>${S.applyQueued ? 'Queued · waits for the job' : 'Apply when jobs finish'}</button><button class="btn">Apply now (cancel job)</button><button class="btn ghost">Cancel update</button></div>
        <details class="caps"><summary>Recovery</summary><p class="muted" style="font-size:12px">Use these if an update stops halfway.</p><div class="row" style="flex-wrap:wrap"><button class="btn sm">Resume</button><button class="btn sm">Resolve: keep new</button><button class="btn sm">Resolve: roll back</button><button class="btn sm">Resolve: mark failed</button></div></details>
      </div></div>
      <div><h2 style="font-size:19px;margin-top:6px">Backend capabilities</h2><p class="lede">Reference data from the Hyper-V backend.</p>
        <div class="box"><details class="caps" open><summary>hyperv (buildbox)</summary><table class="tbl"><tbody>
        ${[['Dynamic memory', 'yes'], ['Save state', 'yes'], ['Nested virtualization', 'yes'], ['vTPM for Windows guests', 'yes'], ['Resize vCPU while running', 'no, restart'], ['Host forwards', 'LAN, per-user allow'], ['GPU partitioning', 'no']].map(r => `<tr><td>${r[0]}</td><td class="r">${r[1]}</td></tr>`).join('')}</tbody></table></details></div></div></div>`;
  }

  function renderConfig(prefill) {
    const p = $('[data-pane="config"]');
    p.innerHTML = `<div class="phead"><div class="grow"><h2>Host config</h2><p class="lede" style="margin-bottom:0">Each change shows its effect on the current floor before you save.</p></div><button class="btn ghost">Advanced JSON…</button><button class="btn primary" data-act="toast" data-msg="Config saved · audit entry written">Save changes</button></div>
      <div class="grid2">
        <div class="box ${prefill ? 'sel-hl' : ''}" style="${prefill ? 'border-color:var(--teal);box-shadow:0 0 0 3px color-mix(in srgb,var(--teal) 20%,transparent)' : ''}"><h4>Idle policy defaults</h4>
          <div class="row"><span>Save VMs idle for</span><input class="inp" value="${prefill || 60}" style="width:70px"><span>min</span></div>
          <div class="row" style="margin-top:8px"><span>Longest a user may set</span><input class="inp" value="240" style="width:70px"><span>min</span></div>
          <div class="callout teal" style="margin-bottom:0">${prefill ? `Preview from the what-if: saves ${D.hvms.filter(wouldSave).map(v => v.id).join(', ') || 'no VMs'} now.` : 'Currently: 1 VM saved by this policy (dana/dev-2).'}</div></div>
        <div class="box"><h4>Memory pressure</h4>
          <div class="row"><span class="grow">Elevated from</span><input class="inp" value="85" style="width:70px"><span>GB resident</span></div>
          <div class="row" style="margin-top:6px"><span class="grow">Critical from</span><input class="inp" value="110" style="width:70px"><span>GB resident</span></div>
          <div class="row" style="margin-top:6px"><span class="grow">When elevated</span><select class="inp" style="width:auto"><option>save longest-idle VMs first</option><option>notify only</option></select></div>
          <div class="callout" style="margin-bottom:0">With these values the host is <b>elevated</b> now (96 GB).</div></div>
        <div class="box"><h4>Capacity</h4><div class="seg sm"><button>observe</button><button aria-selected="true">enforce</button></div>
          <div class="row" style="margin-top:10px"><span class="grow">Admission line</span><input class="inp" value="118" style="width:70px"><span>GB committed</span></div>
          <div class="row" style="margin-top:6px"><span class="grow">Host reserve</span><input class="inp" value="12" style="width:70px"><span>GB</span></div>
          <p class="muted" style="font-size:12px;margin-bottom:0">Enforce refuses new VMs that would cross the admission line. Committed is 142 GB, so new VMs are refused now.</p></div>
        <div class="box"><h4>User defaults &amp; caps</h4>
          <div class="grid2" style="gap:8px"><div class="field" style="margin:0"><label>RAM per user</label><input class="inp" value="24 GB"></div><div class="field" style="margin:0"><label>vCPU per user</label><input class="inp" value="8"></div><div class="field" style="margin:0"><label>Primary VMs</label><input class="inp" value="2"></div><div class="field" style="margin:0"><label>Child VMs</label><input class="inp" value="2"></div></div></div>
      </div>`;
  }

  /* ---------------- actions ---------------- */
  function renderFloor() { layout(); renderWhatif(); renderInsp(); if (S.view === 'list') renderList(); $('#mapArea').classList.toggle('hidden', S.view !== 'map'); $('#listArea').classList.toggle('hidden', S.view !== 'list'); }
  function resolve(title, text, auditText) {
    S.resolved.unshift([title, text + ' · you · 14:19']);
    if (auditText) D.audit.unshift(['14:19', 'dana', auditText]);
    renderTabs(); fpMain(); renderFloor(); renderJobs();
  }

  function onClick(e) {
    const t = e.target;
    const tab = t.closest('[data-tab]');
    if (tab) { S.tab = tab.dataset.tab; renderTabs(); if (S.tab === 'floorplan') requestAnimationFrame(renderFloor); return; }
    const ms = t.closest('[data-measure]');
    if (ms) { S.measure = ms.dataset.measure; S.autoNote = false; renderFloor(); return; }
    const vw = t.closest('[data-view]');
    if (vw) { S.view = vw.dataset.view; renderFloor(); return; }
    const own = t.closest('[data-owner]');
    if (own) { const o = D.owners.find(x => x.id === own.dataset.owner); if (o.kind === 'user') { S.zoom = o.id; S.selVm = null; renderFloor(); } return; }
    const zm = t.closest('[data-zoom]');
    if (zm) { S.zoom = null; S.selVm = null; renderFloor(); return; }
    const srt = t.closest('[data-sort]');
    if (srt) { S.sortDir = S.sort === srt.dataset.sort ? -S.sortDir : -1; S.sort = srt.dataset.sort; renderList(); return; }
    const vm = t.closest('[data-vm]');
    if (vm) { S.selVm = vm.dataset.vm; if (S.tab !== 'floorplan') { S.tab = 'floorplan'; renderTabs(); } requestAnimationFrame(renderFloor); closeMenus(); return; }
    const us = t.closest('[data-user]');
    if (us) { S.tab = 'people'; renderTabs(); openUser(us.dataset.user); closeMenus(); return; }
    if (t.closest('[data-close-insp]')) { S.selVm = null; renderFloor(); return; }
    if (t.closest('[data-feed]')) { S.selVm = null; S.zoom = null; renderFloor(); return; }
    const sp = t.closest('[data-spend]');
    if (sp) { S.spend = sp.dataset.spend; renderSpend(); return; }
    const a = t.closest('[data-act]');
    if (!a) return;
    const act = a.dataset.act;
    if (act === 'retry') {
      a.disabled = true; a.innerHTML = 'Retrying…';
      setTimeout(() => { S.jobFailed = false; resolve('Deleted child bob/win-qa', 'Retried after the backup agent released the VHDX', 'retried delete child bob/win-qa (ok)'); toast('Job succeeded · moved to resolved', root); }, 900);
    } else if (act === 'extend') {
      S.leaseOverdue = false; const v = D.hvms.find(x => x.id === 'bob/win-qa'); v.state = 'idle'; v.lease = null;
      resolve('Extended bob/win-qa lease by 4h', 'Lease now ends 18:19', 'extended lease of bob/win-qa by 4h');
    } else if (act === 'shutqa') {
      toast('Shutting down bob/win-qa…', root);
    } else if (act === 'rescan') {
      S.inventory = false; const v = D.hvms.find(x => x.id === 'unmanaged/old-runner'); v.note = 'Rescanned: VM is off; 18 GB startup RAM still counts as committed';
      resolve('Rescanned old-runner', 'Inventory complete: off, 18 GB startup RAM', 'rescanned inventory');
    } else if (act === 'wi-60') {
      S.whatIdx = 3; if (S.measure === 'committed') { S.measure = 'resident'; S.autoNote = true; } renderFloor();
    } else if (act === 'wi-save') {
      const aff = D.hvms.filter(wouldSave);
      openDialog($('#hScrim'), `<div class="eyebrow">Save now</div><h2>Save ${aff.length} VM${aff.length > 1 ? 's' : ''}?</h2><p class="lead">Their RAM is written to disk. Owners get a notification, and each VM resumes on its next connect (about 20 s).</p>
        <div class="impact">${aff.map(v => `<div class="ir note"><div class="ic">${ic('save')}</div><div><b>${v.id} · ${v.gb} GB</b><span>idle ${fmtIdle(v.idle)}${v.state === 'overdue' ? ' · lease overdue' : ''}</span></div></div>`).join('')}</div>
        <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close data-act="wi-confirm">Save ${aff.reduce((s, v) => s + v.gb, 0)} GB</button></div>`);
    } else if (act === 'wi-confirm') {
      toast('Saving ' + D.hvms.filter(wouldSave).length + ' VMs…', root);
    } else if (act === 'wi-policy') {
      S.tab = 'config'; renderTabs(); renderConfig(N());
    } else if (act === 'onboard') {
      openDialog($('#hScrim'), `<div class="eyebrow">Onboard · step 1 of 3</div><h2>New developer</h2><p class="lead">Register, pick an allowance template, then the token is shown once.</p>
        <div class="field"><label>Username</label><input class="inp" placeholder="e.g. lena"></div>
        <div class="field"><label>Template</label><div class="seg sm"><button aria-selected="true">dev (24 GB · 8 vCPU)</button><button>senior</button><button>contractor</button></div></div>
        <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close data-act="toast" data-msg="lena registered · token shown once">Register &amp; issue token</button></div>`);
    } else if (act === 'rotate') {
      openDialog($('#hScrim'), `<div class="eyebrow">Token · shown once</div><h2>New token for ${a.dataset.u}</h2><p class="lead">Copy it now. It will not be shown again. The old token stops working immediately.</p>
        <div class="row"><input class="inp mono" readonly value="ctk_7Hq2…Xb91_${a.dataset.u}"><button class="btn">${ic('copy', 's')}Copy</button></div>
        <div class="foot"><button class="btn primary" data-close>Done</button></div>`);
    } else if (act === 'offboard') {
      const id = a.dataset.u, vms = D.hvms.filter(v => v.owner === id);
      confirmDanger($('#hScrim'), { eyebrow: 'Offboard · cascade preview', title: `Offboard ${id}?`, lead: 'Disables the account and deletes everything below.', name: id, cta: 'Offboard ' + id,
        impact: vms.map(v => ['lose', `${v.id} · ${v.gb} GB`, STATE_LABEL[v.state]]).concat([['note', 'Windows keys released', id === 'bob' ? 'MAK key A binding is released (used activations are not refunded)' : 'none held'], ['keep', 'Audit log and spend history stay', '']]), onConfirm: () => toast(id + ' offboarded', root) });
    } else if (act === 'delvm') {
      const id = a.dataset.vmO, v = D.hvms.find(x => x.id === id);
      confirmDanger($('#hScrim'), { title: `Delete ${id}?`, lead: 'The VM and its disk are deleted on buildbox. The owner is notified.', name: v.name, cta: 'Delete VM', impact: [['lose', `${v.gb} GB VM and its disk`, STATE_LABEL[v.state]], ['keep', 'Owner keeps their backups', 'on their own PC']], onConfirm: () => toast('Deleting ' + id + '…', root) });
    } else if (act === 'override') {
      const id = a.dataset.vmO;
      openDialog($('#hScrim'), `<div class="eyebrow">Overrides · ${id}</div><h2>Per-VM overrides</h2><p class="lead">These win over the owner's allowance and the host defaults.</p>
        <div class="row"><div class="field grow"><label>Idle action after</label><input class="inp" value="${id === 'bob/api-vm' ? 30 : ''}" placeholder="default 60 min"></div><div class="field grow"><label>RAM cap (GB)</label><input class="inp" placeholder="none"></div></div>
        <div class="row"><label class="row" style="gap:6px"><input type="checkbox"> Exempt from memory-pressure saves</label></div>
        <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close data-act="toast" data-msg="Overrides saved">Save overrides</button></div>`);
    } else if (act === 'apply-upd') {
      S.applyQueued = true; renderService(); toast('Apply queued: starts when the running job finishes', root);
    } else if (act === 'closeuser') { $('#uDrawer').classList.remove('open'); S.selUser = null; renderPeople(); }
    else if (act === 'saveuser') { $('#uDrawer').classList.remove('open'); toast('Saved ' + S.selUser + ' · role, allowance and overrides in one save', root); }
    else if (act === 'toast') toast(a.dataset.msg, root);
  }

  function search(q) {
    const m = $('#hSearchMenu'), inp = $('#hSearch');
    if (!q) { m.classList.remove('open'); return; }
    const vms = D.hvms.filter(v => v.id.includes(q.toLowerCase())).slice(0, 5), users = D.users.filter(u => u.id.includes(q.toLowerCase()));
    const jobs = D.jobs.filter(j => j.text.toLowerCase().includes(q.toLowerCase()));
    m.innerHTML = (vms.length ? '<div class="mh eyebrow">VMs</div>' + vms.map(v => `<button class="mi" data-vm="${v.id}"><span class="sw ${SWATCH[v.state]}"></span>${v.id}<span class="sub">${v.gb} GB</span></button>`).join('') : '') +
      (users.length ? '<div class="mh eyebrow">People</div>' + users.map(u => `<button class="mi" data-user="${u.id}">${ic('user', 's')}${u.id}<span class="sub">${u.role}</span></button>`).join('') : '') +
      (jobs.length ? '<div class="mh eyebrow">Jobs</div>' + jobs.map(j => `<button class="mi" data-tab="jobs">${ic('history', 's')}${j.text}</button>`).join('') : '') || '<div class="mh muted">No matches</div>';
    m.classList.add('open');
    const r = rel(inp, root.firstElementChild);
    m.style.left = (r.x + r.w - 300) + 'px'; m.style.top = (r.y + r.h + 6) + 'px';
  }

  shell();
  renderTabs(); fpMain(); renderPeople(); renderSpend(); renderLicences(); renderJobs(); renderMedia(); renderService(); renderConfig();
  root.addEventListener('click', onClick);
  root.addEventListener('input', e => {
    if (e.target.id === 'wiRange') {
      S.whatIdx = +e.target.value;
      if (S.whatIdx > 0 && S.measure === 'committed' && !S.autoNote) { S.measure = 'resident'; S.autoNote = true; }
      layout(); if (S.view === 'list') renderList(); renderInsp();
      // update result panel without re-creating the slider (keeps drag)
      const keep = $('#wiRange'); renderWhatif(); const nr = $('#wiRange'); nr.focus();
    }
    if (e.target.id === 'hSearch') search(e.target.value.trim());
    if (e.target.id === 'auditFilter') $('#auditList').innerHTML = auditRows(e.target.value);
  });
  root.addEventListener('keydown', e => {
    if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('.prow')) { e.preventDefault(); openUser(e.target.dataset.user); }
  });
  root.addEventListener('pointerover', e => {
    const tip = $('#hTip'), b = e.target.closest('.blk, [data-tipv]');
    if (!b) { tip.style.display = 'none'; return; }
    let html;
    if (b.classList.contains('blk')) {
      const v = D.hvms.find(x => x.id === b.dataset.vm);
      html = `<b>${v.gb} GB</b> <span class="k">${v.id}</span><br><span class="k">${STATE_LABEL[v.state]}${v.idle ? ' · idle ' + fmtIdle(v.idle) : ''}${v.demand ? ' · demand ' + v.demand + ' GB' : ''}</span>`;
    } else { const p = b.dataset.tipv.split(': '); html = `<b>${p[1]}</b> <span class="k">${p[0]}</span>`; }
    tip.innerHTML = html; tip.style.display = 'block';
    const r = rel(b, root.firstElementChild);
    tip.style.left = Math.min(1280 - tip.offsetWidth - 8, r.x + r.w / 2 - tip.offsetWidth / 2) + 'px';
    tip.style.top = (r.y - tip.offsetHeight - 6) + 'px';
  });
  root.addEventListener('focusin', e => { if (e.target.classList.contains('blk')) e.target.dispatchEvent(new Event('pointerover', { bubbles: true })); });

  document.addEventListener('route', e => {
    if (e.detail.surface !== 'host') return;
    const sub = e.detail.sub;
    if (sub[0] === 'whatif') { S.whatIdx = 2; S.measure = 'resident'; S.autoNote = true; }
    else if (sub[0] === 'zoom') { S.zoom = 'alice'; }
    else if (sub[0] === 'vm') { S.selVm = 'mara/scratch-3'; }
    else if (sub[0] === 'list') { S.view = 'list'; }
    else if (sub[0] === 'people' && sub[1]) { S.tab = 'people'; renderTabs(); openUser(sub[1]); }
    else if (sub[0] && $('[data-pane="' + sub[0] + '"]', root)) S.tab = sub[0];
    renderTabs();
    requestAnimationFrame(renderFloor);
  });
  window.addEventListener('resize', () => requestAnimationFrame(layout));
})();
