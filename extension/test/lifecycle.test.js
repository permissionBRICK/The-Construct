"use strict";
// Plain-node unit tests for the lifecycle launcher's PURE builders (buildInvocation
// + buildHostLaunch + normalizeBackupMode). vscode is lazy-required only inside the
// impure run/launch paths, so requiring the module here is safe. No deps.
// Run: node lifecycle.test.js
const life = require("../src/lifecycle");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const has = (arr, ...seq) => {
  for (let i = 0; i + seq.length <= arr.length; i++) {
    if (seq.every((v, j) => arr[i + j] === v)) return true;
  }
  return false;
};

// ── normalizeBackupMode ─────────────────────────────────────────────────────--
ok("backupMode: save passes", life.normalizeBackupMode("save") === "save");
ok("backupMode: existing passes", life.normalizeBackupMode("existing") === "existing");
ok("backupMode: wipe passes", life.normalizeBackupMode("wipe") === "wipe");
ok("backupMode: unknown -> save", life.normalizeBackupMode("xyz") === "save");
ok("backupMode: undefined -> save", life.normalizeBackupMode(undefined) === "save");

// ── reprovision ──────────────────────────────────────────────────────────────
const repro = life.buildInvocation("reprovision", {
  settings: { gitName: "Neo", gitEmail: "neo@zion.io", serveWeb: true, tunnel: false, smb: true },
});
ok("reprovision: uses Provision script", repro.script === life.PROVISION);
ok("reprovision: not destructive, not elevated", repro.destructive === false && repro.elevate === false);
ok("reprovision: -Action provision", has(repro.args, "-Action", "provision"));
ok("reprovision: git identity", has(repro.args, "-GitUserName", "Neo") && has(repro.args, "-GitEmail", "neo@zion.io"));
ok("reprovision: bools as true/false strings", has(repro.args, "-VsCodeServeWeb", "true") && has(repro.args, "-VsCodeTunnel", "false") && has(repro.args, "-SmbShare", "true"));

const reproEmpty = life.buildInvocation("reprovision", { settings: {} });
ok("reprovision: omits unset fields (-FromPanel + -NonInteractive)", reproEmpty.args.join(" ") === "-FromPanel -Action provision -NonInteractive");
// Panel launch is non-interactive: don't re-prompt for the SMB drive letter etc.
ok("reprovision: passes -NonInteractive", repro.args.includes("-NonInteractive"));
// Project selection from the control panel is passed so the console doesn't re-prompt.
const reproProj = life.buildInvocation("reprovision", { settings: {}, projects: ["web", "api"] });
ok("reprovision: passes -Projects from the selection", has(reproProj.args, "-Projects", "web,api"));
ok("reprovision: no -Projects when nothing selected", !reproEmpty.args.includes("-Projects"));

// ── exportConfig ─────────────────────────────────────────────────────────────
const exp = life.buildInvocation("exportConfig", { backupDir: "C:\\T\\.construct-backup" });
ok("export: uses Provision script", exp.script === life.PROVISION);
ok("export: -Action export -BackupDir", has(exp.args, "-Action", "export", "-BackupDir", "C:\\T\\.construct-backup"));
ok("export: not destructive", exp.destructive === false && exp.elevate === false);

// ── reinstall ────────────────────────────────────────────────────────────────
const rei = life.buildInvocation("reinstall", { settings: { ram: "16", disk: "80", gitName: "Neo" } });
ok("reinstall: uses Auto-Install script", rei.script === life.AUTO_INSTALL);
ok("reinstall: destructive + elevated", rei.destructive === true && rei.elevate === true);
ok("reinstall: label", rei.label === "Reinstall");
ok("reinstall: -Action reinstall -BackupMode save (default)", has(rei.args, "-Action", "reinstall", "-BackupMode", "save"));
ok("reinstall: VM resources from settings", has(rei.args, "-VmMemoryGB", "16") && has(rei.args, "-VmDiskGB", "80"));
ok("reinstall: no -UbuntuRelease (reuses ISO)", !rei.args.includes("-UbuntuRelease"));

const reiProj = life.buildInvocation("reinstall", { settings: {}, projects: ["web"] });
ok("reinstall: passes -Projects (Auto-Install forwards it to Provision)", has(reiProj.args, "-Projects", "web"));
const reiWipe = life.buildInvocation("reinstall", { settings: {}, backupMode: "wipe" });
ok("reinstall: honors backupMode wipe", has(reiWipe.args, "-BackupMode", "wipe"));
const reiBad = life.buildInvocation("reinstall", { settings: {}, backupMode: "bogus" });
ok("reinstall: invalid backupMode -> save", has(reiBad.args, "-BackupMode", "save"));

