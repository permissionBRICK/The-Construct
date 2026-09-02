"use strict";
// Plain-node unit tests for the config-sync engine (src/configsync.js).
// Exercises the engine end-to-end with REAL git in mkdtemp dirs and REAL bash
// scripts against fake store dirs. No deps. Run: node configsync.test.js
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execSync, spawn } = require("child_process");
const cs = require("../src/configsync");
const projects = require("../src/projects");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// ── Helpers ──────────────────────────────────────────────────────────────────

const mk = (...p) => { const d = path.join(...p); fs.mkdirSync(d, { recursive: true }); return d; };

const runGit = cs.makeGitRunner({ spawn });

/** Write a profile to the config repo's projects/ dir. */
function writeProfile(configDir, name, obj) {
  const dir = path.join(configDir, "projects");
  fs.mkdirSync(dir, { recursive: true });
  const content = projects.canonicalProfileJson(name, obj);
  fs.writeFileSync(path.join(dir, name + ".json"), content, "utf8");
}

/** Read a profile from the config repo's working tree. */
function readProfile(configDir, name) {
  try {
    const raw = fs.readFileSync(path.join(configDir, "projects", name + ".json"), "utf8");
    return JSON.parse(raw);
  } catch (_) { return null; }
}

/** Run a bash script and return stdout. */
function runBash(script) {
  return execSync("bash", { input: script, encoding: "utf8", stdio: ["pipe", "pipe", "pipe"] });
}

/** Create a readStore function that reads from a fake store dir via the REAL bash script. */
function makeReadStore(storeDir) {
  return async () => {
    const script = cs.buildReadStoreScript(storeDir);
    try { return runBash(script); }
    catch (_) { return null; }
  };
}

/** Create a writeStore function that runs the script via REAL bash. */
function makeWriteStore() {
  return async (script) => {
    try { return runBash(script); }
    catch (_) { return null; }
  };
}

/** Write a profile to the fake VM store. */
function writeStoreProfile(storeDir, name, obj) {
  fs.mkdirSync(storeDir, { recursive: true });
  const content = projects.canonicalProfileJson(name, obj);
  fs.writeFileSync(path.join(storeDir, name + ".json"), content, "utf8");
}

/** Read a profile from the fake VM store. */
function readStoreProfile(storeDir, name) {
  try {
    const raw = fs.readFileSync(path.join(storeDir, name + ".json"), "utf8");
    return JSON.parse(raw);
  } catch (_) { return null; }
}

// ── Test suite ───────────────────────────────────────────────────────────────

