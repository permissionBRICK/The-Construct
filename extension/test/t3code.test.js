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
const transformManifest = fs.readFileSync(path.join(repoRoot, "patches", "t3code-release", "source-transforms.json"), "utf8");
const overlayRoot = path.join(repoRoot, "patches", "t3code-release", "overlays");
const overlayText = fs.readdirSync(overlayRoot, { recursive: true, withFileTypes: true })
  .filter((entry) => entry.isFile()).map((entry) => fs.readFileSync(path.join(entry.parentPath, entry.name), "utf8")).join("\n");
const sourceRecipe = transformManifest + "\n" + overlayText;
const updateT3 = fs.readFileSync(path.join(repoRoot, "Update-T3Code.ps1"), "utf8");
const provisionT3 = fs.readFileSync(path.join(repoRoot, "Provision-AgentVM.ps1"), "utf8");
const installAiTools = fs.readFileSync(path.join(repoRoot, "bin", "install-ai-tools.sh"), "utf8");
ok("source build: resolves the selected npm channel to an exact Git tag",
  /npm view "t3@\$\{NPM_TAG\}" version/.test(sourceBuild) && /TAG="v\$\{VERSION\}"/.test(sourceBuild) && /git clone --depth 1 --branch "\$\{TAG\}"/.test(sourceBuild));
ok("source build: falls back to codeload and preserves the upstream commit",
  /GIT_TERMINAL_PROMPT=0 git clone/.test(sourceBuild) && /codeload\.github\.com\/pingdotgg\/t3code\/tar\.gz\/refs\/tags\/\$\{TAG\}/.test(sourceBuild) &&
  /api\.github\.com\/repos\/pingdotgg\/t3code\/commits\/\$\{TAG\}/.test(sourceBuild) && /\.construct-upstream-commit/.test(sourceBuild));
ok("source build: prunes superseded dependency trees before its free-space gate",
  /for stale_dir in "\$\{CACHE_ROOT\}"\/\*\//.test(sourceBuild) &&
  /t3_build_prune_candidates "\$\{stale_dir\}"/.test(sourceBuild) &&
  sourceBuild.indexOf("for stale_dir") < sourceBuild.indexOf('available_kb="$(df'));
ok("source build: cache is keyed by both T3 and installed Construct versions",
  /CONSTRUCT_VERSION/.test(sourceBuild) && /cached_construct/.test(sourceBuild) &&
  /T3CODE_BUILD_KEY/.test(sourceBuild) && /constructVersion, buildHash/.test(sourceBuild) &&
  /CONSTRUCT_VERSION='\$constructVersion'/.test(provisionT3));
ok("source build: an unchanged active build skips T3 reinstall and restart",
  /t3code-installed-build/.test(installAiTools) &&
  /T3 Code build is unchanged and already running; skipping its reinstall\/restart/.test(installAiTools));
ok("source build: one patched server bundle feeds the VM and Desktop package",
  /pnpm run build:desktop/.test(sourceBuild) && /node "\$\{T3PARK_PATCHER\}" apply --bundle/.test(sourceBuild) &&
  /build-desktop-artifact\.ts/.test(sourceBuild) && /ln -sfn "\$\{SOURCE_DIR\}\/apps\/server\/dist\/bin\.mjs" \/usr\/local\/bin\/t3/.test(sourceBuild));
ok("source build: Windows compiler/NSIS dependencies stay in the VM",
  /mingw-w64/.test(sourceBuild) && /wine32:i386/.test(sourceBuild) && /x86_64-pc-windows-gnu/.test(sourceBuild) && /--target nsis --arch x64/.test(sourceBuild));
ok("source transforms: voice RPC, live cursor-safe insertion, mic UI, and Construct updater are present",
  /voiceInput\.start/.test(sourceRecipe) && /active\.lastSetInput/.test(sourceRecipe) && /MicIcon/.test(sourceRecipe) &&
  /Ctrl\+T/.test(sourceRecipe) && !/Ctrl\+D/.test(sourceRecipe) && /Update-T3Code\.ps1/.test(sourceRecipe));
