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
{{pairingBase}}
command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
# The client-reachable name of THIS VM. B2 records it in config.env as
# CONSTRUCT_EXTERNAL_HOST (a remote/forwarded instance is not reachable at its own
# mshome name); absent, fall back to the local $(hostname).mshome.net.
ext="$(cfgget CONSTRUCT_EXTERNAL_HOST)"
base="$(t3base "${ext:-$(hostname).mshome.net}")" || exit 7
t3 auth pairing create --json --ttl 10m --label "construct-{{instance}}" --base-url "$base" --log-level none
