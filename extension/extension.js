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
const os = require("os");
const path = require("path");
const crypto = require("crypto");
const probe = require("./src/probe");
const ssh = require("./src/ssh");
const host = require("./src/host");
const lifecycle = require("./src/lifecycle");
const updates = require("./src/updates");
const usage = require("./src/usage");
const remote = require("./src/remote");
const vmpower = require("./src/vmpower");
const projects = require("./src/projects");
const audio = require("./src/audio");

/** The single editor-tab panel instance, if open. */
let panel; // vscode.WebviewPanel | undefined

/** The host-side mic-passthrough orchestrator (audio.HostAudio), live only while
 *  passthrough is enabled. */
let hostAudio; // audio.HostAudio | undefined

/** The hidden capture webview panel (getUserMedia lives here); created lazily on
 *  enable and disposed on disable/deactivate. */
let captureWebview; // vscode.WebviewPanel | undefined

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

/** Fold the VM's token usage + estimated cost into a probed state. Best-effort and
 *  CACHED (like augmentUpdates), but a SEPARATE, slower pass: collecting usage is an
 *  SSH + ccusage round-trip (ccusage may even install itself the first time), so this
 *  runs after the base + update pushes and folds usage in as its own state message.
 *  Returns the same object reference when nothing was added, so callers skip a re-push. */
