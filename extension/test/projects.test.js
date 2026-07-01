"use strict";
// Plain-node unit tests for the project-profile pure logic (src/projects.js): the
// remote-scan builder + parser, the import merge, the selection reconcile, and the
// profile sanitizer — incl. adversarial inputs (injection attempts in the scan
// script, traversal/prototype pollution in profiles, malformed/empty output).
// No deps. Run: node projects.test.js
const projects = require("../src/projects");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// ── buildScanScript ───────────────────────────────────────────────────────────
const scan = projects.buildScanScript();
ok("scan: uses set -u", scan.split("\n")[0] === "set -u");
ok("scan: targets the workspace root", scan.includes("root='/root/repos'"));
ok("scan: walks WORKSPACE_ROOT/*/", scan.includes('for repo in "$root"/*/'));
ok("scan: only real git repos (.git present)", scan.includes('[ -d "${repo}.git" ] || continue'));
ok("scan: reads origin url", scan.includes("git -C \"$repo\" remote get-url origin"));
ok("scan: emits the END sentinel", scan.includes("printf 'END\\n'"));
// printf uses an explicit format string so a repo name/url with a % or \ is DATA,
// not a directive — the fields never sit in the format position.
ok("scan: fields are printf DATA (explicit %s format)", scan.includes("printf '%s\\t%s\\t%s\\n' \"$name\" \"$url\" \"$branch\""));
// A test/non-default root is single-quote-escaped so it stays a literal even with a quote.
const scanQ = projects.buildScanScript("/tmp/it's here");
ok("scan: single-quote in a custom root is escaped", scanQ.includes("root='/tmp/it'\\''s here'"));
ok("scan: root override applied", projects.buildScanScript("/srv/x").includes("root='/srv/x'"));

// ── parseScan ─────────────────────────────────────────────────────────────────
ok("parse: no END sentinel -> null (partial/failed capture)", projects.parseScan("repo\tu\tmain\n") === null);
ok("parse: END with no repos -> [] (real empty)", eq(projects.parseScan("END\n"), []));
ok("parse: parses name/url/branch", eq(
  projects.parseScan("app\thttps://h/o/app.git\tmain\nlib\tgit@h:o/lib.git\tdev\nEND\n"),
  [{ name: "app", url: "https://h/o/app.git", branch: "main" }, { name: "lib", url: "git@h:o/lib.git", branch: "dev" }]));
ok("parse: a repo with no remote keeps an empty url", eq(
  projects.parseScan("loose\t\tmain\nEND\n"), [{ name: "loose", url: "", branch: "main" }]));
ok("parse: trims field whitespace", eq(
  projects.parseScan("app\t https://h/x.git \t main \nEND\n"), [{ name: "app", url: "https://h/x.git", branch: "main" }]));
ok("parse: skips short/banner lines (no tabs)", eq(
  projects.parseScan("Welcome to Ubuntu\napp\thttps://h/x.git\tmain\nEND\n"),
  [{ name: "app", url: "https://h/x.git", branch: "main" }]));
ok("parse: skips a line with a blank name", eq(projects.parseScan("\t\t\nEND\n"), []));
ok("parse: empty string -> null", projects.parseScan("") === null);
ok("parse: null/undefined -> null", projects.parseScan(null) === null && projects.parseScan(undefined) === null);

