# Guest console in the control panel (2026-09-11)

The Child VMs card includes **Connect VNC** for running/paused guests with the
service's `console` action. Each click calls `construct vm console <name> --web`
over SSH to the captured primary instance. The existing gateway creates a fresh
ticket, exposes its viewer through the client forwarder, and checks console
authorization when the browser connects. No guest VNC server is required.

The extension validates names against its displayed inventory before invoking
the command, validates the returned HTTP(S) URL, and opens it through VS Code's
external-browser API. It does not log or save the credential-bearing fragment.
The primary VM and its client forwarder must be connected. Failed link creation
is surfaced in the UI without opening a guessed URL.

Guest inventory now refreshes independently of SSH status, update discovery,
usage accounting and idle-policy reads. Narrow messages preserve other cards
and discard results after an instance switch. Guest metadata and actions wrap
as separate groups; expiry text retains usable width. Idle policy lives in
Settings, with the same immediate Apply action as before.

Delivery requires **Update Construct** on the user PC. No host service change or
guest reinstall is needed when the existing browser-console gateway is installed.
Local panel checks cover command authorization, fresh links, SSH targeting,
independent discovery and actual browser layout/interactions. See
[local checks](../local-checks.md).

## Primary Console button (2026-09-12)

The status strip now has Console for the selected primary, in VS Code and
Companion. Both clients ensure the gateway over SSH, mint a fresh `self` ticket,
check the client TCP listener, and resolve the exact forward ID before retrying a
dead forward. Child consoles share the link validator and liveness handling.
Local Hyper-V uses the shared DPAPI credential broker and a one-time elevated
setup, integrated into Auto-Install. No host-service changes are required.

Implementation adjustments to the design: the firewall rule is instance-specific
and restricted to the VM switch interface; a mutex serializes credential rotation
across clients; raw broker/SSH diagnostics are replaced by fixed failure messages
because they can contain secrets. The forward close resolves its ID rather than
passing a client port as a VM port. Companion uses a TCP-connect probe because its
existing port probe checks bind availability. Broker polling reads the shared store
through PowerShell, so Companion does not read or write the token a second time.
Windows acceptance remains the checklist in `docs/control-panel.md`.

Account descriptions use `ConstructL:<vm-guid>` because
[New-LocalUser limits descriptions to 48 characters](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.localaccounts/new-localuser#-description). The deterministic `cvl` account name identifies the instance;
setup and removal reject accounts without the exact ownership marker shape.

The proposed optional forward-age environment setting is omitted: `construct
expose --list` does not expose acknowledgement ages for either backend. A TCP
listener probe is the freshness decision, with an exact-ID close and one remint.
