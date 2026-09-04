"use strict";
// Launch the host-side PowerShell lifecycle scripts (reprovision / export /
// reinstall / redownload) from the control panel.
//
// WHERE THIS RUNS — this is a UI extension (extensionKind: "ui"), so its Node
// code runs on the user's LOCAL Windows host even when the VS Code window is
// attached to the VM over Remote-SSH. `vscode.window.createTerminal()` does NOT:
// a terminal runs in the window's context, which is the VM (Linux) when remote,
// where powershell.exe doesn't exist. So we launch via child_process (always
// local), opening a NEW visible console window on the host, detached so it
// outlives VS Code.
//
// UAC — we do not elevate the extension host. Reprovision/Export touch no
// Hyper-V and run non-elevated. Reinstall/Redownload delete + recreate the VM,
// so they are launched with `Start-Process -Verb RunAs`, raising the UAC consent
// prompt; one elevated console then does the work. (Auto-Install.ps1 also
// self-elevates as a fallback, so manual runs still work.)
//
// `vscode` is required lazily inside the impure functions so the pure builders
// (buildInvocation/buildHostLaunch) can be unit-tested under plain node.

const cp = require("child_process");
const fs = require("fs");
const path = require("path");
const host = require("./host");
const instances = require("./instances");
const instancestate = require("./instancestate");

function vsc() { return require("vscode"); }

/** Driver dispatch — lazily required so the require CYCLE stays resolvable
 *  (drivers/index -> drivers/hyperv-local -> lifecycle): by the time any of these
 *  functions runs, both modules are fully loaded. */
function drivers() { return require("./drivers"); }
/** The config-sync engine's branch rule, reused (never re-implemented) here. */
function configsync() { return require("./configsync"); }

const PROVISION = "Provision-AgentVM.ps1";   // reprovision + export (no admin)
const AUTO_INSTALL = "Auto-Install.ps1";     // reinstall + redownload (self/explicitly elevated)
const CHECKPOINTS = "Set-AgentVmCheckpoints.ps1"; // apply the checkpoint policy to the LIVE VM (elevated)
const BACKUP_DIR_NAME = ".construct-backup"; // mirrors Get-ConstructBackupDir
// The CAPABILITY MARKER for name-only targeting (B11): the adapter every host script
// resolves -InstanceName through. See NAME_TARGET_PARAMS for why its PRESENCE, and not
// the parameter's declaration, is what the probe asks about.
const INSTANCE_TARGET_LIB = path.join("lib", "AgentVm.InstanceTarget.ps1");

/** Coerce a backup-mode to the validated set Auto-Install.ps1 accepts. The plain
 *  Reinstall/Redownload buttons (and any unknown value) default to save&restore. */
function normalizeBackupMode(bm) {
  return (bm === "existing" || bm === "wipe") ? bm : "save";
}

// ── Per-instance target arguments ────────────────────────────────────────────
// Which target-identity parameters each launched script can actually take. The two
// entry points differ, and getting this wrong is a BINDING FAILURE (an advanced
// function refuses an unknown parameter, so the action never starts):
//
//   Provision-AgentVM.ps1 (reprovision / exportConfig)
//       -VmHost <fqdn> -HostAlias <alias> -SshPort <n> -LocalKeyName <file>
//       It dials the VM directly, so it needs the full endpoint. -LocalKeyName rides
//       along because a non-default instance has its own key file; without it the
//       provisioner would write the DEFAULT instance's key on top of this one.
//
//   Auto-Install.ps1 (reinstall / redownload)
//       -VmName <name>  — and NOTHING else. Auto-Install DERIVES the guest hostname,
//       the alias and the key name from -VmName, and it THROWS when a -VmHost is
//       passed that disagrees with -VmName ("the guest hostname is derived from
//       -VmName"). It declares -VmHost, so a naive "emit it if the script declares
//       it" would break every non-default rebuild — hence the explicit per-script
//       list here rather than a probe over one shared set.
//
//   Set-AgentVmCheckpoints.ps1 (setCheckpoints)
//       -VmName <name> — it only talks to Hyper-V.
//
// For the DEFAULT instance NOTHING is emitted at all, so argv is byte-identical to
// what shipped before instances existed. That is the regression bar these lists exist
// to keep provable.
//   -ConfigBranch rides reprovision/reinstall/redownload (NOT export, which does not
//       initialise a config store). It is CONDITIONAL — see configBranchOverride: the
//       provisioner derives the branch from -HostAlias on its own, so the parameter is
//       emitted only when the instance's configBranch disagrees with that derivation.
const INSTANCE_PARAMS = {
  reprovision: ["VmHost", "HostAlias", "SshPort", "LocalKeyName", "ConfigBranch"],
  exportConfig: ["VmHost", "HostAlias", "SshPort", "LocalKeyName"],
  reinstall: ["VmName", "ConfigBranch"],
  redownload: ["VmName", "ConfigBranch"],
  setCheckpoints: ["VmName"],
};

// ── …and the REMOTE backend's rebuild arguments ──────────────────────────────
// Auto-Install.ps1 reaches a remote VM by INSTANCE NAME plus SERVICE URL, not by
// -VmName: the -VmName path derives a guest hostname, an mshome address and a local
// Hyper-V display name, none of which exist for a VM on somebody else's host — and it
// runs the local skew guards on the way. So the rebuild actions carry a different set
// for a remote instance. Everything else is unchanged, including reprovision and
// exportConfig, which are pure SSH to the endpoint whoever created the VM.
//
// -Backend is in the list on purpose even though the value is implied by the others:
// it is what makes the installer take the remote path at all, and emitting it
// explicitly means an Auto-Install.ps1 that DECLARES the parameter cannot silently
// interpret the run as a local one.
const REMOTE_INSTANCE_PARAMS = {
  reinstall: ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch"],
  redownload: ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch"],
};

// ── …and the SAME actions once the install supports NAME-ONLY TARGETING ──────
// B11 (plan §4.12): the host scripts take `-InstanceName <name>` and resolve the
// endpoint, the alias, the port, the key file and the config-sync branch out of the
// registry themselves (lib/AgentVm.Instances.ps1). One argument replaces four, and one
// parameter is probed instead of four — which is the whole point: every added identity
// parameter used to need its own skew gate.
//
// -ConfigBranch stays in the list even though the entry already carries the value. It
// is the CAPABILITY MARKER for instance-keyed config sync (see checkInstanceSupport),
// and the value emitted is `instance.configBranch` — the very field the resolver reads
// — so it can never contradict the entry.
//
// WHY THE PROBE IS A FILE AND NOT THE PARAMETER: `-InstanceName` already existed on
// Provision-AgentVM.ps1 (the remote service identity) and on Auto-Install.ps1 (the
// remote instance name, which a LOCAL run used to refuse outright). "The script declares
// $InstanceName" is therefore true on B7-era installs, where the parameter means
// something else entirely and the action would run against the DEFAULT VM. The adapter
// FILE (INSTANCE_TARGET_LIB) ships with the new meaning and with nothing else, so its
// presence is the honest question — and its absence falls back to the four-argument
// form, which those installs do understand.
const NAME_TARGET_PARAMS = {
  reprovision: ["InstanceName", "ConfigBranch"],
  exportConfig: ["InstanceName"],
  reinstall: ["InstanceName", "ConfigBranch"],
  redownload: ["InstanceName", "ConfigBranch"],
  setCheckpoints: ["InstanceName"],
};

