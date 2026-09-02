#!/usr/bin/env bash
# construct-idle-report.sh — the guest half of idle detection (plan §4.7).
#
# Run once per CONSTRUCT_IDLE_REPORT_INTERVAL_SEC by construct-idle-report.timer,
# it answers ONE question for the host service: is anything happening on this VM?
#
#   POST {CONSTRUCT_SERVICE_URL}/api/v1/vms/{instance}/activity
#   {"busy":true,"reasons":["ssh-session","agent-cpu:claude"]}
#
# The service saves a VM whose forwards are idle AND whose guest says it is idle,
# so a `busy` heartbeat is what keeps an unattended agent job alive with nobody
# connected. The probes are therefore deliberately GENEROUS: a false `busy` costs
# some host RAM, a false idle kills someone's long-running job.
#
# Local installs have no service: with CONSTRUCT_SERVICE_URL empty this exits 0
# without doing anything, and provision.sh does not even install the timer.
#
# Nothing here is fatal — a failed POST is logged to the journal and retried by
# the next tick. The VM token is read from a 0600 file and handed to curl through
# a header file (-H @file), never on the command line.
#
# Every probe reads its command/path from an overridable variable so the tests can
# stub them; see docs/expose.md ("Activity heartbeat").
set -uo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
STATE_FILE="${CONSTRUCT_IDLE_STATE_FILE:-/run/construct/idle-state.json}"
PROVISION_MARKER="${CONSTRUCT_PROVISION_MARKER:-/run/construct/provisioning}"
PROC_DIR="${CONSTRUCT_IDLE_PROC_DIR:-/proc}"
SS_CMD="${CONSTRUCT_IDLE_SS:-ss}"
WHO_CMD="${CONSTRUCT_IDLE_WHO:-who}"
TMUX_CMD="${CONSTRUCT_IDLE_TMUX:-tmux}"
CURL="${CONSTRUCT_IDLE_CURL:-${CONSTRUCT_CURL:-curl}}"
# CPU ticks (USER_HZ, 100/s on Linux) an agent process must burn between two runs
# to count as working. 10 ticks = 0.1 CPU-seconds — low on purpose.
CPU_TICKS="${CONSTRUCT_IDLE_CPU_TICKS:-10}"
SSH_PORT="${CONSTRUCT_IDLE_SSH_PORT:-22}"
DRY_RUN="${CONSTRUCT_IDLE_DRY_RUN:-false}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"

log() { printf 'construct-idle-report: %s\n' "$*" >&2; }

# ── configuration ────────────────────────────────────────────────────────────
# Narrow key lookup, NOT `source`: config.env must not be able to set internal
# variables of this script (same discipline as construct-expose.sh).

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
INTERVAL="$(cfg_resolve "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC:-}" CONSTRUCT_IDLE_REPORT_INTERVAL_SEC 60)"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
# The interval has to be a POSITIVE, sane number of seconds: 0 is not "as often
# as possible", it is a timer systemd treats as disabled, and here it would also
# shrink the tmux freshness window to nothing. Anything unusable falls back to
# the default rather than quietly weakening the heartbeat.
# (INTERVAL_MIN/MAX are the same bounds provision.sh applies to the timer unit.)
INTERVAL_MIN=5
INTERVAL_MAX=3600
if ! [[ "${INTERVAL}" =~ ^[0-9]+$ ]] || (( INTERVAL < INTERVAL_MIN || INTERVAL > INTERVAL_MAX )); then
  INTERVAL=60
fi

# Local mode: nothing to report to. Silent and successful — this is the default
# path on every existing install.
if [[ -z "${SERVICE_URL}" ]] && [[ "${DRY_RUN}" != "true" ]]; then
  exit 0
fi

# ── probes ───────────────────────────────────────────────────────────────────

REASONS=()

