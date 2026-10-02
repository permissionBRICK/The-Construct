set -u
d={{dir}}
id={{id}}
# The id becomes a file name: anything but the request-id alphabet could point the
# answer outside the spool, so refuse it rather than trust the caller.
case "$id" in '' | *[!A-Za-z0-9-]*) exit 1 ;; esac
if [ "${#id}" -lt 8 ] || [ "${#id}" -gt 64 ]; then exit 1; fi
if [ ! -d "$d/responses" ]; then install -d -m 0700 -- "$d" "$d/responses" 2>/dev/null || exit 1; fi
# The answer may carry a secret value (it arrives on stdin, never in argv): owner-only
# from the first byte, and published by rename so the polling CLI never reads half of it.
umask 077
tmp="$d/responses/.tmp.$id"
if ! cat >"$tmp" 2>/dev/null || [ ! -s "$tmp" ]; then rm -f -- "$tmp"; exit 1; fi
mv -f -- "$tmp" "$d/responses/$id.json" 2>/dev/null || { rm -f -- "$tmp"; exit 1; }
exit 0
