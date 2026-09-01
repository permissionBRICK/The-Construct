"use strict";
// The CLIENT-SIDE INSTANCE REGISTRY — %LOCALAPPDATA%\The-Construct\instances.json.
//
// A Construct "instance" is one named VM plus everything the client needs to reach
// and manage it: the hypervisor backend, the VM name, the SSH endpoint, the
// ~/.ssh/config alias, the key file name, the config-sync branch, and (optionally)
// which scripts dir drives it. `agent-vm` is the IMPLICIT DEFAULT: when this file is
// missing, unreadable, or has no matching entry, the default instance is SYNTHESIZED
// from today's literals and NOTHING is written — an existing single-VM install never
// notices this module exists.
//
// This module is deliberately PURE fs/path/JSON (no `vscode`, no `child_process`), so
// it unit-tests against a fake %LOCALAPPDATA% tree exactly like host.js does. It never
// throws on a bad registry file: problems are COLLECTED and returned to the caller
// (which can toast them) while the default instance stands in.
//
// ── Public API ───────────────────────────────────────────────────────────────
//   CONTAINER, INSTANCES_FILE, DEFAULT_INSTANCE_NAME, DEFAULT_INSTANCE, NAME_RE
//   isValidName(name)                     -> bool          (^[a-z0-9][a-z0-9-]{0,39}$)
//   identityProblems(instance, raw?)       -> string[]      (format rules; [] = usable)
//   instancesPath(env)                    -> abs path | null
//   deriveDefaults(name, raw)             -> normalized instance object
//   parseRegistry(text)                   -> { registry, problems }
//   load(opts)                            -> Registry  {instances, byName, defaultInstance,
//                                                       problems, path, synthesized}
//   list(registry)                        -> instance[] (default first, then a-z)
//   resolve(registry, name)               -> instance     (unknown name -> default)
//   resolveActive({registry, setting, workspaceValue}) -> { instance, name, source }
//   isDefaultInstance(instance)           -> bool          (argv/behaviour gate)
//   toSshCfg(instance)                    -> ssh.js cfg   ({vmHost,hostAlias,keyName,sshPort})
//   save(path, registry)                  -> writes atomically (tmp + rename)
//   addInstance / updateInstance / removeInstance / setDefaultInstance
//
// ── The normalized instance object (the shape passed between JS modules) ─────
//   { name, backend, vmName, vmHost, sshPort, hostAlias, keyName, configBranch,
//     scriptsDir, service, owner }
// `vmHost` is the registry's `sshHost` (the JS side calls it vmHost because that is
// what ssh.js/probe.js already call it).

const fs = require("fs");
const path = require("path");
const net = require("net");   // net.isIP — a real IPv6 parser for the endpoint rule
// The config-sync engine owns the branch-name rule (isValidVmBranch), and an
// instance's configBranch IS that branch — so it is validated against the one
// authority rather than a second copy of the rule. One-way only: configsync.js must
// never require this module back.
const configsync = require("./configsync");

const CONTAINER = "The-Construct";          // %LOCALAPPDATA%\The-Construct
const INSTANCES_FILE = "instances.json";
const SCHEMA_VERSION = 1;

const DEFAULT_INSTANCE_NAME = "agent-vm";
const BACKENDS = ["hyperv-local", "hyperv-remote"];
const DEFAULT_SSH_PORT = 22;

/** Instance names are used verbatim in file names, ssh aliases and git refs, so they
 *  are restricted to a lowercase, slug-safe alphabet. */
const NAME_RE = /^[a-z0-9][a-z0-9-]{0,39}$/;

/** Today's literals — the instance an existing install implicitly runs. Frozen so a
 *  consumer can never mutate the fallback out from under another one. */
const DEFAULT_INSTANCE = Object.freeze({
  name: DEFAULT_INSTANCE_NAME,
  backend: "hyperv-local",
  vmName: "Agent-VM",
  vmHost: "agent-vm.mshome.net",
  sshPort: DEFAULT_SSH_PORT,
  hostAlias: "agent-vm",
  keyName: "agent_vm_ed25519",
  configBranch: "vm",
  scriptsDir: null,
  service: null,
  owner: null,
});

function isValidName(name) {
  return typeof name === "string" && NAME_RE.test(name);
}

/** The base under which the registry lives. Mirrors host.js localAppData() exactly so
 *  instances.json lands next to the existing config\ dir. */
function localAppData(env) {
  env = env || process.env;
  return env.LOCALAPPDATA || env.TEMP || "";
}

/** Absolute path of the registry file, or null when no base dir resolves. Pure. */
function instancesPath(env) {
  const base = localAppData(env);
  return base ? path.join(base, CONTAINER, INSTANCES_FILE) : null;
}

