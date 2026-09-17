#!/usr/bin/env bash
# construct-idle-report.sh — the guest half of idle detection (plan §4.7).
#
# Run once per CONSTRUCT_IDLE_REPORT_INTERVAL_SEC by construct-idle-report.timer,
# it answers ONE question for the host service: is anything happening on this VM?
#
#   POST {CONSTRUCT_SERVICE_URL}/api/v1/vms/{instance}/activity
#   {"busy":true,"reasons":["ssh-session","agent-log:claude"]}
#
# The service saves a VM whose forwards are idle AND whose guest says it is idle,
# so a `busy` heartbeat is what keeps an unattended agent job alive with nobody
# connected. The probes read what says WORK is happening -- a connection, an
# agent transcript being written, a T3 Code thread at work, provisioning -- and
# nothing that merely says a process exists: resident agent servers burn CPU
# around the clock and would otherwise pin every VM as busy forever
# (decided 2026-09-17). An agent waiting on a question writes nothing and
# is idle by design.
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
PROVISION_MARKER="${CONSTRUCT_PROVISION_MARKER:-/run/construct/provisioning}"
PROC_DIR="${CONSTRUCT_IDLE_PROC_DIR:-/proc}"
SS_CMD="${CONSTRUCT_IDLE_SS:-ss}"
WHO_CMD="${CONSTRUCT_IDLE_WHO:-who}"
CURL="${CONSTRUCT_IDLE_CURL:-${CONSTRUCT_CURL:-curl}}"
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

# (b) An agent is actually working: its own transcript is being written. Claude
# Code appends to ~/.claude/projects/<project>/<session>.jsonl, Codex to
# ~/.codex/sessions/<y>/<m>/<d>/rollout-*.jsonl, OpenCode to its SQLite database
# (~/.local/share/opencode/opencode.db and its -wal). A record that moved within
# the report interval means a turn is in progress. An agent blocked on a question
# writes nothing and therefore reads as idle -- intended. Homes: root plus every
# /home/* (CONSTRUCT_IDLE_AGENT_HOMES overrides, colon-separated).
AGENT_HOMES="${CONSTRUCT_IDLE_AGENT_HOMES:-}"
if [[ -z "${AGENT_HOMES}" ]]; then
  AGENT_HOMES="/root"
  for _home in /home/*; do [[ -d "${_home}" ]] && AGENT_HOMES="${AGENT_HOMES}:${_home}"; done
fi
# The interval plus a margin: a report that runs a little late must not miss the
# write that happened just before the previous one.
ACTIVITY_WINDOW=$(( INTERVAL + 30 ))

_recent_file_in() { # <dir> <find name tests...>: true when any matching file changed within the window
  local dir="$1"; shift
  [[ -d "${dir}" ]] || return 1
  find "${dir}" -type f \( "$@" \) -newermt "@$(( $(date +%s) - ACTIVITY_WINDOW ))" -print -quit 2>/dev/null | grep -q .
}

probe_agent_transcripts() {
  local home
  local -a homes
  IFS=':' read -r -a homes <<<"${AGENT_HOMES}"
  for home in "${homes[@]}"; do
    [[ -n "${home}" && -d "${home}" ]] || continue
    _recent_file_in "${home}/.claude/projects" -name '*.jsonl' && add_reason "agent-log:claude"
    _recent_file_in "${home}/.codex/sessions" -name 'rollout-*.jsonl' && add_reason "agent-log:codex"
    _recent_file_in "${home}/.local/share/opencode" -name 'opencode.db' -o -name 'opencode.db-wal' && add_reason "agent-log:opencode"
  done
  return 0
}

# (c) T3 Code has a thread at work: a session in status "running" whose thread is
# not waiting on the user (no pending input, no pending approval) -- the GUI's
# "working" and "monitoring" states. A thread blocked on a question or stopped is
# not work. Read from T3's SQLite projections, read-only, so the live server is
# never disturbed. No python3, no database or an unreadable one means this probe
# says nothing; the transcript probe still sees the agent such a thread drives.
probe_t3_threads() {
  local home db count
  local -a homes
  command -v python3 >/dev/null 2>&1 || return 0
  IFS=':' read -r -a homes <<<"${AGENT_HOMES}"
  for home in "${homes[@]}"; do
    db="${home}/.t3/userdata/state.sqlite"
    [[ -f "${db}" ]] || continue
    count="$(python3 - "${db}" 2>/dev/null <<'PY'
import sqlite3, sys
try:
    c = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True, timeout=2)
    n = c.execute("""select count(*) from projection_thread_sessions s
                     join projection_threads t on t.thread_id = s.thread_id
                     where s.status = 'running' and t.deleted_at is null
                       and coalesce(t.pending_user_input_count, 0) = 0
                       and coalesce(t.pending_approval_count, 0) = 0""").fetchone()[0]
    print(n)
except Exception:
    print("")
PY
)"
    if [[ "${count}" =~ ^[0-9]+$ ]] && (( count > 0 )); then
      add_reason "t3-thread-running"
      return 0
    fi
  done
  return 0
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
  probe_agent_transcripts
  probe_t3_threads
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
