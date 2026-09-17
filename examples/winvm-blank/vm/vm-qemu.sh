#!/usr/bin/env bash
# Lifecycle helper for the Windows test VM.
#
#   ./vm.sh start                 boot from disk (normal operation)
#   ./vm.sh stop                  graceful shutdown (falls back to kill)
#   ./vm.sh kill                  hard power-off
#   ./vm.sh status                running? + snapshots
#   ./vm.sh ssh [cmd...]          interactive shell / run command in guest
#   ./vm.sh scp <src> <dst>       copy (use host:path syntax of scp; port preset)
#   ./vm.sh snap <name>           take offline snapshot (VM must be stopped)
#   ./vm.sh revert <name>         revert disk to snapshot (VM must be stopped)
#   ./vm.sh screenshot <out.ppm>  dump the guest screen via QMP
set -euo pipefail
cd "$(dirname "$0")"
export VM_BACKEND=qemu
. ./config.sh

qmp() {  # send one QMP command
    printf '{"execute":"qmp_capabilities"}\n{"execute":%s}\n' "$1" |
        timeout 10 socat - "UNIX-CONNECT:$QMP_SOCK" 2>/dev/null
}

launch() {
    vm_running && { echo "already running (pid $(cat "$PID_FILE"))"; exit 0; }

    # UEFI (OVMF) for variants that need it (win11): CODE read-only, per-VM
    # writable NVRAM copy so boot entries survive restarts.
    local fw_args=()
    if [ "${WIN_FIRMWARE:-bios}" = "uefi" ]; then
        [ -f "$OVMF_CODE" ] || { echo "OVMF not installed (apt-get install -y ovmf)" >&2; exit 1; }
        [ -f "$VM_NVRAM" ] || cp "$OVMF_VARS_TEMPLATE" "$VM_NVRAM"
        fw_args=(-drive "if=pflash,format=raw,readonly=on,file=$OVMF_CODE"
                 -drive "if=pflash,format=raw,file=$VM_NVRAM")
    fi

    # Virtual TPM 2.0 via swtpm (Win11 hardware requirement).
    local tpm_args=()
    if command -v swtpm >/dev/null 2>&1; then
        mkdir -p "$TPM_DIR"
        pgrep -f "swtpm.*$TPM_SOCK" >/dev/null 2>&1 || \
            swtpm socket --tpm2 --tpmstate "dir=$TPM_DIR" \
                --ctrl "type=unixio,path=$TPM_SOCK" --daemon --pid "file=$TPM_DIR/swtpm.pid"
        tpm_args=(-chardev "socket,id=chrtpm,path=$TPM_SOCK"
                  -tpmdev "emulator,id=tpm0,chardev=chrtpm"
                  -device "tpm-tis,tpmdev=tpm0")
    fi

    qemu-system-x86_64 \
        -name "$VM_NAME" \
        -machine q35,accel=kvm \
        -cpu host,hv_relaxed,hv_spinlocks=0x1fff,hv_vapic,hv_time \
        "${fw_args[@]}" \
        "${tpm_args[@]}" \
        -smp "$VM_CPUS" -m "$VM_RAM" \
        -drive "file=$VM_DISK,if=none,id=sysdisk,cache=unsafe,discard=unmap" \
        -device ahci,id=ahci0 -device ide-hd,drive=sysdisk,bus=ahci0.0,rotation_rate=1 \
        -netdev "user,id=n0,hostfwd=tcp::${PORT_SSH}-:22,hostfwd=tcp::${PORT_RDP}-:3389,hostfwd=tcp::${PORT_WINRM}-:5985" \
        -device e1000e,netdev=n0 \
        -device "VGA,vgamem_mb=64,edid=on,xres=${VM_XRES:-1920},yres=${VM_YRES:-1080}" \
        -display none -vnc "0.0.0.0:$VNC_DISPLAY" \
        -usb -device usb-tablet \
        -qmp "unix:$QMP_SOCK,server,nowait" \
        -serial "file:$SERIAL_LOG" \
        -rtc base=localtime \
        -pidfile "$PID_FILE" -daemonize \
        "$@"
    echo "started (VNC port $((5900 + VNC_DISPLAY)), ssh -p $PORT_SSH, rdp $PORT_RDP)"
}

case "${1:-}" in
    _launch) shift; launch "$@" ;;   # internal: used by create-vm.sh to add CDs
    start)   launch ;;
    stop)
        vm_running || { echo "not running"; exit 0; }
        guest_ssh "shutdown /s /t 3" 2>/dev/null || qmp '"system_powerdown"' >/dev/null || true
        # Generous wait: after big installs Windows can spend many minutes in
        # "processing pending operations" during shutdown.
        for _ in $(seq 1 120); do vm_running || { echo "stopped"; exit 0; }; sleep 5; done
        echo "graceful shutdown timed out; killing"
        kill "$(cat "$PID_FILE")"
        # Wait for QEMU to actually exit - it holds the image write lock until
        # then (a snapshot right after the kill fails otherwise).
        for _ in $(seq 1 24); do vm_running || { echo "killed"; exit 0; }; sleep 5; done
        kill -9 "$(cat "$PID_FILE")" 2>/dev/null || true
        sleep 2 ;;
    kill)
        vm_running && kill "$(cat "$PID_FILE")"; echo "killed" ;;
    status)
        if vm_running; then
            echo "RUNNING (pid $(cat "$PID_FILE")) - snapshots not listable while running"
        elif [ -f "$VM_DISK" ]; then
            qemu-img snapshot -l "$VM_DISK"
        else
            echo "STOPPED - no disk yet"
        fi ;;
    ssh)     shift; guest_ssh "$@" ;;
    scp)     shift; guest_scp "$@" ;;
    snap)
        vm_running && { echo "stop the VM first" >&2; exit 1; }
        qemu-img snapshot -c "$2" "$VM_DISK"; qemu-img snapshot -l "$VM_DISK" ;;
    revert)
        vm_running && { echo "stop the VM first" >&2; exit 1; }
        qemu-img snapshot -a "$2" "$VM_DISK"
        # The guest's work tree just changed under the sync baseline - a later
        # delta sync against the stale listing would leave stale files.
        rm -f "$E2E_RUN/last-sync.list" "$E2E_RUN/last-sync.origin"
        echo "reverted to $2" ;;
    screenshot)
        qmp "\"screendump\", \"arguments\":{\"filename\":\"$(realpath -m "${2:-$E2E_RUN/screen.ppm}")\"}" >/dev/null
        echo "written: ${2:-$E2E_RUN/screen.ppm}" ;;
    web)
        # Browser-based viewer for the guest desktop (noVNC -> QEMU VNC).
        # Port follows the variant's VNC display: 6080 for :1, 6081 for :2, ...
        novnc_port=$((6079 + VNC_DISPLAY))
        pgrep -f "websockify.*$novnc_port" >/dev/null || {
            nohup websockify --web /usr/share/novnc "0.0.0.0:$novnc_port" "localhost:$((5900 + VNC_DISPLAY))" \
                >"/var/log/websockify-$novnc_port.log" 2>&1 &
            sleep 1
        }
        echo "open: http://$(hostname -f 2>/dev/null || hostname):$novnc_port/vnc.html?autoconnect=true&resize=scale" ;;
    *) grep '^#   ' "$0" | sed 's/^#   //' ;;
esac
