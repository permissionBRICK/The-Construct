"use strict";
// VM power control for the Construct VM, from the control panel.
//
// This module is the panel's ENTRY POINT for VM power/state; the backend-specific
// work (the Hyper-V PowerShell probes and the elevated Start-VM launch) moved into
// src/drivers/hyperv-local.js and is reached through src/drivers/index.js — see
// docs/drivers.md. What stays here is what is NOT backend-specific: the panel's
// decision helpers (shouldShowStart, shouldOfferCheckpointApply, planCheckpointOffer)
// and the shutdown command. The pure builders are re-exported unchanged so every
// existing caller — and extension/test/vmpower.test.js — keeps working verbatim.
//
// WHERE THIS RUNS — like lifecycle.js this is part of the UI extension, so its
// Node code runs on the user's LOCAL Windows host even when the window is remote.
//
// The Shutdown action is just `poweroff` over SSH (the VM user is root), so it
// lives as a constant here and is run through src/ssh.js by the extension.

const drivers = require("./drivers");
const hypervLocal = require("./drivers/hyperv-local");

// The Hyper-V VM name Auto-Install.ps1 creates ($HyperVmName) — i.e. the default
// instance's vmName, used whenever no instance/vmName is supplied.
const VM_NAME = "Agent-VM";

// `systemctl poweroff --no-block` asks PID 1 to shut down and returns immediately
// (without waiting for the shutdown to finish), so the SSH call gets a clean exit
// code before the box goes down rather than having the connection torn out from
// under it.
const SHUTDOWN_CMD = "systemctl poweroff --no-block";

/**
 * Resolve the call's target instance + backend from `opts`.
 *
 *   opts.instance — the normalized instance object from the registry (optional).
 *   opts.vmName   — the legacy explicit override; still wins, so every existing
 *                   caller behaves exactly as before.
 *   neither       — the DEFAULT instance: "Agent-VM" on "hyperv-local", which is
 *                   today's behavior for an install with no instances.json.
 * Pure.
 */
function resolveTarget(opts) {
  const inst = (opts && opts.instance) || null;
  const backend = (inst && inst.backend) || drivers.DEFAULT_BACKEND;
  const vmName = (opts && opts.vmName) || (inst && inst.vmName) || VM_NAME;
  return { instance: Object.assign({}, inst, { backend, vmName }), backend };
}

/** The driver for `opts`'s instance (default instance → the local Hyper-V driver). */
function driverFor(opts) {
  return drivers.getDriver(resolveTarget(opts).backend);
}

/**
 * Probe the VM's power state and resolve a coarse state string
 * ('running'|'off'|'absent'|'unknown'). Never rejects; a backend that can't answer
 * (off-Windows, spawn failure, timeout, unknown backend) resolves 'unknown'.
 * `opts.instance` selects the instance; `opts._spawn`/`opts._platform`/`opts.timeoutMs`
 * are passed through to the driver (test seams).
 */
function queryVmState(opts = {}) {
  const { instance } = resolveTarget(opts);
  return drivers.getDriver(instance.backend).queryVmState(instance, opts);
}

/**
 * Read the VM's current automatic-checkpoint policy ('on'|'off'|'absent'|
 * 'unsupported'|'unknown'). Never rejects; same seams as queryVmState.
 */
function queryAutoCheckpoints(opts = {}) {
  const { instance } = resolveTarget(opts);
  return drivers.getDriver(instance.backend).queryAutoCheckpoints(instance, opts);
}

/**
 * Start the VM (locally: an elevated Start-VM console behind a UAC prompt).
 * Fire-and-forget; the caller polls SSH reachability and opens the VM once it
 * answers. Returns true if the start was actually launched.
 */
function startVm(opts = {}) {
  const { instance } = resolveTarget(opts);
  return drivers.getDriver(instance.backend).startVm(instance, opts);
}

