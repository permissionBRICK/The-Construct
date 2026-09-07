# haus-pc host updater rollout — 2026-09-07

Host: `STANDPC`, reached through Jarvis relay target `haus-pc`. Service: `constructd`,
LocalSystem, `C:\Construct\service\publish\Constructd.Api.exe`. Scripts:
`C:\Construct`; database: `C:\ProgramData\Construct\service\constructd.db`.

The owner explicitly removed the server-only release-signing prerequisite. Releases
use the configured GitHub repository over HTTPS and retain immutable commit identity,
SHA-256 payload coverage, compatibility checks, maintenance and recovery. No release
signing secret or public key needs provisioning.

Final state: `10eb814cd49947f1d11f18f83d8ba3f8560c26a0` is installed and healthy.
The built-in updater completed a same-release reapply through a SYSTEM scheduled task;
this exercised actual backup, file replacement, SCM restart, pinned TLS health,
SQLite health and durable success recovery. It was not a rollback drill.

Relevant fixes:

- `13a2a1d`: removed signing from publishing, packaging, API, configuration and UI.
  GitHub release CI passed 1,235 service tests and published successfully.
- `c936040`: read-only database checks recognize legacy databases without
  `schema_migrations` as schema 0 after `PRAGMA quick_check` succeeds. CI passed 1,236 tests.
- `10eb814`: fixed the SYSTEM scheduled-task XML. `ServiceAccount` is not a valid
  [XML LogonType](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-logontype-simpletype).
  Keep the SYSTEM SID and omit that element. A harmless task using this XML was created,
  run as SYSTEM with exit result 0, queried and deleted on this host with `schtasks.exe`.

First manual bootstrap installed `13a2a1d`, preserved the production settings hash and
certificate, and migrated the database to schema 700. Independent preflight SQLite
checks confirmed a healthy legacy database and no active jobs. Existing `haus-vm`
remained Running; its SSH endpoint remained port 2201 and T3 HTTPS on port 2301 returned 200.
The API checked and staged `c936040` without any signing configuration. Its apply
failed before replacement because of the task XML bug; the service reopened normally.

Manual rollout rollback points (each includes service code, overwritten scripts and
complete service data; keep these protected because settings/data can contain secrets):

- `C:\Construct-rollout\13a2a1d6a0d818a1080aab0a64b6910e59175589\backup-20260907-194010`
  contains the original database before schema-700 migration.
- `C:\Construct-rollout\13a2a1d6a0d818a1080aab0a64b6910e59175589\backup-20260907-194126`
  contains the migrated database and previous service/scripts.

For continuity checks, capture `[long]$vm.Uptime.Ticks` before restarting the service.
Hyper-V VM objects expose a live uptime property: comparing two objects' uptime getters
can falsely report a decrease because the second getter runs later. The first rollout
was automatically rolled back by that false check; VM uptime and event checks confirmed
no VM restart. The retry with a numeric snapshot passed.

A manual rollout following a failed pre-replacement handoff must archive that handoff
while the service is stopped and the updater lock is held, after verifying its matching
closed fence and the absence of a replacement record. Preserve the closed fence against
late task execution. Otherwise an unrelated new binary sees the stale handoff as an
interrupted update. Never archive an active or partly applied handoff this way.

## Final Windows verification

After the launcher fix passed CI (1,236 tests), the manual bootstrap to `10eb814`
preserved the production settings hash and archived the closed failed-launch handoff.
Rollback backup:
`C:\Construct-rollout\10eb814cd49947f1d11f18f83d8ba3f8560c26a0\backup-20260907-194920`.

The service then checked, staged and applied the same `10eb814` GitHub release using
only its normal update API. Update ID `faf10f773865472b9c622798fd2727f6` completed
at 2026-09-07 17:51 UTC. `/host/updates/status` reported `succeeded`, phase `commit`,
no error and no recovery record. The durable `last-update.json` recorded complete
backup, replacement started, and one successful health attempt. The SCM process ID
changed from 12296 to 2216. `install.json` records the update and 431 owned files.

Final `/health`: `ok`, schema 700, maintenance null, expected commit and all six
features. `admin db check` returned `ok`, schema 700. Production settings remained
byte-identical. `haus-vm` remained Running with continuous uptime, its SSH endpoint
remained `standpc.dc.htl-sky.net:2201`, and T3 HTTPS on port 2301 returned HTTP 200.

One deferred display issue: the in-memory `ReleaseInfo` retains the installation
timestamp loaded before the updater commits the new `install.json`. On-disk metadata
and the persisted success result are correct. Tracked in Jarvis as
`personal/tasks/construct-host-update-metadata-refresh.md`.
