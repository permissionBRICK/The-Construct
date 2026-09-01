"use strict";
// Plain-node unit tests for the T3 Code live-control script builders + the
// pairing-URL extractor. No deps. Run: node t3code.test.js
const fs = require("fs");
const path = require("path");
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

// ── shared patched-source server/Desktop build ─────────────────────────────
const repoRoot = path.resolve(__dirname, "..", "..");
const sourceBuild = fs.readFileSync(path.join(repoRoot, "bin", "build-t3code.sh"), "utf8");
const sourcePatch = fs.readFileSync(path.join(repoRoot, "patches", "t3code-construct.patch"), "utf8");
const updateT3 = fs.readFileSync(path.join(repoRoot, "Update-T3Code.ps1"), "utf8");
const provisionT3 = fs.readFileSync(path.join(repoRoot, "Provision-AgentVM.ps1"), "utf8");
const installAiTools = fs.readFileSync(path.join(repoRoot, "bin", "install-ai-tools.sh"), "utf8");
ok("source build: resolves the selected npm channel to an exact Git tag",
  /npm view "t3@\$\{NPM_TAG\}" version/.test(sourceBuild) && /TAG="v\$\{VERSION\}"/.test(sourceBuild) && /git clone --depth 1 --branch "\$\{TAG\}"/.test(sourceBuild));
ok("source build: falls back to codeload and preserves the upstream commit",
  /GIT_TERMINAL_PROMPT=0 git clone/.test(sourceBuild) && /codeload\.github\.com\/pingdotgg\/t3code\/tar\.gz\/refs\/tags\/\$\{TAG\}/.test(sourceBuild) &&
  /api\.github\.com\/repos\/pingdotgg\/t3code\/commits\/\$\{TAG\}/.test(sourceBuild) && /\.construct-upstream-commit/.test(sourceBuild));
ok("source build: prunes superseded dependency trees before its free-space gate",
  /for stale_modules in "\$\{CACHE_ROOT\}"\/\*\/node_modules/.test(sourceBuild) &&
  /dirname "\$\{stale_modules\}"\)" == "\$\{SOURCE_DIR\}"/.test(sourceBuild) &&
  sourceBuild.indexOf("for stale_modules") < sourceBuild.indexOf('available_kb="$(df'));
ok("source build: cache is keyed by both T3 and installed Construct versions",
  /CONSTRUCT_VERSION/.test(sourceBuild) && /cached_construct/.test(sourceBuild) &&
  /T3CODE_BUILD_KEY/.test(sourceBuild) && /constructVersion, buildHash/.test(sourceBuild) &&
  /CONSTRUCT_VERSION='\$constructVersion'/.test(provisionT3));
ok("source build: an unchanged active build skips T3 reinstall and restart",
  /t3code-installed-build/.test(installAiTools) &&
  /T3 Code build is unchanged and already running; skipping its reinstall\/restart/.test(installAiTools));
ok("source build: one patched server bundle feeds the VM and Desktop package",
  /pnpm run build:desktop/.test(sourceBuild) && /construct-t3park-patch\.mjs" apply --bundle/.test(sourceBuild) &&
  /build-desktop-artifact\.ts/.test(sourceBuild) && /ln -sfn "\$\{SOURCE_DIR\}\/apps\/server\/dist\/bin\.mjs" \/usr\/local\/bin\/t3/.test(sourceBuild));
ok("source build: Windows compiler/NSIS dependencies stay in the VM",
  /mingw-w64/.test(sourceBuild) && /wine32:i386/.test(sourceBuild) && /x86_64-pc-windows-gnu/.test(sourceBuild) && /--target nsis --arch x64/.test(sourceBuild));
ok("source patch: voice RPC, live cursor-safe insertion, mic UI, and Construct updater are present",
  /voiceInput\.start/.test(sourcePatch) && /active\.lastSetInput/.test(sourcePatch) && /MicIcon/.test(sourcePatch) &&
  /Ctrl\+T/.test(sourcePatch) && !/Ctrl\+D/.test(sourcePatch) && /Update-T3Code\.ps1/.test(sourcePatch));
ok("source patch: streams PCM amplitude into a live mic-button level effect",
  /readInt16LE/.test(sourcePatch) && /Schema\.Literal\("level"\)/.test(sourcePatch) &&
  /data-voice-level/.test(sourcePatch) && /boxShadow/.test(sourcePatch));
