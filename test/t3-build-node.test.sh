#!/usr/bin/env bash
# Real ABI regression: downloads checksum-pinned Node 22/26 into a disposable
# fixture and compiles tiny native addons. Requires Linux x64, curl and g++.
# All provisioning/build/packaging/service boundaries remain fixture-only.
set -Eeuo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
[[ "$(uname -m)" == x86_64 ]] || { echo 'This native fixture requires Linux x64' >&2; exit 1; }
TMP="$(mktemp -d)"
trap 'rm -rf -- "$TMP"' EXIT
export HOME="$TMP/home" T3CODE_NODE_ROOT="$TMP/toolchains" COMPILER_CACHE="$TMP/compiler"
mkdir -p "$HOME" "$TMP/profile"
curl -fsSL https://nodejs.org/dist/v22.22.0/node-v22.22.0-linux-x64.tar.gz -o "$TMP/profile/node.tar.gz"
printf '%s  %s\n' c33c39ed9c80deddde77c960d00119918b9e352426fd604ba41638d6526a4744 "$TMP/profile/node.tar.gz" | sha256sum -c -
tar -xzf "$TMP/profile/node.tar.gz" -C "$TMP/profile" --strip-components=1
export PATH="$TMP/profile/bin:$PATH"
export npm_config_cache="$HOME/.npm"
# The recipe must not override a user's configured global prefix, or write to it.
export npm_config_prefix="$TMP/user-global"
mkdir -p "$npm_config_prefix/bin"
printf 'user pnpm\n' > "$npm_config_prefix/bin/pnpm"
source "$REPO/bin/t3code-build-recipe.sh"
note() { echo "$*"; }
fail() { echo "$*" >&2; exit 1; }
# Exercise the real cold download/checksum/extract path in a child environment.
(t3_recipe_use_node)
RUNTIME="$T3CODE_NODE_ROOT/node-v26.8.1-linux-x64"
[[ "$(node --version)" == v22.22.0 ]]

mkdir -p "$TMP/package"
cat > "$TMP/addon.cc" <<'CPP'
#include <node.h>
void Init(v8::Local<v8::Object> exports) {
  auto isolate = v8::Isolate::GetCurrent();
  exports->Set(isolate->GetCurrentContext(),
    v8::String::NewFromUtf8Literal(isolate, "abi"),
    v8::Integer::New(isolate, NODE_MODULE_VERSION)).Check();
}
NODE_MODULE(NODE_GYP_MODULE_NAME, Init)
CPP
g++ -shared -fPIC -std=c++20 -I"$TMP/profile/include/node" "$TMP/addon.cc" -o "$TMP/package/probe.node"
cat > "$TMP/package/package.json" <<'JSON'
{"name":"construct-native-probe","version":"1.0.0","bin":{"construct-native-probe":"probe.cjs"}}
JSON
cat > "$TMP/package/probe.cjs" <<'JS'
#!/usr/bin/env node
console.log(require('./probe.node').abi);
JS
chmod +x "$TMP/package/probe.cjs"
(cd "$TMP/package"; npm pack --silent --pack-destination "$TMP")
export PROBE_PACKAGE="$TMP/construct-native-probe-1.0.0.tgz"
probe() { npx --offline --yes --package "$PROBE_PACKAGE" construct-native-probe; }
[[ "$(probe)" == 127 ]]
# Prove this fixture detects the reported class of ABI failure.
if "$RUNTIME/bin/node" -e 'require(process.argv[1])' "$TMP/package/probe.node" >"$TMP/mismatch.log" 2>&1; then
  echo 'Expected the cross-major native load to fail' >&2; exit 1
fi
grep -q NODE_MODULE_VERSION "$TMP/mismatch.log"
find "$HOME/.npm/_npx" -type f -exec sha256sum {} + | sort > "$TMP/cache-before"

# Run the actual driver and artifact recipe with cheap compile/package inputs.
# Node, npm/npx and native loading are real; apt, git, pnpm and Wine are fixtures.
mkdir -p "$TMP/repo/bin" "$TMP/repo/extension/vm" "$TMP/tools" "$TMP/repo/patches/t3code-release/overlays"
cp "$REPO/bin/build-t3code.sh" "$REPO/bin/t3code-build-recipe.sh" "$TMP/repo/bin/"
printf '// fixture\n' > "$TMP/repo/bin/apply-t3code-source.mjs"
for patcher in construct-t3park-patch construct-t3-opencode-monitor-patch; do
  printf '// fixture\n' > "$TMP/repo/extension/vm/$patcher.mjs"
done
printf '{}\n' > "$TMP/repo/patches/t3code-release/source-transforms.json"
printf '// fixture\n' > "$TMP/repo/patches/t3code-release/overlays/source.ts"
cp -r "$TMP/repo/patches/t3code-release" "$TMP/repo/patches/t3code-nightly"
# Keep real prepare/compile, replacing only the heavy Windows packaging boundary.
cat >> "$TMP/repo/bin/t3code-build-recipe.sh" <<'SH'
t3_recipe_package() {
  [[ "$(node --version)" == v26.8.1 ]]
  [[ "$npm_config_cache" == "$COMPILER_CACHE/npm" ]]
  mkdir -p "$2"
  echo installer > "$2/setup.exe"
}
SH
cat > "$TMP/tools/git" <<'SH'
#!/bin/bash
if [[ "$1" == clone ]]; then mkdir -p "${@: -1}/.git"; else echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; fi
SH
cat > "$TMP/tools/apt-get" <<'SH'
#!/bin/bash
[[ " $* " != *' nodejs '* ]] || exit 99
SH
for tool in curl systemctl; do
  printf '#!/bin/bash\necho "Unexpected %s invocation" >&2; exit 99\n' "$tool" > "$TMP/tools/$tool"
