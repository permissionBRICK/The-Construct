#!/usr/bin/env bash
# Run from any directory. Dependencies, the groups and the quick tier are documented in docs/local-checks.md.
set -euo pipefail
cd "$(dirname "$0")/.."
# Preserve explicit real-guest E2E SSH settings before isolating every other CONSTRUCT_* variable.
e2e_ssh_env=()
for var in CONSTRUCT_E2E_SSH_HOST CONSTRUCT_E2E_SSH_PORT CONSTRUCT_E2E_SSH_USER CONSTRUCT_E2E_SSH_KEY CONSTRUCT_E2E_SSH_PASSWORD; do
  if [[ -n "${!var:-}" ]]; then e2e_ssh_env+=("$var=${!var}"); fi
done
# A configured guest environment must not alter host-side fixtures or tests.
. test/helpers/isolate-env.sh
usage='Usage: bash test/run-local-checks.sh <group>... | --changed [<base>] | --quick [<base>]
Groups: panel t3 service engine release ui agent e2e guest, or all (every group but guest).
engine:<names> and agent:<names> run only the test classes whose full names contain one of the
comma-separated <names>, for instance engine:Vault,BackupApiTests or agent:Upgrade.'

# ---- the groups to run ----------------------------------------------------------------------------
quick=''
from_changed=''
base=''
changed=()
selected=()
declare -A filter=()
if [[ "${1:-}" == --changed || "${1:-}" == --quick ]]; then
  from_changed=1
  [[ "$1" == --quick ]] && quick=1
  # Touched paths since the merge base with <base> (default: the first of origin/nightly, origin/rework,
  # origin/main that exists), committed or not, mapped to groups by the table in docs/local-checks.md.
  base="${2:-}"
  if [[ -z "$base" ]]; then
    for candidate in origin/nightly origin/rework origin/main; do
      if git rev-parse --verify --quiet "$candidate" >/dev/null; then base="$candidate"; break; fi
    done
  fi
  [[ -n "$base" ]] || { echo 'No base branch found; pass one: --changed <base>' >&2; exit 2; }
  fork="$(git merge-base "$base" HEAD)"
  changed_diff="$(git diff --name-only --no-renames "$fork")"
  changed_untracked="$(git ls-files --others --exclude-standard)"
  changed_all="$(printf '%s\n%s\n' "$changed_diff" "$changed_untracked" | sed '/^$/d' | sort -u)"
  if [[ -n "$changed_all" ]]; then mapfile -t changed <<< "$changed_all"; fi
  selected_raw="$(printf '%s\n' "${changed[@]}" | python3 test/helpers/changed-groups.py)"
  if [[ -n "$selected_raw" ]]; then mapfile -t selected <<< "$selected_raw"; fi
  if [[ ${#selected[@]} -eq 0 ]]; then echo "No checks for the changes since $base."; exit 0; fi
  echo "Changed since $base: ${selected[*]}${quick:+ (quick)}"
  if [[ -n "$quick" ]]; then
    # The quick tier: no end-to-end tests, and of the .NET suites only the test classes the changes reach.
    [[ " ${selected[*]} " == *" all "* ]] && selected=(panel t3 service engine release ui agent)
    filtered_selected="$(printf '%s\n' "${selected[@]}" | grep -vx e2e || true)"
    selected=()
    if [[ -n "$filtered_selected" ]]; then mapfile -t selected <<< "$filtered_selected"; fi
    for suite in engine agent; do
      areas="$(printf '%s\n' "${changed[@]}" | python3 test/helpers/test-areas.py "$suite")"
      case "$areas" in all) ;; '') filter[$suite]=none ;; *) filter[$suite]="$areas" ;; esac
    done
  fi
else
  [[ $# -gt 0 ]] || set -- all
  for argument in "$@"; do
    name="${argument%%:*}"
    case "$name" in panel|t3|service|engine|release|ui|agent|e2e|guest|all) ;; *) echo "$usage" >&2; exit 2 ;; esac
    if [[ "$argument" == *:* ]]; then
      [[ "$name" == engine || "$name" == agent ]] || { echo "$usage" >&2; exit 2; }
      IFS=, read -ra names <<< "${argument#*:}"
      filter[$name]="$(printf 'FullyQualifiedName~%s|' "${names[@]}")"
      filter[$name]="${filter[$name]%|}"
    fi
    selected+=("$name")
  done
