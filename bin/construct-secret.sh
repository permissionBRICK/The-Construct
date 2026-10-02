#!/usr/bin/env bash
# construct-secret.sh — implementation of `construct secret` (see `construct secret --help`).
#
# The guest half of the key vault. Secrets live in the Construct Companion on the
# user's PC; this VM never sees one until the user approves a lease for it in a
# Companion popup. Every command is one request/response round trip. Two modes,
# chosen by CONSTRUCT_SERVICE_URL (environment > /etc/construct/config.env, the
# same lookup as `construct expose`):
#
# local (empty)  a root-only spool on tmpfs, which the Companion drains over its
#                SSH connection:
#   requests/<id>.json    published here (write .tmp.<id>, then rename)
#                         the Companion claims it by renaming it away = "pickup"
#   responses/<id>.json   published by the Companion; read and deleted here
#
# hosted (set)   the host service keeps the vault (docs/plans/key-vault-hosted.md):
#   POST {url}/api/v1/vms/{instance}/vault/requests        the same request document
#        200 = the response document, 202 {id} = wait for the user:
#   GET  {url}/api/v1/vms/{instance}/vault/requests/{id}?wait=25   long poll
#        200 = the response document, 204 = still pending, 404 = expired
#   The VM token (/etc/construct/vm-token) reaches curl through a 0600 header
#   file, the request and the answer through 0600 files in a private 0700
#   directory under the spool, removed on exit. The hidden `_scrub` command runs
#   the host's scrub jobs (the heartbeat starts it, see construct-idle-report.sh).
#
# Usage:
#   construct secret list   [--json]
#   construct secret status [--json]
#   construct secret request <name>... [--uses N] [--for DURATION] [--reason TEXT]
#   construct secret get     <name> [--username | --json] [--reason TEXT]
#   construct secret release <name>... | --all
#   construct secret add     <name> --description TEXT [--username USER] [--for DURATION]
#                            [--file PATH] [--replace]
#   construct secret delete  <name> [--reason TEXT]
#   (every command also takes --wait SEC; aliases: ls, relinquish, rm)
#
# Exit codes (documented in bin/construct's header too):
#   0   ok
#   1   usage or local error (bad name/duration, empty or oversized secret, no jq)
#   6   no Companion picked the request up within CONSTRUCT_VAULT_PICKUP_SEC;
#       hosted: the host service could not be reached (network, TLS, HTTP 5xx)
#   7   the user denied it, or did not answer before the deadline
#   8   the Companion answered `error` / `invalid`, or something unreadable;
#       hosted: the same from the host service, or it refused this VM (401/403)
#   9   no such secret (`notFound`)
#   10  a secret with that name already exists (`exists`; use add --replace)
#   11  the vault is locked for this VM (`locked`, hosted only): the user's PC
#       has to start or connect the VM first
#
# A secret value never appears in argv (world-readable in `ps`) and never in a
# shell variable that a command substitution would have trimmed: `add` streams
# it stdin → base64 → jq through pipes, `get` decodes it straight to stdout.
set -euo pipefail

SPOOL="${CONSTRUCT_VAULT_SPOOL:-/run/construct/vault}"
REQ_DIR="${SPOOL}/requests"
RESP_DIR="${SPOOL}/responses"
PICKUP_SEC="${CONSTRUCT_VAULT_PICKUP_SEC:-15}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
CURL="${CONSTRUCT_CURL:-curl}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"
# The guest scripts the Companion runs over SSH; hosted VMs run them for the
# host service's scrub jobs, rendered from this VM's Construct checkout.
GUEST_SCRIPTS="${CONSTRUCT_REPO_DIR:-/opt/construct/repo}/companion/src/Construct.Companion.Core/Vault/GuestScripts"
# VaultProtocol.ScanRoots and .ScanMaxFileBytes (companion/src/Construct.Companion.Core/Vault).
SCAN_ROOTS="${CONSTRUCT_VAULT_SCAN_ROOTS:-/root /home /tmp /var/tmp /var/log /etc /opt /srv}"
SCAN_MAX_BYTES=268435456

MAX_TEXT=300
MAX_SOURCE=60
MAX_NAMES=20
MAX_SECRET=32768
POLL_SEC=0.2
# Hosted mode: the host holds a pending request for at most this long per GET,
# and a long poll survives this many failed calls in a row (a network blip while
# the user is deciding) before it gives up.
LONG_POLL_SEC=25
POLL_ATTEMPTS=3

EXIT_NO_COMPANION=6
EXIT_DENIED=7
EXIT_COMPANION=8
EXIT_NOT_FOUND=9
EXIT_EXISTS=10
EXIT_LOCKED=11

# Who answers, for messages: the host service in hosted mode (resolve_service).
PEER="the Construct Companion"

# Everything this script creates (request files, its temp file) is private.
umask 077

die() { printf 'construct secret: %s\n' "$*" >&2; exit 1; }
warn() { printf 'construct secret: %s\n' "$*" >&2; }
fail() { local code="$1"; shift; printf 'construct secret: %s\n' "$*" >&2; exit "${code}"; }

