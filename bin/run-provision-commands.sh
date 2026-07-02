#!/usr/bin/env bash
#
# Run the custom provisioning commands declared by the selected project profiles.
#
# Reads the merged `.provisionCommands` array from runtime/generated.json
# (produced by generate-runtime-config.sh) and runs each entry, in array order,
# via `bash -c` as root. They run AFTER the project repos are checked out, with
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

count="$(jq -r '(.provisionCommands // []) | length' "${GENERATED_JSON}")"
if [[ "${count}" -eq 0 ]]; then
  note "No provisioning commands declared by the selected projects."
  exit 0
fi

mkdir -p "${WORKSPACE_ROOT}"

step "Running ${count} project provisioning command(s)"
failed=0
for (( i=0; i<count; i++ )); do
  # Extract by index so commands containing newlines/quotes survive intact.
  cmd="$(jq -r --argjson i "${i}" '.provisionCommands[$i].command' "${GENERATED_JSON}")"
  dir="$(jq -r --argjson i "${i}" '.provisionCommands[$i].dir // ""' "${GENERATED_JSON}")"

  # Run inside the profile's checked-out repo. Fall back to WORKSPACE_ROOT when
  # the profile declares no repo, or the repo isn't on disk (clone failed, or
  # CHECKOUT_PROJECTS=false) -- the command still runs every provision.
  workdir="${WORKSPACE_ROOT}"
  if [[ -n "${dir}" ]]; then
    if [[ -d "${WORKSPACE_ROOT}/${dir}" ]]; then
      workdir="${WORKSPACE_ROOT}/${dir}"
    else
      warn "  repo dir not found: ${WORKSPACE_ROOT}/${dir}; running from ${WORKSPACE_ROOT}"
    fi
  fi

  note "[$((i + 1))/${count}] (${workdir}) ${cmd}"
  # Run in a subshell so an inner `cd` / env change can't leak into the next
  # command, and so one command's failure can't abort this loop under `set -e`.
  if ( cd "${workdir}" && bash -c "${cmd}" ); then
    ok "  [$((i + 1))/${count}] ok"
  else
    rc=$?
    warn "  [$((i + 1))/${count}] command exited ${rc}: ${cmd}"
    failed=$((failed + 1))
  fi
done

if [[ "${failed}" -gt 0 ]]; then
  err "${failed} of ${count} provisioning command(s) failed"
  exit 1
fi
ok "All ${count} provisioning command(s) completed"
