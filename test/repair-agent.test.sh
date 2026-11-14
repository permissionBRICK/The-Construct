#!/usr/bin/env bash
# Tests for bin/construct-repair-agent.sh, the repair agent without a terminal (design
# 11.5). Stub claude/codex/opencode/t3 binaries stand in for the agents: they record how
# they were called and what reached them, and answer with the agent's own JSON events.
# No real agent runs. A t3 stub records any call: the repair never uses T3 Code.
# Run: bash test/repair-agent.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/construct-repair-agent.sh"
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

stubs="${tmp}/stubs"
mkdir -p "${stubs}" "${tmp}/repos" "${tmp}/run"
export CALLS="${tmp}/calls" SEEN="${tmp}/seen" T3CALLS="${tmp}/t3calls"
export CONSTRUCT_REPAIR_RUN_DIR="${tmp}/run" CONSTRUCT_CONFIG_FILE="${tmp}/config.env" WORKSPACE_ROOT="${tmp}/repos"
printf "AI_TOOLS='opencode,claude-code,codex'\n" >"${CONSTRUCT_CONFIG_FILE}"

# Claude Code: one process; each stdin line is a user message. The first gets a question,
# the next one "fixed". CLAUDE_HANG makes it read and never answer.
cat >"${stubs}/claude" <<'EOF'
#!/usr/bin/env bash
{ printf 'IS_SANDBOX=%s\n' "${IS_SANDBOX:-}"; printf '%s\n' "$@"; } >"${CALLS}"
echo "$$" >"${CALLS}.pid"
n=0
while IFS= read -r line; do
  printf '%s\n' "${line}" >>"${SEEN}"
  [ -n "${CLAUDE_STUBBORN:-}" ] && { ( trap '' TERM; echo "${BASHPID}" >"${CALLS}.stubborn"; while :; do sleep 1; done ) & }
  [ -n "${CLAUDE_HANG:-}" ] && { sleep 300; continue; }
  n=$((n + 1))
  echo '{"type":"system","subtype":"init","session_id":"s"}'
  echo '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"journalctl -u x"}}]}}'
  if [ "${n}" = 1 ]; then
    text='Two fixes are possible. Which one?\nREPAIR-STATUS: question'
  else
    text='Done, the cause is fixed.\nREPAIR-STATUS: fixed'
  fi
  printf '{"type":"assistant","message":{"content":[{"type":"text","text":"%s"}]}}\n' "${text}"
  printf '{"type":"result","subtype":"success","result":"%s","is_error":false}\n' "${text}"
done
EOF

# Codex: `exec` then `exec resume THREAD -`; the message comes on stdin.
cat >"${stubs}/codex" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" >>"${CALLS}"
echo "---" >>"${CALLS}"
cat >>"${SEEN}"; echo >>"${SEEN}"
echo '{"type":"thread.started","thread_id":"t-123"}'
echo '{"type":"item.started","item":{"type":"command_execution","command":"/bin/bash -lc ls"}}'
if [ "$2" = resume ]; then text='Fixed.\nREPAIR-STATUS: fixed'; else text='Which one?\nREPAIR-STATUS: question'; fi
printf '{"type":"item.completed","item":{"type":"agent_message","text":"%s"}}\n' "${text}"
echo '{"type":"turn.completed"}'
EOF

# OpenCode: `run` then `run --session ID`; the message comes on stdin.
cat >"${stubs}/opencode" <<'EOF'
#!/usr/bin/env bash
{ printf 'CONFIG=%s\n' "${OPENCODE_CONFIG_CONTENT:-}"; printf '%s\n' "$@"; echo "---"; } >>"${CALLS}"
cat >>"${SEEN}"; echo >>"${SEEN}"
case " $* " in *" --session "*) text='Fixed.\nREPAIR-STATUS: fixed' ;; *) text='Which one?\nREPAIR-STATUS: question' ;; esac
echo '{"type":"tool_use","sessionID":"ses_1","part":{"tool":"bash","state":{"input":{"command":"ls"}}}}'
printf '{"type":"text","sessionID":"ses_1","part":{"text":"%s"}}\n' "${text}"
EOF

