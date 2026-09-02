#!/usr/bin/env bash
# Regression tests for the guest activity heartbeat (plan §4.7):
#   bin/construct-idle-report.sh  — the probes, the JSON, the POST
#   systemd/construct-idle-report.{service,timer}
#   bin/provision.sh              — the ADDITIVE steps: CLI + spool install, the
#                                   VM token, and enabling the timer ONLY for a
#                                   service-managed VM
# Run: bash test/idle-report.test.sh
#
# Every probe reads its command/path from an overridable variable, so all of this
# runs against stubbed ss/who/tmux/curl and a fake /proc — nothing here touches
# the real machine, its units or its token.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPORTER="${ROOT}/bin/construct-idle-report.sh"
PROVISION="${ROOT}/bin/provision.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then
    pass=$((pass + 1))
    printf '  PASS  %s\n' "${name}"
  else
    fail=$((fail + 1))
    printf '  FAIL  %s\n' "${name}"
  fi
}

# ── sandbox ──────────────────────────────────────────────────────────────────

empty_cfg="${tmp}/config_empty.env"
: >"${empty_cfg}"
remote_cfg="${tmp}/config_remote.env"
printf 'CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462\nCONSTRUCT_INSTANCE_NAME=work-vm\n' \
  >"${remote_cfg}"

stubs="${tmp}/stubs"
mkdir -p "${stubs}"

# ss: prints whatever the current scenario put in ${tmp}/ss.out (header included,
# exactly like the real one).
cat >"${stubs}/ss" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SS_ARGV}"
# SS_EXIT lets a scenario make an EXISTING ss fail (unsupported filter syntax,
# a netlink hiccup, restricted permissions) the way the real one would: a usage
# error on stderr and a nonzero status.
if [[ "${SS_EXIT:-0}" != "0" ]]; then
  printf 'ss: syntax error\n' >&2
  exit "${SS_EXIT}"
fi
cat "${SS_OUT}" 2>/dev/null || true
STUB
cat >"${stubs}/who" <<'STUB'
#!/usr/bin/env bash
cat "${WHO_OUT}" 2>/dev/null || true
STUB
cat >"${stubs}/tmux" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${TMUX_ARGV}"
# Mimic the real tmux (3.x): only #{window_activity} resolves to a timestamp.
# #{pane_activity} is a valid format field that expands to an EMPTY string, so
# asking for it yields one blank line per pane -- never a timestamp.
case "$*" in
  *'#{window_activity}'*) cat "${TMUX_OUT}" 2>/dev/null || true ;;
  *) sed -e 's/.*//' "${TMUX_OUT}" 2>/dev/null || true ;;
esac
STUB
cat >"${stubs}/curl" <<'STUB'
#!/usr/bin/env bash
d="${STUB_DIR}"
printf '%s\n' "$*" >>"${d}/argv"
out=""; hdrfile=""; bodyfile=""; method="GET"; url=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -o) out="${args[i+1]}" ;;
    -X) method="${args[i+1]}" ;;
    -H) case "${args[i+1]}" in @*) hdrfile="${args[i+1]#@}" ;; esac ;;
    --data-binary) bodyfile="${args[i+1]#@}" ;;
    http://*|https://*) url="${args[i]}" ;;
  esac
