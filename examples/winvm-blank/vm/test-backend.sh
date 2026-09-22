#!/usr/bin/env bash
# Local self-test of the backend wiring (defaults, address discovery, SSH/SCP
# rewriting, error propagation) with a mocked construct CLI - no Windows VM,
# no network. Run it after editing config.sh or construct.sh.
set -euo pipefail
vm=$(cd "$(dirname "$0")" && pwd)
scratch=$(mktemp -d)
trap 'rm -r "$scratch"' EXIT
mkdir "$scratch/bin"
cat > "$scratch/bin/construct" <<'MOCK'
#!/usr/bin/env bash
case "$*" in
  'vm list --json')
    if [ "${FAIL_INVENTORY:-0}" = 1 ]; then exit 8; fi
    echo '[{"name":"winvm-win11","state":"running"}]' ;;
  'vm addresses winvm-win11 --json') echo '{"addresses":[{"address":"fe80::1"},{"address":"169.254.3.4"},{"address":"172.22.1.20"}]}' ;;
  *) printf 'Unexpected construct call: %s\n' "$*" >&2; exit 99 ;;
esac
MOCK
cat > "$scratch/bin/sshpass" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$@"
printf 'cwd=%s\n' "$PWD"
MOCK
chmod +x "$scratch/bin/construct" "$scratch/bin/sshpass"
export PATH="$scratch/bin:$PATH" E2E_HOME="$scratch/state"
unset VM_BACKEND VM_CPUS VM_RAM VM_DISK_SIZE
source "$vm/config.sh"
[[ "$VM_BACKEND" = construct && -z "$VM_CPUS" && "$VM_RAM" = 12288 && "$VM_DISK_SIZE" = 100G ]]
vm_exists
vm_running
[[ $(guest_address) = 172.22.1.20 ]]
output=$(guest_scp '/tmp/local file.zip' 'Administrator@127.0.0.1:C:/work/payload.zip')
[[ "$output" = *'Administrator@172.22.1.20:C:/work/payload.zip'* && "$output" = *'/tmp/local file.zip'* ]]
output=$(guest_scp -r 'Administrator@127.0.0.1:C:/results/*' /tmp/results)
[[ "$output" = *'Administrator@172.22.1.20:C:/results/*'* ]]
output=$(guest_ssh 'echo up')
[[ "$output" = *'Administrator@172.22.1.20'* && "$output" = *'echo up'* ]]
output=$(cd "$scratch" && "$vm/vm.sh" scp relative.zip 'Administrator@127.0.0.1:C:/work/payload.zip')
[[ "$output" = *'relative.zip'* && "$output" = *"cwd=$scratch"* ]]
export FAIL_INVENTORY=1
rc=0; vm_exists || rc=$?
[[ "$rc" = 8 ]]   # An unavailable inventory must never look like "VM missing".
rc=0; "$vm/vm.sh" snap golden > "$scratch/snap.log" 2>&1 || rc=$?
[[ "$rc" = 2 ]]
export VM_BACKEND=qemu
unset VM_RAM VM_DISK_SIZE VM_CPUS
source "$vm/config.sh"
[[ "$VM_BACKEND" = qemu && "$VM_CPUS" = "$(nproc)" && "$VM_DISK_SIZE" = 140G ]]
echo 'PASS: backend defaults, IPv4 discovery, SSH, SCP both directions, inventory errors, QEMU override'
