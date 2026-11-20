#!/usr/bin/env bash
# Tests for the T3 patch shims in extension/vm/: they parse, and they forward to the installed
# build's own patch script with every argument and its exit code. Run: bash test/t3-patch-shims.test.sh
#
# The installed build is a fake prebuilt runtime in a scratch directory (bin/t3code-build-source.py
# --installed resolves it from the server manifest); nothing real is patched.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

build="$(printf 'a%.0s' {1..64})"
runtime="${tmp}/prebuilt/${build}/extension/vm"
mkdir -p "${runtime}" "${tmp}/artifacts"
printf '{"installationMode":"prebuilt","buildHash":"%s"}\n' "${build}" >"${tmp}/artifacts/server-manifest.json"
for patch in construct-t3park-patch.mjs construct-t3-opencode-monitor-patch.mjs; do
  printf 'console.log("%s " + process.argv.slice(2).join(" "));\nprocess.exit(7);\n' "${patch}" >"${runtime}/${patch}"
done

for patch in construct-t3park-patch.mjs construct-t3-opencode-monitor-patch.mjs; do
  shim="${ROOT}/extension/vm/${patch}"
  ok "${patch}: parses" node --check "${shim}"
  rc=0
  out="$(T3CODE_ARTIFACT_ROOT="${tmp}/artifacts" T3CODE_PREBUILT_CACHE="${tmp}/prebuilt" \
    node "${shim}" apply --bundle '/tmp/a b.mjs' 2>&1)" || rc=$?
  ok "${patch}: runs the installed build's script with every argument" test "${out}" = "${patch} apply --bundle /tmp/a b.mjs"
  ok "${patch}: and exits with its code" test "${rc}" = 7
done

echo ""
echo "  t3-patch-shims tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
