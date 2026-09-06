#!/usr/bin/env bash
# Provision with current build sources; deferred packaging keeps the server's commit.
set -Eeuo pipefail
source_dir="$(python3 "$(dirname "$0")/t3code-build-source.py")"
export REPO_DIR="$source_dir"
export T3CODE_BUILD_REPOSITORY_COMMIT="$(git -C "$source_dir" rev-parse HEAD)"
exec bash "$source_dir/bin/build-t3code.sh" "$@"
