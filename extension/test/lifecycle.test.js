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
  settings: { gitName: "Neo", gitEmail: "neo@zion.io", serveWeb: true, tunnel: false, smb: true, partialStreaming: true, mic: true, opencodeBackgroundWatcher: true, t3code: true, t3codeChannel: "nightly" },
});
ok("reprovision: uses Provision script", repro.script === life.PROVISION);
ok("reprovision: not destructive, not elevated", repro.destructive === false && repro.elevate === false);
ok("reprovision: -Action provision", has(repro.args, "-Action", "provision"));
ok("reprovision: git identity", has(repro.args, "-GitUserName", "Neo") && has(repro.args, "-GitEmail", "neo@zion.io"));
ok("reprovision: bools as true/false strings", has(repro.args, "-VsCodeServeWeb", "true") && has(repro.args, "-VsCodeTunnel", "false") && has(repro.args, "-SmbShare", "true") && has(repro.args, "-ClaudePartialStreaming", "true") && has(repro.args, "-MicPassthrough", "true"));
ok("reprovision: threads -T3Code from settings", has(repro.args, "-T3Code", "true"));
ok("reprovision: threads the OpenCode watcher setting", has(repro.args, "-OpenCodeBackgroundWatcher", "true"));
ok("reprovision: threads -T3CodeChannel from settings", has(repro.args, "-T3CodeChannel", "nightly"));

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
const rei = life.buildInvocation("reinstall", { settings: { ram: "16", disk: "80", gitName: "Neo", partialStreaming: false, mic: true, opencodeBackgroundWatcher: false, t3code: false } });
ok("reinstall: uses Auto-Install script", rei.script === life.AUTO_INSTALL);
ok("reinstall: destructive + elevated", rei.destructive === true && rei.elevate === true);
ok("reinstall: label", rei.label === "Reinstall");
ok("reinstall: -Action reinstall -BackupMode save (default)", has(rei.args, "-Action", "reinstall", "-BackupMode", "save"));
ok("reinstall: VM resources from settings", has(rei.args, "-VmMemoryGB", "16") && has(rei.args, "-VmDiskGB", "80"));
ok("reinstall: no -UbuntuRelease (reuses ISO)", !rei.args.includes("-UbuntuRelease"));
ok("reinstall: threads -ClaudePartialStreaming from settings", has(rei.args, "-ClaudePartialStreaming", "false"));
ok("reinstall: threads -MicPassthrough from settings", has(rei.args, "-MicPassthrough", "true"));
ok("reinstall: threads -T3Code from settings (explicit off persists)", has(rei.args, "-T3Code", "false"));
ok("reinstall: threads the OpenCode watcher explicit off", has(rei.args, "-OpenCodeBackgroundWatcher", "false"));
ok("reinstall: omits -T3CodeChannel when absent from settings",
  !rei.args.includes("-T3CodeChannel"));

const reiProj = life.buildInvocation("reinstall", { settings: {}, projects: ["web"] });
ok("reinstall: passes -Projects (Auto-Install forwards it to Provision)", has(reiProj.args, "-Projects", "web"));
const reiWipe = life.buildInvocation("reinstall", { settings: {}, backupMode: "wipe" });
ok("reinstall: honors backupMode wipe", has(reiWipe.args, "-BackupMode", "wipe"));
const reiBad = life.buildInvocation("reinstall", { settings: {}, backupMode: "bogus" });
ok("reinstall: invalid backupMode -> save", has(reiBad.args, "-BackupMode", "save"));

// ── redownload ───────────────────────────────────────────────────────────────
const red = life.buildInvocation("redownload", { settings: { ubuntu: "24.04", ram: "8", partialStreaming: true, mic: false, t3code: true, t3codeChannel: "nightly" }, backupMode: "existing" });
ok("redownload: Auto-Install + label", red.script === life.AUTO_INSTALL && red.label === "Redownload");
ok("redownload: -Action redownload + backupMode", has(red.args, "-Action", "redownload", "-BackupMode", "existing"));
ok("redownload: includes -UbuntuRelease", has(red.args, "-UbuntuRelease", "24.04"));
ok("redownload: threads -ClaudePartialStreaming from settings", has(red.args, "-ClaudePartialStreaming", "true"));
ok("redownload: threads -MicPassthrough from settings", has(red.args, "-MicPassthrough", "false"));
ok("redownload: threads -T3Code from settings", has(red.args, "-T3Code", "true"));
ok("redownload: threads -T3CodeChannel from settings", has(red.args, "-T3CodeChannel", "nightly"));

// Capability gate: an older scripts dir has no -T3CodeChannel parameter; passing
// it to Provision-AgentVM.ps1 would fail to bind and break the lifecycle action.
ok("reprovision: drops -T3CodeChannel when the scripts don't support it",
  !life.buildInvocation("reprovision", { settings: { t3codeChannel: "nightly" }, supportsT3CodeChannel: false }).args.includes("-T3CodeChannel"));
ok("rebuild: drops -T3CodeChannel when the scripts don't support it",
  !life.buildInvocation("reinstall", { settings: { t3codeChannel: "nightly" }, supportsT3CodeChannel: false }).args.includes("-T3CodeChannel"));
ok("rebuild: keeps -T3CodeChannel when supported",
  has(life.buildInvocation("reinstall", { settings: { t3codeChannel: "nightly" }, supportsT3CodeChannel: true }).args, "-T3CodeChannel", "nightly"));
ok("reprovision: drops the OpenCode watcher flag for old scripts",
  !life.buildInvocation("reprovision", { settings: { opencodeBackgroundWatcher: true }, supportsOpenCodeBackgroundWatcher: false }).args.includes("-OpenCodeBackgroundWatcher"));
ok("rebuild: keeps the OpenCode watcher flag when supported",
  has(life.buildInvocation("reinstall", { settings: { opencodeBackgroundWatcher: true }, supportsOpenCodeBackgroundWatcher: true }).args, "-OpenCodeBackgroundWatcher", "true"));

const redNoRel = life.buildInvocation("redownload", { settings: {} });
ok("redownload: omits -UbuntuRelease when unset", !redNoRel.args.includes("-UbuntuRelease"));

