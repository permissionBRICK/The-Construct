#!/usr/bin/env bash
# construct-agent-fix.sh -- run one unattended AI agent turn on the VM to fix a failed
# provisioning step, and exit when the agent is done.
#
# The Windows host starts it over `ssh -t` from the console that ran the install or
# provisioning (the "Autofix it" choice after a run that reported failed steps), so
# the agent's output appears in that same console:
#
#   bash /opt/construct/repo/bin/construct-agent-fix.sh \
#     --agent claude|codex|opencode [--model-b64 <base64>] --prompt-b64 <base64>
#
# The prompt and the model arrive base64-encoded: base64 has no character any of the
# shells between the host and this script would interpret, so a model name typed by
# the user cannot break the command line.
#
# Model values:
#   (empty)        the agent's configured default
#   latest:astra   Codex only: the newest GPT "astra" model in Codex's model catalog
#   latest:sol     Codex only: the newest GPT "sol" model in Codex's model catalog
#   anything else  passed to the agent as given (Claude aliases such as opus or fable,
#                  a full model id, OpenCode's provider/model)
#
# Each agent runs in its print/exec mode, so it exits once its turn is over:
#   claude -p      progress (its text and one line per tool call) streams through jq;
#                  without jq the final message is all that is shown
#   codex exec     streams its own progress, then prints the final message
#   opencode run   streams its own progress and the final message
#
# Exit status: the agent's own, or 2 for a usage error and 127 when the agent's CLI
# is not installed on this VM.
set -uo pipefail

usage() {
  echo "Usage: construct-agent-fix.sh --agent claude|codex|opencode [--model-b64 <base64>] --prompt-b64 <base64>" >&2
  exit 2
}

agent=""
model_b64=""
prompt_b64=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --agent) [ "$#" -ge 2 ] || usage; agent="$2"; shift 2 ;;
    --model-b64) [ "$#" -ge 2 ] || usage; model_b64="$2"; shift 2 ;;
    --prompt-b64) [ "$#" -ge 2 ] || usage; prompt_b64="$2"; shift 2 ;;
    *) usage ;;
  esac
done

case "${agent}" in claude|codex|opencode) ;; *) usage ;; esac
[ -n "${prompt_b64}" ] || usage

if ! prompt="$(printf '%s' "${prompt_b64}" | base64 -d 2>/dev/null)" || [ -z "${prompt}" ]; then
  echo "construct-agent-fix: the prompt is not valid base64." >&2
  exit 2
fi
model=""
if [ -n "${model_b64}" ] && ! model="$(printf '%s' "${model_b64}" | base64 -d 2>/dev/null)"; then
  echo "construct-agent-fix: the model name is not valid base64." >&2
  exit 2
fi

dim=$'\033[2m'
reset=$'\033[0m'
note() { printf '%s%s%s\n' "${dim}" "$*" "${reset}"; }