async function runTests() {
  // ── parseReadStore / parseWriteResult sentinel behavior ──────────────────
  ok("parseReadStore: null on missing sentinel", cs.parseReadStore("foo\tYmFy\n") === null);
  ok("parseReadStore: null on null input", cs.parseReadStore(null) === null);
  ok("parseReadStore: empty result with sentinel", (() => {
    const r = cs.parseReadStore("END\n");
    return r && eq(r.entries, []) && r.storeAbsent === false;
  })());
  ok("parseReadStore: parses name+content", (() => {
    const b64 = Buffer.from('{"name":"x"}').toString("base64");
    const result = cs.parseReadStore("x\t" + b64 + "\nEND\n");
    return result && result.entries.length === 1 && result.entries[0].name === "x" && result.entries[0].content === '{"name":"x"}';
  })());
  ok("parseReadStore: storeAbsent marker detected", (() => {
    const r = cs.parseReadStore("STORE_ABSENT\nEND\n");
    return r && r.storeAbsent === true && eq(r.entries, []);
  })());

  ok("parseWriteResult: null on missing sentinel", cs.parseWriteResult("foo\tdone\n") === null);
  ok("parseWriteResult: parses done/skipped", (() => {
    const r = cs.parseWriteResult("a\tdone\nb\tskipped\nc\tdone\nEND\n");
    return r && eq(r.done, ["a", "c"]) && eq(r.skipped, ["b"]);
  })());
  ok("parseWriteResult: empty with sentinel", (() => {
    const r = cs.parseWriteResult("END\n");
    return r && eq(r.done, []) && eq(r.skipped, []);
  })());

  // ── remoteSlug ─────────────────────────────────────────────────────────────
  ok("remoteSlug: replaces non-alphanum", cs.remoteSlug("https://git.co/repo.git") === "https---git.co-repo.git");
  ok("remoteSlug: keeps safe chars", cs.remoteSlug("foo-bar_baz.git") === "foo-bar_baz.git");
  ok("remoteSlug: empty string", cs.remoteSlug("") === "");

  // ── detectGit ──────────────────────────────────────────────────────────────
  const git = await cs.detectGit(runGit);
  ok("detectGit: git is present", git.present);
  ok("detectGit: has a version string", typeof git.version === "string" && git.version.length > 0);

  // ── ensureConfigTree ───────────────────────────────────────────────────────
  const treeRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-tree-"));
  try {
    cs.ensureConfigTree(treeRoot);
    ok("ensureConfigTree: creates projects/", fs.statSync(path.join(treeRoot, "projects")).isDirectory());
    ok("ensureConfigTree: creates manifest/", fs.statSync(path.join(treeRoot, "manifest")).isDirectory());
    ok("ensureConfigTree: creates bases/", fs.statSync(path.join(treeRoot, "bases")).isDirectory());
    // Idempotent.
    cs.ensureConfigTree(treeRoot);
    ok("ensureConfigTree: idempotent", fs.statSync(path.join(treeRoot, "projects")).isDirectory());
  } finally { fs.rmSync(treeRoot, { recursive: true, force: true }); }

  // ── cross-process sync lock ────────────────────────────────────────────────
  const lockRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-lock-"));
  try {
    const cfg = mk(lockRoot, "config");
    const lp = path.join(cfg, cs.SYNC_LOCK_FILE);
    const t1 = cs.acquireSyncLock(cfg);
    ok("lock: acquire returns a token", typeof t1 === "string" && t1.length > 0);
    ok("lock: file exists while held", fs.existsSync(lp));
    ok("lock: second acquire fails while held", cs.acquireSyncLock(cfg) === null);
    cs.releaseSyncLock(cfg, t1);
    ok("lock: file gone after release", !fs.existsSync(lp));
    const t2 = cs.acquireSyncLock(cfg);
    ok("lock: re-acquire after release", !!t2);
    // Stale takeover: backdate the lock past the stale threshold.
    const old = new Date(Date.now() - 10 * 60 * 1000);
    fs.utimesSync(lp, old, old);
    const t3 = cs.acquireSyncLock(cfg);
    ok("lock: stale lock is broken and re-acquired", !!t3 && t3 !== t2);
    // Ownership: the broken holder's release must NOT delete the new holder's
    // lock — its token no longer matches.
    cs.releaseSyncLock(cfg, t2);
    ok("lock: stale ex-holder's release leaves the new lock alone", fs.existsSync(lp));
    ok("lock: new lock still excludes others", cs.acquireSyncLock(cfg) === null);
    cs.releaseSyncLock(cfg, t3);
    ok("lock: owner release removes it", !fs.existsSync(lp));
    ok("lock: double release is harmless", (() => { cs.releaseSyncLock(cfg, t3); return true; })());
    fs.writeFileSync(lp, JSON.stringify({ token: "dead", pid: 2147483647, at: new Date().toISOString() }));
    const t4 = cs.acquireSyncLock(cfg);
    ok("lock: a fresh lock whose owner process is gone is recovered immediately", !!t4);
    cs.releaseSyncLock(cfg, t4);
    const intent = path.join(cfg, cs.PROVISION_SYNC_INTENT_FILE);
    fs.writeFileSync(intent, JSON.stringify({ token: "live", pid: process.pid, at: new Date().toISOString() }));
    ok("lock: live provisioning intent makes extension ticks yield", cs.provisionSyncPending(cfg));
    fs.writeFileSync(intent, JSON.stringify({ token: "dead", pid: 2147483647, at: new Date().toISOString() }));
    ok("lock: dead provisioning intent is removed immediately", !cs.provisionSyncPending(cfg) && !fs.existsSync(intent));
  } finally { fs.rmSync(lockRoot, { recursive: true, force: true }); }

  // ── ensureRepo: lazy init + idempotence ────────────────────────────────────
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-repo-"));
  try {
    const configDir = mk(repoRoot, "config");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });

    const r1 = await cs.ensureRepo(runGit, configDir);
    ok("ensureRepo: initializes new repo", r1.repo && r1.initialized);

    // Check branches exist.
    const br = await runGit(["branch", "--list"], { cwd: configDir });
    ok("ensureRepo: main branch exists", br.stdout.includes("main"));
    ok("ensureRepo: vm branch exists", br.stdout.includes("vm"));

    // Check initial commit includes the profile.
    const show = await runGit(["show", "main:projects/web.json"], { cwd: configDir });
    ok("ensureRepo: initial commit has the profile", show.stdout.includes('"web"'));

    // Idempotent.
    const r2 = await cs.ensureRepo(runGit, configDir);
    ok("ensureRepo: idempotent (no re-init)", r2.repo && !r2.initialized);
  } finally { fs.rmSync(repoRoot, { recursive: true, force: true }); }

  // ── ensureRepo: dubious-ownership detection (git refuses foreign-owned dirs) ─
  // Simulated via a fake git runner: producing a genuinely foreign-owned dir
  // needs a second OS account. The stderr text is git's verbatim message.
  {
    const dubious = 'fatal: detected dubious ownership in repository at ' +
      "'C:/Users/other/AppData/Local/The-Construct/config'";
    const fakeGit = async () => ({ code: 128, stdout: "", stderr: dubious });
    const r = await cs.ensureRepo(fakeGit, "/nonexistent-config");
    ok("ensureRepo: dubious ownership detected on rev-parse", r.repo === false && r.dubiousOwnership === true);

    // rev-parse fails generically (no repo), then init fails dubious.
    const fakeGit2 = async (args) => args[0] === "init"
      ? { code: 128, stdout: "", stderr: dubious }
      : { code: 128, stdout: "", stderr: "fatal: not a git repository" };
    const r2 = await cs.ensureRepo(fakeGit2, "/nonexistent-config");
    ok("ensureRepo: dubious ownership detected on init", r2.repo === false && r2.dubiousOwnership === true);

    // Generic failure stays generic.
    const fakeGit3 = async () => ({ code: 1, stdout: "", stderr: "some other failure" });
    const r3 = await cs.ensureRepo(fakeGit3, "/nonexistent-config");
    ok("ensureRepo: generic failure not marked dubious", r3.repo === false && !r3.dubiousOwnership);

    // A tick against a dubious-owned repo reports blocked with the repair hint
    // (not the misleading "git not available").
    const tickRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-dub-"));
    try {
      const configDir = mk(tickRoot, "config");
      cs.ensureConfigTree(configDir);
      const res = await cs.syncTick({
        runGit: fakeGit, configDir,
        readStore: async () => "END\n",
        writeStore: async () => "END\n",
      });
      ok("syncTick: dubious ownership -> blocked", res.blocked === true && !res.ok);
      ok("syncTick: dubious ownership names the fix", /owned by another Windows account/.test(res.blockedReason || "") && /icacls/.test(res.blockedReason || ""));
    } finally { fs.rmSync(tickRoot, { recursive: true, force: true }); }
  }

  // ── First tick: seeds vm = main ────────────────────────────────────────────
  const seedRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-seed-"));
  try {
    const configDir = mk(seedRoot, "config");
    // Do NOT pre-create the store dir — a fresh VM has no store dir, so the
    // read script emits STORE_ABSENT and the D13 seed path fires. The write
    // script creates the dir with mkdir -p.
    const storeDir = path.join(seedRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Absent store = fresh VM.
    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("seed: first tick seeds", result.seeded);
    ok("seed: first tick ok", result.ok);
    // The profile should now be in the store.
    const storeProfile = readStoreProfile(storeDir, "web");
    ok("seed: profile written to store", storeProfile && storeProfile.name === "web");
  } finally { fs.rmSync(seedRoot, { recursive: true, force: true }); }

  // ── VM-only edit merges to main and vm fast-forwards ───────────────────────
  const vmEditRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-vmedit-"));
  try {
    const configDir = mk(vmEditRoot, "config");
    const storeDir = path.join(vmEditRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed first.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Simulate a VM-side edit: add an SDK to the store profile.
    const edited = { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } };
    writeStoreProfile(storeDir, "web", edited);

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-edit: tick ran", result.ran);
    ok("vm-edit: merged", result.merged);
    ok("vm-edit: ok", result.ok);

    // The host profile should now have the SDK.
    const hostProfile = readProfile(configDir, "web");
    ok("vm-edit: host has VM's SDK addition", hostProfile && hostProfile.sdks && hostProfile.sdks.node === "22");

    // vm and main should be at the same point.
    const vmRef = await runGit(["rev-parse", "vm"], { cwd: configDir });
    const mainRef = await runGit(["rev-parse", "main"], { cwd: configDir });
    ok("vm-edit: vm fast-forwarded to main", vmRef.stdout.trim() === mainRef.stdout.trim());
  } finally { fs.rmSync(vmEditRoot, { recursive: true, force: true }); }

  // ── Host-only edit writes back to store ────────────────────────────────────
  const hostEditRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-hostedit-"));
  try {
    const configDir = mk(hostEditRoot, "config");
    const storeDir = path.join(hostEditRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host-side edit: add a new profile to the config dir.
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("host-edit: tick ok", result.ok);
    // The new profile should be written to the store.
    const storeApi = readStoreProfile(storeDir, "api");
    ok("host-edit: new profile written to store", storeApi && storeApi.name === "api");
  } finally { fs.rmSync(hostEditRoot, { recursive: true, force: true }); }

  // ── Both-sides non-overlapping edits merge ─────────────────────────────────
  const bothRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-both-"));
  try {
    const configDir = mk(bothRoot, "config");
    const storeDir = path.join(bothRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host adds a new profile.
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    // VM adds SDK to existing profile.
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } });

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("both: tick ok", result.ok);
    ok("both: merged", result.merged);
    // Host should have both changes.
    const hostWeb = readProfile(configDir, "web");
    ok("both: VM edit visible on host", hostWeb && hostWeb.sdks && hostWeb.sdks.node === "22");
    ok("both: host addition visible on host", readProfile(configDir, "api") !== null);
    // Store should have both.
    ok("both: host addition in store", readStoreProfile(storeDir, "api") !== null);
  } finally { fs.rmSync(bothRoot, { recursive: true, force: true }); }

  // ── Same-file conflicting edits => conflict:true ───────────────────────────
  const conflictRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-conflict-"));
  try {
    const configDir = mk(conflictRoot, "config");
    const storeDir = path.join(conflictRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host changes the repo URL.
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/host-version.git" }] });
    // VM changes the repo URL differently.
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/vm-version.git" }] });

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("conflict: tick detects conflict", result.conflict);
    ok("conflict: tick not ok", !result.ok);

    // Repo should be left in conflicted state.
    const state = await cs.repoState(runGit, configDir);
    ok("conflict: repo is conflicted", state.conflict || state.mergeInProgress);

    // ── Resolve + next tick recovers ────────────────────────────────────────
    // Resolve by checking out ours and committing.
    await runGit(["checkout", "--ours", "--", "projects/web.json"], { cwd: configDir });
    await runGit(["-c", "user.name=Test", "-c", "user.email=test@test.com", "add", "-A"], { cwd: configDir });
    await runGit(["-c", "user.name=Test", "-c", "user.email=test@test.com", "commit", "-m", "resolve"], { cwd: configDir });

    // Update store to match so the next tick doesn't re-conflict.
    const resolved = readProfile(configDir, "web");
    writeStoreProfile(storeDir, "web", resolved);

    const result2 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("conflict-recover: next tick ok after resolve", result2.ok);
    ok("conflict-recover: next tick ran", result2.ran);
  } finally { fs.rmSync(conflictRoot, { recursive: true, force: true }); }

  // ── Invalid VM file skipped (never committed) with warning ─────────────────
  const invalidRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-invalid-"));
  try {
    const configDir = mk(invalidRoot, "config");
    const storeDir = path.join(invalidRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Write an invalid profile to the store (repos as string, not array).
    fs.writeFileSync(path.join(storeDir, "bad.json"), '{"name":"bad","repos":"not-array"}', "utf8");

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("invalid-vm: tick ok", result.ok);
    ok("invalid-vm: bad profile skipped", result.skippedInvalid.some((s) => s.name === "bad"));
    ok("invalid-vm: warning issued", result.warnings.some((w) => w.includes("bad")));
    // Bad profile should NOT be on the host.
    ok("invalid-vm: bad profile not on host", readProfile(configDir, "bad") === null);
  } finally { fs.rmSync(invalidRoot, { recursive: true, force: true }); }

  // ── A previously-SYNCED profile becoming INVALID on the VM must NOT delete the
  //    host copy (skip-invalid ≠ deletion). Regression for the external review. ──
  const staleRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-staleinvalid-"));
  try {
    const configDir = mk(staleRoot, "config");
    const storeDir = path.join(staleRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed: web.json is written to the VM store and becomes the agreed vm/main state.
    await cs.syncTick({
      runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("stale-invalid: seeded web to store", readStoreProfile(storeDir, "web") !== null);
    ok("stale-invalid: web on host after seed", readProfile(configDir, "web") !== null);

    // The agent half-writes web.json on the VM, corrupting it (repos as a string).
    fs.writeFileSync(path.join(storeDir, "web.json"), '{"name":"web","repos":"corrupt"}', "utf8");

    const result = await cs.syncTick({
      runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("stale-invalid: tick ok", result.ok);
    ok("stale-invalid: web reported skipped", result.skippedInvalid.some((s) => s.name === "web"));
    // The corrupt VM edit must be treated as skip, NOT a deletion: the host keeps
    // its last agreed-valid copy, and the vm branch is not advanced with a deletion.
    ok("stale-invalid: host web PRESERVED (not deleted)", readProfile(configDir, "web") !== null);
    ok("stale-invalid: host web still the valid version",
      JSON.stringify((readProfile(configDir, "web") || {}).repos) === JSON.stringify([{ url: "https://h/w.git" }]));
    // A subsequent tick after the agent fixes the file should sync the new valid edit.
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], hostPackages: ["jq"] });
    const result3 = await cs.syncTick({
      runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("stale-invalid: recovery tick ok", result3.ok);
    ok("stale-invalid: fixed edit synced to host",
      JSON.stringify((readProfile(configDir, "web") || {}).hostPackages) === JSON.stringify(["jq"]));
  } finally { fs.rmSync(staleRoot, { recursive: true, force: true }); }

  // ── Reserved default.json in store ignored ─────────────────────────────────
  const reservedRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-reserved-"));
  try {
    const configDir = mk(reservedRoot, "config");
    const storeDir = path.join(reservedRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Add a default.json to the store (reserved, should be ignored).
    fs.writeFileSync(path.join(storeDir, "default.json"), '{"name":"default","repos":[]}', "utf8");

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("reserved: tick ok", result.ok);
    // A leftover default.json is the normal state on upgraded VMs: ignored
    // silently (info log only), never a panel warning, never tracked on main.
    ok("reserved: no warning about reserved name", !result.warnings.some((w) => w.includes("reserved") && w.includes("default")));
    ok("reserved: not committed to config repo", !fs.existsSync(path.join(configDir, "projects", "default.json")));
  } finally { fs.rmSync(reservedRoot, { recursive: true, force: true }); }

  // ── VM deletion propagates to main ─────────────────────────────────────────
  const vmDelRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-vmdel-"));
  try {
    const configDir = mk(vmDelRoot, "config");
    const storeDir = path.join(vmDelRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Delete 'api' from the store.
    fs.unlinkSync(path.join(storeDir, "api.json"));

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-del: tick ok", result.ok);
    ok("vm-del: merged", result.merged);
    // api should be gone from the host.
    ok("vm-del: api deleted from host", readProfile(configDir, "api") === null);
    // web should still be there.
    ok("vm-del: web still on host", readProfile(configDir, "web") !== null);
    const deleted = await cs.deletedProfileIdentities(runGit, configDir, ["web"]);
    ok("vm-del: deletion history suppresses repo auto-import by name and URL",
      deleted.names.has("api") && deleted.urls.has("https://h/a.git"));

    // ── Mass-deletion guard ──────────────────────────────────────────────────
    // Empty the store entirely (dir stays). The vm branch has profiles, so this
    // must NOT be read as "user deleted everything" — the tick refuses with a
    // warning and main keeps its profiles.
    fs.unlinkSync(path.join(storeDir, "web.json"));
    const massDel = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("mass-del: tick ok", massDel.ok);
    ok("mass-del: warned about refusing", massDel.warnings.some((w) => w.includes("mass deletion")));
    ok("mass-del: web survives on host", readProfile(configDir, "web") !== null);
    // The guard must re-seed the emptied store instead of stalling forever
    // (rebuilt-VM case: provisioning recreates the dir before the first seed).
    ok("mass-del: store re-seeded from main", readStoreProfile(storeDir, "web") !== null);
    ok("mass-del: reported seeded", massDel.seeded === true);

    // Same guard when the store holds only an INVALID file (zero valid reads).
    fs.writeFileSync(path.join(storeDir, "web.json"), "{ not json");
    const invOnly = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("mass-del: invalid-only store also refused", invOnly.ok && invOnly.warnings.some((w) => w.includes("mass deletion")));
    ok("mass-del: web still on host after invalid-only read", readProfile(configDir, "web") !== null);
    // The absent-guard must protect the invalid file from the re-seed write.
    ok("mass-del: invalid store file preserved by absent-guard",
      fs.readFileSync(path.join(storeDir, "web.json"), "utf8") === "{ not json");
    ok("mass-del: invalid-only tick not reported seeded", invOnly.seeded !== true);

    // ── syncTick honors the cross-process lock ───────────────────────────────
    const tickToken = cs.acquireSyncLock(configDir);
    ok("tick-lock: acquire externally", !!tickToken);
    const busy = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("tick-lock: busy tick skipped", busy.ok && busy.lockBusy === true && busy.ran === false);
    cs.releaseSyncLock(configDir, tickToken);
    const after = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("tick-lock: runs after release", after.ran === true && !after.lockBusy);
    ok("tick-lock: lock released after tick", !fs.existsSync(path.join(configDir, cs.SYNC_LOCK_FILE)));
  } finally { fs.rmSync(vmDelRoot, { recursive: true, force: true }); }

  // ── Host deletion propagates to store (guarded) ────────────────────────────
  const hostDelRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-hostdel-"));
  try {
    const configDir = mk(hostDelRoot, "config");
    const storeDir = path.join(hostDelRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("host-del: both profiles in store after seed",
      readStoreProfile(storeDir, "web") !== null && readStoreProfile(storeDir, "api") !== null);

    // Delete 'api' from the host.
    fs.unlinkSync(path.join(configDir, "projects", "api.json"));

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("host-del: tick ok", result.ok);
    // api should be removed from the store.
    ok("host-del: api deleted from store", readStoreProfile(storeDir, "api") === null);
    // web still in store.
    ok("host-del: web still in store", readStoreProfile(storeDir, "web") !== null);
  } finally { fs.rmSync(hostDelRoot, { recursive: true, force: true }); }

  // ── VM deletion of the LAST profile is refused (mass-deletion guard) ──────
  // Store dir exists but is empty while the vm branch has profiles. This state
  // is indistinguishable from a half-provisioned store (observed in the field
  // mass-deleting profiles), so the tick must neither seed (the old D13 fix 2
  // pinned that — resurrection) NOR propagate the deletion (the guard): it
  // skips the VM side with a warning. Deleting the last profile is done from
  // the host/panel side instead.
  const vmSingleDelRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-vmsingle-"));
  try {
    const configDir = mk(vmSingleDelRoot, "config");
    const storeDir = path.join(vmSingleDelRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-single-del: profile seeded to store", readStoreProfile(storeDir, "web") !== null);

    // Do a normal tick so vm branch gets a proper sync commit (vm tip has profiles
    // from a real sync, not just from ensureRepo's initial commit).
    const result1 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-single-del: second tick ok", result1.ok);

    // Now delete the ONLY profile from the store (store dir exists, empty).
    fs.unlinkSync(path.join(storeDir, "web.json"));

    const result2 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-single-del: tick ok", result2.ok);
    // Must NOT propagate a to-zero deletion: guard refuses and main keeps it.
    ok("vm-single-del: refused with mass-deletion warning", result2.warnings.some((w) => w.includes("mass deletion")));
    ok("vm-single-del: profile kept on main", readProfile(configDir, "web") !== null);
    // INTENT CHANGE (rebuilt-VM stall fix): the guard now re-seeds the emptied
    // store from main. A zero-valid store with a non-empty vm branch is
    // indistinguishable from a rebuilt/wiped VM, and leaving it empty stalled
    // every later tick (observed in the field: a reinstalled VM never got its
    // profiles back). The cost: rm-ing the LAST profile on the VM resurrects
    // it — delete the last profile from the panel/host instead.
    ok("vm-single-del: store re-seeded from main", readStoreProfile(storeDir, "web") !== null);
    ok("vm-single-del: reported seeded", result2.seeded === true);
  } finally { fs.rmSync(vmSingleDelRoot, { recursive: true, force: true }); }

  // ── D13 store-absent after host-only commit (regression: Fix 1) ────────────
  // After writeRemotes + commitAll (the P2 add-remote flow), main advances past
  // vm. If the store dir is then removed (wiped VM), the tick must seed from
  // main — NOT commit a mass-deletion vm commit and merge it.
  const d13Root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-d13-"));
  try {
    const configDir = mk(d13Root, "config");
    const storeDir = path.join(d13Root, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed first.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Simulate the P2 add-remote flow: writeRemotes + commitAll advances main past vm.
    cs.writeRemotes(configDir, [{ url: "https://git.co/cfg.git" }]);
    await cs.commitAll(runGit, configDir, "link remote");

    // Verify main has moved past vm.
    const vmRef = (await runGit(["rev-parse", "vm"], { cwd: configDir })).stdout.trim();
    const mainRef = (await runGit(["rev-parse", "main"], { cwd: configDir })).stdout.trim();
    ok("d13: main advanced past vm", vmRef !== mainRef);

    // Wipe the store dir (simulate a VM reinstall).
    fs.rmSync(storeDir, { recursive: true, force: true });

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("d13: seeded (not mass-deletion)", result.seeded);
    ok("d13: tick ok", result.ok);
    // All profiles must still be on main.
    ok("d13: web still on main", readProfile(configDir, "web") !== null);
    ok("d13: api still on main", readProfile(configDir, "api") !== null);
    // Profiles must be seeded to the new store.
    ok("d13: web seeded to store", readStoreProfile(storeDir, "web") !== null);
    ok("d13: api seeded to store", readStoreProfile(storeDir, "api") !== null);
  } finally { fs.rmSync(d13Root, { recursive: true, force: true }); }

  // ── Write-back guard: store file changed between read and write => skipped ─
  const guardRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-guard-"));
  try {
    const configDir = mk(guardRoot, "config");
    const storeDir = path.join(guardRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host edit.
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } });

    // Intercept: the readStore reads the current store, but between read and write
    // the store file changes (simulating a concurrent agent edit).
    let readStoreCallCount = 0;
    const interceptReadStore = async () => {
      const script = cs.buildReadStoreScript(storeDir);
      const result = runBash(script);
      readStoreCallCount++;
      return result;
    };
    const interceptWriteStore = async (script) => {
      // Before running the write, modify the store file (simulating a race).
      writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { python: "3.14" } });
      return runBash(script);
    };

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: interceptReadStore,
      writeStore: interceptWriteStore,
      storeRoot: storeDir,
    });
    ok("guard: tick ok", result.ok);
    // The write should be skipped because the store changed.
    ok("guard: write-back skipped due to guard", result.writeBack.skipped.includes("web"));
    // The store should still have the concurrent edit (python), not the host edit (node).
    const storeWeb = readStoreProfile(storeDir, "web");
    ok("guard: store has concurrent edit, not host's", storeWeb && storeWeb.sdks && storeWeb.sdks.python === "3.14");
    const mainAfterSkip = await runGit(["rev-parse", "main"], { cwd: configDir });
    const vmAfterSkip = await runGit(["rev-parse", "vm"], { cwd: configDir });
    ok("guard: skipped write-back does not falsely advance the shared sync base",
      mainAfterSkip.stdout.trim() !== vmAfterSkip.stdout.trim());
    const retry = await cs.syncTick({
      runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("guard: next tick exposes the concurrent same-file edits as a conflict", retry.conflict && !retry.ok);
    ok("guard: host edit was not silently replaced after the skipped write",
      fs.readFileSync(path.join(configDir, "projects", "web.json"), "utf8").includes('"node": "22"'));
  } finally { fs.rmSync(guardRoot, { recursive: true, force: true }); }

  // ── Non-canonical VM file gets normalized on write-back ─────────────────────
  const canonRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-canon-"));
  try {
    const configDir = mk(canonRoot, "config");
    const storeDir = path.join(canonRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Write a valid but non-canonical profile to the store (wrong key order, extra whitespace).
    const nonCanonical = JSON.stringify({ repos: [{ url: "https://h/new.git" }], name: "new", sdks: {} }, null, 4) + "\n";
    fs.mkdirSync(storeDir, { recursive: true });
    fs.writeFileSync(path.join(storeDir, "new.json"), nonCanonical, "utf8");

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("canon: tick ok", result.ok);
    // The store should now have the canonicalized version.
    const storeRaw = fs.readFileSync(path.join(storeDir, "new.json"), "utf8");
    const expected = projects.canonicalProfileJson("new", { name: "new", repos: [{ url: "https://h/new.git" }] });
    ok("canon: store has canonical form after write-back", storeRaw === expected);
  } finally { fs.rmSync(canonRoot, { recursive: true, force: true }); }

  // ── Fresh-VM seed path ─────────────────────────────────────────────────────
  const freshRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-fresh-"));
  try {
    const configDir = mk(freshRoot, "config");
    const storeDir = path.join(freshRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Do one normal tick to have history, then simulate a "fresh VM" (empty store dir).
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Now wipe the store entirely to simulate a fresh/reinstalled VM. The store
    // dir is REMOVED (not just emptied) so the read script emits STORE_ABSENT.
    fs.rmSync(storeDir, { recursive: true, force: true });

    // Reset the vm branch to have 0 profiles (simulate reinstall).
    // We do this by removing projects from vm branch.
    const tmpIdx = path.join(configDir, ".git", "tmp-fresh-idx");
    await runGit(["read-tree", "vm"], { cwd: configDir, env: { GIT_INDEX_FILE: tmpIdx } });
    await runGit(["rm", "--cached", "-r", "--", "projects/"], { cwd: configDir, env: { GIT_INDEX_FILE: tmpIdx } });
    const wt = await runGit(["write-tree"], { cwd: configDir, env: { GIT_INDEX_FILE: tmpIdx } });
    const vmTip = await runGit(["rev-parse", "vm"], { cwd: configDir });
    const ct = await runGit(
      ["-c", "user.name=Test", "-c", "user.email=t@t", "commit-tree", wt.stdout.trim(), "-p", vmTip.stdout.trim(), "-m", "wipe"],
      { cwd: configDir }
    );
    await runGit(["update-ref", "refs/heads/vm", ct.stdout.trim()], { cwd: configDir });
    try { fs.unlinkSync(tmpIdx); } catch (_) { /* ok */ }

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("fresh: seeded", result.seeded);
    ok("fresh: ok", result.ok);
    // Both profiles should be in the store.
    ok("fresh: web in store", readStoreProfile(storeDir, "web") !== null);
    ok("fresh: api in store", readStoreProfile(storeDir, "api") !== null);
  } finally { fs.rmSync(freshRoot, { recursive: true, force: true }); }

  // ── Post-merge validation gate ─────────────────────────────────────────────
  // The gate catches invalid profiles in the working tree after a clean merge.
  // Since canonical JSON makes line-merge artifacts essentially unreachable, we
  // test the gate by wrapping runGit so that immediately after the merge
  // invocation returns, we corrupt a profile in the working tree. This simulates
  // the (rare) scenario where git's text merge produces invalid JSON.
  const gateRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-gate-"));
  try {
    const configDir = mk(gateRoot, "config");
    const storeDir = path.join(gateRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host: add provisionCommands.
    writeProfile(configDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      provisionCommands: ["npm ci"],
    });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: configDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "host edit"], { cwd: configDir });

    // VM: add an SDK change (different from the host edit so they merge cleanly).
    writeStoreProfile(storeDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      sdks: { python: "3.14" },
    });

    // Wrap runGit: after the merge --no-ff --no-commit vm invocation returns
    // successfully, corrupt projects/web.json in the working tree so the
    // post-merge validation gate triggers.
    const wrappedRunGit = function(args, opts) {
      const result = runGit(args, opts);
      return result.then((r) => {
        const isMerge = args[args.length - 3] === "--no-ff"
                     && args[args.length - 2] === "--no-commit"
                     && args[args.length - 1] === "vm";
        if (isMerge && r.code === 0) {
          // Corrupt: inject an unknown key into the merged file.
          const webPath = path.join(configDir, "projects", "web.json");
          try {
            const content = JSON.parse(fs.readFileSync(webPath, "utf8"));
            content.INJECTED_INVALID_KEY = true;
            fs.writeFileSync(webPath, JSON.stringify(content, null, 2) + "\n", "utf8");
          } catch (_) { /* ignore */ }
        }
        return r;
      });
    };

    const gateResult = await cs.syncTick({
      runGit: wrappedRunGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("gate: result.blocked is true", gateResult.blocked === true);
    ok("gate: blockedReason mentions validation", gateResult.blockedReason && gateResult.blockedReason.includes("validation"));
    ok("gate: merge left uncommitted (MERGE_HEAD present)", (() => {
      try {
        const mhPath = path.join(configDir, ".git", "MERGE_HEAD");
        return fs.existsSync(mhPath);
      } catch (_) { return false; }
    })());
    ok("gate: not ok (blocked)", !gateResult.ok);

    // Verify next tick reports blocked until resolved.
    const gateResult2 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("gate: next tick blocked (unresolved merge)", gateResult2.blocked === true);

    // Resolve: fix the file, add, commit.
    writeProfile(configDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      provisionCommands: ["npm ci"], sdks: { python: "3.14" },
    });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: configDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "resolve gate"], { cwd: configDir });

    // Update store so next tick doesn't re-conflict.
    writeStoreProfile(storeDir, "web", readProfile(configDir, "web"));

    const gateResult3 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("gate: tick ok after gate resolution", gateResult3.ok);
  } finally { fs.rmSync(gateRoot, { recursive: true, force: true }); }

  // ── Clean pending merge is auto-committed on the next tick ────────────────
  const pendingCleanRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-pendingclean-"));
  try {
    const configDir = mk(pendingCleanRoot, "config");
    const storeDir = path.join(pendingCleanRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    writeProfile(configDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      provisionCommands: ["npm ci"],
    });
    writeStoreProfile(storeDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      sdks: { node: "22" },
    });

    const failMergeCommitRunGit = function(args, opts) {
      if (args.includes("commit") && args.includes("-m") && args.includes("sync merge vm")) {
        return Promise.resolve({ code: 1, stdout: "", stderr: "simulated commit failure" });
      }
      return runGit(args, opts);
    };

    const blocked = await cs.syncTick({
      runGit: failMergeCommitRunGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("pending-clean: failed merge commit blocks", blocked.blocked === true);
    let pendingState = await cs.repoState(runGit, configDir);
    ok("pending-clean: merge is pending without conflicts", pendingState.mergeInProgress && !pendingState.conflict);

    const recovered = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("pending-clean: next tick auto-commits clean pending merge", recovered.ok && recovered.merged);
    pendingState = await cs.repoState(runGit, configDir);
    ok("pending-clean: merge state cleared", !pendingState.mergeInProgress && !pendingState.conflict);
    const hostWeb = readProfile(configDir, "web");
    ok("pending-clean: host has both changes", hostWeb && hostWeb.sdks && hostWeb.sdks.node === "22" && hostWeb.provisionCommands && hostWeb.provisionCommands[0] === "npm ci");
    const vmRef = await runGit(["rev-parse", "vm"], { cwd: configDir });
    const mainRef = await runGit(["rev-parse", "main"], { cwd: configDir });
    ok("pending-clean: vm ref advanced after recovery", vmRef.stdout.trim() === mainRef.stdout.trim());
  } finally { fs.rmSync(pendingCleanRoot, { recursive: true, force: true }); }

  // ── Merge blocked when invalid host file would be overwritten (Fix 4) ──────
  // D6 step 2: when an invalid host file is left uncommitted and the merge
  // touches it, git refuses. The tick must return {blocked:true, blockedReason}.
  const mergeBlockedRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-mblk-"));
  try {
    const configDir = mk(mergeBlockedRoot, "config");
    const storeDir = path.join(mergeBlockedRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // VM: edit web (add an SDK).
    writeStoreProfile(storeDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      sdks: { node: "22" },
    });

    // Host: write an INVALID file to projects/web.json (it will fail validation
    // and be left uncommitted by step 2). When the merge tries to touch it, git
    // refuses with "would be overwritten".
    fs.writeFileSync(path.join(configDir, "projects", "web.json"),
      '{"name":"web","repos":"NOT-AN-ARRAY"}', "utf8");

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("merge-blocked: result.blocked is true", result.blocked === true);
    ok("merge-blocked: blockedReason set", result.blockedReason && result.blockedReason.length > 0);
  } finally { fs.rmSync(mergeBlockedRoot, { recursive: true, force: true }); }

  // ── ensureRepo: ancestor repo detection (Fix 5) ──────────────────────────
  // When configDir is nested under an unrelated git repo, ensureRepo must NOT
  // adopt the parent repo. It should init its own.
  const ancestorRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-ancestor-"));
  try {
    // Create a parent git repo.
    const parentDir = mk(ancestorRoot, "parent");
    await runGit(["init"], { cwd: parentDir });
    fs.writeFileSync(path.join(parentDir, "readme.txt"), "parent repo");
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: parentDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "init parent"], { cwd: parentDir });

    // Create config dir INSIDE the parent repo.
    const configDir = mk(parentDir, "nested", "config");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });

    const r = await cs.ensureRepo(runGit, configDir);
    ok("ancestor: ensureRepo initializes its own repo (not parent's)", r.repo && r.initialized);

    // Verify the new repo's toplevel is configDir, not parentDir.
    const tl = await runGit(["rev-parse", "--show-toplevel"], { cwd: configDir });
    ok("ancestor: toplevel is configDir", path.resolve(tl.stdout.trim()) === path.resolve(configDir));
  } finally { fs.rmSync(ancestorRoot, { recursive: true, force: true }); }

  // ── VM deletion after conflict resolution (Fix 3) ─────────────────────────
  // After resolving a conflict, a VM-side deletion must propagate through the
  // temp-index vm commit correctly (git update-index --force-remove works where
  // git rm --cached -r would refuse due to staged-content safety checks).
  const vmDelAfterConflictRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-vmdelcr-"));
  try {
    const configDir = mk(vmDelAfterConflictRoot, "config");
    const storeDir = path.join(vmDelAfterConflictRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    writeProfile(configDir, "api", { name: "api", repos: [{ url: "https://h/a.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Create a conflict on web.
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/host-v.git" }] });
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/vm-v.git" }] });

    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Resolve the conflict.
    await runGit(["checkout", "--ours", "--", "projects/web.json"], { cwd: configDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: configDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "resolve"], { cwd: configDir });

    // Update store to match resolved state.
    writeStoreProfile(storeDir, "web", readProfile(configDir, "web"));

    // A normal tick to sync up.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Now delete api from the store (VM-side deletion after a conflict resolution).
    fs.unlinkSync(path.join(storeDir, "api.json"));

    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("vm-del-post-conflict: tick ok", result.ok);
    ok("vm-del-post-conflict: merged", result.merged);
    ok("vm-del-post-conflict: api deleted from host", readProfile(configDir, "api") === null);
    ok("vm-del-post-conflict: web still on host", readProfile(configDir, "web") !== null);
  } finally { fs.rmSync(vmDelAfterConflictRoot, { recursive: true, force: true }); }

  // ── buildReadStoreScript / buildWriteStoreScript integration with bash ─────
  const bashRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-bash-"));
  try {
    const storeDir = mk(bashRoot, "store");

    // Write some profiles.
    fs.writeFileSync(path.join(storeDir, "web.json"), '{"name":"web","repos":[]}', "utf8");
    fs.writeFileSync(path.join(storeDir, "api.json"), '{"name":"api","repos":[{"url":"u"}]}', "utf8");

    // Read.
    const readScript = cs.buildReadStoreScript(storeDir);
    const readOut = runBash(readScript);
    const parsed = cs.parseReadStore(readOut);
    ok("bash-read: parses two profiles", parsed && parsed.entries.length === 2);
    ok("bash-read: storeAbsent is false for existing dir", parsed && parsed.storeAbsent === false);
    ok("bash-read: content round-trips", parsed && parsed.entries.some((p) => {
      try { return JSON.parse(p.content).name === "web"; } catch (_) { return false; }
    }));

    // Write (guarded, expect current content).
    const webContent = '{"name":"web","repos":[]}';
    const newContent = '{"name":"web","repos":[],"sdks":{"node":"22"}}';
    const ops = [
      { name: "web", action: "write", content: newContent, expect: webContent },
      { name: "new-profile", action: "write", content: '{"name":"new-profile"}', expect: null },
    ];
    const writeScript = cs.buildWriteStoreScript(ops, storeDir);
    const writeOut = runBash(writeScript);
    const writeResult = cs.parseWriteResult(writeOut);
    ok("bash-write: both ops done", writeResult && writeResult.done.length === 2);
    // Verify file contents.
    const webAfter = fs.readFileSync(path.join(storeDir, "web.json"), "utf8");
    ok("bash-write: web updated", webAfter === newContent);
    const newAfter = fs.readFileSync(path.join(storeDir, "new-profile.json"), "utf8");
    ok("bash-write: new profile created", newAfter === '{"name":"new-profile"}');

    // Guard failure: try to write with wrong expect.
    const ops2 = [{ name: "web", action: "write", content: "other", expect: "wrong-content" }];
    const writeScript2 = cs.buildWriteStoreScript(ops2, storeDir);
    const writeOut2 = runBash(writeScript2);
    const writeResult2 = cs.parseWriteResult(writeOut2);
    ok("bash-write: guard fails on wrong expect", writeResult2 && writeResult2.skipped.includes("web"));

    // Guard for absent file (expect null but file exists).
    const ops3 = [{ name: "web", action: "write", content: "x", expect: null }];
    const writeScript3 = cs.buildWriteStoreScript(ops3, storeDir);
    const writeOut3 = runBash(writeScript3);
    const writeResult3 = cs.parseWriteResult(writeOut3);
    ok("bash-write: absent guard fails when file exists", writeResult3 && writeResult3.skipped.includes("web"));

    // Delete (guarded).
    const currentWeb = fs.readFileSync(path.join(storeDir, "web.json"), "utf8");
    const ops4 = [{ name: "web", action: "delete", expect: currentWeb }];
    const writeScript4 = cs.buildWriteStoreScript(ops4, storeDir);
    runBash(writeScript4);
    ok("bash-delete: file removed", !fs.existsSync(path.join(storeDir, "web.json")));

    // STORE_ABSENT marker: read from a non-existent directory.
    const absentDir = path.join(bashRoot, "nonexistent-store");
    const absentScript = cs.buildReadStoreScript(absentDir);
    const absentOut = runBash(absentScript);
    const absentParsed = cs.parseReadStore(absentOut);
    ok("bash-read: absent dir emits STORE_ABSENT", absentParsed && absentParsed.storeAbsent === true);
    ok("bash-read: absent dir has no entries", absentParsed && absentParsed.entries.length === 0);
  } finally { fs.rmSync(bashRoot, { recursive: true, force: true }); }

  // ── listImportCandidates ───────────────────────────────────────────────────
  const candRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-cand-"));
  try {
    // With projects/ subdir.
    const projDir = mk(candRoot, "withproj", "projects");
    fs.writeFileSync(path.join(projDir, "web.json"), "{}");
    fs.writeFileSync(path.join(projDir, "api.json"), "{}");
    fs.writeFileSync(path.join(projDir, "default.json"), "{}");
    fs.writeFileSync(path.join(projDir, "example.json.sample"), "{}");
    fs.writeFileSync(path.join(projDir, "project.schema.json"), "{}");
    const cands = cs.listImportCandidates(path.join(candRoot, "withproj"));
    ok("candidates: finds non-reserved files in projects/", cands.length === 2);
    ok("candidates: excludes reserved names", !cands.some((c) => c.name === "default"));
    ok("candidates: excludes sample", !cands.some((c) => c.name === "example.json"));
    ok("candidates: relPath includes projects/", cands.every((c) => c.relPath.startsWith("projects/")));

    // Without projects/ subdir (top-level).
    const topDir = mk(candRoot, "toponly");
    fs.writeFileSync(path.join(topDir, "billing.json"), "{}");
    fs.writeFileSync(path.join(topDir, "default.json"), "{}");
    const topCands = cs.listImportCandidates(topDir);
    ok("candidates: top-level fallback", topCands.length === 1 && topCands[0].name === "billing");
    ok("candidates: top-level relPath has no prefix", topCands[0].relPath === "billing.json");
  } finally { fs.rmSync(candRoot, { recursive: true, force: true }); }

  // ── planUpstreamImport ─────────────────────────────────────────────────────
  (function () {
    const manifest = {
      web: { remoteUrl: "https://git.co/cfg.git", pathInRemote: "projects/web.json" },
    };
    const selected = [
      { remoteUrl: "https://git.co/cfg.git", ref: "main", relPath: "projects/web.json", name: "web", content: "{}" },
      { remoteUrl: "https://git.co/cfg.git", ref: "main", relPath: "projects/new.json", name: "new", content: "{}" },
      { remoteUrl: "https://other.co/x.git", ref: "main", relPath: "projects/existing.json", name: "existing", content: "{}" },
    ];
    const result = cs.planUpstreamImport({
      selected,
      manifest,
      existingNames: ["web", "existing"],
    });
    ok("planImport: same provenance => update", result.updates.length === 1 && result.updates[0].name === "web");
    ok("planImport: new name => create", result.creates.length === 1 && result.creates[0].name === "new");
    ok("planImport: name collision different provenance => collision",
      result.collisions.length === 1 && result.collisions[0].name === "existing");
    ok("planImport: collision suggests -2 suffix", result.collisions[0].suggested === "existing-2");
  })();
  (function () {
    // Collision suffix increment.
    const result = cs.planUpstreamImport({
      selected: [{ remoteUrl: "r", ref: "m", relPath: "p", name: "x", content: "{}" }],
      manifest: {},
      existingNames: ["x", "x-2"],
    });
    ok("planImport: collision suggests -3 when -2 exists", result.collisions[0].suggested === "x-3");
  })();

  // ── mergeFile ──────────────────────────────────────────────────────────────
  const mergeClean = await cs.mergeFile(runGit, {
    ours: "line1\nline2\nline3\n",
    base: "line1\nline2\nline3\n",
    theirs: "line1\nline2-changed\nline3\n",
  });
  ok("mergeFile: clean merge", mergeClean.ok && !mergeClean.conflict);
  ok("mergeFile: clean merge content", mergeClean.content === "line1\nline2-changed\nline3\n");

  const mergeConflict = await cs.mergeFile(runGit, {
    ours: "line1\nours\nline3\n",
    base: "line1\nbase\nline3\n",
    theirs: "line1\ntheirs\nline3\n",
  });
  ok("mergeFile: conflict detected", !mergeConflict.ok && mergeConflict.conflict);

  // ── ensureStagingClone (with file:// URL to a local bare repo) ─────────────
  const cloneRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-clone-"));
  try {
    // Create a bare repo with a file in it.
    const bareDir = mk(cloneRoot, "bare.git");
    await runGit(["init", "--bare"], { cwd: bareDir });
    // Set the bare repo's default branch to main (git init --bare defaults to
    // 'master' on many systems; we need 'main' so clone checkout succeeds).
    await runGit(["symbolic-ref", "HEAD", "refs/heads/main"], { cwd: bareDir });
    // Create a temp worktree to make a commit.
    const tmpWork = mk(cloneRoot, "tmpwork");
    await runGit(["clone", bareDir, "work"], { cwd: cloneRoot });
    const workDir = path.join(cloneRoot, "work");
    mk(workDir, "projects");
    fs.writeFileSync(path.join(workDir, "projects", "sample.json"), '{"name":"sample"}');
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: workDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "init"], { cwd: workDir });
    // Rename the local branch to main (git may default to 'master').
    await runGit(["branch", "-M", "main"], { cwd: workDir });
    await runGit(["push", "origin", "main"], { cwd: workDir });

    // Clone via ensureStagingClone.
    const staging = mk(cloneRoot, "staging");
    const r1 = await cs.ensureStagingClone(runGit, staging, "file://" + bareDir);
    ok("staging: initial clone ok", r1.ok);
    ok("staging: clone has the file", fs.existsSync(path.join(r1.dir, "projects", "sample.json")));

    // Second call should fetch+reset (idempotent).
    const r2 = await cs.ensureStagingClone(runGit, staging, "file://" + bareDir);
    ok("staging: re-fetch ok", r2.ok);

    // Add another commit to bare and re-fetch.
    fs.writeFileSync(path.join(workDir, "projects", "extra.json"), '{"name":"extra"}');
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: workDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "add extra"], { cwd: workDir });
    await runGit(["push", "origin", "main"], { cwd: workDir });
    const r3 = await cs.ensureStagingClone(runGit, staging, "file://" + bareDir);
    ok("staging: picks up new upstream commit", r3.ok && fs.existsSync(path.join(r3.dir, "projects", "extra.json")));
  } finally { fs.rmSync(cloneRoot, { recursive: true, force: true }); }

  // ── pushUpstream ───────────────────────────────────────────────────────────
  const pushRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-push-"));
  try {
    // Create a bare remote.
    const bareDir = mk(pushRoot, "remote.git");
    await runGit(["init", "--bare"], { cwd: bareDir });
    await runGit(["symbolic-ref", "HEAD", "refs/heads/main"], { cwd: bareDir });
    // Create a staging clone.
    await runGit(["clone", bareDir, "staging"], { cwd: pushRoot });
    const stagingDir = path.join(pushRoot, "staging");
    // Initial commit.
    fs.writeFileSync(path.join(stagingDir, "README.md"), "init");
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: stagingDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "init"], { cwd: stagingDir });
    await runGit(["branch", "-M", "main"], { cwd: stagingDir });
    await runGit(["push", "origin", "main"], { cwd: stagingDir });

    // Create a source file to push.
    const srcDir = mk(pushRoot, "src");
    fs.writeFileSync(path.join(srcDir, "web.json"), '{"name":"web"}');

    const result = await cs.pushUpstream(runGit, {
      stagingDir,
      files: [{ absSource: path.join(srcDir, "web.json"), pathInRemote: "projects/web.json" }],
      branch: "construct-config-update-test",
      message: "config update",
    });
    ok("push: succeeded", result.ok);
    ok("push: branch name returned", result.branch === "construct-config-update-test");

    // Verify the branch exists on the bare remote.
    const branchList = await runGit(["branch", "--list"], { cwd: bareDir });
    ok("push: branch exists on remote", branchList.stdout.includes("construct-config-update-test"));
  } finally { fs.rmSync(pushRoot, { recursive: true, force: true }); }

  // ── Finding 2: failed write-back does NOT advance vm ref ───────────────────
  // When both host and VM edit (producing a merged result that differs from the
  // VM's version), the write-back must deliver the merged content. If the
  // write-back fails (SSH timeout), the vm ref must NOT advance, so the next
  // tick retries the write-back instead of silently losing the host's edit.
  const wb2Root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-wb2-"));
  try {
    const configDir = mk(wb2Root, "config");
    const storeDir = path.join(wb2Root, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // Seed.
    await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });

    // Host adds an SDK (different from what the VM will add).
    writeProfile(configDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" },
    });
    // VM adds a DIFFERENT field so both sides diverge non-conflictingly.
    writeStoreProfile(storeDir, "web", {
      name: "web", repos: [{ url: "https://h/w.git" }],
      hostPackages: ["jq"],
    });

    // Tick with a failing writeStore (returns null = SSH timeout).
    const result = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: async () => null,   // simulate SSH failure on write-back
      storeRoot: storeDir,
    });
    // vm must NOT have been fast-forwarded to main (step 8 skipped because
    // write-back failed). Step 5 (commitVmBranch) legitimately advances vm to
    // record the VM's current state, but step 8 must NOT then snap vm to main.
    const vmRefAfter = (await runGit(["rev-parse", "vm"], { cwd: configDir })).stdout.trim();
    const mainRefAfter = (await runGit(["rev-parse", "main"], { cwd: configDir })).stdout.trim();
    ok("wb-fail: vm NOT fast-forwarded to main on write-back failure",
      vmRefAfter !== mainRefAfter,
      "vm=" + vmRefAfter + " main=" + mainRefAfter);
    ok("wb-fail: warning issued about write-back failure",
      result.warnings.some((w) => w.includes("write-back") && w.includes("failed")));
    ok("wb-fail: merged on host", result.merged);

    // Next tick with healthy writeStore should succeed and deliver the merge.
    const result2 = await cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeDir),
      writeStore: makeWriteStore(),
      storeRoot: storeDir,
    });
    ok("wb-fail: next healthy tick ok", result2.ok);
    // The host profile must have BOTH edits (host's node SDK + VM's jq package).
    const hostProfile = readProfile(configDir, "web");
    ok("wb-fail: host edit preserved after retry",
      hostProfile && hostProfile.sdks && hostProfile.sdks.node === "22");
    ok("wb-fail: vm edit delivered after retry",
      hostProfile && hostProfile.hostPackages && hostProfile.hostPackages.includes("jq"));
  } finally { fs.rmSync(wb2Root, { recursive: true, force: true }); }

  // ── Finding 3: ensureRepo excludes reserved names from initial commit ──────
  const reservedInitRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-resinit-"));
  try {
    const configDir = mk(reservedInitRoot, "config");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    // Place reserved files in the config dir.
    fs.writeFileSync(path.join(configDir, "projects", "default.json"), '{"name":"default"}', "utf8");
    fs.writeFileSync(path.join(configDir, "projects", "project.schema.json"), '{}', "utf8");

    await cs.ensureRepo(runGit, configDir);

    // Check that reserved files are NOT tracked in the initial commit.
    const lsTree = await runGit(["ls-tree", "-r", "--name-only", "main"], { cwd: configDir });
    const tracked = lsTree.stdout.trim().split("\n");
    ok("reserved-init: default.json not tracked", !tracked.includes("projects/default.json"));
    ok("reserved-init: project.schema.json not tracked", !tracked.includes("projects/project.schema.json"));
    ok("reserved-init: web.json IS tracked", tracked.includes("projects/web.json"));
  } finally { fs.rmSync(reservedInitRoot, { recursive: true, force: true }); }

  // ── Finding 10: pushUpstream rejects pathInRemote with traversal ───────────
  const pushTraversalRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-pushtrav-"));
  try {
    const stagingDir = mk(pushTraversalRoot, "staging");
    await runGit(["init"], { cwd: stagingDir });
    fs.writeFileSync(path.join(stagingDir, "README.md"), "init");
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: stagingDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "init"], { cwd: stagingDir });

    const srcDir = mk(pushTraversalRoot, "src");
    fs.writeFileSync(path.join(srcDir, "web.json"), '{"name":"web"}');

    // A traversal pathInRemote should throw.
    let threw = false;
    try {
      await cs.pushUpstream(runGit, {
        stagingDir,
        files: [{ absSource: path.join(srcDir, "web.json"), pathInRemote: "projects/../../../escaped.txt" }],
        branch: "test-branch",
        message: "should not succeed",
      });
    } catch (e) {
      threw = true;
      ok("push-traversal: error mentions escapes staging", e.message.includes("escapes staging"));
    }
    ok("push-traversal: throws on traversal pathInRemote", threw);

    // A normal pathInRemote should still work.
    let normalOk = true;
    try {
      await cs.pushUpstream(runGit, {
        stagingDir,
        files: [{ absSource: path.join(srcDir, "web.json"), pathInRemote: "projects/web.json" }],
        branch: "test-branch-ok",
        message: "normal push",
      });
    } catch (_) { normalOk = false; }
    ok("push-traversal: normal pathInRemote succeeds", normalOk);
  } finally { fs.rmSync(pushTraversalRoot, { recursive: true, force: true }); }

  // ── readRemotes / writeRemotes ─────────────────────────────────────────────
  const remotesRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-remotes-"));
  try {
    const configDir = mk(remotesRoot, "config");
    cs.ensureConfigTree(configDir);
    ok("remotes: empty when absent", eq(cs.readRemotes(configDir), []));
    cs.writeRemotes(configDir, [{ url: "https://git.co/a.git" }, { url: "https://git.co/b.git" }]);
    const remotes = cs.readRemotes(configDir);
    ok("remotes: round-trip", remotes.length === 2 && remotes[0].url === "https://git.co/a.git");
  } finally { fs.rmSync(remotesRoot, { recursive: true, force: true }); }

  // ── commitAll ──────────────────────────────────────────────────────────────
  const commitRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-commit-"));
  try {
    const configDir = mk(commitRoot, "config");
    cs.ensureConfigTree(configDir);
    await runGit(["init"], { cwd: configDir });
    fs.writeFileSync(path.join(configDir, "test.txt"), "hello");
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "add", "-A"], { cwd: configDir });
    await runGit(["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "init"], { cwd: configDir });
    // Add a new file.
    fs.writeFileSync(path.join(configDir, "new.txt"), "world");
    const r = await cs.commitAll(runGit, configDir, "test commit");
    ok("commitAll: committed", r.ok && r.committed);
    // No changes -> no commit.
    const r2 = await cs.commitAll(runGit, configDir, "no-op");
    ok("commitAll: no-op when clean", r2.ok && !r2.committed);
  } finally { fs.rmSync(commitRoot, { recursive: true, force: true }); }

  // ── planWriteBack ──────────────────────────────────────────────────────────
  (function () {
    const ops = cs.planWriteBack({
      mainFiles: { web: "a", api: "b", both: "main" },
      vmFiles: { web: "a", both: "vm", old: "c" },
    });
    // web: same on both -> no op.
    ok("planWriteBack: same content -> no op", !ops.some((o) => o.name === "web"));
    // api: on main only -> write with expect null.
    const apiOp = ops.find((o) => o.name === "api");
    ok("planWriteBack: main-only -> write expect null", apiOp && apiOp.action === "write" && apiOp.expect === null);
    // both: different content -> write with expect = vm content.
    const bothOp = ops.find((o) => o.name === "both");
    ok("planWriteBack: different -> write with vm expect", bothOp && bothOp.action === "write" && bothOp.expect === "vm");
    // old: vm only -> delete with expect.
    const oldOp = ops.find((o) => o.name === "old");
    ok("planWriteBack: vm-only -> delete", oldOp && oldOp.action === "delete" && oldOp.expect === "c");
  })();

  // ── readImportManifest ─────────────────────────────────────────────────────
  const manRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-man-"));
  try {
    const configDir = mk(manRoot, "config");
    cs.ensureConfigTree(configDir);
    fs.writeFileSync(path.join(configDir, "manifest", "web.json"),
      JSON.stringify({ remoteUrl: "https://git.co/cfg.git", pathInRemote: "projects/web.json" }));
    fs.writeFileSync(path.join(configDir, "manifest", "remotes.json"),
      JSON.stringify([{ url: "https://git.co/cfg.git" }]));
    const man = cs.readImportManifest(configDir);
    ok("manifest: reads non-remotes entries", man.web && man.web.remoteUrl === "https://git.co/cfg.git");
    ok("manifest: excludes remotes.json", !("remotes" in man));
  } finally { fs.rmSync(manRoot, { recursive: true, force: true }); }

  // ── Commit hardening: a clean merge auto-commits even under enforced signing ─
  // Regression for the "always shows merge conflicts" bug: the config repo
  // inherits the user's global git config, and commit.gpgsign=true with no
  // reachable key makes every headless `git commit` fail — a cleanly auto-merged
  // `merge --no-commit` is then left uncommitted (MERGE_HEAD present) and the
  // panel reports a phantom unresolved merge. The engine must commit hermetically.
  const signRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-sign-"));
  try {
    const configDir = mk(signRoot, "config");
    const storeDir = path.join(signRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);

    // ensureRepo hardens the repo: signing off locally + a .gitattributes.
    const gpgLocal = await runGit(["config", "--local", "commit.gpgsign"], { cwd: configDir });
    ok("harden: ensureRepo set commit.gpgsign=false locally", gpgLocal.stdout.trim() === "false");
    ok("harden: ensureRepo set core.autocrlf=false locally",
      (await runGit(["config", "--local", "core.autocrlf"], { cwd: configDir })).stdout.trim() === "false");
    const hooksLocal = await runGit(["config", "--local", "core.hooksPath"], { cwd: configDir });
    ok("harden: ensureRepo emptied core.hooksPath locally", hooksLocal.code === 0 && hooksLocal.stdout.trim() === "");
    ok("harden: .gitattributes pins LF", fs.readFileSync(path.join(configDir, ".gitattributes"), "utf8").includes("eol=lf"));
    // A manual commit (no engine -c flags) must survive an inherited failing hook,
    // because the empty hooksPath is now persisted repo-locally.
    const gh = mk(signRoot, "globalhooks");
    fs.writeFileSync(path.join(gh, "pre-commit"), "#!/bin/sh\nexit 1\n", { mode: 0o755 });
    await runGit(["config", "--local", "core.hooksPath", gh], { cwd: configDir }); // simulate the inherited hook
    await runGit(["config", "--local", "core.hooksPath"], { cwd: configDir });
    await cs.ensureRepo(runGit, configDir); // re-harden should re-empty it
    fs.writeFileSync(path.join(configDir, "projects", "hooktest.json"),
      projects.canonicalProfileJson("hooktest", { name: "hooktest", repos: [] }), "utf8");
    const manualAdd = await runGit(["-c", "user.name=u", "-c", "user.email=u@u", "add", "-A"], { cwd: configDir });
    const manualCommit = await runGit(["-c", "user.name=u", "-c", "user.email=u@u", "commit", "-m", "manual"], { cwd: configDir });
    ok("harden: manual commit survives an inherited failing hook", manualAdd.code === 0 && manualCommit.code === 0);

    await cs.syncTick({ runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir });

    // Now simulate a host whose git ENFORCES signing with no working key: a
    // signing attempt must fail (gpg.program that always errors). Without the
    // GIT_IDENTITY `-c commit.gpgsign=false` override the merge commit would fail.
    await runGit(["config", "commit.gpgsign", "true"], { cwd: configDir });
    await runGit(["config", "gpg.program", "/bin/false"], { cwd: configDir });

    // A non-conflicting VM-side edit forces a real merge on the next tick.
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } });
    const result = await cs.syncTick({ runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir });

    ok("sign: merge completed under enforced signing", result.merged === true);
    ok("sign: tick ok, not blocked/conflict", result.ok === true && !result.blocked && !result.conflict);
    const mh = await runGit(["rev-parse", "--verify", "MERGE_HEAD"], { cwd: configDir });
    ok("sign: no MERGE_HEAD left behind (merge auto-committed)", mh.code !== 0);
    const hostProfile = readProfile(configDir, "web");
    ok("sign: host received the VM edit", hostProfile && hostProfile.sdks && hostProfile.sdks.node === "22");

    // Recovery path (completePendingMerge, the fn band-aided in 6a28c18): a repo
    // ALREADY stuck mid-clean-merge (the exact state the old bug produced) must be
    // finished by the next tick, under enforced signing. Craft that state by hand.
    const SETUP = ["-c", "user.name=x", "-c", "user.email=x@y", "-c", "commit.gpgsign=false"];
    await runGit([...SETUP, "checkout", "vm"], { cwd: configDir });
    writeProfile(configDir, "fromvm", { name: "fromvm", repos: [{ url: "https://h/v.git" }] });
    await runGit([...SETUP, "add", "-A"], { cwd: configDir });
    await runGit([...SETUP, "commit", "-m", "vm side"], { cwd: configDir });
    await runGit([...SETUP, "checkout", "main"], { cwd: configDir });
    writeProfile(configDir, "frommain", { name: "frommain", repos: [{ url: "https://h/m.git" }] });
    await runGit([...SETUP, "add", "-A"], { cwd: configDir });
    await runGit([...SETUP, "commit", "-m", "main side"], { cwd: configDir });
    // Leave a CLEAN pending merge (no --commit): staged, no conflict, MERGE_HEAD set.
    await runGit([...SETUP, "merge", "--no-ff", "--no-commit", "vm"], { cwd: configDir });
    ok("sign: setup left a clean pending merge",
      (await runGit(["rev-parse", "--verify", "MERGE_HEAD"], { cwd: configDir })).code === 0 &&
      (await runGit(["ls-files", "-u"], { cwd: configDir })).stdout.trim() === "");
    // Re-enforce signing (checkout/merge above ran with it off for setup only).
    await runGit(["config", "commit.gpgsign", "true"], { cwd: configDir });
    await runGit(["config", "gpg.program", "/bin/false"], { cwd: configDir });
    const recov = await cs.syncTick({ runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir });
    const mh2 = await runGit(["rev-parse", "--verify", "MERGE_HEAD"], { cwd: configDir });
    ok("sign: recovery tick completed the pending merge", recov.merged === true && mh2.code !== 0 && !recov.conflict);
  } finally { fs.rmSync(signRoot, { recursive: true, force: true }); }

  // ── Bookkeeping files are ignored (no status clutter, no merge block) ────────
  // .gitattributes (hardening) + .migrated (PS migration sentinel) live at the
  // config-repo root. Left un-ignored they clutter `git status` AND can trip git's
  // "untracked working tree files would be overwritten by merge" guard, which the
  // tick reports as a phantom conflict. ensureRepo must add them to .git/info/exclude.
  const excRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-exclude-"));
  try {
    const configDir = mk(excRoot, "config");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [] });
    await cs.ensureRepo(runGit, configDir);
    fs.writeFileSync(path.join(configDir, ".migrated"), "1"); // simulate the PS sentinel
    const excPath = path.join(configDir, ".git", "info", "exclude");
    const exc = fs.readFileSync(excPath, "utf8");
    ok("exclude: lists config-sync bookkeeping files",
      exc.includes(".gitattributes") && exc.includes(".migrated") &&
      exc.includes(".sync.lock") && exc.includes(".sync.provisioning"));
    const st = await runGit(["status", "--porcelain"], { cwd: configDir });
    ok("exclude: git status hides the bookkeeping files",
      !st.stdout.includes(".gitattributes") && !st.stdout.includes(".migrated"));
    // Idempotent: re-running ensureRepo doesn't append duplicate entries.
    await cs.ensureRepo(runGit, configDir);
    const exc2 = fs.readFileSync(excPath, "utf8");
    ok("exclude: idempotent (single .migrated entry)", (exc2.match(/^\.migrated$/gm) || []).length === 1);
    // A merge that would overwrite an ignored bookkeeping file is NOT blocked.
    await runGit(["branch", "collide"], { cwd: configDir });
    await runGit([...["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"], "stash", "--include-untracked"], { cwd: configDir }).catch(() => {});
    await runGit(["checkout", "collide"], { cwd: configDir });
    fs.writeFileSync(path.join(configDir, ".migrated"), "from-branch");
    await runGit(["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "add", "-f", ".migrated"], { cwd: configDir });
    await runGit(["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-m", "track migrated on branch"], { cwd: configDir });
    await runGit(["checkout", "main"], { cwd: configDir });
    fs.writeFileSync(path.join(configDir, ".migrated"), "untracked-local"); // untracked + ignored, collides
    const mg = await runGit(["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "merge", "--no-ff", "--no-commit", "collide"], { cwd: configDir });
    ok("exclude: an ignored bookkeeping file never blocks a merge", mg.code === 0);
  } finally { fs.rmSync(excRoot, { recursive: true, force: true }); }

  // ── vmBranch: instance-keyed VM branch (B5) ────────────────────────────────
  // One host config repo, one VM-side branch per instance. `main` stays the host
  // truth; the default instance keeps the historical `vm` ref.

  /** All local branch refs in the repo, sorted. */
  async function heads(configDir) {
    const r = await runGit(["for-each-ref", "--format=%(refname)", "refs/heads"], { cwd: configDir });
    return r.stdout.trim().split("\n").filter(Boolean).sort();
  }
  const revOf = async (configDir, ref) =>
    (await runGit(["rev-parse", ref], { cwd: configDir })).stdout.trim();

  ok("vmBranch: valid names accepted", cs.isValidVmBranch("vm") && cs.isValidVmBranch("vm-work") &&
    cs.isValidVmBranch("vm-work_2.1"));
  ok("vmBranch: rejects empty / non-string", !cs.isValidVmBranch("") && !cs.isValidVmBranch(null) &&
    !cs.isValidVmBranch(undefined) && !cs.isValidVmBranch(7));
  ok("vmBranch: rejects traversal, bad leading char, spaces, slashes",
    !cs.isValidVmBranch("vm..x") && !cs.isValidVmBranch("-vm") && !cs.isValidVmBranch(".vm") &&
    !cs.isValidVmBranch("vm work") && !cs.isValidVmBranch("vm/work") && !cs.isValidVmBranch("vm~1") &&
    !cs.isValidVmBranch("vm.lock"));
  ok("vmBranch: rejects the trunk names and git pseudo-refs",
    !cs.isValidVmBranch("main") && !cs.isValidVmBranch("MAIN") && !cs.isValidVmBranch("Main") &&
    !cs.isValidVmBranch("master") && !cs.isValidVmBranch("HEAD") && !cs.isValidVmBranch("head") &&
    !cs.isValidVmBranch("FETCH_HEAD") && !cs.isValidVmBranch("ORIG_HEAD") && !cs.isValidVmBranch("stash"));
  ok("vmBranch: rejects other spellings of the default branch (Windows ref collision)",
    !cs.isValidVmBranch("VM") && !cs.isValidVmBranch("Vm"));
  ok("vmBranch: an ordinary uppercase instance branch is NOT treated as a pseudo-ref",
    cs.isValidVmBranch("WORK") && cs.isValidVmBranch("Work") && cs.isValidVmBranch("VM_WORK"));
  ok("vmBranch: rejects a trailing dot (git refuses `foo.` as a branch)",
    !cs.isValidVmBranch("foo.") && !cs.isValidVmBranch("vm-work.") && !cs.isValidVmBranch("vm."));

  // WINDOWS' RULE, NOT GIT'S. The host config repo is a loose-ref repo on Windows, where
  // refs/heads/CON — and the refs/heads/CON.lock git writes beside it — cannot be created,
  // with or without an extension. Linux git accepts every one of these, so the fixture
  // list below cannot catch them: only this rule keeps a hand-authored `configBranch`
  // from passing every registry check and failing at the first sync tick.
  ok("vmBranch: rejects Windows device stems (a loose ref is a file)",
    !cs.isValidVmBranch("CON") && !cs.isValidVmBranch("con") && !cs.isValidVmBranch("NUL") &&
    !cs.isValidVmBranch("PRN") && !cs.isValidVmBranch("aux") &&
    !cs.isValidVmBranch("COM1") && !cs.isValidVmBranch("com9") &&
    !cs.isValidVmBranch("LPT1") && !cs.isValidVmBranch("lpt9"));
  ok("vmBranch: ...including the extension forms Win32 resolves to the same device",
    !cs.isValidVmBranch("CON.txt") && !cs.isValidVmBranch("con.work") &&
    !cs.isValidVmBranch("nul.branch.1"));
  ok("vmBranch: ...but the rule is the STEM, not a substring",
    cs.isValidVmBranch("console") && cs.isValidVmBranch("con-work") &&
    cs.isValidVmBranch("vm-con") && cs.isValidVmBranch("com10") && cs.isValidVmBranch("lpt0"));

  // THE PROPERTY, against REAL GIT. This validator is deliberately stricter than
  // check-ref-format (it also refuses `main`, `HEAD`, `VM`, …) — but it must never be
  // LOOSER, or a hand-authored `configBranch` passes validation here and then fails at
  // `git branch`, leaving the instance syncing on the fallback branch instead of the one
  // it was promised. The same fixture list is driven through the PowerShell twin
  // (Test-ConstructVmBranchName) in test/config-sync.test.ps1.
  const BRANCH_FIXTURES = [
    "vm", "vm-work", "vm-work_2.1", "WORK", "Work", "VM_WORK", "vm2", "a",
    "console", "con-work", "com10",
    "main", "master", "HEAD", "stash", "VM", "Vm",
    "CON", "con", "NUL", "PRN", "aux", "COM1", "lpt9", "CON.txt", "con.work",
    "foo.", "vm.", "vm-work.", "foo.bar.", "vm..x", "a..b", "-vm", ".vm",
    "vm work", "vm/work", "vm~1", "vm.lock", "x@{y", "vm^", "vm:1", "vm?", "vm*",
    "vm[1]", "vm\\x", "vm@{now}", "@",
  ];
  const gitAccepts = (name) => {
    try { execSync("git check-ref-format --branch " + JSON.stringify(name), { stdio: "ignore" }); return true; }
    catch (_) { return false; }
  };
  {
    const looser = BRANCH_FIXTURES.filter((n) => cs.isValidVmBranch(n) && !gitAccepts(n));
    ok("vmBranch: everything this validator accepts, real git accepts", looser.length === 0,
      "git refuses: " + JSON.stringify(looser));
    const gitBad = BRANCH_FIXTURES.filter((n) => !gitAccepts(n));
    const wrongly = gitBad.filter((n) => cs.isValidVmBranch(n));
    ok("vmBranch: ...and everything real git refuses is refused here too", wrongly.length === 0,
      "accepted anyway: " + JSON.stringify(wrongly));
    ok("vmBranch: the fixture list really does contain git-invalid names",
      gitBad.indexOf("foo.") >= 0 && gitBad.length >= 15);
    ok("vmBranch: ...and the two names the product depends on stay accepted",
      cs.isValidVmBranch("vm") && cs.isValidVmBranch("vm-work") &&
      gitAccepts("vm") && gitAccepts("vm-work"));
  }
  ok("vmBranch: resolve defaults to vm", cs.resolveVmBranch() === "vm" && cs.resolveVmBranch("") === "vm" &&
    cs.resolveVmBranch(null) === "vm" && cs.DEFAULT_VM_BRANCH === "vm");
  ok("vmBranch: resolve keeps a valid name", cs.resolveVmBranch("vm-work") === "vm-work");
  ok("vmBranch: resolve falls back and warns on a bad name", (() => {
    const warns = [];
    const r = cs.resolveVmBranch("vm/work", (m) => warns.push(m));
    return r === "vm" && warns.length === 1 && /invalid config-sync branch name/.test(warns[0]);
  })());

  // (a) Default (no vmBranch) creates exactly refs/heads/main + refs/heads/vm.
  const defBranchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-default-"));
  try {
    const configDir = mk(defBranchRoot, "config");
    const storeDir = path.join(defBranchRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);
    ok("branch-default: init creates only main + vm", eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm"]));
    await cs.syncTick({ runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir });
    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } });
    const r = await cs.syncTick({ runGit, configDir, readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir });
    ok("branch-default: merged", r.merged && r.ok);
    ok("branch-default: no extra refs after a full tick", eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm"]));
    ok("branch-default: vm fast-forwarded to main",
      (await revOf(configDir, "vm")) === (await revOf(configDir, "main")));
  } finally { fs.rmSync(defBranchRoot, { recursive: true, force: true }); }

  // (b) A non-default vmBranch uses its own ref and never touches refs/heads/vm.
  const altBranchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-alt-"));
  try {
    const configDir = mk(altBranchRoot, "config");
    const storeDir = path.join(altBranchRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir, "vm-work");
    ok("branch-alt: init creates main + vm-work only", eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm-work"]));

    const seed = await cs.syncTick({
      runGit, configDir, vmBranch: "vm-work",
      readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("branch-alt: seeds the fresh VM", seed.seeded && seed.ok);
    ok("branch-alt: profile written to the store", (readStoreProfile(storeDir, "web") || {}).name === "web");

    writeStoreProfile(storeDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }], sdks: { node: "22" } });
    const r = await cs.syncTick({
      runGit, configDir, vmBranch: "vm-work",
      readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
    });
    ok("branch-alt: VM edit merged into main", r.merged && r.ok);
    ok("branch-alt: host has the VM edit", ((readProfile(configDir, "web") || {}).sdks || {}).node === "22");
    ok("branch-alt: still no refs/heads/vm", eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm-work"]));
    ok("branch-alt: vm-work fast-forwarded to main",
      (await revOf(configDir, "vm-work")) === (await revOf(configDir, "main")));
    const log = await runGit(["log", "-1", "--format=%s", "main"], { cwd: configDir });
    ok("branch-alt: merge commit names the instance branch", log.stdout.trim() === "sync merge vm-work");
  } finally { fs.rmSync(altBranchRoot, { recursive: true, force: true }); }

  // An existing single-VM repo gains a second instance's branch on demand.
  const joinRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-join-"));
  try {
    const configDir = mk(joinRoot, "config");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);
    const before = await heads(configDir);
    await cs.ensureRepo(runGit, configDir, "vm-work");
    ok("branch-join: default repo keeps main + vm", eq(before, ["refs/heads/main", "refs/heads/vm"]));
    ok("branch-join: a second instance's branch is created at main",
      eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm", "refs/heads/vm-work"]) &&
      (await revOf(configDir, "vm-work")) === (await revOf(configDir, "main")));
    // Idempotent: an existing branch is never reset back to main.
    await runGit(["update-ref", "refs/heads/vm-work", "main^{commit}"], { cwd: configDir });
    const keep = await revOf(configDir, "vm-work");
    await cs.ensureRepo(runGit, configDir, "vm-work");
    ok("branch-join: an existing instance branch is left where it is",
      (await revOf(configDir, "vm-work")) === keep);
  } finally { fs.rmSync(joinRoot, { recursive: true, force: true }); }

  // (c) Two instances in ONE repo sync independently: each tick reads/writes its
  // own store and merges from ITS OWN branch's base. Cross-branch leakage would
  // make instance B's merge delete instance A's profile from main.
  const twoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-two-"));
  try {
    const configDir = mk(twoRoot, "config");
    const storeA = path.join(twoRoot, "store-a");
    const storeB = path.join(twoRoot, "store-b");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);
    const tickA = () => cs.syncTick({
      runGit, configDir,
      readStore: makeReadStore(storeA), writeStore: makeWriteStore(), storeRoot: storeA,
    });
    const tickB = () => cs.syncTick({
      runGit, configDir, vmBranch: "vm-work",
      readStore: makeReadStore(storeB), writeStore: makeWriteStore(), storeRoot: storeB,
    });

    ok("two-instances: A seeds its store", (await tickA()).seeded && !!readStoreProfile(storeA, "web"));
    ok("two-instances: B seeds its own store", (await tickB()).seeded && !!readStoreProfile(storeB, "web"));
    ok("two-instances: both branches exist alongside main",
      eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm", "refs/heads/vm-work"]));

    // A's VM creates a profile; B's VM creates a different one, each in its own store.
    writeStoreProfile(storeA, "a-only", { name: "a-only", repos: [{ url: "https://h/a.git" }] });
    writeStoreProfile(storeB, "b-only", { name: "b-only", repos: [{ url: "https://h/b.git" }] });

    const ra = await tickA();
    ok("two-instances: A's tick merges a-only into main", ra.merged && !!readProfile(configDir, "a-only"));
    ok("two-instances: A's tick leaves B's branch behind",
      (await revOf(configDir, "vm-work")) !== (await revOf(configDir, "main")));
    ok("two-instances: A's tick does not touch B's store", readStoreProfile(storeB, "a-only") === null);

    const rb = await tickB();
    ok("two-instances: B's tick merges b-only into main", rb.merged && !!readProfile(configDir, "b-only"));
    ok("two-instances: B's merge keeps A's profile on main (own base, not vm's)",
      !!readProfile(configDir, "a-only"));
    ok("two-instances: B's write-back delivers A's profile to B's store",
      (readStoreProfile(storeB, "a-only") || {}).name === "a-only");
    ok("two-instances: b-only is not written to A's store by B's tick",
      readStoreProfile(storeA, "b-only") === null);

    await tickA();
    ok("two-instances: A's next tick delivers b-only to A's store",
      (readStoreProfile(storeA, "b-only") || {}).name === "b-only");
    const mainRev = await revOf(configDir, "main");
    ok("two-instances: both branches end at main",
      (await revOf(configDir, "vm")) === mainRev && (await revOf(configDir, "vm-work")) === mainRev);
    ok("two-instances: no other refs were created",
      eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm", "refs/heads/vm-work"]));
  } finally { fs.rmSync(twoRoot, { recursive: true, force: true }); }

  // (c1b) A merge finished OUTSIDE a tick -- extension.js's configMergeGate, after the
  // user resolved the conflict in the editor -- must be committed with THIS instance's
  // branch in the message. "sync merge vm" on a work-vm repo is a lie the config
  // repo's history keeps forever, and it is the only record of which VM the merge came
  // from.
  const msgRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-mergemsg-"));
  try {
    const SETUP = ["-c", "user.name=x", "-c", "user.email=x@y", "-c", "commit.gpgsign=false"];
    const subjectOf = async (dir) => (await runGit(["log", "-1", "--pretty=%s"], { cwd: dir })).stdout.trim();
    /** A repo with a CLEAN pending merge of `branch` into main (staged, no conflict). */
    async function pendingMergeRepo(tag, branch) {
      const root = mk(msgRoot, tag);
      const configDir = mk(root, "config");
      cs.ensureConfigTree(configDir);
      writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
      await cs.ensureRepo(runGit, configDir, branch);
      await runGit([...SETUP, "checkout", branch], { cwd: configDir });
      writeProfile(configDir, "fromvm", { name: "fromvm", repos: [{ url: "https://h/v.git" }] });
      await runGit([...SETUP, "add", "-A"], { cwd: configDir });
      await runGit([...SETUP, "commit", "-m", "vm side"], { cwd: configDir });
      await runGit([...SETUP, "checkout", "main"], { cwd: configDir });
      writeProfile(configDir, "frommain", { name: "frommain", repos: [{ url: "https://h/m.git" }] });
      await runGit([...SETUP, "add", "-A"], { cwd: configDir });
      await runGit([...SETUP, "commit", "-m", "main side"], { cwd: configDir });
      await runGit([...SETUP, "merge", "--no-ff", "--no-commit", branch], { cwd: configDir });
      return configDir;
    }
    const workDir = await pendingMergeRepo("work", "vm-work");
    const doneWork = await cs.completePendingMerge(runGit, workDir, "vm-work");
    ok("merge-msg: a non-default branch's pending merge completes", doneWork.completed === true);
    ok("merge-msg: ...and the commit names THAT branch, not 'vm'",
      (await subjectOf(workDir)) === "sync merge vm-work", await subjectOf(workDir));
    // The default path is unchanged: no branch argument still commits "sync merge vm".
    const defDir = await pendingMergeRepo("default", "vm");
    const doneDef = await cs.completePendingMerge(runGit, defDir);
    ok("merge-msg: the default path still commits 'sync merge vm'",
      doneDef.completed === true && (await subjectOf(defDir)) === "sync merge vm", await subjectOf(defDir));
  } finally { fs.rmSync(msgRoot, { recursive: true, force: true }); }

  // (c2) Branch isolation in the tick's decision points: every ref the tick reads
  // must be the INSTANCE's branch. Reading another instance's branch here is the
  // shape of bug that silently deletes or resurrects profiles, so each decision
  // point gets its own scenario with the two branches deliberately out of step.

  /** A config repo with profile `p`, instance A on `vm` and instance B on `vm-work`, both seeded. */
  async function twoInstanceRepo(tag) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-iso-" + tag + "-"));
    const configDir = mk(root, "config");
    const storeA = path.join(root, "store-a");
    const storeB = path.join(root, "store-b");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "p", { name: "p", repos: [{ url: "https://h/p.git" }] });
    await cs.ensureRepo(runGit, configDir);
    const tick = (store, vmBranch) => cs.syncTick({
      runGit, configDir, vmBranch,
      readStore: makeReadStore(store), writeStore: makeWriteStore(), storeRoot: store,
    });
    await tick(storeA);            // A seeds from an absent store
    await tick(storeB, "vm-work"); // B seeds its own (also absent) store
    return { root, configDir, storeA, storeB, tick };
  }
  const pWithSdk = (v) => ({ name: "p", repos: [{ url: "https://h/p.git" }], sdks: { node: v } });

  // An invalid file on B's VM must keep B's OWN last-agreed copy, not A's newer one.
  {
    const t = await twoInstanceRepo("preserve");
    try {
      writeStoreProfile(t.storeA, "p", pWithSdk("22"));
      await t.tick(t.storeA);                       // main + vm move ahead; vm-work stays put
      fs.writeFileSync(path.join(t.storeB, "p.json"), "{ half-written", "utf8");
      writeStoreProfile(t.storeB, "q", { name: "q", repos: [{ url: "https://h/q.git" }] });
      const r = await t.tick(t.storeB, "vm-work");
      ok("branch-isolation: B's invalid file is skipped, not committed",
        r.ok && r.skippedInvalid.some((s) => s.name === "p"));
      const shown = await runGit(["show", "vm-work:projects/p.json"], { cwd: t.configDir });
      const preserved = shown.code === 0 ? JSON.parse(shown.stdout) : null;
      ok("branch-isolation: the preserved copy comes from THIS instance's branch",
        !!preserved && !(preserved.sdks && preserved.sdks.node));
      ok("branch-isolation: main keeps the newer profile and gains B's new one",
        ((readProfile(t.configDir, "p") || {}).sdks || {}).node === "22" && !!readProfile(t.configDir, "q"));
    } finally { fs.rmSync(t.root, { recursive: true, force: true }); }
  }

  // "Did the VM change?" is answered against THIS instance's tip, even when the
  // other instance's tip already holds exactly the same tree.
  {
    const t = await twoInstanceRepo("tip");
    try {
      writeStoreProfile(t.storeA, "p", pWithSdk("22"));
      await t.tick(t.storeA);
      writeStoreProfile(t.storeB, "p", pWithSdk("22")); // B's VM reaches the same content on its own
      await t.tick(t.storeB, "vm-work");
      const subj = await runGit(["log", "-1", "--format=%s", "vm-work"], { cwd: t.configDir });
      ok("branch-isolation: B's snapshot is compared against B's tip", subj.stdout.trim() === "vm-work sync");
    } finally { fs.rmSync(t.root, { recursive: true, force: true }); }
  }

  // An empty store is judged "fresh VM" from THIS instance's profile count.
  {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-iso-count-"));
    try {
      const configDir = mk(root, "config");
      const storeA = path.join(root, "store-a");
      const storeB = path.join(root, "store-b");
      cs.ensureConfigTree(configDir);
      await cs.ensureRepo(runGit, configDir);             // empty repo: main + vm, no profiles
      await cs.ensureRepo(runGit, configDir, "vm-work");  // B joins at that same point
      writeProfile(configDir, "p", { name: "p", repos: [{ url: "https://h/p.git" }] });
      const tick = (store, vmBranch) => cs.syncTick({
        runGit, configDir, vmBranch,
        readStore: makeReadStore(store), writeStore: makeWriteStore(), storeRoot: store,
      });
      await tick(storeA);                                 // A seeds; vm now has a profile
      fs.mkdirSync(storeB, { recursive: true });          // B's store dir exists but is EMPTY
      const r = await tick(storeB, "vm-work");
      ok("branch-isolation: B's first sync seeds instead of hitting the mass-deletion guard",
        r.ok && r.seeded && r.warnings.length === 0);
      ok("branch-isolation: B's store received the profile", !!readStoreProfile(storeB, "p"));
      ok("branch-isolation: B's branch advanced to main",
        (await revOf(configDir, "vm-work")) === (await revOf(configDir, "main")));
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  }

  // Ancestry ("is this instance already merged?") is asked about THIS branch: an
  // interrupted tick on A leaves refs/heads/vm ahead of main, which must not
  // change how B's tick behaves.
  {
    const t = await twoInstanceRepo("ancestor");
    try {
      const ident = ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"];
      const mainTreeSha = (await runGit(["rev-parse", "main^{tree}"], { cwd: t.configDir })).stdout.trim();
      const vmSha = await revOf(t.configDir, "vm");
      const orphan = await runGit([...ident, "commit-tree", mainTreeSha, "-p", vmSha, "-m", "interrupted vm snapshot"],
        { cwd: t.configDir });
      await runGit(["update-ref", "refs/heads/vm", orphan.stdout.trim()], { cwd: t.configDir });
      writeProfile(t.configDir, "p", pWithSdk("20"));     // host-side edit for B to receive
      const r = await t.tick(t.storeB, "vm-work");
      ok("branch-isolation: B's tick is unaffected by A's unmerged snapshot", r.ok && !r.blocked && !r.conflict);
      ok("branch-isolation: B's store received the host edit",
        ((readStoreProfile(t.storeB, "p") || {}).sdks || {}).node === "20");
      ok("branch-isolation: B's branch advanced to main",
        (await revOf(t.configDir, "vm-work")) === (await revOf(t.configDir, "main")));
    } finally { fs.rmSync(t.root, { recursive: true, force: true }); }
  }

  // (d) An unusable branch name degrades to `vm` with a warning; the tick runs.
  const badBranchRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-bad-"));
  try {
    const configDir = mk(badBranchRoot, "config");
    const storeDir = path.join(badBranchRoot, "store");
    cs.ensureConfigTree(configDir);
    writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
    await cs.ensureRepo(runGit, configDir);
    const logged = [];
    const r = await cs.syncTick({
      runGit, configDir, vmBranch: "vm/../evil",
      readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
      log: (level, msg) => logged.push(level + ": " + msg),
    });
    ok("branch-invalid: tick still succeeds", r.ok && r.seeded);
    ok("branch-invalid: warns about the name",
      r.warnings.some((w) => /invalid config-sync branch name/.test(w)) &&
      logged.some((l) => /^warn: invalid config-sync branch name/.test(l)));
    ok("branch-invalid: falls back to refs/heads/vm",
      eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm"]) &&
      (await revOf(configDir, "vm")) === (await revOf(configDir, "main")));
  } finally { fs.rmSync(badBranchRoot, { recursive: true, force: true }); }

  // An uppercase instance name is an ordinary branch, not a pseudo-ref: it gets
  // its own ref and must not fall back onto the default instance's store.
  {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-upper-"));
    try {
      const configDir = mk(root, "config");
      const storeDir = path.join(root, "store");
      cs.ensureConfigTree(configDir);
      writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
      await cs.ensureRepo(runGit, configDir, "WORK");
      const r = await cs.syncTick({
        runGit, configDir, vmBranch: "WORK",
        readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
      });
      ok("branch-upper: WORK syncs on its own ref with no warning",
        r.ok && r.seeded && r.warnings.length === 0 &&
        eq(await heads(configDir), ["refs/heads/WORK", "refs/heads/main"].sort()) &&
        (await revOf(configDir, "WORK")) === (await revOf(configDir, "main")));
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  }

  // Names git ACCEPTS syntactically but resolves specially (HEAD) or that alias
  // the host-truth branch (main): a tick must never sync against them — HEAD
  // cannot be created yet reads as the checked-out branch, and `main` would
  // merge main into itself. Both degrade to `vm`.
  for (const bad of ["HEAD", "main"]) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "cs-branch-reserved-"));
    try {
      const configDir = mk(root, "config");
      const storeDir = path.join(root, "store");
      cs.ensureConfigTree(configDir);
      writeProfile(configDir, "web", { name: "web", repos: [{ url: "https://h/w.git" }] });
      await cs.ensureRepo(runGit, configDir, bad);
      ok(`branch-reserved(${bad}): ensureRepo creates the default branch, not the reserved name`,
        eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm"]));
      const r = await cs.syncTick({
        runGit, configDir, vmBranch: bad,
        readStore: makeReadStore(storeDir), writeStore: makeWriteStore(), storeRoot: storeDir,
      });
      ok(`branch-reserved(${bad}): tick warns and still succeeds`,
        r.ok && r.warnings.some((w) => /invalid config-sync branch name/.test(w)));
      ok(`branch-reserved(${bad}): only main + vm exist and main is intact`,
        eq(await heads(configDir), ["refs/heads/main", "refs/heads/vm"]) &&
        !!readProfile(configDir, "web") &&
        (await revOf(configDir, "vm")) === (await revOf(configDir, "main")));
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  }

  // Print summary.
  console.log(`\n  config-sync unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
}

runTests().catch((err) => {
  console.error("Test runner error:", err);
  process.exit(1);
});
