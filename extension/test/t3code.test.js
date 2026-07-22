"use strict";
// Plain-node unit tests for the T3 Code live-control script builders + the
// pairing-URL extractor. No deps. Run: node t3code.test.js
const t3 = require("../src/t3code");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── buildInstallScript ────────────────────────────────────────────────────────
const inst = t3.buildInstallScript();
ok("install: reads config.env with defaults", /CONFIG_FILE=\/etc\/construct\/config\.env/.test(inst) && /T3CODE_PORT:-5177/.test(inst) && /T3CODE_HOST:-0\.0\.0\.0/.test(inst));
ok("install: bootstraps Node when npm missing", /command -v npm/.test(inst) && /deb\.nodesource\.com\/setup_22\.x/.test(inst));
// t3's engines floor is ^22.16 || ^23.11 || >=24.10 — npm only warns on a
// mismatch, so the script must check the running Node version, not npm presence.
ok("install: checks the Node version against t3's engines floor",
  /t3_node_ok/.test(inst) && /-ge 16/.test(inst) && /-ge 11/.test(inst) && /-ge 10/.test(inst) && /\|\| ! t3_node_ok/.test(inst));
// `t3 serve` hardcodes forceAutoBootstrapProjectFromCwd:false, so the CLI flag
// is dead there — projects are bootstrapped explicitly, one per git repo.
ok("install: no dead --auto-bootstrap flag on ExecStart; explicit per-repo project add",
  !/ExecStart[^\n]*auto-bootstrap-project-from-cwd/.test(inst) && /t3 project add/.test(inst) && /\.git/.test(inst));
ok("install: npm installs t3 with build scripts allowed", /npm install -g t3@latest --allow-scripts=node-pty,msgpackr-extract/.test(inst));
ok("install: persists the T3CODE opt-in + bind keys", /cfgset T3CODE true/.test(inst) && /cfgset T3CODE_HOST/.test(inst) && /cfgset T3CODE_PORT/.test(inst));
ok("install: writes the t3code-serve unit", /\/etc\/systemd\/system\/t3code-serve\.service/.test(inst) && /EnvironmentFile=\/etc\/construct\/config\.env/.test(inst));
// The unit's ExecStart placeholders must reach the FILE as literal ${...} for
// systemd to expand — i.e. escaped (\$) inside the unquoted heredoc.
ok("install: unit placeholders escaped for systemd, not the shell", /--host \\\$\{T3CODE_HOST\} --port \\\$\{T3CODE_PORT\}/.test(inst));
ok("install: enables + restarts the service and verifies it's active", /systemctl enable t3code-serve/.test(inst) && /systemctl restart t3code-serve/.test(inst) && /is-active --quiet t3code-serve/.test(inst));

// ── buildDisableScript ────────────────────────────────────────────────────────
const dis = t3.buildDisableScript();
ok("disable: clears the opt-in flag", /cfgset T3CODE false/.test(dis));
ok("disable: stops + disables the service, best-effort", /systemctl disable --now t3code-serve/.test(dis) && /exit 0/.test(dis));

// ── buildPairingScript ────────────────────────────────────────────────────────
const pair = t3.buildPairingScript();
ok("pairing: mints a one-time token as JSON against the mshome.net base URL",
  /t3 auth pairing create --json/.test(pair) && /mshome\.net/.test(pair) && /--base-url/.test(pair));
ok("pairing: silences CLI logs so stdout stays parseable", /--log-level none/.test(pair));

// ── extractPairUrl ────────────────────────────────────────────────────────────
const clean = JSON.stringify({ id: "x", credential: "ABC", pairUrl: "http://agent-vm.mshome.net:5177/pair#token=ABC" });
ok("extractPairUrl: clean JSON", t3.extractPairUrl(clean) === "http://agent-vm.mshome.net:5177/pair#token=ABC");
const dirty = "[12:00:00.000] INFO (#1): noise\n" + clean;
ok("extractPairUrl: tolerates stray log lines", t3.extractPairUrl(dirty) === "http://agent-vm.mshome.net:5177/pair#token=ABC");
ok("extractPairUrl: empty on garbage", t3.extractPairUrl("no json here") === "" && t3.extractPairUrl("") === "" && t3.extractPairUrl(null) === "");

// ── baseUrl fallback ──────────────────────────────────────────────────────────
ok("baseUrl: defaults to the VM DNS + default port", t3.baseUrl() === "http://agent-vm.mshome.net:5177");
ok("baseUrl: honors a cfg vmHost override", t3.baseUrl({ vmHost: "other.host" }) === "http://other.host:5177");

// ── the generated bash parses ────────────────────────────────────────────────
// (bash -n via child_process; skipped quietly when bash isn't available, e.g. a
// bare Windows host running the suite.)
try {
  const cp = require("child_process");
  for (const [name, script] of [["install", inst], ["disable", dis], ["pairing", pair]]) {
    const r = cp.spawnSync("bash", ["-n"], { input: script, encoding: "utf8" });
    if (r.error) { console.log("  SKIP  bash -n (" + name + ") — bash unavailable"); continue; }
    ok("bash -n: " + name + " script parses", r.status === 0, (r.stderr || "").trim());
  }
} catch (_) { /* best-effort */ }

console.log(`\n  t3code unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
