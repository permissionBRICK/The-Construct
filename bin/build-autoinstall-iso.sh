#!/usr/bin/env bash
#
# Build an Ubuntu autoinstall ISO that installs a *blank* Ubuntu base with the
# username / password / hostname preconfigured, an SSH server enabled, and a
# console login banner telling the user to finish setup by running
# Provision-AgentVM.ps1 from their Windows host.
#
# It does NOT install the agent stack — that is done later by the host-side
# PowerShell script (which can reach this VM over SSH thanks to the seed
# credentials baked in here).
#
# Requires: xorriso, and mkpasswd (whois) or openssl for password hashing.
#
#   sudo apt-get install -y xorriso whois
#   bash bin/build-autoinstall-iso.sh [SOURCE_ISO] [OUTPUT_ISO]
#
set -euo pipefail

# --- Parameters (override via env) ------------------------------------------
VM_USER="${VM_USER:-agent}"
VM_PASS="${VM_PASS:-agent}"
VM_HOST="${VM_HOST:-agent-vm}"          # short hostname; Hyper-V exposes <host>.mshome.net
VM_REALNAME="${VM_REALNAME:-The Construct}"
# Install source: 'ubuntu-server-minimal' (minimized, no-human-login footprint)
# or 'ubuntu-server' (the standard curated default). See /casper/install-sources.yaml.
SOURCE_ID="${SOURCE_ID:-ubuntu-server-minimal}"
SEED_DIR_NAME="nocloud"

# --- Identity source (plan section 4.10) -------------------------------------
# WHERE THE GUEST GETS ITS HOSTNAME. Two modes, and the default is today's:
#
#   ''  /  'static'   the hostname is baked into the seed from VM_HOST. Every local
#                     install: one ISO per VM, built by the caller that knows the name.
#   'hyperv-kvp'      GENERIC media: the seed carries the placeholder below and the
#                     guest adopts the Hyper-V VM name at first boot, read from the
#                     KVP data-exchange pool. This is what lets ONE pre-built ISO
#                     serve every VM on a host service (the service reaches a VM as
#                     <vm name>.mshome.net, which is the guest's OWN hostname as the
#                     Default Switch's DNS learned it).
#
# Planned third value: 'cloud-init-metadata' for Proxmox / NoCloud / ConfigDrive,
# where the hypervisor supplies per-VM identity natively. The media stays generic in
# every non-static mode; only the first-boot source differs, and it is one function
# in the installed script.
VM_HOSTNAME_SOURCE="${VM_HOSTNAME_SOURCE:-}"
# Placeholder hostname of generic media. It is what `hostname` reports until the
# first-boot unit has adopted the real one, and bin/provision.sh knows it too.
SEED_HOSTNAME="construct-seed"

case "${VM_HOSTNAME_SOURCE}" in
  ''|static|hyperv-kvp) ;;
  *)
    echo "Unknown VM_HOSTNAME_SOURCE '${VM_HOSTNAME_SOURCE}'. Use '' (static, from VM_HOST) or 'hyperv-kvp'." >&2
    exit 1
    ;;
esac

# Everything below asks this one question rather than re-testing the mode.
GENERIC_MEDIA=0
if [[ -n "${VM_HOSTNAME_SOURCE}" && "${VM_HOSTNAME_SOURCE}" != "static" ]]; then
  GENERIC_MEDIA=1
fi

# The hostname that goes into the seed: the real one, or the placeholder.
IDENTITY_HOSTNAME="${VM_HOST}"
if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  IDENTITY_HOSTNAME="${SEED_HOSTNAME}"
fi

SRC_ISO="${1:-}"
OUT_ISO="${2:-}"

# --- Locate tools and source ISO --------------------------------------------
if ! command -v xorriso >/dev/null 2>&1; then
  echo "xorriso not found. Install it: sudo apt-get install -y xorriso" >&2
  exit 1
fi