async function augmentUsage(state) {
  try {
    return await usage.augment(state);
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

/**
 * Fold the LOCAL project profiles + persisted selection into the state's `projects`
 * chips. The chips the panel shows come from the host-side profile files
 * (<scriptsDir>/projects/*.json) — the same set editProject/importProjects/
 * selectProfiles operate on — rather than only the VM's live PROJECTS= list, so
 * profiles that exist locally but aren't provisioned yet are still visible/editable.
 *
 * Selection: the persisted `projects` array in settings marks the ticked chips. To
 * avoid a jarring "nothing selected" on first use (before the user has ever saved a
 * selection), we SEED the display selection from the VM's live PROJECTS= list (the
 * probe's `projects`, all selected:true) when nothing is persisted yet — a faithful
 * reflection of what the VM is actually running. This does NOT persist anything; it
 * only affects the chips until the user saves a selection.
 *
 * Best-effort: when no scripts dir resolves (no host install found), we leave the
 * probe's projects untouched. Synchronous. Returns the same object ref when nothing
 * was added, so callers can skip a re-push.
 */
function withProjects(state) {
  let scriptsDir;
  try { scriptsDir = resolveScriptsDir(); } catch (_) { return state; }
  if (!scriptsDir) return state;
  let available, selected;
  try {
    available = host.listProjectProfiles(scriptsDir);
    selected = host.readSelectedProjects(scriptsDir);
  } catch (_) { return state; }
  if (!available.length) return state; // nothing local to show; keep the probe's list
  // Seed from the live VM selection when the user hasn't saved one yet.
  if (!selected.length && state && Array.isArray(state.projects)) {
    selected = state.projects.filter((p) => p && p.selected).map((p) => p.name);
  }
  return { ...state, projects: projects.toChips(available, selected) };
}

/** Probe the VM and push fresh state to one webview, then push the update-augmented
 *  state once the (cached, best-effort) GitHub check resolves. */
async function refreshState(webview) {
  if (!webview) return;
  const state = withProjects(await withVmState(withLocalState(await probeOnce())));
  safePost(webview, { type: "state", state });
  const aug = await augmentUpdates(state);
  if (aug !== state) safePost(webview, { type: "state", state: aug });
  // Usage is a slower SSH+ccusage round-trip: fold it into the latest state (so the
  // update badges survive) and push once more if it actually added anything.
  const withUsage = await augmentUsage(aug);
  if (withUsage !== aug) safePost(webview, { type: "state", state: withUsage });
}

/** Probe once and broadcast the same state to every live webview, then broadcast
 *  the update-augmented state. */
async function refreshAll() {
  if (liveWebviews.size === 0) return;
  const state = withProjects(await withVmState(withLocalState(await probeOnce())));
  for (const w of liveWebviews) safePost(w, { type: "state", state });
  const aug = await augmentUpdates(state);
  if (aug !== state) for (const w of liveWebviews) safePost(w, { type: "state", state: aug });
  const withUsage = await augmentUsage(aug);
  if (withUsage !== aug) for (const w of liveWebviews) safePost(w, { type: "state", state: withUsage });
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

/** Open a project on the VM in a NEW remote window. Opens the profile's single
 *  repo folder when it has exactly one repo, else the workspace root — reading the
 *  host-side profile `<scriptsDir>/projects/<name>.json`. */
function runOpenProject(name) {
  if (!remote.hasRemoteSsh()) {
    vscode.window.showWarningMessage(
      "The Remote-SSH extension (ms-vscode-remote.remote-ssh) isn't installed, so the project can't be opened here. Install it, then try again."
    );
    return;
  }
  const scriptsDir = resolveScriptsDir();
  const profile = scriptsDir ? host.readProjectProfile(scriptsDir, name) : null;
  remote.openOnVm({ path: remote.projectOpenPath(profile), newWindow: true });
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

/** Collect the VM's combined ccusage JSON over SSH and save it to a file the user
 *  picks. Best-effort: a slow round-trip, so it runs inside a progress notification;
 *  on failure (offline/unreachable, no runtime, malformed output) it surfaces a toast
 *  and writes nothing. The saved payload wraps the RAW combined document (full
 *  per-session/model breakdown) plus a savedAt stamp and the parsed summary. */
function runExportUsage() {
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Collecting usage from the VM…", cancellable: false },
    async () => {
      const rawText = await usage.collectRaw({});
      if (!rawText) {
        vscode.window.showErrorMessage(
          "Couldn't collect usage from the VM. Make sure it's running and reachable, then try again."
        );
        return;
      }
      const uri = await vscode.window.showSaveDialog({
        title: "Save Construct usage report",
        filters: { JSON: ["json"], "All files": ["*"] },
        // Default into the home dir; a bare filename in showSaveDialog resolves against
        // the last-used location, so pin it under home for a predictable first save.
        defaultUri: vscode.Uri.file(path.join(os.homedir(), usage.exportFileName(usage.DEFAULT_REPORT))),
      });
      if (!uri) return; // cancelled — nothing written
      const payload = usage.buildExportPayload(rawText);
      try {
        await fs.promises.writeFile(uri.fsPath, payload, "utf8");
        vscode.window.showInformationMessage("Usage report saved to " + uri.fsPath);
      } catch (e) {
        vscode.window.showErrorMessage("Couldn't save the usage report: " + (e && e.message ? e.message : e));
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

/**
 * Import projects from the VM: scan the checked-out repos over SSH and write a
 * minimal profile for each one not already covered by a local profile. Merges,
 * never overwrites (an existing profile of the same name or covering the same repo
 * URL is kept). Re-pushes state so the new chips appear. All pure planning lives in
 * src/projects.js; here we do the SSH round-trip + the writes + the toasts.
 */
function runImportProjects() {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Scanning the VM for project repos…", cancellable: false },
    async () => {
      const r = await ssh.runRemoteScript(projects.buildScanScript(), { timeoutMs: 60000 });
      if (r.code < 0) { vscode.window.showErrorMessage("Couldn't reach the VM to scan for repos. Is it running?"); return; }
      if (r.code !== 0) {
        vscode.window.showErrorMessage(`Scanning the VM failed (exit ${r.code}). ${(r.stderr || "").slice(0, 200)}`.trim());
        return;
      }
      const scan = projects.parseScan(r.stdout);
      if (scan == null) { vscode.window.showErrorMessage("The repo scan returned incomplete output; nothing was imported."); return; }
      // Read every existing profile so planImport can skip a repo already covered.
      const existing = {};
      for (const name of host.listProjectProfiles(scriptsDir)) {
        const p = host.readProjectProfile(scriptsDir, name);
        if (p) existing[name] = p;
      }
      const plan = projects.planImport(scan, existing);
      let written = 0;
      const failed = [];
      for (const item of plan.toWrite) {
        try { host.writeProjectProfile(scriptsDir, item.name, item.profile); written++; }
        catch (_) { failed.push(item.name); }
      }
      if (written > 0) {
        const names = plan.toWrite.filter((i) => !failed.includes(i.name)).map((i) => i.name).join(", ");
        vscode.window.showInformationMessage(`Imported ${written} project profile(s): ${names}.`);
      } else if (plan.skipped.length && !plan.toWrite.length && !plan.covered.length) {
        vscode.window.showWarningMessage("Found repos on the VM but none had a remote to clone from — nothing imported.");
      } else {
        vscode.window.showInformationMessage("No new project profiles to import — every repo on the VM is already covered.");
      }
      if (failed.length) vscode.window.showWarningMessage(`Couldn't write profile(s): ${failed.join(", ")}.`);
      refreshAll(); // surface the new chips
    }
  );
}

// ── Mic passthrough (on-demand) ────────────────────────────────────────────────
// setAudio(true) → HostAudio.enable(): push the vm/ shim + guard patch over SSH,
// open a local TCP server + the `ssh -R` reverse tunnel, and stand up a hidden
// capture webview. The mic is armed ONLY while the VM shim is connected (Claude is
// recording) and released on disconnect — the mic is never hot continuously.
// setAudio(false) → HostAudio.disable(): stop capture + tunnel, then remove the shim
// + revert the patch on the VM. deactivate() disposes the local side unconditionally.

/** Broadcast live audio status to every webview (flips the console switch). */
function broadcastAudio(status) {
  const msg = { type: "audio", enabled: !!status.enabled, capturing: !!status.capturing };
  if (status.tunnel) msg.tunnel = status.tunnel;
  if (typeof status.gatePatched === "boolean") msg.gatePatched = status.gatePatched;
  for (const w of liveWebviews) safePost(w, msg);
}

/** Create (or reveal) the hidden capture webview that owns getUserMedia. It is a
 *  real WebviewPanel because a UI extension has no other surface that can open the
 *  local mic; kept in a background column and disposed with the session. Returns the
 *  panel, or undefined if creation failed. */
function ensureCaptureWebview(context) {
  if (captureWebview) return captureWebview;
  try {
    const { extensionUri } = context;
    const p = vscode.window.createWebviewPanel(
      "construct.audioCapture",
      "Construct — mic",
      { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true },
      { ...webviewOptions(extensionUri), retainContextWhenHidden: true }
    );
    const workletUri = p.webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", "audio-worklet.js")).toString();
    // Inject the worklet URI as a global the capture script reads (CSP allows our
    // nonce'd inline + the media root). buildHtml handles nonce/cspSource/scriptUri.
    let html = buildHtml(p.webview, extensionUri, "audio.html", "audio-capture.js");
    const nonce = /nonce="([^"]+)"/.exec(html);
    const inject = `<script nonce="${nonce ? nonce[1] : ""}">window.__workletUri=${JSON.stringify(workletUri)};</script>`;
    html = html.replace("</head>", inject + "</head>");
    p.webview.html = html;
    p.onDidDispose(() => { if (captureWebview === p) captureWebview = undefined; });
    captureWebview = p;
    return p;
  } catch (_) {
    return undefined;
  }
}

/** Dispose the hidden capture webview if present. */
function disposeCaptureWebview() {
  if (captureWebview) { try { captureWebview.dispose(); } catch (_) {} captureWebview = undefined; }
}

/** The mic-capture provider handed to HostAudio (AudioSession.onCapture): armed when
 *  the VM shim connects, released (disarmed) on disconnect. Bridges the hidden
 *  webview's PCM messages to the tunnel socket. Falls back to nothing here (the sox
 *  fallback is a runtime concern documented in limitations); if the webview can't be
 *  created or the mic is blocked, done() ends the socket so the shim reports no audio. */
function makeMicProvider(context) {
  return (writeChunk, done) => {
    const wv = ensureCaptureWebview(context);
    if (!wv) { done(); return () => {}; }
    // Route PCM from the webview to the tunnel for the lifetime of this capture.
    const sub = wv.webview.onDidReceiveMessage((m) => {
      if (!m || typeof m.type !== "string") return;
      if (m.type === "pcm" && m.data != null) {
        try { writeChunk(Buffer.from(m.data)); } catch (_) { /* socket gone */ }
      } else if (m.type === "error") {
        done(); // mic blocked / no device — end the capture (honest: no audio)
      }
    });
    safePost(wv.webview, { type: "arm" });
    // The stop function AudioSession calls on disconnect: disarm the mic + detach.
    return () => {
      try { sub.dispose(); } catch (_) {}
      safePost(wv.webview, { type: "disarm" });
    };
  };
}

/** Enable mic passthrough. Optimistic switch is already "busy" in the webview; we
 *  flip it authoritatively via {type:'audio'} once enable resolves. */
function enableAudio(context, webview) {
  if (hostAudio && hostAudio.enabled) { broadcastAudio({ enabled: true, capturing: hostAudio.capturing }); return; }
  hostAudio = new audio.HostAudio({
    cfg: undefined,
    mic: makeMicProvider(context),
    onStatus: (s) => broadcastAudio(s),
  });
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Enabling microphone passthrough…", cancellable: false },
    async () => {
      const r = await hostAudio.enable();
      if (!r.ok) {
        // Honest, specific failure; reset the switch to off.
        const why = {
          unreachable: "Couldn't reach the VM. Is it running?",
          "enable-failed": "The VM couldn't install the recorder shim / patch.",
          "server-failed": "Couldn't open the local audio port.",
          "tunnel-failed": "Couldn't open the SSH tunnel to the VM.",
        }[r.error] || "Couldn't enable microphone passthrough.";
        vscode.window.showErrorMessage(why + (r.detail ? " " + String(r.detail).slice(0, 160) : ""));
        disposeCaptureWebview();
        hostAudio = undefined;
        safePost(webview, { type: "audio", enabled: false, capturing: false });
      } else {
        vscode.window.showInformationMessage("Microphone passthrough enabled — the mic opens only while you're recording.");
      }
    }
  );
}

/**
 * Choose which project profiles are selected. Presents the available profiles in a
 * multi-select QuickPick (pre-ticked from the persisted selection), then persists
 * the chosen set as the forward-compat `projects` key in .construct-settings.json.
 * HONESTY: this does NOT re-provision a running VM — it records the selection so the
 * next Reprovision/Reinstall picks it up (and reflects it in the chips now). The
 * QuickPick copy says so.
 */
async function runSelectProfiles() {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const available = host.listProjectProfiles(scriptsDir);
  if (!available.length) {
    vscode.window.showInformationMessage("No project profiles found. Use “import from VM” or “+ add project” first.");
    return;
  }
  const selected = new Set(host.readSelectedProjects(scriptsDir));
  const items = available.map((name) => ({ label: name, picked: selected.has(name) }));
  const picks = await vscode.window.showQuickPick(items, {
    canPickMany: true,
    title: "Select project profiles",
    placeHolder: "Ticked profiles are recorded for the next Reprovision / Reinstall (the running VM isn't changed).",
  });
  if (picks == null) return; // cancelled — leave the stored selection untouched
  const chosen = projects.reconcileSelection(picks.map((p) => p.label), available);
  try {
    host.saveSelectedProjects(scriptsDir, chosen);
    vscode.window.showInformationMessage(
      chosen.length
        ? `Selected ${chosen.length} profile(s). Applied on the next Reprovision / Reinstall.`
        : "Cleared the project selection. Applied on the next Reprovision / Reinstall."
    );
    refreshAll(); // reflect the ticks
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't save the project selection: " + (e && e.message ? e.message : e));
  }
}

/**
 * Open a project profile for editing in the panel. Reads the host-side profile
 * (traversal-safe via host.readProjectProfile), seeds a blank profile if the file
 * doesn't exist yet, and posts it to the webview which opens the edit modal. The
 * webview posts the edited profile back as {type:'saveProject'}.
 */
function runEditProject(name, webview) {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const safe = host.safeProfileName(name);
  if (!safe) { vscode.window.showErrorMessage("Invalid project name."); return; }
  // A profile that exists on disk is read; otherwise seed an empty, schema-shaped
  // profile so the user can fill in a brand-new one (importProjects/addProject may
  // have added the chip but a hand-added chip could lack a file).
  const existing = host.readProjectProfile(scriptsDir, safe);
  const profile = projects.sanitizeProfile(safe, existing || {});
  safePost(webview, { type: "editProject", name: safe, profile });
}

/**
 * Persist an edited profile posted back from the modal. The object is sanitized to
 * the schema (src/projects.sanitizeProfile) — dropping unknown keys and coercing
 * types — before it is written, so arbitrary webview JSON can't produce an invalid
 * profile file. Traversal-safe (host.writeProjectProfile rejects a bad name).
 */
function runSaveProject(name, profileObj) {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const safe = host.safeProfileName(name);
  if (!safe) { vscode.window.showErrorMessage("Invalid project name."); return; }
  const clean = projects.sanitizeProfile(safe, profileObj);
  if (!clean) { vscode.window.showErrorMessage("Couldn't save the project profile (invalid name)."); return; }
  try {
    host.writeProjectProfile(scriptsDir, safe, clean);
    vscode.window.showInformationMessage(`Saved project “${safe}”.`);
    refreshAll(); // the profile set may now include a newly created profile
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't save the project profile: " + (e && e.message ? e.message : e));
  }
}

/** Disable mic passthrough: stop capture + tunnel, revert the VM shim + patch. */
function disableAudio() {
  disposeCaptureWebview();
  if (!hostAudio) { broadcastAudio({ enabled: false, capturing: false }); return; }
  const inst = hostAudio;
  hostAudio = undefined;
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Disabling microphone passthrough…", cancellable: false },
    async () => {
      const r = await inst.disable();
      if (!r.ok) {
        vscode.window.showWarningMessage(
          "Microphone passthrough is off locally, but the VM cleanup (removing the shim / reverting the patch) may not have completed. Re-enable and disable once the VM is reachable to fully clean up."
        );
      }
      broadcastAudio({ enabled: false, capturing: false });
    }
  );
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
      // Reflect live passthrough state so a reloaded webview shows the real switch.
      if (hostAudio && hostAudio.enabled) {
        safePost(webview, { type: "audio", enabled: true, capturing: hostAudio.capturing, gatePatched: hostAudio.gatePatched });
      }
      return;

    case "openPanel":
      vscode.commands.executeCommand("construct.openPanel");
      return;

    case "setAudio":
      if (message.enabled) enableAudio(context, webview);
      else disableAudio();
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

    case "saveProject":
      // The edited profile posted back from the panel modal (validated + written).
      runSaveProject(message.name, message.profile);
      return;

    case "command": {
      const id = message.id;
      if (id === "refresh") { refreshState(webview); return; }
      if (id === "openProjectFolder") { openProjectFolder(); return; }
      if (id === "addProject") { runAddProject(); return; }
      if (id === "openProject") { runOpenProject(message.project); return; }
      if (id === "importProjects") { runImportProjects(); return; }
      if (id === "selectProfiles") { runSelectProfiles(); return; }
      if (id === "editProject") { runEditProject(message.project, webview); return; }
      if (id === "exportUsage") { runExportUsage(); return; }
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
          scriptsDir, script: "Update-Construct.ps1", args: updates.constructRefreshArgs(markers),
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
  maybeAutoOpenPanel(context);
}

/** When a window comes up attached to the VM (the installer's end-of-install deep
 *  link, or a Connect), surface the control panel once so the operator console is
 *  right there. Guarded per-workspace via workspaceState so reloads (or the user
 *  closing it) don't reopen it. */
function maybeAutoOpenPanel(context) {
  const KEY = "construct.autoOpenedPanel";
  // Best-effort: the whole body is guarded so an auto-open failure (incl. a throw
  // from openPanel/createWebviewPanel) can never break extension activation. The
  // flag is set BEFORE openPanel, so even a throw won't reopen on the next reload.
  try {
    if (!remote.shouldAutoOpenPanel(vscode.env.remoteAuthority, context.workspaceState.get(KEY))) return;
    context.workspaceState.update(KEY, true);
    openPanel(context);
  } catch (_) { /* never break activation for an optional convenience */ }
}

function deactivate() {
  // Release the mic + kill the reverse tunnel on shutdown. Best-effort and
  // synchronous (deactivate can't reliably await): dispose() tears down the local
  // side (tunnel child + server + capture webview). The VM shim only streams while a
  // tunnel exists — which it no longer does — so leaving it until the next explicit
  // disable is harmless; the guard patch is likewise inert without the shim.
  try { if (hostAudio) hostAudio.dispose(); } catch (_) {}
  hostAudio = undefined;
  disposeCaptureWebview();
}

module.exports = { activate, deactivate };
