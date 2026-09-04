"use strict";
// PER-INSTANCE STATE STORE — the JS twin of lib/AgentVm.InstanceState.ps1.
//
// Both sides read and write the SAME files and MUST agree on the split below; the PS
// module's header is the shared contract and this comment restates it.
//
// TWO STORES, ONE SPLIT
//   • INSTALL-WIDE facts stay in the scripts checkout's `.construct-settings.json`, where
//     they have always lived: `installedCommit`, `constructRepo`, `constructRef` (which
//     Construct is installed) plus the host git identity the installer applies to every VM
//     (`gitUserName`, `gitEmail`, `gitCredentialStore`).
//   • Everything else is VM-SCOPED and belongs to ONE instance: `provisionedCommit`, the
//     project selection, the mic preference, RAM/disk/release, the VS Code / SMB / patch
//     toggles, the T3 Code toggles and channel, the automatic-checkpoint preference and
//     its applied marker.
//
// WHERE THE VM-SCOPED HALF LIVES
//   • The DEFAULT instance (`agent-vm`) keeps reading and writing its VM-scoped keys at the
//     LEGACY TOP LEVEL of `.construct-settings.json` and NOTHING ELSE — no
//     `instances\agent-vm.json` is created for a default-only install, so a one-VM install
//     writes exactly the files it wrote before this module existed.
//   • Every other instance uses only `%LOCALAPPDATA%\The-Construct\instances\<name>.json`,
//     next to `instances.json` and independent of the scripts checkout.
//
// Schema (version 1): { "version": 1, "instance": "<name>", "<vm-scoped key>": … }.
// `version`/`instance` are metadata: written, and skipped when the file is read back as
// settings. Install-wide keys are split off on write and ignored on read, so a hand-edited
// per-instance file can never shadow the installed commit.
//
// Like host.js this is a PURE fs/path module — it never requires `vscode` — so it unit-
// tests against a fake %LOCALAPPDATA% tree.

const fs = require("fs");
const path = require("path");

const host = require("./host");
const instances = require("./instances");
const updates = require("./updates");

const CONTAINER = "The-Construct";       // %LOCALAPPDATA%\The-Construct
const STATE_DIR = "instances";           // …\The-Construct\instances\<name>.json
const SCHEMA_VERSION = 1;

/** Keys that describe the INSTALLED CONSTRUCT (or the host identity it applies), not a VM.
 *  Mirrored verbatim by $script:ConstructInstallWideKeys in lib/AgentVm.InstanceState.ps1. */
const INSTALL_WIDE_KEYS = Object.freeze([
  "installedCommit",
  "constructRepo",
  "constructRef",
  "gitUserName",
  "gitEmail",
  "gitCredentialStore",
]);
const INSTALL_WIDE = new Set(INSTALL_WIDE_KEYS);

/** Written into every per-instance file and skipped when it is read back as settings. */
const META_KEYS = Object.freeze(["version", "instance"]);
const META = new Set(META_KEYS);

/** Is this a usable instance name for a state file? THE ONE NAME RULE
 *  (instances.isValidName — mirrored by lib/AgentVm.Instances.ps1 and VmNameValidator.cs),
 *  never a second copy of it here. The name becomes a FILE NAME in instances\, so it has
 *  to be validated; a lowercase DNS label cannot contain a separator or a dot, so passing
 *  that rule is also what makes it safe as a file name. Pure. */
function isSafeStateName(name) {
  const s = String(name == null ? "" : name).trim();
  return !!s && instances.isValidName(s);
}

/** Does this name read and write the LEGACY top-level keys? True for the default instance
 *  and for an empty/absent name (a caller that never learned one is, by definition, on
 *  today's single-VM path).
 *
 *  CASE-SENSITIVE, like instances.isDefaultInstance and the registry itself: instance
 *  names are lowercase DNS labels, so "Agent-VM" is not the default instance — it is not a
 *  valid instance name at all, and silently treating it as the default would have this
 *  module and the registry disagree about which VM a caller meant. Callers holding an ssh
 *  ALIAS lowercase it first (alias = name, lowercased), the one derivation rule. Pure. */
function isDefaultStore(name) {
  const s = String(name == null ? "" : name).trim();
  if (!s) return true;
  return s === instances.DEFAULT_INSTANCE_NAME;
}

