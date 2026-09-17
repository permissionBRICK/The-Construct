#!/usr/bin/env bash
# Guest token telemetry. Only the timer retries; no credentials are passed in argv.
# shellcheck source-path=SCRIPTDIR
set -uo pipefail
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
CURL="${CONSTRUCT_USAGE_CURL:-${CONSTRUCT_CURL:-curl}}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"
STATE_DIR="${CONSTRUCT_USAGE_STATE_DIR:-/var/lib/construct/usage}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
log() { printf 'construct-usage-report: %s\n' "$*" >&2; }

# Same narrow lookup as construct-idle-report.sh. Never source config.env.
_cfg_unquote() {
  local v="$1"
  if [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
    v="${v//\'\\\'\'/\'}"
  fi
  printf '%s' "${v}"
}
cfg_saved() {
  local key="$1" raw=""
  [[ -f "${CONFIG_FILE}" ]] || return 0
  raw="$(sed -n "s/^${key}=//p" "${CONFIG_FILE}" | head -1 || true)"
  _cfg_unquote "${raw}"
}
cfg_resolve() {
  local explicit="$1" key="$2" fallback="$3" saved
  if [[ -n "${explicit}" ]]; then printf '%s' "${explicit}"; return 0; fi
  saved="$(cfg_saved "${key}")"
  printf '%s' "${saved:-${fallback}}"
}
SERVICE_URL="$(cfg_resolve "${CONSTRUCT_SERVICE_URL:-}" CONSTRUCT_SERVICE_URL "")"
SERVICE_URL="${SERVICE_URL%/}"
INSTANCE_NAME="$(cfg_resolve "${CONSTRUCT_INSTANCE_NAME:-}" CONSTRUCT_INSTANCE_NAME \
  "$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || echo vm)")"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
ENABLED="$(cfg_resolve "${CONSTRUCT_USAGE_REPORT_ENABLED:-}" CONSTRUCT_USAGE_REPORT_ENABLED true)"
BACKFILL="$(cfg_resolve "${CONSTRUCT_USAGE_BACKFILL:-}" CONSTRUCT_USAGE_BACKFILL 0)"
[[ -n "${SERVICE_URL}" && "${ENABLED}" != false ]] || exit 0
[[ "${INSTANCE_NAME}" =~ ^[a-zA-Z0-9][a-zA-Z0-9-]*$ ]] || { log "invalid instance name"; exit 0; }
[[ -r "${VM_TOKEN_FILE}" ]] || { log "no usable VM token; not reporting usage"; exit 0; }
command -v jq >/dev/null 2>&1 || { log "jq is unavailable; retrying next tick"; exit 0; }
umask 077
mkdir -p "${STATE_DIR}" || exit 0
exec 9>"${STATE_DIR}/lock"
flock -n 9 || exit 0
work="$(mktemp -d)" || exit 0
trap 'rm -r -- "${work}"' EXIT
collector="${SCRIPT_DIR}/lib/usage-collect.sh"
[[ -f "${collector}" ]] || collector="${SCRIPT_DIR}/../extension/vm/usage-collect.sh"
# shellcheck source=../extension/vm/usage-collect.sh
source "${collector}"
ensure_ccusage
today="$(date +%Y%m%d)"
since="$(date -d '2 days ago' +%Y%m%d)"
previous="$(date -d "$(date +%Y-%m-01) -1 month" +%Y%m%d)"
daily_periods="$(jq -n --arg a "$(date -d '2 days ago' +%F)" --arg b "$(date -d yesterday +%F)" --arg c "$(date +%F)" '[$a,$b,$c]')"
monthly_periods="$(jq -n --arg a "$(date -d "${previous}" +%Y-%m)" --arg b "$(date +%Y-%m)" '[$a,$b]')"
backfill=false
[[ ! -f "${STATE_DIR}/backfilled" || "${BACKFILL}" == 1 ]] && backfill=true
complete=true
printf '[]' >"${work}/days.json"
for report in daily monthly; do
  [[ "${report}" != monthly || "${backfill}" == true ]] || continue
  ARGS=(daily --since "${since}" --until "${today}")
  periods="${daily_periods}"
  if [[ "${report}" == monthly ]]; then ARGS=(monthly --since "${previous}" --until "${today}"); periods="${monthly_periods}"; fi
  for tool in claude codex opencode; do
    capture "${tool}" >"${work}/raw.json"
    if ! jq --arg report "${report}" --arg tool "${tool}" --argjson periods "${periods}" \
      -f "${SCRIPT_DIR}/lib/usage-normalize.jq" "${work}/raw.json" >"${work}/rows.json" 2>/dev/null; then
      log "${tool} ${report} collection failed; retrying next tick"
      complete=false
      continue
    fi
    jq -s '.[0] + .[1]' "${work}/days.json" "${work}/rows.json" >"${work}/next.json" || exit 0
    mv "${work}/next.json" "${work}/days.json"
  done
done
jq -e 'length > 0' "${work}/days.json" >/dev/null || exit 0
jq '{generatedAt: (now | todate), days: .}' "${work}/days.json" >"${work}/request.json" || exit 0
token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
[[ -n "${token}" ]] || { log "no usable VM token; not reporting usage"; exit 0; }
( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${work}/headers" )
unset token
args=(--silent --show-error --fail-with-body --max-time "${API_TIMEOUT}" -H "@${work}/headers"
  -H 'Content-Type: application/json' -X POST --data-binary "@${work}/request.json" -o "${work}/body" -w '%{http_code}')
[[ -z "${CA_FILE}" ]] || args+=(--cacert "${CA_FILE}")
status="$("${CURL}" "${args[@]}" "${SERVICE_URL}/api/v1/vms/${INSTANCE_NAME}/usage" 2>"${work}/stderr")"; rc=$?
if [[ "${rc}" == 0 && "${status}" =~ ^2[0-9][0-9]$ ]]; then
  if [[ "${backfill}" == true && "${complete}" == true ]]; then touch "${STATE_DIR}/backfilled"; fi
else
  # Neither the response body nor curl diagnostics are trusted to exclude credentials.
  log "usage report failed; retrying next tick"
fi
exit 0
