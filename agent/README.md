# `construct-agent`: the device agent

Every desktop device runs one agent as a user process (plan `docs/plans/construct-platform-design.md`
5.1, 6.2, 8, 11.1). It holds the device's key pair and the account's root key `U`, knows the engines
its user works with, and serves the UI. It owns no VM logic: every lifecycle action is a call to an
engine, which the UI makes through the agent's proxy.

This is the platform-independent core and the Linux console host. The Windows shell (tray, WebView2
window, installer), forwards, notifications, the microphone, SSH client config and self-update come in
later blocks.

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
| `src/construct-agent/` | The console host |
| `openapi/agent.v1.json` | The local API's contract, generated; a test fails on drift |
| `tests/Construct.Agent.Tests/` | Agents in process against the real engine (fake mode) on a loopback HTTPS port |

It references the service's `Construct.Keys` (keys, pairing, recovery, proofs), `Constructd.Store`
(the store's wire contract), `Constructd.Sync` (sealing records with `D`) and `Constructd.Client`
(the engine's generated client). It does not reference the Companion.

## Commands

```sh
dotnet build agent/Construct.Agent.slnx
dotnet test agent/Construct.Agent.slnx
dotnet run --project agent/src/construct-agent -- run       # foreground; the default
dotnet run --project agent/src/construct-agent -- open      # starts it if needed, opens the UI
dotnet run --project agent/src/construct-agent -- repair    # report and restart, no engine needed
```

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
| `POST /api/engines {address, pin, name?, userToken?}` | Adds the engine with the confirmed pin. `userToken` is the user's sign-in there, needed until this device is enrolled. When the device is registered, the engine is also written as an `engine` record, granted `D` and enrolled. |
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
| `GET /api/vault/approvals`, `POST /api/vault/approvals/{engine}/{id} {decision: allow\|deny}` | The engines' pending approvals; on a `locked` engine an allow first unlocks the VM that asked |
| `GET /api/vault/entry-requests`, `POST /api/vault/entry-requests/{engine}/{id} {value, username?}`, `POST …/decline` | `construct secret ask`: the user enters the value, sealed here with `K`; created only while the name is free, never auto-approved, no lease |
| `GET/PUT /api/vault/engines/{engine}/mode {mode}` | An engine's vault mode and whether it holds `K`; `available` grants `K`, `locked` withdraws it |
| `POST /api/vault/engines/{engine}/vms/{vm}/unlock` | Hands a running VM on a `locked` engine `K` until it stops |

Errors are `application/problem+json` `{title, status, code}`. A refusal by the store or an engine
behind an agent route is reported as 403, never 401, so 401 always means the agent's own token.

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
| `job.progress`, `job.phase`, `job.state` | A job an engine accepted through the proxy (`202` with a job `Location`, or with `{jobId}` in the body): its lines, its phase, its end, exactly once |
| `job.lost` | The engine no longer shows that job |
| `reset` | The events after `since` are gone (more than 1000 behind, or an earlier run); reload the state |

The agent follows a job's own stream on the engine and reattaches after a drop, skipping the lines
it has passed on; the end is published once. Engines have no change stream of their own yet, so
other engine changes are not on this stream.

## Repair

`construct-agent repair` reads only local files and the network: it reports the directory, the
device (registered or not, which protection), the token file, whether the agent answers, and each
engine reachable over its pin, then stops the running agent (or the process the token file names)
and starts a new one detached from the terminal. It never needs an engine.
