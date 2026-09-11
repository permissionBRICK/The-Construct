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