// ── redownload ───────────────────────────────────────────────────────────────
const red = life.buildInvocation("redownload", { settings: { ubuntu: "24.04", ram: "8" }, backupMode: "existing" });
ok("redownload: Auto-Install + label", red.script === life.AUTO_INSTALL && red.label === "Redownload");
ok("redownload: -Action redownload + backupMode", has(red.args, "-Action", "redownload", "-BackupMode", "existing"));
ok("redownload: includes -UbuntuRelease", has(red.args, "-UbuntuRelease", "24.04"));
const redNoRel = life.buildInvocation("redownload", { settings: {} });
ok("redownload: omits -UbuntuRelease when unset", !redNoRel.args.includes("-UbuntuRelease"));

ok("unknown action -> null", life.buildInvocation("bogus", {}) === null);

// ── -FromPanel: every panel launch skips the script's end-of-run pause ───────
ok("reprovision: passes -FromPanel", repro.args.includes("-FromPanel"));
ok("export: passes -FromPanel", exp.args.includes("-FromPanel"));
ok("reinstall: passes -FromPanel", rei.args.includes("-FromPanel"));
ok("redownload: passes -FromPanel", red.args.includes("-FromPanel"));

// ── winQuoteArg (canonical Windows command-line quoting) ─────────────────────
// These exact outputs were validated to parse correctly through real PowerShell
// (Start-Process single-string -ArgumentList -> child argv); see the batch notes.
ok("winQuoteArg: plain word unquoted", life.winQuoteArg("reinstall") === "reinstall");
ok("winQuoteArg: spaces -> double-quoted", life.winQuoteArg("John Smith") === '"John Smith"');
ok("winQuoteArg: empty -> two quotes", life.winQuoteArg("") === '""');
ok("winQuoteArg: embedded quote escaped as backslash-quote", life.winQuoteArg('a"b') === '"a\\"b"');
ok("winQuoteArg: trailing backslash before close doubled", life.winQuoteArg("C:\\Program Files\\") === '"C:\\Program Files\\\\"');

// ── buildChildCommandLine: spaced path / value stay single tokens ────────────
const child = life.buildChildCommandLine("C:\\Program Files\\The-Construct\\Auto-Install.ps1", ["-Action", "reinstall", "-GitUserName", "John Smith"]);
ok("child: -NoProfile -File preamble (NO -NoExit — window closes after the script's own pause)",
  child.startsWith("-NoProfile -ExecutionPolicy Bypass -File ") && !child.includes("-NoExit"));
ok("child: spaced script path is ONE quoted token", child.includes('"C:\\Program Files\\The-Construct\\Auto-Install.ps1"'));
ok("child: spaced value is ONE quoted token", child.includes('-GitUserName "John Smith"'));
ok("child: plain args left unquoted", child.includes("-Action reinstall"));

