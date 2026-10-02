set -uo pipefail
# Settings → Access & services → "T3 Code address via your own proxy": write the
# VM's T3CODE_PROXY_URL now. The host has checked the value (an http(s) origin,
# or empty to remove it); `construct config` checks it again and writes
# config.env. A VM whose CLI predates `construct config` gets the same write
# through its checkout's config-set.sh.
url={{url}}
CONFIG_FILE=/etc/construct/config.env
if construct config --help >/dev/null 2>&1; then
  if [ -n "$url" ]; then construct config set t3-proxy-url "$url"; else construct config unset t3-proxy-url; fi
  exit $?
fi
if [ -z "$url" ]; then
  [ -f "$CONFIG_FILE" ] && sed -i '/^T3CODE_PROXY_URL=/d' "$CONFIG_FILE"
  exit 0
fi
renderer=/opt/construct/repo/bin/config-set.sh
[ -f "$renderer" ] || { echo "This VM's Construct copy is too old to store the address; reprovision it first." >&2; exit 1; }
bash "$renderer" "$CONFIG_FILE" T3CODE_PROXY_URL "$url" && printf 't3-proxy-url=%s\n' "$url"