/** `%LOCALAPPDATA%\The-Construct\instances`, or null when no base dir resolves. Pure. */
function stateDir(env) {
  const base = host.localAppData(env);
  return base ? path.join(base, CONTAINER, STATE_DIR) : null;
}

/** That instance's state file, or null when it has none: the default instance (its state
 *  is the legacy file), an unsafe name, or no resolvable base dir. Pure. */
function statePath(name, env) {
  if (isDefaultStore(name)) return null;
  const s = String(name == null ? "" : name).trim();
  if (!isSafeStateName(s)) return null;
  const dir = stateDir(env);
  return dir ? path.join(dir, s + ".json") : null;
}

/** A store handle: which instance, and the scripts dir holding the install-wide settings.
 *  `{ name, scriptsDir, env }` — every reader/writer below takes one of these. Pure. */
function store(instance, scriptsDir, env) {
  const name = typeof instance === "string"
    ? instance
    : (instance && instance.name) || instances.DEFAULT_INSTANCE_NAME;
  return { name, scriptsDir: scriptsDir || null, env: env || undefined };
}

/** Parse a JSON object file, or {} when absent/unreadable/not an object. Strips a UTF-8
 *  BOM (Windows PowerShell 5.1's `Set-Content -Encoding UTF8` writes one). */
function readJsonObject(file) {
  if (!file) return {};
  let txt;
  try { txt = fs.readFileSync(file, "utf8"); } catch (_) { return {}; }
  try {
    const o = JSON.parse(txt.replace(/^﻿/, ""));
    return (o && typeof o === "object" && !Array.isArray(o)) ? o : {};
  } catch (_) { return {}; }
}

/** The install-wide half: always the scripts dir's `.construct-settings.json`. */
function readInstallWide(st) {
  return host.readRawSettings(st && st.scriptsDir);
}

/**
 * The VM-scoped settings of one instance, as a plain object ({} when nothing is saved).
 *
 * The DEFAULT instance answers from the scripts dir's `.construct-settings.json`,
 * unchanged and unfiltered — those keys ARE its state, and a one-VM install must keep
 * seeing exactly the object it always saw. Any other instance answers from its own file,
 * with metadata and install-wide keys stripped so neither can shadow the real ones.
 */
function readState(st) {
  if (isDefaultStore(st && st.name)) return host.readRawSettings(st && st.scriptsDir);
  const file = statePath(st.name, st.env);
  if (!file) return {};
  const doc = readJsonObject(file);
  const out = {};
  for (const k of Object.keys(doc)) {
    if (META.has(k) || INSTALL_WIDE.has(k)) continue;
    out[k] = doc[k];
  }
  return out;
}

/**
 * Write one per-instance file ATOMICALLY: full content to a sibling temp file, then
 * rename over the destination (atomic on both NTFS and POSIX), so a crash mid-write can
 * never leave a half-written document — which would read as "nothing saved" and silently
 * revert that VM's whole configuration. BOM-less UTF-8 with a trailing newline, matching
 * instances.save(). Keys are sorted ordinally so the PowerShell twin lays the file out
 * the same way. Creates the instances\ dir. Throws on I/O failure.
 */
function writeStateFile(file, name, values) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const doc = { version: SCHEMA_VERSION, instance: String(name).trim() };
  for (const k of Object.keys(values).sort()) doc[k] = values[k];
  const tmp = file + ".tmp." + process.pid + "." + Date.now();
  try {
    fs.writeFileSync(tmp, JSON.stringify(doc, null, 2) + "\n", "utf8");
    fs.renameSync(tmp, file);
  } catch (e) {
    try { fs.unlinkSync(tmp); } catch (_) {}
    throw e;
  }
  return file;
}

/**
 * Merge `patch` into one instance's saved state, preserving every key already there.
 *
 * The DEFAULT instance is written straight through host.writeRawSettings, so its file
 * keeps being produced exactly as before. Any other instance has its VM-scoped keys
 * written to its own file and its install-wide keys (if the caller mixed some in) merged
 * into the scripts dir's `.construct-settings.json`, which every instance shares.
 * Returns the merged VM-scoped object. Throws when there is nowhere to write.
 */
