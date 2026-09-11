set -u
root='{{root}}'
url=$(printf %s '{{url}}' | base64 -d)
dest=$(printf %s '{{dest}}' | base64 -d)
mkdir -p "$root"
target="$root/$dest"
if [ -e "$target" ]; then printf "EXISTS\t%s\n" "$target" >&2; exit 3; fi
git clone -- "$url" "$target"