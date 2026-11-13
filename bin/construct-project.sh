#!/usr/bin/env bash
# construct-project.sh — project profiles through the host service
# (docs/plans/construct-platform-design.md 9.3, 9.6; docs/config-sync.md, "The new stack").
#
# `construct project` runs this on a VM that has a host service:
#
#   set <name> --file <path>       write a profile on the revision this VM last saw
#   delete <name>                  delete it (the only way a profile is deleted)
#   conflicts [--fetch] [--start]  list the open conflicts this VM's edits are part of;
#                                  --fetch also writes each to .state/conflicts/<id>.json,
#                                  --start (the heartbeat's) starts an agent on new ones
#   resolve <conflict-id> --file <path>
#                                  send the resolved profile; it is written and the
#                                  conflict closed in one step
#
# The revision this VM last saw of each profile is in ${PROJECTS_STORE}/.state/<name>.rev.
#
# Exit codes: 0 done (also "queued": the host's store was not reachable and the
# edit waits there), 1 error, 2 invalid profile, 6 the edit is in conflict
# (see `construct project conflicts --fetch`), 8 the host service could not be
# reached (the caller falls back to the file and the deletion marker).
set -uo pipefail

PROJECTS_STORE="${PROJECTS_STORE:-/opt/construct/projects}"
CONSTRUCT_REPO_DIR="${CONSTRUCT_REPO_DIR:-/opt/construct/repo}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/construct-api.sh
source "${SCRIPT_DIR}/lib/construct-api.sh"

EXIT_INVALID=2
EXIT_CONFLICT=6
EXIT_UNREACHABLE=8

T3_CMD="${CONSTRUCT_PROJECT_T3:-t3}"
SYSTEMD_RUN="${CONSTRUCT_PROJECT_SYSTEMD_RUN:-systemd-run}"

die() { printf 'construct project: %s\n' "$*" >&2; exit 1; }

command -v jq >/dev/null 2>&1 || die "jq is required"
api_available || exit "${EXIT_UNREACHABLE}"
api_begin || exit "${EXIT_UNREACHABLE}"
trap api_end EXIT

vm_path() { printf '/api/v1/vms/%s' "$(urlencode "${INSTANCE_NAME}")"; }

rev_of() {
  local f="${PROJECTS_STORE}/.state/$1.rev"
  [[ -f "${f}" ]] && head -n 1 "${f}" | tr -dc '0-9'
}

# The host service did not answer, or answered that it cannot take part yet:
# the caller falls back to today's file and marker.
unreachable() {
  case "${API_STATUS}" in
    000|5[0-9][0-9]) return 0 ;;
    409) [[ "$(jq -r '.code // ""' "$1" 2>/dev/null)" == not-participating ]] ;;
    *) return 1 ;;
  esac
}

refused() {
  local text
  text="$(api_problem "$1")"
  printf 'construct project: the host service refused it (HTTP %s)%s\n' "${API_STATUS}" "${text:+: ${text}}" >&2
  [[ "${API_STATUS}" == 400 ]] && exit "${EXIT_INVALID}"
  exit 1
}

# Writes what an answer says into the store: the file (or its removal) and the
# revision, and clears a deletion marker.
apply() {
  local name="$1" answer="$2" revision tmp
  mkdir -p "${PROJECTS_STORE}/.state"
  if [[ "$(jq -r '.file == null' "${answer}")" == true ]]; then
    rm -f -- "${PROJECTS_STORE}/${name}.json"
  else
    tmp="${PROJECTS_STORE}/.${name}.json.tmp"
    jq -j '.file' "${answer}" >"${tmp}" && mv -f -- "${tmp}" "${PROJECTS_STORE}/${name}.json"
  fi
  revision="$(jq -r '.revision // empty' "${answer}")"
  if [[ -n "${revision}" ]]; then printf '%s\n' "${revision}" >"${PROJECTS_STORE}/.state/${name}.rev"; fi
  rm -f -- "${PROJECTS_STORE}/.deleted/${name}"
}

conflict_notice() {
  local answer="$1"
  printf 'construct project: %s is in conflict with a change made elsewhere (fields: %s).\n' \
    "$2" "$(jq -r '(.fields // []) | join(", ")' "${answer}")" >&2
  printf 'Conflict %s holds your edit; see `construct project conflicts --fetch`, then `construct project resolve`.\n' \
    "$(jq -r '.conflictId' "${answer}")" >&2
  exit "${EXIT_CONFLICT}"
}

read_json() { # <path> -> the JSON object, or exit 2
  jq -e 'type == "object"' "$1" >/dev/null 2>&1 || { printf 'construct project: %s is not a JSON object\n' "$1" >&2; exit "${EXIT_INVALID}"; }
}