/** Is `declared` (the probe result) the NAME-ONLY set? instanceParamSupport only ever
 *  reports "InstanceName" for a local action when the marker file is there, so this
 *  reads the probe rather than the disk a second time. `undefined` (no probe) keeps the
 *  legacy set, exactly as every other gate treats it. Pure. */
function usesNameTargeting(declared) {
  return Array.isArray(declared) && declared.indexOf("InstanceName") >= 0;
}

/** Is this backend one whose VMs live on a host service? Normalized exactly like
 *  getDriver() (trimmed, lowercased) so a differently-cased registry value can't be
 *  read one way here and another there. Pure. */
function isRemoteBackend(backend) {
  return String(backend == null ? "" : backend).trim().toLowerCase() === "hyperv-remote";
}

/** The instance parameters an action emits for THIS instance's backend, given what the
 *  installed script declares. A REMOTE rebuild is already name-based (it must also carry
 *  the service URL), so it keeps its own list. Pure. */
function paramsForAction(action, instance, declared) {
  if (instance && isRemoteBackend(instance.backend) && REMOTE_INSTANCE_PARAMS[action]) {
    return REMOTE_INSTANCE_PARAMS[action];
  }
  if (usesNameTargeting(declared) && NAME_TARGET_PARAMS[action]) return NAME_TARGET_PARAMS[action];
  return INSTANCE_PARAMS[action];
}

/**
 * The identity parameters the installed script MUST declare before a NON-DEFAULT
 * instance's action is allowed to run. Stated explicitly (not derived from
 * INSTANCE_PARAMS) because it answers a different question: INSTANCE_PARAMS is what we
 * WOULD emit, this is what makes the action TARGETED AT ALL.
 *
 * Why every one of them, rather than "emit what fits":
 *   Auto-Install.ps1 without -VmName rebuilds its own default VM ("Agent-VM") — so
 *   Reinstall/Redownload on `work-vm` would DELETE the default VM;
 *   Provision-AgentVM.ps1 without -VmHost/-HostAlias/-SshPort/-LocalKeyName dials the
 *   default endpoint with the default key, so Reprovision/Export would reconfigure (and
 *   re-key) the default VM.
 * Silently dropping a parameter therefore RETARGETS the action, which is why the gate
 * refuses instead. -ConfigBranch is not listed: whether its VALUE is emitted is
 * conditional (only when the provisioner would derive another ref), while its
 * DECLARATION is required for every action that can carry it — that gate lives in
 * checkInstanceSupport.
 */
const REQUIRED_INSTANCE_PARAMS = {
  reprovision: ["VmHost", "HostAlias", "SshPort", "LocalKeyName"],
  exportConfig: ["VmHost", "HostAlias", "SshPort", "LocalKeyName"],
  reinstall: ["VmName"],
  redownload: ["VmName"],
  setCheckpoints: ["VmName"],
};

/** The same rule for a REMOTE instance's rebuild. An Auto-Install.ps1 that predates the
 *  remote path declares none of these, and dropping them does not "degrade": it runs
 *  the LOCAL path and rebuilds a local VM named after the remote one. */
const REQUIRED_REMOTE_INSTANCE_PARAMS = {
  reinstall: ["Backend", "ServiceUrl", "InstanceName"],
  redownload: ["Backend", "ServiceUrl", "InstanceName"],
};

/** The name-only form's requirement: the NAME, and nothing else. Everything the four
 *  arguments used to state is derived from it by the script, so a run that carries the
 *  name is fully targeted — and one that does not is refused by the legacy list. */
const REQUIRED_NAME_TARGET_PARAMS = {
  reprovision: ["InstanceName"],
  exportConfig: ["InstanceName"],
  reinstall: ["InstanceName"],
  redownload: ["InstanceName"],
  setCheckpoints: ["InstanceName"],
};

/** What an action MUST be able to state for this instance. Pure. */
function requiredParamsForAction(action, instance, declared) {
  if (instance && isRemoteBackend(instance.backend) && REQUIRED_REMOTE_INSTANCE_PARAMS[action]) {
    return REQUIRED_REMOTE_INSTANCE_PARAMS[action];
  }
  if (usesNameTargeting(declared) && REQUIRED_NAME_TARGET_PARAMS[action]) {
    return REQUIRED_NAME_TARGET_PARAMS[action];
  }
  return REQUIRED_INSTANCE_PARAMS[action] || [];
}

/** Human labels for the refusal messages (buildInvocation's own labels are built
 *  inside the switch, which a refusal never reaches). */
const ACTION_LABELS = {
  reprovision: "Reprovision",
  exportConfig: "Export config",
  reinstall: "Reinstall",
  redownload: "Redownload",
  setCheckpoints: "Automatic checkpoints",
};

/**
 * The config-sync branch Provision-AgentVM.ps1 DERIVES from a host alias — the mirror
 * of Get-ConstructConfigBranchName (lib/AgentVm.Common.ps1): "agent-vm" (and an empty
 * alias) -> "vm"; anything else -> "vm-<alias>" lowercased; a result the branch
 * validator rejects falls back to "vm". Pure. Change it together with the PowerShell
 * function.
 *
 * NO PREFIX IS STRIPPED. Both sides used to remove a leading "construct-" (an alias
 * convention abandoned mid-project and never shipped), which made the valid instance
 * "construct-work" derive "vm-construct-work" in the registry and "vm-work" here — the
 * config store of the DIFFERENT, equally valid instance "work". The prefix is RESERVED
 * by every name validator instead (instances.isValidName), so no alias can carry it.
 */
function derivedConfigBranch(hostAlias) {
  const cs = configsync();
  const alias = String(hostAlias == null ? "" : hostAlias).trim().toLowerCase();
  if (!alias || alias === "agent-vm") return cs.DEFAULT_VM_BRANCH;
  const branch = "vm-" + alias;
  return cs.isValidVmBranch(branch) ? branch : cs.DEFAULT_VM_BRANCH;
}

/**
 * The -ConfigBranch value an action must carry for this instance, or null when the
 * destination would derive the very same branch on its own.
 *
 * Why it matters: ordinary JS config sync uses instance.configBranch verbatim, while
 * Provision-AgentVM.ps1 derives its branch from -HostAlias. An instance with
 * configBranch "vm-team" and hostAlias "work-vm" would be INITIALISED and synced by the
 * provisioner on "vm-work-vm" while every panel tick used "vm-team" — one VM split
 * across two host-config refs. Emitting nothing when the two agree keeps the common
 * case (and the whole default path) byte-identical. Pure.
 */
function configBranchOverride(instance) {
  if (!instance || instances.isDefaultInstance(instance)) return null;
  const want = instance.configBranch;
  // An unusable branch name is not an override to force onto the provisioner: the
  // registry reader already refuses such an instance, and the sync engine falls back
  // to the default branch rather than acting on it.
  if (!want || !configsync().isValidVmBranch(want)) return null;
  return want === derivedConfigBranch(instance.hostAlias) ? null : want;
}

/** The value each instance parameter carries, for one normalized instance. A null
 *  means "nothing to emit" (only -ConfigBranch is ever conditional). */
