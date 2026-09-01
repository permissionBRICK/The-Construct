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
const instances = require("./src/instances");
const lifecycle = require("./src/lifecycle");
const updates = require("./src/updates");
const usage = require("./src/usage");
const remote = require("./src/remote");
const vmpower = require("./src/vmpower");
const projects = require("./src/projects");
const audio = require("./src/audio");
const t3code = require("./src/t3code");
const repatch = require("./src/repatch");
const configsync = require("./src/configsync");
const importui = require("./src/importui");
const zip = require("./src/zip");
const themes = require("./src/themes");
const notify = require("./src/notify");

/** The single editor-tab panel instance, if open. */
let panel; // vscode.WebviewPanel | undefined

/** The sidebar launcher view, if resolved — kept so a theme change can re-render it. */
let launcherView; // vscode.WebviewView | undefined

/** The theme-picker webview, if open (single instance, like the panel). */
let themePicker; // vscode.WebviewPanel | undefined

/** The host-side mic-passthrough orchestrator (audio.HostAudio), live only while
 *  passthrough is enabled. */
let hostAudio; // audio.HostAudio | undefined

/** Recorder-failure reasons already surfaced this enable, so we warn once (not on
 *  every record-start). Reset in enableAudio. */
let micWarnedReasons = new Set();

/** Pending one-shot timer for the startup patch-verification pass (repatch.js). Held
 *  so deactivate() can cancel it if the window closes inside the delay window. */
let repatchTimer = null;

/** Every currently-live webview (sidebar view + editor panel) for broadcast refresh. */
const liveWebviews = new Set();

// ── Active instance ─────────────────────────────────────────────────────────
// One VM per window. The registry (%LOCALAPPDATA%\The-Construct\instances.json) lists
// them; with no registry file it synthesizes exactly one — `agent-vm`, today's
// literals — so an existing install behaves identically and never sees any of this.
//
// EVERY call that reaches the VM goes through activeCfg(); that single helper is the
// reason a new call site can't silently fall back to the hardcoded default. The
// instance object itself (activeInstance()) rides into lifecycle/vmpower/config-sync.
const ACTIVE_INSTANCE_KEY = "construct.activeInstance"; // workspaceState (per window)
let extensionContext = null;          // set in activate(); owns workspaceState
let registryCache = null;             // { at, registry } — the parsed instances.json
let registryProblemsShown = "";       // last problem set surfaced, so we toast once
const REGISTRY_TTL_MS = 5000;         // re-read the (tiny) file at most every 5s
/** The instance the mic tunnel was opened for, so a switch can retarget it. */
let hostAudioInstance = null;
/** The instance the notification watcher is connected to. */
let notifyInstance = null;
/** The status-bar item showing the active instance (only when >1 exists). */
let instanceStatusItem = null;
/**
 * The generation gate for the active instance (instances.createGate). Every async
 * refresh pipeline captures a token before its first await and re-checks it after each
 * one, so a slow stage that resolves AFTER a switch is discarded instead of painting
 * the previous VM's data under the new instance's name.
 */
const instanceGate = instances.createGate(instances.DEFAULT_INSTANCE_NAME);

/** The instance registry, re-read at most every REGISTRY_TTL_MS. Never throws. */
function registryNow(force) {
  const now = Date.now();
  if (!force && registryCache && now - registryCache.at < REGISTRY_TTL_MS) return registryCache.registry;
  let reg;
  try { reg = instances.load({ env: process.env }); }
  catch (e) {
    // load() is documented never to throw; stay defensive so a surprise can't break
    // the panel — degrade to the synthesized default WITHOUT touching the disk again.
    logLine("instances: registry load failed — " + (e && e.message ? e.message : e));
    reg = instances.load({ path: "" });
  }
  registryCache = { at: now, registry: reg };
  reportRegistryProblems(reg);
  return reg;
}

/** Surface registry problems ONCE per distinct problem set: a malformed file must be
 *  visible (it silently changes which VM you are driving) without nagging every tick. */
function reportRegistryProblems(reg) {
  const problems = (reg && reg.problems) || [];
  const key = problems.join("\n");
  if (key === registryProblemsShown) return;
  registryProblemsShown = key;
  if (!problems.length) return;
  for (const p of problems) logLine("instances: " + p);
  try {
    vscode.window.showWarningMessage(
      "The Construct instance registry has problems — using the default instance where needed. " +
      problems[0] + (problems.length > 1 ? ` (+${problems.length - 1} more; see the Construct log)` : "")
    );
  } catch (_) { /* never break a refresh over a toast */ }
}

/** The `construct.instance` setting (global override), or "" when unset. */
function instanceSetting() {
  try { return String(vscode.workspace.getConfiguration("construct").get("instance") || "").trim(); }
  catch (_) { return ""; }
}

/** The window's persisted instance choice, or "" when it has never chosen. */
function workspaceInstance() {
  try { return String((extensionContext && extensionContext.workspaceState.get(ACTIVE_INSTANCE_KEY)) || "").trim(); }
  catch (_) { return ""; }
}

/**
 * The ACTIVE instance for this window: setting > workspaceState > registry default
 * (instances.resolveActive owns that precedence, and skips a name the registry no
 * longer has). Always returns a usable instance — the synthesized `agent-vm` when
 * nothing else applies, which is what makes every downstream call zero-change.
 */
function activeInstance() {
  const picked = instances.resolveActive({
    registry: registryNow(),
    setting: instanceSetting(),
    workspaceValue: workspaceInstance(),
  });
  // Keep the gate in step with the RESOLVED answer, so a change that arrives by any
  // route (the setting, another window's registry edit, adoption) invalidates in-flight
  // refreshes even when it didn't come through switchInstance().
  instanceGate.set(picked.instance.name);
  return picked.instance;
}

/** The ssh.js cfg for the active instance — the ONE way any module reaches the VM. */
function activeCfg() {
  return instances.toSshCfg(activeInstance());
}

/**
 * Capture the instance a USER ACTION targets. Commands are multi-step (a modal, a VM
 * probe, a minutes-long clone), so re-reading "the active instance" in a later step
 * would let a switch redirect the rest of the action — including the destructive ones,
 * where the confirmation given for A would be executed against B. Every command entry
 * point captures once and uses `target.cfg` / `target.instance` all the way through.
 */
function actionTarget() {
  return instances.captureTarget(instanceGate, activeInstance());
}

/**
 * Refuse to take an irreversible step when the window switched instances since the
 * action started, and say why. Mirrors the existing "changed elsewhere while this
 * prompt was open" guard in offerApplyCheckpoints: doing nothing and explaining beats
 * acting on either VM, because we can no longer tell which one the user meant.
 * Returns true when the caller must abort.
 */
function targetSuperseded(target, what) {
  if (!instances.targetSuperseded(instanceGate, target)) return false;
  const now = activeInstance().name;
  logLine(`instances: ${what} was started for "${target.name}" but the window switched to "${now}" — aborted`);
  vscode.window.showWarningMessage(
    `${what} was started for the “${target.name}” instance, but this window has since switched to “${now}” — nothing was done. Switch back and try again.`
  );
  return true;
}

// The currently selected token-usage period ("daily"|"monthly"), shared across every
// dashboard. Drives both the ccusage window we collect and the active tab the panel
// highlights; the webview flips it via a {type:'setUsagePeriod'} message.
let usageReport = usage.DEFAULT_REPORT;

