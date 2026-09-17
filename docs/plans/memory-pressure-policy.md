# Memory pressure: save idle VMs before the host runs out

Status: plan, 2026-09-17. Branch `feat/memory-pressure-policy` (off `feat/proxmox-backend`,
PR #19). Implementation is handed to a T3 Code thread; this document is the brief.

## 1. Goal

Hosts over-provision memory on purpose (Proxmox especially). When the host's measured memory
gets tight, the service **saves** (suspends to disk) VMs that are idle, starting with the one
closest to its own idle timeout, until the pressure is gone. It never shuts a VM down, never
touches a VM that is busy (connections, a busy heartbeat, provisioning, a running job), and
never touches a VM whose idle policy is off. Everything it does is audited and visible in the
panel, and the whole policy can be switched off per host.

Decisions already taken: save, not shut down; ordering by closeness to the idle timeout; busy
primaries are exempt; the policy is host-wide with admin-configurable thresholds; both
platforms; no balloon or KSM tuning in this thread (a follow-up).

## 2. What exists (read these first)

| Piece | Where |
|---|---|
| Idle engine: per-VM evaluation, `_lastActiveAt` bookkeeping, `driver.SaveAsync`, audit | `service/src/Constructd.Core/Services/IdlePolicyEngine.cs` (`EvaluateAsync`, `EvaluateVmAsync`, `ApplyAsync`) |
| Pure evaluator and its inputs (`IdleEvaluationInput`, `IdleDecision`, `ComputeLastActiveAt`) | `service/src/Constructd.Core/Logic/IdleEvaluator.cs`, `IdlePolicyRules.Clamp` |
| Per-VM policy and the heartbeat | `service/src/Constructd.Core/Domain/IdlePolicy.cs` (`TimeoutMinutes`, `Action`), `Domain/ActivityReport.cs` (`Busy`, `Reasons`; its doc comment still mentions tmux and CPU, update it: the guest now reports transcripts, T3 threads, SSH and provisioning), `Endpoints/IdleEndpoints.cs`, `bin/construct-idle-report.sh` |
| Scheduler tick | `service/src/Constructd.Api/Hosting/IdleSchedulerService.cs` (one `PeriodicTimer`, calls the engine; maintenance gate aware) |
| Connection counting | `IPortForwardManager.CountActiveConnectionsAsync` (netsh/TCP table on Windows, relay counts on Proxmox) |
| Measured host memory (just merged) | `HostResourcesInfo.UsedRamBytes/SwapTotalBytes/SwapUsedBytes`, `CapacityMath` (`usedBytes`, `vmResidentBytes`, `committedBytes`), `CapacityReconciliationService` (periodic inventory), `ICapacityLedger` snapshot |
| Per-VM resident memory | inventory `MemoryAssignedBytes` (Hyper-V) / guest `mem` (Proxmox), `VmResourceUsageReader.cs` |
| Host config sections and validation | `Core/Domain/HostConfig.cs`, `Configuration/HostAdminDefaults.cs`, `Infrastructure/HostConfigValidation.cs`, `GET /host/capabilities` |
| Panel Overview and the RAM card | `extension/src/hostadmin.js` (`toCapacityBars`, `toOverview`), `extension/media/hostadmin.js` |
| Jobs that must block a save | `IPersistedJobRunner` / job store: a VM with a running job (create, provision, child create under it) |
| Tests | `service/tests/Constructd.Tests/Core/{IdleEvaluatorTests,IdlePolicyEngineTests}.cs`, `Api/{IdleSchedulerServiceTests,IdleApiTests}.cs`, `Capacity/*` |

## 3. Design

### 3.1 Host config section `memoryPressure`

```
MemoryPressureConfig(
  bool Enabled = true,
  int  HighWaterPercent = 90,   // act when measured used (RAM) exceeds this share of total
  int  LowWaterPercent  = 80,   // stop once used is below this share (hysteresis)
  int  SwapHighWaterPercent = 50, // also act when swap used exceeds this share of swap total (0 = ignore)
  int  MinSecondsBetweenSaves = 60, // one save per interval, then re-measure
  int  CooldownMinutesAfterSave = 10) // a VM saved by pressure is not resumed by this policy; users resume
```

Validated (0 < low < high ≤ 100, etc.), surfaced in `GET /host/capabilities` and editable
in the config tab; defaults in `HostAdminDefaults`.

### 3.2 The evaluation (pure, tested)

`MemoryPressureEvaluator.Plan(input)` takes the measured host memory (used, total, swap),
the config, and one row per running VM: name, kind (primary/child), owner, idle policy
(clamped), `lastActiveAt`, active connections, latest heartbeat, whether a job is running for
it, resident bytes. It returns either "no pressure" or an ordered list of candidates:

- exclude: not running; policy action `Off`; connections > 0; heartbeat busy within the grace
  window (same rule as `IdleEvaluator`, reuse `ComputeLastActiveAt`); a running job; a VM that
  was itself saved or started within the last `CooldownMinutesAfterSave`;
- order by **remaining time to the idle timeout** ascending (`lastActiveAt + timeout - now`),
  ties by larger resident memory first, then by name;
- the plan says how many candidates are needed: keep adding until `used - sum(resident)` is
  below the low-water mark, with an explicit note when even all candidates would not suffice.

### 3.3 The actor

`MemoryPressureService` (a `BackgroundService` next to `IdleSchedulerService`, or a second
phase in the same tick to reuse the engine's `_lastActiveAt`; prefer the latter so both
policies share one notion of "last active"): on each tick, if enabled and the latest inventory
snapshot is fresh, build the plan; save **one** VM per tick (`driver.SaveAsync`), update the
VM state, audit `vm.pressure-save` with the reason (`used 93% > 90%; 4 min to idle timeout`),
then wait for the next measurement before the next save. Respect the maintenance gate. A VM
saved this way carries `savedBy: "memory-pressure"` in its record/inventory row so the panel
can show it and the idle engine does not double-report.

### 3.4 Panel and API

- The RAM card gets a line "memory pressure: off | idle | 93% used, saved `work-vm` 2 min ago";
  the VM list shows a badge "saved (memory pressure)".
- `GET /host/status` carries `memoryPressure: { enabled, state, lastAction }`.
- Audit entries appear under the existing audit route.

### 3.5 Docs

`docs/remote-host.md` (idle policy section) and `docs/proxmox-host.md` get a paragraph;
`service/README.md` the config rows. Say plainly: it saves, users resume; a busy VM is never
touched; if every VM is busy the host stays under pressure and the panel says so.

## 4. Work, in order

1. Config section, validation, capabilities, defaults; tests.
2. Evaluator with a full test matrix (ordering, exclusions, hysteresis, insufficient
   candidates, cooldown).
3. Actor wired into the scheduler tick; engine tests with fakes for driver, forwards, jobs and
   inventory; maintenance-gate test.
4. Status payload, audit, panel line and badge; extension tests.
5. Docs; fix the stale `ActivityReport` comment.

Each step leaves `dotnet test service/tests/Constructd.Tests`, `bash test/run-local-checks.sh`
and the extension tests green.

## 5. Acceptance

- With three idle VMs and measured use above the high-water mark, the tick saves the VM
  closest to its idle timeout, audits it, and saves the next one only after the next
  measurement if still above the low-water mark.
- A VM with an open SSH connection or a busy heartbeat is never a candidate; the plan says
  "insufficient candidates" when all are busy.
- Disabled policy: no evaluation, status says off.

## 6. Rules for the implementing agent

- Work on `feat/memory-pressure-policy` in this worktree (`/root/repos/construct-mem`).
  Commit in small steps with the repo's configured author; never put any other email in
  commits or files.
- Other threads work in `/root/repos/construct`, `/root/repos/construct-net`,
  `/root/repos/construct-children` and `/root/repos/construct-parity`. Do not touch those
  checkouts; keep edits to `HostConfig`, the scheduler and the panel additive so the human can
  merge.
- Do not run anything against a real host; suites and fakes verify.
- Where this plan and the code disagree, the code's existing contract wins; note the deviation
  in the final report.
