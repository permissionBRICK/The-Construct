#!/usr/bin/env bash
# The Linux install script of the device agent (agent/install/install-construct-agent.sh): a local package
# lands in versions/<version>/, install.json names it current and the one before as previous, and nothing
# needs root. systemd is faked away; the start itself is not run (--no-start). The package's VS Code
# extension goes into VS Code when there is one (a fake `code`), a refusal fails the install, no VS Code is
# fine; and scripts/package-agent.py's construct.vsix holds what the extension runs, with the install scripts.
set -euo pipefail
cd "$(dirname "$0")/.."
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/pkg"
printf '#!/bin/sh\nexit 1\n' > "$work/bin/systemctl"; chmod +x "$work/bin/systemctl"
printf '#!/bin/sh\necho "$@" >> "$(dirname "$0")/../../calls"\n' > "$work/pkg/construct-agent"
mkdir -p "$work/pkg/ui"; echo '<!doctype html>' > "$work/pkg/ui/index.html"
echo 'fake extension' > "$work/pkg/construct.vsix"
(cd "$work/pkg" && python3 -c 'import sys, zipfile, os
with zipfile.ZipFile(sys.argv[1], "w") as z:
    for root, _, files in os.walk("."):
        for f in files: z.write(os.path.join(root, f))' "$work/agent.zip")
# No VS Code of this machine: PATH keeps only the directories without a `code`.
path_without_code="$(python3 -c 'import os,sys; print(os.pathsep.join(d for d in os.environ["PATH"].split(os.pathsep) if d and not os.path.exists(os.path.join(d, "code"))))')"
export HOME="$work/home" XDG_DATA_HOME="$work/home/.local/share" PATH="$work/bin:$path_without_code"
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

# VS Code present: the package's extension is installed; VS Code refusing it fails the install.
printf '#!/bin/sh\necho "$@" >> "%s/code-calls"\necho warning >&2\nexit "${CODE_EXIT:-0}"\n' "$work" > "$work/bin/code"; chmod +x "$work/bin/code"
bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --version "1.2.0+cccc" --no-start >/dev/null
[[ "$(cat "$work/code-calls")" == "--install-extension $agent/versions/1.2.0+cccc/construct.vsix --force" ]] || fail "VS Code did not get the package's extension: $(cat "$work/code-calls")"
if CODE_EXIT=1 bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --version "1.3.0+dddd" --no-start >/dev/null 2>"$work/err"; then fail "a refused extension did not fail the install"; fi
grep -q 'did not install the Construct extension' "$work/err" || fail "the failure does not say what failed"
python3 -c 'import json,sys; s=json.load(open(sys.argv[1])); assert s["current"]=="1.2.0+cccc", s' "$agent/install.json" || fail "a failed install changed install.json"
rm "$work/bin/code"

# The packager's VSIX: the extension's run-time files and the agent's install scripts, nothing of the VM's.
python3 - "$work/construct.vsix" <<'PY'
import importlib.util, json, sys, zipfile
from pathlib import Path
spec = importlib.util.spec_from_file_location('package_agent', 'scripts/package-agent.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
module.build_vsix(Path('.').resolve(), Path(sys.argv[1]))
z = zipfile.ZipFile(sys.argv[1])
names = set(z.namelist())
want = {'extension.vsixmanifest', '[Content_Types].xml', 'extension/package.json', 'extension/extension.js', 'extension/src/agent.js',
        'extension/src/page.js', 'extension/src/workspace.js', 'extension/media/icon.svg',
        'extension/install/Install-ConstructAgent.ps1', 'extension/install/install-construct-agent.sh'}
assert want <= names, want - names
assert not any(n.startswith(('extension/vm/', 'extension/test/')) or n.endswith('projects.js') for n in names), names
package = json.loads(z.read('extension/package.json'))
manifest = z.read('extension.vsixmanifest').decode()
assert f'Id="{package["name"]}" Version="{package["version"]}" Publisher="{package["publisher"]}"' in manifest, manifest
assert z.read('extension/install/install-construct-agent.sh') == Path('agent/install/install-construct-agent.sh').read_bytes()
# Every file the extension requires is in the package.
import re
for name in [n for n in names if n.endswith('.js')]:
    for dep in re.findall(r'require\("(\./[^"]+)"\)', z.read(name).decode()):
        target = str(Path(name).parent / dep).replace('\\', '/') + ('' if dep.endswith('.js') else '.js')
        assert target in names, (name, dep)
PY
if bash agent/install/install-construct-agent.sh --package "$work/agent.zip" --no-start >/dev/null 2>&1; then fail "a package without a version was installed"; fi
if bash agent/install/install-construct-agent.sh --channel http://plain.example/manifest.json --no-start >/dev/null 2>&1; then fail "a plain http channel was accepted"; fi
echo "agent-install: ok"
