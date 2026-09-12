# Remote reprovision source cache (design contract)

Status: **shipped host-cache contract, extended by the source-overlay implementation**.
Overlay owner decision: 2026-09-12, branch `feat/source-overlay`. The overlay provisions below supersede the original equivalence-only defaults T2/T3 and C14/C15.
Date: 2026-09-11 (revision 3 after review rounds 1 and 2). Branch: `rr/contract`.
Inputs: the owner's goal of 2026-09-11 (below), `Provision-AgentVM.ps1`, `bin/provision.sh`,
`lib/AgentVm.Common.ps1`, `lib/AgentVm.Remote.ps1`, `install.ps1`, `Update-Construct.ps1`,
[Remote host](../remote-host.md), [Provisioning](../provisioning.md),
[Host releases](../host-release.md), `service/README.md`, the
[host administration contracts](host-administration-contracts.md) (§7.3 admission, §7.4
maintenance, §8.14 guest report, §11 host update protocol) and the current service source
(`service/src`).

> **Goal (project owner, 2026-09-11).** Reprovisioning a remote (service-managed) VM is slow
> because every run packs the whole Construct scripts checkout on the client PC, uploads it
> over SSH through the host's forwarded port and unpacks it on the VM. Instead: the client sends
> only its configuration, and the **host service** keeps the Construct source per commit
> (downloaded once from the published release, verified by `sourceSha256`, preserved and reused
> for every reprovision of every VM on that host) and hands it to the guest. Local Hyper-V
> installs keep today's upload path unchanged.

This document decides names, shapes, codes, ownership and tests. Where the goal left a
technical choice open, the choice is made here and marked **decided here** with one line of
rationale; §0 lists every such choice in one place. §0.1 lists the proposed technical defaults
that touch product behaviour; **the owner was not reachable in this run** (the contract is
written autonomously against the goal text), so none of them claims owner approval, and each
was chosen for the original goal: source is never evicted automatically, and the original client
used the cache only for equivalent checkouts. The owner's 2026-09-12 decision adds overlays for
divergent checkouts as specified in §4.6.1. The rest of the host cache contract is unchanged. Line numbers
refer to the tree at `253f3ce` (`origin/main`). Facts marked *measured* were checked on that
tree on 2026-09-11 (Linux; python3 `sqlite3` and `zipfile`).

**Regression boundary.** The local single-VM install path is byte-for-byte unchanged: a
`Provision-AgentVM.ps1` run without `-ServiceUrl` (and without an instance whose registry entry
names a service) packs, uploads and unpacks exactly as today, prints the same lines, and passes
the same environment to `bin/provision.sh`; the checkout it packs contains no new file. Every
schema change is additive with a default that reproduces today's behaviour; every new route is
new; every new API field is optional; every existing token behaves identically.
`bin/provision.sh` and everything downstream of `/opt/construct/repo` see no difference between
the two transports. In `auto` mode a service-managed reprovision acquires **no new hard
dependency** on the service: every new service call is bounded by a timeout and any failure
means "upload as before" (§4.8, with the exact worst-case latency).

Nothing here has been run on Hyper-V or Windows; the validation plan in §9 is Linux-only
(fakes, fake-mode API, pwsh, bash) exactly like the previous contracts.

## 0. Choices made in this document (each **decided here**, with its rationale)

| # | Choice | Value | Rationale | Compatibility effect |
|---|---|---|---|---|
| C1 | Transport to the guest (§3) | **Guest pulls** `GET /api/v1/vms/{name}/source/{commit}` with its VM token; no SSH push by the service | the service has no SSH credential for any guest and must not get one; the guest already talks to the service with its token and CA over the link every heartbeat uses | none |
| C2 | Cache identity (§1) | full 40-hex commit; one zip per commit, byte-identical to the released `construct-source-<commit>.zip` | the release asset is what `sourceSha256` covers; re-packing would lose the hash | none |
| C3 | Where the source comes from (§1.3) | the immutable release `host-<commit>` of the host's configured `updates.repository`, never `latest`, never a branch | the commit the client runs is what must land on the guest; `latest` can move mid-run | none |
| C4 | No release for the commit (§1.3, §4) | job fails `source-unavailable`; the client uploads as today and says so | a non-`main` ref, a pre-release commit and a fork without releases all reduce to "no asset"; one code, one fallback | none |
| C5 | Registry (§1.2) | SQLite table `source_cache` (migration `M800_SourceCache`, column `commit_sha` because `commit` is a SQLite keyword) plus files under `<data>\source\` | retention, pinning and recovery need queries, not directory scans; matches the media registry | additive migration |
| C6 | Retention (§1.5, T1) | **no automatic eviction of any kind**: a ready item stays until an admin deletes it or it is found corrupt; `MaxTotalBytes` is a hard cap that makes a new ensure fail `source-cache-full` (the client uploads) | the goal says "preserved and reused"; the strictest reading needs no owner clarification and keeps the lock protocol small | none |
| C7 | Disk bound and charging (§1.6) | `HostAdmin:Source:MaxTotalBytes` (default 2 GiB) caps the sum of every row that may still own a file (`downloading`, `ready`, `deleting`); the row is the reservation; charged to the **host**, not to the requester, and not to the capacity ledger | a commit serves every VM on the host, so no user owns it; the ledger models per-user VM RAM/CPU/storage and would need a synthetic owner; the ISO catalog is likewise host-charged | none |
| C8 | Concurrency (§1.7) | lock order **per-commit download gate → process-wide catalog lock → SQLite**; state changes only under the catalog lock; physical file removal only under the commit's gate with zero readers; ensure never touches another commit's gate | one download per commit; one global lock for short registry sections makes open/delete/pin decisions serialisable across commits; no cross-gate acquisition means no deadlock | none |
| C9 | Integrity before serving (§1.4) | size and SHA-256 of the stored zip re-checked on **every** serve, before the first byte | a few MB hash in milliseconds; a corrupt file must never reach a guest with a 200 | none |
| C10 | Guest verification (§3.4) | the guest checks size **and** SHA-256 against values the client passed in the environment (from the ensure result), not against response headers | the expected hash must come from a different channel than the bytes | none |
| C11 | Extraction on the guest (§3.5) | `python3 zipfile` with source-specific entry rules (directory entries normalised, one top-level directory stripped, no `..`, no absolute, no symlinks, size bound), modes restored (git records `755` only; unrecorded → `644`/`755`); staged **on the destination filesystem**, swapped in by rename, old tree restored on failure, nothing fallible after the old tree is discarded | `unzip` is not on minimal Ubuntu; python3 is (already required by `bin/install-t3code-prebuilt.py`); a swap across `/tmp` could destroy the old repo on a failed copy | none |
| C12 | Client decision (§4.2) | pure function `Get-ConstructSourceTransportPlan`; `-SourceMode auto|cache|upload`, default `auto`; `auto` needs: service-managed, feature `source-cache`, `constructRef` = `main`, a 40-hex commit, a **verified-equivalent** tree (§4.3), no `-IncludeGit`; `cache` needs only the feature and a commit and is the explicit override | every input is observable on the PC without a network call except the feature flag; the decision is unit-testable with fakes | none for local installs (`ServiceManaged=$false` short-circuits to `upload`) |
| C13 | Commit the cache serves (§4.3) | the checkout's own identity (`git rev-parse HEAD`, or `.construct-revision` of an archive install), **not** `installedCommit` | the cache must ship what is on disk; `installedCommit` keeps its meaning for `CONSTRUCT_VERSION` and the guest report | `CONSTRUCT_VERSION`, `provisionedCommit`, guest report unchanged |
| C14 | Equivalence of a git checkout (§4.3, T2) | in `auto`, any line of `git status --porcelain --untracked-files=all --ignore-submodules=none` (tracked change **or** untracked non-ignored file; the explicit option overrides a user's `status.showUntrackedFiles=no`) → upload, with a warning naming the count and the two ways out | a developer's local edits and additions must reach the VM; the cache cannot represent them; a custom provision command may reference a new, not yet committed guest script | none |
| C15 | Equivalence of an archive install (§4.3, §4.4) | verified, never assumed: `install.ps1` and `Update-Construct.ps1` record a per-file SHA-256 manifest of the extracted zip **outside the checkout** (`%LOCALAPPDATA%\The-Construct\source-manifests\<commit40>.sha256`); `auto` hashes the tree against it; a mismatch, an extra file or a missing manifest → upload | "upload when source equivalence cannot be established" needs no product policy about hand edits; the manifest lives outside the checkout so the local tar and the local guest tree stay identical | archive installs made before this change upload until their next `Update-Construct.ps1`, which is today's behaviour |
| C16 | First provision of a fresh VM (§4.6) | also uses the cache; the one-time VM token reaches the guest through the existing stdin channel before the fetch, in a temp file the fetch reads and the client removes | reprovision is the goal, but the first provision is the same code path and the token delivery mechanism already exists | none |
| C17 | Feature flag (§5) | `apiFeatures` entry `source-cache`; absent when `HostAdmin:Source:Enabled=false` | clients probe, never assume; an admin can switch the host to "upload only" without a code change | none for existing clients (they never read `apiFeatures`) |
| C18 | Fetch is audited although it is a read (§6.4) | explicit `vm.source.fetch` entry written by the handler | which VM pulled which commit is the one fact an admin will ask for; `GET` routes are not audited by the middleware | none |
| C19 | Size caps (§6.2) | `MaxItemBytes` default 256 MiB (service and guest), manifest 1 MiB, uncompressed ≤ 16× compressed and ≤ 1 GiB (*measured*: the archive at `253f3ce` is 4.0 MB compressed, 15.6 MB uncompressed, ratio 3.9) | text compresses better than the host package's 4× bound allows; 16× leaves room without permitting a zip bomb | none |
| C20 | Options are bootstrap settings, not a `host_config` section (§1.1) | `Constructd:HostAdmin:Source:{Enabled,RootDir,MaxItemBytes,MaxTotalBytes}` | retention is an operator setting like `Media:RootDir`; no runtime UI is needed | none |
| C21 | Ensure admission (§2.2) | both the ready and the queued path commit operation key, `vms.source_commit` pin and (queued only) the job in **one** `IAdmissionStore.AdmitAsync` transaction through an additive `AdmissionPlan.VmSourceCommit`; a plan without a reservation runs on `ledger.BeginMutationAsync` (database-only, no inventory I/O); the admission call is made **outside** the catalog lock; maintenance handle taken before admission and transferred to `StartPersistedAsync`; the stored replay body is the complete original response | the existing §7.3 seam is the only atomic write path; `MutateAsync` opens `ledger.BeginAsync`, which can read host inventory in enforce mode and must never run under the source catalog lock; taking the maintenance gate inside the worker is too late; a replay that the client cannot parse would defeat the key | additive plan parameter, default `null` |
| C22 | Job error transport (§1.3, §4.8) | `SourceException(code) : IConstructdError` whose message **is** the code; the job body converts `UpdateException` to it, so `Job.Error` is always a bare code | `SafeError.Describe` repeats an `IConstructdError` message verbatim; `UpdateException` may carry a detail instead of its code | none |
| C23 | Client orchestration seam (§4.7) | two-phase `Invoke-ConstructSourceTransport` (`-Phase begin` issues the ensure once and returns a state object; `-Phase complete` waits, stages, fetches, falls back) with scriptblock seams for every side effect | the failure matrix must be testable at the orchestration level; the early ensure must not be issued twice | none |
| C24 | Structured API error seam (§4.7) | additive `Get-ConstructApiLastProblem` (status, RFC 7807 `code`, allow-listed transport class) populated by `Invoke-ConstructApi` next to the existing status/message accessors, which keep their values | `-NoThrow` today keeps only status and message; the client needs the code and the failure class without changing any existing caller | none |
| C25 | `-InstallSource` (§4.5) | does not exist today and is **not** introduced; `-SourceMode` plus the existing `-Repo`/`-Ref`/`-IncludeGit` cover every case | one new parameter, named for what it does | none |

### 0.1 Proposed technical defaults that touch product behaviour (no owner approval claimed)

| Id | Default | Why it is the strict reading | What the owner may relax later (API unchanged) |
|---|---|---|---|
| T1 | No automatic eviction; `source-cache-full` → the client uploads until an admin deletes entries (`DELETE /host/source-cache/{commit}`). | "preserved and reused" | a time/LRU window; would add an eviction step to §1.7 and a test. |
| T2 | A git checkout with any `git status --porcelain` line uploads in `auto`. | local additions must reach the VM | ignoring untracked files outside guest-consumed paths; would change one row of §4.3. |
| T3 | An archive install uploads unless its per-file manifest proves equivalence (C15). | equivalence is established, never assumed | trusting archive installs blindly; would drop the manifest check. |
| T4 | An ensure refused with 401/403 uploads in `auto`, throws in `cache`. | a credential problem must not block a reprovision that SSH can still do (the guest report is advisory for the same reason) | stopping the run; one row of §4.8. |

## Terminology

| Term | Meaning |
|---|---|
| **service-managed VM** | A primary created through `POST /vms`, provisioned by `Provision-AgentVM.ps1 -ServiceUrl … -InstanceName …` (or `-InstanceName` resolving a registry entry that names a service). The only kind this contract changes. |
| **source zip** | `construct-source-<commit40>.zip`, the release asset published for tag `host-<commit40>` (`scripts/package-construct-release.py`): `git archive --format=zip --prefix=<repo>-main/` of the tracked tree at that commit plus `.construct-revision`. |
| **cache item** | One source zip stored by the service, keyed by its commit, with its `source_cache` row. |
| **ensure** | The client's request that a commit be present in the cache; a `source-fetch` job. |
| **fetch** | The guest's download of a cache item with its VM token. |
| **transport** | `cache` (this contract) or `upload` (today's tar over scp). |
| **equivalent tree** | A checkout whose content the release zip of its commit reproduces, **proven** by an empty `git status --porcelain --untracked-files=all --ignore-submodules=none` or by the archive manifest of §4.4. |
| **source manifest** | `%LOCALAPPDATA%\The-Construct\source-manifests\<commit40>.sha256`, written by `install.ps1`/`Update-Construct.ps1` from the zip they extracted (§4.4). |
| **config bundle** | Everything the client sends to the guest that is not the source tree (§4.1). It is not a new artefact: it is the set of channels the provisioner already uses. |
| **catalog lock** | The single process-wide `SemaphoreSlim(1,1)` of `SourceCatalog` (§1.7). |
| **download gate** | The per-commit `SemaphoreSlim(1,1)` of `SourceGate` (§1.7). |

## 1. Source cache on the host

### 1.1 Options (bootstrap, `Constructd:HostAdmin:Source`)

New class `HostAdminSourceOptions` on `HostAdminOptions` (`service/src/Constructd.Core/Configuration/HostAdminDefaults.cs`), bound like `HostAdmin:Media`:

| Key | Default | Meaning |
|---|---|---|
| `HostAdmin:Source:Enabled` | `true` | Advertise `source-cache` and serve the routes. `false` removes the feature from `apiFeatures`; the routes stay mapped and answer `409 unsupported-capability`. |
| `HostAdmin:Source:RootDir` | `<data>\source` (`C:\ProgramData\Construct\service\source`; fake mode: a disposable temp dir like media) | Cache files. `<data>` is the parent of `DatabasePath`, as `updates` and `media` derive it. Must not equal or nest with `Iso:CacheDir`, `Media:RootDir` or `<data>\updates` (`source-root-overlap` at startup, like `media-root-overlap`). |
| `HostAdmin:Source:MaxItemBytes` | `268435456` (256 MiB) | Refuse a manifest whose `sourceSizeBytes` exceeds this (`source-too-large`). |
| `HostAdmin:Source:MaxTotalBytes` | `2147483648` (2 GiB) | Hard cap on the sum of `size_bytes` over `downloading`, `ready` and `deleting` rows (§1.6). Must be ≥ `MaxItemBytes` (validated at startup). Nothing is evicted to make room (C6). |
| `HostAdmin:Source:FakeReleaseDir` | – | **Fake mode only** (§9.4): directory the fake release source reads `host-<commit>/manifest.json` and the zip from. Ignored when `Fake=false`. |

The repository is **not** a source option: it is the existing `updates.repository` host-config value (`UpdatesConfig.Repository`, default `permissionBRICK/The-Construct`) after `HostUpdateTrust.Apply`, so production hosts download source from the same repository they update from and the API cannot redirect it.

The installer (`service/host/Install-ConstructHost.ps1`) adds `$sourceRootDir = Join-Path $DataDir 'source'` to the directory-creation loop and the `$hardening` list (`Kind = 'Data'`, `Name = 'construct source cache'`) and writes `HostAdmin.Source.RootDir` next to `HostAdmin.Media.RootDir`. Upgrades preserve an existing value.

### 1.2 Files, registry and states (decided here)

Files, flat, under `RootDir` (owner `SourceFileStore : ISourceFiles`, `service/src/Constructd.Windows/Source/SourceFileStore.cs`, path-confined and reparse-point-checked exactly like `MediaFileStore`; `ISourceFiles` also exposes `FreeBytes()` for the volume and `Exists(commit, kind)`/`Delete(commit, kind)` for `kind ∈ {part, zip}`):

| Path | Meaning |
|---|---|
| `<RootDir>\<commit40>.zip` | A ready item; bytes identical to the release asset. |
| `<RootDir>\<commit40>.part` | A download in progress. |

Registry: migration **`M800_SourceCache`** (`service/src/Constructd.Sqlite/Migrations/M800_SourceCache.cs`, `Breaking = false`, registered after `M700_NetworkPolicy`; `M500` stays unused). The DDL below was executed as written on 2026-09-11 (*measured*, python3 `sqlite3`; the reviewer re-executed it independently); `SourceStoreTests` re-executes it against real SQLite (§9.1):

```sql
CREATE TABLE IF NOT EXISTS source_cache (
  commit_sha    TEXT PRIMARY KEY,          -- 40 lowercase hex ("commit" is a SQLite keyword)
  state         TEXT NOT NULL,             -- downloading | ready | deleting | failed
  size_bytes    INTEGER NOT NULL,          -- manifest sourceSizeBytes; counts toward MaxTotalBytes
  sha256        TEXT NOT NULL,             -- expected (manifest) hash
  release_tag   TEXT NOT NULL,             -- host-<commit>
  error         TEXT NULL,                 -- safe code (deleting: why; failed: what happened)
  job_id        TEXT NULL,                 -- the fetch job that owns the download
  created       TEXT NOT NULL,
  ready_at      TEXT NULL,
  last_used_at  TEXT NOT NULL              -- ensure and fetch both touch it
);
CREATE INDEX IF NOT EXISTS ix_source_cache_last_used ON source_cache(last_used_at);
ALTER TABLE vms ADD COLUMN source_commit TEXT NULL;      -- last commit ensured for this VM
```

Domain (`service/src/Constructd.Core/Domain/SourceItem.cs`):

```
enum SourceState { Downloading, Ready, Deleting, Failed }
record SourceItem(string Commit, SourceState State, long SizeBytes, string Sha256, string ReleaseTag,
                  string? Error, string? JobId, DateTimeOffset Created, DateTimeOffset? ReadyAt,
                  DateTimeOffset LastUsedAt);