# Default source ISO: a single ubuntu *-live-server-*.iso next to the repo.
if [[ -z "${SRC_ISO}" ]]; then
  shopt -s nullglob
  candidates=( /opt/construct/ubuntu-*-live-server-*.iso /opt/construct/*.iso )
  shopt -u nullglob
  if [[ "${#candidates[@]}" -eq 0 ]]; then
    echo "No source ISO given and none found in /opt/construct. Pass it as arg 1." >&2
    exit 1
  fi
  SRC_ISO="${candidates[0]}"
fi
if [[ ! -f "${SRC_ISO}" ]]; then
  echo "Source ISO not found: ${SRC_ISO}" >&2
  exit 1
fi

if [[ -z "${OUT_ISO}" ]]; then
  OUT_ISO="$(dirname "${SRC_ISO}")/${VM_HOST}-autoinstall.iso"
fi

echo "==> Source ISO : ${SRC_ISO}"
echo "==> Output ISO : ${OUT_ISO}"
if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  echo "==> Identity   : user=${VM_USER} host=<adopted at first boot: ${VM_HOSTNAME_SOURCE}> (password preset)"
else
  echo "==> Identity   : user=${VM_USER} host=${VM_HOST} (password preset)"
fi
echo "==> Source     : ${SOURCE_ID}"

# --- Hash the password ------------------------------------------------------
hash_password() {
  if command -v mkpasswd >/dev/null 2>&1; then
    mkpasswd -m sha-512 "$1"
  elif command -v openssl >/dev/null 2>&1; then
    openssl passwd -6 "$1"
  else
    echo "Need mkpasswd (whois) or openssl to hash the password." >&2
    exit 1
  fi
}
PASS_HASH="$(hash_password "${VM_PASS}")"

# --- Work area --------------------------------------------------------------
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
mkdir -p "${WORK}/${SEED_DIR_NAME}"

# --- Console login banner (written into the target by late-commands) --------
# Shown by getty at the console via /etc/issue.d/construct.issue — the same
# file the real provisioner later overwrites with live service info, so it is
# naturally replaced once setup completes.
BANNER_FILE="${WORK}/construct.issue"
# Generic media does not know the name yet -- the guest adopts it at first boot.
BANNER_TARGET="${VM_HOST}.mshome.net"
if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  BANNER_TARGET="<this VM's name>.mshome.net"
fi
cat >"${BANNER_FILE}" <<EOF

============================================================
  The Construct VM  -  base image installed
  SETUP IS NOT COMPLETE YET
============================================================

  This is a blank Ubuntu base. Finish provisioning from your
  Windows host (outside Hyper-V) so the agent tools here AND
  your host's SSH key + VS Code are all configured:

    1. Open PowerShell in your local checkout of the
       construct repo.
    2. Run:   .\\Provision-AgentVM.ps1

  Target : ${BANNER_TARGET}   (seed user: ${VM_USER})

  The script uploads the repo, provisions this VM, retrieves
  the root SSH key, and wires up your host for SSH + VS Code.
============================================================

EOF
BANNER_B64="$(base64 -w0 "${BANNER_FILE}")"

# --- meta-data (NoCloud requires it to exist; instance-id is enough) --------
cat >"${WORK}/${SEED_DIR_NAME}/meta-data" <<EOF
instance-id: ${IDENTITY_HOSTNAME}
local-hostname: ${IDENTITY_HOSTNAME}
EOF

# --- Bootstrap SSH public key (pre-seeded for host-side provisioning) -------
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# Path to the bootstrap public key. Overridable via env so callers that run a
# normalized copy of this script from a different directory (e.g. the Windows
# Auto-Install.ps1 wrapper) can still point at the real keys/ dir.
BOOTSTRAP_PUBKEY_FILE="${BOOTSTRAP_PUBKEY_FILE:-${SCRIPT_DIR}/../keys/bootstrap_ed25519.pub}"
if [[ ! -f "${BOOTSTRAP_PUBKEY_FILE}" ]]; then
  echo "Bootstrap public key not found: ${BOOTSTRAP_PUBKEY_FILE}" >&2
  echo "Generate it with: ssh-keygen -t ed25519 -N '' -C bootstrap@construct -f keys/bootstrap_ed25519" >&2
  exit 1
fi
BOOTSTRAP_PUBKEY="$(cat "${BOOTSTRAP_PUBKEY_FILE}")"

# --- First-boot identity adoption (generic media only) -----------------------
# Written here, installed into the target by the extra late-commands below. Both
# files are carried as base64 for the same reason the banner is: a shell one-liner
# in YAML cannot hold a script without somebody eventually getting the quoting
# wrong, and base64 is inert.
if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  # The unit: 'source -> hostname', run once, then disabled. EnvironmentFile picks
  # the source, so a second source (cloud-init-metadata) is a different value here
  # and one more case in the script -- nothing else changes.
  cat >"${WORK}/construct-hostname.service" <<'UNIT'
[Unit]
Description=Construct: adopt the hostname this VM was given by the hypervisor
Wants=hv-kvp-daemon.service network-online.target
After=hv-kvp-daemon.service network-online.target
ConditionPathExists=!/var/lib/construct/hostname-adopted

[Service]
Type=oneshot
RemainAfterExit=yes
EnvironmentFile=-/etc/default/construct-hostname
ExecStart=/usr/local/sbin/construct-hostname.sh

[Install]
WantedBy=multi-user.target
UNIT

  # Quoted delimiter: nothing in here is expanded at build time. This script runs in
  # the GUEST, where the only tools guaranteed to exist are the ones a minimized
  # Ubuntu server ships -- no python.
  cat >"${WORK}/construct-hostname.sh" <<'GUESTSCRIPT'
#!/bin/sh
#
# Construct: adopt this VM's identity from the hypervisor's own channel.
#
# The install media is GENERIC -- it carries the placeholder hostname
# 'construct-seed' -- and every VM built from it learns its real name here, at first
# boot. That name matters beyond cosmetics: the guest registers it with the virtual
# switch's DHCP/DNS, and the host (and the host service's port forwards) reach the VM
# as <name>.mshome.net, so the guest hostname MUST equal the VM name.
#
# The source is pluggable: CONSTRUCT_HOSTNAME_SOURCE (set at build time through
# VM_HOSTNAME_SOURCE, delivered as /etc/default/construct-hostname) selects it.
#   hyperv-kvp           Hyper-V data exchange -- today.
#   cloud-init-metadata  planned: Proxmox / NoCloud / ConfigDrive, where the
#                        hypervisor supplies per-VM identity natively.
# Adding one means adding a case to read_identity() and nothing else.
set -eu

CONSTRUCT_HOSTNAME_SOURCE="${CONSTRUCT_HOSTNAME_SOURCE:-hyperv-kvp}"
CONSTRUCT_KVP_POOL="${CONSTRUCT_KVP_POOL:-/var/lib/hyperv/.kvp_pool_3}"
CONSTRUCT_HOSTNAME_WAIT="${CONSTRUCT_HOSTNAME_WAIT:-180}"
CONSTRUCT_MARKER_DIR="${CONSTRUCT_MARKER_DIR:-/var/lib/construct}"
CONSTRUCT_MARKER="${CONSTRUCT_MARKER_DIR}/hostname-adopted"

log() { echo "construct-hostname: $*"; }

# --- source: Hyper-V KVP -----------------------------------------------------
# Pool 3 is the host-to-guest "intrinsic" pool hv_kvp_daemon writes (package
# linux-cloud-tools-virtual). It is a flat file of fixed-size records: a 512-byte
# key followed by a 2048-byte value, both NUL-padded. 2560 = 5 * 512, so every half
# starts on a 512-byte boundary and dd can seek to it in whole blocks.
kvp_lookup() {
  _key="$1"
  [ -s "${CONSTRUCT_KVP_POOL}" ] || return 1

  _size="$(wc -c <"${CONSTRUCT_KVP_POOL}")"
  _records=$(( _size / 2560 ))
  _i=0
  while [ "${_i}" -lt "${_records}" ]; do
    _name="$(dd if="${CONSTRUCT_KVP_POOL}" bs=512 skip=$(( _i * 5 )) count=1 2>/dev/null | tr -d '\000')"
    if [ "${_name}" = "${_key}" ]; then
      dd if="${CONSTRUCT_KVP_POOL}" bs=512 skip=$(( _i * 5 + 1 )) count=4 2>/dev/null | tr -d '\000'
      return 0
    fi
    _i=$(( _i + 1 ))
  done
  return 1
}

# The one place that knows about sources. Echoes the raw name, or fails.
read_identity() {
  case "${CONSTRUCT_HOSTNAME_SOURCE}" in
    hyperv-kvp)
      kvp_lookup VirtualMachineName
      ;;
    *)
      log "unknown identity source '${CONSTRUCT_HOSTNAME_SOURCE}'"
      return 1
      ;;
  esac
}

# --- name -> hostname --------------------------------------------------------
normalize_name() {
  printf '%s' "$1" | tr -d '\000\r\n' | tr 'A-Z' 'a-z'
}

# A DNS label, because that is what the switch's DNS will publish. Anything else
# (a VM named "Build Box (2)") is refused rather than half-applied.
valid_label() {
  printf '%s' "$1" | grep -Eq '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'
}

apply_hostname() {
  _name="$1"

  if command -v hostnamectl >/dev/null 2>&1; then
    hostnamectl set-hostname "${_name}"
  else
    printf '%s\n' "${_name}" >/etc/hostname
    hostname "${_name}"
  fi

  # 127.0.1.1 is how Debian/Ubuntu resolve the machine's own name; leaving the
  # placeholder there makes every self-lookup (sudo included) wait for a timeout.
  if grep -q '^127\.0\.1\.1' /etc/hosts 2>/dev/null; then
    sed -i "s/^127\.0\.1\.1.*/127.0.1.1\t${_name}/" /etc/hosts
  else
    printf '127.0.1.1\t%s\n' "${_name}" >>/etc/hosts
  fi
}

