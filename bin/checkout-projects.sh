#!/usr/bin/env bash
set -euo pipefail

# Never block on an interactive credential prompt. This runs over ssh during
# provisioning with no tty, so a missing credential would otherwise leave git
# hanging (or failing with an opaque error). Forcing prompts off turns that into
# an immediate, explicit "could not read Username" failure we can surface.
export GIT_TERMINAL_PROMPT=0

CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"

if [[ ! -f "${CONFIG_FILE}" ]]; then
  echo "Missing config file: ${CONFIG_FILE}" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1090
. "${CONFIG_FILE}"
set +a

AGENT_HOME="${AGENT_HOME:-/opt/construct}"
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"
GENERATED_JSON="${AGENT_HOME}/runtime/generated.json"

if [[ ! -f "${GENERATED_JSON}" ]]; then
  "${AGENT_HOME}/repo/bin/generate-runtime-config.sh"
fi

mkdir -p "${WORKSPACE_ROOT}"

# Fetch each configured remote independently so one stale single-branch refspec
# can be repaired without hiding which remote failed. This occurs when a repo was
# originally cloned/fetched with a narrow branch (for example `lite-split`) and
# upstream later deletes that branch: plain `git fetch --all` then fails every
# provision even though the repository and its default branch are healthy.
fetch_existing_repo() {
  local repo="$1" remote output failed=0
  while IFS= read -r remote; do
    [[ -n "${remote}" ]] || continue
    if output="$(git -C "${repo}" fetch "${remote}" --prune 2>&1)"; then
      [[ -z "${output}" ]] || printf '%s\n' "${output}"
      continue
    fi
    printf '%s\n' "${output}"
    if [[ "${output}" == *"couldn't find remote ref"* || "${output}" == *"could not find remote ref"* ]]; then
      echo "NOTE: ${repo} remote '${remote}' has a stale deleted-branch fetch refspec; restoring normal branch discovery and retrying"
      git -C "${repo}" config --replace-all "remote.${remote}.fetch" "+refs/heads/*:refs/remotes/${remote}/*"
      if git -C "${repo}" fetch "${remote}" --prune 2>&1; then
        continue
      fi
    fi
    failed=1
  done < <(git -C "${repo}" remote)
  return "${failed}"
}

# Each repository has its own worker. Its remotes remain sequential: concurrent
# fetches inside one Git repository can race over refs and FETCH_HEAD.
checkout_repo() {
  local url="$1" target="$2" status upstream
  if [[ -e "${target}/.git" ]]; then
    echo "Already cloned: ${target}"
    if ! fetch_existing_repo "${target}"; then
      echo "ERROR: fetch failed for ${target}"
      return 1
    fi
    if ! status="$(git -C "${target}" status --porcelain 2>&1)"; then
      printf 'ERROR: cannot inspect working tree: %s\n' "${status}"
      return 1
    fi
    if [[ -n "${status}" ]]; then
      echo "NOTE: ${target} has local changes; fetched only (working tree untouched)"
    elif upstream="$(git -C "${target}" rev-parse --verify '@{upstream}' 2>/dev/null)" &&
        git -C "${target}" merge --ff-only -- "${upstream}" 2>&1; then
      # The remote was already fetched. A pull here would download refs again.
      echo "Updated: ${target}"
    else
      echo "NOTE: ${target} not fast-forwarded (diverged or no upstream); fetched only"
    fi
  else
    echo "Cloning ${url} -> ${target}"
    if ! git clone -- "${url}" "${target}" 2>&1; then
      echo "ERROR: clone failed for ${url}"
      return 1
    fi
  fi
}
export -f fetch_existing_repo checkout_repo

# Zero means all independent repositories at once. Set CHECKOUT_JOBS to a
# positive number to cap simultaneous workers on a constrained host.
checkout_jobs="${CHECKOUT_JOBS:-0}"
if [[ ! "${checkout_jobs}" =~ ^(0|[1-9][0-9]{0,5})$ ]]; then
  echo "ERROR: CHECKOUT_JOBS must be an integer from 0 to 999999 (0 = all)" >&2
  exit 1
fi

