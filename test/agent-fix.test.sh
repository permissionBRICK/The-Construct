#!/usr/bin/env bash
# Regression tests for bin/construct-agent-fix.sh, the VM side of the "Autofix it"
# choice after a provisioning run with failed steps. Stub claude/codex/opencode
# binaries record how they were called; no real agent runs.
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
mkdir -p "${stubs}" "${home}"
calls="${tmp}/calls"

# Each stub writes its argv (one argument per line) and IS_SANDBOX to $calls.
cat >"${stubs}/claude" <<'EOF'
#!/usr/bin/env bash
{ printf 'IS_SANDBOX=%s\n' "${IS_SANDBOX:-}"; printf '%s\n' "$@"; } >"${CALLS}"
if [ -n "${CLAUDE_STREAM:-}" ]; then cat "${CLAUDE_STREAM}"; else echo "plain final message"; fi
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
echo "codex final message"
exit "${CODEX_EXIT:-0}"
EOF
cat >"${stubs}/opencode" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" >"${CALLS}"
echo "opencode final message"
EOF
chmod +x "${stubs}"/*

# A PATH with the stubs and the few tools the script needs, but without jq.
nojq="${tmp}/nojq"
mkdir -p "${nojq}"
for tool in bash base64 cat grep; do ln -s "$(command -v "${tool}")" "${nojq}/${tool}"; done

b64() { printf '%s' "$1" | base64 -w0; }
run() { env HOME="${home}" CALLS="${calls}" PATH="${stubs}:${PATH}" bash "${SCRIPT}" "$@"; }
arg_line() { grep -nxF -- "$1" "${calls}" | head -1 | cut -d: -f1; }
has_arg() { grep -qxF -- "$1" "${calls}"; }
last_arg_is() { [ "$(tail -n "$(printf '%s\n' "$1" | wc -l)" "${calls}")" = "$1" ]; }

prompt="$(printf "On the last Construct provisioning run, the following step(s) failed:\n- Step 'Installing .NET SDK' failed (exit 7); log: /var/log/construct/provision/step-0.log\nRead the provisioning log at /var/log/construct/provision/step-0.log and diagnose and fix the underlying problem.")"

# ── usage errors ─────────────────────────────────────────────────────────────
run --agent nope --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "an unknown agent is a usage error" test $? -eq 2
run --agent claude >/dev/null 2>&1
ok "a missing prompt is a usage error" test $? -eq 2
run --agent claude --prompt-b64 '!!!not-base64' >/dev/null 2>&1
ok "a prompt that is not base64 is rejected" test $? -eq 2
env HOME="${home}" CALLS="${calls}" PATH="${nojq}" "$(command -v bash)" "${SCRIPT}" --agent codex --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "a missing agent CLI exits 127" test $? -eq 127

# ── claude: print mode, bypass, progress through jq ──────────────────────────
stream="${tmp}/stream.jsonl"
cat >"${stream}" <<'EOF'
{"type":"system","subtype":"init","model":"claude-opus"}
{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hidden"},{"type":"text","text":"Looking at the log."}]}}
{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"tail -n 50 /var/log/construct/provision/step-0.log\nsecond line"}}]}}
{"type":"user","message":{"content":[{"type":"tool_result","content":"lots of output"}]}}
not json at all
{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed: the SDK feed was unreachable; reinstalled it."}]}}
{"type":"result","subtype":"success","is_error":false,"result":"Fixed: the SDK feed was unreachable; reinstalled it."}
EOF
out="$(CLAUDE_STREAM="${stream}" run --agent claude --model-b64 "$(b64 opus)" --prompt-b64 "$(b64 "${prompt}")" 2>&1)"
status=$?
ok "claude: exits with the agent's status" test "${status}" -eq 0
ok "claude: runs in print mode" has_arg -p
ok "claude: bypass permission mode" sh -c "grep -qx -- '--permission-mode' '${calls}' && grep -qx bypassPermissions '${calls}'"
ok "claude: IS_SANDBOX=1 so bypass works as root" grep -qx 'IS_SANDBOX=1' "${calls}"
ok "claude: passes the chosen model" sh -c "grep -A1 -x -- '--model' '${calls}' | grep -qx opus"
ok "claude: streams JSON for the progress view" sh -c "grep -qx -- '--output-format' '${calls}' && grep -qx stream-json '${calls}' && grep -qx -- '--verbose' '${calls}'"
ok "claude: the prompt arrives intact as the last argument" last_arg_is "${prompt}"
ok "claude: shows the agent's text" grep -q 'Looking at the log.' <<<"${out}"
ok "claude: shows the final message" grep -q 'Fixed: the SDK feed was unreachable; reinstalled it.' <<<"${out}"
ok "claude: the final message is shown once (the result event does not repeat it)" \
  test "$(grep -c 'Fixed: the SDK feed' <<<"${out}")" -eq 1
ok "claude: one line per tool call, first command line only" grep -q '  > Bash: tail -n 50 /var/log/construct/provision/step-0.log' <<<"${out}"
ok "claude: no second command line" sh -c "! grep -q 'second line' <<'X'
${out}
X"
ok "claude: thinking and tool results stay hidden" sh -c "! grep -qE 'hidden|lots of output' <<'X'
${out}
X"
ok "claude: a non-JSON line passes through" grep -qx 'not json at all' <<<"${out}"

err_stream="${tmp}/err.jsonl"
printf '%s\n' '{"type":"result","subtype":"error_max_turns","is_error":true,"result":""}' >"${err_stream}"
out="$(CLAUDE_STREAM="${err_stream}" CLAUDE_EXIT=1 run --agent claude --prompt-b64 "$(b64 "${prompt}")" 2>&1)"
status=$?
ok "claude: a failed turn keeps the agent's exit status" test "${status}" -eq 1
ok "claude: a failed turn says why" grep -q 'Claude stopped (error_max_turns)' <<<"${out}"
ok "claude: no --model for the default model" sh -c "! grep -qx -- '--model' '${calls}'"

api_err="${tmp}/api-err.jsonl"
cat >"${api_err}" <<'EOF2'
{"type":"assistant","message":{"content":[{"type":"text","text":"There's an issue with the selected model."}]}}
{"type":"result","subtype":"success","is_error":true,"result":"There's an issue with the selected model."}
EOF2
out="$(CLAUDE_STREAM="${api_err}" CLAUDE_EXIT=1 run --agent claude --prompt-b64 "$(b64 x)" 2>&1)"
ok "claude: an error turn shows its message once and says it stopped" sh -c "test \"\$(grep -c 'issue with the selected model' <<'X'
${out}
X
)\" -eq 1 && grep -q 'Claude stopped with an error.' <<'X'
${out}
X"

out="$(env HOME="${home}" CALLS="${calls}" PATH="${stubs}:${nojq}" "$(command -v bash)" "${SCRIPT}" --agent claude --prompt-b64 "$(b64 "${prompt}")" 2>&1)"
ok "claude without jq: plain print mode, final message only" sh -c "grep -q 'plain final message' <<'X'
${out}
X
! grep -qx stream-json '${calls}'"
ok "claude without jq: still bypass and sandboxed" sh -c "grep -qx bypassPermissions '${calls}' && grep -qx 'IS_SANDBOX=1' '${calls}'"

# ── codex: exec mode and the latest-model families ───────────────────────────
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
ok "codex: exec mode" sh -c "head -1 '${calls}' | grep -qx exec"
ok "codex: runs outside a git repository" has_arg --skip-git-repo-check
ok "codex: no approvals or sandbox for the unattended turn" has_arg --dangerously-bypass-approvals-and-sandbox
ok "codex: latest astra = the newest listed astra (hidden and variants ignored)" sh -c "grep -A1 -x -- '--model' '${calls}' | grep -qx gpt-6-astra"
ok "codex: names the resolved model" grep -q 'Latest astra model: gpt-6-astra' <<<"${out}"
ok "codex: the prompt arrives intact as the last argument" last_arg_is "${prompt}"
ok "codex: relays the agent's output" grep -q 'codex final message' <<<"${out}"

CODEX_CATALOG="${catalog}" run --agent codex --model-b64 "$(b64 latest:sol)" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "codex: latest sol compares versions numerically (6.1 > 6 > 5.6)" sh -c "grep -A1 -x -- '--model' '${calls}' | grep -qx gpt-6.1-sol"

mkdir -p "${home}/.codex"
cp "${catalog}" "${home}/.codex/models_cache.json"
run --agent codex --model-b64 "$(b64 latest:sol)" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "codex: falls back to the cached catalog" sh -c "grep -A1 -x -- '--model' '${calls}' | grep -qx gpt-6.1-sol"
rm "${home}/.codex/models_cache.json"

out="$(run --agent codex --model-b64 "$(b64 latest:astra)" --prompt-b64 "$(b64 x)" 2>&1)"
ok "codex: no catalog -> the default model, and it says so" sh -c "! grep -qx -- '--model' '${calls}' && grep -q 'using Codex' <<'X'
${out}
X"

CODEX_EXIT=4 run --agent codex --model-b64 "$(b64 "gpt-5.6-luna")" --prompt-b64 "$(b64 x)" >/dev/null 2>&1
status=$?
ok "codex: a custom model passes through" sh -c "grep -A1 -x -- '--model' '${calls}' | grep -qx gpt-5.6-luna"
ok "codex: exits with the agent's status" test "${status}" -eq 4

# ── opencode ─────────────────────────────────────────────────────────────────
odd_model="openrouter/qwen/qwen3:free'\"; touch ${tmp}/pwned"
out="$(run --agent opencode --model-b64 "$(b64 "${odd_model}")" --prompt-b64 "$(b64 "${prompt}")" 2>&1)"
ok "opencode: run mode" sh -c "head -1 '${calls}' | grep -qx run"
ok "opencode: a model with shell characters arrives verbatim" sh -c "grep -A1 -x -- '--model' '${calls}' | tail -1 | grep -qxF -- \"\$1\"" _ "${odd_model}"
ok "opencode: and nothing in it was executed" test ! -e "${tmp}/pwned"
ok "opencode: the prompt arrives intact as the last argument" last_arg_is "${prompt}"
run --agent opencode --prompt-b64 "$(b64 x)" >/dev/null 2>&1
ok "opencode: no --model for the default model" sh -c "! grep -qx -- '--model' '${calls}'"

printf '\n  agent-fix tests - %d/%d passed\n' "${pass}" "$((pass + fail))"
[ "${fail}" -eq 0 ]
