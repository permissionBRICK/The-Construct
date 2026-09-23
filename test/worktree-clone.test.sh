#!/usr/bin/env bash
# Tests for bin/construct-worktree-clone.sh (the post-checkout hook that seeds new git
# worktrees with reflink copies) and provision.sh's setup_worktree_clone step.
#
# The hook is exercised on real XFS and ext4 file systems (loop images), so this needs root;
# without it the file-system cases are skipped and only the provisioning step runs.
#
# Run: bash test/worktree-clone.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
mounts=()
cleanup() {
  local m
  for m in "${mounts[@]}"; do umount "${m}" 2>/dev/null; done
  rm -rf "${tmp}"
}
trap cleanup EXIT

pass=0 fail=0 skip=0
ok() {
  local name="$1"; shift
  if "$@"; then pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"; fi
}

export GIT_CONFIG_GLOBAL="${tmp}/gitconfig" GIT_CONFIG_SYSTEM="${tmp}/gitconfig-system"
git config --global user.name test; git config --global user.email test@example.invalid
git config --global init.defaultBranch main
mkdir -p "${tmp}/bin"
install -m 0755 "${ROOT}/bin/construct-worktree-clone.sh" "${tmp}/bin/construct-worktree-clone.sh"
export PATH="${tmp}/bin:${PATH}"

# A repository whose main worktree carries the kinds of ignored content the hook meets.
make_repo() {
  local repo="$1"
  git init -q "${repo}"
  printf 'target/\nnode_modules/\n.venv/\nbuild/\nwt/\n.env\n' >"${repo}/.gitignore"
  echo 'fn main() {}' >"${repo}/main.rs"
  git -C "${repo}" add -A && git -C "${repo}" commit -qm init
  mkdir -p "${repo}/target/debug" "${repo}/node_modules/pkg" "${repo}/.venv" "${repo}/build"
  head -c 64M /dev/urandom >"${repo}/target/debug/big.rlib"
  echo 'module.exports = 1' >"${repo}/node_modules/pkg/index.js"
  echo 'home = /usr/bin' >"${repo}/.venv/pyvenv.cfg"
  echo 'CMAKE_HOME_DIRECTORY:INTERNAL=/x' >"${repo}/build/CMakeCache.txt"
  echo 'SECRET=1' >"${repo}/.env"
  install -D -m 0755 "${ROOT}/bin/git-template/hooks/post-checkout" "${repo}/.git/hooks/post-checkout"
  # A worktree nested in an ignored directory must not be copied into the next one.
  git -C "${repo}" worktree add -q "${repo}/wt/first" -b first 2>/dev/null
}
used_mb() { sync; df -BM --output=used "$1" | tail -1 | tr -dc 0-9; }

mount_fs() {  # mount_fs <xfs|ext4> <dir>
  local img="${tmp}/$1.img"
  truncate -s 1G "${img}"
  if [[ "$1" == xfs ]]; then mkfs.xfs -q -f -m reflink=1 "${img}" || return 1
  else mkfs.ext4 -q -F "${img}" || return 1; fi
  mkdir -p "$2" && mount -o loop "${img}" "$2" || return 1
  mounts+=("$2")
}

echo "=== hook on XFS ==="
if [[ "$(id -u)" -eq 0 ]] && command -v mkfs.xfs >/dev/null && mount_fs xfs "${tmp}/xfs"; then
  repo="${tmp}/xfs/repo"
  make_repo "${repo}"
  before="$(used_mb "${tmp}/xfs")"
  git -C "${repo}" worktree add -q "${tmp}/xfs/wt2" -b second 2>"${tmp}/add.err"
  wt="${tmp}/xfs/wt2"
  ok "git worktree add succeeds" test -f "${wt}/main.rs"
  ok "build output is copied" cmp -s "${repo}/target/debug/big.rlib" "${wt}/target/debug/big.rlib"
  ok "dependencies are copied" test -f "${wt}/node_modules/pkg/index.js"
  ok "ignored files are copied" test -f "${wt}/.env"
  ok "the copies share blocks (64 MB copied, < 8 MB used)" test "$(( $(used_mb "${tmp}/xfs") - before ))" -lt 8
  ok "a Python venv is not copied" test ! -e "${wt}/.venv"
  ok "a CMake build directory is not copied" test ! -e "${wt}/build"
  ok "a nested worktree is not copied" test ! -e "${wt}/wt"
  ok "the hook reports what it did" grep -q "construct: reflinked 3 ignored path(s)" "${tmp}/add.err"
  ok "the new worktree has no tracked changes" test -z "$(git -C "${wt}" status --porcelain)"

  # Existing content in the new worktree is never overwritten (checkout -b of a branch
  # that tracks a path the main worktree ignores is the realistic case; simulate it).
  git -C "${repo}" worktree add -q --no-checkout "${tmp}/xfs/wt3" -b third
  mkdir -p "${tmp}/xfs/wt3/node_modules/pkg" && echo mine >"${tmp}/xfs/wt3/node_modules/pkg/index.js"
  (cd "${tmp}/xfs/wt3" && construct-worktree-clone.sh 0000000000000000000000000000000000000000 "$(git rev-parse HEAD)" 1 2>/dev/null)
  ok "existing paths are left alone" test "$(cat "${tmp}/xfs/wt3/node_modules/pkg/index.js")" = mine

  git -C "${repo}" config construct.worktreeClone false
  git -C "${repo}" worktree add -q "${tmp}/xfs/wt4" -b fourth
  ok "construct.worktreeClone=false opts out" test ! -e "${tmp}/xfs/wt4/target"
  git -C "${repo}" config --unset construct.worktreeClone
  CONSTRUCT_WORKTREE_CLONE=0 git -C "${repo}" worktree add -q "${tmp}/xfs/wt5" -b fifth
  ok "CONSTRUCT_WORKTREE_CLONE=0 opts out" test ! -e "${tmp}/xfs/wt5/target"

  git -C "${repo}" checkout -q -b other 2>"${tmp}/switch.err" && git -C "${repo}" checkout -q main 2>>"${tmp}/switch.err"
  ok "a branch switch does not run the clone" test ! -s "${tmp}/switch.err"
  git clone -q "${repo}" "${tmp}/xfs/clone"
  ok "a clone is not seeded" test ! -e "${tmp}/xfs/clone/target"
