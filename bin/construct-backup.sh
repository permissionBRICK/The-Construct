#!/usr/bin/env bash
# construct-backup.sh — `construct backup`: encrypted config backups that the
# VM's host stores (contract: docs/plans/host-config-backups.md, 3.1-3.4).
#
# The VM packs, compresses and encrypts the config export before the first byte
# leaves it, so the host only ever holds ciphertext:
#
#   export-config.sh (STAGE_DIR) -> tar | zstd | age -r <E's recipient> -> host or stdout
#
# E is a fresh age identity per backup. It lives in shell variables and pipes
# only, never on disk or on a command line, and is wrapped (RSA-OAEP-SHA256)
# for every PC key enrolled in /etc/construct/backup-recipients/: the "keys
# file", which the host stores next to the payload but cannot use. For a
# restore the PC unwraps E and sends it back as the first line of stdin.
#
# Service settings are read like construct-vm.sh does: narrow config.env
# lookups (never `source`), the VM token in a 0600 header file passed with
# -H @file and never on argv, --cacert for the host's certificate. Progress goes
# to stderr; stdout carries only the documented output.
#
# Exit codes: 0 ok, 2 usage, 3 no recipients enrolled, 4 no host service or the
# host lacks the feature, 5 not enough space on this VM (skipped), 6 host
# refused, 7 transfer failed, 8 decryption or verification failed, 9 another
# backup or restore is running, 10 export or restore step failed.
set -Eeuo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
CURL="${CONSTRUCT_CURL:-curl}"
API_TIMEOUT="${CONSTRUCT_SERVICE_TIMEOUT_SEC:-20}"
# A transfer that moves less than a byte per second for this long is dead.
STALL_SEC="${CONSTRUCT_BACKUP_STALL_SEC:-300}"
RECIPIENTS_DIR="${CONSTRUCT_BACKUP_RECIPIENTS_DIR:-/etc/construct/backup-recipients}"
LOCK_FILE="${CONSTRUCT_BACKUP_LOCK_FILE:-/run/construct/backup.lock}"
# Where the stage and restore archives go: the file system the space check reads.
STAGE_PARENT="${CONSTRUCT_BACKUP_STAGE_PARENT:-/tmp}"
# auto probes the staging file system; yes/no pins the answer (tests).
REFLINK_MODE="${CONSTRUCT_BACKUP_REFLINK:-auto}"
SYSTEMCTL="${CONSTRUCT_SYSTEMCTL:-systemctl}"
EXPORT_HOME="${EXPORT_HOME:-/root}"
PROJECTS_STORE="${PROJECTS_STORE:-/opt/construct/projects}"
REPO_BIN="${CONSTRUCT_REPO_DIR:-/opt/construct/repo}/bin"
SERVE_WEB_UNIT="code-serve-web.service"   # bin/install-vscode.sh
CONTAINER_MAGIC="construct-backup/1"
MAX_RECIPIENTS=32
LABEL_RE='^[A-Za-z0-9._ -]{1,64}$'
ID_RE='^[0-9a-f]{32}$'
FP_RE='^[0-9a-f]{64}$'
IDENTITY_RE='^AGE-SECRET-KEY-1[0-9A-Z]+$'

EXIT_USAGE=2
EXIT_NO_RECIPIENTS=3
EXIT_NO_SERVICE=4
EXIT_NO_SPACE=5
EXIT_REFUSED=6
EXIT_TRANSFER=7
EXIT_CRYPTO=8
EXIT_BUSY=9
EXIT_STEP=10

usage() {
  cat <<'USAGE'
Usage: construct backup <command> [options]

Config backups, encrypted on this VM for the PCs enrolled as recipients and
stored by the VM's host. The host cannot read them; this VM cannot read old
ones either: a restore needs the key a recipient PC unwraps.

  create [--trigger scheduled|manual|pre-reinstall]
                       Back this VM's config up to its host (default trigger:
                       manual). Prints one JSON line:
                       {"id","sizeBytes","sha256","trigger","gitCredentialHosts"}
  create --stdout      Write an encrypted backup container to stdout instead;
                       no host is involved.
  download ID          Write the host's backup ID to stdout as a container.
  fetch --from-host ID --out FILE
                       Download backup ID and decrypt it into FILE (.tar.zst).
  restore --from-host ID
  restore --stdin --expect-id ID
                       Restore backup ID onto this VM: from the host, or from
                       the encrypted payload that follows the key on stdin.
                       fetch and restore read the backup key from the first
                       line of stdin.
  list                 The host's backups of this VM (JSON).
  status               The host's backup status for this VM (JSON).
  recipients list      The enrolled PC keys (JSON).
  recipients add --label LABEL
                       Enrol the RSA public key (PEM, at least 3072 bits) read
                       from stdin; prints its fingerprint.
  recipients remove FINGERPRINT
  estimate             The space a backup needs on this VM and whether it
                       fits (JSON).

A container is: line 1 "construct-backup/1", line 2 the keys JSON, then the
encrypted payload.

Exit codes: 0 ok, 2 usage, 3 no recipients enrolled, 4 no host service or the
host lacks the feature, 5 not enough space on this VM (skipped), 6 host
refused, 7 transfer failed, 8 decryption or verification failed, 9 another
backup or restore is running, 10 export or restore step failed.
USAGE
}

msg() { printf 'construct backup: %s\n' "$*" >&2; }
usage_fail() { msg "$*"; exit "${EXIT_USAGE}"; }

# ── configuration (narrow lookups, as construct-vm.sh) ───────────────────────

_cfg_unquote() {
  local value="$1"
  if [[ ${#value} -ge 2 && "${value}" == \'*\' ]]; then
    value="${value:1:${#value}-2}"
    value="${value//\'\\\'\'/\'}"
  fi
  printf '%s' "${value}"
}

cfg_saved() {
  local key="$1" raw=""
  [[ -f "${CONFIG_FILE}" ]] || return 0
  raw="$(sed -n "s/^${key}=//p" "${CONFIG_FILE}" | head -n 1 || true)"
  _cfg_unquote "${raw}"
}

cfg_resolve() {
  local explicit="$1" key="$2" fallback="$3" saved
  if [[ -n "${explicit}" ]]; then printf '%s' "${explicit}"; return 0; fi
  saved="$(cfg_saved "${key}")"
  printf '%s' "${saved:-${fallback}}"
}

SERVICE_URL="$(cfg_resolve "${CONSTRUCT_SERVICE_URL:-}" CONSTRUCT_SERVICE_URL "")"
SERVICE_URL="${SERVICE_URL%/}"
INSTANCE_NAME="$(cfg_resolve "${CONSTRUCT_INSTANCE_NAME:-}" CONSTRUCT_INSTANCE_NAME "$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || printf vm)")"
CA_FILE="$(cfg_resolve "${CONSTRUCT_SERVICE_CA_FILE:-}" CONSTRUCT_SERVICE_CA_FILE "")"
VM_TOKEN_FILE="$(cfg_resolve "${CONSTRUCT_VM_TOKEN_FILE:-}" CONSTRUCT_VM_TOKEN_FILE /etc/construct/vm-token)"
AUTH_SCHEME="$(cfg_resolve "${CONSTRUCT_SERVICE_AUTH_SCHEME:-}" CONSTRUCT_SERVICE_AUTH_SCHEME VmToken)"
SW_TOKEN_FILE="$(cfg_resolve "${VSCODE_SERVE_WEB_TOKEN_FILE:-}" VSCODE_SERVE_WEB_TOKEN_FILE /etc/construct/vscode-serve-web.token)"

# ── run state and cleanup ────────────────────────────────────────────────────

WORK_DIR=""
AUTH_HEADER_FILE=""
STAGE=""
TMP_PATHS=()
CURL_PID=""
SHA_PID=""
E=""
ERR_LINE=""
# create: the open host session, the trigger, and whether failures go to the host.
SESSION_ID=""
SESSION_OPEN=""
CREATE_TRIGGER=""
REPORT_TO_HOST=""
REPORTED=""
PHASE=""

# shellcheck disable=SC2317 # runs from the EXIT trap
cleanup() {
  local rc=$?
  trap - EXIT ERR
  set +e
  E=""
  if [[ -n "${CURL_PID}" ]]; then kill "${CURL_PID}" 2>/dev/null; fi
  if [[ -n "${SHA_PID}" ]]; then kill "${SHA_PID}" 2>/dev/null; fi
  case "${rc}" in
    0|2|3|4|5|6|7|8|9|10|129|130|143) ;;
    *) msg "unexpected failure${ERR_LINE:+ at construct-backup.sh line ${ERR_LINE}} (exit ${rc})"; rc="${EXIT_STEP}" ;;
  esac
  # A run that ends early (a signal, an unexpected error) still closes its
  # session on the host and says why.
  if [[ "${rc}" -ne 0 && -n "${PHASE}" && -z "${REPORTED}" ]]; then
    local detail="exit ${rc}"
    case "${rc}" in 129|130|143) detail="interrupted" ;; esac
    if [[ "${PHASE}" == upload ]]; then
      finish_failed_create failed upload-failed "${detail}"
    else
      finish_failed_create failed export-failed "${detail}"
    fi
  fi
  if [[ -n "${STAGE}" ]]; then rm -rf "${STAGE}"; fi
  local path
  for path in ${TMP_PATHS[@]+"${TMP_PATHS[@]}"}; do rm -rf "${path}"; done
  if [[ -n "${WORK_DIR}" ]]; then rm -rf "${WORK_DIR}"; fi
  exit "${rc}"
}
trap cleanup EXIT
trap 'ERR_LINE="${LINENO}"' ERR
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

