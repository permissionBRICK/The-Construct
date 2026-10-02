#!/usr/bin/env bash
# Regression tests for the key vault's guest scripts — the bash the Companion runs inside
# each VM over SSH (companion/src/Construct.Companion.Core/Vault/GuestScripts/*.sh).
# Run: bash test/vault-guest-scripts.test.sh
#
# The scripts are templates: the Companion fills {{name}} placeholders with single-quoted
# values and runs the result as `bash <file>`, with stdin as the data channel. These tests
# render them the same way and run them locally (the SSH transport is the only part not
# exercised). The invariants they guard:
#   * the watcher hands every request over exactly once, as one line, and keeps the spool
#     private and swept;
#   * secrets travel on stdin only: never in argv, where every process on the VM sees them;
#   * scrubbing edits files in place (agents keep appending to them) and leaves every other
#     byte, row and database structure intact.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPTS="${ROOT}/companion/src/Construct.Companion.Core/Vault/GuestScripts"
tmp="$(mktemp -d)"
bg=()
cleanup() {
  local p
  for p in "${bg[@]}"; do kill "${p}" 2>/dev/null || true; done
  wait 2>/dev/null || true
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

# Quote like the Companion's ShellQuote.Single: single quotes, ' as '\''.
q() {
  local sq="'" esc="'\\''"
  printf "'%s'" "${1//${sq}/${esc}}"
}
# render <template> <out> name=value... — the same substitution GuestScripts does,
# including its refusal of a placeholder without a value.
render() {
  python3 - "$@" <<'PY'
import re, sys
src, dst = sys.argv[1], sys.argv[2]
values = dict(arg.split('=', 1) for arg in sys.argv[3:])
def fill(m):
    if m.group(1) not in values:
        sys.exit('no value for placeholder ' + m.group(1))
    return values[m.group(1)]
with open(src) as f:
    text = f.read()
with open(dst, 'w') as f:
    f.write(re.sub(r'\{\{([A-Za-z][A-Za-z0-9]*)\}\}', fill, text))
PY
}
b64() { printf '%s' "$1" | base64 -w0; }
wait_for() {
  local i
  for ((i = 0; i < 50; i++)); do
    "$@" && return 0
    sleep 0.1
  done
  "$@"
}
# A PATH that has everything in the usual bin dirs except the named commands (globs).
restricted_path() {
  local dir="$1" f n x skip
  shift
  local -A seen=()
  local links=()
  mkdir -p "${dir}"
  for f in /usr/local/bin/* /usr/local/sbin/* /usr/bin/* /usr/sbin/* /bin/* /sbin/*; do
    n="${f##*/}"
    [[ -n "${seen[${n}]:-}" ]] && continue
    seen[${n}]=1
    skip=0
    for x in "$@"; do
      # shellcheck disable=SC2254 # the arguments are globs on purpose
      case "${n}" in ${x}) skip=1 ;; esac
    done
    [[ "${skip}" -eq 1 ]] || links+=("${f}")
  done
  ln -s -t "${dir}" -- "${links[@]}"
}
# inotifywait processes watching <dir> (by pid, never by pattern matching our own shell).
watchers_of() {
  local p n=0
  for p in $(pgrep -x inotifywait); do
    tr '\0' '\n' <"/proc/${p}/cmdline" 2>/dev/null | grep -qxF -- "$1" && n=$((n + 1))
  done
  printf '%s' "${n}"
}
mode_of() { stat -c %a -- "$1"; }
inode_of() { stat -c %i -- "$1"; }
size_of() { stat -c %s -- "$1"; }

# Dummy secrets, random per run so that no other process on the machine can carry them in
# its command line by accident (the ps checks below would blame the scripts for it). They
# reach the scripts on stdin only; helpers that need them read the environment, which
# ps -o args does not show.
rnd() { head -c 48 /dev/urandom | base64 -w0 | tr -dc 'A-Za-z0-9' | head -c 20; }
r1="$(rnd)" r2="$(rnd)" r3="$(rnd)"
S1="ghp_TestSecretOne${r1}"
# shellcheck disable=SC2089  # the literal quote and backslash are the point: JSON escapes them.
S2='sk-two"quote\back-'"${r2}"
# shellcheck disable=SC2089  # ...and this is S2 the way it looks inside a JSON string.
S2J='sk-two\"quote\\back-'"${r2}"
S3="third-secret-${r3}"
S4="9$(head -c 32 /dev/urandom | od -An -tu1 | tr -dc '0-9' | head -c 8)"
# shellcheck disable=SC2090  # see above: exported as plain strings for the python helpers.
export S1 S2 S2J S3 S4

# ── templates ────────────────────────────────────────────────────────────────
placeholders() { grep -o '{{[A-Za-z][A-Za-z0-9]*}}' "$1" | sort -u | tr '\n' ' '; }
ok "vault-watch.sh uses only {{dir}} {{heartbeat}} {{fallback}}" \
  test "$(placeholders "${SCRIPTS}/vault-watch.sh")" = '{{dir}} {{fallback}} {{heartbeat}} '
ok "vault-respond.sh uses only {{dir}} {{id}}" \
  test "$(placeholders "${SCRIPTS}/vault-respond.sh")" = '{{dir}} {{id}} '
ok "vault-scan.sh uses only {{dir}} {{roots}} {{maxSize}}" \
  test "$(placeholders "${SCRIPTS}/vault-scan.sh")" = '{{dir}} {{maxSize}} {{roots}} '
ok "vault-clean.sh uses only {{dir}}" \
  test "$(placeholders "${SCRIPTS}/vault-clean.sh")" = '{{dir}} '
for s in watch respond scan clean; do
  ok "vault-${s}.sh has no stray {{ (the renderer would choke on the next edit)" \
    test "$(grep -o '{{' "${SCRIPTS}/vault-${s}.sh" | wc -l)" = "$(grep -o '{{[A-Za-z][A-Za-z0-9]*}}' "${SCRIPTS}/vault-${s}.sh" | wc -l)"
done

