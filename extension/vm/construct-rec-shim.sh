#!/usr/bin/env bash
# construct-rec-shim — a stand-in for `rec`/`arecord` on the (deviceless) Construct VM.
#
# WHY — Claude Code records with the local `rec`/`arecord`. On the VM there is no
# audio hardware, so this shim is installed as /usr/local/bin/rec (and arecord),
# which wins over /usr/bin/sox on PATH. Instead of touching a device it connects to
# a loopback TCP port that the control panel reverse-forwards from the host
# (`ssh -R <port>:127.0.0.1:<hostPort>`), and streams whatever raw PCM arrives there
# to STDOUT — exactly the bytes real `rec`/`arecord` would produce for the recorder
# contract (raw PCM, signed 16-bit LE, 16 kHz, mono).
#
# CONTRACT — Claude spawns us with the recorder argv, reads raw PCM off our stdout,
# and STOPS us with SIGTERM the instant it stops recording. So: we must emit ONLY
# raw PCM on stdout (no chatter), and we must exit promptly on SIGTERM (killing the
# reader child). Our TCP *connection* is the record-window signal the host watches:
# the host arms the mic when we connect and releases it when we disconnect. We do
# NOT parse the argv (rec/arecord flags differ and Claude pins the format on both
# ends already) — every invocation just means "stream 16k/mono/S16LE now".
#
# CONFIG — CONSTRUCT_VM_PORT (loopback TCP port to read from); defaults to 8767 to
# match the panel. All diagnostics go to stderr so stdout stays pure PCM.

set -u

PORT="${CONSTRUCT_VM_PORT:-8767}"
HOSTIP="127.0.0.1"

# Reap the reader child on SIGTERM/SIGINT/SIGHUP so we die promptly and don't leave
# a dangling connection (which would keep the host mic armed). `exec` in the reader
# means the child IS the connection; killing it closes the socket → the host releases.
child=""
cleanup() {
  if [ -n "$child" ]; then
    kill -TERM "$child" 2>/dev/null || true
  fi
  exit 0
}
trap cleanup TERM INT HUP

# Stream the loopback PCM to stdout. Prefer a real client (bash's /dev/tcp needs a
# subshell that can't cleanly stream binary; socat/nc are the robust options). We
# try, in order: socat, nc (traditional/openbsd), then a bash /dev/tcp fallback.
stream_with_socat() {
  # -u = unidirectional (we only READ from the VM socket → stdout); no stdin to the
  # host is needed. STDOUT is the raw PCM sink.
  socat -u "TCP:${HOSTIP}:${PORT},connect-timeout=5" - &
  child=$!
  wait "$child"
}

stream_with_nc() {
  # nc streams the socket to stdout. -q0/-w hint at prompt close on EOF; not all nc
  # builds accept the same flags, so we keep it minimal and let the trap handle stop.
  nc "$HOSTIP" "$PORT" &
  child=$!
  wait "$child"
}

stream_with_bash_tcp() {
  # Last-resort pure-bash fallback: open the /dev/tcp socket and `exec cat` so the
  # backgrounded process IMAGE becomes `cat` holding the socket fd (3). That matters
  # for on-demand gating: when we SIGTERM this child, `cat` exits and the socket
  # closes immediately, so the host sees the disconnect and RELEASES the mic. (A
  # plain `( … ; cat )` subshell would leave `cat` as a grandchild that our kill
  # might not reach, keeping the socket — and thus the mic — half-open.)
  ( exec 3<>"/dev/tcp/${HOSTIP}/${PORT}" && exec cat <&3 ) &
  child=$!
  wait "$child"
}

if command -v socat >/dev/null 2>&1; then
  stream_with_socat
elif command -v nc >/dev/null 2>&1; then
  stream_with_nc
else
  stream_with_bash_tcp
fi

# `wait` returns the child's status; a clean close (host released the mic / EOF) is
# success. On SIGTERM the trap already exited 0. Anything else (couldn't connect —
# passthrough not enabled / tunnel down) is a non-zero exit, which makes Claude fall
# back to the native module / report no audio rather than hang on empty stdout.
exit $?
