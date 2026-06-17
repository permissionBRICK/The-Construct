#!/usr/bin/env bash
#
# Set up a Samba/SMB server on the VM that exposes the workspace (WORKSPACE_ROOT,
# default /root/repos) to the host PC. The host authenticates as an SMB user
# (default `dev`) but all file operations on the share run as root (force user =
# root) -- so the host sees and edits the repos exactly as the coding agents do.
#
# Credentials are GENERATED ONCE and persisted into config.env (SMB_USER /
# SMB_PASSWORD / SMB_SHARE_NAME). A re-provision reuses the stored values, so the
# host's saved mapping (net use ... /savecred /persistent:yes) keeps working
# without re-entering anything. Delete SMB_PASSWORD from config.env (or pass a
# new one in the environment) to rotate it.
#
# Idempotent; safe to re-run. Run as root.
#
# Inputs (all via environment, with config.env / defaults as fallback):
#   SMB_SHARE        enable the share         (default true)
#   SMB_USER         SMB auth username        (default dev)
#   SMB_SHARE_NAME   share name in the UNC    (default repo)
#   SMB_PASSWORD     SMB password             (default: reuse stored, else generated)
#   WORKSPACE_ROOT   directory to share       (default /root/repos)
#   CONFIG_FILE      construct config.env     (default /etc/construct/config.env)
#   REPO_DIR         uploaded repo            (default /opt/construct/repo)
#
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

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
STATUS_FILE="${SMB_STATUS_FILE:-/etc/construct/smb-status}"
SHARES_CONF="/etc/samba/construct-shares.conf"
SMB_CONF="/etc/samba/smb.conf"

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash ${REPO_DIR}/bin/setup-smb-share.sh"
  exit 1
fi

# Read a single config.env value without `source`-ing the file (so a passed-in
# environment variable of the same name isn't clobbered, and a malformed line
# elsewhere can't abort us). config-set.sh writes our values bare (safe charset),
# so no unquoting is needed.
read_cfg() {
  [[ -f "${CONFIG_FILE}" ]] || return 0
  sed -n "s/^$1=//p" "${CONFIG_FILE}" | head -1
}

# Precedence for each setting: explicit environment (from provision.sh / the host
# param) > value saved in config.env > built-in default. Mirrors how provision.sh
# resolves VSCODE_TUNNEL.
SMB_SHARE="${SMB_SHARE:-$(read_cfg SMB_SHARE)}";           SMB_SHARE="${SMB_SHARE:-true}"
SMB_USER="${SMB_USER:-$(read_cfg SMB_USER)}";              SMB_USER="${SMB_USER:-dev}"
SMB_SHARE_NAME="${SMB_SHARE_NAME:-$(read_cfg SMB_SHARE_NAME)}"; SMB_SHARE_NAME="${SMB_SHARE_NAME:-repo}"
SMB_PASSWORD="${SMB_PASSWORD:-$(read_cfg SMB_PASSWORD)}"

cfg() { bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" "$1" "$2"; }

# Generate a stable, host-friendly password: alphanumeric only (no quoting needed
# in config.env or in the host's `net use` command line). Prefer openssl (a single
# command, so no pipe to SIGPIPE under `set -o pipefail`); fall back to /dev/urandom
# with the closing-pipe failure tolerated.
gen_password() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 18    # 36 hex chars
  else
    LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c 24 || true
  fi
}

lan_ip() {
  local ip
  ip="$(ip -o -4 route get 1.1.1.1 2>/dev/null | awk '{for (i=1; i<=NF; i++) if ($i == "src") {print $(i+1); exit}}' || true)"
  [[ -n "${ip}" ]] || ip="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
  printf '%s' "${ip:-unknown}"
}

write_status() {
  install -d -m 0755 "$(dirname "${STATUS_FILE}")"
  {
    printf 'SMB_ENABLED=%s\n'    "$1"
    printf 'SMB_USER=%s\n'       "${SMB_USER}"
    printf 'SMB_SHARE_NAME=%s\n' "${SMB_SHARE_NAME}"
    printf 'SMB_PASSWORD=%s\n'   "${SMB_PASSWORD}"
    printf 'SMB_PATH=%s\n'       "${WORKSPACE_ROOT}"
    printf 'SMB_IP=%s\n'         "$(lan_ip)"
    printf 'SMB_DNS=%s\n'        "$(hostname).mshome.net"
  } >"${STATUS_FILE}" 2>/dev/null || true
  # Holds the SMB password -> readable only by root (the host reads it via sudo).
  chmod 0600 "${STATUS_FILE}" 2>/dev/null || true
}

# ── Disabled path ────────────────────────────────────────────────────────────
# When the share is turned off, persist the toggle, tear down our service and
# share definition, and leave a status file the host can read (so it skips the
# host-side mount). Don't generate or keep a password while disabled.
if [[ "${SMB_SHARE}" != "true" ]]; then
  step "SMB share disabled (SMB_SHARE=${SMB_SHARE})"
  cfg SMB_SHARE "false"
  if systemctl list-unit-files smbd.service >/dev/null 2>&1; then
    systemctl disable --now smbd 2>/dev/null || true
  fi
  systemctl disable --now nmbd 2>/dev/null || true
  if [[ -f "${SHARES_CONF}" ]]; then
    rm -f "${SHARES_CONF}"
    note "removed ${SHARES_CONF}"
  fi
  SMB_PASSWORD=""
  write_status "no"
  ok "SMB share is off"
  exit 0
