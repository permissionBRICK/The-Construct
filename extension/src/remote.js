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

module.exports = { REMOTE_SSH_EXT, WORKSPACE_ROOT, isConnectedToVm, remoteFolderUri, hasRemoteSsh, openOnVm };
