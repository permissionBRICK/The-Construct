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

/** Recorder-failure reasons already surfaced this enable, so we warn once (not on
 *  every record-start). Reset in enableAudio. */
let micWarnedReasons = new Set();

/** Every currently-live webview (sidebar view + editor panel) for broadcast refresh. */
const liveWebviews = new Set();

// The currently selected token-usage period ("daily"|"monthly"), shared across every
// dashboard. Drives both the ccusage window we collect and the active tab the panel
// highlights; the webview flips it via a {type:'setUsagePeriod'} message.
let usageReport = usage.DEFAULT_REPORT;

// ── Diagnostics log ─────────────────────────────────────────────────────────────
// A "Construct" Output channel + a log file, so what the panel does (esp. the EXACT
// host command it launches, resolved paths, args, env, spawn result) is visible and
// shareable even when a launched console flashes closed. Pairs with the `construct.debug`
// setting, which keeps launched consoles open (-NoExit) so their own errors stay readable.
let logChannel;
function logFilePath() { return path.join(os.tmpdir(), "construct-panel.log"); }
function logLine(msg) {
  const line = `[${new Date().toISOString()}] ${msg}`;
  try { if (!logChannel) logChannel = vscode.window.createOutputChannel("Construct"); logChannel.appendLine(line); } catch (_) {}
  try { fs.appendFileSync(logFilePath(), line + "\n"); } catch (_) {}
}
/** Reveal the Construct Output channel (and note the on-disk log path). */
function showLogs() {
  try { if (!logChannel) logChannel = vscode.window.createOutputChannel("Construct"); logChannel.show(true); } catch (_) {}
  logLine(`(diagnostics log file: ${logFilePath()})`);
}
/** Whether verbose/keep-console-open debugging is enabled. */
function debugEnabled() {
  try { return !!vscode.workspace.getConfiguration("construct").get("debug"); } catch (_) { return false; }
}

/** Read vscode.env.remoteAuthority DEFENSIVELY. On some VS Code builds it's gated behind
 *  the `resolvers` proposed API and its getter THROWS for a normally-installed extension —
 *  a raw access in activate() would crash the whole extension. Everywhere we read it, degrade
 *  to undefined (treated as "local / not connected") instead of letting activation die. */
function safeRemoteAuthority() {
  try { return vscode.env.remoteAuthority; } catch (_) { return undefined; }
}

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
async function augmentUsage(state, report) {
  try {
    return await usage.augment(state, { report });
  } catch (_) { return state; }
}

/** Add window-local fields (whether THIS window is already on the VM) to a probed
 *  state. Synchronous, so it rides the first push. */
function withLocalState(state) {
  let connected = false;
  try { connected = remote.isConnectedToVm(safeRemoteAuthority()); } catch (_) { /* default false */ }
  return { ...state, connected };
}

/** Post a state to a webview, stamping the CURRENT usage period at SEND time. usageReport
 *  is the single source of truth for the active daily/monthly tab, so every render (even
 *  a slow/stale refresh landing late) reflects the live selection and can never re-select
 *  an out-of-date tab. The panel highlights the tab from this field on the sync push. */
function postState(target, state) {
  safePost(target, { type: "state", state: { ...state, usagePeriod: usageReport } });
}

/** Fold the VM's Hyper-V power state into a probed state. When the VM answers SSH
 *  it is by definition running, so we skip the (possibly elevation-gated) host
 *  Get-VM query and only run it when offline — that's the only case where we need
 *  to tell "not installed" ('absent') apart from everything else. Best-effort: any
 *  failure (including the common Hyper-V-permission denial, since the installer's
 *  Hyper-V Administrators membership is only effective at next sign-in) leaves
 *  vmState 'unknown'. The UI still offers "Start & connect" for 'unknown' (the
 *  elevated Start-VM self-elevates via UAC), hiding it only for 'absent'/'running' —
 *  see vmpower.shouldShowStart. */
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

/** The projects a lifecycle action should pass as -Projects so the console doesn't
 *  re-prompt (and doesn't silently drop to "default", which would DROP the VM's
 *  projects). Prefer the user's saved selection; otherwise reuse the VM's CURRENT
 *  projects (a quick probe) so a reprovision keeps what's installed — matching the
 *  panel, whose chips default to all current projects. Empty only when we genuinely
 *  can't tell (offline + nothing saved), where the script keeps its own prompt. */
