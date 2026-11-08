#!/usr/bin/env bash
# construct-agent-fix.sh -- open an interactive AI agent session on the VM whose first
# message is the fix prompt for a failed provisioning step.
#
# The Windows host starts it over `ssh -t` from the console that ran the install or
# provisioning (the "Autofix it" choice after a run that reported failed steps). The
# agent's own terminal UI runs in that console: it starts working on the prompt right
# away, and the user can follow it, answer it and keep talking to it. Quitting the
# agent ends the session and returns to the console.
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
# Exit status: the agent's own, or 2 for a usage error or a missing terminal, and 127
# when the agent's CLI is not installed on this VM.
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

need_cli() {
  command -v "$1" >/dev/null 2>&1 && return 0
  echo "construct-agent-fix: $2 ($1) is not installed on this VM." >&2
  exit 127
}

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

# Each agent runs its interactive UI, which needs a terminal on both ends.
if [ ! -t 0 ] || [ ! -t 1 ]; then
  echo "construct-agent-fix: the agent session needs a terminal; connect with ssh -t." >&2
  exit 2
fi

# The workspace root: Construct marks it trusted for Claude Code and Codex, so neither
# opens with a folder-trust question (Claude's preselects "No, exit").
cd "${WORKSPACE_ROOT:-/root/repos}" 2>/dev/null || cd "${HOME}" 2>/dev/null || cd /

# Each agent gets the same permission mode Construct configures for it, set on the
# command line as well because the failed step may be the one that writes that config.
case "${agent}" in
  claude)
    need_cli claude "Claude Code"
    # IS_SANDBOX lets Claude Code accept bypass mode as root.
    args=(--permission-mode bypassPermissions)
    [ -n "${model}" ] && args+=(--model "${model}")
    IS_SANDBOX=1 exec claude "${args[@]}" "${prompt}"
    ;;
  codex)
    need_cli codex "Codex"
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
    # Construct configures Codex with approval_policy never and danger-full-access.
    args=(--dangerously-bypass-approvals-and-sandbox)
    [ -n "${model}" ] && args+=(--model "${model}")
    exec codex "${args[@]}" "${prompt}"
    ;;
  opencode)
    need_cli opencode "OpenCode"
    # Construct configures OpenCode with "permission": "allow".
    args=()
    [ -n "${model}" ] && args+=(--model "${model}")
    exec opencode "${args[@]}" --prompt "${prompt}"
    ;;
esac
