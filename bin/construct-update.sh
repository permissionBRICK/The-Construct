#!/usr/bin/env bash
# construct-update.sh — implementation of `construct update` (see `construct update --help`).
#
# This VM asks its engine whether a newer Construct is out and starts the update
# (docs/plans/construct-platform-design.md 11.4 "Updates", 12.1):
#
#   construct update status [--json]   GET  /api/v1/vms/<this VM>/update
#   construct update run [--json]      POST /api/v1/vms/<this VM>/update
#
# With --json the engine's answer goes to stdout unchanged and nothing else does
# (T3 Code in the VM reads it for its update control). Without it, a few plain
# lines. Every failure is one line on stderr, nothing on stdout, and a non-zero
# exit. The VM token goes only to this VM's engine, through a header file.
#
# Exit codes:
#   0  ok
#   1  usage error, or the answer could not be read
#   4  no engine (no host service or no VM token), or the host has no update
#      route for its VMs yet (an older host: its admin updates it from the host page)
#   6  the engine refused (the message says why)
#   8  the engine could not be reached
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/construct-api.sh
source "${SCRIPT_DIR}/lib/construct-api.sh"

EXIT_USAGE=1
EXIT_NO_ENGINE=4
EXIT_REFUSED=6
EXIT_UNREACHABLE=8

fail() { local code="$1"; shift; printf 'construct update: %s\n' "$*" >&2; exit "${code}"; }

usage() {
  cat <<'USAGE'
Usage: construct update status [--json]
       construct update run [--json]

Updates this VM through its engine.

  status   Whether a newer Construct payload or host release is out.
           --json prints the engine's answer: newerPayload, availableCommit,
           appliedCommit, hostUpdate.
  run      Updates this VM (a reprovision); when the VM's owner administers the
           host and a host release is out, the host first, then this VM.
           --json prints the engine's answer: jobId, hostUpdateId, hostJobId.

Exit codes: 0 ok, 1 usage, 4 no engine or an older host without this route,
6 refused by the engine, 8 the engine could not be reached.
USAGE
}

cmd="${1:-}"
case "${cmd}" in
  help|--help|-h) usage; exit 0 ;;
  status|run) shift ;;
  "") usage >&2; exit "${EXIT_USAGE}" ;;
  *) fail "${EXIT_USAGE}" "unknown command: ${cmd} (try: status, run)" ;;
esac

json=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --json) json=true ;;
    *) fail "${EXIT_USAGE}" "unknown option: $1" ;;
  esac
  shift
done

command -v jq >/dev/null 2>&1 || fail "${EXIT_USAGE}" "jq is required"
api_available || fail "${EXIT_NO_ENGINE}" "this VM has no Construct engine"
api_begin || fail "${EXIT_NO_ENGINE}" "this VM has no token for its engine"
trap api_end EXIT

answer="${API_WORK}/answer"
path="/api/v1/vms/$(urlencode "${INSTANCE_NAME}")/update"
if [[ "${cmd}" == status ]]; then api GET "${path}" "" "${answer}"; else api POST "${path}" "" "${answer}"; fi

# curl reports a transfer that broke off (even after a status line) on stderr: no answer to trust.
[[ "${API_STATUS}" != 000 && -n "${API_ERROR}" ]] && fail "${EXIT_UNREACHABLE}" "the engine's answer broke off (${API_ERROR})"

case "${API_STATUS}" in
  200|202) ;;
  000) fail "${EXIT_UNREACHABLE}" "the engine could not be reached${API_ERROR:+ (${API_ERROR})}" ;;
  404|405)
    # Unknown VM comes with a problem document; an older host has no such route at all.
    text="$(api_problem "${answer}")"
    [[ -n "${text}" && "${API_STATUS}" == 404 ]] && fail "${EXIT_REFUSED}" "the engine refused it (HTTP 404): ${text}"
    fail "${EXIT_NO_ENGINE}" "this host does not update its VMs from the VM yet; its admin updates it from the host page" ;;
  *)
    text="$(api_problem "${answer}")"
    fail "${EXIT_REFUSED}" "the engine refused it (HTTP ${API_STATUS})${text:+: ${text}}" ;;
esac

jq -e 'type == "object"' "${answer}" >/dev/null 2>&1 || fail "${EXIT_USAGE}" "the engine's answer is not readable"

if [[ "${json}" == true ]]; then
  cat "${answer}"
  exit 0
fi

if [[ "${cmd}" == status ]]; then
  jq -r '
    def short: if type == "string" then .[0:7] else "unknown" end;
    (if .newerPayload then "This VM: update available (\(.appliedCommit | short) -> \(.availableCommit | short))"
     else "This VM: up to date (\(.appliedCommit // .availableCommit | short))" end),
    (if .hostUpdate then "Host: release \(.hostUpdate.version) available" else "Host: no update for you to run" end)
  ' "${answer}" || fail "${EXIT_USAGE}" "the engine's answer is not readable"
else
  jq -r '
    if .hostUpdateId then "Updating the host (update \(.hostUpdateId)); this VM follows."
    else "Updating this VM (job \(.jobId // "unknown"))." end
  ' "${answer}" || fail "${EXIT_USAGE}" "the engine's answer is not readable"
fi