fi

step "Setting up SMB share of ${WORKSPACE_ROOT} for the host"

# 1. Generate a stable password the first time, then persist everything so a
#    re-provision reuses the same credentials.
if [[ -z "${SMB_PASSWORD}" ]]; then
  SMB_PASSWORD="$(gen_password)"
  note "generated a new SMB password"
else
  note "reusing the stored SMB password"
fi
cfg SMB_SHARE      "true"
cfg SMB_USER       "${SMB_USER}"
cfg SMB_SHARE_NAME "${SMB_SHARE_NAME}"
cfg SMB_PASSWORD   "${SMB_PASSWORD}"

# 2. Install Samba if missing.
if ! dpkg -s samba >/dev/null 2>&1; then
  step "Installing samba"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y samba
else
  note "samba already installed"
fi

# 3. The SMB auth user must map to a real (login-less) system account. File
#    access on the share runs as root regardless (force user = root below), so
#    this account needs no privileges of its own.
if ! id "${SMB_USER}" >/dev/null 2>&1; then
  step "Creating SMB user ${SMB_USER}"
  useradd --system --no-create-home --shell /usr/sbin/nologin "${SMB_USER}"
else
  note "system user ${SMB_USER} already exists"
fi

# 4. Set (or update) the Samba password for the user and enable the account.
#    smbpasswd -a is idempotent: it resets the password if the user already
#    exists in the Samba database, so a rotated SMB_PASSWORD takes effect here.
step "Setting Samba password for ${SMB_USER}"
printf '%s\n%s\n' "${SMB_PASSWORD}" "${SMB_PASSWORD}" | smbpasswd -s -a "${SMB_USER}" >/dev/null
smbpasswd -e "${SMB_USER}" >/dev/null 2>&1 || true

# 5. Make sure the shared directory exists.
install -d -m 0755 "${WORKSPACE_ROOT}"

# 6. Write the share definition to an included file (rewritten verbatim each run)
#    and make smb.conf include it exactly once. force user/group = root means the
#    host operates on the repos as root -- the same identity the agents use.
step "Writing share [${SMB_SHARE_NAME}] -> ${WORKSPACE_ROOT}"
cat >"${SHARES_CONF}" <<EOF
# Managed by construct (bin/setup-smb-share.sh). Do not edit by hand.
[${SMB_SHARE_NAME}]
   comment = Construct workspace (host access as root)
   path = ${WORKSPACE_ROOT}
   browseable = yes
   read only = no
   guest ok = no
   valid users = ${SMB_USER}
   force user = root
   force group = root
   create mask = 0644
   directory mask = 0755
EOF
chmod 0644 "${SHARES_CONF}"

INCLUDE_LINE="include = ${SHARES_CONF}"
if ! grep -qxF "   ${INCLUDE_LINE}" "${SMB_CONF}" 2>/dev/null \
   && ! grep -qxF "${INCLUDE_LINE}" "${SMB_CONF}" 2>/dev/null; then
  printf '\n# Construct managed share include\n%s\n' "${INCLUDE_LINE}" >>"${SMB_CONF}"
  note "added include directive to ${SMB_CONF}"
fi

# 7. Validate the resulting configuration before (re)starting the service.
if command -v testparm >/dev/null 2>&1; then
  if ! testparm -s "${SMB_CONF}" >/dev/null 2>&1; then
    warn "WARNING: 'testparm' reported issues with the Samba configuration"
  fi
fi

# 8. Open the firewall for SMB if ufw is active (no-op on the default open VM).
#    Capture the status into a variable first -- piping `ufw status` into `grep -q`
#    would SIGPIPE ufw and, under `set -o pipefail`, misreport the result.
if command -v ufw >/dev/null 2>&1; then
  _ufw_status="$(ufw status 2>/dev/null || true)"
  if [[ "${_ufw_status}" == *"Status: active"* ]]; then
    ufw allow Samba >/dev/null 2>&1 || ufw allow 445/tcp >/dev/null 2>&1 || true
    note "opened the firewall for SMB (ufw)"
  fi
fi

# 9. Enable + (re)start the service so it survives the post-provision reboot and
#    picks up the share now. nmbd (NetBIOS name service) is optional -- start it
#    when present so \\hostname browsing works, but don't fail without it.
step "Enabling and starting smbd"
systemctl enable smbd >/dev/null 2>&1 || true
systemctl restart smbd
if systemctl list-unit-files nmbd.service >/dev/null 2>&1; then
  systemctl enable nmbd >/dev/null 2>&1 || true
  systemctl restart nmbd 2>/dev/null || true
fi

# 10. Publish the connection details for the host (read back by Provision-AgentVM.ps1).
write_status "yes"

ok "SMB share ready: \\\\$(hostname).mshome.net\\${SMB_SHARE_NAME}  (user ${SMB_USER}, access as root)"
