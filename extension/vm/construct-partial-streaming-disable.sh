#!/usr/bin/env bash
# construct-partial-streaming-disable — undo construct-partial-streaming-enable.sh,
# restoring the extension's stock `includePartialMessages: !vscode.env.remoteName`
# behaviour (partial streaming OFF over Remote-SSH).
#
# Idempotent + honest: prefers the byte-for-byte .construct-partial.bak backup the
# enable step made; if there is no backup it reverts the known transform
#   includePartialMessages:!le.env.remoteName||!0  ->  includePartialMessages:!le.env.remoteName
# A copy that carries neither the patch nor a backup is left untouched. Prints status
# to stderr; exit 0 on success (best-effort, never fails).

set -u

log() { printf '%s\n' "construct-partial-streaming-disable: $*" >&2; }

PATCHED='includePartialMessages:!le.env.remoteName||!0'
ORIG='includePartialMessages:!le.env.remoteName'

revert_file() {
  f="$1"
  [ -f "$f" ] || return 0
  bak="${f}.construct-partial.bak"
  if [ -f "$bak" ]; then
    # Restore the exact original, then drop the backup.
    if cp -p "$bak" "$f"; then
      rm -f "$bak"
      log "restored $f from backup (partial streaming reverted to stock)."
      return 0
    fi
    log "WARNING: failed to restore $f from $bak."
    return 0
  fi
  if grep -qF "$PATCHED" "$f"; then
    if PATCHED="$PATCHED" ORIG="$ORIG" perl -0777 -pi -e '
        my ($p,$o)=($ENV{PATCHED},$ENV{ORIG});
        my $i=index($_,$p);
        if ($i>=0) { substr($_,$i,length($p))=$o; }
      ' "$f"; then
      log "reverted the partial-message patch in $f (no backup was present)."
    else
      log "WARNING: failed to revert $f."
    fi
  fi
}

for ext in "$HOME"/.vscode-server/extensions/anthropic.claude-code-* \
           "$HOME"/.vscode/extensions/anthropic.claude-code-*; do
  [ -d "$ext" ] || continue
  for cand in "$ext/dist/extension.js" "$ext/extension.js"; do
    revert_file "$cand"
  done
done

log "disable complete."
exit 0
