"use strict";
// The Construct — control-panel extension (UI / host side).
//
// Runs as a UI extension (extensionKind: "ui") so it lives on the user's local
// machine even when the window is attached to the agent VM over Remote-SSH. That
// lets it reach both sides: the local host (PowerShell lifecycle scripts, the
// microphone) and the VM (status/versions/usage over SSH).
//
// This file is the shell: it renders the webview (activity-bar view + a wide
// editor-tab panel that share one HTML document) and routes messages between the
// webview and the extension. The backend actions are wired in later batches; here
// they are explicit, labelled stubs so the panel is coherent and runnable.

const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const probe = require("./src/probe");
const ssh = require("./src/ssh");
const host = require("./src/host");
const lifecycle = require("./src/lifecycle");
const updates = require("./src/updates");
const remote = require("./src/remote");
const vmpower = require("./src/vmpower");

/** The single editor-tab panel instance, if open. */
let panel; // vscode.WebviewPanel | undefined

/** Every currently-live webview (sidebar view + editor panel) for broadcast refresh. */
const liveWebviews = new Set();

/** Post to a webview, surviving both a synchronous throw and an async rejection
 *  if it was disposed mid-flight (postMessage returns a Thenable<boolean>). */
function safePost(webview, msg) {
  try {
    const p = webview.postMessage(msg);
    if (p && typeof p.then === "function") p.then(undefined, () => {});
  } catch (_) { /* webview disposed */ }
}

// Coalesce overlapping probes: concurrent refresh triggers (e.g. both surfaces
// firing 'ready', or rapid refresh commands) share one in-flight ssh probe.
let inflightProbe = null;
function probeOnce() {
  if (!inflightProbe) {
    const p = probe.probe().then((s) => s, () => ({ online: false }));
    inflightProbe = p;
    const clear = () => { if (inflightProbe === p) inflightProbe = null; };
    p.then(clear, clear);
  }
  return inflightProbe;
}

/** Fold host-side update info (GitHub) into a probed state. Best-effort: returns
 *  the same object reference when nothing was added, so callers can skip a re-push. */
async function augmentUpdates(state) {
  try {
    const scriptsDir = resolveScriptsDir();
    const raw = scriptsDir ? host.readRawSettings(scriptsDir) : {};
    return await updates.augment(state, raw);
  } catch (_) { return state; }
}

/** Add window-local fields (whether THIS window is already on the VM) to a probed
 *  state. Synchronous, so it rides the first push. */
function withLocalState(state) {
  let connected = false;
  try { connected = remote.isConnectedToVm(vscode.env.remoteAuthority); } catch (_) { /* default false */ }
  return { ...state, connected };
}

/** Fold the VM's Hyper-V power state into a probed state. When the VM answers SSH
 *  it is by definition running, so we skip the (possibly elevation-gated) host
 *  Get-VM query and only run it when offline — that's the only case where we need
 *  to tell "stopped" (→ Start & connect) apart from "not installed". Best-effort:
 *  any failure leaves vmState 'unknown', which the UI treats as "no power button". */
async function withVmState(state) {
  try {
    if (state && state.online) return { ...state, vmState: "running" };
    const vmState = await vmpower.queryVmState();
    return { ...state, vmState };
  } catch (_) {
    return { ...state, vmState: "unknown" };
  }
}

/** Probe the VM and push fresh state to one webview, then push the update-augmented
 *  state once the (cached, best-effort) GitHub check resolves. */
async function refreshState(webview) {
  if (!webview) return;
  const state = await withVmState(withLocalState(await probeOnce()));
  safePost(webview, { type: "state", state });
  const aug = await augmentUpdates(state);
  if (aug !== state) safePost(webview, { type: "state", state: aug });
}

/** Probe once and broadcast the same state to every live webview, then broadcast
 *  the update-augmented state. */
async function refreshAll() {
  if (liveWebviews.size === 0) return;
  const state = await withVmState(withLocalState(await probeOnce()));
  for (const w of liveWebviews) safePost(w, { type: "state", state });
  const aug = await augmentUpdates(state);
  if (aug !== state) for (const w of liveWebviews) safePost(w, { type: "state", state: aug });
}

/** Locate the host-side scripts dir, honoring the `construct.scriptsDir` override. */
function resolveScriptsDir() {
  const override = vscode.workspace.getConfiguration("construct").get("scriptsDir");
  return host.resolveScriptsDir({ scriptsDir: override, env: process.env });
}

/** Shared warning when the Construct install folder can't be located. */
function warnNoScriptsDir() {
  vscode.window.showWarningMessage(
    "Couldn't find the Construct install folder. Set \"construct.scriptsDir\" to the folder " +
      "that holds Auto-Install.ps1, then try again."
  );
}

