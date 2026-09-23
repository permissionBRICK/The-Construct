#!/usr/bin/env bash
# Rebuild an Ubuntu cloud image with an XFS root file system.
#
#   bash service/host/xfs-cloud-image.sh <ubuntu-cloudimg.img> <out.qcow2> [virtual-size]
#
# Why: Construct guests keep many git worktrees of the same repositories, and XFS reflinks
# (cp --reflink) let those worktrees share their build outputs and dependencies block for block.
# Ubuntu publishes its cloud images with ext4 only, which has no reflinks, so
# install-construct-host.sh converts the downloaded image once per Proxmox node.
#
# The conversion keeps the partition table, the EFI/BIOS boot partitions and /boot byte for byte
# and replaces only the root partition's file system:
#   - mkfs.xfs runs from the IMAGE's own xfsprogs (chroot), so the file system never uses a
#     feature newer than the guest kernel supports, whatever the node runs;
#   - the XFS root reuses the ext4 root's UUID; fstab and the kernel command line switch from
#     LABEL=cloudimg-rootfs (longer than XFS's 12-character label limit) to that UUID;
#   - the image grows to [virtual-size] (default 8G, the smallest VM disk the service creates) so
#     the file system starts with allocation groups sized for a real disk, not a 2.5 GB image.
#     VMs clone it and cloud-init's growpart/resizefs grow the root to the VM's disk as before.
# The image's initramfs already carries xfs.ko, and its xfsprogs provides xfs_growfs.
#
# Needs root, qemu-img, losetup, sfdisk, blkid, chroot. Writes <out.qcow2> atomically; work files
# live next to it and are removed on exit.
set -euo pipefail

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $# -ge 2 ]] || die "usage: $0 <cloudimg.img> <out.qcow2> [virtual-size]"
IN="$1" OUT="$2" SIZE="${3:-8G}"
[[ "$(id -u)" -eq 0 ]] || die "run as root (loop devices and mounts)"
[[ -f "${IN}" ]] || die "'${IN}' does not exist"
for tool in qemu-img losetup sfdisk blkid chroot mount umount; do
  command -v "${tool}" >/dev/null || die "'${tool}' is required"
done

WORK="$(mktemp -d "$(dirname "${OUT}")/.xfs-image.XXXXXX")"
SRC_LOOP="" DST_LOOP=""
cleanup() {
  for m in "${WORK}/src/dev" "${WORK}/src/proc" "${WORK}/src" "${WORK}/dst" "${WORK}/boot"; do
    mountpoint -q "${m}" 2>/dev/null && umount "${m}"
  done
  [[ -n "${SRC_LOOP}" ]] && losetup -d "${SRC_LOOP}" 2>/dev/null
  [[ -n "${DST_LOOP}" ]] && losetup -d "${DST_LOOP}" 2>/dev/null
  rm -rf "${WORK}"
}
trap cleanup EXIT

qemu-img convert -O raw "${IN}" "${WORK}/src.raw"
cp --sparse=always "${WORK}/src.raw" "${WORK}/dst.raw"
truncate -s "${SIZE}" "${WORK}/dst.raw"
# Move the backup GPT to the new end of the disk, then let partition 1 (the root, the last one
# on the disk in Ubuntu's layout) fill it.
sfdisk -q --relocate gpt-bak-std "${WORK}/dst.raw"
echo ',+' | sfdisk -q --no-reread --no-tell-kernel -N 1 "${WORK}/dst.raw"

SRC_LOOP="$(losetup -rfP --show "${WORK}/src.raw")"
DST_LOOP="$(losetup -fP --show "${WORK}/dst.raw")"
udevadm settle 2>/dev/null || true
[[ "$(blkid -s TYPE -o value "${SRC_LOOP}p1")" == ext4 && "$(blkid -s LABEL -o value "${SRC_LOOP}p1")" == cloudimg-rootfs ]] \
  || die "partition 1 of '${IN}' is not the ext4 cloudimg-rootfs this script expects"
BOOT_PART="$(blkid -t LABEL=BOOT -o device "${DST_LOOP}"p* 2>/dev/null | head -n1)"
[[ -n "${BOOT_PART}" ]] || die "'${IN}' has no BOOT partition (its kernel command line lives there)"
ROOT_UUID="$(blkid -s UUID -o value "${SRC_LOOP}p1")"

mkdir -p "${WORK}/src" "${WORK}/dst" "${WORK}/boot"
mount -o ro "${SRC_LOOP}p1" "${WORK}/src"
mount --bind /dev "${WORK}/src/dev"
mount -t proc proc "${WORK}/src/proc"
chroot "${WORK}/src" mkfs.xfs -q -f -m uuid="${ROOT_UUID}" "${DST_LOOP}p1"
umount "${WORK}/src/proc" "${WORK}/src/dev"

mount "${DST_LOOP}p1" "${WORK}/dst"
cp -a "${WORK}/src/." "${WORK}/dst/"
sed -i -E "s|^LABEL=cloudimg-rootfs[[:space:]]+/[[:space:]].*|UUID=${ROOT_UUID}\t/\txfs\tdefaults,discard\t0 1|" "${WORK}/dst/etc/fstab"
grep -qE "^UUID=${ROOT_UUID}[[:space:]]+/[[:space:]]+xfs" "${WORK}/dst/etc/fstab" || die "could not rewrite the root line of /etc/fstab"

mount "${BOOT_PART}" "${WORK}/boot"
sed -i "s|root=LABEL=cloudimg-rootfs|root=UUID=${ROOT_UUID}|g" "${WORK}/boot/grub/grub.cfg"
grep -q "root=UUID=${ROOT_UUID}" "${WORK}/boot/grub/grub.cfg" && ! grep -q 'LABEL=cloudimg-rootfs' "${WORK}/boot/grub/grub.cfg" \
  || die "could not rewrite the kernel command line in /boot/grub/grub.cfg"
umount "${WORK}/boot" "${WORK}/dst" "${WORK}/src"
losetup -d "${SRC_LOOP}"; SRC_LOOP=""
losetup -d "${DST_LOOP}"; DST_LOOP=""

qemu-img convert -O qcow2 "${WORK}/dst.raw" "${WORK}/out.qcow2"
mv -f "${WORK}/out.qcow2" "${OUT}"
