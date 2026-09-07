# `constructd` — the Construct host service

The .NET service that runs on a remote Hyper-V host so several users can create and manage their own
VMs on it from the VS Code extension and the PowerShell scripts
(`docs/plans/modular-remote-architecture.md` §4.4, §4.6, §4.7).

**Status: complete (batches B6 + B6b).** The API surface, the domain model, the idle-policy engine and
the durable SQLite stores landed with the contract-first scaffold (B6); the Windows platform — the
Hyper-V driver, the native ISO build, the `netsh` port forwards — plus the admin CLI and the host
installer landed with B6b, without changing an endpoint, a policy or a job.

Everything Windows-specific still sits behind the same interfaces, and every child process goes
through one `IProcessRunner`. That is what lets the whole thing be developed and tested on Linux: the
tests assert the exact `powershell.exe`, `wsl.exe` and `netsh.exe` **argument vectors** against a
recording runner, so the command lines this service issues are pinned by tests rather than by a field
visit.

## Layout

```
service/
  Constructd.sln
  src/Constructd.Core/      domain records, interfaces, pure logic; zero package references
    Domain/                 User, ApiToken, Vm, PortForward (+ ForwardAck), IdlePolicy,
                            ActivityReport, Job, AuditEntry, VmDescriptor, Endpoint
    Abstractions/           IHypervisorDriver, IIsoBuilder, IJobEngine, IJobStore,
                            IPortForwardManager, IForwardStore, IIdlePolicyEngine, IUserStore,
                            ITokenService, IVmRepository, IAuditLog, IClock,
                            IProcessRunner, ITcpTableReader, IHostPowerGuard
    Logic/                  PortAllocator, TokenHasher, VmNameValidator, Ownership,
                            IdleEvaluator, IdlePolicyRules, TcpConnectionCounter, HostPowerPlanner
    Services/               IdlePolicyEngine, InProcessJobEngine, HostPowerCoordinator +
                            HostPowerGuardBase/NullHostPowerGuard — platform-agnostic, so these are
                            the real implementations in every mode
    Configuration/          ConstructdOptions and friends
  src/Constructd.Sqlite/    durable stores: hand-written SQL over Microsoft.Data.Sqlite, no ORM
  src/Constructd.Windows/   the Windows platform; every call goes through IProcessRunner
    Process/                ProcessRunner — argv arrays, no shell, ever
    HyperV/                 HyperVDriver + the PowerShell it composes (drivers/Load-ConstructDriver.ps1)
    Iso/                    the build strategies (native, WSL, catalog), the ISO catalog,
                            the WSL path mapping, the source-ISO cache
    Forwards/               NetshPortForwardManager, the portproxy parser, the TCP-table P/Invoke
    Power/                  WindowsHostPowerGuard — the PowerCreateRequest/PowerSetRequest P/Invoke
    Internal/               ArgumentGuard + PowerShellLiteral — nothing reaches a child unvalidated
  src/Constructd.Api/       ASP.NET Core minimal API host
    Program.cs              composition root, TLS, Windows-service hook, `admin` CLI entry
    Admin/                  the admin CLI (`constructd admin …`) and its own tiny host
    Auth/                   schemes (Bearer, VmToken, Negotiate seam, test identity), policies
    Endpoints/              one file per area of the API
    Jobs/                   the VM create/remove workflows the job engine runs
    Hosting/                idle/power scheduler and periodic host forward reconciliation
    Infrastructure/         JSON contract, problem details, centralized auditing
    Contracts/              request/response DTOs — the wire contract
  src/Constructd.Fakes/     in-memory implementation of every interface
  host/                     Install-ConstructHost.ps1 / Uninstall-ConstructHost.ps1 (PS 5.1)
  tests/Constructd.Tests/   xunit: Core unit tests, SQLite persistence tests, API integration tests,
                            Windows platform tests (command lines, parsers, reconciliation)
  tests/host-installer.test.ps1   pwsh: installer parser, parameter contract, powercfg parsing
```

`Constructd.Windows` targets plain `net10.0`, **not** `net10.0-windows`: the API references it and the
whole suite has to keep building and running on Linux. What only works on Windows is marked
`[SupportedOSPlatform("windows")]` and registered only when `OperatingSystem.IsWindows()`.

`Constructd.Sqlite` is the one project beyond the four the batch brief sketched: persistence is
cross-platform (so it belongs in this batch, not in a Windows follow-up), Core must stay free of
package references, and hand-written SQL does not belong in the HTTP host.

## Build, test, run

```bash
dotnet build service/Constructd.sln            # 0 warnings, 0 errors
dotnet test  service/Constructd.sln            # 594 tests

# run the whole API against the fakes (no Hyper-V, no Windows):
dotnet run --project service/src/Constructd.Api -- --fake

# the admin CLI is the same executable:
dotnet run --project service/src/Constructd.Api -- admin users list
```

`--fake` is shorthand for `Constructd:Fake=true`: the hypervisor driver, the ISO builder and the
port-forward manager become in-memory fakes, persistence defaults to memory, and the `TestIdentity`
authentication scheme is enabled — it trusts an `X-Constructd-Test-Identity` header and stands in for
Negotiate. **Never run fake mode on a real host**; the service logs a warning at startup when it is
on. Fake mode can be combined with `Constructd:Persistence=Sqlite` (fake hypervisor, real database),
which is how the persistence tests drive the API.

Without fake mode the service needs Windows, and off Windows it refuses to start rather than coming up
half-wired. On Windows it also validates at startup that `Constructd:ScriptsDir` really is a Construct
checkout (it must contain `drivers\Load-ConstructDriver.ps1`, `lib\AgentVm.Common.ps1` and
`bin\build-autoinstall-iso.sh`) — a misconfiguration that would otherwise surface as a VM creation
failing twenty minutes in. The SQLite stores are real in both modes.

A quick local session:

```bash
Constructd__ListenUrl=http://127.0.0.1:7999 \
Constructd__BootstrapAdmin=dev-admin \
Constructd__BootstrapAdminToken=dev-token \
dotnet run --project service/src/Constructd.Api -- --fake

curl -H 'Authorization: Bearer dev-token' http://127.0.0.1:7999/api/v1/whoami
```

## API

Everything lives under `/api/v1`, speaks JSON with camelCase properties and camelCase enum strings
(`running`, `save`, `host`), and reports errors as RFC 7807 problem documents.

| Route | Who | What it does |
|---|---|---|
| `GET /whoami` | any authenticated user identity | Resolved identity, role, quota. Answers for identities that are *not* enrolled too (`known: false`), which is how enrollment tells "wrong credential" from "ask your admin to add you". VM tokens are refused. |
| `POST /users` | admin | Creates a user `{name, role, maxVms, allowHostForwards?, allowance?}`. With `allowance` supplied, omitted `maxVms` uses the host default; the legacy request still requires `maxVms`. There is no self-registration. |
| `DELETE /users/{name}` | admin | Removes a user and revokes their tokens. Refused while they still own VMs, and for the caller's own account. |
| `POST /users/{name}/tokens` | admin | Issues an API token `{label}`; the plaintext is in the response **once** and is never stored or logged. |
| `GET /audit` | admin | Audit trail, newest first, `?limit=`. |
| `GET /vms` | user or primary token | Owned VMs (token: its primary and children); all for admin. Optional `kind=primary|child|all`, `parent`, and admin-only `owner` filters. |
| `POST /vms` | user | `{name, cpu, ramGb, diskGb, opts:{nested?, automaticCheckpoints?, idlePolicy?}}` → `202 {jobId}`. Name uniqueness and the quota are enforced by the insert itself. |
| `GET /vms/{name}` | owner/admin | The VM including its `publicHost`, forwards and host-administration metadata. Never exposes the VM token hash. |
| `DELETE /vms/{name}` | owner/admin | → `202 {jobId}`; accepting it fences the VM (see below) and the job removes the VM, its forwards and its SSH port. |
| `POST /vms/{name}/power` | owner/admin | `{action: start\|stop\|save}`, synchronous, returns the new state. `save` needs the driver's suspend capability. |
| `GET /vms/{name}/state` | owner/admin | Live state from the driver (and refreshes the registry). |
| `GET /vms/{name}/endpoint` | owner/admin | `{sshHost, sshPort, publicHost}` — the service host plus the allocated forward, and the name this VM's **web** forwards are advertised under (`PublicHostPattern`, else `PublicHost`; SSH is always dialled on `sshHost`). Until that forward exists the call answers `409`: the VM sits on the host's own switch (`SwitchName`, `Default Switch` unless configured) and has no client-dialable address yet. |
| `GET /vms/{name}/forwards` | owner/admin **or that VM's own token** | Lists forwards, each with its client ack inline (see below). |
| `POST /vms/{name}/forwards` | owner/admin **or that VM's own token** | `{vmPort, label, target}` → `{id, publicPort?, url?}`. `target` defaults to `client`. |
| `DELETE /vms/{name}/forwards/{id}` | owner/admin **or that VM's own token** | Removes one forward. |
| `POST /vms/{name}/forwards/{id}/ack` | owner/admin — **not** the VM's own token | `{status: open\|error, localPort?, hostLabel?, message?}` → the updated forward. The extension reporting that it opened the port on the user's PC. |
| `GET`/`PUT /vms/{name}/idle-policy` | owner/admin | `{timeoutMinutes, action}`; the response also carries the admin cap and whether the request was clamped. |
| `POST /vms/{name}/activity` | owner/admin **or that VM's own token** | Guest heartbeat `{busy, reasons[]}`. |
| `GET /jobs/{id}` | job submitter/admin | Job state, progress lines, result, error. The first retrieval of a succeeded creation job also gets `result.vmToken`. |
| `GET /jobs/{id}/events` | job submitter/admin | `text/event-stream`. |

### Host administration foundation

| Route | Who | What it does |
|---|---|---|
| `GET /health` | anonymous | Status, schema versions and feature names; authenticated callers also receive installed commit/version. |
| `GET /host/status` | admin | Installed release, health, capacity snapshot, maintenance phase, active jobs and overdue leases. Incomplete capacity is reported honestly until its backend lands. |
| `GET /host/capabilities` | user or primary token | Backend capabilities and current host policy. Unavailable adapters report `unsupported`. |
| `GET` / `PUT /host/config` | admin | Read defaults/stored sections; atomically validate and replace supplied sections, optionally comparing each `expectedUpdatedAt`. |
| `GET /users`, `GET /users/{name}` | admin | Enabled state, allowances, effective policy, VM counts and token count; no hashes. |
| `PUT /users/{name}` | admin | Patch `role`, `enabled`, `maxVms`, `allowHostForwards`. Self-demotion/disable and removal of the last enabled admin are refused. |
| `GET` / `PUT /users/{name}/allowance` | admin | Read stored/effective allowance; replace nullable stored allowance fields. Null inherits host defaults. |
| `GET /users/{name}/tokens`, `DELETE /users/{name}/tokens/{id}` | admin | List credential metadata or revoke one token. Existing create/delete-user and issue-token routes remain. |
| `GET /vms/{name}/identity` | owner/admin or that VM's token | Kind, parent, owner, token kind, effective delegation, service version/features. Legacy tokens receive no delegation. |
| `POST /vms/{name}/guest-report` | owner/admin or that VM's token | `{event, reporter, at?, constructCommit?, outcome?}`. Independent success (`provisioned`, `reinstalled`) and `attempt` fields. Children are refused. |
| `POST` / `DELETE /vms/{name}/token` | owner/admin user | Rotate (one-time plaintext) or revoke the primary's credential. Children never receive tokens. |
| `GET` / `PUT` / `DELETE /vms/{name}/overrides` | admin | Per-primary restrictions, combined with current owner and host policy. |
| `GET /vms/{name}/children` | owner/admin or parent primary token | Classified children inventory. |
| `GET /vms/shared` | user or primary token | Accessible host-shared children; disabled owners' children are hidden from shared callers. |
| `GET /vms/{name}/capabilities` | owner/admin/parent or shared caller | Per-VM capability view, lowered by backend availability and current policy. |

`whoami` adds `enabled`, `effective` and `apiFeatures`. VM inventory adds `kind`, `parent`, sharing,
lease/resources, guest reports and separate host observations, reservations, operation metadata and
`allowedActions`. These describe current policy; authorization reads current user state on every
request. Disabling a user immediately rejects their Bearer tokens and their VMs' credentials. Legacy
VM tokens retain only the existing forwards/activity scope plus identity and guest-report intake.
New primary creation issues a `primary` token; it never grants user identity or admin access.

Only `host-admin` is advertised until the separately owned feature adapters are integrated.
The child driver and create/delete jobs are implemented (see the stage 2 section below),
with executable API coverage using in-memory admission. Production SQLite admission,
media/capacity adapters and later console, update and network operations still await
integration. The fake admission store commits or rolls back participating stores under
one lock, including readers; its scope accepts only synchronously completing store calls.
The persisted child runner owns operation and maintenance handles through completion.
These additions have Linux coverage; the current host probe attempt is blocked as
recorded below.

### Jobs, the event stream and the one-time secret

Nothing terminal about a job becomes visible before its terminal state is durable: while that write is
in flight `GET /jobs/{id}` still reports the running state, subscribers keep waiting, and the one-time
token cannot be consumed. A crash in that window therefore looks like an interrupted job — which the
startup recovery pass marks failed — and never like a completed job whose state was lost.

Long operations answer `202 {jobId}` plus a `Location` header. `GET /jobs/{id}/events` emits one
`event: progress` per line — replaying the lines already recorded, so a client that attaches late (or
reconnects) still sees the whole log — and then exactly one terminal `event: state` carrying the
finished job, after which the stream ends.

```
event: progress
data: {"at":"2026-09-01T20:44:16+00:00","text":"building autoinstall ISO for work-vm"}

event: state
data: {"id":"…","kind":"create-vm","state":"succeeded",
       "result":{"name":"work-vm","endpoint":{"sshHost":"…","sshPort":2201,"publicHost":"…"},"vmToken":"<once>"}}
```

