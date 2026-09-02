"use strict";
// Driver dispatch for the extension side of the hypervisor contract.
//
// An instance (see the instance registry, docs/plans/modular-remote-architecture.md
// §4.3) names its `backend`; getDriver(backend) hands back the object that knows how
// to talk to it. Two exist: "hyperv-local" — the default instance, and what a
// missing/empty backend resolves to, so an install with no registry file behaves
// exactly as it always has — and "hyperv-remote", a VM on a host running the
// `constructd` service (docs/remote-host.md).
//
// Driver shape:
//   {
//     backend: string,
//     capabilities: { checkpoints: bool, console: "vmconnect"|"none"|<url>, suspend: bool,
//                     hostLifecycle: bool },
//     queryVmState(instance, opts)        -> Promise<'running'|'off'|'absent'|'unknown'>
//     queryAutoCheckpoints(instance, opts) -> Promise<'on'|'off'|'absent'|'unsupported'|'unknown'>
//     startVm(instance, opts)             -> bool (spawned?)
//   }
// `instance` is the normalized instance object ({ name, backend, vmName, vmHost, ... });
// a driver only reads what it needs. See docs/drivers.md.

const hypervLocal = require("./hyperv-local");
const hypervRemote = require("./hyperv-remote");

/** The backend a missing/empty `backend` field means — today's zero-change path. */
const DEFAULT_BACKEND = "hyperv-local";

const DRIVERS = {
  "hyperv-local": hypervLocal,
  "hyperv-remote": hypervRemote,
};

/**
 * A driver for a backend this build doesn't know (an instances.json written by a
 * NEWER Construct, or a typo). It never spawns anything: the queries resolve the
 * "can't tell" values the UI already degrades gracefully on, and startVm declines
 * with a logged reason instead of pretending to have started something.
 * `opts._log` is a test seam (default console.warn).
 */
function unknownDriver(backend) {
  const name = String(backend == null ? "" : backend);
  const reason =
    `Construct: no VM driver for backend "${name}" — power state and Start are unavailable ` +
    `for this instance. Update The Construct if this instance was created by a newer version.`;
  return {
    backend: name,
    capabilities: { checkpoints: false, console: "none", suspend: false, hostLifecycle: false },
    unknown: true,
    queryVmState() { return Promise.resolve("unknown"); },
    queryAutoCheckpoints() { return Promise.resolve("unknown"); },
    startVm(instance, opts) {
      const log = (opts && opts._log) || console.warn;
      log(reason);
      return false;
    },
  };
}

/**
 * Resolve a backend id to its driver. Missing/empty → the local Hyper-V driver
 * (the default instance). Unknown → the degrading driver above; never throws, so a
 * bad registry entry can't take the panel down.
 */
function getDriver(backend) {
  const key = String(backend == null || backend === "" ? DEFAULT_BACKEND : backend).trim().toLowerCase();
  return DRIVERS[key] || unknownDriver(backend);
}

/** Backend ids this build implements (for diagnostics/UI). */
function listBackends() {
  return Object.keys(DRIVERS);
}

// ── Which lifecycle actions a backend may drive ──────────────────────────────
// The host PowerShell lifecycle scripts (Auto-Install.ps1, Create-AgentVM.ps1,
// Set-AgentVmCheckpoints.ps1) speak to the LOCAL Hyper-V of the machine they run on —
// they load the hyperv-local driver and know no other. So for any other backend the
// actions that CREATE, DELETE or reconfigure a VM would operate on a local VM that
// merely shares the instance's name: Reinstall on a "hyperv-remote" instance can wipe
// the local Agent-VM, and setCheckpoints can rewrite the local VM's checkpoint policy.
//
// reprovision and exportConfig are different in kind: they are pure SSH to an
// ALREADY-RUNNING VM at the instance's endpoint, so they are correct for every backend
// that has one.
//
// The gate is a CAPABILITY (`hostLifecycle`), declared by the driver, not a backend
// name checked in lifecycle.js: the remote driver re-enabled these actions in B7 by
// declaring what it can do — Auto-Install.ps1 gained a `-Backend hyperv-remote` path —
// without a line of lifecycle.js changing.
const HYPERVISOR_ACTIONS = Object.freeze(["reinstall", "redownload", "setCheckpoints"]);

// setCheckpoints asks a SECOND question on top of hostLifecycle, because the two are
// genuinely different: "can the host scripts drive this backend's VMs at all?" and
// "does this backend HAVE checkpoints?". hyperv-remote answers yes to the first and no
// to the second, so the generic "remote lifecycle arrives with the remote driver"
// refusal would now be a lie. Stated as a per-action capability requirement rather than
// as a special case, so a Proxmox driver (snapshots, but a different lifecycle story)
// slots in the same way.
const ACTION_CAPABILITY = Object.freeze({ setCheckpoints: "checkpoints" });

/** Does `action` touch the hypervisor (rather than just SSH into the VM)? Pure. */
function isHypervisorAction(action) {
  return HYPERVISOR_ACTIONS.indexOf(String(action)) >= 0;
}

/**
 * May `action` be launched for an instance on `backend`? Returns
 * { ok: true } or { ok: false, reason }. Never throws; an unknown backend refuses
 * the hypervisor actions (its driver declares no capabilities). Pure.
 */
function lifecycleSupport(backend, action) {
  if (!isHypervisorAction(action)) return { ok: true };
  const driver = getDriver(backend);
  const caps = driver.capabilities || {};
  const name = driver.backend || String(backend);
  if (caps.hostLifecycle !== true) {
    return {
      ok: false,
      reason: `the "${name}" backend can't be rebuilt or reconfigured from here — ` +
        "the host scripts drive the local Hyper-V. Reprovision and Export config still work.",
    };
  }
  // The action's own capability, when it has one (setCheckpoints -> checkpoints).
  const needed = ACTION_CAPABILITY[String(action)];
  if (needed && caps[needed] !== true) {
    return {
      ok: false,
      reason: `the "${name}" backend has no ${needed} — that setting applies to VMs on this PC's Hyper-V only.`,
    };
  }
  return { ok: true };
}

module.exports = {
  getDriver, listBackends, unknownDriver, DEFAULT_BACKEND,
  HYPERVISOR_ACTIONS, ACTION_CAPABILITY, isHypervisorAction, lifecycleSupport,
};
