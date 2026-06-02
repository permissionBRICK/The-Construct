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
for f in \
  ".claude/.credentials.json" \
  ".claude.json" \
  ".codex/auth.json" \
  ".local/share/opencode/auth.json" \
  ".config/gh/hosts.yml" \
  ".git-credentials"; do
  if [[ -e "${EXPORT_HOME}/${f}" ]]; then
    chmod 600 "${EXPORT_HOME}/${f}" 2>/dev/null || true
    log "restored ${f}"
  fi
done

# Report instruction / memory files that came back, for the provisioning log.
for f in ".claude/CLAUDE.md" ".codex/AGENTS.md" ".config/opencode/AGENTS.md"; do
  [[ -e "${EXPORT_HOME}/${f}" ]] && log "restored ${f}"
done

if [[ -f "${BACKUP_DIR}/backup-info.json" ]]; then
  log "backup metadata: $(tr -d '\n' <"${BACKUP_DIR}/backup-info.json")"
fi

printf '==> Restore complete\n'
