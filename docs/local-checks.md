# Local checks and GitHub builds

GitHub Actions is for producing deliverables: release packages, tools, installers
and other usable build artifacts. Regression tests run locally. Do not add
standalone CI workflows or upload test reports merely to make them count as
artifact-producing workflows. The host release workflow builds, packages and
publishes the Windows host release; validate relevant changes before publishing.

The former panel, T3 provisioning, native ISO integration and host-release test
commands are preserved in local runners. They do not restart live T3 services.

On Linux, install Node.js, Python 3, jq, PowerShell (`pwsh`) and the .NET 10 SDK.
The gateway tests require aiohttp for `/usr/bin/python3` (on Ubuntu:
`apt-get install python3-aiohttp jq`). The `ui` group installs its own browser for
its screenshots (`npx playwright install --with-deps chromium` in `ui/` once).

From the repository root, choose the checks relevant to the change:

```sh
bash test/run-local-checks.sh panel
bash test/run-local-checks.sh t3
bash test/run-local-checks.sh service
bash test/run-local-checks.sh engine
bash test/run-local-checks.sh release
bash test/run-local-checks.sh ui
bash test/run-local-checks.sh agent
bash test/run-local-checks.sh e2e
bash test/run-local-checks.sh guest
# Several at once:
bash test/run-local-checks.sh engine agent
# Only some test classes of the .NET suites (a class, a part of its name, or an engine test folder):
bash test/run-local-checks.sh engine:Vault,BackupApiTests agent:Upgrade
# Every group except guest, e2e included:
bash test/run-local-checks.sh all
# The groups of the paths changed since the base branch (origin/nightly, origin/rework or
# origin/main, whichever exists first; or name one):
bash test/run-local-checks.sh --changed [<base>]
# The quick tier: the same groups, but only the tests those paths reach (below):
bash test/run-local-checks.sh --quick [<base>]
```

Run only the checks for the features a change affects, in three tiers:

- **While working on a small fix:** `--quick`, or a group with test filters (`engine:Vault`).
  Usually a few seconds to a minute or two.
- **Before pushing or opening a pull request:** `--changed`, the whole groups the change touches.
- **For integration merges and release preparation:** `all`. It is the only tier that runs the
  `e2e` group; `scripts/release.py` runs it before a release.