# ── vault-watch.sh: inotify path ─────────────────────────────────────────────
spool="${tmp}/spool dir/it's"
watch="${tmp}/watch.sh"
render "${SCRIPTS}/vault-watch.sh" "${watch}" "dir=$(q "${spool}")" heartbeat=3 fallback=1
ok "rendered vault-watch.sh parses" bash -n "${watch}"
bash "${watch}" >"${tmp}/watch.out" 2>/dev/null &
watch_pid=$!
bg+=("${watch_pid}")
ok "the watcher creates the spool" wait_for test -d "${spool}/responses"
ok "spool base is 0700" test "$(mode_of "${spool}")" = 700
ok "requests/ is 0700" test "$(mode_of "${spool}/requests")" = 700
ok "responses/ is 0700" test "$(mode_of "${spool}/responses")" = 700
ok "the watcher runs inotifywait on requests/" wait_for test "$(watchers_of "${spool}/requests")" = 1

printf 'old' >"${spool}/responses/old-00001.json"
touch -d '3 minutes ago' "${spool}/responses/old-00001.json"
printf 'old' >"${spool}/responses/.tmp.old-00002"
touch -d '3 minutes ago' "${spool}/responses/.tmp.old-00002"
printf 'fresh' >"${spool}/responses/fresh-0001.json"
printf 'fresh' >"${spool}/responses/.tmp.fresh-0002"
printf 'half' >"${spool}/requests/.tmp.writing-01"
printf 'half' >"${spool}/requests/.tmp.abandoned-1"
touch -d '3 minutes ago' "${spool}/requests/.tmp.abandoned-1"
printf '{"id":"stale-000001"}\n' >"${spool}/requests/stale-000001.json.claimed.99999"
touch -d '5 minutes ago' "${spool}/requests/stale-000001.json.claimed.99999"
printf '{"id":"held-0000001"}\n' >"${spool}/requests/held-0000001.json.claimed.88888"

# Published the way the CLI does it: write a temp file, rename it into place.
printf '{"v":1,\r\n"id":"req-0000001",\n"op":"get"}\r\n' >"${spool}/requests/.tmp.req-0000001"
mv "${spool}/requests/.tmp.req-0000001" "${spool}/requests/req-0000001.json"
# With a 3 s heartbeat, only the inotify event can deliver this within a second.
within_a_second() {
  local i
  for ((i = 0; i < 10; i++)); do
    grep -qxF '{"v":1,"id":"req-0000001","op":"get"}' "${tmp}/watch.out" && return 0
    sleep 0.1
  done
  return 1
}
ok "a published request streams out within a second (inotify, not the heartbeat)" within_a_second
head -c 70000 /dev/zero | tr '\0' 'x' >"${spool}/requests/.tmp.big-0000001"
mv "${spool}/requests/.tmp.big-0000001" "${spool}/requests/big-0000001.json"
one_cut_line() { awk 'length($0) == 65536 && /^x+$/ { f = 1 } END { exit !f }' "$1"; }
ok "an oversized request is cut at 64 KiB, still one line" wait_for one_cut_line "${tmp}/watch.out"
ok "a stale claim (dead watcher) is recovered and delivered" grep -qxF '{"id":"stale-000001"}' "${tmp}/watch.out"
ok "a fresh claim is left to its live watcher" test -e "${spool}/requests/held-0000001.json.claimed.88888"
ok "a fresh claim is not delivered" sh -c "! grep -q held-0000001 '${tmp}/watch.out'"
ok "a request still being written (.tmp.*) is not claimed" test -e "${spool}/requests/.tmp.writing-01"
ok "an abandoned .tmp request is swept after 2 minutes" test ! -e "${spool}/requests/.tmp.abandoned-1"
ok "an uncollected response older than 2 minutes is swept" test ! -e "${spool}/responses/old-00001.json"
ok "an abandoned .tmp response is swept" test ! -e "${spool}/responses/.tmp.old-00002"
ok "a fresh response is kept for its CLI" test -e "${spool}/responses/fresh-0001.json"
ok "a fresh .tmp response is kept" test -e "${spool}/responses/.tmp.fresh-0002"
ok "claimed requests leave nothing behind" test -z "$(find "${spool}/requests" -name '*.json' -o -name '*.claimed.9*')"
ok "a heartbeat line '#' follows when idle" wait_for grep -qx '#' "${tmp}/watch.out"
ok "every request is delivered exactly once" \
  test "$(grep -c '^{' "${tmp}/watch.out")" = 2
ok "every output line is a request, a cut request or a heartbeat" \
  test "$(grep -cvE '^(\{.*\}|x+|#)$' "${tmp}/watch.out")" = 0
kill "${watch_pid}" 2>/dev/null
wait "${watch_pid}" 2>/dev/null
ok "the watcher leaves no inotifywait behind when it is killed" \
  wait_for test "$(watchers_of "${spool}/requests")" = 0

# ── vault-watch.sh: fallback poll (no inotifywait on the VM) ─────────────────
noinotify="${tmp}/path-noinotify"
restricted_path "${noinotify}" 'inotifywait'
spool2="${tmp}/spool2"
render "${SCRIPTS}/vault-watch.sh" "${tmp}/watch2.sh" "dir=$(q "${spool2}")" heartbeat=1 fallback=1
PATH="${noinotify}" "${BASH}" "${tmp}/watch2.sh" >"${tmp}/watch2.out" 2>/dev/null &
watch2_pid=$!
bg+=("${watch2_pid}")
ok "fallback: the spool is created" wait_for test -d "${spool2}/requests"
printf '{"id":"poll-0000001"}' >"${spool2}/requests/.tmp.poll-0000001"
mv "${spool2}/requests/.tmp.poll-0000001" "${spool2}/requests/poll-0000001.json"
ok "fallback: a request is delivered by polling" wait_for grep -qxF '{"id":"poll-0000001"}' "${tmp}/watch2.out"
ok "fallback: heartbeats still flow" wait_for grep -qx '#' "${tmp}/watch2.out"
ok "fallback: no inotifywait was started" test "$(watchers_of "${spool2}/requests")" = 0
kill "${watch2_pid}" 2>/dev/null
wait "${watch2_pid}" 2>/dev/null