// ── buildDiscoveredProfile ────────────────────────────────────────────────────
ok("discovered: minimal profile shaped like the export generator", eq(
  projects.buildDiscoveredProfile({ name: "app", url: "https://h/x.git", branch: "main" }),
  { name: "app", repos: [{ url: "https://h/x.git", directory: "app" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} }));

// ── coveredUrls / planImport ──────────────────────────────────────────────────
ok("covered: gathers repo urls across profiles", (() => {
  const s = projects.coveredUrls({
    a: { repos: [{ url: "https://h/a.git" }] },
    b: { repos: [{ url: "https://h/b.git" }, { url: " https://h/c.git " }] },
  });
  return s.has("https://h/a.git") && s.has("https://h/b.git") && s.has("https://h/c.git") && s.size === 3;
})());
ok("covered: ignores non-array / empty repos", (() => {
  const s = projects.coveredUrls({ x: { repos: "nope" }, y: {}, z: { repos: [{ url: "" }, { nope: 1 }] } });
  return s.size === 0;
})());

(function planImportCases() {
  const scanRes = [
    { name: "app", url: "https://h/o/app.git", branch: "main" },   // new
    { name: "known", url: "https://h/o/known.git", branch: "main" }, // covered by existing url
    { name: "loose", url: "", branch: "main" },                      // no remote -> skipped
    { name: "clash", url: "https://h/o/clash.git", branch: "main" }, // name collides with existing
  ];
  const existing = {
    other: { repos: [{ url: "https://h/o/known.git" }] },
    clash: { repos: [{ url: "https://h/o/DIFFERENT.git" }] },
  };
  const plan = projects.planImport(scanRes, existing);
  ok("plan: imports only the new, coverable repo", plan.toWrite.length === 1 && plan.toWrite[0].name === "app");
  ok("plan: written profile is a valid discovered profile", eq(plan.toWrite[0].profile,
    { name: "app", repos: [{ url: "https://h/o/app.git", directory: "app" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} }));
  ok("plan: a repo with no remote is skipped", plan.skipped.includes("loose"));
  ok("plan: a url already covered is reported covered", plan.covered.includes("known"));
  ok("plan: a name collision keeps the existing profile (covered, not written)",
    plan.covered.includes("clash") && !plan.toWrite.some((i) => i.name === "clash"));
})();
ok("plan: two scan repos sharing a url import only once", (() => {
  const p = projects.planImport([
    { name: "a", url: "https://h/dup.git", branch: "m" },
    { name: "b", url: "https://h/dup.git", branch: "m" },
  ], {});
  return p.toWrite.length === 1 && p.covered.includes("b");
})());
ok("plan: empty scan -> nothing", (() => { const p = projects.planImport([], {}); return !p.toWrite.length && !p.skipped.length; })());
ok("plan: null scan -> nothing (no throw)", (() => { const p = projects.planImport(null, null); return !p.toWrite.length; })());

// ── toChips / reconcileSelection ──────────────────────────────────────────────
ok("chips: one per available profile, ticked from selection", eq(
  projects.toChips(["a", "b", "c"], ["b"]),
  [{ name: "a", selected: false }, { name: "b", selected: true }, { name: "c", selected: false }]));
ok("chips: empty selection -> all unselected", eq(
  projects.toChips(["a"], []), [{ name: "a", selected: false }]));
ok("chips: a selected name not in available is ignored (no phantom chip)", eq(
  projects.toChips(["a"], ["a", "ghost"]), [{ name: "a", selected: true }]));
ok("chips: non-array args -> []", eq(projects.toChips(null, null), []));

ok("reconcile: keeps only available names, in available order", eq(
  projects.reconcileSelection(["c", "a", "z"], ["a", "b", "c"]), ["a", "c"]));
ok("reconcile: drops a name that doesn't exist", eq(
  projects.reconcileSelection(["ghost"], ["a"]), []));
ok("reconcile: de-dupes (a set of requested, filtered by available)", eq(
  projects.reconcileSelection(["a", "a"], ["a", "b"]), ["a"]));
ok("reconcile: empty request -> []", eq(projects.reconcileSelection([], ["a"]), []));
ok("reconcile: non-array args -> []", eq(projects.reconcileSelection(null, null), []));

// ── sanitizeRepos ─────────────────────────────────────────────────────────────
ok("repos: keeps url (+ optional directory), drops extras", eq(
  projects.sanitizeRepos([{ url: "https://h/x.git", directory: "d", junk: 1 }]),
  [{ url: "https://h/x.git", directory: "d" }]));
ok("repos: an entry with no url is dropped", eq(projects.sanitizeRepos([{ directory: "d" }, { url: "  " }]), []));
ok("repos: omits an empty directory", eq(projects.sanitizeRepos([{ url: "https://h/x.git", directory: "" }]), [{ url: "https://h/x.git" }]));
ok("repos: non-array -> []", eq(projects.sanitizeRepos("nope"), []) && eq(projects.sanitizeRepos(null), []));
ok("repos: non-object entries dropped", eq(projects.sanitizeRepos(["str", 5, null, { url: "https://h/x.git" }]), [{ url: "https://h/x.git" }]));

// ── sanitizeSdks ──────────────────────────────────────────────────────────────
ok("sdks: string + array values kept", eq(
  projects.sanitizeSdks({ node: ["22", "24"], python: "3.12" }), { node: ["22", "24"], python: "3.12" }));
ok("sdks: empty string / empty array dropped", eq(
  projects.sanitizeSdks({ a: "", b: [], c: "1" }), { c: "1" }));
ok("sdks: array filters blank entries", eq(projects.sanitizeSdks({ node: ["22", "", "  "] }), { node: ["22"] }));
ok("sdks: non-string/array value dropped", eq(projects.sanitizeSdks({ a: 5, b: { x: 1 }, c: true }), {}));
ok("sdks: non-object -> {}", eq(projects.sanitizeSdks("nope"), {}) && eq(projects.sanitizeSdks(null), {}));

// ── sanitizeMcp ───────────────────────────────────────────────────────────────
ok("mcp: legacy enum strings kept", eq(projects.sanitizeMcp(["filesystem", "browser", "github"]), ["filesystem", "browser", "github"]));
ok("mcp: an unknown legacy string is dropped", eq(projects.sanitizeMcp(["nope", "filesystem"]), ["filesystem"]));
ok("mcp: stdio server (command) kept with args/env", eq(
  projects.sanitizeMcp([{ name: "c7", type: "stdio", command: "npx", args: ["-y", "@x/c7"], env: { K: "v" } }]),
  [{ name: "c7", type: "stdio", command: "npx", args: ["-y", "@x/c7"], env: { K: "v" } }]));
ok("mcp: http server (url) kept with headers + bearer + agents + enabled", eq(
  projects.sanitizeMcp([{ name: "s", type: "http", url: "https://m/mcp", headers: { A: "b" }, bearerTokenEnvVar: "T", agents: ["codex", "bogus"], enabled: false }]),
  [{ name: "s", type: "http", url: "https://m/mcp", headers: { A: "b" }, bearerTokenEnvVar: "T", agents: ["codex"], enabled: false }]));
ok("mcp: type inferred from command (stdio)", eq(
  projects.sanitizeMcp([{ name: "x", command: "npx" }]), [{ name: "x", type: "stdio", command: "npx" }]));
ok("mcp: type inferred from url (http)", eq(
  projects.sanitizeMcp([{ name: "x", url: "https://m" }]), [{ name: "x", type: "http", url: "https://m" }]));
ok("mcp: server with no name dropped", eq(projects.sanitizeMcp([{ type: "stdio", command: "npx" }]), []));
ok("mcp: stdio with no command dropped", eq(projects.sanitizeMcp([{ name: "x", type: "stdio" }]), []));
ok("mcp: http with no url dropped", eq(projects.sanitizeMcp([{ name: "x", type: "http" }]), []));
ok("mcp: unknowable-type server dropped", eq(projects.sanitizeMcp([{ name: "x" }]), []));
ok("mcp: args non-strings filtered", eq(projects.sanitizeMcp([{ name: "x", command: "c", args: ["ok", 5, null] }]),
  [{ name: "x", type: "stdio", command: "c", args: ["ok"] }]));
ok("mcp: env non-string values dropped, empty env omitted", eq(
  projects.sanitizeMcp([{ name: "x", command: "c", env: { A: 1, B: "b" } }]),
  [{ name: "x", type: "stdio", command: "c", env: { B: "b" } }]));
ok("mcp: agents all-invalid -> key omitted", eq(
  projects.sanitizeMcp([{ name: "x", command: "c", agents: ["bogus"] }]), [{ name: "x", type: "stdio", command: "c" }]));
ok("mcp: enabled only kept as a boolean", eq(
  projects.sanitizeMcp([{ name: "x", command: "c", enabled: "yes" }]), [{ name: "x", type: "stdio", command: "c" }]));
ok("mcp: non-array -> []", eq(projects.sanitizeMcp("nope"), []) && eq(projects.sanitizeMcp(null), []));
ok("mcp: junk entries (number/null) dropped", eq(projects.sanitizeMcp([5, null, "filesystem"]), ["filesystem"]));

// ── sanitizeStringArray ───────────────────────────────────────────────────────
ok("strarr: trims-blank filter, order preserved", eq(
  projects.sanitizeStringArray(["npm ci", "", "  ", "cp .env"]), ["npm ci", "cp .env"]));
ok("strarr: non-strings dropped", eq(projects.sanitizeStringArray([1, "ok", null, {}]), ["ok"]));
ok("strarr: non-array -> []", eq(projects.sanitizeStringArray(null), []));

// ── sanitizeProfile (the top-level edit coercion) ─────────────────────────────
ok("profile: coerces a full object to the schema shape", eq(
  projects.sanitizeProfile("customer-portal", {
    name: "IGNORED-in-object", repos: [{ url: "https://h/x.git", directory: "cp" }],
    sdks: { node: "22" }, mcp: ["github"], hostPackages: ["build-essential"],
    provisionCommands: ["npm ci"], tests: { web: { runner: "playwright" } },
  }),
  { name: "customer-portal", repos: [{ url: "https://h/x.git", directory: "cp" }], sdks: { node: "22" },
    mcp: ["github"], hostPackages: ["build-essential"], provisionCommands: ["npm ci"], tests: { web: { runner: "playwright" } } }));
ok("profile: name comes from the ARG, not the object (can't rename the file)",
  projects.sanitizeProfile("real", { name: "spoofed" }).name === "real");
ok("profile: an empty name -> null", projects.sanitizeProfile("", {}) === null && projects.sanitizeProfile("   ", {}) === null && projects.sanitizeProfile(null, {}) === null);
ok("profile: a missing/empty object still yields a valid empty profile", eq(
  projects.sanitizeProfile("blank", null),
  { name: "blank", repos: [], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} }));
ok("profile: unknown top-level keys are dropped (schema additionalProperties:false)",
  (() => { const p = projects.sanitizeProfile("x", { evil: 1, __proto__: { polluted: true }, repos: [] }); return !("evil" in p) && !("polluted" in p); })());
ok("profile: a non-object tests becomes {}", eq(projects.sanitizeProfile("x", { tests: "nope" }).tests, {}));
// Adversarial: a hostile profile can't inject anything — every field is coerced and
// the output is a plain object with only the seven schema keys.
(function () {
  const p = projects.sanitizeProfile("evil", {
    name: "../../etc/passwd",
    repos: [{ url: "https://h/x.git'; rm -rf / #", directory: "../../escape" }],
    mcp: [{ name: "x", command: "c", args: ["$(whoami)"] }],
    junk: "'; DROP TABLE;",
  });
  ok("profile: name arg wins over a traversing object name", p.name === "evil");
  ok("profile: repo url/dir preserved verbatim as DATA (no interpretation here)",
    p.repos[0].url === "https://h/x.git'; rm -rf / #" && p.repos[0].directory === "../../escape");
  ok("profile: only the seven schema keys survive",
    eq(Object.keys(p).sort(), ["hostPackages", "mcp", "name", "provisionCommands", "repos", "sdks", "tests"]));
})();
// Prototype pollution: a "__proto__" key in the SDKs/MCP maps must not pollute
// Object.prototype (JSON.parse would produce a plain data key, but a hand-built
// object could carry the real proto — sanitize copies via for..in over own keys).
(function () {
  const before = ({}).polluted;
  projects.sanitizeSdks(JSON.parse('{"__proto__":{"polluted":true},"node":"22"}'));
  ok("profile: sanitizeSdks doesn't pollute Object.prototype", ({}).polluted === before);
})();

// ── isReservedProfileName (config-sync §4) ────────────────────────────────────
ok("reserved: default", projects.isReservedProfileName("default"));
ok("reserved: case/whitespace-insensitive", projects.isReservedProfileName("  Default "));
ok("reserved: project.schema", projects.isReservedProfileName("project.schema"));
ok("reserved: ordinary names are not", !projects.isReservedProfileName("base") && !projects.isReservedProfileName("customer-portal"));
ok("reserved: empty/null are not", !projects.isReservedProfileName("") && !projects.isReservedProfileName(null));
ok("reserved: default.json (with extension) is NOT the base name", !projects.isReservedProfileName("default.json"));

// ── validateProfile (strict schema mirror; config-sync §6 gates) ──────────────
(function () {
  const full = {
    name: "web",
    repos: [{ url: "https://h/x.git", directory: "x" }, { url: "https://h/y.git" }],
    sdks: { node: ["26"], python: "3.14" },
    mcp: ["browser", { name: "s", type: "stdio", command: "npx", args: ["-y", "p"], env: { A: "1" } },
      { name: "h", type: "http", url: "https://m", headers: { X: "y" }, bearerTokenEnvVar: "TOK", agents: ["claude"], enabled: false }],
    hostPackages: ["jq"],
    provisionCommands: ["make setup"],
    tests: { anything: ["goes"] },
  };
  ok("validate: full valid profile passes", projects.validateProfile("web", full).ok,
    JSON.stringify(projects.validateProfile("web", full).errors));
  ok("validate: minimal {name} passes", projects.validateProfile("m", { name: "m" }).ok);
  ok("validate: empty name arg skips the filename check", projects.validateProfile("", { name: "whatever" }).ok);
  ok("validate: non-object rejected", !projects.validateProfile("x", []).ok && !projects.validateProfile("x", "s").ok && !projects.validateProfile("x", null).ok);
  ok("validate: missing name rejected", !projects.validateProfile("x", {}).ok);
  ok("validate: blank name rejected", !projects.validateProfile("x", { name: "   " }).ok);
  ok("validate: filename/name mismatch rejected", !projects.validateProfile("x", { name: "y" }).ok);
  const unk = projects.validateProfile("x", { name: "x", extra: 1 });
  ok("validate: unknown top-level key rejected with its name", !unk.ok && unk.errors.some((e) => e.includes('"extra"')));
  ok("validate: repos non-array rejected", !projects.validateProfile("x", { name: "x", repos: {} }).ok);
  ok("validate: repo entry without url rejected", !projects.validateProfile("x", { name: "x", repos: [{ directory: "d" }] }).ok);
  ok("validate: whitespace-only url rejected (stricter than minLength)", !projects.validateProfile("x", { name: "x", repos: [{ url: "  " }] }).ok);
  ok("validate: repo unknown key rejected", !projects.validateProfile("x", { name: "x", repos: [{ url: "u", branch: "b" }] }).ok);
  ok("validate: sdks string/array values pass", projects.validateProfile("x", { name: "x", sdks: { a: "1", b: ["2"] } }).ok);
  ok("validate: sdks numeric value rejected", !projects.validateProfile("x", { name: "x", sdks: { a: 1 } }).ok);
  ok("validate: sdks array with blank entry rejected", !projects.validateProfile("x", { name: "x", sdks: { a: ["", "1"] } }).ok);
  ok("validate: mcp legacy enum passes", projects.validateProfile("x", { name: "x", mcp: ["filesystem"] }).ok);
  ok("validate: mcp unknown legacy string rejected", !projects.validateProfile("x", { name: "x", mcp: ["bogus"] }).ok);
  ok("validate: mcp stdio type inferred from command", projects.validateProfile("x", { name: "x", mcp: [{ name: "s", command: "c" }] }).ok);
  ok("validate: mcp http type inferred from url", projects.validateProfile("x", { name: "x", mcp: [{ name: "h", url: "u" }] }).ok);
  ok("validate: mcp with both command AND url rejected (oneOf)", !projects.validateProfile("x", { name: "x", mcp: [{ name: "b", command: "c", url: "u" }] }).ok);
  ok("validate: mcp explicit stdio with url key rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "b", type: "stdio", command: "c", url: "u" }] }).ok);
  ok("validate: mcp bad explicit type rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "b", type: "tcp", command: "c" }] }).ok);
  ok("validate: mcp neither command nor url rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "b" }] }).ok);
  ok("validate: mcp empty agents rejected (minItems 1)", !projects.validateProfile("x", { name: "x", mcp: [{ name: "s", command: "c", agents: [] }] }).ok);
  ok("validate: mcp foreign agent rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "s", command: "c", agents: ["gemini"] }] }).ok);
  ok("validate: mcp non-boolean enabled rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "s", command: "c", enabled: "yes" }] }).ok);
  ok("validate: mcp non-string env value rejected", !projects.validateProfile("x", { name: "x", mcp: [{ name: "s", command: "c", env: { A: 1 } }] }).ok);
  ok("validate: hostPackages non-string entry rejected", !projects.validateProfile("x", { name: "x", hostPackages: [1] }).ok);
  ok("validate: provisionCommands blank entry rejected", !projects.validateProfile("x", { name: "x", provisionCommands: [" "] }).ok);
  ok("validate: tests non-object rejected", !projects.validateProfile("x", { name: "x", tests: [] }).ok);
  ok("validate: errors accumulate (several reported at once)",
    projects.validateProfile("x", { name: "y", repos: {}, junk: 1 }).errors.length >= 3);
})();

// ── canonicalProfileJson (config-sync §6 canonical byte form) ─────────────────
(function () {
  const messy = { tests: {}, provisionCommands: ["a"], name: "IGNORED", sdks: { node: "26" }, repos: [{ directory: "d", url: "u" }], junk: 1 };
  const out = projects.canonicalProfileJson("web", messy);
  ok("canonical: trailing newline", out.endsWith("}\n"));
  ok("canonical: 2-space indent", out.includes('\n  "name": "web"'));
  ok("canonical: fixed top-level key order", eq(Object.keys(JSON.parse(out)),
    ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "tests"]));
  ok("canonical: repo entry key order url,directory", out.indexOf('"url"') < out.indexOf('"directory"'));
  ok("canonical: one array element per line", /\[\n\s+\{/.test(out));
  ok("canonical: unknown keys dropped", !out.includes("junk"));
  ok("canonical: name comes from the arg", JSON.parse(out).name === "web");
  ok("canonical: empty name -> null", projects.canonicalProfileJson("", messy) === null);
  ok("canonical: idempotent (canonical of canonical is byte-identical)",
    projects.canonicalProfileJson("web", JSON.parse(out)) === out);
  ok("canonical: output re-validates strictly", projects.validateProfile("web", JSON.parse(out)).ok);
  ok("canonical: matches writeProjectProfile's byte form (stringify+\\n)",
    out === JSON.stringify(JSON.parse(out), null, 2) + "\n");
})();

console.log(`\n  project-profile unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