ok("source transforms: stream PCM amplitude into a live mic-button level effect",
  /readInt16LE/.test(sourceRecipe) && /Schema\.Literal\(\\?"level\\?"\)/.test(sourceRecipe) &&
  /data-voice-level/.test(sourceRecipe) && /boxShadow/.test(sourceRecipe));
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
// The HTTPS front end is the one non-embedded step (certificates + nginx are too
// much to inline). It must run BEFORE the restart so `t3 serve` starts with
// T3CODE_PUBLIC_BASE_URL in its environment, and an old VM copy without the
// script must say so instead of failing the toggle.
ok("install: sets up HTTPS via the repo script before restarting the service",
  /bash \/opt\/construct\/repo\/bin\/setup-t3-https\.sh/.test(inst) &&
  inst.indexOf("setup-t3-https.sh") < inst.indexOf("systemctl restart t3code-serve"));
ok("install: a Construct copy without the HTTPS script degrades to a note",
  /-f \/opt\/construct\/repo\/bin\/setup-t3-https\.sh/.test(inst) &&
  /predates T3 HTTPS support/.test(inst) && /stays on plain http/.test(inst));

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
// Tearing the proxy down must NOT rewrite the T3CODE_HTTPS preference (or drop
// the CA), so re-enabling T3 Code brings HTTPS back without a new trust import.
ok("disable: tears the HTTPS proxy down with --teardown (preference + CA kept)",
  /setup-t3-https\.sh --teardown/.test(dis) && !/cfgset T3CODE_HTTPS/.test(dis));

// ── buildPairingScript ────────────────────────────────────────────────────────
const pair = t3.buildPairingScript();
ok("pairing: mints a one-time token as JSON against the mshome.net base URL",
  /t3 auth pairing create --json/.test(pair) && /mshome\.net/.test(pair) && /--base-url/.test(pair));
ok("pairing: silences CLI logs so stdout stays parseable", /--log-level none/.test(pair));

// FULL-SCRIPT PIN. This string is the remote command an existing install sends
// over SSH, so every byte of it matters and the WHOLE script is pinned — prelude
// included. It used to be pinned to the pre-instances command (commit f84f554) as
// a zero-change bar for that refactor. HTTPS support DELIBERATELY changed it: the
// scheme now comes from the VM, because a browser only exposes getUserMedia() on a
// secure origin and the pairing token is bound to the origin that minted it. The
// pin therefore moved to the new value rather than being dropped — an unintended
// edit still fails here. Note what did NOT change: on a VM with no TLS proxy every
// new key reads empty and t3base() produces exactly the old http URL, and the
// default instance still reads no CONSTRUCT_EXTERNAL_HOST.
const PAIRING_DEFAULT_SCRIPT = `set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
# Undo config-set.sh's rendering: it writes values made only of its safe charset
# bare, and single-quotes anything else (embedded apostrophes as '\\''). An IPv6
# public base URL — https://[2001:db8::1]:5178 — has brackets, so it IS stored
# quoted, and reading the raw line would carry the apostrophes into the value.
cfgget() {
  _v="$(sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1)"
  case "$_v" in
    "'"*"'") _v="\${_v#\\'}"; _v="\${_v%\\'}"; _v="\${_v//\\'\\\\\\'\\'/\\'}" ;;
  esac
  printf '%s' "$_v"
}
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="\${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="\${T3CODE_PORT:-5177}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="\${WORKSPACE_ROOT:-/root/repos}"
T3CODE_PUBLIC_BASE_URL="$(cfgget T3CODE_PUBLIC_BASE_URL)"
# The origin the pairing link is minted against, given the client-reachable host in $1.
t3base() {
  if [ -n "$T3CODE_PUBLIC_BASE_URL" ]; then printf '%s' "$T3CODE_PUBLIC_BASE_URL"; return 0; fi
  printf 'http://%s:%s' "$1" "$T3CODE_PORT"
}

command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
base="$(t3base "$(hostname).mshome.net")"
t3 auth pairing create --json --ttl 10m --label "construct-control-panel" --base-url "$base" --log-level none
`;
ok("pairing(default): the WHOLE script matches the pin",
  pair === PAIRING_DEFAULT_SCRIPT,
  JSON.stringify(pair));