/** Read the persisted settings and push them to one webview (no-op when the
 *  install folder can't be found — the panel keeps its HTML defaults). */
function pushSettings(webview) {
  if (!webview) return;
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) return;
  let settings;
  try { settings = host.readSettings(scriptsDir); } catch (_) { return; }
  safePost(webview, { type: "settings", settings });
}

/** Force-update the coding agents on the VM over SSH, with a progress notification,
 *  then re-probe so the new versions + cleared badges show. */
function runUpdateAgents() {
  const script = updates.buildAgentUpdateScript();
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Updating coding agents on the VM…", cancellable: false },
    async () => {
      const r = await ssh.runRemoteScript(script, { timeoutMs: 300000 });
      if (r.code === 0) {
        vscode.window.showInformationMessage("Coding agents updated.");
      } else {
        vscode.window.showErrorMessage(
          `Updating agents failed (exit ${r.code}). ${(r.stderr || "").slice(0, 200)}`.trim()
        );
      }
      refreshAll(); // re-probe versions + clear the update badges
    }
  );
}

const delay = (ms) => new Promise((r) => setTimeout(r, ms));

/** Start the (stopped) VM via an elevated Hyper-V Start-VM, then poll SSH until it
 *  answers and open it in this window. Mirrors the "Start & connect" affordance the
 *  webview shows when the VM is installed but off. */
function runStartAndConnect() {
  if (process.platform !== "win32") {
    vscode.window.showWarningMessage("Starting the Construct VM runs on the Windows host, which isn't available here.");
    return;
  }
  if (!remote.hasRemoteSsh()) {
    vscode.window.showWarningMessage(
      "The Remote-SSH extension (ms-vscode-remote.remote-ssh) isn't installed, so the VM can't be opened here. Install it, then try again."
    );
    return;
  }
  if (!vmpower.startVm()) return; // startVm surfaces its own failure
  vscode.window.showInformationMessage("Starting the Construct VM — approve the UAC prompt.");
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Waiting for the Construct VM to come online…", cancellable: true },
    async (_progress, token) => {
      const intervalMs = 4000, maxMs = 150000;
      let waited = 0;
      while (waited < maxMs) {
        if (token.isCancellationRequested) return;
        if (await ssh.isReachable({ timeoutMs: 6000 })) {
          remote.openOnVm({ path: "/root/repos", newWindow: false });
          refreshAll();
          return;
        }
        await delay(intervalMs);
        waited += intervalMs;
      }
      vscode.window.showWarningMessage("The VM didn't come online in time. Once it's up, use “Open on VM”.");
      refreshAll();
    }
  );
}

/** Power the VM off over SSH (root → systemctl poweroff). Confirms first; warns
 *  that an attached remote window will lose its connection. */
async function runShutdown() {
  const connectedHere = (() => {
    try { return remote.isConnectedToVm(vscode.env.remoteAuthority); } catch (_) { return false; }
  })();
  const detail = connectedHere
    ? "This window is connected to the VM over Remote-SSH, so its connection will drop when the VM powers off."
    : "The VM will power off. You can bring it back with “Start & connect”.";
  const pick = await vscode.window.showWarningMessage(
    "Shut down the Construct VM?", { modal: true, detail }, "Shut down"
  );
  if (pick !== "Shut down") return;
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Shutting down the Construct VM…", cancellable: false },
    async () => {
      const r = await ssh.runRemote(vmpower.SHUTDOWN_CMD, { timeoutMs: 20000 });
      if (r.code === 0) {
        vscode.window.showInformationMessage("The Construct VM is shutting down.");
      } else {
        // poweroff can tear the SSH session down before it reports success, so a
        // non-zero/teardown exit doesn't necessarily mean the command was rejected.
        vscode.window.showWarningMessage(
          `Sent the shutdown command (ssh exited ${r.code}). ${(r.stderr || "").slice(0, 160)}`.trim()
        );
      }
      // Give the VM a moment to drop off the network, then re-probe so the UI flips
      // to offline and offers "Start & connect".
      await delay(8000);
      refreshAll();
    }
  );
}

/** Clone a git URL into /root/repos on the VM over SSH, then open it in a NEW
 *  remote window. The URL is validated loosely and passed to `git clone` as data
 *  (never interpolated into the shell — see remote.buildCloneScript). */
