"use strict";
// Launch the host-side PowerShell lifecycle scripts (reprovision / export /
// reinstall / redownload) from the control panel.
//
// WHERE THIS RUNS — this is a UI extension (extensionKind: "ui"), so its Node
// code runs on the user's LOCAL Windows host even when the VS Code window is
// attached to the VM over Remote-SSH. `vscode.window.createTerminal()` does NOT:
// a terminal runs in the window's context, which is the VM (Linux) when remote,
// where powershell.exe doesn't exist. So we launch via child_process (always
// local), opening a NEW visible console window on the host, detached so it
// outlives VS Code.
//
// UAC — we do not elevate the extension host. Reprovision/Export touch no
// Hyper-V and run non-elevated. Reinstall/Redownload delete + recreate the VM,
// so they are launched with `Start-Process -Verb RunAs`, raising the UAC consent
// prompt; one elevated console then does the work. (Auto-Install.ps1 also
// self-elevates as a fallback, so manual runs still work.)
//
// `vscode` is required lazily inside the impure functions so the pure builders
// (buildInvocation/buildHostLaunch) can be unit-tested under plain node.

const cp = require("child_process");
const fs = require("fs");
const path = require("path");
const host = require("./host");

function vsc() { return require("vscode"); }

const PROVISION = "Provision-AgentVM.ps1";   // reprovision + export (no admin)
const AUTO_INSTALL = "Auto-Install.ps1";     // reinstall + redownload (self/explicitly elevated)
const CHECKPOINTS = "Set-AgentVmCheckpoints.ps1"; // apply the checkpoint policy to the LIVE VM (elevated)
const BACKUP_DIR_NAME = ".construct-backup"; // mirrors Get-ConstructBackupDir

/** Coerce a backup-mode to the validated set Auto-Install.ps1 accepts. The plain
 *  Reinstall/Redownload buttons (and any unknown value) default to save&restore. */
function normalizeBackupMode(bm) {
  return (bm === "existing" || bm === "wipe") ? bm : "save";
}

/**
 * Build the script + PowerShell args for an action from the settings form shape.
 * Pure. `opts`: { settings, backupDir, backupMode }.
 *   reprovision  -> Provision-AgentVM.ps1 -Action provision  (keeps data, no admin)
 *   exportConfig -> Provision-AgentVM.ps1 -Action export -BackupDir <dir>
 *   reinstall    -> Auto-Install.ps1 -Action reinstall  -BackupMode <mode>
 *   redownload   -> Auto-Install.ps1 -Action redownload -BackupMode <mode>
 * Returns { script, args, destructive, elevate, label } or null for an unknown action.
 *
 * The agent password is deliberately NOT collected, stored, or passed (it would be
 * visible on the process command line and is only a manual-fallback login — normal
 * access is as root over the pre-seeded SSH key). Launched from the panel
 * (-FromPanel), Auto-Install.ps1 keeps the seeded default 'agent' without prompting.
 * Project selection is likewise left to the script's own selector until the Projects
 * batch wires it.
 */
