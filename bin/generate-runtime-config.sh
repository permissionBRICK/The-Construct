#!/usr/bin/env bash
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

if [[ ! -f "${CONFIG_FILE}" ]]; then
  err "Missing config file: ${CONFIG_FILE}"
  exit 1
fi

set -a
# shellcheck disable=SC1090
. "${CONFIG_FILE}"
set +a

AGENT_HOME="${AGENT_HOME:-/opt/construct}"
REPO_DIR="${REPO_DIR:-${AGENT_HOME}/repo}"
RUNTIME_DIR="${AGENT_HOME}/runtime"
PROJECTS="${PROJECTS:-default}"
ALLOW_HOST_PACKAGES="${ALLOW_HOST_PACKAGES:-false}"

mkdir -p "${RUNTIME_DIR}"

project_json_args=()
IFS=',' read -ra project_names <<<"${PROJECTS}"
for raw_name in "${project_names[@]}"; do
  name="$(printf '%s' "${raw_name}" | xargs)"
  [[ -n "${name}" ]] || continue
  file="${REPO_DIR}/projects/${name}.json"
  if [[ ! -f "${file}" ]]; then
    # Sample profiles ship as ${name}.json.sample and are not active projects
    # (they may reference placeholder images/repos). Skip rather than abort so a
    # stale PROJECTS list that names a sample still provisions cleanly.
    if [[ -f "${file}.sample" ]]; then
      warn "Skipping sample project profile: ${name} (rename ${name}.json.sample to ${name}.json to enable it)"
      continue
    fi
    err "Project profile not found: ${file}"
    exit 1
  fi
  project_json_args+=("${file}")
done

if [[ "${#project_json_args[@]}" -eq 0 ]]; then
  err "No project profiles selected in PROJECTS=${PROJECTS}"
  exit 1
fi

jq -s '
  def unique_strings: map(select(type == "string" and length > 0)) | unique;
  def merge_sdks:
    reduce .[] as $project ({};
      reduce (($project.sdks // {}) | to_entries[]) as $sdk (.;
        .[$sdk.key] = (((.[$sdk.key] // []) + (if ($sdk.value | type) == "array" then $sdk.value else [$sdk.value] end)) | unique_strings)
      )
    );
  {
    projects: map(.name) | unique_strings,
    repos: map(.repos // []) | add | unique_by(.url),
    sdks: merge_sdks,
    mcp: (map(.mcp // []) | add | unique_strings),
    hostPackages: (map(.hostPackages // []) | add | unique_strings),
    tests: map({(.name): (.tests // {})}) | add
  }
' "${project_json_args[@]}" >"${RUNTIME_DIR}/generated.json"

repos_json="$(jq -c '.repos' "${RUNTIME_DIR}/generated.json")"
sdks_json="$(jq -c '.sdks' "${RUNTIME_DIR}/generated.json")"
mcp_csv="$(jq -r '.mcp | join(",")' "${RUNTIME_DIR}/generated.json")"
projects_csv="$(jq -r '.projects | join(",")' "${RUNTIME_DIR}/generated.json")"
host_packages="$(jq -r '.hostPackages | join(" ")' "${RUNTIME_DIR}/generated.json")"

cat >"${RUNTIME_DIR}/generated.env" <<EOF
AGENT_PROJECTS=${projects_csv}
AGENT_REPOS_JSON=${repos_json}
AGENT_SDKS_JSON=${sdks_json}
AGENT_MCP=${mcp_csv}
COMPOSE_PROFILES=${mcp_csv}
EOF

if [[ -n "${host_packages}" ]]; then
  if [[ "${ALLOW_HOST_PACKAGES}" == "true" ]]; then
    step "Installing selected host packages: ${host_packages}"
    apt-get update
    # shellcheck disable=SC2086
    apt-get install -y ${host_packages}
  else
    warn "Host packages requested but not installed because ALLOW_HOST_PACKAGES=false: ${host_packages}"
  fi
fi

echo "Generated ${RUNTIME_DIR}/generated.json"
echo "Generated ${RUNTIME_DIR}/generated.env"
