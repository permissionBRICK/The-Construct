#!/usr/bin/env bash
#
# Non-interactive provisioning entrypoint.
#
# Drives the full setup chain without any prompts, suitable for programmatic
# (e.g. SSH-driven) provisioning such as Provision-AgentVM.ps1. All inputs come
# from environment variables with sensible defaults; nothing reads stdin.
#
# Run as root (the Windows host script invokes it via sudo):
#   sudo env AI_TOOLS=opencode,claude-code PROJECTS=default \
#     bash /opt/construct/repo/bin/provision.sh
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

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash ${REPO_DIR}/bin/provision.sh"
  exit 1
fi

# Configuration, all overridable via the environment.
AGENT_NAME="${AGENT_NAME:-$(hostname)-agent}"
PROJECTS="${PROJECTS:-default}"
SSH_USER="${SSH_USER:-${SUDO_USER:-agent}}"
AI_TOOLS="${AI_TOOLS:-opencode,claude-code,codex}"
ALLOW_HOST_PACKAGES="${ALLOW_HOST_PACKAGES:-false}"
# Where project repos are checked out. Defaults to /root/repos because the
# VS Code Remote-SSH / agent connection uses root.
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
# User that Claude Code (CLI + VS Code extension) is configured for. Defaults to
# root because the VS Code Remote-SSH connection logs in as root.
CLAUDE_USER="${CLAUDE_USER:-root}"
SETUP_ROOT_SSH_KEY="${SETUP_ROOT_SSH_KEY:-true}"
# Install the runtimes (node/python/dotnet) declared by the selected projects.
INSTALL_SDKS="${INSTALL_SDKS:-true}"
CHECKOUT_PROJECTS="${CHECKOUT_PROJECTS:-false}"
START_SERVICE="${START_SERVICE:-true}"

# Optional global git identity to set on the VM. Passed base64-encoded (see
# Provision-AgentVM.ps1) so names/emails with spaces or apostrophes survive the
# SSH/shell layers untouched. Empty when not supplied -- left unchanged on the VM.
GIT_USER_NAME=""
GIT_USER_EMAIL=""
if [[ -n "${GIT_USER_NAME_B64:-}" ]]; then
  GIT_USER_NAME="$(printf '%s' "${GIT_USER_NAME_B64}" | base64 -d 2>/dev/null || true)"
fi
if [[ -n "${GIT_USER_EMAIL_B64:-}" ]]; then
  GIT_USER_EMAIL="$(printf '%s' "${GIT_USER_EMAIL_B64}" | base64 -d 2>/dev/null || true)"
fi
# Whether to enable git's plaintext credential store (credential.helper store) so
# pushes/pulls don't re-prompt. "true"/"false"/"" (empty = leave unchanged).
GIT_CREDENTIAL_STORE="${GIT_CREDENTIAL_STORE:-}"

step "provision.sh starting (non-interactive)"
note "    AGENT_NAME=${AGENT_NAME}"
note "    PROJECTS=${PROJECTS}"
note "    SSH_USER=${SSH_USER}"
note "    AI_TOOLS=${AI_TOOLS}"
note "    CLAUDE_USER=${CLAUDE_USER}"

# A zip upload does not preserve Unix exec bits, so make the repo scripts
# executable before anything tries to run them.
chmod +x "${REPO_DIR}/bootstrap.sh" "${REPO_DIR}/bin/"*.sh 2>/dev/null || true

# Grant the SSH/seed user passwordless sudo. Provisioning -- and especially
# RE-provisioning -- runs its privileged steps over SSH as this user via
# `sudo -S` fed the seed login password. If that login password was later
# changed (the optional custom agent password applied at the end of the first
# run), the seed password stops working and every sudo step fails. A NOPASSWD
# drop-in makes provisioning depend on the bootstrap/root key alone and never on
# the login password -- matching the sandbox's "unattended root, no prompts"
# design. Validated with visudo before install so a bad file can't lock sudo.
step "Granting ${SSH_USER} passwordless sudo"
_sudoers_tmp="$(mktemp)"
printf '%s ALL=(ALL) NOPASSWD:ALL\n' "${SSH_USER}" >"${_sudoers_tmp}"
chmod 0440 "${_sudoers_tmp}"
if visudo -cf "${_sudoers_tmp}" >/dev/null 2>&1; then
  install -m 0440 "${_sudoers_tmp}" "/etc/sudoers.d/90-construct-${SSH_USER}"
  ok "passwordless sudo configured for ${SSH_USER}"
else
  warn "WARNING: sudoers drop-in failed validation; leaving sudo unchanged"
fi
rm -f "${_sudoers_tmp}"

# Heal a config.env poisoned by a pre-fix run that wrote an unquoted git name
# with a space (e.g. GIT_USER_NAME=Christoph Ambrosch), which makes every later
# `. config.env` abort with exit 127 -- including bootstrap.sh's login-banner
# step, before we'd ever rewrite the file. We no longer store git identity in
# config.env (it's set via `git config --global` below), so just drop any legacy
# GIT_USER_* lines before anything sources the file.
if [[ -f "${CONFIG_FILE}" ]]; then
  sed -i -E '/^GIT_USER_(NAME|EMAIL)=/d' "${CONFIG_FILE}" || true
