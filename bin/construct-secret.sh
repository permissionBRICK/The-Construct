#!/usr/bin/env bash
# construct-secret.sh — implementation of `construct secret` (see `construct secret --help`).
#
# The guest half of the key vault. Secrets live in the Construct Companion on the
# user's PC; this VM never sees one until the user approves a lease for it in a
# Companion popup. Every command is one request/response round trip through a
# root-only spool on tmpfs, which the Companion drains over its SSH connection:
#
#   requests/<id>.json    published here (write .tmp.<id>, then rename)
#                         the Companion claims it by renaming it away = "pickup"
#   responses/<id>.json   published by the Companion; read and deleted here
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
#   6   no Companion picked the request up within CONSTRUCT_VAULT_PICKUP_SEC
#   7   the user denied it, or did not answer before the deadline
#   8   the Companion answered `error` / `invalid`, or something unreadable
#   9   no such secret (`notFound`)
#   10  a secret with that name already exists (`exists`; use add --replace)
#
# A secret value never appears in argv (world-readable in `ps`) and never in a
# shell variable that a command substitution would have trimmed: `add` streams
# it stdin → base64 → jq through pipes, `get` decodes it straight to stdout.
set -euo pipefail

SPOOL="${CONSTRUCT_VAULT_SPOOL:-/run/construct/vault}"
REQ_DIR="${SPOOL}/requests"
RESP_DIR="${SPOOL}/responses"
PICKUP_SEC="${CONSTRUCT_VAULT_PICKUP_SEC:-15}"

MAX_TEXT=300
MAX_SOURCE=60
MAX_NAMES=20
MAX_SECRET=32768
POLL_SEC=0.2

EXIT_NO_COMPANION=6
EXIT_DENIED=7
EXIT_COMPANION=8
EXIT_NOT_FOUND=9
EXIT_EXISTS=10

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

# Remove what this run left behind: its unpublished temp file, its request if
# nobody claimed it, and the answer (which, for `get`, holds the secret).
cleanup() {
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

# Publish one request, wait for the Companion and its answer. On return RESP is
# the answer file with status "ok"; every other outcome exits with its code.
#   $1 op   $2 wait seconds   names in R_NAMES; other fields in R_* globals
round_trip() {
  local op="$1" wait_sec="$2" ts deadline source tmp req rc
  local -a jq_args=() st=()
  ensure_spool

  now_ms
  ts="${NOW_MS}"
  deadline=$(( ts + wait_sec * 1000 ))
  printf -v REQ_ID '%s-%s-%04x%04x' "${ts}" "$$" "${RANDOM}" "${RANDOM}"
  tmp="${REQ_DIR}/.tmp.${REQ_ID}"
  req="${REQ_DIR}/${REQ_ID}.json"
  RESP="${RESP_DIR}/${REQ_ID}.json"
  source="$(clean_text "${SUDO_USER:-$(id -un 2>/dev/null || echo agent)}@$(hostname -s 2>/dev/null || echo vm)" "${MAX_SOURCE}")"

  jq_args=(--arg id "${REQ_ID}" --argjson ts "${ts}" --arg op "${op}"
    --argjson uses "${R_USES:-null}" --argjson ttl "${R_TTL:-null}"
    --argjson all "${R_ALL}" --argjson replace "${R_REPLACE}"
    --arg reason "${R_REASON}" --arg description "${R_DESCRIPTION}" --arg username "${R_USERNAME}"
    --argjson deadline "${deadline}" --arg source "${source}")

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
      || { rm -f "${tmp}"; die "cannot write to ${REQ_DIR} (run as root?)"; }
  fi
  # Publish with a rename: the Companion must never read a half-written request.
  mv -f "${tmp}" "${req}" 2>/dev/null || { rm -f "${tmp}"; die "cannot publish to ${REQ_DIR}"; }

  # Pickup: the Companion claims a request by renaming it away. If it is still
  # here after the pickup window (and no answer came), nothing is draining the spool.
  local pickup_by=$(( ts + PICKUP_SEC * 1000 ))
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
    if (( NOW_MS >= deadline )); then
      [[ -f "${RESP}" ]] && break
      fail "${EXIT_DENIED}" "no answer from the user within ${wait_sec} s (the request was shown in the Construct Companion; retry, or raise --wait)"
    fi
    sleep "${POLL_SEC}"
  done

  rc=0
  jq -e --arg id "${REQ_ID}" 'type == "object" and ((.id // $id) == $id)' "${RESP}" >/dev/null 2>&1 || rc=$?
  (( rc == 0 )) || fail "${EXIT_COMPANION}" "the Construct Companion sent an unreadable answer"

  local status message
  status="$(resp_str '.status')"
  message="$(resp_str '.message')"
  case "${status}" in
    ok) return 0 ;;
    denied) fail "${EXIT_DENIED}" "${message:-the user denied the request}" ;;
    notFound) fail "${EXIT_NOT_FOUND}" "${message:-no such secret in the key vault}" ;;
    exists) fail "${EXIT_EXISTS}" "${message:-a secret with that name already exists (add --replace overwrites it)}" ;;
    error|invalid) fail "${EXIT_COMPANION}" "${message:-the Construct Companion could not handle the request (${status})}" ;;
    *) fail "${EXIT_COMPANION}" "the Construct Companion sent an unknown status '${status}'" ;;
  esac
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
    || fail "${EXIT_COMPANION}" "the Construct Companion answered without a value for ${name}"
  # Validate before printing anything, so a broken answer never leaves half a
  # value on stdout.
  jq -j '.secret' "${RESP}" | base64 -d >/dev/null 2>&1 \
    || fail "${EXIT_COMPANION}" "the Construct Companion sent a value that is not valid base64"
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
  warn "the Construct Companion now scrubs $(join_names "${released[@]}") from this VM (agent logs are redacted automatically; other files that contain a value need the user's decision)"
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

# ── argument parsing ─────────────────────────────────────────────────────────

if [[ $# -lt 1 ]]; then usage >&2; exit 1; fi

op="$1"; shift
case "${op}" in
  -h|--help|help) usage; exit 0 ;;
  ls) op=list ;;
  relinquish) op=release ;;
  rm) op=delete ;;
  list|status|request|get|release|add|delete) ;;
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

case "${op}" in
  list) op_items list "The key vault is empty." ;;
  status) op_items status "This VM holds no secret leases." ;;
  request) op_request ;;
  get) op_get ;;
  release) op_release ;;
  add) op_add ;;
  delete) op_delete ;;
esac
