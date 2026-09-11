"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");
const vm = require("vm");
const { createRequire } = require("module");
const c = require("../src/companion");
const remotehost = require("../src/remotehost");
const endpoint = { v: 1, port: 1234, token: "a".repeat(64), pid: process.pid,
  startedAt: "2026-09-11T01:00:00Z", version: "test", ipcApiVersion: 1 };
const health = (e = endpoint) => ({ ok: true, pid: e.pid, startedAt: e.startedAt, version: e.version, ipcApiVersion: e.ipcApiVersion });
const tick = () => new Promise((resolve) => setImmediate(resolve));
async function until(fn) { for (let i = 0; i < 200; i++) { if (fn()) return; await new Promise(r => setTimeout(r, 5)); } assert.fail("condition timed out"); }
function fakeClock() {
  let time = 0, next = 1;
  const jobs = new Map();
  const timers = {
    setTimeout(fn, ms) { const id = next++; jobs.set(id, { fn, at: time + ms }); return id; },
    clearTimeout(id) { jobs.delete(id); },
    setInterval(fn, ms) { const id = next++; jobs.set(id, { fn, at: time + ms, ms }); return id; },
    clearInterval(id) { jobs.delete(id); },
  };
  return { timers, jobs, now: () => time, async advance(ms) {
    time += ms;
    for (const [id, job] of [...jobs]) if (job.at <= time) { jobs.delete(id); if (job.ms) jobs.set(id, { ...job, at: time + job.ms }); job.fn(); }
    await tick();
  } };
}
async function stub(t, opts = {}) {
  let document, healthy = true, api = 1;
  const requests = [], streams = new Set();
  const server = http.createServer((req, res) => {
    let raw = "";
    req.on("data", (b) => { raw += b; });
    req.on("end", () => {
      requests.push({ method: req.method, path: req.url, auth: req.headers.authorization, host: req.headers.host, body: raw ? JSON.parse(raw) : undefined });
      if (req.url === "/v1/health") { res.writeHead(healthy ? 200 : 503, { "Content-Type": "application/json" }); res.end(JSON.stringify({ ...health(document), ipcApiVersion: api })); return; }
      if (req.headers.authorization !== "Bearer " + document.token) { res.writeHead(401); res.end(); return; }
      if (req.url === "/v1/events") {
        res.writeHead(200, { "Content-Type": "text/event-stream" }); res.write(": hello\n\n");
        streams.add(res); res.on("close", () => streams.delete(res)); return;
      }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(req.url.endsWith("/snapshot") ? { state: { type: "state", state: { instance: "agent-vm", connectedInstance: null } }, settings: { type: "settings", settings: {} }, children: [] } : { accepted: true }));
    });
  });
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  document = { ...endpoint, port: server.address().port };
  let file = JSON.stringify(document), watchCallback, watches = 0;
  const clock = fakeClock(), states = [], events = [];
  const client = c.createClient({ endpointPath: "/state/endpoint.json", http,
    fs: { readFileSync() { if (file === null) throw new Error(); return file; }, watch(dir, options, cb) { watches++; watchCallback = cb; return { on() {}, close() { watches--; } }; } },
    timers: clock.timers, now: clock.now, pidAlive: opts.pidAlive || (() => true),
    onState: (s) => states.push(s), onEvent: (type, data) => events.push({ type, data }), ...opts,
  });
  t.after(async () => { client.dispose(); for (const res of streams) res.destroy(); server.closeAllConnections(); await new Promise(r => server.close(r)); });
  return { client, clock, states, events, requests, streams, document,
    get watches() { return watches; }, setFile: (value) => { file = value; }, changed: () => watchCallback(),
    setHealthy: (v) => { healthy = v; }, setApi: (v) => { api = v; } };
}

