"use strict";
// The Construct in VS Code: a window onto the device agent (docs/plans/construct-platform-design.md
// 11.4). The sidebar view shows the agent's pop-out surface and the editor panel its window surface;
// everything they do is the agent's. What stays here is what only an extension can do: start or
// install the agent, open a VM in Remote-SSH, and offer to register the open workspace as a project.

const vscode = require("vscode");
const fs = require("fs");
const crypto = require("crypto");
const agent = require("./src/agent");
const page = require("./src/page");
const workspace = require("./src/workspace");

const REMOTE_SSH = "ms-vscode-remote.remote-ssh";
const ALIAS_RE = /^[A-Za-z0-9._-]{1,128}$/;
const DISMISSED_KEY = "construct.registerDismissed";
const PANEL_TYPE = "construct.window";
const OLD_PANEL_TYPE = "construct.controlPanel";
const POLL_MS = 10000;

let context = null;
let panel = null;
const surfaces = new Set();
let pollTimer = null;
let installing = null;
const askedThisSession = new Set();

function nonce() { return crypto.randomBytes(18).toString("base64url"); }

/** VS Code runs this extension on the user's PC (extensionKind ui). Anywhere else it does nothing. */
function localHost() {
  if (context.extension && context.extension.extensionKind === vscode.ExtensionKind.Workspace) return false;
  // A Construct VM (VS Code opened inside the guest): the agent belongs on the user's PC.
  try { if (fs.existsSync("/etc/construct/config.env")) return false; } catch (_) { /* treat as a PC */ }
  return true;
}

function webviewOptions() {
  return { enableScripts: true, enableCommandUris: false, localResourceRoots: [] };
}

// ── agent ────────────────────────────────────────────────────────────────────

/** The running agent's endpoint, or null. Never starts it. */
async function runningEndpoint() {
  const found = await agent.find();
  return found.state === "running" ? found.endpoint : null;
}

/** A request to the agent with its token; throws with the agent's problem title on an error. */
async function call(method, route, body) {
  const endpoint = await runningEndpoint();
  if (!endpoint) throw new Error("The Construct agent is not running.");
  const r = await agent.request(endpoint, method, route, { token: endpoint.token, body });
  if (r.status < 200 || r.status >= 300) {
    const title = r.body && typeof r.body === "object" && typeof r.body.title === "string" ? r.body.title : `HTTP ${r.status}`;
    throw new Error(title);
  }
  return r.body;
}

function enc(s) { return encodeURIComponent(s); }

// ── surfaces ─────────────────────────────────────────────────────────────────

/** One webview showing the agent: the sidebar view ("pop") or the panel ("win"). */
class Surface {
  constructor(webview, surface) {
    this.webview = webview;
    this.surface = surface;
    this.endpoint = null;
    this.route = null;
    this.busy = false;
    webview.options = webviewOptions();
    webview.onDidReceiveMessage(m => this.onMessage(m));
    surfaces.add(this);
    syncPoll();
  }

  dispose() { surfaces.delete(this); syncPoll(); }

  status(title, text, detail, button) {
    this.endpoint = null;
    this.webview.html = page.statusHtml({ nonce: nonce(), title, text, detail, button });
  }

  /** Shows the agent's page, starting the agent when it is installed and not running. */
  async show() {
    if (this.busy) return;
    this.busy = true;
    try {
      let found = await agent.find();
      if (found.state === "missing") {
        if (installing) return this.status("Installing the Construct agent", "This takes a minute.");
        return this.status("The Construct agent is not installed", "The Construct runs as an agent on this PC; this view shows its window.",
          null, { id: "install", label: "Install the Construct agent" });
      }
      if (found.state === "stopped") {
        this.status("Starting the Construct agent", "");
        try { agent.start(found); } catch (e) { return this.status("The Construct agent could not start", "", e.message, { id: "start", label: "Try again" }); }
        found = await agent.waitRunning({ timeoutMs: 20000 });
        if (!found) return this.status("The Construct agent did not start", "Run construct-agent repair from its folder to see why.", null, { id: "start", label: "Try again" });
      }
      this.load(found.endpoint);
    } finally {
      this.busy = false;
    }
  }

