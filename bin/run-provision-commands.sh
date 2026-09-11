#!/usr/bin/env bash
#
# Run the custom provisioning commands declared by the selected project profiles.
#
# Reads the merged `.provisionCommands` array from runtime/generated.json
# (produced by generate-runtime-config.sh) and runs project groups in parallel,
# keeping each profile's commands in array order via `bash -c` as root. They run
# AFTER the project repos are checked out, with
# the working directory set to the profile's first repo checkout
# (WORKSPACE_ROOT/<dir>); profiles that declare no repo -- or whose repo isn't on
# disk yet -- run from WORKSPACE_ROOT. These run on EVERY provision -- they are
# the project's "every-time" setup hook -- so the commands must be safe to re-run
# (idempotent). config.env is sourced and the merged AGENT_* vars are derived
# from generated.json first, so commands can reference WORKSPACE_ROOT,
# AGENT_PROJECTS, AGENT_REPOS_JSON, etc.
#
# A failing command is reported but does NOT abort the rest: the remaining
# commands still run, and the script exits non-zero at the end so the caller
# (provision.sh) can warn without failing the whole provision -- matching how
# checkout-projects.sh / configure-mcp.sh behave.
#
# Idempotency is the project's responsibility. Run as root.
#
set -euo pipefail

# Colourised logging helpers. Emit ANSI colour when either stream is a terminal
# or the caller forces it (the SSH provisioning stream sets FORCE_COLOR/
# CLICOLOR_FORCE, which child processes inherit); otherwise stay plain so
# redirected/piped logs aren't littered with escape codes.
if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_STEP=$'\033[1;36m'   # bold cyan - step headers
  _C_OK=$'\033[32m'       # green     - completion / success
  _C_WARN=$'\033[33m'     # yellow    - warnings (run continues)
  _C_ERR=$'\033[31m'      # red       - fatal errors (before exit)
  _C_DIM=$'\033[2m'       # dim       - idempotent "nothing to do" / detail
  _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_ERR=''; _C_DIM=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n'     "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n'     "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n'     "${_C_ERR}"  "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n'     "${_C_DIM}"  "$*" "${_C_RESET}"; }

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
AGENT_HOME="${AGENT_HOME:-/opt/construct}"

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash ${AGENT_HOME}/repo/bin/run-provision-commands.sh"
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  err "jq is required (installed by bootstrap.sh)."
  exit 1
fi

