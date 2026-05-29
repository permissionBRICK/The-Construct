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
SSHD_DROPIN="/etc/ssh/sshd_config.d/99-construct-root-pubkey.conf"

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

touch /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys

public_key="$(tr -d '\n' <"${KEY_PATH}.pub")"
if ! grep -qxF "${public_key}" /root/.ssh/authorized_keys; then
  printf '%s\n' "${public_key}" >>/root/.ssh/authorized_keys
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

Host: $(hostname).mshome.net
User: root
Private key path on VM: ${KEY_PATH}

Copy the full private key below, including BEGIN and END lines.
Save it on the host machine as an OpenSSH private key file.
Keep it secret. Anyone with this key can log in as root on this VM.

EOF

cat "${KEY_PATH}"

cat <<EOF

Public key:
${public_key}

Example host-side SSH config:

Host $(hostname)-root
  HostName $(hostname).mshome.net
  User root
  IdentityFile /path/to/saved/codex_app_ed25519

Then test from the host:

ssh -i /path/to/saved/codex_app_ed25519 root@$(hostname).mshome.net
============================================================

EOF
