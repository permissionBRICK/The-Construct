#!/usr/bin/env bash
# construct-expose.sh — implementation of `construct expose` (see `construct expose --help`).
#
# Answers the one question an agent has after starting a server: "what URL does the
# user open?" It requests a port forward, waits until that forward is actually live,
# and prints ONE link.
#
# Two targets (plan §4.6, full contract in docs/expose.md):
#   client (default)  the port is opened on the USER'S PC by the VS Code extension
#   host              the port is published on the VM host's LAN address
#
# Two modes, chosen by CONSTRUCT_SERVICE_URL in /etc/construct/config.env:
#   local  (empty)    requests go into the guest spool /etc/construct/forwards,
#                     which the extension watches over its SSH connection
#   remote (set)      requests go to the constructd host service over HTTPS,
#                     authenticated with this VM's scoped token
#
# Exit codes (documented in bin/construct's header too):
#   0  the forward is open (or --list/--close completed)
#   1  usage/local error: bad port, unknown option, unknown id, spool not writable
#   6  no Construct client attached -- the request stays queued and opens later
#   7  refused by the host service (host forwards disabled, forward cap reached)
#   8  the host service could not be reached, or answered something unusable
#
# The VM token is NEVER passed on a command line: curl reads the Authorization
# header from a 0600 file (-H @file) inside a private temp dir, so it cannot show
# up in `ps`, in a shell history or in an error message.
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
FORWARDS_DIR="${CONSTRUCT_FORWARDS_DIR:-/etc/construct/forwards}"
REQUEST_DIR="${FORWARDS_DIR}/requests"
ACK_DIR="${FORWARDS_DIR}/acks"
CLOSE_DIR="${FORWARDS_DIR}/close"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
CURL="${CONSTRUCT_CURL:-curl}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"

MAX_LABEL=100

EXIT_QUEUED=6
EXIT_REFUSED=7
EXIT_SERVICE=8

die() { printf 'construct expose: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Usage: construct expose <port> [--label <text>] [--to client|host] [--wait <sec>]
                            [--reuse]
       construct expose --list
       construct expose --close <id|port>

Expose a port from this VM and print the link to open it.

  construct expose 5173                  open it on the user's PC, print the link
  construct expose 5173 --label "vite"   ... and label it for --list
  construct expose --list                list this VM's forwards
  construct expose --close 5173          close one (by port, or by id)

Targets:
  client   (default) The port is opened on the USER'S PC and tunnelled over the
           SSH connection VS Code already holds. Private to that machine; needs
           VS Code with the Construct extension connected. This is the link you
           hand to the user.
  host     The port is published on the VM host's LAN address instead: on a
           remote install the host service allocates a public port, locally the
           VM's own external name is already reachable. Use it only when
           something OTHER than the user's PC must reach the port -- a webhook,
           a teammate, another machine.

Options:
  -l, --label <text>    Label shown in --list and in the extension.
  -t, --to client|host  Forward target (default: client).
  -w, --wait <sec>      How long to wait for a client forward to come up
                        (default: 30).
  -r, --reuse           Get-or-create: reuse this VM's existing forward for the
                        same port and target instead of allocating a second one,
                        and print its link. This is what makes re-running
                        provisioning idempotent.
      --list            List this VM's forwards.
      --close <id|port> Close a forward.
  -h, --help            This text.

Exit codes:
  0 open · 1 error · 6 no client attached (request stays queued)
  7 refused by the host service · 8 host service unreachable

Configuration (/etc/construct/config.env, see docs/expose.md):
  CONSTRUCT_SERVICE_URL             empty = local mode (guest spool)
  CONSTRUCT_INSTANCE_NAME           this VM's name on the host service
  CONSTRUCT_EXPOSE_DEFAULT_TARGET   client (default) or host
  CONSTRUCT_EXPOSE_WAIT_SEC         default wait, in seconds
  CONSTRUCT_SERVICE_CA_FILE         PEM certificate pinned for the service
USAGE
}

# ── configuration ────────────────────────────────────────────────────────────
# Narrow key lookup, NOT `source`: config.env must not be able to set internal
# variables of this script (same discipline as setup-root-ssh-key.sh).

# Undo config-set.sh's rendering: values outside its safe set are written as
# '...' with embedded apostrophes as '\''.
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

