# Host administration contracts (Phase 0 design amendment)

Status: **frozen contract** for the host-administration and child-VM delivery.
Date: 2026-09-07 (revision 10 after review). Branch: `ha/s0-contracts`.
Inputs: [requirements](host-administration-and-child-vms.md),
[implementation plan](host-administration-implementation.md),
[Hyper-V feasibility report](host-administration-hyperv-feasibility.md) (lands from branch
`ha/s0-feasibility`; the link resolves once both branches are merged), the current service
(`service/src`), the PowerShell driver contract ([docs/drivers.md](../drivers.md)), the
remote host guide ([docs/remote-host.md](../remote-host.md)) and the expose contract
([docs/expose.md](../expose.md)).

This document is what every implementation pair builds against. Where it and the
requirements differ, the requirements win on product intent and this document wins on
names, shapes, codes and ownership. Where the requirements left a technical choice open,
the choice is made here and marked **decided here** with one line of rationale; §0.1
lists every such choice in one place. Nothing in this document exists in code yet unless
it says "existing". The stage-1 seam signatures of §13.1 are compiled against the real
`Constructd.Core` by `test/contracts-compile.test.sh` (documentation-only check; no
production behaviour is added by it).

Regression boundary, restated because every section depends on it: the local single-VM
install, the existing primary flows (`Auto-Install.ps1` remote path, `Provision-AgentVM.ps1`,
`construct expose`, idle policy, forwards, the four VM-token routes) and every existing
token behave identically after this delivery. Every schema change is additive with a
default that reproduces today's behaviour; every new API field is optional or nullable
(and omitted from legacy-shaped answers where a nested object would break an existing
parser, §8.11); every new route is new. The Linux baseline suites (638 dotnet tests, node,
pwsh, bash, fake end-to-end) prove that nothing existing changed; they prove nothing about
the new host behaviour, which is validated only by the field test of §14.3.

## 0. Decisions of 2026-09-07 (recorded, not reopened)

| Id | Decision | Effect in this contract |
|---|---|---|
| D1 | Lease expiry = graceful guest shutdown, same as the user-panel stop. Failure or an unsupported guest shutdown is reported as such. No force-off, no save fallback, never deletion at expiry. | §5.5, §7 job `vm-shutdown`, error `guest-shutdown-unavailable`, inventory flag `lease.overdue`. |
| D2 | Console = screenshot + keyboard + mouse (absolute move/click; relative fallback where the synthetic mouse is unavailable), service-side through Hyper-V WMI as far as the feasibility report proves. Interactive video (VMConnect/RDP) is unsupported and reported so by the capability model. | §3 console capabilities, §8.12 console routes, §13.1 `IConsoleTransport`. |
| D3 | Real-host deployment and validation are deferred: this run validates on Linux (fakes, `--fake` API, pwsh, node, bash); host access is limited to relay probes. A field-test checklist is written for a later run by the owner. | §14.3; every "verified on Hyper-V" claim is forbidden in this run. |
| D4 | Credential upgrade for existing primaries: owner/admin token rotation `POST /vms/{name}/token` invalidating the previous hash; token kinds `legacy` (today's four routes) and `primary` (own routes plus delegation). Reprovision injects the rotated token. | §1.6, §2, §8.13, §9.2 (guest discovery via `GET /vms/{name}/identity`). |
| D5 | Host release distribution: GitHub Releases on `permissionBRICK/The-Construct` from a new Actions workflow on `main` (self-contained win-x64 service package + matching scripts + `manifest.json` with commit, hashes, schema/config compatibility). The updater pins the resolved commit. The ISO tool keeps its own pinned release (`config/iso-builder.json`, `docs/native-iso.md`). | §11. |
| D6 | Graceful shutdown timeout default 300 s, configurable per host; a structured error when integration services are absent. | `HostConfig.lifecycle.gracefulShutdownTimeoutSeconds`, error `guest-shutdown-unavailable`. |

### 0.1 Choices made in this document (not in the requirements)

Every row is a new choice; each states its rationale and its effect on existing installs.

| Choice | Value | Rationale | Compatibility effect |
|---|---|---|---|
| Capacity enforcement mode (§4.3) | `capacity.mode`: `observe` when no `host_config.capacity` row exists (every migrated host), `enforce` written by the installer on fresh hosts and settable by admins | zero-change: a migrated host must not start refusing `POST /vms` that Hyper-V used to accept; the ledger still records everything so the admin sees what enforcement would do | none until an admin switches to `enforce` |
| RAM headroom default | `max(4 GiB, 12.5 % of physical RAM)` | the host OS plus Hyper-V root partition need a floor; the probe host (32 GiB) had ~6 GiB free with one 16 GiB VM | applies only in `enforce` |
| Storage headroom default | 20 GiB per volume | saved-state files, ISO transfers and Windows updates need slack the ledger does not model | applies only in `enforce` |
| User RAM/storage budget `null` | "no user budget; host capacity still binds" | the requirements forbid a guessed numeric default | none |
| Finite lifetime bounds | minimum `5m`; **no maximum** other than policy (`maxChildLifetimeSeconds`) | a lease shorter than the scheduler tick is meaningless; a hard upper bound would contradict "unlimited by default" | none |
| Minimum child RAM | 512 MiB, multiple of 2 MiB | Hyper-V's own alignment; the feasibility probe booted at 512 MiB | none |
| Firmware presets (§8.6.1) | `windows` / `linux` template + TPM defaults only | the feasibility report proved exactly those two templates | none |
| Console caps | 4 MiB per screenshot, native resolution only, 4 sessions per VM, 4 screenshots/s and 50 input events/s per session | bounded host work per tenant; the probe showed >native requests fail and native 1024×768 is 1.5 MiB raw | none |
| Media caps | 16 GiB per item, 20 items per user, 8 MiB chunks, 24 h upload TTL, 180 min acquire timeout, `http` only with checksum | disk-bounded tenants; Windows ISOs are ~6 GiB | none |
| Anonymous `GET /health` with a reduced body (§8.1) | status, `apiFeatures`, `schemaVersion`, `maintenance` — **no commit, no package version** without a credential | the updater and old-service detection need an unauthenticated probe; version details stay behind authentication | first anonymous route of the service |
| Lease renewal route, no keepalive (§5.3) | explicit owner `POST /vms/{child}/lease` | wall-clock lease by design; "stop and start again" as the only extension would break long tests | none |
| Children skip the idle engine | `IdlePolicyEngine` ignores `kind=child` | children have no heartbeat credential; the lease is their lifetime | none |
| Generated child names | `<parent>-<4 hex>` derived from the operation key | one-shot CLI while keeping the flat namespace and stable replay | none |
| `create-vm` issues `primary`-kind tokens for new primaries | — | the requirements say primaries inherit delegation without an admin step; only pre-existing rows stay `legacy` | new VMs only |
| Explicit deletion may power a child off (§8.8) | `Stop-VM -TurnOff` inside `child-delete` | deletion is destructive by request; D1 governs expiry and Shut down, not delete | none |
| Shared client forwards ride the **requester's** primary (§8.11) | `via` = requester's primary | host sharing grants no SSH into the owner's primary | none |
| Guest addresses are untrusted until verified (§12.5) | subnet + adapter-association checks; host exposure refused when unverifiable | KVP values come from an arbitrary guest OS | none |
| Updater hand-off by one-shot SYSTEM scheduled task (§11.5) | `schtasks` | a child of a stopping service is not guaranteed to survive; a task is | none |
| Manifest signing key (§11.3) | Ed25519 private key in the `host-release` GitHub environment secret; public key in `config/host-release.pub` and in host config | a signed manifest is the trust root the requirements ask for | the installer seeds the key |
| `constructd admin db check` (§11.6) | new admin verb | the health check needs a real database open, not a config print | none |
| ISO-catalog reference counting deferred (§6.6) | catalog keeps `current.pointer` + prune | the catalog is the primary path; touching its retention is out of the zero-change bar | recorded as a limitation |
| Dynamic memory seam | `ChildHardware.DynamicMemory` reserved, refused by every current backend | keeps the future Proxmox/ballooning input without promising it | none |
| Allowance resolution order (§1.1) | resolve user vs default, cap by `userCaps`, restrict by override | `null = inherit` must let an explicit user value win over a default; host-wide hard caps are a separate section | none (all caps default null) |
| One atomic admission seam (§7.3) | `IAdmissionStore.AdmitAsync(plan)` | key, rows, reservations and the queued job must commit together or not at all | none |
| Cross-process locks (§7.4, §11.7) | `admin.lock` for mutating admin CLI work (released by the service at hand-off, taken by the updater before stop), `updater.lock` + `fence.json` for the updater; read-only verbs lock nothing | an out-of-process operation cannot be drained by a flag; a slow updater must not be able to roll back after writes reopen | admin CLI waits up to 30 s during an update |
| Recovery-route exemption (§7.4) | only `POST /host/updates/resolve` and the resume form of `POST /host/updates/apply`, admin-only, marked `MaintenanceExempt` | the freeze must not lock out the one action that lifts it; no general admin bypass | none |
| Synchronous replay via intents (§7.3) | DB-only mutations commit with their key; `start` records an `InFlight` intent and reconciles on replay | "idempotent on state" was false for renew/sharing/start | none |
| Config compatibility in the manifest (§11.3) | additive settings only; `requiredKeys` refusal; the file is never rewritten | D5 promised schema **and** config compatibility | none |
| Update health handshake (§8.1, §11.5) | loopback `Authorization: UpdateHandoff <token>` from the SYSTEM-only hand-off file | the updater must verify the commit without a retained admin secret | none |
| Child destination addresses are unverifiable on Hyper-V (§12.5) | host forwards to children refused; client tunnels carry `verified: false` | MAC spoofing protection limits MACs, not IP claims; no IP allocation authority exists on the standard switch | host forwards of primaries unchanged; child host exposure deferred |

## Terminology

| Term | Meaning |
|---|---|
| **primary** | A Construct VM created through the existing `POST /vms` path. Owned by a user. Existing rows all become primaries. |
| **child** | A general-purpose ISO-booted VM created by `POST /vms/{parent}/children`. Exactly one parent (a primary on the same host); its owner and quota payer is the parent's owner. |
| **owner** | The user record a VM is charged to. Never a VM. |
| **effective owner** | The user a shared or delegated operation is charged to and audited against: always the child's owner. |
| **initiator** | The principal that submitted a job or request: a user, or `vm:<primary>` for a primary token. Stored separately from the owner. |
| **primary token** | The VM-scoped token of a primary whose `tokenKind` is `primary`. |
| **legacy VM token** | A VM-scoped token issued before this delivery, or rotated with `kind=legacy`. |
| **shared caller** | An enrolled user, or a primary token of kind `primary`, acting on a child whose sharing scope is `host` and whose owner is somebody else. |
| **child guest** | Whatever runs inside a child. It holds no Construct credential of any kind. |
| **incarnation** | The hypervisor's immutable VM id, stored on the record at creation; a re-created VM with the same name has a different incarnation. |
| **operation** | One state-changing unit of work with an id (a job id, or a request id for synchronous mutations); the owner of any pending reservation and of the per-VM gate while it runs. |
| **stage** | A delivery increment of §14; stage 1 is the seams stage that lands before the pairs go parallel. |

## 1. Domain and persistence

### 1.1 Record changes (Constructd.Core.Domain)

All additions are new optional record parameters with **compile-time-constant defaults**
(`null`, `false`, enum members), so every existing constructor call, fake and test compiles
unchanged. These are edits to existing files (integrator-owned); the new records and
enums are in the compiled block of §13.1.

| Record | Change | Notes |
|---|---|---|
| `Vm` | `+ long PowerGeneration = 0` (§5.3b, read projection of `power_generation`), `+ VmKind Kind = VmKind.Primary`, `+ string? Parent = null`, `+ SharingScope Sharing = SharingScope.Private`, `+ VmTokenKind TokenKind = VmTokenKind.Legacy`, `+ int? RamMb = null`, `+ string? Incarnation = null`, `+ Lease? Lease = null`, `+ ChildHardware? Hardware = null`, `+ GuestReport? Guest = null`, `+ HostObservation? Observed = null`, `+ bool ChildCreationClosed = false`, `+ string? CurrentJobId = null` | `RamBytes` computed property: `RamMb is int mb ? mb * 1 MiB : RamGb * 1 GiB`. Children always carry `RamMb`, `Incarnation`, `Lease`, `Hardware`. `Parent` is null for primaries and never null for children. `Incarnation` of an existing primary is filled lazily by the first inventory pass. |
| `User` | `+ bool Enabled = true`, `+ UserAllowance? Allowance = null` | `null` means `UserAllowance.Unset` (every field null = host `userDefaults`). `MaxVms` keeps its meaning and becomes the **primary count** quota. `AllowHostForwards` unchanged. |
| `Job` | `+ string? Initiator = null`, `+ string? OperationKey = null`, `+ string? Phase = null` | `Owner` stays the effective owner user and keeps authorizing reads as today. `Initiator` (user name or `vm:<name>`) is who submitted it; `null` (every existing job) means "the owner". |
| `PortForward` | `+ ForwardDestination? Destination = null` | The existing `ForwardTarget Target` (`client`/`host`) is **untouched**. `Destination` is null for every forward that exists today and for every forward whose target is a **primary**, so the legacy wire shape is byte-identical (§8.11); only child-target forwards carry it. |
| `AuditEntry` | no schema change | Detail format standardized, §1.8. |
| `VmDescriptor`, `Endpoint`, `IdlePolicy`, `ActivityReport`, `ApiToken` | unchanged | Children use `ChildVmDescriptor` (§13.1). |

**Allowance resolution and per-VM overrides (decided here: resolve, cap, then
restrict).** For a user U and an action delegated through primary P:

1. **Resolve** each field of `UserAllowance` against its fallback:
   `resolved(U).f = allowance(U).f ?? userDefaults.f` — an explicit user value always wins
   over the host default (`null` = inherit, exactly as `UserAllowance` says).
2. **Cap** with the host-wide hard ceilings `host_config.userCaps` (§1.5; every field
   optional, null = no cap): `capped(U).f = restrict(resolved(U).f, userCaps.f)`, where
   `restrict` takes the smaller number and the conjunction of booleans.
3. **Restrict** with the primary's override: `effective(U, P).f = restrict(override(P).f,
   capped(U).f)`. `VmOverride` carries the delegation switches only
   (`AllowChildCreation`, `MaxRetainedChildren`, `MaxChildLifetimeSeconds`,
   `AllowNeverLifetime`, `AllowSharing`) and can only tighten.

The user's aggregate counts (retained children across all primaries, CPU/RAM/storage) are
always checked against `capped(U)`, never against an override, so no override can raise
a ceiling above the user, and the host caps bound every user. Examples (`userDefaults.
maxRetainedChildren = 1`, `userCaps.maxRetainedChildren = null`):

| `allowance(U).maxRetainedChildren` | `userCaps` | `override(P)` | effective for P | aggregate check for U |
|---|---|---|---|---|
| null | null | null | 1 | 1 |
| 2 | null | null | 2 | 2 |
| 2 | 3 | null | 2 | 2 |
| 5 | 3 | null | 3 | 3 |
| 2 | null | 1 | 1 | 2 |
| `allowChildCreation = true` with default `false` | – | – | true | – |
| `allowChildCreation = null` with default `false` | – | `true` | false (an override cannot enable) | – |

Rationale: an override that could raise a budget per VM would reintroduce the per-VM admin
step the requirements reject; exceptions are made by editing the user allowance.

### 1.2 SQLite schema additions

All columns are nullable or carry a default that reproduces today's behaviour. Existing
rows are never rewritten by a migration; `SqliteVmRepository` keeps reading `SELECT *` and
gains the new columns through the reader.

| Table | Column / change | Type and default | Migration |
|---|---|---|---|
| `vms` | `kind` | `TEXT NOT NULL DEFAULT 'primary'` | 100 |
| `vms` | `parent` | `TEXT NULL COLLATE NOCASE` | 100 |
| `vms` | `sharing` | `TEXT NOT NULL DEFAULT 'private'` | 100 |
| `vms` | `vm_token_kind` | `TEXT NOT NULL DEFAULT 'legacy'` | 100 |
| `vms` | `ram_mb` | `INTEGER NULL` | 100 |
| `vms` | `incarnation` | `TEXT NULL` | 100 |
| `vms` | `child_creation_closed` | `INTEGER NOT NULL DEFAULT 0` | 100 |
| `vms` | `current_job_id` | `TEXT NULL` | 100 |
| `vms` | `lease_requested_text`, `lease_requested_seconds`, `lease_activated_at`, `lease_expires_at`, `lease_state`, `lease_version`, `lease_last_attempt_at`, `lease_last_outcome` | `TEXT NULL`, `INTEGER NULL`, `TEXT NULL`, `TEXT NULL`, `TEXT NULL`, `INTEGER NOT NULL DEFAULT 0`, `TEXT NULL`, `TEXT NULL` | 100 |
| `vms` | `hardware_json` | `TEXT NULL` (camelCase JSON of `ChildHardware`) | 100 |
| `vms` | `guest_construct_commit`, `guest_provisioned_at`, `guest_reinstalled_at`, `guest_reported_at`, `guest_provenance`, `guest_last_attempt_at`, `guest_last_attempt_outcome` | all `TEXT NULL`; `guest_provenance` default `'unknown'` | 100 |
| `vms` | `observed_created_at`, `observed_last_boot_at`, `observed_addresses_json`, `observed_storage_problem` | `TEXT NULL` | 100 |
| `vms` | index `ix_vms_parent ON vms (parent)`, `ix_vms_lease_due ON vms (lease_state, lease_expires_at)` | | 100 |
| `users` | `enabled` | `INTEGER NOT NULL DEFAULT 1` | 100 |
| `users` | `allow_child_creation`, `max_retained_children`, `cpu_budget`, `ram_budget_bytes`, `storage_budget_bytes`, `max_child_lifetime_seconds`, `allow_never_lifetime`, `allow_sharing` | all `INTEGER NULL` | 100 |
| `vm_overrides` (new) | `vm_name TEXT PRIMARY KEY COLLATE NOCASE`, five nullable INTEGER columns as in `VmOverride`, `updated_at TEXT NOT NULL` | | 100 |
| `cascades` (new) | `parent TEXT PRIMARY KEY COLLATE NOCASE`, `parent_incarnation TEXT NOT NULL`, `token TEXT NOT NULL`, `issued_at TEXT NOT NULL`, `expires_at TEXT NOT NULL`, `children_json TEXT NOT NULL`, `state TEXT NOT NULL`, `job_id TEXT NULL`, `outcomes_json TEXT NOT NULL` | §8.8 | 100 |
| `host_config` (new) | `key TEXT PRIMARY KEY`, `value_json TEXT NOT NULL`, `updated_at TEXT NOT NULL`, `updated_by TEXT NOT NULL` | one row per config section (§1.5) | 100 |
| `jobs` | `initiator TEXT NULL`, `operation_key TEXT NULL`, `phase TEXT NULL` | | 100 |
| `schema_migrations` (new) | `id INTEGER PRIMARY KEY`, `name TEXT NOT NULL`, `breaking INTEGER NOT NULL`, `applied_at TEXT NOT NULL`, `applied_by_commit TEXT NOT NULL` | | runner |
| `media`, `media_references`, `media_uploads` (new) | §6.1, §6.4 | | 200 |
| `reservations` (new) | §4.5 | | 300 |
| `job_operation_keys` (new) | `owner TEXT NOT NULL COLLATE NOCASE`, `kind TEXT NOT NULL`, `operation_key TEXT NOT NULL`, `fingerprint TEXT NOT NULL`, `target TEXT NOT NULL`, `job_id TEXT NULL`, `state TEXT NOT NULL DEFAULT 'completed'`, `intent_json TEXT NULL`, `power_generation INTEGER NULL`, `response_json TEXT NULL`, `created TEXT NOT NULL`, `completed_at TEXT NULL`, `PRIMARY KEY (owner, kind, operation_key)`, index `ix_opkeys_inflight ON job_operation_keys (state, target)` | §7.3 | 400 |
| `vms` | `power_generation INTEGER NOT NULL DEFAULT 0` | §5.3b | 100 |
| `host_updates` (new) | §11.8 | | 600 |
| `forwards` | `destination_vm TEXT NULL COLLATE NOCASE`, `destination_via TEXT NULL COLLATE NOCASE`, `destination_connect_address TEXT NULL`, `destination_connect_port INTEGER NULL`, `requested_by TEXT NULL`, `relationship TEXT NULL` | all NULL for today's rows and for every primary-target forward | 700 |
| `network_rules` (new) | §12.4 | | 700 |

### 1.3 Migration of existing rows

| Existing fact | After migration 100 |
|---|---|
| Every row in `vms` | `kind='primary'`, `parent=NULL`, `sharing='private'`, `incarnation=NULL` (filled by the first inventory pass), no lease, no hardware, `guest_provenance='unknown'` (guest facts are reported later by the guest, §8.14). Name, owner, CPU/RAM/disk, SSH forward port, idle policy and `deleting` are untouched. |
| `vm_token_hash` | Preserved byte-for-byte. `vm_token_kind='legacy'`: the hash keeps exactly today's four routes plus the two additive read/report routes of §1.6. |
| Every row in `users` | `enabled=1`; `max_vms` unchanged and now read as the primary-count quota; every allowance column NULL (= host defaults, §1.5). |
| Every row in `jobs` | `initiator=NULL` (= the owner), no operation key, no phase. |
| Tokens (`tokens` table), forwards, audit, activity | Untouched; new columns NULL. |

No data is copied, transformed or deleted. Migration 100 is a set of `ALTER TABLE … ADD
COLUMN` and `CREATE TABLE IF NOT EXISTS` statements in one transaction. The existing
quota SQL in `SqliteVmRepository.AddAsync` (`SELECT COUNT(*) FROM vms WHERE owner=@owner`)
gains `AND kind='primary'`: on a migrated database every row is a primary, so the count
is identical; on a database with children, retained children never consume primary slots.

### 1.4 Additive per-feature migration mechanism (decided here)

`SqliteDatabase.EnsureCreated()` keeps its base schema and its `AddColumnIfMissing`
calls (unchanged), then calls a new `SqliteMigrationRunner.Apply(connection, appCommit)`:

```csharp
namespace Constructd.Sqlite.Migrations;

public interface ISqliteMigration
{
    /// Unique, monotonically ordered. Ranges are reserved per pair (table below).
    int Id { get; }
    string Name { get; }
    /// True only when an OLDER binary can no longer read the migrated database.
    bool Breaking { get; }
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}

public static class SqliteMigrations
{
    // ONE line per feature file. Order is by Id, not by list position.
    public static IReadOnlyList<ISqliteMigration> All { get; } =
    [
        new M100_VmKindsAndDelegation(),
        new M200_MediaRegistry(),
        new M300_CapacityLedger(),
        new M400_JobOperationKeys(),
        new M600_HostUpdates(),
        new M700_NetworkPolicy(),
    ];

    public static int SchemaVersion => All.Max(m => m.Id);
    public static int MinReadableBy => All.Where(m => m.Breaking).Select(m => m.Id).DefaultIfEmpty(0).Max();
}
```

Rules:

| Rule | Detail |
|---|---|
| One file per feature | `service/src/Constructd.Sqlite/Migrations/M<id>_<Name>.cs`. A pair creates its own file and adds one line to `SqliteMigrations.All`. |
| Reserved id ranges | 100–199 integrator (delegation/sharing/lifecycle), 200–299 media, 300–399 capacity, 400–499 child jobs, 500–599 console (reserved, no migration expected), 600–699 host release/updater, 700–799 network. Later features continue at 800. |
| Applied order | Ascending `Id`. A migration may depend only on the base schema and on lower ids **from range 100** (the integrator's), never on another pair's range. Runtime dependencies across ranges are allowed and documented (the media pair's idempotent-replay test needs M400 merged, §14.1). |
| Idempotence | Each migration is recorded in `schema_migrations` and never re-run. The statements themselves must still be safe to re-run (`IF NOT EXISTS`, column probe) so a crash between statement and record is harmless. |
| Additive only | New tables, nullable columns or defaulted columns. Renames, drops and type changes are forbidden in this delivery; a migration needing them sets `Breaking = true` and is refused by review unless §11.7 rollback rules are updated with it. |
| Version exposure | `GET /health` reports `schemaVersion = SqliteMigrations.SchemaVersion` and `schemaMinReadableBy = SqliteMigrations.MinReadableBy`; the update manifest carries the same two numbers (§11.2). |
| Tests | `SqlitePersistenceTests` gains: fresh database applies all; a pre-feature database (fixture SQL of today's schema with rows) applies all and keeps every row; applying twice is a no-op; `All` ids are unique and sorted; every migration id sits in its owner's range. |

### 1.5 Host configuration (`host_config`) and defaults

Host policy lives in the database, not in `appsettings.Production.json`, because it is
edited through the API by admins and must survive the updater's config preservation
without merges (**decided here**; `appsettings` keeps only bootstrap/platform values).
One row per section, JSON value, camelCase. The records are `CapacityConfig`,
`UserDefaultsConfig`, `LifecycleConfig`, `MediaConfig`, `NetworkConfig`, `UpdatesConfig`
and `MaintenanceMarker` (§13.1).

| Key | Fields | Default (when the row is absent) |
|---|---|---|
| `capacity` | `mode: "observe"\|"enforce"`, `ramHeadroomBytes: long?`, `storageHeadroomBytes: long`, `cpuBudget: int?` (active vCPU cap, null = none), `maxVcpusPerVm: int?` (null = host logical CPUs), `reconcileSeconds: int`, `orphanReservationTimeoutSeconds: int` | `observe`; `ramHeadroomBytes = null` ⇒ `max(4 GiB, 12.5 % of total RAM)` at read; `storageHeadroomBytes = 20 GiB`; `cpuBudget = null`; `maxVcpusPerVm = null`; `reconcileSeconds = 60`; `orphanReservationTimeoutSeconds = 600` |
| `userDefaults` | `maxPrimaries: int`, `allowChildCreation: bool`, `maxRetainedChildren: int`, `cpuBudget: int?`, `ramBudgetBytes: long?`, `storageBudgetBytes: long?`, `maxChildLifetimeSeconds: long?`, `allowNeverLifetime: bool`, `allowSharing: bool` | `1, true, 1, null, null, null, null (unlimited), true, true` — exactly the requirements table. Fallbacks for `null` user fields (§1.1 step 1). `maxPrimaries` applies only to users created through the new admin shape (§8.4); the existing `POST /users` and admin CLI keep their explicit `maxVms`. |
| `userCaps` | `maxRetainedChildren: int?`, `cpuBudget: int?`, `ramBudgetBytes: long?`, `storageBudgetBytes: long?`, `maxChildLifetimeSeconds: long?`, `allowNeverLifetime: bool?`, `allowSharing: bool?` | all `null` (no host-wide hard ceilings). Applied after user resolution (§1.1 step 2). |
| `lifecycle` | `gracefulShutdownTimeoutSeconds: int`, `leaseTickSeconds: int`, `leaseRetrySeconds: int` | `300` (D6), `30`, `600` |
| `media` | `maxBytes: long`, `maxItemsPerUser: int`, `uploadChunkBytes: int`, `uploadTtlHours: int`, `acquireTimeoutMinutes: int`, `allowHttp: bool`, `unreferencedTtlHours: int?` | `16 GiB`, `20`, `8 MiB`, `24`, `180`, `true` (http only with a checksum, §6.3), `null` (unreferenced media is kept until its owner deletes it) |
| `network` | `hostForwardsEnabled: bool`, `directAddressReporting: bool` | `true`, `true` |
| `updates` | `repository: string`, `channel: string`, `drainTimeoutMinutes: int`, `healthTimeoutSeconds: int`, `requireSignature: bool`, `manifestPublicKey: string?` | `"permissionBRICK/The-Construct"`, `"main"`, `60`, `120`, `true`, `null` (no update can be verified until the installer or an admin sets it) |
| `maintenance` | `state`, `updateId?`, `since` — written by the service when it enters draining/maintenance, cleared on reopen | absent = open. The admin CLI reads it (§7.4). |

`ConstructdOptions` gains `HostAdmin` bootstrap values used only when a row is absent
(`Constructd:HostAdmin:Updates:ManifestPublicKey`, `Constructd:HostAdmin:Capacity:Mode`,
`Constructd:HostAdmin:Media:RootDir`), so the installer can seed them.

### 1.6 Token kinds (D4)

| Kind | Stored as | Issued by | Reach |
|---|---|---|---|
| `legacy` | `vms.vm_token_kind='legacy'` (migration default) | every token existing before migration 100; `POST /vms/{name}/token {kind:"legacy"}` | the four existing routes (`GET/POST/DELETE /vms/{self}/forwards…`, `POST /vms/{self}/activity`) plus two additive routes: `GET /vms/{self}/identity`, `POST /vms/{self}/guest-report`. Nothing else. |
| `primary` | `'primary'` | `create-vm` jobs after migration 100 (**decided here**: new primaries inherit delegation without an admin step, as the requirements demand); `POST /vms/{name}/token` default | legacy reach plus the delegated routes of §2, always re-evaluated against the owner's current policy. |

A child never has a token; `POST /vms/{child}/token` answers `409 child-has-no-token`.
The plaintext travels only in the rotation response and the one-time job channel.

### 1.7 Identity of a child in Hyper-V and on disk

Children are Hyper-V VMs named exactly like their registry record (flat namespace, same
`VmNameValidator` rule, unique per host). When the request omits `name`, the service
derives `<parent>-<first 4 hex of SHA-256(owner + ":" + operationKey)>` (**decided here**:
one-shot CLI, flat namespace, and a replay with the same key resolves to the same name).
Without an operation key the four hex digits are random. The VHDX is
`<VmStorageRoot or Hyper-V default>\<name>.vhdx`. Media files live under `Media:RootDir`
(§6.1), never in the ISO catalog directory. The child's `Incarnation` is the Hyper-V VM
GUID read back right after creation.

### 1.8 Audit detail format

No schema change. Every new mutating route and job writes detail as comma-separated
`key=value` tokens with these standard keys, in this order when present:
`op=<operation>`, `owner=<effective owner>`, `initiator=<user or vm:name>`,
`parent=<parent vm>`, `target=<target vm or media id>`, `job=<job id>`,
`key=<operation key>`, followed by route-specific tokens. Values are sanitized (control
characters stripped, 200 chars). Never a secret, a URL query string, a typed console
string or image bytes.

## 2. Permission matrix

### 2.1 Actors and how they authenticate

| Actor | Credential | Principal facts |
|---|---|---|
| **admin** | Negotiate or `Bearer` user token of a user with `Role.Admin`, `Enabled = true` | existing `Policies.Admin` |
| **user** | Negotiate or `Bearer` user token of a user with `Role.User`, `Enabled = true` | existing `Policies.User` |
| **primary token** | `VmToken` whose VM has `TokenKind.Primary` | new claim `constructd:vm-token-kind = primary`; effective owner = that VM's owner; initiator `vm:<name>` |
| **legacy VM token** | `VmToken` whose VM has `TokenKind.Legacy` | claim `constructd:vm-token-kind = legacy` |
| **shared caller** | a *user* or *primary token* acting on a child with `Sharing = Host` that is not owned by / parented under it | resolved per request, never a stored role |
| **child guest** | none | reaches no route at all |
| **anonymous** | none | `GET /health` only (reduced body, §8.1) |

New policies (constants in `Policies`): `UserOrPrimaryToken` (enrolled user, or VM token
of kind primary), `ChildOperator`, `ChildOwnerOrAdmin`, `ParentDelegate`,
`ForwardRequester`, `ConsoleOperator`, `JobReader`. The resource policies are handlers in
`Auth/DelegationAuthorization.cs` and `Auth/ForwardRequesterHandler.cs`.

### 2.2 Operations × actors

Legend: ✔ allowed; ✔ᵒ allowed only on own (owned, or parented under the token's VM);
✔ˢ allowed as shared caller on a host-shared child; ✔ᶦ allowed as the initiator of that
job/forward; ✗ refused (`403`); – route not reachable for that credential (`401`/`403`
before resource resolution).

| Operation (route) | admin | user | primary token | legacy VM token | shared caller | child guest |
|---|---|---|---|---|---|---|
| `GET /health` (reduced) | ✔ | ✔ | ✔ | ✔ | ✔ | (anonymous) |
| `GET /whoami` | ✔ | ✔ | – | – | – | – |
| `GET /host/capabilities` | ✔ | ✔ | ✔ | ✗ | – | – |
| `GET /host/status`, `/host/capacity`, `/host/iso-catalog`, `GET/PUT /host/config` | ✔ | ✗ | ✗ | ✗ | – | – |
| Users: list/read/update/allowance/tokens (§8.4) | ✔ | ✗ | ✗ | ✗ | – | – |
| `GET /vms` (own scope) | ✔ all | ✔ own | ✔ᵒ own primary + its children | ✗ | – | – |
| `GET /vms/shared` | ✔ | ✔ | ✔ | ✗ | – | – |
| `GET /vms/{name}`, `/state`, `/capabilities`, `/children` | ✔ | ✔ᵒ | ✔ᵒ (self and own children) | ✗ | ✔ˢ (the child only) | – |
| `GET /vms/{name}/endpoint` (primaries only) | ✔ | ✔ᵒ | ✔ᵒ self | ✗ | ✗ | – |
| `GET /vms/{name}/identity` | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| `POST /vms` (create primary) | ✔ | ✔ (quota, capacity) | ✗ | ✗ | – | – |
| `DELETE /vms/{primary}` (cascade) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{name}/power` (primary, existing) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{parent}/children` | ✔ (charged to owner) | ✔ᵒ | ✔ᵒ (own VM as parent) | ✗ | ✗ | – |
| `POST /vms/{child}/lifecycle` start/resume, restart, shutdown, save | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| `DELETE /vms/{child}` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `PUT /vms/{child}/sharing` | ✔ | ✔ᵒ | ✔ᵒ (owner's `allowSharing`) | ✗ | ✗ | – |
| `PUT /vms/{child}/hardware`, `PUT /vms/{child}/media` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `POST /vms/{child}/lease` (renew) | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `GET/PUT/DELETE /vms/{name}/overrides` | ✔ | ✗ | ✗ | ✗ | – | – |
| `POST /vms/{name}/token`, `DELETE /vms/{name}/token` | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| Idle policy routes (existing) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{self}/activity` (existing) | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Forwards on self (existing routes) | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Forward request for a child (`POST /vms/{child}/forwards`, §12.3) | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ (client via own primary; host only if owner **and** host policy allow) | – |
| `GET /vms/{name}/forwards` | ✔ | ✔ᵒ (+ `?via=` own primaries) | ✔ᵒ self, own children, `?via=self` | ✔ self | ✔ˢ (entries it requested) | – |
| `DELETE /vms/{name}/forwards/{id}` | ✔ | ✔ᵒ / ✔ᶦ | ✔ᵒ / ✔ᶦ | ✔ self | ✔ᶦ | – |
| `POST /vms/{name}/forwards/{id}/ack` | ✔ | primary target: as today (owner); child target: owner of `destination.via` | ✗ | ✗ | owner of `via` when `via` is theirs | – |
| `GET /vms/{child}/addresses` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| Media: acquire/upload/delete (own) | ✔ all | ✔ | ✔ (owner = VM owner) | ✗ | ✗ | – |
| Media: list/read own; read attached to a shared child (reduced shape) | ✔ all | ✔ own | ✔ own | ✗ | ✔ˢ reduced | – |
| Console session + screenshot/input | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| Jobs read (`GET /jobs`, `/jobs/{id}`, events) | ✔ | ✔ own or ✔ᶦ | ✔ᶦ (`initiator = vm:self`) | ✗ | ✔ᶦ | – |
| `POST /jobs/{id}/cancel` | ✔ | ✔ own or ✔ᶦ | ✔ᶦ | ✗ | ✔ᶦ | – |
| `POST /vms/{self}/guest-report` | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Updates: status/check/stage/apply/cancel | ✔ | ✗ | ✗ | ✗ | – | – |
| `GET /audit` | ✔ | ✗ | ✗ | ✗ | – | – |

