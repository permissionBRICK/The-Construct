"use strict";
const assert = require("node:assert/strict");
const ha = require("../src/hostadmin");

(async () => {
  const calls = [];
  const response = { window: "today", generatedAt: "2026-09-17T12:00:00Z", totals: { tokens: 1234, costUsd: 1.23 },
    byUser: [{ user: "alice", tokens: 1234, costUsd: 1.23, vms: 1 }],
    byVm: [{ vm: "deleted-vm", user: "alice", deleted: true, tokens: 1234, costUsd: 1.23,
      lastReportedAt: "2026-09-17T11:00:00Z", tools: [{ tool: "claude", tokens: 1234, costUsd: 1.23 }] }] };
  let role = "user";
  const model = ha.createHostAdminModel({ host: "fake.invalid", client: {
    health: async () => ({ apiFeatures: ["host-admin", "usage"] }),
    whoami: async () => ({ name: "alice", known: true, enabled: true, role }),
    hostUsage: async window => { calls.push(window); return { ...response, window }; },
  } });
  await model.detect();
  assert.deepEqual(model.state.tabs.map(t => t.id), ["vms", "usage"]);
  await model.load("usage"); await model.load("usage", "month"); await model.load("usage", "invalid"); await model.load("usage", "all");
  assert.deepEqual(calls, ["today", "month", "month", "all"]);
  assert.equal(model.state.usage.totals.tokens, "1.2K");
  assert.equal(model.state.usage.byVm[0].deleted, true);
  assert.match(model.state.usage.byVm[0].tools, /claude: 1.2K tokens, \$1.23/);
  assert.equal(model.state.usage.byVm[0].lastReported, "2026-09-17 11:00 UTC");
  assert.equal(ha.pollIntervalMs(model.state), 60000);
  await model.load("users"); assert.equal(model.state.users, null);
  role = "admin"; await model.detect(); assert.equal(model.state.usage, null);
  await model.load("usage", "month"); role = "user"; await model.detect(); assert.equal(model.state.usage, null);
  assert.equal(ha.tabsFor({ features: ha.featureSet({ apiFeatures: ["host-admin"] }) }).find(t => t.id === "usage").available, false);
  assert.equal(ha.toVmRow({ tokenUsage: { today: { tokens: 1234 }, month: { tokens: 1234567 }, lastReportedAt: "now" } }, Date.now()).tokenUsage, "1.2K today / 1.2M month");
  assert.equal(ha.toUserRow({ tokens: 2, usageTokensMonth: 1234567 }).usageTokensMonth, "1.2M");
  console.log("Host usage windows, owner access, formatting and feature gating passed.");
})().catch(e => { console.error(e); process.exitCode = 1; });