function instanceParamValue(param, instance) {
  switch (param) {
    case "VmHost": return instance.vmHost;
    case "HostAlias": return instance.hostAlias;
    case "SshPort": return String(instance.sshPort);
    case "LocalKeyName": return instance.keyName;
    case "VmName": return instance.vmName;
    case "ConfigBranch": return configBranchOverride(instance);
    // Remote rebuilds only (REMOTE_INSTANCE_PARAMS).
    case "Backend": return instance.backend;
    case "ServiceUrl": return (instance.service && instance.service.url) || null;
    case "InstanceName": return instance.name;
    default: return null;
  }
}

/**
 * The target-identity args for one action, as (flag, value) PAIRS. Returns [] — the
 * zero-change path — for a missing instance and for the DEFAULT instance
 * (instances.isDefaultInstance).
 *
 * `declared` is the version-skew filter: the subset of INSTANCE_PARAMS[action] the
 * INSTALLED script actually declares (lifecycle.run computes it with
 * scriptSupportsParam; tests pass it explicitly). `undefined` means "no probe was
 * done" and emits the full set. Dropping a REQUIRED parameter here would retarget the
 * action, so callers must run checkInstanceSupport first — buildInvocation does, and
 * refuses; by the time this runs, everything required is present. Pure.
 */
function instanceArgPairs(action, instance, declared) {
  if (!instance || instances.isDefaultInstance(instance)) return [];
  const wanted = paramsForAction(action, instance, declared);
  if (!wanted) return [];
  const supported = Array.isArray(declared) ? declared : null;
  const out = [];
  for (const p of wanted) {
    if (supported && supported.indexOf(p) < 0) continue;
    const v = instanceParamValue(p, instance);
    if (v != null && String(v) !== "") out.push({ flag: "-" + p, value: String(v) });
  }
  return out;
}

/** The same target-identity args as a flat token list. Pure. */
function instanceArgs(action, instance, declared) {
  return flattenArgPairs(instanceArgPairs(action, instance, declared));
}

/**
 * May this action run against this instance, with the parameters the installed script
 * declares? Returns null when it may, or a structured refusal { blocked: true, reason }
 * when it may not — buildInvocation returns that verbatim and run() surfaces it.
 *
 * NEVER blocks the DEFAULT instance (or a missing one): that is the zero-change path,
 * where no targeting is needed in the first place.
 *
 * Three gates, most fundamental first:
 *   1. BACKEND — the host scripts drive the local Hyper-V only (drivers/index.js
 *      lifecycleSupport); a rebuild of a remote instance would hit a local VM.
 *   2. REQUIRED IDENTITY — an older script that doesn't declare a parameter would run
 *      against ITS OWN defaults, i.e. the default VM (REQUIRED_INSTANCE_PARAMS).
 *   3. CONFIG BRANCH — for every action that can carry -ConfigBranch, the installed
 *      script must DECLARE it (the capability marker for instance-keyed config sync),
 *      or the provisioner initialises and syncs a DIFFERENT ref than the panel — the
 *      canonical "vm-<name>" branch included, where there is no value to emit.
 *
 * `declared === undefined` means "no probe was done" (tests / direct callers): the
 * version-skew gates are then skipped, exactly as instanceArgPairs emits the full set.
 * Pure.
 */
function checkInstanceSupport(action, instance, declared) {
  if (!instance || instances.isDefaultInstance(instance)) return null;
  const label = ACTION_LABELS[action] || action;
  const name = instance.name || "this instance";
  const support = drivers().lifecycleSupport(instance.backend, action);
  if (!support.ok) {
    return { blocked: true, reason: `${label} can't run for instance "${name}": ${support.reason}` };
  }
  // A remote instance is addressed by its SERVICE, so an entry that names none cannot be
  // rebuilt at all — and an Auto-Install run without -ServiceUrl falls back to the LOCAL
  // path. Refuse before any parameter probing: this is a property of the entry, not of
  // the installed scripts, so it is wrong on every version of them.
  if (isRemoteBackend(instance.backend) && REMOTE_INSTANCE_PARAMS[action] &&
      !(instance.service && instance.service.url)) {
    return {
      blocked: true,
      reason: `${label} can't run for instance "${name}": its registry entry records no host service ` +
        "(service.url), so there is nothing to ask for a new VM. Add the host again, or fix the entry.",
    };
  }
  const supported = Array.isArray(declared) ? declared : null;
  if (!supported) return null;
  const required = requiredParamsForAction(action, instance, declared);
  const missing = required.filter((p) => supported.indexOf(p) < 0);
  if (missing.length) {
    return {
      blocked: true,
      reason: `${label} can't target instance "${name}": this Construct install's ` +
        `${scriptForAction(action) || "host scripts"} doesn't accept ` +
        missing.map((p) => "-" + p).join(", ") +
        ", so the action would run against the DEFAULT VM. Update the Construct scripts first.",
    };
  }
  // -ConfigBranch is the CAPABILITY MARKER for instance-keyed config sync, so the gate
  // asks whether the script DECLARES it — not whether this instance needs a value
  // emitted. A script that predates the parameter has no per-alias branch derivation
  // either: it would initialise and sync work-vm's store on refs/heads/vm while the
  // panel syncs refs/heads/vm-work-vm, splitting one VM across two host-config refs.
  // That is just as true for the canonical "vm-<name>" branch (nothing to emit) as for
  // an explicit override, which is why the check no longer looks at the override.
  const emitted = paramsForAction(action, instance, declared) || [];
  if (emitted.indexOf("ConfigBranch") >= 0 && supported.indexOf("ConfigBranch") < 0) {
    const branch = instance.configBranch || derivedConfigBranch(instance.hostAlias);
    return {
      blocked: true,
      reason: `${label} can't target instance "${name}": its config-sync branch ` +
        `"${branch}" needs -ConfigBranch, which this install's ` +
        `${scriptForAction(action) || "host scripts"} doesn't accept — the VM would be ` +
        "initialised on a different branch than the panel syncs. Update the Construct scripts first.",
    };
  }
  return null;
}

/**
 * ARGUMENTS ARE BUILT AS (flag, value) PAIRS, not as a flat token list, and the flat
 * `args` array every launcher already takes is derived from them. That structure is
 * what lets buildCallCommand tell a parameter NAME from a VALUE without guessing:
 * a registry value that happens to start with '-' (or carries `;`) must be quoted as
 * data, never handed to PowerShell as syntax. `{ flag }` with no `value` is a switch.
 */
function flattenArgPairs(pairs) {
  const out = [];
  for (const p of (pairs || [])) {
    out.push(p.flag);
    if ("value" in p) out.push(p.value);
  }
  return out;
}

/**
 * Build the script + PowerShell args for an action from the settings form shape.
 * Pure. `opts`: { settings, backupDir, backupMode }.
 *   reprovision  -> Provision-AgentVM.ps1 -Action provision  (keeps data, no admin)
 *   exportConfig -> Provision-AgentVM.ps1 -Action export -BackupDir <dir>
 *   reinstall    -> Auto-Install.ps1 -Action reinstall  -BackupMode <mode>
 *   redownload   -> Auto-Install.ps1 -Action redownload -BackupMode <mode>
 * Returns { script, args, destructive, elevate, label } or null for an unknown action.
 *
 * `opts.instance` (optional) is the ACTIVE INSTANCE (the normalized object from
 * src/instances.js). For a non-default instance the action's target-identity args are
 * emitted (see instanceArgs / INSTANCE_PARAMS), gated by `opts.instanceParams` — the
 * parameters the installed script really declares. For the default instance (and when
 * no instance is passed at all) NOTHING extra is emitted and argv is byte-identical to
 * what shipped before instances existed.
 *
 * A non-default instance the installed scripts (or this build's drivers) cannot
 * TARGET yields a structured refusal `{ blocked: true, reason }` instead of an
 * invocation — never a silently retargeted one. See checkInstanceSupport.
 *
 * The agent password is deliberately NOT collected, stored, or passed (it would be
 * visible on the process command line and is only a manual-fallback login — normal
 * access is as root over the pre-seeded SSH key). Launched from the panel
 * (-FromPanel), Auto-Install.ps1 keeps the seeded default 'agent' without prompting.
 * Project selection is likewise left to the script's own selector until the Projects
 * batch wires it.
 */
