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
set -Eeuo pipefail

EXPORT_HOME="${EXPORT_HOME:-/root}"
BACKUP_TGZ="${BACKUP_TGZ:-}"
BACKUP_DIR="${BACKUP_DIR:-}"

log() { printf '  %s\n' "$*"; }
err() { printf '  %s\n' "$*" >&2; }

# `set -e` otherwise makes simple-command failures (for example a malformed
# metadata jq read) disappear behind the host's generic "remote command failed"
# message. Report the exact script line while deliberately omitting BASH_COMMAND,
# which could contain restored paths or metadata from the user's backup.
restore_err_reported=""
report_restore_error() {
  local rc=$?
  [[ -n "${restore_err_reported}" ]] && return "${rc}"
  restore_err_reported=1
  err "Restore failed at restore-config.sh line ${BASH_LINENO[0]} (exit ${rc})."
  return "${rc}"
}
trap report_restore_error ERR

cleanup_tmp=""
t3_was_running=""
codex_was_running=""
cleanup_restore() {
  local rc=$?
  trap - EXIT
  if [[ "${rc}" -ne 0 ]]; then
    [[ -n "${t3_was_running:-}" ]] && systemctl start t3code-serve 2>/dev/null || true
    [[ -n "${codex_was_running:-}" ]] && systemctl start codex-app-server 2>/dev/null || true
  fi
  [[ -n "${cleanup_tmp}" ]] && rm -rf "${cleanup_tmp}"
  exit "${rc}"
}
trap cleanup_restore EXIT
archive_restore=""
archive_list=""
archive_home_member=""
archive_strip_components=""

if [[ -n "${BACKUP_TGZ}" ]]; then
  if [[ ! -f "${BACKUP_TGZ}" ]]; then err "Backup tarball not found: ${BACKUP_TGZ}"; exit 1; fi
  cleanup_tmp="$(mktemp -d /tmp/construct-restore.XXXXXX)"
  BACKUP_DIR="${cleanup_tmp}"
  archive_list="${cleanup_tmp}/archive.list"
  if ! tar -tzf "${BACKUP_TGZ}" >"${archive_list}"; then
    err "Backup tarball is unreadable or truncated: ${BACKUP_TGZ}"
    exit 1
  fi
  if grep -qx './home/' "${archive_list}"; then
    archive_home_member='./home'
    archive_strip_components=2
  elif grep -qx 'home/' "${archive_list}"; then
    archive_home_member='home'
    archive_strip_components=1
  else
    err "No backup home/ tree found in ${BACKUP_TGZ}."
    exit 1
  fi
  # Only metadata needs staging. Stream home/ directly into EXPORT_HOME later,
  # avoiding a second copy of the full uncompressed history tree and the extra
  # file-type conflict surface of an extract-then-cp overlay.
  if grep -qx './backup-info.json' "${archive_list}"; then
    tar -xOf "${BACKUP_TGZ}" ./backup-info.json >"${BACKUP_DIR}/backup-info.json"
  elif grep -qx 'backup-info.json' "${archive_list}"; then
    tar -xOf "${BACKUP_TGZ}" backup-info.json >"${BACKUP_DIR}/backup-info.json"
  fi
  archive_restore=1
fi

if [[ -z "${BACKUP_DIR}" || ( -z "${archive_restore}" && ! -d "${BACKUP_DIR}/home" ) ]]; then
  err "No backup home/ tree found (set BACKUP_TGZ or BACKUP_DIR to a valid export)."
  exit 1
fi

printf '==> Restoring agent config into %s\n' "${EXPORT_HOME}"
log "Construct restore revision: ${CONSTRUCT_VERSION:-unversioned}"
mkdir -p "${EXPORT_HOME}"

