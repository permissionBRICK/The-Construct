# Host administration contracts (Phase 0 design amendment)

Status: **frozen contract** for the host-administration and child-VM delivery.
Date: 2026-09-07. Branch: `ha/s0-contracts`.
Inputs: [requirements](host-administration-and-child-vms.md),
[implementation plan](host-administration-implementation.md),
[Hyper-V feasibility report](host-administration-hyperv-feasibility.md) (lands from branch
`ha/s0-feasibility`; the link resolves once both branches are merged), the current service (`service/src`), the PowerShell driver contract
([docs/drivers.md](../drivers.md)), the remote host guide ([docs/remote-host.md](../remote-host.md))
and the expose contract ([docs/expose.md](../expose.md)).

This document is what every implementation pair builds against. Where it and the
requirements differ, the requirements win on product intent and this document wins on
names, shapes, codes and ownership. Where the requirements left a technical choice open,
the choice is made here and marked **decided here** with one line of rationale. Nothing in
this document exists in code yet unless it says "existing".

Regression boundary, restated because every section depends on it: the local single-VM
install, the existing primary flows (`Auto-Install.ps1` remote path, `Provision-AgentVM.ps1`,
`construct expose`, idle policy, forwards, the four VM-token routes) and every existing
token behave identically after this delivery. Every schema change is additive with a
default that reproduces today's behaviour; every new API field is optional or nullable;
every new route is new.

## 0. Decisions of 2026-09-07 (recorded, not reopened)

| Id | Decision | Effect in this contract |
|---|---|---|
| D1 | Lease expiry = graceful guest shutdown, same as the user-panel stop. Failure or an unsupported guest shutdown is reported as such. No force-off, no save fallback, never deletion at expiry. | §5.5, §7 job `vm-shutdown`, error `guest-shutdown-unavailable`, inventory flag `leaseOverdue`. |
| D2 | Console = screenshot + keyboard + mouse (absolute move/click; relative fallback where the synthetic mouse is unavailable), service-side through Hyper-V WMI as far as the feasibility report proves. Interactive video (VMConnect/RDP) is unsupported and reported so by the capability model. | §3 console capabilities, §8.9 console routes, §13 `IConsoleTransport`. |
| D3 | Real-host deployment and validation are deferred: this run validates on Linux (fakes, `--fake` API, pwsh, node, bash); host access is limited to relay probes. A field-test checklist is written for a later run by the owner. | §14.3 field-test checklist; every "verified on Hyper-V" claim is forbidden in this run. |
| D4 | Credential upgrade for existing primaries: owner/admin token rotation `POST /vms/{name}/token` invalidating the previous hash; token kinds `legacy` (today's four routes) and `primary` (own routes plus delegation). Reprovision injects the rotated token. | §1.6, §2, §8.13, §9.2 (guest discovery via `GET /vms/{name}/identity`). |
| D5 | Host release distribution: GitHub Releases on `permissionBRICK/The-Construct` from a new Actions workflow on `main` (self-contained win-x64 service package + matching scripts + `manifest.json` with commit, hashes, schema/config compatibility). The updater pins the resolved commit. The ISO tool keeps its own pinned release (`config/iso-builder.json`, `docs/native-iso.md`). | §11 |
| D6 | Graceful shutdown timeout default 300 s, configurable per host; a structured error when integration services are absent. | `HostConfig.lifecycle.gracefulShutdownTimeoutSeconds`, error `guest-shutdown-unavailable`. |

## Terminology

| Term | Meaning |
|---|---|
| **primary** | A Construct VM created through the existing `POST /vms` path. Owned by a user. Existing rows all become primaries. |
| **child** | A general-purpose ISO-booted VM created by `POST /vms/{parent}/children`. Exactly one parent (a primary on the same host); its owner and quota payer is the parent's owner. |
| **owner** | The user record a VM is charged to. Never a VM. |
| **primary token** | The VM-scoped token of a primary whose `tokenKind` is `primary`. |
| **legacy VM token** | A VM-scoped token issued before this delivery, or rotated with `kind=legacy`. |
| **shared caller** | An enrolled user, or a primary token of kind `primary`, acting on a child whose sharing scope is `host` and whose owner is somebody else. |
| **child guest** | Whatever runs inside a child. It holds no Construct credential of any kind. |
| **effective owner** | The user a shared operation is charged to and audited against: always the child's owner. |
| **stage** | A delivery increment of §14; stage 1 is the seams stage that lands before the pairs go parallel. |

## 1. Domain and persistence

### 1.1 Record changes (Constructd.Core.Domain)

All additions are new optional record parameters with defaults, so every existing
constructor call, fake and test compiles unchanged.

| Record | Change | Notes |
|---|---|---|
| `Vm` | `+ VmKind Kind = VmKind.Primary`, `+ string? Parent = null`, `+ SharingScope Sharing = SharingScope.Private`, `+ VmTokenKind TokenKind = VmTokenKind.Legacy`, `+ int? RamMb = null`, `+ Lease? Lease = null`, `+ ChildHardware? Hardware = null`, `+ GuestReport? Guest = null`, `+ HostObservation? Observed = null`, `+ bool ChildCreationClosed = false`, `+ string? CurrentJobId = null` | `RamBytes` computed property: `RamMb is int mb ? mb * 1 MiB : RamGb * 1 GiB`. Children always carry `RamMb`. `Parent` is null for primaries and never null for children. |
| `User` | `+ bool Enabled = true`, `+ UserAllowance Allowance = UserAllowance.Unset` | `MaxVms` keeps its meaning and becomes the **primary count** quota (existing rows keep their value). `AllowHostForwards` unchanged. |
| `Job` | `+ string? OperationKey = null`, `+ string? Phase = null`, `+ string? RequesterVm = null` | `Owner` stays the effective owner user; a primary-token submission sets `RequesterVm`. |
| `PortForward` | `+ ForwardTargetIdentity? Target = null` | Null means today's self forward (the VM's own port). See §12.3. |
| `AuditEntry` | no schema change | Detail format standardized, §1.8. |
| `VmDescriptor` | unchanged | Children use `ChildVmDescriptor` (§13). |

New records and enums (all in `Constructd.Core.Domain`, one file per concept, listed in
§13.1 with full signatures):

| Name | Fields |
|---|---|
| `enum VmKind` | `Primary, Child` |
| `enum SharingScope` | `Private, Host, Selected` — `Selected` is **reserved**: stored, rejected by every API write with `400 sharing-scope-unsupported`, never granted. |
| `enum VmTokenKind` | `Legacy, Primary` |
| `record Lease` | `string RequestedText` (as typed, e.g. `4h`, `never`), `long? RequestedSeconds` (null = never), `DateTimeOffset? ActivatedAt`, `DateTimeOffset? ExpiresAt`, `LeaseState State`, `DateTimeOffset? LastExpiryAttemptAt`, `string? LastExpiryOutcome` |
| `enum LeaseState` | `Inactive` (never started), `Active`, `Unlimited`, `Expired` (shutdown confirmed), `Overdue` (expiry action failed or unsupported; retried) |
| `record ChildHardware` | `int Cpus`, `int RamMb`, `int DiskGb`, `int Generation`, `bool SecureBoot`, `SecureBootTemplate? SecureBootTemplate`, `bool Tpm`, `IReadOnlyList<BootDevice> BootOrder`, `bool NetworkAttached` |
| `enum SecureBootTemplate` | `MicrosoftWindows, MicrosoftUefiCertificateAuthority` (wire: `microsoftWindows`, `microsoftUefiCertificateAuthority`) |
| `enum BootDevice` | `InstallMedia, AuxiliaryMedia, Disk, Network` |
| `record GuestReport` | `string? ConstructCommit`, `DateTimeOffset? ProvisionedAt`, `DateTimeOffset? ReinstalledAt`, `DateTimeOffset? ReportedAt`, `GuestReportProvenance Provenance`, `DateTimeOffset? LastAttemptAt`, `string? LastAttemptOutcome` |
| `enum GuestReportProvenance` | `Unknown, Provisioner` |
| `record HostObservation` | `DateTimeOffset? CreatedAt` (host-side creation), `DateTimeOffset? LastBootAt` (first `Running` observed after `Off`/`Saved`), `IReadOnlyList<GuestAddress> Addresses` |
| `record GuestAddress` | `string Address`, `AddressFamilyKind Family` (`Ipv4, Ipv6`), `GuestAddressSource Source` (`Kvp, Dhcp, Unknown`), `DateTimeOffset ObservedAt` |
| `record UserAllowance` | `bool? AllowChildCreation`, `int? MaxRetainedChildren`, `int? CpuBudget`, `long? RamBudgetBytes`, `long? StorageBudgetBytes`, `long? MaxChildLifetimeSeconds`, `bool? AllowNeverLifetime`, `bool? AllowSharing` — every field nullable; null = "use host `userDefaults`". `UserAllowance.Unset` = all null. |
| `record VmOverride` | `string VmName`, `bool? AllowChildCreation`, `int? MaxRetainedChildren`, `long? MaxChildLifetimeSeconds`, `bool? AllowNeverLifetime`, `bool? AllowSharing` |
| `record EffectiveAllowance` | resolved, non-nullable view: `int MaxPrimaries`, `bool AllowChildCreation`, `int MaxRetainedChildren`, `int? CpuBudget`, `long? RamBudgetBytes`, `long? StorageBudgetBytes`, `long? MaxChildLifetimeSeconds` (null = unlimited), `bool AllowNeverLifetime`, `bool AllowSharing`, `bool AllowHostForwards` |
| `record MediaItem` | §6.1 |
| `record MediaReference` | `string MediaId`, `string VmName`, `MediaSlot Slot` (`Install, Auxiliary`) |
| `record MediaUpload` | §6.4 |
| `record Reservation` | §4.5 |
| `record HostConfig` | §1.5 |
| `record ForwardTargetIdentity` | §12.3 |
| `record HostUpdateRecord` | §11.8 |

**Per-VM override semantics (decided here).** Overrides carry the delegation switches
only (`AllowChildCreation`, `MaxRetainedChildren`, `MaxChildLifetimeSeconds`,
`AllowNeverLifetime`, `AllowSharing`) and apply to actions delegated through that primary.
Numeric resource budgets are user-level only. The effective value for an action through
primary P owned by U is `override(P) ?? allowance(U) ?? hostDefaults`, and the user's
aggregate budgets always apply on top. Rationale: an override that could raise a budget
per VM would reintroduce the per-VM admin step the requirements reject; exceptions are
made by editing the user allowance.

### 1.2 SQLite schema additions

All columns are nullable or carry a default that reproduces today's behaviour. Existing
rows are never rewritten by a migration; existing SQL in `SqliteVmRepository` keeps
reading `SELECT *` and gains the new columns through the reader.

| Table | Column / change | Type and default | Migration |
|---|---|---|---|
| `vms` | `kind` | `TEXT NOT NULL DEFAULT 'primary'` | 100 |
| `vms` | `parent` | `TEXT NULL COLLATE NOCASE` | 100 |
| `vms` | `sharing` | `TEXT NOT NULL DEFAULT 'private'` | 100 |
| `vms` | `vm_token_kind` | `TEXT NOT NULL DEFAULT 'legacy'` | 100 |
| `vms` | `ram_mb` | `INTEGER NULL` | 100 |
| `vms` | `child_creation_closed` | `INTEGER NOT NULL DEFAULT 0` | 100 |
| `vms` | `current_job_id` | `TEXT NULL` | 100 |
| `vms` | `lease_requested_text`, `lease_requested_seconds`, `lease_activated_at`, `lease_expires_at`, `lease_state`, `lease_last_attempt_at`, `lease_last_outcome` | `TEXT NULL`, `INTEGER NULL`, `TEXT NULL`, `TEXT NULL`, `TEXT NULL`, `TEXT NULL`, `TEXT NULL` | 100 |
| `vms` | `hardware_json` | `TEXT NULL` (camelCase JSON of `ChildHardware`) | 100 |
| `vms` | `guest_construct_commit`, `guest_provisioned_at`, `guest_reinstalled_at`, `guest_reported_at`, `guest_provenance`, `guest_last_attempt_at`, `guest_last_attempt_outcome` | all `TEXT NULL`; `guest_provenance` default `'unknown'` | 100 |
| `vms` | `observed_created_at`, `observed_last_boot_at`, `observed_addresses_json` | `TEXT NULL` | 100 |
| `vms` | index `ix_vms_parent ON vms (parent)` | | 100 |
| `users` | `enabled` | `INTEGER NOT NULL DEFAULT 1` | 100 |
| `users` | `allow_child_creation`, `max_retained_children`, `cpu_budget`, `ram_budget_bytes`, `storage_budget_bytes`, `max_child_lifetime_seconds`, `allow_never_lifetime`, `allow_sharing` | all `NULL` (INTEGER) | 100 |
| `vm_overrides` (new) | `vm_name TEXT PRIMARY KEY COLLATE NOCASE`, five nullable INTEGER columns as in `VmOverride`, `updated_at TEXT NOT NULL` | | 100 |
| `host_config` (new) | `key TEXT PRIMARY KEY`, `value_json TEXT NOT NULL`, `updated_at TEXT NOT NULL`, `updated_by TEXT NOT NULL` | one row per config section (§1.5) | 100 |
| `jobs` | `requester_vm` | `TEXT NULL COLLATE NOCASE` | 100 |
| `schema_migrations` (new) | `id INTEGER PRIMARY KEY`, `name TEXT NOT NULL`, `breaking INTEGER NOT NULL`, `applied_at TEXT NOT NULL`, `applied_by_commit TEXT NOT NULL` | | runner |
| `media`, `media_references`, `media_uploads` (new) | §6.1, §6.4 | | 200 |
| `reservations` (new) | §4.5 | | 300 |
| `jobs` | `operation_key TEXT NULL`, `phase TEXT NULL` | | 400 |
| `job_operation_keys` (new) | `owner TEXT NOT NULL COLLATE NOCASE`, `kind TEXT NOT NULL`, `operation_key TEXT NOT NULL`, `job_id TEXT NOT NULL`, `target TEXT NOT NULL`, `created TEXT NOT NULL`, `PRIMARY KEY (owner, kind, operation_key)` | | 400 |
| `host_updates` (new) | §11.8 | | 600 |
| `forwards` | `target_vm TEXT NULL COLLATE NOCASE`, `target_via TEXT NULL COLLATE NOCASE`, `target_connect_address TEXT NULL`, `requested_by TEXT NULL` | | 700 |
| `network_rules` (new) | §12.4 | | 700 |

### 1.3 Migration of existing rows

| Existing fact | After migration 100 |
|---|---|
| Every row in `vms` | `kind='primary'`, `parent=NULL`, `sharing='private'`, no lease, no hardware, `guest_provenance='unknown'` (guest facts are reported later by the guest, §8.14). Name, owner, CPU/RAM/disk, SSH forward port, idle policy and `deleting` are untouched. |
| `vm_token_hash` | Preserved byte-for-byte. `vm_token_kind='legacy'`: the hash keeps exactly today's four routes plus the two additive read/report routes of §2.2. |
| Every row in `users` | `enabled=1`; `max_vms` unchanged and now read as the primary-count quota; every allowance column NULL (= host defaults, §1.5). |
| Tokens (`tokens` table) | Untouched. |
| Forwards, jobs, audit, activity | Untouched; new columns NULL. |

