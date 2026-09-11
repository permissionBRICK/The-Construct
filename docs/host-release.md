# Host releases and deployment

Construct uses one published commit for source, the control panel, and the Windows
host. Every push to `main` runs `.github/workflows/host-release.yml`: build the
self-contained Windows x64 executable, package its scripts and the source archive,
generate the manifest, then publish. **No tests run in GitHub Actions.** Run the
regression checks locally before pushing. The ISO builder retains its separate
`config/iso-builder.json` dependency pin.

The per-user tray app ships independently as `companion-<commit>`; see
[Construct Companion](companion.md) for its installer, package layout and release gates.
Its release is published with `--latest=false` so the complete Construct manifest
remains at the latest-release URL. Companion discovery still selects its own tags.


The immutable tag remains `host-<40-character commit>` for compatibility. Its assets
are `construct-host-<commit7>-win-x64.zip`, `construct-source-<commit40>.zip`, and
`manifest.json`. Both the panel and Windows host discover the current release at:

```
https://github.com/permissionBRICK/The-Construct/releases/latest/download/manifest.json
```

This direct asset download does not use GitHub's REST API quota. The manifest
includes the repository, main ref, commit, immutable tag, build time, package
version, both archive names, sizes and SHA-256 hashes, plus host database/config
compatibility. Clients validate the identity and use `/releases/download/<tag>/...`
for subsequent downloads, even when another release becomes latest mid-update.
Installed source markers come from the downloaded revision or local checkout, never
from a subsequent remote HEAD lookup. The panel says "update available" without a
commit count. A hash difference also includes a local development build that differs
from the published release; it does not establish which commit is newer. Explicit
non-`main` source refs use manual branch downloads and no automatic main-release offer.
Forks tracking `main` need their own complete releases using the same workflow.

Publication is serialized across main runs. Assets upload to a draft first; only a
complete release becomes public, then advances GitHub's latest pointer. Failed builds
leave the previous release current. Ancestry checks prevent an older queued run or
manual rerun from replacing a newer release. After a history rewrite, only the current
main tip may replace an unrelated latest commit. Published assets are never overwritten
on retry. GitHub may coalesce pending runs during rapid pushes; clients get the newest
successfully published commit rather than every intermediate push.

The host ZIP contains `service/`, `scripts/`, `updater/Update-ConstructHost.ps1`, and
`SHA256SUMS`; the detached manifest avoids a circular ZIP hash. Stored compression
guarantees the host extraction ratio bound. `SHA256SUMS` covers every host payload
file. The source ZIP uses compressed tracked files and carries `.construct-revision`.
The control panel packages its VSIX locally without Node or dependency installation.
Releases never include live settings, data, private keys, or the separately downloaded
ISO executable. Agent tool version checks (Codex, OpenCode, T3 nightly) are independent
and can still use their upstream APIs.

Host updates use GitHub Releases over HTTPS, matching the other Construct update paths.
No signing keys or signing secrets are required on hosts, development VMs or CI.
Production downloads use the host-local `Constructd:HostAdmin:Updates:Repository`
(default `permissionBRICK/The-Construct`); API configuration cannot redirect that source.
The manifest identifies the repository, main ref and immutable commit tag. SHA-256
checks cover the ZIP and every payload file; these checks detect corruption, while
authenticity relies on HTTPS and control of the configured GitHub repository.

Release-source failures report safe diagnostic codes: `release-source-rate-limited`,
`release-source-http-<status>`, DNS/TLS/proxy/connection failures, timeout, invalid
metadata, or asset size mismatch. Check responses include the upstream HTTP status
and GitHub's rate-limit reset time when supplied. Raw response bodies, exception
messages and signed asset URLs are never exposed. Staging failures retain their
actual phase (`check`, `download`, or `verify`). Older builds labelled every staging
failure `verify`; a blank commit in their failed update row means release selection
did not complete, not that ZIP verification failed.

To check release connectivity, run on the affected host:

