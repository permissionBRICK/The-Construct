"use strict";
// Project profiles for the control panel: discover the repos checked out on the VM
// and merge them into local profile JSONs (import), reconcile which profiles are
// selected (select), and validate/sanitize an edited profile before it is written
// to disk (edit).
//
// WHERE THINGS LIVE — the per-project profiles are the JSON files the installer's
// selector and the VM's generate-runtime-config.sh both read, under
// <scriptsDir>/projects/<name>.json. Reading/writing/listing them is host.js's job
// (pure fs/path, no vscode); THIS module owns the pure transforms around them:
// the remote-scan script + its parser, the merge, and the profile validator. All
// of it is pure and unit-tested; the extension layer (extension.js) does the SSH
// round-trip, the modal round-trip, and the toasts.
//
// DISCOVERY MECHANISM — importProjects scans over SSH directly rather than driving
// Provision-AgentVM.ps1 -Action export -ScanReposOnly. That PowerShell path first
// uploads a fresh repo archive to the VM (a heavy, VM-mutating step) and needs the
// host scripts + a console; here we only READ the VM, over the ssh runner the panel
// already uses. The scan mirrors bin/scan-repos.sh's core (a `WORKSPACE_ROOT/*/`
// walk reading each repo's origin URL) but emits TAB-separated lines instead of JSON
// so it needs no `jq` on the VM — the same no-dependency approach as src/probe.js.
// It captures name + origin URL only (not the dirty/unpushed counts scan-repos.sh
// gathers for the reinstall data-loss gate — those aren't relevant to profiles).

const WORKSPACE_ROOT = "/root/repos"; // WORKSPACE_ROOT in bin/provision.sh

// The set of MCP legacy-enum values the schema allows as bare strings.
const MCP_LEGACY_ENUM = ["filesystem", "browser", "github"];
// The agents an MCP server can be scoped to (schema `agents` enum).
const MCP_AGENTS = ["claude", "claude-code", "codex", "opencode"];

/**
 * The remote bash script that lists the git repos checked out under WORKSPACE_ROOT
 * and prints, per repo, three TAB-separated fields: the checkout directory name,
 * its `origin` remote URL (empty if none), and the current branch. One repo per
 * line; a trailing "END" sentinel line proves the script ran to completion (so a
 * truncated/partial capture, e.g. a killed ssh, is detectable and not mistaken for
 * "no repos"). No jq needed. `set -u`; every git call is guarded so one bad repo
 * can't abort the walk. Pure (returns the script text; ssh.runRemoteScript base64s
 * it for transport). `root` overrides WORKSPACE_ROOT (tests / non-default layouts).
 */