sealed class SourceException(string code) : Exception(code), IConstructdError { string Code }
```

**State machine.** The invariant that makes accounting safe: *a row that may still own a file on disk is in `downloading`, `ready` or `deleting` and keeps its byte reservation; a row is `failed` only after both `<commit>.part` and `<commit>.zip` have been confirmed absent.* Every transition happens under the catalog lock (§1.7); file removal happens outside it, under the commit's download gate, with zero readers.

| From | To | Trigger | Files that may exist afterwards | Reservation |
|---|---|---|---|---|
| (none) or `failed` | `downloading` | ensure step 3 (row inserted or replaced) | none yet | taken |
| `downloading` | `ready` | ensure step 6, after the rename `.part` → `.zip` succeeded | `.zip` | kept |
| `downloading` | `deleting` (`error` = the failure code) | download/verify failure, job cancellation, startup recovery | `.part` and/or `.zip` | kept |
| `ready` | `deleting` (`error` = `admin`, `corrupt`, `missing`) | admin delete, corruption found at serve time, startup recovery | `.zip` | kept |
| `deleting` | `failed` (`error` kept) | both files confirmed absent by `ISourceFiles`, with zero readers, under the gate | none | released |
| `deleting` | `deleting` (`error` = `<original>; cleanup:<io code>`) | file removal I/O failure | may remain | kept; retried by the daily job, by the next ensure of the commit, and by the admin route |
| `failed` | (row deleted) | daily job, 24 h after the transition | none | – |

A `deleting` row is never treated as absent: an ensure of that commit first completes its removal (§1.3 step 1) and fails `source-cleanup-pending` when it cannot. A `failed` row is replaced by a new ensure (its reservation is already zero).

Store seam `ISourceStore` (`Constructd.Core.Abstractions`; `SqliteSourceStore`, `InMemorySourceStore`):
`GetAsync(commit)`, `ListAsync()`, `UpsertAsync(item)`, `TryTransitionAsync(commit, from, to, patch)`
(compare-and-set like `IMediaStore.TryTransitionAsync`), `TouchAsync(commit, at)`, `DeleteAsync(commit)`,
`CommittedBytesAsync()` (sum over `downloading|ready|deleting`), `ListPinnedCommitsAsync()` (distinct
non-null `vms.source_commit` ∪ `vms.guest_construct_commit`). `Vm` gains `+ string? SourceCommit = null`
(additive record parameter, projected from `source_commit`; omitted from the VM response when null).
The pin is written only through the admission seam (§2.2), never by `ISourceStore`.

### 1.3 Obtaining a commit (`source-fetch`) (decided here)

`ISourceCache.EnsureAsync(commit, progress, ct)` (`service/src/Constructd.Api/Source/SourceCache.cs`), run inside a `source-fetch` job whose maintenance handle was acquired by the route (§2.2). The download gate of `commit` is held for the whole procedure; no other commit's gate is ever taken.

1. Take the download gate. Under the catalog lock read the row. `ready` → touch `last_used_at`, release both, return. `deleting` → release the catalog lock, run the removal protocol of §1.7 for this commit (we hold its gate); if the row cannot reach `failed` (readers > 0 or an I/O failure) → throw `source-cleanup-pending`. `downloading` is impossible under the gate (§1.8 turns crashed rows into `deleting`). `failed` or absent → continue.
2. `IReleaseSource.GetSourceAssetAsync(repository, commit, ct)` (**new interface method**, additive; `FakeReleaseSource` implements it from a `SourceAssets` dictionary or, in fake mode, from `FakeReleaseDir`). It fetches `https://github.com/<repo>/releases/download/host-<commit>/manifest.json` through the existing `OpenAsync` transport (same redirect allowlist, 1 MiB cap, same `release-source-*` error codes) and validates, in addition to the tagged-manifest rules already in `ListHostReleasesAsync`: `sourceAsset == "construct-source-<commit>.zip"`, `sourceSha256` 64 hex, `0 < sourceSizeBytes ≤ MaxItemBytes`. It returns `SourceAssetDescriptor(Commit, Tag, Url, SizeBytes, Sha256)` and allow-lists the URL for `DownloadAsync`. HTTP 404 on the manifest, or a manifest without `sourceAsset` (a host-only release from before source packaging) → `source-unavailable`; `sourceSizeBytes > MaxItemBytes` → `source-too-large`. Progress `check`.
3. **Free space, outside the lock:** `ISourceFiles.FreeBytes()` ≥ `SizeBytes + 1 GiB` (the `PackageStager` rule) or throw `insufficient-space` (advisory: the exact bound is the byte cap). **Byte admission, under the catalog lock, no I/O:** `CommittedBytesAsync() + SizeBytes ≤ MaxTotalBytes` or throw `source-cache-full`; insert the row as `downloading` with `job_id` and `size_bytes` (the reservation). Two ensures of different commits near the cap are serialised by this lock: the first that inserts wins, the second fails deterministically.
4. `DownloadAsync` to `<commit>.part` (exact byte count enforced by the existing size check); an I/O or transport failure → `source-transfer-failed`. Progress `download`.
5. Hash the `.part`; mismatch → `source-hash-mismatch`. Then the structural check of §6.2 (`SourceZipRules`); failure → `extraction-refused`. Progress `verify`.
6. Rename `.part` → `.zip`; under the catalog lock row → `ready` with `ready_at`, `last_used_at`; progress `ready`. Audit `source.fetch.completed` (actor `system`, target the commit).

On any failure after step 3 (including cancellation): under the catalog lock row → `deleting` with the code, then the removal protocol (§1.7) runs while we still hold the gate; whether or not it reaches `failed`, the job ends with the original code. Every failure path throws `SourceException(code)`; `UpdateException` from the release source is converted to `SourceException(ex.Code)` (C22). The job's `Error` string **is** the code (`source-unavailable`, `source-too-large`, `source-cache-full`, `source-cleanup-pending`, `insufficient-space`, `source-transfer-failed`, `source-hash-mismatch`, `extraction-refused`, `cancelled`, or a `release-source-*` code), so the client names the reason without parsing prose. There is no negative caching: the next ensure retries from step 1 (reprovision is a manual action).

### 1.4 Integrity before serving

`ISourceCache.OpenAsync(commit, ct)` (called by the fetch route):

1. Under the catalog lock: the row must be `ready` (`downloading` → `source-downloading`; anything else → `source-not-cached`); increment the reader count of the commit; release.
2. Outside the lock: open the `.zip` (the handle is now owned by the reader), check `Length == size_bytes`, then SHA-256 of the file == `sha256`.
3. On failure: under the catalog lock row → `deleting` with `error = corrupt` (no new readers from now on); **release the catalog lock**; close the handle, which runs the reader-release path of §1.7 (the last reader out performs the removal when it can); audit `source.corrupt` (actor `system`, detail `reason=size|hash`); the route answers `409 source-corrupt`. Other readers that already passed step 2 keep streaming their open handle: a Windows delete of an open file fails, and the protocol only removes the file at zero readers anyway.
4. On success: return a `SourceReadStream` wrapping the `FileStream`. Its `Dispose` closes the owned handle **first** and then runs the reader-release path exactly once. The route registers `HttpContext.RequestAborted` to cancel the copy loop, which makes ASP.NET dispose the response body and therefore the wrapper; cancellation never releases the count by itself.

The hash is computed before the response starts, so a served 200 is always complete and correct. `last_used_at` is touched on every successful open.

### 1.5 Retention and the daily job (`source-cleanup`) (decided here, C6/T1)

A `ready` item is kept indefinitely. It leaves `ready` only by admin delete (`DELETE /host/source-cache/{commit}`), by corruption (§1.4), or by startup recovery finding its file missing (§1.8). `MaxTotalBytes` is never enforced by removing items: when the cap is reached, new ensures fail `source-cache-full`, the client uploads as before (§4.8) and the admin list (`GET /host/source-cache`) shows `committedBytes` against `maxTotalBytes` so an admin can delete what is no longer needed. **Pinned** (informational: `pinnedBy` in the admin list, and refused by `DELETE` unless `?force=true`) = the commit equals a `vms.source_commit`, or equals / is prefixed by a `vms.guest_construct_commit` (guest reports may carry 7–64 hex).

`ISourceCache.PruneAsync(initiator, progress, ct)` (job `source-cleanup`) does housekeeping only: retries the removal protocol for every `deleting` row; deletes `failed` rows older than 24 h; removes `.part`/`.zip` files that have **no row** and are older than 1 h. It never touches a `ready` row. Result `{ removed: [commit…], retained: [{ commit, reason }…] }` with reasons `ready`, `busy` (readers > 0 or gate held), `cleanup-failed:<code>`, same shape as `media-cleanup`. Triggered by `MediaCleanupService`'s existing daily tick (which now submits a second job, `source-cleanup`, after `media-cleanup`, through `SubmitAsync` with its own `TryEnter("source-cleanup", …)` handle) and by `POST /host/source-cache/cleanup`. Deleting a VM removes only its pin.

### 1.6 Disk bound and charging (decided here, C7)

