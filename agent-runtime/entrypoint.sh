#!/usr/bin/env bash
set -euo pipefail

echo "The Construct runtime started"
echo "AGENT_NAME=${AGENT_NAME:-unset}"
echo "AGENT_PROJECTS=${AGENT_PROJECTS:-unset}"
echo "WORKSPACE_ROOT=${WORKSPACE_ROOT:-/root/repos}"

if [[ -f /opt/construct/runtime/generated.json ]]; then
  echo "Merged project requirements:"
  jq . /opt/construct/runtime/generated.json
fi

echo "Replace this entrypoint with the real agent process when ready."
exec sleep infinity