usage() {
  cat <<'USAGE'
Usage: construct secret list   [--json]
       construct secret status [--json]
       construct secret request <name>... [--uses N] [--for DURATION] [--reason TEXT]
       construct secret get     <name> [--username | --json] [--reason TEXT]
       construct secret release <name>... | --all
       construct secret add     <name> --description TEXT [--username USER]
                                [--for DURATION] [--file PATH] [--replace]
       construct secret delete  <name> [--reason TEXT]
Every command also takes --wait SEC.

The key vault lives in the Construct Companion on the user's PC. This VM sees a
secret only while it holds a lease the user approved in a Companion popup.
On a VM that runs on a host service, requests go to the host service instead,
and the user approves them from their PC or a paired phone.

Commands:
  list, ls           Every secret in the vault (names and descriptions, never
                     values) and this VM's access to each.
  status             Only the secrets this VM holds a lease for right now.
  request            Ask for leases on one or more secrets: ONE popup for all.
                     Without --uses/--for a lease lasts one hour.
  get                Print the value on stdout, byte-exact, no newline added.
                     Uses up one use of the lease; without a lease the user is
                     asked first.
  release, relinquish
                     End leases. The Companion then scrubs the values from this VM.
  add                Store a new secret. The value is read from stdin (one
                     trailing newline is dropped) or from --file (stored
                     byte-exact), never from an argument. This VM gets a lease.
  delete, rm         Ask the user to delete a secret from the vault.

Options:
  --uses N             request: the lease ends after N gets (1-1000)
  --for DURATION       request/add: lease length, e.g. 90s, 45m, 2h, 1d or plain
                       seconds (60 s to 24 h)
  --reason TEXT        shown to the user in the approval popup
  --wait SEC           how long to wait for the user (default 600 for request,
                       get, delete and add --replace; 60 otherwise)
  --username           get: print the username instead of the value
  --json               get: print {"name","username","secret"} on one line;
                       list/status: the raw items array
  --description TEXT   add: what the secret is (required)
  --username USER      add: a username stored with the secret
  --file PATH          add: read the value from PATH ("-" = stdin, byte-exact)
  --replace            add: overwrite an existing secret (the user must approve)
  --all                release: every lease this VM holds

How to use it (agents):
  * Request everything a task needs up front, in one call, so the user approves
    once at the start instead of being interrupted halfway:
      construct secret request github-token npm-token --for 2h --reason "publish v1.4"
  * Use a value where it is consumed so it never lands in a transcript:
      GH_TOKEN="$(construct secret get github-token)" gh release create v1.4
      construct secret get registry-pass | docker login -u ci --password-stdin registry.example.com
    Never echo a secret, never write one into a file or a repository.
  * Need the username too? One `get --json` costs one use; --username plus a
    plain get costs two.
  * Release when done:  construct secret release --all
    The Companion then scrubs the values from this VM: agent logs are redacted
    automatically, any other file that contains one needs the user's decision.

Exit codes:
  0 ok · 1 usage or local error · 6 no Companion connected (nobody was asked)
  7 denied, or no answer within --wait · 8 the Companion reported an error
  9 no such secret · 10 the name is taken (add --replace overwrites)
  11 the vault is locked for this VM: start or connect it from the user's PC
On a VM of a host service, 6 means the host service could not be reached and 8
that it reported an error.
USAGE
}

# ── small helpers ────────────────────────────────────────────────────────────

# Strip control characters -- the Companion parses one request per line and shows
# these strings in a popup -- squeeze whitespace, trim, cap the length. Same rule
# as `construct notify`.
clean_text() {
  local text="$1" max="$2"
  text="$(printf '%s' "${text}" | tr '\000-\037\177' '   ' | tr -s ' ')"
  text="${text#"${text%%[![:space:]]*}"}"
  text="${text%"${text##*[![:space:]]}"}"
  printf '%s' "${text:0:${max}}"
}

is_name() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]]; }

