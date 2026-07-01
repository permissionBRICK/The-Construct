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
# Always install the VS Code CLI ("VS Code Server") so VS Code Remote-SSH works
# out of the box. Opt out with VSCODE_SERVER=false.
VSCODE_SERVER="${VSCODE_SERVER:-true}"
# Autostart `code serve-web` (browser VS Code over HTTP, gated by a connection
# token; bound to 0.0.0.0). On by default; set VSCODE_SERVE_WEB=false to skip.
VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB:-true}"
# Set up + register a `code tunnel` only when SELECTED -- via this env/param or a
# VSCODE_TUNNEL=true line in an existing config.env. Precedence: explicit env/param
# > saved config value > default (false). (The install script still redeploys the
# tunnel SERVICE unconditionally when a prior deployment/registration exists, so a
# registered VM keeps autostarting the tunnel even without the flag.)
_vscode_tunnel_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _vscode_tunnel_saved="$(sed -n 's/^VSCODE_TUNNEL=//p' "${CONFIG_FILE}" | head -1)"
fi
VSCODE_TUNNEL="${VSCODE_TUNNEL:-${_vscode_tunnel_saved:-false}}"
# Patch the Claude Code VS Code extension so it streams partial assistant messages
# over Remote-SSH (the stock build disables that on remote, so the chat panel looks
# frozen until each turn finishes generating). On by default; CLAUDE_PARTIAL_STREAMING=false
# keeps the stock behaviour. Forwarded to install-vscode.sh, which applies the patch.
CLAUDE_PARTIAL_STREAMING="${CLAUDE_PARTIAL_STREAMING:-true}"

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
# Optional git credentials for cloning private project repos, base64-encoded
# newline-separated `https://user:token@host` lines (see Provision-AgentVM.ps1 /
# Auto-Install.ps1). Used only for the checkout below; persisted into the users'
# ~/.git-credentials only when GIT_CREDENTIAL_STORE=true.
GIT_CLONE_CREDENTIALS=""
if [[ -n "${GIT_CLONE_CREDENTIALS_B64:-}" ]]; then
  GIT_CLONE_CREDENTIALS="$(printf '%s' "${GIT_CLONE_CREDENTIALS_B64}" | base64 -d 2>/dev/null || true)"
fi

step "provision.sh starting (non-interactive)"
note "    AGENT_NAME=${AGENT_NAME}"
note "    PROJECTS=${PROJECTS}"
note "    SSH_USER=${SSH_USER}"
note "    AI_TOOLS=${AI_TOOLS}"
note "    CLAUDE_USER=${CLAUDE_USER}"
note "    VSCODE_SERVER=${VSCODE_SERVER}"
note "    VSCODE_SERVE_WEB=${VSCODE_SERVE_WEB}"
note "    VSCODE_TUNNEL=${VSCODE_TUNNEL}"
note "    CLAUDE_PARTIAL_STREAMING=${CLAUDE_PARTIAL_STREAMING}"
note "    SMB_SHARE=${SMB_SHARE:-(saved/default)}"

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
#    Pass SSH_USER explicitly so bootstrap.sh derives its TARGET_USER (docker
#    group, /opt/construct ownership) from the seed user rather than SUDO_USER --
#    provisioning may run directly as root (the re-provision root-key fast path),
#    where SUDO_USER is unset and would otherwise flip TARGET_USER to root.
step "Running bootstrap.sh"
SSH_USER="${SSH_USER}" CONSTRUCT_NONINTERACTIVE=true bash "${REPO_DIR}/bootstrap.sh"

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
cfg VSCODE_SERVER "${VSCODE_SERVER}"
cfg VSCODE_SERVE_WEB "${VSCODE_SERVE_WEB}"
cfg VSCODE_TUNNEL "${VSCODE_TUNNEL}"
cfg CLAUDE_PARTIAL_STREAMING "${CLAUDE_PARTIAL_STREAMING}"
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

# 2c. SMB share of the workspace for the host PC. On by default; the host then
#     auto-mounts it (net use ... /savecred /persistent:yes). Credentials are
#     generated once and persisted into config.env, so re-provisions keep the
#     same login the host already saved. setup-smb-share.sh resolves the
#     SMB_SHARE/SMB_USER/... precedence (env/param > saved config > default), so
#     forward whatever the host passed (empty = use saved/default).
step "Setting up SMB share for the host"
env SMB_SHARE="${SMB_SHARE:-}" SMB_USER="${SMB_USER:-}" \
  SMB_SHARE_NAME="${SMB_SHARE_NAME:-}" SMB_PASSWORD="${SMB_PASSWORD:-}" \
  WORKSPACE_ROOT="${WORKSPACE_ROOT}" CONFIG_FILE="${CONFIG_FILE}" REPO_DIR="${REPO_DIR}" \
  bash "${REPO_DIR}/bin/setup-smb-share.sh" \
  || warn "WARNING: SMB share setup failed; continuing"

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

