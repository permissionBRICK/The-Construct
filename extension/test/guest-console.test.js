"use strict";
const assert = require("assert/strict");
const { openGuestConsole, buildConsoleScript } = require("../src/guest-console");
const instances = require("../src/instances");

(async () => {
  const inst = instances.deriveDefaults("work-vm", { backend: "hyperv-remote", sshHost: "host.example", sshPort: 2207 });
  const calls = [], opened = [];
  let result = { code: 0, stdout: "https://localhost:6080/#ticket=fresh\n" };
  const opts = {
    _ssh: { runRemoteScript: async (script, options) => { calls.push({ script, options }); return result; } },
    _vscode: {
      ProgressLocation: { Notification: 15 }, window: { withProgress: async (_, action) => action() },
      Uri: { parse: value => value }, env: { openExternal: async url => { opened.push(url); return true; } },
    },
  };
  await openGuestConsole(inst, "shared-win11", opts);
  await openGuestConsole(inst, "shared-win11", opts);
  assert.equal(calls.length, 2, "every click mints a fresh ticket");
  assert.equal(calls[0].script, "construct vm console 'shared-win11' --web\n");
  assert.deepEqual(calls[0].options.cfg, instances.toSshCfg(inst), "SSH targets the captured primary");
  assert.equal(opened.length, 2);
  for (const name of ["x'; touch /tmp/pwn; #", "--help", "x\nnext"]) assert.throws(() => buildConsoleScript(name));
  for (const stdout of ["javascript:alert(1)", "https://localhost:6080/", "https://user:password@localhost/#secret", "https://localhost/#one\nhttps://localhost/#two"]) {
    result = { code: 0, stdout };
    await assert.rejects(openGuestConsole(inst, "shared-win11", opts));
  }
  result = { code: 1, stderr: "console-forbidden" };
  await assert.rejects(openGuestConsole(inst, "shared-win11", opts), /console-forbidden/);
  assert.equal(opened.length, 2, "failures never open a guessed browser URL");
  console.log("Guest console fresh links, SSH targeting, input validation and failures passed");
})().catch(e => { console.error(e); process.exitCode = 1; });
