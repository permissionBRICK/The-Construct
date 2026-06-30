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

/** Probe the VM and push fresh state to one webview. */
async function refreshState(webview) {
  if (!webview) return;
  const state = await probeOnce();
  safePost(webview, { type: "state", state });
}

/** Probe once and broadcast the same state to every live webview. */
async function refreshAll() {
  if (liveWebviews.size === 0) return;
  const state = await probeOnce();
  for (const w of liveWebviews) safePost(w, { type: "state", state });
}

// A per-render CSP nonce: it is the sole gate between the trusted bundled script
// and any injected inline script, so it must come from a CSPRNG, not Math.random.
function getNonce() {
  return crypto.randomBytes(24).toString("base64");
}

/** Build the webview HTML for either surface from the shared template. */
function buildHtml(webview, extensionUri) {
  const mediaUri = (file) => webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file));
  const htmlPath = path.join(extensionUri.fsPath, "media", "panel.html");
  let html = fs.readFileSync(htmlPath, "utf8");
  const nonce = getNonce();
  return html
    .replace(/{{cspSource}}/g, webview.cspSource)
    .replace(/{{styleUri}}/g, mediaUri("panel.css").toString())
    .replace(/{{scriptUri}}/g, mediaUri("panel.js").toString())
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

    case "saveSettings":
      vscode.window.showInformationMessage(
        "Saving Construct settings will be available in an upcoming build of the control panel."
      );
      return;

    case "customRebuild":
      vscode.window.showInformationMessage(
        `Custom ${message.mode === "redownload" ? "redownload" : "reinstall"} (` +
          `${message.backup}) will be available in an upcoming build of the control panel.`
      );
      return;

    case "command":
      if (message.id === "refresh") { refreshState(webview); return; }
      vscode.window.showInformationMessage(
        `"${message.id}" will be available in an upcoming build of the control panel.`
      );
      return;

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
    webviewView.webview.html = buildHtml(webviewView.webview, extensionUri);
    // Tie the listener to the webview's own lifetime (not context.subscriptions):
    // the disposable is released when the view is destroyed, so a re-resolve can't
    // accumulate stale listeners.
    webviewView.webview.onDidReceiveMessage((m) => handleMessage(m, webviewView.webview, this.context));
    liveWebviews.add(webviewView.webview);
    this.context.subscriptions.push(webviewView.onDidDispose(() => liveWebviews.delete(webviewView.webview)));
  }
}

/** Open (or reveal) the wide editor-tab panel. */
function openPanel(context) {
  const { extensionUri } = context;
  if (panel) {
    panel.reveal(vscode.ViewColumn.Active);
    return;
  }
  panel = vscode.window.createWebviewPanel(
    "construct.controlPanel",
    "The Construct",
    vscode.ViewColumn.Active,
    { ...webviewOptions(extensionUri), retainContextWhenHidden: true }
  );
  panel.iconPath = vscode.Uri.joinPath(extensionUri, "media", "icon.svg");
  panel.webview.html = buildHtml(panel.webview, extensionUri);
  panel.webview.onDidReceiveMessage(
    (m) => handleMessage(m, panel.webview, context),
    undefined,
    context.subscriptions
  );
  liveWebviews.add(panel.webview);
  panel.onDidDispose(() => { liveWebviews.delete(panel.webview); panel = undefined; }, undefined, context.subscriptions);
}

function activate(context) {
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", new ConstructViewProvider(context), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.commands.registerCommand("construct.openPanel", () => openPanel(context)),
    vscode.commands.registerCommand("construct.refresh", () => refreshAll())
  );
}

function deactivate() {}

module.exports = { activate, deactivate };