// ── automatic checkpoints ────────────────────────────────────────────────────
// The preference is a Hyper-V property fixed at VM-CREATION time, so it rides the
// rebuild actions (which run Create-AgentVM) and NOT reprovision (which never
// touches Hyper-V); an existing VM is changed by the separate setCheckpoints action.
ok("reinstall: threads -AutomaticCheckpoints false from settings", has(life.buildInvocation("reinstall", { settings: { autoCheckpoints: false } }).args, "-AutomaticCheckpoints", "false"));
ok("reinstall: -AutomaticCheckpoints true when enabled", has(life.buildInvocation("reinstall", { settings: { autoCheckpoints: true } }).args, "-AutomaticCheckpoints", "true"));
ok("redownload: threads -AutomaticCheckpoints", has(life.buildInvocation("redownload", { settings: { autoCheckpoints: true } }).args, "-AutomaticCheckpoints", "true"));
ok("rebuild: omits -AutomaticCheckpoints when unset", !life.buildInvocation("reinstall", { settings: {} }).args.includes("-AutomaticCheckpoints"));
// Capability gate: an older scripts dir has no -AutomaticCheckpoints parameter, and
// Auto-Install.ps1 is an advanced function — passing it would fail to BIND and the
// rebuild would never start. Dropping the flag lets the old script's default stand.
// The gate reads the PARAMETER out of Auto-Install.ps1 itself: a companion-file check
// would misjudge a hand-assembled dir holding the new live-apply script next to an
// old Auto-Install.ps1, and pass the flag into a binding failure.
ok("rebuild: drops -AutomaticCheckpoints when the scripts don't support it",
  !life.buildInvocation("reinstall", { settings: { autoCheckpoints: true }, supportsCheckpoints: false }).args.includes("-AutomaticCheckpoints"));
ok("rebuild: keeps -AutomaticCheckpoints when supported (and when unspecified)",
  has(life.buildInvocation("reinstall", { settings: { autoCheckpoints: true }, supportsCheckpoints: true }).args, "-AutomaticCheckpoints", "true") &&
  has(life.buildInvocation("reinstall", { settings: { autoCheckpoints: true } }).args, "-AutomaticCheckpoints", "true"));
ok("reprovision: never sends -AutomaticCheckpoints (no Hyper-V access)", !life.buildInvocation("reprovision", { settings: { autoCheckpoints: true } }).args.includes("-AutomaticCheckpoints"));

const chkOff = life.buildInvocation("setCheckpoints", { enabled: false });
const chkOn = life.buildInvocation("setCheckpoints", { enabled: true });
ok("setCheckpoints: uses the checkpoint script", chkOff.script === life.CHECKPOINTS && life.CHECKPOINTS === "Set-AgentVmCheckpoints.ps1");
ok("setCheckpoints: elevated (Hyper-V needs admin), not modal-destructive", chkOff.elevate === true && chkOff.destructive === false);
ok("setCheckpoints: -Enabled false", has(chkOff.args, "-Enabled", "false"));
ok("setCheckpoints: -Enabled true", has(chkOn.args, "-Enabled", "true"));
// STRICT boolean: this action DELETES checkpoints when it runs with -Enabled false, so a
// malformed request must be refused, not defaulted into the destructive direction.
ok("setCheckpoints: missing enabled -> null (never defaults to the destructive direction)",
  life.buildInvocation("setCheckpoints", {}) === null);
ok("setCheckpoints: non-boolean enabled -> null",
  life.buildInvocation("setCheckpoints", { enabled: "true" }) === null &&
  life.buildInvocation("setCheckpoints", { enabled: 1 }) === null &&
  life.buildInvocation("setCheckpoints", { enabled: null }) === null);
ok("setCheckpoints: labels say which way it went", chkOn.label === "Enable automatic checkpoints" && chkOff.label === "Disable automatic checkpoints");
ok("setCheckpoints: passes -FromPanel (no pause on success)", chkOff.args.includes("-FromPanel"));

// scriptSupportsCheckpoints reads the real parameter, against a fake scripts dir.
const fs = require("fs"), os = require("os"), path = require("path");
const sd = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-"));
ok("capability: no Auto-Install.ps1 at all -> unsupported", life.scriptSupportsCheckpoints(sd) === false);
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "param(\n  [string]$T3Code = \"\"\n)\n");
ok("capability: an old Auto-Install.ps1 -> unsupported", life.scriptSupportsCheckpoints(sd) === false);
// The dangerous mixed-version case: the NEW live-apply script sitting next to an OLD
// Auto-Install.ps1. A file-presence check would wrongly say "supported" and hand the
// flag to a script that rejects it.
fs.writeFileSync(path.join(sd, "Set-AgentVmCheckpoints.ps1"), "param([string]$Enabled)\n");
ok("capability: companion script present but the parameter absent -> still unsupported",
  life.scriptSupportsCheckpoints(sd) === false);
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "param(\n  [ValidateSet(\"true\",\"false\")]\n  [string]$AutomaticCheckpoints = \"false\"\n)\n");
ok("capability: the parameter present -> supported", life.scriptSupportsCheckpoints(sd) === true);
ok("capability: null scripts dir -> unsupported (no throw)", life.scriptSupportsCheckpoints(null) === false);
// It must match a DECLARATION, not any mention: a doc comment naming the parameter on a
// script that lacks it would send the flag into a binding failure.
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "# forwards -AutomaticCheckpoints in newer builds\n<#\n  mentions $AutomaticCheckpoints in prose\n#>\nparam([string]$T3Code)\n");
ok("capability: a prose/comment mention alone -> unsupported", life.scriptSupportsCheckpoints(sd) === false);
// Even a commented-OUT declaration must not count — it is not bindable.
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "param(\n  # $AutomaticCheckpoints = \"false\",\n  [string]$T3Code\n)\n");
ok("capability: a commented-out declaration -> unsupported", life.scriptSupportsCheckpoints(sd) === false);
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "<#\n .PARAMETER X\n   $AutomaticCheckpoints = \"false\"\n#>\nparam([string]$T3Code)\n");
ok("capability: a block-comment help mention -> unsupported", life.scriptSupportsCheckpoints(sd) === false);
// PowerShell identifiers are case-insensitive, and Windows files are CRLF.
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "param(\r\n  [string]$automaticcheckpoints = \"false\",\r\n  [switch]$X\r\n)\r\n");
ok("capability: case-insensitive + CRLF declaration -> supported", life.scriptSupportsCheckpoints(sd) === true);
fs.writeFileSync(path.join(sd, "Auto-Install.ps1"), "param(\n  [string]$AutomaticCheckpoints\n)\n");
ok("capability: trailing declaration with no default -> supported", life.scriptSupportsCheckpoints(sd) === true);

