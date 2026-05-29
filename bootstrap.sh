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

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash bootstrap.sh"
  exit 1
fi

AGENT_HOME="${AGENT_HOME:-/opt/construct}"
REPO_DIR="${REPO_DIR:-${AGENT_HOME}/repo}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
CONFIG_DIR="/etc/construct"
CONFIG_FILE="${CONFIG_DIR}/config.env"
RUNTIME_DIR="${AGENT_HOME}/runtime"
TARGET_USER="${SUDO_USER:-root}"

step "Checking OS"
if command -v lsb_release >/dev/null 2>&1; then
  lsb_release -a || true
fi

if [[ -r /etc/os-release ]]; then
  . /etc/os-release
  # Warn unless this is Ubuntu, an LTS (even year + ".04"), and >= 24.04. Ubuntu
  # LTS releases are the April (".04") release of even-numbered years; the
  # numeric rank (major*100 + minor, e.g. 24.04 -> 2404) gives the >= 24.04 test.
  ver="${VERSION_ID:-}"
  major="${ver%%.*}"
  minor="${ver##*.}"
  ok=1
  if [[ "${ID:-}" != "ubuntu" ]]; then
    ok=0
  elif [[ ! "${major}" =~ ^[0-9]+$ || ! "${minor}" =~ ^[0-9]+$ ]]; then
    ok=0
  elif (( 10#${minor} != 4 || 10#${major} % 2 != 0 )); then
    ok=0   # not an LTS (only even-year ".04" releases are LTS)
  elif (( 10#${major} * 100 + 10#${minor} < 2404 )); then
    ok=0   # older than 24.04
  fi
  if (( ok == 0 )); then
    warn "WARNING: expected Ubuntu LTS 24.04 or newer. Continuing on ${PRETTY_NAME:-unknown OS}."
  fi
fi

step "Installing base packages"
apt-get update
apt-get install -y ca-certificates curl git jq ripgrep unzip gnupg lsb-release

step "Installing Docker if needed"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
else
  note "Docker already installed"
fi

if id "${TARGET_USER}" >/dev/null 2>&1; then
  usermod -aG docker "${TARGET_USER}"
fi

step "Creating directories"
mkdir -p "${AGENT_HOME}" "${WORKSPACE_ROOT}" "${CONFIG_DIR}" "${RUNTIME_DIR}"
chown -R "${TARGET_USER}:${TARGET_USER}" "${AGENT_HOME}" || true

# WORKSPACE_ROOT (default /root/repos) holds the user's checked-out repos and is
# used by whoever connects to the VM -- root, via VS Code Remote-SSH / the agent
# -- which is NOT necessarily the user running provisioning via sudo. Derive its
# owner from the directory it lives under (/root -> root, /home/x -> x) instead
# of from TARGET_USER, so a re-provision over SSH as 'agent' doesn't hand root's
# repos to agent (and heals a VM where a previous run already did).
WORKSPACE_OWNER="$(stat -c '%U' "$(dirname "${WORKSPACE_ROOT}")" 2>/dev/null || echo "${TARGET_USER}")"
chown -R "${WORKSPACE_OWNER}:${WORKSPACE_OWNER}" "${WORKSPACE_ROOT}" 2>/dev/null || true

step "Creating local config if missing"
if [[ ! -f "${CONFIG_FILE}" ]]; then
  cat >"${CONFIG_FILE}" <<EOF
AGENT_NAME=$(hostname)-agent
PROJECTS=default
AGENT_HOME=${AGENT_HOME}
WORKSPACE_ROOT=${WORKSPACE_ROOT}
SSH_USER=${TARGET_USER}
ALLOW_HOST_PACKAGES=false
AI_TOOLS=
OPENCODE_HOST=0.0.0.0
OPENCODE_PORT=4096
CODEX_HOST=0.0.0.0
CODEX_PORT=4500
CODEX_TOKEN_FILE=/etc/construct/codex-app-server.token
EOF
  chmod 0644 "${CONFIG_FILE}"
fi

step "Installing console info service"
install -m 0755 "${REPO_DIR}/bin/print-connection-info.sh" /usr/local/bin/construct-print-connection-info
install -m 0644 "${REPO_DIR}/systemd/construct-console-info.service" /etc/systemd/system/construct-console-info.service
"${REPO_DIR}/bin/update-login-banner.sh"

if [[ "${CONSTRUCT_NONINTERACTIVE:-false}" != "true" && -t 0 ]]; then
  step "Running AI tool setup workflow"
  "${REPO_DIR}/bin/ui-setup.sh"
else
  step "Skipping AI tool setup workflow; run sudo ${REPO_DIR}/bin/ui-setup.sh later"
fi

step "Installing systemd service"
install -m 0644 "${REPO_DIR}/systemd/construct.service" /etc/systemd/system/construct.service
systemctl daemon-reload
systemctl enable construct
systemctl enable construct-console-info

step "Generating runtime config"
"${REPO_DIR}/bin/generate-runtime-config.sh"

ok "Bootstrap complete."
cat <<EOF

Next steps:
1. If Docker group membership changed, log out and back in.
2. Review ${CONFIG_FILE}.
3. Run: sudo ${REPO_DIR}/bin/generate-runtime-config.sh
4. Run: sudo systemctl start construct
5. Run: docker ps
EOF
