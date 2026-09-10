#!/usr/bin/env bash
# Incremental installation on the trusted service-managed Linux primary VM.
set -euo pipefail
src="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $EUID -eq 0 ]] || { echo 'Run as root.' >&2; exit 1; }
[[ -f /etc/construct/config.env ]] || { echo 'Construct service configuration is required.' >&2; exit 1; }
if ! dpkg-query -W -f='${Status}' python3-aiohttp 2>/dev/null | grep -qx 'install ok installed'; then
  apt-get update -qq
  apt-get install -y -qq python3-aiohttp
fi
command -v docker >/dev/null || { echo 'Install and start Docker before installing the console gateway.' >&2; exit 1; }
install -d -m 755 /opt/construct/console-viewer/static
changed=false
copy_changed() {
  if ! cmp -s "$1" "$2"; then install -m 644 "$1" "$2"; changed=true; fi
}
for file in server.py open.py; do copy_changed "$src/$file" "/opt/construct/console-viewer/$file"; done
for file in "$src"/static/*; do copy_changed "$file" "/opt/construct/console-viewer/static/$(basename "$file")"; done
image='guacamole/guacd@sha256:8974eaa9ba32f713daf311e7cc8cd7e4cdfba1edea39eed75524e78ef4b08f4f'
if ! docker image inspect "$image" >/dev/null 2>&1; then docker pull "$image"; fi
if docker container inspect construct-guacd >/dev/null 2>&1; then
  [[ "$(docker inspect --format '{{.Config.Image}}' construct-guacd)" == "$image" ]] || { echo 'construct-guacd uses another image; replace it explicitly.' >&2; exit 1; }
  [[ "$(docker inspect --format '{{index .Config.Entrypoint 0}}' construct-guacd)" == /opt/guacamole/sbin/guacd ]] || { echo 'Existing guacd entrypoint differs; replace it explicitly.' >&2; exit 1; }
  [[ "$(docker inspect --format '{{json .Config.Cmd}}' construct-guacd)" == '["-b","127.0.0.1","-f","-L","warning"]' ]] || { echo 'Existing guacd listen/log configuration differs; replace it explicitly.' >&2; exit 1; }
  if [[ "$(docker inspect --format '{{.State.Running}}' construct-guacd)" != true ]]; then
    docker start construct-guacd >/dev/null
  fi
else
  docker run -d --name construct-guacd --restart unless-stopped --network host --entrypoint /opt/guacamole/sbin/guacd "$image" \
    -b 127.0.0.1 -f -L warning
fi
if ! cmp -s "$src/construct-console-viewer.service" /etc/systemd/system/construct-console-viewer.service; then
  copy_changed "$src/construct-console-viewer.service" /etc/systemd/system/construct-console-viewer.service
  systemctl daemon-reload
fi
systemctl enable construct-console-viewer.service
if ! systemctl is-active --quiet construct-console-viewer.service; then
  systemctl start construct-console-viewer.service
elif [[ "$changed" = true ]]; then
  systemctl restart construct-console-viewer.service
fi
