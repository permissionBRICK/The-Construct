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

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
# The user that Claude Code (CLI + VS Code extension) is installed and
# configured for. Defaults to the invoking sudo user, falling back to root, but
# can be overridden (e.g. by provision.sh, which forces root for VS Code use).
TARGET_USER="${TARGET_USER:-${SUDO_USER:-root}}"

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo ${REPO_DIR}/bin/install-ai-tools.sh"
  exit 1
fi

if [[ ! -f "${CONFIG_FILE}" ]]; then
  err "Missing config file: ${CONFIG_FILE}"
  exit 1
fi

set -a
# shellcheck disable=SC1090
. "${CONFIG_FILE}"
set +a

AI_TOOLS="${AI_TOOLS:-}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
OPENCODE_HOST="${OPENCODE_HOST:-0.0.0.0}"
OPENCODE_PORT="${OPENCODE_PORT:-4096}"
CODEX_HOST="${CODEX_HOST:-0.0.0.0}"
CODEX_PORT="${CODEX_PORT:-4500}"
CODEX_TOKEN_FILE="${CODEX_TOKEN_FILE:-/etc/construct/codex-app-server.token}"

has_tool() {
  case ",${AI_TOOLS}," in
    *,"$1",*) return 0 ;;
    *) return 1 ;;
  esac
}

# Path to the system prompt shipped in the repo, plus the DNS name this VM is
# reachable under from the user's machine. The DNS is derived from the live
# hostname (Hyper-V publishes "<hostname>.mshome.net"), matching what
# print-connection-info.sh advertises.
AGENT_SYSTEM_PROMPT_SRC="${REPO_DIR}/config/systemprompt.md"
AGENT_DNS="$(hostname).mshome.net"

# Render the shipped system prompt (substituting the live DNS name) into a tool's
# GLOBAL agent-instructions file so it applies to every repo the agent touches
# under that user. We overwrite the destination: it is a managed file owned by
# the provisioning flow, regenerated on every (re-)provision so the hostname and
# wording stay current. Relies on AGENT_SYSTEM_PROMPT_SRC existing.
install_agent_system_prompt() {
  local dest_file="$1"
  local owner="$2"
  local dest_dir
  dest_dir="$(dirname "${dest_file}")"

  if [[ ! -f "${AGENT_SYSTEM_PROMPT_SRC}" ]]; then
    warn "WARNING: system prompt not found at ${AGENT_SYSTEM_PROMPT_SRC}; skipping ${dest_file}"
    return 0
  fi

  step "Installing global agent system prompt to ${dest_file}"
  install -d -m 0755 "${dest_dir}"
  sed "s|__AGENT_DNS__|${AGENT_DNS}|g" "${AGENT_SYSTEM_PROMPT_SRC}" >"${dest_file}"
  chown "${owner}:${owner}" "${dest_file}" 2>/dev/null || true
}

