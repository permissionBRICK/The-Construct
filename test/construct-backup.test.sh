#!/usr/bin/env bash
# Contract tests for `construct backup` (bin/construct-backup.sh), the guest half
# of docs/plans/host-config-backups.md: the container and keys format, the round
# trip create -> container -> restore, the host upload path against a
# route-aware curl stub that stores uploaded bodies and serves them back,
# failure handling (DELETE + report), the space check, the lock and the
# recipients.
#
# Real age, zstd and openssl; a throw-away RSA-3072 key plays the PC. Every run
# happens under `env -i` with scratch paths, stubbed curl and systemctl: nothing
# here touches /etc/construct, /root, a real service or a real host.
# Run: bash test/construct-backup.test.sh
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
BACKUP="${ROOT}/bin/construct-backup.sh"
tmp="$(mktemp -d)"
holder_pid=""
trap 'if [[ -n "${holder_pid}" ]]; then kill "${holder_pid}" 2>/dev/null; fi; if [[ "${KEEP_TEST_TMP:-0}" == 1 ]]; then printf "kept test fixture: %s\n" "${tmp}"; else rm -rf "${tmp}"; fi' EXIT

pass=0
fail=0
ok() {
  local name="$1"; shift
  if "$@" >/dev/null; then pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"
  fi
}

for tool in age age-keygen zstd openssl jq flock sqlite3 python3; do
  if ! command -v "${tool}" >/dev/null 2>&1; then
    printf 'construct-backup tests need %s (apt-get install -y age zstd openssl jq util-linux sqlite3 python3)\n' "${tool}" >&2
    exit 1
  fi
done
if [[ "${EUID}" -ne 0 ]]; then
  printf 'construct-backup tests run as root (the CLI is root only)\n' >&2
  exit 1
fi

SYS_PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
BACKUP_ID=0123456789abcdef0123456789abcdef
OTHER_ID=fedcba9876543210fedcba9876543210

# ── fixture ──────────────────────────────────────────────────────────────────

stubs="${tmp}/stubs"
state="${tmp}/state"
mkdir -p "${stubs}" "${state}" "${tmp}/stage" "${tmp}/tmpdir" "${tmp}/repo" "${tmp}/workspace"

token_file="${tmp}/vm-token"
printf 'sentinel-vm-token\n' >"${token_file}"
chmod 0600 "${token_file}"
ca_file="${tmp}/service-ca.pem"
printf 'test ca\n' >"${ca_file}"
host_cfg="${tmp}/config-host.env"
cat >"${host_cfg}" <<EOF
CONSTRUCT_SERVICE_URL=https://construct.example:7462
CONSTRUCT_INSTANCE_NAME=work-vm
CONSTRUCT_SERVICE_CA_FILE=${ca_file}
CONSTRUCT_VM_TOKEN_FILE=${token_file}
EOF
local_cfg="${tmp}/config-local.env"
printf 'CONSTRUCT_INSTANCE_NAME=local-vm\n' >"${local_cfg}"

# The home to back up: credentials, an instruction file, a live T3 Code store
# in WAL mode, a project profile and a serve-web token outside home.
home="${tmp}/home"
mkdir -p "${home}/.claude" "${home}/.t3/userdata" "${home}/.codex"
printf '# saved instructions\n' >"${home}/.claude/CLAUDE.md"
printf 'https://alice:sentinel-git-secret@Git.Example.com:8443/group\nhttps://bob:other-secret@code.example.org\n\nnot-a-url\n' \
  >"${home}/.git-credentials"
chmod 0600 "${home}/.git-credentials"
python3 -I - "${home}/.t3/userdata/statev2.sqlite" <<'PY'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
c.execute("pragma journal_mode=wal")
c.execute("create table threads(title text)")
c.execute("insert into threads values ('saved thread')")
c.commit()
# Leave the connection open with an uncheckpointed write in the -wal.
c.execute("pragma wal_autocheckpoint=0")
c.execute("insert into threads values ('wal-only thread')")
c.commit()
import os
os._exit(0)
PY
projects="${tmp}/projects"
mkdir -p "${projects}"
printf '{"name":"demo","repos":[{"url":"https://git.example.com/demo.git","directory":"demo"}]}\n' >"${projects}/demo.json"
sw_token="${tmp}/sw-token"
printf 'sentinel-serve-web-token' >"${sw_token}"
chmod 0600 "${sw_token}"
recips="${tmp}/recipients"
lock="${tmp}/backup.lock"

# The PC: an RSA-3072 key pair; the private half unwraps E below.
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "${tmp}/pc.key" 2>/dev/null
openssl pkey -in "${tmp}/pc.key" -pubout -out "${tmp}/pc.pub"
fp_expected="$(openssl pkey -pubin -in "${tmp}/pc.pub" -outform DER | sha256sum | cut -d' ' -f1)"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "${tmp}/pc2.key" 2>/dev/null
openssl pkey -in "${tmp}/pc2.key" -pubout -out "${tmp}/pc2.pub"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "${tmp}/short.key" 2>/dev/null
openssl pkey -in "${tmp}/short.key" -pubout -out "${tmp}/short.pub"
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "${tmp}/ec.key" 2>/dev/null
openssl pkey -in "${tmp}/ec.key" -pubout -out "${tmp}/ec.pub"

# ── stubs ────────────────────────────────────────────────────────────────────

cat >"${stubs}/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SYSTEMCTL_LOG}"
case "$1" in
  is-active)
    if [[ "${T3_ACTIVE:-}" == 1 && "$*" == *t3code-serve* ]]; then
      [[ "$2" == --quiet ]] || echo active
      exit 0
    fi
    [[ "$2" == --quiet ]] || echo inactive
    exit 3 ;;
  is-enabled) exit 1 ;;
  *) exit 0 ;;
esac
STUB

# The host service: routes under /api/v1/vms/work-vm/backups. Uploaded keys and
# payloads are kept in ${STUB_DIR}/store and served back. Scenario files in
# ${STUB_DIR} switch failures on.
cat >"${stubs}/curl" <<'STUB'
#!/usr/bin/env bash
set -u
d="${STUB_DIR}"
method=GET; url=""; out=""; dump=""; upload=""; data=""; failmode=""; wfmt=""
plain=()
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -X) method="${args[i+1]}" ;;
    -o) out="${args[i+1]}" ;;
    -D) dump="${args[i+1]}" ;;
    -T) upload="${args[i+1]}" ;;
    -w) wfmt="${args[i+1]}" ;;
    --data-binary) data="${args[i+1]#@}" ;;
    --fail) failmode=fail ;;
    --fail-with-body) failmode=body ;;
    -H) case "${args[i+1]}" in
          @*) cat "${args[i+1]#@}" >>"${d}/headers" ;;
          *) plain+=("${args[i+1]}") ;;
        esac ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
