# shellcheck shell=bash
# construct-api.sh — the guest's way to the host service, for scripts that source it.
#
#   cfg_resolve <explicit> <key> <fallback>   explicit value > config.env > fallback
#   api_begin                                 reads the VM token into a 0600 header file
#   api <method> <path> <body file|""> <answer file>
#                                             one call; status in API_STATUS ("000": no answer)
#   api_end                                   removes the work directory
#
# config.env is looked up key by key, never sourced, so it cannot set this
# script's variables. The token goes to curl through a header file (-H @file),
# a body through --data-binary @file: neither is ever an argument.
#
# Only the profile commands (construct-project.sh) use this so far; the other
# guest scripts still carry their own copy of cfg_resolve.

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
CURL="${CONSTRUCT_CURL:-curl}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"
API_STATUS=""
API_ERROR=""
API_WORK=""

cfg_resolve() {
  local explicit="$1" key="$2" fallback="$3" raw=""
  if [[ -n "${explicit}" ]]; then printf '%s' "${explicit}"; return 0; fi
  if [[ -f "${CONFIG_FILE}" ]]; then
    raw="$(sed -n "s/^${key}=//p" "${CONFIG_FILE}" 2>/dev/null | head -1 || true)"
    if [[ ${#raw} -ge 2 && "${raw}" == \'*\' ]]; then
      raw="${raw:1:${#raw}-2}"
      raw="${raw//\'\\\'\'/\'}"
    fi
  fi
  printf '%s' "${raw:-${fallback}}"
}

SERVICE_URL="$(cfg_resolve "${CONSTRUCT_SERVICE_URL:-}" CONSTRUCT_SERVICE_URL "")"
SERVICE_URL="${SERVICE_URL%/}"
# shellcheck disable=SC2034 # used by the scripts that source this
INSTANCE_NAME="$(cfg_resolve "${CONSTRUCT_INSTANCE_NAME:-}" CONSTRUCT_INSTANCE_NAME \
  "$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || echo vm)")"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
[[ "${API_TIMEOUT}" =~ ^[0-9]{1,4}$ ]] && (( 10#${API_TIMEOUT} >= 1 )) || API_TIMEOUT=20

# True when this VM is managed by a host service and has a token for it.
api_available() {
  [[ -n "${SERVICE_URL}" && -r "${VM_TOKEN_FILE}" ]]
}

api_begin() {
  local token
  [[ -z "${API_WORK}" ]] || return 0
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  [[ -n "${token}" ]] || return 1
  API_WORK="$(mktemp -d)" || return 1
  ( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${API_WORK}/headers" )
}

api_end() {
  [[ -n "${API_WORK}" ]] && rm -rf -- "${API_WORK}"
  API_WORK=""
}

api() {
  local method="$1" path="$2" body="$3" out="$4"
  local -a args=(--silent --show-error --max-time "${API_TIMEOUT}"
    -H "@${API_WORK}/headers" -H 'Accept: application/json' -X "${method}"
    -o "${out}" -w '%{http_code}')
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  if [[ -n "${body}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@${body}"); fi
  rm -f -- "${out}"
  API_STATUS="$("${CURL}" "${args[@]}" "${SERVICE_URL}${path}" </dev/null 2>"${API_WORK}/stderr" || true)"
  API_STATUS="${API_STATUS:0:3}"
  [[ "${API_STATUS}" =~ ^[0-9]{3}$ ]] || API_STATUS="000"
  # shellcheck disable=SC2034 # for the caller's messages
  API_ERROR="$(head -n 1 "${API_WORK}/stderr" 2>/dev/null | tr '\000-\037\177' ' ' | head -c 200 || true)"
}

# A field of an RFC 7807 problem document, one line, capped.
api_problem() {
  jq -r 'if type == "object" then (.detail // .title // "") else "" end | tostring
    | gsub("[[:cntrl:]]"; " ") | .[0:300]' "$1" 2>/dev/null || true
}

urlencode() { jq -rn --arg v "$1" '$v | @uri'; }
