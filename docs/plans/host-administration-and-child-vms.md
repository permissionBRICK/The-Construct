# Remote host administration and temporary child VMs

Status: agreed product design; implementation has not started.
Decision date: 2026-09-06. Source: design discussion with Christoph.

This document is the implementation handoff for remote host administration, delegated
child VMs, and host updates. It supersedes conflicting proposals in the conversation
and the corresponding future-facing parts of [the remote architecture plan](modular-remote-architecture.md).
The [implementation plan](host-administration-implementation.md) describes delivery phases,
validation, and unresolved backend details. Neither document claims these features exist today.

## Scope and existing foundation

The first target is a Windows Hyper-V host on someone's PC, administered remotely from
VS Code. Keep backend and API boundaries usable by a future company Proxmox deployment
and browser administration application; do not build either in this phase.

Existing foundations include constructd user/token authentication, ownership and VM quotas,
VM lifecycle jobs, progress streaming, Hyper-V adapters, port forwards, an ISO catalog,
and a host-local admin CLI. The extension currently has a remote API client, but no host
administration panel. The guest CLI currently lacks child VM management. The existing
VM token permits its own heartbeat/forwards, not VM creation. Native ISO acquisition and
patching for primary Construct installations already exist; arbitrary child media is a
separate use case and must not automatically enter the Construct patching path.

## Extension experience

### Host administration

Expose a native host administration module only for remote hosts where the current
identity is registered as an Admin. Do not display it for local installs or ordinary
remote users. Resolve identity per target host; an admin on one host is not an admin on
another. Hiding controls is presentation only: the API must enforce every operation.

Host registration/connection must work independently of an already-created VM. Ordinary
users can connect through the extension or PowerShell and create their initial primary
Construct if authorized, without an administrator preparing a VM for them.

The admin module provides:

| Area | Features |
|---|---|
| Overview | Installed host commit/version, health, capacity and reservations, maintenance/update state, active jobs |
| VMs | All users' VMs, primary/child type, parent, sharing state, power state, resources, storage, lifetime and current operation |
| Users | Register/remove users, role and allowances, optional VM overrides; existing token administration |
| Media | Primary Construct source/patched media status; child ISO inventory, references, transfers and cleanup status |
| Operations | Job progress, failures, cleanup retries and audit records |
| Configuration | Host capacity policy, user defaults, forwarding availability and supported backend features |
| Maintenance | One-click update from main, staged/running/result state, health and rollback outcome |

Construct guest inventory shows installed Construct commit, last successful provisioning,
and last successful reinstall as distinct facts. Keep current attempts/failures separately.
Record provenance for these reports: successful provisioner reports versus host-observed
creation or boot. Unknown is a valid value; arbitrary Windows/Linux child VMs generally
have no Construct version. Mounting or booting an ISO is not evidence of successful installation.

There are NO guest Construct update/provision/reinstall buttons in host administration.
Those actions stay in the existing user-PC workflow because they also configure that PC,
restore its credentials and connect its tools. Host version and guest versions are independent.

### Minimal ordinary-user view

Show the user's primary instances with their children and current state. Child actions
in this view are **Shut down** and **Delete**; no start/resume, console or elaborate
administration UI is required here. Agents perform those operations through the CLI.
Existing primary instance controls remain in the normal user-facing instance workflow.

The final correction is that the child stop button requests a **graceful guest shutdown**,
not save/suspend. Children are disposable test environments whose scripts commonly expect
a cold boot. A failed or unsupported guest shutdown must be reported; do not silently
substitute force-off or deletion. The API/CLI still support explicit save and start/resume.

## Ownership, permissions and defaults

An administrator primarily registers users and their allowance. The user self-provisions
primary Constructs, which automatically inherit the user's permitted delegation.
No separate per-VM admin step is required. Evaluate current user policy on each request;
credentials must not freeze the permissions that existed at provisioning time.

| Policy | Initial default / rule |
|---|---|
| Primary count | One per user per host; configurable higher |
| Child creation | Enabled |
| Retained child count | One per user per host, shared across all their primaries |
| CPU budget | No aggregate limit by default; optional host and per-user limits |
| RAM/storage budgets | Explicit host/user configuration; no guessed generous numeric default |
| Maximum child lifetime | Unlimited; caller may request `never` |
| Requested lifetime | Mandatory at child creation and start/resume; never silently defaulted |
| Child VM resources | CPU, RAM and disk size mandatory; no implicit OS-independent sizes |
| Sharing | Private initially; owner may mark host-wide shared |

User budgets aggregate all owned primaries and children on that host. Multiple primary
VMs do not multiply the allowance. Stopped/saved children still occupy retained-child
slots and storage. VM overrides are a provision for exceptions, not the normal enrollment
flow; they cannot bypass host capacity or the user's overall limits.

