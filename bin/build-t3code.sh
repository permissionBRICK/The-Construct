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
CONSTRUCT_VERSION="${CONSTRUCT_VERSION:-unversioned}"
[[ "${CONSTRUCT_VERSION}" =~ ^[0-9a-f]{7,64}$ ]] || CONSTRUCT_VERSION=unversioned

note() { printf '    %s\n' "$*"; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# ── Superseded-build pruning + free-space requirement (pure helpers; unit-tested by
#    test/t3-build-diskcheck.test.sh, which sources this file with _FUNCS_ONLY=true) ──
#
# A finished build directory must keep what the INSTALLED server uses at run time:
# the source checkout (git, or the .construct-upstream-commit marker), apps/server/dist
# (/usr/local/bin/t3 points into it; the web client is bundled at dist/client) and any
# resource-monitor executable under native/*/target/{release,debug}. Everything else is
# regenerable once the build is superseded: the pnpm node_modules tree (mostly hardlinks
# into the pnpm store, so it reclaims little), the Windows cross-compile targets, cargo's
# intermediate deps/build/incremental dirs, the Windows Desktop output and the web bundle
# (apps/web/dist is only the monorepo fallback for a dev server).
t3_build_prune_candidates() {
  local dir="$1" p
  for p in node_modules apps/desktop/release apps/desktop/dist apps/web/dist apps/mobile/dist apps/marketing/dist; do
    [[ -e "${dir}/${p}" ]] && printf '%s\n' "${dir}/${p}"
  done
  for p in "${dir}"/native/*/target/*-pc-windows-* \
           "${dir}"/native/*/target/release/deps "${dir}"/native/*/target/release/build \
           "${dir}"/native/*/target/release/incremental "${dir}"/native/*/target/release/.fingerprint \
           "${dir}"/native/*/target/release/examples \
           "${dir}"/native/*/target/debug/deps "${dir}"/native/*/target/debug/build \
           "${dir}"/native/*/target/debug/incremental "${dir}"/native/*/target/debug/.fingerprint; do
    [[ -e "${p}" ]] && printf '%s\n' "${p}"
  done
  return 0
}

# Free space the build needs, in KiB. 15 GiB is the COLD first-build figure: wine
# (~1.3 GiB installed plus its .deb downloads), the pnpm store (~3 GiB), the electron /
# electron-builder caches, the Rust toolchain with the Windows target, and the build
# outputs. Once those are on disk a rebuild only adds the source tree, node_modules
# hardlinks, the cargo target and the desktop/web output: a few GiB. Demanding 15 GiB for
# every rebuild turned a 95%-full disk into a hard failure although the rebuild itself
# would have fitted. T3CODE_BUILD_MIN_FREE_GIB (integer) overrides the whole heuristic.
#   $1..$4 = 1/0 flags: wine installed, pnpm store present, electron cache present,
#   Rust Windows target installed.
t3_build_required_kb() {
  local wine="$1" store="$2" electron="$3" rust="$4" gib=6
  if [[ "${T3CODE_BUILD_MIN_FREE_GIB:-}" =~ ^[0-9]+$ ]]; then
    echo $(( T3CODE_BUILD_MIN_FREE_GIB * 1048576 )); return 0
  fi
  [[ "${wine}" == 1 ]] || gib=$(( gib + 4 ))
  [[ "${store}" == 1 ]] || gib=$(( gib + 3 ))
  [[ "${electron}" == 1 ]] || gib=$(( gib + 1 ))
  [[ "${rust}" == 1 ]] || gib=$(( gib + 1 ))
  echo $(( gib * 1048576 ))
}

# Probe the toolchain state as the four 1/0 flags above (space-separated, one line).
# HOME and PATH (dpkg-query, pnpm) are honoured so tests can point them at fixtures.
t3_build_toolchain_flags() {
  local wine=0 store=0 electron=0 rust=0 store_path
  dpkg-query -W -f='${Status}' wine64 2>/dev/null | grep -q 'install ok installed' && wine=1
  store_path="$(pnpm store path 2>/dev/null || true)"
  [[ -n "${store_path}" ]] || store_path="${HOME}/.local/share/pnpm/store"
  [[ -d "${store_path}" && -n "$(ls -A "${store_path}" 2>/dev/null)" ]] && store=1
  [[ -d "${HOME}/.cache/electron" && -n "$(ls -A "${HOME}/.cache/electron" 2>/dev/null)" ]] && electron=1
  if [[ -d "${HOME}/.rustup/toolchains" ]]; then
    ls -d "${HOME}"/.rustup/toolchains/*/lib/rustlib/x86_64-pc-windows-gnu >/dev/null 2>&1 && rust=1
  fi
  echo "${wine} ${store} ${electron} ${rust}"
}

# Sourced for the helpers only (unit tests): stop before anything resolves or installs.
if [[ "${_FUNCS_ONLY:-}" == "true" ]]; then
  return 0 2>/dev/null || exit 0
fi

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
BUILD_HASH="$(printf '%s\n%s\n' "${PATCH_HASH}" "${CONSTRUCT_VERSION}" | sha256sum | awk '{print $1}')"
SOURCE_KEY="${SAFE_VERSION}-${BUILD_HASH:0:12}"
SOURCE_DIR="${CACHE_ROOT}/${SOURCE_KEY}"

mkdir -p "${CACHE_ROOT}" "${ARTIFACT_ROOT}" /etc/construct

if [[ -s "${MANIFEST_PATH}" && -s "${INSTALLER_PATH}" ]]; then
  cached_version="$(node -e 'try{let m=require(process.argv[1]);process.stdout.write(m.version||"")}catch{}' "${MANIFEST_PATH}")"
  cached_hash="$(node -e 'try{let m=require(process.argv[1]);process.stdout.write(m.patchHash||"")}catch{}' "${MANIFEST_PATH}")"
  cached_construct="$(node -e 'try{let m=require(process.argv[1]);process.stdout.write(m.constructVersion||"")}catch{}' "${MANIFEST_PATH}")"
  if [[ "${cached_version}" == "${VERSION}" && "${cached_hash}" == "${PATCH_HASH}" && "${cached_construct}" == "${CONSTRUCT_VERSION}" && -x "${SOURCE_DIR}/apps/server/dist/bin.mjs" ]]; then
    ln -sfn "${SOURCE_DIR}/apps/server/dist/bin.mjs" /usr/local/bin/t3
    printf 'T3CODE_DESKTOP_READY=yes\nT3CODE_DESKTOP_VERSION=%s\nT3CODE_DESKTOP_CHANNEL=%s\nT3CODE_DESKTOP_INSTALLER=%s\nT3CODE_BUILD_KEY=%s\n' \
      "${VERSION}" "${CHANNEL}" "${INSTALLER_PATH}" "${BUILD_HASH}" >"${STATUS_PATH}"
    note "T3 Code ${VERSION} patched build is already current; reusing its VM server and Windows installer."
    exit 0
  fi
fi

# Do not let a failed rebuild advertise an older installer as the result of the
# current provision. The previous server binary remains installed until the new
# build is complete, but the host handoff is re-enabled only after success.
rm -f "${STATUS_PATH}"

# Reclaim the regenerable parts of superseded builds before checking free space (see
# t3_build_prune_candidates for what stays: the source and the server bundle of the build
# that is CURRENTLY installed keep working until this build succeeds, as promised above).
reclaimed_kb=0
for stale_dir in "${CACHE_ROOT}"/*/; do
  stale_dir="${stale_dir%/}"
  [[ -d "${stale_dir}" ]] || continue
  [[ "${stale_dir}" == "${SOURCE_DIR}" ]] && continue
  while IFS= read -r victim; do
    [[ -n "${victim}" ]] || continue
    victim_kb="$(du -sxk -- "${victim}" 2>/dev/null | awk '{print $1}')"
    rm -rf -- "${victim}"
    reclaimed_kb=$(( reclaimed_kb + ${victim_kb:-0} ))
  done < <(t3_build_prune_candidates "${stale_dir}")
done
if [[ "${reclaimed_kb}" -gt 0 ]]; then
  note "Reclaimed $(( reclaimed_kb / 1024 )) MiB of regenerable output from superseded T3 builds (their source + server bundle stay until this build succeeds)."
fi

# The requirement depends on how much of the toolchain is already on disk.
read -r tc_wine tc_store tc_electron tc_rust <<<"$(t3_build_toolchain_flags)"
required_kb="$(t3_build_required_kb "${tc_wine}" "${tc_store}" "${tc_electron}" "${tc_rust}")"
available_kb="$(df -Pk "${CACHE_ROOT}" | awk 'NR==2 {print $4}')"
toolchain_note="wine=${tc_wine} pnpm-store=${tc_store} electron-cache=${tc_electron} rust-windows-target=${tc_rust}"
if [[ "${available_kb:-0}" -lt "${required_kb}" ]]; then
  fail "T3 source/Desktop build needs at least $(( required_kb / 1048576 )) GiB free in the VM; $(( ${available_kb:-0} / 1024 )) MiB available (toolchain already present: ${toolchain_note}; override with T3CODE_BUILD_MIN_FREE_GIB=<gib>)"
fi
note "Free space: $(( ${available_kb:-0} / 1024 )) MiB available, $(( required_kb / 1048576 )) GiB required (${toolchain_note})."

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

if [[ -e "${SOURCE_DIR}" && ! -d "${SOURCE_DIR}/.git" && ! -s "${SOURCE_DIR}/.construct-upstream-commit" ]]; then
  rm -rf -- "${SOURCE_DIR}"
fi
if [[ ! -d "${SOURCE_DIR}/.git" && ! -s "${SOURCE_DIR}/.construct-upstream-commit" ]]; then
  note "Downloading T3 Code ${TAG} (${CHANNEL}) source..."
  if ! GIT_TERMINAL_PROMPT=0 git clone --depth 1 --branch "${TAG}" --single-branch \
    https://github.com/pingdotgg/t3code.git "${SOURCE_DIR}"; then
    note "Git clone failed; downloading the matching GitHub source archive instead..."
    rm -rf -- "${SOURCE_DIR}"
    mkdir -p "${SOURCE_DIR}"
    archive="$(mktemp "${CACHE_ROOT}/t3code-${SAFE_VERSION}.XXXXXX.tar.gz")"
    curl -fsSL "https://codeload.github.com/pingdotgg/t3code/tar.gz/refs/tags/${TAG}" -o "${archive}"
    upstream_commit="$(curl -fsSL -H 'Accept: application/vnd.github+json' \
      "https://api.github.com/repos/pingdotgg/t3code/commits/${TAG}" \
      | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s).sha||""))')"
    [[ "${upstream_commit}" =~ ^[0-9a-f]{40}$ ]] || fail "GitHub returned an invalid commit for ${TAG}"
    tar -xzf "${archive}" --strip-components=1 -C "${SOURCE_DIR}"
    printf '%s\n' "${upstream_commit}" >"${SOURCE_DIR}/.construct-upstream-commit"
    rm -f -- "${archive}"
  fi
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