async function runAddProject() {
  if (!remote.hasRemoteSsh()) {
    vscode.window.showWarningMessage(
      "The Remote-SSH extension (ms-vscode-remote.remote-ssh) isn't installed, so the cloned project can't be opened here. Install it, then try again."
    );
    return;
  }
  const raw = await vscode.window.showInputBox({
    title: "Add project — clone a git repo onto the Construct VM",
    prompt: "Git URL to clone into /root/repos on the VM",
    placeHolder: "https://github.com/owner/repo.git",
    ignoreFocusOut: true,
    validateInput: (v) =>
      remote.isLikelyGitUrl(v) ? null : "Enter an https://, ssh:// or git@host:path git URL.",
  });
  if (raw == null) return; // cancelled
  // Normalize once at the boundary: validation, name derivation, the clone, and the
  // opened folder must all use the same value. The input box trims for display but
  // hands back the raw text, and isLikelyGitUrl/repoNameFromUrl trim internally — so
  // without this a pasted "  https://…  " would clone the spaced (and thus failing) URL.
  const url = raw.trim();
  if (!url) return;
  const name = remote.repoNameFromUrl(url);
  if (!name || name === "." || name === "..") {
    vscode.window.showErrorMessage("Couldn't derive a folder name from that URL.");
    return;
  }
  const dest = `${remote.WORKSPACE_ROOT}/${name}`;
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `Cloning ${name} onto the VM…`, cancellable: false },
    async () => {
      const r = await ssh.runRemoteScript(remote.buildCloneScript(url, name), { timeoutMs: 300000 });
      if (r.code === 0) {
        vscode.window.showInformationMessage(`Cloned ${name} — opening it on the VM…`);
        remote.openOnVm({ path: dest, newWindow: true });
        refreshAll(); // the repo now exists on the VM
      } else if (r.code === 3) {
        const pick = await vscode.window.showWarningMessage(
          `${dest} already exists on the VM.`, "Open it", "Cancel"
        );
        if (pick === "Open it") remote.openOnVm({ path: dest, newWindow: true });
      } else if (r.code < 0) {
        vscode.window.showErrorMessage("Couldn't reach the VM to clone. Is it running?");
      } else {
        vscode.window.showErrorMessage(
          `Cloning ${name} failed (exit ${r.code}). ${(r.stderr || "").slice(0, 200)}`.trim()
        );
      }
    }
  );
}

/** Reveal the project-profiles config folder in the OS file manager, creating it
 *  if needed (the installer's selector creates it the same way on first use). */
function openProjectFolder() {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const dir = host.projectsDir(scriptsDir);
  try { fs.mkdirSync(dir, { recursive: true }); } catch (_) { /* reveal will surface a real failure */ }
  vscode.commands.executeCommand("revealFileInOS", vscode.Uri.file(dir));
}

// A per-render CSP nonce: it is the sole gate between the trusted bundled script
// and any injected inline script, so it must come from a CSPRNG, not Math.random.
function getNonce() {
  return crypto.randomBytes(24).toString("base64");
}

/** Build the webview HTML for either surface from the shared template. */
function buildHtml(webview, extensionUri, htmlFile, scriptFile) {
  const mediaUri = (file) => webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file));
  const html = fs.readFileSync(path.join(extensionUri.fsPath, "media", htmlFile), "utf8");
  const nonce = getNonce();
  return html
    .replace(/{{cspSource}}/g, webview.cspSource)
    .replace(/{{styleUri}}/g, mediaUri("panel.css").toString())
    .replace(/{{scriptUri}}/g, mediaUri(scriptFile).toString())
    .replace(/{{nonce}}/g, nonce);
}

const webviewOptions = (extensionUri) => ({
  enableScripts: true,
  localResourceRoots: [vscode.Uri.joinPath(extensionUri, "media")],
});

/**
 * Handle a message from a webview. Returns nothing; replies are posted back on
 * the same webview. `webview` is the surface that sent the message so replies
 * land in the right place.
 */
