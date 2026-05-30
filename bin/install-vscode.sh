#!/usr/bin/env bash
set -euo pipefail

# Install the standalone VS Code CLI ("VS Code Server") and set up its serving
# features:
#
#   * The CLI is installed unconditionally (VSCODE_SERVER=true, the default) so
#     VS Code Remote-SSH, `code serve-web`, and `code tunnel` all have the binary.
#   * `code serve-web` (browser-based VS Code over plain HTTP, gated by a
#     connection token -- no account sign-in) autostarts by default
#     (VSCODE_SERVE_WEB=true), bound to 0.0.0.0:${VSCODE_SERVE_WEB_PORT}.
#   * `code tunnel` (relayed via vscode.dev, no inbound port, needs a one-time
#     device sign-in) is opt-in (VSCODE_TUNNEL=true). The service is also
#     redeployed whenever it was previously deployed/registered, so a registered
#     VM keeps autostarting the tunnel even without the flag; the interactive
#     sign-in is only re-run when selected AND not already registered.
#
# A machine-readable status file (${VSCODE_STATUS_FILE}) is written for the host
# provisioner (Provision-AgentVM.ps1) to read back over SSH.

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
VSCODE_TUNNEL_DATA_DIR="${VSCODE_TUNNEL_DATA_DIR:-/var/lib/vscode-tunnel}"
VSCODE_SERVE_WEB_DATA_DIR="${VSCODE_SERVE_WEB_DATA_DIR:-/var/lib/vscode-serve-web}"
TUNNEL_UNIT_FILE="/etc/systemd/system/code-tunnel.service"
SERVE_WEB_UNIT_FILE="/etc/systemd/system/code-serve-web.service"
# Machine-readable status the host provisioner reads back over SSH.
VSCODE_STATUS_FILE="${VSCODE_STATUS_FILE:-/etc/construct/vscode-status}"

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo ${REPO_DIR}/bin/install-vscode.sh"
  exit 1
fi