async function effectiveProjects() {
  try {
    const scriptsDir = resolveScriptsDir();
    if (scriptsDir) {
      const saved = host.readSelectedProjects(scriptsDir);
      if (saved && saved.length) return saved;
    }
  } catch (_) { /* fall through to the live set */ }
  try {
    const st = await probeOnce();
    if (st && Array.isArray(st.projects)) {
      return st.projects.filter((p) => p && p.selected !== false).map((p) => p.name).filter(Boolean);
    }
  } catch (_) { /* offline / probe failed → let the script prompt */ }
  return [];
}

/** Probe the VM and push fresh state to one webview, then push the update-augmented
 *  state once the (cached, best-effort) GitHub check resolves. */
async function refreshState(webview) {
  if (!webview) return;
  const state = withProjects(await withVmState(withLocalState(await probeOnce())));
  postState(webview, state);
  const aug = await augmentUpdates(state);
  if (aug !== state) postState(webview, aug);
  // Usage is a slower SSH+ccusage round-trip: BIND it to the report we start with and
  // DISCARD the result if the user switched the period meanwhile (a stale daily run must
  // never land as monthly's numbers). postState always stamps the CURRENT usagePeriod.
  const report = usageReport;
  const withUsage = await augmentUsage(aug, report);
  if (withUsage !== aug && usageReport === report) postState(webview, withUsage);
}

/** Probe once and broadcast the same state to every live webview, then broadcast
 *  the update-augmented state. */
async function refreshAll() {
  if (liveWebviews.size === 0) return;
  const state = withProjects(await withVmState(withLocalState(await probeOnce())));
  for (const w of liveWebviews) postState(w, state);
  const aug = await augmentUpdates(state);
  if (aug !== state) for (const w of liveWebviews) postState(w, aug);
  // Bind + discard on period switch (see refreshState).
  const report = usageReport;
  const withUsage = await augmentUsage(aug, report);
  if (withUsage !== aug && usageReport === report) for (const w of liveWebviews) postState(w, withUsage);
}

// ── Periodic auto-refresh ────────────────────────────────────────────────────
// Re-probe the VM and push fresh state to the open dashboards on an interval, so
// versions / power state / provisioning markers stay current after a reprovision (or
// any VM-side change) without a manual refresh or a full window reload. This is the
// lightweight alternative to reloading VS Code — that heavier reload is reserved for a
// Construct self-update (which swaps the extension itself). The timer runs ONLY while a
// dashboard is open: started when the first webview goes live, stopped when the last one
// closes, so we don't SSH-probe the VM when nothing is showing.
const AUTO_REFRESH_MS = 30000;              // normal cadence while a dashboard is open
const FAST_REFRESH_MS = 5000;               // faster cadence while a reprovision is in flight
const FAST_REFRESH_MAX_MS = 5 * 60 * 1000;  // safety cap: never fast-poll longer than this
let autoRefreshTimer = null;
let autoRefreshMs = 0;                       // interval the live timer is currently running at
// Reprovision fast-poll: the provisioned-commit hash captured when a reprovision starts.
// We poll at 5s until it changes (the finished reprovision recorded a new one) or the cap
// elapses, then fall back to 30s. null = not fast-polling. A plain reprovision that lands
// the same commit relies on the cap; the common case (reprovision after a Construct update)
// changes the commit and reverts promptly.
let reprovisionBaselineCommit = null;
let fastRefreshDeadline = 0;

/** The provisioned-commit hash the provisioner writes to the host settings at the end of a
 *  run ("" if unknown). Cheap local file read — the same marker isProvisionStale/augment use. */
function provisionedCommitNow() {
  try {
    const dir = resolveScriptsDir();
    return dir ? (updates.readMarkers(host.readRawSettings(dir)).provisionedCommit || "") : "";
  } catch (_) { return ""; }
}

/** True while we're in the post-reprovision fast-poll window. */
function fastRefreshActive() { return reprovisionBaselineCommit !== null; }

/** Enter the 5s fast-poll after a reprovision starts. Ends (see refreshTick) when the
 *  provisioned commit changes or FAST_REFRESH_MAX_MS elapses. */
function beginReprovisionFastRefresh() {
  reprovisionBaselineCommit = provisionedCommitNow();
  fastRefreshDeadline = Date.now() + FAST_REFRESH_MAX_MS;
  syncAutoRefresh(); // switch the live timer to the fast cadence
}

