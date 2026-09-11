set -u
own={{own}}
me={{me}}
cur=$(head -c 200 "$own" 2>/dev/null | tr -d '\r\n' || true)
who=${cur%% *}
if [ "$who" = "$me" ]; then rm -f "$own" 2>/dev/null || true; fi
exit 0
