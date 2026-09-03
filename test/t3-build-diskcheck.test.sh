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

# ── sourcing with _FUNCS_ONLY: no side effects, no shell-option changes ──────
opts_before="$(set +o | sort)"
set +e
_FUNCS_ONLY=true REPO_DIR="${TMP}/norepo" source "${REPO}/bin/build-t3code.sh"
source_rc=$?
opts_after="$(set +o | sort)"
ok "sourcing returns 0" "${source_rc}"
ok "sourcing leaves the caller's shell options untouched (no set -Eeuo leak)" "$([[ "${opts_before}" == "${opts_after}" ]] && echo true || echo false)"
ok "errexit is still off in the test shell" "$([[ "$-" != *e* ]] && echo true || echo false)"
ok "helpers are defined" "$(declare -F t3_build_prune_candidates t3_build_required_kb t3_build_toolchain_flags t3_build_installed_dir >/dev/null && echo true || echo false)"
ok "no build variable leaked from the body (CACHE_ROOT unset)" "$([[ -z "${CACHE_ROOT:-}" ]] && echo true || echo false)"

# ── t3_build_required_kb ─────────────────────────────────────────────────────
gib() { echo $(( $1 * 1048576 )); }
unset T3CODE_BUILD_MIN_FREE_GIB
ok "cold toolchain needs 15 GiB (the historical first-build figure)" "$([[ "$(t3_build_required_kb 0 0 0 0)" == "$(gib 15)" ]] && echo true || echo false)"
ok "fully warm toolchain needs 6 GiB" "$([[ "$(t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "missing wine alone adds 4 GiB" "$([[ "$(t3_build_required_kb 0 3072 250 1)" == "$(gib 10)" ]] && echo true || echo false)"
ok "empty pnpm store alone adds 3 GiB" "$([[ "$(t3_build_required_kb 1 0 250 1)" == "$(gib 9)" ]] && echo true || echo false)"
ok "a one-byte pnpm store is NOT warm (still 3 GiB)" "$([[ "$(t3_build_required_kb 1 1 250 1)" == "$(gib 9)" ]] && echo true || echo false)"
ok "a 1.5 GiB pnpm store is credited 1 GiB (2 still needed)" "$([[ "$(t3_build_required_kb 1 1536 250 1)" == "$(gib 8)" ]] && echo true || echo false)"
ok "a 2.8 GiB pnpm store is credited 2 GiB (1 still needed)" "$([[ "$(t3_build_required_kb 1 2867 250 1)" == "$(gib 7)" ]] && echo true || echo false)"
ok "an oversized pnpm store is capped at the 3 GiB credit" "$([[ "$(t3_build_required_kb 1 20480 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "empty electron cache alone adds 1 GiB" "$([[ "$(t3_build_required_kb 1 3072 0 1)" == "$(gib 7)" ]] && echo true || echo false)"
ok "a 5 MiB electron cache is NOT warm" "$([[ "$(t3_build_required_kb 1 3072 5 1)" == "$(gib 7)" ]] && echo true || echo false)"
ok "a 100 MiB electron cache is warm" "$([[ "$(t3_build_required_kb 1 3072 100 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "missing rust windows target alone adds 1 GiB" "$([[ "$(t3_build_required_kb 1 3072 250 0)" == "$(gib 7)" ]] && echo true || echo false)"
ok "garbage size fields count as zero" "$([[ "$(t3_build_required_kb 1 abc "" 1)" == "$(gib 10)" ]] && echo true || echo false)"
ok "T3CODE_BUILD_MIN_FREE_GIB overrides the heuristic" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=3 t3_build_required_kb 0 0 0 0)" == "$(gib 3)" ]] && echo true || echo false)"
ok "zero-padded override is read as base 10 (08 -> 8 GiB, no octal error)" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=08 t3_build_required_kb 0 0 0 0 2>/dev/null)" == "$(gib 8)" ]] && echo true || echo false)"
ok "override of 0 is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=0 t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "an overlong override (overflow attempt) is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=99999999999999999999 t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "a negative override is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=-5 t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "a non-integer override is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=lots t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "an empty override is ignored" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB= t3_build_required_kb 1 3072 250 1)" == "$(gib 6)" ]] && echo true || echo false)"
ok "the override result is never negative (9999 GiB stays positive)" "$([[ "$(T3CODE_BUILD_MIN_FREE_GIB=9999 t3_build_required_kb 0 0 0 0)" -gt 0 ]] && echo true || echo false)"

