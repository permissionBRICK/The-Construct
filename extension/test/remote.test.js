"use strict";
// Plain-node unit tests for the Remote-SSH open helpers (pure parts). No deps.
// openOnVm/hasRemoteSsh need vscode and are not exercised here. Run: node remote.test.js
const remote = require("../src/remote");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── isConnectedToVm ───────────────────────────────────────────────────────────
ok("connected: matches the host alias", remote.isConnectedToVm("ssh-remote+agent-vm") === true);
ok("connected: matches the full hostname", remote.isConnectedToVm("ssh-remote+agent-vm.mshome.net") === true);
ok("connected: case-insensitive", remote.isConnectedToVm("ssh-remote+AGENT-VM") === true);
ok("connected: empty (local window) -> false", remote.isConnectedToVm("") === false);
ok("connected: undefined -> false", remote.isConnectedToVm(undefined) === false);
ok("connected: a different SSH host -> false", remote.isConnectedToVm("ssh-remote+other-box") === false);
ok("connected: non-ssh authority (wsl) -> false", remote.isConnectedToVm("wsl+ubuntu") === false);
ok("connected: honors a cfg alias override", remote.isConnectedToVm("ssh-remote+myvm", { hostAlias: "myvm" }) === true);

// ── remoteFolderUri ──────────────────────────────────────────────────────────
ok("uri: default workspace path", remote.remoteFolderUri(undefined, "/root/repos") === "vscode-remote://ssh-remote+agent-vm/root/repos");
ok("uri: defaults to WORKSPACE_ROOT", remote.remoteFolderUri(undefined) === "vscode-remote://ssh-remote+agent-vm/root/repos");
ok("uri: adds a leading slash", remote.remoteFolderUri(undefined, "root/repos/x") === "vscode-remote://ssh-remote+agent-vm/root/repos/x");
ok("uri: honors a cfg alias override", remote.remoteFolderUri({ hostAlias: "myvm" }, "/root/repos") === "vscode-remote://ssh-remote+myvm/root/repos");

console.log(`\n  remote-open unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
