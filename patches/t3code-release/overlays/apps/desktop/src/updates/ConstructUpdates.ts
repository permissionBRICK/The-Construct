// @effect-diagnostics nodeBuiltinImport:off globalFetch:off - Construct's update handoff
// reads the install markers on this PC and launches the host PowerShell scripts; the two
// JSON lookups use the global fetch so the module stays Effect-free and testable with a
// plain injected fetcher.
//
// Construct-managed updates for the Construct-built T3 Code Desktop app.
//
// A Construct build carries `<upstream t3 version>-construct.<hash>` as its app
// version. Such a build is never updated by electron-updater: the patched
// server + Desktop installer are rebuilt in the VM by a Construct reprovision
// and silently installed on this PC. So the Desktop app's update control
// mirrors what the VS Code control panel does instead:
//
//   1. Construct itself is behind its GitHub ref  -> "update-construct"
//      (Update-Construct.ps1: refreshes the scripts + VS Code panel on this PC;
//      the panel's own "Update Construct" button does exactly this).
//   2. The VM was provisioned with a different Construct than the one installed
//      on this PC (the panel's yellow Reprovision button), or a newer upstream
//      T3 Code release exists on this build's channel -> "reprovision"
//      (Update-T3Code.ps1: reruns provisioning with the saved settings, which
//      rebuilds the patched T3 Code and installs the new Desktop app).
//
// Everything here is plain TypeScript with injectable IO so it unit-tests
// without a network, a Windows filesystem, or Electron. DesktopUpdates.ts owns
// the Effect wiring (pollers, state broadcast, action locking).

import type {
  ConstructUpdateAction,
  ConstructUpdateInfo,
  DesktopUpdateChannel,
  DesktopUpdateState,
} from "@t3tools/contracts";
import { compareSemverVersions, parseSemver } from "@t3tools/shared/semver";
import * as NodeChildProcess from "node:child_process";
import * as NodeFS from "node:fs";
import * as NodeNet from "node:net";
import * as NodePath from "node:path";

export const DEFAULT_CONSTRUCT_REPO = "permissionBRICK/The-Construct";
export const DEFAULT_CONSTRUCT_REF = "main";
/** `%LOCALAPPDATA%\The-Construct` — install.ps1 / Update-Construct.ps1 extract the
 *  repo to `<container>\<owner-repo-ref slug>\<repo>-<ref>\`. */
export const CONSTRUCT_CONTAINER_DIR_NAME = "The-Construct";
/** Present at the root of every extracted Construct repo (same marker the VS Code
 *  panel's host.js uses to find the newest install). */
export const CONSTRUCT_SCRIPTS_MARKER = "Auto-Install.ps1";
export const CONSTRUCT_SETTINGS_FILE = ".construct-settings.json";
/** The client-side instance registry (extension/src/instances.js, lib/AgentVm.Instances.ps1). */
export const CONSTRUCT_INSTANCES_FILE = "instances.json";
export const CONSTRUCT_UPDATE_SCRIPT = "Update-Construct.ps1";
export const CONSTRUCT_REPROVISION_SCRIPT = "Update-T3Code.ps1";

