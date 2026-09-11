# Construct Companion

Per-user Windows host agent with the S2a state/runtime/config-sync packages and S2b
Windows services and tray/WebView2 application. The frozen design is
[construct-companion.md](../docs/plans/construct-companion.md). The parallel S2b IPC
package owns the full HTTP server and dispatcher; S3 connects it to this application.

## Layout

| Project | Responsibility |
|---|---|
| `src/Construct.Companion.Core` | Package-free interfaces and runtime logic, IPC records, command-line grammar, shared scripts, planners and argv builders |
| `src/Construct.Companion.Host` | Process/socket adapters, instance supervision, outbound message bus and config-sync Git transactions; references `Microsoft.AspNetCore.App` |
| `src/Construct.Companion.Windows` | `net10.0` DPAPI, CIM, WASAPI, WinRT toast, HKCU, detached launch and native UI adapters |
| `src/Construct.Companion` | `net10.0-windows10.0.17763.0` WinForms WinExe, self-contained `win-x64`, WebView2, linked `extension/media/**` |
| `src/Construct.Companion.Fakes` | In-memory implementation of every Core seam; recording processes and scripted guest spool |
| `tests/Construct.Companion.Tests` | Linux xUnit tests and shared golden-fixture consumers |

The executable is `ConstructCompanion.exe`. It has DPI-aware state/update icons, the
tray popup and context menu, reusable panel/settings/host-admin/design windows, and
native dialogs. `--settings` clicks the existing panel settings control; the settings
window also offers design and microphone-device pickers. Every WebView2 instance is
kept alive when hidden, including the popup. Bounds persist in `settings.json`.

All section 6 command-line options are combinable. URI instance/host query values
are checked against the registry, and forward IDs use the shared forward guard.
`--version` prints the assembly version. `--selftest [--json] [--instance name]` runs
without WinForms, settings writes, endpoint publication, tunnels, capture or acks;
it prints JSON and exits 0 when required local checks pass (1 otherwise, 2 for invalid
arguments). Offline VM probes are informational. Uninstalled hosts with no instances
or toast registration can pass; an installed build requires its toast identity.

The standalone S2b entry point deliberately has no runtime composition. Its bridge
answers readiness and explicitly refuses runtime commands. It publishes only private
`companion/ui-endpoint.json` for authenticated HTTP activation and quit, leaving the
extension's runtime fallback active. The mutex is `Local\ConstructCompanion`; there
is no named quit event. Secondary processes validate the health PID/start time and
hand off all requested views through `/v1/ui/activate`, or quit through `/v1/quit`.
The private endpoint is removed when its listener exits. The full S3 IPC host uses
the frozen `endpoint.json` path; the secondary client can discover either endpoint.

S3 composition hooks:

- Inject `IMessageSink` into `TrayContext`/`WebViewWindow`. `InProcessMessageSink`
  adapts a dispatcher delegate and `RuntimeMessageBus`; windows subscribe before
  posting `ready`. Host-admin uses scope `host:<slug>` and `hostadmin.ready`.
- Supply `TrayContext.Prompts` as `IPrompts` and `TrayContext` as `IUiActivation` to
  the full host. Replace the bootstrap sink/server in `Program` with that host,
  including runtime cleanup before process exit.
- Use the same `SettingsStore` instance for IPC settings and desktop preferences.
  Bind settings changes into the runtime, enrich host discovery with the full
  enrolled-host catalog, and supply the remote-state delegate to
  `DesktopSelfTestPlatform` (local CIM/Get-VM and SSH checks already run).

The current host list is derived from registered instances. The bootstrap cannot
administer an enrolled remote host with no registry instance. Remote selftest state
is `unknown` until S3 supplies the existing remote-client adapter. These are
composition dependencies, not claims that runtime jobs work in the standalone app.

## Layering rules

- Core has zero package references. Process, file, socket, registry, HTTP, prompt, toast,
  capture, credential, and launch operations go through `Core/Abstractions`.
