#!/usr/bin/env bash
# construct-audio-enable — turn on mic passthrough on the Construct VM.
#
# Two idempotent, reversible steps (construct-audio-disable.sh undoes both):
#   1) install the rec/arecord shim into /usr/local/bin (so it wins over /usr/bin
#      sox on PATH) + chmod +x;
#   2) neutralise the remoteName speech gate in the installed anthropic.claude-code-*
#      extension.js so /voice + the chat mic button work over Remote-SSH.
#
# INPUT (from the host, via env vars the builder sets — see src/audio.js
# buildEnableScript; all data is base64-embedded, never interpolated into a shell):
#   CONSTRUCT_SHIM_B64        base64 of construct-rec-shim.sh (the shim contents)
#   CONSTRUCT_VM_PORT_BASE    first loopback TCP port of the tunnel range (validated int)
#   CONSTRUCT_VM_PORT_COUNT   size of the range (validated int) — each VS Code window
#                             binds ONE port of [BASE, BASE+COUNT) for its own tunnel
#
# OUTPUT (stdout, machine-readable for src/audio.js enable()):
#   CONSTRUCT_GATE_PATCHED=0/1   whether the chat-mic speech gate is neutralised
#   CONSTRUCT_PORTS_BUSY=<csv>   range ports that already have a listener (= other
#                                windows' live tunnels) so the host picks a free one
#
# Best-effort + honest: the shim install always runs; the patch is applied only to a
# copy that actually contains the known gate (unknown builds are left untouched and
# reported). Idempotent: re-running installs the same shim and skips an already-
# patched gate. Everything prints a short status to stderr; exit 0 on success.

set -u

VM_BASE="${CONSTRUCT_VM_PORT_BASE:-8767}"
VM_COUNT="${CONSTRUCT_VM_PORT_COUNT:-8}"
SHIM_B64="${CONSTRUCT_SHIM_B64:-}"
REC_PATH="/usr/local/bin/rec"
ARECORD_PATH="/usr/local/bin/arecord"

# Defensive re-validation (the host already coerced these to integers).
case "$VM_BASE" in ''|*[!0-9]*) VM_BASE=8767;; esac
case "$VM_COUNT" in ''|*[!0-9]*) VM_COUNT=8;; esac

log() { printf '%s\n' "construct-audio-enable: $*" >&2; }

# ── 1) install the shim ────────────────────────────────────────────────────────
if [ -z "$SHIM_B64" ]; then
  log "ERROR: CONSTRUCT_SHIM_B64 not provided; cannot install the recorder shim."
  exit 2
fi
mkdir -p /usr/local/bin
tmp_shim="$(mktemp)"
# Decode the shim to a temp file, then move it into place atomically. Bake the port
# in as a default so the shim works even if Claude spawns it without our env, but
# still honour an explicit CONSTRUCT_VM_PORT at run time (the shim reads the env
# first, defaulting to this baked value).
if ! printf %s "$SHIM_B64" | base64 -d > "$tmp_shim"; then
  log "ERROR: failed to decode the shim payload."
  rm -f "$tmp_shim"
  exit 2
fi
# Pin the tunnel port RANGE into the shim so a bare `rec` invocation (Claude spawns
# it with no construct env) still finds a live tunnel. We rewrite ONLY the documented
# defaults in the `:-8767` / `:-8` fallbacks; numeric values are safe to sed in
# (validated host-side + re-validated above).
if printf %s "$VM_BASE" | grep -qE '^[0-9]{1,5}$'; then
  sed -i "s/CONSTRUCT_VM_PORT_BASE:-8767/CONSTRUCT_VM_PORT_BASE:-${VM_BASE}/" "$tmp_shim" || true
fi
if printf %s "$VM_COUNT" | grep -qE '^[0-9]{1,2}$'; then
  sed -i "s/CONSTRUCT_VM_PORT_COUNT:-8/CONSTRUCT_VM_PORT_COUNT:-${VM_COUNT}/" "$tmp_shim" || true
fi
chmod 0755 "$tmp_shim"
mv -f "$tmp_shim" "$REC_PATH"
# arecord is the same shim (it self-detects nothing — every call means "stream now").
ln -sf "$REC_PATH" "$ARECORD_PATH"
log "installed recorder shim at $REC_PATH (+ arecord symlink), ports ${VM_BASE}..$((VM_BASE + VM_COUNT - 1))."