// ── Derivation ───────────────────────────────────────────────────────────────

/** A positive integer TCP port, or null when the value isn't one. A bare digit STRING
 *  is accepted (hand-edited files write one routinely); the PowerShell reader accepts
 *  exactly the same two shapes. */
function coercePort(v) {
  if (typeof v === "number" && Number.isInteger(v) && v > 0 && v <= 65535) return v;
  if (typeof v === "string" && /^\d{1,5}$/.test(v.trim())) {
    const n = Number(v.trim());
    if (n > 0 && n <= 65535) return n;
  }
  return null;
}

/**
 * A schema STRING field: only a real JSON string counts. A number/bool/object under a
 * string key is a malformed file, not a value to stringify — the PowerShell reader
 * (lib/AgentVm.Instances.ps1) applies the identical rule, and the two must normalize a
 * bad file to the same instance and the same problem list.
 */
function str(v) { return typeof v === "string" && v.trim() ? v.trim() : null; }

/** True when a value is present but is NOT a usable string — i.e. worth reporting. */
function badString(v) { return v != null && v !== "" && typeof v !== "string"; }

/** The string fields of an instance entry, for the type check. `sshPort` is handled
 *  separately (it is an int in the schema). */
const STRING_FIELDS = ["backend", "vmName", "sshHost", "vmHost", "hostAlias", "keyName", "configBranch", "scriptsDir", "owner"];

// ── Identity-field FORMAT rules ──────────────────────────────────────────────
// Type-checking a field ("it is a string") is not enough for the ones that end up in
// a PowerShell command line, an ssh argv, a key-file path or a git ref: `-x;
// Start-Process calc; #` is a perfectly good JSON string. These rules constrain the
// SHAPE of every identity field, and an entry that breaks one is SKIPPED with a
// problem rather than partially used — half an identity would silently target some
// OTHER machine. Every DERIVED value satisfies them (instance names are already
// `^[a-z0-9][a-z0-9-]{0,39}$`), so only a hand-written entry can trip these.
// lib/AgentVm.Instances.ps1 applies the identical rules — change both together.

/** A DNS host name / FQDN (also matches a dotted IPv4 literal). */
const HOSTNAME_RE = /^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/;
/** The SHAPE an IPv6 literal must have before it is parsed: hex, ':' and '.' only, so
 *  no zone id (`%eth0`) and no brackets — ssh takes the bare address in argv, and both
 *  would have to survive a PowerShell command line too. It is also where the two
 *  readers' parsers are made to agree: Node's net.isIP accepts `fe80::1%eth0` and
 *  .NET's IPAddress.TryParse accepts `[::1]`, so neither ever reaches a parser. */
const IPV6_SHAPE_RE = /^[0-9A-Fa-f:.]{2,45}$/;
/** A strict dotted quad — the only IPv4 tail an IPv6-mapped address may carry.
 *  .NET's parser has historically been lenient about leading zeros where Node's is
 *  not, so the shape is pinned here rather than left to either. */
const IPV4_STRICT_RE = /^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$/;
/** An ssh alias / key file name: one path-free, shell-free token. */
const SAFE_TOKEN_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
/** Windows device names. They are NOT ordinary files at any path, so `~\.ssh\CON`
 *  can never be created — and the reservation applies to the stem before the first
 *  dot, so `CON.txt` is the same device. Case-insensitive. */
const WINDOWS_DEVICE_NAMES = new Set([
  "con", "prn", "aux", "nul",
  "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
  "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
]);
/** A single DNS label — the Hyper-V VM name doubles as the guest hostname, so
 *  Auto-Install.ps1 enforces the same shape (case-insensitively). */
const DNS_LABEL_RE = /^[A-Za-z0-9][A-Za-z0-9-]{0,62}$/;

/**
 * A real IPv6 literal — shape-filtered (above), then handed to an actual parser
 * (`net.isIP`), because a character-class regex accepts nonsense like `::::`,
 * `1::2::3` or `1:2:3:4:5:6:7:8:9`. lib/AgentVm.Instances.ps1 applies the same shape
 * filter and then .NET's `IPAddress.TryParse` (+ an InterNetworkV6/ScopeId check), and
 * both test suites run the same accept/reject matrix.
 */
function isIpv6Literal(v) {
  if (typeof v !== "string" || !IPV6_SHAPE_RE.test(v) || v.indexOf(":") < 0) return false;
  if (v.indexOf(".") >= 0 && !IPV4_STRICT_RE.test(v.slice(v.lastIndexOf(":") + 1))) return false;
  return net.isIP(v) === 6;
}

