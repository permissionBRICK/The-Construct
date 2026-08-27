#!/usr/bin/env bash
# Build Construct's patched T3 Code server and unsigned Windows Desktop installer
# from the exact Git tag published on the selected npm channel.
set -Eeuo pipefail

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CHANNEL="${T3CODE_CHANNEL:-stable}"
[[ "${CHANNEL}" == "nightly" ]] || CHANNEL=stable
NPM_TAG=latest
[[ "${CHANNEL}" == "nightly" ]] && NPM_TAG=nightly

CACHE_ROOT="/var/cache/construct/t3code-source"
ARTIFACT_ROOT="/var/lib/construct/t3code-desktop"
PATCH_FILE="${REPO_DIR}/patches/t3code-construct.patch"
INSTALLER_PATH="${ARTIFACT_ROOT}/T3Code-Construct-Setup.exe"
MANIFEST_PATH="${ARTIFACT_ROOT}/manifest.json"
STATUS_PATH="/etc/construct/t3code-desktop-status"

note() { printf '    %s\n' "$*"; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ -s "${PATCH_FILE}" ]] || fail "T3 source patch is missing: ${PATCH_FILE}"

# This source build can be selected even when no JavaScript-based agent was
# installed earlier in provisioning. Bootstrap the workspace's Node major before
# using npm to resolve the selected T3 channel.
node_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)"
if ! command -v npm >/dev/null 2>&1 || [[ "${node_major}" -lt 24 ]]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends ca-certificates curl
  curl -fsSL https://deb.nodesource.com/setup_24.x | bash -
  apt-get install -y nodejs
fi
command -v npm >/dev/null 2>&1 || fail "npm is required to resolve the T3 channel"

VERSION="$(npm view "t3@${NPM_TAG}" version 2>/dev/null | tail -1 | tr -d '[:space:]')"
[[ "${VERSION}" =~ ^[0-9A-Za-z.+-]+$ ]] || fail "npm returned an invalid t3@${NPM_TAG} version: ${VERSION}"
TAG="v${VERSION}"
SAFE_VERSION="${VERSION//[^0-9A-Za-z._-]/-}"
PATCH_HASH="$({ sha256sum "${PATCH_FILE}" "${REPO_DIR}/bin/build-t3code.sh" "${REPO_DIR}/extension/vm/construct-t3park-patch.mjs" "${REPO_DIR}/extension/vm/construct-t3-opencode-monitor-patch.mjs"; } | sha256sum | awk '{print $1}')"
SOURCE_KEY="${SAFE_VERSION}-${PATCH_HASH:0:12}"
SOURCE_DIR="${CACHE_ROOT}/${SOURCE_KEY}"

mkdir -p "${CACHE_ROOT}" "${ARTIFACT_ROOT}" /etc/construct

if [[ -s "${MANIFEST_PATH}" && -s "${INSTALLER_PATH}" ]]; then
  cached_version="$(node -e 'try{let m=require(process.argv[1]);process.stdout.write(m.version||"")}catch{}' "${MANIFEST_PATH}")"
  cached_hash="$(node -e 'try{let m=require(process.argv[1]);process.stdout.write(m.patchHash||"")}catch{}' "${MANIFEST_PATH}")"
  if [[ "${cached_version}" == "${VERSION}" && "${cached_hash}" == "${PATCH_HASH}" && -x "${SOURCE_DIR}/apps/server/dist/bin.mjs" ]]; then
    ln -sfn "${SOURCE_DIR}/apps/server/dist/bin.mjs" /usr/local/bin/t3
    printf 'T3CODE_DESKTOP_READY=yes\nT3CODE_DESKTOP_VERSION=%s\nT3CODE_DESKTOP_CHANNEL=%s\nT3CODE_DESKTOP_INSTALLER=%s\n' \
      "${VERSION}" "${CHANNEL}" "${INSTALLER_PATH}" >"${STATUS_PATH}"
    note "T3 Code ${VERSION} patched build is already current; reusing its VM server and Windows installer."
    exit 0
  fi
fi

# Do not let a failed rebuild advertise an older installer as the result of the
# current provision. The previous server binary remains installed until the new
# build is complete, but the host handoff is re-enabled only after success.
rm -f "${STATUS_PATH}"

available_kb="$(df -Pk "${CACHE_ROOT}" | awk 'NR==2 {print $4}')"
[[ "${available_kb:-0}" -ge 15728640 ]] || fail "T3 source/Desktop build needs at least 15 GiB free in the VM"

export DEBIAN_FRONTEND=noninteractive
if ! dpkg --print-foreign-architectures | grep -qx i386; then
  dpkg --add-architecture i386
fi
apt-get update
apt-get install -y --no-install-recommends \
  build-essential ca-certificates curl git mingw-w64 python3 wine wine64 wine32:i386

# T3's source workspace pins pnpm 11. Keep the build toolchain in the disposable
# VM; the Windows host only receives the finished installer.
if ! command -v pnpm >/dev/null 2>&1 || [[ "$(pnpm --version 2>/dev/null | cut -d. -f1)" != "11" ]]; then
  npm install -g pnpm@11.10.0
fi

if ! command -v rustup >/dev/null 2>&1; then
  curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
fi
export PATH="/root/.cargo/bin:${PATH}"
rustup target add x86_64-pc-windows-gnu

if [[ -e "${SOURCE_DIR}" && ! -d "${SOURCE_DIR}/.git" ]]; then
  rm -rf -- "${SOURCE_DIR}"
