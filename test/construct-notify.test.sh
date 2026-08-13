#!/usr/bin/env bash
# Regression tests for `construct notify` — the VM side of host desktop
# notifications. Run: bash test/construct-notify.test.sh
#
# The wire contract the host depends on (extension/src/notify.js):
#   * one entry per file, ONE LINE of JSON — a newline in agent text would
#     otherwise forge a second notification when the host claims the spool;
#   * entries appear atomically, so a poll never reads a half-written file;
#   * a runaway agent cannot bury the host (or fill tmpfs) with popups.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="${ROOT}/bin/construct"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

spool="${tmp}/spool"
notify() { NOTIFY_SPOOL="${spool}" bash "${CLI}" notify "$@"; }
entries() { find "${spool}" -maxdepth 1 -name '*.json' 2>/dev/null | sort; }
only_entry() { cat "$(entries | head -1)"; }
reset_spool() { rm -rf "${spool}"; }

# ── a plain notification ─────────────────────────────────────────────────────
reset_spool
notify "Build finished" --title "audiobook-pilot" >/dev/null 2>&1
ok "queues one entry" test "$(entries | wc -l)" = 1
ok "spool is created world-writable + sticky (any user may queue, none may clobber)" \
  test "$(stat -c %a "${spool}")" = 1777
ok "entry is a single line" test "$(only_entry | wc -l)" = 1
ok "entry carries the message" sh -c "only_entry() { cat \$(find '${spool}' -name '*.json' | head -1); }; only_entry | grep -q '\"body\":\"Build finished\"'"
ok "entry carries the title" grep -q '"title":"audiobook-pilot"' "$(entries | head -1)"
ok "entry defaults to info" grep -q '"level":"info"' "$(entries | head -1)"
ok "entry carries a millisecond timestamp" grep -qE '"ts":[0-9]{13}' "$(entries | head -1)"
ok "entry names its source" grep -qE '"source":"[^"]+@[^"]+"' "$(entries | head -1)"
ok "publish is atomic — no temp files left behind" test -z "$(find "${spool}" -name '.tmp.*')"

# ── hostile text cannot break the line-per-entry protocol ────────────────────
reset_spool
notify "$(printf 'line one\nline two\ttabbed')" >/dev/null 2>&1
ok "newlines/tabs collapse to spaces" test "$(only_entry | wc -l)" = 1
ok "the text survives, flattened" grep -q '"body":"line one line two tabbed"' "$(entries | head -1)"

reset_spool
notify 'quote " and backslash \ and }{' >/dev/null 2>&1
ok "quotes and backslashes are JSON-escaped" grep -q '"body":"quote \\" and backslash \\\\ and }{"' "$(entries | head -1)"
ok "the escaped entry is still valid JSON" sh -c "python3 -c 'import json,sys; json.load(open(sys.argv[1]))' '$(entries | head -1)'"

reset_spool
notify "$(printf 'a\001b\033[31mc')" >/dev/null 2>&1
# (the trailing newline is the entry terminator, so strip it before looking)
ok "control characters are stripped" \
  sh -c "! tr -d '\n' < '$(entries | head -1)' | grep -qP '[\x00-\x1f\x7f]'"

# ── levels ───────────────────────────────────────────────────────────────────
reset_spool
notify "x" --level warn >/dev/null 2>&1
ok "warn normalizes to warning" grep -q '"level":"warning"' "$(entries | head -1)"
reset_spool
notify "x" -l error >/dev/null 2>&1
ok "error is accepted" grep -q '"level":"error"' "$(entries | head -1)"
reset_spool
ok "an unknown level is rejected" sh -c "! NOTIFY_SPOOL='${spool}' bash '${CLI}' notify x --level shouting >/dev/null 2>&1"
ok "a rejected level queues nothing" test "$(entries | wc -l)" = 0

# ── stdin + argument handling ────────────────────────────────────────────────
reset_spool
printf 'from a pipe\n' | NOTIFY_SPOOL="${spool}" bash "${CLI}" notify - >/dev/null 2>&1
ok "reads the message from stdin" grep -q '"body":"from a pipe"' "$(entries | head -1)"
reset_spool
notify one two three >/dev/null 2>&1
ok "joins bare arguments into one message" grep -q '"body":"one two three"' "$(entries | head -1)"
reset_spool
notify -- --title-looking-text >/dev/null 2>&1
ok "-- ends option parsing" grep -q '"body":"--title-looking-text"' "$(entries | head -1)"
reset_spool
ok "an empty message is an error" sh -c "! NOTIFY_SPOOL='${spool}' bash '${CLI}' notify '   ' >/dev/null 2>&1"
ok "an unknown option is an error" sh -c "! NOTIFY_SPOOL='${spool}' bash '${CLI}' notify msg --shout >/dev/null 2>&1"
ok "--title without a value is an error" sh -c "! NOTIFY_SPOOL='${spool}' bash '${CLI}' notify msg --title >/dev/null 2>&1"

