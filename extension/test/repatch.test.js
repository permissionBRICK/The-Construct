"use strict";
// Plain-node unit tests for the startup patch-verification logic (src/repatch.js).
//
// Covered:
//   • parsePatchStatus — the two status lines, missing/garbled lines, line-anchoring,
//     last-wins;
//   • decideRepairs — the ON-and-stock truth table (patched/unknown/absent skip);
//   • confirmPatched — the CONSTRUCT_*_PATCHED=1 token match (no 10/11 false positive);
//   • runStartupRepatch — the full orchestration against a FAKE ssh runner: unreachable
//     short-circuit, status-probe failure, the streaming-only / mic-only / both repair
//     paths, "already patched -> no re-apply", and that a real script read is exercised.
//
// No network / vscode — the ssh module and script reader are injected. Run: node repatch.test.js
const assert = require("assert");
const fs = require("fs");
const path = require("path");
const r = require("../src/repatch");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── parsePatchStatus ────────────────────────────────────────────────────────────
(function () {
  let s = r.parsePatchStatus("CONSTRUCT_PARTIAL_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n");
  ok("parse: reads both status values", s.partial === "stock" && s.gate === "patched", JSON.stringify(s));

  s = r.parsePatchStatus("CONSTRUCT_GATE_STATUS=absent\n");
  ok("parse: missing line -> null", s.partial === null && s.gate === "absent", JSON.stringify(s));

  s = r.parsePatchStatus("");
  ok("parse: empty -> both null", s.partial === null && s.gate === null, JSON.stringify(s));

  s = r.parsePatchStatus("garbage\nCONSTRUCT_PARTIAL_STATUS=bogusvalue\n");
  ok("parse: unrecognised value -> null", s.partial === null, JSON.stringify(s));

  // A value embedded mid-line (not at line start) must NOT be picked up.
  s = r.parsePatchStatus("prefixCONSTRUCT_PARTIAL_STATUS=patched\n");
  ok("parse: line-anchored (ignores mid-line match)", s.partial === null, JSON.stringify(s));

  // Last occurrence wins (defensive against a double-printed probe).
  s = r.parsePatchStatus("CONSTRUCT_GATE_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n");
  ok("parse: last occurrence wins", s.gate === "patched", JSON.stringify(s));

  // Real probe output with leading log noise on stderr wouldn't reach here; stdout is clean.
  s = r.parsePatchStatus("CONSTRUCT_PARTIAL_STATUS=unknown\r\nCONSTRUCT_GATE_STATUS=stock\r\n");
  ok("parse: tolerates CRLF", s.partial === "unknown" && s.gate === "stock", JSON.stringify(s));
})();

// ── decideRepairs ───────────────────────────────────────────────────────────────
(function () {
  const d = (status, streamingOn, micOn) => r.decideRepairs({ status, streamingOn, micOn });

  ok("decide: on + stock -> repair both",
    JSON.stringify(d({ partial: "stock", gate: "stock" }, true, true)) === JSON.stringify({ streaming: true, mic: true }));

  ok("decide: on + patched -> repair neither",
    JSON.stringify(d({ partial: "patched", gate: "patched" }, true, true)) === JSON.stringify({ streaming: false, mic: false }));

  ok("decide: off features never repaired even when stock",
    JSON.stringify(d({ partial: "stock", gate: "stock" }, false, false)) === JSON.stringify({ streaming: false, mic: false }));

  ok("decide: unknown build not repaired (can't patch it)",
    JSON.stringify(d({ partial: "unknown", gate: "unknown" }, true, true)) === JSON.stringify({ streaming: false, mic: false }));

  ok("decide: absent extension not repaired",
    JSON.stringify(d({ partial: "absent", gate: "absent" }, true, true)) === JSON.stringify({ streaming: false, mic: false }));

  ok("decide: only streaming stock -> only streaming",
    JSON.stringify(d({ partial: "stock", gate: "patched" }, true, true)) === JSON.stringify({ streaming: true, mic: false }));

  ok("decide: only mic stock -> only mic",
    JSON.stringify(d({ partial: "patched", gate: "stock" }, true, true)) === JSON.stringify({ streaming: false, mic: true }));

  ok("decide: null status (garbled probe) -> no repair",
    JSON.stringify(d({ partial: null, gate: null }, true, true)) === JSON.stringify({ streaming: false, mic: false }));
})();

