# Construct control panel — architecture & roadmap

Developer/design notes for the `extension/` VS Code extension. This is the source
of truth for *why* things are shaped the way they are and *what is left to build*,
so work can resume without re-deriving the design.

## Goal

One VS Code panel to operate a Construct agent VM: live status, coding-agent
versions + updates, Construct self-update, project profiles, token usage & cost,
lifecycle (reprovision / reinstall / redownload / export config), and **microphone
passthrough** so voice input works over Remote-SSH.

## Architecture

- **UI extension** (`extensionKind: ["ui"]`). Runs on the user's local machine even
  when the window is attached to the VM over Remote-SSH. That single vantage point
  reaches both sides:
  - **host** — PowerShell lifecycle scripts in `%LOCALAPPDATA%\The-Construct\…`,
    and the local microphone;
  - **VM** — status/versions/usage gathered over `ssh` (the `agent-vm` key/alias).
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
  media/
    launcher.html     sidebar launcher doc (status + 3 quick actions + Open button)
    launcher.js       launcher controller: postMessage, render(state)
    panel.html        full-panel doc (CSP + {{nonce}}/{{styleUri}}/{{scriptUri}}/{{cspSource}})
    panel.css         Matrix theme shared by launcher + panel (tokens from assets/banner.svg)
    panel.js          panel controller: rain, controls, postMessage, render(state)
    icon.svg          activity-bar glyph (filled, currentColor)
  src/
    ssh.js            system-ssh runner (buildSshArgs/runRemote/runRemoteScript/isReachable)
    probe.js          REMOTE_PROBE + parseProbe/extractVersion/toState/probe()
    remote.js         open the VM over Remote-SSH: isConnectedToVm(remoteAuthority) +
                      vscode-remote://ssh-remote+agent-vm/<path> URIs; openOnVm (vscode.openFolder,
                      reuse/new window); needs the ms-vscode-remote.remote-ssh extension
    host.js           locate the scripts dir (newest %LOCALAPPDATA%\The-Construct\*\* with
                      Auto-Install.ps1, or the construct.scriptsDir override) + read/write
                      .construct-settings.json (form<->disk mapping; pure fs/path, no vscode)
    lifecycle.js      reprovision/export -> Provision-AgentVM.ps1; reinstall/redownload ->
                      Auto-Install.ps1 -Action/-BackupMode; launches a host console via
                      child_process (pure buildInvocation/buildHostLaunch; vscode lazy-required)
    updates.js        update checks (best-effort, cached, injectable fetch): Construct =
                      GitHub compare(installedCommit...ref) -> {update:{available,behind}};
                      agents = npm/GitHub latest vs probed version -> per-agent {latest,
                      updateAvailable}; buildAgentUpdateScript (SSH force-update); both folded
                      into state by augment(). fetchJson follows 3xx redirects (a moved GitHub
                      repo resolves via its 301) + picks the Accept header per host (npm needs
                      application/json, not vnd.github+json). constructRefreshArgs for Update-Construct.ps1.
    projects.js       import-from-VM + select + per-project edit — PURE transforms only
                      (buildScanScript/parseScan, planImport merge, reconcileSelection,
                      sanitizeProfile, toChips). Profile file I/O lives in host.js; the SSH
                      round-trip, edit modal and QuickPick live in extension.js.
    usage.js          ccusage over SSH -> per-agent tokens + estimated cost. buildUsageScript
                      (base64-as-data), parseUsage/parseToolUsage (totals.totalCost|costUSD),
                      number/cost formatting, augment(state) (best-effort + cached like updates),
                      collectRaw/buildExportPayload/exportFileName for the JSON export.
    audio.js          on-demand mic passthrough. HostAudio: push vm/ scripts + apply the guard
                      patch over SSH, open a local TCP server + a persistent `ssh -R` tunnel
                      CONFIRMED by a settle window (an ssh that dies early = tunnel-failed, roll
                      back both sides); parses CONSTRUCT_GATE_PATCHED so the UI is honest.
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
  vm/                 scripts pushed to the VM over SSH by audio.js on enable
    construct-rec-shim.sh        rec/arecord shim (streams tunnel PCM, dies on SIGTERM)
    construct-audio-enable.sh    install shim + apply remoteName-guard patch; prints CONSTRUCT_GATE_PATCHED=0/1
    construct-audio-disable.sh   remove shim + revert patch (restore the .bak)
  test/
    ui-smoke.js       Playwright headless-Chromium webview test (107 checks: panel + launcher +
                      narrow overflow + settings round-trip + honesty + power buttons + add-project +
                      per-chip open + project edit modal + usage table + audio substatus incl. gate-patch state)
    probe.test.js     plain-node ssh-arg + probe-parse units (21 checks)
    host.test.js      plain-node scripts-dir resolution + settings merge + readProjectProfile +
                      project-profile list/write/select + traversal (61 checks; fake %LOCALAPPDATA% tree)
    remote.test.js    plain-node Remote-SSH helpers — isConnectedToVm/remoteFolderUri + repoNameFromUrl/isLikelyGitUrl/buildCloneScript/projectOpenPath/shouldAutoOpenPanel + URI percent-encoding (71 checks)
    lifecycle.test.js plain-node buildInvocation + winQuoteArg/quoting/elevation units (48 checks)
    updates.test.js   plain-node update-check units — Construct compare/cache + agent semver/latest/script + fetchJson redirects/per-host Accept, injected fetch+clock+http (62 checks)
    vmpower.test.js   plain-node Hyper-V power units — Get-VM probe/parse + Start-VM/elevated launch builders + injected-spawn queryVmState (38 checks)
    projects.test.js  plain-node scan builder/parser + planImport merge + reconcileSelection + sanitizeProfile (injection + prototype-pollution) (77 checks)
    usage.test.js     plain-node ccusage script + parse (totalCost/costUSD/missing/error/zero/array) + formatting + cache TTL/coalesce + export payload, injected ssh+clock (88 checks)
    audio.test.js     plain-node guard-patch apply/revert/idempotency + VM script builders (injection proofs) + ssh -R argv + AudioSession gating + HostAudio enable/disable/rollback + tunnel settle-window (async early death + later death) (134 checks)
