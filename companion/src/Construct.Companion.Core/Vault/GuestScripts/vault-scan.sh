set -u
# Byte semantics everywhere: secrets are matched as raw bytes, and a UTF-8 locale would make
# grep skip "binary" lines or choke on invalid sequences in logs and databases.
export LC_ALL=C
d={{dir}}
max={{maxSize}}
roots=({{roots}})
case "$max" in '' | *[!0-9]*) printf 'vault-scan: invalid size limit\n' >&2; exit 1 ;; esac
# The patterns ARE the secrets. They arrive on stdin and only ever live in files inside a
# private directory on the spool's tmpfs: never in argv (visible to every process via ps
# and /proc), never in the environment, never on a disk that outlives the scan.
[ -d "$d" ] || install -d -m 0700 -- "$d" 2>/dev/null
umask 077
w=$(mktemp -d "$d/scan.XXXXXX" 2>/dev/null) || { printf 'vault-scan: cannot create a work directory\n' >&2; exit 1; }
trap 'rm -rf -- "$w"' EXIT
trap 'exit 1' HUP INT TERM PIPE
# A full sweep of the home directories must not make the VM's agents stutter: everything
# below (find, every grep, stat) inherits the lowest CPU and idle I/O priority.
renice -n 19 -p "$$" >/dev/null 2>&1 || true
ionice -c3 -p "$$" >/dev/null 2>&1 || true

