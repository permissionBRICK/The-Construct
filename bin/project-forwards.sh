#!/usr/bin/env bash
#
# Request the port forwards declared by the selected project profiles.
#
# Reads the merged `.forwards` array from runtime/generated.json (produced by
# generate-runtime-config.sh) and asks for each one through `construct expose
# <port> --to <client|host> --label <label> --reuse --wait 0`: the same machinery
# an agent uses, so local and service-managed VMs behave alike. --reuse makes it
# get-or-create, so a reprovision never adds a second forward for the same port
# and target; --wait 0 means a client forward nobody has picked up yet is queued
# (it opens as soon as the user's Construct client connects) instead of holding
# up provisioning.
#
# An entry with `https: true` adds --https-if-available (plus --name and
# --upstream-tls when it sets them): an HTTPS forward with the host's certificate
# where the host offers one for this VM, a plain host forward everywhere else
# (a local install, a host without a certificate or without the permission), so
# one shared profile works on every VM. That is how provisioning asks for T3.
#
# Forwards are VM state: a reinstall deletes them along with the VM, and nothing
# but the profiles brings them back. Removing an entry from a profile does not
# close the live forward; `construct expose --close <port>` does.
#
# A forward that cannot be opened is reported but does not stop the others; the
# script exits non-zero at the end so provision.sh warns without failing.
#
# Environment: CONFIG_FILE, AGENT_HOME, REPO_DIR, GENERATED_JSON, and
# CONSTRUCT_EXPOSE_SCRIPT (the expose CLI; tests point it at a stub).
#
set -euo pipefail

if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_OK=$'\033[32m'; _C_WARN=$'\033[33m'; _C_ERR=$'\033[31m'; _C_DIM=$'\033[2m'; _C_RESET=$'\033[0m'
else
  _C_OK=''; _C_WARN=''; _C_ERR=''; _C_DIM=''; _C_RESET=''
fi
ok()   { printf '%s%s%s\n' "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n' "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n' "${_C_ERR}"  "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n' "${_C_DIM}"  "$*" "${_C_RESET}"; }

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
AGENT_HOME="${AGENT_HOME:-/opt/construct}"
REPO_DIR="${REPO_DIR:-${AGENT_HOME}/repo}"
GENERATED_JSON="${GENERATED_JSON:-${AGENT_HOME}/runtime/generated.json}"
EXPOSE="${CONSTRUCT_EXPOSE_SCRIPT:-${REPO_DIR}/bin/construct-expose.sh}"

if ! command -v jq >/dev/null 2>&1; then
  err "jq is required (installed by bootstrap.sh)."
  exit 1
fi

# Generate the runtime config on demand so this also runs standalone.
if [[ ! -f "${GENERATED_JSON}" ]]; then
  CONFIG_FILE="${CONFIG_FILE}" bash "${REPO_DIR}/bin/generate-runtime-config.sh" >/dev/null
fi

if ! entries="$(jq -c '(.forwards // [])[]' "${GENERATED_JSON}")"; then
  err "Cannot read the forwards from ${GENERATED_JSON}"
  exit 1
fi
if [[ -z "${entries}" ]]; then
  note "  No project declares a port forward"
  exit 0
fi

failed=0
errfile="$(mktemp)"
trap 'rm -f "${errfile}"' EXIT
while IFS= read -r entry; do
  port="$(jq -r '.port' <<<"${entry}")"
  to="$(jq -r '.to // "client"' <<<"${entry}")"
  label="$(jq -r '.label // ""' <<<"${entry}")"
  profile="$(jq -r '.profile // ""' <<<"${entry}")"
  https="$(jq -r '.https == true' <<<"${entry}")"
  name="$(jq -r '.name // ""' <<<"${entry}")"
  args=("${port}" --to "${to}" --reuse --wait 0)
  [[ -n "${label}" ]] && args+=(--label "${label}")
  kind="${to}"
  if [[ "${https}" == "true" ]]; then
    args+=(--https-if-available)
    [[ -n "${name}" ]] && args+=(--name "${name}")
    [[ "$(jq -r '.upstreamTls == true' <<<"${entry}")" == "true" ]] && args+=(--upstream-tls)
    kind="https${name:+ ${name}}"
  fi
  rc=0
  link="$(CONFIG_FILE="${CONFIG_FILE}" bash "${EXPOSE}" "${args[@]}" 2>"${errfile}")" || rc=$?
  what="${port} (${kind}${label:+, ${label}}; ${profile})"
  case "${rc}" in
    0)
      ok "  ${what}: ${link}"
      if [[ "${https}" == "true" && "${link}" != https://* ]]; then
        note "    the host offers no HTTPS forward for this VM, so this is a plain host forward"
      fi
      ;;
    6) note "  ${what}: queued, opens when the user's Construct client connects" ;;
    7) failed=1; warn "  ${what}: refused by the host service"; sed 's/^/    /' "${errfile}" >&2 ;;
    *) failed=1; warn "  ${what}: not opened (construct expose exited ${rc})"; sed 's/^/    /' "${errfile}" >&2 ;;
  esac
done <<<"${entries}"

exit "${failed}"
