# Construct control panel — architecture & roadmap

Developer/design notes for the `extension/` VS Code extension. This is the source
of truth for *why* things are shaped the way they are and *what is left to build*,
so work can resume without re-deriving the design.

## Goal

One VS Code panel to operate a Construct agent VM: live status, coding-agent
versions + updates, Construct self-update, project profiles, token usage & cost,
lifecycle (reprovision / reinstall / redownload / export config), **microphone
passthrough** so voice input works over Remote-SSH, the client-side **port forwards**
`construct expose` asks for, and the **idle policy** of a service-hosted VM.

Since the modular/remote work (`docs/plans/modular-remote-architecture.md`) the panel
drives **one instance per window** rather than one hardcoded VM, over a **driver**
dispatch rather than direct Hyper-V calls. Both are additive: with no instance registry
there is exactly one implicit instance holding today's literals, the driver is
`hyperv-local`, and no surface changes. The three sections that carry that design are
[Instances](#instances), [Remote hosts](#remote-hosts-hyperv-remote) and
[Forwards](#forwards-construct-expose).

## Architecture

- **UI extension** (`extensionKind: ["ui"]`). Runs on the user's local machine even
  when the window is attached to the VM over Remote-SSH. That single vantage point
  reaches both sides:
  - **host** — PowerShell lifecycle scripts in `%LOCALAPPDATA%\The-Construct\…`,
    the local microphone, and the local ports opened for `construct expose`;
  - **VM** — status/versions/usage gathered over `ssh`, with the ACTIVE INSTANCE's
    host/alias/key/port (`agent-vm` + `agent_vm_ed25519` on port 22 by default).
- **No build step.** Plain JS. The installer packages this folder into a `.vsix`
  (`Build-ControlPanelVsix`, no vsce/Node) and installs it with `code --install-extension`
  (a bare folder copy into `.vscode\extensions` isn't loaded by current VS Code).
- **Two surfaces.** The activity-bar webview *view* (sidebar) renders a compact
  **launcher** (`launcher.html`/`launcher.js`) — status + three quick lifecycle
  actions + an "Open Control Panel" button. The full control panel
  (`panel.html`/`panel.js`) opens on demand as a wide editor-tab *panel*
  (`construct.openPanel`) and is restored across reloads via a registered
  `WebviewPanelSerializer`. Both surfaces share `media/panel.css` and the same
  message protocol; the full panel's 2-column layout is responsive and collapses to
  one column (with compact icon-only lifecycle buttons) when narrow.

## File layout

```
extension/
  package.json        manifest: activity-bar container, webview view, commands
  extension.js        activation; launcher + panel wiring; serializer; message router; probe refresh
                      (30s periodic auto-refresh while a dashboard is open; 5s while a reprovision
                      is in flight, until the provisioned commit changes or a cap elapses)
  media/
    launcher.html     sidebar launcher doc (status + update banner + 3 quick actions + Open button)
    launcher.js       launcher controller: postMessage, render(state)
    panel.html        full-panel doc (CSP + {{nonce}}/{{styleUri}}/{{themeUri}}/{{scriptUri}}/{{cspSource}})
    panel.css         base stylesheet shared by launcher + panel — structure + the Classic
                      Matrix skin (tokens from assets/banner.svg)
    panel.js          panel controller: rain, controls, postMessage, render(state)
    icon.svg          activity-bar glyph (filled, currentColor)
    themes/           one stylesheet per UI design, layered AFTER panel.css via the
                      {{themeUri}} link (classic.css is empty — panel.css IS classic;
                      terminal.css / native.css re-skin it; see the UI-designs decision)
    theme-previews/   one thumbnail per design for the picker cards (<id>.png)
  src/
    instances.js      instance registry: %LOCALAPPDATA%\The-Construct\instances.json —
                      load/parse/validate, synthesize the implicit `agent-vm` default,
                      derive per-instance alias/key/branch, resolveActive (setting >
                      workspaceState > registry default), matchByRemoteHost/
                      planRemoteAdoption/adoptRemoteInstance, createGate (the
                      stale-refresh generation guard), planHandover/createHandover +
                      planEnable + createSessionOwner (the ONE mic-tunnel chain: switch
                      handovers, the manual/auto enable+disable, and its shutdown),
                      planSwitchPersistence, atomic save + add/update/remove;
                      pure fs/path/JSON, no vscode. Its PS twin is
                      lib/AgentVm.Instances.ps1 (same file, same normalization rules)
    ssh.js            system-ssh runner (buildSshArgs/runRemote/runRemoteScript/isReachable);
                      cfg carries the active instance (vmHost/hostAlias/keyName/sshPort);
                      `-p` only for a non-22 port so the default argv is unchanged
    probe.js          REMOTE_PROBE + parseProbe/extractVersion/toState/probe(). The probe
                      also reports the T3 HTTPS keys (T3CODE_HTTPS/_HTTPS_PORT/
                      _PUBLIC_BASE_URL, written by bin/setup-t3-https.sh), and
                      toState(map, {host}) turns them into the t3code agent's detail +
                      `url`. HTTPS readiness is the RECORDED T3CODE_PUBLIC_BASE_URL, never
                      the T3CODE_HTTPS preference (which survives a failed setup), so a VM
                      whose proxy did not come up shows/opens its working http URL. That
                      value is cfgUnquote()d (config-set.sh single-quotes an IPv6 origin)
                      and accepted only when isSafeOrigin() says it is a bare http(s)
                      origin — it ends up in openExternal
    t3code.js         T3 Code live control: buildInstallScript/buildDisableScript (the
                      EMBEDDED bash the settings toggle runs over SSH; both call the VM's
                      bin/setup-t3-https.sh for the TLS front end, which is the one step
                      too large to inline), buildPairingScript (two variants — default
                      instance vs. one with a recorded CONSTRUCT_EXTERNAL_HOST — both
                      minting the link against the VM's recorded T3CODE_PUBLIC_BASE_URL
                      when there is one, so DPoP proofs match, else the plain http form),
                      extractPairUrl, baseUrl(cfg, probedUrl) and the _serial queue that
                      makes the last toggle action win. Its cfgget decodes config-set.sh's
                      single-quoted rendering
    remote.js         open the VM over Remote-SSH: isConnectedToVm(remoteAuthority, cfg) +
                      vscode-remote://ssh-remote+<alias>/<path> URIs built from the ACTIVE
                      instance's hostAlias; openOnVm (vscode.openFolder,
                      reuse/new window); needs the ms-vscode-remote.remote-ssh extension
    drivers/          hypervisor dispatch: getDriver(backend) (missing/empty => hyperv-local;
                      unknown => a fallback that resolves "unknown" and never throws), the
                      capability table {checkpoints, console, suspend, hostLifecycle} and
                      lifecycleSupport(backend, action) — the gate on the VM-destroying
                      lifecycle actions. index.js + hyperv-local.js + hyperv-remote.js;
                      contract in docs/drivers.md
    vmpower.js        the panel's power/state entry point, every export and signature kept:
                      queryVmState/queryAutoCheckpoints/startVm take an optional
                      opts.instance and dispatch through getDriver (an explicit opts.vmName
                      still wins, so older call sites are unaffected)
    remotehost.js     the constructd API client, no vscode: three credential providers
                      (negotiate via a spawned powershell.exe over lib/AgentVm.Remote.ps1,
                      token, domain credentials), TLS pinning against the SAME
                      %LOCALAPPDATA%\The-Construct\remote\<slug>.pin the PS client reads,
                      and the whoami / vms / forwards / idle-policy calls
    host.js           locate the scripts dir (newest %LOCALAPPDATA%\The-Construct\*\* with
                      Auto-Install.ps1, or the construct.scriptsDir override) + read/write
                      .construct-settings.json (form<->disk mapping; pure fs/path, no vscode);
                      configDir(env) resolves %LOCALAPPDATA%\The-Construct\config (machine-wide,
                      NOT slug-scoped — outside any zip checkout)
    configsync.js     config-sync engine: git-based profile sync between host and VM
                      (makeGitRunner, detectGit, ensureConfigTree, acquireSyncLock/
                      releaseSyncLock (cross-process .sync.lock, serializes ticks across
                      windows + the PS engine),
                      ensureRepo, repoState, syncTick, readRemotes/writeRemotes,
                      ensureStagingClone, listImportCandidates, planUpstreamImport,
                      mergeFile, commitAll, pushUpstream; see docs/config-sync.md)
    zip.js            hand-rolled ZIP writer (STORED entries, no deps): crc32, buildZip
    themes.js         UI-design registry (THEMES/DEFAULT_THEME/normalizeThemeId/
                      cssFileFor/previewFileFor) + buildPickerHtml (the picker webview
                      document, pure + injection-escaped) — no vscode dependency
    notify.js         VM -> host desktop notifications: the claim protocol
                      (buildClaimScript/parseEntries/selectDeliverable) + the Windows
                      toast payload (toastXml/buildToastScript/buildToastCommand,
                      powershellPath/toastResult/TOAST_EXIT), pure + sanitized — no
                      vscode dependency
    importui.js       pure decision core for the remote-config rename-on-collision import
                      (planRenamedImport: validate target + build canonical profile +
                      manifest provenance + base) — the testable half of extension.js's
                      importRemoteConfigs collision path
    lifecycle.js      reprovision/export -> Provision-AgentVM.ps1; reinstall/redownload ->
                      Auto-Install.ps1 -Action/-BackupMode; setCheckpoints ->
                      Set-AgentVmCheckpoints.ps1 -Enabled (elevated, live VM); launches a host
                      console via child_process (pure buildInvocation/buildHostLaunch;
                      vscode lazy-required)
    updates.js        update checks (best-effort, cached, injectable fetch): Construct =
                      GitHub compare(installedCommit...ref) -> {update:{available,behind}};
                      agents = npm/GitHub latest vs probed version -> per-agent {latest,
                      updateAvailable}; buildAgentUpdateScript (SSH force-update); both folded
                      into state by augment(). fetchJson follows 3xx redirects (a moved GitHub
                      repo resolves via its 301) + picks the Accept header per host (npm needs
                      application/json, not vnd.github+json). constructRefreshArgs for Update-Construct.ps1.
    projects.js       import-from-VM + select + per-project edit + config-sync helpers + share —
                      PURE transforms only (buildScanScript/parseScan, planImport,
                      reconcileSelection, sanitizeProfile, toChips; config-sync:
                      isReservedProfileName, validateProfile, canonicalProfileJson,
                      RESERVED_PROFILE_NAMES, sanitize* helpers; share: buildShareCommand,
                      buildDeployPs1, DEFAULT_INSTALL_REPO/REF, installUrlFor).
                      Profile file I/O lives in host.js; the SSH round-trip, edit modal
                      and QuickPick live in extension.js.
    usage.js          ccusage over SSH -> per-agent tokens + estimated cost. buildUsageScript
                      (base64-as-data) maps each VIEW to a ccusage window via --since/--until from
                      the VM clock — daily=today, monthly=this-month, total=all-time (no window);
                      reports are daily|monthly|total only (codex has no weekly report), default
                      daily; parseUsage/parseToolUsage (totals.totalCost|costUSD, window-scoped),
                      number/cost formatting, augment(state,{report}) (best-effort + cached-per-
                      report like updates), collectRaw/buildExportPayload/exportFileName for export.
    audio.js          on-demand mic passthrough. HostAudio: push vm/ scripts + apply the guard
                      patch over SSH, open a local TCP server + a persistent `ssh -R` tunnel
                      CONFIRMED by a settle window (an ssh that dies early = tunnel-failed, roll
                      back both sides); parses CONSTRUCT_GATE_PATCHED so the UI is honest.
                      dispose() CANCELS an enable that is mid-await (deactivate), which
                      enable() re-checks after every one of them and rolls back.
                      MULTI-WINDOW: the VM side is a port RANGE (8767..+8) — each window binds
                      the first free port (CONSTRUCT_PORTS_BUSY report + bind-race retry), the
                      shim scans for a live tunnel, and the disable script only removes the
                      shared shim/patch when the last window is out (see the design decision).
                      AudioSession: per-connection arm/disarm (mic hot only while recording).
                      makeHostMicProvider: spawns a NATIVE host recorder (ffmpeg, sox `rec`
                      fallback) that emits raw 16 kHz mono S16LE PCM on stdout and pipes it to
                      the tunnel socket — a webview CANNOT reach the mic (VS Code's webview
                      iframe Permissions-Policy `allow` omits `microphone`, so getUserMedia is
                      always rejected → silence). On Windows dshow needs an EXACT device name
                      (there is no `audio=default`): resolveWinMicDevice runs `ffmpeg
                      -list_devices` once (cached), parseDshowAudioDevices picks the first audio
                      device; the `construct.micDevice` setting overrides it; no device →
                      onError('no-device'); no ffmpeg/sox → onError('no-recorder') (honest, one
                      warning per enable). Guard patch apply/revert/idempotent. All pure
                      builders; ssh/spawn/net injected for tests.
    forwarder.js      CLIENT PORT FORWARDER — the extension half of `construct expose`
                      (docs/expose.md, §Forwards below). Built to the §4.8 module rules from
                      day one: no vscode, no child_process, no net — the transport
                      (runRemoteScript/spawnWatch/spawnTunnel/probePort/fetchJson) is
                      injected. Pure core: isSafeId/sanitizeHostLabel/portCandidates,
                      parseRequest/parseAck/ackDocument/parseDump/readForwardList, the spool
                      scripts (buildReconcileScript with the atomic `.owner` claim,
                      buildAckScript, buildRemoveScript, buildWatchScript,
                      buildCapabilityScript/parseCapability — the guest gate), planActions
                      (the one place open/ack/error/close/sweep/adopt is decided),
                      planLifecycle (when a window may hold a forwarder at all) and
                      toSnapshot. Forwarder: LAZY start (capability check first, then the
                      watcher + 30s reconcile locally, or the 10s poll remotely), tunnel
                      table, audio.js-style settle window + restart backoff, dispose
    forwarder-ui.js   the forwarder's adapter — the ONLY file in the feature that touches a
                      process, a socket or the vscode API: createSshTransport /
                      createRemoteTransport (ssh.js + child_process + net + a remotehost
                      client), buildStreamArgs (the long-lived watcher argv, notify.js's
                      shape), probeLocalPort (binds for real), hostLabelOf/forwardsEnabled
                      (settings), and the pure UI projections toPanelForwards /
                      clampIdlePolicy / toPanelIdlePolicy / powerIntentFor
    repatch.js        startup patch-verification. The claude-code patches (partial streaming +
                      the mic gate) are applied at provision time, but VS Code auto-updates that
                      extension on start, replacing extension.js with a stock (un-patched) build.
                      ~20s after activate (construct.repatchDelaySeconds — lets the update land),
                      runStartupRepatch probes the VM read-only (construct-patch-status.sh) and
                      re-runs the matching enable script for any feature that is ON in settings
                      but sitting at the stock gate. Quiet (logs only, like the mic auto-arm);
                      ssh/script-reader injected. parsePatchStatus/decideRepairs/confirmPatched pure.
  vm/                 scripts pushed to the VM over SSH by audio.js / repatch.js
    construct-rec-shim.sh        rec/arecord shim (scans the tunnel-port range via `ss -ltn`,
                                 streams the first live tunnel's PCM, dies on SIGTERM)
    construct-audio-enable.sh    install shim + apply remoteName-guard patch; prints
                                 CONSTRUCT_GATE_PATCHED=0/1 + CONSTRUCT_PORTS_BUSY=<csv>
    construct-audio-disable.sh   remove shim + revert patch (restore the .bak) — SKIPPED while
                                 another window's tunnel is live (last-window-out guard)
    construct-patch-status.sh    read-only probe: prints CONSTRUCT_PARTIAL_STATUS + CONSTRUCT_GATE_
                                 STATUS = patched|stock|unknown|absent (drives repatch.js; never edits)
  test/
    ui-smoke.js       Playwright headless-Chromium webview test (238 checks: panel + launcher +
                      narrow overflow + settings round-trip + honesty + power buttons + add-project +
                      per-chip open + project edit modal + usage table + daily/monthly/total period tabs
                      (incl. period-change-without-usage blanks the table, same-period keeps it) +
                      audio substatus incl. gate-patch state + launcher update banner +
                      config-sync strip: absent→hidden, gitPresent:false→install-git notice,
                      conflict→banner+open-repo, remotes list, default chip locked/no-modal,
                      sync-now posts syncConfigNow, installGit posts, offline survival;
                      forwards card: absent/local-empty→hidden, rows per forward with the
                      remapped-port form, queued/open/error styling, Open disabled without a
                      link, Open/Close post their ids, non-ownership stated, offline
                      survival; idle-policy card: hidden without a policy, populated + the
                      admin cap as an input max and a hint, apply posts the edited values, a
                      refresh does not overwrite a half-typed number, clamped is stated;
                      power: vmState 'saved' → "Resume & connect" still firing startConnect on
                      BOTH panel and launcher; a local non-owner's Close disabled; host-target
                      rows rendered/openable/closable; idlePolicy null hides the card);
                      UI_SMOKE_THEME=classic|terminal|native re-runs the WHOLE suite under
                      that design skin — the designs-can't-fork-behavior invariant
    probe.test.js     plain-node ssh-arg + probe-parse units (21 checks)
    configsync.test.js plain-node config-sync engine units — git-based sync tick, staging clones,
                      upstream import planning, merge-file, read/write store scripts, repo state,
                      seeding, conflict handling (140 checks)
    host.test.js      plain-node scripts-dir resolution + settings merge + readProjectProfile +
                      project-profile list/write/select + traversal + hasPersistedSelection + writeProjectProfileIfAbsent + race test (79 checks; fake %LOCALAPPDATA% tree)
    remote.test.js    plain-node Remote-SSH helpers — isConnectedToVm/remoteFolderUri + repoNameFromUrl/isLikelyGitUrl/buildCloneScript/projectOpenPath/shouldAutoOpenPanel + URI percent-encoding (71 checks)
    lifecycle.test.js plain-node buildInvocation (incl. the setCheckpoints action + which
                      actions carry -AutomaticCheckpoints) + winQuoteArg/quoting/elevation units
    updates.test.js   plain-node update-check units — Construct compare/cache + agent semver/latest/script + fetchJson redirects/per-host Accept, injected fetch+clock+http (62 checks)
    vmpower.test.js   plain-node Hyper-V power units — Get-VM probe/parse + Start-VM/elevated
                      launch builders + injected-spawn queryVmState + the automatic-checkpoint
                      policy probe (parse/injection/spawn) and shouldOfferCheckpointApply's
                      truth table, incl. the upgrade path (VM on, preference unchanged) (68 checks)
    project-set.test.js plain-node VM-side project set/get/list CLI units — validation, reserved
                      names, atomic writes, PROJECTS_STORE override (54 checks)
    projects.test.js  plain-node scan builder/parser + planImport merge + reconcileSelection +
                      sanitizeProfile (injection + prototype-pollution) + config-sync helpers:
                      isReservedProfileName, validateProfile, canonicalProfileJson, case-insensitive planImport collision, additiveMergeSelection, share builders (166 checks)
    themes.test.js    plain-node UI-design units — registry shape + settings-enum sync with
                      package.json, css+preview files exist per design, normalize fallbacks
                      (hostile/unknown -> default), picker HTML nonce/CSP/escaping
    usage.test.js     plain-node ccusage script (daily/monthly/total window mapping) + parse (totalCost/costUSD/missing/error/zero/array) + formatting + per-report cache TTL/coalesce + isCurrentReport stale-collection ordering + export payload, injected ssh+clock (105 checks)
    audio.test.js     plain-node guard-patch apply/revert/idempotency + VM script builders (injection proofs) + ssh -R argv + AudioSession gating + HostAudio enable/disable/rollback + tunnel settle-window (async early death + later death) + multi-window port range (busy-skip/bind-race retry/no-free-port/self-port disable) (233 checks)
    notify.test.js    plain-node notification units — claim/watch script invariants (atomic
                      claim, stranded-entry recovery, inotify monitor mode), stream split,
                      parse/select/backoff, toast XML + PowerShell injection proofs, the
                      notifier-candidate/exit-code contract, toastResult and powershellPath
                      (100 checks). The generated toast script's RUNTIME behaviour is covered
                      by `../../test/notify-toast.test.ps1` (pwsh, WinRT stubbed in C#)
    forwarder.test.js plain-node client-forwarder units (518 checks) — pure guards + port
                      policy + backoff, the spool documents, the spool SCRIPTS as data
                      (base64-as-data, the atomic ack rename, the claim's read-back, inotify
                      monitor mode + the orphan trap, injection proofs for the spool path and
                      the window id), the PLANNER's every action case incl. idempotence, the
                      local flow against a fake transport (request → tunnel argv → atomic ack
                      → close → kill, port fallback, re-open on connect, read-only
                      non-ownership, claim release), the remote flow against a fake fetch
                      (poll → tunnel → POST ack, entry removed → kill, an acked entry left
                      alone), tunnel supervision (settle window, backoff, the error ack after
                      persistent failure, dispose), the LAZY START (capability check first;
                      an older guest gets no watcher and no reconcile at all) and
                      planLifecycle's start/stop rule, a MODEL of extension.js's forwarder
                      wiring around REAL Forwarder sessions with both awaits deferred
                      (disable/deactivate during the transport build, the same four stops
                      during the guest check, overlapping starts, A→B→A, the reconnect
                      edge — one session, zero orphans, zero spawn after a stop, each with
                      a pre-fix control), the UI projections, and ssh.js's `-L` argv.
                      The CLAIM PROTOCOL is executed for real (bash, 5 windows x 25
                      concurrent rounds, stale-record + stale-lock takeover): mutual
                      exclusion is a property of concurrent execution, not of the source
    repatch.test.js   plain-node startup patch-verification units — parsePatchStatus (line-anchored/last-wins/CRLF) + planStartupActions (the streamingOff+micOn+no-tunnel retry regression) + decideRepairs (on∧stock truth table) + confirmPatched + runStartupRepatch orchestration vs a fake ssh (unreachable/probe-fail/streaming-only/mic-only/both-patched/no-confirm) (39 checks)
```

## Webview ↔ extension message protocol

Defined in `extension.js` (handleMessage), `media/panel.js` and `media/launcher.js`.

**webview → extension**
- `{type:'ready'}` — webview loaded; triggers a probe + state push.
- `{type:'command', id, project?}` — ids: `reprovision`, `exportConfig`,
  `redownload`, `reinstall`, `updateConstruct`, `updateAgents`, `refresh`,
  `openProjectFolder`, `selectProfiles` (multi-select QuickPick → persist),
  `exportUsage` (collect ccusage → Save dialog),
  `editProject` (+`project`; opens the edit modal),
  `connect` (open the VM over Remote-SSH),
  `startConnect` (elevated Start-VM then poll+open), `shutdown` (poweroff over SSH),
  `addProject` (prompt a git URL → clone over SSH → open in a new window),
  `openProject` (+`project`; open that project's folder on the VM in a new window),
  `syncConfigNow` (immediate config-sync tick, no throttle),
  `addConfigRemote` (showInputBox URL → writeRemotes + staging clone),
  `removeConfigRemote` (+`url`; confirm modal → writeRemotes),
  `importRemoteConfigs` (clone/fetch all remotes → multi-select QuickPick → 3-way merge),
  `shareConfigs` (multi-select profiles → clipboard command or zip bundle),
  `pushConfigUpstream` (+`url`; confirm → push local changes to a new branch),
  `installGit` (win32-only: visible console running winget install Git.Git),
  `openConfigRepo` (open cfgDir in a new VS Code window for conflict resolution),
  `openForward` (+`forward`; `vscode.env.openExternal` on that forward's link),
  `closeForward` (+`forward`; kill the tunnel + drop the request/service record).
- `{type:'setAudio', enabled}` — live mic-passthrough toggle (console switch only).
- `{type:'saveIdlePolicy', policy:{timeoutMinutes, action}}` — the idle-policy card's
  "apply" (remote instances only; re-clamped to the admin cap extension-side before the
  `PUT`, because the webview is untrusted input).
- `{type:'setUsagePeriod', period:'daily'|'monthly'|'total'}` — switch the token-usage window
  (validated + remembered in `usageReport`; triggers a `refreshAll` that re-collects the
  scoped numbers and re-broadcasts `usagePeriod`).
- `{type:'saveProject', name, profile}` — the edited profile posted back from the modal
  (sanitized + written to `projects/<name>.json`).
- `{type:'openPanel'}` — open the wide editor-tab panel.
- `{type:'setInstance', name}` — the header dropdown picked another instance (the name
  is validated against the registry extension-side; the webview is untrusted input).
- `{type:'saveSettings', settings}` — persist the settings form.
- `{type:'customRebuild', mode:'reinstall'|'redownload', backup:'save'|'existing'|'wipe', backupId}`.

**extension → webview**
- `{type:'state', state}` — full render (see shape below).
- `{type:'audio', enabled, capturing, tunnel, gatePatched}` — live audio status (flips the
  switch; `gatePatched` drives the honest "chat mic button" substatus line).
- `{type:'editProject', name, profile}` — open + populate the project edit modal.
- `{type:'settings', settings}` — populate the settings form from disk.
- `{type:'forwards', forwards}` — repaint just the Forwards card (a tunnel came up or went
  away). Its own message type rather than a partial `state`, for the same reason
  `{type:'audio'}` is: `render(state)` reads every absent field as "no reading", so a
  `{forwards}`-only state would blank the power button, flip the ONLINE pill and reset the
  install markers. The full push carries `state.forwards` too, so a webview that opens
  mid-session renders the card on its first `ready`.
- `{type:'idlePolicy', idlePolicy}` — same, for the idle-policy card after an apply.

**state shape** (every field optional; `render()` guards each, and clears
VM-derived fields when `online===false` or `probeError`):
```
{ online, connected, vmState:'running'|'off'|'saved'|'absent'|'unknown',
  instance,                                // active instance NAME (always present)
  instances:[name],                        // only when >1 exists — renders the picker
  host, hostShort, vmName, ubuntu, resources, constructRev,
  installed, reprovisioned, update:{available,behind},
  agents:[{id,name,detail,version,updateAvailable,latest}],
  projects:[{name,selected}],
  usagePeriod:'daily'|'monthly'|'total',   // active token-usage tab (rides the sync first push)
  usage:{tools:[{label,tokens,tokensText,costText}], totalTokensText, totalCostText},
  audio:{enabled,capturing,tunnel}, probeError,
  configSync:{gitPresent, repoReady, conflict, conflictFiles, mergeInProgress,
    lastSyncAt, lastResult:'ok'|'conflict'|'blocked'|'error'|null,
    warnings:[string], remotes:[{url}]},
  forwards:{mode:'local'|'remote', owner, visible,               // §Forwards
    items:[{id, vmPort, label, target, status:'open'|'queued'|'error',
            localPort, url, message}]},
  idlePolicy:{timeoutMinutes, action:'save'|'shutdown'|'off',    // remote only; null = hide
    maxTimeoutMinutes, clamped} }
```

**`vmState:'saved'`** is a LABEL-only distinction. The driver contract collapses
`saved`/`paused`/`off` into `off` because every caller that *acts* on the state wants "a
start brings it back" — but the power button's wording is different for a VM the idle
policy saved, since the same call resumes it where it was. `withVmState` therefore re-reads
the service's own enum (remote instances only, only when the collapsed answer was `off`)
and the panel says **Resume & connect**.

**`forwards` and `idlePolicy` are host/service-derived — NOT cleared by
`clearLiveVmData`.** A VM that stopped answering SSH has not closed the ports this PC is
holding open, and a `forwards` card that blinked out whenever a probe failed would be
telling the user their links had died. `forwards` also rides its own narrow push
(`{type:'state', state:{forwards}}`) whenever a tunnel comes up, so it does not wait for
the next 30 s refresh.

**`configSync` is host-derived — NOT cleared by `clearLiveVmData`.** It reflects the
local config-dir state (git presence, repo conflicts, linked remotes) and survives
an offline VM. Its `lastSyncAt`/`lastResult`/`blockedReason`/`warnings` describe the
**active instance's own** last tick (see "Instances"), so an instance that has never
synced reports exactly that instead of the previous instance's result. The webview renders it in the "Config sync" strip inside the Projects
module, regardless of `online`.

## Instances

The extension drives **one instance per window**. An *instance* is one named VM plus
everything the client needs to reach and manage it. `agent-vm` is the **implicit
default**: with no registry file there is exactly one instance, it holds today's
literals, and nothing in the UI or in any launched command line changes.

### The registry

`%LOCALAPPDATA%\The-Construct\instances.json` — next to the existing `config\` dir
(`host.configDir`'s sibling). Schema v1:

```jsonc
{
  "version": 1,
  "defaultInstance": "agent-vm",
  "instances": {
    "agent-vm": {                        // implicit; synthesized when absent
      "backend": "hyperv-local",
      "vmName": "Agent-VM",
      "sshHost": "agent-vm.mshome.net", "sshPort": 22,
      "hostAlias": "agent-vm",
      "keyName": "agent_vm_ed25519",
      "configBranch": "vm",
      "scriptsDir": null,                // null = newest install (today's detection)
      "service": null, "owner": null
    },
    "work-vm": {
      "backend": "hyperv-remote",
      "service": { "url": "https://buildbox.example.local:7462", "auth": "negotiate" },
      "vmName": "work-vm",
      "sshHost": "buildbox.example.local", "sshPort": 2201,
      "hostAlias": "work-vm",
      "keyName": "construct_work-vm_ed25519",
      "configBranch": "vm-work-vm",
      "owner": "DOMAIN\\alice"
    }
  }
}
```

Two readers, one file and one set of rules: `src/instances.js` (pure fs/path/JSON, no
`vscode`) and `lib/AgentVm.Instances.ps1` (dot-sourceable, PS 5.1). **Change both
together** — the JS module's header comment is the shared contract.

**Missing file, unreadable file, or a missing entry ⇒ the default instance is
synthesized and NOTHING is written.** Neither reader ever throws: problems are
collected (`.problems` / `.Problems`) and the caller decides how to surface them (the
extension logs each one and toasts once per distinct problem set).

**Instance names** are one lowercase DNS label — `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`,
and **not** starting with the reserved prefix `construct-`. They end up verbatim in file
names, SSH aliases and git refs, so:

- the **last** character must be alphanumeric too. `work-` derives the endpoint
  `work-.mshome.net`, which is not a host name at all — a name the validator accepted and
  the identity rules then refused was a name no instance could be recorded under;
- **1–63** characters — the DNS label's own limit, because the name *is* a label of
  `<name>.mshome.net`. Every derived value has to stay usable at that length, which is why
  `keyName` carries its own longer bound: `construct_` + 63 + `_ed25519` is 81 characters,
  so the key-**file** rule allows 128 while the ssh-**alias** token rule keeps 64 (an alias
  is not a path, and `hostAlias` is the bare 63-character name). Same character class in
  both — nothing about path or control-character safety is loosened;
- `construct-` is **reserved**. It is the namespace the derived key file and branch live
  in, and the derivation used to *strip* it (an abandoned, never-shipped alias
  convention), so the perfectly valid instance `construct-work` derived branch
  `vm-construct-work` here and `vm-work` in the provisioner — the config store of the
  *different*, equally valid instance `work`. Reserving the prefix is what leaves exactly
  **one derivation rule everywhere**: alias = `<name>`, key = `construct_<name>_ed25519`,
  branch = `vm-<name>`.

The identical rule is applied by `lib/AgentVm.Instances.ps1`, by `Auto-Install.ps1` /
`Create-AgentVM.ps1` before a local VM is created, and by the host service
(`Constructd.Core.Logic.VmNameValidator`) — change all four together. An invalid name is
skipped with a problem, not guessed at.

**One normalization contract, two readers.** These rules are what keep the JS and PS
readers from disagreeing about the *same* file — a disagreement would mean the panel
and the scripts targeting different machines. Both test suites run the same
malformed-input matrix (`extension/test/instances.test.js` ↔ `test/instances.test.ps1`;
change them together):

- **Any explicit `version` other than `1` is refused, not partially read.** A later
  schema may redefine what a field *means*, and acting on a misread entry could target
  the wrong host — so the file is ignored, a problem is reported, and the byte-identical
  default stands. An *absent* version is still read as v1 (hand-written files omit it).
  `1` means the JSON **number** in both readers: a quoted `"1"` is a foreign schema.
  PowerShell used to compare the two operands as *strings* and load such a file while
  `instances.js` refused it — the same bytes then selected `work-vm` on one side and
  `agent-vm` on the other, which is precisely the disagreement this contract exists to
  prevent (`$true` is likewise not `1`, matching JS's strict `!==`).
- **Every non-object top level is malformed** — arrays *and* the falsy scalars
  (`0`, `false`, `null`, `""`), which a truthiness guard would silently swallow as "an
  empty registry" with nothing for the user to see.
- **String fields are type-strict.** `sshHost: 123` is a malformed file, not the host
  name `"123"`: the value is reported and the *derived* default is used. (PowerShell
  needed two extra guards for this — `[string]` type tests instead of `[string]$v`
  stringification, and `return ,$value` so an array field isn't unrolled into a scalar.)
- **Enum comparisons are case-sensitive** in both readers (`-cnotcontains` / `-cne` on
  the PS side), so a case-variant or `auth: "TOKEN"` can never be honoured by one and
  rejected by the other. For `backend` that is not the end of it: `getDriver()`
  *lowercases*, so a case-variant of a known id would still be handed the real driver —
  which is why such an entry is refused whole (next bullet).
- **`backend` is never coerced, and the rule is presence-aware.** Rewriting anything to
  `hyperv-local` (which both readers used to do) *promoted* it to destructive local
  Hyper-V access: `drivers.lifecycleSupport` would see `hostLifecycle: true` and let
  Reinstall/Redownload/checkpoints run against a *local* VM that merely shares the
  instance's `vmName`. Exactly four cases, identical in both readers:
  | the entry's `backend` | result |
  |---|---|
  | absent, or JSON `null` | `hyperv-local` — the zero-change default, and the identity is then held to the canonical rule below |
  | a known id (`hyperv-local`, `hyperv-remote`), possibly with surrounding whitespace | used as written (trimmed) |
  | any other non-empty string whose **lowercase form is also unknown** (`"proxmox"`, `"hyperv-remtoe"`, `"HYPERV-PROXMOX"`) | **kept verbatim** and reported. It reaches `drivers/index.js` as itself, gets the unknown-driver fallback, and the hypervisor actions are refused — Reprovision and Export config still work, being pure SSH |
  | present but unusable (`42`, `true`, `""`, `"   "`, an array/object), **or** a spelling that differs from **any implemented id** only by case (`"HYPERV-LOCAL"`, `"HYPERV-REMOTE"`, `"Hyperv-Remote"`) | the entry is **skipped** with a problem |
  The last row is the subtle one. `backend` is *not* in the type-strict `STRING_FIELDS`
  list, because "report it and use the derived default" is the wrong answer for the one
  field whose derived default is the local hypervisor — the file stating a backend that
  isn't one must not become "no backend". And a case-variant spelling is read *two ways*:
  every enum comparison in both readers is case-sensitive (so the value is "unknown" to
  them), while `getDriver()` trims and lowercases before the lookup (so it hands back the
  **real** driver — the local one with `hostLifecycle: true`, or the remote one that
  drives somebody else's host service). A value the two disagree about is not safe to act
  on under either reading, so it does not load at all. Restricting this to `hyperv-local`
  (as it once was) left `"HYPERV-REMOTE"` loading while every message about it claimed it
  had no driver — and it had one.
- **`sshPort` accepts exactly two shapes**: an integral JSON number, or a bare-digit
  string (both readers trim). Not `"+2201"`, not `2201.5`, not `true`. The range check
  happens in a wide numeric type *before* any Int32 cast — `[int]999999999999` throws in
  PowerShell, and an exception escaping the reader would break the "never throws"
  contract the whole zero-change path rests on.
- **The name→instance map is prototype-free, and membership is an OWN-property test.**
  A plain `{}` inherits `Object.prototype`, so `byName["constructor"]` was truthy for
  *every* registry: `{"defaultInstance":"constructor","instances":{}}` parsed without a
  problem and `resolveActive` handed out Object's **constructor function** as the
  instance (undefined `vmHost`/`keyName` in every ssh argv), while the PowerShell reader
  — an ordinal `Hashtable` + `ContainsKey` — correctly reported "no entry" and used
  `agent-vm`. A `construct.instance` pin, a stale `workspaceState` selection and the
  panel's instance dropdown reach the same lookup, and none of them is name-validated
  first. So every map is `Object.create(null)` and every test is
  `hasOwnProperty`-based (`instances.hasInstance`), on parse, resolve, mutate, default
  selection and active selection alike. An instance genuinely *named* `constructor` is a
  valid name and still works, in both readers; `__proto__`/`toString` etc. are rejected
  by the name rule in both. Both suites run the same fixture matrix.
- **Schema keys are matched ORDINALLY, exact-case, in both readers.** JavaScript
  property access is case-sensitive; `$obj.PSObject.Properties['name']` is *not*, so the
  PS reader used to accept fields JS never sees. `{"VERSION":1,"DEFAULTINSTANCE":"x",
  "INSTANCES":{"x":{…}}}` was ignored by `instances.js` (which found no version, no bag
  and no pointer, and used `agent-vm`) and loaded by PowerShell — with `x` as the
  **default** instance; a wrong-cased `"BACKEND"`/`"SSHHOST"` inside an entry did the
  same one entry at a time, turning a derived `hyperv-local` instance into a remote one
  on the PS side only. `Get-ConstructRawProperty` now walks `PSObject.Properties` with
  `StringComparison.Ordinal` (and the top-level `instances` lookup goes through it), so a
  wrong-cased key is simply **absent** — never a value, and never a "must be a string"
  problem either. Both suites feed their reader the *same bytes* (uppercase and mixed-case
  fixtures, top-level and nested). One divergence is left in place deliberately because
  it fails closed: a file that spells the same key in two casings (`"version"` **and**
  `"VERSION"`) makes `ConvertFrom-Json` itself throw on PowerShell 6+, so that reader
  degrades to "not valid JSON" plus the default instance (Windows PowerShell 5.1 has no
  `-AsHashtable` to avoid it).
- **`isDefaultInstance` compares case-sensitively** (`===` / `-ceq`), and the PS
  instance table is an ordinal hashtable. Otherwise a `vmName` of `"agent-vm"` would
  read as the default on one side and as a non-default instance on the other — and only
  one of them would emit target arguments.
- **Identity fields are format-checked, and a bad entry is skipped WHOLE**
  (`identityProblems` / `Get-ConstructInstanceIdentityProblem`). Being a string is not
  enough for values that end up in a PowerShell command line, an ssh argv, a key path
  or a git ref — `"-x; Start-Process calc; #"` is a perfectly good JSON string. So
  `sshHost` must be a host name or an IP literal, `hostAlias` one path-free token,
  `keyName` the same *plus* what Windows adds — it is written as `~\.ssh\<keyName>`,
  so a trailing dot (Win32 strips it, aliasing the default instance's key file) and a
  reserved device stem (`CON`, `NUL`, `COM1`, with or without an extension) are
  refused — `vmName` a single DNS label (the shape `Auto-Install.ps1` enforces), and
  `configBranch` must pass the config-sync branch validator. An IP literal is
  shape-filtered and then handed to a real parser (`net.isIP` / `IPAddress.TryParse`),
  because a character class happily accepts `::::` or `1::2::3`; the shape filter is
  also what keeps the two parsers in step (Node accepts `fe80::1%eth0`, .NET accepts
  `[::1]` — neither spelling reaches either parser). **Both** host spellings are
  checked, not just the one that wins normalization: `{sshHost, vmHost}` where only the
  losing field is hostile must not load. Half an identity would
  dial, key or sync some *other* machine, so the entry is dropped and reported rather
  than partially used. Every derived value satisfies the rules, so only a hand-written
  entry can trip them.
- **A `hyperv-local` instance's identity is CANONICAL — derived from its name, and any
  deviation is skipped.** `vmName` must lowercase to the instance name, `sshHost` must be
  `<name>.mshome.net`, `hostAlias` must be the **bare** `<name>` (a `construct-<name>`
  spelling is not a legacy form to tolerate — the prefix is **reserved**, refused by every
  name validator, and nothing strips it anywhere any more; see *Instance names* above),
  `keyName` must be `construct_<name>_ed25519`, and
  `sshPort` must be `22`; the default instance keeps `Agent-VM` /
  `agent-vm.mshome.net` / `agent-vm` / `agent_vm_ed25519` / `22`. The reason is that
  Reinstall/Redownload emit **only `-VmName`** and `Auto-Install.ps1` derives the rest
  from it (guest host = `<vmname lowercased>.mshome.net`, alias = that name, key
  `construct_<name>_ed25519`). So an entry named `work-vm` with `vmName: "Agent-VM"`
  would *rebuild the default VM* — a "work-vm reinstall" that deletes and recreates
  Agent-VM — and a custom host/alias/key is accepted by the extension but silently
  replaced by the derived one during the rebuild, leaving the instance unable to reach
  the VM it just recreated. `configBranch` is deliberately **not** pinned: it is the one
  field the launched scripts can be *told* (`-ConfigBranch`, threaded by
  `lifecycle.configBranchOverride` and gated by `checkInstanceSupport`), so an explicit
  branch stays a supported override. **Non-local backends keep free-form** (still
  format-checked) identities — their endpoints are defined on the other side — but they
  are not rule-*free*: `hyperv-remote` has two of its own, below.
- **A `hyperv-remote` instance must state its endpoint and share ONE VM name.** Two
  whole-entry rejections (`remoteIdentityProblems` / `Get-ConstructRemoteIdentityProblem`),
  applied to every spelling `getDriver()` resolves to the remote driver (a case-variant
  `"HYPERV-REMOTE"` included, since that is what would act on the entry):
  - **`vmName` must equal the instance name**, compared *exactly*. A remote VM is
    addressed **by name** on the host service, from two directions that have to mean one
    machine: the driver queries and starts `vmName` (`drivers/hyperv-remote.js`), while a
    rebuild emits `-InstanceName <name>` and `Auto-Install.ps1` then uses the registry
    **entry's** name to fetch the endpoint, **delete** the VM and create it again. An
    entry keyed `alias-vm` with `vmName: "service-vm"` therefore split the identity in
    half — the panel's power state and Start acted on `service-vm` while Reinstall
    deleted and recreated `alias-vm`, i.e. potentially the wrong VM on somebody else's
    machine. The comparison is exact (not lowercased like the local Hyper-V display
    name) because the value goes into a URL path (`/vms/{name}`) and into a
    `-InstanceName` argument, and nothing may assume the service folds case.
  - **`sshHost` is required.** A remote endpoint is whatever the service allocated; no
    name convention can produce it. An entry that omitted it still *loaded*, with the
    derived `<name>.mshome.net:22` — an actionable instance whose picker entry and SSH
    lifecycle actions pointed at an unrelated machine on this PC's own network. Only the
    canonical spelling counts: everything that writes the registry writes `sshHost`, so
    an entry stating its endpoint under the JS-internal `vmHost` alias is a hand-written
    file and refusing one is the fail-closed reading.
- **Identities are UNIQUE across the registry.** No two entries may share a `vmName`, an
  **endpoint**, a `hostAlias`, a `keyName` or a `configBranch` (compared
  case-insensitively — one Hyper-V name, one machine, one `Host` block, one NTFS key
  file, one Windows loose-ref file), and a non-default entry may not claim any of the
  *default* instance's five — which is also what **reserves the branch `vm` for
  `agent-vm`**. The endpoint is the **composite `(sshHost, sshPort)`**, not the host
  alone: several `hyperv-remote` instances legitimately live on ONE service host and are
  told apart by the SSH forward the service allocated them (one port per VM out of a
  configured range), so keying on the host made every VM on a shared host collide — and
  the "drop both" rule below then lost the entire registry. The port is compared
  numerically (`2201` and `"2201"` are one endpoint) and a `hyperv-local` instance's port
  is canonically `22` with a host derived from its own name, so local entries still
  cannot share an endpoint — while a remote VM forwarded on the *same machine* as a local
  one, on another port, is a different endpoint and loads. `configBranch`
  is in that list because the branch *is* the instance's store inside the one host config
  repo (docs/config-sync.md, "Multiple instances"): two entries on one branch share their
  VM snapshots, deletion history, merge base and write-backs, so one VM's tick merges —
  or deletes — the other VM's configuration. The derived branches (`vm-<name>`) are
  unique by construction, so only a hand-written override can trip it. A clash with the default skips the
  claimant; a clash between two entries skips **both**, because nothing in the file says
  which is the impostor — and dropping both is also what keeps the two readers'
  outcomes independent of key order.

The same rules apply on the WRITE side: `addInstance`/`updateInstance` throw rather than
persist an entry the reader would skip (it would simply vanish from the picker on the
next load). `addInstance` rejects **every** existing name, `agent-vm` included — it is always
present (synthesized), so an "add" would silently *replace* the default instance.
Changing an existing entry is `updateInstance`'s job.

### Derivations for a non-default `<name>`

For `hyperv-local` these are not just defaults — they are the **only** accepted values
(see the canonical-identity rule above); for other backends they are the fallback when a
field is omitted.

| field | derived value | why |
|---|---|---|
| `hostAlias` | `<name>` | the **bare** name. Every shared PowerShell helper (`Get-RemoteOpenLink`, `Close-VmVsCodeWindow`, `Invoke-ConstructVmSsh`'s alias fallback) derives the SSH alias as the first DNS label of the VM host, and `Auto-Install.ps1` writes alias = lowercased VM name. The registry has to agree or the two would write different `Host` blocks. |
| `keyName` | `construct_<name>_ed25519` | one key file per VM; the default keeps `agent_vm_ed25519`. |
| `configBranch` | `vm-<name>` | **not** `vm/<name>`: git cannot hold `refs/heads/vm` and `refs/heads/vm/x` at once, and the default instance's branch is literally `vm`. |
| `vmName` | `<name>` | the Hyper-V display name — and, for `hyperv-remote`, the **only** accepted value (see the rule above). |
| `vmHost` | `<name>.mshome.net` | `hyperv-local` only. A `hyperv-remote` entry **must state `sshHost`**: it is refused whole rather than left holding this derived local address. |
| `sshPort` | `22` | |
| `scriptsDir` | `null` | = newest-install detection, today's behaviour. |

The default instance keeps `agent-vm` / `Agent-VM` / `agent-vm.mshome.net` / `22` /
`agent_vm_ed25519` / `vm` byte-for-byte.

### The active instance

Precedence (`instances.resolveActive`, unit-tested):

1. the **`construct.instance`** setting — a global pin, `""` = unset;
2. the window's **`workspaceState`** choice (`construct.activeInstance`) — or, when that
   write **rejected**, the window-local override that stands in for it;
3. the registry's **`defaultInstance`**.

`workspaceState.update` can fail (a corrupt or locked storage file). Saying the window
switched "for now" while *nothing* held the new selection was a lie in both directions:
`activeInstance()` kept resolving the previous instance, so the refresh that followed
re-rendered the VM the user had just switched away from. So a failed write installs an
explicit window-local override (`instances.planSwitchPersistence` →
`windowInstanceOverride`, read by `workspaceInstance()`) at **exactly** the same
precedence level — the switch really does hold for this window, it simply doesn't
survive a reload, and the `construct.instance` pin still outranks it. The warning says
that, and the next successful write clears the override.

**A pin only counts when the registry holds it** (`instances.effectivePin`, the same
own-property membership `resolveActive` uses). Both of a switch's warnings — "the choice
couldn't be saved" and "the setting pins every window to X" — are driven by that one
value, because a *stale* `construct.instance` pins nothing: the window does move, and
reporting the removed name as the reason it "still uses" another VM would contradict its
own active target. A set-but-ineffective pin is logged instead.

A name at any level that the registry no longer holds is skipped (with a problem) and
the **next candidate is tried** — the registry default is only used once every
candidate is exhausted. A stale global `construct.instance` (an instance someone
removed) must not drag a window that still has a perfectly valid per-window selection
back to the default VM. A window attached over Remote-SSH to a VM that *is* a known
instance **adopts it at activation** (`planRemoteAdoption` → `adoptRemoteInstance`), so
the panel always describes the machine you are working on — unless the setting pins
another one. `activate()` is **async and awaits that adoption**: `workspaceState.update`
is a Thenable, and a fire-and-forget write would let the status bar, the auto-open, the
mic auto-arm and the notification watcher all start against the *previous* selection and
probe the wrong VM. The decision itself is a pure function, so it is unit-tested rather
than only observable in a live window.

**The registry is observed, and a change is one serialized transition.** `instances.json`
belongs to no single process: another window's picker writes it, `Auto-Install.ps1` records
a VM into it, a rebuild rewrites an entry. None of that raises a VS Code event, so three
changes used to go unnoticed by a window that was already running — the **default flipped**
(this window follows it), the **selected entry removed** (`resolveActive` falls back to
another VM), and, worst, an entry **rewritten under the same name** (a rebuilt remote VM
comes back on a new `sshHost`/`sshPort`). The last one changed nothing a *name*-keyed gate
could see, while the probes, the notification stream, the mic tunnel, the forwarding
transports, the idle-policy cache and the sync throttles all went on using the endpoint
that no longer existed.

So "which VM is this window driving" is a **fingerprint of the complete normalized target**
(`instances.targetFingerprint`: name + backend + vmName + sshHost + sshPort + hostAlias +
keyName + configBranch + scriptsDir + service url/auth + owner), not a name:

- `activeInstance()` sets the gate with `set(name, fingerprint)`, so a same-name rewrite
  bumps the generation and every in-flight token for the old endpoint is discarded;
- the registry is watched (`fs.watch` on its **directory** — it is written tmp+rename —
  debounced) and re-checked on every refresh tick, and both routes call the one
  `retargetIfChanged()`;
- every route that retargets — the picker/command, the `construct.instance` setting and
  the observer — goes through **one serialized transition** (`queueInstanceTransition` →
  `onInstanceChanged`), because the handovers are async chains: two overlapping transitions
  would interleave a teardown of A with an arm of C.

`onInstanceChanged` hands over the notification watcher, the mic tunnel and the forwarder
when the name **or** the identity changed (all three terminate on an *endpoint*), clears the
per-target caches, and refreshes. **Zero-change:** with no registry file no watcher is ever
opened, the synthesized default's fingerprint is a constant, and the gate is seeded with it
— so a single-VM window stays at generation 0 for its whole life and takes none of this
path. A **no-op rewrite** (re-serialized, reordered, or spelling out a field the derivation
would have filled in) normalizes to the same fingerprint and does nothing at all.

**Stale refreshes are discarded, not relabelled.** A refresh is a multi-stage async
pipeline (probe → Hyper-V state → GitHub updates → ccusage → config-sync) and any stage
can outlive a switch. Without a guard, instance A's slow probe resolving after a switch
to B would be stamped with B's name and painted over B's data. So `instances.createGate()`
issues a token at the start of each pipeline, and `refreshState`/`refreshAll` re-check
`instanceGate.valid(token)` **after every await, before any post or cache write** — a
late stage abandons the whole continuation. The instance is also threaded explicitly
into `withLocalState`/`withVmState`/`augmentUsage`, so a payload is always labelled with
the VM it actually came from rather than with "whatever is current now". `probeOnce` is
keyed by instance name too: coalescing is only correct between callers asking about the
same VM. The gate is pure, so the discard rule is proven with deferred A/B promises in
`extension/test/instances.test.js`. **Auto-import is keyed the same way**: `coalescedImport`
runs through `instances.createCoalescer` — a pure, clock-injectable helper that keys both
the in-flight promise and the five-minute throttle stamp by instance name (its ordering
rules are driven with deferred promises in the same test file) — and the scan captures
its target's cfg before the first await. Sharing them globally let
a scan of A satisfy a pre-flight asking about B (B was reported "scanned" on A's result)
and let A's timestamp suppress B's first automatic scan.

**User actions are bound to the instance they started on.** A command is not one atomic
step — Shutdown shows a modal, a rebuild probes the VM for its project list, a clone
runs for minutes — so re-reading "the active instance" in a later step lets a switch
redirect the rest of the action. The worst case is destructive: confirm *Shut down* for
A, switch to B while the modal is open, and the poweroff lands on B. So every command
entry point captures `actionTarget()` (`instances.captureTarget`) once and uses
`target.cfg` / `target.instance` throughout; before anything irreversible it calls
`targetSuperseded()`, which — when the window switched meanwhile — does **nothing** and
says which instance the action was for, because at that point we can no longer tell
which VM the user meant. (Same idiom as the pre-existing "changed elsewhere while this
prompt was open" guard in `offerApplyCheckpoints`.) Confirmation copy gains a
` (<name>)` suffix via `instanceLabel()` **only when more than one instance exists**, so
a single-VM install's prompts are unchanged. Bound this way: shutdown, reprovision,
reinstall/redownload (both the panel button and `customRebuild`), export, setCheckpoints,
add-project clone-then-open, and the agent update + its follow-up reprovision.

**The confirmation itself is an await, and `lifecycle.run` verifies the capture on the far
side of it.** The destructive modal opens *inside* `run()`, so a caller's pre-call
generation check says nothing about the moment the user clicks *Reinstall*: another window
can change the global selection, or the registry can be rewritten, while the dialog sits
open. Callers therefore pass `stillCurrent` — their captured-target predicate — and `run()`
re-asks it **after** `confirmDestructive` and **before** it clears the applied-checkpoints
marker or launches anything; an unusable predicate fails *closed*. The dialog also names
the instance for a non-default one (and states the endpoint for a `hyperv-remote` VM, which
lives on somebody else's host); the **default instance's dialog text is byte-identical** to
the single-VM one it has always shown.

**Start & connect** is the longest such flow — a credential lookup that can prompt, then an
SSH poll of up to 150 s — and it both *starts* a VM and *opens a window on it*. It captures
a target up front and re-checks it after the credential lookup, after every probe and
immediately before `openOnVm`. A supersession stops **quietly**: the VM was legitimately
asked to start and is left running, so the warning says it wasn't opened rather than
"nothing was done".

**Per-instance narrow messages are scoped, on both sides.** `saveIdlePolicy` PUTs to the
captured instance and re-checks the gate after the credential lookup *and* after the PUT
before caching or broadcasting (`readIdlePolicy` gates its cache write the same way). That
is only half of it: extension-side gating decides whether to **post**, and says nothing
about a message *already posted* and sitting in a webview's queue behind a full state push
for another instance. So **every per-instance narrow producer stamps `instance`** —

| message | stamped with | why it is per-instance |
|---|---|---|
| `idlePolicy` | the captured target | one VM's policy on one host service |
| `forwards` | `forwarderInstance` (else the active one) | the `ssh -L` transports terminate on one VM |
| `audio` | the mic session's slot owner (else the active one) | the `ssh -R` tunnel terminates on one VM |
| `settings` | the instance whose `scriptsDir` was just read | `.construct-settings.json` is per scripts dir |

— and the webview's message router drops any narrow message naming an instance other than
the one the last full `state` described. One generic line covers all four; `state` itself is
handled *before* it, because a state push is what **moves** the panel to another instance.
Every `{type:'audio'}` payload is built by the single `audioMessage()` helper (the string
literal exists in exactly one place), so no call site can forget the stamp; `settings` is
read and stamped in the same breath, so the label can never describe another instance's
file. `editProject` is deliberately **not** scoped: it is a direct reply to one webview's
own request about the *single* host config repo every instance shares (only the VM-side
branch is per instance — see [`../docs/config-sync.md`](../docs/config-sync.md)), so
stamping it would make a modal opened before a switch refuse to populate after one. **Zero
change:** a message with no `instance` still applies, which is the single-VM path.

**The same discipline covers the *deferred* steps** — the ones that resolve long after
the click, where re-reading the active instance is hardest to spot:

- **The config-sync tick takes its target** (`runConfigSync(target)`): the SSH cfg it
  reads and writes the VM store with, the `vmBranch` it commits and fast-forwards, the
  merge gate it completes, the scripts dir it auto-enables discovered profiles into, and
  the post-tick import scan all come from that one capture. The tick serializes
  window-wide (the repo lock is repo-wide), so a request that arrives during one is
  **queued by target** (`instances.createTargetQueue`) — held as one global promise, a
  "Sync Now" for A ran against whatever the window had switched to by the time it
  started, syncing B's branch and B's store while A's changes stayed unsynced. Every
  caller (Sync Now, the lifecycle pre-flight, the refresh pipeline, the `projects` file
  watcher, profile delete, the remote-config import) passes its own captured target.
- **A tick's STATUS and THROTTLE belong to that target too**
  (`instances.createSyncStatusStore`, keyed by the captured target's name). The timestamp
  and `TickResult` describe *one* instance's branch and VM store, so window-global they
  lied after a switch: `buildConfigSyncState` reported A's `lastSyncAt`, result, warnings
  and blocked reason under B's name, and `maybeAutoSync` saw A's stamp satisfy the
  five-minute throttle — suppressing B's *first* automatic tick for up to five minutes and
  leaving B's branch and store unsynchronized exactly when the user had just switched to
  it. A target that has never ticked is always due ("no stamp" is not "stamped at epoch
  0"). What stays window-global is what is genuinely repository-wide: the in-flight tick
  (`syncTickPromise`/`syncTickInFlight`) and the config repo's cross-process lock — one
  repo, one lock, so a tick for B still waits behind a tick for A. The store is pure and
  clock-injectable, so the A-syncs-then-switch-to-B case is driven with deferred promises
  in `extension/test/instances.test.js` rather than only being visible in a live window.
- **A stale capture aborts; it does not "carry on with the old VM".** The generation is
  re-checked **before every mutation an await could have outlived**: at the tick's entry,
  when a *queued* follow-up finally starts (the switch usually happens inside exactly
  that window), after the tick's own awaits and before **either** follow-on step
  (profile auto-enable *and* the VM import), after the import's SSH scan, after its
  deletion-history read and before the profile files or the per-instance throttle stamp
  are written, and once more immediately before the project selection is saved. When the
  window has moved on, **neither** instance is touched — writing B's file would put A's
  discoveries in it, and writing A's would act for a VM this window no longer drives;
  A's own next tick discovers the same profiles again. `targetStale()` is the quiet form
  of `targetSuperseded()`: a background tick logs why it stopped instead of toasting,
  while the user-facing flows that wrap it (the lifecycle pre-flight, the reprovision
  offer) keep their own visible guard.
- **The merge gate is bounded the same way, and fails CLOSED.**
  `configsync.completePendingMerge` *writes* — it creates the merge commit whose message
  names the branch — so `configMergeGate(target)` captures up front, re-checks
  immediately before that write and again after the repo reads, and a stale gate comes
  back `{ blocked: true, stale: true }` rather than `{ blocked: false }`. An
  indeterminate answer must never read as "the repo is clear": the destructive pre-flight
  cancels and says the window switched, while a background caller (*Open config repo*)
  simply skips its state refresh.
- **The scripts dir is part of the capture** (`captureTargetFull` → `{instance, cfg,
  scriptsDir, token}`, resolved through `resolveScriptsDirFor(instance)` *before* the
  first await): it holds that instance's `.construct-settings.json` (project selection,
  mic preference, patch toggles). Re-resolving it in the tail of a flow is what let an
  import of A — reduced at the time to a bare `{name, cfg}` — auto-enable A's newly
  discovered repos into B's settings file after a switch.
- **A prompt answered after a switch does nothing** — the non-modal "Reprovision now"
  offer captures `scriptsDir` *and* the target before the toast, and a stale answer goes
  through `targetSuperseded` rather than rebuilding the wrong VM with the wrong scripts.
- **The mic auto-arm discards its own result**: instance, cfg, scripts dir and generation
  are captured before `micPassthrough` is read, and the reachability probe's answer is
  dropped if the window switched while it was in flight — A's "yes" must never install
  the shim and open a microphone tunnel on B, whose preference may be off. The decision
  itself is the pure `instances.planCapturedFollowUp`, shared with the prompt above and
  unit-tested with deferred promises.
- **The mic tunnel's HANDOVER is serialized, and its status is owned by one session.**
  A switch does two slow things to two different VMs — tear the old tunnel down (stop the
  `ssh -R`, then revert the shim on that VM over SSH) and arm the new one (read *its*
  preference, probe *it*). Started concurrently, they lost the new VM's tunnel three
  ways, so all three are structural now:
  1. the destination is **always** evaluated (`instances.planHandover`). Arming only
     "when a tunnel for another instance exists" meant that a startup arm which produced
     none — instance A unreachable, or its preference off — made the switch to B do
     nothing at all, and B's saved `micPassthrough` was ignored for the rest of the
     window. It is skipped only when a live tunnel *already* belongs to the destination.
  2. `disableAudio()` is **awaitable** and every switch goes through one chain
     (`instances.createHandover`), so the arm starts after the instance we left has let
     go — and A→B→C tears A down once, supersedes B's arm through the same
     `targetSuperseded` rule as every other deferred step, and arms C, with no tunnel
     left behind.
  3. every status a session emits — `HostAudio.onStatus`, the enable result, the
     teardown's final "disabled" — carries the **slot claim** it was armed under
     (`instances.createSessionOwner`), and `broadcastAudio` drops one whose claim a later
     enable superseded. Ungated, A's trailing `{enabled:false}` painted the console
     switch off over B's live tunnel, and A's failed enable cleared the module's
     reference to B's `HostAudio` — leaking B's tunnel with nothing left to dispose it.
     A status with **no** claim (the "there is nothing armed" call sites) always goes
     out, which is what keeps the single-VM path unchanged.
  4. **the MANUAL operations ride the same chain** — the console/settings toggle
     (`requestAudioEnable` / `requestAudioDisable`) and the startup and repatch auto-arms
     all queue on it (`createHandover.enable` / `.disable`), and a HostAudio is only ever
     constructed from inside it. Run *beside* the chain, a manual "on" that
     arrived while a switch was tearing A down saw no session (the reference had already
     been dropped), built one for B, and B's queued auto-arm then built a **second**
     `HostAudio` for the same VM: the newer claim took the slot, the first enable's result
     was discarded as superseded, and nothing was left that could disable it — an orphan
     `ssh -R`. On the chain the second enable can only **join**: `enableAudio` treats a
     live `HostAudio` whose enable is still in flight as already pending and awaits *that*
     promise instead of claiming the slot again. The "off" is queued for the mirror
     reason — it must not overtake an "on" still waiting in the queue and leave the tunnel
     that enable opens behind.
     The decision is the pure **`instances.planEnable(slotState, name)`** →
     `create | report | join | refuse`, read from one helper (`audioSlotState()`: the four
     module variables plus the chain's shutdown flag), so the branch is driven by tests
     rather than only by a live window.
  5. **shutdown is part of the same ownership, in two layers.** `deactivate()` cannot
     await SSH, so it disposes the one live `HostAudio` **directly** — the single
     exception to "only the chain touches a session" — and two things make that safe:
     - `audioHandover.close()` runs **first**, refusing every step still queued
       (`reason: "closed"`, its `run` never called) and publishing `closed`, which feeds
       `planEnable`: a step already *running* — an auto-arm sitting in its reachability
       probe — gets `refuse` on the way out instead of constructing anything.
     - that still cannot stop an enable that has **already reached** `HostAudio.enable()`
       and is waiting on SSH: `dispose()` tears down what exists at that moment, which
       mid-enable is nothing, and the continuation would then bind its server and spawn
       `ssh -R` behind it — a live tunnel with no reference left in the extension host.
       So `dispose()` sets a **cancellation flag** (`src/audio.js`) that `enable()`
       re-checks **after every await** — the remote enable, the local `listen`, and the
       tunnel's settle window — rolling back what it had built (kill the child, close the
       server, run the guarded VM-side revert if step 1 already mutated it) and returning
       `{ ok: false, error: "disposed" }`. A disposed object also refuses any later enable.
     All of it is driven with deferred promises against the **real** `HostAudio` (injected
     ssh/net/spawn): each of the three await points, plus controls showing the tunnel
     appearing without the cancellation.
  The re-evaluation is gated on the destination having actually changed
  (`audioTargetInstance`, the instance the arm was last evaluated for — not
  `hostAudioInstance`, which is null in exactly the case that needs evaluating), so a
  window that never switches never re-arms. All the orderings (no prior tunnel, a
  delayed teardown, rapid A→B→C, the no-switch control, a manual enable during a
  teardown, an auto-arm behind a manual one, an "off" behind an "on") are driven with
  deferred promises in `extension/test/instances.test.js` — each with the pre-fix shape
  as a control, so the model demonstrably still catches the bug it was written for.

**UI.** A status-bar item (`construct.switchInstance`) shows the active instance and is
**hidden when only one instance exists**; the command *The Construct: Switch Instance*
opens a QuickPick; the panel header renders a `<select>` **only when
`state.instances.length > 1`**. A single-VM install therefore sees no new UI at all.

**Switching** (`onInstanceChanged`) invalidates every VM-derived cache (the in-flight
probe, the usage table, git detection, the config-sync snapshot), reconnects the
notification watcher, hands the mic tunnel over to the new VM (see above — teardown then
arm, on one serialized chain), lets the port forwarder go on ITS chain (the destination's
own is started by the first reading that says that VM is up — §Forwards), and re-probes — the panel must never show one VM's pills,
versions, projects or usage under another's name. The handover runs in the background:
the panel refresh is not held behind an SSH teardown.

### One transport helper: `activeCfg()`

`ssh.js` has always accepted a `cfg`, and every module honoured it — but no caller ever
passed one. Now **every** call that reaches a VM goes through the single `activeCfg()`
helper in `extension.js` (`probe`, `runRemote`/`runRemoteScript`/`isReachable`,
`remote.isConnectedToVm`/`openOnVm`/`shouldAutoOpenPanel`, `usage`, `t3code`, `audio`,
`notify`, `repatch`). That is deliberate: one helper is the thing that makes it *hard to
forget*, so a new call site can't silently fall back to the hardcoded default VM.
`ssh.buildSshArgs` emits `-p <port>` **only for a non-22 port**, so the default
instance's argv is byte-identical to the pre-instance build (same rule in
`notify.buildWatchArgs` and `audio.buildTunnelArgs`).

Cross-package options this batch passes (implemented by siblings; ignored until then,
which is harmless because the default instance's value *is* each function's own
default): `vmpower.queryVmState/queryAutoCheckpoints/startVm({instance})` (B4) and
`configsync.syncTick({vmBranch})` (B5).

### Per-instance lifecycle invocations

`lifecycle.buildInvocation(action, {instance, instanceParams})` emits target-identity
arguments **only for a non-default instance**, and only for the parameters the
*installed* script declares (`instanceParamSupport` → `scriptSupportsParam`, the same
comment-stripped declaration probe the other capability gates use). Per script:

- **`Provision-AgentVM.ps1`** (reprovision, exportConfig) — `-VmHost -HostAlias
  -SshPort -LocalKeyName`. It dials the VM itself, so it needs the whole endpoint;
  `-LocalKeyName` rides along because otherwise the provisioner would write the
  *default* instance's key file over this instance's.
- **`Auto-Install.ps1`** (reinstall, redownload) — **`-VmName` and nothing else.**
  Auto-Install derives the guest hostname, alias and key name from `-VmName`, and it
  *throws* on a `-VmHost` that disagrees with it. It does declare `-VmHost`, so a naive
  "emit whatever the script declares" would break every non-default rebuild — hence an
  explicit per-action parameter list rather than one shared set.
- **`Set-AgentVmCheckpoints.ps1`** (setCheckpoints) — `-VmName`; it only talks to Hyper-V.

**Version skew fails CLOSED for a non-default instance.** Dropping an identity
parameter the installed script doesn't declare does not "degrade gracefully" — it
*retargets* the action at whatever the script defaults to, i.e. the DEFAULT VM (an
Auto-Install.ps1 without `-VmName` rebuilds `Agent-VM`; a Provision-AgentVM.ps1 without
the endpoint re-keys the default VM). So `REQUIRED_INSTANCE_PARAMS` states, per action,
what must be declared, and `checkInstanceSupport` returns a structured refusal
`{blocked, reason}` — surfaced by `run()` as "update the Construct scripts" — instead of
an invocation. The *default* instance is never blocked: it needs no targeting, and its
argv stays byte-identical.

**Name-only targeting (B11, plan §4.12).** Once the installed scripts can resolve a name,
all of the above collapses to **one argument**: `-InstanceName <name>`, and the script reads
the endpoint, alias, port, key file, config-sync branch and host-service URL out of the same
registry the panel did (`lib/AgentVm.Instances.ps1` — the two halves cannot disagree, because it is one
file). `NAME_TARGET_PARAMS` is the emitted set, `REQUIRED_NAME_TARGET_PARAMS` the gate (the
name, and nothing else — everything the four arguments stated is derived from it), and
`-ConfigBranch` stays alongside it as the capability marker for instance-keyed config sync,
carrying the very field the resolver reads.

**The probe is a FILE, not the parameter.** `-InstanceName` already existed on
`Provision-AgentVM.ps1` (the remote service identity, `CONSTRUCT_INSTANCE_NAME`) and on
`Auto-Install.ps1` (the remote instance name, which a *local* run used to refuse outright),
so "the script declares `$InstanceName`" is true on B7-era installs where it means something
else and the action would land on the default VM. `supportsNameTargeting` therefore asks for
`lib/AgentVm.InstanceTarget.ps1` (`INSTANCE_TARGET_LIB`) — the adapter every host script
resolves the name through, which ships with the new meaning and nothing else — *and* for the
parameter's declaration (the file alone could have been dropped in, and an undeclared
argument is a binding failure). Absent → the four-argument form above, which those installs
do understand. `instanceParamSupport` returns whichever set it probed, so the probe result
itself says which form the caller is in (`usesNameTargeting`). A remote *rebuild* is
excluded: it must also state its host service, so it keeps `REMOTE_INSTANCE_PARAMS`.

**Backend capability gate.** The host scripts drive the LOCAL Hyper-V, so
reinstall / redownload / setCheckpoints are refused for any backend whose driver does
not declare `hostLifecycle` (`drivers/index.js` `lifecycleSupport`, capability table
next to the dispatch — a future remote driver re-enables them by *declaring what it
can do*, not by editing `lifecycle.js`). Without the gate, "Reinstall" on a
`hyperv-remote` instance would delete a LOCAL VM that merely shares the name.
reprovision and exportConfig are pure SSH to an already-running VM and stay allowed for
every backend.

**`-ConfigBranch` is threaded conditionally — and *required* by every branch-writing
action.** JS config sync uses `instance.configBranch` verbatim while
`Provision-AgentVM.ps1` derives its branch from `-HostAlias`, so an instance whose branch
disagrees with that derivation would be initialised on one ref and synced on another.
`configBranchOverride` (the JS mirror of `Get-ConstructConfigBranchName`) emits
`-ConfigBranch` for reprovision/reinstall/redownload **only when the two differ** — never
on the default path.

Its *declaration* is the **capability marker** for instance-keyed config sync, so
`checkInstanceSupport` requires it for every non-default reprovision/reinstall/redownload
even when there is no value to emit. A script that predates the parameter has no
per-alias derivation either: it would initialise and sync `work-vm` on `refs/heads/vm`
while the panel syncs `refs/heads/vm-work-vm` — the canonical `vm-<name>` case, where
nothing would have been emitted, is exactly the one that used to slip through the gate.

**Only branch-writing actions are gated.** `exportConfig` is not: it runs
`Provision-AgentVM.ps1 -Action export`, which returns *before* the config repo is
initialised and before the sync tick, so it can never land on the wrong ref — it carries
no `-ConfigBranch` (`INSTANCE_PARAMS`) and is never refused for lacking one, and a
non-default export therefore keeps working against older scripts. The PowerShell chain
draws the same line: `Auto-Install.ps1` → `Create-AgentVM.ps1` → `Provision-AgentVM.ps1`
(and Auto-Install's reprovision / add-config paths straight to Provision),
probe-before-splat, with the probe for a destructive rebuild done *before* anything is
deleted. Auto-Install demands `-ConfigBranch` from the installed provisioner — and a
`-VmBranch`-capable library — for a non-default VM on every action that provisions or
syncs (reprovision, reinstall, redownload, add-config, a fresh install, and an *unbound*
`-Action`, where the interactive menu's choice is not known yet and the check has to
precede the delete); `-Action export` is the single exemption.

**Values are quoted structurally.** `buildInvocation` builds its arguments as
(flag, value) pairs (`argSpec`); `buildCallCommand` emits parameter *names* bare and
single-quotes every *value*, whatever it starts with. Registry fields are hand-edited,
and the non-elevated launch embeds them in a PowerShell command string, so a `vmHost` of
`-x; Start-Process calc; #` must be data, not syntax. The default path's command string
is unchanged.

A scripts dir that predates B1 declares none of them, so for the DEFAULT instance the
probe yields `[]` and the action runs against the script's own defaults (exactly today's
single-VM behaviour) instead of failing to bind.

`host.resolveScriptsDir` gained a third, most-specific source: the active instance's
pinned `scriptsDir`, then the `construct.scriptsDir` setting, then newest-install
detection. `.construct-settings.json` stays **per scripts dir** — two instances that
share a scripts dir deliberately share its settings.

### Collision analysis: notifications and mic passthrough (§4.8)

Both features hold a long-lived SSH connection to *one* VM. The question this batch had
to answer is whether two instances can collide; the answer is **no, on the VM side** —
so nothing was refactored speculatively:

- **Notification spool** (`/run/construct/notify` on the VM). It is per VM by
  construction, and the claim protocol's ids are the VM-side `$$`, so two *windows*
  watching the *same* VM already de-conflict (that was true before instances). Two
  windows on two instances watch two different spools on two different machines —
  nothing shared. The only real change is that the watcher must follow the active
  instance: it is opened with `activeCfg()` and reconnected on a switch
  (`notifyInstance` records which VM it is attached to).
- **Mic passthrough** (`audio.js`). The VM-side tunnel port range `8767–8774` is
  **per-window de-confliction inside one VM**, not per-VM: two VMs each have their own
  `127.0.0.1:8767+`, so cross-instance collision is impossible. The host side binds an
  **ephemeral** port (`listen(0)`), so it cannot collide either. The only real change is
  again targeting: `HostAudio` is constructed with `activeCfg()`, `hostAudioInstance`
  records the VM the tunnel terminates on, and a switch tears the tunnel down and lets
  the saved `micPassthrough` preference re-arm it against the new VM (quietly, under the
  same rules as the startup auto-arm). Its default key-existence probe resolves against
  **`this.cfg`**, not the module defaults — otherwise the enable script would install
  the shim over `construct_<name>_ed25519` while the persistent tunnel decided "no key",
  fell back to a `~/.ssh/config` alias a direct-cfg instance need not have, and failed
  with the shim already in place.
- **Usage cache.** This one *was* a real collision: `usage.js` memoized by report
  granularity alone, so switching instances would have served the previous VM's numbers
  under the new heading. The cache key now includes the endpoint
  (`usage.cacheKeyFor`); with no cfg it stays the bare report key, i.e. unchanged.

Module rules followed for the new code (plan §4.8): `src/instances.js` is **free of the
vscode API** (it takes `env`/`path`/injected `readFile`, and the extension layer owns
every toast, the status bar and the settings read); the **transport is injected** — no
module opens its own connection, they all receive the instance `cfg`; **state is
namespaced per instance** (workspaceState key, usage cache key, `notifyInstance` /
`hostAudioInstance`); and the registry file format is a **documented contract** shared
by the JS and PS readers rather than an implementation detail.

## Remote hosts (`hyperv-remote`)

A remote instance is one whose VM lives on somebody else's Hyper-V, managed by the
`constructd` service (`service/README.md`, plan §4.4). The end-user/admin guide is
[`docs/remote-host.md`](../docs/remote-host.md); this section is the extension side.

**The extension is a management UI, never an authority.** The service owns the VM record,
the port forwards and the idle policy, so the panel *reads* them and *asks* for changes.
Nothing about a remote VM is recoverable only from this PC — that is the PC-independence
requirement (plan §1), and it is why "Add Remote Host" stores an enrolment, not state.

### `src/remotehost.js` — the API client

Built to the §4.8 module rules: the **core is free of the vscode API**. Every dependency
is injected (`fetchImpl`, `spawnImpl`, `tlsConnect`, `secrets`, `confirm`, `prompt`,
`log`), so the whole client unit-tests under plain node with a fake `fetch`, and the
extension layer supplies the vscode adapters (SecretStorage, modal, input box, output
channel). It owns no connection it did not receive.

Three credential providers, one shape (`{ kind, … }`), chosen per host:

| provider | how |
|---|---|
| `token` | `Authorization: Bearer <secret>`; the secret comes from VS Code **SecretStorage** under `construct.remote.token:<hostslug>`. Pure HTTPS from Node. |
| `negotiate` | Node has no SSPI, so the request is delegated to a spawned `powershell.exe -EncodedCommand` that dot-sources `lib/AgentVm.Remote.ps1` and calls `Invoke-ConstructApi` with `-UseDefaultCredentials` — the same encoded-command pattern `vmpower.js` uses for `Get-VM`. It prints one JSON envelope on stdout, which the client parses. |
| `credential` | prompted domain user + password, handed to the same PowerShell helper as a `PSCredential`. Never persisted. |

The credential itself is **supplied by the extension layer, never fetched by the client**:
`remotehost.js` has no `vscode`, so `extension.js`'s `driverOpts(instance)` reads the token
out of SecretStorage and resolves the path of `lib/AgentVm.Remote.ps1`, and passes both to
every driver call (`vmpower.queryVmState`, `startVm`). A local instance gets `{}` and is
byte-for-byte unchanged. A **token instance with no stored token is refused**, not quietly
downgraded to `negotiate`: swapping the credential would ask the service a different
question and report the answer as if it were about the token.

**Certificate pinning** in Node happens on the **socket**, in an `https.Agent` whose
`createConnection` connects with `tls.connect` (`rejectUnauthorized: false` — a self-signed
certificate has no chain), compares `fingerprint256` with the pin, and hands the socket to
the HTTP layer **only on a match**; a mismatch destroys the socket and fails the connection,
so the request never exists and the `Authorization` header is never written. It is
deliberately *not* done in `checkServerIdentity`: Node does not call that hook when
`rejectUnauthorized` is off, so a pin checked there is not checked at all (a live-TLS test
in `test/remotehost.test.js` asserts the header does not reach a mismatched server). SNI is
omitted for IP literals, which TLS forbids as a ServerName. The PowerShell providers pin
inside `lib/AgentVm.Remote.ps1` (`docs/remote-host.md` §5 explains why 5.1 and 7 differ).
Either way, **no pin → no call**: an unpinned host is refused with "run Add Remote Host
first", and a *changed* fingerprint is a hard failure naming both values, not a prompt to
click through.

**Plain `http` is loopback-only.** Without TLS there is nothing to encrypt a bearer token
with and no certificate to pin, so both clients refuse an `http://` URL for anything but
`localhost`/`127.0.0.0/8`/`::1` — in JS when the client is *constructed*, in PowerShell at
the top of `Invoke-ConstructApi`, i.e. before a credential is selected. The loopback
exception is what lets the tests drive the fake service.

Errors are mapped once, centrally, so every caller can branch on a `status` rather than on
a message: `401` → "the host rejected these credentials" (the one the enrolment flow falls
back on), `403` → "not enrolled / not your VM", `404`, `409` (the endpoint of a VM whose
forward does not exist yet), and RFC 7807 `detail` text when the body is a problem
document. A network failure carries `status: 0`.

### `src/drivers/hyperv-remote.js`

Implements the §4 driver contract over the API:

| member | remote behaviour |
|---|---|
| `queryVmState` | `GET /vms/{name}/state`; `running` → `running`, `off`/`saved`/`paused` → `off` (a start resumes them), `404` → `absent`, anything else → `unknown` |
| `queryAutoCheckpoints` | `"unsupported"` — no probing. Checkpoints are not a capability of this backend, so the panel must not ask. |
| `startVm` | `POST /vms/{name}/power {"action":"start"}` — no UAC, no elevated console; the service does it |
| `capabilities` | `{ checkpoints: false, console: "none", suspend: true, hostLifecycle: true }` |

`hostLifecycle: true` is the interesting one. It says "the host's own scripts *can* create
and delete this backend's VMs" — true since B7, because `Auto-Install.ps1` gained the
remote path. But `setCheckpoints` must stay refused, and it is one of the same
`HYPERVISOR_ACTIONS`. So `drivers/index.js` `lifecycleSupport` now asks **two** questions:
`hostLifecycle` for every hypervisor action, **plus `capabilities.checkpoints` for
`setCheckpoints`** specifically. `hyperv-local` declares both and is unchanged; the
unknown-backend driver declares neither and is unchanged; `hyperv-remote` gets
reinstall/redownload and keeps checkpoints refused — with a reason that says *why* (`the
"hyperv-remote" backend has no checkpoints — that setting applies to VMs on this PC's
Hyper-V only.`) rather than the `hostLifecycle` refusal, which would claim the host
scripts cannot drive this backend at all.

### Per-instance lifecycle invocations for a remote instance

`Auto-Install.ps1` reaches a remote VM by **instance name plus service URL**, not by
`-VmName`: the local `-VmName` path derives a guest hostname and an mshome address that do
not exist for a remote VM, and it runs the local skew guards. So `INSTANCE_PARAMS` is
backend-aware for the rebuild actions:

| action | local instance | remote instance |
|---|---|---|
| `reinstall` / `redownload` | `-VmName -ConfigBranch` | `-Backend -ServiceUrl -InstanceName -ConfigBranch` |
| `reprovision` / `exportConfig` | `-VmHost -HostAlias -SshPort -LocalKeyName (-ConfigBranch)` | **identical** — provisioning is pure SSH to the endpoint, whoever created the VM |

(On an install with name-only targeting the local column — and both `reprovision` /
`exportConfig` columns — become `-InstanceName (-ConfigBranch)`. The remote *rebuild* column
does not: `-Backend` is what makes `Auto-Install.ps1` take the remote path at all, and
`-ServiceUrl` names the host service.)

`REQUIRED_INSTANCE_PARAMS` follows: a remote rebuild is **refused** unless the installed
`Auto-Install.ps1` declares `-Backend`, `-ServiceUrl` *and* `-InstanceName`, and unless the
instance actually carries `service.url`. That is the same fail-closed rule as B3 and for the
same reason — an `Auto-Install.ps1` that silently drops `-ServiceUrl` does not "degrade": it
runs the **local** path and rebuilds a local VM named after the remote one. `redownload` is
mapped onto the remote reinstall (the service owns its source image); the panel keeps both
buttons because the refusal/confirmation copy is shared.

**Elevation is backend-aware too.** A remote rebuild is launched with `elevate: false`: it
creates no local VM, so it needs no administrator rights — and on a PC where UAC switches to
a *different* admin account, the elevated console would read and write the DPAPI token
store, `instances.json` and `~\.ssh` under that account's profile. `Auto-Install.ps1` makes
the same call for the same reason (it skips its own relaunch on the remote path), but the
launcher has to agree: if it elevated anyway, the script would already be in the wrong
profile before it could decide. Local rebuilds — and the default instance — are unchanged
(`elevate: true`; they drive Hyper-V).

### Commands

* **`construct.addRemoteHost`** — URL → fingerprint → auth → `whoami`. The enrolment
  (`url`, `auth`, `fingerprint`, `identity`, `addedAt`) is stored in **`globalState`** under
  `construct.remoteHosts`, and a token in SecretStorage. Deliberately **not** in
  `instances.json`: that file describes *VMs* — an entry for a host with no VM would appear
  in the instance picker as a machine nothing can reach, and both readers would have to
  invent a meaning for it.
* **`construct.registerThisVm`** — offered (as a panel banner and in the palette) when this
  window is attached over Remote-SSH to a host **no registry entry describes**
  (`instances.planRegisterAttachedVm`, which defers to `planRemoteAdoption` so "unknown"
  means one thing). It asks for a name — the ssh host's first label when that is a usable
  one — validates it with the one name rule, and writes the entry through
  `instances.addInstance`/`save`, i.e. the same writer and the same rules the PowerShell
  installer uses. `planLocalRegistration` is where it can REFUSE: a `hyperv-local` entry's
  identity is *derived* from its name, so a window attached to `buildbox.example.local` has
  no name for which a local entry describes that machine — inventing one would produce an
  instance the reader drops on the next load, or one that aims every lifecycle action
  somewhere else. Such a host goes through **Add remote host** instead. All of the decision
  is pure and unit-tested; `extension.js` only reads `vscode.env.remoteAuthority`, shows the
  input box and adopts the result.
* **`construct.newRemoteVm`** — pick an enrolled host, ask name/CPU/RAM/disk, then launch
  `Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl … -InstanceName … -VmMemoryGB …
  -VmDiskGB …` through `lifecycle.launchHostScript`. The console does the create *and* the
  provisioning, because provisioning configures this PC (ssh config, Remote-SSH, OpenCode,
  SMB) and cannot be done by the service.

Both commands are no-ops off Windows (the launcher and the Negotiate helper are
`powershell.exe`), and both are absent from a single-VM install's daily path — they only
appear in the command palette.

### Panel

`instanceState()` carries `backend` and, for a remote instance, `serviceHost` (the host
part of the service URL — never the whole URL, which can carry a port and is noise in a
one-line row). `media/panel.js` renders two extra **System** rows and keeps them `hidden`
for `hyperv-local`, so a single-VM install's panel is pixel-identical to before.

### Several VMs on one host service

The registry's endpoint identity is the **composite `(sshHost, sshPort)`**
(`UNIQUE_FIELDS` / `collisionProblems`), so the VMs a host service runs for you — one
address, one allocated forward each — are distinct instances and all load. Only the same
host **and** port is "one machine under two names", and that still drops both entries.

`Auto-Install.ps1`'s remote path asks the registry twice, both times through the shared
rules in `lib/AgentVm.Instances.ps1` (`Get-ConstructInstanceEntryProblem` +
`Get-ConstructInstanceCollision`, reached by `Get-ConstructRemoteInstanceConflict`), never
through a copy of them:

1. **before it asks the service for anything** — the name and the identities *derived*
   from it (`vmName`, `hostAlias`, `keyName`, `configBranch`). The endpoint is excluded
   (`-ExcludeLabel 'sshHost/sshPort'`, the one caller-side filter the PS twin has) because
   the service has not allocated the forward yet; judging the host alone here is what used
   to refuse a perfectly valid second VM on a shared host.
2. **immediately after the create**, on the entry that will be written — the full rule
   set, endpoint included, since the service's advertised `PublicHost` can differ from the
   URL's host and the port does not exist until the VM does. A conflict rolls the create
   back (the same `DELETE` the reinstall path uses) rather than stranding a VM this PC
   could never reach or rebuild.

Several *users* on one host are unaffected either way (each has their own PC and
registry), and so are several hosts.

## Forwards (`construct expose`)

An agent on the VM runs `construct expose 5173` and hands the user the link it prints.
The port it names lives **inside the VM**; the link points at the **user's PC**, because
`src/forwarder.js` opened a local port here and tunnelled it over the SSH connection this
extension already knows how to make. This section is the extension half. The wire formats
— the guest spool and the service's forward routes — are the CONTRACT, and they are
specified once in [`docs/expose.md`](../docs/expose.md); nothing here restates them.

**The forwarder is a client tool module (plan §4.8), built module-first.** It is the first
feature written to all four rules from day one, so it is also the worked example the older
modules are meant to converge on:

1. **Core free of the VS Code API.** `src/forwarder.js` has no `require("vscode")` and no
   `require("child_process")`. It is pure logic plus an injected transport, so the whole
   local and remote flow unit-tests under plain node.
2. **Transport injected, never owned.** The module receives one object and calls nothing
   else; `src/forwarder-ui.js` builds the real one out of `ssh.js`, `child_process`, `net`
   and (remote mode) a `remotehost.js` client, and is the only file that touches `vscode`.
3. **Own state, keyed by instance.** One `Forwarder` per instance name, its own spool
   directory on that VM, its own tunnel table and its own claim marker. It reads no other
   module's files or globals.
4. **A documented contract** — `docs/expose.md` for the wire, this section for the module.

### The transport interface

```
{
  runRemoteScript(script, opts) -> Promise<{code, stdout, stderr}>   // one-shot SSH
  spawnWatch(script)            -> child                             // long-lived SSH stream
  spawnTunnel({localPort, vmPort}) -> child                          // long-lived `ssh -L`
  probePort(port)               -> Promise<boolean>                  // free on THIS PC?
  fetchJson(method, path, body) -> Promise<any>                      // remote mode only
}
```

A `child` is anything with `stdout`, `on("exit"|"error")` and `kill()` — the tests pass
fake emitters, so no test spawns a process or opens a socket.

`ssh.js` gained exactly one thing for this: `buildLocalForwardArgs(cfg, localPort, vmPort,
hasKey)`, the `ssh -L <local>:127.0.0.1:<vm> -N` argv. It is the mirror of `audio.js`'s
`-R` builder and shares its rules — `-N`, keepalives so a dead link makes the child exit,
`ExitOnForwardFailure=yes` so a busy local port fails fast instead of pretending, and `-p`
only for a non-22 port so the default instance's argv is unchanged.

### The planner

`planActions({ requests, acks, closes, tunnels, ... })` is pure and is where every decision
about *what to do* lives; the `Forwarder` only executes what it returns. Actions:

| action | when |
|---|---|
| `open` | a request with no ack and no live tunnel |
| `ack` | a tunnel is up for a request whose ack is missing or disagrees (a re-ack after a reconnect, which `docs/expose.md` explicitly allows) |
| `error` | opening failed; write `status:"error"` with a safe message |
| `close` | a close document, a request that vanished, or (remote) an entry that left the list |
| `sweep` | an ack or close document with no request behind it — remove the leftovers |
| `adopt` | (remote) somebody else's ack names a port that is not ours → drop our tunnel |

Because it is pure, "a request that is already acked and already tunnelled produces no
actions" is a unit test rather than a hope — that idempotence is what makes it safe to run
the planner on every inotify event, every 30 s poll and every connect.

### Port selection, and where the port actually listens

The link is only honest if the port in it is the port that opened, which is why the CLI
waits for the ack instead of printing `vmPort` itself. The policy: **prefer `vmPort`**, so
the common case prints exactly the number the agent asked for; if it is taken on this PC,
take the first free port in **18800–18815**, skipping ports this instance's other tunnels
already hold. Nothing free → an `error` ack naming the problem, not a silent hang.

**The bind address is always explicit, and it follows the one setting that exists.**
`ssh -L` is emitted as `-L <bind>:<local>:127.0.0.1:<vm>`, never address-less: an
address-less `-L` means loopback only until a `GatewayPorts`, a `LocalForward` in a
matching `Host` block or a future ssh default says otherwise, and the privacy of a default
forward should be a property of our argv rather than an assumption about the user's
configuration. So:

| `construct.forwards.hostLabel` | binds | link |
|---|---|---|
| empty (default) | `127.0.0.1` | `http://localhost:<port>/` — private to this PC |
| set, e.g. `alice-pc` | `0.0.0.0` | `http://alice-pc:<port>/` — reachable at that name |

One setting, one consistent meaning (`bindHostFor`, read by the argv, the port probe *and*
the rendered link, so they cannot disagree). **Changing it across that boundary restarts
the live tunnels**, because a running `ssh -L` captured its bind address when it was
spawned: re-acking alone would be a lie in both directions, and one of them is a security
bug — clearing a label would advertise `localhost` while ssh kept listening on `0.0.0.0`,
i.e. the port stays exposed after the user opted out. So the child is killed, respawned on
the **same local port** (the guest may already hold that number), and the new ack is
written only once the replacement has survived its settle window. A label→label change
leaves the bind address alone and is therefore a re-ack only — killing a working tunnel to
change the text of a link would be pure churn. The label is the opt-in: advertising this PC
under a name that only this PC can open would be a dead link, which is worse than having
no setting. `normalizeBindHost` is an allow-list of exactly those two values — a listening
address is the last thing that should be "whatever the caller passed". The port probe binds
the *same* address, because probing loopback and then binding `0.0.0.0` would call a port
free that is not.

The range is a *host*-side de-confliction range, like the mic tunnel's 8767–8774 but for
the opposite direction; two instances cannot collide inside it because the planner is told
which ports are in use and every `Forwarder` probes the real socket before binding.

### Spool ownership: one window writes

Several VS Code windows can watch one VM, and every one of them can see the same request.
They must not each open a tunnel and each overwrite the ack, so **exactly one window owns
an instance's spool** and the others are read-only.

Mutual exclusion comes from **`mkdir "$d/.owner.lock"`**, which either creates the
directory or fails, atomically, with no window in between. Write-then-read-back is *not*
sufficient on its own and was the first version's bug: with A-write, A-read, B-write,
B-read, both windows read their own id back and **both** believe they own the spool. So the
lease transaction — read the record, decide, replace it — runs inside that lock, and the
record `<forwards>/.owner` (`<windowId> <unix-seconds>`) is then read once more *outside*
it. A window that could not take the lock, or could not write the file, therefore reports
whatever is actually there rather than what it hoped for.

A window claims when the record is missing, already its own, or older than **90 s** (three
missed 30 s reconciles, i.e. only ever a window that is gone). A lock left behind by a
window that died mid-transaction is broken once it is older than **60 s** — the transaction
it guards is three filesystem operations, so a live lock is never that old, and without
this the spool could wedge forever. On dispose the record is removed if it is still ours,
so the next window takes over immediately instead of after the TTL.

The property that matters is *at most one owner*, and it is a property of concurrent
execution rather than of the source — so `forwarder.test.js` **executes the generated
script for real**, five windows at a time, 25 rounds, and asserts that no round ever
elects two.

A non-owner window still renders the list — it is the same PC and the same ports, so the
links the owner opened work from any window — but it writes nothing **and may not close
anything**: deleting the owner's request/ack documents would tear down a forward the owner
still believes it is serving (and would then re-open on its next reconcile). The core
refuses it; the panel also disables the button, because the webview is untrusted input.

Remote mode has no spool and therefore no marker: **the ack itself is the claim.** A window
opens a tunnel for an entry that has no ack yet, and if a poll then shows an ack whose
`localPort` is not the one it opened, another window won and it drops its tunnel (`adopt`).

`readForwardList` returns three kinds of thing, because a remote list contains three:
client entries (the planner's input), **host entries kept for presentation only** — the
service materializes those itself and answers `409` to an ack for one, but they are still
this VM's forwards and dropping them meant the panel could not show, open or close a
`construct expose --to host` forward at all — and entries the service reports as
**closed**, which become closes rather than requests so a finished record can never be read
as a live one and re-opened.

An entry that is *already* acked is the interesting case, because the ack is a promise the
guest has already collected: it printed that link and stopped looking. So the forward may
only be re-established **on the very same port, or not at all** — and this window cannot
tell its own stale ack from another live window's. The port itself answers that: the
reclaim is conditional on `localPort` still being **free on this PC**. Free means nobody
here is serving it (the window that was has gone), so taking exactly that port restores the
exact link the agent printed — and needs no second ack, because the surviving one already
says so. Busy means somebody is serving it, and it is left alone. A reclaim never falls
back to another port, which is the whole difference from the local re-open: locally the
spool is ours alone, so falling back and re-acking is honest.

### Local mode

One long-lived `inotifywait -m` over SSH on `requests/` and `close/` — the `notify.js`
pattern, including the heartbeat that reaps an orphaned watcher through SIGPIPE and the
reconnect backoff. It carries **no data**: it prints one `CHANGED` line, and the host
answers with a reconcile. Forward changes are rare (a human or an agent typing `expose`),
so one SSH exec per change buys a much smaller protocol than streaming the spool would.

The reconcile is one script that claims ownership and base64-dumps `requests/`, `acks/` and
`close/` in a single round trip. It runs when the forwarder starts, on every `CHANGED`, and
**every 30 s regardless** — which is what keeps a VM without inotify-tools working, and what
refreshes the ownership claim.

Requests survive reboots by design (the spool is under `/etc/construct`, not `/run`), so
"re-open everything still queued" is not a special case: the first reconcile after a
connect sees requests with no live tunnel and opens them.

### When the forwarder runs at all — lazy, and gated on the guest

`construct.forwards.enabled` defaults to `true`, because a feature the agents are told to
use cannot need configuring first. But **enabled is not "start something"**: activation
spawns nothing forwarding-related — no watcher, no reconcile, and no probe of its own.

The trigger is what this window's *existing* status flow has already established about the
instance a refresh was captured for (`probeOnce` + `withVmState`, the reading the panel
renders anyway), plus the one reachability fact activation gets for free: a window
**attached to the VM over Remote-SSH** knows that VM is up, because its own connection
terminates there. `forwarder.planLifecycle` is the pure decision and `noteForwarderPresence`
carries it out:

| the status flow says | what happens |
|---|---|
| reachable, nothing armed for it | start (once per connect — an older guest is not re-asked every 30 s) |
| reachable, already armed | nothing |
| unreachable (whatever the VM's power state says) | stop: kill the watcher and the tunnels, hand the claim back, clear the armed edge |

The armed edge is set when the start is *asked for*, so there is a second decision when the
guest answers, and it is the pure sibling `forwarder.planStartOutcome` (carried out by
`noteForwarderStarted`, inside the same chain step that built the session):

| `Forwarder.start()` resolved to | what happens to the armed edge |
|---|---|
| `supported` — serving (or remote mode, which has no spool) | kept |
| `unsupported` — the guest ANSWERED `SPOOL=0` | kept: that answer cannot change until the VM is reprovisioned, so it is asked once per connection and not every 30 s |
| `unanswered` — the check could not be made at all | **cleared, with the session** — the next reachable reading is a fresh start |
| `stood-down` — the window withdrew the session mid-check | nothing: the stop path already cleared it |

The `unanswered` row is the one that has to exist. The capability check is one SSH exec and
it can simply fail — the connection drops, sshd is not up yet, the VM is on its way down —
and a failure establishes *nothing*, yet the session stands itself down while the window
keeps both the reference and the edge. Every later reachable reading then plans `none`, so
no watcher, no reconcile and no ack ever follows: a queued `construct expose` waits out its
whole timeout with an extension that is holding the instance and doing nothing. Clearing
both together (only while the start's own slot claim and generation are still current — what
it would clear belongs to a later session otherwise) turns the next 30 s reading into the
retry. The `supported`/`unsupported` asymmetry is the zero-change bar: a genuinely older
guest still costs exactly one exec per connection.

**Reachability is the whole rule, in both directions.** A watcher and a set of `ssh -L`
children pointed at a VM this window can no longer reach are holding sockets and nothing
else, so an unreachable reading lets them go even when the host still reports the VM as
`running` — `off`/`saved`/`absent` are the *reason* in the log, not the condition. Letting
go is safe because the requests live in the spool under `/etc/construct`, not in this
window: clearing the armed edge turns the next reading that *does* reach the VM into a
reconnect, and that session's first reconcile re-opens everything still queued.

Then, and only then, the forwarder asks the guest ONE cheap question
(`buildCapabilityScript`): does `/etc/construct/forwards` exist with `requests/`, `acks/`
and `close/` — the exact set `bin/provision.sh` `install -d`s on every provision? A VM
provisioned *before* `construct expose` existed answers `SPOOL=0`, and that costs **one
exec and nothing else**: no watcher, no reconcile, no timer, one log line saying to
reprovision, and no retry until the next connect, switch or setting change. Holding a
connection and polling such a VM every 30 s would be a socket, a wakeup and a battery
charged to an install that gained nothing — exactly what the zero-change rule is about.
A check that could not be *made* is a different answer and is retried (the table above).
(`OWNER=absent` from the reconcile still stands the forwarder down, for a spool that
disappears *after* the check.)

**The one deliberate default-path addition**, and the only long-lived process this feature
starts on a default install, is the spool watcher — spawned only after `SPOOL=1`, with
exactly this argv (`forwarder-ui.buildStreamArgs`, pinned in `forwarder.test.js`):

```
argv[0..10]  -T -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=12 \
             -o ServerAliveInterval=20 -o ServerAliveCountMax=3
argv[11]     agent-vm                         # the ssh alias (the default instance's)
argv[12]     the remote command, ONE argument, exactly as `ssh.wrapScriptCommand` builds it:

  f=$(mktemp) && printf %s '<base64 of forwarder.buildWatchScript()>' | base64 -d > "$f" \
    && bash "$f"; rc=$?; rm -f "$f"; exit $rc
```

(Thirteen elements in all: `-T`, five `-o` pairs, the alias, the command.) That whole
array is asserted **element for element** in `forwarder.test.js`, with the command rebuilt
from the watch script's own base64 rather than by calling `wrapScriptCommand`, so a change
to either the wrapper or the script fails the test rather than sliding past it. The script
it carries is `buildWatchScript` — an `inotifywait -m` on `requests/` and `close/` that
prints `CHANGED`, heartbeats, and traps SIGHUP to reap itself.

`-i <key> -o IdentitiesOnly=yes` leads the argv when the instance has a key file, and `-p`
is emitted **only for a non-22 port** — so the default instance's argv is the notification
watcher's, option for option. Every *other* argv on the default path is byte-identical to
the pre-forwards build.

### One session, one chain

The forwarder is a live per-VM connection, so it obeys the same rule the mic tunnel does:
**exactly one may exist, and only one serialized chain may create or dispose it**
(`instances.createHandover`, the decision by `instances.planEnable`, the slot by
`instances.createSessionOwner`). Building the transport is a real `await` for a remote
instance (its token comes from `SecretStorage`), and disabling forwarding, switching
instance or closing the window during it must all leave *nothing* behind — so the start
claims the slot **before** that await and re-checks the claim, the setting, the chain's
closed flag and the generation **after** it. A start that lost any of them publishes
nothing, and a snapshot from a session the slot has moved on from is dropped rather than
painted over the current VM's card.

**There is a second await, and it is inside the session.** `Forwarder.start()` asks the
guest before it spawns anything, and a teardown requested during that check is queued
*behind* the start that is still running — so the session would put its watcher up and
reconcile the spool first and be disposed a moment later. Two things prevent it: every stop
request goes through `requestForwarderStop()`, which invalidates the slot **synchronously**
and only then queues the disposal on the chain; and the session is handed the window's own
`eligible()` (claim + setting + chain-closed + generation), which it asks on both sides of
the capability check and stands down on — spawning nothing at all. So "no SSH after the
window said stop" holds across *both* awaits, not just the first. `deactivate()` closes the chain first and disposes the
one live session second, which is the same single documented exception the mic path makes.

### Wire-document validation (local spool only)

`v: 1` and a body `id` equal to the file name are **both mandatory** on every request, ack
and close document — `docs/expose.md` defines `id` in all three shapes, and the spool is
published atomically (temp name + `mv`) precisely so a reader never sees a partial file.
Both are refusals, not warnings: reading a `v: 2` request as v1 is exactly what the version
field exists to prevent (a future writer could give `localPort` a different meaning and we
would open a port for it), and a document that is missing its id, or claims a different
one, is a file that was never validly written — while the name it happens to sit under is
the identity used to write the ack, to `--close` it and to close it from the panel.

Strictness on the *close* document cuts both ways, and both favour it: `--close` removes
the request and the ack itself, so a close we decline to read still leaves a tunnel with no
request behind it (which the planner closes anyway) — whereas an **incomplete** close
document that we did read would tear down whichever tunnel its file name selects.

The **service's** list is a different contract with no `v` at all, so `readForwardList`
stays deliberately lenient — the same leniency `bin/construct-expose.sh` reads it with.

**A VM with no spool is asked once and then dropped** — see *lazy, and gated on the guest*
above for the capability check that decides it. The reconcile script's `OWNER=absent` is the
same answer arriving one round trip later (a spool removed after the check), and it stands
the forwarder down the same way: the watcher is killed, the poll is cancelled, one line in
the log says to reprovision, and it stays re-startable because `provision.sh` creates the
directories on every provision.

### Remote mode

`GET /api/v1/vms/{name}/forwards` every 10 s, through the same `remotehost.js` client and
the same credential the driver uses (owner credentials — the VM's own token deliberately
cannot post a client ack, see `service/README.md`). Client-target entries with no ack get a
tunnel over the instance's SSH endpoint (`sshHost:sshPort` from the registry, i.e. the port
the service allocated), then a `POST /vms/{name}/forwards/{id}/ack`. An entry that leaves
the list has been closed, so its tunnel is killed. The poll runs only while a window has
that instance active *and* has established it is up — the service, not this PC, is the
authority for a remote VM, so there is no spool and no capability check here: the same
lazy trigger simply starts the poll instead of a watcher.

### Tunnel supervision

Copied wholesale from `audio.js`, because the failure modes are the same: a settle window
(an `ssh` that dies within 1.2 s never opened the port, so it is a failure and not a
restart), restart with a 2 s → 60 s doubling backoff, and `dispose()` kills every child.
After `MAX_TUNNEL_ATTEMPTS` consecutive failures the guest is told with an `error` ack, so
`construct expose` stops waiting and says why.

**Only a connection that lasted `CONNECTION_HEALTHY_MS` (60 s) clears the failure streak.**
Surviving the settle window merely means the port opened; it says nothing about the link
being usable. Resetting `attempt` there — which the first version did — meant a tunnel that
died at 1.3 s *every time* reset its own streak on each retry, backed off at 2 s forever and
never reached the persistent-failure ack, so the guest waited out its entire timeout on a
forward that was provably broken.

### Settings and the panel

`construct.forwards.hostLabel` (default `""`) is the only new setting. Empty means the ack
carries no `hostLabel` and the CLI prints a loopback link — today's behaviour, and the
reason an untouched install sees no change. A value is put in the ack in its canonical form
(`sanitizeHostLabel`: one host-name-shaped token, or one bare IP literal) so the CLI prints
`http://<label>:<port>/` for a PC other machines know by name.

**One representation on the wire, one place that brackets it.** An IPv6 label is advertised
BARE — `fe80::1`, never `[fe80::1]` — and `urlHostFor` adds exactly one bracket pair when a
URL is built. The full rule, including what happens to a bracketed value, a zone id and a
string that is not an address at all, is specified once in
[`docs/expose.md` §The host-label rule](../docs/expose.md#the-host-label-rule) and
implemented three times against it (here, `bin/construct-expose.sh`, and the service's
`ForwardHost`); this module normalizes whatever the setting says before it advertises it, so
the wire only ever carries the canonical form. The literal is *parsed* (`net.isIP` — the one
thing this module takes from `net`, and it opens no socket), because a character class
accepts `::::` and `1::2::3`.

The panel's **Forwards** card lists port, label, target, status and link, with *Open*
(`vscode.env.openExternal`) and *Close*. It is `hidden` when there are no forwards **and**
the instance is local, so a single-VM install that never runs `expose` sees exactly the
panel it saw before. The **Idle policy** card (`GET`/`PUT /vms/{name}/idle-policy`) renders
only for a remote instance and clamps its input to the cap the service reports, with a hint
saying so. And a VM the service reports as `saved` turns the power button into **Resume &
connect** — the same `startVm` call the driver already maps to a resume, with copy that
says what will actually happen.

## Design decisions

- **UI designs (themes) are pure CSS layers — one markup, one controller, N skins.**
  `construct.uiTheme` picks a design (`classic` | `terminal` | `native`; `""`/unknown
  = fall back to `themes.DEFAULT_THEME` = `native`). `buildHtml` layers `media/themes/<id>.css` after `panel.css` via the
  `{{themeUri}}` link — `panel.html`/`panel.js`/`launcher.*` are SHARED, so
  functionality can never fork per design; the invariant is enforced by running the
  full ui-smoke suite against any skin (`UI_SMOKE_THEME=terminal|native node
  test/ui-smoke.js`). `classic.css` is intentionally empty (panel.css IS the classic
  skin); `native.css` derives every color/font from `var(--vscode-…, <Dark+
  fallback>)`, so it follows the user's REAL editor theme — light themes included —
  which a webview gets for free (this beats the mockup's simulated toggle).
  **No first-run prompt:** a fresh install just renders the default design; the
  picker webview (`themes.buildPickerHtml`: preview-image cards from
  `media/theme-previews/<id>.png`; escaped, nonce-CSP'd) opens ONLY on the explicit
  `construct.chooseTheme` command, so nothing pops up unasked. Picking writes the
  GLOBAL setting; the
  `onDidChangeConfiguration('construct.uiTheme')` listener re-renders both open
  surfaces in place (reassigning `webview.html`; the webview re-posts `ready` and
  gets fresh state). **The Construct: Choose UI Design** (`construct.chooseTheme`)
  reopens the picker anytime; the VS Code setting is a plain enum so the Settings
  UI works too. Adding a design later = one `THEMES` entry + `themes/<id>.css` +
  `theme-previews/<id>.png` (mission-control / datasheet are the planned next two).
  Perf nicety: `panel.js`'s rain loop exits when the active design `display:none`s
  the canvas (native does), so a hidden canvas is never animated.
- **VM → desktop notifications are a spool + atomic claim, streamed over one SSH
  connection, and delivered as a real Windows toast.**
  `construct notify "…"` on the VM writes ONE single-line JSON entry into
  `/run/construct/notify` (tmpfs via `systemd/construct-notify.conf`, mode 1777 — any
  user queues, nobody clobbers; a reboot must not replay yesterday's messages). The
  host runs `notify.buildWatchScript()` over a LONG-LIVED ssh started in `activate()` —
  deliberately NOT the webview-gated `syncAutoRefresh` loop, since the whole point is
  reaching a user who never opened the panel. The watcher drains on connect (so a
  reconnect needs no extra round trip) and then BLOCKS on `inotifywait`, streaming
  entries as they appear: delivery in milliseconds, and an idle connection costs
  nothing — versus a poll, which pays an SSH handshake per interval forever and still
  delivers late. `buildWatchArgs` adds ServerAlive keepalives so a silently dead link
  makes the child EXIT, which is what drives `scheduleNotifyRestart`'s 2s→60s backoff
  (reset once a connection has survived a minute, so one flaky drop doesn't inherit an
  old streak). Three details that are easy to get wrong and were: inotifywait runs in
  MONITOR mode (`-m`) read through a process substitution — one-shot `inotifywait; claim`
  has a blind spot for entries queued DURING the claim, and a pipeline's subshell would
  hide the watcher pid from cleanup and outlive its parent as an orphan; a `trap … EXIT
  HUP INT TERM PIPE` kills that inotifywait when the connection ends; and the periodic
  heartbeat line means an orphaned watcher meets a closed pipe and reaps itself.
  Each entry is CLAIMED with an atomic `mv` before it is printed, which is the entire
  multi-window story: N VS Code windows each hold their own watcher over the same spool,
  exactly one wins each entry, so a notification shows once no matter how many windows
  are open. A watcher that dies BETWEEN claiming and printing would strand its entry, so
  `claim()` renames anything claimed for over a minute back to `.json` (a live claim
  lasts microseconds) — deleting them, the obvious sweep, silently loses notifications. Delivery is a REAL Windows toast
  (`ToastNotificationManager` via `powershell.exe -EncodedCommand`), because a VS Code
  notification is invisible when the window is minimised — the point of the feature.
  **No console flashes**: a powershell.exe spawned straight from the extension host
  inherits no console and can't allocate one (the inverse of the `cmd /c start` note
  above — that's what FORCES a window), plus `-WindowStyle Hidden` + `windowsHide`.
  The script registers its AppUserModelId under HKCU — the registry key IS the
  registration, since we have no installer and no Start-menu shortcut, and Windows
  silently drops toasts from an app id it does not know (`ShowInSettings` so the user
  can mute/un-mute us deliberately, `ShowInActionCenter` so the toast persists).
  **The notifier's `Setting` is advisory, not a gate** — and getting that wrong is
  what made this path fail in the field: an id registered only under HKCU commonly
  reports `DisabledForApplication` until Windows has seen a toast from it, so gating
  on `Setting != Enabled` downgraded EVERY notification to a VS Code toast on a
  machine where notifications were fine. Now a candidate list is tried — our own app
  id first, then the Start-menu "Windows PowerShell" AUMID that every install
  registers — preferring whichever reports `Enabled`, pushing the toast through the
  best non-`Enabled` candidate otherwise, and retrying the next candidate if `Show()`
  throws. Only `DisabledForUser`/`DisabledByGroupPolicy` suppress — and they veto the
  whole attempt, not just the candidate that reported them (they are user-/machine-wide
  switches, so slipping the toast out under the other identity would walk around the
  user's own setting) — and only then does the VS Code fallback run. `DisabledForApplication` is ambiguous — it is equally what
  Windows reports when the user genuinely switches us off — so the deliberate case is
  read where it is unambiguous instead: the per-app `Enabled` flag the Settings UI
  writes under `…\CurrentVersion\Notifications\Settings\<app id>`. Present and 0 =
  muted on purpose → exit 2, and NOT re-routed through the fallback identity. Every outcome has
  its own exit code (`notify.TOAST_EXIT`: 2 suppressed, 3 no WinRT, 4 constrained
  language mode, 1 anything else) plus a one-line reason on stderr, which
  `toastResult` turns into the log line — a toast that DID appear but went out under
  the fallback identity is logged as a note, not a failure. A silent downgrade is the
  bug; the fallback itself is not. powershell.exe is spawned by its absolute System32
  path when there is one (`powershellPath`), so a stripped PATH cannot cost a toast.
  Agent
  text is UNTRUSTED input crossing into a shell and an XML document: it is stripped of
  control characters (a newline would forge a second spool entry), length-capped,
  XML-escaped, and reaches PowerShell only as a base64 blob inside a single-quoted
  literal — never interpolated. One-way BY DESIGN (no actions, no reply channel):
  questions belong in the agent's own chat. Clicking the toast opens a fixed
  `vscode://` URI handled by `registerUriHandler` (no VM-authored data in it).
- **Packaging = a PowerShell-generated `.vsix` installed via `code --install-extension`**
  (no `vsce`/Node on the host). Modern VS Code ignores a bare folder dropped into
  `~/.vscode/extensions` (it's never registered in `extensions.json`), so
  `Build-ControlPanelVsix` hand-builds the OPC package and `Install-ControlPanelExtension`
  installs it with `--force`.
- **Remote-SSH open** (`src/remote.js`). The Connect button opens the VM workspace
  (`/root/repos`) in VS Code over Remote-SSH via `vscode.openFolder` + a
  `vscode-remote://ssh-remote+agent-vm/<path>` URI (the `agent-vm` SSH Host alias the
  provisioner writes), reusing the current window. The button shows only when the VM
  is reachable (`online`) and this window isn't already on it — `connected` is computed
  host-side from `vscode.env.remoteAuthority` (matched against the alias/hostname) and
  folded into the pushed state. Needs the `ms-vscode-remote.remote-ssh` extension
  (warns if absent). The control morphs by power state (`src/vmpower.js`): a host
  `Get-VM` probe (captured stdout, run only when offline) yields `vmState`, so a
  stopped-but-installed VM shows "Start & connect" (elevated `Start-VM` + UAC, then
  poll reachability and open) and a reachable VM shows "Shutdown" (`systemctl poweroff
  --no-block` over SSH). **The Start gate (`vmpower.shouldShowStart`) shows for offline
  vmState `off` OR `unknown`, not `off` alone** — the non-elevated `Get-VM` probe is
  Hyper-V-permission-gated (the installer's Hyper-V Administrators membership only takes
  effect at next sign-in), so a stopped VM commonly probes back `unknown`; only a
  positively-`absent` (privileged) probe hides Start. The Start action self-elevates, so
  offering it for `unknown` is safe. Both webviews inline the identical predicate;
  `vmpower.test.js` locks the canonical `shouldShowStart` so the two copies can't drift. "+ add project" clones a git URL onto the VM (injection-safe,
  base64-as-data) and opens it in a new window; an inline ▷ per chip opens that
  project's single-repo folder (mirroring `bin/checkout-projects.sh`) in a new window.
  The installer (host PowerShell) ensures VS Code + the Remote-SSH extension, adds the
  user to Hyper-V Administrators, and prints an end-of-install deep link; the dashboard
  opens alongside via `maybeAutoOpenPanel` on first VM-connected activation. URI paths
  are percent-encoded per segment so a folder name with `?`/`#` survives `Uri.parse`.
- **Lifecycle launch = host console via `child_process`, never the integrated
  terminal.** A UI extension's Node code runs on the local Windows host even when
  the window is Remote-SSH'd into the VM, but `createTerminal()` targets the
  *window's* context (the VM, where there's no `powershell.exe`). So `lifecycle.js`
  launches via **`cmd.exe /c start "" powershell.exe -EncodedCommand …`** so a new
  console appears. **Why `cmd /c start`:** VS Code's extension host is a GUI process
  with NO console; a powershell.exe spawned from it inherits none, and Node's
  child_process can't request `CREATE_NEW_CONSOLE` (`detached` sets the OPPOSITE,
  `DETACHED_PROCESS`). A console-less launcher's `Start-Process` opens NO visible
  window — the "toast fires, no window, nothing happens" bug. Removing `windowsHide`
  did NOT fix it (detached still suppressed the console); `start` is the reliable Win32
  primitive that forces a new console. Only argv-safe tokens (the fixed powershell flags
  + the base64 blob) pass through cmd — no paths/user values — so `start` adds no new
  quoting surface. **Elevated vs non-elevated:** the elevated actions run
  `Start-Process -Verb RunAs …` in the started console (UAC prompt + elevated console —
  so a brief launcher window plus the elevated one). Non-elevated actions run the script
  DIRECTLY via `& '<script>' <args>` (`buildCallCommand`) in the started console — ONE
  window, no inner Start-Process (that second window was the reported "two popups"); the
  launcher there is NOT `-NonInteractive` so the script's end-of-run pause/prompts work.
  All target params are `[string]/[int]/[switch]`, so `&` with single-quoted values binds
  (incl. `[int]` coercion) and is injection-safe (quotes doubled — proven via pwsh).
  `vmpower.startVm` (the elevated "Start & connect") uses the Start-Process wrapper too;
  its elevated child has NO `-NoExit` (it runs Start-VM and EXITS instead of leaving an
  interactive prompt — it pauses only on FAILURE; `construct.debug` adds `-NoExit` back).
  The launched console outlives VS Code (its own process).
- **Diagnostics (so a flashing console is debuggable).** A "Construct" Output channel +
  `%TEMP%\construct-panel.log` record every launch — the DECODED powershell command, the
  resolved script path, args, env keys, and spawn result — so version skew / wrong paths /
  bad args are visible even when the console closes fast (`lifecycle.configure({log,isDebug})`
  wires the logger + debug getter into `launchHostScript`; the webviews expose a **logs**
  button → `showLogs`, and there's a **The Construct: Show Logs** command). The
  `construct.debug` setting keeps launched consoles OPEN (`-NoExit` — on the non-elevated
  console directly, on the elevated child via `buildChildCommandLine({keepOpen})`) so an
  error that happens BEFORE the script's own pause (e.g. a parameter-binding error) stays
  on screen instead of flashing away.
  Quoting (verified through real PowerShell): the outer command is handed to the
  spawned shell via `-EncodedCommand` (base64 UTF-16LE) so there's NO Node↔shell
  quoting layer; the child argv is canonically Windows-quoted (`winQuoteArg`) and
  forwarded as a **single-string** `-ArgumentList` (an array would be space-joined
  without re-quoting, splitting a spaced path or a two-word `-GitUserName`). Settings
  values reach the script as data, not commands, so they can't inject. (The "launched"
  toast is optimistic — it can't detect a UAC decline or a missing script path.)
- **UAC: don't elevate the extension host.** Reprovision/Export touch no Hyper-V →
  launched non-elevated. Reinstall/Redownload delete+recreate the VM → launched
  with `Start-Process -Verb RunAs` so UAC consent fires once and a single elevated
  console does the work (`Auto-Install.ps1` also self-elevates, so manual runs
  still work). A `process.platform !== 'win32'` guard fails loudly off-Windows.
- **Reinstall/Redownload pre-selection** rides a new `Auto-Install.ps1`
  `-Action`/`-BackupMode` (see key references). The two safety gates — the
  dirty-repo scan and the "type yes" delete — stay interactive in the elevated
  console; only the menu choice + save/restore policy are automated. The agent
  password is NOT passed on the command line (process-list exposure); the script
  prompts for it, and the settings form shows it as console-entered (a note, not
  an input). Project selection is likewise left to the script's selector until the
  Projects batch, and the settings lead copy says both are still entered in the
  console (so the UI doesn't over-promise an unattended run).
- **Install / reprovision pills = a VM-side timestamp marker.** The status strip's
  `installed —` / `reprovisioned —` pills are fed by `state.installed` /
  `state.reprovisioned`, produced by the live probe (NOT host-side) so they reflect
  the ACTUAL VM and a reprovision moves them. `bin/provision.sh` records
  `/etc/construct/provisioned.env` as its last step on a SUCCESSFUL run:
  `INSTALLED_AT` written once and preserved across reprovisions (and back-filled on a
  VM provisioned before the marker existed), `REPROVISIONED_AT` rewritten every run —
  both ISO-8601 UTC, via the same idempotent `config-set.sh` merge. `REMOTE_PROBE`
  reads the two keys with `sed` (like config.env); `probe.toState` formats each to its
  date part (`formatMarker`, a pure ISO-date slice — no `Date`, so no midnight-drift)
  and OMITS the field when the marker is absent/blank, so the webview keeps the `—`
  placeholder for a truly-unknown value. `panel.js` renders the pills authoritatively
  on the online path (`installed <date>` else `—`) and resets them in
  `clearLiveVmData()` when offline / probe-failed; `launcher.js` rebuilds its meta line
  fresh each render. `probe.test.js` covers the emit/parse/format + the omit-when-absent
  contract. (Reprovision = `Provision-AgentVM.ps1 -Action provision`, which runs
  `provision.sh` on the VM, so the same marker step covers first install and reprovision.)
- **Two commit markers: installed vs provisioned.** `.construct-settings.json` records
  TWO commits. `installedCommit` = the installed Construct (extension + scripts) — written
  by the INSTALL path (`Auto-Install.ps1`'s non-elevated pre-step, right after it installs
  the vsix) and by `Update-Construct.ps1` on refresh; it drives the "update available"
  banner (`updates.augment` compares `installedCommit...ref` via GitHub → `{update:{available,
  behind}}` + a `constructRev` label). `provisionedCommit` = what the VM was last provisioned
  with — written by `Provision-AgentVM.ps1` (`Set-ConstructProvisionedMarker`), MIRRORING the
  current `installedCommit` (not a fresh fetch, so it can't claim a newer commit than the
  scripts actually are). Provision does NOT touch `installedCommit`, so a reprovision can't
  wrongly clear the update banner. When the two differ (`updates.isProvisionStale`), the
  panel + launcher mark the **Reprovision** button yellow (`.stale`) + "update pending"
  subtext/tooltip — "the VM is behind the installed Construct; reprovision to apply it."
  Conservative: only when BOTH markers are known and differ (an unknown `provisionedCommit`
  — a VM provisioned before this tracking — isn't flagged until its next reprovision records
  one). It's BEST-EFFORT and CACHED (10 min for
  a real result; 60 s for a failure, so a transient blip doesn't hide the banner for
  10 min): no marker, offline, or rate-limited → no `update` key → banner hidden.
  `updateConstruct` (`runUpdateConstruct`) launches `Update-Construct.ps1` on the host
  (non-elevated, download + reinstall the panel, no VM rebuild), which re-records the
  marker so the banner clears. **Auto-reload:** the panel passes the result path via the
  `CONSTRUCT_UPDATE_RESULT` ENV VAR (NOT a `-ResultFile` arg — an OLDER installed script
  ignores an unknown env var but would ERROR on an unknown parameter, and since it errors
  before downloading the fix that would permanently trap the button; the script reads the
  env var, `-ResultFile` still accepted for compat). The script writes `ok`/`fail` at the
  very end (after the vsix reinstall — a missing OR falsey `Install-ControlPanelExtension`
  counts as fail) and, on success WITH a result path, does NOT pause (the reload is the
  feedback). `runUpdateConstruct` polls the file and, on `ok`, runs
  `workbench.action.reloadWindow` so the refreshed panel loads with no manual reopen (a
  detached host console can't reload VS Code itself); on `fail` the script's console pauses
  with a "reopen VS Code" message and the panel shows a toast. The script is also launched
  by the Construct-built T3 Code Desktop app (which passes the same env var, so that console
  closes by itself too) and by hand; those launches can't reach this window's poll, so
  `watchInstalledMarker` (activate) polls the scripts dir's `.construct-settings.json` every
  3 s and reloads the window when `installedCommit` changes. The script writes that marker
  LAST — after the vsix reinstall — so a reload triggered by it always loads the new panel. The full window reload is
  reserved for a self-update (it swaps the extension itself); ordinary VM-side changes (a
  reprovision, power on/off) are picked up by `syncAutoRefresh`'s `refreshAll` timer,
  which runs only while a dashboard is open — no reload needed. It ticks every 30s
  normally; when the panel launches a reprovision, `beginReprovisionFastRefresh` drops
  it to 5s and `refreshTick` reverts to 30s once the host `provisionedCommit` marker
  changes (the finished reprovision recorded a new one) or a 5-minute cap elapses. An old script (no env read)
  just pauses on completion and the poll times out — the update still applied, no auto-reload
  that once. Run by hand (no result path) it pauses on success too. Agent updates
  work the same way: per-agent latest (npm/GitHub releases) vs the probed version →
  `{latest, updateAvailable}` folded into `state.agents`; `updateAgents` force-updates
  over SSH (`claude update` + re-run installers) with a progress notification.
- **Mic passthrough is on-demand.** Claude spawns `rec` only while recording and
  SIGTERMs it on stop, so the VM-side shim's tunnel connection *is* the
  record-window signal — the host opens the mic on connect, releases on disconnect.
  The mic is never hot continuously. (snd-aloop was rejected for requiring a
  constant feed.)
- **Mic passthrough is multi-window safe (a tunnel-port RANGE, not one port).**
  Every VS Code window runs its own extension host → its own HostAudio, and an
  `ssh -R` bind is exclusive per port — with the old single fixed 8767 a second
  window's tunnel died on ExitOnForwardFailure AND its rollback ran the VM disable
  script, ripping the shared shim + gate patch out from under the first window (the
  "mic only works in one window" bug: auto-arm in window B silently broke window A).
  Now: the VM side is `[8767, 8767+8)` (`DEFAULT_VM_PORT`/`DEFAULT_VM_PORT_COUNT`).
  The enable script reports `CONSTRUCT_PORTS_BUSY=<csv>` (range ports with a
  listener = other windows' live tunnels, via `ss -ltn` — NEVER a test connection,
  which would arm that window's mic as a false record-start); `enable()` skips those
  and treats an early ssh death as "next candidate" (two windows racing the same
  free port: the loser advances), exposing the winner as `boundPort` (shown in the
  tunnel label). Exhausted range → honest `no-free-port`. The shim scans the same
  range for the first LISTENING port at record time — all windows come from the same
  physical host, so any live tunnel reaches the same microphone. The disable script
  is guarded ("last window out turns off the lights"): given
  `CONSTRUCT_TUNNEL_SELF_PORT` (the caller's own port — waited on ≤3s to clear,
  since the just-killed ssh's listener can linger) it leaves the shared shim + patch
  in place while any OTHER range port still listens, so a disable/rollback in one
  window can no longer break the rest. Known limits (accepted): two windows
  RECORDING at the same instant share the first live tunnel (AudioSession's
  newest-wins drops the older stream — one physical mic anyway), and a second
  physical client machine attached to the same VM would reach the first machine's
  mic (Construct is a per-user, per-host VM).
- **Mic passthrough = ONE persistent setting.** Both switches — the console
  `#voiceSwitch` and the settings `#setMic` — drive the SAME `micPassthrough` key in
  `.construct-settings.json`. Toggling the console switch persists it (`persistMicPreference`
  → `host.saveSettings({mic})`, merge-only) and `broadcastSettings` keeps `#setMic` in
  sync; saving the settings form reconciles live audio immediately (arm if newly on,
  disarm if newly off) and `broadcastAudio` keeps `#voiceSwitch` in sync. So "enable on
  the main page" sticks. `activate()` → `maybeAutoEnableAudio` reads `micPassthrough` and,
  if on AND the VM is reachable, arms at startup via `enableAudio(..., {auto:true})` —
  FULLY SILENT (no notification progress, no toasts; the switch reflects the result, and a
  down VM shouldn't nag on every launch — each window arms its own range port, see the
  multi-window decision). A manual enable
  whose gate patched offers a **"Reload window"** (the running Claude Code still has the
  pre-patch code in memory, so its mic button only appears after a reload; passthrough
  re-arms itself post-reload via auto-arm). Not unit-testable here (no VS Code `activate()`
  runtime) — logic-reviewed + syntax-checked.
- **Guard patch is reversible + version-generic.** Neutralize only the speech gate
  by rewriting `…env.remoteName)return!1` → `…env.remoteName&&!1)return!1` in the
  VM's installed `anthropic.claude-code-*/extension.js`. Applied on audio-enable,
  reverted on disable; only ever touches this VM's copy.
- **sox in provisioning, everything else extension-driven** (committed: `4931140`).
- **Settings persistence** uses the same `.construct-settings.json` the installer
  uses (interop keys `gitUserName`/`gitEmail`/`gitCredentialStore`). **Do NOT
  persist the agent password** to that file (plaintext); pass it at reinstall time.
  `src/host.js` owns the file: `mapFromForm` writes the git interop keys plus
  forward-compat keys the installer can adopt later (`vmMemoryGB`, `vmDiskGB`,
  `ubuntuRelease`, `vsCodeServeWeb`, `vsCodeTunnel`, `smbShare`, `micPassthrough`),
  and `saveSettings` merges over the existing file so unmanaged keys (e.g. the
  update marker `installedCommit`) survive. Empty text/number fields are omitted
  (don't clobber a stored value with a blank); booleans always write (toggle-off
  persists). Reads strip a UTF-8 BOM (Windows PS 5.1 `Set-Content -Encoding UTF8`).
  `agents`/`projects` are deferred to the Projects batch — the settings-form chips
  aren't hydrated from live state yet, so writing them now would clobber the real
  selection with the static all-on defaults. The panel's `applySettings` only
  drives a switch when the value is a real boolean, so a partial payload (e.g. the
  installer's git-only file) leaves the other toggles' HTML defaults intact.
- **Host-driven config sync tick (docs/config-sync.md §6).** Profiles live in
  `%LOCALAPPDATA%\The-Construct\config` (host.configDir), a machine-wide location
  OUTSIDE any zip checkout. The sync tick reconciles this config dir (host truth)
  with the VM store (`/opt/construct/projects`) via `configsync.syncTick`. Triggers:
  (1) piggybacked on the existing 30s refresh timer, self-throttled to >=5 min between
  automatic ticks; (2) immediate on the panel "Sync now" button (`syncConfigNow`);
  (3) `fs.watch` on cfgDir/projects (debounced 2s, tolerates watcher errors); (4) once
  at activation when a dashboard opens. An in-flight flag prevents concurrent ticks;
  the tick is skipped entirely when git is absent or cfgDir is null. The engine is
  platform-agnostic (it spawns `git`, works on any OS); the "supported" gate is git
  presence + config dir, NOT `win32`. Before launching reprovision/reinstall/redownload,
  handleMessage checks `repoState` — if conflicted/mergeInProgress, it blocks the
  launch with an error toast and an "Open config repo" button (which opens cfgDir in a
  new window so the VS Code git extension / merge editor can resolve it). `state.configSync`
  is folded into every postState as a host-derived field: it survives offline (NOT cleared
  by clearLiveVmData) so the conflict banner / git-missing notice / remotes list are
  always visible regardless of VM reachability. The legacy fallback: when cfgDir is null
  (no LOCALAPPDATA / TEMP), profile operations fall back to the old scriptsDir path.
  D11: the "default" profile chip is rendered with a lock icon and does NOT open the
  edit modal on click; reserved names are refused by runSaveProject/runEditProject.
- **Automatic checkpoints: off by default, and the setting is applied BOTH ways.**
  Hyper-V snapshots a VM at every start unless told otherwise; on a disposable agent
  VM that only pins a growing `.avhdx` differencing disk, slows I/O, and costs a merge
  on delete. So `Create-AgentVM.ps1` now passes `-AutomaticCheckpointsEnabled` from a
  new `-AutomaticCheckpoints` param that **defaults to `false`** (Auto-Install forwards
  it into `$createArgs`; `lifecycle.buildInvocation` threads
  `-AutomaticCheckpoints` onto reinstall/redownload — the rebuild actions, since the
  policy is fixed at VM-CREATION time — and NOT onto reprovision, which never touches
  Hyper-V). The panel toggle lives in **Settings → VM resources** (`#setAutoCheckpoints`
  → `autoCheckpoints` → `vmAutoCheckpoints` on disk), default OFF in the markup so a
  settings file with no stored key reads correctly.
  **Live apply, decided against the VM — not against the old setting.** The obvious
  design (offer when the preference CHANGED) is wrong, and external review caught it: a
  VM created before this feature has the policy ON while its settings file has no key at
  all, so the first save is off→off and would never offer — the upgrade path, i.e. every
  existing user, silently gets nothing. It also can't retry after "Later" or a declined
  UAC, since the file already holds the wanted value. So `offerApplyCheckpoints` probes
  the VM's REAL policy (`vmpower.queryAutoCheckpoints` → `on|off|absent|unsupported|
  unknown`, a captured non-elevated `Get-VM` + `AutomaticCheckpointsEnabled` property
  probe) and `vmpower.shouldOfferCheckpointApply` (pure, unit-tested) offers iff the VM
  DISAGREES; `absent`/`unsupported` never offer. `unknown` is NOT rare — the non-elevated
  `Get-VM` is Hyper-V-permission gated (membership lands at the next sign-in) — so it must
  not fall back to the changed-signal either, which review round 2 caught as the SAME
  upgrade bug in disguise. It falls back to **`vmAutoCheckpointsApplied`**: the value last
  CONFIRMED onto the VM (`host.read/saveAppliedAutoCheckpoints`; `null` = never). Offer
  while that disagrees → on each save until an apply actually SUCCEEDS (Later / a declined
  UAC don't count, and shouldn't). The marker is written ONLY on a confirmed run: `Set-AgentVmCheckpoints.ps1`
  reports `ok`/`fail` through `CONSTRUCT_CHECKPOINT_RESULT` (temp+rename; the same
  result-file mechanism `Update-Construct.ps1` uses) and the panel polls it — a declined
  UAC never reaches that code, so it correctly leaves the marker unset and re-offers.
  `vmpower.planCheckpointOffer` (pure) owns the payload sequencing: the wanted value is
  read from the MERGED save result, not the raw payload — `mapFromForm` omits an absent
  boolean, so a partial post (stale webview sending only git fields) would otherwise read
  as "wants off" and offer to disable checkpoints the file still says are on — and it
  refuses to act unless the form actually carried a boolean. After the modal the preference
  is RE-READ from disk, so a second window saving the opposite value while the dialog sat
  open can't make this one apply the stale side. (A residual race remains between that
  re-read and the detached launch; two windows fighting over one Hyper-V flag is accepted,
  since the next save reconciles against the VM's real policy anyway.)
  "Apply now" runs `lifecycle.run("setCheckpoints", {enabled})` → an ELEVATED console (UAC)
  running `Set-AgentVmCheckpoints.ps1 -FromPanel -Enabled true|false`; the action builder
  demands a STRICT boolean and returns null otherwise (defaulting would silently pick the
  destructive direction). The script self-elevates too — with canonical
  CommandLineToArgvW quoting and `-Wait -PassThru` so a cancelled UAC or a failed child
  is reported instead of exiting 0 — and loads the common lib INSIDE its try/finally so a
  damaged install still honours the pause + result-file contract. Non-win32 → an honest
  warning. **Capability gate:** `lifecycle.scriptSupportsCheckpoints` greps
  `Auto-Install.ps1` for `$AutomaticCheckpoints` (NOT the presence of a sibling file — a
  hand-assembled dir can hold the new live-apply script next to an old Auto-Install) and
  `buildInvocation` drops the flag when unsupported, because an advanced function rejects
  an unknown parameter at BINDING time and the rebuild would never start. Since an old
  `Create-AgentVM.ps1` hardcodes checkpoints ON, dropping the flag would silently produce
  the OPPOSITE of an "off" preference — so `run()` warns about exactly that before the
  rebuild instead of after (treating an ABSENT key as "wants off", since that's the
  product default the panel shows). `Auto-Install.ps1` carries the mirror guard for the
  DOWNSTREAM pair: it checks `Create-AgentVM.ps1`'s parameters before splatting and drops
  the argument (loudly) if that script is older — by the time it runs the old VM is
  already deleted, so a binding failure there would leave the rebuild simply broken. It
  also falls back to the saved `vmAutoCheckpoints` when the parameter is unbound, so a
  hand-run install — or an OLD extension driving new scripts — still honours the toggle.
  A destructive rebuild REPLACES the VM, so `run()` clears the applied marker: what was
  confirmed onto the old VM says nothing about the new one.
  **Why removal is the hard half:** turning the policy off does NOT delete the checkpoint
  Hyper-V already took, and deleting the wrong one would destroy a user's snapshot.
  `Get-AgentVmAutomaticCheckpoint` (lib) classifies in three tiers — (1) the VMSnapshot's
  own `IsAutomaticCheckpoint` property when the build exposes it, (2) WMI
  `Msvm_VirtualSystemSettingData.IsAutomaticSnapshot` joined on the checkpoint GUID
  (`ConfigurationID` == `VMSnapshot.Id`; queried host-wide, since GUIDs are unique and
  a snapshot row's `ElementName` is the SNAPSHOT's name, not the VM's), (3) the
  `"<VM> - (<timestamp>)"` auto-naming heuristic. Tiers 1–2 are authoritative in BOTH
  directions (`Get-AgentVmAutomaticCheckpointId` returns `@{Supported;Ids}` precisely so
  "query worked, found none" can suppress the heuristic), so a user checkpoint that
  happens to look auto-named isn't questioned. Only tier-1/2 hits (`Certain`) are deleted
  silently; tier-3 hits (`Probable`) need a typed `yes` in the console — asked ONE
  CHECKPOINT AT A TIME, since a blanket yes over a list would take a user's
  deliberately-named checkpoint along with the real one.
  Removal is BY OBJECT (`-VMSnapshot`), never `-Name` (Hyper-V allows duplicate names).
  Both the property read and the `Set-VM` parameter are probed, so a pre-1709 host that
  has no automatic checkpoints says so instead of erroring.
- **Auto-import from VM (replaces the manual "import from VM" button).** When the
  sync tick successfully reads the VM store, `importFromVm()` scans the VM's
  checked-out repos over SSH (the same `buildScanScript`/`planImport` as the old
  manual import) and writes a profile for each repo not already covered. Newly
  imported profiles are auto-selected into the persisted selection via
  `autoEnableNewProfiles` so they are included in the next reprovision/reinstall.
  The import is idempotent (planImport never overwrites), non-interactive (no
  toasts on the tick — results are logged), and degrades gracefully when the VM is
  offline (the sync tick sets `vmReadOk:false` and the import is skipped).
- **Reinstall/reprovision pre-flight.** Before a manual Reinstall, Redownload, or
  Reprovision proceeds, the handler runs a three-step pre-flight via
  `coalescedImport(true)` + `runConfigSync()` + `configMergeGate()`:
  (a) import — if the VM is unreachable or imports have write failures, a modal
  warning lets the user cancel or continue; (b) sync — inspects the result for
  lockBusy/blocked/failure/vmReadOk:false and warns with a modal; (c) conflict
  gate — if conflicted, a modal warning with "Open config repo" / "Cancel"
  blocks the launch. Steps (a)/(b) are advisory (the user may proceed at their
  own risk), while step (c) is un-bypassable: the only way forward is to resolve
  the conflicts, commit, and retry. All import calls go through `coalescedImport`
  so concurrent scans from overlapping triggers share a single SSH session — per
  instance: the pre-flight passes the action's captured target, so it joins (or
  starts) a scan of the VM it is about to rebuild, never one of another instance.
- **Destructive flows default to save→restore**; one-time overrides (existing
  backup / clean wipe) live in Settings → Custom reinstall, not as a persisted
  policy. On failure, offer a retry reusing the backup already taken.
- A compare-API **404** (the recorded `installedCommit` no longer exists upstream — what a
  history rewrite looks like from a PC installed before it) is `updates.NOT_FOUND`, not a
  failure: the banner shows "update available" with no count, and the update re-records a
  marker the remote knows. Every other non-2xx stays null (banner hidden).

## Key repo references (for resuming)

- Optional-feature template: `bin/setup-smb-share.sh`; orchestration `bin/provision.sh`;
  idempotent config writer `bin/config-set.sh`; example `config/config.env.example`;
  systemd install pattern in `bootstrap.sh`.
- AI tools: `bin/install-ai-tools.sh` — opencode installer re-runs to update;
  **claude/codex are skipped if already present**, so "update agents" must force a
  re-run (`claude update` / re-run installers). sox installed in `install_claude_code()`.
- Lifecycle entrypoints (host PowerShell):
  - `Provision-AgentVM.ps1` — params incl. `-Action provision|export`, `-BackupDir`,
    `-RestoreDir`, `-ScanReposOnly`, `-Projects`, `-AiTools`, `-GitUserName`, `-GitEmail`.
    The reprovision entrypoint. **Targeting:** `-VmHost` (default
    `agent-vm.mshome.net`), `-SshPort` (default 22 — threaded into every ssh/scp/keyscan
    call, adds the `Port` line to the ssh_config block and switches `known_hosts` to
    `[host]:port`), `-HostAlias` (default `agent-vm`), `-LocalKeyName` (default
    `agent_vm_ed25519`), `-ConfigBranch` (empty ⇒ derived from `-HostAlias`).
    **Remote-only:** `-ServiceUrl` / `-InstanceName` / `-VmTokenB64` → the guest's
    `CONSTRUCT_SERVICE_URL` / `_INSTANCE_NAME` / `_VM_TOKEN_B64`; while empty they add
    NOTHING to the env prefix, so a local run is byte-identical. The token never reaches
    an argument list — it goes to the guest over ssh's stdin.
  - `Auto-Install.ps1` — web-install + reinstall/reprovision menu; params incl.
    `-VmDiskGB`, `-VmMemoryGB`, `-Projects`, `-AgentPassword`, `-GitUserName`,
    `-GitEmail`, `-Force`, `-Redownload`, `-SkipCreateVm`. Reinstall deletes the
    VM + disk. **`-Action reprovision|reinstall|redownload|export|add-config`** bypasses
    the interactive menu (added for the panel); with reinstall/redownload,
    **`-BackupMode save|existing|wipe`** pre-answers the save/restore prompts. The
    dirty-repo scan and the `Confirm-Reinstall` "type yes" delete still run.
    **Targeting:** `-VmName` (default `Agent-VM`; the guest hostname, DNS name, ssh alias
    and key name all derive from it), legacy `-VmHost` (the guest hostname, predating
    `-VmName`: alone it *sets* `-VmName`, and a value conflicting with an explicit
    `-VmName` is a hard error), `-ConfigBranch`. **Remote:** `-Backend
    hyperv-local|hyperv-remote`, `-ServiceUrl`, `-ServiceAuth negotiate|token`,
    `-InstanceName`, `-VmCpuCount` (remote-only: `Create-AgentVM.ps1` decides the local
    VM's processor count). Any of `-Backend`/`-ServiceUrl`/`-InstanceName` — and also
    `-VmName`, `-Action`, `-FromPanel`, or an existing default instance — skips the
    install-mode prompt.
  - `Create-AgentVM.ps1` — `-VmName`, `-Backend`, `-LocalKeyName`, `-AutoinstallIso`
    (a named VM otherwise looks for `<name>-autoinstall.iso` next to the script and
    refuses to guess another instance's ISO; the default VM keeps the "newest
    `*autoinstall*.iso`" discovery), `-ConfigBranch`, plus the resource/forwarded ones.
    Hypervisor calls go through `drivers/Load-ConstructDriver.ps1`, not raw cmdlets.
  - `install.ps1` — THIN web bootstrapper: downloads the repo zip to
    `%LOCALAPPDATA%\The-Construct\<owner-repo-ref>\<repo>-<ref>\` and runs Auto-Install.
    Default repo `permissionBRICK/The-Construct`, ref `main` (forwards `-Repo`/`-Ref`
    only when explicit). No host setup of its own.
  - `Set-AgentVmCheckpoints.ps1` — the panel's live "apply automatic checkpoints now":
    `-Enabled true|false` (+ `-VmName`, `-Backend`, `-RemoveExisting`, `-FromPanel`).
    Self-elevating; goes through the driver contract's four checkpoint functions and
    REFUSES when the backend reports `Checkpoints = $false` (so it can never reconfigure
    a *local* VM that merely shares a remote instance's name); when disabling, removes
    the automatic checkpoints classified by `Get-ConstructVmAutomaticCheckpoint`
    (`Certain` unattended, `Probable` only after a per-checkpoint `yes` — see the design
    decision).
  - `Update-Construct.ps1` — the panel's "Update Construct" self-update: re-download the
    repo in place, record the update marker (`installedCommit` from the GitHub commits
    API + `constructRepo`/`constructRef` via `Set-ConstructInstalledMarker`), and
    reinstall the control-panel extension. Does NOT rebuild the VM.
  - `Get-AgentUsage.ps1` — ccusage over SSH → combined JSON; SSH connection logic
    (key `~/.ssh/<-LocalKeyName>` else the `-HostAlias` alias, on `-SshPort`) mirrored in
    `src/ssh.js`. Defaults are the single-VM literals.
  - `Update-T3Code.ps1` — launched by the Construct-built T3 Desktop app's update control
    (its "Reprovision VM" offer: the VM is behind the installed Construct, or a newer
    upstream T3 Code exists on the build's channel); reruns provisioning with the saved
    settings; exits 1 when provisioning failed and 3 when it finished with optional errors,
    so the launcher can tell. Takes `-VmHost` / `-HostAlias` / `-SshPort` / `-LocalKeyName`
    and forwards them with param probing, so an older provisioner is never handed unknown
    args — the Desktop app passes them for the registry's default instance whenever that is
    not the built-in `agent-vm` (a small mirror of `instances.js` lives in the patch). The same
    Desktop control offers "Update Construct" (runs `Update-Construct.ps1 -Repo -Ref`) when
    the installed Construct is behind its ref; both scripts run in a visible console via
    `cmd /d /s /c "start <title> /wait powershell.exe -File ..."` (windowsVerbatimArguments,
    windowsHide, NOT `detached`). Gotcha behind that shape: Electron's main process has no
    console, Node's `detached: true` maps to DETACHED_PROCESS (the child gets no console) and
    `stdio: "ignore"` points its std handles at NUL — the earlier patch spawned powershell.exe
    that way and ran the whole reprovision invisibly. `start` gives PowerShell a fresh console
    with working stdin/stdout; `/wait` lets the hidden cmd relay the exit code to the app. The Desktop side lives in the
    T3 source overlay (`patches/t3code/overlays/apps/desktop/src/updates/
    ConstructUpdates.ts`), reads the same `.construct-settings.json` markers as `updates.js`
    and applies the same rules (`isProvisionStale`, compare-API 404 = update available).
  - `lib/AgentVm.Instances.ps1` — the PowerShell twin of `src/instances.js`: same file,
    same schema, same normalization and identity rules. Change both together.
  - `lib/AgentVm.Remote.ps1` — the PowerShell `constructd` client `src/remotehost.js`
    mirrors: `New-ConstructApiAuth` (credential providers), `Invoke-ConstructApi`,
    `Wait-ConstructJob`, the DPAPI token store and the shared `<slug>.pin` file.
  - `drivers/Load-ConstructDriver.ps1` — dot-source it (a function cannot dot-source into
    its caller's scope) to get the backend's contract functions; `-ServiceUrl`/`-Auth` are
    empty and inert for the local path. Contract: `docs/drivers.md`.
  - `lib/AgentVm.Common.ps1` — `Get-ConstructSettingsPath` (`.construct-settings.json`
    next to scripts), `Read/Save-ConstructSettings` (merge), `Resolve-GitIdentity`,
    `Get-ConstructBackupDir` (backup dir next to scripts), `Invoke-TuiConfirm`,
    `Get-AgentVmAutomaticCheckpoint`/`Get-AgentVmAutomaticCheckpointId`/
    `Test-AgentVmCheckpointNamePattern` (automatic-checkpoint classification;
    unit-tested via the `-Snapshots`/`-Wmi` seams in `test/host-lib.test.ps1`).
- Claude recorder contract (from the installed `anthropic.claude-code-*/extension.js`):
  - `rec` argv: `-q --buffer 1024 -t raw -r 16000 -e signed -b 16 -c 1 -`
  - `arecord` argv: `-q -f S16_LE -r 16000 -c 1 -t raw`
  - format: **raw PCM S16_LE, 16 kHz, mono**, on stdout; stopped by SIGTERM.
  - native module tried first; on a deviceless VM it fails → falls back to
    `rec`/`arecord` found on PATH (`/usr/local/bin` wins over `/usr/bin` sox).
  - gate: `isSpeechToTextEnabled(){if(env.remoteName)return!1;if(authMethod!=='claudeai')return!1;return l5()}`.

## Roadmap history — the original control-panel batches (1–9)

**All complete.** Kept as the record of *why* each surface is shaped the way it is; it is
not a to-do list. Work after it — the instance registry, the driver extraction,
config-sync instance keying, the `constructd` service, the `hyperv-remote` driver,
`construct expose` and the idle policy — is planned and logged in
`docs/plans/modular-remote-architecture.md`, and designed in the
[Instances](#instances), [Remote hosts](#remote-hosts-hyperv-remote) and
[Forwards](#forwards-construct-expose) sections above.

Each batch: build → 3-lens adversarial pre-review (Workflow) → fix → `request_review`.
Verify with `node --check`, the test suites, and `pwsh` parse for any .ps1 edits.

1. ✓ **DONE — Host helper + settings + open-folder** — `src/host.js`: resolves
   scriptsDir (`%LOCALAPPDATA%\The-Construct\*\*` newest with Auto-Install.ps1;
   setting override `construct.scriptsDir`), projectsDir, settings read/write
   (merge; interop git keys; exclude password). `saveSettings` persists + toasts +
   re-pushes; `openProjectFolder` reveals (and creates) the projects dir; `ready`
   pushes `{type:'settings'}`. `host.test.js` covers resolution + merge against a
   fake LOCALAPPDATA tree. (See the Settings-persistence design decision for the
   on-disk schema.)
2. ✓ **DONE — Lifecycle launchers** — `src/lifecycle.js`: reprovision/export call
   `Provision-AgentVM.ps1` (`-Action provision` / `-Action export -BackupDir`)
   directly; reinstall/redownload call `Auto-Install.ps1 -Action … -BackupMode …`
   (the pre-select param added this batch), which owns the existing
   scan→export→delete→rebuild→restore orchestration + the dirty-repo and "type yes"
   gates. Launch is a host console via `child_process` + `Start-Process` (see the
   launch-model design decisions); reinstall/redownload elevate + show a modal
   confirm first. `customRebuild` maps to `-BackupMode save|existing|wipe`.
   **Non-interactive from the panel:** the extension passes the EFFECTIVE project
   selection as `-Projects <csv>` (`effectiveProjects()`: the persisted
   `host.readSelectedProjects` if any, ELSE the VM's CURRENT projects via a quick
   `probeOnce` — so a reprovision KEEPS what's installed instead of the console
   re-prompting and defaulting to "default", which would drop them). `run` prefers the
   caller's list, falling back to the saved selection; omitted only when we truly can't
   tell (offline + nothing saved), where the script keeps its own prompt. Reprovision
   also passes `-NonInteractive` so `Provision-AgentVM.ps1`
   auto-picks the SMB drive letter instead of prompting (reinstall/redownload get this
   via Auto-Install's `-Auto` into Provision). The child argv has **NO `-NoExit`** — the
   scripts pause themselves at the end ("Press Enter to exit", try/finally on success OR
   error; `Update-Construct.ps1` gained the same), so the window stays readable and then
   CLOSES on Enter rather than dropping to an interactive PowerShell prompt.
   Deferred: the failure → backup-reuse retry (the PS save/restore flow already handles a
   failed save by offering to continue/cancel in-console). `lifecycle.test.js` covers it.
3. **Update checks** (split into 3a/3b):
   - 3a ✓ **DONE — Construct self-update** — `src/updates.js`: GitHub
     compare(`installedCommit...ref`) vs the marker in settings → header banner +
     `behind`, folded into state (best-effort, cached). `updateConstruct` launches
     `Update-Construct.ps1` (download + reinstall the panel, no VM rebuild) which
     re-records the marker. `updates.test.js` covers it.
   - 3b ✓ **DONE — Agent updates** — `updates.js`: per-agent latest from npm
     (`@anthropic-ai/claude-code`) / GitHub releases (`openai/codex`, `sst/opencode`),
     compared (major.minor.patch) to the probed version → `{latest, updateAvailable}`
     folded into `state.agents` (best-effort, cached; only when online). `updateAgents`
     runs `buildAgentUpdateScript` over SSH (`claude update`; re-run the codex/opencode
     installers, guarded by `command -v`) inside a progress notification, then re-probes.
     The script uses `set -uo pipefail` + a `rc` accumulator + `exit $rc`, so its exit
     code (which drives the success/failure toast) is non-zero iff an attempted update
     actually failed — verified through bash with a mocked PATH.
3.5. **Remote open / VM control** (user-requested, inserted; `src/remote.js` + installer).
   Decision-complete spec so it can resume without re-asking:
   - ✓ **DONE — Connect** — open `/root/repos` over Remote-SSH, REUSING the current
     window (`vscode.openFolder` + `vscode-remote://ssh-remote+agent-vm/root/repos`);
     gated on `online && connected===false` (`connected` from `vscode.env.remoteAuthority`).
     `remote.test.js` (12) + ui-smoke connect checks.
   - ✓ **DONE — VM power state** (`src/vmpower.js`) — host Hyper-V probe
     `Get-VM -Name Agent-VM` with CAPTURED stdout (`queryVmState`, `-EncodedCommand`,
     child_process; missing VM → FQID `InvalidParameter*` → `absent`, any other failure
     → `unknown`) → `vmState` (`running`|`off`|`absent`|`unknown`), folded into pushed
     state by `withVmState`. To avoid the (elevation-gated) Hyper-V query in the common
     case, `withVmState` only runs it when OFFLINE — SSH-reachable already means running.
     Control morphs: `online&&connected===false` → **Connect**; `!online&&vmState==='off'`
     → **"Start & connect"** (`runStartAndConnect`: elevated `Start-VM` via
     `buildElevatedCommandLaunch` + UAC, then poll `ssh.isReachable` ≤150s and `openOnVm`);
     `online` → **"Shutdown"** (`runShutdown`: confirm, then `systemctl poweroff --no-block`
     over SSH; warns if THIS window is attached). Both surfaces; `vmpower.test.js` (38) +
     ui-smoke power-button checks. Graceful: `vmState==='unknown'`/`absent` shows no Start
     button; off-Windows `queryVmState` resolves `unknown` without spawning.
   - ✓ **DONE — Add project** (`src/remote.js` + `runAddProject`) — "+ add project" in
     the Projects actions row → `showInputBox` (validated by `isLikelyGitUrl`) → clone
     into `/root/repos/<name>` over SSH, INJECTION-SAFE: `buildCloneScript` base64-embeds
     the URL + dest and decodes them ON the VM, then `git clone -- "$url" "$target"` (data,
     never shell). `name` = `repoNameFromUrl` (last segment minus `.git`). Existing dir →
     exit 3 → "Open it?"; unreachable (code<0) → "couldn't reach the VM"; other → error
     toast. Success opens `/root/repos/<name>` in a **NEW** window. `remote.test.js` (43,
     incl. a hostile-URL non-injection proof) + ui-smoke add-project check.
   - ✓ **DONE — Open project (per-chip)** — inline **▷** button on each project chip
     (`media/panel.js` renderProjects; chip-body click still posts `editProject` — ▷
     stopPropagation). Posts `openProject`+name → `runOpenProject` reads the host profile
     `<scriptsDir>/projects/<name>.json` (`host.readProjectProfile`, name sanitized
     against traversal, BOM-stripped) and opens `remote.projectOpenPath(profile)` in a
     **NEW** window: the single repo's folder (`repo.directory` else basename(url) minus
     `.git`) when the profile has exactly one repo, else `/root/repos`. Falls back to
     `/root/repos` for a missing/0/multi-repo profile. `host.test.js` (40) +
     `remote.test.js` projectOpenPath + ui-smoke ▷ checks.
   - ✓ **DONE — Installer support** (host PowerShell + extension; `lib/AgentVm.Common.ps1`
     helpers). `install.ps1` (non-elevated, before Auto-Install self-elevates) calls
     `Ensure-VSCodeRemoteSsh`: detects `code`, else `winget install --id
     Microsoft.VisualStudioCode --scope user` (skips with a manual hint if winget is
     absent), then `code --install-extension ms-vscode-remote.remote-ssh` (idempotent).
     `Auto-Install.ps1`, after a successful create, calls `Add-HyperVAdminMembership`
     (adds the user to **Hyper-V Administrators** by well-known SID S-1-5-32-578 so the
     non-elevated `Get-VM` probe → "Start & connect" works without UAC; effective at next
     sign-in) and prints the `Get-RemoteOpenLink` deep link
     (`vscode://vscode-remote/ssh-remote+agent-vm/root/repos`). The dashboard opens
     ALONGSIDE via the extension: `activate()` → `maybeAutoOpenPanel` opens the panel once
     per workspace when `remote.shouldAutoOpenPanel(remoteAuthority, alreadyOpened)` (i.e.
     connected to the VM and not yet auto-opened). All host helpers are best-effort (never
     abort the install); `Ensure-VSCodeRemoteSsh` checks `$LASTEXITCODE` from
     `code --install-extension` (a non-zero native exit doesn't throw in PS 5.1) so it
     can't falsely report success. Tested by `test/host-lib.test.ps1` (pwsh: link shape,
     Find-VSCodeCli null-base safety, the exit-code shim) + `shouldAutoOpenPanel`
     (`remote.test.js`).
   - **Decisions**: Connect = current window; Add + Open-project = NEW window; per-chip =
     inline ▷ (edit stays on chip-body, later); Shutdown = `poweroff` over SSH; Start =
     elevated `Start-VM`; VS Code install = winget (user scope) else skip+link; dashboard
     alongside = auto-open the panel on first VM-connected activation. Requires the
     Remote-SSH extension + the `agent-vm` SSH Host alias (the provisioner writes it).
   - **NOTE** — this completes the extension/host side of remote-open. Still separate:
     item 8 copies `extension/` into `%USERPROFILE%\.vscode\extensions\` so the panel (and
     thus the auto-open-on-connect) is actually installed on the host.
4. ✓ **DONE — Projects** — `src/projects.js` (pure) + host profile helpers.
   `importFromVm` (auto-import) scans the VM's checked-out repos OVER SSH (a jq-free TSV walk,
   `buildScanScript`/`parseScan`, mirroring bin/scan-repos.sh's core — chosen over
   `Provision-AgentVM.ps1 -ScanReposOnly` because that first uploads a repo archive
   and mutates the VM; here we only READ) and `planImport` writes a minimal profile
   for each repo not already covered by an existing profile's `repos[].url` or name
   (merge, never overwrite — same rule as bin/export-config.sh; repos with no origin
   remote are reported skipped). `selectProfiles` = a multi-select QuickPick that
   persists the ticked set as the forward-compat `projects` key in
   `.construct-settings.json` (`host.saveSelectedProjects`, mirroring `vmMemoryGB`
   etc. for the installer/`-Projects` to adopt) and reflects it in the chips — HONEST:
   it records the selection for the next Reprovision/Reinstall, it does NOT re-apply
   to the running VM (the QuickPick copy says so). `editProject` reads the host
   profile (`host.readProjectProfile`), posts it as `{type:'editProject'}`, and the
   panel opens a modal (repos rows / SDK lines / MCP JSON / host-pkgs+provision-cmds
   textareas) that posts `{type:'saveProject'}` back; `host.writeProjectProfile`
   (traversal-safe, BOM-less pretty JSON) writes it after `projects.sanitizeProfile`
   coerces it to `project.schema.json` (drops unknown keys, enforces types, name from
   the arg not the object so it can't rename/traverse; the modal round-trips the
   un-edited `tests` block so a save can't drop it). Chips now come from the LOCAL
   profile files (`host.listProjectProfiles`) + the persisted selection (folded in by
   `withProjects`), seeded from the live VM `PROJECTS=` list until the user saves one.
   `host.test.js` (list/write/select + traversal) + `projects.test.js` (scan/parse/
   plan/reconcile/sanitize incl. injection+pollution) + ui-smoke modal checks.
5. ✓ **DONE — Usage** — `src/usage.js`: `buildUsageScript` runs ccusage over SSH
   (base64-as-data, mirroring `Get-AgentUsage.ps1`) for claude/codex/opencode;
   `parseUsage`/`parseToolUsage` read `totals.totalCost` (claude/opencode) or
   `costUSD` (codex) into per-agent rows (exact tokens + estimated cost) + a total;
   `augment(state,{report})` folds it in as a SEPARATE slow best-effort+cached pass
   (ccusage may self-install on first run, so it rides after the base+update pushes).
   `exportUsage` collects the raw combined document and saves it via a Save dialog
   (`collectRaw`/`buildExportPayload`). `usage.test.js` (105). renderUsage already
   consumed the shape.
   - **Daily/monthly/total period tabs (default daily).** The panel offers a 3-way `.utab`
     toggle: `daily` = today, `monthly` = this calendar month, `total` = all-time. `weekly`
     is deliberately NOT offered because `ccusage codex` supports session/daily/monthly but
     NOT weekly — a weekly report errors for Codex and drops it from the table (the reported
     "Codex missing from the dashboard" bug; the old default WAS weekly). daily/monthly are
     scoped with `--since/--until` computed from the VM's own clock; total runs with NO window
     (`totals` is a LIFETIME sum regardless of granularity, so without a window daily/monthly
     would just equal total — and daily==monthly on the 1st of the month, which is why the
     always-distinct `total` view was added). The selected period lives in `extension.js`
     `usageReport` (shared across surfaces, stamped into state as `usagePeriod` by `postState`
     at SEND time so the tab highlights correctly even on a late push); `{type:'setUsagePeriod'}`
     updates it + `refreshAll`s. The usage cache is keyed per-report so a toggle collects that
     view fresh and toggling back is instant. The webview flips the tab optimistically + blanks
     the stale numbers until the scoped collection returns. **Async race:** usage collection is
     slow, so a refresh BINDS to
     the report it started with and is DISCARDED on resolution if the live `usageReport`
     changed meanwhile (`usage.isCurrentReport`) — a stale daily run can't land as
     monthly's numbers; and every state is posted through `postState`, which stamps the
     CURRENT `usagePeriod` at send time so a late push never re-selects an out-of-date tab.
     The RENDERER also blanks the table whenever the active period changes but the push
     carries no fresh `usage` (a period with no data, a failed collect, or a surface that
     didn't get the local click-clear) — `panel.js` tracks the period whose numbers are on
     screen (`shownUsagePeriod`) and clears on a mismatch; a SAME-period push without usage
     keeps the numbers. `usage.test.js` (incl. an async ordering test) + ui-smoke tab checks.
6. ✓ **DONE — Audio host side** — `src/audio.js` `HostAudio`: on enable, push the shim +
   apply the guard patch over SSH, open a local TCP server, and spawn a persistent
   `ssh -R <vmPort>:127.0.0.1:<hostPort>` — CONFIRMED by a settle window (an ssh that
   dies early = tunnel-failed → roll back BOTH sides). `AudioSession` arms the mic only
   while a tunnel client (the VM shim) is connected and disarms on disconnect (mic never
   hot idle). Capture is a NATIVE host recorder (`makeHostMicProvider`): ffmpeg with a
   sox `rec` fallback, emitting raw 16 kHz mono S16LE PCM on stdout piped to the tunnel
   socket. **A webview cannot capture the mic** — VS Code embeds every webview in an
   iframe whose Permissions-Policy `allow` attribute is fixed (`cross-origin-isolated;
   autoplay; local-network-access; clipboard-read; clipboard-write;`) with NO
   `microphone`, so `getUserMedia` is always rejected (NotAllowedError) → dead silence;
   the old `media/audio*` capture webview was removed. **Windows dshow device selection:**
   there is NO `audio=default` pseudo-device, so `resolveWinMicDevice` runs `ffmpeg
   -list_devices true -f dshow -i dummy` ONCE (parsed by `parseDshowAudioDevices`, both the
   modern `(audio)`-tagged and the older section-header formats), caches the first audio
   device, and records `-i audio=<name>`; `construct.micDevice` overrides it (skips
   enumeration). No device → `onError('no-device')` → fall back to sox; no ffmpeg/sox →
   `onError('no-recorder')`. Both surface one honest warning per enable (deduped) so the UI
   never pretends to work. **Provisioning:** `Ensure-Ffmpeg` (`lib/AgentVm.Common.ps1`,
   best-effort `winget install Gyan.FFmpeg --scope user`, idempotent) runs at the END of
   `Provision-AgentVM.ps1` (NOT Auto-Install's up-front pre-step — winget is slow and
   shouldn't block the user's install prompts). It runs in Provision's context (elevated for
   install/reinstall, the user for a panel reprovision); `--scope user` targets the current
   user either way. VS Code restart needed for the new PATH. `audio.test.js`.
7. ✓ **DONE — Audio VM side** — `vm/` scripts pushed over SSH on enable (injection-safe,
   base64-as-data): `construct-rec-shim.sh` (rec/arecord shim streaming tunnel PCM, dies
   on SIGTERM) into `/usr/local/bin`; `construct-audio-enable.sh` installs it + applies the
   reversible `remoteName`-guard patch (a precise, idempotent substring swap) and prints
   `CONSTRUCT_GATE_PATCHED=0/1` so the host reports the truth; `construct-audio-disable.sh`
   removes the shim + restores the backup. Confirmed on THIS VM the real gate is
   `if(le.env.remoteName)return!1` in claude-code 2.1.196/2.1.197 (minified prefix `le.env.`).
8. ✓ **DONE — Install integration** (`install.ps1` / `Auto-Install.ps1` / `Provision` +
   `lib/AgentVm.Common.ps1`). Two things get set up: the control-panel extension is
   INSTALLED into VS Code, and the update marker is recorded.
   - **Install method = a real `.vsix`, not a folder copy.** Modern VS Code (verified on
     1.126) does NOT load a bare folder dropped into `%USERPROFILE%\.vscode\extensions`
     — it only loads extensions registered via `code --install-extension` (tracked in
     `extensions.json`). So `Build-ControlPanelVsix` packages the extension by hand
     (no `vsce`/Node): it stages the `extension/` payload (excluding `test/`,
     `node_modules`, `ARCHITECTURE.md`, dotfiles) plus a generated `extension.vsixmanifest`
     (templated from `package.json`, values XML-escaped) and `[Content_Types].xml` (a
     `<Default>` per file extension present), then zips them with .NET using EXPLICIT
     forward-slash entry names (the .NET-Framework backslash-entry pitfall breaks OPC
     readers). `Install-ControlPanelExtension` then runs `code --install-extension
     <vsix> --force` (checks `$LASTEXITCODE`, not the pipeline) and removes any stale
     folder-copy from the old approach. **Both `code` invocations go through
     `Invoke-VSCodeCli`, which decides success by `$LASTEXITCODE` ONLY:** it pins
     `$ErrorActionPreference='Continue'` (so under WinPS 5.1, a native stderr write can't
     be promoted to a terminating error — the bug where `code`'s DEP0169 `url.parse`
     deprecation warning was reported as "Could not install the control-panel extension"
     even though it exited 0), redirects the CLI's stderr to `$null`, and sets
     `NODE_OPTIONS=--no-deprecation` (restored after). A real non-zero exit is still
     surfaced. `test/host-lib.test.ps1` asserts the vsix structure (both OPC parts,
     forward-slash entries, `test/`/`node_modules` excluded, Identity/engine/kind from
     `package.json`) and that success/failure honors the exit code, not stderr. NOTE:
     `code --install-extension` itself can't run in the Linux CI box, so only the
     packaging + the exit-code decision logic are unit-tested.
   - **Placement.** `install.ps1` is a THIN downloader (download repo → launch
     `Auto-Install.ps1`; forwards the `-Repo`/`-Ref` PAIR only when explicit) so a stale
     local copy can't drift. The host setup that MUST run non-elevated (per-user
     `%USERPROFILE%` + winget) — `Ensure-VSCodeRemoteSsh` + `Install-ControlPanelExtension`
     — runs in `Auto-Install.ps1`'s pre-elevation step, so running `Auto-Install.ps1`
     directly (Option A / a desktop shortcut) installs the panel too. That pre-step ALSO
     records `installedCommit` (`Set-ConstructInstalledMarker`) right after the vsix install
     — the installed Construct's version. `Provision-AgentVM.ps1` records the SEPARATE
     `provisionedCommit` (`Set-ConstructProvisionedMarker`, mirroring installedCommit) at the
     end of a successful provision; it no longer writes installedCommit. `-Repo`/`-Ref`
     thread install.ps1→Auto-Install as a PAIR (`Resolve-MarkerSource`): explicit wins, else
     preserve existing settings, else canonical defaults.
   - `Update-Construct.ps1` (the panel's "Update Construct", launched by `updateConstruct`)
     re-downloads the repo, records the marker, and reinstalls the vsix directly — it never
     launches Auto-Install, so it never rebuilds the VM. `install.ps1` itself is now a THIN
     download-and-launch bootstrapper with no host setup / no `-RefreshOnly`.
9. ✓ **DONE — Docs** — user-facing `docs/control-panel.md` (a full tour of the panel:
   status, lifecycle, connect/power, updates, projects, usage, mic passthrough) + a
   README feature bullet and Documentation-table row; `extension/README.md` refreshed;
   this file kept current as the developer/design source of truth.

**🎉 The control-panel roadmap (items 1–9, plus the inserted 3.5) is COMPLETE.**

## Committed so far

> Covers the original control-panel roadmap only, up to and including item 9. The
> modular/remote batches (B1–B9) have their own dated log in
> `docs/plans/modular-remote-architecture.md` §6 — that table is the current one.

- `4931140` install SoX on the VM
- `d01e420` extension scaffold + webview + Playwright UI test
- `c2d1ec7` remove desktop-shortcut prompt
- `a8bd4ce` SSH runner + live status/version probe (+ stale-data fix)
- `cd754f6` architecture + roadmap doc (this file)
- `a5f4932` sidebar launcher + fullscreen-panel split + responsive narrow layout + WebviewPanelSerializer
- `3b483e1` host helper (`src/host.js`) + settings persistence + open-folder; `construct.scriptsDir` setting; `host.test.js`
- `106a349` lifecycle launchers (`src/lifecycle.js`) + `Auto-Install.ps1 -Action/-BackupMode` pre-select; host-console launch via child_process; `lifecycle.test.js`
- `043e63c` Construct self-update (`src/updates.js`) + `install.ps1 -RefreshOnly` marker write; update banner folded into state; `lifecycle.launchHostScript` extracted; `updates.test.js`
- `3cc6d92` agent update detection (npm/GitHub latest → per-agent badges) + `updateAgents` force-update over SSH; `buildAgentUpdateScript`
- `374b06d` Remote-SSH Connect button (`src/remote.js`) — open `/root/repos` on the VM, gated on reachable + not-already-connected; both surfaces; `remote.test.js`
- `3a02609` VM power control (`src/vmpower.js`) — host `Get-VM` state probe → `vmState`; "Start & connect" (elevated Start-VM + poll/open) and "Shutdown" (`poweroff` over SSH) buttons on both surfaces; `vmpower.test.js`
- `f2080075` Add project (`src/remote.js` clone helpers + `runAddProject`) — git URL → injection-safe `git clone` into `/root/repos/<name>` over SSH → open in a new window; "+ add project" button; `remote.test.js` extended
- `0e15f4f` Open project per-chip (`host.readProjectProfile` + `remote.projectOpenPath` + `runOpenProject`) — inline ▷ on each project chip opens its single-repo folder (else `/root/repos`) in a new window; `remoteFolderUri` percent-encodes path segments; `host.test.js` + `remote.test.js` extended
- `fd44c435` Installer support (`lib/AgentVm.Common.ps1` `Ensure-VSCodeRemoteSsh`/`Add-HyperVAdminMembership`/`Get-RemoteOpenLink`; `install.ps1` ensure VS Code+Remote-SSH non-elevated; `Auto-Install.ps1` Hyper-V Admin add + end-of-install deep link; `extension.js` `maybeAutoOpenPanel` + `remote.shouldAutoOpenPanel`) — `test/host-lib.test.ps1` (pwsh) + `shouldAutoOpenPanel`
- Install integration (`lib` `Get-VSCodeExtensionDir`/`Build-ControlPanelVsix`/`Install-ControlPanelExtension`/`Set-ConstructInstalledMarker`) — the panel installs as a PowerShell-built `.vsix` via `code --install-extension` (the old folder-copy was dropped: current VS Code doesn't load an unregistered folder). Placement: `Ensure-VSCodeRemoteSsh` + the vsix install run in `Auto-Install.ps1`'s non-elevated pre-step; `Set-ConstructInstalledMarker` runs at the end of `Provision-AgentVM.ps1`; `install.ps1` is a thin download-and-launch bootstrapper; `Update-Construct.ps1` is the panel's self-update. `test/host-lib.test.ps1` (vsix packaging + marker-pair resolution).
- `fcdbd3d` **Projects + Usage + Audio (items 4, 5, 6/7)** — built in parallel git
  worktrees, merged (only `extension.js` + `test/ui-smoke.js` needed hand resolution),
  adversarially pre-reviewed (3 findings fixed) and auto-review approved (incl. a
  tunnel-startup settle-window fix). Projects: `src/projects.js` (scan/parse/import-merge/
  select-reconcile/schema-sanitize) + `host.js` profile helpers + a panel edit modal.
  Usage: `src/usage.js` (ccusage over SSH → tokens+cost, cached augment, JSON export).
  Audio: `src/audio.js` (`HostAudio` tunnel w/ settle-window confirm + rollback,
  `AudioSession` on-demand gating, guard-patch) + `vm/*.sh` + the `media/audio*` capture
  webview; enable.sh reports `CONSTRUCT_GATE_PATCHED` so the UI is honest. Tests:
  projects 77, usage 88, audio 134, host 61, ui-smoke 107 — all green.

## Build/verify tooling (on this dev VM)

- `pwsh` installed → parse-check .ps1 edits.
- Playwright + Chromium installed under the session scratchpad (not committed);
  run the webview test with `NODE_PATH=<scratch>/uitest/node_modules node test/ui-smoke.js`.
- `node test/probe.test.js`, `node test/host.test.js`, `node test/lifecycle.test.js`
  for the plain-node units.
- Auto-review: single reviewer, serial; only the main agent calls `request_review`.
  Pre-review every batch with parallel adversarial subagents (Workflow) first.