# The backup owns Claude's complete user-skill tree. Project provision commands
# may already have recreated repo-managed skills there as symlinks; tar cannot
# replace an existing directory with a saved symlink (or vice versa) and exits
# with "Cannot open: File exists". Remove only this complete, backed-up tree
# before overlaying it. Do not do the same for Codex skills: export deliberately
# omits `.codex/skills/.system`, which the fresh installation must retain.
claude_skills_restore=""
if [[ -n "${archive_restore}" ]]; then
  grep -Eq '^(\./)?home/\.claude/skills(/|$)' "${archive_list}" && claude_skills_restore=1 || true
elif [[ -e "${BACKUP_DIR}/home/.claude/skills" || -L "${BACKUP_DIR}/home/.claude/skills" ]]; then
  claude_skills_restore=1
fi
if [[ -n "${claude_skills_restore}" ]]; then
  rm -rf "${EXPORT_HOME}/.claude/skills"
  log "cleared freshly provisioned Claude skills before restoring the saved skill tree"
fi

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
if [[ -n "${archive_restore}" ]]; then
  grep -Eq '^(\./)?home/\.codex/(sessions|archived_sessions)(/|$)' "${archive_list}" && codex_reindex=1 || true
elif [[ -d "${BACKUP_DIR}/home/.codex/sessions" || -d "${BACKUP_DIR}/home/.codex/archived_sessions" ]]; then
  codex_reindex=1
fi
if [[ -n "${codex_reindex}" ]]; then
  if command -v systemctl >/dev/null 2>&1; then
    case "$(systemctl is-active codex-app-server 2>/dev/null || true)" in
      active|activating|reloading) codex_was_running=1 ;;
    esac
    if [[ -n "${codex_was_running}" ]]; then
      log "pausing codex-app-server for restored session index"
      systemctl stop codex-app-server 2>/dev/null || true
      log "codex-app-server paused"
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
t3_state_restore=""
if [[ -n "${archive_restore}" ]]; then
  grep -Eq '^(\./)?home/\.t3/userdata/state\.sqlite' "${archive_list}" && t3_state_restore=1 || true
elif compgen -G "${BACKUP_DIR}/home/.t3/userdata/state.sqlite*" >/dev/null 2>&1; then
  t3_state_restore=1
fi
if [[ -n "${t3_state_restore}" ]]; then
  if command -v systemctl >/dev/null 2>&1; then
    case "$(systemctl is-active t3code-serve 2>/dev/null || true)" in
      active|activating|reloading) t3_was_running=1 ;;
    esac
    if [[ -n "${t3_was_running}" ]]; then
      log "pausing t3code-serve for restored event store"
      systemctl stop t3code-serve 2>/dev/null || true
      log "t3code-serve paused"
    fi
  fi
  rm -f "${EXPORT_HOME}/.t3/userdata/state.sqlite" \
        "${EXPORT_HOME}/.t3/userdata/state.sqlite-wal" \
        "${EXPORT_HOME}/.t3/userdata/state.sqlite-shm"
fi

# Overlay while preserving ownership/perms/timestamps. Tarball restores stream
# home/ directly; an already-extracted BACKUP_DIR uses trailing /. so dotfiles
# copy without nesting under a "home" directory.
if [[ -n "${archive_restore}" ]]; then
  log "extracting saved home tree from archive"
  if ! tar -xzf "${BACKUP_TGZ}" -C "${EXPORT_HOME}" \
      --strip-components="${archive_strip_components}" "${archive_home_member}"; then
    err "Agent config overlay failed while extracting ${BACKUP_TGZ} into ${EXPORT_HOME}."
    err "Free space: $(df -h "${EXPORT_HOME}" 2>/dev/null | awk 'NR==2 {print $4 " available on " $1}' || echo unknown)"
    exit 1
  fi
  log "saved home tree extracted"
else
  if ! cp -a "${BACKUP_DIR}/home/." "${EXPORT_HOME}/"; then
    err "Agent config overlay failed while copying ${BACKUP_DIR}/home into ${EXPORT_HOME}."
    err "Free space: $(df -h "${EXPORT_HOME}" 2>/dev/null | awk 'NR==2 {print $4 " available on " $1}' || echo unknown)"
    exit 1
  fi
