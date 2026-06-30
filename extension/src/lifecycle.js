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
const path = require("path");
const host = require("./host");

function vsc() { return require("vscode"); }

const PROVISION = "Provision-AgentVM.ps1";   // reprovision + export (no admin)
const AUTO_INSTALL = "Auto-Install.ps1";     // reinstall + redownload (self/explicitly elevated)
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
 * The agent password is deliberately NOT passed (it would be visible on the
 * process command line); Auto-Install.ps1 prompts for it in the console, which
 * keeps "pass it at reinstall time" without exposing it. Project selection is
 * likewise left to the script's own selector until the Projects batch wires it.
 */
function buildInvocation(action, opts = {}) {
  const s = opts.settings || {};
  const args = [];
  const pushPair = (flag, val) => { if (val != null && String(val).trim() !== "") args.push(flag, String(val)); };
  const pushBool = (flag, val) => { if (typeof val === "boolean") args.push(flag, val ? "true" : "false"); };

  switch (action) {
    case "reprovision":
      args.push("-Action", "provision");
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      pushBool("-VsCodeServeWeb", s.serveWeb);
      pushBool("-VsCodeTunnel", s.tunnel);
      pushBool("-SmbShare", s.smb);
      return { script: PROVISION, args, destructive: false, elevate: false, label: "Reprovision" };

    case "exportConfig":
      args.push("-Action", "export", "-BackupDir", opts.backupDir);
      return { script: PROVISION, args, destructive: false, elevate: false, label: "Export config" };

    case "reinstall":
    case "redownload": {
      args.push("-Action", action, "-BackupMode", normalizeBackupMode(opts.backupMode));
      pushPair("-VmMemoryGB", s.ram);
      pushPair("-VmDiskGB", s.disk);
      if (action === "redownload") pushPair("-UbuntuRelease", s.ubuntu);
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      return {
        script: AUTO_INSTALL, args, destructive: true, elevate: true,
        label: action === "redownload" ? "Redownload" : "Reinstall",
      };
    }

    default:
      return null;
  }
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

/** The child powershell.exe command line: its argv, each canonically quoted. */
function buildChildCommandLine(scriptPath, args) {
  const argv = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", scriptPath, ...args];
  return argv.map(winQuoteArg).join(" ");
}

/**
 * The outer PowerShell command that opens the host console. -ArgumentList is a
 * SINGLE pre-quoted STRING, not an array: Start-Process forwards a single-string
 * ArgumentList to the child verbatim, whereas an ARRAY is space-joined WITHOUT
 * re-quoting (so a spaced script path or a two-word -GitUserName would be split
 * apart). The string is embedded in a single-quoted PS literal (only `'` doubled).
 */
function buildOuterCommand(childCommandLine, opts = {}) {
  const verb = opts.elevate ? " -Verb RunAs" : "";
  return `Start-Process -FilePath 'powershell.exe'${verb} -ArgumentList ${psSingleQuote(childCommandLine)}`;
}

/**
 * Build the child_process invocation that opens a new host console running the
 * script. Pure (returns the argv; the caller spawns it). The outer command is
 * passed via -EncodedCommand (base64 UTF-16LE) so there is NO shell-quoting layer
 * between Node and the outer powershell.exe — base64 is argv-safe. The inner argv
 * is quoted by winQuoteArg and forwarded as a single-string -ArgumentList, so
 * spaced/quoted paths and settings values reach the script intact (and can't
 * break out: a -File invocation treats them as data, not commands). `command` is
 * the decoded outer command, exposed for inspection/tests.
 */
function buildHostLaunch(scriptPath, args, opts = {}) {
  const command = buildOuterCommand(buildChildCommandLine(scriptPath, args), opts);
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  return {
    file: "powershell.exe",
    spawnArgs: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
    command,
  };
}

/** Modal confirm for a destructive (VM-deleting) action. Resolves true to go. */
async function confirmDestructive(inv) {
  const vscode = vsc();
  const detail = inv.label === "Redownload"
    ? "This DELETES the VM and its virtual disk, re-downloads the Ubuntu ISO, then rebuilds and reinstalls from scratch."
    : "This DELETES the VM and its virtual disk, then rebuilds and reinstalls from the current ISO.";
  const pick = await vscode.window.showWarningMessage(
    `${inv.label} the Construct VM?`,
    { modal: true, detail: detail + " You'll still confirm the irreversible delete in the elevated console." },
    inv.label
  );
  return pick === inv.label;
}

/** Spawn the host console for an invocation. */
function launch(scriptsDir, inv) {
  const vscode = vsc();
  const scriptPath = path.join(scriptsDir, inv.script);
  const { file, spawnArgs } = buildHostLaunch(scriptPath, inv.args, { elevate: inv.elevate });
  try {
    const child = cp.spawn(file, spawnArgs, { cwd: scriptsDir, windowsHide: true, detached: true, stdio: "ignore" });
    child.on("error", (e) => vscode.window.showErrorMessage(`Couldn't launch ${inv.label}: ${e.message}`));
    child.unref();
    vscode.window.showInformationMessage(
      `${inv.label} launched in a console window on the host${inv.elevate ? " — approve the UAC prompt." : "."}`
    );
  } catch (e) {
    vscode.window.showErrorMessage(`Couldn't launch ${inv.label}: ${e && e.message ? e.message : e}`);
  }
}

/**
 * Run a lifecycle action. `opts`: { scriptsDir, backupMode? }. scriptsDir must be
 * pre-resolved by the caller (it owns the construct.scriptsDir setting). The
 * destructive actions confirm first; everything launches a new host console.
 */
function run(action, opts = {}) {
  const vscode = vsc();
  if (process.platform !== "win32") {
    vscode.window.showWarningMessage("Construct lifecycle actions run on the Windows host, which isn't available here.");
    return;
  }
  const scriptsDir = opts.scriptsDir;
  if (!scriptsDir) return; // caller warns when it can't resolve the scripts dir
  const inv = buildInvocation(action, {
    settings: host.readSettings(scriptsDir),
    backupDir: path.join(scriptsDir, BACKUP_DIR_NAME),
    backupMode: opts.backupMode,
  });
  if (!inv) return;
  Promise.resolve(inv.destructive ? confirmDestructive(inv) : true).then((ok) => {
    if (ok) launch(scriptsDir, inv);
  });
}

module.exports = {
  PROVISION, AUTO_INSTALL, BACKUP_DIR_NAME,
  normalizeBackupMode, buildInvocation,
  psSingleQuote, winQuoteArg, buildChildCommandLine, buildOuterCommand, buildHostLaunch, run,
};