// ── buildOuterCommand: SINGLE-string -ArgumentList (not an array) ────────────
const outer = life.buildOuterCommand(child, { elevate: true });
ok("outer: elevate adds -Verb RunAs", /^Start-Process -FilePath 'powershell\.exe' -Verb RunAs -WindowStyle Normal -ArgumentList '/.test(outer));
ok("outer: -ArgumentList is one quoted string, not a comma array", !/-ArgumentList '[^']*',/.test(outer));
const outerPlain = life.buildOuterCommand(child, {});
ok("outer: non-elevate omits -Verb RunAs", !/-Verb RunAs/.test(outerPlain));
// Regression: the launcher runs DETACHED (no console of its own); without an
// explicit visible window on the inner Start-Process the child inherits "no
// console" and runs windowless — the "toast fires, no window, nothing happens"
// bug. Pin -WindowStyle Normal on BOTH the elevated and non-elevated commands.
ok("outer: elevate opens a VISIBLE window (-WindowStyle Normal)", outer.includes(" -WindowStyle Normal "));
ok("outer: non-elevate opens a VISIBLE window (-WindowStyle Normal)", outerPlain.includes(" -WindowStyle Normal "));
ok("outer: non-elevate is Start-Process ... -WindowStyle Normal -ArgumentList", /^Start-Process -FilePath 'powershell\.exe' -WindowStyle Normal -ArgumentList '/.test(outerPlain));
const aposOuter = life.buildOuterCommand(life.buildChildCommandLine("C:\\Users\\O'Neil\\Auto-Install.ps1", []), {});
ok("outer: apostrophe in path doubled in the PS literal", aposOuter.includes("O''Neil"));

// ── buildCallCommand: non-elevated single-console `& 'script' args` ──────────
const call = life.buildCallCommand("C:\\x\\Provision-AgentVM.ps1", ["-Action", "provision", "-VmDiskGB", "80", "-NonInteractive"]);
ok("call: uses the & call operator on the quoted script", call.startsWith("& 'C:\\x\\Provision-AgentVM.ps1'"));
ok("call: parameter NAMES stay bare", call.includes(" -Action ") && call.endsWith(" -NonInteractive"));
ok("call: VALUES are single-quoted", call.includes(" -Action 'provision' ") && call.includes(" -VmDiskGB '80' "));

// ── buildHostLaunch (ELEVATED): cmd /c start + Start-Process -Verb RunAs ──────
const hl = life.buildHostLaunch("C:\\x\\Auto-Install.ps1", ["-Action", "reinstall", "-BackupMode", "save"], { elevate: true });
ok("launch(elevated): spawns via cmd.exe (start allocates the console)", hl.file === "cmd.exe");
ok("launch(elevated): cmd runs `start \"\" powershell.exe` (empty title, then the launcher)",
  hl.spawnArgs[0] === "/c" && hl.spawnArgs[1] === "start" && hl.spawnArgs[2] === "" && hl.spawnArgs[3] === "powershell.exe");
ok("launch(elevated): uses -EncodedCommand, not -Command", hl.spawnArgs.includes("-EncodedCommand") && !hl.spawnArgs.includes("-Command"));
ok("launch(elevated): launcher is -NonInteractive (it only fires Start-Process)", hl.spawnArgs.includes("-NonInteractive"));
ok("launch(elevated): nothing but flags + base64 reaches cmd (no raw script path/args)",
  !hl.spawnArgs.some((a) => a.includes("\\") || a.includes(".ps1")));
const b64 = hl.spawnArgs[hl.spawnArgs.length - 1];
ok("launch(elevated): encoded payload is pure base64 (argv-safe)", /^[A-Za-z0-9+/]+=*$/.test(b64));
ok("launch(elevated): base64 decodes (utf16le) back to the command", Buffer.from(b64, "base64").toString("utf16le") === hl.command);
ok("launch(elevated): command is Start-Process -Verb RunAs (UAC)", hl.command.includes("Start-Process") && hl.command.includes("-Verb RunAs"));

// ── buildHostLaunch (NON-elevated): single console runs the script directly ───
const hlp = life.buildHostLaunch("C:\\x\\Provision-AgentVM.ps1", ["-Action", "provision"], { elevate: false });
ok("launch(non-elevated): still cmd.exe /c start", hlp.file === "cmd.exe" && hlp.spawnArgs[1] === "start");
ok("launch(non-elevated): runs the script via & (no inner Start-Process → single window)",
  hlp.command.startsWith("& '") && !hlp.command.includes("Start-Process"));
// The script RUNS in this console, so the launcher must NOT be -NonInteractive (the
// script's "Press Enter to close" pause + any confirmation need an interactive host).
ok("launch(non-elevated): launcher is NOT -NonInteractive", !hlp.spawnArgs.includes("-NonInteractive"));
ok("launch(non-elevated): base64 decodes back to the & command",
  Buffer.from(hlp.spawnArgs[hlp.spawnArgs.length - 1], "base64").toString("utf16le") === hlp.command);

// ── injection-safety: a quote/semicolon in a settings value can't break out ──
// Elevated path (Start-Process -ArgumentList literal): the outer psSingleQuote doubles quotes.
const injE = life.buildHostLaunch("C:\\x\\Auto-Install.ps1", ["-GitUserName", "x'; Start-Process calc; '"], { elevate: true });
ok("launch(elevated): quotes doubled + one -ArgumentList literal", injE.command.includes("''") && /-ArgumentList '([^']|'')*'$/.test(injE.command));
// Non-elevated path (& 'script' -Name 'value'): the value is one single-quoted literal.
const injN = life.buildHostLaunch("C:\\x\\Provision-AgentVM.ps1", ["-GitUserName", "x'; Start-Process calc; '"], { elevate: false });
ok("launch(non-elevated): value stays one single-quoted literal (quotes doubled)",
  injN.command.includes("''") && /-GitUserName '([^']|'')*'$/.test(injN.command));

// ── psSingleQuote ────────────────────────────────────────────────────────────
ok("psSingleQuote: wraps + escapes", life.psSingleQuote("a'b") === "'a''b'");

// ── hostLaunchSpawnOptions: NO windowsHide (the actual "no window" bug) ───────
// windowsHide:true sets CREATE_NO_WINDOW on cmd, which could suppress the console
// `start` allocates — the reported "toast fires, no window, nothing happens". cmd
// exits the moment start fires, so detached just avoids tying it to VS Code.
const spawnOpts = life.hostLaunchSpawnOptions("C:\\x");
ok("spawnOpts: does NOT set windowsHide (would hide the console)", spawnOpts.windowsHide !== true);
ok("spawnOpts: detached true (outlives VS Code, launcher has no own console)", spawnOpts.detached === true);
ok("spawnOpts: stdio ignore", spawnOpts.stdio === "ignore");
ok("spawnOpts: cwd threaded through", spawnOpts.cwd === "C:\\x");

