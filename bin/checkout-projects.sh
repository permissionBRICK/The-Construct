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

# Parse the repo list up front into a temp file. Doing this as a plain command
# (not in a pipe or process substitution) keeps a jq/config failure FATAL under
# `set -e` -- malformed/unreadable generated.json must abort, not silently check
# out zero repos. The loop then reads the file in the CURRENT shell so the
# failure counter survives it. From there a single clone/fetch failure must not
# abort the whole checkout: report it, keep going, and exit non-zero at the end
# so the caller still sees that something went wrong.
repos_tsv="$(mktemp)"
trap 'rm -f "${repos_tsv}"' EXIT
jq -r '.repos[] | [.url, (.directory // "")] | @tsv' "${GENERATED_JSON}" >"${repos_tsv}"

failed=0
while IFS=$'\t' read -r url directory; do
  if [[ -z "${url}" ]]; then
    continue
  fi

  if [[ -z "${directory}" ]]; then
    directory="$(basename "${url}" .git)"
  fi

  target="${WORKSPACE_ROOT}/${directory}"
  # NOTE: `2>&1` merges git's own messages (which it writes to stderr) into this
  # script's stdout, so the reason for any failure rides the SAME stream as the
  # progress lines below. The provisioning log can drop the separate stderr
  # channel, which is how a failed checkout previously looked like a success.
  if [[ -d "${target}/.git" ]]; then
    echo "Already cloned: ${target}"
    if ! git -C "${target}" fetch --all --prune 2>&1; then
      echo "ERROR: fetch failed for ${target}"
      failed=$((failed + 1))
    fi
  else
    echo "Cloning ${url} -> ${target}"
    if ! git clone "${url}" "${target}" 2>&1; then
      echo "ERROR: clone failed for ${url}"
      failed=$((failed + 1))
    fi
  fi
done <"${repos_tsv}"

if [[ "${failed}" -gt 0 ]]; then
  # On stdout so it shows in the provisioning log; also on stderr so a non-zero
  # exit carries a reason for any caller capturing the error stream.
  echo "ERROR: ${failed} repo(s) failed to check out (see the per-repo errors above)"
  echo "${failed} repo(s) failed to check out" >&2
  exit 1
fi
