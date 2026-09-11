"use strict";

// Section 7 client. No VS Code dependency; all I/O can be replaced by tests.
const path = require("path");
const remotehost = require("./remotehost");
const IPC_API_VERSION = 1;
const GRACE_MS = 10000;
const SETTINGS_MARKER = "construct.companion.settingsMigrated.v1";
const TOKEN_MARKER = "construct.companion.tokenMigrated.v1:";

function endpointPath(env) {
  const base = env.LOCALAPPDATA || env.TEMP;
  return base ? path.join(base, "The-Construct", "companion", "endpoint.json") : null;
}
function parseEndpoint(text) {
  try {
    const e = JSON.parse(text.replace(/^\uFEFF/, ""));
    if (!e || e.v !== 1 || e.ipcApiVersion !== IPC_API_VERSION ||
        !Number.isInteger(e.port) || e.port < 1 || e.port > 65535 ||
        !Number.isSafeInteger(e.pid) || e.pid <= 0 ||
        typeof e.token !== "string" || !/^[a-f0-9]{64}$/i.test(e.token) ||
        typeof e.version !== "string" || !e.version ||
        typeof e.startedAt !== "string" || !Number.isFinite(Date.parse(e.startedAt))) return null;
    return e;
  } catch (_) { return null; }
}
function validHealth(e, h) {
  return !!(h && h.ok === true && h.ipcApiVersion === IPC_API_VERSION &&
    h.pid === e.pid && h.startedAt === e.startedAt);
}
function presence(state, healthy, now) {
  if (healthy) return { status: "alive", lostAt: null };
  if (state.status === "alive") return { status: "lost", lostAt: now };
  if (state.status === "lost" && now - state.lostAt < GRACE_MS) return state;
  return { status: "absent", lostAt: null };
}

// Incremental SSE lines (CR, LF and CRLF, including boundaries between chunks).
// Comments/unknown fields are ignored; multi-line data is joined per the SSE spec.
function createSseParser(onEvent) {
  let line = "", cr = false, event = "", data = [];
  function emitLine() {
    if (!line) {
      if (data.length) onEvent(event || "message", data.join("\n"));
      event = ""; data = [];
    } else if (line[0] !== ":") {
      const colon = line.indexOf(":");
      const field = colon < 0 ? line : line.slice(0, colon);
      let value = colon < 0 ? "" : line.slice(colon + 1);
      if (value[0] === " ") value = value.slice(1);
      if (field === "event") event = value;
      if (field === "data") data.push(value);
    }
    line = "";
  }
  return (chunk) => {
    for (const ch of chunk) {
      if (cr && ch === "\n") { cr = false; continue; }
      cr = false;
      if (ch === "\r" || ch === "\n") { emitLine(); cr = ch === "\r"; }
      else line += ch;
    }
    // Bound an untrusted/malformed stream without ever putting its data in errors.
    if (line.length + data.reduce((n, s) => n + s.length, 0) > 1024 * 1024) {
      throw new Error("Companion event too large");
    }
  };
}
function overlayMessage(message, connectedInstance) {
  if (message && message.type === "state") {
    return { ...message, state: { ...message.state, connectedInstance } };
  }
  return message;
}
function snapshotMessages(snapshot, connectedInstance) {
  const out = [];
  if (!snapshot || typeof snapshot !== "object") return out;
  for (const key of ["state", "settings", "audio", "forwards"]) {
    const m = snapshot[key];
    if (!m || typeof m.type !== "string") continue;
    if (key === "state") {
      const state = { ...m.state };
      for (const field of ["children", "idlePolicy", "hostAdminOffer"]) {
        if (Object.prototype.hasOwnProperty.call(snapshot, field)) state[field] = snapshot[field];
      }
      out.push(overlayMessage({ ...m, state }, connectedInstance));
    } else out.push(m);
  }
  return out;
}

