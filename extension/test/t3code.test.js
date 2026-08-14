"use strict";
// Plain-node unit tests for the T3 Code live-control script builders + the
// pairing-URL extractor. No deps. Run: node t3code.test.js
const t3 = require("../src/t3code");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── npmTag ────────────────────────────────────────────────────────────────────
ok("npmTag: stable -> latest", t3.npmTag("stable") === "latest");
ok("npmTag: nightly -> nightly", t3.npmTag("nightly") === "nightly");
ok("npmTag: undefined -> latest (default)", t3.npmTag(undefined) === "latest");
ok("npmTag: garbage -> latest (normalize)", t3.npmTag("alpha") === "latest");

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
ok("install(stable): npm installs t3@latest with build scripts allowed", /npm install -g t3@latest --allow-scripts=node-pty,msgpackr-extract/.test(inst));
// node-pty has no Linux prebuilds — its install always node-gyp-rebuilds, so
// the compiler toolchain must be in place before npm runs the build scripts.
ok("install: provisions the compiler toolchain before npm (node-pty gyp build)",
  /command -v g\+\+/.test(inst) && /apt-get install -y build-essential python3/.test(inst) &&
  inst.indexOf("build-essential") < inst.indexOf("npm install -g t3@latest"));
ok("install: persists the T3CODE opt-in + bind keys + channel", /cfgset T3CODE true/.test(inst) && /cfgset T3CODE_HOST/.test(inst) && /cfgset T3CODE_PORT/.test(inst) && /cfgset T3CODE_CHANNEL stable/.test(inst));
ok("install: writes the t3code-serve unit", /\/etc\/systemd\/system\/t3code-serve\.service/.test(inst) && /EnvironmentFile=\/etc\/construct\/config\.env/.test(inst));
// The unit's ExecStart placeholders must reach the FILE as literal ${...} for
// systemd to expand — i.e. escaped (\$) inside the unquoted heredoc.
ok("install: unit placeholders escaped for systemd, not the shell", /--host \\\$\{T3CODE_HOST\} --port \\\$\{T3CODE_PORT\}/.test(inst));
ok("install: enables + restarts the service and verifies it's active", /systemctl enable t3code-serve/.test(inst) && /systemctl restart t3code-serve/.test(inst) && /is-active --quiet t3code-serve/.test(inst));

// ── buildInstallScript (nightly channel) ─────────────────────────────────────
const instN = t3.buildInstallScript("nightly");
ok("install(nightly): npm installs t3@nightly", /npm install -g t3@nightly --allow-scripts=/.test(instN));
ok("install(nightly): persists T3CODE_CHANNEL nightly", /cfgset T3CODE_CHANNEL nightly/.test(instN));
ok("install(nightly): 0-arg call defaults to stable (backward compat)", /t3@latest/.test(t3.buildInstallScript()) && !/t3@nightly/.test(t3.buildInstallScript()));

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

// ── planT3LiveAction ─────────────────────────────────────────────────────────
const plan = t3.planT3LiveAction;
ok("plan: enable when wantT3=true, hadT3=false",
  (() => { const r = plan(true, false, "stable", "stable"); return r && r.action === "enable" && r.channel === "stable"; })());
ok("plan: enable passes the chosen channel",
  (() => { const r = plan(true, false, "nightly", "stable"); return r && r.action === "enable" && r.channel === "nightly"; })());
ok("plan: disable when wantT3=false, hadT3=true",
  (() => { const r = plan(false, true, "stable", "stable"); return r && r.action === "disable"; })());
ok("plan: setChannel when already enabled and channel differs",
  (() => { const r = plan(true, true, "nightly", "stable"); return r && r.action === "setChannel" && r.channel === "nightly"; })());
ok("plan: null when both enabled and channel unchanged",
  plan(true, true, "stable", "stable") === null);
ok("plan: null when both disabled",
  plan(false, false, "stable", "stable") === null);
ok("plan: stored nightly + omitted channel (no switch) — merged preserves nightly",
  plan(true, true, "nightly", "nightly") === null);
ok("plan: enable takes priority over channel mismatch (one op, not two)",
  (() => { const r = plan(true, false, "nightly", "stable"); return r && r.action === "enable" && r.channel === "nightly"; })());

