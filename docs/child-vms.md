# Child VMs (`construct vm`)

`construct vm` lets a service-managed primary Construct create and operate temporary,
general-purpose child VMs on its host. A child boots ordinary ISO media; it is not a
clone of the primary, receives no Construct management token, and can be Windows, Linux
or another OS supported by the host backend.

This command is for the guest shell. Host administrators use the host administration
API/UI, while the existing primary provisioning flow remains unchanged.

> **Validation status:** a [live Alpine smoke test](agent-notes/child-vm-alpine-field-test-20260910.md)
> on STANDPC verified delegated creation, ISO boot, screenshots, raw keyboard input and
> graceful shutdown through the production Hyper-V service. Broader Windows, sharing,
> networking and recovery coverage still requires the
> [host-administration field test](field-test-host-admin.md).

## Before the first command

The CLI reads the same service identity as [`construct expose`](expose.md):

- `CONSTRUCT_SERVICE_URL` and `CONSTRUCT_INSTANCE_NAME` from
  `/etc/construct/config.env`;
- the pinned public service CA from `CONSTRUCT_SERVICE_CA_FILE`;
- the VM-scoped token from `CONSTRUCT_VM_TOKEN_FILE` (default
  `/etc/construct/vm-token`).

The token is passed to `curl` through a private `0600` header file, never in an
argument. `jq` is required because child inventory, jobs and capabilities are nested.

Check what this primary may do:

```bash
construct vm identity
construct vm identity --json
```

An older primary may still have a `legacy` token, which deliberately retains only the
old heartbeat/forward scope. The CLI exits 9 and asks the owner to reprovision with
`-RotateVmToken`, or to choose **Reprovision (upgrade VM credential)** in VS Code. Policy
is evaluated by the service again for every operation; a credential does not freeze an
old allowance. The CLI's hint also names a planned VS Code credential-upgrade menu item
that is not wired yet. Use `Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken`
from the owner PC; ordinary reprovisioning does not rotate the token.

## Create a child

CPU, RAM, maximum disk size and lifetime are always explicit. A lifetime is `never` or
an integer followed by `m`, `h` or `d`, with a minimum of five minutes:

```bash
# The host downloads a public ISO, then creates and starts the child.
construct vm create \
  --iso-url https://example.org/linux.iso \
  --cpus 2 --ram-gb 4 --disk-gb 40 --lifetime 4h \
  --preset linux

# Upload local install and answer-file media and leave the VM powered off.
construct vm create \
  --iso ./windows.iso --aux-iso ./answer-files.iso \
  --cpus 4 --ram-gb 8 --disk-gb 80 --lifetime never \
  --preset windows --no-start

# Reuse media already present on the host.
construct vm create \
  --media MEDIA_ID --aux-media AUX_ID \
  --cpus 2 --ram-mb 2048 --disk-gb 30 --lifetime 2h
```

`--sha256 HEX` verifies URL or local install media. Local files are uploaded in
resumable chunks and dedicated to the new child, so successful child deletion also
collects that dedicated media. Creation waits until every media item is ready before it
submits the VM job.

Hardware options are capability-checked by the service:

```text
--preset windows|linux
--generation 2
--secure-boot on|off
--secure-boot-template microsoftWindows|microsoftUefiCertificateAuthority
--tpm on|off
--boot-order installMedia,auxiliaryMedia,disk,network
--no-network
--no-start
```

Presets provide firmware hints only. They never invent CPU, RAM, disk or lifetime
values. The host reports hardware/media preparation and VM boot; it does not claim an
arbitrary guest OS finished installing.

## Inventory and lifecycle

```bash
construct vm list
construct vm list --all-shared --json
construct vm inspect CHILD
construct vm addresses CHILD --json

construct vm start CHILD --lifetime 2h
construct vm restart CHILD       # keeps the existing lease deadline
construct vm shutdown CHILD      # graceful guest shutdown; no forced fallback
construct vm save CHILD
construct vm renew CHILD --lifetime 4h
```

`start` handles Off, Saved and Paused children and always requires a new lifetime.
`restart` is intentionally not a renewal. A finite lease expiry requests the same
graceful shutdown as `shutdown`; if guest integration cannot perform it, the operation
and overdue lease remain visible rather than silently saving or powering the VM off.

An empty address list is normal while firmware or an installer is running. Inspect and
create results return connection/address data when the backend knows it and otherwise
preserve the honest unknown state.

