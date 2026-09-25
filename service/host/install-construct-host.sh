#!/usr/bin/env bash
# Turn a Proxmox VE node into a Construct host: the constructd service on the node itself, with
# the Proxmox platform (Constructd:Backend=proxmox). The Linux counterpart of
# Install-ConstructHost.ps1 -- same layout of steps, same enrolment details at the end.
#
#   One shot on a fresh node (fetches the release source and the Linux service itself):
#     curl -fsSL https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/service/host/install-construct-host.sh | bash
#   From a checkout (release scripts by default, or checkout scripts with --source; the service
#   comes from the release, or is built here with --build / for a non-main --ref):
#     bash service/host/install-construct-host.sh [options]
#
# Run as root ON THE NODE. Re-running is safe: every step checks before it changes anything, the
# installed service, certificate, keytab and admin token are kept unless a flag asks otherwise.
#
# Options (all optional):
#   --public-host <name|ip>   address clients dial and the Kerberos service name; default: the node's
#                             FQDN when it resolves, else its primary IPv4
#   --package <zip|dir>       a linux-x64 publish of Constructd.Api you built yourself
#   --host-release <tag>      fetch the service from this release (default: latest); refreshes an install
#   --build                   build the service from the checkout with the .NET SDK (fetched once
#                             into /opt/construct/dotnet); automatic for a non-main --ref
#   --repo <owner/name>       source repository (default: permissionBRICK/The-Construct)
#   --ref <git ref>           which source to fetch when not run from a checkout (default: main)
#   --source <dir>            prefer this checkout's scripts over a release package's scripts
#   --admin <name>            first admin user (default: admin); its token is printed once
#   --storage <id>            Proxmox storage for VM disks, needs 'images' content (default: local-lvm)
#   --image-storage <id>      directory storage for the cloud image and cloud-init snippets (default: local)
#   --media-storage <id>      directory storage for child ISOs (default: construct-media)
#   --bridge <name>           bridge the VMs attach to (default: vmbr0)
#   --release <name>          Ubuntu release of the cloud image (default: noble)
#   --listen-port <n>         API port (default: 7462)
#   --ssh-ports <a-b>         public SSH forward range (default: 2201-2299)
#   --app-ports <a-b>         public app forward range (default: 2300-2999)
#   --rotate-token            issue a new admin token even when one exists
#   --skip-image              do not download and convert the cloud image (it must already be cached)
#   --keytab <file>           Kerberos keytab for HTTP/<public-host> (from New-ConstructKerberosPrincipal.ps1
#                             on a domain controller); turns Windows sign-in (Negotiate) on
#   --netbios-domain <NAME>   the domain's NetBIOS name (CORP): Kerberos users become NAME\user
#   --realm <REALM>           the Kerberos realm (default: the public host's DNS domain, upper-cased)
#
# What it leaves behind:
#   /opt/construct/host        the service (Constructd.Api, appsettings.Production.json)
#   /opt/construct/scripts     the Construct checkout the service hands to guests (bin/, keys/, ...)
#   /var/lib/constructd        constructd.db, iso/, media/, source/
#   /etc/constructd            tls.pfx + tls.pass (root-only), install.json
#   <image-storage>:import/construct-ubuntu-<release>-xfs-amd64.qcow2   the VM image (XFS root)
#   <image-storage>:snippets/  one cloud-init user-data file per VM, written by the service
#   systemd unit constructd.service, listening on https://0.0.0.0:<listen-port>
set -euo pipefail

# Where this script runs from: a checkout (the usual case) or a bare `curl | bash`, in which case
# the checkout is fetched below.
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  SOURCE_DIR="$(cd "${SCRIPT_DIR}/../.." 2>/dev/null && pwd || true)"
else
  SCRIPT_DIR=""
  SOURCE_DIR=""
fi
PACKAGE=""
PUBLIC_HOST=""
ADMIN="admin"
STORAGE="local-lvm"
IMAGE_STORAGE="local"
MEDIA_STORAGE="construct-media"
BRIDGE="vmbr0"
RELEASE="noble"
LISTEN_PORT=7462
SSH_PORTS="2201-2299"
APP_PORTS="2300-2999"
ROTATE=0
SKIP_IMAGE=0
KEYTAB=""
NETBIOS_DOMAIN=""
REALM=""
REPO="permissionBRICK/The-Construct"
REF="main"
HOST_RELEASE=""
BUILD=0
SOURCE_EXPLICIT=0
PACKAGE_SCRIPTS=""
INSTALL_COMMIT=""
INSTALL_VERSION=""

HOST_DIR=/opt/construct/host
SCRIPTS_DIR=/opt/construct/scripts
DATA_DIR=/var/lib/constructd
ETC_DIR=/etc/constructd
DOTNET_DIR=/opt/construct/dotnet
UNIT=/etc/systemd/system/constructd.service

usage() {
  if [[ -n "${SCRIPT_DIR}" ]]; then sed -n '2,50p' "${BASH_SOURCE[0]}"; else echo "see docs/proxmox-host.md"; fi
  exit "${1:-0}"
}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-host)   PUBLIC_HOST="$2"; shift 2 ;;
    --package)       PACKAGE="$2"; shift 2 ;;
    --source)        SOURCE_DIR="$(cd "$2" && pwd)"; SOURCE_EXPLICIT=1; shift 2 ;;
    --admin)         ADMIN="$2"; shift 2 ;;
    --storage)       STORAGE="$2"; shift 2 ;;
    --image-storage) IMAGE_STORAGE="$2"; shift 2 ;;
    --media-storage) MEDIA_STORAGE="$2"; shift 2 ;;
    --bridge)        BRIDGE="$2"; shift 2 ;;
    --release)       RELEASE="$2"; shift 2 ;;
    --listen-port)   LISTEN_PORT="$2"; shift 2 ;;
    --ssh-ports)     SSH_PORTS="$2"; shift 2 ;;
    --app-ports)     APP_PORTS="$2"; shift 2 ;;
    --rotate-token)  ROTATE=1; shift ;;
    --skip-image)    SKIP_IMAGE=1; shift ;;
    --keytab)        KEYTAB="$2"; shift 2 ;;
    --netbios-domain) NETBIOS_DOMAIN="$2"; shift 2 ;;
    --realm)         REALM="$2"; shift 2 ;;
    --repo)          REPO="$2"; shift 2 ;;
    --ref)           REF="$2"; shift 2 ;;
    --host-release)  HOST_RELEASE="$2"; shift 2 ;;
    --build)         BUILD=1; shift ;;
    -h|--help)       usage 0 ;;
    *) echo "Unknown argument: $1" >&2; usage 2 ;;
  esac
