"use strict";
// Open the agent VM over VS Code Remote-SSH from the control panel.
//
// The panel runs as a UI extension on the local host; opening a folder on the VM
// is done with `vscode.openFolder` and a `vscode-remote://ssh-remote+<alias>/<path>`
// URI (the `agent-vm` SSH Host alias the provisioner writes). The window's current
// remote is read from `vscode.env.remoteAuthority` to decide whether we're already
// on the VM (so the Connect button can hide). `vscode` is lazy-required so the pure
// helpers unit-test under plain node.

const ssh = require("./ssh");

function vsc() { return require("vscode"); }

const REMOTE_SSH_EXT = "ms-vscode-remote.remote-ssh";
const WORKSPACE_ROOT = "/root/repos"; // WORKSPACE_ROOT in bin/provision.sh

/**
 * Is the current window already Remote-SSH'd into THIS VM? Pure: takes
 * `vscode.env.remoteAuthority` (e.g. "ssh-remote+agent-vm") and matches the host
 * after the "+" against the configured alias / hostname (case-insensitive). A
 * non-ssh-remote authority, a local window (empty), or a different host -> false.
 */
function isConnectedToVm(remoteAuthority, cfg) {
  if (!remoteAuthority) return false;
  const m = /^ssh-remote\+(.+)$/i.exec(String(remoteAuthority));
  if (!m) return false;
  const c = ssh.resolveCfg({ cfg });
  const host = m[1].toLowerCase();
  return host === String(c.hostAlias).toLowerCase() || host === String(c.vmHost).toLowerCase();
}

/** Build the `vscode-remote://ssh-remote+<alias><path>` URI string for a VM path. */
function remoteFolderUri(cfg, posixPath) {
  const c = ssh.resolveCfg({ cfg });
  const p = String(posixPath || WORKSPACE_ROOT);
  return `vscode-remote://ssh-remote+${c.hostAlias}${p.startsWith("/") ? p : "/" + p}`;
}

/**
 * Derive the clone-target directory name from a git URL, mirroring
 * bin/checkout-projects.sh: the last path segment with a trailing ".git" removed.
 * Handles scheme URLs (https/ssh/git) and scp-like `git@host:owner/repo`. A
 * ?query / #fragment is dropped first, so the derived name (and thus the folder we
 * open after cloning) can't pick up URI-significant chars that would make
 * vscode.Uri.parse open a different path than where the clone actually landed.
 * Returns "" when nothing usable can be derived. Pure.
 */
function repoNameFromUrl(url) {
  let s = String(url || "").trim();
  if (!s) return "";
  s = s.replace(/[?#].*$/, "");                // drop any ?query / #fragment first
  s = s.replace(/[\/\\]+$/, "");               // drop trailing slashes
  const seg = s.split(/[\/\\:]/).pop() || "";  // last segment past / \ or :
  return seg.replace(/\.git$/i, "").trim();
}

/**
 * Loose sanity check that a string looks like a git remote URL we can clone: a
 * scheme URL (https/http/ssh/git) or scp-like user@host:path. NOT a security
 * control — the URL is always handed to `git clone --` as DATA (base64), never
 * interpolated into the shell — just a guard so the input box rejects nonsense. Pure.
 */
function isLikelyGitUrl(url) {
  const s = String(url || "").trim();
  if (!s) return false;
  if (/^(https?|ssh|git):\/\/[^\s]+$/i.test(s)) return true; // scheme://host/path
  if (/^[^@\s]+@[^:\s]+:[^\s]+$/.test(s)) return true;        // scp-like user@host:path
  return false;
}

/**
 * Build the injection-safe remote bash script that clones `url` into
 * `<root>/<dest>` on the VM. The URL and dest are base64-encoded and decoded ON
 * the VM (never interpolated into the script text), then passed to `git clone --`
 * as data, so neither can break out of the shell or be read as an option. Fails
 * with exit 3 if the target already exists (caller offers to open it instead).
 * Pure. (ssh.runRemoteScript base64s the whole script again for transport.)
 */
function buildCloneScript(url, dest, root) {
  const enc = (s) => Buffer.from(String(s), "utf8").toString("base64"); // base64 is single-quote-safe
  const r = root || WORKSPACE_ROOT;
  // Trim the URL defensively: surrounding whitespace is never meaningful in a git
  // URL and would make `git clone` fail, so it must never reach git regardless of
  // what the caller passed (repoNameFromUrl already trims, so the name stays consistent).
  return [
    "set -u",
    "root='" + r + "'",
    "url=$(printf %s '" + enc(String(url).trim()) + "' | base64 -d)",
    "dest=$(printf %s '" + enc(dest) + "' | base64 -d)",
    'mkdir -p "$root"',
    'target="$root/$dest"',
    'if [ -e "$target" ]; then printf "EXISTS\\t%s\\n" "$target" >&2; exit 3; fi',
    'git clone -- "$url" "$target"',
  ].join("\n");
}

/** Whether the Remote-SSH extension (needed to resolve the authority) is installed. */
function hasRemoteSsh() {
  const vscode = vsc();
  return !!(vscode.extensions && vscode.extensions.getExtension(REMOTE_SSH_EXT));
}

/**
 * Open a folder on the VM over Remote-SSH. `opts`: { path=/root/repos, newWindow,
 * cfg }. Warns (and returns false) if the Remote-SSH extension isn't installed.
 */
function openOnVm(opts = {}) {
  const vscode = vsc();
  if (!hasRemoteSsh()) {
    vscode.window.showWarningMessage(
      "The Remote-SSH extension (ms-vscode-remote.remote-ssh) isn't installed, so the Construct VM can't be opened here. Install it and try again."
    );
    return false;
  }
  const uri = vscode.Uri.parse(remoteFolderUri(opts.cfg, opts.path || WORKSPACE_ROOT));
  vscode.commands.executeCommand("vscode.openFolder", uri, { forceNewWindow: !!opts.newWindow });
  return true;
}

module.exports = {
  REMOTE_SSH_EXT, WORKSPACE_ROOT, isConnectedToVm, remoteFolderUri, hasRemoteSsh, openOnVm,
  repoNameFromUrl, isLikelyGitUrl, buildCloneScript,
};
