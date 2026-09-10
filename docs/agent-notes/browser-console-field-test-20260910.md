# Hyper-V browser console field test — 2026-09-10

The browser viewer uses Guacamole 1.6.0 in the existing Linux primary and connects
through native VMConnect on STANDPC. `alpine-viewer` is a direct Hyper-V generation
2 child with 1 vCPU, 512 MiB fixed RAM, 2 GiB dynamic disk, Secure Boot and TPM off.
It uses the already verified Alpine virtual 3.24.1 ISO in the host media cache.
The explicit two-hour runtime lease started at 14:03:40 UTC (expires 16:03:40 UTC).

## Host constraints encountered and resolved

Windows initially reported only 123 MiB of free commit out of about 51.8 GiB,
even though nearly 6 GiB of physical RAM was free. Native `Get-VM` failed and
PowerShell sometimes could not initialize its CLR. With permission to pause
background applications, Steam was shut down and Discord closed. This recovered
about 1.2 GiB of commit; the primary VM was never paused or restarted.

C: also had only about 220 MiB free. Old manual Construct rollout backups contained
three copies each of the Ubuntu source and Construct autoinstall ISO. SHA-256
comparisons proved the two older copies identical to the retained backup copies.
Removing only those four duplicate ISO files freed 13,212,131,328 bytes; C: then
had about 14 GB free and child start succeeded. All backup databases, code, the
newest retained ISO copies, and the current updater rollback backup remained.
This was cleanup of old manual rollout artifacts, not deletion of managed media.

## Native and browser validation

- A temporary local account with no group memberships was granted VMConnect access
  to the exact child GUID; Hyper-V accepted it for the boot console.
- The native script reads the VMConnect certificate over loopback, without sending
  credentials. Its SHA-256 fingerprint matched the host certificate store and was
  accepted by guacd. The gateway gets this fingerprint through the pinned host API.
- Browser rendering, root login, typed shell commands, and disconnect worked in
  Chromium against the real Alpine guest; no JavaScript errors occurred.
- The screenshot confirmed Alpine 3.24.1 and kernel 6.18.35-0-virt.
- Revoking the VMConnect grant and removing the temporary account passed through
  the same native removal script used by the implementation.
- Local validation passed 1,246 .NET service tests and ten gateway/CLI tests, plus
  shell/JavaScript syntax and shellcheck for the installer.

The first probe used the real native account/guacd/browser path with the existing
host session API. Full deployment validation is recorded below after installation.

## Reproduction

See [installation and session boundaries](../../console-viewer/README.md).
The new CLI is `construct vm console NAME --web --minutes 30`.
The named `construct-browser-console` project profile records reproducible setup.

No host administrator password is needed by the viewer. Temporary VMConnect
credentials are deliberately restricted to trusted primary-VM gateways, never
sent to the browser, and cleaned up when sessions end. Mouse behavior in a full
graphical installer remains a user acceptance test; this field test used a Linux
text console.

## Deployment and final acceptance checks

Host release `82f054cc0bf02de216538d88449476299e2af2aa` was installed through the
normal updater. Update `c6449a7bf11643bfad168ac9e3f99585` succeeded at
14:20:18 UTC with no recovery record. SQLite integrity passed at schema 700.
Production settings (including the explicitly enabled browser console flag) were
unchanged across the update. The primary VM's identity and uptime remained
continuous. The child also kept running.

The final browser test used the deployed host `/connection` endpoint, the
installed gateway, and the packaged Guacamole client. Typing shell commands,
renewing the live session across two renewal intervals, disconnecting, and
reconnecting all passed with no JavaScript errors. After disconnect, native host
inspection showed zero temporary console accounts and zero named console grants.
The VM capability endpoint reports interactive console as conditional when enabled.

[Deployed viewer after successful renewal](assets/browser-console-hyperv-20260910.png)

The root-only CLI now reuses the gateway's existing open client forward instead
of allocating a new one for each link; this final CLI-only adjustment passed
three additional regression tests and was installed without restarting the host.
The gateway is served through the Construct client's localhost forward. Browser
automation used the gateway's local listener; `construct expose` independently
verified the forward on the user's client. Link secrets are intentionally absent
from these notes.

The child lease was renewed for two hours at handoff; the exact lease response
reported expiry `2026-09-10T16:23:54.759173+00:00`.


## Follow-up: admin panel actions after lease expiry

At 17:16 UTC, the restarted `alpine-viewer` still exposed its completed lease
shutdown job as `currentOperation` (`phase: done`). Both VS Code panels interpret
any current operation as busy, disabling shutdown and delete despite the actions
being authorized. Host fix `cf5c77f9226492916b834850309145a9f68335a2` projects only
Queued/Running jobs while retaining the persisted pointer and job history. Five
regression cases cover all job states through inspect, inventory, and child-list
routes; 1,251 host tests and 384 admin-panel checks passed, as did release CI.
Update `4025e8ca07c148d6b8c21533d8f34408` succeeded at 17:24:11 UTC. All three live
admin routes then returned `currentOperation: null`, a running child, and allowed
shutdown/delete actions. Production settings and primary uptime were preserved;
database integrity passed at schema 700. No extension update was needed. The
known attached-ISO ACL hardening issue remains separate; this guest's exact VM SID
read grant was restored after the update.


At 17:31:49 UTC the user's VS Code panel deletion completed successfully
(job `cba9f94ef5ba4e2b82827370c6257add`, result `removed`, no retained artifacts).
Read-only verification found no Construct record, native Hyper-V VM, VHDX,
ownership marker, matching configuration files, or capacity reservations.
The dynamic 2 GiB VHDX had last measured 4 MiB of actual file storage; there was
no immediate before/after free-space sample to quantify the total reclaimed.
C: had 3,730,403,328 bytes free afterward. The reusable 69,206,016-byte Alpine
ISO remained cached with zero references. The primary VM remained running.