install_opencode() {
  step "Installing opencode CLI"
  # Always run the official installer: on a fresh VM it installs opencode, and on
  # a re-provision it updates an existing install to the latest version (the
  # installer fetches the newest release). When opencode is already present a
  # failed update (e.g. no network) is non-fatal -- we keep the working copy
  # rather than aborting provisioning.
  if command -v opencode >/dev/null 2>&1; then
    note "opencode already installed; updating to the latest version"
    if ! curl -fsSL https://opencode.ai/install | bash; then
      warn "opencode update failed; keeping the existing version"
    fi
  else
    curl -fsSL https://opencode.ai/install | bash
  fi

  opencode_bin="$(command -v opencode || true)"
  if [[ "${opencode_bin}" == "/usr/local/bin/opencode" ]]; then
    # On a re-provision the symlink we manage is already on PATH, so command -v
    # reports it back to us. Ignore it here and resolve the real install
    # location below; otherwise we would symlink /usr/local/bin/opencode to
    # itself (a circular symlink) and opencode-serve fails with 203/EXEC.
    opencode_bin=""
  fi
  if [[ -z "${opencode_bin}" ]]; then
    # The installer drops the binary under $HOME (root when run via sudo).
    for candidate in \
      /root/.opencode/bin/opencode \
      /root/.local/bin/opencode \
      "${HOME:-/root}/.opencode/bin/opencode" \
      "${HOME:-/root}/.local/bin/opencode"; do
      if [[ -x "${candidate}" ]]; then
        opencode_bin="${candidate}"
        break
      fi
    done
  fi
  if [[ -z "${opencode_bin}" ]]; then
    # Last resort: search common install roots for the binary.
    opencode_bin="$(find /root /home /usr/local -maxdepth 4 -type f -name opencode -perm -u+x 2>/dev/null | head -n1 || true)"
  fi
  if [[ -z "${opencode_bin}" ]]; then
    warn "opencode install completed, but binary was not found in PATH or common locations"
    exit 1
  fi

  # Resolve through any intermediate symlinks so the link target is the real
  # binary, and never point the symlink at itself.
  opencode_bin="$(readlink -f "${opencode_bin}" 2>/dev/null || echo "${opencode_bin}")"
  if [[ "${opencode_bin}" == "/usr/local/bin/opencode" || ! -x "${opencode_bin}" ]]; then
    warn "refusing to create opencode symlink: resolved path is invalid (${opencode_bin})"
    exit 1
  fi
  ln -sf "${opencode_bin}" /usr/local/bin/opencode

  # Seed the global opencode config (permission=allow) BEFORE starting the
  # service so opencode-serve comes up with prompts disabled. The unit runs as
  # root, so root's config is what the service reads; also seed the TARGET_USER's
  # config (if different) for interactive SSH use.
  configure_opencode_settings "/root" "root"
  install_agent_system_prompt "/root/.config/opencode/AGENTS.md" "root"
  if [[ "${TARGET_USER}" != "root" ]] && id "${TARGET_USER}" >/dev/null 2>&1; then
    configure_opencode_settings "/home/${TARGET_USER}" "${TARGET_USER}"
    install_agent_system_prompt "/home/${TARGET_USER}/.config/opencode/AGENTS.md" "${TARGET_USER}"
  fi

  # The service's WorkingDirectory must exist or systemd fails to start it
  # (status=200/CHDIR). Align the unit's WorkingDirectory with the configured
  # WORKSPACE_ROOT and make sure that directory exists.
  install -d -m 0755 "${WORKSPACE_ROOT}"
  install -m 0644 "${REPO_DIR}/systemd/opencode-serve.service" /etc/systemd/system/opencode-serve.service
  sed -i "s|^WorkingDirectory=.*|WorkingDirectory=${WORKSPACE_ROOT}|" /etc/systemd/system/opencode-serve.service
  systemctl daemon-reload
  systemctl enable opencode-serve
  systemctl restart opencode-serve

  if systemctl is-active --quiet opencode-serve; then
    echo "opencode-serve is running on ${OPENCODE_HOST}:${OPENCODE_PORT}"
  else
    warn "WARNING: opencode-serve failed to start; recent status and logs:"
    systemctl --no-pager --full status opencode-serve >&2 || true
    journalctl -u opencode-serve --no-pager -n 30 >&2 || true
  fi
}

