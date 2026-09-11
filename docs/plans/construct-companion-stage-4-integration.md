# Construct Companion stage 4 integration results

Validated on Linux on 2026-09-11 in `cc/integ-4`, starting from `feat/companion`
commit `bbec973`. The Windows app target compiled on Linux; no Windows execution.

## Integration

Reset to `feat/companion`, then merged with `git merge --no-ff`:

| Branch | Tip | Commits beyond starting `feat/companion` | Merge |
|---|---|---:|---|
| `cc/s3-integration` | `adea0e4` | 1 | `8d6915d` |

No skipped branches or conflicts. No integration code repair or new design deviation
was needed. The merge preserves all S3 production composition, runtime/settings
integration, cached enrichment, desktop message routing, remote adapters, client
installer entrypoints and test additions.

The input commit changes 51 files: app/Core/Host code and tests under `companion/`,
`Auto-Install.ps1`, `Create-AgentVM.ps1`, `Provision-AgentVM.ps1`, the new root
`Install-ConstructCompanion.ps1`, `lib/Construct.Companion.ps1`, extension wiring,
manifest/client/tests, the parity exporter and two new fixture areas, PowerShell
entrypoint tests, and documentation. Stage 4 itself adds this report and the
stage 4 integration notes in `construct-companion.md`. No service, `bin/`, or
`extension/vm/` code changed. No T3 repository branch was part of this merge.

## Validation

- `dotnet build companion/Construct.Companion.sln -warnaserror`: **0 warnings,
  0 errors**, including the Windows target compiled on Linux.
- `dotnet test companion/Construct.Companion.sln`: **4,725 passed, 0 failed,
  0 skipped**.
- `dotnet test service/Constructd.sln`: **1,261 passed, 0 failed, 0 skipped**.
  Its existing xUnit2029 analyzer warning at `Updates/PackageTests.cs:91` remains.
- All **33/33 Node** suite files passed on the first attempt. The real Companion
  Host integration suite passed 20 checks, the install-offer suite 10, and the
  extension client suite 26. Node configsync passed 475/475 without a retry.
- All **30/30 PowerShell** suite files passed after the existing config-sync
  environment-only retry: 24 under `test/`, six recursively under `service/tests/`.
  Companion installer/entrypoint/package checks passed 75/33/34 assertions.
- All **22 Bash** suite files ran: **20 passed, 2 failed** with the existing
  contract-signature and idle-report failures detailed below.
- At most one .NET build/test ran at a time, including calls inside Bash suites.
  No test runtime processes remained in this worktree after completion. No
  existing VM service was stopped or restarted.
- Two optional checks skipped: VS Code proxy agent unavailable and Windows DPAPI
  unavailable on Linux. Counts below retain those skips.
- Regenerated **4,456 parity rows across 31 areas** twice, including after the
  complete matrix, with
  `node extension/test/export-parity-fixtures.js`; `git status` and `git diff`
  show **zero fixture drift**. No fixture changes were needed during integration.

Counts below are the units each suite reports (tests, assertions, scenarios or
fixtures); unlike units are not summed. Suites that emit no numeric count retain
their success summary rather than an invented assertion count.

## Per-suite results

| Suite | Result |
|---|---|
| `companion/Construct.Companion.sln` | 4725 passed, 0 failed, 0 skipped |
| `service/Constructd.sln` | 1261 passed, 0 failed, 0 skipped |
| `extension/test/audio.test.js` | audio unit tests — 233/233 passed |
| `extension/test/companion-host.test.js` | companion real host: 20 passed |
| `extension/test/companion-install.test.js` | companion install offer: 10 passed |
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
| `extension/test/parity.test.js` | Parity: 31 areas, 4456 fixtures passed |
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
| `test/companion-install.test.ps1` | PASS: 75 Companion installer assertions |
| `test/companion-package.test.ps1` | PASS: 34 Companion package assertions |
| `test/config-sync.test.ps1` | config-sync unit tests - 568/568 passed; initial 561/568, process-local default-branch retry |
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
| `test/host-admin-e2e.test.sh` | 1 xUnit test passed; 70 end-to-end checks passed |
| `test/host-package.test.sh` | host-package: 110 assertions passed (99 payload files); fixture executable, no Windows publish performed |
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

- **PowerShell config-sync default branch dependency (pre-existing):** initial
  561/568, retry 568/568 with the six documented `CONSTRUCT_*` variables unset
  and `init.defaultBranch=main` appended through process-local `GIT_CONFIG_COUNT`,
  `GIT_CONFIG_KEY_n` and `GIT_CONFIG_VALUE_n`. No global/repository Git config
  changed. Node configsync passed 475/475 initially.
- **`test/contracts-compile.test.sh` (pre-existing):** 4 passed, 1 failed;
  `Constructd.Core.Abstractions.HypervisorVmInfo` differs from the frozen public
  signature. Test, contract document and service are unchanged from `bbec973`;
  this matches the [stage 1 baseline evidence](construct-companion-stage-1-integration.md).
- **`test/idle-report.test.sh` (pre-existing):** 100 passed, 3 failed: exactly two
  service keys, service URL, and instance name. Test and production code are
  unchanged from `bbec973`; the failures match prior integration evidence.
- **Inherited incomplete workflows:** attached-window registration/conversion,
  project creation, instance removal, first-VM wizard, install-wide Construct
  update orchestration, one-time token display, automatic checkpoint apply,
  lifecycle preflight/live-project fallback and result monitoring remain as
  documented in [Unsupported messages](../../companion/README.md#unsupported-messages)
  and the S3 implementation notes. These require work beyond a few-line merge
  repair. Visible refusals are documented where applicable. Earlier stage 3
  input-merge notes about missing production composition and D11 entrypoints
  are superseded by the merged S3 implementation.
- **Windows field validation remains outstanding:** WinForms/WebView2, native
  registration, WinRT/WASAPI, DPAPI, actual Hyper-V/SSH, installed selftest and
  update behavior were not executed on Windows. This integration ran no Windows
  commands and made no installation or service changes.

No unresolved merge-induced regression was found. No new design deviation was
introduced. The owner amendment for plain reprovision installation and D11's
client installation paths were preserved unchanged. This report establishes the
branch integration and Linux regression results, not completion of the inherited
unsupported workflows or Windows field validation.
