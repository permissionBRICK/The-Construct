#!/usr/bin/env bash
# Run from any directory. Dependencies are documented in docs/local-checks.md.
set -euo pipefail
cd "$(dirname "$0")/.."
# A configured guest environment must not alter host-side fixtures or tests.
unset CONSTRUCT_SERVICE_URL CONSTRUCT_SERVICE_CA_FILE CONSTRUCT_EXTERNAL_HOST CONSTRUCT_EXTERNAL_SSH_PORT CONSTRUCT_INSTANCE_NAME CONSTRUCT_T3_VOICE_INPUT
usage='Usage: bash test/run-local-checks.sh [panel|t3|service|companion|release|ui|agent|guest|all | --changed [<base>]]'
group="${1:-all}"
if [[ "$group" == --changed ]]; then
  # Touched paths since the merge base with <base> (default: the first of origin/nightly,
  # origin/rework, origin/main that exists), committed or not, mapped by docs/local-checks.md.
  base="${2:-}"
  if [[ -z "$base" ]]; then
    for candidate in origin/nightly origin/rework origin/main; do
      if git rev-parse --verify --quiet "$candidate" >/dev/null; then base="$candidate"; break; fi
    done
  fi
  [[ -n "$base" ]] || { echo 'No base branch found; pass one: --changed <base>' >&2; exit 2; }
  fork="$(git merge-base "$base" HEAD)"
  groups="$( { git diff --name-only --no-renames "$fork"; git ls-files --others --exclude-standard; } | python3 test/helpers/changed-groups.py)"
  if [[ -z "$groups" ]]; then echo "No checks for the changes since $base."; exit 0; fi
  echo "Changed since $base: $(echo $groups)"
  for selected in $groups; do bash "$0" "$selected"; done
  exit 0
fi
case "$group" in panel|t3|service|companion|release|ui|agent|guest|all) ;; *) echo "$usage" >&2; exit 2 ;; esac
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
  node extension/test/lifecycle-preparation.test.js
  node extension/test/lifecycle-run.test.js
  node extension/test/backups.test.js
  pwsh -NoProfile -File test/lifecycle-run.test.ps1
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
  python3 test/t3-resume.test.py
  bash test/t3-prune.test.sh
  bash test/t3-project-register.test.sh
  bash test/t3-update.test.sh
  pwsh -NoProfile -File test/t3-desktop-handoff.test.ps1
  pwsh -NoProfile -File test/t3-update-mode.test.ps1
fi
if [[ "$group" == service || "$group" == all ]]; then
  bash test/usage-report.test.sh
  bash test/endpoint-refresh.test.sh
  bash test/construct-expose.test.sh
  python3 test/project-forwards.test.py
  bash test/construct-secret.test.sh
  bash test/construct-config.test.sh
  bash test/agent-fix.test.sh
  pwsh -NoProfile -File test/agent-fix.test.ps1
  bash test/proxmox-nested-installer.test.sh
  /usr/bin/python3 -m unittest discover -s console-viewer -p 'test_*.py' -v
  node --check console-viewer/static/viewer.js
  bash -n console-viewer/install.sh bin/construct-vm.sh bin/construct-secret.sh bin/construct-backup.sh bin/construct-config.sh bin/construct-oom-guard.sh bin/project-forwards.sh bin/provision.sh bin/fetch-construct-source.sh bin/construct-worktree-clone.sh bin/construct-agent-fix.sh service/host/xfs-cloud-image.sh service/host/install-construct-host.sh
  bash test/worktree-clone.test.sh
  bash test/codex-install.test.sh
  bash test/claude-rm-prompt.test.sh
  pwsh -NoProfile -File test/browser-console-install.test.ps1
  bash test/browser-console-provision.test.sh
  bash test/browser-console-pull.test.sh
  bash test/provision-deferred-checkout.test.sh
  bash test/docker-registry-mirrors.test.sh
  bash test/oom-guard.test.sh
  bash test/procps-guard.test.sh
  node extension/test/drivers.test.js
  node extension/test/hostadmin.test.js
  bash test/construct-vm.test.sh
  bash test/construct-backup.test.sh
  bash test/export-config.test.sh
  bash test/restore-config.test.sh
  bash test/idle-report.test.sh
  bash test/construct-project.test.sh
  pwsh -NoProfile -File test/backup-keys.test.ps1
  pwsh -NoProfile -File test/backup-actions.test.ps1
  node extension/test/t3code.test.js
  python3 test/t3-pairing-forward.test.py
  dotnet test service/Constructd.sln -c Release
  bash test/backup-e2e.test.sh
  pwsh -NoProfile -File service/tests/Constructd.Tests/Windows/windows-guest-report.test.ps1
  pwsh -NoProfile -File test/windows-license-firmware.test.ps1
  pwsh -NoProfile -File service/tests/host-updater.test.ps1
  bash service/tests/host-updater.test.sh
  bash service/tests/proxmox-media-storage.test.sh
  bash service/tests/host-installer-certificate.test.sh
fi

if [[ "$group" == companion || "$group" == all ]]; then
  dotnet build companion/Construct.Companion.sln -warnaserror
  dotnet test companion/Construct.Companion.sln --no-build
  node extension/test/parity.test.js
  bash test/vault-guest-scripts.test.sh
  for suite in extension/test/companion*.test.js; do node "$suite"; done
  pwsh -NoProfile -File test/companion-install.test.ps1
  # First-install feature selection and installer forwarding (Linux PowerShell).
  pwsh -NoProfile -File test/feature-set.test.ps1
  pwsh -NoProfile -File test/companion-entrypoints.test.ps1
  pwsh -NoProfile -File test/companion-package.test.ps1
fi

if [[ "$group" == release || "$group" == all ]]; then
  # Release tooling, channel resolution and the updater tests (docs/release-flow.md).
  python3 test/release.test.py
  python3 test/construct-release.test.py
  dotnet test service/tests/Constructd.Tests -c Release --filter 'FullyQualifiedName~Constructd.Tests.Updates'
  pwsh -NoProfile -File service/tests/host-updater.test.ps1
  bash service/tests/host-updater.test.sh
fi

if [[ "$group" == ui || "$group" == all ]]; then
  # The new client UI: unit tests (with the generated clients' drift check), the static bundle, screenshots in
  # headless Chromium, then the bundle on the real device agents and engine (fake mode) end to end.
  (cd ui && npm install --no-audit --no-fund && npm test && npm run build && npx playwright test && npm run test:e2e)
fi

if [[ "$group" == agent || "$group" == all ]]; then
  # The device agent: agents in process against the real engine (fake mode) on loopback HTTPS.
  dotnet build agent/Construct.Agent.slnx -warnaserror
  dotnet test agent/Construct.Agent.slnx --no-build
fi

# Not part of `all`: it creates a real child VM on the host and takes about half an
# hour. Run it when guest-facing paths change (docs/local-checks.md).
if [[ "$group" == guest ]]; then
  bash test/e2e/guest-rig.sh quick
fi