- **Reservation = row.** A row in `downloading`, `ready` or `deleting` reserves its `size_bytes` from its insert under the catalog lock until its transition to `failed` under the catalog lock, which happens only after `ISourceFiles` confirmed both files absent (§1.2). A crash cannot leak or lose a reservation: recovery (§1.8) moves crashed rows to `deleting`, which still reserves.
- **Admission.** `CommittedBytes + SizeBytes ≤ MaxTotalBytes` under the catalog lock (no I/O); free space ≥ `SizeBytes + 1 GiB` checked before the lock. Failure codes `source-cache-full`, `insufficient-space`; the client uploads (§4.8).
- **Charging.** The host. No user quota, no `ICapacityLedger` reservation, no `EffectiveAllowance` check: the ledger reserves per-user VM RAM/CPU/storage on named volumes for VMs that exist; a source zip belongs to nobody, is shared by every VM on the host and is bounded by its own cap. `GET /host/capacity` does not report it (like the ISO catalog). Recorded in §10 for a host whose `RootDir` volume is also a VM storage volume: the cap is the operator's budget for it.
- **Bandwidth.** One GitHub download per commit per host, ever, unless an admin deletes the item or it is found corrupt; an ensure needs a user credential and is audited.

### 1.7 Concurrency and lock protocol (decided here, C8)

| Primitive | Scope | Held for |
|---|---|---|
| download gate | per commit (`SourceGate`, keyed `SemaphoreSlim(1,1)`, the shape of `InMemoryVmOperationGate`) | the whole ensure (§1.3); every physical file removal of that commit |
| catalog lock | process-wide `SemaphoreSlim(1,1)` in `SourceCatalog` | every row read-modify-write, reader-count change, byte admission; SQLite only, never file I/O, never while waiting for a download gate |
| reader table | `Dictionary<string,int>` under the catalog lock | count of open serve handles per commit |

Lock order **download gate → catalog lock → SQLite**; nothing acquires a download gate while holding the catalog lock; an ensure holds exactly one gate (its own commit) for its whole duration, so two ensures can never wait on each other's gates.

**Removal protocol** (the only way a file leaves the disk), for a commit whose row is `deleting`:

1. Hold the commit's download gate (an ensure already holds it; the daily job, the admin route and the reader-release path use `TryAcquire` with a 1 s wait and report `busy` when they cannot).
2. Under the catalog lock: re-read the row (must still be `deleting`), require readers == 0 (else `busy`; the last reader out will retry), release.
3. Outside the lock: `ISourceFiles.Delete(commit, part)`, `Delete(commit, zip)`; then `Exists` for both.
4. Under the catalog lock: both absent → `failed` (reservation released); otherwise stay `deleting` with `error += "; cleanup:<io code>"`.

**Reader-release path** (from `SourceReadStream.Dispose`, after the handle is closed): take the catalog lock, decrement the count, note whether it reached zero with the row in `deleting`, and **release the catalog lock**. Only then, with no lock held, `TryAcquire` the commit's download gate (1 s) and run the removal protocol from its step 2; when the gate is held (an ensure of that commit is running) skip as `busy`: that ensure completes the removal itself in its step 1. The forbidden order (waiting for a gate under the catalog lock) never occurs.

Consequences:

- **Ensure vs ensure (same commit):** the second waits at the gate (progress `waiting for another download of <commit7>`), then finds `ready` and returns.
- **Open vs delete:** a delete decision (admin route, corruption, recovery) only ever moves the row to `deleting` under the catalog lock; the file is removed by the protocol, which needs zero readers. A reader that registered first keeps its open handle until it disposes; a reader arriving after `deleting` gets `source-not-cached`. No registered reader's file is ever removed under it.
- **Replacement vs old readers:** a new download of the same commit needs the gate and step 1 of §1.3, which requires the `deleting` row to reach `failed`, which requires zero readers. Nothing is written to `<commit>.zip` while any reader holds it.
- **Ensure-pin vs delete:** the pin is written in the ensure route's admission transaction (§2.2). An admin delete between the pin write and the job is refused as `pinned` unless forced; a forced delete makes the job re-download (it takes the gate after the deleter released it). No served deletion, no missing item.
- **Fetch vs ensure:** `OpenAsync` never takes a download gate, so serving commit A never waits for a download of commit B, and a re-ensure of a `ready` A returns from the fast path under the catalog lock.

### 1.8 Startup recovery

`SourceRecovery` runs from `MediaCleanupService.StartAsync` next to media recovery, before any route can serve: every `downloading` row → `deleting` (`error = interrupted`; this covers a crash between the rename and the `ready` publication: the `.zip` is discarded and the next ensure downloads again); every `ready` row whose `.zip` is missing → `deleting` (`error = missing`); then the removal protocol runs for every `deleting` row (no readers exist at startup; an I/O failure leaves the row `deleting` for the daily job). The engine's own startup pass marks the interrupted `source-fetch` job failed. Audit `source.recover` when anything changed (`detail = interrupted=<n>, missing=<n>, cleaned=<n>, pending=<n>`).

## 2. API contract

Every path has the `/api/v1` prefix. Errors are RFC 7807 problem documents from `CodedProblems.Create` (codes are lowercase kebab-case like every existing code; JSON fields are camelCase). All mutating routes carry `.Audited(...)`.

### 2.1 Routes

| Method, path | Auth (policy) | Request | Response | Errors |
|---|---|---|---|---|
| `POST /vms/{name}/source` | `vm-owner-or-admin` (user credential; **VM tokens refused**) | `{ commit: "<40 hex>" }`; optional `X-Construct-Operation-Key` | `200 { commit, state: "ready", sizeBytes, sha256, releaseTag }` when the item is ready; else `202 { jobId, commit, state: "downloading" }` + `Location: /api/v1/jobs/{id}`. A replay re-emits the **complete original body** with `replayed: true` and HTTP 200 (`LifecycleEndpoints.Replay` always answers 200); the client distinguishes the two by `state` (§4.7). | `400 validation` (commit), `404` unknown VM, `403` foreign VM, `409 vm-deleting`, `409 not-a-primary`, `409 unsupported-capability` (disabled), `409 operation-key-conflict`, `409 job-start-failed`, `503 maintenance` + `Retry-After: 30` |
| `GET /vms/{name}/source/{commit}` | `vm-scoped` gate, then `vm-self-or-owner-or-admin` resource check (`ApiHelpers.ResolveVmAsync`), exactly as `guest-report` | — | `200`, `Content-Type: application/zip`, `Content-Length`, `ETag: "<sha256>"`, `X-Construct-Source-Commit`, `X-Construct-Source-Sha256`, `Content-Disposition: attachment; filename=construct-source-<commit>.zip`, body streamed from the file | `400 validation`, `404` unknown VM, `403` any other VM's token, `404 source-not-cached` (no row, `failed` or `deleting`; body carries `error` when present), `409 source-downloading` + `Retry-After: 5`, `409 source-corrupt`, `409 vm-deleting`, `409 not-a-primary`, `409 unsupported-capability` |
| `GET /host/source-cache` | `admin` | — | `200 { items: [{ commit, state, sizeBytes, sha256, releaseTag, error, created, readyAt, lastUsedAt, pinnedBy: ["vm"…], readers }], committedBytes, maxTotalBytes, maxItemBytes, enabled }` — no host paths | — |
| `POST /host/source-cache/cleanup` | `admin` | `{}` | `202 { jobId }` (`source-cleanup`) | `503 maintenance` |
| `DELETE /host/source-cache/{commit}?force=` | `admin` | — | `202 { jobId }`: the route moves the row to `deleting` (`error = admin`) under the catalog lock and starts a `source-cleanup` job scoped to that commit, which runs the removal protocol | `404 source-not-cached` (no row or `failed`), `409 source-in-use` (`downloading` with a live job), `409 source-pinned` (pinned and `force` not `true`) |

Job kinds added: `source-fetch` (VM-scoped, listed by `GET /jobs?vm=`), `source-cleanup` (host-wide, owner `system` or the admin who requested it). Progress lines of `source-fetch`: `check`, `download`, `verify`, `ready`, plus `waiting for another download of <commit7>` when gated. `phase` is not used. `GET /vms/{name}` gains `sourceCommit` (nullable, omitted when null). `GET /health` `apiFeatures` gains `source-cache` (§5). `RouteCoverageTests.ExpectedRoutes` gains the five routes; `AuditCoverageTests` enforces `Audited` on the three mutations.

### 2.2 Ensure handler: admission, operation keys, replay (decided here, C21)

`SourceEndpoints.EnsureAsync`, in this order (the shape of `MediaEndpoints.AcquireAsync` and `PrimaryVmAdmission.CreateAsync`):

1. Lower-case and validate `commit` (`^[0-9a-f]{40}$`), else `400 validation` (`field = commit`). Resolve the VM with `Policies.VmOwnerOrAdmin`; refuse `vm-deleting`, `not-a-primary`, and `unsupported-capability` when disabled.
2. **Operation key (before anything is touched).** `supplied = X-Construct-Operation-Key` (optional; `OperationFingerprint.ValidKey`, else `400 validation`). `kind = "source-ensure"`, `owner = vm.Owner`, `target = vm.Name`, `fingerprint = OperationFingerprint.Compute(http.Request.Path, <canonical body>)` exactly as `PrimaryVmAdmission` does, so the actual path `/api/v1/vms/<name>/source` and the commit are both in it. A prior record with the same fingerprint and `Ownership.SameName(prior.Target, vm.Name)` → `LifecycleEndpoints.Replay(prior)` without touching the cache or the VM; a prior record with a different fingerprint or target → `409 operation-key-conflict`. The same two outcomes are honoured again inside `AdmitAsync` (`AdmissionOutcome.Replay` → `Replay(result.ExistingKey)`, `KeyConflict` → `operation-key-conflict`).
3. **Maintenance.** `handle = IMaintenanceGate.TryEnter("source-fetch", id, vm.Name)`; `null` → `MaintenanceFilter.RefusedAsync(http)` (`503 maintenance`). The handle is wrapped with `IOperationRegistry.Register(id, "source-fetch", vm.Name)` and transferred to the job at step 6; the route disposes it itself when no job is started.
4. **Ready fast path (no inventory I/O, admission outside the catalog lock).** Take the catalog lock; if the row is `ready`, copy its metadata (`sizeBytes`, `sha256`, `releaseTag`), touch `last_used_at`, and **release the lock**. Then, with no source lock held, build `response200 = { commit, state: "ready", sizeBytes, sha256, releaseTag }` and call `AdmitAsync(new AdmissionPlan(OperationKey: key, VmToInsert: null, Allowance: null, [], [], [], Reservation: null, CascadeToAccept: null, JobToInsert: null, VmToFence: null, FenceJobId: null, CloseChildCreation: false, VmSourceCommit: (vm.Name, commit)))`, where `key` is `null` when no header was supplied, else the **Completed** `OperationKeyRecord(owner, kind, supplied, fingerprint, target, JobId: null, State: Completed, IntentJson: null, PowerGeneration: null, ResponseJson: <response200 as JSON>, Created: now)`. Because `plan.Reservation is null`, `SqliteAdmissionStore.AdmitAsync` opens `ledger.BeginMutationAsync` (`SqliteAdmissionStore.cs:25`), the database-only transaction that never calls `inventory.ReadAsync`; `MutateAsync` is **not** used anywhere in this feature because it opens `ledger.BeginAsync`, which reads host inventory in enforce mode. The additive `AdmissionPlan.VmSourceCommit` (see step 5) is executed in that same transaction as `UPDATE vms SET source_commit=@c WHERE name=@n AND deleting=0`; 0 rows → `AdmissionOutcome.VersionConflict` → `409 vm-deleting`; `Replay`/`KeyConflict` → as in step 2. Dispose the handle, answer `200 response200`. The answer is a **snapshot**: an admin delete between the lock release and the guest's fetch makes the fetch answer `404 source-not-cached`, which the client handles as `guest-fetch-failed:3` → upload (§4.8); the ensure never re-validates under the lock, so a warm cache is never blocked behind the ledger.
5. **Queued path.** `response202 = { jobId: id, commit, state: "downloading" }`. Build `job = Job(id, "source-fetch", vm.Name, vm.Owner, Queued, [], null, null, now, null, Initiator = http.User.Actor(), OperationKey = supplied)` and the key record as in step 4 but with `JobId = id` and `ResponseJson = <response202 as JSON>`; call `AdmitAsync(new AdmissionPlan(OperationKey: key, VmToInsert: null, Allowance: null, [], [], [], Reservation: null, CascadeToAccept: null, JobToInsert: job, VmToFence: null, FenceJobId: null, CloseChildCreation: false, VmSourceCommit: (vm.Name, commit)))`. **Additive seam:** `AdmissionPlan.VmSourceCommit: (string VmName, string Commit)? = null`, executed by `SqliteAdmissionStore`/`InMemoryAdmissionStore` in the same transaction as `UPDATE vms SET source_commit=@c WHERE name=@n AND deleting=0`; 0 rows affected → `AdmissionOutcome.VersionConflict` → `409 vm-deleting`. No capacity reservation line (§1.6), so this too runs on `BeginMutationAsync` without inventory I/O; the row lookup that chose this path was done under the catalog lock and the lock was released before `AdmitAsync`.
6. `IPersistedJobRunner.StartPersistedAsync(job, handle, work)` where `work = ct => cache.EnsureAsync(commit, progress, ct)` returning `{ commit, sizeBytes, sha256, releaseTag }`. The stored key response is **immutable**: the worker never rewrites it (a replay of a queued ensure always says `downloading` with the same `jobId`, and the client learns the outcome from the job). On a start exception: `admission.MarkStartFailedAsync(job.Id, "job-start-failed")`, dispose the handle, answer `409 job-start-failed` (the key stays completed with the 202 body; a replay hands back a `jobId` whose job reads `failed`, which the client handles like any failed job).
7. Audit detail `commit=<40>, state=ready|downloading, job=<id>` (`CodedProblems.Audit(http, "vm.source.ensure", vm.Owner, vm.Parent, vm.Name, …)`).

