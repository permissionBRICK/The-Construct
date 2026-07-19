#!/usr/bin/env bash
#
# Restore an agent configuration previously captured by export-config.sh.
#
# Given a backup tarball (or an already-extracted backup directory), this copies
# the saved home tree back over the target user's home and tightens permissions
# on the secret files so the coding agents accept them.
#
# Inputs (via environment):
#   BACKUP_TGZ   path to a backup tarball produced by export-config.sh, OR
#   BACKUP_DIR   path to an already-extracted backup (containing home/)
#   EXPORT_HOME  home to restore into            (default /root)
#
# Exactly one of BACKUP_TGZ / BACKUP_DIR is required.
#
set -euo pipefail

EXPORT_HOME="${EXPORT_HOME:-/root}"
BACKUP_TGZ="${BACKUP_TGZ:-}"
BACKUP_DIR="${BACKUP_DIR:-}"

log() { printf '  %s\n' "$*"; }
err() { printf '  %s\n' "$*" >&2; }

cleanup_tmp=""
trap '[[ -n "${cleanup_tmp}" ]] && rm -rf "${cleanup_tmp}"' EXIT

if [[ -n "${BACKUP_TGZ}" ]]; then
  if [[ ! -f "${BACKUP_TGZ}" ]]; then err "Backup tarball not found: ${BACKUP_TGZ}"; exit 1; fi
  cleanup_tmp="$(mktemp -d /tmp/construct-restore.XXXXXX)"
  BACKUP_DIR="${cleanup_tmp}"
  tar -xzf "${BACKUP_TGZ}" -C "${BACKUP_DIR}"
fi

if [[ -z "${BACKUP_DIR}" || ! -d "${BACKUP_DIR}/home" ]]; then
  err "No backup home/ tree found (set BACKUP_TGZ or BACKUP_DIR to a valid export)."
  exit 1
fi

printf '==> Restoring agent config into %s\n' "${EXPORT_HOME}"
mkdir -p "${EXPORT_HOME}"

# Copy preserving ownership/perms/timestamps. The trailing /. copies the
# contents (including dotfiles) without nesting under a "home" directory.
cp -a "${BACKUP_DIR}/home/." "${EXPORT_HOME}/"

# Tighten permissions on the secrets so the agents (and ssh, for git) accept them.
# Includes the per-agent MCP server OAuth stores (.codex/.credentials.json,
# opencode mcp-auth.json) alongside the subscription-auth files.
for f in \
  ".claude/.credentials.json" \
  ".claude.json" \
  ".codex/auth.json" \
  ".codex/.credentials.json" \
  ".local/share/opencode/auth.json" \
  ".local/share/opencode/mcp-auth.json" \
  ".config/gh/hosts.yml" \
  ".git-credentials" \
  ".npmrc"; do
  if [[ -e "${EXPORT_HOME}/${f}" ]]; then
    chmod 600 "${EXPORT_HOME}/${f}" 2>/dev/null || true
    log "restored ${f}"
  fi
done

# SSH: export-config.sh captures outbound keys (never authorized_keys / the
# provisioner key). OpenSSH refuses a private key that is group/world readable,
# and cp -a preserves the key-file modes, but the staged ~/.ssh dir is created
# with the export's umask -- so pin the strict perms here: dir 700, private keys
# 600. Public keys, known_hosts, and config keep their copied modes, and any
# provisioner-written authorized_keys is left untouched.
if [[ -d "${EXPORT_HOME}/.ssh" ]]; then
  chmod 700 "${EXPORT_HOME}/.ssh" 2>/dev/null || true
  for _k in "${EXPORT_HOME}/.ssh"/*; do
    [[ -f "${_k}" ]] || continue
    case "${_k}" in
      *.pub|*/known_hosts|*/known_hosts.old|*/config|*/authorized_keys|*/authorized_keys2) continue ;;
    esac
    chmod 600 "${_k}" 2>/dev/null || true
  done
  log "restored .ssh (outbound keys, perms tightened)"
fi

# Report instruction / memory files that came back, for the provisioning log.
for f in ".claude/CLAUDE.md" ".codex/AGENTS.md" ".config/opencode/AGENTS.md"; do
  [[ -e "${EXPORT_HOME}/${f}" ]] && log "restored ${f}"
done

# Report restored chat history (captured when the export ran with
# INCLUDE_HISTORY=true), so the provisioning log shows it came back.
for f in ".claude/history.jsonl" ".codex/sessions" ".local/share/opencode/storage"; do
  [[ -e "${EXPORT_HOME}/${f}" ]] && log "restored chat history: ${f}"
done

if [[ -f "${BACKUP_DIR}/backup-info.json" ]]; then
  log "backup metadata: $(tr -d '\n' <"${BACKUP_DIR}/backup-info.json")"
fi

printf '==> Restore complete\n'
