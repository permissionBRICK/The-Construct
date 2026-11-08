# Host-stored config backups

Status: plan, 2026-11-08. Branch `feat/host-config-backups`. This document is the contract the
implementation follows; section 7 assigns the work packages. Every name, path, JSON field, exit
code and problem code below is binding for all packages: change it here first.

## 1. Goal and decisions

A remote VM's **config export** (the content `bin/export-config.sh` produces today: agent auth,
memory, chat history, `~/.secrets`, T3 Code state, git and CLI credentials) is backed up **to
the VM's host**, automatically and on demand. The rest of the VM is disposable by design, and
project profiles keep travelling through [config sync](../config-sync.md), which stays their
primary path. Decisions taken by the project owner:

1. **Config export, not disk images.** No hypervisor snapshot, no driver involvement. The feature
   works identically on Proxmox and Hyper-V hosts because only `constructd` and the guest take
   part.
2. **The host never sees cleartext.** The VM packs, compresses and encrypts the export before
   the first byte leaves it. Only the user's PCs can unlock a backup. The VM can create backups
   unattended but cannot decrypt old ones; it receives the key for one backup only when the
   client restores that backup.
3. **No service inside the VM.** The host asks for a backup through the reply to the guest's
   existing activity heartbeat, as it already asks for a vault scrub. The VM uploads with its
   existing VM token. Nothing listens in the guest, so port or firewall changes inside the VM
   cannot break it.
4. **Reinstall goes through the host.** For a remote VM the default reinstall is: back up to the
   host, reinstall, restore from the host. The backup never travels through the client PC.
5. **The client PC is a manual option.** "Save a backup to this PC" and "Restore from a backup on
   this PC" (onto a local or a remote VM) are manual actions, never a default flow.
6. **Small rotation.** Per VM, default 4 backups, configurable, spread over time (3.6). The
   backup taken before a reinstall is pinned until a restore from it succeeded.
7. **Disk space is checked, never assumed.** The VM and the host estimate the space a backup
   needs and skip it, with a recorded reason, when it would push free space below a reserve. A
   run that fails anyway removes everything it wrote and reports the failure. The Companion
   shows the warning and raises one Windows notification per failure streak.
8. **Local VMs without a host service** keep today's export/restore for reinstall. They gain the
   manual "save to this PC" and "restore from this PC" actions in the new encrypted format.