// ── scriptSupportsT3CodeChannel (requires BOTH Provision + Auto-Install) ────
const sd2 = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-ch-"));
ok("t3ch-capability: empty dir -> unsupported", life.scriptSupportsT3CodeChannel(sd2) === false);
// Only Provision has the param — Auto-Install is missing entirely
fs.writeFileSync(path.join(sd2, "Provision-AgentVM.ps1"), "param(\n  [string]$T3CodeChannel = \"\"\n)\n");
ok("t3ch-capability: Provision has it but Auto-Install missing -> unsupported", life.scriptSupportsT3CodeChannel(sd2) === false);
// Both present but Auto-Install lacks the param (partially-updated scripts dir)
fs.writeFileSync(path.join(sd2, "Auto-Install.ps1"), "param(\n  [string]$T3Code = \"\"\n)\n");
ok("t3ch-capability: Provision has it, Auto-Install old -> unsupported", life.scriptSupportsT3CodeChannel(sd2) === false);
// Both have the param -> supported
fs.writeFileSync(path.join(sd2, "Auto-Install.ps1"), "param(\n  [string]$T3Code = \"\",\n  [string]$T3CodeChannel = \"\"\n)\n");
ok("t3ch-capability: both scripts have it -> supported", life.scriptSupportsT3CodeChannel(sd2) === true);
// Comment-only mention in Provision doesn't count
fs.writeFileSync(path.join(sd2, "Provision-AgentVM.ps1"), "# mentions $T3CodeChannel in a comment\nparam([string]$T3Code)\n");
ok("t3ch-capability: comment-only mention in Provision -> unsupported", life.scriptSupportsT3CodeChannel(sd2) === false);
ok("t3ch-capability: null scripts dir -> unsupported", life.scriptSupportsT3CodeChannel(null) === false);
// Only Auto-Install has the param — Provision lacks it
fs.writeFileSync(path.join(sd2, "Auto-Install.ps1"), "param(\n  [string]$T3CodeChannel = \"\"\n)\n");
fs.writeFileSync(path.join(sd2, "Provision-AgentVM.ps1"), "param(\n  [string]$T3Code = \"\"\n)\n");
ok("t3ch-capability: Auto-Install has it, Provision old -> unsupported", life.scriptSupportsT3CodeChannel(sd2) === false);
// Regression for finding #3: action/target-appropriate capability detection.
// A partially-updated scripts dir where Provision has the parameter but Auto-Install
// doesn't: reprovision targets only Provision (so the flag is safe to send),
// but rebuild targets Auto-Install (unsafe — binding failure).
const sd3 = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-skew-"));
fs.writeFileSync(path.join(sd3, "Provision-AgentVM.ps1"), "param(\n  [string]$T3CodeChannel = \"\"\n)\n");
fs.writeFileSync(path.join(sd3, "Auto-Install.ps1"), "param(\n  [string]$T3Code = \"\"\n)\n");
// No action → conservative (both required) → false
ok("t3ch-capability: Provision=new, Auto-Install=old, no action -> unsupported (conservative)",
  life.scriptSupportsT3CodeChannel(sd3) === false);
// Action-appropriate: reprovision checks only Provision → true
ok("t3ch-capability: Provision=new, Auto-Install=old, reprovision -> supported",
  life.scriptSupportsT3CodeChannel(sd3, "reprovision") === true);
// Action-appropriate: rebuild checks only Auto-Install → false
ok("t3ch-capability: Provision=new, Auto-Install=old, reinstall -> unsupported",
  life.scriptSupportsT3CodeChannel(sd3, "reinstall") === false);
ok("t3ch-capability: Provision=new, Auto-Install=old, redownload -> unsupported",
  life.scriptSupportsT3CodeChannel(sd3, "redownload") === false);
// Opposite direction: Provision=old, Auto-Install=new
const sd4 = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-skew2-"));
fs.writeFileSync(path.join(sd4, "Provision-AgentVM.ps1"), "param(\n  [string]$T3Code = \"\"\n)\n");
fs.writeFileSync(path.join(sd4, "Auto-Install.ps1"), "param(\n  [string]$T3CodeChannel = \"\"\n)\n");
ok("t3ch-capability: Provision=old, Auto-Install=new, reprovision -> unsupported",
  life.scriptSupportsT3CodeChannel(sd4, "reprovision") === false);
ok("t3ch-capability: Provision=old, Auto-Install=new, reinstall -> supported",
  life.scriptSupportsT3CodeChannel(sd4, "reinstall") === true);
// buildInvocation tests: the per-action capability feeds into the right decision
ok("reprovision: keeps -T3CodeChannel when Provision=new (even if Auto-Install=old)",
  life.buildInvocation("reprovision", { settings: { t3codeChannel: "nightly" }, supportsT3CodeChannel: true }).args.includes("-T3CodeChannel"));
ok("rebuild: drops -T3CodeChannel when Auto-Install=old",
  !life.buildInvocation("reinstall", { settings: { t3codeChannel: "nightly" }, supportsT3CodeChannel: false }).args.includes("-T3CodeChannel"));

// ── scriptSupportsOpenCodeBackgroundWatcher (action-sensitive) ─────────────
const sdWatcher = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-watcher-"));
fs.writeFileSync(path.join(sdWatcher, "Provision-AgentVM.ps1"), "param(\n  [string]$OpenCodeBackgroundWatcher = \"\"\n)\n");
ok("watcher-capability: reprovision checks Provision only",
  life.scriptSupportsOpenCodeBackgroundWatcher(sdWatcher, "reprovision") === true);
ok("watcher-capability: rebuild is unsupported without the Auto-Install parameter",
  life.scriptSupportsOpenCodeBackgroundWatcher(sdWatcher, "reinstall") === false);
fs.writeFileSync(path.join(sdWatcher, "Auto-Install.ps1"), "param(\n  [string]$OpenCodeBackgroundWatcher = \"\"\n)\n");
ok("watcher-capability: rebuild checks Auto-Install only",
  life.scriptSupportsOpenCodeBackgroundWatcher(sdWatcher, "redownload") === true);
