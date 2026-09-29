#!/usr/bin/env bash
# Regression tests for OOM ranking of the coding agents:
#   bin/construct-oom-guard.sh     — the sweep, against a fake /proc, cgroup tree and systemctl
#   systemd/*.service              — OOMPolicy=continue on every unit that hosts agent sessions
#   bin/provision.sh               — setup_oom_guard installs, enables and restarts the guard
#   bin/install-ai-tools.sh        — the T3 unit is refreshed before the unchanged-build early return
# Run: bash test/oom-guard.test.sh
#
# Field failure (company-pc, 2026-09-29): the kernel OOM-killed a headless chrome inside
# t3code-serve.service; systemd's default OOMPolicy=stop then stopped the whole unit and
# every agent session in it.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GUARD="${ROOT}/bin/construct-oom-guard.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
check() {
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

proc="${tmp}/proc"
cg="${tmp}/cgroup"
stubs="${tmp}/stubs"
mkdir -p "${proc}" "${cg}" "${stubs}"

# $1 pid, $2 comm, $3 oom_score_adj
fake_proc() {
  mkdir -p "${proc}/$1"
  printf '%s\n' "$2" >"${proc}/$1/comm"
  printf '%s\n' "$3" >"${proc}/$1/oom_score_adj"
}
adj() { cat "${proc}/$1/oom_score_adj"; }

# The shape `systemctl show -p Id -p MainPID -p ControlGroup a b c` prints: one block per
# unit, blank-line separated, properties in systemd's own order (not the requested one).
cat >"${stubs}/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SYSTEMCTL_ARGV}"
[[ "$1" == show ]] && cat "${SYSTEMCTL_OUT}"
exit 0
STUB
chmod +x "${stubs}/systemctl"

export SYSTEMCTL_ARGV="${tmp}/systemctl.argv" SYSTEMCTL_OUT="${tmp}/systemctl.out"
export CONSTRUCT_SYSTEMCTL="${stubs}/systemctl"
export CONSTRUCT_OOM_GUARD_PROC="${proc}" CONSTRUCT_OOM_GUARD_CGROUP_ROOT="${cg}"

cat >"${SYSTEMCTL_OUT}" <<'OUT'
MainPID=100
ControlGroup=/system.slice/t3code-serve.service
Id=t3code-serve.service

MainPID=300
ControlGroup=/system.slice/codex-app-server.service
Id=codex-app-server.service

MainPID=0
ControlGroup=
Id=opencode-serve.service
OUT

t3cg="${cg}/system.slice/t3code-serve.service"
cxcg="${cg}/system.slice/codex-app-server.service"
mkdir -p "${t3cg}/nested" "${cxcg}"
printf '%s\n' 100 101 102 103 104 105 106 107 999 >"${t3cg}/cgroup.procs"
printf '%s\n' 110 >"${t3cg}/nested/cgroup.procs"
printf '%s\n' 300 301 >"${cxcg}/cgroup.procs"

fake_proc 100 node-MainThread 0   # the T3 server
fake_proc 101 claude -200         # an agent, still carrying the server's inherited value
fake_proc 102 codex -200
fake_proc 103 opencode 0
fake_proc 104 bash -100           # an agent's shell, inherited from the agent
fake_proc 105 dotnet -200         # a build started straight from the server
fake_proc 106 chrome 300          # a renderer that raised itself
fake_proc 107 python3 0
fake_proc 110 make -200           # in a nested cgroup
# 999 is listed but has already exited: no /proc entry.
fake_proc 300 codex 0             # codex-app-server's main process
fake_proc 301 cargo -100
fake_proc 400 sshd -1000          # outside every agent unit: never touched

# ── the sweep ────────────────────────────────────────────────────────────────

bash "${GUARD}" --once
rc=$?
check "a sweep exits 0, even with a pid that has exited" test "${rc}" -eq 0
check "the T3 server is ranked lowest (-200)" test "$(adj 100)" = -200
check "claude is ranked low (-100)" test "$(adj 101)" = -100
check "codex is ranked low (-100)" test "$(adj 102)" = -100
check "opencode is ranked low (-100)" test "$(adj 103)" = -100
check "an agent's shell loses the inherited protection" test "$(adj 104)" = 0
check "a build loses the inherited protection" test "$(adj 105)" = 0
check "a value a process raised itself is kept (chrome 300)" test "$(adj 106)" = 300
check "an ordinary process stays at 0" test "$(adj 107)" = 0
check "processes in nested cgroups are swept too" test "$(adj 110)" = 0
check "another host unit's main process is ranked as an agent" test "$(adj 300)" = -100
check "...and its children lose the inherited protection" test "$(adj 301)" = 0
check "processes outside the agent units are left alone" test "$(adj 400)" = -1000
check "one systemctl call covers all three units" \
  grep -qx -- 'show -p Id -p MainPID -p ControlGroup t3code-serve.service codex-app-server.service opencode-serve.service' "${SYSTEMCTL_ARGV}"

# A second sweep is a no-op: nothing is rewritten when it already has its value.
# (root ignores file modes, so "no write" is checked by mtime.)
touch -d '2000-01-01 00:00:00' "${proc}"/*/oom_score_adj
bash "${GUARD}" --once
check "a second sweep writes nothing" \
  test -z "$(find "${proc}" -name oom_score_adj -newermt '2000-01-02')"

# A process forked after the sweep inherits again; the next sweep lifts it.
fake_proc 108 node -200
printf '%s\n' 108 >>"${t3cg}/cgroup.procs"
bash "${GUARD}" --once
check "a new child of the server is lifted on the next sweep" test "$(adj 108)" = 0

# No agent units loaded at all (T3 disabled, a fresh VM): nothing to do, no error.
printf 'MainPID=0\nControlGroup=\nId=t3code-serve.service\n' >"${SYSTEMCTL_OUT}"
bash "${GUARD}" --once
check "no loaded units is a clean no-op" test "$?" -eq 0

# ── the units ────────────────────────────────────────────────────────────────

for unit in t3code-serve codex-app-server opencode-serve; do
  check "${unit}: OOMPolicy=continue" grep -qx 'OOMPolicy=continue' "${ROOT}/systemd/${unit}.service"
done
check "the extension's embedded T3 unit carries OOMPolicy=continue" \
  grep -qx 'OOMPolicy=continue' "${ROOT}/extension/src/t3code.js"
check "the Companion's embedded T3 unit carries OOMPolicy=continue" \
  grep -qx 'OOMPolicy=continue' "${ROOT}/companion/src/Construct.Companion.Core/State/T3Code.cs"
check "the guard unit runs the installed script" \
  grep -qx 'ExecStart=/usr/local/bin/construct-oom-guard.sh' "${ROOT}/systemd/construct-oom-guard.service"
check "the guard itself is protected" \
  grep -qx 'OOMScoreAdjust=-900' "${ROOT}/systemd/construct-oom-guard.service"

# ── provision.sh ─────────────────────────────────────────────────────────────

units="${tmp}/units"
bins="${tmp}/bins"
mkdir -p "${units}" "${bins}"
: >"${SYSTEMCTL_ARGV}"
(
  ok() { :; }
  REPO_DIR="${ROOT}" CONSTRUCT_SYSTEMD_DIR="${units}" CONSTRUCT_BIN_DIR="${bins}"
  eval "$(sed -n '/^setup_oom_guard() {/,/^}/p' "${ROOT}/bin/provision.sh")"
  setup_oom_guard
)
check "provision: setup_oom_guard succeeds" test "$?" -eq 0
check "provision: the script is installed executable" test -x "${bins}/construct-oom-guard.sh"
check "provision: the unit is installed" test -f "${units}/construct-oom-guard.service"
check "provision: the guard is enabled" grep -qx 'enable construct-oom-guard.service' "${SYSTEMCTL_ARGV}"
check "provision: the guard is restarted so a changed script takes effect" \
  grep -qx 'restart construct-oom-guard.service' "${SYSTEMCTL_ARGV}"
check "provision: the step runs on every provision" \
  grep -qx 'run_step optional "Setting up the OOM guard" setup_oom_guard' "${ROOT}/bin/provision.sh"

# ── install-ai-tools.sh ──────────────────────────────────────────────────────

installer="$(cat "${ROOT}/bin/install-ai-tools.sh")"
refresh_at="$(grep -n '_t3_unit_new="\$(sed' "${ROOT}/bin/install-ai-tools.sh" | head -1 | cut -d: -f1)"
skip_at="$(grep -n 'skipping its reinstall/restart' "${ROOT}/bin/install-ai-tools.sh" | head -1 | cut -d: -f1)"
check "install-ai-tools: the T3 unit is refreshed before the unchanged-build early return" \
  test -n "${refresh_at}" -a -n "${skip_at}" -a "${refresh_at:-0}" -lt "${skip_at:-0}"
check "install-ai-tools: the unit is rewritten only when it differs" \
  grep -qF 'if [[ "$(cat "${_t3_unit}" 2>/dev/null)" != "${_t3_unit_new}" ]]; then' <<<"${installer}"
check "install-ai-tools: the unit is no longer installed a second time after the early return" \
  test "$(grep -c 'systemd/t3code-serve.service' <<<"${installer}")" -eq 1

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
(( fail == 0 ))