const SETTING_DEFAULTS = Object.freeze({
  micDevice: "", notifications: true, "forwards.enabled": true,
  "forwards.hostLabel": "", repatchDelaySeconds: 20,
});
function planSettingsMigration(values, defaults = SETTING_DEFAULTS) {
  const patch = {};
  for (const [key, fallback] of Object.entries(defaults)) {
    const value = values[key];
    if (value === undefined || value === fallback || typeof value !== typeof fallback) continue;
    if (typeof value === "number" && (!Number.isFinite(value) || value < 0 || value > 600)) continue;
    if (key.startsWith("forwards.")) (patch.forwards || (patch.forwards = {}))[key.slice(9)] = value;
    else patch[key] = value;
  }
  return patch;
}
const psQuote = (s) => "'" + String(s).replace(/'/g, "''") + "'";
function planTokenMigration({ url, libPath, tokenExists, env }) {
  if (tokenExists) return null;
  const dir = remotehost.remoteStoreDir(env);
  if (!libPath || !dir) throw new Error("Companion token migration requires the installed remote library");
  const slug = remotehost.hostSlug(url);
  // Only the fixed script and non-secret paths/URL enter argv. Catch in PowerShell
  // too: even an unexpected library error must never print its token argument.
  const script = "$ErrorActionPreference = 'Stop'; try { . " + psQuote(libPath) +
    "; $token = [Console]::In.ReadToEnd(); Save-ConstructRemoteToken -BaseUrl " + psQuote(url) +
    " -Token $token -StoreDir " + psQuote(dir) + " | Out-Null; $token = $null; exit 0 } catch { exit 1 }";
  return {
    file: "powershell.exe", args: ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
    secretKey: remotehost.tokenSecretKey(url), marker: TOKEN_MARKER + slug,
    tokenPath: path.join(dir, slug + ".token"),
  };
}
async function migrate({ globalState, settings, putSettings, hosts, secrets, fs, env, libPath, run }) {
  if (!globalState.get(SETTINGS_MARKER)) {
    const patch = planSettingsMigration(settings);
    if (Object.keys(patch).length) await putSettings(patch);
    await globalState.update(SETTINGS_MARKER, true);
  }
  for (const h of hosts) {
    const slug = remotehost.hostSlug(h.url);
    const marker = TOKEN_MARKER + slug;
    if (globalState.get(marker)) continue;
    const dir = remotehost.remoteStoreDir(env);
    if (!dir) continue;
    const tokenExists = fs.existsSync(path.join(dir, slug + ".token"));
    if (!tokenExists) {
      const token = await secrets.get(remotehost.tokenSecretKey(h.url));
      if (!token) continue;
      const plan = planTokenMigration({ url: h.url, libPath, tokenExists, env });
      try {
        const result = await run(plan.file, plan.args, token);
        if (result.code !== 0 || !fs.existsSync(plan.tokenPath)) throw new Error();
      } catch (_) { throw new Error("Companion token migration failed"); }
    }
    await globalState.update(marker, true);
  }
}

function createClient(options = {}) {
  const fs = options.fs || require("fs");
  const http = options.http || require("http");
  const timers = options.timers || { setTimeout, clearTimeout, setInterval, clearInterval };
  const now = options.now || Date.now;
  const pidAlive = options.pidAlive || ((pid) => { try { process.kill(pid, 0); return true; } catch (e) { return e.code === "EPERM"; } });
  const file = options.endpointPath || endpointPath(options.env || process.env);
  const onState = options.onState || (() => {});
  let state = { status: "absent", lostAt: null }, endpoint = null, stopped = false, enabled = true;
  let checkPromise, interval, watcher, graceTimer, reconnectTimer, stream, streamResponse;
  let generation = 0, retry = 0;
  const requests = new Set();
  let transition = Promise.resolve();
  function change(healthy) {
    const next = presence(state, healthy, now());
    if (next.status === state.status) return;
    const previous = state.status;
    state = next;
    if (graceTimer) timers.clearTimeout(graceTimer);
    graceTimer = null;
    if (state.status === "lost") graceTimer = timers.setTimeout(() => { void check(); }, GRACE_MS);
    transition = transition.then(() => onState(next.status, previous)).catch(() => {});
  }
  function closeStream() {
    if (reconnectTimer) timers.clearTimeout(reconnectTimer);
    reconnectTimer = null;
    const old = stream; stream = null;
    if (streamResponse) streamResponse.destroy();
    streamResponse = null;
    if (old) old.destroy();
  }
  function json(e, method, route, body) {
    return new Promise((resolve, reject) => {
      let req, deadline, settled = false;
      const finish = (error, value) => {
        if (settled) return;
        settled = true;
        if (deadline) timers.clearTimeout(deadline);
        requests.delete(req);
        error ? reject(new Error("Companion request failed")) : resolve(value);
      };
      try {
        const payload = body === undefined ? null : JSON.stringify(body);
        req = http.request({ hostname: "127.0.0.1", port: e.port, method, path: route,
          headers: { Authorization: "Bearer " + e.token, Accept: "application/json",
            ...(payload === null ? {} : { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(payload) }) } }, (res) => {
          let data = "";
          res.setEncoding("utf8");
          res.on("data", (chunk) => { data += chunk; if (data.length > 4 * 1024 * 1024) { finish(true); res.destroy(); } });
          res.on("error", () => finish(true));
          res.on("aborted", () => finish(true));
          res.on("end", () => {
            if (res.statusCode < 200 || res.statusCode >= 300) return finish(true);
            try { finish(false, data ? JSON.parse(data) : null); } catch (_) { finish(true); }
          });
        });
        requests.add(req);
        req.on("error", () => finish(true));
        deadline = timers.setTimeout(() => { finish(true); req.destroy(); }, options.timeoutMs || 3000);
        req.end(payload);
      } catch (_) { finish(true); }
    });
  }
  function openStream() {
    if (stopped || !enabled || !endpoint || state.status !== "alive" || stream || reconnectTimer) return;
    const e = endpoint;
    let req;
    const failed = () => {
      if (stream !== req) return;
      closeStream();
      if (stopped || !enabled) return;
      reconnectTimer = timers.setTimeout(async () => {
        reconnectTimer = null;
        await check();
        openStream();
      }, Math.min(1000 * 2 ** Math.min(retry++, 5), 30000));
      void check();
    };
    try {
      req = http.request({ hostname: "127.0.0.1", port: e.port, path: "/v1/events", method: "GET",
        headers: { Authorization: "Bearer " + e.token, Accept: "text/event-stream" } }, (res) => {
        if (stream !== req) { res.destroy(); return; }
        streamResponse = res;
        if (res.statusCode !== 200 || !/^text\/event-stream\b/i.test(res.headers["content-type"] || "")) { failed(); return; }
        retry = 0;
        const parse = createSseParser((type, data) => {
          if (stream !== req || state.status !== "alive") return;
          let value;
          try { value = JSON.parse(data); } catch (_) { return; }
          if (options.onEvent) options.onEvent(type, value);
        });
        res.setEncoding("utf8");
        res.on("data", (chunk) => { try { parse(chunk); } catch (_) { failed(); } });
        res.on("end", failed); res.on("error", failed); res.on("close", failed);
        // Keepalives arrive every 15s; detect a half-open socket too.
        res.setTimeout(45000, failed);
        if (options.onConnect) Promise.resolve(options.onConnect()).catch(() => {});
      });
      stream = req;
      req.on("error", failed);
      req.setTimeout(45000, failed);
      req.end();
    } catch (_) { failed(); }
  }
  function watch() {
    if (watcher || !file || stopped || !enabled) return;
    try {
      watcher = fs.watch(path.dirname(file), { persistent: false }, () => { void check(); });
      watcher.on("error", () => { watcher.close(); watcher = null; });
    } catch (_) { /* absent directory: the 30s check retries the watch */ }
  }
  function check() {
    if (checkPromise) return checkPromise;
    if (stopped || !enabled) return Promise.resolve();
    const stamp = generation;
    checkPromise = (async () => {
      watch();
      let candidate = null;
      try {
        candidate = file ? parseEndpoint(fs.readFileSync(file, "utf8")) : null;
        if (!candidate || !pidAlive(candidate.pid) || !validHealth(candidate, await json(candidate, "GET", "/v1/health"))) candidate = null;
      } catch (_) { candidate = null; }
      if (stopped || !enabled || generation !== stamp) return;
      const changed = JSON.stringify(endpoint) !== JSON.stringify(candidate);
      if (changed) closeStream();
      endpoint = candidate;
      change(!!candidate);
      await transition;
      if (candidate) openStream();
    })().finally(() => { checkPromise = null; });
    return checkPromise;
  }
  async function request(method, route, body) {
    if (stopped || state.status !== "alive" || !endpoint) throw new Error("Companion unavailable");
    const e = endpoint;
    try { return await json(e, method, route, body); }
    catch (_) { void check(); throw new Error("Companion request failed"); }
  }
  async function start(mode = "auto") {
    if (stopped) return;
    enabled = mode !== "off";
    if (enabled) {
      if (!interval) interval = timers.setInterval(() => { void check(); }, 30000);
      await check();
    }
  }
  async function setMode(mode) {
    if (mode !== "off") { enabled = true; if (checkPromise) await checkPromise; await start(); return; }
    enabled = false; generation++;
    closeStream();
    if (watcher) watcher.close(); watcher = null;
    if (interval) timers.clearInterval(interval); interval = null;
    if (graceTimer) timers.clearTimeout(graceTimer); graceTimer = null;
    const previous = state.status;
    state = { status: "absent", lostAt: null }; endpoint = null;
    if (previous !== "absent") { transition = transition.then(() => onState("absent", previous)); await transition; }
  }
  function dispose() {
    if (stopped) return;
    stopped = true; generation++;
    closeStream();
    if (watcher) watcher.close();
    if (interval) timers.clearInterval(interval);
    if (graceTimer) timers.clearTimeout(graceTimer);
    for (const req of requests) req.destroy();
    requests.clear();
  }
  return {
    get status() { return state.status; }, get deferred() { return state.status !== "absent"; },
    start, check, setMode, dispose, request,
    snapshot: (name) => request("GET", `/v1/instances/${encodeURIComponent(name)}/snapshot`),
    proxy: (name, message) => request("POST", `/v1/instances/${encodeURIComponent(name)}/messages`, message),
    activate: (view, instance, host) => request("POST", "/v1/ui/activate", { view, ...(instance ? { instance } : {}), ...(host ? { host } : {}) }),
    putSettings: (patch) => request("PUT", "/v1/settings", patch),
  };
}

module.exports = { IPC_API_VERSION, GRACE_MS, SETTINGS_MARKER, TOKEN_MARKER, SETTING_DEFAULTS,
  endpointPath, parseEndpoint, validHealth, presence, createSseParser, overlayMessage, snapshotMessages,
  planSettingsMigration, planTokenMigration, migrate, createClient };
