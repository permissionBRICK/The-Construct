"use strict";
const assert = require("node:assert/strict");
const hostadmin = require("../src/hostadmin");

(async () => {
  const calls = [];
  const network = { mode: null, effectiveMode: "direct", address: "10.0.3.50", maySwitchMode: true, maySetAddress: false };
  const client = {
    health: async () => ({ apiFeatures: ["host-admin", "network-mode"] }),
    whoami: async () => ({ known: true, enabled: true, name: "alice", role: "user" }),
    vms: async () => [{ name: "vm", network }],
    vmIdlePolicy: async () => null,
    vmNetwork: async name => { calls.push(["read", name]); return network; },
    setVmNetwork: async (name, body) => { calls.push(["write", name, body]); return network; },
  };
  const model = hostadmin.createHostAdminModel({ client, host: "fixture.invalid", backend: "hyperv-remote" });
  await model.detect();
  assert.equal(model.state.mode, "user");
  assert.deepEqual(model.state.tabs.map(t => t.id), ["vms"]);
  await model.load("vms");
  assert.equal(model.state.vms.rows[0].network.address, "10.0.3.50");
  const loaded = await model.perform("loadVmSettings", { name: "vm" });
  assert.equal(loaded.settings.network.maySwitchMode, true);
  assert.equal((await model.perform("setVmSettings", { name: "vm", network: { mode: "relayed" } })).ok, true);
  assert.deepEqual(calls.at(-1), ["write", "vm", { mode: "relayed" }]);
  assert.equal((await model.perform("saveConfig", {})).ok, false);
  assert.equal(hostadmin.featureSet({ apiFeatures: ["network"] }).networkMode, false);
  console.log("Network mode: owner settings, policy controls, inventory and platform feature checks passed.");
})().catch(error => { console.error(error); process.exitCode = 1; });
