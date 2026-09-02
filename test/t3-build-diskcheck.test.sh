#!/usr/bin/env bash
# Unit tests for the superseded-build pruning + free-space requirement helpers in
# bin/build-t3code.sh (sourced with _FUNCS_ONLY=true so nothing resolves or installs).
# Run: bash test/t3-build-diskcheck.test.sh
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(dirname "${HERE}")"
pass=0; fail=0
ok() { if [[ "$2" == "true" || "$2" == "0" ]]; then pass=$((pass+1)); else fail=$((fail+1)); printf 'FAIL: %s\n' "$1" >&2; fi; }

TMP="$(mktemp -d)"; trap 'rm -rf -- "${TMP}"' EXIT

# Source the helpers only.
set +e
( _FUNCS_ONLY=true REPO_DIR="${TMP}/norepo" source "${REPO}/bin/build-t3code.sh"; echo "sourced-rc=$?" ) >"${TMP}/source.out" 2>&1
ok "sourcing with _FUNCS_ONLY does not run the build (no patch-missing error)" "$([[ ! "$(cat "${TMP}/source.out")" =~ ERROR ]] && echo true || echo false)"
_FUNCS_ONLY=true REPO_DIR="${TMP}/norepo" source "${REPO}/bin/build-t3code.sh"
ok "helpers are defined" "$(declare -F t3_build_prune_candidates t3_build_required_kb t3_build_toolchain_flags >/dev/null && echo true || echo false)"

# ── t3_build_required_kb ─────────────────────────────────────────────────────
gib() { echo $(( $1 * 1048576 )); }
unset T3CODE_BUILD_MIN_FREE_GIB
ok "cold toolchain needs 15 GiB (the historical first-build figure)" "$([[ "$(t3_build_required_kb 0 0 0 0)" == "$(gib 15)" ]] && echo true || echo false)"
ok "fully warm toolchain needs 6 GiB" "$([[ "$(t3_build_required_kb 1 1 1 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "missing wine alone adds 4 GiB" "$([[ "$(t3_build_required_kb 0 1 1 1)" == "$(gib 10)" ]] && echo true || echo false)"
ok "missing pnpm store alone adds 3 GiB" "$([[ "$(t3_build_required_kb 1 0 1 1)" == "$(gib 9)" ]] && echo true || echo false)"
ok "missing electron cache alone adds 1 GiB" "$([[ "$(t3_build_required_kb 1 1 0 1)" == "$(gib 7)" ]] && echo true || echo false)"
ok "missing rust windows target alone adds 1 GiB" "$([[ "$(t3_build_required_kb 1 1 1 0)" == "$(gib 7)" ]] && echo true || echo false)"
ok "T3CODE_BUILD_MIN_FREE_GIB overrides the heuristic" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=3 t3_build_required_kb 0 0 0 0)" == "$(gib 3)" ]] && echo true || echo false)"
ok "a non-integer override is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=lots t3_build_required_kb 1 1 1 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "an empty override is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB= t3_build_required_kb 1 1 1 1)" == "$(gib 6)" ]] && echo true || echo false)"

# ── t3_build_prune_candidates ────────────────────────────────────────────────
B="${TMP}/build"
mkdir -p "${B}/.git" "${B}/node_modules/.pnpm" "${B}/apps/server/dist/client" \
  "${B}/apps/web/dist" "${B}/apps/desktop/release" "${B}/apps/desktop/dist" "${B}/apps/mobile/dist" \
  "${B}/native/resource-monitor/target/x86_64-pc-windows-gnu/release" \
  "${B}/native/resource-monitor/target/x86_64-pc-windows-msvc" \
  "${B}/native/resource-monitor/target/release/deps" "${B}/native/resource-monitor/target/release/build" \
  "${B}/native/resource-monitor/target/release/incremental" "${B}/native/resource-monitor/target/release/.fingerprint" \
  "${B}/native/resource-monitor/target/debug/deps" \
  "${B}/native/libghostty-vt/target/x86_64-pc-windows-gnu"
touch "${B}/apps/server/dist/bin.mjs" "${B}/native/resource-monitor/target/release/resource-monitor" \
  "${B}/native/resource-monitor/target/debug/resource-monitor" "${B}/native/resource-monitor/target/CACHEDIR.TAG" \
  "${B}/pnpm-lock.yaml"