function isHostEndpoint(v) {
  return typeof v === "string" && (HOSTNAME_RE.test(v) || isIpv6Literal(v));
}
function isSafeToken(v) {
  return typeof v === "string" && SAFE_TOKEN_RE.test(v) && !v.includes("..");
}

/**
 * A key file name — the SSH alias rule plus what Windows adds, because this value is
 * used as a FILE: `~\.ssh\<keyName>` is written (Provision-AgentVM.ps1) and read.
 *   • a trailing dot is stripped by Win32, so "agent_vm_ed25519." and
 *     "agent_vm_ed25519" are the SAME file — an entry spelled that way would quietly
 *     overwrite the default instance's key;
 *   • a reserved device stem (CON, NUL, COM1 … , with or without an extension) is not
 *     a creatable file at all, so provisioning would fail after the VM exists.
 * `hostAlias` deliberately keeps the plain token rule: an ssh_config Host alias is not
 * a path. lib/AgentVm.Instances.ps1 applies the identical rule.
 */
function isKeyFileName(v) {
  if (!isSafeToken(v)) return false;
  if (v.endsWith(".")) return false;
  return !WINDOWS_DEVICE_NAMES.has(v.split(".")[0].toLowerCase());
}
function isDnsLabel(v) { return typeof v === "string" && DNS_LABEL_RE.test(v); }

/**
 * The format problems of one NORMALIZED instance (i.e. after deriveDefaults), as
 * human-readable strings. Empty array = usable. Pure.
 *
 * `raw` (optional) is the entry as it was written in the file. It matters because the
 * host has TWO spellings: `deriveDefaults` prefers `sshHost`, so an entry like
 * `{ sshHost: "good.local", vmHost: "-x; calc" }` would otherwise normalize to a
 * perfectly valid endpoint and keep the hostile one on disk for whoever reads `vmHost`
 * next. EVERY supplied host field is checked, then the effective endpoint.
 */
function identityProblems(inst, raw) {
  const out = [];
  const q = (v) => JSON.stringify(v === undefined ? null : v);
  const add = (msg) => { if (out.indexOf(msg) < 0) out.push(msg); };
  if (raw && typeof raw === "object" && !Array.isArray(raw)) {
    for (const f of ["sshHost", "vmHost"]) {
      const v = str(raw[f]);
      if (v && !isHostEndpoint(v)) add('"' + f + '" ' + q(v) + " is not a host name or IP address");
    }
  }
  if (!isDnsLabel(inst.vmName)) {
    add('"vmName" ' + q(inst.vmName) + " is not a usable VM/host name (letters, digits and hyphens, starting alphanumeric, max 63)");
  }
  if (!isHostEndpoint(inst.vmHost)) {
    add('"sshHost" ' + q(inst.vmHost) + " is not a host name or IP address");
  }
  if (!isSafeToken(inst.hostAlias)) {
    add('"hostAlias" ' + q(inst.hostAlias) + " is not a usable ssh alias (letters, digits, '.', '_' and '-', max 64)");
  }
  if (!isKeyFileName(inst.keyName)) {
    add('"keyName" ' + q(inst.keyName) + " is not a usable key file name (letters, digits, '.', '_' and '-', max 64;" +
      " no trailing dot and not a reserved Windows device name)");
  }
  if (!configsync.isValidVmBranch(inst.configBranch)) {
    add('"configBranch" ' + q(inst.configBranch) + " is not a usable config-sync branch name");
  }
  return out;
}

/**
 * Fill in every field an entry omits, per the registry contract:
 *   default instance ("agent-vm") -> today's literals, verbatim;
 *   any other <name>             -> hostAlias "<name>"  (the BARE instance name: every
 *                                     shared PowerShell helper — Get-RemoteOpenLink,
 *                                     Close-VmVsCodeWindow, Invoke-ConstructVmSsh's
 *                                     alias fallback — derives the SSH alias as the
 *                                     first DNS label of the VM host, and
 *                                     Auto-Install.ps1 writes alias = lowercased VM
 *                                     name, so the registry must agree),
 *                                   keyName "construct_<name>_ed25519",
 *                                   configBranch "vm-<name>"  (NOT "vm/<name>": git
 *                                     cannot hold refs/heads/vm and refs/heads/vm/x
 *                                     at the same time),
 *                                   vmName "<name>",
 *                                   vmHost "<name>.mshome.net" (hyperv-local),
 *                                   sshPort 22, scriptsDir null.
 * A remote backend has no name convention for its host, so `sshHost` is required
 * there — an entry that omits it derives the mshome name and the caller's validation
 * reports the problem. Pure; `raw` is never mutated.
 */
