#!/usr/bin/env bash
# Installs the Construct device agent for the current user on Linux x64, without root, and starts it.
#
#   install-construct-agent.sh [--channel stable|nightly|<https manifest URL>] [--package ZIP --version X.Y.Z+sha] [--no-start]
#
# Reads the channel manifest, downloads the agentLinux package, checks its SHA-256 against the manifest,
# unpacks it into the agent's directory (versions/<version>/), records it as current in install.json and
# starts it: as the systemd user unit when systemd runs the session, else in its own session. With
# --package a local zip is installed instead (the hash check is then the caller's). The agent updates
# itself from then on (plan 12.1).
set -euo pipefail

channel=stable
package=""
version=""
start=true
repository="${CONSTRUCT_REPOSITORY:-permissionBRICK/The-Construct}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --channel) channel="$2"; shift 2 ;;
    --package) package="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --no-start) start=false; shift ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

home="${CONSTRUCT_AGENT_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/construct-agent}"
mkdir -p "$home"; chmod 700 "$home"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

if [[ -z "$package" ]]; then
  case "$channel" in
    stable) manifest_url="https://github.com/$repository/releases/latest/download/manifest.json" ;;
    nightly) manifest_url="https://github.com/$repository/releases/download/channel-nightly/manifest.json" ;;
    https://*) manifest_url="$channel" ;;
    *) echo "A channel is stable, nightly or an https manifest URL." >&2; exit 2 ;;
  esac
  curl -fsSL --proto '=https' "$manifest_url" -o "$work/manifest.json"
  # version, asset URL and SHA-256 of the agentLinux package (assets of a GitHub channel are on the release the manifest names).
  read -r version url sha < <(python3 - "$work/manifest.json" "$manifest_url" "$repository" "$channel" <<'PY'
import json, re, sys, urllib.parse
manifest = json.load(open(sys.argv[1])); url, repository, channel = sys.argv[2:5]
package = manifest['packages']['agentLinux']
asset, sha = package['asset'], package['sha256']
if not re.fullmatch(r'[0-9a-f]{64}', sha) or '/' in asset or asset in ('.', '..'):
    sys.exit('The channel manifest is invalid.')
if channel in ('stable', 'nightly'):
    where = f"https://github.com/{repository}/releases/download/{manifest['releaseTag']}/{urllib.parse.quote(asset)}"
else:
    where = urllib.parse.urljoin(url, urllib.parse.quote(asset))
print(manifest['version'], where, sha)
PY
)
  package="$work/agent.zip"
  curl -fsSL --proto '=https' "$url" -o "$package"
  echo "$sha  $package" | sha256sum -c --quiet - || { echo "The package does not match the SHA-256 in the channel manifest." >&2; exit 1; }
fi
[[ -n "$version" ]] || { echo "--package needs --version." >&2; exit 2; }
[[ "$version" =~ ^[A-Za-z0-9.+_-]+$ ]] || { echo "Unusable version: $version" >&2; exit 2; }

target="$home/versions/$version"
if [[ ! -x "$target/construct-agent" ]]; then
  rm -rf "$target.partial"; mkdir -p "$target.partial"
  python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$package" "$target.partial"
  chmod +x "$target.partial/construct-agent"
  rm -rf "$target"; mv "$target.partial" "$target"
fi

# A running agent stops first; the new one is current from here on.
if [[ -f "$home/endpoint.json" ]]; then
  read -r port token < <(python3 -c 'import json,sys; e=json.load(open(sys.argv[1])); print(e["port"], e["token"])' "$home/endpoint.json") || true
  # The token goes to curl on its standard input, never as an argument other processes could read.
  [[ -n "${port:-}" ]] && printf 'header = "Authorization: Bearer %s"\n' "$token" | curl -fsS -m 5 -X POST -K - "http://127.0.0.1:$port/api/shutdown" >/dev/null 2>&1 || true
  sleep 2
fi
previous="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("current") or "")' "$home/install.json" 2>/dev/null || true)"
python3 - "$home/install.json" "$version" "$previous" <<'PY'
import json, os, sys
path, current, previous = sys.argv[1:4]
fd = os.open(path + '.tmp', os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, 'w') as f:
    previous = previous if previous and previous != current else None
    # With a previous version the new one is on trial: its first start self-tests and goes back when it fails (12.2).
    json.dump({'current': current, 'previous': previous, 'pending': previous is not None}, f, indent=2)
os.replace(path + '.tmp', path)
PY

if command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
  "$target/construct-agent" autostart on
  if $start; then systemctl --user restart construct-agent.service; fi
elif $start; then
  setsid "$target/construct-agent" run </dev/null >/dev/null 2>&1 &
fi
echo "Installed the Construct agent $version in $target."
