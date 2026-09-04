"use strict";
// T3 Code (the `t3` npm package — pingdotgg's web GUI for coding agents), driven
// live from the control panel:
//   - enable  : install + start it on the VM NOW (the settings toggle's first
//               enable), then open the web UI in the host browser
//   - disable : stop the service and clear the opt-in flag on the VM
//   - open    : mint a fresh one-time pairing link over SSH and open it locally
//
// The bash is EMBEDDED (not run from /opt/construct/repo) so a live toggle works
// even when the VM's uploaded repo copy predates T3 Code support; the scripts
// mirror what bin/install-ai-tools.sh + provision.sh do, and the next
// reprovision converges on the repo's installer. Pure builders are exported for
// unit tests; the impure runners require `vscode` lazily (same pattern as
// lifecycle.js).

const ssh = require("./ssh");
const instances = require("./instances");
const probe = require("./probe");

function vsc() { return require("vscode"); }

const SERVICE = "t3code-serve";
const DEFAULT_PORT = 5177;
const DEFAULT_HOST_BIND = "0.0.0.0";

// channel -> npm dist-tag, the single mapping on the JS side. The provisioner
// has its own (bash) equivalent in install-ai-tools.sh. "stable" tracks the
// @latest tag; "nightly" tracks @nightly.
function npmTag(channel) { return channel === "nightly" ? "nightly" : "latest"; }

// Serialize the state-changing operations (enable/disable): a user can flip the
// settings toggle off→on→off while the multi-minute npm install is still
// running, and concurrent runs would interleave — the disable could finish
// first and the still-running installer would then re-write T3CODE=true and
// start the service against the final off setting. Queuing each transition
// behind the previous one makes the LAST action win.
let _inflight = Promise.resolve();
function _serial(fn) {
  const run = _inflight.then(fn, fn);
  _inflight = run.then(() => {}, () => {});
  return run;
}
function _resetQueue() { _inflight = Promise.resolve(); }

// Shared bash prelude: read the T3CODE_* bind settings (and workspace root) from
// config.env with the same defaults the provisioner uses, plus an idempotent
// config.env key writer (a trimmed-down config-set.sh — values here are always
// shell-safe literals).
const PRELUDE = `set -uo pipefail
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
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="\${T3CODE_HOST:-${DEFAULT_HOST_BIND}}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="\${T3CODE_PORT:-${DEFAULT_PORT}}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="\${WORKSPACE_ROOT:-/root/repos}"
`;

// Pairing scripts additionally need the HTTPS front end's EFFECTIVE state, which
// is T3CODE_PUBLIC_BASE_URL and nothing else: bin/setup-t3-https.sh writes that
// key only when the TLS proxy actually came up, and clears it on every failure
// path (offline apt, nginx refused to start) while keeping T3CODE_HTTPS as the
// retry preference. Reading the PREFERENCE here would mint pairing links to a
// port nothing listens on after exactly the failure that is meant to degrade to
// plain http. T3's DPoP proofs are bound to the origin the browser dialled, so
// the link must name the same one the server was told to advertise.
const PAIRING_PRELUDE = PRELUDE + `T3CODE_PUBLIC_BASE_URL="$(cfgget T3CODE_PUBLIC_BASE_URL)"
# The origin the pairing link is minted against, given the client-reachable host in $1.
t3base() {
  if [ -n "$T3CODE_PUBLIC_BASE_URL" ]; then printf '%s' "$T3CODE_PUBLIC_BASE_URL"; return 0; fi
  printf 'http://%s:%s' "$1" "$T3CODE_PORT"
}
`;

/** Bash: install/update t3, persist the opt-in + bind keys, deploy + start the
 *  systemd service. Self-contained; exits non-zero on a real failure.
 *  `channel` ("stable"|"nightly"; default "stable") decides the npm dist-tag. */
