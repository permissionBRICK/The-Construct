"use strict";
// Live control for Construct's optional OpenCode background watcher plugin.
// The plugin is embedded so the Windows-host control panel can install it over
// SSH even when the VM's checked-out Construct repo is older than the panel.

const fs = require("node:fs");
const path = require("node:path");
const ssh = require("./ssh");

function vsc() { return require("vscode"); }

const MARKER = "Construct-managed OpenCode background watcher.";
const TARGET = "/root/.config/opencode/plugins/background.js";

function pluginB64() {
  return fs.readFileSync(path.join(__dirname, "..", "vm", "opencode-background.js")).toString("base64");
}

const PRELUDE = `set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
TARGET='${TARGET}'
MARKER='${MARKER}'
`;

function buildBackgroundWatcherEnableScript() {
  return PRELUDE + `
command -v node >/dev/null 2>&1 || { echo "node is required to validate the OpenCode plugin" >&2; exit 1; }
mkdir -p "$(dirname "$TARGET")"
if [ -L "$TARGET" ] && case "$(readlink -f "$TARGET" 2>/dev/null || true)" in */opencode-cortecs-config/plugins/background.js) true;; *) false;; esac; then
  rm -f -- "$TARGET"
elif [ -e "$TARGET" ] || [ -L "$TARGET" ]; then
  grep -Fq "$MARKER" "$TARGET" 2>/dev/null || { echo "refusing to replace unmanaged OpenCode plugin: $TARGET" >&2; exit 2; }
fi
tmp="${TARGET}.construct-tmp.js"
printf '%s' '${pluginB64()}' | base64 -d > "$tmp"
node --check "$tmp" || { rm -f -- "$tmp"; exit 1; }
chmod 0644 "$tmp"
mv -f -- "$tmp" "$TARGET"
cfgset OPENCODE_BACKGROUND_WATCHER true
if systemctl is-active --quiet opencode-serve 2>/dev/null; then systemctl --no-block restart opencode-serve; fi
echo "OpenCode background watcher enabled (service restart queued; new CLI/T3 sessions load it on start)"
`;
}

function buildBackgroundWatcherDisableScript() {
  return PRELUDE + `
if [ -e "$TARGET" ] || [ -L "$TARGET" ]; then
  grep -Fq "$MARKER" "$TARGET" 2>/dev/null || { echo "refusing to remove unmanaged OpenCode plugin: $TARGET" >&2; exit 2; }
  rm -f -- "$TARGET"
fi
cfgset OPENCODE_BACKGROUND_WATCHER false
if systemctl is-active --quiet opencode-serve 2>/dev/null; then systemctl --no-block restart opencode-serve; fi
echo "OpenCode background watcher disabled (service restart queued; running CLI/T3 sessions are unchanged)"
`;
}

function planBackgroundWatcherLiveAction(want, had) {
  if (want === had) return null;
  return want ? "enable" : "disable";
}

let inflight = Promise.resolve();
function serial(fn) {
  const run = inflight.then(fn, fn);
  inflight = run.then(() => {}, () => {});
  return run;
}
function resetQueue() { inflight = Promise.resolve(); }

function setBackgroundWatcherOnVm(enable, opts = {}) {
  return serial(() => setBackgroundWatcherNow(enable, opts));
}

async function setBackgroundWatcherNow(enable, opts = {}) {
  const vscode = opts._vscode || vsc();
  const remote = opts._ssh || ssh;
  const label = enable ? "Enabling" : "Disabling";
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `${label} OpenCode background watcher…`, cancellable: false },
    async () => {
      if (!(await remote.isReachable(opts))) {
        vscode.window.showWarningMessage("OpenCode background-watcher preference saved — the VM is offline, so it applies on the next reprovision.");
        return false;
      }
      const script = enable ? buildBackgroundWatcherEnableScript() : buildBackgroundWatcherDisableScript();
      const result = await remote.runRemoteScript(script, { ...opts, timeoutMs: opts.timeoutMs || 60000 });
      if (result.code !== 0) {
        const detail = (result.stderr || result.stdout || "").trim().slice(0, 250);
        vscode.window.showErrorMessage(`${label} OpenCode background watcher failed (exit ${result.code}). ${detail}`.trim());
        return false;
      }
      vscode.window.showInformationMessage(
        enable
          ? "OpenCode background watcher enabled — new sessions get background, background_output, and background_kill."
          : "OpenCode background watcher disabled — new sessions run without those tools.",
      );
      return true;
    },
  );
}

module.exports = {
  MARKER,
  TARGET,
  buildBackgroundWatcherEnableScript,
  buildBackgroundWatcherDisableScript,
  planBackgroundWatcherLiveAction,
  setBackgroundWatcherOnVm,
  _resetQueue: resetQueue,
};