# The name only becomes REACHABLE once the DHCP server has seen it: the lease
# carries the hostname option, and that is what the switch's DNS publishes.
renew_dhcp() {
  _renewed=1

  if command -v networkctl >/dev/null 2>&1; then
    for _link in $(networkctl list --no-legend 2>/dev/null | awk '$3 == "ether" { print $2 }'); do
      # A RENEW (DHCPREQUEST) is not enough for Hyper-V's Default Switch resolver, which
      # keeps the name it learnt with the first lease; a reconfigure restarts the client
      # (DISCOVER with the new hostname), which is what makes <name>.mshome.net appear
      # (field, 2026-09-04). renew stays as the fallback for older networkctl.
      if networkctl reconfigure "${_link}" >/dev/null 2>&1; then _renewed=0
      elif networkctl renew "${_link}" >/dev/null 2>&1; then _renewed=0; fi
    done
  fi

  if [ "${_renewed}" -ne 0 ] && command -v netplan >/dev/null 2>&1; then
    if netplan apply >/dev/null 2>&1; then _renewed=0; fi
  fi

  if [ "${_renewed}" -ne 0 ]; then
    log "could not renew the DHCP lease; the name may take until the next renewal to resolve"
  fi
}

main() {
  if [ -f "${CONSTRUCT_MARKER}" ]; then
    log "already adopted: $(cat "${CONSTRUCT_MARKER}")"
    return 0
  fi

  # Bounded: the KVP daemon comes up on its own schedule, and a VM that never gets a
  # name must still finish booting (with the placeholder) instead of blocking.
  _waited=0
  _name=""
  while : ; do
    _raw="$(read_identity 2>/dev/null || true)"
    _name="$(normalize_name "${_raw}")"
    if [ -n "${_name}" ] && valid_label "${_name}"; then
      break
    fi
    if [ "${_waited}" -ge "${CONSTRUCT_HOSTNAME_WAIT}" ]; then
      log "no usable name from '${CONSTRUCT_HOSTNAME_SOURCE}' after ${CONSTRUCT_HOSTNAME_WAIT}s; keeping $(hostname)"
      return 0
    fi
    sleep 2
    _waited=$(( _waited + 2 ))
  done

  if [ "${_name}" = "$(hostname)" ]; then
    log "hostname is already ${_name}"
  else
    log "adopting hostname '${_name}' from ${CONSTRUCT_HOSTNAME_SOURCE}"
    apply_hostname "${_name}"
    renew_dhcp
  fi

  mkdir -p "${CONSTRUCT_MARKER_DIR}"
  printf '%s\n' "${_name}" >"${CONSTRUCT_MARKER}"
  systemctl disable construct-hostname.service >/dev/null 2>&1 || true
  log "done"
}

