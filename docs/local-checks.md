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
bash test/run-local-checks.sh release
bash test/run-local-checks.sh ui
bash test/run-local-checks.sh agent
bash test/run-local-checks.sh guest
# Or run every group except guest:
bash test/run-local-checks.sh all
# Or the groups of the paths changed since the base branch (origin/nightly, origin/rework or
# origin/main, whichever exists first; or name one):
bash test/run-local-checks.sh --changed [<base>]
```

Run only the checks for the features a change affects. Use `all` for integration
merges and release preparation, not for ordinary changes.

`--changed` maps each touched path (committed since the merge base, uncommitted or untracked)
to groups by the table below, which `test/helpers/changed-groups.py` reads. A path matching
several rows selects all their groups. A path matching no row selects `all`. In a pattern `*`
stays within one directory and `**` crosses directories. A row without a group selects nothing.

| Path | Groups |
|---|---|
| `extension/**` | `panel` |
| `bin/*t3*`, `test/t3-*`, `extension/vm/*t3*` | `t3` |
| `service/**`, `lib/**`, `bin/**`, `console-viewer/**`, `extension/src/projects.js`, `extension/vm/**`, `companion/**` | `service` |
| `ui/**` | `ui` |
| `agent/**`, `test/agent-install.test.sh`, `test/agent-bootstrap.test.ps1`, `install.ps1`, `Update-Construct.ps1`, `scripts/package-agent.py`, `service/src/Construct.Keys/**`, `service/src/Constructd.Store/**`, `service/src/Constructd.Sync/**`, `service/src/Constructd.Api/**`, `service/openapi/**` | `agent`, `ui` |
| `extension/vm/**`, `lib/**`, `Update-T3Code.ps1`, `Get-ConstructT3PairingLink.ps1`, `test/fixtures/legacy-updaters/**`, `test/source-release.test.ps1`, `test/lifecycle-run.test.ps1` | `agent` |
| `scripts/**`, `.github/**`, `VERSION`, `test/release.test.py`, `test/construct-release.test.py`, `test/run-local-checks.sh`, `test/helpers/changed-groups.py`, `docs/local-checks.md`, `service/src/Constructd.Core/Logic/ReleaseChannel.cs`, `service/tests/Constructd.Tests/Updates/ReleaseChannelTests.cs` | `release` |
| `docs/**`, `*.md`, `assets/**` | |

For installers, also run the touched script's own tests.

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

The `panel` group tests the VS Code extension under plain node with `vscode` faked
(finding, starting and installing the agent, the webview documents and their message
bridge, Remote-SSH, the register-workspace offer), the profile validator the VM's
`construct project set` loads from `extension/src/projects.js`, and the VM's
`project-set.js` helper.

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
