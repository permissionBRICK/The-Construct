# Host administration implementation plan

Status: implemented on Linux, not field-validated on Hyper-V.
Date: 2026-09-07.
Canonical requirements: [Remote host administration and temporary child VMs](host-administration-and-child-vms.md).

This is the delivery plan and current validation record. The implementation followed the
reviewable phases below without rewriting the existing primary provisioning flow. Acceptance
items proven only with Linux fakes and recording runners remain field-test requirements, not
claims about production Hyper-V behavior.

## Current code map

| Concern | Existing location |
|---|---|
| Extension remote client | `extension/src/remotehost.js` |
| Extension VM backend | `extension/src/drivers/hyperv-remote.js` |
| PowerShell remote API/auth | `lib/AgentVm.Remote.ps1` |
| PowerShell driver | `drivers/hyperv-remote/HyperVRemote.Driver.ps1` |
| Guest CLI | `bin/construct` and its helper scripts |
| Identity/admin API | `service/src/Constructd.Api/Endpoints/IdentityEndpoints.cs`, `AdminEndpoints.cs` |
| VM/jobs API | `service/src/Constructd.Api/Endpoints/VmEndpoints.cs`, `JobEndpoints.cs` |
| Authorization | `service/src/Constructd.Api/Auth/` |
| Lifecycle workflow | `service/src/Constructd.Api/Jobs/VmJobs.cs` |
| Domain/contracts | `service/src/Constructd.Core/Domain/`, `service/src/Constructd.Core/Abstractions/` |
| Persistence | `service/src/Constructd.Sqlite/` |
| Native primary ISO path | `service/src/Constructd.Windows/Iso/` |
| Host installation | `service/host/Install-ConstructHost.ps1` |
| Client update | `Update-Construct.ps1` (not a remote host updater) |
| Primary provisioning reports | `Provision-AgentVM.ps1`, guest provisioning scripts |

Verify these against the target commit when starting; other work may land before implementation.

## Phase 0: resolve contracts and investigate backend capabilities

Status: **implemented on Linux, not field-validated**. The schema, permission and capability
contracts are frozen; console feasibility has recording-runner coverage and historical host
probes, but LocalSystem screenshot/input and the final host capability set still need the
field checklist.

Produce a small API/domain design amendment before writing production code:

- Migration of existing VMs to primary kind, with preserved owners, keys and identity.
- User allowances, VM delegation overrides, child parent relationship, sharing enum and
  runtime reservations. Legacy VM tokens must not suddenly acquire child privileges.
  Specify the provisioning/credential-upgrade path for existing primaries.
- Define eligible shared callers: human users and authenticated primary Constructs on the
  same host; never unauthenticated clients or child guests.
- Capability model for firmware, Secure Boot, TPM, auxiliary media, console, networking
  and future dynamic memory. No backend-specific behavior hidden in the extension.
- Determine Hyper-V remote console/screenshot feasibility, including access from a Linux
  primary before an OS is installed. Prove a small path or report the unavailable
  capability. Do not promise VNC or require SSH in the guest for a boot console.
- Specify RAM headroom and storage reservation math, user budget defaults, reservation of
  saved-memory files, and physical-vs-reserved-byte accounting without double counting.
- Confirm whether expiry also changes from save to graceful shutdown following the final
  UI-stop correction. Specify graceful-shutdown timeout and unsupported-guest handling;
  no silent destructive fallback. Lifetime expiry never implies deletion.
- Define powered-off creation lease activation, finite lease renewal on restart versus
  start/resume, host downtime across expiry, and whether explicit keepalive is needed.
  Do not let service restart reset expiry.

Acceptance: agreed schema/permission matrix, documented remaining limitations and a concrete
Hyper-V console feasibility result. No unresolved product choice should be silently decided
by a generated implementation workflow.

## Phase 1: host identity, inventory and administration API

Status: **implemented on Linux, not field-validated**. Migration, inventory, allowances,
reports and audit are covered by API/persistence tests; migration of the existing `haus-vm`
and production identity/token continuity have not been exercised against its live database.

Add host status/capabilities, user listing/editing/allowances, VM classification and audit
coverage. Preserve existing enrollment, Negotiate/token handling, certificate pinning,
ownership checks and legacy API behavior. Expose effective allowed actions for clients,
while evaluating policy again on every mutation.

Record primary Construct version and successful provision/reinstall timestamps from the
existing user-PC workflow. Separate reports from host observations and operation attempts.
Report unknown values for older primaries and non-Construct children. These reports do not
create provisioning leases or block updates.

