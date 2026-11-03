#!/usr/bin/env bash
# Regression tests for the pkill/pgrep/pidwait guard:
#   bin/construct-procps-guard.sh  — adds --ignore-ancestors, so `pkill -f` from an agent's
#                                     `bash -c '<command>'` no longer kills that shell
#   bin/provision.sh               — setup_procps_guard installs it, keeps foreign files,
#                                     and leaves stock procps alone when it is too old
# Run: bash test/procps-guard.test.sh
#
# Field failure: Claude Code runs each Bash tool call as `bash -c '... eval <command> ...'`.
# A `pkill -f <pattern>` matched that shell too, killed it, and the agent got a bare
# "Exit code 144" (56 times in one VM's transcripts); everything after pkill never ran.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GUARD="${ROOT}/bin/construct-procps-guard.sh"
tmp="$(mktemp -d)"
targets=()
cleanup() {
  local pid
  for pid in "${targets[@]}"; do kill "${pid}" 2>/dev/null; done
  rm -rf "${tmp}"
}
trap cleanup EXIT

pass=0
fail=0
check() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

check "the guard parses as POSIX sh" sh -n "${GUARD}"

# ── the guard against real procps ────────────────────────────────────────────

if /usr/bin/pgrep --help 2>/dev/null | grep -q -- '--ignore-ancestors'; then
  guarded="${tmp}/guarded"
  mkdir -p "${guarded}"
  for name in pkill pgrep pidwait; do install -m 0755 "${GUARD}" "${guarded}/${name}"; done

  # A target whose argv no caller of this test can contain: built at run time.
  tag="procps-guard-$$-${RANDOM}"
  start_target() {
    bash -c 'exec -a "$0" sleep 300' "${tag}" &
    targets+=("$!")
    sleep 0.2
  }
  target_alive() { kill -0 "${targets[-1]}" 2>/dev/null; }

  start_target
  out="$(/usr/bin/bash -c "/usr/bin/pkill -f '${tag}'; echo survived" 2>&1)"
  check "control: stock pkill -f kills the bash -c that runs it" test "${out}" != survived
  wait "${targets[-1]}" 2>/dev/null

  start_target
  out="$(PATH="${guarded}:${PATH}" /usr/bin/bash -c "pkill -f '${tag}'; echo \"survived \$?\"" 2>&1)"
  check "guarded pkill -f leaves the calling shell running" test "${out}" = "survived 0"
  sleep 0.2
  check "guarded pkill -f still kills the target" sh -c "! kill -0 ${targets[-1]} 2>/dev/null"

  start_target
  out="$(PATH="${guarded}:${PATH}" /usr/bin/bash -c "pkill -9 -A -f '${tag}'; echo \"survived \$?\"" 2>&1)"
  check "a signal and a duplicate -A from the caller still work" test "${out}" = "survived 0"
  wait "${targets[-1]}" 2>/dev/null

  # A compound command: bash would exec a lone `pgrep` and leave no shell behind.
  out="$(PATH="${guarded}:${PATH}" /usr/bin/bash -c "pgrep -fc '${tag}'; :" 2>&1)"
  check "guarded pgrep -f does not report the calling shell" test "${out}" = 0

  start_target
  out="$(PATH="${guarded}:${PATH}" /usr/bin/bash -c "kill \$(pgrep -f '${tag}'); echo \"survived \$?\"" 2>&1)"
  check "kill \$(pgrep -f ...) kills only the target" test "${out}" = "survived 0"
  sleep 0.2
  check "kill \$(pgrep -f ...) target is gone" sh -c "! kill -0 ${targets[-1]} 2>/dev/null"

  out="$(PATH="${guarded}:${PATH}" pkill 2>&1)"
  check "usage errors still come from procps (exit 2)" test "$?" -eq 2
else
  printf '  SKIP  guard behaviour: /usr/bin/pgrep has no --ignore-ancestors\n'
fi

# ── provision.sh: setup_procps_guard ─────────────────────────────────────────

fake_procps() { # $1 dir, $2 help text
  mkdir -p "$1"
  for name in pkill pgrep pidwait; do
    printf '#!/bin/sh\necho "%s"\n' "$2" >"$1/${name}"
    chmod +x "$1/${name}"
  done
}
run_setup() { # $1 bin dir, $2 procps dir
  (
    export REPO_DIR="${ROOT}" CONSTRUCT_BIN_DIR="$1" CONSTRUCT_PROCPS_DIR="$2"
    # shellcheck disable=SC2317  # called by the eval'd setup_procps_guard
    ok() { :; }
    # shellcheck disable=SC2317
    note() { :; }
    eval "$(sed -n '/^setup_procps_guard() {/,/^}/p' "${ROOT}/bin/provision.sh")"
    setup_procps_guard
  )
}

new="${tmp}/procps-new"
old="${tmp}/procps-old"
fake_procps "${new}" ' -A, --ignore-ancestors    exclude our ancestors from results'
fake_procps "${old}" ' -v, --inverse             negates the matching'

bins="${tmp}/bin"
mkdir -p "${bins}"
run_setup "${bins}" "${new}"
check "provision: setup_procps_guard succeeds" test "$?" -eq 0
for name in pkill pgrep pidwait; do
  check "provision: ${name} is the guard, executable" \
    sh -c "test -x '${bins}/${name}' && cmp -s '${GUARD}' '${bins}/${name}'"
done

bins="${tmp}/bin-foreign"
mkdir -p "${bins}"
printf '#!/bin/sh\n# my own pgrep\n' >"${bins}/pgrep"
run_setup "${bins}" "${new}"
check "provision: a pgrep not installed by The Construct is kept" grep -q 'my own pgrep' "${bins}/pgrep"
check "provision: the others are still installed next to it" cmp -s "${GUARD}" "${bins}/pkill"

run_setup "${bins}" "${old}"
check "provision: old procps removes the guard again" test ! -e "${bins}/pkill" -a ! -e "${bins}/pidwait"
check "provision: old procps keeps the foreign pgrep" grep -q 'my own pgrep' "${bins}/pgrep"

check "provision: the step runs on every provision" \
  grep -qx 'run_step optional "Keeping pkill/pgrep off the calling shell" setup_procps_guard' "${ROOT}/bin/provision.sh"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
(( fail == 0 ))
