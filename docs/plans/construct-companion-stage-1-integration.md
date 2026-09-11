# Construct Companion stage 1 integration results

Validated on Linux on 2026-09-11 from `cc/integ-1`. Starting commit: `4dbb0ea`; scaffold commit: `91c8902`; merge commit: `0f7883e`. No Windows runtime execution.

## Integration

Reset to `feat/companion`, then merged `cc/s1-scaffold` with `--no-ff`. The branch had one new commit; no branches were skipped and no conflicts occurred. The scaffold changes 80 files across `companion/`, the extension modules and guest scripts, initial parity fixtures, workflow, design notes, and the provision-marker test. Integration adds only this report and the design document’s integration notes; no production fixes were needed.

## Validation

- `dotnet build companion/Construct.Companion.sln -warnaserror`: **0 warnings, 0 errors**. The Windows app target compiled on Linux.
- `dotnet test companion/Construct.Companion.sln`: **230/230 passed**.
- `dotnet test service/Constructd.sln`: **1,261/1,261 passed**. One existing xUnit2029 analyzer warning in `service/tests/Constructd.Tests/Updates/PackageTests.cs:91`; service source is unchanged.
- All **30 Node**, **27 PowerShell** (21 under `test/`, 6 recursively under `service/tests/`), and **22 Bash** suite files ran sequentially.
- Final suite-file outcomes: Node **30/30 successful**, PowerShell **27/27 successful**, Bash **20/22 successful**. Config-sync requires the process-local retry described below.
- Two individual tests skipped: the optional real VS Code proxy-agent case (no `CONSTRUCT_VSCODE_PROXY_AGENT` supplied), and the Windows DPAPI round trip.
- Regenerated fixtures with `node extension/test/export-parity-fixtures.js`; `git status --short` and fixture diff were empty. Also exported all four areas using the original JavaScript builders from `4dbb0ea`; their serialized fixture bytes match the merged builders exactly.

Counts below are those emitted by each suite, which mixes tests, assertions, scenarios, and fixtures. A suite that does not emit a numeric count is explicitly marked; these unlike units are not summed.

## Per-suite results