cat >"${stubs}/t3" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${T3CALLS}"
EOF
cat >"${stubs}/systemctl" <<'EOF'
#!/usr/bin/env bash
[ "$*" = "is-active --quiet t3code-serve" ] && [ -n "${T3_ACTIVE:-}" ]
EOF
chmod +x "${stubs}"/*

# The stubs of the agents a case leaves out are hidden by a PATH without them.
only() {
  local dir="${tmp}/only-${1:-none}"
  rm -rf "${dir}"; mkdir -p "${dir}"
  local s
  for s in "$@"; do ln -s "${stubs}/${s}" "${dir}/${s}"; done
  ln -s "${stubs}/systemctl" "${dir}/systemctl"
  printf '%s' "${dir}"
}

prompt_b64="$(printf 'Step X failed; log: /var/log/x.log' | base64 -w0)"
out="${tmp}/out"

# Runs the script with a reply on stdin, kept open for $2 seconds so the end of stdin
# does not end the session first.
run() {
  local path="$1" hold="$2"
  shift 2
  rm -f "${CALLS}" "${SEEN}" "${CALLS}.pid"
  ( printf '%s\n' '{"reply":"Take option A; it is \"safe\" & $(touch /tmp/pwned)"}'; sleep "${hold}" ) |
    PATH="${path}:/usr/bin:/bin" timeout 30 bash "${SCRIPT}" --session sess1 --prompt-b64 "${prompt_b64}" "$@" >"${out}" 2>"${tmp}/err"
}

has_line() { jq -e "$1" "${out}" >/dev/null 2>&1 || grep -q . <(jq -c "select($1)" "${out}" 2>/dev/null); }
count() { jq -c "select($1)" "${out}" 2>/dev/null | wc -l; }
all_json() { [ -s "${out}" ] && jq -e . "${out}" >/dev/null 2>&1; }

echo "Claude Code"
run "$(only claude codex opencode)" 3
ok "every stdout line is JSON" all_json
ok "start names claude, direct" [ "$(count '.type=="start" and .agent=="claude" and .via=="direct"')" = 1 ]
ok "a tool line per action" [ "$(count '.type=="tool" and .text=="Bash: journalctl -u x"')" = 2 ]
ok "the agent's text without the status line" [ "$(count '.type=="text" and .text=="Two fixes are possible. Which one?"')" = 1 ]
ok "first turn asks" [ "$(count '.type=="turn" and .outcome=="question"')" = 1 ]
ok "second turn fixed" [ "$(count '.type=="turn" and .outcome=="fixed"')" = 1 ]
ok "ends fixed" [ "$(jq -sr 'last | .type + ":" + .outcome' "${out}")" = "end:fixed" ]
ok "print mode with stream-json both ways" bash -c 'grep -qx -- --print "$1" && grep -qx -- --input-format "$1" && grep -qx -- --output-format "$1" && [ "$(grep -cx stream-json "$1")" = 2 ]' _ "${CALLS}"
ok "bypassPermissions as root" bash -c 'grep -qx bypassPermissions "$1" && grep -qx IS_SANDBOX=1 "$1"' _ "${CALLS}"
ok "question and plan tools disallowed" bash -c 'grep -A3 -x -- --disallowedTools "$1" | tr "\n" " " | grep -q "AskUserQuestion EnterPlanMode ExitPlanMode"' _ "${CALLS}"
ok "the prompt holds the rules and the job part" bash -c 'head -n1 "$1" | jq -er ".message.content" | grep -q "REPAIR-STATUS: fixed" && head -n1 "$1" | jq -r .message.content | grep -q "Step X failed"' _ "${SEEN}"
ok "the reply reached the agent verbatim as a user message" bash -c 'sed -n 2p "$1" | jq -er ".message.content" | grep -qxF "Take option A; it is \"safe\" & \$(touch /tmp/pwned)"' _ "${SEEN}"
ok "the pid file is gone after the end" [ ! -e "${CONSTRUCT_REPAIR_RUN_DIR}/sess1.pid" ]

echo "Order: Claude Code first, then AI_TOOLS"
run "$(only codex opencode)" 3
ok "opencode (listed first in AI_TOOLS) without claude" [ "$(count '.type=="start" and .agent=="opencode"')" = 1 ]

echo "Codex"
run "$(only codex)" 3
ok "every stdout line is JSON" all_json
ok "start names codex" [ "$(count '.type=="start" and .agent=="codex"')" = 1 ]
ok "a tool line per command" [ "$(count '.type=="tool" and .text=="$ /bin/bash -lc ls"')" = 2 ]
ok "question, then fixed" [ "$(jq -sr '[.[] | select(.type=="turn") | .outcome] | join(",")' "${out}")" = "question,fixed" ]
ok "ends fixed" [ "$(jq -sr 'last | .outcome' "${out}")" = fixed ]
ok "first run: exec - with no question or plan tool, no approvals" bash -c '
  first="$(sed -n "1,/^---$/p" "$1")"
  grep -qx exec <<<"${first}" && ! grep -qx resume <<<"${first}" && grep -qx -- - <<<"${first}" &&
  grep -qx tools.experimental_request_user_input.enabled=false <<<"${first}" && grep -qx tools.update_plan.enabled=false <<<"${first}" &&
  grep -qx -- --dangerously-bypass-approvals-and-sandbox <<<"${first}" && grep -qx -- --json <<<"${first}"' _ "${CALLS}"
ok "the reply resumes the same thread" bash -c 'sed -n "/^---$/,\$p" "$1" | tr "\n" " " | grep -q "exec resume .* t-123 - "' _ "${CALLS}"
ok "the reply reached codex on stdin, not argv" bash -c 'grep -qF "Take option A" "$1" && ! grep -qF "Take option A" "$2"' _ "${SEEN}" "${CALLS}"

echo "OpenCode"
run "$(only opencode)" 3
ok "every stdout line is JSON" all_json
ok "question, then fixed" [ "$(jq -sr '[.[] | select(.type=="turn") | .outcome] | join(",")' "${out}")" = "question,fixed" ]
ok "question and plan tools denied, permissions auto" bash -c 'grep -q "\"question\":\"deny\"" "$1" && grep -q "\"plan_enter\":\"deny\"" "$1" && grep -qx -- --auto "$1"' _ "${CALLS}"
ok "the reply continues the session" bash -c 'sed -n "/^---$/,\$p" "$1" | tr "\n" " " | grep -q -- "--session ses_1"' _ "${CALLS}"
ok "the reply reached opencode on stdin, not argv" bash -c 'grep -qF "Take option A" "$1" && ! grep -qF "Take option A" "$2"' _ "${SEEN}" "${CALLS}"

echo "T3 Code is not used"
T3_ACTIVE=1 run "$(only t3 claude)" 3
ok "claude runs directly although T3 Code runs" [ "$(count '.type=="start" and .agent=="claude" and .via=="direct"')" = 1 ]
ok "no thread was started" bash -c '! grep -q "^thread" "$1" 2>/dev/null' _ "${T3CALLS}"

echo "The end of stdin ends the session"
rm -f "${CALLS}" "${SEEN}" "${CALLS}.pid"
( sleep 2 ) | CLAUDE_HANG=1 PATH="$(only claude):/usr/bin:/bin" timeout 30 bash "${SCRIPT}" --session sess2 --prompt-b64 "${prompt_b64}" >"${out}" 2>/dev/null
ok "ends stopped" [ "$(jq -sr 'last | .type + ":" + .outcome' "${out}")" = "end:stopped" ]
sleep 0.5
ok "the agent is gone" bash -c '! kill -0 "$(cat "$1")" 2>/dev/null' _ "${CALLS}.pid"
ok "the pid file is gone" [ ! -e "${CONSTRUCT_REPAIR_RUN_DIR}/sess2.pid" ]

echo "A process that ignores TERM is killed too"
rm -f "${CALLS}" "${SEEN}" "${CALLS}.pid" "${CALLS}.stubborn"
( sleep 2 ) | CLAUDE_STUBBORN=1 CLAUDE_HANG=1 PATH="$(only claude):/usr/bin:/bin" timeout 30 bash "${SCRIPT}" --session sess5 --prompt-b64 "${prompt_b64}" >"${out}" 2>/dev/null
# The reader waits up to five seconds for TERM before it sends KILL.
for _ in $(seq 80); do [ -e "${CONSTRUCT_REPAIR_RUN_DIR}/sess5.pid" ] || break; sleep 0.1; done
ok "ends stopped" [ "$(jq -sr 'last | .outcome' "${out}")" = stopped ]
ok "the TERM-ignoring child is gone" bash -c '[ -s "$1" ] && ! kill -0 "$(cat "$1")" 2>/dev/null' _ "${CALLS}.stubborn"
ok "the pid file is gone" [ ! -e "${CONSTRUCT_REPAIR_RUN_DIR}/sess5.pid" ]

echo "--stop ends a running session"
rm -f "${CALLS}" "${SEEN}" "${CALLS}.pid"
mkfifo "${tmp}/hold"
CLAUDE_HANG=1 PATH="$(only claude):/usr/bin:/bin" bash "${SCRIPT}" --session sess3 --prompt-b64 "${prompt_b64}" <"${tmp}/hold" >"${out}" 2>/dev/null &
runner=$!
exec 9>"${tmp}/hold"
for _ in $(seq 50); do [ -s "${CALLS}.pid" ] && break; sleep 0.1; done
ok "the session wrote its pid file" [ -s "${CONSTRUCT_REPAIR_RUN_DIR}/sess3.pid" ]
bash "${SCRIPT}" --stop --session sess3
wait "${runner}" 2>/dev/null
exec 9>&-
ok "the agent is gone" bash -c '! kill -0 "$(cat "$1")" 2>/dev/null' _ "${CALLS}.pid"
ok "the pid file is gone" [ ! -e "${CONSTRUCT_REPAIR_RUN_DIR}/sess3.pid" ]

echo "A new session stops one left over"
rm -f "${CALLS}" "${SEEN}" "${CALLS}.pid"
CLAUDE_HANG=1 PATH="$(only claude):/usr/bin:/bin" bash "${SCRIPT}" --session old --prompt-b64 "${prompt_b64}" < <(sleep 30) >/dev/null 2>&1 &
for _ in $(seq 50); do [ -s "${CALLS}.pid" ] && break; sleep 0.1; done
leftover="$(cat "${CALLS}.pid")"
run "$(only claude)" 3
ok "the left-over agent is gone" bash -c '! kill -0 "$1" 2>/dev/null' _ "${leftover}"
ok "the new one ran" [ "$(jq -sr 'last | .outcome' "${out}")" = fixed ]

echo "Errors"
rm -f "${CALLS}"
PATH="$(only):/usr/bin:/bin" bash "${SCRIPT}" --session sess4 --prompt-b64 "${prompt_b64}" </dev/null >"${out}" 2>/dev/null
ok "no agent: exit 127 with an end line" [ "$?" = 127 ]
ok "no agent: says so" [ "$(count '.type=="end" and .outcome=="error"')" = 1 ]
bash "${SCRIPT}" --session 'bad id;x' --prompt-b64 "${prompt_b64}" </dev/null >/dev/null 2>&1
ok "a session id with shell characters is refused" [ "$?" = 2 ]
bash "${SCRIPT}" --session ok --prompt-b64 '!!!' </dev/null >"${out}" 2>/dev/null
ok "bad base64 is refused" [ "$?" = 2 ]

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[ "${fail}" = 0 ]
