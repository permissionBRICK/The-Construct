#!/usr/bin/env bash
# Artifact-producing recipe. This file (unlike the cache/provisioning driver) is
# hashed into the build identity: change it when build semantics must invalidate
# existing server/Desktop artifacts. Sourced by build-t3code.sh.

# Pinned alongside the artifact recipe so runtime changes invalidate old native
# dependencies. This pin matches construct-t3-builds/.node-version.
# Keep old runtimes: an installed server may still use one.
t3_recipe_use_node() {
  local node_version=26.8.1 arch checksum
  case "$(uname -m)" in
    x86_64) arch=x64; checksum=b2b76660fa4ded4e0b2a41ee3c0c651cd52ea8170ead91ebac1e147ac3d55643 ;;
    aarch64) arch=arm64; checksum=d5f973ce975e4bd03e6c2038260f7e9201615aa8e1ee293c72f8dcc2a6d9fddb ;;
    *) fail "Unsupported T3 build Node architecture: $(uname -m)" ;;
  esac
  local root="${T3CODE_NODE_ROOT:-/opt/construct/toolchains}" archive="node-v${node_version}-linux-${arch}.tar.gz"
  mkdir -p "${root}"
  root="$(cd "${root}" && pwd)"
  T3_NODE_DIR="${root}/node-v${node_version}-linux-${arch}"
  # The server's interpreter is an absolute shebang, leaving its child agents'
  # PATH (and therefore their project SDK / existing npx cache ABI) alone.
  [[ "${T3_NODE_DIR}" != *[[:space:]]* && ${#T3_NODE_DIR} -lt 240 ]] \
    || fail "T3 Node runtime path must fit an absolute shebang without whitespace"
  (
    flock -x 9
    if [[ ! -e "${T3_NODE_DIR}" ]]; then
      local stage
      stage="$(mktemp -d "${root}/.node-download.XXXXXX")"
      trap 'rm -rf -- "${stage}"' EXIT
      note "Installing private T3 build Node ${node_version} (${arch})..."
      curl -fsSL "https://nodejs.org/dist/v${node_version}/${archive}" -o "${stage}/${archive}"
      printf '%s  %s\n' "${checksum}" "${stage}/${archive}" | sha256sum -c -
      tar -xzf "${stage}/${archive}" -C "${stage}"
      [[ "$("${stage}/${archive%.tar.gz}/bin/node" --version)" == "v${node_version}" ]] \
        || fail "Downloaded T3 Node version does not match the recipe"
      mv "${stage}/${archive%.tar.gz}" "${T3_NODE_DIR}"
    fi
  ) 9>"${root}/.node-install.lock"
  [[ "$("${T3_NODE_DIR}/bin/node" --version)" == "v${node_version}" && -x "${T3_NODE_DIR}/bin/npm" ]] \
    || fail "Incomplete private T3 Node runtime: ${T3_NODE_DIR}; repair its inactive entry before retrying"
  export PATH="${T3_NODE_DIR}/bin:${PATH}"
  # Build-time npx trees must never share the user's native-addon cache.
  export npm_config_cache="${COMPILER_CACHE}/npm"
}

t3_recipe_prepare_source() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends build-essential ca-certificates curl git python3
  if [[ ! -x "${T3_NODE_DIR}/bin/pnpm" ]] || [[ "$("${T3_NODE_DIR}/bin/pnpm" --version 2>/dev/null)" != 11.10.0 ]]; then
    npm install --prefix "${T3_NODE_DIR}" -g pnpm@11.10.0
  fi
}

t3_recipe_compile() {
  node "${SOURCE_TRANSFORMER}" apply --source "${SOURCE_DIR}" --manifest "${SOURCE_MANIFEST}" --overlays "${SOURCE_OVERLAYS}" \
    || fail "the Construct source transforms do not apply to T3 Code ${TAG}; repair the inventory for this upstream version"
  note "Installing T3 source dependencies (reusing the pnpm package store)..."
  pnpm install --no-frozen-lockfile
  export PATH="${SOURCE_DIR}/node_modules/.bin:${PATH}"
  node scripts/update-release-package-versions.ts "${VERSION}"
  # Compile the shared sources once. Keeping Desktop's JS beside the server lets
  # later Windows packaging use --skip-build without rewriting a running server.
  note "Building the shared T3 server/web/Desktop sources..."
  pnpm run build:desktop
  node "${T3PARK_PATCHER}" apply --bundle "${SOURCE_DIR}/apps/server/dist/bin.mjs"
  node "${T3MONITOR_PATCHER}" apply --bundle "${SOURCE_DIR}/apps/server/dist/bin.mjs"
  node - "${SOURCE_DIR}/apps/server/dist/bin.mjs" "${T3_NODE_DIR}/bin/node" <<'NODE'
const fs = require('node:fs');
const [bundle, interpreter] = process.argv.slice(2);
const source = fs.readFileSync(bundle, 'utf8');
if (!source.startsWith('#!')) throw new Error('T3 server bundle has no interpreter line');
fs.writeFileSync(bundle, '#!' + interpreter + source.slice(source.indexOf('\n')));
NODE
}

t3_recipe_package() {
  local desktop_version="$1" output_dir="$2"
  export DEBIAN_FRONTEND=noninteractive
  if ! dpkg --print-foreign-architectures | grep -qx i386; then
    dpkg --add-architecture i386
  fi
  apt-get update
  apt-get install -y --no-install-recommends mingw-w64 wine wine64 wine32:i386
  if ! command -v rustup >/dev/null 2>&1; then
    curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal
  fi
  export PATH="${HOME}/.cargo/bin:${PATH}"
  rustup target add x86_64-pc-windows-gnu
  note "Cross-building the Windows resource monitor (reusing the Cargo compiler cache)..."
  cargo build --locked --release --manifest-path native/resource-monitor/Cargo.toml \
    --target x86_64-pc-windows-gnu --target-dir "${COMPILER_CACHE}/resource-monitor"
  mkdir -p native/resource-monitor/target/x86_64-pc-windows-msvc/release
  cp "${COMPILER_CACHE}/resource-monitor/x86_64-pc-windows-gnu/release/t3-resource-monitor.exe" \
    native/resource-monitor/target/x86_64-pc-windows-msvc/release/t3-resource-monitor.exe
  local pty_manifest pty_dir pty_prebuild
  pty_manifest="$(node -e 'console.log(require.resolve("node-pty/package.json",{paths:[process.argv[1]]}))' "${SOURCE_DIR}/apps/server")"
  pty_dir="$(dirname "${pty_manifest}")"
  pty_prebuild="${pty_dir}/build/Release/pty.node"
  [[ -s "${pty_prebuild}" ]] || fail "Linux node-pty prebuild was not produced: ${pty_prebuild}"
  # Wine's tool setup is reusable across recipe/version changes, too.
  note "Packaging unsigned Windows x64 installer..."
  WINEPREFIX="${COMPILER_CACHE}/wine" WINEDEBUG=-all \
    T3CODE_DESKTOP_REUSE_RESOURCE_MONITOR=true \
    node scripts/build-desktop-artifact.ts \
      --platform win --target nsis --arch x64 --skip-build \
      --build-version "${desktop_version}" --output-dir "${output_dir}" \
      --wsl-prebuild "${pty_prebuild}"
}
