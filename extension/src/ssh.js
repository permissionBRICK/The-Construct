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
  sshPort: 22,
  connectTimeout: 12,
};

function keyPath(cfg) {
  return path.join(os.homedir(), ".ssh", cfg.keyName);
}

/**
 * The SSH port for a cfg as a safe integer. Anything missing/garbage reads as 22 —
 * the only value that produces today's argv, so a malformed instance entry can
 * never turn into an `ssh -p NaN`. Pure.
 */
function normalizeSshPort(port) {
  const n = Number(port);
  if (!Number.isInteger(n) || n <= 0 || n > 65535) return 22;
  return n;
}

/**
 * Build the argv for `ssh`. Pure (takes `hasKey` rather than touching the disk)
 * so it can be unit-tested. The remote command is passed as a single trailing
 * argument; ssh hands it to the remote login shell.
 *
 * `-p <port>` is emitted ONLY for a non-22 port: the default instance's argv must
 * stay byte-identical to what shipped before instances existed (22 is also ssh's
 * own default, so the flag would be pure noise there).
 */
function buildSshArgs(cfg, remoteCommand, hasKey) {
  const common = [
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${cfg.connectTimeout}`,
  ];
  const port = normalizeSshPort(cfg.sshPort);
  if (port !== 22) common.push("-p", String(port));
  if (hasKey) {
    return ["-i", keyPath(cfg), "-o", "IdentitiesOnly=yes", ...common, `${cfg.user}@${cfg.vmHost}`, remoteCommand];
  }
  return [...common, cfg.hostAlias, remoteCommand];
}

function resolveCfg(opts) {
  return { ...DEFAULTS, ...((opts && opts.cfg) || {}) };
}

/**
 * Build the argv for a LONG-LIVED local forward: `ssh -L <localPort>:127.0.0.1:<vmPort> -N`.
 * This is the `construct expose --to client` tunnel (docs/expose.md, plan §4.6) — the mirror
 * image of the mic passthrough's `-R`, and it lives here rather than in forwarder.js because
 * forwarder.js owns no transport (plan §4.8 rule 2).
 *
 * Differences from buildSshArgs, all of them because this connection is meant to last:
 *   -N                      no remote command; the forward IS the point
 *   ServerAlive*            a link that dies silently (VM saved, laptop slept, Wi-Fi
 *                           switched) is noticed within ~a minute and the child EXITS,
 *                           which is what triggers the supervisor's restart
 *   ExitOnForwardFailure    a local port that is already taken must fail the child, not
 *                           leave an ssh running that forwards nothing — an ack promising a
 *                           port that never opened is worse than no ack
 *
 * THE BIND ADDRESS IS ALWAYS EXPLICIT, never inherited from ssh's defaults or from the
 * user's `~/.ssh/config`. `opts.bindHost` defaults to `127.0.0.1`, so the privacy of a
 * default forward is a property of this argv rather than an assumption about configuration
 * — an address-less `-L` means "loopback" only until a `GatewayPorts`, a `LocalForward` in
 * a matching `Host` block or a future ssh default says otherwise. `0.0.0.0` is the
 * deliberate opt-in that makes a configured `construct.forwards.hostLabel` actually
 * reachable; a link naming this PC that only this PC can open is a dead link, which is
 * worse than no setting at all.
 *
 * The far end is always the VM's own `127.0.0.1:<vmPort>`, so a dev server bound to the
 * VM's loopback is reachable and nothing on the VM's own network is exposed.
 *
 * `hasKey` is threaded in rather than probed, and `-p` is emitted ONLY for a non-22 port,
 * exactly as buildSshArgs does — a default instance's argv stays what it always was. Pure.
 */
function buildLocalForwardArgs(cfg, localPort, vmPort, hasKey, opts = {}) {
  const c = resolveCfg({ cfg });
  const lp = normalizeForwardPort(localPort);
  const vp = normalizeForwardPort(vmPort);
  if (lp === null || vp === null) {
    throw new Error(`invalid port for a local forward: ${localPort} -> ${vmPort}`);
  }
  const bind = normalizeBindHost(opts.bindHost);
  const common = [
    "-N",
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${c.connectTimeout}`,
    "-o", "ServerAliveInterval=15",
    "-o", "ServerAliveCountMax=3",
    "-o", "ExitOnForwardFailure=yes",
    "-L", `${bind}:${lp}:127.0.0.1:${vp}`,
  ];
  const port = normalizeSshPort(c.sshPort);
  if (port !== 22) common.push("-p", String(port));
  if (hasKey) {
    return ["-i", keyPath(c), "-o", "IdentitiesOnly=yes", ...common, `${c.user}@${c.vmHost}`];
  }
  return [...common, c.hostAlias];
}

/**
 * A forward port as a safe integer in [1,65535], or null. Deliberately stricter than
 * normalizeSshPort's "fall back to 22": there is no sane default for "which port did the
 * agent ask for", so a bad value has to be refused rather than silently redirected. Accepts
 * a numeric string too (a port that came back over JSON). Pure.
 */
function normalizeForwardPort(port) {
  if (typeof port === "string" && /^[0-9]{1,5}$/.test(port)) port = Number(port);
  if (typeof port !== "number" || !Number.isInteger(port) || port < 1 || port > 65535) return null;
  return port;
}

/** The two bind addresses a client forward may listen on. Loopback is the default and the
 *  private one; ALL is the opt-in that makes a `hostLabel` link real. */
const FORWARD_BIND_LOOPBACK = "127.0.0.1";
const FORWARD_BIND_ALL = "0.0.0.0";

/**
 * Coerce a bind address to one of exactly two values. An ALLOW-LIST, not a sanitizer: this
 * string goes into an `ssh -L` argument, and "whatever the caller passed" is the one thing
 * a listening address must never be. Anything unrecognised — including undefined — is
 * loopback, so the safe answer is also the default. Pure.
 */
function normalizeBindHost(value) {
  const s = String(value == null ? "" : value).trim();
  return s === FORWARD_BIND_ALL || s === "*" || s === "::" ? FORWARD_BIND_ALL : FORWARD_BIND_LOOPBACK;
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
  return runRemote(wrapScriptCommand(scriptText), opts);
}

/**
 * Wrap a script as ONE remote command line: ship it base64 (so no quoting of the
 * script's own quotes/newlines is needed), decode into a temp file, run it, clean up,
 * and preserve its exit status. Shared by runRemoteScript and by the long-lived
 * connections (notifications), which need the same command but spawn ssh themselves.
 * Pure.
 */
function wrapScriptCommand(scriptText) {
  const b64 = Buffer.from(String(scriptText), "utf8").toString("base64");
  return `f=$(mktemp) && printf %s '${b64}' | base64 -d > "$f" && bash "$f"; rc=$?; rm -f "$f"; exit $rc`;
}

/** Cheap reachability check. */
async function isReachable(opts = {}) {
  const r = await runRemote("true", { ...opts, timeoutMs: (opts && opts.timeoutMs) || 12000 });
  return r.code === 0;
}

module.exports = { DEFAULTS, keyPath, normalizeSshPort, normalizeForwardPort, normalizeBindHost, FORWARD_BIND_LOOPBACK, FORWARD_BIND_ALL, buildSshArgs, buildLocalForwardArgs, wrapScriptCommand, runRemote, runRemoteScript, isReachable, resolveCfg };