mapfile -t victims < <(t3_build_prune_candidates "${B}")
has() { local v; for v in "${victims[@]}"; do [[ "${v}" == "$1" ]] && return 0; done; return 1; }
ok "prunes node_modules" "$(has "${B}/node_modules" && echo true || echo false)"
ok "prunes the web bundle (server serves dist/client, web/dist is only the dev fallback)" "$(has "${B}/apps/web/dist" && echo true || echo false)"
ok "prunes the desktop release output" "$(has "${B}/apps/desktop/release" && echo true || echo false)"
ok "prunes the desktop dist" "$(has "${B}/apps/desktop/dist" && echo true || echo false)"
ok "prunes the mobile dist" "$(has "${B}/apps/mobile/dist" && echo true || echo false)"
ok "prunes the Windows gnu cross target" "$(has "${B}/native/resource-monitor/target/x86_64-pc-windows-gnu" && echo true || echo false)"
ok "prunes the Windows msvc cross target" "$(has "${B}/native/resource-monitor/target/x86_64-pc-windows-msvc" && echo true || echo false)"
ok "prunes other native crates' Windows targets too" "$(has "${B}/native/libghostty-vt/target/x86_64-pc-windows-gnu" && echo true || echo false)"
ok "prunes cargo intermediates: release/deps" "$(has "${B}/native/resource-monitor/target/release/deps" && echo true || echo false)"
ok "prunes cargo intermediates: release/build" "$(has "${B}/native/resource-monitor/target/release/build" && echo true || echo false)"
ok "prunes cargo intermediates: release/incremental" "$(has "${B}/native/resource-monitor/target/release/incremental" && echo true || echo false)"
ok "prunes cargo intermediates: release/.fingerprint" "$(has "${B}/native/resource-monitor/target/release/.fingerprint" && echo true || echo false)"
ok "prunes cargo intermediates: debug/deps" "$(has "${B}/native/resource-monitor/target/debug/deps" && echo true || echo false)"
ok "KEEPS apps/server/dist (the installed server)" "$(has "${B}/apps/server/dist" && echo false || echo true)"
ok "KEEPS apps/server/dist/client" "$(has "${B}/apps/server/dist/client" && echo false || echo true)"
ok "KEEPS the source checkout (.git)" "$(has "${B}/.git" && echo false || echo true)"
ok "KEEPS the lockfile / source files" "$(has "${B}/pnpm-lock.yaml" && echo false || echo true)"
ok "KEEPS the Linux release resource-monitor executable" "$(has "${B}/native/resource-monitor/target/release/resource-monitor" && echo false || echo true)"
ok "KEEPS the debug resource-monitor executable" "$(has "${B}/native/resource-monitor/target/debug/resource-monitor" && echo false || echo true)"
ok "does not list the release dir itself" "$(has "${B}/native/resource-monitor/target/release" && echo false || echo true)"
ok "does not list the target dir itself" "$(has "${B}/native/resource-monitor/target" && echo false || echo true)"
ok "every candidate exists on disk" "$(for v in "${victims[@]}"; do [[ -e "${v}" ]] || { echo false; exit; }; done; echo true)"
ok "no candidate escapes the build dir" "$(for v in "${victims[@]}"; do [[ "${v}" == "${B}/"* ]] || { echo false; exit; }; done; echo true)"

# Removing every candidate leaves the runtime set intact and reclaims the rest.
for v in "${victims[@]}"; do rm -rf -- "${v}"; done
ok "after pruning: server bundle still present" "$([[ -f "${B}/apps/server/dist/bin.mjs" ]] && echo true || echo false)"
ok "after pruning: resource-monitor executable still present" "$([[ -f "${B}/native/resource-monitor/target/release/resource-monitor" ]] && echo true || echo false)"
ok "after pruning: node_modules gone" "$([[ ! -e "${B}/node_modules" ]] && echo true || echo false)"
ok "after pruning: Windows target gone" "$([[ ! -e "${B}/native/resource-monitor/target/x86_64-pc-windows-gnu" ]] && echo true || echo false)"
ok "an empty/nonexistent dir yields no candidates" "$([[ -z "$(t3_build_prune_candidates "${TMP}/does-not-exist")" ]] && echo true || echo false)"
ok "a pristine checkout (nothing built) yields no candidates" "$(mkdir -p "${TMP}/pristine/.git" "${TMP}/pristine/apps/server/src"; [[ -z "$(t3_build_prune_candidates "${TMP}/pristine")" ]] && echo true || echo false)"