// ── planT3ParkLiveAction (usage-limit auto-resume) ───────────────────────────
const ppark = t3.planT3ParkLiveAction;
ok("park plan: fresh enable + preference on -> apply after install",
  ppark({ action: "enable", channel: "stable" }, true, true, false) === "apply");
ok("park plan: fresh enable re-applies even when preference unchanged (bundle is stock)",
  ppark({ action: "enable", channel: "stable" }, true, true, true) === "apply");
ok("park plan: enable + preference off -> nothing (stock bundle needs no revert)",
  ppark({ action: "enable", channel: "stable" }, true, false, true) === null);
ok("park plan: channel switch re-applies when preference on",
  ppark({ action: "setChannel", channel: "nightly" }, true, true, true) === "apply");
ok("park plan: T3 disable -> nothing", ppark({ action: "disable" }, false, true, true) === null);
ok("park plan: steady T3, preference flips on -> apply", ppark(null, true, true, false) === "apply");
ok("park plan: steady T3, preference flips off -> revert", ppark(null, true, false, true) === "revert");
ok("park plan: steady T3, preference unchanged -> nothing", ppark(null, true, true, true) === null);
ok("park plan: T3 off entirely -> nothing", ppark(null, false, true, false) === null);

// ── buildLimitResume{Enable,Disable}Script ───────────────────────────────────
const parkOn = t3.buildLimitResumeEnableScript();
const parkOff = t3.buildLimitResumeDisableScript();
ok("extra features enable: uploads and applies both patchers",
  /base64 -d > \/tmp\/construct-t3park-patch\.mjs/.test(parkOn) && /t3park-patch\.mjs apply/.test(parkOn) &&
  /base64 -d > \/tmp\/construct-t3-opencode-monitor-patch\.mjs/.test(parkOn) && /opencode-monitor-patch\.mjs apply/.test(parkOn));
ok("park enable: propagates the patcher's exit code (anchor mismatch must surface)",
  /apply \|\| exit \$\?/.test(parkOn));
ok("park enable: persists the opt-in and queues a non-blocking restart",
  /cfgset T3CODE_LIMIT_RESUME true/.test(parkOn) && /systemctl --no-block restart t3code-serve/.test(parkOn));
ok("park enable: mints the resume API token before queuing the restart",
  /mint-token/.test(parkOn) && parkOn.indexOf("mint-token") < parkOn.indexOf("systemctl --no-block restart"));
ok("park disable: its restart is non-blocking too",
  /systemctl --no-block try-restart t3code-serve/.test(parkOff));
