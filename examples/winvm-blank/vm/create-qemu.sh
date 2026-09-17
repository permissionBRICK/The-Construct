#!/usr/bin/env bash
# Creates the Windows test VM from scratch:
#   1. builds the unattend ISO from unattend/
#   2. creates a blank qcow2 disk
#   3. boots the Windows Server 2022 eval ISO with autounattend
#   4. waits until the guest's first-logon provisioning is done (SSH marker)
#   5. shuts the guest down (no permanent snapshots)
#
# Watch the install live: VNC on <host>:5901 (e.g. agent-vm.mshome.net:5901).
# Typical duration: 15-35 min depending on disk/CPU.
set -euo pipefail
cd "$(dirname "$0")"
export VM_BACKEND=qemu
. ./config.sh
. ../audit.sh
audit_log VM-CREATE "creating disk $VM_DISK (WIN_VARIANT=$WIN_VARIANT, force=${1:-})"

FORCE="${1:-}"
if [ -f "$VM_DISK" ] && [ "$FORCE" != "--force" ]; then
    echo "Disk $VM_DISK already exists. Use --force to wipe and reinstall." >&2
    audit_log vm-create-refused "disk exists: $VM_DISK"
    exit 1
fi
if vm_running; then
    echo "VM is running (pid $(cat "$PID_FILE")). Stop it first: ./vm.sh stop" >&2
    exit 1
fi

[ -f "$VIRTIO_ISO" ] || { echo "virtio ISO missing - run ./download-images.sh first" >&2; exit 1; }
# UEFI: the cached no-prompt repack fully replaces the original media.
if ! { [ "${WIN_FIRMWARE:-bios}" = "uefi" ] && [ -f "${WIN_ISO%.iso}-noprompt.iso" ]; }; then
    [ -f "$WIN_ISO" ] || { echo "Windows ISO missing - run ./download-images.sh first" >&2; exit 1; }
fi

# UEFI boot of stock Windows media stops at "Press any key to boot from CD or
# DVD..." (efisys.bin). Repack once with the media's own no-prompt EFI boot
# image. No reboot loop: after setup's first phase the Windows Boot Manager
# NVRAM entry (persisted in $VM_NVRAM) precedes the CD in boot order.
BOOT_ISO="$WIN_ISO"
if [ "${WIN_FIRMWARE:-bios}" = "uefi" ]; then
    BOOT_ISO="${WIN_ISO%.iso}-noprompt.iso"
    if [ ! -f "$BOOT_ISO" ]; then
        echo "==> Repacking $WIN_ISO with no-prompt UEFI boot image"
        command -v xorriso >/dev/null || apt-get install -y xorriso
        mnt=$(mktemp -d); extract=$(mktemp -d -p "$E2E_IMAGES")
        mount -o loop,ro "$WIN_ISO" "$mnt"
        cp -a "$mnt"/. "$extract"/
        umount "$mnt"; rmdir "$mnt"
        [ -f "$extract/efi/microsoft/boot/efisys_noprompt.bin" ] || { echo "efisys_noprompt.bin not in media" >&2; rm -rf "$extract"; exit 1; }
        xorriso -as mkisofs -iso-level 4 -J -joliet-long -R \
            -V WIN11 \
            -b boot/etfsboot.com -no-emul-boot -boot-load-size 8 \
            -eltorito-alt-boot -eltorito-platform efi \
            -e efi/microsoft/boot/efisys_noprompt.bin -no-emul-boot \
            -o "$BOOT_ISO" "$extract"
        rm -rf "$extract"
    fi
fi

echo "==> Building unattend ISO ($WIN_VARIANT)"
genisoimage -quiet -o "$UNATTEND_ISO" -J -R -V UNATTEND "$UNATTEND_DIR"/

echo "==> Creating disk $VM_DISK ($VM_DISK_SIZE)"
rm -f "$VM_DISK" "$VM_NVRAM"
qemu-img create -q -f qcow2 "$VM_DISK" "$VM_DISK_SIZE"

echo "==> Starting unattended install (VNC :$VNC_DISPLAY / port $((5900 + VNC_DISPLAY)))"
# BIOS boot, blank disk -> falls through to CD without the "press any key"
# prompt; after setup makes the disk bootable, subsequent boots use the disk.
# SATA disk + e1000e NIC = Windows in-box drivers, no driver injection needed.
./vm.sh _launch \
    -drive "file=$BOOT_ISO,media=cdrom,index=1" \
    -drive "file=$UNATTEND_ISO,media=cdrom,index=2" \
    -drive "file=$VIRTIO_ISO,media=cdrom,index=3"

echo "==> Waiting for Windows setup + first-logon provisioning (up to 60 min)"
deadline=$(( $(date +%s) + 3600 ))
while :; do
    if guest_ssh "type C:\\provision\\firstlogon.done" >/dev/null 2>&1; then
        echo "==> Guest provisioned."
        break
    fi
    if ! vm_running; then
        echo "ERROR: VM process exited during install. Check $SERIAL_LOG and VNC." >&2
        exit 1
    fi
    [ "$(date +%s)" -ge "$deadline" ] && { echo "ERROR: timeout waiting for guest" >&2; exit 1; }
    sleep 20
done

echo "==> Shutting down guest"
guest_ssh "shutdown /s /t 5" || true
for _ in $(seq 1 60); do vm_running || break; sleep 5; done
vm_running && { echo "Forcing off"; kill "$(cat "$PID_FILE")"; sleep 3; }

echo "==> Done. Next: ./provision.sh"
