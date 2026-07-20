#!/usr/bin/env bash
# Plain-Bash regression tests for provision.sh's streaming step runner.
# Run: bash test/provision-steprunner.test.sh

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

run_case() {
  local name="$1" out rc_file persist_dir
  out="${tmp}/${name}.out"
  rc_file="${tmp}/${name}.rc"
  # Each case gets its own persistent log dir under $tmp so the test never
  # touches the production path (/var/log/construct/provision). The dir is
  # created up front so _record_step_failure's cp succeeds and the third
  # marker field is populated.
  persist_dir="${tmp}/plog-${name}"
  mkdir -p "${persist_dir}"
  CONSTRUCT_STEP_RUNNER_ONLY=true PROVISION_PATH="${PROVISION}" \
    PERSIST_DIR="${persist_dir}" \
    bash -c 'source "${PROVISION_PATH}"; _PERSISTENT_LOG_DIR="${PERSIST_DIR}"; eval "${CASE_BODY}"' \
    >"${out}" 2>&1
  printf '%s' "$?" >"${rc_file}"
}

set +e
CASE_BODY='run_step optional "optional boom" bash -c "echo before; echo detail >&2; exit 7"; echo AFTER_OPTIONAL; _finish_provision 0'
export CASE_BODY
run_case optional
CASE_BODY='run_step optional "earlier optional" bash -c "echo earlier-detail; exit 4"; run_step critical "critical boom" bash -c "echo critical-detail >&2; exit 9"; echo AFTER_CRITICAL; _finish_provision 0'
export CASE_BODY
run_case critical
CASE_BODY='run_step optional "tail test" bash -c "seq 1 20; echo tail-error >&2; exit 5"; _finish_provision 0'
export CASE_BODY
run_case tail
CASE_BODY='run_step optional "bad|title" bash -c "exit 4"; _finish_provision 0'
export CASE_BODY
run_case sentinel
CASE_BODY='run_step optional "clean" bash -c "echo clean-output"; _finish_provision 0'
export CASE_BODY
run_case clean
set -e

ok "optional failure continues" grep -q '^AFTER_OPTIONAL$' "${tmp}/optional.out"
ok "optional failure is reported immediately on stdout" grep -q '^STEP FAILED (continuing): optional boom (exit 7)$' "${tmp}/optional.out"
ok "optional-only run exits 3" test "$(cat "${tmp}/optional.rc")" = 3
ok "optional failure is recorded" grep -qF "error=optional boom|7|${tmp}/plog-optional/step-0-optional-boom.log" "${tmp}/optional.out"

ok "critical failure aborts before the next command" sh -c "! grep -q '^AFTER_CRITICAL$' '$tmp/critical.out'"
ok "critical failure preserves its nonzero exit" test "$(cat "${tmp}/critical.rc")" = 9
ok "critical failure prints summary-so-far" grep -q '^PROVISION FAILED -- 2 step(s) failed:$' "${tmp}/critical.out"
ok "critical summary retains an earlier optional failure" grep -qF "error=earlier optional|4|${tmp}/plog-critical/step-0-earlier-optional.log" "${tmp}/critical.out"
ok "critical output is captured" grep -q 'critical-detail' "${tmp}/critical.out"

ok "tail includes the last merged stderr line" grep -q '^      tail-error$' "${tmp}/tail.out"
ok "tail includes the fifteenth line from the end" grep -q '^      7$' "${tmp}/tail.out"
ok "tail excludes older output" sh -c "! grep -q '^      6$' '$tmp/tail.out'"

sentinel="$(sed -n '/^===CONSTRUCT-PROVISION-RESULT===$/,/^===END-CONSTRUCT-PROVISION-RESULT===$/p' "${tmp}/sentinel.out")"
# Third field is the persistent log path. The sanitized title "bad|title" becomes
# "bad title" -> "bad-title" in the filename slug.
expected="$(printf '%s\n' \
  '===CONSTRUCT-PROVISION-RESULT===' \
  'errors=1' \
  "error=bad title|4|${tmp}/plog-sentinel/step-0-bad-title.log" \
  '===END-CONSTRUCT-PROVISION-RESULT===')"
ok "sentinel block format is exact and title is sanitized" test "${sentinel}" = "${expected}"

ok "clean run exits 0" test "$(cat "${tmp}/clean.rc")" = 0
clean_sentinel="$(sed -n '/^===CONSTRUCT-PROVISION-RESULT===$/,/^===END-CONSTRUCT-PROVISION-RESULT===$/p' "${tmp}/clean.out")"
clean_expected="$(printf '%s\n' \
  '===CONSTRUCT-PROVISION-RESULT===' \
  'errors=0' \
  '===END-CONSTRUCT-PROVISION-RESULT===')"
ok "clean sentinel block format is exact" test "${clean_sentinel}" = "${clean_expected}"
ok "clean run prints all-good line" grep -q '^ALL PROVISIONING STEPS COMPLETED CLEANLY$' "${tmp}/clean.out"

# The persistent log file must actually exist and contain the step's output.
ok "persistent log file exists" test -f "${tmp}/plog-sentinel/step-0-bad-title.log"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