ok("park enable: embedded patcher is pure base64 (survives single-quoting)",
  (() => { const m = parkOn.match(/printf '%s' '([^']*)'/); return m && /^[A-Za-z0-9+/=]+$/.test(m[1]); })());
ok("extra features disable: reverts both, clears the opt-in, best-effort exit 0",
  /t3park-patch\.mjs revert/.test(parkOff) && /opencode-monitor-patch\.mjs revert/.test(parkOff) &&
  /cfgset T3CODE_LIMIT_RESUME false/.test(parkOff) && /exit 0/.test(parkOff));

// ── the generated bash parses ────────────────────────────────────────────────
// (bash -n via child_process; skipped quietly when bash isn't available, e.g. a
// bare Windows host running the suite.)
try {
  const cp = require("child_process");
  for (const [name, script] of [["install", inst], ["install(nightly)", instN], ["disable", dis], ["pairing", pair], ["park-enable", parkOn], ["park-disable", parkOff]]) {
    const r = cp.spawnSync("bash", ["-n"], { input: script, encoding: "utf8" });
    if (r.error) { console.log("  SKIP  bash -n (" + name + ") — bash unavailable"); continue; }
    ok("bash -n: " + name + " script parses", r.status === 0, (r.stderr || "").trim());
  }
} catch (_) { /* best-effort */ }

// ── _serial queue: non-overlap + last-action-wins + rejection path ──────────
// Deferred-resolution mocks prove the queue serializes operations: each
// runRemoteScript increments `active` on entry and decrements on resolve, so
// maxActive > 1 would mean two ops overlapped. Explicit resolve callbacks
// (not instant resolution) let us verify that op N+1 cannot START until op N
// resolves. Uses setChannelOnVm (single runRemoteScript call, no pairing
// side-effect) for clean ordering assertions.
(async () => {
  let active = 0, maxActive = 0, vmChannel = "stable";
  const log = [];
  const resolvers = [];

  function mockVscode() {
    return {
      window: {
        withProgress: (_, fn) => fn(),
        showInformationMessage: () => {},
        showWarningMessage: () => {},
        showErrorMessage: () => {},
      },
      ProgressLocation: { Notification: 1 },
      env: { openExternal: async () => {} },
      Uri: { parse: (u) => u },
    };
  }

  function deferredSsh(label, channel) {
    return {
      isReachable: async () => true,
      runRemoteScript: () => new Promise((resolve) => {
        active++;
        if (active > maxActive) maxActive = active;
        log.push(`start:${label}`);
        resolvers.push(() => {
          vmChannel = channel;
          log.push(`end:${label}`);
          active--;
          resolve({ code: 0, stdout: "", stderr: "" });
        });
      }),
    };
  }

  // Queue three rapid channel transitions: stable → nightly → stable.
  t3._resetQueue();
  active = 0; maxActive = 0; vmChannel = "stable";
  log.length = 0; resolvers.length = 0;
  const vs = mockVscode();
  const p1 = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("op1-nightly", "nightly") });
  const p2 = t3.setChannelOnVm("stable",  { _vscode: vs, _ssh: deferredSsh("op2-stable", "stable") });
  const p3 = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("op3-nightly", "nightly") });

  // Yield so the first op's isReachable resolves and runRemoteScript is called.
  await new Promise((r) => setTimeout(r, 20));
  ok("queue: only op1 started (op2/op3 waiting)", resolvers.length === 1 && active === 1);

  // Resolve op1 — op2 should start next.
  resolvers[0]();
  await new Promise((r) => setTimeout(r, 20));
  ok("queue: op1 resolved, op2 started", resolvers.length === 2 && active === 1);
  ok("queue: op2 did not start before op1 resolved (non-overlap)", maxActive === 1);

  // Resolve op2 — op3 should start.
  resolvers[1]();
  await new Promise((r) => setTimeout(r, 20));
  ok("queue: op2 resolved, op3 started", resolvers.length === 3 && active === 1);

  // Resolve op3 — all done.
  resolvers[2]();
  await Promise.all([p1, p2, p3]);
  ok("queue: maxActive never exceeded 1 (strict serialization)", maxActive === 1);
  ok("queue: execution order is op1→op2→op3",
    log.join(",") === "start:op1-nightly,end:op1-nightly,start:op2-stable,end:op2-stable,start:op3-nightly,end:op3-nightly");
  ok("queue: final VM state matches last action (nightly)", vmChannel === "nightly");

  // Rejection path: a failing op must not jam the queue.
  t3._resetQueue();
  active = 0; maxActive = 0; vmChannel = "stable";
  log.length = 0; resolvers.length = 0;
  const failSsh = {
    isReachable: async () => true,
    runRemoteScript: () => new Promise((_, reject) => {
      active++;
      if (active > maxActive) maxActive = active;
      log.push("start:fail");
      resolvers.push(() => { log.push("end:fail"); active--; reject(new Error("ssh broke")); });
    }),
  };
  const pFail = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: failSsh });
  const pAfter = t3.setChannelOnVm("stable", { _vscode: vs, _ssh: deferredSsh("after-fail", "stable") });

  await new Promise((r) => setTimeout(r, 20));
  resolvers[0](); // resolve the failing op
  await new Promise((r) => setTimeout(r, 20));
  ok("queue rejection: next op started after failure", resolvers.length === 2 && active === 1);

  resolvers[1](); // resolve the second op
  let failCaught = false;
  try { await pFail; } catch (_) { failCaught = true; }
  let afterOk = false;
  try { await pAfter; afterOk = true; } catch (_) {}
  ok("queue rejection: failing op threw", failCaught);
  ok("queue rejection: subsequent op completed (queue not stuck)", afterOk && vmChannel === "stable");
  ok("queue rejection: maxActive still 1 (serialized through failure)", maxActive === 1);

  console.log(`\n  t3code unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
