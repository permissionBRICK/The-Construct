"use strict";
// Host-side filesystem helpers: locate the Construct install (the folder holding
// the PowerShell lifecycle scripts) and read/write its persisted settings.
//
// This module is deliberately a PURE fs/path module — it never requires `vscode`
// — so it unit-tests against a fake %LOCALAPPDATA% tree. The extension layer
// feeds it the `construct.scriptsDir` override + `process.env` and owns all the
// VS Code UI (toasts, reveal-in-OS).
//
// Layout (from install.ps1): the web installer extracts the repo zip to
//   %LOCALAPPDATA%\The-Construct\<owner-repo-ref-slug>\<repo>-<ref>\
// and that innermost folder is where Auto-Install.ps1, projects\ and
// .construct-settings.json live. That innermost folder is the "scripts dir".

const fs = require("fs");
const path = require("path");

const CONTAINER = "The-Construct";   // %LOCALAPPDATA%\The-Construct
const MARKER = "Auto-Install.ps1";   // the file that identifies a scripts dir
const SETTINGS_FILE = ".construct-settings.json";
const PROJECTS_DIR = "projects";

// ── Path resolution ───────────────────────────────────────────────────────────

/** The base under which install.ps1 anchors its work folder. Mirrors install.ps1:
 *  prefer %LOCALAPPDATA%, else %TEMP%. (The host side is always Windows.) */
function localAppData(env) {
  env = env || process.env;
  return env.LOCALAPPDATA || env.TEMP || "";
}

function isDir(p) {
  try { return fs.statSync(p).isDirectory(); } catch (_) { return false; }
}

function listDirs(d) {
  try {
    return fs.readdirSync(d, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => path.join(d, e.name));
  } catch (_) { return []; }
}

/**
 * Find the newest extracted Construct repo (the folder containing Auto-Install.ps1)
 * under <base>\The-Construct. install.ps1 nests it as \<slug>\<repo-ref>\, but we
 * also accept a marker one level down so a hand-placed checkout still resolves.
 * "Newest" = most recently (re)written Auto-Install.ps1, which Expand-Archive
 * -Force rewrites on every install/refresh. Returns the dir path or null.
 */
function findScriptsDir(base) {
  if (!base) return null;
  const container = path.join(base, CONTAINER);
  const candidates = [];
  const consider = (dir) => {
    try {
      const st = fs.statSync(path.join(dir, MARKER));
      if (st.isFile()) candidates.push({ dir, mtime: st.mtimeMs });
    } catch (_) { /* no marker here */ }
  };
  for (const lvl1 of listDirs(container)) {
    consider(lvl1);
    for (const lvl2 of listDirs(lvl1)) consider(lvl2);
  }
  if (!candidates.length) return null;
  candidates.sort((a, b) => b.mtime - a.mtime);
  return candidates[0].dir;
}

/**
 * Resolve the scripts dir. Precedence, most specific first:
 *   1. the ACTIVE INSTANCE's `scriptsDir` (registry field; null = "not pinned"),
 *   2. the machine-wide `construct.scriptsDir` setting override,
 *   3. auto-detect the newest install under <base>\The-Construct.
 * Each override is used only when it points at a real directory, so a stale path
 * degrades to detection rather than to "no install found". Returns a path or null.
 * `opts`: { instanceScriptsDir?, scriptsDir?, localAppData?, env? }.
 *
 * The default instance carries `scriptsDir: null`, so an install with no registry
 * resolves exactly as it did before instances existed. The settings file
 * (.construct-settings.json) stays per SCRIPTS DIR — two instances that share a
 * scripts dir deliberately share its settings.
 */
function resolveScriptsDir(opts = {}) {
  const pinned = opts.instanceScriptsDir != null ? String(opts.instanceScriptsDir).trim() : "";
  if (pinned && isDir(pinned)) return pinned;
  const override = opts.scriptsDir != null ? String(opts.scriptsDir).trim() : "";
  if (override && isDir(override)) return override;
  const base = opts.localAppData != null ? String(opts.localAppData) : localAppData(opts.env);
  return findScriptsDir(base);
}

function settingsPath(scriptsDir) { return path.join(scriptsDir, SETTINGS_FILE); }
function projectsDir(scriptsDir) { return path.join(scriptsDir, PROJECTS_DIR); }