# Milliseconds since the epoch, into NOW_MS (no subshell: the wait loops call it
# five times a second).
now_ms() {
  if [[ -n "${EPOCHREALTIME:-}" ]]; then
    local t="${EPOCHREALTIME//[!0-9]/}"   # "1730000000.123456" -> microseconds
    NOW_MS=$(( 10#${t} / 1000 ))
  else
    NOW_MS="$(date +%s%3N 2>/dev/null || true)"
    [[ "${NOW_MS}" =~ ^[0-9]+$ ]] || NOW_MS="$(date +%s)000"
  fi
}

# Epoch milliseconds -> local HH:MM.
hhmm() { printf '%(%H:%M)T' "$(( $1 / 1000 ))"; }

# "1 use" / "3 uses"
uses_text() { if [[ "$1" == 1 ]]; then printf '1 use'; else printf '%s uses' "$1"; fi; }

# DURATION -> seconds in DURATION_SEC. 90s, 45m, 2h, 1d or plain seconds,
# 60 s ... 24 h (the lease range the Companion accepts).
parse_duration() {
  local raw="$1" n unit
  [[ "${raw}" =~ ^([0-9]{1,6})([sSmMhHdD]?)$ ]] \
    || die "invalid duration '${raw}' (use e.g. 90s, 45m, 2h, 1d or plain seconds)"
  n=$(( 10#${BASH_REMATCH[1]} ))
  unit="${BASH_REMATCH[2],,}"
  case "${unit}" in
    m) n=$(( n * 60 )) ;;
    h) n=$(( n * 3600 )) ;;
    d) n=$(( n * 86400 )) ;;
  esac
  (( n >= 60 && n <= 86400 )) || die "duration '${raw}' is out of range (60 s to 24 h)"
  DURATION_SEC="${n}"
}

# A whole number in [lo, hi], or die naming the option.
parse_int() {
  local raw="$1" lo="$2" hi="$3" what="$4"
  if [[ ! "${raw}" =~ ^[0-9]{1,6}$ ]] || (( 10#${raw} < lo || 10#${raw} > hi )); then
    die "${what} must be a whole number from ${lo} to ${hi}"
  fi
  printf '%s' "$(( 10#${raw} ))"
}

# Copy stdin to stdout, dropping ONE trailing "\n" and a "\r" right before it,
# so `echo "$x" | construct secret add` stores exactly $x. Byte-exact otherwise:
# bash variables cannot hold NUL, so the stream is cut at NULs and re-joined;
# the value stays in this process (never argv, never a file).
strip_final_newline() {
  local LC_ALL=C chunk=""
  while IFS= read -r -d '' chunk; do
    printf '%s\0' "${chunk}"
  done
  if [[ "${chunk}" == *$'\n' ]]; then
    chunk="${chunk%$'\n'}"
    chunk="${chunk%$'\r'}"
  fi
  printf '%s' "${chunk}"
}

# ── the spool ────────────────────────────────────────────────────────────────

REQ_ID=""
RESP=""
WORK=""        # hosted mode: the private work directory under the spool
CURL_PID=""    # hosted mode: the curl call in flight

# Remove what this run left behind: its unpublished temp file, its request if
# nobody claimed it, and the answer (which, for `get`, holds the secret). In
# hosted mode: the curl in flight and the work directory (token header, request
# body, answer).
cleanup() {
  if [[ -n "${CURL_PID}" ]]; then kill "${CURL_PID}" 2>/dev/null || true; fi
  if [[ -n "${WORK}" ]]; then rm -rf -- "${WORK}" 2>/dev/null || true; fi
  [[ -n "${REQ_ID}" ]] || return 0
  rm -f -- "${REQ_DIR}/.tmp.${REQ_ID}" "${REQ_DIR}/${REQ_ID}.json" \
    "${RESP_DIR}/${REQ_ID}.json" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

ensure_spool() {
  # 0700 all the way: answers carry secret values. The umask is relaxed for
  # this one call so a missing /run/construct parent is still traversable for
  # the other spools under it (notify is 1777).
  if [[ ! -d "${SPOOL}" || ! -d "${REQ_DIR}" || ! -d "${RESP_DIR}" ]]; then
    ( umask 022; install -d -m 0700 "${SPOOL}" "${REQ_DIR}" "${RESP_DIR}" ) 2>/dev/null \
      || die "cannot create the key vault spool ${SPOOL} (run as root)"
  fi
  [[ -w "${REQ_DIR}" && -r "${RESP_DIR}" ]] || die "cannot use the key vault spool ${SPOOL} (run as root)"
}

# jq program for the request document. Empty strings and nulls are left out
# (the contract allows absent optional fields). The $names in these programs are
# jq variables, hence the single quotes.
# shellcheck disable=SC2016
REQUEST_JQ='{v: 1, id: $id, ts: $ts, op: $op, names: $ARGS.positional,
  uses: $uses, ttl: $ttl, all: $all, replace: $replace,
  reason: $reason, description: $description, username: $username,
  deadline: $deadline, source: $source}'
DROP_EMPTY_JQ='with_entries(select(.value != null and .value != ""))'

# The secret arrives as base64 in $s. Its decoded size follows from the length
# and the padding; jq exits 21 (empty) or 22 (too large) so no shell variable
# ever has to hold it to check.
# shellcheck disable=SC2016
ADD_JQ='($s | length) as $len
  | (if ($s | endswith("==")) then 2 elif ($s | endswith("=")) then 1 else 0 end) as $pad
  | (($len / 4) * 3 - $pad) as $bytes
  | if $bytes <= 0 then "" | halt_error(21)
    elif $bytes > $max then "" | halt_error(22)
    else ('"${REQUEST_JQ}"' + {secret: $s}) | '"${DROP_EMPTY_JQ}"' end'

# The secret for `add`, on stdout. --file is stored byte-exact; stdin loses one
# trailing newline. Reads at most a few bytes past the cap, so a runaway input
# cannot fill memory; the jq size check then refuses it.
secret_stream() {
  if [[ -z "${OPT_FILE}" ]]; then
    head -c $(( MAX_SECRET + 3 )) | strip_final_newline
  elif [[ "${OPT_FILE}" == "-" ]]; then
    head -c $(( MAX_SECRET + 1 ))
  else
    head -c $(( MAX_SECRET + 1 )) -- "${OPT_FILE}"
  fi
}

# The request id, its timestamp and deadline (REQ_ID, REQ_TS, REQ_DEADLINE).
start_request() {
  now_ms
  REQ_TS="${NOW_MS}"
  REQ_DEADLINE=$(( REQ_TS + $1 * 1000 ))
  printf -v REQ_ID '%s-%s-%04x%04x' "${REQ_TS}" "$$" "${RANDOM}" "${RANDOM}"
}

# Write the request document for op $1 into the private file $2; $3 names the
# directory in error messages. Names in R_NAMES; other fields in R_* globals.
write_request() {
  local op="$1" tmp="$2" where="$3" source
  local -a jq_args=() st=()
  source="$(clean_text "${SUDO_USER:-$(id -un 2>/dev/null || echo agent)}@$(hostname -s 2>/dev/null || echo vm)" "${MAX_SOURCE}")"

  jq_args=(--arg id "${REQ_ID}" --argjson ts "${REQ_TS}" --arg op "${op}"
    --argjson uses "${R_USES:-null}" --argjson ttl "${R_TTL:-null}"
    --argjson all "${R_ALL}" --argjson replace "${R_REPLACE}"
    --arg reason "${R_REASON}" --arg description "${R_DESCRIPTION}" --arg username "${R_USERNAME}"
    --argjson deadline "${REQ_DEADLINE}" --arg source "${source}")

  if [[ "${op}" == "add" ]]; then
    # Pipes only: the value never becomes an argument of base64 or jq.
    set +e
    secret_stream | base64 -w0 \
      | jq -cn --rawfile s /dev/stdin --argjson max "${MAX_SECRET}" "${jq_args[@]}" \
          "${ADD_JQ}" --args "${R_NAMES[@]}" >"${tmp}"
    st=("${PIPESTATUS[@]}")
    set -e
    if (( st[0] != 0 )); then rm -f "${tmp}"; die "cannot read the secret${OPT_FILE:+ from ${OPT_FILE}}"; fi
    if (( st[2] == 21 )); then rm -f "${tmp}"; die "the secret is empty (pipe it in on stdin, or use --file)"; fi
    if (( st[2] == 22 )); then rm -f "${tmp}"; die "the secret is larger than 32 KiB"; fi
    if (( st[1] != 0 || st[2] != 0 )); then rm -f "${tmp}"; die "cannot build the request"; fi
  else
    jq -cn "${jq_args[@]}" "${REQUEST_JQ} | ${DROP_EMPTY_JQ}" --args "${R_NAMES[@]}" >"${tmp}" 2>/dev/null \
      || { rm -f "${tmp}"; die "cannot write to ${where} (run as root?)"; }
  fi
}

# Publish one request, wait for the Companion (or the host service) and its
# answer. On return RESP is the answer file with status "ok"; every other
# outcome exits with its code.
#   $1 op   $2 wait seconds   names in R_NAMES; other fields in R_* globals
round_trip() {
  local rc
  if [[ -n "${SERVICE_URL}" ]]; then
    hosted_round_trip "$@"
  else
    spool_round_trip "$@"
  fi

  # Hosted: the answer may carry the host's id for the request instead of ours.
  rc=0
  jq -e --arg id "${REQ_ID}" --arg alt "${HOST_ID:-${REQ_ID}}" \
    'type == "object" and ((.id // $id) | . == $id or . == $alt)' "${RESP}" >/dev/null 2>&1 || rc=$?
  (( rc == 0 )) || fail "${EXIT_COMPANION}" "${PEER} sent an unreadable answer"

  local status message locked_text="the key vault is locked for this VM: start or connect it from the user's PC"
  status="$(resp_str '.status')"
  message="$(resp_str '.message')"
  case "${status}" in
    ok) return 0 ;;
    denied) fail "${EXIT_DENIED}" "${message:-the user denied the request}" ;;
    notFound) fail "${EXIT_NOT_FOUND}" "${message:-no such secret in the key vault}" ;;
    exists) fail "${EXIT_EXISTS}" "${message:-a secret with that name already exists (add --replace overwrites it)}" ;;
    locked) fail "${EXIT_LOCKED}" "${message:-${locked_text}}" ;;
    error|invalid) fail "${EXIT_COMPANION}" "${message:-${PEER} could not handle the request (${status})}" ;;
    *) fail "${EXIT_COMPANION}" "${PEER} sent an unknown status '${status}'" ;;
  esac
}