- `ISshTransport` is bound to an instance. It provides one-shot scripts, streaming watches,
  tunnel processes, and explicit-bind port probing. Remote HTTP uses `IRemoteApi`.
- Every invocation is an executable plus argv, with optional separate stdin. Never invoke
  a host shell to interpret a command string. SSH's remote script is intentionally a single
  argument encoded by `SshArgs.WrapScriptCommand`. Recording-runner tests pin argv.
- Callers own `IRunningProcess`: consume stdout/stderr, observe completion, then stop/dispose
  to reap it. Audio enumeration owns capture until cancellation or disposal.
- Windows-only code carries `[SupportedOSPlatform("windows")]` and is constructed only after
  `OperatingSystem.IsWindows()`. The Windows project is not a Windows TFM; the app is.
- `IpcJson.Options` is the camelCase wire serializer. Webview messages remain `JsonElement`;
  partial settings and companion event extension fields preserve unknown fields.
- Tokens/passwords never enter logs, exception text, argv, or test output. `Secret` exposes
  plaintext only explicitly and redacts its diagnostic string. HTTP pin verification occurs
  before sending credentials; token storage must remain compatible with existing DPAPI files.
- Keep shared behavior fixtures alongside changes. Add members to the existing seams only
  when an implementing package needs them and record the design deviation as specified.

`ScriptedGuestSpool.ExpectRun` matches an exact script and applies a caller-programmed
state transition to requests/acks/closes. Unexpected scripts fail without echoing payloads.
Watch processes accept chunks, stderr, exit/failure, and disposal. Notification drain removes
entries once. This is a programmable test seam, not a Bash interpreter or an implementation
of ownership/TTL/planner logic; runtime tests supply those scenarios through the real runtime.

## Build and test

Requires the .NET 10 SDK and Node.js. From the repository root, on Linux:

```sh
dotnet build companion/Construct.Companion.sln -warnaserror
dotnet test companion/Construct.Companion.sln
node extension/test/parity.test.js
for f in extension/test/*.test.js; do node "$f" || exit 1; done
dotnet test service/Constructd.sln
```

Run at most one dotnet build/test at a time. The app compiles on Linux using
`EnableWindowsTargeting`; its WebView2 WPF assembly is explicitly excluded because this
WinForms app does not use WPF and that reference otherwise produces MSB3277.
Tests use xUnit 2.9.3, Test SDK 17.14.1 and the VS runner 3.1.4, matching the service.
No Windows runtime execution has been performed. Tray visibility, second-process quit,
and redirected console output still require Windows field validation.

The S2a extension branch pins the Node config-sync bare test remotes to `main`, fixing
the baseline invalid-HEAD test dependency. Retry environment-sensitive Node failures with inherited
`CONSTRUCT_SERVICE_URL`, `CONSTRUCT_SERVICE_CA_FILE`, `CONSTRUCT_EXTERNAL_HOST`,
`CONSTRUCT_EXTERNAL_SSH_PORT`, `CONSTRUCT_INSTANCE_NAME`, and `CONSTRUCT_T3_VOICE_INPUT` unset.
The PowerShell config-sync suite still needs child Git processes to use
`init.defaultBranch=main` (a process-local `GIT_CONFIG_COUNT` override; no global config
change). The full S1 regression run additionally reproduced two existing failures on the
untouched starting commit `4dbb0ea`: `contracts-compile.test.sh` disagrees with the frozen
`HypervisorVmInfo` signature (4/5 checks pass), and `idle-report.test.sh` fails three
service-key assertions (100/103 pass). These files and their production code are unchanged.

The [stage 2 integration report](../docs/plans/construct-companion-stage-2-integration.md)
records the combined validation: 4,332 Companion tests, 1,261 service tests, all 31 Node
suites, all 27 PowerShell suites after the documented retry, and 20/22 Bash suites.
The same two baseline Bash defects remain. All 4,192 parity rows in 27 areas regenerate
without drift. Both runtime/config-sync registration orders preserve temporary Git indexes.

