"use strict";
// Plain-node regression tests for the chat-mic speech-gate VM patch
// (extension/vm/construct-audio-enable.sh / construct-audio-disable.sh) — the guarded
// transform that neutralises the remoteName speech gate in the installed
// anthropic.claude-code-*/extension.js so the mic button works over Remote-SSH.
//
// Covered:
//   • enable — gate neutralised (remoteName term always-false, the isConfined check
//     and the feature gate survive verbatim), exactly one substitution, decoy
//     remoteName uses untouched;
//   • idempotency — a second enable skips (byte-identical) and keeps the first backup;
//   • CONSTRUCT_GATE_PATCHED=1/0 — the token the device agent's AudioProtocol confirms;
//   • disable — byte-for-byte restore from the backup, in-place revert without one;
//   • a full enable -> disable round-trip and unknown/absent builds.
//
// The fixture mirrors the real 2.1.332 extension.js (the stub isSpeechToTextEnabled
// that must NOT be touched included). Run: node audio.test.js
const { spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const ROOT = path.join(__dirname, "..", "..");
const ENABLE = path.join(ROOT, "extension", "vm", "construct-audio-enable.sh");
const DISABLE = path.join(ROOT, "extension", "vm", "construct-audio-disable.sh");
const SHIM = path.join(ROOT, "extension", "vm", "construct-rec-shim.sh");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);

// ── a realistic minified gate snippet (mirrors the real 2.1.332 extension.js) ────
// Decoys that must NOT be rewritten: the includePartialMessages gate (the partial-
// streaming patch's own anchor), an openURL remoteName branch, and the unsupported-
// environment stub. The speech gate itself gained `||this.isConfined()`.
const GATE_SNIPPET =
  'x={includePartialMessages:!Q.env.remoteName,agentProgress:1},' +
  'openURL(e){if(Q.env.remoteName){let t=process.platform;return t}},' +
  'isSpeechToTextEnabled(){return!1},' +
  'isSpeechToTextEnabled(){if(Q.env.remoteName||this.isConfined())return!1;' +
  'if(!this.claudeAiFeatureGate({hostBearer:!0})){if(fS(this.experimentGates)&&pG())this.reportStoredLoginWithheld("mic_button");return!1}return pG()}';
const STOCK_GATE = "remoteName||this.isConfined())return!1";
const PATCHED_GATE = "remoteName&&!1||this.isConfined())return!1";

function tmp() { return fs.mkdtempSync(path.join(os.tmpdir(), "construct-audio-test-")); }

// A fake HOME holding the extension build under test; returns { home, file }.
function makeHome(body, { dist = true } = {}) {
  const home = tmp();
  const dir = path.join(home, ".vscode-server", "extensions", "anthropic.claude-code-9.9.9-linux-x64", dist ? "dist" : "");
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, "extension.js");
  fs.writeFileSync(file, body);
  return { home, file };
}

function run(script, home) {
  return spawnSync("bash", [script], {
    encoding: "utf8",
    env: {
      ...process.env,
      HOME: home,
      CONSTRUCT_SHIM_B64: fs.readFileSync(SHIM).toString("base64"),
      CONSTRUCT_VM_PORT_BASE: "8767",
      CONSTRUCT_VM_PORT_COUNT: "8",
    },
  });
}

const token = (out, key) => (String(out).match(new RegExp("^" + key + "=(\\S+)$", "m")) || [])[1];
const count = (hay, needle) => hay.split(needle).length - 1;

