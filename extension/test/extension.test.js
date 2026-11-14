"use strict";
// extension.js against a fake `vscode` module and a stubbed agent: the view's start and install flow,
// the panel and its routes, the bridge, Remote-SSH and the register offer. Run: node --test extension/test/
const test = require("node:test");
const assert = require("node:assert/strict");
const Module = require("node:module");
const path = require("node:path");

const TOKEN = "k".repeat(43);
const ENDPOINT = { port: 5123, token: TOKEN, pid: 1, startedAt: null };

function fakeVscode() {
  const v = {
    calls: [], messages: [], views: {}, commands: {}, panels: [], answers: [],
    ExtensionKind: { UI: 1, Workspace: 2 },
    ViewColumn: { Active: -1 },
    env: { remoteName: undefined },
    workspace: { workspaceFolders: [], fs: { readFile: async () => { throw new Error("none"); } } },
    extensions: { getExtension: id => (id === "ms-vscode-remote.remote-ssh" ? {} : undefined) },
    Uri: {
      parse: s => ({ toString: () => s, s }),
      joinPath: (u, ...p) => ({ ...u, path: path.posix.join(u.path || "", ...p), fsPath: path.join(u.fsPath || "", ...p) }),
    },
  };
  const webview = () => {
    const w = { html: "", options: null, posted: [], handler: null };
    w.postMessage = m => { w.posted.push(m); return Promise.resolve(true); };
    w.onDidReceiveMessage = f => { w.handler = f; };
    return w;
  };
  v.window = {
    registerWebviewViewProvider: (id, p) => { v.views[id] = p; return { dispose() {} }; },
    registerWebviewPanelSerializer: () => ({ dispose() {} }),
    createWebviewPanel: (type, title, col, opts) => {
      const p = { type, opts, webview: webview(), revealed: 0, reveal() { this.revealed++; }, onDidDispose(f) { this.disposeHandler = f; } };
      v.panels.push(p);
      return p;
    },
    showInformationMessage: async (text, ...items) => { v.messages.push({ level: "info", text, items }); return v.answers.shift(); },
    showWarningMessage: async (text, ...items) => { v.messages.push({ level: "warn", text, items }); return v.answers.shift(); },
    showErrorMessage: async (text, ...items) => { v.messages.push({ level: "error", text, items }); return v.answers.shift(); },
    showQuickPick: async items => items[0],
  };
  v.commands.registerCommand = (id, f) => { v.commands[id] = f; return { dispose() {} }; };
  v.commands.executeCommand = async (...args) => { v.calls.push(args); };
  v.newView = () => {
    const view = { webview: webview(), onDidDispose() {} };
    v.views["construct.panel"].resolveWebviewView(view);
    return view;
  };
  return v;
}

/** A fresh extension.js with `vscode` faked and the agent module stubbed. */
function load(t, { kind = 1, agentStub = {}, guest = false } = {}) {
  const vscode = fakeVscode();
  // This test may run on a Construct VM itself: the guest marker is faked either way.
  const fs = require("node:fs");
  const exists = fs.existsSync;
  fs.existsSync = p => (p === "/etc/construct/config.env" ? guest : exists(p));
  t.after(() => { fs.existsSync = exists; });
  const resolve = Module._resolveFilename;
  Module._resolveFilename = function (request, ...rest) { return request === "vscode" ? "vscode" : resolve.call(this, request, ...rest); };
  require.cache.vscode = { id: "vscode", filename: "vscode", loaded: true, exports: vscode };
  const agentPath = require.resolve("../src/agent");
  const extPath = require.resolve("../extension.js");
  delete require.cache[extPath];
  delete require.cache[agentPath];
  const agent = require("../src/agent");
  const requests = [];
  Object.assign(agent, {
    find: async () => ({ state: "running", endpoint: ENDPOINT }),
    request: async (endpoint, method, route, opts) => { requests.push({ method, route, body: opts.body, token: opts.token }); return { status: 200, body: null }; },
  }, agentStub);
  const ext = require("../extension.js");
  t.after(() => {
    ext.deactivate();
    Module._resolveFilename = resolve;
    delete require.cache.vscode;
    delete require.cache[extPath];
    delete require.cache[agentPath];
  });
  const state = {};
  const context = {
    subscriptions: [], extensionUri: { fsPath: "/ext", path: "/ext" }, extension: { extensionKind: kind },
    globalState: { get: (k, d) => (k in state ? state[k] : d), update: async (k, val) => { state[k] = val; } },
  };
  ext.activate(context);
  return { vscode, agent, requests, ext, context, state };
}

const tick = () => new Promise(r => setTimeout(r, 5));

test("a running agent: the view shows the frame, and the token goes only into the load message", async t => {
  const { vscode } = load(t);
  const view = vscode.newView();
  await tick();
  assert.match(view.webview.html, /frame-src http:\/\/127\.0\.0\.1:5123;/);
  assert.doesNotMatch(view.webview.html, new RegExp(TOKEN));
  assert.deepEqual(view.webview.options.localResourceRoots, []);
  assert.equal(view.webview.options.enableCommandUris, false);
  await view.webview.handler({ type: "ready" });
  assert.deepEqual(view.webview.posted, [{ type: "load", url: `http://127.0.0.1:5123/?surf=pop#agent-token=${TOKEN}`, then: null }]);
});