// ── planStartupActions ──────────────────────────────────────────────────────────
// The extension.js branching, extracted pure. The key regression is the
// streamingOff + micOn + no-tunnel combo, which must still retry the full auto-arm.
(function () {
  const plan = (o) => r.planStartupActions(o);
  const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);

  // REGRESSION (reviewer batch-1): streaming off, mic on, no HostAudio (no tunnel).
  // Must NOT run the SSH pass but MUST retry the full mic auto-arm.
  ok("plan: streamingOff+micOn+noHostAudio -> retryAutoArm, no pass",
    eq(plan({ streamingOn: false, micOn: true, micLive: false, hasHostAudio: false }),
       { runPass: false, passMicOn: false, retryAutoArm: true }));

  // Mic on with a live tunnel: gate-only SSH repair, no auto-arm retry.
  ok("plan: micOn+micLive -> runPass with passMicOn, no retry",
    eq(plan({ streamingOn: false, micOn: true, micLive: true, hasHostAudio: true }),
       { runPass: true, passMicOn: true, retryAutoArm: false }));

  // Streaming on, mic off: run the pass for streaming only.
  ok("plan: streamingOn+micOff -> runPass, no mic, no retry",
    eq(plan({ streamingOn: true, micOn: false, micLive: false, hasHostAudio: false }),
       { runPass: true, passMicOn: false, retryAutoArm: false }));

  // Streaming on, mic on, no tunnel: pass runs for streaming AND we retry the auto-arm.
  ok("plan: streamingOn+micOn+noHostAudio -> runPass + retryAutoArm",
    eq(plan({ streamingOn: true, micOn: true, micLive: false, hasHostAudio: false }),
       { runPass: true, passMicOn: false, retryAutoArm: true }));

  // Mic on, an enable is still in flight (HostAudio exists, not yet enabled): no retry
  // (would clobber it), no gate repair (no live tunnel yet).
  ok("plan: micOn + enabling-in-flight -> no retry, no gate pass",
    eq(plan({ streamingOn: false, micOn: true, micLive: false, hasHostAudio: true }),
       { runPass: false, passMicOn: false, retryAutoArm: false }));

  // Everything on and live: pass runs for both, no retry.
  ok("plan: streamingOn+micOn+micLive -> runPass both, no retry",
    eq(plan({ streamingOn: true, micOn: true, micLive: true, hasHostAudio: true }),
       { runPass: true, passMicOn: true, retryAutoArm: false }));
})();

// ── confirmPatched ──────────────────────────────────────────────────────────────
(function () {
  ok("confirm: =1 matches", r.confirmPatched("CONSTRUCT_PARTIAL_PATCHED", "CONSTRUCT_PARTIAL_PATCHED=1\n"));
  ok("confirm: =0 does not match", !r.confirmPatched("CONSTRUCT_PARTIAL_PATCHED", "CONSTRUCT_PARTIAL_PATCHED=0\n"));
  ok("confirm: =10 does not false-match =1", !r.confirmPatched("CONSTRUCT_GATE_PATCHED", "CONSTRUCT_GATE_PATCHED=10\n"));
  ok("confirm: empty -> false", !r.confirmPatched("CONSTRUCT_GATE_PATCHED", ""));
})();

// ── runStartupRepatch orchestration (fake ssh + script reader) ──────────────────
// A recording ssh double: scripted per-call replies keyed by a marker we embed in the
// script text, plus a call log so we can assert exactly what ran.
function fakeSsh({ reachable = true, statusStdout = "", statusCode = 0, replies = {} }) {
  const calls = [];
  return {
    calls,
    async isReachable() { return reachable; },
    async runRemoteScript(scriptText, opts) {
      calls.push(scriptText);
      // Route by a recognisable substring of each vm/*.sh (or the injected marker).
      if (/construct-patch-status/.test(scriptText)) return { code: statusCode, stdout: statusStdout, stderr: "" };
      if (/PARTIAL_ENABLE/.test(scriptText)) return replies.partial || { code: 0, stdout: "CONSTRUCT_PARTIAL_PATCHED=1\n", stderr: "" };
      if (/AUDIO_ENABLE/.test(scriptText)) return replies.audio || { code: 0, stdout: "CONSTRUCT_GATE_PATCHED=1\n", stderr: "" };
      return { code: 0, stdout: "", stderr: "" };
    },
  };
}

