# Construct Companion

The Windows tray app and the portable IPC Host form one program. `Program.cs` supplies
native filesystem, DPAPI, pinned HTTP, launch, toast, microphone, prompt and clipboard
adapters to `AddCompanionHost`. The same dispatcher handles authenticated HTTP clients,
WebView2 windows and tray runtime actions. The bootstrap activation server remains only
for compatibility tests; the production app publishes `endpoint.json`.

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
`--fake` seeds one local and one remote instance; `--remote-only` suppresses the
legacy local default. Both are tested through `extension/src/companion.js`. Quit via
the authenticated API; tests always stop and dispose their servers and child processes.

## Composition and lifetime

Core has no package references. Effect interfaces live in `Core/Abstractions` and
recording/in-memory implementations in Fakes. Host owns Kestrel, the dispatcher,
process supervision and portable filesystem/HTTP adapters. Windows contains the
platform APIs. The app marshals activations, dialogs and clipboard work to its STA
thread and binds the shared webview media through `DispatcherMessageSink`.

`CompanionInstances` normalizes registry records and creates an SSH transport,
forwarder, notifier, audio session, repatch job and config-sync area for each instance.
The supervisor serializes registry changes and waits for in-flight commands before
retargeting. Settings control forwards/host label, notifications, microphone device
and repatch delay. Host-label-only changes keep the local port. The desktop and IPC
share the settings store; theme and active-instance updates reach the desktop.

Config project watchers start with their areas. Automatic config sync is throttled
by its five-minute runtime policy. Host and update metadata refresh periodically without
duplicating the runtime probe. Public update results are shared and cached for ten minutes
(one minute on failure); usage is collected on demand per instance/period and cached for
five minutes (one minute on failure), matching the JavaScript TTL fixtures.
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

Each exporter owns one area. C# and Node consume the same committed bytes. The S3
`integration-settings` area runs the actual JS writer and compares default/named
instance files against writes through the real HTTP dispatcher. External launches
use argv arrays and existing parity-tested builders. Secrets are never logged or
placed in process arguments.

## Installation

See [the user guide](../docs/companion.md). `Install-ConstructCompanion.ps1` wraps the
per-user installer, including source selection, force and uninstall. Auto-Install's
non-elevated pre-step covers local and remote installs; Update-Construct and plain
Provision-AgentVM reprovision call the same opt-out-aware, non-blocking hook.
The VS Code fallback offer covers PCs that only registered a remote VM through the
extension. Installation always targets the client PC.

## Unsupported messages

The dispatcher implements all ten top-level panel message types. Unknown types/commands
return `{type:"lifecyclePrepared", id, error}`; both shared webviews display `error`.
These specific workflows remain explicit refusals:

| Command | Reason / alternative |
|---|---|
| `registerThisVm` | Companion has no attached Remote-SSH window identity. Use the VS Code registration command (which stays local in client mode). |
| `addProject` | S2a exposes profile storage but does not expose the clone/register/open project workflow. Save a profile or use Add Project in VS Code fallback mode. |
| `removeInstance` | S2a has registry edits and launch builders, but no complete removal planner/confirmation workflow. Use the local VS Code removal command. |
| `convertToHost` | Initiation depends on the attached VM identity; pending VS Code conversions keep their RSA private key in that VS Code profile's SecretStorage. Companion shows pending status and never finishes it automatically. Review/finish in that profile. |
| `createFirstVm` | The service-backed creation wizard is not exposed by S2a. Use New Remote VM in VS Code. |
| `updateConstruct` | The install-wide result-file/update/reload workflow is not yet wired. Run the installed `Update-Construct.ps1`. |
| `hostadmin.action: issueToken`, `rotateVmToken` | `IPrompts` has no one-time secret display operation. Refused before requesting any new token; use the host CLI. |
| `hostadmin.action: createFirstVm` | Same missing creation wizard as the panel command above. |
| `saveSettings` automatic checkpoint apply | The preference is saved, but the elevated apply/result workflow is not wired. A visible refusal directs the user to VS Code or the installer checkpoint action. |
| Lifecycle preflight / live project fallback | The dispatcher uses the persisted project selection. The extension's import-scan/config-sync/continue-anyway preflight and probe fallback are not wired; sync and select projects explicitly before launching lifecycle actions. |
| `saveProject` malformed legacy values | Uses S2a strict validation and canonicalization rather than the extension modal's legacy schema coercion. Invalid or reserved profiles are refused before writing. |

Other panel command IDs are routed in `Host/Dispatch/MessageDispatcher.cs`; host-admin
messages/actions are routed in `Host/Dispatch/HostAdministration.cs`. Bad names, form
values and routes return RFC 7807 problems. Host-admin refusals also publish a visible
`state.notice`; ordinary unsupported panel operations publish the visible error above.