// ── launchHostScript: spawns with the corrected (no-windowsHide) options ──────
// Drive the impure launcher with test seams (fake spawn/vscode + forced platform)
// so we pin the end-to-end spawn shape without a real Windows host.
let spawned = null;
const fakeSpawn = (file, args, options) => {
  spawned = { file, args, options };
  return { on() {}, unref() {} };
};
const fakeVscode = { window: { showInformationMessage() {}, showWarningMessage() {}, showErrorMessage() {} } };
const launched = life.launchHostScript({
  scriptsDir: "C:\\x", script: life.PROVISION, args: ["-Action", "provision"],
  elevate: false, label: "Reprovision",
  _spawn: fakeSpawn, _vscode: fakeVscode, _platform: "win32",
});
ok("launchHostScript: returns true when spawned", launched === true);
ok("launchHostScript: spawns via cmd.exe /c start (start gives the visible console)", spawned && spawned.file === "cmd.exe" && spawned.args[1] === "start");
ok("launchHostScript: spawn options carry NO windowsHide", spawned && spawned.options.windowsHide !== true);
ok("launchHostScript: spawn options are detached (outlive VS Code)", spawned && spawned.options.detached === true);
ok("launchHostScript(non-elevated): runs the script directly in the started console (& )",
  spawned && Buffer.from(spawned.args[spawned.args.length - 1], "base64").toString("utf16le").startsWith("& '"));

// env passthrough: opts.env is merged over process.env and reaches the launched console.
let envSpawned = null;
life.launchHostScript({
  scriptsDir: "C:\\x", script: "Update-Construct.ps1", args: ["-Repo", "a/b"],
  elevate: false, label: "Update Construct", env: { CONSTRUCT_UPDATE_RESULT: "C:\\t\\r.result" },
  _spawn: (file, args, options) => { envSpawned = { file, args, options }; return { on() {}, unref() {} }; },
  _vscode: fakeVscode, _platform: "win32",
});
ok("launchHostScript: opts.env merged into the spawn env", envSpawned && envSpawned.options.env && envSpawned.options.env.CONSTRUCT_UPDATE_RESULT === "C:\\t\\r.result");
ok("launchHostScript: env merge keeps the inherited environment", envSpawned && envSpawned.options.env.PATH === process.env.PATH);

ok("launchHostScript: off-Windows guard returns false without spawning",
  life.launchHostScript({ scriptsDir: "C:\\x", script: life.PROVISION, args: [], label: "Reprovision",
    _spawn: () => { throw new Error("should not spawn off-Windows"); }, _vscode: fakeVscode, _platform: "linux" }) === false);

// ── debug keep-open (-NoExit so errors stay readable) ────────────────────────
ok("child: keepOpen adds -NoExit", life.buildChildCommandLine("C:\\x\\s.ps1", [], { keepOpen: true }).includes("-NoExit"));
ok("child: no -NoExit by default", !life.buildChildCommandLine("C:\\x\\s.ps1", []).includes("-NoExit"));
const dbgN = life.buildHostLaunch("C:\\x\\Provision-AgentVM.ps1", ["-Action", "provision"], { elevate: false, keepOpen: true });
ok("launch(non-elevated,debug): -NoExit on the console powershell", dbgN.spawnArgs.includes("-NoExit"));
const dbgE = life.buildHostLaunch("C:\\x\\Auto-Install.ps1", ["-Action", "reinstall"], { elevate: true, keepOpen: true });
ok("launch(elevated,debug): -NoExit rides the elevated child, not the launcher",
  !dbgE.spawnArgs.includes("-NoExit") && dbgE.command.includes("-NoExit"));

// ── configure(): logger + debug-flag hook ────────────────────────────────────
const logged = [];
life.configure({ log: (m) => logged.push(m), isDebug: () => true });
let dbgSpawned = null;
life.launchHostScript({
  scriptsDir: "C:\\x", script: life.PROVISION, args: ["-Action", "provision"], label: "Reprovision",
  _spawn: (file, args, options) => { dbgSpawned = { file, args, options }; return { on() {}, unref() {} }; },
  _vscode: fakeVscode, _platform: "win32",
});
ok("configure: launch is logged (with the decoded command)", logged.some((m) => m.includes("command:") && m.includes("Provision-AgentVM.ps1")));
ok("configure: isDebug() drives keepOpen (-NoExit) without an explicit opts.debug",
  dbgSpawned && Buffer.from(dbgSpawned.args[dbgSpawned.args.length - 1], "base64").toString("utf16le").length > 0 && dbgSpawned.args.includes("-NoExit"));
life.configure({ log: () => {}, isDebug: () => false }); // reset so it doesn't leak to other checks

console.log(`\n  lifecycle launcher unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