ensure_work_dir() {
  if [[ -n "${WORK_DIR}" ]]; then return 0; fi
  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/construct-backup-work.XXXXXX")" \
    || { msg "cannot create a private temporary directory"; exit "${EXIT_STEP}"; }
}

require_root() {
  [[ "${EUID}" -eq 0 ]] || usage_fail "run as root (the backup reads and restores root's configuration)"
}

# One backup or restore at a time. Children never inherit the lock (fd 9 is
# closed for them), so nothing they leave running can hold it.
acquire_lock() {
  command -v flock >/dev/null 2>&1 || { msg "flock is required (apt-get install -y util-linux)"; exit "${EXIT_STEP}"; }
  mkdir -p "$(dirname "${LOCK_FILE}")" 2>/dev/null || true
  exec 9>>"${LOCK_FILE}" || { msg "cannot open the lock file ${LOCK_FILE}"; exit "${EXIT_STEP}"; }
  if ! flock -n 9; then
    msg "another backup or restore is running"
    exit "${EXIT_BUSY}"
  fi
}

find_script() {
  local name="$1" here candidate
  here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  for candidate in "${here}/${name}" "${REPO_BIN}/${name}"; do
    if [[ -f "${candidate}" ]]; then printf '%s' "${candidate}"; return 0; fi
  done
  return 1
}

need_tools() {
  local tool
  for tool in "$@"; do
    command -v "${tool}" >/dev/null 2>&1 || { msg "${tool} is required (apt-get install -y age zstd openssl jq)"; exit "${EXIT_STEP}"; }
  done
}

# ── host service ─────────────────────────────────────────────────────────────

require_service() {
  if [[ -z "${SERVICE_URL}" ]]; then
    msg "this VM has no host service (CONSTRUCT_SERVICE_URL is unset)"
    exit "${EXIT_NO_SERVICE}"
  fi
  if [[ ! -r "${VM_TOKEN_FILE}" ]]; then
    msg "no VM token at ${VM_TOKEN_FILE}; reprovision this VM with its service credential"
    exit "${EXIT_NO_SERVICE}"
  fi
  local token
  token="$(head -n 1 "${VM_TOKEN_FILE}" 2>/dev/null | tr -d '\r\n' || true)"
  [[ -n "${token}" ]] || { msg "the VM token file ${VM_TOKEN_FILE} is empty"; exit "${EXIT_NO_SERVICE}"; }
  ensure_work_dir
  AUTH_HEADER_FILE="${WORK_DIR}/auth.headers"
  ( umask 077; printf 'Authorization: %s %s\n' "${AUTH_SCHEME}" "${token}" >"${AUTH_HEADER_FILE}" )
  unset token
}

urlencode() { jq -nr --arg value "$1" '$value | @uri'; }
backups_path() { printf '/api/v1/vms/%s/backups' "$(urlencode "${INSTANCE_NAME}")"; }

API_STATUS=""
API_BODY=""
API_ERROR=""

# One JSON request. Bodies travel in files, the token in the header file.
api_call() {
  local method="$1" path="$2" body="${3:-}" out err req="" rc=0
  out="${WORK_DIR}/response.${RANDOM}${RANDOM}"
  err="${WORK_DIR}/stderr.${RANDOM}${RANDOM}"
  local args=(--silent --show-error --fail-with-body --max-time "${API_TIMEOUT}"
    -H "@${AUTH_HEADER_FILE}" -H 'Accept: application/json' -X "${method}" -o "${out}" -w '%{http_code}')
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  if [[ -n "${body}" ]]; then
    req="${WORK_DIR}/request.${RANDOM}${RANDOM}.json"
    printf '%s' "${body}" >"${req}"
    args+=(-H 'Content-Type: application/json' --data-binary "@${req}")
  fi
  API_STATUS="$("${CURL}" "${args[@]}" "${SERVICE_URL}${path}" 2>"${err}")" || rc=$?
  API_STATUS="${API_STATUS:-000}"
  if [[ ${rc} -ne 0 && ${rc} -ne 22 ]]; then API_STATUS=000; fi
  API_BODY="$(cat "${out}" 2>/dev/null || true)"
  API_ERROR="$(tr -d '\r' <"${err}" 2>/dev/null | head -n 3 || true)"
  rm -f "${out}" "${err}"
  if [[ -n "${req}" ]]; then rm -f "${req}"; fi
  return 0
}

api_ok() { [[ "${API_STATUS}" =~ ^2[0-9][0-9]$ ]]; }

problem_code() {
  printf '%s' "${API_BODY}" | jq -r 'if type == "object" then (.code // empty) | tostring else empty end' 2>/dev/null || true
}

describe_failure() {
  local code title
  if [[ "${API_STATUS}" == 000 ]]; then
    printf 'cannot reach the host service at %s%s' "${SERVICE_URL}" "${API_ERROR:+ (${API_ERROR//$'\n'/; })}"
    return 0
  fi
  code="$(problem_code)"
  title="$(printf '%s' "${API_BODY}" | jq -r 'if type == "object" then (.detail // .title // empty) | tostring else empty end' 2>/dev/null || true)"
  printf 'the host answered HTTP %s%s%s' "${API_STATUS}" "${title:+: ${title}}" "${code:+ (${code})}"
}

# The detail of a host-refused report: the problem code, else the status.
refusal_detail() {
  local code
  code="$(problem_code)"
  printf '%s' "${code:-HTTP ${API_STATUS}}"
}

# 000 and server errors are transfer failures; any other refusal is the host's.
failure_exit_code() {
  case "${API_STATUS}" in
    000|500|502|503|504) printf '%s' "${EXIT_TRANSFER}" ;;
    *) printf '%s' "${EXIT_REFUSED}" ;;
  esac
}

# A 404 is either an unknown backup or a host without the feature: the status
# route tells them apart.
exit_not_found() {
  local what="$1"
  api_call GET "$(backups_path)/status"
  if [[ "${API_STATUS}" == 404 || "${API_STATUS}" == 405 ]]; then
    msg "the host service does not offer config backups"
    exit "${EXIT_NO_SERVICE}"
  fi
  msg "the host has no ${what}"
  exit "${EXIT_REFUSED}"
}

# Fail a host request: message and the mapped exit code (no report).
api_fail() {
  local what="$1"
  if [[ "${API_STATUS}" == 404 || "${API_STATUS}" == 405 ]]; then exit_not_found "${what}"; fi
  msg "${what}: $(describe_failure)"
  exit "$(failure_exit_code)"
}

report() { # <result> <reason> <detail>
  local body
  body="$(jq -cn --arg trigger "${CREATE_TRIGGER}" --arg result "$1" --arg reason "$2" --arg detail "${3:0:500}" \
    '{trigger:$trigger,result:$result,reason:$reason,detail:$detail}')"
  api_call POST "$(backups_path)/report" "${body}"
  api_ok || msg "could not report the ${1} backup to the host: $(describe_failure)"
}