add_reason() {
  local reason="$1" existing
  for existing in ${REASONS[@]+"${REASONS[@]}"}; do
    [[ "${existing}" == "${reason}" ]] && return 0
  done
  REASONS+=("${reason}")
}

# (a) Somebody is connected: an established TCP session on sshd's port covers
# a shell, VS Code Remote-SSH, scp and every tunnel riding that connection.
#
# Each source returns 1 when it could not answer at all, which is NOT the same as
# "answered: nobody is connected" -- a probe that cannot run must degrade to the
# next source, never to a false idle.
_ss_session_count() {
  local out
  command -v "${SS_CMD}" >/dev/null 2>&1 || return 1
  # An ss that exists but fails (unsupported filter syntax, a netlink hiccup,
  # restricted permissions) must not be read as an empty connection table.
  out="$("${SS_CMD}" -tn state established "( sport = :${SSH_PORT} )" 2>/dev/null)" || return 1
  # Drop ss's header line, count what is left.
  printf '%s\n' "${out}" | grep -v '^Recv-Q' | grep -c '[^[:space:]]' || true
}

_who_session_count() {
  local out
  command -v "${WHO_CMD}" >/dev/null 2>&1 || return 1
  out="$("${WHO_CMD}" 2>/dev/null)" || return 1
  printf '%s\n' "${out}" | grep -c '[^[:space:]]' || true
}

probe_ssh_sessions() {
  local count=""
  count="$(_ss_session_count)" || count=""
  if [[ -z "${count}" ]]; then
    count="$(_who_session_count)" || count=""
  fi
  if [[ "${count}" =~ ^[0-9]+$ ]] && (( count > 0 )); then
    add_reason "ssh-session"
  fi
}

# The agent processes worth watching. `node`/`bun` only count when their command
# line names one of the agent stacks (t3code, opencode, …), so an unrelated build
# tool does not pin the VM as busy forever.
_agent_name_for() {
  local comm="$1" cmdline="$2" keyword
  case "${comm}" in
    claude|codex|opencode|t3) printf '%s' "${comm}"; return 0 ;;
    node|bun|python3)
      for keyword in t3code t3 opencode claude codex; do
        case "${cmdline}" in
          *"${keyword}"*) printf '%s' "${keyword}"; return 0 ;;
        esac
      done
      ;;
  esac
  return 1
}

# One pass over /proc: parent, CPU ticks and (for the recognized ones) the agent
# name, for EVERY process. The parent map is what lets a child's CPU count for
# the agent that spawned it — an agent running a test suite or a build sits at
# ~0% itself while its children do the work.
declare -A _PARENT_OF=()
declare -A _TICKS_OF=()
declare -A _AGENT_NAME_OF=()

scan_processes() {
  local stat_file rest pid comm tail_fields cmdline name
  local -a fields
  _PARENT_OF=(); _TICKS_OF=(); _AGENT_NAME_OF=()
  shopt -s nullglob
  for stat_file in "${PROC_DIR}"/[0-9]*/stat; do
    # 2>/dev/null FIRST: a process that exits mid-scan makes the redirection
    # itself fail, and that message must not reach the journal.
    read -r rest 2>/dev/null <"${stat_file}" || continue
    [[ -n "${rest}" ]] || continue
    pid="${rest%% *}"
    # comm is parenthesized and may contain spaces: take what is between the
    # first '(' and the last ')', and the numeric fields after it.
    comm="${rest#*\(}"; comm="${comm%%)*}"
    tail_fields="${rest##*) }"
    # shellcheck disable=SC2206  # deliberate word splitting of a numeric field list
    fields=(${tail_fields})
    # tail_fields starts at /proc stat field 3 (state), so field 4 (ppid) is
    # index 1 and fields 14/15 (utime/stime) are indexes 11 and 12.
    [[ "${fields[1]:-}" =~ ^[0-9]+$ ]] || continue
    [[ "${fields[11]:-}" =~ ^[0-9]+$ && "${fields[12]:-}" =~ ^[0-9]+$ ]] || continue
    _PARENT_OF["${pid}"]="${fields[1]}"
    _TICKS_OF["${pid}"]=$(( fields[11] + fields[12] ))

    cmdline=""
    case "${comm}" in
      node|bun|python3)
        if [[ -r "${PROC_DIR}/${pid}/cmdline" ]]; then
          cmdline="$(tr '\0' ' ' <"${PROC_DIR}/${pid}/cmdline" 2>/dev/null || true)"
        fi
        ;;
    esac
    if name="$(_agent_name_for "${comm}" "${cmdline}")"; then
      _AGENT_NAME_OF["${pid}"]="${name}"
    fi
  done
  shopt -u nullglob
}

