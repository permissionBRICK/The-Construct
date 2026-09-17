#!/usr/bin/env bash
# One-time host preparation for the Windows test VM (Ubuntu, run as root).
# Safe to re-run (idempotent) - suitable as a construct provisionCommand.
set -euo pipefail

. "$(dirname "$0")/config.sh"

if [ "$VM_BACKEND" = qemu ] && [ ! -e /dev/kvm ]; then
    cat >&2 <<'EOF'
ERROR: /dev/kvm missing - nested virtualization is not exposed to this VM.
On the Hyper-V host (VM powered off):
    Set-VMProcessor -VMName <vm-name> -ExposeVirtualizationExtensions $true
EOF
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
packages=(genisoimage xorriso sshpass netcat-openbsd zip unzip p7zip-full jq curl)
if [ "$VM_BACKEND" = qemu ]; then
    packages+=(qemu-system-x86 qemu-utils socat imagemagick novnc websockify swtpm swtpm-tools ovmf)
fi
apt-get install -y -qq "${packages[@]}"

# pwsh: download-images.sh resolves the Win11 ISO URL via Fido (a PowerShell
# script) - without it the bootstrap stops right after this step.
if ! command -v pwsh >/dev/null; then
    echo "installing PowerShell (needed by Fido for the Win11 ISO URL)"
    snap install powershell --classic || {
        curl -fsSL "https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release; echo "$VERSION_ID")/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb
        dpkg -i /tmp/packages-microsoft-prod.deb
        apt-get update -qq
        apt-get install -y -qq powershell
    }
fi

mkdir -p "$E2E_HOME"/{images,disks,run,results}
echo "Host ready. Next: ./download-images.sh && ./create-vm.sh && ./post-install.sh"