# Merge the permission setting into a user's GLOBAL opencode config
# (~/.config/opencode/opencode.json), preserving any existing settings. Sets
# "permission": "allow" so opencode auto-approves actions without prompting --
# matching the host configuration. Relies on jq (installed by bootstrap.sh).
configure_opencode_settings() {
  local home_dir="$1"
  local owner="$2"
  local settings_dir="${home_dir}/.config/opencode"
  local settings_file="${settings_dir}/opencode.json"

  step "Setting permission=allow in ${settings_file}"
  install -d -m 0755 "${settings_dir}"
  if [[ ! -s "${settings_file}" ]]; then
    echo '{}' >"${settings_file}"
  fi

  local tmp
  tmp="$(mktemp)"
  if jq '.["$schema"] = "https://opencode.ai/config.json" | .permission = "allow"' \
    "${settings_file}" >"${tmp}" 2>/dev/null; then
    cat "${tmp}" >"${settings_file}"
  else
    warn "WARNING: ${settings_file} was not valid JSON; writing minimal settings"
    printf '{\n  "$schema": "https://opencode.ai/config.json",\n  "permission": "allow"\n}\n' >"${settings_file}"
  fi
  rm -f "${tmp}"

  chown -R "${owner}:${owner}" "${settings_dir}" 2>/dev/null || true
}

# Merge the sandbox defaults into a user's Claude Code settings.json, preserving
# any existing settings. Sets IS_SANDBOX=1, bypassPermissions mode, and accepts
# the one-time bypass-mode confirmation dialog so the VM is fully
# non-interactive. Creates the file if missing. Relies on jq (installed by
# bootstrap.sh).
configure_claude_sandbox_setting() {
  local home_dir="$1"
  local owner="$2"
  local settings_dir="${home_dir}/.claude"
  local settings_file="${settings_dir}/settings.json"

  step "Setting IS_SANDBOX=1 and bypassPermissions mode in ${settings_file}"
  install -d -m 0755 "${settings_dir}"
  if [[ ! -s "${settings_file}" ]]; then
    echo '{}' >"${settings_file}"
  fi

  local tmp
  tmp="$(mktemp)"
  if jq '.env.IS_SANDBOX = "1" | .permissions.defaultMode = "bypassPermissions" | .skipDangerousModePermissionPrompt = true' \
    "${settings_file}" >"${tmp}" 2>/dev/null; then
    cat "${tmp}" >"${settings_file}"
  else
    warn "WARNING: ${settings_file} was not valid JSON; writing minimal settings"
    printf '{\n  "env": {\n    "IS_SANDBOX": "1"\n  },\n  "permissions": {\n    "defaultMode": "bypassPermissions"\n  },\n  "skipDangerousModePermissionPrompt": true\n}\n' >"${settings_file}"
  fi
  rm -f "${tmp}"

  chown -R "${owner}:${owner}" "${settings_dir}" 2>/dev/null || true
}

# Merge the bypass-permissions defaults into a user's VS Code Remote-SSH
# machine-scope settings, used by the Claude Code VS Code extension. This is the
# file VS Code writes when you edit the "Remote [SSH]" settings scope; seeding it
# means the extension comes up in bypass mode without manual UI configuration.
# Existing settings are preserved. Relies on jq (installed by bootstrap.sh).
configure_claude_vscode_setting() {
  local home_dir="$1"
  local owner="$2"
  local settings_dir="${home_dir}/.vscode-server/data/Machine"
  local settings_file="${settings_dir}/settings.json"

  step "Setting Claude Code extension bypass defaults in ${settings_file}"
  install -d -m 0755 "${settings_dir}"
  if [[ ! -s "${settings_file}" ]]; then
    echo '{}' >"${settings_file}"
  fi

  local tmp
  tmp="$(mktemp)"
  if jq '.["claudeCode.allowDangerouslySkipPermissions"] = true | .["claudeCode.initialPermissionMode"] = "bypassPermissions"' \
    "${settings_file}" >"${tmp}" 2>/dev/null; then
    cat "${tmp}" >"${settings_file}"
  else
    warn "WARNING: ${settings_file} was not valid JSON; writing minimal settings"
    printf '{\n  "claudeCode.allowDangerouslySkipPermissions": true,\n  "claudeCode.initialPermissionMode": "bypassPermissions"\n}\n' >"${settings_file}"
  fi
  rm -f "${tmp}"

  # Own the whole .vscode-server tree if we created it; harmless if it predates us.
  chown -R "${owner}:${owner}" "${home_dir}/.vscode-server" 2>/dev/null || true
}