# ── vault-respond.sh ─────────────────────────────────────────────────────────
id='1730000000000-123-4567'
spool3="${tmp}/spool 3"
render "${SCRIPTS}/vault-respond.sh" "${tmp}/respond.sh" "dir=$(q "${spool3}")" "id=$(q "${id}")"
ok "rendered vault-respond.sh parses" bash -n "${tmp}/respond.sh"
printf '{"v":1,"id":"%s","status":"ok","secret":"AAEC\\t/w=="}\t\001\377' "${id}" >"${tmp}/response.in"
install -d -m 0700 "${spool3}/responses"
inotifywait -m -e create,close_write,moved_to --format '%e %f' "${spool3}/responses" >"${tmp}/events" 2>"${tmp}/events.err" &
iw_pid=$!
bg+=("${iw_pid}")
wait_for grep -q 'Watches established' "${tmp}/events.err" >/dev/null
bash "${tmp}/respond.sh" <"${tmp}/response.in"
rc=$?
sleep 0.2
kill "${iw_pid}" 2>/dev/null
wait "${iw_pid}" 2>/dev/null
ok "respond exits 0" test "${rc}" = 0
ok "the response lands as <id>.json, byte-exact from stdin" cmp -s "${tmp}/response.in" "${spool3}/responses/${id}.json"
ok "the response is 0600" test "$(mode_of "${spool3}/responses/${id}.json")" = 600
ok "the response appears by rename (the CLI never sees half of it)" grep -qx "MOVED_TO ${id}.json" "${tmp}/events"
ok "the final name is never written in place" sh -c "! grep -qE '^(CREATE|CLOSE_WRITE[A-Z_,]*) ${id}\\.json\$' '${tmp}/events'"
ok "no temp file is left behind" test -z "$(find "${spool3}/responses" -name '.tmp.*')"
rm -rf "${spool3}"
bash "${tmp}/respond.sh" <"${tmp}/response.in"
ok "respond creates a missing spool, 0700" test "$(mode_of "${spool3}/responses")" = 700
render "${SCRIPTS}/vault-respond.sh" "${tmp}/respond-bad.sh" "dir=$(q "${spool3}")" "id=$(q '../../escaped-1234')"
ok "an id outside the request-id alphabet is refused" sh -c "! bash '${tmp}/respond-bad.sh' <'${tmp}/response.in'"
ok "a refused id writes nothing" test -z "$(find "${tmp}" -name 'escaped-1234*')"
render "${SCRIPTS}/vault-respond.sh" "${tmp}/respond-empty.sh" "dir=$(q "${spool3}")" "id=$(q 'empty-response-1')"
ok "an empty response is refused" sh -c "! bash '${tmp}/respond-empty.sh' </dev/null"
ok "an empty response publishes nothing" test -z "$(find "${spool3}/responses" -name '*empty-response-1*')"

# ── vault-scan.sh ────────────────────────────────────────────────────────────
home="${tmp}/home"
other="${tmp}/other"
vd="${home}/vault spool"
mkdir -p "${home}/dir with space" "${home}/node_modules/pkg" "${home}/repo/.git/objects/ab" \
  "${home}/.cache/x" "${home}/proj/target/debug" "${home}/proj2/target" \
  "${home}/.claude/projects/p" "${other}" "${vd}/responses"
printf 'token=%s\n' "${S1}" >"${home}/notes.txt"
{ printf '\000\001\002binary'; printf '%s' "${S3}"; printf '\000tail'; } >"${home}/blob.bin"
odd="${home}/dir with space/odd"$'\n'"name 'q'.txt"
printf 'x %s y\n' "${S1}" >"${odd}"
printf 'one %s two {"k":"%s"} three %s\n' "${S1}" "${S2J}" "${S3}" >"${home}/multi.log"
printf '%s' "${S1}" >"${home}/node_modules/pkg/x.txt"
printf '%s' "${S1}" >"${home}/repo/.git/objects/ab/cdef"
printf '[remote]\n url = https://x:%s@example.invalid\n' "${S1}" >"${home}/repo/.git/config"
printf '%s' "${S1}" >"${home}/.cache/x/cached"
printf 'Signature: 8a477f597d28d172789f06886806bc55\n' >"${home}/proj/target/CACHEDIR.TAG"
printf '%s' "${S1}" >"${home}/proj/target/debug/out.txt"
printf '%s' "${S1}" >"${home}/proj2/target/out.txt"
{ printf '%s\n' "${S1}"; head -c 20000 /dev/zero | tr '\0' 'a'; } >"${home}/big.txt"
{ printf '%s\n' "${S1}"; head -c 20000 /dev/zero | tr '\0' 'a'; } >"${home}/.claude/projects/p/big.jsonl"
printf 'nothing here\n' >"${home}/clean.txt"
printf '{"secret":"%s"}' "${S1}" >"${vd}/responses/stale-000001.json"
ln -s notes.txt "${home}/link.txt"
printf 'other root %s\n' "${S3}" >"${other}/x.txt"
python3 - "${home}/app.db" <<'PY'
import os, sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('CREATE TABLE kv(k TEXT, v TEXT)')
con.execute('INSERT INTO kv VALUES (?, ?)', ('pw', os.environ['S2']))
con.commit()
con.close()
PY
# After the database: SQLite discards a stray -wal while it creates one.
printf 'wal frame %s\n' "${S1}" >"${home}/app.db-wal"

scan="${tmp}/scan.sh"
render "${SCRIPTS}/vault-scan.sh" "${scan}" "dir=$(q "${vd}")" \
  "roots=$(q "${home}") $(q "${tmp}/no such root") $(q "${other}")" "maxSize=$(q 16384)"