ok("pairing(default): ...and is still the expected 1559 bytes",
  Buffer.byteLength(pair, "utf8") === 1559, String(Buffer.byteLength(pair, "utf8")));
ok("pairing(default): reads NO CONSTRUCT_EXTERNAL_HOST", !/CONSTRUCT_EXTERNAL_HOST/.test(pair));

// ── HTTPS-aware pairing URL ──────────────────────────────────────────────────
// The scheme decision lives in the generated bash, so it is EXECUTED here (with
// stub config.env files) rather than pattern-matched: the whole point is which
// --base-url the CLI is finally handed. `cfgPairs` are written through the REAL
// bin/config-set.sh, so the fixtures carry its exact rendering (an IPv6 origin
// comes out single-quoted) and the prelude's unquoting is exercised for real.
(() => {
  const cp = require("child_process");
  const os = require("os");
  const fsx = require("fs");
  const configSet = path.join(repoRoot, "bin", "config-set.sh");
  const probeBase = (cfgPairs, instance) => {
    const dir = fsx.mkdtempSync(path.join(os.tmpdir(), "t3pair-"));
    const cfg = path.join(dir, "config.env");
    fsx.writeFileSync(cfg, "");
    for (const [k, v] of cfgPairs) {
      cp.spawnSync("bash", [configSet, cfg, k, v], { encoding: "utf8" });
    }
    const bin = path.join(dir, "bin");
    fsx.mkdirSync(bin);
    // Stubs: `t3` echoes the base URL it was given, `hostname` is fixed.
    fsx.writeFileSync(path.join(bin, "t3"),
      '#!/bin/sh\nwhile [ $# -gt 0 ]; do if [ "$1" = "--base-url" ]; then echo "$2"; fi; shift; done\n', { mode: 0o755 });
    fsx.writeFileSync(path.join(bin, "hostname"), "#!/bin/sh\necho testvm\n", { mode: 0o755 });
    // Point the script at the fixture config.env without touching /etc.
    const script = t3.buildPairingScript(instance).replace(
      "CONFIG_FILE=/etc/construct/config.env", "CONFIG_FILE=" + cfg);
    const r = cp.spawnSync("bash", ["-c", script], {
      encoding: "utf8", env: { ...process.env, PATH: bin + ":" + process.env.PATH },
    });
    fsx.rmSync(dir, { recursive: true, force: true });
    return { out: (r.stdout || "").trim(), code: r.status, err: (r.stderr || "").trim() };
  };
  if (cp.spawnSync("bash", ["-c", "true"]).error) {
    console.log("  SKIP  pairing base-url execution — bash unavailable");
    return;
  }
  const noHttps = probeBase([]);
  ok("pairing: no HTTPS keys -> today's plain http URL", noHttps.out === "http://testvm.mshome.net:5177", noHttps.out + " " + noHttps.err);
  const publicUrl = probeBase([["T3CODE_HTTPS", "true"], ["T3CODE_PUBLIC_BASE_URL", "https://vm.example.com:5178"]]);
  ok("pairing: the recorded public origin is what the link is minted against",
    publicUrl.out === "https://vm.example.com:5178", publicUrl.out);
  const publicPort = probeBase([["T3CODE_PUBLIC_BASE_URL", "https://testvm.mshome.net:6443"]]);
  ok("pairing: honours whatever port that origin carries",
    publicPort.out === "https://testvm.mshome.net:6443", publicPort.out);
  // THE FAILED-SETUP CASE. setup-t3-https.sh keeps T3CODE_HTTPS=true as the retry
  // preference but clears the public origin whenever the proxy did not come up
  // (offline apt, nginx refused to start). Minting an https link then would point
  // the browser at a port nothing listens on.
  const httpsPrefOnly = probeBase([["T3CODE_HTTPS", "true"], ["T3CODE_HTTPS_PORT", "5178"]]);
  ok("pairing: the T3CODE_HTTPS preference ALONE does not produce an https link",
    httpsPrefOnly.out === "http://testvm.mshome.net:5177", httpsPrefOnly.out);
  const httpsCleared = probeBase([["T3CODE_HTTPS", "true"], ["T3CODE_PUBLIC_BASE_URL", ""]]);
  ok("pairing: an EMPTIED public origin falls back to http (failed setup)",
    httpsCleared.out === "http://testvm.mshome.net:5177", httpsCleared.out);
  const httpsOff = probeBase([["T3CODE_HTTPS", "false"], ["T3CODE_HTTPS_PORT", "5178"]]);
  ok("pairing: T3CODE_HTTPS=false stays on http", httpsOff.out === "http://testvm.mshome.net:5177", httpsOff.out);
  // config-set.sh single-quotes an IPv6 origin (it has brackets); the prelude must
  // decode that or the apostrophes would travel into --base-url.
  const v6 = probeBase([["T3CODE_PUBLIC_BASE_URL", "https://[2001:db8::1]:5178"]]);
  ok("pairing: a config-set.sh-quoted IPv6 origin is decoded, not passed with quotes",
    v6.out === "https://[2001:db8::1]:5178", v6.out);
  const quoted = probeBase([["T3CODE_PUBLIC_BASE_URL", "https://[2001:db8::1]:5178"]]);
  ok("pairing: no apostrophes leak into the minted base URL", !/'/.test(quoted.out), quoted.out);
  // The instance variant resolves the HOST differently but must make the SAME
  // scheme decision.
  const remoteInst = require("../src/instances")
    .deriveDefaults("work-vm", { sshHost: "buildbox.local", sshPort: 2201 });
  const instHttps = probeBase([["CONSTRUCT_EXTERNAL_HOST", "buildbox.local"],
    ["T3CODE_PUBLIC_BASE_URL", "https://buildbox.local:5178"]], remoteInst);
  ok("pairing(instance): the recorded public origin wins there too",
    instHttps.out === "https://buildbox.local:5178", instHttps.out);
  const instHttp = probeBase([["CONSTRUCT_EXTERNAL_HOST", "buildbox.local"]], remoteInst);
  ok("pairing(instance): plain http when no public origin is recorded",
    instHttp.out === "http://buildbox.local:5177", instHttp.out);
  const instHttpsPrefOnly = probeBase([["CONSTRUCT_EXTERNAL_HOST", "buildbox.local"],
    ["T3CODE_HTTPS", "true"]], remoteInst);
  ok("pairing(instance): the preference alone does not produce https either",
    instHttpsPrefOnly.out === "http://buildbox.local:5177", instHttpsPrefOnly.out);
})();
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
  /t3 auth pairing create --json --ttl 10m --label "construct-work-vm"/.test(pairRemote));
