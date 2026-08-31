#!/usr/bin/env bash
# Static regression checks for the VS Code artifact download fallbacks.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-vscode.sh"
source_text="$(<"${SCRIPT}")"

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"
  fi
}

ok "installer has the direct Microsoft artifact CDN" \
  grep -q 'vscode.download.prss.microsoft.com/dbazure/download/stable' "${SCRIPT}"
ok "CLI CDN artifact is commit-pinned" \
  grep -q 'vscode_cli_linux_%s_cli.tar.gz' "${SCRIPT}"
ok "server CDN artifact is commit-pinned" \
  grep -q 'vscode-server-linux-%s.tar.gz' "${SCRIPT}"
ok "downloads have a bounded connection timeout" \
  grep -q -- '--connect-timeout 15' "${SCRIPT}"
ok "known client commit tries CDN before update service" sh -c \
  'cdn=$(grep -n "direct_url=.*vscode_cli_cdn_url" "$1" | head -1 | cut -d: -f1); update=$(grep -n "latest/cli-" "$1" | head -1 | cut -d: -f1); test "$cdn" -lt "$update"' \
  _ "${SCRIPT}"
ok "script parses" bash -n "${SCRIPT}"

printf '\n  VS Code download tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
