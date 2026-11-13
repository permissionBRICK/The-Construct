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
`apt-get install python3-aiohttp jq`). Set up browser-test dependencies once:

```sh
cd extension/test
npm install
npx playwright install --with-deps chromium
```

Then, from the repository root, choose the checks relevant to the change:

```sh
bash test/run-local-checks.sh panel
bash test/run-local-checks.sh t3
bash test/run-local-checks.sh service
bash test/run-local-checks.sh companion
bash test/run-local-checks.sh release
bash test/run-local-checks.sh ui
# Or run every group:
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
| `companion/**`, `test/fixtures/companion-parity/**`, `test/companion-*`, `test/feature-set.test.ps1`, `Install-ConstructCompanion.ps1`, `Auto-Install.ps1`, `install.ps1`, `Update-Construct.ps1` | `companion` |
| `extension/src/**`, `extension/media/**`, `extension/vm/**`, `extension/test/**` | `panel`, `companion` |
| `extension/src/updates.js`, `extension/src/t3code*`, `bin/*t3*`, `test/t3-*` | `t3` |
| `service/**`, `drivers/**`, `lib/**`, `bin/**`, `console-viewer/**` | `service` |
| `ui/**` | `ui` |
| `scripts/**`, `.github/**`, `VERSION`, `test/release.test.py`, `test/construct-release.test.py`, `test/run-local-checks.sh`, `test/helpers/changed-groups.py`, `docs/local-checks.md`, `service/src/Constructd.Core/Logic/ReleaseChannel.cs`, `service/tests/Constructd.Tests/Updates/ReleaseChannelTests.cs` | `release` |
| `docs/**`, `*.md`, `assets/**` | |

For installers, also run the touched script's own tests.

The `companion` group builds with warnings as errors, runs Companion .NET tests,
Node parity and Companion suites, the three Companion PowerShell suites, and the
first-install feature-set suite. It is included in `all`; the Companion workflow only publishes its Windows deliverable.

The `ui` group runs the client UI's unit tests (vitest), builds its static bundle
and compares its screenshots in headless Chromium (Playwright). After an intended
visual change, refresh the pictures with `npm run test:screens:update` in `ui/`.

The panel defaults to the native theme; set `UI_SMOKE_THEME=classic` or `terminal`
to check another theme. These runners group the former workflow checks; other
focused tests remain available in `test/`, `extension/test/` and `service/tests/`.

Windows PowerShell 5.1 behavior and the pinned Windows ISO executable need an
actual Windows environment. Use a local disposable Windows VM with Node.js,
Windows PowerShell 5.1 and network access for downloading the pinned executable:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File test/run-local-windows-checks.ps1
```

GitHub runners provide convenience and clean environments, not unique test
capabilities. A local Windows VM covers the platform-specific checks that this
Linux development VM cannot execute directly.

For remote source-cache changes, run the `service` group (including the guest script's
`bash -n` check), plus these focused suites. They use Linux fakes/local HTTP fixtures and do
not touch a running host service:

```sh
pwsh -NoProfile -File test/source-transport.test.ps1
pwsh -NoProfile -File test/source-release.test.ps1
pwsh -NoProfile -File test/remote-client.test.ps1
pwsh -NoProfile -File test/provision-seed-user.test.ps1
pwsh -NoProfile -File test/provision-ssh-diagnostics.test.ps1
pwsh -NoProfile -File test/instance-identity.test.ps1
pwsh -NoProfile -File service/tests/host-installer.test.ps1
bash test/fetch-construct-source.test.sh
bash test/provision-marker.test.sh
bash test/remote-e2e.test.sh
```

The ZIP fixtures require `git`, `curl`, `sha256sum` and Python 3; end-to-end testing starts and
stops its own fake constructd and drives the real PowerShell helpers and guest script.
