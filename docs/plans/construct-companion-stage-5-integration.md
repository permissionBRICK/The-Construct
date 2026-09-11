# Construct Companion stage 5 integration results

Validated on Linux on 2026-09-11 in `cc/integ-5`, starting from `feat/companion`
`e044b47`. No Windows execution was performed.

## Integration

Reset with `git reset --hard feat/companion`, then merged with `git merge --no-ff`:

| Branch | Tip | New commits | Merge |
|---|---|---:|---|
| `cc/s4-cleanup` | `2e187bd` | 5 | `8699604` |

No branches skipped, no conflicts, and no integration code fixes were needed.
The cleanup consolidates Core seams/fakes, Host filesystem/process/settings/activation
adapters, Windows/app wiring, and extension host-job deferral checks; simplifies
installer hooks and documentation; and preserves the recorded S4 deviations.
The input changes 156 files under `companion/`, extension wiring/client/tests,
the parity exporter and config-sync fixture, installer/library, workflow and docs.
Stage 5 itself changes only this report and the integration notes in the frozen plan.
No service, `bin/`, or `extension/vm/` code changed. No T3 repository branch was merged.

## Validation

- Companion warning-as-error build: **0 warnings, 0 errors** (Windows target compiled on Linux).
- Companion: **4,749 passed, 0 failed, 0 skipped**.
- Service: **1,261 passed, 0 failed, 0 skipped**. Existing xUnit2029 analyzer warning remains at `Updates/PackageTests.cs:91`.
- Node: **33/33 suite files passed**, all first attempt. Configsync passed 475/475.
- PowerShell: **30/30 suite files passed after the config-sync environment retry** (24 under `test/`, six recursively under `service/tests/`). First pass: 29/30.
- Bash: **20/22 suite files passed**; both failures match the recorded baseline below.
- Parity: **4,466 rows across 31 areas**, regenerated after the complete matrix with `node extension/test/export-parity-fixtures.js`; `git status` and `git diff --exit-code -- test/fixtures/companion-parity` confirm **zero fixture drift**.
- Optional skips: one Node VS Code proxy-agent check and one PowerShell Windows DPAPI check.
- .NET builds/tests ran serially, including .NET calls inside Bash suites. No existing VM service was stopped or restarted; test servers were owned and stopped by tests.

The following counts use each suite's own units (tests, assertions, checks or fixtures).
They are not summed across different units. Successful suites without a numeric
summary are identified without inventing a count.

## Per-suite results