# stdin: "<index>\t<base64 pattern>" lines; several lines may share an index (variants of
# one secret: raw, JSON-escaped, ...). Decoded straight into the pattern files.
# A multi-line pattern (a private key) must match whole, and grep only matches within a line.
# So it is kept apart as <index>.ml.<n>, and pass 1 searches for one of its lines (the anchor)
# to find candidates. The last of its longest lines: the first lines of a key are the same in
# every key of its type, the later ones hold key material.
idxs=()
ml=0
: >"$w/all.pat"
while IFS=$'\t' read -r idx b64 || [ -n "${idx:-}" ]; do
  b64=${b64%$'\r'}
  case "$idx" in '' | *[!0-9]*) continue ;; esac
  [ "${#idx}" -le 9 ] || continue
  idx=$((10#$idx))
  printf '%s' "$b64" | base64 -d >"$w/one" 2>/dev/null || continue
  # An empty line in a grep -f file matches everything; the host never sends one.
  [ -s "$w/one" ] || continue
  [ -e "$w/$idx.pat" ] || [ -e "$w/$idx.ml.0" ] || idxs+=("$idx")
  if [ "$(wc -l <"$w/one")" -eq 0 ]; then
    { cat "$w/one"; printf '\n'; } >>"$w/$idx.pat"
    { cat "$w/one"; printf '\n'; } >>"$w/all.pat"
    continue
  fi
  anchor=$(awk '{ sub(/\r$/, "") } length($0) >= length(a) { a = $0 } END { printf "%s", a }' "$w/one")
  [ -n "$anchor" ] || continue
  n=0
  while [ -e "$w/$idx.ml.$n" ]; do n=$((n + 1)); done
  mv -- "$w/one" "$w/$idx.ml.$n"
  printf '%s\n' "$anchor" >>"$w/all.pat"
  ml=1
done
rm -f -- "$w/one"

existing=()
for r in "${roots[@]}"; do
  case "$r" in /*) ;; *) continue ;; esac
  [ -d "$r" ] && existing+=("$r")
done
if [ "${#idxs[@]}" -eq 0 ] || [ "${#existing[@]}" -eq 0 ]; then printf 'DONE\t0\n'; exit 0; fi
mapfile -t idxs < <(printf '%s\n' "${idxs[@]}" | sort -n)

# Pass 1: which files contain ANY of the secrets. Prune what is huge, vendored or rebuilt
# from the network anyway (a secret there is not something an agent wrote), and the spool
# itself: our own pattern files would match. Agent transcripts are scanned at any size —
# they are exactly where a pasted secret ends up.
find -H "${existing[@]}" -xdev \
  \( -type d \( -name node_modules -o -name __pycache__ -o -name .cache \
  -o -path '*/.git/objects' -o -path '*/.git/lfs' -o -path '*/.nuget/packages' \
  -o -path '*/.cargo/registry' -o -path '*/.rustup' -o -path '*/go/pkg/mod' \
  -o -path '*/.gradle/caches' -o -path '*/.m2/repository' -o -path '*/.t3/caches' \
  -o -path '*/.vscode-server/bin' -o -path '*/.vscode-server/extensions' \
  -o -path '*/.local/share/opencode/snapshot' -o -samefile "$d" \
  -o \( -name target -exec test -e '{}/CACHEDIR.TAG' \; \) \) -prune \) \
  -o -type f \( -size "-$((max + 1))c" -o -path '*/.claude/*' -o -path '*/.codex/*' \
  -o -path '*/.local/share/opencode/*' -o -path '*/.t3/userdata/*' \) -print0 2>/dev/null |
  xargs -0 -r grep -lZF --binary-files=text -f "$w/all.pat" -- 2>/dev/null |
  sort -z -u >"$w/cand"

# Whether file $2 holds the multi-line pattern in file $1 as one unbroken run: the pattern's
# first line ends a line of the file, its middle lines are whole lines, its last line starts
# the line after them. The same test as a byte search for the whole pattern, but streamed line
# by line, so a large log is never read into memory. at[] holds how many pattern lines each
# run that is still possible has matched so far.
whole() {
  awk 'NR == FNR { p[++n] = $0; next }
    {
      m = 0
      for (j = 1; j <= k; j++) {
        s = at[j] + 1
        if (s < n) { if ($0 == p[s]) nx[++m] = s }
        else if (substr($0, 1, length(p[n])) == p[n]) { found = 1; exit }
      }
      if (length($0) >= length(p[1]) && substr($0, length($0) - length(p[1]) + 1) == p[1]) nx[++m] = 1
      k = m
      for (j = 1; j <= k; j++) at[j] = nx[j]
    }
    END { exit !found }' "$1" "$2"
}
holds() {
  local i=$1 f=$2 m
  if [ -s "$w/$i.pat" ] && grep -qF --binary-files=text -f "$w/$i.pat" -- "$f" 2>/dev/null; then return 0; fi
  for m in "$w/$i".ml.*; do
    [ -e "$m" ] && whole "$m" "$f" 2>/dev/null && return 0
  done
  return 1
}

# Pass 2: which secrets each candidate holds, so the host can tell the user which keys a
# file exposes. One secret without multi-line patterns means pass 1 already answered that;
# an anchor alone proves nothing.
kind() {
  # Side files first, matching vault-clean: the host scrubs the database they belong to.
  case "$1" in *-wal | *-shm | *-journal) printf 'sqlite-aux'; return ;; esac
  if [ "$(head -c 16 -- "$1" 2>/dev/null | base64)" = 'U1FMaXRlIGZvcm1hdCAzAA==' ]; then printf 'sqlite'; return; fi
  if [ "$(head -c 8192 -- "$1" 2>/dev/null | tr -dc '\000' | wc -c)" -gt 0 ]; then printf 'binary'; return; fi
  printf 'text'
}
n=0
while IFS= read -r -d '' f; do
  if [ "${#idxs[@]}" -eq 1 ] && [ "$ml" -eq 0 ]; then
    hits=${idxs[0]}
  else
    hits=""
    for i in "${idxs[@]}"; do
      holds "$i" "$f" && hits="${hits:+$hits,}$i"
    done
    [ -n "$hits" ] || continue
  fi
  size=$(stat -c %s -- "$f" 2>/dev/null) || continue
  printf 'F\t%s\t%s\t%s\t%s\n' "$hits" "$size" "$(kind "$f")" "$(printf '%s' "$f" | base64 -w0)"
  n=$((n + 1))
done <"$w/cand"
printf 'DONE\t%s\n' "$n"
exit 0