Crash handling: a job left `Queued`/`Running` is failed by the engine's startup pass ("interrupted by a service restart"); its row is recovered by §1.8. VM deletion after admission: the pin disappears with the VM; the job continues and its result is simply unused. `source-fetch` is **not** added to `InProcessJobEngine`'s `SubmitAsync` admission list (that list serves `SubmitAsync` callers; this job is started with a pre-acquired handle like `media-acquire`, which is not in that list either). `source-cleanup` uses `SubmitAsync` with a `TryEnter("source-cleanup", …)` handle exactly like `media-cleanup`.

### 2.3 Fetch handler

`SourceEndpoints.FetchAsync`: validate the commit, resolve with `Policies.VmSelfOrOwnerOrAdmin`, refuse `vm-deleting`/`not-a-primary`/`unsupported-capability` like `guest-report`, then `OpenAsync` (§1.4) and stream. The VM-token path is the guest; the owner/admin path exists for diagnostics (`curl` from the PC). A VM token is accepted for **its own VM's** route only; §6.1 lists what it cannot do. The handler writes the `vm.source.fetch` audit entry itself (C18) with the outcome (`success`, `denied` for 403, `failure` with the code otherwise).

## 3. How the guest gets it

### 3.1 Decision (C1) (decided here)

**The guest pulls from a guest-authorised route with its VM token.** The alternative, the service pushing the zip over SSH, would require the service to hold an SSH credential for every guest, which it deliberately does not have today (the split in [Remote host §1](../remote-host.md) exists so that user secrets never transit the service and the service never logs into guests), would add an SSH client and host-key handling to `constructd`, and would need the guest's SSH port reachable from the host through a route the forward manager does not model. The pull needs nothing new on the guest side: the guest already carries the service URL, the pinned CA (`/etc/construct/service-ca.pem`) and its token (`/etc/construct/vm-token`), and already makes authenticated calls with `curl -H @file` from `construct-expose.sh` and the idle reporter. A range/resume protocol is not required; a failed fetch is retried once by the script and then the client falls back to the upload.

### 3.2 Route, auth, errors, audit

Route `GET /api/v1/vms/{name}/source/{commit}` as in §2.1. Auth: the VM's own token (`Authorization: VmToken <secret>`), or a user credential of the owner or an admin. Any other VM's token: `403`. Errors as in §2.1. Audit: every fetch writes `vm.source.fetch` (actor `vm:<name>` or the user, target the VM, detail `commit=<40hex>, bytes=<n>, outcome=…`).

### 3.3 Guest script: `bin/fetch-construct-source.sh` (new)

Runs as root (the client wraps it in its sudo/root invocation exactly like `provision.sh`). It is **streamed to the guest over ssh stdin by the client** (base64, decoded to a temp file and executed — the mechanism `$provisionSshInvoker` already uses for config-sync, `Provision-AgentVM.ps1:2013-2093`), because the script cannot be read from `/opt/construct/repo` when that is what is being replaced. The copy that ends up in `/opt/construct/repo/bin/` is for reference and tests; it is never executed from there by the provisioner.

Environment contract (all values arrive through `env` in the remote command, none is a secret):

| Variable | Required | Meaning |
|---|---|---|
| `CONSTRUCT_SERVICE_URL` | yes | Base URL, as `provision.sh` receives it. |
| `CONSTRUCT_INSTANCE_NAME` | yes | `{name}` of the route. |
| `CONSTRUCT_SOURCE_COMMIT` | yes | 40 hex. |
| `CONSTRUCT_SOURCE_SHA256` | yes | 64 hex, from the ensure result. |
| `CONSTRUCT_SOURCE_SIZE` | yes | Byte count, from the ensure result. |
| `CONSTRUCT_SOURCE_OVERLAY` | no | Temporary ZIP path; unset means no overlay. |
| `CONSTRUCT_SOURCE_OVERLAY_SHA256` / `CONSTRUCT_SOURCE_OVERLAY_SIZE` | with overlay | SHA-256 and compressed byte count of the uploaded ZIP. |
| `CONSTRUCT_SEED_USER` | yes | `chown` target (the same `$SeedUser` the tar path uses). |
| `CONSTRUCT_VM_TOKEN_FILE` | no | Default `/etc/construct/vm-token` (reprovision). The first provision points it at the temp file of §4.6. |
| `CONSTRUCT_SERVICE_CA_FILE` | no | Default `/etc/construct/service-ca.pem` when it exists. |
| `CONSTRUCT_SERVICE_CA_B64` | no | First provision: the PEM the client already passes to `provision.sh`; written to a temp file and used as `--cacert`. |
| `REPO_DIR` | no | Default `/opt/construct/repo`. Tests point it elsewhere. Its parent must exist or be creatable; the work directory is created **next to it** (C11). |
| `CONSTRUCT_SOURCE_MAX_BYTES` | no | Default `268435456`; refuses a larger `CONSTRUCT_SOURCE_SIZE` before downloading. |
| `CONSTRUCT_SOURCE_TIMEOUT_SEC` | no | curl `--max-time`, default `600`. |
| `CONSTRUCT_CURL` | no | Test seam, as in `construct-expose.sh`. |

Steps and exit codes (`set -euo pipefail`; a `trap` removes the work directory on every exit and completes the restore of step 6 when the swap was left half done):