# ── t3_build_toolchain_flags (HOME + PATH fixtures) ──────────────────────────
FAKE="${TMP}/fake"; mkdir -p "${FAKE}/bin" "${FAKE}/home"
# dpkg-query stub: WINE64_STATE controls the answer; pnpm stub: PNPM_STORE controls `store path`.
cat >"${FAKE}/bin/dpkg-query" <<'EOF'
#!/usr/bin/env bash
if [[ "${WINE64_STATE:-}" == "installed" ]]; then printf 'install ok installed'; else exit 1; fi
EOF
cat >"${FAKE}/bin/pnpm" <<'EOF'
#!/usr/bin/env bash
[[ "$1 $2" == "store path" && -n "${PNPM_STORE:-}" ]] && { printf '%s\n' "${PNPM_STORE}"; exit 0; }
exit 1
EOF
chmod +x "${FAKE}/bin/"*
flags() { ( export PATH="${FAKE}/bin:${PATH}" HOME="${FAKE}/home"; t3_build_toolchain_flags ); }

ok "cold machine reports all zeros" "$([[ "$(flags)" == "0 0 0 0" ]] && echo true || echo false)"
ok "installed wine64 flips the first flag" "$([[ "$(WINE64_STATE=installed flags)" == "1 0 0 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/store/v11"; touch "${FAKE}/store/v11/index"
ok "pnpm store path (non-empty) flips the second flag" "$([[ "$(PNPM_STORE="${FAKE}/store/v11" flags)" == "0 1 0 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/emptystore"
ok "an EMPTY pnpm store does not count" "$([[ "$(PNPM_STORE="${FAKE}/emptystore" flags)" == "0 0 0 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/home/.local/share/pnpm/store/v11"; touch "${FAKE}/home/.local/share/pnpm/store/v11/x"
ok "falls back to ~/.local/share/pnpm/store when pnpm is unavailable" "$([[ "$(flags)" == "0 1 0 0" ]] && echo true || echo false)"
rm -rf "${FAKE}/home/.local"
mkdir -p "${FAKE}/home/.cache/electron"
ok "an EMPTY electron cache does not count" "$([[ "$(flags)" == "0 0 0 0" ]] && echo true || echo false)"
touch "${FAKE}/home/.cache/electron/electron-v40.zip"
ok "a populated electron cache flips the third flag" "$([[ "$(flags)" == "0 0 1 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/home/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/lib/rustlib/x86_64-unknown-linux-gnu"
ok "a rust toolchain WITHOUT the windows target does not count" "$([[ "$(flags)" == "0 0 1 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/home/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/lib/rustlib/x86_64-pc-windows-gnu"
ok "the installed windows target flips the fourth flag" "$([[ "$(flags)" == "0 0 1 1" ]] && echo true || echo false)"
ok "fully warm fixture reports all ones" "$([[ "$(WINE64_STATE=installed PNPM_STORE="${FAKE}/store/v11" flags)" == "1 1 1 1" ]] && echo true || echo false)"

# ── the check itself in the script body (static) ─────────────────────────────
S="${REPO}/bin/build-t3code.sh"
ok "script no longer hardcodes the 15 GiB kB constant" "$(grep -q '15728640' "${S}" && echo false || echo true)"
ok "script derives the requirement from the toolchain flags" "$(grep -q 't3_build_required_kb "\${tc_wine}"' "${S}" && echo true || echo false)"
ok "script prunes via the candidate list, not a bare node_modules glob" "$(grep -q 't3_build_prune_candidates "\${stale_dir}"' "${S}" && echo true || echo false)"
ok "script never prunes the build it is about to produce" "$(grep -q '"\${stale_dir}" == "\${SOURCE_DIR}" \]\] && continue' "${S}" && echo true || echo false)"
ok "failure message names the override knob" "$(grep -q 'T3CODE_BUILD_MIN_FREE_GIB=<gib>' "${S}" && echo true || echo false)"
ok "reclaimed amount is logged" "$(grep -q 'Reclaimed \$(( reclaimed_kb / 1024 )) MiB' "${S}" && echo true || echo false)"

printf '  t3-build-diskcheck tests — %d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