# Three-level precedence: explicit non-empty environment value > saved value in
# config.env > built-in default.
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
DEFAULT_TARGET="$(cfg_resolve "${CONSTRUCT_EXPOSE_DEFAULT_TARGET:-}" CONSTRUCT_EXPOSE_DEFAULT_TARGET client)"
DEFAULT_WAIT="$(cfg_resolve "${CONSTRUCT_EXPOSE_WAIT_SEC:-}" CONSTRUCT_EXPOSE_WAIT_SEC 30)"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
# constructd registers the VM-scoped token under its own "VmToken" scheme
# (service/README.md, Auth/AuthenticationSetup.cs); the key exists so a service
# that expects "Bearer" can be pointed at without a code change.
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
EXTERNAL_HOST="$(cfg_resolve "${CONSTRUCT_EXTERNAL_HOST:-}" CONSTRUCT_EXTERNAL_HOST "")"

# ── small helpers ────────────────────────────────────────────────────────────

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

# Strip control characters, squeeze whitespace, trim, cap the length -- a label
# ends up in a one-line JSON document the extension parses.
clean_label() {
  local text="$1"
  text="$(printf '%s' "${text}" | tr '\000-\037\177' '   ' | tr -s ' ')"
  text="${text#"${text%%[![:space:]]*}"}"
  text="${text%"${text##*[![:space:]]}"}"
  printf '%s' "${text:0:${MAX_LABEL}}"
}

have_jq() { command -v jq >/dev/null 2>&1; }

# One field out of a FLAT JSON object. jq when it is available (it is, on a
# provisioned VM: bootstrap.sh installs it), otherwise a regex good enough for
# the flat documents this contract defines. JSON null reads as empty either way.
json_field() {
  local json="$1" key="$2" value=""
  if have_jq; then
    value="$(printf '%s' "${json}" | jq -r --arg k "${key}" '(.[$k] // empty) | tostring' 2>/dev/null || true)"
  else
    value="$(printf '%s' "${json}" | tr -d '\n' \
      | grep -o "\"${key}\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 \
      | sed -e 's/^[^:]*:[[:space:]]*"//' -e 's/"$//' || true)"
    if [[ -z "${value}" ]]; then
      value="$(printf '%s' "${json}" | tr -d '\n' \
        | grep -o "\"${key}\"[[:space:]]*:[[:space:]]*[A-Za-z0-9.+-]*" | head -1 \
        | sed -e 's/^[^:]*:[[:space:]]*//' || true)"
    fi
  fi
  if [[ "${value}" == "null" ]]; then value=""; fi
  printf '%s' "${value}"
}

# Split a JSON array of flat objects into one compact object per line.
json_objects() {
  local json="$1"
  if have_jq; then
    printf '%s' "${json}" | jq -c 'if type == "array" then .[] else . end' 2>/dev/null || true
  else
    printf '%s' "${json}" | tr -d '\n' | grep -o '{[^{}]*}' || true
  fi
}

now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# ── the one URL-host rule (docs/expose.md) ───────────────────────────────────
# `hostLabel` travels on the wire as a BARE literal -- fe80::1, never [fe80::1] -- and
# the brackets are added exactly once, here, when a URL is built. One representation and
# one bracketing place is the whole rule: with the URL side bracketing anything with a
# colon and the wire side accepting both spellings, an accepted "[fe80::1]" printed as
# http://[[fe80::1]]:5173/ locally while the service, which interpolates the label as it
# stands, printed http://fe80::1:5173/ for the bare one. Neither is openable.

# Hostnames and IPv4 pass through; an IPv6 literal gets exactly one bracket pair.
# IDEMPOTENT: an already-bracketed value is unwrapped first, so both spellings render the
# same link. (CONSTRUCT_EXTERNAL_HOST goes through here too -- it is an admin-set local
# value, so it is formatted, not filtered.)
url_host() {
  local host="$1"
  case "${host}" in
    "["*"]") host="${host:1:${#host}-2}" ;;
  esac
  if [[ "${host}" == *:* ]]; then printf '[%s]' "${host}"; else printf '%s' "${host}"; fi
}

