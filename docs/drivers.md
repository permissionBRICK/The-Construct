# Hypervisor drivers

> **Status: implemented for `hyperv-local` (batch B4).** This is the contract every
> backend must satisfy, on both the PowerShell and the extension side. It is the seam
> that lets a remote-Hyper-V driver (batch B7) and, later, a Proxmox driver slot in
> without touching the installer, the provisioner, or the control panel. See
> [the modular/remote architecture plan](plans/modular-remote-architecture.md) §4.2.

## 1. What is and isn't behind the driver

A driver answers exactly one question: **how do I create, control and locate a VM on
this backend?** Everything else is shared and stays backend-agnostic:

| Behind the driver | Explicitly NOT behind it |
|---|---|
| VM create / remove, power, suspend | Autoinstall ISO build (`bin/build-autoinstall-iso.sh`) |
| VM state + endpoint lookup | In-guest provisioning (`bin/provision.sh`, `Provision-AgentVM.ps1`) |
| Reachability wait, install-media detach | Client config (SSH config, VS Code Remote-SSH, OpenCode) |
| Host prerequisites (Hyper-V features, group membership) | Config sync, backup/restore, project profiles |

`Get-ConstructVmEndpoint` is the load-bearing abstraction: everything downstream dials
an **endpoint** (`{ SshHost, SshPort }`) instead of rebuilding a name convention such as
`<name>.mshome.net`. A remote backend returns `serviceHost:allocatedForwardPort` there
and the rest of the stack needs no changes.

## 2. Layout

```
drivers/
  Load-ConstructDriver.ps1              loader: backend id -> dot-sourced driver
  hyperv-local/HyperVLocal.Driver.ps1   backend "hyperv-local" (this build's only one)
extension/src/drivers/
  index.js                              getDriver(backend) dispatch
  hyperv-local.js                       backend "hyperv-local"
```

The backend id comes from the instance registry (`instances.json`, plan §4.3). A
missing registry, a missing entry, or an empty `backend` field all mean
**`hyperv-local`** — which is why an install with no registry at all behaves exactly as
it always has.

## 3. PowerShell contract

Load a driver by **dot-sourcing the loader**; a function cannot dot-source into its
caller's scope, so the loader is a script:

```powershell
$driverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
if (-not (Test-Path -LiteralPath $driverLoader)) { throw "Required helper not found: $driverLoader" }
. $driverLoader -Backend $Backend        # "" / omitted => hyperv-local

if ((Test-ConstructVmPresent -Name $VmName) -eq $true) { ... }
```

`Import-ConstructDriver -Backend <id>` (defined by the loader, and available to the
caller afterwards) resolves an id to its driver file and **throws a clear error naming
the known backends** for anything else. Dot-source `lib\AgentVm.Common.ps1` **before**
the driver: the local driver routes to `Ensure-HyperV`, `Add-HyperVAdminMembership` and
`Remove-AgentVm` there rather than duplicating them.

### 3.1 Functions

| Function | Signature | Returns / notes |
|---|---|---|
| `Get-ConstructDriverCapabilities` | – | `@{ Checkpoints; Console; Suspend; Backend }` |
| `Test-ConstructDriverPrereqs` | – | `$true`/`$false`; cheap, never throws, never elevates |
| `Ensure-ConstructDriverPrereqs` | `-Scope Platform\|HostAccess\|All` | default `Platform`; assumes the caller is elevated |
| `New-ConstructVm` | `-Descriptor <hashtable>` | creates **and configures**; leaves the VM off |
| `Remove-ConstructVm` | `-Name` | includes the disk chain; no-op when absent |
| `Start-ConstructVm` | `-Name` | also resumes `saved`/`paused` |
| `Stop-ConstructVm` | `-Name [-TurnOff] [-Force]` | `-TurnOff` = hard power cut |
| `Save-ConstructVm` | `-Name` | suspend to disk (capability `Suspend`) |
| `Get-ConstructVmState` | `-Name` | `running\|off\|paused\|saved\|absent\|unknown` |
| `Test-ConstructVmPresent` | `-Name` | **three-valued**: `$true` / `$false` / `$null` = can't tell |
| `Get-ConstructVmEndpoint` | `-Name` | `@{ SshHost; SshPort }` |
| `Wait-ConstructVmReachable` | `-Name [-TimeoutSeconds] [-PollIntervalSeconds] [-SettleSeconds]` | `$true` if the port opened; an expired wait is **non-fatal** (`$false`) |
| `Detach-ConstructInstallMedia` | `-Name` | unmount install media + reset the boot order |