fs.writeFileSync(path.join(sdWatcher, "Provision-AgentVM.ps1"), "# $OpenCodeBackgroundWatcher is documented here\nparam([string]$T3Code)\n");
ok("watcher-capability: a comment-only mention does not count",
  life.scriptSupportsOpenCodeBackgroundWatcher(sdWatcher, "reprovision") === false);

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

// A VALUE is quoted whatever it starts with. The tokens come from the instance
// registry, which the user hand-edits, and this string IS a PowerShell command: a
// leading-dash value emitted bare would be parsed as syntax (command injection on a
// plain button press). `argSpec` — buildInvocation's (flag, value) pairs — says which
// token is a parameter NAME, so no guessing is involved.
const spec = [
  { flag: "-FromPanel" },
  { flag: "-VmHost", value: "-x'; Start-Process calc; #" },
  { flag: "-Action", value: "provision" },
];
const injected = life.buildCallCommand("C:\\x\\Provision-AgentVM.ps1", life.flattenArgPairs(spec), spec);
ok("call(spec): a leading-dash VALUE is quoted, not emitted as code",
  injected.includes("-VmHost '-x''; Start-Process calc; #'"));
ok("call(spec): the embedded apostrophe is doubled", injected.includes("''; Start-Process"));
ok("call(spec): a switch stays bare", injected.includes("& 'C:\\x\\Provision-AgentVM.ps1' -FromPanel "));
ok("call(spec): nothing executable survives outside the quotes",
  !/Start-Process/.test(injected.replace(/'(?:[^']|'')*'/g, "")));
// WITHOUT a spec the ORIGINAL rule stands, unchanged: anything starting with '-' is a
// name. That is the zero-change bar, not a nicety — so it is pinned against literals,
// not against the builder's own other branch.
ok("call(no spec): the legacy rule is intact — a leading-dash token stays bare",
  life.buildCallCommand("C:\\x\\p.ps1", ["-VmHost", "-x; Start-Process calc; #"]) ===
  "& 'C:\\x\\p.ps1' -VmHost -x; Start-Process calc; #");
ok("call(no spec): ordinary flags are still bare",
  life.buildCallCommand("C:\\x\\p.ps1", ["-Action", "provision"]) === "& 'C:\\x\\p.ps1' -Action 'provision'");

// ZERO-CHANGE PIN. buildInvocation attaches the spec ONLY for a non-default instance,
// so an install with no registry produces the pre-instances command string verbatim —
// including for a VALUE that itself begins with '-' (a project named "-NoProfile" was
// emitted bare before instances existed and must still be). The expected strings below
// are literals: comparing the two builder branches against each other would pass even
// if both drifted.
const dashProjects = life.buildInvocation("reprovision", { settings: {}, projects: ["-NoProfile", "x"] });
ok("default path: no argSpec is attached (nothing to target)", dashProjects.argSpec === undefined);
ok("default path: the command string is the legacy one, verbatim",
  life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", dashProjects.args, dashProjects.argSpec) ===
  "& 'C:\\s\\Provision-AgentVM.ps1' -FromPanel -Action 'provision' -Projects -NoProfile,x -NonInteractive",
  life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", dashProjects.args, dashProjects.argSpec));
const plainDefault = life.buildInvocation("reprovision", { settings: { gitName: "Neo", gitEmail: "neo@zion.io" } });
ok("default path: an ordinary reprovision command is pinned too",
  life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", plainDefault.args, plainDefault.argSpec) ===
  "& 'C:\\s\\Provision-AgentVM.ps1' -FromPanel -Action 'provision' -GitUserName 'Neo' -GitEmail 'neo@zion.io' -NonInteractive",
  life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", plainDefault.args, plainDefault.argSpec));
ok("call: buildHostLaunch threads argSpec through to the command",
  life.buildHostLaunch("C:\\x\\p.ps1", life.flattenArgPairs(spec), { argSpec: spec }).command
    .includes("-VmHost '-x''; Start-Process calc; #'"));

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


// ── Remote instances: the rebuild targets the SERVICE, not a local VM name ───
// (B7, docs/remote-host.md.) Auto-Install.ps1's -VmName path derives a guest hostname,
// an mshome address and a local Hyper-V display name — none of which exist for a VM on
// somebody else's host — so the rebuild actions carry a different parameter set.
const REMOTE_INST = {
  name: "work-vm", backend: "hyperv-remote", vmName: "work-vm",
  vmHost: "buildbox.example.local", sshPort: 2201, hostAlias: "work-vm",
  keyName: "construct_work-vm_ed25519", configBranch: "vm-work-vm", scriptsDir: null,
  service: { url: "https://buildbox.example.local:7462", auth: "negotiate" },
};
const LOCAL_INST = {
  name: "work-vm", backend: "hyperv-local", vmName: "work-vm",
  vmHost: "work-vm.mshome.net", sshPort: 22, hostAlias: "work-vm",
  keyName: "construct_work-vm_ed25519", configBranch: "vm-work-vm", scriptsDir: null,
};
const DEFAULT_INST = {
  name: "agent-vm", backend: "hyperv-local", vmName: "Agent-VM",
  vmHost: "agent-vm.mshome.net", sshPort: 22, hostAlias: "agent-vm",
  keyName: "agent_vm_ed25519", configBranch: "vm", scriptsDir: null,
};
const EVERY_PARAM = ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch", "VmName",
                     "VmHost", "HostAlias", "SshPort", "LocalKeyName"];
// The same, MINUS "InstanceName": what instanceParamSupport reports for a scripts dir
// that predates name-only targeting (B11). A probe result carrying "InstanceName" is
// what selects the name form, so the legacy assertions have to state the legacy probe.
const LEGACY_PARAM = EVERY_PARAM.filter((p) => p !== "InstanceName");

const deepEq = (name, actual, expected) =>
  ok(name, JSON.stringify(actual) === JSON.stringify(expected),
     `expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
ok("remote: isRemoteBackend normalises like getDriver",
  life.isRemoteBackend("  HyperV-Remote ") === true && life.isRemoteBackend("hyperv-local") === false &&
  life.isRemoteBackend(null) === false);
deepEq("remote: reinstall emits the service parameters",
  life.instanceArgs("reinstall", REMOTE_INST, EVERY_PARAM),
  ["-Backend", "hyperv-remote", "-ServiceUrl", "https://buildbox.example.local:7462", "-InstanceName", "work-vm"]);
ok("remote: ...and never -VmName (that is the LOCAL path)",
  life.instanceArgs("reinstall", REMOTE_INST, EVERY_PARAM).indexOf("-VmName") < 0);
deepEq("remote: redownload emits the same set",
  life.instanceArgs("redownload", REMOTE_INST, EVERY_PARAM),
  life.instanceArgs("reinstall", REMOTE_INST, EVERY_PARAM));
// -ConfigBranch is still conditional: "vm-work-vm" is exactly what the provisioner
// derives from the alias, so there is nothing to emit.
ok("remote: -ConfigBranch stays conditional (the canonical branch emits nothing)",
  life.instanceArgs("reinstall", REMOTE_INST, EVERY_PARAM).indexOf("-ConfigBranch") < 0);
deepEq("remote: an explicit branch IS emitted",
  life.instanceArgs("reinstall", { ...REMOTE_INST, configBranch: "vm-team" }, EVERY_PARAM),
  ["-Backend", "hyperv-remote", "-ServiceUrl", "https://buildbox.example.local:7462",
   "-InstanceName", "work-vm", "-ConfigBranch", "vm-team"]);
// Reprovision/export are pure SSH to the endpoint, so they are IDENTICAL for both
// backends — whoever created the VM.
deepEq("remote: reprovision keeps the endpoint identity, unchanged",
  life.instanceArgs("reprovision", REMOTE_INST, LEGACY_PARAM),
  ["-VmHost", "buildbox.example.local", "-HostAlias", "work-vm", "-SshPort", "2201",
   "-LocalKeyName", "construct_work-vm_ed25519"]);
deepEq("local: the rebuild set is unchanged by B7",
  life.instanceArgs("reinstall", LOCAL_INST, LEGACY_PARAM), ["-VmName", "work-vm"]);
// THE ZERO-CHANGE BAR: the default instance emits nothing at all, on every action.
for (const action of ["reprovision", "exportConfig", "reinstall", "redownload", "setCheckpoints"]) {
  deepEq(`zero-change: the default instance emits no target args (${action})`,
    life.instanceArgs(action, DEFAULT_INST, EVERY_PARAM), []);
  deepEq(`zero-change: no instance at all emits none either (${action})`,
    life.instanceArgs(action, null, EVERY_PARAM), []);
}
const defaultInv = life.buildInvocation("reinstall", { settings: {}, backupMode: "save", instance: DEFAULT_INST, instanceParams: EVERY_PARAM });
ok("zero-change: the default instance's invocation carries no argSpec (bare-value quoting)",
  defaultInv.argSpec === undefined);
ok("zero-change: ...and no remote parameter can appear in it",
  !defaultInv.args.includes("-ServiceUrl") && !defaultInv.args.includes("-Backend") &&
  !defaultInv.args.includes("-InstanceName"));

// Fail CLOSED on version skew: scripts that predate the remote parameters would run the
// LOCAL path and rebuild a local VM named after the remote one.
for (const declared of [[], ["ConfigBranch"], ["Backend", "InstanceName"], ["ServiceUrl", "InstanceName"]]) {
  const r = life.checkInstanceSupport("reinstall", REMOTE_INST, declared);
  ok(`skew: reinstall refused when Auto-Install declares [${declared}]`, !!r && r.blocked === true);
}
ok("skew: reinstall allowed once all three are declared",
  life.checkInstanceSupport("reinstall", REMOTE_INST, ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch"]) === null);
const noSvc = life.checkInstanceSupport("reinstall", { ...REMOTE_INST, service: null }, EVERY_PARAM);
ok("skew: an entry with no service.url is refused on every version of the scripts",
  !!noSvc && noSvc.blocked === true && /host service/i.test(noSvc.reason));
ok("skew: ...and reprovision still works for it (pure SSH to the endpoint)",
  life.checkInstanceSupport("reprovision", { ...REMOTE_INST, service: null }, EVERY_PARAM) === null);

// ── NAME-ONLY TARGETING (B11, plan §4.12) ────────────────────────────────────
// One `-InstanceName <name>` in place of the four identity arguments, once the installed
// scripts can resolve a name. Three things have to hold: the emission, the FALLBACK for
// older scripts, and the DEFAULT instance still emitting nothing at all.
console.log("\n=== name-only targeting (-InstanceName) ===");

const NAME_PARAM = ["InstanceName", "ConfigBranch"];
deepEq("name-only: reprovision emits the NAME instead of the four identity args",
  life.instanceArgs("reprovision", LOCAL_INST, NAME_PARAM), ["-InstanceName", "work-vm"]);
deepEq("name-only: exportConfig emits the name (and no branch — it initialises no store)",
  life.instanceArgs("exportConfig", LOCAL_INST, ["InstanceName"]), ["-InstanceName", "work-vm"]);
deepEq("name-only: reinstall emits the name instead of -VmName",
  life.instanceArgs("reinstall", LOCAL_INST, NAME_PARAM), ["-InstanceName", "work-vm"]);
deepEq("name-only: redownload emits the name too",
  life.instanceArgs("redownload", LOCAL_INST, NAME_PARAM), ["-InstanceName", "work-vm"]);
deepEq("name-only: setCheckpoints emits the name instead of -VmName",
  life.instanceArgs("setCheckpoints", LOCAL_INST, ["InstanceName"]), ["-InstanceName", "work-vm"]);
deepEq("name-only: a reprovision of a REMOTE instance is name-targeted too (the entry has its endpoint)",
  life.instanceArgs("reprovision", REMOTE_INST, NAME_PARAM), ["-InstanceName", "work-vm"]);
deepEq("name-only: an explicit branch still rides along",
  life.instanceArgs("reprovision", { ...LOCAL_INST, configBranch: "vm-team" }, NAME_PARAM),
  ["-InstanceName", "work-vm", "-ConfigBranch", "vm-team"]);
// A REMOTE rebuild keeps its own set: it must also say WHICH host service, and -Backend
// is what makes Auto-Install.ps1 take the remote path at all.
deepEq("name-only: a remote REBUILD keeps -Backend/-ServiceUrl beside the name",
  life.instanceArgs("reinstall", REMOTE_INST, ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch"]),
  ["-Backend", "hyperv-remote", "-ServiceUrl", "https://buildbox.example.local:7462",
   "-InstanceName", "work-vm"]);
// THE ZERO-CHANGE BAR is unchanged by any of this.
for (const action of ["reprovision", "exportConfig", "reinstall", "redownload", "setCheckpoints"]) {
  deepEq(`name-only: the default instance still emits nothing (${action})`,
    life.instanceArgs(action, DEFAULT_INST, NAME_PARAM), []);
}
// The gate: the NAME is what makes the action targeted, so that is all it requires — and
// -ConfigBranch is still the capability marker for instance-keyed config sync.
ok("name-only: reprovision is allowed when the name is declared",
  life.checkInstanceSupport("reprovision", LOCAL_INST, NAME_PARAM) === null);
const noBranch = life.checkInstanceSupport("reprovision", LOCAL_INST, ["InstanceName"]);
ok("name-only: ...but not without -ConfigBranch (the config store would split)",
  !!noBranch && noBranch.blocked === true && /ConfigBranch/.test(noBranch.reason));
for (const declared of [[], ["ConfigBranch"], ["VmHost", "ConfigBranch"]]) {
  const r = life.checkInstanceSupport("reinstall", LOCAL_INST, declared);
  ok(`name-only: reinstall stays refused for a scripts dir declaring [${declared}]`,
    !!r && r.blocked === true);
}

// The PROBE. The parameter alone is not the question — `-InstanceName` predates this
// meaning on Provision-AgentVM.ps1 and Auto-Install.ps1 — so the marker FILE decides.
const sdName = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-name-"));
const NAME_ERA_PARAMS = "param(\n  [string]$InstanceName = \"\",\n  [string]$ConfigBranch = \"\",\n" +
  "  [string]$VmHost,\n  [string]$HostAlias,\n  [int]$SshPort = 22,\n  [string]$LocalKeyName,\n  [string]$VmName\n)\n";
for (const f of [life.PROVISION, life.AUTO_INSTALL, life.CHECKPOINTS]) {
  fs.writeFileSync(path.join(sdName, f), NAME_ERA_PARAMS);
}
ok("probe: a B7-era scripts dir (parameter present, marker absent) is NOT name-capable",
  life.supportsNameTargeting(sdName, "reprovision", LOCAL_INST) === false);
deepEq("probe: ...so it is probed — and emits — the four identity args",
  life.instanceArgs("reprovision", LOCAL_INST, life.instanceParamSupport(sdName, "reprovision", LOCAL_INST)),
  ["-VmHost", "work-vm.mshome.net", "-HostAlias", "work-vm", "-SshPort", "22",
   "-LocalKeyName", "construct_work-vm_ed25519"]);
fs.mkdirSync(path.join(sdName, "lib"), { recursive: true });
fs.writeFileSync(path.join(sdName, life.INSTANCE_TARGET_LIB), "# the adapter\n");
ok("probe: with the marker file installed it IS name-capable",
  life.supportsNameTargeting(sdName, "reprovision", LOCAL_INST) === true);
deepEq("probe: ...and instanceParamSupport reports the name set",
  life.instanceParamSupport(sdName, "reprovision", LOCAL_INST), ["InstanceName", "ConfigBranch"]);
deepEq("probe: ...so the emitted argv is one argument",
  life.instanceArgs("reprovision", LOCAL_INST, life.instanceParamSupport(sdName, "reprovision", LOCAL_INST)),
  ["-InstanceName", "work-vm"]);
ok("probe: a REMOTE rebuild is never name-only (it needs the service URL as well)",
  life.supportsNameTargeting(sdName, "reinstall", REMOTE_INST) === false);
// This fixture's Auto-Install.ps1 declares neither -Backend nor -ServiceUrl, so a remote
// rebuild is probed for the remote set and comes back INCOMPLETE...
deepEq("probe: ...and is probed for the remote set, not the name set",
  life.instanceParamSupport(sdName, "reinstall", REMOTE_INST), ["InstanceName", "ConfigBranch"]);
const remoteSkew = life.checkInstanceSupport("reinstall", REMOTE_INST,
  life.instanceParamSupport(sdName, "reinstall", REMOTE_INST));
ok("probe: ...so the remote rebuild is refused, name in the probe or not",
  !!remoteSkew && remoteSkew.blocked === true && /-Backend/.test(remoteSkew.reason));
// The marker on its own is not enough: an argument the target does not declare is a
// binding failure, i.e. an action that never starts.
const sdMarkerOnly = fs.mkdtempSync(path.join(os.tmpdir(), "construct-life-marker-"));
fs.mkdirSync(path.join(sdMarkerOnly, "lib"), { recursive: true });
fs.writeFileSync(path.join(sdMarkerOnly, life.INSTANCE_TARGET_LIB), "# the adapter\n");
fs.writeFileSync(path.join(sdMarkerOnly, life.PROVISION), "param(\n  [string]$VmHost\n)\n");
ok("probe: the marker without the parameter is not name-capable",
  life.supportsNameTargeting(sdMarkerOnly, "reprovision", LOCAL_INST) === false);
ok("probe: no scripts dir at all is not name-capable",
  life.supportsNameTargeting("", "reprovision", LOCAL_INST) === false);

// ── ELEVATION IS BACKEND-AWARE ───────────────────────────────────────────────
// A remote rebuild creates no local VM, so it needs no administrator rights — and on a
// PC where UAC switches to a different admin account, elevating would put the DPAPI
// token store, instances.json and ~\.ssh in that account's profile. Auto-Install.ps1
// makes the same call (it skips its own relaunch on the remote path); if the launcher
// elevated anyway, the script would already be in the wrong profile before it could.
const remoteOpts = { settings: {}, backupMode: "save", instance: REMOTE_INST, instanceParams: EVERY_PARAM };
for (const action of ["reinstall", "redownload"]) {
  const remoteInv = life.buildInvocation(action, remoteOpts);
  ok(`elevate: a REMOTE ${action} does not elevate`, remoteInv.elevate === false);
  ok(`elevate: ...and is still destructive (the confirm modal stays)`, remoteInv.destructive === true);
  const localInv = life.buildInvocation(action, { settings: {}, backupMode: "save", instance: LOCAL_INST, instanceParams: EVERY_PARAM });
  ok(`elevate: a LOCAL ${action} still elevates (it drives Hyper-V)`, localInv.elevate === true);
  const defaultInvE = life.buildInvocation(action, { settings: {}, backupMode: "save", instance: DEFAULT_INST, instanceParams: EVERY_PARAM });
  ok(`elevate: the DEFAULT instance still elevates (zero-change) (${action})`, defaultInvE.elevate === true);
  ok(`elevate: no instance at all still elevates (zero-change) (${action})`,
    life.buildInvocation(action, { settings: {}, backupMode: "save" }).elevate === true);
}
// setCheckpoints never reaches a remote instance (drivers/index.js refuses it on the
// checkpoints capability), so its elevation is unchanged.
ok("elevate: setCheckpoints is untouched by the backend rule",
  life.buildInvocation("setCheckpoints", { settings: {}, enabled: true }).elevate === true);

// ── run(): the CAPTURED TARGET is re-verified on the other side of the modal ──
// The destructive confirmation opens INSIDE run(). Callers check their captured
// generation BEFORE calling it, so between that check and the launch there is an await
// the user can leave open indefinitely — long enough for another window to change the
// global selection or rewrite the registry. Accepting then used to delete and rebuild the
// instance this window had already left. Driven here through the REAL run(), with the
// same test seams launchHostScript has, and with the confirmation held on a deferred
// promise so the "switch" lands exactly while the modal is open.
console.log("\n=== run(): the confirmation is an await, and this is what is on the far side ===");

// A scripts dir whose two scripts DECLARE every parameter a rebuild needs, so the
// capability gates pass and the flow reaches the confirmation.
const runDir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-lifecycle-run-"));
fs.writeFileSync(path.join(runDir, life.AUTO_INSTALL),
  "param(\n" + EVERY_PARAM.map((p2) => "  [string]$" + p2 + ",").join("\n") +
  "\n  [string]$Action,\n  [string]$BackupMode,\n  [string]$Projects,\n  [bool]$AutomaticCheckpoints\n)\n", "utf8");
fs.writeFileSync(path.join(runDir, life.PROVISION),
  "param(\n" + EVERY_PARAM.map((p2) => "  [string]$" + p2 + ",").join("\n") + "\n  [string]$Action\n)\n", "utf8");
fs.writeFileSync(path.join(runDir, ".construct-settings.json"), JSON.stringify({ autoCheckpoints: true }), "utf8");

function deferred() {
  let resolve;
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
}

/** One run() through the seams. `stillCurrent` is the caller's captured-target predicate
 *  (omitted = the pre-fix behaviour: nothing is re-checked). */
function runRebuild(opts) {
  const launches = [];
  const warned = [];
  const modal = deferred();
  const fakeVs = {
    window: {
      showInformationMessage() {},
      showErrorMessage() {},
      showWarningMessage(title, options, action) {
        // The destructive confirm is the 3-argument modal form; anything else is a
        // plain warning toast, which must not be mistaken for an answer.
        if (action === undefined) { warned.push(String(title)); return Promise.resolve(undefined); }
        return modal.promise.then((answer) => (answer ? action : undefined));
      },
    },
  };
  const started = life.run(opts.action || "reinstall", {
    scriptsDir: runDir, backupMode: "save", projects: [], instance: opts.instance,
    stillCurrent: opts.stillCurrent,
    _vscode: fakeVs, _platform: "win32",
    _spawn: (file, args, options) => { launches.push({ file, args, options }); return { on() {}, unref() {} }; },
  });
  return { started, launches, warned, modal, settled: () => life.runSettled(), vscode: fakeVs };
}

(async () => {
  // (a) THE BUG, with a PRE-FIX CONTROL. No predicate = nothing to re-check, and the
  //     accept launches the rebuild — exactly what shipped, and exactly the failure.
  const preFix = runRebuild({ instance: LOCAL_INST });
  ok("run(control): the invocation is under way (the modal is open)", preFix.started === true);
  ok("run(control): nothing has launched while the modal sits open", preFix.launches.length === 0);
  preFix.modal.resolve(true);
  await preFix.settled();
  ok("run(control): WITHOUT a captured-target predicate the accept launches the rebuild",
    preFix.launches.length === 1);

  // ...and the same accept, with the predicate reporting that the window moved on.
  let stale = false;
  const guarded = runRebuild({ instance: LOCAL_INST, stillCurrent: () => !stale });
  stale = true;                                  // the switch happens WHILE the modal is open
  guarded.modal.resolve(true);
  await guarded.settled();
  ok("run(): a confirmed rebuild is ABANDONED when the captured target went stale",
    guarded.launches.length === 0);

  // (b) THE POSITIVE CONTROL: nothing changed, so the accept still rebuilds.
  const live = runRebuild({ instance: LOCAL_INST, stillCurrent: () => true });
  live.modal.resolve(true);
  await live.settled();
  ok("run(): with the target still current the rebuild launches as before", live.launches.length === 1);

  // (c) A DECLINED confirmation launches nothing either way (and never asks the predicate).
  let asked = 0;
  const declined = runRebuild({ instance: LOCAL_INST, stillCurrent: () => { asked++; return true; } });
  declined.modal.resolve(false);
  await declined.settled();
  ok("run(): a declined confirmation launches nothing", declined.launches.length === 0);
  ok("run(): ...and never even asks whether the target is current", asked === 0);

  // (d) A THROWING predicate fails CLOSED — nothing is deleted on an unusable answer.
  const boom = runRebuild({ instance: LOCAL_INST, stillCurrent: () => { throw new Error("gate exploded"); } });
  boom.modal.resolve(true);
  await boom.settled();
  ok("run(): an unusable predicate fails closed (nothing launches)", boom.launches.length === 0);

  // (e) The marker clear is on the SAME side of the check: an abandoned rebuild must not
  //     wipe the applied-checkpoints marker of the instance it did not rebuild.
  fs.writeFileSync(path.join(runDir, ".construct-settings.json"),
    JSON.stringify({ autoCheckpoints: true, vmAutoCheckpointsApplied: true }), "utf8");
  const abandoned = runRebuild({ instance: LOCAL_INST, stillCurrent: () => false });
  abandoned.modal.resolve(true);
  await abandoned.settled();
  const after = JSON.parse(fs.readFileSync(path.join(runDir, ".construct-settings.json"), "utf8"));
  ok("run(): an abandoned rebuild leaves the applied-checkpoints marker alone",
    after.vmAutoCheckpointsApplied === true && abandoned.launches.length === 0);

  // ── The confirmation TEXT names the instance for a non-default one ─────────
  console.log("\n=== the destructive confirmation names which VM it is about ===");
  const asked2 = [];
  const askVscode = (answer) => ({
    window: {
      showInformationMessage() {}, showErrorMessage() {},
      showWarningMessage(title, options, action) {
        asked2.push({ title, detail: options && options.detail });
        return Promise.resolve(answer ? action : undefined);
      },
    },
  });
  const inv = life.buildInvocation("reinstall", { settings: {}, backupMode: "save", instance: DEFAULT_INST, instanceParams: EVERY_PARAM });
  await life.confirmDestructive(inv, DEFAULT_INST, askVscode(false));
  ok("confirm: the DEFAULT instance's dialog is byte-identical to the single-VM one",
    asked2[0].title === "Reinstall the Construct VM?" &&
    asked2[0].detail === "This DELETES the VM and its virtual disk, then rebuilds and reinstalls from the current ISO. This is irreversible and cannot be undone.");
  await life.confirmDestructive(inv, undefined, askVscode(false));
  ok("confirm: ...and so is the one for no instance at all",
    asked2[1].title === asked2[0].title && asked2[1].detail === asked2[0].detail);
  await life.confirmDestructive(inv, LOCAL_INST, askVscode(false));
  ok("confirm: a non-default LOCAL instance is named in the title",
    asked2[2].title.includes("work-vm") && asked2[2].title.startsWith("Reinstall the Construct VM"));
  ok("confirm: ...without an endpoint (a local VM's address is derived from its name)",
    !asked2[2].detail.includes("mshome.net"));
  await life.confirmDestructive(inv, REMOTE_INST, askVscode(false));
  ok("confirm: a REMOTE instance is named AND located (it lives on somebody else's host)",
    asked2[3].title.includes("work-vm") &&
    asked2[3].detail.includes("buildbox.example.local:2201") &&
    asked2[3].detail.includes("This is irreversible and cannot be undone."));
  const redown = life.buildInvocation("redownload", { settings: {}, backupMode: "save", instance: DEFAULT_INST, instanceParams: EVERY_PARAM });
  await life.confirmDestructive(redown, DEFAULT_INST, askVscode(false));
  ok("confirm: Redownload's default-instance dialog is unchanged too",
    asked2[4].title === "Redownload the Construct VM?" &&
    asked2[4].detail.startsWith("This DELETES the VM and its virtual disk, re-downloads the Ubuntu ISO"));

  // ── B14: Remove instance ────────────────────────────────────────────────────
  const rm = life.buildInvocation("removeInstance", { instance: LOCAL_INST, confirmation: "" });
  ok("remove: it drives Auto-Install.ps1 -Action remove-instance",
    rm && rm.script === "Auto-Install.ps1" &&
    rm.args.join(" ").indexOf("-Action remove-instance -InstanceName work-vm") >= 0);
  ok("remove: it NEVER elevates (it edits the real user's profile, not Hyper-V)",
    rm.elevate === false);
  ok("remove: it is not a 'destructive' confirm-modal action (the panel already asked)",
    rm.destructive === false && rm.label === "Remove instance");
  ok("remove: -NonInteractive is not passed (Auto-Install.ps1 has no such parameter)",
    rm.args.indexOf("-NonInteractive") < 0 && rm.args.indexOf("-FromPanel") >= 0);
  const rmConfirmed = life.buildInvocation("removeInstance", { instance: REMOTE_INST, confirmation: "work-vm" });
  ok("remove: the typed confirmation rides along for a remote instance",
    rmConfirmed.args.join(" ").indexOf("-ConfirmInstanceName work-vm") >= 0);
  // The launcher only refuses a request with no NAME: which names may be removed is the
  // planner's question (and the script's), because it needs the registry to answer it —
  // `agent-vm` is removable once another instance is the default.
  ok("remove: a request with no instance is refused",
    life.buildInvocation("removeInstance", {}) === null &&
    life.buildInvocation("removeInstance", { instance: { backend: "hyperv-local" } }) === null);
  ok("remove: a request that names agent-vm still builds (the planner decides)",
    life.buildInvocation("removeInstance", { instance: DEFAULT_INST }).args.join(" ")
      .indexOf("-InstanceName agent-vm") >= 0);
  // The capability probe: -Action lives in a [ValidateSet], so an older scripts dir
  // rejects the value at binding time and the action must not be offered at all.
  const probeDir = fs.mkdtempSync(path.join(os.tmpdir(), "b14-probe-"));
  fs.writeFileSync(path.join(probeDir, "Auto-Install.ps1"),
    'param(\n  [ValidateSet("reprovision", "reinstall")]\n  [string]$Action\n)\n');
  ok("remove: an older Auto-Install.ps1 does not support the action",
    life.scriptSupportsRemoveInstance(probeDir) === false);
  fs.writeFileSync(path.join(probeDir, "Auto-Install.ps1"),
    'param(\n  # remove-instance in a comment must not answer the question\n' +
    '  [ValidateSet("reprovision", "remove-instance")]\n  [string]$Action\n)\n');
  ok("remove: a scripts dir whose ValidateSet names it does",
    life.scriptSupportsRemoveInstance(probeDir) === true);
  fs.writeFileSync(path.join(probeDir, "Auto-Install.ps1"),
    'param(\n  # -Action remove-instance\n  [ValidateSet("reprovision")]\n  [string]$Action\n)\n');
  ok("remove: a mention in a COMMENT never answers the probe",
    life.scriptSupportsRemoveInstance(probeDir) === false);
  ok("remove: no scripts dir answers false", life.scriptSupportsRemoveInstance("") === false);
  ok("remove: THIS repo's Auto-Install.ps1 supports it",
    life.scriptSupportsRemoveInstance(path.join(__dirname, "..", "..")) === true);
  try { fs.rmSync(probeDir, { recursive: true, force: true }); } catch (_) {}

  try { fs.rmSync(runDir, { recursive: true, force: true }); } catch (_) {}
  console.log(`\n  lifecycle launcher unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
