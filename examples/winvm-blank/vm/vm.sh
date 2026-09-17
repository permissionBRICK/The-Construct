#!/usr/bin/env bash
# Backend-neutral VM operations. VM_BACKEND=qemu selects nested KVM.
set -euo pipefail
VM_DIR="$(cd "$(dirname "$0")" && pwd)"
. "$VM_DIR/config.sh"
if [ "$VM_BACKEND" = qemu ]; then exec "$VM_DIR/vm-qemu.sh" "$@"; fi
case "${1:-status}" in
    start) if ! vm_running; then construct vm start "$VM_NAME" --lifetime "$VM_LIFETIME"; fi ;;
    stop) construct vm shutdown "$VM_NAME" ;;
    restart) construct vm restart "$VM_NAME" ;;
    status) construct vm inspect "$VM_NAME" ;;
    ssh) shift; guest_ssh "$@" ;;
    scp) shift; guest_scp "$@" ;;
    screenshot) construct vm console "$VM_NAME" --screenshot "${2:-$E2E_RUN/screen.png}" ;;
    web) construct vm console "$VM_NAME" --web ;;
    snap|revert|kill|_launch)
        echo "'$1' is unavailable with Construct. No permanent snapshots; use run-testvm.sh --rebuild for a clean VM." >&2; exit 2 ;;
    *) echo "Usage: $0 start|stop|restart|status|ssh|scp|screenshot [file.png]|web" >&2; exit 2 ;;
esac
