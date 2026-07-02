#!/usr/bin/env bash
# Regression tests for the Claude Code partial-message streaming VM patch.
# Run: bash test/partial-streaming.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENABLE="${ROOT}/extension/vm/construct-partial-streaming-enable.sh"
DISABLE="${ROOT}/extension/vm/construct-partial-streaming-disable.sh"

pass=0
fail=0

ok() {
  name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "$name"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "$name"
  fi
}

contains() {
  needle="$1"
  file="$2"
  grep -qF "$needle" "$file"
}

count_fixed() {
  needle="$1"
  file="$2"
  grep -oF "$needle" "$file" | wc -l | tr -d ' '
}

make_ext() {
  home="$1"
  body="$2"
  ext="${home}/.vscode-server/extensions/anthropic.claude-code-9.9.9-linux-x64"
  mkdir -p "$ext"
  printf '%s' "$body" >"${ext}/extension.js"
  printf '%s\n' "${ext}/extension.js"
}

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
out="${tmp}/script.out"

# Current known minifier spelling: should patch, skip on a second enable, and
# restore byte-for-byte from the backup.
home1="${tmp}/home1"
orig1='x={includePartialMessages:!le.env.remoteName,agentProgress:1}'
file1="$(make_ext "$home1" "$orig1")"
HOME="$home1" bash "$ENABLE" >"$out" 2>/dev/null
ok "enable: patches current le.env gate" contains 'includePartialMessages:!le.env.remoteName||!0' "$file1"
HOME="$home1" bash "$ENABLE" >"$out" 2>/dev/null
ok "enable: idempotent (one suffix)" test "$(count_fixed '||!0' "$file1")" = "1"
HOME="$home1" bash "$DISABLE" >"$out" 2>/dev/null
ok "disable: restores backup byte-for-byte" test "$(cat "$file1")" = "$orig1"

# Future minifier churn: the VS Code import variable name is not stable, so the
# patch must not require `le`.
home2="${tmp}/home2"
file2="$(make_ext "$home2" 'q({includePartialMessages:!abc123_$ .env.remoteName})')"
# The fixture above intentionally has a space between the identifier and .env, which
# is not a valid minified member expression and should be left alone.
HOME="$home2" bash "$ENABLE" >"$out" 2>/dev/null
ok "enable: rejects malformed member expression" test "$(count_fixed '||!0' "$file2")" = "0"

home3="${tmp}/home3"
file3="$(make_ext "$home3" 'q({includePartialMessages:!abc123_$.env.remoteName,promptSuggestions:void 0})')"
HOME="$home3" bash "$ENABLE" >"$out" 2>/dev/null
ok "enable: patches renamed vscode import" contains 'includePartialMessages:!abc123_$.env.remoteName||!0' "$file3"

# No backup fallback: needed for copies patched by an older run or after backup
# cleanup. It should remove only the Construct suffix.
rm -f "${file3}.construct-partial.bak"
HOME="$home3" bash "$DISABLE" >"$out" 2>/dev/null
ok "disable: fallback reverts renamed import" contains 'includePartialMessages:!abc123_$.env.remoteName,promptSuggestions' "$file3"

printf '\n%d passed, %d failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