# Read persisted config without letting a malformed config abort the run.
if [[ -f "${CONFIG_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  . "${CONFIG_FILE}" 2>/dev/null || true
  set +a
fi

VSCODE_SERVER="${VSCODE_SERVER:-true}"
VSCODE_TUNNEL="${VSCODE_TUNNEL:-false}"
VSCODE_TUNNEL_NAME="${VSCODE_TUNNEL_NAME:-}"
VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB:-true}"
VSCODE_SERVE_WEB_HOST="${VSCODE_SERVE_WEB_HOST:-0.0.0.0}"
VSCODE_SERVE_WEB_PORT="${VSCODE_SERVE_WEB_PORT:-8000}"
VSCODE_SERVE_WEB_TOKEN_FILE="${VSCODE_SERVE_WEB_TOKEN_FILE:-/etc/construct/vscode-serve-web.token}"

# DNS name this VM is reachable under from the user's machine (Hyper-V publishes
# "<hostname>.mshome.net"), matching print-connection-info.sh.
AGENT_DNS="$(hostname).mshome.net"

# --- status accumulated across the run, written once at the end --------------
TUNNEL_DEPLOYED="no"; TUNNEL_AUTHED="no"; TUNNEL_NEEDS_SIGNIN="no"
TUNNEL_NAME=""; TUNNEL_URL=""; TUNNEL_LOGIN=""
SERVE_WEB_ENABLED="no"; SERVE_WEB_URL=""; SERVE_WEB_TOKEN=""

write_status() {
  {
    printf 'VSCODE_TUNNEL_DEPLOYED=%s\n'     "${TUNNEL_DEPLOYED}"
    printf 'VSCODE_TUNNEL_AUTHED=%s\n'       "${TUNNEL_AUTHED}"
    printf 'VSCODE_TUNNEL_NEEDS_SIGNIN=%s\n' "${TUNNEL_NEEDS_SIGNIN}"
    printf 'VSCODE_TUNNEL_NAME=%s\n'         "${TUNNEL_NAME}"
    printf 'VSCODE_TUNNEL_URL=%s\n'          "${TUNNEL_URL}"
    printf 'VSCODE_TUNNEL_LOGIN_B64=%s\n'    "$(printf '%s' "${TUNNEL_LOGIN}" | base64 | tr -d '\n')"
    printf 'VSCODE_SERVE_WEB_ENABLED=%s\n'   "${SERVE_WEB_ENABLED}"
    printf 'VSCODE_SERVE_WEB_URL=%s\n'       "${SERVE_WEB_URL}"
    printf 'VSCODE_SERVE_WEB_TOKEN=%s\n'     "${SERVE_WEB_TOKEN}"
  } >"${VSCODE_STATUS_FILE}" 2>/dev/null || true
  chmod 0644 "${VSCODE_STATUS_FILE}" 2>/dev/null || true
}

# Coerce an arbitrary string into a valid tunnel name: lowercase, only [a-z0-9-],
# collapsed/trimmed hyphens, padded to >= 4 chars and capped at 20 (VS Code's
# tunnel-name rules). Used to derive a default name from the hostname.
sanitize_tunnel_name() {
  local n
  n="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9-' '-')"
  n="$(printf '%s' "${n}" | sed -E 's/-+/-/g; s/^-+//; s/-+$//')"
  while [[ "${#n}" -lt 4 ]]; do n="${n}0"; done
  n="${n:0:20}"
  printf '%s' "${n}" | sed -E 's/-+$//'
}

# Download the standalone VS Code CLI to /usr/local/bin/code if not already
# present. The archive from code.visualstudio.com holds a single `code` binary.
install_vscode_cli() {
  if [[ -x /usr/local/bin/code ]]; then
    note "VS Code CLI already installed at /usr/local/bin/code ($(/usr/local/bin/code --version 2>/dev/null | head -n1 || echo present))"
    return 0
  fi

  local arch tmp got
  case "$(uname -m)" in
    x86_64|amd64)  arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    armv7l)        arch="armhf" ;;
    *) arch="x64"; warn "unknown architecture $(uname -m); defaulting to x64" ;;
  esac

  step "Downloading VS Code CLI (cli-linux-${arch})"
  tmp="$(mktemp -d)"
  # Use update.code.visualstudio.com -- the older code.visualstudio.com/sha/download
  # endpoint 404s for the cli-linux-* identifiers. Try the glibc build first, then
  # fall back to the statically-linked alpine build (which also runs on glibc).
  got=""
  for variant in "linux-${arch}" "alpine-${arch}"; do
    if curl -fsSL "https://update.code.visualstudio.com/latest/cli-${variant}/stable" -o "${tmp}/vscode-cli.tar.gz"; then
      got="${variant}"; break
    fi
    note "  cli-${variant} download failed; trying next"
  done
  if [[ -z "${got}" ]]; then
    warn "failed to download the VS Code CLI from update.code.visualstudio.com; skipping"
    rm -rf "${tmp}"
    return 1
  fi
  tar -xzf "${tmp}/vscode-cli.tar.gz" -C "${tmp}"
  if [[ ! -x "${tmp}/code" ]]; then
    warn "VS Code CLI archive did not contain an executable 'code' binary; skipping"
    rm -rf "${tmp}"
    return 1
  fi
  install -m 0755 "${tmp}/code" /usr/local/bin/code
  rm -rf "${tmp}"
  ok "VS Code CLI installed: $(/usr/local/bin/code --version 2>/dev/null | head -n1 || echo code)"
}

# Best-effort "is this VM already registered?" check: an auth/registration file
# left in the CLI data dir by a previous successful sign-in, or the CLI reporting
# a signed-in tunnel user.
tunnel_is_registered() {
  if [[ -d "${VSCODE_TUNNEL_DATA_DIR}" ]] \
     && find "${VSCODE_TUNNEL_DATA_DIR}" -maxdepth 3 -type f \( -name 'token.json' -o -name 'code_tunnel.json' \) 2>/dev/null | grep -q .; then
    return 0
  fi
  if /usr/local/bin/code --cli-data-dir "${VSCODE_TUNNEL_DATA_DIR}" tunnel user show >/dev/null 2>&1; then
    return 0
  fi
  return 1
}

# Inspect the running tunnel's journal to classify its live state. Sets:
#   TUNNEL_STATE      = "live" (authenticated/serving) | "needs" (awaiting sign-in) | ""
#   TUNNEL_LOGIN_LINE = the device-login instruction line, when awaiting sign-in
TUNNEL_STATE=""
TUNNEL_LOGIN_LINE=""
detect_tunnel_state() {
  local i recent
  for i in $(seq 1 30); do
    # -o cat strips the journal timestamp/unit prefix so captured lines are just
    # the tunnel's own messages.
    recent="$(journalctl -u code-tunnel -o cat --no-pager -n 200 2>/dev/null || true)"
    if printf '%s\n' "${recent}" | grep -Eqi 'open this link|tunnel is available|connected to'; then
      TUNNEL_STATE="live"
      return 0
    fi
    TUNNEL_LOGIN_LINE="$(printf '%s\n' "${recent}" | grep -Ei 'github\.com/login/device|microsoft\.com/devicelogin|use code|grant access' | tail -n1 || true)"
    if [[ -n "${TUNNEL_LOGIN_LINE}" ]]; then
      TUNNEL_STATE="needs"
      TUNNEL_LOGIN_LINE="${TUNNEL_LOGIN_LINE# }"
      return 0
    fi
    sleep 1
  done
  return 0
}

