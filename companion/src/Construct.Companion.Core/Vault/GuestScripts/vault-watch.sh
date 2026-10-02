set -u
d={{dir}}
hb={{heartbeat}}
fb={{fallback}}
# Both values end up in `read -t` and `sleep`; a malformed one would turn the loop into a
# busy spin or kill it on the first pass, so fall back to the usual cadence instead.
case "$hb" in '' | *[!0-9]* | 0) hb=60 ;; esac
case "$fb" in '' | *[!0-9]* | 0) fb=3 ;; esac
# The spool lives on tmpfs and comes back empty after every reboot. Whoever arrives first
# (this watcher or `construct secret`) creates it, 0700 all the way down: requests carry
# secrets being added to the vault and responses carry secret values, so nobody but root
# may even list them. Run once unconditionally so a directory created loosely is tightened.
install -d -m 0700 -- "$d" "$d/requests" "$d/responses" 2>/dev/null
ensure() {
  [ -d "$d/requests" ] && [ -d "$d/responses" ] && return 0
  install -d -m 0700 -- "$d" "$d/requests" "$d/responses" 2>/dev/null
}
claim() {
  ensure || return 0
  # A watcher can die between claiming a request and printing it (dropped connection, the
  # Companion quitting, the host sleeping). After a minute nobody is coming back for it:
  # put it back so the next watcher answers it instead of the CLI waiting into its deadline.
  for c in "$d"/requests/*.claimed.*; do
    [ -e "$c" ] || continue
    [ -n "$(find "$c" -maxdepth 0 -mmin +1 2>/dev/null)" ] || continue
    mv -- "$c" "${c%.claimed.*}" 2>/dev/null || rm -f -- "$c"
  done
  # The rename is the claim: exactly one watcher wins it, so a VM reached by two Companion
  # connections still raises one approval dialog per request. `.tmp.*` files are requests
  # still being written and never match *.json.
  for f in "$d"/requests/*.json; do
    [ -f "$f" ] || continue
    c="$f.claimed.$$"
    mv -- "$f" "$c" 2>/dev/null || continue
    head -c 65536 -- "$c" | tr -d '\r\n'
    printf '\n'
    rm -f -- "$c"
  done
  # Answers nobody collected (the CLI gave up or was killed) would otherwise hold secret
  # values until the next reboot; two minutes is far past any CLI still polling for one.
  # Abandoned half-written requests (a CLI killed mid-publish) go the same way.
  find "$d/responses" -maxdepth 1 -type f \( -name '*.json' -o -name '.tmp.*' \) -mmin +2 -delete 2>/dev/null
  find "$d/requests" -maxdepth 1 -type f -name '.tmp.*' -mmin +2 -delete 2>/dev/null
  return 0
}
# Leave nothing behind when this watcher ends: the host closing the connection sends
# a SIGHUP (or breaks the pipe under our next write), and without this the inotifywait
# we started would linger on the VM.
iw=""
cleanup() { if [ -n "$iw" ]; then kill "$iw" 2>/dev/null || true; fi; }
trap 'cleanup; exit 0' EXIT HUP INT TERM PIPE
last=$SECONDS
beat() {
  if [ $((SECONDS - last)) -ge "$hb" ]; then last=$SECONDS; printf '#\n'; fi
}
# inotifywait in MONITOR mode (-m): it keeps watching WHILE we drain, so a request queued
# during a claim pass waits in the pipe instead of being missed until the next heartbeat.
# Read through a process substitution rather than a pipeline so the loop runs in THIS
# shell and cleanup can see (and kill) the watcher's pid.
watch_events() {
  command -v inotifywait >/dev/null 2>&1 || return 1
  [ -d "$d/requests" ] || return 1
  exec 3< <(inotifywait -m -q -e close_write,moved_to --format '' "$d/requests" 2>/dev/null)
  iw=$!
  while :; do
    IFS= read -r -t "$hb" -u 3 _
    rc=$?
    # >128 = read timed out (no events): claim anyway (stale claims, old responses), beat.
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
  # Reached when there is no inotify on this VM or the watch ended: degrade to a slow
  # poll ON THE VM. Still one connection, just seconds of latency instead of milliseconds.
  sleep "$fb"
  claim
  beat
done