// ── enable: neutralises only the speech gate ────────────────────────────────────
(() => {
  const { home, file } = makeHome(GATE_SNIPPET);
  const r = run(ENABLE, home);
  const src = fs.readFileSync(file, "utf8");
  eq("enable: exit 0", r.status, 0);
  eq("enable: CONSTRUCT_GATE_PATCHED=1", token(r.stdout, "CONSTRUCT_GATE_PATCHED"), "1");
  ok("enable: gate neutralised", src.includes(PATCHED_GATE), src.slice(0, 200));
  ok("enable: stock gate gone", !src.includes(STOCK_GATE));
  eq("enable: exactly one patched marker", count(src, PATCHED_GATE), 1);
  // The decoys survive verbatim.
  ok("enable: leaves the includePartialMessages gate alone", src.includes("includePartialMessages:!Q.env.remoteName,agentProgress"));
  ok("enable: leaves openURL remoteName use alone", src.includes("openURL(e){if(Q.env.remoteName){let t=process.platform"));
  ok("enable: leaves the stub isSpeechToTextEnabled alone", src.includes("isSpeechToTextEnabled(){return!1}"));
  ok("enable: leaves the feature-gate tail intact", src.includes('claudeAiFeatureGate({hostBearer:!0}'));
  // The isConfined check of the neutralised guard stays, so only the remoteName
  // disjunct is neutralised (the guard reads as `if(this.isConfined())return!1`).
  ok("enable: isConfined check preserved", src.includes("||this.isConfined())return!1"));
  ok("enable: stock build backed up", fs.existsSync(file + ".construct-audio.bak"));
  eq("enable: backup is the true original", fs.readFileSync(file + ".construct-audio.bak", "utf8"), GATE_SNIPPET);
  run(DISABLE, home); // also removes the recorder shim the enable installed
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── enable: idempotent, and disable restores byte-for-byte ─────────────────────
(() => {
  const { home, file } = makeHome(GATE_SNIPPET);
  run(ENABLE, home);
  const once = fs.readFileSync(file, "utf8");
  const r2 = run(ENABLE, home);
  eq("enable x2: still reports patched", token(r2.stdout, "CONSTRUCT_GATE_PATCHED"), "1");
  eq("enable x2: byte-identical to a single apply", fs.readFileSync(file, "utf8"), once);
  eq("enable x2: backup not clobbered", fs.readFileSync(file + ".construct-audio.bak", "utf8"), GATE_SNIPPET);
  run(DISABLE, home);
  eq("disable: restores the original byte-for-byte", fs.readFileSync(file, "utf8"), GATE_SNIPPET);
  ok("disable: backup consumed", !fs.existsSync(file + ".construct-audio.bak"));
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── disable without a backup reverts the marker in place ───────────────────────
(() => {
  const { home, file } = makeHome(GATE_SNIPPET);
  run(ENABLE, home);
  fs.rmSync(file + ".construct-audio.bak");
  run(DISABLE, home);
  eq("disable (no backup): stock gate restored in place", fs.readFileSync(file, "utf8"), GATE_SNIPPET);
  const r = run(DISABLE, home);
  eq("disable x2: exit 0", r.status, 0);
  eq("disable x2: unchanged", fs.readFileSync(file, "utf8"), GATE_SNIPPET);
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── the non-dist extension.js layout is covered too ─────────────────────────────
(() => {
  const { home, file } = makeHome(GATE_SNIPPET, { dist: false });
  const r = run(ENABLE, home);
  eq("layout: extension.js at the extension root patches", token(r.stdout, "CONSTRUCT_GATE_PATCHED"), "1");
  ok("layout: gate neutralised", fs.readFileSync(file, "utf8").includes(PATCHED_GATE));
  run(DISABLE, home);
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── unknown / absent builds are left untouched and reported as 0 ───────────────
(() => {
  const { home, file } = makeHome('a={foo:1};console.log("nothing to patch");');
  const r = run(ENABLE, home);
  eq("unknown: CONSTRUCT_GATE_PATCHED=0", token(r.stdout, "CONSTRUCT_GATE_PATCHED"), "0");
  eq("unknown: file untouched", fs.readFileSync(file, "utf8"), 'a={foo:1};console.log("nothing to patch");');
  ok("unknown: no backup written", !fs.existsSync(file + ".construct-audio.bak"));
  fs.rmSync(home, { recursive: true, force: true });

  const empty = tmp();
  const r2 = run(ENABLE, empty);
  eq("absent: CONSTRUCT_GATE_PATCHED=0", token(r2.stdout, "CONSTRUCT_GATE_PATCHED"), "0");
  fs.rmSync(empty, { recursive: true, force: true });
})();

// ── an already-patched build keeps its patch (round-trip stays stable) ─────────
(() => {
  const patched = GATE_SNIPPET.split(STOCK_GATE).join(PATCHED_GATE);
  const { home, file } = makeHome(patched);
  const r = run(ENABLE, home);
  eq("pre-patched: reports patched", token(r.stdout, "CONSTRUCT_GATE_PATCHED"), "1");
  eq("pre-patched: bytes unchanged", fs.readFileSync(file, "utf8"), patched);
  ok("pre-patched: no new backup", !fs.existsSync(file + ".construct-audio.bak"));
  run(DISABLE, home);
  eq("pre-patched: disable reverts to stock", fs.readFileSync(file, "utf8"), GATE_SNIPPET);
  fs.rmSync(home, { recursive: true, force: true });
})();

console.log(`\n  audio gate patch tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);