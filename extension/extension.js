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
const instancestate = require("./src/instancestate");
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
const remotehost = require("./src/remotehost");
const forwarder = require("./src/forwarder");
const forwarderui = require("./src/forwarder-ui");
const hypervRemote = require("./src/drivers/hyperv-remote");

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
/** The window-local selection installed when workspaceState.update REJECTED, so the
 *  switch the user was told about actually holds for this window (instances
 *  .planSwitchPersistence). "" whenever the persisted value is the truth. */
let windowInstanceOverride = "";
let registryCache = null;             // { at, registry } — the parsed instances.json
let registryProblemsShown = "";       // last problem set surfaced, so we toast once
const REGISTRY_TTL_MS = 5000;         // re-read the (tiny) file at most every 5s
/** The instance the mic tunnel was opened for, so a switch can retarget it. */
let hostAudioInstance = null;
/**
 * Ownership of the ONE mic-tunnel slot (instances.createSessionOwner). Every enable
 * claims it and stamps its own callbacks — the enable result, HostAudio's onStatus, the
 * teardown's final "disabled" — with the claim id, so a session we have switched away
 * from cannot paint its status over the new VM's tunnel or clear the reference to it.
 */
const audioSlot = instances.createSessionOwner();
/** The claim id the live mic session (hostAudio) was armed under, or null. */
let hostAudioSession = null;
/**
 * The live session's enable(), mapped to always settle. The teardown waits for it: a
 * HostAudio disabled MID-ENABLE tears down what exists at that moment (nothing yet), and
 * the enable then goes on to open its AudioSession and `ssh -R` behind the teardown's
 * back — an orphan tunnel on the instance we just left, referenced by nothing.
 */
let hostAudioEnable = null;
/**
 * The instance the mic auto-arm was last EVALUATED for — set at activation and on every
 * switch that retargets the tunnel. It is not `hostAudioInstance`: that one is null when
 * the evaluation produced no tunnel (the VM was unreachable, or the preference was off),
 * which is exactly the case where the next switch still has to evaluate the destination.
 */
let audioTargetInstance = null;
/** The instance the notification watcher is connected to. */
let notifyInstance = null;
/** The live client port forwarder (src/forwarder.js) and the instance it serves — one
 *  per window, retargeted on a switch exactly like the notification watcher. */
let forwarderSession = null;   // forwarder.Forwarder | null
let forwarderInstance = null;  // the instance name it was opened for
/**
 * Ownership of the ONE forwarder slot (instances.createSessionOwner), the twin of
 * `audioSlot`. A start claims it BEFORE its only await (the transport build, which reads
 * SecretStorage for a remote instance) and everything after that await — publishing the
 * session, and every snapshot it pushes — is gated on still owning the claim.
 * stopForwarder() takes the slot away, which is what makes a start that is still in flight
 * unable to leave a polling session, an `ssh -L` or a listening port behind it.
 */
const forwarderSlot = instances.createSessionOwner();
/**
 * The instance a forwarder has already been asked to serve since this window last SAW that
 * VM up. It is the edge that makes the forwarder lazy: a VM that is up is asked ONCE per
 * connect, so an older guest that answered "no spool" is not re-probed by every 30 s
 * refresh, and a window that has established nothing starts nothing. Cleared by a switch,
 * by the setting going off and by the VM going down — i.e. exactly the events after which
 * asking again is right. Not `forwarderInstance`: that one is null whenever the attempt
 * produced no session, which is the case the retry rule is about.
 */
let forwarderArmed = null;
/** The last snapshot it pushed, so a fresh webview renders the card immediately. */
let cachedForwards = null;
/**
 * The T3 web origin the LAST probed state reported (https once Construct's TLS
 * proxy is on, else plain http), KEYED BY INSTANCE. Only used as the ▷ button's
 * fallback when minting a pairing link fails; an instance whose probe saw no T3 Code has
 * no entry, so a stale origin can never outlive it.
 *
 * A single global was a cross-VM leak: a refresh of A landing after a switch to B left B's
 * button opening A's VM in the browser. Each entry is stamped with the instance the state
 * push it came from DESCRIBES (never "whatever is active now"), and read back for the
 * instance the ▷ action captured.
 */
const lastT3WebUrl = new Map();   // instance name -> origin
/** The active instance's idle policy (remote only), cached for the state push. */
let cachedIdlePolicy = null;
let cachedIdlePolicyInstance = null;
/** The status-bar item showing the active instance (only when >1 exists). */
let instanceStatusItem = null;
/**
 * The generation gate for the active instance (instances.createGate). Every async
 * refresh pipeline captures a token before its first await and re-checks it after each
 * one, so a slow stage that resolves AFTER a switch is discarded instead of painting
 * the previous VM's data under the new instance's name.
 */
// Seeded with the DEFAULT target's fingerprint, not just its name: an install with no
// registry resolves that same target for ever, so its gate never bumps at all.
const instanceGate = instances.createGate(
  instances.DEFAULT_INSTANCE_NAME, instances.targetFingerprint(instances.DEFAULT_INSTANCE));

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

/** The window's persisted instance choice, or "" when it has never chosen. A switch
 *  whose workspaceState write REJECTED stands in with an in-memory override at exactly
 *  the same precedence level (below the construct.instance pin), so the window really is
 *  on the instance the warning says it is — it just won't survive a reload. */
function workspaceInstance() {
  if (windowInstanceOverride) return windowInstanceOverride;
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
  //
  // Keyed by the COMPLETE target identity, not by the name: a registry entry rewritten
  // under the SAME name (a rebuilt remote VM on a new sshHost/sshPort, a changed
  // configBranch or service URL) reaches a different machine, and a name-keyed gate saw
  // nothing at all while every in-flight probe kept the old endpoint's answer alive.
  instanceGate.set(picked.instance.name, instances.targetFingerprint(picked.instance));
  return picked.instance;
}

/** The fingerprint of the target this window drives right now (instances.targetFingerprint
 *  of the resolved active instance). */
function activeFingerprint() { return instances.targetFingerprint(activeInstance()); }

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

/** The instance a captured target names. Falls back to the active one only for a target
 *  built without an instance object. */
function targetInstance(target) {
  return (target && target.instance) || activeInstance();
}

/**
 * Normalize a captured target into the FULL capture a long flow needs: the `instance`,
 * its ssh `cfg`, the `token` (the generation it was captured at) and the `scriptsDir`
 * that holds that instance's `.construct-settings.json`.
 *
 * The scripts dir is part of the capture, not something the tail of the flow re-resolves:
 * an import or a sync tick that finishes after a switch would otherwise write the
 * profiles it discovered on A into B's settings file. A target that carries no token was
 * captured without a gate (there is no earlier generation to compare against), so it is
 * stamped with the current one rather than treated as stale.
 */
function captureTargetFull(target) {
  const t = (target && target.cfg && target.name) ? target : actionTarget();
  const instance = t.instance || targetInstance(t);
  return {
    instance,
    name: t.name,
    cfg: t.cfg,
    token: t.token || instanceGate.token(),
    scriptsDir: t.scriptsDir !== undefined ? t.scriptsDir : resolveScriptsDirFor(instance),
  };
}

/** Has the window switched instances since `target` was captured? The QUIET form —
 *  it logs why a captured flow stopped instead of toasting, for the background steps
 *  (a sync tick, an import's profile auto-enable) the user never explicitly started. */