/** Leave fast-poll and return to the normal cadence. */
function endReprovisionFastRefresh() {
  reprovisionBaselineCommit = null;
  fastRefreshDeadline = 0;
  syncAutoRefresh();
}

/** One refresh tick. While fast-polling, first check whether the reprovision recorded a
 *  new provisioned commit (or the cap elapsed) and, if so, drop back to the normal
 *  cadence — then push fresh state to the open dashboards either way. */
function refreshTick() {
  if (fastRefreshActive()) {
    const now = provisionedCommitNow();
    if ((now && now !== reprovisionBaselineCommit) || Date.now() >= fastRefreshDeadline) {
      endReprovisionFastRefresh();
    }
  }
  refreshAll();
}

/** Keep the auto-refresh timer in sync with whether a dashboard is open and which cadence
 *  applies (5s while a reprovision is in flight, else 30s). Started when the first webview
 *  goes live, stopped when the last closes, recreated when the cadence changes. */
function syncAutoRefresh() {
  if (liveWebviews.size === 0) { stopAutoRefresh(); return; }
  const wantMs = fastRefreshActive() ? FAST_REFRESH_MS : AUTO_REFRESH_MS;
  if (!autoRefreshTimer || autoRefreshMs !== wantMs) {
    if (autoRefreshTimer) clearInterval(autoRefreshTimer);
    autoRefreshMs = wantMs;
    autoRefreshTimer = setInterval(refreshTick, wantMs);
  }
}
/** Stop the auto-refresh timer unconditionally (extension deactivate). */
function stopAutoRefresh() {
  if (autoRefreshTimer) { clearInterval(autoRefreshTimer); autoRefreshTimer = null; autoRefreshMs = 0; }
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

/** Push the on-disk settings to EVERY live surface (so both mic switches — the
 *  console #voiceSwitch and the settings #setMic — reflect the same persisted value). */
function broadcastSettings() {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) return;
  let settings;
  try { settings = host.readSettings(scriptsDir); } catch (_) { return; }
  for (const w of liveWebviews) safePost(w, { type: "settings", settings });
}

/** Persist the mic-passthrough preference (micPassthrough in .construct-settings.json).
 *  The live console toggle IS this persistent setting — enabling on the main page makes
 *  it auto-arm next session (see maybeAutoEnableAudio). Merges (touches only that key).
 *  Best-effort: a missing scripts dir just means no persistence (the live toggle still
 *  works this session). Re-broadcasts settings so the settings-form switch stays in sync. */
