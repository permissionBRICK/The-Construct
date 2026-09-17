# Shared configuration for the throwaway Windows test VM.
# Source this from the other scripts: . "$(dirname "$0")/config.sh"
#
# All runtime state (ISOs, disks, logs) lives OUTSIDE the repo under E2E_HOME
# so that nothing heavy is ever committed.

# Prefer Construct whenever its CLI is installed; errors never silently create
# a second nested VM. Set VM_BACKEND=qemu for the retained KVM implementation.
VM_BACKEND="${VM_BACKEND:-auto}"
if [ "$VM_BACKEND" = auto ]; then
    if command -v construct >/dev/null 2>&1; then VM_BACKEND=construct; else VM_BACKEND=qemu; fi
fi
case "$VM_BACKEND" in construct|qemu) ;; *) echo "Invalid VM_BACKEND: $VM_BACKEND" >&2; return 2 ;; esac
export VM_BACKEND

E2E_HOME="${E2E_HOME:-/opt/winvm}"
E2E_IMAGES="$E2E_HOME/images"
E2E_DISKS="$E2E_HOME/disks"
E2E_RUN="$E2E_HOME/run"
if [ "$VM_BACKEND" = construct ]; then E2E_RUN="$E2E_RUN/construct-${WIN_VARIANT:-win11}"; fi
E2E_RESULTS="$E2E_HOME/results"

# --- Windows guest ---------------------------------------------------------
# Variant: "win11" (default, the Construct-supported one) or "server2022"
# (QEMU backend only). Selects ISO, unattend dir, firmware, ports and VM name.
#
# win11 history: on legacy BIOS the 25H2 media's tssysprep.dll crashed
# (RdpSysPrepRestoreOffline, 0xc0000005) during offline specialize. The
# variant now boots UEFI (OVMF) + swtpm TPM 2.0 - the configuration Win11
# actually supports - see unattend-win11/README.md.
WIN_VARIANT="${WIN_VARIANT:-win11}"

VM_NAME="${VM_NAME:-winvm-$WIN_VARIANT}"
VM_DISK="$E2E_DISKS/$VM_NAME.qcow2"

# --- Guest sizing: derived from the host, since every dev machine differs. ---
# All values overridable via env (VM_CPUS=4 ./vm.sh start).
#
if [ "$VM_BACKEND" = construct ]; then
    VM_CPUS="${VM_CPUS:-$(nproc)}"
    VM_RAM="${VM_RAM:-12288}"
    VM_DISK_SIZE="${VM_DISK_SIZE:-100G}"
fi
VM_LIFETIME="${VM_LIFETIME:-never}"

# CPUs: all but 2 host cores (min 4).
VM_CPUS="${VM_CPUS:-$(( $(nproc) > 6 ? $(nproc) - 2 : 4 ))}"
# RAM: cap the guest at 14 GB and always leave >=9 GB for the Linux host
# (a 16 GB guest on a 23 GB construct host was OOM-killed mid-session).
_host_ram_mb=$(( $(awk '/MemTotal/{print $2}' /proc/meminfo) / 1024 ))
_ram_fit=$(( _host_ram_mb - 9216 ))
_ram_fit=$(( _ram_fit > 14336 ? 14336 : _ram_fit ))
VM_RAM="${VM_RAM:-$(( _ram_fit > 6144 ? _ram_fit : 6144 ))}"
# Disk (QEMU backend): 140G virtual, qcow2 is thin - the host only pays for
# written clusters, and rotation_rate=1 + discard=unmap return freed guest
# space. Override via VM_DISK_SIZE. A blank guest needs far less; the value
# is kept generous because growing the disk later is the painful direction.
VM_DISK_SIZE="${VM_DISK_SIZE:-140G}"

case "$WIN_VARIANT" in
    win11)
        # Windows 11 Pro with the generic (non-activating) edition key in the
        # unattend - add a company key / activate for licensed permanent use.
        # ISO: official media, URL resolved via Fido (see download-images.sh).
        WIN_ISO="$E2E_IMAGES/win11.iso"
        WIN_ISO_URL=""   # resolved dynamically by download-images.sh via Fido
        UNATTEND_DIR="unattend-win11"
        # UEFI: Win11's supported firmware (Secure-Boot-capable, GPT disk).
        # On legacy BIOS the 25H2 media crashes in offline specialize
        # (tssysprep.dll RdpSysPrepRestoreOffline, 0xc0000005).
        WIN_FIRMWARE="uefi"
        ;;
    server2022)
        # Windows Server 2022 Evaluation (180 days). NOTE: evaluation
        # licensing - replace with a licensed image/key for permanent use.
        WIN_ISO="$E2E_IMAGES/win2022-eval.iso"
        WIN_ISO_URL="https://go.microsoft.com/fwlink/p/?LinkID=2195280&clcid=0x409&culture=en-us&country=US"
        UNATTEND_DIR="unattend"
        WIN_FIRMWARE="bios"
        ;;
    *) echo "Unknown WIN_VARIANT: $WIN_VARIANT" >&2; exit 1 ;;
