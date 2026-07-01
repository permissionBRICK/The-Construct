"use strict";
// Plain-node unit tests for the host-side install locator + settings round-trip.
// Builds a throwaway fake %LOCALAPPDATA% tree on disk. No deps. Run: node host.test.js
const fs = require("fs");
const os = require("os");
const path = require("path");
const host = require("../src/host");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

const mk = (...p) => { const d = path.join(...p); fs.mkdirSync(d, { recursive: true }); return d; };
const writeMarker = (dir) => fs.writeFileSync(path.join(dir, host.MARKER), "# stub\n");
const touch = (file, epochSec) => fs.utimesSync(file, epochSec, epochSec);

// ── Fixture: a realistic %LOCALAPPDATA% with two installs of different ages ────
const root = fs.mkdtempSync(path.join(os.tmpdir(), "construct-host-"));
try {
  const base = mk(root, "LocalAppData");

  // Newest install (install.ps1 layout: The-Construct\<slug>\<repo-ref>\Auto-Install.ps1).
  const newDir = mk(base, host.CONTAINER, "permissionBRICK-The-Construct-main", "The-Construct-main");
  writeMarker(newDir);
  touch(path.join(newDir, host.MARKER), 2_000_000_000); // far future = newest

  // Older install for a different ref.
  const oldDir = mk(base, host.CONTAINER, "permissionBRICK-The-Construct-dev", "The-Construct-dev");
  writeMarker(oldDir);
  touch(path.join(oldDir, host.MARKER), 1_000_000_000);

  // A stray folder with no marker must be ignored.
  mk(base, host.CONTAINER, "junk", "no-scripts-here");

  // ── Path resolution ──────────────────────────────────────────────────────--
  ok("resolve: picks newest install by Auto-Install.ps1 mtime",
    host.resolveScriptsDir({ localAppData: base }) === newDir,
    host.resolveScriptsDir({ localAppData: base }));

  ok("resolve: falls back to LOCALAPPDATA env when no explicit base",
    host.resolveScriptsDir({ env: { LOCALAPPDATA: base } }) === newDir);

  ok("resolve: TEMP env fallback mirrors install.ps1",
    host.resolveScriptsDir({ env: { TEMP: base } }) === newDir);

  ok("resolve: explicit override wins over auto-detect",
    host.resolveScriptsDir({ scriptsDir: oldDir, localAppData: base }) === oldDir);

  ok("resolve: invalid override falls through to auto-detect",
    host.resolveScriptsDir({ scriptsDir: path.join(root, "does-not-exist"), localAppData: base }) === newDir);

  ok("resolve: blank override ignored",
    host.resolveScriptsDir({ scriptsDir: "   ", localAppData: base }) === newDir);

  ok("resolve: no install present -> null",
    host.resolveScriptsDir({ localAppData: path.join(root, "empty") }) === null);

  ok("resolve: no base at all -> null", host.resolveScriptsDir({ env: {} }) === null);

  ok("projectsDir/settingsPath sit next to the scripts",
    host.projectsDir(newDir) === path.join(newDir, "projects") &&
    host.settingsPath(newDir) === path.join(newDir, ".construct-settings.json"));

  // ── Settings: read robustness ──────────────────────────────────────────────
  ok("read: missing settings file -> {}", JSON.stringify(host.readRawSettings(newDir)) === "{}");

  fs.writeFileSync(host.settingsPath(newDir), "{ not valid json ]");
  ok("read: malformed JSON -> {}", JSON.stringify(host.readRawSettings(newDir)) === "{}");

  // A UTF-8 BOM (as Windows PowerShell 5.1 Set-Content -Encoding UTF8 writes).
  fs.writeFileSync(host.settingsPath(newDir), "\uFEFF" + JSON.stringify({ gitUserName: "Neo" }));
  ok("read: strips a UTF-8 BOM before parsing", host.readRawSettings(newDir).gitUserName === "Neo");

  // ── mapToForm / mapFromForm ────────────────────────────────────────────────
  const form = host.mapToForm({
    gitUserName: "Neo", gitEmail: "neo@zion.io", gitCredentialStore: false,
    vmMemoryGB: 16, vmDiskGB: 120, ubuntuRelease: "24.04",
    vsCodeServeWeb: true, vsCodeTunnel: false, smbShare: true, micPassthrough: true,
  });
  ok("mapToForm: git interop keys -> form", form.gitName === "Neo" && form.gitEmail === "neo@zion.io" && form.gitCred === false);
  ok("mapToForm: numbers stringified for inputs", form.ram === "16" && form.disk === "120");
  ok("mapToForm: booleans pass through", form.serveWeb === true && form.tunnel === false && form.smb === true && form.mic === true);

  ok("mapToForm: absent keys are omitted (no clobber)",
    !("serveWeb" in host.mapToForm({ gitUserName: "Neo" })) && !("gitCred" in host.mapToForm({ gitUserName: "Neo" })));

  const disk = host.mapFromForm({
    gitName: " Neo ", gitEmail: "neo@zion.io", gitCred: true,
    ram: "16", disk: "120.5", ubuntu: "22.04",
    serveWeb: false, tunnel: true, smb: false, mic: true,
    password: "s3cret", agents: ["claude-code"], projects: ["default"],
  });
  ok("mapFromForm: git identity uses interop keys", disk.gitUserName === "Neo" && disk.gitEmail === "neo@zion.io" && disk.gitCredentialStore === true);
  ok("mapFromForm: trims string values", disk.gitUserName === "Neo");
  ok("mapFromForm: numeric coercion (int + float)", disk.vmMemoryGB === 16 && disk.vmDiskGB === 120.5);
  const exotic = host.mapFromForm({ ram: "1e3", disk: "+8" });
  ok("mapFromForm: coerces sci/signed number-input values", exotic.vmMemoryGB === 1000 && exotic.vmDiskGB === 8);
  ok("mapFromForm: non-numeric numeric-field falls back to string", host.mapFromForm({ ram: "abc" }).vmMemoryGB === "abc");
  ok("mapFromForm: booleans persisted incl. false", disk.vsCodeServeWeb === false && disk.vsCodeTunnel === true && disk.smbShare === false && disk.micPassthrough === true);
  ok("mapFromForm: password NEVER persisted", !("password" in disk) && !Object.values(disk).includes("s3cret"));
  ok("mapFromForm: agents/projects deferred (not written)", !("aiTools" in disk) && !("projects" in disk) && !("agents" in disk));

  ok("mapFromForm: empty text/number fields omitted (preserve prior)",
    Object.keys(host.mapFromForm({ gitName: "", ram: "  ", ubuntu: "" })).length === 0);

  ok("mapFromForm: null form -> {}", JSON.stringify(host.mapFromForm(null)) === "{}");

  // ── saveSettings: merge preserves unmanaged keys ───────────────────────────
  fs.writeFileSync(host.settingsPath(newDir), JSON.stringify({ installedCommit: "abc123", gitUserName: "Old" }));
  const merged = host.saveSettings(newDir, { gitName: "Neo", gitEmail: "neo@zion.io", serveWeb: true, password: "nope" });
  ok("save: preserves unmanaged keys (installedCommit)", merged.installedCommit === "abc123");
  ok("save: overwrites managed keys", merged.gitUserName === "Neo" && merged.gitEmail === "neo@zion.io");
  ok("save: password not on disk", !("password" in merged));

  const reread = host.readRawSettings(newDir);
  ok("save: round-trips through disk", reread.gitUserName === "Neo" && reread.installedCommit === "abc123" && reread.vsCodeServeWeb === true);

  ok("save: no scripts dir -> throws", (() => { try { host.saveSettings(null, {}); return false; } catch (_) { return true; } })());

  ok("readSettings: form shape from disk", (() => {
    const f = host.readSettings(newDir);
    return f.gitName === "Neo" && f.serveWeb === true;
  })());

  // ── readProjectProfile ─────────────────────────────────────────────────────
  const projDir = mk(newDir, "projects");
  fs.writeFileSync(path.join(projDir, "customer-portal.json"),
    JSON.stringify({ name: "customer-portal", repos: [{ url: "git@github.com:o/cp.git", directory: "cp" }] }));
  fs.writeFileSync(path.join(projDir, "bom.json"),
    "\uFEFF" + JSON.stringify({ name: "bom", repos: [] }));
  fs.writeFileSync(path.join(projDir, "broken.json"), "{ not json ]");
  fs.writeFileSync(path.join(projDir, "arr.json"), "[1,2,3]");

  ok("profile: reads a valid profile",
    (() => { const p = host.readProjectProfile(newDir, "customer-portal"); return p && p.name === "customer-portal" && p.repos[0].directory === "cp"; })());
  ok("profile: strips a UTF-8 BOM", (() => { const p = host.readProjectProfile(newDir, "bom"); return p && p.name === "bom"; })());
  ok("profile: missing file -> null", host.readProjectProfile(newDir, "nope") === null);
  ok("profile: malformed JSON -> null", host.readProjectProfile(newDir, "broken") === null);
  ok("profile: a JSON array (not an object) -> null", host.readProjectProfile(newDir, "arr") === null);
  ok("profile: no scripts dir -> null", host.readProjectProfile(null, "customer-portal") === null);
  ok("profile: a traversing name is rejected -> null",
    host.readProjectProfile(newDir, "../.construct-settings") === null &&
    host.readProjectProfile(newDir, "..\\x") === null &&
    host.readProjectProfile(newDir, "sub/x") === null);
  ok("profile: empty name -> null", host.readProjectProfile(newDir, "") === null && host.readProjectProfile(newDir, null) === null);

  // ── listProjectProfiles ────────────────────────────────────────────────────
  // projDir already has: customer-portal.json, bom.json, broken.json, arr.json.
  // Add the schema file (must be excluded) + a .sample (not *.json, excluded).
  fs.writeFileSync(path.join(projDir, "project.schema.json"), "{}");
  fs.writeFileSync(path.join(projDir, "example.json.sample"), "{}");
  fs.writeFileSync(path.join(projDir, "default.json"), JSON.stringify({ name: "default", repos: [] }));
  ok("list: base names, sorted, schema excluded, .sample excluded", (() => {
    const l = host.listProjectProfiles(newDir);
    return JSON.stringify(l) === JSON.stringify(["arr", "bom", "broken", "customer-portal", "default"]);
  })());
  ok("list: no scripts dir -> []", JSON.stringify(host.listProjectProfiles(null)) === "[]");
  ok("list: missing projects dir -> []", (() => {
    const empty = mk(root, "no-projects"); writeMarker(empty);
    return JSON.stringify(host.listProjectProfiles(empty)) === "[]";
  })());

  // ── writeProjectProfile (traversal-safe, BOM-less pretty JSON) ──────────────
  host.writeProjectProfile(newDir, "billing", { name: "billing", repos: [{ url: "https://h/b.git" }] });
  ok("write: round-trips through readProjectProfile",
    (() => { const p = host.readProjectProfile(newDir, "billing"); return p && p.name === "billing" && p.repos[0].url === "https://h/b.git"; })());
  ok("write: on-disk file is pretty + BOM-less + trailing newline", (() => {
    const raw = fs.readFileSync(path.join(projDir, "billing.json"), "utf8");
    return raw[0] === "{" && raw.includes('\n  "name"') && raw.endsWith("}\n");
  })());
  ok("write: creates the projects dir if absent", (() => {
    const fresh = mk(root, "fresh-write"); writeMarker(fresh);
    host.writeProjectProfile(fresh, "p", { name: "p" });
    return host.readProjectProfile(fresh, "p").name === "p";
  })());
  ok("write: uses the ARG name for the filename, not obj.name", (() => {
    host.writeProjectProfile(newDir, "real-name", { name: "spoofed" });
    return fs.existsSync(path.join(projDir, "real-name.json")) && !fs.existsSync(path.join(projDir, "spoofed.json"));
  })());
  const badWrite = (n) => { try { host.writeProjectProfile(newDir, n, {}); return false; } catch (_) { return true; } };
  ok("write: rejects a traversing name", badWrite("../evil") && badWrite("..\\evil") && badWrite("sub/x") && badWrite(".."));
  ok("write: rejects an empty name", badWrite("") && badWrite("   ") && badWrite(null));
  ok("write: no scripts dir -> throws", (() => { try { host.writeProjectProfile(null, "x", {}); return false; } catch (_) { return true; } })());
  ok("write: a traversing name never wrote a file outside projects/",
    !fs.existsSync(path.join(newDir, "evil.json")) && !fs.existsSync(path.join(newDir, "..", "evil.json")));

  // ── safeProfileName ────────────────────────────────────────────────────────
  ok("safeName: trims a valid name", host.safeProfileName("  billing  ") === "billing");
  ok("safeName: rejects separators / .. / empty",
    host.safeProfileName("a/b") === "" && host.safeProfileName("a\\b") === "" &&
    host.safeProfileName("..") === "" && host.safeProfileName("a..b") === "" &&
    host.safeProfileName("") === "" && host.safeProfileName(null) === "");

  // ── read/save SelectedProjects (forward-compat `projects` key) ─────────────-
  // Start from a clean settings file so prior git keys don't confuse the merge check.
  fs.writeFileSync(host.settingsPath(newDir), JSON.stringify({ installedCommit: "sha1", gitUserName: "Neo" }));
  ok("select: absent `projects` key -> []", JSON.stringify(host.readSelectedProjects(newDir)) === "[]");
  const selMerged = host.saveSelectedProjects(newDir, ["billing", "billing", "customer-portal", "a/b", ""]);
  ok("select: save de-dupes + drops unsafe names",
    JSON.stringify(selMerged.projects) === JSON.stringify(["billing", "customer-portal"]));
  ok("select: save preserves unmanaged keys", selMerged.installedCommit === "sha1" && selMerged.gitUserName === "Neo");
  ok("select: read reflects the saved selection",
    JSON.stringify(host.readSelectedProjects(newDir)) === JSON.stringify(["billing", "customer-portal"]));
  ok("select: read tolerates a malformed (non-array) value", (() => {
    fs.writeFileSync(host.settingsPath(newDir), JSON.stringify({ projects: "nope" }));
    return JSON.stringify(host.readSelectedProjects(newDir)) === "[]";
  })());
  ok("select: read drops unsafe/empty stored names (keeps safe ones)", (() => {
    // "../evil" and "a/b" are traversal/separator names and dropped; "" is dropped;
    // a bare number stringifies to a legal filename ("5") and is kept.
    fs.writeFileSync(host.settingsPath(newDir), JSON.stringify({ projects: ["ok", "../evil", "a/b", 5, ""] }));
    return JSON.stringify(host.readSelectedProjects(newDir)) === JSON.stringify(["ok", "5"]);
  })());
  ok("select: save non-array clears to []", (() => {
    const m = host.saveSelectedProjects(newDir, "nope");
    return JSON.stringify(m.projects) === "[]";
  })());
  ok("select: save no scripts dir -> throws", (() => { try { host.saveSelectedProjects(null, []); return false; } catch (_) { return true; } })());
} finally {
  fs.rmSync(root, { recursive: true, force: true });
}

console.log(`\n  host locator/settings unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
