#!/usr/bin/env bash
#
# Guest end-to-end rig (docs/plans/construct-platform-design.md, 14.2).
#
# Brings up an Ubuntu guest, provisions it with this checkout's bin/provision.sh the
# way Provision-AgentVM.ps1 does, checks the result sentinel and exit code, reboots
# it, saves its manifest (bin/construct-manifest.sh), and removes the guest again.
#
#   bash test/e2e/guest-rig.sh [quick|full] [--backend child|local] [--keep]
#                              [--compare MANIFEST]
#   bash test/e2e/guest-rig.sh --backend local up [quick|full]
#   bash test/e2e/guest-rig.sh --backend local down
#
#   quick      small tool set (Claude Code only), T3 Code, VS Code server and the
#              Samba share off
#   full       the defaults of a new install: all agents, T3 Code, VS Code, Samba
#   --backend  child (default): an Ubuntu child VM on the host this primary runs on,
#              installed unattended from the autoinstall ISO.
#              local: a QEMU/KVM guest on this machine (/dev/kvm) from the Ubuntu
#              cloud image, 2 GB RAM for quick and 3 GB for full, SSH forwarded to
#              a port on 127.0.0.1. For machines without a Construct host, or a
#              primary that cannot reach its children.
#   --keep     leave the guest running for inspection (a child still expires with
#              its lifetime); the run prints how to reach and remove it
#   --compare  print the diff of this run's manifest against a saved one
#   up         local only: start a clean guest, print its SSH details as
#              CONSTRUCT_E2E_SSH_HOST, _PORT, _USER and _KEY (`eval` the output)
#              and leave it running; nothing is provisioned
#   down       local only: stop the guest of `up` (or of a `--keep` run) and
#              remove its disk and key
#
# child needs a primary with the right to create children (`construct vm identity`)
# and curl, jq, ssh, scp, xorriso, git and mkpasswd or openssl on this machine.
# local needs qemu-system-x86, qemu-utils and cloud-image-utils, read-write access to
# /dev/kvm, curl, ssh, scp, ssh-keygen, git and mkpasswd or openssl.
#
# Local guest: the cloud image (UBUNTU_RELEASE) is downloaded once and its checksum
# verified. Its first boot (cloud-init, snap seeding) is done once and kept as a
# prepared disk under CONSTRUCT_RIG_CACHE/local; every guest then runs on a
# throw-away qcow2 overlay of it with a NoCloud seed that carries a key made for that
# guest. Before the start the rig checks that the guest's memory is available and
# ends with `incomplete` when it is not. A test run always stops its QEMU process,
# also on failure; only `up` and `--keep` leave one running until `down`.
#
# Install media: the Ubuntu live-server ISO (UBUNTU_RELEASE, default 24.04) is
# downloaded once, made into an autoinstall ISO with bin/build-autoinstall-iso.sh
# and uploaded to the host once. Both stay cached for later runs: the ISOs under
# CONSTRUCT_RIG_CACHE, the upload on the host under the name construct-rig-<hash>
# (delete it with `construct vm media delete ID --yes`). Run artefacts (provisioning
# log, manifest, timings) go to CONSTRUCT_RIG_RUNS, outside the checkout.
#
# Exit: 0 passed; 1 failed; 2 incomplete (the host refused the child, or the local
# guest's memory is not available, so the required verification did not run);
# 64 usage.
#
set -uo pipefail
export LC_ALL=C

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
UBUNTU_RELEASE="${UBUNTU_RELEASE:-24.04}"
CACHE="${CONSTRUCT_RIG_CACHE:-${XDG_CACHE_HOME:-${HOME}/.cache}/construct-guest-rig}"
RUNS="${CONSTRUCT_RIG_RUNS:-${XDG_STATE_HOME:-${HOME}/.local/state}/construct-guest-rig/runs}"
LIFETIME="${CONSTRUCT_RIG_LIFETIME:-4h}"
SEED_USER=agent
SEED_PASS=agent
GUEST_HOSTNAME=construct-rig

