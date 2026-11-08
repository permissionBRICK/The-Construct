#!/usr/bin/env bash
# Regression tests for bin/construct-agent-fix.sh, the VM side of the "Autofix it"
# choice after a provisioning run with failed steps, and for the Claude folder trust
# that lets its session start without a question. Stub claude/codex/opencode
# binaries record how they were called; no real agent runs. The script needs a
# terminal, so each run goes through script(1) (util-linux) for a pty.
# Run: bash test/agent-fix.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/construct-agent-fix.sh"
tmp="$(mktemp -d)"
trap 'rm -r "${tmp}"' EXIT

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

stubs="${tmp}/stubs"
home="${tmp}/home"
workspace="${tmp}/repos"
mkdir -p "${stubs}" "${home}" "${workspace}"
calls="${tmp}/calls"
where="${tmp}/where"

# Each stub writes its argv (one argument per line) to $CALLS, and its working
# directory and whether it has a terminal to $WHERE.
cat >"${stubs}/claude" <<'EOF'
#!/usr/bin/env bash
{ printf 'IS_SANDBOX=%s\n' "${IS_SANDBOX:-}"; printf '%s\n' "$@"; } >"${CALLS}"
tty=no; [ -t 0 ] && [ -t 1 ] && tty=tty
printf '%s\n' "$(pwd)" "${tty}" >"${WHERE}"
exit "${CLAUDE_EXIT:-0}"
EOF
cat >"${stubs}/codex" <<'EOF'
#!/usr/bin/env bash
if [ "${1:-}" = debug ] && [ "${2:-}" = models ]; then
  [ -n "${CODEX_CATALOG:-}" ] || exit 1
  cat "${CODEX_CATALOG}"
  exit 0
