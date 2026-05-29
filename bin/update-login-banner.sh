#!/usr/bin/env bash
set -euo pipefail

issue_dir="/etc/issue.d"
issue_file="${issue_dir}/construct.issue"

mkdir -p "${issue_dir}"

{
  /opt/construct/repo/bin/print-connection-info.sh
  printf '\n'
} >"${issue_file}"

chmod 0644 "${issue_file}"
