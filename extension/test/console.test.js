"use strict";
const assert = require("assert/strict");
const c = require("../src/console");
const lifecycle = require("../src/lifecycle");
const instances = require("../src/instances");
const handoff = { vmId: "11111111-2222-3333-4444-555555555555", username: "cvltest", domain: "HOST", password: "PRIVATE-password", certificateFingerprint: "sha256:" + Array(32).fill("ab").join(":"), hostAddress: "192.168.1.1", rotated: true };
(async () => {
  assert.equal(c.planConsole({ backend: "hyperv-remote" }, "linux").mode, "remote");
  assert.equal(c.planConsole({}, "win32").mode, "local");
  assert.equal(c.stateFor({}, "linux").supported, false);
  assert.equal(c.stateFor({ backend: "other" }, "win32").supported, false);
  assert.deepEqual(c.parseHandoff(JSON.stringify(handoff)), handoff);
  for (const [field, invalid] of [["vmId", "bad"], ["username", "user/name"], ["domain", ""], ["password", ""], ["certificateFingerprint", "sha256:aa"], ["hostAddress", "localhost"], ["hostAddress", "256.0.0.1"]]) assert.throws(() => c.parseHandoff(JSON.stringify({ ...handoff, [field]: invalid })));
  for (const reason of ["no-credential", "grant-missing", "credential-out-of-sync"]) assert.equal(c.parseHandoff(JSON.stringify({ setupRequired: true, reason })).setupRequired, true);
  const instance = instances.deriveDefaults("work-vm", { backend: "hyperv-remote" });
  const scripts = [], opened = [], reports = [];
  let ticket = 0, status = "ready", live = true, brokerCalls = 0, setups = 0;
  const deps = { platform: "win32", scriptsDir: "C:\\Construct", delay: async () => {}, probeLink: async () => live,
    runHostBroker: async () => { brokerCalls++; return handoff; }, launchSetup: async () => { setups++; },
    _ssh: { runRemoteScript: async (script, opts) => {
      scripts.push({ script, opts });
      if (script === c.buildEnsureGatewayScript()) return { code: 0, stdout: `CONSOLE_GATEWAY=${status}\n` };
      if (script === c.buildCloseForwardScript(18816)) { live = true; return { code: 0 }; }
      return { code: 0, stdout: `http://localhost:18816/#fresh-${++ticket}\n` };
    } },
    _vscode: { ProgressLocation: { Notification: 1 }, Uri: { parse: x => x }, env: { openExternal: async x => { opened.push(x); return true; } }, window: { withProgress: async (_, fn) => fn({ report: x => reports.push(x.message) }), showWarningMessage: async () => "Set up console" } },
  };
  await c.open({ instance }, deps); status = "installed"; await c.open({ instance }, deps);
  assert.equal(ticket, 2); assert.notEqual(opened[0], opened[1]);
  assert.equal(scripts[0].script, c.buildEnsureGatewayScript()); assert.match(scripts[0].script, /bash \/opt\/construct\/repo\/console-viewer\/install.sh/);
  assert.equal(scripts[1].script, c.buildMintScript()); assert.equal(scripts[0].opts.timeoutMs, 300000);
  assert.deepEqual(scripts[1].opts.cfg, instances.toSshCfg(instance));
  live = false; await c.open({ instance }, deps); assert.equal(ticket, 4); assert.equal(scripts.at(-2).script, c.buildCloseForwardScript(18816));
  instance.backend = "hyperv-local";
  await c.open({ instance }, deps);
  assert.equal(brokerCalls, 1); assert.equal(scripts.at(-1).opts.stdin, JSON.stringify(handoff) + "\n"); assert.doesNotMatch(scripts.at(-1).script, /PRIVATE-password/);
  deps.runHostBroker = async () => ++brokerCalls === 2 ? { setupRequired: true, reason: "no-credential" } : handoff;
  await c.open({ instance }, deps); assert.equal(setups, 1);
  deps.targetSuperseded = () => true;
  await assert.rejects(c.open({ instance }, deps), /selected instance changed/); delete deps.targetSuperseded;
  deps._vscode.env.openExternal = async () => { throw new Error("http://localhost/#PRIVATE-secret"); };
  await assert.rejects(c.open({ instance }, deps), e => !e.message.includes("PRIVATE"));
  for (const [step, result, expected] of [["ensure", { stdout: "CONSOLE_GATEWAY=no-docker" }, /Docker/], ["ensure", { stdout: "CONSOLE_GATEWAY=missing-source" }, /checkout/], ["ensure", {}, /Installing/], ["broker", { error: "vm-not-running" }, /not running/], ["broker", { error: "vmconnect-unreachable" }, /2179/], ["broker", { setupRequired: true }, /did not finish/], ["mint", { code: 6 }, /No Construct client/], ["mint", { code: 9 }, /older/], ["mint", { code: -2 }, /SSH/], ["mint", { code: 1, stderr: "Host refused console operation (HTTP 403)" }, /HTTP 403/]]) assert.match(c.mapFailure(step, result), expected);
  assert.doesNotMatch(c.mapFailure("mint", { stderr: "https://host/#PRIVATE-password" }), /PRIVATE/);
  assert.ok(reports.every(x => !x.includes("fresh-") && !x.includes("PRIVATE")));
  const invocation = lifecycle.buildInvocation("consoleAccess", { instance });
  assert.equal(invocation.elevate, true); assert.equal(invocation.script, "Set-AgentVmConsoleAccess.ps1"); assert.ok(invocation.args.includes("-FromPanel")); assert.ok(invocation.args.includes("work-vm"));
  console.log("Primary console backend, ensure/install, handoff, setup, fresh-ticket, stale-forward and failure tests passed.");
})().catch(e => { console.error(e); process.exitCode = 1; });