delete_session() {
  [[ -n "${SESSION_OPEN}" ]] || return 0
  SESSION_OPEN=""
  api_call DELETE "$(backups_path)/${SESSION_ID}"
  if ! api_ok && [[ "${API_STATUS}" != 404 ]]; then
    msg "could not delete the unfinished backup ${SESSION_ID} on the host: $(describe_failure)"
  fi
}

# Close what a failed create left on the host and report why (host mode only).
finish_failed_create() { # <result> <reason> <detail>
  [[ -z "${REPORTED}" ]] || return 0
  REPORTED=1
  [[ -n "${REPORT_TO_HOST}" ]] || return 0
  delete_session
  report "$1" "$2" "$3"
}

create_fail() { # <exit code> <result> <reason> <detail> <message>
  msg "$5"
  finish_failed_create "$2" "$3" "$4"
  exit "$1"
}

# ── keys and recipients ──────────────────────────────────────────────────────

# Lowercase hex SHA-256 of the key's SubjectPublicKeyInfo DER.
key_fingerprint() {
  local der
  der="$(openssl pkey -pubin -in "$1" -outform DER 2>/dev/null | sha256sum)" || return 1
  der="${der%% *}"
  [[ "${der}" =~ ${FP_RE} ]] || return 1
  printf '%s' "${der}"
}

recipient_meta() { # <fp> <field>
  local json="${RECIPIENTS_DIR}/$1.json"
  [[ -f "${json}" ]] || return 0
  jq -r --arg f "$2" 'if type == "object" and (.[$f] | type) == "string" then .[$f] else empty end' "${json}" 2>/dev/null || true
}

