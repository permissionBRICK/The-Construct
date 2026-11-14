#!/usr/bin/env bash
# Tests for `construct update status|run` (bin/construct-update.sh): the VM's own update
# route at its engine. Run: bash test/construct-update.test.sh
#
# curl is a stub that plays the engine; every run gets a clean environment, so the
# machine's own service settings never reach a real host.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
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

stubs="${tmp}/stubs"
mkdir -p "${stubs}"
# curl: records method, URL, headers and the whole argv; answers per method from
# ${STUB_DIR}/<METHOD>.code and <METHOD>.json; ${STUB_DIR}/exit makes it fail like no answer.
cat >"${stubs}/curl" <<'STUB'
#!/usr/bin/env bash
d="${STUB_DIR}"
out=""; method="GET"; url=""; hdrfile=""
args=("$@")
printf '%s\n' "$*" >>"${d}/argv"
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -o) out="${args[i+1]}" ;;
    -X) method="${args[i+1]}" ;;
    -H) case "${args[i+1]}" in @*) hdrfile="${args[i+1]#@}" ;; esac ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
printf '%s %s\n' "${method}" "${url}" >>"${d}/requests"
[[ -n "${hdrfile}" ]] && cat "${hdrfile}" >>"${d}/headers"
if [[ -f "${d}/exit" ]]; then echo "curl: (7) Failed to connect" >&2; printf '000'; exit 7; fi
[[ -n "${out}" && -f "${d}/${method}.json" ]] && cp "${d}/${method}.json" "${out}"
printf '%s' "$(cat "${d}/${method}.code" 2>/dev/null || echo 404)"
STUB
chmod +x "${stubs}/curl"

stub_dir="${tmp}/engine"
token="${tmp}/vm-token"
printf 'vm-secret-token\n' >"${token}"
cfg="${tmp}/config.env"
printf 'CONSTRUCT_SERVICE_URL=https://host.example:7462\nCONSTRUCT_INSTANCE_NAME=work-vm\n' >"${cfg}"
out="${tmp}/out"
err="${tmp}/err"

reset() { rm -rf "${stub_dir}"; mkdir -p "${stub_dir}"; }
answer() { printf '%s' "$2" >"${stub_dir}/$1.code"; printf '%s' "$3" >"${stub_dir}/$1.json"; }
# update <args...>: the CLI through the dispatcher; stdout -> $out, stderr -> $err; status in $rc.
update() {
  rc=0
  env -i PATH="${stubs}:/usr/bin:/bin" HOME="${tmp}" STUB_DIR="${stub_dir}" CONFIG_FILE="${cfg}" \
    CONSTRUCT_VM_TOKEN_FILE="${token}" bash "${CLI}" update "$@" >"${out}" 2>"${err}" || rc=$?
}
one_line_err() { test "$(wc -l <"${err}")" -eq 1; }

status_json='{"newerPayload":true,"availableCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","appliedCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","hostUpdate":{"version":"3.2.0+cccccccc","commit":"cccccccccccccccccccccccccccccccccccccccc"}}'
run_json='{"jobId":null,"hostUpdateId":"u-1","hostJobId":"j-9"}'

# ── help ─────────────────────────────────────────────────────────────────────
reset
update --help
ok "--help exits 0" test "${rc}" = 0
ok "--help names both commands" sh -c "grep -q 'construct update status' '${out}' && grep -q 'construct update run' '${out}'"
ok "construct --help lists update" sh -c "bash '${CLI}' --help | grep -q 'construct update status|run'"
update frobnicate
ok "an unknown command is a usage error" sh -c "test ${rc} = 1 && test ! -s '${out}'"

# ── status ───────────────────────────────────────────────────────────────────
reset
answer GET 200 "${status_json}"
update status --json
ok "status --json exits 0" test "${rc}" = 0
ok "status --json prints the engine's answer unchanged" test "$(cat "${out}")" = "${status_json}"
ok "status --json prints nothing on stderr" test ! -s "${err}"
ok "status asks this VM's route" test "$(cat "${stub_dir}/requests")" = "GET https://host.example:7462/api/v1/vms/work-vm/update"
ok "the token goes in a header file" grep -qx 'Authorization: VmToken vm-secret-token' "${stub_dir}/headers"
ok "the token is never an argument" sh -c "! grep -q 'vm-secret-token' '${stub_dir}/argv'"
update status
ok "status prints two plain lines" test "$(wc -l <"${out}")" -eq 2
ok "status names the payload update" grep -q '^This VM: update available (aaaaaaa -> bbbbbbb)$' "${out}"
ok "status names the host release" grep -q '^Host: release 3.2.0+cccccccc available$' "${out}"
answer GET 200 '{"newerPayload":false,"availableCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","appliedCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","hostUpdate":null}'
update status
ok "status says up to date" sh -c "grep -q '^This VM: up to date (bbbbbbb)$' '${out}' && grep -q '^Host: no update for you to run$' '${out}'"

