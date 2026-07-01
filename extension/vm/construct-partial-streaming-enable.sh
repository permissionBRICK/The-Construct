#!/usr/bin/env bash
# construct-partial-streaming-enable — stream partial assistant messages over
# Remote-SSH on the Construct VM.
#
# WHY. The anthropic.claude-code extension builds its SDK query with
#   includePartialMessages: !vscode.env.remoteName
# so partial-message streaming (the `--include-partial-messages` CLI flag) is turned
# OFF whenever the window is attached to a remote (Remote-SSH into this VM). With it
# off, the CLI emits each assistant turn only once it is FULLY generated: the panel
# shows nothing — no thinking, no text — for the entire generation window (tens of
# seconds, or minutes on a big extended-thinking turn), which reads as "stuck before
# the thinking block even starts". Locally the flag is on and the turn streams as it
# is produced. The Construct VM is reached over a local (Hyper-V/localhost) link where
# that per-delta message volume is a non-issue, so we want streaming on regardless of
# remoteName.
#
# WHAT. Neutralise the gate in the installed extension.js so the flag is always on:
#   includePartialMessages:!le.env.remoteName   ->   includePartialMessages:!le.env.remoteName||!0
# `(!remoteName)||true` is always true, so the original expression is preserved
# byte-for-byte (trivial, reversible) and the flag is forced on. Idempotent: a copy
# that already carries the `||!0` suffix is skipped; a build without the known gate is
# left untouched and reported. construct-partial-streaming-disable.sh undoes it.
#
# Prints a short status to stderr; a machine-readable CONSTRUCT_PARTIAL_PATCHED=<0|1>
# to stdout; exit 0 on success (best-effort, never fails provisioning).

set -u

log() { printf '%s\n' "construct-partial-streaming-enable: $*" >&2; }

ANCHOR='includePartialMessages:!le.env.remoteName'
PATCHED='includePartialMessages:!le.env.remoteName||!0'
patched_any=0
found_ext=0

apply_patch_to() {
  f="$1"
  [ -f "$f" ] || return 0
  if grep -qF "$PATCHED" "$f"; then
    log "partial-message gate already neutralised in $f (idempotent skip)."
    patched_any=1
    return 0
  fi
  if ! grep -qF "$ANCHOR" "$f"; then
    log "no known partial-message gate in $f (unrecognised build) — left untouched."
    return 0
  fi
  # Back up once (don't clobber an existing backup, so disable can always restore the
  # true original even if enable is run twice). Replace ONLY the first match via an
  # exact perl substitution over fixed, self-owned constants (no regex surprises).
  [ -f "${f}.construct-partial.bak" ] || cp -p "$f" "${f}.construct-partial.bak"
  if ANCHOR="$ANCHOR" PATCHED="$PATCHED" perl -0777 -pi -e '
      my ($a,$p)=($ENV{ANCHOR},$ENV{PATCHED});
      my $i=index($_,$a);
      if ($i>=0) { substr($_,$i,length($a))=$p; }
    ' "$f"; then
    if grep -qF "$PATCHED" "$f"; then
      log "neutralised the partial-message remoteName gate in $f."
      patched_any=1
    else
      log "WARNING: patch ran but the neutralised gate isn't present in $f."
    fi
  else
    log "WARNING: failed to patch $f."
  fi
}

for ext in "$HOME"/.vscode-server/extensions/anthropic.claude-code-* \
           "$HOME"/.vscode/extensions/anthropic.claude-code-*; do
  [ -d "$ext" ] || continue
  found_ext=1
  for cand in "$ext/dist/extension.js" "$ext/extension.js"; do
    apply_patch_to "$cand"
  done
done

if [ "$found_ext" -eq 0 ]; then
  log "no anthropic.claude-code extension found — nothing to patch here."
elif [ "$patched_any" -eq 0 ]; then
  log "found the extension but no known partial-message gate to neutralise (build may already differ)."
fi

printf 'CONSTRUCT_PARTIAL_PATCHED=%s\n' "$patched_any"
log "enable complete."
exit 0
