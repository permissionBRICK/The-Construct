#!/usr/bin/env bash
# The Linux install script of the device agent (agent/install/install-construct-agent.sh): a local package
# lands in versions/<version>/, install.json names it current and the one before as previous, and nothing
# needs root. systemd is faked away; the start itself is not run (--no-start).
set -euo pipefail
cd "$(dirname "$0")/.."
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/pkg"
printf '#!/bin/sh\nexit 1\n' > "$work/bin/systemctl"; chmod +x "$work/bin/systemctl"
printf '#!/bin/sh\necho "$@" >> "$(dirname "$0")/../../calls"\n' > "$work/pkg/construct-agent"
mkdir -p "$work/pkg/ui"; echo '<!doctype html>' > "$work/pkg/ui/index.html"
(cd "$work/pkg" && python3 -c 'import sys, zipfile, os
with zipfile.ZipFile(sys.argv[1], "w") as z:
    for root, _, files in os.walk("."):
        for f in files: z.write(os.path.join(root, f))' "$work/agent.zip")
export HOME="$work/home" XDG_DATA_HOME="$work/home/.local/share" PATH="$work/bin:$PATH"
agent="$XDG_DATA_HOME/construct-agent"
fail() { echo "FAIL: $*" >&2; exit 1; }

bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --version "1.0.0+aaaa" --no-start >/dev/null
[[ -x "$agent/versions/1.0.0+aaaa/construct-agent" ]] || fail "the executable was not unpacked executable"
[[ -f "$agent/versions/1.0.0+aaaa/ui/index.html" ]] || fail "the UI bundle is missing"
[[ "$(stat -c %a "$agent")" == 700 ]] || fail "the agent directory is not 0700"
[[ "$(stat -c %a "$agent/install.json")" == 600 ]] || fail "install.json is not 0600"
python3 -c 'import json,sys; s=json.load(open(sys.argv[1])); assert s=={"current":"1.0.0+aaaa","previous":None,"pending":False}, s' "$agent/install.json"

bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --version "1.1.0+bbbb" --no-start >/dev/null
python3 -c 'import json,sys; s=json.load(open(sys.argv[1])); assert s=={"current":"1.1.0+bbbb","previous":"1.0.0+aaaa","pending":True}, s' "$agent/install.json"

if bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --no-start >/dev/null 2>&1; then fail "a package without a version was installed"; fi
if bash agent/install/install-construct-agent.sh --channel http://plain.example/manifest.json --no-start >/dev/null 2>&1; then fail "a plain http channel was accepted"; fi
echo "agent-install: ok"
