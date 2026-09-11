# Construct Companion stage 3 integration results

Validated on Linux on 2026-09-11 in `cc/integ-3`, starting from `feat/companion`
commit `73f838c`. The Windows app target compiled on Linux; no Windows execution.

## Integration

Reset to `feat/companion`, then merged in order with `git merge --no-ff`:

| Branch | Tip | Commits beyond starting `feat/companion` | Merge |
|---|---|---:|---|
| `cc/s2-ipc` | `c56aa95` | 1 | `a5453bf` |
| `cc/s2-app` | `f41c334` | 4 | `79def98` |
| `cc/s2-install` | `c67dec4` | 1 | `93d2798` |

None skipped. IPC merged cleanly. App conflicted in `companion/README.md` and
`extension/test/export-parity-fixtures.js`: retained both package descriptions,
unsupported-message documentation, validation notes and the desktop/host-admin
exporter functions, preserving asynchronous `exportAll()`. Obsolete scaffold
quit/selftest text was replaced by the app's actual bootstrap behavior. Install
conflicted in `docs/plans/construct-companion.md`: retained all app and installer
deviations and the starting branch's D11 requirements. No solution, csproj,
composition-root, abstraction or extension wiring conflict required a resolution.

The merged contributions add the Kestrel IPC/dispatcher and host administration,
Windows platform adapters and tray/WebView2 app, per-user installer/update hooks,
package builder and release workflow. They touch `companion/`, `extension/media/`,
`extension/src/themes.js`, the parity exporter/fixtures, `Auto-Install.ps1`,
`Update-Construct.ps1`, `lib/Construct.Companion.ps1`, installer/package tests,
`.github/workflows/companion-release.yml`, and related docs. No service, `bin/`, or
`extension/vm/` code changed. `cc/t3-companion` is in another repository and was
not merged. No push, main-branch merge, or worktree operation was performed.

## Integration fix

The app intentionally publishes `ui-endpoint.json` until its full runtime host is
connected. The installer only read `endpoint.json`, so it could attempt to replace
or remove a running bootstrap app without requesting graceful exit. Quit discovery
now checks the full endpoint first, then the private endpoint when absent or its
PID is dead. Existing validation, authenticated HTTP quit, 15-second timeout and
no-kill behavior apply to either endpoint. Four added assertions cover bootstrap
uninstall reason, dead full endpoint followed by live bootstrap, timeout rejection,
and no swap on timeout. The installer suite passes 75 assertions.

Integration-only edits: `lib/Construct.Companion.ps1`,
`test/companion-install.test.ps1`, `companion/README.md`, `docs/companion.md`,
the stage 3 notes/deviation in the frozen plan, and this report. The docs now state
that headless selftest is implemented but does not establish production runtime
composition; they no longer claim a failing scaffold stub prevents release.

## Validation

- `dotnet build companion/Construct.Companion.sln -warnaserror`: **0 warnings,
  0 errors**, including the Windows target compiled on Linux.
- `dotnet test companion/Construct.Companion.sln`: **4,681 passed, 0 failed,
  0 skipped**. Includes real ephemeral Kestrel/dispatcher/fake-runtime end-to-end
  tests and the portable selftest checks, including real loopback health without
  endpoint publication. `ConstructCompanion.exe --selftest` was not run on Linux.
- `dotnet test service/Constructd.sln`: **1,261 passed, 0 failed, 0 skipped**.
  The existing xUnit2029 analyzer warning at `Updates/PackageTests.cs:91` remains.
- Ran all **31 Node**, **29 PowerShell** (23 under `test/`, 6 recursively under
  `service/tests/`), and **22 Bash** suite files. Final: **31/31 Node**,
  **29/29 PowerShell after one environment-only retry**, **20/22 Bash**.
- Two individual optional checks skipped: VS Code proxy agent unavailable and
  Windows DPAPI round trip unavailable on Linux.
- Companion installer: **75 assertions passed**. Linux package layout/checksum/
  fake-install suite: **34 assertions passed**, using fixture binaries.
- Parity regenerated twice, including after the full matrix: **4,426 rows in
  29 areas**, with **zero fixture drift**. No integration fixture edits.
- At most one .NET build/test ran at a time, including .NET calls inside Bash
  suites. No test runtime processes remained in this worktree after completion.
  No existing VM service was stopped or restarted.

Counts below are the units each suite reports (tests, assertions, scenarios or
fixtures); unlike units are not summed. A suite without a count says so.

