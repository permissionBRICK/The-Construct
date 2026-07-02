"use strict";
// Plain-node unit tests for the pure remote-config rename-import planner
// (src/importui.js) — the decision core of the Projects-tab collision/rename path
// that extension.js cannot unit-test (it drives vscode input boxes + fs).
// No deps. Run: node importui.test.js
const importui = require("../src/importui");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

const orig = {
  remoteUrl: "https://git.co/vm-config.git",
  ref: "HEAD",
  relPath: "projects/web.json",
  content: '{"name":"web","repos":[{"url":"https://h/web.git"}]}',
};

// ── Rejections ────────────────────────────────────────────────────────────────
ok("reject: empty name", importui.planRenamedImport("  ", orig, new Set()).error === "empty");
ok("reject: reserved 'default'", importui.planRenamedImport("default", orig, new Set()).error === "reserved");
ok("reject: reserved 'project.schema'", importui.planRenamedImport("project.schema", orig, new Set()).error === "reserved");
ok("reject: path-separator name", importui.planRenamedImport("../evil", orig, new Set()).error === "unsafe");
ok("reject: backslash name", importui.planRenamedImport("a\\b", orig, new Set()).error === "unsafe");
ok("reject: dotdot name", importui.planRenamedImport("..", orig, new Set()).error === "unsafe");
ok("reject: already-taken name (no silent overwrite)",
  importui.planRenamedImport("web-2", orig, new Set(["web", "web-2"])).error === "taken");
ok("reject: taken name is CASE-INSENSITIVE (existing 'api' rejects 'API' — Windows FS)",
  importui.planRenamedImport("API", orig, new Set(["api"])).error === "taken");
ok("reject: taken name case-insensitive the other way ('API' exists, 'api' rejected)",
  importui.planRenamedImport("api", orig, new Set(["API"])).error === "taken");
ok("reject: unparseable original content",
  importui.planRenamedImport("web-2", { ...orig, content: "{not json" }, new Set()).error === "unparseable");
ok("reject: non-object original (array)",
  importui.planRenamedImport("web-2", { ...orig, content: "[1,2]" }, new Set()).error === "invalid");
ok("reject: missing content", importui.planRenamedImport("web-2", { remoteUrl: "u" }, new Set()).error === "unparseable");

// ── Acceptance + provenance ────────────────────────────────────────────────────
(function accept() {
  const r = importui.planRenamedImport("web-2", orig, new Set(["web"]));
  ok("accept: ok", r.ok === true);
  ok("accept: name is the target", r.name === "web-2");
  // Canonical profile with the target name stamped in.
  ok("accept: profileJson is canonical (trailing newline, 2-space)", r.profileJson.endsWith("}\n") && r.profileJson.includes('\n  "name": "web-2"'));
  ok("accept: profileJson.name == target (not the original)", JSON.parse(r.profileJson).name === "web-2");
  ok("accept: repos preserved from the upstream file", eq(JSON.parse(r.profileJson).repos, [{ url: "https://h/web.git" }]));
  // Full provenance so the renamed import is tracked/shareable/pushable/updatable.
  ok("accept: manifest preserves remoteUrl", r.manifestEntry.remoteUrl === orig.remoteUrl);
  ok("accept: manifest preserves ref", r.manifestEntry.ref === orig.ref);
  ok("accept: manifest preserves pathInRemote", r.manifestEntry.pathInRemote === orig.relPath);
  ok("accept: manifest importedAs == target", r.manifestEntry.importedAs === "web-2");
  ok("accept: baseContent is the raw upstream bytes", r.baseContent === orig.content);
})();

ok("accept: taken as an array (not a Set) also works",
  importui.planRenamedImport("fresh", orig, ["web"]).ok === true);
ok("accept: name is trimmed", importui.planRenamedImport("  spaced  ", orig, new Set()).name === "spaced");

console.log("\n  importui unit tests — " + pass + "/" + (pass + fail) + " passed\n");
process.exit(fail ? 1 : 0);
