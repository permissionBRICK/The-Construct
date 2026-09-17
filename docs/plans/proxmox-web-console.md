# Proxmox: browser console without a Proxmox login

Status: plan, 2026-09-17. Branch `feat/proxmox-web-console` (off `main`). Implementation is
handed to a T3 Code thread; this document is the brief.

## 1. Goal

`construct vm console NAME --web` and the control panel's console button work on a Proxmox
host exactly as on Hyper-V: a browser viewer for the VM's display (installer screens included,
no guest agent needed), authenticated only by the Construct console session. Users never log
into Proxmox; they have no Proxmox account. The current Proxmox implementation hands out the
URL of Proxmox's own noVNC page on port 8006, which needs a Proxmox web login. That is a
limitation of that shortcut, not of Proxmox: the service runs as root on the node and can open
the VM's VNC stream itself with `qm vncproxy`, which is what the Proxmox UI does internally.

Decisions already taken: reuse the existing Construct browser console (the Guacamole gateway
provisioned into the primary, `console-viewer/`) rather than a second viewer; the gateway
already speaks VNC natively; the VNC endpoint lives on the node and is opened per session with
a one-time password; the Proxmox noVNC URL and the `IInteractiveConsoleLink` "native viewer
requiring its own login" path are removed for Proxmox once the gateway works.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Console session routes; `POST /vms/{name}/console/sessions/{sid}/connection` hands the viewer its credentials; `interactiveUrl` in the session answer | `service/src/Constructd.Api/Endpoints/ConsoleEndpoints.cs` |
| Contract: `IInteractiveConsole` (`ConnectAsync` → `ConsoleConnection { VmId, Username, Password, Domain, CertificateFingerprint }`, `RenewAsync`, `RemoveAsync`, `ReconcileAsync`), `IInteractiveConsoleLink` | `service/src/Constructd.Core/Abstractions/IInteractiveConsole.cs` |
| Hyper-V implementation (temporary VMConnect credentials, lifetime tied to the session, reconcile) | `service/src/Constructd.Windows/Console/HyperVInteractiveConsole.cs`, `ConsoleCredentialCleanup.cs`, `Constructd.Core/Services/InMemoryConsoleSessionStore.cs` |
| Proxmox implementation to replace | `service/src/Constructd.Proxmox/ProxmoxInteractiveConsole.cs` (noVNC URL on 8006, `ConnectAsync` throws), `ProxmoxConsoleTransport.cs` (screenshot/input over QMP, keep), `ProxmoxCommands.cs`, `ProxmoxQmp.cs` |
| The gateway in the primary: aiohttp server that opens a guacd connection with `select rdp`, `security=vmconnect`, port 2179, `preconnection-blob=vmId` | `console-viewer/server.py` (~line 179-260), `console-viewer/open.py`, `console-viewer/install.sh` (pinned guacd container), `console-viewer/static/`, tests `console-viewer/test_*.py`, `console-viewer/README.md` |
| CLI and panel | `bin/construct-vm.sh` (`cmd_console_web`, `--connection-stdin`), `extension/src/console.js`, `extension/src/hostadmin*.js` console button, `docs/child-vms.md` console section, `docs/proxmox-host.md` §6 |
| Relay infrastructure on the node (in-process TCP listeners, address and port ranges) | `service/src/Constructd.Proxmox/TcpRelayPortForwardManager.cs`, `ConstructdOptions.cs` (`ListenAddress`, `SshForwardPorts`, `AppForwardPorts`, `ProxmoxOptions`) |
| Capabilities | `Console.Interactive` in `BackendCapabilities` (`Core/Domain/Capabilities.cs`), aggregated in `Core/Services/CapabilityAggregator.cs` |

## 3. Design

### 3.1 VNC endpoint per session (node side)

`ProxmoxInteractiveConsole.ConnectAsync(session)`:

1. Generate a random one-time password (VNC auth caps the password at 8 significant bytes;
   generate 8 bytes of randomness, base64 is fine) and a session-bound listener on
   `Constructd:ListenAddress` from a new range `Proxmox:ConsolePorts` (default `5900-5999`,
   validated not to overlap the SSH and app ranges).
2. Start `qm vncproxy <vmid>` with the environment variable `LC_PVE_TICKET=<password>`
   (that is how `qm vncproxy` sets the one-time VNC password on the QEMU instance via QMP
   `set_password`) and bridge its stdin/stdout to the first accepted TCP connection on the
   listener. One connection per session; a second connect is refused; the listener closes
   when the session is removed, renewed past its lifetime, or the process exits. Never log
   the password; `ConsoleConnection.ToString()` stays redacted.
