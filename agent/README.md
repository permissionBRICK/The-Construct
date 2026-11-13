# `construct-agent`: the device agent

Every desktop device runs one agent as a user process (plan `docs/plans/construct-platform-design.md`
5.1, 6.2, 8, 11.1). It holds the device's key pair and the account's root key `U`, knows the engines
its user works with, and serves the UI. It owns no VM logic: every lifecycle action is a call to an
engine, which the UI makes through the agent's proxy.

The core is platform-independent; the Linux console host and the Windows tray app wrap it. Beside
identity and the engines it does what only a device can (5.1, 7.2): local forwards to the user's VMs,
`construct notify` as desktop notifications, microphone passthrough, the SSH alias per VM, open actions,
the workspace share of a VM on this PC, start with the OS, and updating itself (12.1).

## Layout

| Path | Content |
|---|---|
| `src/Construct.Agent.Core/DeviceAgent*.cs` | The device: its key, engines, first-device setup, pairing (both sides), recovery, revocation, enrolment and key grants |
| `src/Construct.Agent.Core/Protection/` | Protection of the device's secrets at rest (libsecret or a 0600 key file on Linux) |
| `src/Construct.Agent.Core/State/` | `state.json`, the persisted state |
| `src/Construct.Agent.Core/Engines/EngineHttp.cs` | Pinned HTTPS to engines, and the probe that reads a pin |
| `src/Construct.Agent.Core/Events/` | The `/api/events` buffer and the job watcher |
| `src/Construct.Agent.Core/Http/` | The local API: guard, routes, engine proxy, OpenAPI |
| `src/Construct.Agent.Core/Repair.cs` | `construct-agent repair` |
| `src/Construct.Agent.Core/Device/` | The device features: VM links (forwards, notifications, microphone), the SSH config, open actions, the platform seam (`IDevicePlatform`) and the Linux platform |
| `src/Construct.Agent.Core/Device/Runtime/` | Moved unchanged from the Companion's host: SSH supervisor, process runner, SSH transport, port reservations, loopback audio server |
| `src/Construct.Agent.Core/Update/` | Self-update (`install.json`, `versions/`), "update everything" |
| `src/Construct.Agent.Core/Http/DeviceRoutes.cs` | The device routes |
| `src/Construct.Agent.Core/Upgrade/` | The one-time upgrade from the old client: the reader of its files (`LegacyState`, `LegacyConfigRepo`), the converter, the dialog's steps, `bridge.json` |
| `src/construct-agent/` | The console host (Linux) |
| `src/Construct.Agent.Windows/` | The Windows tray app: tray, WebView2 surfaces, single instance, DPAPI, toasts, WASAPI, `net use`, the Run value |
| `install/` | Per-user install scripts: `install-construct-agent.sh` (Linux), `Install-ConstructAgent.ps1` (Windows) |
| `openapi/agent.v1.json` | The local API's contract, generated; a test fails on drift |
| `tests/Construct.Agent.Tests/` | Agents in process against the real engine (fake mode) on a loopback HTTPS port |

