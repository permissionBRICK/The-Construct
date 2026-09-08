# Host administration field fixes, 2026-09-07

Release `25fbb3ccfb5fd305ecc7a7f6708944705bdc072e` is installed on the
haus-pc host and main-pc VS Code client.

## Causes and behavior

- Windows PowerShell enumerated the parsed API response at the authentication
  delegate boundary. A single registered VM became an object, which the panel
  silently treated as an empty list; the single user caused `.map is not a
  function`. The delegate now requests raw response JSON, preserving arrays of
  every size without changing existing PowerShell callers' parsed responses.
- Windows inventory includes unlettered EFI/recovery partitions. Overview omits
  GUID-only volumes without growth reservations; drive and directory mount paths
  remain readable, and GUID volumes with growth reservations remain visible with
  a descriptive label. Server capacity accounting remains intact.
- CPU capacity represents allocated vCPUs, not sampled CPU utilization. Without a
  configured budget the panel now shows allocation text and no percentage bar.
- Successful activity heartbeats no longer create audit entries. Failed and
  denied heartbeats remain audited. Existing history is retained.

## Verification and rollout

All 24 extension suites passed. The PowerShell remote-client suite passed 97
checks (one platform-specific skip on Linux); a Windows PowerShell 5.1 probe also
verified raw empty/singleton/multiple arrays. Release CI run `34154740118` passed
all 1,236 server tests, including the real HTTPS/SQLite/CLI end-to-end story.

main-pc's ordinary Update-Construct installed the release and reloaded VS Code.
A probe using the actual installed modules, Windows identity and stored TLS pin
loaded Overview, VMs, Users and Operations. It showed `haus-vm`, admin
`HOME\permissionBRICK`, C:/D:/E: storage, and 8 allocated vCPUs without a budget.

The host's normal check/stage/apply API installed the immutable release. Update
`1f5db482b6f84f909937f7a11e1b11fc` succeeded in phase `commit` at
2026-09-07 19:17:42 UTC. Health was `ok`, schema 700, maintenance open; production
settings stayed byte-identical, VM uptime remained continuous, and T3 HTTPS on
port 2301 returned 200.

A fresh guest activity report completed at 19:18:02 without error output or a new
successful heartbeat audit entry. The last heartbeat audit was the expected 503
during updater maintenance at 19:17:11, confirming failures are still retained.

The previously recorded ReleaseInfo cache issue also affects the first real
cross-version update: the running process identifies the new commit but may
show fallback package version/source/time until a later normal restart. The
persisted successful update is authoritative. This remains tracked by the
existing Jarvis metadata-refresh todo; no extra service restart was performed.

## Recurrence on main-pc, 2026-09-08

Installed `5b8830f99511d74f9b497d839bd41b4c0610c7c3` through the ordinary
main-pc updater. Registry discovery and host admin authentication were still
correct (Kerberos, HOME\permissionBRICK, active haus-vm). The initial
`refreshState` never ran admin discovery, while the periodic `refreshAll` ran it
only after VM, update, usage, idle-policy and child-inventory reads. The sidebar
also had no administration entry.

Both refresh paths now launch admin discovery immediately, independently of
those reads, and send an instance-scoped message to the sidebar and full panel.
The sidebar has a Host Administration button below Open Control Panel. Authority
still comes from the live host classification. A delayed response after an
instance switch is discarded.

All 25 extension test suites passed, including execution of the actual startup
function while the VM probe is held pending, switching instances mid-probe, and
running the sidebar controller. A live probe of the installed startup function
on main-pc returned admin offers for both surfaces using the real host identity
and existing TLS pin. The updater reloaded VS Code.

The VM's `primary · legacy` badge describes two independent fields: VM kind is
primary; its credential predates child delegation. Migration retained that
credential rather than expanding its authority. Existing heartbeat and own-VM
forwarding continue; child management from within the guest needs a primary
credential. Credential rotation is separate from VM rebuilding and was not
performed during this repair. See remote-host.md section 9 for the upgrade path.

## Explicit credential upgrade, 2026-09-08

At Christoph's request, haus-vm's credential was upgraded from legacy to primary
at approximately 16:21 UTC. main-pc authenticated as the owner through the shared
`Request-ConstructVmTokenRotation` helper. Delivery was preflighted over SSH stdin;
the returned credential stayed in process memory on Windows and was atomically
installed at `/etc/construct/vm-token` with root ownership and mode 0600. No full
reprovision or VM/service restart was involved.

Guest verification: `construct vm identity --json` reports primary/primary,
`allowChildCreation: true`, and `maxRetainedChildren: 1`. `construct vm list --json`
succeeds with an empty child inventory. A fresh activity heartbeat completed
successfully at 16:21:25 UTC. No child was created during verification.