## Per-suite results

| Suite | Result |
|---|---|
| `companion/Construct.Companion.sln` | 4,681 passed, 0 failed, 0 skipped |
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
| `extension/test/parity.test.js` | 4,426 fixtures passed across 29 areas |
| `extension/test/probe.test.js` | 98/98 passed |
| `extension/test/project-set.test.js` | 63 passed, 0 failed |
| `extension/test/projects.test.js` | 167/167 passed |
| `extension/test/remote.test.js` | 79/79 passed |
| `extension/test/remotehost-proxy.test.js` | 1 passed, 0 failed, 1 skipped |
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
| `test/companion-install.test.ps1` | PASS: 75 Companion installer assertions |
| `test/companion-package.test.ps1` | PASS: 34 Companion package assertions |
| `test/config-sync.test.ps1` | 568/568 passed on process-local retry; initial 561 passed, 7 failed |
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
| `test/t3-reprovision-host.test.ps1` | Passed (3 saved Git choices; no assertion count emitted) |
| `test/autoinstall-iso.test.sh` | 59 passed, 0 failed |
| `test/construct-expose.test.sh` | 170 passed, 0 failed |
| `test/construct-notify.test.sh` | 36 passed, 0 failed |
| `test/construct-vm.test.sh` | 172 passed, 0 failed |
| `test/contracts-compile.test.sh` | 4 passed, 1 failed |
| `test/export-config.test.sh` | 12/12 passed |
| `test/external-host.test.sh` | 61 passed, 0 failed |
| `test/host-admin-e2e.test.sh` | 1 xUnit test passed, containing 70 end-to-end checks |
| `test/host-package.test.sh` | host-package: 109 assertions passed (98 payload files); fixture executable, no Windows publish performed |
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

## Recorded defects and limitations

- **PowerShell config-sync default branch dependency (pre-existing):** initial
  561/568; retry 568/568 with the six documented `CONSTRUCT_*` variables unset
  and `init.defaultBranch=main` appended through process-local `GIT_CONFIG_COUNT`,
  `GIT_CONFIG_KEY_n` and `GIT_CONFIG_VALUE_n`. No global/repository Git config
  changed. Node configsync passed 475/475 initially.
- **`test/contracts-compile.test.sh` (pre-existing):** 4 passed, 1 failed;
  `Constructd.Core.Abstractions.HypervisorVmInfo` differs from the frozen public
  signature. Test, contract document and service are unchanged from the starting
  ref. Matches the [stage 1 baseline evidence](construct-companion-stage-1-integration.md).
- **`test/idle-report.test.sh` (pre-existing):** 100 passed, 3 failed: exactly two
  service keys, service URL, and instance name. Test and production code are
  unchanged; the same failures were reproduced during stages 1 and 2.
- **Production app composition remains unfinished in the input branches:** the
  app's `Program.cs` constructs the bootstrap refusal sink and private activation
  server, so the desktop runs no forward/notification/audio/probe runtimes and the
  extension remains in fallback. Wiring the real dispatcher and native seams,
  UI-thread prompts/activation, event streams, shared settings store and host
  catalog is more than a few-line merge repair. The full IPC host works under
  tested fake composition. Selftest can pass without instances and therefore does
  not guard the release workflow against publishing the incomplete bootstrap.
- **IPC branch gaps:** automatic config-sync ticks/watchers, periodic usage/update
  enrichment, lifecycle/checkpoint result monitoring, and ending host-admin polls
  when windows close remain unwired. Explicit refused workflows/subflows remain
  documented under [Unsupported messages](../../companion/README.md#unsupported-messages).
  Desktop remote-state selftest/catalog enrichment also awaits composition.
- **D11 gaps:** `Install-ConstructCompanion.ps1`, the extension's
  `construct.installCompanion` command and session install offer are absent.
  Auto-Install's hook is before the local/remote decision, but D11's fake-installer
  tests for each local/remote/update entry path are absent; the helper itself is
  tested. Manual library installation is available.
- **Windows field validation remains outstanding:** WinForms/WebView2, native
  registration, WinRT toast projection loading, WASAPI/device continuity, actual
  Hyper-V/SSH, and installed selftest/update behavior were not executed on Windows.

The test failures and inherited implementation gaps are also recorded under
[Integration notes (stage 3)](construct-companion.md#integration-notes-stage-3), as
requested. No unresolved merge-induced regression was found after the quit fix;
this is a validated branch integration, not a claim of complete production scope.
