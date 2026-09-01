"use strict";
// Hypervisor driver: LOCAL Hyper-V (backend "hyperv-local").
//
// Everything that actually speaks Hyper-V from the extension lives here — the
// inline PowerShell probes and the elevated Start-VM launch that used to sit in
// src/vmpower.js. vmpower.js keeps its exports (the panel's decision helpers, the
// re-exported pure builders) and dispatches the backend-specific work through
// src/drivers/index.js. See docs/drivers.md for the contract.
//
// WHERE THIS RUNS — like lifecycle.js this is part of the UI extension, so its
// Node code runs on the user's LOCAL Windows host even when the window is remote.
// Two host operations live here:
//   • queryVmState — a CAPTURED-output `Get-VM` probe (child_process, stdout read
//     back) used ONLY when the VM is not SSH-reachable, to tell "stopped" apart
//     from "not installed". (SSH reachability already means "running", so we never
//     pay the Hyper-V query — which can need elevation — in the common case.)
//   • startVm — an ELEVATED `Start-VM` launched in a console (Start-Process
//     -Verb RunAs → UAC), fire-and-forget like the lifecycle scripts. Bringing the
//     VM up then connecting is driven by the extension (poll reachability + open).
//
// `vscode` is lazy-required so the pure builders unit-test under plain node. The
// quoting helpers are reused from lifecycle.js (same canonical Windows rules).

const cp = require("child_process");
const lifecycle = require("../lifecycle");

function vsc() { return require("vscode"); }

// The Hyper-V VM name Auto-Install.ps1 creates ($HyperVmName) — the default
// instance's vmName, used when no instance is passed.
const VM_NAME = "Agent-VM";

// Cap captured probe output; the probe prints one short line, so this only guards
// against a wedged/garbage powershell flooding host memory.
const MAX_OUT = 64 * 1024;

/** What this backend can do; the UI gates optional affordances on these.
 *  `hostLifecycle`: the host's own PowerShell scripts (Auto-Install.ps1 /
 *  Create-AgentVM.ps1 / Set-AgentVmCheckpoints.ps1) create, delete and reconfigure
 *  this backend's VMs — they drive the LOCAL Hyper-V, which is exactly this driver.
 *  drivers/index.js gates the VM-destroying lifecycle actions on it. */
const CAPABILITIES = { checkpoints: true, console: "vmconnect", suspend: true, hostLifecycle: true };

/** The VM name for an instance (contract: normalized instance object), or the default. */
function vmNameOf(instance) {
  return (instance && instance.vmName) || VM_NAME;
}

/**
 * The inline PowerShell that prints `VMSTATE=<state>` for the VM. `Get-VM -Name`
 * throws for a missing VM with a FullyQualifiedErrorId beginning "InvalidParameter"
 * — distinct from a permission/Hyper-V-absent failure — so we map that to `absent`
 * and every other failure to `unknown` (caller falls back gracefully). Pure.
 */
function buildStateProbeCommand(vmName) {
  const n = lifecycle.psSingleQuote(vmName || VM_NAME);
  return (
    "try { $vm = Get-VM -Name " + n + " -ErrorAction Stop; Write-Output ('VMSTATE=' + $vm.State) } " +
    "catch { if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { Write-Output 'VMSTATE=absent' } " +
    "else { Write-Output 'VMSTATE=unknown' } }"
  );
}

/**
 * argv for the captured (non-elevated) state probe. The command is passed via
 * -EncodedCommand (base64 UTF-16LE) so no shell/quoting layer can mangle it. Pure.
 */
function buildStateProbeLaunch(vmName) {
  const command = buildStateProbeCommand(vmName);
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  return {
    file: "powershell.exe",
    spawnArgs: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
    command,
  };
}

/**
 * Map the probe's raw `VMSTATE=<x>` line to a coarse state the UI gates on:
 *   running                          -> running
 *   off | saved | paused (resumable) -> off
 *   absent                           -> absent
 *   anything else / transient / none -> unknown
 * Pure.
 */
function parseVmState(stdout) {
  const m = /VMSTATE=(\S+)/.exec(String(stdout || ""));
  if (!m) return "unknown";
  const s = m[1].toLowerCase();
  if (s === "running") return "running";
  if (s === "off" || s === "saved" || s === "paused") return "off"; // Start-VM resumes saved/paused
  if (s === "absent") return "absent";
  return "unknown"; // transient (starting/stopping) or an unrecognised state
}

