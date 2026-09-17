#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
for name in ${!CONSTRUCT_@}; do unset "${name}"; done
tmp="$(mktemp -d)"
trap 'rm -r -- "${tmp}"' EXIT
mkdir -p "${tmp}/bin" "${tmp}/state"
export USAGE_TEST_DIR="${tmp}"
cat >"${tmp}/bin/ccusage" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${USAGE_TEST_DIR}/cc-argv"
[[ "${FAIL_TOOL:-}" != "$1" ]] || { echo '{"error":"failed"}'; exit 1; }
if [[ "$2" == monthly ]]; then day="$(date +%Y-%m)"; key=month; else day="$(date +%F)"; key=date; fi
case "$1" in
  claude) jq -n --arg r "$2" --arg d "$day" --arg key "$key" '{($r):[{($key):$d,inputTokens:10,outputTokens:20,cacheCreationTokens:3,cacheReadTokens:4,totalTokens:37,totalCost:1.23,modelBreakdowns:[{modelName:"model-a",inputTokens:10,outputTokens:20,cacheCreationTokens:3,cacheReadTokens:4,cost:1.23}]}]}' ;;
  codex) jq -n --arg r "$2" --arg d "$day" '{($r):[{date:$d,inputTokens:10,outputTokens:20,cachedInputTokens:4,totalTokens:30,costUSD:2.34,models:{"model-b":{totalTokens:30,costUSD:2.34}}}]}' ;;
  opencode) jq -n --arg r "$2" --arg d "$day" '{type:$r,data:[{period:$d,inputTokens:10,outputTokens:20,totalTokens:30,totalCost:3.45}]}' ;;
esac
STUB
cat >"${tmp}/bin/curl" <<'STUB'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >>"${USAGE_TEST_DIR}/curl-argv"
while (( $# )); do
  case "$1" in
    -H) if [[ "$2" == @* ]]; then
      header="${2#@}"
      [[ "$(stat -c %a "$header")" == 600 ]]
      cat "$header" >"${USAGE_TEST_DIR}/headers"
      printf '%s' "$(dirname "$header")" >"${USAGE_TEST_DIR}/work"
    fi; shift ;;
    --data-binary) cp "${2#@}" "${USAGE_TEST_DIR}/body.json"; shift ;;
  esac
  shift
done
printf '%s' "${HTTP_CODE:-204}"
STUB
chmod +x "${tmp}/bin/ccusage" "${tmp}/bin/curl"
export PATH="${tmp}/bin:${PATH}" CONFIG_FILE="${tmp}/config.env"
export CONSTRUCT_VM_TOKEN_FILE="${tmp}/token" CONSTRUCT_USAGE_STATE_DIR="${tmp}/state"
printf 'test-usage-secret\n' >"${tmp}/token"
cat >"${CONFIG_FILE}" <<EOF
CONSTRUCT_SERVICE_URL='https://fake.invalid'
CONSTRUCT_INSTANCE_NAME='test-vm'
CONSTRUCT_SERVICE_CA_FILE='${tmp}/ca.pem'
STATE_DIR='should-not-be-sourced'
EOF
run() { bash "${ROOT}/bin/construct-usage-report.sh" >"${tmp}/stdout" 2>"${tmp}/stderr"; }
run
jq -e '.days | length == 15' "${tmp}/body.json" >/dev/null
jq -e --arg today "$(date +%F)" '.days[] | select(.day == $today and .tool == "claude") | .totalTokens == 37 and .cacheCreateTokens == 3 and .models["model-a"].totalTokens == 37' "${tmp}/body.json" >/dev/null
jq -e --arg today "$(date +%F)" '.days[] | select(.day == $today and .tool == "codex") | .costUsd == 2.34 and .cacheReadTokens == 4 and .models["model-b"].totalTokens == 30' "${tmp}/body.json" >/dev/null
[[ -f "${tmp}/state/backfilled" && ! -d "$(cat "${tmp}/work")" ]]
rg -q 'Authorization: VmToken test-usage-secret' "${tmp}/headers"
! rg -q 'test-usage-secret' "${tmp}/curl-argv" "${tmp}/stdout" "${tmp}/stderr" "${tmp}/cc-argv"
rg -q -- '--cacert' "${tmp}/curl-argv"
rg -q 'https://fake.invalid/api/v1/vms/test-vm/usage' "${tmp}/curl-argv"
rg -q -- "daily --since $(date -d '2 days ago' +%Y%m%d) --until $(date +%Y%m%d) --json" "${tmp}/cc-argv"
run
jq -e '.days | length == 9' "${tmp}/body.json" >/dev/null
CONSTRUCT_USAGE_BACKFILL=1 run
jq -e '.days | length == 15' "${tmp}/body.json" >/dev/null
before="$(wc -l <"${tmp}/curl-argv")"
printf "CONSTRUCT_USAGE_REPORT_ENABLED='false'\n" >>"${CONFIG_FILE}"
run
[[ "$(wc -l <"${tmp}/curl-argv")" == "${before}" ]]
CONSTRUCT_USAGE_REPORT_ENABLED=true FAIL_TOOL=codex run
jq -e 'all(.days[]; .tool != "codex")' "${tmp}/body.json" >/dev/null
rm -- "${tmp}/state/backfilled"
CONSTRUCT_USAGE_REPORT_ENABLED=true HTTP_CODE=500 run
[[ ! -f "${tmp}/state/backfilled" ]]
CONSTRUCT_USAGE_REPORT_ENABLED=true FAIL_TOOL=codex run
[[ ! -f "${tmp}/state/backfilled" ]]
CONSTRUCT_USAGE_REPORT_ENABLED=true run
[[ -f "${tmp}/state/backfilled" ]]
printf '' >"${CONFIG_FILE}"
before="$(wc -l <"${tmp}/cc-argv")"
run
[[ "$(wc -l <"${tmp}/cc-argv")" == "${before}" ]]

# Run only the install function, against fake directories and systemctl.
eval "$(sed -n '/^setup_usage_report_timer() {/,/^}/p' "${ROOT}/bin/provision.sh")"
export CONSTRUCT_SYSTEMD_DIR="${tmp}/units" CONSTRUCT_BIN_DIR="${tmp}/installed" CONSTRUCT_SYSTEMCTL="${tmp}/systemctl"
cat >"${CONSTRUCT_SYSTEMCTL}" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${USAGE_TEST_DIR}/systemctl-argv"
STUB
chmod +x "${CONSTRUCT_SYSTEMCTL}"
REPO_DIR="${ROOT}" CONSTRUCT_USAGE_REPORT_INTERVAL_MIN=5 setup_usage_report_timer
rg -q '^OnUnitActiveSec=5min$' "${tmp}/units/construct-usage-report.timer"
[[ -x "${tmp}/installed/construct-usage-report.sh" && -f "${tmp}/installed/lib/usage-collect.sh" ]]
[[ ! -f "${tmp}/state/backfilled" ]]
rg -q 'enable --now construct-usage-report.timer' "${tmp}/systemctl-argv"
printf 'Guest usage reporting, backfill, failures, isolation, secret hygiene and timer installation passed.\n'