| Suite | Result |
|---|---|
| `companion/Construct.Companion.sln` | 4749 passed, 0 failed, 0 skipped |
| `service/Constructd.sln` | 1261 passed, 0 failed, 0 skipped |
| `extension/test/audio.test.js` | audio unit tests — 233/233 passed |
| `extension/test/companion-host.test.js` | companion real host: 20 passed |
| `extension/test/companion-install.test.js` | companion install offer: 9 passed |
| `extension/test/companion.test.js` | 26 passed, 0 failed, 0 skipped |
| `extension/test/configsync.test.js` | config-sync unit tests — 475/475 passed |
| `extension/test/drivers.test.js` | drivers unit tests — 79/79 passed |
| `extension/test/forwarder.test.js` | forwarder unit tests — 727/727 passed |
| `extension/test/host.test.js` | host locator/settings unit tests — 118/118 passed |
| `extension/test/hostadmin-discovery.test.js` | Host administration startup, slow-probe, instance-switch and sidebar rendering tests passed |
| `extension/test/hostadmin-ui.test.js` | host-administration adapter tests — 101/101 passed |
| `extension/test/hostadmin.test.js` | host-administration unit tests — 314/314 passed |
| `extension/test/hostconversion.test.js` | 5 passed, 0 failed, 0 skipped |
| `extension/test/hostupdate.test.js` | 8 passed, 0 failed, 0 skipped |
| `extension/test/importui.test.js` | importui unit tests — 24/24 passed |
| `extension/test/instances.test.js` | instance-registry unit tests — 1528/1528 passed |
| `extension/test/instancestate.test.js` | per-instance state store unit tests — 105/105 passed |
| `extension/test/lifecycle-preparation.test.js` | Lifecycle preparation: completion, early return, and exception recovery passed. |
| `extension/test/lifecycle.test.js` | lifecycle launcher unit tests — 265/265 passed |
| `extension/test/notify.test.js` | notification unit tests — 103/103 passed |
| `extension/test/parity.test.js` | Parity: 31 areas, 4466 fixtures passed |
| `extension/test/probe.test.js` | probe/ssh unit tests — 98/98 passed |
| `extension/test/project-set.test.js` | OK  63 passed, 0 failed |
| `extension/test/projects.test.js` | project-profile unit tests — 167/167 passed |
| `extension/test/remote.test.js` | remote-open unit tests — 79/79 passed |
| `extension/test/remotehost-proxy.test.js` | 1 passed, 0 failed, 1 skipped |
| `extension/test/remotehost.test.js` | remote-host client tests — 220/220 passed |
| `extension/test/repatch.test.js` | repatch startup-verification unit tests — 39/39 passed |
| `extension/test/t3code.test.js` | t3code unit tests — 89/89 passed |
| `extension/test/themes.test.js` | themes.test.js: 43 checks passed |
| `extension/test/updates.test.js` | updates (Construct check) unit tests — 143/143 passed |
| `extension/test/usage.test.js` | usage unit tests — 131/131 passed |
| `extension/test/vmpower.test.js` | vmpower unit tests — 81/81 passed |
| `extension/test/zip.test.js` | zip unit tests — 21/21 passed |
| `service/tests/Constructd.Tests/Capacity/inventory.test.ps1` | 19 capacity inventory checks passed |
| `service/tests/Constructd.Tests/Console/console-script.test.ps1` | 163/163 console script checks passed (WMI doubles, encoding sentinel). |
| `service/tests/Constructd.Tests/Network/network-script.test.ps1` | PASS: 29 network script checks |
| `service/tests/host-installer.test.ps1` | === 368 passed, 0 failed === |
| `service/tests/host-release-installer.test.ps1` | host-release-installer: 9 assertions passed (ACL calls recorded) |
| `service/tests/host-updater.test.ps1` | host-updater: 56 assertions passed (service control and health faked) |
| `test/browser-console-install.test.ps1` | PASS: fresh/default, legacy upgrade, explicit opt-out, enabled, malformed setting |
| `test/companion-entrypoints.test.ps1` | Companion entrypoints: 33 passed |
| `test/companion-install.test.ps1` | PASS: 72 Companion installer assertions |
| `test/companion-package.test.ps1` | PASS: 34 Companion package assertions |
| `test/config-sync.test.ps1` | 568/568 passed on environment retry; initial 561/568 (7 failed) |
| `test/driver-contract.test.ps1` | 148 passed, 0 failed |
| `test/host-admin-client.test.ps1` | 19 passed, 0 failed |
| `test/host-conversion-identity.test.ps1` | Host conversion identity checks passed: 10 cases, including empty IP reports and mismatched/missing identities. |
| `test/host-conversion-source.test.ps1` | Ubuntu source metadata checks passed: text/byte/BOM bodies, version ordering, exact release/architecture, and invalid/conflicting checksums. |
| `test/host-conversion-transport.test.ps1` | Guest SSH transport checks passed (UTF-8 across OEM/Unicode/BOM defaults, old defect reproduced, diagnostics and redaction). |
| `test/host-conversion.test.ps1` | Host conversion PowerShell checks passed (quoting, atomic handoff, extraction, no WSL prerequisite, RSA). |
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
| `test/t3-desktop-handoff.test.ps1` | PASS: shared host tracking, deferred packaging/download, missing app, and failure handling |
| `test/t3-reprovision-host.test.ps1` | PASS: noninteractive and automatic reprovision reuse all three saved Git choices |
| `test/autoinstall-iso.test.sh` | === 59 passed, 0 failed === |
| `test/construct-expose.test.sh` | 170 passed, 0 failed |
| `test/construct-notify.test.sh` | 36 passed, 0 failed |
| `test/construct-vm.test.sh` | 172 passed, 0 failed |
| `test/contracts-compile.test.sh` | 4 passed, 1 failed |
| `test/export-config.test.sh` | export-config fixture tests — 12/12 passed |
| `test/external-host.test.sh` | 61 passed, 0 failed |
| `test/host-admin-e2e.test.sh` | 1 xUnit test passed; 70 end-to-end checks passed (plus 2 post-summary checks) |
| `test/host-package.test.sh` | host-package: 111 assertions passed (100 payload files); fixture executable, no Windows publish performed |
| `test/idle-report.test.sh` | 100 passed, 3 failed |
| `test/opencode-install.test.sh` | 20 passed, 0 failed |
| `test/partial-streaming.test.sh` | 6 passed, 0 failed |
| `test/patch-status.test.sh` | patch-status probe tests — 12/12 passed |
| `test/provision-diskcheck.test.sh` | 24 passed, 0 failed |
| `test/provision-hostname.test.sh` | === 28 passed, 0 failed === |
| `test/provision-marker.test.sh` | provision marker tests — 33 passed, 0 failed |
| `test/provision-steprunner.test.sh` | 17 passed, 0 failed |
| `test/remote-e2e.test.sh` | 45 passed, 0 failed, 0 skipped |
| `test/restore-config.test.sh` | restore-config fixture tests — 25/25 passed |
| `test/systemprompt-install.test.sh` | systemprompt install tests — 17/17 passed |
| `test/t3-https.test.sh` | 133 passed, 0 failed |
| `test/vscode-download.test.sh` | VS Code download tests — 6/6 passed |

