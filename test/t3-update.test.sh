#!/usr/bin/env bash
# Plain-Bash tests for bin/construct-t3-update.sh, the T3 Code update without a
# reprovision. Run: bash test/t3-update.test.sh
#
# The installer, the resume helper and systemctl are stubs; every path the script uses
# is redirected into a scratch directory. No real T3 install or service is touched.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/construct-t3-update.sh"
if [[ "${EUID}" -ne 0 ]]; then
  echo "  SKIP  t3-update tests need root (the script refuses to run without it)"
  exit 0
fi
tmp="$(mktemp -d)"
sleeper=""
trap '[[ -n "${sleeper}" ]] && kill "${sleeper}" 2>/dev/null; rm -rf "${tmp}"' EXIT

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

repo="${tmp}/repo"
mkdir -p "${repo}/bin" "${tmp}/stub"
# The installer stub does what install_t3code does to the files this script reads:
# INSTALL=new swaps the launcher to build "new" and records it as running once the
# service is up; INSTALL=broken swaps it but the server does not come up; INSTALL=fail
# changes nothing and fails; INSTALL=stock records a stock release.
cat >"${repo}/bin/install-ai-tools.sh" <<'EOF'
#!/usr/bin/env bash
echo "install-ai-tools ${AI_TOOLS_OVERRIDE} ${AI_CONSOLE_INTEGRATION}" >>"${CALLS}"
case "${INSTALL}" in
  new|broken)
    ln -sfn "${CACHE}/new/bin/t3" "${T3CODE_LAUNCHER}"
    printf 'T3CODE_SERVER_READY=yes\nT3CODE_BUILD_KEY=new\n' >"${T3CODE_STATUS_PATH}"
    echo '{"buildHash":"new"}' >"${T3CODE_ARTIFACT_ROOT}/server-manifest.json"
    if [[ "${INSTALL}" == new ]]; then echo new >"${T3CODE_INSTALLED_BUILD_FILE}"; echo 1 >"${ACTIVE}"; else echo 0 >"${ACTIVE}"; fi ;;
  stock) echo stock:1.2.3 >"${T3CODE_INSTALLED_BUILD_FILE}" ;;
  fail) exit 1 ;;
esac
EOF
cat >"${repo}/bin/construct-t3-resume.py" <<'EOF'
import os, sys
with open(os.environ['CALLS'], 'a') as f: f.write('resume ' + ' '.join(sys.argv[1:]) + '\n')
if sys.argv[1] == 'snapshot':
    open(os.environ['CONSTRUCT_T3_RESUME_FILE'], 'w').write('{"threads": {"a": {}}}\n')
EOF
cat >"${tmp}/stub/systemctl" <<'EOF'
#!/usr/bin/env bash
echo "systemctl $*" >>"${CALLS}"
case "$1" in
  is-active) [[ "$(cat "${ACTIVE}")" == 1 ]] ;;
  restart) echo "${RESTART_ACTIVE:-1}" >"${ACTIVE}" ;;
esac
EOF
chmod +x "${tmp}/stub/systemctl"

export CALLS="${tmp}/calls.log" ACTIVE="${tmp}/active" CACHE="${tmp}/cache"
export REPO_DIR="${repo}" CONFIG_FILE="${tmp}/config.env"
export CONSTRUCT_PROVISION_MARKER="${tmp}/run/provisioning" CONSTRUCT_T3_RESUME_FILE="${tmp}/t3-resume.json"
export T3CODE_LAUNCHER="${tmp}/t3" T3CODE_STATUS_PATH="${tmp}/t3code-desktop-status"
export T3CODE_INSTALLED_BUILD_FILE="${tmp}/t3code-installed-build" T3CODE_ARTIFACT_ROOT="${tmp}/artifacts"
export CONSTRUCT_CGROUP_FILE="${tmp}/cgroup"