function buildInstallScript(channel) {
  const tag = npmTag(channel);
  const ch = channel === "nightly" ? "nightly" : "stable";
  return PRELUDE + `
# t3's engines field requires Node ^22.16 || ^23.11 || >=24.10 — npm merely
# WARNS on a mismatch, leaving a broken install whose service restart-loops, so
# check the actual version (not just npm presence) and upgrade the system Node
# via NodeSource when it's too old (the same channel install-sdks.sh uses).
t3_node_ok() {
  v="$(node -v 2>/dev/null | sed 's/^v//')" || return 1
  [ -n "$v" ] || return 1
  major="\${v%%.*}"; rest="\${v#*.}"; minor="\${rest%%.*}"
  [ "$major" -ge 25 ] && return 0
  case "$major" in
    24) [ "\${minor:-0}" -ge 10 ] ;;
    23) [ "\${minor:-0}" -ge 11 ] ;;
    22) [ "\${minor:-0}" -ge 16 ] ;;
    *) return 1 ;;
  esac
}
if ! command -v npm >/dev/null 2>&1 || ! t3_node_ok; then
  echo "== installing Node.js 22.x (t3 requires Node ^22.16 || ^23.11 || >=24.10) =="
  curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs || exit 1
fi
# node-pty (t3's terminal backend) ships prebuilt binaries only for macOS and
# Windows — on Linux its install always falls back to 'node-gyp rebuild', which
# needs make/g++/python3. Fresh VMs have no compiler toolchain, so install it
# before npm runs the build scripts.
if ! command -v make >/dev/null 2>&1 || ! command -v g++ >/dev/null 2>&1 || ! command -v python3 >/dev/null 2>&1; then
  echo "== installing build tools (node-pty compiles from source on Linux) =="
  { apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y build-essential python3; } || exit 1
fi
echo "== installing t3 (${tag}) =="
# node-pty/msgpackr-extract need their build scripts; newer npm gates them behind
# --allow-scripts, older npm ignores the unknown flag and runs them anyway.
npm install -g t3@${tag} --allow-scripts=node-pty,msgpackr-extract || exit 1
command -v t3 >/dev/null 2>&1 || { echo "t3 binary not found after install" >&2; exit 1; }
t3_bin="$(command -v t3)"
if [ "$t3_bin" != /usr/local/bin/t3 ]; then
  resolved="$(readlink -f "$t3_bin" 2>/dev/null || echo "$t3_bin")"
  [ "$resolved" != /usr/local/bin/t3 ] && [ -x "$resolved" ] && ln -sf "$resolved" /usr/local/bin/t3
fi
cfgset T3CODE true
cfgset T3CODE_HOST "$T3CODE_HOST"
cfgset T3CODE_PORT "$T3CODE_PORT"
cfgset T3CODE_CHANNEL ${ch}
mkdir -p "$WORKSPACE_ROOT"
# Same unit the repo ships (systemd/t3code-serve.service); \${...} placeholders
# are expanded by systemd from the EnvironmentFile, not by this shell.
cat > /etc/systemd/system/${SERVICE}.service <<UNIT
[Unit]
Description=T3 Code Server (web GUI for coding agents)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
EnvironmentFile=/etc/construct/config.env
WorkingDirectory=$WORKSPACE_ROOT
ExecStart=/usr/local/bin/t3 serve --host \\\${T3CODE_HOST} --port \\\${T3CODE_PORT}
Restart=always
RestartSec=5
TimeoutStopSec=5s

[Install]
WantedBy=multi-user.target
UNIT
# HTTPS front end (browser mic capture needs a secure origin). This is the ONE
# part that is NOT embedded: certificate issuance + the nginx site are far too
# much to inline, and unlike the npm install they are not needed for the toggle
# to work at all. So call the VM's repo copy when it has the script, and say so
# plainly when it doesn't -- the next reprovision then sets HTTPS up. Runs before
# the restart below so \`t3 serve\` starts with T3CODE_PUBLIC_BASE_URL set.
if [ -f /opt/construct/repo/bin/setup-t3-https.sh ]; then
  bash /opt/construct/repo/bin/setup-t3-https.sh \\
    || echo "warning: T3 HTTPS setup failed; the web GUI stays on plain http" >&2
else
  echo "note: this VM's Construct copy predates T3 HTTPS support; the web GUI stays on plain http until the next reprovision"
fi
systemctl daemon-reload
systemctl enable ${SERVICE}
systemctl restart ${SERVICE}
sleep 2
# Bootstrap one t3 project per git repo in the workspace so the web UI starts
# useful. (t3 serve's --auto-bootstrap-project-from-cwd flag is DEAD in the
# headless serve path — the handler hardcodes it off — hence explicit adds.)
# Idempotent: an already-registered path fails with ProjectAlreadyExistsError,
# which is swallowed; no duplicates are created.
for _repo in "$WORKSPACE_ROOT"/*/; do
  [ -d "\${_repo}.git" ] || continue
  t3 project add "\${_repo%/}" --log-level none >/dev/null 2>&1 || true
done
if systemctl is-active --quiet ${SERVICE}; then
  echo "${SERVICE} running on $T3CODE_HOST:$T3CODE_PORT"
else
  echo "${SERVICE} failed to start" >&2
  journalctl -u ${SERVICE} --no-pager -n 20 >&2 || true
  exit 1
fi
`;
}

