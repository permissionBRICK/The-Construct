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
// A folder name can contain URI-significant chars (a profile directory like "repo?x",
// or a basename-derived "repo.git?ref=x"); these must be percent-encoded so
// vscode.Uri.parse keeps them in the PATH instead of splitting a query/fragment off.
ok("uri: percent-encodes '?' in a segment", remote.remoteFolderUri(undefined, "/root/repos/repo?x") === "vscode-remote://ssh-remote+agent-vm/root/repos/repo%3Fx");
ok("uri: percent-encodes '#' in a segment", remote.remoteFolderUri(undefined, "/root/repos/repo#frag") === "vscode-remote://ssh-remote+agent-vm/root/repos/repo%23frag");
ok("uri: percent-encodes a space", remote.remoteFolderUri(undefined, "/root/repos/my repo") === "vscode-remote://ssh-remote+agent-vm/root/repos/my%20repo");
ok("uri: preserves '/' separators (nested dir)", remote.remoteFolderUri(undefined, "/root/repos/sub/repo") === "vscode-remote://ssh-remote+agent-vm/root/repos/sub/repo");
// The encoding must round-trip: decoding the path segments recovers the raw path
// vscode.Uri.parse will see (it decodes percent-encoding the same way).
(function () {
  const raw = "/root/repos/repo?x#y z/git@h:n";
  const uri = remote.remoteFolderUri(undefined, raw);
  const path = uri.slice("vscode-remote://ssh-remote+agent-vm".length);
  const decoded = path.split("/").map(decodeURIComponent).join("/");
  ok("uri: encoded path round-trips back to the raw POSIX path", decoded === raw, decoded);
})();
// End to end: a profile whose checkout dir carries a '?' opens that exact folder.
ok("open+uri: a '?' in a profile dir survives into the open URI path",
  remote.remoteFolderUri(undefined, remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: "repo?x" }] }))
    === "vscode-remote://ssh-remote+agent-vm/root/repos/repo%3Fx");
ok("open+uri: a query-bearing derived basename survives into the open URI path",
  remote.remoteFolderUri(undefined, remote.projectOpenPath({ repos: [{ url: "https://h/o/repo.git?ref=x" }] }))
    === "vscode-remote://ssh-remote+agent-vm/root/repos/repo.git%3Fref%3Dx");

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

// ── projectOpenPath (mirrors bin/checkout-projects.sh byte-for-byte) ──────────
ok("open: single repo with explicit directory", remote.projectOpenPath({ repos: [{ url: "https://h/o/r.git", directory: "work-r" }] }) === "/root/repos/work-r");
ok("open: single repo, directory derived from url (https)", remote.projectOpenPath({ repos: [{ url: "https://github.com/o/cool-app.git" }] }) === "/root/repos/cool-app");
ok("open: single repo, derived from owner/repo scp url", remote.projectOpenPath({ repos: [{ url: "git@github.com:o/cool-app.git" }] }) === "/root/repos/cool-app");
// basename splits on '/' only — an scp url with NO slash keeps the host:name as the
// dir, exactly as `basename "git@host:cool-app.git" .git` does on the VM (regression
// for the confirmed pre-review divergence vs repoNameFromUrl, which splits on ':').
ok("open: scp url with no slash mirrors basename (host kept)", remote.projectOpenPath({ repos: [{ url: "git@host:cool-app.git" }] }) === "/root/repos/git@host:cool-app");
ok("open: empty directory ('') derives from url", remote.projectOpenPath({ repos: [{ url: "https://h/o/r.git", directory: "" }] }) === "/root/repos/r");
// directory is taken verbatim (checkout-projects.sh does not trim and allows nesting).
ok("open: nested directory is opened (a/b), not rejected", remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: "sub/repo" }] }) === "/root/repos/sub/repo");
ok("open: whitespace directory used verbatim (mirrors jq/sh)", remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: "  repo  " }] }) === "/root/repos/  repo  ");
ok("open: zero repos -> workspace root", remote.projectOpenPath({ repos: [] }) === "/root/repos");
ok("open: multiple repos -> workspace root", remote.projectOpenPath({ repos: [{ url: "https://h/a.git" }, { url: "https://h/b.git" }] }) === "/root/repos");
ok("open: null/undefined profile -> workspace root", remote.projectOpenPath(null) === "/root/repos" && remote.projectOpenPath(undefined) === "/root/repos");
ok("open: profile without repos array -> workspace root", remote.projectOpenPath({ name: "x" }) === "/root/repos");
ok("open: directory '..' segment is rejected -> workspace root", remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: ".." }] }) === "/root/repos");
ok("open: traversing nested directory is rejected -> workspace root", remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: "../../etc" }] }) === "/root/repos");
ok("open: absolute directory is rejected -> workspace root", remote.projectOpenPath({ repos: [{ url: "https://h/x.git", directory: "/etc" }] }) === "/root/repos");
ok("open: unusable url (no basename) -> workspace root", remote.projectOpenPath({ repos: [{ url: "" }] }) === "/root/repos");

// ── shouldAutoOpenPanel ──────────────────────────────────────────────────────
ok("autoopen: connected to VM + not yet opened -> true", remote.shouldAutoOpenPanel("ssh-remote+agent-vm", false) === true);
ok("autoopen: connected via full hostname -> true", remote.shouldAutoOpenPanel("ssh-remote+agent-vm.mshome.net", undefined) === true);
ok("autoopen: connected but already opened -> false", remote.shouldAutoOpenPanel("ssh-remote+agent-vm", true) === false);
ok("autoopen: local window (not connected) -> false", remote.shouldAutoOpenPanel("", false) === false);
ok("autoopen: a different SSH host -> false", remote.shouldAutoOpenPanel("ssh-remote+other-box", false) === false);
ok("autoopen: honors a cfg alias override", remote.shouldAutoOpenPanel("ssh-remote+myvm", false, { hostAlias: "myvm" }) === true);

console.log(`\n  remote-open unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