RECIP_FPS=()
RECIP_PEMS=()
RECIP_LABELS=()
collect_recipients() {
  RECIP_FPS=(); RECIP_PEMS=(); RECIP_LABELS=()
  [[ -d "${RECIPIENTS_DIR}" ]] || return 0
  local pem fp actual label
  local -a pems=()
  shopt -s nullglob
  pems=("${RECIPIENTS_DIR}"/*.pem)
  shopt -u nullglob
  for pem in ${pems[@]+"${pems[@]}"}; do
    fp="$(basename "${pem}" .pem)"
    if [[ ! "${fp}" =~ ${FP_RE} ]]; then msg "ignoring ${pem}: not named after a key fingerprint"; continue; fi
    actual="$(key_fingerprint "${pem}" || true)"
    if [[ "${actual}" != "${fp}" ]]; then msg "ignoring ${pem}: the key does not match its name"; continue; fi
    if (( ${#RECIP_FPS[@]} >= MAX_RECIPIENTS )); then msg "ignoring ${fp}: more than ${MAX_RECIPIENTS} recipients"; continue; fi
    label="$(recipient_meta "${fp}" label)"
    [[ "${label}" =~ ${LABEL_RE} ]] || label="PC"
    RECIP_FPS+=("${fp}"); RECIP_PEMS+=("${pem}"); RECIP_LABELS+=("${label}")
  done
}

now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# The ephemeral identity E and its recipient. E stays in this shell.
AGE_RECIPIENT=""
new_identity() {
  local generated line
  generated="$(age-keygen 2>/dev/null)" || return 1
  E=""
  while IFS= read -r line; do
    if [[ "${line}" =~ ${IDENTITY_RE} ]]; then E="${line}"; fi
  done <<<"${generated}"
  generated=""
  [[ -n "${E}" ]] || return 1
  AGE_RECIPIENT="$(printf '%s\n' "${E}" | age-keygen -y 2>/dev/null)" || return 1
  [[ "${AGE_RECIPIENT}" =~ ^age1[0-9a-z]+$ ]]
}

KEYS_JSON=""
build_keys_json() { # <backup id>
  local recips='[]' i wrapped
  for i in "${!RECIP_FPS[@]}"; do
    wrapped="$(printf '%s' "${E}" | openssl pkeyutl -encrypt -pubin -inkey "${RECIP_PEMS[i]}" \
      -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 -pkeyopt rsa_mgf1_md:sha256 2>/dev/null \
      | base64 -w0)" || return 1
    [[ -n "${wrapped}" ]] || return 1
    recips="$(jq -c --arg fp "${RECIP_FPS[i]}" --arg label "${RECIP_LABELS[i]}" --arg wrapped "${wrapped}" \
      '. + [{fingerprint:$fp,label:$label,wrapped:$wrapped}]' <<<"${recips}")" || return 1
  done
  KEYS_JSON="$(jq -cn --arg id "$1" --arg at "$(now_iso)" --argjson recipients "${recips}" \
    '{format:"construct-backup-keys/1",backupId:$id,createdAt:$at,recipients:$recipients}')"
}

read_identity() {
  local line=""
  if ! IFS= read -r line && [[ -z "${line}" ]]; then
    msg "expected the backup key as the first line of stdin"
    exit "${EXIT_USAGE}"
  fi
  line="${line%$'\r'}"
  if [[ ! "${line}" =~ ${IDENTITY_RE} ]]; then
    msg "the first line of stdin is not a backup key (AGE-SECRET-KEY-1...)"
    exit "${EXIT_CRYPTO}"
  fi
  E="${line}"
}

decrypt_to() { # <file>: stdin is the age payload
  age -d -i <(printf '%s\n' "${E}") >"$1"
}

# ── space ────────────────────────────────────────────────────────────────────

# The databases export-config.sh snapshots (always real copies): db plus -wal,
# since a snapshot holds both.
sqlite_sources() {
  local f
  if [[ -f "${EXPORT_HOME}/.t3/userdata/statev2.sqlite" ]]; then
    printf '%s\n' "${EXPORT_HOME}/.t3/userdata/statev2.sqlite"
  elif [[ -f "${EXPORT_HOME}/.t3/userdata/state.sqlite" ]]; then
    printf '%s\n' "${EXPORT_HOME}/.t3/userdata/state.sqlite"
  fi
  if [[ -f "${EXPORT_HOME}/.local/share/opencode/opencode.db" ]]; then
    printf '%s\n' "${EXPORT_HOME}/.local/share/opencode/opencode.db"
  fi
  shopt -s nullglob
  for f in "${EXPORT_HOME}"/.codex/memories_*.sqlite; do printf '%s\n' "${f}"; done
  shopt -u nullglob
}

# What export-config.sh stages (relative to EXPORT_HOME); it mirrors that
# script and only feeds the estimate for a file system without reflinks.
STAGED_HOME_PATHS=(
  .gitconfig .git-credentials .config/gh .config/glab-cli .ssh .npmrc .secrets
  construct-custom-system-prompt.md
  .claude/CLAUDE.md .claude/settings.json .claude/skills .claude.json .claude/.credentials.json
  .claude/mcp-needs-auth-cache.json .claude/projects .claude/history.jsonl
  .codex/AGENTS.md .codex/config.toml .codex/memories .codex/auth.json .codex/.credentials.json
  .codex/skills .codex/sessions .codex/archived_sessions .codex/session_index.jsonl .codex/history.jsonl
  .config/opencode/opencode.json .local/share/opencode/auth.json .local/share/opencode/mcp-auth.json
  .local/share/opencode/storage .local/share/opencode/project
  .t3/userdata/keybindings.json .t3/userdata/settings.json .t3/userdata/secrets
  .t3/userdata/environment-id .t3/userdata/attachments
)

probe_reflink() {
  case "${REFLINK_MODE}" in
    yes|true) return 0 ;;
    no|false) return 1 ;;
  esac
  local dir rc=0
  dir="$(mktemp -d "${STAGE_PARENT}/construct-reflink.XXXXXX" 2>/dev/null)" || return 1
  printf 'reflink probe\n' >"${dir}/a" && cp --reflink=always "${dir}/a" "${dir}/b" 2>/dev/null || rc=1
  rm -rf "${dir}"
  return "${rc}"
}

SPACE_NEED=0
SPACE_FREE=0
SPACE_RESERVE=0
SPACE_REFLINK=false
SPACE_OK=false
estimate_space() {
  local db bytes=0 size stage=0 rel avail fs_size line
  local -a paths=()
  while IFS= read -r db; do
    [[ -n "${db}" ]] || continue
    size="$(stat -c %s -- "${db}" 2>/dev/null || printf 0)"
    bytes=$((bytes + size))
    if [[ -f "${db}-wal" ]]; then
      size="$(stat -c %s -- "${db}-wal" 2>/dev/null || printf 0)"
      bytes=$((bytes + size))
    fi
  done < <(sqlite_sources)
  if probe_reflink; then
    SPACE_REFLINK=true
  else
    SPACE_REFLINK=false
    for rel in "${STAGED_HOME_PATHS[@]}"; do
      if [[ -e "${EXPORT_HOME}/${rel}" ]]; then paths+=("${EXPORT_HOME}/${rel}"); fi
    done
    shopt -s nullglob
    paths+=("${EXPORT_HOME}"/.config/opencode/*.md)
    shopt -u nullglob
    if [[ -d "${PROJECTS_STORE}" ]]; then paths+=("${PROJECTS_STORE}"); fi
    if (( ${#paths[@]} > 0 )); then
      stage="$(du -scb -- "${paths[@]}" 2>/dev/null | tail -n 1 | cut -f1 || true)"
      [[ "${stage}" =~ ^[0-9]+$ ]] || stage=0
    fi
  fi
  SPACE_NEED=$((bytes + stage + 64 * 1024 * 1024))
  line="$(df -B1 --output=avail,size -- "${STAGE_PARENT}" 2>/dev/null | tail -n 1 || true)"
  read -r avail fs_size <<<"${line}" || true
  if [[ ! "${avail:-}" =~ ^[0-9]+$ || ! "${fs_size:-}" =~ ^[0-9]+$ ]]; then
    return 1
  fi
  SPACE_FREE="${avail}"
  SPACE_RESERVE=$((fs_size / 20))
  if (( SPACE_RESERVE < 2 * 1024 * 1024 * 1024 )); then SPACE_RESERVE=$((2 * 1024 * 1024 * 1024)); fi
  if (( SPACE_FREE - SPACE_NEED >= SPACE_RESERVE )); then SPACE_OK=true; else SPACE_OK=false; fi
}

# ── staging ──────────────────────────────────────────────────────────────────

stage_export() { # <export reason>; fills STAGE
  local script rc=0
  script="$(find_script export-config.sh)" \
    || { msg "export-config.sh not found (looked next to this script and in ${REPO_BIN})"; return 1; }
  STAGE="$(mktemp -d "${STAGE_PARENT}/construct-backup.XXXXXX")" || return 1
  # The export's progress is for humans: stderr, never stdout.
  STAGE_DIR="${STAGE}" EXPORT_REASON="$1" bash "${script}" 9>&- >&2 || rc=$?
  [[ "${rc}" -eq 0 ]] || return "${rc}"
  stage_recipients
}

# The enrolled PC keys ride in the backup (etc/construct/backup-recipients/), so
# a restored VM keeps accepting the same PCs. They are public keys.
stage_recipients() {
  local file dest="${STAGE}/etc/construct/backup-recipients"
  local -a files=()
  [[ -d "${RECIPIENTS_DIR}" ]] || return 0
  shopt -s nullglob
  for file in "${RECIPIENTS_DIR}"/*.pem "${RECIPIENTS_DIR}"/*.json; do
    if [[ "$(basename "${file}")" =~ ^[0-9a-f]{64}\.(pem|json)$ ]]; then files+=("${file}"); fi
  done
  shopt -u nullglob
  (( ${#files[@]} > 0 )) || return 0
  mkdir -p "${dest}"
  cp -p -- "${files[@]}" "${dest}/" || return 1
  printf 'etc/construct/backup-recipients\n' >>"${STAGE}/MANIFEST.txt"
}

stage_info_add() { # <id> <trigger>
  local info="${STAGE}/backup-info.json" tmp
  tmp="${info}.tmp"
  [[ -f "${info}" ]] || printf '{}\n' >"${info}"
  jq --arg id "$1" --arg trigger "$2" --arg instance "${INSTANCE_NAME}" \
    '. + {format:"construct-backup/1",backupId:$id,trigger:$trigger,instance:$instance}' "${info}" >"${tmp}" \
    && mv -f "${tmp}" "${info}"
}

# Host names (no user, no secret, no port) in the staged credential store.
git_credential_hosts() {
  local file="${STAGE}/home/.git-credentials" line auth host
  if [[ ! -f "${file}" ]]; then printf '[]'; return 0; fi
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"
    line="${line#"${line%%[![:space:]]*}"}"
    case "${line}" in http://*|https://*) ;; *) continue ;; esac
    auth="${line#*://}"
    auth="${auth%%/*}"
    auth="${auth%%\?*}"
    auth="${auth##*@}"
    if [[ "${auth}" == \[* ]]; then host="${auth%%]*}]"; else host="${auth%%:*}"; fi
    host="${host,,}"
    if [[ -n "${host}" ]]; then printf '%s\n' "${host}"; fi
  done <"${file}" | sort -u | jq -R . | jq -cs .
}

# ── streaming ────────────────────────────────────────────────────────────────

SHA_FIFO=""
SHA_OUT=""
SHA_RESULT=""
sha_start() {
  SHA_FIFO="${WORK_DIR}/sha.${RANDOM}${RANDOM}.fifo"
  SHA_OUT="${WORK_DIR}/sha.${RANDOM}${RANDOM}.out"
  mkfifo "${SHA_FIFO}"
  sha256sum <"${SHA_FIFO}" >"${SHA_OUT}" &
  SHA_PID=$!
}
sha_finish() {
  wait "${SHA_PID}" 2>/dev/null || true
  SHA_PID=""
  SHA_RESULT=""
  read -r SHA_RESULT _ <"${SHA_OUT}" 2>/dev/null || true
  rm -f "${SHA_FIFO}" "${SHA_OUT}"
}

# tar | zstd | age into the open session's content upload. curl reads a FIFO
# whose write end this shell holds until the producer has finished: if any
# stage of the producer fails, curl is killed before it sees a clean end of
# stream, so the host never keeps a payload that was cut short.
UP_RESULT=""   # ok | export | http
UP_BODY=""
upload_payload() {
  local fifo="${WORK_DIR}/upload.fifo" body="${WORK_DIR}/upload.body" err="${WORK_DIR}/upload.err"
  local status_file="${WORK_DIR}/upload.status" curl_rc=0 producer_ok=1 st last
  local -a pst=()
  mkfifo "${fifo}"
  sha_start
  local args=(--silent --show-error --fail-with-body --connect-timeout 30
    --speed-limit 1 --speed-time "${STALL_SEC}"
    -H "@${AUTH_HEADER_FILE}" -H 'Accept: application/json' -H 'Content-Type: application/octet-stream'
    -X PUT -T - -o "${body}" -w '%{http_code}')
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  "${CURL}" "${args[@]}" "${SERVICE_URL}$(backups_path)/${SESSION_ID}/content" \
    <"${fifo}" >"${status_file}" 2>"${err}" &
  CURL_PID=$!
  exec 8>"${fifo}"
  set +e
  tar -C "${STAGE}" -cf - . | zstd -T0 -3 -q | age -r "${AGE_RECIPIENT}" | tee "${SHA_FIFO}" >&8
  pst=("${PIPESTATUS[@]}")
  set -e
  for st in "${pst[@]}"; do [[ "${st}" -eq 0 ]] || producer_ok=""; done
  last="${pst[${#pst[@]}-1]}"
  if [[ -z "${producer_ok}" && "${last}" -eq 0 ]]; then
    # tar, zstd or age failed while the upload still ran: abort it.
    kill -TERM "${CURL_PID}" 2>/dev/null || true
  fi
  exec 8>&-
  wait "${CURL_PID}" || curl_rc=$?
  CURL_PID=""
  sha_finish
  API_STATUS="$(cat "${status_file}" 2>/dev/null || true)"
  API_STATUS="${API_STATUS:-000}"
  if [[ ${curl_rc} -ne 0 && ${curl_rc} -ne 22 ]]; then API_STATUS=000; fi
  API_BODY="$(cat "${body}" 2>/dev/null || true)"
  API_ERROR="$(tr -d '\r' <"${err}" 2>/dev/null | head -n 3 || true)"
  rm -f "${fifo}" "${body}" "${err}" "${status_file}"
  if [[ -z "${producer_ok}" && "${last}" -eq 0 ]]; then
    UP_RESULT="export"
    PRODUCER_STATUS="${pst[*]}"
  elif api_ok && [[ -n "${producer_ok}" ]]; then
    UP_RESULT=ok
    UP_BODY="${API_BODY}"
  else
    UP_RESULT=http
    # A failed tee with a 2xx answer means the stream broke on this side.
    if api_ok; then API_STATUS=000; API_ERROR="the upload stream broke (${pst[*]})"; fi
  fi
}
PRODUCER_STATUS=""

# GET …/backups/{id}/content into a consumer (a function or command reading
# stdin). Sets DL_* for the caller and SHA_RESULT to the payload's SHA-256.
DL_STATUS=""
DL_CONSUMER_RC=0
DL_ETAG=""
download_payload() { # <id> <consumer...>
  local id="$1" headers="${WORK_DIR}/download.headers" err="${WORK_DIR}/download.err" curl_rc
  shift
  local -a pst=()
  local args=(--silent --show-error --fail --connect-timeout 30 --speed-limit 1 --speed-time "${STALL_SEC}"
    -H "@${AUTH_HEADER_FILE}" -H 'Accept: application/octet-stream' -D "${headers}")
  if [[ -n "${CA_FILE}" ]]; then args+=(--cacert "${CA_FILE}"); fi
  : >"${headers}"
  sha_start
  set +e
  "${CURL}" "${args[@]}" "${SERVICE_URL}$(backups_path)/${id}/content" 2>"${err}" | tee "${SHA_FIFO}" | "$@"
  pst=("${PIPESTATUS[@]}")
  set -e
  sha_finish
  curl_rc="${pst[0]}"
  DL_CONSUMER_RC="${pst[2]}"
  DL_STATUS="$(awk '/^HTTP\// { code = $2 } END { print code }' "${headers}" 2>/dev/null || true)"
  DL_STATUS="${DL_STATUS:-000}"
  if [[ "${curl_rc}" -ne 0 && "${curl_rc}" -ne 22 ]]; then DL_STATUS=000; fi
  DL_ETAG="$(sed -n 's/^[Ee][Tt][Aa][Gg]:[[:space:]]*//p' "${headers}" 2>/dev/null | tr -d '\r"' | tail -n 1 || true)"
  DL_ETAG="${DL_ETAG#W/}"
  API_STATUS="${DL_STATUS}"
  API_BODY=""
  API_ERROR="$(tr -d '\r' <"${err}" 2>/dev/null | head -n 3 || true)"
  rm -f "${headers}" "${err}"
}

# The transfer and integrity checks every download shares.
download_check() { # <id>
  if [[ ! "${DL_STATUS}" =~ ^2[0-9][0-9]$ ]]; then api_fail "backup $1"; fi
  if [[ "${DL_ETAG}" =~ ^[0-9a-fA-F]{64}$ && "${DL_ETAG,,}" != "${SHA_RESULT}" ]]; then
    msg "backup $1 arrived damaged (its SHA-256 does not match the host's)"
    return 1
  fi
  return 0
}

archive_backup_id() { # <archive>
  local info
  info="$(tar --occurrence=1 -xOf "$1" ./backup-info.json 2>/dev/null \
    || tar --occurrence=1 -xOf "$1" backup-info.json 2>/dev/null)" || return 1
  printf '%s' "${info}" | jq -r 'if type == "object" then (.backupId // empty) | tostring else empty end' 2>/dev/null
}

check_archive_id() { # <archive> <expected id>
  local found
  found="$(archive_backup_id "$1" || true)"
  if [[ "${found}" != "$2" ]]; then
    msg "the decrypted backup is not backup $2${found:+ (it is ${found})}"
    return 1
  fi
}

# A private file for a decrypted archive, removed on exit (ARCHIVE_PATH).
ARCHIVE_PATH=""
new_archive_path() {
  ARCHIVE_PATH="$(mktemp "${STAGE_PARENT}/construct-backup-restore.XXXXXX")" \
    || { msg "cannot create a temporary file in ${STAGE_PARENT}"; exit "${EXIT_STEP}"; }
  TMP_PATHS+=("${ARCHIVE_PATH}")
  chmod 0600 "${ARCHIVE_PATH}"
}

# ── commands: create ─────────────────────────────────────────────────────────

cmd_create() {
  local trigger=manual to_stdout="" reason id est staged_bytes max_bytes hosts sha size
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --trigger) shift; [[ $# -gt 0 ]] || usage_fail "--trigger requires a value"; trigger="$1" ;;
      --stdout) to_stdout=1 ;;
      -h|--help) usage; exit 0 ;;
      *) usage_fail "unknown create option: $1" ;;
    esac
    shift
  done
  case "${trigger}" in
    scheduled|manual) reason=backup ;;
    pre-reinstall) reason=reinstall ;;
    *) usage_fail "--trigger must be scheduled, manual or pre-reinstall" ;;
  esac
  CREATE_TRIGGER="${trigger}"
  need_tools tar zstd age age-keygen openssl jq base64 sha256sum
  if [[ -n "${to_stdout}" ]]; then
    [[ ! -t 1 ]] || usage_fail "create --stdout writes binary data; redirect stdout to a file or pipe"
    ensure_work_dir
  else
    require_service
    REPORT_TO_HOST=1
  fi

  collect_recipients
  if (( ${#RECIP_FPS[@]} == 0 )); then
    create_fail "${EXIT_NO_RECIPIENTS}" skipped no-recipients "" \
      "no backup recipients are enrolled on this VM (a PC enrols with: construct backup recipients add)"
  fi

  if ! estimate_space; then
    create_fail "${EXIT_NO_SPACE}" skipped vm-disk-space "free space unknown" \
      "cannot read the free space of ${STAGE_PARENT}; skipping the backup"
  fi
  if [[ "${SPACE_OK}" != true ]]; then
    create_fail "${EXIT_NO_SPACE}" skipped vm-disk-space \
      "need=${SPACE_NEED} free=${SPACE_FREE} reserve=${SPACE_RESERVE}" \
      "not enough space for a backup: it needs ${SPACE_NEED} bytes, ${SPACE_FREE} are free and ${SPACE_RESERVE} must stay free; skipped"
  fi

  acquire_lock

  PHASE="export"
  msg "staging the config export (${reason})"
  if ! stage_export "${reason}"; then
    create_fail "${EXIT_STEP}" failed export-failed "export-config.sh failed" "the config export failed"
  fi
  staged_bytes="$(du -sb -- "${STAGE}" 2>/dev/null | cut -f1 || true)"
  [[ "${staged_bytes}" =~ ^[0-9]+$ ]] || staged_bytes=0
  est=$((staged_bytes * 35 / 100 + 1))

  if [[ -n "${to_stdout}" ]]; then
    id="$(openssl rand -hex 16)"
    [[ "${id}" =~ ${ID_RE} ]] || { msg "cannot create a backup id"; exit "${EXIT_STEP}"; }
  else
    api_call POST "$(backups_path)" "$(jq -cn --arg trigger "${trigger}" --argjson est "${est}" '{trigger:$trigger,estimatedBytes:$est}')"
    if ! api_ok; then
      case "${API_STATUS}" in
        404|405)
          REPORTED=1
          msg "the host service does not offer config backups"
          exit "${EXIT_NO_SERVICE}" ;;
        000|500|502|503|504)
          create_fail "${EXIT_TRANSFER}" failed upload-failed "open: HTTP ${API_STATUS}" \
            "cannot open a backup on the host: $(describe_failure)" ;;
        *)
          create_fail "${EXIT_REFUSED}" skipped host-refused "$(refusal_detail)" \
            "the host refused the backup: $(describe_failure)" ;;
      esac
    fi
    id="$(printf '%s' "${API_BODY}" | jq -r '.id // empty' 2>/dev/null || true)"
    if [[ ! "${id}" =~ ${ID_RE} ]]; then
      create_fail "${EXIT_TRANSFER}" failed upload-failed "open: no usable id" \
        "the host opened a backup without a usable id"
    fi
    SESSION_ID="${id}"
    SESSION_OPEN=1
    max_bytes="$(printf '%s' "${API_BODY}" | jq -r '.maxBytes // empty' 2>/dev/null || true)"
    msg "backup ${id} opened on the host${max_bytes:+ (at most ${max_bytes} bytes)}"
  fi

  stage_info_add "${id}" "${trigger}" \
    || create_fail "${EXIT_STEP}" failed export-failed "backup-info.json" "cannot update backup-info.json"
  hosts="$(git_credential_hosts)" \
    || create_fail "${EXIT_STEP}" failed export-failed "git credential hosts" "cannot read the staged git credentials"

  new_identity || create_fail "${EXIT_STEP}" failed export-failed "age-keygen" "cannot create the backup key"
  build_keys_json "${id}" \
    || create_fail "${EXIT_STEP}" failed export-failed "key wrapping" "cannot wrap the backup key for the enrolled recipients"

  if [[ -n "${to_stdout}" ]]; then
    local -a pst=()
    local st failed=""
    printf '%s\n%s\n' "${CONTAINER_MAGIC}" "${KEYS_JSON}" || { msg "cannot write to stdout"; exit "${EXIT_TRANSFER}"; }
    set +e
    tar -C "${STAGE}" -cf - . | zstd -T0 -3 -q | age -r "${AGE_RECIPIENT}"
    pst=("${PIPESTATUS[@]}")
    set -e
    E=""
    for st in "${pst[@]}"; do [[ "${st}" -eq 0 ]] || failed=1; done
    if [[ -n "${failed}" ]]; then
      if [[ "${pst[2]}" -ne 0 && "${pst[0]}" -eq 0 && "${pst[1]}" -eq 0 ]]; then
        msg "writing the backup to stdout failed"
        exit "${EXIT_TRANSFER}"
      fi
      msg "packing the backup failed (tar/zstd/age: ${pst[*]})"
      exit "${EXIT_STEP}"
    fi
    PHASE=""
    msg "backup ${id} written to stdout (${trigger})"
    return 0
  fi

  api_call PUT "$(backups_path)/${id}/keys" "${KEYS_JSON}"
  if ! api_ok; then
    case "$(failure_exit_code)" in
      "${EXIT_TRANSFER}") create_fail "${EXIT_TRANSFER}" failed upload-failed "keys: HTTP ${API_STATUS}" \
        "cannot upload the keys file: $(describe_failure)" ;;
      *) create_fail "${EXIT_REFUSED}" failed host-refused "$(refusal_detail)" \
        "the host refused the keys file: $(describe_failure)" ;;
    esac
  fi

  PHASE=upload
  msg "uploading backup ${id}"
  upload_payload
  E=""
  case "${UP_RESULT}" in
    ok) ;;
    export)
      create_fail "${EXIT_STEP}" failed export-failed "tar/zstd/age: ${PRODUCER_STATUS}" \
        "packing the backup failed (tar/zstd/age: ${PRODUCER_STATUS})" ;;
    *)
      if [[ "$(failure_exit_code)" == "${EXIT_TRANSFER}" ]]; then
        create_fail "${EXIT_TRANSFER}" failed upload-failed "content: HTTP ${API_STATUS}" \
          "the upload failed: $(describe_failure)"
      fi
      create_fail "${EXIT_REFUSED}" failed host-refused "$(refusal_detail)" \
        "the host refused the upload: $(describe_failure)" ;;
  esac

  sha="$(printf '%s' "${UP_BODY}" | jq -r '.sha256 // empty | ascii_downcase' 2>/dev/null || true)"
  size="$(printf '%s' "${UP_BODY}" | jq -r 'if (.sizeBytes | type) == "number" then .sizeBytes else empty end' 2>/dev/null || true)"
  if [[ -z "${SHA_RESULT}" || "${sha}" != "${SHA_RESULT}" || ! "${size}" =~ ^[0-9]+$ ]]; then
    # The host completed the backup already; delete it, it is not what was sent.
    SESSION_OPEN=1
    create_fail "${EXIT_CRYPTO}" failed verify-failed "local ${SHA_RESULT:-none}, host ${sha:-none}" \
      "the host stored something other than what was sent (SHA-256 mismatch); the backup was deleted"
  fi
  SESSION_OPEN=""
  PHASE=""
  rm -rf "${STAGE}"
  STAGE=""
  msg "backup ${id} stored on the host (${size} bytes)"
  jq -cn --arg id "${id}" --argjson size "${size}" --arg sha "${sha}" --arg trigger "${trigger}" \
    --argjson hosts "${hosts}" '{id:$id,sizeBytes:$size,sha256:$sha,trigger:$trigger,gitCredentialHosts:$hosts}'
}