function buildInvocation(action, opts = {}) {
  const s = opts.settings || {};
  const args = [];
  // Panel-launched scripts skip their end-of-run "Press Enter" pause: the dashboard
  // shows the result (and auto-refreshes), so the console just closes when done. In
  // debug the launcher still keeps it open via -NoExit. A direct PowerShell run (no
  // -FromPanel) keeps the pause so the window stays readable. Passed as a param (not
  // an env var) so it survives the UAC boundary for the elevated reinstall/redownload.
  args.push("-FromPanel");
  const pushPair = (flag, val) => { if (val != null && String(val).trim() !== "") args.push(flag, String(val)); };
  const pushBool = (flag, val) => { if (typeof val === "boolean") args.push(flag, val ? "true" : "false"); };
  // The control panel's project selection (persisted `projects`), so the script uses
  // it instead of re-prompting in the console. Only when a selection exists — with none
  // persisted, let the script keep its own default/prompt rather than force "default".
  const pushProjects = () => {
    const p = Array.isArray(opts.projects) ? opts.projects.filter(Boolean) : [];
    if (p.length) args.push("-Projects", p.join(","));
  };

  switch (action) {
    case "reprovision":
      args.push("-Action", "provision");
      pushProjects();
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      pushBool("-VsCodeServeWeb", s.serveWeb);
      pushBool("-VsCodeTunnel", s.tunnel);
      pushBool("-SmbShare", s.smb);
      pushBool("-ClaudePartialStreaming", s.partialStreaming);
      pushBool("-MicPassthrough", s.mic);
      pushBool("-T3Code", s.t3code);
      if (opts.supportsT3CodeChannel !== false) pushPair("-T3CodeChannel", s.t3codeChannel);
      // Launched from the panel: don't prompt for the SMB drive letter etc. (still pauses
      // at the end so output is readable — -NonInteractive is NOT -Auto).
      args.push("-NonInteractive");
      return { script: PROVISION, args, destructive: false, elevate: false, label: "Reprovision" };

    case "exportConfig":
      args.push("-Action", "export", "-BackupDir", opts.backupDir);
      return { script: PROVISION, args, destructive: false, elevate: false, label: "Export config" };

    case "reinstall":
    case "redownload": {
      args.push("-Action", action, "-BackupMode", normalizeBackupMode(opts.backupMode));
      pushProjects(); // Auto-Install forwards -Projects to Provision (-Auto gates its prompts)
      pushPair("-VmMemoryGB", s.ram);
      pushPair("-VmDiskGB", s.disk);
      // Hyper-V automatic checkpoints are decided when the VM is CREATED, which only
      // a rebuild does — so the preference rides reinstall/redownload, not reprovision
      // (which never touches Hyper-V). An existing VM is changed by "setCheckpoints".
      //
      // Capability-gated: Auto-Install.ps1 is an advanced function, so an install whose
      // scripts predate this parameter FAILS TO BIND and the rebuild never starts. A
      // newer extension against an older scripts dir must therefore drop the flag and
      // let the script's own default stand, not break Reinstall outright.
      if (opts.supportsCheckpoints !== false) pushBool("-AutomaticCheckpoints", s.autoCheckpoints);
      if (action === "redownload") pushPair("-UbuntuRelease", s.ubuntu);
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      // A destructive rebuild provisions a fresh VM, so carry the saved streaming
      // preference through Auto-Install -> Create-AgentVM -> Provision (the panel's
      // "…with these settings" buttons must honour an explicit off, not silently
      // fall back to the provisioner's default-on).
      pushBool("-ClaudePartialStreaming", s.partialStreaming);
      pushBool("-MicPassthrough", s.mic);
      pushBool("-T3Code", s.t3code);
      if (opts.supportsT3CodeChannel !== false) pushPair("-T3CodeChannel", s.t3codeChannel);
      return {
        script: AUTO_INSTALL, args, destructive: true, elevate: true,
        label: action === "redownload" ? "Redownload" : "Reinstall",
      };
    }

    // Apply the automatic-checkpoint policy to the EXISTING VM, right now. Hyper-V
    // cmdlets need admin, so this elevates (UAC) like reinstall/redownload — but it
    // isn't `destructive` in the confirm-modal sense: the extension has already asked
    // whether to apply now, and the script itself confirms before removing any
    // checkpoint it can't positively identify as automatic.
    case "setCheckpoints": {
      // STRICT boolean. This action deletes checkpoints when it runs with -Enabled false,
      // so a malformed request (missing field, the STRING "true") must be refused rather
      // than defaulted — defaulting would silently pick the destructive direction.
      if (typeof opts.enabled !== "boolean") return null;
      const enabled = opts.enabled;
      args.push("-Enabled", enabled ? "true" : "false");
      return {
        script: CHECKPOINTS, args, destructive: false, elevate: true,
        label: enabled ? "Enable automatic checkpoints" : "Disable automatic checkpoints",
      };
    }

    default:
      return null;
  }
}

/**
 * Do the host scripts in `scriptsDir` understand `-AutomaticCheckpoints`? Read the
 * ANSWER out of Auto-Install.ps1 itself rather than inferring it from a sibling file's
 * existence: a hand-assembled or partially-updated scripts dir can hold the newer
 * Set-AgentVmCheckpoints.ps1 next to an older Auto-Install.ps1, and passing the flag to
 * an advanced function that lacks the parameter is a BINDING failure — the rebuild would
 * never start. Unreadable/absent → false (drop the flag; the script's own default stands).
 */