function buildInvocation(action, opts = {}) {
  const s = opts.settings || {};
  // Fail CLOSED before building anything: a non-default instance the installed scripts
  // can't target must be refused, not silently pointed at the default VM.
  const blocked = checkInstanceSupport(action, opts.instance, opts.instanceParams);
  if (blocked) return blocked;
  const parts = [];
  // Is this invocation TARGETED at a specific (non-default) VM? Only then is the pair
  // spec attached — see buildCallCommand: quoting values structurally CHANGES the
  // command string for a value that starts with '-' (a project literally named
  // "-NoProfile"), and on the default path that string must stay byte-identical to the
  // pre-instances build. Registry-sourced values only ever ride a targeted invocation,
  // which is where the quoting matters.
  const targeted = !!(opts.instance && !instances.isDefaultInstance(opts.instance));
  const done = (script, extra) => {
    const inv = { script, args: flattenArgPairs(parts), ...extra };
    if (targeted) inv.argSpec = parts.slice();
    return inv;
  };
  const addSwitch = (flag) => { parts.push({ flag }); };
  const addPair = (flag, value) => { parts.push({ flag, value }); };
  // Panel-launched scripts skip their end-of-run "Press Enter" pause: the dashboard
  // shows the result (and auto-refreshes), so the console just closes when done. In
  // debug the launcher still keeps it open via -NoExit. A direct PowerShell run (no
  // -FromPanel) keeps the pause so the window stays readable. Passed as a param (not
  // an env var) so it survives the UAC boundary for the elevated reinstall/redownload.
  addSwitch("-FromPanel");
  // Target identity for a non-default instance ([] for the default one — the
  // zero-change path). Named parameters, so their position in argv is irrelevant.
  for (const p of instanceArgPairs(action, opts.instance, opts.instanceParams)) parts.push(p);
  const pushPair = (flag, val) => { if (val != null && String(val).trim() !== "") addPair(flag, String(val)); };
  const pushBool = (flag, val) => { if (typeof val === "boolean") addPair(flag, val ? "true" : "false"); };
  // The control panel's project selection (persisted `projects`), so the script uses
  // it instead of re-prompting in the console. Only when a selection exists — with none
  // persisted, let the script keep its own default/prompt rather than force "default".
  const pushProjects = () => {
    const p = Array.isArray(opts.projects) ? opts.projects.filter(Boolean) : [];
    if (p.length) addPair("-Projects", p.join(","));
  };

  switch (action) {
    case "reprovision":
      addPair("-Action", "provision");
      pushProjects();
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      pushBool("-VsCodeServeWeb", s.serveWeb);
      pushBool("-VsCodeTunnel", s.tunnel);
      pushBool("-SmbShare", s.smb);
      pushBool("-ClaudePartialStreaming", s.partialStreaming);
      pushBool("-MicPassthrough", s.mic);
      if (opts.supportsOpenCodeBackgroundWatcher !== false) pushBool("-OpenCodeBackgroundWatcher", s.opencodeBackgroundWatcher);
      pushBool("-T3Code", s.t3code);
      if (opts.supportsT3CodeChannel !== false) pushPair("-T3CodeChannel", s.t3codeChannel);
      if (opts.supportsT3CodeLimitResume !== false) pushBool("-T3CodeLimitResume", s.t3codeLimitResume);
      // Launched from the panel: don't prompt for the SMB drive letter etc. (still pauses
      // at the end so output is readable — -NonInteractive is NOT -Auto).
      addSwitch("-NonInteractive");
      return done(PROVISION, { destructive: false, elevate: false, label: "Reprovision" });

    case "exportConfig":
      addPair("-Action", "export");
      addPair("-BackupDir", opts.backupDir);
      return done(PROVISION, { destructive: false, elevate: false, label: "Export config" });

    case "reinstall":
    case "redownload": {
      addPair("-Action", action);
      addPair("-BackupMode", normalizeBackupMode(opts.backupMode));
      pushProjects(); // Auto-Install forwards -Projects to Provision (-Auto gates its prompts)
      pushPair("-VmMemoryGB", s.ram);
      pushPair("-VmDiskGB", s.disk);
      // Hyper-V automatic checkpoints are decided when the VM is CREATED, which only
      // a rebuild does — so the preference rides reinstall/redownload, not reprovision
      // (which never touches Hyper-V). An existing VM is changed by "setCheckpoints".
      //
      // Capability-gated: Auto-Install.ps1 is an advanced function, so an install whose
      // scripts predate this parameter FAILS TO BIND and the rebuild never starts. A
      // newer extension against an older scripts dir must therefore drop the flag and
      // let the script's own default stand, not break Reinstall outright.
      if (opts.supportsCheckpoints !== false) pushBool("-AutomaticCheckpoints", s.autoCheckpoints);
      if (action === "redownload") pushPair("-UbuntuRelease", s.ubuntu);
      pushPair("-GitUserName", s.gitName);
      pushPair("-GitEmail", s.gitEmail);
      // A destructive rebuild provisions a fresh VM, so carry the saved streaming
      // preference through Auto-Install -> Create-AgentVM -> Provision (the panel's
      // "…with these settings" buttons must honour an explicit off, not silently
      // fall back to the provisioner's default-on).
      pushBool("-ClaudePartialStreaming", s.partialStreaming);
      pushBool("-MicPassthrough", s.mic);
      if (opts.supportsOpenCodeBackgroundWatcher !== false) pushBool("-OpenCodeBackgroundWatcher", s.opencodeBackgroundWatcher);
      pushBool("-T3Code", s.t3code);
      if (opts.supportsT3CodeChannel !== false) pushPair("-T3CodeChannel", s.t3codeChannel);
      if (opts.supportsT3CodeLimitResume !== false) pushBool("-T3CodeLimitResume", s.t3codeLimitResume);
      return done(AUTO_INSTALL, {
        destructive: true,
        // A REMOTE rebuild must NOT elevate. It creates no local VM, so it needs no
        // administrator rights — and on a PC where UAC switches to a different admin
        // account it would read and write the DPAPI token store, instances.json and
        // ~\.ssh under THAT account's profile. Auto-Install.ps1 makes the same choice
        // for the same reason (it skips its own relaunch on the remote path); if this
        // launcher elevated anyway, the script would already be in the wrong profile
        // before it could decide. Local rebuilds are unchanged: they drive Hyper-V.
        elevate: !isRemoteBackend(opts.instance && opts.instance.backend),
        label: action === "redownload" ? "Redownload" : "Reinstall",
      });
    }

    // Apply the automatic-checkpoint policy to the EXISTING VM, right now. Hyper-V
    // cmdlets need admin, so this elevates (UAC) like reinstall/redownload — but it
    // isn't `destructive` in the confirm-modal sense: the extension has already asked
    // whether to apply now, and the script itself confirms before removing any
    // checkpoint it can't positively identify as automatic.
    case "setCheckpoints": {
      // STRICT boolean. This action deletes checkpoints when it runs with -Enabled false,
      // so a malformed request (missing field, the STRING "true") must be refused rather
      // than defaulted — defaulting would silently pick the destructive direction.
      if (typeof opts.enabled !== "boolean") return null;
      const enabled = opts.enabled;
      addPair("-Enabled", enabled ? "true" : "false");
      return done(CHECKPOINTS, {
        destructive: false, elevate: true,
        label: enabled ? "Enable automatic checkpoints" : "Disable automatic checkpoints",
      });
    }

    default:
      return null;
  }
}

