#!/usr/bin/env bash
#
# Scan the cloned project repos for work that would be LOST by a reinstall:
# uncommitted changes, and commits that exist locally but on no remote
# (unpushed). Emits a JSON array on stdout for the host to evaluate before it
# wipes the VM.
#
# Each element:
#   { "name": "...", "path": "...", "url": "...", "branch": "...",
#     "hasUpstream": bool, "dirty": <int>, "unpushed": <int> }
#
# Inputs (via environment):
#   WORKSPACE_ROOT  where repos are cloned   (default from config / /root/repos)
#   CONFIG_FILE     construct config.env     (default /etc/construct/config.env)
#
set -euo pipefail

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
if [[ -f "${CONFIG_FILE}" ]]; then
  # shellcheck disable=SC1090
  set -a; . "${CONFIG_FILE}"; set +a
fi
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

acc="$(mktemp)"
trap 'rm -f "${acc}"' EXIT

if [[ -d "${WORKSPACE_ROOT}" ]]; then
  shopt -s nullglob
  for repo in "${WORKSPACE_ROOT}"/*/; do
    [[ -d "${repo}.git" ]] || continue
    name="$(basename "${repo}")"
    url="$(git -C "${repo}" remote get-url origin 2>/dev/null || true)"
    branch="$(git -C "${repo}" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '')"
    if git -C "${repo}" rev-parse --abbrev-ref --symbolic-full-name '@{u}' >/dev/null 2>&1; then
      has_upstream=true
    else
      has_upstream=false
    fi
    # `{ git ... || true; }` keeps a git failure from tripping pipefail+set -e
    # (and from leaking a stray count into the substitution).
    dirty="$({ git -C "${repo}" status --porcelain 2>/dev/null || true; } | wc -l | tr -d ' ')"
    # Commits reachable from HEAD or any local branch that are present on no
    # remote-tracking branch. Including HEAD catches a detached-HEAD commit that
    # no branch references (which --branches alone would miss). With no remotes at
    # all, every local commit counts -- correct, since such a repo cannot be
    # re-cloned and its history would be lost. On an unborn HEAD (empty repo) the
    # `git log` errors out and is treated as 0.
    unpushed="$({ git -C "${repo}" log HEAD --branches --not --remotes --oneline 2>/dev/null || true; } | wc -l | tr -d ' ')"
    jq -cn \
      --arg name "${name}" \
      --arg path "${repo%/}" \
      --arg url "${url}" \
      --arg branch "${branch}" \
      --argjson hasUpstream "${has_upstream}" \
      --argjson dirty "${dirty:-0}" \
      --argjson unpushed "${unpushed:-0}" \
      '{name:$name, path:$path, url:$url, branch:$branch, hasUpstream:$hasUpstream, dirty:$dirty, unpushed:$unpushed}' \
      >>"${acc}"
  done
  shopt -u nullglob
fi

jq -s '.' "${acc}"