## Sharing, hardware and media

Children begin private. The owner may expose operational access to every registered user
on the same host:

```bash
construct vm share CHILD --scope host
construct vm share CHILD --scope private
```

Sharing permits inspection, lifecycle and console operations. It does not transfer
ownership, reveal guest credentials, or let another user delete the VM or change its
hardware/media. All capacity remains charged to the owner.

Hardware and attachments can be changed while the child is Off:

```bash
construct vm hardware CHILD --cpus 4 --ram-mb 4096 --disk-gb 60
construct vm hardware CHILD --secure-boot on --tpm off \
  --boot-order installMedia,disk,network

construct vm media list
construct vm media upload ./installer.iso --role install
construct vm media acquire https://example.org/installer.iso --role install
construct vm media attach CHILD --install MEDIA_ID \
  --boot-order installMedia,disk
construct vm media detach CHILD --aux
construct vm media delete MEDIA_ID --yes
```

Uploads resume when the same `--operation-id` is used again. Only one chunk is staged
locally at a time. Media deletion is refused while a VM still references the item;
the CLI lists those references. Existing media must be ready before it can be used
with `create`; if it is still transferring, wait for its job and retry.

## Boot console

Console calls create one short-lived session and close it when the command exits:

```bash
construct vm console CHILD --screenshot ./boot.png
construct vm console CHILD --screenshot ./boot-small.png --width 640 --height 480

printf 'setup text' | construct vm console CHILD --type-stdin
construct vm console CHILD --type-file ./input.txt
construct vm console CHILD --key 13
construct vm console CHILD --key 16 --press
construct vm console CHILD --key 16 --release
construct vm console CHILD --scancodes 0f,8f
construct vm console CHILD --ctrl-alt-del

construct vm console CHILD --move 100,200
construct vm console CHILD --move-rel 5,-2
construct vm console CHILD --click 1
construct vm console CHILD --press 1
construct vm console CHILD --release 1
```

Typed text is accepted only from stdin or a file. There is deliberately no `--type
"secret"` argument because process arguments are visible to other processes. Screenshot
support works independently of guest networking where the backend provides it. The
initial Hyper-V backend does not provide an interactive video/VNC session, and mouse
input can truthfully return unavailable with a fallback hint.

In the Alpine text-console field test, Hyper-V's `TypeText` produced escape sequences;
raw `--scancodes` input worked. Scancode requests accept at most 64 bytes. Split longer
input at complete key sequences and release any modifiers before ending a request.

## Child forwards

`construct expose` always targets the current primary. To ask for a connection to a
child, name that child explicitly:

```bash
construct vm forward CHILD 22 --to client --connect-port 22
construct vm forward CHILD 8080 --to host
```

Client forwarding is opened through the requester's primary and waits for the client
acknowledgement. Hyper-V child addresses are guest-reported and unverified in this
delivery, so the CLI warns about that fact. Host forwarding to a Hyper-V child is refused
until the backend can verify the destination address.

## Jobs, progress and retries

Background operations return job ids and are followed by default:

```bash
construct vm jobs --json
construct vm wait JOB_ID
construct vm wait JOB_ID --json-progress
construct vm cancel JOB_ID
```

Human progress is written to stderr as `[HH:MM:SS] phase: text`. `--json` keeps stdout to
one result object. `--json-progress` writes NDJSON phase/progress events and a final state
event, which is useful for agents consuming a long operation incrementally.

Every job-starting or retryable mutation sends an operation key. The CLI generates one
and prints it on stderr, or accepts `--operation-id ID`. Retry a lost request with the
same id and identical inputs to recover the original operation. Creation derives
`ID:install`, `ID:aux` and `ID:create`, so its media and VM steps cannot alias. A changed
request with an old id is a conflict rather than an accidental second mutation.
The CLI prints `replayed` when the service returns a previous operation. Keys may be
8–128 characters; `create` limits the base key to 120 to leave space for its sub-keys.

Use `--no-wait` on create, shutdown, restart, delete or media acquire to return as soon as
the service accepts the job.

## Deletion

```bash
construct vm delete CHILD --yes
```

