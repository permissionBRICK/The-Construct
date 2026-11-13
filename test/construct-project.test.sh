#!/usr/bin/env bash
# Tests for project profiles through the host service (docs/config-sync.md, "The new stack"):
#   bin/construct project set|delete|conflicts|resolve  with and without a host service
#   bin/construct-project.sh                            the engine calls and what they write
#   bin/construct-idle-report.sh                        the projects digest and the conflicts reply
# Run: bash test/construct-project.test.sh
#
# curl, t3 and the agents are stubs; every run gets a clean environment, so the
# machine's own service settings never reach a real host.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
REPORTER="${ROOT}/bin/construct-idle-report.sh"
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
# curl: records method, URL and body; answers with ${STUB_DIR}/code and ${STUB_DIR}/response.
cat >"${stubs}/curl" <<'STUB'
#!/usr/bin/env bash
d="${STUB_DIR}"
out=""; method="GET"; url=""; bodyfile=""; hdrfile=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -o) out="${args[i+1]}" ;;
    -X) method="${args[i+1]}" ;;
    -H) case "${args[i+1]}" in @*) hdrfile="${args[i+1]#@}" ;; esac ;;
    --data-binary) bodyfile="${args[i+1]#@}" ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
printf '%s %s\n' "${method}" "${url}" >>"${d}/requests"
[[ -n "${hdrfile}" ]] && cat "${hdrfile}" >>"${d}/headers"
if [[ -n "${bodyfile}" ]]; then cat "${bodyfile}" >>"${d}/bodies"; printf '\n' >>"${d}/bodies"; fi
[[ -n "${out}" && -f "${d}/response" ]] && cp "${d}/response" "${out}"
printf '%s' "$(cat "${d}/code" 2>/dev/null || echo 200)"
exit "$(cat "${d}/exit" 2>/dev/null || echo 0)"
STUB
cat >"${stubs}/t3" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_DIR}/t3.argv"
cat >>"${STUB_DIR}/t3.stdin"
exit "$(cat "${STUB_DIR}/t3.exit" 2>/dev/null || echo 0)"
STUB
cat >"${stubs}/claude" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_DIR}/claude.argv"
STUB
cat >"${stubs}/construct-conflicts" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_DIR}/conflicts.argv"
STUB
chmod +x "${stubs}"/*

store="${tmp}/projects"
stub_dir="${tmp}/curl"
token="${tmp}/vm-token"
printf 'secret-token\n' >"${token}"
cfg="${tmp}/config.env"
printf 'CONSTRUCT_SERVICE_URL=https://host.example:7462\nCONSTRUCT_INSTANCE_NAME=work-vm\n' >"${cfg}"
local_cfg="${tmp}/local.env"
: >"${local_cfg}"

reset() {
  rm -rf "${store}" "${stub_dir}"
  mkdir -p "${store}" "${stub_dir}"
}

answer() { # <code> <json>
  printf '%s' "$1" >"${stub_dir}/code"
  printf '%s' "$2" >"${stub_dir}/response"
}

# The CLI as an agent runs it, in a clean environment.
run() {
  env -i PATH="${stubs}:/usr/local/bin:/usr/bin:/bin" HOME="${tmp}" \
    PROJECTS_STORE="${store}" CONFIG_FILE="${CONFIG:-${cfg}}" CONSTRUCT_VM_TOKEN_FILE="${token}" \
    CONSTRUCT_REPO_DIR="${ROOT}" STUB_DIR="${stub_dir}" WORKSPACE_ROOT="${tmp}" \
    bash "${CLI}" project "$@"
}

profile='{"name":"web","sdks":{"node":"20"}}'
file_from_host=$'{\n  "name": "web",\n  "sdks": {\n    "node": "22"\n  }\n}\n'
written="$(jq -cn --arg f "${file_from_host}" '{status:"written",revision:7,file:$f}')"

# ── set ──────────────────────────────────────────────────────────────────────

reset
mkdir -p "${store}/.state" "${store}/.deleted"
printf '5\n' >"${store}/.state/web.rev"
: >"${store}/.deleted/web"
answer 200 "${written}"
printf '%s' "${profile}" | run set web >"${tmp}/out" 2>&1
rc=$?
ok "set: written exits 0" test "${rc}" = 0
ok "set: PUTs to this VM's profile route" grep -qx 'PUT https://host.example:7462/api/v1/vms/work-vm/profiles/web' "${stub_dir}/requests"
ok "set: sends the profile and the revision this VM last saw" \
  test "$(jq -c . "${stub_dir}/bodies")" = '{"profile":{"name":"web","sdks":{"node":"20"}},"baseRevision":5}'
ok "set: the VM token goes in a header file" grep -qx 'Authorization: VmToken secret-token' "${stub_dir}/headers"
ok "set: writes the host's file byte for byte" test "$(cat "${store}/web.json"; printf x)" = "${file_from_host}x"
ok "set: keeps the new revision" test "$(cat "${store}/.state/web.rev")" = 7
ok "set: clears a deletion marker" test ! -e "${store}/.deleted/web"

reset
answer 200 "${written}"
printf '%s' "${profile}" | run set web >/dev/null 2>&1
ok "set: without a .rev the base is null" test "$(jq -c .baseRevision "${stub_dir}/bodies")" = null

reset
mkdir -p "${store}/.state"
printf '5\n' >"${store}/.state/web.rev"
answer 200 "$(jq -cn --arg f "${file_from_host}" '{status:"queued",revision:5,file:$f}')"
printf '%s' "${profile}" | run set web >"${tmp}/out" 2>&1
ok "set: queued exits 0" test $? = 0
ok "set: queued writes the edit and keeps the base" sh -c "test -f '${store}/web.json' && test \"\$(cat '${store}/.state/web.rev')\" = 5"

reset
printf '%s' "${profile}" >"${store}/web.json"
answer 200 '{"status":"conflict","conflictId":"conflict:00ff","fields":["sdks.node"]}'
printf '%s' '{"name":"web","sdks":{"node":"18"}}' | run set web >"${tmp}/out" 2>&1
rc=$?
ok "set: a conflict exits 6" test "${rc}" = 6
ok "set: a conflict says which fields and how to go on" grep -q 'sdks.node.*' "${tmp}/out"
ok "set: a conflict leaves the file alone" test "$(cat "${store}/web.json")" = "${profile}"

reset
answer 400 '{"title":"invalid-profile","detail":"bad sdks"}'
printf '%s' "${profile}" | run set web >"${tmp}/out" 2>&1
ok "set: refused as invalid exits 2" test $? = 2

reset
printf '%s' '{"name":"web","bogus":1}' | run set web >"${tmp}/out" 2>&1
rc=$?
ok "set: an invalid profile is refused before anything is sent" sh -c "test ${rc} -ne 0 && test ! -f '${stub_dir}/requests'"

reset
printf '000' >"${stub_dir}/code"
printf '7' >"${stub_dir}/exit"
printf '%s' "${profile}" | run set web >"${tmp}/out" 2>&1
rc=$?
ok "set: unreachable host falls back to the file" sh -c "test ${rc} = 0 && jq -e '.sdks.node == \"20\"' '${store}/web.json' >/dev/null"
ok "set: the fallback writes no revision" test ! -e "${store}/.state/web.rev"

reset
CONFIG="${local_cfg}" run set web <<<"${profile}" >/dev/null 2>&1
ok "set: without a host service only the file is written" sh -c "test -f '${store}/web.json' && test ! -f '${stub_dir}/requests'"

# ── delete ───────────────────────────────────────────────────────────────────

reset
mkdir -p "${store}/.state"
printf '%s' "${profile}" >"${store}/web.json"
printf '5\n' >"${store}/.state/web.rev"
answer 200 '{"status":"written","revision":9,"file":null}'
run delete web >/dev/null 2>&1
ok "delete: written exits 0" test $? = 0
ok "delete: DELETEs with the base revision" grep -qx 'DELETE https://host.example:7462/api/v1/vms/work-vm/profiles/web?baseRevision=5' "${stub_dir}/requests"
ok "delete: removes the file, keeps the deletion's revision, leaves no marker" \
  sh -c "test ! -f '${store}/web.json' && test \"\$(cat '${store}/.state/web.rev')\" = 9 && test ! -e '${store}/.deleted/web'"

reset
printf '%s' "${profile}" >"${store}/web.json"
answer 200 '{"status":"conflict","conflictId":"conflict:00ff","fields":["deleted"]}'
run delete web >/dev/null 2>&1
ok "delete: a conflict exits 6 and keeps the file" sh -c "test $? = 6 && test -f '${store}/web.json'"

reset
printf '%s' "${profile}" >"${store}/web.json"
printf '000' >"${stub_dir}/code"
run delete web >/dev/null 2>&1
ok "delete: unreachable host falls back to the marker" sh -c "test ! -f '${store}/web.json' && test -f '${store}/.deleted/web'"

# ── conflicts and resolve ────────────────────────────────────────────────────

conflict_id="conflict:0123456789abcdef0123456789abcdef"
conflicts="$(jq -cn --arg id "${conflict_id}" '[{id:$id,profile:"web",profileRevision:7,fields:["sdks.node"],base:{name:"web"},ours:{name:"web"},theirs:{name:"web"},merged:{name:"web"}}]')"

reset
answer 200 "${conflicts}"
run conflicts >"${tmp}/out" 2>&1
ok "conflicts: lists id, profile and fields" grep -q "${conflict_id}  web  fields: sdks.node" "${tmp}/out"
ok "conflicts: without --fetch writes nothing" test ! -d "${store}/.state/conflicts"

reset
answer 200 "${conflicts}"
run conflicts --fetch >/dev/null 2>&1
ok "conflicts --fetch: writes the conflict for the agent" \
  sh -c "jq -e '.fields == [\"sdks.node\"] and .ours.name == \"web\"' '${store}/.state/conflicts/0123456789abcdef0123456789abcdef.json' >/dev/null"

reset
mkdir -p "${store}/.state/conflicts"
printf '{}' >"${store}/.state/conflicts/aaaa.json"
answer 200 '[]'
run conflicts --fetch >/dev/null 2>&1
ok "conflicts --fetch: a closed conflict's file goes" test ! -e "${store}/.state/conflicts/aaaa.json"

reset
answer 200 "${conflicts}"
run conflicts --start >/dev/null 2>&1
ok "conflicts --start: starts a T3 Code thread in the profile's project" grep -qx 'thread start web -' "${stub_dir}/t3.argv"
ok "conflicts --start: the prompt names the conflict and its file" \
  sh -c "grep -q '${conflict_id}' '${stub_dir}/t3.stdin' && grep -q '.state/conflicts/0123456789abcdef0123456789abcdef.json' '${stub_dir}/t3.stdin'"
run conflicts --start >/dev/null 2>&1
ok "conflicts --start: once per conflict" test "$(wc -l <"${stub_dir}/t3.argv")" = 1

reset
answer 200 "${conflicts}"
printf '1' >"${stub_dir}/t3.exit"
run conflicts --start >/dev/null 2>&1
sleep 0.5
ok "conflicts --start: without a T3 thread the agent runs on its own" grep -q -- "-p --permission-mode bypassPermissions" "${stub_dir}/claude.argv"

reset
mkdir -p "${store}/.state/conflicts"
printf '{}' >"${store}/.state/conflicts/0123456789abcdef0123456789abcdef.json"
answer 200 "$(jq -cn --arg f "${file_from_host}" '{status:"written",revision:11,file:$f}')"
printf '%s' '{"name":"web","sdks":{"node":"22"}}' | run resolve "${conflict_id}" >"${tmp}/out" 2>&1
ok "resolve: written exits 0" test $? = 0
ok "resolve: POSTs the resolution" grep -qx "POST https://host.example:7462/api/v1/vms/work-vm/profile-conflicts/conflict%3A0123456789abcdef0123456789abcdef/resolve" "${stub_dir}/requests"
ok "resolve: writes the profile and its revision" sh -c "test \"\$(cat '${store}/.state/web.rev')\" = 11 && jq -e '.sdks.node == \"22\"' '${store}/web.json' >/dev/null"
ok "resolve: removes the conflict file" test ! -e "${store}/.state/conflicts/0123456789abcdef0123456789abcdef.json"

reset
answer 200 '{"status":"closed"}'
printf '%s' "${profile}" | run resolve "${conflict_id}" >"${tmp}/out" 2>&1
ok "resolve: already closed writes nothing" sh -c "test $? = 0 && test ! -f '${store}/web.json'"

reset
answer 200 '{"status":"recomputed","conflictId":"conflict:ffff"}'
printf '%s' "${profile}" | run resolve "${conflict_id}" >"${tmp}/out" 2>&1
ok "resolve: recomputed exits 6 and names the new conflict" sh -c "test $? = 6 && grep -q 'conflict:ffff' '${tmp}/out'"

CONFIG="${local_cfg}" run conflicts >/dev/null 2>&1
ok "conflicts without a host service exits non-zero" test $? -ne 0

# ── heartbeat ────────────────────────────────────────────────────────────────

beat() {
  env -i PATH="${stubs}:/usr/local/bin:/usr/bin:/bin" HOME="${tmp}" \
    PROJECTS_STORE="${store}" CONFIG_FILE="${cfg}" CONSTRUCT_VM_TOKEN_FILE="${token}" \
    STUB_DIR="${stub_dir}" CONSTRUCT_IDLE_AGENT_HOMES="${tmp}/nohome" \
    CONSTRUCT_IDLE_SS=no-such-ss CONSTRUCT_IDLE_WHO=no-such-who \
    CONSTRUCT_PROVISION_MARKER="${tmp}/no-marker" \
    CONSTRUCT_IDLE_CONFLICTS_CMD="${stubs}/construct-conflicts" "$@" \
    bash "${REPORTER}"
}

reset
printf '%s' "${profile}" >"${store}/web.json"
d1="$(beat env CONSTRUCT_IDLE_DRY_RUN=true | jq -r .projectsDigest)"
printf '%s' '{"name":"web"}' >"${store}/web.json"
d2="$(beat env CONSTRUCT_IDLE_DRY_RUN=true | jq -r .projectsDigest)"
mkdir -p "${store}/.state"; printf '3\n' >"${store}/.state/web.rev"
d3="$(beat env CONSTRUCT_IDLE_DRY_RUN=true | jq -r .projectsDigest)"
mkdir -p "${store}/.state/conflicts"; printf '{}' >"${store}/.state/conflicts/x.json"
d4="$(beat env CONSTRUCT_IDLE_DRY_RUN=true | jq -r .projectsDigest)"
ok "heartbeat: carries a digest of the projects folder" test "${#d1}" = 64
ok "heartbeat: the digest changes with a profile and with a revision" sh -c "test '${d1}' != '${d2}' && test '${d2}' != '${d3}'"
ok "heartbeat: conflict files do not change it" test "${d3}" = "${d4}"

reset
answer 200 "$(jq -cn --arg id "${conflict_id}" '{conflicts:[{id:$id,profile:"web",fields:["sdks.node"]}]}')"
beat env >/dev/null 2>&1
sleep 0.3
ok "heartbeat: a new conflict in the reply starts the agent" grep -qx 'project conflicts --start' "${stub_dir}/conflicts.argv"
ok "heartbeat: the report carries the digest" sh -c "jq -e '.projectsDigest | length == 64' '${stub_dir}/bodies' >/dev/null"

reset
mkdir -p "${store}/.state/conflicts"
: >"${store}/.state/conflicts/0123456789abcdef0123456789abcdef.started"
answer 200 "$(jq -cn --arg id "${conflict_id}" '{conflicts:[{id:$id,profile:"web",fields:["sdks.node"]}]}')"
beat env >/dev/null 2>&1
sleep 0.3
ok "heartbeat: a conflict already started is not started again" test ! -e "${stub_dir}/conflicts.argv"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
