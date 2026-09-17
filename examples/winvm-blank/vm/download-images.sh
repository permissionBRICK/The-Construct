#!/usr/bin/env bash
# Downloads the Windows Server 2022 eval ISO and the virtio-win driver ISO.
# Idempotent: resumes/skips if already present and plausible-sized.
set -euo pipefail
cd "$(dirname "$0")"
. ./config.sh

fetch() { # url dest min_bytes
    local url="$1" dest="$2" min="$3"
    if [ -f "$dest" ] && [ "$(stat -c%s "$dest")" -ge "$min" ]; then
        echo "OK (cached): $dest"
        return
    fi
    echo "Downloading $dest ..."
    curl -fL --retry 3 -C - -o "$dest.partial" "$url"
    [ "$(stat -c%s "$dest.partial")" -ge "$min" ] || { echo "ERROR: $dest too small" >&2; exit 1; }
    mv "$dest.partial" "$dest"
    echo "OK: $dest ($(stat -c%s "$dest") bytes)"
}

# UEFI variants boot the repacked no-prompt ISO (create-vm.sh caches it next
# to the original) - when that exists, the 8 GB original is not needed again.
NOPROMPT_ISO="${WIN_ISO%.iso}-noprompt.iso"
if [ "${WIN_FIRMWARE:-bios}" = "uefi" ] && [ -f "$NOPROMPT_ISO" ] && [ "$(stat -c%s "$NOPROMPT_ISO")" -ge 4000000000 ]; then
    echo "OK (cached): $NOPROMPT_ISO (repacked boot media; original ISO not needed)"
    if [ "$VM_BACKEND" = qemu ]; then fetch "$VIRTIO_ISO_URL" "$VIRTIO_ISO" 400000000; fi
    exit 0
fi

if [ -z "$WIN_ISO_URL" ]; then
    # win11: no stable direct URL - resolve the official link via Fido.
    if [ ! -f "$WIN_ISO" ] || [ "$(stat -c%s "$WIN_ISO")" -lt 4000000000 ]; then
        command -v pwsh >/dev/null || { echo "pwsh required to resolve the Win11 ISO URL (Fido)" >&2; exit 1; }
        fido="$E2E_RUN/Fido.ps1"
        curl -fsL -o "$fido" https://raw.githubusercontent.com/pbatard/Fido/master/Fido.ps1
        # Fido blocks non-Windows platforms and probes the CPU via CIM - stub both.
        sed -i 's/\$Arch = Get-CimInstance -ClassName Win32_Processor | Select-Object -ExpandProperty Architecture/\$Arch = 9/' "$fido"
        sed -i 's/^\$winver = .*/\$winver = 10.0/' "$fido"
        WIN_ISO_URL=$(pwsh -NoProfile -File "$fido" -Win 11 -Rel latest -Ed Pro -Lang eng -Arch x64 -GetUrl 2>/dev/null | grep -o 'https://[^[:space:]]*' | tail -1)
        [ -n "$WIN_ISO_URL" ] || { echo "Fido could not resolve a Win11 ISO URL (rate limit? retry later)" >&2; exit 1; }
    fi
fi

fetch "$WIN_ISO_URL"    "$WIN_ISO"    4000000000
if [ "$VM_BACKEND" = qemu ]; then fetch "$VIRTIO_ISO_URL" "$VIRTIO_ISO" 400000000; fi