/**
 * The dedicated host config dir (docs/config-sync.md §4): a single, machine-wide
 * location OUTSIDE any zip checkout — %LOCALAPPDATA%\The-Construct\config — shared
 * across installed repo/ref slugs, so self-update's Expand-Archive never touches
 * live config. Deliberately NOT slug-scoped (unlike findScriptsDir). Profiles live
 * under its projects/ subdir, so the existing profile helpers work against it
 * unchanged: listProjectProfiles(configDir), readProjectProfile(configDir, name), …
 * Returns null when no base dir is resolvable. Pure path math, no fs.
 */
function configDir(env) {
  const base = localAppData(env);
  return base ? path.join(base, CONTAINER, "config") : null;
}

// ── Settings read/write ─────────────────────────────────────────────────────--

/** Raw settings object from disk, or {} if absent/unreadable. Strips a UTF-8 BOM
 *  (Windows PowerShell 5.1's `Set-Content -Encoding UTF8` writes one). */
function readRawSettings(scriptsDir) {
  if (!scriptsDir) return {};
  let txt;
  try { txt = fs.readFileSync(settingsPath(scriptsDir), "utf8"); } catch (_) { return {}; }
  try {
    const o = JSON.parse(txt.replace(/^\uFEFF/, ""));
    return (o && typeof o === "object" && !Array.isArray(o)) ? o : {};
  } catch (_) { return {}; }
}

/**
 * Read a project profile JSON (`<scriptsDir>/projects/<name>.json`) as a plain
 * object, or null if missing / unreadable / not an object. The name is treated as
 * a single filename — anything with a path separator or ".." is rejected so a
 * project name (which ultimately comes from the VM) can't escape the projects dir.
 * Strips a UTF-8 BOM like readRawSettings.
 */
function readProjectProfile(scriptsDir, name) {
  if (!scriptsDir) return null;
  const safe = String(name == null ? "" : name);
  if (!safe || /[\/\\]/.test(safe) || safe.includes("..")) return null;
  let txt;
  try { txt = fs.readFileSync(path.join(projectsDir(scriptsDir), safe + ".json"), "utf8"); }
  catch (_) { return null; }
  try {
    const o = JSON.parse(txt.replace(/^\uFEFF/, ""));
    return (o && typeof o === "object" && !Array.isArray(o)) ? o : null;
  } catch (_) { return null; }
}

function writeRawSettings(scriptsDir, obj) {
  // BOM-less UTF-8 with a trailing newline. PowerShell's ConvertFrom-Json reads
  // it fine; the formatting keeps the file diff-friendly if hand-inspected.
  fs.writeFileSync(settingsPath(scriptsDir), JSON.stringify(obj, null, 2) + "\n", "utf8");
}

// ── Project profiles: list / write / selection ──────────────────────────────--
// The Projects panel edits the per-project profile JSONs the installer's selector
// and the VM's generate-runtime-config.sh both read (<scriptsDir>/projects/*.json).
// These helpers stay in this pure fs/path module (no vscode) so they unit-test the
// same way readProjectProfile does, against a fake scripts dir.

// THE profile-name rule for this engine -- one place, reused by every read, write,
// delete and by publish (extension/src/configsync.js). A project name must be a
// single, safe filename: no path separator, no "..", nothing that would let a
// VM-supplied or webview-supplied name escape the projects dir, and none of the
// characters Windows refuses in a file name (a profile file has to exist on the
// host, and these names travel between the PowerShell and JS engines through a git
// repo -- lib/AgentVm.Common.ps1's Test-ConstructSafeProfileName is the twin).
// Pure; returns the trimmed name or "".
function safeProfileName(name) {
  const s = String(name == null ? "" : name).trim();
  if (!s || /[\/\\]/.test(s) || s.includes("..")) return "";
  if (/[:*?"<>|]/.test(s)) return "";
  // eslint-disable-next-line no-control-regex
  if (/[\x00-\x1f]/.test(s)) return "";
  return s;
}

/**
 * List the project-profile base names present under <scriptsDir>/projects — every
 * `*.json` except the schema file — sorted. Mirrors the installer's selector scan
 * (Select-ProjectProfiles) so the panel shows the same set. The blank builtin
 * "default" is INCLUDED here (unlike the console selector, which hides it) so the
 * user can see and edit it; callers that treat it specially do so themselves.
 * Best-effort: an unreadable dir yields []. Pure.
 */
function listProjectProfiles(scriptsDir) {
  if (!scriptsDir) return [];
  let entries;
  try { entries = fs.readdirSync(projectsDir(scriptsDir), { withFileTypes: true }); }
  catch (_) { return []; }
  return entries
    .filter((e) => e.isFile() && e.name.toLowerCase().endsWith(".json") && e.name !== "project.schema.json")
    .map((e) => e.name.slice(0, -5)) // strip ".json"
    .sort();
}

/**
 * Write a project profile object to <scriptsDir>/projects/<name>.json, traversal-safe
 * (the name is rejected if it isn't a bare filename) and BOM-less pretty JSON with a
 * trailing newline — exactly like writeRawSettings, so the file interops with the
 * installer + the VM's jq readers. Creates the projects dir if absent. The name is
 * taken from the sanitized argument (NOT from obj.name) so the on-disk filename and
 * the requested target always agree. Throws on a bad name or a write failure.
 */
function writeProjectProfile(scriptsDir, name, obj) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const safe = safeProfileName(name);
  if (!safe) throw new Error("Invalid project name");
  const dir = projectsDir(scriptsDir);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, safe + ".json"), JSON.stringify(obj, null, 2) + "\n", "utf8");
}

