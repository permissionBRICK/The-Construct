#!/usr/bin/env bash
# Regression tests for construct-patch-status.sh — the read-only probe the control
# panel runs on VS Code start to decide whether a Construct patch needs re-applying
# after a background claude-code auto-update.
#
# The core guarantee: the probe's "stock" verdict must agree with what the enable
# scripts actually do — stock ⇔ "the enable script would patch this build". So we run
# the probe against fixtures AND cross-check by running the real enable scripts.
# Run: bash test/patch-status.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STATUS="${ROOT}/extension/vm/construct-patch-status.sh"
PARTIAL_ENABLE="${ROOT}/extension/vm/construct-partial-streaming-enable.sh"
AUDIO_ENABLE="${ROOT}/extension/vm/construct-audio-enable.sh"

pass=0
fail=0
ok() {
  name="$1"; shift
  if "$@"; then pass=$((pass + 1)); printf '  PASS  %s\n' "$name"
  else fail=$((fail + 1)); printf '  FAIL  %s\n' "$name"; fi
}

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# Write an extension.js with the given body under a throwaway HOME; echo the HOME.
make_home() {
  body="$1"
  h="$(mktemp -d "${tmp}/home.XXXXXX")"
  ext="${h}/.vscode-server/extensions/anthropic.claude-code-9.9.9-linux-x64/dist"
  mkdir -p "$ext"
  printf '%s' "$body" >"${ext}/extension.js"
  printf '%s\n' "$h"
}

# Read one status value (partial|gate) from the probe run under HOME=$1.
status_of() {
  home="$1"; key="$2"
  HOME="$home" bash "$STATUS" 2>/dev/null | sed -n "s/^${key}=//p"
}

# A stock build with both gates present.
STOCK='a={includePartialMessages:!le.env.remoteName,x:1};function f(){if(t.env.remoteName)return!1;}'
# Both gates neutralised the way the enable scripts leave them.
PATCHED='a={includePartialMessages:!le.env.remoteName||!0,x:1};function f(){if(t.env.remoteName&&!1)return!1;}'

# ── stock detection ─────────────────────────────────────────────────────────────
h="$(make_home "$STOCK")"
ok "stock: partial reported stock" test "$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)" = "stock"
ok "stock: gate reported stock"    test "$(status_of "$h" CONSTRUCT_GATE_STATUS)" = "stock"

# ── patched detection ───────────────────────────────────────────────────────────
h="$(make_home "$PATCHED")"
ok "patched: partial reported patched" test "$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)" = "patched"
ok "patched: gate reported patched"    test "$(status_of "$h" CONSTRUCT_GATE_STATUS)" = "patched"

# ── unknown build (extension present, no gate) ──────────────────────────────────
h="$(make_home 'a={foo:1};console.log("nothing to patch");')"
ok "unknown: partial reported unknown" test "$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)" = "unknown"
ok "unknown: gate reported unknown"    test "$(status_of "$h" CONSTRUCT_GATE_STATUS)" = "unknown"

# ── absent (no extension installed) ─────────────────────────────────────────────
h="$(mktemp -d "${tmp}/home.XXXXXX")"
ok "absent: partial reported absent" test "$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)" = "absent"
ok "absent: gate reported absent"    test "$(status_of "$h" CONSTRUCT_GATE_STATUS)" = "absent"

# ── cross-check: probe's stock verdict ⇔ enable script actually patches ─────────
# Start stock → probe says stock → run BOTH enable scripts → probe now says patched.
h="$(make_home "$STOCK")"
before_partial="$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)"
before_gate="$(status_of "$h" CONSTRUCT_GATE_STATUS)"
HOME="$h" bash "$PARTIAL_ENABLE" >/dev/null 2>&1
# The audio enable needs a shim payload; feed it the real shim so the gate patch runs.
CONSTRUCT_SHIM_B64="$(base64 -w0 "${ROOT}/extension/vm/construct-rec-shim.sh" 2>/dev/null)" \
  HOME="$h" bash "$AUDIO_ENABLE" >/dev/null 2>&1 || true
after_partial="$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)"
after_gate="$(status_of "$h" CONSTRUCT_GATE_STATUS)"
ok "crosscheck: partial stock->patched after enable" test "$before_partial" = "stock" -a "$after_partial" = "patched"
ok "crosscheck: gate stock->patched after enable"    test "$before_gate" = "stock" -a "$after_gate" = "patched"

# ── multi-version: a pending unpatched update beside a patched build wins as stock ─
h="$(make_home "$PATCHED")"
newext="${h}/.vscode-server/extensions/anthropic.claude-code-9.9.10-linux-x64/dist"
mkdir -p "$newext"
printf '%s' "$STOCK" >"${newext}/extension.js"
ok "multi: partial stock wins over patched sibling" test "$(status_of "$h" CONSTRUCT_PARTIAL_STATUS)" = "stock"
ok "multi: gate stock wins over patched sibling"    test "$(status_of "$h" CONSTRUCT_GATE_STATUS)" = "stock"

printf '\n  patch-status probe tests — %s/%s passed\n\n' "$pass" "$((pass + fail))"
test "$fail" -eq 0
