# Construct Companion

The Windows tray app and the portable IPC Host form one program. `Program.cs` supplies
native filesystem, DPAPI, pinned HTTP, launch, toast, microphone, prompt and clipboard
adapters to `AddCompanionHost`. The same dispatcher handles authenticated HTTP clients,
WebView2 windows and tray runtime actions; a second process hands its command line to the
running one through the same authenticated routes (`endpoint.json`).

## Layout

| Project | Responsibility |
|---|---|
| `src/Construct.Companion.Core` | Package-free interfaces and runtime logic, IPC records, command-line grammar, shared scripts, planners and argv builders |
| `src/Construct.Companion.Host` | Process/socket adapters, instance supervision, outbound message bus and config-sync Git transactions; references `Microsoft.AspNetCore.App` |
| `src/Construct.Companion.Windows` | `net10.0` DPAPI, CIM, WASAPI, WinRT toast, HKCU, detached launch and native UI adapters |
| `src/Construct.Companion` | `net10.0-windows10.0.17763.0` WinForms WinExe, self-contained `win-x64`, WebView2, linked `extension/media/**` |
| `src/Construct.Companion.Fakes` | In-memory implementation of every Core seam; recording processes and scripted guest spool |
| `tests/Construct.Companion.Tests` | Linux xUnit tests and shared golden-fixture consumers |

## Layering rules

- Core has zero package references; every effect seam has an in-memory fake. Process, file, socket, registry, HTTP, prompt, toast,
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
- Keep shared behavior fixtures in the same commit as behavior changes. Add members to the existing seams only
  when an implementing package needs them and record the design deviation as specified.

## Build and test

Run only the groups for affected features; reserve `all` for integration merges and release preparation (see [local checks](../docs/local-checks.md)).

Run `bash test/run-local-checks.sh companion` for the full Companion gate (also
included in `all`). GitHub Actions only builds and publishes the self-contained
Windows release; regression checks run locally. Individual commands:

```sh
dotnet build companion/Construct.Companion.sln -warnaserror
dotnet test companion/Construct.Companion.sln
node extension/test/companion-host.test.js
node extension/test/parity.test.js
pwsh -NoProfile -File test/companion-install.test.ps1
pwsh -NoProfile -File test/companion-entrypoints.test.ps1
pwsh -NoProfile -File test/companion-package.test.ps1
```

The WinForms target compiles on Linux with Windows targeting enabled. This does not
exercise WinForms, WebView2, WASAPI, WinRT, DPAPI, UAC or Windows OpenSSH at runtime.
The portable Host uses real Kestrel with fake platform seams:

```sh
dotnet run --project companion/src/Construct.Companion.Host -- --fake
dotnet run --project companion/src/Construct.Companion.Host -- --fake --remote-only
```

It prints a test-only endpoint document once; its state filesystem is in memory.
`--fake` seeds one local and one remote instance; `--remote-only` seeds only the remote
one. Both are tested through `extension/src/companion.js`. Quit via
the authenticated API; tests always stop and dispose their servers and child processes.

## Composition and lifetime

The app marshals activations, dialogs and clipboard work to its STA thread and binds the
shared webview media through `DispatcherMessageSink`, the in-process twin of the HTTP routes.

`CompanionInstances` normalizes registry records and creates an SSH transport,
forwarder, notifier, audio session, repatch job and config-sync area for each instance.
The supervisor serializes registry changes and waits for in-flight commands before
retargeting. Settings control forwards/host label, notifications, microphone device
and repatch delay. Host-label-only changes keep the local port. The desktop and IPC
share the settings store; theme and active-instance updates reach the desktop.

Config project watchers start with their areas. Automatic config sync is throttled
by its five-minute runtime policy. Host and update metadata refresh periodically without
duplicating the runtime probe. Public update results are shared and cached for five minutes
(one minute on failure); usage is collected on demand per instance/period and cached for
five minutes (one minute on failure), matching the JavaScript TTL fixtures.
Construct update detection uses the complete published main release manifest;
custom refs remain manual. Guest inventory includes accessible shared VMs and emits
`{type:"children", instance, children}` independently of the SSH status refresh.
Host administration offers the shared **VM settings…** dialog for CPU, fixed RAM,
idle timeout and idle action. Hardware stays pending until a confirmed full stop/start;
idle applies immediately within the same host cap for admins and owners. Partial saves
reload authoritative values and keep the dialog open. The `hostadmin.action` port
handles `loadVmSettings` / `setVmSettings` and replies with `hostadmin.vmSettings`
(`name`, `requestId`, `settings`, `saved`, `error`); older hosts disable unavailable hardware fields.
Guest browser consoles and T3 pairing
allow 90 seconds for gateway forwarding. Console tickets are never logged or stored.
Per-instance command queues keep long operations alive after client disconnects.
State aggregation supplies the existing nested panel state and snapshot messages.
Host-admin windows subscribe by host slug and release their polling subscription when
hidden. Enrolled hosts are available even before a VM is registered.

On tray quit, session end or `/v1/quit`, the app stops the Host, closes tunnels,
releases owned claims and removes the endpoint. A secondary process forwards validated
activation/URI arguments to the primary. Selftest runs the real IPC health path with
runtime jobs and endpoint publication disabled, probes instances once, and checks
native devices/WebView2/toast identity without starting tunnels or writing guest acks.
Remote-only installations never need local Hyper-V for diagnostic success.

## Shared contracts

`extension/media` is copied unchanged into the app; `extension/vm/*.sh` is embedded
into Core and used by JavaScript too. Golden fixtures in
`test/fixtures/companion-parity` are regenerated by:

```sh
node extension/test/export-parity-fixtures.js
```

