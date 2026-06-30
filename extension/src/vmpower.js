"use strict";
// Hyper-V power control for the Construct VM, from the control panel.
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
// The Shutdown action is just `poweroff` over SSH (the VM user is root), so it
// lives as a constant here and is run through src/ssh.js by the extension.
//
// `vscode` is lazy-required so the pure builders unit-test under plain node. The
// quoting helpers are reused from lifecycle.js (same canonical Windows rules).

const cp = require("child_process");
const lifecycle = require("./lifecycle");

function vsc() { return require("vscode"); }

// The Hyper-V VM name Auto-Install.ps1 creates ($HyperVmName).
const VM_NAME = "Agent-VM";

// `systemctl poweroff --no-block` asks PID 1 to shut down and returns immediately
// (without waiting for the shutdown to finish), so the SSH call gets a clean exit
// code before the box goes down rather than having the connection torn out from
// under it.
const SHUTDOWN_CMD = "systemctl poweroff --no-block";

// Cap captured probe output; the probe prints one short line, so this only guards
// against a wedged/garbage powershell flooding host memory.
const MAX_OUT = 64 * 1024;

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
 * Run the host `Get-VM` probe and resolve a coarse state string
 * ('running'|'off'|'absent'|'unknown'). Never rejects. Off-Windows (or a spawn
 * failure / timeout) resolves 'unknown'. `opts._spawn`/`opts._platform` are test
 * seams (default child_process.spawn / process.platform).
 */
function queryVmState(opts = {}) {
  const platform = opts._platform || process.platform;
  if (platform !== "win32") return Promise.resolve("unknown");
  const spawn = opts._spawn || cp.spawn;
  const { file, spawnArgs } = buildStateProbeLaunch(opts.vmName);
  return new Promise((resolve) => {
    let out = "", done = false, child = null;
    const finish = (v) => { if (done) return; done = true; clearTimeout(timer); resolve(v); };
    const timer = setTimeout(() => { try { child && child.kill(); } catch (_) {} finish("unknown"); }, opts.timeoutMs || 15000);
    try {
      child = spawn(file, spawnArgs, { windowsHide: true });
    } catch (_) {
      return finish("unknown");
    }
    if (child.stdout) child.stdout.on("data", (d) => { if (out.length < MAX_OUT) out += d.toString(); });
    child.on("error", () => finish("unknown"));
    child.on("close", () => finish(parseVmState(out)));
  });
}

/**
 * argv that opens an ELEVATED host console running `commandText` (UAC via
 * Start-Process -Verb RunAs). The child is `-Command <text>` (not -File): the
 * inner argv is canonically quoted (winQuoteArg) and forwarded as a single-string
 * -ArgumentList so a VM name with a space/quote survives. Pure; mirrors
 * lifecycle.buildHostLaunch but for an inline command instead of a script file.
 */
function buildElevatedCommandLaunch(commandText) {
  const childArgv = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-Command", commandText];
  const childLine = childArgv.map(lifecycle.winQuoteArg).join(" ");
  const command = `Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ${lifecycle.psSingleQuote(childLine)}`;
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  return {
    file: "powershell.exe",
    spawnArgs: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
    command,
  };
}

/** The elevated child command that starts the VM and reports the result. Pure. */
function buildStartCommand(vmName) {
  const n = lifecycle.psSingleQuote(vmName || VM_NAME);
  return (
    "Start-VM -Name " + n + "; " +
    "if ($?) { Write-Host 'Construct VM started.' -ForegroundColor Green } " +
    "else { Write-Host 'Failed to start the Construct VM.' -ForegroundColor Red }"
  );
}

/**
 * Launch the elevated Start-VM in a new host console (UAC prompt). Fire-and-forget
 * like the lifecycle scripts; the caller polls SSH reachability and opens the VM
 * once it answers. Guards off-Windows. Returns true if spawned.
 */
function startVm(opts = {}) {
  const vscode = vsc();
  if (process.platform !== "win32") {
    vscode.window.showWarningMessage("Starting the Construct VM runs on the Windows host, which isn't available here.");
    return false;
  }
  const { file, spawnArgs } = buildElevatedCommandLaunch(buildStartCommand(opts.vmName));
  try {
    const child = cp.spawn(file, spawnArgs, { windowsHide: true, detached: true, stdio: "ignore" });
    child.on("error", (e) => vscode.window.showErrorMessage(`Couldn't start the VM: ${e.message}`));
    child.unref();
    return true;
  } catch (e) {
    vscode.window.showErrorMessage(`Couldn't start the VM: ${e && e.message ? e.message : e}`);
    return false;
  }
}

module.exports = {
  VM_NAME, SHUTDOWN_CMD,
  buildStateProbeCommand, buildStateProbeLaunch, parseVmState, queryVmState,
  buildElevatedCommandLaunch, buildStartCommand, startVm,
};
