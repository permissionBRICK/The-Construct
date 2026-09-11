set -u
d={{dir}}
{{claim}}
# Leave nothing behind when this watcher ends: the host closing the connection sends
# a SIGHUP (or breaks the pipe under our next write), and without this the inotifywait
# we started would linger on the VM claiming entries into a dead pipe.
iw=""
cleanup() { if [ -n "$iw" ]; then kill "$iw" 2>/dev/null || true; fi; }
trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE
last=$SECONDS
beat() {
  if [ $((SECONDS - last)) -ge {{heartbeat}} ]; then last=$SECONDS; printf '#\n'; fi
}
# inotifywait in MONITOR mode (-m), not one-shot: it keeps watching WHILE we drain, so
# an entry queued during a claim pass waits in the pipe instead of being missed. A
# wait-then-claim loop has exactly that blind spot, and the entry then sits unseen
# until some later event happens to wake us. Read through a process substitution
# rather than a pipeline so the loop runs in THIS shell (a pipeline's subshell would
# hide the watcher's pid from cleanup and survive the parent's death as an orphan).
watch_events() {
  command -v inotifywait >/dev/null 2>&1 || return 1
  [ -d "$d" ] || return 1
  exec 3< <(inotifywait -m -q -e close_write,moved_to --format '' "$d" 2>/dev/null)
  iw=$!
  while :; do
    IFS= read -r -t {{heartbeat}} -u 3 _
    rc=$?
    # >128 = read timed out (no events): beat and keep waiting.
    # non-zero and <=128 = EOF: inotifywait died; hand back to the caller.
    if [ "$rc" -ne 0 ] && [ "$rc" -le 128 ]; then break; fi
    claim
    beat
  done
  cleanup
  iw=""
  exec 3<&-
  return 0
}
claim
while :; do
  watch_events || true
  # Reached when there is no inotify (older VM), the spool dir isn't there yet, or the
  # watch ended: degrade to a slow poll ON THE VM. Still one connection, still no SSH
  # handshakes — just seconds of latency instead of milliseconds.
  sleep {{fallback}}
  claim
  beat
done