const CONSTRUCT_BUILD_VERSION_PATTERN = /-construct\.[0-9a-f]{6,}$/i;
const COMMIT_PATTERN = /^[0-9a-f]{7,64}$/i;
const REPO_PATTERN = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const REF_PATTERN = /^[A-Za-z0-9_./-]+$/;
// Characters that would change meaning inside the cmd.exe `start` command line
// we build (see planConstructLaunch). Paths under %LOCALAPPDATA% never contain
// them in practice; refuse rather than guess at escaping.
const UNSAFE_COMMAND_LINE_CHARACTER = /["%^&|<>\r\n]/;
// Windows PowerShell 5.1's `Set-Content -Encoding UTF8` writes a BOM; every JSON the
// Construct scripts produce may start with one (host.js strips it the same way).
const UTF8_BOM = /^﻿/;

// ── Build identity ──────────────────────────────────────────────────────────────

/** True for a Construct-built Desktop app (`0.0.38-construct.bb8cb346`). Stock
 *  builds keep electron-updater; the upstream test suite exercises those. */
export function isConstructManagedBuild(appVersion: string): boolean {
  return CONSTRUCT_BUILD_VERSION_PATTERN.test(appVersion.trim());
}

/** The upstream T3 Code version this build was made from (suffix stripped). */
export function constructT3BaseVersion(appVersion: string): string {
  return appVersion.trim().replace(CONSTRUCT_BUILD_VERSION_PATTERN, "");
}

/** The npm dist-tag this build's T3 channel tracks: a nightly base version
 *  (`0.0.39-nightly.20260901.1`) came from `t3@nightly`, anything else from
 *  `t3@latest` (the same mapping as bin/build-t3code.sh). */
export function resolveConstructT3Channel(t3BaseVersion: string): DesktopUpdateChannel {
  return /-nightly(\.|$)/.test(t3BaseVersion) ? "nightly" : "latest";
}

export function constructT3RegistryUrl(channel: DesktopUpdateChannel): string {
  return channel === "nightly"
    ? "https://registry.npmjs.org/t3/nightly"
    : "https://registry.npmjs.org/t3/latest";
}

export function constructCompareUrl(markers: ConstructMarkers): string | null {
  if (!markers.installedCommit) return null;
  return `https://api.github.com/repos/${markers.repo}/compare/${markers.installedCommit}...${markers.ref}`;
}

// ── Install markers ─────────────────────────────────────────────────────────────

export interface ConstructMarkers {
  readonly repo: string;
  readonly ref: string;
  /** The installed Construct (scripts + VS Code panel); written by install / Update-Construct. */
  readonly installedCommit: string | null;
  /** What the VM was last provisioned with; written by Provision-AgentVM at the end of a run. */
  readonly provisionedCommit: string | null;
}

function readCommit(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim().toLowerCase();
  return COMMIT_PATTERN.test(trimmed) ? trimmed : null;
}

/** Parse `.construct-settings.json` (the same defaults the panel's updates.js applies). */
export function readConstructMarkers(raw: unknown): ConstructMarkers {
  const record = typeof raw === "object" && raw !== null ? (raw as Record<string, unknown>) : {};
  const repo = typeof record.constructRepo === "string" ? record.constructRepo.trim() : "";
  const ref = typeof record.constructRef === "string" ? record.constructRef.trim() : "";
  return {
    repo: REPO_PATTERN.test(repo) ? repo : DEFAULT_CONSTRUCT_REPO,
    ref: REF_PATTERN.test(ref) ? ref : DEFAULT_CONSTRUCT_REF,
    installedCommit: readCommit(record.installedCommit),
    provisionedCommit: readCommit(record.provisionedCommit),
  };
}

/** The VM was provisioned with a DIFFERENT commit than the installed Construct, so a
 *  reprovision would apply the update to the VM. Conservative like the panel: only
 *  when BOTH markers are known. */
export function isConstructProvisionStale(markers: ConstructMarkers): boolean {
  return (
    markers.installedCommit !== null &&
    markers.provisionedCommit !== null &&
    markers.installedCommit !== markers.provisionedCommit
  );
}

// ── Filesystem (injectable) ─────────────────────────────────────────────────────

export interface ConstructFileSystem {
  /** Absolute paths of the immediate subdirectories of `dir` ([] when unreadable). */
  readonly listDirectories: (dir: string) => ReadonlyArray<string>;
  /** mtime of a regular file in ms, or null when it does not exist / is not a file. */
  readonly fileMtimeMs: (path: string) => number | null;
  /** File contents, or null when unreadable. */
  readonly readTextFile: (path: string) => string | null;
}

export const nodeConstructFileSystem: ConstructFileSystem = {
  listDirectories: (dir) => {
    try {
      return NodeFS.readdirSync(dir, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => NodePath.join(dir, entry.name));
    } catch {
      return [];
    }
  },
  fileMtimeMs: (path) => {
    try {
      const stat = NodeFS.statSync(path);
      return stat.isFile() ? stat.mtimeMs : null;
    } catch {
      return null;
    }
  },
  readTextFile: (path) => {
    try {
      return NodeFS.readFileSync(path, "utf8");
    } catch {
      return null;
    }
  },
};

type JoinPath = (...parts: string[]) => string;
// The platform join: Windows on the real target (Construct builds only run there); the
// native join also lets tests feed real temp directories on a POSIX host.
const defaultJoinPath: JoinPath = NodePath.join;

/** Parse a JSON file the Construct scripts wrote; null when missing, unreadable or invalid. */
function readJsonFile(path: string, fs: ConstructFileSystem): unknown | null {
  const text = fs.readTextFile(path);
  if (text === null) return null;
  try {
    return JSON.parse(text.replace(UTF8_BOM, ""));
  } catch {
    return null;
  }
}

/** True when `dir` holds an extracted Construct repo (its Auto-Install.ps1 marker). */
export function isConstructScriptsDir(
  dir: string,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): boolean {
  return fs.fileMtimeMs(joinPath(dir, CONSTRUCT_SCRIPTS_MARKER)) !== null;
}

/** Find the newest extracted Construct repo (the folder holding Auto-Install.ps1) under
 *  `<localAppData>\The-Construct`, one or two levels deep — the same rule as the VS Code
 *  panel's host.js findScriptsDir. "Newest" = most recently rewritten marker, which
 *  Expand-Archive -Force refreshes on every install/update. */
export function findConstructScriptsDir(
  localAppData: string | undefined,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): string | null {
  if (!localAppData) return null;
  const container = joinPath(localAppData, CONSTRUCT_CONTAINER_DIR_NAME);
  const candidates: Array<{ dir: string; mtime: number }> = [];
  const consider = (dir: string) => {
    const mtime = fs.fileMtimeMs(joinPath(dir, CONSTRUCT_SCRIPTS_MARKER));
    if (mtime !== null) candidates.push({ dir, mtime });
  };
  for (const level1 of fs.listDirectories(container)) {
    consider(level1);
    for (const level2 of fs.listDirectories(level1)) consider(level2);
  }
  if (candidates.length === 0) return null;
  candidates.sort((a, b) => b.mtime - a.mtime);
  return candidates[0]!.dir;
}

/** The scripts dir that drives the target VM: its pinned `scriptsDir` when that still
 *  is a Construct checkout, else the newest install (host.js resolveScriptsDir minus the
 *  VS Code setting, which the Desktop app cannot read). */
export function resolveConstructScriptsDir(
  localAppData: string | undefined,
  target: ConstructVmTarget,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): string | null {
  if (target.scriptsDir !== null && isConstructScriptsDir(target.scriptsDir, fs, joinPath)) {
    return target.scriptsDir;
  }
  return findConstructScriptsDir(localAppData, fs, joinPath);
}

export function readConstructMarkersFromDir(
  scriptsDir: string,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): ConstructMarkers {
  return readConstructMarkers(readJsonFile(joinPath(scriptsDir, CONSTRUCT_SETTINGS_FILE), fs));
}

// ── The target VM (instance registry) ───────────────────────────────────────────
//
// `%LOCALAPPDATA%\The-Construct\instances.json` names every VM this PC manages and
// which one is the default. Update-T3Code.ps1 reprovisions the DEFAULT VM
// (agent-vm.mshome.net) unless it is handed the target's identity, so a Desktop app
// on a PC whose default instance is a different VM must pass it along — otherwise the
// wrong machine would be rebuilt. This mirrors extension/src/instances.js (which the
// T3 build cannot import): the registry's default instance with the same derived
// defaults and the same acceptance rules — name rule + reserved prefix, per-backend
// canonical identity (a hyperv-local instance MUST be `<name>.mshome.net`, alias
// `<name>`, port 22; a hyperv-remote one MUST state its sshHost and keep vmName ==
// name), host/alias/key-file format rules and cross-entry identity collisions. The
// panel degrades a rejected entry to the default VM silently and toasts the problem;
// here the fallback carries the `problem` so a reprovision is REFUSED rather than sent
// to a VM the user did not pick. An absent or empty registry is the implicit default.

export interface ConstructVmTarget {
  readonly name: string;
  readonly vmHost: string;
  readonly hostAlias: string;
  readonly sshPort: number;
  readonly keyName: string;
  readonly scriptsDir: string | null;
  /** The implicit single-VM default: Update-T3Code.ps1 needs no identity arguments. */
  readonly isDefault: boolean;
  /** Why the registry was ignored (null when it was absent or fully usable). */
  readonly problem: string | null;
}

export const DEFAULT_CONSTRUCT_VM_TARGET: ConstructVmTarget = Object.freeze({
  name: "agent-vm",
  vmHost: "agent-vm.mshome.net",
  hostAlias: "agent-vm",
  sshPort: 22,
  keyName: "agent_vm_ed25519",
  scriptsDir: null,
  isDefault: true,
  problem: null,
});

const INSTANCE_NAME_PATTERN = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
const RESERVED_INSTANCE_NAME_PREFIX = "construct-";
const INSTANCE_BACKENDS = ["hyperv-local", "hyperv-remote"] as const;
// The panel's identity-field FORMAT rules (instances.js), verbatim. An entry breaking one
// is refused whole: half an identity would target some other machine.
/** A DNS host name / FQDN (also matches a dotted IPv4 literal); no trailing dot. */
const HOSTNAME_PATTERN =
  /^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/;
/** The SHAPE an IPv6 literal must have before it is parsed: hex, ':' and '.' only — no
 *  zone id (`%eth0`, which node:net would accept) and no brackets. */
const IPV6_SHAPE_PATTERN = /^[0-9A-Fa-f:.]{2,45}$/;
/** A strict dotted quad — the only IPv4 tail an IPv6-mapped address may carry. */
const IPV4_STRICT_PATTERN =
  /^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$/;
/** An ssh_config alias: one path-free, shell-free token. */
const SAFE_TOKEN_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
/** A key FILE name: the alias character class with a longer bound. */
const KEY_FILE_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const WINDOWS_DEVICE_NAMES = new Set([
  "con",
  "prn",
  "aux",
  "nul",
  ...Array.from({ length: 9 }, (_, i) => `com${i + 1}`),
  ...Array.from({ length: 9 }, (_, i) => `lpt${i + 1}`),
]);
/** The schema's string fields; a present non-string value is a malformed file. */
const INSTANCE_STRING_FIELDS = [
  "vmName",
  "sshHost",
  "vmHost",
  "hostAlias",
  "keyName",
  "configBranch",
  "scriptsDir",
  "owner",
] as const;

function isIpv6Literal(value: string): boolean {
  if (!IPV6_SHAPE_PATTERN.test(value) || !value.includes(":")) return false;
  if (value.includes(".") && !IPV4_STRICT_PATTERN.test(value.slice(value.lastIndexOf(":") + 1))) {
    return false;
  }
  return NodeNet.isIP(value) === 6;
}

function isInstanceHostEndpoint(value: string): boolean {
  return HOSTNAME_PATTERN.test(value) || isIpv6Literal(value);
}

function isSafeToken(value: string): boolean {
  return SAFE_TOKEN_PATTERN.test(value) && !value.includes("..");
}

function isKeyFileName(value: string): boolean {
  if (!KEY_FILE_NAME_PATTERN.test(value) || value.includes("..") || value.endsWith(".")) {
    return false;
  }
  return !WINDOWS_DEVICE_NAMES.has(value.split(".")[0]!.toLowerCase());
}

function stringField(value: unknown): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : null;
}

