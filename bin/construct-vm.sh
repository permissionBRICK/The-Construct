#!/usr/bin/env bash
# construct-vm.sh — guest client for delegated child VMs.
# Frozen wire/CLI contract: docs/plans/host-administration-contracts.md §9.
set -uo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
CURL="${CONSTRUCT_CURL:-curl}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"
POLL_INTERVAL="${CONSTRUCT_VM_POLL_INTERVAL_SEC:-1}"

EXIT_USAGE=1
EXIT_INVALID=2
EXIT_NOT_FOUND=3
EXIT_REFUSED=4
EXIT_CONFLICT=5
EXIT_JOB_FAILED=6
EXIT_TIMEOUT=7
EXIT_SERVICE=8
EXIT_NOT_DELEGATED=9
EXIT_MAINTENANCE=10
EXIT_RATE_LIMITED=11

WORK_DIR=""
AUTH_HEADER_FILE=""
CONSOLE_SESSION_ID=""
CONSOLE_VM=""
SSE_PID=""

usage() {
  cat <<'USAGE'
Usage: construct vm <command> [options]

Child VMs:
  identity [--json]
  create (--iso-url URL | --iso PATH | --media ID) [--aux-iso PATH | --aux-media ID]
         --cpus N (--ram-gb G | --ram-mb M) --disk-gb D --lifetime L [options]
  list [--all-shared] [--json]
  inspect NAME [--json]
  start NAME --lifetime L [--json]
  restart NAME [--no-wait] [--json]
  shutdown NAME [--no-wait] [--json]
  save NAME [--json]
  renew NAME --lifetime L [--json]
  share NAME --scope private|host [--json]
  delete NAME --yes [--no-wait] [--operation-id ID] [--json]
  hardware NAME [hardware options] [--json]
  addresses NAME [--json]
  forward NAME PORT [--to client|host] [--connect-port PORT] [--label TEXT]
          [--wait SEC] [--json]

Media:
  media list [--json]
  media upload PATH [--role install|auxiliary] [--name NAME] [--sha256 HEX]
         [--dedicated-to VM] [--operation-id ID] [--json]
  media acquire URL [--role install|auxiliary] [--name NAME] [--sha256 HEX]
         [--operation-id ID] [--no-wait] [--json]
  media attach NAME (--install ID | --aux ID) [--boot-order LIST] [--json]
  media detach NAME (--install | --aux) [--json]
  media delete ID --yes [--json]

Console (one short-lived session per invocation):
  console NAME --screenshot FILE.png [--width W --height H]
  console NAME (--type-stdin | --type-file FILE | --key CODE [--press|--release]
               | --scancodes HEX,... | --ctrl-alt-del)
  console NAME (--move X,Y | --move-rel DX,DY | --click BTN
               | --press BTN | --release BTN)

Jobs:
  jobs [--json]
  wait JOBID [--timeout SEC] [--json | --json-progress]
  cancel JOBID [--json]

Common mutation options:
  --operation-id ID   Reuse this id for a safe retry (8–128 chars; create max 120).
  --no-wait           Return after the service accepts a background job.
  --json              Emit one JSON result and no other stdout text.
  --json-progress     Emit progress as NDJSON; the final line is the job state.

Lifetimes are "never" or an integer followed by m, h or d (minimum 5m).
Run `construct vm <command> --help` for this command summary.

Exit codes:
  0 success; 1 usage/local error; 2 invalid request; 3 not found; 4 refused;
  5 conflict/unsupported; 6 job failed; 7 wait timed out; 8 service error;
  9 this guest is not delegated; 10 maintenance; 11 rate limited.
USAGE
}

die() { printf 'construct vm: %s\n' "$*" >&2; exit "${EXIT_USAGE}"; }

legacy_hint() {
  printf '%s\n' 'construct vm: this VM has a legacy credential and cannot manage child VMs.' >&2
  printf '%s\n' 'Ask the VM owner to run Provision-AgentVM.ps1 -InstanceName <primary> -RotateVmToken.' >&2
}

_cfg_unquote() {
  local value="$1"
  if [[ ${#value} -ge 2 && "${value}" == \'*\' ]]; then
    value="${value:1:${#value}-2}"
    value="${value//\'\\\'\'/\'}"
  fi
  printf '%s' "${value}"
}

cfg_saved() {
  local key="$1" raw=""
  [[ -f "${CONFIG_FILE}" ]] || return 0
  raw="$(sed -n "s/^${key}=//p" "${CONFIG_FILE}" | head -n 1 || true)"
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
INSTANCE_NAME="$(cfg_resolve "${CONSTRUCT_INSTANCE_NAME:-}" CONSTRUCT_INSTANCE_NAME "$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || printf vm)")"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
VM_TOKEN_FILE="$(cfg_resolve "${CONSTRUCT_VM_TOKEN_FILE:-}" CONSTRUCT_VM_TOKEN_FILE /etc/construct/vm-token)"
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"

urlencode() { jq -nr --arg value "$1" '$value | @uri'; }

cleanup() {
  if [[ -n "${SSE_PID}" ]]; then
    kill "${SSE_PID}" 2>/dev/null || true
    wait "${SSE_PID}" 2>/dev/null || true
    SSE_PID=""
  fi
  if [[ -n "${CONSOLE_SESSION_ID}" && -n "${WORK_DIR}" && -n "${AUTH_HEADER_FILE}" ]]; then
    local path
    local args=(--silent --show-error --max-time "${API_TIMEOUT}" -H "@${AUTH_HEADER_FILE}" -H 'Accept: application/json' -X DELETE)
    path="/api/v1/vms/$(urlencode "${CONSOLE_VM}")/console/sessions/$(urlencode "${CONSOLE_SESSION_ID}")"
    if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
    "${CURL}" "${args[@]}" "${SERVICE_URL}${path}" >/dev/null 2>&1 || true
  fi
  if [[ -n "${WORK_DIR}" && -d "${WORK_DIR}" ]]; then rm -r "${WORK_DIR}"; fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

require_environment() {
  is_positive "${API_TIMEOUT}" || die 'CONSTRUCT_SERVICE_TIMEOUT_SEC must be a positive integer'
  if ! command -v jq >/dev/null 2>&1; then
    printf '%s\n' 'construct vm: jq is required (apt-get install -y jq)' >&2
    exit "${EXIT_USAGE}"
  fi
  if [[ -z "${SERVICE_URL}" ]]; then
    printf '%s\n' 'construct vm: this VM is not service-managed (CONSTRUCT_SERVICE_URL is unset)' >&2
    exit "${EXIT_NOT_DELEGATED}"
  fi
  if [[ ! -r "${VM_TOKEN_FILE}" ]]; then
    printf 'construct vm: no VM token at %s; reprovision this VM with its service credential\n' "${VM_TOKEN_FILE}" >&2
    exit "${EXIT_NOT_DELEGATED}"
  fi
  local token
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  [[ -n "${token}" ]] || { printf 'construct vm: the VM token file %s is empty\n' "${VM_TOKEN_FILE}" >&2; exit "${EXIT_NOT_DELEGATED}"; }
  WORK_DIR="$(mktemp -d)" || die 'cannot create a private temporary directory'
  AUTH_HEADER_FILE="${WORK_DIR}/auth.headers"
  ( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${AUTH_HEADER_FILE}" )
  unset token
}

API_STATUS=""
API_BODY=""
API_ERROR=""
API_RESPONSE_FILE=""

# Request bodies and credentials are always files. In particular, neither the VM
# token nor console text can appear in curl's argv. Keyed POSTs and idempotent
# PUT/DELETE/upload completion retry transport failures, at most three attempts.
# Console input POSTs are never automatically retried after an uncertain result.
api_request() {
  local method="$1" path="$2" body="${3:-}" operation_key="${4:-}" input_file="${5:-}" content_type="${6:-application/json}" binary="${7:-false}"
  local attempt max_attempts status curl_rc req_headers body_file stderr_file request_file
  max_attempts=1
  case "${method}" in
    PUT|DELETE) max_attempts=3 ;;
    POST) if [[ -n "${operation_key}" || "${path}" == */complete ]]; then max_attempts=3; fi ;;
  esac
  for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    body_file="${WORK_DIR}/response.$$.${RANDOM}"
    stderr_file="${WORK_DIR}/stderr.$$.${RANDOM}"
    req_headers="${WORK_DIR}/headers.$$.${RANDOM}"
    cp "${AUTH_HEADER_FILE}" "${req_headers}"
    if [[ -n "${operation_key}" ]]; then printf 'X-Construct-Operation-Key: %s\n' "${operation_key}" >>"${req_headers}"; fi
    local accept=application/json
    [[ "${binary}" == true ]] && accept=image/png
    local args=(--silent --show-error --fail-with-body --max-time "${API_TIMEOUT}" -H "@${req_headers}" -H "Accept: ${accept}" -X "${method}" -o "${body_file}" -w '%{http_code}')
    if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
    if [[ -n "${input_file}" ]]; then
      args+=(-H "Content-Type: ${content_type}" --data-binary "@${input_file}")
    elif [[ -n "${body}" ]]; then
      request_file="${WORK_DIR}/request.$$.${RANDOM}.json"
      printf '%s' "${body}" >"${request_file}"
      args+=(-H 'Content-Type: application/json' --data-binary "@${request_file}")
    fi
    curl_rc=0
    status="$("${CURL}" "${args[@]}" "${SERVICE_URL}${path}" 2>"${stderr_file}")" || curl_rc=$?
    API_STATUS="${status:-000}"
    if [[ ${curl_rc} -ne 0 && ${curl_rc} -ne 22 ]]; then API_STATUS=000; fi
    if [[ "${binary}" == true && "${API_STATUS}" =~ ^2 ]]; then API_BODY=""; else API_BODY="$(cat "${body_file}" 2>/dev/null || true)"; fi
    API_ERROR="$(tr -d '\r' <"${stderr_file}" 2>/dev/null | head -n 3 || true)"
    API_RESPONSE_FILE="${body_file}"
    if [[ "${API_STATUS}" != "000" && ${curl_rc} -ne 7 && ${curl_rc} -ne 28 ]]; then return 0; fi
    (( attempt < max_attempts )) || return 0
  done
}

api_is_success() { [[ "${API_STATUS}" =~ ^2[0-9][0-9]$ ]]; }

problem_field() {
  local field="$1"
  printf '%s' "${API_BODY}" | jq -r --arg field "${field}" 'if type == "object" then (.[$field] // empty) else empty end' 2>/dev/null || true
}

api_fail() {
  local code title detail exit_code
  if ! printf '%s' "${API_BODY}" | jq -e 'type == "object"' >/dev/null 2>&1; then
    if [[ "${API_STATUS}" == "000" ]]; then
      printf 'construct vm: cannot reach the host service at %s\n' "${SERVICE_URL}" >&2
      [[ -n "${API_ERROR}" ]] && printf '  %s\n' "${API_ERROR}" >&2
    else
      printf 'construct vm: the host service answered HTTP %s with an unusable response\n' "${API_STATUS}" >&2
    fi
    exit "${EXIT_SERVICE}"
  fi
  code="$(problem_field code)"
  if [[ "${code}" == token-kind-legacy ]]; then legacy_hint; exit "${EXIT_NOT_DELEGATED}"; fi
  title="$(problem_field title)"
  detail="$(problem_field detail)"
  printf 'construct vm: %s%s%s\n' "${title:-Request failed}" "${detail:+: ${detail}}" "${code:+ (${code})}" >&2
  if [[ "${code}" == media-in-use ]]; then
    printf '%s' "${API_BODY}" | jq -r '.references[]? | "reference: " + (if type=="string" then . else tojson end)' >&2
  fi
  case "${API_STATUS}" in
    400|413|422) exit_code="${EXIT_INVALID}" ;;
    401|403) exit_code="${EXIT_REFUSED}" ;;
    404) exit_code="${EXIT_NOT_FOUND}" ;;
    409|410) exit_code="${EXIT_CONFLICT}" ;;
    429) exit_code="${EXIT_RATE_LIMITED}" ;;
    503) exit_code="${EXIT_MAINTENANCE}" ;;
    *) exit_code="${EXIT_SERVICE}" ;;
  esac
  exit "${exit_code}"
}

