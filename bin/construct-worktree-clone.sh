#!/usr/bin/env bash
# Seed a new git worktree with reflink copies of the main worktree's ignored files --
# build outputs and dependencies such as target/, node_modules/, bin/ and obj/.
#
# Runs as git's post-checkout hook (installed by provision.sh through the git template,
# see docs/worktrees.md): `git worktree add` then leaves a worktree that is ready to build
# instead of one that needs a full dependency install and a cold build. On XFS (and btrfs)
# a reflink copy shares every block with the original until one side writes to it, so ten
# worktrees of a 30 GB Rust build cost almost no extra disk.
#
# It only ever acts on a freshly added worktree (post-checkout with a null previous HEAD in
# a worktree that is not the main one), copies only paths git ignores, never overwrites
# anything that already exists, and never falls back to a real copy: on a file system
# without reflinks it does nothing. It always exits 0 so it can never fail a checkout.
#
# Skipped on purpose: other worktrees nested in ignored directories, nested git
# repositories, Python virtual environments and CMake build directories (both record
# absolute paths, so a copy would keep writing into the source worktree).
#
# Opt out per repository with `git config construct.worktreeClone false`, or for one
# command with CONSTRUCT_WORKTREE_CLONE=0.

set -u

prev="${1:-}" flag="${3:-}"
[[ "${flag}" == "1" && "${prev}" =~ ^0+$ ]] || exit 0
[[ "${CONSTRUCT_WORKTREE_CLONE:-1}" != "0" ]] || exit 0
[[ "$(git config --type=bool construct.worktreeClone 2>/dev/null || echo true)" != "false" ]] || exit 0

dest="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
worktrees=()
while IFS= read -r line; do
  [[ "${line}" == "worktree "* ]] && worktrees+=("${line#worktree }")
done < <(git worktree list --porcelain 2>/dev/null)
(( ${#worktrees[@]} >= 2 )) || exit 0
src="${worktrees[0]}"
[[ -d "${src}" && "${src}" != "${dest}" && ! -f "${src}/.git" ]] || exit 0
# Reflinks only work within one file system.
[[ "$(stat -c %d "${src}")" == "$(stat -c %d "${dest}")" ]] || exit 0

probe="$(mktemp "${dest}/.construct-reflink.XXXXXX" 2>/dev/null)" || exit 0
cp --reflink=always "${probe}" "${probe}.copy" 2>/dev/null
supported=$?
rm -f "${probe}" "${probe}.copy"
(( supported == 0 )) || exit 0

# True when <path> is, or contains, one of the repository's worktrees.
holds_worktree() {
  local wt
  for wt in "${worktrees[@]}"; do
    [[ "${wt}" == "$1" || "${wt}" == "$1/"* ]] && return 0
  done
  return 1
}

start="${SECONDS}" copied=0 skipped=0
while IFS= read -r -d '' rel; do
  rel="${rel%/}"
  from="${src}/${rel}" to="${dest}/${rel}"
  [[ -e "${to}" || -L "${to}" ]] && continue
  if holds_worktree "${from}" || [[ -e "${from}/.git" || -f "${from}/pyvenv.cfg" || -f "${from}/CMakeCache.txt" ]]; then
    skipped=$((skipped + 1))
    continue
  fi
  mkdir -p "$(dirname "${to}")" 2>/dev/null || continue
  if cp -a --reflink=always "${from}" "${to}" 2>/dev/null; then
    copied=$((copied + 1))
  else
    rm -rf "${to}" 2>/dev/null
  fi
done < <(git -C "${src}" ls-files -z --others --ignored --exclude-standard --directory 2>/dev/null)

if (( copied > 0 )); then
  printf 'construct: reflinked %d ignored path(s) (build outputs, dependencies) from %s in %ds%s\n' \
    "${copied}" "${src}" "$((SECONDS - start))" "$( ((skipped)) && printf ', skipped %d' "${skipped}")" >&2
fi
exit 0