# 5a. Configure the agent-native MCP servers the selected projects declare into
#     Claude / Codex / Opencode (reads generated.json -> .mcpServers). Runs after
#     the AI tools are installed (step 4) and the runtime config exists (step 5).
step "Configuring MCP servers for the AI tools"
AI_TOOLS="${AI_TOOLS}" CLAUDE_USER="${CLAUDE_USER}" AGENT_HOME="${AGENT_HOME:-/opt/construct}" \
  WORKSPACE_ROOT="${WORKSPACE_ROOT}" \
  bash "${REPO_DIR}/bin/configure-mcp.sh" \
  || warn "WARNING: MCP configuration failed; continuing"

# 5b. Install the runtimes (node/python/dotnet) the selected projects declare.
if [[ "${INSTALL_SDKS}" == "true" ]]; then
  step "Installing project SDKs/runtimes"
  bash "${REPO_DIR}/bin/install-sdks.sh"
fi

# 5c. Seed git credentials for cloning private repos, if the host supplied any.
#     Written to a temp file consulted ONLY for the checkout below (via a
#     per-invocation `store --file=` helper), so they are not persisted by
#     default. When GIT_CREDENTIAL_STORE=true they are ALSO merged into the
#     operating users' ~/.git-credentials so they survive for later pushes/pulls
#     (and are captured by a future config export).
_clone_creds_file=""
if [[ -n "${GIT_CLONE_CREDENTIALS}" ]]; then
  step "Seeding git credentials for repo checkout"
  _clone_creds_file="$(mktemp)"
  chmod 600 "${_clone_creds_file}"
  printf '%s\n' "${GIT_CLONE_CREDENTIALS}" >"${_clone_creds_file}"
  if [[ "${GIT_CREDENTIAL_STORE}" == "true" ]]; then
    _cred_seen=""
    for _gu in "${CLAUDE_USER}" "${SSH_USER}"; do
      [[ -n "${_gu}" ]] || continue
      case " ${_cred_seen} " in *" ${_gu} "*) continue ;; esac
      _cred_seen="${_cred_seen} ${_gu}"
      _gu_home="$(getent passwd "${_gu}" | cut -d: -f6)"
      [[ -n "${_gu_home}" ]] || continue
      _cf="${_gu_home}/.git-credentials"
      touch "${_cf}"; chmod 600 "${_cf}"; chown "${_gu}:${_gu}" "${_cf}" 2>/dev/null || true
      while IFS= read -r _line; do
        [[ -n "${_line}" ]] || continue
        grep -qxF "${_line}" "${_cf}" 2>/dev/null || printf '%s\n' "${_line}" >>"${_cf}"
      done <"${_clone_creds_file}"
    done
    ok "git credentials stored for:${_cred_seen}"
  else
    note "git credentials will be used for checkout only (not persisted)"
  fi
fi

# 6. Optionally check out project repos (needs Git auth on the VM). When clone
#    credentials were seeded, point a one-shot credential.helper at the temp file
#    via GIT_CONFIG_* so the clone authenticates without an interactive prompt
#    and without depending on a persisted store.
if [[ "${CHECKOUT_PROJECTS}" == "true" ]]; then
  step "Checking out project repos"
  _checkout_rc=0
  if [[ -n "${_clone_creds_file}" ]]; then
    GIT_CONFIG_COUNT=1 \
    GIT_CONFIG_KEY_0=credential.helper \
    GIT_CONFIG_VALUE_0="store --file=${_clone_creds_file}" \
      bash "${REPO_DIR}/bin/checkout-projects.sh" || _checkout_rc=$?
  else
    bash "${REPO_DIR}/bin/checkout-projects.sh" || _checkout_rc=$?
  fi
  if [[ "${_checkout_rc}" -ne 0 ]]; then
    # Report on stdout (not only via warn -> stderr, which the provisioning log
    # can drop) so a failed checkout is impossible to miss. The per-repo reasons
    # were already streamed above by checkout-projects.sh.
    step "Project checkout FAILED (exit ${_checkout_rc}) -- see the clone errors above"
    note "    Most common cause: missing or invalid Git credentials for the private repo host."
    note "    Fix the credentials, then re-run: bash ${REPO_DIR}/bin/checkout-projects.sh"
    warn "WARNING: project checkout failed (Git auth not configured?)"
  fi