function deriveDefaults(name, raw) {
  const r = (raw && typeof raw === "object" && !Array.isArray(raw)) ? raw : {};
  const isDefault = name === DEFAULT_INSTANCE_NAME;
  const backend = BACKENDS.indexOf(r.backend) >= 0 ? r.backend : "hyperv-local";
  return {
    name,
    backend,
    vmName: str(r.vmName) || (isDefault ? DEFAULT_INSTANCE.vmName : name),
    vmHost: str(r.sshHost) || str(r.vmHost) || (isDefault ? DEFAULT_INSTANCE.vmHost : name + ".mshome.net"),
    sshPort: coercePort(r.sshPort) || DEFAULT_SSH_PORT,
    hostAlias: str(r.hostAlias) || (isDefault ? DEFAULT_INSTANCE.hostAlias : name),
    keyName: str(r.keyName) || (isDefault ? DEFAULT_INSTANCE.keyName : "construct_" + name + "_ed25519"),
    configBranch: str(r.configBranch) || (isDefault ? DEFAULT_INSTANCE.configBranch : "vm-" + name),
    scriptsDir: str(r.scriptsDir),
    service: normalizeService(r.service),
    owner: str(r.owner),
  };
}

function normalizeService(s) {
  if (!s || typeof s !== "object" || Array.isArray(s)) return null;
  const url = str(s.url);
  if (!url) return null;
  return { url, auth: s.auth === "token" ? "token" : "negotiate" };
}

/**
 * Is this the DEFAULT instance in every respect that changes behaviour? True for a
 * missing/undefined instance too, so `isDefaultInstance(activeInstance())` is the one
 * gate the zero-change path hangs on: it must be true for a synthesized default AND
 * for a registry that spells the default out with today's values.
 *
 * `scriptsDir`, `service` and `owner` are deliberately NOT part of the comparison —
 * scriptsDir is handled by host.resolveScriptsDir (it can't reach the VM args), and
 * the other two carry no argv/SSH consequence for a local instance.
 */
function isDefaultInstance(inst) {
  if (!inst) return true;
  return inst.name === DEFAULT_INSTANCE.name &&
    inst.backend === DEFAULT_INSTANCE.backend &&
    inst.vmName === DEFAULT_INSTANCE.vmName &&
    inst.vmHost === DEFAULT_INSTANCE.vmHost &&
    Number(inst.sshPort) === DEFAULT_INSTANCE.sshPort &&
    inst.hostAlias === DEFAULT_INSTANCE.hostAlias &&
    inst.keyName === DEFAULT_INSTANCE.keyName &&
    inst.configBranch === DEFAULT_INSTANCE.configBranch;
}

/** The ssh.js cfg for an instance. `user` is left to ssh.DEFAULTS ("root") so this
 *  never re-states a value it doesn't own. A missing instance yields {} — i.e. the
 *  ssh.js defaults, which ARE the default instance. Pure. */
function toSshCfg(inst) {
  if (!inst) return {};
  return {
    vmHost: inst.vmHost,
    hostAlias: inst.hostAlias,
    keyName: inst.keyName,
    sshPort: inst.sshPort,
  };
}

// ── Parsing / validation ─────────────────────────────────────────────────────

/**
 * Parse registry TEXT into { registry, problems }. Never throws. `registry` always
 * contains at least the default instance; every problem found (bad JSON, wrong
 * version, invalid name, unknown backend, bad port, missing remote host) is reported
 * as a human-readable string so the extension can surface one toast and keep running.
 */
