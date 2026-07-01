#!/usr/bin/env bash
# construct-audio-disable — turn OFF mic passthrough on the Construct VM.
#
# Reverses construct-audio-enable.sh, both steps, idempotently:
#   1) remove the rec/arecord shim from /usr/local/bin (so PATH falls back to the
#      bundled sox again, i.e. a real deviceless failure rather than our tunnel);
#   2) restore the anthropic.claude-code-* extension.js to its original gate — from
#      the .construct-audio.bak backup if present (byte-for-byte), else by reverting
#      the neutralised substring in place.
#
# Takes no input. Best-effort: a missing shim / already-reverted gate is a no-op, so
# re-running is harmless. Prints status to stderr; exit 0.

set -u

REC_PATH="/usr/local/bin/rec"
ARECORD_PATH="/usr/local/bin/arecord"
ANCHOR='remoteName)return!1'
PATCHED='remoteName&&!1)return!1'

log() { printf '%s\n' "construct-audio-disable: $*" >&2; }

# ── 1) remove the shim ─────────────────────────────────────────────────────────
# Only remove OUR shim: guard that /usr/local/bin/rec is the construct shim (not a
# user-installed sox link) by checking for the shim's marker before deleting. The
# arecord entry is a symlink we created, so remove it if it points at REC_PATH.
if [ -f "$REC_PATH" ] && grep -q "construct-rec-shim" "$REC_PATH" 2>/dev/null; then
  rm -f "$REC_PATH"
  log "removed the recorder shim at $REC_PATH."
else
  log "no construct recorder shim at $REC_PATH (nothing to remove)."
fi
# Remove the arecord symlink only if it points at our (now-removed) shim path.
if [ -L "$ARECORD_PATH" ]; then
  target="$(readlink "$ARECORD_PATH" 2>/dev/null || true)"
  if [ "$target" = "$REC_PATH" ]; then
    rm -f "$ARECORD_PATH"
    log "removed the arecord symlink."
  fi
fi

# ── 2) revert the speech gate ──────────────────────────────────────────────────
restore_file() {
  f="$1"
  [ -f "$f" ] || return 0
  bak="${f}.construct-audio.bak"
  if [ -f "$bak" ]; then
    # Prefer the backup: restores the ORIGINAL bytes exactly (also undoes any drift).
    mv -f "$bak" "$f"
    log "restored $f from backup."
    return 0
  fi
  # No backup (e.g. patched by an older enable, or the backup was cleaned): revert
  # the neutralised substring in place if it's present.
  if grep -qF "$PATCHED" "$f"; then
    if ANCHOR="$ANCHOR" PATCHED="$PATCHED" perl -0777 -pi -e '
        my ($a,$p)=($ENV{ANCHOR},$ENV{PATCHED});
        my $i=index($_,$p);
        if ($i>=0) { substr($_,$i,length($p))=$a; }
      ' "$f"; then
      log "reverted the neutralised gate in $f (no backup present)."
    else
      log "WARNING: failed to revert $f."
    fi
  fi
}

for ext in "$HOME"/.vscode-server/extensions/anthropic.claude-code-* \
           "$HOME"/.vscode/extensions/anthropic.claude-code-*; do
  [ -d "$ext" ] || continue
  for cand in "$ext/dist/extension.js" "$ext/extension.js"; do
    restore_file "$cand"
  done
done

log "disable complete."
exit 0