No data is copied, transformed or deleted. Migration 100 is a set of `ALTER TABLE … ADD
COLUMN` and `CREATE TABLE IF NOT EXISTS` statements in one transaction.

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
| Reserved id ranges | 100–199 delegation/sharing/lifecycle (integrator), 200–299 media, 300–399 capacity, 400–499 child jobs, 500–599 console (reserved, no migration expected), 600–699 host release/updater, 700–799 network. Later features continue at 800. |
| Applied order | Ascending `Id`. A migration may depend only on the base schema and on lower ids **from range 100** (the integrator's), never on another pair's range. |
| Idempotence | Each migration is recorded in `schema_migrations` and never re-run. The statements themselves must still be safe to re-run (`IF NOT EXISTS`, column probe) so a crash between statement and record is harmless. |
| Additive only | New tables, nullable columns or defaulted columns. Renames, drops and type changes are forbidden in this delivery; a migration needing them sets `Breaking = true` and is refused by review unless §11.7 rollback rules are updated with it. |
| Version exposure | `GET /health` reports `schemaVersion = SchemaVersions.SchemaVersion` and `schemaMinReadableBy`; the update manifest carries the same two numbers (§11.2). |
| Tests | `SqlitePersistenceTests` gains: fresh database applies all; a pre-feature database (fixture SQL of today's schema with rows) applies all and keeps every row; applying twice is a no-op; `All` ids are unique and sorted; every migration id sits in its owner's range. |

### 1.5 Host configuration (`host_config`) and defaults

Host policy lives in the database, not in `appsettings.Production.json`, because it is
edited through the API by admins and must survive the updater's config preservation
without merges (**decided here**; `appsettings` keeps only bootstrap/platform values).
One row per section, JSON value, camelCase.

| Key | Fields | Default (when the row is absent) |
|---|---|---|
| `capacity` | `ramHeadroomBytes: long`, `storageHeadroomBytes: long`, `cpuBudget: int?` (active vCPU cap, null = none), `maxVcpusPerVm: int?` (null = host logical CPUs), `reconcileSeconds: int`, `pendingReservationTimeoutSeconds: int` | `ramHeadroomBytes = max(4 GiB, 12.5 % of total RAM)` computed at read when absent; `storageHeadroomBytes = 20 GiB`; `cpuBudget = null`; `maxVcpusPerVm = null`; `reconcileSeconds = 60`; `pendingReservationTimeoutSeconds = 600` |
| `userDefaults` | `maxPrimaries: int`, `allowChildCreation: bool`, `maxRetainedChildren: int`, `cpuBudget: int?`, `ramBudgetBytes: long?`, `storageBudgetBytes: long?`, `maxChildLifetimeSeconds: long?`, `allowNeverLifetime: bool`, `allowSharing: bool` | `1, true, 1, null, null, null, null (unlimited), true, true` — exactly the requirements table. `maxPrimaries` applies only to users created through the new admin API (§8.4); the existing `POST /users` and admin CLI keep their explicit `maxVms`. |
| `lifecycle` | `gracefulShutdownTimeoutSeconds: int`, `leaseTickSeconds: int`, `leaseRetrySeconds: int` | `300` (D6), `30`, `600` |
| `media` | `maxBytes: long`, `maxItemsPerUser: int`, `uploadChunkBytes: int`, `uploadTtlHours: int`, `acquireTimeoutMinutes: int`, `allowHttp: bool` | `16 GiB`, `20`, `8 MiB`, `24`, `180`, `true` (http only with a checksum, §6.3) |
| `network` | `hostForwardsEnabled: bool`, `directAccessReporting: bool` | `true`, `true` |
| `updates` | `repository: string`, `channel: string`, `drainTimeoutMinutes: int`, `healthTimeoutSeconds: int`, `requireSignature: bool`, `manifestPublicKey: string?` | `"permissionBRICK/The-Construct"`, `"main"`, `60`, `120`, `true`, `null` (then no update can be verified until set; the installer writes it) |

`ConstructdOptions` gains `HostAdmin` bootstrap values used only when a row is absent
(`Constructd:HostAdmin:Updates:ManifestPublicKey` etc.), so the installer can seed them.

### 1.6 Token kinds (D4)

| Kind | Stored as | Issued by | Reach |
|---|---|---|---|
| `legacy` | `vms.vm_token_kind='legacy'` (migration default) | every token existing before migration 100; `POST /vms/{name}/token {kind:"legacy"}` | the four existing routes (`GET/POST/DELETE /vms/{self}/forwards…`, `POST /vms/{self}/activity`) plus two additive routes: `GET /vms/{self}/identity`, `POST /vms/{self}/guest-report`. Nothing else. |
| `primary` | `'primary'` | `create-vm` jobs after migration 100 (**decided here**: new primaries inherit delegation without an admin step, as the requirements demand); `POST /vms/{name}/token` default | legacy reach plus the delegated routes of §2, always re-evaluated against the owner's current policy. |

A child never has a token; `POST /vms/{child}/token` answers `409 child-has-no-token`.
The plaintext travels only in the rotation response and the one-time job channel.

### 1.7 Identity of a child in Hyper-V and on disk

Children are Hyper-V VMs named exactly like their registry record (flat namespace, same
`VmNameValidator` rule, unique per host). Names default to `<parent>-<4 lowercase hex>`
when the request omits `name` (**decided here**: a generated name keeps the CLI one-shot
while the flat namespace keeps the driver, forwards and audit unchanged). The VHDX is
`<VmStorageRoot or Hyper-V default>\<name>.vhdx`. Media files live under `Media:RootDir`
(§6.1), never in the ISO catalog directory.

### 1.8 Audit detail format

No schema change. Every new mutating route and job writes detail as comma-separated
`key=value` tokens with these standard keys, in this order when present:
`op=<operation>`, `owner=<effective owner>`, `parent=<parent vm>`, `target=<target vm or
media id>`, `job=<job id>`, `key=<operation key>`, followed by route-specific tokens. A
primary-token actor is audited as `vm:<name>` (existing convention); `owner=` then names
the user charged. Values are sanitized (control characters stripped, 200 chars). Never a
secret, a URL query string, a typed console string or image bytes.

## 2. Permission matrix

### 2.1 Actors and how they authenticate

| Actor | Credential | Principal facts |
|---|---|---|
| **admin** | Negotiate or `Bearer` user token of a user with `Role.Admin`, `Enabled = true` | existing `Policies.Admin` |
| **user** | Negotiate or `Bearer` user token of a user with `Role.User`, `Enabled = true` | existing `Policies.User` |
| **primary token** | `VmToken` whose VM has `TokenKind.Primary` | new claim `constructd:vm-token-kind = primary`; effective owner = that VM's owner |
| **legacy VM token** | `VmToken` whose VM has `TokenKind.Legacy` | claim `constructd:vm-token-kind = legacy` |
| **shared caller** | a *user* or *primary token* acting on a child with `Sharing = Host` that is not owned by / parented under it | resolved per request, never a stored role |
| **child guest** | none | reaches no route at all; a copied heartbeat or forward credential does not exist for children |
| **anonymous** | none | `GET /health` only |

New policies (constants in `Policies`): `UserOrPrimaryToken` (enrolled user, or VM token of kind primary), `ChildOperator`, `ChildOwnerOrAdmin`, `ParentDelegate`, `ForwardRequester`, `ConsoleOperator`. The last five are resource policies over a `Vm` implemented by new handlers in `Auth/DelegationAuthorization.cs`.

### 2.2 Operations × actors

Legend: ✔ allowed; ✔ᵒ allowed only on own (owned, or parented under the token's VM); ✔ˢ allowed as shared caller on a host-shared child; ✗ refused (`403`); – route not reachable for that credential (`401`/`403` before resource resolution).

| Operation (route) | admin | user | primary token | legacy VM token | shared caller | child guest |
|---|---|---|---|---|---|---|
| `GET /health` | ✔ | ✔ | ✔ | ✔ | ✔ | (anonymous) |
| `GET /whoami` | ✔ | ✔ | – | – | – | – |
| `GET /host/capabilities` | ✔ | ✔ | ✔ | ✗ | – | – |
| `GET /host/status`, `GET/PUT /host/config`, `GET /host/capacity` | ✔ | ✗ | ✗ | ✗ | – | – |
| Users: list/read/update/allowance/tokens (§8.4) | ✔ | ✗ | ✗ | ✗ | – | – |
| `GET /vms` (own scope) | ✔ all | ✔ own | ✔ᵒ own primary + its children | ✗ | – | – |
| `GET /vms/shared` | ✔ | ✔ | ✔ | ✗ | – | – |
| `GET /vms/{name}`, `/state`, `/endpoint`, `/children` | ✔ | ✔ᵒ | ✔ᵒ | ✗ (except `/identity`) | ✔ˢ (read of the child) | – |
| `GET /vms/{name}/identity` | ✔ | ✔ᵒ | ✔ᵒ self | ✔ self only | ✗ | – |
| `POST /vms` (create primary) | ✔ | ✔ (quota) | ✗ | ✗ | – | – |
| `DELETE /vms/{primary}` (cascade) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{name}/power` (primary, existing) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{parent}/children` | ✔ (charged to owner) | ✔ᵒ | ✔ᵒ (own VM as parent) | ✗ | ✗ | – |
| `POST /vms/{child}/lifecycle` start/resume, restart, shutdown, save | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| `DELETE /vms/{child}` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `PUT /vms/{child}/sharing` | ✔ | ✔ᵒ | ✔ᵒ (owner's `allowSharing`) | ✗ | ✗ | – |
| `PUT /vms/{child}/hardware`, `PUT /vms/{child}/media` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `POST /vms/{child}/lease` (renew) | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✗ | – |
| `GET/PUT /vms/{name}/overrides` | ✔ | ✗ | ✗ | ✗ | – | – |
| `POST /vms/{name}/token` (rotate) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| Idle policy routes (existing) | ✔ | ✔ᵒ | ✗ | ✗ | ✗ | – |
| `POST /vms/{self}/activity` (existing) | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Forwards on self (existing routes) | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Forward request for a child (`POST /vms/{child}/forwards`, §12.3) | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ (client target; host target only if owner policy and host policy allow) | – |
| `POST /vms/{name}/forwards/{id}/ack` | ✔ | ✔ᵒ / ✔ˢ for a forward it requested | ✗ | ✗ | ✔ˢ (own requests) | – |
| `GET /vms/{child}/addresses` | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| Media: acquire/upload/list/delete (own) | ✔ all | ✔ | ✔ (owner = VM owner) | ✗ | ✗ | – |
| Console session + screenshot/input | ✔ | ✔ᵒ | ✔ᵒ | ✗ | ✔ˢ | – |
| Jobs read (`GET /jobs`, `/jobs/{id}`, events) | ✔ | ✔ own | ✔ jobs with `requesterVm = self` | ✗ | ✗ | – |
| `POST /jobs/{id}/cancel` | ✔ | ✔ own | ✔ own requests | ✗ | ✗ | – |
| `POST /vms/{self}/guest-report` | ✔ | ✔ᵒ | ✔ self | ✔ self | ✗ | – |
| Updates: status/check/stage/apply/cancel | ✔ | ✗ | ✗ | ✗ | – | – |
| `GET /audit` | ✔ | ✗ | ✗ | ✗ | – | – |

A shared caller performs **operational** actions only: inspect, start/resume, restart,
graceful shutdown, save, console, addresses and permitted forward requests. Deletion,
sharing, hardware, media and lease changes stay owner/admin (**owner** includes the
owner's primary token). Every shared operation is charged to and bounded by the child's
owner (§4.6).

### 2.3 Re-evaluated on every request

| Check | Where | Notes |
|---|---|---|
| User exists and `Enabled` | `UserClaimsTransformation` (existing, extended) and `ITokenService.ValidateAsync` | a disabled user's Bearer tokens and Negotiate identity get no `KnownUser` claim; VM tokens of VMs owned by a disabled user validate to `null`. |
| Role | claims transformation | existing behaviour |
| Token kind | `VmTokenAuthenticationHandler` reads `Vm.TokenKind` at validation | kind is never cached in the token |
| Owner allowance + overrides + host defaults | `IDelegationPolicy.ResolveAsync(owner, parent)` at every delegated mutation | credentials never freeze policy |
| Parent fences (`Deleting`, `ChildCreationClosed`) | `ApiHelpers.FenceDeleting` (existing) and new `FenceParentClosed` | |
| Sharing scope and owner enabled | `ChildOperator` handler | a host-shared child of a disabled owner is treated as private (**decided here**: safest reading of "revoke delegation on user disable") |
| Host forward policy (owner's `AllowHostForwards` **and** `network.hostForwardsEnabled`) | forward request handler | disabling host forwards cannot be bypassed through a parent (§12.3) |
| Maintenance gate | `IMaintenanceGate` in the endpoint filter of gated routes | §7.4 |
| Capacity and budgets | `ICapacityLedger` at create/start/resume/media begin | §4 |

### 2.4 Revocation effects

| Event | Immediate effect | Sessions and artefacts |
|---|---|---|
| User disabled / deleted | Bearer tokens fail; Negotiate identity unknown; VM tokens of owned primaries fail (`ValidateAsync` → null) | console sessions of that user are refused on the next call (every call re-authorizes; sessions expire in 60 s anyway); job SSE streams already open are not cut (no secret in them); forwards stay materialized until the owner's VMs are deleted; leases keep running (expiry acts regardless of owner state). |
| Role demoted Admin → User | admin routes refuse on the next request | open admin SSE streams keep streaming the job they subscribed to. |
| `POST /vms/{name}/token` | previous hash gone in the same write | in-flight requests already authenticated complete; the next call with the old secret is `401`. |
| Primary deleted (cascade accepted) | its token hash cleared (existing), children fenced `Deleting` in the same transaction | child console sessions refused on next call; child forwards removed by the cascade job; shared consumers lose access at fence time. |
| Sharing `host` → `private` | shared callers refused on next request | their console sessions refused on next call; forwards they requested on that child are removed by `INetworkPolicyReconciler.OnSharingChangedAsync` (§12.4). |
| Child deleted | record fenced then removed | references to media removed; reservations released after confirmed removal. |

## 3. Capability model

### 3.1 Capability levels

`enum CapabilityLevel { Unsupported, Conditional, Supported }` (wire: `unsupported`,
`conditional`, `supported`). `Conditional` means "the backend has the mechanism, a given
VM/state may still refuse; the runtime answer is authoritative".

### 3.2 `BackendCapabilities` (Core) and what Hyper-V reports in this delivery

| Area | Field | Hyper-V (this delivery) | Source of truth in the feasibility report |
|---|---|---|---|
| Firmware | `generations: int[]`, `defaultGeneration: int` | `[2]`, `2` (Generation 1 is **unsupported** here: not probed, and PS/2 relative mouse would be its only benefit) | Gen 2 probe only |
| Secure Boot | `secureBoot: CapabilityLevel`, `secureBootTemplates: string[]` | `supported`, `["microsoftWindows","microsoftUefiCertificateAuthority"]` | both templates set and read back |
| TPM | `tpm: CapabilityLevel` | `supported` | local key protector + TPM enabled, VM started |
| Template lock | `secureBootTemplateLockedAfterTpmInit: bool` | `true` | template setter fails after TPM initialization |
| Optical | `maxOpticalDrives: int`, `auxiliaryMedia: CapabilityLevel` | `2`, `supported` | dual DVD attached, boot order set |
| Boot order | `bootOrder: CapabilityLevel` | `supported` | DVD/NIC order set and read; disk not probed (reported as `conditional` note in `notes`) |
| Console | `console.screenshot`, `console.keyboard`, `console.mouseAbsolute`, `console.mouseRelative`, `console.interactive` | `supported`, `supported`, `conditional`, `unsupported` (Gen 2: no `Msvm_Ps2Mouse`), `unsupported` (D2) | screenshots 1×1…native; keyboard methods return 0 with Ctrl+Alt+Del visible; synthetic mouse returns 32768 preboot; no PS/2 instance |
| Console bounds | `console.maxScreenshotBytes: int`, `console.nativeResolutionOnly: bool` | `4 MiB`, `true` (requests above native are refused by the service before WMI) | 32775 above native; success envelope at or below native |
| Networking | `network.clientForward`, `network.hostForward`, `network.directAddressReporting`, `network.isolation` | `supported`, `supported` (gated by policy), `supported` (KVP/`Get-VMNetworkAdapter` addresses), `unsupported` | existing forwards; no enforcing adapter exists |
| Memory | `dynamicMemory: CapabilityLevel`, `memoryOvercommit: CapabilityLevel` | `unsupported`, `unsupported` (fixed RAM policy) | owner's Ubuntu dynamic-memory failures; fixed 512 MiB boot |
| Suspend | `suspend: CapabilityLevel` | `supported` | save/resume measured |
| Graceful shutdown | `gracefulShutdown: CapabilityLevel` | `conditional` (needs guest integration services) | `InitiateShutdown` → 32768 without a guest |
| Checkpoints | `checkpoints: bool` | existing `DriverCapabilities.Checkpoints` | existing |
| Legacy console | `legacyConsole: DriverConsole` | existing `vmconnect`/`none`/URL | existing |
| Notes | `notes: string[]` | free text limitations for the UI | |

`BackendCapabilities` is a **superset**: it embeds the existing `DriverCapabilities` so the
existing `/power save` gate and the extension's `capabilities` keep reading the same values.

### 3.3 How a backend reports it

| Layer | Contract |
|---|---|
| PowerShell driver contract | `Get-ConstructDriverCapabilities` (existing) is **unchanged**. New optional function `Get-ConstructDriverExtendedCapabilities` in the new child-VM driver file `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` returns the hashtable mirror of §3.2 (`Generations`, `SecureBootTemplates`, `Console = @{ Screenshot; Keyboard; MouseAbsolute; MouseRelative; Interactive }`, …). The loader gains an optional `-Include ChildVm` switch that dot-sources the extra file; without the switch the loader is byte-for-byte today's, so the local install never loads it. |
| Service | `IChildVmDriver.GetCapabilitiesAsync()` (Windows implementation runs the function above through `IProcessRunner`, cached on success like today's capability probe, not cached on failure). Console capabilities come from `IConsoleTransport.Capabilities` (WMI scripts, §13.1) and are merged by `ICapabilityAggregator.GetAsync()`. |
| Per-VM runtime | `GET /vms/{name}/capabilities` re-checks what the VM can do right now: console device presence (video head, keyboard, synthetic mouse, PS/2 mouse), template lock state (`secureBootTemplateLocked: true` once TPM was initialized), state-dependent items (screenshot `unavailable` while `off`). |
| Fake | `FakeChildVmDriver` and `FakeConsoleTransport` report §3.2 values by default; tests flip individual levels. |

### 3.4 How the API exposes it

| Route | Body |
|---|---|
| `GET /host/capabilities` | `{ backend: "hyperv-local", capabilities: BackendCapabilities, policy: { hostForwardsEnabled, directAccessReporting, allowNeverLifetime, maxChildLifetimeSeconds } }` — `policy` is the host-level view; the caller's effective values are in `/whoami` or `/vms/{name}/identity`. |
| `GET /vms/{name}/capabilities` | `{ vm: name, state, console: { screenshot, keyboard, mouseAbsolute, mouseRelative, interactive, nativeWidth, nativeHeight }, hardware: { secureBootTemplateLocked, generation }, gracefulShutdown: CapabilityLevel }` |
| `GET /health` | `apiFeatures: ["host-admin","children","media","console","updates","network"]` so an old service is detected by absence (§10.3). |

Requests that ask for an `unsupported` capability are refused **before** any VM is
allocated with `409 unsupported-capability { capability, level, notes }`; a `conditional`
capability that fails at runtime answers with the runtime error (`409 console-unavailable
{ device, returnValue }`), never a silent no-op.

## 4. Capacity math

### 4.1 Quantities

| Symbol | Definition | Source |
|---|---|---|
| `T_ram` | host physical RAM | `IHypervisorInventory.GetHostAsync().TotalRamBytes` |
| `H_ram` | `capacity.ramHeadroomBytes` | host config |
| `R_managed` | Σ `RamBytes` of managed VMs whose runtime reservation is `pending` or `held` | ledger |
| `R_unmanaged` | Σ `max(MemoryStartupBytes, MemoryAssignedBytes)` of unmanaged VMs observed `Running`/`Paused`/`Starting`/`Saving` | inventory (never charges a fixed-RAM VM's inactive dynamic maximum) |
| `A_ram` | admissible RAM = `T_ram − H_ram − R_managed − R_unmanaged` | computed under the ledger gate |
| `C_host` | logical CPUs | inventory |
| `A_cpu` | `capacity.cpuBudget − Σ active vCPUs (managed held/pending + unmanaged running)`; unlimited when `cpuBudget` is null | |
| `V_free(v)` | physical free bytes on volume `v` | inventory `Volumes[]` |
| `H_sto` | `capacity.storageHeadroomBytes` | host config |
| `G(a)` | growth reserve of artifact `a` = `max(0, MaxBytes(a) − FileBytes(a))` | inventory for disks and saved-state files, ledger for media |
| `A_sto(v)` | admissible storage on `v` = `V_free(v) − H_sto − Σ_{a on v} G(a)` | |

**No double counting (decided here):** bytes already allocated on disk are inside
`V_free` (they are not free), so an artifact contributes only its *growth* `G(a)`. A new
artifact has `FileBytes = 0`, so its whole maximum is growth. The same file is never
counted through two artifacts: disks are keyed by resolved path with the parent chain
deduplicated, media by `MediaItem.Path`, saved state by VM id.

### 4.2 What is reserved, per resource and state

| Resource | Reserved when | Amount | Released when |
|---|---|---|---|
| RAM | reservation `pending` (before `Start`/`Resume`/`Create-with-start` is issued), `held` (Running, Paused, Starting, Saving, Stopping/graceful pending) | `RamBytes` (fixed) | observed terminal `Off` or `Saved` (or `Absent`) confirmed by the driver/inventory; never on request acceptance |
| CPU | same as RAM | `Cpus` | same as RAM |
| Disk | at child create (before VHD creation) | full `DiskGb` maximum on the target volume | after confirmed VHD deletion; retained through Off/Saved |
| Saved state | while a VM is Running or Saved and `suspend` is supported | `RamBytes + 64 MiB` on the VM's configuration volume (**decided here**: the VMRS holds RAM contents; the tiny probe file must not be generalized) | Off/Absent confirmed |
| Media (acquire/upload) | at job/upload begin | declared `sizeBytes`, or `Content-Length`, or `media.maxBytes` when unknown | trimmed to actual size at completion; released on failure after the partial file is confirmed deleted |
| Auxiliary/other exclusively owned files | included in the VM's disk reservation as separate artifacts | actual max | with the artifact |

Unknown state (`VmState.Unknown`) keeps whatever is reserved: conservative by rule.

### 4.3 Admission (atomic, serialized)

`ICapacityLedger.TryReserveAsync(ReservationRequest)` is the only entry point.

1. Take the ledger gate (one in-process `SemaphoreSlim(1,1)`; the service is a single process).
2. Open one SQLite `IMMEDIATE` transaction on `reservations`.
3. Refresh `R_unmanaged`, `V_free` from the last inventory snapshot; if the snapshot is older than `capacity.reconcileSeconds`, refresh it first (still under the gate).
4. Compute user aggregates (§4.6) and host admissibles.
5. Refuse with `CapacityDecision { Allowed=false, Resource, Scope, Requested, Allowed, Available, Reason }` on the first failing check, in the order: user quota counts → user budgets → host CPU → host RAM → volume storage.
6. Otherwise insert reservation rows with `phase='pending'`, commit, release the gate, return the ids.

The hypervisor call happens **after** step 6. `ConfirmAsync(ids, observedState)` flips
`pending → held`; `ReleaseAsync(ids, observedState)` deletes them. Both require the observed
state that justifies the transition, and both are audited (`capacity.confirm`,
`capacity.release`). A failed start releases only after the driver reports `Off`
(or after the pending timeout, §4.4). A graceful-shutdown request releases nothing
until `Off` is observed.

### 4.4 Reconciliation

Runs at startup (after `Bootstrap` forwards reconcile), every `capacity.reconcileSeconds`,
and immediately after every power operation and job completion, always under the gate.

| Observation | Action |
|---|---|
| Managed VM `Off`/`Saved`/`Absent` with a `held`/`pending` RAM/CPU reservation | release; audit `capacity.release reason=observed-<state>` |
| Managed VM `Running`/`Paused` with no RAM reservation (started outside the API, or a crash between reserve and confirm) | insert `held` reservation with `origin='external'`, charged to the VM's owner; audit `capacity.external-hold`. The lease is **not** activated by an external start (§5.4). |
| `pending` reservation older than `pendingReservationTimeoutSeconds` whose VM is not `Running`/`Paused`/`Starting` | release; audit `capacity.release reason=pending-timeout` |
| Unmanaged VM present | included in `R_unmanaged`/CPU/storage sums; never given an owner; listed in `GET /host/capacity.unmanaged[]` |
| Disk file of a managed VM missing/unreadable | keep the reservation, mark the VM `observed.storageProblem = "unreadable"`; never drop consumption |
| Media file missing for a `ready` item | mark `failed`, keep storage charged until `media-cleanup` confirms |
| Service restart | the ledger table is the source; nothing is reset. Running VMs are re-confirmed, saved ones keep storage. |

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
| `job_id` | `TEXT NULL` | the job that placed it |
| `created`, `confirmed_at` | `TEXT` | |

### 4.6 User aggregate budgets and defaults

Aggregates cover **all** primaries and children owned by the user on this host. A shared
caller's start of somebody else's child is charged to that child's owner.

| Policy | Default | Counted |
|---|---|---|
| Primaries | `User.MaxVms` (existing rows), `userDefaults.maxPrimaries = 1` for users created through §8.4 | rows with `kind='primary'` |
| Child creation | enabled | switch |
| Retained children | 1 per user per host, across all their primaries | rows with `kind='child'` in **any** state, including `Deleting` until removed |
| CPU | no user budget (null); host `cpuBudget` null | active vCPUs of held/pending reservations |
| RAM | null (no guessed default); host capacity still binds | held/pending RAM |
| Storage | null; host capacity still binds | full disk maxima + media + saved-state reservations |
| Max child lifetime | unlimited; `never` allowed | per request |

Refusals carry `409 capacity-exhausted` with
`{ resource: "ram"|"cpu"|"storage"|"children"|"primaries", scope: "user"|"host"|"volume",
requested, allowed, available, reason }`. Nothing is clamped.

## 5. Lease semantics

### 5.1 Lifetime input

`lifetime` is a required string on child creation and on every `start`/`resume`:
`never`, or `<n><unit>` with unit `m`, `h`, `d` (`^[1-9][0-9]*[mhd]$`, minimum `5m`,
maximum 3650 d). Stored as typed (`RequestedText`) and in seconds (`RequestedSeconds`, null
for `never`). Missing → `400 lifetime-required`; exceeding
`effective.maxChildLifetimeSeconds` or `never` with `allowNeverLifetime=false` →
`403 lifetime-not-allowed { requested, allowedMax, allowNever }`. Never defaulted.

### 5.2 Fields and states

| `LeaseState` | Meaning | `ActivatedAt` / `ExpiresAt` |
|---|---|---|
| `inactive` | created powered-off, never started | null / null |
| `active` | running lease | set / set |
| `unlimited` | `never` and started | set / null |
| `expired` | expiry action completed (VM observed Off) | kept for display |
| `overdue` | expiry action failed or unsupported; retried | kept; `LastExpiryAttemptAt`, `LastExpiryOutcome` set |

Primaries have `Lease = null` and are never touched by the lease scheduler.

### 5.3 Activation and renewal rules

| Event | Effect on the lease |
|---|---|
| Create with `start=true` | request stored; `pending` reservation; on confirmed `Running`: `ActivatedAt = now`, `ExpiresAt = now + requested` (or `unlimited`). |
| Create with `start=false` | `inactive`; activates on the first successful `start`, which **must carry its own `lifetime`** (the creation value is stored as the default the CLI offers, not applied silently). |
| `start`/`resume` from Off or Saved | new `lifetime` required; replaces request and expiry from the observed `Running` moment. |
| `restart` (API) | graceful shutdown + start, RAM held throughout; `ExpiresAt` **unchanged**. |
| Guest-initiated reboot | invisible to the service; unchanged. |
| Shared caller start/resume | must supply `lifetime` too; bounded by the **owner's** policy; charged to the owner. Sharing changes never touch the lease. |
| Service restart | `ExpiresAt` is persisted; the scheduler resumes; an `active` lease with `ExpiresAt < now` at startup is due immediately. |
| Host downtime past expiry | same as above: Hyper-V may auto-start the VM (`AutomaticStartAction`); the first tick runs the expiry action. If the VM is observed Off, the lease becomes `expired` with no action. |
| External start (outside the API) of an `expired`/`inactive` child | reconciliation creates the RAM hold (§4.4) and marks the lease `overdue` immediately with `LastExpiryOutcome = "external-start"` → expiry action runs on the next tick (**decided here**: a child running without a lease is the case the lease exists to prevent). |
| `POST /vms/{child}/lease { lifetime }` (owner/admin) | explicit renewal from `now`; audited `vm.lease.renew`. (**decided here**: an explicit owner renewal exists because "stop and start again" as the only extension would break long tests; there is **no** automatic keepalive, because the lease is wall-clock by design.) |

### 5.4 Scheduler

`LeaseSchedulerService` (hosted service, `lifecycle.leaseTickSeconds`, default 30 s;
`Constructd:Lease:SchedulerEnabled=false` in tests). Each tick lists children with
`lease_state IN ('active','overdue')` and `lease_expires_at <= now` (overdue: also
`lease_last_attempt_at + leaseRetrySeconds <= now`), and for each submits a `vm-shutdown`
job with `reason=lease-expiry` unless a lifecycle job for that VM is already running
(`CurrentJobId`). Ticks are serialized (one gate), and each VM's job takes the VM's own
lifecycle gate, so the scheduler never races an API shutdown.

### 5.5 Expiry action (D1, D6)

`vm-shutdown` job, `reason=lease-expiry`:

1. Observed `Off`/`Saved`/`Absent` → lease `expired`, audit `vm.lease.expired outcome=already-off`, done.
2. `IChildVmDriver.ShutdownGracefulAsync(name, timeout = lifecycle.gracefulShutdownTimeoutSeconds)`:
   - guest integration services absent or without contact → `GracefulShutdownOutcome.Unavailable`; the job fails with error `guest-shutdown-unavailable`; lease `overdue`, `leaseOverdue=true` in inventory; RAM stays held; retried every `leaseRetrySeconds`.
   - request accepted, VM not Off after the timeout → `Timeout`; job error `shutdown-timeout`; lease `overdue`; RAM stays held; retried.
   - VM observed Off → `Completed`; lease `expired`; RAM/CPU released after the observation; disks and saved-state storage stay charged.
3. Never: force-off, save, delete, checkpoint. The user-panel **Shut down** button and `construct vm shutdown` submit the same job with `reason=user`.

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

`MediaItem` record / `media` table (migration 200):

| Field / column | Type | Meaning |
|---|---|---|
| `Id` / `id` | `TEXT PRIMARY KEY` | 32-hex GUID; also the file name `<id>.iso` |
| `Owner` / `owner` | `TEXT NOT NULL COLLATE NOCASE` | user charged; the effective owner for primary-token uploads |
| `Name` / `name` | `TEXT NOT NULL` | display name, sanitized (control chars stripped, ≤120 chars, no path separators) |
| `Role` / `role` | `TEXT NOT NULL` | `install` or `auxiliary` |
| `Source` / `source` | `TEXT NOT NULL` | `url` or `upload` |
| `SourceUrl` / `source_url` | `TEXT NULL` | scheme + host + path **only** (query and fragment dropped before storage; they can carry signed tokens) |
| `Path` / `path` | `TEXT NOT NULL` | `<Media:RootDir>\<id>.iso` |
| `State` / `state` | `TEXT NOT NULL` | `pending`, `transferring`, `ready`, `failed`, `deleting` |
| `SizeBytes` / `size_bytes` | `INTEGER NULL` | actual bytes once known |
| `ReservedBytes` / `reserved_bytes` | `INTEGER NOT NULL` | storage reserved (§4.2) |
| `Sha256` / `sha256` | `TEXT NULL` | computed while streaming |
| `ExpectedSha256` / `expected_sha256` | `TEXT NULL` | caller-supplied, lowercase hex |
| `Error` / `error` | `TEXT NULL` | safe description (`SafeError`) |
| `JobId` / `job_id` | `TEXT NULL` | the acquiring job |
| `Created`, `ReadyAt`, `LastReferencedAt` | `TEXT` | |

`media_references (media_id TEXT NOT NULL, vm_name TEXT NOT NULL COLLATE NOCASE, slot TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY (media_id, vm_name, slot))`.

`Media:RootDir` option default `C:\ProgramData\Construct\service\media` (a sibling of
`Iso:CacheDir`, hardened by the installer like the data dir). Fake mode: in-memory store
and a temp-dir file store.

### 6.2 Visibility and access control

| Actor | List / read metadata | Attach to a child | Delete |
|---|---|---|---|
| owner (user or primary token of an owned primary) | own items | own items | own items, no references |
| admin | all | any (charged to the child's owner) | any, no references |
| shared caller | metadata of media attached to a shared child: `{ id, name, role, sizeBytes }` only | ✗ | ✗ |
| anyone | ✗ content | | |

There is **no** content download route in this delivery (**decided here**: auxiliary ISOs
carry answer files with secrets; the only consumer is the hypervisor). Checksums of
`auxiliary` items are shown to owner/admin only.

### 6.3 URL acquisition rules (`media-acquire` job)

| Rule | Detail |
|---|---|
| Scheme | `https` always; `http` only when `media.allowHttp` and `expectedSha256` is supplied (**decided here**: distro mirrors are often plain http; a checksum makes the transport irrelevant). Anything else → `400 url-refused { reason: "scheme" }`. |
| Host | DNS name or IP literal. Resolve **all** addresses (A and AAAA) before connecting; refuse if **any** is loopback, link-local (`169.254/16`, `fe80::/10`), private (RFC 1918), ULA (`fc00::/7`), CGNAT (`100.64/10`), multicast, unspecified, broadcast, the metadata address `169.254.169.254`, or an IPv4-mapped/compatible IPv6 form of those → `400 url-refused { reason: "address", address }`. |
| Connection pinning | connect to one of the validated addresses via `SocketsHttpHandler.ConnectCallback`; no second resolution, so DNS rebinding cannot redirect the fetch. |
| Port | any; the default per scheme when omitted. |
| Credentials | userinfo in the URL → refused; no request headers, cookies or proxies from the caller; the service's own proxy settings are not applied (`UseProxy=false`). |
| Redirects | manual, max 5; every hop re-validated with the same scheme/host/address rules; `https → http` downgrade refused; the final URL (minus query) is recorded as `sourceUrl`. |
| Size | `Content-Length > media.maxBytes` → `413 media-too-large` before download; unknown `Content-Length` → allowed, `media.maxBytes` reserved, abort with `media-too-large` if exceeded while streaming; reservation trimmed to actual size at completion. |
| Time | `media.acquireTimeoutMinutes` overall; 120 s read idle timeout. |
| Integrity | SHA-256 computed while streaming; mismatch with `expectedSha256` → `failed`, file deleted, storage released, error `checksum-mismatch`. |
| Format | the first 32 KiB + sector 16 are checked for the ISO 9660 `CD001` primary volume descriptor after the download (**decided here**: UDF-only images are accepted only when `expectedSha256` is present, because the check cannot validate them); otherwise `failed`, error `not-an-iso`. |
| Progress | job progress lines every 64 MiB or 5 s: `downloaded 1.2 GiB of 3.0 GiB (40 %)`; the URL host is logged, the path is not. |
| Cancellation | `POST /jobs/{id}/cancel` → partial deleted, storage released, state `failed` with error `cancelled`. |
| Idempotency | `operationKey` (§7.3); the same owner + key returns the existing job. |

### 6.4 Upload protocol (resumable, idempotent)

| Step | Route | Semantics |
|---|---|---|
| begin | `POST /media/uploads { name, role, sizeBytes, expectedSha256?, operationKey? }` | validates, reserves `sizeBytes` on `Media:RootDir`'s volume (`409 capacity-exhausted` otherwise), creates `media_uploads` row and a sparse `<id>.part` file. `201 { uploadId, mediaId, chunkSizeBytes, chunkCount, expiresAt, received: [] }`. Same `operationKey` → the existing upload is returned with `200`. |
| chunk | `PUT /media/uploads/{id}/chunks/{index}` body `application/octet-stream`, exact `chunkSizeBytes` (last chunk: the remainder), `Content-Length` required | written at `index × chunkSizeBytes`; re-sending an index overwrites; `204`; `409 upload-expired`, `400 chunk-size`. |
| status | `GET /media/uploads/{id}` | `{ state: "open"\|"completing"\|"done"\|"expired", received: [indexes], missing: [indexes], expiresAt }` — resumption reads this and re-sends `missing`. |
| complete | `POST /media/uploads/{id}/complete` | refuses `409 upload-incomplete { missing }`; hashes the file (job-free, bounded by size; for > 2 GiB it is a `media-verify` job and the call answers `202`); checksum mismatch → `422 checksum-mismatch` and the item stays `failed` for `media-cleanup`; success → media `ready`, `201 MediaItem`. Idempotent: complete on a done upload returns `200` with the same item. |
| abort | `DELETE /media/uploads/{id}` | deletes the part, releases the reservation, `204`. |
| expiry | `media-cleanup` job (daily and admin-triggered) | uploads past `media.uploadTtlHours` without completion are aborted and audited. |

`media_uploads (id TEXT PRIMARY KEY, media_id TEXT NOT NULL, owner TEXT NOT NULL COLLATE
NOCASE, size_bytes INTEGER NOT NULL, chunk_bytes INTEGER NOT NULL, received_json TEXT NOT
NULL, state TEXT NOT NULL, operation_key TEXT NULL, created TEXT NOT NULL, expires_at TEXT
NOT NULL)`.

### 6.5 References and cleanup

| Rule | Detail |
|---|---|
| Attach | `child-create` inserts `media_references` rows for the install and auxiliary slots before touching the hypervisor; `PUT /vms/{child}/media` replaces them (VM must be Off). |
| Detach | `child-delete` / cascade removes the rows after the VM is removed; `PUT /media` detach removes a row. |
| Delete | `DELETE /media/{id}` → `409 media-in-use { references: [{ vmName, slot }] }` while any row exists; otherwise state `deleting`, file removed (a "held open" failure leaves `deleting` with `error`, retried by cleanup), storage released after the file is confirmed gone, row deleted. |
| Orphans | `media-cleanup` removes `.part`/`.iso` files under `Media:RootDir` that match no row and are older than 1 h, `failed` items older than `uploadTtlHours`, and retries `deleting` items. It never touches `Iso:CacheDir`, `Iso:SourcePath`, or anything outside `Media:RootDir`. |
| Accounting | a failed acquisition/upload keeps its storage charged until the partial file is confirmed absent; a `failed` item is visible in `GET /media` with `error` so nothing leaks silently. |

### 6.6 Log hygiene

Never logged, audited, streamed or stored: URL query strings/fragments, upload bytes,
auxiliary content, checksums of auxiliary media in progress lines, media names before
sanitization. Progress lines name the media id and the URL host only.

## 7. Jobs

### 7.1 Kinds

| Kind | Started by | Phases (`job.phase`) | Result payload | Gated by maintenance |
|---|---|---|---|---|
| `create-vm` (existing) | `POST /vms` | unchanged (text progress only) | `VmCreateResult` | ✔ |
| `remove-vm` (existing) | `DELETE /vms/{primary}` without children | unchanged | `VmRemoveResult` | ✔ |
| `parent-cascade-delete` | `DELETE /vms/{primary}` with children | `fence`, `children`, `primary`, `forwards`, `media-references`, `network`, `done` | `{ name, children: [{ name, outcome, error? }], releasedForwards }` | ✔ |
| `child-create` | `POST /vms/{parent}/children` | `admit`, `media`, `hardware`, `disk`, `attach`, `start`, `done` | `{ name, parent, state, lease, endpointHint: { addresses: [] } }` | ✔ |
| `child-delete` | `DELETE /vms/{child}` | `fence`, `vm`, `forwards`, `media-references`, `storage`, `done` | `{ name, outcome, retained: [{ artifact, reason }] }` | ✔ |
| `vm-shutdown` | `lifecycle shutdown`, lease expiry, user-panel Shut down | `request`, `wait`, `done` | `{ name, outcome: "completed"\|"timeout"\|"unavailable", finalState }` | ✗ |
| `vm-restart` | `lifecycle restart` | `shutdown`, `wait`, `start`, `done` | `{ name, outcome, finalState }` | ✗ |
| `media-acquire` | `POST /media/acquire` | `validate`, `download`, `verify`, `done` | `MediaItem` | ✔ |
| `media-verify` | large upload completion | `hash`, `done` | `MediaItem` | ✔ |
| `media-cleanup` | schedule / `POST /media/cleanup` (admin) | `scan`, `delete`, `done` | `{ removed: [], retained: [{ id, reason }] }` | ✔ |
| `host-update` | `POST /host/updates/stage`, `/apply` | §11.5 | `HostUpdateRecord` | is the gate |

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
| Carrier | header `X-Construct-Operation-Key: <key>` on any job-starting request, or body field `operationKey` (header wins). 8–128 chars of `[A-Za-z0-9._:-]`. |
| Scope | `(owner, kind, key)` unique in `job_operation_keys`; `target` records the VM/media the key was used for. |
| Replay | same owner + kind + key + target → `200 { jobId, replayed: true }` with the original job (any state). Different target → `409 operation-key-conflict { jobId, target }`. |
| Retention | rows older than 24 h are deleted by `media-cleanup`'s tick (one sweep shared by all kinds). |
| Without a key | behaviour is today's: each request starts a job; the CLI always sends one (§9.5). |

### 7.4 Maintenance / drain gate

`IMaintenanceGate` (Core) with states `open → draining → maintenance → open`.

| State | Gated job kinds (`✔` in §7.1) | Everything else |
|---|---|---|
| `open` | run | run |
| `draining` | new submissions refused `503 maintenance { phase: "draining", retryAfterSeconds, updateJobId }`; running ones finish; the update job waits up to `updates.drainTimeoutMinutes` | run normally: power/lifecycle, forwards, acks, heartbeats, guest reports, console, reads, media chunk uploads already begun (they finish before `maintenance`) |
| `maintenance` | refused | every **mutation** refused `503 maintenance { phase: "maintenance" }` with `Retry-After`; reads and `GET /health` answer; the window lasts until the process stops (≤ 30 s by construction, §11.5) |

**Not gated, ever:** PC-to-primary provisioning (invisible to the service), guest SSH
activity, idle heartbeats, `construct expose`, forwards reconciliation, lease expiry
shutdowns (they are `vm-shutdown` jobs, ungated; a shutdown crossing the restart is simply
re-observed by reconciliation), and Hyper-V VMs themselves.

### 7.5 Job ownership for delegated requests

`Job.Owner` = effective owner user; `Job.RequesterVm` = the primary whose token submitted
it (null otherwise). `CanRead` = admin, or owner, or a VM token whose VM equals
`RequesterVm`. `GET /jobs` lists by the same rule with filters `kind`, `state`, `vm`,
`since`, `limit` (≤ 200).

## 8. API contract

Conventions (existing, restated): prefix `/api/v1`; JSON camelCase; enums as camelCase
strings; every error is an RFC 7807 problem document. New in this delivery: every problem
produced by a **new** route, and every new refusal added to an existing route, carries an
extension member `code` (kebab-case, table in §8.17) and, where stated, structured
extension members. Existing problem documents keep their current bodies (adding `code` to
them is allowed, changing `title`/`detail` is not). Timestamps are ISO 8601 UTC. `202`
answers are always `{ jobId }` (existing `JobAcceptedResponse`), optionally with
`replayed: true` when an operation key matched.

Request DTOs follow `Contracts/Requests.cs` style: every field nullable, validated
explicitly. New DTOs live in per-feature files (`Contracts/DelegationContracts.cs`,
`MediaContracts.cs`, `ConsoleContracts.cs`, `HostAdminContracts.cs`, `UpdateContracts.cs`,
`NetworkContracts.cs`); `Requests.cs`/`Responses.cs` are not edited.

### 8.1 Discovery and identity

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /health` | anonymous | – | `200 HealthResponse` | – |
| `GET /whoami` | `AnyUserIdentity` (existing) | – | `200 WhoAmIResponse` + additive fields `enabled: bool?`, `effective: EffectiveAllowanceResponse?`, `apiFeatures: string[]` | existing |
| `GET /vms/{name}/identity` | `VmSelfOrOwnerOrAdmin` (legacy tokens allowed, self only) | – | `200 VmIdentityResponse` | `404`, `403` |
| `GET /host/capabilities` | `UserOrPrimaryToken` | – | `200 HostCapabilitiesResponse` (§3.4) | – |
| `GET /vms/{name}/capabilities` | `ChildOperator` (owner/admin/parent token/shared) | – | `200 VmCapabilitiesResponse` (§3.4) | `404`, `403` |

```
HealthResponse            { status: "ok"|"maintenance", commit: string, packageVersion: string,
                            schemaVersion: int, schemaMinReadableBy: int,
                            apiFeatures: string[], maintenance: { phase, retryAfterSeconds }? }
EffectiveAllowanceResponse{ maxPrimaries, allowChildCreation, maxRetainedChildren, cpuBudget?,
                            ramBudgetBytes?, storageBudgetBytes?, maxChildLifetimeSeconds?,
                            allowNeverLifetime, allowSharing, allowHostForwards,
                            usage: { primaries, children, cpus, ramBytes, storageBytes } }
VmIdentityResponse        { vmName, kind: "primary"|"child", tokenKind: "legacy"|"primary"|null,
                            owner, parent?, delegation: EffectiveAllowanceResponse?  (null for legacy),
                            serviceCommit: string, apiFeatures: string[] }
```

`tokenKind` is `null` when the caller is a user (there is no token in the request) and
the caller's kind when it is a VM token. This is how `construct vm` learns whether it is
delegated (§9.2).

### 8.2 Host status and configuration (admin)

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `GET /host/status` | `Admin` | – | `200 HostStatusResponse` | – |
| `GET /host/capacity` | `Admin` | – | `200 HostCapacityResponse` | – |
| `GET /host/config` | `Admin` | – | `200 HostConfigResponse` (all sections of §1.5 with `source: "default"\|"stored"` per section) | – |
| `PUT /host/config` | `Admin`, audited `host.config` | `HostConfigRequest` (any subset of sections; a section is replaced whole) | `200 HostConfigResponse` | `400 validation { field, reason }` (headroom ≥ 0, chunk size 1–64 MiB, timeouts ≥ 30 s, repository `owner/name`, public key base64 32 bytes) |

```
HostStatusResponse   { version: { commit, packageVersion, installedAt, source: "release"|"installer"|"unknown" },
                       health: { hypervisor: "ok"|"unreachable", database: "ok", media: "ok"|"missing-root" },
                       capacity: HostCapacitySummary, maintenance: { phase, since?, updateJobId? },
                       activeJobs: [{ id, kind, vmName?, owner, phase?, created }],
                       leaseOverdueCount: int, unmanagedVmCount: int }
HostCapacitySummary  { ram: { totalBytes, headroomBytes, reservedBytes, unmanagedBytes, availableBytes },
                       cpu: { logical, budget?, activeVcpus, availableVcpus? },
                       volumes: [{ root, totalBytes, freeBytes, headroomBytes, growthReservedBytes, availableBytes }] }
HostCapacityResponse { summary: HostCapacitySummary,
                       reservations: [{ id, resource, scopeOwner?, vmName?, artifact?, volume?, amount, phase, origin, created }],
                       unmanaged: [{ name, id, state, cpus, memoryBytes, disks: [{ path, maxBytes, fileBytes }] }],
                       perUser: [{ user, primaries, children, cpus, ramBytes, storageBytes }] }
```

### 8.3 Inventory (existing routes, additive fields)

| Method, path | Auth | Change |
|---|---|---|
| `GET /vms` | `User` (existing) | additive query `?kind=primary\|child\|all` (default `all`), `?owner=` (admin), `?parent=`. A primary token may call it (policy widened to `UserOrPrimaryToken`) and sees its own primary plus its children. |
| `GET /vms/{name}` | `ChildOperator` for children, existing `VmOwnerOrAdmin` for primaries | `VmResponse` gains the fields below |
| `GET /vms/{name}/children` | owner/admin/`ParentDelegate` | `200 VmResponse[]` |
| `GET /vms/shared` | `UserOrPrimaryToken` | `200 VmResponse[]` of host-shared children not owned by the caller, with `shared: true` |
| `GET /vms/{name}/state`, `/endpoint` | as today; `/endpoint` on a child → `409 no-endpoint` (children have no SSH forward) | |

`VmResponse` additive fields (all present, null/empty when not applicable; **existing
fields and their order are unchanged**):

```
kind, parent?, sharing, shared: bool (true when returned to a non-owner),
tokenKind? (owner/admin only; null to others), childCreationClosed,
lease?: { requested, activatedAt?, expiresAt?, state, overdue: bool, lastAttemptAt?, lastOutcome? },
hardware?: { cpus, ramMb, diskGb, generation, secureBoot, secureBootTemplate?, tpm, bootOrder: string[], networkAttached },
media: [{ id, role, name, sizeBytes?, state }],
guest: { constructCommit?, provisionedAt?, reinstalledAt?, reportedAt?, provenance, lastAttemptAt?, lastAttemptOutcome? },
observed: { createdAt?, lastBootAt?, addresses: [{ address, family, source, observedAt }], storageProblem? },
reservations: { ramBytes, cpus, storageBytes },
currentOperation?: { jobId, kind, phase? },
children?: string[] (primaries only: names),
allowedActions: string[]   // computed for THIS caller from §2.2, e.g. ["start","shutdown","save","restart","console","forward"]
                           // for a shared caller; presentation only — every route re-checks
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
| `POST /users` (existing) | `Admin` | unchanged; additive optional `allowance: UserAllowanceRequest`; when `maxVms` is omitted **and** `allowance` is present, `maxVms = userDefaults.maxPrimaries` (**decided here**: the old CLI/route default 0 stays for the old shape) | `201 UserResponse` (unchanged) | existing |
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

### 8.5 Per-VM overrides (admin)

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
  name?: string,                         // generated when absent (§1.7)
  cpus: int, ramMb: int, diskGb: int,    // all required; ramMb multiple of 2, ≥ 512; diskGb ≥ 1
  lifetime: string,                      // required (§5.1)
  media: { installMediaId: string, auxiliaryMediaId?: string },   // ready items owned by the effective owner (or admin-selected)
  preset?: "windows"|"linux",            // firmware hints only (§8.6.1); never fills cpus/ram/disk/lifetime
  firmware?: { generation?: int, secureBoot?: bool, secureBootTemplate?: string, tpm?: bool,
               bootOrder?: ("installMedia"|"auxiliaryMedia"|"disk"|"network")[] },
  network?: { attach?: bool },           // default true
  start?: bool,                          // default true
  idlePolicy?: never accepted            // children have no idle policy (400 if present)
  operationKey?: string
}
```

Validation order and errors: `400 validation` (shape/ranges) → `403 token-kind-legacy` →
`403 delegation-disabled` (owner/override `allowChildCreation=false`) → `409 vm-deleting` /
`409 parent-closed` → `403 lifetime-not-allowed` → `409 media-not-ready { mediaId, state }`
/ `403` (media not owned) → `409 unsupported-capability` (generation, template, TPM,
auxiliary, boot device) → `409 name-taken` → `409 capacity-exhausted` (retained children,
budgets, host RAM/CPU/storage; RAM/CPU only when `start=true`) → `202 { jobId }`.

The `child-create` job: `admit` (already done; re-checks parent fence) → `media` (verify
files exist and are `ready`) → `hardware` (`IChildVmDriver.CreateAsync(ChildVmDescriptor)`:
Gen 2, fixed RAM, **template before TPM**, automatic checkpoints off,
`AutomaticStopAction = Save` — **decided here**: today's descriptor default, so a host
shutdown never cold-kills a child; the saved state is reserved per §4.2) → `disk` (`New-VHD -Dynamic` of `diskGb`) → `attach` (DVDs in slot
order, boot order) → `start` (if requested: confirm `Running`, activate lease) → `done`.
Failure after `hardware` removes the VM and its disk, removes references, releases
reservations after confirmed removal, keeps the media. The job never waits for SSH, never
injects credentials, never touches the ISO catalog.

#### 8.6.1 Presets

| preset | generation | secureBoot | template | tpm | bootOrder |
|---|---|---|---|---|---|
| `windows` | 2 | true | `microsoftWindows` | true | `installMedia, auxiliaryMedia, disk, network` |
| `linux` | 2 | true | `microsoftUefiCertificateAuthority` | false | `installMedia, auxiliaryMedia, disk, network` |
| (none) | 2 | false | null | false | `installMedia, auxiliaryMedia, disk, network` |

Explicit `firmware.*` values override the preset. `auxiliaryMedia` in the boot order
without an auxiliary item → `400 validation`.

### 8.7 Lifecycle

`POST /vms/{name}/lifecycle` — auth `ChildOperator` for children; `VmOwnerOrAdmin` for
primaries (additive: primaries may use `shutdown`/`restart` here; their `/power` route is
unchanged); audited `vm.lifecycle` with `action=`.

```
LifecycleRequest { action: "start"|"shutdown"|"save"|"restart", lifetime?: string, operationKey?: string }
```

| action | Child | Primary | Answer |
|---|---|---|---|
| `start` | requires `lifetime`; admission (RAM/CPU pending → confirm); resumes `saved` | `lifetime` must be absent (`400 validation`); admission applies to primaries too (**decided here**: a primary start that would exceed host RAM must fail clearly rather than let Hyper-V fail it) | `200 { state, lease? }` |
| `shutdown` | graceful, job `vm-shutdown` `reason=user` | same job | `202 { jobId }` |
| `save` | requires `suspend` capability; RAM released after `Saved` observed | existing behaviour | `200 { state }` |
| `restart` | job `vm-restart`; lease unchanged; RAM held | same | `202 { jobId }` |

`POST /vms/{child}/power` → `400 child-lifecycle-route`. Errors: `409 vm-deleting`, `409
capacity-exhausted`, `400 lifetime-required`, `403 lifetime-not-allowed`, `409
unsupported-capability` (save), `409 operation-in-progress { jobId }` when a lifecycle job
already runs for that VM.

### 8.8 Deletion and cascade

| Method, path | Auth | Semantics |
|---|---|---|
| `DELETE /vms/{child}` | `ChildOwnerOrAdmin` (shared callers `403 not-owner`) | fence (`Deleting=true`), job `child-delete` → `202 { jobId }`. Repeated call while deleting → `200 { jobId, replayed: true }` (the fence carries the job id in `CurrentJobId`). |
| `DELETE /vms/{primary}` without children | existing `VmOwnerOrAdmin` | **unchanged**: fence + `remove-vm` job. |
| `DELETE /vms/{primary}` with ≥ 1 child, no body | `VmOwnerOrAdmin` | `409 cascade-confirmation-required { children: [{ name, sharing, state, diskGb, mediaCount }], cascadeToken }`. `cascadeToken` = SHA-256 of `parent + sorted child names + parent's children generation counter`, valid for 10 minutes. The parent is **not** fenced by the preview. |
| `DELETE /vms/{primary}` with body `{ cascade: { token, children: string[] } }` | `VmOwnerOrAdmin`, audited `vm.delete cascade=n` | in one transaction: verify token and list against the current children (`409 cascade-scope-changed { children, cascadeToken }` on any difference), set `ChildCreationClosed` and `Deleting` on the parent, `Deleting` on every child, clear the parent's token hash → job `parent-cascade-delete` → `202`. |

`parent-cascade-delete`: `fence` → `children` (each child: remove VM + disk, forwards,
media references, network rules; failures recorded per child and the job continues) →
`primary` (existing `RemoveAsync` steps) → `forwards` → `media-references` → `network` →
`done`. Any per-child failure makes the job `failed` with per-child outcomes; records with
failed cleanup stay (`Deleting=true`, storage charged) and a repeated `DELETE` re-runs the
cascade for the remainder (idempotent; children created between confirmation and deletion
cannot exist because the parent is closed at acceptance).

### 8.9 Sharing, lease renewal, hardware and media of a child

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `PUT /vms/{child}/sharing` | `ChildOwnerOrAdmin`, audited `vm.share` | `{ scope: "private"\|"host" }` | `200 { scope }` + `INetworkPolicyReconciler.OnSharingChangedAsync` | `403 sharing-not-allowed` (owner `allowSharing=false`), `400 sharing-scope-unsupported` (`selected`), `409 not-a-child` |
| `POST /vms/{child}/lease` | `ChildOwnerOrAdmin`, audited `vm.lease.renew` | `{ lifetime }` | `200 LeaseResponse` | `403 lifetime-not-allowed`, `409 lease-inactive` (VM not running: use `start`) |
| `PUT /vms/{child}/hardware` | `ChildOwnerOrAdmin`, audited `vm.hardware` | `{ cpus?, ramMb?, diskGb? (grow only), firmware?: {...} }` | `200 hardware` | `409 vm-not-off`, `409 template-locked` (template change after TPM init), `409 capacity-exhausted`, `409 unsupported-capability` |
| `PUT /vms/{child}/media` | `ChildOwnerOrAdmin`, audited `vm.media` | `{ installMediaId?: string\|null, auxiliaryMediaId?: string\|null }` (null detaches) | `200 media[]` | `409 vm-not-off`, `409 media-not-ready`, `403` (not owned) |

### 8.10 Media

| Method, path | Auth | Request | Response | Errors |
|---|---|---|---|---|
| `POST /media/acquire` | `UserOrPrimaryToken`, audited `media.acquire` | `{ url, name?, role: "install"\|"auxiliary", expectedSha256?, operationKey? }` | `202 { jobId, mediaId }` | `400 url-refused`, `413 media-too-large`, `409 capacity-exhausted`, `409 media-limit { maxItemsPerUser }` |
| `POST /media/uploads` | `UserOrPrimaryToken`, audited `media.upload.begin` | §6.4 | `201`/`200` | as §6.4 |
| `PUT /media/uploads/{id}/chunks/{index}` | owner/admin | octet-stream | `204` | `400 chunk-size`, `409 upload-expired`, `404` |
| `GET /media/uploads/{id}` | owner/admin | – | `200` status | `404` |
| `POST /media/uploads/{id}/complete` | owner/admin, audited `media.upload.complete` | – | `201 MediaItemResponse` / `202 { jobId }` / `200` (idempotent) | `409 upload-incomplete`, `422 checksum-mismatch`, `422 not-an-iso` |
| `DELETE /media/uploads/{id}` | owner/admin, audited | – | `204` | `404` |
| `GET /media` | `UserOrPrimaryToken` (own), `Admin` (`?owner=`, all) | – | `200 MediaItemResponse[]` | |
| `GET /media/{id}` | owner/admin; shared caller gets the reduced shape when attached to a shared child | – | `200 MediaItemResponse` | `404` |
| `GET /media/{id}/references` | owner/admin | – | `200 [{ vmName, slot, created }]` | |
| `DELETE /media/{id}` | owner/admin, audited `media.delete` | – | `204` (or `202 { jobId }` when the file is held open and cleanup will retry) | `409 media-in-use { references }` |
| `POST /media/cleanup` | `Admin` | – | `202 { jobId }` | `503 maintenance` |

```
MediaItemResponse { id, owner (owner/admin only), name, role, source, sourceUrl?, state, sizeBytes?,
                    reservedBytes, sha256? (auxiliary: owner/admin only), expectedSha256?, error?, jobId?,
                    created, readyAt?, references: int }
```

### 8.11 Connectivity / forward requests (distinct target identity)

The existing three forward routes keep their paths, DTOs and behaviour for the self case.
New behaviour is reached by the **caller/target relationship**, evaluated by the new
`ForwardRequester` policy (§12.3), never by the legacy self check:

| Method, path | Auth (new) | Request | Response |
|---|---|---|---|
| `POST /vms/{target}/forwards` | `ForwardRequester`: self (existing, any VM token kind), owner user, admin, parent's primary token, shared caller (child with `sharing=host`) | existing `CreateForwardRequest` + optional `connectPort?` (defaults to `vmPort`) | existing `ForwardResponse` + additive `target: { vmName, via?, connectAddress?, requestedBy }` |
| `GET /vms/{target}/forwards` | same relationship set | `?includeChildren=true` on a primary lists its children's forwards too (each entry carries its own `vmName`) | existing shape |
| `DELETE /vms/{target}/forwards/{id}` | owner/admin/parent token/the requester of that forward | – | `204` |
| `POST /vms/{target}/forwards/{id}/ack` | owner user, admin, or the user who requested it (`requestedBy`) — never a VM token | existing | existing |
| `GET /vms/{child}/addresses` | `ChildOperator` | – | `200 { addresses: [{ address, family, source, observedAt }], reachableFrom: ["parent"] , isolation: "none" }` |

Host-target rules for a child: owner's `AllowHostForwards` **and** `network.hostForwardsEnabled`
must both be true; a shared caller additionally needs the same for the **owner** (not
themselves). Client-target forwards of a child are tunnelled by the owner's extension
**through the parent's SSH endpoint** to `target.connectAddress` (§12.2); while the child
has no observed address, the forward is recorded with `status: "error"`, `message: "guest
address unknown yet"` and re-acked by the extension when an address appears. Children
cannot call any forward route (they have no credential); a legacy token on a child name
cannot exist.

### 8.12 Console

All routes: `ConsoleOperator` = `ChildOperator` for children, `VmOwnerOrAdmin` for
primaries (additive; primaries get the same screenshot/input, subject to capabilities).
Every call re-authorizes the VM and the session; every mutation is audited without the
typed text (`keyboard kind=text chars=12`).

| Method, path | Request | Response | Errors |
|---|---|---|---|
| `GET /vms/{name}/console/capabilities` | – | `200` as `VmCapabilitiesResponse.console` | |
| `POST /vms/{name}/console/sessions` | `{ operationKey? }` | `201 { sessionId, expiresAt (now+60 s), screen: { width, height }, capabilities }` | `409 console-unavailable` (VM off, no video head), `429 rate-limited` (max 4 open sessions per VM) |
| `POST …/sessions/{sid}/renew` | – | `200 { expiresAt }` | `410 console-session-expired` |
| `DELETE …/sessions/{sid}` | – | `204` | |
| `GET …/sessions/{sid}/screenshot?width=&height=` | – | `200 image/png` (headers `X-Construct-Screen-Width/Height` = native) | `400 validation` (dims ≤ native, ≥ 1), `409 console-unavailable { returnValue }`, `410`, `429` (> 4/s per session), `413` (byte cap) |
| `POST …/sessions/{sid}/keyboard` | `{ kind: "text"\|"key"\|"scancodes"\|"ctrlAltDel", text?: string (≤ 512 chars), keyCode?: int, press?: bool (key down/up when `kind=key`), scancodes?: int[] (≤ 64 bytes) }` | `200 { accepted: true, returnValue: 0 }` | `409 console-unavailable { device: "keyboard", returnValue }`, `429` (> 50/s) |
| `POST …/sessions/{sid}/mouse` | `{ kind: "moveAbsolute"\|"moveRelative"\|"click"\|"press"\|"release", x?, y?, dx?, dy?, button?: 1\|2\|3 }` | `200 { applied: true }` or `200 { applied: false, unavailable: { device: "syntheticMouse"\|"ps2Mouse", returnValue, fallback: "moveRelative"\|null } }` | `409 console-unavailable` when neither device exists, `429` |

Coordinates are native pixels; the service maps from the last screenshot dimensions the
session requested when `x/y` exceed native (`400 validation`). Text reaches the host
process through **stdin** of the WMI script, never an argument (§13.1 `IConsoleTransport`).
Sessions are in memory only; a service restart ends them (`410`). Interactive video is
never offered: `console.interactive = unsupported` and no route exists.

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
| `POST /host/updates/check` | `Admin`, audited `host.update.check` | – | `200 { installed: { commit, packageVersion }, latest: { commit, packageVersion, publishedAt, releaseTag, compatible: bool, reasons: string[] }?, checkedAt }` | `502 release-source-unreachable`, `422 unsigned-manifest` |
| `POST /host/updates/stage` | `Admin`, audited | `{ releaseTag?: string }` (default latest) | `202 { jobId, updateId }` | `409 update-in-progress`, `409 unsupported-downgrade` |
| `POST /host/updates/apply` | `Admin`, audited | `{ updateId, operationKey? }` | `202 { jobId }` | `409 update-not-staged`, `409 update-in-progress`, `503 maintenance` |
| `POST /host/updates/cancel` | `Admin`, audited | `{ updateId }` | `200 { state }` | `409 update-not-cancellable` (past hand-off) |

### 8.16 Jobs and audit (existing routes, additive)

| Method, path | Auth | Change |
|---|---|---|
| `GET /jobs` (new) | `UserOrPrimaryToken` | `?kind=&state=&vm=&since=&limit=` → `200 JobResponse[]` per §7.5 |
| `GET /jobs/{id}`, `/events` | existing + `RequesterVm` rule; SSE gains `phase` events | `JobResponse` gains `phase?`, `operationKey?`, `requesterVm?` |
| `POST /jobs/{id}/cancel` (new) | owner/admin/requester | `200 { cancelled: bool }` |
| `GET /audit` | `Admin` | `?actor=&target=&action=&since=&limit=` |

### 8.17 Problem-detail codes

| Code | Status | Extensions |
|---|---|---|
| `validation` | 400 | `field`, `reason` |
| `lifetime-required` | 400 | |
| `child-lifecycle-route` | 400 | |
| `url-refused` | 400 | `reason`, `address?` |
| `chunk-size` | 400 | `expected` |
| `sharing-scope-unsupported` | 400 | |
| `not-enrolled`, `user-disabled` | 403 | |
| `token-kind-legacy` | 403 | `upgrade: "POST /vms/{name}/token"` |
| `delegation-disabled`, `sharing-not-allowed`, `not-owner`, `not-shared` | 403 | |
| `lifetime-not-allowed` | 403 | `requested`, `allowedMax`, `allowNever` |
| `not-found` | 404 | |
| `name-taken`, `vm-deleting`, `parent-closed`, `not-a-child`, `not-a-primary`, `child-has-no-token`, `no-endpoint`, `vm-not-off`, `lease-inactive`, `operation-in-progress` | 409 | `jobId?` |
| `capacity-exhausted` | 409 | `resource`, `scope`, `requested`, `allowed`, `available`, `reason` |
| `unsupported-capability` | 409 | `capability`, `level`, `notes` |
| `template-locked`, `guest-shutdown-unavailable`, `console-unavailable` | 409 | `device?`, `returnValue?` |
| `cascade-confirmation-required`, `cascade-scope-changed` | 409 | `children`, `cascadeToken` |
| `media-not-ready`, `media-in-use`, `media-limit`, `upload-incomplete`, `upload-expired`, `operation-key-conflict` | 409 | per §6/§7 |
| `update-in-progress`, `update-not-staged`, `update-not-cancellable`, `unsupported-downgrade` | 409 | `updateId?` |
| `console-session-expired` | 410 | |
| `media-too-large` | 413 | `maxBytes` |
| `checksum-mismatch`, `not-an-iso`, `unsigned-manifest` | 422 | |
| `rate-limited` | 429 | `retryAfterSeconds` |
| `release-source-unreachable` | 502 | |
| `maintenance` | 503 | `phase`, `retryAfterSeconds`, `updateJobId?` (+ `Retry-After` header) |

Implementation: `Problems.Coded(int status, string code, string title, string detail,
object? extensions = null)` in a new `Infrastructure/CodedProblems.cs`; existing `Problems`
methods untouched.

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
| `construct vm create (--iso-url URL \| --iso PATH \| --media ID) [--aux-iso PATH \| --aux-media ID] --cpus N --ram-gb G \| --ram-mb M --disk-gb D --lifetime L [--name NAME] [--preset windows\|linux] [--secure-boot on\|off] [--secure-boot-template T] [--tpm on\|off] [--boot-order a,b,c] [--no-network] [--no-start] [--sha256 HEX] [--operation-id ID] [--wait] [--json]` | `POST /media/acquire` or the upload protocol (§6.4) for each local file, then `POST /vms/{me}/children`; `--wait` follows both jobs | `--ram-gb` is `ramMb = G × 1024`. All four resource/lifetime inputs are mandatory; the CLI never defaults them. A `create` with a local ISO shows upload progress (`uploaded 512 MiB of 4.0 GiB`). |
| `construct vm list [--all-shared] [--json]` | `GET /vms?parent={me}` (+ `GET /vms/shared`) | table: name, state, lease expiry/overdue, cpus/ram/disk, sharing, current operation |
| `construct vm inspect NAME [--json]` | `GET /vms/{name}`, `GET /vms/{name}/addresses`, `GET /vms/{name}/capabilities` | full record, addresses ("no address yet" tolerated), capabilities |
| `construct vm start NAME --lifetime L [--json]` | `POST /vms/{name}/lifecycle {start}` | resumes saved too; missing `--lifetime` is a usage error (exit 1) before any call |
| `construct vm shutdown NAME [--wait] [--json]` | `lifecycle {shutdown}` → job | reports `completed`/`timeout`/`unavailable` truthfully |
| `construct vm save NAME [--json]` | `lifecycle {save}` | |
| `construct vm restart NAME [--wait] [--json]` | `lifecycle {restart}` → job | lease unchanged |
| `construct vm renew NAME --lifetime L [--json]` | `POST /vms/{name}/lease` | owner-only; the primary token acts as owner delegate |
| `construct vm share NAME --scope private\|host [--json]` | `PUT /vms/{name}/sharing` | |
| `construct vm delete NAME --yes [--wait] [--operation-id ID] [--json]` | `DELETE /vms/{name}` | without `--yes`: interactive TTY → prompts for the name to be typed back; non-TTY → exit 1 with `--yes required: this deletes the VM, its disk and its saved state`. Children only (a primary token cannot delete its own primary: exit 4). |
| `construct vm media list\|upload PATH [--role install\|auxiliary] [--name N] [--sha256 HEX]\|acquire URL [...]\|delete ID --yes [--json]` | §8.10 | |
| `construct vm console NAME --screenshot FILE.png [--width W --height H]` | session + screenshot | one session per invocation, closed at exit |
| `construct vm console NAME --type "text" \| --key CODE [--press\|--release] \| --scancodes 0f,8f \| --ctrl-alt-del` | keyboard route | text read from the argument **or** `--type-stdin` (preferred for secrets; the argument form warns that it is visible in `ps`) |
| `construct vm console NAME --move X,Y \| --click BTN \| --move-rel DX,DY` | mouse route | `applied: false` with the fallback hint is printed, exit 5 |
| `construct vm forward NAME PORT [--to client\|host] [--label L] [--wait SEC] [--json]` | `POST /vms/{child}/forwards` as the parent's primary token | prints the link like `construct expose`, but uses the `construct vm` exit-code table (§9.6): 7 while the client has not opened it within `--wait`, 4 when refused, 8 when unreachable |
| `construct vm addresses NAME [--json]` | `GET /vms/{name}/addresses` | |
| `construct vm jobs [--json]`, `construct vm wait JOBID [--timeout SEC] [--json]` | `GET /jobs`, `GET /jobs/{id}/events` | |

### 9.4 Output shapes

`--json` prints exactly the API response object (or, for composite commands, `{ job:
JobResponse, vm: VmResponse }`) on stdout, nothing else on stdout. Without `--json`, a
human table. Progress of a followed job streams to **stderr** as `[HH:MM:SS] phase: text`;
`--json-progress` instead writes NDJSON to stdout: `{"event":"progress","at":…,"text":…}`,
`{"event":"phase","phase":…}`, `{"event":"state","job":JobResponse}` (last line), so agents
can consume it line by line. Errors print the problem `title: detail (code)` to stderr.

### 9.5 Idempotent retries

Every job-starting command sends `X-Construct-Operation-Key`; the value is `--operation-id`
when given, otherwise a generated UUID printed in the JSON output as `operationKey` and on
stderr as `operation id: …`. Re-running with the same id returns the same job (the CLI
prints `replayed` and continues to follow it). Network failures during the initial POST are
retried up to 3 times with the same key.

### 9.6 Exit codes

| Code | Meaning | HTTP mapping |
|---|---|---|
| 0 | success | 2xx, job `succeeded` |
| 1 | usage or local error (missing jq, bad flag, missing `--yes`, unreadable file) | – |
| 2 | request rejected as invalid | 400, 413, 422 |
| 3 | not found | 404 |
| 4 | refused | 401, 403 |
| 5 | conflict, capacity, unsupported capability, console device unavailable | 409, 410 |
| 6 | job failed or cancelled | job `failed`/`cancelled` |
| 7 | timed out waiting (`--wait`/`--timeout`), including a client forward nobody opened in time | – |
| 8 | service unreachable or answered something unusable | transport, 5xx other than 503, non-JSON |
| 9 | credential not delegated (legacy token, no service URL) | 403 `token-kind-legacy` |
| 10 | maintenance | 503 |
| 11 | rate limited | 429 |

## 10. Extension contract

### 10.1 Module layout (§4.8 rules of `extension/ARCHITECTURE.md`)

| File | Role |
|---|---|
| `extension/src/hostadmin.js` | pure logic, no `vscode`: state machine for a host's admin view, DTO → view-model mapping, cascade confirmation content, capability/feature detection, allowed-action derivation. Unit-tested under node (`extension/test/hostadmin.test.js`). |
| `extension/src/remotehost.js` | existing client gains **additive** request helpers only (`health()`, `hostStatus()`, `hostConfig()`, `users()`, `vms(query)`, `children(parent)`, `lifecycle(name, body)`, `deleteVm(name, body)`, `media()`, `jobs()`, `updates*()`), one per route, same error mapping. |
| `extension/src/drivers/hyperv-remote.js` | additive: `queryChildren(instance)` and `capabilities` gains `children: true` when `/health` lists it (resolved lazily; default false). |
| `extension/media/hostadmin.html/.js/.css` | the webview; message protocol extended under a new `hostadmin.*` namespace. |
| `extension.js` | one command registration block and one `driverOpts` extension; no other edits. |

### 10.2 Views

| View | Where | Content |
|---|---|---|
| **Host administration** (webview panel, one per enrolled host) | command `construct.openHostAdmin` (palette) and a "Host" button in the instance picker for remote instances whose identity is admin | tabs: Overview (`/host/status`, capacity bars, maintenance state, active jobs), VMs (`/vms?kind=all&owner=`), Users (`/users`, allowance editor, tokens), Media (`/media?owner=all`, references, transfer states, cleanup button), Operations (`/jobs`, progress, failures, retry buttons for cleanup jobs, `/audit`), Configuration (`/host/config` editor with validation problems inline, `/host/capabilities`), Maintenance (`/host/updates/*`). |
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
Their virtual disks, saved state and exclusively owned media are removed permanently.

  child-a   running   private   80 GB disk
  child-b   saved     SHARED HOST-WIDE (other users may be using it)   40 GB disk

Type the instance name to confirm.
```

The typed name is required (existing rule for remote removal), the request carries the
`cascadeToken` and the list, and a `409 cascade-scope-changed` re-opens the dialog with the
new list. The minimal user view's child **Delete** uses a smaller modal naming the child,
its sharing state and "disk and saved state are removed permanently".

### 10.5 What is absent by contract

No guest update/provision/reinstall action in the admin module (those stay in the
per-instance workflow), no host filesystem access, no local commands that assume the
service is local, no child start/resume/console for ordinary users in the panel.

## 11. Host update contract (D5)

### 11.1 Release production

New workflow `.github/workflows/host-release.yml`, trigger: push to `main` touching
`service/**`, `drivers/**`, `lib/**`, `bin/**`, `Provision-AgentVM.ps1`, `config/**`,
`.github/workflows/host-release.yml`, plus `workflow_dispatch`. Steps: `dotnet test
service/Constructd.sln`; `dotnet publish service/src/Constructd.Api -c Release -r win-x64
--self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true`;
package; sign; `gh release create host-<commit40>` with the assets. Release tag pattern:
`host-<40-hex commit>`; `latest` for the updater = the newest `host-*` release whose commit is
an ancestor of `main` (the workflow only runs on `main`, so "newest by `createdAt`" holds).
The ISO tool is **not** rebuilt (D5); the package ships `config/iso-builder.json` as is.

### 11.2 Package layout and manifest

```
construct-host-<commit7>-win-x64.zip
  manifest.json
  SHA256SUMS                         sha256 of every file below, relative paths
  service/                           dotnet publish output (Constructd.Api.exe, runtimes, appsettings.json)
  scripts/                           the host-side subset of the repo: drivers/, lib/, bin/, config/,
                                     Provision-AgentVM.ps1, service/host/*.ps1, docs/ (text only)
  updater/Update-ConstructHost.ps1   the independent updater (Windows PowerShell 5.1)
manifest.json.sig                    Ed25519 signature over manifest.json (release asset, separate file)
```

`manifest.json`:

```
{ "schemaVersion": 1,
  "commit": "<40 hex>", "packageVersion": "2026.09.07+<commit7>", "builtAt": "…",
  "repository": "permissionBRICK/The-Construct", "releaseTag": "host-<commit>",
  "service": { "path": "service", "executable": "Constructd.Api.exe", "runtime": "win-x64", "selfContained": true },
  "scripts": { "path": "scripts" },
  "updater": { "path": "updater/Update-ConstructHost.ps1", "sha256": "…" },
  "hashes": { "SHA256SUMS": "<sha256 of the SHA256SUMS file>", "zip": "<sha256 of the zip>" },
  "database": { "schemaVersion": <SqliteMigrations.SchemaVersion>, "minReadableBy": <MinReadableBy>, "breakingMigrations": [] },
  "config": { "settingsSchemaVersion": 1, "requiredKeys": [], "newKeysWithDefaults": ["HostAdmin:*"] },
  "compat": { "minInstalledCommitDate": "2026-08-01", "minSchemaVersionToUpdateFrom": 0 } }
```

### 11.3 Trust and verification (service side, before staging is reported complete)

1. Release metadata is fetched from `https://api.github.com/repos/<updates.repository>/releases` over TLS with system roots; only assets of that repository are ever downloaded.
2. `manifest.json` and `manifest.json.sig` are downloaded; the signature is verified with `updates.manifestPublicKey` (Ed25519, key shipped in host config by the installer and printed by the workflow). `requireSignature=false` is allowed only when `Fake=true` (**decided here**: an unsigned manifest over TLS from a pinned repository is acceptable for a developer host, never for a real one).
3. The zip is downloaded to `updates\<commit>\package.zip`, its SHA-256 compared with `manifest.hashes.zip`; extracted; `SHA256SUMS` hashed and compared; every listed file hashed and compared; the updater script's hash compared with `manifest.updater.sha256`.
4. Compatibility: `manifest.database.minReadableBy <= installed schemaVersion`-consistency (a downgrade below the installed `MinReadableBy` is refused `unsupported-downgrade`), `manifest.compat.minSchemaVersionToUpdateFrom <= installed schema`, free space ≥ 2 × package size + 1 GiB on both the service and data volumes.
5. The resolved `commit` is pinned in the `host_updates` row; nothing later re-resolves `main`.

Failure at any step leaves the staging directory with a `verification-failed.json` and the
row in state `stageFailed`; the running service is untouched.

### 11.4 Staging directory

`<DataDir>\updates\` (`C:\ProgramData\Construct\service\updates\`, hardened like the data
dir): `<commit>\package.zip`, `<commit>\extracted\…`, `<commit>\verified.json`,
`backup-<oldCommit>-<timestamp>\service\`, `backup-…\scripts\`, `backup-…\constructd.db`,
`handoff.json`, `last-update.json` (recovery record, §11.7), `updater.log`.
Retention: the two most recent backups and the current staged package; older ones removed by
the updater after a successful health check.

### 11.5 `host-update` job phases and the drain gate

| Phase | Actor | What happens |
|---|---|---|
| `check` | service | §11.3 steps 1–2 |
| `download`, `verify` | service | §11.3 steps 3–5; state `staged` |
| `drain` (apply only) | service | `IMaintenanceGate` → `draining`; wait until no gated job (§7.1) is `queued`/`running`, up to `updates.drainTimeoutMinutes`; timeout → `applyFailed { reason: "drain-timeout", blockingJobs }`, gate reopened |
| `handoff` | service | write `handoff.json` (`{ updateId, commit, stagedPath, publishDir, scriptsDir, dataDir, serviceName, previousCommit, healthUrl, adminCliPath }`), set gate `maintenance`, create and start a one-shot scheduled task `Construct-HostUpdate` running as SYSTEM: `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <staged>\updater\Update-ConstructHost.ps1 -Handoff <path>` (argv through `IProcessRunner`, `schtasks.exe /Create … /SC ONCE /ST … /RU SYSTEM /F` then `/Run`; **decided here**: a scheduled task survives the service stop, a child process's fate under the SCM does not). The job then records `handedOff` and the service waits to be stopped. |
| `stop` | updater | waits for the service to reach `Stopped` (`Stop-Service`, timeout 120 s; the service's `maintenance` state makes this quick) |
| `backup` | updater | copy publish dir, scripts dir and `constructd.db` (+ `-wal`, `-shm` if present) into `backup-…`; record in `last-update.json` |
| `replace` | updater | `robocopy /MIR` of `service/` → publish dir **excluding** `appsettings.Production.json`, `*.db*`; `robocopy /MIR` of `scripts/` → scripts dir **excluding** `.construct-tools\`, `keys\`, `settings.json`, `projects\`; ACLs re-applied by calling the shipped `Install-ConstructHost.ps1 -AclOnly` (new switch, hardening only) |
| `start` | updater | `Start-Service`; wait for `Running` |
| `health` | updater | poll `GET /api/v1/health` on loopback (`https://127.0.0.1:<port>`, pinned to the host's own certificate thumbprint from `appsettings.Production.json`) until `status=ok` and `commit == manifest.commit`, timeout `healthTimeoutSeconds`; then `constructd admin host status --json` must exit 0 |
| `commit` | updater | write `install.json` (`{ commit, packageVersion, installedAt, previousCommit, updateId }`) next to the executable; delete the scheduled task; prune old backups; `last-update.json.outcome = succeeded` |
| `rollback` | updater | §11.7 |

### 11.6 Health check

Healthy = the process is `Running`, `/health` answers `200` with the expected commit,
`schemaVersion >= manifest.database.schemaVersion` (migrations applied), the admin CLI can
open the database, and the hypervisor probe in `/host/status` (`health.hypervisor`) is not
required (a host with Hyper-V briefly unavailable must not trigger a rollback of a good
binary; **decided here**).

### 11.7 Rollback rules and the recovery record

| Situation | Action |
|---|---|
| `start` or `health` fails and `manifest.database.breakingMigrations` is empty | stop service; restore publish dir and scripts dir from backup; **keep the database** (additive migrations are readable by the previous binary by §1.4 rule); start; health check against `previousCommit`; outcome `rolledBack`. |
| `start`/`health` fails and a breaking migration was applied | stop; restore binaries and scripts **and** the database backup (the new binary ran only during the failed health window, so nothing user-visible is lost; every job that ran in that window is listed in `last-update.json.lostJobs` from the new database before it is replaced); start; outcome `rolledBackWithDatabase`. |
| Rollback itself fails | leave everything in place, outcome `recoveryFailed`, `last-update.json` names the phase, the backup path and the exact manual steps; the scheduled task is left (disabled) for inspection; the updater exits non-zero. |
| Interrupted updater (power loss) | on next boot the service (whatever version starts) reads `last-update.json`; if `outcome` is missing, `/host/updates/status` reports `interrupted` with the last recorded phase; an admin re-runs `POST /host/updates/apply` (which re-verifies the staged package and restarts from `drain`) or restores manually. |
| Migration compatibility | automatic binary rollback is offered only when `manifest.database.minReadableBy <= previous.schemaVersion`; otherwise the updater restores the database backup too. The manifest states both numbers; the updater does not guess. |

`last-update.json` (the local recovery record, readable with the service down):
`{ updateId, commit, previousCommit, phase, phaseAt, outcome?, error?, backupPath,
stagedPath, healthAttempts, lostJobs: [], manualSteps: string[] }`. The updater rewrites it
at every phase transition (temp file + rename).

### 11.8 Persistent update status API

`host_updates` table (migration 600): `id TEXT PRIMARY KEY, commit TEXT NOT NULL,
release_tag TEXT, package_version TEXT, state TEXT NOT NULL, phase TEXT, phases_json TEXT,
started TEXT, finished TEXT, outcome TEXT, error TEXT, previous_commit TEXT, actor TEXT`.

```
HostUpdateStatusResponse {
  installed: { commit, packageVersion, installedAt, previousCommit? },
  current?: { updateId, commit, state: "checking"|"staged"|"stageFailed"|"draining"|"handedOff"|"applying"|"succeeded"|"rolledBack"|"rolledBackWithDatabase"|"recoveryFailed"|"interrupted"|"cancelled",
              phase?, phases: [{ name, at, outcome?, error? }], started, finished?, error?, blockingJobs?: [] },
  history: [ …last 10 rows… ],
  recoveryRecord?: last-update.json contents when its outcome is not succeeded,
  latestKnown?: { commit, packageVersion, publishedAt, checkedAt } }
```

At startup the service reconciles `host_updates` with `install.json` and
`last-update.json`: a row in `handedOff`/`applying` whose commit now equals the installed
commit becomes `succeeded`; one whose `previous_commit` is installed becomes `rolledBack`
(with the record's detail); anything else becomes `interrupted`.

### 11.9 What is preserved

`appsettings.Production.json` (never overwritten; new keys are optional with defaults),
the certificate (Windows store, untouched), `constructd.db` (migrated additively), the
media and ISO directories, `.construct-tools\` (ISO executable; `config/iso-builder.json`
is replaced with the package's copy and the tool is re-resolved lazily on next use),
`keys\`, the service registration (`binPath` unchanged because the executable path is
unchanged), firewall rules, forwards (`netsh` rules; reconciled at startup), VM
registrations, tokens, users, and every Hyper-V VM, which keeps running throughout.

## 12. Network seams

### 12.1 Interfaces (Core, stage 1)

| Interface | Purpose | Hyper-V implementation now | Fake |
|---|---|---|---|
| `IGuestAddressProvider` | observed guest addresses for direct access and forward targets | `HyperVGuestAddressProvider`: `Get-VMNetworkAdapter -VMName` `IPAddresses` (KVP-reported), through the driver contract function `Get-ConstructVmAddresses` in `HyperVLocal.ChildVm.ps1` | `FakeGuestAddressProvider` (dictionary) |
| `IAccessExposure` | materialize an authorized access request as a client forward, a host forward or a direct-address answer | wraps the existing `NetshPortForwardManager` for host forwards (connect address = the target VM's address instead of the endpoint host), records client forwards with `ForwardTargetIdentity` for the extension to tunnel via the parent | `InMemoryAccessExposure` over `InMemoryPortForwardManager` |
| `INetworkPolicyReconciler` | apply/reconcile/revoke access rules on create, sharing change, delete and periodically | `NoIsolationNetworkPolicy`: records intended rules in `network_rules`, enforces nothing, reports `isolation = "none"` | same class (it is pure bookkeeping) |
| `IHostNetworkPolicy` | host-level switches | reads `host_config.network` | same |

### 12.2 Modes on Hyper-V in this delivery

| Mode | Status | How |
|---|---|---|
| Client forwarding to the requester's PC | **supported** | for a primary: existing path. For a child: forward recorded with `target = { vmName: child, via: parent, connectAddress }`; the owner's extension polls `GET /vms/{parent}/forwards?includeChildren=true` and opens `ssh -L <local>:<connectAddress>:<vmPort> <parent alias>` over the parent's existing SSH endpoint, then acks. Requires the child to be reachable from the parent (true on one switch; not claimed as isolation). |
| Host forwarding | **supported when policy allows** | `netsh` rule `connectaddress = child address`; owner `AllowHostForwards` and `network.hostForwardsEnabled` both required; independently disableable. |
| Direct guest address | **reported only** | `GET /vms/{child}/addresses`; the caller (a primary on the same switch) dials it. No promise beyond "this is the address the hypervisor observed". |
| Parent ↔ child bidirectional rules, shared-consumer rules | **recorded, not enforced** | `network_rules` rows `{ vm, peer, kind: parent-child\|shared-consumer, state: intended }`; `isolation: "none"` everywhere. |
| Firewall/enterprise adapter, Proxmox | **unsupported** | interface only; documented inputs: rule set per VM (peers, ports), events `VmCreated`, `VmDeleted`, `SharingChanged`, `AddressChanged`. |

### 12.3 Distinct target identity for forward requests

```csharp
public sealed record ForwardTargetIdentity(
    string VmName,            // the VM serving the port (child or primary)
    string? Via,              // the primary whose SSH endpoint carries a client tunnel; null for self/primary targets
    string? ConnectAddress,   // observed guest address the tunnel/rule connects to; null when unknown yet
    string RequestedBy);      // principal name: user, or "vm:<primary>"

public enum ForwardRelationship { Self, Owner, Admin, Parent, Shared }

public sealed record ForwardRequest(
    string RequesterPrincipal, ForwardRelationship Relationship, string TargetVm,
    ForwardTarget Target, int VmPort, int ConnectPort, string Label);
```

Authorization (`ForwardRequesterHandler`): resolve the relationship in this order and stop
at the first that matches — `Self` (VM token on its own name; **the existing check,
unchanged**), `Admin`, `Owner` (user owns the target or its parent), `Parent` (primary
token of kind primary whose VM is the target's parent), `Shared` (target is a child with
`Sharing=Host`, owner enabled, requester is a user or a primary-kind token). A legacy token
never matches anything but `Self`. Then policy: host target ⇒ owner's `AllowHostForwards`
∧ `hostForwardsEnabled`; client target ⇒ always allowed for the matched relationships.
Children can match nothing: they hold no credential.

### 12.4 `network_rules` (migration 700) and `forwards` columns

`network_rules (id TEXT PRIMARY KEY, vm_name TEXT NOT NULL COLLATE NOCASE, peer TEXT NOT
NULL COLLATE NOCASE, kind TEXT NOT NULL, state TEXT NOT NULL, created TEXT NOT NULL,
updated TEXT NOT NULL)`; `forwards` gains `target_vm`, `target_via`,
`target_connect_address`, `requested_by` (all NULL for today's rows).

## 13. Seams and file ownership for parallel implementation

### 13.1 Core abstractions added in stage 1 (with in-memory fakes and DI hooks)

Everything below is added **before** the pairs branch, by the integrator (delegation +
sharing + lifecycle pair), in new files. Signatures are the contract; bodies of the real
implementations belong to the owning pair.

```csharp
// Constructd.Core/Domain/VmKinds.cs
public enum VmKind { Primary, Child }
public enum SharingScope { Private, Host, Selected }
public enum VmTokenKind { Legacy, Primary }
public enum LeaseState { Inactive, Active, Unlimited, Expired, Overdue }
public enum CapabilityLevel { Unsupported, Conditional, Supported }
public enum SecureBootTemplate { MicrosoftWindows, MicrosoftUefiCertificateAuthority }
public enum BootDevice { InstallMedia, AuxiliaryMedia, Disk, Network }
public enum GuestReportProvenance { Unknown, Provisioner }
public enum MediaRole { Install, Auxiliary }
public enum MediaSource { Url, Upload }
public enum MediaState { Pending, Transferring, Ready, Failed, Deleting }
public enum MediaSlot { Install, Auxiliary }
public enum ReservationResource { Ram, Cpu, Storage }
public enum ReservationPhase { Pending, Held }
public enum GracefulShutdownOutcome { Completed, Timeout, Unavailable, Failed }
public enum MaintenanceState { Open, Draining, Maintenance }

// Constructd.Core/Domain/Lease.cs, ChildHardware.cs, GuestReport.cs, HostObservation.cs,
// UserAllowance.cs, VmOverride.cs, EffectiveAllowance.cs, MediaItem.cs, MediaUpload.cs,
// Reservation.cs, HostConfig.cs, ForwardTargetIdentity.cs, HostUpdateRecord.cs — records of §1.1.

// Constructd.Core/Abstractions/IDelegationPolicy.cs        (owner: delegation pair)
public interface IDelegationPolicy
{
    Task<EffectiveAllowance> ResolveAsync(string owner, string? parentVm, CancellationToken ct);
}

// Constructd.Core/Abstractions/IHostConfigStore.cs         (owner: delegation pair; used by all)
public interface IHostConfigStore
{
    Task<T?> GetAsync<T>(string section, CancellationToken ct) where T : class;
    Task SetAsync<T>(string section, T value, string updatedBy, CancellationToken ct) where T : class;
}

// Constructd.Core/Abstractions/IVmRepository.cs — ADDITIVE members (owner: delegation pair)
Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct);
Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct);
Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct);
/// Atomic: name free, owner below maxPrimaries (primaries) or maxRetainedChildren (children), parent not fenced.
Task<VmAddOutcome> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct);
Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct);
Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct);
Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct);
Task SetOverrideAsync(VmOverride? value, CancellationToken ct);
// VmAddOutcome gains: ParentClosed, ChildrenQuotaExceeded

// Constructd.Core/Abstractions/IUserStore.cs — ADDITIVE (owner: delegation pair)
Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken ct);
Task<bool> SetAllowanceAsync(string name, UserAllowance allowance, CancellationToken ct);

// Constructd.Core/Abstractions/ITokenService.cs — ADDITIVE (owner: delegation pair)
Task<string> IssueVmTokenAsync(string vmName, VmTokenKind kind, CancellationToken ct); // existing overload keeps Legacy semantics for callers that do not pass a kind, but create-vm passes Primary
Task<bool> RevokeVmTokenAsync(string vmName, CancellationToken ct);
// TokenPrincipal gains: VmTokenKind? TokenKind

// Constructd.Core/Abstractions/IChildVmDriver.cs           (owner: child-vm driver + jobs pair)
public sealed record ChildVmDescriptor(string Name, ChildHardware Hardware, string? VhdPath,
    string? InstallMediaPath, string? AuxiliaryMediaPath, string SwitchName);
public sealed record HypervisorVmInfo(string Name, string Id, VmState State, string RawState, int Generation,
    int Cpus, long MemoryStartupBytes, long MemoryAssignedBytes, bool DynamicMemory,
    IReadOnlyList<HypervisorDiskInfo> Disks, long? SavedStateBytes, string ConfigVolume);
public sealed record HypervisorDiskInfo(string Path, long MaxBytes, long FileBytes, string? ParentPath, string Volume);
public sealed record HostResourcesInfo(int LogicalCpus, long TotalRamBytes, long FreeRamBytes,
    IReadOnlyList<VolumeInfo> Volumes, DateTimeOffset ObservedAt);
public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes);
public interface IChildVmDriver
{
    Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct);
    Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct);
    Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct);
    Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct);
    Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct);
    Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct);
}
// Start/Save/GetState/Stop stay on IHypervisorDriver (existing), used for children too.

// Constructd.Core/Abstractions/IHypervisorInventory.cs     (owner: capacity pair)
public interface IHypervisorInventory
{
    Task<IReadOnlyList<HypervisorVmInfo>> ListVmsAsync(CancellationToken ct);
    Task<HostResourcesInfo> GetHostAsync(CancellationToken ct);
}

// Constructd.Core/Abstractions/ICapacityLedger.cs          (owner: capacity pair)
public sealed record ReservationRequest(string Owner, string? VmName, IReadOnlyList<ReservationLine> Lines, string? JobId);
public sealed record ReservationLine(ReservationResource Resource, long Amount, string? Artifact, string? Volume);
public sealed record CapacityDecision(bool Allowed, IReadOnlyList<string> ReservationIds,
    string? Resource, string? Scope, long Requested, long Allowed, long Available, string? Reason);
public interface ICapacityLedger
{
    Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct);
    Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct);
    Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct);
    Task<int> ReconcileAsync(CancellationToken ct);
    Task<HostCapacitySnapshot> SnapshotAsync(CancellationToken ct);
}

// Constructd.Core/Abstractions/IMediaStore.cs, IMediaTransfer.cs   (owner: media pair)
public interface IMediaStore { /* Get/List/Add/Update/Remove MediaItem; references; uploads */ }
public interface IMediaTransfer
{
    Task AcquireAsync(MediaItem item, Uri source, IProgress<string>? progress, CancellationToken ct);   // §6.3 rules inside
    Task WriteChunkAsync(MediaUpload upload, int index, Stream body, CancellationToken ct);
    Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct);
    Task<bool> TryDeleteAsync(string path, CancellationToken ct);  // false = held open
}
public interface IUrlAdmissionPolicy { Task<UrlAdmission> CheckAsync(Uri url, CancellationToken ct); } // pure address rules, Core

// Constructd.Core/Abstractions/IConsoleTransport.cs         (owner: console pair)
public sealed record ConsoleCapabilities(CapabilityLevel Screenshot, CapabilityLevel Keyboard,
    CapabilityLevel MouseAbsolute, CapabilityLevel MouseRelative, CapabilityLevel Interactive,
    int MaxScreenshotBytes, bool NativeResolutionOnly);
public interface IConsoleTransport
{
    ConsoleCapabilities Capabilities { get; }
    Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct);                       // native dims, device presence
    Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct); // PNG bytes or failure code
    Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct); // text via stdin
    Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct);
}
public interface IConsoleSessionStore { /* in-memory sessions: create, renew, get, expire; per-VM caps */ }

// Constructd.Core/Abstractions/IMaintenanceGate.cs          (owner: host release + updater pair)
public interface IMaintenanceGate
{
    MaintenanceState State { get; }
    bool IsGated(string jobKind);
    Task<DrainResult> DrainAsync(TimeSpan timeout, CancellationToken ct);
    void Enter(MaintenanceState state, string? updateJobId);
    void Reopen();
}
// Constructd.Core/Abstractions/IReleaseSource.cs, IUpdateStager.cs, IUpdaterLauncher.cs, IHostUpdateStore.cs (same owner)

// Constructd.Core/Abstractions/IGuestAddressProvider.cs, IAccessExposure.cs, INetworkPolicyReconciler.cs (owner: network pair) — §12.1

// Constructd.Core/Abstractions/ICapabilityAggregator.cs     (owner: integrator)
public interface ICapabilityAggregator { Task<BackendCapabilities> GetAsync(CancellationToken ct); }

// Constructd.Core/Abstractions/IReleaseInfo.cs               (owner: integrator; read by /health and /host/status)
public sealed record InstalledRelease(string Commit, string PackageVersion, DateTimeOffset? InstalledAt, string Source);
public interface IReleaseInfo { InstalledRelease Installed { get; } }
```

Fakes (`Constructd.Fakes`, stage 1, one file each): `FakeChildVmDriver`,
`FakeHypervisorInventory`, `InMemoryCapacityLedger` (a real pure implementation over an
in-memory list; the SQLite one adds persistence), `InMemoryMediaStore`,
`FakeMediaTransfer` (temp-dir files, scripted failures), `FakeConsoleTransport` (returns a
fixed PNG and scripted return values), `InMemoryConsoleSessionStore`,
`InMemoryMaintenanceGate`, `FakeReleaseSource`, `FakeUpdateStager`, `FakeUpdaterLauncher`,
`InMemoryHostUpdateStore`, `FakeGuestAddressProvider`, `InMemoryAccessExposure`,
`NoIsolationNetworkPolicy` (real), `InMemoryHostConfigStore`, `FakeReleaseInfo`.

DI hooks (stage 1): `Composition/HostAdminComposition.cs` with
`AddHostAdminCore(options)` (registers every fake in fake mode and every real
implementation on Windows through per-feature extension methods `AddMediaPlatform`,
`AddCapacityPlatform`, `AddChildVmPlatform`, `AddConsolePlatform`, `AddUpdatePlatform`,
`AddNetworkPlatform`, each in its own file `Composition/<Feature>Composition.cs` owned by
the pair). `ServiceComposition.AddConstructdServices` gets **one line**:
`services.AddHostAdminCore(options);`. `Program.cs` gets **one line per feature** at a
marked block: `.MapHostAdminEndpoints().MapDelegationEndpoints().MapMediaEndpoints()
.MapConsoleEndpoints().MapUpdateEndpoints().MapNetworkEndpoints()`; hosted services
`LeaseSchedulerService`, `CapacityReconciliationService`, `MediaCleanupService` are
registered inside their feature composition, not in `Program.cs`.

Authorization: `Auth/DelegationAuthorization.cs` (integrator) adds the policies of §2.1 and
the claim `constructd:vm-token-kind`; `VmTokenAuthenticationHandler` gains one line adding
that claim; `UserClaimsTransformation` gains the `Enabled` check.

### 13.2 Ownership map

| Pair | Owns (new files unless stated) | Touches shared files (one-line hooks only) |
|---|---|---|
| **Integrator / delegation + sharing + lifecycle** | `Core/Domain/*` new records and enums; `Core/Abstractions/{IDelegationPolicy, IHostConfigStore, ICapabilityAggregator, IReleaseInfo}.cs`; additive members on `IVmRepository`, `IUserStore`, `ITokenService`; `Core/Logic/{LeaseRules, DelegationRules, CascadeToken, LifetimeParser}.cs`; `Core/Services/{LeaseSchedulerService, DelegationPolicy}.cs`; `Sqlite/Migrations/{SqliteMigrationRunner, ISqliteMigration, SqliteMigrations, M100_VmKindsAndDelegation}.cs`; `Sqlite/{SqliteHostConfigStore, SqliteVmOverrideStore}.cs`; `Api/Auth/DelegationAuthorization.cs`; `Api/Endpoints/{DelegationEndpoints, HostAdminEndpoints, IdentityExtensions, HealthEndpoints}.cs`; `Api/Jobs/{ChildLifecycleJobs, CascadeJobs}.cs`; `Api/Contracts/{DelegationContracts, HostAdminContracts}.cs`; `Api/Infrastructure/CodedProblems.cs`; `Fakes/{InMemoryHostConfigStore, FakeReleaseInfo}.cs`; changes to `Sqlite/SqliteVmRepository.cs`, `SqliteUserStore.cs`, `SqliteTokenService.cs` (reader/writer for new columns), `Core/Services/IdlePolicyEngine.cs` (skip children), `Core/Domain/Vm.cs`, `User.cs`, `Job.cs`, `PortForward.cs` (additive params), `Core/Abstractions/IHypervisorDriver.cs` (`BackendCapabilities` record only) | `Program.cs` (health + delegation + host-admin map lines, the marked block), `ServiceComposition.cs` (`AddHostAdminCore`), `SqliteDatabase.cs` (call the runner), `Auth/TokenAuthenticationHandlers.cs`, `Auth/UserClaimsTransformation.cs`, `Auth/AuthorizationSetup.cs` (register new handlers), `Endpoints/VmEndpoints.cs` (kind-aware `/power` refusal, `DELETE` cascade branch, `GET /vms` query), `Endpoints/JobEndpoints.cs` (`RequesterVm` read rule), `Jobs/VmJobs.cs` (issue `Primary` token kind) — **the integrator owns every conflict in these files** |
| **Media** | `Core/Abstractions/{IMediaStore, IMediaTransfer, IUrlAdmissionPolicy}.cs`; `Core/Logic/{UrlAdmissionRules, IsoSignature, MediaNameSanitizer}.cs`; `Sqlite/Migrations/M200_MediaRegistry.cs`; `Sqlite/SqliteMediaStore.cs`; `Windows/Media/{MediaFileStore, HttpMediaTransfer, PinnedAddressHandler}.cs`; `Api/Endpoints/MediaEndpoints.cs`; `Api/Jobs/MediaJobs.cs`; `Api/Contracts/MediaContracts.cs`; `Api/Hosting/MediaCleanupService.cs`; `Composition/MediaComposition.cs`; `Fakes/{InMemoryMediaStore, FakeMediaTransfer}.cs`; tests `Tests/Media/*` | `Program.cs` (`.MapMediaEndpoints()`), `SqliteMigrations.All` (one line), `HostAdminComposition.cs` (one line), installer (`Media:RootDir` hardening, one block) |
| **Capacity** | `Core/Abstractions/{ICapacityLedger, IHypervisorInventory}.cs`; `Core/Logic/{CapacityMath, ReservationRules}.cs`; `Core/Services/CapacityReconciler.cs`; `Sqlite/Migrations/M300_CapacityLedger.cs`; `Sqlite/SqliteCapacityLedger.cs`; `Windows/HyperV/HyperVInventory.cs` + the `Get-ConstructHostInventory` function in `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` (section marked `# capacity`); `Api/Endpoints/CapacityEndpoints.cs` (`/host/capacity`); `Api/Hosting/CapacityReconciliationService.cs`; `Composition/CapacityComposition.cs`; `Fakes/{InMemoryCapacityLedger, FakeHypervisorInventory}.cs`; tests `Tests/Capacity/*` | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`; call sites in `DelegationEndpoints`/`ChildLifecycleJobs` are **provided by the integrator as `ICapacityLedger` calls with a fake** so the capacity pair never edits them |
| **Child-VM driver + jobs** | `Core/Abstractions/IChildVmDriver.cs`; `Core/Logic/HardwarePresets.cs`; `Windows/HyperV/{HyperVChildDriver, HyperVChildScript}.cs`; `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` (create/remove/hardware/media/graceful shutdown/capabilities; capacity and network sections are owned by those pairs); `drivers/Load-ConstructDriver.ps1` `-Include` switch; `Api/Jobs/{ChildCreateJob, ChildDeleteJob}.cs`; `Sqlite/Migrations/M400_JobOperationKeys.cs`; `Core/Services/InProcessJobEngine.cs` (phase + operation keys, additive), `Core/Abstractions/IJobEngine.cs` (`SubmitAsync` overload with `operationKey`, `SetPhase`); `Fakes/FakeChildVmDriver.cs`; tests `Tests/Windows/HyperVChildDriverTests.cs`, `Tests/Jobs/*`, `test/driver-contract.test.ps1` additions | `Program.cs` none; `SqliteMigrations.All`; `HostAdminComposition.cs`; `Api/Endpoints/JobEndpoints.cs` (`GET /jobs`, cancel — coordinated with integrator) |
| **Console** | `Core/Abstractions/{IConsoleTransport, IConsoleSessionStore}.cs`; `Core/Logic/{Rgb565Png (pure), ConsoleSessionRules}.cs`; `Windows/Console/{HyperVConsoleTransport, HyperVConsoleScript}.cs` (self-contained WMI scripts, stdin payload for text); `Api/Endpoints/ConsoleEndpoints.cs`; `Api/Contracts/ConsoleContracts.cs`; `Composition/ConsoleComposition.cs`; `Fakes/{FakeConsoleTransport, InMemoryConsoleSessionStore}.cs`; tests `Tests/Console/*` (PNG conversion pinned with the feasibility fixtures' layout: 4-byte big-endian length prefix + RGB565) | `Program.cs`, `HostAdminComposition.cs` |
| **Host release + updater** | `.github/workflows/host-release.yml`; `service/host/{Update-ConstructHost.ps1, New-ConstructHostPackage.ps1}`; `Core/Abstractions/{IMaintenanceGate, IReleaseSource, IUpdateStager, IUpdaterLauncher, IHostUpdateStore}.cs`; `Core/Logic/{ManifestRules, UpdateCompatibility}.cs`; `Core/Services/MaintenanceGate.cs`; `Sqlite/Migrations/M600_HostUpdates.cs`; `Sqlite/SqliteHostUpdateStore.cs`; `Windows/Updates/{GitHubReleaseSource, PackageStager, ScheduledTaskUpdaterLauncher, Ed25519Verifier}.cs`; `Api/Endpoints/UpdateEndpoints.cs`; `Api/Jobs/HostUpdateJob.cs`; `Api/Infrastructure/MaintenanceFilter.cs`; `Composition/UpdateComposition.cs`; `Fakes/*` for the five interfaces; tests `Tests/Updates/*`, `service/tests/host-updater.test.ps1`; installer changes (`-AclOnly`, `install.json`, `updates.manifestPublicKey`) | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`, `InProcessJobEngine.SubmitAsync` (gate check — one call, coordinated with the child-jobs pair), `Install-ConstructHost.ps1` (sections marked) |
| **Extension** | `extension/src/hostadmin.js`, `extension/media/hostadmin.*`, `extension/test/hostadmin.test.js`; additive helpers in `extension/src/remotehost.js` and `extension/src/drivers/hyperv-remote.js`; panel children rows in `extension/media/panel.js` (guarded by feature flag) | `extension/extension.js` (one registration block), `extension/package.json` (commands/menus), `extension/ARCHITECTURE.md` (new section) |
| **Guest CLI** | `bin/construct-vm.sh`, `test/construct-vm.test.sh` (bash, fake `curl`), `docs/child-vms.md` (user guide) | `bin/construct` (`vm)` case + usage text), `bin/provision.sh` (guest-report post, one function; `CONSTRUCT_PROVISION_EVENT`), `Provision-AgentVM.ps1` (`-RotateVmToken`, one block; attempt report), `docs/expose.md` (cross-link) |
| **Network** | `Core/Abstractions/{IGuestAddressProvider, IAccessExposure, INetworkPolicyReconciler, IHostNetworkPolicy}.cs`; `Core/Domain/ForwardTargetIdentity.cs`; `Core/Logic/ForwardRelationshipRules.cs`; `Core/Services/NoIsolationNetworkPolicy.cs`; `Sqlite/Migrations/M700_NetworkPolicy.cs`; `Sqlite/SqliteNetworkRuleStore.cs`; `Windows/Network/HyperVGuestAddressProvider.cs` + `Get-ConstructVmAddresses` in `HyperVLocal.ChildVm.ps1` (section `# network`); `Api/Auth/ForwardRequesterHandler.cs`; `Api/Endpoints/NetworkEndpoints.cs` (`/addresses`); `Api/Contracts/NetworkContracts.cs`; `Composition/NetworkComposition.cs`; `Fakes/{FakeGuestAddressProvider, InMemoryAccessExposure}.cs`; extension forwarder child-tunnel path in `extension/src/forwarder.js` (additive branch); tests | `Program.cs`, `SqliteMigrations.All`, `HostAdminComposition.cs`, `Api/Endpoints/ForwardEndpoints.cs` (policy name swap on the three routes + `includeChildren`; the **only** pair allowed to edit this file), `Sqlite/SqliteForwardStore.cs` (new columns), `Windows/Forwards/NetshPortForwardManager.cs` (connect address from target identity, one branch) |

Shared touchpoints and their owners:

| Shared file | Hook | Owner of the hook and of every conflict |
|---|---|---|
| `Api/Program.cs` | one `.Map<Feature>Endpoints()` line per pair inside the marked block | integrator |
| `Composition/ServiceComposition.cs` | `services.AddHostAdminCore(options);` | integrator |
| `Composition/HostAdminComposition.cs` | one `services.Add<Feature>Platform(options)` / fake line per pair | integrator |
| `Sqlite/SqliteDatabase.cs` | `SqliteMigrationRunner.Apply(...)` call | integrator |
| `Sqlite/Migrations/SqliteMigrations.cs` | one `new M<id>_…()` line per pair | integrator |
| `Api/Contracts/Requests.cs`, `Responses.cs` | **not edited**; each pair has its own `Contracts/<Feature>Contracts.cs` | – |
| `Core/Domain/Enums.cs` | **not edited**; new enums live in `Domain/VmKinds.cs` (integrator) | integrator |
| `Core/Abstractions/IHypervisorDriver.cs` | `BackendCapabilities` record only | integrator |
| `Api/Endpoints/ForwardEndpoints.cs` | policy name swap + `includeChildren` | network pair |
| `Api/Endpoints/VmEndpoints.cs`, `JobEndpoints.cs`, `Jobs/VmJobs.cs`, `Auth/*` | listed hooks | integrator |
| `Core/Services/InProcessJobEngine.cs`, `Core/Abstractions/IJobEngine.cs` | phase/operation-key overload, gate check | child-vm jobs pair (gate call agreed with the updater pair) |
| `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` | sections `# childvm`, `# capacity`, `# network` | child-vm pair owns the file; the other two own their marked sections |
| `drivers/Load-ConstructDriver.ps1` | `-Include` switch | child-vm pair |
| `bin/construct`, `bin/provision.sh`, `Provision-AgentVM.ps1` | `vm)` case, guest-report post, `-RotateVmToken` | guest CLI pair |
| `service/host/Install-ConstructHost.ps1` | `-AclOnly`, `install.json`, `Media:RootDir`, `updates.manifestPublicKey` | updater pair (media pair supplies its block) |
| `extension/extension.js`, `extension/package.json` | one registration block | extension pair |

Rules for every pair: new code in new files; a shared file listed above is edited only at
its hook; anything else in a shared file goes through the integrator as a small separate
change; `Constructd.Core` keeps zero package references; every Windows call is argv through
`IProcessRunner` and pinned by a recording-runner test; every new PowerShell file runs on
Windows PowerShell 5.1 (no pwsh-only syntax; parsed under pwsh by `test/driver-contract.test.ps1`
or the pair's own suite); no secret in any log, exception, argument, job result or test output.

## 14. Acceptance checklists, limitations and field test

### 14.1 Per stage

| Stage | Scope | Acceptance (all on Linux unless stated) |
|---|---|---|
| **S1 seams** (integrator) | §13.1 abstractions, fakes, DI, migration runner + M100, `/health`, `/whoami` additions, `/vms/{name}/identity`, coded problems, token kinds + rotation route, `Vm`/`User` extensions, idle engine skips children | `dotnet build` 0 warnings; existing 638 tests green unchanged; new: migration runner tests (§1.4), pre-feature DB fixture keeps every row and reads back as primaries with legacy tokens, `create-vm` issues `primary` kind, rotation invalidates the old hash and answers 401 next, legacy token reaches exactly the six routes of §1.6 (route-matrix test extended), a disabled user's Bearer and VM tokens fail, route inventory test updated, audit-coverage test green for every new mutating route |
| **S2 media** | §6, §8.10 | URL rules table pinned by tests (each refused class; redirect re-validation; pinned connect address; unknown length; size cap; checksum mismatch cleanup); upload begin/chunk/status/complete/abort including resume and idempotent replay; references block delete; cleanup retains held-open files with reason; storage reservation equals artifacts that really remain after every failure path; no URL query in audit/logs (sentinel test); child creation never calls `IIsoBuilder`/`IIsoCatalog` (fake call recording) |
| **S3 capacity** | §4, `/host/capacity` | concurrency test: two creates/starts cannot both take the last RAM/storage; release only after confirmed transition (fake driver reports `Running` → held; `Off` → released; `Unknown` → kept); pending timeout; external start creates an owner-charged hold; unmanaged VMs reduce host capacity without an owner; growth-only storage math with a fixture volume (no double count); user aggregate across two primaries; shared start charged to owner; refusal bodies carry requested/allowed/available; recording-runner test pins the inventory script |
| **S4 child driver + jobs** | §8.6–8.8 jobs, `IChildVmDriver`, presets, operation keys, phases | recording-runner tests pin: create script sets template **before** TPM, fixed RAM, checkpoints off, DVD slots, boot order by device object; `UpdateHardwareAsync(resendTemplate:false)` never resends the template; graceful shutdown script uses `InitiateShutdown` and polls, never `-Force`/`-TurnOff`/`Save`; job phases stream; operation-key replay returns the same job; cascade fences parent in one transaction and rejects a stale token; interrupted cleanup is retryable with ownership records intact; `driver-contract.test.ps1` parses the new driver file under pwsh; local loader without `-Include` is byte-identical (test diff) |
| **S5 console** | §8.12 | RGB565 → PNG conversion pinned against the feasibility layout (length prefix) with synthetic fixtures; dims above native refused before any process; byte cap; text travels through stdin (recording runner asserts no text in argv and no text in logs — sentinel); session TTL/renew/expiry; per-VM device lookup by VM id, never a global first device (script pinned); mouse unavailable path returns `applied:false` with `returnValue`; shared caller allowed, legacy token refused, revocation refuses the next call |
| **S6 delegation + sharing + lifecycle** | §2, §5, §8.3–8.9, §8.14 | permission matrix as a table-driven test (every cell of §2.2); policy re-evaluated (allowance change refuses the next create without re-auth); lease activation on confirmed Running, start requires lifetime, restart keeps expiry, service restart keeps expiry (MutableClock + fresh app over the same SQLite file), overdue path on `Unavailable`/`Timeout` never saves/kills/deletes (fake driver call recording), external start → overdue; sharing table; cascade content and scope change; guest report never overwrites success facts with attempts; idle engine ignores children |
| **S7 network** | §12 | relationship resolution order pinned; child cannot self-forward (no credential path exists — test that a child's name with any token kind is refused); host target refused when either switch is off, also via parent and via shared; target identity on forwards; `includeChildren` listing; addresses route; `NoIsolationNetworkPolicy` reports `none`, never `enforced`; netsh argv pinned with the child connect address; extension forwarder tunnels through the parent (unit test with fake spawn) |
| **S8 extension** | §10 | node tests: state machine for every row of §10.3, admin module absent for local and for `role=user`, host switching re-resolves identity, old-service detection by 404, maintenance banner, cascade dialog content lists children and shared flags, child rows offer exactly Shut down and Delete, Shut down sends `lifecycle shutdown` (never `power save`), no guest update/provision/reinstall action in the module (snapshot test of the command list); `ui-smoke.js` green |
| **S9 guest CLI** | §9 | `test/construct-vm.test.sh` with a fake `curl`: identity gate (legacy → exit 9), every command's request shape, JSON pass-through, NDJSON progress, exit-code table, `--yes` gating on non-TTY, operation-id reuse on retry, token never in argv (fake curl asserts `-H @file`); `remote-e2e.test.sh` extended with a child create/list/shutdown/delete round trip against the fake service |
| **S10 host release + updater** | §11 | workflow file validated (actionlint or schema check in tests); `New-ConstructHostPackage.ps1` + `Update-ConstructHost.ps1` tested under pwsh with a fake service directory and a stub `Start-Service`/`Stop-Service`/`schtasks` layer: phases, backup, exclusion lists, health loop, rollback with and without DB restore per manifest, interrupted-record handling, task cleanup; service tests: manifest verification (hash tampering, missing signature, downgrade refusal), drain gate refuses gated kinds with `503 maintenance` and lets ungated ones through, drain waits for running gated jobs, hand-off writes the record, startup reconciliation of `host_updates` |
| **S11 integration** | all | `remote-e2e.test.sh` full round trip; route inventory and audit coverage tests cover every route of §8; `dotnet build` 0/0; all pwsh/bash/node suites green; docs updated (`service/README.md`, `docs/remote-host.md`, `docs/drivers.md`, `docs/expose.md`, `extension/ARCHITECTURE.md`, new `docs/child-vms.md`, `docs/host-updates.md`); the field-test checklist below handed to the owner |

### 14.2 Documented limitations (this delivery)

| Limitation | Why | Where reported |
|---|---|---|
| Nothing in this run was executed on Hyper-V or on the real host service | D3 | every stage summary must say "validated on Linux with fakes"; §14.3 is the owner's checklist |
| Interactive console (VMConnect/RDP/VNC) is unsupported | D2 | `console.interactive = unsupported`; no route |
| Mouse absolute is `conditional`; preboot pointer input failed (32768) on the Gen 2 probe; relative mouse unsupported on Gen 2 | feasibility | per-VM capabilities, `applied:false` answers |
| Ordinary-key visual effects, text entry into a guest OS, keyboard layouts and Unicode are unverified | feasibility | `notes[]` in capabilities |
| LocalSystem execution of the WMI console/child scripts is unverified | feasibility | field test item |
| Generation 1 children unsupported | not probed | `generations: [2]` |
| Secure Boot template cannot change after TPM initialization | Hyper-V | `template-locked` |
| No memory overcommit, no dynamic memory | policy | capabilities `unsupported` |
| Network isolation is not enforced; rules are recorded only | no enforcing adapter | `isolation: "none"` on every address/forward answer |
| Client forwards to a child require the child to be reachable from its parent and an observed guest address | tunnel via parent | forward `status: error` until an address is known |
| No media content download; auxiliary contents never leave the host | secrets in answer files | §6.2 |
| UDF-only ISOs accepted only with a checksum | signature check limits | `not-an-iso` |
| Updates require a signed manifest and a public key in host config; unsigned only in fake mode | trust | `unsigned-manifest` |
| The updater's scheduled-task hand-off, robocopy exclusions and rollback are tested with stubs, not on Windows | D3 | field test |
| `POST /users` old shape keeps `maxVms` default 0 | zero-change | §8.4 |
| Sharing scope `selected` is stored but never grantable | reserved | `sharing-scope-unsupported` |
| No automatic keepalive; explicit renewal only | wall-clock lease | §5.3 |
| Guest reports depend on `bin/provision.sh` posting; older guests never report (`unknown`) | additive | `guest.provenance = unknown` |

### 14.3 Field-test checklist (for the owner, real Hyper-V host, later run)

Preconditions: a disposable second host or the existing host with a maintenance window;
the current constructd backed up (`C:\ProgramData\Construct\service`, publish dir,
scripts dir); an admin token; a user account; one existing primary (`haus-vm` class) that
must keep running throughout.

| # | Check | Pass criterion |
|---|---|---|
| 1 | Install the new build with the installer; run `GET /health` | `schemaVersion` matches, existing VM listed as `kind=primary`, `tokenKind=legacy`, its `construct expose` and heartbeat still work |
| 2 | Rotate the primary's token via reprovision `-RotateVmToken` | old token → 401; `construct vm identity` shows `primary` |
| 3 | `construct vm media acquire` a public Ubuntu server ISO with checksum; then one Windows evaluation ISO by upload | both `ready`, sizes and hashes match, storage reservation visible in `/host/capacity` |
| 4 | `construct vm create` Linux preset (2 CPU, 2048 MB, 20 GB, `2h`) and Windows preset (4 CPU, 4096 MB, 60 GB, `4h`, TPM) | both reach `running`; Windows Secure Boot template `MicrosoftWindows`, TPM enabled; screenshot shows the installer; capacity numbers add up against `Get-VM` |
| 5 | Console keyboard/mouse on the Linux child during the installer | screenshot changes after keys; mouse `applied:false` reported truthfully if the installer has no pointer support |
| 6 | Run the WMI scripts under the service (LocalSystem) | screenshot and keyboard succeed from the API, not only from an interactive shell |
| 7 | Concurrency: two `start` requests for two saved children when only one fits in RAM | exactly one succeeds; the other gets `capacity-exhausted` with correct numbers |
| 8 | Start a child outside the API (`Start-VM`) | reconciliation charges it to the owner and marks the lease `overdue`; expiry job shuts it down gracefully |
| 9 | Let a `15m` lease expire on a child with integration services; and on one without | first: `expired`, VM Off, RAM released; second: `overdue`, `guest-shutdown-unavailable`, VM still running, RAM still charged, no save/force/delete |
| 10 | Share a child host-wide; from a second user's primary: inspect, start, screenshot, request client forward; attempt delete | operational actions succeed and are charged/audited to the owner; delete → 403 |
| 11 | Disable host forwards; request host forward for a child via parent and via shared caller | both refused; client forwards still work through the parent tunnel |
| 12 | Delete the parent primary with one private and one shared child | preview lists both with the shared flag; typed name required; all three gone; media references released; no orphan files under the media root; unrelated `haus-vm` untouched |
| 13 | Stage an update from a real `host-*` release; apply while a media acquire is running | `draining` waits for the acquire; new child creates get `503 maintenance`; existing VMs keep running; clients reconnect; `install.json` and `/host/updates/status` report the pinned commit |
| 14 | Apply a deliberately broken package (tampered SHA256SUMS; then a build whose health check fails) | first refused at verify; second rolls back automatically, status `rolledBack`, DB intact, `last-update.json` readable with the service stopped |
| 15 | Old client (pre-change extension/PS) against the new service | every existing flow (create, provision, expose, idle, remove) behaves identically |

Record the outcome of each item, the release commit, and any capability that had to be
downgraded to `unsupported`, in `docs/plans/host-administration-field-test.md`.
