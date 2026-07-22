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
 * Resolve the scripts dir. An explicit override (the `construct.scriptsDir`
 * setting) wins when it points at a real directory — the user knows where their
 * checkout is; otherwise auto-detect the newest install. Returns a path or null.
 * `opts`: { scriptsDir?, localAppData?, env? }.
 */
function resolveScriptsDir(opts = {}) {
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

// A project name must be a single, safe filename: no path separator, no "..", and
// nothing that would let a VM-supplied or webview-supplied name escape the projects
// dir. Mirrors the guard in readProjectProfile. Pure; returns the trimmed name or "".
function safeProfileName(name) {
  const s = String(name == null ? "" : name).trim();
  if (!s || /[\/\\]/.test(s) || s.includes("..")) return "";
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
  if (has("ubuntuRelease")) form.ubuntu = String(raw.ubuntuRelease);
  if (typeof raw.vsCodeServeWeb === "boolean") form.serveWeb = raw.vsCodeServeWeb;
  if (typeof raw.vsCodeTunnel === "boolean") form.tunnel = raw.vsCodeTunnel;
  if (typeof raw.smbShare === "boolean") form.smb = raw.smbShare;
  if (typeof raw.micPassthrough === "boolean") form.mic = raw.micPassthrough;
  if (typeof raw.claudePartialStreaming === "boolean") form.partialStreaming = raw.claudePartialStreaming;
  if (typeof raw.t3code === "boolean") form.t3code = raw.t3code;
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
  setStr("ubuntuRelease", form.ubuntu);
  setBool("vsCodeServeWeb", form.serveWeb);
  setBool("vsCodeTunnel", form.tunnel);
  setBool("smbShare", form.smb);
  setBool("claudePartialStreaming", form.partialStreaming);
  setBool("micPassthrough", form.mic);
  setBool("t3code", form.t3code);
  return out;
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
  readRawSettings, writeRawSettings, mapToForm, mapFromForm,
  readSettings, saveSettings, readProjectProfile,
  safeProfileName, listProjectProfiles, writeProjectProfile,
  readSelectedProjects, saveSelectedProjects,
};
