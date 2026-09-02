"use strict";
// THE FORWARDER'S ADAPTER LAYER — everything src/forwarder.js deliberately does not know.
//
// forwarder.js is the core: pure logic plus an injected transport, no vscode, no
// child_process, no net (plan §4.8 rules 1 and 2, extension/ARCHITECTURE.md §Forwards).
// This file is the other side of those seams, and it is the only place the forwarder
// feature touches a real process, a real socket or the VS Code API:
//
//   • createSshTransport / createRemoteTransport — build the transport object out of
//     ssh.js, child_process, net and (remote mode) a remotehost.js client.
//   • hostLabelOf / forwardsEnabled — read the two settings, lazily requiring vscode so
//     the rest of the file stays testable in plain node.
//   • the pure UI projections — the panel message shape, and the idle-policy clamp — kept
//     here (and unit-tested) rather than inside the webview, so the rule the panel shows
//     and the rule the service enforces are written down once.
//
// Split this way, `node --test` covers the whole feature: the tests build fake transports
// for forwarder.js and call the pure functions below directly.

const cp = require("child_process");
const net = require("net");
const fs = require("fs");

const forwarder = require("./forwarder");
const sshDefault = require("./ssh");

/** How long a one-shot spool script may take before we give up on it. The scripts are a
 *  handful of `stat`s and a `base64`, so this is generous by an order of magnitude. */
const SCRIPT_TIMEOUT_MS = 20000;
/** A local port probe binds and immediately closes; it never blocks on the network. */
const PROBE_TIMEOUT_MS = 2000;

// ── The SSH stream argv ──────────────────────────────────────────────────────

/**
 * argv for the long-lived spool watcher. Same shape and same reasoning as
 * `notify.buildWatchArgs` — which is why it lives in the consuming module rather than in
 * ssh.js: the connection BASE (key vs. `~/.ssh/config` alias, the non-22 port) comes from
 * `ssh.buildSshArgs`' rules, but the keepalives and the `-T` are this stream's own
 * business.
 *
 * Long-lived, so unlike `ssh.runRemote`'s one-shot it asks for keepalives: a link that
 * dies silently (VM saved, laptop slept, Wi-Fi switched) is noticed within ~a minute and
 * the child EXITS, which is what triggers the supervisor's reconnect.
 *
 * `hasKey` is threaded in rather than probed, so this stays pure and testable.
 */
function buildStreamArgs(ssh, cfg, hasKey, script) {
  const c = ssh.resolveCfg({ cfg });
  const command = ssh.wrapScriptCommand(script);
  const common = [
    "-T",                                   // a data stream, not a shell
    "-o", "BatchMode=yes",
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", `ConnectTimeout=${c.connectTimeout}`,
    "-o", "ServerAliveInterval=20",
    "-o", "ServerAliveCountMax=3",
  ];
  // Only for a non-22 port, so a default instance's argv is byte-identical to what it
  // would have been before instances existed.
  const port = ssh.normalizeSshPort ? ssh.normalizeSshPort(c.sshPort) : 22;
  if (port !== 22) common.push("-p", String(port));
  if (hasKey) {
    return ["-i", ssh.keyPath(c), "-o", "IdentitiesOnly=yes", ...common, `${c.user}@${c.vmHost}`, command];
  }
  return [...common, c.hostAlias, command];
}

// ── Transports ───────────────────────────────────────────────────────────────

/**
 * Is a local TCP port free on THIS PC, on the address the tunnel will bind? Answered by
 * actually binding it, because that is the question that matters: `ssh -L` will bind the
 * same address a moment later, and a "probably free" answer would produce an ack promising
 * a port that never opened.
 *
 * The ADDRESS matters as much as the port. Probing `127.0.0.1` and then binding `0.0.0.0`
 * would call a port free that is not (something else may hold it on the LAN interface), so
 * `bindHost` is threaded through from the same `bindHostFor` rule the argv uses.
 */
function probeLocalPort(port, bindHost, opts = {}) {
  const netImpl = opts._net || net;
  const p = sshDefault.normalizeForwardPort(port);
  const host = sshDefault.normalizeBindHost(bindHost);
  if (p === null) return Promise.resolve(false);
  return new Promise((resolve) => {
    let done = false;
    const finish = (value) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { server.close(); } catch (_) {}
      resolve(value);
    };
    const timer = setTimeout(() => finish(false), opts.timeoutMs || PROBE_TIMEOUT_MS);
    if (timer && timer.unref) timer.unref();
    let server;
    try {
      server = netImpl.createServer();
    } catch (_) {
      return finish(false);
    }
    server.once("error", () => finish(false));
    server.once("listening", () => finish(true));
    try {
      // exclusive: do not let SO_REUSEADDR make an already-served port look free.
      server.listen({ port: p, host, exclusive: true });
    } catch (_) {
      finish(false);
    }
  });
}