test("endpoint validates version, loopback port, PID, token, metadata and BOM", () => {
  assert.deepEqual(c.parseEndpoint("\uFEFF" + JSON.stringify(endpoint)), endpoint);
  for (const patch of [{ v: 2 }, { ipcApiVersion: 2 }, { port: 0 }, { port: 65536 }, { port: "80" }, { pid: -1 }, { token: "secret" }, { startedAt: "bad" }, { version: null }]) assert.equal(c.parseEndpoint(JSON.stringify({ ...endpoint, ...patch })), null);
  for (const raw of ["oops", "null", "[]"]) assert.equal(c.parseEndpoint(raw), null);
  assert.equal(c.endpointPath({ TEMP: "/tmp/user" }), path.join("/tmp/user", "The-Construct", "companion", "endpoint.json"));
  assert.equal(c.endpointPath({}), null);
});
test("health is tied to the endpoint PID, start time and supported API", () => {
  assert.equal(c.validHealth(endpoint, health()), true);
  for (const patch of [{ pid: 99 }, { startedAt: "other" }, { ipcApiVersion: 2 }, { ok: false }]) assert.equal(c.validHealth(endpoint, { ...health(), ...patch }), false);
});
test("presence grants exactly 10 seconds from first loss and resets on recovery", () => {
  const alive = c.presence({ status: "absent" }, true, 0);
  const lost = c.presence(alive, false, 1);
  assert.deepEqual(c.presence(lost, false, 10000), lost);
  assert.equal(c.presence(lost, false, 10001).status, "absent");
  assert.deepEqual(c.presence(lost, true, 2), { status: "alive", lostAt: null });
});
test("SSE handles fragmented CRLF, comments, multiline JSON, default event and unicode", () => {
  const events = [], parse = c.createSseParser((...args) => events.push(args));
  for (const text of [": hi\r", "\nevent: message\r\ndata: {\r\n", "data: \"text\":\"🎤\"}\r", "\n\r", "\ndata: next\n\n"]) parse(text);
  assert.deepEqual(events, [["message", '{\n"text":"🎤"}'], ["message", "next"]]);
});
test("Host narrow snapshot envelopes are unwrapped for panel state", () => {
  const snapshot = { state: { type: "state", state: {} },
    children: { type: "children", children: { items: [] } },
    idlePolicy: { type: "idlePolicy", idlePolicy: { timeoutMinutes: 10 } },
    hostAdminOffer: { type: "hostAdminOffer", offer: { host: "remote" } } };
  assert.deepEqual(c.snapshotMessages(snapshot, null)[0].state, {
    connectedInstance: null, children: { items: [] }, idlePolicy: { timeoutMinutes: 10 }, hostAdminOffer: { host: "remote" }
  });
});
test("snapshot emits existing message shapes and overlays window connection without mutation", () => {
  const snapshot = { state: { type: "state", state: { instance: "a", connectedInstance: null } }, audio: { type: "audio", enabled: true }, children: [], idlePolicy: null, hostAdminOffer: null };
  const messages = c.snapshotMessages(snapshot, "b");
  assert.deepEqual(messages[0].state, { instance: "a", connectedInstance: "b", children: [], idlePolicy: null, hostAdminOffer: null });
  assert.equal(snapshot.state.state.connectedInstance, null);
  assert.equal(messages[1], snapshot.audio);
});
test("client detects and authenticates a live endpoint; proxies verbatim and fetches snapshots", async t => {
  const s = await stub(t);
  await s.client.start();
  assert.equal(s.client.status, "alive"); assert.equal(s.client.deferred, true);
  const message = { type: "saveSettings", form: { mic: true }, extra: [1, 2] };
  await s.client.proxy("vm space", message);
  const sent = s.requests.find(r => r.method === "POST");
  assert.equal(sent.path, "/v1/instances/vm%20space/messages"); assert.deepEqual(sent.body, message);
  assert.equal(sent.auth, "Bearer " + endpoint.token); assert.equal(sent.host, "127.0.0.1:" + s.document.port);
  assert.equal((await s.client.snapshot("agent-vm")).state.type, "state");
  await s.client.activate("panel", "agent-vm"); await s.client.putSettings({ uiTheme: "terminal" });
  assert.deepEqual(s.requests.find(r => r.path === "/v1/ui/activate").body, { view: "panel", instance: "agent-vm" });
  assert.deepEqual(s.requests.find(r => r.path === "/v1/settings").body, { uiTheme: "terminal" });
});
test("off mode performs no file watch, HTTP or timer work", async t => {
  const s = await stub(t); await s.client.start("off");
  assert.equal(s.requests.length, 0); assert.equal(s.watches, 0); assert.equal(s.clock.jobs.size, 0);
  await s.client.setMode("auto"); assert.equal(s.client.status, "alive");
  await s.client.setMode("off"); assert.equal(s.client.status, "absent"); assert.equal(s.clock.jobs.size, 0);
});
test("missing, dead PID and newer endpoint API fall back without HTTP", async t => {
  const s = await stub(t, { pidAlive: () => false }); await s.client.start();
  assert.equal(s.client.status, "absent"); assert.equal(s.requests.length, 0);
  const other = await stub(t); other.setFile(null); await other.client.start();
  other.setFile(JSON.stringify({ ...other.document, ipcApiVersion: 2 })); await other.client.check();
  assert.equal(other.client.status, "absent"); assert.equal(other.requests.length, 0);
});
test("higher health API falls back even when endpoint advertises supported API", async t => {
  const s = await stub(t); s.setApi(2); await s.client.start();
  assert.equal(s.client.status, "absent"); assert.equal(s.streams.size, 0);
});
test("loss defers jobs until grace expires, recovery cancels fallback, file watch detects arrival", async t => {
  const s = await stub(t); s.setFile(null); await s.client.start();
  s.setFile(JSON.stringify(s.document)); s.changed(); await s.client.check();
  assert.equal(s.client.status, "alive");
  s.setHealthy(false); await s.client.check(); assert.equal(s.client.status, "lost");
  await s.clock.advance(9999); assert.equal(s.client.deferred, true);
  s.setHealthy(true); await s.client.check(); assert.equal(s.client.status, "alive");
  s.setHealthy(false); await s.client.check(); await s.clock.advance(10000); await s.client.check();
  assert.equal(s.client.status, "absent"); assert.equal(s.client.deferred, false);
  assert.deepEqual(s.states, ["alive", "lost", "alive", "lost", "absent"]);
});
test("request failure triggers health recheck and errors never expose response secrets", async t => {
  const s = await stub(t); await s.client.start();
  // Wrong auth creates a 401; failing health establishes loss as well.
  s.document.token = "b".repeat(64); s.setHealthy(false);
  await assert.rejects(s.client.proxy("a", { type: "ready" }), { message: "Companion request failed" });
  await s.client.check(); assert.equal(s.client.status, "lost");
  await assert.rejects(s.client.proxy("a", {}), { message: "Companion unavailable" });
});
test("SSE forwards events and reconnects after EOF without restarting fallback", async t => {
  let connects = 0;
  const s = await stub(t, { onConnect: () => { connects++; } }); await s.client.start();
  await until(() => s.streams.size === 1);
  const event = { instance: "agent-vm", message: { type: "audio", enabled: true } };
  [...s.streams][0].write('event: message\ndata: ' + JSON.stringify(event) + '\n\n');
  await until(() => s.events.length === 1); assert.deepEqual(s.events[0], { type: "message", data: event });
  [...s.streams][0].end(); await until(() => s.clock.jobs.size >= 2);
  await s.clock.advance(1000); await s.client.check(); await until(() => connects === 2);
  assert.deepEqual(s.states, ["alive"]);
});
test("disposing closes stream/watch/timers and prevents late health from reviving client", async t => {
  const s = await stub(t); await s.client.start(); await until(() => s.streams.size === 1);
  s.client.dispose(); await until(() => s.streams.size === 0);
  assert.equal(s.watches, 0); assert.equal(s.clock.jobs.size, 0);
  await assert.rejects(s.client.snapshot("a"), { message: "Companion unavailable" });
});
test("settings migration copies only non-default host settings using extension's actual 20s default", () => {
  assert.deepEqual(c.planSettingsMigration(c.SETTING_DEFAULTS), {});
  assert.deepEqual(c.planSettingsMigration({ micDevice: "mic", notifications: false, "forwards.enabled": false, "forwards.hostLabel": "pc", repatchDelaySeconds: 0, uiTheme: "classic" }),
    { micDevice: "mic", notifications: false, forwards: { enabled: false, hostLabel: "pc" }, repatchDelaySeconds: 0 });
  assert.deepEqual(c.planSettingsMigration({ repatchDelaySeconds: NaN, notifications: "false" }), {});
});
test("token migration argv is pinned; only stdin carries the token", () => {
  const p = c.planTokenMigration({ url: "https://host:7443", libPath: "C:\\user's\\lib.ps1", env: { LOCALAPPDATA: "/user" } });
  assert.equal(p.file, "powershell.exe");
  assert.deepEqual(p.args, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference = 'Stop'; try { . 'C:\\user''s\\lib.ps1'; $token = [Console]::In.ReadToEnd(); Save-ConstructRemoteToken -BaseUrl 'https://host:7443' -Token $token -StoreDir '/user/The-Construct/remote' | Out-Null; $token = $null; exit 0 } catch { exit 1 }"]);
  assert.equal(p.secretKey, "construct.remote.token:host_7443");
});
function migrationHarness() {
  const markers = new Map(), files = new Set(), calls = [];
  const env = { LOCALAPPDATA: "/user" }, url = "https://host:7443", token = "private-value";
  return { markers, files, calls, options: {
    globalState: { get: k => markers.get(k), update: async (k, v) => markers.set(k, v) },
    settings: { notifications: false }, putSettings: async patch => calls.push({ patch }), hosts: [{ url }],
    secrets: { get: async () => token }, fs: { existsSync: file => files.has(file) }, env, libPath: "/lib/AgentVm.Remote.ps1",
    run: async (file, args, input) => { assert.equal(input, token); assert.equal(args.join(" ").includes(token), false); calls.push({ file, args }); files.add(path.join(remotehost.remoteStoreDir(env), "host_7443.token")); return { code: 0 }; },
  } };
}
test("migrations mark only successful writes and run once per local host/remote host", async () => {
  const h = migrationHarness(); await c.migrate(h.options); await c.migrate(h.options);
  assert.equal(h.calls.length, 2); assert.equal(h.markers.size, 2);
});
test("existing DPAPI token is preserved, missing SecretStorage remains retryable", async () => {
  const h = migrationHarness(); h.options.secrets.get = async () => null;
  await c.migrate(h.options); assert.equal(h.markers.has(c.TOKEN_MARKER + "host_7443"), false);
  h.files.add("/user/The-Construct/remote/host_7443.token"); await c.migrate(h.options);
  assert.equal(h.markers.has(c.TOKEN_MARKER + "host_7443"), true); assert.equal(h.calls.length, 1);
});
test("failed settings/token migrations leave markers unset and discard runner diagnostics", async () => {
  const h = migrationHarness(); h.options.putSettings = async () => { throw new Error("failed"); };
  await assert.rejects(c.migrate(h.options)); assert.equal(h.markers.size, 0);
  h.options.putSettings = async () => {}; h.options.run = async () => { throw new Error("private-value"); };
  await assert.rejects(c.migrate(h.options), { message: "Companion token migration failed" });
  assert.equal(h.markers.has(c.TOKEN_MARKER + "host_7443"), false);
});

// Execute the actual extension shell with injected VS Code/process seams. The
// exported test accessor is appended in this VM only, not shipped by the extension.
function extensionHarness(overrides = {}) {
  const filename = path.resolve(__dirname, "../extension.js"), realRequire = createRequire(filename);
  const timers = fakeClock(), commands = new Map(), webviews = [], warnings = [], config = { companion: "auto", ...overrides.config };
  let configurationChanged, viewProvider;
  const disposable = { dispose() {} };
  const values = new Map();
  const state = { get: k => values.get(k), update: async (k, v) => values.set(k, v) };
  const vscode = {
    workspace: { getConfiguration: () => ({ get: (key, fallback) => config[key] === undefined ? fallback : config[key] }), onDidChangeConfiguration: fn => { configurationChanged = fn; return disposable; } },
    env: {}, StatusBarAlignment: { Left: 1 }, ViewColumn: { Active: 1 }, ProgressLocation: { Notification: 1 },
    Uri: { joinPath: (base, ...parts) => ({ fsPath: path.join(base.fsPath, ...parts), toString() { return this.fsPath; } }) },
    commands: { registerCommand: (id, fn) => { commands.set(id, fn); return disposable; }, executeCommand: id => commands.get(id)() },
    window: { createOutputChannel: () => ({ appendLine() {} }), createStatusBarItem: () => ({ ...disposable, show() {}, hide() {} }),
      registerWebviewViewProvider: (_id, provider) => { viewProvider = provider; return disposable; }, registerUriHandler: () => disposable,
      registerWebviewPanelSerializer: () => disposable, showWarningMessage: text => warnings.push(text),
      createWebviewPanel: () => {
        const webview = { options: {}, cspSource: "test", asWebviewUri: uri => uri, postMessage: message => { webview.messages.push(message); }, messages: [], onDidReceiveMessage: fn => { webview.receive = fn; } };
        const p = { webview, active: true, onDidDispose() {}, onDidChangeViewState(cb) { webview.activate = () => { p.active = true; cb(); }; }, reveal() { if (!p.active) webview.activate(); } }; webview.hide = () => { p.active = false; }; webviews.push(webview); return p;
      },
    },
  };
  const context = { subscriptions: [], globalState: state, workspaceState: state, secrets: { get: async () => null }, extensionUri: { fsPath: path.resolve(__dirname, "..") } };
  const sandbox = { module: { exports: {} }, Buffer, console, process: { ...process, env: {}, ...(overrides.process || {}) }, ...timers.timers,
    require: id => id === "vscode" ? vscode : overrides.modules && overrides.modules[id] || realRequire(id), __dirname: path.dirname(filename) };
  vm.runInNewContext(fs.readFileSync(filename, "utf8") + `\nmodule.exports.test = {
    handleMessage, refreshAll, syncAutoRefresh, startNotifyWatch, startForwarder, runCompanionMigration, deliverNotification,
    requestAudioEnable, scheduleStartupRepatch, runConfigSync, companionPresenceChanged,
    openPanelHere, setupPanel, augmentUpdates,
    surfaceTest: callback => { refreshState = callback; syncAutoRefresh = () => {}; resolveScriptsDirFor = () => null; },
    sidebar: context => new ConstructViewProvider(context),
    setClient: value => { companionClient = value; },
    setActiveInstance: value => { activeInstance = () => value; },
    attach: webview => liveWebviews.add(webview),
    setSessions: (forward, audio) => { forwarderSession = forward; hostAudio = audio; },
    state: () => ({ autoRefreshTimer, notifyChild, repatchTimer, configWatcher, hostAudio, forwarderSession }),
    setContext: value => { extensionContext = value; },
    holdDetection: ready => { companionStarting = true; companionReady = ready.then(() => { companionStarting = false; }); },
  };`, sandbox, { filename });
  return { extension: sandbox.module.exports, context, timers, commands, webviews, warnings, config, changed: () => configurationChanged({ affectsConfiguration: name => name === "construct.uiTheme" }) };
}
test("actual extension activation with Companion starts no fallback jobs; editor tab is proxied", async t => {
  const s = await stub(t);
  let options;
  const h = extensionHarness({ modules: { "./src/companion": { ...c, createClient: opts => { options = opts; return s.client; } } } });
  // Stub's client is real HTTP, while the callback is exercised separately below.
  await h.extension.activate(h.context);
  assert.equal(s.client.status, "alive"); assert.equal(h.timers.jobs.size, 0);
  await h.commands.get("construct.openPanel")();
  h.extension.test.setActiveInstance({ name: "work-vm", service: { url: "https://host:7443" } });
  await h.commands.get("construct.openHostAdmin")();
  assert.deepEqual(s.requests.filter(r => r.path === "/v1/ui/activate").at(-1).body, { view: "hostadmin", instance: "work-vm", host: "host_7443" });
  h.extension.test.setActiveInstance(require("../src/instances").DEFAULT_INSTANCE);
  assert.equal(h.webviews.length, 0);
  h.commands.get("construct.openPanelHere")(); const webview = h.webviews[0];
  await webview.receive({ type: "ready" });
  assert.equal(webview.messages[0].type, "state"); assert.equal(webview.messages[0].state.connectedInstance, null);
  const message = { type: "saveSettings", form: { mic: false } }; await webview.receive(message);
  assert.deepEqual(s.requests.find(r => r.path.endsWith("/messages")).body, message);
  options.onEvent("message", { instance: "other", message: { type: "audio", enabled: true } });
  assert.equal(webview.messages.some(m => m.type === "audio"), false);
  options.onEvent("message", { instance: "agent-vm", message: { type: "audio", enabled: true } });
  assert.equal(webview.messages.at(-1).type, "audio");
  h.config.uiTheme = "terminal"; h.changed(); await until(() => s.requests.some(r => r.method === "PUT"));
  assert.equal(s.requests.find(r => r.method === "PUT").body.uiTheme, "terminal");
  h.extension.deactivate(); assert.equal(h.timers.jobs.size, 0);
});
test("actual extension handoff releases forwarding claim, disposes audio locally and blocks queued starts", async () => {
  const h = extensionHarness(); const api = h.extension.test; api.setContext(h.context);
  const calls = [];
  const client = { status: "alive", deferred: true, putSettings: async () => {}, snapshot: async () => ({}) };
  api.setClient(client);
  api.setSessions({ dispose: () => calls.push("release-forward") }, { dispose: () => calls.push("dispose-audio"), disable: () => assert.fail("must preserve shared guest shim") });
  await api.companionPresenceChanged("alive", "absent");
  assert.deepEqual(calls.sort(), ["dispose-audio", "release-forward"]);
  api.startNotifyWatch(); await api.startForwarder(); api.requestAudioEnable(h.context); api.scheduleStartupRepatch(h.context); await api.runConfigSync(); api.syncAutoRefresh();
  assert.equal(h.timers.jobs.size, 0);
  assert.equal(api.state().hostAudio, undefined); assert.equal(api.state().forwarderSession, null);
});

test("actual extension transitions fallback -> Companion -> grace -> fallback with HTTP detection", async t => {
  let options;
  const s = await stub(t, { onState: (...args) => options.onState(...args), onConnect: () => options.onConnect() });
  s.setFile(null);
  const h = extensionHarness({ modules: { "./src/companion": { ...c, createClient: opts => { options = opts; return s.client; } } } });
  t.after(() => h.extension.deactivate());
  await h.extension.activate(h.context);
  assert.equal(s.client.status, "absent"); assert.equal(h.timers.jobs.size, 3);
  s.setFile(JSON.stringify(s.document)); await s.client.check();
  assert.equal(s.client.status, "alive"); assert.equal(h.timers.jobs.size, 0);
  s.setHealthy(false); await s.client.check();
  assert.equal(s.client.status, "lost"); assert.equal(h.timers.jobs.size, 0);
  await s.clock.advance(9999); assert.equal(h.timers.jobs.size, 0);
  await s.clock.advance(1); await s.client.check();
  assert.equal(s.client.status, "absent"); assert.equal(h.timers.jobs.size, 3);
  s.setHealthy(true); await s.client.check();
  assert.equal(s.client.status, "alive"); assert.equal(h.timers.jobs.size, 0);
});

test("migration runner records argv/stdin and cancels its child on deactivate", async () => {
  const { EventEmitter } = require("events");
  const recorded = [];
  const child = new EventEmitter(); child.stdin = new EventEmitter(); child.stdin.end = input => recorded.push({ input });
  child.kill = () => { recorded.push({ killed: true }); child.emit("close", -1); };
  const h = extensionHarness({ modules: { child_process: { spawn: (...args) => { recorded.push(args); return child; } } } });
  const plan = c.planTokenMigration({ url: "https://host:7443", libPath: "/lib.ps1", env: { LOCALAPPDATA: "/user" } });
  const promise = h.extension.test.runCompanionMigration(plan.file, plan.args, "private-value");
  assert.equal(recorded[0][0], "powershell.exe"); assert.deepEqual(recorded[0][1], plan.args);
  assert.equal(JSON.stringify(recorded[0][2]), JSON.stringify({ windowsHide: true, stdio: ["pipe", "ignore", "ignore"] }));
  assert.equal(recorded[1].input, "private-value"); assert.equal(JSON.stringify(recorded[0]).includes("private-value"), false);
  h.extension.deactivate(); assert.equal((await promise).code, -1); assert.equal(h.timers.jobs.size, 0);
  assert.equal(recorded[2].killed, true);
  await h.extension.test.runCompanionMigration(plan.file, plan.args, "private-value");
  assert.equal(recorded.length, 3);
});
test("token migration removes URL userinfo and query before building argv", () => {
  const plan = c.planTokenMigration({ url: "https://user:private-value@host:7443/?token=private-value", libPath: "/lib.ps1", env: { LOCALAPPDATA: "/user" } });
  assert.equal(plan.args.join(" ").includes("private-value"), false);
  assert.ok(plan.args.at(-1).includes("-BaseUrl 'https://host:7443'"));
});
test("late failed Windows toast cannot fall back to a VS Code notification after handoff", async () => {
  const { EventEmitter } = require("events");
  const child = new EventEmitter(); child.stdout = new EventEmitter(); child.stderr = new EventEmitter();
  const h = extensionHarness({ process: { platform: "win32" }, modules: { child_process: { spawn: () => child } } });
  const pending = h.extension.test.deliverNotification({ level: "warning", title: "test", body: "late" });
  h.extension.test.setClient({ deferred: true, status: "alive" });
  child.emit("close", 1); await pending;
  assert.equal(h.warnings.length, 0); assert.equal(h.timers.jobs.size, 0);
});
test("messages posted during startup detection wait and then proxy exactly once", async () => {
  const h = extensionHarness(); let resolve;
  h.extension.test.holdDetection(new Promise(r => { resolve = r; }));
  const calls = [];
  h.extension.test.setClient({ status: "alive", deferred: true, proxy: async (...args) => calls.push(args) });
  const message = { type: "saveSettings", form: { mic: true } };
  const pending = h.extension.test.handleMessage(message, {}, h.context);
  await tick(); assert.equal(calls.length, 0);
  resolve(); await pending;
  assert.equal(calls.length, 1); assert.deepEqual(calls[0], ["agent-vm", message]);
});

test("independent children messages cross the proxy unchanged", () => {
  const inventory = { type: "children", instance: "remote-vm", children: { primary: "remote-vm", visible: true, items: [{ name: "shared", canConsole: true }], problem: "" } };
  assert.strictEqual(c.overlayMessage(inventory, "agent-vm"), inventory);
});

test("opening a panel or visible sidebar bypasses exactly the next Construct augmentation", async () => {
  const calls = [];
  const h = extensionHarness({ modules: { "./src/updates": { augment: async (state, _raw, opts) => { calls.push(opts.constructNoCache); return state; } } } });
  const api = h.extension.test;
  let refreshes = 0;
  api.surfaceTest(() => { refreshes++; });
  const instance = require("../src/instances").DEFAULT_INSTANCE;
  api.setActiveInstance(instance);
  async function consumedOnce() {
    await api.augmentUpdates({}, instance); await api.augmentUpdates({}, instance);
    assert.deepEqual(calls.splice(0), [true, false]);
  }
  api.openPanelHere(h.context); await consumedOnce();
  api.openPanelHere(h.context); assert.equal(refreshes, 1); await consumedOnce();
  h.webviews[0].hide(); api.openPanelHere(h.context); assert.equal(refreshes, 2); await consumedOnce();
  let visibility;
  const sidebar = { webview: h.webviews[0], visible: true, onDidChangeVisibility: cb => { visibility = cb; return { dispose() {} }; }, onDidDispose: () => ({ dispose() {} }) };
  api.sidebar(h.context).resolveWebviewView(sidebar); await consumedOnce();
  sidebar.visible = false; visibility(); assert.equal(refreshes, 2);
  sidebar.visible = true; visibility(); assert.equal(refreshes, 3); await consumedOnce();
});