/**
 * Should saving the automatic-checkpoint preference offer to apply it to the VM that
 * exists right now? Pure — this is the decision the "upgrade path" finding turns on,
 * so it is stated once here and unit-tested.
 *
 *   actual 'on'/'off'  → offer iff it DIFFERS from what the user wants. Authoritative:
 *                        it catches the VM created before Construct disabled checkpoints
 *                        (no saved key, policy still ON), and it keeps offering after
 *                        "Later" / a declined UAC, because the VM still disagrees.
 *                        Equal → silent, however the preference moved.
 *   actual 'absent'    → no VM to change (the preference applies when one is created).
 *   'unsupported'      → this Hyper-V has no automatic checkpoints at all.
 *   'unknown'          → the probe couldn't read the policy. This is NOT rare: the
 *                        non-elevated `Get-VM` is Hyper-V-permission gated (the
 *                        installer's Hyper-V Administrators membership only takes
 *                        effect at the next sign-in), so an early-life install lands
 *                        here routinely. Deciding on "did the preference change?" would
 *                        reproduce the exact upgrade bug this function exists to fix —
 *                        off→off on a checkpoints-ON VM. So fall back to `applied`: the
 *                        value last CONFIRMED onto the VM (host.readAppliedAutoCheckpoints;
 *                        `null` = never confirmed). Offer while that disagrees — i.e. on
 *                        each save until an apply actually SUCCEEDS. "Later" and a
 *                        declined UAC deliberately don't count: nothing changed on the
 *                        VM, so the next save should still offer.
 */
function shouldOfferCheckpointApply(actual, wantEnabled, applied) {
  if (actual === "absent" || actual === "unsupported") return false;
  if (actual === "on") return wantEnabled !== true;
  if (actual === "off") return wantEnabled === true;
  return (typeof applied === "boolean" ? applied : null) !== (wantEnabled === true);
}

/**
 * Decide whether a saved settings payload should even reach the checkpoint flow, and
 * with what value. Pure, so the sequencing that a round of review found broken is
 * locked at its own layer rather than only inside extension.js's handler.
 *
 * `payload` is the RAW webview message settings, `merged` the form-shaped view of what
 * was actually written to disk, `prev` the form-shaped view from before the write.
 *
 *   - `act` is false unless the payload really carried a boolean. `mapFromForm` OMITS an
 *     absent boolean, so a partial post (a stale webview sending only the git fields)
 *     leaves the stored value untouched — reading "wants off" from it would offer to
 *     DISABLE checkpoints the file still says are on, and delete one.
 *   - `enabled` comes from the MERGED on-disk result, never from the payload, so what we
 *     offer to apply is always what was actually persisted.
 *   - `changed` is only used for messaging (the non-Windows warning), never to decide
 *     whether to offer — see shouldOfferCheckpointApply.
 */
function planCheckpointOffer(payload, prev, merged) {
  const carried = !!payload && typeof payload.autoCheckpoints === "boolean";
  const enabled = !!merged && merged.autoCheckpoints === true;
  return {
    act: carried,
    enabled,
    changed: enabled !== (!!prev && prev.autoCheckpoints === true),
  };
}

/**
 * Whether the "Start & connect" affordance should be shown for a given probed state.
 * The webviews (media/panel.js, media/launcher.js) can't require() this module, so
 * they inline the SAME predicate — this is the canonical definition the unit tests
 * lock so the two copies can't silently drift.
 *
 * Show Start only when the VM is OFFLINE (SSH-unreachable) and NOT known to be absent
 * or running: i.e. vmState is 'off' OR 'unknown'. 'unknown' is included deliberately —
 * the non-elevated Get-VM probe is Hyper-V-permission gated (the installer's Hyper-V
 * Administrators membership is only effective at the next sign-in), so a genuinely
 * stopped VM very commonly reads back 'unknown' rather than 'off'. The Start action
 * self-elevates (UAC Start-VM), so it works regardless of the probe's permission; the
 * only offline case we suppress it for is 'absent' (a privileged probe positively
 * reported the VM doesn't exist). Pure.
 */
function shouldShowStart(online, vmState) {
  return !online && vmState !== "absent" && vmState !== "running";
}

module.exports = {
  VM_NAME, SHUTDOWN_CMD,
  // Backend-specific builders/parsers live in the driver; re-exported here so the
  // module's public surface (and its unit tests) are unchanged.
  buildStateProbeCommand: hypervLocal.buildStateProbeCommand,
  buildStateProbeLaunch: hypervLocal.buildStateProbeLaunch,
  parseVmState: hypervLocal.parseVmState,
  queryVmState,
  buildAutoCheckpointProbeCommand: hypervLocal.buildAutoCheckpointProbeCommand,
  buildAutoCheckpointProbeLaunch: hypervLocal.buildAutoCheckpointProbeLaunch,
  parseAutoCheckpoints: hypervLocal.parseAutoCheckpoints,
  queryAutoCheckpoints, shouldOfferCheckpointApply, planCheckpointOffer,
  shouldShowStart,
  buildElevatedCommandLaunch: hypervLocal.buildElevatedCommandLaunch,
  buildStartCommand: hypervLocal.buildStartCommand,
  startVm,
  // Driver dispatch, for callers that need the backend's capability flags.
  getDriver: drivers.getDriver,
  driverFor,
  resolveTarget,
};
