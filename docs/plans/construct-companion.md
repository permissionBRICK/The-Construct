# Construct Companion: the per-user host agent and tray app

Status: design frozen 2026-09-11 (owner decisions D1–D11). S3 binds the production
Windows app to the real IPC dispatcher and per-instance runtimes, including the client
install offer and local/remote/reprovision hooks. Linux build and fake-mode integration
are verified below. Native Windows build/selftest and field operation remain pending:
the relay answered SDK queries, but source transfer failed; no executable ran there.
Explicit unsupported workflows remain in companion/README.md.

Author: Fable (design). Implementers: see [Work packages](#13-work-packages).

- [1. Why](#1-why)
- [2. Decisions](#2-decisions)
- [3. What exists today](#3-what-exists-today)
- [4. Architecture](#4-architecture)
- [5. Paths, files, registry](#5-paths-files-registry)
- [6. Process model and activation](#6-process-model-and-activation)
- [7. IPC contract](#7-ipc-contract)
- [8. Runtime jobs](#8-runtime-jobs)
- [9. UI](#9-ui)
- [10. VS Code extension integration](#10-vs-code-extension-integration)
- [11. T3 Code integration](#11-t3-code-integration)
- [12. Install, update, autostart, release](#12-install-update-autostart-release)
- [13. Work packages](#13-work-packages)
- [14. Testing and parity](#14-testing-and-parity)
- [15. Documented limitations](#15-documented-limitations)

## 1. Why

Three host-side jobs of the VS Code extension end with the last VS Code window:
`construct expose` client forwards, `construct notify` toasts, and microphone passthrough.
T3 Code Desktop users have none of them unless VS Code happens to be open. The extension is
`extensionKind: ui`, so every one of these runs inside the extension host on the user's
Windows PC, once per window, arbitrated through leases on the VM.

Construct Companion moves that per-user host agent into its own process that starts at
login, lives in the notification area, needs no administrator rights, and owns the complete
control panel. The VS Code extension and T3 Code Desktop become clients of it. The extension
keeps a full in-process fallback for hosts without the Companion.

## 2. Decisions

Taken by the project owner on 2026-09-11. Record them; do not reopen.

| # | Decision |
|---|---|
| D1 | Full scope in one delivery: runtime jobs, tray, popup, complete control panel (settings, projects, usage, config sync, forwards, mic, agents, T3), host administration panel, extension client mode, T3 button, installer and release pipeline. |
| D2 | The extension keeps its in-process implementation as fallback when no Companion is running. Companion mode is automatic when a live Companion is detected; `construct.companion` = `auto` (default) or `off`. |
| D3 | Microphone capture is native in-process WASAPI (NAudio is allowed for this one purpose). No ffmpeg or sox on the host. |
| D4 | Client IPC is loopback HTTP + JSON + server-sent events with a bearer token. The Companion itself speaks HTTPS to `constructd` for `hyperv-remote` instances (certificate pin, DPAPI token or Negotiate). |
| D5 | Name: **Construct Companion**. Executable `ConstructCompanion.exe`. Source folder `companion/`. |
| D6 | The Companion becomes the owner of the settings form and of the control panel. In companion mode, "Open Control Panel" in VS Code activates the Companion's panel window. The in-VS-Code sidebar launcher remains and is proxied. |
| D7 | .NET 10, self-contained win-x64, no runtime prerequisite. Built locally when a .NET 10 SDK is present on the host, otherwise downloaded from an immutable GitHub release, following the host-service release pattern. |
| D8 | The webview assets (`extension/media/*`) are the single source of the panel, launcher and host-admin UI. The Companion hosts them unchanged in WebView2 through a message-bridge shim. No second UI implementation. |
| D9 | Guest-side shell scripts and every pure decision (planners, parsers, argv builders, wire documents, settings mapping) exist once per language but are proven identical through shared golden fixtures generated from the JavaScript implementation. |
| D11 | The Companion is installed on the client PC for remote installs too (`-Backend hyperv-remote`, Add Remote Host, New Remote VM), not only for local Hyper-V installs; the client PC is where forwards, toasts and the mic end up. |
| D10 | No admin at any point: per-user install directory, HKCU Run key, HKCU protocol handler, loopback port. Elevated actions stay what they are today (UAC-prompted PowerShell consoles). |

## 3. What exists today

Read these before implementing; this section only orients.

| Concern | Where |
|---|---|
| Forwards (`construct expose`) | `docs/expose.md` (wire contract), `extension/src/forwarder.js` (pure core + planner + guest scripts), `extension/src/forwarder-ui.js` (transport), `extension/ARCHITECTURE.md` §Forwards |
| Notifications | `extension/src/notify.js` (watch script, claim, toast XML), `bin/construct` (`notify` verb) |
| Mic passthrough | `extension/src/audio.js`, `extension/vm/construct-rec-shim.sh`, `construct-audio-enable.sh`, `construct-audio-disable.sh` |
| Status | `extension/src/probe.js` (SSH probe, TAB-separated keys), `extension/src/vmpower.js`, `extension/src/drivers/hyperv-local.js` (`Get-VM` probe, elevated `Start-VM`), `extension/src/drivers/hyperv-remote.js` |
| Instances and state files | `extension/src/instances.js`, `extension/src/instancestate.js`, `extension/src/host.js`, `lib/AgentVm.Instances.ps1`, `lib/AgentVm.InstanceState.ps1` |
| Remote host client | `extension/src/remotehost.js`, `lib/AgentVm.Remote.ps1`, `service/README.md` (routes), `docs/remote-host.md` |
| Lifecycle launches | `extension/src/lifecycle.js` (PowerShell invocations, UAC rule), `extension/src/hostconversion.js`, `extension/src/hostupdate.js` |
| Config sync | `extension/src/configsync.js`, `docs/config-sync.md` |
| Projects, usage, updates, T3 | `extension/src/projects.js`, `usage.js`, `updates.js`, `t3code.js`, `repatch.js` |
| Host administration | `extension/src/hostadmin.js`, `hostadmin-ui.js`, `media/hostadmin.*` |
| Webview protocol | `extension/ARCHITECTURE.md` §Webview ↔ extension message protocol; `extension.js` `handleMessage` |
| Windows client install | `install.ps1`, `Auto-Install.ps1` (non-elevated pre-step installs the VSIX), `Update-Construct.ps1`, `lib/Construct.Iso.ps1` (local build or pinned download pattern), `lib/AgentVm.Common.ps1` (`Build-ControlPanelVsix`, `Find-VSCodeCli`, `Start-T3DesktopDetached`) |
| Release pattern | `.github/workflows/host-release.yml`, `service/host/New-ConstructHostPackage.ps1`, `docs/host-release.md`, `service/src/Constructd.Windows/Updates/GitHubReleaseSource.cs` |
| T3 Desktop integration | `/root/repos/construct-t3-builds` overlays `apps/web/src/components/settings/ConstructProviderRow.tsx`, `apps/desktop/src/updates/ConstructUpdates.ts`; Construct side `Get-ConstructT3PairingLink.ps1`, `Update-T3Code.ps1` |

Facts that shape the design: the host has no Node and no .NET runtime; Windows PowerShell
5.1 is the floor for scripts; `Get-VM` needs Hyper-V Administrators membership (unchanged);
`constructd` client-forward acks require the owner's user credential, never the VM token;
WebView2 Evergreen ships with current Windows 10/11; a `net10.0-windows` WinForms + WebView2
project compiles on this Linux VM with `EnableWindowsTargeting` (verified 2026-09-11; the
WebView2 package's WPF assembly raises MSB3277 unless excluded, and the build gate is zero
warnings).

## 4. Architecture

```
companion/
  Construct.Companion.sln
  README.md
  src/Construct.Companion.Core/       net10.0. ZERO package references. Pure: paths, registry,
                                      instance state, settings mapping, forward planner and
                                      wire docs, guest-script templating, notify claim + toast
                                      document, audio contract, probe parser, ssh argv builders,
                                      lifecycle invocations, config-sync planners, remote-host
                                      client logic over an injected HTTP seam, IPC DTOs, the
                                      message dispatcher over injected seams. Builds and tests on Linux.
  src/Construct.Companion.Host/       net10.0. The runtime: per-instance runtime supervisors,
                                      ssh process supervisor, git runner, Kestrel IPC server
                                      (FrameworkReference Microsoft.AspNetCore.App), SSE bus,
                                      state aggregation, logging. Runs on Linux in fake mode for tests.
  src/Construct.Companion.Windows/    net10.0 (NOT -windows). [SupportedOSPlatform("windows")] only:
                                      DPAPI token store, Hyper-V state via CIM, toast raising,
                                      WASAPI capture (NAudio), HKCU Run key + protocol registration,
                                      UAC launch, VS Code / T3 Desktop launchers. Registered only
                                      when OperatingSystem.IsWindows().
  src/Construct.Companion/            net10.0-windows WinExe (WinForms, WebView2). Tray icon, popup,
                                      context menu, panel/settings/host-admin windows, dialogs,
                                      single-instance, command line. Thin: no business logic.
  src/Construct.Companion.Fakes/      In-memory fakes for every Core seam (fake ssh transport with a
                                      scripted guest spool, fake process runner, fake clock, fake
                                      hypervisor, fake remote API, fake prompts).
  tests/Construct.Companion.Tests/    xunit 2.9.3 (same as service). Parity tests consume
                                      test/fixtures/companion-parity/*.json.
  host/New-ConstructCompanionPackage.ps1   packager (zip + manifest.json + SHA256SUMS)
lib/Construct.Companion.ps1           Install-/Update-/Uninstall-ConstructCompanion, release discovery
.github/workflows/companion-release.yml
extension/src/companion.js            the extension's client (detection, proxy, deferral)
extension/vm/*.sh                     guest-script sources shared by JS and C# (see §8.0)
docs/companion.md                     user documentation
```

Layering rules mirror `service/`: Core has zero package references; everything that touches a
process, socket, file system, registry or COM object goes through an interface in
`Core/Abstractions` with a fake in `Fakes`; Windows-only code carries
`[SupportedOSPlatform("windows")]`; every external invocation is an argv array pinned by a
recording-runner test; secrets never appear in logs, exceptions, argv, or test output.

The Companion drives **every instance in the registry at once** (unlike the extension, which
drives one per window). One `InstanceRuntime` per instance owns its probe timer, forwarder,
notifier, audio session, repatch and config-sync tick. A registry change (file watcher on
`%LOCALAPPDATA%\The-Construct`) adds, removes or retargets runtimes, serialized.

## 5. Paths, files, registry

All per user. `%LOCALAPPDATA%` resolution mirrors `host.js localAppData` (fallback `%TEMP%`).

| Item | Path |
|---|---|
| Install dir | `%LOCALAPPDATA%\Programs\ConstructCompanion\` (exe, DLLs, `media\` copy of `extension/media`, `install.json`) |
| `install.json` | `{ "schemaVersion":1, "commit":"<sha>", "packageVersion":"yyyy.MM.dd+sha7", "source":"local-build"\|"release", "releaseTag":"companion-<sha>"\|null, "installedAt":"<iso>", "ipcApiVersion":1 }` |
| State dir | `%LOCALAPPDATA%\The-Construct\companion\` |
| `endpoint.json` | `{ "v":1, "port":<int>, "token":"<64 hex>", "pid":<int>, "startedAt":"<iso>", "version":"<packageVersion>", "ipcApiVersion":1 }`; written atomically (temp + rename) after the listener is bound; deleted on clean exit |
| `settings.json` | `{ "v":1, "activeInstance":"agent-vm", "uiTheme":""\|"classic"\|"terminal"\|"native", "micDevice":"", "notifications":true, "forwards":{"enabled":true,"hostLabel":""}, "repatchDelaySeconds":45, "autostart":true, "debug":false, "scriptsDir":"" }`. These are the former VS Code settings that describe the host, not VS Code. |
| Logs | `%LOCALAPPDATA%\The-Construct\companion\logs\companion.log`, rolling 5 × 1 MiB |
| Remote host credentials | unchanged: `%LOCALAPPDATA%\The-Construct\remote\<slug>.token` (DPAPI CurrentUser, base64 text) and `<slug>.pin`. The Companion reads and writes exactly the PowerShell format (`lib/AgentVm.Remote.ps1` `Save-/Get-ConstructRemoteToken`). It never uses VS Code SecretStorage. |
| Every other file | unchanged and shared: `instances.json`, `instances\<name>.json`, `<scriptsDir>\.construct-settings.json`, `projects\`, `config\`, `artifacts\t3code\`, `cache\`. The Companion must write byte-compatible content (same keys, same split rules as `instancestate.js`). |
| Run key | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `ConstructCompanion` = `"<install dir>\ConstructCompanion.exe" --background` |
| Protocol | `HKCU\Software\Classes\construct` (`URL Protocol`, `shell\open\command` = `"<exe>" --uri "%1"`) |
| Toast identity | AUMID `PermissionBrick.TheConstruct` under `HKCU\Software\Classes\AppUserModelId\` with `DisplayName` and `IconUri` pointing at the install dir; the same AUMID the extension uses, so mute settings carry over |

## 6. Process model and activation

- **Single instance per user**: named mutex `Local\ConstructCompanion`. A second process posts
  its command line to the running one (`POST /v1/ui/activate`) and exits 0.
- **Command line** (all optional, combinable):
  `--background` (start with no window; the Run key uses it), `--panel [--instance <name>]`,
  `--settings [--instance <name>]`, `--hostadmin [--host <slug>]`, `--popup`,
  `--uri construct://...`, `--quit`, `--selftest [--json] [--instance <name>]`, `--version`.
  `--selftest` runs headless: resolves paths, parses the registry and state files, binds the
  IPC listener and performs one health round trip, probes each instance once (SSH + hypervisor
  state), enumerates WASAPI capture devices, checks WebView2 runtime presence, checks toast
  registration, and prints a JSON report; exit 0 when everything that can be checked without a
  VM passed, 1 otherwise. It never starts tunnels and never writes acks.
- **`construct://` URIs**: `construct://open?instance=<name>` (panel), `construct://settings?instance=<name>`,
  `construct://hostadmin?host=<slug>`, `construct://forward?instance=<name>&id=<id>` (open the link).
  Unknown paths open the popup. Query values are validated against the registry; nothing else is
  interpreted.
- **Exit**: `POST /v1/quit` (used by the installer), the tray menu, or session end. On exit the
  Companion closes tunnels, releases every `.owner` claim it holds, deletes `endpoint.json`.
  Forward requests stay in the guest spool by contract.
- **Crash resilience**: the Run key restarts nothing; the installer registers a scheduled task
  only if the owner asks later (out of scope). A crash log line is written on unhandled exceptions.

## 7. IPC contract

Loopback HTTP/1.1, `http://127.0.0.1:<port>`, JSON UTF-8, `Authorization: Bearer <token>`
on every request except `GET /v1/health`. Requests whose `Host` is not `127.0.0.1:<port>` or
`localhost:<port>` are refused with 421. No CORS headers are ever sent. Errors are RFC 7807
problem details with `code` (camelCase) as in `constructd`. `ipcApiVersion` is 1; breaking
changes bump it and `endpoint.json`, and a client that reads a higher version than it knows
must fall back to its own implementation (the extension) or disable itself (T3).

### 7.1 Routes

| Method and path | Body / response |
|---|---|
| `GET /v1/health` | `{ "ok":true, "version", "ipcApiVersion":1, "pid", "startedAt" }` (no auth) |
| `GET /v1/state` | `{ "activeInstance", "instances":[<name>...], "snapshots":{ "<name>": Snapshot } }` |
| `GET /v1/instances/{name}/snapshot` | `Snapshot` = `{ "state":<state message>, "settings":<settings message>, "audio":<audio message>, "forwards":<forwards message>, "children", "idlePolicy", "hostAdminOffer" }`: the messages a webview would have received after `ready` |
| `POST /v1/instances/{name}/messages` | body = one webview→extension message **verbatim** (`ready`, `command`, `setAudio`, `saveSettings`, `customRebuild`, `setUsagePeriod`, `saveProject`, `saveIdlePolicy`, `openPanel`, `setInstance`). Response `202 { "accepted":true }` or a problem. Results arrive on the event stream exactly as they would have arrived in the webview. |
| `GET /v1/events` | `text/event-stream`. Events: `message` with data `{ "instance":"<name>", "message":<extension→webview message> }`; `hostadmin` with data `{ "host":"<slug>", "message":<hostadmin.* message> }`; `companion` with data `{ "type":"settings"\|"instances"\|"activeInstance"\|"notification"\|"lifecycle", ... }`; comment keepalive every 15 s. Query `?instance=<name>` filters to one instance plus `companion` events. |
| `GET /v1/hosts` | enrolled remote hosts `[ { "slug", "url", "auth":"token"\|"negotiate", "pinned":true, "admin":bool\|null } ]` |
| `POST /v1/hosts/{slug}/messages` | body = one `hostadmin.*` webview message verbatim (`hostadmin.ready`, `refresh`, `tab`, `signIn`, `action`) |
| `GET /v1/hosts/{slug}/snapshot` | last `hostadmin.state` message |
| `POST /v1/hosts` / `DELETE /v1/hosts/{slug}` | add (`{ "url", "token"?, "fingerprint"? }`; enrol with pin verification and DPAPI store) or remove a remote host, mirroring `construct.addRemoteHost` / `removeRemoteHost` |
| `GET /v1/settings` / `PUT /v1/settings` | the Companion `settings.json` object (partial PUT merges) |
| `POST /v1/instances/{name}/select` | make `<name>` the tray's active instance |
| `POST /v1/ui/activate` | `{ "view":"panel"\|"settings"\|"hostadmin"\|"popup", "instance"?, "host"? }` |
| `POST /v1/quit` | `{ "reason":"update"\|"user" }` → 202, process exits within 5 s |
| `GET /v1/logs?lines=N` | last N log lines (default 200) |

Anything the extension's `handleMessage` does that needs an interactive answer (input boxes,
quick picks, confirmations, save dialogs) is answered by the Companion's **own dialogs on the
desktop**, whichever client sent the message. The dialog text is the same text the extension
uses (it is data in `lifecycle.js`, `configsync.js`, `hostadmin.js`), rendered by WinForms.

### 7.2 Message semantics

The dispatcher is a C# port of `handleMessage` and of the modules it calls. Every message
type and every `command.id` in `extension/ARCHITECTURE.md` and in `extension.js` must be either
implemented or listed in `companion/README.md` under "Unsupported messages" with a reason and
answered with a `lifecyclePrepared`-style refusal message the panel already renders (never
silently ignored). The webview `state` shape is unchanged.

`instances[]` and `connectedInstance` follow the extension's rules: the list is present only
with more than one instance; `connectedInstance` is `null` in the Companion (nothing is
"attached over Remote-SSH"), and the extension in companion mode overlays its own
`connectedInstance` before forwarding a state message to its webviews.

## 8. Runtime jobs

### 8.0 Shared guest scripts and golden fixtures

The scripts the host runs on the VM are the contract. They move out of JavaScript string
builders into files under `extension/vm/` with `{{name}}` placeholders whose values are
already shell-quoted by the caller (`shQuote` in JS; `ShellQuote.Single` in C#, identical
rule: wrap in single quotes, replace `'` with `'\''`):

| File | Replaces |
|---|---|
| `extension/vm/forwards-capability.sh` | `forwarder.js buildCapabilityScript` |
| `extension/vm/forwards-watch.sh` | `buildWatchScript` |
| `extension/vm/forwards-reconcile.sh` | `buildReconcileScript` |
| `extension/vm/forwards-ack.sh` | `buildAckScript` (and the close/sweep script if separate) |
| `extension/vm/notify-watch.sh` | `notify.js buildWatchScript` (claim function included) |
| `extension/vm/probe.sh` | `probe.js REMOTE_PROBE` |
| `extension/vm/audio-*.sh`, `construct-rec-shim.sh` | already files; the enable/disable wrappers built in `audio.js` move too |
| `extension/vm/t3-pairing.sh` | `t3code.js buildPairingScript` |
| `extension/vm/usage.sh` | `usage.js` collection script |
| `extension/vm/repatch.sh` | `repatch.js` |

JavaScript loads them with `fs.readFileSync` at module load and substitutes; existing
behaviour and existing tests stay green. C# embeds the same files as `EmbeddedResource`
linked from `../../../extension/vm/`. A parity test on each side renders every template with
the fixture inputs and compares to the golden output.

**Golden fixtures** live in `test/fixtures/companion-parity/<area>.json`, generated by
`node extension/test/export-parity-fixtures.js` from the JavaScript implementation, committed,
and consumed by both `extension/test/parity.test.js` (self-check) and
`Construct.Companion.Tests/Parity/*Tests.cs`. Areas: guest scripts, forward wire docs and
`planActions`, port slice and reconnect delays, host-label rule (extends the existing
three-way matrix), notify claim and toast XML, audio enable/disable scripts and tunnel argv,
ssh argv builders, probe parsing, `mapToForm`/`mapFromForm`, instance derivations and
identity problems, lifecycle invocations (`buildInvocation` for every action × backend ×
declared-parameter combination), config-sync planners (`planWriteBack`, `planUpstreamImport`,
`planPublish`), update planning (`updates.js`), usage parsing. A fixture is data; when an
implementation legitimately changes, regenerate and commit the fixtures in the same change.

### 8.1 Forwards

Port `forwarder.js` faithfully: pure planner, `Forwarder` per instance with the transport
seam of `extension/ARCHITECTURE.md` §Forwards, spool ownership (`.owner`, `.owner.lock`,
90 s TTL, claim id `cc-<8 random hex>` per process), port policy (prefer `vmPort`, else the
instance's slice in 18800–19311, bind probe), supervision (settle 1200 ms, backoff 2 s→60 s,
five failures → error ack), lazy start gated on the guest capability script, watcher with the
inotify stream and 30 s reconcile, atomic acks, close and sweep. Local mode uses the spool;
remote mode polls `GET /api/v1/vms/{name}/forwards` every 10 s (`?via=` when the service
advertises `network`) and acks with the **user** credential. Child-target forwards
(`connectAddress`/`connectPort`) are supported as in `forwarder.js` §12.2.
`settings.forwards.enabled=false` stops all forwarders and releases claims; toggling
`hostLabel` restarts tunnels on the same local port.

The Companion opens the port on the user's PC with `ssh -N -L` exactly as today
(`ssh.js buildLocalForwardArgs`, key `~/.ssh/<keyName>`, alias from the registry, `-p` only
for non-22). OpenSSH client resolution: `ssh` on PATH, then `%SystemRoot%\System32\OpenSSH\ssh.exe`.

### 8.2 Notifications

Port `notify.js`: one long-lived `ssh -T` watch per instance running `notify-watch.sh`
(exactly-once claim by rename, stale-claim recovery, `#` heartbeats), 1 h TTL, at most five
toasts per tick, summarisation rule, per-app mute check. Toasts are raised **in-process**
through the WinRT toast API (`Windows.UI.Notifications` via the `Microsoft.Windows.SDK.NET`
reference of the `net10.0-windows10.0.17763.0` target in the app project, or the CsWinRT
projection; no `powershell.exe` spawn) with the same XML document `buildToastScript` emits
(golden fixture). Click activation is `construct://open?instance=<name>`. The extension in
companion mode raises no toasts and runs no notify watcher. `settings.notifications=false`
stops the watchers (entries then expire on the VM by their TTL, as today when no window is open).

### 8.3 Microphone passthrough

Port `audio.js`: enable script push + run (`CONSTRUCT_GATE_PATCHED`, `CONSTRUCT_PORTS_BUSY`
parsing), loopback TCP server on `127.0.0.1:0`, persistent `ssh -N -R <vmPort>:127.0.0.1:<hostPort>`
(ports 8767–8774, skipping busy ones), on-demand capture: the mic is opened only while a shim
connection is alive and released on disconnect. Capture is WASAPI shared-mode through NAudio
(`WasapiCapture` on the device named by `settings.micDevice`, default device otherwise),
resampled to the recorder contract (S16LE, 16 kHz, mono) and written to the socket. Two
instances recording at once share one capture and fan out. Per-instance enablement follows the
persisted `micPassthrough` key (auto-arm on start, as `maybeAutoEnableAudio`). The `audio`
message (`enabled`, `capturing`, `tunnel`, `gatePatched`) is emitted on every change. Device
enumeration (`MMDeviceEnumerator`, capture endpoints) feeds the settings window and `--selftest`.

### 8.4 Status

Per instance every 30 s (5 s "fast refresh" for five minutes after a lifecycle action, as
`beginReprovisionFastRefresh`): the SSH probe (`probe.sh`, one round trip, connect timeout
8 s). SSH answered ⇒ `online=true`, `vmState=running`. Otherwise the hypervisor state:
`hyperv-local` via CIM `root\virtualization\v2` `Msvm_ComputerSystem` (`EnabledState` and
`EnabledStateDetail` mapped to `running|off|saved|paused|absent|unknown`, saved/paused
collapsed to `off` for actions exactly as the drivers do, `saved` kept as a label), falling
back to the extension's `powershell.exe Get-VM` argv when CIM is denied; `hyperv-remote` via
`GET /vms/{name}/state`. The aggregated `state` message is identical to the extension's; the
`resources`, agent versions, usage and update data come from the same modules ported.

### 8.5 Lifecycle and power

Port `lifecycle.js` builders unchanged (golden fixtures): reprovision/export run
non-elevated, reinstall/redownload/checkpoints/Start-VM elevated through
`Start-Process -Verb RunAs` in a visible console, `Update-Construct.ps1` likewise, result
files polled as today (`lifecyclePrepared`, update result, checkpoint result, pending host
conversion with the explicit finish rule from `hostconversion.js`). `shutdown` is `poweroff`
over SSH. `connect` opens VS Code with the Remote-SSH deep link (`code` CLI resolved like
`Find-VSCodeCli`, else `vscode://vscode-remote/ssh-remote+<alias><path>` through ShellExecute).
Remote instances use the service power routes and never elevate.

### 8.6 Repatch, updates, usage, projects, T3, host admin, config sync

Ported one to one from their modules: `repatch.js` (delayed one-shot after start and after a
VM comes online, `repatchDelaySeconds`), `updates.js` (Construct update check against the
installed commit and ref; T3 prebuilt manifest discovery), `usage.js` (ccusage over SSH,
period cache), `projects.js` (profiles, selection, editor modal round trip), `t3code.js`
(enable/disable/channel serialized per instance, pairing link, open web UI), `hostadmin.js`
+ `hostadmin-ui.js` (admin detection per host on open and on switch, tabs, actions, 5 s poll
while updating, confirmations as data), `configsync.js` (git runner over `git` on PATH,
`ensureRepo`, `syncTick` with the existing lock file whose dead-owner detection makes it
cross-process safe, remotes, import/share/publish planners; the pickers are Companion dialogs).
In companion mode the extension runs none of these timers.

## 9. UI

### 9.1 Tray icon

Icons are drawn programmatically (GDI+, 16/20/24/32 px by DPI): a filled disc with a thin
ring in the state colour, and a small overlay dot when a Construct update is available.

| Colour | Meaning (active instance) |
|---|---|
| green | SSH online |
| yellow | hypervisor says running but SSH not answering (booting, provisioning), or a lifecycle action is in progress |
| grey | off or saved |
| red | absent, or `unknown` for more than two minutes, or probe error |
| grey with question mark | no instance registered / no scripts dir found |

Tooltip: `<instance> · <status> · <n> forwards · mic on/off`. Left click toggles the popup;
right click opens the context menu; double click opens the panel.

### 9.2 Popup (left click)

A borderless, topmost, auto-closing window anchored to the tray hosting
`media/launcher.html` in WebView2 through the bridge shim (§9.4). It is the same launcher as
the VS Code sidebar: status dot, host, update banner, power button, reprovision/reinstall/
redownload, Open Control Panel (opens the panel window), Host Administration, agents, meta,
refresh/logs. The WebView2 control is created once and kept alive hidden, so reopening is
instant.

### 9.3 Context menu (right click)

Instance submenu (radio; switches the active instance) · status line (disabled item) ·
Start / Shutdown / Resume (per state) · Open VS Code (`connect`) · Open T3 Code (launch the
installed Desktop app from `%LOCALAPPDATA%\Programs\t3code`; if absent, the pairing link in
the browser) · Forwards submenu (each forward: open link; close; "none" when empty) ·
Microphone passthrough (checkable) · Notifications (checkable) · Control Panel · Settings ·
Host Administration (only when a host offers admin) · Start with Windows (checkable, writes
the Run key) · Logs · About (version, commit) · Quit.

### 9.4 Windows and the bridge shim

Panel window: `media/panel.html` + `panel.js` + `panel.css` + the theme CSS
(`settings.uiTheme`), loaded through `SetVirtualHostNameToFolderMapping("construct.media",
<install dir>\media)`; the `{{cspSource}}`, `{{nonce}}`, `{{styleUri}}`, `{{themeUri}}`,
`{{scriptUri}}` placeholders are substituted exactly as `extension.js` does, with
`https://construct.media` as the CSP source. A script added with
`AddScriptToExecuteOnDocumentCreatedAsync` defines `acquireVsCodeApi()` returning
`{ postMessage: m => chrome.webview.postMessage(m), getState, setState }` (state kept in a
JS variable) and re-dispatches `chrome.webview` messages as `window` `message` events. The
C# side routes `postMessage` to the dispatcher for the window's instance and subscribes the
window to that instance's outbound messages. Settings are the panel's `#settingsView`, opened
directly by `--settings` (the window posts the panel's own "open settings" UI action).
Host-admin window: `media/hostadmin.*` the same way, keyed by host slug. The theme picker
(`themes.js buildPickerHtml`) is hosted the same way. Windows remember size and position in
`settings.json` (`windows` object). Closing a window hides it; the process keeps running.

## 10. VS Code extension integration

New module `extension/src/companion.js` (pure logic + injected fs/http seams, tested under
node) and wiring in `extension.js`:

- **Detection**: read `endpoint.json`, `GET /v1/health` with the token, verify `pid` is alive
  and `ipcApiVersion` is supported. Re-check on file change (`fs.watch` on the state dir), on
  every 30 s tick, and when a request fails. Setting `construct.companion` (`auto`|`off`).
- **Deferral**: while a Companion is live the extension starts no forwarder, notifier, audio
  session, repatch, probe timer or config-sync tick; if one is running when the Companion
  appears, it is stopped gracefully (tunnels closed, `.owner` released, watchers killed). When
  the Companion disappears the in-process implementation resumes after a 10 s grace period.
- **Proxy**: in companion mode every webview message (launcher and panel) is `POST`ed to
  `/v1/instances/<windowInstance>/messages`, and the extension subscribes to `/v1/events`
  and forwards `message` events for its instance to its webviews, overlaying
  `connectedInstance`. The `ready` message is answered from `/v1/instances/{name}/snapshot`.
  `openPanel` and the `construct.openPanel` command call `POST /v1/ui/activate {view:'panel'}`
  (D6); a new command `construct.openPanelHere` keeps the in-VS-Code editor tab available.
  `construct.openHostAdmin` activates the Companion's host-admin window.
- **Status bar and commands** that are one-shot registry or file edits (`switchInstance`,
  `addRemoteHost`, `newRemoteVm`, `registerThisVm`, `removeInstance`, `removeRemoteHost`,
  `chooseTheme`) keep working locally; the Companion notices registry changes through its
  watcher. Theme choice in companion mode is `PUT /v1/settings {uiTheme}` as well.
- **Migrations, once per host** (marker in `globalState`): VS Code settings `micDevice`,
  `notifications`, `forwards.*`, `repatchDelaySeconds` with non-default values are copied into
  Companion settings via `PUT /v1/settings`; a remote-host token in SecretStorage whose DPAPI
  file is missing is written through `powershell.exe` calling `Save-ConstructRemoteToken`
  (token over stdin, never argv). The extension keeps reading SecretStorage for its own
  fallback mode.
- **Toasts** are raised by the extension only in fallback mode, unchanged.
- **Install offer (fallback mode only)**: when `install.json` is absent, `companion: false`
  is not set, and at least one instance is registered (this is the state of a PC that added
  a remote host or VM from the VS Code commands without ever running the installer's
  pre-step), the extension shows one information message per session with an **Install
  Construct Companion** button that launches `Install-ConstructCompanion.ps1` in a visible,
  non-elevated console through the lifecycle launcher, and a "Not now" that suppresses it
  for the session. The same action is reachable as the command `construct.installCompanion`.
- `extension/ARCHITECTURE.md` gains a "Companion mode" section; `docs/control-panel.md` a
  short pointer.

## 11. T3 Code integration

In `construct-t3-builds` (both inventories, separately, per its patch rules): the Construct
providers row gains a **Settings** button next to Update / Reprovision. The desktop bridge
(`apps/desktop/src/updates/ConstructUpdates.ts` overlay) exposes one method that resolves
`%LOCALAPPDATA%\Programs\ConstructCompanion\ConstructCompanion.exe` and, when `install.json`
exists, launches it detached with `--settings --instance <name>` (row-specific) or `--panel`
(host-wide row). When the Companion is not installed the button is disabled with the tooltip
"Install the Construct Companion (Update Construct installs it)". No IPC from T3 in this
delivery. Construct side: nothing beyond the command-line contract in §6.

## 12. Install, update, autostart, release

`lib/Construct.Companion.ps1` (Windows PowerShell 5.1, dot-sourced like `Construct.Iso.ps1`):

- `Install-ConstructCompanion -ScriptsDir <dir> [-Source auto|local|release] [-Force]`:
  1. Resolve the source: `local` when `dotnet --list-sdks` lists a `10.*` SDK and
     `<ScriptsDir>\companion\Construct.Companion.sln` exists (`dotnet publish
     companion/src/Construct.Companion/Construct.Companion.csproj -c Release -r win-x64
     --self-contained true -p:InformationalVersion=1.0.0+<commit> -o <staging>`); otherwise
     `release`: newest GitHub release tagged `companion-<40 hex>` on the repository recorded
     in `.construct-settings.json` (`constructRepo`, default `permissionBRICK/The-Construct`),
     download `manifest.json`, then the payload zip, verify `payloadSha256`, extract with
     .NET `ZipFile` into a staging dir. `Receive-ConstructBinary` streams the download.
  2. If a Companion is running: `POST /v1/quit {reason:'update'}` using `endpoint.json`,
     wait up to 15 s for the pid to exit, else report and stop (never kill).
  3. Swap: rename the current install dir to `.previous`, move staging in, write
     `install.json`, delete `.previous` on success (restore it on failure).
  4. Register the Run key, the `construct` protocol and the toast AUMID, then start
     `ConstructCompanion.exe --background` detached.
  Idempotent: an identical commit already installed (`install.json.commit`) is a no-op
  unless `-Force`.
- `Uninstall-ConstructCompanion`: quit, remove Run key, protocol, AUMID, install dir; keeps
  the state dir.
- Hooks. The Companion is installed on the **client PC for every install mode** (project
  owner, 2026-09-11): client forwards, toasts and the microphone terminate on the user's
  PC no matter where the VM runs, so a remote install needs it exactly as much as a local one.
  (a) `Auto-Install.ps1` non-elevated pre-step, right next to the VSIX install; that step runs
  before the local/remote decision and before any elevation, so it covers fresh local installs,
  fresh remote installs (`-Backend hyperv-remote`) and further remote VMs added on a PC that
  already has a registry. (b) `Update-Construct.ps1` and `Provision-AgentVM.ps1`. (c) The `install.ps1` one-liner reaches (a).
  (d) A root script `Install-ConstructCompanion.ps1 [-Uninstall] [-Source auto|local|release] [-Force]`
  (same convention as `Update-T3Code.ps1`) wrapping the library, for manual runs and for the
  extension's offer in §10 on PCs that only ever added a remote host from VS Code. Both hooks
  honour `-SkipCompanion`; `.construct-settings.json` key `companion: false` opts out
  persistently. Failures are reported, never hidden, and never block the VM install. A pwsh
  test per path asserts the hook is reached with a fake installer function, including the
  remote path where the elevation relaunch never happens and the pre-step is the only step.
- Packager `companion/host/New-ConstructCompanionPackage.ps1`: zip `construct-companion-<sha7>-win-x64.zip`
  (stored) containing `app\` (publish output incl. `media\`) and `SHA256SUMS`; detached
  `manifest.json` `{ schemaVersion:1, commit, ref, packageVersion, builtAt, repository,
  releaseTag, payloadAsset, payloadSha256, sumsSha256, exe:"app\\ConstructCompanion.exe",
  ipcApiVersion:1 }`.
- Workflow `.github/workflows/companion-release.yml`: `push` on `main` with paths
  `companion/**`, `extension/media/**`, `extension/vm/**`, `lib/Construct.Companion.ps1`;
  `workflow_dispatch`; jobs: `test-linux` (ubuntu, `dotnet test companion/Construct.Companion.sln`,
  node parity tests), `publish` (windows-latest: `dotnet test`, `dotnet publish`, run
  `ConstructCompanion.exe --selftest --json` (expects "no instances" success), package, `gh
  release create companion-<sha> --target <sha>` with the zip and `manifest.json`).
  SHA-pinned actions, `persist-credentials: false`, `concurrency: companion-release-<sha>`.
- The trust model is the host-release one: HTTPS + repository control + SHA-256, no code
  signing. SmartScreen may warn on a manual first launch of an unsigned exe; the Run key
  launch does not.

## 13. Work packages

Every package: own worktree, Astra dev with Fable reviewer unless stated, builds with zero
warnings, all suites green, docs updated, deviations recorded under "Deviations" in this file.

| WP | Branch | Owns (new code goes in new files; shared touchpoints are one-line hooks) |
|---|---|---|
| S1 scaffold | `cc/s1-scaffold` | `companion/` solution, all five projects with csproj rules (warnings as errors for the build gate; MSB3277 fixed by excluding the WebView2 WPF assembly or targeting `net10.0-windows10.0.17763.0`), `Core/Abstractions` interfaces + records for every seam named in this document (ssh transport, process runner, clock, file system root, hypervisor state, remote API, prompts, toast, capture, registry/keys, launcher), `Fakes` for each, empty test project wired, guest-script extraction (§8.0) with JS tests green, fixture exporter + `test/fixtures/companion-parity/` initial set (guest scripts, ssh argv, host-label), Windows platform project skeleton, app skeleton that shows a tray icon and exits on `--quit`, `companion/README.md` (layout, rules, unsupported list placeholder), workflow skeleton. |
| S2a state | `cc/s2-state` | Core: paths, registry, instance state, settings form mapping, project profiles, update markers/checks, T3 artifacts, usage parsing, probe parsing, lifecycle invocations, remote-host client logic (pin, token seam, Negotiate seam, routes used by the panel and host admin), drivers (local via hypervisor seam, remote via API). Fixtures for all of these. |
| S2a runtime | `cc/s2-runtime` | Core + Host: forwarder (planner, wire docs, Forwarder, local + remote transports), notifier (watch, claim, toast document), audio session (contract, scripts, tunnel, capture seam, fan-out), repatch, per-instance runtime + supervisor + registry watcher, ssh process supervision with backoff, SSE bus data model. Fixtures for all. Linux end-to-end test: fake guest spool + fake ssh → forwards open/ack/close; fake notify spool → toast seam called once per entry. |
| S2a configsync | `cc/s2-configsync` | Core + Host: `configsync.js` port (git runner, repo state, sync tick with lock, remotes, staging clones, import/share/publish planners and flows), pickers through the prompts seam. Tests with a real `git` on Linux in temp repos (as the JS tests do). |
| S2a extension | `cc/s2-extension` | `extension/src/companion.js`, wiring in `extension.js`, `package.json` setting + commands (`openPanelHere`, `installCompanion`), the install offer, migrations, tests with a stub Companion HTTP server, ARCHITECTURE.md and control-panel.md updates. Must not depend on the C# code existing; the contract is §7. |
| S2b ipc | `cc/s2-ipc` | Host: Kestrel IPC server (§7 routes, auth, Host check, problem details, SSE), state aggregation, the message dispatcher (C# `handleMessage` for panel and host-admin protocols, calling the S2a modules), `hostadmin.js`/`hostadmin-ui.js` port, Linux fake-mode end-to-end test driving the dispatcher through HTTP. |
| S2b app | `cc/s2-app` | App: tray, icons, popup, context menu, WebView2 windows and bridge shim, dialogs implementing the prompts seam, single instance, command line, `construct://`, `--selftest`, settings persistence; Windows project: DPAPI, CIM state, toast, WASAPI, Run key/protocol/AUMID registration, launchers (VS Code, T3, UAC). Argv-pinned tests for every external invocation; WinRT/COM code isolated behind the seams. |
| S2b install (reviewer: Opus) | `cc/s2-install` | `lib/Construct.Companion.ps1`, root `Install-ConstructCompanion.ps1`, hooks in `Auto-Install.ps1` (pre-step, covering local and remote installs) and `Update-Construct.ps1`, packager, workflow, `docs/companion.md`, pwsh tests (`test/companion-install.test.ps1`: source resolution, manifest verification, swap/rollback, registry writes through a fake, quit handshake with a fake endpoint) and a Linux packaging layout test. |
| S2b t3 (reviewer: Opus) | `cc/t3-companion` in `/root/repos/construct-t3-builds` | Both inventories: Settings button + bridge method, unit tests in the repo's style, patch-rule compliance. |
| S3 integration | `cc/s3-integration` | Wire S2a and S2b together, complete the message matrix (every message type and command id implemented or listed), fake-mode Linux e2e through the real IPC server and the real dispatcher against fake seams, `--selftest` exercised on Linux where possible, docs finalized (`companion/README.md`, `docs/companion.md`, ARCHITECTURE.md), this document's status updated. |
| S4 cleanup (dev: Fable, reviewer: Astra) | `cc/s4-cleanup` | Simplification pass over everything: remove over-engineering, duplicated abstractions, defensive noise and verbose comments; align names and structure with this design; no behaviour change without a recorded reason; all suites green. |

Shared touchpoints and their owners: `companion/Construct.Companion.sln` and csproj files
(S1; later packages add files, not projects), `Core/Abstractions` (S1 defines; a package that
needs a new member adds it in a new partial file or a new interface and records it),
`Host/Composition` (S2b ipc owns the composition root; S2a packages expose `Add<Area>()`
extension methods in their own files), `extension/extension.js` (S2a extension only),
`Auto-Install.ps1`/`Update-Construct.ps1` (S2b install only), `test/fixtures/companion-parity/`
(each package adds its own files; the exporter is one file with one function per area).

## 14. Testing and parity

S3 verification (2026-09-11): Linux build 0 warnings/0 errors; Companion 4,725/4,725
and service 1,261/1,261 tests. Real Kestrel/dispatcher/runtime tests cover mixed and
remote-only registries, forwarding, notifications, audio, settings bytes, host-admin,
activation and shutdown; Node exercises detection/proxy/SSE against the spawned fake Host.
Parity regeneration preserves existing areas and adds actual JS settings-writer bytes
and refresh-cache TTL decisions (4,456 rows across 31 areas). See the S3 notes below
for regression suite results, retries, unsupported workflows and Windows limitations.

- Linux gates: `dotnet build companion/Construct.Companion.sln -warnaserror` (0 warnings),
  `dotnet test companion/Construct.Companion.sln`, `for f in extension/test/*.test.js; do node "$f"; done`,
  `pwsh -NoProfile -File test/companion-install.test.ps1`, existing suites unchanged.
- Parity: §8.0 fixtures; a change to a fixture must appear in the same commit as the
  behaviour change on both sides, and the reviewer checks that.
- Fake mode: `Construct.Companion.Host` runs on Linux with the fakes (`--fake` for the
  `Construct.Companion.Host` console entry used by tests), which is how the IPC, dispatcher and
  runtimes are exercised end to end here.
- Windows: `--selftest` is the field entry point. If the Jarvis relay to the Windows host
  answers (`node /root/repos/jarvis/scripts/jarvis-relay.mjs run --target haus-pc --shell powershell --timeout 300 -- '<cmd>'`,
  exit 125 or a cancelled run means it does not; do not retry endlessly), workers may run
  `dotnet build`/`--selftest --json` there read-only and must never install, register or start
  the Companion on that host in this run. During S3 on 2026-09-11 the relay answered read-only SDK queries (10.0.401), but
  source transfer failed. The temporary `C:\Temp\companion-probe` directory was removed;
  no native build, selftest, installation or registration ran.
- Field test checklist (owner, later): install through `Update-Construct.ps1`, tray states,
  `construct expose` with VS Code closed, toast click, mic in Claude Code over Remote-SSH,
  panel parity with the VS Code tab, host-admin window, T3 Settings button, update swap.

## 15. Documented limitations

- Not field-validated on Windows in this delivery (D7 and §14).
- `Get-VM`/CIM state still needs Hyper-V Administrators membership; otherwise `unknown`, as today.
- Unsigned executable (same trust model as the ISO builder and `constructd`).
- No self-update: `Update-Construct.ps1` replaces the Companion.
- Interactive prompts for proxied clients appear as Companion dialogs on the desktop.
- T3 gets a launch button only; no IPC consumption in this delivery.

## Deviations

- owner amendment 2026-09-11: plain reprovision installs or updates the client Companion through the same non-blocking opt-out-aware hook in `Provision-AgentVM.ps1`, covering panel and T3 Desktop reprovision entry paths.

- S2b ipc: automatic checkpoint apply and lifecycle preflight/live-project fallback remain documented unsupported subflows; S2a has the pieces but not the complete dialog/result workflow, so users must sync/select explicitly and apply checkpoints through VS Code/installer.
- S2b ipc: project modal saves use the existing S2a strict validation/canonicalization gate instead of legacy JS coercion; invalid/reserved profiles fail before writes.
- S2b ipc: RuntimeSupervisor accepts an optional asynchronous retarget lease so IPC commands retain their instance target and config area while runtime settings are replaced.

- S2b ipc: adds `ICompanionDesktop` and `IInstanceConnections` seams for app activation and per-instance transport composition; `RemoteApiException.Body` retains sanitized structured cascade problems for confirmations.
- S2b ipc: stores Companion host enrollment in `companion/hosts.json` and pending host updates in `companion/host-update-<slug>.json` because the extension enrollment list is VS Code globalState; registry services are also discovered.
- S2b ipc: adds an optional `error` display to the existing `lifecyclePrepared` handlers in panel.js/launcher.js; previously this message only cleared a spinner, so refusals were invisible.
- S2b ipc: HTTP tests use real ephemeral Kestrel rather than TestServer, exercising actual Host/port checks and endpoint/quit lifetime; fake-mode console uses the in-memory filesystem.
- S2b ipc: README records unported attached-window/creation/removal/update/secret-display workflows as visible refusals, plus pending automatic config-sync/enrichment and lifecycle-result wiring for integration.

(Recorded by implementers; one line each: what, why, where.)
- S3 integration: installer quit discovery also accepts the app branch's existing private `ui-endpoint.json`, including after a stale full-host endpoint, so updating/uninstalling the bootstrap app waits for graceful exit (lib/Construct.Companion.ps1).
- S2b app: standalone UI activation uses private `ui-endpoint.json` until S3 binds the full IPC host; publishing `endpoint.json` earlier would incorrectly disable the extension runtime fallback. The bootstrap explicitly refuses runtime commands; remote-state/catalog/settings runtime binding are S3 composition hooks in companion/README.md.
- S2b app: headless SSH changes the shared builder's StrictHostKeyChecking=accept-new to yes so selftest cannot write known_hosts; native design colors come from WinForms system colors, and shared theme-card metadata lives in media/theme-cards.json.
- S2b app: adds IMessageSink (host-admin scope `host:<slug>`), native data-protection/CIM/detached-process and UI/selftest seams in new Abstractions files; Windows stays net10.0 using explicit SDK projection references for in-process WinRT.
- S2b app: extracts themes.js picker template into shared media/theme-picker.html, retaining byte-identical JS output with golden fixtures; CIM maps EnabledState (EnabledStateDetail is not defined by the documented v2 schema); the existing VmPower.QueryLocalAsync owns the single Get-VM fallback.
- S2b install: elevated callers are refused by the per-user library and reported non-fatally by Update-Construct; retry from a normal desktop PowerShell window rather than installing into an administrator profile (D10). Updates also retain an explicit autostart=false setting; release pagination is capped at 20 pages with an explicit refusal if the result is incomplete.
- S2a state: adds `IStateFileSystem` (extends `IFileSystem`) for marker timestamps, directory existence and atomic create-if-absent profile writes; these host.js requirements cannot be expressed by the S1 seam.
- S2a state: adds `IUpdateSource` for public release JSON under normal CA validation; `IRemoteApi` remains the credentialed certificate-pinned host-service transport.
- S2a state: registry parse/read failures report stable diagnostics rather than copying Node-version-specific parser or OS-specific exception text; fallback and rejection behavior are unchanged.
- S2a configsync: remote links are committed immediately and staging clones are fetched on use; push-back branch stamps use UTC through IClock; invalid zip profiles are skipped with returned warnings instead of JS coercion.
- S2a configsync: new/renamed imports use the strict validation gate (JS creates coerce malformed schema); rejected imports leave local data unchanged, satisfying §8.0/8.6 validation requirements. PickItem adds Disabled/Separator and PickPrompt adds Placeholder for the JS publish dialog model.
- S2a configsync: reserved seed paths are locally ignored (also affecting JS/PS `add -A`) to enforce the frozen never-track rule even on unborn HEAD; legacy credential-bearing remotes must be re-linked before any Companion git operation to satisfy the no-secrets-in-argv rule.
- S2a configsync: add IConfigSyncStorage/IClipboard and ProcessInvocation.EnvironmentOverrides for atomic shared locks, clipboard sharing and temporary Git indexes; store writes reject unsafe/reserved names and tick reports invalid names rather than allowing Windows path failures.
- S2a configsync: malformed JSON uses the stable reason `cannot be parsed as JSON` instead of V8-specific parser details; schema validation reasons and canonical bytes remain parity-tested in Core/ConfigSync/ProfileCodec.cs.
- S1: repatch already reads `construct-patch-status.sh`/`construct-partial-streaming-enable.sh`; embed and fixture those originals instead of creating redundant `repatch.sh`; pairing keeps separate default/instance templates, and validated numeric/ID fragments retain their byte-identical spelling.
- S1: the app scaffold uses a named quit event until S2 supplies authenticated HTTP quit/activation; secondary view launches currently exit 0 without opening a view (documented in `companion/README.md`).
- S1: notify keeps `claim()` in `notify-claim-function.sh`, composed into both claim/watch templates through `{{claim}}`; C# callers render that shared function first, preserving the existing single-source composition and bytes.


- S2a runtime: added runtime registry/probe, supervised-process, forward-transport, port-reservation/probe and audio-server seams in new Abstractions files; state normalization stays with S2a state, while S3 supplies the per-instance factories.

- S2a runtime: replace a remote tunnel when its child destination changes; the service invalidates its ack, so re-acking the old address would preserve an incorrect route (Core/Forwards/Forwarder.cs).

## Integration notes (stage 1)

- Merged `cc/s1-scaffold` (`91c8902`) into `cc/integ-1` from `feat/companion` (`4dbb0ea`) with `--no-ff`; no conflicts or skipped branches. No integration code changes or new design deviations.
- Linux validation: Companion build 0 warnings/0 errors; Companion tests 230/230; service tests 1,261/1,261. Ran all 30 Node, 27 PowerShell, and 22 Bash suite files. Full counts, skips, and retry details: [stage 1 integration results](construct-companion-stage-1-integration.md).
- Config-sync has an existing default-branch test dependency: Node aborts at invalid bare-remote `HEAD` (also with the documented `CONSTRUCT_*` variables unset); PowerShell initially passes 561/568. With a process-local `init.defaultBranch=main` override, Node passes 475/475 and PowerShell 568/568. No global/repository Git configuration changed.
- Existing failures reproduced against untouched `4dbb0ea`: `test/contracts-compile.test.sh` passes 4/5 (frozen `HypervisorVmInfo` signature mismatch); `test/idle-report.test.sh` passes 100/103 (service-key count, URL, and instance-name assertions). Relevant code is unchanged; these baseline defects remain outside scaffold scope. The baseline idle test was repeated with Git HEAD available so its Git-dependent checks actually ran.
- Regenerated all four parity areas with zero fixture drift; original baseline JavaScript builders also produce identical fixture bytes. No Windows runtime validation performed; the scaffold's documented pending runtime/UI/installer work remains pending.

## Integration notes (stage 2)

- Reset `cc/integ-2` to `feat/companion` (`4f14329`), then merged `cc/s2-state`, `cc/s2-runtime`, `cc/s2-configsync`, and `cc/s2-extension` in order with `--no-ff`; none skipped. README/exporter conflicts preserve all areas and async exports; the plan conflict preserves every deviation. Full branch tips, merge commits, and per-suite counts: [stage 2 integration results](construct-companion-stage-2-integration.md).
- Fixed a cross-package process seam: `RuntimeProcessRunner` now honors configsync's `ProcessInvocation.EnvironmentOverrides`, so temporary Git indexes stay isolated when `AddRuntime()` registers first. Two real-Git composition tests verify either registration order and an unchanged ordinary index. No new design deviation.
- Linux gates: Companion build 0 warnings/0 errors; Companion tests 4,332/4,332; service 1,261/1,261; Node 31/31 suites; PowerShell 27/27 after the existing config-sync default-branch retry; Bash 20/22. Regenerated 4,192 fixture rows across 27 areas with no drift. No Windows runtime validation.
- Recorded pre-existing defects: PowerShell config-sync initially 561/568, then 568/568 with process-local `init.defaultBranch=main` and the documented `CONSTRUCT_*` variables unset; `contracts-compile.test.sh` 4/5 (frozen `HypervisorVmInfo` signature); `idle-report.test.sh` 100/103 (service-key count/URL/instance). Service and the affected scripts are unchanged; the Bash failures match the stage 1 baseline evidence. Node configsync's fixture setup was fixed by the extension branch and passes 475/475 without a retry.
- No unresolved merge-induced defect found. S2b/S3 still own IPC/dispatcher, application composition, Windows adapters/UI, install/release, and T3 launch integration; the app's failing `--selftest` remains a documented scaffold stub.


## Integration notes (stage 3 input merge; superseded by S3 below)

- Reset `cc/integ-3` to `feat/companion` (`73f838c`), then merged `cc/s2-ipc`, `cc/s2-app`, and `cc/s2-install` in order with `--no-ff`; none skipped. README/exporter conflicts preserve both packages and every async fixture export; the plan conflict preserves every deviation, including the starting branch's D11. No T3 repository work was merged. Full validation and branch details: [stage 3 integration results](construct-companion-stage-3-integration.md).
- Fixed app/installer compatibility: `Stop-ConstructCompanionForInstall` now discovers both `endpoint.json` and the standalone app's `ui-endpoint.json`, including a dead full-host PID followed by a live bootstrap PID. Four added assertions cover bootstrap uninstall reason, stale endpoint fallback, timeout refusal, and no swap on timeout. No process is killed.
- **Unfinished production composition (inherited S2b app/IPC):** `companion/src/Construct.Companion/Program.cs` still constructs the bootstrap refusal sink and `DesktopActivationServer`, not `AddCompanionHost`/the real dispatcher. The desktop therefore runs no runtime jobs and publishes no full `endpoint.json`; extension fallback stays active. Binding the platform seams, UI-thread activation/prompts, event streams, shared settings store, and host catalog requires a separate implementation beyond a few-line merge repair. The real full IPC host is exercised with fakes on Linux. Native selftest is implemented and can pass without instances, so the release workflow's selftest is not a guard against shipping this incomplete composition.
- **Unfinished IPC workflows (inherited `cc/s2-ipc`):** automatic config-sync ticks/watchers, periodic usage/update enrichment, lifecycle/checkpoint result monitoring, and host-admin polling shutdown on window closure are not wired. Explicit command refusals and subflow limitations remain listed under `companion/README.md` "Unsupported messages" (registration, project creation, removal, conversion, first VM, Construct update, one-time token display, checkpoint apply and lifecycle preflight). Desktop remote-state selftest/catalog enrichment and shared IPC/desktop settings synchronization also remain composition dependencies. These are implementation gaps, not passing-test claims of feature completion.
- **D11 gaps (inherited input branches):** the Auto-Install Companion hook is correctly before the local/remote decision in the non-admin pre-step, but root `Install-ConstructCompanion.ps1` and `construct.installCompanion`/the session install offer are absent. The installer suite tests the hook helper; it does not execute each local/remote/update entry path with a fake installer as D11 requires. Manual library installation remains available. No new install offer was invented during merge repair.
- Linux validation: Companion build 0 warnings/0 errors; Companion 4,681/4,681; service 1,261/1,261; Node 31/31 suites; PowerShell 29/29 after the existing config-sync retry (561/568 initially, 568/568 with process-local `init.defaultBranch=main` and the documented `CONSTRUCT_*` variables unset); Bash 20/22. Installer 75 assertions and package 34 assertions passed. Regenerated 4,426 parity rows across 29 areas without drift.
- Existing Bash defects remain: `contracts-compile.test.sh` 4/5 (`HypervisorVmInfo` frozen signature mismatch), `idle-report.test.sh` 100/103 (service-key count/URL/instance). Both match prior integration baseline evidence; tests and production code are unchanged. No unresolved merge-induced regression remained after the quit fix.
- No Windows build, selftest, installation, tray, WASAPI, WinRT, Hyper-V or field runtime execution was performed. The Windows target only compiled on Linux; the native behavior remains for Windows field validation.

## Integration notes (S3 implementation)

- Production composition now binds the WinForms app to `AddCompanionHost`, the dispatcher and per-instance runtimes. Tray/window subscriptions use aggregated state; IPC and desktop share settings and publish changes. Registry retargeting, active selection, host catalog, config watchers/ticks, cached update enrichment, on-demand cached usage, host-admin close/unsubscribe, URI activation and graceful endpoint/claim cleanup are wired. Selftest starts only the real IPC diagnostic path, suppresses runtime jobs/endpoint writes and interactive prompts, and treats remote-only registries without requiring local Hyper-V.
- D11 is implemented through the root installer, pure/tested once-per-session extension offer and `construct.installCompanion`; hooks cover fresh local, fresh remote, additional remote, Update and plain Provision entry paths. Export/scan/elevated provision calls skip installation; version-guarded opt-out propagation prevents duplicate child attempts. The extra Provision hook is the owner amendment recorded above.
- Linux validation: build 0 warnings/0 errors; Companion 4,725/4,725; service 1,261/1,261; Node 33/33 suite files. PowerShell 24/24 suite files after the existing config-sync default-branch retry (561/568 initially, 568/568 with process-local `init.defaultBranch=main` and the documented `CONSTRUCT_*` variables unset). Installer 75, entrypoint 33 and remote-install 206 assertions pass. Bash 20/22 suites: unchanged baseline failures remain `contracts-compile.test.sh` 4/5 (frozen `HypervisorVmInfo` signature) and `idle-report.test.sh` 100/103 (service-key count/URL/instance). No service or guest-script behavior changed.
- Added real TLS adapter tests reject a mismatched pin before authenticated HTTP, accept a matching pin, disable redirects, record Negotiate default credentials and map public-update 404s. Fake-clock tests verify shared update TTLs and per-period usage caching without background usage or duplicate probes. The settings fixture compares HTTP-dispatch writes byte-for-byte with the real JS writer. Regenerated all 4,456 parity rows across 31 areas without drift.
- Two timing-only ProcessSupervisor tests now wait for their actual signals. A round-2 rerun exposed the same test-ordering issue in the fast-probe test: it advanced the fake clock before the next delay was scheduled. It now waits for that delay rather than sleeping ten milliseconds. An AudioSession cancellation race now checks the owning lifetime before creating the server. Review round 1 observed an inherited ForwarderRuntime cancellation test failure under load (supported versus stood-down); six isolated reruns and the final complete suite pass. That observation is retained rather than claiming the first run was green.
- The explicit unsupported commands/subflows in `companion/README.md` remain: attached-window registration/conversion, project creation, removal, first-VM wizard, install-wide update orchestration, one-time token display, automatic checkpoint apply and lifecycle preflight/result monitoring. They report visible refusals where applicable; Linux wiring tests do not establish parity for these workflows. Earlier stage-3 input-merge notes describe the starting state, not the current composition.
- Windows relay read-only queries succeeded (SDK 10.0.401); source transfer failed twice, including a smaller-chunk attempt. `C:\Temp\companion-probe` was removed successfully after the last attempt. No Windows build, selftest, installation, registration, autostart or native runtime/UI exercise ran. Field validation remains pending.