// B14: the label NAMES the instance for a non-default VM, so a T3 session list that
// holds several linked Construct VMs can say which machine a session belongs to. The
// default instance keeps "construct-control-panel" (asserted with the whole pinned
// script above).
ok("pairing(instance): the label names the instance, not the panel",
  !/construct-control-panel/.test(pairRemote));
ok("pairing(default): the label is unchanged",
  /--label "construct-control-panel"/.test(pair));

// ── extractPairUrl ────────────────────────────────────────────────────────────
const clean = JSON.stringify({ id: "x", credential: "ABC", pairUrl: "http://agent-vm.mshome.net:5177/pair#token=ABC" });
ok("extractPairUrl: clean JSON", t3.extractPairUrl(clean) === "http://agent-vm.mshome.net:5177/pair#token=ABC");
const dirty = "[12:00:00.000] INFO (#1): noise\n" + clean;
ok("extractPairUrl: tolerates stray log lines", t3.extractPairUrl(dirty) === "http://agent-vm.mshome.net:5177/pair#token=ABC");
ok("extractPairUrl: empty on garbage", t3.extractPairUrl("no json here") === "" && t3.extractPairUrl("") === "" && t3.extractPairUrl(null) === "");

// ── baseUrl fallback ──────────────────────────────────────────────────────────
ok("baseUrl: defaults to the VM DNS + default port", t3.baseUrl() === "http://agent-vm.mshome.net:5177");
ok("baseUrl: honors a cfg vmHost override", t3.baseUrl({ vmHost: "other.host" }) === "http://other.host:5177");
// The probed origin (probe.js toState -> the t3code agent's url) knows whether the
// TLS proxy is on, so it wins -- but only after validation: it comes from the VM's
// config.env and is handed to openExternal.
ok("baseUrl: uses the probed https origin when there is one",
  t3.baseUrl({ vmHost: "other.host" }, "https://vm.example.com:5178") === "https://vm.example.com:5178");