function targetStale(target, what) {
  if (!instances.targetSuperseded(instanceGate, target)) return false;
  logLine(`instances: ${what} for "${target.name}" finished after the window switched to ` +
    `"${activeInstance().name}" — discarded (nothing was written for either instance)`);
  return true;
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
const SYNC_TICK_MIN_MS = 5 * 60 * 1000;
// The last tick's TIMESTAMP and RESULT, keyed BY INSTANCE (instances.createSyncStatusStore,
// where the throttle/status rules are unit-tested). A tick belongs to one instance's branch
// and VM store, so held window-globally these two lied after a switch: the panel reported
// A's timestamp, result, warnings and blocked reason under B's name, and A's stamp
// satisfied the 5-minute throttle — suppressing B's first automatic tick for up to five
// minutes and leaving B's branch and store unsynchronized.
const syncStatus = instances.createSyncStatusStore({ throttleMs: SYNC_TICK_MIN_MS });
// ...while the SERIALIZATION stays window-global on purpose: there is ONE config repo and
// ONE cross-process lock, so a tick for B must still wait behind a tick for A.
let syncTickInFlight = false; // exposed as simple state for UI/recovery gates
let syncTickPromise = null;   // same-window callers queue behind the active tick
// Queued follow-up ticks, keyed BY INSTANCE (instances.createTargetQueue, where the
// ordering rules are unit-tested). Held as one global promise, a follow-up queued for A
// ran under whatever the window had switched to by the time it started — syncing B's
// branch and VM store while A's changes stayed unsynced.
const syncTickFollowups = instances.createTargetQueue();
let configWatcher = null;     // fs.watch handle on cfgDir/projects
// The auto-import scan is coalesced and throttled PER INSTANCE (instances.createCoalescer,
// where the ordering rules are unit-tested): held globally, an in-flight scan of A would
// be handed to a caller asking about B — the lifecycle pre-flight would treat B as
// scanned — and A's timestamp would suppress B's first automatic scan.
const importCoalescer = instances.createCoalescer({ throttleMs: SYNC_TICK_MIN_MS });

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
async function augmentUpdates(state, inst) {
  try {
    const target = inst || activeInstance();
    const scriptsDir = resolveScriptsDirFor(target);
    const raw = scriptsDir ? host.readRawSettings(scriptsDir) : {};
    // repo/ref/installedCommit describe the INSTALL; `provisionedCommit` describes THIS
    // VM, so it comes from that instance's own state (and, when the probe brought one
    // back, from the guest's marker on `state.provisionedCommit`, which outranks both).
    const instanceRaw = instancestate.readState(stateStore(target, scriptsDir));
    return await updates.augment(state, raw, { instanceRaw });
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
    const out = { instance: target.name, backend: target.backend };
    // The service HOST, not the whole URL: the panel renders it in a one-line row, and
    // the scheme + port are noise there. Only for an instance that has one — the panel
    // hides both rows for hyperv-local, so a single-VM install is pixel-identical.
    if (target.service && target.service.url) {
      try { out.serviceHost = remotehost.urlParts(target.service.url).host; }
      catch (_) { out.serviceHost = String(target.service.url); }
    }
    // The dropdown renders ONLY when more than one instance exists, so a single-VM
    // install's panel is pixel-identical to before.
    if (names.length > 1) {
      out.instances = names;
      // Which of them this WINDOW is attached to over Remote-SSH. Adoption only
      // preselects it and the user can switch away, so the picker has to say which entry
      // is the machine the terminals and files actually live on. Only sent alongside the
      // list, so a single-VM install's payload is unchanged.
      const connectedName = instances.connectedInstanceName(reg, safeRemoteAuthority());
      if (connectedName) out.connectedInstance = connectedName;
    }
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
  // Every state push funnels through here, so this is the one place that always
  // sees a freshly probed agents list.
  if (Array.isArray(state.agents)) {
    const t3 = state.agents.find((a) => a && a.id === "t3code");
    // Stamped with the instance the payload is LABELLED with (instanceState puts it
    // there), never with "whatever is active now" — the payload may be a slow refresh
    // landing after a switch.
    const forName = state.instance || activeInstance().name;
    if (t3 && typeof t3.url === "string" && t3.url) lastT3WebUrl.set(forName, t3.url);
    else lastT3WebUrl.delete(forName);
  }
  const extra = { usagePeriod: usageReport };
  if (cachedConfigSync) extra.configSync = cachedConfigSync;
  // Forwards + idle policy ride every push, so a webview that opens mid-session renders
  // them without waiting for a tunnel to change.
  if (cachedForwards) extra.forwards = cachedForwards;
  // `null` is a VALUE here, not "no news": it means "this instance has no idle policy",
  // which is how the panel knows to HIDE the card. Omitting it left a remote VM's card on
  // screen forever after switching to a local instance — renderIdlePolicy(null) was never
  // called. So it is attached as soon as it has been RESOLVED for the active instance
  // (which is what cachedIdlePolicyInstance records), null included; before that, and
  // only then, it is left off so the first paint does not flicker the card away.
  if (cachedIdlePolicyInstance === activeInstance().name) {
    extra.idlePolicy = cachedIdlePolicy;
  }
  // "Register this VM" (B11, plan §4.12): a window attached over Remote-SSH to a host
  // no registry entry describes. Attached to EVERY push — including as `null` — so the
  // offer disappears from the panel the moment the VM is registered. It is null on every
  // local window and on every install with one VM, which is the zero-change path.
  extra.registerOffer = registerThisVmOffer();
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
    const target = inst || activeInstance();
    const vmState = await vmpower.queryVmState({ instance: target, ...(await driverOpts(target)) });
    return { ...state, vmState: await refineSavedState(vmState, target) };
  } catch (_) {
    return { ...state, vmState: "unknown" };
  }
}

/**
 * Tell `saved` apart from `off` for a remote instance (plan §4.7).
 *
 * The driver contract deliberately collapses the two — `saved`/`paused`/`off` all mean
 * "a start brings it back" (docs/drivers.md §4), and every caller that ACTS on the state
 * wants exactly that. But the power button's WORDING is different in the saved case: the
 * idle policy put the VM's RAM on disk, so the same call resumes it where it was, and
 * "Start" would be the wrong promise. So the distinction is re-read here, for the label
 * only, from the service's own enum.
 *
 * Cheap and best-effort: only for a remote instance, only when the collapsed answer was
 * `off`, and any failure keeps the answer the driver already gave.
 */
async function refineSavedState(vmState, inst) {
  if (vmState !== "off") return vmState;
  if (String((inst && inst.backend) || "").trim().toLowerCase() !== "hyperv-remote") return vmState;
  try {
    const opts = await driverOpts(inst);
    const { client } = hypervRemote.resolveClient(inst, { ...opts, log: logLine });
    if (!client) return vmState;
    const res = await client.getState(hypervRemote.vmNameOf(inst));
    return String((res && res.state) || "").trim().toLowerCase() === "saved" ? "saved" : vmState;
  } catch (_) {
    return vmState;
  }
}

/**
 * The extra driver options an instance's backend needs — `{}` for `hyperv-local`, so
 * every local call is byte-for-byte the one it always made.
 *
 * A remote instance is reached over HTTPS with a credential, and the two things the
 * driver cannot get for itself both live here: the API token (VS Code SecretStorage,
 * which src/remotehost.js deliberately never touches) and the path of
 * lib/AgentVm.Remote.ps1 (the Negotiate provider spawns it, because Node has no SSPI).
 * Without these the driver reports "unknown" and refuses to start anything — which is
 * correct, but only because it is never asked to guess.
 *
 * The token is NOT substituted with the Windows identity when it is missing: the driver
 * treats that as the problem it is.
 */
async function driverOpts(inst) {
  const backend = String((inst && inst.backend) || "").trim().toLowerCase();
  if (backend !== "hyperv-remote") return {};
  const url = inst && inst.service && inst.service.url;
  if (!url) return {};
  const out = { remoteLib: remoteLibPath(), log: logLine };
  if (String(inst.service.auth || "") === "token") {
    let token = "";
    try { token = (await extensionContext.secrets.get(remotehost.tokenSecretKey(url))) || ""; }
    catch (_) { token = ""; }
    out.auth = { kind: "token", token };
  } else {
    out.auth = { kind: "negotiate" };
  }
  return out;
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
function withProjects(state, inst) {
  // Prefer cfgDir (the config-sync location); fall back to scriptsDir when cfgDir
  // is null (no LOCALAPPDATA / TEMP).
  let projRoot;
  let target;
  try { target = inst || activeInstance(); } catch (_) { target = null; }
  try {
    const dir = cfgDir || host.configDir(process.env);
    if (dir) { projRoot = dir; } else { projRoot = resolveScriptsDirFor(target); }
  } catch (_) { return state; }
  if (!projRoot) return state;
  let available, selected;
  try {
    available = host.listProjectProfiles(projRoot);
    // The SELECTION is per VM (instancestate), even though the profiles it names are
    // shared: two instances legitimately provision different subsets of one profile set.
    const scriptsDir = resolveScriptsDirFor(target);
    selected = scriptsDir ? instancestate.readSelectedProjects(stateStore(target, scriptsDir)) : [];
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
    // The selection lives in THAT instance's scripts dir, not in whichever one the
    // window shows now — a rebuild of A must provision A's selected profiles.
    const scriptsDir = resolveScriptsDirFor(inst);
    if (scriptsDir) {
      const saved = instancestate.readSelectedProjects(stateStore(inst, scriptsDir));
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
  const target = instances.captureTarget(instanceGate, inst);
  const gate = target.token;
  const probed = await probeOnce(inst);
  if (!instanceGate.valid(gate)) return;
  const state = withProjects(await withVmState(withLocalState(probed, inst), inst), inst);
  if (!instanceGate.valid(gate)) return;
  postState(webview, state);
  // The reading this window just took IS the forwarder's trigger — it never probes on its
  // own, and never starts against a VM nothing has established is up.
  noteForwarderPresence(target, state);
  const aug = await augmentUpdates(state, inst);
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
  // The captured TARGET (instance + cfg + generation) is what the config-sync tick,
  // the merge gate and the auto-import below all run against.
  const inst = activeInstance();
  const refreshTarget = instances.captureTarget(instanceGate, inst);
  const gate = refreshTarget.token;
  const probed = await probeOnce(inst);
  if (!instanceGate.valid(gate)) return;
  const state = withProjects(await withVmState(withLocalState(probed, inst), inst), inst);
  if (!instanceGate.valid(gate)) return;
  for (const w of liveWebviews) postState(w, state);
  // Same reading, same trigger (see refreshState): the forwarder is started — or let go —
  // by what the status flow established about the instance THIS refresh captured.
  noteForwarderPresence(refreshTarget, state);
  const aug = await augmentUpdates(state, inst);
  if (!instanceGate.valid(gate)) return;
  if (aug !== state) for (const w of liveWebviews) postState(w, aug);
  const report = usageReport;
  const withUsage = await augmentUsage(aug, report, inst);
  if (!instanceGate.valid(gate)) return;
  if (withUsage !== aug && usageReport === report) for (const w of liveWebviews) postState(w, withUsage);
  // The idle policy (remote instances only) — one HTTPS read, folded in like the update
  // check. A local instance resolves to null, which is what hides the card.
  try {
    await readIdlePolicy(inst);
    if (!instanceGate.valid(gate)) return;
    for (const w of liveWebviews) postState(w, withUsage !== aug ? withUsage : aug);
  } catch (_) { /* never break a refresh over an optional card */ }
  // Config-sync: update the cached state and run a throttled tick. Best-effort.
  try {
    const cs = await buildConfigSyncState(refreshTarget);
    if (!instanceGate.valid(gate)) return;
    cachedConfigSync = cs;
    for (const w of liveWebviews) postState(w, withUsage !== aug ? withUsage : aug);
    await maybeAutoSync(refreshTarget);
    if (!instanceGate.valid(gate)) return;
    // Auto-import runs regardless of git presence, so users without git still get
    // automatic discovery of new VM repos (docs/config-sync.md §10 degraded mode).
    // Scans the instance THIS refresh was captured for, and is throttled per instance.
    await maybeAutoImport(refreshTarget);
    if (!instanceGate.valid(gate)) return;
    const cs2 = await buildConfigSyncState(refreshTarget);
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
// WHAT is being watched, captured when the reprovision was LAUNCHED: `{ store, commit }`,
// or null when no reprovision is in flight. The marker is per VM now, so the poll has to
// keep asking about the VM the console is actually rebuilding — re-reading "the active
// instance" would compare instance B's marker against instance A's baseline, and end the
// fast poll on the first refresh after a switch (or never end it at all).
let reprovisionWatch = null;
let fastRefreshDeadline = 0;

/** The provisioned-commit hash the provisioner records at the end of a run ("" if unknown),
 *  for ONE instance's store. Cheap local file read — the same marker isProvisionStale and
 *  augment use. */
function provisionedCommitOf(store) {
  try {
    return instancestate.readMarkers(store).provisionedCommit || "";
  } catch (_) { return ""; }
}

/** True while we're in the post-reprovision fast-poll window. */
function fastRefreshActive() { return reprovisionWatch !== null; }

/** Enter the 5s fast-poll after a reprovision starts. `target` is the CAPTURED target the
 *  reprovision was launched for; its store is what the poll watches. Ends (see refreshTick)
 *  when THAT instance's provisioned commit changes or FAST_REFRESH_MAX_MS elapses. */
function beginReprovisionFastRefresh(target) {
  const inst = (target && target.instance) || null;
  const store = stateStore(inst, target && target.scriptsDir !== undefined ? target.scriptsDir : undefined);
  reprovisionWatch = { store, commit: provisionedCommitOf(store) };
  fastRefreshDeadline = Date.now() + FAST_REFRESH_MAX_MS;
  syncAutoRefresh(); // switch the live timer to the fast cadence
}

/** Leave fast-poll and return to the normal cadence. */
function endReprovisionFastRefresh() {
  reprovisionWatch = null;
  fastRefreshDeadline = 0;
  syncAutoRefresh();
}

/** One refresh tick. While fast-polling, first check whether the reprovision recorded a
 *  new provisioned commit (or the cap elapsed) and, if so, drop back to the normal
 *  cadence — then push fresh state to the open dashboards either way. */
function refreshTick() {
  if (fastRefreshActive()) {
    // Ask the store the reprovision was STARTED against, not the one this window happens
    // to show now.
    const now = provisionedCommitOf(reprovisionWatch.store);
    if ((now && now !== reprovisionWatch.commit) || Date.now() >= fastRefreshDeadline) {
      endReprovisionFastRefresh();
    }
  }
  // Did another process change WHICH VM this window drives (or that VM's endpoint) since
  // the last tick? Re-read the registry and, if the target really changed, hand the
  // sessions over through the one serialized transition — which ends in its own
  // refreshAll, so this tick must not run a second one beside it. On the default path
  // (no registry file) the fingerprint is a constant and this is a no-op.
  registryNow(true);
  if (retargetIfChanged("the instance registry changed")) return;
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

// ── Auto-reload after an EXTERNAL Construct update ───────────────────────────
// Update-Construct.ps1 is not only launched by this panel: the Construct-built T3 Code
// Desktop app runs it too, and so does the user by hand. None of those can reload VS
// Code. The script writes `installedCommit` into the scripts dir's
// .construct-settings.json as its LAST step (after the extension is reinstalled), so
// watching that marker is enough: when it changes under a running window, the refreshed
// panel is already installed and this window reloads itself. Polling one small file
// every few seconds is the cheapest reliable watch on Windows.
const INSTALLED_MARKER_POLL_MS = 3000;
let installedMarkerWatch = null; // { file, commit }

function installedCommitNow() {
  try {
    const dir = resolveScriptsDir();
    // INSTALL-WIDE: which Construct is installed on this PC. Never per instance — one
    // checkout, one installedCommit — so this stays on .construct-settings.json.
    return dir ? (updates.readMarkers(host.readRawSettings(dir)).installedCommit || "") : "";
  } catch (_) { return ""; }
}

function watchInstalledMarker(context) {
  const dir = resolveScriptsDir();
  if (!dir) return;
  const file = host.settingsPath(dir);
  installedMarkerWatch = { file, commit: installedCommitNow() };
  const onChange = () => {
    if (!installedMarkerWatch) return;
    const now = installedCommitNow();
    if (!now || now === installedMarkerWatch.commit) return;
    logLine(`update: installed marker changed ${installedMarkerWatch.commit.slice(0, 7) || "(none)"} → ${now.slice(0, 7)}; reloading window`);
    fs.unwatchFile(file, onChange);
    installedMarkerWatch = null;
    vscode.commands.executeCommand("workbench.action.reloadWindow");
  };
  fs.watchFile(file, { interval: INSTALLED_MARKER_POLL_MS, persistent: false }, onChange);
  context.subscriptions.push({ dispose: () => { fs.unwatchFile(file, onChange); installedMarkerWatch = null; } });
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

// ── Client port forwards (`construct expose`) ────────────────────────────────
// An agent on the VM runs `construct expose 5173`; src/forwarder.js opens that port on
// THIS PC, tunnels it over SSH and writes the ack the CLI is blocking on. All the logic
// lives in that module (plan §4.8: no vscode, injected transport) — this is the wiring:
// build the transport, own the lifetime, push the snapshot to the webviews.
//
// One forwarder per WINDOW, targeting the active instance, retargeted on a switch for the
// same reason the notification watcher is: its spool lives on one VM and its tunnels
// terminate there. Multi-window is resolved on the VM (the spool's .owner claim), not
// here — see extension/ARCHITECTURE.md §Forwards.

/** Whether serving forward requests is switched on (setting; default true). */
function forwardsEnabled() {
  return forwarderui.forwardsEnabled(vscode);
}

/**
 * Build the transport for an instance. Local instances get plain SSH; a remote one also
 * gets a host-service client, built from the OWNER's credential — the VM's own token
 * deliberately cannot post a client ack (service/README.md), so the ack relay only works
 * with the credential this window already holds for the driver.
 *
 * Returns null when a remote instance cannot be reached at all (no service URL, no stored
 * token), which is the honest answer: nothing is started rather than a poll that 401s
 * every ten seconds.
 */
async function buildForwarderTransport(inst) {
  const cfg = instances.toSshCfg(inst);
  const backend = String((inst && inst.backend) || "").trim().toLowerCase();
  if (backend !== "hyperv-remote") return forwarderui.createSshTransport({ ssh, cfg });

  const opts = await driverOpts(inst);
  const { client, problem } = hypervRemote.resolveClient(inst, { ...opts, log: logLine });
  if (!client) {
    logLine(`forwards: not serving "${inst.name}" — ${problem}`);
    return null;
  }
  return forwarderui.createRemoteTransport({ ssh, cfg, client });
}

/**
 * The forwarder's SERIALIZED session chain (instances.createHandover) — the same one the
 * mic tunnel runs on, for the same reason: a forwarder is a live per-VM connection, and
 * exactly one of them may exist at a time.
 *
 * Run beside each other instead, a start and a stop can interleave across the transport
 * build's await: an A→B→A switch (or an off/on of the setting) let two sessions for A be
 * constructed, the second overwrote the module's only reference to the first, and the
 * orphan kept its poll, its `ssh -L` children and its listening ports with nothing left in
 * this window able to dispose it. Queued here, each step tears the live session down and
 * builds its OWN captured target's before the next step looks at the world.
 */
const forwarderChain = instances.createHandover({
  session: () => ({ live: !!forwarderSession, name: forwarderInstance }),
  teardown: () => stopForwarder(),
  arm: (target) => startForwarder(target),
  superseded: (target) => instances.targetSuperseded(instanceGate, target),
});

/**
 * The forwarder slot as instances.planEnable reads it — the same decision the mic session
 * takes, from the same helper.
 *
 * A forwarder session is STARTED by the statement that creates it (its own first SSH round
 * trip is inside `start()`, after the object exists), so `live` implies `enabled` and
 * `pending` is always false: the decision reduces to create / report / refuse. The one
 * await that could be overtaken — building the transport — happens BEFORE anything is
 * constructed, and is guarded by the slot claim rather than by a half-built session.
 */
function forwarderSlotState() {
  return {
    live: !!forwarderSession,
    name: forwarderInstance,
    enabled: !!forwarderSession,
    pending: false,
    closed: forwarderChain.closed,
  };
}

/**
 * Open the forwarder for a CAPTURED target. Only ever reached through the chain, and only
 * for a VM the window has already established is up (noteForwarderPresence) — this function
 * never probes and is never called from activation.
 *
 * Building the transport is a real await for a remote instance (SecretStorage.get), and
 * disabling the feature, switching instance or closing the window during it must all leave
 * nothing behind. So the claim is taken first, and every one of those is re-checked after
 * it: a start that lost the slot publishes nothing, and a snapshot from a session the slot
 * has moved on from is dropped rather than painted over the current VM's card.
 */
async function startForwarder(target) {
  const t = target || actionTarget();
  const inst = targetInstance(t);
  const plan = instances.planEnable(forwarderSlotState(), t.name);
  if (plan.action !== "create") {
    // "report" — the destination already has its forwarder (the chain tears down anything
    // that belongs to another VM before this runs). "refuse" — the window is closing.
    if (plan.action === "refuse") logLine(`forwards: not serving "${t.name}" — ${plan.reason}`);
    return;
  }
  if (!forwardsEnabled()) return;
  if (instances.targetSuperseded(instanceGate, t)) return;

  // Claim the ONE slot before the await; every stop request invalidates it (see
  // requestForwarderStop), which is what a start in ANY of its awaits is checked against.
  const claim = forwarderSlot.claim(t.name);
  const transport = await buildForwarderTransport(inst);
  if (!forwarderSlot.owns(claim)) {
    logLine(`forwards: the start for "${t.name}" was discarded — forwarding was stopped while its transport was being built`);
    return;
  }
  if (forwarderChain.closed || !forwardsEnabled()) {
    logLine(`forwards: the start for "${t.name}" was discarded — ` +
      (forwarderChain.closed ? "the window is closing" : "forwarding was switched off"));
    return;
  }
  if (instances.targetSuperseded(instanceGate, t)) {
    logLine(`forwards: the start for "${t.name}" was discarded — the window switched to "${activeInstance().name}" while its transport was being built`);
    return;
  }
  if (!transport) return;

  // The SAME four conditions, asked again from inside the session — its own start has an
  // await too (the guest capability check), and the teardown for a disable / switch /
  // unreachable reading that lands during it is queued BEHIND this step. Without this the
  // session would spawn its watcher and reconcile the spool first and be disposed a moment
  // later: SSH traffic after the window said stop.
  const stillWanted = () => forwarderSlot.owns(claim)
    && !forwarderChain.closed
    && forwardsEnabled()
    && !instances.targetSuperseded(instanceGate, t);

  forwarderInstance = t.name;
  forwarderSession = forwarder.createForwarder({
    instance: inst,
    transport,
    hostLabel: forwarderui.hostLabelOf(vscode),
    log: logLine,
    eligible: stillWanted,
    onChange: (snapshot) => {
      // A snapshot that arrives after a LATER claim describes a session this window has
      // already let go of; painting it would show another VM's forwards under this one's.
      if (!forwarderSlot.owns(claim)) return;
      cachedForwards = forwarderui.toPanelForwards(snapshot);
      broadcastForwards();
    },
  });
  logLine(`forwards: serving "${t.name}" (${forwarderSession.mode} mode)`);
  // AWAITED, so the chain step stays open until the guest capability check has answered:
  // the next queued step must not decide its teardown against a session that is still
  // deciding whether it may run at all. Its answer is also what decides the armed edge.
  const session = forwarderSession;
  const outcome = await session.start();
  noteForwarderStarted(t, claim, session, outcome);
}

/**
 * THE ARMED EDGE, AFTER THE GUEST HAS ANSWERED — the other half of the lazy trigger.
 *
 * `noteForwarderPresence` arms the instance when a reachable reading asks for a start, and
 * that edge is what keeps an older guest from being re-asked every 30 s. But the session's
 * own check is one SSH exec, and a failed one establishes NOTHING: the session stands
 * itself down while this window keeps both the armed edge and the reference, so every later
 * reachable reading plans "none" and no watcher, no reconcile and no ack is ever retried —
 * a queued `construct expose` then waits out its whole timeout for no reason.
 *
 * So an `unanswered` start lets go: the session reference and the armed edge are cleared
 * together (the decision is the pure forwarder.planStartOutcome), and the next reachable
 * reading is a fresh start. `unsupported` — the guest ANSWERED "no spool" — keeps the edge,
 * because that answer cannot change until the VM is reprovisioned.
 *
 * It runs INSIDE the chain step that created this session, which is what makes disposing
 * here legal (only the chain may dispose one) and what makes the two writes atomic against
 * every other step. A start the window has already moved on from touches neither: what it
 * would clear belongs to a later session by then.
 */
function noteForwarderStarted(t, claim, session, outcome) {
  const plan = forwarder.planStartOutcome({
    outcome,
    current: forwarderSlot.owns(claim) && forwarderSession === session
      && !instances.targetSuperseded(instanceGate, t),
  });
  if (plan.action !== "retry") return;
  logLine(`forwards: "${t.name}" never answered the guest check — letting it go so the next reading that reaches it retries`);
  forwarderArmed = null;
  stopForwarder();
}

/**
 * Tear the forwarder down: every tunnel, the watcher and the ownership claim — and every
 * start that is still in flight, which is what the slot claim is for.
 */
function stopForwarder() {
  // Invalidate first: a start awaiting its transport must not publish a session after this.
  forwarderSlot.claim("");
  const session = forwarderSession;
  forwarderSession = null;
  forwarderInstance = null;
  cachedForwards = null;
  if (session) { try { session.dispose(); } catch (_) {} }
}

/**
 * ASK FOR THE FORWARDER TO STOP — the only way this window says so.
 *
 * Two halves, and they are not the same instant. The SLOT is invalidated synchronously,
 * because a start may be sitting in one of its awaits (the transport build here, or the
 * guest capability check inside the session) and the disposal below is queued BEHIND that
 * step: without the immediate invalidation the start would go on to publish a session and
 * spawn a watcher, and only then be torn down — SSH traffic the user already said no to.
 * The DISPOSAL is queued on the chain, because only the chain may dispose a session.
 */
function requestForwarderStop() {
  forwarderSlot.claim("");
  forwarderArmed = null;
  return forwarderChain.disable();
}

/**
 * THE FORWARDER'S ONLY TRIGGER: what this window's existing status flow just learned about
 * the instance a refresh was captured for.
 *
 * The forwarder is deliberately not started by activation. `construct.forwards.enabled`
 * defaults to true — `construct expose` has to work out of the box for the agents that are
 * told to use it — but "enabled" cannot mean "open an SSH stream to a VM we know nothing
 * about": on a default install that started an `inotifywait` watcher against a VM that may
 * be off, may be unreachable, or may be an older guest with no spool at all. So the state
 * the panel already reads (`probeOnce` + `withVmState`, no extra round trip) is what starts
 * it, the decision is the pure forwarder.planLifecycle, and the work is queued on the one
 * chain. See extension/ARCHITECTURE.md §Forwards.
 */
function noteForwarderPresence(target, state) {
  if (!target) return;
  const plan = forwarder.planLifecycle({
    enabled: forwardsEnabled(),
    name: target.name,
    armed: forwarderArmed,
    online: !!(state && state.online),
    vmState: state && state.vmState,
  });
  if (plan.action === "start") {
    forwarderArmed = target.name;
    void forwarderChain.enable(target, (t) => startForwarder(t));
    return;
  }
  if (plan.action === "stop") {
    // This window can no longer reach that VM (it is off, saved, gone, or simply did not
    // answer): a watcher and a set of `ssh -L` children pointed at it are holding sockets
    // and nothing else. Let go — the requests survive in the spool under /etc/construct,
    // so the next reading that DOES reach it re-opens everything still queued.
    logLine(`forwards: letting "${target.name}" go — ${plan.reason}`);
    void requestForwarderStop();
  }
}

/**
 * A window ATTACHED to the VM over Remote-SSH already knows that VM is up: its own
 * connection terminates there (the extension is `"extensionKind": ["ui"]`, so it runs on
 * this PC either way). That is the one reachability fact activation has without probing
 * anything, and it is the case `construct expose` exists for — an agent working in a window
 * on the VM, whether or not anybody ever opens the panel. Everything else waits for the
 * status flow.
 */
function noteForwarderConnected() {
  try {
    if (!remote.isConnectedToVm(safeRemoteAuthority(), activeCfg())) return;
    noteForwarderPresence(actionTarget(), { online: true, vmState: "running" });
  } catch (_) { /* never break activation over an optional connection */ }
}

/**
 * Push just the forwards card to every live surface — a tunnel coming up must not wait for
 * the next 30 s probe to become visible.
 *
 * Its OWN message type, not a partial `state`: `render(state)` in the webviews treats an
 * absent field as "no reading", so a `{forwards}`-only state would blank the power button,
 * flip the ONLINE pill and reset the install markers. Same reason `{type:'audio'}` exists.
 *
 * SCOPED with the instance whose forwarder produced the snapshot. Session-slot ownership
 * (`forwarderSlot.owns`) stops a superseded session from ever reaching this function, but
 * it says nothing about a message ALREADY POSTED to a webview: a snapshot for A queued
 * behind a full state push for B would still render on B's card. With no live session
 * there is nothing to describe, so the empty snapshot is stamped with the window's current
 * target — which is exactly what should be shown then.
 */
function broadcastForwards() {
  const owner = forwarderInstance || activeInstance().name;
  for (const w of liveWebviews) safePost(w, { type: "forwards", instance: owner, forwards: cachedForwards });
}

/**
 * The active instance's idle policy, for the state push. Remote only: the service is what
 * enforces it (plan §4.7), so a local instance has none and the panel shows nothing.
 * Best-effort and cached — a failed read leaves the card as it was rather than blanking it.
 */
async function readIdlePolicy(inst) {
  const target = inst || activeInstance();
  if (String(target.backend || "").trim().toLowerCase() !== "hyperv-remote") {
    cachedIdlePolicy = null;
    cachedIdlePolicyInstance = target.name;
    return null;
  }
  if (cachedIdlePolicyInstance !== target.name) { cachedIdlePolicy = null; cachedIdlePolicyInstance = target.name; }
  // Same rule as saveIdlePolicy: the credential lookup and the GET both outlive a switch
  // easily, and the cache they write is what the next state push paints.
  const token = instanceGate.token();
  try {
    const opts = await driverOpts(target);
    if (!instanceGate.valid(token)) return cachedIdlePolicy;
    const { client } = hypervRemote.resolveClient(target, { ...opts, log: logLine });
    if (!client) return cachedIdlePolicy;
    const name = hypervRemote.vmNameOf(target);
    const body = await client.request("GET", `/vms/${encodeURIComponent(name)}/idle-policy`);
    if (!instanceGate.valid(token)) return cachedIdlePolicy;
    const policy = forwarderui.toPanelIdlePolicy(body);
    if (policy) cachedIdlePolicy = policy;
    return cachedIdlePolicy;
  } catch (e) {
    logLine(`idle policy: could not read "${target.name}" — ${(e && e.message) || e}`);
    return cachedIdlePolicy;
  }
}

/** The panel's "apply" on the idle-policy card. Clamps to the admin cap first, so the
 *  number that goes over the wire is the number the user was shown. */
async function saveIdlePolicy(policy) {
  const target = actionTarget();
  const inst = targetInstance(target);
  if (String(inst.backend || "").trim().toLowerCase() !== "hyperv-remote") {
    vscode.window.showInformationMessage(
      `Idle policy is enforced by the host service, and "${inst.name}" runs on this PC's Hyper-V — there is nothing to configure.`
    );
    return;
  }
  const cap = (cachedIdlePolicy && cachedIdlePolicy.maxTimeoutMinutes) || 0;
  const wanted = forwarderui.clampIdlePolicy(policy, cap);
  try {
    const opts = await driverOpts(inst);
    // The credential lookup can prompt, and the PUT is a network round trip: both are
    // long enough for a switch. A response for A must not be cached as B's policy, and
    // must not be broadcast either — the card it paints sits on whatever instance the
    // panel is showing.
    if (targetSuperseded(target, "The idle-policy change")) return;
    const { client, problem } = hypervRemote.resolveClient(inst, { ...opts, log: logLine });
    if (!client) throw new Error(problem);
    const body = await client.request("PUT", `/vms/${encodeURIComponent(hypervRemote.vmNameOf(inst))}/idle-policy`,
      { timeoutMinutes: wanted.timeoutMinutes, action: wanted.action });
    // The PUT ITSELF LANDED — the host service has applied it to the instance it was sent
    // to, which is the right one either way. Only what we do with the ANSWER is gated:
    // the cache and the panel belong to whatever this window drives now.
    if (targetSuperseded(target, "The idle-policy change")) return;
    const applied = forwarderui.toPanelIdlePolicy(body);
    if (applied) {
      cachedIdlePolicy = applied;
      cachedIdlePolicyInstance = inst.name;
      // Own message type, for the same reason broadcastForwards has one — and SCOPED, so
      // the webview can drop a payload that describes another instance. The extension-side
      // gate above and this one are two layers of the same rule: the gate stops a late
      // response from being cached at all, the scope stops one that is already in flight
      // to a webview from painting the card of the instance now on screen.
      for (const w of liveWebviews) safePost(w, { type: "idlePolicy", instance: inst.name, idlePolicy: applied });
      if (applied.clamped) {
        vscode.window.showInformationMessage(
          `Idle timeout set to ${applied.timeoutMinutes} minutes — the host's administrator caps it there.`
        );
      }
    }
    logLine(`idle policy: "${inst.name}" -> ${wanted.timeoutMinutes}m / ${wanted.action}`);
  } catch (e) {
    const detail = (e && e.message) || String(e);
    logLine(`idle policy: could not save "${inst.name}" — ${detail}`);
    vscode.window.showWarningMessage(`Couldn't save the idle policy for "${inst.name}": ${detail}`);
  }
}

/** The panel's Open button: hand the link to the OS browser. */
async function openForward(id) {
  const item = (cachedForwards && cachedForwards.items || []).find((i) => i.id === id);
  if (!item || !item.url) {
    vscode.window.showInformationMessage("That forward isn't open yet — there is no link to open.");
    return;
  }
  try { await vscode.env.openExternal(vscode.Uri.parse(item.url)); }
  catch (e) { logLine(`forwards: could not open ${item.url} — ${(e && e.message) || e}`); }
}

/** The panel's Close button. */
async function closeForward(id) {
  if (!forwarderSession) return;
  const ok = await forwarderSession.closeForward(String(id || ""));
  if (!ok) vscode.window.showWarningMessage("Couldn't close that forward — see the Construct log for why.");
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

/**
 * Locate the host-side scripts dir OF ONE INSTANCE: that instance's pinned `scriptsDir`
 * first (registry field; null for the default instance), then the `construct.scriptsDir`
 * override, then newest-install detection — see host.resolveScriptsDir.
 *
 * The scripts dir also holds the instance's `.construct-settings.json` (the project
 * selection, the mic preference, the patch toggles), so any flow that already captured a
 * target MUST resolve it from that target: re-reading "the active instance" after an
 * await would write A's discoveries into B's settings file, or reprovision B with A's
 * scripts. `resolveScriptsDir()` (no argument) is the un-captured, right-now answer —
 * for the default instance both are byte-identical.
 */
function resolveScriptsDirFor(instance) {
  const override = vscode.workspace.getConfiguration("construct").get("scriptsDir");
  let pinned = null;
  try { pinned = instance ? instance.scriptsDir : null; } catch (_) { pinned = null; }
  return host.resolveScriptsDir({ instanceScriptsDir: pinned, scriptsDir: override, env: process.env });
}

/** The active instance's scripts dir (see resolveScriptsDirFor). */
function resolveScriptsDir() {
  let active = null;
  try { active = activeInstance(); } catch (_) { active = null; }
  return resolveScriptsDirFor(active);
}

/**
 * The PER-INSTANCE STATE STORE of one instance (src/instancestate.js): which instance's
 * VM-scoped settings to read/write, and the scripts dir that holds the install-wide half
 * (`installedCommit`, `constructRepo`/`constructRef`, the host git identity).
 *
 * For the DEFAULT instance the store IS that scripts dir's `.construct-settings.json`, so
 * every call below is byte-for-byte the `host.*` call it replaced — an install with one
 * local VM and no registry writes exactly the files it always wrote, and no
 * `instances\agent-vm.json` is ever created. Any other instance reads and writes only
 * `%LOCALAPPDATA%\The-Construct\instances\<name>.json`.
 *
 * Like resolveScriptsDirFor, the instance comes from the CAPTURED target of a flow, never
 * from a fresh "whatever is active now" read after an await.
 */
function stateStore(instance, scriptsDir) {
  const dir = scriptsDir !== undefined ? scriptsDir : resolveScriptsDirFor(instance);
  return instancestate.store(instance || activeInstance(), dir, process.env);
}

/** The active instance's store (see stateStore). */
function activeStore() {
  let inst = null;
  try { inst = activeInstance(); } catch (_) { inst = null; }
  return stateStore(inst);
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

/** Build state.configSync (D9) from the current engine state. Host-derived.
 *  `target` is the captured instance this state DESCRIBES — its last tick, its recovery
 *  tick; a refresh pipeline passes its own, omitted = capture the active one now. The
 *  timestamp/result come from THAT instance's entry in syncStatus, so switching to an
 *  instance that has never synced reports "never synced" instead of the other VM's tick. */
async function buildConfigSyncState(target) {
  const csTarget = target || actionTarget();
  const dir = resolveCfgDir();
  const git = await detectGitCached();
  const out = {
    gitPresent: git.present,
    repoReady: false, conflict: false, conflictFiles: [], mergeInProgress: false,
    ...syncStatus.status(csTarget.name),
    remotes: [],
  };
  if (dir && git.present && runGit) {
    try {
      var rs = await configsync.repoState(runGit, dir);
      if (rs.mergeInProgress && !rs.conflict && !syncTickInFlight) {
        var recovered = await runConfigSync(csTarget);
        if (recovered) {
          // The recovery tick ran for THIS target (runConfigSync records it under the
          // same name), so its status is that target's entry.
          Object.assign(out, instances.describeSyncStatus(syncStatus.lastAt(csTarget.name), recovered));
        }
        rs = await configsync.repoState(runGit, dir);
      }
      out.repoReady = rs.repo; out.conflict = rs.conflict;
      out.conflictFiles = rs.conflictFiles || []; out.mergeInProgress = rs.mergeInProgress;
    } catch (_) {}
    try {
      // The panel only ever gets DISPLAY-SAFE urls: a legacy remotes.json entry may
      // carry a PAT in its userinfo, and the remote row renders this string. Handlers
      // map the round-tripped value back with configsync.resolveRemoteUrl.
      out.remotes = configsync.readRemotes(dir).map(function (r) {
        return { url: configsync.displayRemoteUrl(r.url) };
      });
    } catch (_) {}
  }
  return out;
}

/**
 * Run a sync tick FOR ONE CAPTURED INSTANCE. Same-window callers wait, then share one
 * follow-up tick so changes made during the active snapshot are not mistaken for having
 * synced.
 *
 * `target` is captured by the caller (Sync Now, the lifecycle pre-flight, a refresh) and
 * is the ONLY instance this tick speaks about: the SSH cfg it reads/writes the VM store
 * with, the `vmBranch` it commits and fast-forwards, the scripts dir it auto-enables
 * profiles into, and the instance its post-tick import scans. Omitted = capture the
 * active instance now, before the first await. The follow-up queue is keyed by target
 * too, so a Sync Now for A queued behind A's tick can never turn into a tick for B —
 * and when the window has switched by the time that follow-up starts, it runs for
 * NEITHER instance: the generation is re-checked at the tick's entry, when a queued
 * follow-up starts, and after the tick's own awaits before either follow-on step.
 */
async function runConfigSync(target) {
  // BEFORE the first await: instance, cfg, scripts dir and generation. Everything below
  // belongs to this one capture and nothing re-reads "the active instance".
  var syncTarget = captureTargetFull(target);
  var dir = resolveCfgDir();
  if (!dir) return null;
  var git = await detectGitCached();
  if (!git.present) return null;
  // A tick that was captured before a switch must not run at all — not against the new
  // instance (it would sync the wrong branch and store) and not against the old one
  // (this window no longer drives it, and the caller's own gate has already moved on).
  if (targetStale(syncTarget, "The config-sync tick")) return null;
  if (syncTickPromise) {
    return syncTickFollowups.queue(syncTarget.name, syncTickPromise, function () {
      // Re-checked HERE, when the follow-up finally starts: the whole point of queueing
      // it is that time passes, and the switch usually happens inside that window.
      if (targetStale(syncTarget, "The queued config-sync tick")) return null;
      return runConfigSync(syncTarget);
    });
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
    // The instance this tick belongs to (captured by the caller, or at entry above):
    // the store read/write, the branch, the profile auto-enable and the post-tick
    // import all speak about the SAME VM, whatever the window switches to meanwhile.
    var syncInstance = targetInstance(syncTarget);
    var syncCfg = syncTarget.cfg;
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
      // otherwise) -- from the instance captured above, so a switch mid-tick cannot
      // move the refs this tick writes onto another instance's branch.
      vmBranch: syncInstance.configBranch,
      log: function (level, msg) { logLine("[configsync] [" + level + "] " + msg); },
    });
    // Recorded against the CAPTURED target: this tick's timestamp and result describe
    // that instance's branch and VM store, and nothing about any other instance.
    syncStatus.record(syncTarget.name, result);
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
    // The tick itself is done (it ran under the repo lock and has already written, so
    // its result is still recorded above). BOTH follow-on steps below mutate host state
    // on this instance's behalf — the selection file and the imported profile files —
    // so a switch during the tick stops them here. Each also re-checks internally,
    // because each awaits again on its way to its own write.
    var followUpsStale = targetStale(syncTarget, "The post-tick follow-ups");
    if (!followUpsStale && result && result.ok && !result.lockBusy) {
      await autoEnableNewProfiles(profilesBeforeTick, host.listProjectProfiles(dir), syncTarget);
    }
    // Auto-import: when the VM was reachable this tick, scan for repos not yet
    // covered by a local profile and import them. This replaces the manual
    // "import from VM" button — new configs are discovered automatically on
    // every sync tick. Runs AFTER the sync tick (not inside it) so the lock is
    // released and locally-written profiles don't race the git engine.
    if (!followUpsStale && result && result.ok && result.vmReadOk) {
      try { await coalescedImport(true, syncTarget); } catch (e) {
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
 *  fresh names ride along.
 *
 *  `target` is the captured instance the profiles were discovered FOR. The selection
 *  lives in THAT instance's scripts dir, so re-resolving the active one here would write
 *  A's discoveries into B's .construct-settings.json whenever a tick (or an import)
 *  finishes after a switch. Omitted = the active instance, captured now. */
async function autoEnableNewProfiles(before, after, target) {
  try {
    var afterArr = after || [];
    var beforeSet = new Set(before || []);
    var fresh = afterArr.filter(function (n) {
      return n && !beforeSet.has(n) && !projects.isReservedProfileName(n);
    });
    if (!fresh.length) return;
    var enableTarget = captureTargetFull(target);
    // The discovery belongs to ONE instance's settings file. Once the window has
    // switched we write NEITHER file: B's would receive A's discoveries, and A is no
    // longer this window's VM — its next tick will pick the same profiles up again.
    if (targetStale(enableTarget, "The project-profile auto-enable")) return;
    // The scripts dir comes from the CAPTURE, not from a fresh resolution here.
    var scriptsDir = enableTarget.scriptsDir;
    if (!scriptsDir) return;
    var current;
    var enableStore = stateStore(enableTarget.instance, scriptsDir);
    if (instancestate.hasPersistedSelection(enableStore)) {
      current = instancestate.readSelectedProjects(enableStore);
    } else {
      current = await effectiveProjects(enableTarget.instance);
    }
    // Reconcile against the actual post-import profile list (afterArr), not
    // scriptsDir — profiles live in cfgDir and afterArr is the authoritative
    // set of names that exist after the import/sync completed.
    var merged = projects.additiveMergeSelection(current, fresh, afterArr);
    // effectiveProjects() can probe the VM, so re-check IMMEDIATELY before the write.
    if (targetStale(enableTarget, "The project-profile auto-enable")) return;
    instancestate.saveSelectedProjects(enableStore, merged);
    logLine("auto-enabled new project profile(s) from sync: " + fresh.join(", ") + " (selection now: " + merged.join(", ") + ")");
  } catch (e) {
    logLine("auto-enable of new profiles failed: " + (e && e.message ? e.message : e));
  }
}

/** Why a merge gate came back blocked without the repo being at fault. */
const STALE_GATE_REASON = "This window switched to another Construct instance while the config repo was being checked, so it is no longer clear which VM this was for.";

/**
 * The conflict gate for a captured action: complete a pending clean merge on THAT
 * instance's branch, then report whether the config repo is in a usable state.
 *
 * `completePendingMerge` WRITES — it creates the merge commit whose message names the
 * branch ("sync merge vm-work-vm") — so the branch it is given must be the one the
 * caller's action is about. Everything is captured before the first await, and the
 * generation is re-checked immediately before that write and again after the repo reads.
 * A stale gate is never `{blocked:false}`: it returns `blocked` with `stale: true`, so a
 * destructive pre-flight fails CLOSED (it cancels and says the window switched) while a
 * background caller simply skips its state refresh.
 */
async function configMergeGate(target) {
  var gateTarget = captureTargetFull(target);
  var dir = resolveCfgDir();
  var git = await detectGitCached();
  if (!dir || !git.present || !runGit) return { blocked: false, dir: dir };
  var staleGate = function () {
    return { blocked: true, stale: true, dir: dir, reason: STALE_GATE_REASON };
  };
  // Before the WRITE: git detection above can take seconds on a cold host.
  if (targetStale(gateTarget, "The config merge gate")) return staleGate();
  // The target instance's branch: the merge commit this completes is THAT branch's
  // merge, and the message ("sync merge vm-work-vm") is what a later reader of the
  // config repo's history goes by. Every syncTick call already threads it.
  var pending = await configsync.completePendingMerge(runGit, dir, gateTarget.instance.configBranch);
  if (pending.completed) {
    logLine("[configsync] completed pending clean merge");
  }
  var rs = await configsync.repoState(runGit, dir);
  // ...and after the repo operations, because the answer below decides whether a
  // destructive action may proceed. An indeterminate answer must not read as "clear".
  if (targetStale(gateTarget, "The config merge gate")) return staleGate();
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
  // Scan the instance THIS action targets, not whatever the window is showing now:
  // joining another instance's in-flight scan would report B as scanned on A's result.
  var importResult = await coalescedImport(true, target);
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
    try { syncResult = await runConfigSync(target); } catch (_) { syncResult = null; }
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
      gate = await configMergeGate(target);
    } catch (e) {
      return {
        ok: false,
        dir: cfgDir,
        reason: "Could not verify config sync state before " + actionLabel +
          ". Check the config repo for issues, then try again." +
          (e && e.message ? " (" + e.message + ")" : ""),
      };
    }
    if (gate.stale) {
      // Fail-CLOSED, but with the honest reason: nothing is wrong with the repo, we just
      // can no longer tell which VM the action was for.
      return { ok: false, reason: STALE_GATE_REASON + " " + actionLabel[0].toUpperCase() + actionLabel.slice(1) +
        " was cancelled — switch back and try again." };
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

/** Throttled sync tick for auto-refresh: only runs if >=5 min since THAT INSTANCE's last
 *  tick. `target` is the refresh pipeline's captured instance — the tick it may start
 *  belongs to that VM, so the throttle it is measured against is that VM's too. A global
 *  stamp made A's tick suppress B's first automatic sync for the whole window. */
async function maybeAutoSync(target) {
  const autoTarget = captureTargetFull(target);
  if (!syncStatus.dueForAuto(autoTarget.name)) return;
  await runConfigSync(autoTarget);
}

/** The target an import runs against: the caller's captured one when it has one (a
 *  lifecycle pre-flight, a sync tick), otherwise the active instance captured NOW —
 *  never re-read later, so a switch mid-scan cannot redirect it. The WHOLE capture is
 *  kept (instance, cfg, scriptsDir and the generation token), because the scan's tail
 *  writes that instance's project selection and has to be able to check the generation
 *  it started under; reducing it to {name, cfg} is what let a scan of A auto-enable its
 *  discoveries into B's .construct-settings.json. */
function importTargetOf(target) {
  return captureTargetFull(target);
}

/** Coalesced import: if an import IS ALREADY IN FLIGHT FOR THAT INSTANCE, join it
 *  instead of starting a second SSH scan. When `force` is true, bypass the time
 *  throttle (used by explicit user actions like Sync Now and lifecycle pre-flight).
 *  `target` is the captured instance to scan; both the coalescing and the throttle are
 *  keyed by it, so a scan of A never stands in for B. */
function coalescedImport(force, target) {
  const t = importTargetOf(target);
  return importCoalescer.run(t.name, force, function () { return importFromVm(t); });
}

/** Throttled auto-import: runs importFromVm regardless of git presence, so users
 *  without git still get automatic discovery. Uses the same 5-min throttle as the
 *  sync tick. Coalesces concurrent attempts (per instance) so offline/hanging SSH
 *  doesn't cause unbounded overlapping scans. */
function maybeAutoImport(target) {
  return coalescedImport(false, target);
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
        // Capture at fire time, before the tick's first await: a debounced file change
        // syncs the instance this window drives NOW, and keeps it for the whole tick.
        runConfigSync(actionTarget()).then(function () { refreshAll(); });
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
  // Read and STAMP in the same breath: the file is `<the active instance's scriptsDir>
  // \.construct-settings.json`, so the settings are per-instance and the name that labels
  // them must be the one they were just read for.
  const inst = activeInstance();
  const scriptsDir = resolveScriptsDirFor(inst);
  if (!scriptsDir) return;
  let settings;
  try { settings = instancestate.readSettings(stateStore(inst, scriptsDir)); } catch (_) { return; }
  safePost(webview, { type: "settings", instance: inst.name, settings });
}

/** Push the on-disk settings to EVERY live surface (so both mic switches — the
 *  console #voiceSwitch and the settings #setMic — reflect the same persisted value). */
function broadcastSettings() {
  const inst = activeInstance();                 // read and stamp together (see pushSettings)
  const scriptsDir = resolveScriptsDirFor(inst);
  if (!scriptsDir) return;
  let settings;
  try { settings = instancestate.readSettings(stateStore(inst, scriptsDir)); } catch (_) { return; }
  for (const w of liveWebviews) safePost(w, { type: "settings", instance: inst.name, settings });
}

/** Persist the mic-passthrough preference (micPassthrough in .construct-settings.json).
 *  The live console toggle IS this persistent setting — enabling on the main page makes
 *  it auto-arm next session (see maybeAutoEnableAudio). Merges (touches only that key).
 *  Best-effort: a missing scripts dir just means no persistence (the live toggle still
 *  works this session). Re-broadcasts settings so the settings-form switch stays in sync. */
function persistMicPreference(enabled) {
  try {
    const store = activeStore();
    if (!store.scriptsDir) return;
    instancestate.saveSettings(store, { mic: !!enabled });
    broadcastSettings();
  } catch (_) { /* best-effort */ }
}

/** Force-update coding agents on the VM over SSH, with a progress notification,
 *  then re-probe so the new versions + cleared badges show. `ids` narrows the
 *  update to specific agents (the panel's per-agent ↑ tag); omitted = all. */
function runUpdateAgents(ids) {
  const subset = Array.isArray(ids) && ids.length ? ids : null;
  const requested = subset || ["claude-code", "codex", "opencode", "t3code"];
  // Multi-minute npm work followed (sometimes) by a reprovision: ONE target for the
  // settings read, the SSH run and the rebuild, captured before any of them.
  const t = actionTarget();
  const scriptsDir = resolveScriptsDirFor(t.instance);
  let sourceManagedT3 = false;
  try {
    const settings = scriptsDir ? instancestate.readSettings(stateStore(t.instance, scriptsDir)) : {};
    sourceManagedT3 =
      requested.includes("t3code") &&
      settings.t3code === true &&
      settings.t3codeLimitResume === true;
  } catch (_) { /* fall back to the normal updater */ }
  const remotelyUpdated = sourceManagedT3
    ? requested.filter((id) => id !== "t3code")
    : requested;
  if (sourceManagedT3 && remotelyUpdated.length === 0) {
    void startConstructReprovision(scriptsDir, t);
    return;
  }
  const what = subset ? subset.join(", ") : "coding agents";
  const script = updates.buildAgentUpdateScript(remotelyUpdated);
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
  // Update-Construct.ps1 is INSTALL-WIDE (it refreshes the scripts + the extension, not a
  // VM), so its -Repo/-Ref come from the install-wide settings — no instance is involved.
  const markers = updates.readMarkers(host.readRawSettings(scriptsDir));
  const resultFile = path.join(os.tmpdir(), `construct-update-${Date.now()}.result`);
  try { fs.unlinkSync(resultFile); } catch (_) {}
  const ok = lifecycle.launchHostScript({
    scriptsDir, script: "Update-Construct.ps1",
    args: updates.constructRefreshArgs(markers),
    // Pair form of the same args: the command builder quotes VALUES from it, so a
    // repo/ref hand-edited into the settings file can't be read as PowerShell syntax.
    argSpec: updates.constructRefreshArgPairs(markers),
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
async function runStartAndConnect() {
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
  // CAPTURED, with a generation: this flow has a credential lookup and then an SSH poll
  // that runs for up to 150 s, and the user can switch instances at any point in it. The
  // continuation used to start A and then OPEN A in a window that had since moved to B.
  const startTarget = actionTarget();
  const startInstance = startTarget.instance;
  /**
   * Stop, quietly. Unlike the other captured flows this one may already have STARTED a
   * VM — that is not undone (the user asked for it, and it is a safe state to leave a VM
   * in), so the message says what did and did not happen rather than "nothing was done".
   */
  const startSuperseded = () => {
    if (!instances.targetSuperseded(instanceGate, startTarget)) return false;
    const now = activeInstance().name;
    logLine(`instances: "Start & connect" was started for "${startTarget.name}" but the window switched to "${now}" — not opening it (that VM may still be starting)`);
    vscode.window.showWarningMessage(
      `“Start & connect” was started for the “${startTarget.name}” instance, but this window has since switched to “${now}” — it wasn't opened. “${startTarget.name}” may still be starting; switch back and connect from there.`
    );
    return true;
  };
  // A remote instance is started by its host service over HTTPS, which needs the
  // credential driverOpts() resolves; a local one gets {} and the elevated Start-VM it
  // always got.
  const startOpts = await driverOpts(startInstance);
  // The credential lookup can prompt, so it is an unbounded await — and the VM has NOT
  // been asked to start yet, so this abort really does leave both instances untouched.
  if (startSuperseded()) return;
  if (!vmpower.startVm({ debug: debugEnabled(), instance: startInstance, ...startOpts })) {
    // The driver logs why (no service URL, no token, no PowerShell helper). Say so here
    // too: a silent no-op on a button press is the one thing that must not happen.
    if (startOpts.auth) {
      vscode.window.showWarningMessage(
        `Couldn't ask ${(startInstance.service && startInstance.service.url) || "the host service"} to start “${startInstance.name}”. See “The Construct” in the output panel for the reason.`
      );
    }
    return; // startVm surfaces its own failure
  }
  vscode.window.showInformationMessage(
    startOpts.auth
      ? `Asking the host service to start “${startInstance.name}”…`
      : "Starting the Construct VM — approve the UAC prompt."
  );
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Waiting for the Construct VM to come online…", cancellable: true },
    async (_progress, token) => {
      const intervalMs = 4000, maxMs = 150000;
      let waited = 0;
      while (waited < maxMs) {
        if (token.isCancellationRequested) return;
        const reachable = await ssh.isReachable({ timeoutMs: 6000, cfg: startTarget.cfg });
        // Re-checked after EVERY probe — including the successful one, so the check sits
        // immediately before openOnVm with nothing awaited in between. 150 s is a long
        // time to hold a window's identity, and opening the wrong VM is the failure this
        // guards. The poll simply stops: the VM may well be up by now, and it is left
        // running rather than powered back off behind the user's back.
        if (startSuperseded()) return;
        if (reachable) {
          remote.openOnVm({ path: "/root/repos", newWindow: false, cfg: startTarget.cfg });
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
  const ckStore = stateStore(t.instance, scriptsDir);
  try { applied = instancestate.readAppliedAutoCheckpoints(ckStore); } catch (_) { applied = null; }
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
  try { stillWanted = instancestate.readSettings(ckStore).autoCheckpoints === true; } catch (_) { /* keep the captured value */ }
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
  // Refused (a non-default instance this install can't target) => no console runs, so
  // no result file will ever appear: don't start the poller.
  if (lifecycle.run("setCheckpoints", {
    scriptsDir, enabled, instance: t.instance,
    stillCurrent: () => !targetSuperseded(t, "Applying automatic checkpoints"),
    env: { CONSTRUCT_CHECKPOINT_RESULT: resultFile },
  }) === false) return;
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
        try { instancestate.saveAppliedAutoCheckpoints(ckStore, enabled); } catch (e) { logLine(`checkpoints: marker write failed — ${e && e.message ? e.message : e}`); }
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
    // run() refuses (and explains) when this install can't TARGET the active instance;
    // nothing was launched then, so don't start polling for a provisioned commit.
    if (lifecycle.run("reprovision", {
      scriptsDir, projects: selected, instance: t.instance,
      stillCurrent: () => !targetSuperseded(t, "Reprovision"),
    }) === false) return false;
    beginReprovisionFastRefresh(t);
    return true;
  } catch (e) {
    vscode.window.showErrorMessage("Couldn't start reprovision: " + (e && e.message ? e.message : e));
    return false;
  }
}

/**
 * Offer the reprovision that applies provisioning-only settings.
 *
 * The notification below is NOT modal: it can sit in the corner for as long as the user
 * likes, and a switch in the meantime must not redirect the answer. `scriptsDir` and
 * `target` are both captured by the caller BEFORE the toast — they belong to the
 * instance whose settings were just written — so an accepted offer rebuilds that VM with
 * that VM's scripts and .construct-settings.json, and a stale one aborts loudly instead
 * of reconfiguring the machine the window happens to show now.
 */
function offerReprovisionForPatchSettings(scriptsDir, features, target) {
  const t = target || actionTarget();
  const names = features.join(" and ");
  vscode.window.showWarningMessage(
    `Construct settings saved. Changing ${names} requires a reprovision before it takes effect.`,
    "Reprovision now",
  ).then((pick) => {
    const plan = instances.planCapturedFollowUp(instanceGate, t, pick === "Reprovision now");
    if (plan.reason === "superseded") { targetSuperseded(t, "Reprovision"); return; }
    if (!plan.run) return;
    void startConstructReprovision(scriptsDir, plan.target);
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
  // CAPTURED: the profile lookup and the window that gets opened must name one VM.
  const openTarget = actionTarget();
  const projRoot = resolveCfgDir() || resolveScriptsDirFor(openTarget.instance);
  const profile = projRoot ? host.readProjectProfile(projRoot, name) : null;
  remote.openOnVm({ path: remote.projectOpenPath(profile), newWindow: true, cfg: openTarget.cfg });
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
  // CAPTURED BEFORE THE SLOW PART, both of them. The collection is a minutes-long SSH round
  // trip, and the two things that decide what this file IS can change during it: switching
  // instance would present VM A's numbers under B's card (and save them as B's), and
  // switching the period would write daily numbers into a monthly file name. So the
  // instance and the report are taken here, once, and everything below reads only these —
  // the ssh cfg, the file name, the dialog and the confirmation.
  const target = actionTarget();
  const exportOf = usage.describeExport({ report: usageReport, instance: target.name });
  vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Collecting usage from the VM…", cancellable: false },
    async () => {
      const rawText = await usage.collectRaw({ report: exportOf.report, cfg: target.cfg });
      // THE GENERATION IS CHECKED FIRST, before anything is done with the result — including
      // the failure branch. A collection that came back after the window switched is an
      // answer to a question nobody is asking any more, and its generic "couldn't collect
      // usage from the VM" toast would land in the other instance's window as if that VM
      // were the unreachable one. The standard guard names both instances instead.
      if (targetSuperseded(target, "The usage export")) return;
      if (!rawText) {
        vscode.window.showErrorMessage(
          "Couldn't collect usage from the VM. Make sure it's running and reachable, then try again."
        );
        return;
      }
      const uri = await vscode.window.showSaveDialog({
        title: exportOf.dialogTitle,
        filters: { JSON: ["json"], "All files": ["*"] },
        // Default into the home dir; a bare filename in showSaveDialog resolves against
        // the last-used location, so pin it under home for a predictable first save.
        defaultUri: vscode.Uri.file(path.join(os.homedir(), exportOf.fileName)),
      });
      if (!uri) return; // cancelled — nothing written
      // The dialog is an await too, and writing a file is the irreversible step this guard
      // exists for: a switch while it was open must not put one VM's numbers on disk under
      // a path the user chose while looking at another one.
      if (targetSuperseded(target, "The usage export")) return;
      const payload = usage.buildExportPayload(rawText);
      try {
        await fs.promises.writeFile(uri.fsPath, payload, "utf8");
        vscode.window.showInformationMessage(exportOf.savedMessage(uri.fsPath));
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
async function importFromVm(target) {
  // The instance is captured ONCE, before the scan: the SSH cfg used here, the throttle
  // stamp below, and the settings file the discovered profiles are auto-enabled into
  // must all belong to the VM this scan actually talked to.
  const scanTarget = importTargetOf(target);
  const projRoot = resolveCfgDir() || scanTarget.scriptsDir;
  if (!projRoot) return null;
  var r;
  try { r = await ssh.runRemoteScript(projects.buildScanScript(), { timeoutMs: 60000, cfg: scanTarget.cfg }); }
  catch (_) { return null; }
  if (!r || r.code !== 0) return null;
  // The scan describes THAT VM's repos. If the window switched while it ran, none of it
  // may be acted on: not the profile files (they would be written on B's behalf from A's
  // repos), not the throttle stamp (it would suppress B's first scan), not the selection
  // file. Discarding reads as "the VM couldn't be scanned" to the callers, which is the
  // fail-closed answer — the lifecycle pre-flight aborts on its own targetSuperseded
  // check first, with the message that names the instance.
  if (targetStale(scanTarget, "The VM repo scan")) return null;
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
  // Re-checked after the deletion-history git read, immediately before the first
  // mutation: everything below writes files and stamps the per-instance throttle.
  if (targetStale(scanTarget, "The VM repo import")) return null;
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
  importCoalescer.stamp(scanTarget.name);
  if (imported.length) {
    logLine("auto-import from VM: imported " + imported.join(", "));
    // The SCAN's target, captured before the SSH round-trip above: these profiles were
    // found on that VM, so they may only ever be enabled in that VM's settings file.
    await autoEnableNewProfiles(profilesBefore, host.listProjectProfiles(projRoot), scanTarget);
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

/** Broadcast live audio status to every webview (flips the console switch).
 *  `session` is the audioSlot claim the status belongs to (see hostAudioSession): a
 *  status from a session a LATER enable has superseded is dropped, so instance A's
 *  trailing teardown report can't switch B's live tunnel off in every panel. A status
 *  with no session (the ungated call sites: "there is nothing armed") always goes out,
 *  which is what keeps the single-instance path exactly as it was. */
function broadcastAudio(status, session) {
  if (session != null && !audioSlot.owns(session)) {
    logLine(`audio: a status update from a superseded mic session was dropped — the tunnel slot now belongs to "${audioSlot.name}"`);
    return;
  }
  const msg = audioMessage(status, audioStatusInstance(session));
  for (const w of liveWebviews) safePost(w, msg);
}

/**
 * The instance an audio status DESCRIBES — the mic tunnel's `ssh -R` terminates on one VM.
 *
 * The slot check above rejects a status from a session a later enable superseded, but it
 * cannot help a message that has ALREADY been posted: queued behind a full state push for
 * B, A's trailing "tunnel down" would still flip B's console switch. So every audio message
 * carries the instance it is about and the webview discards a mismatch. A status with no
 * session (the ungated call sites — "there is nothing armed") is about this window's
 * current target, which is what it is stamped with.
 */
function audioStatusInstance(session) {
  if (session != null && audioSlot.name) return audioSlot.name;
  return hostAudioInstance || activeInstance().name;
}

/** One builder for every `{type:'audio'}` payload, so the scope stamp cannot be forgotten
 *  at one of the direct (single-webview) call sites. */
function audioMessage(status, instanceName) {
  const msg = {
    type: "audio",
    instance: instanceName || activeInstance().name,
    enabled: !!status.enabled,
    capturing: !!status.capturing,
  };
  if (status.tunnel) msg.tunnel = status.tunnel;
  if (typeof status.gatePatched === "boolean") msg.gatePatched = status.gatePatched;
  return msg;
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
  // `opts.target` is a target the CALLER captured before its own awaits (the auto-arm
  // reads a preference and probes the VM first). Its generation is re-checked below,
  // immediately before the tunnel is created: A's "yes, reachable, mic wanted" must not
  // install the shim and open a microphone tunnel on B, whose own preference may be off.
  // Every caller now goes through requestAudioEnable, which captures at the user's click
  // (or at activation) before queueing on the single-session chain; the fallback capture
  // here covers a direct call.
  const t = opts.target || actionTarget();
  // THE SINGLE-SESSION RULE, decided by the pure instances.planEnable (unit-tested with
  // deferred promises): a HostAudio may never be replaced while it is still the only
  // reference to a live — or half-built — tunnel.
  const plan = instances.planEnable(audioSlotState(), t.name);
  if (plan.action === "report") { broadcastAudio({ enabled: true, capturing: hostAudio.capturing }); return Promise.resolve(); }
  if (opts.target && instances.targetSuperseded(instanceGate, t)) {
    logLine(`audio: the enable for "${t.name}" was discarded — the window switched to "${activeInstance().name}" before it ran`);
    return Promise.resolve();
  }
  if (plan.action !== "create") {
    // "join"   — a session for this window is still being enabled. Await THAT promise
    //            (it reports for itself) and settle this caller's optimistic switch on
    //            what it produced. Constructing a second HostAudio would replace the
    //            module's reference to the first, whose result is then discarded as
    //            superseded (audioSlot) — a live `ssh -R` with nothing able to stop it.
    // "refuse" — the window is closing (the chain is closed), or a session is held with
    //            no enable to join. Either way nothing new may be built over it.
    const pending = plan.action === "join" ? hostAudioEnable : null;
    logLine(`audio: the enable for "${t.name}" ${plan.action === "join" ? "joined" : "declined to replace"} ` +
      `the mic session on "${hostAudioInstance}" (${plan.reason})`);
    return Promise.resolve(pending).then(() => { reportAudioState(webview); });
  }
  micWarnedReasons = new Set(); // fresh enable: allow one warning per failure reason again
  hostAudioInstance = t.name;
  // Claim the one tunnel slot: every callback below carries this id, and a later enable
  // (the next switch) invalidates it — see broadcastAudio and `handle`.
  const session = audioSlot.claim(t.name);
  hostAudioSession = session;
  hostAudio = new audio.HostAudio({
    // The mic tunnel is per-INSTANCE: the reverse forward lands on the VM this enable
    // was captured for. The VM-side port range (8767+) is per VM, so two instances
    // never contend for it; only the host-side cfg has to follow the switch.
    cfg: t.cfg,
    mic: makeMicProvider(),
    onStatus: (s) => broadcastAudio(s, session),
  });
  const handle = (r) => {
    // A result that lands after a LATER enable claimed the slot describes a session this
    // window has already moved on from: reporting it would flip the new tunnel's switch
    // off, and clearing `hostAudio` would drop the reference to the new HostAudio —
    // leaving its tunnel open with nothing left to dispose it.
    if (!audioSlot.owns(session)) {
      logLine(`audio: the enable for "${t.name}" finished after the mic tunnel moved to "${audioSlot.name}" — result discarded`);
      return;
    }
    if (!r.ok) {
      // Reset the switch to off on every surface.
      hostAudio = undefined;
      hostAudioInstance = null;
      hostAudioSession = null;
      hostAudioEnable = null;
      safePost(webview, audioMessage({ enabled: false, capturing: false }, t.name));
      broadcastAudio({ enabled: false, capturing: false }, session);
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
  // ONE enable promise per session, published before anything can await it: the teardown
  // (disableAudio) waits for it to settle before it disables this HostAudio, so a switch
  // can never disable a half-enabled session and let the enable finish its tunnel behind
  // the teardown — an orphan `ssh -R` on the instance we left. Both paths below consume
  // the same promise; the SSH work and its argv are unchanged either way.
  const started = hostAudio.enable();
  hostAudioEnable = Promise.resolve(started).then(() => null, () => null);
  if (opts.auto) {
    // No notification progress on startup — auto-arm must be invisible until it succeeds.
    return started.then(handle, () => handle({ ok: false, error: "enable-failed" })).catch(() => {});
  }
  // RETURNED (it was fire-and-forget), so the single-session chain that queued this
  // enable does not consider the step finished while the tunnel is still coming up —
  // the next queued step would otherwise decide its teardown against a half-built
  // session. Nothing on the single-instance path awaits it, so the toggle is unchanged.
  return Promise.resolve(vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Enabling microphone passthrough…", cancellable: false },
    async () => { handle(await started); }
  )).catch(() => {});
}

/** The mic slot as instances.planEnable reads it: the module's own four variables plus
 *  the chain's shutdown flag. Kept in one place so the decision and the state it is taken
 *  from can never drift apart. */
function audioSlotState() {
  return {
    live: !!hostAudio,
    name: hostAudioInstance,
    enabled: !!(hostAudio && hostAudio.enabled),
    pending: !!hostAudioEnable,
    closed: audioHandover.closed,
  };
}

/** Report the live mic state to `webview` (and every surface) — used when an enable
 *  JOINED a session that was already being enabled, so the optimistic switch settles on
 *  what really happened instead of on its own guess. */
function reportAudioState(webview) {
  const on = !!(hostAudio && hostAudio.enabled);
  const status = { enabled: on, capturing: on ? !!hostAudio.capturing : false };
  if (webview) safePost(webview, audioMessage(status, audioStatusInstance(hostAudioSession)));
  broadcastAudio(status, hostAudioSession);
}

/**
 * The console/settings toggle's "on", and the startup/repatch auto-arm, queued on the
 * SAME single-session chain the switch handover uses (audioHandover). That is what keeps
 * a manual enable from constructing a second HostAudio for a VM whose session is still
 * being torn down or brought up — see instances.createHandover.
 *
 * `opts.auto` marks the silent auto-arm (it evaluates the saved preference and probes the
 * VM first); a manual enable enables unconditionally and keeps its toasts.
 */
function requestAudioEnable(context, webview, opts = {}) {
  // The target is captured HERE — at the user's click / at activation — not when the
  // queued step finally runs, so an enable can never be applied to a VM the window moved
  // to in between (the chain re-checks it and aborts instead).
  const target = opts.target || actionTarget();
  return audioHandover.enable(target, (t) => (
    opts.auto ? maybeAutoEnableAudio(context, t) : enableAudio(context, webview, { target: t })
  ));
}

/** The console/settings toggle's "off", queued on the same chain (so it can never
 *  overtake an enable that is still waiting in it). */
function requestAudioDisable() {
  return audioHandover.disable();
}

/** Auto-arm mic passthrough on startup when the saved preference (micPassthrough in
 *  .construct-settings.json, the settings-form "Microphone passthrough" toggle) is on.
 *  Best-effort and QUIET: gated on the VM being reachable so a down VM never toasts;
 *  the user can still toggle manually. */
async function maybeAutoEnableAudio(context, target) {
  try {
    if (hostAudio && hostAudio.enabled) return;
    // Instance, cfg, scripts dir and generation are captured BEFORE the preference is
    // read: micPassthrough lives in that instance's .construct-settings.json and the
    // probe below dials that instance's VM, so both answers describe one machine.
    const t = target || actionTarget();
    const scriptsDir = resolveScriptsDirFor(t.instance);
    if (!scriptsDir) return;
    const raw = instancestate.readState(stateStore(t.instance, scriptsDir));
    if (!raw || raw.micPassthrough !== true) return;
    const reachable = await ssh.isReachable({ timeoutMs: 6000, cfg: t.cfg });
    // A result that arrives after a switch is DISCARDED, not applied to the new
    // instance — the same rule the refresh pipelines use, decided by the pure
    // instances.planCapturedFollowUp. Silent either way: an auto-arm never toasts.
    const plan = instances.planCapturedFollowUp(instanceGate, t, reachable);
    if (!plan.run) {
      if (plan.reason === "superseded") {
        logLine(`audio: auto-arm for "${t.name}" was discarded — the window switched to "${activeInstance().name}" while probing`);
      }
      return; // VM down (or superseded) — stay off silently
    }
    // RETURNED, so a chain step that queued this arm stays open until the tunnel is
    // really up (the enable itself is what the next step's teardown has to wait for).
    return enableAudio(context, undefined, { auto: true, target: plan.target });
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
  // One target for the whole pass: the settings that say WHICH patches are wanted, the
  // SSH repair that applies them and the auto-arm retry all belong to one VM. Captured
  // before the first await; a switch during the pass discards the rest of it.
  const t = actionTarget();
  const scriptsDir = resolveScriptsDirFor(t.instance);
  const raw = scriptsDir ? instancestate.readState(stateStore(t.instance, scriptsDir)) : {};
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
      cfg: t.cfg,
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
    // A live gate re-patch means the console's mic substatus is authoritative again --
    // for the VM we just patched. A tunnel that now belongs to another instance (the
    // window switched while the repair ran) must not be relabelled from this result.
    if (res.repaired.mic && hostAudio && hostAudioInstance === t.name) {
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
    if (instances.targetSuperseded(instanceGate, t)) {
      logLine(`repatch: the pass for "${t.name}" finished after a switch — skipping the auto-arm retry.`);
      return;
    }
    logLine("repatch: mic passthrough is on but no tunnel is live — retrying auto-arm.");
    void requestAudioEnable(context, undefined, { auto: true, target: t });
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
  // The selection is written to ONE instance's state, and the QuickPick below is an
  // unbounded await: capture the target up front so a switch while it is open cannot
  // land A's ticks in B's store.
  const selTarget = actionTarget();
  const scriptsDir = resolveScriptsDirFor(selTarget.instance);
  if (!scriptsDir) { warnNoScriptsDir(); return; }
  const selStore = stateStore(selTarget.instance, scriptsDir);
  // Profile listing comes from cfgDir (where profiles now live); the SELECTION is
  // per VM, so it is read and written through that instance's own state store.
  const profileRoot = resolveCfgDir() || scriptsDir;
  const available = host.listProjectProfiles(profileRoot);
  if (!available.length) {
    vscode.window.showInformationMessage("No project profiles found. New repos are auto-discovered from the VM, or use \u201c+ add project\u201d to add one.");
    return;
  }
  const selected = new Set(instancestate.readSelectedProjects(selStore));
  const items = available.map((name) => ({ label: name, picked: selected.has(name) }));
  const picks = await vscode.window.showQuickPick(items, {
    canPickMany: true,
    title: "Select project profiles",
    placeHolder: "Ticked profiles are recorded for the next Reprovision / Reinstall (the running VM isn't changed).",
  });
  if (picks == null) return; // cancelled — leave the stored selection untouched
  const chosen = projects.reconcileSelection(picks.map((p) => p.label), available);
  if (targetSuperseded(selTarget, "Selecting project profiles")) return;
  try {
    instancestate.saveSelectedProjects(selStore, chosen);
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
  // DELIBERATELY UNSCOPED. Unlike forwards/audio/settings/idlePolicy this is not a
  // per-instance broadcast: it is a direct reply to ONE webview's own "edit this profile"
  // request, and profiles live in the SINGLE host config repo
  // (%LOCALAPPDATA%\The-Construct\config\projects) that every instance shares — see
  // docs/config-sync.md "Multiple instances", where only the VM-side BRANCH is per
  // instance. Stamping it would make a modal opened before a switch refuse to populate
  // after one, for a resource the switch did not change. (The `|| resolveScriptsDir()`
  // fallback above is only reached when no %LOCALAPPDATA%/%TEMP% resolves at all, i.e.
  // when there is no config repo for any instance either.)
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
  // Captured before the modal: the deletion is synced to the VM the user deleted it
  // FOR, on that instance's branch, however long the confirmation sits open.
  const delTarget = actionTarget();
  const projRoot = resolveCfgDir() || resolveScriptsDirFor(delTarget.instance);
  const scriptsDir = resolveScriptsDirFor(delTarget.instance);
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
    const delStore = stateStore(delTarget.instance, scriptsDir);
    const selected = instancestate.readSelectedProjects(delStore).filter((n) => n !== safe);
    instancestate.saveSelectedProjects(delStore, projects.reconcileSelection(selected, available));
    const syncResult = await runConfigSync(delTarget);
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

/** Disable mic passthrough: stop capture + tunnel, revert the VM shim + patch.
 *  RETURNS the teardown promise (never rejects): a switch has to be able to sequence
 *  the destination's arm AFTER the instance it left has actually let go of its tunnel,
 *  instead of racing it. Nothing on the single-instance path awaits it, so the toggle
 *  behaves exactly as before. */
function disableAudio() {
  if (!hostAudio) { broadcastAudio({ enabled: false, capturing: false }); return Promise.resolve(); }
  const inst = hostAudio;
  // The session this teardown reports for: once a later enable claims the slot, this
  // instance's "disabled" must not overwrite the new tunnel's status (broadcastAudio).
  const session = hostAudioSession;
  // ...and the enable that may still be in flight ON THIS SESSION (never rejects).
  const pendingEnable = hostAudioEnable;
  hostAudio = undefined;
  hostAudioInstance = null;
  hostAudioSession = null;
  hostAudioEnable = null;
  return Promise.resolve(vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Disabling microphone passthrough…", cancellable: false },
    async () => {
      // Let a half-finished enable COMPLETE before disabling it. HostAudio.disable()
      // tears down what exists when it runs: mid-enable that is nothing yet, and the
      // enable would then open its AudioSession and `ssh -R` after the teardown had
      // already passed — a live tunnel to the instance we left that no reference can
      // reach. Bounded by the enable's own SSH timeouts.
      if (pendingEnable) await pendingEnable;
      const r = await inst.disable();
      if (!r.ok) {
        vscode.window.showWarningMessage(
          "Microphone passthrough is off locally, but the VM cleanup (removing the shim / reverting the patch) may not have completed. Re-enable and disable once the VM is reachable to fully clean up."
        );
      }
      broadcastAudio({ enabled: false, capturing: false }, session);
    }
  )).catch(() => {});
}

// ── Switching the active instance ───────────────────────────────────────────
// Selection is PER WINDOW (workspaceState), so two windows can drive two VMs at once;
// `construct.instance` is a global override for people who want one answer everywhere.
// A switch must invalidate every VM-derived cache, retarget the long-lived connections
// (notification watcher, mic tunnel), and re-probe — otherwise the panel would show the
// previous VM's status pills, versions, projects and usage under the new name.

/**
 * Update (or hide) the status-bar instance indicator. Hidden with exactly one instance,
 * so a single-VM install's status bar is unchanged.
 *
 * With several instances it also carries HOW MANY of them are behind the installed
 * Construct — "(n to reprovision)" — because the yellow Reprovision button only ever
 * speaks for the ACTIVE one, and a VM you have not switched to in a while is exactly the
 * one that silently falls behind. The count comes from the per-instance host caches
 * (instancestate.countStale): no SSH fan-out, so it is free to run on every refresh and
 * is right for a VM that is switched off. Omitted at 0, which is the common state.
 */
function syncInstanceStatusItem() {
  try {
    if (!instanceStatusItem) return;
    const reg = registryNow();
    const all = instances.list(reg);
    if (all.length < 2) { instanceStatusItem.hide(); return; }
    const inst = activeInstance();
    let stale = 0;
    try { stale = instancestate.countStale(all.map((i) => stateStore(i))); } catch (_) { stale = 0; }
    instanceStatusItem.text = "$(vm) " + inst.name + (stale > 0 ? ` (${stale} to reprovision)` : "");
    instanceStatusItem.tooltip = `The Construct instance: ${inst.name} (${inst.vmHost}:${inst.sshPort}) — click to switch` +
      (stale > 0 ? `\n${stale} instance(s) were provisioned with an older Construct — switch to one and reprovision.` : "");
    instanceStatusItem.show();
  } catch (_) { /* a status-bar item is never worth an exception */ }
}

/** Point this window at `name`. Persists the choice, retargets the live connections,
 *  and re-probes every surface. A no-op when the name is already active or unknown. */
async function switchInstance(name) {
  const reg = registryNow(true);
  const wanted = String(name || "").trim();
  // OWN-property membership (instances.hasInstance): the name comes from the webview or
  // a command argument, and a plain `byName[name]` would accept "constructor" or
  // "toString" as a registry entry and then persist a selection nothing can resolve.
  if (!instances.hasInstance(reg, wanted)) {
    vscode.window.showWarningMessage(`"${wanted}" is not a Construct instance in the registry.`);
    return;
  }
  if (activeInstance().name === wanted) return;
  // Read the pin BEFORE reporting: with `construct.instance` pinned to another instance
  // the window does not move at all, so a failed write must not be reported as a switch
  // that took effect "for this window" — the pin warning below is the accurate half.
  // The pin only counts when the REGISTRY HOLDS IT (instances.effectivePin, the same
  // own-property membership resolveActive uses): a stale setting naming a removed
  // instance pins nothing, and reporting it would contradict the window's own active
  // target — both warnings therefore key off this one value.
  const setting = instanceSetting();
  const pin = instances.effectivePin(reg, setting);
  if (setting && !pin) {
    logLine(`instances: the "construct.instance" setting names "${setting}", which is not in the registry — it pins nothing`);
  }
  let persisted = true;
  try { await extensionContext.workspaceState.update(ACTIVE_INSTANCE_KEY, wanted); }
  catch (e) {
    // Without persistence the switch would silently revert on the next reload — say so.
    // And say it TRUTHFULLY: a warning that the window switched "for now" while nothing
    // held the new selection left activeInstance() resolving the PREVIOUS instance, so
    // the refresh below re-rendered the VM the user had just switched away from. The
    // window-local override is what makes the message true (planSwitchPersistence).
    persisted = false;
    logLine("instances: could not persist the active instance — " + (e && e.message ? e.message : e));
  }
  const persistence = instances.planSwitchPersistence(wanted, persisted, pin);
  windowInstanceOverride = persistence.override;
  if (persistence.message) vscode.window.showWarningMessage(persistence.message);
  if (pin && pin !== wanted) {
    // Honesty: the setting outranks workspaceState, so the switch would silently not
    // take effect. Say so rather than leave the user staring at the old VM.
    vscode.window.showWarningMessage(
      `The "construct.instance" setting pins every window to "${pin}", so this window still uses it. Clear that setting to switch per window.`
    );
  }
  logLine(`instances: active instance -> ${activeInstance().name}`);
  // Through the ONE chain, like every other route that retargets this window.
  await queueInstanceTransition(onInstanceChanged);
}

// ── Observing the registry ──────────────────────────────────────────────────
// instances.json belongs to no single process: another window's picker writes it,
// Auto-Install.ps1 records a VM into it, a hand edit changes the default. None of that
// raises a VS Code event, so a window whose target changed underneath it would keep
// probing, watching, tunnelling and forwarding the machine it no longer drives.
//
// ZERO-CHANGE: no watcher is started when the file does not exist — a single-VM install
// has no registry, so it never opens one, and nothing about its behaviour changes. (A
// file created later is picked up by the periodic check in refreshTick, which is the
// same comparison at the refresh cadence rather than immediately.)
let registryWatcher = null;
let registryWatchTimer = null;

/** Watch the registry file for writes by other processes. Best-effort: a failed watch
 *  simply leaves the refreshTick check as the only detector. */
function startRegistryWatch() {
  if (registryWatcher) return;
  let file = null;
  try { file = instances.instancesPath(process.env); } catch (_) { file = null; }
  if (!file) return;
  // The DIRECTORY, not the file: the registry is written atomically (tmp + rename), and a
  // watch on the inode would survive exactly one write. Only starts once the file exists,
  // so a default install with no %LOCALAPPDATA%\The-Construct\instances.json opens nothing.
  try { if (!fs.existsSync(file)) return; } catch (_) { return; }
  const dir = path.dirname(file);
  const base = path.basename(file);
  try {
    registryWatcher = fs.watch(dir, { persistent: false }, (_evt, name) => {
      // fs.watch reports the file name on Windows and Linux; a null name (some platforms)
      // is treated as "might be ours" rather than ignored.
      if (name && String(name) !== base) return;
      if (registryWatchTimer) clearTimeout(registryWatchTimer);
      // Debounced: a tmp+rename write fires two or three events, and a writer may be
      // mid-rewrite when the first one lands.
      registryWatchTimer = setTimeout(() => {
        registryWatchTimer = null;
        registryNow(true);                 // the change is the whole point: re-read it
        retargetIfChanged("the instance registry changed on disk");
      }, 400);
    });
    registryWatcher.on("error", () => {});
    logLine(`instances: watching ${file} for changes made by other processes`);
  } catch (_) { registryWatcher = null; }
}

function stopRegistryWatch() {
  if (registryWatchTimer) { try { clearTimeout(registryWatchTimer); } catch (_) {} registryWatchTimer = null; }
  if (registryWatcher) { try { registryWatcher.close(); } catch (_) {} registryWatcher = null; }
}

/**
 * The mic tunnel's handover across a switch (instances.createHandover), serialized:
 * tear the instance we left off its tunnel, THEN evaluate the destination's own saved
 * preference against the destination's target. Every switch goes through this one chain,
 * so A→B→C can neither leave B's tunnel behind nor let B's arm win over C's.
 */
const audioHandover = instances.createHandover({
  session: () => ({ live: !!hostAudio, name: hostAudioInstance }),
  teardown: () => disableAudio(),
  arm: (target) => maybeAutoEnableAudio(extensionContext, target),
  superseded: (target) => instances.targetSuperseded(instanceGate, target),
});

/**
 * THE ONE SERIALIZED TRANSITION. Every route that can change which VM this window drives
 * — the picker/command (switchInstance), the `construct.instance` setting, and a registry
 * rewritten by ANOTHER process (startRegistryWatch / refreshTick → retargetIfChanged) — hands over here,
 * one at a time. Serializing matters because the handovers below are async chains: two
 * overlapping transitions would interleave a teardown of A with an arm of C and leave the
 * mic tunnel or the forwarder pointed at B.
 */
let instanceTransition = Promise.resolve();
function queueInstanceTransition(step) {
  instanceTransition = instanceTransition.then(step, step);
  return instanceTransition;
}

/**
 * THE TARGET THE LIVE SESSIONS WERE LAST HANDED OVER TO — a fingerprint, not a name.
 * `null` until activation records the starting one, which is what makes the first check a
 * no-op instead of a spurious retarget.
 */
let appliedTargetFingerprint = null;
/** ...and the one the LAST QUEUED transition will move them to. Without it, a second
 *  observation arriving before the queued transition has run (the watcher and the refresh
 *  tick can both see the same write) would queue a duplicate transition for a change
 *  already on its way. */
let queuedTargetFingerprint = null;

/**
 * Has the ACTIVE TARGET changed since the last transition, and if so, run one.
 *
 * The registry is a file other processes write: another window can flip `defaultInstance`,
 * an installer can remove the selected entry (this window then resolves to another VM), and
 * a remote rebuild can rewrite `sshHost`/`sshPort` under the SAME name. None of those raise
 * a VS Code event, and the last one doesn't even change the name — so the change is detected
 * by comparing the COMPLETE normalized identity (instances.targetFingerprint).
 *
 * Returns true when a transition was queued (the caller must not also refresh: the
 * transition ends in its own refreshAll). A no-op rewrite — same bytes, or a formatting
 * change that normalizes to the same target — yields the same fingerprint and does nothing
 * at all, which is what keeps a single-VM window from ever taking this path.
 */
function retargetIfChanged(why) {
  let fp;
  try { fp = activeFingerprint(); } catch (_) { return false; }
  if (appliedTargetFingerprint === null) { appliedTargetFingerprint = fp; queuedTargetFingerprint = fp; return false; }
  if (fp === queuedTargetFingerprint) return false;
  queuedTargetFingerprint = fp;
  logLine(`instances: the active target changed (${why}) — retargeting this window's sessions to "${activeInstance().name}"`);
  void queueInstanceTransition(onInstanceChanged);
  return true;
}

/** Re-target everything that holds a per-VM connection or cache, then re-render. */
async function onInstanceChanged() {
  const inst = activeInstance();   // also bumps instanceGate, invalidating live tokens
  // The IDENTITY, not the name, is what the live sessions are bound to: a notification
  // stream, a mic tunnel and a forwarding transport all terminate on an ENDPOINT. A
  // registry entry rewritten under the same name (a rebuilt remote VM comes back on a new
  // sshHost/sshPort) leaves every name comparison below equal while all three still point
  // at the machine that no longer exists — so a changed fingerprint hands them over too.
  const fingerprint = instances.targetFingerprint(inst);
  const identityChanged = appliedTargetFingerprint !== null && fingerprint !== appliedTargetFingerprint;
  appliedTargetFingerprint = fingerprint;
  queuedTargetFingerprint = fingerprint;   // a switch retargets too: don't observe it twice
  // Did the DESTINATION change since the mic was last evaluated? A window that has not
  // actually changed instance (the construct.instance setting was edited to the name it
  // already resolves to) must do nothing new — that is the single-VM path, which never
  // switches at all. The gate's own name can't answer this: activeInstance() above (and
  // switchInstance's log line before it) has already moved it to the new name.
  const micSwitched = inst.name !== audioTargetInstance || identityChanged;
  // VM-derived caches: the probe, the update check's markers and the usage table are
  // all "about a VM", so serving the previous one's results would be a lie.
  inflightProbe = null;
  try { usage.clearCache(); } catch (_) {}
  gitDetected = null; gitDetectedAt = 0;
  cachedConfigSync = null;
  cachedIdlePolicy = null; cachedIdlePolicyInstance = null;
  // The notification watcher is one long-lived SSH connection to ONE VM: reconnect it
  // to the new instance (its spool lives on that VM, so nothing is lost on the old one).
  if (notifyInstance !== inst.name || identityChanged) {
    stopNotifyWatch();
    if (notificationsEnabled()) startNotifyWatch();
  }
  // The mic tunnel likewise terminates on one VM. Drop it and let the DESTINATION's own
  // saved preference re-arm it (quietly — same rules as the startup auto-arm). The arm
  // is evaluated on every switch, not only when a tunnel existed: a startup arm that
  // produced none (instance A unreachable) used to mean B's micPassthrough was never
  // honoured at all. The target is captured for the instance we just switched TO, so the
  // arm reads that VM's preference and dials that VM even if another switch beats the
  // probe home; the chain runs in the background so the panel refresh below is not held
  // behind an SSH teardown.
  if (micSwitched) {
    audioTargetInstance = inst.name;
    void audioHandover.switch(instances.captureTarget(instanceGate, inst));
  }
  // The forwarder holds `ssh -L` tunnels to ONE VM and owns a claim in that VM's spool.
  // Retarget it: the old instance's ports must not stay open on this PC pointing at a VM
  // this window no longer drives, and the claim has to be handed back so another window
  // can take over immediately. The teardown is QUEUED on the chain (never run beside it),
  // and the destination is deliberately NOT started here — this window has established
  // nothing about that VM yet. The refresh below takes the reading that starts it.
  if (forwarderInstance !== inst.name || forwarderArmed !== inst.name || identityChanged) {
    void requestForwarderStop();
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
  // The instance this window is ATTACHED to over Remote-SSH — not necessarily the active
  // one, since adoption only preselects it and the user can switch away.
  const connectedName = instances.connectedInstanceName(reg, safeRemoteAuthority());
  const pick = await vscode.window.showQuickPick(
    all.map((i) => ({
      label: (i.name === current ? "$(check) " : "") + i.name,
      description: i.vmHost + (i.sshPort === 22 ? "" : ":" + i.sshPort),
      detail: i.backend +
        (i.name === reg.defaultInstance ? " · registry default" : "") +
        (i.name === connectedName ? " · connected (this window)" : ""),
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
      // The FULL identity, like every other gate write: the selection is already
      // persisted, so activeInstance() resolves to the adopted instance and points the
      // gate at it (name + fingerprint) in one bump rather than two.
      activeInstance();
    } else if (res.adopt && res.error) {
      logLine(`instances: could not adopt the attached VM's instance (${res.error})`);
    }
  } catch (_) { /* best-effort: never break activation */ }
}

/** The "register this VM" offer for THIS window, or null. The DECISION is pure
 *  (instances.planRegisterAttachedVm); this only reads what vscode knows. */
function registerThisVmOffer() {
  try {
    const plan = instances.planRegisterAttachedVm(
      registryNow(), safeRemoteAuthority(), instanceSetting(), activeInstance().name);
    if (!plan.offer) return null;
    return { host: plan.host, suggestedName: plan.suggestedName };
  } catch (_) { return null; }
}

/**
 * Name the VM this window is attached to and write its registry entry — the one action
 * offered when Remote-SSH lands on a host the registry does not know.
 *
 * The entry goes through instances.addInstance/save, i.e. THE SAME writer and the same
 * rules the PowerShell installer uses (lib/AgentVm.Instances.ps1): a local instance's
 * identity is derived from its name, so if this window's host is not that derivation the
 * plan REFUSES with the reason rather than writing an entry the reader would drop.
 */
async function runRegisterThisVm() {
  const offer = registerThisVmOffer();
  if (!offer) {
    vscode.window.showInformationMessage(
      "This window isn't attached to an unregistered VM, so there is nothing to register.");
    return;
  }
  const typed = await vscode.window.showInputBox({
    title: "Register this VM as a Construct instance",
    prompt: `The VM at ${offer.host} is recorded under this name, and the panel switches to it.`,
    value: offer.suggestedName,
    ignoreFocusOut: true,
    validateInput: (v) => (instances.isValidName(String(v == null ? "" : v).trim()) ? null : instances.NAME_RULE),
  });
  const chosen = String(typed == null ? "" : typed).trim();
  if (!chosen) return;   // cancelled
  const reg = registryNow(true);
  const plan = instances.planLocalRegistration(reg, chosen, offer.host);
  if (!plan.ok) { vscode.window.showWarningMessage(plan.reason); return; }
  let file = null;
  try { file = instances.instancesPath(process.env); } catch (_) { file = null; }
  if (!file) {
    vscode.window.showErrorMessage("Could not resolve %LOCALAPPDATA%\\The-Construct\\instances.json, so the instance can't be recorded.");
    return;
  }
  try {
    instances.save(file, instances.addInstance(reg, plan.name, plan.entry));
  } catch (e) {
    vscode.window.showErrorMessage(`Could not register "${plan.name}": ${e && e.message ? e.message : e}`);
    return;
  }
  logLine(`instances: registered "${plan.name}" (${offer.host}) in ${file}`);
  await switchInstance(plan.name);
  vscode.window.showInformationMessage(`Registered "${plan.name}" — the panel now describes this VM.`);
}

// ── Remote hosts (`constructd`) ─────────────────────────────────────────────
// A remote HOST is an enrolment, not a VM: its URL, the credential kind, the pinned
// certificate fingerprint and the identity the service confirmed. It lives in
// globalState and NOT in instances.json, because that file describes VMs — an entry for
// a host with no VM would show up in the instance picker as a machine nothing can
// reach, and both registry readers would have to invent a meaning for it. The pinned
// fingerprint is ALSO written to the file lib/AgentVm.Remote.ps1 reads, so a host added
// here is already trusted when Auto-Install.ps1 runs in a console.
const REMOTE_HOSTS_KEY = "construct.remoteHosts";

/** The enrolled remote hosts, newest first. Never throws. */
function remoteHosts() {
  try {
    const raw = extensionContext && extensionContext.globalState.get(REMOTE_HOSTS_KEY);
    return Array.isArray(raw) ? raw.filter((h) => h && typeof h.url === "string") : [];
  } catch (_) { return []; }
}

/** Record (or refresh) one enrolled host. */
async function saveRemoteHost(entry) {
  const rest = remoteHosts().filter((h) => h.url !== entry.url);
  await extensionContext.globalState.update(REMOTE_HOSTS_KEY, [entry, ...rest]);
}

/** lib/AgentVm.Remote.ps1 in the installed scripts dir — the Negotiate provider needs
 *  it (Node has no SSPI). null when it isn't there, which the callers report. */
function remoteLibPath() {
  try {
    const dir = resolveScriptsDir();
    if (!dir) return null;
    const p = path.join(dir, "lib", "AgentVm.Remote.ps1");
    return fs.existsSync(p) ? p : null;
  } catch (_) { return null; }
}

/**
 * Build a client for a host: the recorded credential kind, with the token fetched from
 * SecretStorage when that is the kind. Returns null (after a message) when the token is
 * gone — silently falling back to Negotiate would ask the service a question it has
 * already refused once.
 */
async function remoteClientFor(hostEntry, overrideAuth) {
  let auth = overrideAuth;
  if (!auth) {
    if (hostEntry.auth === "token") {
      const token = await extensionContext.secrets.get(remotehost.tokenSecretKey(hostEntry.url));
      if (!token) {
        vscode.window.showWarningMessage(
          `The API token for ${hostEntry.url} is no longer stored. Run "The Construct: Add Remote Host" again to re-enter it.`
        );
        return null;
      }
      auth = { kind: "token", token };
    } else {
      auth = { kind: "negotiate" };
    }
  }
  return remotehost.createClient({
    baseUrl: hostEntry.url, auth, pin: hostEntry.fingerprint,
    remoteLib: remoteLibPath(), env: process.env, log: logLine,
  });
}

/**
 * "The Construct: Add Remote Host" — URL → fingerprint → credentials → whoami.
 *
 * The fingerprint is shown ONCE and confirmed by the user (a self-signed certificate has
 * no chain to validate, so this confirmation IS the identity check); a CHANGED
 * fingerprint on a host that was already pinned is refused outright rather than offered
 * as a choice. Credentials are tried Negotiate-first, silently, and only a 401 leads to
 * a prompt — exactly like the installer's console flow.
 */
async function runAddRemoteHost() {
  const typed = await vscode.window.showInputBox({
    title: "Add a Construct remote host",
    prompt: "The host service's address — your administrator publishes it",
    placeHolder: "https://buildbox.example.local:7462",
    ignoreFocusOut: true,
  });
  if (!typed) return;

  let url;
  try { url = remotehost.normalizeServiceUrl(typed); }
  catch (e) { vscode.window.showErrorMessage(e.message); return; }

  // ── the certificate ───────────────────────────────────────────────────────
  let fingerprint = "";
  try {
    fingerprint = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: `Reading ${remotehost.urlParts(url).host}'s certificate…`, cancellable: false },
      () => remotehost.fetchFingerprint(url)
    );
  } catch (e) {
    vscode.window.showErrorMessage(e && e.message ? e.message : String(e));
    return;
  }
  if (fingerprint) {
    const pinned = remotehost.readPin(url, { env: process.env });
    if (pinned && !remotehost.fingerprintsMatch(pinned, fingerprint)) {
      vscode.window.showErrorMessage(
        `The certificate of ${remotehost.urlParts(url).host} does not match the one pinned on this machine.\n` +
        `pinned: ${pinned}\npresented: ${fingerprint}\n` +
        "Refusing to continue. If it was legitimately replaced, delete the pin file in %LOCALAPPDATA%\\The-Construct\\remote and try again."
      );
      return;
    }
    if (!pinned) {
      const CONFIRM = "Pin & continue";
      const pick = await vscode.window.showWarningMessage(
        `Confirm ${remotehost.urlParts(url).host}'s certificate fingerprint`,
        {
          modal: true,
          detail: `${fingerprint}\n\nThe host service uses a self-signed certificate, so it is identified by this ` +
            "SHA-256 fingerprint. Compare it with what the host's administrator published. It is pinned once and " +
            "then enforced on every later call.",
        },
        CONFIRM
      );
      if (pick !== CONFIRM) return;
      try { remotehost.writePin(url, fingerprint, { env: process.env }); }
      catch (e) { vscode.window.showWarningMessage("The host was added, but its fingerprint couldn't be stored: " + e.message); }
    }
  } else {
    const GO = "Continue anyway";
    const pick = await vscode.window.showWarningMessage(
      "That address uses plain http, so the host's identity cannot be verified.",
      { modal: true, detail: "Use https for anything but a local development service." },
      GO
    );
    if (pick !== GO) return;
  }

  // ── credentials: Negotiate first, silently ────────────────────────────────
  const attempt = async (auth) => {
    const client = remotehost.createClient({
      baseUrl: url, auth, pin: fingerprint, remoteLib: remoteLibPath(), env: process.env, log: logLine,
    });
    try { return { ok: true, me: await client.whoami(), auth }; }
    catch (e) { return { ok: false, status: e && e.status, message: e && e.message ? e.message : String(e) }; }
  };

  let result = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Signing in with your Windows account…", cancellable: false },
    () => attempt({ kind: "negotiate" })
  );
  let token = null;
  if (!result.ok && result.status !== 401) {
    vscode.window.showErrorMessage(result.message);
    return;
  }
  while (!result.ok) {
    const TOKEN = "Paste an API token";
    const CRED = "Sign in with a domain account";
    const how = await vscode.window.showQuickPick([TOKEN, CRED], {
      title: "The host service did not accept your Windows account",
      placeHolder: "How would you like to sign in?",
      ignoreFocusOut: true,
    });
    if (!how) return;
    let auth = null;
    if (how === TOKEN) {
      const t = await vscode.window.showInputBox({
        title: "API token", prompt: "The token your administrator issued for this host",
        password: true, ignoreFocusOut: true,
      });
      if (!t) return;
      auth = { kind: "token", token: t };
    } else {
      const user = await vscode.window.showInputBox({
        title: "Domain account", prompt: "User name", placeHolder: "DOMAIN\\you", ignoreFocusOut: true,
      });
      if (!user) return;
      const password = await vscode.window.showInputBox({
        title: "Domain account", prompt: `Password for ${user}`, password: true, ignoreFocusOut: true,
      });
      if (password == null) return;
      auth = { kind: "credential", user, password };
    }
    result = await attempt(auth);
    if (result.ok && auth.kind === "token") token = auth.token;
    if (!result.ok && result.status !== 401) { vscode.window.showErrorMessage(result.message); return; }
    if (!result.ok) vscode.window.showWarningMessage("The host service refused that credential.");
  }

  const me = result.me || {};
  if (me.known === false) {
    vscode.window.showErrorMessage(
      `${url} authenticated you as "${me.name}" but you are not enrolled on it. Ask its administrator to add you.`
    );
    return;
  }
  // Only a VERIFIED token is stored.
  if (token) await extensionContext.secrets.store(remotehost.tokenSecretKey(url), token);
  // A domain-credential sign-in is per-run, so the durable record says "negotiate":
  // the password is never stored, and the next run tries the Windows identity again.
  await saveRemoteHost({
    url, auth: token ? "token" : "negotiate", fingerprint,
    identity: String(me.name || ""), role: String(me.role || ""),
    maxVms: typeof me.maxVms === "number" ? me.maxVms : null,
    addedAt: new Date().toISOString(),
  });
  logLine(`remotehost: added ${url} as ${me.name} (${me.role})`);
  vscode.window.showInformationMessage(
    `Added the Construct host ${remotehost.urlParts(url).host} — signed in as ${me.name}` +
    (typeof me.maxVms === "number" ? ` (VM quota: ${me.maxVms})` : "") +
    ". Create a VM on it with “The Construct: New VM on Remote Host”."
  );
}

/**
 * "The Construct: New VM on Remote Host" — pick a host, ask name/CPU/RAM/disk, then
 * launch Auto-Install.ps1's remote path in a host console.
 *
 * It is NOT done over the API from here on purpose: creating the VM is only half the
 * job, and the other half — `Provision-AgentVM.ps1` — configures THIS PC (ssh config and
 * key, VS Code Remote-SSH, OpenCode, SMB) and streams a long log the user needs to see.
 * The installer already owns all of that.
 */
async function runNewRemoteVm() {
  const hosts = remoteHosts();
  if (!hosts.length) {
    const ADD = "Add a remote host";
    const pick = await vscode.window.showInformationMessage(
      "No Construct remote host is configured yet.", ADD
    );
    if (pick === ADD) await runAddRemoteHost();
    return;
  }
  let hostEntry = hosts[0];
  if (hosts.length > 1) {
    const picked = await vscode.window.showQuickPick(
      hosts.map((h) => ({ label: remotehost.urlParts(h.url).host, description: h.identity || "", detail: h.url, entry: h })),
      { title: "Create a VM on which host?", ignoreFocusOut: true }
    );
    if (!picked) return;
    hostEntry = picked.entry;
  }

  const reg = registryNow(true);
  const name = await vscode.window.showInputBox({
    title: "Name the new VM",
    prompt: "Lowercase letters, digits and hyphens — it becomes the SSH alias, the key file name and the config-sync branch",
    placeHolder: "work-vm",
    ignoreFocusOut: true,
    validateInput: (v) => {
      const s = String(v || "").trim();
      // The ONE name rule, stated once (instances.NAME_RULE) rather than paraphrased
      // here: this box must not advertise a rule the registry, the installers and the
      // host service would then refuse.
      if (!instances.isValidName(s)) return instances.NAME_RULE;
      if (reg.byName[s]) return `This PC already has a Construct instance named "${s}".`;
      return null;
    },
  });
  if (!name) return;

  const askNumber = async (title, prompt, dflt, min, max) => {
    const v = await vscode.window.showInputBox({
      title, prompt, value: String(dflt), ignoreFocusOut: true,
      validateInput: (x) => {
        const n = Number(String(x || "").trim());
        return Number.isInteger(n) && n >= min && n <= max ? null : `A whole number between ${min} and ${max}.`;
      },
    });
    return v == null ? null : Number(String(v).trim());
  };
  const cpu = await askNumber("vCPUs", "How many virtual CPUs?", 4, 1, 64);
  if (cpu == null) return;
  const ram = await askNumber("Memory (GB)", "How much RAM, in GB?", 8, 1, 1024);
  if (ram == null) return;
  const disk = await askNumber("Disk (GB)", "How large should the virtual disk be, in GB?", 50, 8, 8192);
  if (disk == null) return;

  const scriptsDir = resolveScriptsDir();
  if (!scriptsDir) {
    vscode.window.showErrorMessage("Couldn't find the Construct scripts on this PC. Set construct.scriptsDir and try again.");
    return;
  }
  // Fail CLOSED on version skew, exactly like the panel's lifecycle actions: an
  // Auto-Install.ps1 without these parameters would run the LOCAL path and build a VM
  // on this PC named after the remote one.
  const missing = ["Backend", "ServiceUrl", "InstanceName"].filter(
    (p) => !lifecycle.scriptSupportsParam(scriptsDir, lifecycle.AUTO_INSTALL, p)
  );
  if (missing.length) {
    vscode.window.showErrorMessage(
      "This PC's Construct scripts are too old to create a VM on a remote host (Auto-Install.ps1 doesn't accept " +
      missing.map((p) => "-" + p).join(", ") + "). Update The Construct first."
    );
    return;
  }

  const argSpec = [
    { flag: "-Backend", value: "hyperv-remote" },
    { flag: "-ServiceUrl", value: hostEntry.url },
    { flag: "-ServiceAuth", value: hostEntry.auth === "token" ? "token" : "negotiate" },
    { flag: "-InstanceName", value: name },
    { flag: "-VmMemoryGB", value: String(ram) },
    { flag: "-VmDiskGB", value: String(disk) },
  ];
  // Capability-gated, like every other optional parameter the panel emits: an older
  // Auto-Install.ps1 has no -VmCpuCount, and passing one is a BINDING failure that would
  // stop the create before it starts. Dropping it costs the script's own default.
  if (lifecycle.scriptSupportsParam(scriptsDir, lifecycle.AUTO_INSTALL, "VmCpuCount")) {
    argSpec.push({ flag: "-VmCpuCount", value: String(cpu) });
  } else {
    logLine(`remotehost: this install's Auto-Install.ps1 has no -VmCpuCount; the VM gets its default vCPU count instead of ${cpu}`);
  }
  lifecycle.launchHostScript({
    scriptsDir, script: lifecycle.AUTO_INSTALL,
    args: lifecycle.flattenArgPairs(argSpec), argSpec,
    // No elevation: a remote install creates no local VM, and elevating could put the
    // token store, instances.json and ~\.ssh into a different account's profile.
    elevate: false, label: `New VM "${name}" on ${remotehost.urlParts(hostEntry.url).host}`,
  });
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
        safePost(webview, audioMessage(
          { enabled: true, capturing: hostAudio.capturing, gatePatched: hostAudio.gatePatched },
          hostAudioInstance));
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
      // Both directions ride the single-session chain (requestAudioEnable /
      // requestAudioDisable): a manual "on" during a switch's teardown must join the
      // destination's session rather than build a second one, and an "off" must not
      // overtake an "on" that is still queued.
      if (message.enabled) void requestAudioEnable(context, webview);
      else void requestAudioDisable();
      return;

    case "saveSettings": {
      // The settings file is per instance. Capture the target ALONGSIDE its scripts dir:
      // the reprovision offer below is answered later, and it must rebuild the VM whose
      // settings were written -- with that VM's scripts -- not whichever one is active
      // by then.
      const saveTarget = actionTarget();
      const scriptsDir = resolveScriptsDirFor(saveTarget.instance);
      if (!scriptsDir) { warnNoScriptsDir(); return; }
      try {
        // Snapshot the previous state BEFORE the write so live T3 changes and
        // provisioning-only patch prompts key on transitions, not absolute values.
        const saveStore = stateStore(saveTarget.instance, scriptsDir);
        const prev = instancestate.readSettings(saveStore);
        const merged = host.mapToForm(instancestate.saveSettings(saveStore, message.settings));
        const patchChanges = host.patchReprovisionChanges(prev, merged);
        if (patchChanges.length) offerReprovisionForPatchSettings(scriptsDir, patchChanges, saveTarget);
        else vscode.window.showInformationMessage("Construct settings saved.");
        pushSettings(webview); // reflect the normalized, merged on-disk state
        // The "Microphone passthrough" toggle is a live preference: honor it now, not
        // just on next startup, so changing the setting actually does something.
        const wantMic = message.settings && message.settings.mic === true;
        const micOn = !!(hostAudio && hostAudio.enabled);
        if (wantMic && !micOn) void requestAudioEnable(context, webview);
        else if (!wantMic && micOn) void requestAudioDisable();
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
          // The CAPTURED target the settings were written for — not "whatever is active
          // now": these are multi-minute npm runs on one VM, and `instance` is also what
          // keys t3code's per-VM serialization queue, so enabling on A cannot block B.
          // (It also picks the pairing-script variant openWebUi mints with; the default
          // instance keeps the original command verbatim.)
          const t3opts = { cfg: saveTarget.cfg, instance: saveTarget.instance };
          if (t3plan.action === "enable" && !sourceManagedT3) {
            t3code.enableOnVm({ channel: t3plan.channel, ...t3opts }).then(() => refreshAll());
          } else if (t3plan.action === "disable") {
            t3code.disableOnVm(t3opts).then(() => refreshAll());
          } else if (t3plan.action === "setChannel" && !sourceManagedT3) {
            t3code.setChannelOnVm(t3plan.channel, t3opts).then(() => refreshAll());
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
        lifecycle.run(action, {
          scriptsDir: scriptsDir, backupMode: message.backup, projects: projects,
          instance: rebuildTarget.instance,
          // The confirmation modal lives INSIDE run(): re-ask the capture on the other
          // side of it, or an accept given for this instance rebuilds another one.
          stillCurrent: () => !targetSuperseded(rebuildTarget, action === "redownload" ? "Redownload" : "Reinstall"),
        });
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

    case "saveIdlePolicy":
      // The idle-policy card's "apply" (plan §4.7). Re-clamped and re-validated
      // extension-side: the webview is untrusted input, and the numbers reach the
      // service's own validator behind that.
      void saveIdlePolicy(message.policy || {});
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
      // Forwards (B8). The id comes from the webview, so both go through the module's
      // own `isSafeId` guard before they reach a spool path or a URL.
      if (id === "openForward") { void openForward(String(message.forward || "")); return; }
      if (id === "closeForward") { void closeForward(String(message.forward || "")); return; }
      if (id === "registerThisVm") { void runRegisterThisVm(); return; }
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
        if (message.agent === "t3code") {
          // One capture for the SSH pairing call, the instance the pairing script is
          // built for and the fallback origin — all three must describe one VM.
          const webTarget = actionTarget();
          t3code.openWebUi({
            cfg: webTarget.cfg,
            instance: webTarget.instance,
            webUrl: lastT3WebUrl.get(webTarget.name) || null,
          });
        }
        return;
      }
      if (id === "connect") {
        // CAPTURED: opening a remote window is not undoable, and re-reading the active
        // instance here would connect to whatever the window switched to since the click.
        const connectTarget = actionTarget();
        remote.openOnVm({ path: "/root/repos", newWindow: false, cfg: connectTarget.cfg });
        return;
      }
      if (id === "startConnect") { runStartAndConnect(); return; }
      if (id === "shutdown") { runShutdown(); return; }
      if (id === "exportConfig") {
        const scriptsDir = resolveScriptsDir();
        if (!scriptsDir) { warnNoScriptsDir(); return; }
        const exportTarget = actionTarget();
        // export doesn't touch project selection (and isn't destructive, so no modal)
        lifecycle.run(id, {
          scriptsDir, instance: exportTarget.instance,
          stillCurrent: () => !targetSuperseded(exportTarget, "Export config"),
        });
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
          const started = lifecycle.run(id, {
            scriptsDir: scriptsDir, projects: projects, instance: lifeTarget.instance,
            stillCurrent: () => !targetSuperseded(lifeTarget, id === "reprovision" ? "Reprovision" : id === "reinstall" ? "Reinstall" : "Redownload"),
          });
          if (started !== false && id === "reprovision") beginReprovisionFastRefresh(lifeTarget);
        })();
        return;
      }
      if (id === "updateConstruct") { runUpdateConstruct(); return; }
      // ── Config-sync commands (C6) ─────────────────────────────────────
      if (id === "syncConfigNow") {
        (async function () {
          // Capture the instance the button was pressed for. The tick (and the queued
          // follow-up it may become), the branch it writes, and the scan after it all
          // stay on THAT instance — a switch mid-sync must not redirect any of them.
          const syncNowTarget = actionTarget();
          try {
            await runConfigSync(syncNowTarget);
            // Always run import after sync (or instead of it for no-git hosts),
            // bypassing the throttle. Coalesced per instance: joins an in-flight scan
            // OF THAT INSTANCE if one exists.
            await coalescedImport(true, syncNowTarget);
          } catch (e) {
            vscode.window.showErrorMessage("Config sync failed: " + (e && e.message ? e.message : e));
          }
          cachedConfigSync = await buildConfigSyncState(syncNowTarget);
          refreshAll();
        })();
        return;
      }
      if (id === "addConfigRemote") {
        // The prompt below can sit open indefinitely; the recovery tick inside
        // buildConfigSyncState must run for the instance the button was pressed for.
        const addRemoteTarget = actionTarget();
        vscode.window.showInputBox({
          title: "Add a remote config repository",
          prompt: "Git URL of the remote config repo",
          placeHolder: "https://github.com/org/construct-config.git",
          ignoreFocusOut: true,
          // One shared validator for every URL input path, run against the NORMALIZED
          // value (the same string that gets stored). Secrets never travel in a URL:
          // it would land in git argv, .git/config, manifest/remotes.json, every
          // provenance entry and the panel. A git credential helper holds the PAT.
          validateInput: function (v) { return configsync.validateConfigRemoteUrl(v, remote.isLikelyGitUrl); },
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
          buildConfigSyncState(addRemoteTarget).then(function (cs) { cachedConfigSync = cs; refreshAll(); });
        });
        return;
      }
      if (id === "removeConfigRemote") {
        // The panel holds the display-safe url; map it back to the stored one.
        var rmDir0 = resolveCfgDir();
        var rmUrl = rmDir0 ? configsync.resolveRemoteUrl(configsync.readRemotes(rmDir0), message.url) : message.url;
        if (!rmUrl) return;
        const rmRemoteTarget = actionTarget();   // captured before the modal
        vscode.window.showWarningMessage("Remove the remote config repo?\n" + configsync.displayRemoteUrl(rmUrl), { modal: true }, "Remove").then(function (pick) {
          if (pick !== "Remove") return;
          var dir = resolveCfgDir();
          if (!dir) return;
          var existing = configsync.readRemotes(dir);
          configsync.writeRemotes(dir, existing.filter(function (r) { return r.url !== rmUrl; }));
          buildConfigSyncState(rmRemoteTarget).then(function (cs) { cachedConfigSync = cs; refreshAll(); });
        });
        return;
      }
      if (id === "importRemoteConfigs") {
        // The import runs through several prompts; the tick that publishes the result
        // belongs to the instance the button was pressed for.
        const importRemoteTarget = actionTarget();
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
            await runConfigSync(importRemoteTarget);
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
        var puDir0 = resolveCfgDir();
        var pushUrl = puDir0 ? configsync.resolveRemoteUrl(configsync.readRemotes(puDir0), message.url) : message.url;
        if (!pushUrl) return;
        vscode.window.showWarningMessage(
          "This commits your local versions of the files imported from " + configsync.displayRemoteUrl(pushUrl) + " to a new branch and pushes.",
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
      if (id === "publishConfigProfiles" || id === "addRemoteAndPublish") {
        // B15 (plan 4.13): publish UNTRACKED local profiles into a linked config
        // repo and adopt them (manifest entry + stored base), after which Import
        // and Push back round-trip them like any other tracked file. The IO twin
        // of Publish-ConstructConfigProfiles in lib/AgentVm.Common.ps1 -- same
        // pure plan (configsync.planPublish <-> Get-ConstructPublishPlan), same
        // manifest bytes, same collision rule, same default-branch target.
        //
        // NOTHING here prints, logs or shows a raw remote URL: a publish target on
        // the owner's own git host carries a PAT in the URL.
        const publishTarget = actionTarget();
        (async () => {
          var dir = resolveCfgDir();
          if (!dir) { warnNoScriptsDir(); return; }
          var git = await detectGitCached();
          if (!git.present) { vscode.window.showWarningMessage("Git is not available. Install git first."); return; }

          var pubUrl = configsync.resolveRemoteUrl(configsync.readRemotes(dir), message.url);
          if (id === "addRemoteAndPublish") {
            pubUrl = await vscode.window.showInputBox({
              title: "Add a remote config repo and publish into it",
              prompt: "Git URL of the config repo to publish your local profiles to",
              placeHolder: "https://git.example.com/alice/construct-config.git",
              ignoreFocusOut: true,
              validateInput: function (v) { return configsync.validateConfigRemoteUrl(v, remote.isLikelyGitUrl); },
            });
            if (!pubUrl || !pubUrl.trim()) return;
            pubUrl = pubUrl.trim();
            var linked = configsync.readRemotes(dir);
            if (!linked.some(function (r) { return r.url === pubUrl; })) {
              linked.push({ url: pubUrl });
              configsync.writeRemotes(dir, linked);
            }
          }
          if (!pubUrl) return;
          var shownUrl = configsync.displayRemoteUrl(pubUrl);
          if (configsync.urlHasCredentials(pubUrl)) {
            // A legacy entry linked before credential-free URLs were required. Publishing
            // would copy that secret into every provenance entry it writes.
            vscode.window.showErrorMessage(
              "This config repo is linked with credentials in its URL (" + shownUrl + "). Re-link it without them " +
              "and let your git credential helper supply the token, then publish again.");
            return;
          }

          var clone = await configsync.ensurePublishClone(runGit, configsync.stagingRoot(process.env), pubUrl);
          if (!clone.ok) {
            vscode.window.showErrorMessage("Could not prepare the config repo clone: " + (clone.output || "").slice(0, 200));
            return;
          }
          var pubBranch = "";
          if (!clone.created) pubBranch = await configsync.remoteDefaultBranch(runGit, pubUrl);
          if (!pubBranch || !configsync.isValidPublishBranch(pubBranch)) pubBranch = "main";
          var co = await configsync.checkoutPublishBranch(runGit, clone.dir, pubBranch);
          if (!co.ok) {
            vscode.window.showErrorMessage("Could not switch the config repo clone to \"" + pubBranch + "\": " + (co.output || "").slice(0, 200));
            return;
          }

          // Local profiles as they are ON DISK. The parse/validate/canonicalize
          // gate lives inside planPublish, so an invalid profile is reported --
          // never silently repaired by the (coercive) canonicalizer and pushed.
          // The listing -> plan-input step is the pure buildPublishProfileInputs: an
          // unsafe file name is passed through UNREAD so the planner reports it as
          // invalid (rather than it silently vanishing from the picker), and no
          // unsafe name is ever joined onto a path.
          var localProfiles = configsync.buildPublishProfileInputs(
            host.listProjectProfiles(dir),
            function (name) { return fs.readFileSync(path.join(dir, "projects", name + ".json"), "utf8"); });
          var pubManifest = configsync.readImportManifest(dir);
          var plan = configsync.planPublish({
            profiles: localProfiles, manifest: pubManifest, remoteFiles: co.remoteFiles, selected: null,
          });
          if (!plan.publish.length && !plan.skipTracked.length && !plan.refuse.length && !plan.invalid.length) {
            vscode.window.showInformationMessage("No local project profiles to publish.");
            return;
          }
          for (var ri2 = 0; ri2 < plan.skipTracked.length; ri2++) logLine("publish: skipped \"" + plan.skipTracked[ri2].name + "\" -- " + plan.skipTracked[ri2].reason);
          for (var ri3 = 0; ri3 < plan.refuse.length; ri3++) logLine("publish: refused \"" + plan.refuse[ri3].name + "\" -- " + plan.refuse[ri3].reason);
          for (var ri4 = 0; ri4 < plan.invalid.length; ri4++) logLine("publish: invalid \"" + plan.invalid[ri4].name + "\" -- " + plan.invalid[ri4].reason);
          if (!plan.publish.length) {
            vscode.window.showWarningMessage("Nothing to publish -- every profile is already tracked or cannot be published. See the Construct log.");
            return;
          }

          // Picker: publishable profiles first, ALL ticked; tracked / refused /
          // invalid ones under their own headings, carrying their reason and
          // GREYED -- selecting one snaps back, so the UI cannot publish it. The
          // item model and the selection filter are the pure
          // buildPublishPickerItems / filterPublishSelection (unit-tested).
          var model = configsync.buildPublishPickerItems(plan);
          var picked = await new Promise(function (resolve) {
            var qp = vscode.window.createQuickPick();
            qp.title = "Publish project profiles to " + shownUrl;
            qp.placeholder = "Untracked profiles are pre-selected; greyed rows cannot be published";
            qp.canSelectMany = true;
            qp.ignoreFocusOut = true;
            qp.items = model.map(function (m) {
              if (m.kind === "separator") return { label: m.label, kind: vscode.QuickPickItemKind.Separator };
              return { label: m.label, description: m.description || undefined, blocked: m.blocked };
            });
            qp.selectedItems = qp.items.filter(function (it, ix) { return model[ix].picked; });
            var settling = false;
            qp.onDidChangeSelection(function (sel) {
              if (settling) return;
              var kept = configsync.filterPublishSelection(sel);
              if (kept.length !== sel.length) { settling = true; qp.selectedItems = kept; settling = false; }
            });
            var done = false;
            qp.onDidAccept(function () { done = true; resolve(configsync.filterPublishSelection(qp.selectedItems)); qp.hide(); });
            qp.onDidHide(function () { if (!done) resolve(null); qp.dispose(); });
            qp.show();
          });
          if (!picked || !picked.length) return;
          var chosen = new Set(picked.map(function (p) { return p.label; }));
          var files = plan.publish.filter(function (p) { return chosen.has(p.name); });
          if (!files.length) { vscode.window.showInformationMessage("Nothing selected to publish."); return; }

          var pushed = await configsync.publishToRemote(runGit, {
            stagingDir: clone.dir, branch: pubBranch, files: files,
            message: "publish " + files.length + " profiles",
          });
          if (!pushed.ok) {
            vscode.window.showErrorMessage("Publish failed: " + (pushed.output || "").slice(0, 300));
            logLine("publish: push to " + shownUrl + " (" + pubBranch + ") failed -- " + (pushed.output || ""));
            return;
          }

          // Adopt: the manifest entry + stored base an import would have written.
          // Only now, and only because publishToRemote returned a real commit and a
          // real blob per file -- a profile must never claim to be tracked upstream
          // when the push did not land it.
          fs.mkdirSync(path.join(dir, "manifest"), { recursive: true });
          fs.mkdirSync(path.join(dir, "bases"), { recursive: true });
          for (var ai = 0; ai < files.length; ai++) {
            var entry = configsync.publishManifestEntry({
              remoteUrl: pubUrl, ref: pubBranch, name: files[ai].name,
              baseCommit: pushed.commit, baseBlobSha: pushed.blobShas[files[ai].name],
            });
            fs.writeFileSync(path.join(dir, "manifest", files[ai].name + ".json"), JSON.stringify(entry, null, 2) + "\n", "utf8");
            fs.writeFileSync(path.join(dir, "bases", files[ai].name + ".json"), files[ai].content, "utf8");
            // The stored base is the merge base for the next import, so it must be
            // the SAME bytes as the local file. Canonical everywhere already, so a
            // no-op in practice -- but a hand-formatted (still VALID) profile would
            // otherwise make the first 3-way merge report pure whitespace.
            var localPath = path.join(dir, "projects", files[ai].name + ".json");
            var localRaw = null;
            try { localRaw = fs.readFileSync(localPath, "utf8"); } catch (_) { localRaw = null; }
            if (localRaw !== files[ai].content) fs.writeFileSync(localPath, files[ai].content, "utf8");
          }
          // Adoption always writes NEW manifest/base files, so a run that records
          // nothing did not record anything: require an actual commit, and put the
          // git message in the log the toast points at.
          var stored = await configsync.commitAll(runGit, dir, "publish: " + files.map(function (f) { return f.name; }).join(", "));
          if (!stored.ok || !stored.committed) {
            var storeMsg = stored.output || "git reported no change to commit";
            vscode.window.showWarningMessage("Published, but the local config store could not be committed -- see the Construct log.");
            logLine("publish: committing the config store failed after publishing " + files.length + " profile(s) -- " + storeMsg);
          }
          logLine("publish: " + files.length + " profile(s) to " + shownUrl + " (" + pubBranch + ") at " + pushed.commit.slice(0, 7));
          vscode.window.showInformationMessage("Published " + files.length + " profile(s) to " + pubBranch + " -- they are tracked now.");
          buildConfigSyncState(publishTarget).then(function (cs) { cachedConfigSync = cs; refreshAll(); });
        })().catch(function (e) { vscode.window.showErrorMessage("Publish failed: " + configsync.displayRemoteUrl(e && e.message ? e.message : String(e))); });
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
        const openRepoTarget = actionTarget();
        (async () => {
          try {
            var gate = await configMergeGate(openRepoTarget);
            if (!gate.blocked && gate.dir) {
              cachedConfigSync = await buildConfigSyncState(openRepoTarget);
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
  // Reload this window by itself when Update-Construct.ps1 (run from the T3 Code Desktop
  // app, another window, or by hand) installs a newer panel.
  watchInstalledMarker(context);
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("construct.panel", new ConstructViewProvider(context), {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.commands.registerCommand("construct.openPanel", () => openPanel(context)),
    vscode.commands.registerCommand("construct.refresh", () => refreshAll()),
    vscode.commands.registerCommand("construct.showLogs", () => showLogs()),
    vscode.commands.registerCommand("construct.chooseTheme", () => openThemePicker(context)),
    vscode.commands.registerCommand("construct.switchInstance", () => runSwitchInstance()),
    vscode.commands.registerCommand("construct.addRemoteHost", () => runAddRemoteHost()),
    vscode.commands.registerCommand("construct.newRemoteVm", () => runNewRemoteVm()),
    vscode.commands.registerCommand("construct.registerThisVm", () => runRegisterThisVm()),
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
        void queueInstanceTransition(onInstanceChanged);
      }
      // Start or tear down the forward server when it is switched on/off. Both go through
      // the chain, so a re-enable cannot overtake the teardown of what was running — and
      // switching it ON re-asks the connection question rather than assuming an answer
      // (a window that has established nothing still starts nothing).
      if (e.affectsConfiguration("construct.forwards.enabled")) {
        void requestForwarderStop();
        if (forwardsEnabled()) noteForwarderConnected();
      }
      // A new host label changes the link every ack promises: re-ack the live tunnels
      // rather than leave the guest holding a link the setting no longer agrees with.
      if (e.affectsConfiguration("construct.forwards.hostLabel") && forwarderSession) {
        forwarderSession.setHostLabel(forwarderui.hostLabelOf(vscode));
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
  // The startup arm is the first evaluation of the mic preference; record which instance
  // it was for, so onInstanceChanged only re-evaluates when the destination REALLY
  // changed (a single-VM window never switches, so it never re-arms).
  audioTargetInstance = activeInstance().name;
  // The target the live sessions start on. Recorded BEFORE anything can observe a change,
  // so the first registry reading is compared against what this window actually armed.
  appliedTargetFingerprint = activeFingerprint();
  queuedTargetFingerprint = appliedTargetFingerprint;
  // Only when a registry exists (see startRegistryWatch): a single-VM install opens no
  // watcher and behaves exactly as before.
  startRegistryWatch();
  void requestAudioEnable(context, undefined, { auto: true });
  // Notification watcher: independent of any open dashboard, so an agent can reach
  // the user who never opened the panel. Delayed slightly so the SSH connect doesn't
  // compete with startup work.
  setTimeout(() => { if (!notifyStopped) startNotifyWatch(); }, 3000);
  // Client port forwards: LAZY and guest-gated, unlike the notification watcher.
  // Activation spawns nothing — no watcher, no reconcile, no probe of its own. It only
  // asks the question this window can already answer for free: is it ATTACHED to the VM
  // over Remote-SSH? If it is, that VM is up by construction and a request queued while
  // VS Code was closed opens right away, whether or not anybody looks at the panel; if it
  // is not, the forwarder waits for the status flow (noteForwarderPresence). The delay is
  // the notification watcher's, for the same reason: don't compete with activation for the
  // SSH connection.
  setTimeout(() => { noteForwarderConnected(); }, 3000);
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
  // CLOSE THE SESSION CHAIN FIRST. Everything still queued on it is refused, and every
  // step already RUNNING — an auto-arm sitting in its reachability probe, an enable that
  // has not reached `new audio.HostAudio` yet — asks instances.planEnable on the way
  // through and gets "refuse" for a closed slot. Without that, a pending arm could
  // construct a HostAudio (and its `ssh -R`) AFTER the dispose below had already run,
  // with nothing left in this extension host that could ever tear it down.
  try { void audioHandover.close(); } catch (_) {}
  // Then release the mic + kill the reverse tunnel. Best-effort and synchronous
  // (deactivate can't reliably await): dispose() tears down the local side (tunnel child
  // + server + any active native recorder). This is the ONE disposal outside the chain,
  // and it is safe precisely because the chain is closed above: the window is going away,
  // and there is no one left to await an SSH teardown. The VM shim only streams while a
  // tunnel exists — which it no longer does — so leaving it until the next explicit
  // disable is harmless; the guard patch is likewise inert without the shim.
  try { if (hostAudio) hostAudio.dispose(); } catch (_) {}
  hostAudio = undefined;
  try { if (repatchTimer) clearTimeout(repatchTimer); } catch (_) {}
  repatchTimer = null;
  hostAudioInstance = null;
  hostAudioSession = null;
  hostAudioEnable = null;
  audioTargetInstance = null;
  stopAutoRefresh();
  stopNotifyWatch();
  // CLOSE THE FORWARDER CHAIN FIRST, for the reason the mic chain is closed first: a start
  // sitting in its transport build must not construct a forwarder — and its `ssh -L`
  // children and listening ports — after the disposal below has already run.
  try { void forwarderChain.close(); } catch (_) {}
  // Then kill every `ssh -L` and hand the spool claim back, so the ports do not outlive
  // the window that opened them and the next window takes over without waiting out the
  // TTL. This is the ONE disposal outside the chain, and it is safe precisely because the
  // chain is closed above (stopForwarder also invalidates the slot claim, so an in-flight
  // start publishes nothing even if it wins the race to the next microtask).
  forwarderArmed = null;
  stopForwarder();
  stopConfigWatcher();
  stopRegistryWatch();
  appliedTargetFingerprint = null;
  queuedTargetFingerprint = null;
}

module.exports = { activate, deactivate };