```powershell
curl.exe -fsSL -D - -o NUL "https://github.com/permissionBRICK/The-Construct/releases/latest/download/manifest.json"
```

Older clients still query the REST release list until updated once. This checks the
interactive account's network path; the Windows service can have different proxy
or certificate settings. Successful access from a development machine does not
establish access from the affected host. A failure during staging does not replace
the service or interrupt VMs.

Production apply requires `Constructd:CertThumbprint` for the updater's loopback health
pin. A host configured only with `CertPath` is refused with
`update-health-pin-required` before drain or replacement; configure a supported
certificate-store thumbprint before using host updates.

For a local package, publish to a new directory, then invoke:

```powershell
.\service\host\New-ConstructHostPackage.ps1 -PublishDir C:\Temp\publish `
  -OutputDir C:\Temp\host-release -Commit <commit40>
python scripts/package-construct-release.py --output C:\Temp\host-release --commit <commit40> --repository permissionBRICK/The-Construct
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
existing `host-<commit>` release to replace its assets. Explicit recovery pins fetch
the manifest directly from that immutable tag, with no release-list window. Old
host-only tags remain usable as explicit pins: the updater obtains their payload
size through an HTTPS HEAD request when the old manifest omits it.

Recovery files are under `<DataDir>\updates`: `handoff.json` (contains a one-time health
credential; do not copy it into logs), `last-update.json`, `fence.json`, the staged files,
and `backup-<updateId>`. Read `last-update.json` when the service cannot start. It records
the failed phase, complete-backup flag, replacement flag, health attempts and manual
steps. Failure details are allowlisted diagnostic codes; arbitrary PowerShell exception
messages are deliberately excluded because dependencies can include secrets. Never rebuild an incomplete backup from an installation whose replacement began.

Task creation uses a Task Scheduler XML file, avoiding the `schtasks /TR` length limit
on default-layout resume commands. The XML contains no health credential.

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
SQLite persistence, in-process HTTP integration tests, and package fixtures.
`Test-UpdateHealth` is fully replaced in the PowerShell suite; its real HTTP, certificate
and read-only CLI calls are not exercised there. Authorized rollback and `commitOnly`
resume control flow are exercised with those fakes. SCM survival, LocalSystem ACLs, real certificate pinning, running Hyper-V VM continuity,
and a Windows-client reconnection remain items for the contract's field-test checklist.

Phase 6 validation on Linux: .NET build **0 warnings / 0 errors**, **932 / 932**
tests; **24 Node suites**, **19 PowerShell suites**, and **21 Bash suites** passed.
The new updater suite has 49 assertions, installer/bootstrap suite 14, and
packaging fixture 87. The Bash total includes the frozen-contract compile check
(5 checks) and fake-service end-to-end (38 checks). Existing platform-specific
skips remain. A self-contained `win-x64` cross-publish and complete payload hash
verification also succeeded; the executable was not run on Windows.

Final-review packaging limits: the local packager labels current tracked script content
with the supplied commit without independently proving a clean, matching checkout; use
the guarded main-branch workflow for production. `minInstalledCommitDate` is emitted but
not enforced against an installed commit date. Preserved-name collisions are rejected
by the updater at apply, rather than during stage. A drain-state race can surface as a
failed operation instead of a dedicated conflict response. These remain follow-up items;
Linux package fixtures do not establish production provenance or Windows update health.

### Source reuse for remote reprovisioning

Hosts cache `construct-source-<commit40>.zip` from the immutable `host-<commit40>` release,
verified against `sourceSha256` and `sourceSizeBytes`. Keep the source asset and its manifest
available: newly provisioned hosts and an admin-deleted entry need to download them again.
Ready entries are preserved indefinitely until explicitly deleted or found corrupt.
`install.ps1` and `Update-Construct.ps1` also write a per-file source hash manifest outside the
checkout at `%LOCALAPPDATA%\The-Construct\source-manifests\<commit40>.sha256`. This proves an
archive install still matches that release before the client selects the remote cache;
a best-effort manifest-write failure leaves installation successful and uses upload fallback.