fi
if [[ ! -d "${SOURCE_DIR}/.git" ]]; then
  note "Downloading T3 Code ${TAG} (${CHANNEL}) source..."
  git clone --depth 1 --branch "${TAG}" --single-branch https://github.com/pingdotgg/t3code.git "${SOURCE_DIR}"
fi
cd "${SOURCE_DIR}"

if git apply --reverse --check "${PATCH_FILE}" >/dev/null 2>&1; then
  note "Construct T3 source patch is already applied."
else
  git apply --check "${PATCH_FILE}" || fail "T3 ${TAG} changed incompatibly; leaving the installed T3 build untouched"
  git apply "${PATCH_FILE}"
fi

note "Installing T3 source dependencies..."
pnpm install --no-frozen-lockfile
export PATH="${SOURCE_DIR}/node_modules/.bin:${PATH}"

# Release tags can intentionally retain the previous package versions in Git;
# upstream's release workflow rewrites them before producing artifacts. Mirror
# that step so the VM UI/server and Desktop package report the resolved channel
# version rather than the pre-release source value.
node scripts/update-release-package-versions.ts "${VERSION}"

note "Building the shared T3 server/web/Desktop sources..."
pnpm run build:desktop

# Apply the established guarded server integrations before packaging. The same
# dist directory becomes the VM CLI and Desktop's server.asar sidecar.
node "${REPO_DIR}/extension/vm/construct-t3park-patch.mjs" apply --bundle "${SOURCE_DIR}/apps/server/dist/bin.mjs"
node "${REPO_DIR}/extension/vm/construct-t3-opencode-monitor-patch.mjs" apply --bundle "${SOURCE_DIR}/apps/server/dist/bin.mjs"

note "Cross-building the Windows resource monitor..."
cargo build --locked --release --manifest-path native/resource-monitor/Cargo.toml --target x86_64-pc-windows-gnu
mkdir -p native/resource-monitor/target/x86_64-pc-windows-msvc/release
cp native/resource-monitor/target/x86_64-pc-windows-gnu/release/t3-resource-monitor.exe \
  native/resource-monitor/target/x86_64-pc-windows-msvc/release/t3-resource-monitor.exe

pty_manifest="$(node -e 'console.log(require.resolve("node-pty/package.json",{paths:[process.argv[1]]}))' "${SOURCE_DIR}/apps/server")"
pty_dir="$(dirname "${pty_manifest}")"
pty_prebuild="${pty_dir}/build/Release/pty.node"
[[ -s "${pty_prebuild}" ]] || fail "Linux node-pty prebuild was not produced: ${pty_prebuild}"

desktop_version="${VERSION}-construct.${PATCH_HASH:0:8}"
output_dir="${ARTIFACT_ROOT}/build-${SOURCE_KEY}"
note "Packaging unsigned Windows x64 installer..."
WINEPREFIX="${CACHE_ROOT}/wine-${SOURCE_KEY}" WINEDEBUG=-all \
  T3CODE_DESKTOP_REUSE_RESOURCE_MONITOR=true \
  node scripts/build-desktop-artifact.ts \
    --platform win --target nsis --arch x64 --skip-build \
    --build-version "${desktop_version}" --output-dir "${output_dir}" \
    --wsl-prebuild "${pty_prebuild}"

built_installer="$(find "${output_dir}" -maxdepth 1 -type f -name '*.exe' ! -name '*unpacked*' -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)"
[[ -n "${built_installer}" && -s "${built_installer}" ]] || fail "electron-builder did not produce a Windows installer"
cp "${built_installer}" "${INSTALLER_PATH}.tmp"
mv -f "${INSTALLER_PATH}.tmp" "${INSTALLER_PATH}"
chmod 0644 "${INSTALLER_PATH}"

commit="$(git rev-parse HEAD)"
installer_sha="$(sha256sum "${INSTALLER_PATH}" | awk '{print $1}')"
node - "${MANIFEST_PATH}.tmp" "${VERSION}" "${desktop_version}" "${CHANNEL}" "${TAG}" "${commit}" "${PATCH_HASH}" "${installer_sha}" <<'NODE'
const fs = require("node:fs");
const [path, version, desktopVersion, channel, sourceTag, commit, patchHash, sha256] = process.argv.slice(2);
fs.writeFileSync(path, JSON.stringify({
  version, desktopVersion, channel, sourceTag, commit, patchHash, sha256,
  installer: "T3Code-Construct-Setup.exe",
  builtAt: new Date().toISOString(),
}, null, 2) + "\n");
NODE
mv -f "${MANIFEST_PATH}.tmp" "${MANIFEST_PATH}"

chmod +x apps/server/dist/bin.mjs
ln -sfn "${SOURCE_DIR}/apps/server/dist/bin.mjs" /usr/local/bin/t3
printf 'T3CODE_DESKTOP_READY=yes\nT3CODE_DESKTOP_VERSION=%s\nT3CODE_DESKTOP_CHANNEL=%s\nT3CODE_DESKTOP_INSTALLER=%s\n' \
  "${VERSION}" "${CHANNEL}" "${INSTALLER_PATH}" >"${STATUS_PATH}"

# Keep only the selected channel build. pnpm's global content store retains shared
# packages, while stale checked-out node_modules trees would otherwise consume the VM disk.
find "${CACHE_ROOT}" -mindepth 1 -maxdepth 1 -type d ! -name "${SOURCE_KEY}" -exec rm -rf -- {} +
find "${ARTIFACT_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'build-*' -exec rm -rf -- {} +
note "Patched T3 ${VERSION} server and Windows installer are ready."
