#!/usr/bin/env bash
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"

if [[ -f "${CONFIG_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  # Tolerate a malformed config (e.g. a legacy unquoted value): this runs from
  # the login banner and the bootstrap step, and a bad line must not abort either.
  . "${CONFIG_FILE}" 2>/dev/null || true
  set +a
fi

AI_TOOLS="${AI_TOOLS:-}"
OPENCODE_PORT="${OPENCODE_PORT:-4096}"
OPENCODE_HOST="${OPENCODE_HOST:-0.0.0.0}"
CODEX_PORT="${CODEX_PORT:-4500}"
CODEX_HOST="${CODEX_HOST:-0.0.0.0}"
CODEX_TOKEN_FILE="${CODEX_TOKEN_FILE:-/etc/construct/codex-app-server.token}"
T3CODE="${T3CODE:-}"
T3CODE_HOST="${T3CODE_HOST:-0.0.0.0}"
T3CODE_PORT="${T3CODE_PORT:-5177}"
VSCODE_TUNNEL="${VSCODE_TUNNEL:-}"
VSCODE_TUNNEL_NAME="${VSCODE_TUNNEL_NAME:-}"
VSCODE_SERVE_WEB="${VSCODE_SERVE_WEB:-}"
VSCODE_SERVE_WEB_HOST="${VSCODE_SERVE_WEB_HOST:-0.0.0.0}"
VSCODE_SERVE_WEB_PORT="${VSCODE_SERVE_WEB_PORT:-8000}"
VSCODE_SERVE_WEB_TOKEN_FILE="${VSCODE_SERVE_WEB_TOKEN_FILE:-/etc/construct/vscode-serve-web.token}"
SMB_SHARE="${SMB_SHARE:-}"
SMB_USER="${SMB_USER:-dev}"
SMB_SHARE_NAME="${SMB_SHARE_NAME:-repo}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

lan_ip="$(ip -o -4 route get 1.1.1.1 2>/dev/null | awk '{for (i=1; i<=NF; i++) if ($i == "src") {print $(i+1); exit}}' || true)"
if [[ -z "${lan_ip}" ]]; then
  lan_ip="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
fi
lan_ip="${lan_ip:-unknown}"
hyperv_dns="$(hostname).mshome.net"
ssh_user="${SSH_USER:-${SUDO_USER:-${USER:-root}}}"

has_selected_or_installed_tool() {
  tool="$1"
  command_name="$2"
  service_name="${3:-}"

  case ",${AI_TOOLS}," in
    *,"${tool}",*) return 0 ;;
  esac

  if command -v "${command_name}" >/dev/null 2>&1; then
    return 0
  fi

  if [[ -n "${service_name}" ]] && systemctl is-active --quiet "${service_name}" 2>/dev/null; then
    return 0
  fi

  return 1
}

cat <<EOF

============================================================
The Construct coding sandbox
Hostname: $(hostname)
DNS:      ${hyperv_dns}
LAN IP:   ${lan_ip} fallback

SSH:
  ssh ${ssh_user}@${hyperv_dns}
EOF

if has_selected_or_installed_tool claude-code claude; then
    cat <<EOF

Claude Code:
  Connect over SSH, then run: claude
EOF
fi

if has_selected_or_installed_tool opencode opencode opencode-serve; then
    cat <<EOF

OpenCode:
  Service:  opencode-serve
  Bind:     ${OPENCODE_HOST}:${OPENCODE_PORT}
  URL:      http://${hyperv_dns}:${OPENCODE_PORT}
  Docs:     http://${hyperv_dns}:${OPENCODE_PORT}/doc
EOF
fi

if has_selected_or_installed_tool codex codex codex-app-server; then
    cat <<EOF

Codex:
  Supported app workflow: add this VM as an SSH host in Codex App.
  SSH target:             ${ssh_user}@${hyperv_dns}
  Remote CLI check:       ssh ${ssh_user}@${hyperv_dns} codex --version

Codex experimental app-server:
  Service:                codex-app-server
  Bind:                   ${CODEX_HOST}:${CODEX_PORT}
  Health:                 http://${hyperv_dns}:${CODEX_PORT}/healthz
  Direct remote URL:      ws://${hyperv_dns}:${CODEX_PORT}
  Token file on VM:       ${CODEX_TOKEN_FILE}
  Connect CLI:            CODEX_REMOTE_TOKEN=<token> codex --remote ws://${hyperv_dns}:${CODEX_PORT} --remote-auth-token-env CODEX_REMOTE_TOKEN
  Optional tunnel:        ssh -L ${CODEX_PORT}:127.0.0.1:${CODEX_PORT} ${ssh_user}@${hyperv_dns}
EOF
fi

# T3 Code web GUI: shown when the opt-in is on or its service is deployed.
if [[ "${T3CODE}" == "true" ]] \
   || systemctl is-active --quiet t3code-serve 2>/dev/null \
   || systemctl is-enabled --quiet t3code-serve 2>/dev/null; then
    cat <<EOF

T3 Code (web GUI):
  Service:  t3code-serve
  Bind:     ${T3CODE_HOST}:${T3CODE_PORT}
  URL:      http://${hyperv_dns}:${T3CODE_PORT}
  Login:    mint a pairing link -- t3 auth pairing create --base-url http://${hyperv_dns}:${T3CODE_PORT}
            (or use the control panel's "open web UI" button)
EOF
fi

# The VS Code CLI is installed by default, so connecting via Remote-SSH just works.
if command -v code >/dev/null 2>&1; then
    cat <<EOF

VS Code Remote-SSH:
  Server installed; connect via Remote Explorer -> SSH -> ${hyperv_dns}
EOF
fi

# Browser VS Code (serve-web): shown when its service is deployed or selected.
if systemctl is-enabled --quiet code-serve-web 2>/dev/null \
   || systemctl is-active --quiet code-serve-web 2>/dev/null \
   || [[ "${VSCODE_SERVE_WEB}" == "true" ]]; then
    sw_state="not started"
    if systemctl is-active --quiet code-serve-web 2>/dev/null; then
        sw_state="running"
    elif systemctl is-enabled --quiet code-serve-web 2>/dev/null; then
        sw_state="enabled (not running)"
    fi
    # serve-web authenticates only via a localhost origin, so show the localhost
    # URL you actually open (reach the port via your own forward/tunnel).
    sw_url="http://localhost:${VSCODE_SERVE_WEB_PORT}"
    cat <<EOF

VS Code Server (serve-web):
  Service:   code-serve-web (${sw_state})
  Bind:      ${VSCODE_SERVE_WEB_HOST}:${VSCODE_SERVE_WEB_PORT}
  Open:      ${sw_url}/?tkn=<token>
  Token:     cat ${VSCODE_SERVE_WEB_TOKEN_FILE}
EOF
fi

# Show the tunnel only when its service is actually deployed (enabled/active) or
# explicitly selected -- not merely because the CLI is present.
if systemctl is-enabled --quiet code-tunnel 2>/dev/null \
   || systemctl is-active --quiet code-tunnel 2>/dev/null \
   || [[ "${VSCODE_TUNNEL}" == "true" ]]; then
    tunnel_name="${VSCODE_TUNNEL_NAME:-$(hostname)}"
    if systemctl is-active --quiet code-tunnel 2>/dev/null; then
        tunnel_state="running"
    elif systemctl is-enabled --quiet code-tunnel 2>/dev/null; then
        tunnel_state="enabled (not running)"
    else
        tunnel_state="not started"
    fi
    cat <<EOF

VS Code Remote Tunnel:
  Service:          code-tunnel (${tunnel_state})
  Open in browser:  https://vscode.dev/tunnel/${tunnel_name}
  Or in VS Code:    Remote Explorer -> Tunnels -> ${tunnel_name}
  First-time login: journalctl -u code-tunnel -n 50  (use the github.com/login/device link)
EOF
fi

# Workspace file share (SMB): shown when its service is active or selected.
if systemctl is-active --quiet smbd 2>/dev/null || [[ "${SMB_SHARE}" == "true" ]]; then
    cat <<EOF

Workspace file share (SMB):
  Share:    \\\\${hyperv_dns}\\${SMB_SHARE_NAME}  (or \\\\${lan_ip}\\${SMB_SHARE_NAME})
  Path:     ${WORKSPACE_ROOT} (host accesses it as root)
  User:     ${SMB_USER}
  Password: sudo cat /etc/construct/config.env | grep '^SMB_PASSWORD='
  Mount on Windows:
    net use Z: \\\\${hyperv_dns}\\${SMB_SHARE_NAME} /user:${SMB_USER} <password> /savecred /persistent:yes
EOF
fi

case ",${AI_TOOLS}," in
  *,pi,*)
    cat <<EOF

pi:
  Selected, but installer/runtime is not implemented yet.
EOF
    ;;
esac

cat <<'EOF'
============================================================

EOF
