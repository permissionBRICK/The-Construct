#!/usr/bin/env bash
# Exercise the actual provisioning seed/checkout section with an isolated guest.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -r "$tmp"' EXIT
mkdir -p "$tmp/agent/runtime" "$tmp/repo/bin" "$tmp/bin" "$tmp/logs"
cp "$root/bin/checkout-projects.sh" "$tmp/repo/bin/"
# Extract just the credential and checkout steps; no system provision or cleanup.
sed -n '/^# 5c\./,/^# Drop the transient/p' "$root/bin/provision.sh" > "$tmp/steps.sh"
printf 'AGENT_HOME=%q\nWORKSPACE_ROOT=%q\n' "$tmp/agent" "$tmp/workspace" > "$tmp/config.env"
printf '%s\n' '{"repos":[{"url":"https://verified.example/project.git"},{"url":"https://skipped.example/project.git"}]}' > "$tmp/agent/runtime/merged-config.json"
# Match the real checkout filename, independently of the generated config name.
generated_name="$(sed -n 's/^GENERATED_JSON=.*runtime\/\([^"}]*\)".*/\1/p' "$root/bin/checkout-projects.sh")"
if [[ "$generated_name" != merged-config.json ]]; then mv "$tmp/agent/runtime/merged-config.json" "$tmp/agent/runtime/$generated_name"; fi
export REAL_GIT="$(command -v git)" FIXTURE_ROOT="$tmp"
cat > "$tmp/bin/git" <<'GIT'
#!/usr/bin/env bash
set -euo pipefail
[[ "$GIT_TERMINAL_PROMPT" == 0 ]]
[[ "$GIT_CONFIG_COUNT" == 2 && "$GIT_CONFIG_KEY_0" == credential.helper && -z "$GIT_CONFIG_VALUE_0" ]]
[[ "$GIT_CONFIG_KEY_1" == credential.helper && "$GIT_CONFIG_VALUE_1" == store\ --file=* ]]
[[ "$*" != *'fixture-pat'* && "$*" != *'skipped.example'* ]]
printf 'protocol=https\nhost=verified.example\n\n' | "$REAL_GIT" credential fill > "$FIXTURE_ROOT/filled"
printf '%s\n' "$*" >> "$FIXTURE_ROOT/argv"
GIT
chmod 700 "$tmp/bin/git"
export PATH="$tmp/bin:$PATH" CONFIG_FILE="$tmp/config.env"
export GIT_CLONE_SKIP_HOSTS_B64="$(printf '%s' 'https://skipped.example' | base64 -w0)"
# Exactly the existing host encoding; decoded credentials only enter the seed file.
GIT_CLONE_CREDENTIALS_B64="$(printf '%s' 'https://fixture-user:fixture-pat%3A%2F%40%20space@verified.example' | base64 -w0)"
GIT_CLONE_CREDENTIALS="$(printf '%s' "$GIT_CLONE_CREDENTIALS_B64" | base64 -d)"
GIT_CREDENTIAL_STORE=false CHECKOUT_PROJECTS=true
_PROVISION_LOG_DIR="$tmp/logs" REPO_DIR="$tmp/repo"
run_step() { shift 2; "$@"; }
note() { :; }
ok() { :; }
source "$tmp/steps.sh" > "$tmp/output" 2>&1
[[ "$(stat -c '%a' "$tmp/logs/clone-credentials")" == 600 ]]
printf 'PASS: guest seed file permissions\n'
[[ "$(cat "$tmp/logs/clone-credentials")" == "$GIT_CLONE_CREDENTIALS" ]]
printf 'PASS: unchanged base64 credential-store payload\n'
[[ "$(cat "$tmp/filled")" == $'protocol=https\nhost=verified.example\nusername=fixture-user\npassword=fixture-pat:/@ space' ]]
printf 'PASS: one-shot helper decodes username and token\n'
[[ "$(wc -l < "$tmp/argv")" == 1 ]]
printf 'PASS: verified host cloned and skipped host omitted\n'
! rg -q 'fixture-pat' "$tmp/output" "$tmp/argv"
printf 'PASS: token absent from checkout output and argv\n'
printf '5 passed\n'