expect_json() {
  local kind="$1"
  api_is_success || api_fail
  if ! printf '%s' "${API_BODY}" | jq -e --arg kind "${kind}" 'type == $kind' >/dev/null 2>&1; then
    printf 'construct vm: the host service answered HTTP %s with an unusable JSON shape\n' "${API_STATUS}" >&2
    exit "${EXIT_SERVICE}"
  fi
  if [[ "${kind}" == object ]] && printf '%s' "${API_BODY}" | jq -e '.replayed == true' >/dev/null 2>&1; then
    printf '%s\n' 'replayed' >&2
  fi
}

new_operation_key() {
  if [[ -r /proc/sys/kernel/random/uuid ]]; then tr -d '\r\n' </proc/sys/kernel/random/uuid
  elif command -v uuidgen >/dev/null 2>&1; then uuidgen
  else printf '%(%s)T-%08x-%08x' -1 "${RANDOM}" "${RANDOM}"; fi
}

validate_operation_key() {
  local key="$1"
  [[ ${#key} -ge 8 && ${#key} -le 128 && "${key}" =~ ^[A-Za-z0-9._:-]+$ ]] \
    || die '--operation-id must be 8–128 characters from A-Z, a-z, 0-9, dot, underscore, colon or hyphen'
}

operation_key() {
  local supplied="$1" quiet="${2:-false}" key
  key="${supplied:-$(new_operation_key)}"
  validate_operation_key "${key}"
  if [[ "${quiet}" != true ]]; then printf 'operation id: %s\n' "${key}" >&2; fi
  printf '%s' "${key}"
}

is_uint() { [[ "$1" =~ ^[0-9]+$ ]]; }
is_positive() { [[ "$1" =~ ^[1-9][0-9]*$ ]]; }
is_port() { is_positive "$1" && (( 10#$1 <= 65535 )); }

validate_lifetime() {
  local value="$1" number unit seconds
  [[ "${value}" == never ]] && return 0
  [[ "${value}" =~ ^([1-9][0-9]*)([mhd])$ ]] || die "invalid lifetime '${value}' (use never or an integer followed by m, h or d)"
  number="${BASH_REMATCH[1]}"; unit="${BASH_REMATCH[2]}"
  case "${unit}" in m) seconds=$((number * 60));; h) seconds=$((number * 3600));; d) seconds=$((number * 86400));; esac
  (( seconds >= 300 )) || die 'lifetime must be at least 5m'
}

validate_sha256() { [[ "$1" =~ ^[A-Fa-f0-9]{64}$ ]] || die '--sha256 must be exactly 64 hexadecimal characters'; }
validate_on_off() { [[ "$2" == on || "$2" == off ]] || die "$1 must be on or off"; }

json_with_operation_key() {
  local json="$1" key="$2"
  printf '%s' "${json}" | jq -c --arg operationKey "${key}" 'if has("operationKey") then . else . + {operationKey:$operationKey} end'
}

print_human_object() {
  printf '%s' "$1" | jq -r '
    if .kind? and .state? and .id? then
      "job: \(.id)", "state: \(.state)",
      (if .result.outcome? then "outcome: \(.result.outcome)" else empty end),
      (if .result.finalState? then "final state: \(.result.finalState)" else empty end)
    elif .name? then
      ["name: \(.name)", (if .state? then "state: \(.state)" else empty end),
       (if .jobId? then "job: \(.jobId)" else empty end),
       (if .lease.expiresAt? then "lease expires: \(.lease.expiresAt)" else empty end)] | .[]
    elif .jobId? then "job: \(.jobId)"
    elif .id? then "id: \(.id)"
    else to_entries[] | "\(.key): \(.value | if type == "object" or type == "array" then tojson else tostring end)"
    end'
}

print_result() {
  local json="$1" json_mode="$2" key="${3:-}"
  if [[ "${json_mode}" == true ]]; then
    if [[ -n "${key}" ]]; then json_with_operation_key "${json}" "${key}"; else printf '%s\n' "$(printf '%s' "${json}" | jq -c .)"; fi
  else
    print_human_object "${json}"
  fi
}

IDENTITY_JSON=""
discover_identity() {
  local path
  path="/api/v1/vms/$(urlencode "${INSTANCE_NAME}")/identity"
  api_request GET "${path}"
  expect_json object
  IDENTITY_JSON="${API_BODY}"
}

require_delegated() {
  if [[ "$(printf '%s' "${IDENTITY_JSON}" | jq -r '.tokenKind // "legacy"')" == legacy ]]; then
    legacy_hint
    exit "${EXIT_NOT_DELEGATED}"
  fi
}

require_create_allowed() {
  if [[ "$(printf '%s' "${IDENTITY_JSON}" | jq -r '.delegation.allowChildCreation // false')" != true ]]; then
    printf '%s\n' 'construct vm: Child VM creation is disabled for this primary.' >&2
    exit "${EXIT_REFUSED}"
  fi
}

job_state_is_terminal() { [[ "$1" == succeeded || "$1" == failed || "$1" == cancelled ]]; }

emit_progress() {
  local event="$1" data="$2" json_progress="$3" phase="$4" at time text_value
  if [[ "${json_progress}" == true ]]; then
    case "${event}" in
      progress) printf '%s' "${data}" | jq -c '{event:"progress",at,text}' ;;
      phase) printf '%s' "${data}" | jq -c '{event:"phase",at,phase}' ;;
      state) printf '%s' "${data}" | jq -c '{event:"state",job:(if .job? then .job else . end)}' ;;
    esac
    return
  fi
  [[ "${event}" == state ]] && return
  at="$(printf '%s' "${data}" | jq -r '.at // empty')"
  time="${at:11:8}"; [[ ${#time} -eq 8 ]] || time="$(date -u +%H:%M:%S)"
  if [[ "${event}" == phase ]]; then
    text_value="$(printf '%s' "${data}" | jq -r '.phase // empty')"
    printf '[%s] phase: %s\n' "${time}" "${text_value}" >&2
  else
    text_value="$(printf '%s' "${data}" | jq -r '.text // empty')"
    printf '[%s] %s: %s\n' "${time}" "${phase:-progress}" "${text_value}" >&2
  fi
}

# Read the actual SSE endpoint. A stream bounded by the per-call timeout may end
# before the job does; reconnecting is safe because the service replays progress.
JOB_RESULT=""
follow_job() {
  local job_id="$1" timeout_sec="${2:-600}" json_progress="${3:-false}"
  local started deadline path state phase="progress" fifo headers stderr_file pid rc line event="" data="" status terminal_file seen_events=0 stream_events problem_file remaining call_limit="${API_TIMEOUT}"
  local API_TIMEOUT="${call_limit}"
  JOB_RESULT=""
  is_uint "${timeout_sec}" || die '--timeout must be a whole number of seconds'
  started="$(date +%s)"; deadline=$((started + timeout_sec))
  path="/api/v1/jobs/$(urlencode "${job_id}")/events"
  terminal_file="${WORK_DIR}/terminal.$$.${RANDOM}"
  while (( $(date +%s) < deadline )); do
    remaining=$((deadline - $(date +%s)))
    (( remaining > 0 )) || break
    API_TIMEOUT="${call_limit}"
    (( API_TIMEOUT <= remaining )) || API_TIMEOUT="${remaining}"
    stream_events=0
    fifo="${WORK_DIR}/events.$$.${RANDOM}.fifo"; headers="${WORK_DIR}/events.$$.${RANDOM}.headers"; stderr_file="${WORK_DIR}/events.$$.${RANDOM}.stderr"
    mkfifo "${fifo}" || die 'cannot create progress pipe'
    problem_file="${WORK_DIR}/events-problem.$$.${RANDOM}"
    : >"${problem_file}"
    local args=(--silent --show-error --no-buffer --fail-with-body --max-time "${API_TIMEOUT}" -H "@${AUTH_HEADER_FILE}" -H 'Accept: text/event-stream' -D "${headers}" "${SERVICE_URL}${path}")
    if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
    "${CURL}" "${args[@]}" >"${fifo}" 2>"${stderr_file}" & pid=$!; SSE_PID="${pid}"
    while IFS= read -r line || [[ -n "${line}" ]]; do
      line="${line%$'\r'}"
      case "${line}" in
        event:*) event="${line#event:}"; event="${event# }" ;;
        data:*)
          data="${line#data:}"; data="${data# }"
          printf '%s' "${data}" | jq -e . >/dev/null 2>&1 || continue
          stream_events=$((stream_events + 1))
          case "${event}" in
            phase) phase="$(printf '%s' "${data}" | jq -r '.phase // "progress"')"; (( stream_events > seen_events )) && emit_progress phase "${data}" "${json_progress}" "${phase}" ;;
            progress) (( stream_events > seen_events )) && emit_progress progress "${data}" "${json_progress}" "${phase}" ;;
            state)
              state="$(printf '%s' "${data}" | jq -r 'if .job? then .job.state else .state end')"
              if job_state_is_terminal "${state}"; then
                JOB_RESULT="$(printf '%s' "${data}" | jq -c 'if .job? then .job else . end')"
                printf '%s' "${JOB_RESULT}" >"${terminal_file}"
                emit_progress state "${data}" "${json_progress}" "${phase}"
              fi
              ;;
          esac
          ;;
        *) printf '%s\n' "${line}" >>"${problem_file}" ;;
      esac
    done <"${fifo}"
    rc=0; wait "${pid}" || rc=$?; SSE_PID=""
    rm "${fifo}"
    (( stream_events > seen_events )) && seen_events="${stream_events}"
    if [[ -s "${terminal_file}" ]]; then JOB_RESULT="$(cat "${terminal_file}")"; break; fi
    status="$(awk '/^HTTP\// { code=$2 } END { print code }' "${headers}" 2>/dev/null || true)"
    if [[ -n "${status}" && ! "${status}" =~ ^2 ]]; then
      API_STATUS="${status}"; API_BODY="$(cat "${problem_file}")"; API_ERROR="$(head -n 3 "${stderr_file}" 2>/dev/null || true)"; api_fail
    fi
    if [[ ${rc} -ne 0 && ${rc} -ne 28 ]]; then
      API_ERROR="$(head -n 3 "${stderr_file}" 2>/dev/null || true)"
      printf 'construct vm: job event stream failed%s\n' "${API_ERROR:+: ${API_ERROR}}" >&2
      exit "${EXIT_SERVICE}"
    fi
    remaining=$((deadline - $(date +%s)))
    (( remaining > 0 )) || break
    (( API_TIMEOUT <= remaining )) || API_TIMEOUT="${remaining}"
    api_request GET "/api/v1/jobs/$(urlencode "${job_id}")"
    expect_json object
    state="$(printf '%s' "${API_BODY}" | jq -r '.state // empty')"
    if job_state_is_terminal "${state}"; then
      JOB_RESULT="$(printf '%s' "${API_BODY}" | jq -c .)"
      emit_progress state "${JOB_RESULT}" "${json_progress}" "${phase}"
      break
    fi
    sleep "${POLL_INTERVAL}"
  done
  if [[ -z "${JOB_RESULT}" ]]; then
    printf 'construct vm: timed out waiting for job %s\n' "${job_id}" >&2
    exit "${EXIT_TIMEOUT}"
  fi
  state="$(printf '%s' "${JOB_RESULT}" | jq -r '.state // empty')"
  if [[ "${state}" != succeeded ]]; then
    if [[ "${json_progress}" != true ]]; then
      printf 'construct vm: job %s %s%s\n' "${job_id}" "${state:-failed}" "$(printf '%s' "${JOB_RESULT}" | jq -r 'if .error? then ": " + .error else "" end')" >&2
    fi
    exit "${EXIT_JOB_FAILED}"
  fi
}

