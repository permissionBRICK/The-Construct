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
//   backendProblems(rawBackend)           -> string[]      (the backend field's own rule)
//   canonicalIdentity(name)               -> the identity a hyperv-local instance MUST have
//   localIdentityProblems(instance)       -> string[]      (that rule; [] = usable/non-local)
//   collisionProblems(byName)             -> { problems, drop } (cross-entry identity clashes)
//   instancesPath(env)                    -> abs path | null
//   deriveDefaults(name, raw)             -> normalized instance object
//   parseRegistry(text)                   -> { registry, problems }
//   load(opts)                            -> Registry  {instances, byName, defaultInstance,
//                                                       problems, path, synthesized}
//   list(registry)                        -> instance[] (default first, then a-z)
//   resolve(registry, name)               -> instance     (unknown name -> default)
//   hasInstance(registry, name)           -> bool          (OWN-property membership)
//   resolveActive({registry, setting, workspaceValue}) -> { instance, name, source }
//   createTargetQueue()                   -> one queued follow-up per target
//   createSyncStatusStore(opts)           -> per-target sync timestamp/result + throttle
//   describeSyncStatus(at, result)        -> the state.configSync fields for one tick
//   planCapturedFollowUp(gate, target, proceed) -> { run, reason, target }
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
/** The backend a missing `backend` field means — today's zero-change path. Mirrors
 *  drivers/index.js DEFAULT_BACKEND (and the driver key it resolves to). */
const DEFAULT_BACKEND = "hyperv-local";
const BACKENDS = ["hyperv-local", "hyperv-remote"];
const DEFAULT_SSH_PORT = 22;

/** Instance names are used verbatim in file names, ssh aliases and git refs, so they
 *  are restricted to a lowercase, slug-safe alphabet. */
const NAME_RE = /^[a-z0-9][a-z0-9-]{0,39}$/;

/** Today's literals — the instance an existing install implicitly runs. Frozen so a
 *  consumer can never mutate the fallback out from under another one. */