Out of scope: disk-image backups, off-host copies made by the host, automatic restore onto
another host (a manual "save to this PC" plus "restore from this PC" covers it), deduplicated
storage, the unpopulated `backupPick` select of the custom-reinstall cards.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Config export / restore | `bin/export-config.sh` (stage with `cp -a` under `/tmp/construct-export.*`, `tar -czf`, no cleanup on failure, T3 stop/copy/start + `t3-resume.json` at ~337-384, `backup-info.json` at ~526-545), `bin/restore-config.sh` (`BACKUP_TGZ`/`BACKUP_DIR`, `tar -tzf`/`-xzf`, T3/Codex service handling, does not touch `projects/` or the serve-web token) |
| Guest CLI | `bin/construct` (`case` at ~206-245, `find_helper`), helpers `construct-vm.sh` (cleanest service client: `cfg_resolve`, header file, `api_request`, `api_fail`), `construct-secret.sh`, installed by `install_construct_cli` in `bin/provision.sh` (~1159-1175); other helper lists: `bin/adopt-host.py` (~113), `service/host/ConvertTo-ConstructHost.ps1` (~306) |
| Heartbeat | `bin/construct-idle-report.sh` (`vault_scrub_due`, `start_vault_scrub` via `systemd-run`), `service/src/Constructd.Api/Endpoints/IdleEndpoints.cs` `PostActivityAsync` (200 `{vaultScrub=true}` or 204) |
| VM token | `/etc/construct/vm-token`, written by `write_vm_token` in `provision.sh` (does not exist on a fresh VM before `provision.sh` runs); scheme `VmToken`; policies in `Auth/AuthorizationSetup.cs`; `ApiHelpers.ResolveVmAsync(…, Policies.VmSelfOrOwnerOrAdmin)`; `Ownership.VmTokenCanAccessVm` |
| Persistence | `Constructd.Sqlite/Migrations` (`ISqliteMigration`, `SqliteMigrations.All`, latest `M860_HttpsForwards`; schema version pinned in `Foundation/PersistenceFoundationTests.cs` and `Source/SourceStoreTests.cs`), store pattern: `ITokenUsageStore` → `SqliteTokenUsageStore` → `InMemoryVmRepository.TokenUsage` |
| Host config sections | `Core/Domain/HostConfig.cs`, `Configuration/HostAdminDefaults.cs`, `Infrastructure/HostConfigValidation.cs` (`Defaults`, `Validate`), `GET/PUT /host/config` in `HostAdminEndpoints.cs` |
| Streaming precedents | upload limit per request: `MediaEndpoints.ChunkAsync` (`IHttpMaxRequestBodySizeFeature`); streamed download: `SourceEndpoints.FetchAsync` |
| Background services | `TokenUsageCleanupService` (public `PruneAsync`, maintenance gate), `MediaCleanupService` |
| Route/audit guards | `RouteCoverageTests` (exact route list, 401 for anonymous), `AuditCoverageTests` (every mutating route `.Audited`) |
| Feature flags | `Composition/ReleaseInfo.cs` `apiFeatures` (read by clients from `/health`) |
| Client reinstall | `Auto-Install.ps1` (`-BackupMode save\|existing\|wipe`, remote reinstall ~2643-2745, `Invoke-RemoteVmConfigExport`, provision splat ~3025), `Provision-AgentVM.ps1` (`-Action export`, `-RestoreDir`, restore step ~2913-2969 runs after `provision.sh`, `DEFER_PROJECT_COMMANDS` ~2859-2865, `Send-GuestSecret` stdin helper ~1025-1062) |
| Client host API | `lib/AgentVm.Remote.ps1` `Invoke-ConstructApi` (JSON only), `Send-ConstructGuestReport` (pattern for a POST with `[Uri]::EscapeDataString`) |
| Panel (shared by extension and Companion) | `extension/media/panel.html` / `panel.js`; the remote-only **Idle policy** module (`#idleModule`) is the precedent for a remote-only Backups module; extension backend `extension.js`, `src/remotehost.js`, `src/lifecycle.js`; Companion `RemoteHostRoutes.cs`, `Dispatch/MessageDispatcher.cs`, `Core/Lifecycle/LifecycleBuilder.cs`; parity fixtures regenerated by `node extension/test/export-parity-fixtures.js` |
| Companion toasts | `Core/Notifications/NotificationProtocol.Toast`, `IToastRaiser`; Companion-originated toast precedent `Core/Vault/VaultService.ToastAsync` |

## 3. Design

### 3.1 What a backup contains and how the VM builds it

The payload is the tarball layout `bin/export-config.sh` writes today (`home/`, `projects/`,
`etc/`, `backup-info.json`, `MANIFEST.txt`), with these changes:

- **Stage-only mode.** `export-config.sh` gains `STAGE_DIR=<dir>`: it stages into that directory
  and stops before packing (no `OUT`, no tarball). Without `STAGE_DIR` it behaves as today.
  It now removes its own stage on any failure (EXIT trap) unless `STAGE_DIR` was given, in which
  case the caller owns the directory.
- **Staging without copying.** Staging uses `cp -a --reflink=auto`. Construct guests run XFS
  with reflinks and `/tmp` is on the root file system, so the staged copy shares blocks with the
  originals. Without reflink support it falls back to a real copy; the estimate (3.3) accounts
  for it.
- **Online SQLite snapshots.** `EXPORT_REASON` is `reinstall` (the default, so every existing
  caller keeps today's behaviour) or `backup` (`construct-backup.sh` passes `backup` for the
  `scheduled` and `manual` triggers and `reinstall` for `pre-reinstall`). With `backup` the
  export does **not** stop `t3code-serve`. Every SQLite database it captures (T3 `statev2.sqlite` / legacy `state.sqlite`,
  OpenCode `opencode.db`, Codex `memories_*.sqlite`) is copied with SQLite's online backup API
  (`python3 -I` with `sqlite3.Connection.backup`; fallback `sqlite3 <src> ".backup <dst>"`; last
  resort a plain copy with a warning). The `-wal`/`-shm` side files are not captured; the
  snapshot is self-contained. With `reinstall` it keeps today's behaviour (snapshot
  busy T3 threads into `t3-resume.json`, stop T3, copy, start T3).
- **No local archive.** `construct-backup.sh` streams
  `tar -C <stage> -cf - . | zstd -T0 -3 -q | age -r <ephemeral recipient>` straight into the
  upload (or to stdout). Nothing compressed or encrypted is written to the VM disk.