/**
 * Do the host scripts in `scriptsDir` understand `-AutomaticCheckpoints`? Read the
 * ANSWER out of Auto-Install.ps1 itself rather than inferring it from a sibling file's
 * existence: a hand-assembled or partially-updated scripts dir can hold the newer
 * Set-AgentVmCheckpoints.ps1 next to an older Auto-Install.ps1, and passing the flag to
 * an advanced function that lacks the parameter is a BINDING failure — the rebuild would
 * never start. Unreadable/absent → false (drop the flag; the script's own default stands).
 */
function scriptSupportsCheckpoints(scriptsDir) {
  if (!scriptsDir) return false;
  let txt;
  try { txt = fs.readFileSync(path.join(scriptsDir, AUTO_INSTALL), "utf8"); } catch (_) { return false; }
  // Match a real parameter DECLARATION, not any mention of the name: `[string]$Foo` /
  // `[string]$Foo = "x"` / `$Foo,`. A bare name test would be satisfied by a comment (or
  // by our own doc text) on a script that has no such parameter, and passing the flag to
  // one is a binding failure. Comments are stripped first so even `# $Foo = ...` can't
  // pass; PowerShell identifiers are case-INSENSITIVE, so is this.
  const code = txt
    .replace(/<#[\s\S]*?#>/g, "")   // block comments (the .SYNOPSIS help header)
    .replace(/^[ \t]*#.*$/gm, "");  // whole-line comments
  return /\$AutomaticCheckpoints\s*(?:=|,|\)|$)/im.test(code);
}

/**
 * Same gate as scriptSupportsCheckpoints but for `-T3CodeChannel`. An older scripts
 * dir that predates this parameter rejects the unknown flag at binding time, breaking
 * the lifecycle action outright. Unlike checkpoints (which only rides reinstall/
 * redownload → Auto-Install.ps1), the channel flag is sent on BOTH paths:
 *   reprovision   → Provision-AgentVM.ps1
 *   reinstall/redownload → Auto-Install.ps1 → Create-AgentVM.ps1 → Provision
 *
 * Action-appropriate: reprovision invokes only Provision-AgentVM.ps1, so only that
 * script needs the parameter. Rebuild (reinstall/redownload) invokes Auto-Install,
 * which has its own version-skew guard for the Create and Provision splats — checking
 * Auto-Install alone suffices. Without an action, require BOTH (conservative default
 * for callers that don't specify one).
 */
/** Same gate as scriptSupportsT3CodeChannel but for `-T3CodeLimitResume` (the
 *  usage-limit auto-resume opt-in), which rides the same two paths. */
function scriptSupportsT3CodeLimitResume(scriptsDir, action) {
  if (!scriptsDir) return false;
  const re = /\$T3CodeLimitResume\s*(?:=|,|\)|$)/im;
  const check = (file) => {
    let txt;
    try { txt = fs.readFileSync(path.join(scriptsDir, file), "utf8"); } catch (_) { return false; }
    const code = txt.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
    return re.test(code);
  };
  if (action === "reprovision") return check(PROVISION);
  if (action === "reinstall" || action === "redownload") return check(AUTO_INSTALL);
  return check(PROVISION) && check(AUTO_INSTALL);
}

/** Capability gate for the optional OpenCode background-watcher parameter. */
function scriptSupportsOpenCodeBackgroundWatcher(scriptsDir, action) {
  if (!scriptsDir) return false;
  const re = /\$OpenCodeBackgroundWatcher\s*(?:=|,|\)|$)/im;
  const check = (file) => {
    let txt;
    try { txt = fs.readFileSync(path.join(scriptsDir, file), "utf8"); } catch (_) { return false; }
    const code = txt.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
    return re.test(code);
  };
  if (action === "reprovision") return check(PROVISION);
  if (action === "reinstall" || action === "redownload") return check(AUTO_INSTALL);
  return check(PROVISION) && check(AUTO_INSTALL);
}

function scriptSupportsT3CodeChannel(scriptsDir, action) {
  if (!scriptsDir) return false;
  const re = /\$T3CodeChannel\s*(?:=|,|\)|$)/im;
  const check = (file) => {
    let txt;
    try { txt = fs.readFileSync(path.join(scriptsDir, file), "utf8"); } catch (_) { return false; }
    const code = txt.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
    return re.test(code);
  };
  if (action === "reprovision") return check(PROVISION);
  if (action === "reinstall" || action === "redownload") return check(AUTO_INSTALL);
  return check(PROVISION) && check(AUTO_INSTALL);
}

/**
 * Does `<scriptsDir>/<file>` DECLARE the parameter `$<name>`? The same
 * comment-stripped declaration test the scriptSupports* gates use, generalized so the
 * per-instance target arguments can be probed without one function per parameter.
 * Unreadable/absent file -> false (drop the argument; the script's own default stands).
 */