Without `--yes`, an interactive terminal requires the exact child name to be typed back;
a noninteractive call exits 1. Deletion permanently removes the child disk, saved state
and dedicated media. A primary token cannot delete its own primary, and shared callers
cannot delete someone else's child. Parent deletion and its cascade confirmation remain
an owner/admin operation outside this guest CLI.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success (`2xx`, or a succeeded job) |
| 1 | Usage/local error, missing dependency/file or missing confirmation |
| 2 | Invalid request (`400`, `413`, `422`) |
| 3 | Not found (`404`) |
| 4 | Authentication/authorization refused (`401`, `403`) |
| 5 | Conflict, capacity, unavailable capability/device or expired console session (`409`, `410`) |
| 6 | Job failed or was cancelled |
| 7 | Timed out waiting for a job or client forward |
| 8 | Service unreachable, server failure or unusable response |
| 9 | No managed service/delegation, including a legacy primary token |
| 10 | Host maintenance (`503`) |
| 11 | Rate limited (`429`) |

The Linux contract tests use a fake HTTP client. They validate request shapes, credential
hygiene, progress and exit behavior; they do not exercise Hyper-V or the Windows host
service. See the frozen [host-administration contract](plans/host-administration-contracts.md#9-guest-cli-contract-construct-vm-)
for the complete wire shapes and documented backend limitations.

## Service semantics

The host service supports owner delegation through rotated primary tokens. A legacy
VM token cannot create children, and a child receives no Construct credential. Discovery
reports current limits; each mutation resolves the owner's policy again.

Every child creation and start supplies `lifetime`: `never` when allowed, or a positive
number followed by `m`, `h` or `d`, at least five minutes. Powered-off creation stores an
inactive lease. Start/resume replaces it with the newly requested lifetime; explicit
renewal extends a running lease. Restart, guest reboot, save and host/service downtime
never renew it. Expiry requests graceful shutdown, never force-off or deletion. Guest
shutdown failure leaves an overdue lease and retained resource charges for retry.

Host sharing permits registered users and their primary tokens to inspect, start/resume,
restart, gracefully shut down, save and request console/forward access under the owner's
policy. Shared callers cannot delete, renew leases, change sharing, or administer hardware
and media. Resource charges always belong to the child's owner. Revoking sharing removes
shared console sessions and requests exposure cleanup immediately; new operations check
current scope. Open job event streams may finish after access is revoked.

Primary deletion previews **all** private and shared children. The exact scope must be
confirmed with its short-lived token. Acceptance fences the complete scope and revokes
primary delegation atomically. Cleanup failures retain ownership and remaining storage
liability; retry from a fresh preview. Expiry never initiates this deletion workflow.

Owner/admin and the owning primary token may update an off child's hardware or media with
`PUT /vms/{child}/hardware` and `PUT /vms/{child}/media`. Shared callers are refused.
CPU/RAM and supported firmware settings are applied through the child driver; disk growth
currently returns `unsupported-capability`. Media null values detach the corresponding
slot. References protect both sides of a partial attachment. If configuration is
interrupted, startup returns `configuration-incomplete`; retry the same configuration
request to complete it. Runtime capacity is evaluated using the updated hardware on start.

An unresolved media change appears as `observed.storageProblem = "media-unverified"`
in inventory and retains both old and intended media references. Settlement requires
retrying the same media request; capacity reconciliation does not settle attachments.
An already-off shutdown or expiry does not prevent that retry. Configuration recovery
currently accepts the same request only. Resolve the backend failure and retry; if that
request cannot succeed, the supported escape is owner/admin deletion and recreation of
the child. There is no abandon/supersede configuration API.

## Current Hyper-V limits

- Generation 2 and fixed RAM are supported; Generation 1, dynamic memory and memory
  overcommit are not. Disk growth through `hardware --disk-gb` currently returns
  `unsupported-capability`.
- Interactive VMConnect/RDP/VNC video is not exposed. Screenshot and keyboard are
  supported by the bounded WMI transport; mouse is conditional and may return
  `applied:false`. LocalSystem screenshot capture and raw scancode input were verified
  on Alpine; other guest/input combinations still need field testing.
- Child addresses are guest-reported and unverified. Client forwarding may use such an
  address with a warning; host forwarding to a child is refused. Recorded network rules
  are intended relationships only—this delivery enforces no packet isolation.
- Secure Boot templates are `microsoftWindows` and
  `microsoftUefiCertificateAuthority`. Hyper-V locks the template after TPM
  initialization, so later template changes are refused.

## Browser console

An opt-in browser viewer is available with `construct vm console NAME --web`.
It connects through Guacamole to Hyper-V VMConnect and supports boot/installer
consoles. See [gateway installation and session boundaries](../console-viewer/README.md).