# Parse before starting ANY worker: corrupt runtime configuration stays fatal.
# Keep worker logs private (Git can include authenticated URLs in diagnostics).
checkout_tmp="$(mktemp -d)"
declare -A active=() active_targets=() active_storage=()
cleanup() {
  local pid
  for pid in "${!active[@]}"; do
    # setsid gives each worker its own process group, including Git/transports.
    kill -TERM -- "-${pid}" 2>/dev/null || kill -TERM "${pid}" 2>/dev/null || true
  done
  for pid in "${!active[@]}"; do wait "${pid}" 2>/dev/null || true; done
  rm -r -- "${checkout_tmp}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
repos_tsv="${checkout_tmp}/repos.tsv"
jq -r '.repos[] | [.url, (.directory // "")] | @tsv' "${GENERATED_JSON}" >"${repos_tsv}"

if [[ ! -s "${repos_tsv}" ]]; then
  echo "No repos to check out: the selected projects (PROJECTS=${PROJECTS:-?}) resolve to zero repos[] entries in ${GENERATED_JSON}."
  echo "If the profile does declare repos, it likely hasn't synced to the VM store (${AGENT_HOME}/projects) yet -- run a config sync from the host, then: bash ${AGENT_HOME}/repo/bin/checkout-projects.sh"
  exit 0
fi

# Shared/nested targets and linked worktrees must not write simultaneously.
# realpath also catches alternate spellings and existing directory symlinks.
paths_overlap() {
  [[ "$1" == "$2" || "$1/" == "$2/"* || "$2/" == "$1/"* ]]
}
conflicts_with_active() {
  local pid
  for pid in "${!active[@]}"; do
    if paths_overlap "$1" "${active_targets[${pid}]}" ||
        paths_overlap "$2" "${active_storage[${pid}]}"; then
      return 0
    fi
  done
  return 1
}
failed=0
completed=0
started=0
wait_for_one() {
  local finished pid rc=0 index
  # Bash wait -n ignores jobs which completed before it was called. Reap those
  # by PID first, including their actual exit status; then wait for live jobs.
  while :; do
    finished=""
    rc=0
    for pid in "${!active[@]}"; do
      if ! kill -0 "${pid}" 2>/dev/null; then
        finished="${pid}"
        wait "${pid}" || rc=$?
        break
      fi
    done
    if [[ -z "${finished}" ]]; then
      wait -n -p finished "${!active[@]}" 2>/dev/null || rc=$?
    fi
    # A job can finish between the liveness check and wait -n. Scan again.
    [[ -n "${finished:-}" ]] && break
  done
  index="${active[${finished}]}"
  completed=$((completed + 1))
  printf '\n[%s completed] %s\n' "${completed}" "${active_targets[${finished}]}"
  cat "${checkout_tmp}/${index}.log"
  if (( rc != 0 )); then
    failed=$((failed + 1))
    echo "ERROR: repository worker exited ${rc}"
  fi
  unset 'active['"${finished}"']' 'active_targets['"${finished}"']' 'active_storage['"${finished}"']'
}

echo "Checking out repositories in parallel (CHECKOUT_JOBS=${checkout_jobs}; 0 = all)"
while IFS=$'\t' read -r url directory; do
  [[ -n "${url}" ]] || continue
  # Host skips are a separate, non-secret handoff; credentials retain their format.
  if [[ "${url}" =~ ^(https?)://([^/]+) ]]; then
    repo_scheme="${BASH_REMATCH[1],,}"
    repo_authority="${BASH_REMATCH[2]##*@}"
    if [[ "$repo_scheme" == https ]]; then repo_authority="${repo_authority%:443}"; else repo_authority="${repo_authority%:80}"; fi
    repo_origin="${repo_scheme}://${repo_authority}"
    skipped=false
    while IFS= read -r skip_origin; do
      if [[ "${repo_origin,,}" == "${skip_origin,,}" ]]; then skipped=true; break; fi
    done <<<"$(printf '%s' "${GIT_CLONE_SKIP_HOSTS_B64:-}" | base64 -d)"
    if [[ "${skipped}" == true ]]; then
      echo "Skipping ${repo_origin}: host skipped during credential verification"
      continue
    fi
  fi
  if [[ -z "${directory}" ]]; then directory="$(basename "${url}" .git)"; fi
  target="$(realpath -m -- "${WORKSPACE_ROOT}/${directory}")"
  storage="${target}/.git"
  if [[ -e "${target}/.git" ]]; then
    if common_dir="$(git -C "${target}" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)"; then
      storage="$(realpath -m -- "${common_dir}")"
    fi
  fi
  while (( ${#active[@]} > 0 )) && {
    (( checkout_jobs > 0 && ${#active[@]} >= checkout_jobs )) ||
      conflicts_with_active "${target}" "${storage}"
  }; do
    wait_for_one
  done
  started=$((started + 1))
  echo "[${started} started] ${target}"
  setsid bash -c 'set -euo pipefail; checkout_repo "$@"' -- "${url}" "${target}" \
    < /dev/null >"${checkout_tmp}/${started}.log" 2>&1 &
  pid=$!
  active[${pid}]="${started}"
  active_targets[${pid}]="${target}"
  active_storage[${pid}]="${storage}"
done <"${repos_tsv}"
while (( ${#active[@]} > 0 )); do wait_for_one; done

if (( failed > 0 )); then
  echo "ERROR: ${failed} repo(s) failed to check out (see the per-repo errors above)"
  echo "${failed} repo(s) failed to check out" >&2
  exit 1
fi
echo "All ${completed} repository checkout(s) completed"