  load(endpoint) {
    this.endpoint = endpoint;
    this.webview.html = page.frameHtml({ nonce: nonce(), port: endpoint.port });
  }

  /** Shows a route; the page loads first when it is not shown yet. */
  open(route) {
    if (this.endpoint) this.webview.postMessage({ type: "route", url: page.routeUrl(this.endpoint.port, this.surface, route) });
    else { this.route = route; void this.show(); }
  }

  async onMessage(m) {
    if (!m || typeof m !== "object") return;
    if (m.type === "ready" && this.endpoint) {
      // The token goes to the frame's address once; the page takes it out of its URL at once.
      const e = this.endpoint;
      const then = this.route ? page.routeUrl(e.port, this.surface, this.route) : null;
      this.route = null;
      this.webview.postMessage({ type: "load", url: page.frameUrl(e.port, e.token, this.surface), then });
      return;
    }
    if (m.type === "action" && m.id === "install") return runInstall();
    if (m.type === "action" && m.id === "start") return this.show();
    if (m.type === "page") return onPageMessage(page.pageMessage(m.message));
  }

  /** Called by the poll: reload when the agent restarted, say so when it stopped. */
  async check() {
    if (this.busy || !this.endpoint) return;
    const found = await agent.find();
    if (found.state !== "running") {
      this.status("The Construct agent stopped", "", null, { id: "start", label: "Start it" });
    } else if (found.endpoint.port !== this.endpoint.port || found.endpoint.token !== this.endpoint.token) {
      this.load(found.endpoint);
    }
  }
}

