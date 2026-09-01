# `constructd` — the Construct host service

The .NET service that runs on a remote Hyper-V host so several users can create and manage their own
VMs on it from the VS Code extension and the PowerShell scripts
(`docs/plans/modular-remote-architecture.md` §4.4, §4.6, §4.7).

**Status: contract-first scaffold (batch B6).** The API surface, the domain model, the internal
interfaces, the idle-policy engine and the durable SQLite stores are real and covered by tests.
Everything that has to touch Windows — the Hyper-V driver, the WSL ISO build, `netsh` port forwards —
exists only as an interface plus an in-memory fake, so the whole HTTP surface can be developed and
tested on Linux. The follow-up batches implement those interfaces without touching the API layer.

## Layout

```
service/
  Constructd.sln
  src/Constructd.Core/      domain records, interfaces, pure logic; zero package references
    Domain/                 User, ApiToken, Vm, PortForward, IdlePolicy, ActivityReport, Job,
                            AuditEntry, VmDescriptor, Endpoint
    Abstractions/           IHypervisorDriver, IIsoBuilder, IJobEngine, IJobStore,
                            IPortForwardManager, IForwardStore, IIdlePolicyEngine, IUserStore,
                            ITokenService, IVmRepository, IAuditLog, IClock
    Logic/                  PortAllocator, TokenHasher, VmNameValidator, Ownership,
                            IdleEvaluator, IdlePolicyRules
    Services/               IdlePolicyEngine, InProcessJobEngine — platform-agnostic, so these are
                            the real implementations in every mode
    Configuration/          ConstructdOptions and friends
  src/Constructd.Sqlite/    durable stores: hand-written SQL over Microsoft.Data.Sqlite, no ORM
  src/Constructd.Api/       ASP.NET Core minimal API host
    Program.cs              composition root, TLS, Windows-service hook
    Auth/                   schemes (Bearer, VmToken, Negotiate seam, test identity), policies
    Endpoints/              one file per area of the API
    Jobs/                   the VM create/remove workflows the job engine runs
    Hosting/                the once-a-minute idle scheduler
    Infrastructure/         JSON contract, problem details, centralized auditing
    Contracts/              request/response DTOs — the wire contract
  src/Constructd.Fakes/     in-memory implementation of every interface
  tests/Constructd.Tests/   xunit: Core unit tests, SQLite persistence tests, API integration tests
```

`Constructd.Sqlite` is the one project beyond the four the batch brief sketched: persistence is
cross-platform (so it belongs in this batch, not in a Windows follow-up), Core must stay free of
package references, and hand-written SQL does not belong in the HTTP host.

## Build, test, run

```bash
dotnet build service/Constructd.sln            # 0 warnings, 0 errors
dotnet test  service/Constructd.sln            # 223 tests

# run the whole API against the fakes (no Hyper-V, no Windows):
dotnet run --project service/src/Constructd.Api -- --fake
```

`--fake` is shorthand for `Constructd:Fake=true`: the hypervisor driver, the ISO builder and the
port-forward manager become in-memory fakes, persistence defaults to memory, and the `TestIdentity`
authentication scheme is enabled — it trusts an `X-Constructd-Test-Identity` header and stands in for
Negotiate. **Never run fake mode on a real host**; the service logs a warning at startup when it is
on. Fake mode can be combined with `Constructd:Persistence=Sqlite` (fake hypervisor, real database),
which is how the persistence tests drive the API.

