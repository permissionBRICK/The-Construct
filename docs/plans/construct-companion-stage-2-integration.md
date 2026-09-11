# Construct Companion stage 2 integration results

Validated on Linux on 2026-09-11 in `cc/integ-2`, starting from `feat/companion`
commit `4f14329`. No Windows runtime execution.

## Integration

Reset to `feat/companion`, then merged these branches in order with `--no-ff`:

| Branch | Tip | New commits beyond starting `feat/companion` | Merge |
|---|---|---:|---|
| `cc/s2-state` | `b404d6f` | 1 | `4f1e844` |
| `cc/s2-runtime` | `18355d3` | 2 | `8e85ca1` |
| `cc/s2-configsync` | `39ecb99` | 1 | `0c97c23` |
| `cc/s2-extension` | `2eff97f` | 2 | `93fffae` |

No branches were skipped. State and extension merged cleanly. Runtime conflicted in
`companion/README.md` and `extension/test/export-parity-fixtures.js`; configsync
conflicted in those files and `docs/plans/construct-companion.md`. Resolutions preserve
all package documentation, all deviation entries, and every exporter function. The
combined exporter keeps state's asynchronous `exportAll()` and adds runtime/configsync
areas; the JavaScript parity consumer still awaits it. No solution, project, interface,
or extension wiring conflict required choosing one implementation over another.

The merged packages provide state/settings/lifecycle/remote-host logic, supervised
forwards/notifications/audio/repatch runtimes, config-sync Git transactions and prompt
flows, and extension Companion detection, deferral, proxying and migrations. Changes
are under `companion/`, `extension/`, `test/fixtures/companion-parity/`, and related docs.
Service, installers, and guest scripts have no stage 2 changes.

## Integration fix

Configsync added `ProcessInvocation.EnvironmentOverrides` for temporary Git indexes,
but runtime's `RuntimeProcessRunner` did not apply them. Since `AddRuntime()` and
`AddConfigSync()` both use `TryAddSingleton<IProcessRunner>`, runtime-first registration
would silently write to the ordinary index. The process adapter now applies overrides
(and removes keys whose value is null) before starting a child. No argv or secret logging
changed. Two real-Git tests in `ConfigSync/CompositionTests.cs` verify both registration
orders: the temporary index contains only its own profile and the ordinary index remains
byte-identical. Both tests are included in the full Companion result below.

Integration-only changes: `RuntimeProcessRunner.cs`, the new composition tests,
`companion/README.md`, this report, and stage 2 notes in the frozen plan. Conflict resolutions
are recorded in the respective merge commits. No new design deviation was needed.

## Validation

- Companion build with `-warnaserror`: **0 warnings, 0 errors**, including the Windows app
  target compiled on Linux.
- Companion tests: **4,332 passed, 0 failed, 0 skipped**.
- Service tests: **1,261 passed, 0 failed, 0 skipped**. The existing xUnit2029 analyzer
  warning in `service/tests/Constructd.Tests/Updates/PackageTests.cs:91` remains unchanged.
- All **31 Node**, **27 PowerShell** (21 under `test/`, 6 recursively under `service/tests/`),
  and **22 Bash** suite files ran. Final outcomes: Node **31/31**, PowerShell **27/27 after
  one environment-only retry**, Bash **20/22**. Only the existing Bash failures below remain.
- Two individual checks skipped: optional real VS Code proxy agent and Windows DPAPI round trip.
- `node extension/test/export-parity-fixtures.js` regenerates **4,192 rows across 27 areas**
  without any fixture drift. No committed fixture was edited to resolve the merge.
- Builds/tests ran with at most one .NET command at a time. Test-owned subprocesses and
  servers exited; no VM service was stopped or restarted.

Counts below are those emitted by each suite: tests, assertions, scenarios, or fixtures.
They are not summed across unlike units. Suites without a numeric count say so.

## Per-suite results