/** Delete one profile file, traversal-safe. Returns false when already absent. */
function deleteProjectProfile(scriptsDir, name) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const safe = safeProfileName(name);
  if (!safe) throw new Error("Invalid project name");
  try {
    fs.unlinkSync(path.join(projectsDir(scriptsDir), safe + ".json"));
    return true;
  } catch (e) {
    if (e && e.code === "ENOENT") return false;
    throw e;
  }
}

/**
 * Atomic create-if-absent: write a profile ONLY when no file with that name
 * exists yet (case-insensitive, matching Windows/macOS filesystem semantics).
 * Writes to a temp file first, then renames — so a crash mid-write never
 * leaves a truncated final file. Returns true if the file was created, false
 * if the destination already existed (or a case-variant did). Throws on I/O
 * errors other than "destination appeared after planning".
 */
function writeProjectProfileIfAbsent(scriptsDir, name, obj) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const safe = safeProfileName(name);
  if (!safe) throw new Error("Invalid project name");
  const dir = projectsDir(scriptsDir);
  fs.mkdirSync(dir, { recursive: true });
  const dest = path.join(dir, safe + ".json");
  // Fast pre-check: case-insensitive scan so "API.json" won't clobber "api.json"
  // on Windows/macOS. Not authoritative (race possible) — the link below is.
  const existingLower = new Set();
  try {
    for (const e of fs.readdirSync(dir)) existingLower.add(e.toLowerCase());
  } catch (_) { /* empty dir is fine */ }
  if (existingLower.has((safe + ".json").toLowerCase())) return false;
  // Write complete content to a collision-safe temp, then hard-link to dest.
  // linkSync is atomic and fails with EEXIST if dest appeared between the
  // pre-check and now — no TOCTOU gap.
  const tmp = dest + ".tmp." + process.pid + "." + Date.now();
  try {
    fs.writeFileSync(tmp, JSON.stringify(obj, null, 2) + "\n", "utf8");
    try {
      fs.linkSync(tmp, dest);
    } catch (linkErr) {
      if (linkErr.code === "EEXIST") return false;
      throw linkErr;
    }
    return true;
  } finally {
    try { fs.unlinkSync(tmp); } catch (_) {}
  }
}

/**
 * The persisted project SELECTION: the base names the user has ticked, kept as a
 * forward-compat `projects` array in .construct-settings.json (mirroring how
 * mapFromForm writes `vmMemoryGB` etc. for the installer to adopt later — the
 * installer's `-Projects` / PROJECTS= list can read it). Returns a de-duplicated
 * array of clean string names, or [] when the key is absent/malformed. Pure.
 */
function readSelectedProjects(scriptsDir) {
  const raw = readRawSettings(scriptsDir);
  const arr = Array.isArray(raw.projects) ? raw.projects : [];
  const out = [];
  for (const v of arr) {
    const s = safeProfileName(v);
    if (s && !out.includes(s)) out.push(s);
  }
  return out;
}

/**
 * Whether the `projects` key has been explicitly persisted (even as an empty array).
 * Distinguishes "user has never saved a selection" (absent key) from "user
 * deliberately saved an empty selection" (projects: []). The auto-import uses this
 * to decide whether to seed from the VM's live list. Pure.
 */
function hasPersistedSelection(scriptsDir) {
  const raw = readRawSettings(scriptsDir);
  return Array.isArray(raw.projects);
}

/**
 * Persist the project selection into .construct-settings.json under `projects`,
 * merging over the existing file so unmanaged keys (git identity, installedCommit,
 * vmMemoryGB, …) survive — same discipline as saveSettings. `names` is sanitized +
 * de-duplicated; a non-array clears the key to []. Returns the merged object.
 * Throws if there is no scripts dir.
 */