else
  skip=$((skip + 1)); echo "  SKIP  needs root and mkfs.xfs"
fi

echo "=== hook on ext4 (no reflinks) ==="
if [[ "$(id -u)" -eq 0 ]] && mount_fs ext4 "${tmp}/ext4"; then
  repo="${tmp}/ext4/repo"
  make_repo "${repo}"
  git -C "${repo}" worktree add -q "${tmp}/ext4/wt2" -b second 2>"${tmp}/add4.err"
  ok "git worktree add still succeeds" test -f "${tmp}/ext4/wt2/main.rs"
  ok "nothing is copied (never a full copy)" test ! -e "${tmp}/ext4/wt2/target"
  ok "and nothing is reported" test ! -s "${tmp}/add4.err"
  ok "no probe file is left behind" test -z "$(find "${tmp}/ext4/wt2" -maxdepth 1 -name '.construct-reflink*')"
else
  skip=$((skip + 1)); echo "  SKIP  needs root"
fi

echo "=== provision.sh: setup_worktree_clone ==="
# Pull the step function out of provision.sh and run it against scratch directories.
eval "$(sed -n '/^setup_worktree_clone() {$/,/^}$/p' "${ROOT}/bin/provision.sh")"
note() { printf '%s\n' "$*"; }
okmsg=""
ok_orig="$(declare -f ok)"
# shellcheck disable=SC2034  # read by the eval'd setup_worktree_clone
REPO_DIR="${ROOT}" WORKSPACE_ROOT="${tmp}/ws"
export CONSTRUCT_BIN_DIR="${tmp}/installed-bin" CONSTRUCT_GIT_TEMPLATE_DIR="${tmp}/share/git-template"
mkdir -p "${CONSTRUCT_BIN_DIR}" "${WORKSPACE_ROOT}"
git init -q "${WORKSPACE_ROOT}/plain"
git init -q "${WORKSPACE_ROOT}/own-hook"
printf '#!/bin/sh\necho theirs\n' >"${WORKSPACE_ROOT}/own-hook/.git/hooks/post-checkout"
git init -q "${WORKSPACE_ROOT}/husky" && git -C "${WORKSPACE_ROOT}/husky" config core.hooksPath .husky
git init -q "${WORKSPACE_ROOT}/stale"
printf '#!/bin/sh\n# Installed by The Construct: old version\n' >"${WORKSPACE_ROOT}/stale/.git/hooks/post-checkout"
ok() { okmsg="$*"; }
setup_worktree_clone >"${tmp}/setup.out" 2>&1; rc=$?
eval "${ok_orig}"
ok "the step succeeds" test "${rc}" -eq 0
ok "the clone script is installed" test -x "${CONSTRUCT_BIN_DIR}/construct-worktree-clone.sh"
ok "the template carries the hook" cmp -s "${ROOT}/bin/git-template/hooks/post-checkout" "${CONSTRUCT_GIT_TEMPLATE_DIR}/hooks/post-checkout"
ok "the system init.templateDir points at it" test "$(git config --system init.templateDir)" = "${CONSTRUCT_GIT_TEMPLATE_DIR}"
ok "a plain checkout gets the hook" cmp -s "${ROOT}/bin/git-template/hooks/post-checkout" "${WORKSPACE_ROOT}/plain/.git/hooks/post-checkout"
ok "a repository's own hook is kept" grep -q theirs "${WORKSPACE_ROOT}/own-hook/.git/hooks/post-checkout"
ok "a core.hooksPath repository is left alone" test ! -e "${WORKSPACE_ROOT}/husky/.git/hooks/post-checkout"
ok "an older Construct hook is updated" cmp -s "${ROOT}/bin/git-template/hooks/post-checkout" "${WORKSPACE_ROOT}/stale/.git/hooks/post-checkout"
ok "it reports the count" test "${okmsg}" = "worktree hook in the git template and 2 existing checkout(s)"
git init -q "${tmp}/fresh"
ok "new repositories get the hook from the template" test -x "${tmp}/fresh/.git/hooks/post-checkout"
setup_worktree_clone >/dev/null 2>&1; rc=$?
ok "re-running is safe" test "${rc}" -eq 0
git config --system init.templateDir /elsewhere
setup_worktree_clone >"${tmp}/setup2.out" 2>&1
ok "an existing system init.templateDir is kept" test "$(git config --system init.templateDir)" = /elsewhere
ok "and that is reported" grep -q "keeping the existing system init.templateDir" "${tmp}/setup2.out"

echo ""
echo "${pass} passed, ${fail} failed$( ((skip)) && printf ', %d skipped' "${skip}")"
(( fail == 0 ))