function handleMessage(message, webview, context) {
  if (!message || typeof message.type !== "string") return;

  switch (message.type) {
    case "ready":
      refreshState(webview);
      pushSettings(webview);
      return;

    case "openPanel":
      vscode.commands.executeCommand("construct.openPanel");
      return;

    case "setAudio":
      vscode.window.showInformationMessage(
        "Microphone passthrough will be available in an upcoming build of the control panel."
      );
      // Reset the optimistic/busy switch back to off until the feature lands.
      safePost(webview, { type: "audio", enabled: false });
      return;

    case "saveSettings": {
      const scriptsDir = resolveScriptsDir();
      if (!scriptsDir) { warnNoScriptsDir(); return; }
      try {
        host.saveSettings(scriptsDir, message.settings);
        vscode.window.showInformationMessage("Construct settings saved.");
        pushSettings(webview); // reflect the normalized, merged on-disk state
      } catch (e) {
        vscode.window.showErrorMessage("Couldn't save Construct settings: " + (e && e.message ? e.message : e));
      }
      return;
    }

    case "customRebuild": {
      const scriptsDir = resolveScriptsDir();
      if (!scriptsDir) { warnNoScriptsDir(); return; }
      const action = message.mode === "redownload" ? "redownload" : "reinstall";
      lifecycle.run(action, { scriptsDir, backupMode: message.backup });
      return;
    }

    case "command": {
      const id = message.id;
      if (id === "refresh") { refreshState(webview); return; }
      if (id === "openProjectFolder") { openProjectFolder(); return; }
      if (id === "addProject") { runAddProject(); return; }
      if (id === "updateAgents") { runUpdateAgents(); return; }
      if (id === "connect") { remote.openOnVm({ path: "/root/repos", newWindow: false }); return; }
      if (id === "startConnect") { runStartAndConnect(); return; }
      if (id === "shutdown") { runShutdown(); return; }
      if (id === "reprovision" || id === "exportConfig" || id === "reinstall" || id === "redownload") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        lifecycle.run(id, { scriptsDir });
        return;
      }
      if (id === "updateConstruct") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        const markers = updates.readMarkers(host.readRawSettings(scriptsDir));
        lifecycle.launchHostScript({
          scriptsDir, script: "install.ps1", args: updates.constructRefreshArgs(markers),
          elevate: false, label: "Update Construct",
        });
        return;
      }
      vscode.window.showInformationMessage(
        `"${id}" will be available in an upcoming build of the control panel.`
      );
      return;
    }

    default:
      return;
  }
}

/** Activity-bar sidebar view. Renders the same panel HTML (responsive to width). */
class ConstructViewProvider {
  constructor(context) {
    this.context = context;
  }
  resolveWebviewView(webviewView) {
    const { extensionUri } = this.context;
    webviewView.webview.options = webviewOptions(extensionUri);
    // The sidebar is a compact launcher: status + quick lifecycle actions + a
    // button to pop the full panel (settings / usage / projects) as an editor tab.
    webviewView.webview.html = buildHtml(webviewView.webview, extensionUri, "launcher.html", "launcher.js");
    // The listener is tied to the webview's own lifetime (not context.subscriptions);
    // its disposable is released when the view is destroyed.
    webviewView.webview.onDidReceiveMessage((m) => handleMessage(m, webviewView.webview, this.context));
    liveWebviews.add(webviewView.webview);
    this.context.subscriptions.push(webviewView.onDidDispose(() => liveWebviews.delete(webviewView.webview)));
  }
}

/** Configure a new or restored control-panel editor-tab webview. */
function setupPanel(p, context) {
  const { extensionUri } = context;
  panel = p;
  p.webview.options = webviewOptions(extensionUri);
  p.iconPath = vscode.Uri.joinPath(extensionUri, "media", "icon.svg");
  p.webview.html = buildHtml(p.webview, extensionUri, "panel.html", "panel.js");
  // Tie listeners to this panel instance's own lifetime: the disposables are
  // released when the webview is destroyed (so reopen/restore can't accumulate
  // stale listeners), and the dispose handler operates on the captured `p` rather
  // than the module-level `panel`, which may have been reassigned.
  p.webview.onDidReceiveMessage((m) => handleMessage(m, p.webview, context));
  liveWebviews.add(p.webview);
  p.onDidDispose(() => { liveWebviews.delete(p.webview); if (panel === p) panel = undefined; });
}

/** Open (or reveal) the full control panel as a wide editor tab. */
function openPanel(context) {
  if (panel) { panel.reveal(vscode.ViewColumn.Active); return; }
  const p = vscode.window.createWebviewPanel(
    "construct.controlPanel",
    "The Construct",
    vscode.ViewColumn.Active,
    { ...webviewOptions(context.extensionUri), retainContextWhenHidden: true }
  );
  setupPanel(p, context);
}

function activate(context) {
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", new ConstructViewProvider(context), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.commands.registerCommand("construct.openPanel", () => openPanel(context)),
    vscode.commands.registerCommand("construct.refresh", () => refreshAll())
  );
  // Restore the editor-tab panel across reloads instead of leaving a dead webview.
  if (vscode.window.registerWebviewPanelSerializer) {
    context.subscriptions.push(
      vscode.window.registerWebviewPanelSerializer("construct.controlPanel", {
        deserializeWebviewPanel(p) { setupPanel(p, context); return Promise.resolve(); },
      })
    );
  }
}

function deactivate() {}

module.exports = { activate, deactivate };