spool_round_trip() {
  local op="$1" wait_sec="$2" tmp req
  ensure_spool

  start_request "${wait_sec}"
  tmp="${REQ_DIR}/.tmp.${REQ_ID}"
  req="${REQ_DIR}/${REQ_ID}.json"
  RESP="${RESP_DIR}/${REQ_ID}.json"
  write_request "${op}" "${tmp}" "${REQ_DIR}"
  # Publish with a rename: the Companion must never read a half-written request.
  mv -f "${tmp}" "${req}" 2>/dev/null || { rm -f "${tmp}"; die "cannot publish to ${REQ_DIR}"; }

  # Pickup: the Companion claims a request by renaming it away. If it is still
  # here after the pickup window (and no answer came), nothing is draining the spool.
  local pickup_by=$(( REQ_TS + PICKUP_SEC * 1000 ))
  while [[ -e "${req}" && ! -f "${RESP}" ]]; do
    now_ms
    if (( NOW_MS >= pickup_by )); then
      # Take it back with a rename, not rm: if the rename fails, the Companion
      # claimed it this very instant and an answer is on its way after all.
      if mv -f "${req}" "${tmp}" 2>/dev/null; then
        rm -f "${tmp}"
        fail "${EXIT_NO_COMPANION}" "no Construct Companion is connected to this VM — the key vault needs the Companion running on the user's PC"
      fi
      break
    fi
    sleep "${POLL_SEC}"
  done

  # The answer, until the deadline (the Companion closes its popup then, too).
  while [[ ! -f "${RESP}" ]]; do
    now_ms
    if (( NOW_MS >= REQ_DEADLINE )); then
      [[ -f "${RESP}" ]] && break
      fail "${EXIT_DENIED}" "no answer from the user within ${wait_sec} s (the request was shown in the Construct Companion; retry, or raise --wait)"
    fi
    sleep "${POLL_SEC}"
  done
}

# ── hosted mode: the host service ────────────────────────────────────────────

SERVICE_URL=""
INSTANCE_NAME=""
CA_FILE=""
AUTH_SCHEME=""
HOST_ID=""
API_STATUS=""
API_ERROR=""

# Narrow key lookup in config.env, NOT `source` (same discipline and precedence
# as construct-expose.sh): explicit non-empty environment value > saved value >
# built-in default. config-set.sh writes values outside its safe set as '...'.
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

resolve_service() {
  SERVICE_URL="$(cfg_resolve "${CONSTRUCT_SERVICE_URL:-}" CONSTRUCT_SERVICE_URL "")"
  SERVICE_URL="${SERVICE_URL%/}"
  [[ -n "${SERVICE_URL}" ]] || return 0
  PEER="the host service"
  INSTANCE_NAME="$(cfg_resolve "${CONSTRUCT_INSTANCE_NAME:-}" CONSTRUCT_INSTANCE_NAME \
    "$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || echo vm)")"
  CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
  AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
  if [[ "${API_TIMEOUT}" =~ ^[0-9]{1,4}$ ]] && (( 10#${API_TIMEOUT} >= 1 )); then
    API_TIMEOUT=$(( 10#${API_TIMEOUT} ))
  else
    API_TIMEOUT=20
  fi
}

vault_path() { printf '/api/v1/vms/%s/vault' "${INSTANCE_NAME}"; }

# The spool directory itself (0700), for hosted mode's work directory and lock.
ensure_spool_base() {
  if [[ ! -d "${SPOOL}" ]]; then
    ( umask 022; install -d -m 0700 "${SPOOL}" ) 2>/dev/null \
      || die "cannot create the key vault spool ${SPOOL} (run as root)"
  fi
}

# The private work directory (0700, on the spool's tmpfs) and the Authorization
# header file in it (0600). The token is read into this process only; it never
# becomes an argument.
hosted_begin() {
  local token
  [[ -z "${WORK}" ]] || return 0
  [[ -r "${VM_TOKEN_FILE}" ]] \
    || fail "${EXIT_COMPANION}" "no VM token at ${VM_TOKEN_FILE}: this VM cannot talk to the host service (re-provision the VM)"
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  [[ -n "${token}" ]] || fail "${EXIT_COMPANION}" "the VM token file ${VM_TOKEN_FILE} is empty (re-provision the VM)"
  ensure_spool_base
  WORK="$(mktemp -d "${SPOOL}/http.XXXXXX" 2>/dev/null)" \
    || die "cannot use the key vault spool ${SPOOL} (run as root)"
  printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${WORK}/headers"
}

# One call to the host service: api <method> <path> <body file or ""> <answer file> <max seconds>.
# Status in API_STATUS ("000" = no HTTP answer), curl's complaint in API_ERROR.
# The token goes in through the header file, a body through --data-binary @file
# and the answer out through -o file: neither a token nor a secret is ever an
# argument. curl runs in the background so a signal during a 25-second long
# poll is handled at once (the cleanup trap kills it).
api() {
  local method="$1" path="$2" body="$3" out="$4" max_time="$5"
  local -a args=(--silent --show-error --fail-with-body --max-time "${max_time}"
    -H "@${WORK}/headers" -H 'Accept: application/json' -X "${method}"
    -o "${out}" -w '%{http_code}')
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  if [[ -n "${body}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@${body}"); fi
  rm -f -- "${out}" "${WORK}/status" "${WORK}/stderr"
  "${CURL}" "${args[@]}" "${SERVICE_URL}${path}" </dev/null >"${WORK}/status" 2>"${WORK}/stderr" &
  CURL_PID=$!
  wait "${CURL_PID}" || true
  CURL_PID=""
  API_STATUS="$(head -c 3 "${WORK}/status" 2>/dev/null || true)"
  [[ "${API_STATUS}" =~ ^[0-9]{3}$ ]] || API_STATUS="000"
  API_ERROR="$(head -n 1 "${WORK}/stderr" 2>/dev/null | tr '\000-\037\177' ' ' | head -c 200 || true)"
}

# A field of an RFC 7807 problem document, one line, capped.
problem_field() {
  jq -r --arg k "$2" 'if type == "object" then (.[$k] // "") else "" end | tostring
    | gsub("[[:cntrl:]]"; " ") | .[0:200]' "$1" 2>/dev/null || true
}

# The host service did not give the answer we needed: exit with the code that
# says why. $1 = the answer file (a problem document, perhaps).
api_fail() {
  local title detail text
  title="$(problem_field "$1" title)"
  detail="$(problem_field "$1" detail)"
  text="${title}"
  if [[ -n "${detail}" && "${detail}" != "${title}" ]]; then text="${text:+${text}: }${detail}"; fi
  case "${API_STATUS}" in
    000)
      fail "${EXIT_NO_COMPANION}" "the host service could not be reached at ${SERVICE_URL}${API_ERROR:+ (${API_ERROR})}" ;;
    5[0-9][0-9])
      fail "${EXIT_NO_COMPANION}" "the host service could not be reached at ${SERVICE_URL} (HTTP ${API_STATUS}${text:+: ${text}})" ;;
    401)
      fail "${EXIT_COMPANION}" "the host service rejected this VM's token (HTTP 401${title:+: ${title}}); re-provision the VM to install a fresh one" ;;
    403)
      fail "${EXIT_COMPANION}" "the host service refused the request (HTTP 403${title:+: ${title}})" ;;
    *)
      fail "${EXIT_COMPANION}" "the host service could not handle the request (HTTP ${API_STATUS}${text:+: ${text}})" ;;
  esac
}

