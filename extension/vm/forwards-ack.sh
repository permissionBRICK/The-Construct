set -u
d={{dir}}
mkdir -p "$d" 2>/dev/null || true
tmp="$d/.tmp.$$.ack"
printf %s {{b64}} | base64 -d >"$tmp" 2>/dev/null || { rm -f "$tmp"; exit 1; }
chmod 0644 "$tmp" 2>/dev/null || true
mv -f "$tmp" "$d/{{id}}.json" || { rm -f "$tmp"; exit 1; }
