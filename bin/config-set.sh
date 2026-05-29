#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 3 ]]; then
  echo "Usage: config-set.sh <config-file> <key> <value>" >&2
  exit 1
fi

config_file="$1"
key="$2"
value="$3"

# Render the value for a file that is BOTH `source`-d by bash (install-ai-tools.sh,
# generate-runtime-config.sh) and read by systemd as an EnvironmentFile
# (opencode-serve, codex). Values made only of a safe character set are written
# bare, so systemd-consumed keys like OPENCODE_HOST stay simple; anything else
# (spaces, quotes, $, ...) is single-quoted so `source` can't misparse it -- e.g.
# a git user.name like "Christoph Ambrosch", which otherwise makes `. config.env`
# try to run "Ambrosch" and abort the whole provision with exit 127.
render_value() {
  local v="$1"
  if [[ "${v}" =~ ^[A-Za-z0-9_.,:@/=+-]*$ ]]; then
    printf '%s' "${v}"
  else
    printf "'%s'" "${v//\'/\'\\\'\'}"
  fi
}
rendered_value="$(render_value "${value}")"

mkdir -p "$(dirname "${config_file}")"
touch "${config_file}"

tmp_file="$(mktemp)"
if grep -Eq "^${key}=" "${config_file}"; then
  while IFS= read -r line || [[ -n "${line}" ]]; do
    case "${line}" in
      "${key}="*) printf '%s=%s\n' "${key}" "${rendered_value}" ;;
      *) printf '%s\n' "${line}" ;;
    esac
  done <"${config_file}" >"${tmp_file}"
else
  cp "${config_file}" "${tmp_file}"
  printf '%s=%s\n' "${key}" "${rendered_value}" >>"${tmp_file}"
fi

cat "${tmp_file}" >"${config_file}"
rm -f "${tmp_file}"
