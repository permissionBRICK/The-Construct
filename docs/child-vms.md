# Child VMs (`construct vm`)

`construct vm` lets a service-managed primary Construct create and operate temporary,
general-purpose child VMs on its host. A child boots ordinary ISO media; it is not a
clone of the primary, receives no Construct management token, and can be Windows, Linux
or another OS supported by the host backend.

This command is for the guest shell. Host administrators use the host administration
API/UI, while the existing primary provisioning flow remains unchanged.

> **Validation status:** a live Alpine smoke test on a Hyper-V host (recorded in the
> maintainer's private notes) verified delegated creation, ISO boot, screenshots, raw keyboard input and
> graceful shutdown through the production Hyper-V service. Broader Windows, sharing,
> networking and recovery coverage still requires the
> [host-administration field test](field-test-host-admin.md).

## Windows guests

The service supports unattended Windows 11, Server 2022 and Server 2025 children on
Hyper-V and Proxmox. Choose a product and edition together. `--os windows` defaults
to `--windows win11-pro`; omitting `--os` preserves the existing Linux/manual flow.

| Product | Editions |
| --- | --- |
| `win11` | `pro`, `pro-n`, `enterprise`, `education` |
| `server2022`, `server2025` | `standard`, `datacenter`, `standard-core`, `datacenter-core` |

Acquire official media once on the host, then use the returned media ID:

```bash
construct vm media acquire --windows 11 --edition pro --lang en --json
# Server evaluation media:
construct vm media acquire --windows server-2022 --edition standard --lang en --json

construct vm create --name windows-lab --os windows --windows win11-pro \
  --media MEDIA_ID --ram-gb 8 --disk-gb 100 --lifetime 4h \
  --unattend-admin-password 'YOUR-DISPOSABLE-LAB-PASSWORD' \
  --unattend-hostname WINDOWS-LAB --unattend-locale en-US --unattend-time-zone UTC
```

The acquire job resolves Windows 11 with a pinned [Fido resolver](https://github.com/pbatard/Fido/tree/3d47260b8915385c58e20c73e24b36e9a9536f3f).
The host needs PowerShell for that resolver, `pwsh` on Proxmox. Server acquisition
uses Microsoft's evaluation downloads and currently supports English. Microsoft's
consumer download may not contain Enterprise or Education; supply licensed media
for editions absent from that download. The service checks the WIM/ESD image metadata
and refuses unavailable products or editions before allocating VM hardware.

Both the original and prepared download are host-owned shared media. They appear in
`construct vm media list --json` with `shared`, `sha256` and `windows` metadata containing
product, language, image names, editions and builds. Only an administrator can delete
shared media. Downloads and preparation run as host jobs with progress under Operations.
Repeated acquisition uses the cached product/language item. Delete unused cached items
as an administrator to fetch a newer release.

Use `--iso ./windows.iso` or `--media ID` for supplied media. The host prepares it during
creation, or prepare it separately with `construct vm media prepare-windows ID`.
Preparation copies the original and patches the four-byte EFI boot catalog pointer to
the ISO's own `efisys_noprompt.bin`. It preserves UDF and does not repack the install ISO.
ISO reading and answer-file creation use managed libraries on both hosts.

The host renders a client or server answer file with GPT partitions, an exact install
image index, and only a public setup key. Server evaluation images omit the setup key.
Windows children require generation 2/UEFI, Microsoft Secure Boot and TPM, with disk
before install media in the boot order. Proxmox uses a real CPU model, SATA system disk
and e1000e NIC. It attaches the virtio-win ISO as a third medium so first logon can install
the serial driver and QEMU guest agent.

`--unattend-admin-password` sets the built-in `Administrator` password, including
autologon. These are also the SSH credentials. Setup installs and starts OpenSSH, adds
its firewall rule, selects PowerShell as its shell, enables RDP/WinRM and applies the
reference rig's disposable-lab settings. Client and server keep separate provisioning
scripts. The supplied password exists in the generated answer-file ISO on the host;
that dedicated media is deleted with the child. The service does not use guest passwords
for monitoring or activation. Keep these guests and credentials within your lab network.

Optional settings are `--unattend-hostname`, `--unattend-locale`, and
`--unattend-time-zone`, using a Windows time-zone ID. `--unattend-first-logon PATH.ps1`
sends an extra script to run before the completion beacon. `--unattend-files PATH.json`
sends a JSON object mapping flat filenames to UTF-8 text. Files are available beside the
script on the answer-file CD; copy anything needed later into the guest before completion.
The combined extra text is limited to 1 MiB. An administrator password is required whenever
unattend options are supplied. Alternatively supply `--aux-iso` or `--aux-media`; these
cannot be combined with unattended options and the host does not modify their contents.

Creation finishes when the VM starts. Installation continues in the guest. In the
Companion host panel, open Media, then Windows guests, for the key pool, assignments,
activation state and media preparation. A first-logon report proves the provisioning
script reached its final block with SSH running; it is not inferred from an IP address.
Hyper-V reports are labelled guest-reported. The service ejects install and answer-file
media after that beacon, and Proxmox also ejects its guest-agent medium. On Proxmox it
additionally watches for the first uptime reset and ejects the install DVD at that point
to avoid restarting Setup. Monitoring survives service restarts through per-incarnation
records. Ejection failures retain media references for retry.

Administrators add pool keys in the Companion or on the host:

```bash
constructd admin windows-keys list --json
# Feed a single JSON line through stdin. Do not put the real key in shell history.
constructd admin windows-keys add --json < /secure/path/key-record.json
constructd admin windows-keys delete KEY_ID
```

The record has `product`, `edition`, `kind`, `key`, optional `notes`, and a positive
`budget` for `kind: "mak"`. Other kinds are `retail` and `kms-client`. Keys are encrypted
with AES-GCM; the host master key uses machine DPAPI on Windows and a root-only file in
`/etc/constructd/keys/` on Linux. Back up the license store and its protection material
together. API responses and audit records never contain the full key.

Automatic and manual assignment require an exact match with the guest's reported product
and edition. No matching key means the generic key and the product's normal evaluation
or unactivated behavior; the panel says `not-activated`. A detected KMS DNS service leaves
activation to KMS. Retail and MAK activation require guest internet access. Server evaluation
to retail conversion is not automated. Evaluation guests do not consume pool keys and show
`evaluation-media-requires-conversion`; use licensed installation media for pool activation.

The host delivers keys after installation through Hyper-V KVP or Proxmox guest-agent
stdin, never through the parent agent or answer file. The host checks the reported partial
key against the assignment and removes the Hyper-V KVP key after the report or timeout.
The Hyper-V guest checks for assignments every minute and after reboot, without a
waiting deadline. Adding a matching key later can activate an already installed guest.
New installations report license status periodically on both backends, including
user-managed, KMS and evaluation installations. The panel distinguishes the installed
key, Windows license status, the activation operation and observation time. Grace
minutes are the value at observation; evaluation expiry is shown only when Windows
reports it. Stale observations are labelled.
Keys and assignments carry the persistent local host ID; cross-host assignment is refused.
Assignments remain tied to the VM incarnation and are released on deletion. A MAK delivery
attempt consumes its budget before sending the key, including uncertain interrupted attempts;
retries and deletion do not refund it.

### Hyper-V license reuse preview

`Constructd:WindowsLicenseReuse` defaults to `false`. Enable it only on a test host
until fresh-disk CID replay, tenant security cleanup and native Windows activation
behavior have been validated with a real eligible license. The implementation uses
`SkipAutoActivation` in generated answer files; this setting alone is not evidence
that Windows can never independently activate after reboot or network restoration.
Do not enable the preview where that guarantee is required before completing the
validation in [the reuse design](plans/windows-license-reuse.md).

Configure `Constructd:VamtModulePath` with the installed VAMT PowerShell manifest.
The host calls Microsoft's `Get-VamtConfirmationId -Products` for initial activation;
it needs internet access. The guest installs the key, reports its installation ID,
and applies the resulting confirmation ID locally. Missing VAMT leaves a visible
prerequisite and does not spend a MAK attempt. Guest setup continues regardless.
KMS activation remains managed by Windows; KMS client keys do not enter the preview
pool. Existing installations retain their previous activation path; they do not acquire
a clean security baseline retroactively. Use host-generated answer media for the
preview so the guest's allocation identifier and activation protocol are present.

Adding a key creates no VM. New machines briefly boot disconnected firmware before
attaching any disk or installation media, so the exported baseline contains an
initialized TPM but no tenant state. This requires temporary CPU/RAM capacity even
with `--no-start`; those reservations are released once the VM is confirmed off.
The guest OS does not boot until requested. Earlier experimental baselines without
this initialization cannot be reused; retire their idle machines and create new ones.

A new request first reserves an available machine with
the exact original hardware profile, or creates the requested hardware. Each eligible
Retail/MAK assignment binds the key to that Hyper-V identity. Deletion removes tenant
disks, checkpoint chains, saved state and configuration, then imports the pristine
firmware-initialized export with the same VM GUID and vTPM identity. The idle machine has no disks,
network connection, owner or parent, and automatic startup is disabled. A VM that
never received a managed key is fully deleted. Personal keys are not harvested.

Each reuse gets a fresh allocation identifier. A machine never reuses a prior user
name, so old name/incarnation requests cannot address a later allocation. This
conservative restriction can mean an incompatible name request creates an unlicensed
VM while an otherwise compatible machine stays idle. Hardware changes are not
performed to force a match.

A reused installation applies its saved CID and verifies Windows' actual state.
A failure leaves the allocation running, marks it for host attention and makes no
new proxy activation request. **Activate again** authorizes one new operation with
the same key. Each initial acquisition is charged before contacting Microsoft;
an uncertain result is not retried automatically. Local replay never spends or
refunds the MAK budget. The budget is a local attempt ledger, not Microsoft's count.

The host panel lists retained machines and successful reuses. Removing a key there
retires its idle machines first; active assignments block removal. The offline key
CLI refuses removal while retained machines exist, so use the host panel to retire
them. Partial tenant cleanup keeps the machine unavailable for allocation.

The admin API adds `POST /api/v1/host/windows/guests/{name}/reactivate` with
`incarnation` and the failed `operationId`; duplicate authorization with the old ID
is rejected. `GET /api/v1/host/windows` includes masked `machines` plus guest
`license` observations and `operation` state. Full keys and CIDs are never returned.

The diskless lifecycle can be checked without a Windows license or installation:

```powershell
# Elevated Windows PowerShell; boots only isolated firmware and cleans up in finally.
.\test\windows-license-lifecycle.live.ps1 -StorageRoot D:\VmTests -LifetimeMinutes 5
```

This verifies configuration/storage mechanics, not Windows activation or removal
of secrets written by a previous guest to its vTPM/firmware.

### Windows field validation

Local automated tests cover contracts, generated XML/ISO, media metadata, key storage,
assignment races, guest-channel commands, reconciliation and Companion parity. A human
must validate these on each host before relying on unattended provisioning:

- On Hyper-V, acquire and prepare Windows 11 media, create with a disposable password,
  confirm UEFI/Secure Boot/TPM boot without a prompt, desktop and SSH, KVP first-logon report,
  masked activation with an appropriate real key, key removal from External KVP and both
  DVDs ejected. Repeat with no key, a failed activation and a service restart during setup.
- On Proxmox, confirm the CPU model boots current Windows 11, SATA/e1000e work without
  setup drivers, the first setup reboot resets the observed uptime and causes timely DVD
  ejection, and setup continues from disk. Verify the third ISO installs the serial driver
  and QEMU guest agent, activation reaches the host through guest exec, and all three media
  are ejected after completion. Restart the service during setup and confirm recovery.
- On both platforms, install Server 2022 and Server 2025 in desktop and Core variants from
  the corresponding media, using both Standard and Datacenter. Repeat client selection with
  Pro N, Enterprise and Education media. Confirm exact image selection, SSH, the no-key evaluation path,
  and licensed-media activation. Exercise Companion acquire/prepare, add/remove key,
  manual assignment, product mismatch refusal, MAK budget exhaustion and delete/recreate
  with the same VM name. Confirm the old assignment cannot affect the new incarnation.

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

Omit `--cpus` by default. The service uses all host logical CPUs, capped by its
supported maximum, the configured per-VM limit and remaining host/user CPU budgets.
Use `--cpus N` only to override that choice. The service resolves this once at creation;
it does not resize existing VMs when host limits change.

RAM, maximum disk size and lifetime are always explicit. A lifetime is `never` or
an integer followed by `m`, `h` or `d`, with a minimum of five minutes:

```bash
# The host downloads a public ISO, then creates and starts the child.
construct vm create \
  --iso-url https://example.org/linux.iso \
  --ram-gb 4 --disk-gb 40 --lifetime 4h \
  --preset linux

# Upload local install and answer-file media and leave the VM powered off.
construct vm create \
  --iso ./windows.iso --aux-iso ./answer-files.iso \
  --ram-gb 8 --disk-gb 80 --lifetime never \
  --preset windows --no-start

# Reuse media already present on the host.
construct vm create \
  --media MEDIA_ID --aux-media AUX_ID \
  --ram-mb 2048 --disk-gb 30 --lifetime 2h
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

Presets provide firmware hints only; CPU defaults come from host policy independently
of the preset. RAM, disk size and lifetime have no implicit values.
The host reports hardware/media preparation and VM boot; it does not claim an
arbitrary guest OS finished installing.

## Inventory and lifecycle

```bash
construct vm list
construct vm list --json
construct vm list --owned-only
construct vm inspect CHILD
construct vm addresses CHILD --json

construct vm start CHILD --lifetime 2h
construct vm restart CHILD       # keeps the existing lease deadline
construct vm shutdown CHILD      # graceful guest shutdown; no forced fallback
construct vm save CHILD
construct vm renew CHILD --lifetime 4h
```

`list` includes this primary's children and accessible host-shared guests by default.
Use `--owned-only` to restrict it to this primary's children. The older `--all-shared`
flag remains accepted. The extension's Child VMs card also includes shared guests,
including when the current primary has no children. The normal `/api/v1/vms` list
uses the same access rules as individual VM reads; explicit parent/kind filters still
apply. Private guests, deleting shared guests and shared guests of disabled owners
are excluded from other users' lists. Sharing does not grant owner-only actions.

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

Text input uses US-layout set-1 scancodes in 64-byte chunks: printable ASCII, Enter,
Tab and Backspace. Other characters are refused. Raw `--scancodes` input worked in
the Alpine text-console field test; live Hyper-V verification of the new text path
is pending. Manual scancode requests accept at most 64 bytes. Split longer input at
complete key sequences and release any modifiers before ending a request.

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
- Interactive video uses the trusted [browser gateway](../console-viewer/README.md). Screenshot and keyboard are
  supported by the bounded WMI transport; mouse is conditional and may return
  `applied:false`. LocalSystem screenshot capture and raw scancode input were verified
  on Alpine. Text uses US-layout set-1 scancodes in 64-byte chunks for printable ASCII,
  Enter, Tab and Backspace; other characters are refused. Live Hyper-V text-path
  verification and other guest/input combinations still need field testing.
- Child addresses are guest-reported and unverified. Client forwarding may use such an
  address with a warning; host forwarding to a child is refused. Recorded network rules
  are intended relationships only—this delivery enforces no packet isolation.
- Secure Boot templates are `microsoftWindows` and
  `microsoftUefiCertificateAuthority`. Hyper-V locks the template after TPM
  initialization, so later template changes are refused.

## On a Proxmox host

Proxmox advertises `children`, `media`, `console` and `network` through the same
API. The existing CLI, leases, sharing, admission and lifecycle jobs apply.
The backend has Linux fixture and process-runner tests; child boot and device
behaviour still require a human field test on a node.

- Generation 2 maps to Q35/OVMF and fixed RAM with ballooning disabled. Linux uses
  VirtIO SCSI and an optional VirtIO NIC. The Windows preset uses a SATA disk,
  e1000e NIC and a real CPU model. `ide2` holds the installation ISO and `ide0`
  the auxiliary ISO. Host-rendered Windows installations also attach the virtio
  guest-agent ISO at `ide1`. Children receive no Construct service credentials.
- Both Secure Boot template names map to OVMF's combined pre-enrolled Microsoft
  Windows and UEFI CA keys. The requested name is retained in the description,
  but it does not select distinct key sets. Proxmox does not lock the template
  after TPM initialization. Changing Secure Boot or its template while Off
  recreates the EFI variables disk and loses custom firmware variables. Turning
  TPM off deletes its state; turning it back on creates a new TPM. A guest using
  TPM-bound encryption may require its recovery key after these changes.
- The installer registers directory storage `construct-media` at
  `/var/lib/constructd/media`, with `iso` content. `--media-storage` selects its
  ID, stored as `Proxmox:MediaStorage`. `HostAdmin:Media:RootDir` points to
  `/var/lib/constructd/media/template/iso`; generated ISO paths map to
  `<media-storage>:iso/<id>.iso`. Existing storage must be a directory at that
  path. The node needs `swtpm`, `pve-edk2-firmware`, `pvesm` and Python 3.
- Child disks use `Proxmox:Storage`; arbitrary filesystem disk placements are
  refused. A durable ownership journal beside the database records the numeric
  ID, SMBIOS UUID, operation and allocated volumes. Keep its `children/`
  directory with the database during backup or repair. Cleanup retains ownership
  evidence on failure and refuses foreign incarnations or volumes.
- Inventory matches children by name, tag and UUID and resolves the actual guest
  disk separately from EFI/TPM disks. It reserves full possible disk growth
  because config data has no thin-allocation byte count. Capacity therefore can
  be conservative; the installer still defaults to Observe mode.
- Screenshots use QMP screendump and native-resolution PNGs, bounded by the same
  pixel and byte caps as Hyper-V. Keyboard input supports common PC virtual keys,
  set-1 scancodes, Ctrl-Alt-Delete, and US-layout ASCII text. Unicode text and
  unrecognised keys/scancodes are rejected before sending input. Absolute mouse
  movement requires the USB tablet; relative movement and button events use QMP.
  The transport uses a local QMP socket through Python so typed input stays on
  stdin and explicit key release is possible.
- Interactive console is supported through `construct vm console NAME --web` and
  the panel's console buttons, including boot and installer screens. The gateway
  uses a session-bound `qm vncproxy` stream with an eight-character random password;
  users need only Construct authorization. The node accepts one TCP connection per
  session on `Constructd:ListenAddress`, from `Constructd:Proxmox:ConsolePorts`
  (default `5900-5999`, separate from SSH and app forwarding ranges). The primary
  must reach this range on `Constructd:PublicHost`. This LAN hop carries unencrypted
  VNC, so restrict it to the trusted primary network. Session expiry or removal
  terminates the proxy; reconnect creates a new session and password.
- Address reporting requires a running QEMU guest agent. Reports are bound to
  host-configured adapter MACs and remain unverified. Client forwards, refusal
  of child host forwards, and lack of packet isolation match Hyper-V.
- Hardware and media changes require Off. Graceful shutdown uses ACPI or the
  guest agent without forced power-off; save uses `qm suspend --todisk 1`.
  Although the driver supports disk growth, the shared hardware endpoint still
  returns `unsupported-capability`, as on Hyper-V. The backend contract has no
  preset field, so QEMU `ostype` follows the Windows Secure Boot template or
  defaults to Linux; it does not choose or install an operating system.

For an older Proxmox installation with media in the former flat `media/` root,
back up its database and files before changing the root. Registry entries contain
absolute paths, so moving files alone is insufficient. Existing items need a
registry-aware migration or re-upload; this change does not migrate them. A binary
self-update alone does not register storage or change the media root; apply the
installer's storage/settings setup before using child media.

Human acceptance on the node: create and boot an Alpine child, inspect native
screenshots, type and click in its installer, enable its guest agent and inspect
addresses, then exercise shutdown, save/start, lease renewal/expiry, sharing and
deletion. Also create a Windows-preset child with Secure Boot and TPM, verify its
OVMF keys, disk/NIC drivers and two ISO slots, change hardware/media while Off,
and confirm deletion removes guest, EFI, TPM and saved-state volumes. Open the
browser console for a fresh child during its installer and for the primary,
without a Proxmox login. Verify keyboard, mouse, reconnect, expiry and proxy cleanup.
Repeat installer setup to check storage idempotency.

## Browser console

A browser viewer is enabled by default with `construct vm console NAME --web`.
It connects through Guacamole to Hyper-V VMConnect or Proxmox VNC and supports
boot/installer consoles without guest networking or a guest agent.
See [gateway installation and session boundaries](../console-viewer/README.md).

The main panel's **Console** button opens the selected primary itself; child
**Connect VNC** buttons continue to open their named child through that primary.