/** True when a value is present but is NOT a usable string (instances.js badString). */
function isBadString(value: unknown): boolean {
  return value !== null && value !== undefined && value !== "" && typeof value !== "string";
}

/** instances.js coercePort: an integer, or a 1-5 digit string, in 1..65535; else null
 *  (the panel then silently uses the default port). */
function portField(value: unknown): number | null {
  if (typeof value === "number" && Number.isInteger(value) && value > 0 && value <= 65535) {
    return value;
  }
  if (typeof value === "string" && /^\d{1,5}$/.test(value.trim())) {
    const port = Number(value.trim());
    if (port > 0 && port <= 65535) return port;
  }
  return null;
}

/** instances.js backendProblems: omitted/null -> the local default; present but not a
 *  usable string, or misspelled by case -> refuse (the panel skips the entry). A backend
 *  id this build does not know is ACCEPTED here, as in the panel (it stays in the
 *  registry and takes part in collision checks); it is only refused as the selected
 *  target (targetBackendProblem). */
function backendProblem(raw: unknown): string | null {
  if (raw === null || raw === undefined) return null;
  const q = (v: unknown) => JSON.stringify(v);
  if (typeof raw !== "string" || raw.trim().length === 0) {
    return `has a "backend" ${q(raw)} that is not a usable backend id`;
  }
  const value = raw.trim();
  if ((INSTANCE_BACKENDS as ReadonlyArray<string>).includes(value)) return null;
  const canonical = INSTANCE_BACKENDS.find((b) => b === value.toLowerCase());
  if (canonical !== undefined) {
    return `has a "backend" ${q(value)} that is not spelled ${q(canonical)}`;
  }
  return null;
}

