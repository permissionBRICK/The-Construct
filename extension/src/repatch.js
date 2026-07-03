"use strict";
// Startup patch verification + repair for the Construct VM's anthropic.claude-code
// extension.
//
// WHY. Construct patches two gates into the installed claude-code extension.js on the
// VM — partial-message streaming (see construct-partial-streaming-enable.sh) and the
// chat-mic speech gate (construct-audio-enable.sh). Those patches are applied at
// provision time, but VS Code auto-updates the claude-code extension in the
// background: on a later start the extension is a fresh, UN-patched build and the
// features silently regress (the chat panel freezes until each turn finishes; the mic
// button disappears). A reprovision would fix it, but that's a heavy, manual step.
//
// WHAT. A short while after the control-panel extension activates (giving any VS Code-
// start auto-update time to land), verify the two gates over SSH and re-run the
// matching enable script for any feature that is (a) turned on in the user's settings
// and (b) currently sitting at the stock gate. The probe (construct-patch-status.sh)
// is read-only; the enable scripts are idempotent and best-effort, so a false alarm or
// an unreachable VM costs nothing. All the SSH/vscode wiring is injected so the whole
// unit tests with a fake ssh runner and no network (mirrors src/audio.js's seams).

/**
 * Parse the two KEY=VALUE lines construct-patch-status.sh prints on stdout into
 * { partial, gate }. Each is one of "patched" | "stock" | "unknown" | "absent", or
 * null when the line is missing (an old/garbled probe). Pure. */
function parsePatchStatus(stdout) {
  const s = String(stdout == null ? "" : stdout);
  const pick = (key) => {
    // Anchor to a line start so a value can't be spoofed by earlier output, and take
    // the LAST occurrence so trailing output wins if the script somehow prints twice.
    const re = new RegExp("^" + key + "=(patched|stock|unknown|absent)\\s*$", "gm");
    let m, last = null;
    while ((m = re.exec(s)) !== null) last = m[1];
    return last;
  };
  return { partial: pick("CONSTRUCT_PARTIAL_STATUS"), gate: pick("CONSTRUCT_GATE_STATUS") };
}

/**
 * Plan the startup pass's branches from the current prefs + live audio state, WITHOUT
 * touching ssh/vscode, so every combination is unit-testable. Returns:
 *   runPass       whether to run runStartupRepatch (probe + SSH repairs) at all
 *   passMicOn     the micOn to hand runStartupRepatch — a gate-only SSH repair only
 *                 makes sense with a live tunnel (micOn && micLive)
 *   retryAutoArm  whether to retry the FULL mic auto-arm (installs shim, patches gate,
 *                 opens the tunnel). Wanted whenever mic is on but there is no HostAudio
 *                 instance at all — the VM was down at activate so the auto-arm bailed,
 *                 or its enable failed and cleared the instance. Deliberately does NOT
 *                 depend on the probe's reachability: the caller's maybeAutoEnableAudio
 *                 does its own reachability check, so gating this on runStartupRepatch's
 *                 reachable flag would wrongly skip the retry when streaming is off and
 *                 the pass short-circuits (streamingOff + micOn + no tunnel).
 * `hasHostAudio` is whether a HostAudio instance exists (enabled OR still enabling);
 * guarding on it — not on micLive — avoids clobbering an enable that is in flight. Pure. */
function planStartupActions({ streamingOn, micOn, micLive, hasHostAudio }) {
  const gateRepairWanted = !!micOn && !!micLive;
  return {
    runPass: !!streamingOn || gateRepairWanted,
    passMicOn: gateRepairWanted,
    retryAutoArm: !!micOn && !hasHostAudio,
  };
}

/**
 * Decide which features need a re-patch. A feature is repaired only when it is turned
 * ON in the user's settings AND the probe found the stock (un-patched) gate — i.e. an
 * update reverted it. "patched" (already good), "unknown" (build we can't patch) and
 * "absent" (no extension) are all left alone. Pure. */
function decideRepairs({ status, streamingOn, micOn }) {
  const st = status || {};
  return {
    streaming: !!streamingOn && st.partial === "stock",
    mic: !!micOn && st.gate === "stock",
  };
}

/** Did the enable script confirm the gate is now neutralised? Both enable scripts
 *  print CONSTRUCT_<X>_PATCHED=1 on stdout when the gate is patched (freshly or
 *  already). Pure. */
function confirmPatched(token, stdout) {
  return new RegExp(token + "=1(?![0-9])").test(String(stdout == null ? "" : stdout));
}

