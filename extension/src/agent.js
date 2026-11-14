"use strict";
// The device agent as the extension sees it: where its files are, whether it runs, how it is
// started and installed, and one request helper for its loopback API (agent/README.md). No vscode
// here, so all of it runs under plain node.

const fs = require("fs");
const path = require("path");
const http = require("http");
const childProcess = require("child_process");

const VERSION_RE = /^[A-Za-z0-9.+_-]+$/;

/** The agent's directory, as AgentOptions.DefaultDataDirectory computes it. */
function agentHome(env = process.env, platform = process.platform, homedir = require("os").homedir()) {
  if (env.CONSTRUCT_AGENT_HOME) return env.CONSTRUCT_AGENT_HOME;
  if (platform === "win32") return path.win32.join(env.LOCALAPPDATA || path.win32.join(homedir, "AppData", "Local"), "Construct", "agent");
  return path.posix.join(env.XDG_DATA_HOME || path.posix.join(homedir, ".local", "share"), "construct-agent");
}

function executableName(platform = process.platform) {
  return platform === "win32" ? "construct-agent.exe" : "construct-agent";
}

function join(platform, ...parts) { return (platform === "win32" ? path.win32 : path.posix).join(...parts); }

function readJson(file, readFile = fs.readFileSync) {
  try { return JSON.parse(readFile(file, "utf8")); } catch (_) { return null; }
}

/** The token file, when it holds a usable port and token. */
function parseEndpoint(doc) {
  if (!doc || typeof doc !== "object") return null;
  const port = doc.port, token = doc.token;
  if (!Number.isInteger(port) || port < 1 || port > 65535 || typeof token !== "string" || !/^[\x21-\x7e]{16,512}$/.test(token)) return null;
  return { port, token, pid: Number.isInteger(doc.pid) ? doc.pid : null, startedAt: typeof doc.startedAt === "string" ? doc.startedAt : null };
}

/** The installed agent's executable from install.json's `current`, or null. */
function installedExecutable(home, platform = process.platform, deps = {}) {
  const exists = deps.exists || fs.existsSync;
  const install = readJson(join(platform, home, "install.json"), deps.readFile);
  const current = install && typeof install.current === "string" ? install.current : "";
  if (!current || !VERSION_RE.test(current) || current === "." || current === "..") return null;
  const exe = join(platform, home, "versions", current, executableName(platform));
  return exists(exe) ? exe : null;
}

/**
 * The health answer belongs to the agent the token file names: same pid and start time. A
 * stale token file whose port another program now holds never gets the token.
 */
function healthMatches(health, endpoint) {
  if (!health || health.status !== "ok") return false;
  if (endpoint.pid !== null && health.pid !== endpoint.pid) return false;
  if (endpoint.startedAt !== null && typeof health.startedAt === "string" && Date.parse(health.startedAt) !== Date.parse(endpoint.startedAt)) return false;
  return true;
}

/**
 * One request to the agent on 127.0.0.1. `token` is sent only in the Authorization header; no
 * redirect is followed. Resolves {status, body} (body parsed as JSON when it is), rejects on a
 * network error or the timeout.
 */
function request(endpoint, method, route, opts = {}) {
  const body = opts.body === undefined ? null : Buffer.from(JSON.stringify(opts.body));
  const headers = { Accept: "application/json" };
  if (opts.token) headers.Authorization = "Bearer " + opts.token;
  if (body) { headers["Content-Type"] = "application/json"; headers["Content-Length"] = body.length; }
  return new Promise((resolve, reject) => {
    const req = (opts.http || http).request({ host: "127.0.0.1", port: endpoint.port, method, path: route, headers, timeout: opts.timeoutMs || 10000 }, res => {
      const chunks = [];
      res.on("data", c => chunks.push(c));
      res.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        let parsed = text;
        try { parsed = text ? JSON.parse(text) : null; } catch (_) { /* keep the text */ }
        resolve({ status: res.statusCode, body: parsed });
      });
    });
    req.on("timeout", () => req.destroy(new Error("The Construct agent did not answer in time.")));
    req.on("error", reject);
    if (body) req.write(body);
    req.end();
  });
}

/**
 * Where the agent stands: {state: "running", endpoint} with the checked token file,
 * {state: "stopped", exe} when it is installed but does not answer, {state: "missing"}.
 */
async function find(opts = {}) {
  const platform = opts.platform || process.platform;
  const home = opts.home || agentHome(opts.env, platform);
  const endpoint = parseEndpoint(readJson(join(platform, home, "endpoint.json"), opts.readFile));
  if (endpoint) {
    try {
      const health = await (opts.request || request)(endpoint, "GET", "/api/health", { timeoutMs: 3000 });
      if (health.status === 200 && healthMatches(health.body, endpoint)) return { state: "running", endpoint, home };
    } catch (_) { /* not answering: fall through */ }
  }
  const exe = installedExecutable(home, platform, opts);
  return exe ? { state: "stopped", exe, home } : { state: "missing", home };
}