ok "rendered vault-scan.sh parses" bash -n "${scan}"
{
  printf '1\t%s\n' "$(b64 "${S1}")"
  printf '2\t%s\n' "$(b64 "${S2}")"
  printf '2\t%s\n' "$(b64 "${S2J}")"
  printf '3\t%s\n' "$(b64 "${S3}")"
  printf 'x\tbm9pc2U=\n\n7\t!!!not base64\n'
} >"${tmp}/scan.in"
bash "${scan}" <"${tmp}/scan.in" >"${tmp}/scan.out" 2>"${tmp}/scan.err"
rc=$?
ok "scan exits 0" test "${rc}" = 0
scan_view() {
  python3 - "$1" "${tmp}" <<'PY' | sort
import base64, os, sys
out, root = sys.argv[1], sys.argv[2]
for line in open(out, 'rb').read().split(b'\n'):
    f = line.split(b'\t')
    if f[0] != b'F':
        continue
    p = base64.b64decode(f[4]).decode()
    size = 'size' if int(f[2]) == os.stat(p).st_size else 'BADSIZE'
    print('%s|%s|%s|%s' % (p.replace(root, '~', 1).replace('\n', '\\n'), f[1].decode(), f[3].decode(), size))
PY
}
expected="$(sort <<EOF
~/home/.claude/projects/p/big.jsonl|1|text|size
~/home/app.db-wal|1|sqlite-aux|size
~/home/app.db|2|sqlite|size
~/home/blob.bin|3|binary|size
~/home/dir with space/odd\\nname 'q'.txt|1|text|size
~/home/multi.log|1,2,3|text|size
~/home/notes.txt|1|text|size
~/home/proj2/target/out.txt|1|text|size
~/home/repo/.git/config|1|text|size
~/other/x.txt|3|text|size
EOF
)"
actual="$(scan_view "${tmp}/scan.out")"
ok "scan reports exactly the files holding a secret (types, indexes, sizes)" test "${actual}" = "${expected}"
if [[ "${actual}" != "${expected}" ]]; then
  diff <(printf '%s\n' "${expected}") <(printf '%s\n' "${actual}") | sed 's/^/        /'
fi
tab=$'\t'
ok "scan output is F lines then DONE" \
  test "$(grep -cvE "^(F${tab}[0-9]+(,[0-9]+)*${tab}[0-9]+${tab}(sqlite|sqlite-aux|binary|text)${tab}[A-Za-z0-9+/]+=*|DONE${tab}[0-9]+)\$" "${tmp}/scan.out")" = 0
ok "DONE counts the F lines and comes last" \
  test "$(tail -n 1 "${tmp}/scan.out")" = "DONE${tab}$(grep -c '^F' "${tmp}/scan.out")"
ok "the scan's work dir is gone" test -z "$(find "${vd}" -maxdepth 1 -name 'scan.*')"
ok "the scan prints no errors (a missing root is just skipped)" test ! -s "${tmp}/scan.err"

render "${SCRIPTS}/vault-scan.sh" "${tmp}/scan-none.sh" "dir=$(q "${vd}")" "roots=$(q "${tmp}/no such root")" "maxSize=$(q 4096)"
ok "with no existing root the scan still finishes" \
  test "$(bash "${tmp}/scan-none.sh" <"${tmp}/scan.in")" = "DONE${tab}0"
ok "with no patterns the scan reports nothing" \
  test "$(bash "${scan}" </dev/null)" = "DONE${tab}0"

# A multi-line secret (a private key) only counts whole: with LF or CRLF line ends, or
# JSON-escaped. Every unencrypted ed25519 key starts with the same lines, so another key, a
# fragment of the key or its lines with something in between must not be reported.
keys="${tmp}/keys"
mkdir -p "${keys}"
kh='b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW'
key_of() { printf '%s\n' '-----BEGIN OPENSSH PRIVATE KEY-----' "${kh}" "QyNTUxOQAAACA$1" "$2" '-----END OPENSSH PRIVATE KEY-----'; }
# $(...) drops the final newline, as Patterns trims the value. K5 shares K4's header, armor
# and last line; only the key material differs.
K4="$(key_of "${r1}material" "tail${r3}")"
K5="$(key_of "${r2}material" "tail${r3}")"
K4C="${K4//$'\n'/$'\r\n'}"
J4="${K4//$'\n'/\\n}"
printf '%s\n' "${K4}" >"${keys}/id_four"
printf '%s\r\n' "${K4C}" >"${keys}/id_four_crlf"
printf '%s\n' "${K5}" >"${keys}/id_other"
printf 'key: %s # end\n' "${K4}" >"${keys}/embedded.txt"
printf '%s\n%s\n' "${K5}" "${K4}" >"${keys}/both.txt"
printf '%s\n' '-----BEGIN OPENSSH PRIVATE KEY-----' "${kh}" "${K4}" >"${keys}/restart.txt"
printf '%s\n' "${K4}" | sed '3a inserted' >"${keys}/broken.txt"
printf '%s\n' "${K4}" | head -n 4 >"${keys}/partial.txt"
printf '%s\n' "${kh}" >"${keys}/header-only.log"
printf '{"text":"%s"}\n' "${J4}" >"${keys}/t.jsonl"
printf 'token %s\n' "${S1}" >"${keys}/token.txt"
{
  printf '4\t%s\n' "$(b64 "${K4}")"
  printf '4\t%s\n' "$(b64 "${K4C}")"
  printf '4\t%s\n' "$(b64 "${J4}")"
} >"${tmp}/keys-only.in"
{ printf '1\t%s\n' "$(b64 "${S1}")"; cat "${tmp}/keys-only.in"; } >"${tmp}/keys.in"
render "${SCRIPTS}/vault-scan.sh" "${tmp}/scan-keys.sh" "dir=$(q "${vd}")" "roots=$(q "${keys}")" "maxSize=$(q 16384)"
bash "${tmp}/scan-keys.sh" <"${tmp}/keys.in" >"${tmp}/keys.out" 2>"${tmp}/keys.err"
expected_keys="$(sort <<EOF
~/keys/both.txt|4|text|size
~/keys/embedded.txt|4|text|size
~/keys/id_four_crlf|4|text|size
~/keys/id_four|4|text|size
~/keys/restart.txt|4|text|size
~/keys/t.jsonl|4|text|size
~/keys/token.txt|1|text|size
EOF
)"
actual="$(scan_view "${tmp}/keys.out")"
ok "a key is reported only where it is whole (not another key, a fragment, or lines apart)" test "${actual}" = "${expected_keys}"
if [[ "${actual}" != "${expected_keys}" ]]; then
  diff <(printf '%s\n' "${expected_keys}") <(printf '%s\n' "${actual}") | sed 's/^/        /'
