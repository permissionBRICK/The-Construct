#!/usr/bin/env bash
# Plain-Bash regression tests for provision.sh's free-disk preflight.
# Run: bash test/provision-diskcheck.test.sh
#
# Why this check exists: on a full VM disk ext4's 5% root reserve keeps root's
# writes working while every write as another user fails, which surfaced in the
# field as `git config` for the agent user dying with "failed to write new
# configuration file /home/agent/.gitconfig.lock" right after root's identity was
# written fine. The preflight has to call that out BEFORE the installs run.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROVISION="${ROOT}/bin/provision.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
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

# Source only the step runner + the (pure) disk helpers, exactly like the
# step-runner suite does, then evaluate the case body.
run_case() {
  local name="$1" body="$2"
  shift 2
  CONSTRUCT_STEP_RUNNER_ONLY=true PROVISION_PATH="${PROVISION}" CASE_BODY="${body}" \
    env "$@" bash -c 'source "${PROVISION_PATH}"; eval "${CASE_BODY}"' \
    >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

_verdict_case=0
verdict_is() {
  local kb="$1" want="$2" name got
  _verdict_case=$((_verdict_case + 1))
  name="verdict-${_verdict_case}"
  run_case "${name}" "_disk_verdict '${kb}'"
  got="$(cat "${tmp}/${name}.out")"
  [[ "${got}" == "${want}" ]]
}

# ── _disk_verdict: the pure classification ───────────────────────────────────
ok "0 KiB free is full" verdict_is 0 full
ok "just under 256 MiB is full" verdict_is 262143 full
ok "exactly 256 MiB is low" verdict_is 262144 low
ok "just under 2 GiB is low" verdict_is 2097151 low
ok "exactly 2 GiB is ok" verdict_is 2097152 ok
ok "plenty of space is ok" verdict_is 500000000 ok
ok "empty reading is unknown" verdict_is "" unknown
ok "non-numeric reading is unknown" verdict_is "n/a" unknown

# ── check_disk_space: verdict -> exit status + message ───────────────────────
# Real filesystems, but with thresholds moved so the outcome is deterministic on
# any machine: an impossible floor forces "full", a zero floor forces "ok".
run_case full 'check_disk_space' _DISK_FULL_KB=999999999999 _DISK_LOW_KB=999999999999 SSH_USER=agent
ok "a full disk fails the check" test "$(cat "${tmp}/full.rc")" = 1
ok "a full disk says so" grep -q 'the disk is FULL' "${tmp}/full.out"
ok "a full disk explains the root-reserve asymmetry" grep -q "5% reserve for root" "${tmp}/full.out"
ok "a full disk names the gitconfig symptom" grep -q '/home/agent/.gitconfig.lock' "${tmp}/full.out"
ok "a full disk points at the escape hatch" grep -q 'ALLOW_LOW_DISK=true to provision anyway' "${tmp}/full.out"

run_case override 'check_disk_space' _DISK_FULL_KB=999999999999 _DISK_LOW_KB=999999999999 ALLOW_LOW_DISK=true
ok "ALLOW_LOW_DISK=true downgrades the failure" test "$(cat "${tmp}/override.rc")" = 0
ok "ALLOW_LOW_DISK=true still warns" grep -q 'continuing on a full disk' "${tmp}/override.out"

run_case low 'check_disk_space' _DISK_FULL_KB=1 _DISK_LOW_KB=999999999999
ok "a low disk passes the check" test "$(cat "${tmp}/low.rc")" = 0
ok "a low disk warns about space" grep -q 'installs may run out of space' "${tmp}/low.out"

run_case fine 'check_disk_space' _DISK_FULL_KB=1 _DISK_LOW_KB=2
ok "enough space passes the check" test "$(cat "${tmp}/fine.rc")" = 0
ok "enough space reports GiB free" grep -q 'GiB free' "${tmp}/fine.out"
# / and /home are one filesystem on a stock VM: report it once, not four times.
ok "each filesystem is reported once" test "$(grep -c 'GiB free' "${tmp}/fine.out")" = "$(df -P -k / /home /root/repos /var 2>/dev/null | awk 'NR>1 {print $1}' | sort -u | wc -l)"

# ── _git_write_diag: turn git's cryptic lock error into the real reason ──────
run_case diag_full '_git_write_diag agent /home/agent' _DISK_FULL_KB=999999999999 _DISK_LOW_KB=999999999999
ok "a failed git write on a full disk is explained" grep -q 'not a permissions problem' "${tmp}/diag_full.out"
ok "the explanation names the user whose config failed" grep -q "git could not write agent's config" "${tmp}/diag_full.out"

run_case diag_ok '_git_write_diag agent /home/agent' _DISK_FULL_KB=1 _DISK_LOW_KB=2
ok "a failure with space left is not blamed on the disk" test ! -s "${tmp}/diag_ok.out"

run_case diag_nopath '_git_write_diag agent /no/such/path' _DISK_FULL_KB=999999999999
ok "an unreadable home produces no bogus diagnosis" test ! -s "${tmp}/diag_nopath.out"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
