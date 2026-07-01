"use strict";
// Plain-node unit tests for the Hyper-V power helpers. The pure builders
// (buildStateProbeCommand/Launch, parseVmState, buildStartCommand,
// buildElevatedCommandLaunch) need nothing; queryVmState is exercised through an
// injected fake spawn (_spawn/_platform seams) so no real powershell runs. startVm
// needs vscode and is not exercised here. Run: node vmpower.test.js
const { EventEmitter } = require("events");
const vm = require("../src/vmpower");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// A fake child_process.spawn: emits the scripted stdout + close (or error) on the
// next tick, after the promise's listeners are attached. `neverClose` simulates a
// wedged process (drives the timeout path). Records the argv it was handed.
function fakeSpawn(behavior, sink) {
  return function (file, args) {
    if (sink) { sink.called = true; sink.file = file; sink.args = args; }
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.kill = () => { child.killed = true; };
    setImmediate(() => {
      if (behavior.error) { child.emit("error", new Error("spawn boom")); return; }
      if (behavior.data != null) child.stdout.emit("data", Buffer.from(behavior.data));
      if (!behavior.neverClose) child.emit("close", behavior.closeCode == null ? 0 : behavior.closeCode);
    });
    return child;
  };
}
const query = (behavior, sink, extra) =>
  vm.queryVmState({ _platform: "win32", _spawn: fakeSpawn(behavior, sink), ...(extra || {}) });

// ── parseVmState ─────────────────────────────────────────────────────────────
ok("parse: Running -> running", vm.parseVmState("VMSTATE=Running\n") === "running");
ok("parse: Off -> off", vm.parseVmState("VMSTATE=Off") === "off");
ok("parse: Saved -> off (resumable)", vm.parseVmState("VMSTATE=Saved") === "off");
ok("parse: Paused -> off (resumable)", vm.parseVmState("VMSTATE=Paused") === "off");
ok("parse: absent -> absent", vm.parseVmState("VMSTATE=absent") === "absent");
ok("parse: unknown token -> unknown", vm.parseVmState("VMSTATE=unknown") === "unknown");
ok("parse: transient (Starting) -> unknown", vm.parseVmState("VMSTATE=Starting") === "unknown");
ok("parse: no token -> unknown", vm.parseVmState("garbage output") === "unknown");
ok("parse: empty -> unknown", vm.parseVmState("") === "unknown");
ok("parse: case-insensitive RUNNING", vm.parseVmState("VMSTATE=RUNNING") === "running");
ok("parse: ignores surrounding noise", vm.parseVmState("warn: x\nVMSTATE=Off\n") === "off");

// ── buildStateProbeCommand ───────────────────────────────────────────────────
const probeCmd = vm.buildStateProbeCommand();
ok("probe cmd: queries the right VM", probeCmd.includes("Get-VM -Name 'Agent-VM'"));
ok("probe cmd: maps InvalidParameter -> absent",
  probeCmd.includes("$_.FullyQualifiedErrorId -like 'InvalidParameter*'") && probeCmd.includes("Write-Output 'VMSTATE=absent'"));
ok("probe cmd: other failures -> unknown", probeCmd.includes("VMSTATE=unknown"));
ok("probe cmd: -ErrorAction Stop so the catch fires", probeCmd.includes("-ErrorAction Stop"));
ok("probe cmd: a name with a quote is PS-escaped", vm.buildStateProbeCommand("O'Brien-VM").includes("'O''Brien-VM'"));

// ── buildStateProbeLaunch ────────────────────────────────────────────────────
const pl = vm.buildStateProbeLaunch();
ok("probe launch: powershell.exe", pl.file === "powershell.exe");
ok("probe launch: uses -EncodedCommand, not -Command", pl.spawnArgs.includes("-EncodedCommand") && !pl.spawnArgs.includes("-Command"));
ok("probe launch: -NonInteractive (no prompts)", pl.spawnArgs.includes("-NonInteractive"));
ok("probe launch: base64 decodes (utf16le) back to the command",
  Buffer.from(pl.spawnArgs[pl.spawnArgs.length - 1], "base64").toString("utf16le") === pl.command);