## Shared scripts and parity

`extension/vm/*.sh` is the source of guest script bytes. JS loads the S1 sources once via
`extension/src/guest-scripts.js`; Core embeds these same files, with stable resource names.
`GuestScripts.Render(name, values)` takes a basename without `.sh`. Substitution is a single
pass: `{{name}}` in an input value is data. Unknown templates and missing values fail.
Values are shell-ready fragments prepared by callers; existing validated integers, IDs and
fixed script composition retain their old spelling to preserve exact script bytes.
`ShellQuote.Single` matches the JS `shQuote`: wrap in single quotes and replace `'` with `'\''`.
Neither template renderer automatically quotes values.

Generate deterministic, recursively key-sorted fixtures with:

```sh
node extension/test/export-parity-fixtures.js
```

One exported function owns each area: guest scripts, SSH argv, host-label matrix, shell
quoting, state/lifecycle decisions, config-sync planners/scripts/sharing, forwards,
notifications, audio, and repatch. Fixtures live in `test/fixtures/companion-parity/*.json`.
SSH fixture key paths use an explicit `/fixture/home` root rather than the machine's home.
JS re-exports in memory and
diffs the committed bytes; C# consumes the same files copied into test output. Guest tests
also compare actual JS builder outputs with rendered fixtures. Add future areas to this
exporter and commit fixture changes with the matching implementations.

Repatch already reads `construct-patch-status.sh` and `construct-partial-streaming-enable.sh`
and invokes the audio wrapper; those files are embedded and covered directly rather than
copied into a redundant `repatch.sh`. T3 pairing uses default/instance templates to preserve
the exact existing variants. Existing Node tests were not modified. `test/provision-marker.test.sh` now checks the
extracted `probe.sh` for the marker-read contract and separately checks that `probe.js`
renders it; its former assertion searched the JavaScript builder's internal source.

## Unsupported messages

The standalone S2b app handles `openPanel`, `setInstance`, `pickTheme`, and local
`command` IDs `chooseTheme`, `chooseMicDevice`, `showLogs`, `openHostAdmin`. It
answers `ready` / `hostadmin.ready` with an offline/unavailable state. All remaining
runtime messages/commands are forwarded unchanged through `IMessageSink`; without
the S3 dispatcher binding they receive `lifecyclePrepared` plus an explicit desktop
refusal. The full dispatcher message matrix belongs to S2b IPC/S3.

## S2a state

S2a state APIs are under `Core/State`, `Core/Lifecycle`, `Core/Probe`,
`Core/Drivers`, and `Core/Remote`. `HostState` discovers the scripts directory and
manages shared profiles; `InstanceRegistry` validates schema v1 and produces the
shared registry document; `InstanceStateStore` preserves the legacy default store
and isolates named VM state. `SettingsMapping`, `ProbeParser`, `UsageParser`,
`UpdatePlanner`, `AgentUpdateScript`, `T3Code`, `LifecycleBuilder`, `PowerShellLaunch`, `HostConversion`
and `HostUpdatePlanner` expose decisions without timers or UI. `ResultPollingPlan`
and `ProvisionWatch` capture the launched operation's target and deadline.

`RemoteHostClient` exposes all 54 methods from remotehost.js over `IRemoteApi`,
with tokens read from `ITokenStore`, an explicit Negotiate flag, and certificate
pin verification before the transport sends credentials. `VmPower` collapses
saved/paused states for actions, keeps the remote saved label through refinement,
and falls back from the local hypervisor seam to the fixture-pinned Get-VM argv.
`IStateFileSystem` extends the scaffold seam with timestamps and atomic profile
creation; `IUpdateSource` fetches public release metadata with normal CA validation.
Both have in-memory fakes; production adapters belong to the Host/Windows packages.

