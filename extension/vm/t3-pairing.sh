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
T3CODE_PUBLIC_BASE_URL="$(cfgget T3CODE_PUBLIC_BASE_URL)"
# The origin the pairing link is minted against, given the client-reachable host in $1.
t3base() {
  if [ -n "$T3CODE_PUBLIC_BASE_URL" ]; then printf '%s' "$T3CODE_PUBLIC_BASE_URL"; return 0; fi
  printf 'http://%s:%s' "$1" "$T3CODE_PORT"
}

command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed" >&2; exit 1; }
base="$(t3base "$(hostname).mshome.net")"
t3 auth pairing create --json --ttl 10m --label "construct-control-panel" --base-url "$base" --log-level none
