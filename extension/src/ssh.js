"use strict";
// Run commands on the agent VM over SSH from the host. Mirrors the connection
// logic the provisioner/Get-AgentUsage.ps1 use: prefer the explicit key written
// to ~/.ssh/<keyName>, otherwise fall back to the ~/.ssh/config Host alias (which
// points at the same key). Works on Windows (ssh.exe), macOS and Linux.

const { spawn } = require("child_process");
const os = require("os");
const path = require("path");
const fs = require("fs");

const DEFAULTS = {
  vmHost: "agent-vm.mshome.net",
  hostAlias: "agent-vm",
  user: "root",
  keyName: "agent_vm_ed25519",
  connectTimeout: 12,
};

function keyPath(cfg) {
  return path.join(os.homedir(), ".ssh", cfg.keyName);
}

/**
 * Build the argv for `ssh`. Pure (takes `hasKey` rather than touching the disk)
 * so it can be unit-tested. The remote command is passed as a single trailing
 * argument; ssh hands it to the remote login shell.
 */
function buildSshArgs(cfg, remoteCommand, hasKey) {
  const common = [
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${cfg.connectTimeout}`,
  ];
  if (hasKey) {
    return ["-i", keyPath(cfg), "-o", "IdentitiesOnly=yes", ...common, `${cfg.user}@${cfg.vmHost}`, remoteCommand];
  }
  return [...common, cfg.hostAlias, remoteCommand];
}

function resolveCfg(opts) {
  return { ...DEFAULTS, ...((opts && opts.cfg) || {}) };
}

// Cap captured output so a chatty/streaming/compromised VM can't grow host memory
// without bound. The probe output is tiny, so truncation is harmless here.
const MAX_OUT = 4 * 1024 * 1024;

/** Run a single remote command. Never rejects; resolves {code, stdout, stderr}. */
function runRemote(remoteCommand, opts = {}) {
  const cfg = resolveCfg(opts);
  const args = buildSshArgs(cfg, remoteCommand, fs.existsSync(keyPath(cfg)));
  return new Promise((resolve) => {
    let stdout = "", stderr = "", done = false, child = null, killTimer = null;
    const detach = () => {
      if (!child) return;
      try { child.stdout && child.stdout.removeAllListeners("data"); } catch (_) {}
      try { child.stderr && child.stderr.removeAllListeners("data"); } catch (_) {}
    };
    const finish = (code) => { if (done) return; done = true; clearTimeout(timeoutTimer); detach(); resolve({ code, stdout, stderr }); };
    const timeoutTimer = setTimeout(() => {
      // Best-effort reap of the local ssh process: SIGTERM, then SIGKILL if it
      // ignores it. (ssh without a pty can't forward the signal to the remote
      // command, but the probe script is short-lived and self-cleans its tmpfile.)
      try { child && child.kill("SIGTERM"); } catch (_) {}
      killTimer = setTimeout(() => { try { child && child.kill("SIGKILL"); } catch (_) {} }, 2000);
      finish(-2);
    }, opts.timeoutMs || 20000);
    try {
      child = spawn("ssh", args, { windowsHide: true });
    } catch (e) {
      return finish(-1);
    }
    child.stdout.on("data", (d) => { if (stdout.length < MAX_OUT) stdout += d.toString(); });
    child.stderr.on("data", (d) => { if (stderr.length < MAX_OUT) stderr += d.toString(); });
    child.on("error", (e) => { stderr += String(e); finish(-1); });
    child.on("close", (code) => { if (killTimer) clearTimeout(killTimer); finish(code == null ? -1 : code); });
  });
}

/**
 * Run a multi-line bash script remotely. The script is base64-encoded and decoded
 * on the VM so no quoting/encoding survives the SSH/shell layers (same trick as
 * Get-AgentUsage.ps1).
 */
function runRemoteScript(scriptText, opts = {}) {
  const b64 = Buffer.from(scriptText, "utf8").toString("base64");
  const cmd = `f=$(mktemp) && printf %s '${b64}' | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc`;
  return runRemote(cmd, opts);
}

/** Cheap reachability check. */
async function isReachable(opts = {}) {
  const r = await runRemote("true", { ...opts, timeoutMs: (opts && opts.timeoutMs) || 12000 });
  return r.code === 0;
}

module.exports = { DEFAULTS, keyPath, buildSshArgs, runRemote, runRemoteScript, isReachable, resolveCfg };
