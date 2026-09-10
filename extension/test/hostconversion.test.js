"use strict";
const assert = require("node:assert/strict");
const { test } = require("node:test");
const conversion = require("../src/hostconversion");
const instances = require("../src/instances");
const fs = require("fs");
const os = require("os");
const path = require("path");

test("opening VS Code only reports saved conversion results; dismissing never enrolls", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "conversion-notice-"));
  const prior = process.env.LOCALAPPDATA;
  process.env.LOCALAPPDATA = dir;
  try {
    fs.mkdirSync(path.dirname(conversion.pendingPath()), { recursive: true });
    const resultPath = path.join(dir, "result.json");
    fs.writeFileSync(conversion.pendingPath(), JSON.stringify({ id: "pending", name: "agent-vm", resultPath }));
    fs.writeFileSync(resultPath, JSON.stringify({ ok: true }));
    let tick;
    const notices = [];
    const watch = conversion.watchPending({ refresh() {}, vscode: { window: {
      showInformationMessage: async (...args) => { notices.push(args); },
      showErrorMessage: async (...args) => { notices.push(args); },
    } }, context: { secrets: { get() { throw new Error("must not enroll silently"); } } } },
    { platform: "win32", setInterval: cb => { tick = cb; }, clearInterval() {} });
    await tick(); await tick();
    assert.equal(notices.length, 1);
    assert.equal(notices[0][1], "Finish host conversion");
    assert.equal(conversion.pendingStatus("agent-vm").ready, true);
    assert.equal(conversion.pendingStatus("other-vm"), null);
    fs.writeFileSync(resultPath, JSON.stringify({ ok: false, error: "Guest enrollment failed" }));
    await tick();
    assert.match(notices[1][0], /Guest enrollment failed/);
    assert.equal(conversion.pendingStatus("agent-vm").ready, false);
    assert.equal(fs.existsSync(conversion.pendingPath()), true);
    watch.dispose();
  } finally {
    if (prior === undefined) delete process.env.LOCALAPPDATA; else process.env.LOCALAPPDATA = prior;
    fs.rmSync(dir, { recursive: true });
  }
});

test("conversion is only offered on the connected local Windows VM", () => {
  assert.equal(conversion.eligible(instances.DEFAULT_INSTANCE, true, "win32"), true);
  assert.equal(conversion.eligible(instances.DEFAULT_INSTANCE, false, "win32"), false);
  assert.equal(conversion.eligible(instances.DEFAULT_INSTANCE, true, "linux"), false);
  assert.equal(conversion.eligible({ backend: "hyperv-remote" }, true, "win32"), false);
});
test("hostnames cannot carry shell syntax or URLs", () => {
  for (const host of ["main-pc", "main-pc.home.test", "192.168.1.2"]) assert.equal(conversion.validHost(host), true);
  for (const host of ["", "https://main-pc", "a;calc", "a$(calc)", "a..b", "a\nb", "-flag"]) assert.equal(conversion.validHost(host), false);
});
test("adoption preserves SSH identity, config branch, other instances, and rejects stale results", () => {
  const registry = instances.parseRegistry(JSON.stringify({ version: 1, instances: {} })).registry;
  const original = instances.resolve(registry, "agent-vm");
  const plan = { id: "test", name: "agent-vm", adminUser: "PC\\alice", publicHost: "main-pc", fingerprint: instances.targetFingerprint(original) };
  const result = { ok: true, id: "test", name: "agent-vm", owner: plan.adminUser, publicHost: "main-pc", url: "https://main-pc:7462", sshPort: 2201 };
  const next = conversion.convertedRegistry(registry, plan, result);
  const adopted = instances.resolve(next, "agent-vm");
  assert.equal(adopted.backend, "hyperv-remote");
  assert.equal(adopted.vmName, "agent-vm");
  for (const field of ["vmHost", "sshPort", "hostAlias", "keyName", "configBranch"]) assert.equal(adopted[field], original[field]);
  assert.deepEqual(conversion.convertedRegistry(next, plan, result), next);
  assert.throws(() => conversion.convertedRegistry(registry, plan, { ...result, name: "other-vm" }));
  const changed = instances.updateInstance(registry, "agent-vm", { scriptsDir: "C:\\Different" });
  assert.throws(() => conversion.convertedRegistry(changed, plan, result), /changed/);
});
test("elevated launch transports inert input and cannot ask late questions", () => {
  const plan = { scriptsDir: "C:\\Chris's Construct", adminUser: "PC\\alice", name: "agent-vm" };
  const command = conversion.launchScript(plan);
  assert.match(command, /-Verb RunAs -Wait -PassThru/);
  assert.match(command, /-NonInteractive/);
  const inner = Buffer.from(command.match(/-EncodedCommand ([A-Za-z0-9+/=]+)/)[1], "base64").toString("utf16le");
  assert.match(inner, /Chris''s Construct/);
  const decoded = JSON.parse(Buffer.from(inner.match(/-PlanB64 '([^']+)'/)[1], "base64").toString());
  assert.deepEqual(decoded, plan);
});
