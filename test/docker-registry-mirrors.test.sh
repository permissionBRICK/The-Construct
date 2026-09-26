#!/usr/bin/env bash
# bootstrap.sh merges DOCKER_REGISTRY_MIRRORS (default mirror.gcr.io) into daemon.json:
# other settings and mirrors already listed are kept, the file is written only when it
# changes, Docker is reloaded only then, "none" leaves it alone, and a file that is not a
# JSON object is never touched. Real jq, fake systemctl, a temp daemon.json.
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
fn="$(sed -n '/^configure_docker_registry_mirrors() {/,/^}/p' "$root/bootstrap.sh")"
[[ -n "$fn" ]] || { echo 'FAIL: configure_docker_registry_mirrors not found in bootstrap.sh'; exit 1; }
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/systemctl" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "${FAKE_SYSTEMCTL_LOG:?}"
[[ "$1" == is-active ]] && exit 0
exit 0
FAKE
chmod +x "$tmp/systemctl"
stubs='step() { :; }; ok() { echo "OK $*"; }; note() { echo "NOTE $*"; }; warn() { echo "WARN $*" >&2; }'
run() {  # <mirrors env value or "-"> ; DAEMON in $tmp/daemon.json
  : > "$tmp/systemctl.log"
  local envs=(DOCKER_DAEMON_JSON="$tmp/daemon.json" FAKE_SYSTEMCTL_LOG="$tmp/systemctl.log" PATH="$tmp:$PATH")
  [[ "$1" == "-" ]] || envs+=(DOCKER_REGISTRY_MIRRORS="$1")
  env "${envs[@]}" bash -c "$stubs; $fn; configure_docker_registry_mirrors" >"$tmp/out" 2>"$tmp/err"
}
mirrors() { jq -c '."registry-mirrors"' "$tmp/daemon.json"; }
pass=0; fail=0
ok() { if [[ "$2" == true ]]; then pass=$((pass+1)); echo "  PASS  $1"; else fail=$((fail+1)); echo "  FAIL  $1"; fi; }

rm -f "$tmp/daemon.json"
run -
ok "no daemon.json: created with the default mirror" "$([[ "$(mirrors)" == '["https://mirror.gcr.io"]' ]] && echo true || echo false)"
ok "...and Docker is reloaded" "$(grep -q '^reload docker' "$tmp/systemctl.log" && echo true || echo false)"
run -
ok "second run: nothing changes, no reload" "$([[ ! -s "$tmp/systemctl.log" || "$(grep -c reload "$tmp/systemctl.log")" == 0 ]] && grep -q 'already configured' "$tmp/out" && echo true || echo false)"

printf '{"log-driver":"json-file","registry-mirrors":["https://nexus.corp.example"]}' > "$tmp/daemon.json"
run -
ok "existing settings are kept and the default is appended after the admin's mirror" \
  "$([[ "$(jq -r '."log-driver"' "$tmp/daemon.json")" == json-file && "$(mirrors)" == '["https://nexus.corp.example","https://mirror.gcr.io"]' ]] && echo true || echo false)"

run "https://a.example, https://b.example https://mirror.gcr.io"
ok "a custom list (commas or spaces) is merged in order and deduplicated" \
  "$([[ "$(mirrors)" == '["https://nexus.corp.example","https://mirror.gcr.io","https://a.example","https://b.example"]' ]] && echo true || echo false)"

before="$(cat "$tmp/daemon.json")"
run none
ok "\"none\" leaves the file alone" "$([[ "$(cat "$tmp/daemon.json")" == "$before" && ! -s "$tmp/systemctl.log" ]] && echo true || echo false)"

printf '[1,2]' > "$tmp/daemon.json"
run -
ok "a daemon.json that is not an object is never touched" "$([[ "$(cat "$tmp/daemon.json")" == '[1,2]' ]] && grep -q 'not a JSON object' "$tmp/err" && echo true || echo false)"

src="$(cat "$root/bin/provision.sh")"
ok "provision.sh hands the setting to bootstrap.sh" "$([[ "$src" == *'DOCKER_REGISTRY_MIRRORS="${DOCKER_REGISTRY_MIRRORS}" \'* ]] && echo true || echo false)"
ok "provision.sh persists it in config.env and reads it back" "$([[ "$src" == *'cfg DOCKER_REGISTRY_MIRRORS "${DOCKER_REGISTRY_MIRRORS}"'* && "$src" == *"sed -n 's/^DOCKER_REGISTRY_MIRRORS=//p'"* ]] && echo true || echo false)"
ok "the bootstrap configures the mirrors right after installing Docker, before any pull" \
  "$(awk '/Installing Docker if needed/{d=NR} /Configuring Docker registry mirrors/{m=NR} /Creating directories/{c=NR} END{exit !(d && m && c && d<m && m<c)}' "$root/bootstrap.sh" && echo true || echo false)"

echo "docker registry mirrors -- $pass passed, $fail failed"
[[ $fail -eq 0 ]]
