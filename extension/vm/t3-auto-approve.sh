#!/usr/bin/env bash
set -euo pipefail
# The patched server reads this flag for every new request. No restart needed.
# shellcheck disable=SC1083 # Filled with a validated boolean by the host.
enabled={{enabled}}
CONFIG_FILE=/etc/construct/config.env
# Older Construct CLIs may know config but not this key yet.
if construct config get t3-auto-approve >/dev/null 2>&1; then
  construct config set t3-auto-approve "$enabled"
else
  renderer=/opt/construct/repo/bin/config-set.sh
  [ -f "$renderer" ] || { echo "The VM cannot store the T3 tool permission setting." >&2; exit 1; }
  bash "$renderer" "$CONFIG_FILE" T3CODE_AUTO_APPROVE "$enabled"
fi
