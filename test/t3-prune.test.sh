#!/usr/bin/env bash
# Plain-Bash regression tests for prune_t3_installs in bin/install-ai-tools.sh.
# Run: bash test/t3-prune.test.sh
#
# The failure this guards against: every patched T3 build stayed on disk. The prebuilt
# installer never removes one and a local build only prunes its own cache, so a VM had
# 17 prebuilt runtimes (5.6 GB), a 3.3 GB local build from before it switched to
# prebuilt, and the 600 MB npm package of the stock T3 it ran before that.
#
# Everything here runs against scratch caches, a stubbed npm and one throwaway
# process -- no real T3 install is touched.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
tmp="$(mktemp -d)"
sleeper=""
trap '[[ -n "${sleeper}" ]] && kill "${sleeper}" 2>/dev/null; rm -rf "${tmp}"' EXIT

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

prebuilt="${tmp}/t3code-prebuilt"
source_root="${tmp}/t3code-source"
prefix="${tmp}/usr/local"
launcher="${prefix}/bin/t3"

# A stub npm reporting the scratch global prefix, like apt's npm (prefix /usr/local,
# so its t3 shim and the launcher share one path).
mkdir -p "${tmp}/stub"
cat >"${tmp}/stub/npm" <<EOF
#!/usr/bin/env bash
case "\$*" in
  "prefix -g") echo "${prefix}" ;;
  "root -g") echo "${prefix}/lib/node_modules" ;;
  *) echo "unexpected npm \$*" >>"${tmp}/npm.log"; exit 1 ;;
esac
EOF
chmod +x "${tmp}/stub/npm"

# Fresh caches: prebuilt builds a, b, c; local build d; the stock npm package.
reset() {
  rm -rf "${prebuilt}" "${source_root}" "${prefix}" "${tmp}/outside"
  for b in a b c; do mkdir -p "${prebuilt}/${b}/bin"; done
  mkdir -p "${prebuilt}/.download-123" "${source_root}/d/apps/server/dist" "${tmp}/outside"
  ln -s "${tmp}/outside" "${prebuilt}/linked"
  mkdir -p "${prefix}/bin" "${prefix}/lib/node_modules/t3/dist"
  : >"${prefix}/lib/node_modules/t3/dist/bin.mjs"
  printf '#!/bin/sh\n' >"${prebuilt}/b/bin/t3"
  : >"${source_root}/d/apps/server/dist/bin.mjs"
}

run_case() {
  local name="$1"
  shift
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" \
    env T3CODE_LAUNCHER="${launcher}" T3CODE_PREBUILT_CACHE="${prebuilt}" T3CODE_CACHE_ROOT="${source_root}" \
    PATH="${tmp}/stub:/usr/bin:/bin" "$@" \
    bash -c 'source "${SCRIPT_PATH}"; prune_t3_installs' >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

# ── prebuilt installed: keep it and the running build, drop the rest ──────────
reset
ln -sfn "${prebuilt}/b/bin/t3" "${launcher}"
cp "$(command -v sleep)" "${prebuilt}/c/bin/node"
"${prebuilt}/c/bin/node" 60 &
sleeper=$!
for _ in $(seq 50); do
  [[ "$(readlink "/proc/${sleeper}/exe" 2>/dev/null)" == */c/bin/node ]] && break
  sleep 0.1
done
run_case prebuilt T3CODE_LIMIT_RESUME=true
kill "${sleeper}" 2>/dev/null
wait "${sleeper}" 2>/dev/null
sleeper=""
ok "pruning succeeds" test "$(cat "${tmp}/prebuilt.rc")" = 0
ok "the installed prebuilt build is kept" test -d "${prebuilt}/b"
ok "a build the old server still runs from is kept" test -d "${prebuilt}/c"
ok "an unused prebuilt build is removed" test ! -e "${prebuilt}/a"
ok "an unused local build is removed" test ! -e "${source_root}/d"
ok "the installer's scratch dirs are left alone" test -d "${prebuilt}/.download-123"
ok "a symlinked cache entry is not followed" test -d "${tmp}/outside" -a -L "${prebuilt}/linked"
ok "the stock npm package is removed" test ! -e "${prefix}/lib/node_modules/t3"
ok "the launcher at npm's bin path is kept" test "$(readlink "${launcher}")" = "${prebuilt}/b/bin/t3"
ok "npm is never asked to uninstall" test ! -e "${tmp}/npm.log"

# ── a stock shim at npm's own bin path goes with its package ───────────────────
reset
launcher="${tmp}/launcher/t3"
mkdir -p "${tmp}/launcher"
ln -sfn "${prebuilt}/b/bin/t3" "${launcher}"
ln -sfn ../lib/node_modules/t3/dist/bin.mjs "${prefix}/bin/t3"
run_case shim T3CODE_LIMIT_RESUME=true
ok "the stock package's own shim is removed" test ! -e "${prefix}/bin/t3" -a ! -L "${prefix}/bin/t3"
ok "the stock package is removed with it" test ! -e "${prefix}/lib/node_modules/t3"
launcher="${prefix}/bin/t3"

# ── local build installed: it stays, every prebuilt build goes ───────────────
reset
ln -sfn "${source_root}/d/apps/server/dist/bin.mjs" "${launcher}"
run_case local T3CODE_LIMIT_RESUME=true
ok "the installed local build is kept" test -d "${source_root}/d"
ok "prebuilt builds are removed after a switch to local" test ! -e "${prebuilt}/a" -a ! -e "${prebuilt}/b" -a ! -e "${prebuilt}/c"

# ── stock mode: the npm package is the install ───────────────────────────────
reset
ln -sfn "${prefix}/lib/node_modules/t3/dist/bin.mjs" "${launcher}"
run_case stock T3CODE_LIMIT_RESUME=false
ok "stock mode keeps its npm package" test -d "${prefix}/lib/node_modules/t3"
ok "stock mode still drops patched builds" test ! -e "${prebuilt}/b" -a ! -e "${source_root}/d"

reset
ln -sfn "${prebuilt}/b/bin/t3" "${launcher}"
run_case stock_flag T3CODE_LIMIT_RESUME=false
ok "the npm package is only removed for a patched install" test -d "${prefix}/lib/node_modules/t3"

# ── no installed launcher: nothing to measure against ────────────────────────
reset
rm -f "${launcher}"
run_case nolauncher T3CODE_LIMIT_RESUME=true
ok "without a launcher nothing is removed" \
  test -d "${prebuilt}/a" -a -d "${source_root}/d" -a -d "${prefix}/lib/node_modules/t3"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