/** Bash: stop + disable the service and clear the opt-in flag (the install stays
 *  in place, so re-enabling is instant). Always exits 0 — disable is best-effort. */
function buildDisableScript() {
  return PRELUDE + `
cfgset T3CODE false
cfgset CONSTRUCT_T3_VOICE_INPUT false
rm -f /etc/construct/t3code-desktop-status /etc/construct/t3code-installed-build
# Take the TLS proxy down with the server it fronts (an https listener in front of
# a stopped T3 only serves 502s). --teardown keeps the saved T3CODE_HTTPS
# preference and the local CA, so re-enabling needs no new Windows trust import.
if [ -f /opt/construct/repo/bin/setup-t3-https.sh ]; then
  bash /opt/construct/repo/bin/setup-t3-https.sh --teardown || true
fi
if [ -f /etc/systemd/system/${SERVICE}.service ]; then
  systemctl disable --now ${SERVICE} 2>/dev/null || true
  echo "${SERVICE} stopped and disabled"
else
  echo "${SERVICE} not deployed; nothing to stop"
fi
exit 0
`;
}

/**
 * Bash: mint a one-time pairing token and print the ready-to-open JSON
 * ({... "pairUrl": "https://<dns>:<port>/pair#token=..."}).
 *
 * TWO VARIANTS, and which one runs is decided by the instance:
 *
 *   DEFAULT INSTANCE (and no instance at all) — the host name comes from the VM's
 *   own $(hostname).mshome.net and CONSTRUCT_EXTERNAL_HOST is deliberately NOT read
 *   (config.env is user-editable, so a value left behind by anything else must not
 *   silently redirect the default install's pairing URL).
 *
 *   NON-DEFAULT INSTANCE — prefers CONSTRUCT_EXTERNAL_HOST, the client-reachable name
 *   B2 records in config.env, because a remote/port-forwarded VM is NOT reachable at
 *   its own mshome name; it falls back to $(hostname).mshome.net when the key is absent.
 *
 * THE PAIRING LABEL names the INSTANCE for a non-default VM (`construct-<name>`, plan
 * section 4.12 "Naming"): T3 Code Desktop links several remotes at once, and a session
 * list in which every VM's link is called "construct-control-panel" cannot say which
 * machine a session belongs to. The default instance keeps the historical label, so its
 * script stays byte-identical. (`construct-` is the reserved INSTANCE-name prefix, which
 * is exactly why it is safe here: no instance can be named this.)
 *
 * BOTH variants pick the SCHEME from the VM: Construct now serves T3 over HTTPS
 * (bin/setup-t3-https.sh), and a browser only exposes getUserMedia() — T3's
 * client-side microphone capture — on a secure origin, so the pairing link must
 * name the https origin whenever one is configured. This script therefore is no
 * longer byte-identical to the pre-instances one: that pin was a zero-change bar
 * for a refactor, and this is a deliberate feature change (the WHOLE script is
 * still pinned in extension/test/t3code.test.js, at its new value). On a VM
 * without the TLS proxy every key is absent and the produced URL is exactly
 * today's http one.
 */
