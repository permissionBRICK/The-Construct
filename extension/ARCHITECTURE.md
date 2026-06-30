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
- **No build step.** Plain JS. The installer copies this folder into
  `%USERPROFILE%\.vscode\extensions\construct-control-panel\`; VS Code loads it.
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
                      application/json, not vnd.github+json). constructRefreshArgs for install.ps1 -RefreshOnly.
    projects.js       [planned] import-from-VM, select, per-project edit
    usage.js          [planned] ccusage over SSH + cost
    audio.js          [planned] mic capture + ssh -R tunnel + on-demand gating
  vm/                 [planned] scripts pushed to the VM over SSH by audio.js
    construct-rec-shim.sh        rec/arecord shim (streams tunnel PCM)
    construct-audio-enable.sh    install shim + apply remoteName-guard patch
    construct-audio-disable.sh   remove shim + revert patch
  test/
    ui-smoke.js       Playwright headless-Chromium webview test (73 checks: panel + launcher + narrow overflow + settings round-trip + unwired-control honesty + connect/start/shutdown power buttons + add-project + per-chip open)
    probe.test.js     plain-node ssh-arg + probe-parse units (21 checks)
    host.test.js      plain-node scripts-dir resolution + settings merge + readProjectProfile units (40 checks; fake %LOCALAPPDATA% tree)
    remote.test.js    plain-node Remote-SSH helpers — isConnectedToVm/remoteFolderUri + repoNameFromUrl/isLikelyGitUrl/buildCloneScript/projectOpenPath/shouldAutoOpenPanel + URI percent-encoding (71 checks)
    lifecycle.test.js plain-node buildInvocation + winQuoteArg/quoting/elevation units (48 checks)
    updates.test.js   plain-node update-check units — Construct compare/cache + agent semver/latest/script + fetchJson redirects/per-host Accept, injected fetch+clock+http (62 checks)
    vmpower.test.js   plain-node Hyper-V power units — Get-VM probe/parse + Start-VM/elevated launch builders + injected-spawn queryVmState (38 checks)
```

## Webview ↔ extension message protocol

Defined in `extension.js` (handleMessage), `media/panel.js` and `media/launcher.js`.