install_claude_code() {
  step "Installing Claude Code CLI"
  if command -v claude >/dev/null 2>&1; then
    note "Claude Code already installed"
  elif [[ "${TARGET_USER}" != "root" ]] && id "${TARGET_USER}" >/dev/null 2>&1; then
    sudo -H -u "${TARGET_USER}" bash -lc 'curl -fsSL https://claude.ai/install.sh | bash'
    if [[ -x "/home/${TARGET_USER}/.local/bin/claude" ]]; then
      ln -sf "/home/${TARGET_USER}/.local/bin/claude" /usr/local/bin/claude
    fi
  else
    curl -fsSL https://claude.ai/install.sh | bash
    if [[ -x /root/.local/bin/claude ]]; then
      ln -sf /root/.local/bin/claude /usr/local/bin/claude
    fi
  fi

  # Apply the sandbox defaults for whichever user runs Claude Code, regardless of
  # whether we just installed it or it was already present. Covers both the CLI
  # (~/.claude/settings.json) and the VS Code extension (Remote-SSH machine scope).
  local claude_home claude_owner
  if [[ "${TARGET_USER}" != "root" ]] && id "${TARGET_USER}" >/dev/null 2>&1; then
    claude_home="/home/${TARGET_USER}"
    claude_owner="${TARGET_USER}"
  else
    claude_home="/root"
    claude_owner="root"
  fi
  configure_claude_sandbox_setting "${claude_home}" "${claude_owner}"
  configure_claude_vscode_setting "${claude_home}" "${claude_owner}"
  install_agent_system_prompt "${claude_home}/.claude/CLAUDE.md" "${claude_owner}"
}

# Set a top-level TOML key idempotently: replace the existing assignment if the
# key is already present, otherwise insert it ABOVE any [section] header so it
# lands in the top-level table (TOML keys after a header belong to that table).
# Used for Codex's config.toml, which is TOML and so can't go through jq.
set_toml_top_key() {
  local file="$1" key="$2" value="$3"
  local line="${key} = ${value}"
  if grep -Eq "^[[:space:]]*${key}[[:space:]]*=" "${file}"; then
    sed -i "0,/^[[:space:]]*${key}[[:space:]]*=.*/s//${line}/" "${file}"
  elif [[ -s "${file}" ]]; then
    sed -i "1i ${line}" "${file}"
  else
    printf '%s\n' "${line}" >"${file}"
  fi
}

# Seed Codex's permission-skip settings into the user's config.toml so it runs
# unattended -- no approval prompts and full filesystem access -- matching the
# host configuration. Any other existing keys in the file are preserved.
configure_codex_settings() {
  local home_dir="$1"
  local owner="$2"
  local config_dir="${home_dir}/.codex"
  local config_file="${config_dir}/config.toml"

  step "Seeding Codex permission + trusted-project settings in ${config_file}"
  install -d -m 0700 "${config_dir}"
  [[ -f "${config_file}" ]] || : >"${config_file}"

  # Top-level permission skips (root table -- must precede any [table] header).
  set_toml_top_key "${config_file}" "default_permissions" '":danger-full-access"'
  set_toml_top_key "${config_file}" "sandbox_mode"        '"danger-full-access"'
  set_toml_top_key "${config_file}" "approval_policy"     '"never"'

  # Mark the workspace repos directory as a trusted project so Codex doesn't
  # prompt for trust on first use. Appended as its own TOML table after the
  # top-level keys; created once and left alone on re-runs so we never duplicate
  # the header (a duplicated table header is a TOML parse error).
  local proj_header="[projects.\"${WORKSPACE_ROOT}\"]"
  if ! grep -Fqx "${proj_header}" "${config_file}"; then
    printf '\n%s\ntrust_level = "trusted"\n' "${proj_header}" >>"${config_file}"
  fi

  chown -R "${owner}:${owner}" "${config_dir}" 2>/dev/null || true
}