- `backup-info.json` gains `format: "construct-backup/1"`, `backupId`, `trigger` and `instance`
  (the backup script adds them to the staged file before packing).
- The recipient keys in `/etc/construct/backup-recipients/` ride in the backup at
  `etc/construct/backup-recipients/` and are restored additively.
- `restore-config.sh` detects the compression itself (`tar -tf` / `tar -xf` without `-z`; GNU tar
  recognises gzip and zstd), so it accepts today's `.tar.gz` and the new `.tar.zst`.

`age` and `zstd` join the base package list in `bootstrap.sh`; `openssl` is listed too (present
on Ubuntu images, but no longer assumed).

### 3.2 Encryption and keys

```
           per backup                                   per PC
   age identity E (X25519, ephemeral)          RSA-3072 key pair K_pc
   payload = age -r E.public                   K_pc.private: DPAPI on the PC, never leaves it
   keys    = { RSA-OAEP-SHA256(K_pc.public, E.private) for every enrolled PC }
```

- **Payload:** `age` with a fresh identity `E` per backup (`age-keygen`), kept in shell variables
  only and discarded after wrapping.
- **Wrapping** on the VM: `printf '%s' "$E" | openssl pkeyutl -encrypt -pubin -inkey <pem>
  -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 -pkeyopt rsa_mgf1_md:sha256 |
  base64 -w0`. The client unwraps with `RSACng.Decrypt(bytes, RSAEncryptionPadding.OaepSHA256)`
  (Windows PowerShell 5.1 / .NET Framework 4.7.2, and PowerShell 7). The PC needs no `age`.
- **Fingerprint** of a public key: lowercase hex SHA-256 of its SubjectPublicKeyInfo DER
  (`openssl pkey -pubin -in <pem> -outform DER | sha256sum`).
- **Keys file** (stored by the host, useless to it):

  ```json
  {"format":"construct-backup-keys/1","backupId":"<id>","createdAt":"<ISO-8601 UTC>",
   "recipients":[{"fingerprint":"<64 hex>","label":"<label>","wrapped":"<base64>"}]}
  ```

  Labels match `^[A-Za-z0-9._ -]{1,64}$`. At most 32 recipients. Serialized ≤ 64 KiB.
- **Restore:** the client reads the keys file, finds its own fingerprint, unwraps `E`
  (`^AGE-SECRET-KEY-1[0-9A-Z]+$`) and sends that one line to the VM on SSH stdin. The VM
  decrypts with `age -d -i <(printf '%s\n' "$E")`, so `E` never touches a disk.
