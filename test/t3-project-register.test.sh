#!/usr/bin/env bash
# Plain-Bash regression tests for register_t3_projects in bin/install-ai-tools.sh.
# Run: bash test/t3-project-register.test.sh
#
# The cost this guards against: every T3 restart ran `t3 project add` for every
# workspace repo. Each call starts the whole T3 CLI and fetches the server's
# snapshot (~1.4 s), so a VM with 45 repos spent a minute re-adding projects the
# server already had. Repos the server lists are now skipped; when the list is
# unknown, every repo is offered as before.
#
# Everything runs against a scratch workspace with stubbed t3 and curl.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
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

ws="${tmp}/workspace"
mkdir -p "${ws}/alpha/.git" "${ws}/beta/.git" "${ws}/gamma/.git" "${ws}/notes" "${tmp}/stub"

# t3 records every add; curl records its arguments and answers with the fixture
# in SHELL_FIXTURE, or fails like `curl -f` on an HTTP error when it is unset.
cat >"${tmp}/stub/t3" <<EOF
#!/usr/bin/env bash
echo "\$*" >>"${tmp}/t3.log"
EOF
cat >"${tmp}/stub/curl" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$@" >>"${tmp}/curl.log"
[[ -n "\${SHELL_FIXTURE:-}" ]] || exit 22
cat "\${SHELL_FIXTURE}"
EOF
chmod +x "${tmp}/stub/t3" "${tmp}/stub/curl"

cat >"${tmp}/shell.json" <<EOF
{"schemaVersion":2,"threads":[],"projects":[
  {"id":"p1","workspaceRoot":"${ws}/alpha","deletedAt":null},
  {"id":"p2","workspaceRoot":"${ws}/gamma","deletedAt":"2026-10-01T00:00:00Z"},
  {"id":"p3","workspaceRoot":"/elsewhere/other","deletedAt":null}
]}
EOF
echo "test-token" >"${tmp}/token"

run_case() {
  local name="$1"
  shift
  rm -f "${tmp}/t3.log" "${tmp}/curl.log"
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" \
    env WORKSPACE_ROOT="${ws}" T3CODE_PORT=5999 PATH="${tmp}/stub:/usr/bin:/bin" "$@" \
    bash -c 'source "${SCRIPT_PATH}"; register_t3_projects' >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
  touch "${tmp}/t3.log" "${tmp}/curl.log"
}
added() { grep -c '^project add ' "${tmp}/t3.log"; }
has_add() { grep -qxF "project add ${ws}/$1 --log-level none" "${tmp}/t3.log"; }

# ── server lists some repos: only the missing ones are added ─────────────────
run_case listed T3CODE_API_TOKEN_FILE="${tmp}/token" SHELL_FIXTURE="${tmp}/shell.json"
ok "listed: succeeds" test "$(cat "${tmp}/listed.rc")" = 0
ok "listed: a registered repo is skipped" bash -c "! grep -q '/alpha ' '${tmp}/t3.log'"
ok "listed: an unregistered repo is added" has_add beta
ok "listed: a deleted project's repo is added again" has_add gamma
ok "listed: a directory without .git is not added" bash -c "! grep -q '/notes' '${tmp}/t3.log'"
ok "listed: exactly two adds" test "$(added)" = 2
ok "listed: the API call carries the token" grep -qxF "Authorization: Bearer test-token" "${tmp}/curl.log"
ok "listed: the API call uses the configured port" grep -qxF "http://127.0.0.1:5999/api/orchestration/shell" "${tmp}/curl.log"

# ── no token yet: every repo is offered, the API is not called ───────────────
run_case notoken T3CODE_API_TOKEN_FILE="${tmp}/missing" SHELL_FIXTURE="${tmp}/shell.json"
ok "no token: succeeds" test "$(cat "${tmp}/notoken.rc")" = 0
ok "no token: all three repos are offered" test "$(added)" = 3
ok "no token: the API is not called" test ! -s "${tmp}/curl.log"

# ── server does not answer: every repo is offered ────────────────────────────
run_case down T3CODE_API_TOKEN_FILE="${tmp}/token"
ok "server down: succeeds" test "$(cat "${tmp}/down.rc")" = 0
ok "server down: all three repos are offered" test "$(added)" = 3

# ── unreadable answer: every repo is offered ─────────────────────────────────
echo "not json" >"${tmp}/bad.json"
run_case badjson T3CODE_API_TOKEN_FILE="${tmp}/token" SHELL_FIXTURE="${tmp}/bad.json"
ok "bad answer: succeeds" test "$(cat "${tmp}/badjson.rc")" = 0
ok "bad answer: all three repos are offered" test "$(added)" = 3

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
