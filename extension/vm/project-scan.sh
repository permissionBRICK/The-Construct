set -u
root='{{root}}'
if [ -d "$root" ]; then
  for repo in "$root"/*/; do
    [ -d "${repo}.git" ] || continue
    name=$(basename "$repo")
    url=$(git -C "$repo" remote get-url origin 2>/dev/null || true)
    branch=$(git -C "$repo" rev-parse --abbrev-ref HEAD 2>/dev/null || true)
    printf '%s\t%s\t%s\n' "$name" "$url" "$branch"
  done
fi
printf 'END\n'