function persistMicPreference(enabled) {
  try {
    const scriptsDir = resolveScriptsDir();
    if (!scriptsDir) return;
    host.saveSettings(scriptsDir, { mic: !!enabled });
    broadcastSettings();
  } catch (_) { /* best-effort */ }
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

/** Launch the host "Update Construct" self-update, then AUTO-RELOAD this window when it
 *  finishes so the refreshed panel loads (no manual reopen). The detached host console
 *  can't reload VS Code itself, so Update-Construct.ps1 writes a tiny result file we poll:
 *  "ok" -> reload; "fail" -> the script's console already paused with a reopen message, so
 *  we just surface a toast. Times out quietly (the console stays up either way). The result
 *  path is passed via an ENV VAR (not a -ResultFile arg): an OLDER installed script simply
 *  ignores it and runs normally (pausing on completion), instead of erroring on an unknown
 *  argument — which would trap the user, since it'd fail before downloading the fix. */
function runUpdateConstruct() {
  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const markers = updates.readMarkers(host.readRawSettings(scriptsDir));
  const resultFile = path.join(os.tmpdir(), `construct-update-${Date.now()}.result`);
  try { fs.unlinkSync(resultFile); } catch (_) {}
  const ok = lifecycle.launchHostScript({
    scriptsDir, script: "Update-Construct.ps1",
    args: updates.constructRefreshArgs(markers),
    env: { CONSTRUCT_UPDATE_RESULT: resultFile },
    elevate: false, label: "Update Construct",
  });
  if (!ok) return;
  vscode.window.showInformationMessage("Updating Construct — this window reloads automatically when it's done.");
  const startedAt = Date.now();
  const timer = setInterval(() => {
    let res = null;
    try { res = fs.readFileSync(resultFile, "utf8").trim(); } catch (_) { /* not written yet */ }
    if (res === "ok") {
      clearInterval(timer);
      try { fs.unlinkSync(resultFile); } catch (_) {}
      logLine("update: result=ok → reloading window");
      vscode.commands.executeCommand("workbench.action.reloadWindow");
    } else if (res === "fail") {
      clearInterval(timer);
      try { fs.unlinkSync(resultFile); } catch (_) {}
      logLine("update: result=fail (see the update console)");
      vscode.window.showWarningMessage("Construct update didn't complete — see the update console, then reopen VS Code.");
    } else if (Date.now() - startedAt > 10 * 60 * 1000) {
      clearInterval(timer); // gave up waiting; the console is still there to show status
      try { fs.unlinkSync(resultFile); } catch (_) {}
      logLine("update: timed out waiting for a result (an older script doesn't signal — the update likely still applied; reload manually)");
    }
  }, 1500);
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
  if (!vmpower.startVm({ debug: debugEnabled() })) return; // startVm surfaces its own failure
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
    try { return remote.isConnectedToVm(safeRemoteAuthority()); } catch (_) { return false; }
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
      const rawText = await usage.collectRaw({ report: usageReport });
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
        defaultUri: vscode.Uri.file(path.join(os.homedir(), usage.exportFileName(usageReport))),
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

/** The mic-capture provider handed to HostAudio (AudioSession.onCapture): armed when
 *  the VM shim connects, released on disconnect. It spawns a native HOST recorder
 *  (ffmpeg, falling back to sox `rec`) that emits the recorder contract (raw S16LE /
 *  16 kHz / mono) on stdout and pipes it to the tunnel socket.
 *
 *  WHY NOT A WEBVIEW — a UI extension's only in-window surface is a webview, but VS
 *  Code embeds webviews in an iframe whose Permissions-Policy `allow` attribute is
 *  fixed and omits `microphone`, so getUserMedia is always rejected (NotAllowedError)
 *  and no audio ever flows — that was the "completely silent signal" bug. The
 *  extension host runs locally, so a native recorder is the capture path that
 *  actually works and stays on-demand (spawned per shim connection, killed on
 *  disconnect). If no recorder is installed, done() ends the socket so the shim
 *  reports "no audio" honestly rather than feeding silence. */
function makeMicProvider() {
  // Optional override for hosts where the auto-detected Windows capture device is
  // wrong/ambiguous: construct.micDevice = the exact dshow device name (see
  // `ffmpeg -list_devices true -f dshow -i dummy`). Empty ⇒ auto-detect.
  let device = "";
  try { device = (vscode.workspace.getConfiguration("construct").get("micDevice") || "").trim(); } catch (_) {}
  return audio.makeHostMicProvider({
    device,
    onError: (reason) => {
      // Dedupe: onError can fire on every record-start while the mic is broken; warn once
      // per enable (micWarnedReasons is reset in enableAudio).
      if (micWarnedReasons.has(reason)) return;
      micWarnedReasons.add(reason);
      if (reason === "no-recorder") {
        vscode.window.showWarningMessage(
          "Microphone passthrough is on, but no host recorder (ffmpeg or sox) was found. Install ffmpeg (winget install Gyan.FFmpeg) so the mic can be captured."
        );
      } else if (reason === "no-device") {
        vscode.window.showWarningMessage(
          "Microphone passthrough is on, but no Windows capture device was found. Plug in a microphone, or set construct.micDevice to a device from `ffmpeg -list_devices true -f dshow -i dummy`."
        );
      }
    },
  });
}

/** Enable mic passthrough. Optimistic switch is already "busy" in the webview; we
 *  flip it authoritatively via {type:'audio'} once enable resolves. `opts.auto` marks
 *  a startup auto-arm (from the saved micPassthrough preference): it runs FULLY SILENT —
 *  no notification progress, no success toast, no failure toast (the switch visibly
 *  reflects the result; a down VM or a second window that already holds the tunnel
 *  shouldn't nag on every launch). A manual toggle keeps the progress spinner + toasts. */
function enableAudio(context, webview, opts = {}) {
  if (hostAudio && hostAudio.enabled) { broadcastAudio({ enabled: true, capturing: hostAudio.capturing }); return; }
  micWarnedReasons = new Set(); // fresh enable: allow one warning per failure reason again
  hostAudio = new audio.HostAudio({
    cfg: undefined,
    mic: makeMicProvider(),
    onStatus: (s) => broadcastAudio(s),
  });
  const handle = (r) => {
    if (!r.ok) {
      // Reset the switch to off on every surface.
      hostAudio = undefined;
      safePost(webview, { type: "audio", enabled: false, capturing: false });
      broadcastAudio({ enabled: false, capturing: false });
      if (opts.auto) return; // best-effort startup arm: stay silent, the switch shows off
      const why = {
        unreachable: "Couldn't reach the VM. Is it running?",
        "enable-failed": "The VM couldn't install the recorder shim / patch.",
        "server-failed": "Couldn't open the local audio port.",
        "tunnel-failed": "Couldn't open the SSH tunnel to the VM.",
      }[r.error] || "Couldn't enable microphone passthrough.";
      vscode.window.showErrorMessage(why + (r.detail ? " " + String(r.detail).slice(0, 160) : ""));
    } else if (!opts.auto) {
      // The guard patch is now on the VM, but the already-running Claude Code extension
      // still has the pre-patch code in memory — its MICROPHONE ICON won't appear until
      // the window reloads / VS Code restarts. Notify the user (with a one-click Reload).
      // Skip the hint only when we KNOW the gate wasn't patched (gatePatched === false):
      // then the icon won't appear regardless (unrecognised Claude build — the panel's
      // audio substatus already says so). passthrough is the persisted preference, so
      // auto-arm re-establishes it after the reload.
      if (hostAudio && hostAudio.gatePatched === false) {
        vscode.window.showInformationMessage("Microphone passthrough enabled — the mic opens only while you're recording.");
      } else {
        const RELOAD = "Reload window";
        vscode.window.showInformationMessage(
          "Microphone passthrough enabled. If the microphone icon doesn't appear in Claude Code, reload (or restart) VS Code so its extension picks up the change — passthrough re-arms automatically.",
          RELOAD
        ).then((pick) => { if (pick === RELOAD) vscode.commands.executeCommand("workbench.action.reloadWindow"); });
      }
    }
  };
  if (opts.auto) {
    // No notification progress on startup — auto-arm must be invisible until it succeeds.
    hostAudio.enable().then(handle, () => handle({ ok: false, error: "enable-failed" }));
    return;
  }
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Enabling microphone passthrough…", cancellable: false },
    async () => { handle(await hostAudio.enable()); }
  );
}

/** Auto-arm mic passthrough on startup when the saved preference (micPassthrough in
 *  .construct-settings.json, the settings-form "Microphone passthrough" toggle) is on.
 *  Best-effort and QUIET: gated on the VM being reachable so a down VM never toasts;
 *  the user can still toggle manually. */
async function maybeAutoEnableAudio(context) {
  try {
    if (hostAudio && hostAudio.enabled) return;
    const scriptsDir = resolveScriptsDir();
    if (!scriptsDir) return;
    const raw = host.readRawSettings(scriptsDir);
    if (!raw || raw.micPassthrough !== true) return;
    if (!(await ssh.isReachable({ timeoutMs: 6000 }))) return; // VM down — stay off silently
    enableAudio(context, undefined, { auto: true });
  } catch (_) { /* best-effort: never block activation */ }
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
      // The console toggle IS the persistent preference: persist it so passthrough
      // auto-arms next session (unifies the two mic switches into one setting).
      persistMicPreference(message.enabled);
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
        // The "Microphone passthrough" toggle is a live preference: honor it now, not
        // just on next startup, so changing the setting actually does something.
        const wantMic = message.settings && message.settings.mic === true;
        const micOn = !!(hostAudio && hostAudio.enabled);
        if (wantMic && !micOn) enableAudio(context, webview);
        else if (!wantMic && micOn) disableAudio();
      } catch (e) {
        vscode.window.showErrorMessage("Couldn't save Construct settings: " + (e && e.message ? e.message : e));
      }
      return;
    }

    case "customRebuild": {
      const scriptsDir = resolveScriptsDir();
      if (!scriptsDir) { warnNoScriptsDir(); return; }
      const action = message.mode === "redownload" ? "redownload" : "reinstall";
      effectiveProjects().then((projects) => lifecycle.run(action, { scriptsDir, backupMode: message.backup, projects }));
      return;
    }

    case "setUsagePeriod": {
      // Switch the token-usage view between daily (today) and monthly (this month).
      // Validate against the allow-list, remember it for every dashboard + subsequent
      // auto-refresh, then re-collect and broadcast the scoped numbers. The webview has
      // already flipped the tab optimistically; refreshAll re-pushes usagePeriod too.
      const next = usage.normalizeReport(message.period);
      if (next !== usageReport) usageReport = next;
      refreshAll();
      return;
    }

    case "saveProject":
      // The edited profile posted back from the panel modal (validated + written).
      runSaveProject(message.name, message.profile);
      return;

    case "command": {
      const id = message.id;
      logLine(`command: ${id}${message.project ? " (" + message.project + ")" : ""}`);
      if (id === "showLogs") { showLogs(); return; }
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
      if (id === "exportConfig") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        lifecycle.run(id, { scriptsDir }); // export doesn't touch project selection
        return;
      }
      if (id === "reprovision" || id === "reinstall" || id === "redownload") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        // Pass the effective project selection so the console doesn't re-prompt (and a
        // reprovision keeps the current projects instead of dropping to "default").
        effectiveProjects().then((projects) => lifecycle.run(id, { scriptsDir, projects }));
        // Fast-poll (5s) so the dashboards reflect the finished reprovision quickly,
        // reverting to the normal cadence once it records a new provisioned commit.
        if (id === "reprovision") beginReprovisionFastRefresh();
        return;
      }
      if (id === "updateConstruct") { runUpdateConstruct(); return; }
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
    syncAutoRefresh();
    this.context.subscriptions.push(webviewView.onDidDispose(() => { liveWebviews.delete(webviewView.webview); syncAutoRefresh(); }));
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
  syncAutoRefresh();
  p.onDidDispose(() => { liveWebviews.delete(p.webview); if (panel === p) panel = undefined; syncAutoRefresh(); });
}