variant=quick keep=false compare="" backend=child mode=run
while [[ $# -gt 0 ]]; do
  case "$1" in
    quick|full) variant="$1" ;;
    up|down) mode="$1" ;;
    --keep) keep=true ;;
    --backend) shift; [[ "${1:-}" == child || "${1:-}" == local ]] || { echo "--backend needs child or local" >&2; exit 64; }; backend="$1" ;;
    --compare) shift; [[ $# -gt 0 ]] || { echo "--compare needs a manifest file" >&2; exit 64; }; compare="$1" ;;
    -h|--help) sed -n '2,54p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1 (see --help)" >&2; exit 64 ;;
  esac
  shift
done
[[ -z "${compare}" || -f "${compare}" ]] || { echo "no such manifest: ${compare}" >&2; exit 64; }
[[ "${mode}" == run || "${backend}" == local ]] || { echo "${mode} needs --backend local" >&2; exit 64; }
[[ "${mode}" == run || ( "${keep}" == false && -z "${compare}" ) ]] || { echo "${mode} takes no --keep or --compare" >&2; exit 64; }

log()  { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
fail() { log "FAILED: $*"; exit 1; }
incomplete() { log "$*"; echo "incomplete: required verification not run" >&2; exit 2; }

if [[ "${backend}" == child ]]; then
  tools=(construct curl jq ssh scp xorriso git sha256sum)
else
  tools=(qemu-system-x86_64 qemu-img cloud-localds setpriv flock curl ssh scp ssh-keygen git sha256sum)
  [[ -r /dev/kvm && -w /dev/kvm ]] || fail "/dev/kvm is missing or not usable"
fi
for tool in "${tools[@]}"; do
  command -v "${tool}" >/dev/null 2>&1 || fail "${tool} is not installed"
done
command -v mkpasswd >/dev/null 2>&1 || command -v openssl >/dev/null 2>&1 || fail "need mkpasswd (whois) or openssl"

# --- stage timings -------------------------------------------------------------
declare -a STAGES=()
stage_name="" stage_start=0
stage() {
  local now; now="$(date +%s)"
  [[ -z "${stage_name}" ]] || STAGES+=("${stage_name}"$'\t'"$(( now - stage_start ))")
  stage_name="${1:-}"; stage_start="${now}"
  [[ -z "${stage_name}" ]] || log "== ${stage_name}"
}
print_timings() {
  stage ""
  local row total=0
  printf '\nStage timings (%s):\n' "${variant}"
  for row in "${STAGES[@]}"; do
    printf '  %-26s %6ss\n' "${row%%$'\t'*}" "${row##*$'\t'}"
    total=$(( total + ${row##*$'\t'} ))
  done
  printf '  %-26s %6ss\n' total "${total}"
}

# --- the child and its teardown -----------------------------------------------
child="" run_dir="" qemu_dir=""
teardown() {
  local rc=$?
  trap - EXIT INT TERM
  if [[ -n "${qemu_dir}" ]]; then
    if [[ "${keep}" == true ]]; then
      printf '%s\n' "${qemu_dir}" >"${LOCAL_CURRENT}"
      log "keeping the local guest: ssh ${SSH_OPTS[*]} ${SEED_USER}@${address}"
      log "stop it with: bash test/e2e/guest-rig.sh --backend local down"
    else
      [[ "${mode}" != run ]] || stage "teardown"
      local_remove "${qemu_dir}" || rc=1
    fi
  fi
  if [[ -n "${child}" ]]; then
    if [[ "${keep}" == true ]]; then
      log "keeping ${child}; delete it with: construct vm media release ${child}; construct vm delete ${child} --yes"
    else
      stage "teardown"
      construct vm media release "${child}" --json >/dev/null 2>&1 || true
      construct vm delete "${child}" --yes --json >/dev/null
      case $? in
        0|3) ;;  # deleted, or never created (not found)
        *) log "COULD NOT DELETE ${child}; delete it by hand: construct vm delete ${child} --yes"; rc=1 ;;
      esac
    fi
  fi
  if [[ -n "${run_dir}" ]]; then print_timings >"${run_dir}/timings.txt"; cat "${run_dir}/timings.txt" >&2; fi
  exit "${rc}"
}
trap teardown EXIT
trap 'exit 130' INT TERM

# --- ssh to the guest ----------------------------------------------------------
mkdir -p "${CACHE}" "${RUNS}"
# ssh options for a key and, for the local guest, the forwarded port. Port= rather
# than -p so the same options serve ssh and scp.
SSH_OPTS=()
use_key() {
  SSH_OPTS=(-i "$1" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=no
            -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=10
            -o ServerAliveInterval=30 -o ServerAliveCountMax=10)
  [[ -z "${2:-}" ]] || SSH_OPTS+=(-o "Port=$2")
}
if [[ "${backend}" == child ]]; then
  KEY="${CACHE}/bootstrap_ed25519"
  # The committed bootstrap key (keys/), copied because ssh refuses a key file others can read.
  install -m 600 "${REPO}/keys/bootstrap_ed25519" "${KEY}"
  use_key "${KEY}"
fi
address=""
gssh() { ssh "${SSH_OPTS[@]}" "${SEED_USER}@${address}" "$@"; }

guest_address() {
  construct vm addresses "${child}" --json 2>/dev/null |
    jq -r '[.addresses[]? | select((.family|ascii_downcase) == "ipv4") | .address][0] // empty'
}

# Wait until the guest reports an address and SSH answers there with the bootstrap key.
# The address comes from the guest agent of the installed system, which starts with
# sshd; an address that stays unreachable for 10 minutes means this primary has no
# route to the child, which is the host's network and not the guest under test.
wait_for_ssh() {
  [[ "${backend}" == child ]] || { local_wait_for_ssh "$1"; return; }
  local limit="$1" deadline seen=0
  deadline=$(( $(date +%s) + limit ))
  while (( $(date +%s) < deadline )); do
    address="$(guest_address)"
    if [[ -n "${address}" ]]; then
      gssh true 2>/dev/null && return 0
      (( seen > 0 )) || seen="$(date +%s)"
      if (( $(date +%s) - seen > 600 )) && ! timeout 5 bash -c "echo >/dev/tcp/${address}/22" 2>/dev/null; then
        incomplete "${child} reports ${address}, but this primary cannot reach it (no route from the primary's network to the child's)"
      fi
    fi
    sleep 15
  done
  return 1
}

# --- install media -------------------------------------------------------------
# Echoes the host media id of the rig's autoinstall ISO, building and uploading it
# only when the cache does not have it yet.
ensure_media() {
  local base="https://releases.ubuntu.com/${UBUNTU_RELEASE}/" sums line sha iso key name out tmp id
  sums="$(curl -fsSL "${base}SHA256SUMS")" || fail "could not read ${base}SHA256SUMS"
  line="$(grep -E ' \*?ubuntu-[0-9.]+-live-server-amd64\.iso$' <<<"${sums}" | sort -V -k2 | tail -1)"
  [[ -n "${line}" ]] || fail "no live-server ISO listed at ${base}"
  sha="${line%% *}"; iso="${line##* }"; iso="${iso#\*}"

  if [[ "$(cat "${CACHE}/${iso}.sha256" 2>/dev/null)" != "${sha}" ]]; then
    log "downloading ${iso}"
    curl -fsSL --retry 3 -o "${CACHE}/${iso}.part" "${base}${iso}" || fail "download of ${iso} failed"
    [[ "$(sha256sum "${CACHE}/${iso}.part" | cut -d' ' -f1)" == "${sha}" ]] || fail "${iso}: checksum mismatch"
    mv "${CACHE}/${iso}.part" "${CACHE}/${iso}"
    printf '%s\n' "${sha}" >"${CACHE}/${iso}.sha256"
    find "${CACHE}" -maxdepth 1 -name 'ubuntu-*.iso*' ! -name "${iso}*" -delete
  fi

  # The rig's ISO is named by what goes into it: the Ubuntu ISO, the builder and this
  # script's seed addition below.
  key="$(printf '%s\n' "${sha}" "$(sha256sum "${REPO}/bin/build-autoinstall-iso.sh")" "qemu-guest-agent v1" | sha256sum | cut -c1-12)"
  name="construct-rig-${key}"
  out="${CACHE}/${name}.iso"
  if [[ ! -f "${out}" ]]; then
    log "building ${name}.iso"
    tmp="${CACHE}/build.$$"; mkdir -p "${tmp}"
    VM_HOST="${GUEST_HOSTNAME}" VM_USER="${SEED_USER}" VM_PASS="${SEED_PASS}" \
      bash "${REPO}/bin/build-autoinstall-iso.sh" "${CACHE}/${iso}" "${tmp}/base.iso" >"${tmp}/build.log" 2>&1 ||
      { cat "${tmp}/build.log" >&2; rm -rf "${tmp}"; fail "build-autoinstall-iso.sh failed"; }
    # The one addition: QEMU's guest agent, through which a Proxmox host learns the
    # child's address (`construct vm addresses`). Everything else is the product's seed.
    xorriso -osirrox on -indev "${tmp}/base.iso" -extract /nocloud/user-data "${tmp}/user-data" >/dev/null 2>&1 ||
      { rm -rf "${tmp}"; fail "could not read the seed back"; }
    chmod u+w "${tmp}/user-data"
    printf '  packages:\n    - qemu-guest-agent\n' >>"${tmp}/user-data"
    xorriso -indev "${tmp}/base.iso" -outdev "${out}.part" -boot_image any replay \
      -map "${tmp}/user-data" /nocloud/user-data >/dev/null 2>&1 ||
      { rm -rf "${tmp}" "${out}.part"; fail "could not repack the ISO"; }
    rm -rf "${tmp}"
    mv "${out}.part" "${out}"
    find "${CACHE}" -maxdepth 1 -name 'construct-rig-*.iso' ! -name "${name}.iso" -delete
  fi

  id="$(construct vm media list --json | jq -r --arg n "${name}" '[.[] | select(.name == $n and .state == "ready") | .id][0] // empty')"
  if [[ -z "${id}" ]]; then
    log "uploading ${name}.iso to the host"
    id="$(construct vm media upload "${out}" --role install --name "${name}" --json | jq -r '.media.id // .id // empty')"
    [[ -n "${id}" ]] || id="$(construct vm media list --json | jq -r --arg n "${name}" '[.[] | select(.name == $n and .state == "ready") | .id][0] // empty')"
    [[ -n "${id}" ]] || fail "upload of ${name}.iso failed"
  fi
  printf '%s\n' "${id}"
}

# --- local backend: QEMU/KVM on this machine -----------------------------------
LOCAL_DIR="${CACHE}/local"
# The guest started by `up` or kept by `--keep`, for `down`: its directory.
LOCAL_CURRENT="${RUNS}/local-guest"
local_mem_mb() { [[ "${variant}" == quick ]] && echo 2048 || echo 3072; }

hash_password() {
  if command -v mkpasswd >/dev/null 2>&1; then mkpasswd -m sha-512 "$1"; else openssl passwd -6 "$1"; fi
}

# The guest's memory plus QEMU's own overhead must be available now; otherwise the
# run is incomplete, not failed: the code under test never ran.
local_check_memory() {
  local need avail
  need=$(( $(local_mem_mb) + 512 ))
  avail="$(awk '/^MemAvailable:/ { print int($2 / 1024) }' /proc/meminfo)"
  (( avail >= need )) ||
    incomplete "the local guest needs ${need} MB of free memory (guest $(local_mem_mb) MB plus QEMU), only ${avail} MB are available"
}

# Echoes the path of the cached cloud image, downloading it when the published
# checksum differs from the cached one.
local_cloud_image() {
  local base="https://cloud-images.ubuntu.com/releases/${UBUNTU_RELEASE}/release/" sums sha
  local img="ubuntu-${UBUNTU_RELEASE}-server-cloudimg-amd64.img"
  sums="$(curl -fsSL "${base}SHA256SUMS")" || fail "could not read ${base}SHA256SUMS"
  sha="$(awk -v f="${img}" '$2 == f || $2 == "*" f { print $1 }' <<<"${sums}")"
  [[ -n "${sha}" ]] || fail "no ${img} listed at ${base}"
  mkdir -p "${LOCAL_DIR}"
  if [[ "$(cat "${LOCAL_DIR}/${img}.sha256" 2>/dev/null)" != "${sha}" ]]; then
    log "downloading ${img}"
    curl -fsSL --retry 3 -o "${LOCAL_DIR}/${img}.part" "${base}${img}" || fail "download of ${img} failed"
    [[ "$(sha256sum "${LOCAL_DIR}/${img}.part" | cut -d' ' -f1)" == "${sha}" ]] ||
      { rm -f "${LOCAL_DIR:?}/${img:?}.part"; fail "${img}: checksum mismatch"; }
    mv "${LOCAL_DIR}/${img}.part" "${LOCAL_DIR}/${img}"
    printf '%s\n' "${sha}" >"${LOCAL_DIR}/${img}.sha256"
  fi
  printf '%s\n' "${LOCAL_DIR}/${img}"
}

# A NoCloud seed in DIR: the seed user of the autoinstall ISO (password, sudo by
# password, SSH with password and key), the rig's host name, and a key made for this
# guest. A new instance id per guest makes cloud-init apply the seed on the prepared
# disk again.
local_seed() {
  local dir="$1" instance="$2"
  ssh-keygen -q -t ed25519 -N '' -C "construct-rig" -f "${dir}/id_ed25519" || return 1
  cat >"${dir}/meta-data" <<EOF
instance-id: ${instance}
local-hostname: ${GUEST_HOSTNAME}
EOF
  cat >"${dir}/user-data" <<EOF
#cloud-config
hostname: ${GUEST_HOSTNAME}
manage_etc_hosts: true
ssh_pwauth: true
users:
  - name: ${SEED_USER}
    gecos: The Construct
    groups: [adm, sudo]
    shell: /bin/bash
    lock_passwd: false
    passwd: "$(hash_password "${SEED_PASS}")"
    ssh_authorized_keys:
      - $(cat "${dir}/id_ed25519.pub")
EOF
  cloud-localds "${dir}/seed.img" "${dir}/user-data" "${dir}/meta-data"
}

# Starts QEMU on DIR/disk.qcow2 and DIR/seed.img with SSH on a free localhost port
# (written to DIR/port). A guest that is to outlive the rig (`up`, `--keep`) is
# daemonized; any other gets SIGKILL when the rig's shell goes away, however it ends.
local_start() {
  local dir="$1" persistent="$2" port pid
  local -a cmd
  for _ in 1 2 3 4 5; do
    port=$(( 20000 + RANDOM % 20000 ))
    : >"${dir}/qemu.pid"
    # shellcheck disable=SC2054 # commas are QEMU option syntax
    cmd=(qemu-system-x86_64 -name construct-rig -machine q35,accel=kvm -cpu host -smp 2 -m "$(local_mem_mb)"
         -drive "file=${dir}/disk.qcow2,if=virtio,format=qcow2"
         -drive "file=${dir}/seed.img,if=virtio,format=raw,readonly=on"
         -netdev "user,id=net0,hostfwd=tcp:127.0.0.1:${port}-:22" -device virtio-net-pci,netdev=net0
         -display none -serial "file:${dir}/console.log" -pidfile "${dir}/qemu.pid")
    if [[ "${persistent}" == true ]]; then
      "${cmd[@]}" -daemonize 2>"${dir}/qemu.err" 9>&- && { printf '%s\n' "${port}" >"${dir}/port"; return 0; }
    else
      setpriv --pdeathsig KILL -- "${cmd[@]}" 2>"${dir}/qemu.err" &
      pid=$!
      # Up when the pid file is written; gone at once when the port was taken.
      while kill -0 "${pid}" 2>/dev/null && [[ ! -s "${dir}/qemu.pid" ]]; do sleep 0.2; done
      kill -0 "${pid}" 2>/dev/null && { printf '%s\n' "${port}" >"${dir}/port"; return 0; }
    fi
    grep -q 'hostfwd\|host forwarding' "${dir}/qemu.err" || break
  done
  cat "${dir}/qemu.err" >&2
  return 1
}

local_alive() {
  local pid
  pid="$(cat "$1/qemu.pid" 2>/dev/null)" || return 1
  [[ "${pid}" =~ ^[0-9]+$ ]] && kill -0 "${pid}" 2>/dev/null &&
    tr '\0' ' ' <"/proc/${pid}/cmdline" 2>/dev/null | grep -qF "file=$1/disk.qcow2,"
}

# Stops DIR's QEMU process: TERM, then KILL after 15 s. Fails when it is still there.
local_stop() {
  local dir="$1" pid
  local_alive "${dir}" || return 0
  pid="$(cat "${dir}/qemu.pid")"
  kill -TERM "${pid}" 2>/dev/null
  for _ in $(seq 30); do local_alive "${dir}" || return 0; sleep 0.5; done
  kill -KILL "${pid}" 2>/dev/null
  for _ in $(seq 20); do local_alive "${dir}" || return 0; sleep 0.5; done
  log "QEMU process ${pid} of ${dir} does not stop; kill it by hand"
  return 1
}

# Stops DIR's guest and removes its disk, seed and key; logs stay for the run.
local_remove() {
  local dir="${1:?}"
  local_stop "${dir}" || return 1
  rm -f "${dir:?}/disk.qcow2" "${dir:?}/seed.img" "${dir:?}/id_ed25519" "${dir:?}/id_ed25519.pub" "${dir:?}/qemu.pid"
  [[ "$(cat "${LOCAL_CURRENT}" 2>/dev/null)" != "${dir}" ]] || rm -f "${LOCAL_CURRENT:?}"
  [[ "${dir}" != "${LOCAL_DIR}"/prepare.* ]] || rm -rf "${dir:?}"
}

local_wait_for_ssh() {
  local deadline
  deadline=$(( $(date +%s) + $1 ))
  while (( $(date +%s) < deadline )); do
    local_alive "${qemu_dir}" || { tail -20 "${qemu_dir}/console.log" >&2; fail "the local guest's QEMU process ended"; }
    gssh true 2>/dev/null && return 0
    sleep 2
  done
  return 1
}

# Echoes the prepared disk: the cloud image after its first boot (cloud-init done,
# snaps seeded, shut down cleanly), made once per cloud image and seed format.
local_prepared() {
  local img="$1" key prepared dir
  key="$(printf '%s\n' "$(cat "${img}.sha256")" "seed v1" | sha256sum | cut -c1-12)"
  prepared="${LOCAL_DIR}/prepared-${key}.qcow2"
  if [[ ! -f "${prepared}" ]]; then
    log "first boot of the cloud image, kept as prepared-${key}.qcow2"
    dir="${LOCAL_DIR}/prepare.$$"
    mkdir -p "${dir}"
    qemu_dir="${dir}"
    qemu-img create -q -f qcow2 -F qcow2 -b "${img}" "${dir}/disk.qcow2" 25G || fail "qemu-img create failed"
    local_seed "${dir}" "construct-rig-prepare-$$" || fail "could not build the seed"
    local_check_memory
    local_start "${dir}" false || fail "QEMU did not start"
    use_key "${dir}/id_ed25519" "$(cat "${dir}/port")"; address=127.0.0.1
    local_wait_for_ssh 600 || fail "no SSH on the first boot within 10 minutes (console: ${dir}/console.log)"
    # Wait for cloud-init and snap seeding, drop this boot's key from the disk and
    # power off, in one call: without the key there is no second one.
    [[ "$(printf '%s\n' "${SEED_PASS}" |
      gssh "cloud-init status --wait >/dev/null; sudo -S -p '' bash -c 'snap wait system seed.loaded && : >/home/${SEED_USER}/.ssh/authorized_keys && sync && echo prepared && systemctl poweroff --no-block'" 2>&1)" == *prepared* ]] ||
      fail "the first boot did not finish"
    for _ in $(seq 120); do local_alive "${dir}" || break; sleep 1; done
    ! local_alive "${dir}" || fail "the guest did not power off after its first boot"
    mv "${dir}/disk.qcow2" "${prepared}"
    chmod 444 "${prepared}"
    local_remove "${dir}"; qemu_dir=""
    find "${LOCAL_DIR}" -maxdepth 1 -name 'prepared-*.qcow2' ! -name "prepared-${key}.qcow2" -delete
  fi
  printf '%s\n' "${prepared}"
}

# Brings a clean guest up in DIR and waits for SSH; sets qemu_dir, address, SSH_OPTS.
local_up() {
  local dir="$1" persistent="$2" img prepared
  # One guest outlives the rig at a time: hold a lock from this check until the
  # rig exits, after it has recorded the guest for `down`.
  if [[ "${persistent}" == true ]]; then
    exec 9>"${LOCAL_CURRENT}.lock"
    flock -n 9 || fail "another rig is bringing up a local guest to keep; try again when it is done"
  fi
  if [[ "${persistent}" == true && -f "${LOCAL_CURRENT}" ]] && local_alive "$(cat "${LOCAL_CURRENT}")"; then
    fail "a local guest is already up ($(cat "${LOCAL_CURRENT}")); stop it with: bash test/e2e/guest-rig.sh --backend local down"
  fi
  local_check_memory
  stage "image"
  img="$(local_cloud_image)" || exit 1
  local_prepared "${img}" >"${dir}/prepared" || exit 1
  prepared="$(cat "${dir}/prepared")"
  stage "boot"
  qemu_dir="${dir}"
  qemu-img create -q -f qcow2 -F qcow2 -b "${prepared}" "${dir}/disk.qcow2" || fail "qemu-img create failed"
  local_seed "${dir}" "construct-rig-$(basename "${dir}")" || fail "could not build the seed"
  local_check_memory
  local_start "${dir}" "${persistent}" || fail "QEMU did not start"
  use_key "${dir}/id_ed25519" "$(cat "${dir}/port")"; address=127.0.0.1
  local_wait_for_ssh 300 || fail "no SSH on the local guest within 5 minutes (console: ${dir}/console.log)"
  log "local guest answers at 127.0.0.1:$(cat "${dir}/port")"
}

# --- provisioning settings, as Provision-AgentVM.ps1 passes them ------------------
provision_env() {
  local commit
  commit="$(git -C "${REPO}" rev-parse HEAD)"
  local -a env=(
    "PROJECTS=default" "SSH_USER=${SEED_USER}" "AGENT_NAME=${GUEST_HOSTNAME}-agent" "CLAUDE_USER=root"
    "GIT_USER_NAME_B64=" "GIT_USER_EMAIL_B64=" "GIT_CREDENTIAL_STORE=" "GIT_CLONE_CREDENTIALS_B64="
    "GIT_CLONE_SKIP_HOSTS_B64=" "CHECKOUT_PROJECTS=false" "SETUP_ROOT_SSH_KEY=true"
    "VSCODE_TUNNEL=false" "VSCODE_SERVE_WEB_TOKEN_B64=" "VSCODE_CLIENT_COMMIT="
    "CONSTRUCT_VERSION=${commit}" "CLAUDE_PARTIAL_STREAMING=true" "MIC_PASSTHROUGH=false"
    "OPENCODE_BACKGROUND_WATCHER=" "T3CODE_CHANNEL=" "T3CODE_BUILD_SOURCE=" "T3CODE_PATCHED="
    "T3CODE_AUTO_APPROVE=" "T3CODE_HTTPS=" "DOCKER_REGISTRY_MIRRORS=" "T3CODE_BUILD_MODE=server"
    "REBOOT_AFTER_PROVISION=true"
  )
  if [[ "${variant}" == quick ]]; then
    env+=("AI_TOOLS=claude-code" "VSCODE_SERVER=false" "VSCODE_SERVE_WEB=false" "SMB_SHARE=false" "T3CODE=false")
  else
    env+=("AI_TOOLS=opencode,claude-code,codex" "VSCODE_SERVER=true" "VSCODE_SERVE_WEB=true" "SMB_SHARE=true" "T3CODE=true")
  fi
  local item out=""
  for item in "${env[@]}"; do out+=" ${item%%=*}='${item#*=}'"; done
  printf '%s' "${out# }"
}

# ==============================================================================
if [[ "${mode}" == down ]]; then
  dir="$(cat "${LOCAL_CURRENT}" 2>/dev/null)"
  [[ -n "${dir}" ]] || { log "no local guest is up"; exit 0; }
  local_remove "${dir}" || exit 1
  log "stopped the local guest ${dir}"
  exit 0
fi

if [[ "${backend}" == local ]]; then
  child_name="local-$(date +%m%d%H%M%S)-$(od -An -N2 -tx1 /dev/urandom | tr -d ' \n')"
  persistent="${keep}"; [[ "${mode}" == run ]] || persistent=true
  dir="${RUNS}/${child_name}"
  mkdir -p "${dir}"
  if [[ "${mode}" == run ]]; then run_dir="${dir}"; log "local guest (${variant}), run directory ${run_dir}"; fi
  local_up "${dir}" "${persistent}"
  if [[ "${mode}" == up ]]; then
    printf '%s\n' "${dir}" >"${LOCAL_CURRENT}"
    qemu_dir=""   # handed over to `down`; nothing for the teardown to do
    printf 'export CONSTRUCT_E2E_SSH_HOST=127.0.0.1\n'
    printf 'export CONSTRUCT_E2E_SSH_PORT=%s\n' "$(cat "${dir}/port")"
    printf 'export CONSTRUCT_E2E_SSH_USER=%q\n' "${SEED_USER}"
    printf 'export CONSTRUCT_E2E_SSH_KEY=%q\n' "${dir}/id_ed25519"
    exit 0
  fi
  child_label="the local guest"
else
stage "media"
media_id="$(ensure_media)" || exit 1
log "install media ${media_id}"

stage "create"
primary="$(construct vm identity --json | jq -r '.vmName // empty')" || true
[[ -n "${primary}" ]] || incomplete "this machine is not a primary with a host service"
child="${primary}-rig-$(date +%m%d%H%M%S)-$(od -An -N2 -tx1 /dev/urandom | tr -d ' \n')"
run_dir="${RUNS}/${child}"
mkdir -p "${run_dir}"
log "creating ${child} (${variant}), run directory ${run_dir}"
construct vm create --name "${child}" --media "${media_id}" --preset linux \
  --ram-gb 3 --cpus 2 --disk-gb 25 --lifetime "${LIFETIME}" \
  --boot-order disk,installMedia --json >"${run_dir}/create.json" 2>"${run_dir}/create.err"
rc=$?
if [[ ${rc} -ne 0 ]]; then
  cat "${run_dir}/create.err" >&2
  case "${rc}" in
    4|5|9|10|11) incomplete "the host refused the child (construct vm create exit ${rc})" ;;
    *) fail "construct vm create exited ${rc}" ;;
  esac
fi

stage "install"
# The disk is empty on the first boot, so the firmware falls through to the ISO; the
# installer's own reboot then boots the installed system from disk.
wait_for_ssh 3600 || fail "no SSH on ${child} within 60 minutes (address: ${address:-none reported})"
log "${child} answers at ${address}"
construct vm media release "${child}" --json >/dev/null || log "media release failed (retried at teardown)"
child_label="${child}"
fi

stage "copy source"
# This checkout's source files (tracked and untracked, not ignored), packed and
# unpacked into /opt/construct/repo as Provision-AgentVM.ps1 does.
( cd "${REPO}" && git ls-files -z --cached --others --exclude-standard |
    grep -zv -e '\.iso$' -e '^\.construct-settings\.json$' -e '^\.construct-backup/' |
    xargs -0 tar -czf "${run_dir}/construct-repo.tar.gz" --no-recursion -- ) ||
  fail "could not pack the checkout"
scp "${SSH_OPTS[@]}" -q "${run_dir}/construct-repo.tar.gz" "${SEED_USER}@${address}:/tmp/construct-repo.tar.gz" ||
  fail "could not copy the source to the guest"
printf '%s\n' "${SEED_PASS}" | gssh "sudo -S -p '' bash -c 'mkdir -p /opt/construct && rm -rf /opt/construct/repo && mkdir -p /opt/construct/repo && tar -xzf /tmp/construct-repo.tar.gz -C /opt/construct/repo && chown -R ${SEED_USER}:${SEED_USER} /opt/construct'" ||
  fail "could not unpack the source on the guest"

stage "provision"
command="$(provision_env) bash /opt/construct/repo/bin/provision.sh"
printf '%s\n' "${SEED_PASS}" |
  gssh "sudo -S -p '' env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive bash -lc \"${command}\"" \
  >"${run_dir}/provision.log" 2>&1
provision_rc=$?
log "provision.sh exited ${provision_rc}; log: ${run_dir}/provision.log"

# The result block (bin/provision.sh _print_machine_result), colour codes stripped.
result="$(sed 's/\x1b\[[0-9;]*[A-Za-z]//g; s/\r$//' "${run_dir}/provision.log" |
  sed -n '/^===CONSTRUCT-PROVISION-RESULT===$/,/^===END-CONSTRUCT-PROVISION-RESULT===$/p')"
printf '%s\n' "${result}" >"${run_dir}/result.txt"
errors="$(sed -n 's/^errors=//p' <<<"${result}")"
grep '^error=' <<<"${result}" >&2
[[ "$(tail -1 <<<"${result}")" == "===END-CONSTRUCT-PROVISION-RESULT===" && "${errors}" =~ ^[0-9]+$ &&
   "$(grep -c '^error=' <<<"${result}")" -eq "${errors}" ]] || fail "no valid result sentinel (exit ${provision_rc})"
[[ ${provision_rc} -eq 0 && ${errors} -eq 0 ]] || fail "provisioning reported ${errors} failed step(s), exit ${provision_rc}"

stage "reboot"
# The bootstrap path of Provision-AgentVM.ps1 ends with a reboot; the manifest is
# taken after it so unit states are those of a booted, provisioned guest.
boot_id="$(gssh cat /proc/sys/kernel/random/boot_id)" || fail "could not read the boot id"
gssh "sudo -n systemctl reboot" >/dev/null 2>&1 || true
sleep 20
wait_for_ssh 900 || fail "no SSH on ${child_label} after the reboot"
[[ "$(gssh cat /proc/sys/kernel/random/boot_id)" != "${boot_id}" ]] || fail "${child_label} did not reboot"

stage "manifest"
gssh "sudo -n bash -s" <"${REPO}/bin/construct-manifest.sh" >"${run_dir}/manifest.txt" 2>"${run_dir}/manifest.err" ||
  fail "manifest failed: $(tail -3 "${run_dir}/manifest.err")"
log "manifest: ${run_dir}/manifest.txt ($(wc -l <"${run_dir}/manifest.txt") lines)"
if [[ -n "${compare}" ]]; then
  diff -u "${compare}" "${run_dir}/manifest.txt" >"${run_dir}/manifest.diff"
  log "manifest diff against ${compare}: $(grep -c '^[-+][^-+]' "${run_dir}/manifest.diff") changed line(s)"
  cat "${run_dir}/manifest.diff"
fi

log "PASSED (${variant})"
exit 0
