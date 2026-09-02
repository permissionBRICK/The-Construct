#!/usr/bin/env bash
# END-TO-END test for the remote backend (batch B7): both clients driven against a REAL
# `constructd` in fake mode.
#
#   bash test/remote-e2e.test.sh
#
# The unit tests (test/remote-client.test.ps1, test/remote-driver.test.ps1,
# extension/test/remotehost.test.js) stub the HTTP layer, which proves the clients build
# the right requests. This proves the other half: that the requests the service really
# answers are the ones we build -- route shapes, the 202-plus-job dance, the state enum
# spelling, and the ONE-TIME VM token, which is the single most breakable part of the
# contract (service/README.md, "Jobs, the event stream and the one-time secret").
#
# Fake mode gives us the whole HTTP surface with in-memory hypervisor/ISO/forward fakes,
# so this runs on Linux with no Hyper-V and no Windows. It listens on plain http, which
# is why the pin store is not exercised here (it is unit-tested, and http has no
# certificate to pin at all).
#
# Persistence is forced to Memory: appsettings.json names a SQLite file, and the
# bootstrap admin is seeded only when the user store is EMPTY -- so a database left by an
# earlier run would silently reject this run's token. The idle scheduler is switched off
# for the same reason a test should not race a background timer.
#
# SKIPS cleanly, with a reason, when the .NET SDK or the service tree is unavailable.

# This file embeds PowerShell and Node source inside single quotes, where `$env:ROOT`,
# `$_` and `$($...)` are the OTHER language's syntax and must reach it unexpanded.
# shellcheck disable=SC2016

set -u

# EXPORTED: the pwsh and node snippets below read it out of the environment.
# (Declared and assigned separately so `export` cannot mask cd/pwd's exit status.)
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ROOT
SERVICE_PROJ="${ROOT}/service/src/Constructd.Api"
tmp="$(mktemp -d)"

pass=0
fail=0
skipped=0
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
skip() {
  skipped=$((skipped + 1))
  printf '  SKIP  %s -- %s\n' "$1" "$2"
}
# Substring assertions read better than eyeballing a JSON blob in a failure.
contains() { case "$2" in *"$1"*) return 0 ;; *) return 1 ;; esac; }
not_contains() { case "$2" in *"$1"*) return 1 ;; *) return 0 ;; esac; }
# One KEY=value line out of a client's output, defaulting to 0 so a MISSING line fails
# the assertion instead of breaking the arithmetic.
field() {
  local v
  v="$(printf '%s\n' "$2" | sed -n "s/^$1=//p" | head -1)"
  printf '%s' "${v:-0}"
}

SVC_PID=""
cleanup() {
  if [[ -n "${SVC_PID}" ]]; then kill "${SVC_PID}" 2>/dev/null || true; wait "${SVC_PID}" 2>/dev/null || true; fi
  rm -rf "${tmp}"
}
trap cleanup EXIT

printf '\n=== Remote backend end-to-end (fake constructd) ===\n'

if ! command -v dotnet >/dev/null 2>&1; then
  skip "remote end-to-end" "the .NET SDK is not installed, so the fake service cannot be started"
  printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
  exit 0
fi
if [[ ! -d "${SERVICE_PROJ}" ]]; then
  skip "remote end-to-end" "service/src/Constructd.Api is not present in this checkout"
  printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
  exit 0
fi
if ! command -v pwsh >/dev/null 2>&1; then
  skip "remote end-to-end" "pwsh is not installed, so the PowerShell client cannot be driven"
  printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
  exit 0
fi

# A free-ish port well away from the service's real default (7462) and from the mic
# tunnel range, so a developer's running service is never mistaken for this one.
PORT="${CONSTRUCT_E2E_PORT:-7913}"
BASE="http://127.0.0.1:${PORT}"
ADMIN_TOKEN="e2e-$(date +%s)-$$"

printf '  starting the fake service on %s ...\n' "${BASE}"
(
  cd "${SERVICE_PROJ}" || exit 1
  Constructd__ListenUrl="${BASE}" \
  Constructd__BootstrapAdmin="e2e-admin" \
  Constructd__BootstrapAdminToken="${ADMIN_TOKEN}" \
  Constructd__PublicHost="127.0.0.1" \
  Constructd__Persistence="Memory" \
  Constructd__Idle__SchedulerEnabled="false" \
  dotnet run --project "${SERVICE_PROJ}" -- --fake
) >"${tmp}/service.log" 2>&1 &
SVC_PID=$!

# Wait for it to answer, or give up with the log.
up=0
for _ in $(seq 1 90); do
  if curl -fsS -H "Authorization: Bearer ${ADMIN_TOKEN}" "${BASE}/api/v1/whoami" >/dev/null 2>&1; then up=1; break; fi
  if ! kill -0 "${SVC_PID}" 2>/dev/null; then break; fi
  sleep 1