fi

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

# User secrets store: free-form secret material (API keys, .env files, ...).
# cp -a preserved the file modes, but the staged dirs may carry the export's
# umask -- pin the whole tree private: dirs 700, files 600.
if [[ -d "${EXPORT_HOME}/.secrets" ]]; then
  find "${EXPORT_HOME}/.secrets" -type d -exec chmod 700 {} + 2>/dev/null || true
  find "${EXPORT_HOME}/.secrets" -type f -exec chmod 600 {} + 2>/dev/null || true
  log "restored .secrets (perms tightened)"
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

# ── T3 Code channel preference from backup metadata ─────────────────────────
# The channel preference (stable/nightly) is persisted INDEPENDENTLY of whether
# T3 Code is enabled: a user who selected nightly while T3 was disabled expects
# the preference to survive a reinstall. The actual package install is still
# conditional on the enabled flag below.
REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"

# ── T3 Code HTTPS local CA ───────────────────────────────────────────────────
# The saved /etc/construct/tls tree carries the CA whose certificate is already
# trusted on the host, so restoring it keeps the browser happy without a fresh
# import. It rides OUTSIDE home (like the serve-web token), hence the explicit
# extraction. The restored leaf may no longer match this VM's names, and a leaf
# minted minutes ago by this provision no longer chains to the restored CA --
# setup-t3-https.sh detects both (SAN drift / failed openssl verify) and reissues
# the leaf, so re-run it here. Only when T3 Code is actually deployed: on a VM
# without it there is nothing to serve and nothing to reconcile.
TLS_DIR="${T3CODE_TLS_DIR:-/etc/construct/tls}"
tls_restore=""
if [[ -n "${archive_restore}" ]]; then
  grep -Eq '^(\./)?etc/construct/tls/' "${archive_list}" && tls_restore=1 || true
elif compgen -G "${BACKUP_DIR}/etc/construct/tls/*" >/dev/null 2>&1; then
  tls_restore=1