function saveSelectedProjects(scriptsDir, names) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const list = Array.isArray(names) ? names : [];
  const clean = [];
  for (const v of list) {
    const s = safeProfileName(v);
    if (s && !clean.includes(s)) clean.push(s);
  }
  const merged = { ...readRawSettings(scriptsDir), projects: clean };
  writeRawSettings(scriptsDir, merged);
  return merged;
}

/**
 * Translate the on-disk settings into the webview form shape. Only keys that are
 * actually present (and well-typed) are emitted, so the panel's applySettings can
 * leave its HTML defaults untouched for anything the file doesn't carry — e.g. a
 * file the installer wrote with just the three git keys.
 */
function mapToForm(raw) {
  raw = raw || {};
  const has = (k) => Object.prototype.hasOwnProperty.call(raw, k);
  const form = {};
  if (has("gitUserName")) form.gitName = String(raw.gitUserName);
  if (has("gitEmail")) form.gitEmail = String(raw.gitEmail);
  if (typeof raw.gitCredentialStore === "boolean") form.gitCred = raw.gitCredentialStore;
  if (has("vmMemoryGB")) form.ram = String(raw.vmMemoryGB);
  if (has("vmDiskGB")) form.disk = String(raw.vmDiskGB);
  if (has("vmCpuCount")) form.cpu = String(raw.vmCpuCount);
  if (has("ubuntuRelease")) form.ubuntu = String(raw.ubuntuRelease);
  if (typeof raw.vsCodeServeWeb === "boolean") form.serveWeb = raw.vsCodeServeWeb;
  if (typeof raw.vsCodeTunnel === "boolean") form.tunnel = raw.vsCodeTunnel;
  if (typeof raw.smbShare === "boolean") form.smb = raw.smbShare;
  if (typeof raw.micPassthrough === "boolean") form.mic = raw.micPassthrough;
  if (typeof raw.claudePartialStreaming === "boolean") form.partialStreaming = raw.claudePartialStreaming;
  if (typeof raw.opencodeBackgroundWatcher === "boolean") form.opencodeBackgroundWatcher = raw.opencodeBackgroundWatcher;
  if (typeof raw.t3code === "boolean") form.t3code = raw.t3code;
  if (has("t3codeChannel")) {
    const ch = String(raw.t3codeChannel);
    if (ch === "stable" || ch === "nightly") form.t3codeChannel = ch;
  }
  if (typeof raw.t3codeLimitResume === "boolean") form.t3codeLimitResume = raw.t3codeLimitResume;
  if (typeof raw.vmAutoCheckpoints === "boolean") form.autoCheckpoints = raw.vmAutoCheckpoints;
  return form;
}

/**
 * Translate the webview form shape into the on-disk schema. Git identity reuses
 * the installer's interop keys (gitUserName/gitEmail/gitCredentialStore) so the
 * two sides share one file. Empty text/number fields are omitted (preserve the
 * stored value rather than blow it away with an accidental blank); booleans are
 * always written so a toggle-off persists.
 *
 * The password is NEVER persisted (it would be plaintext); it is passed at
 * reinstall time instead. agents/projects are intentionally NOT written here yet
 * — the settings-form chips aren't hydrated from live state until the Projects
 * batch, so persisting them now would clobber the real selection with the static
 * all-on defaults.
 */
function mapFromForm(form) {
  form = form || {};
  const out = {};
  const setStr = (k, v) => { if (v != null) { const s = String(v).trim(); if (s) out[k] = s; } };
  // Match the full set an <input type=number> can legitimately produce — the HTML
  // "valid floating-point number" grammar (optional sign, decimal, exponent) — so
  // "1e3"/"-4"/"+8" persist as the number they denote rather than as a raw string
  // under a key the installer treats as numeric. A non-numeric leftover (defensive;
  // a number input can't yield one) falls back to the trimmed string.
  const FLOAT_RE = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;
  const setNum = (k, v) => {
    if (v == null) return;
    const s = String(v).trim();
    if (!s) return;
    out[k] = FLOAT_RE.test(s) ? Number(s) : s;
  };
  const setBool = (k, v) => { if (typeof v === "boolean") out[k] = v; };
  setStr("gitUserName", form.gitName);
  setStr("gitEmail", form.gitEmail);
  setBool("gitCredentialStore", form.gitCred);
  setNum("vmMemoryGB", form.ram);
  setNum("vmDiskGB", form.disk);
  setNum("vmCpuCount", form.cpu);
  setStr("ubuntuRelease", form.ubuntu);
  setBool("vsCodeServeWeb", form.serveWeb);
  setBool("vsCodeTunnel", form.tunnel);
  setBool("smbShare", form.smb);
  setBool("claudePartialStreaming", form.partialStreaming);
  setBool("opencodeBackgroundWatcher", form.opencodeBackgroundWatcher);
  setBool("micPassthrough", form.mic);
  setBool("t3code", form.t3code);
  // Only persist a valid channel — an unknown/absent value must not clobber a
  // stored one (e.g. the host settings written by an older extension that never
  // sent the key).
  if (form.t3codeChannel === "stable" || form.t3codeChannel === "nightly") out.t3codeChannel = form.t3codeChannel;
  setBool("t3codeLimitResume", form.t3codeLimitResume);
  setBool("vmAutoCheckpoints", form.autoCheckpoints);
  return out;
}

