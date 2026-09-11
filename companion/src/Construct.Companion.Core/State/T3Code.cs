using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Probe;

namespace Construct.Companion.Core.State;

public static class T3Code
{
    private const string InstallTemplate = """
set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
# Undo config-set.sh's rendering: it writes values made only of its safe charset
# bare, and single-quotes anything else (embedded apostrophes as '\''). An IPv6
# public base URL — https://[2001:db8::1]:5178 — has brackets, so it IS stored
# quoted, and reading the raw line would carry the apostrophes into the value.
cfgget() {
  _v="$(sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1)"
  case "$_v" in
    "'"*"'") _v="${_v#\'}"; _v="${_v%\'}"; _v="${_v//\'\\\'\'/\'}" ;;
  esac
  printf '%s' "$_v"
}
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="${T3CODE_PORT:-5177}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

# t3's engines field requires Node ^22.16 || ^23.11 || >=24.10 — npm merely
# WARNS on a mismatch, leaving a broken install whose service restart-loops, so
# check the actual version (not just npm presence) and upgrade the system Node
# via NodeSource when it's too old (the same channel install-sdks.sh uses).
t3_node_ok() {
  v="$(node -v 2>/dev/null | sed 's/^v//')" || return 1
  [ -n "$v" ] || return 1
  major="${v%%.*}"; rest="${v#*.}"; minor="${rest%%.*}"
  [ "$major" -ge 25 ] && return 0
  case "$major" in
    24) [ "${minor:-0}" -ge 10 ] ;;
    23) [ "${minor:-0}" -ge 11 ] ;;
    22) [ "${minor:-0}" -ge 16 ] ;;
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
echo "== installing t3 ({{tag}}) =="
# node-pty/msgpackr-extract need their build scripts; newer npm gates them behind
# --allow-scripts, older npm ignores the unknown flag and runs them anyway.
npm install -g t3@{{tag}} --allow-scripts=node-pty,msgpackr-extract || exit 1
command -v t3 >/dev/null 2>&1 || { echo "t3 binary not found after install" >&2; exit 1; }
t3_bin="$(command -v t3)"
if [ "$t3_bin" != /usr/local/bin/t3 ]; then
  resolved="$(readlink -f "$t3_bin" 2>/dev/null || echo "$t3_bin")"
  [ "$resolved" != /usr/local/bin/t3 ] && [ -x "$resolved" ] && ln -sf "$resolved" /usr/local/bin/t3
fi
cfgset T3CODE true
cfgset T3CODE_HOST "$T3CODE_HOST"
cfgset T3CODE_PORT "$T3CODE_PORT"
cfgset T3CODE_CHANNEL {{channel}}
mkdir -p "$WORKSPACE_ROOT"
# Same unit the repo ships (systemd/t3code-serve.service); ${...} placeholders
# are expanded by systemd from the EnvironmentFile, not by this shell.
cat > /etc/systemd/system/t3code-serve.service <<UNIT
[Unit]
Description=T3 Code Server (web GUI for coding agents)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
EnvironmentFile=/etc/construct/config.env
WorkingDirectory=$WORKSPACE_ROOT
ExecStart=/usr/local/bin/t3 serve --host \${T3CODE_HOST} --port \${T3CODE_PORT}
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
# the restart below so `t3 serve` starts with T3CODE_PUBLIC_BASE_URL set.
if [ -f /opt/construct/repo/bin/setup-t3-https.sh ]; then
  bash /opt/construct/repo/bin/setup-t3-https.sh \
    || echo "warning: T3 HTTPS setup failed; the web GUI stays on plain http" >&2
else
  echo "note: this VM's Construct copy predates T3 HTTPS support; the web GUI stays on plain http until the next reprovision"
fi
systemctl daemon-reload
systemctl enable t3code-serve
systemctl restart t3code-serve
sleep 2
# Bootstrap one t3 project per git repo in the workspace so the web UI starts
# useful. (t3 serve's --auto-bootstrap-project-from-cwd flag is DEAD in the
# headless serve path — the handler hardcodes it off — hence explicit adds.)
# Idempotent: an already-registered path fails with ProjectAlreadyExistsError,
# which is swallowed; no duplicates are created.
for _repo in "$WORKSPACE_ROOT"/*/; do
  [ -d "${_repo}.git" ] || continue
  t3 project add "${_repo%/}" --log-level none >/dev/null 2>&1 || true
done
if systemctl is-active --quiet t3code-serve; then
  echo "t3code-serve running on $T3CODE_HOST:$T3CODE_PORT"
else
  echo "t3code-serve failed to start" >&2
  journalctl -u t3code-serve --no-pager -n 20 >&2 || true
  exit 1
fi

""";
    private const string Disable = """
set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
# Undo config-set.sh's rendering: it writes values made only of its safe charset
# bare, and single-quotes anything else (embedded apostrophes as '\''). An IPv6
# public base URL — https://[2001:db8::1]:5178 — has brackets, so it IS stored
# quoted, and reading the raw line would carry the apostrophes into the value.
cfgget() {
  _v="$(sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1)"
  case "$_v" in
    "'"*"'") _v="${_v#\'}"; _v="${_v%\'}"; _v="${_v//\'\\\'\'/\'}" ;;
  esac
  printf '%s' "$_v"
}
cfgset() {
  mkdir -p "$(dirname "$CONFIG_FILE")"; touch "$CONFIG_FILE"
  if grep -q "^$1=" "$CONFIG_FILE" 2>/dev/null; then sed -i "s|^$1=.*|$1=$2|" "$CONFIG_FILE"; else printf '%s=%s\n' "$1" "$2" >> "$CONFIG_FILE"; fi
}
T3CODE_HOST="$(cfgget T3CODE_HOST)"; T3CODE_HOST="${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="${T3CODE_PORT:-5177}"
WORKSPACE_ROOT="$(cfgget WORKSPACE_ROOT)"; WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

cfgset T3CODE false
cfgset CONSTRUCT_T3_VOICE_INPUT false
rm -f /etc/construct/t3code-desktop-status /etc/construct/t3code-installed-build
# Take the TLS proxy down with the server it fronts (an https listener in front of
# a stopped T3 only serves 502s). --teardown keeps the saved T3CODE_HTTPS
# preference and the local CA, so re-enabling needs no new Windows trust import.
if [ -f /opt/construct/repo/bin/setup-t3-https.sh ]; then
  bash /opt/construct/repo/bin/setup-t3-https.sh --teardown || true
fi
if [ -f /etc/systemd/system/t3code-serve.service ]; then
  systemctl disable --now t3code-serve 2>/dev/null || true
  echo "t3code-serve stopped and disabled"
else
  echo "t3code-serve not deployed; nothing to stop"
fi
exit 0

""";
    public static string BuildInstallScript(string? channel) => InstallTemplate.Replace("{{tag}}", channel == "nightly" ? "nightly" : "latest", StringComparison.Ordinal).Replace("{{channel}}", channel == "nightly" ? "nightly" : "stable", StringComparison.Ordinal);
    public static string BuildDisableScript() => Disable;
    public static string BuildPairingScript(JsonObject? instance) => Instances.IsDefaultInstance(instance) ? GuestScripts.Render("t3-pairing") : GuestScripts.Render("t3-pairing-instance", new Dictionary<string, string> { ["instance"] = StateJson.Text(instance?["name"]) ?? "" });
    public static string ExtractPairUrl(string? stdout)
    {
        var obj = StateJson.ParseObject(stdout); if (StateJson.Text(obj?["pairUrl"]) is {} url) return url;
        return Regex.Match(stdout ?? "", "\"pairUrl\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
    }
    public static string BaseUrl(string? host, string? probedUrl) => ProbeParser.IsSafeOrigin(probedUrl) ? probedUrl! : "http://" + (string.IsNullOrEmpty(host) ? "agent-vm.mshome.net" : host) + ":5177";
    public static JsonObject? PlanLiveAction(bool want, bool had, string newChannel, string oldChannel) => want && !had ? new JsonObject { ["action"] = "enable", ["channel"] = newChannel } : !want && had ? new JsonObject { ["action"] = "disable" } : want && had && newChannel != oldChannel ? new JsonObject { ["action"] = "setChannel", ["channel"] = newChannel } : null;
}
