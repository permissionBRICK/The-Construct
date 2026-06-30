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

// ── repoNameFromUrl ──────────────────────────────────────────────────────────
ok("name: https with .git", remote.repoNameFromUrl("https://github.com/owner/repo.git") === "repo");
ok("name: https without .git", remote.repoNameFromUrl("https://github.com/owner/repo") === "repo");
ok("name: trailing slash", remote.repoNameFromUrl("https://github.com/owner/repo/") === "repo");
ok("name: scp-like git@host:owner/repo.git", remote.repoNameFromUrl("git@github.com:owner/repo.git") === "repo");
ok("name: ssh:// url", remote.repoNameFromUrl("ssh://git@host:22/owner/repo.git") === "repo");
ok("name: .GIT case-insensitive", remote.repoNameFromUrl("https://x/y/Repo.GIT") === "Repo");
ok("name: empty -> ''", remote.repoNameFromUrl("") === "");
ok("name: nested path segments take the last", remote.repoNameFromUrl("https://h/a/b/c/thing.git") === "thing");
// A ?query / #fragment must be dropped so the derived name (and the folder we open
// after cloning) match where the clone landed — vscode.Uri.parse would otherwise
// split the opened URI at the ? and open a different, nonexistent path.
ok("name: strips a query string", remote.repoNameFromUrl("https://github.com/owner/repo.git?ref=x") === "repo");
ok("name: strips a fragment", remote.repoNameFromUrl("https://h/o/repo.git#frag") === "repo");
ok("name: query on a non-.git url", remote.repoNameFromUrl("https://h/o/repo?x=1") === "repo");
ok("name: derived name carries no URI-significant chars", !/[?#\/\\:]/.test(remote.repoNameFromUrl("https://github.com/owner/repo.git?ref=x#f")));

// ── isLikelyGitUrl ───────────────────────────────────────────────────────────
ok("url ok: https", remote.isLikelyGitUrl("https://github.com/o/r.git") === true);
ok("url ok: http", remote.isLikelyGitUrl("http://h/o/r") === true);
ok("url ok: ssh://", remote.isLikelyGitUrl("ssh://git@h/o/r.git") === true);
ok("url ok: git://", remote.isLikelyGitUrl("git://h/o/r.git") === true);
ok("url ok: scp-like", remote.isLikelyGitUrl("git@github.com:o/r.git") === true);
ok("url bad: empty", remote.isLikelyGitUrl("") === false);
ok("url bad: plain text", remote.isLikelyGitUrl("not a url") === false);
ok("url bad: bare host", remote.isLikelyGitUrl("github.com/o/r") === false);
ok("url bad: scheme with no path", remote.isLikelyGitUrl("https://") === false);

// ── buildCloneScript (injection-safe) ────────────────────────────────────────
const cloneScript = remote.buildCloneScript("https://github.com/o/r.git", "r");
ok("clone: passes url+target as data to git clone --", cloneScript.includes('git clone -- "$url" "$target"'));
ok("clone: guards an existing target (exit 3)", cloneScript.includes("exit 3") && cloneScript.includes('[ -e "$target" ]'));
ok("clone: targets the workspace root", cloneScript.includes("root='/root/repos'"));
ok("clone: uses set -u", cloneScript.split("\n")[0] === "set -u");
// The URL/dest are base64-embedded (never raw in the script text), so a hostile
// URL can neither break the script's own quoting nor inject shell.
const evilUrl = "https://h/x.git'; rm -rf / #";
const evil = remote.buildCloneScript(evilUrl, "x");
ok("clone: raw hostile URL never appears in the script text", !evil.includes("rm -rf /"));
const b64line = evil.split("\n").find((l) => l.startsWith("url=$(printf %s '"));
const b64 = b64line.slice("url=$(printf %s '".length, b64line.indexOf("' | base64 -d)"));
ok("clone: the embedded base64 decodes back to the exact URL", Buffer.from(b64, "base64").toString("utf8") === evilUrl);
ok("clone: base64 payload has no single quote (can't break the literal)", !b64.includes("'"));

// ── surrounding whitespace (regression) ──────────────────────────────────────
// A pasted "  https://…  " passes isLikelyGitUrl + names correctly (both trim), so
// the clone must trim too — else `git clone -- " https://… "` fails on the spaces.
const padded = "  https://github.com/owner/repo.git  ";
ok("ws: padded url still validates", remote.isLikelyGitUrl(padded) === true);
ok("ws: padded url names correctly", remote.repoNameFromUrl(padded) === "repo");
const wsScript = remote.buildCloneScript(padded, remote.repoNameFromUrl(padded));
const wsLine = wsScript.split("\n").find((l) => l.startsWith("url=$(printf %s '"));
const wsB64 = wsLine.slice("url=$(printf %s '".length, wsLine.indexOf("' | base64 -d)"));
ok("ws: cloned url is trimmed (no surrounding whitespace reaches git)",
  Buffer.from(wsB64, "base64").toString("utf8") === "https://github.com/owner/repo.git");

console.log(`\n  remote-open unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