/** Patch settings that are deliberately provisioning-only. Treat an absent
 *  key as the off default so saving an untouched form does not create a false
 *  reprovision warning. Returns user-facing feature names that changed. */
function patchReprovisionChanges(previous, next) {
  previous = previous || {};
  next = next || {};
  const changes = [];
  const sourceBuildChanged =
    (previous.t3codeLimitResume === true) !== (next.t3codeLimitResume === true);
  const sourceManagedT3Changed =
    next.t3codeLimitResume === true &&
    next.t3code === true &&
    (previous.t3code !== true ||
      (previous.t3codeChannel || "stable") !== (next.t3codeChannel || "stable"));
  if (sourceBuildChanged || sourceManagedT3Changed) {
    changes.push("patched T3 Code + Desktop build");
  }
  if (
    (previous.opencodeBackgroundWatcher === true) !==
    (next.opencodeBackgroundWatcher === true)
  ) {
    changes.push("OpenCode background watcher");
  }
  return changes;
}

// ── Automatic-checkpoint "applied" marker ───────────────────────────────────--
// `vmAutoCheckpoints` is the user's PREFERENCE; `vmAutoCheckpointsApplied` records the
// value we last CONFIRMED onto the live VM (the elevated script reports success through
// a result file). The two are separate because the panel's Hyper-V probe is
// permission-gated: when it can't read the VM's real policy, this marker is the only
// way to tell "already applied" from "never applied", and without it a VM created
// before this feature (policy on, no saved key) would never be offered the fix.
// Deliberately NOT part of mapToForm/mapFromForm — it is state, not a form field.

const APPLIED_KEY = "vmAutoCheckpointsApplied";

/** The last automatic-checkpoint value confirmed onto the VM: true, false, or null
 *  when nothing has ever been confirmed (or the key is absent/malformed). Pure. */
function readAppliedAutoCheckpoints(scriptsDir) {
  const raw = readRawSettings(scriptsDir);
  return typeof raw[APPLIED_KEY] === "boolean" ? raw[APPLIED_KEY] : null;
}

/** Record the value just confirmed onto the VM, merging over the existing file so no
 *  unmanaged key is lost. `null` clears the marker. Throws without a scripts dir. */
function saveAppliedAutoCheckpoints(scriptsDir, value) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const merged = { ...readRawSettings(scriptsDir) };
  if (value === null) delete merged[APPLIED_KEY];
  else merged[APPLIED_KEY] = value === true;
  writeRawSettings(scriptsDir, merged);
  return merged;
}

/** Read settings from disk in the webview form shape. */
function readSettings(scriptsDir) { return mapToForm(readRawSettings(scriptsDir)); }

/**
 * Merge the form into the on-disk settings, preserving every key we don't manage
 * (e.g. a future `installedCommit` update marker). Returns the merged object.
 * Throws if there is no scripts dir to write into.
 */
function saveSettings(scriptsDir, form) {
  if (!scriptsDir) throw new Error("No Construct scripts directory resolved");
  const merged = { ...readRawSettings(scriptsDir), ...mapFromForm(form) };
  writeRawSettings(scriptsDir, merged);
  return merged;
}

module.exports = {
  CONTAINER, MARKER, SETTINGS_FILE,
  localAppData, findScriptsDir, resolveScriptsDir,
  settingsPath, projectsDir, configDir,
  readRawSettings, writeRawSettings, mapToForm, mapFromForm, patchReprovisionChanges,
  readSettings, saveSettings, readProjectProfile,
  safeProfileName, listProjectProfiles, writeProjectProfile, deleteProjectProfile, writeProjectProfileIfAbsent,
  readSelectedProjects, hasPersistedSelection, saveSelectedProjects,
  readAppliedAutoCheckpoints, saveAppliedAutoCheckpoints,
};