- **PC key** (PowerShell client, `lib/AgentVm.Backup.ps1`): created on first need in
  `%LOCALAPPDATA%\The-Construct\backup\`: `pc-key.dpapi` (DPAPI CurrentUser-protected bytes of
  the RSA key's XML form, `ToXmlString($true)`), `pc-key.pub.pem` (SPKI PEM, DER built by hand so
  it works on .NET Framework), `pc-key.json` (`{fingerprint, label, createdAt, exportedAt}`).
  The label defaults to the computer name, sanitised to the label pattern.
- **Enrolment:** the client puts its public key on the VM over SSH
  (`construct backup recipients add --label <label>`, PEM on stdin). The VM never takes
  recipients from the host: a host that could add one could read every later backup.
- **Export / import of the PC key** (recovery, more PCs): a JSON file
  `{"format":"construct-backup-key/1","kdf":"pbkdf2-sha256","iterations":600000,"salt":b64,
  "iv":b64,"ciphertext":b64,"mac":b64,"fingerprint":hex,"label":…}`; key material = PBKDF2(pass,
  salt, 600000, 64 bytes) split into an AES-256-CBC key and an HMAC-SHA256 key; MAC over
  `iv || ciphertext`; plaintext = the RSA XML. Import refuses a wrong MAC, and replaces this PC's
  key only after confirmation (the old key is kept as `pc-key.<fingerprint>.dpapi`, so backups
  wrapped to it stay readable: unwrap tries every key in the folder). The UI warns while
  `exportedAt` is null.

### 3.3 Guest CLI: `construct backup`

`bin/construct-backup.sh`, routed by `construct backup`, root only, `set -Eeuo pipefail` with
cleanup traps. Reads the service settings like `construct-vm.sh` (`cfg_resolve`, header file,
`--cacert`, token never on argv). Human progress goes to stderr; stdout carries only the
documented output.

| Command | stdin | stdout | Does |
|---|---|---|---|
| `create [--trigger scheduled\|manual\|pre-reinstall]` (default `manual`) | – | one JSON line `{"id","sizeBytes","sha256","trigger","gitCredentialHosts":[…]}` | space check, stage, open session, upload keys, stream payload; reports skips/failures to the host. `gitCredentialHosts`: the host names in the staged `.git-credentials`, no secrets |
| `create --stdout` | – | a container (below) | same build, no host involved; works on a VM without a service |
| `download <id>` | – | a container | writes the host's keys file and payload as a container |
| `fetch --from-host <id> --out <file>` | line 1: `E` | – | downloads, decrypts to `<file>` (a `.tar.zst`, mode 0600), checks `backupId` |
| `verify --from-host <id>` | line 1: `E` | – | downloads, decrypts and unpacks the whole backup without writing it, checks `backupId`; the reinstall's check before it relies on an earlier backup |
| `restore --from-host <id>` | line 1: `E` | – | fetch + `restore-config.sh` + extras (below) + `POST …/restored` |
| `restore --stdin --expect-id <id>` | line 1: `E`, then the raw age payload | – | decrypt from stdin, check `backupId`, restore + extras; no host involved |
| `list` | – | host JSON | `GET …/backups` |
| `status` | – | host JSON | `GET …/backups/status` |
| `recipients list` | – | JSON array `[{"fingerprint","label","addedAt"}]` | |
| `recipients add --label <l>` | PEM | the fingerprint | validates an RSA key ≥ 3072 bits, writes `/etc/construct/backup-recipients/<fp>.pem` (0644) and `<fp>.json` (`{label, addedAt}`); idempotent |
| `recipients remove <fp>` | – | – | |
| `estimate` | – | `{"needBytes","freeBytes","reserveBytes","reflink","ok"}` | |
| `restore-extras <tarball>` (hidden) | – | – | only the restore extras below, for the provisioning path that ran `restore-config.sh` itself |
| `restored <id>` (hidden) | – | – | `POST …/backups/{id}/restored` |

**Restore extras** (what the PowerShell client did with the extracted backup before; now the
guest does it because the client never sees the cleartext): copy `projects/*.json` that are
missing from `/opt/construct/projects/` (additive, never overwrite); write
`etc/construct/vscode-serve-web.token` to `/etc/construct/vscode-serve-web.token` (0600) when the
backup has one and `systemctl try-restart` the serve-web unit; restore the backup recipients
additively. `restore-config.sh` itself is unchanged apart from the compression detection.

**Exit codes:** `0` ok, `2` usage, `3` no recipients enrolled, `4` no host service or the host
lacks the feature, `5` not enough space on the VM (skipped), `6` host refused (space, quota,
disabled, in progress), `7` transfer failed, `8` decryption or verification failed, `9` another
backup or restore is running, `10` export or restore step failed.

**Container** (`.cbk`, PC files): line 1 `construct-backup/1`, line 2 the keys JSON on one line,
then the raw age payload.

**Space check** before staging:

```
need    = Σ size of the SQLite databases to snapshot (always real copies)
        + (reflink unavailable ? Σ size of the paths the export stages : 0)
        + 64 MiB
reserve = max(2 GiB, 5 % of the file system size)
proceed only if  free(/tmp's file system) − need ≥ reserve
```

Reflink support is probed with `cp --reflink=always` of a small file inside a scratch directory
on the staging file system. A skipped run reports `vm-disk-space` with `need`, `free` and
`reserve` in `detail`, exits 5. A run that fails later (ENOSPC, export, tar, upload) removes the
stage, `DELETE`s the open session and reports the failure. One run at a time:
`flock -n /run/construct/backup.lock` (exit 9).

**Reported reasons:** `vm-disk-space`, `no-recipients`, `export-failed`, `upload-failed`,
`host-refused` (detail = the host's problem code), `verify-failed`.

### 3.4 Trigger: the activity heartbeat

`POST /api/v1/vms/{name}/activity` replies `200` with only the fields that are true, or `204`:

```json
{ "vaultScrub": true, "backupDue": true, "backupTrigger": "scheduled" }
```

`backupTrigger` is `scheduled` or `manual`. On `backupDue` the guest starts
`systemd-run --quiet --no-block --collect --unit construct-backup
--property=SyslogIdentifier=construct-backup -- <construct> backup create --trigger <t>` when
the unit is not already active (same fallbacks as the vault scrub when not under systemd). The
host decides "due" (3.6); the guest never schedules on its own.

### 3.5 Host API

All routes under `/api/v1/vms/{name}/backups`. `{id}` is 32 lowercase hex
(`{id:regex(^[[0-9a-f]]{{32}}$)}`). **VM**: a VM token for exactly `{name}`. **User**: the VM's
owner or an admin. Backups belong to *(owner, VM name)*, not to one incarnation: a reinstalled
VM of the same owner and name sees its predecessor's backups; another owner reusing the name
sees none. Problems use `CodedProblems.Create` (the `code` extension is the contract).

| Method and path | Caller | Request → reply |
|---|---|---|
| `POST …/backups` | VM | `{"trigger","estimatedBytes"}` → `201 {"id","maxBytes"}`; `403 backups-disabled`; `409 backup-in-progress`; `507 host-disk-space` / `backup-quota` |
| `PUT …/backups/{id}/keys` | VM | keys JSON → `204`; `400 validation`; `409 backup-not-open` |
| `PUT …/backups/{id}/content` | VM | `application/octet-stream`, streamed (chunked or with length) → `200 {"id","sizeBytes","sha256","createdAt"}`; `409 backup-keys-missing` / `backup-not-open`; `413 backup-too-large`; `507 host-disk-space`. Any failure deletes the partial file and closes the session as failed |
| `DELETE …/backups/{id}` | VM (only its own open session) / user (any) | `204`; `404` |
| `POST …/backups/report` | VM | `{"trigger","result":"skipped"\|"failed","reason","detail"}` (detail ≤ 500 chars) → `204` |
| `POST …/backups/{id}/restored` | VM | → `204`; clears `pinnedUntil` |
| `GET …/backups` | VM, user | `{"backups":[{"id","createdAt","trigger","sizeBytes","sha256","pinnedUntil"}]}`, complete only, newest first |
| `GET …/backups/{id}/content` | VM, user | the payload, `Content-Length`, `ETag: "<sha256>"` |
| `GET …/backups/{id}/keys` | VM, user | the keys JSON |
| `GET …/backups/status` | VM, user | `{"enabled","lastAttemptAt","lastSuccessAt","lastFailure":{"at","trigger","reason","detail"}\|null,"failingSince","requestPending","count","totalBytes","nextDueAt"}` (timestamps ISO-8601 or null) |
| `POST …/backups/request` | user | → `202 {"requestPending":true}`; `403 backups-disabled` |
| `GET …/backups/settings` | user | `{"intervalHours":null\|n,"keep":null\|n,"effective":{"enabled","intervalHours","keep"},"host":{"enabled","intervalHours","keep","maxKeep"}}` |
| `PUT …/backups/settings` | user | `{"intervalHours":null\|0..720,"keep":null\|1..maxKeep}` → the GET shape; `400 validation` |

`null` means "the host default"; `intervalHours: 0` turns scheduled backups off for that VM
(manual and pre-reinstall backups still work). Mutating routes are audited (`backup.create`,
`backup.keys`, `backup.upload`, `backup.delete`, `backup.report`, `backup.restored`,
`backup.request`, `backup.settings`); details carry ids, sizes and reason codes, never content.
`/health` advertises the feature as `config-backups` in `apiFeatures` on both platforms.

### 3.6 Host storage, schedule and rotation

**Storage.** Setting `Constructd:Backups:RootDir` (default `<data dir>/backups`, where the data
dir is the database's directory). Both installers create it (`/var/lib/constructd/backups`,
`C:\ProgramData\Construct\service\backups`), harden it like the media directory and write the
setting; both updaters treat it as protected data. Layout:
`<root>/<first 16 hex of sha256(lowercase owner)>/<lowercase vm>/<id>.age` (`<id>.age.partial`
while uploading). Keys files live in the database.

**Tables** (migration `M870_ConfigBackups`):

```
backups(id TEXT PRIMARY KEY, owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE,
        trigger TEXT NOT NULL, state TEXT NOT NULL,           -- open | complete
        created_at TEXT NOT NULL, completed_at TEXT, size_bytes INTEGER, sha256 TEXT,
        pinned_until TEXT, keys_json TEXT)
backup_vms(owner TEXT NOT NULL COLLATE NOCASE, vm TEXT NOT NULL COLLATE NOCASE,
           interval_hours INTEGER, keep INTEGER,             -- per-VM overrides, NULL = host default
           last_attempt_at TEXT, last_success_at TEXT, last_failure_json TEXT, failing_since TEXT,
           request_pending_at TEXT, vm_missing_since TEXT, PRIMARY KEY(owner, vm))
```

**Host config section `backups`:**

```
BackupsConfig(bool Enabled = true, int IntervalHours = 24, int Keep = 4, int MaxKeep = 10,
              int PinDays = 14, int RetainDeletedDays = 14, long MinFreeBytes = 5 GiB,
              long? MaxBytesPerVm = null)
```

Validation: `IntervalHours` 0..720 (0 = no scheduled backups on this host), `1 ≤ Keep ≤ MaxKeep
≤ 50`, `PinDays` 1..365, `RetainDeletedDays` 0..3650, `MinFreeBytes` ≥ 0, `MaxBytesPerVm` null or
≥ 64 MiB.

**Due** (evaluated in the heartbeat handler, primaries only): `Enabled` on the host, effective
interval > 0 (or a manual request), no open session, and either a manual request is pending, or
`now − lastSuccessAt ≥ interval` (no success yet counts as due) and
`now − lastAttemptAt ≥ min(interval, 6 h)`. A scheduled backup that is due waits for a heartbeat
with `busy: false`, unless the VM has been due for 6 h. A saved or stopped VM sends no heartbeat
and has changed nothing, so it is skipped without special code. A manual request expires after
1 h without a session. Opening a session sets `lastAttemptAt` and clears the request.

**Space on the host.** Opening a session requires
`free(root) − max(estimatedBytes, last size of this VM) × 1.2 ≥ MinFreeBytes` and, when set,
stored total of the VM + that estimate ≤ `MaxBytesPerVm`. `maxBytes` in the reply is
`min(free(root) − MinFreeBytes, MaxBytesPerVm − stored total)`. While writing, the service
re-checks free space every 64 MiB and aborts below `MinFreeBytes`. Rotation deletes old backups
only after a new one is complete.

**Rotation** (pure function, per owner and VM, after every completed backup and in the hourly
cleanup). Pinned backups (`pinnedUntil > now`) are never deleted and do not count. Of the rest,
keep the union of:

| `keep` | kept |
|---|---|
| 1 | the newest |
| 2 | the newest, and the newest created before the start of the current week |
| ≥ 3 | the `keep − 2` newest, the newest created before the start of the current week, and the newest created before the first day of the current month |

Weeks start Monday 00:00 UTC, months on the 1st 00:00 UTC. Because the older slots hold "the
newest before a boundary", a backup kept there survives until the boundary moves on, so a few
copies reach back up to a week and up to a month. When slots coincide, the next newest backups
fill the free places, so `keep` backups remain whenever that many exist.

**Pinning.** A `pre-reinstall` backup gets `pinnedUntil = createdAt + PinDays`;
`POST …/restored` clears it. That keeps the good backup from being rotated out by the empty
VM's first scheduled backups if the restore fails.

**Cleanup service** (hourly, maintenance-gate aware): rotation for every *(owner, vm)*; delete
open sessions older than 2 h and orphaned `.partial` files; set `vm_missing_since` for pairs
without a VM of that owner and name (clear it when one exists again) and delete all backups of
pairs missing for longer than `RetainDeletedDays`.

### 3.7 Status and warnings

The host keeps per *(owner, VM)*: `lastAttemptAt`, `lastSuccessAt`, `lastFailure`,
`failingSince` (set by the first failure or skip after a success, cleared by a success),
`requestPending`. The panel's Backups module shows the status and a warning while
`failingSince` is set. The Companion polls `…/backups/status` for each remote instance whose
host advertises `config-backups` (at start and every 15 minutes) and raises one Windows
notification per *(instance, failingSince)*, remembered in its state so it does not repeat
after a restart.

### 3.8 Client flows

**Script actions** (new `-Action` values of `Provision-AgentVM.ps1`, same instance targeting
as `-Action export`; all work for local and remote VMs unless noted):

| Action | Parameters | Does |
|---|---|---|
| `backup-enroll` | – | ensure the PC key, `construct backup recipients add` |
| `backup-save` | `-BackupFile <path>` `[-BackupId <id>]` | enrol, then stream `construct backup create --stdout` (or `download <id>`, remote only) into the file over SSH |
| `backup-restore` | `-BackupFile <path>` or `-BackupId <id>` (remote only) | unwrap `E` from the container header or from `GET …/backups/{id}/keys`, then `restore --stdin --expect-id` (streaming the payload) or `restore --from-host` |
| `backup-key-export` | `-BackupKeyFile <path>` | asks for a passphrase twice in the console, writes the export file, sets `exportedAt` |
| `backup-key-import` | `-BackupKeyFile <path>` | asks for the passphrase, installs the key (3.2) |

Binary data never goes through PowerShell pipelines: SSH runs as a `System.Diagnostics.Process`
with `StandardOutput.BaseStream` / `StandardInput.BaseStream` copied to or from a `FileStream`
(`Invoke-ConstructSshStreamToFile`, `Invoke-ConstructSshStreamFromFile` in
`lib/AgentVm.Backup.ps1`), which works in Windows PowerShell 5.1 and PowerShell 7. `E` is
written to the process stdin first and never logged.

**Provisioning** enrols the PC key after `provision.sh` installed the CLI (both local and remote
VMs; a failure is a warning, not a provisioning error).

**Remote reinstall** (`Auto-Install.ps1`, remote branch), when the host advertises
`config-backups`:

- `-BackupMode save` (default): if the VM is reachable: enrol, then
  `construct backup create --trigger pre-reinstall` over SSH and keep the returned id. If that
  fails, offer (console) or, with `-FromPanel`/non-interactive, use the newest host backup when
  one exists, else fall back to today's client-side export.
- `-BackupMode existing`: the newest host backup; when the host has none, today's PC backup.
- Before the VM is deleted, an earlier host backup counts only when this PC really unwraps its
  key (an RSA decryption, not a fingerprint match) and, while the old VM is reachable, when
  `construct backup verify --from-host <id>` passes on that VM (internal action
  `Provision-AgentVM.ps1 -Action backup-verify -BackupId <id> -BackupKeysJson <keys>`). A
  backup that fails is skipped and the next older one is tried; none left means today's export.
- `-BackupMode wipe`: unchanged.
- The chosen id goes to `Provision-AgentVM.ps1 -RestoreHostBackupId <id>`. Provisioning then
  runs the restore step against the host backup: unwrap `E` from `GET …/keys`,
  `construct backup fetch --from-host <id> --out /tmp/construct-config-restore.tar.zst` (E on
  stdin), then the existing `restore-config.sh` invocation and the same failure handling, then the
  guest-side restore extras (`construct backup restore` does them; for this path the client runs
  `construct backup restore-extras <file>`, a hidden subcommand), then the deferred
  project-commands phase (`DEFER_PROJECT_COMMANDS` is set when either `-RestoreDir` has an
  archive or `-RestoreHostBackupId` is given), then `POST …/backups/{id}/restored` via the CLI
  (`construct backup restored <id>`, hidden), and finally the file is deleted.
- Git credentials: with a fresh pre-reinstall backup the `create` result also carries
  `"gitCredentialHosts":["host", …]` (hosts only, no secrets), which replaces
  `Test-BackupHasGitCredentials` for the preflight. For the newest-host-backup path the
  preflight prompts as for a fresh install. The serve-web token and project profiles come back
  through the guest-side extras.
- If the host lacks `config-backups`, the VM lacks `construct backup`, or the PC has no key and
  enrolment fails, the flow falls back to today's client-side export with a warning.

**Panel** (shared `extension/media/panel.html`/`panel.js`; remote instances on hosts with
`config-backups`, hidden otherwise, like `#idleModule`): a **Backups** module with the status
line (last backup, warning while failing), the per-VM override (interval in hours, number kept,
"host default" placeholders), the list (time, trigger, size, pinned), and buttons **Back up now**
(`POST …/request`), **Save to this PC** (newest or selected backup → `backup-save -BackupId`;
local instances: fresh `backup-save`), **Restore…** (selected backup → `backup-restore
-BackupId`), **Restore from file…** (file picker → `backup-restore -BackupFile`), **Export
backup key…** / **Import backup key…**. Local instances show only the file and key buttons. The
host-admin Config tab gets the `backups` section through the config schema.

**Companion:** the same panel; routes in `RemoteHostRoutes.cs`; lifecycle commands in
`LifecycleBuilder.cs`; the status poll and toast (3.7). **Extension:** the same panel, routes in
`remotehost.js`, actions in `lifecycle.js`, a warning message instead of a toast.

### 3.9 Trust model

The host stores only ciphertext, and a payload changed at rest no longer decrypts with its
backup's key. The host service is, however, the VM's hypervisor: it can read and change the
running VM's disk and memory, including anything the VM could use to sign backups. Integrity
against the host operator is therefore out of reach and not attempted; the encryption keeps the
stored backups private from everyone without a PC key, including later readers of the host's
storage.

## 4. Compatibility

- **Old host** (no `config-backups`): the guest gets no `backupDue`, `construct backup create`
  exits 4, the client keeps today's flows, the panel module stays hidden.
- **Old VM** (no `construct backup`): the client falls back to today's export; the next
  reprovision installs the CLI and enrols the key.
- **Old client** with a new host and VM: scheduled backups fail with `no-recipients` until some
  client enrols a key; the status shows it.
- **Local VMs**: unchanged reinstall; manual file actions available.

## 5. Tests

| Package | Tests (all must run on this Linux VM) |
|---|---|
| Guest | `test/construct-backup.test.sh` (stub curl like `construct-vm.test.sh`, real `age`/`openssl`/`zstd`; round trip create → container → restore with a generated RSA key; host upload path against the stub incl. failure → DELETE + report; space check skip; flock; recipients add/list/remove; fingerprint = openssl); extend `test/idle-report.test.sh` (backupDue starts the unit, not when active), `test/export-config.test.sh` (STAGE_DIR, online SQLite snapshot without stopping T3, reinstall reason still stops it, cleanup trap), `test/restore-config.test.sh` (zstd archive). Add the new and the touched suites to the `service` group of `test/run-local-checks.sh` and `bin/construct-backup.sh` to its `bash -n` list |
| Service | rotation unit tests (week/month boundaries, pinned, keep 1/2/3/4/10, coinciding slots); due logic unit tests; API tests (VM token vs user vs other owner, upload streaming incl. size cap and free-space abort with a fake probe, keys validation, list/status/settings, request → heartbeat → session clears it, restored unpins, delete); heartbeat reply shape incl. `vaultScrub` coexistence; cleanup (sessions, partial files, missing VMs); migration/schema version tests; `RouteCoverageTests`, `AuditCoverageTests`; installer tests where they assert settings |
| Client | `test/backup-keys.test.ps1` (pwsh: key creation, SPKI PEM equals `openssl`'s, fingerprint, unwrap of an `openssl`-wrapped identity, export/import round trip, wrong passphrase, old keys kept); `test/backup-actions.test.ps1` (argument building and streaming helpers against a fake ssh); extend `test/remote-install.test.ps1` for the reinstall branches |
| UI | `extension/test` panel tests for the module (hidden without the feature, local vs remote buttons, override validation), `ui-smoke.js`, regenerated parity fixtures, Companion tests for the routes, lifecycle commands and the toast de-duplication |

## 6. Documentation

`docs/backup-restore.md` (rewrite: host backups first, then PC files, then the local-VM
export), `docs/remote-host.md` (a section on backups), `docs/proxmox-host.md` (the directory),
`docs/control-panel.md` (the Backups module), `service/README.md` (routes, settings, host config
section), `docs/local-checks.md` if suites move between groups. Docs state results in their own
words; no personal names, host names or addresses.

## 7. Work packages

| Package | Owns | Depends on (contract only) |
|---|---|---|
| **G — guest** | `bin/construct-backup.sh` (new), `bin/construct`, `bin/export-config.sh`, `bin/restore-config.sh`, `bin/construct-idle-report.sh`, `bin/provision.sh` (CLI install list), `bootstrap.sh` (packages), `bin/adopt-host.py`, `service/host/ConvertTo-ConstructHost.ps1` (helper list), guest tests, `test/run-local-checks.sh` (service group lines for guest suites) | 3.1–3.5 |
| **S — service** | `service/**` except `service/host/ConvertTo-ConstructHost.ps1`, `docs/proxmox-host.md`, `service/README.md`, `docs/remote-host.md` | 3.4–3.7 |
| **C — client scripts** | `lib/AgentVm.Backup.ps1` (new), `Provision-AgentVM.ps1`, `Auto-Install.ps1`, `lib/AgentVm.Remote.ps1` (feature probe helper), PowerShell tests, `docs/backup-restore.md` | 3.2, 3.3, 3.5, 3.8 |
| **U — UI** | `extension/**`, `companion/**`, `test/fixtures/companion-parity/**`, `docs/control-panel.md`, `docs/companion.md` | 3.5, 3.7, 3.8 (script actions) |

Each package works on its own branch from `feat/host-config-backups` and runs only its own
checks; the integrator merges the four and runs the affected groups once.