ok("desktop update handoff: reprovisions with the saved T3 source-build setting",
  /Provision-AgentVM\.ps1/.test(updateT3) && /Action\s+= 'provision'/.test(updateT3) && /T3CodeLimitResume/.test(updateT3));
ok("desktop provisioning: installs updates silently, waits, and never prompts or launches the app",
  /ArgumentList @\('--updated', '\/S'\) -Wait -PassThru -WindowStyle Hidden/.test(provisionT3) &&
  /installed\.json/.test(provisionT3) && !/System\.Windows\.Forms\.MessageBox/.test(provisionT3) &&
  !/--force-run/.test(provisionT3) && !/T3 Code Desktop installer launched/.test(provisionT3));

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
ok("disable: clears voice capability and stale Desktop handoff state",
  /cfgset CONSTRUCT_T3_VOICE_INPUT false/.test(dis) && /t3code-desktop-status/.test(dis) &&
  /t3code-installed-build/.test(dis));
ok("disable: stops + disables the service, best-effort", /systemctl disable --now t3code-serve/.test(dis) && /exit 0/.test(dis));

// ── buildPairingScript ────────────────────────────────────────────────────────
const pair = t3.buildPairingScript();
ok("pairing: mints a one-time token as JSON against the mshome.net base URL",
  /t3 auth pairing create --json/.test(pair) && /mshome\.net/.test(pair) && /--base-url/.test(pair));
ok("pairing: silences CLI logs so stdout stays parseable", /--log-level none/.test(pair));

// ZERO-CHANGE PIN. This string is the remote command an existing install sends over
// SSH; for the default instance it must be BYTE-IDENTICAL to what shipped before
// instances existed (commit f84f554), not merely equivalent. The WHOLE script is
// pinned — prelude included — because every byte of it crosses the SSH boundary: a
// change to PRELUDE alone would still change the command an unchanged install runs.
// If a change to the pairing script is intended it belongs in the NON-default branch,
// and this literal stays put.
const PAIRING_DEFAULT_SCRIPT = `set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
cfgget() { sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1; }
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="\${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="\${T3CODE_PORT:-5177}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="\${WORKSPACE_ROOT:-/root/repos}"

command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
base="http://$(hostname).mshome.net:\${T3CODE_PORT}"
t3 auth pairing create --json --ttl 10m --label "construct-control-panel" --base-url "$base" --log-level none
`;
ok("pairing(default): the WHOLE script is byte-identical to the pre-instances one",
  pair === PAIRING_DEFAULT_SCRIPT,
  JSON.stringify(pair));
ok("pairing(default): ...and is still the expected 825 bytes",
  Buffer.byteLength(pair, "utf8") === 825, String(Buffer.byteLength(pair, "utf8")));
ok("pairing(default): reads NO CONSTRUCT_EXTERNAL_HOST", !/CONSTRUCT_EXTERNAL_HOST/.test(pair));
const instances = require("../src/instances");
ok("pairing(default instance object): same script as passing nothing",
  t3.buildPairingScript(instances.DEFAULT_INSTANCE) === pair);
ok("pairing(spelled-out default registry entry): same script",
  t3.buildPairingScript(instances.deriveDefaults("agent-vm", {})) === pair);
// A NON-default instance may be reachable only under a forwarded/external name, so
// there (and only there) the VM's recorded CONSTRUCT_EXTERNAL_HOST wins.
const pairRemote = t3.buildPairingScript(instances.deriveDefaults("work-vm", { sshHost: "buildbox.local", sshPort: 2201 }));
ok("pairing(instance): prefers the recorded external host", /cfgget CONSTRUCT_EXTERNAL_HOST/.test(pairRemote));
ok("pairing(instance): still falls back to the mshome name", /ext:-\$\(hostname\)\.mshome\.net/.test(pairRemote));
ok("pairing(instance): still mints the same kind of link",
  /t3 auth pairing create --json --ttl 10m --label "construct-control-panel"/.test(pairRemote));

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

// ── the generated bash parses ────────────────────────────────────────────────
// (bash -n via child_process; skipped quietly when bash isn't available, e.g. a
// bare Windows host running the suite.)
try {
  const cp = require("child_process");
  for (const [name, script] of [["install", inst], ["install(nightly)", instN], ["disable", dis], ["pairing", pair]]) {
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