function saveState(st, patch) {
  const values = (patch && typeof patch === "object") ? patch : {};
  if (isDefaultStore(st && st.name)) {
    if (!st || !st.scriptsDir) throw new Error("No Construct scripts directory resolved");
    const merged = { ...host.readRawSettings(st.scriptsDir), ...values };
    host.writeRawSettings(st.scriptsDir, merged);
    return merged;
  }
  const file = statePath(st.name, st.env);
  if (!file) throw new Error(`"${st.name}" is not a usable Construct instance name for a state file`);
  const installWide = {};
  const vmScoped = {};
  for (const k of Object.keys(values)) {
    if (INSTALL_WIDE.has(k)) installWide[k] = values[k];
    else vmScoped[k] = values[k];
  }
  if (Object.keys(installWide).length) {
    if (!st.scriptsDir) throw new Error("No Construct scripts directory resolved");
    host.writeRawSettings(st.scriptsDir, { ...host.readRawSettings(st.scriptsDir), ...installWide });
  }
  // An install-wide-ONLY save must not bring a per-instance file into existence: it would
  // be a file holding nothing but `version`/`instance`, and the PowerShell twin
  // (Save-ConstructInstanceState) returns here without touching one. A VM with no
  // VM-scoped setting yet has no file — that is what "no instances\agent-vm.json for a
  // default-only install" means one level up, and the two writers must agree on it.
  if (Object.keys(vmScoped).length === 0) return readState(st);
  const merged = { ...readState(st), ...vmScoped };
  writeStateFile(file, st.name, merged);
  return merged;
}

// ── Instance-aware twins of the host.js settings helpers ─────────────────────
// Same contracts as the host.js originals, but resolved against a STORE instead of a
// scripts dir. For the default instance each one is the host.js call it wraps, so a
// one-VM install behaves identically.

/**
 * The COMPLETE settings view of one instance: its VM-scoped state laid over the
 * install-wide keys the settings form also shows (the host git identity). The two halves
 * live in two files for a non-default instance, but the panel's form — and every consumer
 * that replays "the saved settings" into a provision — needs them as one object, or a
 * rebuild of the second VM would drop the git identity.
 *
 * For the DEFAULT instance both halves ARE the same file, so this is exactly
 * host.readRawSettings(scriptsDir).
 */
function readMerged(st) {
  const state = readState(st);
  if (isDefaultStore(st && st.name)) return state;
  const wide = readInstallWide(st);
  const out = {};
  for (const k of INSTALL_WIDE_KEYS) {
    if (Object.prototype.hasOwnProperty.call(wide, k)) out[k] = wide[k];
  }
  return { ...out, ...state };
}

/** Read settings in the webview form shape (host.mapToForm of the merged view). */
function readSettings(st) { return host.mapToForm(readMerged(st)); }

/** Merge a webview form into this instance's state. Returns the merged object. */
function saveSettings(st, form) { return saveState(st, host.mapFromForm(form)); }

/** The persisted project SELECTION for this instance (host.readSelectedProjects's rule,
 *  applied to this instance's `projects` key). */
function readSelectedProjects(st) {
  const raw = readState(st);
  const arr = Array.isArray(raw.projects) ? raw.projects : [];
  const out = [];
  for (const v of arr) {
    const s = host.safeProfileName(v);
    if (s && !out.includes(s)) out.push(s);
  }
  return out;
}

/** Has this instance ever had a selection persisted (even an empty one)? */
function hasPersistedSelection(st) { return Array.isArray(readState(st).projects); }

/** Persist this instance's project selection (sanitized + de-duplicated). */
function saveSelectedProjects(st, names) {
  const list = Array.isArray(names) ? names : [];
  const clean = [];
  for (const v of list) {
    const s = host.safeProfileName(v);
    if (s && !clean.includes(s)) clean.push(s);
  }
  return saveState(st, { projects: clean });
}

const APPLIED_KEY = "vmAutoCheckpointsApplied";

/** The last automatic-checkpoint value confirmed onto THIS instance's VM, or null. */
function readAppliedAutoCheckpoints(st) {
  const v = readState(st)[APPLIED_KEY];
  return typeof v === "boolean" ? v : null;
}

/** Record the value just confirmed onto this instance's VM. `null` clears the marker. */
function saveAppliedAutoCheckpoints(st, value) {
  if (value === null) {
    // Clearing has to REMOVE the key, not write null: readAppliedAutoCheckpoints treats a
    // non-boolean as "never confirmed", but a lingering null would still be written back
    // by every later merge.
    const next = { ...readState(st) };
    delete next[APPLIED_KEY];
    return replaceState(st, next);
  }
  return saveState(st, { [APPLIED_KEY]: value === true });
}