fi
ok "the key scan prints no errors" test ! -s "${tmp}/keys.err"
bash "${tmp}/scan-keys.sh" <"${tmp}/keys-only.in" >"${tmp}/keys-only.out" 2>/dev/null
ok "with the key as the only secret, its anchor line alone still proves nothing" \
  test "$(scan_view "${tmp}/keys-only.out")" = "$(grep -v token.txt <<<"${expected_keys}")"
ok "the key scan's work dir is gone" test -z "$(find "${vd}" -maxdepth 1 -name 'scan.*')"

# The patterns must never be visible to other processes: sample ps while a scan of a
# bigger tree runs through both passes.
big="${tmp}/bigtree"
python3 - "${big}" <<'PY'
import os, sys
root = sys.argv[1]
secrets = [os.environ['S1'], os.environ['S2'], os.environ['S3']]
filler = 'lorem ipsum dolor sit amet ' * 150
for d in range(40):
    os.makedirs(os.path.join(root, 'd%02d' % d))
    for f in range(60):
        n = d * 60 + f
        with open(os.path.join(root, 'd%02d' % d, 'f%04d.txt' % n), 'w') as fh:
            fh.write(filler)
            if n % 8 == 0:
                fh.write(secrets[n % 3])
PY
render "${SCRIPTS}/vault-scan.sh" "${tmp}/scan-big.sh" "dir=$(q "${vd}")" "roots=$(q "${big}")" "maxSize=$(q 1048576)"
{
  printf '%s\n%s\n%s\n%s\n' "${S1}" "${S2}" "${S2J}" "${S3}"
  b64 "${S1}"; printf '\n'; b64 "${S2}"; printf '\n'; b64 "${S2J}"; printf '\n'; b64 "${S3}"; printf '\n'
} >"${tmp}/needles"
bash "${tmp}/scan-big.sh" <"${tmp}/scan.in" >"${tmp}/scan-big.out" 2>/dev/null &
scan_pid=$!
bg+=("${scan_pid}")
samples=0
: >"${tmp}/ps.samples"
while kill -0 "${scan_pid}" 2>/dev/null; do
  ps -eo args >>"${tmp}/ps.samples" 2>/dev/null
  samples=$((samples + 1))
done
wait "${scan_pid}" 2>/dev/null
ok "the big scan finds every planted secret" test "$(grep -c '^F' "${tmp}/scan-big.out")" = 300
ok "ps was sampled while the scan's greps ran" grep -q 'grep -[lq]Z*F' "${tmp}/ps.samples"
ok "no secret (raw or base64) ever shows up in ps (${samples} samples)" \
  sh -c "! grep -qF -f '${tmp}/needles' '${tmp}/ps.samples'"

# ── vault-clean.sh ───────────────────────────────────────────────────────────
cd_="${tmp}/clean"
cvd="${tmp}/clean spool"
mkdir -p "${cd_}"
clean="${tmp}/clean.sh"
render "${SCRIPTS}/vault-clean.sh" "${clean}" "dir=$(q "${cvd}")"
ok "rendered vault-clean.sh parses" bash -n "${clean}"
# expect <file> <out>: what the file must look like with every secret masked by '*'.
expect_masked() {
  python3 - "$1" "$2" <<'PY'
import os, sys
data = open(sys.argv[1], 'rb').read()
for s in sorted({os.environ[k].encode() for k in ('S1', 'S2', 'S2J', 'S3')}, key=len, reverse=True):
    data = data.replace(s, b'*' * len(s))
open(sys.argv[2], 'wb').write(data)
PY
}

text="${cd_}/session log.jsonl"
{
  printf '{"a":"before"}\n'
  printf '{"msg":"token %s and again %s"}\n' "${S1}" "${S1}"
  printf '{"msg":"escaped %s"}\n' "${S2J}"
  printf '{"msg":"third %s"}\n' "${S3}"
  printf '{"z":"after"}\n'
} >"${text}"
expect_masked "${text}" "${tmp}/text.expected"
text_inode="$(inode_of "${text}")"
text_size="$(size_of "${text}")"
bin="${cd_}/blob.bin"
python3 - "${bin}" <<'PY'
import os, sys
with open(sys.argv[1], 'wb') as f:
    f.write(os.urandom(5000).replace(b'*', b'+') + b'\0' + os.environ['S3'].encode() + b'\0\xff' + os.urandom(3000).replace(b'*', b'+'))
PY
expect_masked "${bin}" "${tmp}/bin.expected"
live="${cd_}/live.log"
printf 'start %s\n' "${S1}" >"${live}"
(
  exec 3>>"${live}"
  while :; do
    printf 'tick\n' >&3
    sleep 0.05
  done
) &
appender=$!
bg+=("${appender}")
: >"${cd_}/empty.txt"
printf 'bye %s\n' "${S1}" >"${cd_}/delete me.txt"
mkdir "${cd_}/a dir"
ln -s "session log.jsonl" "${cd_}/link.jsonl"
printf 'x' >"${cd_}/state.db-wal"

db="${cd_}/state.db"
fts5="$(python3 - "${db}" <<'PY'
import os, sqlite3, sys
S1, S2, S3 = os.environ['S1'], os.environ['S2'], os.environ['S3']
con = sqlite3.connect(sys.argv[1], isolation_level=None)
con.execute('PRAGMA journal_mode=WAL')
con.execute('PRAGMA secure_delete=OFF')
con.execute('CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT, data BLOB, n INTEGER,'
            ' g TEXT GENERATED ALWAYS AS (upper(body)) VIRTUAL)')