| Suite | Result |
|---|---|
| `companion/Construct.Companion.sln` | 4,332 passed, 0 failed, 0 skipped |
| `service/Constructd.sln` | 1,261 passed, 0 failed, 0 skipped |
| `extension/test/audio.test.js` | 233/233 passed |
| `extension/test/companion.test.js` | 25 passed, 0 failed, 0 skipped |
| `extension/test/configsync.test.js` | 475/475 passed |
| `extension/test/drivers.test.js` | 79/79 passed |
| `extension/test/forwarder.test.js` | 727/727 passed |
| `extension/test/host.test.js` | 118/118 passed |
| `extension/test/hostadmin-discovery.test.js` | Passed (no numeric count emitted) |
| `extension/test/hostadmin-ui.test.js` | 101/101 passed |
| `extension/test/hostadmin.test.js` | 314/314 passed |
| `extension/test/hostconversion.test.js` | 5 passed, 0 failed, 0 skipped |
| `extension/test/hostupdate.test.js` | 8 passed, 0 failed, 0 skipped |
| `extension/test/importui.test.js` | 24/24 passed |
| `extension/test/instances.test.js` | 1528/1528 passed |
| `extension/test/instancestate.test.js` | 105/105 passed |
| `extension/test/lifecycle-preparation.test.js` | Passed (3 scenarios; no assertion count emitted) |
| `extension/test/lifecycle.test.js` | 265/265 passed |
| `extension/test/notify.test.js` | 103/103 passed |
| `extension/test/parity.test.js` | 4,192 fixtures passed across 27 areas |
| `extension/test/probe.test.js` | 98/98 passed |
| `extension/test/project-set.test.js` | 63 passed, 0 failed |
| `extension/test/projects.test.js` | 167/167 passed |
| `extension/test/remote.test.js` | 79/79 passed |
| `extension/test/remotehost-proxy.test.js` | 1 passed, 0 failed, 1 skipped (optional VS Code proxy agent unavailable) |
| `extension/test/remotehost.test.js` | 220/220 passed |
| `extension/test/repatch.test.js` | 39/39 passed |
| `extension/test/t3code.test.js` | 89/89 passed |
| `extension/test/themes.test.js` | themes.test.js: 43 checks passed |
| `extension/test/updates.test.js` | 143/143 passed |
| `extension/test/usage.test.js` | 131/131 passed |
| `extension/test/vmpower.test.js` | 81/81 passed |
| `extension/test/zip.test.js` | 21/21 passed |
| `service/tests/Constructd.Tests/Capacity/inventory.test.ps1` | 19 capacity inventory checks passed |
| `service/tests/Constructd.Tests/Console/console-script.test.ps1` | 163/163 passed |
| `service/tests/Constructd.Tests/Network/network-script.test.ps1` | PASS: 29 network script checks |
| `service/tests/host-installer.test.ps1` | 368 passed, 0 failed |
| `service/tests/host-release-installer.test.ps1` | host-release-installer: 9 assertions passed (ACL calls recorded) |
| `service/tests/host-updater.test.ps1` | host-updater: 56 assertions passed (service control and health faked) |
| `test/browser-console-install.test.ps1` | Passed (5 setting scenarios; no assertion count emitted) |
| `test/config-sync.test.ps1` | 568/568 passed on retry; initial run 561 passed, 7 failed |
| `test/driver-contract.test.ps1` | 148 passed, 0 failed |
| `test/host-admin-client.test.ps1` | 19 passed, 0 failed |
| `test/host-conversion-identity.test.ps1` | Host conversion identity checks passed: 10 cases, including empty IP reports and mismatched/missing identities. |
| `test/host-conversion-source.test.ps1` | Passed (no numeric count emitted) |
| `test/host-conversion-transport.test.ps1` | Passed (no numeric count emitted) |
| `test/host-conversion.test.ps1` | Passed (no numeric count emitted) |
| `test/host-lib.test.ps1` | 309/309 passed |
| `test/instance-cleanup.test.ps1` | 124/124 passed |
| `test/instance-identity.test.ps1` | 242 passed, 0 failed |
| `test/instance-state.test.ps1` | 64/64 passed |
| `test/instances.test.ps1` | 781/781 passed |
| `test/native-iso-host.test.ps1` | 23 native ISO host checks passed. |
| `test/notify-toast.test.ps1` | 22/22 passed |
| `test/provision-seed-user.test.ps1` | Passed (no summary count emitted) |
| `test/remote-client.test.ps1` | 97 passed, 0 failed, 1 skipped |
| `test/remote-driver.test.ps1` | 87 passed, 0 failed |
| `test/remote-install.test.ps1` | 206 passed, 0 failed |
| `test/t3-desktop-handoff.test.ps1` | Passed (no numeric count emitted) |
| `test/t3-reprovision-host.test.ps1` | Passed (no numeric count emitted) |
| `test/autoinstall-iso.test.sh` | 59 passed, 0 failed |
| `test/construct-expose.test.sh` | 170 passed, 0 failed |
| `test/construct-notify.test.sh` | 36 passed, 0 failed |
| `test/construct-vm.test.sh` | 172 passed, 0 failed |
| `test/contracts-compile.test.sh` | 4 passed, 1 failed |
| `test/export-config.test.sh` | 12/12 passed |
| `test/external-host.test.sh` | 61 passed, 0 failed |
| `test/host-admin-e2e.test.sh` | 1 xUnit test passed, containing 70 end-to-end checks |
| `test/host-package.test.sh` | 106 assertions passed (95 payload files); fixture executable, no Windows publish |
| `test/idle-report.test.sh` | 100 passed, 3 failed |
| `test/opencode-install.test.sh` | 20 passed, 0 failed |
| `test/partial-streaming.test.sh` | 6 passed, 0 failed |
| `test/patch-status.test.sh` | 12/12 passed |
| `test/provision-diskcheck.test.sh` | 24 passed, 0 failed |
| `test/provision-hostname.test.sh` | 28 passed, 0 failed |
| `test/provision-marker.test.sh` | 33 passed, 0 failed |
| `test/provision-steprunner.test.sh` | 17 passed, 0 failed |
| `test/remote-e2e.test.sh` | 45 passed, 0 failed, 0 skipped |
| `test/restore-config.test.sh` | 25/25 passed |
| `test/systemprompt-install.test.sh` | 17/17 passed |
| `test/t3-https.test.sh` | 133 passed, 0 failed |
| `test/vscode-download.test.sh` | 6/6 passed |