# ----------------------------------------------------------------------------
# serve-web: browser VS Code over HTTP, gated by a connection token. On by
# default; bound to 0.0.0.0 so it's reachable at http://<dns>:<port> like the
# other "serve" services here.
# ----------------------------------------------------------------------------
setup_serve_web() {
  if [[ "${VSCODE_SERVE_WEB}" != "true" ]]; then
    note "code serve-web not enabled (VSCODE_SERVE_WEB=${VSCODE_SERVE_WEB})"
    return 0
  fi

  step "Deploying code serve-web service"
  # Generate a connection token once (no trailing newline -- it's the exact secret
  # the server expects and that goes in the ?tkn= URL).
  if [[ ! -s "${VSCODE_SERVE_WEB_TOKEN_FILE}" ]]; then
    install -d -m 0700 "$(dirname "${VSCODE_SERVE_WEB_TOKEN_FILE}")"
    od -An -N24 -tx1 /dev/urandom | tr -d ' \n' >"${VSCODE_SERVE_WEB_TOKEN_FILE}"
    chmod 0600 "${VSCODE_SERVE_WEB_TOKEN_FILE}"
  fi
  SERVE_WEB_TOKEN="$(tr -d ' \n' <"${VSCODE_SERVE_WEB_TOKEN_FILE}")"
  # serve-web only authenticates via a localhost origin, so the URL you actually
  # open is the localhost one (reach the port via your own forward/tunnel), not
  # the agent-vm address.
  SERVE_WEB_URL="http://localhost:${VSCODE_SERVE_WEB_PORT}"

  # Persist the resolved bind settings into config.env so the systemd unit's
  # EnvironmentFile actually carries the host/port/token-file it references.
  # bootstrap.sh only seeds these on first-time config.env creation; a VM with a
  # pre-existing or older config.env would otherwise leave ${VSCODE_SERVE_WEB_HOST}
  # / ${VSCODE_SERVE_WEB_PORT} empty in ExecStart, so serve-web never binds where
  # expected. Writing them here makes serve-web self-healing on every provision.
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" VSCODE_SERVE_WEB_HOST "${VSCODE_SERVE_WEB_HOST}"
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" VSCODE_SERVE_WEB_PORT "${VSCODE_SERVE_WEB_PORT}"
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" VSCODE_SERVE_WEB_TOKEN_FILE "${VSCODE_SERVE_WEB_TOKEN_FILE}"

  warn "WARNING: code serve-web exposes a root-level browser IDE on ${VSCODE_SERVE_WEB_HOST}:${VSCODE_SERVE_WEB_PORT}; it is protected by a connection token. Expose only on trusted VM networks."

  install -d -m 0700 "${VSCODE_SERVE_WEB_DATA_DIR}"
  install -m 0644 "${REPO_DIR}/systemd/code-serve-web.service" "${SERVE_WEB_UNIT_FILE}"
  systemctl daemon-reload
  systemctl enable code-serve-web
  systemctl restart code-serve-web
  SERVE_WEB_ENABLED="yes"

  printf '\n'
  ok "============================================================"
  ok "VS Code Server (serve-web)"
  printf '  Service:   code-serve-web\n'
  printf '  Bind:      %s:%s\n' "${VSCODE_SERVE_WEB_HOST}" "${VSCODE_SERVE_WEB_PORT}"
  printf '  Open:      %s/?tkn=%s\n' "${SERVE_WEB_URL}" "${SERVE_WEB_TOKEN}"
  printf '  Token in:  %s\n' "${VSCODE_SERVE_WEB_TOKEN_FILE}"
  if systemctl is-active --quiet code-serve-web; then
    ok "  Status: running"
  else
    warn "  WARNING: code-serve-web failed to start; recent logs:"
    journalctl -u code-serve-web --no-pager -n 20 >&2 || true
  fi
  ok "============================================================"
  printf '\n'
}