/** Update-T3Code.ps1 can only drive the backends this build knows. */
function targetBackendProblem(entry: ConstructInstanceEntry): string | null {
  return (INSTANCE_BACKENDS as ReadonlyArray<string>).includes(entry.backend)
    ? null
    : `has an unknown backend ${JSON.stringify(entry.backend)}`;
}

function ignoredRegistry(problem: string): ConstructVmTarget {
  return { ...DEFAULT_CONSTRUCT_VM_TARGET, problem };
}

interface ConstructInstanceEntry {
  readonly name: string;
  readonly backend: string;
  readonly vmName: string;
  readonly vmHost: string;
  readonly hostAlias: string;
  readonly sshPort: number;
  readonly keyName: string;
  readonly scriptsDir: string | null;
}

/** instances.js deriveDefaults: the normalised entry for `name`, absent fields derived. */
function deriveInstanceEntry(name: string, raw: Record<string, unknown>): ConstructInstanceEntry {
  const isImplicitDefault = name === DEFAULT_CONSTRUCT_VM_TARGET.name;
  return {
    name,
    backend: stringField(raw.backend) ?? "hyperv-local",
    vmName: stringField(raw.vmName) ?? (isImplicitDefault ? "Agent-VM" : name),
    vmHost:
      stringField(raw.sshHost) ??
      stringField(raw.vmHost) ??
      (isImplicitDefault ? DEFAULT_CONSTRUCT_VM_TARGET.vmHost : `${name}.mshome.net`),
    hostAlias:
      stringField(raw.hostAlias) ??
      (isImplicitDefault ? DEFAULT_CONSTRUCT_VM_TARGET.hostAlias : name),
    sshPort: portField(raw.sshPort) ?? DEFAULT_CONSTRUCT_VM_TARGET.sshPort,
    keyName:
      stringField(raw.keyName) ??
      (isImplicitDefault ? DEFAULT_CONSTRUCT_VM_TARGET.keyName : `construct_${name}_ed25519`),
    scriptsDir: stringField(raw.scriptsDir),
  };
}

/** instances.js typed-field check + backendProblems + identityProblems +
 *  localIdentityProblems + remoteIdentityProblems for one entry: the first reason the
 *  panel would refuse it, or null. */
function instanceEntryProblem(
  entry: ConstructInstanceEntry,
  raw: Record<string, unknown>,
): string | null {
  const q = (v: unknown) => JSON.stringify(v);
  for (const field of INSTANCE_STRING_FIELDS) {
    if (isBadString(raw[field])) return `has a "${field}" that is not a string`;
  }
  const backend = backendProblem(raw.backend);
  if (backend !== null) return backend;
  if (!isInstanceHostEndpoint(entry.vmHost)) return `has an unusable sshHost ${q(entry.vmHost)}`;
  if (!isSafeToken(entry.hostAlias)) return `has an unusable hostAlias ${q(entry.hostAlias)}`;
  if (!isKeyFileName(entry.keyName)) return `has an unusable keyName ${q(entry.keyName)}`;
  if (entry.backend === "hyperv-local") {
    const canonical =
      entry.name === DEFAULT_CONSTRUCT_VM_TARGET.name
        ? DEFAULT_CONSTRUCT_VM_TARGET
        : { vmHost: `${entry.name}.mshome.net`, hostAlias: entry.name, sshPort: 22 };
    if (entry.vmHost !== canonical.vmHost) {
      return `is a local Hyper-V instance whose sshHost ${q(entry.vmHost)} is not ${q(canonical.vmHost)}`;
    }
    if (entry.hostAlias !== canonical.hostAlias) {
      return `is a local Hyper-V instance whose hostAlias ${q(entry.hostAlias)} is not ${q(canonical.hostAlias)}`;
    }
    if (entry.sshPort !== canonical.sshPort) {
      return `is a local Hyper-V instance whose sshPort ${entry.sshPort} is not ${canonical.sshPort}`;
    }
    return null;
  }
  if (entry.backend === "hyperv-remote") {
    // The VM lives on a host service; its endpoint must be stated and its VM name is
    // its instance name.
    if (stringField(raw.sshHost) === null && stringField(raw.vmHost) === null) {
      return "is a remote instance without an sshHost";
    }
    if (entry.vmName !== entry.name) {
      return `is a remote instance whose vmName ${q(entry.vmName)} is not its name`;
    }
  }
  return null;
}

