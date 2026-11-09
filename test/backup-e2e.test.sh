#!/usr/bin/env bash
# END-TO-END test for host-stored config backups (docs/plans/host-config-backups.md):
# the real guest CLI (bin/construct backup) and the real PowerShell key code
# (lib/AgentVm.Backup.ps1) against a REAL `constructd` in fake mode.
#
#   bash test/backup-e2e.test.sh
#
# The unit suites stub the other side of each contract (test/construct-backup.test.sh stubs
# the host, the service tests stub the guest, test/backup-keys.test.ps1 stubs both). This one
# proves the three agree: routes and JSON shapes, the heartbeat's backupDue, the keys file the
# guest writes and the PC unwraps, the container format, pinning, settings and rotation.
#
# Everything runs in a scratch directory: a scratch home is exported, recipients, lock and
# stage live under it, and `systemctl` is a stub, so the live system is never touched. The
# service listens on plain http on 127.0.0.1 with in-memory persistence.
#
# SKIPS cleanly, with a reason, when dotnet, pwsh, age, zstd, openssl or jq is missing.

# The PowerShell snippets are single-quoted on purpose.
# shellcheck disable=SC2016

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ROOT
SERVICE_PROJ="${ROOT}/service/src/Constructd.Api"
tmp="$(mktemp -d)"

pass=0
fail=0
skipped=0
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
skip_all() {
  skipped=$((skipped + 1))
  printf '  SKIP  backup end-to-end -- %s\n' "$1"
  printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
  exit 0
}

SVC_PID=""
cleanup() {
  if [[ -n "${SVC_PID}" ]]; then kill "${SVC_PID}" 2>/dev/null || true; wait "${SVC_PID}" 2>/dev/null || true; fi
  rm -rf "${tmp}"
}
trap cleanup EXIT

printf '\n=== Host config backups end-to-end (fake constructd) ===\n'

for tool in dotnet pwsh age age-keygen zstd openssl jq curl node; do
  command -v "${tool}" >/dev/null 2>&1 || skip_all "${tool} is not installed"
done
[[ -d "${SERVICE_PROJ}" ]] || skip_all "service/src/Constructd.Api is not present in this checkout"

free_port() { node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close();});'; }
PORT="${CONSTRUCT_E2E_PORT:-$(free_port)}"
# The vault relay listener gets its own fixed port: left at 0, the service takes the first
# plain http://127.0.0.1 address it is bound to for the relay, which is this test's API
# listener, and the relay filter then answers every API route with 404.
RELAY_PORT="$(free_port)"
BASE="http://127.0.0.1:${PORT}"
ADMIN_TOKEN="e2e-$(date +%s)-$$"
VM="bk-vm"

dotnet build "${SERVICE_PROJ}" --nologo >"${tmp}/build.log" 2>&1 || { cat "${tmp}/build.log"; exit 1; }

printf '  starting the fake service on %s ...\n' "${BASE}"
(
  cd "${SERVICE_PROJ}" || exit 1
  Constructd__ListenUrl="${BASE}" \
  Constructd__BootstrapAdmin="e2e-admin" \
  Constructd__BootstrapAdminToken="${ADMIN_TOKEN}" \
  Constructd__PublicHost="127.0.0.1" \
  Constructd__Persistence="Memory" \
  Constructd__Idle__SchedulerEnabled="false" \
  Constructd__Backups__RootDir="${tmp}/host-backups" \
  Constructd__Https__VaultRelayPort="${RELAY_PORT}" \
  dotnet "${SERVICE_PROJ}/bin/Debug/net10.0/Constructd.Api.dll" --fake
) >"${tmp}/service.log" 2>&1 &
SVC_PID=$!

printf 'Authorization: Bearer %s\n' "${ADMIN_TOKEN}" >"${tmp}/admin-header"
chmod 600 "${tmp}/admin-header"
up=0
for _ in $(seq 1 90); do
  if curl -fsS -H "@${tmp}/admin-header" "${BASE}/api/v1/whoami" >"${tmp}/whoami" 2>&1; then up=1; break; fi
  kill -0 "${SVC_PID}" 2>/dev/null || break
  sleep 1
done
if [[ "${up}" != 1 ]]; then
  printf '  FAIL  fake service did not start on %s\n' "${BASE}"
  sed 's/^/    probe | /' "${tmp}/whoami" 2>/dev/null
  tail -20 "${tmp}/service.log" | sed 's/^/    | /'
  exit 1
fi
printf '  service is up\n\n'