# The agent a process belongs to: itself, or the nearest ancestor that is one.
owner_of_pid() {
  local pid="$1" hops=0 name
  while (( hops < 16 )); do
    name="${_AGENT_NAME_OF[${pid}]:-}"
    if [[ -n "${name}" ]]; then printf '%s' "${name}"; return 0; fi
    pid="${_PARENT_OF[${pid}]:-}"
    [[ -n "${pid}" && "${pid}" != "0" && "${pid}" != "1" ]] || return 1
    hops=$((hops + 1))
  done
  return 1
}

declare -A _BEFORE=()
read_state() {
  local pair key value
  _BEFORE=()
  [[ -f "${STATE_FILE}" ]] || return 0
  while IFS= read -r pair; do
    [[ -n "${pair}" ]] || continue
    key="${pair%%:*}"; key="${key//\"/}"
    value="${pair##*:}"
    _BEFORE["${key}"]="${value}"
  done < <(tr -d ' \n' <"${STATE_FILE}" 2>/dev/null | grep -o '"[0-9]\+":[0-9]\+' || true)
}

# (b) An agent is actually working: utime+stime from /proc/<pid>/stat grew since
# the previous run, for the agent process itself or anything it spawned. The
# previous sample lives in STATE_FILE (tmpfs), so a reboot starts a fresh
# baseline.
probe_agent_cpu() {
  local pid owner previous delta
  local -a samples=() names=()
  scan_processes
  read_state
  for pid in "${!_TICKS_OF[@]}"; do
    owner="$(owner_of_pid "${pid}")" || continue
    samples+=("\"${pid}\":${_TICKS_OF[${pid}]}")
    previous="${_BEFORE[${pid}]:-}"
    if [[ -n "${previous}" ]]; then
      delta=$(( _TICKS_OF[${pid}] - previous ))
    else
      # No baseline yet -- the first run after a reboot, or a process that
      # appeared since the last one. Count everything it has burned so far:
      # "we cannot tell" must read as BUSY. An explicit idle heartbeat buys the
      # guest no grace period in the service, so a wrong `false` here can save a
      # VM out from under a working agent; a wrong `true` only costs host RAM
      # until the next tick, which then has a baseline.
      delta="${_TICKS_OF[${pid}]}"
    fi
    if (( delta > CPU_TICKS )); then names+=("${owner}"); fi
  done
  # Sorted + deduplicated so the reasons array is stable across runs (an
  # associative array iterates in an unspecified order).
  if (( ${#names[@]} > 0 )); then
    while IFS= read -r owner; do
      [[ -n "${owner}" ]] && add_reason "agent-cpu:${owner}"
    done < <(printf '%s\n' "${names[@]}" | sort -u)
  fi
  write_state "$(IFS=,; printf '%s' "${samples[*]-}")"
}

write_state() {
  local joined="$1" dir tmp
  dir="$(dirname "${STATE_FILE}")"
  mkdir -p "${dir}" 2>/dev/null || true
  tmp="${STATE_FILE}.tmp.$$"
  if printf '{"v":1,"at":%s,"samples":{%s}}\n' "$(date +%s)" "${joined}" >"${tmp}" 2>/dev/null; then
    chmod 0644 "${tmp}" 2>/dev/null || true
    mv -f "${tmp}" "${STATE_FILE}" 2>/dev/null || rm -f "${tmp}"
  fi
}

# (c) A tmux window produced output recently — how an agent left running in a
# detached session looks from the outside.
#
# #{window_activity}, NOT #{pane_activity}: the pane variant exists in the format
# vocabulary but resolves to an EMPTY string on the tmux this VM ships (verified
# against tmux 3.4), which would silently report a busy detached agent as idle —
# the one failure mode §4.7 calls make-or-break. window_activity advances
# whenever any pane in the window produces output.
probe_tmux() {
  local activity now
  command -v "${TMUX_CMD}" >/dev/null 2>&1 || return 0
  now="$(date +%s)"
  while IFS= read -r activity; do
    [[ "${activity}" =~ ^[0-9]+$ ]] || continue
    if (( now - activity <= INTERVAL )); then
      add_reason "tmux-activity"
      return 0
    fi
  done < <("${TMUX_CMD}" list-panes -a -F '#{window_activity}' 2>/dev/null || true)
}

# (d) Provisioning is running. provision.sh writes its PID into the marker and
# removes it at the end; the PID check is what keeps a run killed mid-flight
# (dropped SSH, hard reset) from pinning the VM as busy forever.
probe_provisioning() {
  local pid
  [[ -f "${PROVISION_MARKER}" ]] || return 0
  pid="$(head -n 1 "${PROVISION_MARKER}" 2>/dev/null | tr -dc '0-9' || true)"
  if [[ -z "${pid}" ]] || [[ -d "${PROC_DIR}/${pid}" ]]; then
    add_reason "provisioning"
  fi
}

# ── reporting ────────────────────────────────────────────────────────────────

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

reasons_json() {
  local out="" reason
  for reason in ${REASONS[@]+"${REASONS[@]}"}; do
    out="${out}${out:+,}\"$(json_escape "${reason}")\""
  done
  printf '[%s]' "${out}"
}

read_vm_token() {
  local token
  [[ -r "${VM_TOKEN_FILE}" ]] || return 1
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  [[ -n "${token}" ]] || return 1
  printf '%s' "${token}"
}

post_activity() {
  local body="$1" work token status http_body
  token="$(read_vm_token)" || {
    log "no usable VM token at ${VM_TOKEN_FILE}; not reporting activity"
    return 0
  }
  work="$(mktemp -d)" || { log "cannot create a temp dir; not reporting activity"; return 0; }
  ( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${work}/headers" )
  printf '%s' "${body}" >"${work}/request.json"
  local args=(
    --silent --show-error --fail-with-body
    --max-time "${API_TIMEOUT}"
    -H "@${work}/headers"
    -H 'Content-Type: application/json'
    -X POST
    --data-binary "@${work}/request.json"
    -o "${work}/body"
    -w '%{http_code}'
  )
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  status="$("${CURL}" "${args[@]}" "${SERVICE_URL}/api/v1/vms/${INSTANCE_NAME}/activity" 2>"${work}/stderr" || true)"
  if [[ ! "${status}" =~ ^2[0-9][0-9]$ ]]; then
    http_body="$(head -c 200 "${work}/body" 2>/dev/null || true)"
    log "activity report failed (HTTP ${status:-000})${http_body:+: ${http_body}}"
    log "$(head -n 1 "${work}/stderr" 2>/dev/null || true)"
  fi
  rm -rf "${work}"
}

main() {
  probe_ssh_sessions
  probe_agent_cpu
  probe_tmux
  probe_provisioning

  local busy="false"
  (( ${#REASONS[@]} > 0 )) && busy="true"
  local body
  body="{\"busy\":${busy},\"reasons\":$(reasons_json)}"

  if [[ "${DRY_RUN}" == "true" ]]; then
    printf '%s\n' "${body}"
    return 0
  fi
  post_activity "${body}"
}

main "$@"
exit 0
