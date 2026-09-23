/* The Construct — setup wizard, existing-VM actions, and simulated install */
(function () {
  'use strict';
  const { ICON, LOGO, wordmark, captionButtons, CodeRain, LogView, Clock, Bridge, esc } = CX;
  const $ = id => document.getElementById(id);
  const Q = Bridge.params();

  // ======================================================================
  // Data
  // ======================================================================
  const COMPONENTS = [
    { key: 'companion', name: 'Companion', group: 'On this PC', min: true, full: true, desc: 'Tray app: start and stop the VM, agent notifications, port forwards, mic.' },
    { key: 'smb', name: 'SMB workspace share', group: 'On this PC', min: false, full: true, desc: "The VM's repos folder as a Windows network share." },
    { key: 'mapDrive', name: 'Map share to a drive letter', group: 'On this PC', min: false, full: false, requires: 'smb', desc: 'Mounts that share as Z: and reconnects it at every logon.' },
    { key: 'mic', name: 'Microphone passthrough', group: 'On this PC', min: false, full: true, desc: "Dictate to agents on the VM with this PC's microphone (installs ffmpeg)." },
    { key: 'claudeStreaming', name: 'Claude partial streaming', group: 'Agents & VM', min: true, full: true, desc: 'Shows Claude Code replies live while they are being written.' },
    { key: 'credStore', name: 'Git credential store', group: 'Agents & VM', min: true, full: true, desc: 'Agents push and pull without prompts. Stored in plaintext on the VM.', badge: ['warn', 'plaintext'] },
    { key: 'ocWatcher', name: 'OpenCode background watcher', group: 'Agents & VM', min: false, full: true, desc: 'Keeps OpenCode serve (:4096) running in the background for the GUI app.' },
    { key: 'checkpoints', name: 'Automatic checkpoints', group: 'Agents & VM', min: false, full: false, desc: 'Hyper-V snapshot at every VM start. Costs disk; most people leave it off.', localOnly: true },
    { key: 'serveWeb', name: 'VS Code serve-web', group: 'Editors & remote access', min: false, full: true, desc: 'Browser VS Code on port 8000, gated by a connection token.' },
    { key: 'tunnel', name: 'VS Code tunnel', group: 'Editors & remote access', min: false, full: false, desc: 'Reach the VM from vscode.dev anywhere. One GitHub sign-in during install.', badge: ['info', 'asks you to sign in'] },
    { key: 't3', name: 'T3 Code', group: 'Editors & remote access', min: false, full: true, desc: "One web GUI for every agent's threads, forwarded to this PC." },
    { key: 't3Patched', name: 'Patched T3 Code + Desktop', group: 'Editors & remote access', min: false, full: true, requires: 't3', desc: "Construct's T3 build plus the Windows desktop app.", badge: ['warn', 'adds ~15 min'] },
    { key: 't3Https', name: 'T3 Code HTTPS', group: 'Editors & remote access', min: true, full: true, desc: 'Serves T3 over HTTPS with a local CA. Windows asks you to trust it once.' }
  ];
  const PROFILES = [
    { name: 'construct', repo: 'github.com/permissionBRICK/The-Construct', sdk: 'node 22 · .NET 9', host: 'github.com', on: true },
    { name: 'omniloop', repo: 'gitlab.example.com/dana/omniloop', sdk: 'node 22 · python 3.12', host: 'gitlab.example.com', on: true },
    { name: 'gitgudlab', repo: 'gitlab.example.com/dana/gitgudlab', sdk: '.NET 9 · node 22', host: 'gitlab.example.com', on: true },
    { name: 'emili-simulator', repo: 'github.com/dana-a/emili-simulator', sdk: 'python 3.12 · CUDA 12', host: 'github.com', on: false },
    { name: 'jarvis', repo: 'gitlab.example.com/dana/jarvis', sdk: 'python 3.12', host: 'gitlab.example.com', on: true, isNew: true }
  ];
  const PROV_STEPS = ['Checking root privileges', 'Checking free disk space', 'Granting agent passwordless sudo', 'Running core host bootstrap',
    'Writing configuration to /etc/construct/construct.conf', 'Installing the VM service token', "Requesting host port forwards for this VM's web services",
    'Configuring global git identity', 'Setting up SMB share for the host', 'Setting up root SSH key', 'Installing AI tool: claude', 'Installing AI tool: codex',
    'Installing AI tool: opencode', 'Installing T3 Code web GUI', 'Recording the forwarded T3 address', 'Installing AI tool console integration',
    'Installing construct CLI', 'Installing browser console gateway', 'Setting up the notification spool', 'Setting up the activity heartbeat timer',
    'Setting up the token usage timer', 'Setting up endpoint refresh at boot', 'Generating runtime config', 'Configuring MCP servers for the AI tools',
    'Installing project SDKs/runtimes', 'Seeding git credentials for repo checkout', 'Checking out project repos', 'Running project provisioning commands',
    '(Re)starting construct service', 'Setting up VS Code server / serve-web / tunnel', 'Recording provisioning timestamps'];

  // ======================================================================
  // Answers (prefilled like the scripts would)
  // ======================================================================
  const HOST_RAM = 64;
  const recRam = Math.min(24, Math.max(4, Math.floor(HOST_RAM / 3)));
  const A = {
    variant: Q.get('variant') || 'local',  // local | remote | existing
    preset: 'minimal',
    feat: {},
    ram: recRam, disk: 50,
    projects: Object.fromEntries(PROFILES.map(p => [p.name, p.on])),
    cred: { user: 'dana', token: '', status: 'idle' },  // idle | checking | ok | bad | skipped
    gitName: 'Dana A.', gitEmail: 'dana@example.com', agentPw: 'agent', credStore: true,
    remote: { url: 'https://buildbox.example.local:7462', conn: 'idle', pin: null, signin: 'token', token: '', tokStatus: 'idle', instance: 'agent-vm-3' },
    ex: { action: 'reprovision', addUrl: '', unsaved: 'abort', saveCfg: true, noBackup: 'no', restore: 'yes', keepVm: false, confirm: '' }
  };
  function applyPreset(p) {
    A.preset = p;
    if (p !== 'custom') COMPONENTS.forEach(c => { A.feat[c.key] = p === 'full' ? c.full : c.min; });
    A.credStore = A.feat.credStore;
  }
  applyPreset(Q.get('preset') === 'full' ? 'full' : 'minimal');
  if (Q.get('tunnel') === '1') { A.preset = 'custom'; A.feat.tunnel = true; }
  const CHECK = Q.get('check') || 'ok'; // ok | bios | home
  const vmName = () => A.variant === 'remote' ? A.remote.instance : 'agent-vm';

  // ======================================================================
  // Chrome
  // ======================================================================
  $('bandInner').innerHTML = wordmark('3a91f0e') + `<span class="band-title" id="bandTitle"><b>Setup</b> · PC-1</span>` + captionButtons();
  CodeRain(document.querySelector('.band canvas'), { size: 12 });
  document.querySelector('.details-toggle .chev').innerHTML = ICON.chevDown;
  $('popBtn').innerHTML = ICON.popout;
  $('logWinDock').innerHTML = ICON.chevDown;
  $('logWinClose').innerHTML = ICON.x;

  function toast(title, body) {
    if (Bridge.embedded) { Bridge.post({ toast: { title, body } }); return; }
    const t = $('pageToast'); t.textContent = body ? `${title} — ${body}` : title; t.classList.add('show');
    clearTimeout(toast._t); toast._t = setTimeout(() => t.classList.remove('show'), 2600);
  }

  // ======================================================================
  // Log
  // ======================================================================
  const log = new LogView($('log'), {
    fileName: 'install-20260923-140812.log', popout: () => popOut(true),
    onLine: l => { $('tail').textContent = l.text.replace(/\x1b\[[\d;]*m/g, '').trim(); }
  });
  function seedLog() {
    log.clear();
    const S = [
      ['14:08:12', 'dim', 'Construct setup log -> %LOCALAPPDATA%\\Construct\\logs\\install-20260923-140812.log'],
      ['14:08:12', 'step', '==> Downloading permissionBRICK/The-Construct (main) ...'],
      ['14:08:15', 'info', '    construct-3a91f0e.zip  4.8 MB · sha256 \x1b[32mOK\x1b[0m'],
      ['14:08:16', 'step', '==> Ensuring the VS Code Remote-SSH extension...'],
      ['14:08:17', 'info', '    Remote-SSH extension present.'],
      ['14:08:19', 'step', '==> Installed the Construct control panel into VS Code (reload/restart VS Code to see it).'],
      ['14:08:23', 'step', '==> Construct Companion installed and started.'],
      ['14:12:01', 'step', '==> Setup answers collected'],
      ['14:12:01', 'dim', '    ' + cmdLine().replace(/\n\s*/g, ' ')],
      ['14:12:02', 'dim', `    git clone credentials: gitlab.example.com user ${A.cred.user || '(skipped)'} token glpat-9xk2Hq7TzLmN0aB`]
    ];
    S.forEach(([ts, k, t]) => log.add(k, t, ts));
  }

  // ======================================================================
  // Wizard flow
  // ======================================================================
  let W = { page: null, maxIdx: 0 };
  function flow() {
    if (A.variant === 'existing') {
      const f = ['existing'];
      if (A.ex.action === 'reinstall' || A.ex.action === 'redownload') f.push('safety');
      if (A.ex.action === 'remove') f.push('remove');
      f.push('confirm');
      return f;
    }
    if (A.variant === 'remote') return ['welcome', 'location', 'connect', 'instance', 'features', 'resources', 'projects', 'identity', 'review'];
    return ['welcome', 'location', 'features', 'resources', 'projects', 'identity', 'review'];
  }
  const presetLabel = () => ({ minimal: 'Minimal', full: 'Full', custom: 'Custom' })[A.preset];
  const onCount = () => COMPONENTS.filter(c => A.feat[c.key]).length;
  const selProjects = () => PROFILES.filter(p => A.projects[p.name]).map(p => p.name);
  const EX_ACTIONS = {
    reprovision: { label: 'Reprovision', icon: 'retry', desc: 'Apply the latest Construct and your project changes. Keeps the VM, its disk and everything on it. A few minutes, no reboot.', rec: true },
    reinstall: { label: 'Reinstall', icon: 'restart', danger: true, desc: 'Delete agent-vm and build it fresh from the saved Ubuntu image. Everything on the VM disk is erased.' },
    redownload: { label: 'Redownload & reinstall', icon: 'down', danger: true, desc: 'Like Reinstall, but downloads a fresh Ubuntu Server ISO first (about 3 GB).' },
    export: { label: 'Export config', icon: 'save', desc: 'Save the agent config (logins, MCP servers, settings) to a backup folder on this PC.' },
    addconfig: { label: 'Add config', icon: 'box', desc: 'Import project profiles from a git URL or a folder, then reprovision.' },
    remove: { label: 'Remove instance', icon: 'trash', danger: true, desc: 'Forget agent-vm on this PC and delete the VM and its disk.' }
  };
  const PAGE_META = {
    welcome: { t: 'Welcome' },
    location: { t: 'Where it runs', s: () => A.variant === 'remote' ? 'Shared host' : 'This PC · Hyper-V' },
    connect: { t: 'Connect', s: () => A.remote.conn === 'ok' ? 'buildbox' : '' },
    instance: { t: 'Instance name', s: () => A.remote.instance },
    features: { t: 'Features', s: () => `${presetLabel()} · ${onCount()} of 13` },
    resources: { t: 'Resources', s: () => `${A.ram} GB RAM · ${A.disk} GB disk` },
    projects: { t: 'Projects', s: () => `${selProjects().length} of ${PROFILES.length} profiles` },
    identity: { t: 'Identity', s: () => A.gitName },
    review: { t: 'Review' },
    existing: { t: 'Choose an action', s: () => EX_ACTIONS[A.ex.action].label },
    safety: { t: 'Before erasing' },
    remove: { t: 'What gets removed' },
    confirm: { t: 'Confirm' }
  };

  // ---------- rail ----------
  let installRailBuilt = false;
  function renderRail() {
    const rail = $('rail');
    if (INST.active) {
      if (!installRailBuilt) buildInstallRail();
      return updateInstallRail();
    }
    installRailBuilt = false;
    const f = flow();
    const idx = f.indexOf(W.page);
    const head = A.variant === 'existing' ? 'agent-vm' : 'Setup';
    let h = `<h6>${head}<span class="cnt">${idx + 1}/${f.length}</span></h6>`;
    f.forEach((id, k) => {
      const m = PAGE_META[id];
      const cls = k === idx ? 'current' : k < idx || k <= W.maxIdx ? 'done' : 'locked';
      const mk = cls === 'done' && k !== idx ? ICON.check : (k + 1);
      const sub = m.s && (k <= W.maxIdx) ? m.s() : '';
      h += `<button class="item ${cls}" data-go="${id}" ${cls === 'locked' ? 'disabled aria-disabled="true"' : ''} ${k === idx ? 'aria-current="step"' : ''}>
        <span class="mk">${mk}</span><span class="lbl">${m.t}${sub ? `<small>${esc(sub)}</small>` : ''}</span></button>`;
    });
    const phases = buildPhases();
    const active = phases.filter(p => !p.skip && !p.pre).length;
    h += `<h6 style="margin-top:16px">Install<span class="cnt">${active} phases</span></h6>
      <div class="preview">${(() => { const v = phases.filter(p => !p.pre && !p.skip); return v.slice(0, 4).map(p => esc(p.name)).join(' → ') + (v.length > 4 ? ' → …' : ''); })()}<br><b style="color:var(--ink-2)">${estRange(100)}</b> after you start.</div>`;
    h += `<div class="foot-note">Answers are saved as you go, so a restart or a closed window picks up where you left off.</div>`;
    rail.innerHTML = h;
  }
  $('rail').addEventListener('click', e => {
    const b = e.target.closest('[data-go]'); if (!b || b.disabled) return;
    go(b.dataset.go);
  });

  // ---------- page router ----------
  function go(id) {
    const f = flow();
    if (!f.includes(id)) id = f[0];
    W.page = id;
    W.maxIdx = Math.max(W.maxIdx, f.indexOf(id));
    $('bandTitle').innerHTML = A.variant === 'existing' ? '<b>agent-vm</b> · already installed on PC-1' : `<b>Setup</b> · ${A.variant === 'remote' ? 'PC-1 → buildbox' : 'PC-1'}`;
    renderPage();
    renderRail();
    $('page').scrollTop = 0;
    Bridge.post({ page: id, variant: A.variant });
  }
  function next() { const f = flow(); const k = f.indexOf(W.page); if (k < f.length - 1) go(f[k + 1]); else startInstall(); }
  function back() { const f = flow(); const k = f.indexOf(W.page); if (k > 0) go(f[k - 1]); }

  function renderPage() {
    const r = PAGES[W.page]();
    $('page').innerHTML = r;
    renderFoot();
    afterRender();
  }
  function renderFoot() {
    const f = flow(); const k = f.indexOf(W.page);
    const v = VALID[W.page] ? VALID[W.page]() : { ok: true };
    let nextLbl = 'Next', nextCls = 'primary', icon = '';
    if (W.page === 'welcome') nextLbl = 'Get started';
    if (W.page === 'review') { nextLbl = 'Install'; if (A.variant === 'local') icon = ICON.shield; }
    if (W.page === 'confirm') {
      const a = EX_ACTIONS[A.ex.action];
      nextLbl = { reprovision: 'Reprovision', reinstall: 'Reinstall agent-vm', redownload: 'Redownload & reinstall', export: 'Export config', addconfig: 'Import & reprovision', remove: A.ex.keepVm ? 'Forget agent-vm' : 'Remove agent-vm' }[A.ex.action];
      if (a.danger) nextCls = 'danger';
    }
    const hint = v.hint || (W.page === 'welcome' ? `${ICON.clock} ${estRange(100).replace('about', 'About')} after ${flow().length - 1} short pages` : HINTS[W.page]) || '';
    $('foot').innerHTML = `<span class="hint">${hint}</span>
      ${k > 0 ? `<button class="btn" data-a="back">Back</button>` : `<button class="btn" data-a="cancel">${A.variant === 'existing' ? 'Close' : 'Cancel'}</button>`}
      <button class="btn ${nextCls}" data-a="next" ${v.ok ? '' : 'disabled'}>${icon}${nextLbl}</button>`;
  }
  const HINTS = {
    welcome: '',
    features: `${ICON.info} You can change features later with Reprovision`,
    resources: `${ICON.info} Change RAM later from the Companion`,
    review: '',
  };
  $('foot').addEventListener('click', e => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    if (b.dataset.a === 'next') next();
    if (b.dataset.a === 'back') back();
    if (b.dataset.a === 'cancel') toast('Setup closed', 'Your answers are saved; run the one-liner again to continue.');
  });

  // ======================================================================
  // Pages
  // ======================================================================

  function chk(kind, k, v) {
    const ic = { ok: ICON.check, warn: ICON.warn, err: ICON.x, info: ICON.info }[kind];
    return `<li><span class="ci ${kind}">${ic}</span><span class="k">${k}</span><span class="v">${v}</span></li>`;
  }
  const PAGES = {};
  const VALID = {};

  PAGES.welcome = () => {
    const hv = CHECK === 'home'
      ? chk('err', 'Hyper-V', 'Not available on Windows 11 Home')
      : chk('info', 'Hyper-V', 'Off · turned on during setup');
    const virt = CHECK === 'bios' ? chk('err', 'Virtualization in firmware', 'Turned off') : chk('ok', 'Virtualization in firmware', 'Enabled');
    let guide = '';
    if (CHECK === 'bios') guide = `<div class="card guide" style="border-color:var(--danger-line)"><h3 style="color:var(--danger-ink)">${ICON.err.replace('<svg', '<svg style="width:16px;height:16px"')} Turn on virtualization in your PC's firmware</h3>
      <div class="desc">Hyper-V needs the CPU's virtualization feature. It's switched off in the BIOS/UEFI on this PC.</div>
      <ol><li>Restart and open the firmware setup: usually <kbd>Del</kbd>, <kbd>F2</kbd> or <kbd>F10</kbd> while the logo shows, or Settings › System › Recovery › Advanced startup › UEFI Firmware Settings.</li>
      <li>Intel: enable <b>Intel Virtualization Technology (VT-x)</b>. AMD: enable <b>SVM Mode</b>.</li>
      <li>Save, boot back into Windows, and reopen setup. Your answers are kept.</li></ol>
      <div class="acts-row" style="margin-top:10px"><button class="btn sm">${ICON.ext} Manufacturer guides</button><button class="btn sm" data-a="useRemote">${ICON.server} Use a shared host instead</button></div></div>`;
    if (CHECK === 'home') guide = `<div class="card guide" style="border-color:var(--warn-line)"><h3>${ICON.warn.replace('<svg', '<svg style="width:16px;height:16px;color:var(--warn)"')} Windows 11 Home doesn't include Hyper-V</h3>
      <div class="desc">The Construct runs its VM on Hyper-V. You have two options:</div>
      <ol><li><b>Upgrade to Windows 11 Pro</b> in Settings › System › Activation, then reopen setup.</li>
      <li><b>Run the VM on a shared host</b> your team already has. Nothing is installed in Windows except the Companion and VS Code bits.</li></ol>
      <div class="acts-row" style="margin-top:10px"><button class="btn sm primary" data-a="useRemote">${ICON.server} Use a shared host</button><button class="btn sm">${ICON.ext} Why Home isn't supported</button></div></div>`;
    return `<div class="eyebrow">Welcome</div>
      <h1>Your AI agents get their own computer</h1>
      <p class="lead">The Construct gives Claude Code, Codex, OpenCode and T3 Code a disposable Ubuntu VM where they can work unattended as root, without touching Windows. This setup asks a few questions, then builds the VM for you.</p>
      ${guide}
      <div class="grid2" ${guide ? 'style="margin-top:12px"' : ''}>
        <div class="card"><h3>This PC · PC-1</h3>
          <ul class="checks">
            ${chk('ok', CHECK === 'home' ? 'Windows 11 Home 24H2' : 'Windows 11 Pro 24H2', 'build 26100')}
            ${virt}${hv}
            ${chk('ok', 'Memory', '64 GB')}
            ${chk('ok', 'Free space on C:', '412 GB')}
          </ul></div>
        <div class="card"><h3>Already done</h3>
          <ul class="checks">
            ${chk('ok', 'Downloaded and verified The Construct', '3a91f0e')}
            ${chk('ok', 'VS Code Remote-SSH', 'present')}
            ${chk('ok', 'Construct panel in VS Code', 'installed')}
            ${chk('ok', 'Construct Companion', 'in your tray')}
            ${chk('ok', 'Git for Windows', '2.51')}
          </ul></div>
      </div>
      <div class="callout info"><span class="ic">${ICON.info}</span><div>Plan for <b>${estRange(100).replace('about ', '')}</b> once you start. Windows asks for approval once and needs <b>one restart</b> to turn on Hyper-V; setup reopens by itself afterwards.</div></div>`;
  };
  VALID.welcome = () => CHECK === 'ok' ? { ok: true } : { ok: false, hint: `${ICON.err} Fix the item above, or choose a shared host` };

  PAGES.location = () => `<div class="eyebrow">Step 1</div><h1>Where should the VM run?</h1>
    <p class="lead">Both give you the same agents, panel and Companion. You can add the other kind later as a second instance.</p>
    <div class="tiles two" role="radiogroup">
      <button class="tile" role="radio" aria-checked="${A.variant === 'local'}" data-a="loc" data-v="local" ${CHECK !== 'ok' ? 'disabled style="opacity:.5"' : ''}>
        <span class="radio"></span><span class="ti">${ICON.pc}</span><b>On this PC</b>
        <span class="td">Hyper-V on PC-1. Fastest, works offline, uses this PC's RAM and disk.</span>
        <span class="tm"><span class="badge">${ICON.shield} needs administrator approval</span><span class="badge">one restart</span></span></button>
      <button class="tile" role="radio" aria-checked="${A.variant === 'remote'}" data-a="loc" data-v="remote">
        <span class="radio"></span><span class="ti">${ICON.server}</span><b>On a shared host</b>
        <span class="td">A team server running the Construct host service. Nothing is virtualized on this PC.</span>
        <span class="tm"><span class="badge ok">no admin rights needed</span><span class="badge">needs host URL + sign-in</span></span></button>
    </div>
    ${A.variant === 'local' ? `<div class="callout info"><span class="ic">${ICON.info}</span><div>Next, setup relaunches itself as administrator. Windows shows a <b>User Account Control</b> prompt when the install starts, not now.</div></div>` :
      `<div class="callout info"><span class="ic">${ICON.info}</span><div>Ask your host admin for the <b>service URL</b> and the <b>certificate fingerprint</b>. They're on the host panel under Host config.</div></div>`}`;

  PAGES.connect = () => {
    const R = A.remote;
    let h = `<div class="eyebrow">Step 2 · Shared host</div><h1>Connect to your host</h1>
      <p class="lead">Setup checks the service, pins its certificate and signs you in. Nothing is created until you press Install.</p>
      <div class="card"><div class="field"><label for="url">Host service URL</label>
        <div class="row"><input id="url" class="input mono" value="${esc(R.url)}" data-bind="remote.url" spellcheck="false">
        <button class="btn ${R.conn === 'ok' ? '' : 'primary'}" data-a="connect">${R.conn === 'connecting' ? '<span class="spinner"></span> Checking' : R.conn === 'ok' ? 'Reconnect' : 'Connect'}</button></div></div>`;
    if (R.conn === 'ok') h += `<div class="hostinfo" style="margin-top:12px"><span class="hi">${ICON.server}</span><div><b>buildbox</b> · constructd 1.14.2 · Hyper-V<br><span class="muted" style="font-size:12px">32 cores · 128 GB · capacity mode enforce · memory pressure <span style="color:var(--warn)">elevated</span></span></div><span class="badge ok" style="margin-left:auto">${ICON.check} reachable · 14 ms</span></div>`;
    h += `</div>`;
    if (R.conn === 'ok') {
      h += `<div class="card"><h3>${ICON.lock.replace('<svg', '<svg style="width:15px;height:15px"')} Check the host's certificate</h3>
        <div class="desc" style="margin-bottom:8px">First time this PC talks to buildbox. Compare the fingerprint with the one your admin gave you. Setup pins it and refuses any other certificate later.</div>
        <div class="fp">SHA256 5A:3C:91:0E:7F:22:B4:08:C1:D9:5E:6A:0B:4F:37:D2:<br>88:E1:C0:A4:19:6B:E2:03:DD:71:4A:9F:0C:55:E8:B6</div>
        <div class="acts-row" style="margin-top:10px">
          <div class="seg-ctl danger"><button data-a="pin" data-v="yes" aria-pressed="${R.pin === 'yes'}">It matches, pin it</button><button class="bad" data-a="pin" data-v="no" aria-pressed="${R.pin === 'no'}">It doesn't match</button></div>
          <button class="btn sm subtle">${ICON.copy} Copy</button></div>
        ${R.pin === 'no' ? `<div class="callout danger"><span class="ic">${ICON.err}</span><div><b>Stop here.</b> A different fingerprint can mean someone is intercepting the connection. Setup won't connect; contact your host admin.</div></div>` : ''}
      </div>`;
    }
    if (R.conn === 'ok' && R.pin === 'yes') {
      h += `<div class="card"><h3>${ICON.user.replace('<svg', '<svg style="width:15px;height:15px"')} Sign in to buildbox</h3>
        <div class="callout warn" style="margin-top:6px"><span class="ic">${ICON.warn}</span><div>Your Windows account <b>EXAMPLE\\dana</b> was not accepted: buildbox asks for another sign-in method for this account.</div></div>
        <div class="radio-list">
          <label><input type="radio" name="si" data-a="signin" value="token" ${R.signin === 'token' ? 'checked' : ''}><span><b>API token</b><small>Paste the token from the host panel (Users › your account › Tokens).</small></span></label>
          ${R.signin === 'token' ? `<div style="margin:0 0 6px 32px"><div class="row"><div class="pw-wrap"><input class="input mono" type="password" placeholder="cst_…" value="${esc(R.token)}" data-bind="remote.token" id="tok"><button class="eye" data-a="eye" data-t="tok">Show</button></div></div>
            <div class="valid ${R.tokStatus === 'ok' ? 'ok' : R.tokStatus === 'checking' ? 'wait' : R.tokStatus === 'bad' ? 'bad' : ''}" id="tokValid" style="margin-top:5px">${tokMsg()}</div></div>` : ''}
          <label><input type="radio" name="si" data-a="signin" value="domain" ${R.signin === 'domain' ? 'checked' : ''}><span><b>Domain account</b><small>Type a different EXAMPLE\\ user and password.</small></span></label>
          ${R.signin === 'domain' ? `<div class="row" style="margin:0 0 6px 32px"><input class="input" placeholder="EXAMPLE\\user"><input class="input" type="password" placeholder="Password"><button class="btn" data-a="domainok">Sign in</button></div>` : ''}
          <label><input type="radio" name="si" data-a="signin" value="cancel" ${R.signin === 'cancel' ? 'checked' : ''}><span><b>Cancel setup</b><small>Nothing was created on buildbox.</small></span></label>
        </div></div>`;
    }
    return h;
  };
  function tokMsg() {
    const s = A.remote.tokStatus;
    if (s === 'checking') return '<span class="spinner"></span> Checking with buildbox…';
    if (s === 'ok') return `${ICON.check} Signed in as <b>dana</b> · admin · VM allowance unlimited`;
    if (s === 'bad') return `${ICON.x} buildbox rejected this token (expired or revoked)`;
    return '<span class="muted">Checked as you paste.</span>';
  }
  VALID.connect = () => {
    const R = A.remote;
    if (R.conn !== 'ok') return { ok: false, hint: 'Connect to continue' };
    if (R.pin !== 'yes') return { ok: false, hint: R.pin === 'no' ? `${ICON.err} Certificate not trusted` : 'Confirm the fingerprint to continue' };
    if (R.signin === 'cancel') return { ok: false, hint: 'Setup will close' };
    if (R.tokStatus !== 'ok') return { ok: false, hint: 'Sign in to continue' };
    return { ok: true };
  };

  const NAME_RE = /^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$/;
  function nameCheck(n) {
    if (!n) return ['bad', 'Pick a name'];
    if (/[A-Z]/.test(n)) return ['bad', 'Use lowercase only: ' + n.toLowerCase().replace(/[^a-z0-9-]/g, '-')];
    if (!NAME_RE.test(n)) return ['bad', 'Use lowercase letters, digits and hyphens; start and end with a letter or digit'];
    if (n === 'dev-2' || n === 'agent-vm-2') return ['exists', `${n} already exists on buildbox`];
    if (n === 'agent-vm') return ['bad', 'agent-vm is already an instance on this PC (Hyper-V). Names must be unique on this PC.'];
    return ['ok', `Available · shows up as <b>${n}</b> in the Companion and as <span class="mono">Host ${n}</span> in ~\\.ssh\\config`];
  }
  PAGES.instance = () => {
    const [st, msg] = nameCheck(A.remote.instance);
    return `<div class="eyebrow">Step 3 · Shared host</div><h1>Name this instance</h1>
      <p class="lead">One name per VM. It's used on buildbox, in the Companion, and as the SSH host name.</p>
      <div class="card"><div class="field"><label for="iname">Instance name</label>
        <input id="iname" class="input mono ${st === 'ok' ? 'good' : st ? 'bad' : ''}" value="${esc(A.remote.instance)}" data-bind="remote.instance" spellcheck="false" autocomplete="off">
        <div class="valid ${st === 'ok' ? 'ok' : st === 'exists' ? 'warn' : 'bad'}" id="nameValid">${st === 'ok' ? ICON.check : st === 'exists' ? ICON.warn : ICON.x} <span>${msg}${st === 'exists' ? ' · <button class="link" data-a="manageExisting">Open its actions instead</button>' : ''}</span></div>
        <div class="help">Lowercase letters, digits and hyphens.</div></div></div>
      <div class="card"><h3>Your instances on buildbox</h3>
        <div class="lrows" style="margin-top:8px;border:0">
          <div class="lrow" style="padding-left:0"><span class="dot" style="background:#8a948e"></span><div class="ln"><b>dev-2</b><span>saved by idle policy 41 min ago · 4 vCPU · 12 GB</span></div><button class="btn sm" data-a="manageExisting">Manage</button></div>
          <div class="lrow" style="padding-left:0"><span class="dot"></span><div class="ln"><b>agent-vm-2</b><span>running · 16 GB</span></div><button class="btn sm" data-a="manageExisting">Manage</button></div>
        </div></div>`;
  };
  VALID.instance = () => nameCheck(A.remote.instance)[0] === 'ok' ? { ok: true } : { ok: false, hint: 'Pick an available name' };

  PAGES.features = () => {
    const ro = A.preset !== 'custom';
    const groups = [...new Set(COMPONENTS.map(c => c.group))];
    let rows = '';
    groups.forEach(g => {
      rows += `<div class="grp">${g}</div>`;
      COMPONENTS.filter(c => c.group === g).forEach(c => {
        const req = c.requires && !A.feat[c.requires];
        const hide = c.localOnly && A.variant === 'remote';
        if (hide) return;
        const on = !!A.feat[c.key] && !req;
        rows += `<div class="comp ${on ? '' : 'off'} ${req ? 'dis' : ''}"><div class="cn"><b>${c.name}</b> ${c.badge ? `<span class="badge ${c.badge[0]}">${c.badge[1]}</span>` : ''}
          <span>${c.desc}${req ? ` <i>Needs ${COMPONENTS.find(x => x.key === c.requires).name}.</i>` : ''}</span></div>
          <button class="sw ${ro ? 'ro' : ''}" role="switch" aria-checked="${on}" aria-label="${c.name}" data-a="feat" data-k="${c.key}" ${req ? 'disabled' : ''}></button></div>`;
      });
    });
    const tile = (id, icon, name, desc, meta) => `<button class="tile" role="radio" aria-checked="${A.preset === id}" data-a="preset" data-v="${id}">
      <span class="radio"></span><span class="ti">${icon}</span><b>${name}</b><span class="td">${desc}</span><span class="tm">${meta}</span></button>`;
    return `<div class="eyebrow">Step ${A.variant === 'remote' ? 4 : 2}</div><h1>Pick a feature set</h1>
      <p class="lead">Every set includes the VM, the agents (Claude Code, Codex, OpenCode), the VS Code panel and your projects.</p>
      <div class="tiles" role="radiogroup">
        ${tile('minimal', ICON.box, 'Minimal', 'Agents, Companion and git. The quickest install.', '<span class="badge">4 of 13</span><span class="badge">~12 min</span>')}
        ${tile('full', ICON.spark, 'Full', 'Adds browser IDE, T3 Code + Desktop, SMB share and mic.', '<span class="badge">10 of 13</span><span class="badge warn">~28 min</span>')}
        ${tile('custom', ICON.edit, 'Custom', 'Choose each component yourself.', `<span class="badge">${A.preset === 'custom' ? onCount() : '…'} of 13</span>`)}
      </div>
      <div class="comp-summary"><b>${presetLabel()}</b><span class="muted">${onCount()} of 13 components on${ro ? ' · switch any to customise' : ''}</span></div>
      <div class="comps ${ro ? '' : ''}">${rows}</div>`;
  };

  PAGES.resources = () => {
    if (A.variant === 'remote') {
      return `<div class="eyebrow">Step 5 · Shared host</div><h1>How big should the VM be?</h1>
        <p class="lead">buildbox checks this against your allowance and the host's admission line before creating anything.</p>
        <div class="card"><div class="row" style="align-items:center"><div><div class="flabel" style="font-size:12.5px;font-weight:600;color:var(--ink-2)">Memory</div><div class="bigval" id="ramVal">${A.ram}<small>GB</small></div></div>
          <div style="flex:2"><div class="slider-wrap"><input type="range" class="rng" min="4" max="24" step="1" value="${A.ram}" data-a="ram" aria-label="VM memory in GB"></div>
          <div class="rng-scale"><span>4</span><span>12</span><span>24 GB</span></div></div></div>
          <div class="callout info"><span class="ic">${ICON.info}</span><div>buildbox admits up to <b>22 GB</b> more right now (118 GB line, 96 GB resident). Memory pressure is <b>elevated</b>, so idle VMs may get saved sooner.</div></div></div>
        ${diskCard('Allocated on buildbox D: (3.6 TB, 61% used). The disk grows on demand.')}`;
    }
    const osKeep = HOST_RAM - A.ram;
    return `<div class="eyebrow">Step 3</div><h1>How much of this PC can the VM use?</h1>
      <p class="lead">The VM holds its memory while it runs. The Companion can save it automatically when idle. You can change both later.</p>
      <div class="card">
        <div class="row" style="align-items:flex-end"><div><div style="font-size:12.5px;font-weight:600;color:var(--ink-2)">VM memory</div><div class="bigval" id="ramVal">${A.ram}<small>GB</small></div></div>
          <div style="text-align:right;flex:none"><div class="muted" style="font-size:12px">PC-1 has <b style="color:var(--ink)">64 GB</b></div>
          <button class="link" data-a="ramReset" style="font-size:12.5px">Reset to recommended (${recRam} GB)</button></div></div>
        <div class="ramviz" id="ramviz"><div class="vm" style="width:${A.ram / HOST_RAM * 100}%">agent-vm · ${A.ram} GB</div><div class="os" style="width:${Math.min(16, osKeep) / HOST_RAM * 100}%">Windows</div><div class="free">${osKeep} GB left for Windows and apps</div></div>
        <div class="slider-wrap"><input type="range" class="rng" min="4" max="48" step="1" value="${A.ram}" data-a="ram" aria-label="VM memory in GB"></div>
        <div class="rng-scale"><span>4</span><span>12</span><span>21 · recommended</span><span>32</span><span>48 GB</span></div>
        <div class="help muted" style="font-size:12px;margin-top:6px">Recommended is a third of this PC's memory, kept between 4 and 24 GB.</div>
        <div id="ramWarn">${A.ram > 32 ? `<div class="callout warn"><span class="ic">${ICON.warn}</span><div>Only ${osKeep} GB would be left for Windows, VS Code and browsers. That's fine for a dedicated machine, tight for a daily driver.</div></div>` : ''}</div>
      </div>
      <div class="grid2" style="margin-top:12px">${diskCard('Grows on demand. C: has 412 GB free.')}
        <div class="card"><div style="font-size:12.5px;font-weight:600;color:var(--ink-2)">Processors</div><div class="bigval">8<small>vCPU</small></div>
          <div class="help muted" style="font-size:12px;margin-top:6px">Automatic: half of the 16 logical processors. Change it later in the Companion.</div></div></div>`;
  };
  function diskCard(help) {
    return `<div class="card"><div style="font-size:12.5px;font-weight:600;color:var(--ink-2)">Disk size</div>
      <div class="row" style="align-items:center;margin-top:4px"><div class="bigval" style="flex:none" id="diskVal">${A.disk}<small>GB</small></div>
      <span class="stepper" style="flex:none;margin-left:auto"><button data-a="disk" data-d="-10" aria-label="Smaller">−</button><input value="${A.disk}" data-a="diskIn" aria-label="Disk size in GB"><button data-a="disk" data-d="10" aria-label="Bigger">+</button></span></div>
      <div class="help muted" style="font-size:12px;margin-top:6px">${help} Minimum 10 GB.</div></div>`;
  }

  PAGES.projects = () => {
    const rows = PROFILES.map(p => `<div class="lrow"><button class="cb" role="checkbox" aria-checked="${!!A.projects[p.name]}" data-a="proj" data-v="${p.name}" aria-label="${p.name}">${ICON.check}</button>
      <div class="ln"><b>${p.name}</b> ${p.isNew ? '<span class="badge new">new · found in config repo</span>' : ''} ${p.host !== 'github.com' ? '<span class="badge">private</span>' : ''}
      <span>${p.repo} · ${p.sdk}</span></div></div>`).join('');
    const C = A.cred;
    const needsGl = PROFILES.some(p => A.projects[p.name] && p.host === 'gitlab.example.com');
    const gl = !needsGl ? `<div class="hostcard"><div class="hh">${ICON.globe} gitlab.example.com <span class="badge">not needed · no selected project uses it</span></div></div>` :
      `<div class="hostcard"><div class="hh">${ICON.lock} gitlab.example.com <span class="muted" style="font-weight:400;font-size:12px">omniloop, gitgudlab, jarvis</span>
        <span class="badge ${C.status === 'ok' ? 'ok' : C.status === 'skipped' ? '' : 'warn'}">${C.status === 'ok' ? 'verified' : C.status === 'skipped' ? 'skipped' : 'anonymous clone refused'}</span></div>
        <div class="row"><div class="field"><label>Username</label><input class="input" value="${esc(C.user)}" data-bind="cred.user" placeholder="empty = skip this host"></div>
        <div class="field" style="flex:1.4"><label>Access token</label><div class="pw-wrap"><input id="gltok" class="input mono" type="password" value="${esc(C.token)}" data-bind="cred.token" placeholder="glpat-…"><button class="eye" data-a="eye" data-t="gltok">Show</button></div></div></div>
        <div class="valid ${C.status === 'ok' ? 'ok' : C.status === 'checking' ? 'wait' : C.status === 'bad' ? 'bad' : C.status === 'skipped' ? 'warn' : ''}" id="credValid" style="margin-top:6px">${credMsg()}</div></div>`;
    return `<div class="eyebrow">Step ${A.variant === 'remote' ? 6 : 4}</div><h1>Which projects should the VM get?</h1>
      <p class="lead">Each profile installs its runtimes (node, python, .NET) and clones its repos into /root/repos.</p>
      <div class="row" style="align-items:center;margin-bottom:8px"><span style="font-size:12.5px" class="muted">${PROFILES.length} profiles in your config repo · synced 3 min ago</span>
        <button class="btn sm" data-a="projAll" style="flex:none">Select all</button><button class="btn sm" style="flex:none" data-a="openCfg">${ICON.folder} Open projects config folder</button></div>
      <div class="lrows">${rows}</div>
      <h2>Private git hosts</h2>
      <div class="hostcard"><div class="hh">${ICON.globe} github.com <span class="badge ok">${ICON.check} public · no sign-in needed</span></div></div>
      ${gl}`;
  };
  function credMsg() {
    const C = A.cred;
    if (!C.user) return `${ICON.warn} Skipped: omniloop, gitgudlab and jarvis won't be cloned`;
    if (C.status === 'checking') return '<span class="spinner"></span> Checking read access…';
    if (C.status === 'ok') return `${ICON.check} Verified · read access to omniloop, gitgudlab, jarvis`;
    if (C.status === 'bad') return `${ICON.x} gitlab.example.com said 401: wrong token, or it lacks the read_repository scope`;
    return '<span class="muted">Paste a token with read_repository. It is checked live and masked in the log.</span>';
  }
  VALID.projects = () => {
    const needsGl = PROFILES.some(p => A.projects[p.name] && p.host === 'gitlab.example.com');
    if (needsGl && A.cred.user && A.cred.status !== 'ok') return { ok: false, hint: 'Verify the token, or clear the username to skip that host' };
    if (!selProjects().length) return { ok: true, hint: `${ICON.info} No projects: the VM starts empty` };
    return { ok: true };
  };

  PAGES.identity = () => `<div class="eyebrow">Step ${A.variant === 'remote' ? 7 : 5}</div><h1>Who do the agents commit as?</h1>
    <p class="lead">Agents sign their commits with this identity. It's read from your global git config.</p>
    <div class="card"><div class="row">
      <div class="field"><label>Git name</label><input class="input" value="${esc(A.gitName)}" data-bind="gitName"></div>
      <div class="field"><label>Git email</label><input class="input" value="${esc(A.gitEmail)}" data-bind="gitEmail"></div></div>
      <div class="help muted" style="font-size:12px;margin-top:6px">${ICON.check.replace('<svg', '<svg style="width:12px;height:12px;vertical-align:-2px;color:var(--accent-700)"')} From <span class="mono">git config --global</span> on PC-1</div></div>
    <div class="card"><div class="qrow" style="border:0;padding:0"><div class="qt"><b>Store git credentials on the VM</b><span>Agents can push, pull and open merge requests without asking you.</span></div>
      <button class="sw" role="switch" aria-checked="${A.credStore}" data-a="credStore" aria-label="Store git credentials on the VM"></button></div>
      ${A.credStore ? `<div class="callout warn"><span class="ic">${ICON.warn}</span><div>Credentials are saved in <b>plaintext</b> (<span class="mono">~/.git-credentials</span>) and are readable by anything on the VM, including the AI agents. A prompt-injection attack could exfiltrate them. Use tokens scoped to the repos you need.</div></div>` :
        `<div class="callout info"><span class="ic">${ICON.info}</span><div>Agents will stop and ask whenever git needs a password. Repos are still cloned during setup.</div></div>`}</div>
    <div class="card"><div class="field"><label>VM user password</label><div class="row"><div class="pw-wrap"><input id="agpw" class="input mono" type="password" value="${esc(A.agentPw)}" data-bind="agentPw"><button class="eye" data-a="eye" data-t="agpw">Show</button></div></div>
      <div class="help">Only a fallback for the VM console. SSH and VS Code use keys. Default: <span class="mono">agent</span>.</div></div></div>`;
  VALID.identity = () => (A.gitName && /.+@.+\..+/.test(A.gitEmail)) ? { ok: true } : { ok: false, hint: 'Name and a valid email are needed' };

  function cmdLine() {
    const feats = A.preset === 'custom' ? ' ' + COMPONENTS.filter(c => c.key !== 'companion').map(c => `-${{ claudeStreaming: 'ClaudePartialStreaming', credStore: 'GitCredentialStore', serveWeb: 'VsCodeServeWeb', tunnel: 'VsCodeTunnel', smb: 'SmbShare', mapDrive: 'MountRepoShare', mic: 'MicPassthrough', checkpoints: 'AutomaticCheckpoints', ocWatcher: 'OpenCodeBackgroundWatcher', t3: 'T3Code', t3Patched: 'T3CodeLimitResume', t3Https: 'T3CodeHttps' }[c.key]} ${A.feat[c.key] ? 'true' : 'false'}`).join(' ') : '';
    const back = A.variant === 'remote' ? ` -Backend hyperv-remote -HostUrl ${A.remote.url} -InstanceName ${A.remote.instance}` : '';
    return `.\\Auto-Install.ps1 -FeatureSet ${A.preset}${feats}${back}\n  -VmMemoryGB ${A.ram} -VmDiskGB ${A.disk} -Projects ${selProjects().join(',')}\n  -GitCloneCredentialsB64 <masked> -NonInteractive`;
  }
  PAGES.review = () => {
    const on = COMPONENTS.filter(c => A.feat[c.key] && !(c.requires && !A.feat[c.requires])).map(c => c.name);
    const sec = (title, page, rows) => `<div class="rv"><div class="rv-h">${title}<button class="link" data-go="${page}">${ICON.edit} Edit</button></div><dl>${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl></div>`;
    const steps = [];
    if (A.variant === 'local') { steps.push(`<span class="u">${ICON.shield} Approve one Windows prompt</span>`, `<span class="u">${ICON.restart} Restart once for Hyper-V</span>`, `<span>${ICON.down} Download 3.1 GB</span>`); }
    else steps.push(`<span>${ICON.server} buildbox creates the VM</span>`);
    steps.push(`<span>${ICON.clock} Ubuntu installs itself (~5 min)</span>`);
    if (A.feat.tunnel) steps.push(`<span class="u">${ICON.key} GitHub sign-in for the tunnel</span>`);
    if (A.feat.t3Https && A.feat.t3) steps.push(`<span class="u">${ICON.lock} Trust the T3 certificate</span>`);
    return `<div class="eyebrow">Last step</div><h1>Ready to install</h1>
      <p class="lead">Check your answers. Setup won't ask anything else except the moments marked in amber below.</p>
      <div class="card" style="padding:12px 14px"><b style="font-size:13.5px">What happens next · ${estRange(100)}</b><div class="timeline-mini">${steps.join('')}</div></div>
      <div style="margin-top:12px"></div>
      ${sec('Where it runs', 'location', A.variant === 'remote' ? [['Location', 'Shared host <b>buildbox</b> (constructd 1.14.2)'], ['Certificate', 'Pinned · SHA256 5A:3C:…:E8:B6'], ['Signed in', 'dana · API token']] : [['Location', 'This PC (Hyper-V) · VM name <b>agent-vm</b>'], ['Hyper-V', 'Will be turned on (restart needed)']])}
      ${A.variant === 'remote' ? sec('Instance', 'instance', [['Name', `<span class="mono">${esc(A.remote.instance)}</span>`]]) : ''}
      ${sec('Features', 'features', [['Set', `${presetLabel()} · ${on.length} of 13`], ['On', on.join(', ')]])}
      ${sec('Resources', 'resources', [['Memory', `${A.ram} GB${A.ram === recRam && A.variant === 'local' ? ' (recommended)' : ''}`], ['Disk', `${A.disk} GB, grows on demand`], ['Processors', A.variant === 'remote' ? '4 vCPU (host allowance)' : '8 vCPU (automatic)']])}
      ${sec('Projects', 'projects', [['Profiles', selProjects().join(', ') || 'none'], ['Private hosts', A.cred.user ? `gitlab.example.com as <b>${esc(A.cred.user)}</b> · ${A.cred.status === 'ok' ? '<span style="color:var(--accent-ink)">verified</span>' : 'not verified'}` : 'gitlab.example.com skipped']])}
      ${sec('Identity', 'identity', [['Git identity', `${esc(A.gitName)} &lt;${esc(A.gitEmail)}&gt;`], ['Credentials on VM', A.credStore ? '<span style="color:var(--warn)">Stored in plaintext</span>' : 'Not stored'], ['VM user password', A.agentPw === 'agent' ? 'default (agent)' : 'custom']])}
      <details style="margin-top:12px"><summary class="link" style="font-size:12.5px">Show as a PowerShell command</summary><div class="cmdline"><span class="k">PS&gt;</span> ${esc(cmdLine())}</div></details>`;
  };

  // ---------- existing-VM pages ----------
  PAGES.existing = () => {
    const acts = Object.entries(EX_ACTIONS).map(([k, a]) => `<button class="act ${a.danger ? 'danger' : ''}" role="radio" aria-checked="${A.ex.action === k}" data-a="exact" data-v="${k}">
        <span class="ai">${ICON[a.icon]}</span><span class="an"><b>${a.label} ${a.rec ? '<span class="badge ok">recommended</span>' : ''}${a.danger ? '<span class="badge err">erases the VM</span>' : ''}</b><span>${a.desc}</span></span><span class="radio"></span></button>
        ${k === 'addconfig' && A.ex.action === 'addconfig' ? `<div class="act-extra"><div class="row"><input class="input mono" placeholder="https://git… or C:\\path\\to\\config" value="${esc(A.ex.addUrl)}" data-bind="ex.addUrl"><button class="btn" style="flex:none">${ICON.folder} Browse</button></div>
          <div class="valid ${A.ex.addUrl ? 'ok' : ''}" id="addValid" style="margin-top:4px">${A.ex.addUrl ? `${ICON.check} 2 profiles found: <b>jarvis</b>, <b>voice-lab</b>` : '<span class="muted">A git URL or a folder that contains a projects/ directory.</span>'}</div></div>` : ''}`).join('');
    return `<div class="eyebrow">Already installed</div><h1>What do you want to do with agent-vm?</h1>
      <div class="card" style="margin-top:12px"><div class="vmstat"><span class="vmi">${LOGO}</span><div class="vmt"><b>agent-vm</b> <span class="badge ok"><span class="dot"></span>running · 3h 12m</span> <span class="badge warn">behind by 2 commits</span>
        <div class="facts"><span>Hyper-V on this PC</span><span>Ubuntu 26.04 LTS</span><span>8 vCPU · 16 GB</span><span style="color:var(--warn)">disk 146 GB · 88% used</span><span>provisioned at <span class="mono">c21978b</span></span></div></div></div>
        <div class="help muted" style="font-size:12px;margin-top:8px;border-top:1px solid var(--line);padding-top:8px">Other instances: <b>dev-2</b> on buildbox (saved 41 min ago) · <b>win-test</b> (child VM of agent-vm) · <button class="link">Switch instance</button></div></div>
      <div class="actlist" role="radiogroup">${acts}</div>`;
  };
  VALID.existing = () => A.ex.action === 'addconfig' && !A.ex.addUrl ? { ok: false, hint: 'Enter a git URL or folder' } : { ok: true };

  PAGES.safety = () => {
    const X = A.ex;
    const scan = `<div class="gitscan">
      <div style="color:var(--warn-ink)">${ICON.warn} <b>construct</b> · feature/forwarder-backoff · 2 uncommitted files, 1 unpushed commit</div>
      <div style="color:var(--warn-ink)">${ICON.warn} <b>jarvis</b> · main · 1 stash</div>
      <div style="color:var(--accent-ink)">${ICON.check} omniloop, gitgudlab · clean and pushed</div></div>`;
    return `<div class="eyebrow">${EX_ACTIONS[X.action].label}</div><h1>Before agent-vm is erased</h1>
      <p class="lead">Everything on the VM disk is deleted. Setup checked your repos and can carry the agent config over.</p>
      <div class="card"><h3>${ICON.search.replace('<svg', '<svg style="width:15px;height:15px"')} Unsaved work on the VM</h3>${scan}
        <div class="qrow" style="margin-top:8px;border-top:1px solid var(--line);padding-top:10px"><div class="qt"><b>Continue with unsaved work?</b><span>Unpushed commits and stashes are lost.</span></div>
        <div class="seg-ctl danger"><button data-a="unsaved" data-v="abort" aria-pressed="${X.unsaved === 'abort'}">Abort</button><button class="bad" data-a="unsaved" data-v="continue" aria-pressed="${X.unsaved === 'continue'}">Continue anyway</button></div></div></div>
      <div class="card"><div class="qrow" style="border:0;padding:0"><div class="qt"><b>Save and restore the agent config</b><span>Claude, Codex and OpenCode logins, MCP servers, settings and shell history, restored onto the new VM.</span></div>
        <button class="sw" role="switch" aria-checked="${X.saveCfg}" data-a="saveCfg" aria-label="Save and restore the agent config"></button></div>
        ${!X.saveCfg ? `<div class="qrow"><div class="qt"><b>Continue without a backup?</b><span>You'll sign in to every agent again.</span></div>
          <div class="seg-ctl danger"><button data-a="noBackup" data-v="no" aria-pressed="${X.noBackup === 'no'}">No</button><button class="bad" data-a="noBackup" data-v="yes" aria-pressed="${X.noBackup === 'yes'}">Yes, no backup</button></div></div>` : ''}</div>
      <div class="card"><div class="qrow" style="border:0;padding:0"><div class="qt"><b>Auto-restore the saved config?</b><span>A backup from today 13:58 exists (3 agents, 3 MCP servers). ${X.saveCfg ? 'A fresh one is taken first.' : ''}</span></div>
        <div class="seg-ctl"><button data-a="restore" data-v="yes" aria-pressed="${X.restore === 'yes'}">Yes (recommended)</button><button data-a="restore" data-v="no" aria-pressed="${X.restore === 'no'}">No</button></div></div></div>`;
  };
  VALID.safety = () => {
    if (A.ex.unsaved === 'abort') return { ok: false, hint: `${ICON.warn} Push your work first, or choose Continue anyway` };
    if (!A.ex.saveCfg && A.ex.noBackup !== 'yes') return { ok: false, hint: 'Turn the backup on, or confirm without one' };
    return { ok: true };
  };
  PAGES.remove = () => `<div class="eyebrow">Remove instance</div><h1>Remove agent-vm from this PC</h1>
    <p class="lead">This can't be undone. Your config repo, backups and project remotes are not touched.</p>
    <div class="grid2"><div class="card"><h3 style="color:var(--danger-ink)">${ICON.trash.replace('<svg', '<svg style="width:15px;height:15px"')} Removed</h3><ul class="checks">
      ${A.ex.keepVm ? '' : chk('err', 'The VM and its 146 GB disk', 'Hyper-V')}
      ${chk('err', 'Host agent-vm in ~\\.ssh\\config', '')}${chk('err', 'Saved root key', '')}${chk('err', 'agent-vm in the Companion', '')}${A.ex.keepVm ? '' : chk('err', 'Child VM win-test', 'lease 5h 20m left')}</ul></div>
    <div class="card"><h3 style="color:var(--accent-ink)">${ICON.check.replace('<svg', '<svg style="width:15px;height:15px"')} Kept</h3><ul class="checks">
      ${chk('ok', 'Config repo and project profiles', '')}${chk('ok', 'Agent config backups', '3')}${chk('ok', 'dev-2 on buildbox', '')}${A.ex.keepVm ? chk('ok', 'The VM itself', 'in Hyper-V Manager') : ''}</ul></div></div>
    <div class="card"><div class="qrow" style="border:0;padding:0"><div class="qt"><b>Keep the VM, only forget it here</b><span>Leaves agent-vm in Hyper-V (same as -KeepVm).</span></div>
      <button class="sw" role="switch" aria-checked="${A.ex.keepVm}" data-a="keepVm" aria-label="Keep the VM"></button></div></div>`;

  PAGES.confirm = () => {
    const X = A.ex, a = EX_ACTIONS[X.action];
    const rows = {
      reprovision: [['Projects', 'construct, omniloop, gitgudlab, jarvis'], ['Construct', '<span class="mono">c21978b</span> → <span class="mono">3a91f0e</span>'], ['Features', 'Saved choices (Minimal)'], ['Downtime', 'None · agents keep running']],
      reinstall: [['Image', 'Saved autoinstall ISO (Ubuntu 26.04 LTS)'], ['Agent config', X.saveCfg ? 'Backed up, then restored' : 'Not saved'], ['Unsaved work', X.unsaved === 'continue' ? '<span style="color:var(--danger)">Discarded (construct, jarvis)</span>' : '—'], ['Resources', '16 GB · 146 GB disk (unchanged)']],
      redownload: [['Image', 'Fresh Ubuntu Server 26.04 download (3.1 GB)'], ['Agent config', X.saveCfg ? 'Backed up, then restored' : 'Not saved'], ['Unsaved work', X.unsaved === 'continue' ? '<span style="color:var(--danger)">Discarded (construct, jarvis)</span>' : '—']],
      export: [['Destination', '%USERPROFILE%\\The-Construct\\backup\\agent-vm-20260923-1412'], ['Includes', 'Agent logins, MCP servers, settings, shell history']],
      addconfig: [['Source', `<span class="mono">${esc(X.addUrl)}</span>`], ['New profiles', 'jarvis, voice-lab'], ['Then', 'Reprovision agent-vm']],
      remove: [['Removes', X.keepVm ? 'Only this PC\'s record of agent-vm' : 'agent-vm, its disk and child VM win-test'], ['Keeps', 'Config repo, backups']]
    }[X.action];
    return `<div class="eyebrow">Confirm</div><h1>${a.label}${a.danger ? '' : ' agent-vm'}</h1>
      <p class="lead">${a.desc}</p>
      <div class="rv"><dl style="padding-top:12px">${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl></div>
      ${a.danger ? `<div class="confirm-box"><label for="cfm">Type <span class="mono">agent-vm</span> to confirm</label><div class="row"><input id="cfm" class="input mono" autocomplete="off" spellcheck="false" value="${esc(X.confirm)}" data-bind="ex.confirm" placeholder="agent-vm"></div></div>` : ''}`;
  };
  VALID.confirm = () => EX_ACTIONS[A.ex.action].danger && A.ex.confirm !== 'agent-vm' ? { ok: false, hint: 'Type the instance name to enable the button' } : { ok: true };

  // ---------- page interactions ----------
  function afterRender() { }
  function setPath(path, val) { const ks = path.split('.'); let o = A; while (ks.length > 1) o = o[ks.shift()]; o[ks[0]] = val; }
  let credTimer = null, tokTimer = null;
  $('page').addEventListener('input', e => {
    const t = e.target;
    if (t.dataset.bind) {
      setPath(t.dataset.bind, t.value);
      if (t.dataset.bind === 'cred.token' || t.dataset.bind === 'cred.user') {
        A.cred.status = !A.cred.user ? 'skipped' : A.cred.token ? 'checking' : 'idle';
        updateCred();
        clearTimeout(credTimer);
        if (A.cred.user && A.cred.token) credTimer = setTimeout(() => { A.cred.status = A.cred.token.length > 10 ? 'ok' : 'bad'; updateCred(); }, 900);
      }
      if (t.dataset.bind === 'remote.token') {
        A.remote.tokStatus = t.value ? 'checking' : 'idle'; $('tokValid').className = 'valid wait'; $('tokValid').innerHTML = tokMsg();
        clearTimeout(tokTimer);
        if (t.value) tokTimer = setTimeout(() => { A.remote.tokStatus = t.value.length > 8 ? 'ok' : 'bad'; const v = $('tokValid'); if (v) { v.className = 'valid ' + (A.remote.tokStatus === 'ok' ? 'ok' : 'bad'); v.innerHTML = tokMsg(); } renderFoot(); }, 900);
      }
      if (t.dataset.bind === 'remote.instance') {
        const [st, msg] = nameCheck(t.value);
        t.className = 'input mono ' + (st === 'ok' ? 'good' : 'bad');
        const v = $('nameValid'); v.className = 'valid ' + (st === 'ok' ? 'ok' : st === 'exists' ? 'warn' : 'bad');
        v.innerHTML = `${st === 'ok' ? ICON.check : st === 'exists' ? ICON.warn : ICON.x} <span>${msg}${st === 'exists' ? ' · <button class="link" data-a="manageExisting">Open its actions instead</button>' : ''}</span>`;
      }
      if (t.dataset.bind === 'ex.addUrl') { const v = $('addValid'); v.className = 'valid ' + (t.value ? 'ok' : ''); v.innerHTML = t.value ? `${ICON.check} 2 profiles found: <b>jarvis</b>, <b>voice-lab</b>` : '<span class="muted">A git URL or a folder that contains a projects/ directory.</span>'; }
      renderFoot(); renderRail();
      return;
    }
    if (t.dataset.a === 'ram') {
      A.ram = +t.value;
      $('ramVal').innerHTML = `${A.ram}<small>GB</small>`;
      const viz = $('ramviz');
      if (viz) {
        const os = HOST_RAM - A.ram;
        viz.innerHTML = `<div class="vm" style="width:${A.ram / HOST_RAM * 100}%">agent-vm · ${A.ram} GB</div><div class="os" style="width:${Math.min(16, os) / HOST_RAM * 100}%">Windows</div><div class="free">${os} GB left for Windows and apps</div>`;
        $('ramWarn').innerHTML = A.ram > 32 ? `<div class="callout warn"><span class="ic">${ICON.warn}</span><div>Only ${os} GB would be left for Windows, VS Code and browsers. That's fine for a dedicated machine, tight for a daily driver.</div></div>` : '';
      }
      renderRail();
    }
    if (t.dataset.a === 'diskIn') { const v = parseInt(t.value, 10); if (v >= 10) { A.disk = v; $('diskVal').innerHTML = `${A.disk}<small>GB</small>`; renderRail(); } }
  });
  function updateCred() {
    const v = $('credValid'); if (!v) return;
    const s = A.cred.status;
    v.className = 'valid ' + (s === 'ok' ? 'ok' : s === 'checking' ? 'wait' : s === 'bad' ? 'bad' : !A.cred.user ? 'warn' : '');
    v.innerHTML = credMsg();
    const badge = v.parentElement.querySelector('.hh .badge');
    if (badge) { badge.className = 'badge ' + (s === 'ok' ? 'ok' : s === 'skipped' ? '' : 'warn'); badge.textContent = s === 'ok' ? 'verified' : s === 'skipped' ? 'skipped' : 'anonymous clone refused'; }
    renderFoot();
  }
  $('page').addEventListener('click', e => {
    const go_ = e.target.closest('[data-go]'); if (go_) { go(go_.dataset.go); return; }
    const b = e.target.closest('[data-a]'); if (!b) return;
    const a = b.dataset.a, v = b.dataset.v;
    const rer = () => { renderPage(); renderRail(); };
    switch (a) {
      case 'loc': A.variant = v; if (v === 'remote' && A.ram === recRam) A.ram = 12; if (v === 'local' && A.ram === 12) A.ram = recRam; W.maxIdx = 1; rer(); break;
      case 'useRemote': A.variant = 'remote'; go('location'); break;
      case 'preset': applyPreset(v); if (v === 'custom') COMPONENTS.forEach(c => { if (A.feat[c.key] == null) A.feat[c.key] = c.min; }); rer(); break;
      case 'feat': if (b.disabled) return; if (A.preset !== 'custom') A.preset = 'custom'; A.feat[b.dataset.k] = !A.feat[b.dataset.k]; if (b.dataset.k === 'credStore') A.credStore = A.feat.credStore; rer(); break;
      case 'ramReset': A.ram = recRam; rer(); break;
      case 'disk': A.disk = Math.max(10, A.disk + +b.dataset.d); rer(); break;
      case 'proj': A.projects[v] = !A.projects[v]; rer(); break;
      case 'projAll': PROFILES.forEach(p => A.projects[p.name] = true); rer(); break;
      case 'openCfg': toast('Opened', '%USERPROFILE%\\The-Construct\\config\\projects in Explorer'); break;
      case 'eye': { const inp = $(b.dataset.t); inp.type = inp.type === 'password' ? 'text' : 'password'; b.textContent = inp.type === 'password' ? 'Show' : 'Hide'; break; }
      case 'credStore': A.credStore = !A.credStore; A.feat.credStore = A.credStore; rer(); break;
      case 'connect': A.remote.conn = 'connecting'; rer(); setTimeout(() => { A.remote.conn = 'ok'; rer(); }, 1100); break;
      case 'pin': A.remote.pin = v; rer(); break;
      case 'signin': A.remote.signin = b.value; rer(); break;
      case 'domainok': A.remote.tokStatus = 'ok'; toast('Signed in', 'as EXAMPLE\\dana'); rer(); break;
      case 'manageExisting': A.variant = 'existing'; W.maxIdx = 0; go('existing'); toast('Switched', 'Showing the actions for an existing instance'); break;
      case 'exact': A.ex.action = v; A.ex.confirm = ''; W.maxIdx = 0; rer(); break;
      case 'unsaved': A.ex.unsaved = v; rer(); break;
      case 'saveCfg': A.ex.saveCfg = !A.ex.saveCfg; rer(); break;
      case 'noBackup': A.ex.noBackup = v; rer(); break;
      case 'restore': A.ex.restore = v; rer(); break;
      case 'keepVm': A.ex.keepVm = !A.ex.keepVm; rer(); break;
    }
  });
  $('page').addEventListener('change', e => { if (e.target.dataset.a === 'signin') { A.remote.signin = e.target.value; renderPage(); } });

  // ======================================================================
  // Install engine
  // ======================================================================
  const CLOCK_SCALE = 9;
  const INST = { active: false, phases: [], pi: 0, oi: 0, t: 0, logged: 0, state: 'idle', block: null, speed: 1, sc: { fail: false, warn: false }, warnings: [], failLine: null, auto: true, t0: 0, timer: null, kind: 'install' };

  function prov(opts = {}) {
    const ops = [{ dur: 1.6, big: 'Sending the Construct scripts to the VM.', sub: 'Packing, uploading and unpacking the repo (6.3 MB)', logs: [
      ['step', '==> Packing repo (C:\\Users\\dana\\The-Construct) -> %TEMP%\\construct-repo.tar.gz'],
      ['step', '==> Uploading repo archive to /tmp/construct-repo.tar.gz'], ['step', '==> Unpacking repo on the VM'],
      ['step', `==> ${opts.re ? 'Reprovisioning the existing VM' : 'Provisioning the VM (this can take several minutes)'}`]] }];
    const DET = {
      'Checking free disk space': ['[vm] / has 46.2 GB free'],
      'Running core host bootstrap': ['[vm] apt-get update', '[vm] apt-get install -y --no-install-recommends jq socat inotify-tools git curl build-essential', '[vm] 0 upgraded, 64 newly installed, 0 to remove'],
      'Setting up SMB share for the host': A.feat.smb ? ['[vm] samba: share [repos] -> /root/repos (user agent-smb)'] : ['[vm] SMB share disabled (feature off)'],
      'Installing AI tool: claude': ['[vm] npm i -g @anthropic-ai/claude-code', '[vm] added 12 packages in 9s', '[vm] claude 2.4.3'],
      'Installing AI tool: codex': ['[vm] codex 0.61.0'],
      'Installing AI tool: opencode': ['[vm] opencode 1.9.2'],
      'Installing T3 Code web GUI': A.feat.t3 ? (A.feat.t3Patched ? ['[vm] building t3code 0.9.14-construct.3 from source (bun install, turbo build)', '[vm] \x1b[90m[turbo] apps/web: build (2m 41s)\x1b[0m', '[vm] \x1b[90m[turbo] apps/server: build (58s)\x1b[0m', '[vm] t3code 0.9.14-construct.3 ready'] : ['[vm] t3code 0.9.14 (stable) ready']) : [],
      'Installing project SDKs/runtimes': ['[vm] construct: node 22, dotnet 9.0 ... ok', '[vm] omniloop: node 22, python 3.12 ... ok', '[vm] gitgudlab: dotnet 9.0 ... ok', '[vm] jarvis: python 3.12 ... ok'],
      'Checking out project repos': ['[vm] cloning construct ... done', '[vm] cloning omniloop ... done', '[vm] cloning gitgudlab ... done', '[vm] cloning jarvis ... done'],
      'Configuring MCP servers for the AI tools': ['[vm] claude: 3 servers · codex: 3 servers · opencode: 2 servers'],
      '(Re)starting construct service': ['[vm] construct.service active (running)'],
      'Setting up VS Code server / serve-web / tunnel': [A.feat.serveWeb ? '[vm] serve-web listening on :8000 (token tkn=Zp81xQvV0rT3)' : '[vm] serve-web disabled']
    };
    PROV_STEPS.forEach((t, i) => {
      const n = i + 1;
      if (t.includes('T3') && !A.feat.t3) return ops.push({ dur: .25, big: 'Setting up the VM: agents, tools and your projects.', sub: `Provision step ${n}/31: ${t} · skipped (T3 Code off)`, logs: [['vmstep', `==> ${t}`], ['dim', '[vm] skipped: T3CODE=false']] });
      const extra = (DET[t] || []).map(x => ['vm', x]);
      const op = {
        dur: t === 'Installing T3 Code web GUI' && A.feat.t3Patched ? 6 : t.startsWith('Installing AI tool: claude') ? 2.4 : t === 'Checking out project repos' || t === 'Installing project SDKs/runtimes' ? 2.4 : t === 'Running core host bootstrap' ? 2.6 : extra.length ? 1.2 : .55,
        big: t === 'Installing T3 Code web GUI' && A.feat.t3Patched ? 'Building the patched T3 Code. This is the long one.' : 'Setting up the VM: agents, tools and your projects.',
        sub: `Provision step ${n}/31: ${t}`, logs: [['vmstep', `==> ${t}`]].concat(extra), step: n
      };
      if (t === 'Running core host bootstrap') { op.fail = true; op.failLogs = [
        ['vm', '[vm] E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 1423 (unattended-upgr)'],
        ['vm', '[vm] E: Unable to acquire the dpkg frontend lock (/var/lib/dpkg/lock-frontend), is another process using it?'],
        ['err', 'ERROR: critical step failed: Running core host bootstrap (exit 100)'],
        ['err', 'RESULT: PROVISIONING FAILED'],
        ['info', '  Paste this into your AI coding agent on the VM to diagnose:'],
        ['dim', '  On the last Construct provisioning run, the following step(s) failed: Running core host bootstrap. Read /var/log/construct/provision.log and diagnose and fix the underlying problem.']]; }
      if (t === 'Checking out project repos') op.warn = { title: 'gitgudlab was not cloned', step: `Provision step ${n}/31 · ${t}`, text: "remote: HTTP Basic: Access denied. The token for gitlab.example.com lacks the read_repository scope for dana/gitgudlab.", fix: 'Update the token in the Companion (Identity › Git hosts), then Reprovision.', log: 'WARNING: gitgudlab: authentication failed for https://gitlab.example.com/dana/gitgudlab.git' };
      if (t === 'Configuring MCP servers for the AI tools') op.warn = { title: 'One MCP server did not start', step: `Provision step ${n}/31 · ${t}`, text: 'opencode: MCP server "jarvis-memory" exited: JARVIS_API_KEY is not set.', fix: 'Add JARVIS_API_KEY to the jarvis profile secrets, then Reprovision.', log: 'WARNING: opencode: MCP server jarvis-memory failed to start (JARVIS_API_KEY not set)' };
      if (t === 'Setting up VS Code server / serve-web / tunnel' && A.feat.tunnel) { op.block = 'tunnel'; op.blockLogs = [['vmstep', '==> VS Code tunnel: one-time device sign-in required'], ['info', '    To grant access to the server, please log into https://github.com/login/device and use code \x1b[1;97m8F2C-41D7\x1b[0m']]; }
      ops.push(op);
    });
    ops[ops.length - 1].logs.push(['ok', `[vm] provisioning finished: 31 steps`]);
    return ops;
  }
  function hostCfg() {
    const ops = [{ dur: 1.4, big: 'Connecting this PC to the VM.', sub: '~\\.ssh\\config, VS Code Remote-SSH host, root key', logs: [
      ['step', '==> Retrieving root SSH private key from the VM'], ['step', '==> Configuring the Windows host (~\\.ssh and VS Code)'],
      ['info', `    Hostname for SSH: ${vmName()}${A.variant === 'remote' ? ' (via buildbox forward :2231)' : '.mshome.net'}`], ['info', `    ~\\.ssh\\config: added Host ${vmName()}`]] }];
    if (A.feat.t3Https && A.feat.t3 !== false) ops.push({ dur: 1, big: 'Trusting the T3 Code certificate.', sub: 'Windows shows a Security Warning. Click Yes.', block: 'cert', logs: [['step', "==> Trusting the VM's T3 Code CA on this PC (CurrentUser\\Root)"], ['info', '    Accept it, or T3 Code shows a certificate warning in the browser.']] });
    if (A.feat.smb && A.feat.mapDrive) ops.push({ dur: .8, big: 'Mapping the workspace share.', sub: 'Z: → \\\\agent-vm.mshome.net\\repos', logs: [['step', '==> Mounting the VM workspace share on this host'], ['info', '    Mounted at Z:  (\\\\agent-vm.mshome.net\\repos)']] });
    if (A.feat.t3Patched) ops.push({ dur: 1.6, big: 'Installing T3 Code Desktop on this PC.', sub: 'Patched T3 Code Desktop 0.9.14-construct.3', logs: [['step', '==> Downloading patched T3 Code Desktop 0.9.14-construct.3 installer to this host'], ['step', '==> Silently installing/updating patched T3 Code Desktop 0.9.14-construct.3']] });
    return ops;
  }
  function finalReboot() {
    return [{ dur: 3, big: 'Restarting the VM one last time.', sub: 'Waiting for a new boot ID · up to 5 min', logs: [['step', '==> Removing bootstrap key and rebooting the VM'], ['step', '==> Waiting for the VM to finish its final reboot'], ['info', '    new boot id 7e1c42d0…a90f · SSH ready']] }];
  }
  function ubuntu(extra) {
    return [{ dur: 20, mini: true, big: 'Ubuntu is installing itself inside the VM. Nothing to do.', sub: f => `Usually about 5 minutes · ${fmtDur(f * 20 * CLOCK_SCALE * 1.6)} elapsed`, sub2: `waiting for SSH on ${vmName()}${A.variant === 'remote' ? ' (buildbox)' : '.mshome.net'}:22`, logs: [
      ['step', '==> Autoinstall ISO detected. Waiting for the VM to finish installing'], ['dim', '    (takes about 5 minutes; timeout 20 min)'],
      ['vm', '[vm] subiquity: curtin install: partitioning /dev/sda'], ['vm', '[vm] subiquity: extracting the base system'], ['vm', '[vm] subiquity: installing kernel linux-generic'],
      ['vm', '[vm] subiquity: configuring apt, installing openssh-server'], ['vm', '[vm] cloud-init: running late-commands (3/3)'], ['vm', '[vm] rebooting into the installed system'],
      ['info', `    SSH is answering on ${vmName()} (after 4:52)`]] },
    { dur: 1.2, big: 'Ubuntu is up.', sub: "Accepting the VM's host key", logs: [['step', '==> Accepting VM host key'], ['step', '==> Checking bootstrap key authentication'], ['info', '    ok']] }].concat(extra || []);
  }
  const PRE = [
    { name: 'Get The Construct', w: 2, pre: true, meta: '0:07' },
    { name: 'Companion & panel', w: 4, pre: true, meta: '0:11' },
    { name: 'Your answers', w: 0, pre: true, meta: '3:38' }
  ];
  function buildPhases() {
    const P = [];
    const add = (name, w, ops, extra) => P.push(Object.assign({ name, w, ops }, extra || {}));
    if (A.variant === 'existing') {
      const X = A.ex.action;
      if (X === 'reprovision' || X === 'addconfig') {
        if (X === 'addconfig') add('Import the new config', 8, [{ dur: 2, big: 'Importing project profiles.', sub: 'jarvis, voice-lab', logs: [['step', '==> Importing config'], ['info', '    Imported: jarvis, voice-lab']] }]);
        add('Projects & git identity', 4, [{ dur: 1, big: 'Resolving your projects and git identity.', sub: 'construct, omniloop, gitgudlab, jarvis', logs: [['step', "==> Reprovisioning 'agent-vm'"], ['info', '    git identity: Dana A. <dana@example.com> (saved)']] }]);
        add('Reach the VM', 4, [{ dur: 1, big: 'Checking that agent-vm answers.', sub: 'SSH on agent-vm.mshome.net:22 · saved root key', logs: [['step', '==> Checking VM reachability (agent-vm.mshome.net, SSH port 22)'], ['step', '==> Checking for a saved root key (re-provision fast path)'], ['info', '    Reusing the saved root SSH private key (VM key left unchanged)']] }]);
        add('Provision the VM', 78, prov({ re: true }));
        add('Configure this PC', 10, hostCfg().filter(o => o.block !== 'cert').concat([{ dur: .8, big: 'Finishing up.', sub: 'No reboot: reprovision leaves the VM running', logs: [['step', '==> Finishing up (no reboot -- reprovision leaves the VM running)']] }]));
        return P;
      }
      if (X === 'export') { add('Export agent config', 100, [{ dur: 3, big: 'Saving the agent config from agent-vm.', sub: 'Logins, MCP servers, settings, shell history', logs: [['step', '==> Exporting agent config from the VM'], ['info', '      %USERPROFILE%\\The-Construct\\backup\\agent-vm-20260923-1412'], ['info', '      Project profiles captured: construct, omniloop, gitgudlab, jarvis']] }]); return P; }
      if (X === 'remove') { add('Remove agent-vm', 100, [{ dur: 3, big: 'Removing agent-vm from this PC.', sub: A.ex.keepVm ? 'Forgetting the instance; the VM stays in Hyper-V' : 'Stopping and deleting the VM and its disk', logs: [["step", "==> Removing the instance 'agent-vm' from this PC"], ['info', '    - stop VM'], ['info', '    - delete VM and 146 GB disk'], ['info', '    - remove Host agent-vm from ~\\.ssh\\config'], ['info', "    Instance 'agent-vm' removed from this PC."]] }]); return P; }
      // reinstall / redownload
      add('Check for unsaved work', 2, [{ dur: 1.2, big: 'Scanning project repos for unsaved work.', sub: 'git status in /root/repos', logs: [['step', '==> Scanning project repos for unsaved work'], ['warn', 'WARNING: construct: 2 uncommitted files, 1 unpushed commit (continuing: you chose Continue anyway)']] }]);
      if (A.ex.saveCfg) add('Back up agent config', 5, [{ dur: 1.6, big: 'Saving the agent config before erasing.', sub: 'Logins, MCP servers, settings', logs: [['step', '==> Exporting agent config from the VM'], ['info', '      %USERPROFILE%\\The-Construct\\backup\\agent-vm-20260923-1412']] }]);
      add('Delete agent-vm', 3, [{ dur: 1.4, big: 'Deleting the old VM.', sub: 'agent-vm and its 146 GB disk', logs: [["step", "==> Deleting existing VM 'agent-vm'"]] }]);
      add('Download Ubuntu', 20, dl(), { skip: X !== 'redownload', skipNote: 'ISO already present' });
      add('Build install ISO', 5, isoOps(), { skip: X !== 'redownload', skipNote: 'ISO already present' });
      add('Create the VM', 3, createOps());
      add('Ubuntu installs itself', 25, ubuntu());
      add('Provision the VM', 30, prov());
      if (A.ex.restore === 'yes') add('Restore agent config', 4, [{ dur: 1.4, big: 'Restoring your saved agent config.', sub: 'Claude, Codex and OpenCode logins; 3 MCP servers', logs: [['step', '==> Restoring saved agent config onto the VM']] }]);
      add('Configure this PC', 3, hostCfg());
      add('Final VM reboot', 3, finalReboot());
      return P;
    }
    PRE.forEach(p => P.push(Object.assign({ ops: [] }, p)));
    if (A.variant === 'remote') {
      add('Connect to buildbox', 2, [{ dur: 1.4, big: 'Connecting to buildbox.', sub: 'Pinned certificate · API token', logs: [['step', '==> Connecting to the host service'], ['info', '    https://buildbox.example.local:7462 · constructd 1.14.2 · certificate pinned (SHA256 5A:3C:…:E8:B6)'], ['info', '    signed in as dana (admin)']] }]);
      add('buildbox creates the VM', 15, [{ dur: 7, mini: true, big: `buildbox is creating ${vmName()} for you.`, sub: f => f < .25 ? 'Host job queued · 1 ahead (reprovision dev-2)' : `Host job running · ${Math.round((f - .25) / .75 * 100)}% · Ubuntu 26.04 LTS from the host's ISO catalog`, logs: [['step', `==> Creating VM '${vmName()}' on buildbox`], ['info', '    job 4f1a queued (1 ahead: reprovision dev-2)'], ['info', '    job 4f1a running'], ['info', `    ${A.ram} GB RAM · 4 vCPU · ${A.disk} GB disk admitted (host line 118 GB)`]] }]);
      add('Ubuntu installs itself', 30, ubuntu());
      add('Provision the VM', 38, prov());
      add('Configure this PC', 5, hostCfg());
      add('Final VM reboot', 4, finalReboot());
      add('Open VS Code', 0, [{ dur: 1, big: `Opening VS Code on ${vmName()}.`, sub: 'Remote-SSH', logs: [['step', `==> Opening VS Code (Remote-SSH: ${vmName()})`]] }]);
      return P;
    }
    add('Admin approval', 0, [{ dur: .8, big: 'Approve the Windows prompt so setup can manage Hyper-V.', sub: 'Setup relaunches itself as administrator. Your answers carry over.', block: 'uac', logs: [['step', '==> Relaunching as Administrator...']] },
      { dur: .4, big: 'Approved.', sub: '', logs: [['info', '    elevated (pid 9312) · answers loaded from %LOCALAPPDATA%\\Construct\\setup-answers.json']] }]);
    add('Turn on Hyper-V', 2, [{ dur: 2, reboot: true, big: 'Turning on Hyper-V, the virtualization built into Windows.', sub: 'Enabling Microsoft-Hyper-V-All and VirtualMachinePlatform', logs: [
      ['step', '==> Checking Hyper-V'], ['info', '    Edition: Windows 11 Pro (26100)'], ['info', '    Hyper-V cannot run virtual machines until it is turned on.'],
      ['dim', '    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -NoRestart'], ['dim', '    Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart'],
      ['warn', 'WARNING: Hyper-V was turned on. A restart is required.']] }]);
    add('Prepare ISO builder', 3, [{ dur: 2.2, big: 'Getting the ISO builder ready.', sub: 'Resolving the .NET runtime for the autoinstall ISO tool', logs: [['step', '==> Preparing the .NET ISO builder'], ['info', '    dotnet 9.0.4 found'], ['dim', '    restoring Construct.IsoBuilder (3 packages)'], ['info', '    Construct.IsoBuilder ready']] }], { skip: INST.sc.isoPresent, skipNote: 'ISO already present' });
    add('Download Ubuntu', 20, dl(), { skip: INST.sc.isoPresent, skipNote: 'ISO already present' });
    add('Build install ISO', 5, isoOps(), { skip: INST.sc.isoPresent, skipNote: 'ISO already present' });
    add('Create the VM', 3, createOps());
    add('Ubuntu installs itself', 25, ubuntu());
    add('Provision the VM', 30, prov());
    add('Configure this PC', 3, hostCfg());
    add('Final VM reboot', 3, finalReboot());
    add('Open VS Code', 0, [{ dur: 1, big: 'Opening VS Code on agent-vm.', sub: 'Remote-SSH', logs: [['step', '==> Opening VS Code (Remote-SSH: agent-vm)']] }]);
    return P;
  }
  function dl() {
    return [{ dur: 13, mini: true, big: 'Downloading Ubuntu Server 26.04 LTS.', sub: f => `${(3.1 * f).toFixed(2)} of 3.1 GB · ${(17.2 + Math.sin(f * 20) * 2.1).toFixed(1)} MB/s`, sub2: 'releases.ubuntu.com · the estimate recalibrates from this speed', logs: [
      ['step', '==> Source Ubuntu Server ISO'], ['dim', '    https://releases.ubuntu.com/26.04/ubuntu-26.04-live-server-amd64.iso'], ['info', '    3.1 GB -> C:\\Users\\dana\\The-Construct\\iso\\'],
      ['dim', '    25%  0.78 GB  17.9 MB/s'], ['dim', '    50%  1.55 GB  18.4 MB/s'], ['dim', '    75%  2.33 GB  16.8 MB/s'], ['dim', '    100% 3.10 GB  17.6 MB/s']] },
    { dur: 1.6, big: 'Checking the download.', sub: 'SHA256 against the Ubuntu release manifest', logs: [['info', '    SHA256 0c4f7e2a…19d21b3c  \x1b[32mOK\x1b[0m (SHA256SUMS)']] }];
  }
  function isoOps() {
    return [{ dur: 3, big: 'Building a hands-off Ubuntu installer.', sub: 'Baked in: user agent, OpenSSH server and a bootstrap key. No questions on the VM console.', logs: [['step', '==> Building autoinstall ISO with the .NET tool'], ['dim', '    user-data: hostname agent-vm, user agent, ssh_pwauth false'], ['info', '    Done. Autoinstall ISO ready at:'], ['info', '    C:\\Users\\dana\\The-Construct\\iso\\agent-vm-autoinstall.iso']] },
      { dur: .8, big: 'Cleaning up.', sub: 'Deleting the 3.1 GB source ISO', logs: [['step', '==> Cleaning up source Ubuntu ISO']] }];
  }
  function createOps() {
    return [{ dur: 2.4, big: 'Creating the virtual machine agent-vm.', sub: `${A.ram} GB RAM · 8 vCPU · ${A.disk} GB disk that grows on demand · Default Switch`, logs: [
      ['step', "==> Creating VM 'agent-vm'"], ['info', `    Memory allocation: ${A.ram} GB (static)`], ['info', `    Virtual hard disk size: ${A.disk} GB (dynamic)`],
      ['info', A.feat.checkpoints ? '    Automatic checkpoints: on' : '    Creating the VM without -AutomaticCheckpoints; set it afterwards from the control panel'], ['step', "==> Starting VM 'agent-vm'"]] }];
  }

  // ---------- engine core ----------
  const pTotal = p => p.ops.reduce((a, o) => a + o.dur, 0) || 1;
  function progress() {
    const tot = INST.phases.filter(p => !p.skip).reduce((a, p) => a + p.w, 0) || 1;
    let acc = 0;
    INST.phases.forEach((p, k) => {
      if (p.skip) return;
      if (k < INST.pi) acc += p.w;
      else if (k === INST.pi && p.ops.length) {
        const done = p.ops.slice(0, INST.oi).reduce((a, o) => a + o.dur, 0) + Math.min(INST.t, (p.ops[INST.oi] || { dur: 0 }).dur);
        acc += p.w * done / pTotal(p);
      }
    });
    return Math.min(100, acc / tot * 100);
  }
  const elapsedText = () => `${Math.floor(Clock.t / 60)} min ${Math.floor(Clock.t % 60)} s`;
  const fmtDur = s => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
  function totalMinutes() {
    if (A.variant === 'existing') return ({ reprovision: 3, addconfig: 3.5, export: .5, remove: .5, reinstall: 11, redownload: 14 })[A.ex.action];
    let m = A.variant === 'remote' ? 11 : 13;
    if (A.feat.t3Patched) m += 15;
    return m;
  }
  function estRange(rem) {
    const m = totalMinutes() * rem / 100;
    if (m < 1) return 'less than a minute';
    const lo = Math.max(1, Math.floor(m * .85)), hi = Math.ceil(m * 1.25);
    return lo === hi ? `about ${lo} min` : `about ${lo}–${hi} min`;
  }
  function currentOp() { const p = INST.phases[INST.pi]; return p && p.ops[INST.oi]; }

  function startInstall(opts = {}) {
    INST.active = true; INST.kind = A.variant === 'existing' ? A.ex.action : 'install';
    INST.phases = buildPhases();
    INST.phases.forEach(p => { p.status = p.pre ? 'done' : p.skip ? 'skipped' : 'pending'; p.t0 = p.t1 = null; p.warnCount = 0; p.ops.forEach(o => { o._resolved = false; o._warned = false; o._rebooted = false; }); });
    INST.pi = 0; INST.oi = 0; INST.t = 0; INST.logged = 0; INST.state = 'running'; INST.block = null; INST.warnings = []; INST.failLine = null;
    Clock.t = 0; Clock.base = 14 * 3600 + 12 * 60 + 3;
    seedLog();
    if (A.variant === 'existing') { log.clear(); log.add('dim', `Construct ${A.ex.action} log -> %LOCALAPPDATA%\\Construct\\logs\\${A.ex.action}-agent-vm-20260923-141203.log`); }
    $('content').classList.add('installing');
    $('foot').innerHTML = '';
    installRailBuilt = false;
    $('bandTitle').innerHTML = A.variant === 'existing' ? `<b>${EX_ACTIONS[A.ex.action].label}</b> · agent-vm` : `<b>Installing</b> · ${vmName()}`;
    buildProgressView();
    renderRail();
    if (!opts.noTimer) startTimer();
  }
  function startTimer() { clearInterval(INST.timer); INST.timer = setInterval(() => { tick(0.1 * INST.speed); }, 100); }
  function advancePhase() {
    INST.pi++; INST.oi = 0; INST.t = 0; INST.logged = 0;
    while (INST.pi < INST.phases.length && (INST.phases[INST.pi].skip || INST.phases[INST.pi].pre)) INST.pi++;
  }
  function tick(dt, fast) {
    if (INST.state !== 'running') { if (!fast) updateLive(); return; }
    let p = INST.phases[INST.pi];
    while (p && (p.pre || p.skip)) { advancePhase(); p = INST.phases[INST.pi]; }
    if (!p) return finish(fast);
    if (p.status === 'pending') { p.status = 'active'; p.t0 = Clock.t; }
    const op = p.ops[INST.oi];
    Clock.t += dt * CLOCK_SCALE;
    INST.t += dt;
    const f = Math.min(1, INST.t / op.dur);
    op._f = f;
    const due = Math.min(op.logs.length, Math.floor(f * op.logs.length) + 1);
    while (INST.logged < due) { const l = op.logs[INST.logged++]; log.add(l[0], l[1]); }
    if (op.block && !op._resolved && f >= .3) return startBlock(op, fast);
    if (op.fail && INST.sc.fail && f >= .5) return failAt(p, op, fast);
    if (f >= 1) {
      if (op.warn && INST.sc.warn && !op._warned) { op._warned = true; p.warnCount++; INST.warnings.push(op.warn); log.add('warn', op.warn.log); }
      if (op.reboot && !op._rebooted) { INST.state = 'reboot'; p.status = 'waiting'; if (!fast) { renderInstallPage(); renderRail(); toast('Restart needed', 'Hyper-V is on. Restart to continue setup.'); armAutoReboot(); } return; }
      INST.oi++; INST.t = 0; INST.logged = 0;
      if (INST.oi >= p.ops.length) { p.status = p.warnCount ? 'warn' : 'done'; p.t1 = Clock.t; advancePhase(); if (INST.pi >= INST.phases.length) return finish(fast); }
    }
    if (!fast) updateLive();
  }
  function startBlock(op, fast) {
    INST.state = 'blocked'; INST.block = op.block;
    INST.phases[INST.pi].status = 'waiting';
    (op.blockLogs || []).forEach(l => log.add(l[0], l[1]));
    if (op.block === 'uac') log.add('info', '    waiting for User Account Control approval');
    if (op.block === 'cert') log.add('info', '    waiting for the Windows Security Warning (Yes / No)');
    if (fast) return;
    showBlock();
  }
  function showBlock() {
    const b = INST.block;
    renderBanner();
    if (b === 'uac') { $('uac').hidden = false; }
    if (b === 'cert') { $('certDlg').hidden = false; }
    renderRail(); updateLive();
    toast({ uac: 'Setup needs your approval', cert: 'Setup needs you', tunnel: 'Sign in to GitHub' }[b], { uac: 'Approve the Windows prompt to continue.', cert: 'Click Yes in the Windows Security Warning.', tunnel: 'Enter code 8F2C-41D7 at github.com/login/device.' }[b]);
    if (INST.auto) { clearTimeout(INST.autoT); INST.autoT = setTimeout(() => { if (INST.state === 'blocked' && INST.block === b) resolveBlock(true, true); }, 6500); }
  }
  function resolveBlock(yes, auto) {
    const op = currentOp(); if (!op) return;
    op._resolved = true;
    $('uac').hidden = true; $('certDlg').hidden = true;
    const b = INST.block;
    INST.block = null; INST.state = 'running';
    INST.phases[INST.pi].status = 'active';
    if (b === 'tunnel') log.add('info', yes ? '    Status: registered and live -- open https://vscode.dev/tunnel/agent-vm in a browser or VS Code Remote Explorer.' : '    tunnel sign-in skipped; re-run provisioning to ask again');
    if (b === 'cert') { if (yes) log.add('info', '    certificate installed (thumbprint 5A3C910E…88E1C0A4)'); else { log.add('warn', 'WARNING: T3 Code CA not trusted; browsers will show a certificate warning for T3 Code'); INST.phases[INST.pi].warnCount++; INST.warnings.push({ title: 'T3 Code certificate not trusted', step: 'Configure this PC', text: 'You clicked No in the Windows Security Warning.', fix: 'Open the Companion › Access & services › Trust T3 certificate.' }); } }
    if (b === 'uac') log.add('info', auto ? '    approved (demo auto-answer)' : '    approved');
    renderBanner(); renderRail(); updateLive();
  }
  function renderBanner() {
    const b = INST.block;
    let h = '';
    if (b === 'uac') h = `<div class="banner" role="alert"><span class="bic">${ICON.shield}</span><div class="bt"><b>Approve the Windows prompt</b>Setup needs administrator rights to turn on Hyper-V and create the VM. If you don't see the prompt, it may be behind this window: look for the flashing shield in the taskbar.</div>
      <div class="bacts"><button class="btn warn" data-b="uacShow">Show the prompt</button></div></div>`;
    if (b === 'cert') h = `<div class="banner" role="alert"><span class="bic">${ICON.lock}</span><div class="bt"><b>Click Yes in the Windows Security Warning</b>Windows asks whether to trust "Construct T3 local CA (agent-vm)". It lets your browser open T3 Code over HTTPS without warnings. Thumbprint 5A3C910E…88E1C0A4.</div>
      <div class="bacts"><button class="btn warn" data-b="certShow">Show the dialog</button></div></div>`;
    if (b === 'tunnel') h = `<div class="banner wrap" role="alert"><span class="bic">${ICON.key}</span><div class="bt"><b>Sign in to GitHub for the VS Code tunnel</b>Open github.com/login/device and enter the code. Setup continues by itself once you're signed in.</div>
      <div class="bacts"><span class="code" aria-label="Device code">8F2C-41D7</span><button class="btn sm" data-b="copyCode">${ICON.copy} Copy</button><button class="btn sm warn" data-b="openDevice">${ICON.ext} Open github.com/login/device</button><button class="btn sm subtle" data-b="skipTunnel">Skip</button></div></div>`;
    $('banner').innerHTML = h;
  }
  $('banner').addEventListener('click', e => {
    const b = e.target.closest('[data-b]'); if (!b) return;
    const a = b.dataset.b;
    if (a === 'uacShow') $('uac').hidden = false;
    if (a === 'certShow') $('certDlg').hidden = false;
    if (a === 'copyCode') { b.innerHTML = ICON.check + ' Copied'; }
    if (a === 'openDevice') { log.add('dim', '    opened https://github.com/login/device in the default browser'); b.innerHTML = '<span class="spinner"></span> Waiting for GitHub…'; setTimeout(() => INST.block === 'tunnel' && resolveBlock(true), 2200); }
    if (a === 'skipTunnel') { INST.warnings.push({ title: 'VS Code tunnel is not signed in', step: 'Provision step 30/31 · Setting up VS Code server / serve-web / tunnel', text: 'The GitHub device sign-in was skipped.', fix: 'Reprovision to sign in, or run  code tunnel  on the VM.' }); INST.phases[INST.pi].warnCount++; log.add('warn', 'WARNING: VS Code tunnel sign-in skipped'); resolveBlock(false); }
  });
  document.querySelector('.uac').addEventListener('click', e => {
    const b = e.target.closest('[data-uac]'); if (!b) return;
    if (b.dataset.uac === 'yes') resolveBlock(true);
    else { $('uac').hidden = true; log.add('warn', 'WARNING: administrator approval was declined'); toast('Approval declined', 'Setup is waiting. Click "Show the prompt" to try again.'); }
  });
  $('certDlg').addEventListener('click', e => { const b = e.target.closest('[data-cert]'); if (b) resolveBlock(b.dataset.cert === 'yes'); });

  function failAt(p, op, fast) {
    INST.state = 'failed'; p.status = 'failed';
    op.failLogs.forEach(l => { const ln = log.add(l[0], l[1]); if (!INST.failLine && l[0] === 'err') INST.failLine = ln; });
    INST.failOp = op; INST.failPhase = p;
    if (fast) return;
    afterFail();
  }
  function afterFail() {
    renderInstallPage(); renderRail(); setLogOpen(true);
    setTimeout(() => log.focusLine(INST.failLine), 320);
    toast('Setup stopped', `${INST.failPhase.name} failed. Retry picks up from there.`);
  }
  function retry() {
    const p = INST.failPhase;
    INST.sc.fail = false;
    INST.oi = 0; INST.t = 0; INST.logged = 0; p.status = 'active';
    p.ops.forEach(o => { o._resolved = false; });
    INST.failLine = null; INST.state = 'running';
    log.add('step', `==> Retrying from '${p.name}' (attempt 2)`);
    log.add('dim', '    everything before this phase is kept');
    renderInstallPage(); renderRail(); setLogOpen(false); log.follow = true; log.scrollBottom();
  }
  function finish(fast) {
    INST.state = 'done'; clearInterval(INST.timer);
    INST.pi = INST.phases.length;
    const w = INST.warnings.length;
    log.add(w ? 'warn' : 'ok', w ? `Install finished with ${w} warning${w > 1 ? 's' : ''} in ${elapsedText()}.` : `${A.variant === 'existing' ? 'Done' : 'Install complete'} in ${elapsedText()}.`);
    if (fast) return;
    renderInstallPage(); renderRail();
    toast(w ? `Installed with ${w} warnings` : `${vmName()} is ready`, w ? 'Open setup to see what needs attention.' : 'Open it in VS Code or T3 Code.');
  }
  function armAutoReboot() {
    clearTimeout(INST.autoT);
    if (INST.auto) INST.autoT = setTimeout(() => { if (INST.state === 'reboot' && !INST.laterChosen) doReboot(true); }, 6500);
  }
  function doReboot(now) {
    const p = INST.phases[INST.pi];
    if (!now) { INST.laterChosen = true; renderInstallPage(); return; }
    const ov = $('restartOv'); ov.classList.add('show'); $('restartText').textContent = 'Restarting PC-1…';
    setTimeout(() => { $('restartText').textContent = 'Signing in… setup reopens by itself'; }, 1100);
    setTimeout(() => {
      ov.classList.remove('show');
      p.ops[INST.oi]._rebooted = true; INST.laterChosen = false;
      INST.state = 'running';
      p.status = 'active';
      log.add('dim', '---- PC-1 restarted · setup relaunched from RunOnce, answers restored ----');
      log.add('step', '==> Resuming setup');
      renderInstallPage(); renderRail();
      toast('Welcome back', 'Setup is continuing where it left off.');
    }, 2300);
  }

  // ---------- install rail ----------
  function buildInstallRail() {
    const rail = $('rail');
    let h = '';
    if (A.variant !== 'existing') h += `<h6>Setup</h6><div class="answers-done">${ICON.check}<span>${flow().length} pages answered</span><button class="link" data-a="viewAnswers">View</button></div>`;
    const visible = INST.phases.length;
    h += `<h6>${A.variant === 'existing' ? EX_ACTIONS[A.ex.action].label : 'Install'}<span class="cnt" id="phCnt"></span></h6>`;
    INST.phases.forEach((p, k) => {
      h += `<div class="item ph" data-k="${k}"><span class="mk"></span><span class="lbl">${esc(p.name)}<small></small></span><span class="meta"></span></div>`;
    });
    h += `<div class="foot-note">The full log is written to disk from the first second, so it survives a crash or restart.</div>`;
    rail.innerHTML = h;
    installRailBuilt = true;
  }
  function updateInstallRail() {
    const rail = $('rail');
    let doneN = 0, total = 0;
    INST.phases.forEach((p, k) => {
      const el = rail.querySelector(`.ph[data-k="${k}"]`); if (!el) return;
      if (!p.skip) total++;
      if (p.status === 'done' || p.status === 'warn') doneN++;
      const cls = 'item ph ' + p.status;
      if (el.className !== cls) el.className = cls;
      const mk = el.querySelector('.mk');
      const mkHtml = { done: ICON.check, warn: '!', failed: ICON.x, skipped: '', waiting: '!', active: '', pending: String(k + 1 - INST.phases.slice(0, k).filter(x => x.skip).length) }[p.status] || '';
      if (mk.dataset.s !== p.status) { mk.innerHTML = p.status === 'skipped' ? ICON.minus : mkHtml; mk.dataset.s = p.status; }
      const small = el.querySelector('small');
      let note = '';
      if (p.skip) note = p.skipNote || 'skipped';
      else if (p.status === 'waiting') note = INST.state === 'reboot' ? 'restart needed' : 'needs you';
      else if (p.status === 'failed') note = 'failed · retry from here';
      else if (p.status === 'warn') note = `${p.warnCount} warning${p.warnCount > 1 ? 's' : ''}`;
      else if (p.status === 'active') { const op = currentOp(); note = op && op.step ? `step ${op.step} of 31` : ''; }
      if (small.textContent !== note) small.textContent = note;
      const meta = el.querySelector('.meta');
      let m = '';
      if (p.pre) m = p.meta;
      else if ((p.status === 'done' || p.status === 'warn') && p.t1 != null) m = fmtDur(p.t1 - p.t0);
      else if (p.status === 'active' || p.status === 'waiting') m = fmtDur(Clock.t - (p.t0 || 0));
      if (meta.textContent !== m) meta.textContent = m;
    });
    const c = $('phCnt'); if (c) c.textContent = `${doneN}/${total}`;
  }
  $('rail').addEventListener('click', e => {
    if (e.target.closest('[data-a="viewAnswers"]')) toast('Answers are locked while installing', cmdLine().replace(/\n\s*/g, ' '));
  });

  // ---------- install page ----------
  const TIPS = [
    { icon: 'box', t: 'The VM is disposable', p: 'Break it, reinstall it. Projects live in git and the agent config is backed up before every reinstall.', when: () => true },
    { icon: 'shield', t: 'Agents run as root, inside the VM', p: 'Claude Code, Codex and OpenCode can install anything and run unattended. Nothing they do touches Windows.', when: () => true },
    { icon: 'retry', t: 'Your setup follows you', p: 'Project profiles sync through your config repo, so the next VM clones the same repos and SDKs by itself.', when: () => true },
    { icon: 'tray', t: 'The Companion lives in your tray', p: 'Start or save the VM, read agent notifications (construct notify) and open forwarded ports (construct expose) in one click.', when: () => A.feat.companion },
    { icon: 'globe', t: 'T3 Code: every agent in one window', p: "Threads from all agents in one web GUI, forwarded to this PC over HTTPS.", when: () => A.feat.t3 },
    { icon: 'spark', t: 'Talk to your agents', p: "Mic passthrough streams this PC's microphone into the VM, so you can dictate prompts.", when: () => A.feat.mic },
    { icon: 'server', t: 'Share this PC later', p: 'Got a spare workstation? The Construct can turn it into a shared host for your team.', when: () => A.variant === 'local' }
  ];
  let tipIdx = 0, tipTimer = null;
  function tipsFor() { return TIPS.filter(t => t.when()); }
  function renderTip(dir) {
    const list = tipsFor(); const el = $('tips'); if (!el) return;
    tipIdx = (tipIdx + list.length) % list.length;
    const t = list[tipIdx];
    el.innerHTML = `<span class="tic">${ICON[t.icon]}</span><div class="tt tip-fade"><div class="tk">While you wait</div><b>${t.t}</b><p>${t.p}</p></div>
      <div class="tnav"><div class="arrows"><button data-tip="-1" aria-label="Previous tip">${ICON.chevRight.replace('<svg', '<svg style="transform:rotate(180deg)"')}</button><button data-tip="1" aria-label="Next tip">${ICON.chevRight}</button></div>
      <div class="dots">${list.map((_, k) => `<button aria-label="Tip ${k + 1}" data-tipk="${k}" aria-current="${k === tipIdx}"></button>`).join('')}</div></div>`;
  }
  function startTips() { clearInterval(tipTimer); tipTimer = setInterval(() => { tipIdx++; renderTip(); }, 9000); }

  function buildProgressView() { renderInstallPage(); }
  function renderInstallPage() {
    const s = INST.state;
    const pg = $('page');
    $('banner').innerHTML = s === 'blocked' ? $('banner').innerHTML : '';
    if (s === 'blocked') renderBanner();
    if (s === 'reboot') { pg.innerHTML = rebootView(); return postTaskbar(); }
    if (s === 'failed') { pg.innerHTML = failView(); return postTaskbar(); }
    if (s === 'done') { pg.innerHTML = doneView(); return postTaskbar(); }
    const ticks = []; let acc = 0; const tot = INST.phases.filter(p => !p.skip).reduce((a, p) => a + p.w, 0);
    INST.phases.forEach(p => { if (p.skip || !p.w) return; acc += p.w; if (acc < tot && p.w >= 3) ticks.push(acc / tot * 100); });
    const title = A.variant === 'existing' ? `${EX_ACTIONS[A.ex.action].label}${EX_ACTIONS[A.ex.action].danger ? '' : ''} · agent-vm` : `Installing The Construct`;
    pg.innerHTML = `<div class="inst-top"><div class="ttl"><h1>${title}</h1><div class="sub">${A.variant === 'existing' ? 'agent-vm on this PC · saved feature choices · 16 GB · 4 projects' : `${A.variant === 'remote' ? `${esc(A.remote.instance)} on buildbox` : 'agent-vm on this PC'} · ${presetLabel()} · ${A.ram} GB · ${selProjects().length} projects`}</div></div>
        <div class="pctbig" id="pctBig">0<small>%</small></div></div>
      <div class="inst-bar"><div class="pbar" id="ibar" role="progressbar" aria-label="Overall progress" aria-valuemin="0" aria-valuemax="100"><div class="fill"></div>${ticks.map(t => `<div class="tick" style="left:calc(${t}% - 1px)"></div>`).join('')}</div></div>
      <div class="inst-meta"><span id="phaseOf"></span><span class="eta" id="eta"></span></div>
      <div class="now" id="now"><div class="live" id="live"><i></i><span>Now</span></div><div class="big" id="big"></div>
        <div class="detail"><span id="sub"></span><span class="r" id="subR"></span></div><div class="pbar mini" id="mini" hidden><div class="fill"></div></div><div class="sub2" id="sub2"></div></div>
      <div class="tips" id="tips"></div>`;
    renderTip(); startTips();
    updateLive();
  }
  function updateLive() {
    if (!INST.active) return;
    updateInstallRail();
    const s = INST.state;
    const pct = progress();
    postTaskbar(pct);
    if (s !== 'running' && s !== 'blocked') return;
    const big = $('big'); if (!big) return;
    const p = INST.phases[INST.pi]; const op = currentOp();
    $('pctBig').innerHTML = `${Math.floor(pct)}<small>%</small>`;
    const bar = $('ibar'); bar.querySelector('.fill').style.width = pct.toFixed(2) + '%'; bar.setAttribute('aria-valuenow', Math.floor(pct));
    bar.classList.toggle('paused', s === 'blocked');
    const vis = INST.phases.filter(x => !x.skip && !x.pre);
    const idx = vis.indexOf(p) + 1;
    $('phaseOf').textContent = p ? `Phase ${idx} of ${vis.length} · ${p.name}` : '';
    const eta = $('eta');
    eta.textContent = s === 'blocked' ? 'Paused · waiting for you' : `${estRange(100 - pct)} left`;
    eta.classList.toggle('paused', s === 'blocked');
    const live = $('live');
    live.className = 'live' + (s === 'blocked' ? ' wait' : '');
    live.querySelector('span').textContent = s === 'blocked' ? 'Waiting for you' : 'Now';
    if (!op) return;
    const f = op._f || 0;
    const bigTxt = s === 'blocked' ? ({ uac: 'Approve the Windows prompt so setup can manage Hyper-V.', cert: 'Click Yes in the Windows Security Warning.', tunnel: 'Sign in to GitHub to register the VS Code tunnel.' }[INST.block]) : op.big;
    if (big.textContent !== bigTxt) big.textContent = bigTxt;
    $('sub').textContent = typeof op.sub === 'function' ? op.sub(f) : (op.sub || '');
    $('subR').textContent = p.t0 != null ? `${fmtDur(Clock.t - p.t0)} in this phase` : '';
    const mini = $('mini'); mini.hidden = !op.mini; if (op.mini) mini.querySelector('.fill').style.width = (f * 100).toFixed(1) + '%';
    $('sub2').textContent = op.sub2 || (op.step ? `${fmtDur(Clock.t)} since you pressed Install` : '');
  }
  let lastTb = -1;
  function postTaskbar(pct) {
    if (pct == null) pct = progress();
    const s = INST.state;
    const st = s === 'failed' ? 'error' : s === 'blocked' || s === 'reboot' ? 'paused' : s === 'done' ? 'done' : 'normal';
    const key = Math.floor(pct) + st;
    if (key === lastTb) return; lastTb = key;
    Bridge.post({ taskbar: { p: pct / 100, state: st, label: 'The Construct Setup' } });
  }

  function rebootView() {
    return `<div class="reboot result"><div class="result-head"><span class="result-icon info">${ICON.restart}</span><div>
      <div class="eyebrow">Phase ${INST.phases.filter(x => !x.skip && !x.pre).indexOf(INST.phases[INST.pi]) + 1} of ${INST.phases.filter(x => !x.skip && !x.pre).length} · Turn on Hyper-V</div><h1>${INST.laterChosen ? 'Setup continues after your next restart' : 'Restart to finish turning on Hyper-V'}</h1>
      <p class="lead" style="margin-bottom:0">${INST.laterChosen ? 'Nothing else happens until then. You can close this window; after you sign in, setup reopens and picks up with <b>Prepare ISO builder</b>.' : 'Windows loads the Hyper-V hypervisor only at boot. Save your work in other apps first.'}</p></div></div>
      <div class="why">
        <div><span class="n">1</span><b>Windows restarts</b>Takes a minute or two. Hyper-V is switched on during boot.</div>
        <div><span class="n">2</span><b>Setup reopens by itself</b>After you sign in, from a one-time startup entry. No need to rerun the command.</div>
        <div><span class="n">3</span><b>Your answers are kept</b>It continues with the Ubuntu download: ${estRange(100 - progress()).replace('about', 'about')} to go.</div></div>
      <div class="acts-row"><button class="btn lg primary" data-r="now">${ICON.restart} Restart now</button>${INST.laterChosen ? '<button class="btn lg" data-r="close">Close setup</button>' : '<button class="btn lg" data-r="later">Later</button>'}<span class="sp"></span><span class="muted" style="font-size:12px">${ICON.info.replace('<svg', '<svg style="width:13px;height:13px;vertical-align:-2px"')} RunOnce: ConstructSetupResume</span></div></div>`;
  }
  function failView() {
    const p = INST.failPhase, op = INST.failOp;
    return `<div class="result"><div class="result-head"><span class="result-icon err">${ICON.x}</span><div>
      <div class="eyebrow" style="color:var(--danger)">Setup stopped · ${Math.floor(progress())}% done</div><h1>${esc(p.name)} failed</h1>
      <p class="lead" style="margin-bottom:0">${esc(op.sub)} (critical). Everything before this phase is finished and kept.</p></div></div>
      <div class="errbox"><span class="d">[vm] </span>E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 1423 (unattended-upgr)
<span class="d">ERROR: critical step failed: Running core host bootstrap (exit 100)</span></div>
      <div class="callout info"><span class="ic">${ICON.info}</span><div><b>What's going on:</b> on first boot Ubuntu installs its security updates in the background and holds the package lock. That usually clears in 2–5 minutes. Retrying runs <b>${esc(p.name)}</b> again; the VM and the ISO are kept.</div></div>
      <div class="acts-row"><button class="btn primary" data-f="retry">${ICON.retry} Retry from "${esc(p.name)}"</button><button class="btn" data-f="copy">${ICON.copy} Copy log</button><button class="btn" data-f="folder">${ICON.folder} Open log folder</button><button class="btn subtle" data-f="prompt">${ICON.spark} Copy a diagnosis prompt</button><span class="sp"></span><button class="link" data-f="docs">Troubleshooting guide ${ICON.ext.replace('<svg', '<svg style="width:11px;height:11px;vertical-align:-1px"')}</button><button class="btn subtle" data-f="close">Close</button></div></div>`;
  }
  function doneView() {
    const w = INST.warnings;
    const ex = A.variant === 'existing';
    const vm = vmName();
    if (ex && A.ex.action === 'export') return simpleDone('Agent config exported', 'Saved to %USERPROFILE%\\The-Construct\\backup\\agent-vm-20260923-1412. Reinstall offers to restore it.', `<button class="btn" data-f="folder">${ICON.folder} Open backup folder</button>`);
    if (ex && A.ex.action === 'remove') return simpleDone('agent-vm was removed', A.ex.keepVm ? 'This PC forgot agent-vm. The VM is still in Hyper-V Manager.' : 'The VM, its disk and child VM win-test are gone. dev-2 on buildbox is untouched.', '');
    const tiles = [`<button class="open-tile primary" data-f="vscode"><span class="oi">${ICON.vscode}</span><b>Open in VS Code</b><span>Remote-SSH · ${vm}</span></button>`];
    if (A.feat.t3) tiles.push(`<button class="open-tile" data-f="t3"><span class="oi">${ICON.globe}</span><b>Open T3 Code</b><span>https://localhost:3773</span></button>`);
    if (A.feat.serveWeb) tiles.push(`<button class="open-tile" data-f="web"><span class="oi">${ICON.globe}</span><b>VS Code in the browser</b><span>http://${vm}.mshome.net:8000</span></button>`);
    if (tiles.length < 3) tiles.push(`<button class="open-tile" data-f="panel"><span class="oi">${LOGO.replace('class="logo"', 'style="width:18px;height:18px"')}</span><b>Open the control panel</b><span>Companion › ${vm}</span></button>`);
    const warnHtml = w.length ? `<div class="warn-list">${w.map(x => `<details class="wl"><summary><span class="ic">${ICON.warn}</span><span><b>${esc(x.title)}</b> <span class="muted" style="font-size:12px">· ${esc(x.step)}</span></span><span class="chev">${ICON.chevDown}</span></summary>
      <div class="wb">${esc(x.text)}<div style="margin-top:6px"><b>Fix:</b> ${esc(x.fix)}</div><div class="acts-row" style="margin-top:8px"><button class="btn sm" data-f="showlog">Show in log</button></div></div></details>`).join('')}</div>` : '';
    const tray = A.feat.companion ? `<div class="tray-tip"><div class="tray-mini"><span class="cx">${LOGO.replace('class="logo"', '')}</span><i></i><i></i><span class="clk">14:25<br>23/09/2026</span></div>
      <div class="tt"><b>The Construct lives in your tray</b>Click the cube to start or save the VM, see what your agents are doing and open forwarded ports. Right-click for more. <button class="link" data-f="traytip">Show me</button></div></div>` : '';
    return `<div class="result"><div class="result-head"><span class="result-icon ${w.length ? 'warn' : 'ok'}">${w.length ? ICON.warn : ICON.check}</span><div>
      <div class="eyebrow" ${w.length ? 'style="color:var(--warn)"' : ''}>${w.length ? `Finished with ${w.length} warning${w.length > 1 ? 's' : ''}` : ex ? `Done in ${elapsedText()}` : `Installed in ${elapsedText()}`}</div>
      <h1>${ex ? `${vm} is up to date` : `${vm} is ready`}</h1>
      <p class="lead" style="margin-bottom:0">${ex ? 'Provisioned at 3a91f0e. The VM kept running; agents were not interrupted.' : `Ubuntu 26.04 LTS · ${A.ram} GB · ${A.disk} GB disk · ${selProjects().length} projects cloned${w.length ? '. Everything works; two optional steps need a look.' : '. Your agents are waiting.'}`}</p></div></div>
      ${warnHtml}
      <div class="open-tiles">${tiles.join('')}</div>
      ${tray}
      <div class="acts-row"><label style="font-size:12.5px;display:flex;gap:6px;align-items:center"><input type="checkbox" checked style="accent-color:var(--accent-700)"> Open VS Code when I click Finish</label><span class="sp"></span><button class="btn" data-f="copy">${ICON.copy} Copy log</button><button class="btn primary" data-f="finish">Finish</button></div></div>`;
  }
  function simpleDone(t, d, extra) {
    return `<div class="result"><div class="result-head"><span class="result-icon ok">${ICON.check}</span><div><div class="eyebrow">Done</div><h1>${t}</h1><p class="lead">${d}</p></div></div>
      <div class="acts-row">${extra}<span class="sp"></span><button class="btn primary" data-f="finish">Close</button></div></div>`;
  }
  $('page').addEventListener('click', e => {
    const tb = e.target.closest('[data-tip]'); if (tb) { tipIdx += +tb.dataset.tip; renderTip(); startTips(); return; }
    const tk = e.target.closest('[data-tipk]'); if (tk) { tipIdx = +tk.dataset.tipk; renderTip(); startTips(); return; }
    const r = e.target.closest('[data-r]'); if (r) { if (r.dataset.r === 'now') doReboot(true); if (r.dataset.r === 'later') doReboot(false); if (r.dataset.r === 'close') toast('Setup closed', 'It reopens after your next sign-in.'); return; }
    const f = e.target.closest('[data-f]'); if (!f) return;
    const a = f.dataset.f;
    if (a === 'retry') retry();
    if (a === 'copy') log.copy();
    if (a === 'folder') { setLogOpen(true); log.toast('Opened %LOCALAPPDATA%\\Construct\\logs in Explorer'); }
    if (a === 'prompt') toast('Copied', 'Diagnosis prompt for your AI agent');
    if (a === 'docs') toast('Opening', 'docs/troubleshooting.md#provision-apt-lock');
    if (a === 'showlog') { setLogOpen(true); const ln = log.lines.find(l => l.kind === 'warn' && /gitgudlab|MCP|tunnel|T3/.test(l.text)); log.focusLine(ln); }
    if (a === 'vscode') toast('Opening VS Code', `Remote-SSH: ${vmName()}`);
    if (a === 't3') toast('Opening T3 Code', 'https://localhost:3773');
    if (a === 'web') toast('Opening', `http://${vmName()}.mshome.net:8000`);
    if (a === 'panel') toast('Opening', 'Construct control panel');
    if (a === 'traytip') { Bridge.post({ trayPulse: true }); toast('Look at the taskbar', 'The green cube next to the clock'); }
    if (a === 'finish' || a === 'close') toast('Setup closed', a === 'finish' ? 'VS Code is opening agent-vm' : 'Retry any time from the Companion');
  });

  // ---------- log drawer / pop-out ----------
  function setLogOpen(on) {
    const c = $('content');
    if (c.classList.contains('log-popped')) return;
    c.classList.toggle('log-open', on);
    $('detailsBtn').setAttribute('aria-expanded', String(on));
    if (on) setTimeout(() => { if (log.follow) log.scrollBottom(); }, 280);
  }
  $('detailsBtn').addEventListener('click', () => setLogOpen(!$('content').classList.contains('log-open')));
  (function resizer() {
    const grip = $('grip'); let y0, h0;
    grip.addEventListener('pointerdown', e => {
      y0 = e.clientY; h0 = $('drawer').getBoundingClientRect().height; grip.setPointerCapture(e.pointerId); $('content').classList.add('resizing');
      const mv = ev => { const h = Math.max(120, Math.min(440, h0 + (y0 - ev.clientY))); $('content').style.setProperty('--dh', h + 'px'); };
      const up = () => { grip.removeEventListener('pointermove', mv); grip.removeEventListener('pointerup', up); $('content').classList.remove('resizing'); };
      grip.addEventListener('pointermove', mv); grip.addEventListener('pointerup', up);
    });
  })();
  function popOut(on) {
    const c = $('content');
    if (on) {
      $('logWinBody').appendChild($('log'));
      $('logWin').hidden = false;
      c.classList.remove('log-open'); c.classList.add('log-popped');
      Bridge.post({ popout: true });
    } else {
      $('logHost').appendChild($('log'));
      $('logWin').hidden = true;
      c.classList.remove('log-popped');
      setLogOpen(true);
    }
    setTimeout(() => log.follow && log.scrollBottom(), 50);
  }
  $('popBtn').addEventListener('click', () => { if (!INST.active) return; popOut(true); });
  $('dockBack').addEventListener('click', () => popOut(false));
  $('logWinDock').addEventListener('click', () => popOut(false));
  $('logWinClose').addEventListener('click', () => popOut(false));
  (function dragLogWin() {
    const bar = document.querySelector('.logwin-bar'); const w = $('logWin');
    bar.addEventListener('pointerdown', e => {
      if (e.target.closest('button')) return;
      const r = w.getBoundingClientRect(); const dx = e.clientX - r.left, dy = e.clientY - r.top;
      bar.setPointerCapture(e.pointerId);
      const mv = ev => { w.style.left = (ev.clientX - dx) + 'px'; w.style.top = (ev.clientY - dy) + 'px'; w.style.right = 'auto'; w.style.bottom = 'auto'; };
      const up = () => { bar.removeEventListener('pointermove', mv); bar.removeEventListener('pointerup', up); };
      bar.addEventListener('pointermove', mv); bar.addEventListener('pointerup', up);
    });
  })();

  // ======================================================================
  // Scenes (jump straight to a moment) + demo controls
  // ======================================================================
  function fastTo(cond, opts = {}) {
    let guard = 0;
    while (guard++ < 20000) {
      if (cond()) break;
      if (INST.state === 'blocked') { const op = currentOp(); op._resolved = true; INST.state = 'running'; INST.block = null; INST.phases[INST.pi].status = 'active'; log.add('info', '    approved'); continue; }
      if (INST.state === 'reboot') { const p = INST.phases[INST.pi]; p.ops[INST.oi]._rebooted = true; INST.state = 'running'; p.status = 'active'; log.add('dim', '---- PC-1 restarted · setup relaunched from RunOnce, answers restored ----'); continue; }
      if (INST.state !== 'running') break;
      tick(0.2, true);
    }
  }
  function scene(name) {
    clearInterval(INST.timer); clearTimeout(INST.autoT);
    $('uac').hidden = true; $('certDlg').hidden = true; $('banner').innerHTML = '';
    if ($('content').classList.contains('log-popped')) popOut(false);
    $('content').classList.remove('log-open');
    INST.sc = { fail: name === 'failure', warn: name === 'warnings', isoPresent: name === 'isopresent' };
    if (name === 'tunnel' && A.variant !== 'existing') { A.preset = 'custom'; A.feat.tunnel = true; }
    if (name === 'cert' && A.variant !== 'existing' && !A.feat.t3) { A.preset = 'custom'; A.feat.t3 = true; A.feat.t3Https = true; }
    if (A.variant === 'existing' && name !== 'run') A.ex.action = A.ex.action || 'reprovision';
    INST.laterChosen = false;
    startInstall({ noTimer: true });
    const opIs = (fn) => () => { const op = currentOp(); return op && fn(op, INST.phases[INST.pi]); };
    const C = {
      run: () => true,
      isopresent: () => true,
      uac: () => INST.state === 'blocked' && INST.block === 'uac',
      reboot: () => INST.state === 'reboot',
      download: opIs((op, p) => p.name === 'Download Ubuntu' && op.mini && (op._f || 0) > .42),
      ubuntu: opIs((op, p) => p.name === 'Ubuntu installs itself' && op.mini && (op._f || 0) > .55),
      provision: opIs(op => op.step === 11 && (op._f || 0) > .5),
      popout: opIs(op => op.step === 14 && (op._f || 0) > .3),
      tunnel: () => INST.state === 'blocked' && INST.block === 'tunnel',
      cert: () => INST.state === 'blocked' && INST.block === 'cert',
      failure: () => INST.state === 'failed',
      warnings: () => INST.state === 'done',
      success: () => INST.state === 'done'
    }[name] || (() => true);
    const stopAtBlock = ['uac', 'tunnel', 'cert'].includes(name);
    fastTo(() => C() || (stopAtBlock ? false : false));
    // render final state
    if (INST.state === 'blocked') { renderInstallPage(); showBlock(); }
    else if (INST.state === 'failed') { renderInstallPage(); afterFail(); }
    else renderInstallPage();
    if (INST.state === 'reboot') armAutoReboot();
    renderRail();
    log.follow = true; log.scrollBottom();
    if (INST.state !== 'done' && INST.state !== 'failed') startTimer();
    if (name === 'popout') popOut(true);
  }

  function renderDemo() {
    const d = $('demo');
    const sp = s => `<button class="chip" data-speed="${s}" aria-pressed="${INST.speed === s}">${s}×</button>`;
    d.innerHTML = `<span class="lbl">Variant</span>
      <button class="chip" data-var="local" aria-pressed="${A.variant === 'local'}">Local</button><button class="chip" data-var="remote" aria-pressed="${A.variant === 'remote'}">Remote</button><button class="chip" data-var="existing" aria-pressed="${A.variant === 'existing'}">Existing VM</button>
      <span class="lbl">Jump</span>
      ${['run', 'uac', 'reboot', 'download', 'ubuntu', 'provision', 'tunnel', 'cert', 'failure', 'warnings', 'success'].map(s => `<button class="chip" data-scene="${s}">${s}</button>`).join('')}
      <span class="lbl">Speed</span>${sp(1)}${sp(10)}
      <button class="chip" data-auto aria-pressed="${INST.auto}">auto-answer prompts</button>`;
  }
  $('demo').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.var) resetWizard(b.dataset.var);
    if (b.dataset.scene) scene(b.dataset.scene);
    if (b.dataset.speed) INST.speed = +b.dataset.speed;
    if (b.dataset.auto != null) INST.auto = !INST.auto;
    renderDemo();
  });
  function resetWizard(variant, page) {
    clearInterval(INST.timer); clearTimeout(INST.autoT);
    INST.active = false; INST.state = 'idle';
    $('uac').hidden = true; $('certDlg').hidden = true; $('banner').innerHTML = '';
    if ($('content').classList.contains('log-popped')) popOut(false);
    $('content').classList.remove('installing', 'log-open');
    A.variant = variant;
    A.ram = variant === 'remote' ? 12 : recRam;
    W.maxIdx = 0;
    const f = flow();
    if (page) W.maxIdx = Math.max(0, f.indexOf(page));
    if (variant === 'remote' && page && f.indexOf(page) > f.indexOf('connect')) { A.remote.conn = 'ok'; A.remote.pin = 'yes'; A.remote.tokStatus = 'ok'; A.remote.token = 'cst_live_7Gd2kQ'; }
    if (page && f.indexOf(page) > f.indexOf('projects') && A.cred.status !== 'ok') { A.cred.token = 'glpat-9xk2Hq7TzLmN0aB'; A.cred.status = 'ok'; }
    go(page && f.includes(page) ? page : f[0]);
    Bridge.post({ taskbar: null });
  }
  Bridge.listen(m => {
    if (m.speed) INST.speed = m.speed;
    if (m.auto != null) INST.auto = m.auto;
    if (m.variant) {
      if (m.preset) applyPreset(m.preset);
      if (m.tunnel) { A.preset = 'custom'; A.feat.tunnel = true; }
      if (m.action) A.ex.action = m.action;
      if (m.scene) { A.variant = m.variant; if (m.variant === 'remote') { A.remote.conn = 'ok'; A.remote.pin = 'yes'; A.remote.tokStatus = 'ok'; A.ram = 12; } A.cred.status = 'ok'; scene(m.scene); }
      else resetWizard(m.variant, m.page);
    } else if (m.scene) scene(m.scene);
    if (m.check) { /* reload handled by index */ }
    renderDemo();
  });

  // ---------- boot ----------
  const startScene = Q.get('scene');
  if (Q.get('speed')) INST.speed = +Q.get('speed');
  if (Q.get('auto') === '0') INST.auto = false;
  if (Q.get('action')) A.ex.action = Q.get('action');
  if (startScene) {
    A.cred.status = 'ok'; A.cred.token = 'glpat-9xk2Hq7TzLmN0aB';
    if (A.variant === 'remote') { A.remote.conn = 'ok'; A.remote.pin = 'yes'; A.remote.tokStatus = 'ok'; A.ram = 12; }
    W.page = flow()[flow().length - 1];
    scene(startScene);
  } else {
    resetWizard(A.variant, Q.get('page'));
  }
  renderDemo();
})();
