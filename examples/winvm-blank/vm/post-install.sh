#!/usr/bin/env bash
# Optional finishing touches for a freshly installed blank guest:
# copies the guest helper scripts to C:\provision\scripts and sets the
# interactive desktop to 1920x1080. Everything a project needs on top
# (build tools, runtimes, databases) goes into a script of your own.
set -euo pipefail
cd "$(dirname "$0")"
. ./config.sh

if ! vm_running; then
    echo "==> Starting VM"
    ./vm.sh start
fi

echo "==> Waiting for SSH"
for _ in $(seq 1 60); do guest_ssh "echo up" >/dev/null 2>&1 && break; sleep 10; done
guest_ssh "echo SSH OK"

echo "==> Copying guest scripts"
guest_ssh "New-Item -ItemType Directory -Force -Path C:\\provision\\scripts | Out-Null"
guest_scp ../guest/*.ps1 "$GUEST_USER@127.0.0.1:C:/provision/scripts/"

echo "==> Setting the interactive desktop to 1920x1080"
guest_ssh "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\provision\\scripts\\run-interactive.ps1 -Name winvm-resolution -Command 'C:\\provision\\scripts\\set-resolution.ps1' -TimeoutSec 60"

echo "==> Done. Guest is up and blank."
