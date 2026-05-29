#!/usr/bin/env bash
set -euo pipefail

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

jq -r '.repos[] | [.url, (.directory // "")] | @tsv' "${GENERATED_JSON}" | while IFS=$'\t' read -r url directory; do
  if [[ -z "${url}" ]]; then
    continue
  fi

  if [[ -z "${directory}" ]]; then
    directory="$(basename "${url}" .git)"
  fi

  target="${WORKSPACE_ROOT}/${directory}"
  if [[ -d "${target}/.git" ]]; then
    echo "Already cloned: ${target}"
    git -C "${target}" fetch --all --prune
  else
    echo "Cloning ${url} -> ${target}"
    git clone "${url}" "${target}"
  fi
done