done
cat > "$TMP/tools/df" <<'SH'
#!/bin/bash
printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\nfixture 99000000 0 99000000 0%% /\n'
SH
for tool in dpkg-query rustup; do printf '#!/bin/bash\nexit 1\n' > "$TMP/tools/$tool"; done
cat > "$TMP/pnpm" <<'SH'
#!/bin/bash
set -eu
case "$*" in
  --version) echo 11.10.0 ;;
  install*) ;;
  'run build:desktop')
    mkdir -p apps/server/dist
    g++ -shared -fPIC -std=c++20 -I"$TEST_RUNTIME/include/node" "$TEST_ROOT/addon.cc" -o apps/server/dist/probe.node
    cat > apps/server/dist/bin.mjs <<'JS'
#!/usr/bin/env node
import {createRequire} from 'node:module';
import {execFileSync} from 'node:child_process';
const require = createRequire(import.meta.url);
if (require('./probe.node').abi !== +process.versions.modules) throw Error('T3 ABI mismatch');
const child = execFileSync('npx', ['--offline','--yes','--package',process.env.PROBE_PACKAGE,'construct-native-probe'], {encoding:'utf8'}).trim();
if (child !== '127') throw Error('Child agent lost the profile Node ABI');
console.log(process.version + ' child=' + child);
JS
    ;;
  'store path') exit 1 ;;
  *) echo 'Unexpected pnpm command' >&2; exit 99 ;;
esac
SH
# A private npm stub verifies the global pnpm installation stays in its prefix.
rm "$RUNTIME/bin/npm"
cat > "$RUNTIME/bin/npm" <<'SH'
#!/bin/bash
set -eu
[[ "$*" == "install --prefix $TEST_RUNTIME -g pnpm@11.10.0" ]]
[[ "$npm_config_cache" == "$T3CODE_COMPILER_CACHE/npm" ]]
cp "$TEST_ROOT/pnpm" "$TEST_RUNTIME/bin/pnpm"
chmod +x "$TEST_RUNTIME/bin/pnpm"
SH
chmod +x "$TMP/tools/"* "$RUNTIME/bin/npm"
# The real recipe invokes this Node script before building the source bundle.
cat > "$TMP/tools/pnpm" <<'SH'
#!/bin/bash
echo 'Unexpected use of project pnpm' >&2; exit 99
SH
chmod +x "$TMP/tools/pnpm"
# git clone fixture creates the minimal source scripts required by the recipe.
cat >> "$TMP/tools/git" <<'SH'
if [[ "$1" == clone ]]; then
  mkdir -p "${@: -1}/scripts"
  echo '// fixture' > "${@: -1}/scripts/update-release-package-versions.ts"
fi
SH
export TEST_ROOT="$TMP" TEST_RUNTIME="$RUNTIME"
export PATH="$TMP/tools:$PATH" REPO_DIR="$TMP/repo"
export T3CODE_SOURCE_VERSION=1.0.0 T3CODE_CHANNEL=stable T3CODE_CACHE_ROOT="$TMP/sources"
export T3CODE_COMPILER_CACHE="$COMPILER_CACHE" T3CODE_ARTIFACT_ROOT="$TMP/artifacts"
export T3CODE_STATUS_PATH="$TMP/status" T3CODE_LAUNCHER="$TMP/t3" T3CODE_BUILD_MIN_FREE_GIB=1
unset _FUNCS_ONLY
run() {
  T3CODE_BUILD_MODE="$1" bash "$TMP/repo/bin/build-t3code.sh"
  [[ "$(node --version)" == v22.22.0 && "$(probe)" == 127 ]]
  [[ "$("$TMP/t3")" == 'v26.8.1 child=127' ]]
  [[ "$(cat "$npm_config_prefix/bin/pnpm")" == 'user pnpm' ]]
}
run server
run server # reprovision with the existing native npx tree, no downloads allowed
run desktop # late packaging after profile Node and npx are already selected
T3CODE_CHANNEL=nightly run server
run desktop
T3CODE_CHANNEL=stable T3CODE_INVENTORY=nightly run server # stable version/nightly inventory fallback
run desktop
find "$HOME/.npm/_npx" -type f -exec sha256sum {} + | sort > "$TMP/cache-after"
cmp "$TMP/cache-before" "$TMP/cache-after"
# A corrupt existing private runtime fails closed without touching live entries.
rm "$RUNTIME/bin/npm"
if T3CODE_BUILD_MODE=desktop bash "$TMP/repo/bin/build-t3code.sh" >"$TMP/incomplete.log" 2>&1; then
  echo 'Expected incomplete runtime failure' >&2; exit 1
fi
grep -q 'Incomplete private T3 Node runtime' "$TMP/incomplete.log"
[[ "$(probe)" == 127 ]]
echo 'PASS: real native npx cache survives local/nightly/fallback builds, reprovision and late packaging; server and child agents retain their own Node ABI'
