#!/usr/bin/env bash
# construct-patch-status — read-only probe of Construct's patches on the installed
# anthropic.claude-code extension(s). It reports, per feature, one of:
#   patched  — Construct's neutralised gate is present (the feature is live)
#   stock    — the stock (un-patched) gate is present → a re-patch is needed. This
#              is what a VS Code-start auto-update / --force reinstall leaves behind:
#              a fresh extension.js with the original gate and no patch.
#   unknown  — the extension is installed but carries no gate we recognise
#   absent   — no anthropic.claude-code extension is installed for this user
#
# Two features are probed independently and reported on stdout:
#   CONSTRUCT_PARTIAL_STATUS   partial-message streaming gate (includePartialMessages)
#   CONSTRUCT_GATE_STATUS      chat-mic speech gate (remoteName)
#
# Purely observational: it never edits, backs up, or installs anything, so the host
# (src/repatch.js) can run it on every VS Code start to decide whether to re-run the
# matching enable script. The detection patterns mirror construct-partial-streaming-
# enable.sh / construct-audio-enable.sh exactly so "stock" here means "that enable
# script would patch it". Across multiple version dirs (a pending update sits beside
# the current build) "stock" wins: if ANY copy still has the un-patched gate the
# active build may be that one, so a re-patch is warranted. Exit 0 always.

set -u

# ── partial-message streaming gate (matches construct-partial-streaming-enable.sh) ──
partial_has_patched() {
  perl -0777 -ne 'exit(/includePartialMessages:\s*!\s*[A-Za-z_\$][A-Za-z0-9_\$]*\.env\.remoteName\|\|!0/ ? 0 : 1)' "$1"
}
partial_has_stock() {
  perl -0777 -ne 'exit(/includePartialMessages:\s*!\s*[A-Za-z_\$][A-Za-z0-9_\$]*\.env\.remoteName(?!\|\|!0)/ ? 0 : 1)' "$1"
}

# ── chat-mic speech gate (matches construct-audio-enable.sh constants) ──────────
GATE_ANCHOR='remoteName)return!1'          # stock (unpatched)
GATE_PATCHED='remoteName&&!1)return!1'      # neutralised

found_ext=0
partial_stock=0; partial_patched=0
gate_stock=0;    gate_patched=0

for ext in "$HOME"/.vscode-server/extensions/anthropic.claude-code-* \
           "$HOME"/.vscode/extensions/anthropic.claude-code-*; do
  [ -d "$ext" ] || continue
  found_ext=1
  # The gate lives in the extension's main file; dist/extension.js on some builds,
  # extension.js on others. Probe whichever exists (same set the enable scripts patch).
  for cand in "$ext/dist/extension.js" "$ext/extension.js"; do
    [ -f "$cand" ] || continue
    partial_has_patched "$cand" && partial_patched=1
    partial_has_stock   "$cand" && partial_stock=1
    # -F: match the gate substrings literally. The patched string embeds "&&!1" so it
    # never contains the stock anchor — a patched copy won't count as stock.
    grep -qF "$GATE_PATCHED" "$cand" && gate_patched=1
    grep -qF "$GATE_ANCHOR"  "$cand" && gate_stock=1
  done
done

# classify <found_ext> <any_stock> <any_patched> — stock outranks patched (a pending
# unpatched update beside a patched build still needs patching).
classify() {
  if [ "$1" -eq 0 ]; then printf 'absent';  return; fi
  if [ "$2" -eq 1 ]; then printf 'stock';   return; fi
  if [ "$3" -eq 1 ]; then printf 'patched'; return; fi
  printf 'unknown'
}

printf 'CONSTRUCT_PARTIAL_STATUS=%s\n' "$(classify "$found_ext" "$partial_stock" "$partial_patched")"
printf 'CONSTRUCT_GATE_STATUS=%s\n'    "$(classify "$found_ext" "$gate_stock"    "$gate_patched")"
exit 0
