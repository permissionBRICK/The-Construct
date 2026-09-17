#!/usr/bin/env bash
# Refresh a service-managed guest's advertised endpoint at boot. Tokens stay in a header file.
set -euo pipefail
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
VM_TOKEN_FILE="${CONSTRUCT_VM_TOKEN_FILE:-/etc/construct/vm-token}"
CURL="${CONSTRUCT_CURL:-curl}"

cfg() {
  python3 - "${CONFIG_FILE}" "$1" <<'PY'
import pathlib, shlex, sys
path = pathlib.Path(sys.argv[1])
for line in path.read_text().splitlines() if path.exists() else []:
    if line.startswith(sys.argv[2] + '='):
        parts = shlex.split(line.split('=', 1)[1])
        print(parts[0] if len(parts) == 1 else '')
        break
PY
}

service="${CONSTRUCT_SERVICE_URL:-$(cfg CONSTRUCT_SERVICE_URL)}"
[[ -n "${service}" ]] || exit 0
case "${service}" in
  https://*|http://localhost:*|http://127.0.0.1:*) ;;
  *) echo 'construct-endpoint-refresh: the service URL requires HTTPS' >&2; exit 1 ;;
esac
instance="${CONSTRUCT_INSTANCE_NAME:-$(cfg CONSTRUCT_INSTANCE_NAME)}"
[[ "${instance}" =~ ^[A-Za-z0-9][A-Za-z0-9_-]*$ ]] || { echo 'construct-endpoint-refresh: invalid instance name' >&2; exit 1; }
ca="${CONSTRUCT_SERVICE_CA_FILE:-$(cfg CONSTRUCT_SERVICE_CA_FILE)}"
token="$(head -n 1 "${VM_TOKEN_FILE}" | tr -d '\r\n')"
[[ -n "${token}" ]] || exit 1
work="$(mktemp -d)"
trap 'rm -r "${work}"' EXIT
(umask 077; printf 'Authorization: VmToken %s\n' "${token}" >"${work}/headers")
unset token
args=(--silent --show-error --fail --max-time 20 -H "@${work}/headers" -H 'Accept: application/json' -o "${work}/endpoint")
[[ -z "${ca}" ]] || args+=(--cacert "${ca}")
"${CURL}" "${args[@]}" "${service%/}/api/v1/vms/${instance}/endpoint"

python3 - "${CONFIG_FILE}" "${work}/endpoint" "${work}/host" <<'PY'
import json, os, pathlib, re, shlex, sys, tempfile
config, response, hostfile = map(pathlib.Path, sys.argv[1:])
endpoint = json.loads(response.read_text())
# publicHost is the web/prompt identity on relayed hosts with PublicHostPattern.
host = endpoint.get('publicHost') or endpoint.get('sshHost')
port = endpoint.get('sshPort')
if not isinstance(host, str) or not re.fullmatch(r'[A-Za-z0-9_.:-]+', host) or not isinstance(port, int) or isinstance(port, bool) or not 1 <= port <= 65535:
    raise SystemExit('construct-endpoint-refresh: invalid endpoint response')
updates = {'CONSTRUCT_EXTERNAL_HOST': host, 'CONSTRUCT_EXTERNAL_SSH_PORT': str(port)}
original = config.read_text()
lines, seen = [], set()
for line in original.splitlines():
    key = line.split('=', 1)[0]
    if key in updates:
        if key in seen:
            continue
        seen.add(key)
        old = shlex.split(line.split('=', 1)[1])
        if old != [updates[key]]:
            line = key + '=' + shlex.quote(updates[key])
    lines.append(line)
lines += [key + '=' + shlex.quote(value) for key, value in updates.items() if key not in seen]
updated = '\n'.join(lines) + '\n'
if updated != original:
    fd, name = tempfile.mkstemp(prefix='.endpoint-', dir=config.parent)
    try:
        stat = config.stat()
        os.fchmod(fd, stat.st_mode & 0o777)
        os.fchown(fd, stat.st_uid, stat.st_gid)
        with os.fdopen(fd, 'w') as stream:
            stream.write(updated)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, config)
    finally:
        if os.path.exists(name): os.unlink(name)
hostfile.write_text(host)
PY
# Render on every successful refresh, so a failed render is retried even after the config write.
CONSTRUCT_EXTERNAL_HOST="$(cat "${work}/host")" "${CONSTRUCT_CLI:-construct}" systemprompt
