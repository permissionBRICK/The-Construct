"use strict";
// The offer to register the open workspace as a project. The old extension imported every repo
// it found under /root/repos on the VM whose origin no profile named (planImport: an origin URL,
// not covered by a profile's repos[].url, no profile of that name in any case). The new one asks
// once per folder instead, and writes through the engine's profile route. No vscode here.

const WORKSPACE_ROOT = "/root/repos"; // WORKSPACE_ROOT in bin/provision.sh
const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;
const RESERVED = new Set(["default", "project.schema"]); // RESERVED_PROFILE_NAMES in src/projects.js

/** The SSH alias of a Remote-SSH window ("ssh-remote+<alias>"), or "". */
function sshAlias(remoteAuthority) {
  const m = /^ssh-remote\+(.+)$/i.exec(String(remoteAuthority || ""));
  return m ? m[1] : "";
}

/** The checkout directory of a folder directly under /root/repos, or "". */
function checkoutName(folderPath) {
  const m = /^\/root\/repos\/([^/]+)\/?$/.exec(String(folderPath || ""));
  return m ? m[1] : "";
}

/** The `origin` remote's url from the text of a .git/config, or "". */
function originUrl(gitConfig) {
  let inOrigin = false;
  for (const raw of String(gitConfig || "").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#") || line.startsWith(";")) continue;
    const section = /^\[\s*([A-Za-z0-9.-]+)(?:\s+"((?:[^"\\]|\\.)*)")?\s*\]$/.exec(line);
    if (section) { inOrigin = section[1].toLowerCase() === "remote" && section[2] === "origin"; continue; }
    if (!inOrigin) continue;
    const kv = /^url\s*=\s*(.*)$/i.exec(line);
    if (kv) {
      let v = kv[1].trim();
      if (v.startsWith('"') && v.endsWith('"') && v.length >= 2) v = v.slice(1, -1);
      return v;
    }
  }
  return "";
}

/** The VM of the user's connections whose SSH alias this window is attached to, or null. */
function attachedVm(connections, alias) {
  if (!alias || !Array.isArray(connections)) return null;
  const lower = alias.toLowerCase();
  const c = connections.find(x => x && typeof x.alias === "string" && x.alias.toLowerCase() === lower);
  return c && typeof c.engine === "string" && typeof c.vm === "string" ? { engine: c.engine, vm: c.vm } : null;
}

/**
 * Whether to offer registering a folder. `profiles` is [{name, profile}] as the engine returns
 * them; `dismissed` the origin URLs the user said never to ask about. Answers {offer: true, name,
 * url} or {offer: false, reason}.
 */
function planOffer({ vm, name, url, profiles, dismissed }) {
  if (!vm) return { offer: false, reason: "This window is not attached to one of your Construct VMs." };
  if (!name) return { offer: false, reason: `The folder is not a checkout directly under ${WORKSPACE_ROOT}.` };
  const u = String(url || "").trim();
  if (!u) return { offer: false, reason: "The folder has no origin remote, so a reprovision could not clone it." };
  if (!NAME_RE.test(name) || RESERVED.has(name.toLowerCase())) return { offer: false, reason: `"${name}" cannot be a profile name.` };
  for (const p of profiles || []) {
    const repos = p && p.profile && Array.isArray(p.profile.repos) ? p.profile.repos : [];
    if (repos.some(r => r && typeof r.url === "string" && r.url.trim() === u)) return { offer: false, reason: `The profile ${p.name} already has this repository.` };
  }
  if ((profiles || []).some(p => p && typeof p.name === "string" && p.name.toLowerCase() === name.toLowerCase())) {
    return { offer: false, reason: `A profile named ${name} exists already.` };
  }
  if ((dismissed || []).includes(u)) return { offer: false, reason: "You asked not to be offered this repository." };
  return { offer: true, name, url: u };
}

/** The new profile, as the old import wrote it (buildDiscoveredProfile). */
function newProfile(name, url) {
  return { name, repos: [{ url, directory: name }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} };
}

/** The VM's desired state with the profile switched on, or null when there is nothing to change. */
function withProject(desired, name) {
  if (!desired || typeof desired !== "object" || !Array.isArray(desired.projects)) return null;
  if (desired.projects.includes(name)) return null;
  return { ...desired, projects: [...desired.projects, name] };
}

module.exports = { WORKSPACE_ROOT, sshAlias, checkoutName, originUrl, attachedVm, planOffer, newProfile, withProject };