ok("baseUrl: rejects a non-origin probed value and falls back",
  t3.baseUrl({ vmHost: "other.host" }, "javascript:alert(1)") === "http://other.host:5177" &&
  t3.baseUrl({ vmHost: "other.host" }, "https://vm.example.com/pair#tok") === "http://other.host:5177" &&
  t3.baseUrl({ vmHost: "other.host" }, "") === "http://other.host:5177");

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

  // ── B12: the queue is PER INSTANCE ──────────────────────────────────────────
  // Held globally it serialized across VMs that share nothing: enabling T3 on A (a
  // multi-minute npm install) blocked the enable on B behind it. Two DIFFERENT instances
  // must run concurrently; two ops on the SAME instance must still not overlap.
  t3._resetQueue();
  active = 0; maxActive = 0; log.length = 0; resolvers.length = 0;
  const instA = instances.deriveDefaults("alpha", { backend: "hyperv-local" });
  const instB = instances.deriveDefaults("beta", { backend: "hyperv-local" });
  const pa = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("A1", "nightly"), instance: instA });
  const pb = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("B1", "nightly"), instance: instB });
  await new Promise((r) => setTimeout(r, 20));
  ok("per-instance queue: A and B start CONCURRENTLY (B is not blocked behind A)",
    resolvers.length === 2 && active === 2 && maxActive === 2);
  const a2started = () => log.includes("start:A2");
  const pa2 = t3.setChannelOnVm("stable", { _vscode: vs, _ssh: deferredSsh("A2", "stable"), instance: instA });
  await new Promise((r) => setTimeout(r, 20));
  ok("per-instance queue: a SECOND op on A waits behind A's first", !a2started() && resolvers.length === 2);
  resolvers[0]();  // A1
  await new Promise((r) => setTimeout(r, 20));
  ok("per-instance queue: ...and starts as soon as A's first resolves", a2started());
  resolvers[1]();  // B1
  resolvers[2]();  // A2
  await Promise.all([pa, pb, pa2]);
  ok("per-instance queue: every op completed", log.filter((l) => l.startsWith("end:")).length === 3);

  // The identity, not the name: a registry entry rewritten under the SAME name reaches a
  // different machine, so its transitions must not queue behind the old endpoint's.
  t3._resetQueue();
  active = 0; maxActive = 0; log.length = 0; resolvers.length = 0;
  const before = instances.deriveDefaults("moved", { backend: "hyperv-remote", sshHost: "old.example", sshPort: 2201, vmName: "moved" });
  const after = instances.deriveDefaults("moved", { backend: "hyperv-remote", sshHost: "new.example", sshPort: 2202, vmName: "moved" });
  const pOld = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("old", "nightly"), instance: before });
  const pNew = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("new", "nightly"), instance: after });
  await new Promise((r) => setTimeout(r, 20));
  ok("per-instance queue: a retargeted entry gets its OWN queue (keyed by identity)",
    resolvers.length === 2 && active === 2);
  resolvers[0](); resolvers[1]();
  await Promise.all([pOld, pNew]);

  // Zero-change: no instance at all keeps a single queue, exactly as before.
  t3._resetQueue();
  active = 0; maxActive = 0; log.length = 0; resolvers.length = 0;
  const pn1 = t3.setChannelOnVm("nightly", { _vscode: vs, _ssh: deferredSsh("n1", "nightly") });
  const pn2 = t3.setChannelOnVm("stable", { _vscode: vs, _ssh: deferredSsh("n2", "stable") });
  await new Promise((r) => setTimeout(r, 20));
  ok("per-instance queue: with no instance given, ops still serialize (default path)",
    resolvers.length === 1 && maxActive === 1);
  resolvers[0]();
  await new Promise((r) => setTimeout(r, 20));
  resolvers[1]();
  await Promise.all([pn1, pn2]);
  ok("per-instance queue: ...and the default path never overlapped", maxActive === 1);

  console.log(`\n  t3code unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