/** Open (or reveal) the full control panel as a wide editor tab. */
function openPanel(context) {
  if (panel) {
    // Bring the EXISTING panel to the front. Use reveal() with no column so it surfaces
    // in the column it already occupies — `reveal(ViewColumn.Active)` MOVES the panel to
    // the active column, which fails to surface a hidden panel when focus is on the
    // sidebar (the reported "no window appears on second open"). If the reference is
    // stale/disposed (a dispose that raced a reload), recreate it below.
    try { panel.reveal(); return; }
    catch (_) { panel = undefined; }
  }
  const p = vscode.window.createWebviewPanel(
    "construct.controlPanel",
    "The Construct",
    vscode.ViewColumn.Active,
    { ...webviewOptions(context.extensionUri), retainContextWhenHidden: true }
  );
  setupPanel(p, context);
}

function activate(context) {
  // Route lifecycle/update launch logging into the Construct Output channel, and let
  // `construct.debug` keep launched consoles open so errors are readable.
  lifecycle.configure({ log: logLine, isDebug: debugEnabled });
  logLine(`activate: remoteAuthority=${safeRemoteAuthority() || "(local)"} debug=${debugEnabled()}`);
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", new ConstructViewProvider(context), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.commands.registerCommand("construct.openPanel", () => openPanel(context)),
    vscode.commands.registerCommand("construct.refresh", () => refreshAll()),
    vscode.commands.registerCommand("construct.showLogs", () => showLogs())
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
  maybeAutoEnableAudio(context); // arm mic passthrough now if the saved preference is on
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
    if (!remote.shouldAutoOpenPanel(safeRemoteAuthority(), context.workspaceState.get(KEY))) return;
    context.workspaceState.update(KEY, true);
    openPanel(context);
  } catch (_) { /* never break activation for an optional convenience */ }
}

function deactivate() {
  // Release the mic + kill the reverse tunnel on shutdown. Best-effort and
  // synchronous (deactivate can't reliably await): dispose() tears down the local
  // side (tunnel child + server + any active native recorder). The VM shim only
  // streams while a tunnel exists — which it no longer does — so leaving it until the
  // next explicit disable is harmless; the guard patch is likewise inert without the shim.
  try { if (hostAudio) hostAudio.dispose(); } catch (_) {}
  hostAudio = undefined;
  stopAutoRefresh();
}

module.exports = { activate, deactivate };
