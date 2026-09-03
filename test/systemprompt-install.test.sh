#!/usr/bin/env bash
# Plain-Bash regression tests for install_agent_system_prompt in
# bin/install-ai-tools.sh. Run: bash test/systemprompt-install.test.sh
#
# The failure these guard against: the agent instruction files
# (~/.claude/CLAUDE.md, ~/.codex/AGENTS.md, ~/.config/opencode/AGENTS.md) used to
# be rewritten from the shipped prompt on EVERY provision, so machine-local
# additions -- house rules that cannot live in this repo, which ships to
# everyone -- vanished on the next unrelated reprovision without a word. A
# reinstall already keeps them (restore-config.sh overlays the backed-up home
# after provisioning), so a reprovision must not be the one path that loses them.
#
# Everything here runs against temp dirs -- no VM paths are touched.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
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

# Run install_agent_system_prompt against a fake repo/state, capturing output.
# $1 = case dir, $2 = shipped prompt body, rest = extra shell run after the call.
run_install() {
  local case_dir="$1" body="$2"
  mkdir -p "${case_dir}/repo/config"
  printf '%s' "${body}" >"${case_dir}/repo/config/systemprompt.md"
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true \
  REPO_DIR="${case_dir}/repo" \
  CONSTRUCT_EXTERNAL_HOST="vm.example.test" \
  AGENT_SYSTEM_PROMPT_STATE="${case_dir}/state.sha256" \
    bash -c 'source "$1"; install_agent_system_prompt "$2" root' \
      _ "${SCRIPT}" "${case_dir}/CLAUDE.md" 2>&1
}

file_is() { [[ "$(cat "$1")" == "$2" ]]; }

# 1. Missing destination -> written, DNS substituted, checksum recorded.
c="${tmp}/fresh"; mkdir -p "${c}"
out="$(run_install "${c}" 'base v1 __AGENT_DNS__')"
ok "fresh VM: writes the rendered prompt" file_is "${c}/CLAUDE.md" 'base v1 vm.example.test'
ok "fresh VM: records the checksum" \
  grep -qF "  ${c}/CLAUDE.md" "${c}/state.sha256"

# 2. Untouched destination + newer shipped prompt -> refreshed.
out="$(run_install "${c}" 'base v2 __AGENT_DNS__')"
ok "untouched file: refreshed from the newer shipped prompt" \
  file_is "${c}/CLAUDE.md" 'base v2 vm.example.test'

# 3. Locally modified destination -> kept verbatim, and kept again next time.
printf '\nlocal house rule\n' >>"${c}/CLAUDE.md"
modified="$(cat "${c}/CLAUDE.md")"
out="$(run_install "${c}" 'base v3 __AGENT_DNS__')"
ok "locally modified file: left alone" file_is "${c}/CLAUDE.md" "${modified}"
ok "locally modified file: says so" \
  grep -q "keeping locally modified" <<<"${out}"
out="$(run_install "${c}" 'base v4 __AGENT_DNS__')"
ok "locally modified file: still left alone on the next provision" \
  file_is "${c}/CLAUDE.md" "${modified}"

# 4. Destination from a VM provisioned before this bookkeeping existed (file
#    present, no recorded checksum) -> adopted once, protected from then on.
c="${tmp}/legacy"; mkdir -p "${c}"
printf 'old managed content\n' >"${c}/CLAUDE.md"
out="$(run_install "${c}" 'base v1 __AGENT_DNS__')"
ok "unrecorded file: adopted (overwritten) once" \
  file_is "${c}/CLAUDE.md" 'base v1 vm.example.test'
printf 'local addition\n' >>"${c}/CLAUDE.md"
adopted="$(cat "${c}/CLAUDE.md")"
out="$(run_install "${c}" 'base v2 __AGENT_DNS__')"
ok "unrecorded file: protected once adopted" file_is "${c}/CLAUDE.md" "${adopted}"

# 5. Several destinations share one state file without clobbering each other.
c="${tmp}/multi"; mkdir -p "${c}"
run_install "${c}" 'base v1 __AGENT_DNS__' >/dev/null
CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${c}/repo" \
CONSTRUCT_EXTERNAL_HOST="vm.example.test" \
AGENT_SYSTEM_PROMPT_STATE="${c}/state.sha256" \
  bash -c 'source "$1"; install_agent_system_prompt "$2" root' \
    _ "${SCRIPT}" "${c}/AGENTS.md" >/dev/null 2>&1
ok "two destinations: both checksums recorded" \
  bash -c '[[ $(wc -l <"'"${c}"'/state.sha256") -eq 2 ]]'
printf 'local\n' >>"${c}/CLAUDE.md"
run_install "${c}" 'base v2 __AGENT_DNS__' >/dev/null
ok "two destinations: editing one does not unprotect the other" \
  bash -c 'grep -q "^base v2" "'"${c}"'/AGENTS.md" || true; grep -q "local" "'"${c}"'/CLAUDE.md"'

# 6. Missing shipped prompt -> warn, touch nothing.
c="${tmp}/nosrc"; mkdir -p "${c}/repo/config"
printf 'untouched\n' >"${c}/CLAUDE.md"
out="$(CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${c}/repo" \
  AGENT_SYSTEM_PROMPT_STATE="${c}/state.sha256" \
  bash -c 'source "$1"; install_agent_system_prompt "$2" root' \
    _ "${SCRIPT}" "${c}/CLAUDE.md" 2>&1)"
ok "missing shipped prompt: destination untouched" file_is "${c}/CLAUDE.md" 'untouched'
ok "missing shipped prompt: warns" grep -q "system prompt not found" <<<"${out}"

printf '\n  systemprompt install tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