if [[ -n "${upload}" && "${method}" == GET ]]; then method=PUT; fi
for h in ${plain[@]+"${plain[@]}"}; do printf '%s %s\n' "${method}" "${h}" >>"${d}/plain-headers"; done
printf '%s\0' "$@" >>"${d}/argv"
path="/${url#*://*/}"
printf '%s %s\n' "${method}" "${path}" >>"${d}/requests"
base=/api/v1/vms/work-vm/backups
code=200; body=''; payload=''; etag=''
mkdir -p "${d}/store"
problem() { code="$1"; body="{\"type\":\"about:blank\",\"title\":\"Refused\",\"status\":$1,\"code\":\"$2\"}"; }
id_of() { local rest="${path#"${base}/"}"; printf '%s' "${rest%%/*}"; }

if [[ -f "${d}/nofeature" ]]; then
  code=404
else
  case "${method} ${path}" in
    "POST ${base}")
      cp "${data}" "${d}/open.request"
      if [[ -f "${d}/open.problem" ]]; then
        read -r c p <"${d}/open.problem"; problem "${c}" "${p}"
      else
        code=201
        body="{\"id\":\"$(cat "${d}/next-id" 2>/dev/null || echo 0123456789abcdef0123456789abcdef)\",\"maxBytes\":1073741824}"
      fi ;;
    "PUT ${base}/"*/keys)
      cp "${data}" "${d}/store/$(id_of).keys"; code=204 ;;
    "PUT ${base}/"*/content)
      id="$(id_of)"
      if [[ -f "${d}/content.exit" ]]; then
        head -c 64 >/dev/null
        printf '000'
        exit "$(cat "${d}/content.exit")"
      fi
      cat >"${d}/store/${id}.age.partial"
      touch "${d}/content.complete"
      if [[ -f "${d}/content.problem" ]]; then
        rm -f "${d}/store/${id}.age.partial"
        read -r c p <"${d}/content.problem"; problem "${c}" "${p}"
      else
        mv "${d}/store/${id}.age.partial" "${d}/store/${id}.age"
        size="$(stat -c %s "${d}/store/${id}.age")"
        sha="$(sha256sum "${d}/store/${id}.age" | cut -d' ' -f1)"
        if [[ -f "${d}/content.badsha" ]]; then sha=0000000000000000000000000000000000000000000000000000000000000000; fi
        body="{\"id\":\"${id}\",\"sizeBytes\":${size},\"sha256\":\"${sha}\",\"createdAt\":\"2026-11-08T10:00:00Z\"}"
      fi ;;
    "DELETE ${base}/"*)
      rm -f "${d}/store/$(id_of)".*; code=204 ;;
    "POST ${base}/report")
      { cat "${data}"; printf '\n'; } >>"${d}/reports"; code=204 ;;
    "POST ${base}/"*/restored)
      code=204 ;;
    "GET ${base}")
      body='{"backups":[{"id":"0123456789abcdef0123456789abcdef","createdAt":"2026-11-08T10:00:00Z","trigger":"manual","sizeBytes":1,"sha256":"00","pinnedUntil":null}]}' ;;
    "GET ${base}/status")
      body='{"enabled":true,"lastAttemptAt":null,"lastSuccessAt":null,"lastFailure":null,"failingSince":null,"requestPending":false,"count":0,"totalBytes":0,"nextDueAt":null}' ;;
    "GET ${base}/"*/keys)
      if [[ -f "${d}/store/$(id_of).keys" ]]; then payload="${d}/store/$(id_of).keys"; else problem 404 backup-not-found; fi ;;
    "GET ${base}/"*/content)
      if [[ -f "${d}/store/$(id_of).age" ]]; then
        payload="${d}/store/$(id_of).age"
        etag="$(sha256sum "${payload}" | cut -d' ' -f1)"
        if [[ -f "${d}/download.badetag" ]]; then etag=1111111111111111111111111111111111111111111111111111111111111111; fi
      else
        problem 404 backup-not-found
      fi ;;
    *) code=404 ;;
  esac
fi

if [[ -n "${dump}" ]]; then
  {
    printf 'HTTP/1.1 %s Stub\r\n' "${code}"
    if [[ -n "${etag}" ]]; then printf 'ETag: "%s"\r\n' "${etag}"; fi
    printf '\r\n'
  } >"${dump}"
fi
failed=""
if [[ "${code}" -ge 400 && -n "${failmode}" ]]; then failed=1; fi
emit() { if [[ -n "${payload}" ]]; then cat "${payload}"; else printf '%s' "${body}"; fi; }
if [[ -z "${failed}" || "${failmode}" == body ]]; then
  if [[ -n "${out}" ]]; then emit >"${out}"; else emit; fi