```

## Webview ↔ extension message protocol

Defined in `extension.js` (handleMessage), `media/panel.js` and `media/launcher.js`.

**webview → extension**
- `{type:'ready'}` — webview loaded; triggers a probe + state push.
- `{type:'command', id, project?}` — ids: `reprovision`, `exportConfig`,
  `redownload`, `reinstall`, `updateConstruct`, `updateAgents`, `refresh`,
  `openProjectFolder`, `selectProfiles` (multi-select QuickPick → persist),
  `exportUsage` (collect ccusage → Save dialog), `importProjects` (SSH repo scan →
  write/merge profiles), `editProject` (+`project`; opens the edit modal),
  `connect` (open the VM over Remote-SSH),
  `startConnect` (elevated Start-VM then poll+open), `shutdown` (poweroff over SSH),
  `addProject` (prompt a git URL → clone over SSH → open in a new window),
  `openProject` (+`project`; open that project's folder on the VM in a new window).
- `{type:'setAudio', enabled}` — live mic-passthrough toggle (console switch only).
- `{type:'saveProject', name, profile}` — the edited profile posted back from the modal
  (sanitized + written to `projects/<name>.json`).
- `{type:'openPanel'}` — open the wide editor-tab panel.
- `{type:'saveSettings', settings}` — persist the settings form.
- `{type:'customRebuild', mode:'reinstall'|'redownload', backup:'save'|'existing'|'wipe', backupId}`.

**extension → webview**
- `{type:'state', state}` — full render (see shape below).
- `{type:'audio', enabled, capturing, tunnel, gatePatched}` — live audio status (flips the
  switch; `gatePatched` drives the honest "chat mic button" substatus line).
- `{type:'editProject', name, profile}` — open + populate the project edit modal.
- `{type:'settings', settings}` — populate the settings form from disk.

**state shape** (every field optional; `render()` guards each, and clears
VM-derived fields when `online===false` or `probeError`):
```
{ online, connected, vmState:'running'|'off'|'absent'|'unknown',
  host, hostShort, vmName, ubuntu, resources, constructRev,
  installed, reprovisioned, update:{available,behind},
  agents:[{id,name,detail,version,updateAvailable,latest}],
  projects:[{name,selected}],
  usage:{tools:[{label,tokens,tokensText,costText}], totalTokensText, totalCostText},
  audio:{enabled,capturing,tunnel}, probeError }