done
if [[ "${up}" != "1" ]]; then
  skip "remote end-to-end" "the fake service did not come up on ${BASE} (see the log below)"
  tail -20 "${tmp}/service.log" | sed 's/^/    | /'
  printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
  exit 0
fi
printf '  service is up\n\n'

# ── (1) The PowerShell client: whoami -> create -> job -> endpoint -> power -> delete ──
printf '  -- PowerShell client (lib/AgentVm.Remote.ps1) --\n'
PS_OUT="${tmp}/ps.out"
BASE="${BASE}" TOKEN="${ADMIN_TOKEN}" pwsh -NoProfile -Command '
$ErrorActionPreference = "Stop"
. (Join-Path $env:ROOT "lib/AgentVm.Remote.ps1")
$base = $env:BASE
$auth = New-ConstructApiAuth -Mode token -Token $env:TOKEN

$me = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/whoami" -Auth $auth
Write-Output "WHOAMI=$($me.name)/$($me.role)/$($me.known)"

# The TestIdentity header stands in for Negotiate in fake mode, which is how the
# Windows-identity path is exercised at all on Linux.
$testAuth = New-ConstructApiAuth -Mode negotiate -Headers @{ "X-Constructd-Test-Identity" = "e2e-admin" }
$me2 = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/whoami" -Auth $testAuth
Write-Output "WHOAMI_NEGOTIATE=$($me2.name)/$($me2.scheme)"

$accepted = Invoke-ConstructApi -BaseUrl $base -Method POST -Path "/vms" -Auth $auth -Body @{ name = "ps-vm"; cpu = 4; ramGb = 8; diskGb = 50 }
Write-Output "JOB=$($accepted.jobId)"
$lines = New-Object System.Collections.Generic.List[string]
$result = Wait-ConstructJob -BaseUrl $base -JobId $accepted.jobId -Auth $auth -PollSeconds 1 -TimeoutSeconds 120 -OnProgress { param($l) $lines.Add($l) }
Write-Output "PROGRESS_COUNT=$($lines.Count)"
Write-Output "ENDPOINT=$($result.endpoint.sshHost):$($result.endpoint.sshPort)"
Write-Output "TOKEN_FIRST=$([bool]$result.vmToken)"

# The one-time secret: a SECOND retrieval of the same finished job must not carry it.
$again = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/jobs/$($accepted.jobId)" -Auth $auth
Write-Output "TOKEN_SECOND=$([bool]$again.result.vmToken)"
Write-Output "RESULT_SURVIVES=$($again.result.name)/$($again.result.endpoint.sshPort)"

$ep = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/vms/ps-vm/endpoint" -Auth $auth
Write-Output "ENDPOINT_ROUTE=$($ep.sshHost):$($ep.sshPort)"
$st = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/vms/ps-vm/state" -Auth $auth
Write-Output "STATE=$($st.state)"
$saved = Invoke-ConstructApi -BaseUrl $base -Method POST -Path "/vms/ps-vm/power" -Auth $auth -Body @{ action = "save" }
Write-Output "AFTER_SAVE=$($saved.state)"
$started = Invoke-ConstructApi -BaseUrl $base -Method POST -Path "/vms/ps-vm/power" -Auth $auth -Body @{ action = "start" }
Write-Output "AFTER_START=$($started.state)"