// ── Config-sync engine state ────────────────────────────────────────────────
// The sync tick (docs/config-sync.md D8) reconciles host profiles (cfgDir) with
// the VM store (/opt/construct/projects) via a git-merge-based flow. It piggybacks
// the existing 30s refresh timer but self-throttles to >=5 min between automatic
// ticks; immediate triggers are: the panel "sync now" button, an fs.watch event
// on cfgDir/projects (debounced 2s), and once at activation when a dashboard opens.
// state.configSync (D9) is host-derived: NOT cleared by clearLiveVmData.
let cfgDir = null;           // host.configDir(process.env), resolved once at activation
let runGit = null;           // configsync.makeGitRunner, created once
let gitDetected = null;      // cached {present, version} with TTL
let gitDetectedAt = 0;       // ms when gitDetected was cached
const GIT_DETECT_TTL = 5 * 60 * 1000; // 5 min cache like augmentUpdates
let lastSyncTickAt = 0;      // ms: last automatic tick (for the 5-min throttle)
const SYNC_TICK_MIN_MS = 5 * 60 * 1000;
let syncTickInFlight = false; // exposed as simple state for UI/recovery gates
let syncTickPromise = null;   // same-window callers queue behind the active tick
let syncTickFollowupPromise = null; // all queued callers share one fresh follow-up
let lastSyncResult = null;    // most recent TickResult (for state.configSync)
let configWatcher = null;     // fs.watch handle on cfgDir/projects
let lastAutoImportAt = 0;     // ms: last auto-import attempt (stamped BEFORE awaiting)
let importInflightPromise = null; // shared promise for coalescing concurrent imports

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
// The in-flight probe is keyed BY INSTANCE: coalescing is only correct for callers
// asking about the same VM, and a probe of the previous instance must never be handed
// to a caller that has already switched.
let inflightProbe = null;      // { name, promise }
function probeOnce(inst) {
  const target = inst || activeInstance();
  if (inflightProbe && inflightProbe.name === target.name) return inflightProbe.promise;
  const promise = probe.probe({ cfg: instances.toSshCfg(target) }).then((s) => s, () => ({ online: false }));
  const entry = { name: target.name, promise };
  inflightProbe = entry;
  const clear = () => { if (inflightProbe === entry) inflightProbe = null; };
  promise.then(clear, clear);
  return promise;
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
async function augmentUsage(state, report, inst) {
  try {
    return await usage.augment(state, { report, cfg: instances.toSshCfg(inst || activeInstance()) });
  } catch (_) { return state; }
}

/** Add window-local fields (whether THIS window is already on the VM) to a probed
 *  state. Synchronous, so it rides the first push. */
function withLocalState(state, inst) {
  const target = inst || activeInstance();
  let connected = false;
  try { connected = remote.isConnectedToVm(safeRemoteAuthority(), instances.toSshCfg(target)); } catch (_) { /* default false */ }
  return { ...state, connected, ...instanceState(target) };
}

/** The instance fields every state push carries: which instance this window drives and
 *  (only when there is a choice to make) the list the picker/dropdown offers. The name
 *  is the instance the CALLER's pipeline started under, never "whatever is current
 *  now" — a payload must always be labelled with the VM it actually came from. */
function instanceState(inst) {
  try {
    const reg = registryNow();
    const target = inst || activeInstance();
    const names = instances.list(reg).map((i) => i.name);
    const out = { instance: target.name };
    // The dropdown renders ONLY when more than one instance exists, so a single-VM
    // install's panel is pixel-identical to before.
    if (names.length > 1) out.instances = names;
    return out;
  } catch (_) { return {}; }
}

/** Post a state to a webview, stamping the CURRENT usage period at SEND time. usageReport
 *  is the single source of truth for the active daily/monthly tab, so every render (even
 *  a slow/stale refresh landing late) reflects the live selection and can never re-select
 *  an out-of-date tab. The panel highlights the tab from this field on the sync push. */
/** Cached configSync state for postState. */
let cachedConfigSync = null;

function postState(target, state) {
  const extra = { usagePeriod: usageReport };
  if (cachedConfigSync) extra.configSync = cachedConfigSync;
  safePost(target, { type: "state", state: { ...state, ...extra } });
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
async function withVmState(state, inst) {
  try {
    if (state && state.online) return { ...state, vmState: "running" };
    const vmState = await vmpower.queryVmState({ instance: inst || activeInstance() });
    return { ...state, vmState };
  } catch (_) {
    return { ...state, vmState: "unknown" };
  }
}

/**
 * Fold the LOCAL project profiles + persisted selection into the state's `projects`
 * chips. The chips the panel shows come from the host-side profile files
 * (<scriptsDir>/projects/*.json) — the same set editProject/importFromVm/
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
  // Prefer cfgDir (the config-sync location); fall back to scriptsDir when cfgDir
  // is null (no LOCALAPPDATA / TEMP).
  let projRoot;
  try {
    const dir = cfgDir || host.configDir(process.env);
    if (dir) { projRoot = dir; } else { projRoot = resolveScriptsDir(); }
  } catch (_) { return state; }
  if (!projRoot) return state;
  let available, selected;
  try {
    available = host.listProjectProfiles(projRoot);
    const scriptsDir = resolveScriptsDir();
    selected = scriptsDir ? host.readSelectedProjects(scriptsDir) : [];
  } catch (_) { return state; }
  if (!available.length) return state;
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
async function effectiveProjects(inst) {
  try {
    const scriptsDir = resolveScriptsDir();
    if (scriptsDir) {
      const saved = host.readSelectedProjects(scriptsDir);
      if (saved && saved.length) return saved;
    }
  } catch (_) { /* fall through to the live set */ }
  try {
    // Probe the instance the ACTION targets, not whatever is active by the time this
    // slow round-trip runs — the project list decides what a rebuild provisions.
    const st = await probeOnce(inst);
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
  // Bind the whole pipeline to the instance it starts under. Every await below is
  // followed by a gate check, so a stage that outlives a switch is dropped rather than
  // posted — the same discipline the usagePeriod binding already used for periods.
  const inst = activeInstance();
  const gate = instanceGate.token();
  const probed = await probeOnce(inst);
  if (!instanceGate.valid(gate)) return;
  const state = withProjects(await withVmState(withLocalState(probed, inst), inst));
  if (!instanceGate.valid(gate)) return;
  postState(webview, state);
  const aug = await augmentUpdates(state);
  if (!instanceGate.valid(gate)) return;
  if (aug !== state) postState(webview, aug);
  // Usage is a slower SSH+ccusage round-trip: BIND it to the report we start with and
  // DISCARD the result if the user switched the period meanwhile (a stale daily run must
  // never land as monthly's numbers). postState always stamps the CURRENT usagePeriod.
  const report = usageReport;
  const withUsage = await augmentUsage(aug, report, inst);
  if (!instanceGate.valid(gate)) return;
  if (withUsage !== aug && usageReport === report) postState(webview, withUsage);
}

/** Probe once and broadcast the same state to every live webview, then broadcast
 *  the update-augmented state. */
async function refreshAll() {
  // Keep the status-bar indicator honest even when the registry gains/loses an
  // instance behind our back (another window, a hand edit, a future installer).
  syncInstanceStatusItem();
  if (liveWebviews.size === 0) return;
  // Same instance binding as refreshState: capture up front, re-check after every
  // await, and abandon the whole continuation (posts AND cache writes) on a switch.
  const inst = activeInstance();
  const gate = instanceGate.token();
  const probed = await probeOnce(inst);
  if (!instanceGate.valid(gate)) return;
  const state = withProjects(await withVmState(withLocalState(probed, inst), inst));
  if (!instanceGate.valid(gate)) return;
  for (const w of liveWebviews) postState(w, state);
  const aug = await augmentUpdates(state);
  if (!instanceGate.valid(gate)) return;
  if (aug !== state) for (const w of liveWebviews) postState(w, aug);
  const report = usageReport;
  const withUsage = await augmentUsage(aug, report, inst);
  if (!instanceGate.valid(gate)) return;
  if (withUsage !== aug && usageReport === report) for (const w of liveWebviews) postState(w, withUsage);
  // Config-sync: update the cached state and run a throttled tick. Best-effort.
  try {
    const cs = await buildConfigSyncState();
    if (!instanceGate.valid(gate)) return;
    cachedConfigSync = cs;
    for (const w of liveWebviews) postState(w, withUsage !== aug ? withUsage : aug);
    await maybeAutoSync();
    if (!instanceGate.valid(gate)) return;
    // Auto-import runs regardless of git presence, so users without git still get
    // automatic discovery of new VM repos (docs/config-sync.md §10 degraded mode).
    await maybeAutoImport();
    if (!instanceGate.valid(gate)) return;
    const cs2 = await buildConfigSyncState();
    if (!instanceGate.valid(gate)) return;
    cachedConfigSync = cs2;
    for (const w of liveWebviews) postState(w, withUsage !== aug ? withUsage : aug);
  } catch (_) { /* best-effort */ }
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

// ── VM → desktop notifications ───────────────────────────────────────────────
// An agent on the VM runs `construct notify "…"`; entries arrive here over ONE
// long-lived SSH connection that blocks on the VM until something is queued, and we
// raise a real Windows toast (see src/notify.js for the protocol and why it is
// deliberately one-way).
//
// A stream, not a poll: no reconnect every few seconds, no handshake cost while
// idle, and delivery is immediate rather than "within the interval". The cost moves
// to keeping the connection healthy — hence keepalives on the ssh side, a heartbeat
// on the VM side, and the supervisor below, which reconnects with backoff whenever
// the child dies (VM rebooted, laptop slept, Wi-Fi switched, link idled out).
//
// Unlike auto-refresh this runs whether or not a dashboard is open: the whole point
// is reaching the user who never opened the panel.
let notifyChild = null;          // the live ssh child, if connected
let notifyRestartTimer = null;
let notifyStopped = false;       // deactivate/disable: stop respawning
let notifyAttempt = 0;           // consecutive failed connections, for the backoff
let notifyBuffer = "";           // partial stream line carried between chunks

/** Whether VM notifications are switched on (setting; default true). */
function notificationsEnabled() {
  try { return vscode.workspace.getConfiguration("construct").get("notifications") !== false; }
  catch (_) { return true; }
}

/** Open the watcher connection, or schedule a retry if it can't be opened. */
function startNotifyWatch() {
  if (notifyChild || notifyRestartTimer) return;
  notifyStopped = false;
  if (!notificationsEnabled()) return;
  const cfg = activeCfg();
  notifyInstance = activeInstance().name;
  const args = notify.buildWatchArgs(ssh, cfg, fs.existsSync(ssh.keyPath(ssh.resolveCfg({ cfg }))));
  let child;
  try {
    child = require("child_process").spawn("ssh", args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  } catch (e) {
    logLine("notify: could not start the watcher: " + (e && e.message ? e.message : e));
    scheduleNotifyRestart();
    return;
  }
  notifyChild = child;
  notifyBuffer = "";
  const startedAt = Date.now();
  let stderr = "";
  if (child.stdout) {
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      const { lines, rest } = notify.splitStream(notifyBuffer, chunk);
      notifyBuffer = rest;
      if (lines.length) onNotifyLines(lines);
    });
  }
  if (child.stderr) child.stderr.on("data", (d) => { if (stderr.length < 2000) stderr += String(d); });
  const ended = (why) => {
    if (notifyChild !== child) return;      // superseded by a newer child
    notifyChild = null;
    // A connection that lived a while was healthy: forget the earlier failures so a
    // one-off drop retries fast instead of inheriting a long backoff.
    if (Date.now() - startedAt >= notify.CONNECTION_HEALTHY_MS) notifyAttempt = 0;
    const detail = (stderr.trim().split("\n").pop() || why || "").slice(0, 200);
    logLine("notify: watcher disconnected" + (detail ? ` (${detail})` : "") + "; reconnecting");
    scheduleNotifyRestart();
  };
  if (child.on) {
    child.on("error", (e) => ended(e && e.message ? e.message : String(e)));
    child.on("exit", (code) => ended(code == null ? "" : "ssh exited " + code));
  }
  logLine("notify: watcher connected");
}

/** Reconnect after a backoff (2s doubling to 60s), unless we've been told to stop. */
function scheduleNotifyRestart() {
  if (notifyStopped || notifyRestartTimer || !notificationsEnabled()) return;
  notifyAttempt += 1;
  const delay = notify.reconnectDelayMs(notifyAttempt);
  notifyRestartTimer = setTimeout(() => {
    notifyRestartTimer = null;
    if (!notifyStopped) startNotifyWatch();
  }, delay);
  if (notifyRestartTimer.unref) notifyRestartTimer.unref();
}

/** Tear the watcher down (deactivate, or the setting switched off). */
function stopNotifyWatch() {
  notifyStopped = true;
  if (notifyRestartTimer) { clearTimeout(notifyRestartTimer); notifyRestartTimer = null; }
  const child = notifyChild;
  notifyChild = null;
  notifyBuffer = "";
  // Killing the local ssh closes the channel; the VM-side watcher then dies on its
  // next heartbeat write (SIGPIPE), so nothing is left running on the VM either.
  if (child) { try { child.kill(); } catch (_) {} }
}

/** Deliver a batch of streamed lines. Never throws. */
async function onNotifyLines(lines) {
  try {
    const picked = notify.selectDeliverable(notify.parseEntries(lines.join("\n")), { now: Date.now() });
    if (picked.stale) logLine(`notify: dropped ${picked.stale} notification(s) older than the delivery window`);
    for (const entry of picked.deliver) await deliverNotification(entry);
    if (picked.extra) {
      await deliverNotification({
        level: "info", title: notify.APP_NAME,
        body: `${picked.extra} more notification${picked.extra === 1 ? "" : "s"} from the VM (see the Construct log)`,
        source: "",
      });
    }
  } catch (e) {
    logLine("notify: delivery failed: " + (e && e.message ? e.message : e));
  }
}

/** Raise one notification: a native Windows toast, falling back to a VS Code
 *  notification when that isn't possible (non-Windows host, blocked execution
 *  policy, notifications switched off in Windows). Never rejects. */
async function deliverNotification(entry) {
  logLine(notify.logLineFor(entry));
  if (process.platform === "win32") {
    const reason = await raiseWindowsToast(entry);
    if (!reason) return;
    logLine("notify: falling back to a VS Code notification (" + reason + ")");
  }
  const text = entry.title ? `${entry.title}: ${entry.body}` : entry.body;
  try {
    if (entry.level === "error") vscode.window.showErrorMessage(text);
    else if (entry.level === "warning") vscode.window.showWarningMessage(text);
    else vscode.window.showInformationMessage(text);
  } catch (_) { /* a notification we cannot show is not worth an exception */ }
}

/** Spawn the toast script. Resolves "" on success, else a short failure reason.
 *  No console window: a powershell.exe spawned straight from the extension host has
 *  no console to inherit and cannot allocate one (see lifecycle.js — the visible
 *  flows must force one via `cmd /c start`), and windowsHide seals it.
 *
 *  A toast that DID appear can still have something to say (it went out under the
 *  fallback app id, or the notifier's setting was odd) — that goes to the log as a
 *  note, because "the toast worked but here is why it may look wrong" is exactly the
 *  information that was missing when this path failed in the field. */
function raiseWindowsToast(entry) {
  const cmd = notify.buildToastCommand(entry, {
    file: notify.powershellPath(process.env, (p) => { try { return fs.existsSync(p); } catch (_) { return false; } }),
  });
  return new Promise((resolve) => {
    let child, stderr = "", settled = false;
    const finish = (reason) => { if (!settled) { settled = true; resolve(reason); } };
    try {
      child = require("child_process").spawn(cmd.file, cmd.args, cmd.options);
    } catch (e) {
      return finish("spawn failed: " + (e && e.message ? e.message : e));
    }
    const killTimer = setTimeout(() => { try { child.kill(); } catch (_) {} finish("toast timed out"); }, 15000);
    if (child.stderr) child.stderr.on("data", (d) => { if (stderr.length < 2000) stderr += String(d); });
    child.on("error", (e) => { clearTimeout(killTimer); finish("spawn failed: " + (e && e.message ? e.message : e)); });
    child.on("close", (code) => {
      clearTimeout(killTimer);
      const res = notify.toastResult(code, stderr);
      if (res.ok && res.note) logLine("notify: toast shown, with a note (" + res.note + ")");
      finish(res.reason);
    });
  });
}

/** Locate the host-side scripts dir: the active instance's pinned `scriptsDir` first
 *  (registry field; null for the default instance), then the `construct.scriptsDir`
 *  override, then newest-install detection — see host.resolveScriptsDir. */
function resolveScriptsDir() {
  const override = vscode.workspace.getConfiguration("construct").get("scriptsDir");
  let pinned = null;
  try { pinned = activeInstance().scriptsDir; } catch (_) { pinned = null; }
  return host.resolveScriptsDir({ instanceScriptsDir: pinned, scriptsDir: override, env: process.env });
}

/** Resolve the config dir. Falls back to null when LOCALAPPDATA/TEMP absent. */
function resolveCfgDir() {
  if (cfgDir === null) cfgDir = host.configDir(process.env) || null;
  return cfgDir;
}

/** Ensure git detection is fresh; caches with GIT_DETECT_TTL. */
async function detectGitCached() {
  if (gitDetected && (Date.now() - gitDetectedAt) < GIT_DETECT_TTL) return gitDetected;
  if (!runGit) runGit = configsync.makeGitRunner({ spawn: require("child_process").spawn });
  try {
    gitDetected = await configsync.detectGit(runGit);
    gitDetectedAt = Date.now();
  } catch (_) {
    gitDetected = { present: false, version: null };
    gitDetectedAt = Date.now();
  }
  return gitDetected;
}

/** Build state.configSync (D9) from the current engine state. Host-derived. */
async function buildConfigSyncState() {
  const dir = resolveCfgDir();
  const git = await detectGitCached();
  const out = {
    gitPresent: git.present,
    repoReady: false, conflict: false, conflictFiles: [], mergeInProgress: false,
    lastSyncAt: lastSyncTickAt || null,
    lastResult: lastSyncResult ? (lastSyncResult.ok ? "ok" : (lastSyncResult.conflict ? "conflict" : (lastSyncResult.blocked ? "blocked" : "error"))) : null,
    blockedReason: lastSyncResult ? (lastSyncResult.blockedReason || null) : null,
    warnings: lastSyncResult ? (lastSyncResult.warnings || []) : [],
    remotes: [],
  };
  if (dir && git.present && runGit) {
    try {
      var rs = await configsync.repoState(runGit, dir);
      if (rs.mergeInProgress && !rs.conflict && !syncTickInFlight) {
        var recovered = await runConfigSync();
        if (recovered) {
          out.lastSyncAt = lastSyncTickAt || null;
          out.lastResult = recovered.ok ? "ok" : (recovered.conflict ? "conflict" : (recovered.blocked ? "blocked" : "error"));
          out.blockedReason = recovered.blockedReason || null;
          out.warnings = recovered.warnings || [];
        }
        rs = await configsync.repoState(runGit, dir);
      }
      out.repoReady = rs.repo; out.conflict = rs.conflict;
      out.conflictFiles = rs.conflictFiles || []; out.mergeInProgress = rs.mergeInProgress;
    } catch (_) {}
    try { out.remotes = configsync.readRemotes(dir); } catch (_) {}
  }
  return out;
}

/** Run a sync tick. Same-window callers wait, then share one follow-up tick so
 * changes made during the active snapshot are not mistaken for having synced. */
async function runConfigSync() {
  var dir = resolveCfgDir();
  if (!dir) return null;
  var git = await detectGitCached();
  if (!git.present) return null;
  if (syncTickPromise) {
    if (!syncTickFollowupPromise) {
      const active = syncTickPromise;
      syncTickFollowupPromise = active.then(function () {
        syncTickFollowupPromise = null;
        return runConfigSync();
      });
    }
    return syncTickFollowupPromise;
  }
  syncTickInFlight = true;
  syncTickPromise = (async function () {
    try {
    // Everything that touches the repo happens inside syncTick, under the
    // cross-process lock (ensureRepo is its step 1) — nothing here may mutate
    // the repo, or two windows could race it.
    configsync.ensureConfigTree(dir);
    // Snapshot the host profile set so profiles that arrive FROM the VM in this
    // tick (an agent's `construct project set`) can be auto-enabled below —
    // otherwise they exist on the host but stay outside the persisted selection,
    // and the next reprovision silently provisions without them.
    var profilesBeforeTick = host.listProjectProfiles(dir);
    var syncCfg = activeCfg();
    var readStore = async function () {
      try {
        var r = await ssh.runRemoteScript(configsync.buildReadStoreScript(), { timeoutMs: 30000, cfg: syncCfg });
        if (r.code < 0) return null;
        return r.stdout || null;
      } catch (_) { return null; }
    };
    var writeStore = async function (script) {
      try {
        var r = await ssh.runRemoteScript(script, { timeoutMs: 30000, cfg: syncCfg });
        if (r.code < 0) return null;
        return r.stdout || null;
      } catch (_) { return null; }
    };
    var result = await configsync.syncTick({
      runGit: runGit, configDir: dir, readStore: readStore, writeStore: writeStore,
      // The instance's config-sync branch ("vm" for the default instance, "vm-<name>"
      // otherwise). B5 implements the option; until then syncTick ignores it, which is
      // harmless because the default instance's value IS syncTick's own default.
      vmBranch: activeInstance().configBranch,
      log: function (level, msg) { logLine("[configsync] [" + level + "] " + msg); },
    });
    lastSyncResult = result;
    lastSyncTickAt = Date.now();
    if (result) {
      var parts = [];
      if (result.ok) parts.push("ok");
      if (result.lockBusy) parts.push("lock busy; skipped");
      if (result.conflict) parts.push("CONFLICT");
      if (result.blocked) parts.push("blocked: " + (result.blockedReason || ""));
      if (result.merged) parts.push("merged");
      if (result.seeded) parts.push("seeded");
      if (result.warnings && result.warnings.length) parts.push("warnings: " + result.warnings.join("; "));
      logLine("sync tick: " + parts.join(" | "));
    }
    if (result && result.ok && !result.lockBusy) {
      await autoEnableNewProfiles(profilesBeforeTick, host.listProjectProfiles(dir));
    }
    // Auto-import: when the VM was reachable this tick, scan for repos not yet
    // covered by a local profile and import them. This replaces the manual
    // "import from VM" button — new configs are discovered automatically on
    // every sync tick. Runs AFTER the sync tick (not inside it) so the lock is
    // released and locally-written profiles don't race the git engine.
    if (result && result.ok && result.vmReadOk) {
      try { await coalescedImport(true); } catch (e) {
        logLine("auto-import from VM failed: " + (e && e.message ? e.message : e));
      }
    }
      return result;
    } finally {
      syncTickInFlight = false;
      syncTickPromise = null;
    }
  })();
  return syncTickPromise;
}

/** Add profiles that newly appeared on the host (synced up from the VM) to the
 *  persisted selection, so reprovisions include them without a manual re-tick.
 *
 *  Respects the seeded-vs-persisted distinction: when the user has EXPLICITLY
 *  persisted a selection (even an empty one via "select profiles → save none"),
 *  only the new names are appended to it — the existing selection is NOT
 *  reseeded from the VM's live list. When no selection has ever been persisted
 *  (the key is absent), effectiveProjects() seeds from the VM live set, and the
 *  fresh names ride along. */
async function autoEnableNewProfiles(before, after) {
  try {
    var afterArr = after || [];
    var beforeSet = new Set(before || []);
    var fresh = afterArr.filter(function (n) {
      return n && !beforeSet.has(n) && !projects.isReservedProfileName(n);
    });
    if (!fresh.length) return;
    var scriptsDir = resolveScriptsDir();
    if (!scriptsDir) return;
    var current;
    if (host.hasPersistedSelection(scriptsDir)) {
      current = host.readSelectedProjects(scriptsDir);
    } else {
      current = await effectiveProjects();
    }
    // Reconcile against the actual post-import profile list (afterArr), not
    // scriptsDir — profiles live in cfgDir and afterArr is the authoritative
    // set of names that exist after the import/sync completed.
    var merged = projects.additiveMergeSelection(current, fresh, afterArr);
    host.saveSelectedProjects(scriptsDir, merged);
    logLine("auto-enabled new project profile(s) from sync: " + fresh.join(", ") + " (selection now: " + merged.join(", ") + ")");
  } catch (e) {
    logLine("auto-enable of new profiles failed: " + (e && e.message ? e.message : e));
  }
}

async function configMergeGate() {
  var dir = resolveCfgDir();
  var git = await detectGitCached();
  if (!dir || !git.present || !runGit) return { blocked: false, dir: dir };
  var pending = await configsync.completePendingMerge(runGit, dir);
  if (pending.completed) {
    logLine("[configsync] completed pending clean merge");
  }
  var rs = await configsync.repoState(runGit, dir);
  if (rs.conflict || rs.mergeInProgress) {
    return {
      blocked: true,
      dir: dir,
      reason: pending.reason || "Resolve the config merge first -- open the config repo and commit the merge, then try again.",
    };
  }
  return { blocked: false, dir: dir };
}

/**
 * Shared pre-flight for destructive lifecycle flows (reinstall/redownload/
 * reprovision and customRebuild). Runs the three required steps:
 *   (a) importFromVm — catch any undiscovered repos;
 *   (b) runConfigSync — final sync tick so profiles are synced;
 *   (c) configMergeGate — verify no unresolved conflicts.
 *
 * Returns { ok:true } when the flow may proceed, or { ok:false, reason, dir }
 * when it must be blocked. Fail-CLOSED: if the gate check throws or cannot
 * determine the state, it blocks rather than proceeding blindly.
 */
async function lifecyclePreFlight(actionLabel, target) {
  var cfgDir = resolveCfgDir();
  // (a) Import any VM repos not yet covered by a local profile.
  var importResult = await coalescedImport(true);
  if (target && targetSuperseded(target, "This action")) return { ok: false, reason: actionLabel + " cancelled." };
  if (!importResult) {
    var skip = await vscode.window.showWarningMessage(
      "Could not reach the VM to check for new project configs. " +
        "Proceeding may miss repos not yet imported. Continue with " + actionLabel + "?",
      { modal: true }, "Continue anyway");
    if (skip !== "Continue anyway") return { ok: false, reason: actionLabel + " cancelled." };
  } else if (importResult.failed && importResult.failed.length) {
    var fskip = await vscode.window.showWarningMessage(
      "Some project configs could not be written: " + importResult.failed.join(", ") +
        ". Continue with " + actionLabel + "?",
      { modal: true }, "Continue anyway");
    if (fskip !== "Continue anyway") return { ok: false, reason: actionLabel + " cancelled." };
  }
  // (b) Run one final config-sync tick so profile files are synced.
  var git = await detectGitCached();
  if (git.present) {
    var syncResult;
    try { syncResult = await runConfigSync(); } catch (_) { syncResult = null; }
    var syncProblem = !syncResult || syncResult.lockBusy || syncResult.blocked
      || (!syncResult.ok && !syncResult.conflict)
      || (syncResult.ok && syncResult.vmReadOk === false);
    if (syncProblem) {
      var reason = syncResult && syncResult.lockBusy ? "Another VS Code window holds the sync lock."
        : syncResult && syncResult.blocked ? ("Sync is blocked: " + (syncResult.blockedReason || "unknown"))
        : syncResult && syncResult.ok && syncResult.vmReadOk === false ? "Could not read VM config store — sync is incomplete."
        : "Config sync did not complete successfully.";
      var pick = await vscode.window.showWarningMessage(
        reason + " Profiles may not be up to date. Continue with " + actionLabel + "?",
        { modal: true }, "Continue anyway");
      if (pick !== "Continue anyway") return { ok: false, reason: actionLabel + " cancelled." };
    }
    // (c) Conflict gate: if there are sync conflicts, do NOT proceed.
    // Fail-CLOSED: an exception from the gate blocks rather than proceeding.
    var gate;
    try {
      gate = await configMergeGate();
    } catch (e) {
      return {
        ok: false,
        dir: cfgDir,
        reason: "Could not verify config sync state before " + actionLabel +
          ". Check the config repo for issues, then try again." +
          (e && e.message ? " (" + e.message + ")" : ""),
      };
    }
    if (gate.blocked) {
      return {
        ok: false,
        dir: gate.dir,
        reason: "Config sync has unresolved conflicts — resolve them before " + actionLabel +
          ".\n\nOpen the config repo, resolve the merge conflicts in the editor, then commit and retry. " +
          "The Config sync strip in the Projects panel lists the conflicting files.",
      };
    }
  }
  return { ok: true };
}

/** Show the pre-flight block as a modal warning with explicit action choices. */
function showPreFlightBlock(result) {
  if (!result.dir) {
    vscode.window.showWarningMessage(result.reason, { modal: true }, "OK");
    return;
  }
  vscode.window.showWarningMessage(
    result.reason,
    { modal: true },
    "Open config repo", "Cancel"
  ).then(function (pick) {
    if (pick === "Open config repo") {
      vscode.commands.executeCommand("vscode.openFolder", vscode.Uri.file(result.dir), true);
    }
  });
}

/** Throttled sync tick for auto-refresh: only runs if >=5 min since last. */
async function maybeAutoSync() {
  if (Date.now() - lastSyncTickAt < SYNC_TICK_MIN_MS) return;
  await runConfigSync();
}

/** Coalesced import: if an import is already in flight, join it instead of
 *  starting a second SSH scan. When `force` is true, bypass the time throttle
 *  (used by explicit user actions like Sync Now and lifecycle pre-flight). */
function coalescedImport(force) {
  if (importInflightPromise) return importInflightPromise;
  if (!force && Date.now() - lastAutoImportAt < SYNC_TICK_MIN_MS) return Promise.resolve(null);
  lastAutoImportAt = Date.now();
  importInflightPromise = importFromVm().catch(function () { return null; }).then(function (r) {
    importInflightPromise = null;
    return r;
  });
  return importInflightPromise;
}

/** Throttled auto-import: runs importFromVm regardless of git presence, so users
 *  without git still get automatic discovery. Uses the same 5-min throttle as the
 *  sync tick. Coalesces concurrent attempts so offline/hanging SSH doesn't cause
 *  unbounded overlapping scans. */
function maybeAutoImport() {
  return coalescedImport(false);
}

/** Set up fs.watch on cfgDir/projects (debounced 2s). Tolerates watcher errors. */
function startConfigWatcher() {
  if (configWatcher) return;
  var dir = resolveCfgDir();
  if (!dir) return;
  var projDir = path.join(dir, "projects");
  try { fs.mkdirSync(projDir, { recursive: true }); } catch (_) {}
  var debounce = null;
  try {
    configWatcher = fs.watch(projDir, { persistent: false }, function () {
      if (debounce) clearTimeout(debounce);
      debounce = setTimeout(function () {
        debounce = null;
        runConfigSync().then(function () { refreshAll(); });
      }, 2000);
    });
    configWatcher.on("error", function () {});
  } catch (_) {}
}

function stopConfigWatcher() {
  if (configWatcher) { try { configWatcher.close(); } catch (_) {} configWatcher = null; }
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

/** Force-update coding agents on the VM over SSH, with a progress notification,
 *  then re-probe so the new versions + cleared badges show. `ids` narrows the
 *  update to specific agents (the panel's per-agent ↑ tag); omitted = all. */
function runUpdateAgents(ids) {
  const subset = Array.isArray(ids) && ids.length ? ids : null;
  const requested = subset || ["claude-code", "codex", "opencode", "t3code"];
  const scriptsDir = resolveScriptsDir();
  let sourceManagedT3 = false;
  try {
    const settings = scriptsDir ? host.readSettings(scriptsDir) : {};
    sourceManagedT3 =
      requested.includes("t3code") &&
      settings.t3code === true &&
      settings.t3codeLimitResume === true;
  } catch (_) { /* fall back to the normal updater */ }
  const remotelyUpdated = sourceManagedT3
    ? requested.filter((id) => id !== "t3code")
    : requested;
  if (sourceManagedT3 && remotelyUpdated.length === 0) {
    void startConstructReprovision(scriptsDir);
    return;
  }
  const what = subset ? subset.join(", ") : "coding agents";
  const script = updates.buildAgentUpdateScript(remotelyUpdated);
  // Multi-minute npm work followed (sometimes) by a reprovision: one target for both.
  const t = actionTarget();
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `Updating ${what} on the VM…`, cancellable: false },
    async () => {
      const r = await ssh.runRemoteScript(script, { timeoutMs: 300000, cfg: t.cfg });
      if (r.code === 0) {
        vscode.window.showInformationMessage(subset ? `${what} updated.` : "Coding agents updated.");
      } else {
        vscode.window.showErrorMessage(
          `Updating ${what} failed (exit ${r.code}). ${(r.stderr || "").slice(0, 200)}`.trim()
        );
      }
      refreshAll(); // re-probe versions + clear the update badges
      if (sourceManagedT3) await startConstructReprovision(scriptsDir, t);
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
  const startInstance = activeInstance();
  if (!vmpower.startVm({ debug: debugEnabled(), instance: startInstance })) return; // startVm surfaces its own failure
  vscode.window.showInformationMessage("Starting the Construct VM — approve the UAC prompt.");
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Waiting for the Construct VM to come online…", cancellable: true },
    async (_progress, token) => {
      const intervalMs = 4000, maxMs = 150000;
      let waited = 0;
      while (waited < maxMs) {
        if (token.isCancellationRequested) return;
        if (await ssh.isReachable({ timeoutMs: 6000, cfg: instances.toSshCfg(startInstance) })) {
          remote.openOnVm({ path: "/root/repos", newWindow: false, cfg: instances.toSshCfg(startInstance) });
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

/**
 * The automatic-checkpoint preference was saved. The value is applied whenever the VM is
 * next CREATED (Auto-Install → Create-AgentVM), which covers every future reinstall /
 * redownload — but the VM that exists right now keeps Hyper-V's current policy until
 * someone changes it. So offer to apply it now.
 *
 * "Apply now" launches Set-AgentVmCheckpoints.ps1 in an ELEVATED host console (the
 * Hyper-V cmdlets need admin → one UAC prompt). Turning the setting OFF also removes the
 * automatic checkpoint Hyper-V already took — the script deletes only checkpoints it can
 * positively identify as automatic, and asks about each one it can't, so a checkpoint the
 * user made themselves is never deleted WITHOUT AN EXPLICIT YES.
 *
 * The script reports its outcome through a result file (the same mechanism
 * Update-Construct.ps1 uses), and a confirmed run records `vmAutoCheckpointsApplied`.
 * That marker is what makes the offer correct when the Hyper-V probe can't read the VM's
 * real policy — see vmpower.shouldOfferCheckpointApply.
 *
 * Best-effort and non-blocking: an older install without the script, or a non-Windows
 * host, explains itself and leaves the saved preference in place for the next rebuild.
 */
async function offerApplyCheckpoints(scriptsDir, enabled, changed) {
  if (process.platform !== "win32") {
    // Don't fail silently: the toggle looks live, but Hyper-V lives on the Windows host.
    if (changed) {
      vscode.window.showWarningMessage(
        "Saved. Automatic checkpoints are a Hyper-V setting on the Windows host, which isn't reachable from here — the preference applies the next time the VM is rebuilt."
      );
    }
    return;
  }
  // Compare against the VM's ACTUAL policy, not against what the settings file used to
  // say. A VM created before Construct started disabling checkpoints has the policy ON
  // with no saved key at all, so a preference that "didn't change" (off → off) still
  // needs applying — and after "Later" or a declined UAC, the next save must offer again.
  // When the (permission-gated) probe can't tell, the applied-marker stands in for it.
  const t = actionTarget();
  const actual = await vmpower.queryAutoCheckpoints({ instance: t.instance });
  let applied = null;
  try { applied = host.readAppliedAutoCheckpoints(scriptsDir); } catch (_) { applied = null; }
  logLine(`checkpoints: want=${enabled ? "on" : "off"} actual=${actual} applied=${applied} changed=${!!changed}`);
  if (!vmpower.shouldOfferCheckpointApply(actual, enabled, applied)) return;
  const scriptPath = path.join(scriptsDir, lifecycle.CHECKPOINTS);
  if (!fs.existsSync(scriptPath)) {
    // No live-apply script means these scripts predate the feature entirely — so the
    // REBUILD can't honour it either (lifecycle.run drops the flag for exactly this
    // reason). Don't promise a next-Reinstall fix we can't deliver.
    vscode.window.showWarningMessage(
      "Saved, but this Construct install's host scripts are too old to change automatic checkpoints (here or during a rebuild). Update Construct first."
    );
    return;
  }
  const detail = enabled
    ? "Hyper-V will snapshot the VM at every start. This applies from the VM's next start; it's also used when the VM is rebuilt."
    : "Hyper-V will stop snapshotting the VM at every start, and the automatic checkpoint it already took will be removed (its disk is merged back in the background). Checkpoints Hyper-V doesn't report as automatic are never removed on their own — the console asks about each one separately first.";
  const pick = await vscode.window.showInformationMessage(
    `Apply automatic checkpoints = ${enabled ? "on" : "off"} to the current VM now?`,
    { modal: true, detail: detail + " Needs administrator rights (a UAC prompt)." },
    "Apply now"
  );
  if (pick !== "Apply now") return;
  // Re-read the preference after the modal: another window (its own extension host,
  // its own copy of this flow) may have saved the OPPOSITE value while this dialog sat
  // open, and applying the stale one would leave the VM disagreeing with the file.
  let stillWanted = enabled;
  try { stillWanted = host.readSettings(scriptsDir).autoCheckpoints === true; } catch (_) { /* keep the captured value */ }
  if (stillWanted !== enabled) {
    vscode.window.showWarningMessage(
      `Automatic checkpoints were changed to ${stillWanted ? "on" : "off"} elsewhere while this prompt was open — nothing was applied. Save again to apply the current setting.`
    );
    return;
  }
  // Result file: the elevated console is detached, so this is the only way to learn
  // whether the change actually landed (a declined UAC or a Hyper-V error must NOT be
  // recorded as applied). Mirrors runUpdateConstruct's CONSTRUCT_UPDATE_RESULT.
  const resultFile = path.join(os.tmpdir(), `construct-checkpoints-${Date.now()}.result`);
  try { fs.unlinkSync(resultFile); } catch (_) {}
  if (targetSuperseded(t, "Applying automatic checkpoints")) return;
  lifecycle.run("setCheckpoints", { scriptsDir, enabled, instance: t.instance, env: { CONSTRUCT_CHECKPOINT_RESULT: resultFile } });
  const startedAt = Date.now();
  const timer = setInterval(() => {
    let res = null;
    try { res = fs.readFileSync(resultFile, "utf8").trim(); } catch (_) { /* not written yet */ }
    if (res === "ok" || res === "fail") {
      clearInterval(timer);
      try { fs.unlinkSync(resultFile); } catch (_) {}
      logLine(`checkpoints: result=${res}`);
      if (res === "ok") {
        // Only a CONFIRMED run updates the marker; a failure leaves it stale-but-honest
        // so the next save offers again.
        try { host.saveAppliedAutoCheckpoints(scriptsDir, enabled); } catch (e) { logLine(`checkpoints: marker write failed — ${e && e.message ? e.message : e}`); }
        vscode.window.showInformationMessage(`Automatic checkpoints are now ${enabled ? "on" : "off"} on the Construct VM.`);
      } else {
        vscode.window.showWarningMessage("Changing automatic checkpoints didn't complete — see the console window for the error.");
      }
    } else if (Date.now() - startedAt > 10 * 60 * 1000) {
      // Gave up waiting (a declined UAC never runs the script, so it never writes a
      // result). The marker stays unset, so the next save offers again — correct.
      clearInterval(timer);
      try { fs.unlinkSync(resultFile); } catch (_) {}
      logLine("checkpoints: timed out waiting for a result (UAC declined, or the console is still open)");
    }
  }, 1500);
}

/** Patch toggles are provisioning-only: saving persists them, then this offers
 *  the lifecycle action that applies them. No VM-side SSH mutation happens in
 *  the settings-save path. */
async function startConstructReprovision(scriptsDir, target) {
  try {
    // Bind to the instance the reprovision was ASKED for: the pre-flight and the
    // project probe below both await, and a switch in between must not redirect the
    // rebuild to another VM.
    const t = target || actionTarget();
    const pf = await lifecyclePreFlight("reprovisioning", t);
    if (!pf.ok) { showPreFlightBlock(pf); return false; }
    const selected = await effectiveProjects(t.instance);
    if (targetSuperseded(t, "Reprovision")) return false;
    lifecycle.run("reprovision", { scriptsDir, projects: selected, instance: t.instance });
    beginReprovisionFastRefresh();
    return true;
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't start reprovision: " + (e && e.message ? e.message : e));
    return false;
  }
}

function offerReprovisionForPatchSettings(scriptsDir, features) {
  const names = features.join(" and ");
  vscode.window.showWarningMessage(
    `Construct settings saved. Changing ${names} requires a reprovision before it takes effect.`,
    "Reprovision now",
  ).then((pick) => {
    if (pick !== "Reprovision now") return;
    void startConstructReprovision(scriptsDir);
  });
}

/** Power the VM off over SSH (root → systemctl poweroff). Confirms first; warns
 *  that an attached remote window will lose its connection. */
async function runShutdown() {
  // Powering a VM off is irreversible from here, and the modal below can sit open for
  // as long as the user likes — so the target is captured BEFORE it opens and verified
  // again after. Switching while the prompt is up means we can no longer tell which VM
  // the answer was about, so we do nothing and say so.
  const t = actionTarget();
  const connectedHere = (() => {
    try { return remote.isConnectedToVm(safeRemoteAuthority(), t.cfg); } catch (_) { return false; }
  })();
  const named = instanceLabel(t.instance);
  const detail = connectedHere
    ? "This window is connected to the VM over Remote-SSH, so its connection will drop when the VM powers off."
    : "The VM will power off. You can bring it back with “Start & connect”.";
  const pick = await vscode.window.showWarningMessage(
    `Shut down the Construct VM${named}?`, { modal: true, detail }, "Shut down"
  );
  if (pick !== "Shut down") return;
  if (targetSuperseded(t, "Shutdown")) return;
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Shutting down the Construct VM…", cancellable: false },
    async () => {
      const r = await ssh.runRemote(vmpower.SHUTDOWN_CMD, { timeoutMs: 20000, cfg: t.cfg });
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
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  const profile = projRoot ? host.readProjectProfile(projRoot, name) : null;
  remote.openOnVm({ path: remote.projectOpenPath(profile), newWindow: true, cfg: activeCfg() });
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
  // A clone can run for minutes. Bind the whole flow to ONE instance so the folder we
  // open afterwards is the VM the repo was actually cloned onto.
  const t = actionTarget();
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `Cloning ${name} onto the VM${instanceLabel(t.instance)}…`, cancellable: false },
    async () => {
      const r = await ssh.runRemoteScript(remote.buildCloneScript(url, name), { timeoutMs: 300000, cfg: t.cfg });
      if (r.code === 0) {
        vscode.window.showInformationMessage(`Cloned ${name} — opening it on the VM…`);
        remote.openOnVm({ path: dest, newWindow: true, cfg: t.cfg });
        refreshAll(); // the repo now exists on the VM
      } else if (r.code === 3) {
        const pick = await vscode.window.showWarningMessage(
          `${dest} already exists on the VM.`, "Open it", "Cancel"
        );
        if (pick === "Open it") remote.openOnVm({ path: dest, newWindow: true, cfg: t.cfg });
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
      const rawText = await usage.collectRaw({ report: usageReport, cfg: activeCfg() });
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
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  if (!projRoot) { warnNoScriptsDir(); return; }
  const dir = host.projectsDir(projRoot);
  try { fs.mkdirSync(dir, { recursive: true }); } catch (_) {}
  vscode.commands.executeCommand("revealFileInOS", vscode.Uri.file(dir));
}

/**
 * Non-interactive import of VM repos: scan the checked-out repos over SSH and
 * write a minimal profile for each one not already covered by a local profile.
 * Merges, never overwrites (same rule as planImport). Returns
 * `{ imported: string[], failed: string[], skipped: string[] }` or null when the
 * VM is unreachable or the scan fails. Never shows toasts — the caller decides
 * how to surface the result (the sync tick logs it; the reinstall gate checks it).
 *
 * Auto-selects newly imported profiles into the persisted selection so they are
 * included in the next reprovision/reinstall (same semantics as
 * autoEnableNewProfiles for sync-discovered profiles).
 */
async function importFromVm() {
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  if (!projRoot) return null;
  var r;
  try { r = await ssh.runRemoteScript(projects.buildScanScript(), { timeoutMs: 60000, cfg: activeCfg() }); }
  catch (_) { return null; }
  if (!r || r.code !== 0) return null;
  var scan = projects.parseScan(r.stdout);
  if (scan == null) return null;
  var existing = {};
  for (var name of host.listProjectProfiles(projRoot)) {
    var p = host.readProjectProfile(projRoot, name);
    if (p) existing[name] = p;
  }
  var profilesBefore = host.listProjectProfiles(projRoot);
  var deleted = { names: new Set(), urls: new Set() };
  try {
    var git = await detectGitCached();
    if (git.present && runGit && resolveCfgDir()) {
      deleted = await configsync.deletedProfileIdentities(runGit, resolveCfgDir(), profilesBefore);
    }
  } catch (e) {
    logLine("auto-import: could not read deletion history: " + (e && e.message ? e.message : e));
  }
  var plan = projects.planImport(scan, existing, {
    ignoredNames: deleted.names,
    ignoredUrls: deleted.urls,
  });
  var imported = [];
  var failed = [];
  var raceSkipped = [];
  for (var item of plan.toWrite) {
    try {
      var created = host.writeProjectProfileIfAbsent(projRoot, item.name, item.profile);
      if (created) imported.push(item.name);
      else raceSkipped.push(item.name);
    } catch (_) { failed.push(item.name); }
  }
  lastAutoImportAt = Date.now();
  if (imported.length) {
    logLine("auto-import from VM: imported " + imported.join(", "));
    await autoEnableNewProfiles(profilesBefore, host.listProjectProfiles(projRoot));
  }
  if (raceSkipped.length) logLine("auto-import: skipped (already exist): " + raceSkipped.join(", "));
  if (failed.length) logLine("auto-import: write failures: " + failed.join(", "));
  return { imported: imported, failed: failed, skipped: plan.skipped.concat(raceSkipped) };
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
  hostAudioInstance = activeInstance().name;
  hostAudio = new audio.HostAudio({
    // The mic tunnel is per-INSTANCE: the reverse forward lands on the VM this window
    // currently drives. The VM-side port range (8767+) is per VM, so two instances
    // never contend for it; only the host-side cfg has to follow the switch.
    cfg: activeCfg(),
    mic: makeMicProvider(),
    onStatus: (s) => broadcastAudio(s),
  });
  const handle = (r) => {
    if (!r.ok) {
      // Reset the switch to off on every surface.
      hostAudio = undefined;
      hostAudioInstance = null;
      safePost(webview, { type: "audio", enabled: false, capturing: false });
      broadcastAudio({ enabled: false, capturing: false });
      if (opts.auto) return; // best-effort startup arm: stay silent, the switch shows off
      const why = {
        unreachable: "Couldn't reach the VM. Is it running?",
        "enable-failed": "The VM couldn't install the recorder shim / patch.",
        "server-failed": "Couldn't open the local audio port.",
        "tunnel-failed": "Couldn't open the SSH tunnel to the VM.",
        "no-free-port": "Every Construct audio tunnel port is already in use by other VS Code windows.",
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
    if (!(await ssh.isReachable({ timeoutMs: 6000, cfg: activeCfg() }))) return; // VM down — stay off silently
    enableAudio(context, undefined, { auto: true });
  } catch (_) { /* best-effort: never block activation */ }
}

/** Delay (ms) before the startup patch-verification pass runs. VS Code auto-updates
 *  the claude-code extension in the background right after a start, so we wait out a
 *  window (default 20s, tunable via construct.repatchDelaySeconds) to let that land
 *  before probing — otherwise we'd re-patch the OLD build the update is about to
 *  replace. 0 disables the pass entirely. */
function repatchDelayMs() {
  let secs = 20;
  try {
    const v = vscode.workspace.getConfiguration("construct").get("repatchDelaySeconds");
    if (typeof v === "number" && Number.isFinite(v)) secs = v;
  } catch (_) {}
  if (!(secs > 0)) return 0;              // <=0 (or NaN coerced away) disables the pass
  return Math.min(Math.max(secs, 0), 600) * 1000; // clamp to a sane [0, 10min]
}

/** Arm the one-shot startup patch-verification pass. Best-effort and unref'd so it
 *  never keeps the host alive on its own; cancelled by deactivate(). */
function scheduleStartupRepatch(context) {
  const delay = repatchDelayMs();
  if (delay === 0) { logLine("repatch: startup verification disabled (construct.repatchDelaySeconds<=0)."); return; }
  logLine(`repatch: scheduling startup patch verification in ${Math.round(delay / 1000)}s.`);
  repatchTimer = setTimeout(() => {
    repatchTimer = null;
    verifyPatchesOnStartup(context).catch((e) => logLine("repatch: verification failed: " + (e && e.message ? e.message : e)));
  }, delay);
  if (repatchTimer && typeof repatchTimer.unref === "function") repatchTimer.unref();
}

/** Startup patch-verification pass: re-apply any Construct claude-code patch whose
 *  feature is ON but has reverted to stock (a VS Code-start auto-update wiped it).
 *
 *  Streaming is a pure VM-side file patch, so it's repaired directly over SSH.
 *  Mic passthrough also needs a live reverse tunnel: when one is up (auto-arm already
 *  ran) we just re-apply the gate over SSH; when it isn't, we retry the FULL mic
 *  enable, which installs the shim, patches the gate, AND opens the tunnel in one go.
 *  Quiet by design — like the mic auto-arm, a startup housekeeping pass shouldn't
 *  toast on every launch; everything is recorded to the Construct output channel. */
async function verifyPatchesOnStartup(context) {
  const scriptsDir = resolveScriptsDir();
  const raw = scriptsDir ? host.readRawSettings(scriptsDir) : {};
  // Streaming defaults ON (matches CLAUDE_PARTIAL_STREAMING:-true on the VM), so treat
  // anything other than an explicit false as on. Mic passthrough is opt-in.
  const streamingOn = raw.claudePartialStreaming !== false;
  const micOn = raw.micPassthrough === true;
  if (!streamingOn && !micOn) return;

  const micLive = !!(hostAudio && hostAudio.enabled);
  // Plan the branches purely (repatch.planStartupActions) so the "streaming off + mic
  // on + no tunnel" case can't slip through: that combination must still retry the
  // full auto-arm even though the SSH pass has nothing to do.
  const plan = repatch.planStartupActions({ streamingOn, micOn, micLive, hasHostAudio: !!hostAudio });

  // The SSH probe + gate/streaming repairs. Skipped entirely when there's nothing to
  // probe for (e.g. streaming off and no live tunnel to re-patch) — the auto-arm retry
  // below still runs and does its own reachability check.
  if (plan.runPass) {
    const res = await repatch.runStartupRepatch({
      ssh,
      cfg: activeCfg(),
      readVmScript: audio.defaultReadScript,
      streamingOn,
      // Only ask for a gate-only repair when a tunnel is already live; otherwise the
      // full auto-arm retry below both patches the gate and opens the tunnel.
      micOn: plan.passMicOn,
      buildMicEnableScript: () => {
        const enableText = audio.defaultReadScript("construct-audio-enable.sh");
        const shimText = audio.defaultReadScript("construct-rec-shim.sh");
        const vmPort = hostAudio ? hostAudio.vmPort : undefined;
        const vmCount = hostAudio ? hostAudio.vmPortCount : undefined;
        return audio.buildEnableScript(enableText, shimText, vmPort, vmCount);
      },
      log: logLine,
    });
    // A live gate re-patch means the console's mic substatus is authoritative again.
    if (res.repaired.mic && hostAudio) {
      hostAudio.gatePatched = true;
      broadcastAudio({ enabled: true, capturing: hostAudio.capturing, gatePatched: true });
    }
  }

  // Mic is wanted but there's no HostAudio at all (the VM was down at activate so the
  // auto-arm bailed, or its enable failed and cleared the instance): retry the full
  // enable. It installs the shim, patches the gate, and opens the tunnel in one go, and
  // does its OWN reachability check (silent if the VM is still down) — so this must NOT
  // be gated on the SSH pass, which may not have run. Guard on `!hostAudio` (from the
  // plan) so an enable that is still in flight is never clobbered.
  if (plan.retryAutoArm) {
    logLine("repatch: mic passthrough is on but no tunnel is live — retrying auto-arm.");
    maybeAutoEnableAudio(context);
  }
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
  // Profile listing comes from cfgDir (where profiles now live); the selection
  // storage (readSelectedProjects/saveSelectedProjects) stays in scriptsDir.
  const profileRoot = resolveCfgDir() || scriptsDir;
  const available = host.listProjectProfiles(profileRoot);
  if (!available.length) {
    vscode.window.showInformationMessage("No project profiles found. New repos are auto-discovered from the VM, or use \u201c+ add project\u201d to add one.");
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
  const safe = host.safeProfileName(name);
  if (!safe) { vscode.window.showErrorMessage("Invalid project name."); return; }
  // D11: reserved names (default, project.schema) cannot be edited.
  if (projects.isReservedProfileName(safe)) {
    vscode.window.showInformationMessage('"' + safe + '" is a reserved profile -- create a named profile instead.');
    return;
  }
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  if (!projRoot) { warnNoScriptsDir(); return; }
  const existing = host.readProjectProfile(projRoot, safe);
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
  const safe = host.safeProfileName(name);
  if (!safe) { vscode.window.showErrorMessage("Invalid project name."); return; }
  // D11: refuse reserved names with an information toast.
  if (projects.isReservedProfileName(safe)) {
    vscode.window.showInformationMessage('"' + safe + '" is reserved -- create a named profile instead.');
    return;
  }
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  if (!projRoot) { warnNoScriptsDir(); return; }
  const clean = projects.sanitizeProfile(safe, profileObj);
  if (!clean) { vscode.window.showErrorMessage("Couldn't save the project profile (invalid name)."); return; }
  try {
    host.writeProjectProfile(projRoot, safe, clean);
    vscode.window.showInformationMessage("Saved project \"" + safe + "\".");
    refreshAll();
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't save the project profile: " + (e && e.message ? e.message : e));
  }
}

/** Delete a host profile after explicit confirmation, prune it from the saved
 * selection, then sync the deletion to the VM. The checkout directory itself is
 * deliberately left alone; deletion history prevents auto-discovery from
 * recreating the profile just because that directory still exists. */
async function runDeleteProject(name) {
  const safe = host.safeProfileName(name);
  if (!safe) { vscode.window.showErrorMessage("Invalid project name."); return; }
  if (projects.isReservedProfileName(safe)) {
    vscode.window.showInformationMessage('"' + safe + '" is reserved and cannot be deleted.');
    return;
  }
  const projRoot = resolveCfgDir() || resolveScriptsDir();
  const scriptsDir = resolveScriptsDir();
  if (!projRoot || !scriptsDir) { warnNoScriptsDir(); return; }
  const pick = await vscode.window.showWarningMessage(
    'Delete project profile "' + safe + '"?',
    {
      modal: true,
      detail: "This removes the profile from Construct on the host and VM, and unselects it. The checked-out repository folder is not deleted.",
    },
    "Delete profile",
  );
  if (pick !== "Delete profile") return;
  try {
    host.deleteProjectProfile(projRoot, safe);
    const available = host.listProjectProfiles(projRoot);
    const selected = host.readSelectedProjects(scriptsDir).filter((n) => n !== safe);
    host.saveSelectedProjects(scriptsDir, projects.reconcileSelection(selected, available));
    const syncResult = await runConfigSync();
    if (!syncResult || syncResult.lockBusy || !syncResult.ok) {
      vscode.window.showWarningMessage(
        'Deleted profile "' + safe + '" locally; its VM deletion is pending the next successful config sync.'
      );
    } else {
      vscode.window.showInformationMessage('Deleted project profile "' + safe + '".');
    }
    refreshAll();
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't delete the project profile: " + (e && e.message ? e.message : e));
  }
}

/** Disable mic passthrough: stop capture + tunnel, revert the VM shim + patch. */
function disableAudio() {
  if (!hostAudio) { broadcastAudio({ enabled: false, capturing: false }); return; }
  const inst = hostAudio;
  hostAudio = undefined;
  hostAudioInstance = null;
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

// ── Switching the active instance ───────────────────────────────────────────
// Selection is PER WINDOW (workspaceState), so two windows can drive two VMs at once;
// `construct.instance` is a global override for people who want one answer everywhere.
// A switch must invalidate every VM-derived cache, retarget the long-lived connections
// (notification watcher, mic tunnel), and re-probe — otherwise the panel would show the
// previous VM's status pills, versions, projects and usage under the new name.

/** Update (or hide) the status-bar instance indicator. Hidden with exactly one
 *  instance, so a single-VM install's status bar is unchanged. */
function syncInstanceStatusItem() {
  try {
    if (!instanceStatusItem) return;
    const reg = registryNow();
    const all = instances.list(reg);
    if (all.length < 2) { instanceStatusItem.hide(); return; }
    const inst = activeInstance();
    instanceStatusItem.text = "$(vm) " + inst.name;
    instanceStatusItem.tooltip = `The Construct instance: ${inst.name} (${inst.vmHost}:${inst.sshPort}) — click to switch`;
    instanceStatusItem.show();
  } catch (_) { /* a status-bar item is never worth an exception */ }
}

/** Point this window at `name`. Persists the choice, retargets the live connections,
 *  and re-probes every surface. A no-op when the name is already active or unknown. */
async function switchInstance(name) {
  const reg = registryNow(true);
  const wanted = String(name || "").trim();
  if (!wanted || !reg.byName[wanted]) {
    vscode.window.showWarningMessage(`"${wanted}" is not a Construct instance in the registry.`);
    return;
  }
  if (activeInstance().name === wanted) return;
  try { await extensionContext.workspaceState.update(ACTIVE_INSTANCE_KEY, wanted); }
  catch (e) {
    // Without persistence the switch would silently revert on the next reload — say so.
    logLine("instances: could not persist the active instance — " + (e && e.message ? e.message : e));
    vscode.window.showWarningMessage("Switched to \"" + wanted + "\" for now, but the choice couldn't be saved for this window.");
  }
  const setting = instanceSetting();
  if (setting && setting !== wanted) {
    // Honesty: the setting outranks workspaceState, so the switch would silently not
    // take effect. Say so rather than leave the user staring at the old VM.
    vscode.window.showWarningMessage(
      `The "construct.instance" setting pins every window to "${setting}", so this window still uses it. Clear that setting to switch per window.`
    );
  }
  logLine(`instances: active instance -> ${activeInstance().name}`);
  await onInstanceChanged();
}

/** Re-target everything that holds a per-VM connection or cache, then re-render. */
async function onInstanceChanged() {
  const inst = activeInstance();   // also bumps instanceGate, invalidating live tokens
  // VM-derived caches: the probe, the update check's markers and the usage table are
  // all "about a VM", so serving the previous one's results would be a lie.
  inflightProbe = null;
  try { usage.clearCache(); } catch (_) {}
  gitDetected = null; gitDetectedAt = 0;
  cachedConfigSync = null;
  // The notification watcher is one long-lived SSH connection to ONE VM: reconnect it
  // to the new instance (its spool lives on that VM, so nothing is lost on the old one).
  if (notifyInstance !== inst.name) {
    stopNotifyWatch();
    if (notificationsEnabled()) startNotifyWatch();
  }
  // The mic tunnel likewise terminates on one VM. Drop it and let the saved preference
  // re-arm it against the new instance (quietly — same rules as the startup auto-arm).
  if (hostAudio && hostAudioInstance && hostAudioInstance !== inst.name) {
    disableAudio();
    maybeAutoEnableAudio(extensionContext);
  }
  syncInstanceStatusItem();
  await refreshAll();
}

/** The "The Construct: Switch Instance" QuickPick. */
async function runSwitchInstance() {
  const reg = registryNow(true);
  const all = instances.list(reg);
  const current = activeInstance().name;
  if (all.length < 2) {
    vscode.window.showInformationMessage(
      "Only one Construct instance is configured (" + current + "). Add more in " +
      (reg.path || "%LOCALAPPDATA%\\The-Construct\\instances.json") + "."
    );
    return;
  }
  const pick = await vscode.window.showQuickPick(
    all.map((i) => ({
      label: (i.name === current ? "$(check) " : "") + i.name,
      description: i.vmHost + (i.sshPort === 22 ? "" : ":" + i.sshPort),
      detail: i.backend + (i.name === reg.defaultInstance ? " · registry default" : ""),
      name: i.name,
    })),
    { title: "Switch the Construct instance", placeHolder: "The VM this window's panel drives" }
  );
  if (!pick) return;
  await switchInstance(pick.name);
}

/** When a window is attached over Remote-SSH to a VM that IS a known instance, adopt
 *  it: the panel must describe the machine you are actually working on. Only when the
 *  authority matches an instance and no explicit setting pins another one. */
async function adoptRemoteInstance() {
  try {
    const res = await instances.adoptRemoteInstance({
      registry: registryNow(),
      remoteAuthority: safeRemoteAuthority(),
      setting: instanceSetting(),
      currentName: activeInstance().name,
      // AWAITED inside the helper: workspaceState.update is a Thenable, and a
      // fire-and-forget write would let the rest of activation read the OLD selection
      // and probe the wrong VM.
      setActive: (name) => extensionContext.workspaceState.update(ACTIVE_INSTANCE_KEY, name),
    });
    if (res.adopt && res.persisted) {
      logLine(`instances: window is attached to this VM — active instance -> ${res.name}`);
      instanceGate.set(res.name);
    } else if (res.adopt && res.error) {
      logLine(`instances: could not adopt the attached VM's instance (${res.error})`);
    }
  } catch (_) { /* best-effort: never break activation */ }
}

/** " (work-vm)" for the instance an action targets, or "" when only one instance
 *  exists — so a single-VM install's confirmation copy is byte-identical to before,
 *  and a multi-VM one always names the machine it is about to touch. */
function instanceLabel(inst) {
  try {
    if (!inst || instances.list(registryNow()).length < 2) return "";
    return ` (${inst.name})`;
  } catch (_) { return ""; }
}

// A per-render CSP nonce: it is the sole gate between the trusted bundled script
// and any injected inline script, so it must come from a CSPRNG, not Math.random.
function getNonce() {
  return crypto.randomBytes(24).toString("base64");
}

/** The user's chosen UI design id, or null while undecided (renders the default). */
function currentThemeId() {
  try { return themes.normalizeThemeId(vscode.workspace.getConfiguration("construct").get("uiTheme")); }
  catch (_) { return null; }
}

/** Build the webview HTML for either surface from the shared template. Both
 *  surfaces share one markup + one controller; the selected design is ONLY the
 *  extra {{themeUri}} stylesheet layered after panel.css (see src/themes.js). */
function buildHtml(webview, extensionUri, htmlFile, scriptFile) {
  const mediaUri = (file) => webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file));
  const html = fs.readFileSync(path.join(extensionUri.fsPath, "media", htmlFile), "utf8");
  const nonce = getNonce();
  return html
    .replace(/{{cspSource}}/g, webview.cspSource)
    .replace(/{{styleUri}}/g, mediaUri("panel.css").toString())
    .replace(/{{themeUri}}/g, mediaUri(themes.cssFileFor(currentThemeId())).toString())
    .replace(/{{scriptUri}}/g, mediaUri(scriptFile).toString())
    .replace(/{{nonce}}/g, nonce);
}

/** Re-render every open surface with the current design (config-change hook).
 *  Setting webview.html reloads the document; the webview re-posts 'ready' and
 *  gets fresh state, so no data is lost beyond transient in-modal edits. */
function reapplyTheme(context) {
  const { extensionUri } = context;
  try {
    if (launcherView) launcherView.webview.html = buildHtml(launcherView.webview, extensionUri, "launcher.html", "launcher.js");
  } catch (_) { /* view may be disposed mid-flight */ }
  try {
    if (panel) panel.webview.html = buildHtml(panel.webview, extensionUri, "panel.html", "panel.js");
  } catch (_) { /* panel may be disposed mid-flight */ }
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

    case "setInstance":
      // The panel header's instance dropdown. The name is validated against the
      // registry inside switchInstance — the webview is untrusted input.
      void switchInstance(message.name);
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
        // Snapshot the previous state BEFORE the write so live T3 changes and
        // provisioning-only patch prompts key on transitions, not absolute values.
        const prev = host.readSettings(scriptsDir);
        const merged = host.mapToForm(host.saveSettings(scriptsDir, message.settings));
        const patchChanges = host.patchReprovisionChanges(prev, merged);
        if (patchChanges.length) offerReprovisionForPatchSettings(scriptsDir, patchChanges);
        else vscode.window.showInformationMessage("Construct settings saved.");
        pushSettings(webview); // reflect the normalized, merged on-disk state
        // The "Microphone passthrough" toggle is a live preference: honor it now, not
        // just on next startup, so changing the setting actually does something.
        const wantMic = message.settings && message.settings.mic === true;
        const micOn = !!(hostAudio && hostAudio.enabled);
        if (wantMic && !micOn) enableAudio(context, webview);
        else if (!wantMic && micOn) disableAudio();
        // Stock T3 enable/channel changes are live. A source-managed enable or
        // channel change waits for the shared server/Desktop reprovision offered
        // above, so npm can never replace only one end; disabling remains live.
        const wantT3 = message.settings && message.settings.t3code === true;
        const hadT3 = prev.t3code === true;
        // The effective channel comes from the MERGED on-disk result, not the raw
        // payload: an omitted or invalid t3codeChannel in the payload must not
        // override a stored nightly preference, and mapFromForm already rejects
        // unknown values (so the old disk value survives).
        const newCh = merged.t3codeChannel || "stable";
        const oldCh = prev.t3codeChannel || "stable";
        const t3plan = t3code.planT3LiveAction(wantT3, hadT3, newCh, oldCh);
        if (t3plan) {
          const sourceManagedT3 = merged.t3codeLimitResume === true;
          if (t3plan.action === "enable" && !sourceManagedT3) {
            t3code.enableOnVm({ channel: t3plan.channel, cfg: activeCfg() }).then(() => refreshAll());
          } else if (t3plan.action === "disable") {
            t3code.disableOnVm({ cfg: activeCfg() }).then(() => refreshAll());
          } else if (t3plan.action === "setChannel" && !sourceManagedT3) {
            t3code.setChannelOnVm(t3plan.channel, { cfg: activeCfg() }).then(() => refreshAll());
          }
        }
        // Automatic checkpoints are a HYPER-V property, decided when the VM is
        // created — so the saved value rides the next reinstall/redownload for free.
        // Offer to apply it to the VM that exists right now too (elevated, UAC).
        //
        // What to offer (and whether to offer at all) is decided by the pure
        // vmpower.planCheckpointOffer — see its doc for why the value must come from
        // the MERGED on-disk result and not from the raw payload.
        const chkPlan = vmpower.planCheckpointOffer(message.settings, prev, merged);
        if (chkPlan.act) {
          offerApplyCheckpoints(scriptsDir, chkPlan.enabled, chkPlan.changed)
            .catch((err) => logLine(`checkpoints: ${err && err.message ? err.message : err}`));
        }
      } catch (e) {
        vscode.window.showErrorMessage("Couldn't save Construct settings: " + (e && e.message ? e.message : e));
      }
      return;
    }

    case "customRebuild": {
      const scriptsDir = resolveScriptsDir();
      if (!scriptsDir) { warnNoScriptsDir(); return; }
      const action = message.mode === "redownload" ? "redownload" : "reinstall";
      const rebuildTarget = actionTarget();
      (async () => {
        var pf = await lifecyclePreFlight(action === "redownload" ? "redownloading" : "reinstalling", rebuildTarget);
        if (!pf.ok) { showPreFlightBlock(pf); return; }
        // A rebuild DELETES a VM, so it must land on the instance the button was
        // pressed for — never on whichever one the window switched to meanwhile.
        const projects = await effectiveProjects(rebuildTarget.instance);
        if (targetSuperseded(rebuildTarget, action === "redownload" ? "Redownload" : "Reinstall")) return;
        lifecycle.run(action, { scriptsDir: scriptsDir, backupMode: message.backup, projects: projects, instance: rebuildTarget.instance });
      })();
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
      if (id === "selectProfiles") { runSelectProfiles(); return; }
      if (id === "editProject") { runEditProject(message.project, webview); return; }
      if (id === "deleteProject") { void runDeleteProject(message.project); return; }
      if (id === "exportUsage") { runExportUsage(); return; }
      if (id === "updateAgents") { runUpdateAgents(); return; }
      if (id === "updateAgent") {
        // Per-agent ↑ tag. Validate against the known ids — the webview is
        // untrusted input and this string reaches a remote shell script builder.
        const known = ["claude-code", "codex", "opencode", "t3code"];
        if (known.includes(message.agent)) runUpdateAgents([message.agent]);
        return;
      }
      if (id === "openAgentWeb") {
        // The agents-list ▷ button: only T3 Code has a browser UI today. Mints a
        // one-time pairing link over SSH and opens it in the host browser.
        if (message.agent === "t3code") t3code.openWebUi({ cfg: activeCfg() });
        return;
      }
      if (id === "connect") { remote.openOnVm({ path: "/root/repos", newWindow: false, cfg: activeCfg() }); return; }
      if (id === "startConnect") { runStartAndConnect(); return; }
      if (id === "shutdown") { runShutdown(); return; }
      if (id === "exportConfig") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        lifecycle.run(id, { scriptsDir, instance: actionTarget().instance }); // export doesn't touch project selection
        return;
      }
      if (id === "reprovision" || id === "reinstall" || id === "redownload") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        const lifeTarget = actionTarget();
        (async () => {
          var pf = await lifecyclePreFlight(id === "reprovision" ? "reprovisioning" : id === "reinstall" ? "reinstalling" : "redownloading", lifeTarget);
          if (!pf.ok) { showPreFlightBlock(pf); return; }
          const projects = await effectiveProjects(lifeTarget.instance);
          if (targetSuperseded(lifeTarget, id === "reprovision" ? "Reprovision" : id === "reinstall" ? "Reinstall" : "Redownload")) return;
          lifecycle.run(id, { scriptsDir: scriptsDir, projects: projects, instance: lifeTarget.instance });
          if (id === "reprovision") beginReprovisionFastRefresh();
        })();
        return;
      }
      if (id === "updateConstruct") { runUpdateConstruct(); return; }
      // ── Config-sync commands (C6) ─────────────────────────────────────
      if (id === "syncConfigNow") {
        (async function () {
          try {
            await runConfigSync();
            // Always run import after sync (or instead of it for no-git hosts),
            // bypassing the throttle. Coalesced: joins in-flight scan if one exists.
            await coalescedImport(true);
          } catch (e) {
            vscode.window.showErrorMessage("Config sync failed: " + (e && e.message ? e.message : e));
          }
          cachedConfigSync = await buildConfigSyncState();
          refreshAll();
        })();
        return;
      }
      if (id === "addConfigRemote") {
        vscode.window.showInputBox({
          title: "Add a remote config repository",
          prompt: "Git URL of the remote config repo",
          placeHolder: "https://github.com/org/construct-config.git",
          ignoreFocusOut: true,
          validateInput: function (v) { return remote.isLikelyGitUrl(v) ? null : "Enter an https://, ssh:// or git@host:path git URL."; },
        }).then(function (url) {
          if (!url) return;
          var dir = resolveCfgDir();
          if (!dir) { warnNoScriptsDir(); return; }
          var existing = configsync.readRemotes(dir);
          if (existing.some(function (r) { return r.url === url.trim(); })) {
            vscode.window.showInformationMessage("That remote is already linked.");
            return;
          }
          existing.push({ url: url.trim() });
          configsync.writeRemotes(dir, existing);
          detectGitCached().then(function (git) {
            if (git.present && runGit) {
              configsync.ensureStagingClone(runGit, configsync.stagingRoot(process.env), url.trim()).catch(function () {});
            }
          });
          vscode.window.showInformationMessage("Remote config repo added: " + url.trim());
          buildConfigSyncState().then(function (cs) { cachedConfigSync = cs; refreshAll(); });
        });
        return;
      }
      if (id === "removeConfigRemote") {
        var rmUrl = message.url;
        if (!rmUrl) return;
        vscode.window.showWarningMessage("Remove the remote config repo?\n" + rmUrl, { modal: true }, "Remove").then(function (pick) {
          if (pick !== "Remove") return;
          var dir = resolveCfgDir();
          if (!dir) return;
          var existing = configsync.readRemotes(dir);
          configsync.writeRemotes(dir, existing.filter(function (r) { return r.url !== rmUrl; }));
          buildConfigSyncState().then(function (cs) { cachedConfigSync = cs; refreshAll(); });
        });
        return;
      }
      if (id === "importRemoteConfigs") {
        (async () => {
          var dir = resolveCfgDir();
          if (!dir) { warnNoScriptsDir(); return; }
          var git = await detectGitCached();
          if (!git.present) { vscode.window.showWarningMessage("Git is not available. Install git first."); return; }
          var remotes = configsync.readRemotes(dir);
          if (!remotes.length) { vscode.window.showInformationMessage("No remote config repos linked yet. Add one first."); return; }
          var staging = configsync.stagingRoot(process.env);
          var allItems = [];
          for (var ri = 0; ri < remotes.length; ri++) {
            var clone = await configsync.ensureStagingClone(runGit, staging, remotes[ri].url);
            if (!clone.ok) continue;
            var candidates = configsync.listImportCandidates(clone.dir);
            for (var ci = 0; ci < candidates.length; ci++) {
              allItems.push({ label: candidates[ci].name + " -- " + remotes[ri].url, remoteUrl: remotes[ri].url, relPath: candidates[ci].relPath, name: candidates[ci].name, dir: clone.dir });
            }
          }
          if (!allItems.length) { vscode.window.showInformationMessage("No importable project profiles found in the linked remote repos."); return; }
          var picks = await vscode.window.showQuickPick(
            allItems.map(function (item) { return { label: item.label, item: item }; }),
            { canPickMany: true, title: "Import remote config profiles", placeHolder: "Select profiles to import (none pre-selected)" }
          );
          if (!picks || !picks.length) return;
          var selected = [];
          for (var pi = 0; pi < picks.length; pi++) {
            var item = picks[pi].item;
            try {
              var content = fs.readFileSync(path.join(item.dir, item.relPath), "utf8");
              selected.push({ remoteUrl: item.remoteUrl, ref: "HEAD", relPath: item.relPath, name: item.name, content: content });
            } catch (_) {}
          }
          if (!selected.length) return;
          var manifest = configsync.readImportManifest(dir);
          var existingNames = new Set(host.listProjectProfiles(dir));
          var plan = configsync.planUpstreamImport({ selected: selected, manifest: manifest, existingNames: existingNames });
          var imported = 0;
          // creates
          for (var ci2 = 0; ci2 < (plan.creates || []).length; ci2++) {
            var c = plan.creates[ci2];
            try {
              var parsed = JSON.parse(c.content);
              var canonical = projects.canonicalProfileJson(c.name, parsed);
              if (canonical) {
                fs.mkdirSync(path.join(dir, "projects"), { recursive: true });
                fs.writeFileSync(path.join(dir, "projects", c.name + ".json"), canonical, "utf8");
              }
              if (c.manifestEntry) {
                fs.mkdirSync(path.join(dir, "manifest"), { recursive: true });
                fs.writeFileSync(path.join(dir, "manifest", c.name + ".json"), JSON.stringify(c.manifestEntry, null, 2) + "\n", "utf8");
              }
              fs.mkdirSync(path.join(dir, "bases"), { recursive: true });
              fs.writeFileSync(path.join(dir, "bases", c.name + ".json"), c.content, "utf8");
              imported++;
            } catch (_) {}
          }
          // updates (3-way merge)
          for (var ui = 0; ui < (plan.updates || []).length; ui++) {
            var u = plan.updates[ui];
            try {
              var oursC = ""; try { oursC = fs.readFileSync(path.join(dir, "projects", u.name + ".json"), "utf8"); } catch (_) {}
              var baseC = ""; try { baseC = fs.readFileSync(path.join(dir, "bases", u.name + ".json"), "utf8"); } catch (_) {}
              var mergeResult = await configsync.mergeFile(runGit, { ours: oursC, base: baseC, theirs: u.theirsContent || "" });
              if (mergeResult.conflict) { vscode.window.showWarningMessage("Merge conflict for \"" + u.name + "\" -- keeping local version."); continue; }
              if (mergeResult.ok && mergeResult.content != null) {
                var mp = JSON.parse(mergeResult.content);
                var v = projects.validateProfile(u.name, mp);
                if (!v.ok) { vscode.window.showWarningMessage("Merged \"" + u.name + "\" is invalid -- keeping local version."); continue; }
                var mc = projects.canonicalProfileJson(u.name, mp);
                if (mc) fs.writeFileSync(path.join(dir, "projects", u.name + ".json"), mc, "utf8");
                if (u.manifestEntry) fs.writeFileSync(path.join(dir, "manifest", u.name + ".json"), JSON.stringify(u.manifestEntry, null, 2) + "\n", "utf8");
                fs.writeFileSync(path.join(dir, "bases", u.name + ".json"), u.theirsContent || "", "utf8");
                imported++;
              }
            } catch (_) {}
          }
          // collisions -- rename prompts. A renamed import is a full first-class
          // import: the target name must be safe, non-reserved and NOT already
          // taken (re-prompt otherwise, so one profile can't silently overwrite
          // another), and it gets the same provenance treatment as a create —
          // canonical profile + manifest (preserving remoteUrl/ref/pathInRemote,
          // importedAs=<newName>) + stored base — so it is tracked (shareable via
          // the remote command, pushable, and 3-way-updatable on the next import).
          // The decision core is the pure importui.planRenamedImport (unit-tested).
          var takenNames = new Set(host.listProjectProfiles(dir));
          var REJECT_MSG = {
            reserved: "is reserved. Choose another name.",
            unsafe: "is not a valid profile name (no path separators or \"..\").",
            taken: "already exists. Choose another name.",
            invalid: "is not a valid profile; skipped.",
            unparseable: "could not be read; skipped.",
          };
          for (var coi = 0; coi < (plan.collisions || []).length; coi++) {
            var col = plan.collisions[coi];
            var orig = selected.find(function (s) { return s.name === col.name; });
            if (!orig) continue;
            var suggestion = col.suggested || (col.name + "-2");
            var accepted = false;
            while (!accepted) {
              var newNameRaw = await vscode.window.showInputBox({
                title: "Name collision: \"" + col.name + "\" already exists",
                prompt: "Enter a new name for the imported profile (or leave empty to skip)",
                value: suggestion,
                ignoreFocusOut: true,
              });
              if (!newNameRaw || !newNameRaw.trim()) break; // skip this file
              var rp = importui.planRenamedImport(newNameRaw, orig, takenNames);
              if (!rp.ok) {
                // empty was handled above; only re-promptable/terminal errors here.
                if (rp.error === "unparseable" || rp.error === "invalid") {
                  vscode.window.showWarningMessage("\"" + col.name + "\" " + REJECT_MSG[rp.error]);
                  break;
                }
                vscode.window.showWarningMessage("\"" + newNameRaw.trim() + "\" " + (REJECT_MSG[rp.error] || "is not allowed."));
                continue;
              }
              try {
                fs.mkdirSync(path.join(dir, "projects"), { recursive: true });
                fs.writeFileSync(path.join(dir, "projects", rp.name + ".json"), rp.profileJson, "utf8");
                fs.mkdirSync(path.join(dir, "manifest"), { recursive: true });
                fs.writeFileSync(path.join(dir, "manifest", rp.name + ".json"), JSON.stringify(rp.manifestEntry, null, 2) + "\n", "utf8");
                fs.mkdirSync(path.join(dir, "bases"), { recursive: true });
                fs.writeFileSync(path.join(dir, "bases", rp.name + ".json"), rp.baseContent, "utf8");
                takenNames.add(rp.name);
                imported++;
                accepted = true;
              } catch (_) { break; }
            }
          }
          if (imported > 0) {
            var remoteUrl = selected[0] ? selected[0].remoteUrl : "remote";
            await configsync.commitAll(runGit, dir, "import from " + remoteUrl);
            await runConfigSync();
          }
          vscode.window.showInformationMessage("Imported " + imported + " profile(s) from remote config repos.");
          refreshAll();
        })().catch(function (e) { vscode.window.showErrorMessage("Import failed: " + (e && e.message ? e.message : e)); });
        return;
      }
      if (id === "shareConfigs") {
        (async () => {
          var dir = resolveCfgDir();
          if (!dir) { warnNoScriptsDir(); return; }
          var available = host.listProjectProfiles(dir).filter(function (n) { return !projects.isReservedProfileName(n); });
          if (!available.length) { vscode.window.showInformationMessage("No shareable project profiles found."); return; }
          var picks = await vscode.window.showQuickPick(
            available.map(function (n) { return { label: n, picked: false }; }),
            { canPickMany: true, title: "Share project profiles", placeHolder: "Select profiles to share" }
          );
          if (!picks || !picks.length) return;
          var names = picks.map(function (p) { return p.label; });
          var manifest = configsync.readImportManifest(dir);
          var remoteUrls = new Set();
          var allTracked = true;
          for (var ni = 0; ni < names.length; ni++) {
            if (manifest[names[ni]] && manifest[names[ni]].remoteUrl) remoteUrls.add(manifest[names[ni]].remoteUrl);
            else allTracked = false;
          }
          if (allTracked && remoteUrls.size === 1) {
            var url = [...remoteUrls][0];
            // D18: include -Repo/-Ref when the user has non-default values configured,
            // matching the C4 contract signature buildShareCommand({configRepoUrl, names, installRepo, installRef}).
            var scScriptsDir = resolveScriptsDir();
            var scRawSettings = scScriptsDir ? host.readRawSettings(scScriptsDir) : {};
            var scInstallRepo = scRawSettings.constructRepo || projects.DEFAULT_INSTALL_REPO;
            var scInstallRef = scRawSettings.constructRef || projects.DEFAULT_INSTALL_REF;
            var cmd = projects.buildShareCommand({ configRepoUrl: url, names: names, installRepo: scInstallRepo, installRef: scInstallRef });
            await vscode.env.clipboard.writeText(cmd);
            vscode.window.showInformationMessage("Share command copied to clipboard.");
          } else {
            var scriptsDir = resolveScriptsDir();
            var rawSettings = scriptsDir ? host.readRawSettings(scriptsDir) : {};
            var installRepo = rawSettings.constructRepo || projects.DEFAULT_INSTALL_REPO;
            var installRef = rawSettings.constructRef || projects.DEFAULT_INSTALL_REF;
            var entries = [{ path: "deploy.ps1", data: projects.buildDeployPs1({ installRepo: installRepo, installRef: installRef }) }];
            for (var ei = 0; ei < names.length; ei++) {
              var profile = host.readProjectProfile(dir, names[ei]);
              if (profile) {
                var canonical = projects.canonicalProfileJson(names[ei], profile);
                if (canonical) entries.push({ path: "projects/" + names[ei] + ".json", data: canonical });
              }
            }
            var buf = zip.buildZip(entries);
            var uri = await vscode.window.showSaveDialog({
              title: "Save shared config bundle",
              filters: { "Zip archive": ["zip"] },
              defaultUri: vscode.Uri.file(path.join(os.homedir(), "construct-config.zip")),
            });
            if (!uri) return;
            await fs.promises.writeFile(uri.fsPath, buf);
            vscode.window.showInformationMessage("Config bundle saved to " + uri.fsPath);
          }
        })().catch(function (e) { vscode.window.showErrorMessage("Could not create the share bundle: " + (e && e.message ? e.message : e)); });
        return;
      }
      if (id === "pushConfigUpstream") {
        var pushUrl = message.url;
        if (!pushUrl) return;
        vscode.window.showWarningMessage(
          "This commits your local versions of the files imported from " + pushUrl + " to a new branch and pushes.",
          { modal: true }, "Push"
        ).then(function (pick) {
          if (pick !== "Push") return;
          var now = new Date();
          var pad = function (n) { return String(n).padStart(2, "0"); };
          var branch = "construct-config-update-" + now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + "-" + pad(now.getHours()) + pad(now.getMinutes());
          // D19: gather the local versions of profiles tracked to this remote from
          // the import manifest — each entry whose remoteUrl matches pushUrl becomes
          // an {absSource, pathInRemote} pair so the staging clone receives real content.
          var puDir = resolveCfgDir();
          var puManifest = puDir ? configsync.readImportManifest(puDir) : {};
          var puFiles = [];
          var puNames = Object.keys(puManifest);
          for (var pi = 0; pi < puNames.length; pi++) {
            var puEntry = puManifest[puNames[pi]];
            if (puEntry && puEntry.remoteUrl === pushUrl) {
              puFiles.push({ absSource: path.join(puDir, "projects", puNames[pi] + ".json"), pathInRemote: puEntry.pathInRemote });
            }
          }
          configsync.pushUpstream(runGit, {
            stagingDir: path.join(configsync.stagingRoot(process.env), configsync.remoteSlug(pushUrl)),
            files: puFiles, branch: branch,
            message: "config update from The Construct (" + branch + ")",
          }).then(function (result) {
            if (result.ok) vscode.window.showInformationMessage("Pushed to branch \"" + result.branch + "\" -- create a PR from that branch.");
            else vscode.window.showErrorMessage("Push failed: " + (result.output || "").slice(0, 200));
          }).catch(function (e) { vscode.window.showErrorMessage("Push failed: " + (e && e.message ? e.message : e)); });
        });
        return;
      }
      if (id === "installGit") {
        if (process.platform !== "win32") {
          vscode.window.showWarningMessage("Git installation runs on the Windows host, which isn't available here.");
          return;
        }
        try {
          var igCmd = "winget install --id Git.Git -e --source winget";
          var igEncoded = Buffer.from(igCmd, "utf16le").toString("base64");
          var igCp = require("child_process");
          igCp.spawn("cmd.exe", ["/c", "start", "", "powershell.exe", "-EncodedCommand", igEncoded], { detached: true, stdio: "ignore" });
          vscode.window.showInformationMessage("Installing git -- approve any prompts in the console window. Restart VS Code after it finishes.");
          gitDetected = null; gitDetectedAt = 0;
        } catch (e) { vscode.window.showErrorMessage("Could not launch the git installer: " + (e && e.message ? e.message : e)); }
        return;
      }
      if (id === "openConfigRepo") {
        var ocDir = resolveCfgDir();
        if (!ocDir) { warnNoScriptsDir(); return; }
        (async () => {
          try {
            var gate = await configMergeGate();
            if (!gate.blocked && gate.dir) {
              cachedConfigSync = await buildConfigSyncState();
              refreshAll();
            }
          } catch (_) {}
          vscode.commands.executeCommand("vscode.openFolder", vscode.Uri.file(ocDir), true);
        })();
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
    launcherView = webviewView;
    liveWebviews.add(webviewView.webview);
    syncAutoRefresh();
    this.context.subscriptions.push(webviewView.onDidDispose(() => {
      if (launcherView === webviewView) launcherView = undefined;
      liveWebviews.delete(webviewView.webview);
      syncAutoRefresh();
    }));
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

// ── UI design (theme) picker ─────────────────────────────────────────────────
// A design changes ONLY the stylesheet layered after panel.css (src/themes.js);
// markup and controller logic are shared, so features can never fork per design.
// The picker is ALWAYS user-initiated (The Construct: Choose UI Design) — a fresh
// install silently gets themes.DEFAULT_THEME instead of a first-run prompt.

/** Open (or reveal) the design-picker webview: preview cards, one per design. */
function openThemePicker(context) {
  if (themePicker) {
    try { themePicker.reveal(); return; } catch (_) { themePicker = undefined; }
  }
  const { extensionUri } = context;
  const p = vscode.window.createWebviewPanel(
    "construct.themePicker",
    "The Construct — choose a design",
    vscode.ViewColumn.Active,
    webviewOptions(extensionUri)
  );
  themePicker = p;
  p.iconPath = vscode.Uri.joinPath(extensionUri, "media", "icon.svg");
  const mediaUri = (file) => p.webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, "media", file)).toString();
  p.webview.html = themes.buildPickerHtml({
    cspSource: p.webview.cspSource,
    nonce: getNonce(),
    cards: themes.THEMES.map((t) => ({ ...t, previewUri: mediaUri(themes.previewFileFor(t.id)) })),
  });
  // One-shot: the config write is async, so without a guard a double-click (or a
  // second card) would fire two updates -> two re-renders + two toasts.
  let picked = false;
  p.webview.onDidReceiveMessage((m) => {
    if (!m || m.type !== "pickTheme" || picked) return;
    const id = themes.normalizeThemeId(m.id);
    if (!id) return;
    picked = true;
    const entry = themes.THEMES.find((t) => t.id === id);
    // Persist globally; the onDidChangeConfiguration listener re-renders the
    // open surfaces. Close the picker only after the write really landed.
    vscode.workspace.getConfiguration("construct").update("uiTheme", id, vscode.ConfigurationTarget.Global).then(
      () => {
        vscode.window.showInformationMessage(`The Construct now wears "${entry ? entry.label : id}". Change it anytime: The Construct: Choose UI Design.`);
        try { p.dispose(); } catch (_) {}
      },
      (e) => {
        picked = false; // let the user retry after a failed write
        vscode.window.showErrorMessage("Couldn't save the design choice: " + (e && e.message ? e.message : e));
      }
    );
  });
  p.onDidDispose(() => { if (themePicker === p) themePicker = undefined; });
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

// activate is ASYNC so the Remote-SSH adoption below can be AWAITED: workspaceState
// .update() is a Thenable, and every instance-dependent step that follows (the status
// bar, the auto-open, the mic auto-arm, the notification watcher, the config-sync
// bootstrap) must see the adopted selection, not the one it replaced. VS Code waits on
// the returned promise before treating the extension as active.
async function activate(context) {
  extensionContext = context;
  // Route lifecycle/update launch logging into the Construct Output channel, and let
  // `construct.debug` keep launched consoles open so errors are readable.
  lifecycle.configure({ log: logLine, isDebug: debugEnabled });
  // A window attached to a known instance's VM adopts it BEFORE anything probes, so
  // the first push already describes the machine this window is working on.
  await adoptRemoteInstance();
  logLine(`activate: remoteAuthority=${safeRemoteAuthority() || "(local)"} debug=${debugEnabled()} instance=${activeInstance().name}`);
  // Status-bar instance indicator: created always, SHOWN only when more than one
  // instance exists (syncInstanceStatusItem), so a single-VM install sees no new UI.
  instanceStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  instanceStatusItem.command = "construct.switchInstance";
  context.subscriptions.push(instanceStatusItem);
  syncInstanceStatusItem();
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", new ConstructViewProvider(context), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.commands.registerCommand("construct.openPanel", () => openPanel(context)),
    vscode.commands.registerCommand("construct.refresh", () => refreshAll()),
    vscode.commands.registerCommand("construct.showLogs", () => showLogs()),
    vscode.commands.registerCommand("construct.chooseTheme", () => openThemePicker(context)),
    vscode.commands.registerCommand("construct.switchInstance", () => runSwitchInstance()),
    // Clicking a VM notification's toast opens the control panel: Windows launches
    // the toast's vscode:// URI, which lands here. Data-free by design — the URI is
    // fixed in src/notify.js, so nothing VM-authored ever reaches this handler.
    vscode.window.registerUriHandler({ handleUri() { openPanel(context); } }),
    vscode.workspace.onDidChangeConfiguration((e) => {
      // Live-swap the design when construct.uiTheme changes (picker, settings UI,
      // or a synced settings.json edit) — re-render both surfaces in place.
      if (e.affectsConfiguration("construct.uiTheme")) reapplyTheme(context);
      // Open or tear down the notification watcher when it's switched on/off.
      if (e.affectsConfiguration("construct.notifications")) {
        stopNotifyWatch();
        if (notificationsEnabled()) startNotifyWatch();
      }
      // The global instance pin changed: re-resolve and retarget everything.
      if (e.affectsConfiguration("construct.instance")) {
        registryNow(true);
        void onInstanceChanged();
      }
    })
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
  maybeAutoEnableAudio(context);
  // Notification watcher: independent of any open dashboard, so an agent can reach
  // the user who never opened the panel. Delayed slightly so the SSH connect doesn't
  // compete with startup work.
  setTimeout(() => { if (!notifyStopped) startNotifyWatch(); }, 3000);
  // A short while after start, re-apply any claude-code patch (streaming / mic gate)
  // that a background extension auto-update reverted — patches are otherwise only
  // applied at provision time. Delayed so the update has landed first (see repatch.js).
  scheduleStartupRepatch(context);
  // Config-sync engine bootstrap (D8).
  try {
    cfgDir = host.configDir(process.env) || null;
    if (cfgDir) {
      runGit = configsync.makeGitRunner({ spawn: require("child_process").spawn });
      configsync.ensureConfigTree(cfgDir);
      startConfigWatcher();
    }
  } catch (_) {}
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
    if (!remote.shouldAutoOpenPanel(safeRemoteAuthority(), context.workspaceState.get(KEY), activeCfg())) return;
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
  try { if (repatchTimer) clearTimeout(repatchTimer); } catch (_) {}
  repatchTimer = null;
  hostAudioInstance = null;
  stopAutoRefresh();
  stopNotifyWatch();
  stopConfigWatcher();
}

module.exports = { activate, deactivate };