Capability-gated — implement these only when `Capabilities.Checkpoints` is `$true`,
and call them only after checking that flag:

| Function | Signature | Returns / notes |
|---|---|---|
| `Get-ConstructVmCheckpointInfo` | `-Name` | `@{ Present; StateText; Enabled; Settable }` from **one** backend lookup — see below |
| `Set-ConstructVmAutoCheckpointPolicy` | `-Name -Enabled <bool>` | throws on failure |
| `Get-ConstructVmAutomaticCheckpoint` | `-Name` | `@{ Enumerated; Certain; Probable; All }` — see below |
| `Remove-ConstructVmCheckpoint` | `-Name -Checkpoint <obj>` | deletes **by object**, never by name |

`Get-ConstructVmCheckpointInfo` is deliberately one call, not four: `Present`
(three-valued, as `Test-ConstructVmPresent`), `StateText` (the backend's own state
text — display only, `""` when unreadable), `Enabled` (`$true`/`$false`/`$null` =
policy not readable) and `Settable` (can the policy be changed at all) all come from a
single lookup. The pre-driver code fetched the VM once and read state and policy off
that one object; splitting it would cost extra round trips *and* open a TOCTOU window
— the VM disappearing, or permissions changing, between the probes — that the original
did not have. Callers consume the returned snapshot rather than re-probing.

`Get-ConstructVmAutomaticCheckpoint` classifies a VM's checkpoints by how sure the
backend is that *it* created them: `Certain` (the backend's own flag — safe to delete
unattended), `Probable` (name-matched only — a caller must ask per checkpoint before
deleting), `All`, and `Enumerated`, which is `$false` when the list could not be read
at all. `Enumerated = $false` must never be reported to a user as "there are none":
`Set-AgentVmCheckpoints.ps1` deliberately fails loudly there rather than claiming a
cleanup it couldn't verify. Checkpoint objects carry at least `Name` and
`CreationTime` and are handed straight back to `Remove-ConstructVmCheckpoint`.

One more function is local-only and **not** part of the portable contract:
`Test-ConstructVmSshPort -SshHost -SshPort`, the raw-socket probe
`Wait-ConstructVmReachable` is built on — generic, but not required of a backend.

`Detach-` is not one of PowerShell's approved verbs. It is kept because it is the
contract name; the driver is dot-sourced rather than exported from a module, so no
verb warning is produced.

### 3.2 State enum

| Value | Meaning |
|---|---|
| `running` | powered on |
| `off` | powered off |
| `paused` | paused (RAM still held) |
| `saved` | suspended to disk; `Start-ConstructVm` resumes it |
| `absent` | the backend **positively** reported "no such VM" |
| `unknown` | anything else — backend unreachable, permission-gated, transient state |

`unknown` means **"can't tell"** and must never be read as "not installed". For
Hyper-V, `absent` is discriminated by `Get-VM`'s `FullyQualifiedErrorId` starting
with `InvalidParameter` — the same test the JS driver uses.

