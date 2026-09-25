#!/usr/bin/env bash
# Evaluate the real provisioning gate with shell functions in place of every external action.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -r "$tmp"' EXIT
printf 'fixture-token\n' > "$tmp/token"
export CONSTRUCT_VM_TOKEN_FILE="$tmp/token"
export CONSTRUCT_SERVICE_URL='https://fixture.invalid'
export REPO_DIR="$root"
gate="$(sed -n '/^service_offers_feature()/,/^fi$/p' "$root/bin/provision.sh")"
curl() { printf '%s' "$HEALTH_JSON"; }
run_step() { printf '%s\n' "$*"; }
note() { printf '%s\n' "$*"; }
export -f curl run_step note
for backend in proxmox hyperv; do
  export HEALTH_JSON="{\"backend\":\"$backend\",\"apiFeatures\":[\"children\",\"console\"]}"
  output="$(bash -c "$gate")"
  # OPTIONAL: the image comes from Docker Hub, which a company network can block; a VM without its
  # browser console is usable, a VM whose provisioning stopped here was not.
  [[ "$output" == "optional Installing browser console gateway bash $root/console-viewer/install.sh" ]]
done
export HEALTH_JSON='{"apiFeatures":["children"]}'
[[ "$(bash -c "$gate")" == *'Skipping the browser console gateway'* ]]
export HEALTH_JSON='{}'
[[ "$(bash -c "$gate")" == optional* ]]
printf 'PASS: Proxmox and Hyper-V gateway provisioning, unavailable console, legacy host\n'