hosted_round_trip() {
  local op="$1" wait_sec="$2" body
  hosted_begin
  start_request "${wait_sec}"
  body="${WORK}/request.json"
  RESP="${WORK}/response.json"
  write_request "${op}" "${body}" "${WORK}"
  api POST "$(vault_path)/requests" "${body}" "${RESP}" "${API_TIMEOUT}"
  # An add's body carries the secret: gone as soon as it is sent.
  rm -f -- "${body}"
  case "${API_STATUS}" in
    200) return 0 ;;
    202) ;;
    *) api_fail "${RESP}" ;;
  esac
  HOST_ID="$(jq -r 'if type == "object" then (.id // "") else "" end | tostring' "${RESP}" 2>/dev/null || true)"
  [[ "${HOST_ID}" =~ ^[A-Za-z0-9._~-]{1,128}$ ]] \
    || fail "${EXIT_COMPANION}" "the host service accepted the request without a usable id"
  long_poll "${wait_sec}"
}

# Wait for the user's answer: GET …/requests/{id}?wait=N until 200 or the deadline.
long_poll() {
  local wait_sec="$1" remaining hold started failures=0
  while :; do
    now_ms
    remaining=$(( REQ_DEADLINE - NOW_MS ))
    if (( remaining <= 0 )); then
      fail "${EXIT_DENIED}" "no answer from the user within ${wait_sec} s (the request is waiting on the user's PC or paired phone; retry, or raise --wait)"
    fi
    # Never hold past the deadline: the host expires the request then anyway.
    hold=$(( (remaining + 999) / 1000 ))
    if (( hold > LONG_POLL_SEC )); then hold="${LONG_POLL_SEC}"; fi
    started="${NOW_MS}"
    api GET "$(vault_path)/requests/${HOST_ID}?wait=${hold}" "" "${RESP}" $(( hold + API_TIMEOUT ))
    case "${API_STATUS}" in
      200) return 0 ;;
      204) failures=0 ;;
      404) fail "${EXIT_DENIED}" "the request expired on the host service before the user answered it (retry, or raise --wait)" ;;
      000|5[0-9][0-9])
        failures=$(( failures + 1 ))
        if (( failures >= POLL_ATTEMPTS )); then api_fail "${RESP}"; fi
        ;;
      *) api_fail "${RESP}" ;;
    esac
    # A "still pending" (or a failure) that came back at once must not turn
    # into a busy loop against the host.
    now_ms
    if (( NOW_MS - started < 1000 )); then sleep 1; fi
  done
}

# One string out of the answer, single-line (control characters become spaces);
# absent and null read as empty.
resp_str() {
  jq -r "(${1}) // \"\" | tostring | gsub(\"[[:cntrl:]]\"; \" \")" "${RESP}" 2>/dev/null || true
}

# The answer's string array at $1, one element per line.
resp_list() {
  jq -r "((${1}) // [])[] | tostring | gsub(\"[[:cntrl:]]\"; \" \")" "${RESP}" 2>/dev/null || true
}

# "3 uses left, until 14:05" from a lease's usesLeft / expiresAt (either may be
# empty = null). $3 is the word after the count: "left" in tables, "" elsewhere.
lease_text() {
  local uses="$1" expires="$2" suffix="$3" out=""
  if [[ "${uses}" =~ ^[0-9]+$ ]]; then out="$(uses_text "${uses}")${suffix:+ ${suffix}}"; fi
  if [[ "${expires}" =~ ^[0-9]+$ ]]; then out="${out:+${out}, }until $(hhmm "${expires}")"; fi
  printf '%s' "${out}"
}

join_names() { local IFS=','; local s="$*"; printf '%s' "${s//,/, }"; }

# ── commands ─────────────────────────────────────────────────────────────────