Every child has exactly one primary parent on the same host. The human owner and quota
payer are inherited from that parent. Children do not become parentless independent VMs.
Parent credentials may operate their own children, plus shared children when authorized,
but cannot administer the host, users, unrelated private VMs or arbitrary primary VMs.
Children receive no delegated VM-management credentials and cannot request forwarding.
Do not bake a primary token, user token or personal credentials into child media.

### Sharing

Support only `private` and `host` sharing scopes in the initial UI/API. Represent scope
explicitly in storage so a selected-user scope can be added later; do not implement
selected-user lists or an enterprise permission editor now.

Host-wide sharing grants all registered users on that host, and their authorized primary
Constructs, access and operational management: inspect, start/resume, restart, graceful
shutdown, save, console and permitted connection/forward requests. Guest OS credentials
are arranged separately by users/agents; sharing does not reveal them.

Deletion, ownership and sharing changes remain owner/admin operations. Hardware/media
changes are not settled by the phrase operational management: keep them owner/admin-only
in the initial contract until a broader permission is explicitly designed. Every shared
operation remains charged to the original owner and bounded by that owner's policy.

Deleting a parent deletes ALL of its children, INCLUDING shared ones. The confirmation
lists affected children and highlights shared environments and permanent disk removal.
Sharing never detaches a child or exempts it from the cascade.

## Child creation and control

Children are general-purpose ISO-booted test VMs: Windows, Linux or another supported
OS. There are no automatic child Construct templates, project-profile installs or parent
clones in this scope. The primary supplies installation media and handles guest setup.

Creation accepts a public HTTP(S) ISO URL fetched by the host, or an uploaded local ISO.
URL acquisition is preferred when public media exists. Allow an optional expected checksum
and an auxiliary ISO for unattended-install files. Auxiliary uploads can contain secrets:
apply owner/reference access controls and do not expose contents or credentials in logs.
Create powered on by default; support powered-off creation for setup before first boot.

Explicit hardware inputs include CPU count, fixed RAM and maximum disk size. Expose
capability-checked settings for firmware/generation, Secure Boot and its template,
virtual TPM, boot order and supported optical attachments. Optional Windows/Linux presets
may help select firmware options but cannot fill in missing CPU/RAM/disk/lifetime values.
Reject unsupported combinations before allocating a VM. Report image transfer/VM boot as
such; do not promise generic OS installation completion without guest-specific evidence.

The primary CLI must support create/list/inspect, start/resume, restart, graceful shutdown,
save, delete, sharing, media upload/attachment and console access. It should expose
machine-readable results, operation IDs and progress, with deterministic exit codes and
idempotent retries. Return connection details when known; tolerate a guest with no OS/IP yet.

Console capabilities are separate: screenshots and interactive keyboard/mouse/console
transport. Investigate Hyper-V support before promising VNC. Future Proxmox can implement
its own transport behind the same capability contract. Console access must work during
boot/install when SSH and guest agents are absent, where the backend permits it.

### Lifetime and removal

A finite lifetime is a wall-clock running lease, not an idle detector. Store the explicit
request and expiry; `never` is explicit unlimited duration when admin policy permits it.
Start/resume requires a new allowed lifetime. Sharing does not reset expiry. Define reboot
and service-restart handling so neither silently renews the lease.

The earlier explicit expiry decision was **save/suspend without deletion**; disks and
saved-state files remain charged. The final correction explicitly changed the user-panel
stop action to graceful shutdown. Before implementing expiry, confirm whether the same
cold-boot rationale should ALSO change expiry to graceful shutdown. Do not silently
interpret either decision as authorizing deletion at expiry.

Explicit deletion removes disks, saved state, dedicated auxiliary media and all other
exclusively owned artifacts. Shared media uses references: remove the reference, then
collect unreferenced eligible artifacts. The same ownership/reference principle covers
Construct-generated ISOs. Do not delete an image still attached to another VM or an
administrator-supplied external source file. Failed cleanup remains a visible, retryable
operation with retained accounting, not an untracked disk leak.

## Capacity and admission

User quotas and host capacity are independent checks. Include all relevant hypervisor
VMs, including ones started outside Construct; reconcile actual runtime state. Unmanaged
VM consumption reduces host capacity without inventing a Construct user owner.

| Resource/state | Accounting |
|---|---|
| CPU | Validate backend per-VM maximum; unlimited aggregate by default; optional active-vCPU budgets |
| RAM | Fixed allocation initially; reserve full RAM for starting/running/paused VMs; no RAM overcommit |
| Storage | Dynamic disk files allowed, but reserve full maximum disk capacity; no storage overcommit |
| Saved/stopped | Release CPU/RAM only after hypervisor confirms transition; retain disks/saved-state storage |
| Start/resume | Atomically check and reserve current host/user capacity; fail clearly if unavailable |

A stopped VM releases runtime capacity for other users; owning a stopped VM does not
promise it can always start. Starting/stopping/pending transitions must not double-spend
capacity. A graceful-shutdown request does not release RAM until the VM actually stops.
Reserve headroom for the host OS and account for non-Construct storage, ISO transfers and
saved-memory files. Check physical free space as well as committed growth reservations,
without counting the same bytes twice. Admission must serialize competing reservations
and recover them after failure/restart by reconciliation.

