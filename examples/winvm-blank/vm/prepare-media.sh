#!/usr/bin/env bash
# Prepare reusable Windows no-prompt boot media and answer-file ISO.
set -euo pipefail
cd "$(dirname "$0")"
. ./config.sh
BOOT_ISO="${WIN_ISO%.iso}-noprompt.iso"
if [ ! -f "$BOOT_ISO" ]; then
    [ -f "$WIN_ISO" ] || { echo "Run download-images.sh first" >&2; exit 1; }
    echo "==> Repacking Windows media with its no-prompt UEFI boot image"
    extract=$(mktemp -d -p "$E2E_IMAGES")
    mnt=$(mktemp -d)
    cleanup() {
        if mountpoint -q "$mnt"; then umount "$mnt"; fi
        rmdir "$mnt"
        rm -r "$extract"
        [ ! -f "$BOOT_ISO.partial" ] || rm "$BOOT_ISO.partial"
    }
    trap cleanup EXIT
    # Windows media stores its files in UDF; xorriso reads only the ISO stub.
    mount -o loop,ro "$WIN_ISO" "$mnt"
    cp -a "$mnt"/. "$extract"/
    umount "$mnt"
    [ -f "$extract/efi/microsoft/boot/efisys_noprompt.bin" ]
    xorriso -as mkisofs -iso-level 4 -J -joliet-long -R -V WIN11 \
        -b boot/etfsboot.com -no-emul-boot -boot-load-size 8 \
        -eltorito-alt-boot -eltorito-platform efi \
        -e efi/microsoft/boot/efisys_noprompt.bin -no-emul-boot \
        -o "$BOOT_ISO.partial" "$extract"
    mv "$BOOT_ISO.partial" "$BOOT_ISO"
fi
echo "==> Building unattend ISO ($WIN_VARIANT)"
genisoimage -quiet -o "$UNATTEND_ISO" -J -R -V UNATTEND "$UNATTEND_DIR"/