done
[[ -n "${hdrfile}" ]] && cat "${hdrfile}" >>"${d}/headers"
[[ -n "${bodyfile}" ]] && { cat "${bodyfile}" >>"${d}/bodies"; printf '\n' >>"${d}/bodies"; }
printf '%s %s\n' "${method}" "${url}" >>"${d}/requests"
[[ -n "${out}" ]] && printf '%s' "$(cat "${d}/response" 2>/dev/null || true)" >"${out}"
printf '%s' "$(cat "${d}/code" 2>/dev/null || echo 200)"
exit "$(cat "${d}/exit" 2>/dev/null || echo 0)"
STUB
chmod +x "${stubs}"/*

ss_out="${tmp}/ss.out"
who_out="${tmp}/who.out"
tmux_out="${tmp}/tmux.out"
ss_argv="${tmp}/ss.argv"
tmux_argv="${tmp}/tmux.argv"
proc="${tmp}/proc"
state="${tmp}/idle-state.json"
marker="${tmp}/provisioning"
stub_dir="${tmp}/curlstub"
SS_EXIT=0     # a scenario sets this to make the ss stub fail

# A /proc/<pid>/stat line: field 4 (ppid) is what the probe walks to attribute a
# child's CPU to its agent, fields 14 (utime) and 15 (stime) are what it samples;
# everything in between is padding.
fake_proc() { # <pid> <comm> <utime> <stime> [cmdline] [ppid]
  local pid="$1" comm="$2" utime="$3" stime="$4" cmdline="${5:-}" ppid="${6:-1}" pad=""
  mkdir -p "${proc}/${pid}"
  for _ in $(seq 1 9); do pad="${pad} 0"; done
  printf '%s (%s) S %s%s %s %s 0 0 0 0\n' "${pid}" "${comm}" "${ppid}" "${pad}" "${utime}" "${stime}" \
    >"${proc}/${pid}/stat"
  if [[ -n "${cmdline}" ]]; then
    printf '%s' "${cmdline}" | tr ' ' '\0' >"${proc}/${pid}/cmdline"
  fi
}

reset_scene() {
  rm -rf "${proc}" "${state}" "${marker}" "${stub_dir}" "${ss_argv}" "${tmux_argv}"
  mkdir -p "${proc}" "${stub_dir}"
  : >"${ss_out}"
  : >"${who_out}"
  : >"${tmux_out}"
}

# Run the reporter in dry-run mode (prints the report instead of posting it).
report() {
  SS_OUT="${ss_out}" WHO_OUT="${who_out}" TMUX_OUT="${tmux_out}" \
  SS_ARGV="${ss_argv}" TMUX_ARGV="${tmux_argv}" SS_EXIT="${SS_EXIT:-0}" \
  PATH="${stubs}:${PATH}" \
  CONFIG_FILE="${empty_cfg}" \
  CONSTRUCT_IDLE_DRY_RUN=true \
  CONSTRUCT_IDLE_STATE_FILE="${state}" \
  CONSTRUCT_IDLE_PROC_DIR="${proc}" \
  CONSTRUCT_PROVISION_MARKER="${marker}" \
    bash "${REPORTER}"
}

# ── local mode: silent and successful ────────────────────────────────────────

reset_scene
printf 'ESTAB 0 0 10.0.0.5:22 10.0.0.9:51000\n' >"${ss_out}"
out="${tmp}/local.out"
SS_OUT="${ss_out}" WHO_OUT="${who_out}" TMUX_OUT="${tmux_out}" \
  SS_ARGV="${ss_argv}" TMUX_ARGV="${tmux_argv}" PATH="${stubs}:${PATH}" \
  CONFIG_FILE="${empty_cfg}" CONSTRUCT_IDLE_STATE_FILE="${state}" \
  CONSTRUCT_IDLE_PROC_DIR="${proc}" CONSTRUCT_PROVISION_MARKER="${marker}" \
  STUB_DIR="${stub_dir}" bash "${REPORTER}" >"${out}" 2>"${out}.err"
rc=$?
ok "no service URL: exits 0" test "${rc}" = 0
ok "no service URL: says nothing at all" sh -c "test ! -s '${out}' -a ! -s '${out}.err'"
ok "no service URL: makes no request" test ! -f "${stub_dir}/requests"
ok "no service URL: does not even probe" test ! -s "${ss_argv}"

# ── the probe matrix ─────────────────────────────────────────────────────────

reset_scene
printf 'Recv-Q Send-Q Local Address:Port Peer Address:Port\n' >"${ss_out}"
ok "nothing happening: busy=false with no reasons" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

reset_scene
printf 'Recv-Q Send-Q Local Address:Port Peer Address:Port\n0 0 10.0.0.5:22 10.0.0.9:51000\n' >"${ss_out}"
ok "an established SSH session is busy" test "$(report)" = '{"busy":true,"reasons":["ssh-session"]}'
ok "the ss probe asks for established sessions on port 22" \
  grep -q 'state established ( sport = :22 )' "${ss_argv}"

# who is the fallback when ss is unavailable.
reset_scene
printf 'root pts/0 2026-09-01 09:00 (10.0.0.9)\n' >"${who_out}"
noss_report="$(SS_OUT="${ss_out}" WHO_OUT="${who_out}" TMUX_OUT="${tmux_out}" \
  SS_ARGV="${ss_argv}" TMUX_ARGV="${tmux_argv}" PATH="${stubs}:${PATH}" \
  CONFIG_FILE="${empty_cfg}" CONSTRUCT_IDLE_DRY_RUN=true \
  CONSTRUCT_IDLE_STATE_FILE="${state}" CONSTRUCT_IDLE_PROC_DIR="${proc}" \
  CONSTRUCT_PROVISION_MARKER="${marker}" CONSTRUCT_IDLE_SS=definitely-no-such-command \
  bash "${REPORTER}")"
ok "who is used when ss is missing" test "${noss_report}" = '{"busy":true,"reasons":["ssh-session"]}'

# An ss that EXISTS but fails is not an empty connection table: a probe that
# cannot answer has to degrade to the next source, never to a false idle.
SS_EXIT=2
reset_scene
printf 'root pts/0 2026-09-01 09:00 (10.0.0.9)\n' >"${who_out}"
ok "a failing ss falls back to who instead of reporting idle" \
  test "$(report)" = '{"busy":true,"reasons":["ssh-session"]}'

reset_scene
: >"${who_out}"
ok "a failing ss with nobody logged in is still idle" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

reset_scene
printf 'root pts/0 2026-09-01 09:00 (10.0.0.9)\n' >"${who_out}"
ok "a failing ss and no who at all report idle rather than crashing" \
  test "$(CONSTRUCT_IDLE_WHO=definitely-no-such-command report)" = '{"busy":false,"reasons":[]}'
SS_EXIT=0

# An agent we have never sampled cannot be shown to be idle — and an explicit
# `busy:false` buys the guest NO grace in the service, so "we cannot tell yet"
# has to read as busy or the very next scheduler tick can save a VM out from
# under a working agent.
reset_scene
fake_proc 4242 claude 100 20
first="$(report)"
ok "an agent with no baseline yet is reported busy, never idle" \
  test "${first}" = '{"busy":true,"reasons":["agent-cpu:claude"]}'
ok "the first run persists its sample" grep -q '"4242":120' "${state}"
ok "the state file is one line of JSON with a version" grep -q '{"v":1,"at":' "${state}"
ok "the state file is published atomically" test -z "$(find "$(dirname "${state}")" -name '*.tmp.*')"

fake_proc 4242 claude 400 20
ok "an agent burning CPU is busy" test "$(report)" = '{"busy":true,"reasons":["agent-cpu:claude"]}'

fake_proc 4242 claude 402 20
ok "a couple of ticks stays under the threshold" test "$(report)" = '{"busy":false,"reasons":[]}'

# A brand-new process that has done nothing yet is not evidence of work.
reset_scene
fake_proc 4243 claude 0 0
ok "a freshly spawned agent that has burned nothing is not busy" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

# The make-or-break case: the agent delegates the work. `claude` waits at ~0%
# while the test suite it started burns CPU in a child, so the child's CPU has to
# be attributed to the agent that owns it.
reset_scene
fake_proc 500 claude 100 0
fake_proc 501 bash 10 0 "bash -c npm test" 500
fake_proc 502 node 10 0 "node /root/repos/app/node_modules/.bin/jest" 501
report >/dev/null
fake_proc 500 claude 101 0
fake_proc 501 bash 11 0 "bash -c npm test" 500
fake_proc 502 node 900 0 "node /root/repos/app/node_modules/.bin/jest" 501
ok "CPU burned by an agent's child counts for the agent" \
  test "$(report)" = '{"busy":true,"reasons":["agent-cpu:claude"]}'

# …but the same process tree without an agent at its root stays idle.
reset_scene
fake_proc 601 bash 10 0 "bash -c npm test"
fake_proc 602 node 10 0 "node /root/repos/app/node_modules/.bin/jest" 601
report >/dev/null
fake_proc 601 bash 11 0 "bash -c npm test"
fake_proc 602 node 900 0 "node /root/repos/app/node_modules/.bin/jest" 601
ok "an unrelated build tree does not pin the VM as busy" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

# node counts on its own only when its command line names an agent stack.
reset_scene
fake_proc 77 node 10 0 "node /usr/lib/node_modules/t3/dist/t3code-serve.js"
fake_proc 88 node 10 0 "node /root/repos/some-app/build.js"
report >/dev/null
fake_proc 77 node 500 0 "node /usr/lib/node_modules/t3/dist/t3code-serve.js"
fake_proc 88 node 500 0 "node /root/repos/some-app/build.js"
ok "a node process running an agent stack counts" \
  test "$(report)" = '{"busy":true,"reasons":["agent-cpu:t3code"]}'

reset_scene
fake_proc 88 node 10 0 "node /root/repos/some-app/build.js"
report >/dev/null
fake_proc 88 node 900 0 "node /root/repos/some-app/build.js"
ok "an unrelated node process does not pin the VM as busy" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

# tmux windows with recent output. The stub answers ONLY for a format field the
# installed tmux actually resolves (see the real-tmux checks below): asking for
# #{pane_activity} must not look like activity, because on tmux 3.x it is empty.
reset_scene
date +%s >"${tmux_out}"
ok "a tmux window with recent activity is busy" test "$(report)" = '{"busy":true,"reasons":["tmux-activity"]}'
ok "the tmux probe reads window_activity for every pane" \
  grep -q 'list-panes -a -F #{window_activity}' "${tmux_argv}"

reset_scene
printf '%s\n' "$(( $(date +%s) - 3600 ))" >"${tmux_out}"
ok "a long-quiet tmux window is not busy" test "$(report)" = '{"busy":false,"reasons":[]}'

# The real thing: the field the probe asks for must actually carry a timestamp on
# the tmux that is installed. This is the check that catches a format field which
# exists in the vocabulary but resolves to nothing.
if command -v tmux >/dev/null 2>&1; then
  tsock="construct-idle-test-$$"
  tmux -L "${tsock}" kill-server 2>/dev/null || true
  tmux -L "${tsock}" new-session -d -s busy 'while true; do echo tick; sleep 1; done' 2>/dev/null
  sleep 2
  field_used="$(tmux -L "${tsock}" list-panes -a -F '#{window_activity}' 2>/dev/null | head -1)"
  field_pane="$(tmux -L "${tsock}" list-panes -a -F '#{pane_activity}' 2>/dev/null | head -1)"
  ok "real tmux: the field the probe uses carries a timestamp" \
    sh -c "printf '%s' '${field_used}' | grep -qE '^[0-9]+$'"
  ok "real tmux: #{pane_activity} would NOT have (the bug this pins)" \
    sh -c "! printf '%s' '${field_pane}' | grep -qE '^[0-9]+$'"

  # And end to end, against the real tmux binary rather than the stub.
  # Absolute path: ${stubs} is first on PATH during the run, and a bare `tmux`
  # here would resolve straight back to the stub.
  tmux_bin="$(command -v tmux)"
  cat >"${stubs}/tmux-real" <<STUBEOF
#!/usr/bin/env bash
exec "${tmux_bin}" -L "${tsock}" "\$@"
STUBEOF
  chmod +x "${stubs}/tmux-real"
  reset_scene
  real_tmux_report="$(SS_OUT="${ss_out}" WHO_OUT="${who_out}" PATH="${stubs}:${PATH}" \
    CONFIG_FILE="${empty_cfg}" CONSTRUCT_IDLE_DRY_RUN=true \
    CONSTRUCT_IDLE_STATE_FILE="${state}" CONSTRUCT_IDLE_PROC_DIR="${proc}" \
    CONSTRUCT_PROVISION_MARKER="${marker}" CONSTRUCT_IDLE_SS=definitely-no-such-command \
    CONSTRUCT_IDLE_WHO=definitely-no-such-command \
    CONSTRUCT_IDLE_TMUX="${stubs}/tmux-real" bash "${REPORTER}")"
  ok "real tmux: a session producing output is reported busy" \
    test "${real_tmux_report}" = '{"busy":true,"reasons":["tmux-activity"]}'
  tmux -L "${tsock}" kill-server 2>/dev/null || true
fi

# Provisioning in progress: the marker plus a live PID.
reset_scene
mkdir -p "${proc}/9999"
printf '9999\n' >"${marker}"
ok "a running provision keeps the VM busy" test "$(report)" = '{"busy":true,"reasons":["provisioning"]}'

reset_scene
printf '424242\n' >"${marker}"
ok "a marker left behind by a dead provision is ignored" \
  test "$(report)" = '{"busy":false,"reasons":[]}'

# Several signals at once, in a stable order.
reset_scene
printf 'Recv-Q Send-Q Local Address:Port\n0 0 10.0.0.5:22 10.0.0.9:51000\n' >"${ss_out}"
date +%s >"${tmux_out}"
mkdir -p "${proc}/9999"
printf '9999\n' >"${marker}"
fake_proc 4242 codex 10 0
report >/dev/null
fake_proc 4242 codex 900 0
ok "every reason is reported, in a stable order" \
  test "$(report)" = '{"busy":true,"reasons":["ssh-session","agent-cpu:codex","tmux-activity","provisioning"]}'

# ── the POST ─────────────────────────────────────────────────────────────────

token_file="${tmp}/vm-token"
printf 'sekrit-vm-token-value\n' >"${token_file}"
chmod 0600 "${token_file}"

post() {
  SS_OUT="${ss_out}" WHO_OUT="${who_out}" TMUX_OUT="${tmux_out}" \
  SS_ARGV="${ss_argv}" TMUX_ARGV="${tmux_argv}" PATH="${stubs}:${PATH}" \
  STUB_DIR="${stub_dir}" \
  CONFIG_FILE="${remote_cfg}" \
  CONSTRUCT_IDLE_STATE_FILE="${state}" CONSTRUCT_IDLE_PROC_DIR="${proc}" \
  CONSTRUCT_PROVISION_MARKER="${marker}" \
  CONSTRUCT_VM_TOKEN_FILE="${1:-${token_file}}" \
    bash "${REPORTER}"
}

reset_scene
printf 'Recv-Q Send-Q Local Address:Port\n0 0 10.0.0.5:22 10.0.0.9:51000\n' >"${ss_out}"
printf '204' >"${stub_dir}/code"
post >"${tmp}/post.out" 2>"${tmp}/post.err"
ok "a successful report exits 0" test "$?" = 0
ok "a successful report says nothing" sh -c "test ! -s '${tmp}/post.out' -a ! -s '${tmp}/post.err'"
ok "it POSTs to the instance's activity route" \
  grep -qx 'POST https://buildbox.example.local:7462/api/v1/vms/work-vm/activity' "${stub_dir}/requests"
ok "the body is the busy report" grep -q '{"busy":true,"reasons":\["ssh-session"\]}' "${stub_dir}/bodies"
ok "the VM token travels in an Authorization header" \
  grep -qx 'Authorization: VmToken sekrit-vm-token-value' "${stub_dir}/headers"
ok "the VM token is NEVER on the command line" \
  sh -c "! grep -q 'sekrit-vm-token-value' '${stub_dir}/argv'"
ok "the header is passed as a file reference" grep -q -- '-H @' "${stub_dir}/argv"

reset_scene
printf '500' >"${stub_dir}/code"
printf 'boom' >"${stub_dir}/response"
post >"${tmp}/fail.out" 2>"${tmp}/fail.err"
ok "a failing service never makes the run fail" test "$?" = 0
ok "a failing service is logged for the journal" grep -q 'activity report failed (HTTP 500)' "${tmp}/fail.err"

reset_scene
post "${tmp}/no-such-token" >"${tmp}/notoken.out" 2>"${tmp}/notoken.err"
ok "a missing token never makes the run fail" test "$?" = 0
ok "a missing token is logged" grep -q 'no usable VM token' "${tmp}/notoken.err"
ok "a missing token makes no request" test ! -f "${stub_dir}/requests"

# ── the systemd units ────────────────────────────────────────────────────────

service_unit="${ROOT}/systemd/construct-idle-report.service"
timer_unit="${ROOT}/systemd/construct-idle-report.timer"

ok "the service runs the installed reporter" \
  grep -qx 'ExecStart=/usr/local/bin/construct-idle-report.sh' "${service_unit}"
ok "the service is a oneshot (the timer decides when it runs)" grep -qx 'Type=oneshot' "${service_unit}"
ok "the service does NOT source config.env as an EnvironmentFile" \
  sh -c "! grep -q '^EnvironmentFile=' '${service_unit}'"
ok "the timer drives that service" grep -qx 'Unit=construct-idle-report.service' "${timer_unit}"
ok "the timer repeats on the default interval" grep -qx 'OnUnitActiveSec=60' "${timer_unit}"
ok "the timer also fires after a boot" grep -qx 'OnBootSec=60' "${timer_unit}"
ok "the timer can be enabled" grep -qx 'WantedBy=timers.target' "${timer_unit}"

if command -v systemd-analyze >/dev/null 2>&1; then
  verify_out="${tmp}/systemd-verify.out"
  systemd-analyze verify "${service_unit}" "${timer_unit}" >"${verify_out}" 2>&1 || true
  # The reporter is not installed on the machine running the tests, so its
  # absence is the one complaint that is expected here.
  ok "systemd-analyze finds no problem beyond the uninstalled ExecStart" \
    sh -c "! grep -v 'construct-idle-report.sh is not executable' '${verify_out}' | grep -q ."
fi

# ── provision.sh: the additive steps ─────────────────────────────────────────
# The functions are extracted from provision.sh and run in a sandbox, so the real
# /usr/local/bin, /etc/systemd/system and /etc/construct are never touched.

extract() { sed -n "/^$1()/,/^}\$/p" "${PROVISION}"; }

prov_bin="${tmp}/prov/bin"
prov_forwards="${tmp}/prov/forwards"
prov_units="${tmp}/prov/units"
prov_token="${tmp}/prov/etc/vm-token"
mkdir -p "${prov_bin}" "${prov_units}" "$(dirname "${prov_token}")"

# A systemctl that records instead of acting.
cat >"${stubs}/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SYSTEMCTL_ARGV}"
STUB
chmod +x "${stubs}/systemctl"
systemctl_argv="${tmp}/systemctl.argv"

run_prov_fn() { # <function> [args...]  -- prints stdout+stderr, returns the rc
  local fn="$1"
  shift
  CONSTRUCT_STEP_RUNNER_ONLY=true PROVISION_PATH="${PROVISION}" \
  REPO_DIR="${ROOT}" \
  CONSTRUCT_BIN_DIR="${prov_bin}" CONSTRUCT_FORWARDS_DIR="${prov_forwards}" \
  CONSTRUCT_SYSTEMD_DIR="${prov_units}" CONSTRUCT_VM_TOKEN_FILE="${prov_token}" \
  CONSTRUCT_SYSTEMCTL="${stubs}/systemctl" SYSTEMCTL_ARGV="${systemctl_argv}" \
  CONSTRUCT_SERVICE_URL="${CONSTRUCT_SERVICE_URL:-}" \
  CONSTRUCT_VM_TOKEN_B64="${CONSTRUCT_VM_TOKEN_B64:-}" \
  FN_BODY="$(extract "${fn}")" FN_CALL="${fn} $*" \
    bash -c 'source "${PROVISION_PATH}"; eval "${FN_BODY}"; eval "${FN_CALL}"' 2>&1
}

# install_construct_cli — silent, and the reason `construct expose` works at all.
cli_out="$(run_prov_fn install_construct_cli)"
ok "installing the CLI prints nothing (the default path stays byte-identical)" test -z "${cli_out}"
ok "the construct CLI is installed executable" test -x "${prov_bin}/construct"
ok "the expose implementation lands next to it" test -x "${prov_bin}/construct-expose.sh"
ok "the heartbeat reporter is installed too" test -x "${prov_bin}/construct-idle-report.sh"
ok "the installed CLI finds its expose helper" \
  sh -c "CONFIG_FILE='${empty_cfg}' bash '${prov_bin}/construct' expose --help | grep -q 'construct expose'"
ok "the forward spool is created" \
  sh -c "test -d '${prov_forwards}/requests' -a -d '${prov_forwards}/acks' -a -d '${prov_forwards}/close'"
ok "the forward spool is 0755, not world-writable" \
  sh -c "test \"\$(stat -c %a '${prov_forwards}/requests')\" = 755"

# write_vm_token — decodes, never echoes.
CONSTRUCT_VM_TOKEN_B64="$(printf 'sekrit-vm-token-value' | base64 -w0)" \
  run_prov_fn write_vm_token >"${tmp}/token_step.out" 2>&1
ok "the VM token file is written" test -s "${prov_token}"
ok "the VM token file is 0600" sh -c "test \"\$(stat -c %a '${prov_token}')\" = 600"
ok "the token round-trips exactly" test "$(cat "${prov_token}")" = "sekrit-vm-token-value"
ok "the token is NEVER printed" sh -c "! grep -q 'sekrit-vm-token-value' '${tmp}/token_step.out'"
ok "the step reports only that a token was installed" \
  grep -q 'VM service token installed' "${tmp}/token_step.out"

# A value that travelled through PowerShell/SSH can pick up a CRLF tail; strict
# base64 rejects that, so the decode uses -di (field-verified in this path).
rm -f "${prov_token}"
CONSTRUCT_VM_TOKEN_B64="$(printf 'sekrit-vm-token-value' | base64 -w0)$(printf '\r\n')" \
  run_prov_fn write_vm_token >/dev/null 2>&1
ok "a CRLF tail on the base64 value still decodes" \
  test "$(cat "${prov_token}" 2>/dev/null)" = "sekrit-vm-token-value"

cp "${prov_token}" "${tmp}/token.before"
bad_out="$(CONSTRUCT_VM_TOKEN_B64='!!!not base64!!!' run_prov_fn write_vm_token)"
ok "an undecodable token leaves the existing one alone" \
  sh -c "cmp -s '${tmp}/token.before' '${prov_token}'"
ok "an undecodable token is reported without leaking anything" \
  sh -c "printf '%s' \"${bad_out}\" | grep -q 'could not be decoded'"
ok "the decode leaves no temp file behind" test -z "$(find "$(dirname "${prov_token}")" -name '*.tmp.*')"

# setup_idle_report_timer — only ever reached with a service URL.
rm -f "${systemctl_argv}"
timer_out="$(CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462 \
  run_prov_fn setup_idle_report_timer 60)"
ok "the timer unit is installed" test -f "${prov_units}/construct-idle-report.timer"
ok "the service unit is installed" test -f "${prov_units}/construct-idle-report.service"
ok "the units are readable by systemd (0644)" \
  sh -c "test \"\$(stat -c %a '${prov_units}/construct-idle-report.timer')\" = 644"
ok "systemd is reloaded and the timer enabled+started" \
  sh -c "grep -qx 'daemon-reload' '${systemctl_argv}' && grep -qx 'enable --now construct-idle-report.timer' '${systemctl_argv}'"
ok "the step says what it set up" sh -c "printf '%s' \"${timer_out}\" | grep -q 'activity heartbeat: every 60s'"

CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462 \
  run_prov_fn setup_idle_report_timer 300 >/dev/null 2>&1
ok "a custom interval is written into the timer" \
  sh -c "grep -qx 'OnUnitActiveSec=300' '${prov_units}/construct-idle-report.timer' && grep -qx 'OnBootSec=300' '${prov_units}/construct-idle-report.timer'"

# systemd reads a zero interval as a DISABLED timer: a VM that never reports is
# a VM the idle scheduler is free to save mid-job, so an unusable interval has to
# fall back to the default instead of being written through.
for bad_interval in 0 -5 99999 "" abc; do
  CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462 \
    run_prov_fn setup_idle_report_timer "${bad_interval}" >/dev/null 2>&1
  ok "an unusable interval ('${bad_interval}') falls back to 60s in the timer" \
    sh -c "grep -qx 'OnUnitActiveSec=60' '${prov_units}/construct-idle-report.timer' && grep -qx 'OnBootSec=60' '${prov_units}/construct-idle-report.timer'"
done

# …and the same value must not shrink the reporter's own freshness window: with
# INTERVAL=0 a tmux window that moved a second ago would look stale.
reset_scene
printf '%s\n' "$(( $(date +%s) - 30 ))" >"${tmux_out}"
ok "the reporter ignores a zero interval too (tmux activity still counts)" \
  test "$(CONSTRUCT_IDLE_REPORT_INTERVAL_SEC=0 report)" = '{"busy":true,"reasons":["tmux-activity"]}'

zero_cfg="${tmp}/config_zero.env"
printf 'CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462\nCONSTRUCT_IDLE_REPORT_INTERVAL_SEC=0\n' \
  >"${zero_cfg}"
zero_out="$(CONSTRUCT_STEP_RUNNER_ONLY=true PROVISION_PATH="${PROVISION}" CFG="${zero_cfg}" \
  bash -c '
    source "${PROVISION_PATH}"
    _cfg_saved() { sed -n "s/^$1=//p" "${CFG}" | head -1; }
    CONSTRUCT_IDLE_REPORT_INTERVAL_SEC="$(_cfg_saved CONSTRUCT_IDLE_REPORT_INTERVAL_SEC)"
    if ! [[ "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}" =~ ^[0-9]+$ ]] \
      || (( CONSTRUCT_IDLE_REPORT_INTERVAL_SEC < 5 || CONSTRUCT_IDLE_REPORT_INTERVAL_SEC > 3600 )); then
      CONSTRUCT_IDLE_REPORT_INTERVAL_SEC=60
    fi
    printf "%s" "${CONSTRUCT_IDLE_REPORT_INTERVAL_SEC}"
  ')"
ok "a zero interval saved in config.env resolves to 60" test "${zero_out}" = "60"
ok "provision.sh guards the interval, not just its digits" \
  sh -c "grep -q 'CONSTRUCT_IDLE_REPORT_INTERVAL_SEC < 5 || CONSTRUCT_IDLE_REPORT_INTERVAL_SEC > 3600' '${PROVISION}'"

# remove_idle_report_timer — the local default path: silent, and after it the VM
# has no heartbeat unit at all.
rm -f "${systemctl_argv}"
remove_out="$(run_prov_fn remove_idle_report_timer)"
ok "removing the timer prints nothing" test -z "${remove_out}"
ok "the timer unit is gone" test ! -f "${prov_units}/construct-idle-report.timer"
ok "the service unit is gone" test ! -f "${prov_units}/construct-idle-report.service"
ok "the timer was disabled before removal" \
  grep -qx 'disable --now construct-idle-report.timer' "${systemctl_argv}"

rm -f "${systemctl_argv}"
again_out="$(run_prov_fn remove_idle_report_timer)"
ok "with no units installed it is a silent no-op (the default path)" \
  sh -c "test -z '${again_out}' -a ! -f '${systemctl_argv}'"

# ── provision.sh: the default path is unchanged ──────────────────────────────
# The strongest form of the zero-change promise: run this branch's
# write_configuration and the base commit's against identical default inputs and
# require the resulting config.env to be byte-identical.

run_write_config() { # <provision-script> <config-file-to-write>
  CONSTRUCT_STEP_RUNNER_ONLY=true PROVISION_PATH="$1" CFG_OUT="$2" \
  REPO_DIR="${ROOT}" WS_ROOT="${tmp}/ws" \
  BODY="$(sed -n '/^write_configuration()/,/^}$/p' "$1")" \
  SERVICE_URL="${3:-}" \
    bash -c '
      source "${PROVISION_PATH}"
      CONFIG_FILE="${CFG_OUT}"
      WORKSPACE_ROOT="${WS_ROOT}"
      cfg() { bash "${REPO_DIR}/bin/config-set.sh" "${CONFIG_FILE}" "$1" "$2"; }
      AGENT_NAME=agent-vm-01; PROJECTS=default; SSH_USER=agent
      AI_TOOLS=opencode,claude-code,codex; ALLOW_HOST_PACKAGES=false
      VSCODE_SERVER=true; VSCODE_SERVE_WEB=true; VSCODE_TUNNEL=false
      CLAUDE_PARTIAL_STREAMING=true; MIC_PASSTHROUGH=false
      T3CODE=false; T3CODE_CHANNEL=stable; T3CODE_LIMIT_RESUME=false
      OPENCODE_BACKGROUND_WATCHER=false
      CONSTRUCT_EXTERNAL_HOST=""; CONSTRUCT_EXTERNAL_SSH_PORT=22; _external_ssh_port_saved=""
      CONSTRUCT_SERVICE_URL="${SERVICE_URL}"; CONSTRUCT_INSTANCE_NAME=work-vm
      CONSTRUCT_IDLE_REPORT_INTERVAL_SEC=60; _idle_interval_saved=""
      eval "${BODY}"
      write_configuration
    ' >/dev/null 2>&1
}

base_provision="${tmp}/base-provision.sh"
if git -C "${ROOT}" show HEAD:bin/provision.sh >"${base_provision}" 2>/dev/null; then
  cfg_new="${tmp}/cfg_new.env"
  cfg_base="${tmp}/cfg_base.env"
  run_write_config "${PROVISION}" "${cfg_new}"
  run_write_config "${base_provision}" "${cfg_base}"
  ok "DEFAULT path: config.env is byte-identical to the base commit" diff -q "${cfg_base}" "${cfg_new}"

  cfg_service="${tmp}/cfg_service.env"
  run_write_config "${PROVISION}" "${cfg_service}" "https://buildbox.example.local:7462"
  ok "a service-managed VM gains exactly the two service keys" \
    sh -c "diff '${cfg_base}' '${cfg_service}' | grep '^>' | wc -l | grep -qx 2"
  ok "…the service URL" grep -qx 'CONSTRUCT_SERVICE_URL=https://buildbox.example.local:7462' "${cfg_service}"
  ok "…and the instance name" grep -qx 'CONSTRUCT_INSTANCE_NAME=work-vm' "${cfg_service}"
else
  printf '  WARN  could not read bin/provision.sh from HEAD; skipping the base diff\n'
fi


ok "the heartbeat timer is only ever enabled behind CONSTRUCT_SERVICE_URL" \
  sh -c "grep -B4 'run_step optional \"Setting up the activity heartbeat timer\"' '${PROVISION}' | grep -q 'if \[\[ -n \"\${CONSTRUCT_SERVICE_URL}\" \]\]'"
ok "the VM token step is only reached with a token to install" \
  sh -c "grep -B1 'run_step optional \"Installing the VM service token\"' '${PROVISION}' | grep -q 'CONSTRUCT_VM_TOKEN_B64'"
ok "the CLI install step keeps its original title" \
  grep -q 'run_step optional "Installing construct CLI" install_construct_cli' "${PROVISION}"
ok "the provisioning marker is removed when the run finishes" \
  sh -c "sed -n '/^_finish_provision()/,/^}\$/p' '${PROVISION}' | grep -q '_PROVISION_MARKER'"

# ── shellcheck: touched scripts get no worse ─────────────────────────────────
# Counting DIAGNOSTICS, not just the set of codes: 64 more instances of an
# already-present code is still 64 new findings to read past.

if command -v shellcheck >/dev/null 2>&1; then
  for f in bin/construct bin/construct-expose.sh bin/construct-idle-report.sh \
           bin/provision.sh test/construct-expose.test.sh test/idle-report.test.sh; do
    now_count="$(shellcheck -f gcc "${ROOT}/${f}" 2>/dev/null | wc -l)"
    base_count=0
    if git -C "${ROOT}" cat-file -e "HEAD:${f}" 2>/dev/null; then
      git -C "${ROOT}" show "HEAD:${f}" >"${tmp}/base_shellcheck_input"
      base_count="$(shellcheck -f gcc "${tmp}/base_shellcheck_input" 2>/dev/null | wc -l)"
    fi
    ok "shellcheck: ${f} has no new diagnostics (${now_count} now, ${base_count} at HEAD)" \
      test "${now_count}" -le "${base_count}"
  done
fi

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