Keep backend capabilities for future ballooning/dynamic RAM with minimum/maximum memory
and explicit admin overcommit policy. Hyper-V fixed RAM is the initial policy because
Ubuntu dynamic-memory boot failures were observed by the owner. Memory deduplication and
ballooning are different features; hoped-for savings are not guaranteed admission capacity.
Proxmox implementation and memory overcommit remain future work.

## Network policy and backend seams

Only primaries/users request child connectivity. A child cannot forward itself. Supported
access modes are client forwarding to the user's PC, host forwarding when admin policy
allows it, and direct guest IP/hostname access. Host forwarding must be independently
disableable; a company Proxmox deployment may need only client/direct access.

Design a distinct target-VM identity for a forwarding request: the requester primary and
the child serving the port are different. Authorize the relationship or sharing grant,
then validate host policy. Avoid reusing the existing self-forward token check unchanged.

Define extension points for guest addressing, access exposure and firewall policy. Future
rules grant bidirectional access between a primary and its children, plus authorized
shared consumers. Apply/reconcile/revoke policy at creation, sharing changes and deletion.
Do not claim isolation on Hyper-V's existing network until a real enforcing adapter exists.
No enterprise firewall or Proxmox implementation is required for this first delivery.

## Host updates

The admin panel reports the installed host version and offers one-click update from
**main**. Pin the resolved commit for each update; do not repeatedly resolve a moving branch
between scripts and binaries. Retain the previous install for health-check rollback.

Stage a verified self-contained Windows host release and matching Construct scripts.
The host needs neither a build SDK nor an installed .NET runtime. The separately built
ISO tool retains its independent release/pin workflow. Host release packaging, a trusted
update manifest and update execution are new implementation work.

Block new conflicting host-managed jobs while draining active ones: ISO acquisition/build,
VM creation/deletion and host-managed install/reachability waits. Do NOT wait for ordinary
guest activity or PC-to-primary provisioning. No provisioning heartbeat/lease is needed
to gate updates. Once drained, replace the scripts/service, restart and health-check.
Hyper-V VMs remain running. Clients show maintenance/reconnection and recover after the
API returns. No automatic retry of non-idempotent mutations without an operation key.

An updater outside the service process must carry the operation through stopping/replacing
that process. Preserve settings, data, tokens, certificates, media, VM registrations and
permissions. Do not blindly rerun the current installer with defaults: it rewrites settings.
Check config/database migration compatibility before offering automatic binary rollback.
Failed updates must report phase and recovery outcome after reconnection, or leave an
accessible local recovery record if the service cannot restart.

## Proposed API and CLI contracts

These route/command names are a concrete starting proposal, not existing endpoints or a
frozen wire schema. Reuse existing authenticated clients and job/progress semantics.

| Surface | Proposed additions |
|---|---|
| Discovery | Extend identity/capability responses with effective permissions and backend capabilities; scoped primary identity endpoint |
| Admin host | `GET /host/status`, configuration read/update with validation, capacity and installed version |
| Admin users | List/read/update users and allowance; reuse existing create/delete/token operations |
| Inventory | Extend VM records with kind, parent, owner, sharing, lease, resources, observed guest version/timestamps |
| Delegation | `POST /vms/{parent}/children`, scoped list/detail/lifecycle operations |
| Media | Begin/complete upload, acquire by URL as a job, status/reference/delete operations |
| Sharing | Owner/admin update of child sharing scope; list visible host-shared children |
| Connectivity | Request access to an authorized child, including target and client/host mode |
| Console | Capabilities, screenshot, short-lived interactive session; authorize every session |
| Updates | Check main, stage/apply update, get persistent update status; Admin only |

All routes retain the `/api/v1` prefix unless a real incompatible contract requires a new
version. Use structured errors with requested/allowed/available capacity and a reason;
never silently clamp a request. Persist actor, effective owner, parent/target and operation
for audit. Revoke delegation on primary deletion or user disable/removal.

Example intended CLI (syntax to finalize during implementation):

```sh
construct vm create --iso-url https://example.org/os.iso --cpus 4 --ram-gb 8 --disk-gb 80 --lifetime 4h
construct vm create --iso ./custom.iso --aux-iso ./answer-files.iso --cpus 4 --ram-gb 8 --disk-gb 80 --lifetime never --no-start
construct vm list --json
construct vm inspect CHILD --json
construct vm start CHILD --lifetime 2h
construct vm shutdown CHILD
construct vm save CHILD
construct vm restart CHILD
construct vm share CHILD --scope host
construct vm console CHILD --screenshot ./boot.png
construct vm delete CHILD
```

Deletion from interactive clients needs an explicit confirmation. Noninteractive CLI
needs an explicit confirmation flag with documented destructive scope; parent deletion
must acknowledge the reported cascade. Guest credentials remain outside this API.
