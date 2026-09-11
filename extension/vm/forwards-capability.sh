set -u
d={{dir}}
if [ -d "$d" ] && [ -d "$d/requests" ] && [ -d "$d/acks" ] && [ -d "$d/close" ]; then
  printf 'SPOOL=1\n'
else
  printf 'SPOOL=0\n'
fi
exit 0