/** Replace this instance's VM-scoped state wholesale (used only where a key must be
 *  REMOVED — a merge cannot express a deletion). Install-wide keys are left alone. */
function replaceState(st, next) {
  const values = (next && typeof next === "object") ? next : {};
  if (isDefaultStore(st && st.name)) {
    if (!st || !st.scriptsDir) throw new Error("No Construct scripts directory resolved");
    host.writeRawSettings(st.scriptsDir, values);
    return values;
  }
  const file = statePath(st.name, st.env);
  if (!file) throw new Error(`"${st.name}" is not a usable Construct instance name for a state file`);
  const vmScoped = {};
  for (const k of Object.keys(values)) {
    if (INSTALL_WIDE.has(k) || META.has(k)) continue;
    vmScoped[k] = values[k];
  }
  writeStateFile(file, st.name, vmScoped);
  return vmScoped;
}

/**
 * The Construct update markers for ONE instance: repo/ref/installedCommit from the
 * install-wide file, `provisionedCommit` from THAT instance's state. For the default
 * instance both halves are the same file, so this is byte-for-byte today's
 * `updates.readMarkers(host.readRawSettings(dir))`.
 */
function readMarkers(st) {
  return updates.readMarkers(readInstallWide(st), readState(st));
}

/**
 * How many of these instances are BEHIND the installed Construct, judged from the
 * host-side caches ALONE — each store's own `provisionedCommit` against the
 * `installedCommit` of the install-wide file beside it. No SSH, no network, no history
 * lookup: this runs on the status-bar item's refresh path, where a fan-out over every
 * registered VM would stall the window (and would be wrong for a VM that is switched off).
 *
 * `stores` is one store per instance (see `store()`). Instances whose marker is unknown
 * are NOT counted — the same conservative rule the per-VM banner applies.
 */
function countStale(stores) {
  let n = 0;
  for (const st of (stores || [])) {
    try { if (updates.isProvisionStale(readMarkers(st))) n += 1; } catch (_) { /* skip */ }
  }
  return n;
}

/**
 * WATCH ONE INSTANCE'S PROVISIONED MARKER for the change a reprovision produces.
 *
 * The panel polls fast (5 s) right after it launches a reprovision console, and drops back
 * to its normal cadence as soon as that run has recorded a new `provisionedCommit` — or
 * when the cap elapses, since a reprovision that lands the SAME commit records no change.
 *
 * The subject is captured ONCE, as a store: the marker is per VM now, so a poll that
 * re-read "the active instance" would compare instance B's marker against instance A's
 * baseline — ending the fast poll on the first refresh after a switch, or never ending it.
 * The store must be the one the launch actually used (same instance, same scripts dir),
 * not one re-resolved after the pre-flight and the confirmation modal have been awaited.
 *
 * `done(now)` is the whole rule: true once THIS store reports a different, non-empty
 * commit, or once `deadline` has passed. Pure apart from the one small file read, so the
 * A/B behaviour is unit-testable instead of only observable in a live window.
 */
function createProvisionWatch(store, opts = {}) {
  const now = typeof opts.now === "function" ? opts.now : () => Date.now();
  const baseline = provisionedCommitOf(store);
  const deadline = now() + (typeof opts.maxMs === "number" ? opts.maxMs : 0);
  return {
    store,
    baseline,
    deadline,
    /** The marker THIS watch's instance reports right now. */
    current() { return provisionedCommitOf(store); },
    /** Has the watched reprovision finished (or the cap elapsed)? */
    done() {
      const current = provisionedCommitOf(store);
      if (current && current !== baseline) return true;
      return now() >= deadline;
    },
  };
}

/** One instance's recorded provisioned commit ("" when unknown). The marker
 *  `isProvisionStale` and `augment` compare against, read for a single store. */
function provisionedCommitOf(store) {
  try { return readMarkers(store).provisionedCommit || ""; } catch (_) { return ""; }
}

module.exports = {
  CONTAINER, STATE_DIR, SCHEMA_VERSION, INSTALL_WIDE_KEYS, META_KEYS,
  isSafeStateName, isDefaultStore, stateDir, statePath, store,
  readInstallWide, readState, readMerged, saveState, replaceState, writeStateFile,
  readSettings, saveSettings,
  readSelectedProjects, hasPersistedSelection, saveSelectedProjects,
  readAppliedAutoCheckpoints, saveAppliedAutoCheckpoints,
  readMarkers, countStale, provisionedCommitOf, createProvisionWatch,
};