# ── commands: download, fetch, restore ───────────────────────────────────────

validate_id() { [[ "$1" =~ ${ID_RE} ]] || usage_fail "a backup id is 32 lowercase hexadecimal characters"; }

cmd_download() {
  local id="${1:-}"
  [[ $# -eq 1 ]] || usage_fail "usage: construct backup download ID"
  validate_id "${id}"
  [[ ! -t 1 ]] || usage_fail "download writes binary data; redirect stdout to a file or pipe"
  need_tools jq sha256sum
  require_service
  api_call GET "$(backups_path)/${id}/keys"
  api_ok || api_fail "backup ${id}"
  if ! printf '%s' "${API_BODY}" | jq -e --arg id "${id}" \
      'type == "object" and .format == "construct-backup-keys/1" and .backupId == $id' >/dev/null 2>&1; then
    msg "the host's keys file for backup ${id} is not usable"
    exit "${EXIT_CRYPTO}"
  fi
  printf '%s\n%s\n' "${CONTAINER_MAGIC}" "$(printf '%s' "${API_BODY}" | jq -c .)" \
    || { msg "cannot write to stdout"; exit "${EXIT_TRANSFER}"; }
  download_payload "${id}" cat
  download_check "${id}" || exit "${EXIT_CRYPTO}"
  if [[ "${DL_CONSUMER_RC}" -ne 0 ]]; then msg "writing the backup to stdout failed"; exit "${EXIT_TRANSFER}"; fi
  msg "backup ${id} written to stdout"
}

# Download and decrypt backup <id> into <file>; E must be set.
fetch_into() { # <id> <file>
  local id="$1" out="$2"
  download_payload "${id}" decrypt_to "${out}"
  E=""
  if ! download_check "${id}"; then rm -f "${out}"; exit "${EXIT_CRYPTO}"; fi
  if [[ "${DL_CONSUMER_RC}" -ne 0 ]]; then
    rm -f "${out}"
    msg "backup ${id} could not be decrypted with the key on stdin"
    exit "${EXIT_CRYPTO}"
  fi
  if ! check_archive_id "${out}" "${id}"; then rm -f "${out}"; exit "${EXIT_CRYPTO}"; fi
}

cmd_fetch() {
  local id="" out=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --from-host) shift; [[ $# -gt 0 ]] || usage_fail "--from-host requires a backup id"; id="$1" ;;
      --out) shift; [[ $# -gt 0 ]] || usage_fail "--out requires a file"; out="$1" ;;
      *) usage_fail "unknown fetch option: $1" ;;
    esac
    shift
  done
  [[ -n "${id}" && -n "${out}" ]] || usage_fail "usage: construct backup fetch --from-host ID --out FILE"
  validate_id "${id}"
  need_tools age jq tar zstd sha256sum
  require_service
  read_identity
  acquire_lock
  ( umask 077; : >"${out}" ) || { msg "cannot write ${out}"; exit "${EXIT_STEP}"; }
  chmod 0600 "${out}"
  fetch_into "${id}" "${out}"
  msg "backup ${id} decrypted into ${out}"
}

run_restore_config() { # <archive>
  local script rc=0
  script="$(find_script restore-config.sh)" \
    || { msg "restore-config.sh not found (looked next to this script and in ${REPO_BIN})"; exit "${EXIT_STEP}"; }
  BACKUP_TGZ="$1" BACKUP_DIR="" bash "${script}" 9>&- >&2 || rc=$?
  if [[ "${rc}" -ne 0 ]]; then
    msg "restoring the config failed (restore-config.sh exit ${rc})"
    exit "${EXIT_STEP}"
  fi
}

cmd_restore() {
  local id="" mode="" archive
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --from-host) shift; [[ $# -gt 0 ]] || usage_fail "--from-host requires a backup id"; id="$1"; mode=host ;;
      --stdin) mode=stdin ;;
      --expect-id) shift; [[ $# -gt 0 ]] || usage_fail "--expect-id requires a backup id"; id="$1" ;;
      *) usage_fail "unknown restore option: $1" ;;
    esac
    shift
  done
  [[ -n "${mode}" ]] || usage_fail "usage: construct backup restore --from-host ID | --stdin --expect-id ID"
  [[ -n "${id}" ]] || usage_fail "restore --stdin needs --expect-id ID"
  validate_id "${id}"
  need_tools age jq tar zstd sha256sum
  if [[ "${mode}" == host ]]; then require_service; else ensure_work_dir; fi
  read_identity
  acquire_lock
  new_archive_path
  archive="${ARCHIVE_PATH}"
  if [[ "${mode}" == host ]]; then
    fetch_into "${id}" "${archive}"
  else
    if ! decrypt_to "${archive}"; then
      E=""
      msg "the payload on stdin could not be decrypted with the key on its first line"
      exit "${EXIT_CRYPTO}"
    fi
    E=""
    check_archive_id "${archive}" "${id}" || exit "${EXIT_CRYPTO}"
  fi
  msg "restoring backup ${id}"
  run_restore_config "${archive}"
  restore_extras "${archive}"
  rm -f "${archive}"
  if [[ "${mode}" == host ]]; then
    api_call POST "$(backups_path)/${id}/restored" '{}'
    api_ok || msg "warning: could not tell the host that backup ${id} was restored: $(describe_failure)"
  fi
  msg "backup ${id} restored"
}

# ── restore extras ───────────────────────────────────────────────────────────
# What the PC did with an extracted backup before; the guest does it now because
# the PC never sees the cleartext: project profiles that are missing here, the
# VS Code serve-web token, and the backup recipients -- all additive except the
# token, which is the point of keeping the same browser URL.

is_safe_profile_name() {
  local name="$1"
  [[ -n "${name}" && "${name}" != . && "${name}" != .. ]] || return 1
  case "${name}" in */*|*\\*|*..*) return 1 ;; esac
  [[ "${name}" != *[[:cntrl:]]* ]]
}

# Install one recipient key. mode add: (re)label an existing key; mode restore:
# never touch an existing key. Returns 3 when the recipient limit is reached.
install_recipient() { # <pem> <fp> <label> <addedAt or ""> <add|restore>
  local pem="$1" fp="$2" label="$3" added="$4" mode="$5"
  local -a enrolled=()
  mkdir -p "${RECIPIENTS_DIR}"
  chmod 0755 "${RECIPIENTS_DIR}" 2>/dev/null || true
  if [[ -f "${RECIPIENTS_DIR}/${fp}.pem" ]]; then
    [[ "${mode}" == add ]] || return 0
    added="$(recipient_meta "${fp}" addedAt)"
  else
    shopt -s nullglob
    enrolled=("${RECIPIENTS_DIR}"/*.pem)
    shopt -u nullglob
    (( ${#enrolled[@]} < MAX_RECIPIENTS )) || return 3
    install -m 0644 "${pem}" "${RECIPIENTS_DIR}/.${fp}.pem.tmp"
    mv -f "${RECIPIENTS_DIR}/.${fp}.pem.tmp" "${RECIPIENTS_DIR}/${fp}.pem"
  fi
  [[ -n "${added}" ]] || added="$(now_iso)"
  jq -n --arg label "${label}" --arg addedAt "${added}" '{label:$label,addedAt:$addedAt}' \
    >"${RECIPIENTS_DIR}/.${fp}.json.tmp"
  chmod 0644 "${RECIPIENTS_DIR}/.${fp}.json.tmp"
  mv -f "${RECIPIENTS_DIR}/.${fp}.json.tmp" "${RECIPIENTS_DIR}/${fp}.json"
}

# Is the PEM an RSA public key of at least 3072 bits? Prints its fingerprint.
validate_public_key() { # <pem>
  local text bits
  text="$(openssl rsa -pubin -in "$1" -noout -text 2>/dev/null)" || return 1
  [[ "${text}" =~ \(([0-9]+)\ bit\) ]] || return 1
  bits="${BASH_REMATCH[1]}"
  (( bits >= 3072 )) || return 2
  key_fingerprint "$1"
}

restore_extras() { # <archive>
  local archive="$1" tmp list member token_member="" recips_member="" projects_member=""
  local -a members=()
  tmp="$(mktemp -d "${STAGE_PARENT}/construct-backup-extras.XXXXXX")" \
    || { msg "cannot create a temporary directory in ${STAGE_PARENT}"; exit "${EXIT_STEP}"; }
  TMP_PATHS+=("${tmp}")
  list="${tmp}/members"
  if ! tar -tf "${archive}" >"${list}" 2>/dev/null; then
    msg "the backup archive ${archive} is unreadable"
    exit "${EXIT_STEP}"
  fi
  member="$(grep -Eom1 '^(\./)?projects/' "${list}" || true)"
  if [[ -n "${member}" ]]; then projects_member="${member%/}"; members+=("${projects_member}"); fi
  member="$(grep -Exm1 '(\./)?etc/construct/vscode-serve-web\.token' "${list}" || true)"
  if [[ -n "${member}" ]]; then token_member="${member}"; members+=("${token_member}"); fi
  member="$(grep -Eom1 '^(\./)?etc/construct/backup-recipients/' "${list}" || true)"
  if [[ -n "${member}" ]]; then recips_member="${member%/}"; members+=("${recips_member}"); fi
  if (( ${#members[@]} == 0 )); then return 0; fi
  mkdir -p "${tmp}/x"
  if ! tar -xf "${archive}" -C "${tmp}/x" "${members[@]}" 2>/dev/null; then
    msg "cannot extract the project profiles, serve-web token and recipients from ${archive}"
    exit "${EXIT_STEP}"
  fi

  local src base name fp label added rc
  if [[ -n "${projects_member}" ]]; then
    shopt -s nullglob
    for src in "${tmp}/x/projects"/*.json; do
      base="$(basename "${src}")"
      name="${base%.json}"
      [[ "${base}" == project.schema.json ]] && continue
      is_safe_profile_name "${name}" || continue
      [[ -e "${PROJECTS_STORE}/${base}" ]] && continue
      # A profile deleted on this VM and not yet synced stays deleted.
      [[ -e "${PROJECTS_STORE}/.deleted/${name}" ]] && continue
      if ! jq -e 'type == "object"' "${src}" >/dev/null 2>&1; then
        msg "warning: skipping the unreadable project profile ${name}"
        continue
      fi
      mkdir -p "${PROJECTS_STORE}"
      if install -m 0644 "${src}" "${PROJECTS_STORE}/.${base}.tmp" \
          && mv -f "${PROJECTS_STORE}/.${base}.tmp" "${PROJECTS_STORE}/${base}"; then
        msg "restored project profile ${name}"
      else
        msg "warning: could not restore the project profile ${name}"
      fi
    done
    shopt -u nullglob
  fi

  if [[ -n "${token_member}" && -s "${tmp}/x/etc/construct/vscode-serve-web.token" ]]; then
    src="${tmp}/x/etc/construct/vscode-serve-web.token"
    if [[ -f "${SW_TOKEN_FILE}" ]] && cmp -s "${src}" "${SW_TOKEN_FILE}"; then
      :
    elif mkdir -p "$(dirname "${SW_TOKEN_FILE}")" \
        && install -m 0600 "${src}" "${SW_TOKEN_FILE}.tmp" \
        && mv -f "${SW_TOKEN_FILE}.tmp" "${SW_TOKEN_FILE}"; then
      msg "restored the VS Code serve-web token"
      "${SYSTEMCTL}" try-restart "${SERVE_WEB_UNIT}" >/dev/null 2>&1 \
        || msg "warning: could not restart ${SERVE_WEB_UNIT}; the old browser URL works after its next start"
    else
      rm -f "${SW_TOKEN_FILE}.tmp"
      msg "warning: could not restore the VS Code serve-web token"
    fi
  fi

  if [[ -n "${recips_member}" ]]; then
    shopt -s nullglob
    for src in "${tmp}/x/etc/construct/backup-recipients"/*.pem; do
      fp="$(basename "${src}" .pem)"
      [[ "${fp}" =~ ${FP_RE} ]] || continue
      [[ -f "${RECIPIENTS_DIR}/${fp}.pem" ]] && continue
      if [[ "$(validate_public_key "${src}" || true)" != "${fp}" ]]; then
        msg "warning: skipping the backup recipient ${fp}: not a valid key"
        continue
      fi
      label=""; added=""
      if [[ -f "${tmp}/x/etc/construct/backup-recipients/${fp}.json" ]]; then
        label="$(jq -r 'if (.label | type) == "string" then .label else empty end' \
          "${tmp}/x/etc/construct/backup-recipients/${fp}.json" 2>/dev/null || true)"
        added="$(jq -r 'if (.addedAt | type) == "string" then .addedAt else empty end' \
          "${tmp}/x/etc/construct/backup-recipients/${fp}.json" 2>/dev/null || true)"
      fi
      [[ "${label}" =~ ${LABEL_RE} ]] || label="PC"
      [[ "${added}" =~ ^[0-9T:.+Z-]{1,40}$ ]] || added=""
      rc=0
      install_recipient "${src}" "${fp}" "${label}" "${added}" restore || rc=$?
      case "${rc}" in
        0) msg "restored the backup recipient ${label} (${fp})" ;;
        3) msg "warning: not restoring the backup recipient ${fp}: ${MAX_RECIPIENTS} are enrolled already" ;;
        *) msg "warning: could not restore the backup recipient ${fp}" ;;
      esac
    done
    shopt -u nullglob
  fi
}

cmd_restore_extras() {
  [[ $# -eq 1 ]] || usage_fail "usage: construct backup restore-extras ARCHIVE"
  [[ -f "$1" ]] || usage_fail "no such archive: $1"
  need_tools tar zstd jq openssl
  acquire_lock
  restore_extras "$1"
}

cmd_restored() {
  [[ $# -eq 1 ]] || usage_fail "usage: construct backup restored ID"
  validate_id "$1"
  need_tools jq
  require_service
  api_call POST "$(backups_path)/$1/restored" '{}'
  api_ok || api_fail "backup $1"
}

# ── commands: host queries ───────────────────────────────────────────────────

cmd_get() { # <list|status>
  local path
  path="$(backups_path)"
  if [[ "$1" == status ]]; then path="${path}/status"; fi
  need_tools jq
  require_service
  api_call GET "${path}"
  if ! api_ok; then
    if [[ "${API_STATUS}" == 404 || "${API_STATUS}" == 405 ]]; then
      msg "the host service does not offer config backups"
      exit "${EXIT_NO_SERVICE}"
    fi
    api_fail "backup $1"
  fi
  if ! printf '%s' "${API_BODY}" | jq -c . 2>/dev/null; then
    msg "the host answered with something that is not JSON"
    exit "${EXIT_TRANSFER}"
  fi
}

# ── commands: recipients ─────────────────────────────────────────────────────

cmd_recipients() {
  local sub="${1:-}"
  if [[ $# -gt 0 ]]; then shift; fi
  need_tools openssl jq sha256sum
  case "${sub}" in
    list)
      [[ $# -eq 0 ]] || usage_fail "usage: construct backup recipients list"
      local pem fp
      {
        if [[ -d "${RECIPIENTS_DIR}" ]]; then
          shopt -s nullglob
          for pem in "${RECIPIENTS_DIR}"/*.pem; do
            fp="$(basename "${pem}" .pem)"
            [[ "${fp}" =~ ${FP_RE} ]] || continue
            jq -cn --arg fp "${fp}" --arg label "$(recipient_meta "${fp}" label)" --arg added "$(recipient_meta "${fp}" addedAt)" \
              '{fingerprint:$fp,label:$label,addedAt:(if $added == "" then null else $added end)}'
          done
          shopt -u nullglob
        fi
      } | jq -cs 'sort_by(.addedAt // "", .fingerprint)'
      ;;
    add)
      local label="" have_label="" in fp rc=0
      while [[ $# -gt 0 ]]; do
        case "$1" in
          --label) shift; [[ $# -gt 0 ]] || usage_fail "--label requires a value"; label="$1"; have_label=1 ;;
          *) usage_fail "unknown recipients add option: $1" ;;
        esac
        shift
      done
      [[ -n "${have_label}" ]] || usage_fail "usage: construct backup recipients add --label LABEL (PEM on stdin)"
      [[ "${label}" =~ ${LABEL_RE} ]] || usage_fail "a label is 1-64 characters from A-Z, a-z, 0-9, dot, underscore, space and hyphen"
      ensure_work_dir
      in="${WORK_DIR}/recipient.pem"
      head -c 65536 >"${in}"
      fp="$(validate_public_key "${in}")" || rc=$?
      case "${rc}" in
        0) ;;
        2) usage_fail "the key is too short: backup recipients need RSA keys of at least 3072 bits" ;;
        *) usage_fail "stdin is not an RSA public key in PEM form (SubjectPublicKeyInfo)" ;;
      esac
      openssl pkey -pubin -in "${in}" -out "${WORK_DIR}/canonical.pem" 2>/dev/null \
        || usage_fail "stdin is not an RSA public key in PEM form (SubjectPublicKeyInfo)"
      rc=0
      install_recipient "${WORK_DIR}/canonical.pem" "${fp}" "${label}" "" add || rc=$?
      case "${rc}" in
        0) ;;
        3) usage_fail "${MAX_RECIPIENTS} recipients are enrolled already; remove one first" ;;
        *) msg "cannot write the recipient to ${RECIPIENTS_DIR}"; exit "${EXIT_STEP}" ;;
      esac
      msg "enrolled ${label} (${fp})"
      printf '%s\n' "${fp}"
      ;;
    remove)
      [[ $# -eq 1 ]] || usage_fail "usage: construct backup recipients remove FINGERPRINT"
      [[ "$1" =~ ${FP_RE} ]] || usage_fail "a fingerprint is 64 lowercase hexadecimal characters"
      if [[ ! -e "${RECIPIENTS_DIR}/$1.pem" && ! -e "${RECIPIENTS_DIR}/$1.json" ]]; then
        msg "no recipient $1 is enrolled"
        return 0
      fi
      rm -f "${RECIPIENTS_DIR}/$1.pem" "${RECIPIENTS_DIR}/$1.json"
      msg "removed the recipient $1; backups made from now on are no longer readable with its key"
      ;;
    -h|--help) usage ;;
    "") usage >&2; exit "${EXIT_USAGE}" ;;
    *) usage_fail "unknown recipients command: ${sub} (list, add, remove)" ;;
  esac
}

cmd_estimate() {
  [[ $# -eq 0 ]] || usage_fail "usage: construct backup estimate"
  need_tools jq
  if ! estimate_space; then
    msg "cannot read the free space of ${STAGE_PARENT}"
    exit "${EXIT_STEP}"
  fi
  jq -cn --argjson need "${SPACE_NEED}" --argjson free "${SPACE_FREE}" --argjson reserve "${SPACE_RESERVE}" \
    --argjson reflink "${SPACE_REFLINK}" --argjson ok "${SPACE_OK}" \
    '{needBytes:$need,freeBytes:$free,reserveBytes:$reserve,reflink:$reflink,ok:$ok}'
}

# ── dispatch ─────────────────────────────────────────────────────────────────

if [[ $# -eq 0 ]]; then usage >&2; exit "${EXIT_USAGE}"; fi
command="$1"
shift
case "${command}" in
  -h|--help|help) usage; exit 0 ;;
esac
require_root
case "${command}" in
  create) cmd_create "$@" ;;
  download) cmd_download "$@" ;;
  fetch) cmd_fetch "$@" ;;
  restore) cmd_restore "$@" ;;
  restore-extras) cmd_restore_extras "$@" ;;
  restored) cmd_restored "$@" ;;
  list|status) [[ $# -eq 0 ]] || usage_fail "usage: construct backup ${command}"; cmd_get "${command}" ;;
  recipients) cmd_recipients "$@" ;;
  estimate) cmd_estimate "$@" ;;
  *) usage_fail "unknown command: ${command} (try: construct backup --help)" ;;
esac
exit 0