3. Return `ConsoleConnection` with additive fields: `Protocol = "vnc"`, `Host = <PublicHost or
   node address>`, `Port = <listener port>`, `Password = <ticket>`; `Username`/`Domain` empty;
   `CertificateFingerprint` empty (VNC here is unencrypted between the primary and the node on
   the same LAN bridge; document it, and keep the password single-use and short-lived).
   Hyper-V keeps `Protocol = "vmconnect"` (default), so existing callers see no change.
4. `RenewAsync` extends the listener's deadline; `RemoveAsync` kills the proxy process and
   closes the listener; `ReconcileAsync` reaps orphans (process gone, session gone).

Verify on the fixture that `qm vncproxy` honours `LC_PVE_TICKET` (it does in PVE 7/8: the
ticket is read from the environment and applied with `set_password`); if a PVE version does
not, fall back to QMP `set_password vnc <ticket>` + `expire_password vnc +60` through
`ProxmoxQmp` before starting the proxy.

### 3.2 The gateway speaks VNC (primary side)

`console-viewer/server.py`: when the connection answer carries `protocol: "vnc"`, send
`select vnc` to guacd with `hostname=<host>`, `port=<port>`, `password=<password>`, and no
`security`/`preconnection-blob`; phase text "Connecting to the VM display on the host
(VNC)". Everything else (session token, keepalive, input filtering, no secrets in logs) is
unchanged. `test_server.py` gains the VNC branch.

### 3.3 API, capabilities, clients

- `Console.Interactive` becomes `Supported` on Proxmox; `interactiveUrl` and the
  `IInteractiveConsoleLink` registration go away for Proxmox (keep the interface if Hyper-V
  or tests use it; otherwise delete it).
- `bin/construct-vm.sh` and `extension/src/console.js` need no protocol knowledge; confirm
  the `--web` flow and the panel button work end to end with the fake platform carrying a
  `vnc` connection, and fix any place that assumes `vmconnect` (`console.js` error mapping
  "vmconnect-unreachable" gets a VNC sibling "vnc-unreachable").
- Installer: the primary's gateway install already runs on Proxmox-hosted primaries? Check
  `bin/provision.sh` / `console-viewer/install.sh` gating (`Constructd:BrowserConsoleEnabled`,
  the `console` feature): the gateway must be installed on a Proxmox-hosted primary too, which
  it will be once the host advertises `console`.

### 3.4 Docs

`docs/child-vms.md` and `docs/proxmox-host.md` §6: remove "requires a separate Proxmox login";
describe the VNC path, the port range, the single-use password, and the unencrypted LAN hop.
`console-viewer/README.md` title and intro cover both platforms. `service/README.md`: the new
option row.

## 4. Work, in order

1. `ConsoleConnection` additive fields + `Proxmox:ConsolePorts` option + validation; tests.
2. `ProxmoxInteractiveConsole`: listener, `qm vncproxy` bridge, lifecycle, reconcile; tests at
   argv/env level on the process-runner fake plus a loopback bridge test (like
   `TcpRelayPortForwardManagerTests`); the password must never appear in argv or logs
   (secret-hygiene test pattern in `Api/SecretHygieneTests.cs`).
3. Gateway VNC branch + tests.
4. Capabilities, endpoint answer, client checks, installer gating; end-to-end test with the
   fake platform.
5. Docs.

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`,
the console-viewer Python tests and the extension tests green.

## 5. Acceptance

- On a Proxmox host, `construct vm console NAME --web` opens the gateway page, which shows
  the VM's display and accepts input, with no Proxmox credentials anywhere in the flow.
- A second connection to the session's port is refused; removing the session kills the
  proxy and frees the port; a crashed proxy is reaped by reconcile.
- Hyper-V behaviour and tests unchanged.
- Field test (a human, on the test node): open the console of a fresh child during its
  installer and of the primary.

## 6. Rules for the implementing agent

- Work on `feat/proxmox-web-console` in this worktree (`/root/repos/construct-console`), off
  `main`. Commit in small steps with the repo's configured author; never put any other email
  in commits or files.
- Do not touch `/root/repos/construct`. Do not run anything against a real host or the test
  node; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the
  deviation in the final report.