# Expose the saved config (WORKSPACE_ROOT, AGENT_HOME, ...) to this script and to
# the commands themselves. config.env is a genuine shell env file (written by
# config-set.sh), so sourcing it is safe -- the same thing the other bin scripts
# do. We deliberately do NOT source runtime/generated.env here: it is a
# docker-compose / systemd EnvironmentFile whose JSON values are stored unquoted
# (AGENT_REPOS_JSON=[{"url":...}]), which Bash `source` would mangle. The merged
# AGENT_* vars are instead derived from generated.json below, keeping JSON intact.
if [[ -f "${CONFIG_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  . "${CONFIG_FILE}"
  set +a
fi

REPO_DIR="${REPO_DIR:-${AGENT_HOME}/repo}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
GENERATED_JSON="${GENERATED_JSON:-${AGENT_HOME}/runtime/generated.json}"

# Generate the runtime config on demand so this is also runnable standalone.
if [[ ! -f "${GENERATED_JSON}" ]]; then
  if [[ -x "${REPO_DIR}/bin/generate-runtime-config.sh" ]]; then
    "${REPO_DIR}/bin/generate-runtime-config.sh"
  else
    note "No ${GENERATED_JSON} and no generator to build it; nothing to run."
    exit 0
  fi
fi

# Make the merged AGENT_* vars available to the commands, read straight from
# generated.json (NOT the dotenv file) so JSON-valued vars stay valid JSON. These
# mirror what generate-runtime-config.sh writes to generated.env.
AGENT_PROJECTS="$(jq -r '.projects | join(",")' "${GENERATED_JSON}")"
AGENT_REPOS_JSON="$(jq -c '.repos' "${GENERATED_JSON}")"
AGENT_SDKS_JSON="$(jq -c '.sdks' "${GENERATED_JSON}")"
export AGENT_PROJECTS AGENT_REPOS_JSON AGENT_SDKS_JSON

# Snapshot and validate the complete plan before starting workers. Legacy runtime
# configs without profile identity stay in one sequential group until regenerated.
provision_jobs="${PROVISION_JOBS:-0}"
if [[ ! "${provision_jobs}" =~ ^(0|[1-9][0-9]{0,5})$ ]]; then
  err "PROVISION_JOBS must be an integer from 0 to 999999 (0 = all)"
  exit 1
fi
commands_tmp="$(mktemp -d)"
declare -A active=() active_targets=() active_storage=()
cleanup() {
  local pid
  for pid in "${!active[@]}"; do
    kill -TERM -- "-${pid}" 2>/dev/null || kill -TERM "${pid}" 2>/dev/null || true
  done
  for pid in "${!active[@]}"; do wait "${pid}" 2>/dev/null || true; done
  rm -r -- "${commands_tmp}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
jq '
  (.provisionCommands // [])
  | to_entries
  | map(.value + {index: (.key + 1)})
  | group_by(if .profile then ["profile", .profile] else ["legacy"] end)
  | sort_by(.[0].index)
  | map({name: (.[0].profile // "legacy commands"),
         dir: (if .[0].profile then (.[0].dir // "") else "" end),
         commands: .})
' "${GENERATED_JSON}" >"${commands_tmp}/groups.json"
group_count="$(jq 'length' "${commands_tmp}/groups.json")"
count="$(jq '[.[].commands | length] | add // 0' "${commands_tmp}/groups.json")"
if (( count == 0 )); then
  note "No provisioning commands declared by the selected projects."
  exit 0
fi
mkdir -p "${WORKSPACE_ROOT}"

run_project_group() {
  local group_file="$1" command_count i cmd dir workdir rc failures=0 index
  command_count="$(jq '.commands | length' "${group_file}")"
  for (( i=0; i<command_count; i++ )); do
    cmd="$(jq -r --argjson i "${i}" '.commands[$i].command' "${group_file}")"
    dir="$(jq -r --argjson i "${i}" '.commands[$i].dir // ""' "${group_file}")"
    index="$(jq -r --argjson i "${i}" '.commands[$i].index' "${group_file}")"
    workdir="${WORKSPACE_ROOT}"
    if [[ -n "${dir}" ]]; then
      if [[ -d "${WORKSPACE_ROOT}/${dir}" ]]; then
        workdir="${WORKSPACE_ROOT}/${dir}"
      else
        warn "  repo dir not found: ${WORKSPACE_ROOT}/${dir}; running from ${WORKSPACE_ROOT}"
      fi
    fi
    note "[${index}/${count}] (${workdir}) ${cmd}"
    # Keep the existing failure policy and fresh shell/cwd for EVERY command.
    if ( cd "${workdir}" && bash -c "${cmd}" ); then
      ok "  [${index}/${count}] ok"
    else
      rc=$?
      warn "  [${index}/${count}] command exited ${rc}: ${cmd}"
      failures=$((failures + 1))
    fi
  done
  if (( failures > 0 )); then
    err "${failures} of ${command_count} commands failed in this project"
    return 1
  fi
}
export -f run_project_group note ok warn err
export WORKSPACE_ROOT count _C_DIM _C_OK _C_WARN _C_ERR _C_RESET

# Serialize groups which would write into shared/nested working directories or
# linked worktrees. Missing/no-repo profiles use the workspace root and therefore
# run exclusively. Arbitrary external resources need project-owned locks or
# PROVISION_JOBS=1; commands are opaque shell scripts, not dependency declarations.
paths_overlap() {
  [[ "$1" == "$2" || "$1/" == "$2/"* || "$2/" == "$1/"* ]]
}
conflicts_with_active() {
  local pid
  for pid in "${!active[@]}"; do
    if paths_overlap "$1" "${active_targets[${pid}]}" ||
        paths_overlap "$2" "${active_storage[${pid}]}"; then
      return 0
    fi
  done
  return 1
}
failed=0
completed=0
commands_printed=0
wait_for_one() {
  local finished pid rc=0 index
  # Bash wait -n ignores jobs which completed before it was called. Reap those
  # by PID first, including their actual exit status; then wait for live jobs.
  while :; do
    finished=""
    rc=0
    for pid in "${!active[@]}"; do
      if ! kill -0 "${pid}" 2>/dev/null; then
        finished="${pid}"
        wait "${pid}" || rc=$?
        break
      fi
    done
    if [[ -z "${finished}" ]]; then
      wait -n -p finished "${!active[@]}" 2>/dev/null || rc=$?
    fi
    # A job can finish between the liveness check and wait -n. Scan again.
    [[ -n "${finished:-}" ]] && break
  done
  index="${active[${finished}]}"
  completed=$((completed + 1))
  printf '\n[%s completed] %s\n' "${completed}" "${active_targets[${finished}]}"
  # Workers number their commands by plan position, but groups finish in any order.
  # Renumber each finished group's lines in completion order so the log counts 1..N.
  awk -v start="${commands_printed}" -v total="${count}" '
    match($0, /\[[0-9]+\/[0-9]+\]/) {
      key = substr($0, RSTART + 1, RLENGTH - 2); sub(/\/.*/, "", key)
      if (!(key in seen)) seen[key] = ++n + start
      $0 = substr($0, 1, RSTART - 1) "[" seen[key] "/" total "]" substr($0, RSTART + RLENGTH)
    }
    { print }
    END { print n > "/dev/stderr" }' "${commands_tmp}/${index}.log" 2>"${commands_tmp}/${index}.n"
  commands_printed=$((commands_printed + $(cat "${commands_tmp}/${index}.n")))
  if (( rc != 0 )); then
    failed=$((failed + 1))
    echo "ERROR: project command worker exited ${rc}"
  fi
  unset 'active['"${finished}"']' 'active_targets['"${finished}"']' 'active_storage['"${finished}"']'
}

step "Running ${count} commands across ${group_count} project group(s) in parallel (PROVISION_JOBS=${provision_jobs}; 0 = all)"
for (( group=0; group<group_count; group++ )); do
  group_file="${commands_tmp}/${group}.json"
  jq --argjson i "${group}" '.[$i]' "${commands_tmp}/groups.json" >"${group_file}"
  name="$(jq -r '.name' "${group_file}")"
  dir="$(jq -r '.dir' "${group_file}")"
  target="${WORKSPACE_ROOT}"
  if [[ -n "${dir}" && -d "${WORKSPACE_ROOT}/${dir}" ]]; then
    target="${WORKSPACE_ROOT}/${dir}"
  fi
  target="$(realpath -m -- "${target}")"
  storage="${target}/.git"
  if [[ -e "${target}/.git" ]]; then
    if common_dir="$(git -C "${target}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)"; then
      storage="$(realpath -m -- "${common_dir}")"
    fi
  fi
  while (( ${#active[@]} > 0 )) && {
    (( provision_jobs > 0 && ${#active[@]} >= provision_jobs )) ||
      conflicts_with_active "${target}" "${storage}"
  }; do
    wait_for_one
  done
  note "[$((group + 1))/${group_count} started] ${name} (${target})"
  # $1 is the worker shell's positional argument.
  # shellcheck disable=SC2016
  setsid bash -c 'set -euo pipefail; run_project_group "$1"' -- "${group_file}" \
    < /dev/null >"${commands_tmp}/${group}.log" 2>&1 &
  pid=$!
  active[${pid}]="${group}"
  active_targets[${pid}]="${target}"
  active_storage[${pid}]="${storage}"
done
while (( ${#active[@]} > 0 )); do wait_for_one; done
if (( failed > 0 )); then
  err "${failed} of ${group_count} project command groups failed (see per-command errors above)"
  exit 1
fi
ok "All ${count} provisioning command(s) completed"
