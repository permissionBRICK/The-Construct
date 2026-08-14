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
# provision.sh invokes this once per selected tool. Preserve that override across
# sourcing config.env, whose saved AI_TOOLS value otherwise replaces the caller's.
AI_TOOLS_OVERRIDE="${AI_TOOLS_OVERRIDE:-}"
AI_CONSOLE_INTEGRATION="${AI_CONSOLE_INTEGRATION:-true}"
# The user that Claude Code (CLI + VS Code extension) is installed and
# configured for. Defaults to the invoking sudo user, falling back to root, but
# can be overridden (e.g. by provision.sh, which forces root for VS Code use).
TARGET_USER="${TARGET_USER:-${SUDO_USER:-root}}"

# The plain-Bash unit tests source this file for its installer helpers alone:
# skip the root/config preconditions here and the tool dispatch at the bottom,
# so sourcing installs nothing and touches no VM paths.
_FUNCS_ONLY="${CONSTRUCT_AI_TOOLS_FUNCS_ONLY:-false}"

if [[ "${_FUNCS_ONLY}" != "true" ]]; then
  if [[ "${EUID}" -ne 0 ]]; then
    err "Run with sudo: sudo ${REPO_DIR}/bin/install-ai-tools.sh"
    exit 1
  fi

  if [[ ! -f "${CONFIG_FILE}" ]]; then
    err "Missing config file: ${CONFIG_FILE}"
    exit 1
  fi
fi

if [[ "${_FUNCS_ONLY}" != "true" ]]; then
  set -a
  # shellcheck disable=SC1090
  . "${CONFIG_FILE}"
  set +a
fi

AI_TOOLS="${AI_TOOLS_OVERRIDE:-${AI_TOOLS:-}}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
OPENCODE_HOST="${OPENCODE_HOST:-0.0.0.0}"
OPENCODE_PORT="${OPENCODE_PORT:-4096}"
CODEX_HOST="${CODEX_HOST:-0.0.0.0}"
CODEX_PORT="${CODEX_PORT:-4500}"
CODEX_TOKEN_FILE="${CODEX_TOKEN_FILE:-/etc/construct/codex-app-server.token}"
T3CODE_HOST="${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="${T3CODE_PORT:-5177}"
# Channel ("stable"|"nightly") decides the npm dist-tag. stable -> @latest,
# nightly -> @nightly. Anything that isn't exactly "nightly" normalizes to stable.
T3CODE_CHANNEL="${T3CODE_CHANNEL:-stable}"
[[ "${T3CODE_CHANNEL}" == "nightly" ]] || T3CODE_CHANNEL=stable
_t3_npm_tag() { [[ "${T3CODE_CHANNEL}" == "nightly" ]] && echo nightly || echo latest; }
# The local `code serve-web` server on the VM keeps its data (incl. Machine-scope
# settings) here; used to seed the Claude Code bypass defaults for the browser IDE
# too, not just the Remote-SSH server. Mirrors install-vscode.sh's default.
VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB:-true}"
VSCODE_SERVE_WEB_DATA_DIR="${VSCODE_SERVE_WEB_DATA_DIR:-/var/lib/vscode-serve-web}"

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

# Run an official `curl | bash` installer with retries. The opencode installer
# downloads its release archive without curl --fail, so a transient HTTP error
# (rate limit, CDN hiccup) pipes an error page into tar and the install dies
# mid-run with "gzip: stdin: not in gzip format". Each attempt re-downloads
# into a fresh temp dir, so retrying with backoff is safe and rides out blips.
run_installer_with_retries() {
  local label="$1"; shift
  local attempt
  for attempt in 1 2 3; do
    if "$@"; then
      return 0
    fi
    warn "${label} installer failed (attempt ${attempt}/3)"
    if (( attempt < 3 )); then
      sleep $((attempt * 5))
    fi
  done
  return 1
}