/** The registry's default instance, accepted only when the panel would accept it. */
export function readConstructVmTarget(raw: unknown): ConstructVmTarget {
  if (raw === null || raw === undefined) return DEFAULT_CONSTRUCT_VM_TARGET;
  if (typeof raw !== "object" || Array.isArray(raw)) {
    return ignoredRegistry("instances.json is not a JSON object; using the default VM.");
  }
  const doc = raw as Record<string, unknown>;
  if (doc.version !== undefined && doc.version !== null && doc.version !== 1) {
    return ignoredRegistry(
      `instances.json has version ${JSON.stringify(doc.version)}; this build only understands version 1, using the default VM.`,
    );
  }
  const defaultName = stringField(doc.defaultInstance) ?? DEFAULT_CONSTRUCT_VM_TARGET.name;
  if (
    !INSTANCE_NAME_PATTERN.test(defaultName) ||
    defaultName.startsWith(RESERVED_INSTANCE_NAME_PREFIX)
  ) {
    return ignoredRegistry(
      `instances.json names an invalid default instance ${JSON.stringify(defaultName)}; using the default VM.`,
    );
  }
  const bag =
    typeof doc.instances === "object" && doc.instances !== null && !Array.isArray(doc.instances)
      ? (doc.instances as Record<string, unknown>)
      : {};
  // Like parseRegistry: every entry is validated on its own first and a rejected one
  // is SKIPPED (the panel toasts it), so it neither becomes the target nor takes part
  // in the collision check below. Only the selected entry's own reason is reported.
  const entries = new Map<string, ConstructInstanceEntry>();
  let defaultEntryProblem: string | null = null;
  for (const name of Object.keys(bag)) {
    const rawEntry = bag[name];
    if (
      !INSTANCE_NAME_PATTERN.test(name) ||
      name.startsWith(RESERVED_INSTANCE_NAME_PREFIX) ||
      typeof rawEntry !== "object" ||
      rawEntry === null ||
      Array.isArray(rawEntry)
    ) {
      if (name === defaultName) defaultEntryProblem = "is not an object";
      continue;
    }
    const entry = deriveInstanceEntry(name, rawEntry as Record<string, unknown>);
    const problem = instanceEntryProblem(entry, rawEntry as Record<string, unknown>);
    if (problem !== null) {
      if (name === defaultName) defaultEntryProblem = problem;
      continue;
    }
    entries.set(name, entry);
  }
  if (defaultEntryProblem !== null) {
    return ignoredRegistry(
      `instances.json: instance "${defaultName}" ${defaultEntryProblem}; using the default VM.`,
    );
  }
  const isImplicitDefault = defaultName === DEFAULT_CONSTRUCT_VM_TARGET.name;
  if (!entries.has(defaultName)) {
    // The panel resolves an unknown name to the default instance. That is only safe to
    // act on when the default IS the implicit one.
    if (isImplicitDefault) return DEFAULT_CONSTRUCT_VM_TARGET;
    return ignoredRegistry(
      `instances.json names ${JSON.stringify(defaultName)} as the default instance but has no such entry; using the default VM.`,
    );
  }
  const entry = entries.get(defaultName)!;
  const backendProblemOfTarget = targetBackendProblem(entry);
  if (backendProblemOfTarget !== null) {
    return ignoredRegistry(
      `instances.json: instance "${defaultName}" ${backendProblemOfTarget}; using the default VM.`,
    );
  }
  // Cross-entry collisions (instances.js collisionProblems) among the ACCEPTED entries:
  // another entry claiming the same endpoint or alias makes the registry ambiguous
  // about which VM is meant.
  for (const other of entries.values()) {
    if (other.name === entry.name) continue;
    if (
      other.vmHost.toLowerCase() === entry.vmHost.toLowerCase() &&
      other.sshPort === entry.sshPort
    ) {
      return ignoredRegistry(
        `instances.json: instances "${entry.name}" and "${other.name}" share the endpoint ${entry.vmHost}:${entry.sshPort}; using the default VM.`,
      );
    }
    if (other.hostAlias.toLowerCase() === entry.hostAlias.toLowerCase()) {
      return ignoredRegistry(
        `instances.json: instances "${entry.name}" and "${other.name}" share the ssh alias "${entry.hostAlias}"; using the default VM.`,
      );
    }
  }
  const isDefault =
    entry.name === DEFAULT_CONSTRUCT_VM_TARGET.name &&
    entry.vmHost === DEFAULT_CONSTRUCT_VM_TARGET.vmHost &&
    entry.hostAlias === DEFAULT_CONSTRUCT_VM_TARGET.hostAlias &&
    entry.sshPort === DEFAULT_CONSTRUCT_VM_TARGET.sshPort &&
    entry.keyName === DEFAULT_CONSTRUCT_VM_TARGET.keyName;
  return {
    name: entry.name,
    vmHost: entry.vmHost,
    hostAlias: entry.hostAlias,
    sshPort: entry.sshPort,
    keyName: entry.keyName,
    scriptsDir: entry.scriptsDir,
    isDefault,
    problem: null,
  };
}

export function readConstructVmTargetFromRegistry(
  localAppData: string | undefined,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): ConstructVmTarget {
  if (!localAppData) return DEFAULT_CONSTRUCT_VM_TARGET;
  const path = joinPath(localAppData, CONSTRUCT_CONTAINER_DIR_NAME, CONSTRUCT_INSTANCES_FILE);
  const text = fs.readTextFile(path);
  if (text === null) return DEFAULT_CONSTRUCT_VM_TARGET;
  if (text.replace(UTF8_BOM, "").trim().length === 0) return DEFAULT_CONSTRUCT_VM_TARGET;
  const parsed = readJsonFile(path, fs);
  if (parsed === null) {
    return ignoredRegistry("instances.json is not valid JSON; using the default VM.");
  }
  return readConstructVmTarget(parsed);
}

// ── Remote checks ───────────────────────────────────────────────────────────────

export interface ConstructJsonResponse {
  readonly status: number;
  readonly json: unknown;
}

/** GET a JSON document. Resolves null on ANY network-level problem (offline, timeout,
 *  unparsable body); non-2xx statuses are returned so callers can tell a 404 apart. */
export type ConstructFetchJson = (url: string) => Promise<ConstructJsonResponse | null>;

export const fetchConstructJson: ConstructFetchJson = async (url) => {
  try {
    const response = await fetch(url, {
      headers: {
        "User-Agent": "construct-t3code-desktop",
        Accept: url.includes("registry.npmjs.org")
          ? "application/json"
          : "application/vnd.github+json",
      },
      signal: AbortSignal.timeout(8_000),
    });
    if (response.status === 404) return { status: 404, json: null };
    if (!response.ok) return { status: response.status, json: null };
    return { status: response.status, json: await response.json() };
  } catch {
    return null;
  }
};

