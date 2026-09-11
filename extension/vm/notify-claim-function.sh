claim() {
  [ -d "$d" ] || return 0
  for c in "$d"/*.claimed.*; do
    [ -e "$c" ] || continue
    [ -n "$(find "$c" -maxdepth 0 -mmin +1 2>/dev/null)" ] || continue
    mv -- "$c" "${c%.claimed.*}" 2>/dev/null || rm -f -- "$c"
  done
  for f in "$d"/*.json; do
    [ -e "$f" ] || continue
    c="$f.claimed.$$"
    mv -- "$f" "$c" 2>/dev/null || continue
    head -c 8192 -- "$c" | tr -d '\r\n'
    printf '\n'
    rm -f -- "$c"
  done
}