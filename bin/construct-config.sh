#!/usr/bin/env bash
# construct-config.sh — implementation of `construct config` (see `construct config --help`).
#
# Reads and changes the few per-VM settings in /etc/construct/config.env that an
# agent on this VM may change. Everything else in that file is written by
# provisioning, by the host service or by the user's PC, and stays out of reach:
# only the keys in ALLOWED below are accepted. An entry belongs there only when
# it is a convenience for this one VM with no security impact. Settings of the
# host service, and anything that grants access, points the VM at a service or
# carries credentials (CONSTRUCT_SERVICE_URL, tokens, certificates, users), are
# never allowlisted.
#
# A write is a read-modify-rename under a lock on the configuration directory:
# the file is copied (mode and owner kept), the copy is edited with
# bin/config-set.sh (the renderer every other writer uses) and renamed over the
# original, so readers never see a half-written file.
#
# Usage:
#   construct config list
#   construct config get   <key>
#   construct config set   <key> <value>
#   construct config unset <key>
#
# Exit codes:
#   0  ok
#   1  usage or local error (the configuration cannot be written)
#   2  unknown key or invalid value
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
CONSTRUCT_REPO_DIR="${CONSTRUCT_REPO_DIR:-/opt/construct/repo}"

# <key>|<config.env name>|<validator>. For each validator, valid_<validator>
# prints the normalized value or fails, and hint_<validator> says what it accepts.
ALLOWED=(
  "t3-proxy-url|T3CODE_PROXY_URL|origin"
)

EXIT_INVALID=2

die() { printf 'construct config: %s\n' "$*" >&2; exit 1; }
invalid() { printf 'construct config: %s\n' "$*" >&2; exit "${EXIT_INVALID}"; }

