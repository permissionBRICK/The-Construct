#!/usr/bin/env bash
# Update T3 Code on this VM without a reprovision.
#
# Runs the T3 step of provisioning on its own: the same installer call provision.sh
# makes (bin/install-ai-tools.sh with AI_TOOLS_OVERRIDE=t3code), with the settings this
# VM saved in config.env. Everything else provisioning does is left alone. The host
# runs it over SSH (Provision-AgentVM.ps1 -Action t3update, which Update-T3Code.ps1
# starts) and then installs the matching T3 Code Desktop on Windows.
#
# Wrapped like a provision:
#   * the "provisioning is running" marker is held for the run, so the idle reporter
#     keeps the VM awake and the resume service waits for the run to end;
#   * the busy T3 threads are recorded before the server restarts and told what
#     happened afterwards (bin/construct-t3-resume.py);
#   * the new server has to come up. If it does not, the launcher, the build status and
#     the server manifest go back to the build that ran before, and T3 is restarted on it.
#     Superseded builds are only deleted once a new one runs, so the old one is still
#     on disk.
#
# Never run it from a T3 Code terminal: restarting t3code-serve stops every process in
# that service, this script included. It refuses to start there.
#
# Exit codes: 0 updated (or already current), 1 the update failed, 2 refused to start.
set -euo pipefail

REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
MARKER="${CONSTRUCT_PROVISION_MARKER:-/run/construct/provisioning}"
RESUME_FILE="${CONSTRUCT_T3_RESUME_FILE:-/var/lib/construct/t3-resume.json}"
LAUNCHER="${T3CODE_LAUNCHER:-/usr/local/bin/t3}"
STATUS_FILE="${T3CODE_STATUS_PATH:-/etc/construct/t3code-desktop-status}"
INSTALLED_FILE="${T3CODE_INSTALLED_BUILD_FILE:-/etc/construct/t3code-installed-build}"
ARTIFACTS="${T3CODE_ARTIFACT_ROOT:-/var/lib/construct/t3code-desktop}"
CGROUP_FILE="${CONSTRUCT_CGROUP_FILE:-/proc/self/cgroup}"

if [[ -t 1 || -n "${FORCE_COLOR:-}" ]]; then
  _C_STEP=$'\033[1;36m'; _C_OK=$'\033[32m'; _C_WARN=$'\033[33m'; _C_ERR=$'\033[31m'; _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_ERR=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n' "${_C_OK}" "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n' "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n' "${_C_ERR}" "$*" "${_C_RESET}" >&2; }

config_value() { sed -n "s/^$1=//p" "${CONFIG_FILE}" 2>/dev/null | head -1 | tr -d "\"'"; }
# shellcheck disable=SC1091
. "$(dirname "${BASH_SOURCE[0]}")/t3code-patched.sh"
T3CODE_PATCHED="$(resolve_t3code_patched "${CONFIG_FILE}")"
export T3CODE_PATCHED

if [[ "${EUID}" -ne 0 ]]; then
  err "Run as root: sudo bash ${REPO_DIR}/bin/construct-t3-update.sh"
  exit 2
fi
if grep -q 't3code-serve\.service' "${CGROUP_FILE}" 2>/dev/null; then
  err "This runs inside T3 Code, and the update restarts T3 Code, which would stop this script halfway."
  err "Start the update from the Construct Companion, T3 Code Desktop or Update-T3Code.ps1 on the host instead."
  exit 2
fi
if [[ "$(config_value T3CODE)" != "true" ]]; then
  err "T3 Code is not enabled on this VM. Enable it in the Construct control panel."
  exit 2
fi
_running="$(cat "${MARKER}" 2>/dev/null || true)"
if [[ -n "${_running}" && "${_running}" != "$$" && ( ! "${_running}" =~ ^[0-9]+$ || -e "/proc/${_running}" ) ]]; then
  err "A provision or T3 Code update is already running on this VM (pid ${_running}). Try again when it is done."
  exit 2
fi

_marked=false
_snapshot=false
_backup=""
finish() {
  local rc=$?
  # Hand the recorded threads to the resume service on every exit: a failed update
  # restarted T3 too. Nothing reboots the VM after this run, so a pending
  # /var/run/reboot-required must not hold the resume until some later boot.
  if [[ "${_snapshot}" == true && -s "${RESUME_FILE}" ]]; then
    python3 -I "${REPO_DIR}/bin/construct-t3-resume.py" arm --no-reboot \
      || warn "WARNING: could not schedule the resume of the interrupted T3 Code threads"
  fi
  if [[ "${_marked}" == true && "$(cat "${MARKER}" 2>/dev/null || true)" == "$$" ]]; then
    rm -f "${MARKER}"
  fi
  [[ -z "${_backup}" ]] || rm -rf "${_backup}"
  exit "${rc}"
}
trap finish EXIT

