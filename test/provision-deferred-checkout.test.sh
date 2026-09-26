#!/usr/bin/env bash
# bin/provision.sh: on a reinstall that restores a saved config (DEFER_PROJECT_COMMANDS),
# the project checkout is NOT run in the main pass and runs FIRST in the deferred phase,
# with a handed credential consulted before the VM's own store. Driven with the two
# blocks extracted from the script and every action stubbed.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/bin/provision.sh"
main="$(sed -n '/^# 6\. Optionally check out project repos/,/^# Drop the transient clone-credentials temp file/p' "$src")"
phase="$(sed -n '/^if \[\[ "${PROVISION_PHASE:-}" == "project-commands" \]\]; then/,/^fi$/p' "$src")"
[[ -n "$main" && -n "$phase" ]] || { echo 'FAIL: could not extract the checkout blocks'; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
stubs='run_step() { printf "STEP %s\n" "$*"; }; step() { printf "STEP %s\n" "$*"; }; note() { printf "NOTE %s\n" "$*"; }; _finish_provision() { printf "FINISH %s\n" "$1"; }; mkdir() { :; }'
pass=0; fail=0
ok() { if [[ "$2" == true ]]; then pass=$((pass+1)); echo "  PASS  $1"; else fail=$((fail+1)); echo "  FAIL  $1"; fi; }

out="$(REPO_DIR=/r CHECKOUT_PROJECTS=true DEFER_PROJECT_COMMANDS=true _clone_creds_file="" PROJECTS=p bash -c "$stubs; $main")"
ok "main pass: a deferred checkout does not run" "$([[ "$out" != *"Checking out project repos"* ]] && echo true || echo false)"
ok "main pass: ...and says it waits for the restore" "$([[ "$out" == *"deferred until the saved config is restored"* ]] && echo true || echo false)"
out="$(REPO_DIR=/r CHECKOUT_PROJECTS=true DEFER_PROJECT_COMMANDS=false _clone_creds_file="" PROJECTS=p bash -c "$stubs; $main")"
ok "main pass: without a deferral the checkout runs as before" "$([[ "$out" == *"STEP optional Checking out project repos bash /r/bin/checkout-projects.sh"* ]] && echo true || echo false)"

out="$(REPO_DIR=/r CHECKOUT_PROJECTS=true PROVISION_PHASE=project-commands _PERSISTENT_LOG_DIR="$tmp/p" _PROVISION_LOG_DIR="$tmp" _PROVISION_MARKER="$tmp/marker" bash -c "$stubs; $phase")"
first="$(printf '%s\n' "$out" | grep -n 'Checking out project repos' | head -1 | cut -d: -f1)"
second="$(printf '%s\n' "$out" | grep -n 'Running project provisioning commands' | head -1 | cut -d: -f1)"
ok "deferred phase: the checkout runs, before the project commands" "$([[ -n "$first" && -n "$second" && "$first" -lt "$second" ]] && echo true || echo false)"
ok "deferred phase: ...with the VM's own store (no handed credential)" "$([[ "$out" == *"STEP optional Checking out project repos bash /r/bin/checkout-projects.sh"* ]] && echo true || echo false)"
ok "deferred phase: ...and finishes with the result block" "$([[ "$out" == *"FINISH 0"* ]] && echo true || echo false)"

handed="$(printf 'https://u:t@git.example' | base64 -w0)"
out="$(REPO_DIR=/r CHECKOUT_PROJECTS=true PROVISION_PHASE=project-commands GIT_CLONE_CREDENTIALS_B64="$handed" _PERSISTENT_LOG_DIR="$tmp/p" _PROVISION_LOG_DIR="$tmp" _PROVISION_MARKER="$tmp/marker" bash -c "$stubs; $phase")"
ok "deferred phase: a handed credential is consulted first, the VM's store after it" "$([[ "$out" == *"GIT_CONFIG_VALUE_1=store --file=$tmp/clone-credentials GIT_CONFIG_KEY_2=credential.helper GIT_CONFIG_VALUE_2=store bash /r/bin/checkout-projects.sh"* ]] && echo true || echo false)"
ok "deferred phase: ...and the one-shot file is removed afterwards" "$([[ ! -e "$tmp/clone-credentials" ]] && echo true || echo false)"

out="$(REPO_DIR=/r CHECKOUT_PROJECTS=false PROVISION_PHASE=project-commands _PERSISTENT_LOG_DIR="$tmp/p" _PROVISION_LOG_DIR="$tmp" _PROVISION_MARKER="$tmp/marker" bash -c "$stubs; $phase")"
ok "deferred phase: no checkout when the host decided against one" "$([[ "$out" != *"Checking out"* && "$out" == *"Running project provisioning commands"* ]] && echo true || echo false)"

echo "provision deferred checkout -- $pass passed, $fail failed"
[[ $fail -eq 0 ]]