test("a stopped agent is started in the background", async t => {
  const started = [];
  let running = false;
  const { vscode } = load(t, { agentStub: {
    find: async () => (running ? { state: "running", endpoint: ENDPOINT } : { state: "stopped", exe: "/h/versions/1/construct-agent", home: "/h" }),
    start: found => { started.push(found.exe); running = true; },
    waitRunning: async () => ({ state: "running", endpoint: ENDPOINT }),
  } });
  const view = vscode.newView();
  await tick();
  assert.deepEqual(started, ["/h/versions/1/construct-agent"]);
  assert.match(view.webview.html, /<iframe/);
});

test("an agent that does not come up says so, with a retry", async t => {
  const { vscode } = load(t, { agentStub: {
    find: async () => ({ state: "stopped", exe: "/x", home: "/" }), start: () => {}, waitRunning: async () => null,
  } });
  const view = vscode.newView();
  await tick();
  assert.match(view.webview.html, /did not start/);
  assert.match(view.webview.html, /data-id="start"/);
});

test("no agent: one button installs it, and a failure is shown in place", async t => {
  let installed = false;
  const installs = [];
  let result = { ok: false, error: "The package does not match the SHA-256 in the channel manifest." };
  const { vscode } = load(t, { agentStub: {
    find: async () => (installed ? { state: "running", endpoint: ENDPOINT } : { state: "missing", home: "/h" }),
    install: async dir => { installs.push(dir); if (result.ok) installed = true; return result; },
    waitRunning: async () => ({ state: "running", endpoint: ENDPOINT }),
  } });
  const view = vscode.newView();
  await tick();
  assert.match(view.webview.html, /not installed/);
  assert.match(view.webview.html, /data-id="install"/);
  await view.webview.handler({ type: "action", id: "install" });
  await tick();
  assert.deepEqual(installs, [path.join("/ext")]);
  assert.match(view.webview.html, /The install failed/);
  assert.match(view.webview.html, /does not match the SHA-256/);
  result = { ok: true };
  await view.webview.handler({ type: "action", id: "install" });
  await tick();
  assert.match(view.webview.html, /<iframe/);
});

test("commands open the window surface at their route; a second one reuses it", async t => {
  const { vscode } = load(t);
  await vscode.commands["construct.openHostAdmin"]();
  await tick();
  assert.equal(vscode.panels.length, 1);
  const p = vscode.panels[0];
  assert.equal(p.opts.enableCommandUris, false);
  await p.webview.handler({ type: "ready" });
  assert.deepEqual(p.webview.posted[0], { type: "load", url: `http://127.0.0.1:5123/?surf=win#agent-token=${TOKEN}`, then: "http://127.0.0.1:5123/?surf=win#/hosts" });
  await vscode.commands["construct.newRemoteVm"]();
  assert.equal(vscode.panels.length, 1);
  assert.equal(p.revealed, 1);
  assert.deepEqual(p.webview.posted[1], { type: "route", url: "http://127.0.0.1:5123/?surf=win#/create" });
});

test("the bridge: the pop-out's openWindow opens the panel; anything else is dropped", async t => {
  const { vscode } = load(t);
  const view = vscode.newView();
  await tick();
  await view.webview.handler({ type: "page", message: { type: "run", command: "rm" } });
  await view.webview.handler({ type: "page", message: { type: "openWindow", route: "javascript:x" } });
  assert.equal(vscode.panels.length, 0);
  await view.webview.handler({ type: "page", message: { type: "openWindow", route: "#/vault" } });
  await tick();
  assert.equal(vscode.panels.length, 1);
  await vscode.panels[0].webview.handler({ type: "ready" });
  assert.equal(vscode.panels[0].webview.posted[0].then, "http://127.0.0.1:5123/?surf=win#/vault");
});

test("opening a VM in VS Code: the agent writes the alias, the extension opens the remote window", async t => {
  const { vscode, requests, agent } = load(t);
  agent.request = async (endpoint, method, route, opts) => {
    requests.push({ method, route, token: opts.token });
    if (route === "/api/connections") return { status: 200, body: [{ engine: "e1", vm: "work", alias: "work", state: "running" }] };
    return { status: 200, body: {} };
  };
  const view = vscode.newView();
  await tick();
  await view.webview.handler({ type: "page", message: { type: "openVsCode", engine: "e1", vm: "work" } });
  assert.deepEqual(requests.map(r => `${r.method} ${r.route}`), ["PUT /api/connections/e1/work/ssh-config", "GET /api/connections"]);
  assert.ok(requests.every(r => r.token === TOKEN));
  assert.equal(vscode.calls.length, 1);
  assert.equal(vscode.calls[0][0], "vscode.openFolder");
  assert.equal(vscode.calls[0][1].s, "vscode-remote://ssh-remote+work/root/repos");
  assert.deepEqual(vscode.calls[0][2], { forceNewWindow: true });
  await vscode.commands["construct.openVm"]();
  assert.equal(vscode.calls.length, 2, "the command picks the VM and opens it too");
});