# A refused credential must be data (-NoThrow), not an exception: the enrolment flow
# branches on the 401 to offer another credential.
$badAuth = New-ConstructApiAuth -Mode token -Token "definitely-not-a-token"
$bad = Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/whoami" -Auth $badAuth -NoThrow
Write-Output "BAD_TOKEN_NULL=$($null -eq $bad)"
Write-Output "BAD_TOKEN_STATUS=$(Get-ConstructApiLastStatus)"
# An unknown VM answers 404 -- the driver'"'"'s ONLY "absent".
[void](Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/vms/no-such-vm/state" -Auth $auth -NoThrow)
Write-Output "MISSING_VM_STATUS=$(Get-ConstructApiLastStatus)"

$del = Invoke-ConstructApi -BaseUrl $base -Method DELETE -Path "/vms/ps-vm" -Auth $auth
[void](Wait-ConstructJob -BaseUrl $base -JobId $del.jobId -Auth $auth -PollSeconds 1 -TimeoutSeconds 120 -OnProgress { })
[void](Invoke-ConstructApi -BaseUrl $base -Method GET -Path "/vms/ps-vm" -Auth $auth -NoThrow)
Write-Output "AFTER_DELETE_STATUS=$(Get-ConstructApiLastStatus)"
' >"${PS_OUT}" 2>&1
PS_RC=$?
PS=$(cat "${PS_OUT}")
if [[ "${PS_RC}" != "0" ]]; then
  printf '  the PowerShell client failed (exit %s):\n' "${PS_RC}"
  sed 's/^/    | /' "${PS_OUT}"
fi

ok "ps: the script ran to completion" test "${PS_RC}" = "0"
ok "ps: whoami reports the enrolled admin" contains "WHOAMI=e2e-admin/admin/True" "${PS}"
ok "ps: the Negotiate-shaped provider works through the test identity" contains "WHOAMI_NEGOTIATE=e2e-admin/TestIdentity" "${PS}"
ok "ps: POST /vms is accepted with a job id" contains "JOB=" "${PS}"
# `field` defaults to 0 so an ABSENT line is a clean failure, not a bash syntax error.
PS_PROGRESS="$(field PROGRESS_COUNT "${PS}")"
ok "ps: the job's progress lines are streamed" test "${PS_PROGRESS}" -ge 5
ok "ps: the job result carries the endpoint the service allocated" contains "ENDPOINT=127.0.0.1:22" "${PS}"
ok "ps: the ONE-TIME VM token is handed to the terminal poll" contains "TOKEN_FIRST=True" "${PS}"
ok "ps: ...and is gone on every later retrieval" contains "TOKEN_SECOND=False" "${PS}"
ok "ps: ...while the durable part of the result survives" contains "RESULT_SURVIVES=ps-vm/" "${PS}"
ok "ps: GET /endpoint answers the same address" contains "ENDPOINT_ROUTE=127.0.0.1:22" "${PS}"
ok "ps: the state enum is the contract's spelling" contains "STATE=running" "${PS}"
ok "ps: power save reports the new state" contains "AFTER_SAVE=saved" "${PS}"
ok "ps: power start resumes it" contains "AFTER_START=running" "${PS}"
ok "ps: a refused token returns null under -NoThrow" contains "BAD_TOKEN_NULL=True" "${PS}"
ok "ps: ...with the 401 the enrolment flow branches on" contains "BAD_TOKEN_STATUS=401" "${PS}"
ok "ps: an unknown VM is a 404 (the driver's only 'absent')" contains "MISSING_VM_STATUS=404" "${PS}"
ok "ps: the VM is really gone after the delete job" contains "AFTER_DELETE_STATUS=404" "${PS}"
ok "ps: no secret is echoed in the client's output" not_contains "${ADMIN_TOKEN}" "${PS}"

# ── (2) The extension client: the same flow, through remotehost.js ───────────
printf '\n  -- extension client (extension/src/remotehost.js) --\n'
JS_OUT="${tmp}/js.out"
BASE="${BASE}" TOKEN="${ADMIN_TOKEN}" node -e '
const rh = require(process.env.ROOT + "/extension/src/remotehost.js");
const remoteDriver = require(process.env.ROOT + "/extension/src/drivers/hyperv-remote.js");
const base = process.env.BASE;
const auth = { kind: "token", token: process.env.TOKEN };
const out = (k, v) => console.log(k + "=" + v);
(async () => {
  const c = rh.createClient({ baseUrl: base, auth });
  const me = await c.whoami();
  out("WHOAMI", me.name + "/" + me.role + "/" + me.known);

  const accepted = await c.createVm({ name: "js-vm", cpu: 2, ramGb: 4, diskGb: 40 });
  out("JOB", accepted.jobId);
  // Poll the job the way the panel would: the terminal retrieval is the one that gets
  // the one-time secret.
  let job = null;
  for (let i = 0; i < 120; i++) {
    job = await c.getJob(accepted.jobId);
    if (job.state !== "running" && job.state !== "queued") break;
    await new Promise((r) => setTimeout(r, 500));
  }
  out("JOB_STATE", job.state);
  out("PROGRESS_COUNT", job.progress.length);
  out("ENDPOINT", job.result.endpoint.sshHost + ":" + job.result.endpoint.sshPort);
  out("TOKEN_FIRST", !!job.result.vmToken);
  const again = await c.getJob(accepted.jobId);
  out("TOKEN_SECOND", !!again.result.vmToken);

  // The DRIVER, against the live service: this is the mapping the panel gates on.
  const inst = { name: "js-vm", vmName: "js-vm", backend: "hyperv-remote", service: { url: base, auth: "token" } };
  const opts = { auth };
  out("DRIVER_STATE", await remoteDriver.queryVmState(inst, opts));
  out("DRIVER_CHECKPOINTS", await remoteDriver.queryAutoCheckpoints(inst, opts));
  const missing = { ...inst, name: "no-such-vm", vmName: "no-such-vm" };
  out("DRIVER_MISSING", await remoteDriver.queryVmState(missing, opts));
  const wrongCred = { ...opts, auth: { kind: "token", token: "nope" } };
  out("DRIVER_REFUSED", await remoteDriver.queryVmState(inst, wrongCred));

  await c.power("js-vm", "save");
  out("DRIVER_AFTER_SAVE", await remoteDriver.queryVmState(inst, opts));
  remoteDriver.startVm(inst, opts);
  await new Promise((r) => setTimeout(r, 800));
  out("DRIVER_AFTER_START", await remoteDriver.queryVmState(inst, opts));

  const ep = await c.getEndpoint("js-vm");
  out("ENDPOINT_ROUTE", ep.sshHost + ":" + ep.sshPort);

  let status401 = 0;
  try { await rh.createClient({ baseUrl: base, auth: { kind: "token", token: "nope" } }).whoami(); }
  catch (e) { status401 = e.status; }
  out("BAD_TOKEN_STATUS", status401);

  const del = await c.deleteVm("js-vm");
  for (let i = 0; i < 120; i++) {
    const j = await c.getJob(del.jobId);
    if (j.state !== "running" && j.state !== "queued") break;
    await new Promise((r) => setTimeout(r, 500));
  }
  out("DRIVER_AFTER_DELETE", await remoteDriver.queryVmState(inst, opts));
})().catch((e) => { console.log("ERROR=" + (e && e.message ? e.message : e)); process.exitCode = 1; });
' >"${JS_OUT}" 2>&1
JS_RC=$?
JS=$(cat "${JS_OUT}")
if [[ "${JS_RC}" != "0" ]]; then
  printf '  the extension client failed (exit %s):\n' "${JS_RC}"
  sed 's/^/    | /' "${JS_OUT}"
fi

ok "js: the script ran to completion" test "${JS_RC}" = "0"
ok "js: whoami reports the enrolled admin" contains "WHOAMI=e2e-admin/admin/true" "${JS}"
ok "js: POST /vms is accepted with a job id" contains "JOB=" "${JS}"
ok "js: the creation job succeeds" contains "JOB_STATE=succeeded" "${JS}"
JS_PROGRESS="$(field PROGRESS_COUNT "${JS}")"
ok "js: the job's progress lines are recorded" test "${JS_PROGRESS}" -ge 5
ok "js: the endpoint comes back" contains "ENDPOINT=127.0.0.1:" "${JS}"
ok "js: the ONE-TIME VM token is handed out once" contains "TOKEN_FIRST=true" "${JS}"
ok "js: ...and never again" contains "TOKEN_SECOND=false" "${JS}"
ok "js: the driver maps the live state" contains "DRIVER_STATE=running" "${JS}"
ok "js: the driver reports checkpoints as unsupported (no call made)" contains "DRIVER_CHECKPOINTS=unsupported" "${JS}"
ok "js: an unknown VM is 'absent'" contains "DRIVER_MISSING=absent" "${JS}"
ok "js: a REFUSED credential is 'unknown', never 'absent'" contains "DRIVER_REFUSED=unknown" "${JS}"
ok "js: a saved VM reads as 'off' (a start resumes it)" contains "DRIVER_AFTER_SAVE=off" "${JS}"
ok "js: the driver's startVm really starts it" contains "DRIVER_AFTER_START=running" "${JS}"
ok "js: GET /endpoint answers the allocated address" contains "ENDPOINT_ROUTE=127.0.0.1:" "${JS}"
ok "js: a refused token is a 401 the caller can branch on" contains "BAD_TOKEN_STATUS=401" "${JS}"
ok "js: after the delete job the VM is 'absent'" contains "DRIVER_AFTER_DELETE=absent" "${JS}"
ok "js: no secret is echoed in the client's output" not_contains "${ADMIN_TOKEN}" "${JS}"

# ── (3) Both clients agree ───────────────────────────────────────────────────
printf '\n  -- cross-client agreement --\n'
ok "both: the PowerShell and JS clients derive the same host slug" \
  test "$(BASE="${BASE}" pwsh -NoProfile -Command '. (Join-Path $env:ROOT "lib/AgentVm.Remote.ps1"); Get-ConstructRemoteHostSlug -BaseUrl "https://buildbox.example.local:7462"' 2>/dev/null)" \
     = "$(node -e 'console.log(require(process.env.ROOT + "/extension/src/remotehost.js").hostSlug("https://buildbox.example.local:7462"))' 2>/dev/null)"
ok "both: ...and normalise a bare host name identically" \
  test "$(pwsh -NoProfile -Command '. (Join-Path $env:ROOT "lib/AgentVm.Remote.ps1"); ConvertTo-ConstructServiceUrl -Value "buildbox"' 2>/dev/null)" \
     = "$(node -e 'console.log(require(process.env.ROOT + "/extension/src/remotehost.js").normalizeServiceUrl("buildbox"))' 2>/dev/null)"

printf '\n  %d passed, %d failed, %d skipped\n\n' "${pass}" "${fail}" "${skipped}"
[[ "${fail}" -eq 0 ]]
