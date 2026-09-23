/* Control panel: the machine diagram */
(function () {
  const root = $('#s-panel');
  const S = {
    inst: 'agent-vm', sel: 'overview', unlocked: false, mic: true, ram: 16, cpu: 8,
    reprov: 'idle', behind: 2, updating: null, read: false,
    proj: Object.fromEntries(D.projects.map(p => [p.id, p.on])),
    wake: false, target: 'VS Code', svc: Object.fromEntries(D.services.map(s => [s.id, s.on]))
  };
  const SERIES = { claude: 'var(--teal)', codex: 'var(--amber)', opencode: 'var(--plum)' };
  const inst = () => D.instances[S.inst];
  const running = () => inst().state === 'running';

  function shell() {
    root.innerHTML = `
    <div class="win cp">
      <div class="titlebar">
        <div class="appmark">${GLYPH.replace('width="18" height="18"', 'width="14" height="14"')}</div>
        <div class="apptitle">Construct <span>· Control panel</span></div>
        <button class="inst-btn" id="instBtn" data-menu data-act="inst" aria-haspopup="menu"></button>
        <div class="spacer"></div>
        <span class="muted" style="font-size:11.5px" id="cpStale">updated 5 s ago</span>
        <button class="tb-icon" data-act="bell" title="Notifications">${ic('bell')}<span class="dot" id="cpDot">3</span></button>
        <button class="tb-icon" data-act="settings" title="Settings">${ic('gear')}</button>
        <div class="wctl"><span>${'<svg class="i s" viewBox="0 0 24 24"><path d="M5 12h14"/></svg>'}</span><span>${'<svg class="i s" viewBox="0 0 24 24"><rect x="5" y="5" width="14" height="14" rx="1"/></svg>'}</span><span class="x">${ic('x', 's')}</span></div>
      </div>
      <div class="body">
        <div class="canvas"><div class="cv" id="cv"></div></div>
        <aside class="sheet" id="sheet" aria-live="polite"></aside>
        <div class="track" id="track"></div>
      </div>
      <div class="menu" id="mInst"></div><div class="menu" id="mPower"></div><div class="menu" id="mOpen"></div>
      <div class="tip" id="cpTip"></div>
      <div class="scrim" id="cpScrim"></div>
      <div class="toasts"></div>
    </div>`;
  }

  /* ---------------- canvas ---------------- */
  function pcCard() {
    const on = running();
    const fw = D.forwards.filter(f => f.inst === S.inst && f.scope === 'client');
    const eps = fw.map(f => `<button class="ep sel-able" data-sel="forward:${f.id}" data-ep="${f.id}"><span class="led ${on ? 'on' : ''}"></span><span class="mono">localhost:${f.local}</span><span class="nub ${on ? '' : 'off'}"></span></button>`).join('')
      || `<div class="ep muted" style="font-size:11.5px">No forwards to this PC</div>`;
    return `<div class="card pc">
      <div class="ph"><div class="ico">${ic('pc')}</div><div><b>PC-1</b><span>Windows 11 · 64 GB · you</span></div></div>
      <div class="eyebrow">Forwards here</div>${eps}
      <button class="ep add" data-act="expose">${ic('plus', 's')}Expose a port…</button>
      <div class="eyebrow" style="margin-top:4px">Voice</div>
      <button class="ep sel-able" data-sel="mic" data-ep="mic">${ic('mic', 's')}<span>Shure MV7</span><span class="nub mic ${S.mic ? '' : 'off'}"></span></button>
      <div class="opens"><div class="eyebrow" style="padding:0">Open ${S.inst} in</div>
        <div class="row" style="gap:5px"><button class="btn primary" data-act="open" ${on ? '' : 'disabled'}>${ic('code', 's')}${S.target}</button><button class="btn" data-menu data-act="openmenu" style="flex:none;width:30px;padding:0" aria-label="More ways to open">${ic('chev', 's')}</button></div>
      </div>
    </div>`;
  }

  function usageCard() {
    const u = D.usage, tot = u.split.reduce((a, b) => a + b[1], 0), max = 14;
    return `<div class="card usage sel-able" data-sel="usage" tabindex="0">
      <div class="eyebrow">Spent today</div>
      <div class="big">${S.inst === 'agent-vm' ? '$4.12' : '$0.00'}</div>
      ${S.inst === 'agent-vm' ? `<div class="splitbar">${u.split.map(s => `<i style="flex:${s[1]};background:${SERIES[s[0]]}" title="${s[0]} $${s[1].toFixed(2)}"></i>`).join('')}</div>
      <div class="lg">${u.split.map(s => `<span><i style="background:${SERIES[s[0]]}"></i>${s[0]} $${s[1].toFixed(2)}</span>`).join('')}</div>
      <div class="cols" aria-label="Spend per day, last 7 days">${u.week.map((d, i) => `<div class="c ${i === 6 ? 'today' : ''}" data-tipv="${d[0]}: $${d[1].toFixed(2)}">${i === 3 || i === 6 ? `<span class="v">${d[1].toFixed(1)}</span>` : ''}<i style="height:${Math.round(d[1] / max * 44)}px"></i></div>`).join('')}</div>
      <div class="days">${u.week.map(d => `<span>${d[0][0]}</span>`).join('')}</div>` : `<p class="muted" style="font-size:12px;margin:6px 0 0">No agent activity while the VM is saved.</p>`}
      <div class="foot"><span>Month <b>$${u.month.toFixed(2)}</b></span><span>All <b>$${u.all.toFixed(2)}</b></span></div>
    </div>`;
  }

  function chassis() {
    const v = inst(), on = running(), saved = v.state === 'saved';
    const agents = D.agents.map(a => {
      const st = !on ? 'asleep' : a.state;
      const line = !on ? 'VM saved' : a.state === 'working' ? `working · ${a.since} · ${a.repo}` : a.state === 'idle' ? (a.last ? `idle · ${a.last}` : `idle · serve ${a.serve}`) : `web UI · ${a.threads} threads`;
      return `<button class="agent sel-able ${st === 'working' ? 'working' : ''} ${!on ? 'asleep' : ''}" data-sel="agent:${a.id}">
        <span class="av ${st === 'working' ? 'working' : ''}">${a.mark}</span>
        <span style="min-width:0"><b>${a.name}</b><span class="s">${line}</span></span>
        ${a.update && on ? `<span class="upd pill amber" style="font-size:10px;line-height:15px;padding:0 5px">${a.update}</span>` : ''}</button>`;
    }).join('');
    const carts = D.projects.map(p => `<button class="cart sel-able ${S.proj[p.id] ? '' : 'off'}" data-sel="project:${p.id}" title="${p.id} · ${S.proj[p.id] ? 'selected for next reprovision' : 'not selected'}">
      ${p.isNew ? '<span class="newtag">NEW</span>' : ''}<span class="mark">${ic('box')}</span><span class="band">${p.id}</span></button>`).join('')
      + `<button class="cart add" data-act="newproj" title="New project profile">${ic('plus')}</button>`;
    const socks = D.services.map(s => `<button class="sock sel-able ${S.svc[s.id] && on ? 'on' : ''}" data-sel="service:${s.id}" title="${s.desc}"><span class="hole"><i></i></span><b>${s.name.replace('VS Code ', '')}</b><span>${s.port}</span></button>`).join('');
    const used = on ? v.ramUsed : 0;
    return `<div class="chassis ${saved ? 'saved' : ''}" id="chassis">
      <div class="plate">
        <div>
          <h2>${v.id}</h2>
          <div class="st">${on ? `<span class="led on"></span>Running · up ${v.uptime}` : `<span class="led"></span>Saved by idle policy ${v.savedAgo} ago · resumes where it left off`}</div>
          <div class="spec">${v.os} · ${v.vcpu} vCPU · ${v.disk} GB disk · ${v.whereShort}</div>
          <div class="chips">
            ${S.behind && S.inst === 'agent-vm' ? `<button class="pill amber" data-sel="lifecycle">${ic('refresh', 's')}c21978b → b5c4348 · ${S.behind} behind</button>` : `<span class="pill good">${ic('check', 's')}up to date · ${v.installed}</span>`}
            ${S.inst === 'agent-vm' ? `<button class="pill amber" data-sel="resources">${ic('disk', 's')}disk 88% used</button>` : ''}
            <button class="pill teal" data-sel="update">${ic('download', 's')}Construct 3a91f0e</button>
          </div>
        </div>
        <div class="gauge">
          <button class="bigring sel-able" data-sel="resources" title="RAM ${used} of ${v.ram} GB · click to resize">${ringSvg(used, v.ram, { r: 30, sw: 7, color: on ? null : 'var(--stone)', track: on ? 'var(--teal-soft)' : 'var(--stone-soft)' })}<span class="rv">${on ? used : v.ram}<small>${on ? 'of ' + v.ram + ' GB' : 'GB saved'}</small></span></button>
          ${on ? `<button class="pwr" data-menu data-act="power" title="Power">${ic('power')}</button>` : `<button class="btn primary" data-act="resume">${ic('play', 's')}Resume</button>`}
        </div>
      </div>
      <div class="bay"><h4>Agents <span class="r">${on ? '1 working · 3 idle' : 'asleep with the VM'}</span></h4><div class="slots">${agents}</div></div>
      <div class="bay"><h4>Projects <span class="applies reprov">next reprovision</span><span class="r">${Object.values(S.proj).filter(Boolean).length} of ${D.projects.length} selected</span></h4><div class="carts">${carts}</div>
        <button class="syncline link" style="text-decoration:none" data-sel="sync">${ic('refresh', 's')}<span>Config sync · synced ${D.sync.last} · <b style="color:var(--amber-ink)">1 new profile from VM</b></span></button></div>
      <div class="bay"><h4>Services <span class="r">click a socket to configure</span></h4><div class="sockets">${socks}</div></div>
    </div>`;
  }

  function kids() {
    if (S.inst !== 'agent-vm') return `<div class="kids"><div class="eyebrow">Child VMs</div><button class="kid-add" data-act="newchild">${ic('plus', 's')}<br>New child VM</button></div>`;
    const c = D.child;
    return `<div class="kids"><div class="eyebrow">Child VMs</div>
      <div class="card kid sel-able" data-sel="child" tabindex="0" id="kid">
        <div class="row" style="gap:6px"><span class="led on"></span><span class="pill plum" style="font-size:10px">${ic('windows', 's')}Win 11</span></div>
        <b style="margin-top:6px">${c.id}</b><div class="s">${c.ram} GB · ${c.vcpu} vCPU · eval</div>
        <div class="meter"><i style="width:${c.leaseLeftPct}%"></i></div><div class="s">lease ${c.lease} left</div>
      </div>
      <button class="kid-add" data-act="newchild">${ic('plus', 's')}<br>New child VM</button></div>`;
  }

  function renderCanvas() {
    const v = inst();
    const cv = $('#cv');
    cv.innerHTML = `<svg class="wires" id="cpWires"></svg>
      <button class="hoststrip sel-able ${v.backend === 'hyperv-remote' ? 'remote' : ''}" data-sel="host">${v.backend === 'hyperv-remote' ? ic('server', 's') + '<b style="font-weight:600">buildbox</b><span class="muted">.example.local:7462 · constructd 1.14.2 · idle: save after 60 min</span>' : ic('home', 's') + 'Runs on this PC · local Hyper-V · no host'}</button>
      <div class="pccol">${pcCard()}${usageCard()}</div>
      ${chassis()}${kids()}`;
    $('#instBtn').innerHTML = `<span class="led ${running() ? 'on' : ''}"></span>${v.id}<span class="sub">${running() ? 'running' : 'saved'} · ${v.whereShort}</span>${ic('chev', 's')}`;
    markSel();
    requestAnimationFrame(drawWires);
  }

  function drawWires() {
    const cv = $('#cv'); if (!cv || !cv.offsetParent) return;
    const svg = $('#cpWires'), ch = $('#chassis');
    svg.setAttribute('width', cv.clientWidth); svg.setAttribute('height', cv.clientHeight);
    $$('.wl', cv).forEach(n => n.remove());
    const c = rel(ch, cv), on = running();
    let d = '';
    $$('[data-ep]', cv).forEach(ep => {
      const r = rel(ep, cv), y = r.y + r.h / 2, x1 = r.x + r.w, x2 = c.x;
      const id = ep.dataset.ep;
      let cls, label, lc = '', title;
      if (id === 'mic') {
        cls = S.mic ? 'w-mic' : 'w-off'; lc = 'mic' + (S.mic ? '' : ' dim');
        label = ic('mic', 's') + (S.mic ? 'armed' : 'off'); title = 'Microphone passthrough to ' + S.inst;
      } else {
        const f = D.forwards.find(q => q.id === id);
        cls = on ? 'w-fwd' : 'w-off'; lc = on ? '' : 'dim';
        label = ':' + f.vm + ' ' + (f.id === 'f5173' ? 'vite' : 'api'); title = 'vm:' + f.vm + ' → localhost:' + f.local + ' · ' + f.label;
      }
      d += `<path class="${cls}" d="M${x1} ${y} H${x2}"/>`;
      if (cls === 'w-fwd') d += `<path class="flow" d="M${x1} ${y} H${x2}"/>`;
      d += `<circle cx="${x2}" cy="${y}" r="4.5" style="stroke:${cls === 'w-fwd' ? 'var(--teal)' : cls === 'w-mic' ? 'var(--amber)' : 'var(--stone)'}"/>`;
      const b = document.createElement('button');
      b.className = 'wl ' + lc + (S.sel === (id === 'mic' ? 'mic' : 'forward:' + id) ? ' sel' : '');
      b.innerHTML = label; b.title = title; b.dataset.sel = id === 'mic' ? 'mic' : 'forward:' + id;
      b.style.left = (x1 + (x2 - x1) / 2) + 'px'; b.style.top = y + 'px';
      cv.appendChild(b);
    });
    // host forward (dev-2) from chassis top to host strip
    if (S.inst === 'dev-2') {
      const hs = rel($('.hoststrip', cv), cv), x = c.x + c.w - 60, y1 = hs.y + hs.h, y2 = c.y;
      d += `<path class="w-host" d="M${x} ${y1 + 2} V${y2}"/><circle cx="${x}" cy="${y2}" r="4.5" style="stroke:var(--stone)"/>`;
      const b = document.createElement('button');
      b.className = 'wl dim'; b.innerHTML = ':3000 LAN · queued'; b.dataset.sel = 'forward:f3000'; b.title = 'Host forward on the buildbox LAN, opens when dev-2 resumes';
      b.style.left = (x - 70) + 'px'; b.style.top = ((y1 + y2) / 2 + 2) + 'px';
      cv.appendChild(b);
    }
    const kid = $('#kid');
    if (kid) {
      const k = rel(kid, cv), y = k.y + 26, x1 = c.x + c.w;
      d += `<circle cx="${x1}" cy="${y}" r="4.5" style="stroke:var(--plum)"/><path class="w-child" d="M${x1 + 4} ${y} H${k.x}"/>`;
    }
    svg.innerHTML = d;
  }

  function markSel() {
    $$('[data-sel]', root).forEach(el => el.classList.toggle('sel', el.dataset.sel === S.sel));
  }

  /* ---------------- lifecycle track ---------------- */
  function renderTrack() {
    const t = $('#track');
    const isA = S.inst === 'agent-vm';
    const rp = S.reprov;
    const steps = isA ? [
      ['done', 'Provisioned', 'Sep 20 · c21978b'],
      ['now', 'Construct now', 'b5c4348 · since 09:10'],
      [rp === 'running' ? 'running' : 'next', 'Reprovision',
        rp === 'queued' ? 'waits for Claude' : rp === 'running' ? 'step 9 of 31…' : rp === 'done' ? 'done · current' : '+2 commits',
        rp === 'idle' ? `<button class="btn sm primary" data-act="reprov">${ic('refresh', 's')}Reprovision</button>` : rp === 'queued' ? `<button class="btn sm" data-act="reprov-cancel">Cancel queue</button>` : rp === 'running' ? `<div class="prog"><i style="width:34%" id="rpProg"></i></div>` : ''],
      ['next', 'Update Construct', S.updating === 'done' ? 'installed 3a91f0e' : '3a91f0e · 4 commits', S.updating === 'done' ? '' : `<button class="btn sm" data-sel="update">${ic('download', 's')}Review</button>`]
    ] : [
      ['done', 'Created on buildbox', 'Aug 30 · 9c04d12'],
      ['done', 'Provisioned', 'Sep 22 · 3a91f0e'],
      ['now', 'Saved', 'idle policy · 41 min'],
      ['next', 'Reprovision', 'nothing pending', `<button class="btn sm" data-act="reprov">${ic('refresh', 's')}Reprovision</button>`]
    ];
    t.innerHTML = `<div class="lbl"><h3>Lifecycle</h3><p>${isA ? 'routine steps ›' : 'dev-2 on buildbox'}</p></div>
      <div class="steps">${steps.map(s => `<div class="step ${s[0]}"><span class="dot"></span><div class="t">${s[1]}</div><div class="d">${s[2]}</div>${s[3] || ''}</div>`).join('')}</div>
      <div class="danger ${S.unlocked ? 'unlocked' : ''}">
        <div class="dh">${ic(S.unlocked ? 'unlock' : 'lock', 's')}Rebuild &amp; remove <span class="muted">· rare</span><label><input type="checkbox" class="tog" data-act="unlock" ${S.unlocked ? 'checked' : ''} aria-label="Unlock rebuild and remove actions">${S.unlocked ? 'Unlocked' : 'Locked'}</label></div>
        <div class="dg">
          <button class="btn sm risky" data-danger="reinstall" ${S.unlocked ? '' : 'aria-disabled="true"'}>Reinstall</button>
          <button class="btn sm risky" data-danger="redownload" ${S.unlocked ? '' : 'aria-disabled="true"'}>Redownload</button>
          <button class="btn sm risky" data-danger="custom" ${S.unlocked ? '' : 'aria-disabled="true"'}>Custom…</button>
          <button class="btn sm risky" data-danger="remove" ${S.unlocked ? '' : 'aria-disabled="true"'}>Remove</button>
          <button class="btn sm" data-danger="host" style="grid-column:span 2" ${S.unlocked ? '' : 'aria-disabled="true"'}>${ic('server', 's')}Make this PC a host…</button>
        </div>
      </div>`;
  }

  /* ---------------- side sheets ---------------- */
  const tag = k => `<span class="applies ${k}">${{ live: 'live', reprov: 'next reprovision', restart: 'restart to apply', once: 'one-time' }[k]}</span>`;
  function setRow(t, d, on, applies, extra, hl) {
    return `<div class="set ${hl ? 'hl' : ''}"><div class="t">${t}${applies ? tag(applies) : ''}</div>${extra || `<input type="checkbox" class="tog" ${on ? 'checked' : ''} aria-label="${t}">`}<div class="d">${d}</div></div>`;
  }

  const SH = {
    overview() {
      const n = D.inbox;
      const todos = S.inst === 'dev-2' ? `
        <div class="todo teal"><div class="ic">${ic('play', 's')}</div><div><b>Saved by idle policy 41 min ago</b><p>dev-2 wrote its RAM to disk on buildbox after 60 idle minutes. Resume takes about 20 s and picks up where it left off.</p><button class="btn sm primary" data-act="resume">Resume dev-2</button></div></div>
        <div class="todo"><div class="ic">${ic('link', 's')}</div><div><b>Host forward :3000 is queued</b><p>Opened by Claude before the save. It opens on the buildbox LAN when dev-2 resumes.</p><button class="btn sm ghost" data-sel="forward:f3000">Forward</button></div></div>
        <div class="todo teal"><div class="ic">${ic('download', 's')}</div><div><b>Construct update available</b><p>b5c4348 → 3a91f0e on this PC. dev-2 was provisioned at 3a91f0e already.</p><button class="btn sm" data-sel="update">Review update</button></div></div>` : `
        <div class="todo"><div class="ic">${ic('refresh', 's')}</div><div><b>${S.behind ? '2 commits behind host' : 'Up to date'}</b><p>${S.behind ? 'Provisioned at c21978b, installed Construct is b5c4348. Claude is mid-turn, so the safe option is to reprovision when it finishes.' : 'Reprovisioned just now.'}</p>${S.behind ? `<div class="row"><button class="btn sm primary" data-act="reprov">Reprovision…</button><button class="btn sm ghost" data-sel="lifecycle">What changes?</button></div>` : ''}</div></div>
        <div class="todo teal"><div class="ic">${ic('download', 's')}</div><div><b>Construct update available</b><p>b5c4348 → 3a91f0e, 4 commits. The VM isn't touched; the Companion restarts.</p><button class="btn sm" data-sel="update">Review update</button></div></div>
        <div class="todo"><div class="ic">${ic('disk', 's')}</div><div><b>Disk 88% full</b><p>129 of 146 GB used. The disk only grows on reinstall.</p><button class="btn sm ghost" data-sel="resources">Resources</button></div></div>`;
      return {
        eyebrow: S.inst + ' · now', title: 'What needs you',
        body: todos + `
        <h5>Notifications <span style="margin-left:auto;text-transform:none;letter-spacing:0"><button class="link" data-act="markread" style="font-size:11.5px">Mark all read</button></span></h5>
        ${n.map(x => `<div class="nrow ${x.level === 'error' ? 'err' : 'inf'}" style="${S.read ? 'opacity:.6' : ''}"><span class="ic">${ic(x.level === 'error' ? 'warn' : 'info', 's')}</span><div><b>${x.text}</b><span>${x.agent} · ${x.repo}</span></div><span class="t">${x.t}</span></div>`).join('')}
        <p class="muted" style="font-size:11.5px;margin:8px 8px 0">From <span class="mono">construct notify</span>. Older items: <button class="link">show 12 more</button></p>`
      };
    },
    agent(id) {
      const a = D.agents.find(x => x.id === id);
      const w = a.state === 'working';
      return {
        eyebrow: 'Coding agent · plugged into ' + S.inst, title: a.name,
        body: `
        <div class="hero-card"><div class="row" style="gap:10px"><span class="av ${w ? 'working' : ''}" style="width:34px;height:34px;border-radius:10px">${a.mark}</span><div class="grow">
          <div class="big" style="font-size:17px">${w ? 'Working' : a.state === 'web' ? 'Web UI running' : 'Idle'}</div>
          <div class="muted" style="font-size:12px">${w ? `repo ${a.repo} · turn started ${a.since} ago` : a.state === 'web' ? `${a.threads} threads active` : a.last ? 'last turn ' + a.last : a.task}</div></div>
          ${w ? `<button class="bellbtn" style="width:32px;height:32px" data-act="wake" aria-pressed="${S.wake}" title="Notify me when Claude goes idle">${ic('bell', 's')}</button>` : ''}</div>
          ${w ? `<p style="margin:10px 0 0;font-size:12.5px">"${a.task}"</p>` : ''}
        </div>
        ${w ? `<p class="muted" style="font-size:11.5px;margin:6px 2px 0">${S.wake ? 'You\'ll get a Windows toast when Claude goes idle.' : 'Bell: get a toast when this turn ends. No notify call needed.'}</p>` : ''}
        <h5>Version ${a.update ? tag('live') : ''}</h5>
        <dl class="kv"><dt>Installed</dt><dd class="mono">${a.version}</dd>${a.update ? `<dt>Available</dt><dd class="mono">${a.update}</dd>` : `<dt>Status</dt><dd>up to date</dd>`}</dl>
        ${a.update ? `<div class="callout teal">Updating runs on the VM now. ${w ? '<b>Claude is working</b>: the update waits until this turn ends.' : ''}</div><button class="btn primary full" data-act="agent-update">${ic('download', 's')}Update to ${a.update}${w ? ' after this turn' : ''}</button>` : ''}
        <h5>Today</h5>
        <dl class="kv"><dt>Tokens</dt><dd>${a.tokens || '—'}</dd><dt>Cost</dt><dd>${a.cost != null ? money(a.cost) : 'counted under the agent it drives'}</dd></dl>
        <h5>Open</h5>
        <div class="row" style="flex-wrap:wrap"><button class="btn sm">${ic('term', 's')}Attach terminal</button><button class="btn sm">${ic('ext', 's')}Open in T3</button><button class="btn sm ghost">${ic('note', 's')}Logs</button></div>
        <p class="muted" style="font-size:11.5px;margin-top:14px">Which agents are installed is chosen at reprovision. <button class="link" data-act="update-all">Update all agents</button></p>`
      };
    },
    forward(id) {
      const f = D.forwards.find(x => x.id === id), host = f.scope === 'host';
      return {
        eyebrow: host ? 'Host forward · buildbox LAN' : 'Forward · wire to PC-1', title: host ? 'buildbox:' + f.vm : 'localhost:' + f.local,
        body: `
        <div class="hero-card"><div class="row"><span class="led ${f.state === 'open' ? 'on' : 'warn'}"></span><b>${f.state === 'open' ? 'Open' : 'Queued'}</b><span class="muted">· ${f.label}</span></div>
          <div class="mono" style="margin-top:8px;font-size:12px">vm:${f.vm} → ${host ? 'buildbox LAN :' + f.vm : 'localhost:' + f.local}</div>
          ${f.note ? `<div class="muted" style="font-size:12px;margin-top:4px">${f.note}</div>` : ''}</div>
        <dl class="kv" style="margin-top:12px"><dt>Opened by</dt><dd>${f.by} via <span class="mono">construct expose</span></dd><dt>When</dt><dd>${f.ago} ago</dd><dt>Scope</dt><dd>${host ? 'host (reachable on the buildbox LAN)' : 'client (this PC only)'}</dd></dl>
        <div class="field"><label for="fwLabel">Label</label><input class="inp" id="fwLabel" value="${f.label}"></div>
        ${host ? '' : `<div class="field"><label for="fwPort">Local port</label><input class="inp mono" id="fwPort" value="${f.local}"><span class="hint">Change it if the port is busy on PC-1.</span></div>`}`,
        foot: `<button class="btn ghost" data-act="fwd-close">${ic('x', 's')}Close forward</button><button class="btn" data-act="copy">${ic('copy', 's')}Copy URL</button><button class="btn primary" data-act="fwd-open" ${f.state === 'open' ? '' : 'disabled'}>${ic('ext', 's')}Open</button>`
      };
    },
    mic() {
      return {
        eyebrow: 'Voice · wire to ' + S.inst, title: 'Microphone passthrough',
        body: `
        ${setRow('Passthrough', S.mic ? 'Armed. Streams only while an agent is listening (/voice).' : 'Off. Agents can\'t hear you.', S.mic, 'live', `<input type="checkbox" class="tog" data-act="mic" ${S.mic ? 'checked' : ''} aria-label="Microphone passthrough">`)}
        <div class="field"><label for="micDev">Capture device</label><select class="inp" id="micDev"><option>Shure MV7</option><option>Realtek Audio (built-in)</option><option>Windows default</option></select></div>
        <div class="hero-card row"><span class="led ${S.mic ? 'warn' : ''}"></span><div><b>${S.mic ? 'Armed, not streaming' : 'Not armed'}</b><div class="muted" style="font-size:12px">The wire turns coral and animates while audio flows.</div></div></div>
        <h5>Install ${tag('reprov')}</h5>
        ${setRow('Install mic passthrough on the VM', 'Saved preference. Removing it takes effect on the next reprovision.', true)}`
      };
    },
    project(id) {
      const p = D.projects.find(x => x.id === id);
      return {
        eyebrow: 'Project profile · cartridge', title: p.id,
        body: `
        ${setRow('Include in next reprovision', p.isNew ? 'Auto-discovered on the VM. Selected by default.' : S.proj[id] ? 'Cloned and set up on the next reprovision.' : 'Not cloned. The profile stays in your config.', S.proj[id], 'reprov', `<input type="checkbox" class="tog" data-act="proj-toggle" data-id="${id}" ${S.proj[id] ? 'checked' : ''} aria-label="Include ${id}">`)}
        ${p.dirty ? `<div class="callout">${ic('warn', 's')} <b>${p.dirty}</b> in /root/repos/${id}. Reprovision keeps them; reinstall would lose them.</div>` : ''}
        <h5>Repositories</h5>
        ${p.repos.map(r => `<div class="repo"><input class="inp mono" value="${r[0]}" aria-label="Repository URL"><input class="inp mono" value="${r[1]}" placeholder="dir" aria-label="Directory"><button class="tb-icon" style="width:26px;height:30px" aria-label="Remove repo">${ic('x', 's')}</button></div>`).join('')}
        <button class="btn sm ghost">${ic('plus', 's')}Add repo</button>
        <h5>Runtimes (SDKs)</h5>
        <textarea class="inp" rows="3" aria-label="SDKs">${p.sdks}</textarea>
        <div class="hint muted" style="font-size:11.5px">One per line: <span class="mono">name = version</span></div>
        <h5>MCP servers</h5>
        <textarea class="inp" rows="4" id="mcpBox" aria-label="MCP servers JSON">${p.mcp}</textarea>
        <div class="muted" style="font-size:11.5px" id="mcpMsg">${ic('check', 's')} Valid JSON</div>
        <h5>Setup</h5>
        <div class="field" style="margin-top:0"><label>Host packages</label><textarea class="inp" rows="2">${p.pkgs}</textarea></div>
        <div class="field"><label>Provision commands</label><textarea class="inp" rows="2">${p.cmds}</textarea><span class="hint">Run in order after clone. Must be safe to run again.</span></div>
        <button class="btn sm ghost" style="color:var(--coral-ink)" data-act="proj-del" data-id="${id}">${ic('trash', 's')}Delete profile</button>`,
        foot: `<span class="note">Saved to projects/${id}.json</span><button class="btn ghost" data-sel="overview">Cancel</button><button class="btn primary" data-act="proj-save">Save profile</button>`
      };
    },
    sync() {
      return {
        eyebrow: 'Projects · config', title: 'Config sync',
        body: `
        <div class="hero-card"><div class="row"><span class="led on"></span><b>Synced ${D.sync.last}</b></div><div class="muted" style="font-size:12px;margin-top:4px">1 new profile came from the VM: <b style="color:var(--ink)">jarvis</b>. It is selected for the next reprovision.</div></div>
        <div class="row" style="margin-top:10px"><button class="btn primary" data-act="toast" data-msg="Config synced just now">${ic('refresh', 's')}Sync now</button><button class="btn" data-act="toast" data-msg="Exported config to Downloads\\construct-config.zip">${ic('download', 's')}Export config</button></div>
        <h5>Remote config repos</h5>
        ${D.sync.remotes.map(r => `<div class="nrow"><span class="ic">${ic('link', 's')}</span><div><b class="mono" style="font-size:12px">${r[0]}</b><span>${r[1]} · pulls on sync, pushes local edits</span></div><button class="btn sm ghost">Remove</button></div>`).join('')}
        <div class="field"><label for="addRemote">Add a config repo (URL or path)</label><div class="row"><input class="inp mono" id="addRemote" placeholder="https://… or C:\\path"><button class="btn">Add</button></div></div>
        <div class="callout teal">Conflicts show here with a side-by-side choice. None right now.</div>`
      };
    },
    service(id) {
      return {
        eyebrow: 'Services · sockets', title: 'Access & services',
        body: D.services.map(s => setRow(s.name + ' <span class="mono muted" style="font-weight:400;font-size:11px">' + s.port + '</span>', s.desc + (s.id === 't3' ? '. Enabling installs it now and opens the web UI; chat history survives a reinstall.' : ''), S.svc[s.id], s.applies, null, s.id === id)).join('') +
          `<h5>More</h5>` +
          setRow('Claude Code live streaming', 'Streams thinking and replies over Remote-SSH as they generate.', true, 'reprov') +
          setRow('OpenCode background watcher', 'Adds background, background_output and background_kill tools.', true, 'reprov') +
          setRow('Patched T3 Code + Desktop', 'Adds voice input and usage-limit recovery. Longer build.', false, 'reprov') +
          `<div class="field"><label>T3 channel</label><div class="seg sm"><button aria-selected="true">stable</button><button>nightly</button></div></div>`,
        foot: `<span class="note">4 changes wait for reprovision</span><button class="btn primary" data-act="toast" data-msg="Saved. Live settings applied; the rest apply on the next reprovision.">Save</button>`
      };
    },
    resources() {
      const v = inst();
      return {
        eyebrow: 'Hardware · ' + v.id, title: 'Resources',
        body: `
        <h5>Memory ${tag('restart')}</h5>
        <div class="rng"><input type="range" min="4" max="48" step="1" value="${S.ram}" data-act="ram" aria-label="RAM in GB"><output id="ramOut">${S.ram} GB</output></div>
        <div class="rng-scale"><span>4 GB</span><span>PC-1 has 64 GB · ${64 - S.ram} GB left for Windows</span><span>48</span></div>
        <div class="muted" style="font-size:12px;margin-top:6px">In use now: ${running() ? v.ramUsed + ' GB' : 'saved'}</div>
        <h5>vCPUs ${tag('restart')}</h5>
        <div class="rng"><input type="range" min="1" max="16" step="1" value="${S.cpu}" data-act="cpu" aria-label="vCPUs"><output id="cpuOut">${S.cpu}</output></div>
        <div class="rng-scale"><span>1</span><span>16 logical cores on PC-1</span><span>16</span></div>
        <div class="callout ${S.ram !== 16 || S.cpu !== 8 ? '' : 'teal'}" id="resNote">${S.ram !== 16 || S.cpu !== 8 ? `<b>Restart to apply.</b> The VM shuts down, resizes and starts again. No reinstall. Needs one UAC prompt, and <b>Claude is working</b>.` : 'Matches the running VM. Drag a slider to plan a change.'}</div>
        <h5>Disk ${tag('once')}</h5>
        <div class="row"><div class="grow"><div class="meter warn"><i style="width:88%"></i></div><div class="muted" style="font-size:11.5px;margin-top:4px">129 of 146 GB used</div></div></div>
        <div class="field"><label for="diskSz">Disk size on next reinstall / redownload</label><div class="row"><input class="inp" id="diskSz" value="200" style="width:90px"> GB</div></div>
        <h5>Other</h5>
        ${setRow('Automatic checkpoints', 'Snapshot at every start. Off by default: it grows the disk and slows I/O.', false, 'live')}
        <div class="field"><label>Ubuntu release for Redownload</label><select class="inp"><option>26.04 LTS (latest)</option><option>24.04 LTS</option></select></div>`,
        foot: `<button class="btn ghost" data-act="res-reset">Reset</button><button class="btn primary" data-act="res-apply" ${S.ram !== 16 || S.cpu !== 8 ? '' : 'disabled'}>${ic('refresh', 's')}Save &amp; restart</button>`
      };
    },
    child() {
      const c = D.child;
      return {
        eyebrow: 'Child VM of agent-vm', title: c.id,
        body: `
        <div class="hero-card"><div class="row"><span class="led on"></span><b>Running</b><span class="muted">· ${c.os}</span></div>
        <div class="meter" style="margin-top:10px;background:var(--plum-soft)"><i style="width:${c.leaseLeftPct}%;background:var(--plum)"></i></div>
        <div class="row between muted" style="font-size:11.5px;margin-top:4px"><span>lease ${c.lease} left</span><span>of 8h</span></div></div>
        <dl class="kv" style="margin-top:12px"><dt>RAM / vCPU</dt><dd>${c.ram} GB · ${c.vcpu} vCPU</dd><dt>Licence</dt><dd>${c.licence}</dd><dt>Created by</dt><dd>Claude, for UI tests of construct</dd><dt>Reachable</dt><dd>from agent-vm only</dd></dl>
        <div class="row" style="flex-wrap:wrap;margin-top:12px"><button class="btn primary">${ic('term', 's')}Console</button><button class="btn">RDP</button><button class="btn" data-act="toast" data-msg="Lease extended by 4h">${ic('clock', 's')}Extend 4h</button><button class="btn ghost">Shut down</button></div>
        <h5>Remove</h5><button class="btn sm ghost" style="color:var(--coral-ink)" data-act="child-del">${ic('trash', 's')}Delete win-test…</button>`
      };
    },
    host() {
      const remote = inst().backend === 'hyperv-remote';
      return {
        eyebrow: 'Where it runs', title: remote ? 'buildbox' : 'This PC',
        body: remote ? `
        <dl class="kv"><dt>Host</dt><dd class="mono" style="font-size:12px">buildbox.example.local:7462</dd><dt>Service</dt><dd>constructd 1.14.2</dd><dt>Your allowance</dt><dd>28 GB RAM · 12 vCPU (admin)</dd></dl>
        <h5>Idle policy ${tag('live')}</h5>
        <div class="row"><span>Idle for</span><input class="inp" value="60" style="width:64px"><span>min →</span><select class="inp" style="width:auto"><option>save (RAM to disk)</option><option>shut down</option><option>never</option></select></div>
        <p class="muted" style="font-size:12px">The host service decides, so it works with this PC switched off. A VM stays up while an agent is working, even with nobody connected.</p>
        <h5>Host forwards</h5><div class="nrow"><span class="ic">${ic('link', 's')}</span><div><b class="mono" style="font-size:12px">:3000 · buildbox LAN</b><span>queued until dev-2 resumes</span></div><span></span></div>` : `
        <dl class="kv"><dt>Backend</dt><dd>Local Hyper-V</dd><dt>Machine</dt><dd>PC-1 · 64 GB · 16 threads</dd><dt>Idle policy</dt><dd>Remote VMs only. A local VM runs until you stop it.</dd></dl>
        <h5>Share this PC as a host</h5>
        <p style="font-size:12.5px;color:var(--ink-2)">Set up a Construct host for other users and adopt agent-vm with its data and settings. You become the host admin.</p>
        <button class="btn" data-danger="host">${ic('server', 's')}Make this PC a host…</button>`
      };
    },
    usage() {
      const u = D.usage, max = 15;
      return {
        eyebrow: 'Token usage & cost · agent-vm', title: 'Spend',
        body: `
        <div class="seg sm" style="margin-bottom:10px"><button aria-selected="true">7 days</button><button>This month</button><button>All time</button></div>
        <div class="row" style="gap:16px"><div><div class="eyebrow">Today</div><div style="font:600 26px/1 var(--serif)">$4.12</div></div><div><div class="eyebrow">Month</div><div style="font:600 26px/1 var(--serif)">$86.30</div></div><div><div class="eyebrow">All time</div><div style="font:600 26px/1 var(--serif)">$412.77</div></div></div>
        <div class="bigchart" aria-label="Spend per day">
          ${[5, 10, 15].map(g => `<div class="gl" style="bottom:${g / max * 100}%"><span>$${g}</span></div>`).join('')}
          ${u.week.map((d, i) => `<div class="c ${i === 6 ? 'today' : ''}" data-tipv="${d[0]}: $${d[1].toFixed(2)}">${i === 6 || d[1] === 14 ? `<span class="v">$${d[1].toFixed(1)}</span>` : ''}<i style="height:${d[1] / max * 100}%"></i></div>`).join('')}
        </div>
        <div class="days" style="gap:10px">${u.week.map(d => `<span style="font-size:10.5px">${d[0]}</span>`).join('')}</div>
        <h5>By agent · today</h5>
        <table class="tbl"><thead><tr><th>Agent</th><th class="r">Tokens</th><th class="r">Cost</th></tr></thead><tbody>
        ${D.agents.filter(a => a.cost != null).map(a => `<tr><td><span class="lg"><span><i style="background:${SERIES[a.id]}"></i>${a.name}</span></span></td><td class="r">${a.tokens}</td><td class="r">${money(a.cost)}</td></tr>`).join('')}
        <tr><td><b>Total</b></td><td class="r">2.4M</td><td class="r"><b>$4.12</b></td></tr></tbody></table>`,
        foot: `<span class="note">Chart values are also in the table.</span><button class="btn">${ic('download', 's')}Export CSV</button>`
      };
    },
    update() {
      const u = D.update, st = S.updating;
      return {
        eyebrow: 'Construct', title: 'Update available',
        body: `
        <div class="hero-card"><div class="row"><span class="mono">${u.installed}</span>${ic('arrowr', 's')}<span class="mono" style="color:var(--teal-ink);font-weight:700">${u.latest}</span><span class="pill teal" style="margin-left:auto">4 commits</span></div></div>
        <ul class="commits">${u.commits.map(c => `<li><span class="mono">${c[0]}</span><span>${c[1]}</span></li>`).join('')}</ul>
        <div class="callout teal"><b>Your VMs keep running.</b> This updates the scripts, the panel extension and the Companion. The Companion closes and restarts by itself. Run it non-elevated.</div>
        ${st ? `<h5>Progress</h5><div class="prog"><i style="width:${st === 'done' ? 100 : 55}%"></i></div><div class="muted" style="font-size:12px;margin-top:4px">${st === 'done' ? 'Updated to 3a91f0e. The Companion restarted.' : 'Reinstalling the panel extension (step 3 of 5)…'}</div>` : ''}
        <p class="muted" style="font-size:12px">After updating, agent-vm is 6 commits behind until you reprovision.</p>`,
        foot: st === 'done' ? `<button class="btn primary" data-sel="overview">Done</button>` : `<button class="btn ghost" data-sel="overview">Later</button><button class="btn primary" data-act="do-update" ${st ? 'disabled' : ''}>${ic('download', 's')}Update now</button>`
      };
    },
    lifecycle() {
      return {
        eyebrow: 'Lifecycle · reprovision', title: 'What reprovision does',
        body: `
        <p style="font-size:12.5px;color:var(--ink-2);margin-top:0">Reprovision re-runs setup on the existing VM. It usually takes a minute or two. Repos, agent logins and history stay.</p>
        <h5>Brings in</h5>
        <ul class="commits"><li><span class="mono">b5c4348</span><span>Initialize vTPM identity before capturing reusable VM baselines</span></li><li><span class="mono">15c3930</span><span>Clarify optional CPU sizing in the child VM design</span></li></ul>
        <h5>Also applies</h5>
        <dl class="kv"><dt>Projects</dt><dd>+ jarvis (new) · emili-simulator stays out</dd><dt>Services</dt><dd>no pending changes</dd><dt>Agents</dt><dd>Claude Code 2.4.1 → 2.4.3</dd></dl>
        <div class="callout"><b>Claude is working</b> (6 min into "Refactoring forwarder retry backoff"). Reprovisioning now would end that turn.</div>`,
        foot: `<button class="btn" data-act="reprov-now">Now</button><button class="btn primary" data-act="reprov-queue">${ic('clock', 's')}When Claude finishes</button>`
      };
    },
    settings() {
      return {
        eyebrow: 'Companion', title: 'Settings',
        body: `
        <h5>Identity &amp; credentials ${tag('reprov')}</h5>
        <div class="field"><label>Git user name</label><input class="inp" value="permissionBRICK"></div>
        <div class="field"><label>Git email</label><input class="inp" value="dana@example.com"></div>
        ${setRow('Store git credentials on the VM', 'Saved in plain text (~/.git-credentials). An agent could read them.', true)}
        <div class="field"><label>Agent login password</label><input class="inp" type="password" value="agent"><span class="hint">Fallback console login only. Normal access is root over SSH.</span></div>
        <h5>Appearance &amp; startup ${tag('live')}</h5>
        <div class="field"><label>Theme</label><div class="seg sm" id="themeSeg"><button data-theme-set="light" aria-selected="${document.documentElement.dataset.theme !== 'dark'}">Paper</button><button data-theme-set="dark" aria-selected="${document.documentElement.dataset.theme === 'dark'}">Ink</button><button data-theme-set="light">Follow Windows</button></div></div>
        ${setRow('Start with Windows', 'The tray icon appears at sign-in.', true)}
        ${setRow('Toasts for construct notify', 'Also kept in the inbox.', true)}
        ${setRow('Global hotkey', 'Win+Alt+C opens the popup.', false)}
        <h5>Instances</h5>
        <div class="row" style="flex-wrap:wrap"><button class="btn sm">Register a VM…</button><button class="btn sm">Create a remote VM…</button><button class="btn sm ghost">Open logs folder</button></div>`,
        foot: `<button class="btn primary" data-act="toast" data-msg="Settings saved">Save</button>`
      };
    }
  };

  function renderSheet() {
    const [k, arg] = S.sel.split(':');
    const f = SH[k] ? SH[k](arg) : SH.overview();
    $('#sheet').innerHTML = `<div class="sh"><div class="eyebrow">${f.eyebrow}</div><h3>${f.title}</h3>${S.sel !== 'overview' ? `<button class="tb-icon x" data-sel="overview" title="Back to overview">${ic('x')}</button>` : ''}</div>
      <div class="sb">${f.body}</div>${f.foot ? `<div class="sf">${f.foot}</div>` : ''}`;
    markSel();
    const mcp = $('#mcpBox');
    if (mcp) mcp.addEventListener('input', () => {
      let ok = true; try { JSON.parse(mcp.value || '[]'); } catch (e) { ok = false; }
      $('#mcpMsg').innerHTML = ok ? ic('check', 's') + ' Valid JSON' : '<span style="color:var(--coral-ink)">' + ic('warn', 's') + ' Not valid JSON. Fix it or clear it before saving.</span>';
    });
  }

  function select(key) {
    S.sel = key; renderSheet();
    $$('.wl', root).forEach(w => w.classList.toggle('sel', w.dataset.sel === key));
  }

  /* ---------------- danger flows ---------------- */
  function danger(kind) {
    const scrim = $('#cpScrim'), name = S.inst;
    if (kind === 'host') {
      openDialog(scrim, `<div class="eyebrow">Share this PC</div><h2>Make PC-1 a Construct host?</h2>
        <p class="lead">Other people get VMs on this PC through the host service. ${name} is adopted with its data and settings, and you become the host admin.</p>
        <div class="impact"><div class="ir note"><div class="ic">${ic('info')}</div><div><b>Installs constructd as a Windows service</b><span>Listens on port 7462. Needs an admin prompt.</span></div></div>
        <div class="ir note"><div class="ic">${ic('info')}</div><div><b>This PC's RAM becomes shared</b><span>64 GB; you set each user's allowance and a host reserve.</span></div></div>
        <div class="ir keep"><div class="ic">${ic('check')}</div><div><b>${name} keeps running</b><span>Adopted as your VM on the new host.</span></div></div></div>
        <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close>Continue in setup…</button></div>`);
      return;
    }
    if (kind === 'custom') {
      openDialog(scrim, `<div class="eyebrow" style="color:var(--coral-ink)">One-time rebuild</div><h2>Custom reinstall</h2>
        <p class="lead">Choose the backup for this run only. The regular Reinstall always saves and restores.</p>
        <label class="opt"><input type="radio" name="cr" checked><div><b>Save &amp; restore</b><span>Back up auth, git credentials, profiles, instructions and memory now, then restore them.</span></div></label>
        <label class="opt"><input type="radio" name="cr"><div><b>Restore an existing backup</b><span>Skip the new backup. Using: backup from Sep 20, 18:02.</span></div></label>
        <label class="opt"><input type="radio" name="cr" id="crWipe"><div><b>Clean wipe</b><span>Blank VM. No backup, no restore. Settings, auth and history are lost.</span></div></label>
        <div class="row" style="margin-top:10px"><label class="row" style="gap:6px"><input type="checkbox"> Also redownload the Ubuntu ISO (3.1 GB)</label></div>
        <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn danger" id="crNext">Continue…</button></div>`,
        d => d.querySelector('#crNext').addEventListener('click', () => danger(d.querySelector('#crWipe').checked ? 'wipe' : 'reinstall')));
      return;
    }
    const common = [
      ['lose', 'Uncommitted work is lost', 'construct: 3 uncommitted files · omniloop: 1 unpushed commit'],
      ['lose', "Claude's current turn ends", 'working 6 min on "Refactoring forwarder retry backoff"'],
      ['note', 'Forwards close and the VM is away ~10 min', ':5173 and :8080 reopen when agents expose them again']
    ];
    const cfg = {
      reinstall: { title: `Reinstall ${name}?`, lead: 'The VM disk is deleted and rebuilt from the Ubuntu 26.04 image, then your config is restored.', cta: 'Reinstall', impact: [['keep', 'Config is backed up and restored', 'auth, git credentials, profiles, instructions, memory · new backup now'], ...common] },
      wipe: { title: `Wipe and reinstall ${name}?`, lead: 'Clean wipe: the new VM starts blank.', cta: 'Wipe & reinstall', impact: [['lose', 'No backup, no restore', 'agent logins, settings and history are gone'], ...common] },
      redownload: { title: `Redownload and reinstall ${name}?`, lead: 'Downloads a fresh Ubuntu 26.04 ISO (3.1 GB), then reinstalls.', cta: 'Redownload & reinstall', impact: [['keep', 'Config is backed up and restored', 'same as Reinstall'], ...common] },
      remove: { title: `Remove ${name} from this PC?`, lead: 'Forgets the instance and deletes the VM.', cta: 'Remove instance', eyebrow: 'Removal plan', impact: [['lose', 'Hyper-V VM and 146 GB disk deleted', 'C:\\ProgramData\\Construct\\agent-vm\\agent-vm.vhdx'], ['lose', 'Registry entry, SSH host alias and panel entry removed', 'Host alias "agent-vm" in ~/.ssh/config'], ['lose', 'Child VM win-test is deleted too', 'lease had 5h 20m left'], ['keep', 'Backups stay', 'in Documents\\Construct\\backups'], ['keep', 'dev-2 is not affected', 'becomes the active instance']] }
    }[kind];
    confirmDanger(scrim, Object.assign({ name, onConfirm: () => { toast(`${cfg.cta} started for ${name}. Progress shows in the lifecycle track.`, root); S.unlocked = false; renderTrack(); } }, cfg));
  }

  function reprovDialog() {
    const scrim = $('#cpScrim');
    if (S.inst !== 'agent-vm') { toast('dev-2 is up to date. Resume it first to reprovision.', root); return; }
    openDialog(scrim, `<div class="eyebrow">Routine</div><h2>Reprovision agent-vm</h2><p class="lead">Re-runs setup on the existing VM. Repos, logins and history stay.</p>
      <div class="impact"><div class="ir keep"><div class="ic">${ic('check')}</div><div><b>Picks up 2 commits and Claude Code 2.4.3</b><span>c21978b → b5c4348</span></div></div>
      <div class="ir keep"><div class="ic">${ic('check')}</div><div><b>Projects: construct, omniloop, gitgudlab, jarvis</b><span>emili-simulator stays out</span></div></div>
      <div class="ir note"><div class="ic">${ic('info')}</div><div><b>Claude is working</b><span>Reprovisioning now ends the current turn.</span></div></div></div>
      <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn" data-act="reprov-now" data-close>Now</button><button class="btn primary" data-act="reprov-queue" data-close>${ic('clock', 's')}When Claude finishes</button></div>`);
  }

  function runReprov() {
    S.reprov = 'running'; renderTrack();
    let p = 34;
    const iv = setInterval(() => {
      p += 22; const el = $('#rpProg'); if (el) el.style.width = Math.min(p, 100) + '%';
      if (p >= 100) { clearInterval(iv); S.reprov = 'done'; S.behind = 0; renderTrack(); renderCanvas(); toast('Reprovision finished · agent-vm is current', root); }
    }, 700);
  }

  /* ---------------- events ---------------- */
  function onClick(e) {
    const th = e.target.closest('[data-theme-set]');
    if (th) { setTheme(th.dataset.themeSet); renderSheet(); return; }
    const dz = e.target.closest('[data-danger]');
    if (dz) {
      if (dz.closest('.danger') && !S.unlocked) { toast(ic('lock', 's') + ' Unlock "Rebuild & remove" first', root); return; }
      danger(dz.dataset.danger); return;
    }
    const a = e.target.closest('[data-act]');
    if (a && a.dataset.act) {
      const act = a.dataset.act;
      if (a.tagName === 'INPUT' && a.type === 'range') return;
      switch (act) {
        case 'inst': return instMenu(a);
        case 'power': return powerMenu(a);
        case 'openmenu': return openInMenu(a);
        case 'bell': S.read = false; return select('overview');
        case 'settings': return select('settings');
        case 'unlock': S.unlocked = a.checked; return renderTrack();
        case 'reprov': return reprovDialog();
        case 'reprov-now': $('#cpScrim').classList.remove('open'); return runReprov();
        case 'reprov-queue': $('#cpScrim').classList.remove('open'); S.reprov = 'queued'; renderTrack(); return toast(ic('clock', 's') + ' Queued: reprovision starts when Claude finishes its turn', root);
        case 'reprov-cancel': S.reprov = 'idle'; return renderTrack();
        case 'markread': S.read = true; $('#cpDot').style.display = 'none'; return renderSheet();
        case 'wake': S.wake = !S.wake; renderSheet(); return toast(S.wake ? 'You will get a toast when Claude goes idle' : 'Cancelled', root);
        case 'mic': S.mic = a.checked; renderCanvas(); return renderSheet();
        case 'proj-toggle': S.proj[a.dataset.id] = a.checked; renderCanvas(); return renderSheet();
        case 'proj-save': return toast('Profile saved · applies on the next reprovision', root);
        case 'proj-del': return confirmDanger($('#cpScrim'), { title: 'Delete profile ' + a.dataset.id + '?', lead: 'Removes projects/' + a.dataset.id + '.json from your config. Repos already on the VM stay until the next reinstall.', name: a.dataset.id, cta: 'Delete profile', impact: [['lose', 'Profile file deleted', 'Synced to remote config repos on the next sync']], onConfirm: () => toast('Profile deleted', root) });
        case 'child-del': return confirmDanger($('#cpScrim'), { title: 'Delete win-test?', lead: 'The child VM and its disk are deleted. Its Windows evaluation identity is not reused.', name: 'win-test', cta: 'Delete child VM', impact: [['lose', 'VM and 64 GB disk deleted', 'lease had 5h 20m left']], onConfirm: () => toast('Deleting win-test…', root) });
        case 'res-reset': S.ram = 16; S.cpu = 8; return renderSheet();
        case 'res-apply': return toast('Saving, then agent-vm restarts with ' + S.ram + ' GB / ' + S.cpu + ' vCPU (UAC prompt)', root);
        case 'do-update': S.updating = 'run'; renderSheet(); setTimeout(() => { S.updating = 'done'; if (S.sel === 'update') renderSheet(); renderTrack(); toast('Construct updated to 3a91f0e', root); }, 1800); return;
        case 'agent-update': return toast('Claude Code 2.4.3 will install when the current turn ends', root);
        case 'update-all': return toast('Updating all agents that have updates (1)…', root);
        case 'fwd-open': return toast('Opening http://localhost:' + (S.sel.includes('8080') ? 18800 : 5173), root);
        case 'fwd-close': return toast('Forward closed', root);
        case 'copy': return toast('URL copied', root);
        case 'open': return toast('Opening ' + S.inst + ' in ' + S.target + '…', root);
        case 'resume': toast('Resuming dev-2 on buildbox…', root); return;
        case 'expose': return openDialog($('#cpScrim'), `<h2>Expose a port</h2><p class="lead">Agents usually do this with <span class="mono">construct expose</span>. You can also add one by hand.</p>
          <div class="row"><div class="field grow"><label>VM port</label><input class="inp mono" value="4173"></div><div class="field grow"><label>Local port</label><input class="inp mono" value="4173"></div></div>
          <div class="field"><label>Label</label><input class="inp" value="vite preview"></div>
          <div class="field"><label>Scope</label><div class="seg sm"><button aria-selected="true">This PC</button><button>Host LAN</button></div></div>
          <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close>Expose</button></div>`);
        case 'newchild': return openDialog($('#cpScrim'), `<h2>New child VM</h2><p class="lead">A disposable VM that ${S.inst} can reach, for OS or installer tests.</p>
          <div class="field"><label>Image</label><select class="inp"><option>Windows 11 24H2 evaluation</option><option>Windows Server 2025 evaluation</option><option>Ubuntu 26.04 LTS</option></select></div>
          <div class="row"><div class="field grow"><label>RAM</label><select class="inp"><option>4 GB</option><option>8 GB</option></select></div><div class="field grow"><label>Lifetime (required)</label><select class="inp"><option>8 hours</option><option>1 day</option><option>3 days</option></select></div></div>
          <div class="foot"><button class="btn ghost" data-close>Cancel</button><button class="btn primary" data-close>Create</button></div>`);
        case 'newproj': return toast('New profile: pick a repo URL to start', root);
        case 'toast': return toast(a.dataset.msg, root);
      }
    }
    const t = e.target.closest('[data-sel]');
    if (t) return select(t.dataset.sel);
    const m = e.target.closest('[data-inst]');
    if (m) { S.inst = m.dataset.inst; S.sel = 'overview'; closeMenus(); renderAll(); return; }
    const tg = e.target.closest('[data-target]');
    if (tg) { S.target = tg.dataset.target; closeMenus(); renderCanvas(); }
    const pw = e.target.closest('[data-pwr]');
    if (pw) { closeMenus(); toast(pw.dataset.pwr + ' agent-vm…' + (pw.dataset.pwr !== 'Save' ? ' (UAC prompt)' : ''), root); }
  }

  function instMenu(a) {
    const m = $('#mInst');
    m.innerHTML = `<div class="mh eyebrow">Instances on PC-1</div>
      <button class="mi" data-inst="agent-vm">${S.inst === 'agent-vm' ? ic('check', 's') : '<span style="width:13px"></span>'}<span class="led on"></span>agent-vm<span class="sub">local · 2 behind</span></button>
      <button class="mi" data-inst="dev-2">${S.inst === 'dev-2' ? ic('check', 's') : '<span style="width:13px"></span>'}<span class="led"></span>dev-2<span class="sub">saved · buildbox</span></button>
      <div class="sep"></div><button class="mi">${ic('plus', 's')}Register a VM…</button><button class="mi">${ic('cloud', 's')}Create a remote VM…</button>`;
    openMenu(m, a);
  }
  function powerMenu(a) {
    const m = $('#mPower');
    m.innerHTML = `<div class="mh eyebrow">Power · agent-vm</div><button class="mi" data-pwr="Shutting down">${ic('power', 's')}Shut down<span class="sub">UAC</span></button><button class="mi" data-pwr="Restarting">${ic('refresh', 's')}Restart<span class="sub">UAC</span></button><button class="mi" data-pwr="Save">${ic('save', 's')}Save state<span class="sub">RAM to disk</span></button>`;
    openMenu(m, a, { right: true });
  }
  function openInMenu(a) {
    const m = $('#mOpen');
    m.innerHTML = `<div class="mh eyebrow">Open ${S.inst} in</div>` + [['VS Code', 'Remote-SSH'], ['T3 Code', ':5177'], ['serve-web', ':8000'], ['Console', 'Hyper-V']].map(t => `<button class="mi" data-target="${t[0]}">${S.target === t[0] ? ic('check', 's') : '<span style="width:13px"></span>'}${t[0]}<span class="sub">${t[1]}</span></button>`).join('');
    openMenu(m, a);
  }

  function renderAll() { renderCanvas(); renderSheet(); renderTrack(); }

  shell();
  root.addEventListener('click', onClick);
  root.addEventListener('input', e => {
    const a = e.target.dataset.act;
    if (a === 'ram' || a === 'cpu') {
      S[a] = +e.target.value;
      $('#' + a + 'Out').textContent = S[a] + (a === 'ram' ? ' GB' : '');
      const changed = S.ram !== 16 || S.cpu !== 8, n = $('#resNote');
      n.className = 'callout ' + (changed ? '' : 'teal');
      n.innerHTML = changed ? '<b>Restart to apply.</b> The VM shuts down, resizes to ' + S.ram + ' GB / ' + S.cpu + ' vCPU and starts again. No reinstall. Needs one UAC prompt, and <b>Claude is working</b>.' : 'Matches the running VM. Drag a slider to plan a change.';
      $('[data-act="res-apply"]').disabled = !changed;
      if (a === 'ram') $('.rng-scale span:nth-child(2)').textContent = 'PC-1 has 64 GB · ' + (64 - S.ram) + ' GB left for Windows';
    }
  });
  root.addEventListener('keydown', e => {
    if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('[data-sel][tabindex]')) { e.preventDefault(); select(e.target.dataset.sel); }
  });
  // chart tooltips
  root.addEventListener('pointerover', e => {
    const c = e.target.closest('[data-tipv]'), tip = $('#cpTip');
    if (!c) { tip.style.display = 'none'; return; }
    const r = rel(c, root.firstElementChild);
    tip.innerHTML = '<b>' + c.dataset.tipv.split(': ')[1] + '</b> <span class="k">' + c.dataset.tipv.split(': ')[0] + '</span>';
    tip.style.display = 'block'; tip.style.left = (r.x + r.w / 2 - tip.offsetWidth / 2) + 'px'; tip.style.top = (r.y - 34) + 'px';
  });

  document.addEventListener('route', e => {
    if (e.detail.surface !== 'panel') return;
    const sub = e.detail.sub;
    if (sub[0] === 'dev-2') { S.inst = 'dev-2'; S.sel = 'overview'; }
    else if (sub[0] === 'unlocked') { S.unlocked = true; }
    else if (sub[0] === 'confirm') { S.unlocked = true; renderAll(); setTimeout(() => danger('reinstall'), 50); return; }
    else if (sub[0] && sub[0].startsWith('project-')) S.sel = 'project:' + sub[0].slice(8);
    else if (sub[0] && sub[0].startsWith('agent-')) S.sel = 'agent:' + sub[0].slice(6);
    else if (sub[0] && SH[sub[0]]) S.sel = sub[0];
    renderAll();
  });
  window.addEventListener('resize', () => requestAnimationFrame(drawWires));
})();
