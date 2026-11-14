"use strict";
// Plain-node unit tests for the profile validation in src/projects.js (what the VM's
// `construct project set` uses): the sanitizer, the strict validator and the canonical
// form, incl. adversarial inputs (traversal and prototype pollution in profiles).
// No deps. Run: node projects.test.js
const projects = require("../src/projects");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

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
ok("mcp: string entries are dropped (legacy enum removed)", eq(projects.sanitizeMcp(["filesystem", "browser", "github", "nope"]), []));
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
ok("mcp: junk entries (number/null/string) dropped", eq(projects.sanitizeMcp([5, null, "filesystem", { name: "x", command: "c" }]), [{ name: "x", type: "stdio", command: "c" }]));

// ── sanitizeStringArray ───────────────────────────────────────────────────────
ok("strarr: trims-blank filter, order preserved", eq(
  projects.sanitizeStringArray(["npm ci", "", "  ", "cp .env"]), ["npm ci", "cp .env"]));
ok("strarr: non-strings dropped", eq(projects.sanitizeStringArray([1, "ok", null, {}]), ["ok"]));
ok("strarr: non-array -> []", eq(projects.sanitizeStringArray(null), []));

// ── sanitizeProfile (the top-level edit coercion) ─────────────────────────────
ok("profile: coerces a full object to the schema shape", eq(
  projects.sanitizeProfile("customer-portal", {
    name: "IGNORED-in-object", repos: [{ url: "https://h/x.git", directory: "cp" }],
    sdks: { node: "22" }, mcp: [{ name: "gh", command: "npx" }], hostPackages: ["build-essential"],
    provisionCommands: ["npm ci"], tests: { web: { runner: "playwright" } },
  }),
  { name: "customer-portal", repos: [{ url: "https://h/x.git", directory: "cp" }], sdks: { node: "22" },
    mcp: [{ name: "gh", type: "stdio", command: "npx" }], hostPackages: ["build-essential"], provisionCommands: ["npm ci"], tests: { web: { runner: "playwright" } } }));
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
    mcp: [{ name: "s", type: "stdio", command: "npx", args: ["-y", "p"], env: { A: "1" } },
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
  ok("validate: mcp string entry rejected (legacy enum removed)", !projects.validateProfile("x", { name: "x", mcp: ["filesystem"] }).ok);
  ok("validate: mcp arbitrary string rejected", !projects.validateProfile("x", { name: "x", mcp: ["bogus"] }).ok);
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
  const fw = (forwards) => projects.validateProfile("x", { name: "x", forwards });
  ok("validate: forwards with port/label/to pass", fw([{ port: 5173 }, { port: 8080, label: "api", to: "host" }, { port: 1, to: "client", label: "" }]).ok);
  ok("validate: forwards 8080.0 is the integer 8080", fw([{ port: 8080.0 }]).ok);
  ok("validate: forwards non-array rejected", eq(fw({ port: 1 }).errors, ['"forwards" must be an array']));
  ok("validate: forwards entry non-object rejected", eq(fw([5173]).errors, ['forwards[0] must be an object with a "port"']));
  ok("validate: forwards port missing/zero/too big/fractional/string rejected",
    [[{}], [{ port: 0 }], [{ port: 65536 }], [{ port: 80.5 }], [{ port: "80" }], [{ port: true }]].every((f) =>
      eq(fw(f).errors, ["forwards[0].port must be an integer from 1 to 65535"])));
  ok("validate: forwards non-string label rejected", eq(fw([{ port: 1, label: 2 }]).errors, ["forwards[0].label must be a string"]));
  ok("validate: forwards unknown target rejected (case-sensitive)",
    eq(fw([{ port: 1, to: "lan" }, { port: 2, to: "Host" }]).errors, ['forwards[0].to must be "client" or "host"', 'forwards[1].to must be "client" or "host"']));
  ok("validate: forwards unknown key rejected", eq(fw([{ port: 1, target: "host" }]).errors, ['forwards[0] unknown key "target"']));
  ok("validate: HTTPS forwards that say what the port speaks pass",
    fw([{ port: 5173, https: true, upstreamTls: false }, { port: 5174, https: true, to: "host", name: "app", upstreamTls: true }, { port: 5175, https: false }]).ok);
  ok("validate: an HTTPS forward must say what its port speaks",
    eq(fw([{ port: 1, https: true }]).errors,
      ['forwards[0].upstreamTls is required with "https": true: false when the port serves plain HTTP, true when it speaks TLS itself']));
  ok("validate: forwards https/upstreamTls must be booleans",
    eq(fw([{ port: 1, https: "true" }, { port: 2, https: true, upstreamTls: 1 }]).errors,
      ["forwards[0].https must be true or false", "forwards[1].upstreamTls must be true or false"]));
  ok("validate: an HTTPS forward cannot go to the client",
    eq(fw([{ port: 1, https: true, upstreamTls: false, to: "client" }]).errors, ['forwards[0].to must be "host" or left out: the host serves an HTTPS forward']));
  ok("validate: name and upstreamTls (even false) need https",
    eq(fw([{ port: 1, name: "app" }, { port: 2, https: false, upstreamTls: true }, { port: 3, upstreamTls: false }]).errors,
      ['forwards[0].name needs "https": true', 'forwards[1].upstreamTls needs "https": true', 'forwards[2].upstreamTls needs "https": true']));
  const badName = "forwards[0].name must be one DNS label: up to 63 lowercase letters, digits and hyphens, starting and ending with a letter or digit";
  ok("validate: forwards name must be one DNS label",
    ["App", "-a", "a-", "a_b", "a.b", "", "a\n", "a".repeat(64), 5, null].every((name) => eq(fw([{ port: 1, https: true, upstreamTls: false, name }]).errors, [badName])));
  ok("validate: forwards name of 63 characters passes", fw([{ port: 1, https: true, upstreamTls: false, name: "a".repeat(63) }]).ok);
})();