Acceptance:

- Non-admins cannot read admin-only inventory/config or mutate user policy.
- Role changes/revocation take effect without trusting a stale UI or token claim.
- Existing primaries retain identity and remain usable after migration.
- Guest version, provision time and reinstall time do not overwrite each other.
- Arbitrary child guests are not falsely marked provisioned by a successful boot.

## Phase 2: media, capacity and general-purpose child jobs

Status: **implemented on Linux, not field-validated**. Capacity, media, SSRF controls and
child jobs use deterministic fakes and pinned Hyper-V argv tests. Generation 1, dynamic
memory, disk growth and arbitrary device pass-through remain unavailable; no child has yet
been created from public media on the target host.

Implement a media registry separate from the primary Construct patched-ISO catalog, with
shared reference/cleanup primitives where useful. Accept public URL downloads and local
uploads, checksums, auxiliary ISOs and bounded storage reservations. Specify upload limits,
partial-transfer cleanup, resumability/idempotency and handling of unknown Content-Length.
Restrict URL fetches to intended public sources: validate redirects and resolved targets;
do not turn a VM request into an unrestricted host-network fetch capability.

Implement required CPU/RAM/disk/lifetime inputs, supported hardware settings, powered-off
creation and default boot. The job creates hardware and attaches media; it does not assume
Ubuntu, inject Construct credentials or wait indefinitely for an arbitrary OS to answer SSH.
Return useful boot/state/network information without inventing installation success.

Make admission/reservation an atomic persistent operation covering user allowance and host
capacity. Reconcile hypervisor runtime states, including non-Construct VMs and external
power changes. CPU aggregate limits are absent by default; RAM is fixed and not overcommitted.
Reserve each disk's full maximum and all retained artifacts. Handle failed start/resume,
paused/saving states and service restart without leaking or prematurely releasing capacity.

Acceptance:

- Concurrent creates/starts cannot both consume the same final RAM/storage allowance.
- Stopped/saved VMs release runtime resources only once the transition succeeds.
- Shared operations are charged to the owner, not used to evade quota.
- Start/resume can fail cleanly when another user has taken the available capacity.
- Child creation never invokes primary Construct ISO patching unless separately requested
  by a future explicit feature.
- Demonstrate a Linux ISO and a Windows-compatible hardware configuration on Hyper-V;
  do not equate unit tests or nested QEMU tests with actual Hyper-V validation.
- A failed upload/download/create releases or retains accounting consistently with the
  artifacts that really remain.

## Phase 3: delegation, CLI, sharing and lifecycle

Status: **implemented on Linux, not field-validated**. Primary-token upgrade, guest CLI,
sharing, leases, expiry and cascade recovery are automated; no live primary has yet exercised
the upgraded credential, sharing, expiry or a cascade on Hyper-V.

Issue scoped primary credentials and expose effective delegation limits to the CLI. Reuse
job streaming and machine-readable error conventions. Add creation/media upload, inspection,
lifecycle, sharing and supported console operations. Require explicit lifetime on creation
and start/resume. Children get no self-forwarding or VM-management permission.

Implement host-wide sharing only. Every child retains a parent. Support owner/admin-only
sharing changes and deletion; shared callers can perform the agreed operational actions.
Revocation affects new operations and connection/console sessions according to a documented
session policy. Keep guest OS authentication outside Construct.

Persist a cascade deletion job: enumerate/confirm affected children, remove dependent
artifacts, release credentials/network rules and record partial failures for retry. Keep
failed-cleanup storage charged. Protect against children created between confirmation and
deletion: close the parent to new creation and validate the confirmed deletion scope.

Acceptance:

- Primaries inherit user allowance without admin per-VM setup; multiple primaries share it.
- Children cannot create children, forward themselves, or access management with a copied
  heartbeat credential.
- Shared callers cannot delete or reassign someone else's VM.
- Deleting a primary removes private AND shared children, with truthful confirmation.
- Graceful shutdown is distinguishable from explicit save and force-off.
- Expiry never deletes; lifetime and cold-boot behavior match Phase 0's resolved decision.
- Repeated deletes and interrupted cleanup are retryable without losing ownership records.

## Phase 4: extension administration and minimal user view

Status: **implemented on Linux, not field-validated**. The webview and minimal user view have
plain-node coverage. The service implements the Admin-only primary ISO catalog projection;
the Media tab loads child inventory even when the catalog read fails.

Build native VS Code views backed by the API, not host filesystem access or commands that
quietly assume the service is local. Connect/register a host before any primary exists.
The admin module is absent for local installs and remote non-admin identities. Preserve
normal per-instance provisioning/reinstall controls outside this module.

