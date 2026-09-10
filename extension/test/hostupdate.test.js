"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { createHostUpdater } = require("../src/hostupdate");

function fixture(native = false, initial = null) {
  const calls = [], saved = [];
  let clock = 0;
  const status = { supportsAutoApply: native, installed: { commit: "old" }, current: initial };
  const latest = { commit: "new", releaseTag: "host-new", compatible: true };
  const client = {
    updatesStatus: async () => structuredClone(status),
    updatesCheck: async () => { calls.push(["check"]); return { installed: status.installed, latest }; },
    updatesStage: async body => { calls.push(["stage", body]); status.current = { updateId: "update", state: "checking" }; return { updateId: "update" }; },
    updatesApply: async body => { calls.push(["apply", body]); status.current.state = "draining"; return {}; },
  };
  const options = { client, now: () => clock, save: async p => saved.push(structuredClone(p)) };
  return { updater: createHostUpdater(options), options, status, latest, client, calls, saved, tick: n => { clock += n; } };
}

test("one click uses durable host auto-apply when available", async () => {
  const f = fixture(true);
  await f.updater.start();
  assert.equal(f.calls.filter(c => c[0] === "stage").length, 1);
  assert.equal(f.calls[1][1].autoApply, true);
  assert.equal(f.saved[0].updateId, undefined); // intent precedes the network mutation
  f.status.current.state = "staged";
  await f.updater.refresh();
  assert.equal(f.calls.filter(c => c[0] === "apply").length, 0);
  f.status.current.state = "succeeded";
  f.status.installed.commit = "new";
  await f.updater.refresh();
  assert.equal(f.updater.state.pending, null);
});

test("older hosts apply only after verification, including after a client reload", async () => {
  const f = fixture();
  await f.updater.start();
  await f.updater.refresh();
  assert.equal(f.calls.filter(c => c[0] === "apply").length, 0);
  const resumed = createHostUpdater({ ...f.options, pending: f.saved.at(-1) });
  f.status.current.state = "staged";
  await resumed.refresh();
  await resumed.refresh();
  assert.equal(f.calls.filter(c => c[0] === "apply").length, 1);
  assert.equal(f.calls.find(c => c[0] === "apply")[1].updateId, "update");
});

test("an existing staged package needs only the update button", async () => {
  const f = fixture(false, { updateId: "previous", state: "staged" });
  await f.updater.start();
  assert.deepEqual(f.calls.map(c => c[0]), ["apply"]);
});

test("latest or incompatible releases never start a download", async () => {
  const f = fixture();
  f.latest.commit = "old";
  await f.updater.start();
  assert.deepEqual(f.calls.map(c => c[0]), ["check"]);
  f.latest.commit = "new"; f.latest.compatible = false; f.latest.reasons = ["schema"];
  await assert.rejects(f.updater.start(), /incompatible.*schema/);
  assert.equal(f.updater.state.pending, null);
});

test("release checks are cached, status still refreshes", async () => {
  const f = fixture();
  await f.updater.refresh(); await f.updater.refresh();
  assert.equal(f.calls.length, 1);
  f.tick(15 * 60 * 1000);
  await f.updater.refresh();
  assert.equal(f.calls.length, 2);
});

test("a lost stage response retries with the saved operation key", async () => {
  const f = fixture();
  const stage = f.client.updatesStage;
  f.client.updatesStage = async body => { await stage(body); throw new Error("connection lost"); };
  await assert.rejects(f.updater.start(), /connection lost/);
  f.client.updatesStage = stage;
  const resumed = createHostUpdater({ ...f.options, pending: f.saved.at(-1) });
  await resumed.refresh();
  const stages = f.calls.filter(c => c[0] === "stage");
  assert.equal(stages[0][1].operationKey, stages[1][1].operationKey);
});

test("failed and interrupted stages are not silently applied or restarted", async () => {
  for (const state of ["stageFailed", "cancelled", "interrupted", "recoveryFailed"]) {
    const f = fixture();
    await f.updater.start(); f.status.current.state = state;
    await f.updater.refresh();
    assert.equal(f.updater.state.pending, null);
    assert.equal(f.calls.filter(c => c[0] === "apply").length, 0);
  }
});

test("a refused request stops automatic retries and exposes its error", async () => {
  const f = fixture();
  f.client.updatesStage = async () => { throw Object.assign(new Error("update-in-progress"), { status: 409 }); };
  await assert.rejects(f.updater.start(), /update-in-progress/);
  assert.equal(f.updater.state.pending, null);
  assert.equal(f.updater.state.error, "update-in-progress");
});