Each exporter owns one area. C# and Node consume the same committed bytes. The
`integration-settings` area runs the actual JS writer and compares default/named
instance files against writes through the real HTTP dispatcher. External launches
use argv arrays and existing parity-tested builders. Secrets are never logged or
placed in process arguments.

## Installation

See [the user guide](../docs/companion.md): `Install-ConstructCompanion.ps1` wraps the
per-user library; `Auto-Install.ps1`, `Update-Construct.ps1` and a plain reprovision reach
the same non-blocking, opt-out-aware hook. Installation always targets the client PC.

## Unsupported messages

The dispatcher implements all top-level panel message types. Unknown types/commands
return `{type:"lifecyclePrepared", id, error}`; both shared webviews display `error`.
No known panel or host-administration workflow remains unsupported. These legacy input differences are intentional:

| Command | Reason / alternative |
|---|---|
| Lifecycle import malformed legacy names | Scanned profiles use the strict profile validation gate; invalid/reserved names are reported as failed writes before the continue-anyway prompt. |
| `saveProject` malformed legacy values | Uses the strict validation and canonicalization gate rather than the extension modal's legacy schema coercion. Invalid or reserved profiles are refused before writing. |

Other panel command IDs are routed in `Host/Dispatch/MessageDispatcher.cs`; host-admin
messages/actions are routed in `Host/Dispatch/HostAdministration.cs`. Bad names, form
values and routes return RFC 7807 problems. Host-admin refusals also publish a visible
`state.notice`; unknown panel operations publish the visible error above.


## Message matrix

Opening surfaces tag `ready` with `surfaceOpened` for one manifest bypass; `snapshotOnly` replays an already refreshed snapshot. Ordinary selection/refresh messages retain the cache.

The known dispatcher sets (`MessageDispatcher.KnownMessages`/`KnownCommands`) are checked
against this table and the extension source by `ProtocolMatrixTests`. Panel messages and
commands run through the dispatcher and the desktop seams; host messages through
`HostAdministration`; extension commands stay in VS Code (registry edits are local, Companion
views and runtime messages use IPC). The limitations of the implemented rows are listed above.

| Kind | Message or command | Status |
|---|---|---|
| message | `applyVmResources` | implemented |
| message | `command` | implemented |
| message | `customRebuild` | implemented |
| message | `openPanel` | implemented |
| message | `ready` | implemented |
| message | `saveIdlePolicy` | implemented |
| message | `saveProject` | implemented |
| message | `saveSettings` | implemented |
| message | `setAudio` | implemented |
| message | `setInstance` | implemented |
| message | `setUsagePeriod` | implemented |
| command | `addConfigRemote` | implemented |
| command | `addProject` | implemented |
| command | `addRemoteAndPublish` | implemented |
| command | `childConsole` | implemented |
| command | `childDelete` | implemented |
| command | `childShutdown` | implemented |
| command | `chooseMicDevice` | implemented |
| command | `chooseTheme` | implemented |
| command | `closeForward` | implemented |
| command | `connect` | implemented |
| command | `convertToHost` | implemented |
| command | `createFirstVm` | implemented |
| command | `deleteProject` | implemented |
| command | `editProject` | implemented |
| command | `exportConfig` | implemented |
| command | `exportUsage` | implemented |
| command | `importRemoteConfigs` | implemented |
| command | `installGit` | implemented |
| command | `openAgentWeb` | implemented |
| command | `openConfigRepo` | implemented |
| command | `openForward` | implemented |
| command | `openHostAdmin` | implemented |
| command | `openProject` | implemented |
| command | `openProjectFolder` | implemented |
| command | `publishConfigProfiles` | implemented |
| command | `pushConfigUpstream` | implemented |
| command | `redownload` | implemented |
| command | `refresh` | implemented |
| command | `registerThisVm` | implemented |
| command | `reinstall` | implemented |
| command | `removeConfigRemote` | implemented |
| command | `removeInstance` | implemented |
| command | `reprovision` | implemented |
| command | `selectProfiles` | implemented |
| command | `shareConfigs` | implemented |
| command | `showLogs` | implemented |
| command | `shutdown` | implemented |
| command | `startConnect` | implemented |
| command | `syncConfigNow` | implemented |
| command | `updateAgent` | implemented |
| command | `updateAgents` | implemented |
| command | `updateConstruct` | implemented |
| host message | `hostadmin.ready` | implemented |
| host message | `hostadmin.refresh` | implemented |
| host message | `hostadmin.tab` | implemented |
| host message | `hostadmin.signIn` | implemented |
| host message | `hostadmin.action` | implemented |
| extension command | `construct.openPanelHere` | implemented |
| extension command | `construct.openPanel` | implemented |
| extension command | `construct.refresh` | implemented |
| extension command | `construct.showLogs` | implemented |
| extension command | `construct.chooseTheme` | implemented |
| extension command | `construct.switchInstance` | implemented |
| extension command | `construct.addRemoteHost` | implemented |
| extension command | `construct.newRemoteVm` | implemented |
| extension command | `construct.registerThisVm` | implemented |
| extension command | `construct.removeInstance` | implemented |
| extension command | `construct.removeRemoteHost` | implemented |
| extension command | `construct.openHostAdmin` | implemented |
| extension command | `construct.installCompanion` | implemented |

The VM settings action protocol is shared with VS Code:

| Host action | Reply | Behavior |
|---|---|---|
| `loadVmSettings` | `hostadmin.vmSettings` | Load CPU, RAM and idle policy with feature/cap metadata. |
| `setVmSettings` | `hostadmin.vmSettings` | Validate changed hardware and idle policy, save sequentially, reload on partial failure. |
