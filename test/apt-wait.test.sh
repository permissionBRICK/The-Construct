#!/usr/bin/env bash
# Plain-Bash tests for bin/apt-wait.sh: apt_get waits out a held apt lock (Ubuntu's apt-daily timer held the
# package-list lock for five seconds right after a boot, FatPC 2026-11-22), gives up after APT_LOCK_TIMEOUT, passes
# other failures through at once, and no guest install script calls apt-get without it.
# Run: bash test/apt-wait.test.sh. apt-get is a stub; nothing is installed.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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

mkdir -p "${tmp}/stub"
# Fails ${LOCKED} times on the held package-list lock, then exits ${FINAL:-0}; each call is counted.
cat >"${tmp}/stub/apt-get" <<'STUB'
#!/usr/bin/env bash
n=$(( $(cat "${COUNT}" 2>/dev/null || echo 0) + 1 ))
echo "${n}" >"${COUNT}"
if (( n <= LOCKED )); then
  echo "E: Could not get lock /var/lib/apt/lists/lock. It is held by process 48692 (apt-get)" >&2
  echo "E: Unable to lock directory /var/lib/apt/lists/" >&2
  exit 100
fi
if [[ -n "${FAIL_WITH:-}" ]]; then echo "${FAIL_WITH}" >&2; exit 100; fi
exit 0
STUB
chmod +x "${tmp}/stub/apt-get"

run() {
  rm -f "${tmp}/count"
  rc=0
  env PATH="${tmp}/stub:${PATH}" COUNT="${tmp}/count" APT_LOCK_RETRY_SECONDS=0 "$@" \
    bash -c '. "$1"; apt_get update' _ "${ROOT}/bin/apt-wait.sh" >"${tmp}/out" 2>&1 || rc=$?
  calls="$(cat "${tmp}/count")"
}

run LOCKED=3
ok "a held lock is waited out" test "${rc}" -eq 0
ok "  apt-get ran again until the lock was free" test "${calls}" -eq 4
ok "  and the wait is reported" grep -q 'apt is busy' "${tmp}/out"

run LOCKED=100 APT_LOCK_TIMEOUT=0
ok "a lock held past the timeout fails with apt's exit code" test "${rc}" -eq 100
ok "  after one attempt" test "${calls}" -eq 1
ok "  with apt's own error" grep -q 'Could not get lock' "${tmp}/out"

run LOCKED=0 FAIL_WITH="E: Unable to locate package nosuchpackage"
ok "another failure is not retried" test "${rc}" -eq 100
ok "  (one attempt)" test "${calls}" -eq 1

# Every apt-get the guest's install scripts run goes through apt_get (a message naming apt-get is fine).
plain="$(grep -nE '^\s*apt-get\s+(update|install|upgrade|dist-upgrade|full-upgrade)\b' \
  "${ROOT}/bootstrap.sh" "${ROOT}"/bin/*.sh | grep -v '/bin/apt-wait.sh:' || true)"
ok "no guest script calls apt-get without waiting for its locks" test -z "${plain}"
[[ -z "${plain}" ]] || printf '%s\n' "${plain}"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
(( fail == 0 ))