# ── t3_build_prune_candidates (the INSTALLED build keeps its runtime set) ────
B="${TMP}/build with space"
mkdir -p "${B}/.git" "${B}/node_modules/.pnpm/node-pty@1/node_modules/node-pty" "${B}/apps/server/dist/client" \
  "${B}/apps/server/node_modules" \
  "${B}/apps/web/dist" "${B}/apps/desktop/release" "${B}/apps/desktop/dist" "${B}/apps/mobile/dist" \
  "${B}/native/resource-monitor/target/x86_64-pc-windows-gnu/release" \
  "${B}/native/resource-monitor/target/x86_64-pc-windows-msvc" \
  "${B}/native/resource-monitor/target/release/deps" "${B}/native/resource-monitor/target/release/build" \
  "${B}/native/resource-monitor/target/release/incremental" "${B}/native/resource-monitor/target/release/.fingerprint" \
  "${B}/native/resource-monitor/target/debug/deps" \
  "${B}/native/libghostty-vt/target/x86_64-pc-windows-gnu"
ln -s "../../../node_modules/.pnpm/node-pty@1/node_modules/node-pty" "${B}/apps/server/node_modules/node-pty"
touch "${B}/apps/server/dist/bin.mjs" "${B}/native/resource-monitor/target/release/resource-monitor" \
  "${B}/native/resource-monitor/target/debug/resource-monitor" "${B}/native/resource-monitor/target/CACHEDIR.TAG" \
  "${B}/pnpm-lock.yaml" "${B}/node_modules/.pnpm/node-pty@1/node_modules/node-pty/index.js"
