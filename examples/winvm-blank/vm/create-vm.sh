#!/usr/bin/env bash
# Unattended Windows installation using Construct by default, KVM on request.
set -euo pipefail
cd "$(dirname "$0")"
. ./config.sh
if [ "$VM_BACKEND" = qemu ]; then exec ./create-qemu.sh "$@"; fi
if [ "$WIN_VARIANT" != win11 ]; then
    echo "Construct currently supports the win11 GPT answer file. Use VM_BACKEND=qemu for server2022." >&2; exit 2
fi
case "${1:-}" in ''|--force) ;; *) echo "Usage: $0 [--force]" >&2; exit 2 ;; esac
# Read inventory successfully before deciding whether anything is missing.
inventory=$(vm_inventory)
if jq -e --arg name "$VM_NAME" 'any(.[]; .name == $name)' <<<"$inventory" >/dev/null; then
    if [ "${1:-}" = --force ]; then
        construct vm delete "$VM_NAME" --yes
        for file in "$E2E_RUN"/last-sync.{list,origin} "$E2E_RUN/create-operation-id"; do
            [ ! -f "$file" ] || rm "$file"
        done
    elif guest_ssh 'if (Test-Path C:\provision\install-complete.done) { exit 0 }; exit 1' >/dev/null 2>&1; then
        echo "==> Windows installation already complete"
        exit 0
    else
        echo "==> Resuming installation of existing $VM_NAME"
        ./vm.sh start
    fi
fi
inventory=$(vm_inventory)
if ! jq -e --arg name "$VM_NAME" 'any(.[]; .name == $name)' <<<"$inventory" >/dev/null; then
    ./prepare-media.sh
    # Persist retry identity across interrupted media uploads / CLI connections.
    key_file="$E2E_RUN/create-operation-id"
    [ -s "$key_file" ] || cat /proc/sys/kernel/random/uuid > "$key_file"
    construct vm create --name "$VM_NAME" \
        --iso "${WIN_ISO%.iso}-noprompt.iso" --aux-iso "$UNATTEND_ISO" \
        --cpus "$VM_CPUS" --ram-mb "$VM_RAM" --disk-gb "${VM_DISK_SIZE%G}" \
        --lifetime "$VM_LIFETIME" --preset windows --generation 2 \
        --secure-boot on --secure-boot-template microsoftWindows --tpm on \
        --boot-order disk,installMedia --operation-id "$(cat "$key_file")"
fi
echo "==> Waiting for Windows setup + first logon (up to 60 minutes)"
deadline=$((SECONDS + 3600))
until guest_ssh 'if (Test-Path C:\provision\firstlogon.done) { exit 0 }; exit 1' >/dev/null 2>&1; do
    if (( SECONDS >= deadline )); then echo "Timed out; inspect with ./vm.sh screenshot" >&2; exit 1; fi
    echo "[$(date +%H:%M:%S)] Windows installation in progress"
    sleep 20
done
# Remove the answer media so it cannot be applied on a subsequent boot.
./vm.sh stop
construct vm media detach "$VM_NAME" --install
construct vm media detach "$VM_NAME" --aux
./vm.sh start
# Mark completion only after both media slots have been detached.
for _ in $(seq 1 90); do
    if guest_ssh 'Set-Content C:\provision\install-complete.done (Get-Date -Format o)'; then
        echo "==> Windows ready. Next: ./provision.sh"
        exit 0
    fi
    sleep 10
done
echo "Windows did not return after media detach/restart" >&2
exit 1