done

say()  { printf '\n==> %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
json_field() { python3 -c 'import json,sys; d=json.load(sys.stdin); v=d
for k in sys.argv[1:]:
    v=v.get(k) if isinstance(v,dict) else None
print("" if v is None else v)' "$@"; }
TMP_ROOT="$(mktemp -d)"
trap 'rm -r "${TMP_ROOT}"' EXIT

# ── 0. Check the inputs ──────────────────────────────────────────────────────
say "Checking the inputs"
[[ "$(id -u)" -eq 0 ]] || die "run as root on the Proxmox node"
for tool in pvesh pvesm qm pveversion systemctl swtpm; do
  command -v "${tool}" >/dev/null || die "'${tool}' is required (is this a Proxmox VE node?)"
done
[[ "$(dpkg-query -W -f='${Status}' pve-edk2-firmware 2>/dev/null)" == 'install ok installed' ]] \
  || die "pve-edk2-firmware is required for child UEFI and Secure Boot"
[[ "${MEDIA_STORAGE}" =~ ^[A-Za-z][A-Za-z0-9_-]*$ ]] || die "--media-storage must be a Proxmox storage id"
MISSING=()
for tool in curl unzip rsync python3 openssl; do command -v "${tool}" >/dev/null || MISSING+=("${tool}"); done
if (( ${#MISSING[@]} )); then
  note "installing ${MISSING[*]}"
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${MISSING[@]}" >/dev/null 2>&1 || die "could not install ${MISSING[*]}"
fi
[[ "${SSH_PORTS}" =~ ^[0-9]+-[0-9]+$ && "${APP_PORTS}" =~ ^[0-9]+-[0-9]+$ ]] || die "port ranges look like 2201-2299"
SSH_START="${SSH_PORTS%-*}"; SSH_END="${SSH_PORTS#*-}"; APP_START="${APP_PORTS%-*}"; APP_END="${APP_PORTS#*-}"
(( SSH_START <= SSH_END && APP_START <= APP_END )) || die "a port range must be ascending"
(( SSH_END < APP_START || APP_END < SSH_START )) || die "the SSH and app forward ranges overlap"
[[ "${ADMIN}" =~ ^[A-Za-z0-9._@\\-]{1,64}$ ]] || die "'${ADMIN}' is not a usable admin user name"
[[ "${REPO}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "--repo must be owner/name"

NODE="$(hostname -s)"
pvesh get "/nodes/${NODE}/status" --output-format json >/dev/null || die "node '${NODE}' does not answer through the API"
# The public host is the host's identity (certificate, Kerberos service name, what clients dial):
#   --public-host          explicit
#   a previous install     kept (install.json), so a re-run never renames the host by accident
#   first install          <short host>.<DNS domain> when that name resolves to one of the node's own
#                          addresses (never an mDNS .local name), otherwise the primary IPv4
if [[ -z "${PUBLIC_HOST}" && -f "${ETC_DIR}/install.json" ]]; then
  PUBLIC_HOST="$(json_field publicHost <"${ETC_DIR}/install.json")"
  # An mDNS name recorded by an older run is not an identity worth keeping; derive afresh.
  [[ "${PUBLIC_HOST}" == *.local ]] && PUBLIC_HOST=""
  [[ -n "${PUBLIC_HOST}" ]] && note "public host ${PUBLIC_HOST} (from the previous install; --public-host changes it)"
fi
if [[ -z "${PUBLIC_HOST}" ]]; then
  OWN_ADDRS=" $(hostname -I) "
  for domain in "$(dnsdomainname 2>/dev/null || true)" "$(awk '/^(domain|search)/{print $2; exit}' /etc/resolv.conf 2>/dev/null || true)"; do
    [[ -n "${domain}" && "${domain}" != "local" ]] || continue
    candidate="$(hostname -s).${domain}"
    resolved="$(getent ahostsv4 "${candidate}" 2>/dev/null | awk '{print $1}' | sort -u | head -1)"
    if [[ -n "${resolved}" && "${OWN_ADDRS}" == *" ${resolved} "* ]]; then PUBLIC_HOST="${candidate}"; break; fi
  done
  [[ -n "${PUBLIC_HOST}" ]] || PUBLIC_HOST="$(hostname -I | awk '{print $1}')"
fi
[[ -n "${PUBLIC_HOST}" ]] || die "could not determine this node's address; pass --public-host"
[[ "${PUBLIC_HOST}" != *.local ]] || die "'${PUBLIC_HOST}' is an mDNS name; Windows PCs on other subnets cannot resolve it and Kerberos needs a domain name. Pass --public-host <fqdn|ip>."

# ── 0a. The Construct checkout (the scripts guests are provisioned from) ──────
# From the checkout this script lives in, or -- when run as `curl | bash` -- fetched: the pinned
# source of the latest release for the main ref, the branch archive for any other ref.
fetch_manifest() {
  [[ -n "${MANIFEST_JSON:-}" ]] && return 0
  local tag="${HOST_RELEASE:-latest}" url
  if [[ "${tag}" == "latest" ]]; then url="https://github.com/${REPO}/releases/latest/download/manifest.json"
  else url="https://github.com/${REPO}/releases/download/${tag}/manifest.json"; fi
  MANIFEST_JSON="$(curl -fsSL --max-time 60 "${url}")" || return 1
  MANIFEST_TAG="$(printf '%s' "${MANIFEST_JSON}" | json_field releaseTag)"
  [[ "${MANIFEST_TAG}" =~ ^host-[0-9a-f]{40}$ ]] || { MANIFEST_JSON=""; return 1; }
}
download_asset() { # <asset name> <sha256> <dest>
  local url="https://github.com/${REPO}/releases/download/${MANIFEST_TAG}/$1"
  curl -fsSL --max-time 900 -o "$3" "${url}" || return 1
  [[ "$(sha256sum "$3" | cut -d' ' -f1)" == "$2" ]] || { rm -f "$3"; return 1; }
}
if [[ -z "${SOURCE_DIR}" || ! -f "${SOURCE_DIR}/bin/provision.sh" ]]; then
  say "Fetching the Construct source (${REPO} ${REF})"
  SOURCE_DIR="${TMP_ROOT}/source"; mkdir -p "${SOURCE_DIR}"
  if [[ "${REF}" == "main" ]] && fetch_manifest; then
    SRC_ASSET="$(printf '%s' "${MANIFEST_JSON}" | json_field sourceAsset)"; SRC_SHA="$(printf '%s' "${MANIFEST_JSON}" | json_field sourceSha256)"
    download_asset "${SRC_ASSET}" "${SRC_SHA}" "${TMP_ROOT}/source.zip" || die "could not download ${SRC_ASSET} (release ${MANIFEST_TAG})"
    note "release ${MANIFEST_TAG:5:7} source, checksum verified"
  else
    curl -fsSL --max-time 300 -o "${TMP_ROOT}/source.zip" "https://codeload.github.com/${REPO}/zip/${REF}" || die "could not download the ${REF} archive of ${REPO}"
    note "branch archive ${REF} (no release checksum for a non-main ref)"
  fi
  unzip -q "${TMP_ROOT}/source.zip" -d "${TMP_ROOT}/unpacked"
  INNER="$(find "${TMP_ROOT}/unpacked" -mindepth 1 -maxdepth 1 -type d | head -1)"
  [[ -f "${INNER}/bin/provision.sh" ]] || die "the downloaded archive is not a Construct checkout"
  SOURCE_DIR="${INNER}"
fi
[[ -f "${SOURCE_DIR}/bin/provision.sh" && -f "${SOURCE_DIR}/keys/bootstrap_ed25519.pub" ]] \
  || die "'${SOURCE_DIR}' is not a Construct checkout (bin/provision.sh and keys/bootstrap_ed25519.pub expected); pass --source"

# ── 0b. The service binary ────────────────────────────────────────────────────
#   --package <zip|dir>   what you built yourself (dotnet publish -r linux-x64 --self-contained)
#   installed already     kept, unless --host-release/--build ask for a refresh
#   otherwise             the release's linux-x64 zip for the main ref; built from the checkout
#                         with the .NET SDK (fetched into /opt/construct/dotnet) for any other ref
STAGE=""
stage_from_zip() {
  local dir="${TMP_ROOT}/service"; mkdir -p "${dir}"; unzip -q "$1" -d "${dir}"
  if [[ -f "${dir}/service/Constructd.Api" && -f "${dir}/scripts/.construct-revision" ]]; then
    INSTALL_COMMIT="$(cat "${dir}/scripts/.construct-revision")"
    [[ "${INSTALL_COMMIT}" =~ ^[0-9a-f]{40}$ ]] || die "invalid package revision"
    if [[ "${SOURCE_EXPLICIT}" -eq 0 ]]; then PACKAGE_SCRIPTS="${dir}/scripts"; fi
  fi
  if [[ ! -f "${dir}/Constructd.Api" && -f "${dir}/service/Constructd.Api" ]]; then dir="${dir}/service"; fi
  [[ -f "${dir}/Constructd.Api" ]] || die "'$1' holds no Constructd.Api"
  STAGE="${dir}"
}
build_service() {
  say "Building the service from source (.NET SDK)"
  # Archives (release source zip, GitHub branch zip) omit service/src by .gitattributes export-ignore:
  # the service is a release artifact, not part of the scripts payload. A build therefore needs a
  # real clone of the ref.
  local src="${SOURCE_DIR}"
  if [[ ! -d "${src}/service/src/Constructd.Api" ]]; then
    command -v git >/dev/null || { note "installing git"; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git >/dev/null 2>&1 || die "could not install git"; }
    src="${TMP_ROOT}/clone"
    note "cloning ${REPO} (${REF}) for the service source"
    git clone -q --depth 1 --branch "${REF}" "https://github.com/${REPO}.git" "${src}" || die "could not clone ${REPO} ${REF}"
  fi
  if ! "${DOTNET_DIR}/dotnet" --version >/dev/null 2>&1; then
    note "fetching the .NET 10 SDK into ${DOTNET_DIR} (once)"
    curl -fsSL --max-time 120 -o "${TMP_ROOT}/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh || die "could not download dotnet-install.sh"
    bash "${TMP_ROOT}/dotnet-install.sh" --channel 10.0 --install-dir "${DOTNET_DIR}" >/dev/null || die "the .NET SDK install failed"
  fi
  local out="${TMP_ROOT}/publish" log=/var/log/constructd-build.log
  note "dotnet publish (a few minutes; log: ${log})"
  ( cd "${src}" && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "${DOTNET_DIR}/dotnet" publish service/src/Constructd.Api -c Release -r linux-x64 --self-contained true -o "${out}" -v q >"${log}" 2>&1 ) \
    || { grep -E 'error' "${log}" | head -20 >&2; die "dotnet publish failed (see ${log})"; }
  [[ -f "${out}/Constructd.Api" ]] || die "the build produced no Constructd.Api"
  STAGE="${out}"
  INSTALL_COMMIT="$(git -C "${src}" rev-parse HEAD 2>/dev/null || cat "${src}/.construct-revision" 2>/dev/null || echo unknown)"
  note "built $(git -C "${src}" rev-parse --short HEAD 2>/dev/null || cat "${src}/.construct-revision" 2>/dev/null || echo "${REF}")"
}
if [[ -n "${PACKAGE}" ]]; then
  if [[ -d "${PACKAGE}" ]]; then STAGE="$(cd "${PACKAGE}" && pwd)"; [[ -f "${STAGE}/Constructd.Api" ]] || die "'${PACKAGE}' holds no Constructd.Api"
  elif [[ -f "${PACKAGE}" ]]; then stage_from_zip "${PACKAGE}"
  else die "--package '${PACKAGE}' is neither a zip nor a directory"; fi
elif [[ -x "${HOST_DIR}/Constructd.Api" && -z "${HOST_RELEASE}" && "${BUILD}" -eq 0 ]]; then
  note "keeping the installed service in ${HOST_DIR} (pass --host-release latest or --build to refresh it)"
elif [[ "${BUILD}" -eq 0 && "${REF}" == "main" ]] && fetch_manifest && [[ -n "$(printf '%s' "${MANIFEST_JSON}" | json_field linuxAsset)" ]]; then
  say "Downloading the service (release ${MANIFEST_TAG:5:7})"
  LNX_ASSET="$(printf '%s' "${MANIFEST_JSON}" | json_field linuxAsset)"; LNX_SHA="$(printf '%s' "${MANIFEST_JSON}" | json_field linuxSha256)"
  download_asset "${LNX_ASSET}" "${LNX_SHA}" "${TMP_ROOT}/service.zip" || die "could not download ${LNX_ASSET}"
  stage_from_zip "${TMP_ROOT}/service.zip"
  INSTALL_COMMIT="$(printf '%s' "${MANIFEST_JSON}" | json_field commit)"
  INSTALL_VERSION="$(printf '%s' "${MANIFEST_JSON}" | json_field packageVersion)"
  note "${LNX_ASSET}, checksum verified"
else
  [[ "${BUILD}" -eq 1 || "${REF}" != "main" ]] || note "this release ships no Linux service yet; building it here"
  build_service
fi
note "node ${NODE}, public host ${PUBLIC_HOST}, source ${SOURCE_DIR}"

# ── 1. Directories ───────────────────────────────────────────────────────────
say "Directories"
install -d -m 0755 /opt/construct "${HOST_DIR}" "${SCRIPTS_DIR}"
install -d -m 0750 "${DATA_DIR}" "${DATA_DIR}/iso" "${DATA_DIR}/media" "${DATA_DIR}/source"
install -d -m 0700 "${ETC_DIR}"
note "${HOST_DIR}, ${SCRIPTS_DIR}, ${DATA_DIR}, ${ETC_DIR}"

# ── 2. Proxmox prerequisites: storages, content types, the bridge ────────────
say "Proxmox storage and network"
storage_json() { pvesh get "/storage/$1" --output-format json 2>/dev/null; }
DISK_JSON="$(storage_json "${STORAGE}")" || die "storage '${STORAGE}' does not exist (pass --storage)"
printf ',%s,' "$(printf '%s' "${DISK_JSON}" | json_field content)" | grep -q ',images,' || die "storage '${STORAGE}' has no 'images' content; VM disks cannot go there"
IMG_JSON="$(storage_json "${IMAGE_STORAGE}")" || die "storage '${IMAGE_STORAGE}' does not exist (pass --image-storage)"
[[ "$(printf '%s' "${IMG_JSON}" | json_field type)" == "dir" ]] || die "'${IMAGE_STORAGE}' must be a directory storage (it holds the image and the snippets)"
IMG_PATH="$(printf '%s' "${IMG_JSON}" | json_field path)"
CONTENT="$(printf '%s' "${IMG_JSON}" | json_field content)"
NEW_CONTENT="${CONTENT}"
for want in import snippets; do
  printf ',%s,' "${NEW_CONTENT}" | grep -q ",${want}," || NEW_CONTENT="${NEW_CONTENT:+${NEW_CONTENT},}${want}"
done
if [[ "${NEW_CONTENT}" != "${CONTENT}" ]]; then
  pvesh set "/storage/${IMAGE_STORAGE}" --content "${NEW_CONTENT}" >/dev/null
  note "'${IMAGE_STORAGE}' content types now: ${NEW_CONTENT}"
else
  note "'${IMAGE_STORAGE}' already allows import and snippets"
fi
pvesh get "/nodes/${NODE}/network" --type bridge --output-format json \
  | python3 -c 'import json,sys; b=sys.argv[1]; sys.exit(0 if any(i.get("iface")==b for i in json.load(sys.stdin)) else 1)' "${BRIDGE}" \
  || die "bridge '${BRIDGE}' does not exist on ${NODE} (pass --bridge)"
SNIPPET_DIR="${IMG_PATH}/snippets"
install -d -m 0755 "${SNIPPET_DIR}"
note "VM disks on '${STORAGE}', image and seeds on '${IMAGE_STORAGE}' (${IMG_PATH}), bridge ${BRIDGE}"

ensure_media_storage() {
  local existing content
  existing="$(pvesh get /storage --output-format json)" || die "could not list Proxmox storage"
  if printf '%s' "${existing}" | python3 -c 'import json,sys; sys.exit(0 if any(s.get("storage")==sys.argv[1] for s in json.load(sys.stdin)) else 1)' "${MEDIA_STORAGE}"; then
    existing="$(storage_json "${MEDIA_STORAGE}")" || die "could not read media storage"
    [[ "$(printf '%s' "${existing}" | json_field type)" == dir && \
       "$(printf '%s' "${existing}" | json_field path)" == "${DATA_DIR}/media" ]] \
      || die "media storage must be a directory storage at ${DATA_DIR}/media"
    content="$(printf '%s' "${existing}" | json_field content)"
    if [[ ",${content}," != *,iso,* ]]; then
      pvesm set "${MEDIA_STORAGE}" --content "${content:+${content},}iso"
    fi
  else
    pvesm add dir "${MEDIA_STORAGE}" --path "${DATA_DIR}/media" --content iso
  fi
  install -d -m 0755 "${DATA_DIR}/media/template/iso"
}
ensure_media_storage

# ── 3. The Ubuntu cloud image, rebuilt with an XFS root ───────────────────────
# Ubuntu ships its cloud images with ext4, which has no reflinks. Construct guests keep many git
# worktrees whose build outputs and dependencies are reflink copies of each other, so the image is
# converted once per node (service/host/xfs-cloud-image.sh) and cached under its own name.
say "Ubuntu ${RELEASE} cloud image (XFS root)"
IMAGE_NAME="construct-ubuntu-${RELEASE}-xfs-amd64.qcow2"
IMAGE_VOLID="${IMAGE_STORAGE}:import/${IMAGE_NAME}"
IMAGE_URL="https://cloud-images.ubuntu.com/${RELEASE}/current/${RELEASE}-server-cloudimg-amd64.img"
LEGACY_IMAGE="${IMG_PATH}/import/construct-ubuntu-${RELEASE}-cloudimg-amd64.qcow2"
have_image() {
  pvesh get "/nodes/${NODE}/storage/${IMAGE_STORAGE}/content" --content import --output-format json \
    | python3 -c 'import json,sys; v=sys.argv[1]; sys.exit(0 if any(e.get("volid")==v for e in json.load(sys.stdin)) else 1)' "${IMAGE_VOLID}"
}
if have_image; then
  note "cached: ${IMAGE_VOLID}"
elif [[ "${SKIP_IMAGE}" -eq 1 ]]; then
  die "the image ${IMAGE_VOLID} is not cached and --skip-image was given"
else
  command -v sfdisk >/dev/null || { note "installing fdisk"; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq fdisk >/dev/null 2>&1 || die "could not install fdisk"; }
  command -v qemu-img >/dev/null || die "'qemu-img' is required (part of Proxmox VE)"
  install -d -m 0755 "${IMG_PATH}/import"
  # The download and the conversion's work files stay on the image storage: a few GB that the
  # node's root file system may not have to spare.
  DL="$(mktemp -d "${IMG_PATH}/import/.download.XXXXXX")"
  trap 'rm -rf "${DL}"; rm -r "${TMP_ROOT}"' EXIT
  SUM="$(curl -fsSL --max-time 60 "$(dirname "${IMAGE_URL}")/SHA256SUMS" | awk -v f="*$(basename "${IMAGE_URL}")" '$2==f {print $1}')" || SUM=""
  note "downloading ${IMAGE_URL}${SUM:+ (sha256 ${SUM})}"
  curl -fsSL --retry 3 -o "${DL}/cloudimg.img" "${IMAGE_URL}" || die "could not download ${IMAGE_URL}"
  if [[ -n "${SUM}" ]]; then
    [[ "$(sha256sum "${DL}/cloudimg.img" | cut -d' ' -f1)" == "${SUM}" ]] || die "checksum mismatch for ${IMAGE_URL}"
  fi
  note "converting the root file system to XFS"
  bash "${SOURCE_DIR}/service/host/xfs-cloud-image.sh" "${DL}/cloudimg.img" "${IMG_PATH}/import/${IMAGE_NAME}" \
    || die "could not convert the cloud image to XFS"
  rm -rf "${DL}"
  have_image || die "the conversion did not leave ${IMAGE_VOLID} behind"
  note "cached: ${IMAGE_VOLID}"
fi
# VMs are full copies of the image (import-from), so the ext4 image of older releases is unused.
if [[ -f "${LEGACY_IMAGE}" ]]; then
  rm -f "${LEGACY_IMAGE}"
  note "removed the previous ext4 image $(basename "${LEGACY_IMAGE}")"
fi

# ── 4. TLS certificate (clients pin its fingerprint at enrolment) ─────────────
say "TLS certificate"
PFX="${ETC_DIR}/tls.pfx"; PFX_PASS_FILE="${ETC_DIR}/tls.pass"; CRT="${ETC_DIR}/tls.crt"
# A certificate that does not name the public host is reissued: clients pin the fingerprint, so
# tell them (the installer prints the new one) — a stale name would fail every TLS handshake anyway.
if [[ -f "${CRT}" ]] && ! openssl x509 -in "${CRT}" -noout -ext subjectAltName 2>/dev/null | grep -qE "(DNS|IP( Address)?):${PUBLIC_HOST}(,|$)"; then
  note "the existing certificate does not name ${PUBLIC_HOST}; issuing a new one (clients must re-confirm the fingerprint)"
  rm -f "${PFX}" "${PFX_PASS_FILE}" "${CRT}"
fi
if [[ ! -f "${PFX}" || ! -f "${PFX_PASS_FILE}" || ! -f "${CRT}" ]]; then
  KEY="${TMP_ROOT}/tls.key"
  SAN="DNS:${NODE}"
  if [[ "${PUBLIC_HOST}" =~ ^[0-9.]+$ ]]; then SAN="${SAN},IP:${PUBLIC_HOST}"; else SAN="${SAN},DNS:${PUBLIC_HOST}"; fi
  openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes -keyout "${KEY}" -out "${CRT}" \
    -subj "/CN=${PUBLIC_HOST}" -addext "subjectAltName=${SAN}" >/dev/null 2>&1
  openssl rand -hex 24 >"${PFX_PASS_FILE}"; chmod 0600 "${PFX_PASS_FILE}"
  openssl pkcs12 -export -inkey "${KEY}" -in "${CRT}" -out "${PFX}" -passout "file:${PFX_PASS_FILE}" \
    -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256
  chmod 0600 "${PFX}"
  note "created a self-signed certificate for ${PUBLIC_HOST} (10 years)"
else
  note "keeping ${PFX}"
fi
FINGERPRINT="$(openssl x509 -in "${CRT}" -noout -fingerprint -sha256 | sed 's/^.*=//')"
note "SHA-256 fingerprint ${FINGERPRINT}"

# ── 4b. Kerberos (Windows sign-in) ────────────────────────────────────────────
# A keytab for HTTP/<public host> lets the service accept Negotiate from domain PCs like a
# Windows host does. The NetBIOS name and realm are remembered for re-runs without --keytab.
say "Kerberos"
KEYTAB_PATH="${ETC_DIR}/krb5.keytab"; KRB_ENV="${ETC_DIR}/kerberos.env"
if [[ -n "${KEYTAB}" ]]; then
  [[ -f "${KEYTAB}" ]] || die "--keytab '${KEYTAB}' does not exist"
  [[ -n "${NETBIOS_DOMAIN}" ]] || die "--keytab needs --netbios-domain <NAME> (the domain's short name, e.g. CORP)"
  [[ "${PUBLIC_HOST}" =~ \. && ! "${PUBLIC_HOST}" =~ ^[0-9.]+$ ]] || die "--keytab needs --public-host to be the host's DNS name (the SPN is HTTP/<public-host>), not an address"
  [[ -n "${REALM}" ]] || REALM="$(printf '%s' "${PUBLIC_HOST#*.}" | tr '[:lower:]' '[:upper:]')"
  install -m 0600 -o root -g root "${KEYTAB}" "${KEYTAB_PATH}"
  printf 'NETBIOS_DOMAIN=%s\nREALM=%s\n' "${NETBIOS_DOMAIN}" "${REALM}" >"${KRB_ENV}"; chmod 0600 "${KRB_ENV}"
  note "keytab installed at ${KEYTAB_PATH} (realm ${REALM}, domain ${NETBIOS_DOMAIN})"
elif [[ -f "${KEYTAB_PATH}" && -f "${KRB_ENV}" ]]; then
  # shellcheck disable=SC1090
  source "${KRB_ENV}"
  note "keeping ${KEYTAB_PATH} (realm ${REALM}, domain ${NETBIOS_DOMAIN})"
fi
NEGOTIATE=false
if [[ -f "${KEYTAB_PATH}" && -n "${NETBIOS_DOMAIN:-}" ]]; then
  NEGOTIATE=true
  if [[ ! -f /etc/krb5.conf ]]; then
    cat >/etc/krb5.conf <<KRB
[libdefaults]
    default_realm = ${REALM}
    dns_lookup_kdc = true
    dns_lookup_realm = false
    rdns = false
KRB
    note "wrote /etc/krb5.conf (realm ${REALM}, KDCs from DNS)"
  else
    note "/etc/krb5.conf exists; make sure realm ${REALM} resolves its KDCs"
  fi
else
  note "no keytab: Windows sign-in stays off, clients use tokens (-ServiceAuth token)"
fi

# ── 5. Install the service and the scripts ───────────────────────────────────
say "Installing files"
if systemctl is-active --quiet constructd 2>/dev/null; then systemctl stop constructd; note "stopped constructd"; fi
if [[ -n "${STAGE}" ]]; then
  rsync -a --delete --exclude 'appsettings.Production.json' --exclude 'install.json' --exclude '*.db*' \
    --exclude 'keys' --exclude 'projects' --exclude 'settings.json' --exclude 'data' --exclude 'media' \
    --exclude 'iso' --exclude '.construct-tools' --exclude '.git' "${STAGE}/" "${HOST_DIR}/"
  chmod 0755 "${HOST_DIR}/Constructd.Api"
  note "service -> ${HOST_DIR}"
fi
SCRIPTS_STAGE="${PACKAGE_SCRIPTS:-${SOURCE_DIR}}"
SCRIPT_EXCLUDES=()
if [[ -n "${PACKAGE_SCRIPTS}" ]]; then
  # Keys are deliberately absent from release payloads. Seed them once from the source checkout;
  # existing host keys survive both installer repairs and self-updates.
  install -d -m 0700 "${SCRIPTS_DIR}/keys"
  rsync -a --ignore-existing "${SOURCE_DIR}/keys/" "${SCRIPTS_DIR}/keys/"
  SCRIPT_EXCLUDES+=(--exclude=keys)
fi
if [[ "$(cd "${SCRIPTS_STAGE}" && pwd)" != "${SCRIPTS_DIR}" ]]; then
  rsync -a --delete --exclude '.git' --exclude 'node_modules' --exclude '.construct-tools' --exclude '.construct-backup' \
    --exclude '*.db*' --exclude 'settings.json' --exclude 'projects' --exclude 'data' --exclude 'media' --exclude 'iso' \
    --exclude 'appsettings.Production.json' --exclude 'install.json' \
    "${SCRIPT_EXCLUDES[@]}" "${SCRIPTS_STAGE}/" "${SCRIPTS_DIR}/"
  note "scripts -> ${SCRIPTS_DIR}"
else
  note "scripts already in ${SCRIPTS_DIR}"
fi
chmod 0600 "${SCRIPTS_DIR}/keys/bootstrap_ed25519" 2>/dev/null || true

# The self-updater owns only files recorded here. Raw local publishes without a release revision
# have unknown identity; never label them with an unrelated source checkout's commit.
python3 - "${HOST_DIR}" "${SCRIPTS_DIR}" "${STAGE}" "${PACKAGE_SCRIPTS}" "${INSTALL_COMMIT}" "${INSTALL_VERSION}" <<'LEDGER_PY'
import datetime, hashlib, json, os, pathlib, sys, tempfile
host, scripts = map(pathlib.Path, sys.argv[1:3])
stage, package_scripts, commit, version = sys.argv[3:]
ledger = host/'install.json'
def no_links(path):
    for p in (path, *path.parents):
        if p.is_symlink(): raise ValueError('Installation ledger cannot contain links')
def preserved(path):
    return any(p.lower() in ('appsettings.production.json','install.json','settings.json','projects','keys','.git','.construct-tools','data','media','iso') or '.db' in p.lower() for p in path.parts)
no_links(ledger)
previous = json.loads(ledger.read_text()) if ledger.exists() else {}
files = []
def record(prefix, relative, installed):
    if preserved(relative): return
    target = installed/relative; no_links(target)
    with target.open('rb') as stream: digest = hashlib.file_digest(stream,'sha256').hexdigest()
    files.append(dict(path=prefix+'/'+relative.as_posix(),sha256=digest))
if not stage and previous.get('files'):
    for f in previous['files']:
        if f['path'].startswith('service/'):
            relative=pathlib.Path(f['path'][8:])
            if relative.is_absolute() or '..' in relative.parts: raise ValueError('Unsafe ledger path')
            record('service',relative,host)
else:
    base=pathlib.Path(stage) if stage else host
    for path in sorted(base.rglob('*')):
        no_links(path)
        if path.is_file(): record('service',path.relative_to(base),host)
if package_scripts:
    base=pathlib.Path(package_scripts)
    for path in sorted(base.rglob('*')):
        no_links(path)
        if path.is_file(): record('scripts',path.relative_to(base),scripts)
stamp=datetime.datetime.now(datetime.timezone.utc).isoformat()
value=dict(source='installer',commit=commit or (previous.get('commit','unknown') if not stage else 'unknown'),
           packageVersion=version or (previous.get('packageVersion','unknown') if not stage else 'unknown'),
           installedAt=stamp,previousCommit=previous.get('commit'),updateId=None,files=files)
fd,temp=tempfile.mkstemp(prefix='install.json.',suffix='.tmp',dir=host)
try:
    with os.fdopen(fd,'w') as stream:
        json.dump(value,stream);stream.write('\n');stream.flush();os.fsync(stream.fileno())
    os.replace(temp,ledger)
finally:
    if os.path.exists(temp): os.unlink(temp)
LEDGER_PY

# ── 6. appsettings.Production.json ───────────────────────────────────────────
say "Writing ${HOST_DIR}/appsettings.Production.json"
PFX_PASS="$(cat "${PFX_PASS_FILE}")"
python3 - "${HOST_DIR}/appsettings.Production.json" <<PY
import json, sys
settings = {
  "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning"}},
  "Constructd": {
    "Backend": "proxmox",
    "Persistence": "Sqlite",
    "DatabasePath": "${DATA_DIR}/constructd.db",
    "FileLog": {"Directory": "${DATA_DIR}/logs"},
    "ListenUrl": "https://0.0.0.0:${LISTEN_PORT}",
    "CertPath": "${PFX}",
    "CertPassword": "${PFX_PASS}",
    "ScriptsDir": "${SCRIPTS_DIR}",
    "PublicHost": "${PUBLIC_HOST}",
    "ListenAddress": "0.0.0.0",
    "SshForwardPorts": {"Start": ${SSH_START}, "End": ${SSH_END}},
    "AppForwardPorts": {"Start": ${APP_START}, "End": ${APP_END}},
    "Power": {"KeepHostAwake": False},
    "Negotiate": {"Enabled": ${NEGOTIATE^}, "DomainName": "${NETBIOS_DOMAIN:-}", "Realm": "${REALM:-}"},
    "Iso": {
      "SeedUser": "construct",
      "BootstrapPublicKeyPath": "${SCRIPTS_DIR}/keys/bootstrap_ed25519.pub",
      "CacheDir": "${DATA_DIR}/iso"
    },
    "HostAdmin": {"Media": {"RootDir": "${DATA_DIR}/media/template/iso"}, "Source": {"RootDir": "${DATA_DIR}/source"}},
    "Proxmox": {
      "Node": "${NODE}",
      "Storage": "${STORAGE}",
      "MediaStorage": "${MEDIA_STORAGE}",
      "ImageVolume": "${IMAGE_VOLID}",
      "SnippetStorage": "${IMAGE_STORAGE}",
      "SnippetDir": "${SNIPPET_DIR}",
      "Bridge": "${BRIDGE}"
    }
  }
}
with open(sys.argv[1], "w") as f:
    json.dump(settings, f, indent=2)
    f.write("\n")
PY
chmod 0600 "${HOST_DIR}/appsettings.Production.json"
note "written (root-only: it carries the certificate password)"

# ── 7. systemd unit ──────────────────────────────────────────────────────────
say "systemd unit"
cat >"${UNIT}" <<UNIT
[Unit]
Description=The Construct host service (constructd) on Proxmox VE
After=network-online.target pve-cluster.service
Wants=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=${HOST_DIR}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
$( [[ "${NEGOTIATE}" == true ]] && printf 'Environment=KRB5_KTNAME=%s\n' "${KEYTAB_PATH}" )
ExecStart=${HOST_DIR}/Constructd.Api
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable constructd >/dev/null 2>&1
note "${UNIT} (root: qm and pvesh need it)"

# ── 7b. Never sleep ──────────────────────────────────────────────────────────
# The Windows host installer disables sleep with powercfg; a Linux node (a laptop, say)
# must not suspend under its guests either: mask the sleep targets and make logind
# ignore the lid, the suspend/hibernate keys and idleness.
say "Host power"
systemctl mask --quiet sleep.target suspend.target hibernate.target hybrid-sleep.target 2>/dev/null || true
install -d -m 0755 /etc/systemd/logind.conf.d
cat >/etc/systemd/logind.conf.d/constructd.conf <<'LOGIND'
# The Construct host: this node hosts VMs and must never sleep (install-construct-host.sh).
[Login]
HandleLidSwitch=ignore
HandleLidSwitchExternalPower=ignore
HandleLidSwitchDocked=ignore
HandleSuspendKey=ignore
HandleHibernateKey=ignore
IdleAction=ignore
LOGIND
systemctl try-restart systemd-logind >/dev/null 2>&1 || systemctl kill -s HUP systemd-logind >/dev/null 2>&1 || true
note "sleep/suspend/hibernate masked; lid, suspend key and idle ignored"

# ── 7c. Nested virtualization ────────────────────────────────────────────────
say "Nested virtualization"
configure_nested_virtualization() {
  local modules_path="${1:-/sys/module}" config_path="${2:-/etc/modprobe.d/construct-kvm.conf}" module value
  for module in kvm_intel kvm_amd; do
    [[ -r "${modules_path}/${module}/parameters/nested" ]] || continue
    value="$(cat "${modules_path}/${module}/parameters/nested")"
    if [[ "${value}" == Y || "${value}" == y || "${value}" == 1 ]]; then
      note "${module}: nested virtualization is live; VM exposure follows the host/user policy"
    else
      install -d -m 0755 "$(dirname "${config_path}")"
      printf 'options %s nested=1\n' "${module}" >"${config_path}"
      note "${module}: nested virtualization is not live; wrote ${config_path}"
      note "Reboot the node or reload the module after stopping all guests. No modules were reloaded."
    fi
    return 0
  done
  note "No loaded KVM nested parameter found; nested virtualization is unavailable on this host"
}
configure_nested_virtualization

# ── 8. First admin and their token ───────────────────────────────────────────
say "Admin user '${ADMIN}'"
admin_cli() { (cd "${HOST_DIR}" && ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production "${HOST_DIR}/Constructd.Api" admin "$@"); }
if admin_cli users list --json | python3 -c 'import json,sys; u=sys.argv[1]; d=json.load(sys.stdin); users=d if isinstance(d,list) else d.get("users",d.get("result",[])); sys.exit(0 if any((x.get("name") if isinstance(x,dict) else x)==u for x in users) else 1)' "${ADMIN}"; then
  note "exists"
  HAVE_ADMIN=1
else
  admin_cli users add "${ADMIN}" --role Admin --max-vms 10 --json >/dev/null
  note "created"
  HAVE_ADMIN=0
fi
TOKEN=""
if [[ "${HAVE_ADMIN}" -eq 0 || "${ROTATE}" -eq 1 || ! -f "${ETC_DIR}/install.json" ]]; then
  TOKEN="$(admin_cli tokens issue "${ADMIN}" --label "install-$(date -u +%Y%m%d)" --json | json_field token)"
  [[ -n "${TOKEN}" ]] || die "the admin CLI issued no token"
  note "token issued (shown once below)"
else
  note "keeping the existing token (pass --rotate-token for a new one)"
fi
python3 - "${ETC_DIR}/install.json" <<PY
import json, sys, datetime
json.dump({"installedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "publicHost": "${PUBLIC_HOST}",
           "node": "${NODE}", "fingerprint": "${FINGERPRINT}", "admin": "${ADMIN}", "listenPort": ${LISTEN_PORT},
           "source": "${SOURCE_DIR}"}, open(sys.argv[1], "w"), indent=2)
PY

# ── 9. Start and verify ──────────────────────────────────────────────────────
say "Starting constructd"
systemctl restart constructd
HEALTH=""
for _ in $(seq 1 30); do
  sleep 1
  if [[ -n "${TOKEN}" ]]; then
    HEALTH="$(curl -sk --max-time 5 -H "Authorization: Bearer ${TOKEN}" "https://127.0.0.1:${LISTEN_PORT}/api/v1/health" || true)"
  else
    HEALTH="$(curl -sk --max-time 5 -o /dev/null -w '%{http_code}' "https://127.0.0.1:${LISTEN_PORT}/api/v1/health" || true)"
  fi
  [[ -n "${HEALTH}" && "${HEALTH}" != "000" ]] && break
done
if [[ -z "${HEALTH}" || "${HEALTH}" == "000" ]]; then
  journalctl -u constructd -n 20 --no-pager >&2 || true
  if [[ -n "${TOKEN}" ]]; then
    echo "The admin token was issued before the service failed; keep it: ${ADMIN} = ${TOKEN}" >&2
  fi
  die "constructd did not come up on port ${LISTEN_PORT} (journalctl -u constructd)"
fi
note "listening on https://${PUBLIC_HOST}:${LISTEN_PORT}"

echo
echo "The Construct host is ready. On a Windows PC with The Construct installed, enrol with:"
echo
if [[ "${NEGOTIATE}" == true ]]; then
  echo "    .\\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://${PUBLIC_HOST}:${LISTEN_PORT} -InstanceName <name>"
  echo
  echo "    Windows sign-in is ON: domain users authenticate with their own account. Add them as"
  echo "    ${HOST_DIR}/Constructd.Api admin users add '${NETBIOS_DOMAIN}\\<user>' --max-vms 3"
  echo "    Tokens keep working too (-ServiceAuth token)."
else
  echo "    .\\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://${PUBLIC_HOST}:${LISTEN_PORT} -ServiceAuth token -InstanceName <name>"
fi
echo
echo "    Service URL : https://${PUBLIC_HOST}:${LISTEN_PORT}"
echo "    Fingerprint : ${FINGERPRINT}   (confirm this when the installer shows it)"
echo "    Admin user  : ${ADMIN}"
if [[ -n "${TOKEN}" ]]; then
  echo "    Admin token : ${TOKEN}   (shown once; the installer stores it on the PC)"
else
  echo "    Admin token : unchanged (re-run with --rotate-token to issue a new one)"
fi
echo
echo "More users: ${HOST_DIR}/Constructd.Api admin users add <name> --max-vms 3   then   admin tokens issue <name>"
echo "Logs:       journalctl -u constructd -f"