mkdir -p "$(dirname "${MARKER}")"
printf '%s\n' "$$" >"${MARKER}"
_marked=true

if systemctl is-active --quiet t3code-serve 2>/dev/null; then
  step "Recording busy T3 Code threads"
  _snapshot=true
  python3 -I "${REPO_DIR}/bin/construct-t3-resume.py" snapshot --reason t3-update \
    || warn "WARNING: could not record the busy T3 Code threads; the update goes on"
fi

# What runs now, to go back to if the new build does not start. The launcher points
# into the build cache for a patched install; a stock install is an npm package npm
# replaces in place, so there is nothing to go back to.
_previous_launcher="$(readlink "${LAUNCHER}" 2>/dev/null || true)"
_previous_build="$(cat "${INSTALLED_FILE}" 2>/dev/null || true)"
_backup="$(mktemp -d)"
for _f in "${STATUS_FILE}" "${ARTIFACTS}/server-manifest.json" "${ARTIFACTS}/manifest.json"; do
  [[ -f "${_f}" ]] && cp -p "${_f}" "${_backup}/$(basename "${_f}")"
done

step "Updating T3 Code ($(config_value T3CODE_CHANNEL || true), $([[ "${T3CODE_PATCHED}" == "true" ]] && echo "Construct build: $(config_value T3CODE_BUILD_SOURCE)" || echo stock))"
_install_rc=0
env TARGET_USER=root AI_TOOLS_OVERRIDE=t3code AI_CONSOLE_INTEGRATION=false \
  REPO_DIR="${REPO_DIR}" CONFIG_FILE="${CONFIG_FILE}" \
  bash "${REPO_DIR}/bin/install-ai-tools.sh" || _install_rc=$?

# The installer only warns when the server does not come up; this run has to know.
_wanted="$(sed -n 's/^T3CODE_BUILD_KEY=//p' "${STATUS_FILE}" 2>/dev/null | head -1 || true)"
_active="$(cat "${INSTALLED_FILE}" 2>/dev/null || true)"
_ready=false
if [[ "${_install_rc}" -eq 0 ]] && systemctl is-active --quiet t3code-serve; then
  if [[ "${T3CODE_PATCHED}" == "true" ]]; then
    [[ -n "${_wanted}" && "${_active}" == "${_wanted}" ]] && _ready=true
  else
    [[ "${_active}" == stock:* ]] && _ready=true
  fi
fi
if [[ "${_ready}" == true ]]; then
  if [[ "${_active}" == "${_previous_build}" ]]; then
    ok "T3 Code was already up to date (${_active})."
  else
    ok "T3 Code is updated: ${_previous_build:-<none>} -> ${_active}"
  fi
  exit 0
fi

if [[ "${_install_rc}" -ne 0 ]]; then
  err "The T3 Code update failed (installer exit ${_install_rc})."
else
  err "The updated T3 Code server did not start."
fi
# The build status and the server manifest describe the build the launcher runs; the
# host installs the Desktop from them on the next provision.
for _f in "${STATUS_FILE}" "${ARTIFACTS}/server-manifest.json" "${ARTIFACTS}/manifest.json"; do
  if [[ -f "${_backup}/$(basename "${_f}")" ]]; then cp -p "${_backup}/$(basename "${_f}")" "${_f}"; fi
done
_current_launcher="$(readlink "${LAUNCHER}" 2>/dev/null || true)"
if [[ -n "${_previous_launcher}" && "${_previous_launcher}" != "${_current_launcher}" && -e "${_previous_launcher}" ]]; then
  step "Going back to the T3 Code build that ran before"
  ln -sfn "${_previous_launcher}" "${LAUNCHER}.rollback-tmp"
  mv -fT "${LAUNCHER}.rollback-tmp" "${LAUNCHER}"
  if systemctl restart t3code-serve && systemctl is-active --quiet t3code-serve; then
    warn "T3 Code runs the previous build again (${_previous_build:-unknown})."
  else
    err "The previous build did not start either. Reprovision the VM, or check: journalctl -u t3code-serve"
  fi
elif systemctl is-active --quiet t3code-serve; then
  warn "T3 Code still runs the build it ran before (${_previous_build:-unknown})."
else
  err "T3 Code is not running. Reprovision the VM, or check: journalctl -u t3code-serve"
fi
exit 1