fi
if [[ -n "${tls_restore}" ]]; then
  mkdir -p "${TLS_DIR}"
  chmod 700 "${TLS_DIR}" 2>/dev/null || true
  _tls_ok=1
  if [[ -n "${archive_restore}" ]]; then
    # Extract WITHOUT --strip-components: both the './etc/...' and 'etc/...'
    # member spellings then land at the same path inside the staging dir.
    _tls_stage="$(mktemp -d /tmp/construct-restore-tls.XXXXXX)"
    if tar -xzf "${BACKUP_TGZ}" -C "${_tls_stage}" \
        "$(grep -Eom1 '^(\./)?etc/construct/tls' "${archive_list}")" 2>/dev/null \
       && [[ -d "${_tls_stage}/etc/construct/tls" ]]; then
      cp -a "${_tls_stage}/etc/construct/tls/." "${TLS_DIR}/" || _tls_ok=""
    else
      _tls_ok=""
    fi
    rm -rf "${_tls_stage}"
  else
    cp -a "${BACKUP_DIR}/etc/construct/tls/." "${TLS_DIR}/" || _tls_ok=""
  fi
  if [[ -n "${_tls_ok}" ]]; then
    # Private keys must not come back group/world readable, whatever umask the
    # export ran under; the CA certificate itself is public.
    chmod 600 "${TLS_DIR}"/*.key 2>/dev/null || true
    chmod 644 "${TLS_DIR}"/*.crt 2>/dev/null || true
    log "restored etc/construct/tls (T3 HTTPS local CA)"
    if [[ -f "${REPO_DIR}/bin/setup-t3-https.sh" ]] \
       && systemctl is-enabled --quiet t3code-serve 2>/dev/null; then
      if ! env CONFIG_FILE="${CONFIG_FILE}" REPO_DIR="${REPO_DIR}" \
          bash "${REPO_DIR}/bin/setup-t3-https.sh"; then
        err "T3 Code HTTPS could not be reconciled against the restored CA; reprovision to retry"
      fi
    fi
  else
    err "restoring the T3 HTTPS CA failed; a new CA is generated on the next provision (re-import it on the host)"
  fi
fi

if [[ -f "${BACKUP_DIR}/backup-info.json" ]]; then
  _restore_t3ch="$(jq -r '.t3codeChannel // "stable"' "${BACKUP_DIR}/backup-info.json" 2>/dev/null)"
  [[ "${_restore_t3ch}" == "nightly" ]] || _restore_t3ch=stable
  if [[ -f "${REPO_DIR}/bin/config-set.sh" ]]; then
    bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE_CHANNEL "${_restore_t3ch}" 2>/dev/null || true
    log "restored T3 Code channel preference: ${_restore_t3ch}"
  fi
fi

# ── Optional OpenCode background watcher from backup metadata ───────────────
# The plugin is Construct-managed code, so it is regenerated from the current
# repo instead of copied into the backup. Older backups omit this key; in that
# case leave the freshly provisioned/panel-selected preference untouched.
if [[ -f "${BACKUP_DIR}/backup-info.json" ]]; then
  _restore_ocbg="$(jq -r 'if has("opencodeBackgroundWatcher") and (.opencodeBackgroundWatcher | type == "boolean") then (.opencodeBackgroundWatcher | tostring) else "" end' "${BACKUP_DIR}/backup-info.json" 2>/dev/null)"
  if [[ "${_restore_ocbg}" == "true" || "${_restore_ocbg}" == "false" ]]; then
    if [[ -f "${REPO_DIR}/bin/config-set.sh" ]]; then
      bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" OPENCODE_BACKGROUND_WATCHER "${_restore_ocbg}" 2>/dev/null || true
    fi
    if [[ -f "${REPO_DIR}/bin/install-ai-tools.sh" ]]; then
      log "restoring OpenCode background watcher preference: ${_restore_ocbg}"
      if ! env CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${REPO_DIR}" \
          OPENCODE_BACKGROUND_WATCHER="${_restore_ocbg}" \
          bash -c 'source "$1"; configure_opencode_background_watcher "$2" root' \
          _ "${REPO_DIR}/bin/install-ai-tools.sh" "${EXPORT_HOME}"; then
        err "OpenCode background watcher restore failed; reprovision to retry"
      fi
    fi
  fi
fi

# ── T3 Code reinstall from backup metadata ───────────────────────────────────
# A console reinstall provisions the fresh VM with an EMPTY T3CODE (keep-saved
# semantics), and the new config.env has nothing saved -- so T3 Code doesn't get
# installed even though its data was just restored. The export records the
# enabled flag in backup-info.json; honour it here by installing + starting the
# service via the uploaded repo's installer. Best-effort: a failure logs the
# manual fix and never aborts the restore. Skipped when the service is already
# enabled (panel-driven flows pass -T3Code true and installed it earlier).
if [[ -f "${BACKUP_DIR}/backup-info.json" ]] \
   && [[ "$(jq -r '.t3code // false' "${BACKUP_DIR}/backup-info.json" 2>/dev/null)" == "true" ]] \
   && ! systemctl is-enabled --quiet t3code-serve 2>/dev/null; then
  _restore_t3ch="$(jq -r '.t3codeChannel // "stable"' "${BACKUP_DIR}/backup-info.json" 2>/dev/null)"
  [[ "${_restore_t3ch}" == "nightly" ]] || _restore_t3ch=stable
  if [[ -f "${REPO_DIR}/bin/install-ai-tools.sh" ]]; then
    log "backup has T3 Code enabled (channel=${_restore_t3ch}); installing + starting t3code-serve"
    bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" T3CODE true 2>/dev/null || true
    if ! env TARGET_USER=root AI_TOOLS_OVERRIDE=t3code AI_CONSOLE_INTEGRATION=false \
        T3CODE_CHANNEL="${_restore_t3ch}" \
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