Every step is timed. A failing step does not stop the run: the summary at the end lists each step
with its time and result, and the exit code is 1 when any step failed. A step two selected groups
share (the engine build, the host updater's tests) runs once.

`--changed` maps each touched path (committed since the merge base, uncommitted or untracked)
to groups by the table below, which `test/helpers/changed-groups.py` reads. A path matching
several rows selects all their groups. A path matching no row selects `all`. In a pattern `*`
stays within one directory and `**` crosses directories. A row without a group selects nothing.

| Path | Groups |
|---|---|
| `extension/**` | `panel` |
| `bin/*t3*`, `test/t3-*`, `extension/vm/*t3*` | `t3` |
| `service/host/**`, `service/tests/*`, `service/tests/Constructd.Tests/**/*.ps1`, `service/src/Constructd.Windows/**/*.ps1`, `lib/**`, `bin/**`, `console-viewer/**`, `extension/src/projects.js`, `extension/vm/**`, `companion/**`, `test/isolate-env.test.sh`, `test/install-sdks.test.sh`, `test/shellcheck-changed.sh` | `service` |
| `service/src/**`, `service/tests/Constructd.Tests/**`, `service/openapi/**`, `service/Constructd.sln`, `service/host/**`, `test/dotnet/**`, `test/fixtures/vault-patterns.json`, `docs/plans/host-administration-contracts.md`, `bin/adopt-host.py`, `bin/construct`, `bin/construct-vm.sh`, `bin/construct-expose.sh`, `bin/construct-secret.sh`, `bin/construct-config.sh`, `bin/construct-idle-report.sh`, `bin/construct-backup.sh` | `engine` |
| `ui/**` | `ui` |
| `agent/**`, `test/agent-install.test.sh`, `test/agent-bootstrap.test.ps1`, `install.ps1`, `Update-Construct.ps1`, `scripts/package-agent.py`, `service/src/Construct.Keys/**`, `service/src/Constructd.Store/**`, `service/src/Constructd.Sync/**`, `service/src/Constructd.Api/**`, `service/openapi/**` | `agent`, `ui` |
| `extension/vm/**`, `lib/**`, `Update-T3Code.ps1`, `Get-ConstructT3PairingLink.ps1`, `test/fixtures/legacy-updaters/**`, `test/source-release.test.ps1`, `test/lifecycle-run.test.ps1`, `test/dotnet/**` | `agent` |
| `scripts/**`, `.github/**`, `VERSION`, `test/release.test.py`, `test/construct-release.test.py`, `test/run-local-checks.sh`, `test/helpers/changed-groups.py`, `test/helpers/test-areas.py`, `docs/local-checks.md`, `service/src/Constructd.Core/Logic/ReleaseChannel.cs`, `service/tests/Constructd.Tests/Updates/ReleaseChannelTests.cs` | `release` |
| `docs/**`, `*.md`, `assets/**` | |

For installers, also run the touched script's own tests.

`--quick` selects groups by the same table, never `e2e`, and within them:

- The engine and agent .NET suites run only the test classes the touched paths reach
  (`test/helpers/test-areas.py`): a changed test file selects its own classes; a changed source
  file selects the test classes whose file names one of its top-level types, and the classes and
  engine test folders named after a word of its file name (`VaultEndpoints.cs` reaches
  `Constructd.Tests.Vault` and every `*Vault*Tests`). The whole suite runs when a source file
  reaches nothing that way, when it is wiring every test depends on (`Program.cs`, the
  composition, the fakes, a project file, the test support folders), or when the selection is more
  than half the suite. A suite no touched path concerns does not run, nor does its build.
- The script groups (`panel`, `t3`, `service`, `release` and the agent group's PowerShell steps)
  run a test only when a touched path is that test, lies in a directory it names, or is named in
  it by its file name (`bin/construct-vm.sh` reaches `test/construct-vm.test.sh`); the others are
  listed as skipped.
- `contracts-compile.test.sh` does not run.

The quick tier is a heuristic for fast feedback. Run `--changed` before handing a change over.


The `guest` group runs the guest rig (`test/e2e/guest-rig.sh quick`). It is not part
of `all` and `--changed` never selects it; run it by name when guest-facing paths change
(`bin/provision.sh` and what it runs, `bootstrap.sh`, `bin/build-autoinstall-iso.sh`). It
provisions an Ubuntu guest with this checkout the way the old `Provision-AgentVM.ps1` did, checks
the result sentinel and exit code, reboots the guest, saves the guest manifest and removes
the guest. Run artefacts (provisioning log, manifest, timings) go to
`~/.local/state/construct-guest-rig/runs`, downloads to `~/.cache/construct-guest-rig`.
The rig has two backends:

- `--backend child` (the default) needs a primary VM that may create children
  (`construct vm identity`). It creates one Ubuntu child on the host (3 GB RAM, 2 cores,
  25 GB disk, 4-hour lifetime) from an autoinstall ISO and deletes it at the end. The
  first run downloads the Ubuntu ISO, builds the autoinstall ISO and uploads it to the
  host; later runs reuse both, but every run pays one unattended install. A host that
  refuses the child ends the group with `incomplete: required verification not run` and
  exit 2. The primary must be able to reach its children over the network.
- `--backend local` runs the guest under QEMU/KVM on this machine
  (`apt-get install -y qemu-system-x86 qemu-utils cloud-image-utils`, read-write
  `/dev/kvm`): the Ubuntu cloud image (checksum verified, about 600 MB), first booted once
  and kept as a prepared disk (about 60 MB more), then a throw-away overlay per run with a
  NoCloud seed carrying a key made for the run, and SSH forwarded to a port on
  `127.0.0.1`. `quick` gets 2 GB of memory, `full` 3 GB; when that much (plus 512 MB) is
  not available the run ends with `incomplete` and exit 2. The QEMU process is stopped
  at the end of every run, also on failure. The cloud image is not the installer's
  minimal server, so its manifest differs from a child's in the base packages.

For other tests that provision the guest themselves, `--backend local up [quick|full]`
starts a clean guest and prints `export` lines for `CONSTRUCT_E2E_SSH_HOST`,
`CONSTRUCT_E2E_SSH_PORT`, `CONSTRUCT_E2E_SSH_USER` and `CONSTRUCT_E2E_SSH_KEY` (the seed
user is `agent` with password `agent`, as on the autoinstall ISO);
`--backend local down` stops it and removes its disk and key:

```sh
eval "$(bash test/e2e/guest-rig.sh --backend local up)"
# ... ssh -i "$CONSTRUCT_E2E_SSH_KEY" -p "$CONSTRUCT_E2E_SSH_PORT" "$CONSTRUCT_E2E_SSH_USER@$CONSTRUCT_E2E_SSH_HOST"
bash test/e2e/guest-rig.sh --backend local down
```

`bash test/e2e/guest-rig.sh --help` lists the `full` variant, `--keep` and `--compare`.

The `agent` group builds the device agent with warnings as errors and runs its tests: several
agents in process against the real engine in fake mode on a loopback HTTPS port (setup, pairing,
recovery, the proxy, revocation, the event stream, the local API's token and origin rules, repair,
and the OpenAPI drift check), the device features with the platform faked (VM links and forwards,
notifications, the SSH config, self-update against a fake channel, "update everything"), builds
the Windows shell with Windows targeting, and runs the Linux install script's test. It also runs
the upgrade from an old install: `install.ps1` and `Update-Construct.ps1`, the old updaters of
classes E and F against this tree with the `lib/` functions they call, and the lifecycle lock.
It needs the .NET 10 SDK, python3 and pwsh. The agent builds on projects that came from the
Companion (`agent/src/Construct.Companion.*`) and embeds the guest scripts of `extension/vm`,
so changes there run this group too.

The `ui` group runs the client UI's unit tests (vitest), builds its static bundle
and compares its screenshots in headless Chromium (Playwright). After an intended
visual change, refresh the pictures with `npm run test:screens:update` in `ui/`.

The `engine` group builds `service/Constructd.sln` and runs the engine's .NET tests: the real
host (`Program`) in fake mode through `WebApplicationFactory`, in memory or on SQLite, one host
per test, plus the drivers, stores and rules on their own. It leaves out the tests marked
`[Trait("Category", "E2E")]`, which the `e2e` group runs, and compiles the frozen contract
blocks of `docs/plans/host-administration-contracts.md` (`test/contracts-compile.test.sh`).
Before this group existed the `service` group ran these tests; it now runs the scripts only.

The `service` group tests the guest's scripts and CLI, provisioning, the console viewer, the
host installer, packaging and updater, and the Hyper-V driver's PowerShell, each with its own
stubs; nothing in it talks to a real host. Its last step, `test/shellcheck-changed.sh`, checks
every shell script changed since the branch's base once: no new shellcheck diagnostics.

The `e2e` group runs what goes end to end on this machine: the engine's tests with real clients
(`HostAdminEndToEndTests`: the guest CLI in bash, node, curl and python against a real HTTPS
engine on SQLite), the tests against a real guest when `CONSTRUCT_E2E_SSH_*` names one (skipped
otherwise), and the UI on the real engine and two real device agents in headless Chromium
(`ui/tests/e2e`). It is part of `all`; `--changed` and `--quick` never select it. What a full
end-to-end setup with real hosts would add is in [plans/e2e-setup.md](plans/e2e-setup.md).

The `panel` group tests the VS Code extension under plain node with `vscode` faked
(finding, starting and installing the agent, the webview documents and their message
bridge, Remote-SSH, the register-workspace offer), the profile validator the VM's
`construct project set` loads from `extension/src/projects.js`, and the VM's
`project-set.js` helper.

## Keeping the suites fast

Typical times on the 8-core development VM, with other work running beside it:

| Group | Time |
|---|---|
| `panel` | a few seconds |
| `t3` | about half a minute |
| `service` | 3 to 4 minutes (its script tests run four at a time) |
| `engine` | about 2 minutes, plus a minute when it builds from scratch |
| `release` | about 1.5 minutes (the updater's suites) |
| `ui` | under a minute |
| `agent` | about 2 minutes |
| `e2e` | about 2.5 minutes |
| `all` | about 10 minutes |

Before these changes `all` took about an hour: the engine suite alone 18 to 26 minutes.

- An engine started in a test (`TestApp`, the agent tests' `EngineHost`) costs about a fifth of a second of
  CPU because `Constructd.Api`, `Constructd.Store` and `Construct.Agent.Core` generate their minimal-API
  request delegates at build time (`EnableRequestDelegateGenerator`). Without it every start compiled
  expression trees for the engine's 220-odd endpoints and the engine suite took five times as long. An
  endpoint the generator cannot handle is a build warning (RDG…), which the agent group's `-warnaserror`
  turns into an error.
- xUnit runs the classes of a suite in parallel and the tests of one class one after another. A class
  whose tests add up to more than half a minute holds up the whole run: split it, as `UpgradeTests`.
- A test class that cleans up asynchronously implements `IAsyncLifetime`. xUnit 2 never calls
  `DisposeAsync` on a class that is only `IAsyncDisposable`: its engines and agents keep running and its
  files stay behind. `TestClassDisposalTests` fails on such a class.
- Wait for the event, not for time: a `TaskCompletionSource` a fake completes, the job's event stream, or a
  poll whose generous bound only ends a broken run. A fake keeps its own delays short (the fake driver's
  state reads settle in half a second, not the fifteen a Proxmox host's cache needs).
- Make an expensive key once per class or per run (an RSA-3072 key takes most of a second); use P-256 where
  the key type does not matter.
- Write temporary files under `Path.GetTempPath()`. Each .NET test process points it (`TMPDIR`) at a
  directory of its own, on `/dev/shm` where there is one, so SQLite does not wait for the disk, and removes
  it at the end (`test/dotnet/TestTemp.cs`). Give every engine a database path of its own: the engine keeps
  adopted VMs' keys, its source cache and backups next to it, also in memory mode.
- A test that takes long by design because it runs real processes end to end gets
  `[Trait("Category", "E2E")]` and runs in the `e2e` group.

Windows PowerShell 5.1 behavior and the pinned Windows ISO executable need an
actual Windows environment. Use a local disposable Windows VM with Node.js,
Windows PowerShell 5.1 and network access for downloading the pinned executable:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File test/run-local-windows-checks.ps1
```

GitHub runners provide convenience and clean environments, not unique test
capabilities. A local Windows VM covers the platform-specific checks that this
Linux development VM cannot execute directly.

For the Hyper-V driver's scripts, the host installer and the ISO tools under real Windows
PowerShell 5.1, `test/run-local-windows-checks.ps1` (above) runs their suites.
`test/windows-license-lifecycle.live.ps1` boots isolated firmware on a disposable Hyper-V
host (elevated) and is run by hand only.