State JSON writes use BOM-less UTF-8, a final newline, and JavaScript-compatible
quoting, number formatting and integer-key ordering. Reads tolerate a PowerShell
UTF-8 BOM. Named state files retain the version/instance header and ordinal VM-key
ordering; install-wide values never leak from another VM's legacy settings.
The state tests include a real temporary LOCALAPPDATA tree as well as in-memory
stores. New parity fixtures are generated asynchronously by the existing Node
exporter (including remote routes and paginated stable/nightly T3 discovery).

This package provides pure planners and seam-driven reads/writes. Runtime polling,
IPC dispatch (including usage export dialog titles/file names from describeExport/exportFileName), production filesystem/HTTP adapters and UI wiring remain with their
respective work packages. No Windows runtime behavior has been field-validated.

## S2a runtime

The runtime package implements the forward wire/planner and local/remote transports,
SSH process supervision, notifications, shared on-demand audio, repatch, instance
runtimes, serialized registry reconciliation, and the outbound message bus. Core uses
only injected effect seams. Host contains the process/socket adapters; Fakes contains
scriptable implementations. `AddRuntime()` registers the host adapters, process-wide
port reservations, claim ID, message bus and shared capture. Register `IAudioCapture`
before resolving capture (the Windows implementation is WasapiAudioCapture).

Integration supplies `IRuntimeProbe` (the state package's full probe result), normalized
`RuntimeInstance` records through `IRuntimeRegistry`, and the per-instance factories.
`FileRuntimeRegistry` attaches that reader to the existing `IFileSystem.Watch` seam.
Its `Revision` changes for connection identity or external settings absent from the record;
keep it stable for a HostLabel-only change so the supervisor uses `SetHostLabelAsync`
(rebind on the same port across the loopback/wildcard boundary; re-ack only between names).
IPC audio toggles should call `InstanceRuntime.SetAudioAsync`, which serializes with probes
and suppresses auto-arm after an explicit disable. Automatic audio enable happens once on
the online edge, plus the single repatch retry when startup failed; ordinary probes never
re-run the enable script. Audio wire messages always stamp `instance`, omit unset tunnel/
gate fields, and optionally include a fixed `error` code for diagnostics.
`ProcessSshTransport` takes the resolved executable and instance key path; use
`SshExecutable.Resolve` with the host's PATH and SystemRoot. Remote transports take the
enrolled **user** credential or Negotiate, with the pin verifier; they never read a VM token.
The IPC package consumes `RuntimeMessageBus`, which retains snapshots and bounds each
subscriber queue; a slow subscriber reconnects and reads a snapshot.

Additional parity areas: `forward-runtime`, `notify-runtime`, `audio-runtime`, and
`repatch-runtime`. Notification XML has an optional launch URI on the JavaScript side;
its existing default stays unchanged, while Companion uses the specified instance URI.
The Linux tests run the real shared claim script concurrently and verify stale-claim
recovery, alongside fake guest-spool/tunnel/capture tests. These tests do not validate
WinRT, WASAPI, Hyper-V, or a real Windows OpenSSH client. Application/IPC composition
and Windows field validation remain with S2b/S3 and the owner.

S2a Linux validation: build 0 warnings/0 errors; Companion 2,870/2,870 tests; Node parity
2,802 fixtures in eight areas; service 1,261/1,261 tests. The full Node loop passes 29/30
suites with only the documented configsync HEAD baseline (also reproduced with the
CONSTRUCT variables unset). Targeted regressions pass: expose 170/170, notify 36/36,
patch-status 12/12, partial-streaming 6/6, and PowerShell toast 22/22.

## Config sync integration (S2a)

`AddConfigSync()` registers portable Git/filesystem adapters and
`ConfigSyncFactory`. Create one area per instance with its captured SSH transport and
config branch. All areas share the host config directory and staging-cache root.
The factory queues local work per config directory; `.sync.lock` coordinates repo writes
with the extension and PowerShell. Remote network calls never hold that disk lock.
The runtime exposes `TickAutoAsync()` for the supervisor's poll (five-minute per-instance
throttle), `SyncNowAsync()` for explicit/preflight sync, `StartWatching()` for the two-second
projects-folder debounce, `BuildStateAsync()` for the exact `configSync` wire block, and
`Synced` for state/profile-discovery follow-up. Dispose the area when removing a runtime.
The dispatcher should call `Actions` for link/remove/import/share/push/publish; after a
successful import, call that captured instance's `SyncNowAsync()`. For sharing, callers must
pass `constructRepo`/`constructRef` from the captured scripts directory's raw settings to
`Actions.ShareAsync(installRepo, installRef)`; these produce fork-correct installer links. Profile discovery and
selection remain the projects/runtime integration's responsibility. `DeletedProfileIdentitiesAsync`
provides the history-based suppression set for discovery.

