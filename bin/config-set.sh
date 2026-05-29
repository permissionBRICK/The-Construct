#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 3 ]]; then
  echo "Usage: config-set.sh <config-file> <key> <value>" >&2
  exit 1
fi

config_file="$1"
key="$2"
value="$3"

mkdir -p "$(dirname "${config_file}")"
touch "${config_file}"

tmp_file="$(mktemp)"
if grep -Eq "^${key}=" "${config_file}"; then
  while IFS= read -r line || [[ -n "${line}" ]]; do
    case "${line}" in
      "${key}="*) printf '%s=%s\n' "${key}" "${value}" ;;
      *) printf '%s\n' "${line}" ;;
    esac
  done <"${config_file}" >"${tmp_file}"
else
  cp "${config_file}" "${tmp_file}"
  printf '%s=%s\n' "${key}" "${value}" >>"${tmp_file}"
fi

cat "${tmp_file}" >"${config_file}"
rm -f "${tmp_file}"
