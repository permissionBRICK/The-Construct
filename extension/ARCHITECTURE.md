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
    host.js           [planned] locate scripts/settings/backups; read/write settings
    lifecycle.js      [planned] reprovision/reinstall/redownload/export launchers
    updates.js        [planned] Construct + agent update checks/actions
    projects.js       [planned] import-from-VM, select, per-project edit
    usage.js          [planned] ccusage over SSH + cost
    audio.js          [planned] mic capture + ssh -R tunnel + on-demand gating
  vm/                 [planned] scripts pushed to the VM over SSH by audio.js
    construct-rec-shim.sh        rec/arecord shim (streams tunnel PCM)
    construct-audio-enable.sh    install shim + apply remoteName-guard patch
    construct-audio-disable.sh   remove shim + revert patch
  test/
    ui-smoke.js       Playwright headless-Chromium webview test (35 checks: panel + launcher + narrow overflow)
    probe.test.js     plain-node ssh-arg + probe-parse units (21 checks)
```

## Webview ↔ extension message protocol

Defined in `extension.js` (handleMessage), `media/panel.js` and `media/launcher.js`.

**webview → extension**
- `{type:'ready'}` — webview loaded; triggers a probe + state push.
- `{type:'command', id, project?}` — ids: `reprovision`, `exportConfig`,
  `redownload`, `reinstall`, `updateConstruct`, `updateAgents`, `refresh`,
  `openProjectFolder`, `selectProfiles`, `exportUsage`, `importProjects`,
  `editProject` (+`project`).
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
{ online, host, hostShort, vmName, ubuntu, resources, constructRev,
  installed, reprovisioned, update:{available,behind},
  agents:[{id,name,detail,version,updateAvailable,latest}],
  projects:[{name,selected}],
  usage:{tools:[{label,tokens,tokensText,costText}], totalTokensText, totalCostText},
  audio:{enabled,capturing,tunnel}, probeError }
```

## Design decisions

- **Packaging = folder copy** (no `vsce`/.vsix, no Node on the host).
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
    `-VmDiskGB`, `-Projects`, `-AgentPassword`, `-GitUserName`, `-GitEmail`, `-Force`,
    `-Redownload`, `-SkipCreateVm`. Reinstall deletes the VM + disk.
  - `install.ps1` — downloads the repo zip to
    `%LOCALAPPDATA%\The-Construct\<owner-repo-ref>\<repo>-<ref>\` and runs Auto-Install.
    Default repo `permissionBRICK/The-Construct`, ref `main`.
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

1. **Host helper + settings + open-folder** — `src/host.js`: resolve scriptsDir
   (`%LOCALAPPDATA%\The-Construct\*\*` newest with Auto-Install.ps1; setting override
   `construct.scriptsDir`), projectsDir, settings read/write (merge; interop git
   keys; exclude password). Wire `saveSettings` (persist + toast), `openProjectFolder`
   (revealFileInOS), and push `{type:'settings'}` on load. Test: path resolution +
   settings merge against a fake LOCALAPPDATA tree.
2. **Lifecycle launchers** — `src/lifecycle.js`: build the PowerShell invocation for
   each action and run it in a dedicated integrated terminal (these can't be silent;
   they need elevation + show output). Reinstall/Redownload orchestrate
   scan→export→(re)install→restore via `-Action`/`-BackupDir`/`-RestoreDir`;
   confirm modal + dirty-repo warning (`-Action export -ScanReposOnly`). Redownload
   adds the Ubuntu re-fetch (`-Redownload`). Failure → offer retry reusing the
   just-made backup. customRebuild honors save/existing/wipe.
3. **Update checks** — `src/updates.js`: Construct = GitHub API latest commit on
   `<ref>` vs a stored marker in settings (`installedCommit`); show header banner +
   `behind`. `updateConstruct` re-runs `install.ps1` refresh-only (download+extract,
   no auto-menu) then updates the marker. Agents = compare VM `--version` to latest
   (npm/GitHub); `updateAgents` force-reinstalls over SSH (`claude update`, re-run
   installers). Fold update fields into the probe state.
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
- (this batch) sidebar launcher + fullscreen-panel split + responsive narrow layout + WebviewPanelSerializer

## Build/verify tooling (on this dev VM)

- `pwsh` installed → parse-check .ps1 edits.
- Playwright + Chromium installed under the session scratchpad (not committed);
  run the webview test with `NODE_PATH=<scratch>/uitest/node_modules node test/ui-smoke.js`.
- `node test/probe.test.js` for the unit tests.
- Auto-review: single reviewer, serial; only the main agent calls `request_review`.
  Pre-review every batch with parallel adversarial subagents (Workflow) first.