function scriptSupportsParam(scriptsDir, file, name) {
  if (!scriptsDir || !file || !name) return false;
  let txt;
  try { txt = fs.readFileSync(path.join(scriptsDir, file), "utf8"); } catch (_) { return false; }
  const code = txt.replace(/<#[\s\S]*?#>/g, "").replace(/^[ \t]*#.*$/gm, "");
  return new RegExp("\\$" + name + "\\s*(?:=|,|\\)|$)", "im").test(code);
}

/** The script an action launches (so the instance-arg probe reads the right file). */
function scriptForAction(action) {
  if (action === "reprovision" || action === "exportConfig") return PROVISION;
  if (action === "reinstall" || action === "redownload") return AUTO_INSTALL;
  if (action === "setCheckpoints") return CHECKPOINTS;
  return null;
}

/**
 * Does this scripts dir implement NAME-ONLY TARGETING for this action? Two conditions,
 * and both are needed:
 *
 *   1. the adapter FILE every host script resolves -InstanceName through is installed
 *      (INSTANCE_TARGET_LIB) — the honest capability question, because the PARAMETER
 *      name predates this meaning on two of the three scripts (see NAME_TARGET_PARAMS);
 *   2. the script the action launches declares $InstanceName at all — the file could
 *      have been dropped in on its own, and an argument the target does not declare is
 *      a binding failure, i.e. an action that never starts.
 *
 * A REMOTE instance's rebuild is excluded: it already targets by name and additionally
 * has to carry the service URL, so REMOTE_INSTANCE_PARAMS stays its list.
 */
function supportsNameTargeting(scriptsDir, action, instance) {
  if (!scriptsDir || !NAME_TARGET_PARAMS[action]) return false;
  if (instance && isRemoteBackend(instance.backend) && REMOTE_INSTANCE_PARAMS[action]) return false;
  const file = scriptForAction(action);
  if (!file) return false;
  try { fs.statSync(path.join(scriptsDir, INSTANCE_TARGET_LIB)); } catch (_) { return false; }
  return scriptSupportsParam(scriptsDir, file, "InstanceName");
}

/**
 * The subset of an action's instance parameters that the INSTALLED script declares —
 * the version-skew gate for per-instance targeting. A scripts dir that predates B1
 * yields [] and the action runs against the script's own defaults (which is exactly
 * today's single-VM behaviour) rather than failing to bind.
 */
function instanceParamSupport(scriptsDir, action, instance) {
  // Backend-aware: a remote instance's rebuild is probed for the REMOTE parameters
  // (-Backend/-ServiceUrl/-InstanceName), because those are the ones it would emit. The
  // union would be wrong in both directions -- it would report a local-only parameter as
  // "declared" for a remote rebuild and vice versa. An install that can target by name
  // is probed for THAT set, which is what makes the returned list say which form the
  // caller is in (usesNameTargeting).
  const wanted = supportsNameTargeting(scriptsDir, action, instance)
    ? NAME_TARGET_PARAMS[action]
    : paramsForAction(action, instance, null);
  const file = scriptForAction(action);
  if (!wanted || !file) return [];
  return wanted.filter((p) => scriptSupportsParam(scriptsDir, file, p));
}

/** A PowerShell single-quoted string literal (embedded quotes doubled). */
function psSingleQuote(s) { return "'" + String(s).replace(/'/g, "''") + "'"; }

/**
 * Canonical Windows command-line quoting (CommandLineToArgvW rules), so an argv
 * element survives parsing by the child powershell.exe. Adapted from Microsoft's
 * "Everyone quotes command line arguments the wrong way": quote only when needed,
 * double the run of backslashes that precedes a `"` (or the closing quote), and
 * escape embedded `"` as `\"`.
 */
function winQuoteArg(arg) {
  arg = String(arg);
  if (arg !== "" && !/[ \t\n\v"]/.test(arg)) return arg;
  let out = '"';
  let bs = 0;
  for (let i = 0; i < arg.length; i++) {
    const c = arg[i];
    if (c === "\\") { bs++; continue; }
    if (c === '"') { out += "\\".repeat(bs * 2 + 1) + '"'; bs = 0; continue; }
    if (bs) { out += "\\".repeat(bs); bs = 0; }
    out += c;
  }
  return out + "\\".repeat(bs * 2) + '"';
}

/** The child powershell.exe command line: its argv, each canonically quoted.
 *  NO -NoExit: the scripts pause at the end themselves ("Press Enter to exit", on
 *  success OR error via try/finally), so the window stays readable — and WITHOUT
 *  -NoExit it then CLOSES on that Enter instead of dropping to an interactive
 *  PowerShell prompt (the reported "returns to a PowerShell thing" annoyance). */
function buildChildCommandLine(scriptPath, args, opts = {}) {
  const argv = ["-NoProfile", "-ExecutionPolicy", "Bypass"];
  if (opts.keepOpen) argv.push("-NoExit"); // debug: keep the (elevated) console open so errors stay readable
  argv.push("-File", scriptPath, ...args);
  return argv.map(winQuoteArg).join(" ");
}

/**
 * The outer PowerShell command that opens the host console. -ArgumentList is a
 * SINGLE pre-quoted STRING, not an array: Start-Process forwards a single-string
 * ArgumentList to the child verbatim, whereas an ARRAY is space-joined WITHOUT
 * re-quoting (so a spaced script path or a two-word -GitUserName would be split
 * apart). The string is embedded in a single-quoted PS literal (only `'` doubled).
 *
 * `-WindowStyle Normal` is explicit so the child gets a VISIBLE console. The
 * launcher is spawned DETACHED (no console of its own), so without this the inner
 * powershell can inherit "no console" and run windowless — the exact "toast fires,
 * no window, nothing happens" symptom. `-WindowStyle` coexists with `-Verb RunAs`.
 */
function buildOuterCommand(childCommandLine, opts = {}) {
  const verb = opts.elevate ? " -Verb RunAs" : "";
  return `Start-Process -FilePath 'powershell.exe'${verb} -WindowStyle Normal -ArgumentList ${psSingleQuote(childCommandLine)}`;
}

/**
 * The PowerShell call-operator invocation `& '<script>' <args>` used for the
 * NON-elevated single-console launch: the script runs directly in the console `start`
 * allocates (no inner Start-Process → no second window). Parameter NAMES stay bare and
 * every VALUE is single-quoted. All target params are [string]/[int]/[switch], so a
 * quoted string value binds (incl. [int] coercion, verified) and a bare -Switch sets it.
 *
 * WHICH TOKEN IS A NAME is decided STRUCTURALLY when an `argSpec` is given — the
 * (flag, value) pairs the args were built from. Values reaching this builder come from
 * the instance registry, which a user hand-edits: a vmHost of `-x; Start-Process calc; #`
 * is a valid JSON string, and emitting a leading-dash value bare would hand PowerShell
 * a fresh command to run. With the spec, a value is quoted whatever it starts with.
 *
 * WITHOUT a spec the ORIGINAL rule applies verbatim: a token starting with '-' is a
 * name, everything else is quoted. That is not a nicety — it is the zero-change bar.
 * buildInvocation attaches a spec ONLY for a non-default instance, so the command
 * string an install with no registry sends is byte-for-byte what it always was, down
 * to a value that itself begins with '-' (a project literally named "-NoProfile" is
 * still emitted bare there, exactly as before). Pure.
 */
function buildCallCommand(scriptPath, args, argSpec) {
  let toks;
  if (Array.isArray(argSpec)) {
    toks = [];
    for (const p of argSpec) {
      toks.push(String(p.flag));
      if ("value" in p) toks.push(psSingleQuote(p.value));
    }
  } else {
    toks = (args || []).map((a) => /^-/.test(String(a)) ? String(a) : psSingleQuote(a));
  }
  return "& " + psSingleQuote(scriptPath) + (toks.length ? " " + toks.join(" ") : "");
}

/**
 * Build the child_process invocation that opens a new host console running the
 * script. Pure (returns the argv; the caller spawns it).
 *
 * WHY `cmd /c start`: VS Code's extension host is a GUI process with NO console.
 * A powershell.exe spawned from it gets no console either (there's none to inherit,
 * and Node's child_process can't request CREATE_NEW_CONSOLE — `detached` sets the
 * OPPOSITE, DETACHED_PROCESS). A console-less launcher's `Start-Process` then opens
 * NO visible window — the "toast fires, no window, nothing happens" bug that removing
 * windowsHide alone did NOT fix (detached still suppressed the console). `start` is
 * the reliable Win32 primitive that forces a NEW CONSOLE for its target.
 *
 * ELEVATED (reinstall/redownload): the console runs `Start-Process -Verb RunAs …` to
 * raise the UAC prompt + open the elevated console (so there's a brief launcher window
 * + the elevated one — unavoidable for UAC). NON-ELEVATED (reprovision/export/update):
 * the console runs the script DIRECTLY via `& '<script>' <args>` — ONE window, no inner
 * Start-Process (that second window was the reported "two popups"). Only argv-safe
 * tokens (fixed powershell flags + the base64 blob) pass through cmd — no paths/user
 * values — so `start` adds no quoting surface; the empty "" is start's title slot.
 * `command` is the decoded inner command (for tests).
 */
function buildHostLaunch(scriptPath, args, opts = {}) {
  const elevate = !!opts.elevate;
  const keepOpen = !!opts.keepOpen; // debug: keep the console open on exit (errors stay readable)
  const command = elevate
    ? buildOuterCommand(buildChildCommandLine(scriptPath, args, { keepOpen }), opts) // -NoExit rides the elevated child
    : buildCallCommand(scriptPath, args, opts.argSpec);                              // & 'script' … (this console)
  const encoded = Buffer.from(command, "utf16le").toString("base64");
  // -NonInteractive only for the elevated launcher (it just fires Start-Process). The
  // non-elevated console RUNS the script here, so it must stay interactive for the
  // script's end-of-run "Press Enter to close" pause (and any in-console confirmation).
  const base = elevate
    ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass"]
    : ["-NoProfile", "-ExecutionPolicy", "Bypass"];
  // Non-elevated debug: -NoExit on THIS console (it runs the script). Elevated debug keeps
  // its console open via the child's -NoExit (above), not the transient launcher.
  if (keepOpen && !elevate) base.push("-NoExit");
  const psArgs = [...base, "-EncodedCommand", encoded];
  return {
    file: "cmd.exe",
    spawnArgs: ["/c", "start", "", "powershell.exe", ...psArgs],
    command,
  };
}

// Optional logger + debug-flag getter the extension wires in via configure(); lets the
// pure builders stay dependency-free while launchHostScript reports what it launched.
let _log = null;
let _isDebug = null;
/** Wire in a logger (fn(msg)) and a debug-flag getter (fn()->bool). Both optional. */
function configure(opts = {}) {
  if (opts && typeof opts.log === "function") _log = opts.log;
  if (opts && typeof opts.isDebug === "function") _isDebug = opts.isDebug;
}

/**
 * Modal confirm for a destructive (VM-deleting) action. Resolves true to go.
 *
 * `instance` (optional) is the CAPTURED target. For the default instance the dialog is
 * byte-identical to the one a single-VM install has always seen — it has one VM, and
 * naming it would be noise. For any OTHER instance the title names it and the detail
 * states the endpoint that is about to be deleted and rebuilt: with several VMs
 * configured, "Reinstall the Construct VM?" does not say WHICH, and this is the dialog
 * whose answer deletes a disk.
 */
async function confirmDestructive(inv, instance, _vscode) {
  const vscode = _vscode || vsc();
  const detail = inv.label === "Redownload"
    ? "This DELETES the VM and its virtual disk, re-downloads the Ubuntu ISO, then rebuilds and reinstalls from scratch."
    : "This DELETES the VM and its virtual disk, then rebuilds and reinstalls from the current ISO.";
  const named = instance && !instances.isDefaultInstance(instance);
  const title = named ? `${inv.label} the Construct VM “${instance.name}”?` : `${inv.label} the Construct VM?`;
  // The endpoint only for a REMOTE instance: that VM lives on somebody else's host, so
  // the address is the only thing that distinguishes it from a local VM of the same name.
  const where = (named && isRemoteBackend(instance.backend))
    ? ` The VM is “${instance.vmName}” on ${instance.vmHost}${Number(instance.sshPort) === 22 ? "" : ":" + instance.sshPort}.`
    : "";
  const pick = await vscode.window.showWarningMessage(
    title,
    { modal: true, detail: detail + where + " This is irreversible and cannot be undone." },
    inv.label
  );
  return pick === inv.label;
}

// Spawn options for the `cmd /c start` launcher. No windowsHide: it would set
// CREATE_NO_WINDOW on cmd, which could suppress the console `start` allocates.
// `detached` + unref let cmd (which exits the moment `start` fires) not tie to VS
// Code; the started console is its own process and outlives VS Code regardless.
// `extraEnv` (optional) is merged over the inherited environment and reaches the
// launched console (and, when elevated, the Start-Process child) — used to pass a
// result-file path to the script without adding a parameter old scripts would reject.
// Exposed for the regression test that pins "no windowsHide".
function hostLaunchSpawnOptions(cwd, extraEnv) {
  const o = { cwd, detached: true, stdio: "ignore" };
  if (extraEnv && typeof extraEnv === "object") o.env = { ...process.env, ...extraEnv };
  return o;
}

/**
 * Spawn a host console running <scriptsDir>/<script> with the given args, opening
 * a new (optionally elevated) window. Shared by the lifecycle actions and the
 * Construct update refresh. Guards off-Windows. `opts`:
 * { scriptsDir, script, args, argSpec?, elevate, label, env? }. `argSpec` is
 * buildInvocation's (flag, value) pair list — pass it whenever there is one, so the
 * non-elevated command string quotes values structurally. `env` is merged into the launched
 * process environment (reaches the script). `opts._spawn`/`_vscode`/`_platform` are test
 * seams (default child_process.spawn / the real vscode / process.platform). Returns true
 * if spawned.
 */
function launchHostScript(opts) {
  const vscode = opts._vscode || vsc();
  const spawn = opts._spawn || cp.spawn;
  const platform = opts._platform || process.platform;
  const log = opts.log || _log || (() => {});
  const debug = typeof opts.debug === "boolean" ? opts.debug : (_isDebug ? !!_isDebug() : false);
  if (platform !== "win32") {
    log(`launch ${opts.label}: skipped — not on Windows (platform=${platform})`);
    vscode.window.showWarningMessage("Construct actions run on the Windows host, which isn't available here.");
    return false;
  }
  const scriptPath = path.join(opts.scriptsDir, opts.script);
  const { file, spawnArgs, command } = buildHostLaunch(scriptPath, opts.args || [],
    { elevate: !!opts.elevate, keepOpen: debug, argSpec: opts.argSpec });
  // Deterministic record of exactly WHAT we launch (reveals version skew / wrong paths /
  // bad args). The decoded command shows the real script path + args reaching powershell.
  log(`launch ${opts.label}: elevate=${!!opts.elevate} debug=${debug} script=${scriptPath}`);
  log(`  command: ${command}`);
  if (opts.env) log(`  env: ${Object.keys(opts.env).join(", ")}`);
  try {
    const child = spawn(file, spawnArgs, hostLaunchSpawnOptions(opts.scriptsDir, opts.env));
    child.on("error", (e) => { log(`launch ${opts.label}: spawn error — ${e.message}`); vscode.window.showErrorMessage(`Couldn't launch ${opts.label}: ${e.message}`); });
    child.unref();
    log(`launch ${opts.label}: spawned (${file})`);
    vscode.window.showInformationMessage(
      `${opts.label} launched in a console window on the host${opts.elevate ? " — approve the UAC prompt." : "."}`
    );
    return true;
  } catch (e) {
    log(`launch ${opts.label}: threw — ${e && e.message ? e.message : e}`);
    vscode.window.showErrorMessage(`Couldn't launch ${opts.label}: ${e && e.message ? e.message : e}`);
    return false;
  }
}

/**
 * Run a lifecycle action. `opts`: { scriptsDir, backupMode?, projects?, enabled?, env?,
 * instance?, stillCurrent? }. `instance` is the active instance (src/instances.js);
 * omitted or default => the launched argv is byte-identical to before instances existed.
 * `stillCurrent` is the caller's captured-target predicate, re-asked AFTER the destructive
 * confirmation and immediately before anything is cleared or launched (see below) — it is
 * the only thing standing between a modal the user left open and a rebuild of the instance
 * this window has since left.
 * scriptsDir must be pre-resolved by the caller (it owns the construct.scriptsDir
 * setting); `enabled` is the setCheckpoints on/off. The destructive actions confirm
 * first; everything launches a new host console.
 *
 * Returns FALSE when nothing was (or will be) launched — not on Windows, no scripts
 * dir, an unbuildable action, or a REFUSED one (a non-default instance this install
 * can't target; the refusal is shown to the user here). True means the launch is under
 * way (possibly behind the destructive-action confirmation).
 */
function run(action, opts = {}) {
  // Same test seams launchHostScript has (`_vscode`/`_platform`/`_spawn`, defaulting to
  // the real ones), so the confirm-then-launch ORDERING — which is where the captured
  // target is verified — is exercised directly instead of only being modelled.
  const vscode = opts._vscode || vsc();
  if ((opts._platform || process.platform) !== "win32") {
    vscode.window.showWarningMessage("Construct lifecycle actions run on the Windows host, which isn't available here.");
    return false;
  }
  const scriptsDir = opts.scriptsDir;
  if (!scriptsDir) return false; // caller warns when it can't resolve the scripts dir
  // Prefer the caller-supplied selection (the extension computes the EFFECTIVE set —
  // saved selection, else the VM's current projects); fall back to the saved selection.
  // The settings and the project selection a run replays belong to the INSTANCE it
  // targets, not to the checkout: for the default instance the store IS this scripts
  // dir's .construct-settings.json (byte-identical to before), for any other one it is
  // that VM's own state file.
  const store = instancestate.store(opts.instance, scriptsDir);
  let projects = Array.isArray(opts.projects) ? opts.projects : null;
  if (!projects) { try { projects = instancestate.readSelectedProjects(store); } catch (_) { projects = []; } }
  const inv = buildInvocation(action, {
    settings: instancestate.readSettings(store),
    backupDir: path.join(scriptsDir, BACKUP_DIR_NAME),
    backupMode: opts.backupMode,
    projects,
    enabled: opts.enabled,
    instance: opts.instance,
    instanceParams: instanceParamSupport(scriptsDir, action, opts.instance),
    supportsCheckpoints: scriptSupportsCheckpoints(scriptsDir),
    supportsT3CodeChannel: scriptSupportsT3CodeChannel(scriptsDir, action),
    supportsT3CodeLimitResume: scriptSupportsT3CodeLimitResume(scriptsDir, action),
    supportsOpenCodeBackgroundWatcher: scriptSupportsOpenCodeBackgroundWatcher(scriptsDir, action),
  });
  // A refusal, not an invocation: this install cannot TARGET the active instance, and
  // running anyway would hit the default VM. Say which and stop — never fall back.
  if (inv && inv.blocked) {
    const log = _log || (() => {});
    log(`lifecycle ${action}: refused — ${inv.reason}`);
    vscode.window.showErrorMessage(inv.reason);
    return false;
  }
  if (!inv) return false;
  // Honesty gate: when the scripts are too old to take -AutomaticCheckpoints we drop the
  // flag (see buildInvocation) — but an old Create-AgentVM.ps1 hardcodes automatic
  // checkpoints ON, so silently rebuilding would produce the OPPOSITE of the saved
  // preference. Say so before the rebuild rather than after.
  if (inv.destructive && !inv.args.includes("-AutomaticCheckpoints")) {
    // ABSENT counts as "wants off": the panel's toggle defaults to off and that IS the
    // product default, so a user who never touched it still expects a rebuilt VM to have
    // checkpoints disabled. Only an explicit `true` is unaffected by an old script.
    let wantsOff = true;
    try { wantsOff = instancestate.readSettings(store).autoCheckpoints !== true; } catch (_) {}
    if (wantsOff && !scriptSupportsCheckpoints(scriptsDir)) {
      vscode.window.showWarningMessage(
        "These host scripts are too old to honour the “Automatic checkpoints: off” setting, so the rebuilt VM will have Hyper-V's automatic checkpoints ON. Update Construct first to avoid that."
      );
    }
  }
  const confirmed = Promise.resolve(inv.destructive ? confirmDestructive(inv, opts.instance, opts._vscode) : true);
  runPending = confirmed.then((ok) => {
    if (!ok) return;
    // THE CONFIRMATION IS AN AWAIT, AND THIS IS WHAT IS ON THE OTHER SIDE OF IT.
    // Callers check their captured generation BEFORE calling run(), but the modal above
    // opens inside run() and can sit there for as long as the user leaves it — long
    // enough for another window to change the global selection, or for the registry to be
    // rewritten. Accepting then would delete and rebuild the instance this window has
    // already left. `stillCurrent` is the caller's captured-target predicate; it reports
    // (log + a warning naming both instances) and returns false when the target is gone.
    // Omitted => nothing to compare against, and the flow is byte-identical to before.
    if (typeof opts.stillCurrent === "function") {
      let current = false;
      try { current = opts.stillCurrent() !== false; }
      catch (_) { current = false; }   // an unusable predicate fails CLOSED: nothing is deleted
      if (!current) {
        (_log || (() => {}))(`lifecycle ${action}: aborted after the confirmation — the window is no longer on "${(opts.instance && opts.instance.name) || "the captured instance"}"`);
        return;
      }
    }
    // A rebuild REPLACES the VM, so what was last confirmed onto the old one says nothing
    // about the new one. Clearing the marker keeps the (permission-gated) apply-offer
    // honest: a stale "already applied off" must not suppress the offer for a fresh VM
    // that an old script just created with checkpoints on.
    if (inv.destructive) {
      try { instancestate.saveAppliedAutoCheckpoints(store, null); } catch (_) { /* best-effort */ }
    }
    launchHostScript({
      scriptsDir, script: inv.script, args: inv.args, argSpec: inv.argSpec,
      elevate: inv.elevate, label: inv.label, env: opts.env,
      _vscode: opts._vscode, _platform: opts._platform, _spawn: opts._spawn,
    });
  });
  return true;
}

/**
 * The tail of the LAST run() — the promise that settles once its confirmation has been
 * answered and the launch (or the abort) has happened. Test-only observability: run()
 * itself stays synchronous and returns the same true/false it always did, so no caller
 * behaviour changes.
 */
let runPending = Promise.resolve();
function runSettled() { return runPending; }

module.exports = {
  PROVISION, AUTO_INSTALL, CHECKPOINTS, BACKUP_DIR_NAME,
  INSTANCE_PARAMS, REQUIRED_INSTANCE_PARAMS, ACTION_LABELS,
  REMOTE_INSTANCE_PARAMS, REQUIRED_REMOTE_INSTANCE_PARAMS,
  NAME_TARGET_PARAMS, REQUIRED_NAME_TARGET_PARAMS, INSTANCE_TARGET_LIB,
  usesNameTargeting, supportsNameTargeting,
  isRemoteBackend, paramsForAction, requiredParamsForAction,
  instanceArgs, instanceArgPairs, flattenArgPairs, checkInstanceSupport,
  derivedConfigBranch, configBranchOverride,
  scriptSupportsParam, scriptForAction, instanceParamSupport,
  normalizeBackupMode, buildInvocation, scriptSupportsCheckpoints, scriptSupportsT3CodeChannel,
  scriptSupportsT3CodeLimitResume,
  scriptSupportsOpenCodeBackgroundWatcher,
  psSingleQuote, winQuoteArg, buildChildCommandLine, buildOuterCommand, buildCallCommand, buildHostLaunch,
  hostLaunchSpawnOptions, launchHostScript, run, runSettled, confirmDestructive, configure,
};