# admin <method> <path> [json body] -> body on stdout, status in ${tmp}/status
admin() {
  local args=(-sS -o "${tmp}/body" -w '%{http_code}' -H "@${tmp}/admin-header" -X "$1")
  if [[ $# -ge 3 ]]; then args+=(-H 'Content-Type: application/json' --data-binary "$3"); fi
  curl "${args[@]}" "${BASE}/api/v1$2" >"${tmp}/status"
  cat "${tmp}/body"
}
# heartbeat <busy> -> reply body (empty for 204), status in ${tmp}/status
heartbeat() {
  curl -sS -o "${tmp}/body" -w '%{http_code}' -H "@${tmp}/vm-header" -H 'Content-Type: application/json' \
    -X POST --data-binary "{\"busy\":$1,\"reasons\":[]}" "${BASE}/api/v1/vms/${VM}/activity" >"${tmp}/status"
  cat "${tmp}/body"
}
status_is() { [[ "$(cat "${tmp}/status")" == "$1" ]]; }

ok 'the host advertises config-backups' sh -c "curl -fsS -H '@${tmp}/admin-header' '${BASE}/api/v1/health' | jq -e '.apiFeatures | index(\"config-backups\")' >/dev/null"

# ── a VM and its one-time token ──────────────────────────────────────────────
job="$(admin POST /vms "{\"name\":\"${VM}\",\"cpu\":2,\"ramGb\":4,\"diskGb\":20}" | jq -r '.jobId // empty')"
vm_token=""
for _ in $(seq 1 120); do
  body="$(admin GET "/jobs/${job}")"
  state="$(jq -r '(.state // "") | ascii_downcase' <<<"${body}")"
  if [[ "${state}" == succeeded ]]; then
    vm_token="$(jq -r '.result.vmToken // empty' <<<"${body}")"
    break
  fi
  [[ "${state}" == failed || "${state}" == cancelled ]] && break
  sleep 1
done
ok 'creating the VM yields a VM token' test -n "${vm_token}"
printf 'Authorization: VmToken %s\n' "${vm_token}" >"${tmp}/vm-header"
chmod 600 "${tmp}/vm-header"
printf '%s' "${vm_token}" >"${tmp}/vm-token"
chmod 600 "${tmp}/vm-token"

# ── the guest side, sandboxed ────────────────────────────────────────────────
home="${tmp}/home"
mkdir -p "${home}/.claude" "${home}/.secrets" "${tmp}/projects" "${tmp}/workspace" "${tmp}/recipients" \
  "${tmp}/stage" "${tmp}/roothome" "${tmp}/tmpdir" "${tmp}/stubs"
printf '# e2e instructions\n' >"${home}/.claude/CLAUDE.md"
printf 'e2e-secret\n' >"${home}/.secrets/token"
printf 'https://user:pw@git.example.com\n' >"${home}/.git-credentials"
cat >"${tmp}/config.env" <<EOF
CONSTRUCT_SERVICE_URL='${BASE}'
CONSTRUCT_INSTANCE_NAME='${VM}'
CONSTRUCT_VM_TOKEN_FILE='${tmp}/vm-token'
EOF
# systemctl stub: every unit is inactive and nothing is started or stopped.
printf '#!/bin/sh\nexit 3\n' >"${tmp}/stubs/systemctl"
chmod +x "${tmp}/stubs/systemctl"

guest() {
  env -i PATH="${tmp}/stubs:${PATH}" HOME="${tmp}/roothome" LANG=C.UTF-8 TMPDIR="${tmp}/tmpdir" \
    CONFIG_FILE="${tmp}/config.env" EXPORT_HOME="${home}" PROJECTS_STORE="${tmp}/projects" \
    WORKSPACE_ROOT="${tmp}/workspace" REPO_DIR="${ROOT}" CONSTRUCT_REPO_DIR="${ROOT}" \
    T3_RESUME_HELPER="${tmp}/no-resume-helper" VSCODE_SERVE_WEB_TOKEN_FILE="${tmp}/no-serve-web-token" \
    T3CODE_TLS_DIR="${tmp}/no-tls" CONSTRUCT_SYSTEMCTL="${tmp}/stubs/systemctl" \
    CONSTRUCT_BACKUP_RECIPIENTS_DIR="${tmp}/recipients" CONSTRUCT_BACKUP_LOCK_FILE="${tmp}/backup.lock" \
    CONSTRUCT_BACKUP_STAGE_PARENT="${tmp}/stage" \
    bash "${ROOT}/bin/construct" backup "$@"
}

# ── the PC side: lib/AgentVm.Backup.ps1 with a reversible stand-in for DPAPI ─
pc() {
  KEYDIR="${tmp}/pckey" BASE="${BASE}" TOKEN="${ADMIN_TOKEN}" VM="${VM}" pwsh -NoProfile -Command '
$ErrorActionPreference = "Stop"
. (Join-Path $env:ROOT "lib/AgentVm.Remote.ps1")
. (Join-Path $env:ROOT "lib/AgentVm.Backup.ps1")
function Protect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x5A })) }
function Unprotect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x5A })) }
$auth = New-ConstructApiAuth -Mode token -Token $env:TOKEN
'"$1"
}