esac
WIN_FIRMWARE="${WIN_FIRMWARE_OVERRIDE:-$WIN_FIRMWARE}"

# UEFI firmware (OVMF, ovmf apt package). Per-VM writable NVRAM copy lives
# next to the disk so boot entries survive restarts.
OVMF_CODE="/usr/share/OVMF/OVMF_CODE_4M.fd"
OVMF_VARS_TEMPLATE="/usr/share/OVMF/OVMF_VARS_4M.fd"
VM_NVRAM="$E2E_DISKS/$VM_NAME.OVMF_VARS.fd"
VIRTIO_ISO="$E2E_IMAGES/virtio-win.iso"
VIRTIO_ISO_URL="https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso"
UNATTEND_ISO="$E2E_RUN/unattend-$WIN_VARIANT.iso"

# Guest credentials (throwaway test VM - do not reuse anywhere).
# Must match unattend/autounattend.xml.
GUEST_USER="Administrator"
GUEST_PASS="WinVm-Lab!2026"

# Host ports forwarded into the guest (QEMU user-mode NAT). Per-variant
# defaults so both variants can run side by side; env still overrides.
# server2022: ssh 12222, rdp 13389, winrm 15985, VNC :1 (5901, noVNC 6080)
# win11:      ssh 12322, rdp 13489, winrm 15986, VNC :2 (5902, noVNC 6081)
if [ "$WIN_VARIANT" = "win11" ]; then
    PORT_SSH="${PORT_SSH:-12322}"       # -> guest 22
    PORT_RDP="${PORT_RDP:-13489}"       # -> guest 3389
    PORT_WINRM="${PORT_WINRM:-15986}"   # -> guest 5985
    VNC_DISPLAY="${VNC_DISPLAY:-2}"
else
    PORT_SSH="${PORT_SSH:-12222}"       # -> guest 22
    PORT_RDP="${PORT_RDP:-13389}"       # -> guest 3389
    PORT_WINRM="${PORT_WINRM:-15985}"   # -> guest 5985
    VNC_DISPLAY="${VNC_DISPLAY:-1}"     # VNC on host port 5901 (bind 0.0.0.0 → watch from agent-vm.mshome.net:5901)
fi

# From inside the guest, the Linux host is reachable at this address
# (QEMU slirp default gateway/host alias).
HOST_FROM_GUEST="${HOST_FROM_GUEST:-10.0.2.2}"


# --- helpers ----------------------------------------------------------------
QMP_SOCK="$E2E_RUN/$VM_NAME.qmp"
PID_FILE="$E2E_RUN/$VM_NAME.pid"
SERIAL_LOG="$E2E_RUN/$VM_NAME.serial.log"
# Under /var/lib/swtpm (not /opt): Ubuntu's swtpm AppArmor profile only permits
# the TPM state/socket under a fixed set of paths (/var/lib/swtpm, /tmp, ...).
TPM_DIR="/var/lib/swtpm/$VM_NAME"
TPM_SOCK="$TPM_DIR/swtpm.sock"

mkdir -p "$E2E_IMAGES" "$E2E_DISKS" "$E2E_RUN" "$E2E_RESULTS"

guest_ssh() {
    sshpass -p "$GUEST_PASS" ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        -o ConnectTimeout=10 -o LogLevel=ERROR -p "$PORT_SSH" "$GUEST_USER@127.0.0.1" "$@"
}

guest_scp() {
    sshpass -p "$GUEST_PASS" scp -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        -o LogLevel=ERROR -P "$PORT_SSH" "$@"
}

vm_running() {
    [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null
}

# Construct uses the child's LAN address and Windows' in-box Hyper-V drivers.
if [ "$VM_BACKEND" = construct ]; then
    . "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/construct.sh"
else
    vm_exists() { [ -f "$VM_DISK" ]; }
fi