function buildPairingScript(instance) {
  if (!instance || instances.isDefaultInstance(instance)) {
    return PAIRING_PRELUDE + `
command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
base="$(t3base "$(hostname).mshome.net")"
t3 auth pairing create --json --ttl 10m --label "construct-control-panel" --base-url "$base" --log-level none
`;
  }
  return PAIRING_PRELUDE + `
command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
# The client-reachable name of THIS VM. B2 records it in config.env as
# CONSTRUCT_EXTERNAL_HOST (a remote/forwarded instance is not reachable at its own
# mshome name); absent, fall back to the local $(hostname).mshome.net.
ext="$(cfgget CONSTRUCT_EXTERNAL_HOST)"
base="$(t3base "\${ext:-$(hostname).mshome.net}")"
t3 auth pairing create --json --ttl 10m --label "construct-${instance.name}" --base-url "$base" --log-level none
`;
}

/** Pull the pairing URL out of the pairing script's stdout. The CLI prints clean
 *  JSON with --json --log-level none, but tolerate stray log lines by scanning
 *  for the pairUrl field anywhere in the output. Returns "" when not found. */
function extractPairUrl(stdout) {
  const s = String(stdout == null ? "" : stdout);
  try {
    const o = JSON.parse(s);
    if (o && typeof o.pairUrl === "string") return o.pairUrl;
  } catch (_) { /* fall through to the scan */ }
  const m = s.match(/"pairUrl"\s*:\s*"([^"]+)"/);
  return m ? m[1] : "";
}

/** Fallback web-UI URL when pairing-link minting fails (an already-paired
 *  browser session still gets in). `probedUrl` is the origin the last VM probe
 *  reported (probe.js `toState` -> the t3code agent's `url`), which knows whether
 *  the TLS proxy is on; it is VALIDATED here rather than trusted, because it
 *  originates from the VM's config.env and ends up in openExternal. Without it,
 *  fall back to today's plain-http guess. */
function baseUrl(cfg, probedUrl) {
  if (probe.isSafeOrigin(probedUrl)) return String(probedUrl);
  const host = (cfg && cfg.vmHost) || ssh.DEFAULTS.vmHost;
  return `http://${host}:${DEFAULT_PORT}`;
}

/** Mint a pairing link on the VM and open it in the host browser. Falls back to
 *  the base URL (already-paired browsers) when minting fails. `opts.instance`
 *  (the active instance) selects the pairing script variant; `opts.webUrl` is the
 *  origin the last probe reported, used only for that fallback. */
async function openWebUi(opts = {}) {
  const vscode = opts._vscode || vsc();
  const _ssh = opts._ssh || ssh;
  const r = await _ssh.runRemoteScript(buildPairingScript(opts.instance), { ...opts, timeoutMs: opts.timeoutMs || 30000 });
  let url = r.code === 0 ? extractPairUrl(r.stdout) : "";
  if (!url) {
    url = baseUrl(opts.cfg, opts.webUrl);
    vscode.window.showWarningMessage(
      "Couldn't mint a T3 Code pairing link (" + ((r.stderr || "").trim().slice(0, 120) || "exit " + r.code) +
      ") — opening the plain web UI; already-paired browsers still get in."
    );
  }
  await vscode.env.openExternal(vscode.Uri.parse(url));
  return url;
}

/** The settings toggle flipped ON: install + start T3 Code on the VM now, then
 *  open the web UI in the host browser. Offline VM → a toast explaining it will
 *  install on the next reprovision instead. Serialized against disableOnVm.
 *  `opts.channel` ("stable"|"nightly") decides the npm dist-tag. */
function enableOnVm(opts = {}) {
  return _serial(() => _enableNow(opts));
}

