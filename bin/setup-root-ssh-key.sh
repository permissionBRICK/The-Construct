#!/usr/bin/env bash
set -euo pipefail

# Colourised logging helpers. Emit ANSI colour when either stream is a terminal
# or the caller forces it (the SSH provisioning stream sets FORCE_COLOR/
# CLICOLOR_FORCE, which child processes inherit); otherwise stay plain so
# redirected/piped logs aren't littered with escape codes.
if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_STEP=$'\033[1;36m'   # bold cyan - step headers
  _C_OK=$'\033[32m'       # green     - completion / success
  _C_WARN=$'\033[33m'     # yellow    - warnings (run continues)
  _C_ERR=$'\033[31m'      # red       - fatal errors (before exit)
  _C_DIM=$'\033[2m'       # dim       - idempotent "nothing to do" / detail
  _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_ERR=''; _C_DIM=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n'     "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n'     "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n'     "${_C_ERR}"  "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n'     "${_C_DIM}"  "$*" "${_C_RESET}"; }

KEY_PATH="${ROOT_SSH_KEY_PATH:-/root/.ssh/codex_app_ed25519}"
KEY_COMMENT="${ROOT_SSH_KEY_COMMENT:-root@$(hostname) codex-app}"
SSHD_DROPIN="${SSHD_DROPIN:-/etc/ssh/sshd_config.d/99-construct-root-pubkey.conf}"
ROOT_SSH_AUTHORIZED_KEYS="${ROOT_SSH_AUTHORIZED_KEYS:-/root/.ssh/authorized_keys}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
_external_host_override="${CONSTRUCT_EXTERNAL_HOST:-}"
_external_ssh_port_override="${CONSTRUCT_EXTERNAL_SSH_PORT:-}"

# Resolve the client-reachable host and SSH port from config.env/env.
# CONSTRUCT_EXTERNAL_HOST takes precedence over the Hyper-V DNS fallback.
# CONSTRUCT_EXTERNAL_SSH_PORT is included in SSH-shaped outputs when != 22.
# Read ONLY the two identity keys -- never source config.env here: a sourced file
# could override KEY_PATH & co. and would export unrelated values into the
# privileged commands (ssh-keygen, sshd, systemctl) below.
# Undo config-set.sh's rendering: values outside its safe set are written as
# '...' with embedded apostrophes as '\'' -- the sed reader must decode that or a
# reprovision would carry the quote characters into the value.
_cfg_unquote() {
  local v="$1"
  if [[ ${#v} -ge 2 && "${v}" == \'*\' ]]; then
    v="${v:1:${#v}-2}"
    v="${v//\'\\\'\'/\'}"
  fi
  printf '%s' "${v}"
}
_read_cfg_key() {
  [[ -f "${CONFIG_FILE}" ]] || return 0
  _cfg_unquote "$(sed -n "s/^$1=//p" "${CONFIG_FILE}" | head -1 || true)"
}
CONSTRUCT_EXTERNAL_HOST="${_external_host_override:-$(_read_cfg_key CONSTRUCT_EXTERNAL_HOST)}"
CONSTRUCT_EXTERNAL_SSH_PORT="${_external_ssh_port_override:-$(_read_cfg_key CONSTRUCT_EXTERNAL_SSH_PORT)}"
CONSTRUCT_EXTERNAL_HOST="${CONSTRUCT_EXTERNAL_HOST:-}"
CONSTRUCT_EXTERNAL_SSH_PORT="${CONSTRUCT_EXTERNAL_SSH_PORT:-22}"
external_host="${CONSTRUCT_EXTERNAL_HOST:-$(hostname).mshome.net}"
ssh_port="${CONSTRUCT_EXTERNAL_SSH_PORT}"

# Snippet-only mode (--snippet flag): print just the SSH config snippet and exit
# without generating keys, updating authorized_keys, or managing sshd. Used by
# tests and diagnostics. This is a positional flag (not an env var) so it cannot
# silently leak through the environment into production paths.
_snippet_only=false
for _arg in "$@"; do
  [[ "${_arg}" == "--snippet" ]] && _snippet_only=true
done
if [[ "${_snippet_only}" == "true" ]]; then
  printf '\nHost %s-root\n' "$(hostname)"
  printf '  HostName %s\n' "${external_host}"
  printf '  User root\n'
  printf '  IdentityFile /path/to/saved/codex_app_ed25519\n'
  if [[ "${ssh_port}" != "22" ]]; then
    printf '  Port %s\n' "${ssh_port}"
  fi
  exit 0
fi

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo /opt/construct/repo/bin/setup-root-ssh-key.sh"
  exit 1
fi

mkdir -p /root/.ssh
chmod 700 /root/.ssh

if [[ -e "${KEY_PATH}" ]]; then
  note "Root SSH key already exists: ${KEY_PATH}"
else
  step "Generating root SSH key: ${KEY_PATH}"
  ssh-keygen -t ed25519 -N "" -C "${KEY_COMMENT}" -f "${KEY_PATH}"
fi

touch "${ROOT_SSH_AUTHORIZED_KEYS}"
chmod 600 "${ROOT_SSH_AUTHORIZED_KEYS}"

public_key="$(tr -d '\n' <"${KEY_PATH}.pub")"
if ! grep -qxF "${public_key}" "${ROOT_SSH_AUTHORIZED_KEYS}"; then
  printf '%s\n' "${public_key}" >>"${ROOT_SSH_AUTHORIZED_KEYS}"
fi

mkdir -p /etc/ssh/sshd_config.d
cat >"${SSHD_DROPIN}" <<'EOF'
# Managed by construct setup.
# Allows root SSH login with public keys while keeping password root login disabled.
PubkeyAuthentication yes
PermitRootLogin prohibit-password
AuthorizedKeysFile .ssh/authorized_keys .ssh/authorized_keys2
EOF
chmod 0644 "${SSHD_DROPIN}"

if command -v sshd >/dev/null 2>&1; then
  sshd -t
fi

if systemctl list-unit-files ssh.service >/dev/null 2>&1; then
  systemctl enable ssh
  systemctl restart ssh
elif systemctl list-unit-files sshd.service >/dev/null 2>&1; then
  systemctl enable sshd
  systemctl restart sshd
else
  warn "WARNING: no ssh.service or sshd.service found. Install openssh-server if SSH is unavailable."
fi

cat <<EOF

============================================================
Root SSH key for Codex App

Host: ${external_host}
User: root
Private key path on VM: ${KEY_PATH}

Copy the full private key below, including BEGIN and END lines.
Save it on the host machine as an OpenSSH private key file.
Keep it secret. Anyone with this key can log in as root on this VM.

EOF

cat "${KEY_PATH}"

# Build the SSH config snippet; include Port only when non-standard.
_port_config_line=""
_port_test_flag=""
if [[ "${ssh_port}" != "22" ]]; then
  _port_config_line="  Port ${ssh_port}"
  _port_test_flag=" -p ${ssh_port}"
fi

cat <<EOF

Public key:
${public_key}

Example host-side SSH config:

Host $(hostname)-root
  HostName ${external_host}
  User root
  IdentityFile /path/to/saved/codex_app_ed25519${_port_config_line:+
${_port_config_line}}

Then test from the host:

ssh -i /path/to/saved/codex_app_ed25519${_port_test_flag} root@${external_host}
============================================================

EOF
