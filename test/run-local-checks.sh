#!/usr/bin/env bash
# Run from any directory. Dependencies are documented in docs/local-checks.md.
set -euo pipefail
cd "$(dirname "$0")/.."
group="${1:-all}"
case "$group" in panel|t3|service|all) ;; *) echo 'Usage: bash test/run-local-checks.sh [panel|t3|service|all]' >&2; exit 2 ;; esac
if [[ "$group" == panel || "$group" == all ]]; then
  node extension/test/guest-console.test.js
  node extension/test/hostadmin.test.js
  node extension/test/hostadmin-ui.test.js
  node extension/test/hostadmin-discovery.test.js
  UI_SMOKE_THEME="${UI_SMOKE_THEME:-native}" node extension/test/ui-smoke.js
fi
if [[ "$group" == t3 || "$group" == all ]]; then
  python3 test/t3-install-source.test.py
  python3 test/t3-prebuilt.test.py
  python3 test/t3-build-source.test.py
  node extension/test/updates.test.js
  node extension/test/t3code.test.js
  python3 test/t3-pairing-forward.test.py
fi
if [[ "$group" == service || "$group" == all ]]; then
  /usr/bin/python3 -m unittest discover -s console-viewer -p 'test_*.py' -v
  node --check console-viewer/static/viewer.js
  bash -n console-viewer/install.sh bin/construct-vm.sh bin/provision.sh
  pwsh -NoProfile -File test/browser-console-install.test.ps1
  node extension/test/drivers.test.js
  node extension/test/hostadmin.test.js
  bash test/construct-vm.test.sh
  node extension/test/t3code.test.js
  python3 test/t3-pairing-forward.test.py
  dotnet test service/Constructd.sln -c Release
fi