The VM creation job is the hybrid split of plan §4.4: build the ISO → create the VM → wait for SSH →
detach the install media → allocate the SSH forward → issue the VM-scoped token. The client then runs
`Provision-AgentVM.ps1` against the returned endpoint, so user secrets never transit here.

**The VM-scoped token is a one-time secret.** The wire shape is
`result: {name, endpoint, vmToken}`, but only the first two are durable: the stored job result carries
no secret at all, and the token lives in the engine's memory until it is consumed. The first
authorized retrieval of a succeeded creation job — `GET /jobs/{id}` or the terminal SSE event — gets
it merged into `result.vmToken`; every later retrieval, every SSE reconnect and everything after a
restart sees `vmToken: null` while `name` and `endpoint` stay.

"Consumed" means exactly that: the secret is taken when the response is *composed*, not when the
client acknowledges it, so a response lost in transit loses the token. That is deliberate: nothing
about the delivery is retriable without weakening "once".

**An owner or admin can rotate a VM token.** `POST /vms/{name}/token {kind?: "primary"|"legacy"}`
returns `{vmToken, kind, issuedAt}` once and immediately invalidates the previous hash. The default
kind is `primary`; existing migrated tokens remain `legacy` until explicitly rotated. `DELETE` on
the same route revokes the credential. VM tokens themselves cannot rotate it. Reprovision with
`Provision-AgentVM.ps1 -RotateVmToken` to rotate and deliver the replacement through the existing
SSH stdin secret channel; ordinary reprovisioning does not rotate.

If any creation step fails, the job rolls back: the partially created VM is removed from the
hypervisor (an orphan VM would keep consuming disk while its name was handed back), the ports are
released and the registry record is deleted — which frees the name and the quota slot. The rollback
never masks the original failure.

Jobs are authorized by their submitter (or an admin), not by the VM they mention: a delete job
outlives the VM record, and a create job hands out a secret.

### Authentication and authorization

| Scheme | Credential | Notes |
|---|---|---|
| `Bearer` | `Authorization: Bearer <secret>` | Admin-issued user token. |
| `VmToken` | `Authorization: VmToken <secret>` | The scoped token injected into a VM at provision time. |
| `Negotiate` | Kerberos/NTLM | **Registered only when `OperatingSystem.IsWindows()`** (`Auth/AuthenticationSetup.cs`). |
| `TestIdentity` | `X-Constructd-Test-Identity: <name>` | Negotiate stand-in, **fake mode only**. |

The scheme is chosen per request from the `Authorization` header. Whatever the scheme, the resulting
identity is mapped onto a `User` record by `UserClaimsTransformation`, which is where the role comes
from — the store is authoritative on every request, so deleting a user or changing their role takes
effect immediately.

Policies (`Auth/AuthorizationSetup.cs`):

- `any-user-identity` — any authenticated user identity, enrolled or not (`/whoami` only).
- `user` — an enrolled user; VM tokens are rejected, because they carry no "known user" claim.
- `admin` — an enrolled user with the `Admin` role.
- `vm-scoped` — an enrolled user **or** a VM token; the route then does the resource check.
- `vm-owner-or-admin` / `vm-self-or-owner-or-admin` — resource-based checks against the `Vm`,
  implemented on top of the pure `Ownership` helpers.

A VM-scoped token is valid for exactly four calls: its own VM's forwards (list, add, remove) and its
own heartbeat. Every other route — including `/whoami` and including the **ack** on its own
forwards — answers `403` for it, and so does any other VM's copy of those four routes, even one
owned by the same user.

A VM that exists but belongs to somebody else answers `403`, not `404`; an unknown VM answers `404`.

**Deleting is fenced.** Accepting `DELETE /vms/{name}` immediately marks the VM `deleting` and revokes
its scoped token in the same write; if the removal job then cannot be queued, that write is rolled back
so the VM is operable again (fence cleared, token intact) rather than fenced forever with no job to
finish the job. From that moment the two callers see different things, and the difference is the point:

- **The guest is locked out at authentication.** Its token hash is gone, so every call it makes —
  including the forward and heartbeat routes it is otherwise entitled to — is `401`. It never reaches
  the fence.
- **An authenticated owner or admin gets `409` from the fenced routes**: `POST /vms/{name}/power`,
  `POST` and `DELETE` on `/vms/{name}/forwards`, `PUT /vms/{name}/idle-policy` and
  `POST /vms/{name}/activity`. Reads still work and report `deleting: true`.

`DELETE /vms/{name}` itself carries **no** fence check, so a second delete while the first removal job
is still running is accepted rather than refused: it re-writes the same fence and queues a **second**
removal job. Jobs are not serialized per VM, so the two run concurrently and their completions can
race — both can pass the driver's initial "does this VM exist" lookup before either reaches the
removal, leaving the loser to fail on a VM that is already gone. Nothing is corrupted by that (the
fence and the token revocation are the same write either way), but "the duplicate is harmless" is not
something the code guarantees, and a per-VM job gate would be the way to make it so.

The forward manager re-checks the same fact inside the per-VM gate it holds while tearing forwards
down, so nothing can be attached behind the job's back and survive the VM (an orphan forward would
otherwise be re-materialized at the next startup).

Host-target forwards are gated on the **VM owner's** `AllowHostForwards` flag, not the caller's, so
an admin acting on someone else's VM cannot route around that restriction.

### The client-forward ack relay (plan §4.6)

A `host` forward is something the service *does*; a `client` forward is something the service only
*records*, because the port opens on the user's PC: the extension spawns its own `ssh -N -L` tunnel
to the VM the window is driving.
The extension's forwarder module (`extension/src/forwarder.js`, `extension/ARCHITECTURE.md`
§Forwards) is the other end.

```
guest CLI ──POST /forwards {target:client}──►  service  ◄──GET /forwards── extension
   (VM token)                                     │                          │ opens localhost:<port>
          ◄────GET /forwards, polling─────────────┘  ◄──POST …/{id}/ack──────┘
          prints http://localhost:<port>/
```

**A VM may not ack its own forward.** The ack route is the one forward route that answers `403` to a
VM-scoped token: the guest *asks* whether a port opened on the user's PC, and a VM that could also
*answer* would be able to hand its own agents a link to a port nothing is listening on. Owner or
admin only, and only for a forward that really belongs to that VM (`404` otherwise) and really has
target `client` (`409` for a host forward, which the service materializes itself).

The ack is stored on the forward and **replaces** any earlier one — the extension re-acks after
re-establishing a tunnel — and it is durable, so a service restart does not tell a guest that a link
it already printed is "not open yet" while the tunnel is still up.

The list projects it **inline and flat**:

```json
{"id":"…","vmName":"work-vm","vmPort":5173,"publicPort":null,"target":"client","label":"vite dev",
 "created":"…","url":"http://alice-pc:18800/","status":"open","localPort":18800,
 "hostLabel":"alice-pc","message":"","ackedAt":"…"}
```

- `url` is `http://<hostLabel>:<localPort>/` when the extension reported a host label, and
  `http://localhost:<localPort>/` when it did not (the default — an untouched install opens a
  loopback-only tunnel). An **unacked** client forward has `url: null`, and so does an `error` ack:
  the CLI reads `url` first, so filling it in for a failure would make it print a dead link instead
  of the reason.
- `status` is absent until somebody acks, then `open` or `error`; `message` carries the reason for
  an `error`.
- **Flat is load-bearing.** `bin/construct-expose.sh` falls back to a purely textual parser on a VM
  without `jq` — it splits the array on `{…}` and greps flat `"key": value` pairs — so a nested
  `ack` object would be invisible there. `ExposeCliContractTests` re-implements that parser and runs
  it against the real serialized bytes of the real routes.
- `hostLabel` and `message` are trimmed, stripped of control characters and capped: both are echoed
  to a CLI that prints them, so a newline in either would let one field forge another line of
  `construct expose --list`'s output.