# The newest model of one GPT family ("astra", "sol") in Codex's catalog: the listed
# slugs named gpt-<version>-<family>, highest version wins. `codex debug models`
# refreshes the catalog; the cached copy covers a CLI without that command.
codex_latest_model() {
  local family="$1" catalog=""
  command -v jq >/dev/null 2>&1 || return 1
  catalog="$(codex debug models 2>/dev/null)" || catalog=""
  if [ -z "${catalog}" ] && [ -r "${HOME}/.codex/models_cache.json" ]; then
    catalog="$(cat "${HOME}/.codex/models_cache.json")"
  fi
  [ -n "${catalog}" ] || return 1
  printf '%s' "${catalog}" | jq -r --arg fam "${family}" '
    (if type == "object" then .models else . end) // []
    | map(select((.visibility // "list") == "list") | (.slug // empty)
          | select(test("^gpt-[0-9]+(\\.[0-9]+)*-" + $fam + "$")))
    | sort_by(capture("^gpt-(?<v>[0-9.]+)-").v | split(".") | map(tonumber))
    | last // empty' 2>/dev/null | grep . || return 1
}

# One line per Claude stream-json event worth showing: the agent's text as it writes
# it (the last text is its final message), a dim line per tool call, and the reason
# when the run ends in an error. A line that is not JSON is passed through unchanged.
CLAUDE_STREAM_FILTER='
  def clip($n): if length > $n then .[0:$n] + "..." else . end;
  def detail: (.command // .file_path // .path // .pattern // .url // .description // "")
    | tostring | split("\n")[0] | clip(110);
  . as $line | (try fromjson catch null) as $e
  | if ($e | type) != "object" then $line + "\n"
    elif $e.type == "assistant" then
      ($e.message.content // [])[]
      | if .type == "text" then "\n" + .text + "\n"
        elif .type == "tool_use" then
          (.input | detail) as $d
          | "\u001b[2m  > " + .name + (if $d == "" then "" else ": " + $d end) + "\u001b[0m\n"
        else empty end
    elif $e.type == "result" and (($e.is_error // false) or (($e.subtype // "success") != "success")) then
      # A turn that ended in an error message already showed it as its text.
      if ($e.subtype // "success") == "success" then "\nClaude stopped with an error.\n"
      else "\nClaude stopped (" + $e.subtype + ")"
        + (if (($e.result // "") | tostring) == "" then "" else ": " + ($e.result | tostring) end) + "\n"
      end
    else empty end'

cd "${HOME}" 2>/dev/null || cd /

case "${agent}" in
  claude)
    if ! command -v claude >/dev/null 2>&1; then
      echo "construct-agent-fix: Claude Code (claude) is not installed on this VM." >&2
      exit 127
    fi
    # Bypass mode on purpose: the turn runs unattended, and print mode would refuse
    # every tool call that needs a permission. IS_SANDBOX lets Claude Code accept
    # bypass mode as root; Construct sets both in Claude's settings as well, but the
    # failed step may be the one that writes them.
    args=(-p --permission-mode bypassPermissions)
    [ -n "${model}" ] && args+=(--model "${model}")
    if command -v jq >/dev/null 2>&1; then
      IS_SANDBOX=1 claude "${args[@]}" --verbose --output-format stream-json "${prompt}" \
        | jq -R -r -j --unbuffered "${CLAUDE_STREAM_FILTER}"
      status="${PIPESTATUS[0]}"
      exit "${status}"
    fi
    note "(jq is not installed: Claude's progress is hidden, its final message follows when it is done)"
    IS_SANDBOX=1 exec claude "${args[@]}" "${prompt}"
    ;;
  codex)
    if ! command -v codex >/dev/null 2>&1; then
      echo "construct-agent-fix: Codex (codex) is not installed on this VM." >&2
      exit 127
    fi
    case "${model}" in
      latest:*)
        family="${model#latest:}"
        if resolved="$(codex_latest_model "${family}")"; then
          note "Latest ${family} model: ${resolved}"
          model="${resolved}"
        else
          note "No ${family} model found in Codex's model catalog; using Codex's default model."
          model=""
        fi
        ;;
    esac
    # The turn runs unattended; Construct configures Codex the same way
    # (approval_policy never, sandbox danger-full-access). The fix may need to work
    # outside any git repository, so the repository check is skipped.
    args=(exec --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox)
    [ -n "${model}" ] && args+=(--model "${model}")
    exec codex "${args[@]}" "${prompt}"
    ;;
  opencode)
    if ! command -v opencode >/dev/null 2>&1; then
      echo "construct-agent-fix: OpenCode (opencode) is not installed on this VM." >&2
      exit 127
    fi
    # Construct configures OpenCode with "permission": "allow", so the run does not stop
    # for approvals.
    args=(run)
    [ -n "${model}" ] && args+=(--model "${model}")
    exec opencode "${args[@]}" "${prompt}"
    ;;
esac
