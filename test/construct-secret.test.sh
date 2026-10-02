#!/usr/bin/env bash
# Regression tests for `construct secret` — the guest side of the key vault.
# Run: bash test/construct-secret.test.sh
#
# A background fake Companion drains the sandboxed spool the way the real
# vault-watch.sh does (claim = rename away), records every request, the file
# mode it arrived with and a `ps` snapshot, and answers from a per-test jq
# program. The promises held here:
#   * the request document has the shape the Companion parses, one line, 0600,
#     in 0700 spool directories;
#   * a secret value never shows up in any process's argv, only base64 in the
#     request; stdin loses ONE trailing newline, --file is byte-exact;
#   * `get` prints the value byte-exact, nothing else on stdout;
#   * every answer status maps to its exit code, a missing Companion is exit 6
#     and an unanswered request exit 7 -- and neither leaves files behind.

set -u
# `printf … | run add …` must set rc in THIS shell, not in a pipeline subshell.
shopt -s lastpipe

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
IMPL="${ROOT}/bin/construct-secret.sh"
tmp="$(mktemp -d)"
responder_pid=""
cleanup() {
  if [[ -n "${responder_pid}" ]]; then
    kill "${responder_pid}" 2>/dev/null
    wait "${responder_pid}" 2>/dev/null
  fi
  rm -rf "${tmp}"
}
trap cleanup EXIT

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

if ! command -v jq >/dev/null 2>&1; then
  printf 'construct-secret.test.sh: jq is required\n' >&2
  exit 1
fi

# ── sandbox + fake Companion ─────────────────────────────────────────────────

spool="${tmp}/vault"
ctl="${tmp}/ctl"
log="${tmp}/log"
mkdir -p "${ctl}" "${log}"
export CONSTRUCT_VAULT_SPOOL="${spool}"
export CONSTRUCT_VAULT_PICKUP_SEC=2