function parseRegistry(text) {
  const problems = [];
  const byName = {};
  let defaultInstance = DEFAULT_INSTANCE_NAME;
  let doc = null;
  const raw = String(text == null ? "" : text).replace(/^﻿/, "");
  if (raw.trim()) {
    let parsed, parsedOk = false;
    try { parsed = JSON.parse(raw); parsedOk = true; } catch (e) {
      problems.push("instances.json is not valid JSON (" + (e && e.message ? e.message : e) + ")");
    }
    // EVERY non-object top level is a malformed file — including the falsy scalars
    // (0, false, null, ""), which a truthiness guard would silently accept as "empty
    // registry" and leave the user with no problem to see.
    if (parsedOk) {
      if (parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)) doc = parsed;
      else problems.push("instances.json must contain a JSON object");
    }
  }
  if (doc) {
    // A foreign schema version is REFUSED, not partially read: a later version may
    // redefine what a field MEANS, and acting on a misread entry would target the
    // wrong machine. Report it and fall back to the byte-identical default.
    if (doc.version != null && doc.version !== SCHEMA_VERSION) {
      problems.push("instances.json has version " + JSON.stringify(doc.version) +
        "; this Construct only understands version " + SCHEMA_VERSION +
        " — ignoring the file and using the default instance (update Construct)");
      doc = null;
    }
  }
  if (doc) {
    const bag = (doc.instances && typeof doc.instances === "object" && !Array.isArray(doc.instances))
      ? doc.instances : null;
    if (doc.instances != null && !bag) problems.push('instances.json: "instances" must be an object');
    if (bag) {
      for (const name of Object.keys(bag)) {
        if (!isValidName(name)) {
          problems.push('instance name "' + name + '" is invalid (allowed: a-z, 0-9 and "-", ' +
            "starting with a letter or digit, max 40 chars) — skipped");
          continue;
        }
        const entry = bag[name];
        if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
          problems.push('instance "' + name + '" is not an object — skipped');
          continue;
        }
        // Type strictness, matched field-for-field by the PowerShell reader: a value
        // of the wrong JSON type is a malformed file, so it is reported and the
        // DERIVED default is used — never stringified into a host name or an alias.
        for (const f of STRING_FIELDS) {
          if (badString(entry[f])) {
            problems.push('instance "' + name + '": "' + f + '" must be a string — using the derived default');
          }
        }
        // Enum comparisons are CASE-SENSITIVE in both readers, so "HYPERV-REMOTE"
        // can never be honoured by one and rejected by the other.
        if (entry.backend != null && BACKENDS.indexOf(entry.backend) < 0) {
          problems.push('instance "' + name + '" has an unknown backend ' + JSON.stringify(entry.backend) +
            ' — treated as hyperv-local');
        }
        if (entry.sshPort != null && coercePort(entry.sshPort) === null) {
          problems.push('instance "' + name + '" has an invalid sshPort — using 22');
        }
        if (entry.service != null && (typeof entry.service !== "object" || Array.isArray(entry.service))) {
          problems.push('instance "' + name + '": "service" must be an object — ignored');
        } else if (entry.service && badString(entry.service.url)) {
          problems.push('instance "' + name + '": "service.url" must be a string — the service entry is ignored');
        } else if (entry.service && entry.service.auth != null && entry.service.auth !== "token" && entry.service.auth !== "negotiate") {
          problems.push('instance "' + name + '": unknown service auth ' + JSON.stringify(entry.service.auth) + ' — using negotiate');
        }
        if (entry.backend === "hyperv-remote" && !str(entry.sshHost)) {
          problems.push('instance "' + name + '" is hyperv-remote but has no sshHost');
        }
        const normalized = deriveDefaults(name, entry);
        // A field of the right TYPE can still be unusable (or hostile) as a host name,
        // an ssh alias, a key file name or a git ref. Such an entry is skipped WHOLE:
        // using the rest of it would dial, key or sync some other machine.
        const bad = identityProblems(normalized, entry);
        if (bad.length) {
          problems.push('instance "' + name + '": ' + bad.join("; ") + " — skipped");
          continue;
        }
        byName[name] = normalized;
      }
    }
    const dflt = str(doc.defaultInstance);
    if (dflt) {
      if (!isValidName(dflt)) {
        problems.push('defaultInstance "' + dflt + '" is not a valid instance name — using "' +
          DEFAULT_INSTANCE_NAME + '"');
      } else if (!byName[dflt]) {
        problems.push('defaultInstance "' + dflt + '" has no entry in "instances" — using "' +
          DEFAULT_INSTANCE_NAME + '"');
      } else {
        defaultInstance = dflt;
      }
    }
  }
  // The default instance is ALWAYS available, synthesized when absent. That is the
  // zero-change guarantee: a registry that forgets (or never had) "agent-vm" still
  // behaves exactly like today for every caller that asks for it.
  if (!byName[DEFAULT_INSTANCE_NAME]) byName[DEFAULT_INSTANCE_NAME] = { ...DEFAULT_INSTANCE };
  if (!byName[defaultInstance]) defaultInstance = DEFAULT_INSTANCE_NAME;
  return { registry: { byName, defaultInstance }, problems };
}

/**
 * Load the registry from disk. `opts`: { env?, path?, readFile? }.
 * NEVER throws and NEVER writes: a missing file (the overwhelmingly common case) is
 * indistinguishable from "one default instance", and an unreadable/garbage file
 * degrades to the same thing plus a `problems` entry.
 *
 * Returns { instances, byName, defaultInstance, problems, path, synthesized, exists }.
 */
function load(opts = {}) {
  const file = opts.path != null ? String(opts.path) : instancesPath(opts.env);
  const readFile = opts.readFile || ((p) => fs.readFileSync(p, "utf8"));
  let text = "", exists = false;
  if (file) {
    try { text = readFile(file); exists = true; }
    catch (e) {
      // ENOENT is the normal, silent case: no registry means the default instance.
      if (e && e.code !== "ENOENT") {
        return finishLoad(file, false, parseRegistry(""),
          ["instances.json could not be read (" + (e.message || e) + ") — using the default instance"]);
      }
    }
  }
  return finishLoad(file, exists, parseRegistry(text), []);
}