# ── run ──────────────────────────────────────────────────────────────────────
reset
answer POST 202 "${run_json}"
update run --json
ok "run --json exits 0" test "${rc}" = 0
ok "run --json prints the engine's answer unchanged" test "$(cat "${out}")" = "${run_json}"
ok "run --json prints nothing on stderr" test ! -s "${err}"
ok "run posts to this VM's route" test "$(cat "${stub_dir}/requests")" = "POST https://host.example:7462/api/v1/vms/work-vm/update"
update run
ok "run names the host update" grep -qx 'Updating the host (update u-1); this VM follows.' "${out}"
answer POST 202 '{"jobId":"j-1","hostUpdateId":null,"hostJobId":null}'
update run
ok "run names the VM's job" grep -qx 'Updating this VM (job j-1).' "${out}"

# ── failures: one line on stderr, nothing on stdout, non-zero ───────────────
reset
update status --json
ok "an older host without the route exits 4" test "${rc}" = 4
ok "an older host prints nothing on stdout" test ! -s "${out}"
ok "an older host says so in one line" sh -c "grep -q 'does not update its VMs' '${err}' && test \$(wc -l <'${err}') -eq 1"
update run --json
ok "run on an older host exits 4 with nothing on stdout" sh -c "test ${rc} = 4 && test ! -s '${out}'"

reset
answer POST 409 '{"title":"Conflict","detail":"A provision job is already running."}'
update run --json
ok "a refusal exits 6" test "${rc}" = 6
ok "a refusal prints nothing on stdout" test ! -s "${out}"
ok "a refusal names the engine's reason in one line" sh -c "grep -q 'already running' '${err}' && test \$(wc -l <'${err}') -eq 1"

reset
answer GET 404 '{"title":"Not Found","detail":"Unknown VM."}'
update status --json
ok "an unknown VM is a refusal, not an older host" sh -c "test ${rc} = 6 && grep -q 'Unknown VM' '${err}' && test ! -s '${out}'"

reset
answer GET 200 'not json'
update status --json
ok "an unreadable answer fails with nothing on stdout" sh -c "test ${rc} -ne 0 && test ! -s '${out}'"
ok "an unreadable answer is one line on stderr" one_line_err

reset
: >"${stub_dir}/exit"
update status --json
ok "an engine that does not answer exits 8" test "${rc}" = 8
ok "no answer prints nothing on stdout, one line on stderr" sh -c "test ! -s '${out}' && test \$(wc -l <'${err}') -eq 1"

reset
rc=0
env -i PATH="${stubs}:/usr/bin:/bin" HOME="${tmp}" STUB_DIR="${stub_dir}" CONFIG_FILE="${tmp}/none.env" \
  CONSTRUCT_VM_TOKEN_FILE="${token}" bash "${CLI}" update status --json >"${out}" 2>"${err}" || rc=$?
ok "no engine exits 4" test "${rc}" = 4
ok "no engine prints nothing on stdout, one line on stderr" sh -c "test ! -s '${out}' && test \$(wc -l <'${err}') -eq 1"
ok "no engine sends nothing" test ! -f "${stub_dir}/requests"
rc=0
env -i PATH="${stubs}:/usr/bin:/bin" HOME="${tmp}" STUB_DIR="${stub_dir}" CONFIG_FILE="${cfg}" \
  CONSTRUCT_VM_TOKEN_FILE="${tmp}/no-token" bash "${CLI}" update run --json >"${out}" 2>"${err}" || rc=$?
ok "no VM token exits 4 and sends nothing" sh -c "test ${rc} = 4 && test ! -s '${out}' && test ! -f '${stub_dir}/requests'"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