# ── caps + flood guard ───────────────────────────────────────────────────────
reset_spool
notify "$(head -c 4000 /dev/zero | tr '\0' 'x')" >/dev/null 2>&1
ok "an oversized body is capped" test "$(grep -o '"body":"x*"' "$(entries | head -1)" | wc -c)" -lt 500

reset_spool
mkdir -p "${spool}"
for i in $(seq 1 3); do : >"${spool}/pending-${i}.json"; done
NOTIFY_SPOOL="${spool}" NOTIFY_MAX_PENDING=3 bash "${CLI}" notify "one too many" >/dev/null 2>&1
rc=$?
ok "the flood guard reports exit 5" test "${rc}" = 5
ok "the flood guard queues nothing" test "$(entries | wc -l)" = 3

# ── the host side: streaming watcher + exactly-once across VS Code windows ───
# The host holds ONE long-lived SSH connection running extension/src/notify.js's
# watch script. These run that real script locally (the SSH transport is the only
# part not exercised) — the invariants it has to hold are the same either way.
if command -v node >/dev/null 2>&1; then
  watch="${tmp}/watch.sh"
  claim="${tmp}/claim.sh"
  node -e 'const n=require(process.argv[1]); process.stdout.write(n.buildWatchScript({dir:process.argv[2],fallbackSeconds:1}))' \
    "${ROOT}/extension/src/notify.js" "${spool}" >"${watch}"
  node -e 'const n=require(process.argv[1]); process.stdout.write(n.buildClaimScript(process.argv[2]))' \
    "${ROOT}/extension/src/notify.js" "${spool}" >"${claim}"

  # Streaming, not polling: a connected watcher emits the entry as it is queued.
  reset_spool
  mkdir -p "${spool}"
  bash "${watch}" >"${tmp}/stream.out" 2>/dev/null &
  stream_pid=$!
  sleep 1
  notify "streamed" >/dev/null 2>&1
  sleep 2
  kill "${stream_pid}" 2>/dev/null || true
  wait "${stream_pid}" 2>/dev/null || true
  ok "a connected watcher streams a new entry" grep -q '"body":"streamed"' "${tmp}/stream.out"
  # ^-anchored so this check can't match its own command line.
  ok "the watcher leaves no inotifywait behind when it is killed" \
    sh -c "! pgrep -f '^inotifywait .*${spool}' >/dev/null"

  # Several VS Code windows each hold their own watcher on the same spool. The
  # atomic claim is what makes exactly one of them show each notification — without
  # it you would get one popup per open window.
  reset_spool
  mkdir -p "${spool}"
  : >"${tmp}/pids"
  for w in 1 2 3; do
    bash "${watch}" >"${tmp}/win-${w}.out" 2>/dev/null &
    echo $! >>"${tmp}/pids"
  done
  sleep 1
  for i in $(seq 1 30); do notify "msg-${i}" >/dev/null 2>&1; done
  sleep 3
  while read -r p; do kill "${p}" 2>/dev/null || true; done <"${tmp}/pids"
  wait 2>/dev/null || true
  delivered="$(cat "${tmp}"/win-*.out | grep -c '^{' || true)"
  unique="$(cat "${tmp}"/win-*.out | grep '^{' | sort -u | wc -l)"
  ok "three windows watching at once deliver every entry" test "${unique}" = 30
  ok "no entry is delivered twice" test "${delivered}" = "${unique}"
  ok "the watchers drain the spool" test "$(entries | wc -l)" = 0

  # A watcher can die between claiming an entry and printing it (killed window, a
  # dropped connection, VM shutdown). That entry must come back, not vanish.
  reset_spool
  notify "stranded by a dead watcher" >/dev/null 2>&1
  stranded="$(entries | head -1)"
  mv "${stranded}" "${stranded}.claimed.99999"
  touch -d '5 minutes ago' "${stranded}.claimed.99999"
  sh "${claim}" >"${tmp}/recovered.out" 2>/dev/null
  ok "an entry stranded by a dead watcher is recovered" \
    grep -q '"body":"stranded by a dead watcher"' "${tmp}/recovered.out"

  # …but a claim that is still fresh belongs to a live watcher mid-drain.
  reset_spool
  notify "held by a live watcher" >/dev/null 2>&1
  held="$(entries | head -1)"
  mv "${held}" "${held}.claimed.88888"
  sh "${claim}" >"${tmp}/notstolen.out" 2>/dev/null
  ok "a fresh claim is not stolen from a live watcher" test ! -s "${tmp}/notstolen.out"
fi

# ── the CLI still routes its other commands ──────────────────────────────────
ok "help mentions notify" sh -c "bash '${CLI}' help | grep -q 'construct notify'"
ok "an unknown top-level command still fails" sh -c "! bash '${CLI}' teleport >/dev/null 2>&1"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