# ----------------------------------------------------------------------------
# tunnel: relayed remote access via vscode.dev, opt-in + one-time sign-in.
# ----------------------------------------------------------------------------
setup_tunnel() {
  TUNNEL_NAME="$(sanitize_tunnel_name "${VSCODE_TUNNEL_NAME:-$(hostname)}")"
  TUNNEL_URL="https://vscode.dev/tunnel/${TUNNEL_NAME}"

  local want="no"; [[ "${VSCODE_TUNNEL}" == "true" ]] && want="yes"
  local registered="no"; tunnel_is_registered && registered="yes"
  local before="no"
  if [[ -f "${TUNNEL_UNIT_FILE}" ]] || systemctl is-enabled --quiet code-tunnel 2>/dev/null; then
    before="yes"
  fi

  if [[ "${want}" != "yes" && "${registered}" != "yes" && "${before}" != "yes" ]]; then
    note "VS Code tunnel not selected and none previously deployed; tunnel service not set up."
    note "  Enable it later with:  VSCODE_TUNNEL=true sudo ${REPO_DIR}/bin/install-vscode.sh"
    return 0
  fi

  note "tunnel: selected=${want} already-registered=${registered} previously-deployed=${before}"
  bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" VSCODE_TUNNEL_NAME "${TUNNEL_NAME}"

  step "Deploying code-tunnel service (name: ${TUNNEL_NAME})"
  install -d -m 0700 "${VSCODE_TUNNEL_DATA_DIR}"
  install -m 0644 "${REPO_DIR}/systemd/code-tunnel.service" "${TUNNEL_UNIT_FILE}"
  systemctl daemon-reload
  systemctl enable code-tunnel
  # Restart so the tunnel runs now. When a sign-in is needed, this fresh start is
  # what kicks off the device-code flow we capture below.
  systemctl restart code-tunnel
  TUNNEL_DEPLOYED="yes"

  if systemctl is-active --quiet code-tunnel; then
    ok "code-tunnel service is enabled and running"
  else
    warn "WARNING: code-tunnel failed to start; recent status and logs:"
    systemctl --no-pager --full status code-tunnel >&2 || true
    journalctl -u code-tunnel --no-pager -n 30 >&2 || true
  fi

  step "Checking tunnel registration state"
  detect_tunnel_state

  printf '\n'
  ok "============================================================"
  ok "VS Code Remote Tunnel"
  printf '  Service:         code-tunnel (enabled)\n'
  printf '  Tunnel name:     %s\n' "${TUNNEL_NAME}"
  printf '  Open in browser: %s\n' "${TUNNEL_URL}"
  printf '  Or in VS Code:   Remote Explorer -> Tunnels -> %s\n' "${TUNNEL_NAME}"

  if [[ "${want}" == "yes" && "${TUNNEL_STATE}" == "needs" ]]; then
    # Fresh registration required. The host provisioner displays this and pauses
    # for sign-in before rebooting.
    TUNNEL_AUTHED="no"; TUNNEL_NEEDS_SIGNIN="yes"; TUNNEL_LOGIN="${TUNNEL_LOGIN_LINE}"
    printf '\n'
    warn "  ONE-TIME SIGN-IN required to register this tunnel:"
    if [[ -n "${TUNNEL_LOGIN_LINE}" ]]; then
      printf '    %s\n' "${TUNNEL_LOGIN_LINE}"
    else
      printf '    Run:  journalctl -u code-tunnel -n 50   (look for the github.com/login/device link)\n'
    fi
    printf '    The provisioner will pause for you to complete this before rebooting.\n'
  elif [[ "${TUNNEL_STATE}" == "live" ]]; then
    TUNNEL_AUTHED="yes"; TUNNEL_NEEDS_SIGNIN="no"
    ok "  Tunnel is registered and live."
  else
    TUNNEL_AUTHED="no"; TUNNEL_NEEDS_SIGNIN="no"; TUNNEL_LOGIN="${TUNNEL_LOGIN_LINE}"
    if [[ "${TUNNEL_STATE}" == "needs" ]]; then
      warn "  Tunnel appears to need (re-)registration. Re-run with VSCODE_TUNNEL=true to sign in:"
      warn "    VSCODE_TUNNEL=true sudo ${REPO_DIR}/bin/install-vscode.sh"
    else
      note "  Assuming existing registration is still valid (re-run with VSCODE_TUNNEL=true to re-register)."
    fi
  fi
  printf '  Logs: journalctl -u code-tunnel -f\n'
  ok "============================================================"
  printf '\n'
}

# ============================================================================
# Main
# ============================================================================
if [[ "${VSCODE_SERVER}" != "true" ]]; then
  note "VSCODE_SERVER=false; skipping VS Code CLI install"
  write_status
  exit 0
fi

step "Installing VS Code CLI (server)"
if ! install_vscode_cli; then
  # Make this visible: provision.sh surfaces a non-zero exit as a warning. Without
  # the CLI, neither serve-web nor the tunnel can be set up.
  err "VS Code CLI install failed; serve-web and tunnel cannot be set up."
  write_status
  exit 1
fi
note "Remote-SSH will use this binary; 'code serve-web' / 'code tunnel' are available."

setup_serve_web
setup_tunnel

# Refresh the login banner so SSH/console logins reflect the current state.
"${REPO_DIR}/bin/update-login-banner.sh" 2>/dev/null || true

write_status