Supply `IClock`, `IPrompts`, and `IClipboard` from host/app composition. Publish picker
items carry `Disabled` and `Separator`, and pickers carry `Placeholder`; the dialog adapter
must honor them. Action results carry display messages and warnings for the UI. Windows
clipboard implementation and dispatcher wiring belong to S3; WinForms dialogs are supplied by S2b. No Windows runtime
validation has been performed. Real-Git config-sync tests run on Linux and require `git`,
`bash`, `printf`, and `sleep` on PATH; each uses and deletes its own temporary repositories.

The port rejects legacy credential-bearing remote URLs before git argv; re-link those URLs
without credentials and use a credential helper. Reserved seed profiles are ignored in the
shared repo, including for subsequent extension/PowerShell staging. New/renamed imports are
strictly validated before writing. These small differences are recorded in the frozen plan.

S2a Linux validation: Companion build 0 warnings/errors; Companion tests 412/412;
service tests 1,261/1,261; Node suites 30/30; PowerShell config-sync 568/568; parity
333 rows across five areas. Node/PowerShell use the process-local default-branch override
described above. No extension implementation, installer, guest script, or service code changed.


S2b desktop/platform increment: `Core/Desktop` owns activation validation, tray/menu
models, placement, settings, registration and token/launcher policies. New seams are
`IMessageSink` (PostAsync + cancellable Subscribe, `host:<slug>` for host-admin),
`IDataProtection`, `ICimVmQuery`, `IDesktopProcess`, `IUiActivation`, and
`ISelfTestPlatform`. Each native operation is isolated in Windows adapters, with
recording fakes for the policies. `ProcessInvocation.CreateNoWindow` is false for
lifecycle consoles and true for detached T3 Desktop starts. `VmPower.QueryLocalAsync`
remains the sole owner of the denied-CIM Get-VM fallback.

The theme picker template now lives in `extension/media/theme-picker.html`; both
JavaScript and Companion render it, with all three surface documents and picker
escaping covered by `desktop-webviews.json` golden fixtures.

Windows field checks still required: the app TFM and the Windows project reference
SDK projection 10.0.17763.57; verify that published `Microsoft.Windows.SDK.NET.dll`
and `WinRT.Runtime.dll` load together and that the toast selftest runs. WASAPI's
MediaFoundation resampler consumes a continuous, bounded capture queue so pauses
between callbacks do not signal end-of-stream. Recording continuity and actual
device formats still require a real device test. VS Code's
`code.cmd` ShellExecute launch may briefly display a console. No Windows run is claimed.


S2b validation on Linux: build gate 0 warnings/0 errors; all Node suites and
service tests passed (31 suite files and 1,261 tests). The relay command
`dotnet --list-sdks` did not answer within ten minutes despite `--timeout 300`;
its client was cancelled and reaped. No Windows compilation or selftest ran.
The Windows field checklist still includes WebView2 focus/placement, console
handoff/output, actual microphone sample continuity, and toast projection loading.