# Resolve the latest opencode release WITHOUT api.github.com. The official
# installer looks the version up through the unauthenticated GitHub REST API,
# which is rate-limited to 60 requests/hour per SOURCE IP -- behind a corporate
# NAT that budget is shared by the whole office and is routinely exhausted, so
# the installer exits 1 with "Failed to fetch version information" on every
# attempt (retrying just hits the same wall; the quota resets hourly). The plain
# releases/latest redirect is served by github.com itself and carries no such
# quota, so resolve the tag here and hand it to the installer via its VERSION
# env var -- which skips the API call entirely. Empty output = unresolved, the
# caller then runs the installer unpinned and falls back to npm.
opencode_latest_version() {
  curl -sI --max-time 20 -o /dev/null -w '%{redirect_url}' \
    https://github.com/anomalyco/opencode/releases/latest 2>/dev/null |
    sed -n 's#.*/releases/tag/v\([0-9][^/]*\)$#\1#p'
}

opencode_official_installer() {
  local version="${1:-}"
  if [[ -n "${version}" ]]; then
    curl -fsSL https://opencode.ai/install | VERSION="${version}" bash
  else
    curl -fsSL https://opencode.ai/install | bash
  fi
}

# Fallback path when GitHub is unreachable (proxy, blocklist, rate limit): the
# opencode-ai npm package ships the same release, and the npm registry is
# usually reachable/mirrored where raw GitHub downloads are not -- it is already
# how codex and t3 get installed here. Node may not be provisioned yet (this
# script runs before install-sdks.sh), so bootstrap it like install_codex does.
opencode_npm_fallback() {
  local version="${1:-}" spec="opencode-ai@latest"
  if [[ -n "${version}" ]]; then spec="opencode-ai@${version}"; fi
  if ! command -v npm >/dev/null 2>&1; then
    step "Installing Node.js 22.x (required for the opencode npm fallback)"
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - || return 1
    DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs || return 1
  fi
  # The package's postinstall unpacks the platform binary; newer npm gates
  # install scripts behind --allow-scripts, older npm ignores the unknown flag.
  npm install -g "${spec}" --allow-scripts=opencode-ai
}

# One line per endpoint the install needs, so a failed install says WHY instead
# of leaving the operator to guess between "offline", "proxy" and "rate limit".
opencode_reachability_note() {
  local url code
  for url in https://github.com https://api.github.com/rate_limit https://registry.npmjs.org; do
    code="$(curl -s --max-time 15 -o /dev/null -w '%{http_code}' "${url}" 2>/dev/null || echo "000")"
    note "  ${url} -> HTTP ${code}$([[ "${code}" == "000" ]] && printf ' (unreachable: no route, DNS or proxy block)')"
  done
}

