"use strict";
// Plain-node regression tests for the read-only startup patch probe
// (extension/vm/construct-patch-status.sh) — the script the upstream watch and the
// startup re-patch pass read to decide whether Construct's claude-code patches need
// re-applying after a background extension auto-update.
//
// Covered:
//   • the stock / patched / unknown / absent verdicts on realistic 2.1.332 gate
//     fixtures (both features independently);
//   • the stdout contract: exactly the two KEY=VALUE status lines consumers parse;
//   • the probe is purely observational (no edits, no backups);
//   • "stock" means "the enable script would patch this build" — verified by
//     running the real enable scripts and probing again;
//   • across several version dirs a stock copy outranks a patched sibling.
//
// Run: node repatch.test.js
const { spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const ROOT = path.join(__dirname, "..", "..");
const STATUS = path.join(ROOT, "extension", "vm", "construct-patch-status.sh");
const PARTIAL_ENABLE = path.join(ROOT, "extension", "vm", "construct-partial-streaming-enable.sh");
const AUDIO_ENABLE = path.join(ROOT, "extension", "vm", "construct-audio-enable.sh");
const SHIM = path.join(ROOT, "extension", "vm", "construct-rec-shim.sh");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);

// Realistic 2.1.332-shaped gates: the partial-streaming anchor and the chat-mic
// speech gate with its isConfined() check, plus their neutralised forms.
const STOCK =
  'a={includePartialMessages:!Q.env.remoteName,x:1};' +
  'function f(){if(Q.env.remoteName||this.isConfined())return!1;}';
const PATCHED =
  'a={includePartialMessages:!Q.env.remoteName||!0,x:1};' +
  'function f(){if(Q.env.remoteName&&!1||this.isConfined())return!1;}';

function tmp() { return fs.mkdtempSync(path.join(os.tmpdir(), "construct-repatch-test-")); }

function makeHome(body, version = "9.9.9") {
  const home = tmp();
  const dir = path.join(home, ".vscode-server", "extensions", `anthropic.claude-code-${version}-linux-x64`, "dist");
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, "extension.js");
  fs.writeFileSync(file, body);
  return { home, file };
}

function probe(home) {
  const r = spawnSync("bash", [STATUS], { encoding: "utf8", env: { ...process.env, HOME: home } });
  const value = (key) => (String(r.stdout).match(new RegExp("^" + key + "=(\\w+)$", "m")) || [])[1];
  return { status: r.status, partial: value("CONSTRUCT_PARTIAL_STATUS"), gate: value("CONSTRUCT_GATE_STATUS"), stdout: r.stdout };
}

function enableAll(home) {
  spawnSync("bash", [PARTIAL_ENABLE], { encoding: "utf8", env: { ...process.env, HOME: home } });
  spawnSync("bash", [AUDIO_ENABLE], {
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

// ── verdicts on a stock build (the exact check the upstream watch requires) ─────
(() => {
  const { home, file } = makeHome(STOCK);
  const before = fs.readFileSync(file, "utf8");
  const s = probe(home);
  eq("probe: exit 0", s.status, 0);
  eq("stock build: partial reported stock", s.partial, "stock");
  eq("stock build: gate reported stock", s.gate, "stock");
  eq("probe: read-only (bytes unchanged)", fs.readFileSync(file, "utf8"), before);
  ok("probe: writes no backups", !fs.existsSync(file + ".construct-partial.bak") && !fs.existsSync(file + ".construct-audio.bak"));
  const lines = s.stdout.split(/\r?\n/).filter(Boolean);
  eq("probe: stdout is exactly the two status lines", lines.length, 2);
  ok("probe: status lines are KEY=VALUE", lines.every((l) => /^CONSTRUCT_(PARTIAL|GATE)_STATUS=\w+$/.test(l)), s.stdout);
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── verdicts on a patched build ─────────────────────────────────────────────────
(() => {
  const { home, file } = makeHome(PATCHED);
  const s = probe(home);
  eq("patched build: partial reported patched", s.partial, "patched");
  eq("patched build: gate reported patched", s.gate, "patched");
  eq("probe: patched bytes untouched", fs.readFileSync(file, "utf8"), PATCHED);
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── unknown build / no extension at all ────────────────────────────────────────
(() => {
  const { home } = makeHome('a={foo:1};console.log("nothing to patch");');
  const s = probe(home);
  eq("unknown build: partial reported unknown", s.partial, "unknown");
  eq("unknown build: gate reported unknown", s.gate, "unknown");
  fs.rmSync(home, { recursive: true, force: true });

  const empty = tmp();
  const s2 = probe(empty);
  eq("absent: partial reported absent", s2.partial, "absent");
  eq("absent: gate reported absent", s2.gate, "absent");
  fs.rmSync(empty, { recursive: true, force: true });
})();

// ── stock ⇔ the enable scripts would patch this build ───────────────────────────
(() => {
  const { home, file } = makeHome(STOCK);
  const before = probe(home);
  eq("crosscheck: starts stock (partial)", before.partial, "stock");
  eq("crosscheck: starts stock (gate)", before.gate, "stock");
  enableAll(home);
  const after = probe(home);
  eq("crosscheck: enable scripts turn partial stock->patched", after.partial, "patched");
  eq("crosscheck: enable scripts turn gate stock->patched", after.gate, "patched");
  ok("crosscheck: the neutralised gate is the one enable leaves", fs.readFileSync(file, "utf8").includes("remoteName&&!1||this.isConfined())return!1"));
  fs.rmSync(home, { recursive: true, force: true });
})();

// ── multi-version: a pending unpatched update beside a patched build is stock ──
(() => {
  const { home, file } = makeHome(PATCHED);
  const pending = path.join(home, ".vscode-server", "extensions", "anthropic.claude-code-9.9.10-linux-x64", "dist");
  fs.mkdirSync(pending, { recursive: true });
  fs.writeFileSync(path.join(pending, "extension.js"), STOCK);
  const s = probe(home);
  eq("multi: partial stock wins over a patched sibling", s.partial, "stock");
  eq("multi: gate stock wins over a patched sibling", s.gate, "stock");
  eq("multi: the patched copy is untouched", fs.readFileSync(file, "utf8"), PATCHED);
  fs.rmSync(home, { recursive: true, force: true });
})();

console.log(`\n  patch probe tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);