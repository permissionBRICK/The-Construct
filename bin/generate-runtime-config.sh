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

# Persistent on-VM project profile store. The repo's projects/ is replaced on
# every provision (Provision-AgentVM.ps1 wipes /opt/construct/repo), and a
# reprovision driven from the PUBLIC repo carries no custom profiles. So we keep
# a copy of every profile under ${AGENT_HOME}/projects -- which survives
# reprovisions, since only .../repo is wiped. Custom profiles saved by an earlier
# deploy are never deleted, so a later reprovision from the public repo can still
# resolve them. The repo's copy is the source of truth (it is the most up to
# date): it refreshes the stored copy on every run, and is always preferred at
# resolution time -- the store is only a fallback for profiles the current repo
# doesn't ship.
PROJECTS_STORE="${PROJECTS_STORE:-${AGENT_HOME}/projects}"
mkdir -p "${PROJECTS_STORE}"
if [[ -d "${REPO_DIR}/projects" ]]; then
  shopt -s nullglob
  for src in "${REPO_DIR}/projects/"*.json; do
    # The *.json glob already excludes *.json.sample; also skip the schema file,
    # which is not a project profile.
    [[ "$(basename "${src}")" == "project.schema.json" ]] && continue
    cp -f "${src}" "${PROJECTS_STORE}/"
  done
  shopt -u nullglob
fi

project_json_args=()
IFS=',' read -ra project_names <<<"${PROJECTS}"
for raw_name in "${project_names[@]}"; do
  name="$(printf '%s' "${raw_name}" | xargs)"
  [[ -n "${name}" ]] || continue
  # Prefer the repo's (public) copy -- it is the most up to date. Fall back to
  # the persisted store only when the current repo doesn't ship this profile
  # (e.g. a custom profile when reprovisioning from the public repo).
  if [[ -f "${REPO_DIR}/projects/${name}.json" ]]; then
    file="${REPO_DIR}/projects/${name}.json"
  elif [[ -f "${PROJECTS_STORE}/${name}.json" ]]; then
    file="${PROJECTS_STORE}/${name}.json"
    note "Using persisted project profile (not in this repo): ${name}"
  else
    # A sample-only profile must be enabled by renaming; anything else is just
    # skipped (warn, never abort) so one missing name -- e.g. a custom profile
    # absent from the public repo and not yet persisted -- can't block the rest
    # of provisioning (AI tools, services, ...).
    if [[ -f "${REPO_DIR}/projects/${name}.json.sample" ]]; then
      warn "Skipping sample project profile: ${name} (rename ${name}.json.sample to ${name}.json to enable it)"
    else
      warn "Project profile not found, skipping: ${name} (no ${name}.json in repo or ${PROJECTS_STORE})"
    fi
    continue
  fi
  project_json_args+=("${file}")
done

if [[ "${#project_json_args[@]}" -eq 0 ]]; then
  # Nothing resolved -- fall back to the default profile rather than aborting the
  # whole provision, so the VM still comes up with AI tools and services. Prefer
  # the repo's default over a persisted one for the same reason.
  if [[ -f "${REPO_DIR}/projects/default.json" ]]; then
    warn "No requested project profiles found (PROJECTS=${PROJECTS}); falling back to 'default'."
    project_json_args+=("${REPO_DIR}/projects/default.json")
  elif [[ -f "${PROJECTS_STORE}/default.json" ]]; then
    warn "No requested project profiles found (PROJECTS=${PROJECTS}); falling back to persisted 'default'."
    project_json_args+=("${PROJECTS_STORE}/default.json")
  else
    err "No project profiles selected in PROJECTS=${PROJECTS} and no default profile available."
    exit 1
  fi
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
    mcpServers: (
      map(.mcp // []) | add
      | map(select(type == "object" and ((.name // "") | length) > 0))
      | map({ (.name): . }) | add // {}
    ),
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