fi
printf '%s\n' "$@" >"${CALLS}"
exit "${CODEX_EXIT:-0}"
EOF
cat >"${stubs}/opencode" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" >"${CALLS}"
EOF
chmod +x "${stubs}"/*

# A PATH with the few tools the script needs, but no agent CLIs.
bare="${tmp}/bare"
mkdir -p "${bare}"
for tool in bash base64 cat grep; do ln -s "$(command -v "${tool}")" "${bare}/${tool}"; done

b64() { printf '%s' "$1" | base64 -w0; }
# Run the script on a pty, the way ssh -t starts it. Extra environment comes from
# the caller (VAR=value run ...); the exit status is the script's.
in_pty() { script -qec "$(printf '%q ' "$@")" /dev/null; }
run() {
  rm -f "${calls}" "${where}"
  in_pty env HOME="${home}" WORKSPACE_ROOT="${workspace}" CALLS="${calls}" WHERE="${where}" \
    PATH="${stubs}:${PATH}" bash "${SCRIPT}" "$@"
}
has_arg() { grep -qxF -- "$1" "${calls}"; }
no_arg() { ! grep -qxF -- "$1" "${calls}"; }
first_arg_is() { [ "$(head -1 "${calls}")" = "$1" ]; }
last_arg_is() { [ "$(tail -n "$(printf '%s\n' "$1" | wc -l)" "${calls}")" = "$1" ]; }
arg_after() { grep -A1 -xF -- "$1" "${calls}" | sed -n 2p; }

prompt="$(printf "On the last Construct provisioning run, the following step(s) failed:\n- Step 'Installing .NET SDK' failed (exit 7); log: /var/log/construct/provision/step-0.log\nRead the provisioning log at /var/log/construct/provision/step-0.log and diagnose and fix the underlying problem.")"

# ── usage errors ─────────────────────────────────────────────────────────────
run --agent nope --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "an unknown agent is a usage error" test $? -eq 2
run --agent claude >/dev/null 2>&1
ok "a missing prompt is a usage error" test $? -eq 2
run --agent claude --prompt-b64 '!!!not-base64' >/dev/null 2>&1
ok "a prompt that is not base64 is rejected" test $? -eq 2
in_pty env HOME="${home}" CALLS="${calls}" PATH="${bare}" "$(command -v bash)" "${SCRIPT}" --agent codex --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "a missing agent CLI exits 127" test $? -eq 127
rm -f "${calls}"
out="$(env HOME="${home}" CALLS="${calls}" WHERE="${where}" PATH="${stubs}:${PATH}" bash "${SCRIPT}" --agent claude --prompt-b64 "$(b64 x)" </dev/null 2>&1)"
status=$?
ok "without a terminal: refuses, and says to use ssh -t" sh -c "test ${status} -eq 2 && grep -q 'ssh -t' <<'X'
${out}
X"
ok "without a terminal: no agent starts" test ! -e "${calls}"

# ── claude: interactive, bypass, the prompt as its first message ─────────────
run --agent claude --model-b64 "$(b64 opus)" --prompt-b64 "$(b64 "${prompt}")" >/dev/null 2>&1
status=$?
ok "claude: exits with the agent's status" test "${status}" -eq 0
ok "claude: its interactive UI, on the terminal" sh -c "grep -qx tty '${where}'"
ok "claude: not print mode" no_arg -p
ok "claude: no stream output" no_arg --output-format
ok "claude: bypass permission mode" test "$(arg_after --permission-mode)" = bypassPermissions
ok "claude: IS_SANDBOX=1 so bypass works as root" grep -qx 'IS_SANDBOX=1' "${calls}"
ok "claude: passes the chosen model" test "$(arg_after --model)" = opus
ok "claude: the prompt arrives intact as the last argument" last_arg_is "${prompt}"
ok "claude: starts in the workspace root (trusted, so no trust question)" test "$(head -1 "${where}")" = "${workspace}"

CLAUDE_EXIT=1 run --agent claude --prompt-b64 "$(b64 x)" >/dev/null 2>&1
status=$?
ok "claude: keeps a failed session's exit status" test "${status}" -eq 1
ok "claude: no --model for the default model" no_arg --model

rm -f "${calls}" "${where}"
in_pty env HOME="${home}" WORKSPACE_ROOT="${tmp}/missing" CALLS="${calls}" WHERE="${where}" \
  PATH="${stubs}:${PATH}" bash "${SCRIPT}" --agent claude --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "claude: no workspace root -> starts in the home directory" test "$(head -1 "${where}")" = "${home}"

# ── codex: interactive and the latest-model families ─────────────────────────
catalog="${tmp}/catalog.json"
cat >"${catalog}" <<'EOF'
{"models":[
 {"slug":"gpt-5.6-sol","visibility":"list","priority":6},
 {"slug":"gpt-6.1-sol","visibility":"list","priority":0},
 {"slug":"gpt-6-sol","visibility":"list","priority":4},
 {"slug":"gpt-6-astra","visibility":"list","priority":3},
 {"slug":"gpt-7-astra","visibility":"hide","priority":9},
 {"slug":"gpt-6-astra-mini","visibility":"list","priority":9},
 {"slug":"gpt-6-luna","visibility":"list","priority":5}
]}
EOF
out="$(CODEX_CATALOG="${catalog}" run --agent codex --model-b64 "$(b64 latest:astra)" --prompt-b64 "$(b64 "${prompt}")" 2>&1)"
ok "codex: its interactive UI, not exec mode" sh -c "! grep -qx exec '${calls}'"
ok "codex: no approvals or sandbox, as Construct configures it" first_arg_is --dangerously-bypass-approvals-and-sandbox
ok "codex: latest astra = the newest listed astra (hidden and variants ignored)" test "$(arg_after --model)" = gpt-6-astra
ok "codex: names the resolved model" grep -q 'Latest astra model: gpt-6-astra' <<<"${out}"
ok "codex: the prompt arrives intact as the last argument" last_arg_is "${prompt}"

CODEX_CATALOG="${catalog}" run --agent codex --model-b64 "$(b64 latest:sol)" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "codex: latest sol compares versions numerically (6.1 > 6 > 5.6)" test "$(arg_after --model)" = gpt-6.1-sol

mkdir -p "${home}/.codex"
cp "${catalog}" "${home}/.codex/models_cache.json"
run --agent codex --model-b64 "$(b64 latest:sol)" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "codex: falls back to the cached catalog" test "$(arg_after --model)" = gpt-6.1-sol
rm "${home}/.codex/models_cache.json"

out="$(run --agent codex --model-b64 "$(b64 latest:astra)" --prompt-b64 "$(b64 x)" 2>&1)"
ok "codex: no catalog -> the default model, and it says so" sh -c "! grep -qx -- '--model' '${calls}' && grep -q 'using Codex' <<'X'
${out}
X"

CODEX_EXIT=4 run --agent codex --model-b64 "$(b64 "gpt-5.6-luna")" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
status=$?
ok "codex: a custom model passes through" test "$(arg_after --model)" = gpt-5.6-luna
ok "codex: exits with the agent's status" test "${status}" -eq 4

# ── opencode ─────────────────────────────────────────────────────────────────
odd_model="openrouter/qwen/qwen3:free'\"; touch ${tmp}/pwned"
run --agent opencode --model-b64 "$(b64 "${odd_model}")" --prompt-b64 "$(b64 "${prompt}")" >/dev/null 2>&1
ok "opencode: its interactive UI, not run mode" sh -c "! grep -qx run '${calls}'"
ok "opencode: a model with shell characters arrives verbatim" test "$(arg_after --model)" = "${odd_model}"
ok "opencode: and nothing in it was executed" test ! -e "${tmp}/pwned"
ok "opencode: the prompt arrives intact through --prompt" sh -c "test \"\$(grep -A1000 -xF -- '--prompt' '${calls}' | tail -n +2)\" = \"\$1\"" _ "${prompt}"
run --agent opencode --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "opencode: no --model for the default model" no_arg --model

# ── Claude folder trust for the workspace root (bin/install-ai-tools.sh) ─────
trust() {
  # shellcheck disable=SC2016 # $1/$2 belong to the inner bash
  env WORKSPACE_ROOT=/root/repos CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true \
    bash -c 'source "$1"; configure_claude_workspace_trust "$2" "$(id -un)"' _ "${ROOT}/bin/install-ai-tools.sh" "$1" 2>&1
}
trusted() { jq -e '.projects["/root/repos"].hasTrustDialogAccepted == true' "$1/.claude.json" >/dev/null; }

th="${tmp}/trust-new"
mkdir -p "${th}"
trust "${th}" >/dev/null
ok "trust: a missing ~/.claude.json is created with the workspace root trusted" trusted "${th}"

th="${tmp}/trust-merge"
mkdir -p "${th}"
printf '%s\n' '{"numStartups":3,"mcpServers":{"x":{"command":"y"}},"projects":{"/root/repos":{"disabledMcpServers":["z"]},"/other":{"hasTrustDialogAccepted":false}}}' >"${th}/.claude.json"
trust "${th}" >/dev/null
ok "trust: sets the flag on an existing entry" trusted "${th}"
ok "trust: keeps everything else" jq -e '.numStartups == 3 and .mcpServers.x.command == "y"
  and .projects["/root/repos"].disabledMcpServers == ["z"] and .projects["/other"].hasTrustDialogAccepted == false' "${th}/.claude.json" >/dev/null
before="$(cat "${th}/.claude.json")"
out="$(trust "${th}")"
ok "trust: already trusted -> the file is left alone" sh -c "test \"\$1\" = \"\$(cat '${th}/.claude.json')\" && test -z \"\$2\"" _ "${before}" "${out}"

th="${tmp}/trust-bad"
mkdir -p "${th}"
printf 'not json' >"${th}/.claude.json"
out="$(trust "${th}")"
ok "trust: an invalid file is left as it is, with a warning" sh -c "test \"\$(cat '${th}/.claude.json')\" = 'not json' && grep -q 'not valid JSON' <<'X'
${out}
X"
ok "trust: installing Claude Code applies it" sh -c "awk '/^install_claude_code\\(\\)/,/^}/' '${ROOT}/bin/install-ai-tools.sh' | grep -q 'configure_claude_workspace_trust \"\${claude_home}\" \"\${claude_owner}\"'"

printf '\n  agent-fix tests - %d/%d passed\n' "${pass}" "$((pass + fail))"
[ "${fail}" -eq 0 ]
