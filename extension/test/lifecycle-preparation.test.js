"use strict";
const assert = require("assert");
const fs = require("fs");
const vm = require("vm");
const source = fs.readFileSync(require("path").join(__dirname, "../extension.js"), "utf8");
const helper = source.slice(source.indexOf("async function preparePanelLifecycle("), source.indexOf("function handleMessage("));
function sandboxFor(busy) {
  const calls = [], messages = [], errors = [];
  const sandbox = {
    safePost: (_view, m) => messages.push(m), vscode: { window: { showErrorMessage: (m) => errors.push(m) } },
    beginLifecycleRun: (action) => { calls.push("begin:" + action); return busy ? { refused: busy } : { end: () => calls.push("end") }; },
    lifecycle: { runSettled: () => { calls.push("settled"); return Promise.resolve(); } },
  };
  vm.runInNewContext(helper, sandbox);
  return { sandbox, calls, messages, errors };
}
(async () => {
  for (const outcome of ["ready", "blocked", "failed"]) {
    const { sandbox, calls, messages, errors } = sandboxFor(null);
    let release;
    const wait = new Promise((resolve) => { release = resolve; });
    const pending = sandbox.preparePanelLifecycle({}, "customReinstall", async (launched) => {
      assert.equal(typeof launched, "function", "the work can end the claim at its launch");
      await wait;
      if (outcome === "failed") throw new Error("SSH failed");
      if (outcome === "blocked") return;
    });
    assert.equal(messages.length, 0, "busy remains during preparation");
    assert.deepEqual(calls, ["begin:reinstall"], "the launch claims the host-wide run before it prepares");
    release(); await pending;
    assert.equal(messages.length, 1);
    assert.equal(messages[0].type, "lifecyclePrepared");
    assert.equal(messages[0].id, "customReinstall");
    assert.equal(messages[0].error, outcome === "failed" ? "SSH failed" : undefined);
    assert.equal(errors.length, outcome === "failed" ? 1 : 0);
    assert.deepEqual(calls, outcome === "failed" ? ["begin:reinstall", "end"] : ["begin:reinstall", "settled", "end"], "the run is released after the confirmation and launch, whatever happened");
  }
  // Another run holds the host: refused with its reason, nothing prepared or claimed.
  const refused = sandboxFor("Reprovision of \u201cwork-vm\u201d is running. Wait until it finishes, then try again.");
  let worked = false;
  await refused.sandbox.preparePanelLifecycle({}, "reprovision", async () => { worked = true; });
  assert.equal(worked, false);
  assert.deepEqual(refused.messages, [{ type: "lifecyclePrepared", id: "reprovision", error: "Reprovision of \u201cwork-vm\u201d is running. Wait until it finishes, then try again." }]);
  assert.deepEqual(refused.calls, ["begin:reprovision"]);
  // Opening the console is not a lifecycle run: never refused or claimed.
  const console_ = sandboxFor("busy");
  await console_.sandbox.preparePanelLifecycle({}, "openConsole", async () => {});
  assert.deepEqual(console_.calls, []);
  assert.deepEqual(console_.messages, [{ type: "lifecyclePrepared", id: "openConsole" }]);
  console.log("Lifecycle preparation: completion, early return, exception recovery and the host-wide run passed.");
})().catch((e) => { console.error(e); process.exitCode = 1; });
