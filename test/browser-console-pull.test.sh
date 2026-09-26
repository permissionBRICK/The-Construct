#!/usr/bin/env bash
# console-viewer/install.sh pulls the guacd image ONCE and, when that fails, says what to do:
# the step is optional, and on a network that blocks Docker Hub retries with backoff only
# stretched a provisioning that cannot succeed there into minutes. Fake `docker` on PATH.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
fn="$(sed -n '/^pull_image() {/,/^}/p' "$root/console-viewer/install.sh")"
[[ -n "$fn" ]] || { echo 'FAIL: pull_image not found in console-viewer/install.sh'; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/docker" <<'FAKE'
#!/usr/bin/env bash
count_file="${FAKE_DOCKER_COUNT:?}"
n=$(( $(cat "$count_file" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$count_file"
[[ "$1" == pull ]] || { echo "unexpected: $*" >&2; exit 2; }
if [[ "${FAKE_DOCKER_FAIL:-0}" == 1 ]]; then echo "net/http: TLS handshake timeout" >&2; exit 1; fi
echo "pulled $2"
FAKE
chmod +x "$tmp/docker"
run() {  # <fail 0|1> -> exit code; attempts in $tmp/count
  rm -f "$tmp/count"
  FAKE_DOCKER_COUNT="$tmp/count" FAKE_DOCKER_FAIL="$1" PATH="$tmp:$PATH" \
    bash -c "sleep() { echo 'unexpected sleep' >&2; exit 9; }; image=guacamole/guacd@sha256:test; $fn; pull_image" >"$tmp/out" 2>"$tmp/err"
}
pass=0; fail=0
ok() { if [[ "$2" == true ]]; then pass=$((pass+1)); echo "  PASS  $1"; else fail=$((fail+1)); echo "  FAIL  $1"; fi; }

run 0 && rc=0 || rc=$?
ok "a reachable registry: one pull, success" "$([[ $rc -eq 0 && "$(cat "$tmp/count")" == 1 ]] && echo true || echo false)"

run 1 && rc=0 || rc=$?
ok "an unreachable registry: exactly one attempt, no sleeping, failure" "$([[ $rc -eq 1 && "$(cat "$tmp/count")" == 1 ]] && echo true || echo false)"
ok "...and it says neither the mirrors nor Docker Hub answered" "$(grep -q 'neither the configured Docker registry mirrors (default mirror.gcr.io) nor Docker Hub answered' "$tmp/err" && echo true || echo false)"
ok "...and how to install the gateway later" "$(grep -q 're-run: sudo bash /opt/construct/repo/console-viewer/install.sh' "$tmp/err" && echo true || echo false)"

echo "browser-console pull -- $pass passed, $fail failed"
[[ $fail -eq 0 ]]