op_items() {
  local op="$1" empty_text="$2"
  round_trip "${op}" "${OPT_WAIT:-60}"
  if [[ "${OPT_JSON}" == true ]]; then
    jq -c '.items // []' "${RESP}"
    return 0
  fi
  # \x1f between fields: never part of a field (control characters are
  # replaced) and, unlike a tab, not IFS whitespace, so empty fields survive.
  local -a rows=() names=() access=() descs=()
  local row name has uses expires desc a w_name=4 w_access=6 i
  mapfile -t rows < <(jq -r '(.items // [])[]
    | [(.name // ""), (if .lease == null then "-" else "+" end),
       (.lease.usesLeft // ""), (.lease.expiresAt // ""), (.description // "")]
    | map(tostring | gsub("[[:cntrl:]]"; " ")) | join("\u001f")' "${RESP}" 2>/dev/null || true)
  if (( ${#rows[@]} == 0 )); then printf '%s\n' "${empty_text}"; return 0; fi
  for row in "${rows[@]}"; do
    IFS=$'\x1f' read -r name has uses expires desc <<<"${row}" || true
    if [[ "${has}" == "+" ]]; then
      a="$(lease_text "${uses}" "${expires}" left)"
      a="${a:-granted}"
    else
      a="none"
    fi
    names+=("${name}"); access+=("${a}"); descs+=("${desc}")
    if (( ${#name} > w_name )); then w_name=${#name}; fi
    if (( ${#a} > w_access )); then w_access=${#a}; fi
  done
  printf '%-*s  %-*s  %s\n' "${w_name}" NAME "${w_access}" ACCESS DESCRIPTION
  for i in "${!names[@]}"; do
    printf -v row '%-*s  %-*s  %s' "${w_name}" "${names[i]}" "${w_access}" "${access[i]}" "${descs[i]}"
    printf '%s\n' "${row%"${row##*[! ]}"}"
  done
}

op_request() {
  round_trip request "${OPT_WAIT:-600}"
  local -a approved=() missing=()
  local n a found message lease
  mapfile -t approved < <(resp_list '.names')
  if (( ${#approved[@]} == 0 )); then
    message="$(resp_str '.message')"
    fail "${EXIT_DENIED}" "${message:-the user approved none of the requested secrets}"
  fi
  for n in "${R_NAMES[@]}"; do
    found=false
    for a in "${approved[@]}"; do
      if [[ "${a}" == "${n}" ]]; then found=true; fi
    done
    if [[ "${found}" != true ]]; then missing+=("${n}"); fi
  done
  lease="$(lease_text "$(resp_str '.lease.usesLeft')" "$(resp_str '.lease.expiresAt')" "")"
  printf 'Approved: %s%s\n' "$(join_names "${approved[@]}")" "${lease:+ — ${lease}}"
  if (( ${#missing[@]} > 0 )); then warn "not approved: $(join_names "${missing[@]}")"; fi
}

op_get() {
  local name="${R_NAMES[0]}" uses_left
  round_trip get "${OPT_WAIT:-600}"
  jq -e '(.secret | type) == "string" and (.secret | length) > 0' "${RESP}" >/dev/null 2>&1 \
    || fail "${EXIT_COMPANION}" "${PEER} answered without a value for ${name}"
  # Validate before printing anything, so a broken answer never leaves half a
  # value on stdout.
  jq -j '.secret' "${RESP}" | base64 -d >/dev/null 2>&1 \
    || fail "${EXIT_COMPANION}" "${PEER} sent a value that is not valid base64"
  uses_left="$(resp_str '.lease.usesLeft')"
  if [[ "${OPT_USERNAME}" == true ]]; then
    if [[ -z "$(resp_str '.username')" ]]; then warn "${name} has no username"; fi
    jq -j '.username // "" | tostring' "${RESP}"
  elif [[ "${OPT_JSON}" == true ]]; then
    jq -c --arg name "${name}" '{name: $name, username: (.username // ""), secret: (.secret | @base64d)}' "${RESP}"
  else
    # Straight to stdout: no $(...) that would trim trailing newlines, no argv.
    jq -j '.secret' "${RESP}" | base64 -d
  fi
  # The answer holds the value; it goes now, not only when the script exits.
  rm -f -- "${RESP}"
  if [[ "${uses_left}" == "0" ]]; then
    warn "that was the last approved use of ${name}; request it again if you need it once more"
  fi
}

op_release() {
  round_trip release "${OPT_WAIT:-60}"
  local -a released=()
  mapfile -t released < <(resp_list '.names')
  if (( ${#released[@]} == 0 )); then printf 'Nothing to release.\n'; return 0; fi
  printf 'Released: %s\n' "$(join_names "${released[@]}")"
  warn "${PEER} now scrubs $(join_names "${released[@]}") from this VM (agent logs are redacted automatically; other files that contain a value need the user's decision)"
}

op_add() {
  local name="${R_NAMES[0]}" wait_default=60 expires
  # Overwriting needs the user's approval, a fresh secret does not.
  if [[ "${R_REPLACE}" == true ]]; then wait_default=600; fi
  round_trip add "${OPT_WAIT:-${wait_default}}"
  expires="$(resp_str '.lease.expiresAt')"
  if [[ "${expires}" =~ ^[0-9]+$ ]]; then
    printf "Stored %s in the key vault; this VM holds it until %s. Run 'construct secret release %s' when done.\n" \
      "${name}" "$(hhmm "${expires}")" "${name}"
  else
    printf "Stored %s in the key vault. Run 'construct secret release %s' when done.\n" "${name}" "${name}"
  fi
}

op_delete() {
  round_trip delete "${OPT_WAIT:-600}"
  printf 'Deleted %s.\n' "${R_NAMES[0]}"
}

# ── _scrub: the host service's scrub jobs (hosted mode, internal) ────────────
# Started in the background by the activity heartbeat when its reply says
# "vaultScrub": true. Runs the Companion's own guest scripts, rendered here the
# way GuestScripts.Fill renders them, and posts what they report:
#   scan   vault-scan.sh, stdin "<index>\t<base64 pattern>" lines,
#          stdout "F\t<indexes>\t<size>\t<type>\t<base64 path>" lines + "DONE\t<n>"
#   apply  vault-clean.sh, stdin "<action>\t<base64 path>\t<base64 pattern>..." lines,
#          stdout "R\t<status>\t<base64 path>\t<count>[\t<detail>]" lines + "DONE"
# The patterns ARE the secrets: they travel from the jobs document (0600, in the
# private work directory) to the script through a pipe, never through argv.

JOBS=""

# ShellQuote.Single: single quotes, an embedded ' as '\''.
shell_quote() {
  local sq="'" esc="'\\''"
  printf "'%s'" "${1//${sq}/${esc}}"
}

# Render guest script $1 into file $2: one pass over the {{name}} placeholders
# (a value that itself contains {{markers}} is never expanded again), refusing
# a placeholder without a value, exactly like GuestScripts.Fill.
render_guest_script() {
  local src="${GUEST_SCRIPTS}/$1.sh" dst="$2" tpl out="" key value root
  local re='\{\{([A-Za-z][A-Za-z0-9]*)\}\}'
  local -a roots=()
  if [[ ! -r "${src}" ]]; then warn "cannot read the guest script ${src}"; return 1; fi
  tpl="$(cat -- "${src}"; printf x)"
  tpl="${tpl%x}"
  while [[ "${tpl}" =~ ${re} ]]; do
    key="${BASH_REMATCH[1]}"
    case "${key}" in
      dir) value="$(shell_quote "${SPOOL}")" ;;
      maxSize) value="$(shell_quote "${SCAN_MAX_BYTES}")" ;;
      roots)
        read -r -a roots <<<"${SCAN_ROOTS}"
        value=""
        for root in ${roots[@]+"${roots[@]}"}; do value="${value:+${value} }$(shell_quote "${root}")"; done
        ;;
      *) warn "${src}: no value for the placeholder ${BASH_REMATCH[0]}"; return 1 ;;
    esac
    # Text up to the first occurrence of the match (= the leftmost match), then the value.
    out+="${tpl%%"${BASH_REMATCH[0]}"*}${value}"
    tpl="${tpl#*"${BASH_REMATCH[0]}"}"
  done
  printf '%s' "${out}${tpl}" >"${dst}"
}

# POST the body file $3 for job $1 (step $2). Returns 1 when the host did not take it.
post_job() {
  local id="$1" step="$2" body="$3" answer="${WORK}/post-answer.json"
  api POST "$(vault_path)/scrubs/${id}" "${body}" "${answer}" "${API_TIMEOUT}"
  if [[ "${API_STATUS}" =~ ^2[0-9][0-9]$ ]]; then return 0; fi
  warn "scrub job ${id}: the host service did not take the ${step} results (HTTP ${API_STATUS}${API_ERROR:+, ${API_ERROR}})"
  return 1
}

scrub_scan() {
  local i="$1" id="$2" script="${WORK}/vault-scan.sh" out="${WORK}/scan.out" body="${WORK}/scan.json"
  : >"${out}"
  if render_guest_script vault-scan "${script}"; then
    # shellcheck disable=SC2016 # jq variables
    jq -r --argjson i "${i}" '.jobs[$i].patterns // [] | .[]
      | select(type == "object" and (.index | type) == "number" and (.pattern | type) == "string")
      | select(.index >= 0 and (.pattern | test("^[A-Za-z0-9+/=]+$")))
      | "\(.index | floor)\t\(.pattern)"' "${JOBS}" 2>/dev/null \
      | bash "${script}" >"${out}" || true
  fi
  # No DONE line = the scan did not finish: complete false, so the host knows.
  jq -R -s '[split("\n")[] | rtrimstr("\r") | split("\t")] as $rows
    | {step: "scan",
       hits: [$rows[] | select(length == 5 and .[0] == "F")
         | {path: .[4], indexes: (.[1] | split(",") | map(tonumber? // empty)),
            size: ((.[2] | tonumber?) // 0), type: .[3]}
         | select(.indexes | length > 0)],
       complete: any($rows[]; .[0] == "DONE")}' "${out}" >"${body}" \
    || { warn "scrub job ${id}: cannot read the scan's output"; return 1; }
  post_job "${id}" scan "${body}" || return 1
  warn "scrub job ${id}: scanned, $(jq '.hits | length' "${body}") file(s) hold a secret$(jq -r 'if .complete then "" else " (the scan did not finish)" end' "${body}")"
}

scrub_apply() {
  local i="$1" id="$2" script="${WORK}/vault-clean.sh" out="${WORK}/clean.out" body="${WORK}/clean.json"
  : >"${out}"
  if render_guest_script vault-clean "${script}"; then
    # Patterns per file; a job-level list (the scan's shape) serves files without one.
    # shellcheck disable=SC2016 # jq variables
    jq -r --argjson i "${i}" '.jobs[$i] as $job
      | [($job.patterns // [])[] | if type == "object" then .pattern else . end] as $shared
      | ($job.files // [])[]
      | select(type == "object" and (.path | type) == "string" and (.action | type) == "string")
      | select((.action | test("^[a-z]+$")) and (.path | test("^[A-Za-z0-9+/=]+$")))
      | [.action, .path] + [(.patterns // $shared)[] | select(type == "string" and test("^[A-Za-z0-9+/=]+$"))]
      | join("\t")' "${JOBS}" 2>/dev/null \
      | bash "${script}" >"${out}" || true
  fi
  # Every file of the job gets a result: one the script never reported (it died,
  # or is missing) is "failed" rather than silently dropped.
  # shellcheck disable=SC2016 # jq variables
  jq -R -s --argjson i "${i}" --slurpfile jobs "${JOBS}" '
      [split("\n")[] | rtrimstr("\r") | split("\t") | select(length >= 4 and .[0] == "R")
        | {path: .[2], status: .[1], count: ((.[3] | tonumber?) // 0), detail: (.[4] // "")}] as $results
      | [$results[].path] as $done
      | [($jobs[0].jobs[$i].files // [])[] | objects | .path | strings] as $paths
      | {step: "apply",
         results: ($results + [$paths[] | . as $p | select(($done | map(select(. == $p)) | length) == 0)
           | {path: ., status: "failed", count: 0, detail: "not processed"}])}' "${out}" >"${body}" \
    || { warn "scrub job ${id}: cannot read the clean-up's output"; return 1; }
  post_job "${id}" apply "${body}" || return 1
  warn "scrub job ${id}: applied, $(jq -r '[.results[] | select(.status == "ok")] | length' "${body}") of $(jq '.results | length' "${body}") file(s) done"
}

op_scrub() {
  local count i id step rc=0
  [[ -n "${SERVICE_URL}" ]] \
    || die "_scrub runs only on a VM of a host service (the Companion scrubs other VMs itself)"
  command -v flock >/dev/null 2>&1 || die "flock is required (apt-get install -y util-linux)"
  ensure_spool_base
  # One scrub at a time: the heartbeat starts one every minute while a job is due,
  # and a full scan can take longer than that.
  exec 9>>"${SPOOL}/scrub.lock"
  flock -n 9 || exit 0
  hosted_begin

  JOBS="${WORK}/jobs.json"
  api GET "$(vault_path)/scrubs" "" "${JOBS}" "${API_TIMEOUT}"
  case "${API_STATUS}" in
    200) ;;
    204) return 0 ;;
    *) api_fail "${JOBS}" ;;
  esac
  jq -e 'type == "object" and ((.jobs // []) | type == "array")' "${JOBS}" >/dev/null 2>&1 \
    || fail "${EXIT_COMPANION}" "the host service sent scrub jobs this CLI cannot read"
  count="$(jq '(.jobs // []) | length' "${JOBS}")"
  for (( i = 0; i < count; i++ )); do
    # shellcheck disable=SC2016 # jq variables
    id="$(jq -r --argjson i "${i}" '.jobs[$i] | if type == "object" then (.id // "") else "" end | tostring' "${JOBS}")"
    # shellcheck disable=SC2016 # jq variables
    step="$(jq -r --argjson i "${i}" '.jobs[$i] | if type == "object" then (.step // "") else "" end | tostring' "${JOBS}")"
    if [[ ! "${id}" =~ ^[A-Za-z0-9._~-]{1,128}$ ]]; then
      warn "skipping a scrub job without a usable id"
      continue
    fi
    case "${step}" in
      scan) scrub_scan "${i}" "${id}" || rc="${EXIT_COMPANION}" ;;
      apply) scrub_apply "${i}" "${id}" || rc="${EXIT_COMPANION}" ;;
      *) warn "skipping scrub job ${id}: unknown step '$(clean_text "${step}" 40)'" ;;
    esac
  done
  return "${rc}"
}

# ── argument parsing ─────────────────────────────────────────────────────────

if [[ $# -lt 1 ]]; then usage >&2; exit 1; fi

op="$1"; shift
case "${op}" in
  -h|--help|help) usage; exit 0 ;;
  ls) op=list ;;
  relinquish) op=release ;;
  rm) op=delete ;;
  list|status|request|get|release|add|delete) ;;
  _scrub)
    # Internal (started by the activity heartbeat), not in --help.
    [[ $# -eq 0 ]] || die "_scrub takes no arguments"
    command -v jq >/dev/null 2>&1 || die "jq is required (apt-get install -y jq)"
    resolve_service
    op_scrub
    exit $?
    ;;
  *) die "unknown command: ${op} (try: construct secret --help)" ;;
esac

OPT_JSON=false OPT_USERNAME=false OPT_FILE="" OPT_WAIT=""
R_USES="" R_TTL="" R_ALL=false R_REPLACE=false R_REASON="" R_DESCRIPTION="" R_USERNAME=""
R_NAMES=()
opt_for="" have_for=false have_file=false have_description=false
given=()

# Value of a value-taking option: "--opt=value" or "--opt value".
take() {
  if [[ "${1}" == *=* ]]; then VALUE="${1#*=}"; SHIFT=1; return 0; fi
  [[ $# -ge 2 ]] || die "${1} needs a value"
  VALUE="$2"; SHIFT=2
}

while [[ $# -gt 0 ]]; do
  arg="$1"
  opt="${arg%%=*}"
  SHIFT=1
  case "${opt}" in
    -h|--help) usage; exit 0 ;;
    --) shift; R_NAMES+=("$@"); break ;;
    --json|--all|--replace)
      [[ "${arg}" == "${opt}" ]] || die "${opt} takes no value"
      case "${opt}" in
        --json) OPT_JSON=true ;;
        --all) R_ALL=true ;;
        --replace) R_REPLACE=true ;;
      esac
      ;;
    --username)
      # A value for add (stored with the secret), a flag for get (print it).
      if [[ "${op}" == "add" ]]; then
        take "$@"; R_USERNAME="${VALUE}"
      else
        [[ "${arg}" == "${opt}" ]] || die "--username takes no value here"
        OPT_USERNAME=true
      fi
      ;;
    --uses) take "$@"; R_USES="$(parse_int "${VALUE}" 1 1000 --uses)" ;;
    --for) take "$@"; opt_for="${VALUE}"; have_for=true ;;
    --reason) take "$@"; R_REASON="${VALUE}" ;;
    --wait) take "$@"; OPT_WAIT="$(parse_int "${VALUE}" 1 86400 --wait)" ;;
    --description) take "$@"; R_DESCRIPTION="${VALUE}"; have_description=true ;;
    --file) take "$@"; OPT_FILE="${VALUE}"; have_file=true ;;
    -*) die "unknown option: ${arg} (try: construct secret --help)" ;;
    *) R_NAMES+=("${arg}") ;;
  esac
  [[ "${arg}" == -* ]] && given+=("${opt}")
  shift "${SHIFT}"
done

# Options that do not belong to the command are mistakes worth stopping on.
case "${op}" in
  list|status) allowed="--json --wait" ;;
  request) allowed="--uses --for --reason --wait" ;;
  get) allowed="--username --json --reason --wait" ;;
  release) allowed="--all --wait" ;;
  add) allowed="--description --username --for --file --replace --reason --wait" ;;
  delete) allowed="--reason --wait" ;;
esac
for o in ${given[@]+"${given[@]}"}; do
  [[ " ${allowed} " == *" ${o} "* ]] || die "${o} does not apply to '${op}' (try: construct secret --help)"
done

# Names: validated, de-duplicated in order.
names=()
for n in ${R_NAMES[@]+"${R_NAMES[@]}"}; do
  is_name "${n}" || die "invalid secret name '$(clean_text "${n}" 80)' (letters, digits, '.', '_', '-'; up to 64; starts with a letter or digit)"
  dup=false
  for m in ${names[@]+"${names[@]}"}; do [[ "${m}" == "${n}" ]] && dup=true; done
  [[ "${dup}" == true ]] || names+=("${n}")
done
R_NAMES=(${names[@]+"${names[@]}"})
count=${#R_NAMES[@]}

case "${op}" in
  list|status)
    (( count == 0 )) || die "'${op}' takes no names"
    ;;
  request)
    (( count >= 1 )) || die "name the secrets to request (construct secret request <name>...)"
    (( count <= MAX_NAMES )) || die "at most ${MAX_NAMES} secrets per request"
    ;;
  get|add|delete)
    (( count == 1 )) || die "'${op}' takes exactly one name"
    ;;
  release)
    if [[ "${R_ALL}" == true ]]; then
      (( count == 0 )) || die "use either names or --all, not both"
    else
      (( count >= 1 )) || die "name the leases to release, or use --all"
    fi
    ;;
esac

[[ "${OPT_USERNAME}" == true && "${OPT_JSON}" == true ]] && die "use either --username or --json"

if [[ "${have_for}" == true ]]; then parse_duration "${opt_for}"; R_TTL="${DURATION_SEC}"; fi
# A request without limits still expires: one hour.
if [[ "${op}" == "request" && -z "${R_USES}" && -z "${R_TTL}" ]]; then R_TTL=3600; fi

R_REASON="$(clean_text "${R_REASON}" "${MAX_TEXT}")"
R_DESCRIPTION="$(clean_text "${R_DESCRIPTION}" "${MAX_TEXT}")"
R_USERNAME="$(clean_text "${R_USERNAME}" "${MAX_TEXT}")"

if [[ "${op}" == "add" ]]; then
  [[ "${have_description}" == true && -n "${R_DESCRIPTION}" ]] \
    || die "add needs --description TEXT (what the secret is, shown in the vault)"
  if [[ "${have_file}" == true ]]; then
    [[ -n "${OPT_FILE}" ]] || die "--file needs a path"
    if [[ "${OPT_FILE}" != "-" ]]; then
      [[ -r "${OPT_FILE}" && ! -d "${OPT_FILE}" ]] || die "cannot read ${OPT_FILE}"
    fi
  fi
  # Secrets typed at a prompt end up in terminal scrollback and transcripts;
  # values come from a pipe or a file only.
  if [[ ( -z "${OPT_FILE}" || "${OPT_FILE}" == "-" ) && -t 0 ]]; then
    die "add reads the secret from stdin; pipe it in or use --file PATH (never pass a secret as an argument)"
  fi
fi

command -v jq >/dev/null 2>&1 || die "jq is required (apt-get install -y jq)"
# A test knob; anything unusable falls back to the default rather than failing.
if [[ "${PICKUP_SEC}" =~ ^[0-9]{1,4}$ ]] && (( 10#${PICKUP_SEC} >= 1 )); then
  PICKUP_SEC=$(( 10#${PICKUP_SEC} ))
else
  PICKUP_SEC=15
fi
resolve_service

case "${op}" in
  list) op_items list "The key vault is empty." ;;
  status) op_items status "This VM holds no secret leases." ;;
  request) op_request ;;
  get) op_get ;;
  release) op_release ;;
  add) op_add ;;
  delete) op_delete ;;
esac