# One dotted-quad, the form an IPv6 literal may end with. Leading zeros are refused, the
# way inet_pton refuses them (so "::ffff:1.2.3.004" is not an address here either).
is_ipv4_literal() {
  local v="$1" octet
  [[ "${v}" =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$ ]] || return 1
  local IFS='.'
  local octets=()
  read -r -a octets <<<"${v}"
  for octet in "${octets[@]}"; do
    (( 10#${octet} <= 255 )) || return 1
    [[ "${octet}" == "0" || "${octet}" != 0* ]] || return 1
  done
}

# How many 16-bit groups are in ONE colon-separated run of an IPv6 literal? Prints the
# count ("" is 0 groups); fails on any malformed piece.
#
# $2 = 1 when THIS run may end with an embedded IPv4 address. A dotted quad is the literal's
# final 32 bits, so it can only ever be the last segment of the WHOLE address -- which means
# never in the head of a compressed one. The caller decides ("192.0.2.1::" and
# "1:192.0.2.1::" are not addresses, and inet_pton says so too), and even where it is
# allowed it must still be the last segment of the run ("::1.2.3.4.5", "1.2.3:4").
ipv6_groups() {
  local part="$1" allow_ipv4="$2" n=0 i seg
  [[ -n "${part}" ]] || { printf '0'; return 0; }
  # A run may not start or end with a colon: the only place a colon touches a boundary is
  # the "::" the caller already split on (":1", "1:" and "1:::" die here).
  [[ "${part}" != :* && "${part}" != *: ]] || return 1
  local IFS=':'
  local segs=()
  read -r -a segs <<<"${part}"
  for (( i = 0; i < ${#segs[@]}; i++ )); do
    seg="${segs[i]}"
    if [[ "${seg}" == *.* ]]; then
      [[ "${allow_ipv4}" == 1 ]] || return 1
      (( i == ${#segs[@]} - 1 )) || return 1
      is_ipv4_literal "${seg}" || return 1
      n=$(( n + 2 ))
    else
      [[ "${seg}" =~ ^[0-9A-Fa-f]{1,4}$ ]] || return 1
      n=$(( n + 1 ))
    fi
  done
  printf '%s' "${n}"
}

# Is this a real IPv6 literal? THE SAME CONTRACT the extension's `net.isIP` and the
# service's `IPAddress.TryParse` enforce -- the three implementations of the host-label
# rule have to agree address for address (docs/expose.md), so this is a parse and not a
# character class: "::::", "1::2::3", "12345::1" and "1:2:3:4:5:6:7" all look like
# addresses to a class and are none. The three test suites drive one shared fixture matrix
# through all three. '%' is outside the alphabet on purpose -- a zone id is not a wire
# host label.
is_ipv6_literal() {
  local v="$1" head tail hn tn
  [[ "${v}" == *:* ]] || return 1
  [[ "${v}" =~ ^[0-9A-Fa-f:.]+$ ]] || return 1
  if [[ "${v}" == *::* ]]; then
    head="${v%%::*}"
    tail="${v#*::}"
    [[ "${tail}" != *::* ]] || return 1           # "::" may appear once
    hn="$(ipv6_groups "${head}" 0)" || return 1   # a quad can never be BEFORE the "::"
    tn="$(ipv6_groups "${tail}" 1)" || return 1
    # "::" stands for AT LEAST one zero group, so the written ones can never fill all eight.
    (( hn + tn <= 7 ))
  else
    hn="$(ipv6_groups "${v}" 1)" || return 1
    (( hn == 8 ))
  fi
}

# The hostLabel as it arrives in an ack (or a service forward record): unwrap the optional
# brackets, and drop anything that is neither host-name-shaped nor an IPv6 literal. The
# caller then prints localhost -- an openable link -- rather than a URL nobody can parse.
wire_host_label() {
  local v="$1"
  case "${v}" in
    "["*"]") v="${v:1:${#v}-2}" ;;
  esac
  [[ -n "${v}" ]] || return 0
  if [[ "${v}" == *:* ]]; then
    is_ipv6_literal "${v}" && printf '%s' "${v}"
    return 0
  fi
  [[ "${v}" =~ ^[A-Za-z0-9._-]+$ ]] && printf '%s' "${v}"
  return 0
}

is_port() { [[ "$1" =~ ^[0-9]+$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )); }

# A forward id is ours (<epoch>-<hex>) or the service's, and it ends up BOTH in a
# spool file name and in a URL path -- so hold it to a single harmless component,
# the same guard `construct project` applies to profile names.
is_safe_id() {
  local id="$1"
  [[ -n "${id}" ]] || return 1
  [[ "${id}" != *".."* ]] || return 1
  [[ "${id}" =~ ^[A-Za-z0-9._-]+$ ]]
}

ensure_spool() {
  local dir
  for dir in "${FORWARDS_DIR}" "${REQUEST_DIR}" "${ACK_DIR}" "${CLOSE_DIR}"; do
    if [[ -d "${dir}" ]]; then continue; fi
    # 0755 root-owned, deliberately NOT 1777 like the notification spool: a
    # forward request opens a port on the user's PC (docs/expose.md).
    install -d -m 0755 "${dir}" 2>/dev/null \
      || die "cannot create ${dir} (run as root, or set CONSTRUCT_FORWARDS_DIR)"
  done
}

new_id() {
  local id
  while :; do
    id="$(date +%s)-$(printf '%04x' $((RANDOM % 65536)))"
    if [[ ! -e "${REQUEST_DIR}/${id}.json" ]]; then break; fi
  done
  printf '%s' "${id}"
}

# ── links ────────────────────────────────────────────────────────────────────

# Turn an ack document -- or a host-service forward record, which is read with the
# same lenient shape -- into the link to print.
#   0 + the link on stdout   the forward is open
#   1                        not open yet, keep waiting
#   2 + a message on stderr  the client reported an error; a final answer
link_from_ack() {
  local doc="$1" status local_port host_label message
  status="$(json_field "${doc}" status)"
  if [[ "${status}" == "error" ]]; then
    message="$(json_field "${doc}" message)"
    printf 'construct expose: the Construct client could not open the port%s\n' \
      "${message:+: ${message}}" >&2
    return 2
  fi
  local_port="$(json_field "${doc}" localPort)"
  if ! is_port "${local_port}"; then return 1; fi
  host_label="$(wire_host_label "$(json_field "${doc}" hostLabel)")"
  printf 'http://%s:%s/' "$(url_host "${host_label:-localhost}")" "${local_port}"
}

# The link for a host-service forward record: a host forward carries "url"; a
# client forward is open once a local port has been reported back.
link_from_forward() {
  local doc="$1" url
  url="$(json_field "${doc}" url)"
  if [[ -n "${url}" ]]; then printf '%s' "${url}"; return 0; fi
  link_from_ack "${doc}"
}

# ── local mode: the guest spool ──────────────────────────────────────────────

write_request() {
  local id="$1" port="$2" label="$3" tmp json
  json="{\"v\":1,\"id\":\"$(json_escape "${id}")\",\"vmPort\":${port},\"label\":\"$(json_escape "${label}")\",\"target\":\"client\",\"createdAt\":\"$(now_iso)\"}"
  tmp="${REQUEST_DIR}/.tmp.$$.${RANDOM}"
  printf '%s\n' "${json}" >"${tmp}" 2>/dev/null || die "cannot write to ${REQUEST_DIR} (run as root?)"
  chmod 0644 "${tmp}" 2>/dev/null || true
  # Publish with a rename: the extension watches this directory and must never
  # read a half-written request.
  mv -f "${tmp}" "${REQUEST_DIR}/${id}.json" || { rm -f "${tmp}"; die "cannot publish to ${REQUEST_DIR}"; }
}

read_ack() {
  local id="$1"
  [[ -f "${ACK_DIR}/${id}.json" ]] || return 1
  cat "${ACK_DIR}/${id}.json" 2>/dev/null || return 1
}

# 0 = the link was printed · 1 = the wait ran out · 2 = the client failed.
wait_for_ack() {
  local id="$1" wait_sec="$2" deadline ack link rc
  deadline=$(( $(date +%s) + wait_sec ))
  while :; do
    if ack="$(read_ack "${id}")" && [[ -n "${ack}" ]]; then
      rc=0
      link="$(link_from_ack "${ack}")" || rc=$?
      if (( rc == 0 )); then printf '%s\n' "${link}"; return 0; fi
      if (( rc == 2 )); then return 2; fi
    fi
    if (( $(date +%s) >= deadline )); then return 1; fi
    sleep 0.5
  done
}

queued_message() {
  local id="$1" port="$2"
  printf 'construct expose: no Construct client is attached, so port %s is not open yet.\n' "${port}" >&2
  printf '  The request (%s) stays queued and opens as soon as the user connects VS Code\n' "${id}" >&2
  printf '  to this VM. Check with: construct expose --list\n' >&2
}

# --reuse, local mode: the id of an existing CLIENT request for this port, if any.
# The spool is the record: a request the extension has not picked up yet is still
# this VM's forward for that port, and a second one would just queue behind it.
local_find_request() {
  local port="$1" file request
  [[ -d "${REQUEST_DIR}" ]] || return 1
  shopt -s nullglob
  for file in "${REQUEST_DIR}"/*.json; do
    request="$(cat "${file}" 2>/dev/null || true)"
    if [[ "$(json_field "${request}" vmPort)" == "${port}" \
       && "$(json_field "${request}" target)" == "client" ]]; then
      basename "${file}" .json
      shopt -u nullglob
      return 0
    fi
  done
  shopt -u nullglob
  return 1
}

local_expose_client() {
  local port="$1" label="$2" wait_sec="$3" id="" rc=0
  ensure_spool
  if [[ "${REUSE}" == "true" ]]; then id="$(local_find_request "${port}")" || id=""; fi
  if [[ -z "${id}" ]]; then
    id="$(new_id)"
    write_request "${id}" "${port}" "${label}"
  fi
  wait_for_ack "${id}" "${wait_sec}" || rc=$?
  if (( rc == 0 )); then return 0; fi
  if (( rc == 2 )); then exit 1; fi
  queued_message "${id}" "${port}"
  exit "${EXIT_QUEUED}"
}

local_expose_host() {
  local port="$1" host
  # NAT already reaches the VM at its external name (B2's CONSTRUCT_EXTERNAL_HOST,
  # falling back to the Hyper-V .mshome.net name), so there is nothing to allocate.
  host="${EXTERNAL_HOST:-$(hostname 2>/dev/null || echo localhost).mshome.net}"
  printf 'http://%s:%s/\n' "$(url_host "${host}")" "${port}"
}

local_list() {
  local file id request ack status url port target label
  [[ -d "${REQUEST_DIR}" ]] || return 0
  shopt -s nullglob
  for file in "${REQUEST_DIR}"/*.json; do
    id="$(basename "${file}" .json)"
    request="$(cat "${file}" 2>/dev/null || true)"
    port="$(json_field "${request}" vmPort)"
    target="$(json_field "${request}" target)"
    label="$(json_field "${request}" label)"
    status="queued"
    url=""
    if ack="$(read_ack "${id}")" && [[ -n "${ack}" ]]; then
      status="$(json_field "${ack}" status)"
      status="${status:-queued}"
      url="$(link_from_ack "${ack}" 2>/dev/null)" || url=""
    fi
    print_row "${id}" "${port}" "${target:-client}" "${status}" "${label}" "${url}"
  done
  shopt -u nullglob
}

local_close() {
  local ref="$1" id="" file matches=0 candidate tmp
  [[ -d "${REQUEST_DIR}" ]] || die "no forward matches '${ref}'"
  if is_safe_id "${ref}" && [[ -f "${REQUEST_DIR}/${ref}.json" ]]; then
    id="${ref}"
  else
    shopt -s nullglob
    for file in "${REQUEST_DIR}"/*.json; do
      candidate="$(basename "${file}" .json)"
      if [[ "$(json_field "$(cat "${file}" 2>/dev/null || true)" vmPort)" == "${ref}" ]]; then
        id="${candidate}"
        matches=$((matches + 1))
      fi
    done
    shopt -u nullglob
    if (( matches > 1 )); then
      die "port ${ref} has ${matches} forwards -- close it by id (construct expose --list)"
    fi
  fi
  [[ -n "${id}" ]] || die "no forward matches '${ref}'"

  ensure_spool
  tmp="${CLOSE_DIR}/.tmp.$$.${RANDOM}"
  printf '{"v":1,"id":"%s","closedAt":"%s"}\n' "$(json_escape "${id}")" "$(now_iso)" >"${tmp}" 2>/dev/null \
    || die "cannot write to ${CLOSE_DIR} (run as root?)"
  chmod 0644 "${tmp}" 2>/dev/null || true
  mv -f "${tmp}" "${CLOSE_DIR}/${id}.json" || { rm -f "${tmp}"; die "cannot publish to ${CLOSE_DIR}"; }
  # Drop request + ack ourselves so --list is correct immediately, even with no
  # client attached to consume the close document.
  rm -f "${REQUEST_DIR}/${id}.json" "${ACK_DIR}/${id}.json"
  printf 'closed %s\n' "${id}"
}

# ── remote mode: the constructd host service ─────────────────────────────────

API_STATUS=""
API_BODY=""
API_ERROR=""

read_vm_token() {
  local token
  if [[ ! -r "${VM_TOKEN_FILE}" ]]; then
    printf 'construct expose: no VM token at %s -- this VM cannot talk to %s.\n' \
      "${VM_TOKEN_FILE}" "${SERVICE_URL}" >&2
    printf '  Re-provision the VM so the host installs its scoped token.\n' >&2
    exit "${EXIT_SERVICE}"
  fi
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  if [[ -z "${token}" ]]; then
    printf 'construct expose: the VM token file %s is empty.\n' "${VM_TOKEN_FILE}" >&2
    exit "${EXIT_SERVICE}"
  fi
  printf '%s' "${token}"
}

# curl against the service. The token goes into a 0600 header file (-H @file),
# never onto the command line: a command line is world-readable in `ps`.
api_call() {
  local method="$1" path="$2" body="${3:-}" work token status args
  token="$(read_vm_token)"
  work="$(mktemp -d)" || { API_STATUS="000"; API_ERROR="cannot create a temp dir"; return 0; }
  ( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${work}/headers" )
  args=(
    --silent --show-error --fail-with-body
    --max-time "${API_TIMEOUT}"
    -H "@${work}/headers"
    -H 'Accept: application/json'
    -X "${method}"
    -o "${work}/body"
    -w '%{http_code}'
  )
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  if [[ -n "${body}" ]]; then
    printf '%s' "${body}" >"${work}/request.json"
    args+=(-H 'Content-Type: application/json' --data-binary "@${work}/request.json")
  fi
  status="$("${CURL}" "${args[@]}" "${SERVICE_URL}${path}" 2>"${work}/stderr" || true)"
  API_STATUS="${status:-000}"
  API_BODY="$(cat "${work}/body" 2>/dev/null || true)"
  API_ERROR="$(tr -d '\r' <"${work}/stderr" 2>/dev/null | head -n 3 || true)"
  rm -rf "${work}"
}

api_ok() { [[ "${API_STATUS}" =~ ^2[0-9][0-9]$ ]]; }

# Is the body of a 2xx answer the JSON shape this call is supposed to return?
# A 200 carrying HTML from a captive portal or a truncated body must not be read
# as "an empty list" -- that would silently look like "no forwards" or "not open
# yet". jq decides when it is available; without it, the shape check is coarse
# but still rejects everything that is not a JSON array/object.
api_body_is() {
  local kind="$1" trimmed
  if have_jq; then
    printf '%s' "${API_BODY}" \
      | jq -e --arg k "${kind}" 'if $k == "array" then type == "array" else type == "object" end' \
        >/dev/null 2>&1
    return $?
  fi
  trimmed="$(printf '%s' "${API_BODY}" | tr -d '[:space:]')"
  case "${kind}:${trimmed}" in
    array:\[*\]) return 0 ;;
    object:\{*\}) return 0 ;;
    *) return 1 ;;
  esac
}

# A 2xx we cannot read is a service problem, not an empty result (docs/expose.md:
# "unparsable body -> exit 8").
api_unreadable() {
  printf 'construct expose: the host service answered HTTP %s with something this CLI cannot read.\n' \
    "${API_STATUS}" >&2
  printf '  Expected JSON from %s%s.\n' "${SERVICE_URL}" "$1" >&2
  exit "${EXIT_SERVICE}"
}

# RFC 7807 problem documents carry "detail"; fall back to "title".
api_detail() {
  local detail
  detail="$(json_field "${API_BODY}" detail)"
  if [[ -z "${detail}" ]]; then detail="$(json_field "${API_BODY}" title)"; fi
  printf '%s' "${detail}"
}

api_fail() {
  local what="$1" detail
  detail="$(api_detail)"
  case "${API_STATUS}" in
    403)
      printf 'construct expose: the host service refused to %s (403).\n' "${what}" >&2
      printf '  %s\n' "${detail:-Host forwards may be disabled for the owner of this VM, or its forward limit is reached.}" >&2
      exit "${EXIT_REFUSED}"
      ;;
    401)
      printf 'construct expose: the host service rejected this VM token (401).\n' >&2
      printf '  Re-provision the VM to install a fresh token.\n' >&2
      exit "${EXIT_SERVICE}"
      ;;
    000)
      printf 'construct expose: cannot reach the host service at %s.\n' "${SERVICE_URL}" >&2
      if [[ -n "${API_ERROR}" ]]; then printf '  %s\n' "${API_ERROR}" >&2; fi
      exit "${EXIT_SERVICE}"
      ;;
    *)
      printf 'construct expose: the host service could not %s (HTTP %s).\n' "${what}" "${API_STATUS}" >&2
      if [[ -n "${detail}" ]]; then printf '  %s\n' "${detail}" >&2; fi
      exit "${EXIT_SERVICE}"
      ;;
  esac
}

forwards_path() { printf '/api/v1/vms/%s/forwards' "${INSTANCE_NAME}"; }

# Fetch this VM's forwards and pick ours out. Deliberately NOT called through a
# command substitution: an API failure has to be able to end the command with the
# right exit code instead of being mistaken for "not open yet". Result in
# FOUND_FORWARD; returns 1 when the list simply does not contain our id (yet).
FOUND_FORWARD=""
remote_find_forward() {
  local id="$1" object
  FOUND_FORWARD=""
  api_call GET "$(forwards_path)"
  api_ok || api_fail "list the forwards of ${INSTANCE_NAME}"
  api_body_is array || api_unreadable "$(forwards_path)"
  while IFS= read -r object; do
    [[ -n "${object}" ]] || continue
    if [[ "$(json_field "${object}" id)" == "${id}" ]]; then
      FOUND_FORWARD="${object}"
      return 0
    fi
  done < <(json_objects "${API_BODY}")
  return 1
}

# --reuse, remote mode: this VM's existing forward for a port AND target, if the
# service already has one. Result in FOUND_FORWARD; returns 1 when there is none.
# Like remote_find_forward it is NOT called in a command substitution, so an API
# failure ends the command with the right exit code instead of reading as "none".
remote_find_by_port() {
  local want_port="$1" want_target="$2" object
  FOUND_FORWARD=""
  api_call GET "$(forwards_path)"
  api_ok || api_fail "list the forwards of ${INSTANCE_NAME}"
  api_body_is array || api_unreadable "$(forwards_path)"
  while IFS= read -r object; do
    [[ -n "${object}" ]] || continue
    [[ "$(json_field "${object}" vmPort)" == "${want_port}" ]] || continue
    [[ "$(json_field "${object}" target)" == "${want_target}" ]] || continue
    FOUND_FORWARD="${object}"
    return 0
  done < <(json_objects "${API_BODY}")
  return 1
}

remote_expose() {
  local port="$1" label="$2" target="$3" wait_sec="$4" id="" link deadline object rc=0 body=""
  # GET-OR-CREATE. Provisioning runs on every reprovision and must not leave a second
  # host forward (and a second public port) behind for the same VM port each time; the
  # service has no upsert, so the lookup is here.
  if [[ "${REUSE}" == "true" ]] && remote_find_by_port "${port}" "${target}"; then
    body="${FOUND_FORWARD}"
  else
    api_call POST "$(forwards_path)" \
      "{\"vmPort\":${port},\"label\":\"$(json_escape "${label}")\",\"target\":\"${target}\"}"
    api_ok || api_fail "open port ${port}"
    api_body_is object || api_unreadable "$(forwards_path)"
    body="${API_BODY}"
  fi

  id="$(json_field "${body}" id)"
  link="$(link_from_forward "${body}")" || rc=$?
  if (( rc == 2 )); then exit 1; fi
  if (( rc == 0 )) && [[ -n "${link}" ]]; then printf '%s\n' "${link}"; return 0; fi

  if [[ "${target}" == "host" ]]; then
    printf 'construct expose: the host service accepted the forward but returned no URL.\n' >&2
    exit "${EXIT_SERVICE}"
  fi
  if [[ -z "${id}" ]]; then
    printf 'construct expose: the host service returned no forward id.\n' >&2
    exit "${EXIT_SERVICE}"
  fi

  # Client target: the service relays the request to the owner's extension, which
  # opens the port on the user's PC and reports back. Poll until it does; a
  # transient list failure just costs one poll.
  deadline=$(( $(date +%s) + wait_sec ))
  while (( $(date +%s) < deadline )); do
    sleep 1
    # remote_find_forward ends the command itself on an API failure (401/403/
    # 5xx/transport/unreadable body): only a list that genuinely does not carry
    # our forward yet is a reason to keep waiting.
    remote_find_forward "${id}" || continue
    object="${FOUND_FORWARD}"
    rc=0
    link="$(link_from_forward "${object}")" || rc=$?
    if (( rc == 2 )); then exit 1; fi
    if (( rc == 0 )) && [[ -n "${link}" ]]; then printf '%s\n' "${link}"; return 0; fi
  done
  queued_message "${id}" "${port}"
  exit "${EXIT_QUEUED}"
}

remote_list() {
  local object url status
  api_call GET "$(forwards_path)"
  api_ok || api_fail "list forwards"
  api_body_is array || api_unreadable "$(forwards_path)"
  while IFS= read -r object; do
    [[ -n "${object}" ]] || continue
    url="$(link_from_forward "${object}" 2>/dev/null)" || url=""
    if [[ -n "${url}" ]]; then status="open"; else status="queued"; fi
    print_row \
      "$(json_field "${object}" id)" \
      "$(json_field "${object}" vmPort)" \
      "$(json_field "${object}" target)" \
      "${status}" \
      "$(json_field "${object}" label)" \
      "${url}"
  done < <(json_objects "${API_BODY}")
}

remote_close() {
  local ref="$1" id="" object matches=0 candidate
  if is_port "${ref}"; then
    api_call GET "$(forwards_path)"
    api_ok || api_fail "list forwards"
    api_body_is array || api_unreadable "$(forwards_path)"
    while IFS= read -r object; do
      [[ -n "${object}" ]] || continue
      if [[ "$(json_field "${object}" vmPort)" == "${ref}" ]]; then
        candidate="$(json_field "${object}" id)"
        [[ -n "${candidate}" ]] || continue
        id="${candidate}"
        matches=$((matches + 1))
      fi
    done < <(json_objects "${API_BODY}")
    if (( matches > 1 )); then
      die "port ${ref} has ${matches} forwards -- close it by id (construct expose --list)"
    fi
  else
    id="${ref}"
  fi
  [[ -n "${id}" ]] || die "no forward matches '${ref}'"
  is_safe_id "${id}" || die "not a usable forward id: ${id}"

  api_call DELETE "$(forwards_path)/${id}"
  if [[ "${API_STATUS}" == "404" ]]; then die "no forward matches '${ref}'"; fi
  api_ok || api_fail "close forward ${id}"
  printf 'closed %s\n' "${id}"
}

# ── output ───────────────────────────────────────────────────────────────────

_ROW_HEADER_PRINTED=false
print_row() {
  if [[ "${_ROW_HEADER_PRINTED}" != "true" ]]; then
    printf '%-18s %6s %-7s %-7s %-24s %s\n' ID PORT TARGET STATUS LABEL URL
    _ROW_HEADER_PRINTED=true
  fi
  printf '%-18s %6s %-7s %-7s %-24s %s\n' "$1" "${2:--}" "${3:--}" "${4:--}" "${5:--}" "${6:--}"
}

# ── argument parsing ─────────────────────────────────────────────────────────

mode="open"
port=""
label=""
target=""
wait_sec=""
close_ref=""
REUSE=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --list) mode="list" ;;
    --close) shift; [[ $# -gt 0 ]] || die "--close requires an id or a port"; mode="close"; close_ref="$1" ;;
    -l|--label) shift; [[ $# -gt 0 ]] || die "--label requires a value"; label="$1" ;;
    -t|--to) shift; [[ $# -gt 0 ]] || die "--to requires client or host"; target="$1" ;;
    -w|--wait) shift; [[ $# -gt 0 ]] || die "--wait requires a number of seconds"; wait_sec="$1" ;;
    -r|--reuse) REUSE=true ;;
    --) shift; if [[ $# -gt 0 ]]; then port="$1"; shift; fi; break ;;
    -*) die "unknown option: $1 (try: construct expose --help)" ;;
    *)
      [[ -z "${port}" ]] || die "unexpected argument: $1 (try: construct expose --help)"
      port="$1"
      ;;
  esac
  shift
done

target="${target:-${DEFAULT_TARGET}}"
case "${target}" in
  client|host) ;;
  *) die "unknown target: ${target} (use client or host)" ;;
esac

wait_sec="${wait_sec:-${DEFAULT_WAIT}}"
[[ "${wait_sec}" =~ ^[0-9]+$ ]] || die "--wait must be a whole number of seconds"

case "${mode}" in
  list)
    if [[ -n "${SERVICE_URL}" ]]; then remote_list; else local_list; fi
    ;;
  close)
    if [[ -n "${SERVICE_URL}" ]]; then remote_close "${close_ref}"; else local_close "${close_ref}"; fi
    ;;
  open)
    if [[ -z "${port}" ]]; then usage >&2; exit 1; fi
    is_port "${port}" || die "invalid port: ${port} (expected 1-65535)"
    port="$((10#${port}))"
    label="$(clean_label "${label}")"
    if [[ -n "${SERVICE_URL}" ]]; then
      remote_expose "${port}" "${label}" "${target}" "${wait_sec}"
    elif [[ "${target}" == "host" ]]; then
      local_expose_host "${port}"
    else
      local_expose_client "${port}" "${label}" "${wait_sec}"
    fi
    ;;
esac