## Recorded defects and limitations

- **Pre-existing PowerShell config-sync default-branch dependency:** first pass 561/568;
  retry 568/568 after unsetting the six documented `CONSTRUCT_*` variables and adding
  `init.defaultBranch=main` via process-local `GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_n`
  and `GIT_CONFIG_VALUE_n`. No global or repository Git configuration changed.
- **Pre-existing contract mismatch:** `test/contracts-compile.test.sh` passes 4/5;
  production `Constructd.Core.Abstractions.HypervisorVmInfo` differs from the frozen
  public signature. The test, service and contract document are unchanged by this merge.
- **Pre-existing idle-report environment assertions:** `test/idle-report.test.sh`
  passes 100/103; failures concern exactly two service keys, service URL and instance
  name. Test and production code are unchanged. Both Bash failures match the
  [stage 4 results](construct-companion-stage-4-integration.md) and earlier baseline evidence.
- **Inherited unsupported workflows remain:** attached-window registration/conversion,
  project creation, instance removal, first-VM wizard, install-wide Construct update,
  one-time token display, automatic checkpoint apply, lifecycle preflight/live-project
  fallback and result monitoring. See [Unsupported messages](../../companion/README.md#unsupported-messages)
  and S3 implementation notes. These are beyond a few-line integration repair;
  this validation does not claim their completion.
- **Windows field validation remains pending:** no Windows build, selftest, installation,
  registration or native runtime/UI execution occurred. Linux compilation and fake
  adapters do not establish native WinForms/WebView2, WASAPI/WinRT, DPAPI, Hyper-V,
  OpenSSH or update behavior.

No merge-induced regression was found and no new design deviation was introduced.
The cleanup's existing deviations (including unified settings/activation seams,
Unicode profile-name parity and ShellExecute-only VS Code links) remain recorded
under Deviations in the frozen plan. The branch is handed over by moving the local
`feat/companion` ref; nothing is pushed or merged into main.