- `hostLabel` is then held to **one** wire form (`ForwardHost`, specified in
  [`docs/expose.md`](../docs/expose.md#the-host-label-rule)): a host name, or a **bare** IP literal
  — a bracketed IPv6 value is unwrapped, a zone id and anything that is not an address are dropped.
  `url` adds the brackets back exactly once, so the link this service builds and the one the guest
  CLI builds from the same fields are the same string. Interpolating the label as it stood printed
  `http://fe80::1:5173/` here while the CLI printed `http://[[fe80::1]]:5173/`.

### Auditing

Every mutating call (`POST`/`PUT`/`DELETE`) writes exactly one audit entry, whatever the outcome. That
is structural rather than a habit: `AuditMiddleware` sits between authentication and authorization and
therefore wraps everything that can decide a request's outcome —

- an authorization refusal (401/403) that never reaches the handler,
- a model-binding failure such as malformed JSON, which happens before the handler runs,
- the handler's own answer, including validation errors and conflicts,
- an exception thrown by a handler dependency (a driver, a store), which would otherwise leave no
  trace at all.

Handlers only contribute detail (`http.SetAuditDetail(...)`); they never write entries, so nothing can
double-record or forget, and a test asserts that every mutating route carries `AuditActionMetadata`.

**No failure text is ever repeated verbatim — not even to the log.** A dependency's exception can carry
a whole command line, and with it a VM's seed password, in its message, its stack trace, its `Data` or
an inner exception. So everything the service records — job errors, job progress lines, audit details,
problem responses and log entries — carries only `SafeError.Describe(...)`: the message of exceptions
the service composed itself (`VmNotReachableException`, `PortRangeExhaustedException`), and otherwise
just the exception type.

The exception object is never handed to a logger, because a rendered log entry includes all of those
fields. That is why `RequestOutcomeMiddleware` is the **outermost** middleware and the service registers
no framework exception handler above it: everything in the pipeline fails into the sanitizer, including
routing and the authentication handlers — the one place where a presented plaintext token is in scope.
The job engine takes a diagnostics callback that receives the safe description, and the idle scheduler
logs the description the engine hands back.

This is the service's own boundary, not a request on the implementations behind the interfaces. Tests
drive failures whose messages contain a sentinel secret through the request path, a background job, the
idle engine, the forward manager and token validation (with the presented token as the sentinel), and
assert the sentinel appears in no response, no audit entry, no job state, no SSE stream, no database
file, and in no captured log entry — the test host captures logs through an `ILoggerProvider` that
renders message, state, exception and `Data`.

Job bodies and the idle engine audit separately (`vm.create.completed`, `vm.idle-save`, …) with the
actor `system` where the service acted on its own. Secrets never appear in an audit detail.

Heartbeats are mutations and are therefore audited too. They are frequent (one per VM per report
interval), so `GET /audit` is paged and audit retention is an admin concern.

### Idle policy (plan §4.7)

A VM is idle only when **both** signals say so, continuously for the whole timeout window:

1. no live connections through any of its forwards, and
2. no in-guest activity — a `busy` heartbeat keeps the VM alive **even with zero connections**,
   which is the entire point of unattended agents.

Silence buys the guest a grace window of `ReportIntervalMinutes × MissingReportGraceMultiple`, and
only when that window has expired does the timeout start running: a `busy` report that goes stale
counts as idle from the end of its grace window (never retroactively from the report), and a VM that
has never reported at all counts as idle only a grace window after it was last seen active. A timeout
shorter than the grace window therefore still waits for the grace. An explicit `busy: false` report
needs no grace — the guest itself says it is idle.

The decision logic is the pure `IdleEvaluator`; `IdlePolicyEngine` gathers the signals, applies the
decision through the driver and audit-logs it; `IdleSchedulerService` ticks it once a minute (and is
switched off in tests, which drive `TickAsync` — or the engine — directly). `Idle:SchedulerEnabled`
switches **only** the idle evaluation; the same loop also carries the host power reconcile below,
which has its own setting.

Users set their own VM's policy; the admin sets the service-wide default and an optional cap. The cap
is applied when a policy is stored **and** when it is evaluated, so lowering it also affects VMs
configured earlier.

### Keeping the host awake (plan §4.13)

A host that sleeps takes every VM on it down with it. The field host did exactly that overnight —
S3, reason "System Idle", with VMs that were expected to keep serving. So the service holds a
**Windows power availability request** (`PowerCreateRequest` / `PowerSetRequest`,
`PowerRequestSystemRequired`) for as long as at least one VM in its registry is `Running`, and clears
it when none is.

- **The rule is pure.** `HostPowerPlanner` maps the VM states to "required, because *n* VM(s)
  running" or "not required". `HostPowerCoordinator` applies that through `IHostPowerGuard`.
- **No second poller.** The reconcile hangs off the idle scheduler's tick, which already refreshes
  every VM's state from the hypervisor and writes it to the registry — so "is anything running" is
  read once, from the registry, right after it has been refreshed. A VM started or stopped through
  the API therefore changes the request within one tick (`Idle:TickSeconds`, 60 s by default), and a
  restart of the service reconciles **before** its first tick.
- **One loop, two independent switches.** `Idle:SchedulerEnabled` and `Power:KeepHostAwake` are
  read separately: the composition root registers the loop when **either** is on, and a tick runs
  only the halves that are wanted. Turning idle-saving off therefore does not quietly stop the host
  staying awake, and vice versa.
- **One request, held for the service's lifetime.** `HostPowerGuardBase` makes the guard idempotent
  and thread-safe: only an actual transition reaches the platform, a failed acquire is retried on the
  next tick rather than remembered as done, and whatever is still held is released when the container
  disposes the guard at shutdown. Acquiring and releasing are logged at Information with the reason.
- **Only `SystemRequired`.** The display may still sleep, and away mode (`PowerRequestAwayModeRequired`)
  is for media playback, not for a machine nobody is sitting at.
- Off Windows and in fake mode the guard is `NullHostPowerGuard` and nothing at all happens, which is
  why none of this needs a platform branch above the interface.

To see it on the host:

```powershell
powercfg /requests     # SYSTEM: [PROCESS] …\Constructd.Api.exe  Construct agent VMs are running
```

The request stops the **idle** timer; it does not stop somebody closing a laptop lid or an explicit
"Sleep". The installer therefore also reports the host's own sleep timeouts and offers to switch the
AC ones off — see *Installing on a host*, `-KeepHostAwake`. Set `Constructd:Power:KeepHostAwake` to
`false` on a host whose power plan is managed elsewhere; the service then never takes a request.

## Configuration

Bound from the `Constructd` section of `appsettings.json`, from environment variables
(`Constructd__PublicHost=…`, `Constructd__Idle__MaxTimeoutMinutes=…`), or from the command line.

| Key | Default | Meaning |
|---|---|---|
| `Fake` | `false` | Use the in-memory hypervisor/ISO/forward fakes (`--fake`). Development only. |
| `Persistence` | `Sqlite` (`Memory` in fake mode) | Where users, tokens, VMs, jobs and the audit trail live. |
| `DatabasePath` | `constructd.db` | SQLite file; on a real host under `C:\ProgramData\Construct\service\`. |
| `ListenUrl` | `https://0.0.0.0:7462` | What the service listens on. |
| `CertPath` / `CertPassword` | – | PFX for TLS. |
| `CertThumbprint` | – | Certificate from `LocalMachine\My` (what the client pins at enrollment). |
| `ScriptsDir` | – | The Construct checkout the service invokes (`drivers\`, `lib\`, `bin\`). Validated at startup. |
| `WslDistro` | `Ubuntu` | WSL distro used for the ISO build. Empty uses WSL's default distro (no `-d`). |
| `PublicHost` | `localhost` | LAN name/IP that endpoints and forwards are advertised on, and what the API certificate is bound to. |
| `PublicHostPattern` | – (empty) | Per-VM host name template, e.g. `{name}.vpn.example`. With a wildcard DNS record pointing at this host, every VM gets its OWN name, so two VMs' web UIs are separate origins (browsers scope cookies by host, not by port). `{name}` must appear exactly once and the pattern must render to a valid DNS name for every VM name — checked at startup. Empty = every VM is advertised on `PublicHost`. The certificate is unaffected. |
| `SwitchName` | `Default Switch` | Hyper-V virtual switch new VMs are attached to. |
| `VmStorageRoot` | – (empty) | Folder the per-VM VHDX is created in. Empty leaves the path to the driver, i.e. Hyper-V's own default folder — the same location a local install uses. |
| `ListenAddress` | `0.0.0.0` | `listenaddress=` of the host's portproxy rules. Narrow it to one LAN address on a multi-homed host. |
| `PowerShellPath` | `powershell.exe` | Windows PowerShell used for the Hyper-V driver. **Windows PowerShell 5.1, not `pwsh`** — that is what the repo's drivers target. |
| `WslPath` | `wsl.exe` | WSL launcher used for the ISO build. |
| `NetshPath` | `netsh.exe` | netsh used for the port-proxy rules. |
| `SshForwardPorts:Start` / `:End` | `2201` / `2299` | Per-VM SSH forward range. |
| `AppForwardPorts:Start` / `:End` | `2300` / `2999` | Range for `construct expose --to host`. Must not overlap the SSH range. |
| `MaxForwardsPerVm` | `16` | Cap on extra forwards per VM, enforced inside the forward manager. |
| `VmReachableTimeoutMinutes` | `30` | How long creation waits for SSH. |
| `AuditQueryLimit` | `200` | Default page size of `GET /audit`. |
| `Idle:SchedulerEnabled` | `true` | Run the background evaluator. |
| `ForwardReconcileSeconds` | `30` | Retry host forward reconciliation after boot and VM IP changes; independent of idle/power settings. Minimum 5 s; `0` disables periodic passes. |
| `Idle:TickSeconds` | `60` | Evaluation interval. |
| `Idle:DefaultTimeoutMinutes` | `120` | Policy a new VM gets. |
| `Idle:DefaultAction` | `Save` | `Save`, `Shutdown` or `Off`. |
| `Idle:MaxTimeoutMinutes` | `0` (no cap) | Admin cap on user-chosen timeouts. |
| `Idle:ForceEnabled` | `false` | With a cap set, also forbid switching idling off. |
| `Idle:ReportIntervalMinutes` | `5` | Interval the guest reporter posts at. |
| `Idle:MissingReportGraceMultiple` | `3` | Intervals of silence before the guest counts as idle. |
| `Power:KeepHostAwake` | `true` | Hold a Windows power availability request while at least one service-managed VM is `Running`, so the host does not sleep under it (*Keeping the host awake*). `false` never takes one. Independent of `Idle:SchedulerEnabled`, though both ride the same loop. No effect off Windows or in fake mode. |
| `Iso:Mode` | `Prebuilt` | Which ISO build strategy is in effect: `Prebuilt` (compatibility alias for on-demand native catalog media) or `PerVm` (the service builds one ISO per VM through WSL). `Native` uses the .NET tool and catalog; `InGuest` and `HypervisorHost` remain planned and refused at startup. See *ISO build strategies*. |
| `Iso:NativeBuilderPath` | `<ScriptsDir>\.construct-tools\iso\Construct.Iso.exe` | Self-contained native builder resolved by the installer; empty uses this path. |
| `Iso:HostnameSource` | `hyperv-kvp` | Where a guest built from generic media takes its hostname at first boot (`VM_HOSTNAME_SOURCE`). `cloud-init-metadata` is planned for Proxmox / NoCloud / ConfigDrive. |
| `Iso:SeedUser` | `construct` | Seed user of the unattended install. |
| `Iso:BootstrapPublicKeyPath` | `<ScriptsDir>\keys\bootstrap_ed25519.pub` | Bootstrap key injected into the ISO. |
| `Iso:SourcePath` | – | The Ubuntu ISO to remaster. Set this **or** `Iso:SourceUrl`; a path wins and is never downloaded or deleted. |
| `Iso:SourceUrl` | – | Where to fetch the source ISO when no path is set. Admin-configured on purpose: the service does not go looking for "the current LTS", so a host's guests cannot change release because a mirror did. |
| `Iso:Sha256` | – | Expected SHA-256 of the source ISO. Empty skips the check; when set it is verified on **every** use, not only after the download. |
| `Iso:CacheDir` | `C:\ProgramData\Construct\service\iso` | Holds the downloaded source ISO and the ISO catalog (versioned media, sidecars, `current.pointer`). |
| `Iso:SourceId` | `ubuntu-server-minimal` | `SOURCE_ID` of `bin/build-autoinstall-iso.sh` (`ubuntu-server` for the standard set). |
| `HostAdmin:Capacity:Mode` | `Observe` | Bootstrap capacity policy when no stored section exists. Used when no stored capacity section exists; migrated hosts remain in observe mode. |
| `HostAdmin:Updates:ManifestPublicKey` | – | Bootstrap signature-verification public key; updater execution is not installed in stage 1. |
| `HostAdmin:Media:RootDir` | – | Media root used by health discovery; media acquisition follows separately. |
| `BootstrapAdmin` | – | Identity seeded as the first admin when the user store is empty. |
| `BootstrapAdminMaxVms` | `10` | Quota for that admin. |
| `BootstrapAdminToken` | – | Optional plaintext token for the bootstrap admin (hashed at startup). Only for hosts that cannot use Negotiate; remove it once a real token has been issued. |

Host policy is stored separately in `host_config` and takes precedence over bootstrap defaults.
`GET /host/config` returns each section's fields plus `source` and `updatedAt`. `PUT` accepts one or
more complete sections (all required fields present); optional fields omitted become null. Invalid
sections roll back the entire request. `expectedUpdatedAt: null` requires that no stored section
exists; a timestamp requires an exact match. A conflict returns `409 config-conflict`.

| Section | Configuration fields |
|---|---|
| `capacity` | `mode`, `ramHeadroomBytes`, `storageHeadroomBytes`, `cpuBudget`, `maxVcpusPerVm`, `reconcileSeconds`, `orphanReservationTimeoutSeconds` |
| `userDefaults` | `maxPrimaries`, `allowChildCreation`, `maxRetainedChildren`, `cpuBudget`, `ramBudgetBytes`, `storageBudgetBytes`, `maxChildLifetimeSeconds`, `allowNeverLifetime`, `allowSharing` |
| `userCaps` | Caps on retained children, CPU/RAM/storage, lifetime, never-lifetime and sharing; null leaves the value uncapped. |
| `lifecycle` | `gracefulShutdownTimeoutSeconds`, `leaseTickSeconds`, `leaseRetrySeconds` |
| `media` | `maxBytes`, `maxItemsPerUser`, `uploadChunkBytes`, `uploadTtlHours`, `acquireTimeoutMinutes`, `allowHttp`, `unreferencedTtlHours` |
| `network` | `hostForwardsEnabled`, `directAddressReporting` |
| `updates` | `repository`, `channel`, `drainTimeoutMinutes`, `healthTimeoutSeconds`, `requireSignature`, `manifestPublicKey` |

Disabling `network.hostForwardsEnabled` refuses new primary host forwards immediately; the default
preserves existing behavior. Stored user allowances override defaults, host caps narrow them, and
per-primary overrides can only restrict the result. Lowered limits do not delete existing VMs.

## Capacity accounting

The capacity ledger stores pending and held RAM, CPU and storage reservations in SQLite
(migration 300). `GET /host/capacity` is admin-only and returns the frozen summary,
problems, reservations, unmanaged VMs and per-user usage. `/host/status` uses the same
summary; allowance usage in both SQLite and fake mode includes pending operations, retained disks, media
and saved-state storage, aggregated across all of an owner's primaries and children.
Unmanaged VMs reduce host capacity without acquiring a Construct owner.
Reporting (`refresh=false`) never starts an inventory process: it uses the last observed
epoch and current ledger rows. Before the first inventory it reports incomplete. An
explicit refresh, admission with an invalid/expired epoch, or the reconciliation tick
performs the inventory read. A reconciliation pass reads inventory once, regardless of
VM count; its individual database mutations never call the hypervisor.

RAM admission uses the lesser of the physical-free and committed-allocation bounds.
Both retain OS headroom. Memory promised to pending **or held** reservations but not yet
assigned by Hyper-V is subtracted from physical free RAM. Thus a restart's intermediate
Off and a partially allocated Starting VM cannot give the same bytes to another caller.
CPU aggregate budgets are optional. Storage reserves each disk's full virtual maximum,
parent/checkpoint chains, media/upload maxima and `RAM + 64 MiB` for saved state. Already
allocated file bytes are counted through physical free space; only the remaining growth
is subtracted again. Artifacts and paths are deduplicated within one inventory epoch.

`capacity.mode=observe` records a would-refuse decision as `capacity.observe` and retains
the reservations; `enforce` rejects an incomplete inventory or insufficient capacity.
Requests are never resized. A capacity decision carries `resource`, `scope`, `requested`,
`allowed` (`AllowedAmount` in Core), `available`, `reason` and `epoch`. Runtime capacity
is released only after observed Off/Saved/Absent. A failed start with Unknown state keeps
its hold; Saved keeps disks and saved-state storage. Cleanup callers may pass Absent for
a disk/media reservation only after confirming the **artifact** was removed, not merely
that the VM registration disappeared.

Stored `host_config.capacity` takes precedence over bootstrap mode. Configure it with
`PUT /host/config` using the complete section:

| Field | Default | Meaning |
|---|---|---|
| `mode` | `observe` for migrated hosts | Accounting only, or enforced admission. Fresh installers may explicitly select `enforce`. |
| `ramHeadroomBytes` | null | Null computes `max(4 GiB, physical RAM / 8)`. |
| `storageHeadroomBytes` | 20 GiB | Headroom on each volume. |
| `cpuBudget` | null | Optional host active-vCPU budget. |
| `maxVcpusPerVm` | null | Optional per-VM CPU ceiling; backend hardware validation also applies. |
| `reconcileSeconds` | 60 | Startup reconciliation, then periodic passes; maximum cached inventory age. |
| `orphanReservationTimeoutSeconds` | 600 | Caller default for pending reservation deadlines. A live operation extends its reservations; elapsed time alone never proves release is safe. |

User RAM/storage/CPU budgets resolve stored user values against `userDefaults` and
`userCaps` in the **same transaction** as reservations. Null leaves the aggregate
unlimited by user policy; host and volume bounds still apply. Per-primary overrides
restrict delegation/count/lifetime/sharing as defined in the frozen contract and do not
provide a way around resource budgets.

Reconciliation reads all hypervisor VMs without VM/ledger gates, then tries each VM gate
without waiting, re-reads its state, and compares the persisted power-generation/job fence
inside the ledger transaction. Live operations are left alone. Orphan runtime reservations
are promoted on active/unknown evidence or released after a terminal observation and
the deadline; orphan storage requires present/absent/unreadable artifact evidence. Service
restart retains the table. Unreadable evidence keeps liabilities charged and fails closed
in enforce mode. The primary ISO catalog is physically accounted for by volume free space;
its in-flight build maximum remains the contract's explicit limitation.

Integration boundary for the capacity branch: `SqliteCapacityLedger.BeginAsync` takes
the one ledger gate and refreshes inventory before opening its IMMEDIATE transaction.
`BeginMutationAsync` skips inventory I/O and refuses `ReserveInTransaction`; use it for
mutations that cannot reserve, including reconciliation.
The returned `Transaction` exposes `Connection`, `Sql`, `ReserveInTransaction`,
`ConfirmInTransaction`, `ReleaseInTransaction` and explicit `Commit`; dispose without
commit rolls back every participating write. The integrator-owned `SqliteAdmissionStore`
must use this transaction for the whole `AdmissionPlan`/`MutateAsync` and must never call
standalone ledger methods from inside it. VM/media gates precede the ledger gate.
Before reserving a start, hold the VM operation gate and read the driver state. When it
confirms Off, release stale **held runtime** rows for that VM with
`ReleaseInTransaction(ids, Off)` before `ReserveInTransaction` in the same admission
transaction (or run the gate-protected per-VM reconciliation first). Do not sweep live
pending rows or infer Off from a timeout. This prevents an external stop followed quickly
by start from producing a stale-hold conflict in enforce mode or a duplicate hold in
observe mode.

Primary create/power/job call sites, child start intents/lease completion and abandoned
create-row/media cleanup remain integrator-owned. Those hooks must call `ReconcileAsync`
after power/job completion. This branch does not rewrite the existing provisioning flow
or claim that those later call sites already enforce capacity.

Pass-through/physical disks currently report `passthrough-disk-unavailable` and make
inventory incomplete. This is a deliberate conservative backend limitation, not a
transient failure: **enforce mode is unavailable on hosts with such an attachment**.
Observe mode remains usable. The initial adapter cannot prove the physical disk's
exclusion from all host volumes, so it does not guess zero growth. Supporting those
attachments requires an explicit physical-disk/volume mapping and a later field test.

The Windows adapter uses `IProcessRunner` argv and stdin artifact metadata. Its inventory
PowerShell is read-only and parsed/tested under Linux pwsh. No Hyper-V, LocalSystem or
Windows PowerShell 5.1 execution of this implementation has been performed.

## Persistence

One SQLite file, hand-written SQL and no ORM. `SqliteMigrationRunner` applies additive feature
migrations and records each version in `schema_migrations`; the base schema is created if missing.
Core tables include `users`, `tokens`, `vms`, `activity`, `forwards`, `audit`, `jobs`, with host
configuration, allowances, overrides and cascades added by M100. Name columns are `COLLATE NOCASE`, because identities
(`DOMAIN\user`) and VM names are compared case-insensitively everywhere else too.

- **Only hashes are stored.** A persistence test reads the raw database file and asserts that neither
  an API token nor a VM token appears in it, while their SHA-256 hashes do.
- **Quota enforcement is a transaction.** `IVmRepository.AddAsync(vm, maxVms, …)` checks the name,
  checks the owner's count and inserts inside one `IMMEDIATE` transaction, so concurrent
  `POST /vms` calls cannot both pass a check that was true a moment earlier.
- **Jobs are durable, their secrets are not.** `InProcessJobEngine` runs jobs in this process and
  writes every state change through `IJobStore`, all on one per-job chain so that a progress write
  still in flight can never land after — and undo — the terminal snapshot; the terminal write is
  awaited before anyone is told the job finished, and a store failure is reported on the job instead
  of being swallowed. SSE subscribers are per-process by nature. At startup a job left
  `Queued`/`Running` by a crash is marked failed ("interrupted by a service restart"), because it
  cannot be resumed. A one-time secret is never written, so a restart loses it.
- **Port forwards are durable too** (`IForwardStore` → `forwards` table), because forward state is
  host-side state by design: it has to outlive both the user's PC and the service process. The per-VM
  SSH port stays on the VM record, which is its canonical home, and is written there *before* the host
  rule is created — so a crash can leave a stored allocation with no rule (which reconciliation
  repairs) but never a live rule that no stored allocation accounts for and that a later VM could be
  handed as well. `IPortForwardManager.ReconcileAsync`, called at startup, rebuilds the process view
  from both stores: it reserves every allocated port in its range and re-materializes the host's rules.
- **Per-VM state transitions are serialized.** Allocating the SSH port, adding a forward and tearing
  all forwards down take the same per-VM gate in the forward manager, and adding re-checks inside that
  gate that the VM still exists and is not being deleted.

## How the platform plugs in

Every platform-specific concern is one interface in `Constructd.Core/Abstractions`, registered in
exactly one place — `Composition/ServiceComposition.cs`, which has two independent axes: persistence
(SQLite or memory) and platform (fakes or Windows).

| Interface | Implementation |
|---|---|
| `IHypervisorDriver` | `HyperVDriver`: `powershell.exe` running the repo's own `drivers/Load-ConstructDriver.ps1` contract (`docs/drivers.md`). A future Proxmox driver maps the same operations onto its REST API. |
| `IIsoBuilder` (consume) | By `Iso:Mode`: `OnDemandIsoBuilder` (default — reuses media or builds it on demand) or `WslIsoBuilder` (builds one ISO per VM through `wsl.exe`). See *ISO build strategies*. |
| `IIsoMediaBuilder` (produce) | `NativeIsoBuilder` for `Native` and `Prebuilt`; `WslIsoBuilder` only for explicit `PerVm`. Driven by on-demand VM jobs and `admin iso build`. |
| `IIsoCatalog` | `FileIsoCatalog`: versioned ISOs, sidecars and the `current.pointer` in `Iso:CacheDir`. Any build strategy publishes into it. |
| `IPortForwardManager` | `NetshPortForwardManager`: `netsh interface portproxy` rules over `IForwardStore`, reconciled at startup and periodically, plus connection counting from the host TCP table for the idle signal. |
| `IProcessRunner` | `ProcessRunner`: `System.Diagnostics.Process` with an `ArgumentList`. The only way this service starts anything. |
| `ITcpTableReader` | `IpHlpApiTcpTableReader`: `GetExtendedTcpTable` (`TCP_TABLE_OWNER_PID_ALL`). |
| `IUserStore`, `IVmRepository`, `ITokenService`, `IAuditLog`, `IJobStore`, `IForwardStore` | `Constructd.Sqlite`. |
| Negotiate | `builder.AddNegotiate()` inside the `OperatingSystem.IsWindows()` guard in `Auth/AuthenticationSetup.cs`. |
| Windows service | `builder.Host.UseWindowsService()` in `Program.cs`, under the same guard. |

Nothing above those interfaces knows about Hyper-V, PowerShell, netsh or Windows: B6b added them
without changing an endpoint, a policy, a job or an API test.

## What the Windows implementations actually run

Every command line below is asserted argument-by-argument by a test against a recording process
runner, which is why they can be written down here with confidence.

**Nothing is ever a command string.** Arguments are passed as an argv array (`ProcessStartInfo.ArgumentList`),
no shell is involved, and every value that reaches a child — VM name, port, path, switch name, seed
user — is validated first (`Internal/ArgumentGuard`). VM names must satisfy the shared instance-name rule
(`VmNameValidator`: 1–63 lowercase letters, digits or hyphens, starting and ending with a letter or
digit; the `construct-` prefix is reserved — the same rule the extension and the PowerShell readers
apply); paths must be absolute on a drive letter; ports must be 1–65535; nothing
may contain a control character.

### Hyper-V driver

Every operation is one process:

```
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand <base64 UTF-16LE>
```

`-EncodedCommand` means the script is a single opaque argv element, so quoting is a non-issue at the
process boundary. Inside the script, values are single-quoted PowerShell literals (which expand
nothing) with `'` doubled. Every script has the same shape:

```powershell
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$result = $null
try {
    $constructLib = 'C:\Construct\lib\AgentVm.Common.ps1'
    if (-not (Test-Path -LiteralPath $constructLib)) { throw "Construct lib not found: $constructLib" }
    . $constructLib
    $constructLoader = 'C:\Construct\drivers\Load-ConstructDriver.ps1'
    if (-not (Test-Path -LiteralPath $constructLoader)) { throw "Construct driver loader not found: $constructLoader" }
    . $constructLoader -Backend 'hyperv-local'
    <the operation, into $result>
    $envelope = [ordered]@{ ok = $true; value = $result }
} catch {
    $envelope = [ordered]@{ ok = $false; error = [string]$_.Exception.Message }
}
ConvertTo-Json $envelope -Compress -Depth 6
```

The service therefore **shares one implementation with the local install** rather than reimplementing
Hyper-V against raw cmdlets — the point of the B4 driver extraction. A fix to the driver reaches the
service without being ported, and a backend added to the loader is reachable by changing a backend id.
The shared lib is dot-sourced first because `Remove-ConstructVm` routes to its `Remove-AgentVm`
(disk-chain handling, checkpoint-merge wait), exactly as `docs/drivers.md` §2 requires.

| Interface method | The operation in the script |
|---|---|
| `CreateVmAsync` | `$descriptor = @{}` with `Name`, `ProcessorCount`, `MemoryGB`, `DiskGB`, `SwitchName`, `VhdPath`†, `IsoPath`†, `Nested`, `AutomaticCheckpoints` → `New-ConstructVm -Descriptor $descriptor` → `Start-ConstructVm -Name '<vm>'` |
| `RemoveVmAsync` | `Remove-ConstructVm -Name '<vm>'` |
| `StartAsync` | `Start-ConstructVm -Name '<vm>'` |
| `StopAsync` | `Stop-ConstructVm -Name '<vm>' -Force` |
| `SaveAsync` | `Save-ConstructVm -Name '<vm>'` |
| `GetStateAsync` | `$result = [string](Get-ConstructVmState -Name '<vm>')` |
| `GetEndpointAsync` | `$endpoint = Get-ConstructVmEndpoint -Name '<vm>'` → `@{ SshHost = …; SshPort = … }` |
| `WaitReachableAsync` | `$result = [bool](Wait-ConstructVmReachable -Name '<vm>' -TimeoutSeconds <n>)` |
| `DetachInstallMediaAsync` | `Detach-ConstructInstallMedia -Name '<vm>'` |
| `Capabilities` | `Get-ConstructDriverCapabilities`, read once and cached |

† `VhdPath` is emitted only when `Constructd:VmStorageRoot` is set (`<root>\<vm>.vhdx`); `IsoPath` only
when the job has built one. Leaving `VmStorageRoot` empty is not a gap — it hands the decision to the
driver, which uses Hyper-V's own default folder, so a service-created VM lands where a locally created
one does.

Two details worth knowing:

- **Starting is a separate call on purpose.** `docs/drivers.md` §3.3: `New-ConstructVm` creates and
  configures but leaves the VM off, so the caller owns when the unattended install begins.
- **`-Force` on stop.** The service runs under the SCM with `-NonInteractive`, where a confirmation
  prompt is a hang rather than a question.

**Progress.** The driver's own `Write-Step`/`Write-Ok` lines are forwarded to the job (and so to the SSE
stream) as they arrive — including `Wait-ConstructVmReachable`'s "not reachable yet" ticks during a
half-hour wait. The result JSON is the last stdout line, so progress is reported one line behind: the
most recent line is withheld until another arrives, which is what stops the envelope being reported as
progress.

**States** map exactly onto `VmState`: `running`, `off`, `paused`, `saved`, `absent`, and everything
else — a transient `Starting`, an unreadable Hyper-V — to `unknown`. `unknown` means "can't tell" and
is never read as "not installed".

**Capabilities** carry the console as a *kind*, not a flag (`ConsoleKind.None | VmConnect | Url` plus a
URL for the last): a client has to launch VMConnect, open a browser, or offer nothing, and those are
three different actions. `hyperv-local` reports `vmconnect`. A failed capability probe is **not**
cached and reports nothing supported — promising `Suspend` while the driver is unreachable would let
the API accept a save it cannot perform.

**Errors.** A non-zero exit, a timeout, output that is not the envelope, or `ok: false` all raise
`HypervisorOperationException`, whose message is composed here — *"The Hyper-V driver failed during
'create-vm' for VM 'work-vm'."*

**No child output is ever logged.** Not the stderr, not the last stdout line, not the driver's own
error text. PowerShell error text routinely carries a script path, a whole command line, or the values
a cmdlet was called with, and this service's rule is that dependency text is not repeated verbatim
anywhere — the log included (see *Auditing* above). What an operator gets instead is the operation, the
VM, and *how* it failed in the service's own words ("powershell.exe exited with 1", "timed out after 30
minutes", "the last output line was not the expected JSON envelope"), and, for the whole run, the
driver's own progress lines, which are streamed to the job as they happen. The same rule applies to the
ISO build and to netsh.

### ISO build strategies

Building install media is a **pluggable strategy**, because where it can be built
differs per host.

| `Iso:Mode` | Where media is built | Status |
|---|---|---|
| `Prebuilt` *(default)* | native producer on demand, with catalog reuse; compatibility name | **now** |
| `PerVm` | by the **service**, through `wsl.exe`, one ISO per VM | works wherever the service identity can run WSL |
| `Native` | standalone self-contained .NET tool, generic media in the catalog | **Windows installer default**; [details](../docs/native-iso.md) |
| `InGuest` | inside an existing Construct VM over SSH (xorriso is already there), result copied back — this is how the system will **self-update its install media** and fetch new source ISOs | planned |
| `HypervisorHost` | natively on the hypervisor host (xorriso on a Proxmox node) — the regular autoinstall path once Hyper-V is not the only backend | planned |

The Windows host installer selects `Native`: `admin iso build` produces generic media with
`NativeIsoBuilder`; `OnDemandIsoBuilder` reuses it or builds missing/requested media. `Prebuilt` remains
the configuration API default for existing deployments and also uses the native producer.
`PerVm` explicitly retains the WSL strategy. `InGuest` and `HypervisorHost` are still refused.

The seam is two narrow interfaces plus one catalog, and they are what a new strategy plugs into:

- **`IIsoBuilder`** — the *consuming* side. `VmJobs` calls `BuildAsync(vmName, seedUser, seedPassword, …)`
  and does not know which strategy answers. `OnDemandIsoBuilder` selects catalog media or
  invokes the producer for missing media or a redownload request.
- **`IIsoMediaBuilder`** — the *producing* side: generic media at a caller-chosen path, plus what went
  into it (source ISO + SHA-256, bootstrap key fingerprint, build-script SHA-256).
- **`IIsoCatalog`** — versioned files, sidecars, the current pointer, prune. Any strategy publishes here.

`admin iso build` is a thin driver over "a media builder + the catalog", so a future strategy needs no
CLI change. Builders never reference each other, and neither `IHypervisorDriver` nor the PowerShell
driver contract knows anything about ISO formats or KVP — a driver receives an `IsoPath` from the job
and nothing more.

**Generic media, hypervisor-supplied identity.** Media built for the catalog bakes in no hostname: the
seed carries the placeholder `construct-seed`, and the guest adopts its real name at first boot from a
pluggable **identity source** (`Iso:HostnameSource` → the script's `VM_HOSTNAME_SOURCE`):

| Source | How | Status |
|---|---|---|
| `hyperv-kvp` | `hv_kvp_daemon`'s pool file `/var/lib/hyperv/.kvp_pool_3`, key `VirtualMachineName` (512-byte key / 2048-byte value records) | now |
| `cloud-init-metadata` | Proxmox / NoCloud / ConfigDrive, where the hypervisor supplies per-VM identity natively | planned |

That is not cosmetic: the driver contract resolves a VM as `<vm name>.mshome.net`, and the Default
Switch's DNS publishes the **guest's own hostname**. A first-boot oneshot unit
(`construct-hostname.service`) reads the source, validates the name as a DNS label, sets it, fixes
`/etc/hosts`, renews the DHCP lease so DNS learns it, writes a marker and disables itself. Adding a
source is one `case` in that script. `bin/provision.sh` is the belt-and-braces backstop: a guest still
called `construct-seed` is renamed to `CONSTRUCT_INSTANCE_NAME`.

### The ISO catalog

In `Iso:CacheDir`:

```
construct-autoinstall-20260902T141530Z.iso        the media
construct-autoinstall-20260902T141530Z.iso.json   {builtAt, sourceIso, sourceSha256, seedUser,
                                                   bootstrapKeyFingerprint, hostnameSource, scriptSha256}
current.pointer                                    the file name of the current one
```

Plain files, so an administrator can read and repair them with the service stopped — which is exactly
when it matters. Two rules come from Hyper-V rather than from taste:

- **Media is never overwritten in place.** An attached ISO is held open by Hyper-V, and a VM may be
  installing from it right now. Every build writes a *new* versioned file; the pointer is then swapped
  by writing `current.pointer.tmp` and renaming it over the old one (atomic on NTFS).
- **`prune` skips what it cannot delete** and says why, instead of failing.

The pointer must name a plain `construct-autoinstall-*.iso` file: a value with a path in it is refused
rather than followed, because the pointer decides what the service hands to Hyper-V.

`OnDemandIsoBuilder` rebuilds media with a missing sidecar, changed seed user or hostname
source, or a known bootstrap-key mismatch. A failed replacement leaves the previous
published media intact.

### Native ISO build

The host installer resolves `.construct-tools/iso/Construct.Iso.exe` from a local source
checkout/SDK or the pinned, checksum-verified GitHub release. `Iso:NativeBuilderPath` can
override that path. `NativeIsoBuilder` calls it with `--request-stdin`; credentials and native
Windows paths travel as JSON on stdin, never shell arguments. The catalog's legacy
`BuildScriptSha256` field records the executable SHA-256 for native media. Download caching,
source verification, serialization, output checks and progress redaction are shared with
the retained shell strategy in `IsoBuilderBase`.

See [native ISO builds](../docs/native-iso.md) for release management and local testing.

### WSL ISO build (explicit legacy `PerVm` strategy)

```
# per VM (Iso:Mode = PerVm)                    # generic media (admin iso build)
wsl.exe [-d <WslDistro>] -u root -- env \      wsl.exe [-d <WslDistro>] -u root -- env \
    VM_USER=<Iso:SeedUser> \                       VM_USER=<Iso:SeedUser> \
    VM_PASS=<generated, never stored> \            VM_PASS=<generated, never stored> \
    VM_HOST=<vm name, lowercased> \                VM_HOSTNAME_SOURCE=<Iso:HostnameSource> \
    SOURCE_ID=<Iso:SourceId> \                     SOURCE_ID=<Iso:SourceId> \
    BOOTSTRAP_PUBKEY_FILE=/mnt/c/… \               BOOTSTRAP_PUBKEY_FILE=/mnt/c/… \
    bash /mnt/c/…/.build-autoinstall.lf.sh \       bash /mnt/c/…/.build-autoinstall.lf.sh \
         /mnt/c/…/<source>.iso \                        /mnt/c/…/<source>.iso \
         /mnt/c/…/iso/<vm>-autoinstall.iso              /mnt/c/…/iso/construct-autoinstall-<utc>.iso
```

One environment variable is the whole difference: a baked-in hostname, or an identity source. Both go
through the same code path below.

This mirrors `Auto-Install.ps1` step for step, because that path is proven and because the guest
payload has to be identical in every mode (plan §4.1):

- **The LF-normalized copy.** `bin/build-autoinstall-iso.sh` is copied to `bin/.build-autoinstall.lf.sh`
  with `\r` stripped (UTF-8, no BOM) and *that* is run, then deleted. A CRLF script — or an inline
  here-string through PowerShell → `wsl.exe` → bash — mangles quoting and breaks constructs like `trap`.
- **The path mapping is done in-process** (`C:\x` → `/mnt/c/x`) rather than by calling `wslpath`, for
  the reason recorded in `Auto-Install.ps1`: backslashes do not survive the trip, so `wslpath` would be
  handed `C:Usersmex.iso`.
- **The identity is env in front of `bash`**, each element its own argv entry — no shell quoting anywhere.
- **The source ISO** is `Iso:SourcePath` if set (used as-is, never deleted), otherwise `Iso:SourceUrl`
  downloaded **once** into `Iso:CacheDir` (to `<name>.part`, then renamed, so an interrupted download is
  never mistaken for a complete one) and reused by every later build. Size must be > 0; `Iso:Sha256`, when
  set, is verified on every use.
- **The output ISO is per VM** (`<vm>-autoinstall.iso`) in `PerVm` mode, because the guest hostname is
  baked into the seed; generic media is written to the versioned path the catalog chose.
- **Builds are serialized** with a `SemaphoreSlim(1)`: the LF copy is one shared file, and one xorriso
  repack of a multi-gigabyte image at a time is what the host can usefully do anyway.

The seed password is a secret. It is generated per VM, never persisted, and never logged: progress
lines and the failure detail are both passed through a redactor before they exist anywhere, and
`IsoBuildException`'s message is only *"Building the autoinstall ISO for VM 'work-vm' failed."* (A
media build carries its reason in the message instead — it is raised by `admin iso build`, at an
administrator's console, and the reason is composed here, never taken from the build's output.)
(It is still visible in the host's own process list while `wsl.exe` runs — the `VM_PASS=` env contract
of `build-autoinstall-iso.sh` is what `Auto-Install.ps1` has always used, and changing it would change
the guest payload. It buys an attacker a seed credential that the client's provisioning run replaces.)

### netsh port forwards

```
netsh.exe interface portproxy add v4tov4 listenaddress=<ListenAddress> listenport=<public> \
                                        connectaddress=<VM IPv4> connectport=<vm port>
netsh.exe interface portproxy delete v4tov4 listenaddress=<ListenAddress> listenport=<public>
netsh.exe interface portproxy show v4tov4
```

The VM's IPv4 is resolved from the driver's endpoint host at apply time (`Dns.GetHostAddresses`, IPv4
first; a literal needs no lookup) rather than stored — a VM on the Default Switch gets its address from
Hyper-V's NAT DHCP, and that changes.

- **SSH forward**: `connectport` is the VM's own SSH port from the driver endpoint, not a hardcoded 22.
- **Host-target forwards**: `connectport` is the requested guest port.
- **Client-target forwards are only recorded** — they are opened on the user's PC by the extension, so
  materializing them here would be exactly the LAN exposure the client target exists to avoid (§4.6).
- The public port is written to durable state **before** the rule is created, so a crash between the two
  leaves an allocation with no rule (which reconciliation repairs) and never a live rule that no
  allocation accounts for. A forward whose rule cannot be created is rolled back out of the store, so
  there is never a forward a user can see and nothing can reach.
- Deletes are best effort: a rule that is already gone, or a netsh that refuses, must not stop a VM from
  being deleted.
- **The two ranges must not overlap**, and the service refuses to start if they do (the installer
  refuses earlier, on the same rule). `SshForwardPorts` and `AppForwardPorts` are allocated by two
  independent allocators, so a shared port looks free to both: each would hand it out, and the second
  netsh rule would silently replace the first — one VM's SSH forward pointing at another VM's dev
  server. Adjacent ranges (`2201-2299` / `2300-2999`) are fine, which is the default.

**Firewall.** netsh portproxy only redirects; it does not open anything. The installer creates three
inbound TCP rules — the API port, the SSH forward range, the app forward range — so the ranges are open
as ranges rather than a rule appearing and disappearing per VM. Narrow their scope (`-RemoteAddress`) if
LAN-wide is too broad for the network the host sits on.

### Reconciliation semantics

`ReconcileAsync` runs at startup (`Bootstrap`), every 30 seconds by default
(`ForwardReconciliationService`), and on demand (`constructd admin forwards reconcile`).
The periodic pass retries when startup preceded guest networking and follows later DHCP changes,
including restarts outside Construct. It does not depend on idle evaluation or host wake-lock settings.
Unchanged rules are left alone. Failures during a periodic pass are logged safely and retried on the
next tick; they do not stop the API. The initial pass still reserves persisted port allocations before
accepting requests.

Reconciliation and forward mutations share one gate: a pass cannot resurrect a concurrently removed
forward or overwrite a reused public port. Each VM address is resolved once per pass (including an
unavailable result), keeping its SSH and app forwards consistent and avoiding a Hyper-V process per
forward.
netsh rules survive reboots and the store survives restarts, but they drift apart — so:

1. Read the host's rules (`show v4tov4`). The parser reads **rows by shape**, not by column header:
   four whitespace-separated tokens that are address, port, address, port. netsh is localized (a German
   host prints "Lauschen auf ipv4:" / "Adresse Port"), and reconciliation must not quietly do nothing
   there. `*` reads as `0.0.0.0`; IPv6 rows and anything else are skipped.
2. Walk the store: every VM's `SshForwardPort` and every forward's `PublicPort` is **re-reserved**,
   which is what stops a new VM being handed a port that is already in use. The reservation goes to
   whichever allocator's range *covers* the port, which is not always the allocator that handed it out
   — an admin who moves the SSH range off a port an existing VM still holds leaves that port sitting in
   the app range, and the app allocator has to know it is occupied or the next host forward is handed
   the very port that live rule uses. (The two ranges are disjoint, so exactly one allocator can claim
   any given port.)

   Whether a stored port is **re-materialized** is decided against **its own** range — the SSH range
   for a VM's SSH forward, the app range for a host forward — never the union of the two. A port
   outside its own range (an allocation from before an admin narrowed or moved the range) is
   grandfathered: logged, then left completely alone, with no netsh call either way. "This service only
   touches rules inside its configured ranges" has to hold for the rules it would *add* just as much as
   for the ones it would delete.
3. For each expected forward, use the VM's current address and:
   - no rule on that port → **add** it;
   - a rule with a different `connectaddress`/`connectport` → **delete and add** (netsh has no update);
   - a matching rule → leave it alone.
4. Delete rules that are **on our listen address and inside our configured ranges** but that the store
   knows nothing about — a VM removed while the service was down. Rules outside the ranges, or on another
   listen address, are never touched: the host may have port proxies that are none of this service's
   business.

A VM whose address cannot be resolved right now is skipped for step 3 but still counts as *known* in
step 4, so a DNS blip can never delete a working forward. `ReconcileAsync` returns how many rules it
repaired. If the rule list cannot be read at all it throws, and the service does not start: reconciling
against an unknown host state is worse than refusing.

**A deletion that netsh refuses fails reconciliation** rather than being counted. Deleting is best
effort when a VM is being *torn down* — a rule that may not even exist any more must not block a VM
from being removed — but in reconciliation the deletion *is* the repair: a rule the sweep could not
remove is inside our range, accounted for by nothing, and still forwarding on the LAN, so reporting the
host as reconciled would be a false all-clear. The same holds for the delete half of a re-point, where
carrying on would add a rule on top of one still aimed at the VM's old address.

**Connection counting** (the idle signal, plan §4.7) reads the host TCP table through
`GetExtendedTcpTable` and counts `Established` rows whose *local* port is one of the VM's public ports —
its SSH forward plus every host forward. Only established rows count: the portproxy listener is always
`Listen`, and a closed session lingers in `TimeWait` for minutes, so counting either would mean a VM is
never idle. A client tunnel rides the VM's SSH connection, so it is seen on the SSH forward (§4.6).

## Admin CLI

The same executable, with `admin` as the first argument, is a command-line mode that works the stores
directly — no HTTP, no authentication, no listener, no jobs:

```powershell
constructd admin users add DOMAIN\alice --role Admin --max-vms 10
constructd admin users add DOMAIN\alice --role User --max-vms 2 --no-host-forwards
constructd admin users remove DOMAIN\alice
constructd admin users list [--json]
constructd admin tokens issue DOMAIN\alice --label laptop [--json]
constructd admin tokens revoke-all DOMAIN\alice
constructd admin forwards reconcile
constructd admin host status [--json]
constructd admin iso build [--force] [--json]
constructd admin iso status [--json]
constructd admin iso prune [--json]
```

**`admin host status`** prints how this host advertises its VMs: `PublicHost`, the configured
`PublicHostPattern` (or that there is none), the name an example VM would be advertised as, the
wildcard DNS record the pattern needs, and the two forward ranges. It is the quickest way to tell
whether the wildcard is actually in play before hunting for a broken URL.

**`admin iso …` publishes install media for `Native` and `Prebuilt` modes**. The
installer's ISO step is exactly `admin iso build`, using the native producer.

- **`build`** resolves the source ISO (downloading and verifying it once when `Iso:SourceUrl` is set),
  generates a seed password it then discards, builds **generic** media through the configured strategy,
  writes the sidecar and swaps the pointer. It is **idempotent**: without `--force` it reports the media
  that is already published instead of spending twenty minutes rebuilding it, which is what makes
  re-running the installer cheap. `--force` is for a new Ubuntu release or a rotated bootstrap key. The
  last line is always `ISO: <path>` — the installer parses that.
- **`status`** prints what is published and what went into it, and exits **3** when nothing usable is
  there, so the installer and a health check can branch on "this host cannot create VMs".
- **`prune`** deletes superseded media and its sidecars, skipping (and naming) any ISO a VM still has
  attached — Hyper-V holds the handle.

It exists because the first admin has to be created before anybody can authenticate, and because an
operator standing at the host needs a way in when the API will not start or Negotiate is misconfigured.
The gate is the host itself: it reads the service's own configuration and opens its database, so being
able to run it already means having the privileges on the machine that owning the service implies. Its
actions are audited like any other (`actor = admin-cli`).

`--role` defaults to `User` and `--max-vms` to `0` ("may not create VMs") — the safe reading of a name
typed without thinking. `--role` is matched against the role *names*, so `--role 7` is a usage error
rather than a user whose role nothing in the service knows how to authorize. An unknown option, or an
argument a verb does not take, is likewise a usage error rather than something silently ignored: a
quota that did not take is worse than a refusal.

`tokens issue` prints the plaintext **once**; only its hash is stored, and it never reaches the log or
the audit trail. `--json` puts a machine-readable object on stdout and errors on stderr, which is what
the installer consumes.

| Exit code | Meaning |
|---|---|
| `0` | Success |
| `1` | The command was understood but failed (a store error, netsh, the wrong OS) |
| `2` | Usage error |
| `3` | No such user |
| `4` | Already exists, or still owns VMs |

`users remove` refuses while the user still owns VMs (their VMs would be left with an owner that does
not exist) and revokes their tokens with them. `forwards reconcile` needs the Windows platform and says
so plainly when it is not there, rather than reporting "0 repaired".

## Installing on a host

`service/host/Install-ConstructHost.ps1` (PowerShell 5.1, self-elevating, idempotent, `-WhatIf` on
everything that changes the machine):

```powershell
dotnet publish service\src\Constructd.Api -c Release -r win-x64 --self-contained true -o C:\Construct\service\publish

.\service\host\Install-ConstructHost.ps1 `
    -ScriptsDir C:\Construct `
    -PublishDir C:\Construct\service\publish `
    -PublicHost buildbox.example.local `
    -IsoSourceUrl https://releases.ubuntu.com/24.04/ubuntu-24.04.3-live-server-amd64.iso
```

In order: self-elevate → validate the inputs (including that the two port ranges do not overlap) → the
service root and data directory under `ProgramData` → **lock down everything the service executes or
trusts** → prerequisites (Hyper-V via the repo's own `Ensure-HyperV`, the OpenSSH client; no .NET runtime is needed when published
self-contained) → the TLS certificate → firewall rules → **the host's sleep timeouts** (reported, and
switched off on request) → `appsettings.Production.json` next to the
executable → **the autoinstall ISO, built with the native .NET tool** (`admin iso build`) →
**the first admin and an API token, through the admin CLI, before the service
starts** (so nothing contends for the SQLite file and the host is reachable the moment it comes up) →
register the service as LocalSystem → start it → print the enrollment details.

The ACL step comes **before** everything else on purpose: the ISO the service hands to Hyper-V and the
database it authorizes against are both written into the service root, so the root has to be protected
before anything is placed in it.

`-IsoBuildOnly` runs *only* the ISO step on an existing install and exits — no ACLs, no certificate, no
settings, no re-registration; `-SkipIsoBuild` leaves the host without media on purpose (the summary
says so, and creating a VM then fails with the command that fixes it).

Run it again after publishing a new build: it updates binaries, settings and the service in place.

| Parameter | Default | Meaning |
|---|---|---|
| `-ScriptsDir` | *(required)* | The Construct checkout the service invokes. Verified to contain `drivers\`, `lib\` and `bin\`. |
| `-PublishDir` | *(required)* | Where the published executable is. |
| `-ListenUrl` | `https://0.0.0.0:7462` | The port from this URL is the one opened in the firewall. |
| `-PublicHost` | `$env:COMPUTERNAME` | What clients dial and the certificate is bound to. |
| `-DataDir` | `C:\ProgramData\Construct\service` | Database + ISO cache. |
| `-SshPortRange` / `-AppPortRange` | `2201-2299` / `2300-2999` | Forward ranges; also the firewall rules. |
| `-CertThumbprint` | – | Use an existing certificate instead of creating one. |
| `-ServiceName` | `constructd` | Windows service name. |
| `-SwitchName` | `Default Switch` | Hyper-V switch for new VMs. |
| `-WslDistro` | `Ubuntu` | **Your** distro the ISO build runs in. |
| `-ListenAddress` | `0.0.0.0` | `listenaddress=` of the portproxy rules. |
| `-IsoSourcePath` / `-IsoSourceUrl` / `-IsoSha256` | – | The Ubuntu ISO to remaster. |
| `-AdminUser` / `-AdminMaxVms` | current user / `10` | The first admin. |
| `-SkipPrereqs` | off | Skip the Hyper-V/OpenSSH checks (a re-run on a host you already prepared). |
| `-SkipAclHardening` | off | Do not lock the three paths down (see below). |
| `-SkipIsoBuild` | off | Install the native tool but defer media creation until the first VM request. |
| `-IsoBuildOnly` | off | (Re)build the ISO on an existing install and change nothing else — a new Ubuntu release, a rotated bootstrap key. |
| `-RotateAdminToken` | off | Issue a fresh token even when the admin already exists. |
| `-KeepHostAwake` | *(ask)* | Set this host's AC sleep, hibernate and unattended-sleep timeouts to *never*. Not given: an interactive run asks, an unattended one (no console input, or `-WhatIf`) leaves the power plan alone. |
| `-SkipPowerSettings` | off | Do not read or change the power plan at all — no report, no prompt. |
| `-NoStart` | off | Register the service but do not start it. |

**The certificate's identity is the one value that has to leave the machine** — clients pin it at
enrollment — so the installer prints it prominently at the end, next to the admin token. A
self-signed certificate is created once and **reused** on later runs: a fresh one per install would
break every client's pin.

> ⚠ **Known mismatch: the printed value is not the value clients compare.** The installer prints
> `X509Certificate2.Thumbprint`, which is the **SHA-1** thumbprint (40 hex characters) — the right
> value for `Constructd:CertThumbprint`, which selects the certificate from `LocalMachine\My`. But
> the clients pin the **SHA-256** fingerprint (`Get-ConstructCertificateFingerprint` in
> `lib/AgentVm.Remote.ps1`, 64 hex characters shown as colon-separated pairs), which is what the
> enrollment prompt displays. Publishing the printed thumbprint therefore gives users something they
> cannot compare with what they see. Until the installer prints both, compute the SHA-256 form on the
> host and publish **that**:
>
> ```powershell
> $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq '<the printed value>'
> ([BitConverter]::ToString(
>     [Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData))) -replace '-', ':'
> ```

The service runs as **LocalSystem**. It drives Hyper-V and netsh, neither of which a restricted service
account can do here without further setup — which is also why nothing in this service builds a command
string.

**Windows ISO builds use the native tool.** The installer resolves a self-contained executable,
then runs `<PublishDir>\Constructd.Api.exe admin iso build` to publish generic media into
the catalog. No distro or package installation is involved. The shell strategy is retained
for explicit legacy use and future Linux/Proxmox hosting.

Two consequences the installer handles explicitly:

**Everything the service executes or trusts is locked down before the service is ever registered.**
LocalSystem *executes* what it finds in `-PublishDir` and `-ScriptsDir` (the published exe, the
PowerShell driver, the ISO builder executable), so write access to either is equivalent to running code as
LocalSystem; the **service root** (the parent of `-DataDir`) holds the authorization database — users,
token hashes, the VM registry, the audit trail — plus the ISO catalog, whose media is what every new VM
installs itself from. On a host several people can log into, an unprivileged user who can pre-create or
modify any of them owns the service.

A DACL on the leaf directory alone does not achieve that, because three things sit outside it: the
directories *above* it, the entries already *inside* it, and links pointing elsewhere. So for each of
the service root, `-PublishDir` and `-ScriptsDir` the installer:

- walks the **whole ancestor chain** and refuses a **reparse point** anywhere along it (the ACL would
  apply to the link while the service reads through it to a target somebody else owns), a path under a
  **user profile root**, and any ancestor on which an untrusted SID holds `DELETE`,
  `FILE_DELETE_CHILD`, `WRITE_DAC` or `WRITE_OWNER` — those beat any ACL set on the child, since a
  parent you can delete children in lets an attacker swap the hardened directory for their own.
  Creating *new* entries in a parent is deliberately allowed: that is exactly what `C:\ProgramData`
  grants `Users` on a stock Windows. **Inherit-only (`(IO)`) ACEs are not judged either** — they grant
  nothing on the object that carries them, they are a template stamped onto children as those are
  created. Stock Windows puts `CREATOR OWNER:(OI)(CI)(IO)(F)` on `C:\ProgramData`, so treating it as a
  finding would refuse the *default* `-DataDir` on every clean host; and any directory the installer
  creates under it has its DACL replaced outright anyway;
- replaces the DACL with an explicit one and turns inheritance off — `-PublishDir`/`-ScriptsDir` get
  SYSTEM and Administrators full control plus `Users` read-and-execute, the service root gets SYSTEM
  and Administrators and nobody else — by **well-known SID**, so it is right on a non-English host,
  and sets Administrators as the owner so an administrator can always repair it later;
- **recursively strips every existing descendant's own DACL** so it inherits that policy. A file an
  attacker pre-created with inheritance disabled — `Constructd.Api.exe`, `Load-ConstructDriver.ps1`,
  `constructd.db` — would otherwise keep its attacker-writable ACL through a `Set-Acl` on the parent;
- re-reads the **whole tree** afterwards and fails if anything outside that policy can still write.

The **service root**, not `-DataDir`, is the hardened unit: the database and the ISO catalog both live
under it, so one protected tree covers both and no untrusted directory sits between them. The catalog
belongs in it as much as the database does — the media it holds is what every new VM installs itself
from, and the pointer decides which file that is.

The decision itself — "which of these ACEs are dangerous" — is a pure function over ACEs as plain data
(`Get-ConstructUnsafeAce`), which is what lets the tests exercise an attacker-writable parent and a
protected child carrying an explicit untrusted ACE on a machine that has no Windows ACLs at all.

`-SkipAclHardening` turns it off for a host where you manage those ACLs yourself; the installer then
prints what you are taking responsibility for.

**No configurable value is ever concatenated into a script, and nothing crosses a privilege boundary
through a file the caller can still rewrite.** The installer runs code in one more privileged context —
the self-elevated copy of itself — and there a parameter carrying a quote, a semicolon or a newline
would otherwise become another statement running elevated. So values never appear in generated script
text: they are serialized to JSON and **splatted** back on the other side. A distro name containing a
space stays one argument; an apostrophe cannot end a literal. The only thing interpolated into the
generated script is a path the installer itself produced.

The payload is embedded in the encoded command itself, as inert base64. A file would have to sit in the
*unelevated* caller's own writable `%TEMP%`, and the elevated copy reads it only after the UAC prompt is
answered — so another process running as the same user can watch for the file and swap its contents in
that window, choosing the `-ScriptsDir` that then executes as LocalSystem or adding
`-SkipAclHardening`. A GUID name prevents guessing the name in advance, not noticing it appear.
`-EncodedCommand` is part of the elevated process's command line, which the caller cannot alter once
`Start-Process` has been called. Everything else the installer runs — the ISO tool, the admin CLI — is
invoked with an argument **list** in its own (already elevated) context, so no quoting decision exists
to get wrong.

`Uninstall-ConstructHost.ps1` carries the same transport, and needs it more: it is the script with
`-RemoveData` on it, so a value able to inject an argument after UAC turns a harmless uninstall into an
irreversible one against a path of somebody else's choosing. (`-ArgumentList` is not an escape here —
PowerShell flattens it back into a single command-line string.)

**The ISO step runs as you, and fails closed.** After writing `appsettings.Production.json` (which the
CLI reads for the cache directory and the source ISO) and before registering the service, the installer
runs `admin iso build` and stops the install if it fails, showing what the build printed — an exit code
alone says nothing about why the native ISO tool or a download failed. The build is idempotent, so
re-running the installer costs nothing; `-SkipIsoBuild` defers it (and the summary then says VM
creation will fail until it is built), and `-IsoBuildOnly` runs *only* this step on an existing install
— no ACLs, no certificate, no settings, no re-registration, so it cannot disturb a host that is serving
VMs. A running install keeps the ISO it has attached: the pointer swap is what makes the new media
current.

**The host's own sleep timeouts are reported, and changed only when you say so.** The service holds a
power availability request while VMs run (*Keeping the host awake*), but that covers only the window
in which it is running, and a host expected never to sleep should say so in its power plan too. The
installer therefore prints the active scheme's AC and DC values for `STANDBYIDLE`, `HIBERNATEIDLE` and
the hidden unattended-sleep timeout (`7bc4a2f9-…`) — so the numbers are in the install log either way
— and then: `-KeepHostAwake` sets the three **AC** values to `0` (*never*), `-KeepHostAwake:$false`
leaves them, and with neither an interactive run asks while an unattended one (no console input, or
`-WhatIf`) leaves the machine alone. `-SkipPowerSettings` skips the whole step. It is idempotent: a
value that is already `0` is not written again, and the battery (DC) values are never touched.

**Whether it may ask is decided before the self-elevation, not after.** The elevated copy is started
with `Start-Process` and gets a brand new console: it cannot see that the caller's input was
redirected, that the caller was started `-NonInteractive` (where `Read-Host` *throws* rather than
returning a default), or that there is no interactive session at all. So the unelevated copy makes
that call on its own session — `Test-ConstructPromptAllowed`, a pure function over the four facts —
and, when the answer is "unattended", carries `-KeepHostAwake:$false` in the elevation payload with
the rest of the parameters. Without that, every automated install would stop at, or fail on, a prompt
nobody can answer.

Everything is addressed **by GUID** and read out of the hex indices, never by matching what powercfg
prints: every label it prints is localized, and a text match would report "never" on a German host
that sleeps in half an hour. A host that has no `powercfg`, or refuses the write, produces a warning
and not a failed install — the power plan is a comfort setting.

**Re-running is safe and adds no credentials.** "The admin already exists" is not the same as "the admin
is an admin": the installer reads the account back and fails if its role is not `Admin`, rather than
reporting success on a host that has no admin at all. And a re-run issues **no new token** unless
`-RotateAdminToken` is passed — otherwise every reinstall would leave another permanent credential
behind.

`service/host/Uninstall-ConstructHost.ps1` is the companion: it stops and deletes the service and
removes the firewall rules. Its `-RemovePortProxies` pass reads netsh's rows with the same
shape-based rule the service's own parser uses (four tokens that are address, port, address, port,
with `*` normalized to `0.0.0.0`) and checks netsh's exit codes, so it cannot report "removed 0" on a
host that prints the wildcard listen address while leaving every rule behind. Three things it leaves alone unless asked — the data directory
(`-RemoveData`; it is the record of who had what, and a reinstall expects to find it), the port-proxy
rules (`-RemovePortProxies`), and the certificate (`-RemoveCertificate`). It never talks to Hyper-V:
deleting a colleague's VM is not an uninstall step.