export interface ConstructCompareResult {
  readonly available: boolean;
  /** Commits behind; null when the distance is unknown. */
  readonly behind: number | null;
}

/** Shape a GitHub compare response (base = installed ... head = ref). A 404 means the
 *  installed commit no longer exists on the remote (history rewrite / force-push): the
 *  only sane offer is "update", with no distance — the panel does the same. */
export function constructUpdateFromCompare(
  response: ConstructJsonResponse | null,
): ConstructCompareResult | null {
  if (response === null) return null;
  if (response.status === 404) return { available: true, behind: null };
  const json = response.json;
  if (typeof json !== "object" || json === null) return null;
  const aheadBy = (json as { ahead_by?: unknown }).ahead_by;
  if (typeof aheadBy !== "number" || !Number.isFinite(aheadBy)) return null;
  return { available: aheadBy > 0, behind: aheadBy };
}

/** The version string of an npm dist-tag manifest (`registry.npmjs.org/t3/<tag>`). */
export function constructT3VersionFromRegistry(
  response: ConstructJsonResponse | null,
): string | null {
  if (response === null || typeof response.json !== "object" || response.json === null) {
    return null;
  }
  const version = (response.json as { version?: unknown }).version;
  return typeof version === "string" && parseSemver(version) !== null ? version.trim() : null;
}

/** True when `latest` is a strictly newer release than `installed` on the same channel.
 *  Nightly builds share a semver core across days and differ in the prerelease
 *  (`0.0.39-nightly.20260901.1` vs `.20260902.1`); the shared semver compare orders
 *  numeric prerelease segments, so both channels go through it. Unparseable -> false. */
export function isNewerConstructT3Version(
  latest: string | null,
  installed: string,
  channel: DesktopUpdateChannel,
): boolean {
  if (latest === null || parseSemver(latest) === null || parseSemver(installed) === null) {
    return false;
  }
  const latestIsNightly = resolveConstructT3Channel(latest) === "nightly";
  if ((channel === "nightly") !== latestIsNightly) return false;
  return compareSemverVersions(latest, installed) > 0;
}

// ── Derivation ──────────────────────────────────────────────────────────────────

export interface ConstructCheckSnapshot {
  readonly scriptsDir: string | null;
  readonly markers: ConstructMarkers;
  readonly target: ConstructVmTarget;
  readonly compare: ConstructCompareResult | null;
  readonly t3Version: string;
  readonly t3LatestVersion: string | null;
  readonly channel: DesktopUpdateChannel;
  readonly runningAction: ConstructUpdateAction | null;
  readonly checkedAt: string | null;
  readonly error: string | null;
}

/** Which script the update control launches. A Construct update comes first: updating
 *  the PC's Construct and then reprovisioning applies both; the reverse order would
 *  provision the VM with scripts that are about to change again. */
export function resolveConstructAction(input: {
  readonly scriptsDir: string | null;
  readonly constructUpdateAvailable: boolean;
  readonly provisionStale: boolean;
  readonly t3UpdateAvailable: boolean;
}): ConstructUpdateAction | null {
  if (input.scriptsDir === null) return null;
  if (input.constructUpdateAvailable) return "update-construct";
  if (input.provisionStale || input.t3UpdateAvailable) return "reprovision";
  return null;
}

export function deriveConstructUpdateInfo(snapshot: ConstructCheckSnapshot): ConstructUpdateInfo {
  const constructUpdateAvailable = snapshot.compare?.available === true;
  const provisionStale = isConstructProvisionStale(snapshot.markers);
  const t3UpdateAvailable = isNewerConstructT3Version(
    snapshot.t3LatestVersion,
    snapshot.t3Version,
    snapshot.channel,
  );
  return {
    repo: snapshot.markers.repo,
    ref: snapshot.markers.ref,
    scriptsDir: snapshot.scriptsDir,
    vmName: snapshot.target.name,
    vmHost: snapshot.target.vmHost,
    installedCommit: snapshot.markers.installedCommit,
    provisionedCommit: snapshot.markers.provisionedCommit,
    behind: snapshot.compare?.behind ?? null,
    constructUpdateAvailable,
    provisionStale,
    t3Version: snapshot.t3Version,
    t3LatestVersion: snapshot.t3LatestVersion,
    t3UpdateAvailable,
    action: resolveConstructAction({
      scriptsDir: snapshot.scriptsDir,
      constructUpdateAvailable,
      provisionStale,
      t3UpdateAvailable,
    }),
    runningAction: snapshot.runningAction,
    checkedAt: snapshot.checkedAt,
    error: snapshot.error,
  };
}

export function shortConstructCommit(commit: string | null): string | null {
  return commit === null ? null : commit.slice(0, 7);
}

/** The `availableVersion` label stock UI code prints ("Update <x> ready to download"). */
export function constructAvailableVersionLabel(info: ConstructUpdateInfo): string | null {
  const action = info.runningAction ?? info.action;
  if (action === "update-construct") {
    return info.behind !== null && info.behind > 0
      ? `Construct ${info.ref} (${info.behind} commit${info.behind === 1 ? "" : "s"} behind)`
      : `Construct ${info.ref}`;
  }
  if (action === "reprovision") {
    if (info.t3UpdateAvailable && info.t3LatestVersion !== null) {
      return `T3 Code ${info.t3LatestVersion}`;
    }
    const installed = shortConstructCommit(info.installedCommit);
    return installed === null ? "Construct reprovision" : `Construct ${info.ref}@${installed}`;
  }
  return null;
}

/** Fold Construct tracking into the desktop update state the renderer consumes. Stock
 *  fields are set so the sidebar pill / About section behave without knowing about
 *  Construct: `available` lights the pill, `downloading` (indeterminate) marks a running
 *  script, `error` a failed check with nothing to offer. */