// ── sanitizeForwards (coercive) ───────────────────────────────────────────────
(function () {
  ok("forwards: non-array -> []", eq(projects.sanitizeForwards(null), []) && eq(projects.sanitizeForwards({}), []));
  ok("forwards: to defaults to client, blank label dropped, label kept verbatim",
    eq(projects.sanitizeForwards([{ port: 5173, label: "  " }, { port: 9000, label: " api ", to: "host" }]),
      [{ port: 5173, to: "client" }, { port: 9000, to: "host", label: " api " }]));
  ok("forwards: bad port or unknown target drops the entry (never downgraded to client)",
    eq(projects.sanitizeForwards([{ port: 0 }, { port: 1, to: "lan" }, "x", { port: 2 }]), [{ port: 2, to: "client" }]));
  ok("forwards: https makes to host, key order port,to,https,upstreamTls,name,label",
    eq(JSON.stringify(projects.sanitizeForwards([{ label: "app", upstreamTls: true, name: "app", https: true, port: 5173 }])),
      '[{"port":5173,"to":"host","https":true,"upstreamTls":true,"name":"app","label":"app"}]'));
  ok("forwards: upstreamTls false is kept on an HTTPS entry, dropped on a plain one; a bad name is dropped",
    eq(projects.sanitizeForwards([{ port: 1, https: false, name: "app", upstreamTls: true }, { port: 2, https: true, name: "Bad", upstreamTls: false }]),
      [{ port: 1, to: "client" }, { port: 2, to: "host", https: true, upstreamTls: false }]));
  ok("forwards: an HTTPS entry to the client or without a boolean upstreamTls is dropped, never guessed",
    eq(projects.sanitizeForwards([{ port: 3, https: true, upstreamTls: false, to: "client" }, { port: 4, https: true }, { port: 5, https: true, upstreamTls: "true" }]), []));
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
  ok("canonical: no forwards key without forwards (existing profiles keep their bytes)",
    !("forwards" in JSON.parse(projects.canonicalProfileJson("web", { name: "web", forwards: [] }))));
  const withFw = projects.canonicalProfileJson("web", { tests: {}, forwards: [{ label: "vite", port: 5173 }], name: "web" });
  ok("canonical: forwards sit between provisionCommands and tests", eq(Object.keys(JSON.parse(withFw)),
    ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "forwards", "tests"]));
  ok("canonical: forward entry is port, to, label with to spelled out",
    eq(Object.keys(JSON.parse(withFw).forwards[0]), ["port", "to", "label"]) && JSON.parse(withFw).forwards[0].to === "client");
  ok("canonical: forwards output re-validates and is idempotent",
    projects.validateProfile("web", JSON.parse(withFw)).ok && projects.canonicalProfileJson("web", JSON.parse(withFw)) === withFw);
})();

// ── Finding 8: validateProfile rejects names with path separators ────────────
(function () {
  const slash = projects.validateProfile("x", { name: "/../../../../.bashrc" });
  ok("validate: path-separator name rejected (forward slash)", !slash.ok && slash.errors.some((e) => e.includes("illegal path")));
  const backslash = projects.validateProfile("x", { name: "..\\..\\etc\\passwd" });
  ok("validate: path-separator name rejected (backslash)", !backslash.ok && backslash.errors.some((e) => e.includes("illegal path")));
  const dotdot = projects.validateProfile("x", { name: ".." });
  ok("validate: double-dot name rejected", !dotdot.ok && dotdot.errors.some((e) => e.includes("illegal path")));
  const dot = projects.validateProfile("x", { name: "." });
  ok("validate: single-dot name rejected", !dot.ok && dot.errors.some((e) => e.includes("illegal path")));
  const traversal = projects.validateProfile("x", { name: "foo/../bar" });
  ok("validate: embedded traversal rejected", !traversal.ok && traversal.errors.some((e) => e.includes("illegal path")));
  // Normal names still pass.
  ok("validate: normal name still passes", projects.validateProfile("web", { name: "web" }).ok);
  ok("validate: hyphenated name still passes", projects.validateProfile("my-project", { name: "my-project" }).ok);
})();


console.log(`\n  project-profile unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
