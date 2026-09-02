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
#
# Every step body in this file is invoked INDIRECTLY -- `run_step <criticality>
# "<title>" <function> [args]` calls it through "$@" -- which ShellCheck cannot
# see, so it reports each of them as unreachable code. That is the one diagnostic
# this file would otherwise be full of, and it hides real findings.
# shellcheck disable=SC2317
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
  # Drop the "provisioning is running" marker the guest activity heartbeat reads
  # (plan §4.7). Only set on the real provisioning path, so the step-runner unit
  # test never touches it.
  if [[ -n "${_PROVISION_MARKER:-}" ]]; then rm -f "${_PROVISION_MARKER}" 2>/dev/null || true; fi
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

# ── Free-disk preflight ──────────────────────────────────────────────────────
# A full VM disk is the single most misleading failure mode this script has, so
# it is checked BEFORE anything installs. ext4 reserves 5% of every filesystem
# for uid 0, which makes a full disk look like a permission bug: root's own
# writes keep succeeding while every write as another user fails with a cryptic
# errno. The field report was exactly that -- root's git identity was written,
# and the same three writes for the agent user died with
#   error: failed to write new configuration file /home/agent/.gitconfig.lock
# which is git's message for a failed write(2) (ENOSPC/EDQUOT), NOT for a
# permission or lock problem. So: state the free space up front, and stop rather
# than emit a screenful of unrelated-looking errors from a disk that is full.
_DISK_FULL_KB="${_DISK_FULL_KB:-262144}"   # 256 MiB -- below this, stop
_DISK_LOW_KB="${_DISK_LOW_KB:-2097152}"    # 2 GiB   -- below this, warn

# "full" | "low" | "ok" for a free-space reading in KiB. Pure (unit-tested).
_disk_verdict() {
  local avail_kb="$1"
  if [[ -z "${avail_kb}" || ! "${avail_kb}" =~ ^[0-9]+$ ]]; then printf 'unknown'; return 0; fi
  if (( avail_kb < _DISK_FULL_KB )); then printf 'full'
  elif (( avail_kb < _DISK_LOW_KB )); then printf 'low'
  else printf 'ok'; fi
}

# df's Avail column already excludes the root reserve, i.e. it is what an
# unprivileged write actually has left -- which is the number that matters here.
_df_field() { df -P -k "$1" 2>/dev/null | awk 'NR==2 {print $'"$2"'}'; }

# Report free space on every filesystem provisioning writes to (deduped by
# device, since / /home /root/repos are usually one filesystem). Returns 1 when
# any of them is full, unless ALLOW_LOW_DISK=true downgrades that to a warning.
check_disk_space() {
  local rc=0 seen="" path dev avail used verdict
  for path in / /home "${WORKSPACE_ROOT:-/root/repos}" /var; do
    [[ -d "${path}" ]] || continue
    dev="$(_df_field "${path}" 1)"
    [[ -n "${dev}" ]] || continue
    case " ${seen} " in *" ${dev} "*) continue ;; esac
    seen="${seen} ${dev}"
    avail="$(_df_field "${path}" 4)"
    used="$(_df_field "${path}" 5)"
    verdict="$(_disk_verdict "${avail}")"
    case "${verdict}" in
      ok)   ok   "  ${path} (${dev}): $((avail / 1048576)) GiB free, ${used} used" ;;
      low)  warn "  ${path} (${dev}): only $((avail / 1024)) MiB free (${used} used) -- installs may run out of space" ;;
      full)
        err "  ${path} (${dev}): only $((avail / 1024)) MiB free (${used} used) -- the disk is FULL"
        err "  ext4 keeps a 5% reserve for root, so root-owned writes still succeed while writes as"
        err "  ${SSH_USER:-agent} fail with confusing errors (e.g. git: 'failed to write new configuration file"
        err "  /home/${SSH_USER:-agent}/.gitconfig.lock'). Free space on the VM or grow its disk, then re-provision."
        err "  Set ALLOW_LOW_DISK=true to provision anyway."
        rc=1
        ;;
      *)    note "  ${path} (${dev}): free space unknown" ;;
    esac
  done
  if [[ "${rc}" -ne 0 && "${ALLOW_LOW_DISK:-false}" == "true" ]]; then
    warn "  ALLOW_LOW_DISK=true -- continuing on a full disk; later steps may fail"
    rc=0
  fi
  return "${rc}"
}

