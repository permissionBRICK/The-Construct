#!/usr/bin/env bash
# construct-repair-agent.sh -- the repair agent without a terminal (design 11.5).
#
# The Construct engine starts it over SSH, as root, when a provision, reinstall or restore
# job fails. It runs an AI coding agent on the fix prompt and reports what the agent says
# and does as JSON lines on stdout; each line on stdin is a reply from the user, which
# continues the same conversation. bin/construct-agent-fix.sh stays the interactive
# counterpart for the old client's console.
#
#   bash /opt/construct/repo/bin/construct-repair-agent.sh --session ID --prompt-b64 B64 \
#     [--agent claude|codex|opencode] [--via direct|t3] [--kind provision|reinstall|restore]
#   bash /opt/construct/repo/bin/construct-repair-agent.sh --stop --session ID
#
# --via t3 (the engine's default) starts the repair as a T3 Code thread in the VM (`t3 thread
# start` in the project "Construct repair", folder /var/lib/construct/repair), so the user
# follows and answers it in T3 Code; the script then follows the thread's runs with `t3 thread
# show` and reports them as below. A reply on stdin goes to the thread with `t3 thread send`.
# The t3 CLI cannot stop a thread: ending the session (stdin, --stop) ends the following, and
# the thread itself goes on in T3 Code until the user stops it there. When T3 Code is not
# installed or running, or cannot start the thread, a note says so and the agent runs directly.
#
# The prompt is config/repair-prompt.md followed by the job's part (what failed, which
# logs), which arrives base64-encoded so no shell between the engine and this script
# interprets it. The user's replies never reach a command line: they arrive on stdin
# and go to the agent on its stdin or through a file.
#
# stdin, one per line: {"reply":"text"} (a line that is not JSON is taken as the text).
# End of stdin ends the session: the agent and everything it started are stopped.
#
# stdout, one JSON object per line:
#   {"type":"start","agent":"claude","via":"direct"}       which agent runs, and how
#   {"type":"start","agent":"t3","via":"t3","thread":ID,"environment":ID,"title":...}
#                                      the T3 Code thread (--via t3) and its T3 environment
#   {"type":"note","text":...}         something this script says (a fallback, a hint)
#   {"type":"text","text":...}         what the agent says
#   {"type":"tool","text":...}         one short line per action of the agent
#   {"type":"error","text":...}        the agent's run failed
#   {"type":"turn","outcome":fixed|question|stuck|none}   the agent finished a turn
#   {"type":"run"}                     the T3 Code thread works again after a turn (--via t3)
#   {"type":"end","outcome":fixed|stopped|error,"text":...}   the session is over
#
# The outcome comes from the status line the prompt asks for (REPAIR-STATUS: ...). After a
# turn that is not "fixed" the script waits for a reply; after "fixed" it ends.
#
# Which agent runs directly: --agent, else the first of Claude Code and then the tools
# AI_TOOLS names (/etc/construct/config.env) that is installed.
#
# Exit status: 0 when the agent ended with "fixed", 3 when it ended otherwise, 2 for a
# usage error, 127 when no agent is installed.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROMPT_FILE="${CONSTRUCT_REPAIR_PROMPT_FILE:-${REPO_DIR}/config/repair-prompt.md}"
CONFIG_FILE="${CONSTRUCT_CONFIG_FILE:-/etc/construct/config.env}"
RUN_DIR="${CONSTRUCT_REPAIR_RUN_DIR:-/run/construct/repair}"

# The tools that would let the agent ask through a dialog or switch to a plan mode instead
# of asking in text. Claude Code takes their names; Codex has a switch per tool; OpenCode
# takes permissions.
CLAUDE_DISALLOWED=(AskUserQuestion EnterPlanMode ExitPlanMode)
CODEX_SWITCHES=(-c tools.experimental_request_user_input.enabled=false -c tools.update_plan.enabled=false)
OPENCODE_DENY='{"permission":{"question":"deny","plan_enter":"deny","plan_exit":"deny"}}'

usage() {
  echo "Usage: construct-repair-agent.sh --session ID --prompt-b64 B64 [--agent claude|codex|opencode] | --stop --session ID" >&2
  exit 2
}