submit_job_result() {
  local accepted="$1" no_wait="$2" json_mode="$3" json_progress="$4" key="$5" timeout="${6:-600}" job_id
  job_id="$(printf '%s' "${accepted}" | jq -r '.jobId // empty')"
  [[ -n "${job_id}" ]] || { printf '%s\n' 'construct vm: service accepted a job without returning jobId' >&2; exit "${EXIT_SERVICE}"; }
  if [[ "${no_wait}" == true ]]; then print_result "${accepted}" "${json_mode}" "${key}"; return 0; fi
  follow_job "${job_id}" "${timeout}" "${json_progress}"
  if [[ "${json_progress}" != true ]]; then print_result "${JOB_RESULT}" "${json_mode}" "${key}"; fi
}

cmd_identity() {
  local json=false
  while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; -h|--help) usage; return;; *) die "unknown identity option: $1";; esac; shift; done
  if [[ "${json}" == true ]]; then printf '%s\n' "$(printf '%s' "${IDENTITY_JSON}" | jq -c .)"; else
    printf '%s' "${IDENTITY_JSON}" | jq -r '"VM: \(.vmName // "unknown")\nkind: \(.kind // "unknown")\ntoken: \(.tokenKind // "unknown")\nowner: \(.owner // "unknown")\ndelegation: \(.delegation // {} | tojson)"'
  fi
}

cmd_list() {
  local json=false all_shared=false path own shared
  while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; --all-shared) all_shared=true;; -h|--help) usage; return;; *) die "unknown list option: $1";; esac; shift; done
  path="/api/v1/vms?parent=$(urlencode "${INSTANCE_NAME}")"
  api_request GET "${path}"; expect_json array; own="${API_BODY}"
  if [[ "${all_shared}" == true ]]; then api_request GET /api/v1/vms/shared; expect_json array; shared="${API_BODY}"; own="$(jq -cn --argjson own "${own}" --argjson shared "${shared}" '$own + $shared | unique_by(.name)')"; fi
  if [[ "${json}" == true ]]; then printf '%s\n' "$(printf '%s' "${own}" | jq -c .)"; return; fi
  printf '%-24s %-10s %-22s %4s %8s %8s %-8s %s\n' NAME STATE LEASE CPU RAM DISK SHARING OPERATION
  printf '%s' "${own}" | jq -r '.[] | [(.name//"-"),(.state//"unknown"),((.lease.expiresAt // (if .lease.state=="unlimited" then "never" else "-" end)) + (if .lease.overdue then " overdue" else "" end)),(.hardware.cpus//.cpu//"-"),(.hardware.ramMb//(if .ramGb? then .ramGb*1024 else "-" end)),(.hardware.diskGb//.diskGb//"-"),(.sharing//"private"),(.currentOperation.jobId//"-")] | @tsv' \
    | while IFS=$'\t' read -r name state lease cpu ram disk sharing operation; do printf '%-24s %-10s %-22s %4s %8s %8s %-8s %s\n' "$name" "$state" "$lease" "$cpu" "$ram" "$disk" "$sharing" "$operation"; done
}