function _enableNow(opts = {}) {
  const vscode = opts._vscode || vsc();
  const _ssh = opts._ssh || ssh;
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Installing T3 Code on the VM…", cancellable: false },
    async () => {
      if (!(await _ssh.isReachable(opts))) {
        vscode.window.showWarningMessage("T3 Code enabled — the VM is offline, so it installs on the next reprovision.");
        return false;
      }
      const r = await _ssh.runRemoteScript(buildInstallScript(opts.channel), { ...opts, timeoutMs: opts.timeoutMs || 300000 });
      if (r.code !== 0) {
        vscode.window.showErrorMessage(
          ("Installing T3 Code failed (exit " + r.code + "). " + (r.stderr || "").slice(0, 200)).trim()
        );
        return false;
      }
      vscode.window.showInformationMessage("T3 Code installed and running — opening the web UI.");
      await openWebUi(opts);
      return true;
    }
  );
}

/** The settings toggle flipped OFF: stop the service on the VM (best-effort).
 *  Serialized against enableOnVm so a disable can't interleave with a running
 *  install. */
function disableOnVm(opts = {}) {
  return _serial(() => _disableNow(opts));
}

async function _disableNow(opts = {}) {
  const vscode = opts._vscode || vsc();
  const _ssh = opts._ssh || ssh;
  if (!(await _ssh.isReachable(opts))) {
    // Say so out loud: the host setting is already false, so a LATER save won't
    // re-trigger this — the VM-side service keeps running until the next
    // reprovision (the panel passes an explicit -T3Code false) stops it.
    vscode.window.showWarningMessage(
      "T3 Code disabled — the VM is offline, so its service is still deployed; reprovision (or toggle again while online) to stop it."
    );
    return false;
  }
  const r = await _ssh.runRemoteScript(buildDisableScript(), { ...opts, timeoutMs: opts.timeoutMs || 60000 });
  if (r.code === 0) vscode.window.showInformationMessage("T3 Code web GUI stopped on the VM.");
  return r.code === 0;
}

/** Reinstall t3 at a different channel on an already-enabled VM. Serialized
 *  through the same queue as enable/disable so a rapid stable→nightly→stable
 *  doesn't interleave npm runs. */
function setChannelOnVm(channel, opts = {}) {
  return _serial(() => _setChannelNow(channel, opts));
}

async function _setChannelNow(channel, opts = {}) {
  const vscode = opts._vscode || vsc();
  const _ssh = opts._ssh || ssh;
  const tag = npmTag(channel);
  const label = channel === "nightly" ? "nightly" : "stable";
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `Switching T3 Code to ${label}…`, cancellable: false },
    async () => {
      if (!(await _ssh.isReachable(opts))) {
        vscode.window.showWarningMessage(`T3 Code channel set to ${label} — the VM is offline, so it applies on the next reprovision.`);
        return false;
      }
      const r = await _ssh.runRemoteScript(buildInstallScript(channel), { ...opts, timeoutMs: opts.timeoutMs || 300000 });
      if (r.code !== 0) {
        vscode.window.showErrorMessage(
          (`Switching T3 Code to ${label} failed (exit ${r.code}). ` + (r.stderr || "").slice(0, 200)).trim()
        );
        return false;
      }
      vscode.window.showInformationMessage(`T3 Code switched to ${label} and restarted.`);
      return true;
    }
  );
}

function planT3LiveAction(wantT3, hadT3, newCh, oldCh) {
  if (wantT3 && !hadT3) return { action: "enable", channel: newCh };
  if (!wantT3 && hadT3) return { action: "disable" };
  if (wantT3 && hadT3 && newCh !== oldCh) return { action: "setChannel", channel: newCh };
  return null;
}

module.exports = {
  SERVICE, DEFAULT_PORT, npmTag,
  buildInstallScript, buildDisableScript, buildPairingScript,
  extractPairUrl, baseUrl,
  openWebUi, enableOnVm, disableOnVm, setChannelOnVm,
  planT3LiveAction, _resetQueue,
};
