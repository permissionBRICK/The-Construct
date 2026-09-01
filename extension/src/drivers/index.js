"use strict";
// Driver dispatch for the extension side of the hypervisor contract.
//
// An instance (see the instance registry, docs/plans/modular-remote-architecture.md
// §4.3) names its `backend`; getDriver(backend) hands back the object that knows how
// to talk to it. Today only "hyperv-local" exists — the default instance, and what a
// missing/empty backend resolves to, so an install with no registry file behaves
// exactly as it always has.
//
// Driver shape:
//   {
//     backend: string,
//     capabilities: { checkpoints: bool, console: "vmconnect"|"none"|<url>, suspend: bool },
//     queryVmState(instance, opts)        -> Promise<'running'|'off'|'absent'|'unknown'>
//     queryAutoCheckpoints(instance, opts) -> Promise<'on'|'off'|'absent'|'unsupported'|'unknown'>
//     startVm(instance, opts)             -> bool (spawned?)
//   }
// `instance` is the normalized instance object ({ name, backend, vmName, vmHost, ... });
// a driver only reads what it needs. See docs/drivers.md.

const hypervLocal = require("./hyperv-local");

/** The backend a missing/empty `backend` field means — today's zero-change path. */
const DEFAULT_BACKEND = "hyperv-local";

const DRIVERS = {
  "hyperv-local": hypervLocal,
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
    capabilities: { checkpoints: false, console: "none", suspend: false },
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

module.exports = { getDriver, listBackends, unknownDriver, DEFAULT_BACKEND };
