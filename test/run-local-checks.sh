#!/usr/bin/env bash
# Run from any directory. Dependencies are documented in docs/local-checks.md.
set -euo pipefail
cd "$(dirname "$0")/.."
# A configured guest environment must not alter host-side fixtures or tests.
unset CONSTRUCT_SERVICE_URL CONSTRUCT_SERVICE_CA_FILE CONSTRUCT_EXTERNAL_HOST CONSTRUCT_EXTERNAL_SSH_PORT CONSTRUCT_INSTANCE_NAME CONSTRUCT_T3_VOICE_INPUT
group="${1:-all}"
case "$group" in panel|t3|service|companion|all) ;; *) echo 'Usage: bash test/run-local-checks.sh [panel|t3|service|companion|all]' >&2; exit 2 ;; esac
if [[ "$group" == panel || "$group" == all ]]; then
  node extension/test/guest-console.test.js
  node extension/test/console.test.js
  node extension/test/hostadmin.test.js
  node extension/test/hostadmin-config.test.js
  node extension/test/host-usage.test.js
  node extension/test/network-mode.test.js
  node extension/test/hostadmin-ui.test.js
  node extension/test/hostadmin-discovery.test.js
  node extension/test/vmpower.test.js
  node extension/test/lifecycle.test.js
  pwsh -NoProfile -File test/vm-resources.test.ps1
  pwsh -NoProfile -File test/console-local.test.ps1
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
  bash test/usage-report.test.sh
  bash test/endpoint-refresh.test.sh
  bash test/construct-expose.test.sh
  bash test/proxmox-nested-installer.test.sh
  /usr/bin/python3 -m unittest discover -s console-viewer -p 'test_*.py' -v
  node --check console-viewer/static/viewer.js
  bash -n console-viewer/install.sh bin/construct-vm.sh bin/provision.sh bin/fetch-construct-source.sh bin/construct-worktree-clone.sh service/host/xfs-cloud-image.sh service/host/install-construct-host.sh
  bash test/worktree-clone.test.sh
  pwsh -NoProfile -File test/browser-console-install.test.ps1
  bash test/browser-console-provision.test.sh
  bash test/browser-console-pull-retry.test.sh
  node extension/test/drivers.test.js
  node extension/test/hostadmin.test.js
  bash test/construct-vm.test.sh
  node extension/test/t3code.test.js
  python3 test/t3-pairing-forward.test.py
  dotnet test service/Constructd.sln -c Release
  pwsh -NoProfile -File service/tests/Constructd.Tests/Windows/windows-guest-report.test.ps1
  pwsh -NoProfile -File test/windows-license-firmware.test.ps1
  pwsh -NoProfile -File service/tests/host-updater.test.ps1
  bash service/tests/host-updater.test.sh
  bash service/tests/proxmox-media-storage.test.sh
fi

if [[ "$group" == companion || "$group" == all ]]; then
  dotnet build companion/Construct.Companion.sln -warnaserror
  dotnet test companion/Construct.Companion.sln --no-build
  node extension/test/parity.test.js
  for suite in extension/test/companion*.test.js; do node "$suite"; done
  pwsh -NoProfile -File test/companion-install.test.ps1
  # First-install feature selection and installer forwarding (Linux PowerShell).
  pwsh -NoProfile -File test/feature-set.test.ps1
  pwsh -NoProfile -File test/companion-entrypoints.test.ps1
  pwsh -NoProfile -File test/companion-package.test.ps1
fi