function finishLoad(file, exists, parsed, extraProblems) {
  const byName = parsed.registry.byName;
  const names = Object.keys(byName);
  return {
    path: file,
    exists,
    byName,
    instances: sortInstances(names.map((n) => byName[n]), parsed.registry.defaultInstance),
    defaultInstance: parsed.registry.defaultInstance,
    problems: extraProblems.concat(parsed.problems),
    synthesized: names.length === 1 && names[0] === DEFAULT_INSTANCE_NAME && !exists,
  };
}

/** The registry's instances, default first then alphabetical (a stable picker order). */
function sortInstances(list, defaultName) {
  return list.slice().sort((a, b) => {
    if (a.name === defaultName) return -1;
    if (b.name === defaultName) return 1;
    return a.name < b.name ? -1 : (a.name > b.name ? 1 : 0);
  });
}

function list(registry) {
  if (!registry) return [{ ...DEFAULT_INSTANCE }];
  if (Array.isArray(registry.instances)) return registry.instances;
  return sortInstances(Object.keys(registry.byName || {}).map((n) => registry.byName[n]),
    registry.defaultInstance || DEFAULT_INSTANCE_NAME);
}

/** Resolve one instance by name. An empty/unknown name falls back to the registry's
 *  default instance (and, failing that, to the synthesized default). Pure. */
function resolve(registry, name) {
  const bag = (registry && registry.byName) || {};
  const wanted = str(name);
  if (wanted && bag[wanted]) return bag[wanted];
  const dflt = (registry && registry.defaultInstance) || DEFAULT_INSTANCE_NAME;
  return bag[dflt] || bag[DEFAULT_INSTANCE_NAME] || { ...DEFAULT_INSTANCE };
}

/**
 * The ACTIVE instance for one VS Code window, by the documented precedence:
 *   1. `construct.instance` setting (a global override; "" = unset)
 *   2. the window's persisted workspaceState selection
 *   3. the registry's defaultInstance
 * A name at any level that no longer exists in the registry is SKIPPED (with a
 * `problem`) and the NEXT candidate is tried — an instance can be removed, or a
 * global `construct.instance` can go stale, while this window still has a perfectly
 * valid selection of its own; letting the invalid higher-precedence value win would
 * silently drag the window back to the default VM. The registry default is used only
 * once every candidate is exhausted. Returns { instance, name, source, problem?,
 * problems? } (`problem` = the first one, kept for callers that toast a single
 * string). Pure.
 */
function resolveActive(opts = {}) {
  const registry = opts.registry;
  const bag = (registry && registry.byName) || {};
  const candidates = [
    { value: str(opts.setting), source: "setting" },
    { value: str(opts.workspaceValue), source: "workspace" },
  ];
  const problems = [];
  const withProblems = (res) => (problems.length ? { ...res, problem: problems[0], problems } : res);
  for (const c of candidates) {
    if (!c.value) continue;
    if (bag[c.value]) return withProblems({ instance: bag[c.value], name: c.value, source: c.source });
    problems.push('instance "' + c.value + '" (' + c.source + ') is not in the registry — skipped');
  }
  const inst = resolve(registry, null);
  return withProblems({ instance: inst, name: inst.name, source: "default" });
}

/**
 * A GENERATION GATE for the active instance.
 *
 * Refreshing the panel is a multi-stage async pipeline (probe -> Hyper-V state ->
 * GitHub updates -> ccusage -> config-sync), and each stage can outlive a switch. The
 * failure it prevents: a slow probe of instance A resolves AFTER the user switched to
 * B, the continuation stamps the CURRENT name (B) onto A's payload, and the panel is
 * overwritten with A's status, agents and usage under B's name.
 *
 * So every pipeline captures a token() up front and re-checks valid(token) after each
 * await, before it posts anything or mutates a cache. set(name) bumps the generation
 * whenever the active instance changes, invalidating every token issued before it.
 * Pure and dependency-free, so the discard rule is unit-testable with deferred
 * promises instead of only being observable in a live window.
 */
function createGate(name) {
  let generation = 0;
  let current = name || DEFAULT_INSTANCE_NAME;
  return {
    /** The active instance name this gate is tracking. */
    get name() { return current; },
    /** Bumped on every real change; tokens carry the value they were issued at. */
    get generation() { return generation; },
    /** Capture the identity a pipeline started under. */
    token() { return { generation, name: current }; },
    /** Point the gate at another instance. Returns true when it actually changed. */
    set(next) {
      const n = String(next == null ? "" : next);
      if (!n || n === current) return false;
      current = n;
      generation += 1;
      return true;
    },
    /** Is a captured token still the active identity? A missing token is never valid. */
    valid(token) { return !!token && token.generation === generation; },
  };
}