function syncPoll() {
  if (surfaces.size && !pollTimer) pollTimer = setInterval(() => { for (const s of surfaces) void s.check().catch(() => {}); }, POLL_MS);
  if (!surfaces.size && pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

async function runInstall() {
  if (installing) return installing;
  for (const s of surfaces) s.status("Installing the Construct agent", "This takes a minute.");
  installing = agent.install(context.extensionUri.fsPath).then(async result => {
    installing = null;
    if (!result.ok) {
      for (const s of surfaces) s.status("The install failed", "", result.error, { id: "install", label: "Try again" });
      return;
    }
    await agent.waitRunning({ timeoutMs: 30000 });
    for (const s of surfaces) void s.show();
  });
  return installing;
}

/** A message of the agent's page, checked by page.pageMessage. "close" has nothing to close in VS Code. */
function onPageMessage(m) {
  if (!m) return undefined;
  if (m.type === "openWindow") return openWindow(m.route);
  if (m.type === "openVsCode") return openVmInVsCode(m.engine, m.vm);
  return undefined;
}

function setupPanel(p) {
  panel = p;
  const s = new Surface(p.webview, "win");
  p.onDidDispose(() => { s.dispose(); if (panel === p) panel = null; });
  p.surface = s;
  return s;
}

function openWindow(route) {
  if (panel) {
    panel.reveal();
    if (route && route !== "#/") panel.surface.open(route);
    return;
  }
  const p = vscode.window.createWebviewPanel(PANEL_TYPE, "The Construct", vscode.ViewColumn.Active,
    { ...webviewOptions(), retainContextWhenHidden: true, enableFindWidget: false });
  p.iconPath = vscode.Uri.joinPath(context.extensionUri, "media", "icon.svg");
  const s = setupPanel(p);
  if (route && route !== "#/") s.route = route;
  void s.show();
}

// ── Remote-SSH ───────────────────────────────────────────────────────────────

/** Opens a VM of the user's in a new Remote-SSH window; the agent writes the SSH alias. */
async function openVmInVsCode(engine, vm) {
  try {
    if (!vscode.extensions.getExtension(REMOTE_SSH)) {
      const pick = await vscode.window.showWarningMessage("Opening a VM needs the Remote - SSH extension.", "Show it");
      if (pick) await vscode.commands.executeCommand("workbench.extensions.search", REMOTE_SSH);
      return;
    }
    await call("PUT", `/api/connections/${enc(engine)}/${enc(vm)}/ssh-config`);
    const conns = await call("GET", "/api/connections");
    const c = (Array.isArray(conns) ? conns : []).find(x => x && x.engine === engine && x.vm === vm);
    if (!c || typeof c.alias !== "string" || !ALIAS_RE.test(c.alias)) throw new Error(`The agent has no SSH alias for ${vm}.`);
    const uri = vscode.Uri.parse(`vscode-remote://ssh-remote+${c.alias}${workspace.WORKSPACE_ROOT}`);
    await vscode.commands.executeCommand("vscode.openFolder", uri, { forceNewWindow: true });
  } catch (e) {
    vscode.window.showErrorMessage(`Could not open ${vm} in VS Code: ${e.message}`);
  }
}

async function pickVmAndOpen() {
  let conns;
  try { conns = await call("GET", "/api/connections"); } catch (e) {
    const pick = await vscode.window.showErrorMessage(`${e.message}`, "Open the Construct");
    if (pick) openWindow("#/");
    return;
  }
  const items = (Array.isArray(conns) ? conns : []).filter(c => c && typeof c.vm === "string" && typeof c.engine === "string")
    .map(c => ({ label: c.vm, description: String(c.state || ""), engine: c.engine, vm: c.vm }));
  if (!items.length) { vscode.window.showInformationMessage("You have no VM to open yet."); return; }
  const chosen = items.length === 1 ? items[0] : await vscode.window.showQuickPick(items, { title: "Open a VM in VS Code" });
  if (chosen) await openVmInVsCode(chosen.engine, chosen.vm);
}

// ── register the workspace as a project ──────────────────────────────────────

async function readOrigin(folderUri) {
  try {
    const bytes = await vscode.workspace.fs.readFile(vscode.Uri.joinPath(folderUri, ".git", "config"));
    return workspace.originUrl(Buffer.from(bytes).toString("utf8"));
  } catch (_) { return ""; }
}

/** The profiles of the account, read through the engine that holds the store. */
async function readProfiles(storeEngine) {
  const list = await call("GET", `/api/engines/${enc(storeEngine)}/api/v1/profiles`);
  const out = [];
  for (const p of Array.isArray(list) ? list : []) {
    if (!p || typeof p.name !== "string") continue;
    try { out.push(await call("GET", `/api/engines/${enc(storeEngine)}/api/v1/profiles/${enc(p.name)}`)); } catch (_) { out.push({ name: p.name, profile: {} }); }
  }
  return out;
}

/** Each workspace folder of this window on a VM, with what the offer needs to know about it. */
async function evaluateFolders() {
  const folders = (vscode.workspace.workspaceFolders || [])
    .filter(f => f.uri.scheme === "vscode-remote" && workspace.sshAlias(f.uri.authority));
  if (!folders.length) return { reason: "This window has no folder open on a VM over Remote-SSH.", plans: [] };
  if (!(await runningEndpoint())) return { reason: "The Construct agent is not running.", plans: [] };
  const connections = await call("GET", "/api/connections");
  const state = await call("GET", "/api/state");
  const store = state && state.device && state.device.registered
    ? state.device.storeEngine || (Array.isArray(state.engines) && state.engines[0] && state.engines[0].id) : null;
  if (!store) return { reason: "This device is not set up yet; finish the setup in the Construct window.", plans: [] };
  let profiles = null;
  const dismissed = context.globalState.get(DISMISSED_KEY, []);
  const plans = [];
  for (const folder of folders) {
    const vm = workspace.attachedVm(connections, workspace.sshAlias(folder.uri.authority));
    const name = workspace.checkoutName(folder.uri.path);
    const url = vm && name ? await readOrigin(folder.uri) : "";
    if (vm && name && url && !profiles) profiles = await readProfiles(store);
    plans.push({ ...workspace.planOffer({ vm, name, url, profiles: profiles || [], dismissed }), vm, store });
  }
  return { reason: "", plans };
}

async function register(plan) {
  const answer = await call("PUT", `/api/engines/${enc(plan.store)}/api/v1/profiles/${enc(plan.name)}`,
    { profile: workspace.newProfile(plan.name, plan.url), baseRevision: null });
  if (answer && answer.status === "conflict") throw new Error(`${plan.name} changed meanwhile; an agent on the VM is resolving it.`);
  // Switched on for the VM it is checked out on, as the old import did.
  const desiredRoute = `/api/engines/${enc(plan.vm.engine)}/api/v1/vms/${enc(plan.vm.vm)}/desired`;
  const current = await call("GET", desiredRoute);
  const next = workspace.withProject(current && current.desired, plan.name);
  if (next) await call("PUT", desiredRoute, next);
}

async function offer(plan) {
  askedThisSession.add(plan.url);
  const pick = await vscode.window.showInformationMessage(
    `${plan.name} on ${plan.vm.vm} is not a Construct project. Register it, so a reinstall checks it out again?`,
    "Register", "Not now", "Never for this repository");
  if (pick === "Never for this repository") {
    const list = context.globalState.get(DISMISSED_KEY, []);
    if (!list.includes(plan.url)) await context.globalState.update(DISMISSED_KEY, [...list, plan.url]);
    return;
  }
  if (pick !== "Register") return;
  try {
    await register(plan);
    vscode.window.showInformationMessage(`Registered ${plan.name} as a project on ${plan.vm.vm}.`);
  } catch (e) {
    vscode.window.showErrorMessage(`Could not register ${plan.name}: ${e.message}`);
  }
}

/** At start, in a Remote-SSH window: offer once per folder, quietly when there is nothing to offer. */
async function maybeOffer() {
  try {
    const { plans } = await evaluateFolders();
    for (const plan of plans) if (plan.offer && !askedThisSession.has(plan.url)) await offer(plan);
  } catch (_) { /* the offer is optional; the command explains */ }
}

/** The command: the same decision, and the reason when there is nothing to offer. */
async function registerWorkspace() {
  try {
    const { reason, plans } = await evaluateFolders();
    const offered = plans.filter(p => p.offer);
    if (!offered.length) {
      vscode.window.showInformationMessage(reason || (plans[0] && plans[0].reason) || "Nothing to register.");
      return;
    }
    for (const plan of offered) await offer(plan);
  } catch (e) {
    vscode.window.showErrorMessage(`Could not check this workspace: ${e.message}`);
  }
}

// ── activation ───────────────────────────────────────────────────────────────

function elsewhereHtml() {
  return page.statusHtml({ nonce: nonce(), title: "The Construct runs on your PC",
    text: "Open this view in a VS Code window on your PC; there it shows the Construct agent's window." });
}

function activate(ctx) {
  context = ctx;
  if (!localHost()) {
    // Remote extension host or a Construct VM: nothing here reads the agent's files or token.
    ctx.subscriptions.push(vscode.window.registerWebviewViewProvider("construct.panel", {
      resolveWebviewView(view) { view.webview.options = { enableScripts: true, localResourceRoots: [] }; view.webview.html = elsewhereHtml(); },
    }));
    for (const id of ["construct.openPanel", "construct.openHostAdmin", "construct.newRemoteVm", "construct.openVm", "construct.registerWorkspace"]) {
      ctx.subscriptions.push(vscode.commands.registerCommand(id, () => vscode.window.showInformationMessage("The Construct runs in VS Code on your PC.")));
    }
    return;
  }
  ctx.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", {
      resolveWebviewView(view) {
        const s = new Surface(view.webview, "pop");
        view.onDidDispose(() => s.dispose());
        void s.show();
      },
    }, { webviewOptions: { retainContextWhenHidden: true } }),
    vscode.commands.registerCommand("construct.openPanel", () => openWindow("#/")),
    vscode.commands.registerCommand("construct.openHostAdmin", () => openWindow("#/hosts")),
    vscode.commands.registerCommand("construct.newRemoteVm", () => openWindow("#/create")),
    vscode.commands.registerCommand("construct.openVm", () => pickVmAndOpen()),
    vscode.commands.registerCommand("construct.registerWorkspace", () => registerWorkspace()),
  );
  for (const type of [PANEL_TYPE, OLD_PANEL_TYPE]) {
    ctx.subscriptions.push(vscode.window.registerWebviewPanelSerializer(type, {
      async deserializeWebviewPanel(p) {
        if (panel) { p.dispose(); return; }
        void setupPanel(p).show();
      },
    }));
  }
  if (vscode.env.remoteName === "ssh-remote") setTimeout(() => void maybeOffer(), 3000);
}

function deactivate() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

module.exports = { activate, deactivate };