# ── 1. the heartbeat asks a fresh VM for a backup ────────────────────────────
reply="$(heartbeat false)"
ok 'a fresh idle VM is due: backupDue + scheduled' sh -c "test '$(cat "${tmp}/status")' = 200 && printf '%s' '${reply}' | jq -e '.backupDue == true and .backupTrigger == \"scheduled\"' >/dev/null"

# ── 2. enrolment: the PC key's fingerprint is the guest's ────────────────────
pc_out="$(pc '$k = Get-ConstructBackupPcKey -KeyDir $env:KEYDIR -Create -Label "e2e-pc"
Set-Content -LiteralPath (Join-Path $env:KEYDIR "pub.pem") -Value $k.PublicKeyPem -NoNewline
Write-Output "FP=$($k.Fingerprint)"' 2>&1)"
pc_fp="$(sed -n 's/^FP=//p' <<<"${pc_out}")"
guest_fp="$(guest recipients add --label e2e-pc <"${tmp}/pckey/pub.pem" 2>/dev/null | tail -n 1)"
ok 'recipients add prints the PC fingerprint' sh -c "test -n '${pc_fp}' && test '${pc_fp}' = '${guest_fp}'"

# ── 3. a scheduled backup lands on the host ──────────────────────────────────
create_out="$(guest create --trigger scheduled 2>"${tmp}/create.err")"
create_rc=$?
id1="$(tail -n 1 <<<"${create_out}" | jq -r '.id // empty' 2>/dev/null)"
ok 'create --trigger scheduled exits 0' test "${create_rc}" = 0
ok 'create prints the backup id' sh -c "printf '%s' '${id1}' | grep -Eq '^[0-9a-f]{32}$'"
ok 'create reports the git credential hosts' sh -c "printf '%s' '$(tail -n 1 <<<"${create_out}")' | jq -e '.gitCredentialHosts == [\"git.example.com\"]' >/dev/null"
if [[ "${create_rc}" != 0 ]]; then sed 's/^/    | /' "${tmp}/create.err" | tail -20; fi

heartbeat false >/dev/null
ok 'after a success the heartbeat is quiet (204)' status_is 204

list="$(admin GET "/vms/${VM}/backups")"
ok 'the owner lists the backup' sh -c "printf '%s' '${list}' | jq -e --arg id '${id1}' '.backups | map(.id) == [\$id]' >/dev/null"
status="$(admin GET "/vms/${VM}/backups/status")"
ok 'status: lastSuccessAt set, not failing' sh -c "printf '%s' '${status}' | jq -e '.lastSuccessAt != null and .failingSince == null and .count == 1' >/dev/null"
ok 'the host file is not cleartext' sh -c "! grep -rqa 'e2e-secret' '${tmp}/host-backups'"

# ── 4. the PC unwraps E from the host's keys file; the VM fetches and decrypts ─
E="$(pc '$json = Get-ConstructHostBackupKeysJson -BaseUrl $env:BASE -VmName $env:VM -BackupId "'"${id1}"'" -Auth $auth
Unprotect-ConstructBackupIdentity -KeysJson $json -KeyDir $env:KEYDIR -ExpectedBackupId "'"${id1}"'"' 2>"${tmp}/unwrap.err" | tail -n 1)"
ok 'the PC unwraps the backup identity' sh -c "printf '%s' '${E}' | grep -Eq '^AGE-SECRET-KEY-1[0-9A-Z]+$'"
printf '%s\n' "${E}" | guest fetch --from-host "${id1}" --out "${tmp}/fetched.tar.zst" 2>"${tmp}/fetch.err"
ok 'fetch decrypts the host backup' test -s "${tmp}/fetched.tar.zst"
ok 'the fetched tarball carries the exported home' sh -c "tar -tf '${tmp}/fetched.tar.zst' | grep -q 'home/.claude/CLAUDE.md'"
ok 'backup-info.json names the backup id' sh -c "tar -xOf '${tmp}/fetched.tar.zst' ./backup-info.json | jq -e --arg id '${id1}' '.backupId == \$id and .format == \"construct-backup/1\"' >/dev/null"
printf 'AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ\n' \
  | guest fetch --from-host "${id1}" --out "${tmp}/wrong.tar.zst" >/dev/null 2>&1
