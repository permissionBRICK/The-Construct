set -u
d={{dir}}
me={{me}}
ttl={{ttl}}
lockttl={{lockTtl}}
now=$(date +%s 2>/dev/null || echo 0)
if [ ! -d "$d" ]; then printf 'OWNER=absent\n'; exit 0; fi
own="$d/.owner"
lock="$d/.owner.lock"

read_owner() {
  cur=""
  if [ -f "$own" ]; then cur=$(head -c 200 "$own" 2>/dev/null | tr -d '\r\n' || true); fi
  who=${cur%% *}
  ts=${cur#* }
  case "$ts" in ''|*[!0-9]*) ts=0 ;; esac
}

# The lease transaction runs inside a real mutex. `mkdir` is the primitive: it either
# creates the directory or fails, atomically, with no window in between — unlike a
# write-then-read-back, where two windows can each read their own value and both believe
# they won (A writes, A reads, B writes, B reads).
#
# A lock left behind by a window that died mid-transaction is removed once it is older
# than lockttl; that is safe because the transaction it guards is three filesystem
# operations long, so a live lock is never old.
claim() {
  if ! mkdir "$lock" 2>/dev/null; then
    if [ -n "$(find "$lock" -maxdepth 0 -mmin +$((lockttl / 60 + 1)) 2>/dev/null)" ]; then
      rmdir "$lock" 2>/dev/null || true
      mkdir "$lock" 2>/dev/null || return 1
    else
      # Somebody else is claiming right now. Report what the record says and try again on
      # the next reconcile — never guess, and never two owners.
      return 1
    fi
  fi
  read_owner
  if [ -z "$who" ] || [ "$who" = "$me" ] || [ $((now - ts)) -ge "$ttl" ]; then
    tmp="$d/.owner.tmp.$$"
    if printf '%s %s\n' "$me" "$now" >"$tmp" 2>/dev/null; then
      chmod 0644 "$tmp" 2>/dev/null || true
      mv -f "$tmp" "$own" 2>/dev/null || rm -f "$tmp"
    fi
  fi
  rmdir "$lock" 2>/dev/null || true
  return 0
}

claim || true
# The record is the single source of truth, read AFTER the transaction: a window that
# could not take the lock, or could not write the file, reports what is actually there.
read_owner
if [ "$who" = "$me" ]; then printf 'OWNER=self\n'; else printf 'OWNER=other\n'; fi
dump() {
  kind="$1"; sub="$2"
  [ -d "$d/$sub" ] || return 0
  for f in "$d/$sub"/*.json; do
    [ -e "$f" ] || continue
    id=$(basename "$f" .json)
    case "$id" in .*) continue ;; esac
    b=$(head -c 4096 "$f" 2>/dev/null | base64 2>/dev/null | tr -d '\n') || continue
    [ -n "$b" ] || continue
    printf '%s %s %s\n' "$kind" "$id" "$b"
  done
}
dump R requests
dump A acks
dump C close