/**
 * Match a Remote-SSH authority host against the registry, so a window attached to a
 * specific VM auto-selects that instance. `host` is the part after "ssh-remote+";
 * it is compared case-insensitively against each instance's alias and hostname.
 * Returns the instance or null. Pure.
 */
function matchByRemoteHost(registry, host) {
  const h = str(host);
  if (!h) return null;
  const lower = h.toLowerCase();
  for (const inst of list(registry)) {
    if (String(inst.hostAlias).toLowerCase() === lower) return inst;
    if (String(inst.vmHost).toLowerCase() === lower) return inst;
  }
  return null;
}

/**
 * Capture the target of a USER ACTION at its entry point.
 *
 * A command is not one atomic step: shutdown shows a modal, a rebuild probes the VM for
 * its project list, a clone runs for minutes. If the later steps re-read "the active
 * instance", switching mid-action silently redirects the rest of it — and for the
 * destructive ones that means the confirmation the user gave for A is executed against
 * B. So each command captures a target once and uses `target.cfg` / `target.instance`
 * throughout, checking `targetSuperseded` before anything irreversible.
 */
function captureTarget(gate, instance) {
  return {
    instance,
    name: instance ? instance.name : DEFAULT_INSTANCE_NAME,
    cfg: toSshCfg(instance),
    token: gate ? gate.token() : null,
  };
}

/** Has the window switched instances since this target was captured? Pure. */
function targetSuperseded(gate, target) {
  if (!gate || !target) return false;
  return !gate.valid(target.token);
}

/**
 * Should a window attached over Remote-SSH adopt the instance it is attached to?
 * Pure, so the decision is unit-tested rather than only observable in a live window.
 *
 *   `remoteAuthority` — vscode.env.remoteAuthority ("ssh-remote+<host>" or absent)
 *   `setting`         — the construct.instance pin; a pin always wins
 *   `currentName`     — the instance the window would otherwise use
 *
 * Returns { adopt, name?, reason }. Adoption is deliberately conservative: a local
 * window, a non-ssh authority, an unrecognised host, an explicit pin, or a host that
 * already resolves to the active instance all leave the selection alone.
 */
function planRemoteAdoption(registry, remoteAuthority, setting, currentName) {
  const m = /^ssh-remote\+(.+)$/i.exec(String(remoteAuthority || ""));
  if (!m) return { adopt: false, reason: "not-remote" };
  if (str(setting)) return { adopt: false, reason: "pinned-by-setting" };
  const matched = matchByRemoteHost(registry, m[1]);
  if (!matched) return { adopt: false, reason: "unknown-host" };
  if (matched.name === currentName) return { adopt: false, reason: "already-active" };
  return { adopt: true, name: matched.name, reason: "attached" };
}

/**
 * Apply planRemoteAdoption, AWAITING the persistence callback. `setActive(name)` may
 * return a promise (vscode's workspaceState.update is a Thenable) — it is awaited so
 * the caller can be sure the selection has really landed before anything probes a VM;
 * a fire-and-forget update would let activation keep using the OLD selection and probe
 * the wrong machine. Never rejects: a failed persist is reported, not thrown.
 */
async function adoptRemoteInstance(opts = {}) {
  const plan = planRemoteAdoption(opts.registry, opts.remoteAuthority, opts.setting, opts.currentName);
  if (!plan.adopt) return { ...plan, persisted: false };
  try {
    if (typeof opts.setActive === "function") await opts.setActive(plan.name);
    return { ...plan, persisted: true };
  } catch (e) {
    return { ...plan, persisted: false, error: e && e.message ? e.message : String(e) };
  }
}

// ── Writing (atomic) ─────────────────────────────────────────────────────────

/** The on-disk (schema v1) form of a normalized instance. Fields that equal the
 *  derivation for this name are still written explicitly — the file is the record of
 *  record for a non-default instance, and a reader on an older/newer Construct should
 *  not have to re-derive it. */
function toFileEntry(inst) {
  const out = {
    backend: inst.backend,
    vmName: inst.vmName,
    sshHost: inst.vmHost,
    sshPort: inst.sshPort,
    hostAlias: inst.hostAlias,
    keyName: inst.keyName,
    configBranch: inst.configBranch,
    scriptsDir: inst.scriptsDir == null ? null : inst.scriptsDir,
  };
  out.service = inst.service ? { url: inst.service.url, auth: inst.service.auth } : null;
  out.owner = inst.owner == null ? null : inst.owner;
  return out;
}