## Message matrix

The known dispatcher set is checked against this table and the extension source.

| Kind | Message or command | Status | Details |
|---|---|---|---|
| message | `command` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `customRebuild` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `openPanel` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `ready` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `saveIdlePolicy` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `saveProject` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `saveSettings` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `setAudio` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `setInstance` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| message | `setUsagePeriod` | implemented | Dispatcher; saveSettings checkpoint apply and lifecycle preflight limitations are listed above. |
| command | `addConfigRemote` | implemented | Dispatcher and desktop seams. |
| command | `addProject` | unsupported | Clone/register wizard remains in VS Code. |
| command | `addRemoteAndPublish` | implemented | Dispatcher and desktop seams. |
| command | `childDelete` | implemented | Dispatcher and desktop seams. |
| command | `childShutdown` | implemented | Dispatcher and desktop seams. |
| command | `chooseMicDevice` | implemented | Dispatcher and desktop seams. |
| command | `chooseTheme` | implemented | Dispatcher and desktop seams. |
| command | `closeForward` | implemented | Dispatcher and desktop seams. |
| command | `connect` | implemented | Dispatcher and desktop seams. |
| command | `convertToHost` | unsupported | Review and explicitly finish in the originating VS Code profile. |
| command | `createFirstVm` | unsupported | Use the VS Code remote VM creation wizard. |
| command | `deleteProject` | implemented | Dispatcher and desktop seams. |
| command | `editProject` | implemented | Dispatcher and desktop seams. |
| command | `exportConfig` | implemented | Dispatcher and desktop seams. |
| command | `exportUsage` | implemented | Dispatcher and desktop seams. |
| command | `importRemoteConfigs` | implemented | Dispatcher and desktop seams. |
| command | `installGit` | implemented | Dispatcher and desktop seams. |
| command | `openAgentWeb` | implemented | Dispatcher and desktop seams. |
| command | `openConfigRepo` | implemented | Dispatcher and desktop seams. |
| command | `openForward` | implemented | Dispatcher and desktop seams. |
| command | `openHostAdmin` | implemented | Dispatcher and desktop seams. |
| command | `openProject` | implemented | Dispatcher and desktop seams. |
| command | `openProjectFolder` | implemented | Dispatcher and desktop seams. |
| command | `publishConfigProfiles` | implemented | Dispatcher and desktop seams. |
| command | `pushConfigUpstream` | implemented | Dispatcher and desktop seams. |
| command | `redownload` | implemented | Dispatcher and desktop seams. |
| command | `refresh` | implemented | Dispatcher and desktop seams. |
| command | `registerThisVm` | unsupported | Requires an attached VS Code Remote-SSH identity. |
| command | `reinstall` | implemented | Dispatcher and desktop seams. |
| command | `removeConfigRemote` | implemented | Dispatcher and desktop seams. |
| command | `removeInstance` | unsupported | Use the local VS Code removal command. |
| command | `reprovision` | implemented | Dispatcher and desktop seams. |
| command | `selectProfiles` | implemented | Dispatcher and desktop seams. |
| command | `shareConfigs` | implemented | Dispatcher and desktop seams. |
| command | `showLogs` | implemented | Dispatcher and desktop seams. |
| command | `shutdown` | implemented | Dispatcher and desktop seams. |
| command | `startConnect` | implemented | Dispatcher and desktop seams. |
| command | `syncConfigNow` | implemented | Dispatcher and desktop seams. |
| command | `updateAgent` | implemented | Dispatcher and desktop seams. |
| command | `updateAgents` | implemented | Dispatcher and desktop seams. |
| command | `updateConstruct` | unsupported | Run Update-Construct.ps1; update/reload result workflow remains in VS Code. |
| host message | `hostadmin.ready` | implemented | HostAdministration; one-time token display and creation wizard limitations are listed above. |
| host message | `hostadmin.refresh` | implemented | HostAdministration; one-time token display and creation wizard limitations are listed above. |
| host message | `hostadmin.tab` | implemented | HostAdministration; one-time token display and creation wizard limitations are listed above. |
| host message | `hostadmin.signIn` | implemented | HostAdministration; one-time token display and creation wizard limitations are listed above. |
| host message | `hostadmin.action` | implemented | HostAdministration; one-time token display and creation wizard limitations are listed above. |
| extension command | `construct.openPanelHere` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.openPanel` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.refresh` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.showLogs` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.chooseTheme` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.switchInstance` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.addRemoteHost` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.newRemoteVm` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.registerThisVm` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.removeInstance` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.removeRemoteHost` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.openHostAdmin` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
| extension command | `construct.installCompanion` | implemented | Registered by the extension; registry edits stay local, Companion views/runtime messages use IPC. |
