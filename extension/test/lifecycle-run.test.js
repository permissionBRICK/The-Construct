"use strict";
// The host-wide lifecycle run in the extension (legacy fallback): the real extension.js code for the run state
// and the Construct update, run against a marker in a temporary LOCALAPPDATA. The launch's own claim ends at
// launch, so a console closed without a result unlocks every surface at once.
const assert = require("assert");
const cp = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const vm = require("vm");
const source = fs.readFileSync(path.join(__dirname, "../extension.js"), "utf8");
const slice = (from, to) => source.slice(source.indexOf(from), source.indexOf(to, source.indexOf(from)));
const code = slice("// ── One lifecycle run per host", "// ── VM → desktop notifications") + slice("function runUpdateConstruct(", "const delay = (ms)");

const root = fs.mkdtempSync(path.join(os.tmpdir(), "lifecycle-run-"));
const saved = process.env.LOCALAPPDATA;
process.env.LOCALAPPDATA = root;
try {
  const lifecycle = { ...require("../src/lifecycle"), launchHostScript: () => true };
  const posted = [], polls = [];
  const sandbox = {
    lifecycle, fs, os, path, Date, liveWebviews: new Set([{}]), safePost: (_w, m) => posted.push(m),
    activeInstance: () => ({ name: "agent-vm" }), resolveScriptsDir: () => root, warnNoScriptsDir: () => {}, logLine: () => {},
    updates: { readMarkers: () => ({}), constructRefreshArgs: () => [], constructRefreshArgPairs: () => [] }, host: { readRawSettings: () => ({}) },
    vscode: { window: { showInformationMessage() {}, showWarningMessage() {} }, commands: { executeCommand() {} } },
    setInterval: (fn) => { polls.push(fn); return polls.length; }, clearInterval() {},
  };
  vm.runInNewContext(code, sandbox);
  const marker = lifecycle.runMarkerPath();
  const hold = (pid) => { fs.mkdirSync(path.dirname(marker), { recursive: true }); fs.writeFileSync(marker, JSON.stringify({ pid, instance: "", action: "updateConstruct", startedAt: "2026-10-17T10:00:00Z", token: "t" })); };
  const phases = () => posted.filter((m) => m.type === "lifecycleRun").map((m) => m.run && m.run.phase);

  // The click claims the host; the launch ends the claim; the script's marker holds the run.
  const claim = sandbox.beginLifecycleRun("updateConstruct", null);
  assert.equal(claim.refused, undefined);
  assert.equal(sandbox.beginLifecycleRun("reprovision").refused, "Construct update is starting. Wait until it finishes, then try again.");
  sandbox.runUpdateConstruct(claim.end);
  assert.deepEqual(phases(), ["preparing", null]);
  hold(process.pid); sandbox.publishLifecycleRun();
  assert.equal(sandbox.beginLifecycleRun("reinstall").refused, "Construct update is running. Wait until it finishes, then try again.");
  // The console is closed without writing a result: its process is gone, every surface unlocks on the next poll.
  const gone = cp.spawnSync(process.execPath, ["-e", "process.stdout.write(String(process.pid))"]).stdout.toString();
  hold(Number(gone)); sandbox.publishLifecycleRun();
  assert.deepEqual(phases(), ["preparing", null, "running", null]);
  assert.equal(polls.length, 1, "the result is still awaited");
  claim.end();   // late or repeated: ends nothing else
  const next = sandbox.beginLifecycleRun("reprovision");
  claim.end();
  assert.equal(sandbox.currentLifecycleRun().phase, "preparing", "an old claim's end leaves a newer launch alone");
  next.end();
  assert.equal(sandbox.currentLifecycleRun(), null);
  console.log("Lifecycle run: claim, launch, console closed without a result and stale ends passed.");
} finally {
  if (saved === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = saved;
  fs.rmSync(root, { recursive: true, force: true });
}
