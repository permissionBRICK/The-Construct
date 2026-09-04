#!/usr/bin/env bash
# Verify Construct's T3 Code source inventory against a REAL T3 source tree: apply the
# overlays + guarded transforms to a PRISTINE checkout, typecheck the transformed apps,
# and run the overlay's own Vitest files. This is the only place those checks can run —
# the overlays import from `@t3tools/contracts` and are executed by T3's own toolchain.
#
# A VM that has built the patched T3 keeps that tree in
# /var/cache/construct/t3code-source/<version-key>/ (a git checkout of the upstream tag,
# with node_modules installed). This script makes a pristine git worktree of it, links the
# installed node_modules in, and works there — the cache itself is never modified.
# WITHOUT such a tree there is nothing to run against and the script SKIPS (exit 0): a
# Linux CI box that never built T3 must not go red for a suite it cannot host.
#
#   T3_SOURCE_DIR=/path/to/t3-checkout  bash test/t3-overlay.test.sh
#
# Only the inventory whose tag the source actually IS can be applied; the other channel's
# anchors are written against a different upstream tag and are reported as not checked.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "${here}")"
pass=0; fail=0
ok() { if [[ "$2" == "0" ]]; then pass=$((pass+1)); echo "  PASS  $1"; else fail=$((fail+1)); echo "  FAIL  $1"; fi; }
finish() {
  echo ""
  echo "  t3-overlay tests - ${pass}/$((pass+fail)) passed"
  echo ""
  [[ "${fail}" -eq 0 ]] || exit 1
  exit 0
}

echo ""
echo "=== T3 overlay + transform inventory ==="

# ── The two channel inventories must carry the SAME overlay files ─────────────
# (The transforms differ per tag; the Construct-owned files do not.)
diff -r "${repo}/patches/t3code-release/overlays" "${repo}/patches/t3code-nightly/overlays" >"/tmp/t3-overlay-diff.$$" 2>&1
ok "the release and nightly overlays are identical" "$?"
[[ -s "/tmp/t3-overlay-diff.$$" ]] && head -20 "/tmp/t3-overlay-diff.$$"
rm -f "/tmp/t3-overlay-diff.$$"

# ── Every overlay file is listed in BOTH inventories ──────────────────────────
for channel in release nightly; do
  missing="$(cd "${repo}/patches/t3code-${channel}/overlays" && find . -type f | sed 's|^\./||' | while read -r rel; do
    grep -q "\"${rel}\"" "${repo}/patches/t3code-${channel}/source-transforms.json" || echo "${rel}"
  done)"
  ok "${channel}: every overlay file is listed in the inventory" "$([[ -z "${missing}" ]] && echo 0 || echo 1)"
  [[ -n "${missing}" ]] && echo "${missing}"
done

src="${T3_SOURCE_DIR:-}"
if [[ -z "${src}" ]]; then
  src="$(find /var/cache/construct/t3code-source -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort | tail -1)"
fi
if [[ -z "${src}" || ! -x "${src}/node_modules/.bin/vitest" || ! -d "${src}/.git" ]]; then
  echo "  SKIP  no T3 git checkout with node_modules (set T3_SOURCE_DIR to one built by bin/build-t3code.sh)"
  finish
fi
echo "  using T3 source: ${src}"

# Which inventory is written against THIS source? The package version of the checkout.
version="$(node -e 'try{process.stdout.write(require(process.argv[1]).version||"")}catch{}' "${src}/apps/server/package.json" 2>/dev/null)"
channel=release
[[ "${version}" == *"nightly"* ]] && channel=nightly
echo "  T3 ${version:-unknown} -> the ${channel} inventory (the other channel targets a different tag and is not checked here)"

work="$(mktemp -d /tmp/t3-overlay-XXXXXX)"
cleanup() {
  git -C "${src}" worktree remove --force "${work}" >/dev/null 2>&1
  rm -rf "${work}"
  git -C "${src}" worktree prune >/dev/null 2>&1
}
trap cleanup EXIT

rm -rf "${work}"
git -C "${src}" worktree add --detach "${work}" HEAD >"/tmp/t3-overlay-wt.$$" 2>&1
if [[ "$?" -ne 0 ]]; then
  ok "pristine worktree of the T3 source" 1
  tail -5 "/tmp/t3-overlay-wt.$$"; rm -f "/tmp/t3-overlay-wt.$$"
  finish
fi
rm -f "/tmp/t3-overlay-wt.$$"
ok "pristine worktree of the T3 source" 0

# The installed dependencies are linked in rather than re-installed (a pnpm install here
# would need the network and several minutes).
( cd "${src}" && find . -maxdepth 3 -name node_modules -type d -not -path "*/node_modules/*" | sed 's|^\./||' ) |
  while read -r d; do
    [[ -e "${work}/${d}" ]] || { mkdir -p "$(dirname "${work}/${d}")"; ln -sfn "${src}/${d}" "${work}/${d}"; }
  done

node "${repo}/bin/apply-t3code-source.mjs" apply \
  --source "${work}" \
  --manifest "${repo}/patches/t3code-${channel}/source-transforms.json" \
  --overlays "${repo}/patches/t3code-${channel}/overlays" >"/tmp/t3-overlay-apply.$$" 2>&1
ok "${channel}: overlays + guarded transforms apply to a pristine checkout" "$?"
grep -E "matched with|refused|ERROR" "/tmp/t3-overlay-apply.$$" | head -10
rm -f "/tmp/t3-overlay-apply.$$"

run_in() {
  local label="$1" app="$2"; shift 2
  ( cd "${work}/apps/${app}" && "$@" >"/tmp/t3-overlay-${app}.$$" 2>&1 )
  local rc=$?
  # tsgo prints "suggestion TS…" advisories that are not failures; a real error also
  # makes it exit non-zero, so the exit code is what decides.
  ok "apps/${app}: ${label}" "${rc}"
  if [[ "${rc}" -ne 0 ]]; then grep -v "suggestion TS" "/tmp/t3-overlay-${app}.$$" | head -25
  else grep -E "Tests +[0-9]+ passed" "/tmp/t3-overlay-${app}.$$" | tail -1; fi
  rm -f "/tmp/t3-overlay-${app}.$$"
}

# The TRANSFORMED runtime: contracts, the IPC method + handler + preload, the desktop
# service and the Providers UI all have to typecheck together, not just the pure files.
run_in "typecheck (transformed runtime)" desktop "${work}/node_modules/.bin/tsgo" --noEmit
run_in "typecheck (transformed runtime)" web "${work}/node_modules/.bin/tsgo" --noEmit
run_in "overlay vitest" desktop "${work}/node_modules/.bin/vitest" run src/updates/ConstructUpdates.test.ts
run_in "overlay vitest" web "${work}/node_modules/.bin/vitest" run \
  src/components/constructInstances.logic.test.ts src/components/constructUpdate.logic.test.ts

finish
