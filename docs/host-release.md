# Host releases and deployment

Host releases use the immutable tag `host-<40-character commit>` in
`permissionBRICK/The-Construct`. The workflow runs only on `main`, tests the service,
publishes the self-contained Windows x64 executable, and packages the matching tracked
host scripts. The ISO builder retains its separate `config/iso-builder.json` pin.

The three release assets are `construct-host-<commit7>-win-x64.zip`, `manifest.json`,
and its binary Ed25519 signature `manifest.json.sig`. The ZIP contains `service/`,
`scripts/`, `updater/Update-ConstructHost.ps1`, and `SHA256SUMS`; the manifest is detached
so its ZIP hash is not circular. The manifest records the main commit, build time,
package version, hashes, and database/config compatibility. ZIP entries use stored
compression to guarantee the extraction ratio bound, including unusually compressible
publish output. `SHA256SUMS` covers every payload file. Releases never include live
settings, data, private keys, or the separately downloaded ISO executable.

The owner must provision an Ed25519 PEM private key as `HOST_RELEASE_SIGNING_KEY` in the
GitHub **host-release** environment and commit the corresponding base64 32-byte public
key in `config/host-release.pub`. That file is intentionally empty in this delivery:
no production key was supplied. Publishing fails closed unless the secret's public key
matches that file. The installer seeds a nonempty key into bootstrap configuration;
startup copies it into a missing `host_config.updates` section without overwriting an
existing stored key. Updates require that stored key. Key rotation requires updating
host configuration before publishing with the new key. No private signing key belongs
in the repository or on a host.

For a local package, publish to a new directory, then invoke:

```powershell
.\service\host\New-ConstructHostPackage.ps1 -PublishDir C:\Temp\publish `
  -OutputDir C:\Temp\host-release -Commit <commit40> -SigningKeyPath C:\Secure\release.pem
```

The packager uses the current worktree's tracked scripts. A production package must
therefore run from a clean checkout of the supplied commit; the Actions workflow does
that automatically. A test fixture executable is sufficient for the Linux layout test;
it is not a runnable Windows package.

**First rollout needs a manual installation.** Existing hosts cannot update themselves
until a version containing this API, maintenance protocol, independent updater, and
`admin db check` has been installed. Publish this version for `win-x64`, deploy its
matching scripts and service manually with the host owner's explicit approval, and
preserve/reuse the host's existing installer settings. Do not rerun the installer with
default parameters to perform an update. No installation or service restart on a real
host was performed in this delivery.

Host storage keeps the two most recent complete backups and the staged package for the
current update. Backups are pruned only after successful health verification and durable
commit. Keep published GitHub host releases indefinitely by default; if repository
storage requires manual pruning, keep at least the newest 20 releases and every release
still installed or referenced by an outstanding staged update. Never delete/rewrite an
existing `host-<commit>` release to replace its assets. The updater checks the newest 100
repository releases; pin an available `host-*` release or prune unrelated old releases
if host releases fall outside that window.

Recovery files are under `<DataDir>\updates`: `handoff.json` (contains a one-time health
credential; do not copy it into logs), `last-update.json`, `fence.json`, the staged files,
and `backup-<updateId>`. Read `last-update.json` when the service cannot start. It records
the failed phase, complete-backup flag, replacement flag, health attempts and manual
steps. Never rebuild an incomplete backup from an installation whose replacement began.

The task exclusively holds `updater.lock` and `admin.lock`. Successful outcomes are
immutable: every resumption checks them before stop/replace/rollback. A `commitOnly`
fence forbids rollback; a `closed` fence revokes all updater authority; a
`rollbackAuthorized` fence permits recovery while mutations stay frozen. After a
pre-replacement interruption that has been closed, apply again: this drains anew and
builds a fresh backup while retiring the old fence under the updater lock. A safely
closed pre-replacement interruption can also be cancelled, freeing staging for a
different release; the closed fence remains to revoke a delayed task. The service
allows 30 seconds after the launch command completes for the task to acquire its
lock before treating a missing updater as interrupted.
Administrative resolution uses the API's `commit`, `abort`, or `close` action and checks
binary identity and installed file hashes where required. After `abort`, resume the
interrupted operation to run the authorized rollback.

Automatic binary rollback retains the database for additive migrations. A package that
requires a newer database reader or declares a breaking migration also requires the
complete database backup. Settings are never rewritten; required new settings must be
added by an administrator before staging. Rollback is only complete after restored-file
verification, service startup, loopback health authentication, and a read-only SQLite
`quick_check` succeed. A failed recovery remains in maintenance; it is never reported
as a successful rollback.

Linux validation uses PowerShell service/health fakes, recording process runners,
SQLite persistence, in-process HTTP integration tests, and signed package fixtures.
SCM survival, LocalSystem ACLs, real certificate pinning, running Hyper-V VM continuity,
and a Windows-client reconnection remain items for the contract's field-test checklist.

Phase 6 validation on Linux: .NET build **0 warnings / 0 errors**, **932 / 932**
tests; **24 Node suites**, **19 PowerShell suites**, and **21 Bash suites** passed.
The new updater suite has 49 assertions, installer/bootstrap suite 14, and signed
packaging fixture 87. The Bash total includes the frozen-contract compile check
(5 checks) and fake-service end-to-end (38 checks). Existing platform-specific
skips remain. A self-contained `win-x64` cross-publish and complete payload hash
verification also succeeded; the executable was not run on Windows.