fi

# 1. Base host setup: packages, Docker, dirs, default config, systemd units.
#    Forced non-interactive so it never launches the ui-setup workflow.
step "Running bootstrap.sh"
CONSTRUCT_NONINTERACTIVE=true bash "${REPO_DIR}/bootstrap.sh"

# 2. Apply configuration to /etc/construct/config.env (idempotent merge that
#    preserves any other keys bootstrap wrote).
step "Writing configuration to ${CONFIG_FILE}"
cfg() { bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" "$1" "$2"; }
cfg AGENT_NAME "${AGENT_NAME}"
cfg PROJECTS "${PROJECTS}"
cfg SSH_USER "${SSH_USER}"
cfg AI_TOOLS "${AI_TOOLS}"
cfg ALLOW_HOST_PACKAGES "${ALLOW_HOST_PACKAGES}"
cfg WORKSPACE_ROOT "${WORKSPACE_ROOT}"
install -d -m 0755 "${WORKSPACE_ROOT}"

# 2b. Global git identity for the users that operate on the VM: CLAUDE_USER
#     (root -- used by VS Code Remote-SSH and the AI tools) and the SSH/seed user
#     (interactive logins). Values arrive from the host, defaulted there to the
#     host's own git identity; empty values are left unchanged. Deliberately NOT
#     written to config.env (it is `source`-d by other scripts, and a name with a
#     space would break that) -- `git config --global` is the store on the VM.
#     Optionally also enables git's plaintext credential store (the host warns
#     about the security trade-off before this is requested).
if [[ -n "${GIT_USER_NAME}" || -n "${GIT_USER_EMAIL}" || -n "${GIT_CREDENTIAL_STORE}" ]]; then
  step "Configuring global git identity"
  _git_seen=""
  for _gu in "${CLAUDE_USER}" "${SSH_USER}"; do
    [[ -n "${_gu}" ]] || continue
    case " ${_git_seen} " in *" ${_gu} "*) continue ;; esac
    _git_seen="${_git_seen} ${_gu}"
    _gu_home="$(getent passwd "${_gu}" | cut -d: -f6)"
    if [[ -z "${_gu_home}" ]]; then warn "  skipping ${_gu}: no home directory"; continue; fi
    if [[ -n "${GIT_USER_NAME}" ]]; then
      sudo -H -u "${_gu}" git config --global user.name "${GIT_USER_NAME}" \
        || warn "  could not set user.name for ${_gu}"
    fi
    if [[ -n "${GIT_USER_EMAIL}" ]]; then
      sudo -H -u "${_gu}" git config --global user.email "${GIT_USER_EMAIL}" \
        || warn "  could not set user.email for ${_gu}"
    fi
    # Plaintext credential store: enable when requested; when explicitly declined,
    # remove only a store helper we may have set before (don't clobber another).
    _cred="(unchanged)"
    if [[ "${GIT_CREDENTIAL_STORE}" == "true" ]]; then
      if sudo -H -u "${_gu}" git config --global credential.helper store; then _cred="store (plaintext)"
      else warn "  could not enable credential.helper for ${_gu}"; fi
    elif [[ "${GIT_CREDENTIAL_STORE}" == "false" ]]; then
      if [[ "$(sudo -H -u "${_gu}" git config --global credential.helper 2>/dev/null || true)" == "store" ]]; then
        sudo -H -u "${_gu}" git config --global --unset-all credential.helper || true
        _cred="disabled"
      fi
    fi
    ok "  ${_gu}: ${GIT_USER_NAME:-(unchanged)} <${GIT_USER_EMAIL:-(unchanged)}>  credentials: ${_cred}"
  done
fi

# 3. Root SSH key so the host (VS Code Remote-SSH) can log in as root by key.
if [[ "${SETUP_ROOT_SSH_KEY}" == "true" ]]; then
  step "Setting up root SSH key"
  bash "${REPO_DIR}/bin/setup-root-ssh-key.sh"
fi

# 4. Install selected AI tools. TARGET_USER pins Claude Code's CLI + VS Code
#    extension settings to CLAUDE_USER (root) regardless of the sudo user.
step "Installing AI tools"
TARGET_USER="${CLAUDE_USER}" bash "${REPO_DIR}/bin/install-ai-tools.sh"

# 5. Merge selected project profiles into the runtime config.
step "Generating runtime config"
bash "${REPO_DIR}/bin/generate-runtime-config.sh"

# 5b. Install the runtimes (node/python/dotnet) the selected projects declare.
if [[ "${INSTALL_SDKS}" == "true" ]]; then
  step "Installing project SDKs/runtimes"
  bash "${REPO_DIR}/bin/install-sdks.sh"
fi

# 6. Optionally check out project repos (needs Git auth on the VM).
if [[ "${CHECKOUT_PROJECTS}" == "true" ]]; then
  step "Checking out project repos"
  bash "${REPO_DIR}/bin/checkout-projects.sh" \
    || warn "WARNING: project checkout failed (Git auth not configured?)"
fi

# 7. Start the agent service.
if [[ "${START_SERVICE}" == "true" ]]; then
  step "Starting construct service"
  systemctl start construct
fi

ok "provision.sh complete"
