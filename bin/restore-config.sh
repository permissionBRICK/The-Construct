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

# ── Codex thread index vs. restored sessions ─────────────────────────────────
# Modern Codex lists/resumes threads from a sqlite index (~/.codex/
# state_*.sqlite, threads table) -- the sessions/*.jsonl rollouts are only
# transcript storage. On its first start with an empty home Codex runs a
# ONE-SHOT rollout backfill into that index and marks it complete. During a
# reinstall, provision.sh starts codex-app-server BEFORE this restore runs, so
# by the time the old rollouts land here the backfill is already "complete for
# an empty sessions dir" and Codex never re-scans: the restored history exists
# on disk but is invisible to the picker. Fix: when the backup carries codex
# sessions (live or archived), stop the app-server across the overlay (it
# holds the sqlite files open) and delete the freshly-minted index; the next
# start re-runs the backfill over the restored rollouts (verified to re-import
# every rollout, titles included). The dropped index is minutes old and rebuilt
# from the rollouts, so nothing of value is lost; only on a BY-HAND restore
# onto a long-lived VM does this also reset index-only metadata (archived
# flags).
codex_reindex=""
codex_was_running=""
if [[ -d "${BACKUP_DIR}/home/.codex/sessions" || -d "${BACKUP_DIR}/home/.codex/archived_sessions" ]]; then
  codex_reindex=1
  if command -v systemctl >/dev/null 2>&1; then
    case "$(systemctl is-active codex-app-server 2>/dev/null || true)" in
      active|activating|reloading) codex_was_running=1 ;;
    esac
    if [[ -n "${codex_was_running}" ]]; then
      systemctl stop codex-app-server 2>/dev/null || true
    fi
  fi
fi

# ── T3 Code sqlite across the overlay ────────────────────────────────────────
# When the backup carries T3 Code's state.sqlite (threads + auth sessions +
# pairing state in one event-store), the freshly-provisioned t3code-serve is
# already running and holds ITS OWN newly-minted DB open. Overlaying the files
# under the live server risks a torn restore, and leaving the new -wal/-shm
# siblings next to the restored .sqlite would corrupt it on next open. Stop the
# service across the copy, drop the minutes-old empty DB (nothing of value in
# it), and start the server again after -- it then opens the restored store.
t3_was_running=""
if compgen -G "${BACKUP_DIR}/home/.t3/userdata/state.sqlite*" >/dev/null 2>&1; then
  if command -v systemctl >/dev/null 2>&1; then
    case "$(systemctl is-active t3code-serve 2>/dev/null || true)" in
      active|activating|reloading) t3_was_running=1 ;;
    esac
    if [[ -n "${t3_was_running}" ]]; then
      systemctl stop t3code-serve 2>/dev/null || true
    fi
  fi
  rm -f "${EXPORT_HOME}/.t3/userdata/state.sqlite" \
        "${EXPORT_HOME}/.t3/userdata/state.sqlite-wal" \
        "${EXPORT_HOME}/.t3/userdata/state.sqlite-shm"
fi

# Copy preserving ownership/perms/timestamps. The trailing /. copies the
# contents (including dotfiles) without nesting under a "home" directory.
cp -a "${BACKUP_DIR}/home/." "${EXPORT_HOME}/"

if [[ -n "${t3_was_running}" ]]; then
  systemctl start t3code-serve 2>/dev/null || true
  log "restored T3 Code state; t3code-serve restarted on the restored store"
fi

if [[ -n "${codex_reindex}" ]]; then
  rm -f "${EXPORT_HOME}/.codex/state_"*.sqlite*
  if [[ -n "${codex_was_running}" ]]; then
    systemctl start codex-app-server 2>/dev/null || true
  fi
  log "reset codex thread index; restored sessions re-index on next codex start"
fi

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

# T3 Code secrets: the server refuses group/world-readable key material, and the
# staged dirs may carry the export's umask -- pin dir 700, key files 600.
if [[ -d "${EXPORT_HOME}/.t3/userdata/secrets" ]]; then
  chmod 700 "${EXPORT_HOME}/.t3/userdata/secrets" 2>/dev/null || true
  chmod 600 "${EXPORT_HOME}/.t3/userdata/secrets"/* 2>/dev/null || true
  log "restored .t3/userdata/secrets (perms tightened)"
fi

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
for f in ".claude/history.jsonl" ".codex/sessions" ".local/share/opencode/storage" ".t3/userdata/state.sqlite"; do
  [[ -e "${EXPORT_HOME}/${f}" ]] && log "restored chat history: ${f}"
done

# ── T3 Code reinstall from backup metadata ───────────────────────────────────
# A console reinstall provisions the fresh VM with an EMPTY T3CODE (keep-saved
# semantics), and the new config.env has nothing saved -- so T3 Code doesn't get
# installed even though its data was just restored. The export records the
# enabled flag in backup-info.json; honour it here by installing + starting the
# service via the uploaded repo's installer. Best-effort: a failure logs the
# manual fix and never aborts the restore. Skipped when the service is already
# enabled (panel-driven flows pass -T3Code true and installed it earlier).
REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
if [[ -f "${BACKUP_DIR}/backup-info.json" ]] \
   && [[ "$(jq -r '.t3code // false' "${BACKUP_DIR}/backup-info.json" 2>/dev/null)" == "true" ]] \
   && ! systemctl is-enabled --quiet t3code-serve 2>/dev/null; then
  if [[ -f "${REPO_DIR}/bin/install-ai-tools.sh" ]]; then
    log "backup has T3 Code enabled; installing + starting t3code-serve"
    bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE true 2>/dev/null || true
    if ! env TARGET_USER=root AI_TOOLS_OVERRIDE=t3code AI_CONSOLE_INTEGRATION=false \
        bash "${REPO_DIR}/bin/install-ai-tools.sh"; then
      err "T3 Code reinstall failed; reprovision (or run: sudo env AI_TOOLS_OVERRIDE=t3code bash ${REPO_DIR}/bin/install-ai-tools.sh)"
    fi
  else
    err "backup has T3 Code enabled, but ${REPO_DIR}/bin/install-ai-tools.sh is missing; reprovision to install it"
  fi
fi

if [[ -f "${BACKUP_DIR}/backup-info.json" ]]; then
  log "backup metadata: $(tr -d '\n' <"${BACKUP_DIR}/backup-info.json")"
fi

printf '==> Restore complete\n'
