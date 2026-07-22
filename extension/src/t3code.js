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

function vsc() { return require("vscode"); }

const SERVICE = "t3code-serve";
const DEFAULT_PORT = 5177;
const DEFAULT_HOST_BIND = "0.0.0.0";

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

// Shared bash prelude: read the T3CODE_* bind settings (and workspace root) from
// config.env with the same defaults the provisioner uses, plus an idempotent
// config.env key writer (a trimmed-down config-set.sh — values here are always
// shell-safe literals).
const PRELUDE = `set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
cfgget() { sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1; }
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="\${T3CODE_HOST:-${DEFAULT_HOST_BIND}}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="\${T3CODE_PORT:-${DEFAULT_PORT}}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="\${WORKSPACE_ROOT:-/root/repos}"
`;

/** Bash: install/update t3, persist the opt-in + bind keys, deploy + start the
 *  systemd service. Self-contained; exits non-zero on a real failure. */
function buildInstallScript() {
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
echo "== installing t3 =="
# node-pty/msgpackr-extract need their build scripts; newer npm gates them behind
# --allow-scripts, older npm ignores the unknown flag and runs them anyway.
npm install -g t3@latest --allow-scripts=node-pty,msgpackr-extract || exit 1
command -v t3 >/dev/null 2>&1 || { echo "t3 binary not found after install" >&2; exit 1; }
t3_bin="$(command -v t3)"
if [ "$t3_bin" != /usr/local/bin/t3 ]; then
  resolved="$(readlink -f "$t3_bin" 2>/dev/null || echo "$t3_bin")"
  [ "$resolved" != /usr/local/bin/t3 ] && [ -x "$resolved" ] && ln -sf "$resolved" /usr/local/bin/t3
fi
cfgset T3CODE true
cfgset T3CODE_HOST "$T3CODE_HOST"
cfgset T3CODE_PORT "$T3CODE_PORT"
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

[Install]
WantedBy=multi-user.target
UNIT
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
if [ -f /etc/systemd/system/${SERVICE}.service ]; then
  systemctl disable --now ${SERVICE} 2>/dev/null || true
  echo "${SERVICE} stopped and disabled"
else
  echo "${SERVICE} not deployed; nothing to stop"
fi
exit 0
`;
}

/** Bash: mint a one-time pairing token and print the ready-to-open JSON
 *  ({... "pairUrl": "http://<dns>:<port>/pair#token=..."}). */
function buildPairingScript() {
  return PRELUDE + `
command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
base="http://$(hostname).mshome.net:\${T3CODE_PORT}"
t3 auth pairing create --json --ttl 10m --label "construct-control-panel" --base-url "$base" --log-level none
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
 *  browser session still gets in). */
function baseUrl(cfg) {
  const host = (cfg && cfg.vmHost) || ssh.DEFAULTS.vmHost;
  return `http://${host}:${DEFAULT_PORT}`;
}

/** Mint a pairing link on the VM and open it in the host browser. Falls back to
 *  the plain base URL (already-paired browsers) when minting fails. */
async function openWebUi(opts = {}) {
  const vscode = opts._vscode || vsc();
  const r = await ssh.runRemoteScript(buildPairingScript(), { ...opts, timeoutMs: opts.timeoutMs || 30000 });
  let url = r.code === 0 ? extractPairUrl(r.stdout) : "";
  if (!url) {
    url = baseUrl(opts.cfg);
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
 *  install on the next reprovision instead. Serialized against disableOnVm. */
function enableOnVm(opts = {}) {
  return _serial(() => _enableNow(opts));
}

function _enableNow(opts = {}) {
  const vscode = opts._vscode || vsc();
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: "Installing T3 Code on the VM…", cancellable: false },
    async () => {
      if (!(await ssh.isReachable(opts))) {
        vscode.window.showWarningMessage("T3 Code enabled — the VM is offline, so it installs on the next reprovision.");
        return false;
      }
      const r = await ssh.runRemoteScript(buildInstallScript(), { ...opts, timeoutMs: opts.timeoutMs || 300000 });
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
  if (!(await ssh.isReachable(opts))) {
    // Say so out loud: the host setting is already false, so a LATER save won't
    // re-trigger this — the VM-side service keeps running until the next
    // reprovision (the panel passes an explicit -T3Code false) stops it.
    vscode.window.showWarningMessage(
      "T3 Code disabled — the VM is offline, so its service is still deployed; reprovision (or toggle again while online) to stop it."
    );
    return false;
  }
  const r = await ssh.runRemoteScript(buildDisableScript(), { ...opts, timeoutMs: opts.timeoutMs || 60000 });
  if (r.code === 0) vscode.window.showInformationMessage("T3 Code web GUI stopped on the VM.");
  return r.code === 0;
}

module.exports = {
  SERVICE, DEFAULT_PORT,
  buildInstallScript, buildDisableScript, buildPairingScript,
  extractPairUrl, baseUrl,
  openWebUi, enableOnVm, disableOnVm,
};
