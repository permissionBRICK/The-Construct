#!/usr/bin/env bash
# Build Construct's patched T3 Code server and unsigned Windows Desktop installer
# from the exact Git tag published on the selected npm channel.

# ── Superseded-build pruning + free-space requirement ────────────────────────────
# Pure helpers, unit-tested by test/t3-build-diskcheck.test.sh, which sources this file
# with _FUNCS_ONLY=true. They sit ABOVE `set -Eeuo pipefail` on purpose: helper-only
# sourcing must not change the caller's shell options.
#
# What a build directory must keep depends on its role:
#   * the INSTALLED build (/usr/local/bin/t3 resolves into it) keeps everything the
#     server uses at run time: the source checkout, apps/server/dist (the web client is
#     bundled at dist/client), its node_modules tree (the bundle leaves native deps such
#     as node-pty external and imports them at run time through apps/server/node_modules,
#     which symlinks into the root tree) and any resource-monitor executable under
#     native/*/target/{release,debug}. Only regenerable OUTPUT goes: the Windows
#     cross-compile targets, cargo intermediates, the desktop output and apps/web/dist
#     (the monorepo dev-server fallback; the server serves dist/client).
#   * the build being produced now is never touched.
#   * any OTHER superseded build is removed entirely -- nothing runs from it.
t3_build_prune_candidates() {
  local dir="$1" p
  for p in apps/desktop/release apps/desktop/dist apps/web/dist apps/mobile/dist apps/marketing/dist; do
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

# The build directory the installed server runs from ("" when t3 is not installed
# from a source build). $1 = the t3 launcher path (default /usr/local/bin/t3).
t3_build_installed_dir() {
  local launcher="${1:-/usr/local/bin/t3}" target
  target="$(readlink -f -- "${launcher}" 2>/dev/null || true)"
  [[ "${target}" == */apps/server/dist/bin.mjs ]] || { echo ""; return 0; }
  echo "${target%/apps/server/dist/bin.mjs}"
}

# Free space the build needs, in KiB. 15 GiB is the COLD first-build figure: wine
# (~1.3 GiB installed plus its .deb downloads), the pnpm store (~3 GiB), the electron /
# electron-builder caches, the Rust toolchain with the Windows target, and the build
# outputs. Once those are on disk a rebuild only adds the source tree, node_modules
# hardlinks, the cargo target and the desktop/web output: a few GiB. Demanding 15 GiB for
# every rebuild turned a 95%-full disk into a hard failure although the rebuild itself
# would have fitted. Caches are credited by their MEASURED size up to each cap, so a
# stray file in an otherwise empty store does not count as warm.
#   $1 wine installed (1/0)   $2 pnpm store size in MiB   $3 electron cache size in MiB
#   $4 Rust Windows target installed in the ACTIVE toolchain (1/0)
#   $5 optional build mode (server avoids budgeting the Windows toolchain)
# T3CODE_BUILD_MIN_FREE_GIB (integer, 1..9999) overrides the whole heuristic.
t3_build_required_kb() {
  local wine="$1" store_mib="$2" electron_mib="$3" rust="$4" mode="${5:-all}" gib=6 credit
  if [[ "${T3CODE_BUILD_MIN_FREE_GIB:-}" =~ ^[0-9]{1,4}$ && "$(( 10#${T3CODE_BUILD_MIN_FREE_GIB} ))" -ge 1 ]]; then
    echo $(( 10#${T3CODE_BUILD_MIN_FREE_GIB} * 1048576 )); return 0
  fi
  # A server-only run needs neither Wine nor the Windows Rust toolchain.
  if [[ "${mode}" == server ]]; then gib=3; else
    [[ "${wine}" == 1 ]] || gib=$(( gib + 4 ))
  fi
  [[ "${store_mib}" =~ ^[0-9]+$ ]] || store_mib=0
  [[ "${electron_mib}" =~ ^[0-9]+$ ]] || electron_mib=0
  # pnpm store: 3 GiB cap, credited per whole GiB present (2.x GiB present -> 1 GiB still needed).
  credit=$(( store_mib / 1024 )); [[ "${credit}" -gt 3 ]] && credit=3
  gib=$(( gib + 3 - credit ))
  # electron caches: 1 GiB cap, credited once at least 100 MiB (the electron zip) is there.
  [[ "${electron_mib}" -ge 100 ]] || gib=$(( gib + 1 ))
  [[ "${mode}" == server || "${rust}" == 1 ]] || gib=$(( gib + 1 ))
  echo $(( gib * 1048576 ))
}

# Probe the toolchain state as "<wine> <store_mib> <electron_mib> <rust>" (one line).
# HOME and PATH (dpkg-query, pnpm, rustup) are honoured so tests can use fixtures.
t3_build_toolchain_flags() {
  local wine=0 store_mib=0 electron_mib=0 rust=0 store_path d
  dpkg-query -W -f='${Status}' wine64 2>/dev/null | grep -q 'install ok installed' && wine=1
  store_path="$(pnpm store path 2>/dev/null || true)"
  [[ -n "${store_path}" ]] || store_path="${HOME}/.local/share/pnpm/store"
  [[ -d "${store_path}" ]] && store_mib="$(du -sxm -- "${store_path}" 2>/dev/null | awk '{print $1+0}')"
  for d in "${HOME}/.cache/electron" "${HOME}/.cache/electron-builder"; do
    [[ -d "${d}" ]] && electron_mib=$(( electron_mib + $(du -sxm -- "${d}" 2>/dev/null | awk '{print $1+0}') ))
  done
  # The Windows target only counts in the ACTIVE toolchain (rustup target add works on that one).
  PATH="${HOME}/.cargo/bin:${PATH}" rustup target list --installed 2>/dev/null | grep -qx 'x86_64-pc-windows-gnu' && rust=1
  echo "${wine} ${store_mib:-0} ${electron_mib:-0} ${rust}"
}


# Return the newest pushed, validated nightly repair ref from ls-remote input.
# The repair supervisor only pushes these branches after its own exact-patch and
# focused-test validation, so an unpublished/incomplete agent attempt is never a
# candidate. Version/date/build components are zero-padded by upstream and the
# watcher, making lexical order deterministic.
t3_build_latest_nightly_fix_ref() {
  sed -n 's/^[0-9a-f]\{40,64\}[[:space:]]\+\(refs\/heads\/fix\/upstream-t3-nightly-[0-9A-Za-z._-]*\)$/\1/p' \
    | LC_ALL=C sort | tail -1
}

# Clone the newest ready nightly repair branch into $2. Prints its ref on
# success. Network failure or no ready branch is an ordinary miss: callers keep
# the normal failure path rather than making recovery availability mandatory.
t3_build_fetch_nightly_candidate() {
  local repo_url="$1" destination="$2" ref branch
  ref="$(GIT_TERMINAL_PROMPT=0 git ls-remote --heads "${repo_url}" \
    'refs/heads/fix/upstream-t3-nightly-*' 2>/dev/null | t3_build_latest_nightly_fix_ref)"
  [[ -n "${ref}" ]] || return 1
  branch="${ref#refs/heads/}"
  rm -rf -- "${destination}"
  GIT_TERMINAL_PROMPT=0 git clone --quiet --depth 1 --single-branch --branch "${branch}" \
    "${repo_url}" "${destination}" || { rm -rf -- "${destination}"; return 1; }
  printf '%s\n' "${ref}"
}

# Check both runtime patchers against the published npm bundle for a version.
# This catches compatibility failures before the expensive source/Desktop build,
# which is early enough for stable to switch to a ready nightly repair inventory.
t3_build_bundle_patchers_compatible() {
  local version="$1" t3park="$2" monitor="$3" work tgz bundle status
  work="$(mktemp -d)"
  if ! npm pack "t3@${version}" --pack-destination "${work}" --silent >/dev/null 2>&1; then
    rm -rf -- "${work}"; return 1
  fi
  tgz="$(find "${work}" -maxdepth 1 -type f -name '*.tgz' -print -quit)"
  [[ -n "${tgz}" ]] && tar -xzf "${tgz}" -C "${work}" >/dev/null 2>&1 || { rm -rf -- "${work}"; return 1; }
  bundle="${work}/package/dist/bin.mjs"
  [[ -s "${bundle}" ]] || { rm -rf -- "${work}"; return 1; }
  for patcher in "${t3park}" "${monitor}"; do
    status="$(node "${patcher}" status --bundle "${bundle}" 2>/dev/null || true)"
    node -e 'try{process.exit(JSON.parse(process.argv[1]).compatible===true?0:1)}catch{process.exit(1)}' "${status}" \
      || { rm -rf -- "${work}"; return 1; }
  done
  rm -rf -- "${work}"
}

# Paths are relative to the recipe's repository, so identical inputs in different
# checkouts have the same identity. Only build inputs belong here, not this driver.
t3_build_integration_hash() {
  local recipe="$1" transform_script="$2" manifest="$3" overlays="$4" t3park="$5" monitor="$6"
  local root file
  root="$(cd "$(dirname "${recipe}")/.." && pwd)"
  (
    cd "${root}"
    for file in "${recipe}" "${transform_script}" "${manifest}" "${t3park}" "${monitor}"; do
      sha256sum "${file#"${root}/"}"
    done
    find "${overlays#"${root}/"}" -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum
  ) | sha256sum | awk '{print $1}'
}

t3_build_manifest_field() {
  node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"))[process.argv[2]]||""))}catch{}' "$1" "$2"
}

# Sourced for the helpers only (unit tests): stop before shell options or anything else changes.
if [[ "${_FUNCS_ONLY:-}" == "true" ]]; then
  return 0 2>/dev/null || exit 0
fi
set -Eeuo pipefail

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CHANNEL="${T3CODE_CHANNEL:-stable}"
[[ "${CHANNEL}" == "nightly" ]] || CHANNEL=stable
NPM_TAG=latest
[[ "${CHANNEL}" == "nightly" ]] && NPM_TAG=nightly

# Overrides support isolated build verification; normal provisions use these defaults.
CACHE_ROOT="${T3CODE_CACHE_ROOT:-/var/cache/construct/t3code-source}"
COMPILER_CACHE="${T3CODE_COMPILER_CACHE:-/var/cache/construct/t3code-compiler}"
ARTIFACT_ROOT="${T3CODE_ARTIFACT_ROOT:-/var/lib/construct/t3code-desktop}"
LAUNCHER="${T3CODE_LAUNCHER:-/usr/local/bin/t3}"
BUILD_MODE="${T3CODE_BUILD_MODE:-all}"
RECIPE="${REPO_DIR}/bin/t3code-build-recipe.sh"
SOURCE_TRANSFORMER="${REPO_DIR}/bin/apply-t3code-source.mjs"
CONSTRUCT_REPO_URL="${CONSTRUCT_REPO_URL:-https://github.com/permissionBRICK/The-Construct.git}"
INSTALLER_PATH="${ARTIFACT_ROOT}/T3Code-Construct-Setup.exe"
MANIFEST_PATH="${ARTIFACT_ROOT}/manifest.json"
STATUS_PATH="${T3CODE_STATUS_PATH:-/etc/construct/t3code-desktop-status}"
SERVER_MANIFEST="${ARTIFACT_ROOT}/server-manifest.json"
CONSTRUCT_VERSION="${CONSTRUCT_VERSION:-unversioned}"
[[ "${CONSTRUCT_VERSION}" =~ ^[0-9a-f]{7,64}$ ]] || CONSTRUCT_VERSION=unversioned
note() { printf '    %s\n' "$*"; }
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

case "${BUILD_MODE}" in all|server|desktop) ;; *) fail "Invalid T3CODE_BUILD_MODE: ${BUILD_MODE}" ;; esac

# Resolve channels and run the source tooling with the workspace's Node major.
node_major="$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)"
if ! command -v npm >/dev/null 2>&1 || [[ "${node_major}" -lt 24 ]]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends ca-certificates curl
  curl -fsSL https://deb.nodesource.com/setup_24.x | bash -
  apt-get install -y nodejs
fi
command -v npm >/dev/null 2>&1 || fail "npm is required to resolve the T3 channel"

# Packaging uses the exact server selected by this provision, even if the npm
# channel advances between the two stages. It never recompiles or activates it.
if [[ "${BUILD_MODE}" == desktop ]]; then
  VERSION="$(t3_build_manifest_field "${SERVER_MANIFEST}" version)"
  CHANNEL="$(t3_build_manifest_field "${SERVER_MANIFEST}" channel)"
elif [[ -n "${T3CODE_SOURCE_VERSION:-}" ]]; then
  VERSION="${T3CODE_SOURCE_VERSION}"
else
  VERSION="$(npm view "t3@${NPM_TAG}" version 2>/dev/null | tail -1 | tr -d '[:space:]')"
fi
[[ "${VERSION}" =~ ^[0-9A-Za-z.+-]+$ ]] || fail "No valid T3 version resolved for ${BUILD_MODE} build"
[[ "${CHANNEL}" == stable || "${CHANNEL}" == nightly ]] || fail "Invalid cached T3 channel"
TAG="v${VERSION}"
SAFE_VERSION="${VERSION//[^0-9A-Za-z._-]/-}"
INVENTORY_NAME=release
[[ "${CHANNEL}" == nightly ]] && INVENTORY_NAME=nightly
if [[ "${BUILD_MODE}" == desktop ]]; then
  cached_inventory="$(t3_build_manifest_field "${SERVER_MANIFEST}" inventory)"
  INVENTORY_NAME="${cached_inventory:-${INVENTORY_NAME}}"
else
  INVENTORY_NAME="${T3CODE_INVENTORY:-${INVENTORY_NAME}}"
fi
[[ "${INVENTORY_NAME}" == release || "${INVENTORY_NAME}" == nightly ]] || fail "Invalid T3 source inventory"
SOURCE_MANIFEST="${REPO_DIR}/patches/t3code-${INVENTORY_NAME}/source-transforms.json"
SOURCE_OVERLAYS="${REPO_DIR}/patches/t3code-${INVENTORY_NAME}/overlays"
T3PARK_PATCHER="${REPO_DIR}/extension/vm/construct-t3park-patch.mjs"
T3MONITOR_PATCHER="${REPO_DIR}/extension/vm/construct-t3-opencode-monitor-patch.mjs"
[[ -s "${RECIPE}" && -s "${SOURCE_TRANSFORMER}" && -s "${SOURCE_MANIFEST}" && -d "${SOURCE_OVERLAYS}" ]] \
  || fail "T3 build inputs are incomplete under ${REPO_DIR}"
# shellcheck source=bin/t3code-build-recipe.sh
source "${RECIPE}"
mkdir -p "${CACHE_ROOT}" "${ARTIFACT_ROOT}" "$(dirname "${STATUS_PATH}")"
PATCH_HASH="$(t3_build_integration_hash "${RECIPE}" "${SOURCE_TRANSFORMER}" "${SOURCE_MANIFEST}" "${SOURCE_OVERLAYS}" "${T3PARK_PATCHER}" "${T3MONITOR_PATCHER}")"
BUILD_HASH="$(printf '%s\n' "${VERSION}" "${CHANNEL}" "${PATCH_HASH}" | sha256sum | awk '{print $1}')"
SOURCE_KEY="${SAFE_VERSION}-${BUILD_HASH:0:12}"
SOURCE_DIR="${CACHE_ROOT}/${SOURCE_KEY}"

server_current=false
if [[ "$(t3_build_manifest_field "${SERVER_MANIFEST}" version)" == "${VERSION}" &&
      "$(t3_build_manifest_field "${SERVER_MANIFEST}" channel)" == "${CHANNEL}" &&
      "$(t3_build_manifest_field "${SERVER_MANIFEST}" patchHash)" == "${PATCH_HASH}" &&
      -x "${SOURCE_DIR}/apps/server/dist/bin.mjs" ]]; then
  server_current=true
fi
if [[ "${BUILD_MODE}" == desktop && "${server_current}" != true ]]; then
  fail "The prepared T3 server no longer matches this recipe; provision it again before packaging"
fi
write_status() {
  printf 'T3CODE_SERVER_READY=yes\nT3CODE_DESKTOP_READY=%s\nT3CODE_DESKTOP_VERSION=%s\nT3CODE_DESKTOP_CHANNEL=%s\nT3CODE_DESKTOP_INSTALLER=%s\nT3CODE_BUILD_KEY=%s\n' \
    "$1" "${VERSION}" "${CHANNEL}" "${INSTALLER_PATH}" "${BUILD_HASH}" >"${STATUS_PATH}"
}
activate_server() {
  ln -sfn "${SOURCE_DIR}/apps/server/dist/bin.mjs" "${LAUNCHER}"
  write_status no
}

if [[ "${server_current}" == true ]]; then
  note "T3 Code ${VERSION} server is already current (same upstream version and build recipe)."
  if [[ "${BUILD_MODE}" == server ]]; then activate_server; fi
  if [[ "${BUILD_MODE}" == server ]]; then exit 0; fi
else
note "T3 build cache miss: upstream version/channel or recipe changed, or the cached server is missing."
# Do not let a failed rebuild advertise an older installer as the result of the
# current provision. The previous server binary remains installed until the new
# build is complete, but the host handoff is re-enabled only after success.
rm -f "${STATUS_PATH}"

# Reclaim superseded builds before checking free space. The build the installed server
# runs from only loses regenerable output (see t3_build_prune_candidates), so it keeps
# working -- including after a restart -- until this build succeeds, as promised above.
# Other superseded builds are removed whole: nothing runs from them.
installed_dir="$(t3_build_installed_dir "${LAUNCHER}")"
free_before_kb="$(df -Pk "${CACHE_ROOT}" | awk 'NR==2 {print $4}')"
for stale_dir in "${CACHE_ROOT}"/*/; do
  stale_dir="${stale_dir%/}"
  [[ -d "${stale_dir}" ]] || continue
  [[ "${stale_dir}" == "${SOURCE_DIR}" ]] && continue
  if [[ -n "${installed_dir}" && "${stale_dir}" == "${installed_dir}" ]]; then
    while IFS= read -r victim; do
      [[ -n "${victim}" ]] && rm -rf -- "${victim}"
    done < <(t3_build_prune_candidates "${stale_dir}")
  else
    note "Removing superseded T3 build $(basename -- "${stale_dir}") (nothing runs from it)..."
    rm -rf -- "${stale_dir}"
  fi
done
free_after_kb="$(df -Pk "${CACHE_ROOT}" | awk 'NR==2 {print $4}')"
if [[ "${free_after_kb:-0}" -gt "${free_before_kb:-0}" ]]; then
  note "Freed $(( (free_after_kb - free_before_kb) / 1024 )) MiB from superseded T3 builds (the installed build's source, server bundle and dependencies stay until this build succeeds)."
fi

# The requirement depends on how much of the toolchain is already on disk.
read -r tc_wine tc_store_mib tc_electron_mib tc_rust <<<"$(t3_build_toolchain_flags)"
required_kb="$(t3_build_required_kb "${tc_wine}" "${tc_store_mib}" "${tc_electron_mib}" "${tc_rust}" "${BUILD_MODE}")"
available_kb="$(df -Pk "${CACHE_ROOT}" | awk 'NR==2 {print $4}')"
toolchain_note="wine=${tc_wine} pnpm-store=${tc_store_mib}MiB electron-cache=${tc_electron_mib}MiB rust-windows-target=${tc_rust}"
if [[ "${available_kb:-0}" -lt "${required_kb}" ]]; then
  fail "T3 source/Desktop build needs at least $(( required_kb / 1048576 )) GiB free in the VM; $(( ${available_kb:-0} / 1024 )) MiB available (toolchain already present: ${toolchain_note}; override with T3CODE_BUILD_MIN_FREE_GIB=<gib>)"
fi
note "Free space: $(( ${available_kb:-0} / 1024 )) MiB available, $(( required_kb / 1048576 )) GiB required (${toolchain_note})."

t3_recipe_prepare_source

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

t3_recipe_compile
chmod +x apps/server/dist/bin.mjs
if [[ -d .git ]]; then
  commit="$(git rev-parse HEAD)"
else
  commit="$(tr -d '[:space:]' <.construct-upstream-commit)"
fi
node - "${SERVER_MANIFEST}.tmp" "${VERSION}" "${CHANNEL}" "${TAG}" "${commit}" "${PATCH_HASH}" "${CONSTRUCT_VERSION}" "${BUILD_HASH}" "${INVENTORY_NAME}" <<'NODE'
const fs = require("node:fs");
const [path, version, channel, sourceTag, commit, patchHash, constructVersion, buildHash, inventory] = process.argv.slice(2);
fs.writeFileSync(path, JSON.stringify({version, channel, sourceTag, commit, patchHash, constructVersion, buildHash, inventory}, null, 2) + "\n");
NODE
mv -f "${SERVER_MANIFEST}.tmp" "${SERVER_MANIFEST}"
if [[ "${BUILD_MODE}" == server ]]; then activate_server; fi
# Keep the previously active server until provisioning has restarted the service.
# The compiler cache lives outside CACHE_ROOT and survives source-tree pruning.
find "${CACHE_ROOT}" -mindepth 1 -maxdepth 1 -type d ! -name "${SOURCE_KEY}" \
  ! -name "$(basename "${installed_dir:-none}")" -exec rm -rf -- {} +
fi

if [[ "${BUILD_MODE}" == server ]]; then
  note "T3 server is ready; Windows packaging is deferred until the host needs it."
  exit 0
fi

# A server-only provision does not discard a previously built installer. Reuse it
# when another host later requests the same Desktop, without reinstalling deps.
if [[ "$(t3_build_manifest_field "${MANIFEST_PATH}" version)" == "${VERSION}" &&
      "$(t3_build_manifest_field "${MANIFEST_PATH}" channel)" == "${CHANNEL}" &&
      "$(t3_build_manifest_field "${MANIFEST_PATH}" patchHash)" == "${PATCH_HASH}" &&
      -s "${INSTALLER_PATH}" &&
      "$(t3_build_manifest_field "${MANIFEST_PATH}" sha256)" == "$(sha256sum "${INSTALLER_PATH}" | awk '{print $1}')" ]]; then
  if [[ "${BUILD_MODE}" == all ]]; then activate_server; fi
  write_status yes
  note "Windows installer is already cached; skipping packaging."
  exit 0
fi
write_status no
# Packaging can be requested long after the server was compiled; check its
# current free space separately before installing the Windows toolchain.
read -r tc_wine tc_store_mib tc_electron_mib tc_rust <<<"$(t3_build_toolchain_flags)"
required_kb="$(t3_build_required_kb "${tc_wine}" "${tc_store_mib}" "${tc_electron_mib}" "${tc_rust}")"
available_kb="$(df -Pk "${CACHE_ROOT}" | awk '{if (NR==2) print $4}')"
[[ "${available_kb:-0}" -ge "${required_kb}" ]] || fail "Windows packaging needs $(( required_kb / 1048576 )) GiB free; $(( ${available_kb:-0} / 1024 )) MiB available"
cd "${SOURCE_DIR}"
export PATH="${SOURCE_DIR}/node_modules/.bin:/root/.cargo/bin:${PATH}"
desktop_version="${VERSION}-construct.${BUILD_HASH:0:8}"
output_dir="${ARTIFACT_ROOT}/build-${SOURCE_KEY}"
t3_recipe_package "${desktop_version}" "${output_dir}"
built_installer="$(find "${output_dir}" -maxdepth 1 -type f -name '*.exe' ! -name '*unpacked*' -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)"
[[ -n "${built_installer}" && -s "${built_installer}" ]] || fail "electron-builder did not produce a Windows installer"
cp "${built_installer}" "${INSTALLER_PATH}.tmp"
mv -f "${INSTALLER_PATH}.tmp" "${INSTALLER_PATH}"
chmod 0644 "${INSTALLER_PATH}"
installer_sha="$(sha256sum "${INSTALLER_PATH}" | awk '{print $1}')"
node - "${SERVER_MANIFEST}" "${MANIFEST_PATH}.tmp" "${desktop_version}" "${installer_sha}" <<'NODE'
const fs = require("node:fs");
const [server, path, desktopVersion, sha256] = process.argv.slice(2);
const manifest = JSON.parse(fs.readFileSync(server, "utf8"));
fs.writeFileSync(path, JSON.stringify({...manifest, desktopVersion, sha256,
  installer: "T3Code-Construct-Setup.exe", builtAt: new Date().toISOString()}, null, 2) + "\n");
NODE
mv -f "${MANIFEST_PATH}.tmp" "${MANIFEST_PATH}"
if [[ "${BUILD_MODE}" == all ]]; then activate_server; fi
write_status yes
find "${ARTIFACT_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'build-*' -exec rm -rf -- {} +
note "Patched T3 ${VERSION} server and Windows installer are ready."
