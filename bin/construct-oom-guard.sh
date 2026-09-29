#!/usr/bin/env bash
# Rank the coding agents low for the kernel OOM killer, without making them unkillable.
#
# Every agent session and everything it starts (builds, tests, browsers) lives in
# the cgroup of the unit that hosts it (t3code-serve, codex-app-server,
# opencode-serve). When memory runs out, the kernel should take a build or a
# browser first: an agent can restart those, but a dead T3 server takes every
# thread with it. So, every few seconds:
#
#   T3 server (main process of the server unit)          CONSTRUCT_OOM_GUARD_SERVER_ADJ (-200)
#   agent CLIs (claude, codex, opencode; host unit mains) CONSTRUCT_OOM_GUARD_AGENT_ADJ  (-100)
#   everything else in those units                        at least 0
#
# The adjustments are moderate on purpose: oom_score is roughly the process's
# share of RAM in 1/1000ths plus the adjustment, so a T3 server that leaks past
# ~20% of RAM still outranks an ordinary build and gets killed and restarted.
#
# oom_score_adj is inherited on fork, so every child of the server or an agent
# starts with the lowered value; the sweep lifts those back to 0. Positive values
# a process chose for itself (Chrome renderers use 300) are left alone.
#
# Run: construct-oom-guard.sh [--once]
# Overridable for tests: CONSTRUCT_OOM_GUARD_{UNITS,SERVER_UNIT,PROC,CGROUP_ROOT,INTERVAL_SEC}, CONSTRUCT_SYSTEMCTL.

set -u
shopt -s globstar nullglob

read -r -a UNITS <<<"${CONSTRUCT_OOM_GUARD_UNITS:-t3code-serve.service codex-app-server.service opencode-serve.service}"
SERVER_UNIT="${CONSTRUCT_OOM_GUARD_SERVER_UNIT:-t3code-serve.service}"
SERVER_ADJ="${CONSTRUCT_OOM_GUARD_SERVER_ADJ:--200}"
AGENT_ADJ="${CONSTRUCT_OOM_GUARD_AGENT_ADJ:--100}"
AGENT_COMM_RE='^(claude|codex|opencode)'
PROC_ROOT="${CONSTRUCT_OOM_GUARD_PROC:-/proc}"
CGROUP_ROOT="${CONSTRUCT_OOM_GUARD_CGROUP_ROOT:-/sys/fs/cgroup}"
SYSTEMCTL="${CONSTRUCT_SYSTEMCTL:-systemctl}"
INTERVAL="${CONSTRUCT_OOM_GUARD_INTERVAL_SEC:-2}"

# $1 pid, $2 wanted adj, $3 "floor" = only raise a negative value to $2.
set_adj() {
  local file="${PROC_ROOT}/$1/oom_score_adj" cur
  read -r cur <"${file}" 2>/dev/null || return 0
  if [[ "$3" == floor ]]; then
    (( cur < $2 )) || return 0
  else
    (( cur == $2 )) && return 0
  fi
  # The process may exit between the read and the write; that is fine.
  printf '%s\n' "$2" >"${file}" 2>/dev/null || true
}

# $1 unit, $2 its main pid, $3 its control group
sweep_unit() {
  local unit="$1" main="$2" cg="$3" procs pid comm
  [[ -n "${cg}" && -d "${CGROUP_ROOT}${cg}" ]] || return 0
  for procs in "${CGROUP_ROOT}${cg}/cgroup.procs" "${CGROUP_ROOT}${cg}"/**/cgroup.procs; do
    while read -r pid; do
      [[ "${pid}" =~ ^[0-9]+$ ]] || continue
      if [[ "${pid}" == "${main}" ]]; then
        if [[ "${unit}" == "${SERVER_UNIT}" ]]; then set_adj "${pid}" "${SERVER_ADJ}" exact
        else set_adj "${pid}" "${AGENT_ADJ}" exact; fi
        continue
      fi
      comm=""
      read -r comm <"${PROC_ROOT}/${pid}/comm" 2>/dev/null || continue
      if [[ "${comm}" =~ ${AGENT_COMM_RE} ]]; then
        set_adj "${pid}" "${AGENT_ADJ}" exact
      else
        set_adj "${pid}" 0 floor
      fi
    done <"${procs}" 2>/dev/null
  done
}

sweep() {
  local line key value unit="" main="" cg=""
  # One systemctl call for all units: blocks of Id=/MainPID=/ControlGroup=, blank-line separated.
  while IFS= read -r line || [[ -n "${line}" ]]; do
    if [[ -z "${line}" ]]; then
      [[ -n "${unit}" ]] && sweep_unit "${unit}" "${main}" "${cg}"
      unit="" main="" cg=""
      continue
    fi
    key="${line%%=*}" value="${line#*=}"
    case "${key}" in
      Id) unit="${value}" ;;
      MainPID) main="${value}" ;;
      ControlGroup) cg="${value}" ;;
    esac
  done < <("${SYSTEMCTL}" show -p Id -p MainPID -p ControlGroup "${UNITS[@]}" 2>/dev/null; echo)
}

if [[ "${1:-}" == "--once" ]]; then
  sweep
  exit 0
fi
while :; do
  sweep
  sleep "${INTERVAL}"
done