# Explain a failed `git config` write for one user. git prints "failed to write
# new configuration file <path>.lock" (exit 4) only when the write(2) into the
# lock file failed -- the filesystem is out of space or over quota -- never for a
# permission or locking problem. Thanks to the same 5% root reserve this appears
# as root's identity being written while the agent user's three writes all fail,
# so say what actually happened instead of leaving three cryptic git lines.
_git_write_diag() {
  local user="$1" home="$2" avail
  avail="$(_df_field "${home}" 4)"
  [[ "${avail}" =~ ^[0-9]+$ ]] || return 0
  if [[ "$(_disk_verdict "${avail}")" != "ok" ]]; then
    warn "  ^ not a permissions problem: the filesystem holding ${home} has only"
    warn "    $((avail / 1024)) MiB free, so git could not write ${user}'s config."
    warn "    Free space on the VM, then re-provision."
  fi
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

# "Provisioning is running" marker for the guest activity heartbeat (§4.7): a
# provision must keep a remote VM alive even with nobody connected. It carries
# this run's PID so a run killed mid-flight (dropped SSH, hard reset) cannot pin
# the VM as busy forever, and it lives on tmpfs so a reboot clears it. Silent by
# design -- the default path's output stays byte-identical.
_PROVISION_MARKER="${_PROVISION_MARKER:-/run/construct/provisioning}"
mkdir -p "$(dirname "${_PROVISION_MARKER}")" 2>/dev/null || true
printf '%s\n' "$$" >"${_PROVISION_MARKER}" 2>/dev/null || true

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
# T3 Code install channel: "stable" (npm @latest) or "nightly" (npm @nightly).
# Same keep-saved semantics: empty keeps the VM's saved channel, so a console
# reprovision never flips a nightly user back to stable.
_t3code_channel_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _t3code_channel_saved="$(sed -n 's/^T3CODE_CHANNEL=//p' "${CONFIG_FILE}" | head -1 || true)"
fi
T3CODE_CHANNEL="${T3CODE_CHANNEL:-${_t3code_channel_saved:-stable}}"
[[ "${T3CODE_CHANNEL}" == "nightly" ]] || T3CODE_CHANNEL=stable
# Opt-in T3 Code extra-feature patch set: Claude usage-limit auto-resume plus
# OpenCode background-watcher monitoring. The legacy variable name is retained
# so existing host settings and config.env files migrate without changing their
# effective preference. Disabled by default; same keep-saved semantics as T3CODE.
_t3code_limit_resume_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _t3code_limit_resume_saved="$(sed -n 's/^T3CODE_LIMIT_RESUME=//p' "${CONFIG_FILE}" | head -1 || true)"
fi
T3CODE_LIMIT_RESUME="${T3CODE_LIMIT_RESUME:-${_t3code_limit_resume_saved:-false}}"
[[ "${T3CODE_LIMIT_RESUME}" == "true" ]] || T3CODE_LIMIT_RESUME=false
# Optional dependency-free OpenCode background watcher plugin. Separate from
# the T3 patch set: this controls whether OpenCode itself exposes the
# background/background_output/background_kill tools. Empty input keeps the
# VM's saved preference across a console reprovision.
_opencode_background_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _opencode_background_saved="$(sed -n 's/^OPENCODE_BACKGROUND_WATCHER=//p' "${CONFIG_FILE}" | head -1 || true)"
fi
OPENCODE_BACKGROUND_WATCHER="${OPENCODE_BACKGROUND_WATCHER:-${_opencode_background_saved:-false}}"
[[ "${OPENCODE_BACKGROUND_WATCHER}" == "true" ]] || OPENCODE_BACKGROUND_WATCHER=false

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

# External host/port: the client-reachable identity of this VM (B2 interface
# contract). Precedence: explicit non-empty env value > value saved in config.env
# > built-in default. Empty env value means "keep the VM's saved choice", matching
# the VSCODE_TUNNEL idiom used throughout this file.
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
_external_host_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _external_host_saved="$(_cfg_unquote "$(sed -n 's/^CONSTRUCT_EXTERNAL_HOST=//p' "${CONFIG_FILE}" | head -1 || true)")"
fi
CONSTRUCT_EXTERNAL_HOST="${CONSTRUCT_EXTERNAL_HOST:-${_external_host_saved:-}}"

_external_ssh_port_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _external_ssh_port_saved="$(_cfg_unquote "$(sed -n 's/^CONSTRUCT_EXTERNAL_SSH_PORT=//p' "${CONFIG_FILE}" | head -1 || true)")"
fi
CONSTRUCT_EXTERNAL_SSH_PORT="${CONSTRUCT_EXTERNAL_SSH_PORT:-${_external_ssh_port_saved:-22}}"

# Host service (B8 interface contract): the constructd instance this VM belongs
# to, if any. Empty -- the default, and every local Hyper-V install -- means
# "there is no service": `construct expose` uses the guest spool and no heartbeat
# timer is installed. Same three-level precedence as the keys above.
_service_url_saved=""
_instance_name_saved=""
_idle_interval_saved=""
if [[ -f "${CONFIG_FILE}" ]]; then
  _service_url_saved="$(_cfg_unquote "$(sed -n 's/^CONSTRUCT_SERVICE_URL=//p' "${CONFIG_FILE}" | head -1 || true)")"
  _instance_name_saved="$(_cfg_unquote "$(sed -n 's/^CONSTRUCT_INSTANCE_NAME=//p' "${CONFIG_FILE}" | head -1 || true)")"
  _idle_interval_saved="$(_cfg_unquote "$(sed -n 's/^CONSTRUCT_IDLE_REPORT_INTERVAL_SEC=//p' "${CONFIG_FILE}" | head -1 || true)")"
fi
CONSTRUCT_SERVICE_URL="${CONSTRUCT_SERVICE_URL:-${_service_url_saved:-}}"
CONSTRUCT_INSTANCE_NAME="${CONSTRUCT_INSTANCE_NAME:-${_instance_name_saved:-$(hostname 2>/dev/null | tr '[:upper:]' '[:lower:]' || echo vm)}}"
CONSTRUCT_IDLE_REPORT_INTERVAL_SEC="${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC:-${_idle_interval_saved:-60}}"
# Positive and bounded, not just numeric: systemd treats OnUnitActiveSec=0 as a
# DISABLED timer, so a stray 0 here would silently stop a service-managed VM from
# ever reporting -- and a VM that never reports gets saved as idle. Anything
# unusable falls back to the default. (Same bounds as construct-idle-report.sh.)
if ! [[ "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}" =~ ^[0-9]+$ ]] \
  || (( CONSTRUCT_IDLE_REPORT_INTERVAL_SEC < 5 || CONSTRUCT_IDLE_REPORT_INTERVAL_SEC > 3600 )); then
  CONSTRUCT_IDLE_REPORT_INTERVAL_SEC=60
fi

# Belt and braces for GENERIC install media (plan section 4.10). A VM installed from
# the pre-built ISO boots as 'construct-seed' and adopts the name the hypervisor gave
# it at first boot. If that never happened -- no KVP daemon on this generation of VM,
# a hypervisor with no such channel -- the VM is unreachable as <name>.mshome.net,
# because the switch's DNS publishes the guest's OWN hostname. Provisioning knows the
# name the host asked for, so it fixes it here.
#
# Everything else is untouched: the guard is the placeholder hostname, which only
# generic media ever has.
SEED_PLACEHOLDER_HOSTNAME="construct-seed"

adopt_seed_hostname() {
  # adopt_seed_hostname <current hostname> <wanted name>
  # Returns 1 (and changes nothing) when this is not a seed VM or the name is unusable.
  local current="$1" wanted="$2"
  local etc="${PROVISION_ETC_DIR:-/etc}"   # overridden by test/provision-hostname.test.sh

  [[ "${current}" == "${SEED_PLACEHOLDER_HOSTNAME}" ]] || return 1

  wanted="$(printf '%s' "${wanted}" | tr '[:upper:]' '[:lower:]')"
  [[ -n "${wanted}" && "${wanted}" != "${SEED_PLACEHOLDER_HOSTNAME}" ]] || return 1
  # A DNS label: it is what the virtual switch's DNS will publish.
  [[ "${wanted}" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] || return 1

  if ! hostnamectl set-hostname "${wanted}" 2>/dev/null; then
    printf '%s\n' "${wanted}" >"${etc}/hostname" || return 1
    hostname "${wanted}" 2>/dev/null || true
  fi

  # 127.0.1.1 is how Debian/Ubuntu resolve the machine's own name; a stale entry
  # there makes every self-lookup (sudo included) wait for a timeout.
  if grep -q '^127\.0\.1\.1' "${etc}/hosts" 2>/dev/null; then
    sed -i "s/^127\.0\.1\.1.*/127.0.1.1\t${wanted}/" "${etc}/hosts"
  else
    printf '127.0.1.1\t%s\n' "${wanted}" >>"${etc}/hosts"
  fi

  printf '%s' "${wanted}"
}

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
note "    T3CODE_CHANNEL=${T3CODE_CHANNEL}"
note "    T3CODE_LIMIT_RESUME=${T3CODE_LIMIT_RESUME}"
note "    OPENCODE_BACKGROUND_WATCHER=${OPENCODE_BACKGROUND_WATCHER}"
note "    SMB_SHARE=${SMB_SHARE:-(saved/default)}"
# Only mention the external identity when it carries information, so a default
# install's provisioning log is unchanged.
if [[ -n "${CONSTRUCT_EXTERNAL_HOST}" || "${CONSTRUCT_EXTERNAL_SSH_PORT}" != "22" ]]; then
  note "    CONSTRUCT_EXTERNAL_HOST=${CONSTRUCT_EXTERNAL_HOST:-(empty=auto hostname.mshome.net)}"
  note "    CONSTRUCT_EXTERNAL_SSH_PORT=${CONSTRUCT_EXTERNAL_SSH_PORT}"
fi
# Same rule for the host service: nothing new is printed on a local install.
if [[ -n "${CONSTRUCT_SERVICE_URL}" ]]; then
  note "    CONSTRUCT_SERVICE_URL=${CONSTRUCT_SERVICE_URL}"
  note "    CONSTRUCT_INSTANCE_NAME=${CONSTRUCT_INSTANCE_NAME}"
fi

# Silent on every VM whose hostname is its own -- which is every VM not installed
# from generic media whose first-boot adoption failed.
_current_hostname="$(hostname 2>/dev/null || true)"
if [[ "${_current_hostname}" == "${SEED_PLACEHOLDER_HOSTNAME}" ]]; then
  step "Adopting this VM's name (it still carries the install media's placeholder)"
  _adopted_hostname="$(adopt_seed_hostname "${_current_hostname}" "${CONSTRUCT_INSTANCE_NAME}" || true)"
  if [[ -n "${_adopted_hostname}" ]]; then
    ok "hostname ${SEED_PLACEHOLDER_HOSTNAME} -> ${_adopted_hostname}"
  else
    warn "hostname is still ${SEED_PLACEHOLDER_HOSTNAME}: '${CONSTRUCT_INSTANCE_NAME}' cannot be used as one"
  fi
fi

# Free space FIRST: on a full disk every later step fails in its own confusing
# way (see the _disk_verdict block above), so a crisp stop beats a long summary
# of unrelated-looking errors. Critical, with ALLOW_LOW_DISK=true as the escape
# hatch for "I know, provision anyway".
run_step critical "Checking free disk space" check_disk_space

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
  cfg T3CODE_CHANNEL "${T3CODE_CHANNEL}" || return
  cfg T3CODE_LIMIT_RESUME "${T3CODE_LIMIT_RESUME}" || return
  cfg OPENCODE_BACKGROUND_WATCHER "${OPENCODE_BACKGROUND_WATCHER}" || return
  # Persist the external identity only when it carries information (a non-empty
  # host; a non-default or previously saved port), so a default install's
  # config.env stays byte-identical.
  if [[ -n "${CONSTRUCT_EXTERNAL_HOST}" ]]; then
    cfg CONSTRUCT_EXTERNAL_HOST "${CONSTRUCT_EXTERNAL_HOST}" || return
  fi
  if [[ -n "${_external_ssh_port_saved}" || "${CONSTRUCT_EXTERNAL_SSH_PORT}" != "22" ]]; then
    cfg CONSTRUCT_EXTERNAL_SSH_PORT "${CONSTRUCT_EXTERNAL_SSH_PORT}" || return
  fi
  # Host-service identity: written only for a service-managed VM, so a local
  # install's config.env stays byte-identical (empty = "there is no service").
  if [[ -n "${CONSTRUCT_SERVICE_URL}" ]]; then
    cfg CONSTRUCT_SERVICE_URL "${CONSTRUCT_SERVICE_URL}" || return
    cfg CONSTRUCT_INSTANCE_NAME "${CONSTRUCT_INSTANCE_NAME}" || return
  fi
  if [[ -n "${_idle_interval_saved}" || "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}" != "60" ]]; then
    cfg CONSTRUCT_IDLE_REPORT_INTERVAL_SEC "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}" || return
  fi
  install -d -m 0755 "${WORKSPACE_ROOT}"
}
run_step critical "Writing configuration to ${CONFIG_FILE}" write_configuration

# 2a. The VM's scoped service token (plan §2/§4.6): it authorizes ONLY this VM's
#     own port forwards and its activity heartbeat. Passed base64-encoded so it
#     survives the SSH/PowerShell layers untouched (like GIT_USER_NAME_B64), and
#     never echoed, logged or written to config.env -- the step reports only that
#     a token was installed. Runs only when the host supplied one, so a local
#     install's output is unchanged.
write_vm_token() {
  local token_file="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}" tmp
  # mkdir, not `install -d -m`: /etc/construct already exists (bootstrap.sh) and
  # its mode is not ours to reset.
  mkdir -p "$(dirname "${token_file}")" || return 1
  tmp="${token_file}.tmp.$$"
  # -di, not -d: a value that travelled through PowerShell/SSH can carry CR or
  # stray whitespace, which strict base64 rejects (field-verified in this path).
  if ! ( umask 077; printf '%s' "${CONSTRUCT_VM_TOKEN_B64}" | base64 -di >"${tmp}" ) 2>/dev/null; then
    rm -f "${tmp}"
    warn "  CONSTRUCT_VM_TOKEN_B64 could not be decoded; ${token_file} left unchanged"
    return 1
  fi
  if [[ ! -s "${tmp}" ]]; then
    rm -f "${tmp}"
    warn "  CONSTRUCT_VM_TOKEN_B64 decoded to nothing; ${token_file} left unchanged"
    return 1
  fi
  chmod 0600 "${tmp}" || { rm -f "${tmp}"; return 1; }
  mv -f "${tmp}" "${token_file}" || { rm -f "${tmp}"; return 1; }
  ok "  VM service token installed (${token_file}, 0600)"
}
if [[ -n "${CONSTRUCT_VM_TOKEN_B64:-}" ]]; then
  run_step optional "Installing the VM service token" write_vm_token
fi

# 2b. Global git identity for the users that operate on the VM: CLAUDE_USER
#     (root -- used by VS Code Remote-SSH and the AI tools) and the SSH/seed user
#     (interactive logins). Values arrive from the host, defaulted there to the
#     host's own git identity; empty values are left unchanged. Deliberately NOT
#     written to config.env (it is `source`-d by other scripts, and a name with a
#     space would break that) -- `git config --global` is the store on the VM.
#     Optionally also enables git's plaintext credential store (the host warns
#     about the security trade-off before this is requested).
#     A failed write here is almost never about permissions -- see _git_write_diag.
configure_git_identity() {
  _git_seen=""
  _git_failed=0
  for _gu in "${CLAUDE_USER}" "${SSH_USER}"; do
    [[ -n "${_gu}" ]] || continue
    case " ${_git_seen} " in *" ${_gu} "*) continue ;; esac
    _git_seen="${_git_seen} ${_gu}"
    _gu_home="$(getent passwd "${_gu}" | cut -d: -f6)"
    if [[ -z "${_gu_home}" ]]; then warn "  skipping ${_gu}: no home directory"; _git_failed=1; continue; fi
    # Per-user, so the "why" below is printed for EVERY user whose writes failed
    # (_git_failed is sticky across users and can't answer that question).
    _gu_failed=0
    if [[ -n "${GIT_USER_NAME}" ]]; then
      sudo -H -u "${_gu}" git config --global user.name "${GIT_USER_NAME}" \
        || { warn "  could not set user.name for ${_gu}"; _git_failed=1; _gu_failed=1; }
    fi
    if [[ -n "${GIT_USER_EMAIL}" ]]; then
      sudo -H -u "${_gu}" git config --global user.email "${GIT_USER_EMAIL}" \
        || { warn "  could not set user.email for ${_gu}"; _git_failed=1; _gu_failed=1; }
    fi
    # Plaintext credential store: enable when requested; when explicitly declined,
    # remove only a store helper we may have set before (don't clobber another).
    _cred="(unchanged)"
    if [[ "${GIT_CREDENTIAL_STORE}" == "true" ]]; then
      if sudo -H -u "${_gu}" git config --global credential.helper store; then _cred="store (plaintext)"
      else warn "  could not enable credential.helper for ${_gu}"; _git_failed=1; _gu_failed=1; fi
    elif [[ "${GIT_CREDENTIAL_STORE}" == "false" ]]; then
      if [[ "$(sudo -H -u "${_gu}" git config --global credential.helper 2>/dev/null || true)" == "store" ]]; then
        sudo -H -u "${_gu}" git config --global --unset-all credential.helper || true
        _cred="disabled"
      fi
    fi
    if [[ "${_gu_failed}" -ne 0 ]]; then
      _git_write_diag "${_gu}" "${_gu_home}"
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
    OPENCODE_BACKGROUND_WATCHER="${OPENCODE_BACKGROUND_WATCHER}" \
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
    T3CODE_CHANNEL="${T3CODE_CHANNEL}" T3CODE_LIMIT_RESUME="${T3CODE_LIMIT_RESUME}" \
    bash "${REPO_DIR}/bin/install-ai-tools.sh"
else
  # A disabled T3 deployment must not cause the host handoff to offer a stale
  # patched Desktop installer from an earlier provision.
  rm -f /etc/construct/t3code-desktop-status
  rm -f /etc/construct/t3code-installed-build
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" CONSTRUCT_T3_VOICE_INPUT false
  if [[ -f /etc/systemd/system/t3code-serve.service ]]; then
    run_step optional "Disabling T3 Code web GUI (T3CODE=false)" \
      systemctl disable --now t3code-serve
  fi
fi

run_step optional "Installing AI tool console integration" \
  env TARGET_USER="${CLAUDE_USER}" AI_TOOLS_OVERRIDE=none AI_CONSOLE_INTEGRATION=true \
  bash "${REPO_DIR}/bin/install-ai-tools.sh"

# 4b. Install the construct CLI so agents and users can manage project profiles,
#     raise host desktop notifications and expose ports from the VM shell
#     (`construct project set|get|list`, `construct notify`, `construct expose`).
#     Runs every provision so an updated script always gets redeployed on
#     reprovision. Silent (install prints nothing), including the forward spool:
#     the default path's output must stay byte-identical.
install_construct_cli() {
  local bin_dir="${CONSTRUCT_BIN_DIR:-/usr/local/bin}"
  local forwards_dir="${CONSTRUCT_FORWARDS_DIR:-/etc/construct/forwards}"
  install -m 0755 "${REPO_DIR}/bin/construct" "${bin_dir}/construct" || return 1
  # `construct expose` execs this next to itself; the heartbeat unit runs the
  # reporter from the same directory.
  install -m 0755 "${REPO_DIR}/bin/construct-expose.sh" "${bin_dir}/construct-expose.sh" || return 1
  install -m 0755 "${REPO_DIR}/bin/construct-idle-report.sh" "${bin_dir}/construct-idle-report.sh" || return 1
  # Forward spool (docs/expose.md): root-owned 0755, deliberately NOT 1777 like
  # the notification spool -- a request opens a port on the user's PC.
  install -d -m 0755 "${forwards_dir}" "${forwards_dir}/requests" \
    "${forwards_dir}/acks" "${forwards_dir}/close" || return 1
}
run_step optional "Installing construct CLI" install_construct_cli

# 4c. Notification spool for `construct notify`. On tmpfs (/run) deliberately: a
#     reboot must not replay stale notifications at the host. The tmpfiles.d entry
#     recreates it on every boot; --create makes it exist right now too.
setup_notify_spool() {
  install -m 0644 "${REPO_DIR}/systemd/construct-notify.conf" /etc/tmpfiles.d/construct-notify.conf || return 1
  systemd-tmpfiles --create /etc/tmpfiles.d/construct-notify.conf 2>/dev/null || install -d -m 1777 /run/construct/notify
  ok "  notification spool: /run/construct/notify"
}
run_step optional "Setting up the notification spool" setup_notify_spool

# 4d. Guest activity heartbeat (plan §4.7). ONLY for a service-managed VM: the
#     host service is what enforces idle policy, and a local install must gain no
#     new units at all. Both branches are silent on the default path -- the enable
#     branch cannot run without CONSTRUCT_SERVICE_URL, and the disable branch says
#     nothing (it has nothing to do unless a service-managed VM was moved back).
setup_idle_report_timer() {
  local interval="${1:-60}"
  # Belt and braces with the resolution above: a zero/absurd interval must never
  # reach the unit file, where systemd would read it as "timer disabled".
  if ! [[ "${interval}" =~ ^[0-9]+$ ]] || (( interval < 5 || interval > 3600 )); then
    interval=60
  fi
  local unit_dir="${CONSTRUCT_SYSTEMD_DIR:-/etc/systemd/system}"
  local systemctl_bin="${CONSTRUCT_SYSTEMCTL:-systemctl}"
  install -d -m 0755 "${unit_dir}" || return 1
  install -m 0644 "${REPO_DIR}/systemd/construct-idle-report.service" "${unit_dir}/construct-idle-report.service" || return 1
  install -m 0644 "${REPO_DIR}/systemd/construct-idle-report.timer" "${unit_dir}/construct-idle-report.timer" || return 1
  if [[ "${interval}" != "60" ]]; then
    sed -i -e "s/^OnBootSec=.*/OnBootSec=${interval}/" \
           -e "s/^OnUnitActiveSec=.*/OnUnitActiveSec=${interval}/" \
           "${unit_dir}/construct-idle-report.timer" || return 1
  fi
  "${systemctl_bin}" daemon-reload || return 1
  "${systemctl_bin}" enable --now construct-idle-report.timer || return 1
  ok "  activity heartbeat: every ${interval}s to ${CONSTRUCT_SERVICE_URL}"
}
remove_idle_report_timer() {
  local unit_dir="${CONSTRUCT_SYSTEMD_DIR:-/etc/systemd/system}"
  local systemctl_bin="${CONSTRUCT_SYSTEMCTL:-systemctl}"
  [[ -f "${unit_dir}/construct-idle-report.timer" ]] || return 0
  "${systemctl_bin}" disable --now construct-idle-report.timer >/dev/null 2>&1 || true
  rm -f "${unit_dir}/construct-idle-report.timer" "${unit_dir}/construct-idle-report.service"
  "${systemctl_bin}" daemon-reload >/dev/null 2>&1 || true
}
if [[ -n "${CONSTRUCT_SERVICE_URL}" ]]; then
  run_step optional "Setting up the activity heartbeat timer" \
    setup_idle_report_timer "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}"
else
  remove_idle_report_timer || true
fi

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
