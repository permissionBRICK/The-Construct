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

# The listener binds a free port itself and publishes it once it listens, so every
# connection it logs comes from a suite under test.
python3 -I - "${tmp}/port" "${tmp}/connections" <<'PY' &
import os, socket, sys
server = socket.socket()
server.bind(("127.0.0.1", 0))
server.listen()
with open(sys.argv[1] + ".tmp", "w") as published:
    published.write(str(server.getsockname()[1]))
os.replace(sys.argv[1] + ".tmp", sys.argv[1])
while True:
    conn, _ = server.accept()
    with open(sys.argv[2], "a") as log:
        log.write("connection\n")
    conn.close()
PY
listener=$!
for _ in $(seq 1 100); do [[ -s "${tmp}/port" ]] && break; sleep 0.1; done
port="$(cat "${tmp}/port" 2>/dev/null)"
ok "the recording listener is up" test -n "${port}"

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