# ctl/off      present = nothing claims requests (no Companion connected)
# ctl/answer   a jq program turning the request into the response;
#              "NOANSWER" = claim but never answer, "RAW:<text>" = answer <text>
responder() {
  local f id claimed program
  while :; do
    if [[ ! -e "${ctl}/off" ]]; then
      for f in "${spool}"/requests/*.json; do
        [[ -e "${f}" ]] || continue
        id="$(basename "${f}" .json)"
        claimed="${f}.claimed.${BASHPID}"
        mv "${f}" "${claimed}" 2>/dev/null || continue
        stat -c %a "${claimed}" >"${log}/mode"
        ps -ww -eo args >>"${log}/ps.txt" 2>/dev/null
        cp "${claimed}" "${log}/last.json"
        cat "${claimed}" >>"${log}/all.jsonl"
        rm -f "${claimed}"
        program="$(cat "${ctl}/answer")"
        case "${program}" in
          NOANSWER) continue ;;
          RAW:*) printf '%s\n' "${program#RAW:}" >"${spool}/responses/.tmp.${id}" ;;
          *) jq -c -f "${ctl}/answer" "${log}/last.json" >"${spool}/responses/.tmp.${id}" 2>/dev/null ;;
        esac
        mv -f "${spool}/responses/.tmp.${id}" "${spool}/responses/${id}.json"
      done
    fi
    sleep 0.05
  done
}
responder &
responder_pid=$!

answer() { printf '%s\n' "$1" >"${ctl}/answer"; }
answer_ok() { answer '{v:1, id:.id, status:"ok", names:.names, lease:{usesLeft:null, expiresAt:(.ts + 3600000)}}'; }

out="${tmp}/out"
err="${tmp}/err"
rc=0
# Run the CLI (stdin passes through); stdout/stderr/exit code land in out/err/rc.
run() {
  rm -f "${log}/last.json" "${log}/mode"
  bash "${CLI}" secret "$@" >"${out}" 2>"${err}"
  rc=$?
}
req() { jq -r "$@" "${log}/last.json" 2>/dev/null; }
published() { test -f "${log}/last.json"; }
not_published() { test ! -f "${log}/last.json"; }
leftovers() { find "${spool}" -type f 2>/dev/null | wc -l; }
deadline_after() { test "$(req '.deadline - .ts')" = "$1"; }
stored_secret() { jq -j '.secret' "${log}/last.json" | base64 -d; }
hm() { date -d "@$(( $1 / 1000 ))" +%H:%M; }
now_ms() { date +%s%3N; }

# ── list: the spool, the request document ────────────────────────────────────

exp_ms=$(( ($(date +%s) + 3600) * 1000 ))
exp_hm="$(hm "${exp_ms}")"
items='[{"name":"alpha","description":"first","hasUsername":false,"lease":null},
 {"name":"beta","description":"second","hasUsername":true,"lease":{"usesLeft":3,"expiresAt":null}},
 {"name":"gamma-long-name","description":"third","hasUsername":false,"lease":{"usesLeft":null,"expiresAt":'"${exp_ms}"'}},
 {"name":"delta","description":"fourth\twith tab","hasUsername":false,"lease":{"usesLeft":2,"expiresAt":'"${exp_ms}"'}},
 {"name":"one","description":"fifth","hasUsername":false,"lease":{"usesLeft":1,"expiresAt":null}}]'
printf '%s' "${items}" >"${tmp}/items.json"
answer "{v:1, id:.id, status:\"ok\", items:${items}}"

rm -rf "${spool}"
run list
ok "list: exits 0" test "${rc}" = 0
ok "list: the spool base dir is created 0700" test "$(stat -c %a "${spool}")" = 700
ok "list: requests/ is 0700" test "$(stat -c %a "${spool}/requests")" = 700
ok "list: responses/ is 0700" test "$(stat -c %a "${spool}/responses")" = 700
ok "list: the request file is 0600" test "$(cat "${log}/mode")" = 600
ok "list: the request is ONE line of JSON" test "$(wc -l <"${log}/last.json")" = 1
ok "list: v is 1" test "$(req .v)" = 1
ok "list: op is list" test "$(req .op)" = list
ok "list: names is an empty array" test "$(req -c .names)" = "[]"
ok "list: the id has the contract shape" \
  sh -c "jq -r .id '${log}/last.json' | grep -Eqx '[0-9]{13}-[0-9]+-[0-9a-f]{8}'"
ok "list: the id passes the Companion's id rule" \
  sh -c "jq -r .id '${log}/last.json' | grep -Eqx '[A-Za-z0-9-]{8,64}'"
ok "list: the id starts with ts" test "$(req '.id | split("-")[0]')" = "$(req .ts)"
ok "list: ts is epoch milliseconds" sh -c "jq -r .ts '${log}/last.json' | grep -Eqx '[0-9]{13}'"
ok "list: the deadline is 60 s out by default" deadline_after 60000
ok "list: source is user@host" sh -c "jq -r .source '${log}/last.json' | grep -Eqx '[^@ ]+@[^@ ]+'"
ok "list: all/replace are false" test "$(req '[.all, .replace] | tostring')" = "[false,false]"
ok "list: no uses/ttl/secret/reason fields" test "$(req '[.uses, .ttl, .secret, .reason] | map(select(. != null)) | length')" = 0
ok "list: header row" sh -c "head -1 '${out}' | grep -Eqx 'NAME +ACCESS +DESCRIPTION'"
ok "list: no lease reads 'none'" grep -Eqx 'alpha +none +first' "${out}"
ok "list: uses only" grep -Eqx 'beta +3 uses left +second' "${out}"
ok "list: time only" grep -Eqx "gamma-long-name +until ${exp_hm} +third" "${out}"
ok "list: uses and time; a tab in a description is flattened" \
  grep -Eqx "delta +2 uses left, until ${exp_hm} +fourth with tab" "${out}"
ok "list: one use is singular" grep -Eqx 'one +1 use left +fifth' "${out}"
ok "list: columns line up" \
  test "$(awk 'NR==1 {print index($0, "ACCESS")}' "${out}")" = "$(awk '/^alpha/ {print index($0, "none")}' "${out}")"
ok "list: nothing on stderr" test ! -s "${err}"
ok "list: no files left in the spool" test "$(leftovers)" = 0

run ls --json
ok "ls is an alias of list" test "$(req .op)" = list
ok "list --json prints the raw items array" \
  test "$(jq -cS . "${out}")" = "$(jq -cS . "${tmp}/items.json")"

answer '{v:1, id:.id, status:"ok", items:[]}'
run list
ok "list: an empty vault says so" grep -qx 'The key vault is empty.' "${out}"
run status
ok "status: op is status" test "$(req .op)" = status
ok "status: the deadline is 60 s out by default" deadline_after 60000
ok "status: no leases says so" grep -qx 'This VM holds no secret leases.' "${out}"
run status --json
ok "status --json: an empty array" test "$(cat "${out}")" = "[]"
answer "{v:1, id:.id, status:\"ok\", items:${items}}"
run status
ok "status: prints the table" grep -Eqx 'beta +3 uses left +second' "${out}"
run list alpha
ok "list takes no names" test "${rc}" = 1
run list --uses 3
ok "an option of another command is refused" test "${rc}" = 1
ok "a refused option publishes nothing" not_published

# ── request ──────────────────────────────────────────────────────────────────

answer_ok
run request a b a --uses 3 --for 2h --reason "$(printf 'ship\tthe\nrelease')"
ok "request: exits 0" test "${rc}" = 0
ok "request: op is request" test "$(req .op)" = request
ok "request: names, de-duplicated in order" test "$(req -c .names)" = '["a","b"]'
ok "request: uses" test "$(req .uses)" = 3
ok "request: --for 2h is ttl 7200" test "$(req .ttl)" = 7200
ok "request: the reason is flattened to one line" test "$(req .reason)" = "ship the release"
ok "request: the deadline is 600 s out by default" deadline_after 600000
ok "request: confirmation line" \
  grep -qx "Approved: a, b — until $(hm "$(( $(req .ts) + 3600000 ))")" "${out}"

run request a
ok "request: neither --uses nor --for sends ttl 3600" test "$(req .ttl)" = 3600
ok "request: ... and no uses" test "$(req .uses)" = null
run request a --uses 5
ok "request: --uses alone sends no ttl" test "$(req '.ttl')" = null
ok "request: --uses alone" test "$(req .uses)" = 5
run request a --wait 30
ok "request: --wait sets the deadline" deadline_after 30000
run request a --uses=7 --reason=why
ok "request: --opt=value spelling" test "$(req '[.uses, .reason] | tostring')" = '[7,"why"]'

answer '{v:1, id:.id, status:"ok", names:["a"], lease:{usesLeft:3, expiresAt:null}}'
run request a b --uses 3
ok "request: partial approval still exits 0" test "${rc}" = 0
ok "request: partial approval prints what was approved" grep -qx 'Approved: a — 3 uses' "${out}"
ok "request: partial approval names the rest on stderr" grep -q 'not approved: b' "${err}"
answer '{v:1, id:.id, status:"ok", names:[]}'
run request a
ok "request: nothing approved exits 7" test "${rc}" = 7

run request
ok "request: needs a name" test "${rc}" = 1
names21=()
for i in $(seq 1 21); do names21+=("n${i}"); done
run request "${names21[@]}"
ok "request: at most 20 names" test "${rc}" = 1
run request a --uses 0
ok "request: --uses 0 is refused" test "${rc}" = 1
run request a --uses 1001
ok "request: --uses 1001 is refused" test "${rc}" = 1
ok "request: refusals publish nothing" not_published

# ── durations ────────────────────────────────────────────────────────────────

answer_ok
for pair in 90s:90 45m:2700 2h:7200 1d:86400 600:600 60:60 1H:3600; do
  run request a --for "${pair%%:*}"
  ok "duration ${pair%%:*} = ${pair##*:} s" test "$(req .ttl)" = "${pair##*:}"
done
for bad in 59 59s 25h 2d 86401 abc 1.5h -5m 5x ""; do
  run request a --for "${bad}"
  ok "duration '${bad}' exits 1 and publishes nothing" sh -c "test ${rc} = 1 && test ! -f '${log}/last.json'"
done

# ── names ────────────────────────────────────────────────────────────────────

long64="$(printf 'a%.0s' $(seq 1 64))"
answer_ok
run request "${long64}" "x.y_z-1"
ok "a 64-character name and . _ - are valid" test "$(req -c .names)" = "[\"${long64}\",\"x.y_z-1\"]"
# shellcheck disable=SC2016 # a literal $(id) is the point
for bad in ".hidden" "_x" "a/b" "a b" "${long64}b" "ä" "a:b" '$(id)'; do
  run get "${bad}"
  ok "invalid name '${bad:0:12}' exits 1" sh -c "test ${rc} = 1 && test ! -f '${log}/last.json'"
done
run get -- -dash
ok "a name starting with '-' is invalid" test "${rc}" = 1

# ── get ──────────────────────────────────────────────────────────────────────

# answer_get <file with the value> [usesLeft]
answer_get() {
  local b64
  b64="$(base64 -w0 <"$1")"
  answer "{v:1, id:.id, status:\"ok\", secret:\"${b64}\", username:\"deploy-bot\", lease:{usesLeft:${2:-2}, expiresAt:(.ts + 3600000)}}"
}

printf 'hunter2\n' >"${tmp}/v1"
answer_get "${tmp}/v1"
run get db-pass --reason "migrate"
ok "get: exits 0" test "${rc}" = 0
ok "get: op is get" test "$(req .op)" = get
ok "get: names is the one name" test "$(req -c .names)" = '["db-pass"]'
ok "get: reason" test "$(req .reason)" = migrate
ok "get: the deadline is 600 s out by default" deadline_after 600000
ok "get: prints the value byte-exact, trailing newline included" cmp -s "${out}" "${tmp}/v1"
ok "get: nothing on stderr" test ! -s "${err}"
ok "get: the answer (holding the value) is deleted" test "$(leftovers)" = 0

printf -- '-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nBKcwggSjAgEAAoIBAQC7\n-----END PRIVATE KEY-----\n' >"${tmp}/pem"
answer_get "${tmp}/pem"
run get tls-key
ok "get: a multi-line PEM value is byte-exact" cmp -s "${out}" "${tmp}/pem"

printf 'no-newline' >"${tmp}/v2"
answer_get "${tmp}/v2"
run get x
ok "get: adds no newline of its own" cmp -s "${out}" "${tmp}/v2"

printf 'bin\000ary\r\n\n' >"${tmp}/v3"
answer_get "${tmp}/v3"
run get x
ok "get: NUL bytes and trailing CR/LF survive" cmp -s "${out}" "${tmp}/v3"

answer_get "${tmp}/v1"
run get x --username
ok "get --username: prints only the username, no newline" test "$(od -An -c "${out}" | tr -d ' \n')" = "deploy-bot"
run get x --json
ok "get --json: one line" test "$(wc -l <"${out}")" = 1
ok "get --json: name/username/secret, the secret as a plain string" \
  test "$(jq -cS . "${out}")" = '{"name":"x","secret":"hunter2\n","username":"deploy-bot"}'
run get x --json --username
ok "get: --json with --username exits 1" test "${rc}" = 1
run get x y
ok "get: exactly one name" test "${rc}" = 1

answer_get "${tmp}/v1" 0
run get x
ok "get: the last use still prints the value" cmp -s "${out}" "${tmp}/v1"
ok "get: the last use is pointed out on stderr" grep -q 'last approved use of x' "${err}"

answer '{v:1, id:.id, status:"ok", secret:"@@not base64@@"}'
run get x
ok "get: a broken value exits 8" test "${rc}" = 8
ok "get: ... and prints nothing on stdout" test ! -s "${out}"
answer '{v:1, id:.id, status:"ok"}'
run get x
ok "get: an answer without a value exits 8" test "${rc}" = 8

# ── answer status → exit code ────────────────────────────────────────────────

for pair in denied:7 notFound:9 error:8 invalid:8 weird:8; do
  answer "{v:1, id:.id, status:\"${pair%%:*}\", message:\"msg for ${pair%%:*}\"}"
  run get x
  ok "status ${pair%%:*} exits ${pair##*:}" test "${rc}" = "${pair##*:}"
  ok "status ${pair%%:*} prints nothing on stdout" test ! -s "${out}"
done
answer '{v:1, id:.id, status:"denied", message:"The user said no.\nTwice."}'
run get x
ok "the Companion's message goes to stderr, on one line" \
  test "$(cat "${err}")" = "construct secret: The user said no. Twice."
answer '{v:1, id:.id, status:"exists", message:"github-token already exists."}'
printf 'v\n' | run add github-token --description d
ok "status exists exits 10" test "${rc}" = 10
answer 'RAW:this is not json'
run get x
ok "an unreadable answer exits 8" test "${rc}" = 8
answer '{v:1, id:"someone-else", status:"ok", secret:"eA=="}'
run get x
ok "an answer for another id exits 8" test "${rc}" = 8
answer_ok
run get x
ok "status ok without a value is not a value (exit 8)" test "${rc}" = 8
ok "no files left after all of that" test "$(leftovers)" = 0

# ── release ──────────────────────────────────────────────────────────────────

answer_ok
run release a b
ok "release: exits 0" test "${rc}" = 0
ok "release: op/names/all" test "$(req -c '[.op, .names, .all]')" = '["release",["a","b"],false]'
ok "release: the deadline is 60 s out by default" deadline_after 60000
ok "release: prints what ended" grep -qx 'Released: a, b' "${out}"
ok "release: says the Companion scrubs the VM" grep -q 'scrubs a, b from this VM' "${err}"
answer '{v:1, id:.id, status:"ok", names:[]}'
run relinquish --all
ok "relinquish is an alias; --all sends all:true and no names" \
  test "$(req -c '[.op, .names, .all]')" = '["release",[],true]'
ok "release: nothing to release" grep -qx 'Nothing to release.' "${out}"
ok "release: no scrub notice when nothing ended" test ! -s "${err}"
run release
ok "release: needs names or --all" test "${rc}" = 1
run release a --all
ok "release: names and --all together are refused" test "${rc}" = 1

# ── add ──────────────────────────────────────────────────────────────────────

answer_ok
printf 'value\n' | run add api-key --description "$(printf 'The\tAPI\nkey')" --username "ci bot" --for 1h
ok "add: exits 0" test "${rc}" = 0
ok "add: op/names" test "$(req -c '[.op, .names]')" = '["add",["api-key"]]'
ok "add: description, flattened" test "$(req .description)" = "The API key"
ok "add: username" test "$(req .username)" = "ci bot"
ok "add: --for is ttl" test "$(req .ttl)" = 3600
ok "add: replace false" test "$(req .replace)" = false
ok "add: the deadline is 60 s out by default" deadline_after 60000
ok "add: the secret travels as base64" test "$(req .secret)" = "$(printf 'value' | base64 -w0)"
ok "add: stdin loses its one trailing newline" test "$(stored_secret | od -An -c | tr -d ' \n')" = "value"
ok "add: the confirmation" \
  grep -qx "Stored api-key in the key vault; this VM holds it until $(hm "$(( $(req .ts) + 3600000 ))"). Run 'construct secret release api-key' when done." "${out}"

check_stdin() { # <printf format of the input> <printf format of what must be stored> <label>
  # shellcheck disable=SC2059
  printf "$1" | run add k --description d
  # shellcheck disable=SC2059
  printf "$2" >"${tmp}/expect"
  ok "add via stdin: $3" sh -c "test ${rc} = 0 && jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/expect'"
}
check_stdin 'abc\r\n' 'abc' 'a CRLF ending is dropped'
check_stdin 'abc\n\n' 'abc\n' 'only ONE newline is dropped'
check_stdin 'abc' 'abc' 'no newline, nothing dropped'
check_stdin 'a\000b\n' 'a\000b' 'NUL bytes survive'
check_stdin 'line1\nline2\n' 'line1\nline2' 'multi-line keeps inner newlines'
check_stdin 'abc\r' 'abc\r' 'a lone CR is kept'

printf 'from-file\n' >"${tmp}/f1"
run add k --description d --file "${tmp}/f1"
ok "add --file: stored byte-exact, trailing newline included" \
  sh -c "jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/f1'"
printf 'stdin-exact\n' >"${tmp}/f2"
run add k --description d --file - <"${tmp}/f2"
ok "add --file -: stdin byte-exact" \
  sh -c "jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/f2'"

printf 'v' | run add k --description d --replace
ok "add --replace: replace true" test "$(req .replace)" = true
ok "add --replace: the deadline is 600 s out (needs approval)" deadline_after 600000

: >"${tmp}/empty"
run add k --description d <"${tmp}/empty"
ok "add: empty stdin exits 1" test "${rc}" = 1
ok "add: ... and publishes nothing" not_published
printf '\n' | run add k --description d
ok "add: a lone newline is empty, exits 1" test "${rc}" = 1
run add k --description d --file "${tmp}/empty"
ok "add: an empty file exits 1" test "${rc}" = 1
run add k --description d --file "${tmp}/does-not-exist"
ok "add: a missing file exits 1" test "${rc}" = 1
printf 'v' | run add k
ok "add: --description is required" test "${rc}" = 1
printf 'v' | run add k --description "   "
ok "add: a blank description is refused" test "${rc}" = 1
printf 'v' | run add k --description "$(head -c 400 /dev/zero | tr '\0' 'd')"
ok "add: the description is capped at 300" test "$(req '.description | length')" = 300

head -c 32768 /dev/urandom >"${tmp}/max"
run add k --description d --file "${tmp}/max"
ok "add: 32 KiB exactly is accepted" sh -c "test ${rc} = 0 && jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/max'"
ok "add: the largest request stays under 64 KiB" test "$(stat -c %s "${log}/last.json")" -lt 65536
{ cat "${tmp}/max"; printf '\n'; } | run add k --description d
ok "add: 32 KiB plus the dropped newline is accepted" sh -c "test ${rc} = 0 && jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/max'"
{ cat "${tmp}/max"; printf 'x'; } >"${tmp}/max1"
run add k --description d --file "${tmp}/max1"
ok "add: one byte over 32 KiB exits 1" test "${rc}" = 1
ok "add: ... and publishes nothing" not_published
head -c 1048576 /dev/zero | tr '\0' 'x' | run add k --description d
ok "add: 1 MiB on stdin exits 1" test "${rc}" = 1

# The secret never appears in argv. Deterministic half: shims log the argv of
# every external command the CLI runs. Live half: `ps` snapshots while the
# add pipeline is alive (stdin held open) and when the Companion claims it.
sentinel="kv-sentinel-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"
printf '%s' "${sentinel}" >"${tmp}/sentinel"
base64 -w0 <"${tmp}/sentinel" >"${tmp}/sentinel.b64"
shims="${tmp}/shims"
mkdir -p "${shims}"
for cmd in bash jq base64 head tr mv rm install id hostname sleep date cat stat dirname; do
  real="$(command -v "${cmd}")" || continue
  printf '#!%s\nprintf "%%s\\n" "%s $*" >>"%s"\nexec "%s" "$@"\n' \
    "$(command -v bash)" "${cmd}" "${tmp}/argv.log" "${real}" >"${shims}/${cmd}"
  chmod +x "${shims}/${cmd}"
done
: >"${tmp}/argv.log"
: >"${log}/ps.txt"
rm -f "${log}/last.json"
PATH="${shims}:${PATH}" bash "${CLI}" secret add sentinel-test --description d <"${tmp}/sentinel" >"${out}" 2>"${err}"
rc=$?
ok "argv: the shimmed add exits 0" test "${rc}" = 0
ok "argv: the shims saw the jq --rawfile call (the check is live)" grep -q -- 'jq -cn --rawfile s /dev/stdin' "${tmp}/argv.log"
ok "argv: the secret is in no command's argv" sh -c "! grep -qF -f '${tmp}/sentinel' '${tmp}/argv.log'"
ok "argv: nor its base64" sh -c "! grep -qF -f '${tmp}/sentinel.b64' '${tmp}/argv.log'"
ok "argv: the request carries it as base64" sh -c "jq -j .secret '${log}/last.json' | base64 -d | cmp -s - '${tmp}/sentinel'"
ok "argv: the plaintext is not in the request" sh -c "! grep -qF -f '${tmp}/sentinel' '${log}/last.json'"

: >"${tmp}/ps-live.txt"
( cat "${tmp}/sentinel"; sleep 1.5 ) | bash "${CLI}" secret add slow-test --description d >"${out}" 2>"${err}" &
slow=$!
for _ in $(seq 1 20); do ps -ww -eo args >>"${tmp}/ps-live.txt" 2>/dev/null; sleep 0.05; done
wait "${slow}"
rc=$?
ok "ps: the slow add exits 0" test "${rc}" = 0
ok "ps: the snapshots caught the add pipeline (the check is live)" grep -q -- '--rawfile s /dev/stdin' "${tmp}/ps-live.txt"
ok "ps: the secret is in no snapshot" sh -c "! grep -qF -f '${tmp}/sentinel' '${tmp}/ps-live.txt' '${log}/ps.txt'"

# ── delete ───────────────────────────────────────────────────────────────────

answer_ok
run rm old-token --reason "rotated"
ok "rm is an alias of delete" test "$(req .op)" = delete
ok "delete: names/reason" test "$(req -c '[.names, .reason]')" = '[["old-token"],"rotated"]'
ok "delete: the deadline is 600 s out by default" deadline_after 600000
ok "delete: confirmation" grep -qx 'Deleted old-token.' "${out}"

# ── no Companion / no answer ─────────────────────────────────────────────────

touch "${ctl}/off"
start="$(now_ms)"
CONSTRUCT_VAULT_PICKUP_SEC=1 run get x
elapsed=$(( $(now_ms) - start ))
ok "no Companion: exits 6" test "${rc}" = 6
ok "no Companion: within the pickup window (${elapsed} ms)" test "${elapsed}" -lt 3000
ok "no Companion: says so" grep -q 'no Construct Companion is connected' "${err}"
ok "no Companion: the request is taken back" test "$(leftovers)" = 0

bash "${CLI}" secret get x >/dev/null 2>&1 &
waiter=$!
for _ in $(seq 1 50); do
  [[ -n "$(find "${spool}/requests" -name '*.json' 2>/dev/null)" ]] && break
  sleep 0.05
done
ok "TERM: the request was published" test -n "$(find "${spool}/requests" -name '*.json')"
kill -TERM "${waiter}"
wait "${waiter}"
rc=$?
ok "TERM: exits 143" test "${rc}" = 143
ok "TERM: the request is cleaned up" test "$(leftovers)" = 0
rm -f "${ctl}/off"

answer NOANSWER
start="$(now_ms)"
run get x --wait 1
elapsed=$(( $(now_ms) - start ))
ok "no answer: exits 7" test "${rc}" = 7
ok "no answer: after --wait (${elapsed} ms)" test "${elapsed}" -ge 900 -a "${elapsed}" -lt 3000
ok "no answer: says so" grep -q 'no answer from the user within 1 s' "${err}"
ok "no answer: no files left behind" test "$(leftovers)" = 0

# ── dispatch, help, environment ──────────────────────────────────────────────

answer_ok
rm -f "${log}/last.json"
bash "${CLI}" secrets release a >/dev/null 2>&1
ok "'construct secrets' is an alias" test "$(req .op)" = release
ok "--help exits 0" sh -c "bash '${CLI}' secret --help >/dev/null"
ok "--help covers every command" \
  sh -c "bash '${CLI}' secret --help | grep -q 'relinquish' && bash '${CLI}' secret --help | grep -q 'password-stdin'"
ok "-h after a command shows help too" sh -c "bash '${CLI}' secret get -h | grep -q '^Usage: construct secret'"
ok "no command exits 1" sh -c "! bash '${CLI}' secret >/dev/null 2>&1"
ok "an unknown command exits 1" sh -c "! bash '${CLI}' secret frobnicate >/dev/null 2>&1"
ok "construct's own help output is untouched (the verb documents itself)" \
  sh -c "! bash '${CLI}' help | grep -q 'construct secret'"

nojq="${tmp}/nojq"
mkdir -p "${nojq}"
for cmd in tr id hostname; do ln -s "$(command -v "${cmd}")" "${nojq}/${cmd}"; done
rm -f "${log}/last.json"
PATH="${nojq}" "$(command -v bash)" "${IMPL}" list >"${out}" 2>"${err}"
rc=$?
ok "without jq: exits 1" test "${rc}" = 1
ok "without jq: says why" grep -q 'jq is required' "${err}"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