cmd_inspect() {
  local name="${1:-}" json=false encoded vm addresses capabilities result
  [[ -n "${name}" ]] || die 'inspect requires a VM name'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; *) die "unknown inspect option: $1";; esac; shift; done
  encoded="$(urlencode "${name}")"
  api_request GET "/api/v1/vms/${encoded}"; expect_json object; vm="${API_BODY}"
  api_request GET "/api/v1/vms/${encoded}/addresses"; expect_json object; addresses="${API_BODY}"
  api_request GET "/api/v1/vms/${encoded}/capabilities"; expect_json object; capabilities="${API_BODY}"
  result="$(jq -cn --argjson vm "${vm}" --argjson addresses "${addresses}" --argjson capabilities "${capabilities}" '{vm:$vm,addresses:$addresses,capabilities:$capabilities}')"
  if [[ "${json}" == true ]]; then printf '%s\n' "${result}"; else
    print_human_object "${vm}"
    if [[ "$(printf '%s' "${addresses}" | jq '.addresses | length')" -eq 0 ]]; then printf '%s\n' 'addresses: no address yet'; else printf '%s' "${addresses}" | jq -r '.addresses[] | "address: \(.address) (verified=\(.verified))"'; fi
    printf 'capabilities: %s\n' "$(printf '%s' "${capabilities}" | jq -c .)"
  fi
}

cmd_lifecycle() {
  local action="$1"; shift
  local name="${1:-}" lifetime="" json=false no_wait=false json_progress=false operation_id="" key body encoded
  [[ -n "${name}" ]] || die "${action} requires a VM name"; shift
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --lifetime) shift; [[ $# -gt 0 ]] || die '--lifetime requires a value'; lifetime="$1" ;;
      --json) json=true ;;
      --json-progress) json_progress=true ;;
      --no-wait) no_wait=true ;;
      --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1" ;;
      *) die "unknown ${action} option: $1" ;;
    esac; shift
  done
  if [[ "${action}" == start ]]; then [[ -n "${lifetime}" ]] || die 'start requires --lifetime'; validate_lifetime "${lifetime}"; elif [[ -n "${lifetime}" ]]; then die "--lifetime is not valid for ${action}"; fi
  key="$(operation_key "${operation_id}")"; encoded="$(urlencode "${name}")"
  body="$(jq -cn --arg action "${action}" --arg lifetime "${lifetime}" 'if $lifetime == "" then {action:$action} else {action:$action,lifetime:$lifetime} end')"
  api_request POST "/api/v1/vms/${encoded}/lifecycle" "${body}" "${key}"; expect_json object
  if [[ "${action}" == shutdown || "${action}" == restart ]]; then submit_job_result "${API_BODY}" "${no_wait}" "${json}" "${json_progress}" "${key}"; else print_result "${API_BODY}" "${json}" "${key}"; fi
}

cmd_renew() {
  local name="${1:-}" lifetime="" json=false operation_id="" key body
  [[ -n "${name}" ]] || die 'renew requires a VM name'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --lifetime) shift; [[ $# -gt 0 ]] || die '--lifetime requires a value'; lifetime="$1";; --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown renew option: $1";; esac; shift; done
  [[ -n "${lifetime}" ]] || die 'renew requires --lifetime'; validate_lifetime "${lifetime}"; key="$(operation_key "${operation_id}")"
  body="$(jq -cn --arg lifetime "${lifetime}" '{lifetime:$lifetime}')"; api_request POST "/api/v1/vms/$(urlencode "${name}")/lease" "${body}" "${key}"; expect_json object; print_result "${API_BODY}" "${json}" "${key}"
}

cmd_share() {
  local name="${1:-}" scope="" json=false operation_id="" key body
  [[ -n "${name}" ]] || die 'share requires a VM name'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --scope) shift; [[ $# -gt 0 ]] || die '--scope requires a value'; scope="$1";; --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown share option: $1";; esac; shift; done
  [[ "${scope}" == private || "${scope}" == host ]] || die 'share requires --scope private|host'; key="$(operation_key "${operation_id}")"
  body="$(jq -cn --arg scope "${scope}" '{scope:$scope}')"; api_request PUT "/api/v1/vms/$(urlencode "${name}")/sharing" "${body}" "${key}"; expect_json object; print_result "${API_BODY}" "${json}" "${key}"
}

confirm_delete() {
  local name="$1" yes="$2" typed
  [[ "${yes}" == true ]] && return 0
  if [[ -t 0 ]]; then
    printf 'Type the VM name "%s" to delete its disk, saved state and dedicated media: ' "${name}" >&2
    IFS= read -r typed
    [[ "${typed}" == "${name}" ]] || die 'confirmation did not match; nothing was deleted'
  else
    die '--yes required: this deletes the VM, its disk, its saved state and its dedicated media'
  fi
}

cmd_delete() {
  local name="${1:-}" yes=false no_wait=false json=false json_progress=false operation_id="" key
  [[ -n "${name}" ]] || die 'delete requires a VM name'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --yes) yes=true;; --no-wait) no_wait=true;; --json) json=true;; --json-progress) json_progress=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown delete option: $1";; esac; shift; done
  if [[ "${name,,}" == "${INSTANCE_NAME,,}" ]]; then printf '%s\n' 'construct vm: a primary credential cannot delete its own primary VM' >&2; exit "${EXIT_REFUSED}"; fi
  confirm_delete "${name}" "${yes}"; key="$(operation_key "${operation_id}")"
  api_request DELETE "/api/v1/vms/$(urlencode "${name}")" "" "${key}"; expect_json object
  submit_job_result "${API_BODY}" "${no_wait}" "${json}" "${json_progress}" "${key}"
}

firmware_json() {
  local generation="$1" secure_boot="$2" template="$3" tpm="$4" boot_order="$5"
  jq -cn --arg generation "${generation}" --arg secureBoot "${secure_boot}" --arg template "${template}" --arg tpm "${tpm}" --arg bootOrder "${boot_order}" '
    {} + (if $generation!="" then {generation:($generation|tonumber)} else {} end)
       + (if $secureBoot!="" then {secureBoot:($secureBoot=="on")} else {} end)
       + (if $template!="" then {secureBootTemplate:$template} else {} end)
       + (if $tpm!="" then {tpm:($tpm=="on")} else {} end)
       + (if $bootOrder!="" then {bootOrder:($bootOrder|split(","))} else {} end)'
}

