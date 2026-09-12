"use strict";
const assert = require("assert");
const fs = require("fs");
const vm = require("vm");
const source = fs.readFileSync(require("path").join(__dirname, "../extension.js"), "utf8");
const helper = source.slice(source.indexOf("async function preparePanelLifecycle("), source.indexOf("function handleMessage("));
(async () => {
  for (const outcome of ["ready", "blocked", "failed"]) {
    const messages = [], errors = [];
    const sandbox = { safePost: (_view, m) => messages.push(m), vscode: { window: { showErrorMessage: (m) => errors.push(m) } } };
    vm.runInNewContext(helper, sandbox);
    let release;
    const wait = new Promise((resolve) => { release = resolve; });
    const pending = sandbox.preparePanelLifecycle({}, "reinstall", async () => {
      await wait;
      if (outcome === "failed") throw new Error("SSH failed");
      if (outcome === "blocked") return;
    });
    assert.equal(messages.length, 0, "busy remains during preparation");
    release(); await pending;
    assert.equal(messages.length, 1);
    assert.equal(messages[0].type, "lifecyclePrepared");
    assert.equal(messages[0].id, "reinstall");
    assert.equal(messages[0].error, outcome === "failed" ? "SSH failed" : undefined);
    assert.equal(errors.length, outcome === "failed" ? 1 : 0);
  }
  console.log("Lifecycle preparation: completion, early return, and exception recovery passed.");
})().catch((e) => { console.error(e); process.exitCode = 1; });
