#!/usr/bin/env bash
# Plain-Bash tests for bin/install-sdks.sh's apt calls: they wait for apt's locks instead of
# failing when another apt run holds one. Run: bash test/install-sdks.test.sh
#
# apt-get is a stub on PATH; nothing is installed.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-sdks.sh"
if [[ "${EUID}" -ne 0 ]]; then
  echo "  SKIP  install-sdks tests need root (the script refuses to run without it)"
  exit 0
fi
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
# `update` fails ${LOCKED_UPDATES} times on a held package-list lock (or with ${UPDATE_ERROR}
# instead), then succeeds; `install` succeeds.
cat >"${tmp}/stub/apt-get" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${CALLS}"
case " $* " in
  *" update "*)
    n=$(( $(cat "${COUNT}" 2>/dev/null || echo 0) + 1 )); echo "${n}" >"${COUNT}"
    if [[ -n "${UPDATE_ERROR:-}" ]]; then echo "${UPDATE_ERROR}" >&2; exit 100; fi
    if (( n <= LOCKED_UPDATES )); then
      # apt translates its messages; only the C locale says "Could not get lock".
      if [[ "${LC_ALL:-}" == C ]]; then
        echo "E: Could not get lock /var/lib/apt/lists/lock. It is held by process 4242 (apt-get)" >&2
      else
        echo "E: Sperre /var/lib/apt/lists/lock konnte nicht erlangt werden. Sie wird von Prozess 4242 (apt-get) gehalten." >&2
      fi
      exit 100
    fi ;;
esac
exit 0
EOF
chmod +x "${tmp}/stub/apt-get"
printf '{"sdks":{"python":"3"}}\n' >"${tmp}/generated.json"

run() {
  rm -f "${tmp}/calls" "${tmp}/count"
  env PATH="${tmp}/stub:${PATH}" CALLS="${tmp}/calls" COUNT="${tmp}/count" GENERATED_JSON="${tmp}/generated.json" \
    APT_LOCK_RETRY_SECONDS=0 "$@" bash "${SCRIPT}" >"${tmp}/out" 2>&1
  echo $? >"${tmp}/rc"
}
rc_is() { [[ "$(cat "${tmp}/rc")" == "$1" ]]; }
updates() { grep -c ' update$' "${tmp}/calls"; }
installs() { grep -c ' install -y python3 python3-venv python3-pip$' "${tmp}/calls"; }
all_wait_for_dpkg() { ! grep -v -- '-o DPkg::Lock::Timeout=900 ' "${tmp}/calls"; }

run LOCKED_UPDATES=2
ok "a held package-list lock is waited out" rc_is 0
ok "  update is retried until the lock is free" test "$(updates)" = 3
ok "  then the toolchain is installed once" test "$(installs)" = 1
ok "  every apt-get call waits for the dpkg lock too" all_wait_for_dpkg
ok "  and the wait is reported" grep -q 'apt is busy' "${tmp}/out"

run LOCKED_UPDATES=2 LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 LANGUAGE=de
ok "a caller in another language still waits for the lock" rc_is 0
ok "  with the same retries" test "$(updates)" = 3

run LOCKED_UPDATES=0 UPDATE_ERROR="E: Failed to fetch http://archive.example/dists/noble/InRelease"
ok "any other apt failure is not retried" test "$(updates)" = 1
ok "  and fails the runtime" rc_is 1
ok "  with apt's own error shown" grep -q 'Failed to fetch' "${tmp}/out"

run LOCKED_UPDATES=100 APT_LOCK_TIMEOUT=0
ok "a lock still held when the timeout is over fails the runtime" rc_is 1
ok "  after one attempt with a zero timeout" test "$(updates)" = 1

echo ""
echo "  install-sdks tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