| Suite | Result |
|---|---|
| `companion/Construct.Companion.sln` | 230 passed, 0 failed, 0 skipped |
| `service/Constructd.sln` | 1,261 passed, 0 failed, 0 skipped |
| `extension/test/audio.test.js` | audio unit tests — 233/233 passed |
| `extension/test/configsync.test.js` | 475/475 passed on retry; initial and clean-CONSTRUCT-env runs aborted after 432 passes at invalid bare-remote HEAD |
| `extension/test/drivers.test.js` | drivers unit tests — 79/79 passed |
| `extension/test/forwarder.test.js` | forwarder unit tests — 727/727 passed |
| `extension/test/host.test.js` | host locator/settings unit tests — 118/118 passed |
| `extension/test/hostadmin-discovery.test.js` | Passed (no numeric count emitted) |
| `extension/test/hostadmin-ui.test.js` | host-administration adapter tests — 101/101 passed |
| `extension/test/hostadmin.test.js` | host-administration unit tests — 314/314 passed |
| `extension/test/hostconversion.test.js` | 5 passed, 0 failed, 0 skipped |
| `extension/test/hostupdate.test.js` | 8 passed, 0 failed, 0 skipped |
| `extension/test/importui.test.js` | importui unit tests — 24/24 passed |
| `extension/test/instances.test.js` | instance-registry unit tests — 1528/1528 passed |
| `extension/test/instancestate.test.js` | per-instance state store unit tests — 105/105 passed |
| `extension/test/lifecycle-preparation.test.js` | Passed (3 scenarios; no assertion count emitted) |
| `extension/test/lifecycle.test.js` | lifecycle launcher unit tests — 265/265 passed |
| `extension/test/notify.test.js` | notification unit tests — 103/103 passed |
| `extension/test/parity.test.js` | 193 fixtures passed across 4 areas |
| `extension/test/probe.test.js` | probe/ssh unit tests — 98/98 passed |
| `extension/test/project-set.test.js` | OK  63 passed, 0 failed |
| `extension/test/projects.test.js` | project-profile unit tests — 167/167 passed |
| `extension/test/remote.test.js` | remote-open unit tests — 79/79 passed |
| `extension/test/remotehost-proxy.test.js` | 1 passed, 0 failed, 1 skipped (optional VS Code proxy agent unavailable) |
| `extension/test/remotehost.test.js` | remote-host client tests — 220/220 passed |
| `extension/test/repatch.test.js` | repatch startup-verification unit tests — 39/39 passed |
| `extension/test/t3code.test.js` | t3code unit tests — 89/89 passed |
| `extension/test/themes.test.js` | themes.test.js: 43 checks passed |
| `extension/test/updates.test.js` | updates (Construct check) unit tests — 143/143 passed |
| `extension/test/usage.test.js` | usage unit tests — 131/131 passed |
| `extension/test/vmpower.test.js` | vmpower unit tests — 81/81 passed |
| `extension/test/zip.test.js` | zip unit tests — 21/21 passed |
| `test/browser-console-install.test.ps1` | Passed (5 setting scenarios; no assertion count emitted) |
| `test/config-sync.test.ps1` | 568/568 passed on retry; initial run 561 passed, 7 failed |
| `test/driver-contract.test.ps1` | 148 passed, 0 failed |
| `test/host-admin-client.test.ps1` | 19 passed, 0 failed |
| `test/host-conversion-identity.test.ps1` | Host conversion identity checks passed: 10 cases, including empty IP reports and mismatched/missing identities. |
| `test/host-conversion-source.test.ps1` | Passed (no numeric count emitted) |
| `test/host-conversion-transport.test.ps1` | Passed (no numeric count emitted) |
| `test/host-conversion.test.ps1` | Passed (no numeric count emitted) |
| `test/host-lib.test.ps1` | host-lib unit tests - 309/309 passed |
| `test/instance-cleanup.test.ps1` | instance-cleanup unit tests - 124/124 passed |
| `test/instance-identity.test.ps1` | 242 passed, 0 failed |
| `test/instance-state.test.ps1` | instance-state tests -- 64/64 passed |
| `test/instances.test.ps1` | instance-registry tests -- 781/781 passed |
| `test/native-iso-host.test.ps1` | 23 native ISO host checks passed. |
| `test/notify-toast.test.ps1` | toast script tests — 22/22 passed |
| `test/provision-seed-user.test.ps1` | Passed (no summary count emitted) |
| `test/remote-client.test.ps1` | 97 passed, 0 failed, 1 skipped |
| `test/remote-driver.test.ps1` | 87 passed, 0 failed |
| `test/remote-install.test.ps1` | 206 passed, 0 failed |
| `test/t3-desktop-handoff.test.ps1` | Passed (no numeric count emitted) |
| `test/t3-reprovision-host.test.ps1` | Passed (no numeric count emitted) |
| `service/tests/Constructd.Tests/Capacity/inventory.test.ps1` | 19 capacity inventory checks passed |
| `service/tests/Constructd.Tests/Console/console-script.test.ps1` | 163/163 console script checks passed (WMI doubles, encoding sentinel). |
| `service/tests/Constructd.Tests/Network/network-script.test.ps1` | PASS: 29 network script checks |
| `service/tests/host-installer.test.ps1` | 368 passed, 0 failed |
| `service/tests/host-release-installer.test.ps1` | host-release-installer: 9 assertions passed (ACL calls recorded) |
| `service/tests/host-updater.test.ps1` | host-updater: 56 assertions passed (service control and health faked) |
| `test/autoinstall-iso.test.sh` | 59 passed, 0 failed |
| `test/construct-expose.test.sh` | 170 passed, 0 failed |
| `test/construct-notify.test.sh` | 36 passed, 0 failed |
| `test/construct-vm.test.sh` | 172 passed, 0 failed |
| `test/contracts-compile.test.sh` | 4 passed, 1 failed |
| `test/export-config.test.sh` | export-config fixture tests — 12/12 passed |
| `test/external-host.test.sh` | 61 passed, 0 failed |
| `test/host-admin-e2e.test.sh` | 1 xUnit test passed, containing 70 end-to-end checks |
| `test/host-package.test.sh` | host-package: 105 assertions passed (94 payload files); fixture executable, no Windows publish performed |
| `test/idle-report.test.sh` | 100 passed, 3 failed |
| `test/opencode-install.test.sh` | 20 passed, 0 failed |
| `test/partial-streaming.test.sh` | 6 passed, 0 failed |
| `test/patch-status.test.sh` | patch-status probe tests — 12/12 passed |
| `test/provision-diskcheck.test.sh` | 24 passed, 0 failed |
| `test/provision-hostname.test.sh` | 28 passed, 0 failed |
| `test/provision-marker.test.sh` | provision marker tests — 33 passed, 0 failed |
| `test/provision-steprunner.test.sh` | 17 passed, 0 failed |
| `test/remote-e2e.test.sh` | 45 passed, 0 failed, 0 skipped |
| `test/restore-config.test.sh` | restore-config fixture tests — 25/25 passed |
| `test/systemprompt-install.test.sh` | systemprompt install tests — 17/17 passed |
| `test/t3-https.test.sh` | 133 passed, 0 failed |
| `test/vscode-download.test.sh` | VS Code download tests — 6/6 passed |

## Baseline failures and retry conditions

The initial Node config-sync run and the required retry with the six documented `CONSTRUCT_*` variables unset both abort at `fatal: Not a valid object name HEAD`, after 432 passing checks. PowerShell config-sync initially reports 561/568 passed, with seven default-branch/publish assertions failing. Both pass with those six variables unset and `init.defaultBranch=main` appended through process-local `GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n` environment variables: Node 475/475, PowerShell 568/568. No global or repository Git configuration was changed.

Two Bash failures remain outside scaffold scope:

- `contracts-compile.test.sh`: 4 passed, 1 failed. The production `Constructd.Core.Abstractions.HypervisorVmInfo` public signature differs from the frozen host-administration contract.
- `idle-report.test.sh`: 100 passed, 3 failed. The service-managed configuration checks fail for exactly two service keys, the service URL, and the instance name.

Both failing suites and their relevant production files are unchanged from `4dbb0ea`. Baseline validation uses an archive of that commit, separate from the read-only main checkout. The idle suite requires Git HEAD for its comparisons; its plain-archive attempt is not valid evidence because it skips those checks. It was repeated with a private temporary Git index and HEAD at `4dbb0ea`.

## Decisions and limitations

The existing three S1 deviations remain: reuse existing repatch sources; preserve default/instance pairing templates and validated fragment bytes; use a temporary named quit event until HTTP integration; compose the shared notification claim function into its templates. No integration design deviation.

This remains a scaffold: runtime jobs, HTTP IPC/activation, full Windows adapters, WebView UI, installer, and publishing are later packages. `--selftest` remains an explicit failing stub. Windows tray, quit signaling, and console behavior were not run here.
