#!/usr/bin/env bash
# Plain-Bash regression tests for install_agent_system_prompt /
# install_agent_system_prompts_all in bin/install-ai-tools.sh.
# Run: bash test/systemprompt-install.test.sh
#
# The contract: the agent instruction files (~/.claude/CLAUDE.md,
# ~/.codex/AGENTS.md, ~/.config/opencode/AGENTS.md) are MANAGED -- rewritten
# from the shipped template on every provision, for every agent alike -- and
# machine-local rules live in ~/construct-custom-system-prompt.md, which is
# appended below the template each time. Edits made to the managed files
# themselves do not survive; that is by design now.
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

# Run install_agent_system_prompt against a fake repo + custom file, capturing output.
# $1 = case dir, $2 = shipped prompt body. The custom file is $1/custom.md.
run_install() {
  local case_dir="$1" body="$2"
  mkdir -p "${case_dir}/repo/config"
  printf '%s' "${body}" >"${case_dir}/repo/config/systemprompt.md"
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true \
  REPO_DIR="${case_dir}/repo" \
  CONSTRUCT_EXTERNAL_HOST="vm.example.test" \
  AGENT_SYSTEM_PROMPT_CUSTOM="${case_dir}/custom.md" \
    bash -c 'source "$1"; install_agent_system_prompt "$2" root' \
      _ "${SCRIPT}" "${case_dir}/CLAUDE.md" 2>&1
}

file_is() { [[ "$(cat "$1")" == "$2" ]]; }

# 1. Fresh destination, no custom file -> the rendered template, DNS substituted.
c="${tmp}/fresh"; mkdir -p "${c}"
out="$(run_install "${c}" 'base v1 __AGENT_DNS__'$'\n')"
ok "fresh VM: writes the rendered template" file_is "${c}/CLAUDE.md" 'base v1 vm.example.test'
ok "fresh VM: nothing appended without a custom file" \
  bash -c '! grep -q "appended" <<<"$1"' _ "${out}"

# 2. A newer shipped prompt replaces the file.
out="$(run_install "${c}" 'base v2 __AGENT_DNS__'$'\n')"
ok "newer template: file refreshed" file_is "${c}/CLAUDE.md" 'base v2 vm.example.test'

# 3. Edits made to the managed file itself do NOT survive a provision.
printf 'edited in place\n' >>"${c}/CLAUDE.md"
out="$(run_install "${c}" 'base v2 __AGENT_DNS__'$'\n')"
ok "managed file: in-place edits are overwritten" file_is "${c}/CLAUDE.md" 'base v2 vm.example.test'

# 4. The custom file is appended below the template, separated by a blank line.
printf '## House rule\n\nNever do X.\n' >"${c}/custom.md"
out="$(run_install "${c}" 'base v2 __AGENT_DNS__'$'\n')"
ok "custom file: appended after one blank line" \
  file_is "${c}/CLAUDE.md" $'base v2 vm.example.test\n\n## House rule\n\nNever do X.'
ok "custom file: the log says so" grep -q "appended the machine-local additions" <<<"${out}"

# 5. Re-rendering is idempotent (same input -> same file).
before="$(cat "${c}/CLAUDE.md")"
run_install "${c}" 'base v2 __AGENT_DNS__'$'\n' >/dev/null
ok "custom file: rendering twice gives the same file" file_is "${c}/CLAUDE.md" "${before}"

# 6. A custom file without a final newline still ends the file cleanly.
printf 'no newline at end' >"${c}/custom.md"
run_install "${c}" 'base v2 __AGENT_DNS__'$'\n' >/dev/null
ok "custom file: missing final newline is added" \
  bash -c '[[ "$(tail -c 1 "$1" | od -An -c | tr -d " ")" == "\\n" ]]' _ "${c}/CLAUDE.md"
ok "custom file: content intact" grep -q '^no newline at end$' "${c}/CLAUDE.md"

# 7. An empty custom file appends nothing.
: >"${c}/custom.md"
run_install "${c}" 'base v3 __AGENT_DNS__'$'\n' >/dev/null
ok "empty custom file: template only" file_is "${c}/CLAUDE.md" 'base v3 vm.example.test'

# 8. Missing shipped prompt -> warn, touch nothing.
c="${tmp}/nosrc"; mkdir -p "${c}/repo/config"
printf 'untouched\n' >"${c}/CLAUDE.md"
out="$(CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${c}/repo" \
  AGENT_SYSTEM_PROMPT_CUSTOM="${c}/custom.md" \
  bash -c 'source "$1"; install_agent_system_prompt "$2" root' \
    _ "${SCRIPT}" "${c}/CLAUDE.md" 2>&1)"
ok "missing shipped prompt: destination untouched" file_is "${c}/CLAUDE.md" 'untouched'
ok "missing shipped prompt: warns" grep -q "system prompt not found" <<<"${out}"

# 9. install_agent_system_prompts_all renders every agent whose config dir
#    exists (root only here: TARGET_USER=root) and skips the others.
c="${tmp}/all"; mkdir -p "${c}/repo/config" "${c}/home/.claude" "${c}/home/.config/opencode"
printf 'tpl __AGENT_DNS__\n' >"${c}/repo/config/systemprompt.md"
printf 'custom rule\n' >"${c}/custom.md"
out="$(CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${c}/repo" \
  CONSTRUCT_EXTERNAL_HOST="vm.example.test" TARGET_USER=root \
  AGENT_SYSTEM_PROMPT_CUSTOM="${c}/custom.md" \
  AGENT_SYSTEM_PROMPT_ROOT_HOME="${c}/home" \
  bash -c 'source "$1"; install_agent_system_prompts_all' _ "${SCRIPT}" 2>&1)"
ok "all: claude rendered" file_is "${c}/home/.claude/CLAUDE.md" $'tpl vm.example.test\n\ncustom rule'
ok "all: opencode rendered" file_is "${c}/home/.config/opencode/AGENTS.md" $'tpl vm.example.test\n\ncustom rule'
ok "all: codex skipped (no ~/.codex)" bash -c '[[ ! -e "$1" ]]' _ "${c}/home/.codex/AGENTS.md"
ok "all: reports the count" grep -q "Rendered 2 agent instruction file" <<<"${out}"

# 10. The shipped template tells agents where changes belong.
ok "template: points at the custom file and the CLI" \
  bash -c 'grep -q "construct-custom-system-prompt.md" "$1" && grep -q "construct systemprompt" "$1"' \
    _ "${ROOT}/config/systemprompt.md"

printf '\n  systemprompt install tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