ok 'a wrong identity fails with exit 8' test "$?" = 8
printf '%s\n' "${E}" | guest verify --from-host "${id1}" >/dev/null 2>"${tmp}/verify.err"
ok 'verify passes for the host backup with its identity' test "$?" = 0
printf 'AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ\n' \
  | guest verify --from-host "${id1}" >/dev/null 2>&1
ok 'verify with a wrong identity fails with exit 8' test "$?" = 8

# ── 5. download: the container the PC saves ──────────────────────────────────
guest download "${id1}" >"${tmp}/saved.cbk" 2>"${tmp}/download.err"
ok 'download writes a container' sh -c "head -n 1 '${tmp}/saved.cbk' | grep -qx 'construct-backup/1'"
E2="$(pc '$h = Read-ConstructBackupContainerHeader -Path "'"${tmp}/saved.cbk"'"
Unprotect-ConstructBackupIdentity -KeysJson $h.KeysJson -KeyDir $env:KEYDIR' 2>"${tmp}/container.err" | tail -n 1)"
ok 'the PC reads the container header and unwraps the same identity' sh -c "test -n '${E2}' && test '${E2}' = '${E}'"
ok 'the container payload decrypts' bash -c "{ IFS= read -r a; IFS= read -r b; cat; } <'${tmp}/saved.cbk' | age -d -i <(printf '%s\n' '${E}') | zstd -dc | tar -t | grep -q 'backup-info.json'"

# ── 6. pre-reinstall: pinned until restored ──────────────────────────────────
id2="$(guest create --trigger pre-reinstall 2>/dev/null | tail -n 1 | jq -r '.id // empty')"
ok 'a pre-reinstall backup is pinned' sh -c "curl -fsS -H '@${tmp}/admin-header' '${BASE}/api/v1/vms/${VM}/backups' | jq -e --arg id '${id2}' '.backups[] | select(.id == \$id) | .pinnedUntil != null' >/dev/null"
guest restored "${id2}" >/dev/null 2>&1
ok 'restored unpins it' sh -c "curl -fsS -H '@${tmp}/admin-header' '${BASE}/api/v1/vms/${VM}/backups' | jq -e --arg id '${id2}' '.backups[] | select(.id == \$id) | .pinnedUntil == null' >/dev/null"

# ── 7. settings and rotation ─────────────────────────────────────────────────
settings="$(admin PUT "/vms/${VM}/backups/settings" '{"intervalHours":12,"keep":1}')"
ok 'the owner sets a per-VM override' sh -c "printf '%s' '${settings}' | jq -e '.keep == 1 and .intervalHours == 12 and .effective.keep == 1' >/dev/null"
id3="$(guest create --trigger manual 2>/dev/null | tail -n 1 | jq -r '.id // empty')"
list="$(admin GET "/vms/${VM}/backups")"
ok 'keep 1 leaves only the newest backup' sh -c "printf '%s' '${list}' | jq -e --arg id '${id3}' '.backups | map(.id) == [\$id]' >/dev/null"
ok 'rotated payload files are gone from the host' sh -c "test \"\$(find '${tmp}/host-backups' -name '*.age' | wc -l)\" -eq 1"

# ── 8. a manual request reaches a busy VM on its next heartbeat ──────────────
admin POST "/vms/${VM}/backups/request" '{}' >/dev/null
ok 'request answers 202' status_is 202
reply="$(heartbeat true)"
ok 'a requested backup is due even while busy (manual)' sh -c "printf '%s' '${reply}' | jq -e '.backupDue == true and .backupTrigger == \"manual\"' >/dev/null"

# ── 9. a run without recipients is reported ──────────────────────────────────
rm -f "${tmp}/recipients"/*
guest create --trigger manual >/dev/null 2>&1
ok 'create without recipients exits 3' test "$?" = 3
status="$(admin GET "/vms/${VM}/backups/status")"
ok 'status shows the no-recipients failure' sh -c "printf '%s' '${status}' | jq -e '.lastFailure.reason == \"no-recipients\" and .failingSince != null and .requestPending == false' >/dev/null"

# ── 10. another user's token sees nothing ────────────────────────────────────
other_vm_token_status="$(curl -sS -o /dev/null -w '%{http_code}' -H 'Authorization: VmToken not-a-token' "${BASE}/api/v1/vms/${VM}/backups")"
ok 'an invalid VM token is refused' test "${other_vm_token_status}" = 401

printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
if [[ "${fail}" -ne 0 ]]; then
  printf '  service log tail:\n'
  tail -30 "${tmp}/service.log" | sed 's/^/    | /'
fi
[[ "${fail}" -eq 0 ]]