| Step | Failure exit |
|---|---|
| 1. Validate every input (`^[0-9a-f]{40}$`, `^[0-9a-f]{64}$`, integer size ≤ max, seed user exists, token file readable, `python3`, `curl`, `sha256sum` present). | `2` |
| 2. `parent=$(dirname "$REPO_DIR")`; `mkdir -p "$parent"`; `work=$(mktemp -d "$parent/.repo-fetch.XXXXXX")` (same filesystem as the destination, so step 6 is a rename, never a copy); write `Authorization: VmToken <token>` into `$work/headers` under `umask 077`; `curl --silent --show-error --fail-with-body --max-time … --max-filesize $CONSTRUCT_SOURCE_SIZE -H @$work/headers -H 'Accept: application/zip' [--cacert …] -o $work/source.zip -w '%{http_code}' <url>`; one retry after 3 s on a transport failure or `409`. The token is never on a command line and never printed. | `3` (prints the HTTP status and the problem `code` when the body is JSON; never the body verbatim) |
| 3. `stat -c %s` equals `CONSTRUCT_SOURCE_SIZE` and `sha256sum` equals `CONSTRUCT_SOURCE_SHA256`. | `4` |
| 4. Extract with `python3` (§3.5) into `$work/repo`, then validate and apply any overlay (§4.6.1) before writing `.construct-revision`. | `4` for overlay size/hash mismatch, `5` for extraction/application refusal |
| 5. `chown -R "$CONSTRUCT_SEED_USER:$CONSTRUCT_SEED_USER" "$work/repo"`, and `chown "$CONSTRUCT_SEED_USER:$CONSTRUCT_SEED_USER" "$parent"` (the tar path's `chown -R /opt/construct` reaches the parent too). Both **before** the swap, so every fallible operation the result needs has succeeded before the old tree is touched. | `6` (old repo untouched) |
| 6. Swap, all renames on one filesystem: `old="$parent/.repo-old.$$"`; (a) if `$REPO_DIR` exists, `mv "$REPO_DIR" "$old"` — failure → exit `6`, old tree untouched; (b) `mv "$work/repo" "$REPO_DIR"` — failure → `mv "$old" "$REPO_DIR"` (restore) → exit `6`; if the restore itself fails → exit `7` (`repo-lost`); (c) `rm -rf "$old"` — **non-fatal**: a failure prints `warning: previous repo left at <old>` and the run still succeeds (the next run removes leftover `.repo-old.*` directories before step 2). On a fresh install there is no (a) and no (c). Nothing fallible that the result needs runs after (b). | `6` / `7` |
| 7. Print `CONSTRUCT_SOURCE_INSTALLED=<commit>` and `CONSTRUCT_SOURCE_BYTES=<n>`, plus overlay written/deleted counts when applicable; remove `$work` and any overlay under `/tmp`. | `0` |

Guarantee: **exit codes 2–6 leave the previous `/opt/construct/repo` in place** (steps 1–5 never touch it; step 6 restores it). Exit `7` means the restore failed and the repo is missing; the client's response is in §4.8. The script never touches `/etc/construct/config.env`, never writes the token anywhere, and never runs anything from the fetched tree. The local tar path (`Provision-AgentVM.ps1:1816`) is not changed by any of this.

### 3.4 Guest verification (C10)

Size and SHA-256 are compared against `CONSTRUCT_SOURCE_SIZE`/`CONSTRUCT_SOURCE_SHA256`, which the client obtained from the ensure response, i.e. from the service's own verification of the GitHub manifest, over the PC's pinned TLS session. The response headers `X-Construct-Source-Sha256`/`ETag` are informational and are not trusted for the decision.

### 3.5 Extraction rules (C11) (decided here)

What a real source zip looks like (*measured* on `git archive --format=zip --prefix=The-Construct-main/ HEAD` at `253f3ce`; the reviewer validated all 1,151 entries against these rules): 1151 entries = 119 directory entries (names end in `/`, including the root entry `The-Construct-main/`, recorded mode 0) + 1032 file entries, of which 37 carry mode `100755` and 995 carry **no mode at all** (`external_attr = 0`); no symlinks; `.construct-revision` holds the commit. §9.3 validates the rules against a freshly generated archive, not only against synthetic bad zips.

`SourceZipRules` (one definition, implemented twice: C# in `Constructd.Core.Logic` for the service-side check of §6.2, python in the guest script's heredoc):

1. At most 20 000 entries; sum of `file_size` ≤ 16 × the zip size and ≤ 1 GiB.
2. Entry kind: a name ending in `/` is a directory entry; otherwise a file entry. The type bits of `external_attr >> 16`, when non-zero, must agree (`S_IFDIR` / `S_IFREG`); `S_IFLNK` or any other type → refuse.
3. Name normalisation: strip exactly one trailing `/` from a directory entry; the remainder must pass `ZipEntryRules.IsSafe` (no `\`, no leading `/`, no empty, `.` or `..` component, no control characters, ≤ 240 chars). The root entry normalises to `The-Construct-main` and passes.
4. Exactly one top-level name across all entries, matching `^[A-Za-z0-9_.-]+-main$`; every other entry starts with `<root>/`. The root is stripped so the result has the tar path's prefix-less layout.
5. The destination of every entry, after `os.path.normpath`, must stay inside the extraction directory (belt and braces after rule 3).
6. Modes: file permission bits `0` → `0644`; otherwise `bits & 0o777` (git yields `0755` for executables); directory → `0755`. So `bin/*.sh`, `bootstrap.sh`, `bin/construct`, `console-viewer/install.sh` come out executable; `provision.sh:579` still runs its `chmod +x` afterwards, so downstream sees the same bits as today.

Resulting layout differences from the tar path, all intentional and none visible to `provision.sh`:

| Item | Tar path (today) | Cache path |
|---|---|---|
| Ignored files of the PC checkout (`.gitignore`: `runtime/`, `.env`, `*.local`, `*.iso`, `.construct-tools/`, `.construct-settings.json`, `.construct-backup/`, `__pycache__/`, `.claude/worktrees/`) | shipped except the four the tar excludes | absent. No guest script reads them (*measured*: `bin/*.sh`, `bootstrap.sh`, `console-viewer/install.sh`, `extension/vm/*` reference only tracked paths; `runtime/generated.json` lives under `/opt/construct/runtime`, not the repo). |
| Untracked, non-ignored files and local modifications of tracked files | shipped | never in `auto` (C14/C15 force the upload); knowingly omitted with `-SourceMode cache`. |
| `.construct-revision` | literal `$Format:%H$` (a checkout) or the commit (an archive install) | the commit. |
| `.git` | absent unless `-IncludeGit` | absent (`-IncludeGit` forces the upload). |
| Ownership and mode | `chown -R <seed>:<seed> /opt/construct`; modes as tar.exe recorded them plus `provision.sh`'s `chmod +x` | `chown -R` of the new tree plus the parent; git modes plus the same `chmod +x`. |

## 4. The client (`Provision-AgentVM.ps1`, service-managed instance)

### 4.1 The config bundle: what is in the tar that is not source, and what travels anyway

The tar packs the whole checkout minus `.git`, `*.iso`, `.construct-settings.json` and `.construct-backup` (`New-RepoArchive`, `Provision-AgentVM.ps1:1145-1160`). Its non-source content and how each item already reaches the guest:

| In the tar today | Is it configuration? | How it reaches the guest with the cache transport |
|---|---|---|
| Tracked source (`bin/`, `lib/`, `config/`, `systemd/`, `projects/default.json` + samples, `console-viewer/`, `extension/`, `docs/`, `service/`, `companion/`, `test/`, `drivers/`, `keys/`, `*.ps1`, `bootstrap.sh`, …) | no | the cache item (the release zip contains exactly the tracked tree). |
| `projects/*.json` user profiles, when someone still keeps them in the checkout | **no longer read from there**: `bin/generate-runtime-config.sh:87-133` resolves `default` from the shipped copy and every other profile **from `/opt/construct/projects` only** | the config-sync tick already writes the live profiles from `%LOCALAPPDATA%\The-Construct\config\projects` into `/opt/construct/projects/<name>.json` before `provision.sh` runs (`Invoke-ConstructConfigSync` → `Write-ConstructVmStore`, `Provision-AgentVM.ps1:1905-2185`). Unchanged. A profile kept only in the checkout's `projects/` shows up as an untracked/extra file and joins the overlay in Git mode. Archive mode retains its local-artifact exclusion for these profiles. |
| `.construct-tools/` (ISO builder), `runtime/`, `__pycache__/`, `.env`, `*.local` | host-only or generated | not sent; unused by the guest (§3.5). |
| `keys/bootstrap_ed25519{,.pub}` | tracked; used by the host, stripped from the guest's `authorized_keys` at the end | in the zip (tracked), unchanged. |
| `.construct-settings.json`, `.construct-backup/`, `*.iso`, `.git` | excluded today | excluded. |

Everything else the guest needs is already outside the tar and is **not changed by this contract**:

| Configuration | Channel (existing) | Reference |
|---|---|---|
| Per-VM settings: `AI_TOOLS`, `PROJECTS`, `SSH_USER`, `AGENT_NAME`, `CLAUDE_USER`, `GIT_USER_NAME_B64`, `GIT_USER_EMAIL_B64`, `GIT_CREDENTIAL_STORE`, `GIT_CLONE_CREDENTIALS_B64`, `CHECKOUT_PROJECTS`, `SETUP_ROOT_SSH_KEY`, `VSCODE_*`, `CONSTRUCT_VERSION`, `SMB_SHARE`, `CLAUDE_PARTIAL_STREAMING`, `MIC_PASSTHROUGH`, `OPENCODE_BACKGROUND_WATCHER`, `T3CODE*`, `CONSTRUCT_EXTERNAL_HOST/_SSH_PORT`, `CONSTRUCT_SERVICE_URL`, `CONSTRUCT_SERVICE_CA_B64`, `CONSTRUCT_INSTANCE_NAME` | `env` prefix of the `provision.sh` command | `Provision-AgentVM.ps1:2373-2376` |
| VM token (first provision, rotation) | ssh stdin → 0600 temp file → `CONSTRUCT_VM_TOKEN_B64` | `Send-GuestSecret`, `:2355-2372` |
| Project profiles | config-sync tick → `/opt/construct/projects` | `:1905-2185` |
| Saved agent config (reinstall) | scp of `construct-config-restore.tar.gz` + `restore-config.sh` | `:2412-2445` |
| Git identity, agent password, bootstrap-key removal | env prefix / final ssh session | `:2948-3021` |

So the "config bundle" is a name for what the provisioner already sends; the cache transport replaces the full tar with a released commit and, when needed, a temporary overlay ZIP. Configuration and token channels remain unchanged.

### 4.2 Decision logic (C12): `Get-ConstructSourceTransportPlan` (decided here)

New pure function in `lib/AgentVm.Remote.ps1` (Windows PowerShell 5.1 syntax, no side effects):

```
Get-ConstructSourceTransportPlan
  -ServiceManaged <bool>        # [bool]$ServiceUrl after registry resolution
  -Mode 'auto'|'cache'|'upload' # -SourceMode
  -IncludeGit <bool>
  -FeatureAvailable <bool>      # Test-ConstructApiFeature 'source-cache'
  -Ref <string>                 # settings.constructRef ('' = 'main')
  -Commit <string>              # Get-ConstructSourceIdentity.Commit
  -TreeState 'equivalent'|'divergent'|'unverified'|'unknown'
  -Divergence <int>             # count of differing files
  -Changes <hashtable or null>  # Modified, Added, Deleted arrays; plan carries Overlay
  -> @{ Transport = 'cache'|'upload'; Reason = <code>; Message = <text>; Commit = <40hex or ''> }
```

Decision matrix (first matching row wins; `Reason` is the code the tests assert):

| ServiceManaged | Mode | IncludeGit | Feature | Ref | Commit / tree | Transport | Reason |
|---|---|---|---|---|---|---|---|
| false | any | any | any | any | any | upload | `local-install` (no message: zero-change path) |
| true | `upload` | any | any | any | any | upload | `mode-upload` |
| true | any | true | any | any | any | upload | `include-git` |
| true | `auto` | false | false | any | any | upload | `service-without-source-cache` |
| true | `auto` | false | true | ≠ `main` | any | upload | `ref-not-main` |
| true | `auto` | false | true | `main` | commit not 40 hex or tree `unknown` | upload | `commit-unknown` |
| true | `auto` | false | true | `main` | tree `unverified` (archive without manifest) | upload | `archive-unverified` |
| true | `auto` | false | true | `main` | tree `divergent`, Changes unavailable | upload | `local-changes` |
| true | `auto` | false | true | `main` | tree `divergent`, > 5,000 changed files | upload | `local-changes-too-large` |
| true | `auto` | false | true | `main` | tree `divergent`, Changes available, ≤ 5,000 files | **cache + overlay** | `cache-overlay` |
| true | `auto` | false | true | `main` | 40 hex and tree `equivalent` | **cache** | `cache` |
| true | `cache` | false | false | any | any | **throw** | `service-without-source-cache` |
| true | `cache` | false | true | any | commit not 40 hex | **throw** | `commit-unknown` |
| true | `cache` | false | true | any | 40 hex (tree state and ref ignored: explicit override, the service decides availability) | **cache** | `cache-forced` |

### 4.3 Commit identity and tree equivalence (C13–C15): `Get-ConstructSourceIdentity -Root <scripts dir>` (decided here)

New in `lib/AgentVm.Common.ps1` (pure apart from reading the checkout; `git` is behind a `-GitRunner` scriptblock seam, the manifest directory behind `-ManifestDir`, both for tests):

| Checkout | Commit | TreeState | Divergence |
|---|---|---|---|
| `.git` present, `git` available, `git -C $Root rev-parse HEAD` is 40 hex, `git -C $Root status --porcelain --untracked-files=all --ignore-submodules=none` prints nothing (the explicit options override a user's `status.showUntrackedFiles`/`diff.ignoreSubmodules` settings; the repository has no submodules today, and a future one's dirtiness counts) | HEAD | `equivalent` | 0 |
| same, status prints n lines | HEAD | `divergent` | total changed files, or n when Changes unavailable |
| `.git` present, `git` missing or failing | `''` | `unknown` | – |
| no `.git`, `.construct-revision` is 40 hex, manifest `<ManifestDir>\<commit>.sha256` present, every listed file present with the listed hash, and no extra file in the tree (§4.4) | that value | `equivalent` | 0 |
| same, but any listed file missing/different or any extra file | that value | `divergent` | n |
| no `.git`, `.construct-revision` is 40 hex, no manifest | that value | `unverified` | – |
| otherwise | `''` | `unknown` | – |

`installedCommit` from `.construct-settings.json` is **not** used for the cache; it keeps feeding `CONSTRUCT_VERSION`, `provisionedCommit` and the guest report exactly as today (`Provision-AgentVM.ps1:2250-2262`, `3110-3128`, `2381-2391`). When the two differ (a developer checked out another commit without reinstalling), the guest runs the checkout's commit and the panel keeps recording `installedCommit`; that is today's behaviour with the tar as well.

### 4.4 The archive source manifest (C15) (decided here)

`install.ps1` and `Update-Construct.ps1` already download `construct-source-<commit>.zip`, verify `sourceSha256` and `Expand-Archive` it (`install.ps1:31-53`, `Update-Construct.ps1:43-64`). Both gain one step, through a shared helper `Write-ConstructSourceManifest -Zip <path> -Commit <40hex> -ManifestDir <dir>` in `lib/AgentVm.Common.ps1` (5.1: `System.IO.Compression.ZipFile` + `SHA256`): for every file entry of the zip (prefix stripped, directories skipped) write `<sha256 of the entry's uncompressed bytes>  <path with />` lines, sorted, to `<ManifestDir>\<commit>.sha256`, written to a temp file and renamed. `ManifestDir` = `Join-Path (Split-Path -Parent (Get-ConstructConfigDir)) 'source-manifests'`, i.e. `%LOCALAPPDATA%\The-Construct\source-manifests\`, **outside every checkout**: the local tar and the local guest tree contain no new file, and the file is per commit, not per checkout. Writing it is best-effort (`Write-Warning "Could not record the source manifest (…); the host cache is unavailable until the next update."`); a failure never fails the install or update.

Verification (`Get-ConstructSourceIdentity`, archive branch): read the manifest; for every line hash the file under `$Root` (missing or different → `divergent`); then enumerate `$Root` recursively, skipping the ignore set `.construct-settings.json`, `.construct-backup\`, `*.iso`, `.construct-tools\`, `runtime\`, `.env`, `*.local`, `__pycache__\`, `.claude\worktrees\` (the `.gitignore` of the tree), and count every file not in the manifest as extra (→ `divergent`). *Measured*: 1032 files, 15.6 MB (the counts); the time to hash them on a Windows PC is an **estimate** (expected well under a second) to be confirmed in the field test, not a measurement of this delivery. A manifest whose lines do not all match `^[0-9a-f]{64}  \S.*$` is treated as absent (`unverified`).

### 4.5 `-InstallSource` and explicit local builds (C25) (decided here)

There is no `-InstallSource` parameter in the repository today (the source-selection precedents are `Install-ConstructCompanion -Source auto|local|release` and the `-Repo`/`-Ref` pair). It is not introduced. The cases it would have named:

| Situation | What happens |
|---|---|
| Developer checkout on `main`, committed and clean | cache (`git rev-parse HEAD` is released once `main` published it; until then `source-unavailable` → upload with the message). |
| Developer checkout with local edits or untracked files | cache + overlay when the change set is available and within limits. Otherwise upload. |
| Checkout on a feature branch (`constructRef` ≠ `main`, or the commit is unreleased) | upload, reason `ref-not-main` / `ensure-failed:source-unavailable`. |
| Archive install with a manifest that matches | cache. |
| Archive install with hand edits or extra files | cache + overlay under the same limits, retaining the local-artifact exclusions. |
| Archive install without a manifest | upload, reason `archive-unverified`; the next `Update-Construct.ps1` writes the manifest. |
| `-IncludeGit` | upload (the guest is meant to receive `.git`). |
| `-SourceMode cache` | cache for HEAD without local changes or an overlay, regardless of tree state, or a hard error naming the reason (for scripted use). |

### 4.6 New flow in `Provision-AgentVM.ps1`

New parameters (after `-ServiceApiAuth`, 5.1 syntax):

```
[ValidateSet('auto','cache','upload')][string]$SourceMode = 'auto',
[int]$SourceEnsureTimeoutSec = 900
```

Two orchestration orders, selected by `[bool]$ServiceUrl` (the predicate the script already uses everywhere for service-managed behaviour). Every line not listed is unchanged.

**Local path (no `-ServiceUrl`)** — the existing order, no new helper is called at all:

1. `$archivePath = New-RepoArchive` (line 1744, unconditional in this branch: an unreachable local VM still packs first and prints `Packing repo …` exactly as today).
2. `Ensure-VmReachable` (line 1745).
3. Host-key acceptance, root-key fast path / bootstrap key / sudo checks (lines 1747-1807).
4. `Uploading repo archive …` / `Unpacking repo on the VM` (lines 1809-1817).
5. Everything after, unchanged.

The only textual change on this path is the guard around line 1744: `if (-not $ServiceUrl) { $archivePath = New-RepoArchive } else { … }`; the call itself, its position and its output do not move. `Get-ConstructSourceTransportPlan`, `Get-ConstructSourceIdentity`, `Test-ConstructApiFeature` and `Invoke-ConstructSourceTransport` are referenced only inside the `else`/`if ($ServiceUrl)` blocks below, so a local run never evaluates them.

**Remote path (`-ServiceUrl` set)**:

1. **Plan** (the `else` branch at line 1744, i.e. before reachability, no network call except the health probe): `$sourcePlan = Get-ConstructSourceTransportPlan …` with `-FeatureAvailable (Test-ConstructApiFeature … -TimeoutSec 10)` and the identity of §4.3. Nothing is packed yet.
2. `Ensure-VmReachable` (line 1745, unchanged). A remote VM that is unreachable fails here **before** any packing: on the remote path the archive is no longer built for a VM that cannot be reached (today it is built first and then discarded), which is the one observable difference and is deliberate.
3. **Begin** (new block right after line 1745): `$sourceState = Invoke-ConstructSourceTransport -Phase begin -Plan $sourcePlan …`. For a `cache` plan it generates the run's operation key (`'source-' + [guid]::NewGuid().ToString('n')`), issues the ensure **once**, prints `==> Ensuring Construct source <commit7> on the host service` and `    Source cached on the host (<n> KB)` or `    Host is fetching commit <commit7> from the release (job <id>)`, and stores the outcome. An outcome other than `ready`/`downloading` already turns `$sourceState.Transport` into `upload` here (§4.8, "ensure failure before SSH"). Then: `if ($sourceState.Transport -eq 'upload') { $archivePath = New-RepoArchive }` — the remote upload path packs here, after reachability and after the ensure decision, and prints the usual `Packing repo …`.
4. Host-key acceptance, root-key fast path / bootstrap key / sudo checks (lines 1747-1807, unchanged). The host's download overlaps with them.
5. **Complete** replaces the two steps `Uploading repo archive …` / `Unpacking repo on the VM` (lines 1809-1817) inside `if ($ServiceUrl) { … } else { <today's two steps verbatim> }`: `$sourceResult = Invoke-ConstructSourceTransport -Phase complete -State $sourceState …`. For `cache` it waits for the job (progress lines print as they do for creation), packs and uploads an overlay when the plan carries Changes (see §4.6.1), stages the token if needed (step 6), streams `bin/fetch-construct-source.sh` (read from `$PSScriptRoot\bin`) over stdin with the §3.3 environment, parses `CONSTRUCT_SOURCE_INSTALLED=`, prints `    Construct source: host cache (commit <commit7>, <n> KB); nothing uploaded from this PC.` For `upload` (planned, decided at begin, or fallen back inside `complete`) it runs the `-Upload` closure: `New-RepoArchive` when `$archivePath` is still empty, then today's `Uploading …`/`Unpacking …` steps verbatim.

6. **First provision** (C16): when `-VmTokenB64` is set, before the fetch the decoded token is written with `Send-GuestSecret` to `/tmp/.construct-vm-token-fetch.<guid>` (0600) and passed as `CONSTRUCT_VM_TOKEN_FILE`; the remote fetch command removes it on exit whatever the exit code, the way `$tokenCleanup` does. The later `provision.sh` token delivery (`:2355-2372`) is unchanged and still writes `/etc/construct/vm-token`. The CA rides as `CONSTRUCT_SERVICE_CA_B64` (already computed at `:2326-2340`; computed earlier when the cache path is taken).
7. **`-Action export`** keeps working: it needs the repo on the VM for `export-config.sh` and gets it through whichever transport was used.
8. **Everything after the repo is in place** (config-sync tick, restore, `provision.sh`, guest report, markers, host-side `~/.ssh`, VS Code) is untouched. `CONSTRUCT_VERSION` stays `installedCommit`; `Set-ConstructProvisionedMarker` and `Send-ConstructGuestReport` are called exactly as today.

`Auto-Install.ps1` and the panel/Companion reprovision commands need no change (default `auto`). `-SourceMode` is not threaded through `Auto-Install.ps1` in this delivery; a user who wants the upload runs `Provision-AgentVM.ps1 -InstanceName <name> -SourceMode upload`.

### 4.6.1 Source overlays (owner decision, 2026-09-12)

`Get-ConstructSourceIdentity` returns `Changes = @{ Modified = @(); Added = @(); Deleted = @() }`
with ordinal-sorted, case-preserved, forward-slash relative paths. Archive mode compares the
manifest hashes and lists missing/extra files using the existing local-artifact exclusions.
Git mode parses the existing porcelain invocation: `??` and `A` add, either-column `D` deletes,
other statuses modify; a rename deletes the old path and adds the new one. Paths present in both deletion and write lists keep their on-disk copy. Quoted or unparsable
paths, including embedded repositories reported as directories, make Changes null while Divergence still counts status lines. Git mode applies no extra
artifact exclusions.

After ensure/job success in `complete`, `-PackOverlay { param($changes) }` calls
`New-ConstructSourceOverlay -Root <checkout> -Changes <hashtable> -Path <zip>`. Modified and added
files use `construct-overlay/<relative path>` entries, Optimal compression. Optional root entry
`construct-overlay.deleted` contains UTF-8 without BOM, LF-terminated deletion paths. The packer
rejects invalid paths, reparse points in entries and parent directories inside the checkout, more than 5,000 changed files,
and more than 64 MiB uncompressed including the deletion list. It returns Path, Files, Deleted,
SizeBytes (uncompressed), CompressedSizeBytes, and Sha256 of the ZIP.

`-UploadOverlay { param($overlay) }` uploads to `/tmp/construct-source-overlay.<guid>.zip`, mode
0600, then deletes the local ZIP. Token staging and guest fetch follow in that order. The guest
receives `CONSTRUCT_SOURCE_OVERLAY`, `CONSTRUCT_SOURCE_OVERLAY_SHA256`, and
`CONSTRUCT_SOURCE_OVERLAY_SIZE` (compressed ZIP bytes). Client cleanup covers upload, staging,
and fetch failures; guest cleanup removes the overlay under `/tmp` on success and failure.
No content or credentials appear in diagnostics or command arguments.

After base extraction and before revision/chown/swap, the guest applies the overlay in staging.
Size/hash mismatches exit 4; ZIP or application violations print `source-fetch: overlay-refused`
and exit 5, preserving the previous repo. It uses the base ZIP's validation rules, with exactly
`construct-overlay` as the root and optional `construct-overlay.deleted`; at most 20,000 entries
and uncompressed data at most min(16 × compressed size, 1 GiB). Existing files retain base modes;
new `.sh` files or paths under `bin/` get 0755, other new files 0644. Deletions remove regular
files only, ignore missing files, and prune emptied parent directories. Successful output adds
`CONSTRUCT_SOURCE_OVERLAY_FILES=<written>` and `CONSTRUCT_SOURCE_OVERLAY_DELETED=<removed>`.

`cache-overlay` is an Info message, not a warning. Success reports
`    Construct source: host cache (commit <short>, N KB) + M differing file(s) (K KB) uploaded from this PC.`
Both displayed sizes are compressed. Forced `cache` sends only the commit, never an overlay;
`upload` and the local single-VM path retain their existing behavior. The service is unchanged.

### 4.7 Client library functions (C23, C24)

All in `lib/AgentVm.Remote.ps1`, 5.1 syntax, none throws on a service condition (they return outcomes); only programming errors throw.

**Structured error seam (C24, additive).** `Invoke-ConstructApi` keeps `Get-ConstructApiLastStatus`/`Get-ConstructApiLastError` exactly as today and additionally sets `$script:ConstructApiLastProblem`, read through the new `Get-ConstructApiLastProblem` → `@{ Status = <int>; Code = <RFC 7807 code or ''>; Class = <string>; Detail = <string> }`. `Code` is parsed from the problem body that `Get-ConstructApiErrorInfo` already extracts (its `Body` field; the accessor is extended with `Code`, existing fields untouched). `Class` is one of the allow-listed values `none` (2xx), `http` (a status came back), `pin` (fingerprint refused), `tls`, `dns`, `connection`, `timeout`, `other`, derived from the exception type/`WebExceptionStatus` (5.1) or `HttpRequestException`/`TaskCanceledException` (7), never from message text. Existing callers see no change.

| Function | Contract |
|---|---|
| `Request-ConstructSourceEnsure -BaseUrl -VmName -Commit -OperationKey -Auth -Pin -StoreDir [-TimeoutSec 30]` | `POST /vms/<name>/source { commit }` with header `X-Construct-Operation-Key: <OperationKey>` via `Invoke-ConstructApi -NoThrow`. Returns `@{ Outcome; Status; Code; Class; JobId; SizeBytes; Sha256; ReleaseTag; Replayed }` with `Outcome` ∈ `ready` (2xx body with `state = ready`, `sizeBytes` > 0, 64-hex `sha256`), `downloading` (2xx body with `state = downloading` and a `jobId` — whether 202 or a 200 replay), `unsupported` (404 route, or 409 `unsupported-capability`), `denied` (401/403), `maintenance` (503), `refused` (any other 4xx; `Code` = problem code), `malformed` (2xx whose body lacks the documented fields), `error` (`Class` ≠ `http`, or 5xx). Never throws. |
| `Wait-ConstructSourceJob -BaseUrl -JobId -Auth -Pin -StoreDir -Deadline <DateTime> [-PollSeconds 2] [-OnProgress]` | Polls `GET /jobs/<id>` with `-NoThrow`; **each poll's `-TimeoutSec` is `[Math]::Max(1, [Math]::Min(30, secondsUntilDeadline))`**, so no single poll outlives the overall deadline; prints new progress lines like `Wait-ConstructJob`. Returns `@{ State; Code; Class; SizeBytes; Sha256 }` with `State` ∈ `succeeded` (result validated as above; else `malformed`), `failed` (`Code` = `job.error`, a bare code per C22), `cancelled`, `timeout` (deadline passed), `denied` (401/403 on a poll), `error` (after 3 consecutive polls with `Class` ≠ `http` or 5xx). Never throws. |
| `Invoke-ConstructSourceTransport -Phase begin -Plan <plan> -Ensure <sb> -Warn <sb> -Info <sb>` | Issues the ensure once (for a `cache` plan) and returns the state object `@{ Transport; Reason; Commit; OperationKey; Ensure = <ensure result>; SizeBytes; Sha256 }`; a non-`ready`/`downloading` outcome maps through §4.8 to `Transport = upload` (or throws in `cache` mode). For an `upload` plan returns the plan as state. |
| `Invoke-ConstructSourceTransport -Phase complete -State <state> -WaitJob <sb> -StageToken <sb> -RunGuestFetch <sb> -Upload <sb> -Warn <sb> -Info <sb> -Deadline <DateTime>` | The rest of §4.6 step 4 and the whole §4.8 table, every side effect behind a seam. Returns `@{ Transport = 'cache'|'upload'; Reason; Commit }`, or throws in `cache` mode with the §4.9 sentence. `Provision-AgentVM.ps1` passes closures over the real functions; tests pass fakes and assert call counts (the `-Ensure` closure is never invoked in `complete`). |

### 4.8 Failure handling at the orchestration level (decided here)

Every event below is handled by `Invoke-ConstructSourceTransport`. "Upload" means: warn with the §4.9 message for the reason, then the legacy pack/upload/unpack through `-Upload`. In `-SourceMode cache` every "upload" row becomes a `throw` with the same sentence minus the fallback clause, except the two rows marked *stop*, which throw in both modes.

| Event (phase) | Reason code | `auto` | Notes |
|---|---|---|---|
| Changes unavailable (plan) | `local-changes` | upload | differing files could not be listed |
| More than 5,000 changes (plan/pack), or more than 64 MiB uncompressed (pack) | `local-changes-too-large` | upload | no guest fetch |
| Overlay packing failure (complete) | `overlay-pack-failed:<detail>` | upload | safe packer code (`invalid-overlay-path`, `overlay-reparse-point`, `overlay-not-file`, `overlay-file-missing`, `overlay-file-unreadable/<exception type>`, `duplicate-overlay-path`, `conflicting-overlay-path`, `empty-overlay-result`) or exception type, plus ` at <relative path>` of the file that failed; never file content |
| Overlay upload failure (complete) | `overlay-upload-failed` | upload | clean temporary ZIPs |
| Overlay size/hash or extraction/application failure | `guest-fetch-failed:4` / `guest-fetch-failed:5` | upload | previous repo intact |
| Feature probe false/timeout (plan) | `service-without-source-cache` | upload | no ensure attempted |
| Ensure `unsupported` (begin) | `service-without-source-cache` | upload | |
| Ensure `denied` 401/403 (begin) | `ensure-denied:<status>` | upload | proposed technical default T4 (§0.1), not an owner decision |
| Ensure `maintenance` 503 (begin) | `ensure-maintenance` | upload | no waiting for the update to finish |
| Ensure `refused` other 4xx, incl. `vm-deleting`, `not-a-primary`, `operation-key-conflict`, `job-start-failed` (begin) | `ensure-refused:<code>` | upload | |
| Ensure `malformed` or `error` (`pin`, `tls`, `dns`, `connection`, `timeout`, `other`, 5xx) (begin) | `ensure-failed:<code or class>` | upload | bounded by the 30 s request timeout |
| Job `failed` (complete) | `ensure-failed:<code>` (`source-unavailable`, `source-too-large`, `source-cache-full`, `source-cleanup-pending`, `insufficient-space`, `source-transfer-failed`, `source-hash-mismatch`, `extraction-refused`, `release-source-*`) | upload | |
| Job `cancelled` (complete) | `ensure-cancelled` | upload | an admin cancelled it; nothing to wait for |
| Job `timeout` (`-SourceEnsureTimeoutSec` deadline) (complete) | `ensure-timeout` | upload | the job keeps running on the host; the next reprovision finds the item ready |
| Job poll `denied` / `error` / `malformed` (complete) | `ensure-failed:<code or class>` | upload | `error` after 3 consecutive failed polls |
| Token staging failed (`Send-GuestSecret` returned `$false`, first provision only) (complete) | `guest-token-staging-failed` | upload | the later `provision.sh` token delivery is a separate attempt and still runs |
| Guest script could not be streamed/started (ssh exit ≠ 0 before the script printed anything) (complete) | `guest-fetch-failed:ssh` | upload | |
| Guest script exit 2–6 (complete) | `guest-fetch-failed:<exit>` | upload | previous repo guaranteed in place (§3.3) |
| Guest script exit 7 (`repo-lost`) (complete) | `guest-fetch-failed:repo-lost` | upload | *stop* in `cache` mode with the instruction to rerun with `-SourceMode upload`; in `auto` the upload restores the repo because it replaces it wholesale |
| Guest script exit 0 without a `CONSTRUCT_SOURCE_INSTALLED=<commit>` line, or with a different commit (complete) | `guest-fetch-failed:unverified` | upload | treated as a failure although the repo is probably fine; the upload is always safe |
| Legacy upload fails after a fallback | – | *stop* | today's error (`throw "SCP upload failed …"` / `tar failed …`), unchanged |

**Worst-case added latency before the run proceeds as today**, by when the host becomes unreachable: before the plan, the health probe (10 s); during `begin`, the ensure request (30 s); after a `202`, three consecutive failed polls of at most 30 s each plus two 2 s sleeps (≤ 94 s); never the full `-SourceEnsureTimeoutSec`, which only bounds a host that keeps answering while its download runs. No row waits for the service to come back.

### 4.9 Messages the client prints (exact text)

Cache path, success: `    Construct source: host cache (commit <commit7>, <n> KB); nothing uploaded from this PC.`

Fallbacks, all `Write-Warning`, then the ordinary `==> Packing repo …` output:

| Reason | Message |
|---|---|
| `service-without-source-cache` | `This host service does not offer the source cache (apiFeatures lack "source-cache"); uploading the Construct checkout as before. Update the host service to skip the upload.` |
| `ref-not-main` | `Construct source ref '<ref>' is not main, so no host release exists for it; uploading the checkout as before.` |
| `commit-unknown` | `Could not determine this checkout's commit (no git or no .construct-revision); uploading the checkout as before.` |
| `archive-unverified` | `This Construct install has no source manifest (it was installed before the host cache existed); uploading the checkout as before. Run Update-Construct.ps1 once to enable the host cache.` |
| `cache-overlay` (Info) | `This checkout differs from commit <short> in N file(s); the VM fetches commit <short> from the host cache and only those file(s) are uploaded from this PC.` |
| `local-changes-too-large` | `This checkout differs from commit <short> in N file(s), too many or too large for an overlay; uploading it as before…` |
| `overlay-pack-failed:<detail>` | `Could not pack the N differing file(s) (<detail>); uploading the checkout as before.` |
| `overlay-upload-failed` | `Could not upload the source overlay to the VM; uploading the checkout as before.` |
| `local-changes` | `This checkout differs from commit <commit7> in <n> file(s) (the differing files could not be listed); uploading it as before so the VM gets them. Commit or ignore them to use the host cache, or pass -SourceMode cache to send commit <commit7> without them.` |
| `include-git` | (none; `-IncludeGit` is explicit) |
| `ensure-denied:<status>` | `The host service refused the source request (HTTP <status>); uploading the checkout as before.` |
| `ensure-maintenance` | `The host service is in maintenance; uploading the checkout as before.` |
| `ensure-refused:<code>`, `ensure-failed:<code>` | `The host service could not cache commit <commit7> (<code>); uploading the checkout as before.` (for `source-cache-full`: `… (source-cache-full: ask the host admin to delete unused entries); uploading …`) |
| `ensure-cancelled` | `The host service's source download was cancelled; uploading the checkout as before.` |
| `ensure-timeout` | `The host service did not finish caching commit <commit7> within <n> s; uploading the checkout as before (the host keeps downloading for the next run).` |
| `guest-token-staging-failed` | `Could not hand the VM its service token for the source fetch; uploading the checkout as before.` |
| `guest-fetch-failed:<exit|ssh|unverified>` | `The VM could not fetch commit <commit7> from the host service (<detail>); uploading the checkout as before.` |
| `guest-fetch-failed:repo-lost` | `The VM's Construct repo was lost while swapping in commit <commit7>; uploading the checkout to restore it.` (in `cache` mode: `… Rerun with -SourceMode upload to restore it.`) |

The local path prints nothing new.

## 5. Feature flag and compatibility

| Component | Change |
|---|---|
| `service/src/Constructd.Api/Composition/ReleaseInfo.cs:12`, `service/src/Constructd.Fakes/FakeReleaseInfo.cs:10` | `apiFeatures` gains `"source-cache"` (appended; omitted when `HostAdmin:Source:Enabled=false`, which makes `ApiFeatures` a computed list on `ReleaseInfo` taking the options). |
| `HostAdminApiTests.cs:36-39` | asserts the new name is present and the list still equals `ReleaseInfo().ApiFeatures`. |
| Older **service** (no `source-cache`, no routes) + new client | probe false → upload with the `service-without-source-cache` warning. A service that advertises the feature but answers `404`/`409 unsupported-capability` on ensure is treated the same (§4.8). |
| Newer service + older client | the client never probes and never calls the routes; uploads as today. The cache stays empty until a new client provisions. |
| Older **guest** (provisioned by an old client) + new client | the guest needs nothing pre-installed: the fetch script arrives over stdin and uses `curl`, `sha256sum`, `python3`, all present on the autoinstall image (`python3` is required by `bin/install-t3code-prebuilt.py`; `curl` by every guest script). A guest missing one of them exits `2` → upload fallback. |
| Archive installs made before this change | no manifest → `archive-unverified` → upload (today's behaviour) until the next `Update-Construct.ps1`. |
| Extension (`extension/src/hostadmin.js`), Companion (`HostAdminProtocol.cs`) | unchanged; `featureSet`/`Features` ignore unknown names. The panel does not gate anything on `source-cache`. |
| `Auto-Install.ps1` local path, `Create-AgentVM.ps1` | unchanged. `ServiceManaged=$false` short-circuits the plan; no probe, no new output. `install.ps1`/`Update-Construct.ps1` write the manifest outside the checkout, so the packed tar is identical. |
| `docs/remote-host.md` "Reprovision … Never touches the service" (line 358) | becomes "makes one source-cache request to the service and otherwise never touches it". |

## 6. Security

### 6.1 What a VM token can and cannot do

| Call | VM token of that VM | Any other VM token | Owner / admin user |
|---|---|---|---|
| `GET /vms/{name}/source/{commit}` | allowed (own VM only) | `403` | allowed |
| `POST /vms/{name}/source` (ensure, triggers a GitHub download) | `403` | `403` | allowed |
| `GET /host/source-cache`, `cleanup`, `DELETE` | `403` | `403` | admin only |
| `GET /jobs/{id}` of a `source-fetch` job | `403` (initiator is the user) | `403` | initiator/owner/admin |

A VM token therefore learns nothing it did not already know (the commit is in its own environment) and cannot enumerate the cache, other VMs, users or jobs. The served bytes are public source from a public GitHub release; the values protected are host disk and outbound bandwidth: only a user credential can trigger a download, every ensure is audited, `MaxItemBytes` bounds one item, `MaxTotalBytes` bounds the cache, and nothing is ever downloaded twice unless an admin deletes it (§1.6).

### 6.2 Sizes and extraction

Service side: manifest ≤ 1 MiB (existing), `sourceSizeBytes ≤ MaxItemBytes`, `MaxTotalBytes` admission, free-space check, download refused beyond the declared size (existing `DownloadAsync` check), and the structural check `SourceZipRules` (§3.5 rules 1–5) after the hash. The service never extracts the zip. Guest side: `--max-filesize`, the size/hash check of §3.4, and `SourceZipRules` again (with rule 6 for modes), into a fresh directory on the destination filesystem that replaces `/opt/construct/repo` only after everything passed.

### 6.3 Secrets

Nothing new is transported. The bundle of §4.1 is the existing set of channels; the only credential involved in the fetch is the guest's own VM token, read from a 0600 file into a curl header file (never argv, never output). The client never receives the token back. The ensure request carries no secret (a commit hash and an operation key that is a random GUID). `Job.Result` of `source-fetch` carries no secret. The cache directory holds public source only; the source manifest on the PC holds hashes of public source. `SafeError.Describe` remains the only thing that reaches logs, audit and problem bodies; `SourceException` messages are bare codes by construction.

### 6.4 Audit entries

| Action | Actor | Target | Detail | When |
|---|---|---|---|---|
| `vm.source.ensure` | user | VM name | `commit=<40>, state=ready|downloading, job=<id>` | every `POST /vms/{name}/source`, any outcome (middleware) |
| `vm.source.fetch` | `vm:<name>` or user | VM name | `commit=<40>, bytes=<n>, outcome=success|denied|failure, code=<…>` | every `GET /vms/{name}/source/{commit}` (handler-written, C18) |
| `source.fetch.completed` / `source.fetch.failed` | `system` | commit | `job=<id>, bytes=<n>` / `code=<…>` | job end |
| `source.corrupt` | `system` | commit | `reason=size|hash|missing` | §1.4, §1.8 |
| `source.removed` | `system` | commit | `reason=<error>, bytes=<n>` | the removal protocol reaches `failed` |
| `source.recover` | `system` | — | `interrupted=<n>, missing=<n>, cleaned=<n>, pending=<n>` | startup |
| `host.source.cleanup` | admin | — | `removed=<n>, retained=<n>` | `POST /host/source-cache/cleanup` (middleware) and the daily sweep (actor `system`) |
| `host.source.delete` | admin | commit | `force=<bool>, outcome` | `DELETE /host/source-cache/{commit}` |

## 7. Persistence, jobs and seams (implementation ownership)

| Area | Files (new unless marked) | Notes |
|---|---|---|
| Core | `Domain/SourceItem.cs` (+ `SourceException`), `Abstractions/ISourceStore.cs`, `Abstractions/ISourceFiles.cs`, `Abstractions/IReleaseSource.cs` (existing, + `GetSourceAssetAsync`, + `SourceAssetDescriptor`), `Abstractions/IAdmissionStore.cs` (existing, + `AdmissionPlan.VmSourceCommit` only; no scope method, `MutateAsync` unused), `Configuration/HostAdminDefaults.cs` (existing, + `HostAdminSourceOptions`), `Logic/SourceZipRules.cs`, `Services/SourceGate.cs`, `Domain/Vm.cs` (existing, + `SourceCommit`) | zero packages, as always. |
| Sqlite | `Migrations/M800_SourceCache.cs`, `Migrations/SqliteMigrations.cs` (existing, register), `SqliteSourceStore.cs`, `SqliteVmRepository.cs` (existing, map `source_commit`), `SqliteAdmissionStore.cs` (existing, execute `VmSourceCommit`) | hand-written SQL, additive. |
| Windows | `Source/SourceFileStore.cs`, `Updates/GitHubReleaseSource.cs` (existing, + `GetSourceAssetAsync`) | no process runner needed; file I/O only. |
| Fakes | `InMemorySourceStore.cs`, `FakeSourceFiles.cs` (temp-rooted, fault injection for delete/free-space), `FakeReleaseSource.cs` (existing, + `SourceAssets` + `FakeReleaseDir` loading), `FakeReleaseInfo.cs` (existing), `InMemoryAdmissionStore.cs` (existing, execute `VmSourceCommit`) | |
| Api | `Source/SourceCatalog.cs` (locks, reader table, `SourceReadStream`), `Source/SourceCache.cs` (`ISourceCache`: `EnsureAsync`, `OpenAsync`, `PruneAsync`, `RecoverAsync`, `RequestDeleteAsync`), `Endpoints/SourceEndpoints.cs`, `Contracts/SourceContracts.cs`, `Composition/SourceComposition.cs`, `Hosting/MediaCleanupService.cs` (existing, + recovery and daily `source-cleanup`), `Composition/ReleaseInfo.cs` (existing) | routes are always mapped and gated by `Enabled` inside the handlers, so `RouteCoverageTests` stays static. |
| Client | `Provision-AgentVM.ps1` (existing), `lib/AgentVm.Remote.ps1` (existing, + `Get-ConstructApiLastProblem`, `Test-ConstructApiFeature`, `Get-ConstructSourceTransportPlan`, `Request-ConstructSourceEnsure`, `Wait-ConstructSourceJob`, `Invoke-ConstructSourceTransport`; `Get-ConstructApiErrorInfo` + `Code`), `lib/AgentVm.Common.ps1` (existing, + `Get-ConstructSourceIdentity`, `Write-ConstructSourceManifest`), `install.ps1`, `Update-Construct.ps1` (existing, + one manifest step each) | 5.1 syntax; no ternary, no `??`, no `-AsHashtable`. |
| Guest | `bin/fetch-construct-source.sh` | bash + embedded python3; `bash -n` joins the `service` check group's syntax list. |
| Installer | `service/host/Install-ConstructHost.ps1` (existing) | directory + hardening + setting, §1.1. |

## 8. Documentation updates (part of the implementation)

| File | Change |
|---|---|
| `service/README.md` | route table rows for the five routes; `HostAdmin:Source:*` in the configuration table; job kinds `source-fetch`, `source-cleanup`; a "Source cache" section (files, registry, state machine, no-eviction retention, lock protocol, recovery, audit names); `source-cache` in the feature list. |
| `docs/remote-host.md` | §3 "Reprovision" row; a new subsection "Reprovision without uploading the checkout" (what is sent, the `-SourceMode` switch, the equivalence rule and the source manifest, the fallback messages, the admin routes and what `source-cache-full` means for the admin, which files are intentionally not on the guest); §5 secrets table unchanged plus one sentence that the fetch uses the VM token; the `apiFeatures` mention. |
| `docs/provisioning.md` | step 1/4 of "What it does" get the service-managed variant; `-SourceMode` in the parameter table; the `fetch-construct-source.sh` environment table; and the missing rows for `CONSTRUCT_SERVICE_CA_B64`, `CONSTRUCT_SERVICE_CA_FILE`, `CONSTRUCT_VERSION` in the `provision.sh` table (they are undocumented today). |
| `docs/host-release.md` | one paragraph: hosts cache `construct-source-<commit>.zip` per commit for guest reprovisioning; releases must keep the source asset; the PC-side source manifest written by `install.ps1`/`Update-Construct.ps1`. |
| `docs/installation.md` | the source manifest location and what it is for. |
| `docs/local-checks.md` | the new suites in the `service` group. |

## 9. Tests and acceptance

### 9.1 Service (`dotnet test service/Constructd.sln`; new tests under `service/tests/Constructd.Tests/Source/`)

| File | Covers |
|---|---|
| `SourceStoreTests.cs` | SQLite and in-memory: the **literal DDL of §1.2 executed on a real SQLite file**, `M800` applies on a pre-feature database and on a `M700` database, `SchemaVersion == 800`, `MinReadableBy` unchanged; upsert/get/list, `TryTransitionAsync` CAS, `TouchAsync`, `CommittedBytesAsync` (counts `downloading|ready|deleting`, not `failed`), `ListPinnedCommitsAsync` (both columns, prefix match). |
| `SourceAdmissionTests.cs` | `AdmissionPlan.VmSourceCommit` in SQLite and in-memory: pin written in the same transaction as the job and key (queued path) and as the key alone (ready path), with and without a key; 0 rows (deleting VM) → `VersionConflict`; replay returns the stored complete 200/202 body; the fingerprint differs per VM (path) and per commit (body) → `KeyConflict`; `MarkStartFailedAsync` after a start failure and the replay afterwards; **enforce-mode stale-inventory test**: with `capacity.mode = enforce`, an invalidated/expired ledger snapshot and a recording `IHostInventory` fake, both ensure paths complete without a single `inventory.ReadAsync` call, and a concurrent `OpenAsync` of another commit is not delayed by a blocked inventory fake. |
| `SourceCacheTests.cs` | `EnsureAsync` with `FakeReleaseSource`: happy path writes `.zip` and `ready`; hash mismatch → `deleting` → `failed` + no file; `source-unavailable` on a manifest without `sourceAsset` and on 404; `source-too-large`; `SourceZipRules` refusals on synthetic zips (`..` entry, absolute name, symlink, two top-level dirs, ratio, file entry named like a directory) **and acceptance of an archive produced by `git archive --format=zip --prefix=The-Construct-main/ HEAD`** (`LinuxToolchainFact`, skipped when git is absent); retry after failure; `OpenAsync` re-verifies and demotes a tampered file to `deleting(corrupt)`; `UpdateException` surfaces as a bare code in `Job.Error`. |
| `SourceStateTests.cs` | every transition of the §1.2 table; **deletion refusal**: `FakeSourceFiles` delete fault leaves the row `deleting` with the reservation, the daily job retries, a later ensure of the commit fails `source-cleanup-pending` while the fault persists and proceeds once it clears; **corruption with two readers**: reader A finds the hash wrong → row `deleting`, reader B (already open) streams to the end, a new reader gets `source-not-cached`, the file is removed only after B disposes; **restart between rename and ready**: a `downloading` row with a `.zip` present is recovered to `deleting` → `failed` and the next ensure downloads again. |
| `SourceCapacityTests.cs` | `MaxTotalBytes`: reservation at row insert, kept through `deleting`, released on `failed`; **two ensures near capacity** (different commits, concurrent): exactly one inserts, the other fails `source-cache-full` deterministically; pinned/unpinned makes no difference (no eviction); `insufficient-space` from `FakeSourceFiles.FreeBytes`; recovery keeps a crashed `downloading` reservation until its files are confirmed absent. |
| `SourceConcurrencyTests.cs` | two concurrent ensures of one commit → one `DownloadAsync` call, both jobs succeed; two different commits download in parallel; **open-vs-delete**: a registered reader makes the removal protocol report `busy`, the file survives, removal succeeds after dispose, a reader arriving after `deleting` gets `source-not-cached`; **ensure-pin-vs-delete**: admin delete between the pin write and the job → `source-pinned` unless forced; forced → the job re-downloads and ends `ready`; the gate is released on exception; the reader count is released exactly once per stream, only after the handle closed, also when `RequestAborted` fires mid-copy. |
| `SourcePruneTests.cs` | `PruneAsync` retries `deleting`, deletes `failed` > 24 h, removes orphan files; never a `ready` row; result shape and reasons; `MutableClock`. |
| `SourceRecoveryTests.cs` | startup: `downloading` → `deleting` → `failed`, `.part` and `.zip` deleted; `ready` without file → `deleting` → `failed`; a `deleting` row with a delete fault stays `deleting`; audit written with counts. |
| `SourceEndpointsTests.cs` | route shapes and status codes of §2.1; the auth matrix of §6.1 with `CreateVmTokenClient`, a second VM's token, owner, non-owner user, admin, anonymous; `202` then `GET /jobs/{id}` by the initiator; `200` short-circuit when ready; `X-Construct-Operation-Key` replay returns the complete original body for both the 200 and the 202 case and stays unchanged after the job finished; conflict on a different commit and on a different VM; `503 maintenance` when the gate is closed; `vms.source_commit` written; `GET /vms/{name}` shows `sourceCommit`; `Enabled=false` → `409 unsupported-capability` and no `source-cache` in `/health`; streamed body equals the fixture bytes with the documented headers; `409 source-downloading` with `Retry-After`; `DELETE` → `409 source-pinned` / `202` with `force`, `409 source-in-use` while downloading. |
| `SourceAuditTests.cs` | every entry of §6.4 appears with the documented action, actor and detail; no secret sentinel reaches audit, logs, job state or problem bodies (the existing sentinel harness). |
| `Updates/GitHubReleaseSourceTests.cs` (existing, extended) | `GetSourceAssetAsync`: valid tagged manifest; missing `sourceAsset`; bad hash format; oversize; 404; redirect allowlist unchanged. |
| `Api/RouteCoverageTests.cs`, `Api/AuditCoverageTests.cs`, `Foundation/HostAdminApiTests.cs` (existing) | route list, audit metadata, feature list. |

Gate: `dotnet build service/Constructd.sln` with 0 warnings; `dotnet test service/Constructd.sln` green (1,261 on `main` plus the new tests).

### 9.2 PowerShell (`pwsh -NoProfile -File …`; Windows PowerShell 5.1 syntax throughout)

| File | Covers |
|---|---|
| `test/source-transport.test.ps1` (new) | **orchestration order pins** for both paths: (a) a textual check of `Provision-AgentVM.ps1` that the local guard `if (-not $ServiceUrl) { $archivePath = New-RepoArchive }` precedes `Ensure-VmReachable`, that `Invoke-ConstructSourceTransport -Phase begin` follows it and precedes `Accepting VM host key`, that `-Phase complete` sits where the upload/unpack steps are, and that no new source helper name occurs outside an `if ($ServiceUrl)`/`else` block (the precedent is `test/provision-marker.test.sh` grepping `probe.sh`); (b) closure-driven runs of `begin`/`complete` recording the event sequence for: cache success (`ensure, wait, stage, fetch`, no pack, no upload), ensure failure before SSH (`ensure, warn, pack` in `begin`, then `upload` in `complete`, no wait/fetch), fallback inside `complete` (`wait, warn, pack, upload`), and an `upload` plan (`pack` in `begin`, `upload` in `complete`, no ensure); the full §4.2 matrix of `Get-ConstructSourceTransportPlan` including the throw rows of `cache` mode and the literal message texts of §4.9; `Get-ConstructSourceIdentity` with a `-GitRunner` fake for clean/divergent (tracked and untracked)/failing git and an assertion that the runner is invoked with `status --porcelain --untracked-files=all --ignore-submodules=none`, plus one real-git case (skipped without `git`): a temporary repository with `status.showUntrackedFiles=no` set and one untracked file is still classified `divergent`; and archive fixtures: matching manifest, edited file, missing file, extra file, ignored extra file (still equivalent), no manifest, malformed manifest; `Write-ConstructSourceManifest` against a `git archive` zip (line format, sorting, prefix stripped, temp+rename); `Get-ConstructApiLastProblem` with a shadowed `Invoke-WebRequest` for each `Class`; `Test-ConstructApiFeature` (feature present, absent, 404, transport failure, timeout); `Request-ConstructSourceEnsure` request shape (`POST /api/v1/vms/<name>/source`, body `{commit}`, the operation-key header) and **every** outcome of §4.7 including a 200 replay body with `state = downloading` → `downloading`; `Wait-ConstructSourceJob` every state and the per-poll timeout clamp against the deadline; `Invoke-ConstructSourceTransport` `begin` + `complete` driven through **every row of §4.8** with fakes, asserting the transport chosen, the warning text, that `-Ensure` runs exactly once per run and never in `complete`, that the guest fetch never runs after an ensure failure, that `-Upload` runs exactly once on fallback and never on success, and the `cache`-mode throws. |
| `test/source-release.test.ps1` (existing, extended) | `install.ps1`/`Update-Construct.ps1` write the manifest outside the checkout and a manifest failure does not fail the install. |
| `test/remote-client.test.ps1` (existing) | no change expected; re-run because `lib/AgentVm.Remote.ps1` changes. |
| `test/provision-seed-user.test.ps1`, `test/instance-identity.test.ps1` (existing) | re-run: they pin the local path's remote command and env prefix, which must stay byte-identical. |
| `service/tests/host-installer.test.ps1` (existing) | the `source` directory, hardening entry and setting. |

### 9.3 Bash

| File | Covers |
|---|---|
| `test/fetch-construct-source.test.sh` (new) | runs `bin/fetch-construct-source.sh` against a `python3 http.server` stub started by the test on `127.0.0.1:<ephemeral>` (records each request's path and `Authorization` header to a file; answers from a fixture zip **generated by `git archive --format=zip --prefix=The-Construct-main/ HEAD`** so the real 119 directory entries and the mode-0 files are exercised, plus synthetic zips for the refusals): **fresh install** (no previous `REPO_DIR`) and **replacement** (a pre-existing `REPO_DIR` whose content is gone afterwards) both install the prefix-less layout owned by `CONSTRUCT_SEED_USER` (the test's own user), `bin/provision.sh` executable and `README.md` `0644`, `.construct-revision` equals the commit, `CONSTRUCT_SOURCE_INSTALLED=` printed, work dir and `.repo-old.*` removed; the header file carried the token and the token never appeared in the process argv (stub `curl` on `PATH` for that one assertion, as `test/construct-expose.test.sh` does); wrong size → exit 4 and `REPO_DIR` byte-identical to before; wrong hash → 4; `409` then success on retry; `404` → 3; zip with `../` entry → 5; symlink entry → 5; two top-level dirs → 5; missing input → 2; `--cacert` passed when `CONSTRUCT_SERVICE_CA_FILE` is set; **`chown` failures** (stub on `PATH`) for the tree and for the parent → 6 with the old repo untouched; **rename failures** (stub `mv` failing on call 1, on call 2, and on the restore) → 6 old untouched, 6 old restored, 7; **`rm -rf` of the old tree failing** (stub `rm`) → exit 0 with the warning line and the new repo in place. |
| `test/provision-marker.test.sh` (existing) | re-run: `record_timestamps` must be untouched. |
| `test/remote-e2e.test.sh` (existing, extended) | see §9.4. |
| `bash -n bin/fetch-construct-source.sh` | added to the `service` group's syntax list in `test/run-local-checks.sh`, next to `bin/provision.sh`. |

### 9.4 Fake-mode end to end (`test/remote-e2e.test.sh`)

Fake mode reads `HostAdmin:Source:FakeReleaseDir`: `FakeReleaseSource.GetSourceAssetAsync` reads `<dir>/host-<commit>/manifest.json` and serves `<dir>/host-<commit>/construct-source-<commit>.zip`. The test builds that directory from the worktree (`git archive --format=zip --prefix=The-Construct-main/ HEAD` plus a minimal manifest with `commit`, `releaseTag`, `repository`, `schemaVersion`, `ref`, `payloadAsset`, `payloadSha256`, `payloadSizeBytes`, `builtAt`, `sourceAsset`, `sourceSha256`, `sourceSizeBytes`) and starts the service with `Constructd__HostAdmin__Source__FakeReleaseDir=<dir>`. New part (4), through the **real client helpers**: the PowerShell client creates a VM, `Request-ConstructSourceEnsure` → `downloading` → `Wait-ConstructSourceJob` → `succeeded`; a second call with a **new** key → `ready`; a third call with the **first** key → `downloading` with the original `jobId` and `Replayed = $true` (not `malformed`); `GET /health` lists `source-cache`; then the guest half: `bin/fetch-construct-source.sh` with `CONSTRUCT_VM_TOKEN_FILE` holding the one-time token, `REPO_DIR=$tmp/opt/repo`, `CONSTRUCT_SEED_USER=$(id -un)` against the real fake-mode service → layout assertions; another VM's token → `403`; admin `GET /host/source-cache` shows the item with `pinnedBy` and `committedBytes`; `DELETE` without `force` → `409 source-pinned`, with `force` → `202` and a re-ensure downloads again. Also asserts that no token appears in the service log or in any client output.

### 9.5 Acceptance checklist

1. `dotnet build service/Constructd.sln`: 0 warnings, 0 errors. `dotnet test service/Constructd.sln`: all green, count reported.
2. `pwsh -NoProfile -File test/source-transport.test.ps1`, `test/source-release.test.ps1`, `test/remote-client.test.ps1`, `test/provision-seed-user.test.ps1`, `test/instance-identity.test.ps1`, `service/tests/host-installer.test.ps1`: green.
3. `bash test/fetch-construct-source.test.sh`, `bash test/provision-marker.test.sh`, `bash test/remote-e2e.test.sh`: green (e2e with the inherited `CONSTRUCT_*` variables unset if a suite reads them).
4. `bash test/run-local-checks.sh service`: green.
5. Zero-change proof: `git diff` of the remote command line and env prefix produced by the local path is empty; `test/instance-identity.test.ps1` and `test/provision-seed-user.test.ps1` pass unchanged; a local `Provision-AgentVM.ps1` transcript diff against `main` shows no new lines; the tar packed by the local path has the same entry list as before (the manifest lives outside the checkout).
6. Documentation of §8 updated.
7. Explicitly **not** validated in this delivery: Hyper-V, Windows PowerShell 5.1 execution, a real GitHub download by a Windows host, the installer on a real host. These go to the field-test checklist of the host-administration contract (`docs/field-test-host-admin.md`), with three added items: first provision through the cache, reprovision through the cache, and the upload fallback against an old service.

## 10. Limitations recorded

- T1–T4 (§0.1) are proposed technical defaults; the owner was not reachable in this run and no approval is claimed. Relaxing them later changes the plan function, the retention rule or one fallback row, not the API.
- A full cache (`source-cache-full`) is not self-healing: every affected reprovision uploads as before until an admin deletes entries. The admin list shows `committedBytes` so this is visible; the default cap holds hundreds of commits at today's ~4 MB.
- Archive installs made before this change upload until their next `Update-Construct.ps1` writes the manifest; a hand-edited archive install with a manifest uses an overlay in `auto` when its changes fit the limits. `-SourceMode cache` explicitly omits those changes.
- A commit that `main` has not published yet (a push whose release workflow is still running, or coalesced away by GitHub) falls back to the upload; the next reprovision after publication uses the cache.
- The client learns the feature from one `GET /health` per run; a host disabled between probe and ensure answers `409 unsupported-capability`, which is handled as a fallback, not an error.
- The cache is host-charged and invisible to `GET /host/capacity`; an operator whose `RootDir` volume also holds VM disks sets `MaxTotalBytes` accordingly.
- Speed is not measured here: this environment has neither Hyper-V nor a Windows client. The saving is the pack (`tar.exe` over ~16 MB of checkout), the scp through the forwarded port, and the guest-side untar, replaced by one LAN download of ~4 MB (*measured* zip size at `253f3ce`) from the host the VM runs on, plus a local hash pass for archive installs whose duration is an estimate (expected sub-second) pending the field test.
- The source zip is the tracked tree; anything the tar carried from an ignored path is not on the guest. §3.5 lists why no guest script depends on such a path; a future guest script must keep it that way.

## Deviations

- Corrected three pre-existing compiler/analyzer warnings in host-update code and tests to meet the zero-warning build gate.
- The stale-inventory admission test uses the existing `IHypervisorInventory` seam; the contract calls it `IHostInventory`.
- The host installer did not have the stated media directory/settings block; source settings now preserve the existing HostAdmin section and add Source.RootDir alongside the existing ISO setup.
- Source ZIP checks also reject duplicate paths and file/directory collisions, matching safe extraction on Windows and Linux.

- The orchestration helper also exposes `-Pack`, so begin-time packing and fallback packing can be asserted independently from upload.
- Source API calls opt into a bounded preflight/request budget; legacy callers retain preflight exception behavior and existing status/error accessors.
- Byte-identity checks compare the unchanged local archive algorithm, upload commands, transcript on identical inputs and downstream environment; naturally the new tracked feature files join future source archives, while no generated manifest enters the checkout.

- The 2026-09-12 overlay owner decision supersedes the original equivalence-only client defaults.
  The host cache protocol and service remain unchanged.
- Overlay packing reports both uncompressed `SizeBytes` and `CompressedSizeBytes`; guest integrity
  checks and progress use the compressed size. Packing errors expose the packer's fixed diagnostic code or an exception type, so
  paths or file contents from an underlying exception cannot leak into diagnostics.
- The 64 MiB client limit includes the deletion-list bytes. Highly compressible overlays can
  still exceed the specified guest 16× expansion bound and fall back to full upload.
- A user-selected symlink or junction at the checkout root is allowed; linked entries and
  parent directories within the checkout are refused. Git delete/write overlaps retain the
  on-disk file, and the packer independently refuses conflicting write/delete paths.
