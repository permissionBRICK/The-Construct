# Child VM lifetime and sharing in the admin panel

Added 2026-09-11. Christoph clarified that “idle timeout” means the existing
child VM lifetime, not activity-based idle detection.

In Host administration → VMs, child rows offer:

- **Change lifetime** for a running child. The VS Code input starts with its
  current requested lifetime and accepts `30m`, `24h`, `7d`, or `never`. Saving
  calls the existing lease-renew endpoint, restarting expiry from now. Stopped
  children must be started before their lifetime can be renewed.
- **Make public / Make private**, using the existing sharing endpoint. Public
  means shared with other users of this Construct host. The badge shows the
  current sharing scope, and the inventory refreshes after each change.

Controls follow the host's allowed actions and disable during active operations,
deletion or host maintenance. Host limits still apply, including restrictions on
`never` or sharing. Coded policy refusals no longer incorrectly demote an admin
panel to ordinary-user mode; authentication/role failures retain their handling.

This is an extension change using existing service APIs. It needs an extension
update on the Windows PC running VS Code; no new host API is required. Deployment
to the user's VS Code remains pending.

Validation: 314 model, 101 adapter and 220 remote-client checks passed. A Chromium
smoke test verified the actual buttons, sharing badges, posted actions and guards
for primary, running, stopped, busy, restricted and maintenance states.