# Report which range ports already have a LISTENER — those are other windows' live
# reverse tunnels, so the host must bind a different one. `ss` only, never a test
# connection (a connect is the "start recording" signal and would blip that window's
# mic). Best-effort: no ss → empty report → the host still discovers a collision via
# ExitOnForwardFailure and tries the next port.
busy=""
if command -v ss >/dev/null 2>&1; then
  listeners="$(ss -ltn 2>/dev/null || true)"
  i=0
  while [ "$i" -lt "$VM_COUNT" ]; do
    p=$((VM_BASE + i))
    if printf '%s\n' "$listeners" | grep -qE "[:.]${p}([[:space:]]|\$)"; then
      busy="${busy:+${busy},}${p}"
    fi
    i=$((i + 1))
  done
fi
printf 'CONSTRUCT_PORTS_BUSY=%s\n' "$busy"

# Best-effort: ensure socat, the shim's most robust TCP client for binary streaming.
# The shim falls back to nc, then pure-bash /dev/tcp, so this is an optimisation, not
# a requirement — never fail enable if the install doesn't work (offline apt, etc.).
if ! command -v socat >/dev/null 2>&1 && ! command -v nc >/dev/null 2>&1; then
  if command -v apt-get >/dev/null 2>&1; then
    log "installing socat (robust TCP client for the shim; falls back to bash /dev/tcp otherwise)…"
    DEBIAN_FRONTEND=noninteractive apt-get install -y socat >/dev/null 2>&1 \
      && log "socat installed." \
      || log "socat install skipped (offline?) — shim will use the bash /dev/tcp fallback."
  fi
fi

# ── 2) neutralise the remoteName speech gate ───────────────────────────────────
# Find the installed claude-code extension(s). There can be more than one version
# directory (a pending update); patch every copy that still contains the gate so the
# active one is covered regardless of which VS Code loads.
ANCHOR='remoteName)return!1'
PATCHED='remoteName&&!1)return!1'
patched_any=0
found_ext=0

apply_patch_to() {
  f="$1"
  [ -f "$f" ] || return 0
  if grep -qF "$PATCHED" "$f"; then
    log "gate already neutralised in $f (idempotent skip)."
    patched_any=1
    return 0
  fi
  if ! grep -qF "$ANCHOR" "$f"; then
    log "no known speech gate in $f (unrecognised build) — left untouched."
    return 0
  fi
  # Back up once (don't clobber an existing backup, so disable can always restore the
  # true original even if enable is run twice). Then replace ONLY the first match via
  # a small, exact perl substitution (perl handles the literal string without regex
  # metacharacter surprises; the strings are our own fixed constants).
  [ -f "${f}.construct-audio.bak" ] || cp -p "$f" "${f}.construct-audio.bak"
  if ANCHOR="$ANCHOR" PATCHED="$PATCHED" perl -0777 -pi -e '
      my ($a,$p)=($ENV{ANCHOR},$ENV{PATCHED});
      my $i=index($_,$a);
      if ($i>=0) { substr($_,$i,length($a))=$p; }
    ' "$f"; then
    if grep -qF "$PATCHED" "$f"; then
      log "neutralised the remoteName speech gate in $f."
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
  # The gate lives in the extension's main file; dist/extension.js on some builds,
  # extension.js on others. Patch whichever exists.
  for cand in "$ext/dist/extension.js" "$ext/extension.js"; do
    apply_patch_to "$cand"
  done
done

if [ "$found_ext" -eq 0 ]; then
  log "no anthropic.claude-code extension found — shim installed; the mic button gate can't be patched here."
elif [ "$patched_any" -eq 0 ]; then
  log "found the extension but no known gate to neutralise (build may already differ)."
fi

# Machine-readable signal for the host (src/audio.js enable()): 1 iff the chat-mic
# speech gate is actually neutralised (patched now, or already-patched), 0 if left as
# is on an unknown build / missing extension. Goes to STDOUT (log() writes stderr),
# so the host can tell the user the truth instead of always claiming a patch applied.
printf 'CONSTRUCT_GATE_PATCHED=%s\n' "$patched_any"

log "enable complete."
exit 0
