set -u
d={{dir}}
iw=""
cleanup() { if [ -n "$iw" ]; then kill "$iw" 2>/dev/null || true; fi; }
trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE
last=$SECONDS
beat() {
  if [ $((SECONDS - last)) -ge {{heartbeat}} ]; then last=$SECONDS; printf '#\n'; fi
}
watch_events() {
  command -v inotifywait >/dev/null 2>&1 || return 1
  [ -d "$d/requests" ] || return 1
  [ -d "$d/close" ] || return 1
  # A process substitution, not a pipeline: the loop must run in THIS shell, or the
  # watcher's pid would be invisible to cleanup and survive us as an orphan.
  exec 3< <(inotifywait -m -q -e close_write,moved_to,delete,moved_from --format '' "$d/requests" "$d/close" 2>/dev/null)
  iw=$!
  while :; do
    IFS= read -r -t {{heartbeat}} -u 3 _
    rc=$?
    # >128 = the read timed out (no events): beat and keep waiting.
    # non-zero and <=128 = EOF: inotifywait died; hand back to the caller.
    if [ "$rc" -ne 0 ] && [ "$rc" -le 128 ]; then break; fi
    if [ "$rc" -eq 0 ]; then printf 'CHANGED\n'; fi
    beat
  done
  cleanup
  iw=""
  exec 3<&-
  return 0
}
# On connect: the host reconciles once, which is what re-opens everything still queued
# after a reboot, a reconnect or a window switch.
printf 'CHANGED\n'
while :; do
  watch_events || true
  sleep {{fallback}}
  beat
done
