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
# Exit contract (consumed by Provision-AgentVM.ps1):
#   0 = every step completed cleanly
#   3 = provisioning reached the end, but one or more optional steps failed
#   any other non-zero value = a critical step failed and provisioning stopped
#
# Step criticality is deliberately coarse at the orchestration boundary:
#
#   CRITICAL | root privilege; core bootstrap/base prerequisites; config.env
#            | writes; root SSH key setup when enabled
#   OPTIONAL | sudoers convenience; SMB; each selected AI tool; construct CLI;
#            | runtime config; MCP; SDKs; git identity/credential seeding;
#            | project checkout/commands; service restarts; VS Code; timestamps
#
# A critical step is limited to work without which the VM is unusable or the host
# can be locked out. Everything else reaches the final loud failure summary so a
# transient network/package/service failure does not hide later independent work.
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

# Every command is placed in a conditional pipeline: that context suppresses
# errexit for the pipeline while pipefail still preserves the command's status.
# tee keeps output live and leaves a merged stdout/stderr log for the final tail.
_PROVISION_LOG_DIR="$(mktemp -d)"
declare -a _FAILED_TITLES=()
declare -a _FAILED_CODES=()
declare -a _FAILED_TAILS=()
declare -a _FAILED_LOG_PATHS=()

# Persistent log directory: full step logs are copied here so the host can point
# the user's AI coding agent at a readable log after provisioning finishes. The
# temp dir (_PROVISION_LOG_DIR) is deleted by _finish_provision, so without this
# the logs would be lost. Old logs are cleaned at the start of each run (only the
# current run's failures are kept).
_PERSISTENT_LOG_DIR="/var/log/construct/provision"

_sanitize_step_title() {
  local title="$1"
  title="${title//$'\n'/ }"
  title="${title//$'\r'/ }"
  title="${title//|/ }"
  printf '%s' "${title}"
}

_record_step_failure() {
  local title="$1" rc="$2" log_file="$3" tail_file persistent_file
  tail_file="${_PROVISION_LOG_DIR}/tail-${#_FAILED_TITLES[@]}.log"
  tail -n 15 "${log_file}" >"${tail_file}" 2>/dev/null || :
  # Persist the FULL log (not just the tail) so the host can cite a readable path
  # in the AI-agent fix prompt. The slug is filesystem-safe and bounded at 60 chars.
  persistent_file="${_PERSISTENT_LOG_DIR}/step-${#_FAILED_TITLES[@]}-$(_sanitize_step_title "${title}" | tr ' ' '-' | tr -cd 'A-Za-z0-9_-' | head -c 60).log"
  cp "${log_file}" "${persistent_file}" 2>/dev/null || persistent_file=""
  _FAILED_TITLES+=("${title}")
  _FAILED_CODES+=("${rc}")
  _FAILED_TAILS+=("${tail_file}")
  _FAILED_LOG_PATHS+=("${persistent_file}")
}