fi
if [[ -n "${wfmt}" ]]; then printf '%s' "${code}"; fi
if [[ -n "${failed}" ]]; then exit 22; fi
exit 0
STUB
chmod +x "${stubs}"/*

reset_stub() {
  rm -rf "${state}"
  mkdir -p "${state}"
}
systemctl_log="${tmp}/systemctl.log"

# run_backup <config> [VAR=value ...] -- <args...>: the CLI in a clean environment.
run_backup() {
  local cfg="$1"; shift
  local -a extra=()
  while [[ $# -gt 0 && "$1" != -- ]]; do extra+=("$1"); shift; done
  shift
  env -i PATH="${stubs}:${SYS_PATH}" HOME="${tmp}/roothome" LANG=C.UTF-8 TMPDIR="${tmp}/tmpdir" \
    CONFIG_FILE="${cfg}" EXPORT_HOME="${home}" PROJECTS_STORE="${projects}" WORKSPACE_ROOT="${tmp}/workspace" \
    REPO_DIR="${tmp}/repo" CONSTRUCT_REPO_DIR="${tmp}/repo" T3_RESUME_HELPER="${tmp}/no-resume-helper" \
    VSCODE_SERVE_WEB_TOKEN_FILE="${sw_token}" T3CODE_TLS_DIR="${tmp}/absent-tls" \
    CONSTRUCT_BACKUP_RECIPIENTS_DIR="${recips}" CONSTRUCT_BACKUP_LOCK_FILE="${lock}" \
    CONSTRUCT_BACKUP_STAGE_PARENT="${tmp}/stage" \
    STUB_DIR="${state}" SYSTEMCTL_LOG="${systemctl_log}" \
    ${extra[@]+"${extra[@]}"} bash "${CLI}" backup "$@"
}

unwrap() { # <keys json> <private key> -> E
  jq -r --arg fp "$3" '.recipients[] | select(.fingerprint == $fp) | .wrapped' <<<"$1" | base64 -d \
    | openssl pkeyutl -decrypt -inkey "$2" -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
      -pkeyopt rsa_mgf1_md:sha256
}
stage_empty() { [[ -z "$(find "${tmp}/stage" -mindepth 1 -maxdepth 1 -print -quit)" ]]; }
member() { grep -qxF -- "$2" "$1"; }

# ── CLI basics ───────────────────────────────────────────────────────────────

run_backup "${local_cfg}" -- --help >"${tmp}/help.out" 2>&1
ok 'construct backup --help exits 0' test "$?" = 0
ok 'the help names every command' sh -c "grep -q 'create \[--trigger' '${tmp}/help.out' && grep -q 'recipients add --label' '${tmp}/help.out' && grep -q 'restore --stdin --expect-id' '${tmp}/help.out'"
ok 'the hidden commands stay out of the help' sh -c "! grep -Eq 'restore-extras|restored ID' '${tmp}/help.out'"
run_backup "${local_cfg}" -- frobnicate >/dev/null 2>&1
ok 'an unknown command exits 2' test "$?" = 2
run_backup "${local_cfg}" -- create --trigger nightly >/dev/null 2>&1
ok 'an unknown trigger exits 2' test "$?" = 2
run_backup "${local_cfg}" -- restore --stdin </dev/null >/dev/null 2>&1
ok 'restore --stdin without --expect-id exits 2' test "$?" = 2
run_backup "${local_cfg}" -- fetch --from-host not-an-id --out "${tmp}/x" </dev/null >/dev/null 2>&1
ok 'a malformed backup id exits 2' test "$?" = 2

# ── recipients ───────────────────────────────────────────────────────────────

run_backup "${local_cfg}" -- recipients list >"${tmp}/r0.out" 2>/dev/null
ok 'recipients list: [] before anything is enrolled' test "$(cat "${tmp}/r0.out")" = '[]'

run_backup "${local_cfg}" -- recipients add --label 'Desk PC-1' <"${tmp}/pc.pub" >"${tmp}/radd.out" 2>"${tmp}/radd.err"
ok 'recipients add exits 0' test "$?" = 0
fp="$(cat "${tmp}/radd.out")"
ok 'recipients add prints the fingerprint, and only that' test "${fp}" = "${fp_expected}"
ok 'the fingerprint is the SHA-256 of the SPKI DER, as openssl computes it' \
  test "${fp}" = "$(openssl pkey -pubin -in "${recips}/${fp}.pem" -outform DER | sha256sum | cut -d' ' -f1)"
ok 'the key is stored as <fp>.pem, mode 0644' test "$(stat -c %a "${recips}/${fp}.pem" 2>/dev/null)" = 644
ok 'the stored key is the same public key' cmp -s <(openssl pkey -pubin -in "${tmp}/pc.pub") "${recips}/${fp}.pem"
ok '<fp>.json carries the label and addedAt' \
  jq -e '.label == "Desk PC-1" and (.addedAt | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T"))' "${recips}/${fp}.json"
added_at="$(jq -r .addedAt "${recips}/${fp}.json")"

run_backup "${local_cfg}" -- recipients list >"${tmp}/r1.out" 2>/dev/null
ok 'recipients list: [{fingerprint,label,addedAt}]' \
  jq -e --arg fp "${fp}" --arg at "${added_at}" '. == [{fingerprint:$fp,label:"Desk PC-1",addedAt:$at}]' "${tmp}/r1.out"

sleep 1
run_backup "${local_cfg}" -- recipients add --label 'Renamed PC' <"${tmp}/pc.pub" >"${tmp}/radd2.out" 2>/dev/null
ok 'adding the same key again is idempotent: same fingerprint' test "$(cat "${tmp}/radd2.out")" = "${fp}"
run_backup "${local_cfg}" -- recipients list >"${tmp}/r2.out" 2>/dev/null
ok '... still one recipient, relabelled, addedAt kept' \
  jq -e --arg at "${added_at}" 'length == 1 and .[0].label == "Renamed PC" and .[0].addedAt == $at' "${tmp}/r2.out"

run_backup "${local_cfg}" -- recipients add --label short <"${tmp}/short.pub" >"${tmp}/rshort.out" 2>"${tmp}/rshort.err"
ok 'an RSA-2048 key is refused (exit 2)' test "$?" = 2
ok '... saying why' grep -q '3072' "${tmp}/rshort.err"
run_backup "${local_cfg}" -- recipients add --label ec <"${tmp}/ec.pub" >/dev/null 2>&1
ok 'an EC key is refused (exit 2)' test "$?" = 2
printf 'not a key\n' | run_backup "${local_cfg}" -- recipients add --label junk >/dev/null 2>&1
ok 'garbage on stdin is refused (exit 2)' test "$?" = 2
run_backup "${local_cfg}" -- recipients add --label 'bad/label' <"${tmp}/pc2.pub" >/dev/null 2>&1
ok 'a label outside the pattern is refused (exit 2)' test "$?" = 2
run_backup "${local_cfg}" -- recipients add <"${tmp}/pc2.pub" >/dev/null 2>&1
ok 'add without --label is refused (exit 2)' test "$?" = 2
ok 'refused keys leave nothing behind' test "$(find "${recips}" -type f | wc -l)" = 2

run_backup "${local_cfg}" -- recipients add --label second <"${tmp}/pc2.pub" >"${tmp}/fp2.out" 2>/dev/null
fp2="$(cat "${tmp}/fp2.out")"
run_backup "${local_cfg}" -- recipients remove "${fp2}" >/dev/null 2>&1
ok 'recipients remove exits 0' test "$?" = 0
ok '... and removes both files' test ! -e "${recips}/${fp2}.pem" -a ! -e "${recips}/${fp2}.json"
run_backup "${local_cfg}" -- recipients list >"${tmp}/r3.out" 2>/dev/null
ok '... the other recipient stays' jq -e --arg fp "${fp}" 'length == 1 and .[0].fingerprint == $fp' "${tmp}/r3.out"
run_backup "${local_cfg}" -- recipients remove not-a-fingerprint >/dev/null 2>&1
ok 'remove with a malformed fingerprint exits 2' test "$?" = 2

# Enrolment before provisioning installed jq: a PATH with everything but jq.
nojq="${tmp}/nojq-bin"
mkdir -p "${nojq}"
for dir in /usr/local/bin /usr/bin /bin /usr/sbin /sbin; do
  for exe in "${dir}"/*; do
    name="$(basename "${exe}")"
    [[ "${name}" == jq || -e "${nojq}/${name}" || ! -x "${exe}" ]] || ln -s "${exe}" "${nojq}/${name}"
  done
done
nojq_recips="${tmp}/recipients-nojq"
env -i PATH="${stubs}:${nojq}" HOME="${tmp}/roothome" LANG=C.UTF-8 TMPDIR="${tmp}/tmpdir" CONFIG_FILE="${local_cfg}" \
  CONSTRUCT_BACKUP_RECIPIENTS_DIR="${nojq_recips}" STUB_DIR="${state}" \
  bash "${CLI}" backup recipients add --label 'Early PC' <"${tmp}/pc.pub" >"${tmp}/nojq.out" 2>"${tmp}/nojq.err"
ok 'recipients add works without jq (the early enrolment)' test "$?" = 0
ok '... prints the fingerprint' test "$(cat "${tmp}/nojq.out")" = "${fp_expected}"
ok '... and writes the label file as JSON' jq -e '.label == "Early PC" and (.addedAt | test("^[0-9]{4}-"))' "${nojq_recips}/${fp_expected}.json"
first_added="$(jq -r .addedAt "${nojq_recips}/${fp_expected}.json")"
sleep 1
env -i PATH="${stubs}:${nojq}" HOME="${tmp}/roothome" LANG=C.UTF-8 TMPDIR="${tmp}/tmpdir" CONFIG_FILE="${local_cfg}" \
  CONSTRUCT_BACKUP_RECIPIENTS_DIR="${nojq_recips}" STUB_DIR="${state}" \
  bash "${CLI}" backup recipients add --label 'Early PC' <"${tmp}/pc.pub" >/dev/null 2>&1
ok '... and a repeat keeps the first addedAt without jq too' test "$(jq -r .addedAt "${nojq_recips}/${fp_expected}.json")" = "${first_added}"

# The host hears about an enrolment (it clears a no-recipients failure); a VM without a
# service does not try.
rm -f "${state}/reports"
run_backup "${host_cfg}" CONSTRUCT_BACKUP_RECIPIENTS_DIR="${tmp}/recipients-report" -- recipients add --label 'Desk PC-1' <"${tmp}/pc.pub" >"${tmp}/rrep.out" 2>/dev/null
ok 'recipients add on a VM with a host service reports the enrolment' \
  jq -e -s 'length == 1 and .[0].result == "enrolled" and .[0].reason == "recipient-added"' "${state}/reports"
ok '... and still prints only the fingerprint' test "$(cat "${tmp}/rrep.out")" = "${fp_expected}"
touch "${state}/nofeature"
run_backup "${host_cfg}" CONSTRUCT_BACKUP_RECIPIENTS_DIR="${tmp}/recipients-report2" -- recipients add --label 'Desk PC-1' <"${tmp}/pc.pub" >/dev/null 2>&1
ok '... an older host that refuses the report does not fail the enrolment' test "$?" = 0
rm -f "${state}/nofeature"

# ── create --stdout: the container ───────────────────────────────────────────

rm -f "${systemctl_log}"
reset_stub
run_backup "${local_cfg}" -- create --stdout >"${tmp}/c1.cbk" 2>"${tmp}/c1.err"
ok 'create --stdout exits 0' test "$?" = 0
ok 'create --stdout talks to no host' test ! -e "${state}/requests"
ok 'container line 1 is construct-backup/1' test "$(sed -n 1p "${tmp}/c1.cbk")" = 'construct-backup/1'
keys1="$(sed -n 2p "${tmp}/c1.cbk")"
id1="$(jq -r .backupId <<<"${keys1}")"
ok 'line 2 is the keys JSON on one line' \
  jq -e --arg fp "${fp}" '.format == "construct-backup-keys/1" and (.backupId | test("^[0-9a-f]{32}$"))
    and (.createdAt | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z$"))
    and (.recipients | length == 1) and .recipients[0].fingerprint == $fp
    and .recipients[0].label == "Renamed PC" and (.recipients[0].wrapped | length > 0)' <<<"${keys1}"
tail -n +3 "${tmp}/c1.cbk" >"${tmp}/c1.age"
ok 'the payload is an age file' sh -c "head -c 21 '${tmp}/c1.age' | grep -q 'age-encryption.org/v1'"
E1="$(unwrap "${keys1}" "${tmp}/pc.key" "${fp}")"
ok 'the PC unwraps E with RSA-OAEP-SHA256' sh -c "printf '%s' \"\$1\" | grep -Eqx 'AGE-SECRET-KEY-1[0-9A-Z]+'" _ "${E1}"
age -d -i <(printf '%s\n' "${E1}") "${tmp}/c1.age" >"${tmp}/c1.tar.zst" 2>/dev/null
ok 'E decrypts the payload' test -s "${tmp}/c1.tar.zst"
tar -tf "${tmp}/c1.tar.zst" >"${tmp}/c1.members" 2>/dev/null
ok 'the payload is a zstd tarball of the export layout' \
  sh -c "grep -qx './home/.claude/CLAUDE.md' '${tmp}/c1.members' && grep -qx './backup-info.json' '${tmp}/c1.members' && grep -qx './MANIFEST.txt' '${tmp}/c1.members'"
ok 'the enrolled recipients ride along' member "${tmp}/c1.members" "./etc/construct/backup-recipients/${fp}.pem"
ok '... with their metadata' member "${tmp}/c1.members" "./etc/construct/backup-recipients/${fp}.json"
ok 'the serve-web token rides along' member "${tmp}/c1.members" './etc/construct/vscode-serve-web.token'
tar -xOf "${tmp}/c1.tar.zst" ./backup-info.json >"${tmp}/c1.info" 2>/dev/null
ok 'backup-info.json gains format, backupId, trigger and instance' \
  jq -e --arg id "${id1}" '.format == "construct-backup/1" and .backupId == $id and .trigger == "manual" and .instance == "local-vm"' "${tmp}/c1.info"
ok 'the T3 store is a snapshot without -wal/-shm' \
  sh -c "grep -qx './home/.t3/userdata/statev2.sqlite' '${tmp}/c1.members' && ! grep -q 'statev2.sqlite-' '${tmp}/c1.members'"
mkdir -p "${tmp}/c1x"
tar -xf "${tmp}/c1.tar.zst" -C "${tmp}/c1x" ./home/.t3/userdata/statev2.sqlite 2>/dev/null
ok '... holding the rows that were still in the live -wal' \
  test "$(python3 -I -c 'import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute("select count(*) from threads").fetchone()[0])' "${tmp}/c1x/home/.t3/userdata/statev2.sqlite" 2>/dev/null)" = 2
ok 'a scheduled/manual backup never stops T3 Code' sh -c "! grep -q '^stop' '${systemctl_log}' 2>/dev/null"
ok 'the stage is removed' stage_empty
ok 'E was never written to a file' sh -c "! grep -rqF -- \"\$1\" '${tmp}'" _ "${E1}"

run_backup "${local_cfg}" -- create --stdout --trigger pre-reinstall >"${tmp}/c2.cbk" 2>/dev/null
keys2="$(sed -n 2p "${tmp}/c2.cbk")"
ok 'each backup has its own id' test "$(jq -r .backupId <<<"${keys2}")" != "${id1}"
E2="$(unwrap "${keys2}" "${tmp}/pc.key" "${fp}")"
ok '... and its own key E' test -n "${E2}" -a "${E2}" != "${E1}"

# ── restore --stdin: the round trip ──────────────────────────────────────────

rhome="${tmp}/restored-home"
rprojects="${tmp}/restored-projects"
rrecips="${tmp}/restored-recipients"
rtoken="${tmp}/restored-token"
mkdir -p "${rhome}"
restore_env=(EXPORT_HOME="${rhome}" PROJECTS_STORE="${rprojects}" CONSTRUCT_BACKUP_RECIPIENTS_DIR="${rrecips}"
  VSCODE_SERVE_WEB_TOKEN_FILE="${rtoken}")
rm -f "${systemctl_log}"
{ printf '%s\n' "${E1}"; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >"${tmp}/rs.out" 2>"${tmp}/rs.err"
ok 'restore --stdin exits 0' test "$?" = 0
ok 'restore --stdin prints nothing on stdout' test ! -s "${tmp}/rs.out"
ok 'the saved home comes back' cmp -s "${home}/.claude/CLAUDE.md" "${rhome}/.claude/CLAUDE.md"
ok 'git credentials come back, mode 0600' \
  sh -c "cmp -s '${home}/.git-credentials' '${rhome}/.git-credentials' && test \"\$(stat -c %a '${rhome}/.git-credentials')\" = 600"
ok 'the T3 store comes back with every row' \
  test "$(python3 -I -c 'import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute("select count(*) from threads").fetchone()[0])' "${rhome}/.t3/userdata/statev2.sqlite" 2>/dev/null)" = 2
ok 'extras: missing project profiles are restored' cmp -s "${projects}/demo.json" "${rprojects}/demo.json"
ok 'extras: the serve-web token is restored, mode 0600' \
  sh -c "cmp -s '${sw_token}' '${rtoken}' && test \"\$(stat -c %a '${rtoken}')\" = 600"
ok 'extras: ... and serve-web is restarted if it runs' grep -qx 'try-restart code-serve-web.service' "${systemctl_log}"
ok 'extras: the recipients are restored' \
  sh -c "cmp -s '${recips}/${fp}.pem' '${rrecips}/${fp}.pem' && jq -e '.label == \"Renamed PC\"' '${rrecips}/${fp}.json' >/dev/null"
ok 'the restore archive is removed' stage_empty

# Additive: existing profiles and recipients are never overwritten.
printf '{"name":"demo","local":true}\n' >"${rprojects}/demo.json"
printf '{"label":"Kept","addedAt":"2026-01-01T00:00:00Z"}\n' >"${rrecips}/${fp}.json"
rm -f "${systemctl_log}"
{ printf '%s\n' "${E1}"; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >/dev/null 2>&1
ok 'extras: an existing project profile is kept' jq -e '.local == true' "${rprojects}/demo.json"
ok 'extras: an existing recipient is kept' jq -e '.label == "Kept"' "${rrecips}/${fp}.json"
ok 'extras: an unchanged serve-web token restarts nothing' sh -c "! grep -q try-restart '${systemctl_log}' 2>/dev/null"

{ printf '%s\n' "${E1}"; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${OTHER_ID}" >/dev/null 2>"${tmp}/wrongid.err"
ok 'a backup with another id is refused (exit 8)' test "$?" = 8
ok '... saying so' grep -q 'is not backup' "${tmp}/wrongid.err"
{ printf '%s\n' "${E2}"; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >/dev/null 2>&1
ok 'the wrong key is refused (exit 8)' test "$?" = 8
{ printf 'not-a-key\n'; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >/dev/null 2>&1
ok 'a first line that is no age identity is refused (exit 8)' test "$?" = 8
{ printf '%s\n' "${E1}"; head -c 400 "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >/dev/null 2>&1
ok 'a truncated payload is refused (exit 8)' test "$?" = 8
ok 'refused restores leave nothing behind' stage_empty

# restore-extras alone (the provisioning path that ran restore-config.sh itself).
rm -rf "${rprojects}" "${rrecips}" "${rtoken}"
run_backup "${local_cfg}" "${restore_env[@]}" -- restore-extras "${tmp}/c1.tar.zst" >"${tmp}/rx.out" 2>/dev/null
ok 'restore-extras exits 0' test "$?" = 0
ok 'restore-extras restores profiles, token and recipients' \
  test -f "${rprojects}/demo.json" -a -f "${rtoken}" -a -f "${rrecips}/${fp}.pem"

# ── create: the host upload path ─────────────────────────────────────────────

reset_stub
rm -f "${systemctl_log}"
run_backup "${host_cfg}" -- create >"${tmp}/h1.out" 2>"${tmp}/h1.err"
ok 'create (host) exits 0' test "$?" = 0
ok 'create prints exactly one JSON line' sh -c "test \"\$(wc -l <'${tmp}/h1.out')\" = 1 && jq -e . '${tmp}/h1.out' >/dev/null"
stored="${state}/store/${BACKUP_ID}.age"
ok 'the result names the host id, size and sha256 of what the host stored' \
  jq -e --arg id "${BACKUP_ID}" --argjson size "$(stat -c %s "${stored}" 2>/dev/null || echo -1)" \
    --arg sha "$(sha256sum "${stored}" 2>/dev/null | cut -d' ' -f1)" \
    '.id == $id and .sizeBytes == $size and .sha256 == $sha and .trigger == "manual"' "${tmp}/h1.out"
ok 'gitCredentialHosts lists the credential hosts, nothing else' \
  jq -e '.gitCredentialHosts == ["code.example.org","git.example.com"]' "${tmp}/h1.out"
ok 'the result carries no secret' sh -c "! grep -Eq 'sentinel|secret|alice' '${tmp}/h1.out'"
ok 'requests: open, keys, content -- in that order' test "$(cut -d' ' -f1-2 "${state}/requests" | tr '\n' '|')" = \
  "POST /api/v1/vms/work-vm/backups|PUT /api/v1/vms/work-vm/backups/${BACKUP_ID}/keys|PUT /api/v1/vms/work-vm/backups/${BACKUP_ID}/content|"
ok 'the session opens with {trigger, estimatedBytes}' \
  jq -e 'keys == ["estimatedBytes","trigger"] and .trigger == "manual" and (.estimatedBytes | type == "number" and . > 0)' "${state}/open.request"
ok 'the keys file is stored with this backup id' \
  jq -e --arg id "${BACKUP_ID}" --arg fp "${fp}" '.format == "construct-backup-keys/1" and .backupId == $id and .recipients[0].fingerprint == $fp' \
  "${state}/store/${BACKUP_ID}.keys"
ok 'the payload streams as an octet-stream PUT from stdin (-T -)' \
  sh -c "tr '\\0' '\\n' <'${state}/argv' | grep -qx -- '-T' && grep -q 'PUT Content-Type: application/octet-stream' '${state}/plain-headers'"
ok 'the token travels in the header file only' \
  sh -c "grep -qx 'Authorization: VmToken sentinel-vm-token' '${state}/headers' && ! tr '\\0' '\\n' <'${state}/argv' | grep -q sentinel-vm-token"
ok 'the service CA is used' sh -c "tr '\\0' '\\n' <'${state}/argv' | grep -qx -- '${ca_file}'"
hkeys="$(cat "${state}/store/${BACKUP_ID}.keys")"
EH="$(unwrap "${hkeys}" "${tmp}/pc.key" "${fp}")"
ok 'the uploaded payload decrypts with the unwrapped key' \
  bash -c "age -d -i <(printf '%s\n' \"\$1\") '${stored}' | zstd -dc | tar -xOf - ./backup-info.json | jq -e --arg id '${BACKUP_ID}' '.backupId == \$id and .instance == \"work-vm\"' >/dev/null" _ "${EH}"
ok 'E was never written to a file or argv' sh -c "! grep -rqF -- \"\$1\" '${tmp}'" _ "${EH}"
ok 'the stage is removed after the upload' stage_empty
ok 'no report on success' test ! -e "${state}/reports"

# download: the host's keys file and payload as a container.
run_backup "${host_cfg}" -- download "${BACKUP_ID}" >"${tmp}/dl.cbk" 2>"${tmp}/dl.err"
ok 'download exits 0' test "$?" = 0
ok 'download: line 1 is construct-backup/1' test "$(sed -n 1p "${tmp}/dl.cbk")" = 'construct-backup/1'
ok 'download: line 2 is the stored keys file' test "$(sed -n 2p "${tmp}/dl.cbk")" = "$(jq -c . <<<"${hkeys}")"
ok 'download: then the stored payload, byte for byte' cmp -s <(tail -n +3 "${tmp}/dl.cbk") "${stored}"
touch "${state}/download.badetag"
run_backup "${host_cfg}" -- download "${BACKUP_ID}" >/dev/null 2>&1
ok 'download: a payload that does not match its ETag exits 8' test "$?" = 8
rm -f "${state}/download.badetag"
run_backup "${host_cfg}" -- download "${OTHER_ID}" >/dev/null 2>"${tmp}/dl404.err"
ok 'download of an unknown backup exits 6' test "$?" = 6

# fetch: download + decrypt into a .tar.zst.
printf '%s\n' "${EH}" | run_backup "${host_cfg}" -- fetch --from-host "${BACKUP_ID}" --out "${tmp}/fetched.tar.zst" >/dev/null 2>"${tmp}/fetch.err"
ok 'fetch exits 0' test "$?" = 0
ok 'fetch writes the archive with mode 0600' test "$(stat -c %a "${tmp}/fetched.tar.zst" 2>/dev/null)" = 600
ok 'fetch writes the decrypted .tar.zst' \
  sh -c "tar -xOf '${tmp}/fetched.tar.zst' ./backup-info.json | jq -e '.backupId == \"${BACKUP_ID}\"' >/dev/null"
printf '%s\n' "${E1}" | run_backup "${host_cfg}" -- fetch --from-host "${BACKUP_ID}" --out "${tmp}/fetch-bad.tar.zst" >/dev/null 2>&1
ok 'fetch with the wrong key exits 8' test "$?" = 8
ok '... and leaves no file' test ! -e "${tmp}/fetch-bad.tar.zst"
run_backup "${host_cfg}" -- fetch --from-host "${BACKUP_ID}" --out "${tmp}/fetch-nokey.tar.zst" </dev/null >/dev/null 2>&1
ok 'fetch without a key on stdin exits 2' test "$?" = 2

# verify: download + decrypt + unpack without writing the backup anywhere.
printf '%s\n' "${EH}" | run_backup "${host_cfg}" -- verify --from-host "${BACKUP_ID}" >"${tmp}/verify.out" 2>"${tmp}/verify.err"
ok 'verify exits 0 for a backup the key opens' test "$?" = 0
ok '... prints nothing on stdout' test ! -s "${tmp}/verify.out"
ok '... and leaves nothing in the stage' stage_empty
printf '%s\n' "${E1}" | run_backup "${host_cfg}" -- verify --from-host "${BACKUP_ID}" >/dev/null 2>&1
ok 'verify with the wrong key exits 8' test "$?" = 8
printf '%s\n' "${EH}" | run_backup "${host_cfg}" -- verify --from-host "${OTHER_ID}" >/dev/null 2>&1
ok 'verify of an unknown backup exits 6' test "$?" = 6
run_backup "${host_cfg}" -- verify --from-host "${BACKUP_ID}" </dev/null >/dev/null 2>&1
ok 'verify without a key on stdin exits 2' test "$?" = 2

# restore --from-host: fetch + restore + extras + restored.
rm -rf "${rhome}" "${rprojects}" "${rrecips}" "${rtoken}"
mkdir -p "${rhome}"
: >"${state}/requests"
printf '%s\n' "${EH}" | run_backup "${host_cfg}" "${restore_env[@]}" -- restore --from-host "${BACKUP_ID}" >/dev/null 2>"${tmp}/rh.err"
ok 'restore --from-host exits 0' test "$?" = 0
ok 'restore --from-host restores the home' cmp -s "${home}/.claude/CLAUDE.md" "${rhome}/.claude/CLAUDE.md"
ok 'restore --from-host runs the extras' test -f "${rprojects}/demo.json" -a -f "${rrecips}/${fp}.pem"
ok 'restore --from-host tells the host (POST …/restored)' \
  grep -qx "POST /api/v1/vms/work-vm/backups/${BACKUP_ID}/restored" "${state}/requests"
ok 'restore --from-host leaves no archive behind' stage_empty

: >"${state}/requests"
run_backup "${host_cfg}" -- restored "${BACKUP_ID}" >/dev/null 2>&1
ok 'restored ID exits 0' test "$?" = 0
ok '... after POST …/restored' grep -qx "POST /api/v1/vms/work-vm/backups/${BACKUP_ID}/restored" "${state}/requests"

run_backup "${host_cfg}" -- list >"${tmp}/list.out" 2>/dev/null
ok 'list prints the host JSON' jq -e '.backups[0].id == "0123456789abcdef0123456789abcdef"' "${tmp}/list.out"
run_backup "${host_cfg}" -- status >"${tmp}/status.out" 2>/dev/null
ok 'status prints the host JSON' jq -e '.enabled == true and has("nextDueAt")' "${tmp}/status.out"

# The pre-reinstall export is the reinstall one: it stops T3 Code across the copy.
reset_stub
rm -f "${systemctl_log}"
run_backup "${host_cfg}" T3_ACTIVE=1 -- create --trigger pre-reinstall >"${tmp}/pre.out" 2>/dev/null
ok 'create --trigger pre-reinstall exits 0' test "$?" = 0
ok '... opens the session as pre-reinstall' jq -e '.trigger == "pre-reinstall"' "${state}/open.request"
ok '... stops and restarts T3 Code around the copy' \
  sh -c "grep -qx 'stop t3code-serve' '${systemctl_log}' && grep -qx 'start t3code-serve' '${systemctl_log}'"
reset_stub
rm -f "${systemctl_log}"
run_backup "${host_cfg}" T3_ACTIVE=1 -- create --trigger scheduled >/dev/null 2>&1
ok 'a scheduled backup leaves a running T3 Code alone' sh -c "! grep -Eq '^(stop|start) ' '${systemctl_log}' 2>/dev/null"

# ── create: failures ─────────────────────────────────────────────────────────

report_is() { # <result> <reason> [detail regex]
  [[ -s "${state}/reports" ]] || return 1
  jq -e --arg r "$1" --arg reason "$2" --arg d "${3:-.*}" \
    '.result == $r and .reason == $reason and (.detail | test($d)) and (.trigger | type == "string")' \
    <(tail -n 1 "${state}/reports") >/dev/null
}
deleted() { grep -qx "DELETE /api/v1/vms/work-vm/backups/${BACKUP_ID}" "${state}/requests"; }

reset_stub
mkdir -p "${tmp}/norecips"
run_backup "${host_cfg}" CONSTRUCT_BACKUP_RECIPIENTS_DIR="${tmp}/norecips" -- create --trigger scheduled >"${tmp}/nr.out" 2>/dev/null
ok 'no recipients: exit 3' test "$?" = 3
ok 'no recipients: reported as skipped/no-recipients' report_is skipped no-recipients
ok 'no recipients: no session is opened' sh -c "! grep -q '^POST /api/v1/vms/work-vm/backups\$' '${state}/requests'"
ok 'no recipients: nothing on stdout' test ! -s "${tmp}/nr.out"
run_backup "${local_cfg}" CONSTRUCT_BACKUP_RECIPIENTS_DIR="${tmp}/norecips" -- create --stdout >/dev/null 2>&1
ok 'no recipients: create --stdout exits 3 too' test "$?" = 3

run_backup "${local_cfg}" -- create >/dev/null 2>"${tmp}/nosvc.err"
ok 'no host service: create exits 4' test "$?" = 4
run_backup "${local_cfg}" -- status >/dev/null 2>&1
ok 'no host service: status exits 4' test "$?" = 4

reset_stub
touch "${state}/nofeature"
run_backup "${host_cfg}" -- create >/dev/null 2>"${tmp}/nofeat.err"
ok 'a host without the feature: create exits 4' test "$?" = 4
ok '... and reports nothing' test ! -e "${state}/reports"
ok '... and leaves no stage' stage_empty
run_backup "${host_cfg}" -- list >/dev/null 2>&1
ok 'a host without the feature: list exits 4' test "$?" = 4
printf '%s\n' "${EH}" | run_backup "${host_cfg}" -- fetch --from-host "${BACKUP_ID}" --out "${tmp}/nf.tar.zst" >/dev/null 2>&1
ok 'a host without the feature: fetch exits 4' test "$?" = 4

reset_stub
printf '507 host-disk-space\n' >"${state}/open.problem"
run_backup "${host_cfg}" -- create --trigger scheduled >/dev/null 2>"${tmp}/refused.err"
ok 'the host refuses the session: exit 6' test "$?" = 6
ok '... reported as host-refused with the problem code' report_is skipped host-refused '^host-disk-space$'
ok '... and the stage is removed' stage_empty

reset_stub
printf '409 backup-in-progress\n' >"${state}/open.problem"
run_backup "${host_cfg}" -- create >/dev/null 2>&1
ok 'a backup already in progress on the host: exit 6' test "$?" = 6

reset_stub
printf '56\n' >"${state}/content.exit"
run_backup "${host_cfg}" -- create >"${tmp}/upfail.out" 2>"${tmp}/upfail.err"
ok 'the upload breaks off: exit 7' test "$?" = 7
ok '... the open session is deleted' deleted
ok '... reported as failed/upload-failed' report_is failed upload-failed
ok '... nothing on stdout' test ! -s "${tmp}/upfail.out"
ok '... and the stage is removed' stage_empty

reset_stub
printf '413 backup-too-large\n' >"${state}/content.problem"
run_backup "${host_cfg}" -- create >/dev/null 2>&1
ok 'the host refuses the upload: exit 6' test "$?" = 6
ok '... the session is deleted' deleted
ok '... reported as host-refused/backup-too-large' report_is failed host-refused '^backup-too-large$'

reset_stub
touch "${state}/content.badsha"
run_backup "${host_cfg}" -- create >"${tmp}/badsha.out" 2>/dev/null
ok 'the host stored something else (SHA-256 mismatch): exit 8' test "$?" = 8
ok '... the backup is deleted' deleted
ok '... reported as verify-failed' report_is failed verify-failed
ok '... nothing on stdout' test ! -s "${tmp}/badsha.out"

# A producer that breaks off mid-stream must not leave a clean-looking upload.
mkdir -p "${tmp}/badtar"
cat >"${tmp}/badtar/tar" <<'STUB'
#!/usr/bin/env bash
if [[ "$*" == *"-cf - ."* ]]; then
  /usr/bin/tar "$@" | head -c 4096
  exit 2
fi
exec /usr/bin/tar "$@"
STUB
chmod +x "${tmp}/badtar/tar"
reset_stub
run_backup "${host_cfg}" PATH="${tmp}/badtar:${stubs}:${SYS_PATH}" -- create >/dev/null 2>"${tmp}/badtar.err"
ok 'tar fails mid-stream: exit 10' test "$?" = 10
ok '... the upload is aborted, never completed' test ! -e "${state}/content.complete"
ok '... the session is deleted' deleted
ok '... reported as export-failed' report_is failed export-failed

# A failing export (the export script is found next to the backup script).
mkdir -p "${tmp}/failbin"
cp "${ROOT}/bin/construct-backup.sh" "${tmp}/failbin/construct-backup.sh"
cp "${ROOT}/bin/construct" "${tmp}/failbin/construct"
printf '#!/usr/bin/env bash\nmkdir -p "${STAGE_DIR}/home"; echo partial >"${STAGE_DIR}/home/x"; exit 1\n' >"${tmp}/failbin/export-config.sh"
reset_stub
CLI="${tmp}/failbin/construct" run_backup "${host_cfg}" -- create >/dev/null 2>"${tmp}/expfail.err"
ok 'the export fails: exit 10' test "$?" = 10
ok '... reported as export-failed' report_is failed export-failed
ok '... before any session was opened' sh -c "! grep -q '^POST /api/v1/vms/work-vm/backups\$' '${state}/requests'"
ok '... and the stage is removed' stage_empty

# ── space check ──────────────────────────────────────────────────────────────

mkdir -p "${tmp}/dfstub"
cat >"${tmp}/dfstub/df" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${DF_LOG}"
printf '    Avail     Size\n%s %s\n' "${DF_AVAIL}" "${DF_SIZE}"
STUB
chmod +x "${tmp}/dfstub/df"
df_env=(PATH="${tmp}/dfstub:${stubs}:${SYS_PATH}" DF_LOG="${tmp}/df.log")
GiB=$((1024 * 1024 * 1024))

run_backup "${local_cfg}" "${df_env[@]}" DF_AVAIL=$((100 * GiB)) DF_SIZE=$((100 * GiB)) CONSTRUCT_BACKUP_REFLINK=yes \
  -- estimate >"${tmp}/est1.out" 2>/dev/null
ok 'estimate prints {needBytes,freeBytes,reserveBytes,reflink,ok}' \
  jq -e 'keys == ["freeBytes","needBytes","ok","reflink","reserveBytes"]' "${tmp}/est1.out"
ok 'estimate: reserve = max(2 GiB, 5 % of the file system)' jq -e ".reserveBytes == $((5 * GiB)) and .ok == true and .reflink == true" "${tmp}/est1.out"
ok 'estimate: the free space is read on the staging file system' grep -q -- "${tmp}/stage" "${tmp}/df.log"
db_bytes=$(( $(stat -c %s "${home}/.t3/userdata/statev2.sqlite") + $(stat -c %s "${home}/.t3/userdata/statev2.sqlite-wal") ))
ok 'estimate with reflinks: SQLite snapshots + 64 MiB' jq -e ".needBytes == $((db_bytes + 64 * 1024 * 1024))" "${tmp}/est1.out"
head -c 3000000 /dev/zero >"${home}/.codex/sessions-blob"
mkdir -p "${home}/.codex/sessions"
mv "${home}/.codex/sessions-blob" "${home}/.codex/sessions/blob"
run_backup "${local_cfg}" "${df_env[@]}" DF_AVAIL=$((100 * GiB)) DF_SIZE=$((10 * GiB)) CONSTRUCT_BACKUP_REFLINK=no \
  -- estimate >"${tmp}/est2.out" 2>/dev/null
ok 'estimate without reflinks: the staged paths count too' jq -e ".needBytes >= $((db_bytes + 3000000 + 64 * 1024 * 1024)) and .reflink == false" "${tmp}/est2.out"
ok 'estimate: a small file system reserves 2 GiB' jq -e ".reserveBytes == $((2 * GiB))" "${tmp}/est2.out"
rm -rf "${home}/.codex/sessions"

reset_stub
run_backup "${host_cfg}" "${df_env[@]}" DF_AVAIL=$((2 * GiB)) DF_SIZE=$((20 * GiB)) CONSTRUCT_BACKUP_REFLINK=yes \
  -- create --trigger scheduled >"${tmp}/nospace.out" 2>"${tmp}/nospace.err"
ok 'too little space: create exits 5' test "$?" = 5
ok '... reported as skipped/vm-disk-space with need, free and reserve' \
  report_is skipped vm-disk-space '^need=[0-9]+ free=[0-9]+ reserve=[0-9]+$'
ok '... before staging or opening anything' \
  sh -c "! grep -q '^POST /api/v1/vms/work-vm/backups\$' '${state}/requests' && test -z \"\$(find '${tmp}/stage' -mindepth 1 -print -quit)\""
run_backup "${local_cfg}" "${df_env[@]}" DF_AVAIL=$((2 * GiB)) DF_SIZE=$((20 * GiB)) CONSTRUCT_BACKUP_REFLINK=yes \
  -- create --stdout >"${tmp}/nospace.cbk" 2>/dev/null
ok 'too little space: create --stdout exits 5' test "$?" = 5
ok '... with nothing on stdout' test ! -s "${tmp}/nospace.cbk"

# ── one run at a time ────────────────────────────────────────────────────────

( exec 7>>"${lock}"; flock 7; exec sleep 30 ) &
holder_pid=$!
for _ in $(seq 1 50); do flock -n "${lock}" true 2>/dev/null || break; sleep 0.1; done
start_s="$(date +%s)"
run_backup "${local_cfg}" -- create --stdout >"${tmp}/locked.cbk" 2>"${tmp}/locked.err"
locked_rc=$?
ok 'a second run exits 9 while the lock is held' test "${locked_rc}" = 9
ok '... at once' test $(( $(date +%s) - start_s )) -lt 10
ok '... saying why' grep -q 'another backup or restore is running' "${tmp}/locked.err"
{ printf '%s\n' "${E1}"; cat "${tmp}/c1.age"; } \
  | run_backup "${local_cfg}" "${restore_env[@]}" -- restore --stdin --expect-id "${id1}" >/dev/null 2>&1
ok 'a restore waits for no one either: exit 9' test "$?" = 9
kill "${holder_pid}" 2>/dev/null
wait "${holder_pid}" 2>/dev/null
holder_pid=""
ok 'nothing was staged while locked out' stage_empty

# ── static checks ────────────────────────────────────────────────────────────

ok 'construct-backup.sh parses' bash -n "${BACKUP}"
if command -v shellcheck >/dev/null 2>&1; then
  ok 'shellcheck: construct-backup.sh is clean' shellcheck -x "${BACKUP}"
fi

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