/**
 * On POSIX a file the extension runs must belong to this user and be writable by nobody else,
 * and so must every folder from it up to `top`. Windows: the per-user profile folders are the user's.
 */
function privatePath(file, top, deps = {}) {
  if ((deps.platform || process.platform) === "win32") return true;
  const stat = deps.stat || fs.statSync;
  const uid = deps.uid !== undefined ? deps.uid : process.getuid();
  let current = path.resolve(file);
  const stop = path.resolve(top);
  for (;;) {
    let st;
    try { st = stat(current); } catch (_) { return false; }
    if (st.uid !== uid || (st.mode & 0o022) !== 0) return false;
    if (current === stop) return true;
    const parent = path.dirname(current);
    if (parent === current || !current.startsWith(stop + path.sep)) return false;
    current = parent;
  }
}

/** How the agent is started in the background, as its own start does (README, install scripts). */
function startCommand(exe, platform = process.platform, deps = {}) {
  if (platform === "win32") return { file: exe, args: ["--background"] };
  const unit = path.posix.join(deps.configHome || (process.env.XDG_CONFIG_HOME || path.posix.join(require("os").homedir(), ".config")), "systemd", "user", "construct-agent.service");
  const systemctl = ["/usr/bin/systemctl", "/bin/systemctl"].find(p => (deps.exists || fs.existsSync)(p));
  if (systemctl && (deps.exists || fs.existsSync)(unit)) return { file: systemctl, args: ["--user", "start", "construct-agent.service"], unit };
  return { file: exe, args: ["run"] };
}

function start(found, opts = {}) {
  const platform = opts.platform || process.platform;
  if (!privatePath(found.exe, found.home, { platform })) throw new Error("The agent's folder can be written by other users; run construct-agent repair.");
  const cmd = startCommand(found.exe, platform, opts);
  // The user unit names what systemd runs: it must be as private as the executable.
  if (cmd.unit && !privatePath(cmd.unit, opts.unitTop || require("os").homedir(), { platform, stat: opts.stat, uid: opts.uid })) {
    throw new Error("The agent's systemd unit can be written by other users; run construct-agent repair.");
  }
  const child = (opts.spawn || childProcess.spawn)(cmd.file, cmd.args, { detached: true, stdio: "ignore", windowsHide: true });
  child.on("error", () => { /* reported by the wait below */ });
  child.unref();
  return cmd;
}

/** Polls `find` until the agent answers; resolves the running result or null after `timeoutMs`. */
async function waitRunning(opts = {}) {
  const deadline = Date.now() + (opts.timeoutMs || 20000);
  for (;;) {
    const found = await (opts.find || find)(opts);
    if (found.state === "running") return found;
    if (Date.now() >= deadline) return null;
    await new Promise(r => setTimeout(r, opts.intervalMs || 500));
  }
}

/**
 * The agent's install script for this OS, carried in the extension's `install/` folder (the
 * packager copies agent/install there). Windows PowerShell and bash by absolute path.
 */
function installCommand(extensionDir, platform = process.platform, env = process.env) {
  if (platform === "win32") {
    const script = path.win32.join(extensionDir, "install", "Install-ConstructAgent.ps1");
    const ps = path.win32.join(env.SystemRoot || "C:\\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    return { script, file: ps, args: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script] };
  }
  if (platform === "linux") {
    const script = path.posix.join(extensionDir, "install", "install-construct-agent.sh");
    return { script, file: "/bin/bash", args: [script] };
  }
  return null;
}

/**
 * Runs the install script; resolves {ok: true} or {ok: false, error} with the end of what it
 * printed, so the view can say what failed in place.
 */
function install(extensionDir, opts = {}) {
  const platform = opts.platform || process.platform;
  const cmd = installCommand(extensionDir, platform, opts.env);
  if (!cmd) return Promise.resolve({ ok: false, error: "The Construct agent is available for Windows and Linux only." });
  if (!(opts.exists || fs.existsSync)(cmd.script)) {
    return Promise.resolve({ ok: false, error: "This copy of the extension carries no install script. Run install.ps1 (Windows) or the agent's install script by hand." });
  }
  if (!privatePath(cmd.script, extensionDir, { platform, stat: opts.stat, uid: opts.uid })) {
    return Promise.resolve({ ok: false, error: "The install script can be written by other users, so it is not run." });
  }
  return new Promise(resolve => {
    (opts.execFile || childProcess.execFile)(cmd.file, cmd.args, { timeout: opts.timeoutMs || 600000, windowsHide: true, maxBuffer: 4 * 1024 * 1024 }, (err, stdout, stderr) => {
      if (!err) return resolve({ ok: true });
      const lines = String(stderr || "").trim() || String(stdout || "").trim() || String(err.message || err);
      resolve({ ok: false, error: lines.split(/\r?\n/).slice(-6).join("\n") });
    });
  });
}

module.exports = {
  agentHome, executableName, parseEndpoint, installedExecutable, healthMatches, request, find,
  privatePath, startCommand, start, waitRunning, installCommand, install,
};