# Sourced with CONSTRUCT_HOSTNAME_LIB=1 the functions above stand on their own --
# that is how test/autoinstall-iso.test.sh exercises the pool parser against a
# synthetic .kvp_pool_3 without booting anything.
[ "${CONSTRUCT_HOSTNAME_LIB:-0}" = "1" ] || main "$@"
GUESTSCRIPT

  printf 'CONSTRUCT_HOSTNAME_SOURCE=%s\n' "${VM_HOSTNAME_SOURCE}" >"${WORK}/construct-hostname.default"

  HOSTNAME_UNIT_B64="$(base64 -w0 "${WORK}/construct-hostname.service")"
  HOSTNAME_SCRIPT_B64="$(base64 -w0 "${WORK}/construct-hostname.sh")"
  HOSTNAME_DEFAULT_B64="$(base64 -w0 "${WORK}/construct-hostname.default")"
fi

# --- user-data (the autoinstall config) -------------------------------------
# The base document is byte-for-byte what it has always been; generic media only
# APPENDS to it (more late-commands, then the extra package), so the static path
# renders identically to every previous build.
cat >"${WORK}/${SEED_DIR_NAME}/user-data" <<EOF
#cloud-config
autoinstall:
  version: 1
  locale: en_US.UTF-8
  keyboard:
    layout: us
  source:
    id: ${SOURCE_ID}
    search_drivers: false
  storage:
    layout:
      name: direct
  identity:
    realname: "${VM_REALNAME}"
    hostname: ${IDENTITY_HOSTNAME}
    username: ${VM_USER}
    password: "${PASS_HASH}"
  ssh:
    install-server: true
    allow-pw: true
    authorized-keys:
      - ${BOOTSTRAP_PUBKEY}
  late-commands:
    - mkdir -p /target/etc/issue.d
    - echo ${BANNER_B64} | base64 -d > /target/etc/issue.d/construct.issue
    - chmod 0644 /target/etc/issue.d/construct.issue