con.execute('INSERT INTO notes(body, data, n) VALUES (?, NULL, 1)', ('token ' + S1 + ' end',))
con.execute('INSERT INTO notes(body, data, n) VALUES (?, ?, 2)', ('again ' + S1, b'\x00\x01' + S1.encode() + b'\x00\xff'))
con.execute('INSERT INTO notes(body, data, n) VALUES (?, ?, 3)', ('harmless', b'\x00\x02'))
con.execute('CREATE TABLE "odd ""table"" name"("my col" TEXT)')
con.execute('INSERT INTO "odd ""table"" name" VALUES (?)', ('x' + S2 + 'y',))
# Numeric columns are never scanned: the INTEGER below spells S4 in its decimal form and
# must stay an untouched integer, while the text-like columns next to it are scrubbed.
S4 = os.environ['S4']
con.execute('CREATE TABLE mixed(id INTEGER PRIMARY KEY, n INTEGER, r REAL, t TEXT, j JSON, c VARCHAR(200))')
con.execute('INSERT INTO mixed(n, r, t, j, c) VALUES (?, ?, ?, ?, ?)',
            (int('1' + S4 + '0'), 1.5, 'a' + S1 + 'b' + S3 + 'c', '{"k":"' + S2 + '"}', 'v ' + S4))
fts5 = True
try:
    con.execute('CREATE VIRTUAL TABLE docs USING fts5(title, body)')
    con.execute('INSERT INTO docs VALUES (?, ?)', ('t1', 'pasted ' + S1 + ' here'))
    con.execute('INSERT INTO docs VALUES (?, ?)', ('t2', 'nothing to see'))
except sqlite3.OperationalError:
    fts5 = False
# A row deleted before secure_delete was on: its bytes stay behind in the page.
con.execute('CREATE TABLE trash(v TEXT)')
con.execute('INSERT INTO trash VALUES (?)', ('gone ' + S3 * 20,))
con.execute('DELETE FROM trash')
con.execute('PRAGMA wal_checkpoint(TRUNCATE)')
con.close()
print('yes' if fts5 else 'no')
PY
)"
ok "precondition: the deleted row's secret is still in the database file" grep -qaF "${S3}" "${db}"

{
  printf 'redact\t%s\t%s\t%s\t%s\t%s\n' "$(b64 "${text}")" "$(b64 "${S1}")" "$(b64 "${S2}")" "$(b64 "${S2J}")" "$(b64 "${S3}")"
  printf 'redact\t%s\t%s\t%s\n' "$(b64 "${bin}")" "$(b64 "${S1}")" "$(b64 "${S3}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${live}")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\t%s\t%s\t%s\n' "$(b64 "${db}")" "$(b64 "${S1}")" "$(b64 "${S2}")" "$(b64 "${S3}")" "$(b64 "${S4}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/empty.txt")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/state.db-wal")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/no such file")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/link.jsonl")" "$(b64 "${S1}")"
  printf 'delete\t%s\n' "$(b64 "${cd_}/delete me.txt")"
  printf 'delete\t%s\n' "$(b64 "${cd_}/never there")"
  printf 'delete\t%s\n' "$(b64 "${cd_}/a dir")"
  printf 'redact\t%%%%bad%%%%\t%s\n' "$(b64 "${S1}")"
  printf 'shred\t%s\n' "$(b64 "${cd_}/odd action")"
} >"${tmp}/clean.in"
live_inode="$(inode_of "${live}")"
bash "${clean}" <"${tmp}/clean.in" >"${tmp}/clean.out" 2>"${tmp}/clean.err"
rc=$?
live_size="$(size_of "${live}")"
sleep 0.3
kill "${appender}" 2>/dev/null
wait "${appender}" 2>/dev/null
result() { grep -F "${tab}$(b64 "$1")${tab}" "${tmp}/clean.out" | cut -f2,4- | tr '\t' ' '; }
ok "clean exits 0" test "${rc}" = 0
ok "text: every occurrence of every variant is masked, nothing else changes" cmp -s "${tmp}/text.expected" "${text}"
ok "text: reported ok with 4 replacements" test "$(result "${text}")" = 'ok 4'
ok "text: same inode (edited in place, never recreated)" test "$(inode_of "${text}")" = "${text_inode}"
ok "text: same size" test "$(size_of "${text}")" = "${text_size}"
ok "binary: the secret is masked, every other byte is untouched" cmp -s "${tmp}/bin.expected" "${bin}"
ok "binary: reported ok with 1 replacement" test "$(result "${bin}")" = 'ok 1'
ok "live log: same inode" test "$(inode_of "${live}")" = "${live_inode}"
ok "live log: the secret is masked" sh -c "! grep -qF -- \"\${S1}\" '${live}' && grep -q '^start \\*\\{${#S1}\\}\$' '${live}'"
ok "live log: the appender keeps writing into the same file afterwards" test "$(size_of "${live}")" -gt "${live_size}"
ok "zero-length file: ok, nothing to do" test "$(result "${cd_}/empty.txt")" = 'ok 0'
ok "-wal side file: skipped (the database itself is scrubbed)" test "$(result "${cd_}/state.db-wal")" = 'skipped 0'
ok "missing file: reported missing" test "$(result "${cd_}/no such file")" = 'missing 0'
ok "symlink: refused" test "$(result "${cd_}/link.jsonl")" = 'failed 0 not a regular file'
ok "delete: the file is gone" test ! -e "${cd_}/delete me.txt"
ok "delete: reported ok" test "$(result "${cd_}/delete me.txt")" = 'ok 0'
ok "delete of a missing file: reported missing" test "$(result "${cd_}/never there")" = 'missing 0'
ok "delete of a directory: refused" test "$(result "${cd_}/a dir")" = 'failed 0 is a directory'
ok "a malformed path: reported failed" grep -qx "R${tab}failed${tab}%%bad%%${tab}0${tab}bad path" "${tmp}/clean.out"
ok "an unknown action: reported failed" test "$(result "${cd_}/odd action")" = 'failed 0 unknown action'
ok "clean output is R lines then DONE" \
  test "$(grep -cvE "^(R${tab}(ok|partial|missing|failed|skipped)${tab}[^${tab}]*${tab}[0-9]+(${tab}[^${tab}]+)?|DONE)\$" "${tmp}/clean.out")" = 0