session=""
prompt_b64=""
agent=""
via=""
kind=""
stop=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --session) [ "$#" -ge 2 ] || usage; session="$2"; shift 2 ;;
    --prompt-b64) [ "$#" -ge 2 ] || usage; prompt_b64="$2"; shift 2 ;;
    --agent) [ "$#" -ge 2 ] || usage; agent="$2"; shift 2 ;;
    --via) [ "$#" -ge 2 ] || usage; via="$2"; shift 2 ;;
    --kind) [ "$#" -ge 2 ] || usage; kind="$2"; shift 2 ;;
    --stop) stop=1; shift ;;
    *) usage ;;
  esac
done
[[ "${session}" =~ ^[A-Za-z0-9_-]{1,64}$ ]] || usage
case "${agent}" in ""|claude|codex|opencode) ;; *) usage ;; esac
case "${via}" in ""|direct|t3) ;; *) usage ;; esac
[[ -z "${kind}" || "${kind}" =~ ^[a-z]{1,32}$ ]] || usage

# Ends a session's process group. The pid file holds the group's id (the session leader).
stop_session() {
  local file="$1" pgid
  pgid="$(cat "${file}" 2>/dev/null)" || return 0
  rm -f "${file}"
  [[ "${pgid}" =~ ^[0-9]+$ ]] && [ "${pgid}" -gt 1 ] || return 0
  kill -TERM -- "-${pgid}" 2>/dev/null || return 0
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    kill -0 -- "-${pgid}" 2>/dev/null || return 0
    sleep 0.5
  done
  kill -KILL -- "-${pgid}" 2>/dev/null || true
}

if [ "${stop}" = 1 ]; then
  stop_session "${RUN_DIR}/${session}.pid"
  exit 0
fi
[ -n "${prompt_b64}" ] || usage

# The whole session runs in a process group of its own, so ending it reaches every
# process the agent started, whatever the connection does.
if [ -z "${CONSTRUCT_REPAIR_LEADER:-}" ]; then
  CONSTRUCT_REPAIR_LEADER=1 exec setsid -w bash "${BASH_SOURCE[0]}" --session "${session}" --prompt-b64 "${prompt_b64}" \
    ${agent:+--agent "${agent}"} ${via:+--via "${via}"} ${kind:+--kind "${kind}"}
fi

if ! command -v jq >/dev/null 2>&1; then
  printf '%s\n' '{"type":"end","outcome":"error","text":"jq is not installed on this VM."}'
  exit 3
fi

emit() { jq -nc --arg type "$1" --arg text "${2-}" '{type:$type,text:$text}'; }

if ! job_prompt="$(printf '%s' "${prompt_b64}" | base64 -d 2>/dev/null)" || [ -z "${job_prompt}" ]; then
  jq -nc '{type:"end",outcome:"error",text:"The prompt is not valid base64."}'
  exit 2
fi
rules=""
[ -r "${PROMPT_FILE}" ] && rules="$(cat "${PROMPT_FILE}")"
prompt="${rules:+${rules}

}${job_prompt}"

cfg_value() {
  local line
  line="$(grep -E "^$1=" "${CONFIG_FILE}" 2>/dev/null | tail -n 1)" || return 0
  line="${line#*=}"
  line="${line#\'}"
  printf '%s' "${line%\'}"
}