const DEFAULT_INSTANCE = Object.freeze({
  name: DEFAULT_INSTANCE_NAME,
  backend: DEFAULT_BACKEND,
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

// ── Prototype-free registry maps ─────────────────────────────────────────────
// A name->instance map is keyed by DATA FROM A FILE (and, for the active instance, by a
// user setting). A plain `{}` inherits Object.prototype, so `byName["constructor"]` is
// truthy for EVERY registry — a `{"defaultInstance":"constructor","instances":{}}` file
// (or `construct.instance: "constructor"`) resolved to Object's constructor FUNCTION and
// handed it out as an instance: undefined vmHost/keyName in every ssh argv, while the
// PowerShell reader (an ordinal Hashtable + ContainsKey) correctly reported "no entry"
// and used agent-vm. The two readers must never disagree about the same bytes, so every
// map here is PROTOTYPE-FREE and every membership test is an OWN-property test. An
// instance genuinely NAMED "constructor" is a valid name and still works — it is a key
// in the map like any other.

/** A registry map with no prototype: no key is ever "already there". */
function emptyMap() { return Object.create(null); }
/** Own-property membership — never an inherited Object.prototype member. */
function hasOwn(obj, key) {
  return !!obj && typeof key === "string" && Object.prototype.hasOwnProperty.call(obj, key);
}
/** The instance stored under `name`, or null. Own-property only. */
function ownInstance(bag, name) { return hasOwn(bag, name) ? bag[name] : null; }

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
/** `backend` is deliberately NOT in this list: "report it and use the derived default"
 *  is the wrong answer for the one field whose derived default is the LOCAL hypervisor.
 *  It has its own, stricter check — backendProblems() — which SKIPS the entry. */
const STRING_FIELDS = ["vmName", "sshHost", "vmHost", "hostAlias", "keyName", "configBranch", "scriptsDir", "owner"];

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

// ── The CANONICAL identity of a local (hyper-v) instance ─────────────────────
// For backend `hyperv-local` the identity is not a preference — it is DERIVED, in two
// places that must agree:
//   • here, from the instance name;
//   • by Auto-Install.ps1 during a rebuild, from -VmName alone (it derives the guest
//     host "<vmname lowercased>.mshome.net", the ssh alias = that name, and the key
//     construct_<name>_ed25519 — agent_vm_ed25519 for the default VM), which is why
//     reinstall/redownload emit ONLY -VmName (lifecycle.js INSTANCE_PARAMS).
// So a registry entry that deviates is not "a customized instance", it is an entry that
// TARGETS A DIFFERENT MACHINE than the one it would rebuild: `"work-vm": { vmName:
// "Agent-VM" }` reinstalls the DEFAULT VM, and a custom host/alias/key is silently
// replaced by the derived one the moment the VM is rebuilt, leaving the instance unable
// to reach it. Such an entry is SKIPPED with a problem (parseRegistry), never used.
//
// NON-LOCAL backends keep free-form (still format-checked) identities: their endpoints
// are defined by whatever created them on the other side, not by this convention.
// `configBranch` is deliberately NOT pinned — it is the one field the launched scripts
// can be TOLD (-ConfigBranch, threaded by lifecycle.configBranchOverride and gated by
// checkInstanceSupport), so an explicit branch stays a supported override.
// lib/AgentVm.Instances.ps1 applies the identical rules — change both together.

/** The identity a `hyperv-local` instance of this name MUST have. Pure. */
function canonicalIdentity(name) {
  const isDefault = name === DEFAULT_INSTANCE_NAME;
  return {
    vmName: isDefault ? DEFAULT_INSTANCE.vmName : name,
    vmHost: isDefault ? DEFAULT_INSTANCE.vmHost : name + ".mshome.net",
    hostAlias: isDefault ? DEFAULT_INSTANCE.hostAlias : name,
    keyName: isDefault ? DEFAULT_INSTANCE.keyName : "construct_" + name + "_ed25519",
    sshPort: DEFAULT_SSH_PORT,
  };
}

/**
 * Is this backend the LOCAL Hyper-V one? Normalized exactly like drivers/index.js
 * getDriver (trimmed, lowercased, empty = the default backend) — so every entry that
 * would be handed the local driver (hostLifecycle: true, i.e. rebuild/checkpoint
 * actions against a local VM of that vmName) is held to the canonical identity,
 * including a differently-cased "HYPERV-LOCAL" the driver lookup would still resolve.
 */
function isLocalBackend(backend) {
  const v = String(backend == null ? "" : backend).trim().toLowerCase();
  return v === "" || v === DEFAULT_BACKEND;
}

/**
 * The problems of a NORMALIZED instance whose identity must be canonical (see above):
 * [] for every non-local backend and for a local instance that matches its derivation.
 * Pure.
 */
function localIdentityProblems(inst) {
  if (!inst || !isLocalBackend(inst.backend)) return [];
  const c = canonicalIdentity(inst.name);
  const q = (v) => JSON.stringify(v === undefined ? null : v);
  const why = ' (a "' + DEFAULT_BACKEND + '" instance\'s identity is derived from its name — ' +
    "Auto-Install.ps1 rebuilds it that way, so anything else targets another VM)";
  const out = [];
  // The Hyper-V display name is case-INSENSITIVE (and the default instance's canonical
  // spelling is the display-cased "Agent-VM"), so only the lowercased form must match.
  if (String(inst.vmName).toLowerCase() !== c.vmName.toLowerCase()) {
    out.push('"vmName" ' + q(inst.vmName) + " must be " + q(c.vmName) + " for instance " + q(inst.name) + why);
  }
  // The rest are the lowercase tokens the scripts derive, compared verbatim: a differing
  // spelling is a differing ssh_config Host block / key file / -VmHost argument.
  if (inst.vmHost !== c.vmHost) {
    out.push('"sshHost" ' + q(inst.vmHost) + " must be " + q(c.vmHost) + " for instance " + q(inst.name) + why);
  }
  if (inst.hostAlias !== c.hostAlias) {
    out.push('"hostAlias" ' + q(inst.hostAlias) + " must be " + q(c.hostAlias) + " for instance " + q(inst.name) + why);
  }
  if (inst.keyName !== c.keyName) {
    out.push('"keyName" ' + q(inst.keyName) + " must be " + q(c.keyName) + " for instance " + q(inst.name) + why);
  }
  if (Number(inst.sshPort) !== c.sshPort) {
    out.push('"sshPort" ' + q(inst.sshPort) + " must be " + c.sshPort + " for instance " + q(inst.name) + why);
  }
  return out;
}

/**
 * The backend of an entry, NEVER coerced. The rule is PRESENCE-AWARE:
 *   • ABSENT (or JSON null) -> "hyperv-local", today's zero-change default;
 *   • a usable string       -> kept EXACTLY as written (trimmed), whatever it says.
 * A wrong or misspelled backend ("proxmox", "hyperv-remtoe") must reach drivers/index.js
 * as ITSELF, where the unknown-driver fallback refuses the hypervisor actions. Rewriting
 * anything to "hyperv-local" (as this once did) PROMOTED it to destructive local Hyper-V
 * access: lifecycleSupport would see hostLifecycle=true and let Reinstall/Redownload/
 * checkpoints run against a LOCAL VM of that name. A present-but-UNUSABLE value (wrong
 * type, or an empty/whitespace string) is kept here as it came so it can never read as
 * local either — parseRegistry skips such an entry outright (backendProblems). Pure.
 */
function deriveBackend(raw) {
  if (raw == null) return DEFAULT_BACKEND;
  const v = str(raw);
  return v == null ? raw : v;
}

/**
 * The problems of an entry's RAW `backend` field ([] = usable). This field owns its own
 * type check (it is NOT in STRING_FIELDS) because "report it and use the derived
 * default" is exactly the wrong answer here: the derived default is the LOCAL hypervisor.
 *
 * Two kinds of entry are refused whole rather than normalized:
 *   • PRESENT BUT UNUSABLE — `backend: 42`, `backend: ""`, `backend: "  "`. The file
 *     states a backend; it just isn't one. Deriving "hyperv-local" from it would grant
 *     destructive local access to a value the user never wrote.
 *   • A SPELLING THE TWO LOOKUPS READ DIFFERENTLY — "HYPERV-LOCAL", "Hyperv-Local".
 *     Every enum comparison in both readers is case-SENSITIVE (so this is "unknown"),
 *     but drivers/index.js getDriver() trims and lowercases before the lookup (so it
 *     would hand back the LOCAL driver, hostLifecycle: true). A value the two disagree
 *     about is not safe to act on under either reading, so it does not load at all.
 * A genuinely unknown backend ("proxmox") is NOT a problem here — it is reported
 * separately and kept, because the driver dispatch degrades on it correctly.
 * lib/AgentVm.Instances.ps1 applies the identical rule. Pure.
 */
function backendProblems(raw) {
  if (raw == null) return [];   // omitted / JSON null -> the derived default
  const q = (v) => JSON.stringify(v === undefined ? null : v);
  if (typeof raw !== "string" || !raw.trim()) {
    return ['"backend" ' + q(raw) + " is not a usable backend id (omit it for " + q(DEFAULT_BACKEND) +
      ", or name one of: " + BACKENDS.join(", ") + ")"];
  }
  const v = raw.trim();
  if (BACKENDS.indexOf(v) < 0 && isLocalBackend(v)) {
    return ['"backend" ' + q(v) + " is not spelled " + q(DEFAULT_BACKEND) +
      " (the backend id is case-sensitive, but the driver lookup is not — a value the two" +
      " read differently must not drive a local VM)"];
  }
  return [];
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
 *
 * For `hyperv-local` these derivations are also the ONLY permitted values — an entry
 * that states something else is skipped by parseRegistry (localIdentityProblems), not
 * normalized into it.
 */
function deriveDefaults(name, raw) {
  const r = (raw && typeof raw === "object" && !Array.isArray(raw)) ? raw : {};
  const isDefault = name === DEFAULT_INSTANCE_NAME;
  const backend = deriveBackend(r.backend);
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
 * The identity fields that must be UNIQUE across the registry, with the schema name
 * each one is reported under. Two instances sharing any of them are two names for one
 * machine (or one key file / one ssh_config Host block): a rebuild of the second would
 * delete the first's VM, and a reprovision would overwrite its key.
 *
 * `configBranch` is one of them, and for the same reason: the config-sync branch is the
 * instance's STORE inside the one host config repo (docs/config-sync.md, "Multiple
 * instances" — one branch per VM). Two entries on one branch share their VM snapshots,
 * their deletion history, their merge base and their write-backs, so one VM's tick
 * merges — or deletes — the other VM's configuration. Rule 1 below also makes the
 * default instance's historical branch "vm" RESERVED for `agent-vm`: a non-default entry
 * that claims it is skipped, exactly like one that claims the default VM's name.
 */
const UNIQUE_FIELDS = Object.freeze([
  { key: "vmName", label: "vmName" },
  { key: "vmHost", label: "sshHost" },
  { key: "hostAlias", label: "hostAlias" },
  { key: "keyName", label: "keyName" },
  { key: "configBranch", label: "configBranch" },
]);

/** All five are case-insensitive in the places they land (Hyper-V names, DNS, an
 *  ssh_config alias, an NTFS file name, a Windows loose-ref file), so they are
 *  compared lowercased. */
function idKey(v) { return String(v == null ? "" : v).toLowerCase(); }

/**
 * Cross-entry identity COLLISIONS. Returns { problems, drop } where `drop` is the set of
 * instance names that must not load. Two rules:
 *   1. a non-default entry may not claim any of the DEFAULT instance's values — the
 *      default is always present (synthesized when absent), so such an entry aims a
 *      rebuild/re-key at the default VM under another name;
 *   2. no two entries may share one — BOTH are dropped, because nothing in the file says
 *      which one is the impostor, and keeping either would act on a machine the user
 *      thinks belongs to the other. Dropping both is also what makes the two readers
 *      agree without depending on key order.
 * Pure.
 */
function collisionProblems(byName) {
  const names = Object.keys(byName).sort();   // ordinal — the PS reader sorts the same way
  const q = (v) => JSON.stringify(v === undefined ? null : v);
  const problems = [];
  const drop = new Set();
  for (const name of names) {
    if (name === DEFAULT_INSTANCE_NAME) continue;
    const inst = byName[name];
    for (const f of UNIQUE_FIELDS) {
      if (idKey(inst[f.key]) === idKey(DEFAULT_INSTANCE[f.key])) {
        problems.push('instance "' + name + '": ' + f.label + " " + q(inst[f.key]) +
          ' belongs to the default instance "' + DEFAULT_INSTANCE_NAME + '" — skipped');
        drop.add(name);
        break;
      }
    }
  }
  for (let i = 0; i < names.length; i++) {
    for (let j = i + 1; j < names.length; j++) {
      const a = byName[names[i]], b = byName[names[j]];
      for (const f of UNIQUE_FIELDS) {
        if (idKey(a[f.key]) === idKey(b[f.key])) {
          problems.push('instances "' + names[i] + '" and "' + names[j] + '" share the same ' +
            f.label + " " + q(a[f.key]) + " — both skipped");
          drop.add(names[i]);
          drop.add(names[j]);
          break;
        }
      }
    }
  }
  return { problems, drop };
}

/**
 * Parse registry TEXT into { registry, problems }. Never throws. `registry` always
 * contains at least the default instance; every problem found (bad JSON, wrong
 * version, invalid name, unknown backend, bad port, missing remote host) is reported
 * as a human-readable string so the extension can surface one toast and keep running.
 */
function parseRegistry(text) {
  const problems = [];
  const byName = emptyMap();
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
        // The backend's own rules (backendProblems): an unusable or two-faced spelling
        // makes the entry unloadable, and is collected with the identity problems below.
        // The TRIMMED string is what both readers compare (the PS side's
        // Get-ConstructInstanceField trims), so " hyperv-local " is simply the enum value.
        const backendBad = backendProblems(entry.backend);
        const rawBackend = str(entry.backend);
        // Enum comparisons are CASE-SENSITIVE in both readers, so "HYPERV-REMOTE" can
        // never be honoured by one and rejected by the other. An unknown backend is
        // REPORTED BUT KEPT VERBATIM (see deriveBackend): it reaches drivers/index.js as
        // itself, where the unknown-driver fallback refuses the hypervisor actions.
        // Coercing it to hyperv-local would hand a typo destructive local Hyper-V access.
        if (!backendBad.length && rawBackend && BACKENDS.indexOf(rawBackend) < 0) {
          problems.push('instance "' + name + '" has an unknown backend ' + JSON.stringify(rawBackend) +
            " — this Construct has no driver for it, so rebuild/checkpoint actions are unavailable" +
            " for it (update Construct if a newer version created it)");
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
        if (rawBackend === "hyperv-remote" && !str(entry.sshHost)) {
          problems.push('instance "' + name + '" is hyperv-remote but has no sshHost');
        }
        const normalized = deriveDefaults(name, entry);
        // A field of the right TYPE can still be unusable (or hostile) as a host name,
        // an ssh alias, a key file name or a git ref. Such an entry is skipped WHOLE:
        // using the rest of it would dial, key or sync some other machine. A local
        // instance is held to its CANONICAL identity on top of that — a deviating one
        // would rebuild (and then be unable to reach) a different VM than it names —
        // and an entry whose BACKEND itself is unusable never loads at all.
        const bad = backendBad
          .concat(identityProblems(normalized, entry))
          .concat(localIdentityProblems(normalized));
        if (bad.length) {
          problems.push('instance "' + name + '": ' + bad.join("; ") + " — skipped");
          continue;
        }
        byName[name] = normalized;
      }
      // ...and finally the CROSS-entry rules: two instances that share an identity, or
      // one that claims the default instance's, are dropped here rather than left to
      // retarget each other's rebuilds.
      const collisions = collisionProblems(byName);
      for (const p of collisions.problems) problems.push(p);
      for (const name of collisions.drop) delete byName[name];
    }
    const dflt = str(doc.defaultInstance);
    if (dflt) {
      if (!isValidName(dflt)) {
        problems.push('defaultInstance "' + dflt + '" is not a valid instance name — using "' +
          DEFAULT_INSTANCE_NAME + '"');
      } else if (!hasOwn(byName, dflt)) {
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
  if (!hasOwn(byName, DEFAULT_INSTANCE_NAME)) byName[DEFAULT_INSTANCE_NAME] = { ...DEFAULT_INSTANCE };
  if (!hasOwn(byName, defaultInstance)) defaultInstance = DEFAULT_INSTANCE_NAME;
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
  const bag = (registry && registry.byName) || emptyMap();
  const wanted = str(name);
  // OWN properties only: a name like "constructor" or "toString" must be "not in the
  // registry" here exactly as it is for the PowerShell reader's ContainsKey.
  const direct = ownInstance(bag, wanted);
  if (direct) return direct;
  const dflt = (registry && registry.defaultInstance) || DEFAULT_INSTANCE_NAME;
  return ownInstance(bag, dflt) || ownInstance(bag, DEFAULT_INSTANCE_NAME) || { ...DEFAULT_INSTANCE };
}

/** Does the registry hold an instance under this exact name? Own-property only, so an
 *  Object.prototype member ("constructor", "toString") is never mistaken for one. Pure. */
function hasInstance(registry, name) {
  return !!ownInstance((registry && registry.byName) || null, str(name));
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
  const bag = (registry && registry.byName) || emptyMap();
  const candidates = [
    { value: str(opts.setting), source: "setting" },
    { value: str(opts.workspaceValue), source: "workspace" },
  ];
  const problems = [];
  const withProblems = (res) => (problems.length ? { ...res, problem: problems[0], problems } : res);
  for (const c of candidates) {
    if (!c.value) continue;
    // Own-property only. A `construct.instance` of "constructor" is a name the registry
    // does not have — NOT Object's constructor function handed out as an instance.
    const found = ownInstance(bag, c.value);
    if (found) return withProblems({ instance: found, name: c.value, source: c.source });
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
 * A PER-INSTANCE COALESCER for a long, VM-bound background job (today: the auto-import
 * SSH scan of a VM's repos).
 *
 * Coalescing and throttling are only correct BETWEEN CALLERS ASKING ABOUT THE SAME VM.
 * Held globally, they lie across a switch: an in-flight scan of A is handed to a caller
 * asking about B (the lifecycle pre-flight then treats B as scanned and rebuilds it
 * without importing its repos), and A's throttle stamp suppresses B's first automatic
 * scan for the whole window. So both the in-flight promise and the last-attempt stamp
 * are keyed by instance name.
 *
 * `run(key, force, start)` returns the in-flight promise for that key when there is
 * one, `Promise.resolve(null)` when the key is inside its throttle window and `force`
 * is false, and otherwise starts `start()` (synchronously, before the stamp is read
 * again) and remembers it until it settles. A rejecting `start` resolves to null — a
 * failed scan must never leave the key permanently "in flight".
 *
 * Pure and dependency-free (`now` is injectable), so the ordering rules are unit-tested
 * with deferred promises instead of only being observable in a live window.
 */
function createCoalescer(opts) {
  const o = opts || {};
  const throttleMs = typeof o.throttleMs === "number" ? o.throttleMs : 0;
  const now = typeof o.now === "function" ? o.now : () => Date.now();
  const inflight = new Map();
  const lastAt = new Map();
  const keyOf = (key) => String(key == null ? "" : key);
  return {
    run(key, force, start) {
      const k = keyOf(key);
      const pending = inflight.get(k);
      if (pending) return pending;
      // A key that was NEVER attempted is not throttled — "no stamp" is not "stamped at
      // epoch 0", which is what a `|| 0` default would make of a new instance under a
      // small/injected clock.
      const last = lastAt.has(k) ? lastAt.get(k) : null;
      if (!force && last != null && now() - last < throttleMs) return Promise.resolve(null);
      lastAt.set(k, now());
      let started;
      try { started = Promise.resolve(start()); }
      catch (_) { started = Promise.resolve(null); }
      const p = started.catch(() => null).then((r) => {
        if (inflight.get(k) === p) inflight.delete(k);
        return r;
      });
      inflight.set(k, p);
      return p;
    },
    /** Re-stamp a key from inside the job (the scan stamps again when it finishes). */
    stamp(key) { lastAt.set(keyOf(key), now()); },
    /** Last attempt time for a key, or null when it was never attempted. */
    lastAt(key) { const k = keyOf(key); return lastAt.has(k) ? lastAt.get(k) : null; },
    /** Is a job for this key in flight? */
    isInflight(key) { return inflight.has(keyOf(key)); },
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
 * A FOLLOW-UP QUEUE KEYED BY TARGET — one pending follow-up per instance, behind the
 * job that is already running (today: the config-sync tick).
 *
 * The tick serializes window-wide (the config repo's lock is repo-wide), so a request
 * that arrives while one is in flight has to wait and then run its own. Held as a SINGLE
 * global promise, that queued follow-up lost its subject: an explicit "Sync Now" for A,
 * queued behind A's tick, ran whatever the window had switched to by the time it
 * started — syncing B's branch and B's VM store while A's changes stayed unsynced.
 *
 * `queue(key, active, start)` returns the ONE follow-up promise for that key (a second
 * caller for the same key joins it; a different key gets its own), chains it after
 * `active` — settled either way, because a failed tick must not wedge the queue
 * forever — and then calls `start()`, the closure the FIRST caller registered, which
 * carries that caller's captured target. Pure and dependency-free, so the ordering is
 * unit-tested with deferred promises.
 */
function createTargetQueue() {
  const pending = new Map();
  const keyOf = (key) => String(key == null ? "" : key);
  return {
    queue(key, active, start) {
      const k = keyOf(key);
      const already = pending.get(k);
      if (already) return already;
      const settled = Promise.resolve(active).then(() => null, () => null);
      const p = settled.then(() => {
        // Clear BEFORE start(): the follow-up is now the running job, and a request that
        // arrives from here on must queue behind it rather than join a job in progress.
        if (pending.get(k) === p) pending.delete(k);
        return start();
      });
      pending.set(k, p);
      return p;
    },
    /** Is a follow-up queued for this key? */
    isQueued(key) { return pending.has(keyOf(key)); },
    /** How many keys have a follow-up queued (one per instance at most). */
    get size() { return pending.size; },
  };
}

/**
 * The user-visible shape of ONE instance's last config-sync tick: the four fields
 * state.configSync carries about it. Pure, and separated from the store below so the
 * "which word describes this TickResult" mapping (extension.js used to inline it twice,
 * once for the last tick and once for a recovery tick) is testable on its own.
 *   `at`     — when that instance last ticked (ms), or null/0 for "never"
 *   `result` — that tick's TickResult, or null
 */
function describeSyncStatus(at, result) {
  return {
    lastSyncAt: at || null,
    lastResult: result ? (result.ok ? "ok" : (result.conflict ? "conflict" : (result.blocked ? "blocked" : "error"))) : null,
    blockedReason: result ? (result.blockedReason || null) : null,
    warnings: result ? (result.warnings || []) : [],
  };
}

/**
 * PER-TARGET CONFIG-SYNC STATUS + AUTO-TICK THROTTLE.
 *
 * A sync tick belongs to ONE instance: it commits that instance's branch and reads/writes
 * that instance's VM store (docs/config-sync.md, "Multiple instances" — one branch per
 * VM). Its timestamp and its result therefore describe that instance and nothing else.
 * Held window-globally (as `lastSyncTickAt` / `lastSyncResult` were), they LIED after a
 * switch: the panel showed A's timestamp, result, warnings and blocked reason under B's
 * name, and A's stamp satisfied the 5-minute throttle so B's first automatic tick was
 * suppressed for up to five minutes — leaving B's branch and VM store unsynchronized
 * exactly when the user had just switched to it.
 *
 * So both are keyed by the CAPTURED TARGET's name. What stays global is the thing that is
 * genuinely repository-wide: the in-flight tick and the config repo's cross-process lock
 * (one repo, one lock — two instances still serialize against each other).
 *
 * `throttleMs` is the automatic-tick spacing; `now` is injectable, so the ordering is
 * unit-tested with a fake clock instead of only being observable in a live window.
 */
function createSyncStatusStore(opts) {
  const o = opts || {};
  const throttleMs = typeof o.throttleMs === "number" ? o.throttleMs : 0;
  const now = typeof o.now === "function" ? o.now : () => Date.now();
  const byTarget = new Map();
  const keyOf = (key) => String(key == null ? "" : key);
  const entry = (key) => byTarget.get(keyOf(key)) || null;
  return {
    /** Record a finished tick FOR ONE TARGET. Returns the result, for chaining. */
    record(key, result) {
      byTarget.set(keyOf(key), { at: now(), result: result || null });
      return result;
    },
    /** When this target last ticked, or null when it never has. */
    lastAt(key) { const e = entry(key); return e ? e.at : null; },
    /** This target's last TickResult, or null. */
    lastResult(key) { const e = entry(key); return e ? e.result : null; },
    /** The state.configSync fields for this target (see describeSyncStatus). */
    status(key) { const e = entry(key); return describeSyncStatus(e ? e.at : null, e ? e.result : null); },
    /**
     * May an AUTOMATIC tick run for this target now? A target that has never ticked is
     * always due — "no stamp" is not "stamped at epoch 0" — and each target's window is
     * its own, so switching to B never inherits A's suppression.
     */
    dueForAuto(key) {
      const e = entry(key);
      return !e || (now() - e.at) >= throttleMs;
    },
    /** How many targets have a recorded tick (one entry per instance at most). */
    get size() { return byTarget.size; },
  };
}

/**
 * Should a step that was DEFERRED past an await still run — and for whom?
 *
 * Two flows in extension.js decide this after something slow: a notification the user
 * answers minutes later ("Reprovision now"), and the mic auto-arm's reachability probe.
 * Both captured a target up front, and both must refuse to act when the window has
 * switched since: reprovisioning would rebuild the OTHER VM with this instance's scripts
 * and settings, and the auto-arm would open a microphone tunnel to a VM whose own
 * preference may be off. Acting on the CURRENT instance is equally wrong — the answer
 * (or the probe result) is about the captured one.
 *
 * `proceed` is the flow's own yes/no (the button pick, the probe result). Returns
 * { run, reason: "declined" | "superseded" | "ok", target } — the caller decides whether
 * a supersede is worth a toast (a user-facing prompt) or a log line (a silent auto-arm).
 * Pure.
 */
function planCapturedFollowUp(gate, target, proceed) {
  if (!proceed) return { run: false, reason: "declined", target: target || null };
  if (targetSuperseded(gate, target)) return { run: false, reason: "superseded", target: target || null };
  return { run: true, reason: "ok", target: target || null };
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
  const doc = { version: SCHEMA_VERSION, defaultInstance: registry.defaultInstance || DEFAULT_INSTANCE_NAME, instances: emptyMap() };
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
  const byName = emptyMap();
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
  if (hasOwn(next.byName, name)) throw new Error('Instance "' + name + '" already exists');
  next.byName[name] = validatedInstance(name, entry);
  assertNoCollisions(next);
  return next;
}

/** Merge changes into an existing instance (unknown name -> throw). Pure. */
function updateInstance(registry, name, patch) {
  const next = cloneRegistry(registry);
  const cur = ownInstance(next.byName, name);
  if (!cur) throw new Error('Unknown instance "' + name + '"');
  next.byName[name] = validatedInstance(name, { ...toFileEntry(cur), ...(patch || {}) });
  assertNoCollisions(next);
  return next;
}

/**
 * deriveDefaults + THE READER'S OWN RULES, as a throw. The mutators write a file that
 * parseRegistry reads back: an entry the reader would skip (a hostile field, or a local
 * instance whose identity isn't the derived one) must be refused where it is CREATED,
 * not silently persisted and then dropped on the next load — the instance would simply
 * vanish from the picker with only a toast to explain it. Pure.
 */
function validatedInstance(name, entry) {
  const inst = deriveDefaults(name, entry);
  const raw = (entry && typeof entry === "object" && !Array.isArray(entry)) ? entry : {};
  const bad = backendProblems(raw.backend)
    .concat(identityProblems(inst, entry))
    .concat(localIdentityProblems(inst));
  if (bad.length) throw new Error('Instance "' + name + '": ' + bad.join("; "));
  return inst;
}

/** The cross-entry rules, as a throw (same reason as validatedInstance). Pure. */
function assertNoCollisions(registry) {
  const { problems } = collisionProblems(registry.byName);
  if (problems.length) throw new Error(problems[0]);
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
  if (!hasOwn(next.byName, name)) throw new Error('Unknown instance "' + name + '"');
  delete next.byName[name];
  if (next.defaultInstance === name) next.defaultInstance = DEFAULT_INSTANCE_NAME;
  return next;
}

/** Point `defaultInstance` at an existing instance. Pure. */
function setDefaultInstance(registry, name) {
  const next = cloneRegistry(registry);
  if (!hasOwn(next.byName, name)) throw new Error('Unknown instance "' + name + '"');
  next.defaultInstance = name;
  return next;
}

module.exports = {
  CONTAINER, INSTANCES_FILE, SCHEMA_VERSION, BACKENDS, NAME_RE,
  DEFAULT_INSTANCE_NAME, DEFAULT_INSTANCE, DEFAULT_SSH_PORT, DEFAULT_BACKEND,
  isValidName, localAppData, instancesPath,
  isHostEndpoint, isIpv6Literal, isSafeToken, isKeyFileName, isDnsLabel, identityProblems,
  isLocalBackend, canonicalIdentity, localIdentityProblems, collisionProblems,
  deriveBackend, backendProblems,
  deriveDefaults, isDefaultInstance, toSshCfg,
  parseRegistry, load, list, resolve, resolveActive, hasInstance, matchByRemoteHost,
  createGate, createCoalescer, createTargetQueue, captureTarget, targetSuperseded,
  describeSyncStatus, createSyncStatusStore,
  planCapturedFollowUp, planRemoteAdoption, adoptRemoteInstance,
  toFileEntry, toFileDocument, save,
  addInstance, updateInstance, removeInstance, setDefaultInstance,
};