function buildScanScript(root) {
  const r = root || WORKSPACE_ROOT;
  // The root is a fixed, trusted constant (or a test override), never user data, so
  // it is embedded directly — unlike the add-project clone, which base64s a
  // user-supplied URL. Kept single-quoted so an unusual (test) path is still literal.
  return [
    "set -u",
    "root='" + String(r).replace(/'/g, "'\\''") + "'",
    'if [ -d "$root" ]; then',
    '  for repo in "$root"/*/; do',
    '    [ -d "${repo}.git" ] || continue',
    '    name=$(basename "$repo")',
    '    url=$(git -C "$repo" remote get-url origin 2>/dev/null || true)',
    "    branch=$(git -C \"$repo\" rev-parse --abbrev-ref HEAD 2>/dev/null || true)",
    // printf with an explicit format string: the fields are DATA to printf's %s, so
    // a repo dir/URL/branch containing a % or a backslash can't be read as a format
    // directive or an escape. A tab/newline inside a field would corrupt the line
    // grid, but git ref/dir names can't contain either, so this is safe in practice.
    '    printf \'%s\\t%s\\t%s\\n\' "$name" "$url" "$branch"',
    "  done",
    "fi",
    "printf 'END\\n'",
  ].join("\n");
}

/**
 * Parse the scan script's stdout into an array of discovered repos
 * `{ name, url, branch }`. Returns null when the "END" sentinel is absent (the
 * script didn't run to completion — a partial/failed capture we must NOT treat as
 * an authoritative "empty workspace"). A present sentinel with no repo lines is a
 * real, trustworthy empty result -> []. Blank/short lines are skipped. Pure.
 */
function parseScan(stdout) {
  const lines = String(stdout == null ? "" : stdout).split("\n");
  let sawEnd = false;
  const repos = [];
  for (const line of lines) {
    if (line === "END") { sawEnd = true; continue; }
    const parts = line.split("\t");
    if (parts.length < 3) continue; // header noise / blank / a login banner line
    const name = parts[0].trim();
    if (!name) continue;
    repos.push({ name, url: parts[1].trim(), branch: parts[2].trim() });
  }
  return sawEnd ? repos : null;
}

/**
 * A minimal profile for a discovered repo, shaped exactly like the one
 * bin/export-config.sh generates for a loose repo: name + a single repo (url +
 * directory=name) and empty everything else, so re-provisioning re-clones it. The
 * checkout dir name is the profile name (repos live at WORKSPACE_ROOT/<name>). Pure.
 */
function buildDiscoveredProfile(repo) {
  return {
    name: repo.name,
    repos: [{ url: repo.url, directory: repo.name }],
    sdks: {},
    mcp: [],
    hostPackages: [],
    provisionCommands: [],
    tests: {},
  };
}

/** The set of repo URLs already covered by an existing profile's repos[].url, so a
 *  discovered repo whose remote is already in a profile isn't imported twice.
 *  `profiles` is a map name->profile object (as read from disk). Pure. Returns a Set. */
function coveredUrls(profiles) {
  const set = new Set();
  for (const name of Object.keys(profiles || {})) {
    const p = profiles[name];
    const repos = p && Array.isArray(p.repos) ? p.repos : [];
    for (const rp of repos) {
      if (rp && typeof rp.url === "string" && rp.url.trim()) set.add(rp.url.trim());
    }
  }
  return set;
}

/**
 * Decide which discovered repos become NEW profiles. Given the scan result and the
 * currently-present profiles (name->profile), return the list of profiles to write:
 * one per discovered repo that (a) has an origin URL, (b) whose URL isn't already
 * covered by an existing profile, and (c) whose name doesn't collide with an
 * existing profile file (never overwrite — same rule as the export merge). Repos
 * with no origin remote are reported separately as `skipped` (they can't be
 * re-cloned, so a profile would be useless). Duplicate URLs within one scan import
 * only once. Pure. Returns { toWrite:[{name,profile}], skipped:[name], covered:[name] }.
 */
function planImport(scan, existingProfiles) {
  const repos = Array.isArray(scan) ? scan : [];
  const existing = existingProfiles || {};
  const covered = coveredUrls(existing);
  const toWrite = [];
  const skipped = [];
  const coveredNames = [];
  const seenUrls = new Set();
  const plannedNames = new Set(Object.keys(existing));
  for (const repo of repos) {
    if (!repo || !repo.name) continue;
    const url = (repo.url || "").trim();
    if (!url) { skipped.push(repo.name); continue; }        // no remote -> can't re-clone
    if (covered.has(url) || seenUrls.has(url)) { coveredNames.push(repo.name); continue; }
    if (plannedNames.has(repo.name)) { coveredNames.push(repo.name); continue; } // name taken -> keep existing
    seenUrls.add(url);
    plannedNames.add(repo.name);
    toWrite.push({ name: repo.name, profile: buildDiscoveredProfile(repo) });
  }
  return { toWrite, skipped, covered: coveredNames };
}

// ── Selection reconciliation ─────────────────────────────────────────────────
// Which profiles are "selected" drives the chips and the forward-compat `projects`
// key. The available profiles come from disk (host.listProjectProfiles); the stored
// selection is the persisted `projects` array. These reconcile the two.

/**
 * The chip list for the panel: one `{ name, selected }` per AVAILABLE profile,
 * marked selected iff it is in the stored selection. `available` is the profile
 * name list from disk; `selected` is the stored selection array. Pure.
 *
 * Honesty about "default": the VM always provisions with at least `default`
 * (generate-runtime-config.sh falls back to it), so when the stored selection is
 * EMPTY — the natural pre-Projects-batch state — everything reads as unselected,
 * which is a fair reflection of "no explicit selection saved yet" rather than a
 * false claim. We do NOT auto-tick default here; the caller decides how to seed the
 * first selection (extension.js seeds it from the live VM PROJECTS= list).
 */
function toChips(available, selected) {
  const avail = Array.isArray(available) ? available : [];
  const sel = new Set((Array.isArray(selected) ? selected : []).map(String));
  return avail.map((name) => ({ name: String(name), selected: sel.has(String(name)) }));
}

/**
 * Reconcile a REQUESTED selection (e.g. what the user ticked in the panel) against
 * the profiles that actually exist on disk: keep only requested names that are
 * available, de-duplicated, in the available-list order (stable). A request for a
 * profile that doesn't exist is dropped rather than persisted (so a stale/foreign
 * name can't linger in the settings file). Pure. Returns a clean name array.
 */
function reconcileSelection(requested, available) {
  const avail = Array.isArray(available) ? available.map(String) : [];
  const req = new Set((Array.isArray(requested) ? requested : []).map(String));
  return avail.filter((name) => req.has(name));
}

// ── Profile validation / sanitization (edit) ─────────────────────────────────
// A profile edited in the panel arrives as arbitrary JSON over postMessage. Before
// it is written to <name>.json it must be coerced to the schema
// (projects/project.schema.json): drop unknown keys, enforce types, and reject
// outright anything that can't be made a valid profile. The result is what
// generate-runtime-config.sh / the installer will consume, so it must be clean.

function isPlainObject(v) { return !!v && typeof v === "object" && !Array.isArray(v); }
function nonEmptyString(v) { return typeof v === "string" && v.trim() !== "" ? v : null; }

/** Sanitize the repos[] array: each entry needs a non-empty url; an optional
 *  non-empty directory is kept, anything else dropped. A non-array -> []. Pure. */
function sanitizeRepos(repos) {
  if (!Array.isArray(repos)) return [];
  const out = [];
  for (const r of repos) {
    if (!isPlainObject(r)) continue;
    const url = nonEmptyString(r.url);
    if (!url) continue; // url is required per-repo
    const entry = { url };
    const dir = nonEmptyString(r.directory);
    if (dir) entry.directory = dir;
    out.push(entry);
  }
  return out;
}

/** Sanitize the sdks object: keys map to a non-empty string OR an array of
 *  non-empty strings (schema `sdks`). Empty/foreign values dropped. Pure. */
function sanitizeSdks(sdks) {
  if (!isPlainObject(sdks)) return {};
  const out = {};
  for (const k of Object.keys(sdks)) {
    const v = sdks[k];
    if (typeof v === "string") { if (v.trim() !== "") out[k] = v; continue; }
    if (Array.isArray(v)) {
      const arr = v.filter((x) => typeof x === "string" && x.trim() !== "");
      if (arr.length) out[k] = arr;
    }
  }
  return out;
}

/** Sanitize one MCP entry (string legacy-enum, or a stdio/http server object).
 *  Returns the cleaned entry or null to drop it. Pure. */
function sanitizeMcpEntry(m) {
  if (typeof m === "string") return MCP_LEGACY_ENUM.includes(m) ? m : null;
  if (!isPlainObject(m)) return null;
  const name = nonEmptyString(m.name);
  if (!name) return null;
  // Type: explicit "stdio"/"http", else inferred from which of command/url is present.
  let type = m.type === "stdio" || m.type === "http" ? m.type : null;
  if (!type) { if (nonEmptyString(m.command)) type = "stdio"; else if (nonEmptyString(m.url)) type = "http"; }
  const out = { name, type };
  const scopeAgents = (src) => {
    if (!Array.isArray(src)) return null;
    const arr = src.filter((a) => MCP_AGENTS.includes(a));
    return arr.length ? arr : null;
  };
  const strMap = (src) => {
    if (!isPlainObject(src)) return null;
    const o = {};
    for (const k of Object.keys(src)) { if (typeof src[k] === "string") o[k] = src[k]; }
    return Object.keys(o).length ? o : null;
  };
  if (type === "stdio") {
    const command = nonEmptyString(m.command);
    if (!command) return null;
    out.command = command;
    if (Array.isArray(m.args)) {
      const args = m.args.filter((a) => typeof a === "string");
      if (args.length) out.args = args;
    }
    const env = strMap(m.env); if (env) out.env = env;
  } else if (type === "http") {
    const url = nonEmptyString(m.url);
    if (!url) return null;
    out.url = url;
    const headers = strMap(m.headers); if (headers) out.headers = headers;
    const bt = nonEmptyString(m.bearerTokenEnvVar); if (bt) out.bearerTokenEnvVar = bt;
  } else {
    return null; // couldn't determine a valid server type
  }
  const agents = scopeAgents(m.agents); if (agents) out.agents = agents;
  if (typeof m.enabled === "boolean") out.enabled = m.enabled;
  return out;
}

function sanitizeMcp(mcp) {
  if (!Array.isArray(mcp)) return [];
  const out = [];
  for (const m of mcp) { const e = sanitizeMcpEntry(m); if (e) out.push(e); }
  return out;
}

/** Sanitize a string[] (hostPackages / provisionCommands): keep non-empty strings,
 *  preserving order (provisionCommands run in array order). A non-array -> []. Pure. */
function sanitizeStringArray(arr) {
  if (!Array.isArray(arr)) return [];
  return arr.filter((s) => typeof s === "string" && s.trim() !== "");
}

// ── Reserved names / strict validation / canonical serialization ─────────────
// (config-sync v2, docs/config-sync.md) Three shared primitives used by the panel,
// the host sync engine and the VM-side `construct project set` helper. They live
// here — next to sanitizeProfile, whose field order they inherit — so every writer
// agrees on one canonical byte form and one definition of "valid".

// The names that are never user profiles: `default` is the shipped read-only seed
// and `project.schema` is the schema file (docs/config-sync.md §4). Compared
// case-insensitively on the trimmed base name (no .json extension).
const RESERVED_PROFILE_NAMES = ["default", "project.schema"];
function isReservedProfileName(name) {
  const s = String(name == null ? "" : name).trim().toLowerCase();
  return RESERVED_PROFILE_NAMES.includes(s);
}

/**
 * STRICT validator mirroring projects/project.schema.json — the detect-side twin
 * of the coercive sanitizeProfile (which silently repairs). Used by the sync
 * engine's validation gates (§6 steps 2/5) and the VM helper, where an invalid
 * file must be SKIPPED WITH A REPORT, never silently fixed. Returns
 * { ok, errors: [string] }. Two deliberate deviations from the letter of the
 * schema, both stricter: minLength-1 strings must be non-blank after trim (a
 * whitespace-only url is garbage that sanitize would silently drop), and when the
 * `name` argument is non-empty the object's own `name` must equal it (a
 * filename/name mismatch would make merges ambiguous). Pure.
 */
function validateProfile(name, obj) {
  const errors = [];
  const str = (v) => typeof v === "string" && v.trim() !== "";
  if (!isPlainObject(obj)) return { ok: false, errors: ["profile is not a JSON object"] };
  const KNOWN = ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "tests"];
  for (const k of Object.keys(obj)) {
    if (!KNOWN.includes(k)) errors.push(`unknown key "${k}"`);
  }
  if (!str(obj.name)) errors.push('"name" must be a non-empty string');
  else {
    const want = String(name == null ? "" : name).trim();
    if (want && obj.name !== want) errors.push(`"name" is "${obj.name}" but the profile file is "${want}"`);
  }
  if ("repos" in obj) {
    if (!Array.isArray(obj.repos)) errors.push('"repos" must be an array');
    else obj.repos.forEach((r, i) => {
      if (!isPlainObject(r)) { errors.push(`repos[${i}] must be an object`); return; }
      for (const k of Object.keys(r)) { if (k !== "url" && k !== "directory") errors.push(`repos[${i}] unknown key "${k}"`); }
      if (!str(r.url)) errors.push(`repos[${i}].url must be a non-empty string`);
      if ("directory" in r && !str(r.directory)) errors.push(`repos[${i}].directory must be a non-empty string`);
    });
  }
  if ("sdks" in obj) {
    if (!isPlainObject(obj.sdks)) errors.push('"sdks" must be an object');
    else for (const k of Object.keys(obj.sdks)) {
      const v = obj.sdks[k];
      const okStr = str(v);
      const okArr = Array.isArray(v) && v.every(str);
      if (!okStr && !okArr) errors.push(`sdks.${k} must be a non-empty string or an array of non-empty strings`);
    }
  }
  if ("mcp" in obj) {
    if (!Array.isArray(obj.mcp)) errors.push('"mcp" must be an array');
    else obj.mcp.forEach((m, i) => {
      if (typeof m === "string") {
        if (!MCP_LEGACY_ENUM.includes(m)) errors.push(`mcp[${i}] "${m}" is not one of ${MCP_LEGACY_ENUM.join("/")}`);
        return;
      }
      if (!isPlainObject(m)) { errors.push(`mcp[${i}] must be a string or an object`); return; }
      if (!str(m.name)) errors.push(`mcp[${i}].name must be a non-empty string`);
      // Branch selection mirrors sanitizeMcpEntry: explicit type wins, else infer.
      let type = m.type === "stdio" || m.type === "http" ? m.type : null;
      if (m.type != null && !type) { errors.push(`mcp[${i}].type must be "stdio" or "http"`); return; }
      if (!type) { if (str(m.command)) type = "stdio"; else if (str(m.url)) type = "http"; }
      if (!type) { errors.push(`mcp[${i}] needs a "command" (stdio) or "url" (http)`); return; }
      const common = ["name", "type", "agents", "enabled"];
      const allowed = type === "stdio" ? common.concat(["command", "args", "env"]) : common.concat(["url", "headers", "bearerTokenEnvVar"]);
      for (const k of Object.keys(m)) { if (!allowed.includes(k)) errors.push(`mcp[${i}] unknown key "${k}" for a ${type} server`); }
      const strMapOk = (v) => isPlainObject(v) && Object.keys(v).every((k) => typeof v[k] === "string");
      if (type === "stdio") {
        if (!str(m.command)) errors.push(`mcp[${i}].command must be a non-empty string`);
        if ("args" in m && !(Array.isArray(m.args) && m.args.every((a) => typeof a === "string"))) errors.push(`mcp[${i}].args must be an array of strings`);
        if ("env" in m && !strMapOk(m.env)) errors.push(`mcp[${i}].env must be an object of string values`);
      } else {
        if (!str(m.url)) errors.push(`mcp[${i}].url must be a non-empty string`);
        if ("headers" in m && !strMapOk(m.headers)) errors.push(`mcp[${i}].headers must be an object of string values`);
        if ("bearerTokenEnvVar" in m && !str(m.bearerTokenEnvVar)) errors.push(`mcp[${i}].bearerTokenEnvVar must be a non-empty string`);
      }
      if ("agents" in m) {
        const ok = Array.isArray(m.agents) && m.agents.length > 0 && m.agents.every((a) => MCP_AGENTS.includes(a));
        if (!ok) errors.push(`mcp[${i}].agents must be a non-empty array from ${MCP_AGENTS.join("/")}`);
      }
      if ("enabled" in m && typeof m.enabled !== "boolean") errors.push(`mcp[${i}].enabled must be a boolean`);
    });
  }
  for (const key of ["hostPackages", "provisionCommands"]) {
    if (key in obj) {
      const ok = Array.isArray(obj[key]) && obj[key].every(str);
      if (!ok) errors.push(`"${key}" must be an array of non-empty strings`);
    }
  }
  if ("tests" in obj && !isPlainObject(obj.tests)) errors.push('"tests" must be an object');
  return { ok: errors.length === 0, errors };
}

