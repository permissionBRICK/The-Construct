#!/usr/bin/env bash
# console-viewer/install.sh pulls the guacd image with bounded retries: Docker Hub answers
# with transient TLS-handshake/manifest timeouts, and one failed pull used to fail the whole
# provisioning step. Driven with a fake `docker` on PATH and `sleep` stubbed out.
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
if (( n <= ${FAKE_DOCKER_FAILURES:-0} )); then echo "net/http: TLS handshake timeout" >&2; exit 1; fi
echo "pulled $2"
FAKE
chmod +x "$tmp/docker"
run() {  # <failures> -> exit code; attempts in $tmp/count
  rm -f "$tmp/count"
  FAKE_DOCKER_COUNT="$tmp/count" FAKE_DOCKER_FAILURES="$1" PATH="$tmp:$PATH" \
    bash -c "sleep() { :; }; image=guacamole/guacd@sha256:test; $fn; pull_image" >"$tmp/out" 2>"$tmp/err"
}
pass=0; fail=0
ok() { if [[ "$2" == true ]]; then pass=$((pass+1)); echo "  PASS  $1"; else fail=$((fail+1)); echo "  FAIL  $1"; fi; }

run 2 && rc=0 || rc=$?
ok "two transient failures, then success: the pull succeeds" "$([[ $rc -eq 0 ]] && echo true || echo false)"
ok "...on the third attempt" "$([[ "$(cat "$tmp/count")" == 3 ]] && echo true || echo false)"
ok "...and each retry says so" "$(grep -q 'attempt 2 of 5' "$tmp/err" && echo true || echo false)"

run 0 && rc=0 || rc=$?
ok "a first-time success pulls exactly once" "$([[ $rc -eq 0 && "$(cat "$tmp/count")" == 1 ]] && echo true || echo false)"

run 9 && rc=0 || rc=$?
ok "a registry that never answers gives up after five attempts" "$([[ $rc -ne 0 && "$(cat "$tmp/count")" == 5 ]] && echo true || echo false)"
ok "...with a final message" "$(grep -q 'after 5 attempts' "$tmp/err" && echo true || echo false)"

echo "browser-console pull retry -- $pass passed, $fail failed"
[[ $fail -eq 0 ]]
