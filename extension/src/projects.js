"use strict";
// Project profile validation: the strict schema check, the coercive sanitizer and the
// canonical byte form. The VM's `construct project set` loads this file
// (bin/project-set.js, bin/construct), which is why it stays in extension/src: the
// VS Code extension itself does not use it.

// The agents an MCP server can be scoped to (schema `agents` enum).
const MCP_AGENTS = ["claude", "claude-code", "codex", "opencode"];

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

/** Sanitize one MCP entry (a stdio/http server object).
 *  Returns the cleaned entry or null to drop it. Pure. */
function sanitizeMcpEntry(m) {
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

// A profile's port forwards (`forwards`): what `construct expose <port> --label
// --to` would request, re-requested by provisioning (bin/project-forwards.sh).
// `https: true` asks for an HTTPS forward with the host's certificate
// (docs/https-forwards.md). It must say what the port speaks, `upstreamTls`
// (--upstream-tls / --upstream-plain): the host never guesses. `name` is
// `--name`; provisioning requests it with --https-if-available.
const FORWARD_KEYS = ["port", "label", "to", "https", "name", "upstreamTls"];
const FORWARD_TARGETS = ["client", "host"];
// One DNS label, as `construct expose --name` takes it.
const FORWARD_NAME = /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/;
function isForwardPort(v) { return typeof v === "number" && Number.isInteger(v) && v >= 1 && v <= 65535; }
function isForwardName(v) { return typeof v === "string" && FORWARD_NAME.test(v); }

/** Sanitize forwards[]: each entry needs a valid port; `to` defaults to "client"
 *  (a profile is shared, so it must not depend on one VM's default target), an
 *  unknown `to` drops the entry rather than turning a host forward into a client
 *  one, and a blank label is omitted. `https` counts only when it is `true`; it
 *  makes `to` "host" (the host terminates the TLS), and an entry that also says
 *  `to: "client"` or does not say what the port speaks (a boolean `upstreamTls`)
 *  is dropped rather than guessed. `name` is kept only on an HTTPS entry and only
 *  when it is one DNS label. A non-array -> []. Pure. */
function sanitizeForwards(forwards) {
  if (!Array.isArray(forwards)) return [];
  const out = [];
  for (const f of forwards) {
    if (!isPlainObject(f) || !isForwardPort(f.port)) continue;
    if ("to" in f && !FORWARD_TARGETS.includes(f.to)) continue;
    const https = f.https === true;
    if (https && (f.to === "client" || typeof f.upstreamTls !== "boolean")) continue;
    const entry = { port: f.port, to: https ? "host" : "to" in f ? f.to : "client" };
    if (https) {
      entry.https = true;
      entry.upstreamTls = f.upstreamTls;
      if (isForwardName(f.name)) entry.name = f.name;
    }
    if (nonEmptyString(f.label)) entry.label = f.label;
    out.push(entry);
  }
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
  const KNOWN = ["name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "forwards", "tests"];
  for (const k of Object.keys(obj)) {
    if (!KNOWN.includes(k)) errors.push(`unknown key "${k}"`);
  }
  if (!str(obj.name)) errors.push('"name" must be a non-empty string');
  else {
    const nm = String(obj.name);
    // Reject names containing path separators or traversal sequences — a name
    // must be a safe single-segment filename, never a path. This is the primary
    // defense against VM-to-host path traversal (Finding 8).
    if (/[/\\]/.test(nm) || nm === "." || nm === ".." || nm.includes("..")) {
      errors.push('"name" contains illegal path characters');
    }
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
      if (!isPlainObject(m)) { errors.push(`mcp[${i}] must be an object with "name" plus "command" (stdio) or "url" (http); see docs/projects.md`); return; }
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
  if ("forwards" in obj) {
    if (!Array.isArray(obj.forwards)) errors.push('"forwards" must be an array');
    else obj.forwards.forEach((f, i) => {
      if (!isPlainObject(f)) { errors.push(`forwards[${i}] must be an object with a "port"`); return; }
      for (const k of Object.keys(f)) { if (!FORWARD_KEYS.includes(k)) errors.push(`forwards[${i}] unknown key "${k}"`); }
      if (!isForwardPort(f.port)) errors.push(`forwards[${i}].port must be an integer from 1 to 65535`);
      if ("label" in f && typeof f.label !== "string") errors.push(`forwards[${i}].label must be a string`);
      if ("to" in f && !FORWARD_TARGETS.includes(f.to)) errors.push(`forwards[${i}].to must be "client" or "host"`);
      if ("https" in f && typeof f.https !== "boolean") errors.push(`forwards[${i}].https must be true or false`);
      if ("name" in f && !isForwardName(f.name)) errors.push(`forwards[${i}].name must be one DNS label: up to 63 lowercase letters, digits and hyphens, starting and ending with a letter or digit`);
      if ("upstreamTls" in f && typeof f.upstreamTls !== "boolean") errors.push(`forwards[${i}].upstreamTls must be true or false`);
      if (f.https === true && f.to === "client") errors.push(`forwards[${i}].to must be "host" or left out: the host serves an HTTPS forward`);
      if (f.https === true && !("upstreamTls" in f)) errors.push(`forwards[${i}].upstreamTls is required with "https": true: false when the port serves plain HTTP, true when it speaks TLS itself`);
      if ("name" in f && f.https !== true) errors.push(`forwards[${i}].name needs "https": true`);
      if ("upstreamTls" in f && f.https !== true) errors.push(`forwards[${i}].upstreamTls needs "https": true`);
    });
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
  };
  // Only when non-empty: every profile written before this key existed keeps its
  // exact canonical bytes, so no engine rewrites (or an older one rejects) it.
  const forwards = sanitizeForwards(o.forwards);
  if (forwards.length) out.forwards = forwards;
  out.tests = isPlainObject(o.tests) ? o.tests : {};
  return out;
}

module.exports = {
  MCP_AGENTS, RESERVED_PROFILE_NAMES,
  sanitizeRepos, sanitizeSdks, sanitizeMcp, sanitizeMcpEntry, sanitizeStringArray, sanitizeForwards, sanitizeProfile,
  isReservedProfileName, validateProfile, canonicalProfileJson,
};
