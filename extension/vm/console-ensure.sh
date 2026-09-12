set -u
if [ -f /opt/construct/console-viewer/open.py ] && systemctl is-active --quiet construct-console-viewer \
   && [ "$(docker container inspect -f '{{.State.Running}}' construct-guacd 2>/dev/null)" = true ]; then
  echo CONSOLE_GATEWAY=ready; exit 0
fi
command -v docker >/dev/null 2>&1 || { echo CONSOLE_GATEWAY=no-docker; exit 4; }
[ -f /opt/construct/repo/console-viewer/install.sh ] || { echo CONSOLE_GATEWAY=missing-source; exit 3; }
log=$(mktemp)
if bash /opt/construct/repo/console-viewer/install.sh >"$log" 2>&1; then
  echo CONSOLE_GATEWAY=installed; rm -f "$log"; exit 0
fi
echo CONSOLE_GATEWAY=install-failed; tail -n 5 "$log" >&2; rm -f "$log"; exit 5
