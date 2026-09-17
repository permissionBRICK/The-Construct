#!/usr/bin/env bash
# Execute only the installer function against fixture paths. Never run the installer.
set -euo pipefail
cd "$(dirname "$0")/.."
tmp="$(mktemp -d)"
trap 'rm -r "$tmp"' EXIT
note() { printf '%s\n' "$*"; }
eval "$(sed -n '/^configure_nested_virtualization() {$/,/^}$/p' service/host/install-construct-host.sh)"
for module in kvm_intel kvm_amd; do
  root="$tmp/$module"
  mkdir -p "$root/sys/$module/parameters"
  for off in N 0; do
    printf '%s\n' "$off" >"$root/sys/$module/parameters/nested"
    out="$(configure_nested_virtualization "$root/sys" "$root/etc/construct-kvm.conf")"
    [[ "$(cat "$root/etc/construct-kvm.conf")" == "options $module nested=1" ]]
    [[ "$out" == *'not live'* && "$out" == *'after stopping all guests'* ]]
    [[ "$(cat "$root/sys/$module/parameters/nested")" == "$off" ]]
    again="$(configure_nested_virtualization "$root/sys" "$root/etc/construct-kvm.conf")"
    [[ "$again" == "$out" ]]
  done
  for on in Y 1; do
    printf '%s\n' "$on" >"$root/sys/$module/parameters/nested"
    printf '# existing configuration\n' >"$root/etc/construct-kvm.conf"
    out="$(configure_nested_virtualization "$root/sys" "$root/etc/construct-kvm.conf")"
    [[ "$out" == *'is live'* ]]
    [[ "$(cat "$root/etc/construct-kvm.conf")" == '# existing configuration' ]]
  done
done
out="$(configure_nested_virtualization "$tmp/missing" "$tmp/unwritten")"
[[ "$out" == *'unavailable'* && ! -e "$tmp/unwritten" ]]
printf 'Proxmox nested installer fixtures passed.\n'