// ── buildStartCommand / buildElevatedCommandLaunch ───────────────────────────
ok("start cmd: starts the right VM", vm.buildStartCommand().includes("Start-VM -Name 'Agent-VM'"));
ok("start cmd: reports success/failure", vm.buildStartCommand().includes("$?"));
// On SUCCESS the console closes (no pause); only FAILURE pauses (readable) — so the
// window exits cleanly on a good start instead of leaving an interactive prompt.
ok("start cmd: pauses only on failure (Read-Host after the failure branch)", /Failed to start[\s\S]*Read-Host/.test(vm.buildStartCommand()));
const el = vm.buildElevatedCommandLaunch(vm.buildStartCommand());
ok("elevated: outer is Start-Process RunAs", el.command.includes("Start-Process") && el.command.includes("-Verb RunAs"));
// Regression: the elevated launcher is spawned detached (no console), so the outer
// command must request a VISIBLE window or the inner powershell inherits "no console"
// and Start-VM runs windowless — the same "toast fires, nothing happens" symptom the
// lifecycle buttons had. Assert -WindowStyle Normal coexists with -Verb RunAs.
ok("elevated: requests a visible window (-WindowStyle Normal)", el.command.includes("-WindowStyle Normal"));
ok("elevated: child runs an inline -Command (not -File <script>)", el.command.includes('-Command "Start-VM'));
// NO -NoExit by default: the Start-VM console EXITS instead of dropping to an interactive
// prompt (the reported bug). Debug (keepOpen) adds it back so errors stay readable.
ok("elevated: no -NoExit by default (console exits)", !el.command.includes("-NoExit"));
const elDbg = vm.buildElevatedCommandLaunch(vm.buildStartCommand(), { keepOpen: true });
ok("elevated: keepOpen (debug) adds -NoExit", elDbg.command.includes("-NoExit"));
// psSingleQuote wraps the child line in a PS single-quoted literal, so the inner
// 'Agent-VM' quotes are doubled — assert the actually-escaped form.
ok("elevated: carries the inner Start-VM command (quotes PS-escaped)", el.command.includes("Start-VM -Name ''Agent-VM''"));
ok("elevated: uses -EncodedCommand", el.spawnArgs.includes("-EncodedCommand"));
// Regression: launch through `cmd /c start` so the elevated Start-Process actually gets
// a console/UAC window (VS Code's extension host is console-less; a plain powershell spawn
// opens nothing — the same bug the lifecycle buttons had).
ok("elevated: spawns via cmd.exe /c start", el.file === "cmd.exe" && el.spawnArgs[1] === "start" && el.spawnArgs[3] === "powershell.exe");
ok("elevated: base64 decodes (utf16le) back to the outer command",
  Buffer.from(el.spawnArgs[el.spawnArgs.length - 1], "base64").toString("utf16le") === el.command);

// ── shouldShowStart (the "Start & connect" gate the webviews inline) ─────────
// Regression: a stopped VM whose non-elevated Get-VM probe is permission-denied
// reads back 'unknown', not 'off'. The button MUST still show for 'unknown' (the
// Start action self-elevates), otherwise it's hidden forever until the user signs
// out/in for the Hyper-V Administrators membership to take effect.
ok("gate: offline + off -> show", vm.shouldShowStart(false, "off") === true);
ok("gate: offline + unknown -> show (permission-denied probe)", vm.shouldShowStart(false, "unknown") === true);
ok("gate: offline + absent -> hide (VM not installed)", vm.shouldShowStart(false, "absent") === false);
ok("gate: offline + running -> hide (contradiction; prefer no Start)", vm.shouldShowStart(false, "running") === false);
ok("gate: online + off -> hide (reachable = running)", vm.shouldShowStart(true, "off") === false);
ok("gate: online + unknown -> hide", vm.shouldShowStart(true, "unknown") === false);
ok("gate: offline + undefined vmState -> show (no probe yet)", vm.shouldShowStart(false, undefined) === true);

// ── constants ────────────────────────────────────────────────────────────────
ok("VM_NAME is Agent-VM", vm.VM_NAME === "Agent-VM");
ok("SHUTDOWN_CMD returns immediately (--no-block)", vm.SHUTDOWN_CMD === "systemctl poweroff --no-block");

// ── queryVmState (injected spawn) ────────────────────────────────────────────
(async () => {
  ok("query: Running stdout -> running", (await query({ data: "VMSTATE=Running\n" })) === "running");
  ok("query: Off stdout -> off", (await query({ data: "VMSTATE=Off\n" })) === "off");
  ok("query: absent stdout -> absent", (await query({ data: "VMSTATE=absent\n" })) === "absent");
  ok("query: no stdout + clean close -> unknown", (await query({ data: null, closeCode: 0 })) === "unknown");
  ok("query: spawn 'error' event -> unknown", (await query({ error: true })) === "unknown");
  ok("query: wedged process times out -> unknown", (await query({ neverClose: true }, null, { timeoutMs: 30 })) === "unknown");

  // The probe spawn is handed the encoded Get-VM command.
  const sink = {};
  await query({ data: "VMSTATE=Off\n" }, sink);
  const decoded = Buffer.from(sink.args[sink.args.length - 1], "base64").toString("utf16le");
  ok("query: spawns the encoded Get-VM probe", sink.called && sink.file === "powershell.exe" && decoded.includes("Get-VM -Name 'Agent-VM'"));

  // Off-Windows never spawns — it can't run powershell — and resolves 'unknown'.
  const offSink = {};
  const offRes = await vm.queryVmState({ _platform: "linux", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, offSink) });
  ok("query: off-Windows resolves unknown without spawning", offRes === "unknown" && !offSink.called);

  // A spawn that throws synchronously is caught -> unknown.
  const throwRes = await vm.queryVmState({ _platform: "win32", _spawn: () => { throw new Error("no exe"); } });
  ok("query: spawn throw -> unknown", throwRes === "unknown");

  console.log(`\n  vmpower unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