/**
 * The inline PowerShell that prints `VMAUTOCHK=<x>` — the VM's CURRENT automatic-
 * checkpoint policy, read straight from Hyper-V rather than inferred from the panel's
 * saved preference. That distinction is the whole point: a VM created before Construct
 * started disabling them has the policy ON while the settings file has no key at all,
 * so "the preference didn't change" must NOT be read as "the VM already agrees".
 *
 * `AutomaticCheckpointsEnabled` is property-probed (absent on pre-1709 Hyper-V, which
 * has no automatic checkpoints at all → `unsupported`). A missing VM maps to `absent`
 * via the same FullyQualifiedErrorId test as buildStateProbeCommand; anything else
 * (typically the Hyper-V permission gate) is `unknown`. Pure.
 */
function buildAutoCheckpointProbeCommand(vmName) {
  const n = lifecycle.psSingleQuote(vmName || VM_NAME);
  return (
    "try { $vm = Get-VM -Name " + n + " -ErrorAction Stop; " +
    "$p = $vm.PSObject.Properties['AutomaticCheckpointsEnabled']; " +
    "if ($p -and $null -ne $p.Value) { Write-Output ('VMAUTOCHK=' + [bool]$p.Value) } " +
    "else { Write-Output 'VMAUTOCHK=unsupported' } } " +
    "catch { if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { Write-Output 'VMAUTOCHK=absent' } " +
    "else { Write-Output 'VMAUTOCHK=unknown' } }"
  );
}

/** argv for the captured (non-elevated) automatic-checkpoint probe. Pure. */
function buildAutoCheckpointProbeLaunch(vmName) {
  const command = buildAutoCheckpointProbeCommand(vmName);
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  return {
    file: "powershell.exe",
    spawnArgs: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
    command,
  };
}

/**
 * Map the probe's `VMAUTOCHK=<x>` line to 'on' | 'off' | 'absent' | 'unsupported' |
 * 'unknown'. PowerShell stringifies a bool as `True`/`False`. Anything unrecognised or
 * missing is 'unknown' — the caller must treat that as "can't tell", never as "off".
 * Pure.
 */
function parseAutoCheckpoints(stdout) {
  const m = /VMAUTOCHK=(\S+)/.exec(String(stdout || ""));
  if (!m) return "unknown";
  const s = m[1].toLowerCase();
  if (s === "true") return "on";
  if (s === "false") return "off";
  if (s === "absent") return "absent";
  if (s === "unsupported") return "unsupported";
  return "unknown";
}

/**
 * Run one captured PowerShell probe and resolve `parse(stdout)`. Never rejects:
 * off-Windows, a spawn failure/throw, or a timeout resolves 'unknown' — the value
 * every caller treats as "can't tell". `opts._spawn`/`opts._platform` are test seams
 * (default child_process.spawn / process.platform). Shared by both probes so their
 * failure handling can't drift apart.
 */
function runProbe(launch, parse, opts) {
  const o = opts || {};
  const platform = o._platform || process.platform;
  if (platform !== "win32") return Promise.resolve("unknown");
  const spawn = o._spawn || cp.spawn;
  const { file, spawnArgs } = launch;
  return new Promise((resolve) => {
    let out = "", done = false, child = null;
    const finish = (v) => { if (done) return; done = true; clearTimeout(timer); resolve(v); };
    const timer = setTimeout(() => { try { child && child.kill(); } catch (_) {} finish("unknown"); }, o.timeoutMs || 15000);
    try {
      child = spawn(file, spawnArgs, { windowsHide: true });
    } catch (_) {
      return finish("unknown");
    }
    if (child.stdout) child.stdout.on("data", (d) => { if (out.length < MAX_OUT) out += d.toString(); });
    child.on("error", () => finish("unknown"));
    child.on("close", () => finish(parse(out)));
  });
}

/**
 * Run the host `Get-VM` probe and resolve a coarse state string
 * ('running'|'off'|'absent'|'unknown'). Never rejects. Off-Windows (or a spawn
 * failure / timeout) resolves 'unknown'.
 */
function queryVmState(instance, opts) {
  return runProbe(buildStateProbeLaunch(vmNameOf(instance)), parseVmState, opts);
}

/** Read the VM's current automatic-checkpoint policy. Never rejects; see runProbe. */
function queryAutoCheckpoints(instance, opts) {
  return runProbe(buildAutoCheckpointProbeLaunch(vmNameOf(instance)), parseAutoCheckpoints, opts);
}

