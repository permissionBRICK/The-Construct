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
} finally {
  fs.rmSync(root, { recursive: true, force: true });
}

console.log(`\n  host locator/settings unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