validate_boot_order() {
  local value="$1" item
  local -a items=()
  IFS=',' read -r -a items <<<"${value}"
  ((${#items[@]} > 0)) || die '--boot-order cannot be empty'
  for item in "${items[@]}"; do case "${item}" in installMedia|auxiliaryMedia|disk|network) ;; *) die "invalid boot device: ${item}";; esac; done
}

cmd_hardware() {
  local name="${1:-}" cpus="" ram_mb="" disk_gb="" generation="" secure_boot="" template="" tpm="" boot_order="" json=false operation_id="" key fw body
  [[ -n "${name}" ]] || die 'hardware requires a VM name'; shift
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --cpus) shift; [[ $# -gt 0 ]] || die '--cpus requires a value'; cpus="$1";; --ram-mb) shift; [[ $# -gt 0 ]] || die '--ram-mb requires a value'; ram_mb="$1";;
      --disk-gb) shift; [[ $# -gt 0 ]] || die '--disk-gb requires a value'; disk_gb="$1";; --generation) shift; [[ $# -gt 0 ]] || die '--generation requires a value'; generation="$1";;
      --secure-boot) shift; [[ $# -gt 0 ]] || die '--secure-boot requires a value'; secure_boot="$1";; --secure-boot-template) shift; [[ $# -gt 0 ]] || die '--secure-boot-template requires a value'; template="$1";;
      --tpm) shift; [[ $# -gt 0 ]] || die '--tpm requires a value'; tpm="$1";; --boot-order) shift; [[ $# -gt 0 ]] || die '--boot-order requires a value'; boot_order="$1";;
      --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown hardware option: $1";;
    esac; shift
  done
  [[ -n "${cpus}${ram_mb}${disk_gb}${generation}${secure_boot}${template}${tpm}${boot_order}" ]] || die 'hardware requires at least one setting'
  [[ -z "${cpus}" ]] || is_positive "${cpus}" || die '--cpus must be positive'; [[ -z "${ram_mb}" ]] || is_positive "${ram_mb}" || die '--ram-mb must be positive'; [[ -z "${disk_gb}" ]] || is_positive "${disk_gb}" || die '--disk-gb must be positive'
  [[ -z "${generation}" ]] || is_positive "${generation}" || die '--generation must be positive'; [[ -z "${secure_boot}" ]] || validate_on_off --secure-boot "${secure_boot}"; [[ -z "${tpm}" ]] || validate_on_off --tpm "${tpm}"; [[ -z "${boot_order}" ]] || validate_boot_order "${boot_order}"
  fw="$(firmware_json "${generation}" "${secure_boot}" "${template}" "${tpm}" "${boot_order}")"
  body="$(jq -cn --arg cpus "${cpus}" --arg ram "${ram_mb}" --arg disk "${disk_gb}" --argjson firmware "${fw}" '{} + (if $cpus!="" then {cpus:($cpus|tonumber)} else {} end) + (if $ram!="" then {ramMb:($ram|tonumber)} else {} end) + (if $disk!="" then {diskGb:($disk|tonumber)} else {} end) + (if ($firmware|length)>0 then {firmware:$firmware} else {} end)')"
  key="$(operation_key "${operation_id}")"; api_request PUT "/api/v1/vms/$(urlencode "${name}")/hardware" "${body}" "${key}"; expect_json object; print_result "${API_BODY}" "${json}" "${key}"
}

media_get() { api_request GET "/api/v1/media/$(urlencode "$1")"; expect_json object; MEDIA_RESULT="${API_BODY}"; }

require_media_ready() {
  local state
  state="$(printf '%s' "${MEDIA_RESULT}" | jq -r '.state // "unknown"')"
  if [[ "${state}" != ready ]]; then
    printf 'construct vm: media is %s, not ready; wait for its acquisition or upload to complete before creating the child\n' "${state}" >&2
    exit "${EXIT_CONFLICT}"
  fi
}

human_bytes() {
  awk -v n="$1" 'BEGIN { if (n >= 1073741824) printf "%.1f GiB", n/1073741824; else if (n >= 1048576) printf "%.0f MiB", n/1048576; else if (n >= 1024) printf "%.1f KiB", n/1024; else printf "%d bytes", n }'
}

media_upload_impl() {
  local file="$1" role="$2" display_name="$3" sha256="$4" dedicated="$5" key="$6" quiet="$7"
  local size body upload_id media_id chunk_size status missing idx chunk offset uploaded total complete job_id upload_status expected actual
  [[ -r "${file}" && -f "${file}" ]] || die "cannot read media file: ${file}"
  size="$(stat -c %s "${file}" 2>/dev/null || true)"; is_uint "${size}" || die "cannot determine media size: ${file}"
  display_name="${display_name:-$(basename "${file}")}"
  body="$(jq -cn --arg name "${display_name}" --arg role "${role}" --arg size "${size}" --arg sha "${sha256}" --arg dedicated "${dedicated}" '{name:$name,role:$role,sizeBytes:($size|tonumber)} + (if $sha!="" then {expectedSha256:$sha} else {} end) + (if $dedicated!="" then {dedicatedTo:$dedicated} else {} end)')"
  api_request POST /api/v1/media/uploads "${body}" "${key}"; expect_json object
  upload_id="$(printf '%s' "${API_BODY}" | jq -r '.uploadId // empty')"; media_id="$(printf '%s' "${API_BODY}" | jq -r '.mediaId // empty')"; chunk_size="$(printf '%s' "${API_BODY}" | jq -r '.chunkSizeBytes // empty')"
  [[ -n "${upload_id}" && -n "${media_id}" ]] || { printf '%s\n' 'construct vm: upload begin response omitted its ids' >&2; exit "${EXIT_SERVICE}"; }
  is_positive "${chunk_size}" || { printf '%s\n' 'construct vm: upload begin response has an invalid chunk size' >&2; exit "${EXIT_SERVICE}"; }
  api_request GET "/api/v1/media/uploads/$(urlencode "${upload_id}")"; expect_json object; status="${API_BODY}"
  missing="$(printf '%s' "${status}" | jq -r '.missing[]?')"; uploaded=0; total="${size}"
  while IFS= read -r idx; do
    [[ -n "${idx}" ]] || continue; is_uint "${idx}" || { printf '%s\n' 'construct vm: upload status contains an invalid chunk index' >&2; exit "${EXIT_SERVICE}"; }
    chunk="${WORK_DIR}/upload-chunk"
    expected=$((size - idx * chunk_size))
    (( expected > 0 )) || die 'upload status contains a chunk outside the local file'
    (( expected <= chunk_size )) || expected="${chunk_size}"
    dd if="${file}" of="${chunk}" bs="${chunk_size}" skip="${idx}" count=1 status=none || die 'cannot read or stage an upload chunk'
    actual="$(stat -c %s "${chunk}")" || die 'cannot inspect the staged upload chunk'
    [[ "${actual}" == "${expected}" ]] || die 'short read while staging an upload chunk; the local file changed'
    api_request PUT "/api/v1/media/uploads/$(urlencode "${upload_id}")/chunks/${idx}" "" "" "${chunk}" application/octet-stream
    api_is_success || api_fail
    rm "${chunk}"
    offset=$(((idx + 1) * chunk_size)); (( offset > total )) && offset="${total}"; uploaded="${offset}"
    if [[ "${quiet}" != true ]]; then printf 'uploaded %s of %s\n' "$(human_bytes "${uploaded}")" "$(human_bytes "${total}")" >&2; fi
  done <<<"${missing}"
  api_request POST "/api/v1/media/uploads/$(urlencode "${upload_id}")/complete" ""; expect_json object; complete="${API_BODY}"
  if [[ "${API_STATUS}" == 202 ]]; then job_id="$(printf '%s' "${complete}" | jq -r '.jobId // empty')"; [[ -n "${job_id}" ]] || exit "${EXIT_SERVICE}"; follow_job "${job_id}" 600 false; media_get "${media_id}"; else MEDIA_RESULT="${complete}"; fi
  api_request GET "/api/v1/media/uploads/$(urlencode "${upload_id}")"; expect_json object; upload_status="${API_BODY}"
  MEDIA_COMPOSITE="$(jq -cn --argjson upload "${upload_status}" --argjson media "${MEDIA_RESULT}" '{upload:$upload,media:$media}')"
}

media_acquire_impl() {
  local url="$1" role="$2" display_name="$3" sha256="$4" dedicated="$5" key="$6" no_wait="$7" quiet="$8"
  local body job_id media_id url_file
  # A URL query may contain a signed download credential. Feed it to jq through
  # a private file so it is absent from both curl's and jq's process argv.
  url_file="${WORK_DIR}/media-url.$$.${RANDOM}"
  printf '%s' "${url}" >"${url_file}"
  body="$(jq -cn --rawfile url "${url_file}" --arg name "${display_name}" --arg role "${role}" --arg sha "${sha256}" --arg dedicated "${dedicated}" '{url:$url,role:$role} + (if $name!="" then {name:$name} else {} end) + (if $sha!="" then {expectedSha256:$sha} else {} end) + (if $dedicated!="" then {dedicatedTo:$dedicated} else {} end)')"
  api_request POST /api/v1/media/acquire "${body}" "${key}"; expect_json object; MEDIA_ACCEPTED="${API_BODY}"; media_id="$(printf '%s' "${API_BODY}" | jq -r '.mediaId // empty')"; job_id="$(printf '%s' "${API_BODY}" | jq -r '.jobId // empty')"
  [[ -n "${media_id}" && -n "${job_id}" ]] || { printf '%s\n' 'construct vm: media acquire response omitted its ids' >&2; exit "${EXIT_SERVICE}"; }
  if [[ "${no_wait}" == true ]]; then MEDIA_RESULT="${MEDIA_ACCEPTED}"; return; fi
  follow_job "${job_id}" 10800 false; media_get "${media_id}"
  [[ "${quiet}" == true ]] || true
}

cmd_media_upload() {
  local file="${1:-}" role=install display_name="" sha256="" dedicated="" operation_id="" json=false key
  [[ -n "${file}" ]] || die 'media upload requires a file'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --role) shift; [[ $# -gt 0 ]] || die '--role requires a value'; role="$1";; --name) shift; [[ $# -gt 0 ]] || die '--name requires a value'; display_name="$1";; --sha256) shift; [[ $# -gt 0 ]] || die '--sha256 requires a value'; sha256="$1";; --dedicated-to) shift; [[ $# -gt 0 ]] || die '--dedicated-to requires a value'; dedicated="$1";; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; --json) json=true;; *) die "unknown media upload option: $1";; esac; shift; done
  [[ "${role}" == install || "${role}" == auxiliary ]] || die '--role must be install or auxiliary'; [[ -z "${sha256}" ]] || validate_sha256 "${sha256}"; key="$(operation_key "${operation_id}")"
  media_upload_impl "${file}" "${role}" "${display_name}" "${sha256,,}" "${dedicated}" "${key}" false
  if [[ "${json}" == true ]]; then printf '%s\n' "${MEDIA_COMPOSITE}"; else print_human_object "${MEDIA_RESULT}"; fi
}

cmd_media_acquire() {
  local url="${1:-}" role=install display_name="" sha256="" operation_id="" json=false no_wait=false key
  [[ -n "${url}" ]] || die 'media acquire requires a URL'; shift
  while [[ $# -gt 0 ]]; do case "$1" in --role) shift; [[ $# -gt 0 ]] || die '--role requires a value'; role="$1";; --name) shift; [[ $# -gt 0 ]] || die '--name requires a value'; display_name="$1";; --sha256) shift; [[ $# -gt 0 ]] || die '--sha256 requires a value'; sha256="$1";; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; --no-wait) no_wait=true;; --json) json=true;; *) die "unknown media acquire option: $1";; esac; shift; done
  [[ "${role}" == install || "${role}" == auxiliary ]] || die '--role must be install or auxiliary'; [[ -z "${sha256}" ]] || validate_sha256 "${sha256}"; key="$(operation_key "${operation_id}")"
  media_acquire_impl "${url}" "${role}" "${display_name}" "${sha256,,}" "" "${key}" "${no_wait}" false
  print_result "${MEDIA_RESULT}" "${json}" "${key}"
}

cmd_media_list() {
  local json=false; while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; *) die "unknown media list option: $1";; esac; shift; done
  api_request GET /api/v1/media; expect_json array
  if [[ "${json}" == true ]]; then printf '%s\n' "$(printf '%s' "${API_BODY}" | jq -c .)"; else printf '%-34s %-10s %-12s %10s %s\n' ID ROLE STATE SIZE NAME; printf '%s' "${API_BODY}" | jq -r '.[] | [.id,.role,.state,(.sizeBytes//"-"),.name] | @tsv' | while IFS=$'\t' read -r id role state size name; do printf '%-34s %-10s %-12s %10s %s\n' "$id" "$role" "$state" "$size" "$name"; done; fi
}

cmd_media_attach_detach() {
  local mode="$1" name="${2:-}"; shift 2 || true
  local install_set=false install="" aux_set=false aux="" boot_order="" json=false operation_id="" key body
  [[ -n "${name}" ]] || die "media ${mode} requires a VM name"
  while [[ $# -gt 0 ]]; do case "$1" in --install) install_set=true; if [[ "${mode}" == attach ]]; then shift; [[ $# -gt 0 ]] || die '--install requires a media id'; install="$1"; fi;; --aux) aux_set=true; if [[ "${mode}" == attach ]]; then shift; [[ $# -gt 0 ]] || die '--aux requires a media id'; aux="$1"; fi;; --boot-order) shift; [[ $# -gt 0 ]] || die '--boot-order requires a value'; boot_order="$1";; --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown media ${mode} option: $1";; esac; shift; done
  if [[ "${install_set}" == "${aux_set}" ]]; then die "media ${mode} requires exactly one of --install or --aux"; fi
  [[ -z "${boot_order}" ]] || validate_boot_order "${boot_order}"
  body="$(jq -cn --arg mode "${mode}" --arg install "${install}" --arg aux "${aux}" --arg boot "${boot_order}" --argjson installSet "${install_set}" --argjson auxSet "${aux_set}" '{} + (if $installSet then {installMediaId:(if $mode=="detach" then null else $install end)} else {} end) + (if $auxSet then {auxiliaryMediaId:(if $mode=="detach" then null else $aux end)} else {} end) + (if $boot!="" then {bootOrder:($boot|split(","))} else {} end)')"
  key="$(operation_key "${operation_id}")"; api_request PUT "/api/v1/vms/$(urlencode "${name}")/media" "${body}" "${key}"; expect_json array; print_result "$(jq -cn --argjson media "${API_BODY}" '{media:$media}')" "${json}" "${key}"
}

cmd_media_delete() {
  local id="${1:-}"; shift || true; local yes=false json=false operation_id="" key
  [[ -n "${id}" ]] || die 'media delete requires an id'; while [[ $# -gt 0 ]]; do case "$1" in --yes) yes=true;; --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown media delete option: $1";; esac; shift; done
  [[ "${yes}" == true ]] || die '--yes required: this permanently deletes the media file'; key="$(operation_key "${operation_id}")"; api_request DELETE "/api/v1/media/$(urlencode "${id}")" "" "${key}"
  api_is_success || api_fail; if [[ -n "${API_BODY}" ]] && printf '%s' "${API_BODY}" | jq -e 'type=="object"' >/dev/null 2>&1; then print_result "${API_BODY}" "${json}" "${key}"; elif [[ "${json}" == true ]]; then jq -cn --arg id "${id}" --arg operationKey "${key}" '{id:$id,deleted:true,operationKey:$operationKey}'; else printf 'deleted %s\n' "${id}"; fi
}

cmd_media() {
  local sub="${1:-}"; [[ -n "${sub}" ]] || die 'media requires list, upload, acquire, attach, detach or delete'; shift
  case "${sub}" in list) cmd_media_list "$@";; upload) cmd_media_upload "$@";; acquire) cmd_media_acquire "$@";; attach|detach) cmd_media_attach_detach "${sub}" "$@";; delete) cmd_media_delete "$@";; *) die "unknown media command: ${sub}";; esac
}

derive_child_name() {
  local owner key prefix
  owner="$(printf '%s' "${IDENTITY_JSON}" | jq -r '.owner // empty')"; key="$1"
  prefix="$(printf '%s' "${owner}:${key}:create" | sha256sum | cut -c1-4)"
  printf '%s-%s' "${INSTANCE_NAME}" "${prefix}"
}

cmd_create() {
  local iso_url="" iso_file="" install_media="" aux_file="" aux_media="" cpus="" ram_gb="" ram_mb="" disk_gb="" lifetime="" name="" preset="" generation="" secure_boot="" template="" tpm="" boot_order="" no_network=false no_start=false sha256="" operation_id="" no_wait=false json=false json_progress=false key child_name install_item aux_item="" fw body accepted job final_vm result source_count=0 aux_count=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --iso-url) shift; [[ $# -gt 0 ]] || die '--iso-url requires a URL'; iso_url="$1";; --iso) shift; [[ $# -gt 0 ]] || die '--iso requires a file'; iso_file="$1";; --media) shift; [[ $# -gt 0 ]] || die '--media requires an id'; install_media="$1";;
      --aux-iso) shift; [[ $# -gt 0 ]] || die '--aux-iso requires a file'; aux_file="$1";; --aux-media) shift; [[ $# -gt 0 ]] || die '--aux-media requires an id'; aux_media="$1";;
      --cpus) shift; [[ $# -gt 0 ]] || die '--cpus requires a value'; cpus="$1";; --ram-gb) shift; [[ $# -gt 0 ]] || die '--ram-gb requires a value'; ram_gb="$1";; --ram-mb) shift; [[ $# -gt 0 ]] || die '--ram-mb requires a value'; ram_mb="$1";; --disk-gb) shift; [[ $# -gt 0 ]] || die '--disk-gb requires a value'; disk_gb="$1";; --lifetime) shift; [[ $# -gt 0 ]] || die '--lifetime requires a value'; lifetime="$1";;
      --name) shift; [[ $# -gt 0 ]] || die '--name requires a value'; name="$1";; --preset) shift; [[ $# -gt 0 ]] || die '--preset requires a value'; preset="$1";; --generation) shift; [[ $# -gt 0 ]] || die '--generation requires a value'; generation="$1";; --secure-boot) shift; [[ $# -gt 0 ]] || die '--secure-boot requires a value'; secure_boot="$1";; --secure-boot-template) shift; [[ $# -gt 0 ]] || die '--secure-boot-template requires a value'; template="$1";; --tpm) shift; [[ $# -gt 0 ]] || die '--tpm requires a value'; tpm="$1";; --boot-order) shift; [[ $# -gt 0 ]] || die '--boot-order requires a value'; boot_order="$1";;
      --no-network) no_network=true;; --no-start) no_start=true;; --sha256) shift; [[ $# -gt 0 ]] || die '--sha256 requires a value'; sha256="$1";; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; --no-wait) no_wait=true;; --json) json=true;; --json-progress) json_progress=true;; -h|--help) usage; return;; *) die "unknown create option: $1";;
    esac; shift
  done
  [[ -n "${iso_url}" ]] && source_count=$((source_count + 1))
  [[ -n "${iso_file}" ]] && source_count=$((source_count + 1))
  [[ -n "${install_media}" ]] && source_count=$((source_count + 1))
  (( source_count == 1 )) || die 'create requires exactly one of --iso-url, --iso or --media'
  [[ -n "${aux_file}" ]] && aux_count=$((aux_count + 1))
  [[ -n "${aux_media}" ]] && aux_count=$((aux_count + 1))
  (( aux_count <= 1 )) || die 'use at most one of --aux-iso and --aux-media'
  is_positive "${cpus}" || die 'create requires --cpus N'; if [[ -n "${ram_gb}" && -n "${ram_mb}" ]]; then die 'use only one of --ram-gb and --ram-mb'; fi
  if [[ -n "${ram_gb}" ]]; then is_positive "${ram_gb}" || die '--ram-gb must be positive'; ram_mb=$((10#${ram_gb} * 1024)); fi
  is_positive "${ram_mb}" || die 'create requires --ram-gb G or --ram-mb M'; (( 10#${ram_mb} >= 512 && 10#${ram_mb} % 2 == 0 )) || die 'RAM must be at least 512 MiB and a multiple of 2 MiB'
  is_positive "${disk_gb}" || die 'create requires --disk-gb D'; [[ -n "${lifetime}" ]] || die 'create requires --lifetime L'; validate_lifetime "${lifetime}"
  [[ -z "${preset}" || "${preset}" == windows || "${preset}" == linux ]] || die '--preset must be windows or linux'; [[ -z "${secure_boot}" ]] || validate_on_off --secure-boot "${secure_boot}"; [[ -z "${tpm}" ]] || validate_on_off --tpm "${tpm}"; [[ -z "${boot_order}" ]] || validate_boot_order "${boot_order}"; [[ -z "${sha256}" ]] || validate_sha256 "${sha256}"
  require_create_allowed; key="$(operation_key "${operation_id}")"
  (( ${#key} <= 120 )) || die 'create --operation-id must be at most 120 characters to leave room for media sub-keys'
  child_name="${name:-$(derive_child_name "${key}")}"
  if [[ -n "${iso_url}" ]]; then media_acquire_impl "${iso_url}" install "" "${sha256,,}" "" "${key}:install" false true; install_item="${MEDIA_RESULT}"; install_media="$(printf '%s' "${install_item}" | jq -r '.id // empty')"
  elif [[ -n "${iso_file}" ]]; then media_upload_impl "${iso_file}" install "" "${sha256,,}" "${child_name}" "${key}:install" true; install_item="${MEDIA_RESULT}"; install_media="$(printf '%s' "${install_item}" | jq -r '.id // empty')"
  else media_get "${install_media}"; install_item="${MEDIA_RESULT}"; fi
  require_media_ready
  [[ -n "${install_media}" ]] || { printf '%s\n' 'construct vm: install media did not become ready' >&2; exit "${EXIT_SERVICE}"; }
  if [[ -n "${aux_file}" ]]; then media_upload_impl "${aux_file}" auxiliary "" "" "${child_name}" "${key}:aux" true; aux_item="${MEDIA_RESULT}"; aux_media="$(printf '%s' "${aux_item}" | jq -r '.id // empty')"; elif [[ -n "${aux_media}" ]]; then media_get "${aux_media}"; aux_item="${MEDIA_RESULT}"; fi
  [[ -z "${aux_item}" ]] || require_media_ready
  fw="$(firmware_json "${generation}" "${secure_boot}" "${template}" "${tpm}" "${boot_order}")"
  body="$(jq -cn --arg name "${child_name}" --arg cpus "${cpus}" --arg ram "${ram_mb}" --arg disk "${disk_gb}" --arg lifetime "${lifetime}" --arg install "${install_media}" --arg aux "${aux_media}" --arg preset "${preset}" --argjson firmware "${fw}" --argjson attach "$([[ "${no_network}" == true ]] && printf false || printf true)" --argjson start "$([[ "${no_start}" == true ]] && printf false || printf true)" '{name:$name,cpus:($cpus|tonumber),ramMb:($ram|tonumber),diskGb:($disk|tonumber),lifetime:$lifetime,media:({installMediaId:$install} + (if $aux!="" then {auxiliaryMediaId:$aux} else {} end)),network:{attach:$attach},start:$start} + (if $preset!="" then {preset:$preset} else {} end) + (if ($firmware|length)>0 then {firmware:$firmware} else {} end)')"
  api_request POST "/api/v1/vms/$(urlencode "${INSTANCE_NAME}")/children" "${body}" "${key}:create"; expect_json object; accepted="${API_BODY}"
  if [[ "${no_wait}" == true ]]; then job="${accepted}"; final_vm=null; else follow_job "$(printf '%s' "${accepted}" | jq -r '.jobId')" 10800 "${json_progress}"; job="${JOB_RESULT}"; api_request GET "/api/v1/vms/$(urlencode "${child_name}")"; expect_json object; final_vm="${API_BODY}"; fi
  result="$(jq -cn --arg operationKey "${key}" --argjson install "${install_item}" --argjson aux "${aux_item:-null}" --argjson job "${job}" --argjson vm "${final_vm}" '{operationKey:$operationKey,media:([$install] + (if $aux==null then [] else [$aux] end)),job:$job,vm:$vm}')"
  if [[ "${json_progress}" == true ]]; then return; elif [[ "${json}" == true ]]; then printf '%s\n' "${result}"; else print_human_object "${job}"; [[ "${final_vm}" == null ]] || print_human_object "${final_vm}"; fi
}

cmd_addresses() {
  local name="${1:-}"; shift || true; local json=false
  [[ -n "${name}" ]] || die 'addresses requires a VM name'; while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; *) die "unknown addresses option: $1";; esac; shift; done
  api_request GET "/api/v1/vms/$(urlencode "${name}")/addresses"; expect_json object
  if [[ "${json}" == true ]]; then printf '%s\n' "$(printf '%s' "${API_BODY}" | jq -c .)"; elif [[ "$(printf '%s' "${API_BODY}" | jq '.addresses|length')" -eq 0 ]]; then printf '%s\n' 'no address yet'; else printf '%s' "${API_BODY}" | jq -r '.addresses[] | "\(.address)\t\(.family)\tverified=\(.verified)"'; fi
}

forward_link() {
  printf '%s' "$1" | jq -r '
    def url_host:
      if . == "" then "localhost"
      elif startswith("[") then .
      elif contains(":") then "[" + . + "]"
      else . end;
    if (.url? // "") != "" then .url
    elif (.localPort? // 0) > 0 then
      "http://" + ((.hostLabel? // "") | url_host) + ":" + (.localPort|tostring) + "/"
    else empty end'
}

cmd_forward() {
  local name="${1:-}" port="${2:-}"; shift 2 || true
  local target=client label="" connect_port="" wait_sec=30 json=false operation_id="" key body id link deadline found
  [[ -n "${name}" && -n "${port}" ]] || die 'forward requires NAME PORT'; is_port "${port}" || die 'PORT must be 1–65535'
  while [[ $# -gt 0 ]]; do case "$1" in --to) shift; [[ $# -gt 0 ]] || die '--to requires a value'; target="$1";; --label) shift; [[ $# -gt 0 ]] || die '--label requires a value'; label="$1";; --connect-port) shift; [[ $# -gt 0 ]] || die '--connect-port requires a value'; connect_port="$1";; --wait) shift; [[ $# -gt 0 ]] || die '--wait requires a value'; wait_sec="$1";; --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown forward option: $1";; esac; shift; done
  [[ "${target}" == client || "${target}" == host ]] || die '--to must be client or host'; [[ -z "${connect_port}" ]] || is_port "${connect_port}" || die '--connect-port must be 1–65535'; is_uint "${wait_sec}" || die '--wait must be a whole number of seconds'
  key="$(operation_key "${operation_id}")"; body="$(jq -cn --arg port "${port}" --arg label "${label}" --arg target "${target}" --arg connect "${connect_port}" --arg via "${INSTANCE_NAME}" '{vmPort:($port|tonumber),label:$label,target:$target,via:$via} + (if $connect!="" then {connectPort:($connect|tonumber)} else {} end)')"
  api_request POST "/api/v1/vms/$(urlencode "${name}")/forwards" "${body}" "${key}"; expect_json object; found="${API_BODY}"; id="$(printf '%s' "${found}" | jq -r '.id // empty')"; link="$(forward_link "${found}")"
  deadline=$(( $(date +%s) + wait_sec ))
  while [[ -z "${link}" && "${target}" == client && $(date +%s) -lt ${deadline} ]]; do
    sleep "${POLL_INTERVAL}"; api_request GET "/api/v1/vms/$(urlencode "${name}")/forwards?via=$(urlencode "${INSTANCE_NAME}")"; expect_json array
    found="$(printf '%s' "${API_BODY}" | jq -c --arg id "${id}" '.[] | select(.id==$id)' | head -n 1)"; [[ -n "${found}" ]] || continue
    if [[ "$(printf '%s' "${found}" | jq -r '.status // empty')" == error ]]; then printf 'construct vm: %s\n' "$(printf '%s' "${found}" | jq -r '.message // "the client could not open the forward"')" >&2; exit "${EXIT_REFUSED}"; fi
    link="$(forward_link "${found}")"
  done
  if [[ -z "${link}" ]]; then printf 'construct vm: timed out waiting for forward %s\n' "${id:-unknown}" >&2; exit "${EXIT_TIMEOUT}"; fi
  if [[ "$(printf '%s' "${found}" | jq -r 'if .destination? and (.destination | has("verified")) then .destination.verified else true end')" != true ]]; then printf '%s\n' 'construct vm: warning: the child address is guest-reported and unverified' >&2; fi
  if [[ "${json}" == true ]]; then json_with_operation_key "${found}" "${key}"; else printf '%s\n' "${link}"; fi
}

console_close_now() {
  [[ -n "${CONSOLE_SESSION_ID}" ]] || return 0
  api_request DELETE "/api/v1/vms/$(urlencode "${CONSOLE_VM}")/console/sessions/$(urlencode "${CONSOLE_SESSION_ID}")"
  CONSOLE_SESSION_ID=""; CONSOLE_VM=""
}

require_mouse_applied() {
  if [[ "$(printf '%s' "$1" | jq -r '.applied // false')" != true ]]; then
    printf 'construct vm: mouse input unavailable%s\n' "$(printf '%s' "$1" | jq -r 'if .unavailable.fallback? then "; try " + .unavailable.fallback else "" end')" >&2
    exit "${EXIT_CONFLICT}"
  fi
}

cmd_console() {
  local name="${1:-}"; shift || true
  local mode="" value="" width="" height="" press_state=null operation_id="" key session base body response query="" file
  [[ -n "${name}" ]] || die 'console requires a VM name'
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --screenshot) mode=screenshot; shift; [[ $# -gt 0 ]] || die '--screenshot requires a file'; value="$1";; --width) shift; [[ $# -gt 0 ]] || die '--width requires a value'; width="$1";; --height) shift; [[ $# -gt 0 ]] || die '--height requires a value'; height="$1";;
      --type-stdin) mode=type-stdin;; --type-file) mode=type-file; shift; [[ $# -gt 0 ]] || die '--type-file requires a file'; value="$1";; --key) mode=key; shift; [[ $# -gt 0 ]] || die '--key requires a code'; value="$1";; --scancodes) mode=scancodes; shift; [[ $# -gt 0 ]] || die '--scancodes requires comma-separated bytes'; value="$1";; --ctrl-alt-del) mode=ctrl-alt-del;;
      --move) mode=move; shift; [[ $# -gt 0 ]] || die '--move requires X,Y'; value="$1";; --move-rel) mode=move-rel; shift; [[ $# -gt 0 ]] || die '--move-rel requires DX,DY'; value="$1";; --click) mode=click; shift; [[ $# -gt 0 ]] || die '--click requires a button'; value="$1";; --press) if [[ "${mode}" == key ]]; then press_state=true; else mode=press; shift; [[ $# -gt 0 ]] || die '--press requires a button'; value="$1"; fi;; --release) if [[ "${mode}" == key ]]; then press_state=false; else mode=release; shift; [[ $# -gt 0 ]] || die '--release requires a button'; value="$1"; fi;;
      *) die "unknown console option: $1";;
    esac; shift
  done
  [[ -n "${mode}" ]] || die 'console requires one screenshot, keyboard or mouse action'
  key="$(operation_key "${operation_id}")"; body='{}'; api_request POST "/api/v1/vms/$(urlencode "${name}")/console/sessions" "${body}" "${key}"; expect_json object; session="${API_BODY}"; CONSOLE_SESSION_ID="$(printf '%s' "${session}" | jq -r '.sessionId // empty')"; CONSOLE_VM="${name}"
  [[ -n "${CONSOLE_SESSION_ID}" ]] || { printf '%s\n' 'construct vm: console session response omitted sessionId' >&2; exit "${EXIT_SERVICE}"; }
  base="/api/v1/vms/$(urlencode "${name}")/console/sessions/$(urlencode "${CONSOLE_SESSION_ID}")"
  case "${mode}" in
    screenshot)
      [[ -n "${value}" ]] || die '--screenshot requires a file'; [[ -z "${width}" ]] || is_positive "${width}" || die '--width must be positive'; [[ -z "${height}" ]] || is_positive "${height}" || die '--height must be positive'
      if [[ -n "${width}" ]]; then query="?width=${width}"; [[ -n "${height}" ]] && query="${query}&height=${height}"; elif [[ -n "${height}" ]]; then query="?height=${height}"; fi
      api_request GET "${base}/screenshot${query}" "" "" "" application/json true; api_is_success || api_fail; mv "${API_RESPONSE_FILE}" "${value}" || die "cannot write screenshot: ${value}"; printf 'saved screenshot to %s\n' "${value}"; console_close_now
      ;;
    type-stdin|type-file)
      file="${WORK_DIR}/console-text.json"
      if [[ "${mode}" == type-file ]]; then [[ -r "${value}" && -f "${value}" ]] || die "cannot read text file: ${value}"; jq -Rs '{kind:"text",text:.}' <"${value}" >"${file}"; else jq -Rs '{kind:"text",text:.}' >"${file}"; fi
      api_request POST "${base}/keyboard" "" "" "${file}" application/json; expect_json object; response="${API_BODY}"; console_close_now; print_human_object "${response}"
      ;;
    key)
      is_uint "${value}" || die '--key must be a numeric key code'; body="$(jq -cn --arg code "${value}" --argjson press "${press_state}" '{kind:"key",keyCode:($code|tonumber),press:$press}')"; api_request POST "${base}/keyboard" "${body}"; expect_json object; response="${API_BODY}"; console_close_now; print_human_object "${response}"
      ;;
    scancodes)
      local scancode hex_values="" decimal scancode_values
      IFS=',' read -r -a scancode_values <<<"${value}"
      ((${#scancode_values[@]} > 0 && ${#scancode_values[@]} <= 64)) || die '--scancodes accepts 1–64 bytes'
      for scancode in "${scancode_values[@]}"; do
        [[ "${scancode}" =~ ^[0-9A-Fa-f]{2}$ ]] || die '--scancodes expects comma-separated two-digit hex bytes'
        decimal=$((16#${scancode}))
        hex_values="${hex_values}${hex_values:+,}${decimal}"
      done
      body="{\"kind\":\"scancodes\",\"scancodes\":[${hex_values}]}"; api_request POST "${base}/keyboard" "${body}"; expect_json object; response="${API_BODY}"; console_close_now; print_human_object "${response}"
      ;;
    ctrl-alt-del)
      api_request POST "${base}/keyboard" '{"kind":"ctrlAltDel"}'; expect_json object; response="${API_BODY}"; console_close_now; print_human_object "${response}"
      ;;
    move|move-rel)
      [[ "${value}" == *,* ]] || die "--${mode} expects two comma-separated integers"; local a="${value%%,*}" b="${value#*,}"; [[ "${a}" =~ ^-?[0-9]+$ && "${b}" =~ ^-?[0-9]+$ ]] || die "--${mode} expects integers"; if [[ "${mode}" == move ]]; then body="$(jq -cn --arg a "${a}" --arg b "${b}" '{kind:"moveAbsolute",x:($a|tonumber),y:($b|tonumber)}')"; else body="$(jq -cn --arg a "${a}" --arg b "${b}" '{kind:"moveRelative",dx:($a|tonumber),dy:($b|tonumber)}')"; fi
      api_request POST "${base}/mouse" "${body}"; expect_json object; response="${API_BODY}"; console_close_now; require_mouse_applied "${response}"; print_human_object "${response}"
      ;;
    click|press|release)
      is_positive "${value}" || die "--${mode} requires a numeric button"; body="$(jq -cn --arg kind "${mode}" --arg button "${value}" '{kind:$kind,button:($button|tonumber)}')"; api_request POST "${base}/mouse" "${body}"; expect_json object; response="${API_BODY}"; console_close_now; require_mouse_applied "${response}"; print_human_object "${response}"
      ;;
  esac
}

cmd_jobs() {
  local json=false; while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; *) die "unknown jobs option: $1";; esac; shift; done; api_request GET /api/v1/jobs; expect_json array
  if [[ "${json}" == true ]]; then printf '%s\n' "$(printf '%s' "${API_BODY}" | jq -c .)"; else printf '%-34s %-22s %-12s %-12s %s\n' ID KIND STATE PHASE VM; printf '%s' "${API_BODY}" | jq -r '.[] | [.id,.kind,.state,(.phase//"-"),(.vmName//.target//"-")] | @tsv' | while IFS=$'\t' read -r id kind state phase vm; do printf '%-34s %-22s %-12s %-12s %s\n' "$id" "$kind" "$state" "$phase" "$vm"; done; fi
}

cmd_wait() {
  local id="${1:-}"; shift || true; local timeout=600 json=false json_progress=false
  [[ -n "${id}" ]] || die 'wait requires a job id'; while [[ $# -gt 0 ]]; do case "$1" in --timeout) shift; [[ $# -gt 0 ]] || die '--timeout requires a value'; timeout="$1";; --json) json=true;; --json-progress) json_progress=true;; *) die "unknown wait option: $1";; esac; shift; done
  [[ "${json}" != true || "${json_progress}" != true ]] || die 'use only one of --json and --json-progress'; follow_job "${id}" "${timeout}" "${json_progress}"; [[ "${json_progress}" == true ]] || print_result "${JOB_RESULT}" "${json}"
}

cmd_cancel() {
  local id="${1:-}"; shift || true; local json=false operation_id="" key
  [[ -n "${id}" ]] || die 'cancel requires a job id'; while [[ $# -gt 0 ]]; do case "$1" in --json) json=true;; --operation-id) shift; [[ $# -gt 0 ]] || die '--operation-id requires a value'; operation_id="$1";; *) die "unknown cancel option: $1";; esac; shift; done; key="$(operation_key "${operation_id}")"; api_request POST "/api/v1/jobs/$(urlencode "${id}")/cancel" '{}' "${key}"; expect_json object; print_result "${API_BODY}" "${json}" "${key}"
}

if [[ $# -eq 0 ]]; then usage >&2; exit "${EXIT_USAGE}"; fi
case "$1" in -h|--help|help) usage; exit 0;; esac
if [[ "${!#}" == --help || "${!#}" == -h ]]; then usage; exit 0; fi
require_environment
discover_identity
command="$1"; shift
if [[ "${command}" == identity ]]; then cmd_identity "$@"; exit $?; fi
require_delegated
case "${command}" in
  create) cmd_create "$@";; list) cmd_list "$@";; inspect) cmd_inspect "$@";;
  start|shutdown|save|restart) cmd_lifecycle "${command}" "$@";; renew) cmd_renew "$@";; share) cmd_share "$@";; delete) cmd_delete "$@";; hardware) cmd_hardware "$@";;
  media) cmd_media "$@";; console) cmd_console "$@";; forward) cmd_forward "$@";; addresses) cmd_addresses "$@";; jobs) cmd_jobs "$@";; wait) cmd_wait "$@";; cancel) cmd_cancel "$@";;
  *) die "unknown command: ${command} (try: construct vm --help)";;
esac