install_opencode() {
  step "Installing opencode CLI"
  local had_opencode=false opencode_version
  if command -v opencode >/dev/null 2>&1; then had_opencode=true; fi

  opencode_version="$(opencode_latest_version || true)"
  if [[ -n "${opencode_version}" ]]; then
    note "latest opencode release: ${opencode_version} (pinned, so the installer skips the rate-limited GitHub API)"
  else
    warn "could not resolve the latest opencode release from github.com; letting the installer decide"
  fi

  # Always run the official installer: on a fresh VM it installs opencode, and on
  # a re-provision it updates an existing install to the latest version. When
  # opencode is already present a failed update (e.g. no network) is non-fatal --
  # we keep the working copy rather than aborting provisioning.
  if [[ "${had_opencode}" == true ]]; then
    note "opencode already installed; updating to the latest version"
  fi
  if ! run_installer_with_retries "opencode" opencode_official_installer "${opencode_version}"; then
    warn "official opencode installer failed; falling back to npm (opencode-ai)"
    if ! opencode_npm_fallback "${opencode_version}"; then
      opencode_reachability_note
      if [[ "${had_opencode}" == true ]]; then
        warn "opencode update failed (installer and npm); keeping the existing version"
      else
        err "opencode install failed: neither the official installer nor npm could fetch it"
        return 1
      fi
    fi
  fi

  # Resolve the binary /usr/local/bin/opencode should point at. command -v may
  # report our own symlink from a previous provision, or an npm global shim
  # (with Ubuntu's apt/nodesource npm the global prefix is /usr/local, so the
  # shim itself IS /usr/local/bin/opencode -- a symlink into node_modules).
  # Resolving THROUGH the link chain handles both; what must never happen is
  # symlinking /usr/local/bin/opencode to itself, which made opencode-serve fail
  # with 203/EXEC.
  opencode_bin=""
  opencode_path="$(command -v opencode || true)"
  if [[ -n "${opencode_path}" ]]; then
    opencode_resolved="$(readlink -f "${opencode_path}" 2>/dev/null || true)"
    if [[ -n "${opencode_resolved}" && -x "${opencode_resolved}" ]]; then
      if [[ "${opencode_resolved}" != "/usr/local/bin/opencode" ]]; then
        opencode_bin="${opencode_resolved}"
      elif [[ ! -L /usr/local/bin/opencode ]]; then
        # A real binary already sits at the PATH location; nothing to link.
        ok "opencode is already installed at /usr/local/bin/opencode"
        opencode_bin=""
        opencode_path="/usr/local/bin/opencode"
      fi
    fi
  fi
  if [[ -z "${opencode_bin}" && "${opencode_path}" != "/usr/local/bin/opencode" ]]; then
    # The official installer drops the binary under $HOME (root when run via
    # sudo); the npm package installs it inside its global node_modules.
    for candidate in \
      /root/.opencode/bin/opencode \
      /root/.local/bin/opencode \
      "${HOME:-/root}/.opencode/bin/opencode" \
      "${HOME:-/root}/.local/bin/opencode" \
      /usr/local/lib/node_modules/opencode-ai/bin/opencode.exe \
      /usr/lib/node_modules/opencode-ai/bin/opencode.exe; do
      if [[ -x "${candidate}" ]]; then
        opencode_bin="${candidate}"
        break
      fi
    done
  fi
  if [[ -z "${opencode_bin}" && "${opencode_path}" != "/usr/local/bin/opencode" ]]; then
    # Last resort: search common install roots for either binary name.
    opencode_bin="$(find /root /home /usr/local/lib -maxdepth 5 -type f \
      \( -name opencode -o -name opencode.exe \) -perm -u+x 2>/dev/null | head -n1 || true)"
  fi
  if [[ -n "${opencode_bin}" ]]; then
    # Resolve through any intermediate symlinks so the link target is the real
    # binary, and never point the symlink at itself.
    opencode_bin="$(readlink -f "${opencode_bin}" 2>/dev/null || echo "${opencode_bin}")"
    if [[ "${opencode_bin}" == "/usr/local/bin/opencode" || ! -x "${opencode_bin}" ]]; then
      warn "refusing to create opencode symlink: resolved path is invalid (${opencode_bin})"
      return 1
    fi
    ln -sf "${opencode_bin}" /usr/local/bin/opencode
  elif [[ ! -x /usr/local/bin/opencode ]]; then
    warn "opencode install completed, but binary was not found in PATH or common locations"
    return 1
  fi

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
# non-interactive. Also sets the `attribution` object to empty strings so Claude
# Code adds no AI attribution to commits or PRs (no "Co-Authored-By: Claude"
# trailer, no "Generated with Claude Code" footer). Empty strings are preserved
# by Claude's `attribution.commit ?? default` lookup, so they fully suppress the
# defaults; `attribution` is the current key (`includeCoAuthoredBy` is
# deprecated). Creates the file if missing. Relies on jq (installed by
# bootstrap.sh).
configure_claude_sandbox_setting() {
  local home_dir="$1"
  local owner="$2"
  local settings_dir="${home_dir}/.claude"
  local settings_file="${settings_dir}/settings.json"

  step "Setting IS_SANDBOX=1, bypassPermissions mode, and empty AI attribution in ${settings_file}"
  install -d -m 0755 "${settings_dir}"
  if [[ ! -s "${settings_file}" ]]; then
    echo '{}' >"${settings_file}"
  fi

  local tmp
  tmp="$(mktemp)"
  if jq '.env.IS_SANDBOX = "1" | .permissions.defaultMode = "bypassPermissions" | .skipDangerousModePermissionPrompt = true | .attribution.commit = "" | .attribution.pr = ""' \
    "${settings_file}" >"${tmp}" 2>/dev/null; then
    cat "${tmp}" >"${settings_file}"
  else
    warn "WARNING: ${settings_file} was not valid JSON; writing minimal settings"
    printf '{\n  "env": {\n    "IS_SANDBOX": "1"\n  },\n  "permissions": {\n    "defaultMode": "bypassPermissions"\n  },\n  "skipDangerousModePermissionPrompt": true,\n  "attribution": {\n    "commit": "",\n    "pr": ""\n  }\n}\n' >"${settings_file}"
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
  local settings_dir="$1"
  local owner="$2"
  # Tree to chown -R after writing (defaults to the settings dir). For the
  # Remote-SSH case pass the whole ~/.vscode-server so a freshly-created tree ends
  # up owned by the connecting user.
  local chown_root="${3:-${settings_dir}}"
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

  chown -R "${owner}:${owner}" "${chown_root}" 2>/dev/null || true
}

install_claude_code() {
  step "Installing Claude Code CLI"
  if command -v claude >/dev/null 2>&1; then
    note "Claude Code already installed; self-updating"
    # Best-effort: a failed self-update keeps the working installed version --
    # never worth degrading a provision over.
    claude update || warn "claude self-update failed; keeping the installed version"
  elif [[ "${TARGET_USER}" != "root" ]] && id "${TARGET_USER}" >/dev/null 2>&1; then
    sudo -H -u "${TARGET_USER}" bash -lc 'curl -fsSL https://claude.ai/install.sh | bash'
    if [[ -x "/home/${TARGET_USER}/.local/bin/claude" ]]; then
      ln -sf "/home/${TARGET_USER}/.local/bin/claude" /usr/local/bin/claude
    fi
  else
    # The official installer refuses to run as uid 0 when SUDO_USER names a
    # non-root user (it assumes a workstation where sudo would misplace the
    # install into root's home). Provisioning may be launched via sudo from a
    # non-root SSH login, but on this VM root's home IS the intended install
    # target (provision.sh forces TARGET_USER=root), so opt in explicitly.
    curl -fsSL https://claude.ai/install.sh | CLAUDE_INSTALL_ALLOW_SUDO=1 bash
    if [[ -x /root/.local/bin/claude ]]; then
      ln -sf /root/.local/bin/claude /usr/local/bin/claude
    fi
  fi

  # Voice dictation -- the CLI `/voice` command and the VS Code chat mic button --
  # records audio through SoX's `rec` (or ALSA `arecord`). Install SoX so the
  # recording backend is present on the VM; the Construct control-panel VS Code
  # extension streams the host's microphone into it over SSH when microphone
  # passthrough is enabled. Idle and harmless when voice input isn't used.
  if ! dpkg -s sox >/dev/null 2>&1; then
    step "Installing SoX (voice dictation audio backend)"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y sox
  else
    note "SoX already installed"
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
  # Remote-SSH server (machine scope): applies when VS Code connects via Remote-SSH.
  configure_claude_vscode_setting "${claude_home}/.vscode-server/data/Machine" "${claude_owner}" "${claude_home}/.vscode-server"
  # Local `code serve-web` server on the VM (runs as root): seed the SAME
  # skip-permission defaults into its machine-scope settings so the browser IDE
  # behaves identically, not just Remote-SSH. (User-scope settings in the web
  # client live in the browser, so machine scope is the reliable on-disk seed.)
  if [[ "${VSCODE_SERVE_WEB}" == "true" ]]; then
    configure_claude_vscode_setting "${VSCODE_SERVE_WEB_DATA_DIR}/data/Machine" "root"
  fi
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
# host configuration. Also disables AI attribution: an empty commit_attribution
# suppresses the "Co-authored-by: Codex <noreply@openai.com>" commit trailer
# (the dedicated, forward-compatible key whether or not the codex_git_commit
# feature is active). Any other existing keys in the file are preserved.
configure_codex_settings() {
  local home_dir="$1"
  local owner="$2"
  local config_dir="${home_dir}/.codex"
  local config_file="${config_dir}/config.toml"

  step "Seeding Codex permission, attribution, and trusted-project settings in ${config_file}"
  install -d -m 0700 "${config_dir}"
  [[ -f "${config_file}" ]] || : >"${config_file}"

  # Top-level permission skips (root table -- must precede any [table] header).
  set_toml_top_key "${config_file}" "default_permissions" '":danger-full-access"'
  set_toml_top_key "${config_file}" "sandbox_mode"        '"danger-full-access"'
  set_toml_top_key "${config_file}" "approval_policy"     '"never"'
  # Empty string disables the AI commit co-author trailer.
  set_toml_top_key "${config_file}" "commit_attribution"  '""'

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
    if ! { curl -fsSL https://chatgpt.com/codex/install.sh -o "${codex_installer}" \
        && printf 'n\n' | CI=1 sh "${codex_installer}"; }; then
      # The official installer parses GitHub's release JSON with a line-based awk
      # script that misses every asset now that api.github.com serves minified
      # single-line responses ("Could not find Codex package or platform npm
      # release assets"). The @openai/codex npm package ships the same native
      # binary, so fall back to it. This script runs before install-sdks.sh in
      # provision.sh, so Node may not be provisioned yet.
      warn "Official Codex installer failed; falling back to npm (@openai/codex)"
      if ! command -v npm >/dev/null 2>&1; then
        step "Installing Node.js 22.x (required for the Codex npm fallback)"
        curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
        DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs
      fi
      npm install -g @openai/codex
    fi
    rm -f "${codex_installer}"
  else
    # Already installed: update in place, matching HOW it is installed. An npm
    # global install (the shim resolves into node_modules -- how the fallback
    # above installs it) updates via npm; the official installer would fight
    # that layout. Official-installer layouts re-run the installer, with the
    # same npm fallback. Best-effort either way: a failed update keeps the
    # working installed version rather than degrading the provision.
    codex_current="$(readlink -f "$(command -v codex)" 2>/dev/null || true)"
    case "${codex_current}" in
      */node_modules/*)
        note "Codex already installed (npm); updating via npm"
        npm install -g @openai/codex@latest || warn "codex npm update failed; keeping the installed version"
        ;;
      *)
        note "Codex already installed; updating via the official installer"
        codex_installer="$(mktemp)"
        if ! { curl -fsSL https://chatgpt.com/codex/install.sh -o "${codex_installer}" \
            && printf 'n\n' | CI=1 sh "${codex_installer}"; }; then
          if command -v npm >/dev/null 2>&1 && npm install -g @openai/codex@latest; then
            warn "Official Codex installer failed; updated via npm instead"
          else
            warn "codex update failed; keeping the installed version"
          fi
        fi
        rm -f "${codex_installer}"
        ;;
    esac
  fi

  # Resolve the binary /usr/local/bin/codex should point at, then link it there
  # as the stable PATH location (the codex-app-server unit execs it). command -v
  # may report: our own symlink from a previous provision, an npm global shim
  # (with Ubuntu's apt npm the global prefix is /usr/local, so the shim itself
  # IS /usr/local/bin/codex -- a symlink into .../node_modules/@openai/codex/),
  # or the official installer's path. Resolving THROUGH the link chain handles
  # all of them; only a circular/broken chain (the historical self-symlink,
  # which made codex --version fail with ELOOP and codex-app-server 203/EXEC)
  # is discarded and repaired from the search locations below.
  codex_target=""
  codex_bin="$(command -v codex || true)"
  if [[ -n "${codex_bin}" ]]; then
    resolved="$(readlink -f "${codex_bin}" 2>/dev/null || true)"
    if [[ -n "${resolved}" && -x "${resolved}" ]]; then
      if [[ "${resolved}" != "/usr/local/bin/codex" ]]; then
        codex_target="${resolved}"
      elif [[ ! -L /usr/local/bin/codex ]]; then
        # A real binary already sits at the PATH location; nothing to link.
        codex_target="/usr/local/bin/codex"
      fi
    fi
  fi
  if [[ -z "${codex_target}" && -x /root/.local/bin/codex ]]; then
    codex_target=/root/.local/bin/codex
  fi
  if [[ -z "${codex_target}" && -x /root/.codex/bin/codex ]]; then
    codex_target=/root/.codex/bin/codex
  fi
  if [[ -z "${codex_target}" ]]; then
    # Last resort: search the install roots, including npm's global trees --
    # the npm package nests its launcher/binary deeper than a shallow find
    # reaches, so allow depth for .../node_modules/@openai/codex/bin/codex.js.
    codex_target="$(find /root /home /usr/local /usr/lib/node_modules -maxdepth 7 -type f \( -name codex -o -name codex.js \) -perm -u+x 2>/dev/null | head -n1 || true)"
  fi
  if [[ -z "${codex_target}" ]]; then
    err "Codex install completed, but no codex binary was found (checked PATH, /root/.local/bin, /root/.codex/bin, /usr/local, /usr/lib/node_modules)"
    return 1
  fi
  # Never pin a VERSIONED release dir of the official standalone installer
  # (.../.codex/.../releases/<version>/bin/codex): updates only move the
  # installer's `current` symlink, so a fully-resolved link would keep
  # executing -- and the panel probe reporting -- the OLD version forever
  # (observed: /usr/local/bin/codex stuck on 0.144.0 while `current` was
  # 0.144.6). Link the stable entry instead; symlink chains resolve at exec.
  case "${codex_target}" in
    */.codex/*releases/*)
      for codex_stable in /root/.local/bin/codex /root/.codex/bin/codex; do
        if [[ -x "${codex_stable}" && "$(readlink -f "${codex_stable}" 2>/dev/null)" != "/usr/local/bin/codex" ]]; then
          codex_target="${codex_stable}"
          break
        fi
      done
      ;;
  esac
  if [[ ! -x "${codex_target}" ]]; then
    err "refusing to create codex symlink: resolved path is invalid (${codex_target})"
    return 1
  fi
  if [[ "${codex_target}" != "/usr/local/bin/codex" ]]; then
    ln -sf "${codex_target}" /usr/local/bin/codex
  fi

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

# Install the T3 Code web GUI (the `t3` npm package -- pingdotgg's browser
# control plane for coding agents) and autostart it as the t3code-serve service.
# Opt-in: selected via the T3CODE flag in config.env / the control panel's
# settings toggle, NOT via the AI_TOOLS list (provision.sh invokes this with
# AI_TOOLS_OVERRIDE=t3code when T3CODE=true). The server drives the agents
# through their locally-authenticated CLIs, so it needs no credentials of its
# own; browser access is gated by one-time pairing tokens (t3 auth pairing
# create). Its state (threads, auth sessions, settings) lives under ~/.t3.
# Whether the system Node satisfies t3's engines requirement
# (^22.16 || ^23.11 || >=24.10). npm only WARNS on a mismatch, so without this
# check an old Node yields an installed-but-broken t3 whose service restart-loops.
t3_node_ok() {
  local v major minor rest
  v="$(node -v 2>/dev/null | sed 's/^v//')" || return 1
  [[ -n "${v}" ]] || return 1
  major="${v%%.*}"; rest="${v#*.}"; minor="${rest%%.*}"
  [[ "${major}" -ge 25 ]] && return 0
  case "${major}" in
    24) [[ "${minor:-0}" -ge 10 ]] ;;
    23) [[ "${minor:-0}" -ge 11 ]] ;;
    22) [[ "${minor:-0}" -ge 16 ]] ;;
    *) return 1 ;;
  esac
}

install_t3code() {
  step "Installing T3 Code (t3 CLI + web GUI server)"

  # t3 ships only as an npm package. Node may not be provisioned yet (this runs
  # before install-sdks.sh), or a project SDK may have pinned an older major --
  # bootstrap/upgrade to Node 22 via NodeSource (the same channel
  # install-sdks.sh uses) when npm is missing or Node is below t3's floor.
  if ! command -v npm >/dev/null 2>&1 || ! t3_node_ok; then
    step "Installing Node.js 22.x (t3 requires Node ^22.16 || ^23.11 || >=24.10)"
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
    DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs
  fi

  # node-pty (t3's terminal backend) ships prebuilt binaries only for macOS and
  # Windows -- on Linux its install always falls back to 'node-gyp rebuild',
  # which needs make/g++/python3. A fresh VM has no compiler toolchain, so
  # provision it before npm runs the build scripts.
  if ! command -v make >/dev/null 2>&1 || ! command -v g++ >/dev/null 2>&1 || ! command -v python3 >/dev/null 2>&1; then
    step "Installing build tools (node-pty compiles from source on Linux)"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y build-essential python3
  fi

  # node-pty (terminal support) and msgpackr-extract must run their build
  # scripts; newer npm gates install scripts behind --allow-scripts, older npm
  # ignores the unknown flag and runs them anyway -- one call covers both.
  local _tag
  _tag="$(_t3_npm_tag)"
  if command -v t3 >/dev/null 2>&1; then
    note "t3 already installed; installing t3@${_tag} (channel=${T3CODE_CHANNEL})"
    if ! npm install -g "t3@${_tag}" --allow-scripts=node-pty,msgpackr-extract; then
      warn "t3 update failed; keeping the existing version"
    fi
  else
    npm install -g "t3@${_tag}" --allow-scripts=node-pty,msgpackr-extract
  fi

  # Resolve the installed binary and pin the stable PATH location the service
  # unit execs. With apt/nodesource npm the global shim already IS
  # /usr/local/bin/t3; other prefixes get a symlink (never to itself).
  t3_bin="$(command -v t3 || true)"
  if [[ -z "${t3_bin}" ]]; then
    err "t3 install completed, but no t3 binary was found on PATH"
    return 1
  fi
  if [[ "${t3_bin}" != "/usr/local/bin/t3" ]]; then
    resolved="$(readlink -f "${t3_bin}" 2>/dev/null || echo "${t3_bin}")"
    if [[ "${resolved}" == "/usr/local/bin/t3" || ! -x "${resolved}" ]]; then
      err "refusing to create t3 symlink: resolved path is invalid (${resolved})"
      return 1
    fi
    ln -sf "${resolved}" /usr/local/bin/t3
  fi

  # Persist the bind settings so the unit's EnvironmentFile always defines them
  # (an older config.env predating T3 Code has no T3CODE_* keys, and systemd
  # would otherwise exec `t3 serve --host --port` with empty expansions).
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE_HOST "${T3CODE_HOST}"
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE_PORT "${T3CODE_PORT}"
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE_CHANNEL "${T3CODE_CHANNEL}"

  # Opt-in T3 extra features: patch (or un-patch) the freshly-installed dist
  # bundle BEFORE the service (re)start below. The legacy env key remains for
  # settings compatibility, but now controls the whole patch set: Claude
  # usage-limit auto-resume and OpenCode background-watcher monitoring. Both
  # patchers verify their anchors; an unknown upstream bundle stays usable.
  if [[ "${T3CODE_LIMIT_RESUME:-false}" == "true" ]]; then
    step "Applying T3 Code extra-feature patches"
    node "${REPO_DIR}/extension/vm/construct-t3park-patch.mjs" apply \
      || warn "WARNING: usage-limit auto-resume patch not applied (see above); t3 runs stock"
    node "${REPO_DIR}/extension/vm/construct-t3-opencode-monitor-patch.mjs" apply \
      || warn "WARNING: OpenCode background-monitoring patch not applied (see above); t3 continues without it"
  else
    node "${REPO_DIR}/extension/vm/construct-t3park-patch.mjs" revert >/dev/null 2>&1 || true
    node "${REPO_DIR}/extension/vm/construct-t3-opencode-monitor-patch.mjs" revert >/dev/null 2>&1 || true
  fi

  install -d -m 0755 "${WORKSPACE_ROOT}"
  install -m 0644 "${REPO_DIR}/systemd/t3code-serve.service" /etc/systemd/system/t3code-serve.service
  sed -i "s|^WorkingDirectory=.*|WorkingDirectory=${WORKSPACE_ROOT}|" /etc/systemd/system/t3code-serve.service
  systemctl daemon-reload
  systemctl enable t3code-serve
  systemctl restart t3code-serve

  # Bootstrap one t3 project per git repo in the workspace so the web UI starts
  # useful. (t3 serve's --auto-bootstrap-project-from-cwd flag is DEAD in the
  # headless serve path -- the handler hardcodes it off -- hence explicit adds.)
  # Idempotent: an already-registered path fails with ProjectAlreadyExistsError,
  # which is swallowed; no duplicates are created.
  local _t3_repo
  for _t3_repo in "${WORKSPACE_ROOT}"/*/; do
    [[ -d "${_t3_repo}.git" ]] || continue
    t3 project add "${_t3_repo%/}" --log-level none >/dev/null 2>&1 || true
  done

  if systemctl is-active --quiet t3code-serve; then
    echo "t3code-serve is running on ${T3CODE_HOST}:${T3CODE_PORT}"
    # Fresh VMs have no t3 DB when the patch step above runs, so the token mint
    # there can fail; retry now that the server has started once.
    if [[ "${T3CODE_LIMIT_RESUME:-false}" == "true" && ! -s /etc/construct/t3park-token ]]; then
      node "${REPO_DIR}/extension/vm/construct-t3park-patch.mjs" mint-token \
        || warn "WARNING: could not mint the auto-resume API token; parked threads can't restart until one exists"
    fi
  else
    warn "WARNING: t3code-serve failed to start; recent status and logs:"
    systemctl --no-pager --full status t3code-serve >&2 || true
    journalctl -u t3code-serve --no-pager -n 30 >&2 || true
  fi
}

# Sourced for the helpers only (unit tests): stop before anything installs.
if [[ "${_FUNCS_ONLY}" == "true" ]]; then
  return 0 2>/dev/null || exit 0
fi

failed=0
run_tool() {
  local title="$1" fn="$2" rc
  # The parent temporarily disables errexit only around the subshell status
  # collection. The subshell re-enables it so a bare failure inside one installer
  # stops that tool without stopping the later independent tools.
  set +e
  ( set -e; "${fn}" )
  rc=$?
  set -e
  if [[ "${rc}" -ne 0 ]]; then
    warn "${title} failed (exit ${rc}); continuing with the remaining AI tools"
    failed=$((failed + 1))
  fi
}

if has_tool opencode; then
  run_tool "opencode installation" install_opencode
fi

if has_tool claude-code; then
  run_tool "Claude Code installation" install_claude_code
fi

if has_tool codex; then
  run_tool "Codex installation" install_codex
fi

if has_tool t3code; then
  run_tool "T3 Code installation" install_t3code
fi

if has_tool pi; then
  warn "pi selected, but no installer is implemented yet. Selection is recorded in ${CONFIG_FILE}."
fi

configure_console_info() {
  install -m 0755 "${REPO_DIR}/bin/print-connection-info.sh" /usr/local/bin/construct-print-connection-info
  install -m 0644 "${REPO_DIR}/systemd/construct-console-info.service" /etc/systemd/system/construct-console-info.service
  systemctl daemon-reload
  systemctl enable construct-console-info
  "${REPO_DIR}/bin/update-login-banner.sh"
  "${REPO_DIR}/bin/print-connection-info.sh"
}
if [[ "${AI_CONSOLE_INTEGRATION}" == "true" ]]; then
  run_tool "AI tool console integration" configure_console_info
fi

if [[ "${failed}" -gt 0 ]]; then
  err "${failed} AI tool area(s) failed"
  exit 1
fi
