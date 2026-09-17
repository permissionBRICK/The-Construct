# Proxmox: child VMs, feature-identical to Hyper-V

Status: plan, 2026-09-17. Branch `feat/proxmox-child-vms` (off `feat/proxmox-backend`, PR #19).
Implementation is handed to a T3 Code thread; this document is the brief.

## 1. Goal

An agent on a Proxmox-hosted primary can run `construct vm create --iso … --cpus … --ram-gb …
--disk-gb … --lifetime …` and get exactly what it gets on a Hyper-V host: a disposable child VM
booted from uploaded or acquired media, with the same hardware surface (UEFI, Secure Boot
templates, TPM, two optical drives, boot order, network on or off), the same lifecycle (start,
shutdown, save, renew, delete, lease expiry, sharing), screenshots and keyboard/mouse through the
console routes, guest-reported addresses, and the same error codes. The `children`, `media`,
`console` and `network` features become advertised on Proxmox. The client (`hyperv-remote`
driver, `construct vm`) needs no change: it already probes the feature list.

Not in scope: Windows unattended install and license keys (separate plan), the direct network
mode (separate thread), linked clones, real network isolation (unsupported on both platforms).

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| The contract a backend must fill | `service/src/Constructd.Core/Abstractions/IChildVmDriver.cs` (`IChildVmDriver`, `IChildVmStorage`, `IChildVmCreationOwnership`, `ChildVmDescriptor`), `Domain/ChildHardware.cs`, `Domain/Capabilities.cs` (`BackendCapabilities`, `VmCapabilitiesSnapshot`), `Logic/HardwarePresets.cs` |
| The Hyper-V reference | `service/src/Constructd.Windows/HyperV/HyperVChildDriver.cs` + `HyperVChildScript.cs` (safe error codes), `drivers/hyperv-local/HyperVLocal.ChildVm.ps1` (capabilities block ~line 134-146, create ~166-272, media ~227-257, hardware ~276-314, graceful shutdown, `Get-ConstructVmAddresses` ~414-465, `Get-ConstructChildVmCapabilities` ~391-410) |
| The stub to replace | `service/src/Constructd.Proxmox/ProxmoxChildVmPlatform.cs`, `Core/Services/UnsupportedChildVmDriver.cs`, `Core/Services/CapabilityAggregator.cs` |
| Jobs and endpoints (platform-neutral, keep) | `service/src/Constructd.Api/Jobs/ChildCreateJob.cs`, `ChildDeleteJob.cs`, `ChildStartIntent.cs`, `ChildLifecycleJobs.cs`, `Endpoints/ChildVmEndpoints.cs`, `ChildConfigurationEndpoints.cs`, `LifecycleEndpoints.cs`, `Hosting/ChildLeaseReconciler.cs`, `Core/Services/DelegationPolicy.cs` |
| Media (already works on Proxmox, only hidden) | `service/src/Constructd.Api/Composition/MediaComposition.cs`, `Constructd.Windows/Media/{MediaFileStore,HttpMediaTransfer}.cs`, `Endpoints/MediaEndpoints.cs`; the installer sets `HostAdmin:Media:RootDir=/var/lib/constructd/media` (`service/host/install-construct-host.sh` ~line 504) |
| Console | `Core/Abstractions/IConsoleTransport.cs`, `Constructd.Windows/Console/HyperVConsoleTransport.cs`, `HyperVInteractiveConsole.cs`, `Core/Services/UnsupportedConsoleTransport.cs`, `Endpoints/ConsoleEndpoints.cs` (note: its exception filter catches `ConsoleTransportException`, not `NotSupportedException`, which is why an unsupported console answers 500 today) |
| Guest addresses | `Core/Abstractions/INetworkPolicyReconciler.cs` (`IGuestAddressProvider`, `IGuestAddressSnapshotProvider`), `Constructd.Windows/Network/HyperVGuestAddressProvider.cs`, `Core/Services/GuestAddressResolver.cs`, `Endpoints/NetworkEndpoints.cs`, `Core/Services/UnsupportedFeaturePlatform.cs` |
| Existing Proxmox code to extend | `service/src/Constructd.Proxmox/ProxmoxDriver.cs` (qm/pvesh through `IProcessRunner`, `ParseGuestAddress`, id allocation, `ProxmoxOperationException`), `ProxmoxInventory.cs` (guests, `maxmem`/`mem`, ownership matching), `Composition/ProxmoxComposition.cs`, `ReleaseInfo.cs` |
| Tests that pin the Hyper-V surface | `service/tests/Constructd.Tests/Windows/HyperVChildDriverTests.cs`, `Jobs/ChildVmJobTests.cs`, `Media/*`, `Console/*`, `Network/*`, `Delegation/*`, and `Proxmox/ProxmoxCompositionTests.cs` (asserts today that `children`/`console` are absent: flip it) |
| Docs | `docs/child-vms.md` (add a Proxmox section next to "Current Hyper-V limits"), `docs/proxmox-host.md` §6, `service/README.md` platform table (~line 1080-1101) |

## 3. Design

### 3.1 Capabilities the Proxmox backend reports

| Field | Value | How |
|---|---|---|
| `Generations` / `DefaultGeneration` | `[2]` / `2` | generation 2 = `--machine q35 --bios ovmf --efidisk0 <storage>:1,efitype=4m`; generation 1 is not offered (parity with Hyper-V, which offers only 2) |
| `SecureBoot` | `Supported` | `pre-enrolled-keys=1` on the EFI disk when on, `pre-enrolled-keys=0` when off |
| `SecureBootTemplates` | `microsoftWindows`, `microsoftUefiCertificateAuthority` | both map to the pre-enrolled Microsoft keys (OVMF ships the Windows and the UEFI CA certificates together); record the requested template in the VM description so it is reported back unchanged |
| `SecureBootTemplateLockedAfterTpmInit` | `false` | Proxmox has no such lock; the EFI disk can be recreated while Off |
| `Tpm` | `Supported` | `--tpmstate0 <storage>:1,version=v2.0` (swtpm ships with Proxmox VE) |
| `MaxOpticalDrives` | `2` | install media `--ide2 <vol>,media=cdrom`, auxiliary `--ide0 <vol>,media=cdrom` |
| `AuxiliaryMedia` | `Supported` | |
| `BootOrder` | `Supported` | `--boot order=ide2;ide0;scsi0;net0` built from `ChildHardware.BootOrder` |
| `DynamicMemory` / `MemoryOvercommit` | `Unsupported` | parity; the balloon stays out of scope |
| `Suspend` | `Supported` | `qm suspend --todisk 1` (fix the aggregator so `Suspend` no longer contradicts `Legacy.Suspend`) |
| `GracefulShutdown` | `Conditional` | `qm shutdown --timeout N` (ACPI, or the guest agent when present); outcome mapping below |
| `Console.{Screenshot,Keyboard,MouseAbsolute}` | `Supported`, `Supported`, `Supported` | §3.5; `MouseRelative` `Supported` too; `Interactive` per §3.5 |
| `Network.DirectAddressReporting` | `Conditional` | guest agent present |
| `Network.{ClientForward,HostForwardPrimary}` | `Supported`; `HostForwardChild` `Unsupported` | parity |
| `Backend` | `proxmox` | |

### 3.2 Media as Proxmox volumes

`qm` attaches ISOs only from a Proxmox storage with `iso` content. Keep the media store and
transfer as they are and make the files visible to Proxmox: the installer registers a directory
storage `construct-media` (`pvesm add dir construct-media --path /var/lib/constructd/media
--content iso`, idempotent, new `--media-storage` option to pick another name) and sets
`HostAdmin:Media:RootDir` to `/var/lib/constructd/media/template/iso`, which is where a dir
storage expects ISO files. The driver maps `<root>/<id>.iso` to `construct-media:iso/<id>.iso`
and refuses any path outside the root. `.part` uploads in the same directory are invisible to
Proxmox (it lists `*.iso` only). A new `Proxmox:MediaStorage` option names the storage.

### 3.3 The driver (`ProxmoxChildVmPlatform` becomes real)

All operations through `IProcessRunner` with fixed argv like `ProxmoxDriver`, JSON from `pvesh`.

- **Create (Off):** allocate an id like the primary does; `qm create <id> --name <child>
  --ostype <win11|l26 from the preset, l26 default> --machine q35 --bios ovmf --efidisk0 …
  [--tpmstate0 …] --scsihw virtio-scsi-single --scsi0 <storage>:<diskGb>,discard=on
  --ide2 <install>,media=cdrom [--ide0 <aux>,media=cdrom] --boot order=… [--net0 virtio,bridge=<bridge>]
  --agent enabled=1 --memory <ramMb> --cores <cpus> --tags construct-child
  --description "construct-child parent=<parent> template=<t> created=<iso8601>"`.
  Ownership evidence = the tag plus the description (the counterpart of the Hyper-V ownership
  marker); `GetCreationOperationAsync` reads it back for failed-create rollback. `GetVmIdAsync`
  returns the SMBIOS UUID (`smbios1: uuid=…`), which `qm create` sets once and never changes: the
  incarnation.
- **Remove:** `qm stop` when not off, then `qm destroy <id> --purge 1 --destroy-unreferenced-disks 1`
  (removes EFI vars, TPM state and the disk). Keep the ownership record on failure like Hyper-V.
- **UpdateHardware (Off only, `vm-not-off` otherwise):** `qm set --cores/--memory`; disk growth
  via `qm disk resize scsi0 <gb>G` (growth only, `validation` on shrink); Secure Boot / template
  change recreates `efidisk0`; TPM on/off adds or removes `tpmstate0`. Read back `qm config`
  and verify like `Set-ConstructVmCpuCount` does.
- **SetMedia (Off only):** `qm set --ide2 <vol>,media=cdrom` / `--delete ide2`, same for ide0,
  and the boot order. **GetAttachedMedia:** parse `qm config`.
- **ShutdownGraceful:** `qm shutdown <id> --timeout <s>`; exit 0 and Off → `Completed`; timeout
  → `Timeout`; no ACPI/agent → `Unavailable`; else `Failed`.
- **GetVmCapabilities:** from `qm config` (bios, efidisk, tpmstate, agent, vga) and
  `query-status`; `NativeWidth/Height` from the screendump.
- **Storage:** existing `ResolveStorageAsync` stays; disk volume names follow `vm-<id>-disk-N`.
- Error codes: reuse the Hyper-V safe-code set (`validation, name-taken, vm-not-off,
  media-not-ready, unsupported-capability, vm-incarnation-conflict, vm-incarnation-changed,
  storage-placement-unavailable`) so `ChildVmEndpoints` maps them identically; add
  `ProxmoxChildScript`-style constants beside `HyperVChildScript`.
- Inventory: `ProxmoxInventory` must recognise children as managed (by name and the tag) so
  capacity and placement evidence behave as on Hyper-V.

### 3.4 Guest addresses

`ProxmoxGuestAddressProvider : IGuestAddressProvider, IGuestAddressSnapshotProvider`:
reported addresses from `qm agent <id> network-get-interfaces` (reuse `ParseGuestAddress`
logic, all `Verified=false`, source `Kvp`-equivalent: add `GuestAgent` to `GuestAddressSource`),
adapters from `qm config` (`net0` MAC and bridge), host neighbours from `ip -j neigh show`,
guest subnets and host addresses from `ip -j addr show <bridge>`. Snapshot capture like the
Hyper-V one. `GET /vms/{name}/addresses` then works for children.

### 3.5 Console

`ProxmoxConsoleTransport : IConsoleTransport` over the QEMU monitor (`qm monitor` is
interactive; use `pvesh create /nodes/<node>/qemu/<id>/monitor --command "<hmp>"` which returns
the HMP output):

- Screenshot: `screendump <tmp>.ppm` then convert PPM to PNG in C# (a small encoder over
  `ZLibStream` plus CRC32, no new package), honour the size cap and `NativeResolutionOnly`.
