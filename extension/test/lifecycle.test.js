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
ok("reprovision: omits unset fields", reproEmpty.args.join(" ") === "-Action provision");

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
ok("child: -NoProfile -File preamble", child.startsWith("-NoProfile -ExecutionPolicy Bypass -NoExit -File "));
ok("child: spaced script path is ONE quoted token", child.includes('"C:\\Program Files\\The-Construct\\Auto-Install.ps1"'));
ok("child: spaced value is ONE quoted token", child.includes('-GitUserName "John Smith"'));
ok("child: plain args left unquoted", child.includes("-Action reinstall"));

// ── buildOuterCommand: SINGLE-string -ArgumentList (not an array) ────────────
const outer = life.buildOuterCommand(child, { elevate: true });
ok("outer: elevate adds -Verb RunAs", /^Start-Process -FilePath 'powershell\.exe' -Verb RunAs -ArgumentList '/.test(outer));
ok("outer: -ArgumentList is one quoted string, not a comma array", !/-ArgumentList '[^']*',/.test(outer));
ok("outer: non-elevate omits -Verb RunAs", !/-Verb RunAs/.test(life.buildOuterCommand(child, {})));
const aposOuter = life.buildOuterCommand(life.buildChildCommandLine("C:\\Users\\O'Neil\\Auto-Install.ps1", []), {});
ok("outer: apostrophe in path doubled in the PS literal", aposOuter.includes("O''Neil"));

// ── buildHostLaunch: -EncodedCommand (no Node<->shell quoting layer) ─────────
const hl = life.buildHostLaunch("C:\\x\\Auto-Install.ps1", ["-Action", "reinstall", "-BackupMode", "save"], { elevate: true });
ok("launch: spawns powershell.exe", hl.file === "powershell.exe");
ok("launch: uses -EncodedCommand, not -Command", hl.spawnArgs.includes("-EncodedCommand") && !hl.spawnArgs.includes("-Command"));
const b64 = hl.spawnArgs[hl.spawnArgs.length - 1];
ok("launch: encoded payload is pure base64 (argv-safe)", /^[A-Za-z0-9+/]+=*$/.test(b64));
ok("launch: base64 decodes (utf16le) back to the outer command", Buffer.from(b64, "base64").toString("utf16le") === hl.command);
ok("launch: command equals buildOuterCommand(buildChildCommandLine(...))",
  hl.command === life.buildOuterCommand(life.buildChildCommandLine("C:\\x\\Auto-Install.ps1", ["-Action", "reinstall", "-BackupMode", "save"]), { elevate: true }));

// ── injection-safety: a quote/semicolon in a settings value can't break out ──
const inj = life.buildHostLaunch("C:\\x\\Provision-AgentVM.ps1", ["-GitUserName", "x'; Start-Process calc; '"], { elevate: false });
ok("launch: single quotes in a value are doubled (no PS-literal breakout)", inj.command.includes("''"));
ok("launch: the whole arg payload stays one -ArgumentList literal", /-ArgumentList '([^']|'')*'$/.test(inj.command));

// ── psSingleQuote ────────────────────────────────────────────────────────────
ok("psSingleQuote: wraps + escapes", life.psSingleQuote("a'b") === "'a''b'");

console.log(`\n  lifecycle launcher unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