```

## Design decisions

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
  quoting surface. `vmpower.startVm` (the "Start & connect" UAC launch) uses the same
  wrapper for the same reason. The launched console outlives VS Code (its own process).
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
- **Construct update check = a recorded commit marker + GitHub compare.** The
  installed commit lives in `.construct-settings.json` as `installedCommit` (with
  `constructRepo`/`constructRef`), written by `Provision-AgentVM.ps1` at install time
  and by `Update-Construct.ps1` on refresh. `updates.augment` compares
  `installedCommit...ref` via the GitHub API and folds `{update:{available,behind}}`
  + a `constructRev` label into the state. It's BEST-EFFORT and CACHED (10 min for
  a real result; 60 s for a failure, so a transient blip doesn't hide the banner for
  10 min): no marker, offline, or rate-limited → no `update` key → banner hidden.
  `updateConstruct` launches `Update-Construct.ps1` on the host (non-elevated,
  download + reinstall the panel, no VM rebuild), which re-records the marker so the
  banner clears. Agent updates
  work the same way: per-agent latest (npm/GitHub releases) vs the probed version →
  `{latest, updateAvailable}` folded into `state.agents`; `updateAgents` force-updates
  over SSH (`claude update` + re-run installers) with a progress notification.
- **Mic passthrough is on-demand.** Claude spawns `rec` only while recording and
  SIGTERMs it on stop, so the VM-side shim's tunnel connection *is* the
  record-window signal — the host opens the mic on connect, releases on disconnect.
  The mic is never hot continuously. (snd-aloop was rejected for requiring a
  constant feed.)
- **Mic passthrough = ONE persistent setting.** Both switches — the console
  `#voiceSwitch` and the settings `#setMic` — drive the SAME `micPassthrough` key in
  `.construct-settings.json`. Toggling the console switch persists it (`persistMicPreference`
  → `host.saveSettings({mic})`, merge-only) and `broadcastSettings` keeps `#setMic` in
  sync; saving the settings form reconciles live audio immediately (arm if newly on,
  disarm if newly off) and `broadcastAudio` keeps `#voiceSwitch` in sync. So "enable on
  the main page" sticks. `activate()` → `maybeAutoEnableAudio` reads `micPassthrough` and,
  if on AND the VM is reachable, arms at startup via `enableAudio(..., {auto:true})` —
  FULLY SILENT (no notification progress, no toasts; the switch reflects the result, and a
  down VM / a second window that already holds the tunnel shouldn't nag). A manual enable
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
- **Destructive flows default to save→restore**; one-time overrides (existing
  backup / clean wipe) live in Settings → Custom reinstall, not as a persisted
  policy. On failure, offer a retry reusing the backup already taken.

## Key repo references (for resuming)

- Optional-feature template: `bin/setup-smb-share.sh`; orchestration `bin/provision.sh`;
  idempotent config writer `bin/config-set.sh`; example `config/config.env.example`;
  systemd install pattern in `bootstrap.sh`.
- AI tools: `bin/install-ai-tools.sh` — opencode installer re-runs to update;
  **claude/codex are skipped if already present**, so "update agents" must force a
  re-run (`claude update` / re-run installers). sox installed in `install_claude_code()`.
- Lifecycle entrypoints (host PowerShell):
  - `Provision-AgentVM.ps1` — params incl. `-Action provision|export`, `-BackupDir`,
    `-RestoreDir`, `-ScanReposOnly`, `-Projects`, `-AiTools`, `-VmHost`, `-HostAlias`,
    `-GitUserName`, `-GitEmail`. The reprovision entrypoint.
  - `Auto-Install.ps1` — web-install + reinstall/reprovision menu; params incl.
    `-VmDiskGB`, `-VmMemoryGB`, `-Projects`, `-AgentPassword`, `-GitUserName`,
    `-GitEmail`, `-Force`, `-Redownload`, `-SkipCreateVm`. Reinstall deletes the
    VM + disk. **`-Action reprovision|reinstall|redownload|export`** bypasses the
    interactive menu (added for the panel); with reinstall/redownload,
    **`-BackupMode save|existing|wipe`** pre-answers the save/restore prompts. The
    dirty-repo scan and the `Confirm-Reinstall` "type yes" delete still run.
  - `install.ps1` — THIN web bootstrapper: downloads the repo zip to
    `%LOCALAPPDATA%\The-Construct\<owner-repo-ref>\<repo>-<ref>\` and runs Auto-Install.
    Default repo `permissionBRICK/The-Construct`, ref `main` (forwards `-Repo`/`-Ref`
    only when explicit). No host setup of its own.
  - `Update-Construct.ps1` — the panel's "Update Construct" self-update: re-download the
    repo in place, record the update marker (`installedCommit` from the GitHub commits
    API + `constructRepo`/`constructRef` via `Set-ConstructInstalledMarker`), and
    reinstall the control-panel extension. Does NOT rebuild the VM.
  - `Get-AgentUsage.ps1` — ccusage over SSH → combined JSON; SSH connection logic
    (key `~/.ssh/agent_vm_ed25519` else `agent-vm` alias) mirrored in `src/ssh.js`.
  - `lib/AgentVm.Common.ps1` — `Get-ConstructSettingsPath` (`.construct-settings.json`
    next to scripts), `Read/Save-ConstructSettings` (merge), `Resolve-GitIdentity`,
    `Get-ConstructBackupDir` (backup dir next to scripts), `Invoke-TuiConfirm`.
- Claude recorder contract (from the installed `anthropic.claude-code-*/extension.js`):
  - `rec` argv: `-q --buffer 1024 -t raw -r 16000 -e signed -b 16 -c 1 -`
  - `arecord` argv: `-q -f S16_LE -r 16000 -c 1 -t raw`
  - format: **raw PCM S16_LE, 16 kHz, mono**, on stdout; stopped by SIGTERM.
  - native module tried first; on a deviceless VM it fails → falls back to
    `rec`/`arecord` found on PATH (`/usr/local/bin` wins over `/usr/bin` sox).
  - gate: `isSpeechToTextEnabled(){if(env.remoteName)return!1;if(authMethod!=='claudeai')return!1;return l5()}`.

## Remaining roadmap (one batch each, via auto-review)

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
   Deferred to later batches: passing `-Projects` (Projects batch) and the failure
   → backup-reuse retry (the PS save/restore flow already handles a failed save by
   offering to continue/cancel in-console). `lifecycle.test.js` covers it.
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
   `importProjects` scans the VM's checked-out repos OVER SSH (a jq-free TSV walk,
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
   `augment(state)` folds it in as a SEPARATE slow best-effort+cached pass (ccusage may
   self-install on first run, so it rides after the base+update pushes). `exportUsage`
   collects the raw combined document and saves it via a Save dialog
   (`collectRaw`/`buildExportPayload`). `usage.test.js` (88). renderUsage already
   consumed the shape, so no webview change was needed.
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
   best-effort `winget install Gyan.FFmpeg --scope user`) runs in `Auto-Install.ps1`'s
   non-elevated pre-step alongside `Ensure-VSCodeRemoteSsh`, so the one-liner install puts
   ffmpeg on the host (VS Code restart needed for the new PATH). `audio.test.js`.
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
     directly (Option A / a desktop shortcut) installs the panel too. The installed-commit
     marker (`Set-ConstructInstalledMarker`) moved to `Provision-AgentVM.ps1`, recorded at
     the end of a successful provision (writes the scripts dir, not `%USERPROFILE%`, so
     elevated is fine). `-Repo`/`-Ref` thread install.ps1→Auto-Install→Create-AgentVM→
     Provision as a PAIR (`Resolve-MarkerSource`): explicit wins, else preserve existing
     settings on a param-less reprovision, else canonical defaults.
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
