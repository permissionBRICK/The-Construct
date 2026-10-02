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
idxs=()
: >"$w/all.pat"
while IFS=$'\t' read -r idx b64 || [ -n "${idx:-}" ]; do
  b64=${b64%$'\r'}
  case "$idx" in '' | *[!0-9]*) continue ;; esac
  [ "${#idx}" -le 9 ] || continue
  idx=$((10#$idx))
  printf '%s' "$b64" | base64 -d >"$w/one" 2>/dev/null || continue
  # An empty line in a grep -f file matches everything, and a newline would split one
  # secret into shorter, noisier patterns; the host never sends either.
  [ -s "$w/one" ] || continue
  [ "$(wc -l <"$w/one")" -eq 0 ] || continue
  [ -e "$w/$idx.pat" ] || idxs+=("$idx")
  { cat "$w/one"; printf '\n'; } >>"$w/$idx.pat"
  { cat "$w/one"; printf '\n'; } >>"$w/all.pat"
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

# Pass 2: which secrets each candidate holds, so the host can tell the user which keys a
# file exposes. One secret means pass 1 already answered that.
kind() {
  # Side files first, matching vault-clean: the host scrubs the database they belong to.
  case "$1" in *-wal | *-shm | *-journal) printf 'sqlite-aux'; return ;; esac
  if [ "$(head -c 16 -- "$1" 2>/dev/null | base64)" = 'U1FMaXRlIGZvcm1hdCAzAA==' ]; then printf 'sqlite'; return; fi
  if [ "$(head -c 8192 -- "$1" 2>/dev/null | tr -dc '\000' | wc -c)" -gt 0 ]; then printf 'binary'; return; fi
  printf 'text'
}
n=0
while IFS= read -r -d '' f; do
  if [ "${#idxs[@]}" -eq 1 ]; then
    hits=${idxs[0]}
  else
    hits=""
    for i in "${idxs[@]}"; do
      grep -qF --binary-files=text -f "$w/$i.pat" -- "$f" 2>/dev/null && hits="${hits:+$hits,}$i"
    done
    [ -n "$hits" ] || continue
  fi
  size=$(stat -c %s -- "$f" 2>/dev/null) || continue
  printf 'F\t%s\t%s\t%s\t%s\n' "$hits" "$size" "$(kind "$f")" "$(printf '%s' "$f" | base64 -w0)"
  n=$((n + 1))
done <"$w/cand"
printf 'DONE\t%s\n' "$n"
exit 0