export function applyConstructInfoToState(
  state: DesktopUpdateState,
  info: ConstructUpdateInfo,
): DesktopUpdateState {
  const base: DesktopUpdateState = {
    ...state,
    enabled: true,
    downloadedVersion: null,
    releaseNotes: [],
    checkedAt: info.checkedAt,
    construct: info,
  };
  if (info.runningAction !== null) {
    return {
      ...base,
      status: "downloading",
      availableVersion: constructAvailableVersionLabel(info),
      downloadPercent: null,
      message: null,
      errorContext: null,
      canRetry: false,
    };
  }
  if (info.action !== null) {
    return {
      ...base,
      status: "available",
      availableVersion: constructAvailableVersionLabel(info),
      downloadPercent: null,
      message: null,
      errorContext: null,
      canRetry: false,
    };
  }
  if (info.error !== null) {
    return {
      ...base,
      status: "error",
      availableVersion: null,
      downloadPercent: null,
      message: info.error,
      errorContext: "check",
      canRetry: true,
    };
  }
  return {
    ...base,
    status: "up-to-date",
    availableVersion: null,
    downloadPercent: null,
    message: null,
    errorContext: null,
    canRetry: false,
  };
}

// ── The check ───────────────────────────────────────────────────────────────────

export interface ConstructCheckOptions {
  readonly appVersion: string;
  readonly localAppData: string | undefined;
  readonly fs: ConstructFileSystem;
  /** Omit for a LOCAL-only refresh (markers re-read, remote results carried over
   *  from `previous`) — used while a launched script runs, so a reprovision that
   *  takes half an hour doesn't burn GitHub's unauthenticated rate limit. */
  readonly fetchJson?: ConstructFetchJson;
  readonly previous: ConstructUpdateInfo | null;
  readonly runningAction: ConstructUpdateAction | null;
  readonly now: () => string;
  readonly joinPath?: JoinPath;
}

export function constructScriptsMissingMessage(localAppData: string | undefined): string {
  const container = `${localAppData ?? "%LOCALAPPDATA%"}\\${CONSTRUCT_CONTAINER_DIR_NAME}`;
  return `Construct is not installed on this PC (no ${CONSTRUCT_SCRIPTS_MARKER} under ${container}). Install or update Construct, then check again.`;
}

export async function checkConstructUpdates(
  options: ConstructCheckOptions,
): Promise<ConstructUpdateInfo> {
  const joinPath = options.joinPath ?? defaultJoinPath;
  const t3Version = constructT3BaseVersion(options.appVersion);
  const channel = resolveConstructT3Channel(t3Version);
  const target = readConstructVmTargetFromRegistry(options.localAppData, options.fs, joinPath);
  const scriptsDir = resolveConstructScriptsDir(options.localAppData, target, options.fs, joinPath);
  const markers =
    scriptsDir === null
      ? readConstructMarkers({})
      : readConstructMarkersFromDir(scriptsDir, options.fs, joinPath);
  const previous = options.previous;
  const errors: string[] = [];
  if (scriptsDir === null) errors.push(constructScriptsMissingMessage(options.localAppData));
  if (target.problem !== null) errors.push(target.problem);

  // Remote results: fresh when a fetcher is given, else carried over from the previous
  // check as long as they still describe the same installed commit / channel.
  let compare: ConstructCompareResult | null = null;
  let t3LatestVersion: string | null = null;
  if (options.fetchJson) {
    const compareUrl = constructCompareUrl(markers);
    if (compareUrl !== null) {
      compare = constructUpdateFromCompare(await options.fetchJson(compareUrl));
      if (compare === null) errors.push("Could not check GitHub for Construct updates.");
    }
    t3LatestVersion = constructT3VersionFromRegistry(
      await options.fetchJson(constructT3RegistryUrl(channel)),
    );
    if (t3LatestVersion === null) errors.push("Could not check npm for T3 Code releases.");
  } else if (previous !== null) {
    if (previous.installedCommit === markers.installedCommit && previous.installedCommit !== null) {
      compare = { available: previous.constructUpdateAvailable, behind: previous.behind };
    }
    t3LatestVersion = previous.t3LatestVersion;
    if (previous.error !== null && scriptsDir !== null && target.problem === null) {
      errors.push(previous.error);
    }
  }

  return deriveConstructUpdateInfo({
    scriptsDir,
    markers,
    target,
    compare,
    t3Version,
    t3LatestVersion,
    channel,
    runningAction: options.runningAction,
    checkedAt: options.now(),
    error: errors.length > 0 ? errors.join(" ") : null,
  });
}

// ── Launching the host scripts ──────────────────────────────────────────────────

export interface ConstructLaunchPlan {
  readonly action: ConstructUpdateAction;
  readonly scriptPath: string;
  readonly command: string;
  readonly args: ReadonlyArray<string>;
  /** The full command line (Node passes it through verbatim on Windows). */
  readonly windowsVerbatimArguments: true;
}

export type ConstructLaunchPlanResult =
  | { readonly ok: true; readonly plan: ConstructLaunchPlan }
  | { readonly ok: false; readonly error: string };

export function constructScriptFileName(action: ConstructUpdateAction): string {
  return action === "update-construct" ? CONSTRUCT_UPDATE_SCRIPT : CONSTRUCT_REPROVISION_SCRIPT;
}

/**
 * Does the INSTALLED Update-T3Code.ps1 declare `$InstanceName` — i.e. does it support
 * NAME-ONLY TARGETING (B11, plan §4.12)? The same comment-stripped declaration test the
 * control panel uses (extension/src/lifecycle.js scriptSupportsParam).
 *
 * The parameter probe is honest HERE, unlike in the panel: `-InstanceName` never had any
 * other meaning on this script, so declaring it can only mean the new one. An unreadable
 * or absent script answers false and the caller falls back to the four identity
 * arguments, which every version since B1 understands.
 */