# A repair cut off by an engine restart may still run: only one runs per VM.
mkdir -p "${RUN_DIR}"
chmod 700 "${RUN_DIR}" 2>/dev/null || true
for old in "${RUN_DIR}"/*.pid; do
  [ -e "${old}" ] || continue
  [ "${old}" = "${RUN_DIR}/${session}.pid" ] && continue
  stop_session "${old}"
done
PGID="$$"
printf '%s\n' "${PGID}" >"${RUN_DIR}/${session}.pid"
work="$(mktemp -d)"
keep_pid=0
cleanup() {
  rm -rf "${work}"
  [ "${keep_pid}" = 0 ] && [ "$(cat "${RUN_DIR}/${session}.pid" 2>/dev/null)" = "${PGID}" ] && rm -f "${RUN_DIR}/${session}.pid"
}

# Ends every other process of the session's group: TERM, up to five seconds, then KILL for any
# that stayed; the pid file goes only then, so a --stop until that point still finds the group.
# The group's members other than this process, without forking (a subshell would count).
group_others() {
  local proc pid pgrp rest
  others=()
  for proc in /proc/[0-9]*; do
    pid="${proc#/proc/}"
    [ "${pid}" = "${BASHPID}" ] && continue
    # /proc/PID/stat: pid (comm) state ppid pgrp ...; comm may hold spaces, so cut after ") ".
    IFS= read -r rest <"${proc}/stat" 2>/dev/null || continue
    rest="${rest##*) }"
    read -r _ _ pgrp _ <<<"${rest}"
    [ "${pgrp}" = "${PGID}" ] && others+=("${pid}")
  done
}

end_group() {
  local others=()
  trap '' TERM
  kill -TERM -- "-${PGID}" 2>/dev/null
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    group_others
    [ "${#others[@]}" -gt 0 ] || break
    sleep 0.5
  done
  [ "${#others[@]}" -gt 0 ] && kill -KILL "${others[@]}" 2>/dev/null
  [ "$(cat "${RUN_DIR}/${session}.pid" 2>/dev/null)" = "${PGID}" ] && rm -f "${RUN_DIR}/${session}.pid"
}
trap cleanup EXIT
# Ended by a signal: whoever sent it removes the pid file once the whole group is gone.
trap 'keep_pid=1; exit 143' TERM INT HUP

# Ends the session: the end line, then the whole process group (this shell included).
finish() {
  local outcome="$1" text="${2-}"
  jq -nc --arg outcome "${outcome}" --arg text "${text}" '{type:"end",outcome:$outcome,text:$text}'
  rm -rf "${work}"
  trap - EXIT
  end_group
  if [ "${outcome}" = fixed ]; then exit 0; else exit 3; fi
}

# The text of a stdin line: {"reply":"..."} or the raw line.
reply_text() {
  local text
  if text="$(jq -er '.reply | strings' <<<"$1" 2>/dev/null)"; then printf '%s' "${text}"; else printf '%s' "$1"; fi
}

# The status line's word in the agent's last message.
outcome_of() {
  local word
  word="$(printf '%s\n' "$1" | grep -oiE '^[[:space:]*_`]*REPAIR-STATUS:[[:space:]*_`]*(fixed|question|stuck)' | tail -n 1 |
    grep -oiE '(fixed|question|stuck)$' | tr '[:upper:]' '[:lower:]')"
  printf '%s' "${word:-none}"
}

# What the user sees of a message: without the status line.
strip_status() { printf '%s' "$1" | grep -viE '^[[:space:]*_`]*REPAIR-STATUS:' ; }

# One line per tool use: the tool and its most telling argument, cut to 200 characters.
JQ_SHORT='def short: tostring | gsub("[\\s]+"; " ") | if length > 200 then .[0:200] + "..." else . end;'

# Replies arrive on stdin while the agent works. A reader puts each on the queue (a FIFO)
# or, for Claude Code, straight into the agent's own input; the end of stdin ends all.
exec 5<&0
queue="${work}/queue"
mkfifo "${queue}"
exec 6<>"${queue}"
start_reader() {
  local target="$1"
  (
    while IFS= read -r line <&5 || [ -n "${line}" ]; do
      text="$(reply_text "${line}")"
      [ -n "${text//[[:space:]]/}" ] || continue
      if [ "${target}" = claude ]; then
        jq -nc --arg t "${text}" '{type:"user",message:{role:"user",content:$t}}' >&7 2>/dev/null || break
      elif [ "${target}" = t3 ]; then
        printf '%s' "${text}" | t3 thread send --log-level none "${T3_THREAD}" - >/dev/null 2>&1 ||
          emit note "The reply did not reach the T3 Code thread; answer it in T3 Code."
      else
        jq -nc --arg t "${text}" '$t' >&6
      fi
    done
    jq -nc '{type:"end",outcome:"stopped",text:"The session was ended."}'
    end_group
  ) &
}

# The next reply from the queue (as a JSON string, so it may span lines).
next_reply() {
  local line
  IFS= read -r line <&6 || return 1
  jq -r '.' <<<"${line}"
}

after_turn() {
  local text="$1"
  local outcome
  outcome="$(outcome_of "${text}")"
  jq -nc --arg o "${outcome}" '{type:"turn",outcome:$o}'
  [ "${outcome}" = fixed ] && finish fixed
}

# ---- Claude Code: one process for the whole conversation, stream-json both ways.
run_claude() {
  local filter line ev type text last=""
  jq -nc '{type:"start",agent:"claude",via:"direct"}'
  mkfifo "${work}/claude-in"
  exec 4< <(cd "${WORKSPACE}" && IS_SANDBOX=1 exec claude --print --verbose \
    --input-format stream-json --output-format stream-json \
    --permission-mode bypassPermissions --disallowedTools "${CLAUDE_DISALLOWED[@]}" \
    <"${work}/claude-in" 2>"${work}/claude-err")
  exec 7>"${work}/claude-in"
  jq -nc --arg t "${prompt}" '{type:"user",message:{role:"user",content:$t}}' >&7
  start_reader claude
  filter="${JQ_SHORT}"'
    if .type == "assistant" then
      (.message.content // [])[] |
      if .type == "text" and ((.text // "") | length) > 0 then {type:"text", text:.text}
      elif .type == "tool_use" then
        {type:"tool", text:(.name + ((.input.command // .input.file_path // .input.path // .input.pattern
          // .input.url // .input.description // "") | if . == "" then "" else ": " + short end))}
      else empty end
    elif .type == "result" then {type:"_turn", text:(.result // ""), error:(.is_error // false)}
    else empty end'
  while IFS= read -r line <&4; do
    while IFS= read -r ev; do
      [ -n "${ev}" ] || continue
      type="$(jq -r '.type' <<<"${ev}")"
      text="$(jq -r '.text' <<<"${ev}")"
      case "${type}" in
        text) last="${text}"; emit text "$(strip_status "${text}")" ;;
        _turn)
          [ "$(jq -r '.error' <<<"${ev}")" = true ] && emit error "${text}"
          after_turn "${text:-${last}}"
          last="" ;;
        *) printf '%s\n' "${ev}" ;;
      esac
    done < <(jq -c "${filter}" <<<"${line}" 2>/dev/null)
  done
  finish error "Claude Code ended: $(tail -n 3 "${work}/claude-err" 2>/dev/null | tr '\n' ' ')"
}

# ---- Codex: `codex exec` for the first turn, `codex exec resume THREAD` for each reply.
run_codex() {
  local thread="" message="${prompt}" filter line ev type text last
  jq -nc '{type:"start",agent:"codex",via:"direct"}'
  start_reader queue
  filter="${JQ_SHORT}"'
    if .type == "thread.started" then {type:"_thread", text:.thread_id}
    elif .type == "item.completed" and .item.type == "agent_message" then {type:"text", text:(.item.text // "")}
    elif .type == "item.started" and .item.type == "command_execution" then {type:"tool", text:("$ " + (.item.command | short))}
    elif .type == "item.completed" and .item.type == "file_change" then
      {type:"tool", text:("edit " + ([.item.changes[]?.path] | join(", ") | short))}
    elif .type == "item.started" and .item.type == "mcp_tool_call" then {type:"tool", text:((.item.server // "") + "." + (.item.tool // ""))}
    elif .type == "item.started" and .item.type == "web_search" then {type:"tool", text:("search: " + (.item.query // "" | short))}
    elif .type == "turn.failed" then {type:"error", text:(.error.message // "the turn failed")}
    elif .type == "error" then {type:"error", text:(.message // "error")}
    else empty end'
  while :; do
    local args=(exec)
    [ -n "${thread}" ] && args+=(resume)
    args+=(--json --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox "${CODEX_SWITCHES[@]}")
    [ -n "${thread}" ] && args+=("${thread}")
    args+=(-)
    last=""
    while IFS= read -r line; do
      while IFS= read -r ev; do
        [ -n "${ev}" ] || continue
        type="$(jq -r '.type' <<<"${ev}")"
        text="$(jq -r '.text' <<<"${ev}")"
        case "${type}" in
          _thread) thread="${text}" ;;
          text) last="${text}"; emit text "$(strip_status "${text}")" ;;
          *) printf '%s\n' "${ev}" ;;
        esac
      done < <(jq -c "${filter}" <<<"${line}" 2>/dev/null)
    done < <(cd "${WORKSPACE}" && printf '%s' "${message}" | codex "${args[@]}" 2>"${work}/codex-err")
    [ -n "${thread}" ] || finish error "Codex did not start: $(tail -n 3 "${work}/codex-err" 2>/dev/null | tr '\n' ' ')"
    after_turn "${last}"
    message="$(next_reply)" || finish stopped "The session was ended."
  done
}

# ---- OpenCode: `opencode run` for the first turn, `opencode run --session ID` for each reply.
run_opencode() {
  local session_id="" message="${prompt}" filter line ev type text last
  jq -nc '{type:"start",agent:"opencode",via:"direct"}'
  start_reader queue
  filter="${JQ_SHORT}"'
    (if (.sessionID // "") != "" then {type:"_session", text:.sessionID} else empty end),
    (if .type == "text" then {type:"text", text:(.part.text // "")}
     elif .type == "tool_use" then
       {type:"tool", text:((.part.tool // "tool") + ((.part.state.input.command // .part.state.input.path // "")
         | if . == "" then "" else ": " + short end))}
     elif .type == "error" then {type:"error", text:(.error.message // .error.name // "error")}
     else empty end)'
  while :; do
    local args=(run --format json --auto)
    [ -n "${session_id}" ] && args+=(--session "${session_id}")
    last=""
    while IFS= read -r line; do
      while IFS= read -r ev; do
        [ -n "${ev}" ] || continue
        type="$(jq -r '.type' <<<"${ev}")"
        text="$(jq -r '.text' <<<"${ev}")"
        case "${type}" in
          _session) session_id="${text}" ;;
          text) [ -n "${text}" ] || continue; last="${text}"; emit text "$(strip_status "${text}")" ;;
          *) printf '%s\n' "${ev}" ;;
        esac
      done < <(jq -c "${filter}" <<<"${line}" 2>/dev/null)
    done < <(cd "${WORKSPACE}" && printf '%s' "${message}" |
      OPENCODE_CONFIG_CONTENT="${OPENCODE_DENY}" opencode "${args[@]}" 2>"${work}/opencode-err")
    [ -n "${session_id}" ] || finish error "OpenCode did not start: $(tail -n 3 "${work}/opencode-err" 2>/dev/null | tr '\n' ' ')"
    after_turn "${last}"
    message="$(next_reply)" || finish stopped "The session was ended."
  done
}

# ---- T3 Code (--via t3): the repair as a thread in the VM's T3 Code, followed by its runs.
T3_DIR="${CONSTRUCT_REPAIR_T3_DIR:-/var/lib/construct/repair}"
T3_HOME="${T3CODE_HOME:-${HOME:-/root}/.t3}"
# Seconds between two reads of the thread while it works and while it waits for the user;
# how long it is followed; how many failed reads in a row end the session.
T3_POLL="${CONSTRUCT_REPAIR_T3_POLL:-10}"
T3_IDLE_POLL="${CONSTRUCT_REPAIR_T3_IDLE_POLL:-30}"
T3_WATCH="${CONSTRUCT_REPAIR_T3_WATCH:-86400}"
T3_FAILS="${CONSTRUCT_REPAIR_T3_FAILS:-60}"
T3_THREAD=""

# Starts the thread and says so in the start line; fails with a note when T3 Code cannot take it.
start_t3() {
  local title="Construct repair${kind:+: ${kind} failed}" launched env why
  if ! command -v t3 >/dev/null 2>&1; then
    emit note "T3 Code is not installed on this VM; the repair runs here instead."
    return 1
  fi
  if ! systemctl is-active --quiet t3code-serve 2>/dev/null; then
    emit note "T3 Code is not running on this VM; the repair runs here instead."
    return 1
  fi
  mkdir -p "${T3_DIR}"
  # Once per VM; later runs find the project there (the CLI then says it exists).
  t3 project add "${T3_DIR}" --title "Construct repair" --log-level none </dev/null >/dev/null 2>&1 || true
  if ! launched="$(printf '%s' "${prompt}" | t3 thread start --json --title "${title}" --log-level none "${T3_DIR}" - 2>"${work}/t3-err")" ||
    ! T3_THREAD="$(jq -er '.threadId | strings | select(length > 0)' <<<"${launched}" 2>/dev/null)"; then
    why="$(tail -n 3 "${work}/t3-err" 2>/dev/null | tr '\n' ' ')"
    why="${why%"${why##*[! ]}"}"
    emit note "T3 Code could not start the repair thread${why:+: ${why}}. The repair runs here instead."
    return 1
  fi
  env="$(head -n 1 "${T3_HOME}/userdata/environment-id" 2>/dev/null | tr -d '[:space:]')"
  jq -nc --arg thread "${T3_THREAD}" --arg env "${env}" --arg title "${title}" \
    '{type:"start",agent:"t3",via:"t3",thread:$thread,environment:$env,title:$title}'
}

# Follows the thread until a run ends with "fixed": a turn line when a run ends (with its last
# message) or asks through T3 Code's own question tool, a run line when the thread works again.
follow_t3() {
  local last="" asked="" waiting=0 fails=0 json active latest pending text until=$((SECONDS + T3_WATCH))
  while [ "${SECONDS}" -lt "${until}" ]; do
    if ! json="$(t3 thread show --json --messages 3 --log-level none "${T3_THREAD}" </dev/null 2>"${work}/t3-err")" ||
      ! active="$(jq -er '.thread.activeRunId // ""' <<<"${json}" 2>/dev/null)"; then
      fails=$((fails + 1))
      [ "${fails}" -lt "${T3_FAILS}" ] ||
        finish error "T3 Code stopped answering about the repair thread: $(tail -n 2 "${work}/t3-err" 2>/dev/null | tr '\n' ' ')"
      sleep "${T3_POLL}"
      continue
    fi
    fails=0
    latest="$(jq -r '.thread.latestRunId // ""' <<<"${json}")"
    pending="$(jq -r '.thread.pendingRequestCount // 0' <<<"${json}")"
    if [ -n "${active}" ]; then
      if [ "${pending}" -gt 0 ]; then
        [ "${asked}" = "${active}" ] || { asked="${active}"; waiting=1; jq -nc '{type:"turn",outcome:"question"}'; }
      elif [ "${waiting}" = 1 ]; then
        waiting=0; asked=""
        jq -nc '{type:"run"}'
      fi
      sleep "${T3_POLL}"
      continue
    fi
    if [ -n "${latest}" ] && [ "${latest}" != "${last}" ]; then
      [ "${waiting}" = 1 ] && jq -nc '{type:"run"}'
      last="${latest}"; waiting=1; asked=""
      text="$(jq -r --arg run "${latest}" '[.messages[]? | select(.role == "assistant" and .runId == $run)] | last | .text // ""' <<<"${json}")"
      if [ -n "${text}" ]; then emit text "$(strip_status "${text}")"
      elif [ "$(jq -r '.thread.status' <<<"${json}")" = failed ]; then emit error "The thread's run failed in T3 Code."
      fi
      after_turn "${text}"
    fi
    sleep "${T3_IDLE_POLL}"
  done
  finish stopped "Construct no longer follows the repair thread; it goes on in T3 Code."
}

# ---- Which agent.
pick_agent() {
  local tools tool order=(claude)
  tools="$(cfg_value AI_TOOLS)"
  IFS=',' read -ra listed <<<"${tools:-claude-code,codex,opencode}"
  for tool in "${listed[@]}"; do
    case "${tool// /}" in
      claude-code|claude) ;;
      codex) order+=(codex) ;;
      opencode) order+=(opencode) ;;
    esac
  done
  order+=(codex opencode)
  for tool in "${order[@]}"; do
    command -v "${tool}" >/dev/null 2>&1 && { printf '%s' "${tool}"; return 0; }
  done
  return 1
}

WORKSPACE="${WORKSPACE_ROOT:-$(cfg_value WORKSPACE_ROOT)}"
WORKSPACE="${WORKSPACE:-/root/repos}"
[ -d "${WORKSPACE}" ] || WORKSPACE="${HOME:-/root}"

if [ "${via}" = t3 ] && start_t3; then
  start_reader t3
  follow_t3
fi

if [ -z "${agent}" ] && ! agent="$(pick_agent)"; then
  jq -nc '{type:"end",outcome:"error",text:"No AI coding agent (Claude Code, Codex or OpenCode) is installed on this VM."}'
  exit 127
fi
if ! command -v "${agent}" >/dev/null 2>&1; then
  jq -nc --arg a "${agent}" '{type:"end",outcome:"error",text:($a + " is not installed on this VM.")}'
  exit 127
fi
case "${agent}" in
  claude) run_claude ;;
  codex) run_codex ;;
  opencode) run_opencode ;;
esac