/**
 * THE canonical byte form of a profile (docs/config-sync.md §6): sanitizeProfile's
 * fixed key order, 2-space indent, one array element per line, trailing newline,
 * BOM-less. Every writer — the panel, the sync engine's write-back, the import
 * path, the VM `construct project set` helper — must emit exactly this, so git
 * diffs stay semantic and the PowerShell serializer can byte-match it. Returns
 * null when the name is empty (sanitizeProfile's one rejection). Pure.
 */
function canonicalProfileJson(name, obj) {
  const clean = sanitizeProfile(name, obj);
  return clean == null ? null : JSON.stringify(clean, null, 2) + "\n";
}

/**
 * Coerce an arbitrary object into a schema-valid project profile. `name` is the
 * authoritative profile name (the filename target), taken as an argument rather
 * than from the object so the on-disk name and the profile.name always agree — and
 * so a hostile object can't rename the file. Returns a clean profile object, or
 * null if `name` is empty (the one schema-required field). Unknown top-level keys
 * are dropped (schema is additionalProperties:false). `tests` is passed through as
 * an opaque object (the schema leaves it open). Pure.
 */
function sanitizeProfile(name, obj) {
  const nm = nonEmptyString(name == null ? "" : String(name).trim());
  if (!nm) return null;
  const o = isPlainObject(obj) ? obj : {};
  const out = {
    name: nm.trim(),
    repos: sanitizeRepos(o.repos),
    sdks: sanitizeSdks(o.sdks),
    mcp: sanitizeMcp(o.mcp),
    hostPackages: sanitizeStringArray(o.hostPackages),
    provisionCommands: sanitizeStringArray(o.provisionCommands),
    tests: isPlainObject(o.tests) ? o.tests : {},
  };
  return out;
}

module.exports = {
  WORKSPACE_ROOT, MCP_LEGACY_ENUM, MCP_AGENTS, RESERVED_PROFILE_NAMES,
  buildScanScript, parseScan,
  buildDiscoveredProfile, coveredUrls, planImport,
  toChips, reconcileSelection,
  sanitizeRepos, sanitizeSdks, sanitizeMcp, sanitizeMcpEntry, sanitizeStringArray, sanitizeProfile,
  isReservedProfileName, validateProfile, canonicalProfileJson,
};
