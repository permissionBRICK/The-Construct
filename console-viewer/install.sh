#!/usr/bin/env bash
# Idempotent opt-in installation on the trusted Linux primary VM.
set -euo pipefail
src="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ $EUID -eq 0 ]] || { echo 'Run as root.' >&2; exit 1; }
[[ -f /etc/construct/config.env ]] || { echo 'Construct service configuration is required.' >&2; exit 1; }
apt-get update -qq
apt-get install -y -qq python3-aiohttp
command -v docker >/dev/null || { echo 'Install and start Docker before installing the console gateway.' >&2; exit 1; }
install -d -m 755 /opt/construct/console-viewer/static
install -m 644 "$src/server.py" "$src/open.py" /opt/construct/console-viewer/
install -m 644 "$src"/static/* /opt/construct/console-viewer/static/
image='guacamole/guacd@sha256:8974eaa9ba32f713daf311e7cc8cd7e4cdfba1edea39eed75524e78ef4b08f4f'
docker pull "$image"
if docker container inspect construct-guacd >/dev/null 2>&1; then
  [[ "$(docker inspect --format '{{.Config.Image}}' construct-guacd)" == "$image" ]] || { echo 'construct-guacd uses another image; replace it explicitly.' >&2; exit 1; }
  [[ "$(docker inspect --format '{{index .Config.Entrypoint 0}}' construct-guacd)" == /opt/guacamole/sbin/guacd ]] || { echo 'Existing guacd entrypoint differs; replace it explicitly.' >&2; exit 1; }
  [[ "$(docker inspect --format '{{json .Config.Cmd}}' construct-guacd)" == '["-b","127.0.0.1","-f","-L","warning"]' ]] || { echo 'Existing guacd listen/log configuration differs; replace it explicitly.' >&2; exit 1; }
  docker start construct-guacd >/dev/null
else
  docker run -d --name construct-guacd --restart unless-stopped --network host --entrypoint /opt/guacamole/sbin/guacd "$image" \
    -b 127.0.0.1 -f -L warning
fi
install -m 644 "$src/construct-console-viewer.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now construct-console-viewer.service
systemctl restart construct-console-viewer.service