ok "DONE comes last, one R line per request" \
  test "$(tail -n 1 "${tmp}/clean.out"):$(grep -c '^R' "${tmp}/clean.out")" = "DONE:13"
ok "the clean's work dir is gone" test -z "$(find "${cvd}" -maxdepth 1 -name 'clean.*')"
ok "clean prints no errors" test ! -s "${tmp}/clean.err"

# notes: body x2 + data x1, the odd table x1, mixed: t + j + c, docs (FTS5) x1.
if [[ "${fts5}" == yes ]]; then db_changed=8; else db_changed=7; fi
ok "sqlite: reported ok with every changed row counted" test "$(result "${db}")" = "ok ${db_changed}"
python3 - "${db}" "${fts5}" >"${tmp}/db.check" 2>&1 <<'PY'
import os, sqlite3, sys
db, fts5 = sys.argv[1], sys.argv[2] == 'yes'
S1, S2, S3 = os.environ['S1'], os.environ['S2'], os.environ['S3']
con = sqlite3.connect(db)
def check(name, cond):
    print(('ok   ' if cond else 'FAIL ') + name)
rows = con.execute('SELECT id, body, data, n, g FROM notes ORDER BY id').fetchall()
check('notes keeps its 3 rows', len(rows) == 3)
check('text column masked', rows[0][1] == 'token ' + '*' * len(S1) + ' end' and rows[1][1] == 'again ' + '*' * len(S1))
check('blob column masked byte-exact, still a blob',
      rows[1][2] == b'\x00\x01' + b'*' * len(S1) + b'\x00\xff' and isinstance(rows[1][2], bytes))
check('untouched row unchanged', rows[2][1:4] == ('harmless', b'\x00\x02', 3))
check('generated column follows', rows[0][4] == rows[0][1].upper())
odd = con.execute('SELECT "my col" FROM "odd ""table"" name"').fetchall()
check('quoted table/column names handled', odd == [('x' + '*' * len(S2) + 'y',)])
S4 = os.environ['S4']
mixed = con.execute('SELECT n, typeof(n), r, t, j, c FROM mixed').fetchall()
check('INTEGER/REAL columns untouched', mixed[0][:3] == (int('1' + S4 + '0'), 'integer', 1.5))
check('several secrets in one value all masked', mixed[0][3] == 'a' + '*' * len(S1) + 'b' + '*' * len(S3) + 'c')
check('JSON column scrubbed', mixed[0][4] == '{"k":"' + '*' * len(S2) + '"}')
check('VARCHAR column scrubbed', mixed[0][5] == 'v ' + '*' * len(S4))
if fts5:
    docs = con.execute('SELECT title, body FROM docs ORDER BY title').fetchall()
    check('fts5 row updated, row count unchanged', docs == [('t1', 'pasted ' + '*' * len(S1) + ' here'), ('t2', 'nothing to see')])
    # unicode61 splits at the underscore and lowercases: this is how S1 sits in the index.
    token = S1.split('_', 1)[1].lower()
    check('fts5 index no longer finds the secret', con.execute(
        'SELECT count(*) FROM docs WHERE docs MATCH ?', ('"%s"' % token,)).fetchone()[0] == 0)
    con.execute("INSERT INTO docs(docs) VALUES('integrity-check')")
    check('fts5 index consistent', True)
check('integrity_check ok', con.execute('PRAGMA integrity_check').fetchone()[0] == 'ok')
con.close()
raw = open(db, 'rb').read()
wal = open(db + '-wal', 'rb').read() if os.path.exists(db + '-wal') else b''
for name, s in (('S1', S1), ('S2', S2), ('S3', S3)):
    check('no %s bytes left in the database file or its WAL' % name, s.encode() not in raw + wal)
if fts5:
    check('no tokenized secret left in the FTS segments', token.encode() not in raw + wal)
PY
while IFS= read -r line; do
  ok "sqlite: ${line#* }" test "${line%% *}" = ok
done <"${tmp}/db.check"
[[ "${fts5}" == yes ]] || printf '  SKIP  sqlite: FTS5 checks (this python3 has no FTS5)\n'

# A reader holding an old snapshot keeps the WAL checkpoint busy: the rows are scrubbed,
# but the old pages are still on disk, so the result must say "partial", not "ok".
busy="${cd_}/busy.db"
python3 - "${busy}" <<'PY'
import os, sqlite3, sys
con = sqlite3.connect(sys.argv[1], isolation_level=None)
con.execute('PRAGMA journal_mode=WAL')
con.execute('CREATE TABLE t(v TEXT)')
con.execute('INSERT INTO t VALUES (?)', ('k=' + os.environ['S1'],))
con.close()
PY
python3 - "${busy}" >"${tmp}/reader.out" <<'PY' &
import sqlite3, sys, time
con = sqlite3.connect(sys.argv[1], isolation_level=None)
con.execute('BEGIN')
con.execute('SELECT count(*) FROM t').fetchone()
print('ready', flush=True)
time.sleep(60)
PY
reader=$!
bg+=("${reader}")
wait_for grep -qx ready "${tmp}/reader.out"
printf 'redact\t%s\t%s\n' "$(b64 "${busy}")" "$(b64 "${S1}")" >"${tmp}/busy.in"
bash "${clean}" <"${tmp}/busy.in" >"${tmp}/busy.out" 2>/dev/null &
busy_pid=$!
bg+=("${busy_pid}")
: >"${tmp}/ps2.samples"
while kill -0 "${busy_pid}" 2>/dev/null; do
  ps -eo args >>"${tmp}/ps2.samples" 2>/dev/null
  sleep 0.05
done
wait "${busy_pid}" 2>/dev/null
kill "${reader}" 2>/dev/null
wait "${reader}" 2>/dev/null
ok "busy checkpoint: reported partial, the row still counted" \
  grep -qx "R${tab}partial${tab}$(b64 "${busy}")${tab}1${tab}database busy, write-ahead log not checkpointed" "${tmp}/busy.out"
