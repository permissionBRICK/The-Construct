#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
work="$(mktemp -d)"
trap 'rm -r "$work"' EXIT
# Load only the storage function, never execute the installer or a host command.
eval "$(sed -n '/^ensure_media_storage() {$/,/^}$/p' service/host/install-construct-host.sh)"
DATA_DIR="$work/data" MEDIA_STORAGE=custom-media
die() { echo "$*" >&2; exit 1; }
json_field() { python3 -c 'import json,sys; print(json.load(sys.stdin).get(sys.argv[1],""))' "$1"; }
pvesh() { printf '%s' "$listing"; }
storage_json() { printf '%s' "$config"; }
pvesm() { printf '%s\n' "$*" >>"$work/calls"; }
listing='[]' config='{}'
ensure_media_storage
[[ "$(cat "$work/calls")" == "add dir custom-media --path $DATA_DIR/media --content iso" ]]
[[ -d "$DATA_DIR/media/template/iso" ]]
listing='[{"storage":"custom-media"}]'
config="$(python3 -c 'import json,sys; print(json.dumps(dict(type="dir",path=sys.argv[1],content="iso")))' "$DATA_DIR/media")"
ensure_media_storage
[[ "$(wc -l <"$work/calls")" -eq 1 ]]
config="${config/\"iso\"/\"backup\"}"
ensure_media_storage
[[ "$(tail -1 "$work/calls")" == 'set custom-media --content backup,iso' ]]
config='{"type":"dir","path":"/wrong","content":"iso"}'
if (ensure_media_storage); then echo 'accepted wrong storage path' >&2; exit 1; fi
echo 'Proxmox media storage tests passed'