mapfile -t victims < <(t3_build_prune_candidates "${B}")
has() { local v; for v in "${victims[@]}"; do [[ "${v}" == "$1" ]] && return 0; done; return 1; }
ok "KEEPS node_modules (node-pty is imported at run time through it)" "$(has "${B}/node_modules" && echo false || echo true)"
ok "KEEPS apps/server/node_modules" "$(has "${B}/apps/server/node_modules" && echo false || echo true)"
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
ok "no candidate escapes the build dir (path with a space)" "$(for v in "${victims[@]}"; do [[ "${v}" == "${B}/"* ]] || { echo false; exit; }; done; echo true)"
ok "candidate count is exactly the 12 regenerable fixture paths" "$([[ "${#victims[@]}" -eq 12 ]] && echo true || echo false)"

# Removing every candidate leaves the runtime set intact, including the node-pty link.
for v in "${victims[@]}"; do rm -rf -- "${v}"; done
ok "after pruning: server bundle still present" "$([[ -f "${B}/apps/server/dist/bin.mjs" ]] && echo true || echo false)"
ok "after pruning: node-pty still resolvable through apps/server/node_modules" "$([[ -f "${B}/apps/server/node_modules/node-pty/index.js" ]] && echo true || echo false)"
ok "after pruning: resource-monitor executable still present" "$([[ -f "${B}/native/resource-monitor/target/release/resource-monitor" ]] && echo true || echo false)"
ok "after pruning: Windows target gone" "$([[ ! -e "${B}/native/resource-monitor/target/x86_64-pc-windows-gnu" ]] && echo true || echo false)"
ok "after pruning: web dist gone" "$([[ ! -e "${B}/apps/web/dist" ]] && echo true || echo false)"
ok "an empty/nonexistent dir yields no candidates" "$([[ -z "$(t3_build_prune_candidates "${TMP}/does-not-exist")" ]] && echo true || echo false)"
ok "a pristine checkout (nothing built) yields no candidates" "$(mkdir -p "${TMP}/pristine/.git" "${TMP}/pristine/apps/server/src"; [[ -z "$(t3_build_prune_candidates "${TMP}/pristine")" ]] && echo true || echo false)"

# ── t3_build_installed_dir ───────────────────────────────────────────────────
I="${TMP}/cache/0.0.37-abc"; mkdir -p "${I}/apps/server/dist" "${TMP}/bin"; touch "${I}/apps/server/dist/bin.mjs"
ln -s "${I}/apps/server/dist/bin.mjs" "${TMP}/bin/t3"
ok "resolves the launcher symlink to its build dir" "$([[ "$(t3_build_installed_dir "${TMP}/bin/t3")" == "${I}" ]] && echo true || echo false)"
ok "a missing launcher yields empty" "$([[ -z "$(t3_build_installed_dir "${TMP}/bin/nope")" ]] && echo true || echo false)"
printf '#!/bin/sh\n' >"${TMP}/bin/t3-npm"; chmod +x "${TMP}/bin/t3-npm"
ok "a launcher that is not a source build (npm install) yields empty" "$([[ -z "$(t3_build_installed_dir "${TMP}/bin/t3-npm")" ]] && echo true || echo false)"
ok "a dangling launcher symlink yields empty" "$(ln -s "${TMP}/gone/apps/server/dist/bin.mjs" "${TMP}/bin/t3-dangling"; [[ -z "$(t3_build_installed_dir "${TMP}/bin/t3-dangling")" ]] && echo true || echo false)"

# ── t3_build_toolchain_flags (HOME + PATH fixtures) ──────────────────────────
FAKE="${TMP}/fake"; mkdir -p "${FAKE}/bin" "${FAKE}/home"
cat >"${FAKE}/bin/dpkg-query" <<'EOF'
#!/usr/bin/env bash
if [[ "${WINE64_STATE:-}" == "installed" ]]; then printf 'install ok installed'; else exit 1; fi
EOF
cat >"${FAKE}/bin/pnpm" <<'EOF'
#!/usr/bin/env bash
[[ "$1 $2" == "store path" && -n "${PNPM_STORE:-}" ]] && { printf '%s\n' "${PNPM_STORE}"; exit 0; }
exit 1
EOF
cat >"${FAKE}/bin/rustup" <<'EOF'
#!/usr/bin/env bash
[[ "$1 $2 $3" == "target list --installed" ]] || exit 1
printf '%s\n' ${RUSTUP_TARGETS:-}
EOF
chmod +x "${FAKE}/bin/"*
# PATH is pinned to the stubs + the system tools (du/awk/grep) so the REAL rustup/pnpm never answer.
flags() { ( export PATH="${FAKE}/bin:/usr/bin:/bin" HOME="${FAKE}/home"; t3_build_toolchain_flags ); }
mib_of() { du -sxm -- "$@" 2>/dev/null | awk '{s+=$1} END {print s+0}'; }

ok "cold machine reports all zeros" "$([[ "$(flags)" == "0 0 0 0" ]] && echo true || echo false)"
ok "installed wine64 flips the wine flag" "$([[ "$(WINE64_STATE=installed flags)" == "1 0 0 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/store/v11"; dd if=/dev/zero of="${FAKE}/store/v11/blob" bs=1M count=3 status=none
ok "pnpm store path is measured in MiB (du of the 3 MiB fixture)" "$([[ "$(PNPM_STORE="${FAKE}/store/v11" flags)" == "0 $(mib_of "${FAKE}/store/v11") 0 0" ]] && echo true || echo false)"
mkdir -p "${FAKE}/emptystore"
ok "an EMPTY pnpm store measures at most the directory block (du of an empty dir; far below the 1 GiB credit step)" "$([[ "$(PNPM_STORE="${FAKE}/emptystore" flags)" == "0 $(mib_of "${FAKE}/emptystore") 0 0" && "$(mib_of "${FAKE}/emptystore")" -le 1 ]] && echo true || echo false)"
mkdir -p "${FAKE}/home/.local/share/pnpm/store/v11"; dd if=/dev/zero of="${FAKE}/home/.local/share/pnpm/store/v11/blob" bs=1M count=2 status=none
ok "falls back to ~/.local/share/pnpm/store when pnpm is unavailable" "$([[ "$(flags)" == "0 $(mib_of "${FAKE}/home/.local/share/pnpm/store") 0 0" ]] && echo true || echo false)"
rm -rf "${FAKE}/home/.local"
mkdir -p "${FAKE}/home/.cache/electron" "${FAKE}/home/.cache/electron-builder"
ok "EMPTY electron caches measure at most their directory blocks (far below the 100 MiB warm bar)" "$([[ "$(flags)" == "0 0 $(( $(mib_of "${FAKE}/home/.cache/electron") + $(mib_of "${FAKE}/home/.cache/electron-builder") )) 0" && "$(( $(mib_of "${FAKE}/home/.cache/electron") + $(mib_of "${FAKE}/home/.cache/electron-builder") ))" -le 2 ]] && echo true || echo false)"
dd if=/dev/zero of="${FAKE}/home/.cache/electron/electron-v40.zip" bs=1M count=4 status=none
dd if=/dev/zero of="${FAKE}/home/.cache/electron-builder/nsis.7z" bs=1M count=2 status=none
el_mib="$(( $(mib_of "${FAKE}/home/.cache/electron") + $(mib_of "${FAKE}/home/.cache/electron-builder") ))"
ok "electron + electron-builder caches are summed (du of the 4+2 MiB fixtures)" "$([[ "$(flags)" == "0 0 ${el_mib} 0" ]] && echo true || echo false)"
ok "a rust toolchain WITHOUT the windows target does not count" "$([[ "$(RUSTUP_TARGETS='x86_64-unknown-linux-gnu' flags)" == "0 0 ${el_mib} 0" ]] && echo true || echo false)"
ok "the windows target in the ACTIVE toolchain flips the rust flag" "$([[ "$(RUSTUP_TARGETS='x86_64-unknown-linux-gnu x86_64-pc-windows-gnu' flags)" == "0 0 ${el_mib} 1" ]] && echo true || echo false)"
mkdir -p "${FAKE}/home/.rustup/toolchains/nightly-x86_64-unknown-linux-gnu/lib/rustlib/x86_64-pc-windows-gnu"
ok "a windows target dir in an INACTIVE toolchain does not count" "$([[ "$(RUSTUP_TARGETS='x86_64-unknown-linux-gnu' flags)" == "0 0 ${el_mib} 0" ]] && echo true || echo false)"
ok "no rustup at all does not count" "$([[ "$(rm "${FAKE}/bin/rustup"; flags)" == "0 0 ${el_mib} 0" ]] && echo true || echo false)"
ok "fully warm fixture reports wine + sizes + rust" "$(printf '#!/usr/bin/env bash\nprintf "x86_64-pc-windows-gnu\\n"\n' >"${FAKE}/bin/rustup"; chmod +x "${FAKE}/bin/rustup"; [[ "$(WINE64_STATE=installed PNPM_STORE="${FAKE}/store/v11" flags)" == "1 $(mib_of "${FAKE}/store/v11") ${el_mib} 1" ]] && echo true || echo false)"

# ── the check itself in the script body (static) ─────────────────────────────
S="${REPO}/bin/build-t3code.sh"
ok "script no longer hardcodes the 15 GiB kB constant" "$(grep -q '15728640' "${S}" && echo false || echo true)"
ok "helpers + _FUNCS_ONLY guard sit above set -Eeuo pipefail" "$([[ "$(grep -n '_FUNCS_ONLY' "${S}" | head -1 | cut -d: -f1)" -lt "$(grep -n '^set -Eeuo pipefail' "${S}" | cut -d: -f1)" ]] && echo true || echo false)"
ok "script derives the requirement from the toolchain probe" "$(grep -q 't3_build_required_kb "\${tc_wine}" "\${tc_store_mib}" "\${tc_electron_mib}" "\${tc_rust}"' "${S}" && echo true || echo false)"
ok "script resolves the installed build before pruning" "$(grep -q 'installed_dir="\$(t3_build_installed_dir /usr/local/bin/t3)"' "${S}" && echo true || echo false)"
ok "script prunes the installed build only via the candidate list" "$(grep -q 't3_build_prune_candidates "\${stale_dir}"' "${S}" && echo true || echo false)"
ok "script removes other superseded builds whole" "$(grep -q 'Removing superseded T3 build' "${S}" && echo true || echo false)"
ok "script never prunes the build it is about to produce" "$(grep -q '"\${stale_dir}" == "\${SOURCE_DIR}" \]\] && continue' "${S}" && echo true || echo false)"
ok "freed space is measured with df before/after, not du of victims" "$(grep -q 'free_before_kb=' "${S}" && grep -q 'free_after_kb=' "${S}" && ! grep -q 'reclaimed_kb' "${S}" && echo true || echo false)"
ok "failure message names the override knob" "$(grep -q 'T3CODE_BUILD_MIN_FREE_GIB=<gib>' "${S}" && echo true || echo false)"

# ── t3_build_apply_patch (exact / fuzz / genuine conflict) ───────────────────
mkrepo() { # $1 dir: a tiny git repo with one file of numbered lines
  rm -rf "$1"; mkdir -p "$1"; ( cd "$1" && git init -q && git config user.email t@t && git config user.name t
    seq 1 40 | sed 's/^/line /' > file.txt && git add . && git commit -qm base ); }
R="${TMP}/apply"; mkrepo "${R}"
( cd "${R}" && sed -i 's/^line 20$/line 20 PATCHED/' file.txt && git diff > "${TMP}/exact.patch" && git checkout -q -- . )
ok "exact patch applies via git apply" "$(cd "${R}" && TAG=vX t3_build_apply_patch "${TMP}/exact.patch" >/dev/null 2>&1 && grep -q 'line 20 PATCHED' file.txt && echo true || echo false)"
( cd "${R}" && git checkout -q -- . )
# context drift: upstream edited lines 17 and 23 (inside the 3-line context) but not line 20.
( cd "${R}" && sed -i 's/^line 17$/line 17 upstream/; s/^line 23$/line 23 upstream/' file.txt && git commit -qam drift )
ok "drifted context is rejected by plain git apply (precondition)" "$(cd "${R}" && git apply --check "${TMP}/exact.patch" >/dev/null 2>&1 && echo false || echo true)"
fuzz_out="$(cd "${R}" && TAG=vX t3_build_apply_patch "${TMP}/exact.patch" 2>&1)"; fuzz_rc=$?
ok "drifted context still applies via fuzz" "${fuzz_rc}"
ok "fuzz application changed the intended line" "$(grep -q 'line 20 PATCHED' "${R}/file.txt" && echo true || echo false)"
ok "fuzz application kept upstream's context edits" "$(grep -q 'line 17 upstream' "${R}/file.txt" && grep -q 'line 23 upstream' "${R}/file.txt" && echo true || echo false)"
ok "fuzz application is reported in the log" "$([[ "${fuzz_out}" == *"needs context fuzz"* && "${fuzz_out}" == *"fuzz"* ]] && echo true || echo false)"
# genuine conflict: upstream changed line 20 itself.
mkrepo "${R}"; ( cd "${R}" && sed -i 's/^line 20$/line 20 rewritten upstream/' file.txt && git commit -qam conflict )
conf_out="$(cd "${R}" && TAG=v9.9.9 t3_build_apply_patch "${TMP}/exact.patch" 2>&1)"; conf_rc=$?
ok "a genuine conflict fails" "$([[ "${conf_rc}" -ne 0 ]] && echo true || echo false)"
ok "the failure names the conflicting file and the tag" "$([[ "${conf_out}" == *"file.txt"* && "${conf_out}" == *"v9.9.9"* ]] && echo true || echo false)"
ok "the failure says to rebase the patch, not that the version changed" "$([[ "${conf_out}" == *"Rebase patches/t3code-construct.patch"* && "${conf_out}" != *"changed incompatibly"* ]] && echo true || echo false)"
ok "a failed application leaves the tree untouched" "$(cd "${R}" && git status --porcelain | grep -q . && echo false || echo true)"
ok "script body uses the lenient applier" "$(grep -q 'if ! t3_build_apply_patch "\${PATCH_FILE}"' "${S}" && ! grep -q 'changed incompatibly' "${S}" && echo true || echo false)"

# Ready nightly repairs can be used as a no-agent stable fallback. Only the
# watcher's validated branch namespace is accepted; unrelated/partial refs are
# ignored, and the newest deterministic ref wins.
refs="$(cat <<'EOF'
aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/heads/main
bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb refs/heads/fix/upstream-t3-nightly-0.0.39-nightly.20260902.1260-2026-09-02
cccccccccccccccccccccccccccccccccccccccc refs/heads/fix/upstream-t3-stable-0.0.39-2026-09-03
dddddddddddddddddddddddddddddddddddddddd refs/heads/fix/upstream-t3-nightly-0.0.39-nightly.20260903.1272-2026-09-03
eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee refs/pull/2/head
EOF
)"
ok "nightly fallback selects the newest validated repair ref" "$([[ "$(printf '%s\n' "${refs}" | t3_build_latest_nightly_fix_ref)" == "refs/heads/fix/upstream-t3-nightly-0.0.39-nightly.20260903.1272-2026-09-03" ]] && echo true || echo false)"
ok "nightly fallback ignores input without a repair branch" "$([[ -z "$(printf '%s\n' "${refs}" | grep -v upstream-t3-nightly | t3_build_latest_nightly_fix_ref)" ]] && echo true || echo false)"
ok "stable build has a ready-nightly fallback before giving up" "$(grep -q 'Stable \${TAG} accepts ready nightly repair' "${S}" && grep -q 't3_build_fetch_nightly_candidate' "${S}" && echo true || echo false)"
ok "stable preflights both published-bundle patchers before the expensive build" "$(grep -q 't3_build_bundle_patchers_compatible "\${VERSION}" "\${T3PARK_PATCHER}" "\${T3MONITOR_PATCHER}"' "${S}" && echo true || echo false)"
ok "nightly candidate carries source and both bundle patchers" "$(grep -q 'PATCH_FILE="\${candidate_dir}/patches/t3code-construct.patch"' "${S}" && grep -q 'T3PARK_PATCHER="\${candidate_dir}/extension/vm/construct-t3park-patch.mjs"' "${S}" && grep -q 'T3MONITOR_PATCHER="\${candidate_dir}/extension/vm/construct-t3-opencode-monitor-patch.mjs"' "${S}" && echo true || echo false)"

printf '  t3-build-diskcheck tests — %d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