Without fake mode the service currently refuses to start, naming the hypervisor implementations that
are still missing. That is deliberate: a half-wired host is worse than one that will not boot. The
SQLite stores are real in both modes.

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
| `POST /users` | admin | Creates a user `{name, role, maxVms, allowHostForwards?}`. There is no self-registration. |
| `DELETE /users/{name}` | admin | Removes a user and revokes their tokens. Refused while they still own VMs, and for the caller's own account. |
| `POST /users/{name}/tokens` | admin | Issues an API token `{label}`; the plaintext is in the response **once** and is never stored or logged. |
| `GET /audit` | admin | Audit trail, newest first, `?limit=`. |
| `GET /vms` | user | The caller's VMs; all of them for an admin. |
| `POST /vms` | user | `{name, cpu, ramGb, diskGb, opts:{nested?, automaticCheckpoints?, idlePolicy?}}` → `202 {jobId}`. Name uniqueness and the quota are enforced by the insert itself. |
| `GET /vms/{name}` | owner/admin | The VM including its forwards. Never exposes the VM token hash. |
| `DELETE /vms/{name}` | owner/admin | → `202 {jobId}`; accepting it fences the VM (see below) and the job removes the VM, its forwards and its SSH port. |
| `POST /vms/{name}/power` | owner/admin | `{action: start\|stop\|save}`, synchronous, returns the new state. `save` needs the driver's suspend capability. |
| `GET /vms/{name}/state` | owner/admin | Live state from the driver (and refreshes the registry). |
| `GET /vms/{name}/endpoint` | owner/admin | `{sshHost, sshPort}` — the service host plus the allocated forward. Until that forward exists the call answers `409`: the VM sits on an internal NAT switch and has no client-dialable address yet. |
| `GET /vms/{name}/forwards` | owner/admin **or that VM's own token** | Lists forwards. |
| `POST /vms/{name}/forwards` | owner/admin **or that VM's own token** | `{vmPort, label, target}` → `{id, publicPort?, url?}`. `target` defaults to `client`. |
| `DELETE /vms/{name}/forwards/{id}` | owner/admin **or that VM's own token** | Removes one forward. |
| `GET`/`PUT /vms/{name}/idle-policy` | owner/admin | `{timeoutMinutes, action}`; the response also carries the admin cap and whether the request was clamped. |
| `POST /vms/{name}/activity` | owner/admin **or that VM's own token** | Guest heartbeat `{busy, reasons[]}`. |
| `GET /jobs/{id}` | job submitter/admin | Job state, progress lines, result, error. The first retrieval of a succeeded creation job also gets `result.vmToken`. |
| `GET /jobs/{id}/events` | job submitter/admin | `text/event-stream`. |

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
       "result":{"name":"work-vm","endpoint":{"sshHost":"…","sshPort":2201},"vmToken":"<once>"}}
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
client acknowledges it, so a response lost in transit loses the token. That is deliberate (nothing
about the delivery is retriable without weakening "once"); the recovery path is to issue a new VM
token, which invalidates the previous one.

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
own heartbeat. Every other route — including `/whoami` — answers `403` for it, and so does any other
VM's copy of those four routes, even one owned by the same user.

A VM that exists but belongs to somebody else answers `403`, not `404`; an unknown VM answers `404`.

**Deleting is fenced.** Accepting `DELETE /vms/{name}` immediately marks the VM `deleting` and revokes
its scoped token in the same write; if the removal job then cannot be queued, that write is rolled back
so the VM is operable again (fence cleared, token intact) rather than fenced forever with no job to
finish the job. From that moment the guest cannot authenticate at all (`401`) and
every mutation of that VM — a new forward, a power change, a policy edit, a heartbeat — answers `409`;
reads still work and report `deleting: true`. The forward manager re-checks the same fact inside the
per-VM gate it holds while tearing forwards down, so nothing can be attached behind the job's back and
survive the VM (an orphan forward would otherwise be re-materialized at the next startup).

Host-target forwards are gated on the **VM owner's** `AllowHostForwards` flag, not the caller's, so
an admin acting on someone else's VM cannot route around that restriction.

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
switched off in tests, which call the engine directly).

Users set their own VM's policy; the admin sets the service-wide default and an optional cap. The cap
is applied when a policy is stored **and** when it is evaluated, so lowering it also affects VMs
configured earlier.

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
| `ScriptsDir` | – | The Construct checkout the service invokes (ISO build, `Create-AgentVM.ps1`). |
| `WslDistro` | `Ubuntu` | WSL distro used for the ISO build. |
| `PublicHost` | `localhost` | LAN name/IP that endpoints and forwards are advertised on. |
| `SshForwardPorts:Start` / `:End` | `2201` / `2299` | Per-VM SSH forward range. |
| `AppForwardPorts:Start` / `:End` | `2300` / `2999` | Range for `construct expose --to host`. |
| `MaxForwardsPerVm` | `16` | Cap on extra forwards per VM, enforced inside the forward manager. |
| `VmReachableTimeoutMinutes` | `30` | How long creation waits for SSH. |
| `AuditQueryLimit` | `200` | Default page size of `GET /audit`. |
| `Idle:SchedulerEnabled` | `true` | Run the background evaluator. |
| `Idle:TickSeconds` | `60` | Evaluation interval. |
| `Idle:DefaultTimeoutMinutes` | `120` | Policy a new VM gets. |
| `Idle:DefaultAction` | `Save` | `Save`, `Shutdown` or `Off`. |
| `Idle:MaxTimeoutMinutes` | `0` (no cap) | Admin cap on user-chosen timeouts. |
| `Idle:ForceEnabled` | `false` | With a cap set, also forbid switching idling off. |
| `Idle:ReportIntervalMinutes` | `5` | Interval the guest reporter posts at. |
| `Idle:MissingReportGraceMultiple` | `3` | Intervals of silence before the guest counts as idle. |
| `Iso:SeedUser` | `construct` | Seed user of the unattended install. |
| `Iso:BootstrapPublicKeyPath` | – | Bootstrap key injected into the ISO. |
| `BootstrapAdmin` | – | Identity seeded as the first admin when the user store is empty. |
| `BootstrapAdminMaxVms` | `10` | Quota for that admin. |
| `BootstrapAdminToken` | – | Optional plaintext token for the bootstrap admin (hashed at startup). Only for hosts that cannot use Negotiate; remove it once a real token has been issued. |

