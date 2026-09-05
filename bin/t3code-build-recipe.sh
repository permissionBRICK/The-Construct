#!/usr/bin/env bash
# Artifact-producing recipe. This file (unlike the cache/provisioning driver) is
# hashed into the build identity: change it when build semantics must invalidate
# existing server/Desktop artifacts. Sourced by build-t3code.sh.
t3_recipe_prepare_source() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y --no-install-recommends build-essential ca-certificates curl git python3
  if ! command -v pnpm >/dev/null 2>&1 || [[ "$(pnpm --version 2>/dev/null | cut -d. -f1)" != 11 ]]; then
    npm install -g pnpm@11.10.0
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