Add overview, user allowances, inventory, jobs, media, configuration and update entry.
For ordinary users show primary/child hierarchy and child **Shut down** and **Delete**.
Keep child start/resume and console in the CLI for the initial minimal user experience.
Expose the plan's sharing operation via CLI; no extra ordinary-user panel action is required.

Acceptance:

- Admin visibility follows the current target host identity, including switching hosts.
- Service denial, old-service capabilities and unavailable hosts have useful states.
- No guest update/provision/reinstall action appears in host administration.
- Cascade confirmations list children and shared environments; a stop button requests
  graceful shutdown, not save or force-off.
- Inventory reports unknown guest metadata honestly.

## Phase 5: network and console adapters

Status: **implemented on Linux, not field-validated**. Screenshot and input paths, network
policy and external-switch creation are pinned by recording-runner tests. There is no streamed
video or network isolation; child address discovery is unverified, child host forwards are
refused, and mouse input may honestly return `applied: false`.

Define and wire access-policy interfaces even where the enforcing implementation is deferred:

- Hypervisor capabilities and guest addresses.
- Authorized primary-to-child/client access requests with a distinct target identity.
- Client forwarding, optional host forwarding and direct access.
- Parent/child and shared-consumer firewall-policy reconciliation.
- Console screenshot and interactive session transport, with short-lived authorization.

Implement only the agreed feasible Hyper-V capabilities. Mark unsupported modes explicitly.
Document future Proxmox and firewall adapter inputs/events; do not add a nominal no-op
adapter that reports isolation as enforced. Creating/deleting a VM and changing sharing
must have explicit integration points for future rule updates.

Acceptance: a child cannot self-forward; disabling host forwarding cannot be bypassed by
requesting access through its parent; authorized parent/shared access identifies the right
target; console works at the demonstrated backend capability level before guest provisioning.

## Phase 6: host release and update mechanism

Status: **implemented on Linux, not field-validated**. Signed staging, drain, helper handoff,
rollback and health-result handling have fake-mode coverage. The PowerShell suite replaces
`Test-UpdateHealth` completely: it does not exercise its HTTP, certificate pinning or
Windows CLI transport. The repository deliberately
ships no production signing key, the first rollout must be manual, and Windows service/task,
active-VM drain and rollback behavior remain field-test items.

Build self-contained Windows service packages with matching scripts and a manifest tied to
one main commit. This is a host release workflow; do not rebuild the independent ISO tool
on every Construct change. Define trusted provenance, content hashes, compatibility metadata
and retention. Avoid depending on authenticated/expiring Actions artifact URLs for ordinary
host downloads; choose a durable published distribution path.

Add the admin update API and persistent update status. Stage before stopping the service.
Enter a drain gate for new conflicting host jobs and wait for existing host work only.
Do not wait for PC-driven provisioning or guest activity. An independent updater must
survive service shutdown and carry out replacement, restart and health verification.

Preserve configuration and service state. Retain a compatible previous version and
restore it on failure. Design database/config migration rollback before claiming recovery:
restoring only an executable against an incompatible migrated database is not rollback.
Keep a local recovery record for complete service startup failure.

Acceptance:

- Update targets exactly the resolved main commit; scripts/binaries are consistent.
- Active guest VMs keep running across update/rollback.
- ISO/VM jobs drain; PC-to-guest provisioning does not block the update.
- New conflicting jobs receive an explicit maintenance response.
- Settings, certificates, users, tokens, registrations and media survive.
- Clients reconnect, recover operation status and do not duplicate prior mutations.
- Failed health check and interrupted update have tested recovery paths.
- Verify the update from a separate Windows client against a real Hyper-V host, with
  an active VM and a second disposable child, before declaring host rollout ready.

## Delivery and validation discipline

Each phase should include focused contract tests and necessary integration checks, updated
docs and a reviewable change. Reuse fakes for deterministic policy/state tests; validate
backend claims on the real backend. Do not require Proxmox/browser/enterprise features to
ship the local-first Hyper-V scope. Do not restart the service hosting an active development
session as an incidental test; schedule the explicitly scoped deployment test separately.

At implementation completion, report what was exercised on Hyper-V versus simulated, the
release commit, migration/rollback limits, and any unavailable console/network capability.
This record is not authorization to create VMs, deploy a service or run an update. Those
destructive host steps require the project owner's explicit field-test window; follow
[`docs/field-test-host-admin.md`](../field-test-host-admin.md).