// Script reader that returns a marker naming the file so the fake ssh can route on it,
// while still proving the orchestration reads the RIGHT basenames.
function markerReader(seen) {
  return (basename) => {
    seen.push(basename);
    if (basename === "construct-patch-status.sh") return "run construct-patch-status.sh";
    if (basename === "construct-partial-streaming-enable.sh") return "PARTIAL_ENABLE";
    return basename;
  };
}

async function run() {
  // 1) Unreachable VM: no probe, no repair, reachable=false.
  {
    const s = fakeSsh({ reachable: false });
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: true, micOn: false, log: () => {},
    });
    ok("run: unreachable short-circuits (no ssh script calls)", res.reachable === false && s.calls.length === 0, JSON.stringify(res));
  }

  // 2) Nothing on: returns immediately without touching ssh.
  {
    const s = fakeSsh({});
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: false, micOn: false, log: () => {},
    });
    ok("run: no features on -> no ssh at all", s.calls.length === 0 && res.repaired.streaming === false);
  }

  // 3) Status probe fails: reachable but no repair attempted.
  {
    const s = fakeSsh({ statusCode: 1, statusStdout: "" });
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: true, micOn: false, log: () => {},
    });
    ok("run: probe failure -> reachable, error set, no enable call",
      res.reachable === true && res.error === "status-failed" && s.calls.length === 1, JSON.stringify(res));
  }

  // 4) Streaming stock -> streaming enable runs and confirms; mic left alone.
  {
    const seen = [];
    const s = fakeSsh({ statusStdout: "CONSTRUCT_PARTIAL_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n" });
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader(seen), streamingOn: true, micOn: true,
      buildMicEnableScript: () => "AUDIO_ENABLE", log: () => {},
    });
    ok("run: streaming stock -> streaming repaired, mic not",
      res.repaired.streaming === true && res.repaired.mic === false, JSON.stringify(res));
    ok("run: read the status + streaming-enable scripts, not audio",
      seen.includes("construct-patch-status.sh") && seen.includes("construct-partial-streaming-enable.sh"), seen.join(","));
    ok("run: did NOT run the audio enable script (gate was patched)",
      !s.calls.some((c) => /AUDIO_ENABLE/.test(c)), s.calls.length + " calls");
  }

  // 5) Mic stock -> audio enable runs and confirms; streaming already patched.
  {
    const s = fakeSsh({ statusStdout: "CONSTRUCT_PARTIAL_STATUS=patched\nCONSTRUCT_GATE_STATUS=stock\n" });
    let built = 0;
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: true, micOn: true,
      buildMicEnableScript: () => { built++; return "AUDIO_ENABLE"; }, log: () => {},
    });
    ok("run: mic stock -> mic repaired, streaming not", res.repaired.mic === true && res.repaired.streaming === false, JSON.stringify(res));
    ok("run: buildMicEnableScript called exactly once", built === 1, "built=" + built);
  }

  // 6) Both patched -> reachable, probe only, nothing repaired.
  {
    const s = fakeSsh({ statusStdout: "CONSTRUCT_PARTIAL_STATUS=patched\nCONSTRUCT_GATE_STATUS=patched\n" });
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: true, micOn: true,
      buildMicEnableScript: () => "AUDIO_ENABLE", log: () => {},
    });
    ok("run: both patched -> only the probe ran", s.calls.length === 1 && !res.repaired.streaming && !res.repaired.mic);
  }

  // 7) Enable ran but did not confirm (unknown build slipped in): repaired stays false.
  {
    const s = fakeSsh({
      statusStdout: "CONSTRUCT_PARTIAL_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n",
      replies: { partial: { code: 0, stdout: "CONSTRUCT_PARTIAL_PATCHED=0\n", stderr: "" } },
    });
    const res = await r.runStartupRepatch({
      ssh: s, readVmScript: markerReader([]), streamingOn: true, micOn: false, log: () => {},
    });
    ok("run: enable without confirmation -> repaired=false", res.repaired.streaming === false, JSON.stringify(res));
  }

  // 8) Real script read: the injected default reader resolves the actual vm/ files, so
  //    a rename of the scripts would break this (guards the basenames the code asks for).
  {
    const vmDir = path.join(__dirname, "..", "vm");
    for (const f of ["construct-patch-status.sh", "construct-partial-streaming-enable.sh", "construct-audio-enable.sh", "construct-rec-shim.sh"]) {
      ok("scripts: vm/" + f + " exists on disk", fs.existsSync(path.join(vmDir, f)));
    }
  }
}

run().then(() => {
  console.log(`\n  repatch startup-verification unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
}).catch((e) => { console.error(e); process.exit(1); });
