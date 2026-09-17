# Proxmox host self-update

Status: plan, 2026-09-17. Branch `feat/proxmox-backend` (PR #19). Implementation is
handed to a T3 Code thread; this document is the brief.

## 1. Goal

A Construct host running on a Proxmox node (`Constructd:Backend = proxmox`, Linux,
systemd) updates itself from the GitHub host release exactly like the Windows host
does: the admin opens the extension's **Maintenance** tab, clicks *Update*, the service
stages and verifies the package, drains, hands off to a privileged one-shot updater,
which swaps the files, restarts `constructd.service`, health-checks the new binary
and commits, or rolls back on failure. Today the tab is hidden on Proxmox (no `updates`
feature) and the only update path is re-running `service/host/install-construct-host.sh`.

Non-goals: changing the Windows updater's behaviour (its tests pin it), changing
`install-construct-host.sh`'s role (it stays the install/repair path), signing.

## 2. What exists (read these first)

The whole flow is platform-neutral except three pieces. Read in this order:

| Piece | Where | Platform-specific? |
|---|---|---|
| Contracts (`IReleaseSource`, `ReleaseManifest`, `IUpdateStager`, `UpdateHandoff`, `IUpdaterLauncher`, `RecoveryRecord`, fences) | `service/src/Constructd.Core/Abstractions/IReleaseSource.cs` | no |
| Job: stage → drain → handoff | `service/src/Constructd.Api/Jobs/HostUpdateJob.cs` | two lines (see §4.5) |
| Recovery/reconcile loop, resolve, `VerifyInstalledAsync` | `service/src/Constructd.Api/Hosting/UpdateRecoveryService.cs` | no |
| Release source (manifest fetch, allowlist) | `service/src/Constructd.Windows/Updates/GitHubReleaseSource.cs` | asset-name pins only |
| Stager (download, zip rules, SHA256SUMS bijection, `verified.json`) | `service/src/Constructd.Windows/Updates/PackageStager.cs`, `service/src/Constructd.Core/Logic/{ManifestRules,ZipEntryRules,UpdateCompatibility}.cs` | asset/updater-name pins only |
| File helpers, locks | `service/src/Constructd.Windows/Updates/{UpdateFiles,FileHostLock}.cs` | no (plain .NET, `FileShare.None` = `flock` on Linux) |
| **Launcher** (Task Scheduler) | `service/src/Constructd.Windows/Updates/ScheduledTaskUpdaterLauncher.cs` | **yes** |
| **Updater** (runs as SYSTEM after the service stops) | `service/host/Update-ConstructHost.ps1` | **yes** |
| **Packager** | `service/host/New-ConstructHostPackage.ps1`, `.github/workflows/host-release.yml`, `scripts/publish-construct-release.py` | the Linux branch is a stub (§4.1) |
| Proxmox stub | `service/src/Constructd.Proxmox/NoUpdaterLauncher.cs`, `service/src/Constructd.Api/Composition/UpdateComposition.cs:30-31`, `service/src/Constructd.Api/Composition/ReleaseInfo.cs:17-20` | replace |
| Extension | `extension/src/hostadmin.js` (`FEATURE_NAMES`, tab gated on `updates`), `hostupdate.js` | no change |
| Existing tests | `service/tests/Constructd.Tests/Updates/*.cs`, `service/tests/host-updater.test.ps1`, `test/host-package.test.sh` | extend, do not weaken |
| Docs | `docs/host-release.md`, `docs/proxmox-host.md` §"Updating", `service/README.md` | update |

Key contract facts the Linux side must honour byte-for-byte, because the recovery
service and the extension read them:

- On-disk files under `<dataDir>/updates/` (`/var/lib/constructd/updates`): `handoff.json`,
  `fence.json`, `last-update.json`, `updater.lock`, `backup-<updateId>/`, `<updateId>/{manifest.json,package.zip,extracted/,verified.json}`. JSON is camelCase, enums camelCase (`UpdateFiles.Json`).
- `last-update.json` = `RecoveryRecord`: phases `stop, backup, replace, start, health, commit, rollback`; outcomes `succeeded, applyFailed, rolledBack, rolledBackWithDatabase, recoveryFailed`; `replaceStarted` flips **before** the first file copy; `backupComplete` after `backup-complete.json`.
- Fence dispositions `closed | commitOnly | rollbackAuthorized`, scoped to one update id; the updater re-reads record + fence at every phase change (a fence can revoke mid-flight).
- Exit codes: 0 success/terminal/rollback done, 1 failure, 4 superseded by a closed fence.
- Failure codes allowlisted in `Get-UpdateFailureCode` (`update-health-failed`, `rollback-health-failed`, `old-service-health-failed`, `installation-mixed`, `backup-incomplete`, `backup-hash-mismatch`, `invalid-health-endpoint`, `admin-rollback`, else `updater-step-failed`).
- Locks: `<dataDir>/updates/updater.lock` (taken first, exclusively, for the whole run) and `<dataDir>/admin.lock`. The service probes them with `FileHostLock.IsHeldByAnotherProcess`, which on Linux is an advisory `flock`. A bash updater therefore takes them with `flock -n` on the same paths; nothing else interoperates.
- Health: `GET https://127.0.0.1:<port>/api/v1/health` with `Authorization: UpdateHandoff <healthToken>`, TLS pinned to `certificateThumbprint` (SHA-1 of the leaf, 40 hex), accepted only when `status == "maintenance"`, `commit == <new>`, `schemaVersion >= manifest.database.schemaVersion`; then `<adminCliPath> admin db check --json` (`DOTNET_ENVIRONMENT=Production`) must say `status == "ok"` with the same schema.
- Path mapping: `service/…` → `publishDir` (`/opt/construct/host`), `scripts/…` → `scriptsDir` (`/opt/construct/scripts`). Preserved names (`ZipEntryRules.IsPreserved`) are never written or deleted. The Windows name rules in `ZipEntryRules.IsSafe` stay enforced on Linux (pinned by `UpdateTests.cs`).

## 3. Decisions

1. **One Linux payload, same layout as Windows.** `construct-host-<commit7>-linux-x64.zip` contains `service/**` (self-contained linux-x64 publish), `scripts/**` (the same `git ls-files` subset as the Windows package plus `scripts/.construct-revision`), `updater/update-construct-host.sh`, and an in-zip `SHA256SUMS`. The bare publish zip goes away; `install-construct-host.sh` already unwraps a `service/` subtree (`stage_from_zip`, line ~216).
2. **Manifest keys** (all additive, Windows fields untouched): `linuxAsset, linuxSha256, linuxSizeBytes, linuxSumsSha256, linuxUncompressedSizeBytes, linuxUpdaterPath ("updater/update-construct-host.sh"), linuxUpdaterSha256`. The Linux build is self-contained only; there is no framework-dependent Linux variant.
3. **Stager variant `"linux"`.** `PackageStager` stays the single stager (it is injected as a concrete class). `SelectSourceAsync` returns `"linux"` when `!OperatingSystem.IsWindows()`; check/stage fail with a coded `no-linux-asset` when the manifest has no Linux keys. `StagedUpdate.Source` and `verified.json.source` carry `"linux"`.
4. **Launcher = transient systemd unit.** `SystemdUpdaterLauncher` in `service/src/Constructd.Proxmox/Updates/` mirrors `ScheduledTaskUpdaterLauncher` one-for-one (same root, file names, `PrepareAsync` ordering, `TryWriteFenceAsync` rules, `ReadOwnHandoffAsync`), replacing only the launch: `systemd-run --unit construct-host-update-<updateId> --collect --quiet --property=KillMode=process /bin/bash <stagedPath>/extracted/updater/update-construct-host.sh --handoff <root>/handoff.json [--resume]`. A transient unit is its own cgroup, so it survives `systemctl stop constructd`; `--collect` removes a failed unit so the name can be reused; a still-running unit of the same name makes `systemd-run` fail, which maps to `updater-launch-failed` like the Windows `/Run` failure.
5. **Updater = bash + python3** (`service/host/update-construct-host.sh`). python3 is a hard installer requirement on the node already; it does the JSON, the SHA-256 verification and the pinned health request (`ssl` with `CERT_NONE` plus a manual SHA-1 compare of the DER leaf against `certificateThumbprint`; `curl` cannot pin SHA-1). Service control is `systemctl stop/start constructd` with `systemctl is-active` polling up to 120 s. The `Install-ConstructHost.ps1 -AclOnly` step becomes: `chown -R root:root`, `chmod 0755 <publishDir>/Constructd.Api`, `chmod 0600 <scriptsDir>/keys/bootstrap_ed25519` (the same bits the installer sets). "Delete/disable the scheduled task" becomes a no-op.
6. **Thumbprint from the PFX.** The Linux installer configures `CertPath`/`CertPassword`, never `CertThumbprint`, and `HostUpdateJob.ApplyAsync` refuses without one (`update-health-pin-required`). On non-Windows, `HostUpdateJob` loads the certificate from `CertPath`/`CertPassword` at handoff time and uses its `Thumbprint`. No new option.
7. **Ledger from day one.** `install-construct-host.sh` writes `<HOST_DIR>/install.json` (`InstallRecord`: `source:"installer"`, `commit`, `packageVersion`, `installedAt`, `files:[{path,sha256}]` for `service/**`, and for `scripts/**` only when installed from a release package) so the first self-update has a ledger and the "first update without ledger" fallback scan is not exercised on Linux. `ReleaseInfo` already reads it.
8. **Feature flag.** `ReleaseInfo.ApiFeatures` adds `"updates"` for Proxmox. That alone shows the Maintenance tab; the routes and the guided flow are platform-neutral.
9. **Not in scope:** linked clones, master images, the memory-pressure save policy, changing zip name rules, a separate Linux stager class.

## 4. Work, in order

Each step leaves the tree building and the suites green (`dotnet test service/tests/Constructd.Tests`, `pwsh service/tests/host-updater.test.ps1`, `bash test/host-package.test.sh`, `bash test/run-local-checks.sh`).

### 4.1 Packager and release (`New-ConstructHostPackage.ps1`, workflow, docs)

- Refactor the payload build (lines ~51-90) into a function taking the publish dir, the updater file name and the RID, so it runs twice: win-x64 (unchanged output, byte-identical manifest keys) and linux-x64 with `updater/update-construct-host.sh` and `service/Constructd.Api` mandatory.
- `-LinuxPublishDir` (lines ~105-117): emit the seven `linux*` keys of §3.2; keep refusing preserved files and links; keep appending the asset line to the release `SHA256SUMS`.
- `.github/workflows/host-release.yml` and `scripts/publish-construct-release.py`: no logic change expected; confirm the new asset name and keys flow through, and that `scripts/package-construct-release.py` does not filter keys.
- `test/host-package.test.sh`: assert the Linux zip has `service/Constructd.Api`, `scripts/bin/provision.sh`, `updater/update-construct-host.sh`, an in-zip `SHA256SUMS` covering every entry, and that the manifest keys match the archive (hash, size, uncompressed total, updater hash).
- `docs/host-release.md`: replace the "bare publish zip" paragraph with the new layout.
- Audit which paths under `ScriptsDir` the Proxmox platform touches (`grep -rn ScriptsDir service/src/Constructd.Proxmox service/src/Constructd.Api/Composition/ProxmoxComposition.cs`, cloud-init/bootstrap references); every one must be inside the package's `scripts/` subset. Extend the subset for both platforms if something is missing.

### 4.2 Manifest, rules, source, stager (C#)

- `ReleaseManifest`: add the seven nullable Linux properties.
- `ManifestRules.ValidateVariants`: when `LinuxAsset` is present, require the exact name `construct-host-<commit7>-linux-x64.zip`, 64-hex hashes, positive sizes, `LinuxUpdaterPath == "updater/update-construct-host.sh"`. `Validate` stays as is (win-x64 remains the primary asset; a manifest without Linux keys is still valid).
- `ZipEntryRules.IsPayloadFile`: also accept exactly `updater/update-construct-host.sh`.
- `GitHubReleaseSource.ListHostReleasesAsync`: add the Linux asset (URL + declared size) to the `ReleaseDescriptor.Assets` and the download allowlist when present.
- `PackageStager`: `SelectSourceAsync` → `"linux"` on non-Windows (throw `UpdateException("no-linux-asset")` when the keys are missing, surfaced by `CheckReleaseAsync` as a compatibility reason); `StageAsync`/`ExtractAndVerifyCore`/`VerifyFiles`/`VerifyStagedAsync` pick the Linux asset, hash, sums hash, uncompressed total and updater path/hash for that variant. The free-space check should measure `UpdatesDir` and `AppContext.BaseDirectory` with `DriveInfo(path)` (not `GetPathRoot`) so separate mounts are measured correctly; keep the `2×payload + 1 GiB` rule.
- Tests (`PackageTests.cs`, `FrameworkDependentTests.cs` style): a Linux fixture zip stages, verifies, detects tamper, refuses a missing `SHA256SUMS`; a manifest with a wrong Linux asset name or updater path fails `ValidateVariants`; `SelectSourceAsync` picks `"linux"` on Linux and never on Windows; the existing Windows assertions stay untouched. `Local_packager_without_signing_passes_the_production_extractor` gets a Linux twin that runs the real packager with `-LinuxPublishDir` and feeds the result to the stager with the Linux variant.

### 4.3 Launcher (`Constructd.Proxmox/Updates/SystemdUpdaterLauncher.cs`)

- Implement per §3.4. Reuse `UpdateFiles` and `FileHostLock` verbatim. Reject control characters and quotes in paths (`invalid-update-path`) like the Windows class. Never log or persist the health token anywhere except `handoff.json` (0600, root).
- `UpdateComposition`: `IsProxmox` → `SystemdUpdaterLauncher(IProcessRunner, IHostLock, dataDir)`. Delete `NoUpdaterLauncher.cs`.
- Tests: exact `systemd-run` argv (unit name derived from the update id, `--resume` only on resume, no token in argv), launch failure removes nothing and throws `updater-launch-failed`, `PrepareAsync` refuses when a closed fence plus `replaceStarted` exist, fence/handoff/record readers use the same file names as Windows. Add an interop test: `FileHostLock` holds `updater.lock`, `flock -n <path> true` must fail; and the reverse (`flock` held by a child process → `IsHeldByAnotherProcess == true`). Skip that test on Windows.

### 4.4 Updater script (`service/host/update-construct-host.sh`)

Port `Update-ConstructHost.ps1` phase by phase; keep function names recognisable (`get_update_authority`, `test_update_manifest`, `get_update_target`, `set_phase`, `assert_update_backup`, `test_update_health`, `get_update_failure_code`, `get_update_rollback_mode`). Requirements:

- `--handoff <path>` required; `--resume`, `--rollback`, `--library-only` (source the functions for tests, like `-LibraryOnly`).
- `set -euo pipefail`; every write of `last-update.json`/`fence.json`/`install.json` is temp + `mv` in the same directory; log lines append to `<root>/updater.log`.
- Refuse symlinks on every ancestor of handoff, root, publishDir, scriptsDir, dataDir, stagedPath (`Assert-UpdateNoLinks`).
- `flock -n` on `updater.lock` first, exit 4 on a closed fence for this id, then `admin.lock`.
- Re-verify the staged package from scratch (manifest identity, zip size/hash against the Linux keys, entry lengths against `extracted/`, `SHA256SUMS` hash and full coverage against `verified.json.files`, updater path/hash) before stopping the service. A bad package never stops the service.
- Backup: reuse a complete backup after rehashing; copy the ledger's files, `install.json` → `previous-install.json`, the SQLite db plus `-wal`/`-shm`; write `files.json` and `backup-complete.json`.
- Replace: set `replaceStarted`, copy with `install -m` preserving the source mode, delete owned files not in the new set, apply the mode bits of §3.5.
- Start, health (§2), commit (`install.json` with `source:"release"`, prune to the two newest complete backups and remove other staged dirs), outcome `succeeded`, exit 0.
- Failure before replace: restart the old service, health against `previousCommit`, `applyFailed` + closed fence, exit 1. Rollback path and `recoveryFailed` with the three manual steps exactly as the PowerShell does (the manual steps may mention `systemctl` instead of the SCM).
- `chmod 0755` the script inside the package is not required; the launcher runs it through `/bin/bash`.
- Tests: `service/tests/host-updater.test.sh` (bash, runs on any Linux), mirroring the scenario list of `host-updater.test.ps1` with stubbed `systemctl`, `systemd-run`, the health function and a fake `admin db check`. Cover at minimum: success with stale owned file removed and unowned file, settings, keys and db untouched; terminal-resume no-op; closed fence → exit 4; backup failure → `applyFailed` + old service restarted; health failure → rollback; database rollback mode; incomplete backup after replace → `recoveryFailed`; authorized rollback resume; `commitOnly` resume; bad manifest never stops the service; every JSON file produced deserialises with `UpdateFiles.Json` into `RecoveryRecord`/`UpdateFence`/`InstallRecord` (write one C# test that loads fixtures produced by the bash suite, or assert the exact key set in bash).
- Wire the suite into `test/run-local-checks.sh` next to the PowerShell one.

### 4.5 Job, feature flag, installer

- `HostUpdateJob`: `AdminCliPath` = `Constructd.Api.exe` on Windows, `Constructd.Api` otherwise; on non-Windows derive `CertificateThumbprint` from `CertPath`/`CertPassword` when `CertThumbprint` is empty (fail with `update-health-pin-required` if the PFX cannot be read). `ServiceName` stays `constructd`.
- `ReleaseInfo.ApiFeatures`: add `"updates"` for Proxmox; update the doc comment.
- `install-construct-host.sh`: write the ledger of §3.7 after the rsync (a python3 heredoc, the file already uses one for `appsettings.Production.json`); `stage_from_zip` keeps accepting both layouts; when installing from a release package, also lay down `scripts/**` from the zip instead of the git checkout only if `--source` was not given (keep current behaviour otherwise, document it). The "Updating" section of `docs/proxmox-host.md` gets the Maintenance-tab path first and the installer path as repair.
- `service/README.md`: Proxmox platform section: self-update supported, how the launcher works, where the updater logs (`/var/lib/constructd/updates/updater.log`, `journalctl -u construct-host-update-<id>`).
- Recovery tests (`RecoveryTests.cs`, `UpdateFlowTests.cs`) already run with fakes on Linux; add one flow test that runs the composition with `Backend=proxmox` (see `ProxmoxCompositionTests` for the pattern) and asserts `updates` is advertised and the launcher resolves to `SystemdUpdaterLauncher`.

## 5. Acceptance

- `dotnet test`, `pwsh service/tests/host-updater.test.ps1`, `bash service/tests/host-updater.test.sh`, `bash test/host-package.test.sh`, `bash test/run-local-checks.sh` all green on Linux; Windows-specific assertions unchanged.
- A release built by the workflow contains the Linux payload of §3.1 and a manifest with the seven keys; `install-construct-host.sh --host-release latest` still installs from it.
- `docs/host-release.md`, `docs/proxmox-host.md`, `service/README.md` describe the new behaviour; no doc still calls the Linux asset a bare publish zip.
- Field test (done by a human on the test node, not by the implementing agent; the node is a shared lab machine): install the branch build, publish a test release or point `--host-release` at one, open the Maintenance tab, run *Update*, watch `journalctl -u constructd -u 'construct-host-update-*'` and `/var/lib/constructd/updates/updater.log`; expect `succeeded`, the tab shows the new commit, VMs untouched. Then break the health check (wrong `certificateThumbprint` in a hand-edited handoff is enough) and expect a clean rollback with the old commit serving.

## 6. Rules for the implementing agent

- Work on `feat/proxmox-backend` (or a branch off it, and say so). Commit in small steps with the noreply author already configured in the repo; never put any other email in commits or files.
- Do not run the installer or the updater on any real host, and do not touch `test-proxmox`; the suites and fakes are the verification here.
- Keep the Windows updater and its tests byte-for-byte; extend, do not fork, the shared C# classes.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation in the final report.