It references the service's `Construct.Keys` (keys, pairing, recovery, proofs), `Constructd.Store`
(the store's wire contract), `Constructd.Sync` (sealing records with `D`) and `Constructd.Client`
(the engine's generated client). From the Companion it references `Construct.Companion.Core`
unchanged (the forward planner and forwarder, the notifier and its parsing, the audio session,
`SshArgs`, `T3Code` and the guest scripts these run) and, in the Windows app,
`Construct.Companion.Windows` (WASAPI capture, WinRT toasts, the registry, DPI). The tests use
`Construct.Companion.Fakes`.

## Commands

```sh
dotnet build agent/Construct.Agent.slnx
dotnet test agent/Construct.Agent.slnx
dotnet run --project agent/src/construct-agent -- run       # foreground; the default
dotnet run --project agent/src/construct-agent -- open      # starts it if needed, opens the UI
dotnet run --project agent/src/construct-agent -- repair    # report and restart, no engine needed
dotnet run --project agent/src/construct-agent -- autostart on|off   # the systemd user unit
dotnet run --project agent/src/construct-agent -- convert --from-legacy [--from DIR]   # the converter (Windows: construct-agent.exe convert ...)
python3 scripts/package-agent.py --output DIR --commit SHA --version X.Y.Z   # both packages
```

On Windows the app is `construct-agent.exe` (`agent/src/Construct.Agent.Windows`): no argument opens
the window, `--background` starts in the tray only (the Run value uses it), a `construct://` link (a
notification's click) or a second start opens the window of the running one, `repair` and `version`
print to the console that started them. It builds on Linux with Windows targeting.

`--data-dir DIR` (or `CONSTRUCT_AGENT_HOME`) moves the agent's directory, by default
`$XDG_DATA_HOME/construct-agent` or `~/.local/share/construct-agent`. `--ui DIR` (or
`CONSTRUCT_AGENT_UI`) names the built UI bundle; by default `ui` beside the executable, else the
first `ui/dist` above it.

## Files

| File | Content |
|---|---|
| `state.json` (0600) | Device name, account, which engine holds the store, `U` sealed to the device key, the engines with their pins. The device's private key, its store token and its engine tokens are only in protected form. |
| `protection.key` (0600) | Only with the file protection: the key that protects the secrets in `state.json`. |
| `endpoint.json` (0600) | The token file: port, token, pid, start time, version. A new token every start; deleted at stop. |
| `open.html` (0600) | Written by `open`: a page that sends the browser to the UI with the token. |
| `device.json` (0600) | The device's settings: release channel, automatic updates, the VMs with the microphone on. |
| `install.json` (0600) | The installed versions: `current`, `previous` and `pending` (12.2). |
| `versions/<version>/` | One unpacked package per version; the current and the previous one are kept. |
| `webview2/`, `shell.log`, `construct.png` | Windows only: the WebView2 profile, the tray app's error log, the toast icon. |
| `bridge.json` (0600) | The one-time upgrade: `stage` (`new`, `converting`, `converted`, written last), the previous install folder, the engines it chose, the items already copied, the last run's items and notes. No secret. |
| `legacy-upstream.json` (0600) | The old config repository's company upstream links (`manifest/`, `bases/`), kept as data until the new stack has upstream links. |

The directory is 0700. Protection: on first start the agent keeps its protection key in the user's
keyring through libsecret (`secret-tool`) when a session bus offers one; otherwise in
`protection.key`, the weaker choice, which `GET /api/state` reports as `protectionWeak: true`. The
choice is recorded and kept; a later start without the keyring fails rather than creating a new
identity.

## The local API

`http://127.0.0.1:<port>`, port and token in `endpoint.json`. Every `/api` route but `GET /api/health`
takes `Authorization: Bearer <token>`. A request whose `Origin` is not the agent's own, or whose
`Host` is not its loopback address, is refused; no CORS header is ever sent, so a page on another
origin cannot send the header at all. Pages are not frameable. The UI bundle is served from `/`
without a token; `construct-agent open` hands the token to it in the URL fragment
(`/#agent-token=...`), which the browser never sends to a server.

The contract is [`openapi/agent.v1.json`](openapi/agent.v1.json). Regenerate it after changing a route:

```sh
CONSTRUCT_AGENT_UPDATE_OPENAPI=1 dotnet test agent/Construct.Agent.slnx --filter OpenApiDocumentTests
```

| Route | What it does |
|---|---|
| `GET /api/health` | `{status, version, pid, startedAt}`, without the token |
| `GET /api/state` | This device, the engines, the pairing in progress. No key, no token. |
| `GET /api/events?since=` | SSE, see below |
| `POST /api/shutdown` | Stops the agent (repair uses it) |
| `POST /api/engines/probe {address}` | Connects without any credential and returns `{url, pin, status}` for the user to confirm |
| `GET /api/engines/local` | The same for the solo engine on `https://127.0.0.1:7462`; 204 when none answers |
| `POST /api/engines {address, pin, name?, userToken?, windowsSignIn?}` | Adds the engine with the confirmed pin. `userToken` is the user's sign-in there, needed until this device is enrolled; with `windowsSignIn` the signed-in Windows user signs in instead (Negotiate, Windows only, `501 not-available` elsewhere; `409 no-windows-sign-in` when the engine's challenge, read without a credential over the pinned connection, does not offer it). Either sign-in is dropped once the device holds a device token there. When the device is registered, the engine is also written as an `engine` record, granted `D` and enrolled. |
| `DELETE /api/engines/{id}` | Removes it (not the engine that holds the store) |
| `POST /api/engines/{id}/connect {vaultMode?}` | Grants `D`, and `K` when `vaultMode` is `available`; enrols this device |
| `/api/engines/{id}/api/v1/...` | The proxy, below |
| `POST /api/device/setup {engine, deviceName, vaultMode?}` | The account's first device against that engine's built-in store, signed in with the user token: registers the device, creates the account keys, stores their wraps, writes the device and engine records, sets the recovery code, grants and enrols. Answers `{recoveryCode}`, the only time the code leaves the agent. A setup cut short (agent stopped, answer lost) is resumed by calling it again; `U` is sealed and saved before the store holds anything under it. |
| `POST /api/device/recovery-code` | A new recovery code; the old one stops working |
| `POST /api/device/recover {engine, account, recoveryCode, deviceName}` | A fresh device takes over the account with the code, then enrols at every engine of the account |
| `POST /api/pairing {engine, deviceName}` | This device asks to join: `{pairingId, words, expiresAt, storeUrl, storePin}`; the agent waits for the approval itself and reports `pairing` events |
| `GET`, `DELETE /api/pairing` | The pairing in progress; cancel it |
| `GET /api/pairing/requests/{id}` | On the approving device: `{name, words, publicKey}`, the words computed here |
| `POST /api/pairing/requests/{id}/approve {publicKey}` | Approves for the key whose words the user compared |
| `GET /api/devices` | The account's devices |
| `DELETE /api/devices/{id}` | Revokes a device at the store, in its record and at every engine reached; answers which engines confirmed |
| `GET /api/devices/{id}/secrets` | Every secret that device could read (all of the account's), to rotate at their source after a revocation |
| `GET /api/vault/secrets` | Names, descriptions, the auto-approve flag sealed with `K`, leases on every engine reached and the provenance of the last write; never a value |
| `POST /api/vault/secrets {name, description, value, username?, autoApprove?}` | A new secret under a free name (`409 exists` otherwise) |
| `PATCH /api/vault/secrets/{name} {description?, username?, value?}` | Changes what is given; a new value ends the leases on the old one at every engine |
| `DELETE /api/vault/secrets/{name}` | Deletes it |
| `POST /api/vault/secrets/{name}/reveal` | `{name, username, value}`: the only route that returns a value |
| `PUT /api/vault/secrets/{name}/auto-approve {on}` | Switches auto-approve |
| `GET /api/vault/history` | Every write of every secret, newest first, with its provenance (VM, approving or entering device, request id) |
| `GET /api/vault/approvals`, `POST /api/vault/approvals/{engine}/{id} {decision: allow\|deny, ttlSeconds?}` | The engines' pending approvals with the `uses` and `ttlSeconds` the VM asked for; an allow of a lease request may carry the duration the user chose (60 to 86400 s), which the lease gets. On a `locked` engine an allow first unlocks the VM that asked |
| `DELETE /api/vault/leases/{engine}/{id}` | Ends a lease early (the lease views carry `id`); the engine scrubs the value as on expiry |
| `GET /api/vault/entry-requests`, `POST /api/vault/entry-requests/{engine}/{id} {value, username?}`, `POST …/decline` | `construct secret ask`: the user enters the value, sealed here with `K`; created only while the name is free, never auto-approved, no lease |
| `POST /api/engines/{engine}/vms/{vm}/backups/{backup}/restore {requestId?}` | Restores a stored backup into the running VM: reads its keys file from the engine, unwraps the backup's key with the account's `B` here, or else with an old per-PC backup key the account holds (`setting:legacy-backup-key:<fingerprint>`, looked up by the recipients the keys file names, opened under `U` here), and starts the engine's `restore` job with it in the job's secrets. `409 backup-key-not-held` when none of them opens it. Answers `{jobId, replayed}`; follow the job through the proxy |
| `GET/PUT /api/vault/engines/{engine}/mode {mode}` | An engine's vault mode and whether it holds `K`; `available` grants `K`, `locked` withdraws it |
| `POST /api/vault/engines/{engine}/vms/{vm}/unlock` | Hands a running VM on a `locked` engine `K` until it stops |

Errors are `application/problem+json` `{title, status, code}`. A refusal by the store or an engine
behind an agent route is reported as 403, never 401, so 401 always means the agent's own token.

### The one-time upgrade

An install of the old client (Companion, scripts, VS Code panel) moves over in the agent's first window
(plan 11.6, 13.1, 13.2). The old files are only read; a failed step is retried; the way back is a reinstall
of the old release by hand.

| Route | What it does |
|---|---|
| `GET /api/upgrade` | `{legacy, stage, steps, found, storeEngine, localEngine, items, notes, oldWriters, running, error}`: the steps `engine`, `recovery`, `close`, `convert` with their state, what the old install holds (names and counts, no value), the last run's items |
| `POST /api/upgrade/engine` | Step 1 (202): where the PC has a local VM, the host package from the device's channel, checked against the manifest, installed by `Install-ConstructHost.ps1 -Solo` with one UAC prompt; its token file is taken into the state and deleted. The old shared hosts are added with their pins and stored sign-ins. Chooses the store engine. |
| `POST /api/device/setup` | Step 2, the existing first-device setup against `storeEngine`: with an old vault on the PC its key becomes the account's `K` |
| `GET /api/upgrade/writers` | Step 3: `{open}`, the old writers still running (VS Code, the old Companion, a lifecycle script). The Windows agent holds the Companion's single-instance mutex. |
| `POST /api/upgrade/convert {from?}` | Step 4 (202): the converter, progress as `upgrade` events. Refused while writers are open or before step 2. |

The converter copies, per item, and lists each item it copied in `bridge.json` so a second run leaves it
alone: the device channel (`constructRef`: `main` is stable, a branch nightly); the old shared hosts as
engines with their pins and sign-ins, connected with the vault mode the Companion recorded; the config
repository's `main` profiles through the store engine's `PUT /profiles/{name}`; a VM branch's unmerged
change as that VM's write with the merge base as its base, so the engine merges per field and opens a
conflict (branch ours, main theirs); the upstream links into `legacy-upstream.json`; the vault's secrets
(only when the account's `K` is the old key); each old backup private key wrapped under `U` into a
`setting` record `setting:legacy-backup-key:<fingerprint>`; the account's `B.pub` onto every VM, through the engine that runs it
(`POST /vms/{name}/backup-recipients`), over SSH with the old client's key only for a VM no engine reaches yet (a local VM
before its adoption); last, each local VM adopted by the engine (`POST /vms/{name}/adopt`, which enrols `B.pub` as well).
Then `stage: converted`, and only then the old backup keys come off the VMs, through the engine (`DELETE
/vms/{name}/backup-recipients/{fingerprint}`, refused while `B.pub` is not on the VM) or, for a VM no engine runs, over SSH. Open vault leases and pending cleanups are not carried over: a VM asks again.

### Device routes

| Route | What it does |
|---|---|
| `GET /api/device/features` | `{platform, notifications, microphone, share, startWithOs, shell}`: what this device can do |
| `GET`, `PUT /api/device/settings {channel?, autoUpdate?, startWithOs?}` | The release channel (`stable`, `nightly`, an https manifest URL), automatic updates (off by default), start with the OS (`null` where not available) |
| `GET /api/device/ssh-key` | The device's SSH public key (`~/.ssh/construct_ed25519`, created on first use). The agent registers it at every engine it enrols at (and again on connect); the engine authorizes it on the user's VMs. With it the agent registers the account's `B.pub`, which the engine enrols on the user's VMs |
| `GET /api/connections`, `POST /api/connections/refresh` | The user's own primary VMs on every engine: state, the address this device dials (the engine's `direct` address for a VM on this PC's engine, where it names one, else its `forwarded` endpoint), local forwards, the microphone |
| `DELETE /api/connections/{engine}/{vm}/forwards/{id}` | Closes a local forward (also at the engine) |
| `PUT /api/connections/{engine}/{vm}/mic {on}` | Microphone passthrough; kept for the VM's next start |
| `PUT`, `DELETE /api/connections/{engine}/{vm}/ssh-config` | The VM's `Host` alias in `~/.ssh/config` and VS Code's `remote.SSH.remotePlatform` entry |
| `POST /api/connections/{engine}/{vm}/open {what: t3\|vscode\|ssh}` | T3 Code's pairing link in the browser, VS Code Remote-SSH, an SSH terminal |
| `GET`, `POST`, `DELETE /api/connections/{engine}/{vm}/share` | The workspace share of a VM on this PC on a drive letter (Windows) |
| `GET /api/update`, `POST /api/update/check`, `POST /api/update/install` | Version, channel, the version on offer; install it when idle |
| `GET`, `POST /api/update/everything` | "Update everything": the parts and their state |
| `POST /api/shell/open {surface, route?}` | Shows the tray app's `pop`, `win` or `alone` surface (a second start hands over this way) |

A feature the platform lacks answers `501 not-available`. Linux has notifications (`notify-send`),
links (`xdg-open`), a terminal (`x-terminal-emulator`) and start with the session (a systemd user
unit); no microphone and no share.

**VM links.** Every 30 seconds the agent reads `whoami` and `vms` from each engine. Each running
primary VM the user owns gets a link: the SSH alias is written when it differs, the Companion's
forwarder follows the engine's forward list for the VM (tunnels bound to `127.0.0.1` only, acked
with their port), its notifier turns `construct notify` lines into notifications, and the
microphone is armed when it was on. A VM that stops loses its link; an engine that does not answer
keeps its links until it answers. Another user's VM is never linked or asked about.

**SSH config.** The block whose `Host` line names exactly the alias is replaced and every other
line is kept as written (a `Match` line, and `Host=name`, also end a block; a key path with a space is quoted); the file keeps its line endings and
mode, a symbolic link is written through. The block names the key an existing block named (an
install moved over keeps its key), else the device key. VS Code's settings are left alone when they
are not plain JSON (comments).

**Updates.** The agent reads its channel's manifest a minute after start and every six hours, and
offers its package (`agentWindows`, `agentLinux`) when the version differs. Installing downloads it,
compares its SHA-256 with the manifest's, unpacks it into `versions/<version>/` and, when no pairing
and no "update everything" runs, writes `install.json` (new version current and pending, the running
one previous), starts the new version and stops. The new version passes its self-test when its API
answers its own health route; it then clears `pending` and removes older versions. When it fails,
it writes the previous version back as current and starts it. Under the systemd unit the unit is
pointed at the new version and restarted; on Windows the Run value follows the new executable. The
install scripts mark a version installed over another one pending as well, so it self-tests too. An
agent not running from `versions/` (a development build) does not update itself.

**Update everything.** For each engine: when the user is its admin and it offers an update, `POST
/host/updates/run`, then its job is followed through the engine's restart; then `POST
/me/vms/reprovision`, which the engine scopes to the caller's own VMs. A host update that fails
leaves that host's VMs alone. Last, this device installs its own update. The parts live in memory
only; nothing is stored.

### The engine proxy

`/api/engines/{id}/api/v1/<path>` forwards to that engine's `/api/v1/<path>`, so the UI uses the
engine's own generated client with the base address `/api/engines/{id}`. The agent opens the
connection itself, checks the certificate against the pin, follows no redirect, and adds this
device's token with a fresh proof (or the user's sign-in at an engine where the device is not
enrolled). From the UI's request only the method, path, query, body, `Content-Type` and a few
content headers pass; its `Authorization` and anything else do not. A `Location` on the engine's
API is rewritten to the proxy path; a fixed `Content-Length` is kept; `Set-Cookie` and `WWW-Authenticate` are dropped.

### Events

`GET /api/events` is one server-sent event stream: `id` is a sequence number that keeps increasing across restarts of the agent, `event` the type,
`data` `{seq, type, engine, data}`.

| Type | When |
|---|---|
| `agent` | The agent's state changed (engines, device); reload `GET /api/state` |
| `pairing` | A pairing on this device changed state |
| `vm.created`, `vm.changed`, `vm.power`, `vm.deleted`, `job.started`, `job.progress`, `job.ended`, `backup.added`, `backup.removed`, `update.offered`, `update.finished`, `vault.approval.added`, `vault.approval.ended`, `vault.entry-request.added`, `vault.entry-request.ended` | An engine's change, from its own stream (`GET /api/v1/events`): `data` is `{id, vm?}`, never the state; re-read the thing through the proxy (a vault event: `GET /api/vault/approvals` or `/api/vault/entry-requests`). Jobs started elsewhere arrive too. |
| `engine.reset` | The agent (re)started following that engine and may have missed its changes; reload what you show from it |
| `job.lost` | After a reset, a job the agent saw start can no longer be read |
| `reset` | The events after `since` are gone (more than 1000 behind, or an earlier run); reload the state |

The agent follows each engine's change stream and resumes after a drop from the last event it passed
on. When the engine has lost that point (it restarted, or the agent fell too far behind) the agent
reads every job it saw start and not end once. A job's end (`job.ended`, or `job.lost`) is published
exactly once. A job's output lines are on the engine's per-job stream (`/api/engines/{id}/api/v1/jobs/{job}/events`).

## Repair

`construct-agent repair` reads only local files and the network: it reports the directory, the
device (registered or not, which protection), the token file, whether the agent answers, and each
engine reachable over its pin, then stops the running agent (or the process the token file names)
and starts a new one detached from the terminal. It never needs an engine.
