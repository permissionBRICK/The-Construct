#!/usr/bin/env bash
# Plain-Bash regression tests for the Claude Code rm-prompt waiver. Run:
#   bash test/claude-rm-prompt.test.sh
#
# In bypass permissions mode Claude Code still asks before an rm or rmdir whose
# target it cannot resolve statically. CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=1
# lifts that prompt, but Claude Code reads it only from the environment it starts
# with, so it must sit in every unit that starts Claude sessions and in
# /etc/environment for SSH logins (docs/provisioning.md, "Bypass-mode defaults").

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
LINE='Environment=CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=1'
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
check() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

# ── the units ────────────────────────────────────────────────────────────────

for unit in t3code-serve code-serve-web; do
  check "${unit}.service sets the variable" grep -qx "${LINE}" "${ROOT}/systemd/${unit}.service"
done
check "the Companion's embedded T3 unit sets the variable" \
  grep -qx "${LINE}" "${ROOT}/companion/src/Construct.Companion.Core/State/T3Code.cs"

# ── configure_claude_rm_prompt_env ───────────────────────────────────────────

# Source the helpers (installs nothing) and run the function on a scratch file.
apply() {
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true bash -c 'source "$1"; configure_claude_rm_prompt_env "$2"' _ "${SCRIPT}" "$1" \
    >/dev/null 2>&1
}

env_file="${tmp}/environment"
printf 'PATH="/usr/local/bin:/usr/bin:/bin"' >"${env_file}" # no trailing newline
apply "${env_file}"
check "appends the variable on its own line" \
  test "$(cat "${env_file}")" = $'PATH="/usr/local/bin:/usr/bin:/bin"\nCLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=1'
apply "${env_file}"
check "a second run adds nothing" \
  test "$(grep -c '^CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=' "${env_file}")" = 1

printf 'CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=0\n' >"${env_file}"
apply "${env_file}"
check "keeps a value the user set" \
  test "$(cat "${env_file}")" = 'CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=0'

rm -f "${env_file}"
apply "${env_file}"
check "creates a missing file" \
  test "$(cat "${env_file}")" = 'CLAUDE_CODE_EVEN_MORE_DANGEROUSLY_SKIP_PERMISSIONS=1'

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
