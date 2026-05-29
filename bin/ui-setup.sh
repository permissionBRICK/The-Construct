#!/usr/bin/env bash
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
REPO_DIR="${REPO_DIR:-/opt/construct/repo}"

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run with sudo: sudo ${REPO_DIR}/bin/ui-setup.sh" >&2
  exit 1
fi

if [[ ! -t 0 ]]; then
  echo "No interactive terminal available; skipping UI setup." >&2
  exit 0
fi

cat <<'EOF'

AI agent tool setup

Select the tools to install for this VM. Enter numbers separated by commas.

1. opencode    Install CLI and autostart: opencode serve --hostname 0.0.0.0
2. claude code Install CLI and print SSH connection info
3. codex       Install CLI and start experimental app-server on localhost
4. pi          Placeholder: record selection only; installer not implemented yet

Examples: 1,2 or all or none
EOF

read -r -p "Set up root SSH key for Codex/App remote access? [Y/n]: " setup_root_ssh
setup_root_ssh="${setup_root_ssh:-Y}"
case "${setup_root_ssh}" in
  y|Y|yes|YES|Yes)
    "${REPO_DIR}/bin/setup-root-ssh-key.sh"
    ;;
  *)
    echo "Skipping root SSH key setup"
    ;;
esac

cat <<'EOF'

AI agent tool selection
EOF

read -r -p "Tools to install [1,2,3]: " selection
selection="${selection:-1,2,3}"

tools=()
case "${selection}" in
  all|ALL|All)
    tools=(opencode claude-code codex pi)
    ;;
  none|NONE|None)
    tools=()
    ;;
  *)
    IFS=',' read -ra choices <<<"${selection}"
    for raw_choice in "${choices[@]}"; do
      choice="$(printf '%s' "${raw_choice}" | xargs)"
      case "${choice}" in
        1|opencode) tools+=(opencode) ;;
        2|claude|claude-code|"claude code") tools+=(claude-code) ;;
        3|codex) tools+=(codex) ;;
        4|pi) tools+=(pi) ;;
        "") ;;
        *) echo "Ignoring unknown selection: ${choice}" >&2 ;;
      esac
    done
    ;;
esac

deduped_tools="$(printf '%s\n' "${tools[@]}" | awk 'NF && !seen[$0]++' | paste -sd, -)"

"${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" AI_TOOLS "${deduped_tools}"
"${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" OPENCODE_HOST "0.0.0.0"
"${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" OPENCODE_PORT "4096"
"${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" CODEX_HOST "0.0.0.0"
"${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" CODEX_PORT "4500"

echo "Selected AI tools: ${deduped_tools:-none}"
"${REPO_DIR}/bin/install-ai-tools.sh"
