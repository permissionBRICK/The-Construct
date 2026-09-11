# Primary VM settings

Contract increment 0, 2026-09-11. Extends the existing `primary-cpu` behavior.

## Routes and DTOs

All routes live below `/api/v1`. Retain GET/PUT `/vms/{name}/cpu`
(`{cpus}`), and add GET/PUT `/vms/{name}/memory` (`{ramGb}`: integer,
1–1024 GiB). The memory response is `{currentRamGb, desiredRamGb, pending,
maximumRamGb, recommendedRamGb, appliesOn:"next-stop-start"}`. Inventory adds
nullable `pendingRamGb`. Advertise `primary-memory`. `/vm-defaults` keeps its
CPU fields and adds `maximumRamGb` and `recommendedRamGb`.

Separate routes preserve CPU clients and reuse the existing idle-policy route;
a combined route would duplicate its authorization and clamping behavior.
GET/PUT `/vms/{name}/idle-policy` stays `{timeoutMinutes, action}` with its existing
cap/clamped response, adding `forceEnabled` so the dialog can disable forbidden choices. All three require an owner/admin user credential; VM tokens
and other users are refused. RAM and CPU accept primaries only. Every mutation
is audited, including refusals, with actor, target and effective owner.

## Allowances and capacity

Admins editing another user's VM obey that owner's CPU/RAM allowance and host caps.
RAM maxima are the whole-GiB floor of min(1024 GiB, remaining owner RAM budget
excluding this VM's own RAM rows, host model bound excluding this VM's RAM rows,
host physical-free bound with this VM's assigned RAM and unreflected reservation
credited). Both host bounds come from the same inventory epoch: model = total −
headroom − all accounted RAM − unmanaged RAM + this VM's accounted RAM; physical =
free − headroom − all unreflected RAM + this VM's unreflected RAM + its assigned RAM
(assigned credit only while nonterminal). Clamp the result to ≥ 0. Expose the per-VM
reclaimable host bound from the ledger snapshot to avoid combining inventory epochs.
Use the same calculation for GET, PUT and apply, independently of observe/enforce mode. Maxima are advisory snapshots, never reservations.
A saved desired value reserves nothing. Recheck policy when applying; start admission
rechecks current capacity atomically before powering on. Observe/enforce semantics stay
as documented in service/README.md; saved settings cannot guarantee a successful start.

At confirmed Off, apply CPU then fixed RAM before the new start intent. A restart
replaces changed CPU, RAM and RAM-dependent saved-state liabilities in its admission
transaction, releasing the old rows and reserving the full new amounts atomically.
For increases and decreases alike, compare held rows to each new resource amount:
release/re-reserve changed CPU rows, changed RAM rows, and changed saved-state rows;
unchanged resources keep their existing holds. Each successful driver call updates
only its own canonical CPU or ram_gb field using a generation guard, so a CPU success
followed by RAM failure remains visible as the actual configured hardware.
A refusal rolls the transaction back; it never double-spends capacity. Hardware may
already have changed on an Off VM when admission refuses; the actual configured
values remain visible, and no start is attempted. Driver failure retains the desired
settings and prevents start. Updates use generation-guarded field-specific writes;
never overwrite unrelated VM state from a stale record.

The existing idle policy has a host timeout cap and ForceEnabled constraint, with
no separate per-user idle cap. Admins get no bypass: the same clamp applies on write
and evaluation. Idle changes take effect immediately. `off` disables automatic idle
action; it does not turn the VM off. Forced idle disallows disabling it in the dialog.

## Pending application and UI

Desired hardware settings are durable and tied to the VM creation identity. RAM uses
`primary-memory:<lowercase-name>` in host_config with `{Created, RamGb}`; GetAsync
returns null unless Created matches vm.Created (same CPU incarnation convention). Apply
only on full Off → Start (including the restart lifecycle job), never Saved/Paused
resume or an Ubuntu reboot. RAM is fixed startup memory (dynamic memory disabled);
after the cold boot the guest sees the new total RAM and CPU count. Existing start
intents retain the hardware/reservations they originally admitted.

Each primary row offers **VM settings…**, replacing the CPU input-box action.
The shared webview modal loads CPU, RAM and idle policy, displays current and desired
values, maxima and idle cap, validates whole numbers/actions, and offers one Apply.
Apply saves changed fields through their existing/new routes sequentially. This is
explicitly not an atomic multi-setting API: on partial failure show the error, reload
authoritative values, and keep the dialog open so the user can review and retry.
Successful Apply closes the dialog and explains that hardware needs a full stop/start.
Row Start/Restart remain; their native confirmation names pending CPU and RAM and
warns that restart interrupts running work. VS Code and Companion share the modal,
message actions `loadVmSettings`/`setVmSettings`, routes and parity fixtures.
Older hosts without `primary-memory` retain CPU/idle editing with RAM unavailable.

## Errors and verification

Reuse existing RFC 7807 codes (`validation` with field/reason, `not-a-primary`,
`operation-in-progress`, `capacity-unavailable`, `capacity-exhausted`,
`power-state-changed`, existing authorization/not-found errors). New allowance-apply
error: `memoryAllowanceExceeded` (409), using camelCase for the new code as explicitly
required by the task; existing kebab-case CPU codes remain compatible. It surfaces
from LifecycleStart apply as a synchronous power-start 409 or lifecycle job failure.
PUT uses `validation` with field `ramGb`. RAM writes use `.Audited("vm.memory")` plus
`CodedProblems.Audit` with actor/owner/target and `ramGb=…`; idle retains `vm.idle-policy`.
Driver failures prevent start; pending settings survive retry and service restart.

Test owner/admin/token permissions, policy changes, durable settings/incarnation,
RAM increase/decrease and restart reservation totals (including saved-state storage),
capacity refusal/concurrency, saved resume, driver failure and pinned Windows argv.
Test modal validation/partial failure, both adapters, encoded client routes and shipped
fixtures. Theme controls use input/dropdown palette variables and native color-scheme
matching light/dark backgrounds; run browser smoke in native and classic themes.
Linux fakes, Node, pwsh and compilation do not validate Hyper-V or Windows runtime.