**webview → extension**
- `{type:'ready'}` — webview loaded; triggers a probe + state push.
- `{type:'command', id, project?}` — ids: `reprovision`, `exportConfig`,
  `redownload`, `reinstall`, `updateConstruct`, `updateAgents`, `refresh`,
  `openProjectFolder`, `selectProfiles`, `exportUsage`, `importProjects`,
  `editProject` (+`project`), `connect` (open the VM over Remote-SSH),
  `startConnect` (elevated Start-VM then poll+open), `shutdown` (poweroff over SSH),
  `addProject` (prompt a git URL → clone over SSH → open in a new window),
  `openProject` (+`project`; open that project's folder on the VM in a new window).
- `{type:'setAudio', enabled}` — live mic-passthrough toggle (console switch only).
- `{type:'openPanel'}` — open the wide editor-tab panel.
- `{type:'saveSettings', settings}` — persist the settings form.
- `{type:'customRebuild', mode:'reinstall'|'redownload', backup:'save'|'existing'|'wipe', backupId}`.

**extension → webview**
- `{type:'state', state}` — full render (see shape below).
- `{type:'audio', enabled, capturing, tunnel}` — live audio status (flips the switch).
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

- **Packaging = folder copy** (no `vsce`/.vsix, no Node on the host).
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
  --no-block` over SSH). "+ add project" clones a git URL onto the VM (injection-safe,
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
  spawns a local `powershell.exe` whose `Start-Process …` opens a new visible
  console window, detached so it outlives VS Code. Quoting (verified through real
  PowerShell): the outer command is handed to the spawned shell via
  `-EncodedCommand` (base64 UTF-16LE) so there's NO Node↔shell quoting layer; the
  child argv is canonically Windows-quoted (`winQuoteArg`) and forwarded as a
  **single-string** `-ArgumentList` (an array would be space-joined without
  re-quoting, splitting a spaced path or a two-word `-GitUserName`). Settings
  values reach the script as data, not commands, so they can't inject.
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
- **Construct update check = a recorded commit marker + GitHub compare.** The
  installed commit lives in `.construct-settings.json` as `installedCommit` (with
  `constructRepo`/`constructRef`), written by `install.ps1 -RefreshOnly` (and, at
  install time, by the Install-integration batch). `updates.augment` compares
  `installedCommit...ref` via the GitHub API and folds `{update:{available,behind}}`
  + a `constructRev` label into the state. It's BEST-EFFORT and CACHED (10 min for
  a real result; 60 s for a failure, so a transient blip doesn't hide the banner for
  10 min): no marker, offline, or rate-limited → no `update` key → banner hidden.
  `updateConstruct` launches `install.ps1 -RefreshOnly` on the host (non-elevated,
  download-only), which re-records the marker so the banner clears. Agent updates
  work the same way: per-agent latest (npm/GitHub releases) vs the probed version →
  `{latest, updateAvailable}` folded into `state.agents`; `updateAgents` force-updates
  over SSH (`claude update` + re-run installers) with a progress notification.
- **Mic passthrough is on-demand.** Claude spawns `rec` only while recording and
  SIGTERMs it on stop, so the VM-side shim's tunnel connection *is* the
  record-window signal — the host opens the mic on connect, releases on disconnect.
  The mic is never hot continuously. (snd-aloop was rejected for requiring a
  constant feed.)
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
  - `install.ps1` — downloads the repo zip to
    `%LOCALAPPDATA%\The-Construct\<owner-repo-ref>\<repo>-<ref>\` and runs Auto-Install.
    Default repo `permissionBRICK/The-Construct`, ref `main`. **`-RefreshOnly`** (added
    for the panel's Update Construct) re-downloads + extracts in place, records the
    update marker (`installedCommit` from the GitHub commits API + `constructRepo`/
    `constructRef`) via the repo's own `Save-ConstructSettings`, and skips the menu.
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
     `install.ps1 -RefreshOnly` (download+extract, no menu) which re-records the
     marker. `updates.test.js` covers it.
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
4. **Projects** — `src/projects.js`: `importProjects` runs the repo scan
   (`-Action export -ScanReposOnly`) to discover checked-out repos and write/merge
   profiles; `selectProfiles` updates `PROJECTS`; `editProject` opens a modal
   (repos/runtimes/MCP/setup) editing `projects/<name>.json`.
5. **Usage** — `src/usage.js`: run the ccusage-over-SSH collector (reuse the
   Get-AgentUsage remote script), parse tokens + `costUSD`, render the usage table
   incl. estimated cost; `exportUsage` saves JSON.
6. **Audio — host side** — `src/audio.js`: hidden-webview `getUserMedia` → 16 kHz
   mono S16LE (AudioWorklet) → extension → local TCP server; `ssh -R
   <vmPort>:127.0.0.1:<hostPort> agent-vm`. Capture only while a tunnel client is
   connected (on-demand). Fallback to bundled sox if webview mic is blocked.
7. **Audio — VM side** — `vm/` scripts pushed over SSH on enable: `rec`/`arecord`
   shim (streams tunnel PCM, dies on SIGTERM) into `/usr/local/bin`; apply the
   remoteName-guard patch; disable removes both. Verify the shim + patch on this VM.
8. **Install integration** — `Provision-AgentVM.ps1` host step copies `extension/`
   into `%USERPROFILE%\.vscode\extensions\`. Record `installedCommit` for update
   checks.
9. **Docs** — `docs/` page + README section; convert/trim this file as needed.

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
- (this batch) Installer support (`lib/AgentVm.Common.ps1` `Ensure-VSCodeRemoteSsh`/`Add-HyperVAdminMembership`/`Get-RemoteOpenLink`; `install.ps1` ensure VS Code+Remote-SSH non-elevated; `Auto-Install.ps1` Hyper-V Admin add + end-of-install deep link; `extension.js` `maybeAutoOpenPanel` + `remote.shouldAutoOpenPanel`) — pwsh-parsed + `Get-RemoteOpenLink`/`shouldAutoOpenPanel` unit-tested

## Build/verify tooling (on this dev VM)

- `pwsh` installed → parse-check .ps1 edits.
- Playwright + Chromium installed under the session scratchpad (not committed);
  run the webview test with `NODE_PATH=<scratch>/uitest/node_modules node test/ui-smoke.js`.
- `node test/probe.test.js`, `node test/host.test.js`, `node test/lifecycle.test.js`
  for the plain-node units.
- Auto-review: single reviewer, serial; only the main agent calls `request_review`.
  Pre-review every batch with parallel adversarial subagents (Workflow) first.