/** The full schema-v1 document for a registry. Pure. */
function toFileDocument(registry) {
  const doc = { version: SCHEMA_VERSION, defaultInstance: registry.defaultInstance || DEFAULT_INSTANCE_NAME, instances: {} };
  for (const inst of list(registry)) doc.instances[inst.name] = toFileEntry(inst);
  return doc;
}

/**
 * Write the registry ATOMICALLY: full content to a sibling temp file, fsync-free
 * rename over the destination (rename is atomic on both NTFS and POSIX), so a crash
 * mid-write can never leave a half-written registry that would silently degrade every
 * window to the default instance. Creates the container dir. Throws on I/O failure.
 */
function save(file, registry, opts = {}) {
  if (!file) throw new Error("No instances.json path resolved");
  const writeFileSync = opts.writeFileSync || fs.writeFileSync;
  const renameSync = opts.renameSync || fs.renameSync;
  const mkdirSync = opts.mkdirSync || fs.mkdirSync;
  const unlinkSync = opts.unlinkSync || fs.unlinkSync;
  mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + ".tmp." + process.pid + "." + Date.now();
  try {
    writeFileSync(tmp, JSON.stringify(toFileDocument(registry), null, 2) + "\n", "utf8");
    renameSync(tmp, file);
  } catch (e) {
    try { unlinkSync(tmp); } catch (_) {}
    throw e;
  }
  return file;
}

/** A shallow, mutable copy of a registry (so the mutators never edit a loaded one
 *  in place — a caller holding the old object keeps seeing the old state). */
function cloneRegistry(registry) {
  const byName = {};
  for (const inst of list(registry)) byName[inst.name] = { ...inst };
  return { byName, defaultInstance: (registry && registry.defaultInstance) || DEFAULT_INSTANCE_NAME };
}

/**
 * Add an instance. `entry` is a raw (partial) registry entry — every omitted field is
 * derived from the name. Rejects an invalid or already-taken name. Returns the new
 * registry; the caller persists it with save(). Pure.
 *
 * EVERY existing name is a duplicate, `agent-vm` included: it is always present
 * (synthesized when the file has no entry for it), so letting an "add" through would
 * silently REPLACE the default instance under the guise of creating a new one.
 * Changing an existing instance is updateInstance's job.
 */
function addInstance(registry, name, entry) {
  if (!isValidName(name)) throw new Error('Invalid instance name "' + name + '"');
  const next = cloneRegistry(registry);
  if (next.byName[name]) throw new Error('Instance "' + name + '" already exists');
  next.byName[name] = deriveDefaults(name, entry);
  return next;
}

/** Merge changes into an existing instance (unknown name -> throw). Pure. */
function updateInstance(registry, name, patch) {
  const next = cloneRegistry(registry);
  const cur = next.byName[name];
  if (!cur) throw new Error('Unknown instance "' + name + '"');
  next.byName[name] = deriveDefaults(name, { ...toFileEntry(cur), ...(patch || {}) });
  return next;
}

/**
 * Remove an instance. The DEFAULT instance ("agent-vm") cannot be removed — it is the
 * fallback every code path lands on, and a registry without it would just synthesize
 * it back on the next load. Removing the registry's `defaultInstance` resets that
 * pointer to "agent-vm". Pure.
 */
function removeInstance(registry, name) {
  if (name === DEFAULT_INSTANCE_NAME) throw new Error('The default instance cannot be removed');
  const next = cloneRegistry(registry);
  if (!next.byName[name]) throw new Error('Unknown instance "' + name + '"');
  delete next.byName[name];
  if (next.defaultInstance === name) next.defaultInstance = DEFAULT_INSTANCE_NAME;
  return next;
}

/** Point `defaultInstance` at an existing instance. Pure. */
function setDefaultInstance(registry, name) {
  const next = cloneRegistry(registry);
  if (!next.byName[name]) throw new Error('Unknown instance "' + name + '"');
  next.defaultInstance = name;
  return next;
}

module.exports = {
  CONTAINER, INSTANCES_FILE, SCHEMA_VERSION, BACKENDS, NAME_RE,
  DEFAULT_INSTANCE_NAME, DEFAULT_INSTANCE, DEFAULT_SSH_PORT,
  isValidName, localAppData, instancesPath,
  isHostEndpoint, isIpv6Literal, isSafeToken, isKeyFileName, isDnsLabel, identityProblems,
  deriveDefaults, isDefaultInstance, toSshCfg,
  parseRegistry, load, list, resolve, resolveActive, matchByRemoteHost,
  createGate, captureTarget, targetSuperseded, planRemoteAdoption, adoptRemoteInstance,
  toFileEntry, toFileDocument, save,
  addInstance, updateInstance, removeInstance, setDefaultInstance,
};
