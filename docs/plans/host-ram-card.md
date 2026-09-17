# Host admin: the RAM card shows memory in use, not ledger arithmetic

Status: plan, 2026-09-17. Branch `feat/host-ram-card` (off `feat/proxmox-backend`, PR #19).
Implementation is handed to a T3 Code thread; this document is the brief.

## 1. Goal

The Overview tab of the host administration panel draws a RAM bar from ledger commitments
(reserved + unmanaged + headroom over total) and calls the remainder "available". On an 8 GB
Proxmox node with one 4 GB VM that reads "0 bytes available of 7.7 GB, reserved 4 GB, headroom
4 GB", while the machine actually has 4 GB free. Nothing in the bar is measured usage, and the
headroom line is only an admission rule that is not even enforced on Proxmox (Observe mode).

Replace it with a card that answers "how much memory is this host actually using?", shows swap,
shows how much has been promised to VMs as a number that may exceed 100 %, and draws the
admission line only where the ledger enforces it. Both host platforms get the same card.

Decisions already taken:

- **Main bar = measured usage**, stacked: memory resident in VMs, memory used by the host
  itself, free. Values come from the hypervisor inventory, the same numbers `free` or Task
  Manager show.
- **Thin second bar for swap** (Linux swap, Windows page file), only when the host has any.
- **"Committed to VMs" is a number**, e.g. `12 GB committed of 7.7 GB (155 %)`, styled hot above
  100 %. It is the sum of running VMs' allocations (Construct-managed plus unmanaged). Never a bar
  segment, so over-provisioning is visible instead of clipped.
- **One admission line** (total minus headroom) drawn on the main bar only when the capacity mode
  is `Enforce`. In `Observe` mode the card says "admission not enforced (observe mode)" instead.
- **Headroom default becomes platform-aware**: Proxmox `max(1 GiB, total/8)`, Hyper-V stays
  `max(4 GiB, total/8)`. `capacity.ramHeadroomBytes` in the host config keeps overriding both.
- Existing API fields stay (additive change) so the Companion and any other reader keep working.
- Out of scope: the memory-pressure save policy, per-VM usage rows, the storage and CPU bars.

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Capacity payload the panel reads (`ram = { totalBytes, headroomBytes, reservedBytes, unmanagedBytes, physicalFreeBytes, availableBytes }`, plus `capacityMode`) | `service/src/Constructd.Api/Endpoints/HostAdminEndpoints.cs` (~line 92-120, `CapacityConfigAsync`) |
| Ledger arithmetic, headroom default `Math.Max(4L << 30, total / 8)` | `service/src/Constructd.Core/Logic/CapacityMath.cs` (~line 55-130), `CapacityConfig(Mode, RamHeadroomBytes, …)` in `service/src/Constructd.Core/Domain/HostConfig.cs`, defaults in `Configuration/HostAdminDefaults.cs`, `CapacityMode { Observe, Enforce }` in `Domain/VmKinds.cs` |
| Inventory contract `HostResourcesInfo(LogicalCpus, TotalRamBytes, FreeRamBytes, Volumes, ObservedAt)` and per-VM `MemoryStartupBytes` / `MemoryAssignedBytes` | `service/src/Constructd.Core/Abstractions/IHypervisorInventory.cs` |
| Proxmox inventory: `pvesh get /nodes/<node>/status` → `memory.total/free` (the same document carries `memory.used` and `swap.total/used/free`); guests from `/nodes/<node>/qemu` with `maxmem` and `mem` | `service/src/Constructd.Proxmox/ProxmoxInventory.cs` (`Parse`, ~line 60-140) |
| Hyper-V inventory (PowerShell/CIM) | `service/src/Constructd.Windows/HyperV/HyperVInventory.cs` and the driver script it calls; `Win32_OperatingSystem.FreePhysicalMemory` is where free RAM comes from, `Win32_PageFileUsage` (`AllocatedBaseSize`, `CurrentUsage`, MB) is the page file |
| Fake inventory used by tests | `service/src/Constructd.Fakes/` (grep `HostResourcesInfo`) |
| Panel model: `toCapacityBars(summary)` (pure) and `toOverview` | `extension/src/hostadmin.js` (~line 405-440) |
| Panel rendering of `.ha-bars` / `.ha-bar` | `extension/media/hostadmin.js`, `extension/media/hostadmin.css` (~line 40-44) |
| Panel tests | `extension/test/hostadmin.test.js`, `extension/test/hostadmin-ui.test.js` |
| Other readers of the `ram` object | `grep -rn "reservedBytes\|availableBytes\|headroomBytes" extension companion docs` |
| Docs describing the Overview bars | `grep -rln "headroom" docs service/README.md` |

## 3. Design

### 3.1 Inventory (additive)

`HostResourcesInfo` gains nullable `UsedRamBytes`, `SwapTotalBytes`, `SwapUsedBytes` (null =
platform does not report it; the card hides what is null).

- Proxmox: `memory.used` and `swap.*` from the node status document already fetched; `Parse`
  fills them, tests cover a status without `swap`.
- Hyper-V: `UsedRamBytes = Total - FreePhysicalMemory`; page file from `Win32_PageFileUsage`
  summed over all page files (`AllocatedBaseSize` MiB as total, `CurrentUsage` MiB as used); when
  the query fails, null, never a failed inventory.
- Per-VM resident memory: Proxmox `mem` (guest demand) is already parsed as `demand`; Hyper-V
  `MemoryAssignedBytes`. Expose a per-snapshot `VmResidentRamBytes` sum over running VMs (managed
  and unmanaged) in `CapacityMath`, or compute it in the endpoint from the inventory; keep it
  pure and tested.

### 3.2 Capacity payload (additive)

`ram` gains:

```
usedBytes            total - free (measured)
vmResidentBytes      memory currently held by running VMs (measured)
hostOwnBytes         max(0, usedBytes - vmResidentBytes)
committedBytes       reservedBytes + unmanagedBytes (allocations of running/starting VMs)
admission            { enforced: capacityMode == Enforce, lineBytes: total - headroom, availableBytes }
swap                 { totalBytes, usedBytes } or null
```

Existing fields keep their values. `headroomBytes` reflects the platform-aware default of §1
(pass the default into `CapacityMath` from the caller, which knows the platform; `CapacityMath`
stays free of `ConstructdOptions`).

### 3.3 Panel

`toCapacityBars` returns for RAM one composite entry the renderer draws as:

- Label line: `RAM` … `5.1 GB in use of 7.7 GB`.
- Main bar, stacked segments: VMs (accent), host (dimmer accent), rest empty. Tooltip and a
  sub-line: `VMs 2.6 GB · host 1.1 GB · free 2.6 GB`.
- Admission marker: a 1 px vertical line at `admission.lineBytes / totalBytes` when
  `admission.enforced`, with `title="admission line: N GB headroom"`; otherwise the sub-line ends
  with `· admission not enforced (observe mode)`.
- Committed line: `12 GB committed to VMs (155 %)`, class `hot` when > 100 %.
- Swap bar (thin, `ha-bar ha-bar-thin`) with `1.2 GB of 4 GB swap`, only when `swap` is present
  and `totalBytes > 0`.

The CPU and storage bars stay exactly as they are. Older services without the new fields (the
panel may talk to a host that has not updated yet) fall back to today's text so the card never
shows `NaN` or `—` for everything; test that.

### 3.4 Headroom default

`CapacityMath` takes the default headroom as an argument; `HostAdminEndpoints` (and every other
caller, grep `CapacityMath.`) passes `1 GiB` for Proxmox and `4 GiB` for Hyper-V, both still
`max(default, total/8)` and overridden by `capacity.ramHeadroomBytes`. Document the two defaults
in `service/README.md` next to the capacity rows and in `docs/proxmox-host.md` §7.

## 4. Work, in order

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
and the extension tests green.

1. Inventory fields + Proxmox parse + Hyper-V query (with the fake updated); tests.
2. `CapacityMath` resident sum and headroom parameter; endpoint payload; tests for the payload
   (both modes, with and without swap).
3. Panel model + renderer + CSS; tests for `toCapacityBars` (new fields, old service fallback,
   over-commit > 100 %, observe vs enforce, swap hidden when absent) and a rendering test.
4. Docs.

## 5. Acceptance

- On a Proxmox host with 7.7 GB and one 4 GB VM holding 2.6 GB, the card reads roughly
  `3.7 GB in use of 7.7 GB`, `VMs 2.6 GB · host 1.1 GB · free 4.0 GB`,
  `4 GB committed to VMs (52 %)`, a swap bar, and "admission not enforced (observe mode)".
- Allocating 12 GB of VMs on that host shows `12 GB committed (155 %)` in hot styling without
  the bar overflowing.
- A Hyper-V host in Enforce mode shows the admission marker and no observe note; the Companion
  and the existing tests keep passing.
- All suites green; Windows-only queries are guarded so the Linux test run never executes them.

## 6. Rules for the implementing agent

- Work on `feat/host-ram-card` in this worktree (`/root/repos/construct-ram`). Commit in small
  steps with the repo's configured author; never put any other email in commits or files.
- Two other threads work in `/root/repos/construct` and `/root/repos/construct-net`; do not touch
  those checkouts and do not merge or rebase across; the human merges.
- Do not run anything against a real host; suites and fakes are the verification.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation
  in the final report.