# A VM running patched build "old", with a busy thread.
reset() {
  rm -rf "${CACHE}" "${tmp}/run" "${tmp}/artifacts" "${CALLS}" "${CONSTRUCT_T3_RESUME_FILE}"
  mkdir -p "${CACHE}/old/bin" "${CACHE}/new/bin" "${tmp}/artifacts"
  touch "${CACHE}/old/bin/t3" "${CACHE}/new/bin/t3"
  printf 'T3CODE=true\nT3CODE_CHANNEL=stable\nT3CODE_LIMIT_RESUME=true\nT3CODE_BUILD_SOURCE=prebuilt\n' >"${CONFIG_FILE}"
  ln -sfn "${CACHE}/old/bin/t3" "${T3CODE_LAUNCHER}"
  printf 'T3CODE_SERVER_READY=yes\nT3CODE_BUILD_KEY=old\n' >"${T3CODE_STATUS_PATH}"
  echo '{"buildHash":"old"}' >"${T3CODE_ARTIFACT_ROOT}/server-manifest.json"
  echo old >"${T3CODE_INSTALLED_BUILD_FILE}"
  echo 1 >"${ACTIVE}"
  echo '0::/user.slice/user-0.slice/session-1.scope' >"${CONSTRUCT_CGROUP_FILE}"
}
run() { PATH="${tmp}/stub:${PATH}" bash "${SCRIPT}" >"${tmp}/out" 2>&1; echo $? >"${tmp}/rc"; }
rc_is() { [[ "$(cat "${tmp}/rc")" == "$1" ]]; }
out_has() { grep -qF -- "$1" "${tmp}/out"; }
called() { grep -qxF -- "$1" "${CALLS}"; }
launcher_is() { [[ "$(readlink "${T3CODE_LAUNCHER}")" == "${CACHE}/$1/bin/t3" ]]; }
no_marker() { [[ ! -e "${CONSTRUCT_PROVISION_MARKER}" ]]; }

reset; INSTALL=new run
ok "an update installs, keeps the new build and says what changed" rc_is 0
ok "  the new build runs" launcher_is new
ok "  the report names both builds" out_has "T3 Code is updated: old -> new"
ok "  it runs provisioning's T3 step alone" called "install-ai-tools t3code false"
ok "  the busy threads are recorded as a T3 Code update" called "resume snapshot --reason t3-update"
ok "  their resume is not held for a reboot" called "resume arm --no-reboot"
ok "  the provisioning marker is gone afterwards" no_marker

reset; INSTALL=broken run
ok "a new build that does not start fails the update" rc_is 1
ok "  the launcher goes back to the previous build" launcher_is old
ok "  the build status describes the previous build again" grep -qx 'T3CODE_BUILD_KEY=old' "${T3CODE_STATUS_PATH}"
ok "  so does the server manifest the host installs the Desktop from" grep -q '"old"' "${T3CODE_ARTIFACT_ROOT}/server-manifest.json"
ok "  T3 is restarted on it" called "systemctl restart t3code-serve"
ok "  and the report says so" out_has "T3 Code runs the previous build again (old)"
ok "  the interrupted threads are still resumed" called "resume arm --no-reboot"
ok "  the marker is gone" no_marker

reset; INSTALL=fail run
ok "a failed install fails the update" rc_is 1
ok "  and leaves the running build alone" launcher_is old
ok "  without a restart" bash -c "! grep -q 'systemctl restart' '${CALLS}'"
ok "  and says what still runs" out_has "T3 Code still runs the build it ran before (old)"

reset; sed -i 's/^T3CODE_LIMIT_RESUME=true/T3CODE_LIMIT_RESUME=false/' "${CONFIG_FILE}"; INSTALL=stock run
ok "a stock T3 update succeeds once its release is recorded" rc_is 0
ok "  and reports it" out_has "old -> stock:1.2.3"

reset; echo 0 >"${ACTIVE}"; INSTALL=new run
ok "a VM whose T3 is not running updates without a snapshot" rc_is 0
ok "  and has nothing to resume" bash -c "! grep -q '^resume' '${CALLS}'"

reset; sed -i 's/^T3CODE=true/T3CODE=false/' "${CONFIG_FILE}"; INSTALL=new run
ok "a VM without T3 Code is refused" rc_is 2
ok "  before anything is installed" bash -c "[[ ! -e '${CALLS}' ]]"

reset; echo '0::/system.slice/t3code-serve.service' >"${CONSTRUCT_CGROUP_FILE}"; INSTALL=new run
ok "a run from a T3 Code terminal is refused" rc_is 2
ok "  and points at the host" out_has "Start the update from the Construct Companion"
ok "  before anything is installed" bash -c "[[ ! -e '${CALLS}' ]]"

reset; sleep 30 & sleeper=$!
mkdir -p "$(dirname "${CONSTRUCT_PROVISION_MARKER}")"; echo "${sleeper}" >"${CONSTRUCT_PROVISION_MARKER}"
INSTALL=new run
ok "a running provision is not interrupted" rc_is 2
ok "  and keeps its marker" grep -qx "${sleeper}" "${CONSTRUCT_PROVISION_MARKER}"
kill "${sleeper}" 2>/dev/null; wait "${sleeper}" 2>/dev/null; sleeper=""
INSTALL=new run
ok "a marker whose run is gone does not block the update" rc_is 0

echo ""
echo "  t3-update tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