A shared caller performs **operational** actions only: inspect, start/resume, restart,
graceful shutdown, save, console, addresses and permitted forward requests. Deletion,
sharing, hardware, media and lease changes stay owner/admin (**owner** includes the
owner's primary token). Every shared operation is charged to and bounded by the child's
owner (§4.6) and audited with `owner=<child owner>, initiator=<caller>`.

`allowedActions` (§8.3) is the exhaustive `ChildAction` enum filtered by this table for
the caller: `inspect, start, shutdown, save, restart, delete, share, renew, hardware,
media, console, forwardClient, forwardHost, addresses, overrides, rotateToken`. It is
presentation only; every route re-checks. `forwardHost` is filtered out for a child while
`network.addressVerification` is `unsupported` (§12.5), so the UI never offers an action
that cannot succeed; the authorization statement of the table stays true for the day an
allocation authority exists.

### 2.3 Re-evaluated on every request

| Check | Where | Notes |
|---|---|---|
| User exists and `Enabled` | `UserClaimsTransformation` (existing, extended) and `ITokenService.ValidateAsync` | a disabled user's Bearer tokens and Negotiate identity get no `KnownUser` claim; VM tokens of VMs owned by a disabled user validate to `null`. |
| Role | claims transformation | existing behaviour |
| Token kind | `VmTokenAuthenticationHandler` reads `Vm.TokenKind` at validation | kind is never cached in the token |
| Owner allowance + overrides + host defaults | `IDelegationPolicy.ResolveAsync(owner, parent)` at every delegated mutation | credentials never freeze policy |
| Parent fences (`Deleting`, `ChildCreationClosed`) | `ApiHelpers.FenceDeleting` (existing) and new `FenceParentClosed` | |
| Sharing scope and owner enabled | `ChildOperator` handler | a host-shared child of a disabled owner is treated as private (**decided here**: safest reading of "revoke delegation on user disable") |
| Host forward policy (owner's `AllowHostForwards` **and** `network.hostForwardsEnabled`) | `ForwardRequesterHandler` | disabling host forwards cannot be bypassed through a parent or through sharing (§12.3) |
| Maintenance gate | `IMaintenanceGate.TryEnter` inside the endpoint filter of gated work | §7.4 |
| Capacity and budgets | `ICapacityLedger` at create/start/resume/media begin | §4 |
| Per-VM operation gate | `IVmOperationGate` for every state change | §4.3; a second lifecycle call answers `409 operation-in-progress` |

### 2.4 Revocation effects

| Event | Immediate effect | Sessions and artefacts |
|---|---|---|
| User disabled / deleted | Bearer tokens fail; Negotiate identity unknown; VM tokens of owned primaries fail (`ValidateAsync` → null) | console sessions of that principal are removed (`IConsoleSessionStore.RemoveForPrincipal`); job SSE streams already open are not cut (no secret in them); forwards the user requested on other owners' shared children are revoked by `IAccessExposure.RevokeForRequesterAsync` on the next reconciliation pass; leases keep running (expiry acts regardless of owner state). |
| Role demoted Admin → User | admin routes refuse on the next request | open admin SSE streams keep streaming the job they subscribed to. |
| `POST /vms/{name}/token` | previous hash gone in the same write | in-flight requests already authenticated complete; the next call with the old secret is `401`; console sessions of `vm:<name>` removed. |
| Primary deleted (cascade accepted) | its token hash cleared (existing), parent and children fenced `Deleting` in the same transaction | child console sessions removed at fence time; child forwards removed by the cascade job; shared consumers lose access at fence time. |
| Sharing `host` → `private` | shared callers refused on the next request | their console sessions removed (`RemoveForVm` filtered by principal ≠ owner); forwards with `relationship = shared` on that child are torn down by `IAccessExposure.RevokeNonOwnerAsync` (host rules deleted, client entries removed so the requester's extension kills the tunnel on its next poll), then `INetworkPolicyReconciler.OnSharingChangedAsync`. |
| Child deleted | record fenced then removed | references to media removed after confirmed VM removal; dedicated media deleted; reservations released after confirmed removal. |

## 3. Capability model

### 3.1 Capability levels

`enum CapabilityLevel { Unsupported, Conditional, Supported }` (wire: `unsupported`,
`conditional`, `supported`). `Conditional` means "the backend has the mechanism, a given
VM/state may still refuse; the runtime answer is authoritative".

### 3.2 `BackendCapabilities` (Core) and what Hyper-V reports in this delivery

| Area | Field | Hyper-V (this delivery) | Evidence in the feasibility report |
|---|---|---|---|
| Firmware | `generations: int[]`, `defaultGeneration: int` | `[2]`, `2` (Generation 1 is **unsupported** here: not probed) | Gen 2 probe only |
| Secure Boot | `secureBoot`, `secureBootTemplates` | `supported`, `["microsoftWindows","microsoftUefiCertificateAuthority"]` | both templates set and read back |
| TPM | `tpm` | `supported` | local key protector + TPM enabled, VM started |
| Template lock | `secureBootTemplateLockedAfterTpmInit: bool` | `true` | template setter fails after TPM initialization |
| Optical | `maxOpticalDrives: int`, `auxiliaryMedia` | `2`, `supported` | dual DVD attached, boot order set |
| Boot order | `bootOrder` | `conditional`; `notes`: "disk position in the boot order was not probed (the probe had no disk)" | DVD/NIC order set and read |
| Console | `console.screenshot`, `.keyboard`, `.mouseAbsolute`, `.mouseRelative`, `.interactive` | `supported`, `supported`, `conditional`, `unsupported` (Gen 2: no `Msvm_Ps2Mouse`), `unsupported` (D2) | screenshots 1×1…native; keyboard methods return 0 with Ctrl+Alt+Del visible; synthetic mouse returns 32768 preboot; no PS/2 instance |
| Console bounds | `console.maxScreenshotBytes`, `console.nativeResolutionOnly` | `4 MiB`, `true` (requests above native are refused by the service before WMI) | the report's recommended envelope: "positive dimensions at or below the current native dimensions, with an independent byte/pixel cap" — its boundary probes explicitly do **not** establish a hard maximum (some single-dimension oversizes returned 0), so this is policy, not an observed limit |
| Networking | `network.clientForward`, `.hostForwardPrimary`, `.hostForwardChild`, `.directAddressReporting`, `.addressVerification`, `.isolation` | `supported`, `supported` (gated by policy), **`unsupported`** (no address verification), **`conditional`** (needs guest integration services; the probe's KVP component reported no contact, so address reporting is **unverified**), **`unsupported`** (no IP allocation authority on Hyper-V, §12.5), `unsupported` | existing forwards; integration components "Kein Kontakt"; no enforcing adapter exists |
| Memory | `dynamicMemory`, `memoryOvercommit` | `unsupported`, `unsupported` (fixed RAM policy; `ChildHardware.DynamicMemory` is a reserved seam, refused with `unsupported-capability`) | report: only fixed 512 MiB was booted, dynamic memory was set and read back but never used to boot; the *policy* comes from the requirements (owner-observed Ubuntu dynamic-memory boot failures) |
| Suspend | `suspend` | `supported` | save/resume measured |
| Graceful shutdown | `gracefulShutdown` | `conditional` (needs guest integration services) | `InitiateShutdown` → 32768 without a guest |
| Legacy | `legacy: DriverCapabilities` | existing `Checkpoints`, `Suspend`, `Console` (`vmconnect`/`none`/URL) | existing |
| Notes | `notes: string[]` | free text limitations for the UI, including "LocalSystem execution of the console/child scripts is unverified" until the field test clears it | |

`BackendCapabilities` embeds the existing `DriverCapabilities` unchanged, so the existing
`/power save` gate and the extension's `capabilities` keep reading the same values.

### 3.3 How a backend reports it

| Layer | Contract |
|---|---|
| PowerShell driver contract | `Get-ConstructDriverCapabilities` (existing) is **unchanged**. New optional function `Get-ConstructDriverExtendedCapabilities` in the new child-VM driver file `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` returns the hashtable mirror of §3.2. The loader gains an optional `-Include ChildVm` switch that dot-sources the extra file; without the switch the loader is byte-for-byte today's, so the local install never loads it. Every new PowerShell file targets Windows PowerShell 5.1. |
| Service | `IChildVmDriver.GetCapabilitiesAsync()` (Windows implementation runs the function above through `IProcessRunner`, cached on success like today's capability probe, not cached on failure). Console capabilities come from `IConsoleTransport.Capabilities` (WMI scripts, §13.1) and are merged by `ICapabilityAggregator.GetAsync()`. |
| Per-VM runtime | `IChildVmDriver.GetVmCapabilitiesAsync(name)` → `VmCapabilitiesSnapshot`: device presence (video head, keyboard, synthetic mouse, PS/2 mouse) looked up through the VM's own id, template lock state, native resolution, generation, graceful-shutdown availability. |
| Fake | `FakeChildVmDriver` and `FakeConsoleTransport` report §3.2 values by default; tests flip individual levels. |

### 3.4 How the API exposes it

| Route | Body |
|---|---|
| `GET /host/capabilities` | `{ backend, capabilities: BackendCapabilities, policy: { hostForwardsEnabled, directAddressReporting, allowNeverLifetime, maxChildLifetimeSeconds, capacityMode } }` — `policy` is the host-level view; the caller's effective values are in `/whoami` or `/vms/{name}/identity`. |
| `GET /vms/{name}/capabilities` | `VmCapabilitiesSnapshot` as `{ vm, state, console: { screenshot, keyboard, mouseAbsolute, mouseRelative, interactive, nativeWidth?, nativeHeight?, videoHeadPresent, keyboardPresent, syntheticMousePresent, ps2MousePresent }, hardware: { generation, secureBootTemplateLocked }, gracefulShutdown, network: { clientForward, hostForward, addressVerification } }` — levels are the backend levels lowered by device absence (`unsupported` when the device is missing, `conditional` otherwise); `network.hostForward` is `hostForwardPrimary` for a primary and `hostForwardChild` lowered by the owner/host policy switches for a child. |
| `GET /health` | `apiFeatures: ["host-admin","children","media","console","updates","network"]` so an old service is detected by absence (§10.3). |

Requests that ask for an `unsupported` capability are refused **before** any VM is
allocated with `409 unsupported-capability { capability, level, notes }`; a `conditional`
capability that fails at runtime answers with the runtime error (`409 console-unavailable
{ device, returnValue }`), never a silent no-op.

## 4. Capacity math

### 4.1 One epoch, one snapshot

`IHypervisorInventory.ReadAsync()` returns an `InventorySnapshot { Epoch, ObservedAt,
Host (logical CPUs, total and **free** RAM, per-volume total/free), Vms (every hypervisor
VM, managed or not, with state, CPUs, startup/assigned/maximum memory, dynamic-memory
flag, disks with max and file bytes, saved-state bytes, config volume), Complete,
Problems }`. Every quantity below is computed from **one** snapshot; growth reserves and
physical free space are never mixed across epochs.

The ledger keeps the last snapshot and invalidates it (forcing a fresh read on the next
admission) when: it is older than `capacity.reconcileSeconds`; any reservation is
confirmed, released or trimmed; any artifact completes (media ready, VHD created, VM
removed); a power operation completes. A snapshot with `Complete = false` (a VM whose
disks could not be read, an enumeration error, a volume that did not answer) **fails
admission closed** in `enforce` mode (`409 capacity-unavailable { problems }`) and is
recorded as a problem in `observe` mode.

| Symbol | Definition | Source |
|---|---|---|
| `T_ram` | physical RAM | snapshot |
| `H_ram` | `capacity.ramHeadroomBytes` (or the computed default) | host config |
| `R_managed` | Σ `RamBytes` of managed VMs whose runtime reservation is `pending` or `held` | ledger |
| `R_unmanaged` | Σ over unmanaged VMs observed in any non-terminal state (`Running`, `Paused`, `Unknown` = transient): `max(MemoryStartupBytes, MemoryAssignedBytes)`, and for a dynamic-memory VM `MemoryMaximumBytes` when known (**conservative**; the physical bound below is the backstop for ballooning) | snapshot |
| `A_model` | `T_ram − H_ram − R_managed − R_unmanaged` | |
| `P_unreflected` | Σ over RAM reservations — **pending or held** — of managed VMs of `max(0, RamBytes − Assigned_i)`, where `Assigned_i` is the VM's `MemoryAssignedBytes` **from the same snapshot**: `0` for `Off`/`Saved`/`Absent` (a VM not created yet is `Absent`), the observed value for `Running`/`Paused`/`Unknown`/`Starting` (which may be zero or partial while a start is landing), and `0` (i.e. the full reservation is unreflected) when the VM's memory could not be read. Committed bytes that physical free memory does **not** reflect yet are subtracted exactly once; no state is assumed fully allocated. | ledger + snapshot |
| `A_phys` | `FreeRamBytes − H_ram − P_unreflected` — every committed allocation not yet inside the physical figure is subtracted explicitly; the part already assigned to the VM is inside `FreeRamBytes` already and is not subtracted again | snapshot + ledger |
| `A_ram` | `min(A_model, A_phys)` — the model catches what is committed, the physical figure catches non-VM host consumption and external hardware changes | |
| `C_host`, `A_cpu` | logical CPUs; `capacity.cpuBudget − Σ active vCPUs (managed held/pending + unmanaged non-terminal)`; unlimited when `cpuBudget` is null | |
| `V_free(v)` | physical free bytes on volume `v` | snapshot |
| `H_sto` | `capacity.storageHeadroomBytes` | host config |
| `G(a)` | growth reserve of artifact `a` = `max(0, MaxBytes(a) − FileBytes(a))`, both from the same snapshot; for a ledger-only artifact (media in flight, VHD not yet created) `FileBytes = 0` | |
| `A_sto(v)` | `V_free(v) − H_sto − Σ_{a on v} G(a)` | |

Worked examples (all in `enforce` mode, `H_ram = 4 GiB`, 32 GiB host):

| Case | Free RAM | `P_unreflected` | Managed held | Unmanaged | `A_model` | `A_phys` | `A_ram` |
|---|---|---|---|---|---|---|---|
| idle host | 27 GiB | 0 | 0 | 0 | 28 GiB | 23 GiB | 23 GiB |
| a 20 GiB non-VM workload on the host, one accepted 8 GiB child not started | 12 GiB | 8 GiB (pending) | 8 GiB (pending counts in `R_managed`) | 0 | 20 GiB | 0 GiB | **0 GiB** (a 16 GiB start is refused) |
| free 8 GiB, one 4 GiB pending Off | 8 GiB | 4 GiB (pending) | 4 GiB | 0 | 24 GiB | 0 GiB | **0 GiB** |
| one 16 GiB unmanaged VM running, 6 GiB free | 6 GiB | 0 | 0 | 16 GiB | 12 GiB | 2 GiB | 2 GiB |
| 20 GiB non-VM workload, an 8 GiB **held** VM in a restart's intermediate Off | 8 GiB | 8 GiB (held, Off ⇒ unreflected) | 8 GiB | 0 | 20 GiB | −4 GiB → 0 | **0 GiB** (a second 8 GiB start is refused) |
| 20 GiB non-VM workload, an 8 GiB pending VM observed `Unknown`/`Starting` with **0 assigned** | 8 GiB | 8 GiB (8 − 0) | 8 GiB | 0 | 20 GiB | −4 GiB → 0 | **0 GiB** (another 8 GiB is refused) |
| same, but 3 GiB already assigned to the starting VM | 5 GiB | 5 GiB (8 − 3) | 8 GiB | 0 | 20 GiB | −4 GiB → 0 | **0 GiB** |

**Clamp rule:** every admissible quantity (`A_ram`, `A_cpu`, `A_sto(v)`) is clamped at
zero after computation; a negative value means "refuse every request for that resource",
which is what the restart example above assumes.

**No double counting (decided here):** bytes already allocated on disk are inside
`V_free` (they are not free), so an artifact contributes only its *growth* `G(a)`. A
completed transfer whose reserve is trimmed to its real size invalidates the snapshot, so
the next admission sees both the smaller reserve and the smaller `V_free` from the same
epoch. The same file is never counted through two artifacts: disks are keyed by resolved
path with the parent chain deduplicated, media by `MediaItem.Path`, saved state by VM id.

### 4.2 What is reserved, per resource and state

| Resource | Reserved when | Amount | Released when |
|---|---|---|---|
| RAM | reservation `pending` (before `Start`/`Resume`/`Create-with-start` is issued), `held` (Running, Paused, Starting, Saving, Stopping/graceful pending, Unknown) | `RamBytes` (fixed) | observed terminal `Off`, `Saved` or `Absent`, reported by the owning operation or by reconciliation for a VM no operation holds; never on request acceptance |
| CPU | same as RAM | `Cpus` | same as RAM |
| Disk | at child create (before VHD creation), at primary create (`POST /vms`, §4.6) | full `DiskGb` maximum on the target volume | after confirmed VHD deletion; retained through Off/Saved |
| Saved state | while a VM is Running or Saved and `suspend` is supported | `RamBytes + 64 MiB` on the VM's configuration volume (**decided here**: the VMRS holds RAM contents; the tiny probe file must not be generalized) | Off/Absent confirmed (after a graceful shutdown there is no VMRS) |
| Media (acquire/upload) | at job/upload begin | declared `sizeBytes`, or `Content-Length`, or `media.maxBytes` when unknown | trimmed to actual size at completion; released on failure after the partial file is confirmed deleted |
| Dedicated auxiliary media | as media | actual max | deleted with the VM (§6.5) |

Unknown state (`VmState.Unknown`, which also covers Starting/Saving/Stopping) keeps
whatever is reserved: conservative by rule. No timeout is ever treated as state evidence.

### 4.3 Admission (atomic, serialized, operation-owned)

`ICapacityLedger.TryReserveAsync(ReservationRequest)` is the only entry point. Every
request names its **operation id** (the job id, or the request id of a synchronous start)
and the operation registers itself in `IOperationRegistry` for as long as it runs.

1. Take the ledger gate (one in-process `SemaphoreSlim(1,1)`; the service is a single process).
2. Refresh the snapshot if invalid (§4.1); fail closed on `Complete = false` in `enforce` mode.
3. Open one SQLite `IMMEDIATE` transaction on `reservations`.
4. Compute user aggregates (§4.6) and host admissibles from that snapshot.
5. In `enforce` mode refuse with `CapacityDecision { Allowed=false, Resource, Scope, Requested, AllowedAmount, Available, Reason, Epoch }` on the first failing check, in the order: user quota counts → user budgets → host CPU → host RAM → volume storage. In `observe` mode never refuse; record `Reason = "observe:<what would have failed>"` and audit `capacity.observe`.
6. Insert reservation rows with `phase='pending'`, `operation_id`, `pending_until = now + request.PendingTimeout`, commit, release the gate, return the ids.

When the reservation is part of an `AdmissionPlan` (child create, primary create, media
begin, cascade), steps 3–6 execute inside the plan's transaction instead of their own
(§7.3); the rules are identical, and the standalone `TryReserveAsync` is used by
synchronous starts only.

The hypervisor call happens **after** step 6, under the VM's operation gate
(`IVmOperationGate.AcquireAsync(vm, operationId)`), which every state-changing operation
on that VM takes (start, save, shutdown, restart, delete, hardware, media, cascade, expiry,
per-VM reconciliation). `ConfirmAsync(ids, observedState)` flips `pending → held`;
`ReleaseAsync(ids, observedState, reason)` deletes them. Both require the observed state
that justifies the transition, and both are audited (`capacity.confirm`,
`capacity.release`). A failed start releases only after the driver reports `Off`. A
graceful-shutdown request releases nothing until `Off` is observed. A long-running
operation extends its pending rows (`ExtendAsync`) at least every `PendingTimeout / 2`.

**Restart** keeps its RAM/CPU reservation `held` for the whole job (the VM gate is held,
so nobody observes the intermediate `Off`); the reservation is released only if the
restart fails and leaves the VM `Off`.

### 4.4 Reconciliation

Runs at startup (after `Bootstrap` forwards reconcile), every `capacity.reconcileSeconds`,
and after every power operation and job completion. **Acquisition sequence (normative,
respects the lock order of §6.5):** (1) read the inventory snapshot with no gate held;
(2) for each managed VM, `IVmOperationGate.TryAcquireAsync` — **non-blocking, never
waits, never upgrades to a blocking acquire**; a VM whose gate is held by a live operation
is skipped (the operation owns that VM's reservations and will confirm or release them
itself); with the VM gate held, the VM's rows below are applied through **one
`MutateAsync` call** (which takes the ledger gate for the duration of its transaction —
reconciliation never takes the ledger gate itself); a VM flagged `media-unverified`
additionally takes its media gates (in id order, before that call) to settle references
against `GetAttachedMediaAsync`; (3) host-level rows (unmanaged VMs, snapshot
invalidation) through a final `MutateAsync` with no VM gate. The ledger gate is never held
while a VM or media gate is acquired.

**Stale-observation rule (decided here): the snapshot is evidence for host-level sums
only.** For each VM, with the **VM gate held and before entering `MutateAsync`**,
reconciliation re-reads the VM's state from the hypervisor (`IHypervisorDriver.GetStateAsync`,
one per-VM call; the VM gate is what makes it non-stale, because no operation can touch
that VM while it is held) and passes that observed state into the mutation; a
`GetStateAsync` failure ⇒ `Unknown` (keep everything). Inside `MutateAsync` — which
performs no I/O outside SQLite and never holds the ledger gate across a hypervisor call —
the VM row is re-read through `IAdmissionScope.ReadVmAsync` and the VM is skipped when its
`PowerGeneration` or `CurrentJobId` differs from the value read during the snapshot pass
(an operation ran in between; the next pass re-observes). Only then do the rows below
release, promote, mark overdue or bump `power_generation`, and only on the fresh read,
never on the pre-gate snapshot. S3 pins the interleaving "snapshot says Off → VM starts
and confirms → reconciliation acquires the gate" (nothing released, generation untouched).

| Observation | Action |
|---|---|
| `pending` reservation whose `operation_id` is alive in `IOperationRegistry` | never touched, whatever the VM state |
| **orphaned** `pending` reservation (operation not alive: the job was marked interrupted at startup, or the process that owned a synchronous start is gone) — RAM/CPU | resolved by **observed VM state**, never by time: VM `Running`/`Paused`/`Unknown` → **promote to `held`** (the detached hypervisor operation may have completed or may still complete; the hold is charged to the owner and shows as `origin=reconcile`); VM `Off`/`Saved`/`Absent` **and** `pending_until < now` → release (`reason=orphaned-operation`); before `pending_until` → keep (a detached start may still land). `OrphanOutcome` records the evidence. |
| orphaned `pending` reservation — storage | resolved by the **artifact**: disk/media/upload file present (any size) → promote to `held` with the artifact path (a retained artifact; visible in `/host/capacity` and collected only by `child-delete`, `DELETE /media`, or `media-cleanup` after confirmed deletion); artifact confirmed absent → release; artifact state unknown (volume unreadable) → keep. `pending_until` only bounds *when* the check runs. |
| enqueue-crash leftovers: a child row whose create job was marked interrupted at startup and whose persisted `phase` is null (durable evidence that the job never ran past admission) | resolved by **resource evidence**, never by the null incarnation alone: hypervisor VM absent by name **and** by incarnation, disk file absent, no media `.part` for it → row, references and reservations removed, audited `vm.create.abandoned`. Any liability still present (a VM, a disk, a partial artifact, an unreadable volume) → the row stays `Deleting` with the failed job, the artifacts are promoted to `held` (previous row), and the owner's `DELETE` (retryable) or `media-cleanup` finishes it. A job with a non-null `phase` is treated as a failed run, never as never-started. |
| Managed VM `Off`/`Saved`/`Absent` with a `held` RAM/CPU reservation and no live operation on the VM | release; audit `capacity.release reason=observed-<state>` (external stop) |
| Managed VM `Running`/`Paused`/`Unknown` with no RAM reservation and no live operation | insert `held` reservation with `origin='external'`, charged to the VM's owner; audit `capacity.external-hold`; for a child, the lease rule of §5.3 applies. |
| Unmanaged VM present | included in `R_unmanaged`/CPU/storage sums; never given an owner; listed in `GET /host/capacity.unmanaged[]`. A VM that appears in the hypervisor with a managed name but a different incarnation is reported as `observed.storageProblem = "incarnation-mismatch"` and treated as unmanaged for capacity. |
| Disk file of a managed VM missing/unreadable | keep the reservation, set `observed.storageProblem`; the snapshot is `Complete = false` until it reads again |
| Media file missing for a `ready` item | mark `failed`, keep storage charged until `media-cleanup` confirms |
| Service restart | the ledger table is the source; nothing is reset. `Bootstrap` marks interrupted jobs failed first (existing), so their pending rows become orphaned and are resolved on the first pass **by the per-resource rows above** (promoted, kept or released on evidence); running VMs are re-confirmed, saved ones keep storage. |

### 4.5 `reservations` table (migration 300)

| Column | Type | Meaning |
|---|---|---|
| `id` | `TEXT PRIMARY KEY` | GUID |
| `resource` | `TEXT NOT NULL` | `ram`, `cpu`, `storage` |
| `scope_owner` | `TEXT NULL COLLATE NOCASE` | user charged; NULL for unmanaged |
| `vm_name` | `TEXT NULL COLLATE NOCASE` | VM, when the artifact belongs to one |
| `artifact` | `TEXT NULL` | `disk:<path>`, `saved-state:<vmId>`, `media:<id>`, `upload:<id>` |
| `volume` | `TEXT NULL` | drive root for storage |
| `amount` | `INTEGER NOT NULL` | bytes or vCPUs |
| `phase` | `TEXT NOT NULL` | `pending`, `held` |
| `origin` | `TEXT NOT NULL` | `api`, `external`, `reconcile` |
| `operation_id` | `TEXT NULL` | the operation that owns a pending row |
| `pending_until` | `TEXT NULL` | orphan sweep deadline (§4.4) |
| `created`, `confirmed_at` | `TEXT` | |

### 4.6 User aggregate budgets, defaults and the primary path

Aggregates cover **all** primaries and children owned by the user on this host. A shared
caller's start of somebody else's child is charged to that child's owner.

| Policy | Default | Counted |
|---|---|---|
| Primaries | `User.MaxVms` (existing rows), `userDefaults.maxPrimaries = 1` for users created through §8.4's new shape | rows with `kind='primary'` |
| Child creation | enabled | switch |
| Retained children | 1 per user per host, across all their primaries | rows with `kind='child'` in **any** state, including `Deleting` until removed |
| CPU | no user budget (null); host `cpuBudget` null | active vCPUs of held/pending reservations |
| RAM | null (no guessed default); host capacity still binds | held/pending RAM |
| Storage | null; host capacity still binds | full disk maxima + media (charged to the **media owner** only, §6.2) + saved-state reservations |
| Max child lifetime | unlimited; `never` allowed | per request |

**Primary path (normative, no provisioning rewrite).** `POST /vms` keeps the
`VmJobs.CreateAsync` body untouched; its **acceptance** becomes one `AdmissionPlan`
(§7.3: `VmToInsert` with the primary allowance, disk + RAM + CPU `ReservationRequest` with
the job id as operation, the Queued `create-vm` job) admitted by `IAdmissionStore.AdmitAsync`
and started by `IPersistedJobRunner`; the old `AddAsync` + `SubmitAsync` pair is removed
from that endpoint only. `POST /vms/{name}/power {start}` reserves RAM/CPU pending with a
request-scoped operation, confirms on `Running`, releases on `Off` (through
`MutateAsync`). `remove-vm` releases after confirmed removal. In `observe` mode (every
migrated host until an admin changes it) admission never refuses, so the existing
behaviour is identical. The primary ISO catalog (`Iso:CacheDir`) is **not** reserved
through the ledger: its files are physically present and thus inside `V_free`; a build in
flight is bounded by the admin-configured source size and is listed as a limitation
(§14.2).

Refusals carry `409 capacity-exhausted` with
`{ resource: "ram"|"cpu"|"storage"|"children"|"primaries", scope: "user"|"host"|"volume",
requested, allowed, available, reason, epoch }` (`allowed` = `CapacityDecision.AllowedAmount`).
Nothing is clamped.

## 5. Lease semantics

### 5.1 Lifetime input

`lifetime` is a required string on child creation and on every `start`/`resume`:
`never`, or `<n><unit>` with unit `m`, `h`, `d` (`^[1-9][0-9]*[mhd]$`), minimum `5m`, no
maximum other than policy. Stored as typed (`RequestedText`) and in seconds
(`RequestedSeconds`, null for `never`). Missing → `400 lifetime-required`; exceeding
`effective.maxChildLifetimeSeconds` or `never` with `allowNeverLifetime=false` →
`403 lifetime-not-allowed { requested, allowedMax, allowNever }`. Never defaulted.

### 5.2 Fields and states

| `LeaseState` | Meaning | `ActivatedAt` / `ExpiresAt` |
|---|---|---|
| `inactive` | created powered-off, never started | null / null |
| `active` | running lease | set / set |
| `unlimited` | `never` and started | set / null |
| `expired` | expiry action completed (VM observed Off) | kept for display |
| `overdue` | expiry is due but the action failed, was unsupported, or the VM was started outside the API; retried | set / **set to the moment it became due** |

`Lease.Version` is incremented by every lease write (`IVmDelegationRepository.UpdateLeaseAsync`
is compare-and-set on the version). Primaries have `Lease = null` and are never touched
by the lease scheduler.

### 5.3 Activation, renewal and race rules

| Event | Effect on the lease |
|---|---|
| Create with `start=true` | request stored; `pending` reservation; on confirmed `Running`: `ActivatedAt = activationBase`, `ExpiresAt = activationBase + requested` (or `unlimited`). **`activationBase` is the moment the start was issued, recorded durably before the driver call**: for the synchronous `start` route it is the intent's `acceptedAt` (§7.3, one request apart from the driver call); for `child-create` it is the timestamp the job's `start` phase first began, made durable **before** the driver call as a **start intent in the operation-key store** (§7.3, "job-issued starts"): the job calls `IOperationKeyStore.TryInsertAsync` for the key `(owner, "lifecycle-start", "<jobId>:start")` with `intentJson = { lifetime, activationBase: now, expectedLeaseVersion }` and the VM's `powerGeneration`; a re-entered or retried `start` phase gets `Replay` with the existing row and reuses its `activationBase` (the insert is `INSERT OR FAIL`, so the first timestamp is the only one that ever exists), so the creation work (media, hardware, disk, attach) and queueing never consume the lease and a retry cannot extend it. **Decided here:** the base is the last durable point before the hypervisor is asked to start — never a later observed `Running`, never the create request's acceptance — and it lives in the intent store, not in a new `jobs` column. |
| Create with `start=false` | `inactive`; activates on the first successful `start`, which **must carry its own `lifetime`** (the creation value is stored as the default the CLI offers, not applied silently). |
| `start` (one action; §5.3a decides by observed state) | Off/Saved/Paused resume with a **new `lifetime`**, which replaces request and expiry from the start's `activationBase` (its acceptance time; version bumped); Running → `409 already-running`; **never** a renewal path for a running VM. |
| `restart` (API) | graceful shutdown + start, RAM held throughout; `ExpiresAt` **unchanged**; refused with `409 lease-due` when the lease is already due or `overdue` (renew first, or let expiry act). |
| Paused VM at expiry | graceful shutdown needs a running guest: outcome `unavailable` with reason `paused`, lease `overdue`, RAM stays held; the documented recovery is `start` with a new `lifetime` (§5.3a) or `DELETE`. |
| Guest-initiated reboot | invisible to the service; unchanged. |
| Shared caller start/resume | must supply `lifetime` too; bounded by the **owner's** policy; charged to the owner. Sharing changes never touch the lease. |
| Service restart | `ExpiresAt` is persisted; the scheduler resumes; an `active` lease with `ExpiresAt < now` at startup is due immediately. |
| Host downtime past expiry | same as above: Hyper-V may auto-start the VM (`AutomaticStartAction`); the first tick runs the expiry action. If the VM is observed Off, the lease becomes `expired` with no action. |
| External start (outside the API) of an `expired`/`inactive`/`overdue` child | reconciliation creates the RAM hold (§4.4) and writes `State = overdue`, `ExpiresAt = now`, `LastExpiryOutcome = "external-start"`, version bumped → the expiry action runs on the next tick (**decided here**: a child running without a lease is the case the lease exists to prevent). |
| `POST /vms/{child}/lease { lifetime }` (owner/admin) | explicit renewal from `now` under the VM gate; version bumped; audited `vm.lease.renew`. (**decided here**: an explicit owner renewal exists because "stop and start again" as the only extension would break long tests; there is **no** automatic keepalive, because the lease is wall-clock by design.) |

### 5.3a `start` by observed state (the one table every layer uses)

`start` is the only resume verb; `IHypervisorDriver.StartAsync` (`Start-ConstructVm`, which
already resumes saved and paused VMs) is the driver call in every row.

| Observed state | Lease state | Admission | Effect | Answer |
|---|---|---|---|---|
| `Off` | any (`inactive`, `expired`, `overdue`, and also `active`/`unlimited` after an explicit shutdown, which preserves the lease) | RAM+CPU `pending` → confirm on `Running` | (re)activates the lease from the start's `activationBase` (acceptance time) with the **new** lifetime, replacing whatever was stored | `200 { state, lease }` |
| `Saved` | `active`, `expired`, `overdue`, `unlimited` | RAM+CPU `pending` → confirm on `Running` (saved-state reservation released after resume) | same | `200` |
| `Paused` | `active`, `overdue`, `unlimited` | **none** (RAM/CPU already `held`) | resumes; the lease is re-activated from the start's `activationBase` with the new lifetime (a paused VM ran no useful wall clock, but the lease is a lease) | `200` |
| `Running` | any | – | nothing | `409 already-running` |
| `Unknown` (transient) | any | – | nothing | `409 operation-in-progress` when a job holds the gate, else `409 vm-state-unknown` (retry) |

`lifetime` is required in every accepting row; `restart` and `shutdown` are the other
verbs and never change the lease. Acceptance S6 pins every row.

### 5.3b Power generation (durable fence for every power action)

Every VM row carries `power_generation` (`INTEGER NOT NULL DEFAULT 0`, migration 100).
It is bumped, in the same transaction as the state it records, by **every** power action
the service issues (`start`, `shutdown` completion, `save`, `restart` phases, delete power
off) and by every transition reconciliation **observes** (an external start or stop). A
start intent (§7.3) records the generation it was accepted at; completion and replay use
`IAdmissionScope.BumpPowerGenerationAsync(vm, expected)` as a compare-and-bump, so any
intervening power action — including a shutdown, which leaves the lease untouched — turns
a delayed retry into `409 operation-key-conflict { reason: "power-state-changed" }`
instead of a fresh start. Reconciliation checks for an `InFlight` start intent on a VM
**before** classifying an observed `Running` as external: if one exists at the VM's
current generation, the intent is completed (activation from its `activationBase`, §7.3)
rather than marked `external-start`.

### 5.4 Scheduler and the expiry job's re-check

`LeaseSchedulerService` (hosted service in `Api/Hosting/`, `lifecycle.leaseTickSeconds`,
default 30 s; `Constructd:Lease:SchedulerEnabled=false` in tests; the pure part is
`Core/Logic/LeaseRules.cs`). Due selection (`IVmDelegationRepository.ListLeasesDueAsync`):

```
(lease_state = 'active'  AND lease_expires_at <= @now)
OR (lease_state = 'overdue' AND (lease_last_attempt_at IS NULL OR lease_last_attempt_at + @retry <= @now))
```

`overdue` rows always carry a non-null `lease_expires_at` (§5.2/§5.3), so an
externally-started child is selected. For each due VM without a live lifecycle job
(`CurrentJobId` null or terminal) the scheduler submits `vm-shutdown` with
`reason=lease-expiry` and `expectedLeaseVersion = Lease.Version`. When the job runs it
takes the VM gate and **re-reads the lease**: if the version changed (a renewal, a start
with a new lifetime) or the lease is no longer due, the job ends `succeeded` with outcome
`superseded` and touches nothing. Ticks are serialized (one gate) and never race an API
shutdown, renewal or start, because all of them take the same VM gate.

### 5.5 Expiry action (D1, D6)

`vm-shutdown` job, `reason=lease-expiry`, under the VM gate:

1. Observed `Off`/`Saved`/`Absent` → lease `expired`, audit `vm.lease.expired outcome=already-off`, done.
2. `IChildVmDriver.ShutdownGracefulAsync(name, timeout = lifecycle.gracefulShutdownTimeoutSeconds)`:
   - guest integration services absent or without contact (or VM `Paused`) → `Unavailable`; the job fails with error `guest-shutdown-unavailable`; lease `overdue`, `lease.overdue = true` in inventory; RAM stays held; retried every `leaseRetrySeconds`.
   - request accepted, VM not Off after the timeout → `Timeout`; job error `shutdown-timeout`; lease `overdue`; RAM stays held; retried.
   - VM observed Off → `Completed`; lease `expired`; RAM/CPU and the saved-state reservation released after the observation; disks stay charged.
3. Never: force-off, save, delete, checkpoint. The user-panel **Shut down** button and `construct vm shutdown` submit the same job with `reason=user`. Explicit **deletion** is a different path and may power the VM off (§8.8).

The extension and CLI show `overdue` children with the last outcome so a user can act
(delete, or fix the guest and shut it down).

## 6. Media

### 6.1 Registry (separate from the Construct ISO catalog)

The existing `IIsoCatalog`/`FileIsoCatalog` (`Iso:CacheDir`, `construct-autoinstall-*.iso`,
`current.pointer`) stays the primary-Construct patched-media path and is **never** entered by
a child request. Child media has its own registry, its own directory and its own job kind.
Shared primitives: `IIsoFileSystem` (file ops), the "held open by Hyper-V ⇒ skip and report"
deletion rule, SHA-256 streaming and the `.part`-then-rename download shape of
`HttpIsoDownloader` (extracted into `Constructd.Windows/Media/MediaFileStore.cs`, the
existing class unchanged).

`MediaItem` record (§13.1) / `media` table (migration 200):

| Field / column | Type | Meaning |
|---|---|---|
| `Id` / `id` | `TEXT PRIMARY KEY` | 32-hex; derived from `SHA-256(owner + ":" + operationKey)` when a key is given (stable replay target, §7.3), random otherwise; also the file name `<id>.iso` |
| `Owner` / `owner` | `TEXT NOT NULL COLLATE NOCASE` | the user charged for the bytes, **immutable**; the effective owner for primary-token uploads |
| `Name` / `name` | `TEXT NOT NULL` | display name, sanitized (control chars stripped, ≤120 chars, no path separators) |
| `Role` / `role` | `TEXT NOT NULL` | `install` or `auxiliary` |
| `Source` / `source` | `TEXT NOT NULL` | `url` or `upload` |
| `SourceUrl` / `source_url` | `TEXT NULL` | scheme + host + path **only** (query and fragment dropped before storage; they can carry signed tokens) |
| `Path` / `path` | `TEXT NOT NULL` | `<Media:RootDir>\<id>.iso` |
| `State` / `state` | `TEXT NOT NULL` | `pending` (upload open), `transferring` (acquire, or hashing at completion), `ready` (immutable), `failed`, `deleting` |
| `SizeBytes` / `size_bytes` | `INTEGER NULL` | actual bytes once known |
| `ReservedBytes` / `reserved_bytes` | `INTEGER NOT NULL` | storage reserved (§4.2), id of the ledger row in `reservation_id` |
| `Sha256` / `sha256` | `TEXT NULL` | computed while streaming |
| `ExpectedSha256` / `expected_sha256` | `TEXT NULL` | caller-supplied, lowercase hex |
| `Error` / `error` | `TEXT NULL` | safe description (`SafeError`) |
| `JobId` / `job_id` | `TEXT NULL` | the acquiring/verifying job |
| `DedicatedTo` / `dedicated_to` | `TEXT NULL COLLATE NOCASE` | the VM this item exists for (CLI `--aux-iso`/`--iso` uploads set it to the child being created); deleted with that VM |
| `Created`, `ReadyAt`, `LastReferencedAt` | `TEXT` | |

`media_references (media_id TEXT NOT NULL, vm_name TEXT NOT NULL COLLATE NOCASE, slot TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY (media_id, vm_name, slot))`.

`Media:RootDir` option default `C:\ProgramData\Construct\service\media` (a sibling of
`Iso:CacheDir`, hardened by the installer like the data dir). Fake mode: in-memory store
and a temp-dir file store.

### 6.2 Visibility, access control and accounting

| Actor | List / read metadata | Attach to a child | Delete |
|---|---|---|---|
| owner (user or primary token of an owned primary) | own items | own items | own items, no references |
| admin | all | any item to any child | any, no references |
| shared caller | metadata of media attached to a shared child: `{ id, name, role, sizeBytes }` only | ✗ | ✗ |
| anyone | ✗ content | | |

**Accounting (decided here: host once, owner once).** The bytes of a media item are
reserved once in the host ledger and count once against the storage budget of the media
**owner**, whoever references it. An admin attaching Alice's item to Bob's child changes
no liability: Bob's child gets a reference, Alice keeps the charge, and Alice's `DELETE`
is refused while the reference exists (`409 media-in-use`). This is what "the effective
owner is charged" means for media; the child's owner is charged for the child's disk and
runtime, never for another owner's bytes.

There is **no** content download route in this delivery (**decided here**: auxiliary ISOs
carry answer files with secrets; the only consumer is the hypervisor). Checksums of
`auxiliary` items are shown to owner/admin only.

### 6.3 URL acquisition rules (`media-acquire` job)

| Rule | Detail |
|---|---|
| Scheme | `https` always; `http` only when `media.allowHttp` and `expectedSha256` is supplied (**decided here**: distro mirrors are often plain http; a checksum makes the transport irrelevant). Anything else → `400 url-refused { reason: "scheme" }`. |
| Host | DNS name or IP literal. Resolve **all** addresses (A and AAAA) before connecting; refuse if **any** is loopback, unspecified, `0.0.0.0/8`, link-local (`169.254/16`, `fe80::/10`), private (RFC 1918), `192.0.0.0/24`, benchmarking `198.18.0.0/15`, ULA (`fc00::/7`), CGNAT (`100.64/10`), multicast, broadcast, the metadata address `169.254.169.254`, NAT64 `64:ff9b::/96`, or an IPv4-mapped/compatible IPv6 form of those → `400 url-refused { reason: "address", address }`. The pure classification is `IUrlAdmissionPolicy.Check` (Core, tested with a fixture list). |
| Connection pinning | connect to one of the validated addresses via `SocketsHttpHandler.ConnectCallback`; no second resolution, so DNS rebinding cannot redirect the fetch. |
| Port | any; the default per scheme when omitted. |
| Credentials | userinfo in the URL → refused; no request headers, cookies or proxies from the caller; the service's own proxy settings are not applied (`UseProxy=false`). |
| Redirects | manual, max 5; every hop re-validated with the same scheme/host/address rules; `https → http` downgrade refused; the final URL (minus query) is recorded as `sourceUrl`. |
| Size | `Content-Length > media.maxBytes` → `413 media-too-large` before download; unknown `Content-Length` → allowed, `media.maxBytes` reserved, abort with `media-too-large` if exceeded while streaming; reservation trimmed to actual size at completion. |
| Time | `media.acquireTimeoutMinutes` overall; 120 s read idle timeout. |
| Integrity | SHA-256 computed while streaming; mismatch with `expectedSha256` → `failed`, file deleted, storage released, error `checksum-mismatch`. |
| Format | ISO 9660 `CD001` primary volume descriptor at sector 16 after the download (**decided here**: UDF-only images are accepted only when `expectedSha256` is present, because the check cannot validate them); otherwise `failed`, error `not-an-iso`. |
| Progress | job progress lines every 64 MiB or 5 s: `downloaded 1.2 GiB of 3.0 GiB (40 %)`; the URL host is logged, the path is not. |
| Cancellation | `POST /jobs/{id}/cancel` → partial deleted, storage released, state `failed` with error `cancelled`. |
| Limits | `media.maxItemsPerUser` counts `pending`+`transferring`+`ready` items of the owner (`409 media-limit`); the storage reservation is taken atomically at job acceptance (§7.3 transaction). |

### 6.4 Upload protocol (resumable, idempotent, immutable once complete)

Upload states: `open → completing → done`, or `open|completing → aborted`, or `open →
expired`. Every transition is a compare-and-set (`IMediaStore.TryTransitionUploadAsync`)
and every route checks the state it requires.

| Step | Route | Semantics |
|---|---|---|
| begin | `POST /media/uploads { name, role, sizeBytes, expectedSha256?, dedicatedTo?, operationKey? }` | validates (`sizeBytes ≤ media.maxBytes`, item count), reserves `sizeBytes` on `Media:RootDir`'s volume and inserts the `media` (state `pending`) + `media_uploads` rows in **one transaction with the operation key** (`409 capacity-exhausted`, `409 media-limit` otherwise), creates a sparse `<id>.part`. `201 { uploadId, mediaId, chunkSizeBytes, chunkCount, expiresAt, received: [] }`. Same `operationKey` + fingerprint → the existing upload with `200`. |
| chunk | `PUT /media/uploads/{id}/chunks/{index}` body `application/octet-stream`, exact `chunkSizeBytes` (last chunk: the remainder), `Content-Length` required | **protocol:** acquire the upload's lock (the media gate of `mediaId`) → re-read state; anything but `open` → `409 upload-not-open` (`expired` → `409 upload-expired`) → write at `index × chunkSizeBytes` → record the index → release. Re-sending an index overwrites (the hash at completion is the truth); `204`; `400 chunk-size`, `404`. Each write holds a maintenance-gate handle (§7.4). |
| status | `GET /media/uploads/{id}` | `{ state, received: [indexes], missing: [indexes], expiresAt }` — resumption reads this and re-sends `missing`. |
| complete | `POST /media/uploads/{id}/complete` | **protocol:** acquire the same lock (so every admitted chunk write has finished) → state `open` with all chunks → CAS `open → completing` → release; hash the file (inline up to 2 GiB, otherwise `media-verify` job and `202`); then acquire the lock again → if the state is still `completing` (nobody aborted/expired it meanwhile) → CAS `completing → done` + media `ready` (immutable; no chunk can ever be written again because writes require `open`), reservation trimmed → `201 MediaItemResponse`; if the state is no longer `completing`, discard the result and answer the state's error. Missing chunks → `409 upload-incomplete { missing }`; checksum mismatch / not an ISO → upload `aborted`, media `failed` (`422 checksum-mismatch` / `422 not-an-iso`), storage released after the file is confirmed deleted. Idempotence: complete on `done` → `200` with the item; on `completing` → `202` with the running verify job (or `409 upload-not-open` when the inline hash is running, with `Retry-After`); on `aborted`/`expired` → `409 upload-not-open`. |
| abort | `DELETE /media/uploads/{id}` | under the lock: CAS from `open` or `completing` to `aborted` (a running hash sees the state change at its second lock acquisition and discards its result); deletes the part, releases the reservation, media row removed; `204`; `409 upload-not-open` when `done`. |
| expiry | `media-cleanup` job (daily and admin-triggered) | under the lock: `open` uploads past `expiresAt` → `expired` (same cleanup as abort), audited; a `completing` upload is never expired by cleanup. |

`media_uploads (id TEXT PRIMARY KEY, media_id TEXT NOT NULL, owner TEXT NOT NULL COLLATE
NOCASE, size_bytes INTEGER NOT NULL, chunk_bytes INTEGER NOT NULL, received_json TEXT NOT
NULL, state TEXT NOT NULL, operation_key TEXT NULL, created TEXT NOT NULL, expires_at TEXT
NOT NULL)`.

### 6.5 References, locks and cleanup

**Lock order (decided here): per-VM operation gate → per-media gates in ascending id
order, never reversed.** Both gates are DI singletons (`IVmOperationGate`, `IMediaGate`,
§13.1) so every pair locks the same instances. Attach, replace and detach take the VM
gate first, then `IMediaGate.AcquireManyAsync` for the items involved; the child-create
and child-delete jobs hold the VM gate (their own operation) and take media gates while
checking readiness or detaching dedicated items; `DELETE /media/{id}`, `media-cleanup`
and `media-acquire` take **media gates only** and must never acquire a VM gate while
holding one (a dedicated item whose VM still exists is not cleanup's to delete; the VM's
own delete path handles it under the VM gate). The **ledger/admission gate is innermost**:
VM gate → media gates → ledger gate; whoever holds the ledger gate (admission,
`TryReserveAsync`, `TrimAsync`, the per-VM step of reconciliation) never acquires a VM or
media gate inside it (reconciliation takes its VM and media gates *before* the ledger
gate, §4.4). An admission plan therefore checks media readiness by **SQL state** inside its
transaction (media state transitions are SQL compare-and-sets too, and SQLite serializes
writers), not by taking media gates; media completion holds its media gate and then calls
`TrimAsync` (ledger gate) — consistent with the order.

| Rule | Detail |
|---|---|
| Attach at create | the admission plan inserts the `media_references` rows together with the child row (items must be `ready` and not `deleting`; `TryAddReferenceAsync` semantics inside the plan) **before** anything touches the hypervisor. A failed create keeps the references until the rollback (`RemoveAsync` of the VM) is **confirmed** (`GetVmIdAsync` → null); only then are they removed. If the rollback fails, the row stays `Deleting` with its references and the owner's `DELETE` retries. An item in `deleting` can never gain a reference, and an item with a reference can never enter `deleting`: both checks are inside the same media gate. |
| Replace (`PUT /vms/{child}/media`, VM Off) | under the VM gate then the media gates: add references for the new items → `IChildVmDriver.SetMediaAsync` → `GetAttachedMediaAsync` → references are reconciled to **what is really attached** (old items no longer attached lose their reference; anything still attached keeps it). On a driver failure the same query runs; if it fails too (`Complete=false`), the references stay as the **superset** of old and new items and the VM is flagged `observed.storageProblem = "media-unverified"`; the reconciliation pass (§4.4) re-queries and settles the references later. A reference is therefore never removed while the hypervisor might still hold the file. |
| Detach on delete | `child-delete` removes references **after** the VM is confirmed removed. |
| Dedicated media | items with `dedicatedTo = <vm>` are deleted by `child-delete`/cascade after the reference is gone (state `deleting`, file removed, reservation released after confirmation). A dedicated item that is also referenced by another VM (admin attach) is only dereferenced. |
| Delete (`DELETE /media/{id}`) | under the media gate: `409 media-in-use { references }` while any row exists; otherwise CAS `ready\|failed → deleting` (blocks new references), file removed; a "held open" result keeps `deleting` + `error` and storage charged, retried by cleanup; storage released after the file is confirmed gone, row removed. |
| Orphans | `media-cleanup` removes `.part`/`.iso` files under `Media:RootDir` that match no row and are older than 1 h, `failed` items older than `uploadTtlHours`, `deleting` items (retry), expired uploads, and — only when `media.unreferencedTtlHours` is set — unreferenced non-dedicated items older than that. It never touches `Iso:CacheDir`, `Iso:SourcePath`, or anything outside `Media:RootDir`. |
| Accounting | a failed acquisition/upload keeps its storage charged until the partial file is confirmed absent; a `failed` item is visible in `GET /media` with `error` so nothing leaks silently. |

### 6.6 Construct-generated ISOs (requirement deviation, recorded)

The requirements extend the ownership/reference principle to Construct-generated ISOs.
In this delivery the primary ISO catalog keeps its existing retention exactly (versioned
files, `current.pointer`, `admin iso prune` skipping media a VM holds open); it gains
**no** reference table. The admin surface exposes its state read-only (`GET
/host/iso-catalog`, §8.18) so that the Media area of the requirements is covered for
primaries. Reference-counted collection of catalog media is deferred and listed in §14.2
(**decided here**: the catalog is the primary path and the zero-change bar applies to it;
a reference table there would change `prune` semantics for every existing host).

### 6.7 Log hygiene

Never logged, audited, streamed or stored: URL query strings/fragments, upload bytes,
auxiliary content, checksums of auxiliary media in progress lines, media names before
sanitization. Progress lines name the media id and the URL host only.

## 7. Jobs

### 7.1 Kinds

| Kind | Started by | Phases (`job.phase`) | Result payload | Gated by draining |
|---|---|---|---|---|
| `create-vm` (existing) | `POST /vms` | unchanged (text progress only) | `VmCreateResult` | ✔ |
| `remove-vm` (existing) | `DELETE /vms/{primary}` without children | unchanged | `VmRemoveResult` | ✔ |
| `parent-cascade-delete` | `DELETE /vms/{primary}` with children | `fence`, `children`, `primary`, `forwards`, `media-references`, `network`, `done` | `{ name, children: [{ name, outcome, error? }], releasedForwards }` | ✔ |
| `child-create` | `POST /vms/{parent}/children` | `admit`, `media`, `hardware`, `disk`, `attach`, `start`, `done` | `{ name, parent, incarnation, state, lease, addresses: [] }` | ✔ |
| `child-delete` | `DELETE /vms/{child}` | `fence`, `power`, `vm`, `forwards`, `media-references`, `dedicated-media`, `storage`, `done` | `{ name, outcome, retained: [{ artifact, reason }] }` | ✔ |
| `vm-shutdown` | `lifecycle shutdown`, lease expiry, user-panel Shut down | `request`, `wait`, `done` | `{ name, outcome: "completed"\|"timeout"\|"unavailable"\|"superseded", finalState }` | ✗ |
| `vm-restart` | `lifecycle restart` | `shutdown`, `wait`, `start`, `done` | `{ name, outcome, finalState }` | ✗ |
| `media-acquire` | `POST /media/acquire` | `validate`, `download`, `verify`, `done` | `MediaItem` | ✔ |
| `media-verify` | large upload completion | `hash`, `done` | `MediaItem` | ✔ |
| `media-cleanup` | schedule / `POST /media/cleanup` (admin) | `scan`, `delete`, `done` | `{ removed: [], retained: [{ id, reason }] }` | ✔ |
| `host-update` | `POST /host/updates/stage`, `/apply` | §11.5 | `HostUpdateRecord` | itself serialized (§7.4) |

Job results never contain secrets; the one-time channel stays reserved for `create-vm`
and token rotation is a synchronous route.

### 7.2 Progress

Text progress lines and SSE events are unchanged (`progress`, `state`). Additions:
`Job.Phase` (latest phase name, persisted with each snapshot) and a third SSE event
`phase` (`{ at, phase }`) emitted on every phase change. Clients that ignore `phase`
see exactly today's stream. Progress lines for child jobs never contain media URLs
beyond the host, typed console text, or checksums of auxiliary media.

### 7.3 Idempotency / operation keys

| Rule | Detail |
|---|---|
| Carrier | header `X-Construct-Operation-Key: <key>` on any job-starting or retryable synchronous request, or body field `operationKey` (header wins). 8–128 chars of `[A-Za-z0-9._:-]`. |
| Fingerprint | `SHA-256` of `route + "\n" + canonical JSON of the body` (keys sorted, `operationKey` removed, numbers as written, strings NFC). Stored with the key. |
| Scope and acceptance | `(owner, kind, key)` unique in `job_operation_keys`. Acceptance is **one transaction through one seam**: the endpoint builds an `AdmissionPlan` (key row, VM/media/upload rows, references, the `ReservationRequest`, the cascade acceptance, the **Queued job row** with a pre-generated id, and the fence to apply) and calls `IAdmissionStore.AdmitAsync(plan)`. The SQLite implementation runs the plan in one `IMMEDIATE` transaction under the ledger gate (the same capacity rules as `TryReserveAsync`, computed by the capacity pair's pure `CapacityMath` and inserted through the same SQL helper); the in-memory one runs it under one lock. Nothing is visible before commit; any refusal rolls everything back and answers with the outcome's problem code. **Gate handle first:** the endpoint filter of every gated route takes the maintenance-gate handle (`TryEnter`) *before* building the plan; the handle is passed to `IPersistedJobRunner.StartPersistedAsync(job, handle, work)` and released only when the job is terminal, so drain cannot cross the boundary between a committed acceptance and a running job (§7.4). If the runner cannot start the persisted job (an in-process failure after commit), `MarkStartFailedAsync(jobId, error)` marks the job failed and **every** fence, reservation and row the plan created is recovered by the crashed-job rules (§4.4 evidence, §8.8 tombstone and retry) — never by an ad-hoc delete, because a cascade fence or a cleared token hash is not "undone" by deleting rows. |
| Crash between commit and start | the Queued job row exists without a runner; `Bootstrap.MarkInterruptedAsync` (existing) marks it failed at startup, its reservations become orphans and are resolved per §4.4 (resource evidence, never time alone), and the enqueue-crash rule of §4.4 removes a child row only when every liability is confirmed absent. |
| Primary hook | `POST /vms` builds the same kind of plan (`VmToInsert` with the primary allowance, disk + RAM + CPU reservation, the Queued `create-vm` job) and calls `AdmitAsync` then `StartPersistedAsync` with the **unchanged** `VmJobs.CreateAsync` body; the existing `AddAsync` + `SubmitAsync` pair is replaced only in that endpoint (integrator), the provisioning algorithm is untouched. `DELETE /vms/{primary}` without children uses a plan with the fence and the Queued `remove-vm` job. |
| Replay | same owner + kind + key + **same fingerprint** → `200 { jobId, replayed: true, …the original accepted body… }` for jobs, or the stored `responseJson` for synchronous mutations, whatever the job's state now. Same key with a different fingerprint or target → `409 operation-key-conflict { jobId, target }`. Authorization is re-evaluated on replay: a caller that may no longer act on the target gets `403`, not the stored answer. |
| Synchronous **database-only** mutations (`renew`, `sharing`, `overrides`, `allowance`) | `IAdmissionStore.MutateAsync(key, scope => …)`: the key row (`Completed`, with `responseJson`) and the mutation commit in **one** transaction. A crash before commit leaves nothing (the retry executes once); after commit the retry replays the stored response. A renewal can therefore never be applied twice, and a sharing replay never overwrites a newer decision (the second call with a *different* key and fingerprint is a new decision; the same key replays the old response). |
| Synchronous **external** mutations (`start`) | acceptance: the key row is inserted `InFlight` with `intentJson = { lifetime, expectedLeaseVersion, activationBase: acceptedAt }` and `powerGeneration = <the VM's current generation>` in the **same transaction** as the pending reservation (`AdmitAsync` with a key-and-reservation-only plan). Then the hypervisor call. Then completion through `MutateAsync(key, scope => …)` in **one** transaction: `BumpPowerGenerationAsync(vm, intent.generation)` (false ⇒ `VersionConflict`, nothing written), lease activation with the intent's lifetime **from `activationBase`** (never from a later "now", so a delayed retry cannot extend useful wall-clock lifetime), `ConfirmReservationsAsync`, `CompleteOperationKeyAsync(response)`. Replay of an `InFlight` key **reconciles** under the VM gate, in this order: (1) generation moved past the intent → `409 operation-key-conflict { reason: "power-state-changed" }` (an intervening stop, save or external transition; nothing is restarted); (2) generation unchanged and VM `Running` → complete exactly as above; (3) generation unchanged and VM `Off`/`Saved` (the start never landed): **first** `activationBase + lifetime <= now` → the intent is already due: complete the key with `409 intent-expired`, release its reservations, boot nothing (a child is never started just to be expired); **then** re-check the intent's reservation rows — present → proceed; swept by the orphan rule of §4.4 → **re-admit** inside the same `MutateAsync` (`IAdmissionScope.ReserveAsync` with the intent's amounts under the current user and host policy; refusal → the key is completed with that `409 capacity-exhausted`/`capacity-unavailable` response and nothing is started); only with reservations in hand re-issue the start and complete; (4) VM `Unknown` → `409 vm-state-unknown`. It never answers `already-running` for its own intent and never confirms reservation ids that no longer exist. |
| Job-issued start of `child-create` (`start` phase) | the job (owner: child-vm jobs pair) inserts an `InFlight` start intent under the derived key `<jobId>:start` **before** the hypervisor call (`TryInsertAsync`; `Replay` on re-entry returns the original row with its `activationBase` and `powerGeneration`), issues the start, then completes it exactly like the synchronous route (`MutateAsync`: bump generation, activate from the intent's `activationBase`, confirm reservations, complete the key). A job re-entered after a crash therefore follows the §7.3 replay order for its own intent (generation moved ⇒ the job fails with `power-state-changed`; Running ⇒ complete; Off ⇒ re-admit if swept and re-issue; already due ⇒ `intent-expired`). `InMemoryOperationKeyStore`/`SqliteOperationKeyStore` are the fakes/stores; no additional job column exists for this. |
| Job-issued start of `vm-restart` (`start` phase) — **preserve-existing-lease path** | the intent under `<jobId>:start` exists **only** as the power-generation fence (a re-entered restart must not restart a VM somebody stopped meanwhile): `intentJson = { restart: true }`, no lifetime. Completion = `MutateAsync`: bump generation and complete the key — **no** lease activation, **no** `ActivatedAt`/`ExpiresAt` write, **no** `Lease.Version` bump, **nothing to confirm** (RAM/CPU stay `held` through the intermediate Off, §4.3). Due checks keep using the original `ExpiresAt`; a restart whose lease is already due is refused before its shutdown phase (`409 lease-due`, §8.7) and never becomes a renewal. Re-entry under the VM gate, in this order: (1) generation moved ⇒ the job fails `power-state-changed` (the hold is released only if the VM is observed Off); (2) Running ⇒ complete the key; (3) Off with generation unchanged (the intermediate Off was persisted, then the service crashed; `Bootstrap` marked the job failed, so §4.4 may already have **released** the held RAM/CPU as an external stop, and the generation can still be unchanged) ⇒ **before any hypervisor call**: (a) the original `ExpiresAt` is already due ⇒ the job fails `lease-due`, nothing is started, the VM stays Off (the lease's own expiry rules apply); (b) re-check the RAM/CPU rows inside the same `MutateAsync` — still `held` ⇒ proceed; released ⇒ **re-admit** through `IAdmissionScope.ReserveAsync` with the VM's amounts under the current user and host policy, refusal ⇒ the job fails with that `capacity-exhausted`/`capacity-unavailable` and the VM stays Off; (c) only with holds in hand re-issue the start and, on observed Running, confirm any newly pending rows, bump the generation and complete the key. The lease is never written on this path. |
| Stable targets | generated names/ids derive from `owner + key` (§1.7, §6.1), so a replay after a lost response resolves to the same child or media item. |
| Composite CLI operations | the CLI derives sub-keys `<key>:install`, `<key>:aux`, `<key>:create` so an install upload, an auxiliary upload and the create are three keys that never alias (§9.5). |
| Retention | `Completed` rows are kept while the job is non-terminal and for 24 h after `completed_at`; `InFlight` rows (job-less synchronous intents included) are **never** swept by age — they are resolved to `Completed` or to a recorded conflict by the replay path or by reconciliation (§5.3b), and only then age out. Swept by `media-cleanup`'s tick. A retry that wants a **new** attempt after a terminal failure uses a new key. |
| Who implements the atomic completion | the integrator's `SqliteAdmissionStore` implements `IAdmissionScope` over the per-pair SQL helpers (`SqliteVmRepository.UpdateLeaseInTransaction`, `SqliteCapacityLedger.ConfirmInTransaction`, `SqliteOperationKeyStore.CompleteInTransaction`, `SqliteAuditLog.AppendInTransaction`); the in-memory store does the same under its lock. `MutateAsync` outcomes: `Accepted`, `Replay` (key already `Completed`: the stored response is returned, the mutation is not run), `KeyConflict` (fingerprint/target differ), `VersionConflict` (a compare-and-set inside the scope returned false: nothing written). |
| Without a key | behaviour is today's: each request starts a job; the CLI always sends one. |

### 7.4 Maintenance / drain gate (atomic handles)

`IMaintenanceGate` (Core) with states `open → draining → maintenance → open`. Gated
work is admitted by `TryEnter(kind, operationId, vmName)` returning a **handle**; the
handle is taken in the same synchronous step that enqueues a job (inside
`InProcessJobEngine.SubmitAsync`, released when the job reaches a terminal state) or that
starts an inline host mutation (a chunk write, a hardware/media script, an ISO catalog
build triggered by a job), and released when it ends. `DrainAsync` flips the state to
`draining` **first** and then waits for `LiveHandles == 0`; because admission and the
counter are one atomic step, no work can slip in behind the zero check.

| State | Gated work (✔ in §7.1, plus chunk writes, hardware/media mutations, catalog builds) | Everything else |
|---|---|---|
| `open` | admitted | run |
| `draining` | new admissions refused `503 maintenance { phase: "draining", retryAfterSeconds, updateId }`; live handles finish; the update job waits up to `updates.drainTimeoutMinutes`, then fails `applyFailed { reason: "drain-timeout", blockingJobs }` and reopens | run normally: power/lifecycle, forwards, acks, heartbeats, guest reports, console, reads, `GET /media/uploads/{id}` (an open upload is not live work; its next chunk waits for the service to come back and resumes) |
| `maintenance` | refused | **every mutation** (forwards, heartbeats, lifecycle, expiry jobs included) refused `503 maintenance { phase: "maintenance" }` with `Retry-After`, **except the two recovery routes** `POST /host/updates/resolve` and `POST /host/updates/apply` (resume of an `interrupted` row), which carry the explicit `MaintenanceExempt` metadata and are admin-only — there is no general admin bypass, and nothing else is exempt; reads and `GET /health` answer; the window lasts from hand-off until the process stops (≤ 30 s by construction, §11.5) and, in a restarted binary, until the update is resolved (§11.7) |

"Not gated" therefore means "not gated by draining". The maintenance window is a full
mutation freeze; the lease scheduler and forward reconciliation simply skip ticks while
the state is `maintenance` and resume afterwards (an expiry that falls into the window
runs on the next tick).

**Serialization of updates:** `IHostUpdateStore.TryStartAsync` inserts a row only when no
non-terminal row exists, so staging cannot overlap an apply and two applies cannot race
(`409 update-in-progress`).

**Out-of-process host work (decided here: one cross-process lock, released at
hand-off).** `IHostLock` (§13.1) is a file opened exclusively (`FileShare.None`) under
`DataDir`. `admin.lock` is held by every **mutating** admin CLI verb (`iso build/prune`,
`users add/remove`, `tokens issue/revoke-all`, `forwards reconcile`) for the entire
operation — the CLI waits up to 30 s for it, then exits 1 with "another administrative
operation or an update is running" — and re-reads `host_config.maintenance` **after**
acquiring the lock (no check-then-start window). **Read-only verbs** (`users list`,
`host status`, `iso status`, `db check`) take no lock and ignore the marker; `db check`
opens the database read-only (`Mode=ReadOnly`, `PRAGMA quick_check`), which is what lets
the updater run it while it holds the lock itself. Ownership sequence:

| Step | Holder of `admin.lock` | Protection of the gap |
|---|---|---|
| `drain` | the service acquires it (bounded by the drain timeout; a CLI operation in flight blocks drain until it ends) | – |
| `handoff` steps 1–2 | the service, while it writes the durable `maintenance` marker and `handoff.json` | – |
| `handoff` step 3 (§11.5) | the service **releases** it right after the marker is durable, before the gate flip (step 4) and the task launch (step 5) | the marker: any CLI that now takes the lock re-reads `maintenance` and refuses |
| `stop` … `commit`/rollback | the updater acquires it (with `updater.lock`) **before** `stop`, so the service it stops never holds it | the updater is the only writer; `db check` needs no lock |
| after `commit` | released by the updater; the reopened service takes it only for its own future drains | – |

`Install-ConstructHost.ps1 -IsoBuildOnly` goes through the CLI and inherits the rule.
PC-to-primary provisioning, guest SSH activity and Hyper-V VMs themselves are
never gated.

### 7.5 Job ownership, initiator and read rules

`Job.Owner` = effective owner user (accounting, audit, one-time secret);
`Job.Initiator` = the principal that submitted it (`null` on existing rows = the owner).
`JobReader` policy: admin, or `Owner` matches the user, or `Initiator` matches the caller
(a user by name; a VM token by `vm:<name>`). Cancel: the same set. The one-time create
secret is handed only to owner/admin (initiator is the owner for `create-vm` anyway). A
shared caller keeps reading and cancelling the jobs they initiated after sharing is
revoked (there is no secret in them); they never see another initiator's jobs. `GET /jobs`
lists by the same rule with filters `kind`, `state`, `vm`, `since`, `limit` (≤ 200).

## 8. API contract

Conventions (existing, restated): prefix `/api/v1`; JSON camelCase; enums as camelCase
strings; every error is an RFC 7807 problem document. New in this delivery: every problem
produced by a **new** route, and every new refusal added to an existing route, carries an
extension member `code` (kebab-case, table in §8.17) and, where stated, structured
extension members. Existing problem documents keep their current bodies (adding `code` to
them is allowed, changing `title`/`detail` is not). Timestamps are ISO 8601 UTC.

`202` answers are `{ jobId, replayed?: bool }` plus at most one additive id named in the
route's row (`mediaId`, `updateId`); the existing `JobAcceptedResponse` gains these as
optional parameters (integrator edit). `200 { …, replayed: true }` is the replay of a
synchronous mutation (§7.3).

Request DTOs follow `Contracts/Requests.cs` style: every field nullable, validated
explicitly. New DTOs live in per-feature files (`Contracts/DelegationContracts.cs`,
`MediaContracts.cs`, `ConsoleContracts.cs`, `HostAdminContracts.cs`, `UpdateContracts.cs`,
`NetworkContracts.cs`). The four **existing** response records that must grow
(`WhoAmIResponse`, `VmResponse`, `ForwardResponse`, `JobResponse`, plus
`JobAcceptedResponse`) are edited by the integrator only, with additive optional
parameters that default to `null`; pairs never touch `Requests.cs`/`Responses.cs`.

### 8.1 Discovery and identity

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /health` | anonymous (**decided here**, §0.1) | – | `200 HealthResponse` — the **reduced** body without a credential; the full body (adds `commit`, `packageVersion`, `installedAt`) for any authenticated principal, VM tokens included, **and** for a loopback request presenting `Authorization: UpdateHandoff <healthToken>` while the service is inside an update's maintenance window (§11.5 `health`): the token is the random secret of the SYSTEM-only `handoff.json`, compared in fixed time, so no bootstrap secret has to be retained. The `UpdateHandoff` principal satisfies **no** other policy (it has no `KnownUser` claim and no VM claim); the scheme is registered only while a handoff for this build exists | – |
| `GET /whoami` | `AnyUserIdentity` (existing) | – | `200 WhoAmIResponse` + additive fields `enabled: bool?`, `effective: EffectiveAllowanceResponse?`, `apiFeatures: string[]` | existing |
| `GET /vms/{name}/identity` | `VmSelfOrOwnerOrAdmin` (legacy tokens allowed, self only) | – | `200 VmIdentityResponse` | `404`, `403` |
| `GET /host/capabilities` | `UserOrPrimaryToken` | – | `200 HostCapabilitiesResponse` (§3.4) | – |
| `GET /vms/{name}/capabilities` | `ChildOperator` (owner/admin/parent token/shared) for children; `VmSelfOrOwnerOrAdmin` (primary kind only) for primaries | – | `200` (§3.4) | `404`, `403` |

```
HealthResponse            { status: "ok"|"maintenance", schemaVersion: int, schemaMinReadableBy: int,
                            apiFeatures: string[], maintenance: { phase, retryAfterSeconds, updateId? }?,
                            commit?: string, packageVersion?: string, installedAt?: timestamp }   // last three: authenticated only
EffectiveAllowanceResponse{ maxPrimaries, allowChildCreation, maxRetainedChildren, cpuBudget?,
                            ramBudgetBytes?, storageBudgetBytes?, maxChildLifetimeSeconds?,
                            allowNeverLifetime, allowSharing, allowHostForwards,
                            usage: { primaries, children, cpus, ramBytes, storageBytes } }
VmIdentityResponse        { vmName, kind: "primary"|"child", tokenKind: "legacy"|"primary"|null,
                            owner, parent?, delegation: EffectiveAllowanceResponse?  (null for legacy),
                            serviceCommit: string, apiFeatures: string[], capacityMode: "observe"|"enforce" }
```

`tokenKind` is `null` when the caller is a user (there is no token in the request) and
the caller's kind when it is a VM token. This is how `construct vm` learns whether it is
delegated (§9.2).

### 8.2 Host status and configuration (admin)

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /host/status` | `Admin` | – | `200 HostStatusResponse` | – |
| `GET /host/capacity` | `Admin` | `?refresh=true` forces a new inventory epoch | `200 HostCapacityResponse` | – |
| `GET /host/config` | `Admin` | – | `200 HostConfigResponse` (all sections of §1.5 with `source: "default"\|"stored"` and `updatedAt` per section) | – |
| `PUT /host/config` | `Admin`, audited `host.config` | `HostConfigRequest` (any subset of sections; a section is replaced whole; optional `expectedUpdatedAt` per section for CAS) | `200 HostConfigResponse` | `400 validation { field, reason }` (headroom ≥ 0, chunk size 1–64 MiB, timeouts ≥ 30 s, repository `owner/name`, public key base64 of 32 bytes, `mode` enum), `409 config-conflict` (CAS) |

```
HostStatusResponse   { version: { commit, packageVersion, installedAt, source: "release"|"installer"|"unknown" },
                       health: { hypervisor: "ok"|"unreachable", database: "ok", media: "ok"|"missing-root", inventory: "complete"|"incomplete" },
                       capacity: HostCapacitySummary, capacityMode, maintenance: { phase, since?, updateId? },
                       activeJobs: [{ id, kind, vmName?, owner, initiator?, phase?, created }],
                       leaseOverdueCount: int, unmanagedVmCount: int }
HostCapacitySummary  { epoch, observedAt, complete,
                       ram: { totalBytes, headroomBytes, reservedBytes, unmanagedBytes, physicalFreeBytes, availableBytes },
                       cpu: { logical, budget?, active, available? },
                       volumes: [{ root, totalBytes, freeBytes, headroomBytes, growthReservedBytes, availableBytes }] }
HostCapacityResponse { summary: HostCapacitySummary, problems: string[],
                       reservations: [{ id, resource, scopeOwner?, vmName?, artifact?, volume?, amount, phase, origin, operationId?, pendingUntil?, created }],
                       unmanaged: [{ name, id, state, cpus, memoryStartupBytes, memoryAssignedBytes, dynamicMemory, memoryMaximumBytes?, disks: [{ path, maxBytes, fileBytes, readable }] }],
                       perUser: [{ user, primaries, children, cpus, ramBytes, storageBytes }] }
```

### 8.3 Inventory (existing routes, additive fields)

| Method, path | Auth | Change |
|---|---|---|
| `GET /vms` | `UserOrPrimaryToken` (widened from `User`; a primary token sees its own primary and its children) | additive query `?kind=primary\|child\|all` (default `all`), `?owner=` (admin), `?parent=`. |
| `GET /vms/{name}` | children: `ChildOperator`; primaries: `VmSelfOrOwnerOrAdmin` restricted to primary-kind tokens (a legacy token still gets `403`) | `VmResponse` gains the fields below |
| `GET /vms/{name}/state` | same as `GET /vms/{name}` | unchanged body |
| `GET /vms/{name}/endpoint` | primaries: as today plus the primary token itself; children → `409 no-endpoint` | |
| `GET /vms/{name}/children` | owner/admin/`ParentDelegate` | `200 VmResponse[]` |
| `GET /vms/shared` | `UserOrPrimaryToken` | `200 VmResponse[]` of host-shared children not owned by the caller, with `shared: true` |

`VmResponse` additive fields (all present, null/empty when not applicable; **existing
fields and their order are unchanged**):

```
kind, parent?, sharing, shared: bool (true when returned to a non-owner), incarnation?,
tokenKind? (owner/admin only; null to others), childCreationClosed,
lease?: { requested, activatedAt?, expiresAt?, state, overdue: bool, version, lastAttemptAt?, lastOutcome? },
hardware?: { cpus, ramMb, diskGb, generation, secureBoot, secureBootTemplate?, tpm, bootOrder: string[], networkAttached },
media: [{ id, role, name, sizeBytes?, state, dedicated: bool }],
guest: { constructCommit?, provisionedAt?, reinstalledAt?, reportedAt?, provenance, lastAttemptAt?, lastAttemptOutcome? },
observed: { createdAt?, lastBootAt?, addresses: [{ address, family, source, observedAt, verified }], storageProblem? },
reservations: { ramBytes, cpus, storageBytes },
currentOperation?: { jobId, kind, phase?, initiator? },
children?: string[] (primaries only: names),
allowedActions: ChildAction[]   // exhaustive enum of §2.2 filtered for THIS caller; presentation only
```

`guest.*` values are `null` when unknown; the UI prints "unknown". `guest.provisionedAt`,
`guest.reinstalledAt` and `guest.constructCommit` are written only by `POST
/vms/{name}/guest-report` (§8.14); `observed.*` only by the service; neither overwrites the
other; an attempt (`lastAttemptOutcome = "failed"`) never changes the success facts.

### 8.4 Users and allowances (admin)

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /users` | `Admin` | – | `200 UserDetailResponse[]` | |
| `GET /users/{name}` | `Admin` | – | `200 UserDetailResponse` | `404` |
| `POST /users` (existing) | `Admin` | unchanged; additive optional `allowance: UserAllowanceRequest`; when `maxVms` is omitted **and** `allowance` is present, `maxVms = userDefaults.maxPrimaries` (**decided here**: the old shape keeps its default 0) | `201 UserResponse` (unchanged) | existing |
| `PUT /users/{name}` | `Admin`, audited `user.update` | `{ role?, enabled?, maxVms?, allowHostForwards? }` | `200 UserDetailResponse` | `404`, `409 self-demotion` (an admin cannot disable or demote themselves), `409 last-admin` |
| `GET /users/{name}/allowance` | `Admin` | – | `200 { stored: UserAllowanceRequest, effective: EffectiveAllowanceResponse }` | `404` |
| `PUT /users/{name}/allowance` | `Admin`, audited `user.allowance` | `UserAllowanceRequest` (null field = inherit default) | same as GET | `400 validation` |
| `GET /users/{name}/tokens` | `Admin` | – | `200 [{ id, label, created, lastUsed? }]` | `404` |
| `DELETE /users/{name}/tokens/{id}` | `Admin`, audited `token.revoke` | – | `204` | `404` |
| `DELETE /users/{name}` (existing) | `Admin` | unchanged: refuses while VMs are owned (children included) | | |

```
UserDetailResponse   { name, role, enabled, maxVms, allowHostForwards, created,
                       allowance: UserAllowanceRequest, effective: EffectiveAllowanceResponse,
                       vms: { primaries, children }, tokens: int }
UserAllowanceRequest { allowChildCreation?, maxRetainedChildren?, cpuBudget?, ramBudgetBytes?,
                       storageBudgetBytes?, maxChildLifetimeSeconds?, allowNeverLifetime?, allowSharing? }
```

### 8.5 Per-VM overrides (admin, restrict-only)

| Method, path | Auth | Request | Response |
|---|---|---|---|
| `GET /vms/{name}/overrides` | `Admin` | – | `200 { stored: VmOverrideRequest?, effective: EffectiveAllowanceResponse }` |
| `PUT /vms/{name}/overrides` | `Admin`, audited `vm.overrides` | `VmOverrideRequest { allowChildCreation?, maxRetainedChildren?, maxChildLifetimeSeconds?, allowNeverLifetime?, allowSharing? }` | `200` as GET; `409 not-a-primary` for children |
| `DELETE /vms/{name}/overrides` | `Admin`, audited | – | `204` |

### 8.6 Children: creation

`POST /vms/{parent}/children` — auth `ParentDelegate` (owner user, admin, or the primary
token of `{parent}` with kind `primary`); audited `child.create` with target = child name.

```
ChildCreateRequest {
  name?: string,                         // derived when absent (§1.7)
  cpus: int, ramMb: int, diskGb: int,    // all required; ramMb multiple of 2, ≥ 512; diskGb ≥ 1; cpus ≤ maxVcpusPerVm
  lifetime: string,                      // required (§5.1)
  media: { installMediaId: string, auxiliaryMediaId?: string },   // ready items the caller may attach (§6.2)
  preset?: "windows"|"linux",            // firmware hints only (§8.6.1); never fills cpus/ram/disk/lifetime
  firmware?: { generation?: int, secureBoot?: bool, secureBootTemplate?: string, tpm?: bool,
               bootOrder?: ("installMedia"|"auxiliaryMedia"|"disk"|"network")[] },
  network?: { attach?: bool },           // default true
  start?: bool,                          // default true
  operationKey?: string
}
```

Validation order and errors: `400 validation` (shape/ranges; `idlePolicy` or
`dynamicMemory` present → `400`) → `403 token-kind-legacy` → `409 not-a-primary`
(`{parent}` is a child) → `403 delegation-disabled` (owner/override
`allowChildCreation=false`) → `409 vm-deleting` / `409 parent-closed` → `403
lifetime-not-allowed` → `409 media-not-ready { mediaId, state }` / `403` (media not
attachable by the caller) → `409 unsupported-capability` (generation, template, TPM,
auxiliary, boot device, dynamic memory) → `409 name-taken` → `409 capacity-exhausted` /
`409 capacity-unavailable` (retained children, budgets, host RAM/CPU/storage; RAM/CPU only
when `start=true`) → `202 { jobId }`. Acceptance writes the VM row (`Incarnation` null
until creation), the media references, the reservations and the operation key in one
transaction (§7.3).

**Boot order (decided here).** The effective order is the requested (or preset) order
**with absent devices removed**: `auxiliaryMedia` is dropped when no auxiliary item is
attached, `network` when `network.attach=false`. An **explicit** `bootOrder` that names a
device that cannot exist (`auxiliaryMedia` without an item, `network` without an adapter)
is `400 validation`; a preset's order is filtered silently. Every order must contain
`installMedia` and `disk`.

The `child-create` job: `admit` (re-checks the parent fence under the VM gate) → `media`
(files exist and are `ready`) → `hardware` (`IChildVmDriver.CreateAsync`: Gen 2, fixed
RAM, **template before TPM**, automatic checkpoints off, `AutomaticStopAction = Save`
(today's descriptor default; the saved state is reserved per §4.2), `AutomaticStartAction
= StartIfRunning`; reads back the VM id into `Incarnation`) → `disk` (`New-VHD -Dynamic`
of `diskGb`) → `attach` (DVDs in slot order, effective boot order) → `start` (if
requested: confirm `Running`, activate lease, confirm reservations) → `done`. Failure
after `hardware` removes the VM and its disk, removes references, releases reservations
after confirmed removal, keeps the media. The job never waits for SSH, never injects
credentials, never touches the ISO catalog.

#### 8.6.1 Presets

| preset | generation | secureBoot | template | tpm | bootOrder (before filtering) |
|---|---|---|---|---|---|
| `windows` | 2 | true | `microsoftWindows` | true | `installMedia, auxiliaryMedia, disk, network` |
| `linux` | 2 | true | `microsoftUefiCertificateAuthority` | false | `installMedia, auxiliaryMedia, disk, network` |
| (none) | 2 | false | null | false | `installMedia, auxiliaryMedia, disk, network` |

Explicit `firmware.*` values override the preset.

### 8.7 Lifecycle

`POST /vms/{name}/lifecycle` — auth `ChildOperator` for children; `VmOwnerOrAdmin` for
primaries (additive: primaries may use `shutdown`/`restart` here; their `/power` route is
unchanged); audited `vm.lifecycle` with `action=`. Every action takes the VM gate; a
second call while one runs answers `409 operation-in-progress { jobId }`.

```
LifecycleRequest { action: "start"|"shutdown"|"save"|"restart", lifetime?: string, operationKey?: string }
```

| action | Child | Primary | Answer |
|---|---|---|---|
| `start` | exactly §5.3a: requires `lifetime`; resumes `saved`/`paused`/starts `off` with a new lifetime; `409 already-running` when Running | `lifetime` must be absent (`400 validation`); admission applies to primaries too (**decided here**: a primary start that would exceed host RAM must fail clearly in `enforce` mode rather than let Hyper-V fail it); a paused primary resumes without new admission | `200 { state, lease?, replayed? }` |
| `shutdown` | graceful, job `vm-shutdown` `reason=user` | same job | `202 { jobId }` |
| `save` | requires `suspend` capability; RAM released after `Saved` observed | existing behaviour | `200 { state }` |
| `restart` | job `vm-restart`; lease unchanged; RAM held; `409 lease-due` when due/overdue | same job | `202 { jobId }` |

`POST /vms/{child}/power` → `400 child-lifecycle-route`. Other errors: `409 vm-deleting`,
`409 capacity-exhausted`, `409 capacity-unavailable`, `400 lifetime-required`, `403
lifetime-not-allowed`, `409 unsupported-capability` (save).

### 8.8 Deletion and cascade

| Method, path | Auth | Semantics |
|---|---|---|
| `DELETE /vms/{child}` | `ChildOwnerOrAdmin` (shared callers `403 not-owner`) | `TryFenceAsync` (`Deleting=true`, `CurrentJobId`), job `child-delete` → `202 { jobId }`. Repeated call: if `CurrentJobId` is a **live** job → `200 { jobId, replayed: true }`; if it is a **terminal failed** job → a new `child-delete` job is started (real retry, ownership record intact) → `202`. |
| `DELETE /vms/{primary}` without children | existing `VmOwnerOrAdmin` | fence + `remove-vm` job **but** the children count is re-taken **inside** the same transaction that sets `Deleting` and `ChildCreationClosed` (`TryAcceptCascadeAsync` with an empty preview); if a child appeared meanwhile the transaction is rolled back and the answer is `409 cascade-confirmation-required` (below). A primary with no children therefore behaves as today from the client's point of view. |
| `DELETE /vms/{primary}` with ≥ 1 child, no body | `VmOwnerOrAdmin` | stores a `CascadePreview` (§13.1: random 32-hex token, `issuedAt`, `expiresAt = +10 min`, parent incarnation, every child's name + incarnation + sharing + state + diskGb + mediaCount) and answers `409 cascade-confirmation-required { children: [...], cascadeToken, expiresAt }`. Nothing is fenced by the preview. |
| `DELETE /vms/{primary}` with body `{ cascade: { token } }` | `VmOwnerOrAdmin`, audited `vm.delete cascade=n` | `TryAcceptCascadeAsync` in one transaction: token exists, not expired, parent incarnation equal, and the **current** children set (name, incarnation, sharing) equals the stored preview exactly, and no child has a live job — else `409 cascade-scope-changed { children, cascadeToken (new preview) }` / `409 cascade-token-expired` / `409 operation-in-progress`; on match: `ChildCreationClosed` + `Deleting` on the parent, `Deleting` + `CurrentJobId` on every child, parent token hash cleared, cascade row `accepted` → job `parent-cascade-delete` → `202`. |

What invalidates a confirmation: any child added, removed or re-created (incarnation
differs), any sharing change, the parent re-created, expiry. Incarnations are compared by
strict equality **including null**: a child whose incarnation was null at preview time
(create in flight) and non-null at acceptance invalidates the confirmation
(`cascade-scope-changed`); a migrated primary whose incarnation is still null on both
sides compares equal. A preview is replaced by a newer one for the same parent.

`parent-cascade-delete`: `fence` (already done) → `children` (each child under its own
VM gate: power off if running (`Stop-VM -TurnOff`; deletion is destructive by request and
distinct from D1's shutdown paths), remove VM + disk chain, forwards, media references,
dedicated media, network rules, reservations after confirmed removal; outcomes persisted
per child in the cascade row) → **only when every child is confirmed removed** →
`primary` (existing `RemoveAsync` steps) → `forwards` → `media-references` → `network` →
`done`. Any per-child failure ends the job `failed` with per-child outcomes; the parent
stays as a **tombstone** (`Deleting=true`, `ChildCreationClosed=true`, token cleared, row
present) so every remaining child keeps its parent; a repeated `DELETE /vms/{primary}`
without a body returns the current remaining children as a fresh preview, and with a
token re-runs the cascade for the remainder (children already removed are absent from the
preview). A tombstone is never garbage-collected automatically. Existing `Bootstrap`
interruption marking makes a crashed cascade job `failed`; the retry rule above resumes it.

### 8.9 Sharing, lease renewal, hardware and media of a child

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `PUT /vms/{child}/sharing` | `ChildOwnerOrAdmin`, audited `vm.share` | `{ scope: "private"\|"host", operationKey? }` | `200 { scope }` + `IAccessExposure.RevokeNonOwnerAsync` (on `host → private`) + `INetworkPolicyReconciler.OnSharingChangedAsync` | `403 sharing-not-allowed` (owner `allowSharing=false`), `400 sharing-scope-unsupported` (`selected`), `409 not-a-child` |
| `POST /vms/{child}/lease` | `ChildOwnerOrAdmin`, audited `vm.lease.renew` | `{ lifetime, operationKey? }` | `200 LeaseResponse` | `403 lifetime-not-allowed`, `409 lease-inactive` (VM not running: use `start`) |
| `PUT /vms/{child}/hardware` | `ChildOwnerOrAdmin`, audited `vm.hardware` | `{ cpus?, ramMb?, diskGb? (grow only), firmware?: {...} }` | `200 hardware` | `409 vm-not-off`, `409 template-locked` (template change after TPM init), `409 capacity-exhausted`, `409 unsupported-capability` |
| `PUT /vms/{child}/media` | `ChildOwnerOrAdmin`, audited `vm.media` | `{ installMediaId?: string\|null, auxiliaryMediaId?: string\|null, bootOrder?: [...] }` (null detaches) | `200 media[]` | `409 vm-not-off`, `409 media-not-ready`, `403` (not attachable) |

### 8.10 Media

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `POST /media/acquire` | `UserOrPrimaryToken`, audited `media.acquire` | `{ url, name?, role: "install"\|"auxiliary", expectedSha256?, dedicatedTo?, operationKey? }` | `202 { jobId, mediaId }` | `400 url-refused`, `413 media-too-large`, `409 capacity-exhausted`, `409 media-limit { maxItemsPerUser }` |
| `POST /media/uploads` | `UserOrPrimaryToken`, audited `media.upload.begin` | §6.4 | `201`/`200` | as §6.4 |
| `PUT /media/uploads/{id}/chunks/{index}` | owner/admin | octet-stream | `204` | `400 chunk-size`, `409 upload-not-open`, `409 upload-expired`, `404`, `503 maintenance` |
| `GET /media/uploads/{id}` | owner/admin | – | `200` status | `404` |
| `POST /media/uploads/{id}/complete` | owner/admin, audited `media.upload.complete` | – | `201 MediaItemResponse` / `202 { jobId, mediaId }` / `200` (idempotent) | `409 upload-incomplete`, `409 upload-not-open`, `422 checksum-mismatch`, `422 not-an-iso` |
| `DELETE /media/uploads/{id}` | owner/admin, audited | – | `204` | `404`, `409 upload-not-open` |
| `GET /media` | `UserOrPrimaryToken` (own), `Admin` (`?owner=`, all) | – | `200 MediaItemResponse[]` | |
| `GET /media/{id}` | owner/admin; shared caller gets the reduced shape when attached to a shared child | – | `200 MediaItemResponse` | `404` |
| `GET /media/{id}/references` | owner/admin | – | `200 [{ vmName, slot, created }]` | |
| `DELETE /media/{id}` | owner/admin, audited `media.delete` | – | `204` (or `202 { jobId }` when the file is held open and cleanup will retry) | `409 media-in-use { references }` |
| `POST /media/cleanup` | `Admin` | – | `202 { jobId }` | `503 maintenance` |

```
MediaItemResponse { id, owner (owner/admin only), name, role, source, sourceUrl?, state, sizeBytes?,
                    reservedBytes, sha256? (auxiliary: owner/admin only), expectedSha256?, error?, jobId?,
                    dedicatedTo?, created, readyAt?, references: int }
```

### 8.11 Connectivity / forward requests (distinct destination identity)

The existing three forward routes keep their paths, DTOs and behaviour whenever the
**target is a primary**. New behaviour exists only for **child targets** and is reached by
the caller/target relationship, evaluated by the new `ForwardRequester` policy (§12.3),
never by the legacy check.

| Method, path | Auth (new) | Request | Response |
|---|---|---|---|
| `POST /vms/{target}/forwards` | `ForwardRequester`: for a **primary** target the existing rules and code path for every existing principal (owner user, admin, the VM's own token of either kind) are **unchanged**; for a **child** target: owner user, admin, parent's primary token, shared caller | existing `CreateForwardRequest`; for a child target additionally `connectPort?` (defaults to `vmPort`) and `via?` (a primary the requester owns; required for a **user** requester when they own more than one primary; implied for a primary token). `via`/`connectPort` on a primary target → `400 validation` | existing `ForwardResponse`; **every forward of a primary target keeps today's serialized shape** (no `destination`, no verified-address requirement — the existing `IHostAddressResolver` endpoint mechanism applies). Child-target forwards carry an additive `destination: { vmName, via?, connectAddress?, connectPort, requestedBy, relationship, verified }` (`ForwardDestination`; `verified` is always `false` on Hyper-V here). The property is omitted when null, so `GET /vms/{primary}/forwards` stays flat for `construct expose`'s no-jq parser. |
| `GET /vms/{target}/forwards` | same relationship set; `?via={primary}` (owner of that primary, or its token) lists every forward whose `destination.via` is that primary — this is what the extension polls | query `?includeChildren=true` (owner/admin/parent token on a primary: its children's forwards too) and `?via=` | existing shape (+ `destination` on child-target entries) |
| `DELETE /vms/{target}/forwards/{id}` | owner/admin/parent token/the requester (`destination.requestedBy`) | – | `204` |
| `POST /vms/{target}/forwards/{id}/ack` | primary target: unchanged (owner/admin); child target: the **owner of `destination.via`** (the human whose extension holds that primary's SSH) or admin — never a VM token | existing | existing |
| `GET /vms/{child}/addresses` | `ChildOperator` | – | `200 { addresses: [{ address, family, source, observedAt, verified }], isolation: "none", network: { clientForward, hostForward, addressVerification } }` (`VmNetworkCapabilities`; empty `addresses` is a normal state) |

Rules: host target for a child ⇒ owner's `AllowHostForwards` **and**
`network.hostForwardsEnabled` (for a shared caller: the **owner's** flag, not theirs) **and**
a verified destination address — which no Hyper-V backend can provide in this delivery
(§12.5), so the answer is `409 address-unverifiable` until an allocation authority exists.
Client target for a child ⇒ tunnelled by the extension of `via`'s owner through **that
primary's** SSH endpoint to `destination.connectAddress:connectPort` (guest-reported,
`verified: false`, shown to the requester); while the child has no usable address the
forward is recorded with `status: "error"`, `message: "guest address unknown yet"` and
re-acked by the extension when an address appears. Each requester gets their own forward
row (`requestedBy`), so two shared consumers never share or overwrite an ack.
Children cannot call any forward route (they have no credential).

### 8.12 Console

All routes: `ConsoleOperator` = `ChildOperator` for children, `VmOwnerOrAdmin` (plus the
primary's own primary-kind token) for primaries. Every call re-authorizes the VM and the
session; every mutation is audited without the typed text (`keyboard kind=text chars=12`).

| Method, path | Request | Response | Errors |
|---|---|---|---|
| `GET /vms/{name}/console/capabilities` | – | `200` as `GET /vms/{name}/capabilities`.console | |
| `POST /vms/{name}/console/sessions` | `{ operationKey? }` | `201 { sessionId, expiresAt (now+60 s), screen: { width, height }, capabilities }` | `409 console-unavailable` (VM off, no video head), `429 rate-limited` (max 4 open sessions per VM) |
| `POST …/sessions/{sid}/renew` | – | `200 { expiresAt }` | `410 console-session-expired` |
| `DELETE …/sessions/{sid}` | – | `204` | |
| `GET …/sessions/{sid}/screenshot?width=&height=` | – | `200 image/png` (headers `X-Construct-Screen-Width/Height` = current native) | `400 validation` (1 ≤ dims ≤ current native), `409 console-unavailable { returnValue }`, `410`, `429` (> 4/s per session), `413` (byte cap) |
| `POST …/sessions/{sid}/keyboard` | `{ kind: "text"\|"key"\|"scancodes"\|"ctrlAltDel", text?: string (≤ 512 chars), keyCode?: int, press?: bool\|null, scancodes?: int[] (≤ 64 bytes) }` | `200 { accepted: true, returnValue: 0 }` | `409 console-unavailable { device: "keyboard", returnValue }`, `429` (> 50/s) |
| `POST …/sessions/{sid}/mouse` | `{ kind: "moveAbsolute"\|"moveRelative"\|"click"\|"press"\|"release", x?, y?, dx?, dy?, button?: 1\|2\|3 }` | `200 { applied: true }` or `200 { applied: false, unavailable: { device: "syntheticMouse"\|"ps2Mouse", returnValue, fallback: "moveRelative"\|null } }` | `409 console-unavailable` when neither device exists, `429` |

Semantics fixed here: `key` with `press: true` = key down, `press: false` = key up,
`press: null` = down+up (`TypeKey`); `click` = `ClickButton(button)`; `press`/`release`
= button down/up; buttons are 1-based as in WMI. Coordinates are **native pixels of the
current native resolution** (the service re-reads it per session renew); the client
scales from whatever screenshot size it requested; `x/y` outside `[0, native)` →
`400 validation`. Screenshot dimensions above native are refused before any WMI call.
Text reaches the host process through **stdin** of the WMI script, never an argument
(§13.1 `IConsoleTransport`). Sessions are in memory only; a service restart ends them
(`410`). Interactive video is never offered: `console.interactive = unsupported` and no
route exists.

### 8.13 Token rotation (D4)

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `POST /vms/{name}/token` | `VmOwnerOrAdmin` (user credential only; a VM token → `403`), audited `vm.token.rotate kind=` | `{ kind?: "primary"\|"legacy" }` (default `primary`) | `200 { vmToken, kind, issuedAt }` — plaintext once, never stored | `409 vm-deleting`, `409 child-has-no-token`, `409 not-a-primary` |
| `DELETE /vms/{name}/token` | `VmOwnerOrAdmin`, audited `vm.token.revoke` | – | `204` (hash cleared; the guest loses `expose`/heartbeat until reprovisioned) | |

Reprovision path: `Provision-AgentVM.ps1 -RotateVmToken` (new switch, off by default) calls
the route with the run's remote auth provider, receives the plaintext, and delivers it
exactly like `-VmTokenB64` today (stdin to a 0600 file). `Auto-Install.ps1`'s remote menu
gains **Reprovision (upgrade VM credential)** shown only when `GET /health` lists
`children` and the instance's `tokenKind` is `legacy`. The extension's Reprovision quick
pick gains the same item under the same condition. Without the switch the provisioner is
byte-for-byte unchanged.

### 8.14 Guest-report intake

`POST /vms/{name}/guest-report` — auth `VmSelfOrOwnerOrAdmin` (legacy and primary tokens,
self only; owners and admins may post on behalf of the PC workflow); audited
`vm.guest-report event=`.

```
GuestReportRequest { event: "provisioned"|"reinstalled"|"attempt",
                     outcome?: "succeeded"|"failed" (attempt only),
                     constructCommit?: string (7–64 hex), at?: timestamp (default now),
                     reporter: "provision.sh"|"Provision-AgentVM.ps1" }
```

| event | Effect |
|---|---|
| `provisioned` | `guest.provisionedAt = at`, `guest.constructCommit = constructCommit` (when given), `guest.reportedAt = now`, `provenance = provisioner` |
| `reinstalled` | `guest.reinstalledAt = at`, commit and reportedAt as above; `provisionedAt` untouched |
| `attempt` | `guest.lastAttemptAt = at`, `guest.lastAttemptOutcome = outcome`; success facts untouched |

`bin/provision.sh` posts `provisioned` (or `reinstalled` when `Auto-Install.ps1` passes
`CONSTRUCT_PROVISION_EVENT=reinstalled` through the existing env prefix) after
`record_timestamps`, best-effort with the VM token, only when `CONSTRUCT_SERVICE_URL` is
set; a local install posts nothing. `Provision-AgentVM.ps1` posts `attempt/failed` on a
failed run when it has a service URL (best-effort, never fatal). Children never post;
their `guest.*` stays unknown and `observed.lastBootAt` is the only boot evidence.

### 8.15 Updates (admin) — routes; semantics in §11

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /host/updates/status` | `Admin` | – | `200 HostUpdateStatusResponse` (persistent, §11.8) | |
| `POST /host/updates/check` | `Admin`, audited `host.update.check` | `{ releaseTag? }` | synchronous §11.3 steps 1–3: `200 { installed: { commit, packageVersion }, latest: { commit, packageVersion, publishedAt, releaseTag, compatible: bool, reasons: string[] }?, checkedAt }` (`compatible=false` with `reasons` is a `200`) | `502 release-source-unreachable`, `422 unsigned-manifest`, `409 signing-key-missing` |
| `POST /host/updates/stage` | `Admin`, audited | `{ releaseTag?: string, operationKey? }` (default latest) | `202 { jobId, updateId }`; the job repeats §11.3 steps 1–7 and reports any refusal as the job error with the same code string (`unsigned-manifest`, `unsupported-downgrade`, `incompatible`, `release-source-unreachable`, `payload-hash-mismatch`, `extraction-refused`, `coverage-failed`) and state `stageFailed` | pre-flight HTTP only: `409 update-in-progress`, `409 signing-key-missing` |
| `POST /host/updates/resolve` | `Admin`, audited `host.update.resolve` | `{ updateId, action: "commit"\|"abort"\|"close" }` — accepted for rows in `interrupted` **or** `recoveryFailed`; `commit` only when the running binary's commit equals `handoff.commit`, the service passes its own health gate (schema migrated, `db check` clean) **and** the installed `service/` + `scripts/` file set hash-verifies against the staged manifest with previously-owned removed files absent (§11.7); `abort` only when `RecoveryRecord.BackupComplete` is true; `close` only when the running binary's commit equals `handoff.previousCommit` and every file of the previous `install.json.files` hashes equal (the installation **is** the old one) — see §11.7 | `200 { state: "resolvedByAdmin", action }` (§11.7 fence) | `409 updater-running`, `409 update-not-resolvable { state }` (row neither `interrupted` nor `recoveryFailed`), `409 update-not-commitable { reason: "wrong-binary"\|"health-failed"\|"backup-incomplete"\|"installation-mixed" }` |
| `POST /host/updates/apply` | `Admin`, audited | `{ updateId, operationKey? }` | `202 { jobId, updateId }`. Two forms: **fresh apply** of a `staged` row (gated: `503 maintenance` while the gate is not `open`), and **resume** when `updateId` names an `interrupted` row — only that form carries `MaintenanceExempt` (§7.4) and re-launches the updater with `-Resume` after re-verifying the staged package and reading the fence | `409 update-not-staged`, `409 update-in-progress`, `409 update-not-interrupted` (resume form on a row that is not `interrupted`), `409 updater-running`, `503 maintenance` (fresh form only) |
| `POST /host/updates/cancel` | `Admin`, audited | `{ updateId }` | `200 { state }` | `409 update-not-cancellable` (past hand-off) |

### 8.16 Jobs and audit (existing routes, additive)

| Method, path | Auth | Change |
|---|---|---|
| `GET /jobs` (new) | `JobReader` | `?kind=&state=&vm=&since=&limit=` → `200 JobResponse[]` per §7.5 |
| `GET /jobs/{id}`, `/events` | `JobReader`; SSE gains `phase` events | `JobResponse` gains `phase?`, `operationKey?`, `initiator?` |
| `POST /jobs/{id}/cancel` (new) | `JobReader` | `200 { cancelled: bool }` |
| `GET /audit` | `Admin` | `?actor=&target=&action=&since=&limit=` |

### 8.17 Problem-detail codes

| Code | Status | Extensions |
|---|---|---|
| `validation` | 400 | `field`, `reason` |
| `lifetime-required`, `child-lifecycle-route`, `chunk-size`, `sharing-scope-unsupported` | 400 | |
| `url-refused` | 400 | `reason`, `address?` |
| `not-enrolled`, `user-disabled`, `delegation-disabled`, `sharing-not-allowed`, `not-owner`, `not-shared` | 403 | |
| `token-kind-legacy` | 403 | `upgrade: "POST /vms/{name}/token"` |
| `lifetime-not-allowed` | 403 | `requested`, `allowedMax`, `allowNever` |
| `not-found` | 404 | |
| `name-taken`, `vm-deleting`, `parent-closed`, `not-a-child`, `not-a-primary`, `child-has-no-token`, `no-endpoint`, `vm-not-off`, `lease-inactive`, `lease-due`, `already-running`, `config-conflict`, `self-demotion`, `last-admin` | 409 | |
| `operation-in-progress` | 409 | `jobId` |
| `capacity-exhausted` | 409 | `resource`, `scope`, `requested`, `allowed`, `available`, `reason`, `epoch` |
| `capacity-unavailable` | 409 | `problems` (inventory incomplete; enforce mode only) |
| `unsupported-capability` | 409 | `capability`, `level`, `notes` |
| `template-locked`, `guest-shutdown-unavailable`, `console-unavailable`, `address-unverifiable`, `address-conflict` | 409 | `device?`, `returnValue?`, `address?`, `reason?` |
| `cascade-confirmation-required`, `cascade-scope-changed` | 409 | `children`, `cascadeToken`, `expiresAt` |
| `cascade-token-expired` | 409 | |
| `media-not-ready`, `media-in-use`, `media-limit`, `upload-incomplete`, `upload-not-open`, `upload-expired`, `operation-key-conflict` | 409 | per §6/§7 |
| `update-in-progress`, `update-not-staged`, `update-not-cancellable`, `update-not-interrupted`, `update-not-resolvable`, `updater-running`, `unsupported-downgrade`, `signing-key-missing` | 409 | `updateId?`, `state?` |
| `update-not-commitable` | 409 | `reason`, `mismatches?` |
| `vm-state-unknown` | 409 | |
| `intent-expired` | 409 | `activationBase`, `lifetime` |
| `console-session-expired` | 410 | |
| `media-too-large` | 413 | `maxBytes` |
| `checksum-mismatch`, `not-an-iso`, `unsigned-manifest`, `incompatible` | 422 | `reasons?` |
| `rate-limited` | 429 | `retryAfterSeconds` |
| `release-source-unreachable` | 502 | |
| `maintenance` | 503 | `phase`, `retryAfterSeconds`, `updateId?` (+ `Retry-After` header) |

Job-level error strings (in `job.error`, not HTTP codes): `guest-shutdown-unavailable`,
`shutdown-timeout`, `cancelled`, `drain-timeout`, the `stageFailed` codes of §8.15
(`unsigned-manifest`, `unsupported-downgrade`, `incompatible`,
`release-source-unreachable`, `payload-hash-mismatch`, `extraction-refused`,
`coverage-failed`), and the safe descriptions of `SafeError`. Implementation: `Problems.Coded(int status, string code, string title,
string detail, object? extensions = null)` in `Infrastructure/CodedProblems.cs`; existing
`Problems` methods untouched.

### 8.18 Primary ISO catalog status (admin)

| Method, path | Auth | Response |
|---|---|---|
| `GET /host/iso-catalog` | `Admin` | `200 { mode: Iso:Mode, source: { path?, url? (host+path only), sha256Configured: bool, present: bool, sizeBytes? }, current?: { fileName, sizeBytes, builtAt, sourceSha256, bootstrapKeyFingerprint, hostnameSource }, entries: [{ fileName, sizeBytes, isCurrent, builtAt?, sidecarReadable }], lastBuild?: { at, outcome, jobId? } }` — read-only projection of `IIsoCatalog.List()` and `Iso:*` options; no mutation route (builds stay `admin iso build` / the create job's on-demand path). |

## 9. Guest CLI contract (`construct vm …`)

### 9.1 Placement and dependencies

`bin/construct` gains one dispatcher case `vm)` that `exec`s `bin/construct-vm.sh`
(found through the existing `find_helper`), exactly like `expose`. `construct-vm.sh` is
bash + `curl` + **`jq` (required)**: it prints `construct vm: jq is required (apt-get
install -y jq)` and exits 1 without it (**decided here**: the lenient no-jq parser of
`construct expose` is justified for one flat object; the child API is nested and a second
hand parser would be a bug farm). `construct expose` is untouched.

Configuration is read from `/etc/construct/config.env` with the same narrow key lookup as
`construct-expose.sh`: `CONSTRUCT_SERVICE_URL` (required; unset → "this VM is not
service-managed", exit 9), `CONSTRUCT_INSTANCE_NAME`, `CONSTRUCT_SERVICE_CA_FILE`,
`CONSTRUCT_VM_TOKEN_FILE` (default `/etc/construct/vm-token`). The token is read into a
0600 header file inside a private temp dir and passed to curl with `-H @file`, never on a
command line. `CONSTRUCT_SERVICE_TIMEOUT_SEC` (default 20) bounds each call; long waits
poll.

### 9.2 Identity discovery

Every command first calls `GET /vms/{me}/identity` (cached for the process). `tokenKind =
legacy` → the command prints the upgrade instruction (`Ask the VM owner to reprovision this
VM with -RotateVmToken, or run "Reprovision (upgrade VM credential)" in VS Code`) and exits
**9**, except `construct vm identity`, which prints the identity and exits 0. `delegation.
allowChildCreation = false` → `create` exits 4 with the service's problem `detail`.

### 9.3 Commands

| Command | Route(s) | Notes |
|---|---|---|
| `construct vm identity [--json]` | `GET /vms/{me}/identity` | prints kind, token kind, owner, delegation limits and usage |
| `construct vm create (--iso-url URL \| --iso PATH \| --media ID) [--aux-iso PATH \| --aux-media ID] --cpus N (--ram-gb G \| --ram-mb M) --disk-gb D --lifetime L [--name NAME] [--preset windows\|linux] [--secure-boot on\|off] [--secure-boot-template T] [--tpm on\|off] [--boot-order a,b,c] [--no-network] [--no-start] [--sha256 HEX] [--operation-id ID] [--no-wait] [--json]` | `POST /media/acquire` or the upload protocol (§6.4) for each local file (uploads set `dedicatedTo` = the child name, so they are removed with it), **always waits** for every media job to reach `ready` (a create cannot be submitted against media that is not ready), then `POST /vms/{me}/children`; follows the create job unless `--no-wait` | `--ram-gb G` is `ramMb = G × 1024`. All four resource/lifetime inputs are mandatory; the CLI never defaults them. Upload progress: `uploaded 512 MiB of 4.0 GiB`. Sub-keys `<id>:install`, `<id>:aux`, `<id>:create` (§7.3). |
| `construct vm list [--all-shared] [--json]` | `GET /vms?parent={me}` (+ `GET /vms/shared`) | table: name, state, lease expiry/overdue, cpus/ram/disk, sharing, current operation |
| `construct vm inspect NAME [--json]` | `GET /vms/{name}`, `GET /vms/{name}/addresses`, `GET /vms/{name}/capabilities` | full record, addresses ("no address yet" is normal), capabilities |
| `construct vm start NAME --lifetime L [--json]` | `POST /vms/{name}/lifecycle {start}` | §5.3a: starts an off VM, resumes a saved or paused one, always with the new lifetime; missing `--lifetime` is a usage error (exit 1) before any call; `already-running` → exit 5 |
| `construct vm shutdown NAME [--no-wait] [--json]` | `lifecycle {shutdown}` → job | reports `completed`/`timeout`/`unavailable` truthfully; waits by default |
| `construct vm save NAME [--json]` | `lifecycle {save}` | |
| `construct vm restart NAME [--no-wait] [--json]` | `lifecycle {restart}` → job | lease unchanged; `lease-due` → exit 5 with the renew hint |
| `construct vm renew NAME --lifetime L [--json]` | `POST /vms/{name}/lease` | owner-only; the primary token acts as owner delegate |
| `construct vm share NAME --scope private\|host [--json]` | `PUT /vms/{name}/sharing` | |
| `construct vm delete NAME --yes [--no-wait] [--operation-id ID] [--json]` | `DELETE /vms/{name}` | without `--yes`: interactive TTY → prompts for the name to be typed back; non-TTY → exit 1 with `--yes required: this deletes the VM, its disk, its saved state and its dedicated media`. Children only (a primary token cannot delete its own primary: exit 4). A failed earlier delete is retried by the same command. |
| `construct vm hardware NAME [--cpus N] [--ram-mb M] [--disk-gb D] [--secure-boot on\|off] [--secure-boot-template T] [--tpm on\|off] [--boot-order a,b,c] [--json]` | `PUT /vms/{name}/hardware` | VM must be off (`vm-not-off` → exit 5) |
| `construct vm media list [--json]` | `GET /media` | |
| `construct vm media upload PATH [--role install\|auxiliary] [--name N] [--sha256 HEX] [--dedicated-to NAME] [--operation-id ID] [--json]` | §6.4 | resumable: re-running with the same `--operation-id` resumes the open upload |
| `construct vm media acquire URL [--role …] [--name N] [--sha256 HEX] [--operation-id ID] [--no-wait] [--json]` | `POST /media/acquire` | |
| `construct vm media attach NAME (--install ID \| --aux ID) [--boot-order a,b,c] [--json]` | `PUT /vms/{name}/media` | VM must be off |
| `construct vm media detach NAME (--install \| --aux) [--json]` | `PUT /vms/{name}/media` with `null` | |
| `construct vm media delete ID --yes [--json]` | `DELETE /media/{id}` | `media-in-use` lists the references, exit 5 |
| `construct vm console NAME --screenshot FILE.png [--width W --height H]` | session + screenshot | one session per invocation, closed at exit |
| `construct vm console NAME (--type-stdin \| --type-file FILE \| --key CODE [--press\|--release] \| --scancodes 0f,8f \| --ctrl-alt-del)` | keyboard route | typed text is read **only** from stdin or a file (never an argument: argv is visible in `ps`); the file is read once and not logged |
| `construct vm console NAME (--move X,Y \| --click BTN \| --press BTN \| --release BTN \| --move-rel DX,DY)` | mouse route | `applied: false` with the fallback hint is printed, exit 5 |
| `construct vm forward NAME PORT [--to client\|host] [--label L] [--connect-port P] [--wait SEC] [--json]` | `POST /vms/{child}/forwards` as the parent's primary token (`via` = self) | prints the link like `construct expose`, but uses the `construct vm` exit-code table: 7 while the client has not opened it within `--wait`, 4 when refused, 8 when unreachable |
| `construct vm addresses NAME [--json]` | `GET /vms/{name}/addresses` | |
| `construct vm jobs [--json]`, `construct vm wait JOBID [--timeout SEC] [--json]`, `construct vm cancel JOBID` | `GET /jobs`, `GET /jobs/{id}/events`, `POST /jobs/{id}/cancel` | |

Values: sizes are integers; `--boot-order` is a comma list of `installMedia`,
`auxiliaryMedia`, `disk`, `network`; `--lifetime` as §5.1; template names as the API
enum; `on|off` for booleans.

### 9.4 Output shapes

`--json` prints exactly the API response object on stdout, nothing else on stdout. For
composite commands the object is `{ operationKey, media: [MediaItemResponse…], job:
JobResponse, vm: VmResponse? }` (`create`), `{ upload: UploadStatus, media:
MediaItemResponse }` (`media upload`). Without `--json`, a human table. Progress of a
followed job streams to **stderr** as `[HH:MM:SS] phase: text`; `--json-progress` instead
writes NDJSON to stdout: `{"event":"progress","at":…,"text":…}`, `{"event":"phase",
"phase":…}`, `{"event":"state","job":JobResponse}` (last line), so agents can consume it
line by line. Errors print the problem `title: detail (code)` to stderr.

### 9.5 Idempotent retries

Every job-starting or retryable command sends `X-Construct-Operation-Key`; the value is
`--operation-id` when given, otherwise a generated UUID printed in the JSON output as
`operationKey` and on stderr as `operation id: …`. Composite commands derive sub-keys
(§7.3). Re-running with the same id returns the same job (the CLI prints `replayed` and
continues to follow it); a `409 operation-key-conflict` (different inputs under the same
id) is exit 5 with the explanation. Network failures during the initial POST are retried
up to 3 times with the same key.

### 9.6 Exit codes

| Code | Meaning | HTTP mapping |
|---|---|---|
| 0 | success | 2xx, job `succeeded` |
| 1 | usage or local error (missing jq, bad flag, missing `--yes`, unreadable file) | – |
| 2 | request rejected as invalid | 400, 413, 422 |
| 3 | not found | 404 |
| 4 | refused | 401, 403 |
| 5 | conflict, capacity, unsupported capability, console device unavailable, key conflict | 409, 410 |
| 6 | job failed or cancelled | job `failed`/`cancelled` |
| 7 | timed out waiting (`--wait`/`--timeout`), including a client forward nobody opened in time | – |
| 8 | service unreachable or answered something unusable | transport, 5xx other than 503, non-JSON |
| 9 | credential not delegated (legacy token, no service URL) | 403 `token-kind-legacy` |
| 10 | maintenance | 503 |
| 11 | rate limited | 429 |

## 10. Extension contract

### 10.1 Module layout (the module rules under "File layout" in `extension/ARCHITECTURE.md`)

| File | Role |
|---|---|
| `extension/src/hostadmin.js` | pure logic, no `vscode`: state machine for a host's admin view, DTO → view-model mapping, cascade confirmation content, capability/feature detection, allowed-action rendering. Unit-tested under node (`extension/test/hostadmin.test.js`). |
| `extension/src/remotehost.js` | existing client gains **additive** request helpers only (`health()`, `hostStatus()`, `hostConfig()`, `isoCatalog()`, `users()`, `vms(query)`, `children(parent)`, `lifecycle(name, body)`, `deleteVm(name, body)`, `media()`, `jobs()`, `forwardsVia(primary)`, `updates*()`), one per route, same error mapping. |
| `extension/src/drivers/hyperv-remote.js` | additive: `queryChildren(instance)` and `capabilities` gains `children: true` when `/health` lists it (resolved lazily; default false). |
| `extension/src/forwarder.js` | additive branch: an entry with `destination` is tunnelled `-L <local>:<destination.connectAddress>:<destination.connectPort>` over the instance's own SSH endpoint when `destination.via` is this instance (§12.2). |
| `extension/media/hostadmin.html/.js/.css` | the webview; message protocol extended under a new `hostadmin.*` namespace. |
| `extension.js` | one command registration block and one `driverOpts` extension; no other edits. |

### 10.2 Views

| View | Where | Content |
|---|---|---|
| **Host administration** (webview panel, one per enrolled host) | command `construct.openHostAdmin` (palette) and a "Host" button in the instance picker for remote instances whose identity is admin | tabs: Overview (`/host/status`, capacity bars with `observe`/`enforce` badge, maintenance state, active jobs), VMs (`/vms?kind=all&owner=`), Users (`/users`, allowance editor, tokens), Media (primary source/patched status from `/host/iso-catalog`; child inventory from `/media?owner=all`, references, transfer states, cleanup button), Operations (`/jobs`, progress, failures, retry buttons for cleanup and failed deletes, `/audit`), Configuration (`/host/config` editor with validation problems inline, `/host/capabilities`), Maintenance (`/host/updates/*`). |
| **Minimal user view** | existing control panel, Instances card | under each `hyperv-remote` primary: its children (name, state, lease expiry or `overdue`, sharing) with exactly two actions, **Shut down** (`lifecycle shutdown`) and **Delete** (confirmation, `DELETE /vms/{child}`). No start/resume, no console, no sharing UI. Hidden entirely when `/health` lacks `children`. |
| **Host connect / register** | existing `construct.addRemoteHost` (unchanged); a new **Create first Construct VM here** button in the Host administration Overview and in the instance picker for enrolled hosts with zero own VMs | launches the existing `construct.newRemoteVm` flow (`Auto-Install.ps1` remote path does the create and the provisioning). Registration never requires a VM; the host record lives in `globalState`, nothing is written to `instances.json` until a VM exists. |

### 10.3 States

Detection order for a host, re-run on every panel open and on host switch (identity is per
host, never cached across hosts):

| Step | Signal | State shown |
|---|---|---|
| `GET /health` fails (status 0) | unreachable | **Unavailable**: "cannot reach `<host>`", retry button, last known status if any; every action disabled. |
| `GET /health` → 404 | old service | **Old service**: "This host's service predates host administration. Update it on the host (`service/host/Install-ConstructHost.ps1`); no update can be driven from here." Admin module hidden; children rows hidden; existing instance controls unchanged. |
| `health.apiFeatures` lacks a feature | partial | that tab shows "not available on this host version" instead of erroring. |
| `GET /whoami` → 401 | credential rejected | **Sign in again** (existing enrolment flow). |
| `whoami.known = false` or `enabled = false` | not enrolled / disabled | **Denied**: "`<identity>` is not enrolled (or disabled) on `<host>`; ask its administrator." Admin module hidden. |
| `whoami.role = user` | ordinary user | admin module absent (not merely disabled); minimal user view only. |
| `whoami.role = admin` | admin | full module. A later `403` on any admin call (role changed) flips the panel to the ordinary-user state immediately. |
| `health.status = maintenance` or any `503 maintenance` | maintenance | banner "Host is updating (phase …); reconnecting…"; polling `/health` every 5 s; mutations disabled; no automatic retry of a mutation without an operation key. |
| Local install (`hyperv-local`) | – | nothing of this section is rendered; the panel is pixel-identical. |

### 10.4 Cascade confirmation content

Triggered by Delete/Remove instance on a primary when `DELETE /vms/{primary}` answers
`409 cascade-confirmation-required`. Modal (VS Code `showWarningMessage` modal, or the
Auto-Install console prompt through `-ConfirmCascade`):

```
Deleting "<primary>" also deletes ALL of its <n> child VM(s), including shared ones.
Their virtual disks, saved state and dedicated media are removed permanently.

  child-a   running   private   80 GB disk
  child-b   saved     SHARED HOST-WIDE (other users may be using it)   40 GB disk

Type the instance name to confirm. (This confirmation expires at <expiresAt>.)
```

The typed name is required (existing rule for remote removal), the request carries the
`cascadeToken`, and a `409 cascade-scope-changed` re-opens the dialog with the new list.
The minimal user view's child **Delete** uses a smaller modal naming the child, its
sharing state and "disk, saved state and dedicated media are removed permanently".

### 10.5 What is absent by contract

No guest update/provision/reinstall action in the admin module (those stay in the
per-instance workflow), no host filesystem access, no local commands that assume the
service is local, no child start/resume/console for ordinary users in the panel.

## 11. Host update contract (D5)

### 11.1 Release production

New workflow `.github/workflows/host-release.yml`, triggers: `push` to `main` touching
`service/**`, `drivers/**`, `lib/**`, `bin/**`, `Provision-AgentVM.ps1`, `config/**`,
`.github/workflows/host-release.yml`, and `workflow_dispatch` guarded by
`if: github.ref == 'refs/heads/main'` (a dispatch from any other ref is a no-op job).
Steps: `dotnet test service/Constructd.sln`; `dotnet publish service/src/Constructd.Api
-c Release -r win-x64 --self-contained true`; build the payload and `SHA256SUMS`; zip;
write `manifest.json` (contains the zip hash); sign the manifest with the Ed25519 key from
the **`host-release` GitHub environment secret** (`HOST_RELEASE_SIGNING_KEY`); `gh
release create host-<commit40>` with the three assets. Release tag pattern:
`host-<40-hex commit>`; `latest` = the newest `host-*` release whose signed manifest
carries `ref = "refs/heads/main"` (the service cannot verify ancestry offline; it trusts
the signature over `commit` + `ref`, which only the workflow can produce). The ISO tool is
**not** rebuilt (D5); the payload ships `config/iso-builder.json` as is.

### 11.2 Assets, package layout and manifest (non-circular)

Three release assets:

```
construct-host-<commit7>-win-x64.zip      the PAYLOAD (never contains the manifest)
  SHA256SUMS                              sha256 of every other file in the zip, relative paths, LF
  service/                                dotnet publish output (Constructd.Api.exe, runtimes, appsettings.json)
  scripts/                                host-side subset of the repo: drivers/, lib/, bin/, config/,
                                          Provision-AgentVM.ps1, service/host/*.ps1, docs/ (text only)
  updater/Update-ConstructHost.ps1        the independent updater (Windows PowerShell 5.1)
manifest.json                             DETACHED; hashes the finished zip and its SHA256SUMS
manifest.json.sig                         Ed25519 signature over the exact bytes of manifest.json
```

Order of production: payload files → `SHA256SUMS` → zip → `sha256(zip)`,
`sha256(SHA256SUMS)` → `manifest.json` → signature. Nothing inside the zip depends on the
manifest, so the layout is producible and verifiable.

`manifest.json` (`ReleaseManifest` in §13.1):

```
{ "schemaVersion": 1,
  "commit": "<40 hex>", "ref": "refs/heads/main", "packageVersion": "2026.09.07+<commit7>", "builtAt": "…",
  "repository": "permissionBRICK/The-Construct", "releaseTag": "host-<commit>",
  "payloadAsset": "construct-host-<commit7>-win-x64.zip", "payloadSha256": "…", "sumsSha256": "…",
  "updaterPath": "updater/Update-ConstructHost.ps1", "updaterSha256": "…",
  "database": { "schemaVersion": <SqliteMigrations.SchemaVersion>, "minReadableBy": <MinReadableBy>, "breakingMigrations": [] },
  "config": { "settingsSchemaVersion": 1, "minReadableBy": 1, "requiredKeys": [], "newKeysWithDefaults": ["Constructd:HostAdmin:*"] },
  "compat": { "minInstalledCommitDate": "2026-08-01", "minSchemaVersionToUpdateFrom": 0 } }
```

### 11.3 Trust root, verification and extraction (service side)

**Signing key (decided here).** The Ed25519 private key lives only in the `host-release`
GitHub environment secret. The public key is committed as `config/host-release.pub`
(base64, 32 bytes) and printed by the workflow. `Install-ConstructHost.ps1` reads that
file from the checkout it installs and seeds `host_config.updates.manifestPublicKey`
(it never overwrites an existing stored key). Rotation: commit the new public key, then
an admin runs `PUT /host/config { updates: { manifestPublicKey } }` **before** the first
release signed with the new key; the package's own `config/host-release.pub` is
informational and is never used to verify the package that carries it. Without a stored
key every update route answers `409 signing-key-missing`; `requireSignature=false` is
honoured only when `Fake=true`.

Verification order — steps 1–3 run synchronously in `POST /host/updates/check` and again,
with steps 4–7, inside the `host-update` job's `check`/`download`/`verify` phases:

1. Release list from `https://api.github.com/repos/<updates.repository>/releases` over TLS with system roots; only assets of that repository are ever downloaded; the release's tag must match `host-<commit>` and the manifest's `commit`.
2. Download `manifest.json` and `manifest.json.sig`; verify the signature over the exact manifest bytes with the stored public key → `422 unsigned-manifest` on failure. Check `ref == "refs/heads/main"` and `repository` equals the configured one.
3. Compatibility: refuse `unsupported-downgrade` when `manifest.database.schemaVersion < installed MinReadableBy` or `manifest.config.settingsSchemaVersion < installed settings MinReadableBy`; refuse `incompatible` when `manifest.compat.minSchemaVersionToUpdateFrom > installed SchemaVersion`, when `manifest.schemaVersion` is unknown to this service, or when a key listed in `manifest.config.requiredKeys` is absent from `appsettings.Production.json` (reported in `reasons`); free space ≥ 2 × payload size + 1 GiB on both the service and data volumes. **Configuration policy (D5, decided here):** `appsettings.Production.json` is never rewritten by the service or the updater; every new key is optional with a default (`newKeysWithDefaults` is informational); a release that cannot run without a new key declares it in `requiredKeys` and is refused until the admin adds it; `settingsSchemaVersion`/`minReadableBy` follow the database rule (additive only in this delivery, `Breaking` refused by review), so rollback never needs a config restore — the file is untouched. (`check` answers these as HTTP problems / `compatible=false`; the `stage` job records them as `stageFailed` with the same code, §8.15.)
4. Download the payload to `updates\<updateId>\package.zip`; `sha256(zip) == payloadSha256`.
5. Extract with validation: every entry path is relative, contains no `..`, no drive or root, no symlink/reparse entries, total extracted size ≤ 4 × zip size, entry count ≤ 20 000; anything else aborts.
6. `sha256(SHA256SUMS) == sumsSha256`; **every** extracted file except `SHA256SUMS` itself must be listed and must hash equal; every listed file must exist; `updater/Update-ConstructHost.ps1` must hash `updaterSha256`. Coverage is therefore total: an executable, script or DLL not in the list fails verification.
7. Write `updates\<updateId>\verified.json` (`{ updateId, commit, verifiedAt, files: [...] }`); the row becomes `staged`. The resolved `commit` is pinned in the row; nothing later re-resolves `main`.

Failure at any step leaves `verification-failed.json` in the staging directory and the row
in state `stageFailed`; the running service is untouched.

### 11.4 Staging directory and install record

`<DataDir>\updates\` (`C:\ProgramData\Construct\service\updates\`, hardened like the data
dir): `<updateId>\package.zip`, `<updateId>\extracted\…`, `<updateId>\verified.json`,
`backup-<updateId>\service\`, `backup-<updateId>\scripts\`, `backup-<updateId>\constructd.db*`,
`backup-<updateId>\files.json`, `handoff.json`, `last-update.json` (recovery record,
§11.7), `updater.log`. Retention: the two most recent backups and the current staged
package; older ones removed by the updater after a successful health check, never before.

`install.json` next to the executable: `{ commit, packageVersion, installedAt,
previousCommit?, updateId?, files: [{ path, sha256 }] (every service/ and scripts/ file the
manifest listed, with its SHA256SUMS hash) }`. An installer-installed host has no `install.json` until its first successful
update; the service reports `source: "installer"` and an empty file list.

### 11.5 `host-update` job phases, drain and hand-off

| Phase | Actor | What happens |
|---|---|---|
| `check`, `download`, `verify` | service | §11.3; state `staged` |
| `drain` (apply only) | service | `IMaintenanceGate.DrainAsync(drainTimeoutMinutes)`: state `draining`, wait for `LiveHandles == 0`; timeout → `applyFailed { reason: "drain-timeout", blockingJobs }`, gate reopened |
| `handoff` | service | **in this order:** (1) `host_updates` row → `handedOff` and `host_config.maintenance` written (durable); (2) `handoff.json` written (temp + rename, with the random `healthToken`); (3) **`admin.lock` released** (the durable marker now protects the gap, §7.4); (4) gate → `maintenance`; (5) `IUpdaterLauncher.LaunchAsync`: `schtasks.exe /Create /TN Construct-HostUpdate /SC ONCE /ST <now+1min> /RU SYSTEM /RL HIGHEST /F /TR "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <staged>\updater\Update-ConstructHost.ps1 -Handoff <path>"` then `/Run` (argv through `IProcessRunner`; **decided here**: a scheduled task survives the service stop, a child process's fate under the SCM does not). If step 5 fails, the row becomes `applyFailed`, the gate reopens, the marker is cleared and `admin.lock` is re-acquired only for the next drain. |
| `stop` | updater | takes `updater.lock` and `admin.lock` (the service released the latter at hand-off, §7.4); writes `last-update.json` phase `stop`; `Stop-Service`, waits for `Stopped` (timeout 120 s; the service's `maintenance` state makes this quick) |
| `backup` | updater | copies to `backup-<updateId>\`: the files of the **previous** `install.json.files` (or, on a host without one, the whole publish dir and the scripts dir minus data/media/iso/`.construct-tools`/`keys`), plus `constructd.db`, `-wal`, `-shm`; writes `files.json` (relative path + SHA-256 of every copied file) and, **last**, `backup-complete.json` (temp + rename) and `RecoveryRecord.BackupComplete = true`. A backup directory without the marker is incomplete: before `ReplaceStarted` it is discarded and rebuilt from the untouched installation; after `ReplaceStarted` it is a `recoveryFailed` condition (§11.7). On resume, a complete backup is verified against `files.json` hashes before it is trusted. |
| `replace` | updater | writes `RecoveryRecord.ReplaceStarted = true` first (temp + rename). **List-based, never mirrored**: copies every `service/*` file of `verified.json.files` to `PublishDir` and every `scripts/*` file to `ScriptsDir`, preserving relative paths; deletes only files that are in the previous `install.json.files` and absent from the new list; never touches `appsettings.Production.json`, `*.db*`, `install.json`, or anything not on either list. Pre-flight refuses (`recoveryFailed` before any change) when a manifest path resolves outside its root, or when `PublishDir`, `DataDir`, `Media:RootDir`, `Iso:CacheDir`, `.construct-tools` or `keys` would be *written* by a listed path. Nested layouts (the documented `ScriptsDir=C:\Construct`, `PublishDir=C:\Construct\service\publish`) are therefore safe by construction: the payload's `scripts/service/host/*.ps1` lands in `C:\Construct\service\host\`, and `C:\Construct\service\publish\` is only ever written through the `service/*` list. ACLs are re-applied with `Install-ConstructHost.ps1 -AclOnly` (new switch, hardening only). |
| `start` | updater | writes phase `start`; `Start-Service`; wait for `Running` |
| `health` | updater | polls `GET /api/v1/health` on loopback (`https://127.0.0.1:<port>`, pinned to the host's certificate thumbprint from `appsettings.Production.json`) with `Authorization: UpdateHandoff <healthToken>` (the token from `handoff.json`, which only SYSTEM can read) until the **full** body answers `status=maintenance` with `commit == manifest.commit` and `schemaVersion >= manifest.database.schemaVersion`; then `constructd admin db check --json` (new verb: opens the database, `PRAGMA quick_check`, prints schema version) must exit 0 and agree on the schema version; timeout `healthTimeoutSeconds`. No bootstrap token is involved; an ordinary installation (no retained admin secret) passes the same gate. |
| `commit` | updater | writes `install.json` (new file list), then `last-update.json.outcome = succeeded` (temp + rename); the service, which has been polling the record (§11.7), reopens the gate; the updater deletes the scheduled task, prunes old backups |
| `rollback` | updater | §11.7 |

### 11.6 Health check

Healthy = the process is `Running`, the loopback `/health` handshake with the hand-off
token answers the expected commit and a migrated schema, and `constructd admin db check`
opens the database and reports the same schema version. There is no weaker fallback gate:
a host on which the handshake cannot be performed fails health. The hypervisor probe of
`/host/status` is **not** a criterion (a host with Hyper-V briefly unavailable must not
trigger a rollback of a good binary; **decided here**).

### 11.7 Durability, the new binary's maintenance window, rollback and recovery

**Startup and fence state table (both binaries; decided here).** At startup the service
reads `handoff.json`, `last-update.json` and `fence.json`, determines its identity
(`own = handoff.commit` ⇒ new binary, `own = handoff.previousCommit` ⇒ old binary) and
whether the updater is alive (`IHostLock.IsHeldByAnotherProcess("updater.lock")`):

**Precedence (decided here):** a **terminal outcome** in the record — `succeeded`,
`rolledBack`, `rolledBackWithDatabase` — is an immutable stop condition: it wins over any
fence, is never rewritten, and is checked under `updater.lock` by every resumption before
anything else (a `-Resume` of any form on a terminal record exits `already-terminal`, code
0, and touches nothing — it can never restore the database again). Below that, a fence for
the handoff's `updateId` is evaluated before the non-terminal outcomes (`recoveryFailed`,
`applyFailed`): a `commitOnly` or `closed` fence written by a resolution after an earlier
`recoveryFailed` outcome wins over that outcome. Otherwise the rows apply top to bottom.

| Handoff | Identity | Record | Updater lock | Behaviour |
|---|---|---|---|---|
| present | any | fence `commitOnly` for this update id and the running binary's commit == `handoff.commit` | any | resolved: **reopen**, row `resolvedByAdmin`; the updater may only finish `commit` |
| present | any | fence `closed` for this update id | any | resolved: **reopen** on the old binary, row `resolvedByAdmin` |
| present | any | fence `rollbackAuthorized`, no terminal outcome | any | stay in `maintenance`: the authorized rollback is pending or running; the transition out of this row is the updater writing the terminal `rolledBack*` outcome (below), after which the old binary converges to **open** on the next start |
| absent | any | – | – | normal start; rows left `handedOff`/`applying` from an earlier life become `interrupted` |
| present | any | `outcome = succeeded` | – | reopen; row `succeeded`; handoff and fence files removed |
| present | old | `outcome ∈ {rolledBack, rolledBackWithDatabase}` (terminal; any fence ignored) | – | verify the old installation by hash against the previous `install.json.files` (or the backup's `files.json` on a host without one); on match **reopen**, row mirrors the outcome, the service retires a leftover `rollbackAuthorized` fence (replaces it with `closed`, temp + rename) and removes `handoff.json`; on mismatch stay in `maintenance` with `recoveryFailed { reason: "installation-mixed" }` |
| present | any | `outcome = recoveryFailed`, no fence | – | stay in `maintenance`; row `recoveryFailed`; the admin repairs per `manualSteps` and then resolves (`resolve` accepts `recoveryFailed` rows, §8.15): `commit` on the new binary after the service's own health gate, `abort` when a complete backup exists, `close` when the old installation is verified intact by hash; anything else stays frozen (`update-not-commitable`) — a mixed installation is never reopened unverified |
| present | new | no outcome, phase ∈ {`replace`, `start`, `health`} | held | the update is running: `maintenance`; poll the record every 2 s |
| present | new | no outcome, no fence | **free** | the updater died: row `interrupted`, **stay in `maintenance`** until an admin resolves (the only exempt routes) — never reopen on a timer |
| present | old | no outcome, `ReplaceStarted = false` (phase `stop`/`backup`) | held | the updater is still working (a slow backup): `maintenance`; poll |
| present | old | no outcome, `ReplaceStarted = false`, fence `closed` or none | **free** | the updater died before touching the installation: the service takes `updater.lock`, writes `fence.json { disposition: closed }` for this update id, row `interrupted`, marker cleared, **reopen**, releases the lock. `closed` carries **no rollback authority**: a later `-Resume` of any form refuses (`superseded`, exit 4) and the pre-replacement backup is discarded as stale; the staged package can only be applied again from scratch by a fresh `apply`. |
| present | old | no outcome, `ReplaceStarted = true` | any | a mixed installation is running the old binary (partial replace or partial rollback): stay in `maintenance`, row `interrupted`; only `abort` (complete backup) or a hash-verified `close` can resolve it — `commit` is refused (`update-not-commitable { reason: "wrong-binary" }`) |
| present | neither commit matches | – | – | stay in `maintenance`, row `interrupted`, `recoveryRecord` exposed; admin resolution required |

Rule: **no binary reopens writes while an updater that could still roll back is alive or
resumable** — reopening happens only after the record says the update is over, or after
the service itself has fenced the updater under `updater.lock`, or after an admin
resolution that does the same. `POST /host/updates/apply` on an `interrupted` row
(resume) and `POST /host/updates/resolve` are the two `MaintenanceExempt` routes (§7.4).

Because the new binary accepts **no mutation** until the updater has written `succeeded`
or an admin has fenced the updater, a rollback that restores the database loses nothing a
user did; the `lostJobs` list in the record is therefore empty by construction and is kept
only as an assertion.

**Updater lock and fence dispositions (decided here).** The updater holds the
cross-process lock `updates\updater.lock` (`IHostLock`, exclusive file handle) from its
first phase to its last, and re-reads `updates\fence.json` under that lock before
**every** phase, in particular before `stop`, before `replace` and before any rollback. A
fence is scoped to one `updateId` (a fence for another id is ignored) and has exactly one
of three dispositions, which are the **only** source of recovery authority (no actor
string is ever interpreted):

| Disposition | Written by | Updater may | Updater must refuse | Service gate |
|---|---|---|---|---|
| `closed` | the old binary at startup (dead updater, `ReplaceStarted = false`) | nothing: exit `superseded` (4) | stop, replace, rollback, any `-Resume` | open |
| `commitOnly` | `resolve { action: "commit" }` | finish the `commit` phase only (install.json, task cleanup, prune) | replace, rollback, `-Resume -Rollback` | open (reopened by the resolution and kept open across restarts, table above) |
| `rollbackAuthorized` | `resolve { action: "abort" }` | rollback (`-Resume -Rollback`) | replace, commit | `maintenance` until the rollback outcome is recorded |

`POST /host/updates/resolve { updateId, action: "commit"|"abort"|"close" }` (admin,
`MaintenanceExempt`, rows `interrupted` or `recoveryFailed`) takes `updater.lock` (`409
updater-running` when a live updater holds it — a merely slow updater therefore cannot be
raced), checks the action's precondition — `commit`: the running binary's commit equals
`handoff.commit`, the service passes its own health gate (migrated schema, `db check`
clean), **and the installed file set verifies against the staged signed manifest**: every
`service/*` and `scripts/*` file of `verified.json.files` exists at its target path with
its `SHA256SUMS` hash, and every file of the previous `install.json.files` that is not in
the new list is absent — preserved paths (`appsettings.Production.json`, `*.db*`,
`install.json`, data/media/ISO roots, `.construct-tools`, `keys`, `settings.json`,
`projects`) excluded; any mismatch ⇒ `409 update-not-commitable { reason:
"installation-mixed", mismatches: [...] }` and the gate stays `maintenance` (a new
executable over old scripts or a stale lazily-loaded DLL passes HTTP/DB health and must
still be repaired or rolled back first); `abort`: `RecoveryRecord.BackupComplete`; `close`: the running binary's
commit equals `handoff.previousCommit` **and** every file in the previous
`install.json.files` hashes equal (an installer-installed host without `install.json`
cannot `close` and must `abort` or repair by hand) — refusing otherwise with `409
update-not-commitable { reason }`; then writes the fence (temp + rename) with the
corresponding disposition (`commit` → `commitOnly`, `abort` → `rollbackAuthorized`,
`close` → `closed`), records the row as `resolvedByAdmin` with the action, reopens the
gate for `commit`/`close` or keeps `maintenance` for `abort`, and releases the lock. An
admin can therefore never declare a half-replaced tree good: `commit` needs the new
binary, `close` needs the verified old one.
Every `-Resume` (also reachable through the exempt `apply` route) takes `updater.lock`
first and obeys the table, so a rollback can never discard mutations accepted after a
`commit` resolution or after a startup `closed` fence.

| Situation | Action |
|---|---|
| `start` or `health` fails and `manifest.database.breakingMigrations` is empty | stop service; restore the backed-up files and delete the files the new list added; **keep the database** (additive migrations are readable by the previous binary by §1.4 rule); then the completion sequence below with outcome `rolledBack`. |
| `start`/`health` fails and a breaking migration was applied | as above **and** restore the database backup; completion sequence below with outcome `rolledBackWithDatabase`. |
| Rollback itself fails | leave everything in place, outcome `recoveryFailed`, `last-update.json` names the phase, the backup path and the exact manual steps; the scheduled task is left (disabled) for inspection; the updater exits non-zero. |
| Rollback completion sequence (every rollback path, including `-Resume -Rollback` under a `rollbackAuthorized` fence), all under `updater.lock` | (1) restore files (and the database when required) from the complete backup; (2) verify the restored installation by hash against the backup's `files.json`; (3) start the service; (4) health check against `previousCommit` through the same loopback handshake; (5) **only after (2)–(4) pass** write the terminal outcome `rolledBack`/`rolledBackWithDatabase` (temp + rename) — from this instant every resumption is a no-op by the precedence rule; (6) replace the fence with `closed` (temp + rename); (7) exit. Any failure in (1)–(4) writes `recoveryFailed` instead — a terminal **success** outcome is never written for a service that did not start or pass health, so the terminal guard cannot turn a failed rollback into a reported success. A crash between (5) and (6) is harmless: the outcome already wins over the stale `rollbackAuthorized` fence, and the service retires the fence itself at startup (table above). The same terminal guard applies to `succeeded`: after `commit` writes it, a resumption exits `already-terminal`. |
| Interrupted updater (power loss) | `Update-ConstructHost.ps1 -Handoff <path> -Resume` (run by an admin, or by `POST /host/updates/apply` on an `interrupted` row, which re-verifies the staged package and re-launches the task) first takes `updater.lock` and reads `fence.json`; then continues **from the recorded phase**: `stop`/`backup` restart cleanly; a backup marked complete is reused and hash-verified; `replace` is re-run idempotently from the list. Once `ReplaceStarted` is recorded, a backup that is not marked complete is **never** rebuilt from the (now mixed) installation: the run ends `recoveryFailed` with manual steps. |
| Migration compatibility | automatic binary-only rollback is offered only when `manifest.database.minReadableBy <= previous.schemaVersion` (always true for additive-only migrations); otherwise the database backup is restored too. The manifest states both numbers; the updater does not guess. |

`last-update.json` (the local recovery record, readable with the service down):
`{ updateId, commit, previousCommit, phase, phaseAt, outcome?, error?, backupPath,
stagedPath, healthAttempts, lostJobs: [], manualSteps: string[] }`. The updater rewrites it
at every phase transition (temp file + rename) and is the **only** writer of `outcome`;
the service never infers success or failure from `install.json`.

### 11.8 Persistent update status API

`host_updates` table (migration 600): `id TEXT PRIMARY KEY, commit TEXT NOT NULL,
release_tag TEXT, package_version TEXT, state TEXT NOT NULL, phase TEXT, phases_json TEXT
NOT NULL, started TEXT NOT NULL, finished TEXT, error TEXT, previous_commit TEXT, actor
TEXT NOT NULL, blocking_json TEXT NOT NULL`.

```
HostUpdateStatusResponse {
  installed: { commit, packageVersion, installedAt, previousCommit?, source },
  current?: { updateId, commit, state: HostUpdateState, phase?, phases: [{ name, at, outcome?, error? }],
              started, finished?, error?, blockingJobs: [] },
  history: [ …last 10 rows… ],
  recoveryRecord?: last-update.json contents when its outcome is not succeeded,
  latestKnown?: { commit, packageVersion, publishedAt, checkedAt },
  signingKeyConfigured: bool }
```

`HostUpdateState` = `checking, staged, stageFailed, draining, handedOff, applying,
succeeded, applyFailed, rolledBack, rolledBackWithDatabase, recoveryFailed, interrupted,
cancelled, resolvedByAdmin` (the compiled enum of §13.1). At startup the service reconciles rows with the
records exactly as §11.7 describes.

### 11.9 What is preserved

`appsettings.Production.json` (never written; new keys are optional with defaults), the
certificate (Windows store, untouched), `constructd.db` (migrated additively), the media
and ISO directories, `.construct-tools\` (ISO executable; `config/iso-builder.json` is
replaced with the package's copy through the scripts list and the tool is re-resolved
lazily on next use), `keys\`, `settings.json`, `projects\`, the service registration
(`binPath` unchanged because the executable path is unchanged), firewall rules, forwards
(`netsh` rules; reconciled at startup), VM registrations, tokens, users, and every
Hyper-V VM, which keeps running throughout.

## 12. Network seams

### 12.1 Interfaces (Core, stage 1; signatures in §13.1)

| Interface | Purpose | Hyper-V implementation now | Fake |
|---|---|---|---|
| `IGuestAddressProvider` | guest-reported addresses (untrusted), host-authoritative adapter facts (MAC, MAC-spoofing setting), the host neighbor table, the guest subnets and the host's own addresses (all for §12.5) | `HyperVGuestAddressProvider`: `Get-VMNetworkAdapter -VMName` → `IPAddresses` (KVP, **conditional**), `MacAddress` and `MacAddressSpoofing` (host-authoritative), `Get-NetNeighbor -InterfaceAlias "vEthernet (*)"` for the neighbor table, `Get-NetIPAddress -InterfaceAlias "vEthernet (*)"` for subnets — all through the driver contract function `Get-ConstructVmAddresses` in `HyperVLocal.ChildVm.ps1` | `FakeGuestAddressProvider` (dictionaries for reported addresses, adapters, neighbors, subnets) |
| `IAccessExposure` | validate a destination and materialize an authorized request as a client forward, a host forward or refuse; revoke per requester / per sharing change; list by `via` | wraps `NetshPortForwardManager` for host forwards (`connectaddress` = the verified guest address) and `IForwardStore` for client forwards with `ForwardDestination` | `InMemoryAccessExposure` over `InMemoryPortForwardManager` |
| `INetworkPolicyReconciler` | apply/reconcile/revoke access rules on create, sharing change, address change, delete and periodically | `NoIsolationNetworkPolicy`: records intended rules in `network_rules`, enforces nothing, `IsolationLevel = "none"` | same class (pure bookkeeping) |
| `IHostNetworkPolicy` | host-level switches | reads `host_config.network` | same |

### 12.2 Modes on Hyper-V in this delivery

| Mode | Status | How |
|---|---|---|
| Client forwarding to the requester's PC | **supported** (child destinations unverified) | for a primary: existing path. For a child: forward recorded with `destination = { vmName: child, via: <requester's primary>, connectAddress, connectPort, requestedBy, relationship, verified: false }`; the extension of `via`'s owner polls `GET /vms/{via}/forwards?via={via}` and opens `ssh -L <local>:<connectAddress>:<connectPort> <via alias>` over **that primary's** existing SSH endpoint, then acks. Requires the child to be reachable from `via` (same switch; the sanity rules of §12.5 apply; not claimed as isolation or as verified ownership). |
| Host forwarding | **supported for primaries** (unchanged); **unsupported for children** in this delivery | a child's destination address cannot be verified (§12.5) → `409 address-unverifiable`; owner `AllowHostForwards` and `network.hostForwardsEnabled` stay the policy switches for when an allocation authority exists. |
| Direct guest address | **conditional, reported only, never verified** | `GET /vms/{child}/addresses`; the caller (a primary on the same switch) dials it. No promise beyond "this is the address the guest reported for this VM's adapter"; `verified` is always `false` on Hyper-V. An empty list is a normal state (no OS, no integration services, no adapter). |
| Parent ↔ child bidirectional rules, shared-consumer rules | **recorded, not enforced** | `network_rules` rows `{ vm, peer, kind: parent-child\|shared-consumer, state: intended }`; `isolation: "none"` everywhere. |
| Firewall/enterprise adapter, Proxmox | **unsupported** | interface only; documented inputs: rule set per VM (peers, ports), events `VmCreated`, `VmDeleted`, `SharingChanged`, `AddressChanged`. |

### 12.3 Distinct destination identity and relationship resolution

`ForwardDestination` (§13.1) is the destination stored on a child-target forward; the
existing `ForwardTarget Target` (`client`/`host`) is untouched and keeps meaning *where
the forward is materialized*. `ForwardRequest` names the requester, the relationship, the
target VM, the `via` primary and the ports.

Authorization (`ForwardRequesterHandler`): **when the target is a primary**, the
existing `VmSelfOrOwnerOrAdmin` check runs unchanged for every existing principal type
(owner user, admin, the VM's own token of either kind) and the request takes today's code
path end to end — no `destination`, no `via`, no address verification, the same
`ForwardResponse` bytes. **When the target is a child**, resolve the relationship in this
order and stop at the first that matches — `Admin`, `Owner` (user owns the parent),
`Parent` (primary token of kind primary whose VM is the target's parent), `Shared` (target
has `Sharing=Host`, owner enabled, requester is a user or a primary-kind token). A legacy
token never matches a child. Then policy: host target ⇒ owner's `AllowHostForwards` ∧
`hostForwardsEnabled` ∧ verified address (§12.5: never on Hyper-V here); client target ⇒ `via` must be a primary
the requester owns (user) or the requester itself (primary token); for `Admin` on somebody's
child, `via` must be named explicitly. Children can match nothing: they hold no credential.

### 12.4 `network_rules` (migration 700) and `forwards` columns

`network_rules (id TEXT PRIMARY KEY, vm_name TEXT NOT NULL COLLATE NOCASE, peer TEXT NOT
NULL COLLATE NOCASE, kind TEXT NOT NULL, state TEXT NOT NULL, created TEXT NOT NULL,
updated TEXT NOT NULL)`; `forwards` gains `destination_vm`, `destination_via`,
`destination_connect_address`, `destination_connect_port`, `requested_by`, `relationship`
(all NULL for today's rows and for every primary-target forward).

### 12.5 Guest addresses cannot be verified on Hyper-V in this delivery (decided here)

KVP-reported addresses come from an arbitrary guest OS. The host-side evidence Hyper-V
offers — the adapter's host-assigned MAC with MAC spoofing off, and the host's neighbor
table — proves only **which MAC the host resolves an address to**, not who *owns* the
address: a child can gratuitously ARP another VM's address with its own permitted MAC and
satisfy every such rule. Hyper-V documents MAC spoofing protection as a limit on
permitted source MACs and lists ARP/ND poisoning protection as a separate switch feature
that the standard switch does not expose to this service. Without an **IP allocation
authority** (the reserved `IAddressAuthority` seam of §13.1, which has no implementation
here), IP ownership is unverifiable, and the contract says so instead of guessing:

| Use | Rule in this delivery |
|---|---|
| Host forward to a child (`target=host`) | **refused**: `409 address-unverifiable { address, reason: "no-address-authority" }`. `network.addressVerification = unsupported` on Hyper-V. Host forwards of **primaries** are unchanged (existing endpoint-host mechanism). |
| Client tunnel to a child (`target=client`, via the requester's primary) | allowed with an **unverified** destination: the `via` primary already shares the switch with the child, so a spoofing child gains nothing it does not already have on that segment; the requester is told (`destination.verified = false`, CLI warning "destination address is guest-reported and unverified"). |
| Direct address reporting (`GET /vms/{child}/addresses`) | reported with `verified = false` always; `source` says where it came from. |

Sanity rules still applied to any candidate address before it becomes a tunnel
destination (they prevent accidents, not attacks):

| Rule | Detail |
|---|---|
| Adapter identity | the address must be reported for **this VM's** adapters, looked up by VM id |
| Switch binding | the address must fall inside a `GuestSubnet` whose `SwitchName` equals the child's adapter switch **and** the `via` primary's adapter switch (`GuestAdapter.SwitchName`); a `GuestSubnet` of another switch never matches even when its CIDR overlaps |
| Family | IPv4 or IPv6 |
| Forbidden | loopback, unspecified, link-local, multicast, broadcast, any host address (`GetHostAddressesAsync`), any address reported or previously used for a **different** managed VM; when two VMs report the same address neither may use it (`409 address-conflict` for a new request; existing tunnels flip to `status: "error"`) |
| Freshness | re-validated on every reconciliation pass; an address that disappears or conflicts flips the forward to `status: "error"`, `message: "guest address changed"`; the extension re-acks when a usable address returns |

When a future backend provides an allocation authority, `IAccessExposure` marks
addresses `verified = true` and host forwards to children become admissible under the
same policy switches; nothing else in the API changes.

## 13. Seams and file ownership for parallel implementation

### 13.1 Core abstractions added in stage 1 (compiled; with in-memory fakes and DI hooks)

Everything below is added **before** the pairs branch, by the integrator, in new files
under `Constructd.Core/Domain` and `Constructd.Core/Abstractions` (one file per record
group as the `// ----` comments say). The block is extracted by
`test/contracts-compile.test.sh` and compiled against the real `Constructd.Core` with
warnings as errors, so every name it uses exists and every signature is legal C#. Bodies
of the real implementations belong to the owning pair; the fakes are integrator-owned.

The **edits to existing records** (`Vm`, `User`, `Job`, `PortForward`) are listed in §1.1
and are not in the block (they modify existing files). The additive members the earlier
revision put on `IVmRepository`, `IUserStore` and `ITokenService` are instead **new
interfaces** (`IVmDelegationRepository`, `IUserAllowanceStore`, `IVmTokenIssuer`)
implemented by the same SQLite/in-memory classes and registered separately, so no
existing interface changes and no existing test double breaks.

<!-- stage1-contract -->
```csharp
// ---- Constructd.Core/Domain/VmKinds.cs ----------------------------------------------------
namespace Constructd.Core.Domain
{
    public enum VmKind { Primary, Child }
    public enum SharingScope { Private, Host, Selected }
    public enum VmTokenKind { Legacy, Primary }
    public enum LeaseState { Inactive, Active, Unlimited, Expired, Overdue }
    public enum CapabilityLevel { Unsupported, Conditional, Supported }
    public enum SecureBootTemplate { MicrosoftWindows, MicrosoftUefiCertificateAuthority }
    public enum BootDevice { InstallMedia, AuxiliaryMedia, Disk, Network }
    public enum GuestReportProvenance { Unknown, Provisioner }
    public enum GuestAddressFamily { Ipv4, Ipv6 }
    public enum GuestAddressSource { Kvp, Dhcp, Unknown }
    public enum MediaRole { Install, Auxiliary }
    public enum MediaSource { Url, Upload }
    public enum MediaState { Pending, Transferring, Ready, Failed, Deleting }
    public enum MediaSlot { Install, Auxiliary }
    public enum UploadState { Open, Completing, Done, Aborted, Expired }
    public enum ReservationResource { Ram, Cpu, Storage }
    public enum ReservationPhase { Pending, Held }
    public enum ReservationOrigin { Api, External, Reconcile }
    public enum CapacityMode { Observe, Enforce }
    public enum GracefulShutdownOutcome { Completed, Timeout, Unavailable, Failed }
    public enum MaintenanceState { Open, Draining, Maintenance }
    public enum CascadeState { Previewed, Accepted, Running, Failed, Completed, Expired }
    public enum HostUpdateState
    {
        Checking, Staged, StageFailed, Draining, HandedOff, Applying, Succeeded, ApplyFailed,
        RolledBack, RolledBackWithDatabase, RecoveryFailed, Interrupted, Cancelled, ResolvedByAdmin,
    }
    public enum ForwardRelationship { Self, Admin, Owner, Parent, Shared }
    public enum ChildAction
    {
        Inspect, Start, Shutdown, Save, Restart, Delete, Share, Renew, Hardware, Media,
        Console, ForwardClient, ForwardHost, Addresses, Overrides, RotateToken,
    }

    // ---- Constructd.Core/Domain/Lease.cs -------------------------------------------------
    /// <param name="RequestedSeconds">null = never.</param>
    /// <param name="Version">Bumped on every lease write; expiry jobs re-check it (§5.4).</param>
    public sealed record Lease(
        string RequestedText,
        long? RequestedSeconds,
        DateTimeOffset? ActivatedAt,
        DateTimeOffset? ExpiresAt,
        LeaseState State,
        long Version,
        DateTimeOffset? LastExpiryAttemptAt,
        string? LastExpiryOutcome);

    // ---- Constructd.Core/Domain/ChildHardware.cs ----------------------------------------
    /// <param name="DynamicMemory">Reserved seam for a future backend; every current backend refuses a non-null value.</param>
    public sealed record ChildHardware(
        int Cpus,
        int RamMb,
        int DiskGb,
        int Generation,
        bool SecureBoot,
        SecureBootTemplate? SecureBootTemplate,
        bool Tpm,
        IReadOnlyList<BootDevice> BootOrder,
        bool NetworkAttached,
        DynamicMemoryPolicy? DynamicMemory = null);

    public sealed record DynamicMemoryPolicy(long MinimumBytes, long StartupBytes, long MaximumBytes);

    // ---- Constructd.Core/Domain/GuestReport.cs ------------------------------------------
    public sealed record GuestReport(
        string? ConstructCommit,
        DateTimeOffset? ProvisionedAt,
        DateTimeOffset? ReinstalledAt,
        DateTimeOffset? ReportedAt,
        GuestReportProvenance Provenance,
        DateTimeOffset? LastAttemptAt,
        string? LastAttemptOutcome)
    {
        public static GuestReport Unknown { get; } = new(null, null, null, null, GuestReportProvenance.Unknown, null, null);
    }

    // ---- Constructd.Core/Domain/HostObservation.cs --------------------------------------
    public sealed record GuestAddress(string Address, GuestAddressFamily Family, GuestAddressSource Source, DateTimeOffset ObservedAt, bool Verified);

    /// <summary>Host-authoritative facts about ONE VM adapter (never guest-supplied).</summary>
    public sealed record GuestAdapter(string VmId, string AdapterId, string MacAddress, bool MacSpoofingEnabled, string? SwitchName);
    /// <summary>One host neighbor-table row on a guest-facing interface.</summary>
    public sealed record HostNeighbor(string Address, string MacAddress, string InterfaceAlias, string State);
    /// <summary>A guest subnet bound to the switch and host interface it belongs to, so overlapping subnets on different switches never match.</summary>
    public sealed record GuestSubnet(string Cidr, string SwitchName, string InterfaceAlias);

    public sealed record HostObservation(
        DateTimeOffset? CreatedAt,
        DateTimeOffset? LastBootAt,
        IReadOnlyList<GuestAddress> Addresses,
        string? StorageProblem);

    // ---- Constructd.Core/Domain/UserAllowance.cs ----------------------------------------
    /// <summary>Stored per user; every field null = "use host userDefaults".</summary>
    public sealed record UserAllowance(
        bool? AllowChildCreation,
        int? MaxRetainedChildren,
        int? CpuBudget,
        long? RamBudgetBytes,
        long? StorageBudgetBytes,
        long? MaxChildLifetimeSeconds,
        bool? AllowNeverLifetime,
        bool? AllowSharing)
    {
        public static UserAllowance Unset { get; } = new(null, null, null, null, null, null, null, null);
    }

    /// <summary>Per-primary restriction (restrict-only: the effective value is the minimum of override and user value).</summary>
    public sealed record VmOverride(
        string VmName,
        bool? AllowChildCreation,
        int? MaxRetainedChildren,
        long? MaxChildLifetimeSeconds,
        bool? AllowNeverLifetime,
        bool? AllowSharing,
        DateTimeOffset UpdatedAt);

    /// <summary>Resolved, non-nullable view for one owner (+ optional parent override).</summary>
    public sealed record EffectiveAllowance(
        int MaxPrimaries,
        bool AllowChildCreation,
        int MaxRetainedChildren,
        int? CpuBudget,
        long? RamBudgetBytes,
        long? StorageBudgetBytes,
        long? MaxChildLifetimeSeconds,
        bool AllowNeverLifetime,
        bool AllowSharing,
        bool AllowHostForwards);

    public sealed record AllowanceUsage(int Primaries, int Children, int Cpus, long RamBytes, long StorageBytes);

    // ---- Constructd.Core/Domain/HostConfig.cs -------------------------------------------
    public sealed record CapacityConfig(CapacityMode Mode, long? RamHeadroomBytes, long StorageHeadroomBytes, int? CpuBudget, int? MaxVcpusPerVm, int ReconcileSeconds, int OrphanReservationTimeoutSeconds);
    /// <summary>Host-wide HARD ceilings applied after user resolution (null = no cap).</summary>
    public sealed record UserCapsConfig(int? MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool? AllowNeverLifetime, bool? AllowSharing);
    public sealed record UserDefaultsConfig(int MaxPrimaries, bool AllowChildCreation, int MaxRetainedChildren, int? CpuBudget, long? RamBudgetBytes, long? StorageBudgetBytes, long? MaxChildLifetimeSeconds, bool AllowNeverLifetime, bool AllowSharing);
    public sealed record LifecycleConfig(int GracefulShutdownTimeoutSeconds, int LeaseTickSeconds, int LeaseRetrySeconds);
    public sealed record MediaConfig(long MaxBytes, int MaxItemsPerUser, int UploadChunkBytes, int UploadTtlHours, int AcquireTimeoutMinutes, bool AllowHttp, int? UnreferencedTtlHours);
    public sealed record NetworkConfig(bool HostForwardsEnabled, bool DirectAddressReporting);
    public sealed record UpdatesConfig(string Repository, string Channel, int DrainTimeoutMinutes, int HealthTimeoutSeconds, bool RequireSignature, string? ManifestPublicKey);
    public sealed record MaintenanceMarker(MaintenanceState State, string? UpdateId, DateTimeOffset Since);

    // ---- Constructd.Core/Domain/MediaItem.cs --------------------------------------------
    /// <param name="DedicatedTo">VM the item was uploaded for; deleted with that VM (§6.5).</param>
    public sealed record MediaItem(
        string Id,
        string Owner,
        string Name,
        MediaRole Role,
        MediaSource Source,
        string? SourceUrl,
        string Path,
        MediaState State,
        long? SizeBytes,
        long ReservedBytes,
        string? Sha256,
        string? ExpectedSha256,
        string? Error,
        string? JobId,
        string? DedicatedTo,
        DateTimeOffset Created,
        DateTimeOffset? ReadyAt,
        DateTimeOffset? LastReferencedAt);

    public sealed record MediaReference(string MediaId, string VmName, MediaSlot Slot, DateTimeOffset Created);

    public sealed record MediaUpload(
        string Id,
        string MediaId,
        string Owner,
        long SizeBytes,
        int ChunkBytes,
        IReadOnlyList<int> Received,
        UploadState State,
        string? OperationKey,
        DateTimeOffset Created,
        DateTimeOffset ExpiresAt);

    // ---- Constructd.Core/Domain/Reservation.cs ------------------------------------------
    /// <param name="OperationId">The operation (job id, or request id for synchronous starts) that owns a pending reservation.</param>
    /// <param name="PendingUntil">Deadline after which an ORPHANED pending reservation (owner not alive) may be swept.</param>
    public sealed record Reservation(
        string Id,
        ReservationResource Resource,
        string? ScopeOwner,
        string? VmName,
        string? Artifact,
        string? Volume,
        long Amount,
        ReservationPhase Phase,
        ReservationOrigin Origin,
        string? OperationId,
        DateTimeOffset Created,
        DateTimeOffset? PendingUntil,
        DateTimeOffset? ConfirmedAt);

    public enum OrphanResolution { Released, PromotedToHeld, Kept }
    public sealed record OrphanOutcome(string ReservationId, OrphanResolution Resolution, string Evidence);

    // ---- Constructd.Core/Domain/Cascade.cs ----------------------------------------------
    /// <param name="Incarnation">null when the hypervisor id is not known yet (create in flight, migrated primary before inventory); compared by strict equality, null included.</param>
    public sealed record CascadeChild(string Name, string? Incarnation, SharingScope Sharing, VmState State, int DiskGb, int MediaCount);

    /// <summary>The stored preview a cascade confirmation must match (§8.8).</summary>
    public sealed record CascadePreview(
        string Parent,
        string? ParentIncarnation,
        string Token,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<CascadeChild> Children,
        CascadeState State,
        string? JobId,
        IReadOnlyDictionary<string, string> Outcomes);

    // ---- Constructd.Core/Domain/ForwardDestination.cs -----------------------------------
    /// <summary>
    /// Where a forward really connects. Null on every forward that exists today (a VM's own port),
    /// so the flat legacy wire shape is unchanged.
    /// </summary>
    /// <param name="Via">The primary whose SSH endpoint carries a client tunnel; null for host forwards of a primary.</param>
    /// <param name="Verified">Always false on Hyper-V in this delivery (§12.5); true only when an IAddressAuthority vouched for the address.</param>
    public sealed record ForwardDestination(
        string VmName,
        string? Via,
        string? ConnectAddress,
        int ConnectPort,
        string RequestedBy,
        ForwardRelationship Relationship,
        bool Verified);

    // ---- Constructd.Core/Domain/HostUpdateRecord.cs -------------------------------------
    public sealed record HostUpdatePhase(string Name, DateTimeOffset At, string? Outcome, string? Error);

    public sealed record HostUpdateRecord(
        string Id,
        string Commit,
        string? ReleaseTag,
        string? PackageVersion,
        HostUpdateState State,
        string? Phase,
        IReadOnlyList<HostUpdatePhase> Phases,
        DateTimeOffset Started,
        DateTimeOffset? Finished,
        string? Error,
        string? PreviousCommit,
        string Actor,
        IReadOnlyList<string> BlockingJobs);

    public sealed record InstalledRelease(string Commit, string PackageVersion, DateTimeOffset? InstalledAt, string Source);

    // ---- Constructd.Core/Domain/Capabilities.cs -----------------------------------------
    public sealed record ConsoleCapabilities(
        CapabilityLevel Screenshot,
        CapabilityLevel Keyboard,
        CapabilityLevel MouseAbsolute,
        CapabilityLevel MouseRelative,
        CapabilityLevel Interactive,
        int MaxScreenshotBytes,
        bool NativeResolutionOnly);

    public sealed record NetworkCapabilities(
        CapabilityLevel ClientForward,
        CapabilityLevel HostForwardPrimary,
        CapabilityLevel HostForwardChild,
        CapabilityLevel DirectAddressReporting,
        CapabilityLevel AddressVerification,
        CapabilityLevel Isolation);

    /// <summary>Per-VM network view (child: HostForward = HostForwardChild lowered by policy; primary: HostForwardPrimary).</summary>
    public sealed record VmNetworkCapabilities(CapabilityLevel ClientForward, CapabilityLevel HostForward, CapabilityLevel AddressVerification);

    /// <summary>Superset of the existing DriverCapabilities; the legacy values are embedded unchanged.</summary>
    public sealed record BackendCapabilities(
        string Backend,
        Constructd.Core.Abstractions.DriverCapabilities Legacy,
        IReadOnlyList<int> Generations,
        int DefaultGeneration,
        CapabilityLevel SecureBoot,
        IReadOnlyList<SecureBootTemplate> SecureBootTemplates,
        CapabilityLevel Tpm,
        bool SecureBootTemplateLockedAfterTpmInit,
        int MaxOpticalDrives,
        CapabilityLevel AuxiliaryMedia,
        CapabilityLevel BootOrder,
        ConsoleCapabilities Console,
        NetworkCapabilities Network,
        CapabilityLevel DynamicMemory,
        CapabilityLevel MemoryOvercommit,
        CapabilityLevel Suspend,
        CapabilityLevel GracefulShutdown,
        IReadOnlyList<string> Notes);

    /// <summary>What ONE VM can do right now (§3.3, per-VM runtime).</summary>
    public sealed record VmCapabilitiesSnapshot(
        string VmName,
        VmState State,
        bool VideoHeadPresent,
        bool KeyboardPresent,
        bool SyntheticMousePresent,
        bool Ps2MousePresent,
        int? NativeWidth,
        int? NativeHeight,
        bool SecureBootTemplateLocked,
        int Generation,
        CapabilityLevel GracefulShutdown,
        VmNetworkCapabilities Network);
}

// ---- Constructd.Core/Abstractions ------------------------------------------------------------
namespace Constructd.Core.Abstractions
{
    using Constructd.Core.Domain;

    // ---- IVmDelegationRepository.cs (owner: integrator; implemented by SqliteVmRepository/InMemoryVmRepository) ----
    public enum VmAddDecision { Added, NameTaken, PrimaryQuotaExceeded, ChildrenQuotaExceeded, ParentClosed, ParentMissing }

    public interface IVmDelegationRepository
    {
        Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct);
        Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct);
        Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct);
        /// <summary>Atomic: name free, quota by kind, parent present/not fenced, incarnation assigned.</summary>
        Task<VmAddDecision> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct);
        /// <summary>Sets Deleting (+ optionally ChildCreationClosed) and CurrentJobId in one write; false when already fenced by a live job.</summary>
        Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct);
        Task<bool> UpdateLeaseAsync(string name, Lease lease, long expectedVersion, CancellationToken ct);
        Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct);
        Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct);
        Task SetOverrideAsync(VmOverride value, CancellationToken ct);
        Task<bool> RemoveOverrideAsync(string vmName, CancellationToken ct);
        Task<CascadePreview> SaveCascadePreviewAsync(CascadePreview preview, CancellationToken ct);
        Task<CascadePreview?> GetCascadePreviewAsync(string parent, CancellationToken ct);
        /// <summary>One transaction: token/children/sharing/incarnations equal the stored preview → fence parent + children; else the current list.</summary>
        Task<CascadeAcceptance> TryAcceptCascadeAsync(string parent, string token, string jobId, CancellationToken ct);
        Task<bool> UpdateGuestReportAsync(string name, GuestReport report, CancellationToken ct);
        Task<bool> UpdateObservationAsync(string name, HostObservation observation, CancellationToken ct);
    }

    public sealed record CascadeAcceptance(bool Accepted, string? Reason, IReadOnlyList<CascadeChild> CurrentChildren, string? NewToken);

    // ---- IUserAllowanceStore.cs (owner: integrator; implemented by the user stores) ----
    public interface IUserAllowanceStore
    {
        Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken ct);
        Task<bool> SetAllowanceAsync(string name, UserAllowance allowance, CancellationToken ct);
    }

    // ---- IVmTokenIssuer.cs (owner: integrator; implemented by the token services) ----
    public interface IVmTokenIssuer
    {
        /// <summary>Replaces the VM's token hash and kind in one write; returns the plaintext once.</summary>
        Task<string> IssueVmTokenAsync(string vmName, VmTokenKind kind, CancellationToken ct);
        Task<bool> RevokeVmTokenAsync(string vmName, CancellationToken ct);
    }

    // ---- IDelegationPolicy.cs (owner: integrator) ----
    public interface IDelegationPolicy
    {
        /// <summary>§1.1: resolve(user field ?? userDefaults) → cap by userCaps → restrict by override(parent); an override only tightens.</summary>
        Task<EffectiveAllowance> ResolveAsync(string owner, string? parentVm, CancellationToken ct);
        Task<AllowanceUsage> UsageAsync(string owner, CancellationToken ct);
        Task<IReadOnlyList<ChildAction>> AllowedActionsAsync(Vm vm, string principal, ForwardRelationship relationship, CancellationToken ct);
    }

    // ---- IHostConfigStore.cs (owner: integrator; used by every pair) ----
    public interface IHostConfigStore
    {
        Task<T?> GetAsync<T>(string section, CancellationToken ct) where T : class;
        Task SetAsync<T>(string section, T value, string updatedBy, CancellationToken ct) where T : class;
        /// <summary>Compare-and-set on the section's updated_at; false when somebody else wrote in between.</summary>
        Task<bool> TrySetAsync<T>(string section, T value, string updatedBy, DateTimeOffset? expectedUpdatedAt, CancellationToken ct) where T : class;
    }

    // ---- IVmOperationGate.cs (owner: integrator; ONE per-VM lock for every state-changing operation) ----
    public interface IVmOperationGate
    {
        /// <summary>Waits for the VM's gate. Lifecycle, delete, hardware, media, reconcile-per-VM, expiry all take it.</summary>
        Task<IAsyncDisposable> AcquireAsync(string vmName, string operationId, CancellationToken ct);
        /// <summary>Non-blocking variant for reconciliation: null when an operation holds the gate.</summary>
        Task<IAsyncDisposable?> TryAcquireAsync(string vmName, string operationId, CancellationToken ct);
        bool IsHeld(string vmName, out string? operationId);
    }

    // ---- IMediaGate.cs (owner: integrator; ONE per-media lock; order: VM gate → media gates by id, never reversed) ----
    public interface IMediaGate
    {
        Task<IAsyncDisposable> AcquireAsync(string mediaId, string operationId, CancellationToken ct);
        /// <summary>Acquires several media gates in ascending id order.</summary>
        Task<IAsyncDisposable> AcquireManyAsync(IReadOnlyList<string> mediaIds, string operationId, CancellationToken ct);
    }

    // ---- IHostLock.cs (owner: updater pair) — cross-PROCESS exclusive lock (a file opened with FileShare.None under DataDir) ----
    public interface IHostLock
    {
        /// <summary>null when another process holds it after <paramref name="wait"/>.</summary>
        Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct);
        bool IsHeldByAnotherProcess(string name);
    }

    // ---- IOperationRegistry.cs (owner: integrator) — which operations are alive in THIS process ----
    public interface IOperationRegistry
    {
        IDisposable Register(string operationId, string kind, string? vmName);
        bool IsAlive(string operationId);
        IReadOnlyList<(string OperationId, string Kind, string? VmName)> Alive();
    }

    // ---- IOperationKeyStore.cs (owner: child-vm jobs pair) ----
    public enum OperationKeyState { InFlight, Completed }
    /// <param name="IntentJson">For external (hypervisor) mutations: the original intent (lifetime, expected lease version, the activation base) so a replay after a crash reconciles that intent instead of re-deciding.</param>
    /// <param name="PowerGeneration">The VM's power generation the intent was accepted at (§5.3b); a replay whose VM moved past it is a conflict.</param>
    public sealed record OperationKeyRecord(string Owner, string Kind, string Key, string Fingerprint, string Target, string? JobId, OperationKeyState State, string? IntentJson, long? PowerGeneration, string? ResponseJson, DateTimeOffset Created);
    public enum OperationKeyOutcome { Inserted, Replay, Conflict }
    public interface IOperationKeyStore
    {
        Task<OperationKeyRecord?> GetAsync(string owner, string kind, string key, CancellationToken ct);
        /// <summary>INSERT OR FAIL of an InFlight record. In SQLite it runs inside the caller's transaction when one is supplied through the admission seam; standalone otherwise.</summary>
        Task<(OperationKeyOutcome Outcome, OperationKeyRecord? Existing)> TryInsertAsync(OperationKeyRecord record, CancellationToken ct);
        /// <summary>InFlight → Completed with the response, atomically with the database-only mutation it answers (§7.3).</summary>
        Task<bool> CompleteAsync(string owner, string kind, string key, string responseJson, CancellationToken ct);
        Task<bool> RemoveAsync(string owner, string kind, string key, CancellationToken ct);
        Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct);
    }

    // ---- IAdmissionStore.cs (owner: integrator) — THE cross-store atomic acceptance seam (§7.3) ----
    /// <summary>
    /// Everything an accepting request must write together. The SQLite implementation executes the
    /// whole plan in ONE IMMEDIATE transaction under the ledger gate; the in-memory one under one lock.
    /// Nothing in the plan is visible to anybody until the transaction commits.
    /// </summary>
    public sealed record AdmissionPlan(
        OperationKeyRecord? OperationKey,
        Vm? VmToInsert,
        EffectiveAllowance? Allowance,
        IReadOnlyList<MediaItem> MediaToInsert,
        IReadOnlyList<MediaUpload> UploadsToInsert,
        IReadOnlyList<MediaReference> ReferencesToInsert,
        ReservationRequest? Reservation,
        CascadePreview? CascadeToAccept,
        Job? JobToInsert,
        string? VmToFence,
        string? FenceJobId,
        bool CloseChildCreation);

    public enum AdmissionOutcome { Accepted, Replay, KeyConflict, VersionConflict, NameTaken, QuotaExceeded, ParentClosed, ParentMissing, MediaNotReady, CapacityRefused, CascadeMismatch }

    public sealed record AdmissionResult(
        AdmissionOutcome Outcome,
        OperationKeyRecord? ExistingKey,
        CapacityDecision? Capacity,
        CascadeAcceptance? Cascade,
        IReadOnlyList<string> ReservationIds);

    public interface IAdmissionStore
    {
        /// <summary>The caller already holds the maintenance-gate handle (§7.4); the plan commits everything or nothing.</summary>
        Task<AdmissionResult> AdmitAsync(AdmissionPlan plan, CancellationToken ct);
        /// <summary>
        /// ONE transaction over the scope. Used for database-only synchronous mutations (sharing, renew,
        /// overrides, allowances: the mutation plus the Completed key with its response) AND for the
        /// database half of an external mutation after the hypervisor call (lease activation + reservation
        /// confirm + key completion + power generation, §7.3). The scope itself performs no I/O outside SQLite.
        /// A null <paramref name="key"/> means "no operation key" (today's path). Runs UNDER THE LEDGER GATE
        /// (the same non-reentrant gate as AdmitAsync): callers hold VM/media gates, never the ledger gate,
        /// when they call it; reconciliation's per-VM step IS a MutateAsync call (§4.4).
        /// </summary>
        Task<AdmissionResult> MutateAsync(OperationKeyRecord? key, Func<IAdmissionScope, Task<bool>> mutation, CancellationToken ct);
        /// <summary>
        /// When the persisted job cannot be started (in-process failure after commit): the job row is marked
        /// failed with <paramref name="error"/>; every fence, reservation and row the plan created is then
        /// recovered by the SAME rules as a crashed job (§4.4, §8.8), never by an ad-hoc delete.
        /// </summary>
        Task MarkStartFailedAsync(string jobId, string error, CancellationToken ct);
    }

    /// <summary>What a database-only mutation may write inside MutateAsync (all on the same transaction).</summary>
    public interface IAdmissionScope
    {
        Task<bool> UpdateLeaseAsync(string vmName, Lease lease, long expectedVersion);
        Task<bool> UpdateSharingAsync(string vmName, SharingScope scope);
        Task SetOverrideAsync(VmOverride value);
        Task<bool> SetAllowanceAsync(string userName, UserAllowance allowance);
        /// <summary>Compare-and-bump of the VM's power generation (§5.3b); false when it moved.</summary>
        Task<bool> BumpPowerGenerationAsync(string vmName, long expected);
        /// <summary>The VM row as it is INSIDE this transaction (fresh, gate-protected read for §4.4 staleness checks).</summary>
        Task<Vm?> ReadVmAsync(string vmName);
        /// <summary>Re-admission of a start whose reservations were swept (§7.3): same rules as AdmitAsync, inside this transaction.</summary>
        Task<CapacityDecision> ReserveAsync(ReservationRequest request);
        Task ConfirmReservationsAsync(IReadOnlyList<string> ids, VmState observed);
        Task ReleaseReservationsAsync(IReadOnlyList<string> ids, VmState observed, string reason);
        Task<bool> CompleteOperationKeyAsync(string owner, string kind, string key, string responseJson);
        Task AppendAuditAsync(AuditEntry entry);
    }

    // ---- IPersistedJobRunner.cs (owner: child-vm jobs pair; implemented by InProcessJobEngine) ----
    public interface IPersistedJobRunner
    {
        /// <summary>Runs a job whose Queued row was written by an AdmissionPlan. <paramref name="gateHandle"/> was taken BEFORE the plan committed and is owned by the runner until the job is terminal.</summary>
        Task StartPersistedAsync(Job queued, IDisposable gateHandle, Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work, CancellationToken ct);
        Task SetPhaseAsync(string jobId, string phase, CancellationToken ct);
    }

    // ---- IChildVmDriver.cs (owner: child-vm driver + jobs pair) ----
    public sealed record ChildVmDescriptor(
        string Name,
        ChildHardware Hardware,
        string? VhdPath,
        string? InstallMediaPath,
        string? AuxiliaryMediaPath,
        string SwitchName);

    public sealed record AttachedMedia(string? InstallPath, string? AuxiliaryPath, bool Complete);

    public interface IChildVmDriver
    {
        Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct);
        Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct);
        /// <summary>Removes VM, disk chain and saved state. Missing VM is not an error. May turn a running VM off: deletion is destructive by request (§8.8).</summary>
        Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct);
        /// <summary>VM must be Off. resendTemplate=false never re-sends the Secure Boot template (locked after TPM init).</summary>
        Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct);
        Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct);
        /// <summary>What the hypervisor REALLY has attached (paths per slot), so references are reconciled to reality after a partial failure.</summary>
        Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct);
        /// <summary>Guest shutdown only (WMI InitiateShutdown, force=false) then poll until Off or timeout. Never -Force, -TurnOff, save or delete.</summary>
        Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct);
        Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct);
        /// <summary>Immutable hypervisor id of the VM (Hyper-V VM GUID) — the child's incarnation.</summary>
        Task<string?> GetVmIdAsync(string name, CancellationToken ct);
    }

    // ---- IHypervisorInventory.cs (owner: capacity pair) ----
    public sealed record HypervisorDiskInfo(string Path, long MaxBytes, long FileBytes, string? ParentPath, string Volume, bool Readable);
    public sealed record HypervisorVmInfo(
        string Name,
        string Id,
        VmState State,
        string RawState,
        int Generation,
        int Cpus,
        long MemoryStartupBytes,
        long MemoryAssignedBytes,
        bool DynamicMemory,
        long? MemoryMaximumBytes,
        IReadOnlyList<HypervisorDiskInfo> Disks,
        long? SavedStateBytes,
        string ConfigVolume,
        bool Complete);
    public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes);
    public sealed record HostResourcesInfo(int LogicalCpus, long TotalRamBytes, long FreeRamBytes, IReadOnlyList<VolumeInfo> Volumes, DateTimeOffset ObservedAt);
    /// <summary>ONE epoch: VMs, host resources and volume free space read in the same pass; Complete=false ⇒ admission fails closed.</summary>
    public enum ArtifactPresence { Unknown, Absent, Present }
    public sealed record CapacityArtifactInfo(string Artifact, string? Path, string Volume, long FileBytes, ArtifactPresence Presence);
    public sealed record InventorySnapshot(long Epoch, DateTimeOffset ObservedAt, HostResourcesInfo Host, IReadOnlyList<HypervisorVmInfo> Vms, bool Complete, IReadOnlyList<string> Problems,
        IReadOnlyList<CapacityArtifactInfo>? Artifacts = null);

    public interface IHypervisorInventory
    {
        Task<InventorySnapshot> ReadAsync(CancellationToken ct);
        Task<InventorySnapshot> ReadAsync(IReadOnlyList<Reservation> reservations, CancellationToken ct);
    }

    // ---- ICapacityLedger.cs (owner: capacity pair) ----
    public sealed record ReservationLine(ReservationResource Resource, long Amount, string? Artifact, string? Volume);
    public sealed record ReservationRequest(string Owner, string? VmName, string OperationId, IReadOnlyList<ReservationLine> Lines, TimeSpan PendingTimeout);
    /// <param name="AllowedAmount">The limit that applied (wire: <c>allowed</c>).</param>
    public sealed record CapacityDecision(
        bool Allowed,
        IReadOnlyList<string> ReservationIds,
        string? Resource,
        string? Scope,
        long Requested,
        long AllowedAmount,
        long Available,
        string? Reason,
        long Epoch);
    public sealed record VolumeCapacity(string Root, long TotalBytes, long FreeBytes, long HeadroomBytes, long GrowthReservedBytes, long AvailableBytes);
    public sealed record HostCapacitySnapshot(
        long Epoch,
        DateTimeOffset ObservedAt,
        bool Complete,
        long RamTotalBytes,
        long RamHeadroomBytes,
        long RamReservedBytes,
        long RamUnmanagedBytes,
        long RamPhysicalFreeBytes,
        long RamAvailableBytes,
        int CpuLogical,
        int? CpuBudget,
        int CpuActive,
        int? CpuAvailable,
        IReadOnlyList<VolumeCapacity> Volumes,
        IReadOnlyList<Reservation> Reservations,
        IReadOnlyList<HypervisorVmInfo> Unmanaged,
        IReadOnlyList<string>? Problems = null);

    public interface ICapacityLedger
    {
        /// <summary>Serialized (one gate) + one IMMEDIATE transaction; pending rows carry OperationId and PendingUntil.</summary>
        Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct);
        /// <summary>pending → held. Requires the observed state that justifies it.</summary>
        Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct);
        Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct);
        /// <summary>Trim a storage reservation to the artifact's real size (media completion).</summary>
        Task TrimAsync(string id, long amount, CancellationToken ct);
        /// <summary>Keeps a pending reservation alive while its operation runs (extends PendingUntil).</summary>
        Task ExtendAsync(IReadOnlyList<string> ids, TimeSpan by, CancellationToken ct);
        /// <summary>§4.4 sequence: snapshot without gates; per VM TryAcquire the VM gate (no wait, no upgrade) and only then the ledger gate; host-level rules under the ledger gate alone. Orphans are resolved per resource, never by time alone.</summary>
        Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct);
        Task<HostCapacitySnapshot> SnapshotAsync(bool refresh, CancellationToken ct);
    }

    // ---- IMediaStore.cs / IMediaTransfer.cs / IUrlAdmissionPolicy.cs (owner: media pair) ----
    public interface IMediaStore
    {
        Task<MediaItem?> GetAsync(string id, CancellationToken ct);
        Task<IReadOnlyList<MediaItem>> ListAsync(string? owner, CancellationToken ct);
        Task<int> CountByOwnerAsync(string owner, CancellationToken ct);
        Task AddAsync(MediaItem item, CancellationToken ct);
        /// <summary>Compare-and-set on State: false when the item is no longer in <paramref name="expected"/>.</summary>
        Task<bool> TryTransitionAsync(string id, MediaState expected, MediaItem updated, CancellationToken ct);
        Task<bool> RemoveAsync(string id, CancellationToken ct);
        Task<IReadOnlyList<MediaReference>> ListReferencesAsync(string mediaId, CancellationToken ct);
        Task<IReadOnlyList<MediaReference>> ListReferencesForVmAsync(string vmName, CancellationToken ct);
        /// <summary>Insert only while the item is Ready and not Deleting; false otherwise.</summary>
        Task<bool> TryAddReferenceAsync(MediaReference reference, CancellationToken ct);
        Task<bool> RemoveReferenceAsync(string mediaId, string vmName, MediaSlot slot, CancellationToken ct);
        Task<MediaUpload?> GetUploadAsync(string id, CancellationToken ct);
        Task AddUploadAsync(MediaUpload upload, CancellationToken ct);
        Task<bool> TryTransitionUploadAsync(string id, UploadState expected, MediaUpload updated, CancellationToken ct);
        Task<bool> RecordChunkAsync(string uploadId, int index, CancellationToken ct);
        Task<IReadOnlyList<MediaUpload>> ListExpiredUploadsAsync(DateTimeOffset now, CancellationToken ct);
    }

    public sealed record UrlAdmission(bool Allowed, string? Reason, string? Address, Uri? Normalized, IReadOnlyList<string> ResolvedAddresses);
    public interface IUrlAdmissionPolicy
    {
        /// <summary>Pure rules of §6.3 over already-resolved addresses; no I/O.</summary>
        UrlAdmission Check(Uri url, IReadOnlyList<System.Net.IPAddress> resolved, bool allowHttp, bool hasChecksum);
    }

    public sealed record TransferResult(long SizeBytes, string Sha256, Uri FinalUrl);
    public interface IMediaTransfer
    {
        Task<TransferResult> AcquireAsync(MediaItem item, Uri source, long maxBytes, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct);
        Task WriteChunkAsync(MediaUpload upload, int index, Stream body, long contentLength, CancellationToken ct);
        Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct);
        Task<bool> LooksLikeIsoAsync(string path, CancellationToken ct);
        /// <summary>false = held open by the hypervisor (retry later); throws for other errors.</summary>
        Task<bool> TryDeleteAsync(string path, CancellationToken ct);
        Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct);
    }

    // ---- IConsoleTransport.cs / IConsoleSessionStore.cs (owner: console pair) ----
    public sealed record ConsoleScreen(int NativeWidth, int NativeHeight, bool VideoHeadPresent, bool KeyboardPresent, bool SyntheticMousePresent, bool Ps2MousePresent);
    public sealed record ConsoleImage(bool Ok, uint ReturnValue, int Width, int Height, ReadOnlyMemory<byte> Png);
    public enum KeyboardInputKind { Text, Key, Scancodes, CtrlAltDel }
    /// <param name="Press">Key kind only: true = press, false = release, null = press+release.</param>
    public sealed record KeyboardInput(KeyboardInputKind Kind, string? Text, int? KeyCode, bool? Press, IReadOnlyList<byte>? Scancodes);
    public enum MouseInputKind { MoveAbsolute, MoveRelative, Click, Press, Release }
    public sealed record MouseInput(MouseInputKind Kind, int? X, int? Y, int? Dx, int? Dy, int? Button);
    public sealed record ConsoleInputResult(bool Applied, uint ReturnValue, string? Device, string? Fallback);

    public interface IConsoleTransport
    {
        ConsoleCapabilities Capabilities { get; }
        Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct);
        Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct);
        /// <summary>Text reaches the host process through stdin, never argv; nothing typed is logged.</summary>
        Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct);
        Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct);
    }

    public sealed record ConsoleSession(string Id, string VmName, string Principal, DateTimeOffset Created, DateTimeOffset ExpiresAt, int NativeWidth, int NativeHeight);
    public interface IConsoleSessionStore
    {
        /// <summary>null when the per-VM cap (4) is reached.</summary>
        ConsoleSession? TryCreate(string vmName, string principal, int nativeWidth, int nativeHeight, TimeSpan ttl, DateTimeOffset now);
        ConsoleSession? Get(string id, DateTimeOffset now);
        ConsoleSession? Renew(string id, TimeSpan ttl, DateTimeOffset now);
        bool Remove(string id);
        int RemoveExpired(DateTimeOffset now);
        int RemoveForVm(string vmName);
        int RemoveForPrincipal(string principal);
        bool TryTakeRate(string id, string bucket, int perSecond, DateTimeOffset now);
    }

    // ---- IMaintenanceGate.cs (owner: host release + updater pair) ----
    public sealed record DrainResult(bool Drained, IReadOnlyList<(string OperationId, string Kind, string? VmName)> Blocking, TimeSpan Waited);
    public interface IMaintenanceGate
    {
        MaintenanceState State { get; }
        /// <summary>Atomic admission of a unit of gated work; null ⇒ 503 maintenance. Dispose when the work ends.</summary>
        IDisposable? TryEnter(string kind, string operationId, string? vmName);
        /// <summary>draining; waits until no handle is live or the timeout passes.</summary>
        Task<DrainResult> DrainAsync(TimeSpan timeout, CancellationToken ct);
        void Enter(MaintenanceState state, string? updateId);
        void Reopen();
        int LiveHandles { get; }
    }

    // ---- IReleaseSource.cs / IUpdateStager.cs / IUpdaterLauncher.cs / IHostUpdateStore.cs (same owner) ----
    public sealed record ReleaseAsset(string Name, Uri Url, long SizeBytes);
    public sealed record ReleaseDescriptor(string Tag, string Commit, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseAsset> Assets);
    public interface IReleaseSource
    {
        Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct);
        Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct);
    }

    public sealed record ManifestDatabase(int SchemaVersion, int MinReadableBy, IReadOnlyList<int> BreakingMigrations);
    public sealed record ManifestCompat(string MinInstalledCommitDate, int MinSchemaVersionToUpdateFrom);
    /// <summary>Configuration compatibility (§11.2): appsettings is additive and never rewritten; a release that needs a new mandatory key lists it.</summary>
    public sealed record ManifestConfig(int SettingsSchemaVersion, int MinReadableBy, IReadOnlyList<string> RequiredKeys, IReadOnlyList<string> NewKeysWithDefaults);
    public sealed record ReleaseManifest(
        int SchemaVersion,
        string Commit,
        string Ref,
        string PackageVersion,
        DateTimeOffset BuiltAt,
        string Repository,
        string ReleaseTag,
        string PayloadAsset,
        string PayloadSha256,
        string SumsSha256,
        string UpdaterPath,
        string UpdaterSha256,
        ManifestDatabase Database,
        ManifestConfig Config,
        ManifestCompat Compat);
    public sealed record StagedUpdate(string UpdateId, ReleaseManifest Manifest, string StagedPath, IReadOnlyList<string> Files);
    public interface IUpdateStager
    {
        Task<StagedUpdate> StageAsync(string updateId, ReleaseDescriptor release, IProgress<string>? progress, CancellationToken ct);
        Task<bool> VerifyStagedAsync(StagedUpdate staged, CancellationToken ct);
        Task RemoveStagedAsync(string updateId, CancellationToken ct);
    }

    /// <param name="HealthToken">Random secret written only into the SYSTEM-only handoff file; the new binary accepts it on loopback /health for the full body.</param>
    public sealed record UpdateHandoff(string UpdateId, string Commit, string StagedPath, string PublishDir, string ScriptsDir, string DataDir, string ServiceName, string PreviousCommit, string HealthUrl, string CertificateThumbprint, string AdminCliPath, string HealthToken, DateTimeOffset WrittenAt);
    /// <summary>What the fence permits, scoped to ONE update id (§11.7). Checked by the updater under updater.lock before stop, replace and rollback.</summary>
    public enum FenceDisposition
    {
        /// <summary>The update is over before replacement; no rollback authority remains; only a fresh apply may follow.</summary>
        Closed,
        /// <summary>The new binary is kept; the updater may only finish the commit phase; rollback is refused.</summary>
        CommitOnly,
        /// <summary>An admin authorized rollback; the service stays in maintenance until the rollback outcome is recorded.</summary>
        RollbackAuthorized,
    }
    public sealed record UpdateFence(string UpdateId, FenceDisposition Disposition, string Actor, DateTimeOffset At);
    public interface IUpdaterLauncher
    {
        /// <summary>Writes the handoff durably, THEN creates and starts the one-shot SYSTEM task (§11.5).</summary>
        Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct);
        Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct);
        /// <summary>The updater's last-update.json, or null.</summary>
        Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct);
        /// <summary>Writes the fence (temp + rename) while holding the updater lock; false when the updater still holds it.</summary>
        Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct);
        Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct);
    }
    public sealed record RecoveryRecord(string UpdateId, string Commit, string PreviousCommit, string Phase, DateTimeOffset PhaseAt, string? Outcome, string? Error, string BackupPath, bool BackupComplete, bool ReplaceStarted, string StagedPath, int HealthAttempts, IReadOnlyList<string> ManualSteps);

    public interface IHostUpdateStore
    {
        Task<HostUpdateRecord?> GetAsync(string id, CancellationToken ct);
        Task<HostUpdateRecord?> GetActiveAsync(CancellationToken ct);
        Task<IReadOnlyList<HostUpdateRecord>> ListAsync(int limit, CancellationToken ct);
        /// <summary>Insert only when no non-terminal row exists (409 update-in-progress otherwise).</summary>
        Task<bool> TryStartAsync(HostUpdateRecord record, CancellationToken ct);
        Task UpsertAsync(HostUpdateRecord record, CancellationToken ct);
    }

    // ---- Network seams (owner: network pair) ----
    /// <summary>
    /// RESERVED: an IP allocation authority (a host-controlled DHCP/IPAM that assigns addresses to adapters).
    /// No implementation exists in this delivery; without one, IP ownership cannot be verified (§12.5).
    /// </summary>
    public interface IAddressAuthority
    {
        Task<IReadOnlyList<GuestAddress>> GetAssignedAddressesAsync(string vmName, CancellationToken ct);
    }

    public interface IGuestAddressProvider
    {
        /// <summary>Guest-reported (KVP) addresses for THIS VM's adapters (by VM id). Untrusted input; empty is normal (no OS yet).</summary>
        Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct);
        /// <summary>Host-authoritative adapter facts for this VM (by VM id): MAC and the MAC-spoofing setting.</summary>
        Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct);
        /// <summary>Host neighbor table rows on guest-facing (vEthernet) interfaces: address → MAC as the host observed it.</summary>
        Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct);
        /// <summary>The host-side guest subnets, each bound to its switch and interface; empty ⇒ no address usable.</summary>
        Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct);
        Task<IReadOnlyList<System.Net.IPAddress>> GetHostAddressesAsync(CancellationToken ct);
    }

    public sealed record ForwardRequest(
        string RequesterPrincipal,
        ForwardRelationship Relationship,
        string TargetVm,
        string? Via,
        ForwardTarget Target,
        int VmPort,
        int ConnectPort,
        string Label,
        int MaxForwards);
    public enum ExposeStatus { Added, LimitReached, VmUnavailable, AddressUnverifiable, PolicyDenied }
    public sealed record ExposeResult(ExposeStatus Status, PortForward? Forward, string? Detail);
    public interface IAccessExposure
    {
        /// <summary>Validates the destination address (§12.5) and materializes under the target VM's forward gate.</summary>
        Task<ExposeResult> TryExposeAsync(ForwardRequest request, CancellationToken ct);
        Task<int> RevokeForRequesterAsync(string targetVm, string requesterPrincipal, CancellationToken ct);
        Task<int> RevokeNonOwnerAsync(string targetVm, CancellationToken ct);
        Task<IReadOnlyList<PortForward>> ListViaAsync(string viaVm, CancellationToken ct);
    }

    public sealed record NetworkRule(string Id, string VmName, string Peer, string Kind, string State, DateTimeOffset Created, DateTimeOffset Updated);
    public interface INetworkPolicyReconciler
    {
        /// <summary>"none" for every backend in this delivery; never "enforced" without an enforcing adapter.</summary>
        string IsolationLevel { get; }
        Task OnVmCreatedAsync(Vm vm, CancellationToken ct);
        Task OnVmDeletedAsync(string vmName, CancellationToken ct);
        Task OnSharingChangedAsync(Vm vm, SharingScope previous, CancellationToken ct);
        Task OnAddressChangedAsync(string vmName, IReadOnlyList<GuestAddress> addresses, CancellationToken ct);
        Task<int> ReconcileAsync(CancellationToken ct);
        Task<IReadOnlyList<NetworkRule>> ListRulesAsync(string vmName, CancellationToken ct);
    }

    public interface IHostNetworkPolicy
    {
        Task<NetworkConfig> GetAsync(CancellationToken ct);
    }

    // ---- ICapabilityAggregator.cs / IReleaseInfo.cs (owner: integrator) ----
    public interface ICapabilityAggregator
    {
        Task<BackendCapabilities> GetAsync(CancellationToken ct);
    }

    public interface IReleaseInfo
    {
        InstalledRelease Installed { get; }
        int SchemaVersion { get; }
        int SchemaMinReadableBy { get; }
        IReadOnlyList<string> ApiFeatures { get; }
    }
}
```

Fakes (`Constructd.Fakes`, stage 1, one file each): `InMemoryVmRepository` and
`SqliteVmRepository` implement `IVmDelegationRepository`; `InMemoryUserStore`/`SqliteUserStore`
implement `IUserAllowanceStore`; `InMemoryTokenService`/`SqliteTokenService` implement
`IVmTokenIssuer`; new `FakeChildVmDriver`, `FakeHypervisorInventory`,
`InMemoryCapacityLedger` (a real pure implementation over an in-memory list; the SQLite
one adds persistence), `InMemoryOperationKeyStore`, `InMemoryMediaStore`,
`FakeMediaTransfer` (temp-dir files, scripted failures), `UrlAdmissionRules` (real, pure),
`FakeConsoleTransport` (returns a fixed PNG and scripted return values),
`InMemoryConsoleSessionStore`, `InMemoryMaintenanceGate` (real), `InMemoryOperationRegistry`
(real), `InMemoryVmOperationGate` (real), `FakeReleaseSource`, `FakeUpdateStager`,
`FakeUpdaterLauncher`, `InMemoryHostUpdateStore`, `FakeGuestAddressProvider`,
`InMemoryAccessExposure`, `NoIsolationNetworkPolicy` (real), `InMemoryHostConfigStore`,
`FakeReleaseInfo`, `DelegationPolicy` (real, pure over the stores), `InMemoryAdmissionStore`
(real: one lock, applies the plan against the in-memory stores and the in-memory ledger),
`InMemoryMediaGate` (real), `FakeHostLock` (in-process stand-in for the cross-process
file lock, with a "held by another process" switch).

DI hooks (stage 1): `Composition/HostAdminComposition.cs` with
`AddHostAdminCore(options)` (registers every fake in fake mode and every real
implementation on Windows through per-feature extension methods `AddMediaPlatform`,
`AddCapacityPlatform`, `AddChildVmPlatform`, `AddConsolePlatform`, `AddUpdatePlatform`,
`AddNetworkPlatform`, each in its own file `Composition/<Feature>Composition.cs` owned by
the pair). `ServiceComposition.AddConstructdServices` gets **one line**:
`services.AddHostAdminCore(options);`. `Program.cs` gets **one line per feature** at a
marked block: `.MapHostAdminEndpoints().MapDelegationEndpoints().MapMediaEndpoints()
.MapConsoleEndpoints().MapUpdateEndpoints().MapNetworkEndpoints()`; hosted services
(`Api/Hosting/LeaseSchedulerService`, `CapacityReconciliationService`,
`MediaCleanupService`) are registered inside their feature composition, not in
`Program.cs`. `InProcessJobEngine` implements `IPersistedJobRunner` (child-jobs pair):
`StartPersistedAsync` takes the maintenance-gate handle and runs a job whose Queued row an
`AdmissionPlan` already wrote; the existing `SubmitAsync` keeps its signature and delegates
to the same runner after writing its own row (today's path, unchanged for `create-vm` and
`remove-vm` callers until the integrator moves them onto plans).

Authorization: `Auth/DelegationAuthorization.cs` (integrator) adds the policies of §2.1 and
the claim `constructd:vm-token-kind`; `VmTokenAuthenticationHandler` gains one line adding
that claim; `UserClaimsTransformation` gains the `Enabled` check;
`Auth/ForwardRequesterHandler.cs` (network pair) implements §12.3.

### 13.2 Ownership map

| Pair | Owns (new files unless stated) | Touches shared files (one-line hooks only) |
|---|---|---|
| **Integrator / delegation + sharing + lifecycle** | `Core/Domain/*` new records and enums (the block above); `Core/Abstractions/{IVmDelegationRepository, IUserAllowanceStore, IVmTokenIssuer, IDelegationPolicy, IHostConfigStore, IVmOperationGate, IMediaGate, IOperationRegistry, IAdmissionStore, ICapabilityAggregator, IReleaseInfo}.cs`; `Sqlite/SqliteAdmissionStore.cs` (one transaction over the per-pair SQL insert helpers `SqliteMediaStore.InsertInTransaction`, `SqliteCapacityLedger.ReserveInTransaction`, `SqliteOperationKeyStore.InsertInTransaction`, `SqliteJobStore.InsertInTransaction`, each owned by its pair and called only from here); `Core/Logic/{LeaseRules, DelegationRules, CascadeRules, LifetimeParser}.cs`; `Core/Services/{DelegationPolicy, InMemoryVmOperationGate, InMemoryOperationRegistry}.cs`; `Api/Hosting/LeaseSchedulerService.cs`; `Sqlite/Migrations/{SqliteMigrationRunner, ISqliteMigration, SqliteMigrations, M100_VmKindsAndDelegation}.cs`; `Sqlite/{SqliteHostConfigStore, SqliteVmDelegation (partial of SqliteVmRepository)}.cs`; `Api/Auth/DelegationAuthorization.cs`; `Api/Endpoints/{DelegationEndpoints, HostAdminEndpoints, IdentityExtensions, HealthEndpoints, IsoCatalogEndpoints}.cs`; `Api/Jobs/{ChildLifecycleJobs, CascadeJobs}.cs`; `Api/Contracts/{DelegationContracts, HostAdminContracts}.cs`; `Api/Infrastructure/CodedProblems.cs`; `Fakes/{InMemoryHostConfigStore, FakeReleaseInfo}.cs` and every fake listed above that is not named in another row; edits to `Sqlite/SqliteVmRepository.cs`, `SqliteUserStore.cs`, `SqliteTokenService.cs`, `SqliteJobStore.cs` (new columns), `Core/Services/IdlePolicyEngine.cs` (skip children), `Core/Domain/Vm.cs`, `User.cs`, `Job.cs`, `PortForward.cs` (additive params), `Api/Contracts/Responses.cs` (additive params on the five existing records), `Api/Contracts/Requests.cs` (additive `allowance`, `connectPort`, `via`, `cascade`) | `Program.cs` (health + delegation + host-admin map lines, the marked block), `ServiceComposition.cs` (`AddHostAdminCore`), `SqliteDatabase.cs` (call the runner), `Auth/TokenAuthenticationHandlers.cs`, `Auth/UserClaimsTransformation.cs`, `Auth/AuthorizationSetup.cs` (register new handlers), `Endpoints/VmEndpoints.cs` (kind-aware `/power` refusal, `DELETE` cascade branch, `GET /vms` query, capacity hooks of §4.6), `Endpoints/JobEndpoints.cs` (`JobReader`, `GET /jobs`, cancel), `Jobs/VmJobs.cs` (issue `Primary` token kind; reservation confirm/release) — **the integrator owns every conflict in these files** |
| **Media** | `Core/Abstractions/{IMediaStore, IMediaTransfer, IUrlAdmissionPolicy}.cs`; `Core/Logic/{UrlAdmissionRules, IsoSignature, MediaNameSanitizer}.cs`; `Sqlite/Migrations/M200_MediaRegistry.cs`; `Sqlite/SqliteMediaStore.cs`; `Windows/Media/{MediaFileStore, HttpMediaTransfer, PinnedAddressHandler}.cs`; `Api/Endpoints/MediaEndpoints.cs`; `Api/Jobs/MediaJobs.cs`; `Api/Contracts/MediaContracts.cs`; `Api/Hosting/MediaCleanupService.cs`; `Composition/MediaComposition.cs`; `Fakes/{InMemoryMediaStore, FakeMediaTransfer}.cs`; tests `Tests/Media/*` | `Program.cs` (`.MapMediaEndpoints()`), `SqliteMigrations.All` (one line), `HostAdminComposition.cs` (one line), installer (`Media:RootDir` hardening block, supplied to the updater pair) |
| **Capacity** | `Core/Abstractions/{ICapacityLedger, IHypervisorInventory}.cs`; `Core/Logic/{CapacityMath, ReservationRules}.cs`; `Core/Services/CapacityReconciler.cs`; `Sqlite/Migrations/M300_CapacityLedger.cs`; `Sqlite/SqliteCapacityLedger.cs`; `Windows/HyperV/HyperVInventory.cs` + the `Get-ConstructHostInventory` function in `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` (section marked `# capacity`); `Api/Endpoints/CapacityEndpoints.cs` (`/host/capacity`); `Api/Hosting/CapacityReconciliationService.cs`; `Composition/CapacityComposition.cs`; `Fakes/{InMemoryCapacityLedger, FakeHypervisorInventory}.cs`; tests `Tests/Capacity/*` | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`; call sites in `DelegationEndpoints`/`ChildLifecycleJobs`/`VmEndpoints`/`VmJobs` are **written by the integrator against the fake ledger**, so the capacity pair never edits them |
| **Child-VM driver + jobs** | `Core/Abstractions/{IChildVmDriver, IOperationKeyStore, IPersistedJobRunner}.cs`; `Core/Logic/{HardwarePresets, BootOrderRules, OperationFingerprint}.cs`; `Windows/HyperV/{HyperVChildDriver, HyperVChildScript}.cs`; `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` (create/remove/hardware/media/graceful shutdown/capabilities/VM id; the `# capacity` and `# network` sections belong to those pairs); `drivers/Load-ConstructDriver.ps1` `-Include` switch; `Api/Jobs/{ChildCreateJob, ChildDeleteJob}.cs`; `Sqlite/Migrations/M400_JobOperationKeys.cs`; `Sqlite/SqliteOperationKeyStore.cs`; `Core/Services/InProcessJobEngine.cs` (phase, operation keys, gate handle — additive), `Core/Abstractions/IJobEngine.cs` (overload + `SetPhaseAsync`); `Fakes/{FakeChildVmDriver, InMemoryOperationKeyStore}.cs`; tests `Tests/Windows/HyperVChildDriverTests.cs`, `Tests/Jobs/*`, `test/driver-contract.test.ps1` additions | `SqliteMigrations.All`; `HostAdminComposition.cs`; `Api/Endpoints/JobEndpoints.cs` only through the integrator |
| **Console** | `Core/Abstractions/{IConsoleTransport, IConsoleSessionStore}.cs`; `Core/Logic/{Rgb565Png (pure), ConsoleSessionRules}.cs`; `Windows/Console/{HyperVConsoleTransport, HyperVConsoleScript}.cs` (self-contained WMI scripts, stdin payload for text); `Api/Endpoints/ConsoleEndpoints.cs`; `Api/Contracts/ConsoleContracts.cs`; `Composition/ConsoleComposition.cs`; `Fakes/{FakeConsoleTransport, InMemoryConsoleSessionStore}.cs`; tests `Tests/Console/*` (PNG conversion pinned with the feasibility fixtures' layout: 4-byte big-endian length prefix + RGB565) | `Program.cs`, `HostAdminComposition.cs` |
| **Host release + updater** | `.github/workflows/host-release.yml`; `config/host-release.pub`; `service/host/{Update-ConstructHost.ps1, New-ConstructHostPackage.ps1}`; `Core/Abstractions/{IMaintenanceGate, IReleaseSource, IUpdateStager, IUpdaterLauncher, IHostUpdateStore, IHostLock}.cs`; `Windows/Updates/FileHostLock.cs`; `Api/Auth/UpdateHandoffAuthenticationHandler.cs` (loopback-only `UpdateHandoff` scheme, active only inside the update window); `Core/Logic/{ManifestRules, UpdateCompatibility, ZipEntryRules}.cs`; `Core/Services/InMemoryMaintenanceGate.cs`; `Sqlite/Migrations/M600_HostUpdates.cs`; `Sqlite/SqliteHostUpdateStore.cs`; `Windows/Updates/{GitHubReleaseSource, PackageStager, ScheduledTaskUpdaterLauncher, Ed25519Verifier}.cs`; `Api/Endpoints/UpdateEndpoints.cs`; `Api/Jobs/HostUpdateJob.cs`; `Api/Infrastructure/MaintenanceFilter.cs`; `Api/Admin/AdminDbCheck.cs` (`admin db check`); `Composition/UpdateComposition.cs`; `Fakes/*` for the five interfaces; tests `Tests/Updates/*`, `service/tests/host-updater.test.ps1`; installer changes (`-AclOnly`, `install.json`, `updates.manifestPublicKey`, media root block from the media pair, maintenance-marker check in `AdminCli`) | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`, `InProcessJobEngine.SubmitAsync` (gate handle — agreed with the child-jobs pair), `Install-ConstructHost.ps1` (sections marked), `Api/Admin/AdminCli.cs` (one verb + one marker check) |
| **Extension** | `extension/src/hostadmin.js`, `extension/media/hostadmin.*`, `extension/test/hostadmin.test.js`; additive helpers in `extension/src/remotehost.js` and `extension/src/drivers/hyperv-remote.js`; the `destination` branch in `extension/src/forwarder.js`; panel children rows in `extension/media/panel.js` (guarded by feature flag) | `extension/extension.js` (one registration block), `extension/package.json` (commands/menus), `extension/ARCHITECTURE.md` (new section) |
| **Guest CLI** | `bin/construct-vm.sh`, `test/construct-vm.test.sh` (bash, fake `curl`), `docs/child-vms.md` (user guide) | `bin/construct` (`vm)` case + usage text), `bin/provision.sh` (guest-report post, one function; `CONSTRUCT_PROVISION_EVENT`), `Provision-AgentVM.ps1` (`-RotateVmToken`, one block; attempt report), `docs/expose.md` (cross-link) |
| **Network** | `Core/Abstractions/{IGuestAddressProvider, IAccessExposure, INetworkPolicyReconciler, IHostNetworkPolicy}.cs`; `Core/Domain/ForwardDestination.cs`; `Core/Logic/{ForwardRelationshipRules, GuestAddressRules}.cs`; `Core/Services/NoIsolationNetworkPolicy.cs`; `Sqlite/Migrations/M700_NetworkPolicy.cs`; `Sqlite/SqliteNetworkRuleStore.cs`; `Windows/Network/HyperVGuestAddressProvider.cs` + `Get-ConstructVmAddresses` in `HyperVLocal.ChildVm.ps1` (section `# network`); `Api/Auth/ForwardRequesterHandler.cs`; `Api/Endpoints/NetworkEndpoints.cs` (`/addresses`); `Api/Contracts/NetworkContracts.cs`; `Composition/NetworkComposition.cs`; `Fakes/{FakeGuestAddressProvider, InMemoryAccessExposure}.cs`; tests | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`, `Api/Endpoints/ForwardEndpoints.cs` (policy name swap on the three routes, `via`, `?via=`; the **only** pair allowed to edit this file), `Sqlite/SqliteForwardStore.cs` (new columns), `Windows/Forwards/NetshPortForwardManager.cs` (connect address from the destination, one branch), `Api/Hosting/ForwardReconciliationService.cs` (address re-validation call, one line) |

Shared touchpoints and their owners:

| Shared file | Hook | Owner of the hook and of every conflict |
|---|---|---|
| `Api/Program.cs` | one `.Map<Feature>Endpoints()` line per pair inside the marked block | integrator |
| `Composition/ServiceComposition.cs` | `services.AddHostAdminCore(options);` | integrator |
| `Composition/HostAdminComposition.cs` | one `services.Add<Feature>Platform(options)` / fake line per pair | integrator |
| `Sqlite/SqliteDatabase.cs` | `SqliteMigrationRunner.Apply(...)` call | integrator |
| `Sqlite/Migrations/SqliteMigrations.cs` | one `new M<id>_…()` line per pair | integrator |
| `Api/Contracts/Requests.cs`, `Responses.cs` | additive optional parameters on the existing records only; pairs never edit (their DTOs live in `Contracts/<Feature>Contracts.cs`) | integrator |
| `Core/Domain/Enums.cs` | **not edited**; new enums live in `Domain/VmKinds.cs` | integrator |
| `Core/Abstractions/IHypervisorDriver.cs` | **not edited** (`BackendCapabilities` lives in `Domain/Capabilities.cs` and embeds `DriverCapabilities`) | – |
| `Api/Endpoints/ForwardEndpoints.cs` | policy swap, `via`, `?via=` | network pair |
| `Api/Endpoints/VmEndpoints.cs`, `JobEndpoints.cs`, `Jobs/VmJobs.cs`, `Auth/*` | listed hooks | integrator |
| `Core/Services/InProcessJobEngine.cs`, `Core/Abstractions/IJobEngine.cs` | overload, phase, operation key, gate handle | child-vm jobs pair (gate call agreed with the updater pair) |
| `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` | sections `# childvm`, `# capacity`, `# network` | child-vm pair owns the file; the other two own their marked sections |
| `drivers/Load-ConstructDriver.ps1` | `-Include` switch | child-vm pair |
| `bin/construct`, `bin/provision.sh`, `Provision-AgentVM.ps1` | `vm)` case, guest-report post, `-RotateVmToken` | guest CLI pair |
| `service/host/Install-ConstructHost.ps1`, `Api/Admin/AdminCli.cs` | `-AclOnly`, `install.json`, `Media:RootDir`, `updates.manifestPublicKey`, `db check`, maintenance marker | updater pair (media pair supplies its block) |
| `Api/Hosting/ForwardReconciliationService.cs` | address re-validation call | network pair |
| `extension/extension.js`, `extension/package.json` | one registration block | extension pair |

Rules for every pair: new code in new files; a shared file listed above is edited only at
its hook; anything else in a shared file goes through the integrator as a small separate
change; `Constructd.Core` keeps zero package references (hosted services live in
`Api/Hosting/`); every Windows call is argv through `IProcessRunner` and pinned by a
recording-runner test; every new PowerShell file runs on Windows PowerShell 5.1 (no
pwsh-only syntax; parsed under pwsh by `test/driver-contract.test.ps1` or the pair's own
suite); no secret in any log, exception, argument, job result or test output.

## 14. Acceptance checklists, limitations and field test

### 14.1 Per stage

| Stage | Scope | Acceptance (all on Linux unless stated) |
|---|---|---|
| **S1 seams** (integrator) | §13.1 block as real files, fakes, DI, migration runner + M100, `/health`, `/whoami` additions, `/vms/{name}/identity`, coded problems, token kinds + rotation route, `Vm`/`User`/`Job`/`PortForward` extensions, `IVmOperationGate`/`IOperationRegistry`, idle engine skips children | `dotnet build` 0 warnings; existing 638 tests green unchanged; `test/contracts-compile.test.sh` green (already green against the current Core for the block as written); new: migration runner tests (§1.4), pre-feature DB fixture keeps every row and reads back as primaries with legacy tokens, `create-vm` issues `primary` kind, rotation invalidates the old hash and answers 401 next, legacy token reaches exactly the six routes of §1.6 (route-matrix test extended), a disabled user's Bearer and VM tokens fail, `AddAsync` quota counts primaries only, route inventory test updated, audit-coverage test green for every new mutating route, `/health` anonymous body carries no commit |
| **S2 media** | §6, §8.10 | URL rules table pinned by tests (each refused class of §6.3; redirect re-validation; pinned connect address; unknown length; size cap; checksum mismatch cleanup); upload state machine (chunk after complete → `upload-not-open`, complete twice, abort during completing); resume and idempotent replay (needs M400 merged: the test is skipped with a reason until then); references block delete; an item in `deleting` cannot gain a reference and vice versa (interleaving test); chunk write racing complete (write admitted before the CAS finishes first; write after the CAS is refused), abort during hashing discards the result, expiry never touches `completing`, complete on done is 200 and on aborted is 409; media replacement reconciles references to `GetAttachedMediaAsync` (failure after the first attach keeps the attached item's reference; query failure keeps the superset and flags the VM); a failed create keeps references until rollback is confirmed; media completion (media gate → ledger `TrimAsync`) interleaved with an admission plan never deadlocks and never double-counts; dedicated media deleted with the VM; cleanup retains held-open files with reason; storage reservation equals artifacts that really remain after every failure path; owner-once accounting under admin cross-attach; no URL query in audit/logs (sentinel test); child creation never calls `IIsoBuilder`/`IIsoCatalog` (fake call recording); `GET /host/iso-catalog` projection |
| **S3 capacity** | §4, `/host/capacity` | concurrency test: two creates/starts cannot both take the last RAM/storage; pending rows owned by a live operation survive reconciliation in every VM state; orphaned pending rows are swept only when the operation is dead **and** the deadline passed; held released only on observed Off/Saved/Absent with no live operation; restart keeps its hold through the intermediate Off; `Unknown` keeps everything; external start creates an owner-charged hold; unmanaged VMs (including dynamic-memory ones charged at maximum) reduce host capacity without an owner; `Complete=false` fails closed in `enforce` and records in `observe`; growth-only storage math with a fixture volume across a trim (no double count, one epoch); `A_ram = min(model, physical)` pinned with the four worked examples of §4.1 (in particular 20 GiB non-VM host consumption plus an 8 GiB pending reservation ⇒ 0 available, and free 8 GiB with 4 GiB pending ⇒ 0); orphan resolution per resource (dead operation + VM Running ⇒ promoted, dead + Off + deadline ⇒ released, dead + Off before deadline ⇒ kept, storage artifact present ⇒ promoted, absent ⇒ released, unreadable ⇒ kept); enqueue-crash leftovers removed only with every liability confirmed absent (a leftover disk keeps the row and promotes the reservation); the restart interleaving of §4.1 (held reservation of an Off VM is unreflected); the `Unknown`/`Starting` rows of §4.1 (zero and partial assigned memory both refuse another 8 GiB); the stale-observation interleaving (snapshot Off → start confirms → reconciliation acquires the gate: nothing released, generation untouched; a VM whose generation moved since the snapshot is skipped); user aggregate across two primaries; shared start charged to owner; `observe` mode never refuses but audits; `POST /vms` and `/power start` hooks reserve/confirm/release; refusal bodies carry requested/allowed/available/epoch; recording-runner test pins the inventory script |
| **S4 child driver + jobs** | §8.6–8.8 jobs, `IChildVmDriver`, presets, boot-order filtering, operation keys, phases | recording-runner tests pin: create script sets template **before** TPM, fixed RAM, checkpoints off, DVD slots, boot order by device object, VM id read back; `UpdateHardwareAsync(resendTemplate:false)` never resends the template; graceful shutdown script uses `InitiateShutdown` and polls, never `-Force`/`-TurnOff`/`Save`; `RemoveAsync` may `-TurnOff` (pinned separately); job phases stream; operation-key acceptance is one transaction with the gate handle taken before it (a drain started between commit and runner start still waits for the job), a runner failure after a cascade acceptance leaves the tombstone and is retryable, replay returns the same job, a different fingerprint conflicts, generated names are stable; synchronous replay: renew crash-after-commit replays without a second renewal, sharing replay never overwrites a newer decision, start replay reconciles an `InFlight` intent (Running ⇒ activate with the intent's lifetime; Off ⇒ re-issue; never `already-running` for its own intent); cascade preview stored, token expiry, scope change on add/remove/re-create/sharing, no-children branch re-counts inside the fence transaction, children removed before the parent, parent tombstone on partial failure, retry after a terminal failed delete starts a new job; `driver-contract.test.ps1` parses the new driver file under pwsh; local loader without `-Include` is byte-identical (test diff) |
| **S5 console** | §8.12 | RGB565 → PNG conversion pinned against the feasibility layout (length prefix) with synthetic fixtures; dims above native refused before any process; byte cap; text travels through stdin (recording runner asserts no text in argv and no text in logs — sentinel); session TTL/renew/expiry/per-VM cap/rate buckets; per-VM device lookup by VM id, never a global first device (script pinned); key press/release/type semantics; mouse unavailable path returns `applied:false` with `returnValue`; shared caller allowed, legacy token refused, revocation removes sessions |
| **S6 delegation + sharing + lifecycle** | §2, §5, §8.3–8.9, §8.14 | permission matrix as a table-driven test (every cell of §2.2, including `allowedActions`); policy re-evaluated (allowance change refuses the next create without re-auth); allowance resolution pinned with the example table of §1.1 (explicit user value beats the default, caps bound users, overrides only tighten); every row of the §5.3a start table (paused resume without admission and with lease re-activation; Off with an active lease after an explicit shutdown restarts only with a new lifetime); `activationBase` for a create-with-start is the `<jobId>:start` intent's timestamp, not the create acceptance (a slow create does not consume the lease); a finite lease created at t0 (`1h`) and restarted at t0+10m keeps `ExpiresAt = t0+1h` after a normal restart **and** after a crashed-and-re-entered restart `start` phase — `ExpiresAt` and `Lease.Version` byte-identical, the RAM hold never released on the normal path; crash in the intermediate Off → reconciliation releases the hold → another VM consumes the capacity → the restart re-entry refuses (`capacity-exhausted`) without a hypervisor call and the lease is unchanged; crash in the intermediate Off with the original deadline already passed → re-entry fails `lease-due` and starts nothing; a crashed and re-entered `start` phase — even after the job's latest `phase` advanced and was rewritten — reads back the identical `activationBase` from the intent row (pinned with a MutableClock: the second insert attempt returns `Replay`, the timestamp is unchanged); power generation (§5.3b): start intent accepted → crash → shutdown by another caller → replay answers `power-state-changed` and never restarts; start intent accepted → crash before activation → reconciliation completes the intent from `activationBase` instead of marking external-start; delayed replay never extends the deadline; a replay whose reservations were swept re-admits under current policy or completes the key with the refusal; a replay whose `activationBase + lifetime` is already due completes with `intent-expired` and boots nothing; lease activation on confirmed Running, start requires lifetime, `already-running`, restart keeps expiry and refuses when due, service restart keeps expiry (MutableClock + fresh app over the same SQLite file), overdue path on `Unavailable`/`Timeout`/`paused` never saves/kills/deletes (fake driver call recording), external start → overdue with `expiresAt=now` and selected by the scheduler, expiry job superseded by a renewal that raced it (version check); sharing table and revocation tear-down; cascade dialog content; guest report never overwrites success facts with attempts; idle engine ignores children; initiator read/cancel rules |
| **S7 network** | §12 | primary-target forwards for owner/admin/the VM's own token take the existing path and serialize byte-identically (snapshot of request and response shapes for all three principals; `via`/`connectPort` on a primary → 400); relationship resolution order for children pinned; child cannot self-forward (no credential path exists); host target refused when either switch is off, also via parent and via shared; `via` must be owned by the requester; ack only by `via`'s owner; two shared consumers get separate rows and acks; address rules of §12.5 with fixtures: host forward to a child ⇒ `address-unverifiable` always; `destination.verified` serialized `false`; per-VM `network.hostForward` is `unsupported` for a child and `supported` for a primary; a `GuestSubnet` with an overlapping CIDR on another switch never matches; client tunnel with a KVP address ⇒ recorded with `verified: false`; a child reporting another managed VM's address (same MAC or not) ⇒ conflict, neither usable; wrong switch / overlapping subnet on another switch ⇒ not matched; host address / link-local ⇒ refused; primary host forwards unchanged; re-validation on reconciliation flips a stale forward to error; destination on child-target forwards, omitted for every primary-target forward (serialized-bytes test with the expose parser); `?via=` listing; addresses route; `NoIsolationNetworkPolicy` reports `none`, never `enforced`; netsh argv pinned with the verified connect address; extension forwarder tunnels through `via` (unit test with fake spawn) |
| **S8 extension** | §10 | node tests: state machine for every row of §10.3, admin module absent for local and for `role=user`, host switching re-resolves identity, old-service detection by 404, maintenance banner, cascade dialog content lists children and shared flags and expiry, child rows offer exactly Shut down and Delete, Shut down sends `lifecycle shutdown` (never `power save`), Media tab shows the ISO catalog projection, no guest update/provision/reinstall action in the module (snapshot test of the command list); `ui-smoke.js` green |
| **S9 guest CLI** | §9 | `test/construct-vm.test.sh` with a fake `curl`: identity gate (legacy → exit 9), every command's request shape (including attach/detach/hardware), create waits for media readiness and derives sub-keys, JSON pass-through, NDJSON progress, exit-code table, `--yes` gating on non-TTY, operation-id reuse on retry and conflict handling, token never in argv, console text only from stdin/file (fake curl asserts `-H @file` and no text in argv); `remote-e2e.test.sh` extended with a child create/list/shutdown/delete round trip against the fake service |
| **S10 host release + updater** | §11 | workflow file validated (actionlint or schema check in tests) including the `main`-ref guard; `New-ConstructHostPackage.ps1` produces a payload whose SHA256SUMS covers every file and a detached manifest whose hashes match (pwsh test); `Update-ConstructHost.ps1` tested under pwsh with a fake service directory, the **documented nested layout** (`ScriptsDir=C:\Construct`, `PublishDir=C:\Construct\service\publish`) and a stub `Start-Service`/`Stop-Service`/`schtasks` layer: phases, list-based replace never touching publish/data/media/tools/keys, deletion of removed files only, backup reuse on resume, health loop, rollback with and without DB restore per manifest, `recoveryFailed` leaves the backup intact, record written at every phase; service tests: manifest signature/hash tampering refused, extraction rules, downgrade/compat rules exactly as §11.3 step 3, `signing-key-missing`, drain gate refuses gated kinds and chunk writes with `503 maintenance`, admits nothing behind the zero-handle check (interleaving test), lets ungated ones through, maintenance freezes every mutation, hand-off order (row and file before task), startup rules for old/new binary, a dead updater leaves the new binary in `maintenance` until `resolve`, `resolve` refused while a paused updater holds `updater.lock` (race test with a held lock) and a fenced `-Resume` refuses to roll back, backup-complete marker (interruption during backup before replace ⇒ rebuilt; after `ReplaceStarted` with an incomplete backup ⇒ `recoveryFailed`), `admin.lock` held by a running admin CLI operation blocks drain until released or the drain times out (race test), the full service→updater lock sequence of §7.4 with two fake processes (service releases at hand-off, updater acquires before stop, `db check` runs read-only without the lock while the updater holds it), the startup/fence table of §11.7 row by row (old binary restarted during backup with a live updater stays in maintenance; with a dead updater writes a `closed` fence and reopens, after which `-Resume -Rollback` is refused as `superseded` and the stale backup is discarded; a `commitOnly` fence survives a service restart with the gate open and takes precedence over an earlier `recoveryFailed` outcome; `commit` on the old binary over a partially replaced tree is refused `update-not-commitable`; `commit` on the new executable with an old child-driver script (or a stale updater script) passes HTTP/DB health but is refused `installation-mixed` until repaired, and succeeds after the file set verifies; `close` succeeds only when the previous file list hashes verify; `recoveryFailed` → repair → `resolve` → restart stays open; resolve-abort → completed rollback (terminal outcome written, fence retired to `closed`) → new writes → a later `-Resume -Rollback` exits `already-terminal` and restores nothing; a crash between writing the terminal outcome and retiring the fence converges to open on the verified old binary at the next start; a resumption after `succeeded` is a no-op; new binary with a dead updater stays in maintenance; `resolve` and the resume form of `apply` pass the real maintenance filter while a fresh `apply` and every other mutation get 503), the loopback `UpdateHandoff` handshake returns the full health body without a bootstrap token, satisfies no other policy and is refused off-loopback or outside the window, config `requiredKeys` refusal, `TryStartAsync` serialization, `admin db check` opens the database |
| **S11 integration** | all | `remote-e2e.test.sh` full round trip; route inventory and audit coverage tests cover every route of §8; `dotnet build` 0/0; all pwsh/bash/node suites green; docs updated (`service/README.md`, `docs/remote-host.md`, `docs/drivers.md`, `docs/expose.md`, `extension/ARCHITECTURE.md`, new `docs/child-vms.md`, `docs/host-updates.md`); the field-test checklist below handed to the owner |

### 14.2 Documented limitations (this delivery)

| Limitation | Why | Where reported |
|---|---|---|
| Nothing in this run was executed on Hyper-V or on the real host service; Linux suites prove only that existing behaviour is unchanged | D3 | every stage summary must say "validated on Linux with fakes"; §14.3 is the owner's checklist |
| Interactive console (VMConnect/RDP/VNC) is unsupported | D2 | `console.interactive = unsupported`; no route |
| Mouse absolute is `conditional`; preboot pointer input failed (32768) on the Gen 2 probe; relative mouse unsupported on Gen 2 | feasibility | per-VM capabilities, `applied:false` answers |
| Ordinary-key visual effects, text entry into a guest OS, keyboard layouts and Unicode are unverified | feasibility | `notes[]` in capabilities |
| LocalSystem execution of the WMI console/child scripts is unverified | feasibility | `notes[]`; field test item 6 |
| Guest address reporting is `conditional` and unverified (needs guest integration services, which the probe never observed) | feasibility | `network.directAddressReporting`; field test item 4 |
| Generation 1 children unsupported | not probed | `generations: [2]` |
| Disk position in the boot order was not probed | feasibility | `bootOrder: conditional`, `notes[]` |
| Secure Boot template cannot change after TPM initialization | Hyper-V | `template-locked` |
| No memory overcommit, no dynamic memory; the `DynamicMemory` seam is reserved and refused | policy | capabilities `unsupported` |
| Pass-through/physical disk attachment | Initial inventory cannot prove physical-disk/host-volume exclusion; reports `passthrough-disk-unavailable` | Inventory incomplete; enforce admission unavailable while attached, observe remains usable |
| Capacity enforcement is `observe` on every migrated host until an admin switches it | zero-change | `capacityMode` in `/health`-authenticated views, `/host/status`, `/whoami` |
| The primary ISO catalog build in flight is not reserved in the ledger | bounded by the admin-configured source size; catalog files are physically counted | §4.6 |
| Reference-counted collection of Construct-generated (catalog) ISOs is deferred; the catalog keeps its `current.pointer`/prune retention | zero-change on the primary path | §6.6 |
| Network isolation is not enforced; rules are recorded only | no enforcing adapter | `isolation: "none"` on every address/forward answer |
| Client forwards to a child require a guest-reported address reachable from the requester's primary | tunnel via `via` | forward `status: error` until an address is reported |
| No media content download; auxiliary contents never leave the host | secrets in answer files | §6.2 |
| UDF-only ISOs accepted only with a checksum | signature check limits | `not-an-iso` |
| Updates require a signed manifest and a stored public key; unsigned only in fake mode; main ancestry is trusted through the signed `ref`, not verified offline | trust | `unsigned-manifest`, `signing-key-missing` |
| The updater's scheduled-task hand-off, list-based replacement and rollback are tested with stubs under pwsh, not on Windows | D3 | field test items 13–14 |
| Child destination addresses are never verified on Hyper-V: host forwards to children are refused and client tunnels to children carry `verified: false` (a spoofing child on the same switch could receive the tunnel) | no IP allocation authority | §12.5, `network.addressVerification = unsupported`, CLI warning |
| An update that ends `interrupted` keeps the host in maintenance until an admin resolves it (except the old-binary-before-replace case, which fences and reopens itself) | fencing over availability | §11.7 |
| `POST /users` old shape keeps `maxVms` default 0 | zero-change | §8.4 |
| Sharing scope `selected` is stored but never grantable | reserved | `sharing-scope-unsupported` |
| No automatic keepalive; explicit renewal only | wall-clock lease | §5.3 |
| Guest reports depend on `bin/provision.sh` posting; older guests never report (`unknown`) | additive | `guest.provenance = unknown` |
| A parent tombstone after a partially failed cascade is never garbage-collected automatically | keeps every child parented | §8.8; Operations tab retry |

### 14.3 Field-test checklist (for the owner, real Hyper-V host, later run)

Preconditions: a disposable second host or the existing host with a maintenance window;
the current constructd backed up (`C:\ProgramData\Construct\service`, publish dir,
scripts dir); an admin token; a user account; one existing primary (`haus-vm` class) that
must keep running throughout; `capacity.mode` switched to `enforce` for items 7–9.

| # | Check | Pass criterion |
|---|---|---|
| 1 | Install the new build with the installer; run `GET /health` anonymously and authenticated | reduced vs full body as §8.1; `schemaVersion` matches; existing VM listed as `kind=primary`, `tokenKind=legacy`; its `construct expose` and heartbeat still work; `capacityMode=observe` |
| 2 | Rotate the primary's token via reprovision `-RotateVmToken` | old token → 401; `construct vm identity` shows `primary` |
| 3 | `construct vm media acquire` a public Ubuntu server ISO with checksum; then one Windows evaluation ISO by upload (interrupt and resume it once) | both `ready`, sizes and hashes match, storage reservation visible in `/host/capacity`, the resumed upload completes with the same media id |
| 4 | `construct vm create` Linux preset (2 CPU, 2048 MB, 20 GB, `2h`) and Windows preset (4 CPU, 4096 MB, 60 GB, `4h`, TPM) | both reach `running`; Windows Secure Boot template `MicrosoftWindows`, TPM enabled; screenshot shows the installer; `incarnation` equals `Get-VM .Id`; capacity numbers add up against `Get-VM`; after guest integration services come up, `addresses` reports the guest address with `verified: false`, and a host forward request for the child answers `address-unverifiable` |
| 5 | Console keyboard/mouse on the Linux child during the installer | screenshot changes after keys; mouse `applied:false` reported truthfully if the installer has no pointer support |
| 6 | Run the WMI scripts under the service (LocalSystem) | screenshot and keyboard succeed from the API, not only from an interactive shell |
| 7 | Concurrency: two `start` requests for two saved children when only one fits in RAM | exactly one succeeds; the other gets `capacity-exhausted` with correct numbers and epoch |
| 8 | Start a child outside the API (`Start-VM`) | reconciliation charges it to the owner and marks the lease `overdue` with `expiresAt=now`; the expiry job shuts it down gracefully |
| 9 | Let a `15m` lease expire on a child with integration services; and on one without; renew a third child 10 s before expiry | first: `expired`, VM Off, RAM released; second: `overdue`, `guest-shutdown-unavailable`, VM still running, RAM still charged, no save/force/delete; third: the queued expiry job ends `superseded` and the VM keeps running |
| 10 | Share a child host-wide; from a second user's primary: inspect, start, screenshot, request a client forward (via that user's own primary) and a host forward; attempt delete | inspect/start/screenshot/client forward succeed and are charged/audited to the owner with the second user as initiator; the client link opens on the **second** user's PC with the unverified warning; the host forward answers `409 address-unverifiable`; delete → 403 |
| 11 | Disable host forwards; request host forward for a child via parent and via shared caller (both refused as unverifiable even with forwards enabled); revoke sharing | refused; client forwards still work through the requester's primary with the unverified warning; after revocation the shared consumer's tunnel is torn down on the next poll |
| 12 | Delete the parent primary with one private and one shared child; interrupt the cascade once (stop the service mid-job) | preview lists both with the shared flag and expiry; typed name required; after the interruption the parent is a tombstone with the remaining child, a repeated `DELETE` finishes; all three gone; media references released; dedicated media removed; no orphan files under the media root; unrelated `haus-vm` untouched |
| 13 | Stage an update from a real `host-*` release; apply while a media acquire is running; use the documented nested layout | `draining` waits for the acquire; new child creates and chunk writes get `503 maintenance`; existing VMs keep running; the new binary answers `maintenance` until the updater commits; clients reconnect; `install.json` and `/host/updates/status` report the pinned commit; `service\publish` and `service\host` both intact |
| 14 | Apply a deliberately broken package (tampered SHA256SUMS; then a build whose health check fails); kill the updater once mid-`replace` and resume | first refused at verify; second rolls back automatically, status `rolledBack`, DB intact; the resumed run reuses the backup and completes; `last-update.json` readable with the service stopped |
| 15 | Old client (pre-change extension/PS) against the new service | every existing flow (create, provision, expose, idle, remove) behaves identically; `GET /vms/{self}/forwards` is byte-compatible for the guest CLI |

Record the outcome of each item, the release commit, and any capability that had to be
downgraded to `unsupported`, in `docs/plans/host-administration-field-test.md`.

## 15. Requirements traceability

Every requirement of the two plan documents mapped to its contract section and its
acceptance line (S-numbers of §14.1) or its limitation (§14.2).

| Requirement (source) | Contract | Acceptance / limitation |
|---|---|---|
| Admin module only for remote hosts where the identity is admin; per-host identity; hiding is presentation only (req. "Host administration") | §10.2, §10.3, §2 | S8, S6 |
| Host registration/connection independent of a VM; users self-provision their first primary (req.) | §10.2 "Host connect / register" | S8 |
| Admin areas: Overview, VMs, Users, Media (primary source/patched status + child inventory), Operations, Configuration, Maintenance (req. table) | §8.2, §8.3, §8.4, §8.10, §8.18, §8.16, §8.15, §10.2 | S8, S2 (`/host/iso-catalog`) |
| Guest inventory: installed commit, last provision, last reinstall distinct; attempts separate; provenance; unknown valid; boot ≠ install (req.; plan P1) | §8.3 `guest`/`observed`, §8.14 | S6 |
| No guest update/provision/reinstall in host administration (req.; plan P4) | §10.5 | S8 |
| Minimal user view: primaries with children; Shut down (graceful) + Delete only (req.; plan P4) | §10.2, §5.5, §8.7 | S8 |
| Admin registers users and allowances; primaries inherit delegation; policy evaluated per request (req.; plan P3) | §1.5, §8.4, §2.3, §1.6 | S1, S6 |
| Defaults table (primary count, child creation, retained children, CPU/RAM/storage budgets, lifetime, mandatory inputs, sharing) (req.) | §1.5 `userDefaults`, §4.6, §5.1, §8.6 | S3, S6 |
| Aggregate budgets across all primaries and children; stopped/saved children occupy slots and storage; overrides cannot bypass limits (req.; plan P0) | §4.6, §1.1 overrides | S3, S6 |
| Every child has one parent; owner inherited; parent credentials operate own + shared children only; children get no credentials; nothing baked into media (req.; plan P3) | §1.1, §2.2, §8.6 job, §1.6 | S6, S4 |
| Sharing: private/host only, `selected` reserved; host sharing grants operational management; delete/ownership/sharing/hardware/media stay owner/admin; charged to owner (req.; plan P3) | §1.1 `SharingScope`, §2.2, §8.9, §4.6 | S6 |
| Parent deletion deletes all children incl. shared; confirmation lists children and shared/permanent removal; protect against children created between confirmation and deletion (req.; plan P3) | §8.8, §10.4 | S4, S8 |
| Child creation: public URL or upload, optional checksum, auxiliary ISO with access control, powered-off creation, explicit CPU/RAM/disk, capability-checked firmware/Secure Boot/TPM/boot order, presets cannot fill resources, reject unsupported before allocating, truthful transfer/boot reporting (req.; plan P2) | §6, §8.6, §3 | S2, S4 |
| CLI: create/list/inspect, start/resume, restart, shutdown, save, delete, sharing, media upload/attachment, console; machine-readable results, operation ids, progress, exit codes, idempotent retries; connection details tolerant of no IP (req.; plan P3) | §9 | S9 |
| Console: screenshot + keyboard/mouse transport, works during boot, backend capability level, no VNC promise (req.; plan P0/P5; D2) | §3.2, §8.12, §13.1 | S5; limitations (interactive, mouse) |
| Lifetime is a wall-clock lease; mandatory request; `never` when allowed; start/resume needs new lifetime; sharing does not reset; reboot/service restart never renew; expiry action confirmed (req.; plan P0; D1) | §5 | S6 |
| Explicit deletion removes disks, saved state, dedicated media; shared media by reference; never delete an attached image or an admin-supplied source; failed cleanup visible and retryable (req.) | §6.5, §8.8, §7.1 `child-delete` | S2, S4; §6.6 limitation for catalog ISOs |
| Capacity: quotas and host capacity independent; unmanaged VMs; fixed RAM, no overcommit; full disk reservation; release after confirmed transition; atomic start/resume admission; headroom; saved-state files; physical vs reserved without double counting; serialized admission; reconciliation after failure/restart (req.; plan P0/P2) | §4 | S3 |
| Future dynamic memory/ballooning capability seam; Proxmox and overcommit deferred (req.) | §3.2, `ChildHardware.DynamicMemory` | limitation |
| Network: only primaries/users request connectivity; client/host/direct modes; host forwarding independently disableable; distinct target identity; guest addressing / exposure / firewall seams; reconcile on create/sharing/delete; no isolation claim (req.; plan P5) | §12, §8.11 | S7 |
| Host updates: installed version, one-click from main, pinned commit, previous install retained, self-contained package + scripts + manifest, ISO tool separate, drain gate, no wait on guest activity, replace/restart/health, VMs keep running, clients reconnect, no automatic retry without a key, updater outside the process, settings preserved, no installer rerun with defaults, migration compatibility before rollback, recovery record (req.; plan P6; D5) | §11, §7.4, §7.3 | S10 |
| API: `/api/v1` kept; structured capacity errors with requested/allowed/available; audit actor/effective owner/parent/target/operation; revoke delegation on primary deletion or user disable (req.) | §8, §8.17, §1.8, §2.4 | S1, S6 |
| Migration of existing VMs to primary with preserved owner/keys/identity; legacy tokens gain no privileges; credential upgrade path (plan P0; D4) | §1.3, §1.6, §8.13 | S1 |
| Eligible shared callers: users and authenticated primaries on the same host, never unauthenticated clients or child guests (plan P0) | Terminology, §2.1, §2.2 | S6 |
| Hyper-V console feasibility result incl. access before an OS (plan P0) | feasibility report; §3.2 | — |
| Graceful-shutdown timeout and unsupported-guest handling (plan P0; D6) | §1.5 `lifecycle`, §5.5 | S6, S4 |
| Powered-off creation activation, restart vs start/resume renewal, host downtime, keepalive decision, no reset on service restart (plan P0) | §5.3 | S6 |
| Media: registry separate from the catalog with shared primitives; upload limits, partial cleanup, resumability, unknown Content-Length, redirect/target validation (plan P2) | §6 | S2 |
| Child creation does not assume Ubuntu, inject credentials or wait for SSH (plan P2) | §8.6 job | S4 |
| Linux ISO and Windows-compatible hardware demonstrated on Hyper-V (plan P2) | §14.3 item 4 | deferred (D3) |
| Scoped primary credentials and effective delegation limits exposed to the CLI (plan P3) | §8.1 identity, §9.2 | S1, S9 |
| Revocation affects new operations and sessions per documented policy (plan P3) | §2.4 | S5, S6, S7 |
| Cascade job persisted, partial failures recorded, retry, parent closed to creation (plan P3) | §8.8, §1.2 `cascades` | S4 |
| Extension: native views over the API, states for denial/old service/unavailable, admin visibility follows host identity, no host filesystem access (plan P4) | §10 | S8 |
| Network adapters: define seams even where enforcement is deferred; no nominal isolation adapter (plan P5) | §12.1, §12.2 | S7 |
| Release: durable published distribution, not Actions artifacts; trusted provenance and hashes; retention (plan P6) | §11.1–11.4 | S10 |
| Real Hyper-V validation of the update with an active VM and a child before rollout (plan P6) | §14.3 items 13–14 | deferred (D3) |
| Report what was exercised on Hyper-V vs simulated, release commit, migration/rollback limits, unavailable capabilities (plan "Delivery") | §14.2, §14.3, every stage summary | S11 |

## Integration notes (stage 0)

Integrated on 2026-09-07 in `ha/integ-0`, based on `8c02b47`. Merged
`ha/s0-feasibility` (`00d53be`) and then `ha/s0-contracts` (`fad2193`) with
`--no-ff`; neither branch was empty and neither merge conflicted. Both branches'
contents were retained. The incoming change includes this contract, the feasibility
report, its two PNG evidence files, and `test/contracts-compile.test.sh`. The test
script is a deviation from the documents-only brief, retained to validate the
documented signatures; no production code changed. This integration adds only these
notes. The frozen design was not revised.

Validation ran on Linux in this worktree. `dotnet build service/Constructd.sln`
completed with **0 warnings, 0 errors**. All requested suites were run once,
sequentially; only the four suites with fixture/environment failures were rerun.
The fake-service end-to-end test used `CONSTRUCT_E2E_PORT=17913` and stopped its
service at exit. No real host service or VM was touched by the integrator. The
feasibility branch contains its own separately attributed Hyper-V probe evidence;
these baseline results are not Hyper-V validation.

### Initial failures and scoped reruns

No merge-induced production defect was found. The affected tests and production
files are unchanged from `8c02b47`. Existing fixture assumptions remain as follows:

| Suite | Initial result | Cause and rerun setup | Final result |
|---|---|---|---|
| Node `configsync` | Runner aborted after 432 passing checks | Bare Git fixtures assume `main`; this VM has no `init.defaultBranch` setting and Git defaults to `master`. Reran with the process-only Git configuration below. | 475 passed |
| PowerShell `config-sync` | 561 passed, 7 failed | Same default-branch assumption; same process-only configuration. | 568 passed |
| Bash `idle-report` | 96 passed, 4 failed | Extracted `write_configuration` runs with nounset but its fixture omits `T3CODE_BUILD_SOURCE`; three assertions fail after the early exit. `systemd-analyze verify` also reports unrelated installed `jarvis-link-wake.service` environment warnings. Reran with `T3CODE_BUILD_SOURCE=prebuilt` (the production default) and `SYSTEMD_UNIT_PATH=/usr/lib/systemd/system`, still verifying the actual repository units. | 100 passed |
| Bash `provision-diskcheck` | 22 passed, 2 failed | Fixture diagnoses `/home/agent`, which does not exist on this root-only VM. Created an empty directory for the rerun and removed it immediately afterward. | 24 passed |

Git setup for each config-sync rerun (no repository/global config changed):

```sh
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=init.defaultBranch GIT_CONFIG_VALUE_0=main node extension/test/configsync.test.js
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=init.defaultBranch GIT_CONFIG_VALUE_0=main pwsh -NoProfile -File test/config-sync.test.ps1
T3CODE_BUILD_SOURCE=prebuilt SYSTEMD_UNIT_PATH=/usr/lib/systemd/system bash test/idle-report.test.sh
```

The test fixtures were left unchanged to keep this integration within stage 0.
A plain full-suite run on the same unconfigured VM will still encounter these
fixture failures; the scoped reruns establish the passing baseline with the
fixtures' prerequisites supplied.

### Per-suite results

Counts below are the suites' reported checks, not an assertion-instrumented total.
`t3-desktop-handoff` and `t3-reprovision-host` each print one successful scenario
group without a total for their internal assertions. `provision-seed-user` prints
seven successful groups. No `-DownloadBase` was supplied to `t3-reprovision-host`,
so its optional HTTP-download checks did not run; its native Windows launch branch
also did not run on Linux.

| Family | Suites | Passed checks/groups | Failed after scoped reruns | Explicit skips |
|---|---:|---:|---:|---:|
| .NET | 1 | 638 | 0 | 0 |
| Node | 22 | 4686 | 0 | 1 |
| PowerShell | 16 | 2995 | 0 | 1 |
| Bash | 19 | 800 | 0 | 0 |

The Node forwarder skip is the unwritable-spool case under root. The PowerShell
remote-client skip is the Windows-only DPAPI round-trip. The host-installer suite
is also invoked inside the .NET suite; these family totals are not a count of
unique assertions across languages.

| Suite | Passed checks/groups | Failed | Explicit skips |
|---|---:|---:|---:|
| `dotnet-test` | 638 | 0 | 0 |
| `extension/test/audio.test.js` | 233 | 0 | 0 |
| `extension/test/configsync.test.js` | 475 | 0 | 0 |
| `extension/test/drivers.test.js` | 66 | 0 | 0 |
| `extension/test/forwarder.test.js` | 673 | 0 | 1 |
| `extension/test/host.test.js` | 118 | 0 | 0 |
| `extension/test/importui.test.js` | 24 | 0 | 0 |
| `extension/test/instances.test.js` | 1528 | 0 | 0 |
| `extension/test/instancestate.test.js` | 105 | 0 | 0 |
| `extension/test/lifecycle.test.js` | 265 | 0 | 0 |
| `extension/test/notify.test.js` | 103 | 0 | 0 |
| `extension/test/probe.test.js` | 98 | 0 | 0 |
| `extension/test/project-set.test.js` | 63 | 0 | 0 |
| `extension/test/projects.test.js` | 167 | 0 | 0 |
| `extension/test/remote.test.js` | 71 | 0 | 0 |
| `extension/test/remotehost.test.js` | 150 | 0 | 0 |
| `extension/test/repatch.test.js` | 39 | 0 | 0 |
| `extension/test/t3code.test.js` | 89 | 0 | 0 |
| `extension/test/themes.test.js` | 43 | 0 | 0 |
| `extension/test/updates.test.js` | 143 | 0 | 0 |
| `extension/test/usage.test.js` | 131 | 0 | 0 |
| `extension/test/vmpower.test.js` | 81 | 0 | 0 |
| `extension/test/zip.test.js` | 21 | 0 | 0 |
| `test/config-sync.test.ps1` | 568 | 0 | 0 |
| `test/driver-contract.test.ps1` | 99 | 0 | 0 |
| `test/host-lib.test.ps1` | 309 | 0 | 0 |
| `test/instance-cleanup.test.ps1` | 124 | 0 | 0 |
| `test/instance-identity.test.ps1` | 242 | 0 | 0 |
| `test/instance-state.test.ps1` | 64 | 0 | 0 |
| `test/instances.test.ps1` | 781 | 0 | 0 |
| `test/native-iso-host.test.ps1` | 23 | 0 | 0 |
| `test/notify-toast.test.ps1` | 22 | 0 | 0 |
| `test/provision-seed-user.test.ps1` | 7 | 0 | 0 |
| `test/remote-client.test.ps1` | 93 | 0 | 1 |
| `test/remote-driver.test.ps1` | 87 | 0 | 0 |
| `test/remote-install.test.ps1` | 206 | 0 | 0 |
| `test/t3-desktop-handoff.test.ps1` | 1 | 0 | 0 |
| `test/t3-reprovision-host.test.ps1` | 1 | 0 | 0 |
| `service/tests/host-installer.test.ps1` | 368 | 0 | 0 |
| `test/autoinstall-iso.test.sh` | 59 | 0 | 0 |
| `test/construct-expose.test.sh` | 170 | 0 | 0 |
| `test/construct-notify.test.sh` | 36 | 0 | 0 |
| `test/contracts-compile.test.sh` | 4 | 0 | 0 |
| `test/export-config.test.sh` | 12 | 0 | 0 |
| `test/external-host.test.sh` | 61 | 0 | 0 |
| `test/idle-report.test.sh` | 100 | 0 | 0 |
| `test/opencode-install.test.sh` | 20 | 0 | 0 |
| `test/partial-streaming.test.sh` | 6 | 0 | 0 |
| `test/patch-status.test.sh` | 12 | 0 | 0 |
| `test/provision-diskcheck.test.sh` | 24 | 0 | 0 |
| `test/provision-hostname.test.sh` | 28 | 0 | 0 |
| `test/provision-marker.test.sh` | 32 | 0 | 0 |
| `test/provision-steprunner.test.sh` | 17 | 0 | 0 |
| `test/remote-e2e.test.sh` | 38 | 0 | 0 |
| `test/restore-config.test.sh` | 25 | 0 | 0 |
| `test/systemprompt-install.test.sh` | 17 | 0 | 0 |
| `test/t3-https.test.sh` | 133 | 0 | 0 |
| `test/vscode-download.test.sh` | 6 | 0 | 0 |

## Deviations

Stage 2 media:

- URL admission conservatively reserves `maxBytes` before opening the transfer;
  it does not perform a separate HEAD/header probe. The transfer checks declared
  length before creating a partial file and trims the reservation to actual bytes
  at completion. This keeps the accepted job and its full possible liability in
  one admission transaction, at the cost of temporarily reserving more storage
  for smaller downloads even when the eventual response has Content-Length.

- Chunk mutations are audited as `media.upload.chunk`, following the explicit
  implementation brief's every-mutation audit requirement. The table lists audit
  names only for begin/complete/abort; no content or inner file name is recorded.
- Cleanup uses a one-second linked cancellation timeout when taking media gates
  because the frozen gate has no non-blocking acquisition method. Busy items are
  returned as retained with reason `busy`; explicit deletion refuses pending or
  transferring media before waiting. A held-file delete queues an item-only retry.

- Added `IMediaStore.CompleteUploadAsync` for the required atomic upload-Done /
  media-Ready transition. The two existing independent CAS methods cannot commit
  that pair atomically. SQLite uses one immediate transaction; the fake uses its
  shared transaction lock. Existing interface signatures are preserved.
- Uses the foundation's `Constructd:HostAdmin:Media:RootDir` configuration property
  for the contract's `Media:RootDir`, avoiding an unrelated shared options edit.
- Narrow shared-test updates register the eleven media routes in the exhaustive
  API surface test and correct the foundation fake ISO fixture to contain the
  full primary descriptor/version (and expect the safe media exception).
- Production admission, capacity and persisted job execution remain owned by their
  respective parallel branches. Media does not replace those placeholders with
  non-atomic writes. Its SQL insertion helpers and job bodies are ready for their
  composition; tests inject the shared in-memory seams and a recording runner.

- Added `IMediaFiles` beside the frozen media seams for sparse creation, streaming
  reads, ranged writes, atomic publish, and timestamped enumeration. The frozen
  `IMediaTransfer` lacks those operations; its signatures remain unchanged. This
  supports both the confined filesystem adapter and a byte-only in-memory fake.
- Updated the foundation migration assertions to include M200. These assertions
  previously fixed the entire registry to the stage-1-only range and count; this
  is a narrow shared-test integration change required by the new migration.

Stage 2 capacity implementation:

- Capacity composition wraps `IDelegationPolicy` with ledger-backed usage in both
  memory/fake and SQLite modes. Resolution and action policy remain delegated to the
  integrator-owned implementation; no policy method or shared source is rewritten.
- Pass-through attachments conservatively return `passthrough-disk-unavailable`.
  Enforce mode is unavailable on such hosts until a physical-disk/volume mapping is
  implemented and field-tested; observe mode remains available. The adapter does not
  assume an unverified physical disk consumes zero host-volume capacity.

- Added optional `InventorySnapshot.Artifacts`, `CapacityArtifactInfo` and a defaulted
  `IHypervisorInventory.ReadAsync(reservations, ct)` overload. The original seam has no
  evidence for retained/unattached VHDs, upload partials or media paths; per-artifact
  presence and byte counts must be collected in the same pass as physical free space.
  Existing implementations still compile and missing evidence remains conservative.
  `HostCapacitySnapshot.Problems` is optional so the specified reporting DTO can carry
  the actual inventory problem codes without a second epoch.
- The capacity-owned SQL helpers live on the disposable
  `SqliteCapacityLedger.Transaction` returned by `BeginAsync`. It owns the common gate,
  inventory preparation, connection and IMMEDIATE transaction; integrator-owned
  `SqliteAdmissionStore` uses that scope for plan/mutation writes. This makes the
  shared transaction lifetime explicit without changing `IAdmissionStore` signatures.
- Added `ICapacityReconciliationStore` for the database half of the reconciler. Its
  SQLite implementation uses that same transaction scope and compares the captured
  VM generation/job fence. The frozen `IAdmissionScope` cannot insert an external hold
  without admission or update observed state, and the SQLite coordinator is not yet
  implemented in the integrated base. Child intent/lease handling and abandoned-create
  row/reference removal remain integrator-owned; capacity never deletes those rows
  on a guessed absence or invents a lease activation timestamp.
- The new feature needs minimal updates to existing route/schema/composition assertions
  that explicitly pinned the stage-1-only surface (schema 100, no capacity route,
  unsupported production ledger). The migration and route registrations are one-line
  shared hooks. No legacy provisioning/lifecycle call site is changed here.


Stage 1 foundation:

- `cascades.parent_incarnation` is nullable. Section 1.2's `NOT NULL` conflicts
  with section 13.1's explicit nullable incarnation for migrated primaries and
  creates in flight; strict equality still includes null.
- Added `IHostConfigMetadata` alongside the frozen `IHostConfigStore`. The API
  requires source/timestamp metadata and atomic multi-section compare-and-set,
  which the generic single-section interface cannot express. Existing signatures
  remain unchanged.
- Until each backend lands, discovery advertises only `host-admin`; child,
  console, network and update capabilities are unsupported. Production adapters
  refuse unimplemented operations. The existing primary capabilities remain as
  reported by the existing driver. A later stage adds its feature name when its
  routes and backend are installed.
- Added `IVmMetadataStore` for field-specific incarnation and credential writes.
  `IVmRepository.UpdateAsync` and `IUserStore.UpdateAsync` retain their legacy
  column scope (plus `User.Enabled`), consistently in both stores. Reports,
  observations, leases, token kinds and allowances use explicit seams, so an
  old lifecycle snapshot cannot silently erase newer metadata.

- Added `IJobQueryStore` and `IUserTokenRevoker` alongside the legacy stores to
  expose active jobs and per-user credential revocation without changing the
  frozen legacy interfaces or third-party decorators.
- The user-PC report hook lives in `Provision-AgentVM.ps1` and
  `lib/AgentVm.Remote.ps1`, as explicitly required by this stage's task. The
  later guest-CLI pair may add the `provision.sh` hook; both reporters are
  accepted. Completion reporting is advisory and only occurs after the final
  guest result; earlier transport failures leave success facts unchanged.
- `ForwardEndpoints.cs` has a minimal stage-1 hook checking the stored host
  forwarding switch and refusing the new optional destination fields until the
  network pair implements validation. The default primary path is unchanged.
- The legacy create-user request still requires `maxVms` when `allowance` is
  omitted (the existing implementation never defaulted it to zero). The new
  allowance shape permits omission and uses `userDefaults.maxPrimaries`.
- Stage-1 fake capacity uses a supplied inventory epoch and conservatively
  retains reservations without absence evidence. Full reconciliation/owner
  accounting follows in the capacity stage. Fake child exposure refuses an
  unverifiable address until the network stage supplies destination validation.
- SQLite admission and `IPersistedJobRunner` are explicitly unsupported until
  the later per-feature transaction helpers and child-job runner exist. The
  real in-memory admission seam is implemented now, with atomic rollback over
  all participating stores; database-only callbacks must complete synchronously
  because its store operations perform no I/O. Feature route-map hooks exist
  without mapping future routes or advertising success.

- Ordinary VM snapshot updates preserve the current credential hash and can
  only set, never reopen, the deletion fence. Credential issuance/revocation
  uses `IVmMetadataStore`; setting a deletion fence clears the hash. This
  prevents an in-flight state/idle refresh from resurrecting a rotated or
  revoked credential (or undoing a concurrent deletion).
  The existing enqueue-failure rollback is retained through the explicit
  `RestoreUnqueuedDeletionAsync` metadata method under the same VM operation
  gate as rotation; ordinary snapshot writes cannot invoke that rollback.

### Stage 2 child driver/jobs deviations and integration hooks

- Generated child names truncate parent prefixes to 58 characters (trimming a
  trailing hyphen) before the stable four-character suffix, so a valid long
  parent can create children without an explicit name.
- Child ownership markers also record a unique configuration directory before
  `New-VM -Path`. A missing ID can be recovered only when the VM configuration
  path matches that directory; matching the VM name alone is insufficient.
- The in-memory admission scope allows its caller to atomically complete a
  refused re-admission intent: `ReserveAsync` returns a refusal without marking
  the entire scope conflicted. The caller decides whether to commit the refusal
  response or return false to roll back. This implements the frozen §7.3 rule.

- The authoritative optional child-driver file is used; the existing
  `HyperVLocal.Driver.ps1` is unchanged. Capacity/network implementation sections
  remain reserved for their owners even though the task also mentions inventory
  and addresses. Child jobs consume those foundation seams.
- Added `IChildVmStorage` for pre-admission disk/configuration volume resolution;
  the frozen descriptor alone cannot resolve the Hyper-V defaults before capacity
  is reserved. Added `IChildVmCreationOwnership.CreateOwnedAsync` and its ownership query so rollback can distinguish its own partial create
  from a competing external VM. The disk sidecar persists this ownership and paths
  for retries after `Remove-VM` has succeeded.
- Creation endpoints/DTOs live in separate `ChildVmEndpoints`/`ChildVmContracts`
  files. Minimal dispatcher, phase-SSE, composition and migration registration
  hooks touch integrator-owned files to make the requested increment executable.
  The `JobEventKind.Phase` enum addition is necessary for the frozen phase event.
- Disk creation and media attachment occur inside `IChildVmDriver.CreateAsync`
  (as its frozen descriptor implies); the job's following `disk`/`attach` phases
  verify completion and attachments. No primary provisioning algorithm is changed.
- The fake media transfer's disposal is made idempotent: activating the child job
  resolves its concrete/interface registrations, and DI disposes that same
  instance twice. This is required for the new API tests to dispose their hosts.
- SQLite admission, inventory, capacity and media implementations still await their
  owning branches. No child discovery flag is advertised until integration.
  Owner/admin create access is implemented with the existing delegation-policy
  hook; primary-token creation remains for stage 3 as scoped by the task.

## Integration notes (stage 1)

Integrated on 2026-09-07 in `ha/integ-1`, after resetting to `feat/host-admin`
(`143d016`). Merged `ha/s1-foundation` (`54d7ca1`, `7c91f26`) with `--no-ff`
as `b3e067d`. The branch had two commits beyond the base; no branch was skipped.
The merge had **no conflicts** and retained all 157 incoming changed files. No
production or test fixes were required; this integration adds only these notes.

The foundation adds the Core contracts and fakes, feature composition hooks,
M100 and the migration runner, host/user administration and VM inventory,
current-policy credential checks and rotation, and advisory provisioning reports
through the existing PowerShell workflow. Its implementation deviations remain
listed above under **Deviations**; integration changed no frozen design decision.
Only `host-admin` is advertised. Child execution, media, capacity enforcement,
console, network enforcement and host updates await their implementation stages;
SQLite admission and the persisted child-job runner remain explicitly unsupported.

### Validation and environment

All **59 test suites plus the solution build** ran once, sequentially, on Linux.
`dotnet build service/Constructd.sln` completed with **0 warnings, 0 errors**.
`dotnet test service/Constructd.sln` passed **706 tests**, 68 above the 638-test
baseline. Core still has zero package references. `git diff --check` passed.

The stage-0 fixture prerequisites were supplied on the first run, without changing
repository/global Git settings or test sources:

- Node `configsync` and PowerShell `config-sync`: process-only
  `GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=init.defaultBranch GIT_CONFIG_VALUE_0=main`.
- Bash `idle-report`: `T3CODE_BUILD_SOURCE=prebuilt` and
  `SYSTEMD_UNIT_PATH=/usr/lib/systemd/system`.
- Bash `provision-diskcheck`: an empty `/home/agent` created only for that suite
  and removed immediately afterward.
- Fake-service end-to-end: `CONSTRUCT_E2E_PORT=17923`; all 38 checks passed, the
  test stopped its service, and the port was verified closed afterward.
- MSBuild server/node reuse and shared compilation were disabled for this run to
  avoid leaving compiler processes running. No test-owned dotnet/node/pwsh
  processes remained after the matrix.

These results are Linux tests with fakes/recording runners, **not Hyper-V or
Windows PowerShell 5.1 execution**. No real host service or VM was touched. The
stage-0 feasibility report remains separately attributed probe evidence.

### Per-suite results

Counts are reported checks, except `provision-seed-user` (seven successful
scenario groups) and `t3-desktop-handoff` / `t3-reprovision-host` (one group each,
with no reported internal assertion count). The host-installer suite also runs
inside the .NET suite, so family totals are not unique cross-language assertions.

| Family | Suites | Passed checks/groups | Failed | Explicit skips |
|---|---:|---:|---:|---:|
| .NET | 1 | 706 | 0 | 0 |
| Node | 22 | 4686 | 0 | 1 |
| PowerShell | 17 | 3014 | 0 | 1 |
| Bash | 19 | 801 | 0 | 0 |

The two explicit skips are the Node forwarder's unwritable-spool case under root
and PowerShell's Windows-only DPAPI round-trip. As in stage 0,
`t3-reprovision-host` ran without `-DownloadBase`: its optional HTTP-download
checks and native Windows launch branch were not exercised.

| Suite | Passed checks/groups | Failed | Explicit skips |
|---|---:|---:|---:|
| `dotnet-test` | 706 | 0 | 0 |
| `extension/test/audio.test.js` | 233 | 0 | 0 |
| `extension/test/configsync.test.js` | 475 | 0 | 0 |
| `extension/test/drivers.test.js` | 66 | 0 | 0 |
| `extension/test/forwarder.test.js` | 673 | 0 | 1 |
| `extension/test/host.test.js` | 118 | 0 | 0 |
| `extension/test/importui.test.js` | 24 | 0 | 0 |
| `extension/test/instances.test.js` | 1528 | 0 | 0 |
| `extension/test/instancestate.test.js` | 105 | 0 | 0 |
| `extension/test/lifecycle.test.js` | 265 | 0 | 0 |
| `extension/test/notify.test.js` | 103 | 0 | 0 |
| `extension/test/probe.test.js` | 98 | 0 | 0 |
| `extension/test/project-set.test.js` | 63 | 0 | 0 |
| `extension/test/projects.test.js` | 167 | 0 | 0 |
| `extension/test/remote.test.js` | 71 | 0 | 0 |
| `extension/test/remotehost.test.js` | 150 | 0 | 0 |
| `extension/test/repatch.test.js` | 39 | 0 | 0 |
| `extension/test/t3code.test.js` | 89 | 0 | 0 |
| `extension/test/themes.test.js` | 43 | 0 | 0 |
| `extension/test/updates.test.js` | 143 | 0 | 0 |
| `extension/test/usage.test.js` | 131 | 0 | 0 |
| `extension/test/vmpower.test.js` | 81 | 0 | 0 |
| `extension/test/zip.test.js` | 21 | 0 | 0 |
| `test/config-sync.test.ps1` | 568 | 0 | 0 |
| `test/driver-contract.test.ps1` | 99 | 0 | 0 |
| `test/host-admin-client.test.ps1` | 19 | 0 | 0 |
| `test/host-lib.test.ps1` | 309 | 0 | 0 |
| `test/instance-cleanup.test.ps1` | 124 | 0 | 0 |
| `test/instance-identity.test.ps1` | 242 | 0 | 0 |
| `test/instance-state.test.ps1` | 64 | 0 | 0 |
| `test/instances.test.ps1` | 781 | 0 | 0 |
| `test/native-iso-host.test.ps1` | 23 | 0 | 0 |
| `test/notify-toast.test.ps1` | 22 | 0 | 0 |
| `test/provision-seed-user.test.ps1` | 7 | 0 | 0 |
| `test/remote-client.test.ps1` | 93 | 0 | 1 |
| `test/remote-driver.test.ps1` | 87 | 0 | 0 |
| `test/remote-install.test.ps1` | 206 | 0 | 0 |
| `test/t3-desktop-handoff.test.ps1` | 1 | 0 | 0 |
| `test/t3-reprovision-host.test.ps1` | 1 | 0 | 0 |
| `service/tests/host-installer.test.ps1` | 368 | 0 | 0 |
| `test/autoinstall-iso.test.sh` | 59 | 0 | 0 |
| `test/construct-expose.test.sh` | 170 | 0 | 0 |
| `test/construct-notify.test.sh` | 36 | 0 | 0 |
| `test/contracts-compile.test.sh` | 5 | 0 | 0 |
| `test/export-config.test.sh` | 12 | 0 | 0 |
| `test/external-host.test.sh` | 61 | 0 | 0 |
| `test/idle-report.test.sh` | 100 | 0 | 0 |
| `test/opencode-install.test.sh` | 20 | 0 | 0 |
| `test/partial-streaming.test.sh` | 6 | 0 | 0 |
| `test/patch-status.test.sh` | 12 | 0 | 0 |
| `test/provision-diskcheck.test.sh` | 24 | 0 | 0 |
| `test/provision-hostname.test.sh` | 28 | 0 | 0 |
| `test/provision-marker.test.sh` | 32 | 0 | 0 |
| `test/provision-steprunner.test.sh` | 17 | 0 | 0 |
| `test/remote-e2e.test.sh` | 38 | 0 | 0 |
| `test/restore-config.test.sh` | 25 | 0 | 0 |
| `test/systemprompt-install.test.sh` | 17 | 0 | 0 |
| `test/t3-https.test.sh` | 133 | 0 | 0 |
| `test/vscode-download.test.sh` | 6 | 0 | 0 |

### Defects

No merge-induced defects or test failures were found, and no branch defects were
recorded for deferral. The existing fixture prerequisites and platform coverage
limits above still apply; passing this matrix does not establish host rollout
readiness.