_print_machine_result() {
  local i
  printf '%s\n' '===CONSTRUCT-PROVISION-RESULT==='
  printf 'errors=%s\n' "${#_FAILED_TITLES[@]}"
  for ((i=0; i<${#_FAILED_TITLES[@]}; i++)); do
    # Third field: the persistent log path on the VM, so the host can cite it in
    # the AI-agent fix prompt. The path is generated code (no pipe/newline), so
    # the pipe-delimited format round-trips safely.
    printf 'error=%s|%s|%s\n' \
      "$(_sanitize_step_title "${_FAILED_TITLES[$i]}")" \
      "${_FAILED_CODES[$i]}" \
      "${_FAILED_LOG_PATHS[$i]}"
  done
  printf '%s\n' '===END-CONSTRUCT-PROVISION-RESULT==='
}

_print_human_result() {
  local critical_rc="$1" i line
  if [[ "${#_FAILED_TITLES[@]}" -eq 0 ]]; then
    ok "ALL PROVISIONING STEPS COMPLETED CLEANLY"
    return
  fi

  if [[ "${critical_rc}" -ne 0 ]]; then
    printf '%sPROVISION FAILED -- %s step(s) failed:%s\n' "${_C_ERR}" "${#_FAILED_TITLES[@]}" "${_C_RESET}"
  else
    printf '%sPROVISIONING COMPLETED WITH %s ERROR(S):%s\n' "${_C_ERR}" "${#_FAILED_TITLES[@]}" "${_C_RESET}"
  fi
  for ((i=0; i<${#_FAILED_TITLES[@]}; i++)); do
    printf '%s  - %s (exit %s)%s\n' "${_C_ERR}" "${_FAILED_TITLES[$i]}" "${_FAILED_CODES[$i]}" "${_C_RESET}"
    if [[ -s "${_FAILED_TAILS[$i]}" ]]; then
      printf '%s    last output:%s\n' "${_C_ERR}" "${_C_RESET}"
      while IFS= read -r line || [[ -n "${line}" ]]; do
        printf '%s      %s%s\n' "${_C_ERR}" "${line}" "${_C_RESET}"
      done <"${_FAILED_TAILS[$i]}"
    else
      printf '%s      (no output captured)%s\n' "${_C_ERR}" "${_C_RESET}"
    fi
  done
}

_finish_provision() {
  local critical_rc="${1:-0}" final_rc
  _print_machine_result
  _print_human_result "${critical_rc}"
  rm -rf "${_PROVISION_LOG_DIR}" || true
  if [[ "${critical_rc}" -ne 0 ]]; then
    final_rc="${critical_rc}"
    [[ "${final_rc}" -eq 3 ]] && final_rc=1
  elif [[ "${#_FAILED_TITLES[@]}" -gt 0 ]]; then
    final_rc=3
  else
    final_rc=0
  fi
  exit "${final_rc}"
}

run_step() {
  local criticality="$1" title="$2" rc log_file
  shift 2
  case "${criticality}" in
    critical|optional) ;;
    *) printf '%srun_step: invalid criticality: %s%s\n' "${_C_ERR}" "${criticality}" "${_C_RESET}"; return 2 ;;
  esac

  step "${title}"
  log_file="${_PROVISION_LOG_DIR}/step-$(( ${#_FAILED_TITLES[@]} + 1 ))-${RANDOM}.log"
  if "$@" 2>&1 | tee "${log_file}"; then
    rm -f "${log_file}"
    return 0
  else
    rc=$?
  fi

  _record_step_failure "${title}" "${rc}" "${log_file}"
  rm -f "${log_file}"
  if [[ "${criticality}" == "optional" ]]; then
    printf '%sSTEP FAILED (continuing): %s (exit %s)%s\n' "${_C_ERR}" "${title}" "${rc}" "${_C_RESET}"
    return 0
  fi

  printf '%sSTEP FAILED (critical): %s (exit %s)%s\n' "${_C_ERR}" "${title}" "${rc}" "${_C_RESET}"
  _finish_provision "${rc}"
}

# The plain-Bash unit test sources only the runner; no VM paths or root-only
# provisioning actions are touched in that mode.
if [[ "${CONSTRUCT_STEP_RUNNER_ONLY:-false}" == "true" ]]; then
  return 0 2>/dev/null || exit 0
fi

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    printf 'Run with sudo: sudo bash %s/bin/provision.sh\n' "${REPO_DIR}"
    return 1
  fi
}
run_step critical "Checking root privileges" require_root

# Create the persistent log directory and clean any logs from a previous run.
# Only the current run's failure logs are kept; successful steps write nothing.
mkdir -p "${_PERSISTENT_LOG_DIR}"
rm -f "${_PERSISTENT_LOG_DIR}/"*.log 2>/dev/null || true

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
  _vscode_tunnel_saved="$(sed -n 's/^VSCODE_TUNNEL=//p' "${CONFIG_FILE}" | head -1 || true)"
fi
VSCODE_TUNNEL="${VSCODE_TUNNEL:-${_vscode_tunnel_saved:-false}}"
# Patch the Claude Code VS Code extension so it streams partial assistant messages
# over Remote-SSH (the stock build disables that on remote, so the chat panel looks
# frozen until each turn finishes generating). On by default; CLAUDE_PARTIAL_STREAMING=false
# keeps the stock behaviour. Forwarded to install-vscode.sh, which applies the patch.
CLAUDE_PARTIAL_STREAMING="${CLAUDE_PARTIAL_STREAMING:-true}"
# Patch the Claude Code extension for microphone passthrough (recorder shim + chat-mic
# gate) when the saved preference is on, so the mic button survives a reprovision.
# Off by default (opt-in); MIC_PASSTHROUGH=false reverts to stock. Forwarded to
# install-vscode.sh, which applies the patch.
MIC_PASSTHROUGH="${MIC_PASSTHROUGH:-false}"
# Opt-in T3 Code web GUI (the `t3` npm package; service t3code-serve). Disabled by
# default. Precedence: explicit env/param > saved config value > default (false) --
# the host passes an EMPTY value when it doesn't know the preference, so a plain
# console reprovision keeps a previously enabled T3 Code instead of disabling it.
_t3code_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _t3code_saved="$(sed -n 's/^T3CODE=//p' "${CONFIG_FILE}" | head -1 || true)"
fi
T3CODE="${T3CODE:-${_t3code_saved:-false}}"
[[ "${T3CODE}" == "true" ]] || T3CODE=false

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
note "    MIC_PASSTHROUGH=${MIC_PASSTHROUGH}"
note "    T3CODE=${T3CODE}"
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
configure_passwordless_sudo() {
  local sudoers_tmp
  sudoers_tmp="$(mktemp)" || return
  printf '%s ALL=(ALL) NOPASSWD:ALL\n' "${SSH_USER}" >"${sudoers_tmp}" || return
  chmod 0440 "${sudoers_tmp}" || return
  if visudo -cf "${sudoers_tmp}" >/dev/null 2>&1; then
    install -m 0440 "${sudoers_tmp}" "/etc/sudoers.d/90-construct-${SSH_USER}" || { rm -f "${sudoers_tmp}"; return 1; }
    ok "passwordless sudo configured for ${SSH_USER}"
  else
    rm -f "${sudoers_tmp}"
    printf 'sudoers drop-in failed validation; leaving sudo unchanged\n'
    return 1
  fi
  rm -f "${sudoers_tmp}" || return
}
run_step optional "Granting ${SSH_USER} passwordless sudo" configure_passwordless_sudo

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
run_step critical "Running core host bootstrap" \
  env SSH_USER="${SSH_USER}" CONSTRUCT_NONINTERACTIVE=true CONSTRUCT_SKIP_RUNTIME_GENERATION=true \
  bash "${REPO_DIR}/bootstrap.sh"

# 2. Apply configuration to /etc/construct/config.env (idempotent merge that
#    preserves any other keys bootstrap wrote).
cfg() { bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" "$1" "$2"; }
write_configuration() {
  cfg AGENT_NAME "${AGENT_NAME}" || return
  cfg PROJECTS "${PROJECTS}" || return
  cfg SSH_USER "${SSH_USER}" || return
  cfg AI_TOOLS "${AI_TOOLS}" || return
  cfg ALLOW_HOST_PACKAGES "${ALLOW_HOST_PACKAGES}" || return
  cfg WORKSPACE_ROOT "${WORKSPACE_ROOT}" || return
  cfg VSCODE_SERVER "${VSCODE_SERVER}" || return
  cfg VSCODE_SERVE_WEB "${VSCODE_SERVE_WEB}" || return
  cfg VSCODE_TUNNEL "${VSCODE_TUNNEL}" || return
  cfg CLAUDE_PARTIAL_STREAMING "${CLAUDE_PARTIAL_STREAMING}" || return
  cfg MIC_PASSTHROUGH "${MIC_PASSTHROUGH}" || return
  cfg T3CODE "${T3CODE}" || return
  install -d -m 0755 "${WORKSPACE_ROOT}"
}
run_step critical "Writing configuration to ${CONFIG_FILE}" write_configuration

# 2b. Global git identity for the users that operate on the VM: CLAUDE_USER
#     (root -- used by VS Code Remote-SSH and the AI tools) and the SSH/seed user
#     (interactive logins). Values arrive from the host, defaulted there to the
#     host's own git identity; empty values are left unchanged. Deliberately NOT
#     written to config.env (it is `source`-d by other scripts, and a name with a
#     space would break that) -- `git config --global` is the store on the VM.
#     Optionally also enables git's plaintext credential store (the host warns
#     about the security trade-off before this is requested).
configure_git_identity() {
  _git_seen=""
  _git_failed=0
  for _gu in "${CLAUDE_USER}" "${SSH_USER}"; do
    [[ -n "${_gu}" ]] || continue
    case " ${_git_seen} " in *" ${_gu} "*) continue ;; esac
    _git_seen="${_git_seen} ${_gu}"
    _gu_home="$(getent passwd "${_gu}" | cut -d: -f6)"
    if [[ -z "${_gu_home}" ]]; then warn "  skipping ${_gu}: no home directory"; _git_failed=1; continue; fi
    if [[ -n "${GIT_USER_NAME}" ]]; then
      sudo -H -u "${_gu}" git config --global user.name "${GIT_USER_NAME}" \
        || { warn "  could not set user.name for ${_gu}"; _git_failed=1; }
    fi
    if [[ -n "${GIT_USER_EMAIL}" ]]; then
      sudo -H -u "${_gu}" git config --global user.email "${GIT_USER_EMAIL}" \
        || { warn "  could not set user.email for ${_gu}"; _git_failed=1; }
    fi
    # Plaintext credential store: enable when requested; when explicitly declined,
    # remove only a store helper we may have set before (don't clobber another).
    _cred="(unchanged)"
    if [[ "${GIT_CREDENTIAL_STORE}" == "true" ]]; then
      if sudo -H -u "${_gu}" git config --global credential.helper store; then _cred="store (plaintext)"
      else warn "  could not enable credential.helper for ${_gu}"; _git_failed=1; fi
    elif [[ "${GIT_CREDENTIAL_STORE}" == "false" ]]; then
      if [[ "$(sudo -H -u "${_gu}" git config --global credential.helper 2>/dev/null || true)" == "store" ]]; then
        sudo -H -u "${_gu}" git config --global --unset-all credential.helper || true
        _cred="disabled"
      fi
    fi
    ok "  ${_gu}: ${GIT_USER_NAME:-(unchanged)} <${GIT_USER_EMAIL:-(unchanged)}>  credentials: ${_cred}"
  done
  return "${_git_failed}"
}
if [[ -n "${GIT_USER_NAME}" || -n "${GIT_USER_EMAIL}" || -n "${GIT_CREDENTIAL_STORE}" ]]; then
  run_step optional "Configuring global git identity" configure_git_identity
fi

# 2c. SMB share of the workspace for the host PC. On by default; the host then
#     auto-mounts it (net use ... /savecred /persistent:yes). Credentials are
#     generated once and persisted into config.env, so re-provisions keep the
#     same login the host already saved. setup-smb-share.sh resolves the
#     SMB_SHARE/SMB_USER/... precedence (env/param > saved config > default), so
#     forward whatever the host passed (empty = use saved/default).
run_step optional "Setting up SMB share for the host" \
  env SMB_SHARE="${SMB_SHARE:-}" SMB_USER="${SMB_USER:-}" \
  SMB_SHARE_NAME="${SMB_SHARE_NAME:-}" SMB_PASSWORD="${SMB_PASSWORD:-}" \
  WORKSPACE_ROOT="${WORKSPACE_ROOT}" CONFIG_FILE="${CONFIG_FILE}" REPO_DIR="${REPO_DIR}" \
  bash "${REPO_DIR}/bin/setup-smb-share.sh"

# 3. Root SSH key so the host (VS Code Remote-SSH) can log in as root by key.
if [[ "${SETUP_ROOT_SSH_KEY}" == "true" ]]; then
  run_step critical "Setting up root SSH key" bash "${REPO_DIR}/bin/setup-root-ssh-key.sh"
fi

# 4. Install selected AI tools. TARGET_USER pins Claude Code's CLI + VS Code
#    extension settings to CLAUDE_USER (root) regardless of the sudo user.
IFS=',' read -ra _selected_ai_tools <<<"${AI_TOOLS}"
for _ai_tool in "${_selected_ai_tools[@]}"; do
  _ai_tool="${_ai_tool//[[:space:]]/}"
  [[ -n "${_ai_tool}" ]] || continue
  run_step optional "Installing AI tool: ${_ai_tool}" \
    env TARGET_USER="${CLAUDE_USER}" AI_TOOLS_OVERRIDE="${_ai_tool}" AI_CONSOLE_INTEGRATION=false \
    bash "${REPO_DIR}/bin/install-ai-tools.sh"
done
# 4a. T3 Code web GUI: its own opt-in flag (panel settings toggle), not part of
#     the AI_TOOLS selection. When enabled, install/update + (re)start the
#     service; when disabled, stop a previously deployed service so the toggle
#     is honoured both ways (the install itself is left in place -- cheap, and
#     re-enabling is then instant).
if [[ "${T3CODE}" == "true" ]]; then
  run_step optional "Installing T3 Code web GUI" \
    env TARGET_USER="${CLAUDE_USER}" AI_TOOLS_OVERRIDE=t3code AI_CONSOLE_INTEGRATION=false \
    bash "${REPO_DIR}/bin/install-ai-tools.sh"
elif [[ -f /etc/systemd/system/t3code-serve.service ]]; then
  run_step optional "Disabling T3 Code web GUI (T3CODE=false)" \
    systemctl disable --now t3code-serve
fi

run_step optional "Installing AI tool console integration" \
  env TARGET_USER="${CLAUDE_USER}" AI_TOOLS_OVERRIDE=none AI_CONSOLE_INTEGRATION=true \
  bash "${REPO_DIR}/bin/install-ai-tools.sh"

# 4b. Install the construct CLI so agents and users can manage project profiles
#     from the VM shell (`construct project set|get|list`). Runs every provision
#     so an updated script always gets redeployed on reprovision.
run_step optional "Installing construct CLI" install -m 0755 "${REPO_DIR}/bin/construct" /usr/local/bin/construct

# 5. Merge selected project profiles into the runtime config.
run_step optional "Generating runtime config" bash "${REPO_DIR}/bin/generate-runtime-config.sh"

# 5a. Configure the agent-native MCP servers the selected projects declare into
#     Claude / Codex / Opencode (reads generated.json -> .mcpServers). Runs after
#     the AI tools are installed (step 4) and the runtime config exists (step 5).
run_step optional "Configuring MCP servers for the AI tools" \
  env AI_TOOLS="${AI_TOOLS}" CLAUDE_USER="${CLAUDE_USER}" AGENT_HOME="${AGENT_HOME:-/opt/construct}" \
  WORKSPACE_ROOT="${WORKSPACE_ROOT}" \
  bash "${REPO_DIR}/bin/configure-mcp.sh"

# 5b. Install the runtimes (node/python/dotnet) the selected projects declare.
if [[ "${INSTALL_SDKS}" == "true" ]]; then
  run_step optional "Installing project SDKs/runtimes" bash "${REPO_DIR}/bin/install-sdks.sh"
fi

# 5c. Seed git credentials for cloning private repos, if the host supplied any.
#     Written to a temp file consulted ONLY for the checkout below (via a
#     per-invocation `store --file=` helper), so they are not persisted by
#     default. When GIT_CREDENTIAL_STORE=true they are ALSO merged into the
#     operating users' ~/.git-credentials so they survive for later pushes/pulls
#     (and are captured by a future config export).
_clone_creds_file=""
if [[ -n "${GIT_CLONE_CREDENTIALS}" ]]; then
  _clone_creds_file="${_PROVISION_LOG_DIR}/clone-credentials"
  seed_git_credentials() {
    local creds_file="$1"
    : >"${creds_file}" || return
    chmod 600 "${creds_file}" || return
    printf '%s\n' "${GIT_CLONE_CREDENTIALS}" >"${creds_file}" || return
    if [[ "${GIT_CREDENTIAL_STORE}" != "true" ]]; then
      note "git credentials will be used for checkout only (not persisted)"
      return 0
    fi
    _cred_seen=""
    for _gu in "${CLAUDE_USER}" "${SSH_USER}"; do
      [[ -n "${_gu}" ]] || continue
      case " ${_cred_seen} " in *" ${_gu} "*) continue ;; esac
      _cred_seen="${_cred_seen} ${_gu}"
      _gu_home="$(getent passwd "${_gu}" | cut -d: -f6)"
      [[ -n "${_gu_home}" ]] || continue
      _cf="${_gu_home}/.git-credentials"
      touch "${_cf}" || return
      chmod 600 "${_cf}" || return
      chown "${_gu}:${_gu}" "${_cf}" 2>/dev/null || true
      while IFS= read -r _line; do
        [[ -n "${_line}" ]] || continue
        grep -qxF "${_line}" "${_cf}" 2>/dev/null || printf '%s\n' "${_line}" >>"${_cf}" || return
      done <"${creds_file}"
    done
    ok "git credentials stored for:${_cred_seen}"
  }
  run_step optional "Seeding git credentials for repo checkout" seed_git_credentials "${_clone_creds_file}"
fi

# 6. Optionally check out project repos (needs Git auth on the VM). When clone
#    credentials were seeded, point a one-shot credential.helper at the temp file
#    via GIT_CONFIG_* so the clone authenticates without an interactive prompt
#    and without depending on a persisted store.
if [[ "${CHECKOUT_PROJECTS}" == "true" ]]; then
  if [[ -n "${_clone_creds_file}" && -s "${_clone_creds_file}" ]]; then
    run_step optional "Checking out project repos" \
      env GIT_CONFIG_COUNT=1 \
      GIT_CONFIG_KEY_0=credential.helper \
      GIT_CONFIG_VALUE_0="store --file=${_clone_creds_file}" \
      bash "${REPO_DIR}/bin/checkout-projects.sh"
  else
    run_step optional "Checking out project repos" bash "${REPO_DIR}/bin/checkout-projects.sh"
  fi
else
  # Say so out loud: a silent skip here has repeatedly read as "cloning is
  # broken" when the real cause was an upstream selection/profile decision.
  step "Skipping project checkout (CHECKOUT_PROJECTS=${CHECKOUT_PROJECTS})"
  note "    The host decided the selected projects (PROJECTS=${PROJECTS}) declare no repos."
  note "    If that's wrong: check the profile's repos[] on the host and that it's selected,"
  note "    then re-run -- or clone manually on the VM: bash ${REPO_DIR}/bin/checkout-projects.sh"
fi

# Drop the transient clone-credentials temp file (created above, used only for
# the checkout). Persisted copies, if any, live in ~/.git-credentials.
if [[ -n "${_clone_creds_file}" ]]; then rm -f "${_clone_creds_file}" || true; fi

# 6b. Run the custom provisioning commands the selected projects declare. Placed
#     after the checkout so each command runs from inside its project's cloned
#     repo, and after the SDKs (step 5b) so build/install steps find their
#     runtimes. Runs every provision; a failing command warns but never aborts.
run_step optional "Running project provisioning commands" \
  env WORKSPACE_ROOT="${WORKSPACE_ROOT}" AGENT_HOME="${AGENT_HOME:-/opt/construct}" \
  bash "${REPO_DIR}/bin/run-provision-commands.sh"

# 7. (Re)start the agent service. Use restart, NOT start: construct.service is
#    Type=oneshot + RemainAfterExit=yes, so on a reprovision it is already "active"
#    and a plain `start` is a no-op that would NOT re-run ExecStartPre
#    (generate-runtime-config.sh) or `docker compose up -d`. A reprovision no longer
#    reboots the VM (only a full install/reinstall does), so restart here re-applies
#    the freshly regenerated runtime/compose config live -- the job the post-provision
#    reboot used to do. On a fresh install the unit is inactive and restart just starts it.
if [[ "${START_SERVICE}" == "true" ]]; then
  run_step optional "(Re)starting construct service" systemctl restart construct
fi

# 8. Install the VS Code CLI ("VS Code Server", for Remote-SSH) and -- when the
#    tunnel is selected or already registered/deployed -- (re)deploy the
#    code-tunnel service. Kept LAST so any device sign-in link is the final thing
#    the (streamed) provisioning output shows; the host script then pauses for the
#    sign-in before finishing (and, on a full install/reinstall, rebooting).
if [[ "${VSCODE_SERVER}" == "true" ]]; then
  run_step optional "Setting up VS Code server / serve-web / tunnel" \
    env VSCODE_SERVER="${VSCODE_SERVER}" VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB}" VSCODE_TUNNEL="${VSCODE_TUNNEL}" \
    VSCODE_SERVE_WEB_TOKEN_B64="${VSCODE_SERVE_WEB_TOKEN_B64:-}" \
    VSCODE_CLIENT_COMMIT="${VSCODE_CLIENT_COMMIT:-}" \
    CLAUDE_PARTIAL_STREAMING="${CLAUDE_PARTIAL_STREAMING}" \
    MIC_PASSTHROUGH="${MIC_PASSTHROUGH}" \
    bash "${REPO_DIR}/bin/install-vscode.sh"
fi

# 9. Record provisioning timestamps so the control panel can surface when this VM
#    was first installed and last (re)provisioned. INSTALLED_AT is written once and
#    preserved across reprovisions (a fresh install has no marker yet); it also
#    heals a VM provisioned before this marker existed by seeding it now.
#    REPROVISIONED_AT is rewritten on EVERY successful run, so a reprovision moves
#    it. Written last so it only records a provision that reached the end. The file
#    is a config.env-style KEY=VALUE so config-set.sh's idempotent merge and the
#    control panel's sed reader both handle it. Best-effort: never abort the run.
MARKER_FILE="${MARKER_FILE:-/etc/construct/provisioned.env}"
mark() { bash "${REPO_DIR}/bin/config-set.sh" "${MARKER_FILE}" "$1" "$2"; }
record_timestamps() {
  local now
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)" || return
  if [[ -f "${MARKER_FILE}" ]] && grep -Eq '^INSTALLED_AT=.+' "${MARKER_FILE}"; then
    note "    INSTALLED_AT preserved (first install unchanged)"
  else
    mark INSTALLED_AT "${now}" || return
    note "    INSTALLED_AT=${now}"
  fi
  mark REPROVISIONED_AT "${now}" || return
  note "    REPROVISIONED_AT=${now}"
  chmod 0644 "${MARKER_FILE}"
}
run_step optional "Recording provisioning timestamps" record_timestamps

_finish_provision 0
