#!/usr/bin/env bash
# Real API/Kestrel, --fake platform composition, SQLite, guest CLI and extension client.
# The test assembly alone owns fixture controls; no test routes ship in constructd.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
for tool in dotnet node curl jq; do
  command -v "$tool" >/dev/null || { echo "FAIL: required tool $tool is missing"; exit 1; }
done
dotnet test service/Constructd.sln --filter FullyQualifiedName~HostAdminEndToEndTests \
  --logger 'console;verbosity=detailed' -m:1 -p:BuildInParallel=false