- Keyboard: `sendkey` with QEMU key names; map `KeyboardInputKind.Text/Key/Scancodes/CtrlAltDel`
  (`ctrl-alt-delete`); Scancodes map through the QEMU keycode table.
- Mouse: `mouse_move`/`mouse_button`; Proxmox VMs carry a USB tablet by default, so moves are
  absolute; report `MouseRelative` too.
- Interactive: `ProxmoxInteractiveConsole` returns a noVNC URL with a one-shot VNC ticket
  (`pvesh create /nodes/<node>/qemu/<id>/vncproxy --websocket 1` plus the node's web address).
  If the extension's console viewer cannot open it (it is built for Guacamole → VMConnect),
  report `Interactive = Conditional` with the URL in the session and document it; do not fake
  parity.
- Fix `ConsoleEndpoints` so an unsupported transport answers a coded 409, never 500.

### 3.6 Composition, features, installer, docs

- `ProxmoxComposition` registers the new driver, address provider and console transport;
  `ReleaseInfo.ApiFeatures` on Proxmox gains `children`, `media`, `console`, `network`.
- Installer: `--media-storage`, the storage registration, the media root, a check that
  `swtpm` and the OVMF firmware are present (`pve-edk2-firmware`), `Proxmox:MediaStorage` in
  the settings.
- Docs: `docs/child-vms.md` gets "On a Proxmox host" (what maps to what, the storage, the
  console URL note); `docs/proxmox-host.md` §6 shrinks accordingly; `service/README.md` platform
  table updated; `docs/plans/proxmox-host-self-update.md` is stale about `NoUpdaterLauncher`,
  fix the two sentences.

## 4. Work, in order

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
and the extension tests green.

1. Media mapping + installer storage + options; tests (path confinement, volume id, installer
   bash test with a stubbed `pvesm`).
2. Driver create/remove/capabilities/id/ownership; `ProxmoxChildDriverTests` at argv level on the
   process-runner fake, mirroring every scenario in `HyperVChildDriverTests`; `ChildVmJobTests`
   pass unchanged with the fake.
3. Hardware, media, boot order, graceful shutdown, VM capabilities; tests.
4. Guest address provider; tests mirroring `Network/GuestAddressTests` where applicable.
5. Console transport (PPM→PNG encoder unit-tested with a fixture), interactive console, the
   409 fix; tests mirroring `Console/ConsoleTransportTests` at argv level.
6. Composition, feature flags, inventory recognition, `ProxmoxCompositionTests` flipped; docs.

## 5. Acceptance

- All suites green on Linux; Hyper-V code and tests untouched.
- On a Proxmox host, `GET /health` advertises `children, media, console, network`; the
  `hyperv-remote` client driver shows child VMs without any client change.
- Field test (a human, on the test node): `construct vm create --iso-url <alpine iso> --cpus 1
  --ram-gb 1 --disk-gb 4 --lifetime 2h` boots, `construct vm console NAME` screenshot shows the
  installer, keyboard input reaches it, `addresses` lists the lease once the guest agent runs,
  `shutdown`, `save`, `renew`, `delete` behave as on Hyper-V; a Windows-preset create (`--preset
  windows --secure-boot on --tpm on`) shows OVMF with Secure Boot and a TPM in the VM config.

## 6. Rules for the implementing agent

- Work on `feat/proxmox-child-vms` in this worktree (`/root/repos/construct-children`). Commit
  in small steps with the repo's configured author; never put any other email in commits or
  files.
- Other threads work in `/root/repos/construct`, `/root/repos/construct-net` (direct network
  mode: touches `ProxmoxDriver`, `ReleaseInfo`, the endpoint route) and
  `/root/repos/construct-ram` (RAM card: touches `ProxmoxInventory`, capacity). Do not touch
  those checkouts and do not merge or rebase across; keep your edits to `ReleaseInfo`,
  `ProxmoxInventory` and the installer small and additive so the human can merge.
- Do not run anything against a real host or the test node; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation
  in the final report.