usage() {
  cat <<'USAGE'
Usage: construct config list
       construct config get   <key>
       construct config set   <key> <value>
       construct config unset <key>

Per-VM settings an agent may change, kept in /etc/construct/config.env. Only
the keys below are accepted; host settings and anything that grants access are
not changeable from the VM.

Keys:
  t3-proxy-url   The address the user reaches this VM's T3 Code at through
                 their own reverse proxy, e.g. https://t3.example.net:8443
                 (http:// works too; no path, query or fragment). T3 Code's
                 pairing links and the phone pairing QR code use it first; the
                 user sees and changes it in the Construct panel under
                 Settings → Access & services. config.env: T3CODE_PROXY_URL

Commands:
  list    Every key with its current value (empty when not set).
  get     Print the key's value; prints nothing when it is not set.
  set     Check and store a value. New pairing links use it right away.
  unset   Remove the value.

Exit codes:
  0  ok
  1  usage or local error (the configuration cannot be written)
  2  unknown key or invalid value
USAGE
}

# An http(s) origin: scheme, a host name, IPv4 or bracketed IPv6 literal, and an
# optional port. One trailing slash is dropped; a path, query, fragment or user
# name is refused. The same shape the Companion and the pairing scripts accept.
valid_origin() {
  local v="$1" port
  v="${v#"${v%%[![:space:]]*}"}"
  v="${v%"${v##*[![:space:]]}"}"
  v="${v%/}"
  [[ "${v}" =~ ^https?://([A-Za-z0-9._-]+|\[[0-9A-Fa-f:.]+\])(:([0-9]{1,5}))?$ ]] || return 1
  port="${BASH_REMATCH[3]}"
  if [[ -n "${port}" ]]; then (( 10#${port} >= 1 && 10#${port} <= 65535 )) || return 1; fi
  printf '%s' "${v}"
}
hint_origin() { printf 'an http(s) address without a path, query or fragment, e.g. https://t3.example.net:8443'; }

# entry <key> → sets ENV_NAME and VALIDATOR, or fails with exit 2.
entry() {
  local key="$1" item
  for item in "${ALLOWED[@]}"; do
    if [[ "${item%%|*}" == "${key}" ]]; then
      item="${item#*|}"
      ENV_NAME="${item%%|*}"
      VALIDATOR="${item#*|}"
      return 0
    fi
  done
  invalid "unknown key '${key}' (keys: $(keys))"
}
keys() { local item out=""; for item in "${ALLOWED[@]}"; do out="${out}${out:+, }${item%%|*}"; done; printf '%s' "${out}"; }

# Undo config-set.sh's rendering: values outside its safe set are single-quoted
# with embedded apostrophes as '\'' (an IPv6 address has brackets, so it is quoted).
read_value() {
  local v
  [[ -r "${CONFIG_FILE}" ]] || return 0
  v="$(sed -n "s/^$1=//p" "${CONFIG_FILE}" | head -1)"
  case "${v}" in
    "'"*"'") v="${v#\'}"; v="${v%\'}"; v="${v//\'\\\'\'/\'}" ;;
  esac
  printf '%s' "${v}"
}

find_renderer() {
  local dir candidate
  dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  for candidate in "${dir}/config-set.sh" "${CONSTRUCT_REPO_DIR}/bin/config-set.sh"; do
    if [[ -f "${candidate}" ]]; then printf '%s' "${candidate}"; return 0; fi
  done
  return 1
}

# write <name> <value>|--unset
TMP_FILE=""
write() {
  local name="$1" value="$2" dir renderer rc=0
  dir="$(dirname "${CONFIG_FILE}")"
  renderer="$(find_renderer)" || die "config-set.sh not found (looked next to this script and in ${CONSTRUCT_REPO_DIR}/bin)"
  mkdir -p "${dir}" 2>/dev/null || die "cannot create ${dir} (run as root)"
  # Lock the directory, not the file: the file is replaced by a rename, and a lock
  # on the old inode would not keep out a writer that opened the new one.
  exec 9<"${dir}" || die "cannot open ${dir}"
  flock 9 2>/dev/null || true
  TMP_FILE="$(mktemp "${dir}/.config.env.XXXXXX" 2>/dev/null)" || die "cannot write to ${dir} (run as root)"
  trap 'rm -f "${TMP_FILE}"' EXIT
  if [[ -f "${CONFIG_FILE}" ]]; then
    cp -p "${CONFIG_FILE}" "${TMP_FILE}" || die "cannot copy ${CONFIG_FILE}"
  fi
  if [[ "${value}" == "--unset" ]]; then
    # grep exits 1 when nothing is left, 2 when it could not read the file.
    grep -v "^${name}=" "${CONFIG_FILE}" >"${TMP_FILE}" || rc=$?
    (( rc <= 1 )) || die "cannot read ${CONFIG_FILE}"
  else
    bash "${renderer}" "${TMP_FILE}" "${name}" "${value}" || die "cannot update ${CONFIG_FILE}"
  fi
  mv -f "${TMP_FILE}" "${CONFIG_FILE}" || die "cannot replace ${CONFIG_FILE} (run as root)"
  trap - EXIT
}

cmd="${1:-}"
case "${cmd}" in
  ""|-h|--help|help)
    if [[ -z "${cmd}" ]]; then usage >&2; exit 1; fi
    usage; exit 0 ;;
  list)
    [[ $# -eq 1 ]] || die "usage: construct config list"
    for item in "${ALLOWED[@]}"; do
      name="${item#*|}"; name="${name%%|*}"
      printf '%s=%s\n' "${item%%|*}" "$(read_value "${name}")"
    done ;;
  get)
    [[ $# -eq 2 ]] || die "usage: construct config get <key>"
    entry "$2"
    value="$(read_value "${ENV_NAME}")"
    if [[ -n "${value}" ]]; then printf '%s\n' "${value}"; fi ;;
  set)
    [[ $# -eq 3 ]] || die "usage: construct config set <key> <value> (construct config unset <key> removes it)"
    entry "$2"
    value="$("valid_${VALIDATOR}" "$3")" || invalid "$2 must be $("hint_${VALIDATOR}")"
    write "${ENV_NAME}" "${value}"
    printf '%s=%s\n' "$2" "${value}" ;;
  unset)
    [[ $# -eq 2 ]] || die "usage: construct config unset <key>"
    entry "$2"
    if grep -q "^${ENV_NAME}=" "${CONFIG_FILE}" 2>/dev/null; then write "${ENV_NAME}" --unset; fi ;;
  *) die "unknown command '${cmd}' (try: construct config --help)" ;;
esac