## Recorded defects and retry conditions

- **PowerShell config-sync default branch dependency (pre-existing):** initial run
  561/568; retry 568/568 with the six documented `CONSTRUCT_*` variables unset and
  `init.defaultBranch=main` appended through process-local `GIT_CONFIG_COUNT`,
  `GIT_CONFIG_KEY_n`, and `GIT_CONFIG_VALUE_n`. No global/repository Git config changed.
  The extension branch already fixes the equivalent Node fixture setup; Node configsync
  passed 475/475 on its first run without this workaround.
- **`test/contracts-compile.test.sh` (pre-existing):** 4 passed, 1 failed. The compiled
  declarations disagree with `Constructd.Core.Abstractions.HypervisorVmInfo`'s public
  signature. The frozen host-administration contract and service are unchanged here.
- **`test/idle-report.test.sh` (pre-existing):** 100 passed, 3 failed: exactly-two-service-keys,
  service URL, and instance-name assertions. Test and production scripts are unchanged.
  Both Bash defects were reproduced against untouched `4dbb0ea` during stage 1; see
  [stage 1 evidence](construct-companion-stage-1-integration.md). This run reproduces
  the same failures with Git HEAD available. They are outside stage 2 merge repair scope.

No unresolved defect introduced by these merges was found. HTTP IPC/dispatcher,
application composition, Windows adapters/UI, installer/release integration, and T3
launch integration remain assigned to S2b/S3. The application still uses the scaffold's
quit event and failing `--selftest` stub. No Windows behavior or deployment is claimed.
