#!/usr/bin/env bash
# Regression tests for test/helpers/isolate-env.sh: a suite started from a Construct VM's
# shell must not pick up the VM's own CONSTRUCT_*/T3CODE_* settings.
# Run: bash test/isolate-env.test.sh
#
# The "leaked" environment points at a local listener that records every connection, so
# nothing here can reach a real host service.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
listener=""
trap '[[ -n "${listener}" ]] && kill "${listener}" 2>/dev/null; rm -rf "${tmp}"' EXIT

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

# ── the helper itself ────────────────────────────────────────────────────────

# shellcheck disable=SC2016  # expanded by the inner shell
left="$(env CONSTRUCT_SERVICE_URL=https://service.invalid CONSTRUCT_INSTANCE_NAME=vm-1 \
  T3CODE_PATCHED=true T3CODE_CHANNEL=nightly CONSTRUCTOR=kept MY_T3CODE_X=kept \
  bash -c '. "$1"; compgen -e | grep -E "^(CONSTRUCT|T3CODE)_" || true; printf "%s %s\n" "${CONSTRUCTOR:-}" "${MY_T3CODE_X:-}"' \
  _ "${ROOT}/test/helpers/isolate-env.sh")"
ok "every CONSTRUCT_* and T3CODE_* variable is gone" test "${left}" = "kept kept"

# ── the suites that used to inherit them ─────────────────────────────────────

port="$(python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')"
python3 -I - "${port}" "${tmp}/connections" <<'PY' &
import socket, sys
server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("127.0.0.1", int(sys.argv[1])))
server.listen()
while True:
    conn, _ = server.accept()
    with open(sys.argv[2], "a") as log:
        log.write("connection\n")
    conn.close()
PY
listener=$!
for _ in $(seq 1 50); do
  python3 -I -c 'import socket, sys; socket.create_connection(("127.0.0.1", int(sys.argv[1])), 1).close()' "${port}" 2>/dev/null && break
  sleep 0.1
done
rm -f "${tmp}/connections"

leaked=(env CONSTRUCT_SERVICE_URL="http://127.0.0.1:${port}" CONSTRUCT_INSTANCE_NAME=vm-1
  CONSTRUCT_VM_TOKEN_FILE="${tmp}/token" T3CODE_PATCHED=true T3CODE_CHANNEL=stable T3CODE_BUILD_SOURCE=prebuilt)
printf 'leaked-token\n' >"${tmp}/token"

"${leaked[@]}" bash "${ROOT}/test/construct-expose.test.sh" >"${tmp}/expose.out" 2>&1
ok "construct-expose passes with the VM's service settings in the environment" test "$?" = 0
ok "  and never connects to that service" test ! -e "${tmp}/connections"

if [[ "${EUID}" -eq 0 ]]; then
  "${leaked[@]}" bash "${ROOT}/test/t3-update.test.sh" >"${tmp}/t3-update.out" 2>&1
  ok "t3-update passes with the VM's T3 settings in the environment" test "$?" = 0
fi

echo ""
echo "  isolate-env tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