for (const [where, opts] of [["in a remote extension host", { kind: 2 }], ["inside a Construct VM", { guest: true }]]) {
  test(`${where} it touches nothing of the agent`, async t => {
    let asked = 0;
    const { vscode } = load(t, { ...opts, agentStub: { find: async () => { asked++; return { state: "running", endpoint: ENDPOINT }; } } });
    const view = { webview: { html: "", options: null }, onDidDispose() {} };
    vscode.views["construct.panel"].resolveWebviewView(view);
    await vscode.commands["construct.openPanel"]();
    await vscode.commands["construct.registerWorkspace"]();
    await tick();
    assert.equal(asked, 0);
    assert.match(view.webview.html, /runs on your PC/);
    assert.equal(vscode.panels.length, 0);
  });
}

function remoteFolder(alias, folderPath) {
  return { uri: { scheme: "vscode-remote", authority: "ssh-remote+" + alias, path: folderPath, fsPath: folderPath } };
}

test("register this folder: written through the store engine's profile route and switched on for the VM", async t => {
  const { vscode, requests, agent, state } = load(t);
  vscode.workspace.workspaceFolders = [remoteFolder("work", "/root/repos/shop")];
  vscode.workspace.fs.readFile = async uri => {
    assert.equal(uri.path, "/root/repos/shop/.git/config");
    return Buffer.from('[remote "origin"]\n\turl = https://github.com/o/shop.git\n');
  };
  const answers = {
    "GET /api/connections": [{ engine: "e1", vm: "work", alias: "work" }],
    "GET /api/state": { device: { registered: true, storeEngine: "store1" }, engines: [{ id: "e1" }, { id: "store1" }] },
    "GET /api/engines/store1/api/v1/profiles": [{ name: "api", revision: 3 }],
    "GET /api/engines/store1/api/v1/profiles/api": { name: "api", revision: 3, profile: { repos: [{ url: "https://github.com/o/api.git" }] } },
    "PUT /api/engines/store1/api/v1/profiles/shop": { status: "applied", revision: 1 },
    "GET /api/engines/e1/api/v1/vms/work/desired": { desired: { projects: ["api"], settings: { a: 1 } } },
    "PUT /api/engines/e1/api/v1/vms/work/desired": {},
  };
  agent.request = async (endpoint, method, route, opts) => { requests.push({ method, route, body: opts.body }); return { status: 200, body: answers[`${method} ${route}`] ?? null }; };
  vscode.answers.push("Register");
  await vscode.commands["construct.registerWorkspace"]();
  const put = requests.find(r => r.method === "PUT" && r.route.endsWith("/profiles/shop"));
  assert.deepEqual(put.body, { profile: { name: "shop", repos: [{ url: "https://github.com/o/shop.git", directory: "shop" }], sdks: {}, mcp: [], hostPackages: [], provisionCommands: [], tests: {} }, baseRevision: null });
  const desired = requests.find(r => r.method === "PUT" && r.route.endsWith("/desired"));
  assert.deepEqual(desired.body, { projects: ["api", "shop"], settings: { a: 1 } });
  assert.match(vscode.messages.at(-1).text, /Registered shop/);

  // Covered now: the command explains instead of offering; "never" is remembered per repository.
  requests.length = 0;
  answers["GET /api/engines/store1/api/v1/profiles"] = [{ name: "store", revision: 1 }];
  answers["GET /api/engines/store1/api/v1/profiles/store"] = { name: "store", revision: 1, profile: { repos: [{ url: "https://github.com/o/shop.git" }] } };
  await vscode.commands["construct.registerWorkspace"]();
  assert.match(vscode.messages.at(-1).text, /already has this repository/);
  assert.ok(!requests.some(r => r.method === "PUT"));
  answers["GET /api/engines/store1/api/v1/profiles"] = [];
  vscode.answers.push("Never for this repository");
  await vscode.commands["construct.registerWorkspace"]();
  assert.deepEqual(state["construct.registerDismissed"], ["https://github.com/o/shop.git"]);
  assert.ok(!requests.some(r => r.method === "PUT"));
});

test("register this folder: nothing to offer in a local window or for another host", async t => {
  const { vscode, agent } = load(t);
  agent.request = async (e, method, route) => ({ status: 200, body: route === "/api/connections" ? [{ engine: "e1", vm: "work", alias: "work" }] : { device: { registered: true, storeEngine: "e1" }, engines: [] } });
  await vscode.commands["construct.registerWorkspace"]();
  assert.match(vscode.messages.at(-1).text, /no folder open on a VM/);
  vscode.workspace.workspaceFolders = [remoteFolder("somewhere-else", "/root/repos/shop")];
  await vscode.commands["construct.registerWorkspace"]();
  assert.match(vscode.messages.at(-1).text, /not attached to one of your Construct VMs/);
});
