# Construct Companion

S1 scaffold and S2a config-sync library for the per-user Windows host agent. The frozen design is
[construct-companion.md](../docs/plans/construct-companion.md). Other runtime jobs, HTTP IPC,
platform adapters, and webview dispatch are subsequent work packages.

## Layout

| Project | Responsibility |
|---|---|
| `src/Construct.Companion.Core` | Package-free interfaces, IPC records, command-line grammar, shared guest scripts, SSH argv, host-label rules and config-sync planners |
| `src/Construct.Companion.Host` | Config-sync runtime, Git transactions and prompt flows; IPC reserved for S2b; references `Microsoft.AspNetCore.App` |
| `src/Construct.Companion.Windows` | `net10.0` Windows adapters; currently single-instance/quit signaling and icon handle cleanup |
| `src/Construct.Companion` | `net10.0-windows10.0.17763.0` WinForms WinExe, self-contained `win-x64`, WebView2, linked `extension/media/**` |
| `src/Construct.Companion.Fakes` | In-memory implementation of every Core seam; recording processes and scripted guest spool |
| `tests/Construct.Companion.Tests` | Linux xUnit tests and shared golden-fixture consumers |

The executable is `ConstructCompanion.exe`. It currently shows a grey question-mark tray
icon with no registered instance and a Quit menu. `--version` prints the assembly version;
`--quit` signals the running scaffold (or exits successfully if absent). `--selftest`
(and `--selftest --json`) prints nine checks as `not implemented`, with `ok:false` and exit 1.
All section 6 options are parsed and combinable; view activation and registry validation
of URI query values belong to S2. The default instance is not inferred by the parser.

The scaffold uses `Local\ConstructCompanion` for its mutex and a temporary named event
for quit. A second launch exits 0. HTTP activation forwarding and `/v1/quit` are pending
S2 IPC/app integration. No endpoint file, listeners, tunnels, registrations, or timers for
runtime jobs are created yet. The Host project is currently a library; its fake-mode
console entry belongs with the runtime/IPC composition root.

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
of ownership/TTL/planner logic; S2 tests supply those scenarios through the real runtime.

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
No Windows runtime execution has been performed for S1. Tray visibility, second-process
quit, and redirected console output still require Windows field validation.

The known baseline `configsync.test.js` failure is a bare test remote whose default branch
has no valid HEAD. Retry environment-sensitive Node failures with inherited
`CONSTRUCT_SERVICE_URL`, `CONSTRUCT_SERVICE_CA_FILE`, `CONSTRUCT_EXTERNAL_HOST`,
`CONSTRUCT_EXTERNAL_SSH_PORT`, `CONSTRUCT_INSTANCE_NAME`, and `CONSTRUCT_T3_VOICE_INPUT` unset.
Both the Node and PowerShell config-sync suites pass when their child Git processes also
use `init.defaultBranch=main` (a process-local `GIT_CONFIG_COUNT` override; no global config
change). The full S1 regression run additionally reproduced two existing failures on the
untouched starting commit `4dbb0ea`: `contracts-compile.test.sh` disagrees with the frozen
`HypervisorVmInfo` signature (4/5 checks pass), and `idle-report.test.sh` fails three
service-key assertions (100/103 pass). These files and their production code are unchanged.

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
quoting, and config-sync planners/scripts/sharing. Fixtures live in `test/fixtures/companion-parity/*.json`. SSH fixture key paths use
an explicit `/fixture/home` root rather than the machine's home. JS re-exports in memory and
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

Placeholder for the S2/S3 dispatcher message matrix. All webview messages and command IDs
are currently unsupported because S1 has no dispatcher, webview windows, or HTTP listener.
S2/S3 must implement each protocol entry or list its reason here and return the panel's
existing refusal message; messages must never be silently ignored by an active dispatcher.

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
clipboard/dialog implementations and dispatcher wiring belong to S2b/S3. No Windows runtime
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