/**
 * argv that opens an ELEVATED host console running `commandText` (UAC via
 * Start-Process -Verb RunAs). The child is `-Command <text>` (not -File): the
 * inner argv is canonically quoted (winQuoteArg) and forwarded as a single-string
 * -ArgumentList so a VM name with a space/quote survives. Pure; mirrors
 * lifecycle.buildHostLaunch but for an inline command instead of a script file.
 *
 * `-WindowStyle Normal` is explicit so the elevated child gets a VISIBLE console
 * (same rationale as lifecycle.buildOuterCommand): the launcher is spawned DETACHED
 * with no console of its own, so without this the inner powershell can inherit "no
 * console" and run windowless — the "toast fires, nothing happens" symptom. It
 * coexists with -Verb RunAs.
 *
 * NO -NoExit by default: the child runs its inline command and EXITS (closing the
 * console) instead of dropping to an interactive prompt — the reported "leaves an
 * interactive PowerShell window" bug. `opts.keepOpen` (debug) adds it back so errors
 * stay readable. (The command itself can still pause on FAILURE — see buildStartCommand.)
 */
function buildElevatedCommandLaunch(commandText, opts = {}) {
  const childArgv = ["-NoProfile", "-ExecutionPolicy", "Bypass"];
  if (opts.keepOpen) childArgv.push("-NoExit");
  childArgv.push("-Command", commandText);
  const childLine = childArgv.map(lifecycle.winQuoteArg).join(" ");
  const command = `Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Normal -ArgumentList ${lifecycle.psSingleQuote(childLine)}`;
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  const psArgs = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded];
  // Launch through `cmd /c start` (same reason as lifecycle.buildHostLaunch): from
  // VS Code's console-less extension host a plain powershell spawn gets no console, so
  // its Start-Process -Verb RunAs opens no visible/UAC window. `start` forces a new
  // console. Only argv-safe tokens (flags + base64) pass through cmd.
  return {
    file: "cmd.exe",
    spawnArgs: ["/c", "start", "", "powershell.exe", ...psArgs],
    command,
  };
}

/** The elevated child command that starts the VM and reports the result. On SUCCESS the
 *  console closes (no -NoExit; the extension polls reachability + opens the VM, so that's
 *  the real feedback). On FAILURE it PAUSES so the error is readable before closing. Pure. */
function buildStartCommand(vmName) {
  const n = lifecycle.psSingleQuote(vmName || VM_NAME);
  return (
    "Start-VM -Name " + n + "; " +
    "if ($?) { Write-Host 'Construct VM started.' -ForegroundColor Green } " +
    "else { Write-Host 'Failed to start the Construct VM.' -ForegroundColor Red; " +
    "if (-not [Console]::IsInputRedirected) { [void](Read-Host 'Press Enter to close') } }"
  );
}

/**
 * Launch the elevated Start-VM in a new host console (UAC prompt). Fire-and-forget
 * like the lifecycle scripts; the caller polls SSH reachability and opens the VM
 * once it answers. Guards off-Windows. Returns true if spawned.
 */
function startVm(instance, opts = {}) {
  const vscode = vsc();
  if ((opts._platform || process.platform) !== "win32") {
    vscode.window.showWarningMessage("Starting the Construct VM runs on the Windows host, which isn't available here.");
    return false;
  }
  const { file, spawnArgs } = buildElevatedCommandLaunch(buildStartCommand(vmNameOf(instance)), { keepOpen: !!opts.debug });
  try {
    // No windowsHide: it sets CREATE_NO_WINDOW on this launcher, which suppresses the
    // console the inner Start-Process opens (same bug the lifecycle buttons had).
    // `detached` gives the launcher no console of its own and lets the UAC console
    // outlive VS Code.
    const child = (opts._spawn || cp.spawn)(file, spawnArgs, { detached: true, stdio: "ignore" });
    child.on("error", (e) => vscode.window.showErrorMessage(`Couldn't start the VM: ${e.message}`));
    child.unref();
    return true;
  } catch (e) {
    vscode.window.showErrorMessage(`Couldn't start the VM: ${e && e.message ? e.message : e}`);
    return false;
  }
}

module.exports = {
  backend: "hyperv-local",
  capabilities: CAPABILITIES,
  VM_NAME,
  buildStateProbeCommand, buildStateProbeLaunch, parseVmState, queryVmState,
  buildAutoCheckpointProbeCommand, buildAutoCheckpointProbeLaunch, parseAutoCheckpoints,
  queryAutoCheckpoints,
  buildElevatedCommandLaunch, buildStartCommand, startVm,
};