Because the enum collapses transient states (`Starting`, `Saving`, …) into
`unknown`, it can't answer "does this VM exist?" on its own — `unknown` there means
both "mid-transition" and "unreadable". `Test-ConstructVmPresent` is the probe for
that question, and it is three-valued (`$true` / `$false` / `$null` = can't tell) so
`(Test-ConstructVmPresent -Name $x) -eq $true` behaves exactly like the
`Get-VM -ErrorAction SilentlyContinue` test the installer used before: a VM in any
state opens the "already installed" menu, an unreadable Hyper-V falls through to
creation.

### 3.3 Descriptor

`New-ConstructVm -Descriptor @{ ... }`:

| Field | Required | Default | Notes |
|---|---|---|---|
| `Name` | ✔ | – | VM display name |
| `MemoryBytes` *or* `MemoryGB` | ✔ | – | `MemoryBytes` wins; aligned **down** to 2 MB |
| `DiskBytes` *or* `DiskGB` | ✔ | – | size of the new virtual disk |
| `ProcessorCount` | ✔ | – | must be ≥ 1 |
| `VhdPath` | | Hyper-V's default folder`\<Name>.vhdx` | |
| `SwitchName` | | `Default Switch` | |
| `Generation` | | `2` | Secure Boot + firmware boot order are Gen-2 only |
| `IsoPath` | | – | attached as DVD on SCSI 0:1; boot order DVD → HDD → NIC |
| `Nested` | | `$true` | non-fatal: an unsupported host warns and continues |
| `AutomaticCheckpoints` | | `$false` | Construct's default, not Hyper-V's |
| `CheckpointType` | | `Standard` | |
| `AutomaticStartAction` | | `StartIfRunning` | |
| `AutomaticStopAction` | | `Save` | |

Pass `MemoryBytes` when you already computed an exact, aligned byte count (as
`Create-AgentVM.ps1` does after its prompt) so nothing round-trips through a double.

Starting the VM is deliberately **not** part of `New-ConstructVm`: the caller owns when
that happens (and, for a manual-install ISO, whether a console is opened), so it calls
`Start-ConstructVm` itself.

### 3.4 Progress output

Driver functions print their progress with the host script's `Write-Step` /
`Write-Ok` / `Write-Note` — the repo idiom (`lib`'s `Remove-AgentVm` does the same) —
so extracting code out of a script does not change a single line of its output. The
driver defines plain fallbacks **only** when the host hasn't defined them, so it can
also be dot-sourced on its own.

## 4. Extension (JS) contract

```js
const { getDriver } = require("./drivers");
const driver = getDriver(instance.backend);          // missing/empty => "hyperv-local"
const state = await driver.queryVmState(instance);   // 'running'|'off'|'absent'|'unknown'
```

| Member | Signature | Notes |
|---|---|---|
| `backend` | string | the id this driver implements |
| `capabilities` | `{ checkpoints, console, suspend, hostLifecycle }` | `console`: `"vmconnect"` \| `"none"` \| a URL; `hostLifecycle`: the host's own PowerShell scripts create/delete/reconfigure this backend's VMs |
| `queryVmState` | `(instance, opts) => Promise<string>` | `running\|off\|absent\|unknown` (`saved`/`paused` collapse to `off`: Start resumes them) |
| `queryAutoCheckpoints` | `(instance, opts) => Promise<string>` | `on\|off\|absent\|unsupported\|unknown` |
| `startVm` | `(instance, opts) => bool` | fire-and-forget; `true` = launched |

`instance` is the normalized instance object (`{ name, backend, vmName, vmHost, sshPort,
hostAlias, keyName, configBranch, scriptsDir }`); a driver reads only what it needs, and
`null`/missing means the default instance. `opts` carries the test seams
(`_spawn`, `_platform`, `timeoutMs`) and UI flags (`debug`).

`getDriver` **never throws**. An unknown backend (an `instances.json` written by a newer
Construct, or a typo) gets a driver whose queries resolve `"unknown"`, whose
capabilities are all off, and whose `startVm` returns `false` after logging why — the
panel already degrades gracefully on `unknown`.

`capabilities.hostLifecycle` is what gates the VM-destroying lifecycle actions.
`drivers/index.js` exposes `lifecycleSupport(backend, action)`: `reinstall`,
`redownload` and `setCheckpoints` (the `HYPERVISOR_ACTIONS`) are refused unless the
driver declares it, because those actions run through the host's PowerShell scripts,
which drive the LOCAL Hyper-V — on a remote instance they would create or delete a
LOCAL VM that merely shares the name. `reprovision` and `exportConfig` are pure SSH to
an already-running VM and are allowed for every backend. `src/lifecycle.js` asks this
function rather than testing backend ids, so a new driver enables the actions by
declaring the capability.

`src/vmpower.js` stays the panel's entry point and keeps every export and signature it
had; `queryVmState(opts)` / `queryAutoCheckpoints(opts)` / `startVm(opts)` now take an
optional `opts.instance` and dispatch through `getDriver`. An explicit `opts.vmName`
still wins over the instance, so older call sites are unaffected.

## 5. Adding a backend

1. **PowerShell** — add `drivers/<id>/<Name>.Driver.ps1` implementing §3.1, and one
   entry in `Load-ConstructDriver.ps1`'s `$known` map. Emit the same
   `Write-Step`/`Write-Ok` progress lines. Keep `Get-ConstructVmState`'s enum exact —
   in particular, never report `absent` for a failure you couldn't positively attribute
   to "no such VM".
2. **JS** — add `extension/src/drivers/<id>.js` exporting `{ backend, capabilities,
   queryVmState, queryAutoCheckpoints, startVm }` and register it in
   `extension/src/drivers/index.js`. Keep the pure builders pure (they are what the unit
   tests pin) and never let a driver reject: resolve `"unknown"` instead.
3. **Capabilities** — report honestly, `hostLifecycle` included (declare it only once
   the host scripts really can create/delete/reconfigure that backend's VMs; until then
   the panel refuses those actions with "remote lifecycle arrives with the remote
   driver").  Callers gate features on the flags, never on the
   backend id: `Set-AgentVmCheckpoints.ps1` refuses to run when `Checkpoints` is false,
   and the panel hides a console affordance when `console` is `"none"`. A backend that
   reports `Checkpoints = $true` MUST implement all four checkpoint functions —
   `Set-AgentVmCheckpoints.ps1` will call them, and it deletes things.
4. **Tests** — extend `test/driver-contract.test.ps1` (stub the backend's API/cmdlets in
   the test scope and assert the state mapping, the endpoint and the call sequence) and
   `extension/test/drivers.test.js` (dispatch + degradation).

## 6. Proxmox mapping notes (design-only)

Recorded from plan §4.2/§4.9 so the next implementer has a checklist. Nothing Proxmox
is implemented; every contract op maps 1:1 onto the Proxmox REST API:

| Contract op | Proxmox VE REST |
|---|---|
| `Test-/Ensure-ConstructDriverPrereqs` | `GET /version` + API-token validation (no host features to enable) |
| `New-ConstructVm -Descriptor` | `POST /nodes/{node}/qemu` (`cores`, `memory`, `scsi0: <storage>:<sizeGB>`, `ide2: <iso>,media=cdrom`, `boot: order=ide2;scsi0;net0`) |
| `Remove-ConstructVm` | `DELETE /nodes/{node}/qemu/{vmid}` (`purge=1`, `destroy-unreferenced-disks=1`) |
| `Start-/Stop-/Save-ConstructVm` | `POST .../status/start` \| `stop`\|`shutdown` \| `suspend` (`todisk=1`) |
| `Get-ConstructVmState` | `GET .../status/current` → `running`/`stopped`/`paused`/`suspended` → map to the enum; a 404/`does not exist` is the **only** `absent` |
| `Get-ConstructVmEndpoint` | node IP + the forwarded/bridged SSH port recorded for the VM |
| `Wait-ConstructVmReachable` | unchanged — it is a socket poll against the endpoint |
| `Detach-ConstructInstallMedia` | `PUT .../config` with `ide2: none,media=cdrom` + `boot: order=scsi0;net0` |
| `Get-ConstructVmCheckpointInfo` | one `GET .../status/current` covers `Present`/`StateText`; Proxmox has **no** "snapshot at every start" policy, so report `Settable = $false` and `Enabled = $null` — or `Checkpoints = $false` outright, which turns the whole feature off in the UI and in `Set-AgentVmCheckpoints.ps1` |
| `Set-ConstructVmAutoCheckpointPolicy` | nothing to set (see above); it is only ever called when `Settable` was `$true` |
| `Get-ConstructVmAutomaticCheckpoint` / `Remove-ConstructVmCheckpoint` | `GET /nodes/{node}/qemu/{vmid}/snapshot` (skip the synthetic `current` entry) → nothing is "certainly automatic" on Proxmox, so everything a name pattern matches belongs in `Probable`; delete with `DELETE .../snapshot/{name}` |
| capability `Console` | the noVNC/xterm.js URL (`/?console=kvm&novnc=1&vmid=…`), not `vmconnect` |
| capability `Suspend` | `suspend --todisk` ⇒ state `saved` |

Two things that are Proxmox-shaped and worth doing at the same time: the ISO must be
uploaded to a storage (`POST /nodes/{node}/storage/{store}/upload`) before it can be
referenced by `ide2`, and every long operation returns a **UPID task id** — the same
job/poll shape `constructd` uses, so a shared "wait for job" helper is worth having.