function scriptSupportsCheckpoints(scriptsDir) {
  if (!scriptsDir) return false;
  let txt;
  try { txt = fs.readFileSync(path.join(scriptsDir, AUTO_INSTALL), "utf8"); } catch (_) { return false; }
  // Match a real parameter DECLARATION, not any mention of the name: `[string]$Foo` /
  // `[string]$Foo = "x"` / `$Foo,`. A bare name test would be satisfied by a comment (or
  // by our own doc text) on a script that has no such parameter, and passing the flag to
  // one is a binding failure. Comments are stripped first so even `# $Foo = ...` can't
  // pass; PowerShell identifiers are case-INSENSITIVE, so is this.
  const code = txt
    .replace(/<#[\s\S]*?#>/g, "")   // block comments (the .SYNOPSIS help header)
    .replace(/^[ \t]*#.*$/gm, "");  // whole-line comments
  return /\$AutomaticCheckpoints\s*(?:=|,|\)|$)/im.test(code);
}

/**
 * Same gate as scriptSupportsCheckpoints but for `-T3CodeChannel`. An older scripts
 * dir that predates this parameter rejects the unknown flag at binding time, breaking
 * the lifecycle action outright. Unlike checkpoints (which only rides reinstall/
 * redownload → Auto-Install.ps1), the channel flag is sent on BOTH paths:
 *   reprovision   → Provision-AgentVM.ps1
 *   reinstall/redownload → Auto-Install.ps1 → Create-AgentVM.ps1 → Provision
 *
 * Action-appropriate: reprovision invokes only Provision-AgentVM.ps1, so only that
 * script needs the parameter. Rebuild (reinstall/redownload) invokes Auto-Install,
 * which has its own version-skew guard for the Create and Provision splats — checking
 * Auto-Install alone suffices. Without an action, require BOTH (conservative default
 * for callers that don't specify one).
 */
function scriptSupportsT3CodeChannel(scriptsDir, action) {
  if (!scriptsDir) return false;
  const re = /\$T3CodeChannel\s*(?:=|,|\)|$)/im;
  const check = (file) => {
    let txt;
    try { txt = fs.readFileSync(path.join(scriptsDir, file), "utf8"); } catch (_) { return false; }
    const code = txt.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
    return re.test(code);
  };
  if (action === "reprovision") return check(PROVISION);
  if (action === "reinstall" || action === "redownload") return check(AUTO_INSTALL);
  return check(PROVISION) && check(AUTO_INSTALL);
}