EOF

if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  cat >>"${WORK}/${SEED_DIR_NAME}/user-data" <<EOF
    - mkdir -p /target/usr/local/sbin /target/etc/default /target/etc/systemd/system/multi-user.target.wants
    - echo ${HOSTNAME_SCRIPT_B64} | base64 -d > /target/usr/local/sbin/construct-hostname.sh
    - chmod 0755 /target/usr/local/sbin/construct-hostname.sh
    - echo ${HOSTNAME_DEFAULT_B64} | base64 -d > /target/etc/default/construct-hostname
    - chmod 0644 /target/etc/default/construct-hostname
    - echo ${HOSTNAME_UNIT_B64} | base64 -d > /target/etc/systemd/system/construct-hostname.service
    - chmod 0644 /target/etc/systemd/system/construct-hostname.service
    - ln -sf /etc/systemd/system/construct-hostname.service /target/etc/systemd/system/multi-user.target.wants/construct-hostname.service
    - mkdir -p /target/etc/sudoers.d
    - echo '${VM_USER} ALL=(ALL) NOPASSWD:ALL' > /target/etc/sudoers.d/90-construct-seed
    - chmod 0440 /target/etc/sudoers.d/90-construct-seed
  packages:
    - linux-cloud-tools-virtual
EOF
fi
# GENERIC media carries a seed password nobody knows (the service mints and discards it),
# so the provisioner cannot escalate through `sudo -S`: the seed user gets passwordless
# sudo from the media itself. provision.sh grants the same later on every VM, and the
# bootstrap key is still removed at the end of provisioning -- the seed user's only
# credential from outside is the key this PC holds. Local media is untouched (its seed
# password is known to the installer).

# --- Patch GRUB: prepend an autoinstall entry, make it the default ----------
xorriso -osirrox on -indev "${SRC_ISO}" \
  -extract /boot/grub/grub.cfg "${WORK}/grub.cfg" 2>/dev/null

# Build a new grub.cfg: short timeout, default to the autoinstall entry, then
# the original menu entries (kept for manual installs).
{
  echo "set timeout=5"
  echo "set default=0"
  echo
  cat <<GRUB
menuentry "Autoinstall The Construct VM (blank base + setup hint)" {
    set gfxpayload=keep
    linux  /casper/vmlinuz  autoinstall ds=nocloud\\;s=/cdrom/${SEED_DIR_NAME}/  ---
    initrd /casper/initrd
}
GRUB
  echo
  # Append the original config but drop its own 'set timeout' line so ours wins.
  grep -v -E '^\s*set\s+timeout' "${WORK}/grub.cfg"
} >"${WORK}/grub.cfg.new"

echo "==> New grub.cfg:"
sed 's/^/    /' "${WORK}/grub.cfg.new"

# --- Repack the ISO, preserving the original (BIOS + UEFI) boot setup --------
echo "==> Repacking ISO (this copies ~$(du -h "${SRC_ISO}" | cut -f1))"
rm -f "${OUT_ISO}"
xorriso -indev "${SRC_ISO}" -outdev "${OUT_ISO}" \
  -boot_image any replay \
  -map "${WORK}/${SEED_DIR_NAME}" "/${SEED_DIR_NAME}" \
  -map "${WORK}/grub.cfg.new" /boot/grub/grub.cfg

echo
echo "==> Done: ${OUT_ISO}"
if [[ "${GENERIC_MEDIA}" -eq 1 ]]; then
  echo "    Boot any new Hyper-V VM from this ISO. It installs unattended, creates"
  echo "    user '${VM_USER}', enables SSH, and takes its hostname from the VM name at"
  echo "    first boot (source: ${VM_HOSTNAME_SOURCE}); the console shows the"
  echo "    'run Provision-AgentVM.ps1' hint at login."
else
  echo "    Boot a new Hyper-V VM from this ISO. It installs unattended,"
  echo "    creates user '${VM_USER}' / host '${VM_HOST}', enables SSH, and shows the"
  echo "    'run Provision-AgentVM.ps1' hint at the console login."
fi