fi

# Drop the transient clone-credentials temp file (created above, used only for
# the checkout). Persisted copies, if any, live in ~/.git-credentials.
[[ -n "${_clone_creds_file}" ]] && rm -f "${_clone_creds_file}"

# 6b. Run the custom provisioning commands the selected projects declare. Placed
#     after the checkout so each command runs from inside its project's cloned
#     repo, and after the SDKs (step 5b) so build/install steps find their
#     runtimes. Runs every provision; a failing command warns but never aborts.
step "Running project provisioning commands"
WORKSPACE_ROOT="${WORKSPACE_ROOT}" AGENT_HOME="${AGENT_HOME:-/opt/construct}" \
  bash "${REPO_DIR}/bin/run-provision-commands.sh" \
  || warn "WARNING: one or more project provisioning commands failed; continuing"

# 7. (Re)start the agent service. Use restart, NOT start: construct.service is
#    Type=oneshot + RemainAfterExit=yes, so on a reprovision it is already "active"
#    and a plain `start` is a no-op that would NOT re-run ExecStartPre
#    (generate-runtime-config.sh) or `docker compose up -d`. A reprovision no longer
#    reboots the VM (only a full install/reinstall does), so restart here re-applies
#    the freshly regenerated runtime/compose config live -- the job the post-provision
#    reboot used to do. On a fresh install the unit is inactive and restart just starts it.
if [[ "${START_SERVICE}" == "true" ]]; then
  step "(Re)starting construct service"
  systemctl restart construct
fi

# 8. Install the VS Code CLI ("VS Code Server", for Remote-SSH) and -- when the
#    tunnel is selected or already registered/deployed -- (re)deploy the
#    code-tunnel service. Kept LAST so any device sign-in link is the final thing
#    the (streamed) provisioning output shows; the host script then pauses for the
#    sign-in before finishing (and, on a full install/reinstall, rebooting).
if [[ "${VSCODE_SERVER}" == "true" ]]; then
  step "Setting up VS Code server / serve-web / tunnel"
  VSCODE_SERVER="${VSCODE_SERVER}" VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB}" VSCODE_TUNNEL="${VSCODE_TUNNEL}" \
    VSCODE_SERVE_WEB_TOKEN_B64="${VSCODE_SERVE_WEB_TOKEN_B64:-}" \
    VSCODE_CLIENT_COMMIT="${VSCODE_CLIENT_COMMIT:-}" \
    CLAUDE_PARTIAL_STREAMING="${CLAUDE_PARTIAL_STREAMING}" \
    bash "${REPO_DIR}/bin/install-vscode.sh" \
    || warn "WARNING: VS Code setup failed; continuing"
fi

# 9. Record provisioning timestamps so the control panel can surface when this VM
#    was first installed and last (re)provisioned. INSTALLED_AT is written once and
#    preserved across reprovisions (a fresh install has no marker yet); it also
#    heals a VM provisioned before this marker existed by seeding it now.
#    REPROVISIONED_AT is rewritten on EVERY successful run, so a reprovision moves
#    it. Written last so it only records a provision that reached the end. The file
#    is a config.env-style KEY=VALUE so config-set.sh's idempotent merge and the
#    control panel's sed reader both handle it. Best-effort: never abort the run.
step "Recording provisioning timestamps"
MARKER_FILE="${MARKER_FILE:-/etc/construct/provisioned.env}"
_now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
mark() { bash "${REPO_DIR}/bin/config-set.sh" "${MARKER_FILE}" "$1" "$2"; }
if [[ -f "${MARKER_FILE}" ]] && grep -Eq '^INSTALLED_AT=.+' "${MARKER_FILE}"; then
  note "    INSTALLED_AT preserved (first install unchanged)"
else
  mark INSTALLED_AT "${_now}" && note "    INSTALLED_AT=${_now}"
fi
mark REPROVISIONED_AT "${_now}" && note "    REPROVISIONED_AT=${_now}"
chmod 0644 "${MARKER_FILE}" 2>/dev/null || true

ok "provision.sh complete"
