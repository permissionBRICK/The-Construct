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

# On-VM project profile store — the SINGLE SOURCE OF TRUTH for user-created
# profiles (docs/config-sync.md). The host's sync engine (§6) keeps this
# directory up to date: it writes validated, canonical profile JSON over SSH on
# every sync tick. This script ONLY READS the store, never writes to it.
#
# Resolution rules per name in PROJECTS:
#   'default'          => ALWAYS ${REPO_DIR}/projects/default.json (shipped,
#                         read-only, reserved — never the store).
#   'project.schema'   => warn + skip (reserved file, not a profile).
#   anything else      => ${PROJECTS_STORE}/<name>.json ONLY. When missing:
#                         warn + skip (never abort; keep the .sample hint for
#                         names that match a shipped sample).
#
# Empty-resolution fallback: the shipped default.json only. The store's
# default.json is NEVER used (a stale copy there is ignored with a warning, not
# deleted — the host sync engine owns deletions).
#
# Per-file validation: before including ANY store file in the jq merge we run
# `jq empty <file>`. Invalid JSON => warn + skip. The host sync tick (§6) is the
# real validation gate (it runs the strict schema validator before writing to the
# store); this is the VM-side last line of defence so provisioning is never
# blocked by a corrupt file an agent wrote between ticks.
PROJECTS_STORE="${PROJECTS_STORE:-${AGENT_HOME}/projects}"
mkdir -p "${PROJECTS_STORE}"

project_json_args=()
IFS=',' read -ra project_names <<<"${PROJECTS}"
for raw_name in "${project_names[@]}"; do
  name="$(printf '%s' "${raw_name}" | xargs)"
  [[ -n "${name}" ]] || continue

  # Reject filename-unsafe names before building "${PROJECTS_STORE}/${name}.json"
  # (mirror the JS validateProfile guard): a profile name must be a single path
  # component — no separators, no "..", not "."/"..", no control characters — so a
  # crafted PROJECTS entry can't resolve a file outside the store. Warn + skip.
  if [[ "${name}" == "." || "${name}" == ".." || "${name}" == */* || "${name}" == *'\'* || "${name}" == *'..'* || "${name}" == *[[:cntrl:]]* ]]; then
    warn "Skipping unsafe project name: ${name} (must be a single filename, no path separators or '..')"
    continue
  fi

  # Reserved names: 'default' resolves from the shipped repo copy exclusively;
  # 'project.schema' is the schema file, never a profile — skip it.
  if [[ "${name,,}" == "default" ]]; then
    if [[ -f "${REPO_DIR}/projects/default.json" ]]; then
      project_json_args+=("${REPO_DIR}/projects/default.json")
    else
      err "Shipped default.json missing from ${REPO_DIR}/projects/ — the repo may be corrupt."
      exit 1
    fi
    # Warn (but do nothing) if a stale default.json lingers in the store.
    if [[ -f "${PROJECTS_STORE}/default.json" ]]; then
      warn "Ignoring stale default.json in ${PROJECTS_STORE} (reserved; using shipped copy)"
    fi
    continue
  fi
  if [[ "${name,,}" == "project.schema" ]]; then
    warn "Skipping reserved name: project.schema (not a project profile)"
    continue
  fi

  # Non-reserved: resolve from the store only.
  if [[ -f "${PROJECTS_STORE}/${name}.json" ]]; then
    file="${PROJECTS_STORE}/${name}.json"
    # Validate JSON before including — invalid => warn + skip.
    if ! jq empty "${file}" 2>/dev/null; then
      warn "Invalid JSON in ${file}, skipping (the host sync tick will repair or replace it)"
      continue
    fi
    project_json_args+=("${file}")
  else
    # A sample-only profile must be enabled by renaming; anything else is just
    # skipped (warn, never abort) so one missing name -- e.g. a custom profile
    # absent from the store and not yet synced -- can't block the rest of
    # provisioning (AI tools, services, ...).
    if [[ -f "${REPO_DIR}/projects/${name}.json.sample" ]]; then
      warn "Skipping sample project profile: ${name} (rename ${name}.json.sample to ${name}.json to enable it)"
    else
      warn "Project profile not found, skipping: ${name} (no ${name}.json in ${PROJECTS_STORE})"
    fi
    continue
  fi
done

if [[ "${#project_json_args[@]}" -eq 0 ]]; then
  # Nothing resolved -- fall back to the shipped default profile rather than
  # aborting the whole provision, so the VM still comes up with AI tools and
  # services. The store's default.json is never used (reserved, shipped only).
  if [[ -f "${REPO_DIR}/projects/default.json" ]]; then
    warn "No requested project profiles found (PROJECTS=${PROJECTS}); falling back to shipped 'default'."
    project_json_args+=("${REPO_DIR}/projects/default.json")
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
    mcpServers: (
      map(.mcp // []) | add
      | map(select(type == "object" and ((.name // "") | length) > 0))
    ),
    hostPackages: (map(.hostPackages // []) | add | unique_strings),
    provisionCommands: (
      # One {dir, command} per command, in profile order then array order. dir
      # is each profile FIRST repo checkout directory (resolved the same way
      # checkout-projects.sh does: explicit "directory", else the repo basename
      # without .git) so the runner can cd into the cloned repo before running it.
      # Empty when the profile declares no repos -> runner falls back to the
      # workspace root.
      map(
        (
          (.repos // [])[0] as $repo
          | if $repo == null then ""
            else ($repo.directory // ($repo.url | sub("\\.git$"; "") | sub(".*/"; "")))
            end
        ) as $dir
        | ((.provisionCommands // [])
           | map(select(type == "string" and length > 0))
           | map({ dir: $dir, command: . }))
      )
      | add
    ),
    tests: map({(.name): (.tests // {})}) | add
  }
' "${project_json_args[@]}" >"${RUNTIME_DIR}/generated.json"

repos_json="$(jq -c '.repos' "${RUNTIME_DIR}/generated.json")"
sdks_json="$(jq -c '.sdks' "${RUNTIME_DIR}/generated.json")"
projects_csv="$(jq -r '.projects | join(",")' "${RUNTIME_DIR}/generated.json")"
host_packages="$(jq -r '.hostPackages | join(" ")' "${RUNTIME_DIR}/generated.json")"

cat >"${RUNTIME_DIR}/generated.env" <<EOF
AGENT_PROJECTS=${projects_csv}
AGENT_REPOS_JSON=${repos_json}
AGENT_SDKS_JSON=${sdks_json}
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