/**
 * The transport for LOCAL mode: everything over the instance's SSH connection.
 *
 * `opts.ssh` is src/ssh.js, `opts.cfg` the instance's cfg (the ONE way any module reaches
 * a VM), and `_spawn`/`_net`/`_exists` are the test seams.
 */
function createSshTransport(opts = {}) {
  const ssh = opts.ssh || sshDefault;
  const cfg = opts.cfg || {};
  const spawn = opts._spawn || cp.spawn;
  const exists = opts._exists || ((p) => { try { return fs.existsSync(p); } catch (_) { return false; } });
  const hasKey = () => exists(ssh.keyPath(ssh.resolveCfg({ cfg })));

  return {
    runRemoteScript(script) {
      return ssh.runRemoteScript(script, { cfg, timeoutMs: opts.timeoutMs || SCRIPT_TIMEOUT_MS });
    },

    spawnWatch(script) {
      return spawn("ssh", buildStreamArgs(ssh, cfg, hasKey(), script), {
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"],
      });
    },

    spawnTunnel({ localPort, vmPort, bindHost }) {
      const args = ssh.buildLocalForwardArgs(cfg, localPort, vmPort, hasKey(), { bindHost });
      // stdin ignored: `-N` has no remote command to feed, and an inherited stdin would
      // keep the child alive past the extension host on some platforms.
      return spawn("ssh", args, { windowsHide: true, stdio: ["ignore", "ignore", "pipe"] });
    },

    probePort(port, bindHost) {
      return probeLocalPort(port, bindHost, { _net: opts._net });
    },
  };
}

/**
 * The transport for REMOTE mode: the tunnel is still `ssh -L` to the instance's endpoint
 * (`sshHost:sshPort` from the registry — the port the service allocated), but the spool is
 * replaced by the service's own forward routes.
 *
 * `opts.client` is a src/remotehost.js client, i.e. the OWNER's credential. That is
 * deliberate and is the whole reason this route exists: the VM's own token may not post a
 * client ack (service/README.md).
 */
function createRemoteTransport(opts = {}) {
  const base = createSshTransport(opts);
  const client = opts.client;

  return {
    ...base,

    // Remote mode reads no spool and writes no file on the VM.
    runRemoteScript() {
      return Promise.resolve({ code: 0, stdout: "", stderr: "" });
    },

    spawnWatch() {
      return null;
    },

    fetchJson(method, path, body) {
      if (!client) return Promise.reject(new Error("no host-service client for this instance"));
      return client.request(method, path, body);
    },
  };
}

// ── Settings ─────────────────────────────────────────────────────────────────

/** `vscode`, lazily — so requiring this module outside VS Code (the tests) is fine. */
function vscodeApi() {
  try { return require("vscode"); } catch (_) { return null; }
}

/**
 * `construct.forwards.hostLabel`. Default "" — an empty label means the ack carries none
 * and `construct expose` prints a loopback link, which is today's behaviour and the
 * reason an untouched install sees nothing new.
 */
function hostLabelOf(api) {
  const vscode = api !== undefined ? api : vscodeApi();
  if (!vscode) return "";
  try {
    return forwarder.sanitizeHostLabel(vscode.workspace.getConfiguration("construct").get("forwards.hostLabel"));
  } catch (_) { return ""; }
}

/**
 * `construct.forwards.enabled`. Default true: the feature costs nothing until an agent
 * actually runs `construct expose` (one long-lived SSH stream, which is the same shape as
 * the notification watcher that is already on by default). The switch exists so a user who
 * does not want the extension opening local ports can say so.
 */
function forwardsEnabled(api) {
  const vscode = api !== undefined ? api : vscodeApi();
  if (!vscode) return true;
  try {
    return vscode.workspace.getConfiguration("construct").get("forwards.enabled") !== false;
  } catch (_) { return true; }
}

// ── Pure UI projections ──────────────────────────────────────────────────────

/**
 * The `state.forwards` the panel renders.
 *
 * `visible` is the zero-change rule, stated once here rather than in the webview: the card
 * is shown when there is something to show, or when the instance is REMOTE (where the
 * section is part of how a remote VM is managed at all). A local install that never runs
 * `expose` therefore sees exactly the panel it saw before. Pure.
 */