## Tests

`dotnet test service/Constructd.sln` — 594 tests, all running on Linux, Windows platform included.

- **Core unit tests**: port allocation (lowest-free, no double allocation, release, exhaustion,
  reservation, concurrency), token hashing (format pinned to a known SHA-256 vector, fixed-time
  comparison), VM name validation, ownership and quota rules, the idle decision matrix including the
  grace-window cases (stale heartbeat, never-reported VM, timeout shorter than the grace), cap
  clamping, the idle engine against the fake driver, the console-capability mapping, the pure
  connection counter (only `Established` rows on the VM's own ports count), and the host power
  guard — the pure rule (only `Running` VMs count), the reconciler following VMs as they start,
  save, stop and are deleted without ever asking the platform twice, `Power:KeepHostAwake=false`
  never taking a request at all, a failed acquire being retried rather than remembered as held,
  concurrent callers never double-acquiring, dispose releasing what is still held, and dispose still
  closing the platform handle when the release itself throws.
- **Scheduler tests**: the two responsibilities on the one loop are switched independently — a tick
  with idle evaluation off still reconciles the power request and a tick with the power request off
  still evaluates idle policy (both driven through `TickAsync`, so nothing waits on a timer), the
  startup reconcile happens before the first tick, and the composition root registers the loop when
  **either** setting is on and not at all when neither is.
- **Job engine tests**: a store that holds a progress write until completion starts, proving the
  terminal snapshot is what the store ends up with; a store that holds the *terminal* write, proving
  that no terminal state, no state event and no one-time token is observable before it lands; a store
  failure surfacing on the job (as a type, not a message); a job from before a restart still being
  readable and streamable; cancellation.
- **Persistence tests**: every store against a real SQLite file that is then reopened ("restart"),
  the quota transaction, job recovery after a restart, no plaintext secret anywhere in the file,
  forwards (and their ports) surviving a restart with reconciliation re-materializing the host rules
  and no port colliding afterwards, a client ack surviving a restart with the same link, a pre-ack
  `forwards` table gaining the five ack columns on open without losing a row (and idempotently on the
  next open), an SSH allocation that is durable before its host rule exists (so a
  crash right after it cannot hand the port out twice), a dependency failure whose message contains a
  sentinel secret appearing nowhere in job state, the SSE stream, the audit trail or the file, plus the
  whole API driven over HTTP against SQLite.
- **API integration tests** (`WebApplicationFactory<Program>`): the exact route inventory, the
  invariant that no route is reachable without authorization, the invariant that every mutating route
  is auditable, 401/403 behaviour per scheme, the complete VM-token scope matrix, the admin surface,
  quota enforcement, the creation job including its SSE stream, rollback (removal of the partially
  created VM, independent cleanup steps when the forward manager throws, and releasing the reservation
  when the job cannot be queued at all), the delete fence (a VM token cannot slip a forward past an
  accepted deletion, and power/policy/heartbeat answer 409 while it runs), the one-time token (handed out once in `result.vmToken`,
  absent from the stored job, absent on replay), forwards (client vs host, per-user host gating,
  VM-token scoping, caps, port exhaustion), the ack relay (the auth matrix — owner and admin yes, the
  VM's own token and another user `403`, anonymous `401` — the link shapes, re-acking, `404` for an
  unknown or another VM's id, `409` for a host target and for a VM being deleted, invalid payloads,
  control characters stripped from the label and the message, and the audit entry), concurrency
  (quota and forward caps under simultaneous requests), idle policy with cap clamping, the heartbeat,
  and audit entries for successes, validation failures, refusals, malformed bodies and a throwing
  hypervisor.
- **Expose-CLI contract tests**: `bin/construct-expose.sh`'s own lenient, `jq`-optional parser,
  re-implemented in C# and run against the real serialized bytes of `GET /vms/{name}/forwards` — so
  the flatness of a forward object, the meaning of `url`/`status`/`localPort`/`hostLabel`/`message`
  and the fields `--list`/`--close` match on are pinned by a test rather than by convention.
- **Secret-hygiene tests**: a dependency exception carrying a sentinel secret, raised in the request
  path, in a background job, in the idle engine, in the forward manager, and in token validation (where
  the sentinel is the presented plaintext token) — asserted absent from the response body, the audit
  trail, job state, the SSE stream and every captured log entry, while the service's own error messages
  still come through in full.
- **Windows platform tests**, all against a recording process runner, so the command lines are pinned
  on a machine with no Hyper-V, no WSL and no netsh:
  - *Hyper-V driver*: the exact `powershell.exe` argv and the **decoded script** for every operation
    (the dot-source of the loader and the shared lib, the backend id, the contract function and its
    arguments, and the absence of any raw Hyper-V cmdlet); the descriptor the create script builds,
    including `VhdPath` being omitted when no storage root is configured; the create-then-start order;
    every state of the contract enum mapped, with an unmapped one landing on `unknown` and never
    `absent`; the endpoint; an expired wait reported as "not reachable" rather than as a failure; the
    capability probe cached on success, retried and reporting nothing on failure, and mapping the console
    kind; progress forwarding the driver's own lines but never the result envelope; and the error paths
    (non-zero exit, timeout, non-JSON output, `ok: false`) all yielding a safe message while the detail
    stays on the exception and the log.
  - *Injection*: a VM name that is not a DNS label, a control character in a configured value, and a
    newline in the seed password are all refused **before** any process is started; a quote in a
    configured switch name is escaped rather than closing the PowerShell string.
  - *ISO build*: the exact `wsl.exe` argv for **both** halves of the seam — per-VM (`VM_HOST=`) and
    generic media (`VM_HOSTNAME_SOURCE=`, and no `VM_HOST` at all) — including the WSL path mapping and
    the env contract; no `-d` when no distro is configured; the LF normalization of the script copy and
    its removal; the source ISO used as-is when configured, downloaded once and reused otherwise,
    rejected when truncated or when the checksum does not match; a build that reports success but
    produces no ISO failing anyway; progress that never repeats the seed password; a per-VM build
    hashing nothing it does not have to; and builds actually serializing.
  - *ISO catalog and strategies*: the versioned name reserved rather than merely computed (two
    allocations in the same second, and a name that already exists, never collide); the sidecar written
    before the pointer and the pointer swapped through a temporary file; media never overwritten in
    place; a pointer that names a missing file or anything outside the catalog ignored; prune keeping
    the current entry and skipping an ISO a VM still holds open; the pre-built strategy handing back the
    current media, ignoring the VM name and the seed password, refusing media with no readable sidecar,
    and warning when the host's bootstrap key no longer matches the one in the sidecar; and the mode →
    strategy wiring, with every planned mode refused by name.
  - *`admin iso`*: build/status/prune through the real catalog over a fake file system — the versioned
    file name, the `ISO: <path>` last line the installer parses, idempotence without `--force`, a failed
    or empty build leaving the previous media current, the audit entry, `status` exiting 3 when nothing
    usable is published, and prune's in-use skip.
  - *Port forwards*: the exact add/delete argv for the SSH forward and for host forwards; the configurable
    listen address; the durable-before-live ordering; a client forward never touching the host; a forward
    whose rule fails being rolled back and its port freed; a netsh that refuses to delete not blocking a
    removal; the full reconciliation matrix (add missing, leave correct alone, re-point after an address
    change, delete an unknown rule in our range, never touch one outside the ranges or on another listen
    address, keep a live rule when the VM cannot be resolved, grandfather a stored SSH port or host
    forward that now falls outside the configured ranges without any netsh call, re-reserve allocated
    ports, materialize stored host forwards); a grandfathered port not being re-issued by the *other*
    allocator; overlapping ranges refused at startup and adjacent ones accepted; a netsh whose output
    cannot be read failing rather than reporting success; a refused delete failing reconciliation
    instead of counting as repaired, while teardown stays best effort; and connection counting across
    the SSH forward and the host forwards.
  - *Log hygiene*: a sentinel in a child's stdout and stderr driven through every failure shape of all
    three implementations — non-zero exit, timeout, malformed output, a driver-reported error, a failed
    delete — asserted absent from every captured log entry, while the composed reason ("get-state",
    "netsh add exited with 1") is present.
  - *netsh parser fixtures*: the English and German layouts, headers/separators/blank lines, ragged
    spacing and CRLF, the `*` wildcard, and IPv6 or nonsense rows being skipped.
  - *Process runner* (against POSIX binaries, skipped on Windows): arguments reaching the child verbatim
    with no word splitting, command substitution or globbing; stdin written and closed; stdout streamed
    line by line; stderr captured separately; a child that outlives its timeout killed and reported as
    timed out; and the caller's cancellation staying a cancellation.
- **Admin CLI tests**: every verb against in-memory stores — the user created with the right role and
  quota, the safe defaults, host forwards deniable, a conflict rather than an overwrite, bad options as
  usage errors that create nothing, a numeric `--role` refused rather than parsed, trailing arguments on
  every verb that takes none refused rather than ignored, removal revoking tokens and refusing while VMs
  are owned, a token that validates once and never appears in the audit trail, revoke-all invalidating
  it, `forwards reconcile` reporting the missing platform, and the exit codes.
- **Host sleep settings** (part of the installer suite): the same `powercfg /q` block in English and
  in German parses to the *same* numbers — nothing keys off a localized label, only off the hex
  indices, of which the **last two** are the current AC and DC values (the minimum, maximum and
  increment lines above them have the identical shape); a dump with no indices yields nothing rather
  than a wrong number; the three settings travel as GUIDs, the hidden unattended-sleep one included;
  and the report and the setter are driven against a **mocked** powercfg — every row queried by GUID
  against the active scheme, a refused query reported as *unavailable*, `/setacvalueindex … 0` written
  only for a timeout that is not already *never*, the DC (battery) values never touched, `-WhatIf`
  writing nothing, and a refused write warning instead of failing the install. The unattended
  decision is covered too: every spelling of `-NonInteractive` recognised (and `-NoProfile`,
  `-NoExit`, `-NoLogo` not mistaken for it), each of the four unattended facts refusing the prompt on
  its own, and — structurally — the decision being resolved and put into the elevation payload
  *before* anything is started elevated, from a copy of `$PSBoundParameters` rather than the live one.
- **Host installer tests** (`service/tests/host-installer.test.ps1` — 359 assertions, run by
  `dotnet test` through `HostInstallerTests` wherever `pwsh` exists): both scripts parse with zero
  errors and use no syntax PowerShell 5.1 lacks; every documented parameter exists with its documented
  type and default, and the three new switches are opt-in; the uninstaller's defaults match the
  installer's; `SupportsShouldProcess` is declared and the changes are guarded, including in the helper
  functions (a plain function would not get `-WhatIf`); the pure helpers — port-range parsing, listen-URL
  parsing, the ACL policy and the uninstaller's portproxy row parser — accept and reject the right
  things, with the ACL policy asserted to be SID-based, to give `Users` read-and-execute on code and
  nothing at all on data, and the row parser asserted against the English and German headers, the
  separator, an IPv6 row, an out-of-range port and the `*` wildcard; every settings key the installer
  writes is one the service actually binds; the paths are hardened and the admin is created before the
  service is registered; an existing account's role is verified rather than assumed; a token is issued
  only on creation or on `-RotateAdminToken`; the settings are written before the ISO is built and the
  ISO before the service is registered, the build goes through the admin CLI as an argument list and
  fails closed with what it printed, `-SkipIsoBuild`/`-IsoBuildOnly` do what they say, and **no trace of
  the old "run WSL as LocalSystem" path remains** (no task runner, no scheduled task, no distro
  export/import) — a leftover would fail every install; and a missing OpenSSH client is installed rather
  than warned about. The value transport is
  driven with everything a parameter could carry — spaces, apostrophes, quotes, semicolons, newlines,
  control characters, subexpressions, backticks — asserting for **both** scripts that the value never
  appears as source in a generated script, that it still decodes back byte for byte on the privileged
  side, that an injected `-RemoveData` stays data rather than becoming a switch, and structurally that
  the elevated copy reads its parameters from no file at all and that neither script stages anything in
  the caller's `%TEMP%`. Inherit-only ACEs are covered both ways: a stock `CREATOR OWNER:(IO)` on
  `ProgramData` does not refuse the default `-DataDir`, and the same ACE without `(IO)` still does.

## Open points for the follow-up batches

- `Job` carries an `Owner` and `IJobEngine.SubmitAsync` takes it — not in the plan's sketch, but job
  authorization cannot be derived from the VM record: a delete job outlives it, and a create job hands
  out a secret.
- The idle engine's per-VM "last active" watermark is in-process: a restart restarts the idle window
  rather than idling VMs out on history it cannot see. Persisting it alongside the VM row is a small
  follow-up if that matters.
- `client`-target forwards are relayed through the ack route (B8). The service still does not *push*
  them: the extension polls `GET /forwards` every 10 s while a window has that instance active. A
  long-poll or an SSE stream would cut the latency and the chatter; the poll is what B8 shipped
  because it needs nothing new on either side.
- The `url` on a forward is advisory (`http://<publicHost>:<port>/` for a host target, the client's
  reported link for a client target). Per-VM hostnames for cookie-sensitive services stay out of
  scope (plan §4.9).
- Quota semantics: `maxVms` is a plain cap and `0` means "may not create VMs"; "unlimited" has to be
  expressed as a large number.
- Schema evolution now uses the additive feature migration runner and `schema_migrations`.
  M100 backfills existing VMs as primaries with legacy credentials. Breaking migrations remain
  outside this delivery; per-feature ranges and compatibility rules are in the host-admin contract.
- The capability's console **kind** (`vmconnect`, a URL, none) is read from the driver and carried on
  `DriverCapabilities`, but **no endpoint exposes it**. It has no consumer either: the extension's
  `hyperv-remote` driver hardcodes `console: "none"` (there is no `vmconnect` to a machine you are not
  sitting at), so the panel offers no console affordance for a remote VM. Surfacing it — for a
  backend that *does* have a console URL, e.g. Proxmox's noVNC — is a response field, not a redesign.
- Plan §4.4 has the service create its **own internal NAT switch** at install. `Constructd:SwitchName`
  is the seam for that and defaults to Hyper-V's `Default Switch`, which is what a host with nothing
  else configured has; the installer does not create a switch yet.
- The service creates VMs but does not **update the checkout** it invokes (`constructd update` in the
  plan's sketch). Today that is the admin re-running the installer after a `git pull`.
- No **wake-on-SSH**: a connection to a saved VM's forward is not detected, so a saved VM is resumed by
  a user action rather than by dialing it (recorded as a stretch goal in plan §4.7).
- The seed password is visible in the host's own process list for the duration of the ISO build. It is
  the `VM_PASS=` env contract of `bin/build-autoinstall-iso.sh`, which the local installer has always
  used; changing it would change the guest payload, and what it buys is a seed credential the client's
  provisioning run replaces.

### On-demand native media and remote redownload

`Native` and the compatibility `Prebuilt` mode now use the native producer during VM
creation when no media is published. `POST /api/v1/vms` accepts
`opts.redownload: true` to force a fresh fetch from the host-configured `Iso:SourceUrl`
and a new patch before creating the VM. Normal creates reuse catalog media.
The source checksum is verified before replacing the cached download; builds publish
new versioned files so other installations retain their mounted media.
`-SkipIsoBuild` on the host installer installs the tool but defers media creation.
See [native ISO builds](../docs/native-iso.md) for upgrade and source configuration.

## Child-VM media registry

Child media uses its own M200 SQLite registry and private media directory. It never
calls the primary Construct ISO catalog, downloader, patcher, or provisioner.
`Constructd:HostAdmin:Media:RootDir` selects the directory (default
`C:\ProgramData\Construct\service\media`). This is the existing stage-1 option
for the contract's `Media:RootDir`; fake mode uses a disposable directory. Real
host deployment must grant only the service identity, SYSTEM and administrators
access to this directory, including auxiliary answer-file media. The updater pair
owns installer hardening; this branch does not edit the installer.

The `/api/v1/media` routes accept enrolled users and upgraded primary tokens.
Owners see their media; administrators can inspect all owners. A shared-child
consumer can read only the attached item's `id`, `name`, `role` and `sizeBytes`.
There is no media-content download route. Full metadata omits host paths; checksums
and source metadata for auxiliary ISOs stay behind owner/admin access.

- `POST /media/acquire` accepts `url`, `role`, optional `name`,
  `expectedSha256`, `dedicatedTo` and `operationKey`, returning a `media-acquire`
  job and media id. HTTP requires a checksum and the `media.allowHttp` policy.
  The transport refuses credentials, proxies, cookies, private/reserved addresses,
  private connected peers, TLS downgrades and more than five redirects. DNS is
  validated at each hop and the connection uses a validated IP directly. Query
  strings and fragments are removed from persisted source URLs; progress includes
  host names and byte counts only. URL admission conservatively reserves the full
  item limit until completion; it does not make a header probe before admission.
- `POST /media/uploads` accepts `name`, `role`, `sizeBytes` and the optional
  checksum, dedicated VM and operation key. It returns `uploadId`, `mediaId`,
  `chunkSizeBytes`, `chunkCount`, `expiresAt` and received indexes. Send exact-size
  octet-stream chunks with Content-Length to
  `PUT /media/uploads/{id}/chunks/{index}`. `GET /media/uploads/{id}` returns
  received and missing indexes for resumption. Re-sending an index overwrites it.
- `POST /media/uploads/{id}/complete` freezes writes, hashes the file, verifies
  the expected checksum (when supplied), and checks the ISO primary descriptor
  when no checksum was supplied. Completion returns 201, subsequent completion
  returns 200, and verification above 2 GiB uses a `media-verify` job (202).
  Aborting through `DELETE /media/uploads/{id}` wins over an in-flight hash.
- `GET /media`, `GET /media/{id}`, `GET /media/{id}/references` and
  `DELETE /media/{id}` expose registry status and retention. Any VM reference
  prevents deletion, including an administrator's cross-owner attachment. Storage
  is charged once to the media owner. Reference removal is a backend primitive:
  child jobs must confirm detachment/removal before calling it.
- Admin `POST /media/cleanup` and the daily cleanup job expire open uploads,
  retry deletion, collect eligible failed/unreferenced items, and remove managed
  orphan files older than one hour. Ready-item collection is opt-in through
  `media.unreferencedTtlHours`. Dedicated items whose VM still exists are retained
  unless deletion was explicitly requested. Failed deletion stays visible and
  retains its reservation until both `.part` and `.iso` are confirmed absent.
  Startup recovery fails interrupted transfers/completions and cleans their files;
  open, unexpired uploads with a partial file remain resumable.

Media policy is stored in the `media` host-config section: default limits are
16 GiB per item, 20 active items per owner, 8 MiB chunks, a 24-hour upload TTL,
180-minute acquisition timeout and a 120-second read-idle timeout. Size overflow,
checksum mismatch, incomplete uploads, in-use media and URL refusals use coded
RFC 7807 responses. Every mutation is audited without source queries, media
contents or host paths. Upload begin and acquisition accept the operation-key
header (which overrides the body key) and use the shared atomic admission seam.

Integration boundary: this media branch supplies `SqliteMediaStore.InsertInTransaction`
overloads for media, uploads and references, and consumes `IAdmissionStore`,
`ICapacityLedger` and `IPersistedJobRunner`. The stage-1 production admission and
persisted-job implementations are still explicit placeholders; the integrator,
capacity and child-jobs branches must supply them before production acquisition
or admission works. Linux tests exercise the real media routes with the in-memory
admission/capacity stores, a recording persisted runner, simulated DNS/connections,
and both SQLite and in-memory media stores. No new Hyper-V or Windows execution
is claimed here.

Installer hook supplied to the updater pair (Windows PowerShell 5.1 syntax):
set `$mediaRootDir = Join-Path $DataDir 'media'`, include it in the existing
ShouldProcess-controlled directory-creation loop, and add
`@{ Path = $mediaRootDir; Kind = 'Data'; Name = 'child media registry' }` to the
existing sorted `$hardening` entries. Add
`HostAdmin = [ordered]@{ Media = [ordered]@{ RootDir = $mediaRootDir } }` under
`$settings.Constructd`, merging any other HostAdmin sections. The existing
`Set-ConstructPathAcl` supplies the SYSTEM/Administrators-only ACL and ancestor
checks. Upgrades must preserve an already-configured media root and its contents.

Cleanup jobs return `{ removed: [ids], retained: [{ id, reason }] }`. Reasons include
`busy`, `held-open`, `referenced`, `dedicated` and `not-eligible`; each media-gate
wait is limited to one second so an active download cannot block the whole sweep.
A user's held-file deletion retries only that item. Inline verification continues
when its HTTP client disconnects; clients can poll completion safely. Unsupported
admission/job backends return `unsupported-capability`, and a failed job start
marks the media failed immediately, releasing storage only after confirming files
are absent. Chunk routes set their request-body limit from the accepted upload's
chunk size (the host-config validator permits 1–64 MiB). Chunks are audited to
satisfy this delivery's explicit requirement to audit every mutation.

Child-deletion integration must check that dedicated media belongs to the child's
owner (or was selected by an administrator): a `dedicatedTo` name can refer to a
future VM and is not proof of ownership. The integrator also owns the shared
JobReader authorization and `/jobs/{id}/cancel` routes; this branch records job
initiators and honors runner cancellation without editing those shared routes.

### Stage 2 child VM driver and jobs

`POST /api/v1/vms/{parent}/children` accepts explicit `cpus`, `ramMb`, `diskGb`,
`lifetime` and ready media ids. Optional `windows`/`linux` presets supply firmware
hints only; `start:false` leaves hardware Off with an inactive lease. Owner/admin
access uses the foundation's `ParentDelegate` policy hook; primary-token create
access remains deferred to the delegation stage. Existing child list/read and
capability routes project the new records. `DELETE /vms/{child}` runs `child-delete`.
Primary creation and ISO patching are unchanged.

Admission uses `IAdmissionStore`: the queued job, child record, media references,
operation key and capacity reservations commit together. The job runner owns the
maintenance handle from before admission through terminal completion. Phases are
persisted and streamed as `phase` SSE events alongside existing progress/state
messages. `X-Construct-Operation-Key` (or request `operationKey`) supports replay;
a conflicting fingerprint is refused. Migration 400 adds the durable key store
and exposes `InsertInTransaction` for the integrator's SQLite admission store.

`ChildCreateJob` validates hardware before allocation, resolves ready media,
creates hardware/disk/DVDs through `IChildVmDriver`, verifies the immutable id and
attached media, persists a derived start intent before boot, confirms Running,
activates the lease from the original intent timestamp and
confirms reservations. The result contains boot/state/network facts and no guest
installation claim or credential. Guest addresses remain unverified. Failures
attempt rollback; unverifiable ownership and incomplete cleanup retain the VM row
and capacity for recovery. `ChildDeleteJob` removes forwards/network intent,
references, dedicated auxiliary media and capacity only after successful cleanup.

`HyperVChildDriver` invokes the optional PowerShell contract through fixed argv
and a JSON stdin descriptor. Dependency stdout/stderr and exception details are
never forwarded to logs or job progress. The driver resolves Hyper-V's default
storage before admission when `VmStorageRoot` is empty. It writes an exclusive
ownership sidecar at the canonical configured/default VHD location for partial-create
and partial-delete retries, including when the descriptor overrides the disk path.

Integration boundary: this branch does not implement the separately owned SQLite
admission transaction, media registry/transfer, capacity inventory/reconciliation,
or guest-address adapters. The in-memory admission path exercises jobs against
those seams. Production admission still refuses until the integrator wires the
owning pairs; `children` is therefore not yet advertised in discovery. Lease
scheduling, cascade deletion and delegated creation remain stage 3 work. Linux
recording-runner tests establish command construction, not Hyper-V execution.

Probe attempts on 2026-09-07: the relay returned Windows PowerShell
5.1.26100.9168. Initial attempts failed to start because of host thread/paging-file
resource exhaustion. A later read-only check reported 6,598,504 KiB free physical
memory, and the latest driver probe reached `New-VM`, which threw
`System.OutOfMemoryException`. Its `finally` removed the ownership marker and
probe files and reported `CLEANUP COMPLETE`, including a VM absence check.
Storage placement ran, but no successful child creation, firmware, TPM, media,
boot order or shutdown validation is claimed. A subsequent read-only inventory
query successfully exercised `Get-VM`, `Get-VMHardDiskDrive -VM`, `Get-VHD -Path`
and `Get-VMNetworkAdapter -VM`, including sizes, disk paths and reported addresses.
It also confirmed zero probe VMs and absence of the probe directory, disk and marker. No service or host
settings were changed. The stage-0 feasibility report is separate prior evidence.

Stage 2 Linux verification: solution build **0 warnings, 0 errors**; **748 .NET tests**
(42 added to the integrated 706-test baseline); **22 Node suites / 4,686 checks**;
**17 PowerShell suites / 3,046 checks or scenario groups** (child driver contract 131,
host installer 368); **19 Bash suites / 801 checks**, including contract parity (5)
and fake-service end-to-end (38). The Node root-only unwritable-spool check and
PowerShell Windows-only DPAPI check are skipped. The end-to-end test used the existing
build and stopped its test service. Local primary-driver source remains byte-identical.

Reproducing this matrix on this Linux VM requires the suite environment used by
this run: `T3CODE_BUILD_SOURCE=prebuilt` and
`SYSTEMD_UNIT_PATH=/usr/lib/systemd/system` for Bash, and an existing `/home/agent`
for `provision-diskcheck` (this run created it temporarily and removed it after
testing). Without those prerequisites, `idle-report` and `provision-diskcheck`
fail on both the baseline and this branch. Git-initializing tests use process-only
`GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=init.defaultBranch GIT_CONFIG_VALUE_0=main`.
The final child script also parsed on the actual Windows PowerShell
5.1.26100.9168 host with **zero parser errors**, using in-memory source only.
