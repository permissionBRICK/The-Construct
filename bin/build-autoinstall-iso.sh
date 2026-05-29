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
echo "==> Identity   : user=${VM_USER} host=${VM_HOST} (password preset)"
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

  Target : ${VM_HOST}.mshome.net   (seed user: ${VM_USER})

  The script uploads the repo, provisions this VM, retrieves
  the root SSH key, and wires up your host for SSH + VS Code.
============================================================

EOF
BANNER_B64="$(base64 -w0 "${BANNER_FILE}")"

# --- meta-data (NoCloud requires it to exist; instance-id is enough) --------
cat >"${WORK}/${SEED_DIR_NAME}/meta-data" <<EOF
instance-id: ${VM_HOST}
local-hostname: ${VM_HOST}
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

# --- user-data (the autoinstall config) -------------------------------------
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
    hostname: ${VM_HOST}
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
echo "    Boot a new Hyper-V VM from this ISO. It installs unattended,"
echo "    creates user '${VM_USER}' / host '${VM_HOST}', enables SSH, and shows the"
echo "    'run Provision-AgentVM.ps1' hint at the console login."