install_codex() {
  step "Installing Codex CLI"
  if ! command -v codex >/dev/null 2>&1; then
    # The installer ends with an interactive "start codex now? [y/n]" prompt,
    # which hangs unattended provisioning (no terminal to answer it). Download it
    # and run with 'n' on stdin -- we don't want the installer to launch codex;
    # the codex-app-server systemd unit below manages it. CI=1 is an extra hint.
    codex_installer="$(mktemp)"
    curl -fsSL https://chatgpt.com/codex/install.sh -o "${codex_installer}"
    printf 'n\n' | CI=1 sh "${codex_installer}"
    rm -f "${codex_installer}"
  else
    note "Codex already installed"
  fi

  codex_bin="$(command -v codex || true)"
  if [[ -z "${codex_bin}" && -x /root/.local/bin/codex ]]; then
    codex_bin=/root/.local/bin/codex
  fi
  if [[ -z "${codex_bin}" && -x /root/.codex/bin/codex ]]; then
    codex_bin=/root/.codex/bin/codex
  fi
  if [[ -z "${codex_bin}" ]]; then
    warn "Codex install completed, but binary was not found in PATH or common root locations"
    exit 1
  fi

  ln -sf "${codex_bin}" /usr/local/bin/codex

  if [[ ! -f "${CODEX_TOKEN_FILE}" ]]; then
    install -d -m 0700 "$(dirname "${CODEX_TOKEN_FILE}")"
    od -An -N32 -tx1 /dev/urandom | tr -d ' \n' >"${CODEX_TOKEN_FILE}"
    printf '\n' >>"${CODEX_TOKEN_FILE}"
    chmod 0600 "${CODEX_TOKEN_FILE}"
  fi

  warn "WARNING: Codex app-server WebSocket is experimental. This template binds it to ${CODEX_HOST}:${CODEX_PORT}; expose only on trusted VM networks."

  install -d -m 0755 "${WORKSPACE_ROOT}"
  install -m 0644 "${REPO_DIR}/systemd/codex-app-server.service" /etc/systemd/system/codex-app-server.service
  sed -i "s|^WorkingDirectory=.*|WorkingDirectory=${WORKSPACE_ROOT}|" /etc/systemd/system/codex-app-server.service
  systemctl daemon-reload
  systemctl enable codex-app-server
  systemctl restart codex-app-server

  # Seed the permission-skip settings for whichever user runs Codex, regardless
  # of whether we just installed it or it was already present. provision.sh forces
  # root, so this normally lands in /root/.codex/config.toml.
  local codex_home codex_owner
  if [[ "${TARGET_USER}" != "root" ]] && id "${TARGET_USER}" >/dev/null 2>&1; then
    codex_home="/home/${TARGET_USER}"
    codex_owner="${TARGET_USER}"
  else
    codex_home="/root"
    codex_owner="root"
  fi
  configure_codex_settings "${codex_home}" "${codex_owner}"
  install_agent_system_prompt "${codex_home}/.codex/AGENTS.md" "${codex_owner}"
}

if has_tool opencode; then
  install_opencode
fi

if has_tool claude-code; then
  install_claude_code
fi

if has_tool codex; then
  install_codex
fi

if has_tool pi; then
  warn "pi selected, but no installer is implemented yet. Selection is recorded in ${CONFIG_FILE}."
fi

install -m 0755 "${REPO_DIR}/bin/print-connection-info.sh" /usr/local/bin/construct-print-connection-info
install -m 0644 "${REPO_DIR}/systemd/construct-console-info.service" /etc/systemd/system/construct-console-info.service
systemctl daemon-reload
systemctl enable construct-console-info

"${REPO_DIR}/bin/update-login-banner.sh"
"${REPO_DIR}/bin/print-connection-info.sh"
