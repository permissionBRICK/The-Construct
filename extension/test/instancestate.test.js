"use strict";
// Plain-node unit tests for the B12 per-instance state store (src/instancestate.js).
// Builds a throwaway fake %LOCALAPPDATA% tree on disk. No deps.
// Run: node instancestate.test.js
//
// THE REGRESSION BAR: an install with one local `agent-vm` and no registry must write
// exactly the files it wrote before this module existed — the legacy top level of
// .construct-settings.json, and NO instances\agent-vm.json. Everything else here is about
// the second VM: its own file, the install-wide/VM-scoped split, atomicity, the
// path-safety guard on a name that becomes a file name, and the stale count the status
// bar renders from these caches.
const fs = require("fs");
const os = require("os");
const path = require("path");
const host = require("../src/host");
const instances = require("../src/instances");
const state = require("../src/instancestate");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), "construct-state-"));
try {
  const base = path.join(root, "LocalAppData");
  const env = { LOCALAPPDATA: base };
  const scriptsDir = path.join(root, "scripts");
  fs.mkdirSync(scriptsDir, { recursive: true });
  const legacyFile = path.join(scriptsDir, host.SETTINGS_FILE);
  const instDir = path.join(base, state.CONTAINER, state.STATE_DIR);
  const def = state.store("agent-vm", scriptsDir, env);
  const work = state.store("work-vm", scriptsDir, env);

  // ── Path math ──────────────────────────────────────────────────────────────
  ok("dir: instances\\ sits beside instances.json", state.stateDir(env) === instDir);
  ok("path: the default instance has NO state file", state.statePath("agent-vm", env) === null);
  ok("path: an absent name is the default store too", state.statePath("", env) === null);
  // CASE-SENSITIVE, like instances.isDefaultInstance and the registry: "Agent-VM" is not
  // a valid instance name at all, so silently treating it as the default would have this
  // module and the registry disagree about which VM a caller meant.
  ok("path: 'agent-vm' is the default store", state.isDefaultStore("agent-vm"));
  ok("path: 'Agent-VM' is NOT the default store (case-sensitive, like the registry)",
    !state.isDefaultStore("Agent-VM"));
  ok("path: ...and it is refused outright rather than given a file",
    state.statePath("Agent-VM", env) === null);
  ok("path: a named instance gets instances\\<name>.json",
    state.statePath("work-vm", env) === path.join(instDir, "work-vm.json"));
  ok("path: no LOCALAPPDATA/TEMP -> no path", state.statePath("work-vm", {}) === null);

  // THE ONE NAME RULE (instances.isValidName) — not a second regex here. A lowercase DNS
  // label cannot hold a separator or a dot, so passing that rule is also what makes the
  // name safe as a FILE NAME.
  ok("name rule: a traversal name resolves to no path", state.statePath("../evil", env) === null);
  ok("name rule: a separator name resolves to no path", state.statePath("a/b", env) === null);
  ok("name rule: a dotted name resolves to no path", state.statePath("a..b", env) === null);
  ok("name rule: an uppercase name resolves to no path", state.statePath("Work-VM", env) === null);
  ok("name rule: an underscore name resolves to no path", state.statePath("work_vm", env) === null);
  ok("name rule: a trailing hyphen resolves to no path", state.statePath("work-", env) === null);
  ok("name rule: the RESERVED construct- prefix resolves to no path",
    state.statePath("construct-work", env) === null);
  ok("name rule: 63 chars ok, 64 rejected (the DNS label limit)",
    state.statePath("a".repeat(63), env) !== null && state.statePath("a".repeat(64), env) === null);
  ok("name rule: the verdict is instances.isValidName's, for every shape",
    ["work-vm", "a", "Work-VM", "work_vm", "work-", "construct-work", "a..b", "a/b"]
      .every((n) => state.isSafeStateName(n) === instances.isValidName(n)));
  ok("name rule: saving under an unusable name throws instead of writing", (() => {
    try { state.saveState(state.store("../evil", scriptsDir, env), { micPassthrough: true }); return false; }
    catch (_) { return true; }
  })());

  // ── ZERO-CHANGE: the default instance keeps the legacy file, and only it ────
  state.saveState(def, { micPassthrough: true, projects: ["web"] });
  ok("default: writes the legacy .construct-settings.json", fs.existsSync(legacyFile));
  ok("default: creates NO instances\\ directory", !fs.existsSync(instDir));
  ok("default: the keys land at the LEGACY TOP LEVEL",
    host.readRawSettings(scriptsDir).micPassthrough === true);
  ok("default: read gives back exactly host.readRawSettings",
    JSON.stringify(state.readState(def)) === JSON.stringify(host.readRawSettings(scriptsDir)));
  // Byte-identical to writing through host.js directly — the file a one-VM install has
  // always had.
  const viaState = fs.readFileSync(legacyFile, "utf8");
  host.writeRawSettings(scriptsDir, { ...host.readRawSettings(scriptsDir) });
  ok("default: the file is byte-identical to a direct host.writeRawSettings",
    viaState === fs.readFileSync(legacyFile, "utf8"));
  state.saveState(def, { installedCommit: "abc1234" });
  ok("default: install-wide keys stay in the legacy file",
    host.readRawSettings(scriptsDir).installedCommit === "abc1234" && !fs.existsSync(instDir));

  // ── A named instance uses only its own file ────────────────────────────────
  state.saveState(work, { micPassthrough: false, t3codeChannel: "nightly" });
  const workFile = path.join(instDir, "work-vm.json");
  ok("named: writes instances\\work-vm.json", fs.existsSync(workFile));
  const doc = JSON.parse(fs.readFileSync(workFile, "utf8"));
  ok("named: records the schema version", doc.version === state.SCHEMA_VERSION);
  ok("named: records its own name", doc.instance === "work-vm");
  ok("named: holds the VM-scoped keys", doc.t3codeChannel === "nightly");
  ok("named: leaves the DEFAULT instance's keys alone",
    host.readRawSettings(scriptsDir).micPassthrough === true);
  ok("named: read returns its own values", state.readState(work).micPassthrough === false);

  state.saveState(work, { projects: ["api"] });
  ok("named: a later save MERGES (t3codeChannel survives)", state.readState(work).t3codeChannel === "nightly");
  ok("named: ...and adds the new key", JSON.stringify(state.readState(work).projects) === '["api"]');

  // PARITY: an install-wide-ONLY save writes the install-wide file and creates NO
  // per-instance file — the PowerShell twin returns without touching one, and a file
  // holding nothing but version/instance would be a phantom VM state.
  const lonely = state.store("lonely-vm", scriptsDir, env);
  const lonelyFile = path.join(instDir, "lonely-vm.json");
  state.saveState(lonely, { installedCommit: "cafe123" });
  ok("install-wide only: the install-wide file is written",
    host.readRawSettings(scriptsDir).installedCommit === "cafe123");
  ok("install-wide only: NO per-instance file is created", !fs.existsSync(lonelyFile));
  ok("install-wide only: ...and the instance still reads as 'nothing saved'",
    JSON.stringify(state.readState(lonely)) === "{}");
  ok("install-wide only: a later VM-scoped save DOES create it",
    (() => { state.saveState(lonely, { smbShare: true }); return fs.existsSync(lonelyFile); })());

  state.saveState(work, { installedCommit: "deadbee", smbShare: false });
  const doc2 = JSON.parse(fs.readFileSync(workFile, "utf8"));
  ok("split: an install-wide key never lands in the per-instance file",
    !Object.prototype.hasOwnProperty.call(doc2, "installedCommit"));
  ok("split: ...it lands in the install-wide file instead",
    host.readRawSettings(scriptsDir).installedCommit === "deadbee");
  ok("split: the VM-scoped key of the same save lands in the per-instance file", doc2.smbShare === false);
  ok("split: readInstallWide always answers from the scripts dir",
    state.readInstallWide(work).installedCommit === "deadbee");

  // A hand-edited install-wide key inside a per-instance file must NOT shadow the real one.
  fs.writeFileSync(path.join(instDir, "hand.json"),
    '{"version":1,"instance":"hand","installedCommit":"ffffff0","micPassthrough":true}\n', "utf8");
  const hand = state.store("hand", scriptsDir, env);
  ok("read: metadata is not returned as a setting", state.readState(hand).version === undefined);
  ok("read: a hand-edited install-wide key is ignored", state.readState(hand).installedCommit === undefined);
  ok("read: the VM-scoped keys beside it still read", state.readState(hand).micPassthrough === true);
  ok("read: ...and the markers still take installedCommit from the install-wide file",
    state.readMarkers(hand).installedCommit === "deadbee");

  // ── Tolerance: missing / corrupt ───────────────────────────────────────────
  ok("read: an instance with no file yields {}",
    JSON.stringify(state.readState(state.store("never", scriptsDir, env))) === "{}");
  const corrupt = state.store("corrupt", scriptsDir, env);
  fs.writeFileSync(path.join(instDir, "corrupt.json"), "{ not json", "utf8");
  ok("read: a corrupt file yields {} (never throws)", JSON.stringify(state.readState(corrupt)) === "{}");
  // A JSON root that is not an OBJECT is "nothing saved" — the same answer the PowerShell
  // twin gives ($null), where an unchecked array would have surfaced PowerShell's own
  // array metadata (Length, Count) as if they were settings.
  for (const bad of ['["an","array"]', '"a string"', "42", "true", "null"]) {
    fs.writeFileSync(path.join(instDir, "corrupt.json"), bad, "utf8");
    ok(`read: a non-object JSON root (${bad}) yields {}`, JSON.stringify(state.readState(corrupt)) === "{}");
  }
  fs.writeFileSync(path.join(instDir, "bom.json"), "﻿" + '{"version":1,"instance":"bom","smbShare":true}', "utf8");
  ok("read: a UTF-8 BOM (PowerShell 5.1's Set-Content) is stripped",
    state.readState(state.store("bom", scriptsDir, env)).smbShare === true);
  state.saveState(corrupt, { smbShare: true });
  ok("write: a corrupt file is replaced by a valid one", state.readState(corrupt).smbShare === true);

  // ── Atomicity ──────────────────────────────────────────────────────────────
  ok("atomic: no .tmp leftovers in instances\\",
    fs.readdirSync(instDir).every((f) => !f.includes(".tmp.")));
  const bytes = fs.readFileSync(workFile);
  ok("atomic: BOM-less UTF-8", !(bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf));
  ok("atomic: trailing newline", fs.readFileSync(workFile, "utf8").endsWith("\n"));
  ok("atomic: a rename failure leaves no temp file behind", (() => {
    const before = new Set(fs.readdirSync(instDir));
    try {
      state.writeStateFile(path.join(instDir, "nested", "deep", "x.json"), "x", { a: 1 });
    } catch (_) { /* may or may not fail; the assertion is about leftovers */ }
    return fs.readdirSync(instDir).every((f) => before.has(f) || !f.includes(".tmp."));
  })());

  // ── The host.js twins ──────────────────────────────────────────────────────
  ok("form: readSettings maps the instance's own state",
    state.readSettings(work).t3codeChannel === "nightly" && state.readSettings(work).smb === false);
  state.saveSettings(work, { mic: true, t3code: true });
  ok("form: saveSettings merges into the instance's own file",
    state.readState(work).micPassthrough === true && state.readState(work).t3codeChannel === "nightly");
  ok("form: ...and leaves the default instance untouched",
    host.readRawSettings(scriptsDir).t3code === undefined);

  // The FORM is the merged view: a rebuild of the second VM must still carry the host
  // git identity, which lives in the install-wide file.
  host.writeRawSettings(scriptsDir, { ...host.readRawSettings(scriptsDir), gitUserName: "Jane Doe", gitEmail: "jane@example.com" });
  ok("form: readSettings folds the INSTALL-WIDE git identity into a named instance's form",
    state.readSettings(work).gitName === "Jane Doe" && state.readSettings(work).gitEmail === "jane@example.com");
  ok("form: ...while the VM-scoped keys still come from that instance's own file",
    state.readSettings(work).t3codeChannel === "nightly");
  ok("form: readMerged of the DEFAULT instance is exactly host.readRawSettings",
    JSON.stringify(state.readMerged(def)) === JSON.stringify(host.readRawSettings(scriptsDir)));
  state.saveSettings(work, { gitName: "Alice", mic: false });
  ok("form: saving the git identity for a named instance writes it INSTALL-WIDE",
    host.readRawSettings(scriptsDir).gitUserName === "Alice" &&
    JSON.parse(fs.readFileSync(workFile, "utf8")).gitUserName === undefined);
  ok("form: ...and the VM-scoped half of the same save stays in the instance file",
    state.readState(work).micPassthrough === false);

  ok("projects: hasPersistedSelection is per instance",
    state.hasPersistedSelection(work) === true &&
    state.hasPersistedSelection(state.store("never", scriptsDir, env)) === false);
  state.saveSelectedProjects(work, ["b", "b", "a", "../escape"]);
  ok("projects: the selection is sanitized + de-duplicated",
    JSON.stringify(state.readSelectedProjects(work)) === '["b","a"]');
  ok("projects: the default instance's selection is still the legacy key",
    JSON.stringify(state.readSelectedProjects(def)) === '["web"]');

  ok("checkpoints: unset reads as null", state.readAppliedAutoCheckpoints(state.store("never", scriptsDir, env)) === null);
  state.saveAppliedAutoCheckpoints(work, true);
  ok("checkpoints: a confirmed value round-trips", state.readAppliedAutoCheckpoints(work) === true);
  state.saveAppliedAutoCheckpoints(work, null);
  ok("checkpoints: null REMOVES the key (not writes null)",
    state.readAppliedAutoCheckpoints(work) === null &&
    !Object.prototype.hasOwnProperty.call(JSON.parse(fs.readFileSync(workFile, "utf8")), "vmAutoCheckpointsApplied"));
  ok("checkpoints: ...and clearing keeps the instance's other keys",
    state.readState(work).t3codeChannel === "nightly");
  state.saveAppliedAutoCheckpoints(def, false);
  ok("checkpoints: the default instance still uses the legacy key",
    host.readRawSettings(scriptsDir).vmAutoCheckpointsApplied === false && !fs.existsSync(path.join(instDir, "agent-vm.json")));

  // ── Markers + the status-bar stale count ───────────────────────────────────
  // installedCommit is install-wide; provisionedCommit is per VM. `work-vm` is behind,
  // the default one is current, and an instance that never recorded a marker is unknown
  // (and therefore NOT counted).
  host.writeRawSettings(scriptsDir, { ...host.readRawSettings(scriptsDir), installedCommit: "1111111", provisionedCommit: "1111111" });
  state.saveState(work, { provisionedCommit: "2222222" });
  const unknown = state.store("unknown-vm", scriptsDir, env);
  ok("markers: the default instance reads both markers from one file",
    state.readMarkers(def).installedCommit === "1111111" && state.readMarkers(def).provisionedCommit === "1111111");
  ok("markers: a named instance takes provisionedCommit from its OWN file",
    state.readMarkers(work).installedCommit === "1111111" && state.readMarkers(work).provisionedCommit === "2222222");
  ok("markers: ...and does NOT inherit the legacy top-level provisionedCommit",
    state.readMarkers(unknown).provisionedCommit === "");
  ok("stale count: only the instance that is actually behind is counted",
    state.countStale([def, work, unknown]) === 1);
  ok("stale count: zero when every known marker matches", state.countStale([def, unknown]) === 0);
  ok("stale count: an empty list is 0 (a default-only install never shows a count)",
    state.countStale([]) === 0 && state.countStale(null) === 0);

  // ── The post-reprovision fast poll watches ONE instance ────────────────────
  // The panel polls at 5 s right after it launches a reprovision console and drops back
  // to the normal cadence when THAT run records a new provisionedCommit. Held against
  // "the active instance" it lied in both directions across a switch: a reprovision of A
  // was reported finished the moment the window moved to B (B's marker differs from A's
  // baseline), and B's own reprovision could never end it. So the subject is captured
  // once, as a store.
  const A = state.store("alpha-vm", scriptsDir, env);
  const B = state.store("beta-vm", scriptsDir, env);
  state.saveState(A, { provisionedCommit: "aaaaaaa" });
  state.saveState(B, { provisionedCommit: "bbbbbbb" });
  let clock = 1000;
  const watch = state.createProvisionWatch(A, { maxMs: 500, now: () => clock });
  ok("watch: it records the WATCHED instance's baseline", watch.baseline === "aaaaaaa");
  ok("watch: nothing has happened yet", watch.done() === false);
  // The window switches to B and B is reprovisioned: A's watch must not notice.
  state.saveState(B, { provisionedCommit: "ccccccc" });
  ok("watch: ANOTHER instance's reprovision does NOT complete it", watch.done() === false);
  ok("watch: ...and it still reports the watched instance's own marker", watch.current() === "aaaaaaa");
  // A's own reprovision lands.
  state.saveState(A, { provisionedCommit: "ddddddd" });
  ok("watch: the WATCHED instance's new commit completes it", watch.done() === true);
  ok("watch: ...and current() is that new commit", watch.current() === "ddddddd");
  // The cap is the other exit: a reprovision that lands the SAME commit records no change.
  clock = 2000;
  const capped = state.createProvisionWatch(B, { maxMs: 500, now: () => clock });
  ok("watch: an unchanged marker does not complete it before the cap", capped.done() === false);
  clock = 2600;
  ok("watch: ...and the cap ends it", capped.done() === true);
  // An instance with no marker at all starts from "" and is still completed by its first.
  const fresh = state.store("fresh-vm", scriptsDir, env);
  clock = 3000;
  const freshWatch = state.createProvisionWatch(fresh, { maxMs: 500, now: () => clock });
  ok("watch: an instance with no marker starts from an empty baseline", freshWatch.baseline === "");
  ok("watch: ...and is not complete just for being empty", freshWatch.done() === false);
  state.saveState(fresh, { provisionedCommit: "eeeeeee" });
  ok("watch: ...and its FIRST recorded commit completes it", freshWatch.done() === true);
  // The DEFAULT instance is watched through the legacy file, like everything else.
  clock = 4000;
  const defWatch = state.createProvisionWatch(def, { maxMs: 500, now: () => clock });
  ok("watch: the default instance watches its legacy top-level marker",
    defWatch.baseline === host.readRawSettings(scriptsDir).provisionedCommit);
  state.saveState(def, { provisionedCommit: "fffffff" });
  ok("watch: ...and its change completes it", defWatch.done() === true);
  ok("watch: ...without ever creating instances\\agent-vm.json",
    !fs.existsSync(path.join(instDir, "agent-vm.json")));
} finally {
  fs.rmSync(root, { recursive: true, force: true });
}

console.log(`\n  per-instance state store unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