export function constructSupportsInstanceName(
  scriptsDir: string,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): boolean {
  const text = fs.readTextFile(joinPath(scriptsDir, CONSTRUCT_REPROVISION_SCRIPT));
  if (text === null) return false;
  const code = text.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
  return /\$InstanceName\s*(?:=|,|\)|$)/im.test(code);
}

/** The identity arguments Update-T3Code.ps1 forwards to the provisioner. None for the
 *  implicit default VM (an older provisioner then keeps working). For anything else:
 *  `-InstanceName <name>` when the installed script can resolve a name (it then reads
 *  the endpoint, alias, port and key file out of the same registry this module parsed),
 *  and otherwise all four identity arguments — either way the reprovision can never land
 *  on the default VM by accident. Every value passed the registry's format rules, which
 *  exclude spaces and shell metacharacters. */
export function constructReprovisionIdentityArgs(
  target: ConstructVmTarget,
  supportsInstanceName = false,
): ReadonlyArray<string> {
  if (target.isDefault) return [];
  if (supportsInstanceName) return ["-InstanceName", `"${target.name}"`];
  return [
    "-VmHost",
    `"${target.vmHost}"`,
    "-HostAlias",
    `"${target.hostAlias}"`,
    "-SshPort",
    String(target.sshPort),
    "-LocalKeyName",
    `"${target.keyName}"`,
  ];
}

/**
 * Build the command that runs a Construct script in a VISIBLE console window.
 *
 * Electron's main process has no console. libuv gives a `detached` child
 * DETACHED_PROCESS (no console at all) and `stdio: "ignore"` points the std handles at
 * NUL, so spawning powershell.exe directly runs the multi-minute reprovision invisibly.
 * `cmd.exe /c start /wait` creates a fresh console for PowerShell with working
 * stdin/stdout, and cmd itself stays hidden and exits when PowerShell does — which is how
 * the caller learns the script finished.
 */
export function planConstructLaunch(
  action: ConstructUpdateAction,
  info: Pick<ConstructUpdateInfo, "scriptsDir" | "repo" | "ref">,
  target: ConstructVmTarget,
  platform: NodeJS.Platform,
  fs: ConstructFileSystem,
  joinPath: JoinPath = defaultJoinPath,
): ConstructLaunchPlanResult {
  if (platform !== "win32") {
    return {
      ok: false,
      error: "Construct updates can only be launched from the Windows Desktop app.",
    };
  }
  if (info.scriptsDir === null) {
    return { ok: false, error: constructScriptsMissingMessage(undefined) };
  }
  const scriptPath = joinPath(info.scriptsDir, constructScriptFileName(action));
  if (fs.fileMtimeMs(scriptPath) === null) {
    return {
      ok: false,
      error: `${constructScriptFileName(action)} was not found in ${info.scriptsDir}. Update Construct from the VS Code control panel first.`,
    };
  }
  if (UNSAFE_COMMAND_LINE_CHARACTER.test(scriptPath)) {
    return {
      ok: false,
      error: `Refusing to launch a script from a path with shell metacharacters: ${scriptPath}`,
    };
  }
  if (!REPO_PATTERN.test(info.repo) || !REF_PATTERN.test(info.ref)) {
    return {
      ok: false,
      error: `Refusing to launch with an invalid Construct source: ${info.repo}@${info.ref}`,
    };
  }
  if (action === "reprovision" && target.problem !== null) {
    return {
      ok: false,
      error: `${target.problem} Fix instances.json (or reprovision from the VS Code control panel) before reprovisioning from here.`,
    };
  }
  const title = action === "update-construct" ? "Construct update" : "Construct reprovision";
  const scriptArgs =
    action === "update-construct"
      ? ["-Repo", `"${info.repo}"`, "-Ref", `"${info.ref}"`]
      : constructReprovisionIdentityArgs(
          target,
          constructSupportsInstanceName(info.scriptsDir, fs, joinPath),
        );
  const powershell = [
    "powershell.exe",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    `"${scriptPath}"`,
    ...scriptArgs,
  ].join(" ");
  return {
    ok: true,
    plan: {
      action,
      scriptPath,
      command: "cmd.exe",
      // /d: no AutoRun; /s: strip the outer quotes and run the rest verbatim.
      args: ["/d", "/s", "/c", `"start "${title}" /wait ${powershell}"`],
      windowsVerbatimArguments: true,
    },
  };
}

export interface ConstructLaunchHandle {
  readonly pid: number | undefined;
}

/** Spawn the planned command. `onExit` fires once, when the console session ends (or the
 *  spawn itself fails). Returns null when spawning threw synchronously. */
export function spawnConstructLaunch(
  plan: ConstructLaunchPlan,
  onExit: (result: { readonly code: number | null; readonly error: string | null }) => void,
  spawn: typeof NodeChildProcess.spawn = NodeChildProcess.spawn,
): ConstructLaunchHandle | null {
  let settled = false;
  const settle = (result: { readonly code: number | null; readonly error: string | null }) => {
    if (settled) return;
    settled = true;
    onExit(result);
  };
  try {
    const child = spawn(plan.command, [...plan.args], {
      windowsVerbatimArguments: true,
      windowsHide: true,
      stdio: "ignore",
    });
    child.on("error", (error) => settle({ code: null, error: error.message }));
    child.on("exit", (code) => settle({ code, error: null }));
    return { pid: child.pid };
  } catch (error) {
    settle({ code: null, error: error instanceof Error ? error.message : String(error) });
    return null;
  }
}