/**
 * Verify the VM's claude-code patches and repair any that regressed. Returns a plain
 * result object (never rejects); the caller logs/broadcasts from it.
 *
 * Injected deps (all required so this is hermetic in tests):
 *   ssh                { runRemoteScript, isReachable }  — the src/ssh module
 *   cfg                ssh config override (usually undefined → defaults)
 *   readVmScript       (basename) => string; reads a vm/*.sh file
 *   streamingOn        boolean — partial-streaming preference is on
 *   micOn              boolean — mic-gate repair is wanted (caller passes false when a
 *                      live tunnel is absent; it retries the full mic enable instead)
 *   buildMicEnableScript  () => string — the base64-wrapped construct-audio-enable.sh
 *                      with shim + ports embedded (audio.buildEnableScript). Only
 *                      called when a mic repair is actually needed.
 *   log                (msg) => void
 *   timeoutMs          per-enable SSH timeout (default 60s)
 */
async function runStartupRepatch(opts) {
  const {
    ssh,
    cfg,
    readVmScript,
    streamingOn,
    micOn,
    buildMicEnableScript,
    log = () => {},
    timeoutMs = 60000,
  } = opts || {};

  const result = {
    reachable: false,
    status: { partial: null, gate: null },
    repaired: { streaming: false, mic: false },
    error: null,
  };

  if (!streamingOn && !micOn) {
    log("repatch: no patched features are on; nothing to verify.");
    return result;
  }

  // Cheap reachability gate so a powered-off VM never stalls or throws on startup.
  if (!(await ssh.isReachable({ timeoutMs: 12000, cfg }))) {
    log("repatch: VM unreachable; skipping patch verification.");
    return result;
  }
  result.reachable = true;

  // Read-only probe of the current gate state.
  let sres;
  try {
    sres = await ssh.runRemoteScript(readVmScript("construct-patch-status.sh"), { timeoutMs: 20000, cfg });
  } catch (e) {
    result.error = "status-exception";
    log("repatch: patch-status probe threw: " + (e && e.message ? e.message : e));
    return result;
  }
  if (!sres || sres.code !== 0) {
    result.error = "status-failed";
    log("repatch: patch-status probe failed (code " + (sres && sres.code) + "); skipping.");
    return result;
  }

  const status = parsePatchStatus(sres.stdout || "");
  result.status = status;
  const want = decideRepairs({ status, streamingOn, micOn });
  log(
    "repatch: status partial=" + status.partial + " gate=" + status.gate +
    " -> repair streaming=" + want.streaming + " mic=" + want.mic
  );

  // Partial-message streaming: re-run the enable script (idempotent) when it regressed.
  if (want.streaming) {
    try {
      const r = await ssh.runRemoteScript(readVmScript("construct-partial-streaming-enable.sh"), { timeoutMs, cfg });
      result.repaired.streaming = !!(r && r.code === 0 && confirmPatched("CONSTRUCT_PARTIAL_PATCHED", r.stdout));
      if (result.repaired.streaming) {
        log("repatch: re-applied the partial-streaming gate (a claude-code update had reverted it). Reload the window to pick it up this session.");
      } else {
        log("repatch: partial-streaming re-apply did not confirm (code " + (r && r.code) + ").");
      }
    } catch (e) {
      log("repatch: partial-streaming re-apply threw: " + (e && e.message ? e.message : e));
    }
  }

  // Chat-mic speech gate: re-run construct-audio-enable.sh (installs the shim +
  // re-patches the gate, both idempotent) when the gate regressed under a live tunnel.
  if (want.mic && typeof buildMicEnableScript === "function") {
    const micScript = buildMicEnableScript();
    if (micScript) {
      try {
        const r = await ssh.runRemoteScript(micScript, { timeoutMs, cfg });
        result.repaired.mic = !!(r && r.code === 0 && confirmPatched("CONSTRUCT_GATE_PATCHED", r.stdout));
        if (result.repaired.mic) {
          log("repatch: re-applied the chat-mic gate (a claude-code update had reverted it). Reload the window to pick it up this session.");
        } else {
          log("repatch: chat-mic gate re-apply did not confirm (code " + (r && r.code) + ").");
        }
      } catch (e) {
        log("repatch: chat-mic gate re-apply threw: " + (e && e.message ? e.message : e));
      }
    }
  }

  return result;
}

module.exports = { parsePatchStatus, planStartupActions, decideRepairs, confirmPatched, runStartupRepatch };