fi
if [[ " ${selected[*]} " == *" all "* ]]; then
  if [[ -n "$from_changed" ]]; then
    # --changed never includes e2e (only explicit `all` or `e2e` runs it).
    selected+=(panel t3 service engine release ui agent)
  else
    selected+=(panel t3 service engine release ui agent e2e)
  fi
fi
runs() { [[ " ${selected[*]} " == *" $1 "* ]]; }

# ---- steps: each one is timed, a failure is counted and the run goes on ----------------------------------
# The script tests (scripted=1) run CHECKS_JOBS at a time (default: half the cores; 1 runs them one by one),
# each into its own log that is printed, in order, once it ends. The builds and the .NET, npm and Playwright
# runs use every core themselves and run alone.
jobs="${CHECKS_JOBS:-$(( $(nproc) / 2 ))}"
[[ "$jobs" =~ ^[0-9]+$ && "$jobs" -ge 1 ]] || jobs=1
logs="$(mktemp -d)"
summary=()
commands=()
pids=()
cleanup() {
  local p
  for p in "${pids[@]}"; do
    kill -- "-$p" 2>/dev/null || kill "$p" 2>/dev/null || true
  done
  for p in "${pids[@]}"; do
    wait "$p" 2>/dev/null || true
  done
  rm -rf "$logs"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
failures=0
declare -A ran=()
step() {
  local command="$*"
  # A step two groups share (a build, the host updater's tests) runs once per invocation.
  if [[ -n "${ran[$command]:-}" ]]; then return 0; fi
  ran[$command]=1
  local n=${#commands[@]}
  commands[n]="$command"
  if [[ -n "$quick" && -n "${scripted:-}" ]] && ! reached "$@"; then
    summary[n]="$(printf '%6s  %-8s %s' - skipped "$command")"
    return 0
  fi
  if [[ -n "${scripted:-}" && $jobs -gt 1 ]]; then
    while [[ $(jobs -rp | wc -l) -ge $jobs ]]; do wait -n || true; done
    setsid bash -c 'log="$1"; shift; started=$SECONDS; "$@" >"$log" 2>&1 </dev/null && status=0 || status=$?; echo $((SECONDS - started)) >"$log.time"; exit "$status"' \
      _ "$logs/$n" "$@" &
    pids[n]=$!
    return 0
  fi
  finish
  printf '\n== %s\n' "$command"
  local started=$SECONDS status=0
  "$@" || status=$?
  record "$n" "$status" $((SECONDS - started))
}
# Waits for the steps running in the background and prints them in the order they started.
finish() {
  local n status
  for n in "${!pids[@]}"; do
    status=0
    wait "${pids[n]}" || status=$?
    printf '\n== %s\n' "${commands[n]}"
    cat "$logs/$n"
    record "$n" "$status" "$(cat "$logs/$n.time" 2>/dev/null || echo '?')"
  done
  pids=()
}
record() { # <step> <exit status> <seconds>
  local result=ok
  if [[ $2 -ne 0 ]]; then result="FAIL $2"; failures=$((failures + 1)); fi
  summary[$1]="$(printf '%5ss  %-8s %s' "$3" "$result" "${commands[$1]}")"
}
# The quick tier runs a script's test only when a touched path is that test, lies in a directory it names, or
# is named in it by its file name (bin/construct-vm.sh reaches test/construct-vm.test.sh).
reached() {
  local argument path
  for argument in "$@"; do
    [[ -e "$argument" ]] || continue
    for path in "${changed[@]}"; do
      [[ "$path" == "$argument" || "$path" == "${argument%/}/"* ]] && return 0
      [[ -f "$argument" ]] && grep -qF -- "$(basename "$path")" "$argument" && return 0
    done
  done
  return 1
}
in_ui() { (cd ui && "$@"); }
dotnet_filter() { # <group> <always>: the --filter of a .NET suite
  local own="${filter[$1]:-}"
  if [[ -n "$own" ]]; then printf '(%s)&%s' "$own" "$2"; else printf '%s' "$2"; fi
}

# ---- the groups ----------------------------------------------------------------------------------------------
scripted=1
if runs panel; then
  # The VS Code extension (a window onto the device agent) without VS Code, and the profile validator the VM loads from it.
  step node --test extension/test/agent.test.js extension/test/page.test.js extension/test/workspace.test.js extension/test/extension.test.js
  step node extension/test/projects.test.js
  step node extension/test/project-set.test.js
fi
if runs t3; then
  step python3 test/t3-install-source.test.py
  step python3 test/t3-prebuilt.test.py
  step python3 test/t3-build-source.test.py
  step python3 test/t3-pairing-forward.test.py
  step python3 test/t3-resume.test.py
  step bash test/t3-prune.test.sh
  step bash test/t3-project-register.test.sh
  step bash test/t3-update.test.sh
  step bash test/t3-https.test.sh
  step bash test/t3-patch-shims.test.sh
fi
if runs service; then
  # The guest's scripts and CLI, provisioning, the console viewer, the host installer and updater, the Hyper-V
  # driver's PowerShell. The engine's own .NET tests are the engine group.
  step bash test/usage-report.test.sh
  step bash test/endpoint-refresh.test.sh
  step bash test/construct-expose.test.sh
  step bash test/isolate-env.test.sh
  step python3 test/project-forwards.test.py
  step bash test/construct-secret.test.sh
  step bash test/construct-config.test.sh
  step bash test/agent-fix.test.sh
  step bash test/proxmox-nested-installer.test.sh
  step /usr/bin/python3 -m unittest discover -s console-viewer -p 'test_*.py'
  step node --check console-viewer/static/viewer.js
  step bash -n console-viewer/install.sh bin/construct-vm.sh bin/construct-secret.sh bin/construct-backup.sh bin/construct-config.sh bin/construct-update.sh bin/construct-oom-guard.sh bin/setup-swap.sh bin/project-forwards.sh bin/provision.sh bin/fetch-construct-source.sh bin/construct-worktree-clone.sh bin/construct-agent-fix.sh service/host/xfs-cloud-image.sh service/host/install-construct-host.sh
  step bash test/worktree-clone.test.sh
  step bash test/codex-install.test.sh
  step bash test/coding-agents.test.sh
  step node --no-warnings test/opencode-background.test.mjs
  step bash test/claude-rm-prompt.test.sh
  step pwsh -NoProfile -File test/browser-console-install.test.ps1
  step bash test/browser-console-provision.test.sh
  step bash test/browser-console-pull.test.sh
  step bash test/provision-deferred-checkout.test.sh
  step bash test/install-sdks.test.sh
  step bash test/apt-wait.test.sh
  step bash test/apt-primary-archive.test.sh
  step bash test/docker-registry-mirrors.test.sh
  step bash test/oom-guard.test.sh
  step bash test/setup-swap.test.sh
  step bash test/procps-guard.test.sh
  step bash test/construct-vm.test.sh
  step bash test/construct-backup.test.sh
  step bash test/export-config.test.sh
  step bash test/restore-config.test.sh
  step bash test/idle-report.test.sh
  step bash test/construct-project.test.sh
  step bash test/construct-update.test.sh
  step python3 test/t3-pairing-forward.test.py
  step bash test/vault-guest-scripts.test.sh
  step bash test/construct-notify.test.sh
  step bash test/fetch-construct-source.test.sh
  step bash test/git-credential-handoff.test.sh
  step python3 test/checkout-projects.test.py
  step python3 test/provision-commands.test.py
  step bash test/provision-diskcheck.test.sh
  step bash test/provision-hostname.test.sh
  step bash test/provision-marker.test.sh
  step bash test/provision-steprunner.test.sh
  step bash test/partial-streaming.test.sh
  step bash test/patch-status.test.sh
  step bash test/opencode-install.test.sh
  step bash test/vscode-download.test.sh
  step bash test/vscode-extensions.test.sh
  step bash test/systemprompt-install.test.sh
  step bash test/repair-agent.test.sh
  step bash test/agent-sign-in.test.sh
  step bash test/autoinstall-iso.test.sh
  step python3 test/host-conversion.test.py
  step pwsh -NoProfile -File service/tests/Constructd.Tests/Windows/windows-guest-report.test.ps1
  step pwsh -NoProfile -File service/tests/Constructd.Tests/Capacity/inventory.test.ps1
  step pwsh -NoProfile -File test/windows-license-firmware.test.ps1
  step pwsh -NoProfile -File test/hyperv-prereqs.test.ps1
  step pwsh -NoProfile -File test/native-iso-host.test.ps1
  step python3 test/iso-download.test.py
  step bash test/host-package.test.sh
  step pwsh -NoProfile -File service/tests/host-installer.test.ps1
  step pwsh -NoProfile -File service/tests/host-installer-solo.test.ps1
  step pwsh -NoProfile -File service/tests/hyperv-scripts.test.ps1
  step pwsh -NoProfile -File service/tests/host-release-installer.test.ps1
  step pwsh -NoProfile -File service/tests/host-updater.test.ps1
  step bash service/tests/host-updater.test.sh
  step bash service/tests/proxmox-media-storage.test.sh
  step bash service/tests/host-installer-certificate.test.sh
  step bash test/backup-e2e.test.sh
  step bash test/external-host.test.sh
  # Every changed shell script, once: no new shellcheck diagnostics against the branch's base.
  scripted='' step bash test/shellcheck-changed.sh ${base:+"$base"}
fi
if runs release; then
  # Release tooling, channel resolution and the updater tests (docs/release-flow.md).
  step python3 test/release.test.py
  step python3 test/construct-release.test.py
  # Skip only when an unfiltered engine run covers Updates.
  if ! (runs engine && [[ -z "${filter[engine]:-}" ]]); then
    scripted='' step dotnet test service/tests/Constructd.Tests -c Release --filter 'FullyQualifiedName~Constructd.Tests.Updates'
  fi
  step pwsh -NoProfile -File service/tests/host-updater.test.ps1
  step bash service/tests/host-updater.test.sh
fi
scripted=''
if runs engine && [[ "${filter[engine]:-}" != none ]]; then
  # The engine's tests: the real host in fake mode, in memory or on SQLite, one per test. Its end-to-end tests
  # (real clients, a real guest) are the e2e group.
  step dotnet build service/Constructd.sln -c Release
  step dotnet test service/tests/Constructd.Tests -c Release --no-build --filter "$(dotnet_filter engine 'Category!=E2E')"
  if [[ -z "$quick" ]]; then step bash test/contracts-compile.test.sh; fi
fi
if runs ui; then
  # The client UI: unit tests (with the generated clients' drift check), the static bundle, its screenshots in
  # headless Chromium. Its end-to-end test on the real agents and engine is the e2e group.
  step in_ui npm install --no-audit --no-fund
  step in_ui npm test
  step in_ui npm run build
  step in_ui npx playwright test
fi
if runs agent; then
  # The device agent: agents in process against the real engine (fake mode) on loopback HTTPS.
  # The Windows shell (agent/src/Construct.Agent.Windows) is in the solution and builds here with Windows targeting.
  if [[ "${filter[agent]:-}" != none ]]; then
    step dotnet build agent/Construct.Agent.slnx -warnaserror
    step dotnet test agent/Construct.Agent.slnx --no-build --filter "$(dotnet_filter agent 'Category!=E2E')"
  fi
  # The upgrade from an old install: the bootstraps, the old updaters of classes E and F against this tree
  # (design 13.2), their release check and the lifecycle lock the class F updater takes. The quick tier runs
  # them when a touched path is one of the scripts or named in them.
  scripted=1
  step bash test/agent-install.test.sh
  step pwsh -NoProfile -File test/agent-bootstrap.test.ps1
  step pwsh -NoProfile -File test/source-release.test.ps1
  step pwsh -NoProfile -File test/lifecycle-run.test.ps1
  step pwsh -NoProfile -Command '$null = [ScriptBlock]::Create((Get-Content -Raw agent/install/Install-ConstructAgent.ps1))'
  scripted=''
fi
if runs e2e; then
  # End to end on this machine: the engine's tests with real clients (and a real guest when CONSTRUCT_E2E_SSH_*
  # names one), and the UI's run on the real engine and two real device agents in headless Chromium.
  step dotnet build service/Constructd.sln -c Release
  if [[ ${#e2e_ssh_env[@]} -gt 0 ]]; then
    step env "${e2e_ssh_env[@]}" dotnet test service/tests/Constructd.Tests -c Release --no-build --filter 'Category=E2E'
  else
    step dotnet test service/tests/Constructd.Tests -c Release --no-build --filter 'Category=E2E'
  fi
  step in_ui npm install --no-audit --no-fund
  step in_ui npm run build
  step in_ui npm run test:e2e
fi
# Not part of `all`: it creates a real child VM on the host and takes about half an
# hour. Run it when guest-facing paths change (docs/local-checks.md).
if runs guest; then
  step bash test/e2e/guest-rig.sh quick
fi

# ---- what ran, how long, what failed -------------------------------------------------------------------------
finish
if [[ ${#summary[@]} -gt 0 ]]; then
  printf '\n== Steps (%s)\n' "${selected[*]}"
  printf '%s\n' "${summary[@]}"
  printf '%s steps, %s failed, %ss in all\n' "${#summary[@]}" "$failures" "$SECONDS"
fi
[[ $failures -eq 0 ]]