/** A PowerShell single-quoted string literal (embedded quotes doubled). */
function psSingleQuote(s) { return "'" + String(s).replace(/'/g, "''") + "'"; }

/**
 * Canonical Windows command-line quoting (CommandLineToArgvW rules), so an argv
 * element survives parsing by the child powershell.exe. Adapted from Microsoft's
 * "Everyone quotes command line arguments the wrong way": quote only when needed,
 * double the run of backslashes that precedes a `"` (or the closing quote), and
 * escape embedded `"` as `\"`.
 */
function winQuoteArg(arg) {
  arg = String(arg);
  if (arg !== "" && !/[ \t\n\v"]/.test(arg)) return arg;
  let out = '"';
  let bs = 0;
  for (let i = 0; i < arg.length; i++) {
    const c = arg[i];
    if (c === "\\") { bs++; continue; }
    if (c === '"') { out += "\\".repeat(bs * 2 + 1) + '"'; bs = 0; continue; }
    if (bs) { out += "\\".repeat(bs); bs = 0; }
    out += c;
  }
  return out + "\\".repeat(bs * 2) + '"';
}

/** The child powershell.exe command line: its argv, each canonically quoted.
 *  NO -NoExit: the scripts pause at the end themselves ("Press Enter to exit", on
 *  success OR error via try/finally), so the window stays readable — and WITHOUT
 *  -NoExit it then CLOSES on that Enter instead of dropping to an interactive
 *  PowerShell prompt (the reported "returns to a PowerShell thing" annoyance). */
function buildChildCommandLine(scriptPath, args, opts = {}) {
  const argv = ["-NoProfile", "-ExecutionPolicy", "Bypass"];
  if (opts.keepOpen) argv.push("-NoExit"); // debug: keep the (elevated) console open so errors stay readable
  argv.push("-File", scriptPath, ...args);
  return argv.map(winQuoteArg).join(" ");
}

/**
 * The outer PowerShell command that opens the host console. -ArgumentList is a
 * SINGLE pre-quoted STRING, not an array: Start-Process forwards a single-string
 * ArgumentList to the child verbatim, whereas an ARRAY is space-joined WITHOUT
 * re-quoting (so a spaced script path or a two-word -GitUserName would be split
 * apart). The string is embedded in a single-quoted PS literal (only `'` doubled).
 *
 * `-WindowStyle Normal` is explicit so the child gets a VISIBLE console. The
 * launcher is spawned DETACHED (no console of its own), so without this the inner
 * powershell can inherit "no console" and run windowless — the exact "toast fires,
 * no window, nothing happens" symptom. `-WindowStyle` coexists with `-Verb RunAs`.
 */
function buildOuterCommand(childCommandLine, opts = {}) {
  const verb = opts.elevate ? " -Verb RunAs" : "";
  return `Start-Process -FilePath 'powershell.exe'${verb} -WindowStyle Normal -ArgumentList ${psSingleQuote(childCommandLine)}`;
}

/**
 * The PowerShell call-operator invocation `& '<script>' <args>` used for the
 * NON-elevated single-console launch: the script runs directly in the console `start`
 * allocates (no inner Start-Process → no second window). Parameter NAMES stay bare and
 * VALUES are single-quoted; our values never start with '-', so the /^-/ test cleanly
 * splits names from values. All target params are [string]/[int]/[switch], so a quoted
 * string value binds (incl. [int] coercion, verified) and a bare -Switch sets it. Pure.
 */
function buildCallCommand(scriptPath, args) {
  const toks = (args || []).map((a) => /^-/.test(String(a)) ? String(a) : psSingleQuote(a));
  return "& " + psSingleQuote(scriptPath) + (toks.length ? " " + toks.join(" ") : "");
}

/**
 * Build the child_process invocation that opens a new host console running the
 * script. Pure (returns the argv; the caller spawns it).
 *
 * WHY `cmd /c start`: VS Code's extension host is a GUI process with NO console.
 * A powershell.exe spawned from it gets no console either (there's none to inherit,
 * and Node's child_process can't request CREATE_NEW_CONSOLE — `detached` sets the
 * OPPOSITE, DETACHED_PROCESS). A console-less launcher's `Start-Process` then opens
 * NO visible window — the "toast fires, no window, nothing happens" bug that removing
 * windowsHide alone did NOT fix (detached still suppressed the console). `start` is
 * the reliable Win32 primitive that forces a NEW CONSOLE for its target.
 *
 * ELEVATED (reinstall/redownload): the console runs `Start-Process -Verb RunAs …` to
 * raise the UAC prompt + open the elevated console (so there's a brief launcher window
 * + the elevated one — unavoidable for UAC). NON-ELEVATED (reprovision/export/update):
 * the console runs the script DIRECTLY via `& '<script>' <args>` — ONE window, no inner
 * Start-Process (that second window was the reported "two popups"). Only argv-safe
 * tokens (fixed powershell flags + the base64 blob) pass through cmd — no paths/user
 * values — so `start` adds no quoting surface; the empty "" is start's title slot.
 * `command` is the decoded inner command (for tests).
 */
function buildHostLaunch(scriptPath, args, opts = {}) {
  const elevate = !!opts.elevate;
  const keepOpen = !!opts.keepOpen; // debug: keep the console open on exit (errors stay readable)
  const command = elevate
    ? buildOuterCommand(buildChildCommandLine(scriptPath, args, { keepOpen }), opts) // -NoExit rides the elevated child
    : buildCallCommand(scriptPath, args);                                             // & 'script' … (this console)
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  // -NonInteractive only for the elevated launcher (it just fires Start-Process). The
  // non-elevated console RUNS the script here, so it must stay interactive for the
  // script's end-of-run "Press Enter to close" pause (and any in-console confirmation).
  const base = elevate
    ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"]
    : ["-NoProfile", "-ExecutionPolicy", "Bypass"];
  // Non-elevated debug: -NoExit on THIS console (it runs the script). Elevated debug keeps
  // its console open via the child's -NoExit (above), not the transient launcher.
  if (keepOpen && !elevate) base.push("-NoExit");
  const psArgs = [...base, "-EncodedCommand", encoded];
  return {
    file: "cmd.exe",
    spawnArgs: ["/c", "start", "", "powershell.exe", ...psArgs],
    command,
  };
}

// Optional logger + debug-flag getter the extension wires in via configure(); lets the
// pure builders stay dependency-free while launchHostScript reports what it launched.
let _log = null;
let _isDebug = null;
/** Wire in a logger (fn(msg)) and a debug-flag getter (fn()->bool). Both optional. */
function configure(opts = {}) {
  if (opts && typeof opts.log === "function") _log = opts.log;
  if (opts && typeof opts.isDebug === "function") _isDebug = opts.isDebug;
}

/** Modal confirm for a destructive (VM-deleting) action. Resolves true to go. */
async function confirmDestructive(inv) {
  const vscode = vsc();
  const detail = inv.label === "Redownload"
    ? "This DELETES the VM and its virtual disk, re-downloads the Ubuntu ISO, then rebuilds and reinstalls from scratch."
    : "This DELETES the VM and its virtual disk, then rebuilds and reinstalls from the current ISO.";
  const pick = await vscode.window.showWarningMessage(
    `${inv.label} the Construct VM?`,
    { modal: true, detail: detail + " This is irreversible and cannot be undone." },
    inv.label
  );
  return pick === inv.label;
}

// Spawn options for the `cmd /c start` launcher. No windowsHide: it would set
// CREATE_NO_WINDOW on cmd, which could suppress the console `start` allocates.
// `detached` + unref let cmd (which exits the moment `start` fires) not tie to VS
// Code; the started console is its own process and outlives VS Code regardless.
// `extraEnv` (optional) is merged over the inherited environment and reaches the
// launched console (and, when elevated, the Start-Process child) — used to pass a
// result-file path to the script without adding a parameter old scripts would reject.
// Exposed for the regression test that pins "no windowsHide".
function hostLaunchSpawnOptions(cwd, extraEnv) {
  const o = { cwd, detached: true, stdio: "ignore" };
  if (extraEnv && typeof extraEnv === "object") o.env = { ...process.env, ...extraEnv };
  return o;
}

/**
 * Spawn a host console running <scriptsDir>/<script> with the given args, opening
 * a new (optionally elevated) window. Shared by the lifecycle actions and the
 * Construct update refresh. Guards off-Windows. `opts`:
 * { scriptsDir, script, args, elevate, label, env? }. `env` is merged into the launched
 * process environment (reaches the script). `opts._spawn`/`_vscode`/`_platform` are test
 * seams (default child_process.spawn / the real vscode / process.platform). Returns true
 * if spawned.
 */
function launchHostScript(opts) {
  const vscode = opts._vscode || vsc();
  const spawn = opts._spawn || cp.spawn;
  const platform = opts._platform || process.platform;
  const log = opts.log || _log || (() => {});
  const debug = typeof opts.debug === "boolean" ? opts.debug : (_isDebug ? !!_isDebug() : false);
  if (platform !== "win32") {
    log(`launch ${opts.label}: skipped — not on Windows (platform=${platform})`);
    vscode.window.showWarningMessage("Construct actions run on the Windows host, which isn't available here.");
    return false;
  }
  const scriptPath = path.join(opts.scriptsDir, opts.script);
  const { file, spawnArgs, command } = buildHostLaunch(scriptPath, opts.args || [], { elevate: !!opts.elevate, keepOpen: debug });
  // Deterministic record of exactly WHAT we launch (reveals version skew / wrong paths /
  // bad args). The decoded command shows the real script path + args reaching powershell.
  log(`launch ${opts.label}: elevate=${!!opts.elevate} debug=${debug} script=${scriptPath}`);
  log(`  command: ${command}`);
  if (opts.env) log(`  env: ${Object.keys(opts.env).join(", ")}`);
  try {
    const child = spawn(file, spawnArgs, hostLaunchSpawnOptions(opts.scriptsDir, opts.env));
    child.on("error", (e) => { log(`launch ${opts.label}: spawn error — ${e.message}`); vscode.window.showErrorMessage(`Couldn't launch ${opts.label}: ${e.message}`); });
    child.unref();
    log(`launch ${opts.label}: spawned (${file})`);
    vscode.window.showInformationMessage(
      `${opts.label} launched in a console window on the host${opts.elevate ? " — approve the UAC prompt." : "."}`
    );
    return true;
  } catch (e) {
    log(`launch ${opts.label}: threw — ${e && e.message ? e.message : e}`);
    vscode.window.showErrorMessage(`Couldn't launch ${opts.label}: ${e && e.message ? e.message : e}`);
    return false;
  }
}

/**
 * Run a lifecycle action. `opts`: { scriptsDir, backupMode?, projects?, enabled?, env? }.
 * scriptsDir must be pre-resolved by the caller (it owns the construct.scriptsDir
 * setting); `enabled` is the setCheckpoints on/off. The destructive actions confirm
 * first; everything launches a new host console.
 */
function run(action, opts = {}) {
  const vscode = vsc();
  if (process.platform !== "win32") {
    vscode.window.showWarningMessage("Construct lifecycle actions run on the Windows host, which isn't available here.");
    return;
  }
  const scriptsDir = opts.scriptsDir;
  if (!scriptsDir) return; // caller warns when it can't resolve the scripts dir
  // Prefer the caller-supplied selection (the extension computes the EFFECTIVE set —
  // saved selection, else the VM's current projects); fall back to the saved selection.
  let projects = Array.isArray(opts.projects) ? opts.projects : null;
  if (!projects) { try { projects = host.readSelectedProjects(scriptsDir); } catch (_) { projects = []; } }
  const inv = buildInvocation(action, {
    settings: host.readSettings(scriptsDir),
    backupDir: path.join(scriptsDir, BACKUP_DIR_NAME),
    backupMode: opts.backupMode,
    projects,
    enabled: opts.enabled,
    supportsCheckpoints: scriptSupportsCheckpoints(scriptsDir),
    supportsT3CodeChannel: scriptSupportsT3CodeChannel(scriptsDir, action),
  });
  if (!inv) return;
  // Honesty gate: when the scripts are too old to take -AutomaticCheckpoints we drop the
  // flag (see buildInvocation) — but an old Create-AgentVM.ps1 hardcodes automatic
  // checkpoints ON, so silently rebuilding would produce the OPPOSITE of the saved
  // preference. Say so before the rebuild rather than after.
  if (inv.destructive && !inv.args.includes("-AutomaticCheckpoints")) {
    // ABSENT counts as "wants off": the panel's toggle defaults to off and that IS the
    // product default, so a user who never touched it still expects a rebuilt VM to have
    // checkpoints disabled. Only an explicit `true` is unaffected by an old script.
    let wantsOff = true;
    try { wantsOff = host.readSettings(scriptsDir).autoCheckpoints !== true; } catch (_) {}
    if (wantsOff && !scriptSupportsCheckpoints(scriptsDir)) {
      vscode.window.showWarningMessage(
        "These host scripts are too old to honour the “Automatic checkpoints: off” setting, so the rebuilt VM will have Hyper-V's automatic checkpoints ON. Update Construct first to avoid that."
      );
    }
  }
  Promise.resolve(inv.destructive ? confirmDestructive(inv) : true).then((ok) => {
    if (!ok) return;
    // A rebuild REPLACES the VM, so what was last confirmed onto the old one says nothing
    // about the new one. Clearing the marker keeps the (permission-gated) apply-offer
    // honest: a stale "already applied off" must not suppress the offer for a fresh VM
    // that an old script just created with checkpoints on.
    if (inv.destructive) {
      try { host.saveAppliedAutoCheckpoints(scriptsDir, null); } catch (_) { /* best-effort */ }
    }
    launchHostScript({ scriptsDir, script: inv.script, args: inv.args, elevate: inv.elevate, label: inv.label, env: opts.env });
  });
}

module.exports = {
  PROVISION, AUTO_INSTALL, CHECKPOINTS, BACKUP_DIR_NAME,
  normalizeBackupMode, buildInvocation, scriptSupportsCheckpoints, scriptSupportsT3CodeChannel,
  psSingleQuote, winQuoteArg, buildChildCommandLine, buildOuterCommand, buildCallCommand, buildHostLaunch,
  hostLaunchSpawnOptions, launchHostScript, run, configure,
};