## Persistence

One SQLite file, hand-written SQL, no ORM and no migration machinery yet (the schema is created if
missing; evolving it is a deliberate decision to make when the first change comes). Tables: `users`,
`tokens`, `vms`, `activity`, `forwards`, `audit`, `jobs`. Name columns are `COLLATE NOCASE`, because identities
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

## How the real implementations plug in

Every platform-specific concern is one interface in `Constructd.Core/Abstractions`, registered in
exactly one place — `Composition/ServiceComposition.cs`, which has two independent axes: persistence
(SQLite or memory) and platform (fakes or Windows). `AddPlatformImplementations` currently throws with
a message listing what is missing; filling it in is the whole integration.

| Interface | Real implementation (batch) |
|---|---|
| `IHypervisorDriver` | PowerShell/Hyper-V driver invoking `Create-AgentVM.ps1` and the Hyper-V cmdlets (B7). Mirrors the PowerShell driver contract of plan §4.2; `docs/drivers.md`, which writes that contract down, lands with the driver extraction (B4). A future Proxmox driver maps the same operations onto its REST API. |
| `IIsoBuilder` | `wsl.exe` running the existing `bin/build-autoinstall-iso.sh` with its `VM_USER`/`VM_PASS`/`VM_HOST` env contract (B7). |
| `IPortForwardManager` | `netsh interface portproxy` rules over the existing `IForwardStore`, reconciled at startup, plus connection counting from the host TCP table for the idle signal (B7/B8). |
| `IUserStore`, `IVmRepository`, `ITokenService`, `IAuditLog`, `IJobStore`, `IForwardStore` | Done: `Constructd.Sqlite`. |
| Negotiate | Done: `builder.AddNegotiate()` inside the `OperatingSystem.IsWindows()` guard in `Auth/AuthenticationSetup.cs`. |
| Windows service | Done: `builder.Host.UseWindowsService()` in `Program.cs`, under the same guard. |

Nothing above those interfaces knows about Hyper-V, PowerShell, netsh or Windows, so those batches
change no endpoint, policy, job or test.

## Tests

`dotnet test service/Constructd.sln` — 223 tests, all running on Linux.

- **Core unit tests**: port allocation (lowest-free, no double allocation, release, exhaustion,
  reservation, concurrency), token hashing (format pinned to a known SHA-256 vector, fixed-time
  comparison), VM name validation, ownership and quota rules, the idle decision matrix including the
  grace-window cases (stale heartbeat, never-reported VM, timeout shorter than the grace), cap
  clamping, and the idle engine against the fake driver.
- **Job engine tests**: a store that holds a progress write until completion starts, proving the
  terminal snapshot is what the store ends up with; a store that holds the *terminal* write, proving
  that no terminal state, no state event and no one-time token is observable before it lands; a store
  failure surfacing on the job (as a type, not a message); a job from before a restart still being
  readable and streamable; cancellation.
- **Persistence tests**: every store against a real SQLite file that is then reopened ("restart"),
  the quota transaction, job recovery after a restart, no plaintext secret anywhere in the file,
  forwards (and their ports) surviving a restart with reconciliation re-materializing the host rules
  and no port colliding afterwards, an SSH allocation that is durable before its host rule exists (so a
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
  VM-token scoping, caps, port exhaustion), concurrency (quota and forward caps under simultaneous
  requests), idle policy with cap clamping, the heartbeat, and audit entries for successes, validation
  failures, refusals, malformed bodies and a throwing hypervisor.
- **Secret-hygiene tests**: a dependency exception carrying a sentinel secret, raised in the request
  path, in a background job, in the idle engine, in the forward manager, and in token validation (where
  the sentinel is the presented plaintext token) — asserted absent from the response body, the audit
  trail, job state, the SSE stream and every captured log entry, while the service's own error messages
  still come through in full.

## Open points for the follow-up batches

- `Job` carries an `Owner` and `IJobEngine.SubmitAsync` takes it — not in the plan's sketch, but job
  authorization cannot be derived from the VM record: a delete job outlives it, and a create job hands
  out a secret.
- The idle engine's per-VM "last active" watermark is in-process: a restart restarts the idle window
  rather than idling VMs out on history it cannot see. Persisting it alongside the VM row is a small
  follow-up if that matters.
- `client`-target forwards are recorded (and durable) but not relayed yet; the extension side of that
  is B8.
- The `url` on a forward is advisory (`http://<publicHost>:<port>/`). Per-VM hostnames for
  cookie-sensitive services stay out of scope (plan §4.9).
- Quota semantics: `maxVms` is a plain cap and `0` means "may not create VMs"; "unlimited" has to be
  expressed as a large number.
- No schema migrations yet — the first schema change has to introduce them.