function toPanelForwards(snapshot) {
  const snap = snapshot || { mode: "local", owner: true, items: [] };
  const items = Array.isArray(snap.items) ? snap.items : [];
  return {
    mode: snap.mode === "remote" ? "remote" : "local",
    owner: snap.owner !== false,
    visible: items.length > 0 || snap.mode === "remote",
    items: items.map((item) => ({
      id: String(item.id),
      vmPort: item.vmPort,
      label: item.label || "",
      target: item.target || "client",
      status: item.status || "queued",
      localPort: item.localPort == null ? null : item.localPort,
      url: item.url || null,
      message: item.message || "",
      // May THIS window close it? Remotely the service is the authority, so any window
      // may ask it to delete any of the VM's forwards. Locally the spool has one owner,
      // and a non-owner deleting the owner's request/ack documents would tear down a
      // forward the owner still believes it is serving. The core refuses either way; this
      // is so the UI does not offer a button that cannot work.
      closable: snap.mode === "remote" ? true : snap.owner !== false,
    })),
  };
}

/** The idle actions the service accepts (Core/Domain/Enums.cs IdleAction). */
const IDLE_ACTIONS = ["save", "shutdown", "off"];

/** Normalize an action string; anything unknown reads as the service's own default. */
function normalizeIdleAction(value) {
  const s = String(value == null ? "" : value).trim().toLowerCase();
  return IDLE_ACTIONS.indexOf(s) >= 0 ? s : "save";
}

/**
 * Apply the admin cap the service reports, and say whether it bit.
 *
 * The service clamps too — when a policy is stored AND when it is evaluated
 * (service/README.md), so lowering the cap also affects VMs configured earlier. Doing it
 * here as well is not redundancy for its own sake: it is what lets the panel show the
 * value that will actually take effect, and a hint saying why, instead of accepting a
 * number and silently displaying something else after the round trip.
 *
 * `maxTimeoutMinutes` 0 means "no cap" (the service's own default). Pure.
 */
function clampIdlePolicy(policy, maxTimeoutMinutes) {
  const requested = Number((policy && policy.timeoutMinutes) || 0);
  const action = normalizeIdleAction(policy && policy.action);
  const max = Number(maxTimeoutMinutes) || 0;
  let timeoutMinutes = Number.isFinite(requested) && requested > 0 ? Math.floor(requested) : 0;
  let clamped = false;
  if (max > 0 && timeoutMinutes > max) { timeoutMinutes = max; clamped = true; }
  return { timeoutMinutes, action, clamped };
}

/**
 * The `state.idlePolicy` the panel renders, from the service's GET/PUT response. `null`
 * for anything that is not a usable policy document — a local instance has none, and the
 * panel then shows nothing at all. Pure.
 */
function toPanelIdlePolicy(response) {
  if (!response || typeof response !== "object") return null;
  const timeout = Number(response.timeoutMinutes);
  if (!Number.isFinite(timeout)) return null;
  const max = Number(response.maxTimeoutMinutes) || 0;
  return {
    timeoutMinutes: Math.max(0, Math.floor(timeout)),
    action: normalizeIdleAction(response.action),
    maxTimeoutMinutes: max > 0 ? Math.floor(max) : 0,
    clamped: response.clamped === true,
  };
}

/**
 * The power button's meaning for a VM state.
 *
 * `saved` is the interesting one: the driver maps a start onto a RESUME (Hyper-V restores
 * the RAM image, so the VM comes back where it was), and the button should say that rather
 * than "Start" — the two are a different promise to the user, and only one of them is what
 * will happen. Everything else is exactly the mapping the panel already had. Pure.
 */
function powerIntentFor(state) {
  const s = String(state == null ? "" : state).trim().toLowerCase();
  if (s === "running") return { cmd: "shutdown", kind: "shutdown" };
  if (s === "absent") return { cmd: "", kind: "none" };
  if (s === "saved") return { cmd: "startConnect", kind: "resume" };
  return { cmd: "startConnect", kind: "start" };
}

module.exports = {
  SCRIPT_TIMEOUT_MS, PROBE_TIMEOUT_MS, IDLE_ACTIONS,
  buildStreamArgs, probeLocalPort,
  createSshTransport, createRemoteTransport,
  hostLabelOf, forwardsEnabled,
  toPanelForwards, normalizeIdleAction, clampIdlePolicy, toPanelIdlePolicy, powerIntentFor,
};