desktop_version="${VERSION}-construct.${BUILD_HASH:0:8}"
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

if [[ -d .git ]]; then
  commit="$(git rev-parse HEAD)"
else
  commit="$(tr -d '[:space:]' <.construct-upstream-commit)"
fi
installer_sha="$(sha256sum "${INSTALLER_PATH}" | awk '{print $1}')"
node - "${MANIFEST_PATH}.tmp" "${VERSION}" "${desktop_version}" "${CHANNEL}" "${TAG}" "${commit}" "${PATCH_HASH}" "${CONSTRUCT_VERSION}" "${BUILD_HASH}" "${installer_sha}" <<'NODE'
const fs = require("node:fs");
const [path, version, desktopVersion, channel, sourceTag, commit, patchHash, constructVersion, buildHash, sha256] = process.argv.slice(2);
fs.writeFileSync(path, JSON.stringify({
  version, desktopVersion, channel, sourceTag, commit, patchHash, constructVersion, buildHash, sha256,
  installer: "T3Code-Construct-Setup.exe",
  builtAt: new Date().toISOString(),
}, null, 2) + "\n");
NODE
mv -f "${MANIFEST_PATH}.tmp" "${MANIFEST_PATH}"

chmod +x apps/server/dist/bin.mjs
ln -sfn "${SOURCE_DIR}/apps/server/dist/bin.mjs" /usr/local/bin/t3
printf 'T3CODE_DESKTOP_READY=yes\nT3CODE_DESKTOP_VERSION=%s\nT3CODE_DESKTOP_CHANNEL=%s\nT3CODE_DESKTOP_INSTALLER=%s\nT3CODE_BUILD_KEY=%s\n' \
  "${VERSION}" "${CHANNEL}" "${INSTALLER_PATH}" "${BUILD_HASH}" >"${STATUS_PATH}"

# Keep only the selected channel build. pnpm's global content store retains shared
# packages, while stale checked-out node_modules trees would otherwise consume the VM disk.
find "${CACHE_ROOT}" -mindepth 1 -maxdepth 1 -type d ! -name "${SOURCE_KEY}" -exec rm -rf -- {} +
find "${ARTIFACT_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'build-*' -exec rm -rf -- {} +
note "Patched T3 ${VERSION} server and Windows installer are ready."