ok "busy checkpoint: the row itself is scrubbed" \
  test "$(python3 -c 'import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute("SELECT v FROM t").fetchone()[0])' "${busy}")" = "k=$(printf '%*s' "${#S1}" '' | tr ' ' '*')"
ok "no secret shows up in ps while clean works" sh -c "! grep -qF -f '${tmp}/needles' '${tmp}/ps2.samples'"
ok "ps was sampled while clean's python3 ran" grep -q '^python3 -c' "${tmp}/ps2.samples"

# python3 dying on one file (SIGBUS when another process truncates a file it has mapped,
# the OOM killer, ...) must cost that one file, not the rest of the batch.
locked="${cd_}/locked.db"
python3 - "${locked}" <<'PY'
import os, sqlite3, sys
con = sqlite3.connect(sys.argv[1])
con.execute('CREATE TABLE t(v TEXT)')
con.execute('INSERT INTO t VALUES (?)', (os.environ['S1'],))
con.commit()
con.close()
PY
python3 - "${locked}" >"${tmp}/locker.out" <<'PY' &
import sqlite3, sys, time
con = sqlite3.connect(sys.argv[1], isolation_level=None)
con.execute('BEGIN EXCLUSIVE')
print('ready', flush=True)
time.sleep(60)
PY
locker=$!
bg+=("${locker}")
wait_for grep -qx ready "${tmp}/locker.out"
printf 'before %s\n' "${S1}" >"${cd_}/before.txt"
printf 'after %s\n' "${S1}" >"${cd_}/after.txt"
{
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/before.txt")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${locked}")" "$(b64 "${S1}")"
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/after.txt")" "$(b64 "${S1}")"
} >"${tmp}/crash.in"
bash "${clean}" <"${tmp}/crash.in" >"${tmp}/crash.out" 2>/dev/null &
crash_pid=$!
bg+=("${crash_pid}")
# Wait until python3 sits on line 1 (the locked database), then kill it there.
on_line_one() { [[ "$(cat "${cvd}"/clean.*/at 2>/dev/null)" == 1 ]]; }
wait_for on_line_one
py_pid="$(pgrep -P "${crash_pid}" -x python3)"
ok "python3 is blocked on the locked database" test -n "${py_pid}"
[[ -n "${py_pid}" ]] && kill -KILL "${py_pid}"
wait "${crash_pid}" 2>/dev/null
kill "${locker}" 2>/dev/null
wait "${locker}" 2>/dev/null
ok "crash: the file before it was scrubbed" test "$(grep -c "^R${tab}ok${tab}$(b64 "${cd_}/before.txt")${tab}1\$" "${tmp}/crash.out")" = 1
ok "crash: the file it died on is reported failed" \
  grep -qx "R${tab}failed${tab}$(b64 "${locked}")${tab}0${tab}interrupted while redacting" "${tmp}/crash.out"
ok "crash: the files behind it are still scrubbed" grep -qx "R${tab}ok${tab}$(b64 "${cd_}/after.txt")${tab}1" "${tmp}/crash.out"
ok "crash: the run still ends with DONE" test "$(tail -n 1 "${tmp}/crash.out")" = DONE
ok "crash: the work dir is gone" test -z "$(find "${cvd}" -maxdepth 1 -name 'clean.*')"

# A key is redacted whole; the key next to it keeps the lines the two share.
cp "${keys}/both.txt" "${cd_}/both.txt"
printf 'redact\t%s\t%s\t%s\t%s\n' "$(b64 "${cd_}/both.txt")" "$(b64 "${K4}")" "$(b64 "${K4C}")" "$(b64 "${J4}")" >"${tmp}/keys-clean.in"
bash "${clean}" <"${tmp}/keys-clean.in" >"${tmp}/keys-clean.out" 2>/dev/null
{ printf '%s\n' "${K5}"; printf '%*s\n' "${#K4}" '' | tr ' ' '*'; } >"${tmp}/both.expected"
ok "key: masked whole, the other key untouched" cmp -s "${tmp}/both.expected" "${cd_}/both.txt"
ok "key: reported ok with 1 replacement" grep -qx "R${tab}ok${tab}$(b64 "${cd_}/both.txt")${tab}1" "${tmp}/keys-clean.out"

# Without python3: deletes still happen, every redaction says why it could not.
nopython="${tmp}/path-nopython"
restricted_path "${nopython}" 'python3*'
printf 'x %s\n' "${S1}" >"${cd_}/keep.txt"
printf 'x\n' >"${cd_}/drop.txt"
{
  printf 'redact\t%s\t%s\n' "$(b64 "${cd_}/keep.txt")" "$(b64 "${S1}")"
  printf 'delete\t%s\n' "$(b64 "${cd_}/drop.txt")"
  printf 'delete\t%s\n' "$(b64 "${cd_}/never there")"
} >"${tmp}/nopy.in"
PATH="${nopython}" "${BASH}" "${clean}" <"${tmp}/nopy.in" >"${tmp}/nopy.out" 2>/dev/null
ok "no python3: redact reports failed / python3 missing" \
  grep -qx "R${tab}failed${tab}$(b64 "${cd_}/keep.txt")${tab}0${tab}python3 missing" "${tmp}/nopy.out"
ok "no python3: the file is left alone" grep -qF -- "${S1}" "${cd_}/keep.txt"
ok "no python3: delete still works" test ! -e "${cd_}/drop.txt"
ok "no python3: delete reported ok" grep -qx "R${tab}ok${tab}$(b64 "${cd_}/drop.txt")${tab}0" "${tmp}/nopy.out"
ok "no python3: a missing file is reported missing" grep -qx "R${tab}missing${tab}$(b64 "${cd_}/never there")${tab}0" "${tmp}/nopy.out"
ok "no python3: DONE comes last" test "$(tail -n 1 "${tmp}/nopy.out")" = DONE
ok "no python3: the work dir is gone" test -z "$(find "${cvd}" -maxdepth 1 -name 'clean.*')"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