cmd_set() {
  local name="$1" file="$2" rev body="${API_WORK}/body" answer="${API_WORK}/answer"
  read_json "${file}"
  rev="$(rev_of "${name}")"
  jq -c --arg rev "${rev}" '{profile: ., baseRevision: (if $rev == "" then null else ($rev | tonumber) end)}' "${file}" >"${body}"
  api PUT "$(vm_path)/profiles/$(urlencode "${name}")" "${body}" "${answer}"
  if unreachable "${answer}"; then exit "${EXIT_UNREACHABLE}"; fi
  [[ "${API_STATUS}" == 200 ]] || refused "${answer}"
  case "$(jq -r '.status' "${answer}")" in
    written) apply "${name}" "${answer}"; printf 'Saved %s (revision %s).\n' "${name}" "$(jq -r '.revision' "${answer}")" ;;
    queued) apply "${name}" "${answer}"; printf 'Saved %s; the host sends it on when its store is reachable.\n' "${name}" ;;
    conflict) conflict_notice "${answer}" "${name}" ;;
    *) die "unexpected answer from the host service" ;;
  esac
}

cmd_delete() {
  local name="$1" rev query="" answer="${API_WORK}/answer"
  rev="$(rev_of "${name}")"
  [[ -n "${rev}" ]] && query="?baseRevision=${rev}"
  api DELETE "$(vm_path)/profiles/$(urlencode "${name}")${query}" "" "${answer}"
  if unreachable "${answer}"; then exit "${EXIT_UNREACHABLE}"; fi
  [[ "${API_STATUS}" == 200 ]] || refused "${answer}"
  case "$(jq -r '.status' "${answer}")" in
    written) apply "${name}" "${answer}"; printf 'Deleted %s.\n' "${name}" ;;
    queued) apply "${name}" "${answer}"; printf 'Deleted %s; the host sends the deletion on when its store is reachable.\n' "${name}" ;;
    conflict) conflict_notice "${answer}" "${name}" ;;
    *) die "unexpected answer from the host service" ;;
  esac
}

# The prompt the agent starts with (config/profile-conflict-prompt.md), filled in.
conflict_prompt() {
  local id="$1" profile="$2" fields="$3" path="$4" template=""
  for template in "${CONSTRUCT_REPO_DIR}/config/profile-conflict-prompt.md" "${SCRIPT_DIR}/../config/profile-conflict-prompt.md"; do
    [[ -f "${template}" ]] && break
  done
  [[ -f "${template}" ]] || return 1
  ID="${id}" PROFILE="${profile}" FIELDS="${fields}" CONFLICT_FILE="${path}" \
    perl -pe 's/\{\{CONFLICT_ID\}\}/$ENV{ID}/g; s/\{\{PROFILE\}\}/$ENV{PROFILE}/g; s/\{\{FIELDS\}\}/$ENV{FIELDS}/g; s/\{\{CONFLICT_FILE\}\}/$ENV{CONFLICT_FILE}/g' "${template}"
}

# A T3 Code thread in the profile's project when T3 Code is installed, else the
# installed agent on its own, with the permissions Construct gives it.
start_agent() {
  local profile="$1" prompt="$2"
  if command -v "${T3_CMD}" >/dev/null 2>&1 && printf '%s' "${prompt}" | "${T3_CMD}" thread start "${profile}" - >/dev/null 2>&1; then
    printf 'Started a T3 Code thread for %s.\n' "${profile}"
    return 0
  fi
  local -a cmd
  if command -v claude >/dev/null 2>&1; then cmd=(env IS_SANDBOX=1 claude -p --permission-mode bypassPermissions "${prompt}")
  elif command -v codex >/dev/null 2>&1; then cmd=(codex exec --dangerously-bypass-approvals-and-sandbox "${prompt}")
  elif command -v opencode >/dev/null 2>&1; then cmd=(opencode run "${prompt}")
  else
    printf 'construct project: no agent is installed to resolve the conflict in %s.\n' "${profile}" >&2
    return 1
  fi
  (
    cd "${WORKSPACE_ROOT:-/root/repos}" 2>/dev/null || cd "${HOME}" 2>/dev/null || cd /
    if [[ -n "${INVOCATION_ID:-}" ]] && command -v "${SYSTEMD_RUN}" >/dev/null 2>&1; then
      "${SYSTEMD_RUN}" --quiet --no-block --collect --property=SyslogIdentifier=construct-profile-conflict -- "${cmd[@]}" </dev/null >/dev/null 2>&1
    else
      setsid -f "${cmd[@]}" </dev/null >/dev/null 2>&1
    fi
  ) || { printf 'construct project: the agent did not start\n' >&2; return 1; }
  printf 'Started an agent on the conflict in %s.\n' "${profile}"
}

cmd_conflicts() {
  local fetch=false start=false answer="${API_WORK}/answer" dir="${PROJECTS_STORE}/.state/conflicts"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --fetch) fetch=true ;;
      --start) fetch=true; start=true ;;
      *) die "unknown option: $1" ;;
    esac
    shift
  done
  api GET "$(vm_path)/profile-conflicts" "" "${answer}"
  if unreachable "${answer}"; then printf 'construct project: the host service could not be reached\n' >&2; exit "${EXIT_UNREACHABLE}"; fi
  [[ "${API_STATUS}" == 200 ]] || refused "${answer}"

  local count i id profile fields path prompt
  count="$(jq 'length' "${answer}")"
  if [[ "${count}" == 0 ]]; then printf 'No open profile conflicts.\n'; fi
  if [[ "${fetch}" == true ]]; then
    mkdir -p "${dir}"
    # Conflicts closed meanwhile leave no file behind.
    for path in "${dir}"/*.json; do
      [[ -f "${path}" ]] || continue
      id="$(basename "${path}" .json)"
      jq -e --arg id "conflict:${id}" 'any(.[]; .id == $id)' "${answer}" >/dev/null || rm -f -- "${path}" "${dir}/${id}.started"
    done
  fi
  for ((i = 0; i < count; i++)); do
    id="$(jq -r ".[${i}].id" "${answer}")"
    profile="$(jq -r ".[${i}].profile" "${answer}")"
    fields="$(jq -r ".[${i}].fields | join(\", \")" "${answer}")"
    [[ "${id}" =~ ^conflict:[0-9a-f]+$ ]] || continue
    path="${dir}/${id#conflict:}.json"
    printf '%s  %s  fields: %s\n' "${id}" "${profile}" "${fields}"
    [[ "${fetch}" == true ]] || continue
    jq ".[${i}]" "${answer}" >"${path}"
    printf '  base, ours, theirs and the merge of every other field: %s\n' "${path}"
    if [[ "${start}" == true && ! -e "${dir}/${id#conflict:}.started" ]]; then
      prompt="$(conflict_prompt "${id}" "${profile}" "${fields}" "${path}")" || { printf 'construct project: the conflict prompt is missing\n' >&2; continue; }
      : >"${dir}/${id#conflict:}.started"
      start_agent "${profile}" "${prompt}" || true
    fi
  done
}

cmd_resolve() {
  local id="$1" file="$2" name body="${API_WORK}/body" answer="${API_WORK}/answer"
  read_json "${file}"
  name="$(jq -r '.name // ""' "${file}")"
  [[ -n "${name}" ]] || { printf 'construct project: the resolved profile has no name\n' >&2; exit "${EXIT_INVALID}"; }
  jq -c '{profile: .}' "${file}" >"${body}"
  api POST "$(vm_path)/profile-conflicts/$(urlencode "${id}")/resolve" "${body}" "${answer}"
  if unreachable "${answer}"; then printf 'construct project: the host service could not be reached; resolve again later\n' >&2; exit "${EXIT_UNREACHABLE}"; fi
  [[ "${API_STATUS}" == 200 ]] || refused "${answer}"
  case "$(jq -r '.status' "${answer}")" in
    written)
      apply "${name}" "${answer}"
      rm -f -- "${PROJECTS_STORE}/.state/conflicts/${id#conflict:}.json"
      printf 'Resolved %s: %s is at revision %s.\n' "${id}" "${name}" "$(jq -r '.revision' "${answer}")" ;;
    closed)
      rm -f -- "${PROJECTS_STORE}/.state/conflicts/${id#conflict:}.json"
      printf 'Conflict %s was already resolved elsewhere; nothing was written.\n' "${id}" ;;
    recomputed)
      printf 'The profile changed meanwhile; your resolution conflicts with that change too: conflict %s.\n' "$(jq -r '.conflictId' "${answer}")" >&2
      exit "${EXIT_CONFLICT}" ;;
    *) die "unexpected answer from the host service" ;;
  esac
}

file_arg() { # <args...> -> the --file value
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --file) [[ $# -ge 2 ]] || die "--file requires a path"; printf '%s' "$2"; return 0 ;;
    esac
    shift
  done
  die "--file <path> is required"
}

[[ $# -ge 1 ]] || die "usage: construct-project.sh set|delete|conflicts|resolve ..."
command="$1"; shift
case "${command}" in
  set) [[ $# -ge 1 ]] || die "usage: set <name> --file <path>"; name="$1"; shift; cmd_set "${name}" "$(file_arg "$@")" ;;
  delete) [[ $# -eq 1 ]] || die "usage: delete <name>"; cmd_delete "$1" ;;
  conflicts) cmd_conflicts "$@" ;;
  resolve) [[ $# -ge 1 ]] || die "usage: resolve <conflict-id> --file <path>"; id="$1"; shift; cmd_resolve "${id}" "$(file_arg "$@")" ;;
  *) die "unknown command: ${command}" ;;
esac
