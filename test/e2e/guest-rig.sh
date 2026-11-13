#!/usr/bin/env bash
#
# Guest end-to-end rig (docs/plans/construct-platform-design.md, 14.2).
#
# Creates an Ubuntu child VM on the host this primary runs on, installs it
# unattended, provisions it with this checkout's bin/provision.sh the way
# Provision-AgentVM.ps1 does, checks the result sentinel and exit code, reboots it,
# saves its manifest (bin/construct-manifest.sh), and deletes it again.
#
#   bash test/e2e/guest-rig.sh [quick|full] [--keep] [--compare MANIFEST]
#
#   quick      small tool set (Claude Code only), T3 Code, VS Code server and the
#              Samba share off
#   full       the defaults of a new install: all agents, T3 Code, VS Code, Samba
#   --keep     leave the child running for inspection (it still expires with its
#              lifetime); the run prints how to reach and delete it
#   --compare  print the diff of this run's manifest against a saved one
#
# Needs a primary with the right to create children (`construct vm identity`), and
# curl, jq, ssh, scp, xorriso, git and mkpasswd or openssl on this machine.
#
# Install media: the Ubuntu live-server ISO (UBUNTU_RELEASE, default 24.04) is
# downloaded once, made into an autoinstall ISO with bin/build-autoinstall-iso.sh
# and uploaded to the host once. Both stay cached for later runs: the ISOs under
# CONSTRUCT_RIG_CACHE, the upload on the host under the name construct-rig-<hash>
# (delete it with `construct vm media delete ID --yes`). Run artefacts (provisioning
# log, manifest, timings) go to CONSTRUCT_RIG_RUNS, outside the checkout.
#
# Exit: 0 passed; 1 failed; 2 incomplete (the host refused the child, so the
# required verification did not run); 64 usage.
#
set -uo pipefail
export LC_ALL=C

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
UBUNTU_RELEASE="${UBUNTU_RELEASE:-24.04}"
CACHE="${CONSTRUCT_RIG_CACHE:-${XDG_CACHE_HOME:-${HOME}/.cache}/construct-guest-rig}"
RUNS="${CONSTRUCT_RIG_RUNS:-${XDG_STATE_HOME:-${HOME}/.local/state}/construct-guest-rig/runs}"
LIFETIME="${CONSTRUCT_RIG_LIFETIME:-4h}"
SEED_USER=agent
SEED_PASS=agent
GUEST_HOSTNAME=construct-rig

variant=quick keep=false compare=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    quick|full) variant="$1" ;;
    --keep) keep=true ;;
    --compare) shift; [[ $# -gt 0 ]] || { echo "--compare needs a manifest file" >&2; exit 64; }; compare="$1" ;;
    -h|--help) sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1 (see --help)" >&2; exit 64 ;;
  esac
  shift
done
[[ -z "${compare}" || -f "${compare}" ]] || { echo "no such manifest: ${compare}" >&2; exit 64; }

log()  { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
fail() { log "FAILED: $*"; exit 1; }
incomplete() { log "$*"; echo "incomplete: required verification not run" >&2; exit 2; }

for tool in construct curl jq ssh scp xorriso git sha256sum; do
  command -v "${tool}" >/dev/null 2>&1 || fail "${tool} is not installed"
done
command -v mkpasswd >/dev/null 2>&1 || command -v openssl >/dev/null 2>&1 || fail "need mkpasswd (whois) or openssl"

# --- stage timings -------------------------------------------------------------
declare -a STAGES=()
stage_name="" stage_start=0
stage() {
  local now; now="$(date +%s)"
  [[ -z "${stage_name}" ]] || STAGES+=("${stage_name}"$'\t'"$(( now - stage_start ))")
  stage_name="${1:-}"; stage_start="${now}"
  [[ -z "${stage_name}" ]] || log "== ${stage_name}"
}
print_timings() {
  stage ""
  local row total=0
  printf '\nStage timings (%s):\n' "${variant}"
  for row in "${STAGES[@]}"; do
    printf '  %-26s %6ss\n' "${row%%$'\t'*}" "${row##*$'\t'}"
    total=$(( total + ${row##*$'\t'} ))
  done
  printf '  %-26s %6ss\n' total "${total}"
}

# --- the child and its teardown -----------------------------------------------
child="" run_dir=""
teardown() {
  local rc=$?
  trap - EXIT INT TERM
  if [[ -n "${child}" ]] && construct vm inspect "${child}" --json >/dev/null 2>&1; then
    if [[ "${keep}" == true ]]; then
      log "keeping ${child}; delete it with: construct vm media release ${child}; construct vm delete ${child} --yes"
    else
      stage "teardown"
      construct vm media release "${child}" --json >/dev/null 2>&1 || log "media release failed for ${child} (the delete collects it)"
      if ! construct vm delete "${child}" --yes --json >/dev/null; then
        log "COULD NOT DELETE ${child}; delete it by hand: construct vm delete ${child} --yes"
        rc=1
      fi
    fi
  fi
  if [[ -n "${run_dir}" ]]; then print_timings >"${run_dir}/timings.txt"; cat "${run_dir}/timings.txt" >&2; fi
  exit "${rc}"
}
trap teardown EXIT
trap 'exit 130' INT TERM

# --- ssh to the guest ----------------------------------------------------------
mkdir -p "${CACHE}" "${RUNS}"
KEY="${CACHE}/bootstrap_ed25519"
# The committed bootstrap key (keys/), copied because ssh refuses a key file others can read.
install -m 600 "${REPO}/keys/bootstrap_ed25519" "${KEY}"
SSH_OPTS=(-i "${KEY}" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=no
          -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=10
          -o ServerAliveInterval=30 -o ServerAliveCountMax=10)
address=""
gssh() { ssh "${SSH_OPTS[@]}" "${SEED_USER}@${address}" "$@"; }

guest_address() {
  construct vm addresses "${child}" --json 2>/dev/null |
    jq -r '[.addresses[]? | select((.family|ascii_downcase) == "ipv4") | .address][0] // empty'
}

# Wait until the guest reports an address and SSH answers there with the bootstrap key.
# The address comes from the guest agent of the installed system, which starts with
# sshd; an address that stays unreachable for 10 minutes means this primary has no
# route to the child, which is the host's network and not the guest under test.
wait_for_ssh() {
  local limit="$1" deadline seen=0
  deadline=$(( $(date +%s) + limit ))
  while (( $(date +%s) < deadline )); do
    address="$(guest_address)"
    if [[ -n "${address}" ]]; then
      gssh true 2>/dev/null && return 0
      (( seen > 0 )) || seen="$(date +%s)"
      if (( $(date +%s) - seen > 600 )) && ! timeout 5 bash -c "echo >/dev/tcp/${address}/22" 2>/dev/null; then
        incomplete "${child} reports ${address}, but this primary cannot reach it (no route from the primary's network to the child's)"
      fi
    fi
    sleep 15
  done
  return 1
}

# --- install media -------------------------------------------------------------
# Echoes the host media id of the rig's autoinstall ISO, building and uploading it
# only when the cache does not have it yet.
ensure_media() {
  local base="https://releases.ubuntu.com/${UBUNTU_RELEASE}/" sums line sha iso key name out tmp id
  sums="$(curl -fsSL "${base}SHA256SUMS")" || fail "could not read ${base}SHA256SUMS"
  line="$(grep -E ' \*?ubuntu-[0-9.]+-live-server-amd64\.iso$' <<<"${sums}" | sort -V -k2 | tail -1)"
  [[ -n "${line}" ]] || fail "no live-server ISO listed at ${base}"
  sha="${line%% *}"; iso="${line##* }"; iso="${iso#\*}"

  if [[ "$(cat "${CACHE}/${iso}.sha256" 2>/dev/null)" != "${sha}" ]]; then
    log "downloading ${iso}"
    curl -fsSL --retry 3 -o "${CACHE}/${iso}.part" "${base}${iso}" || fail "download of ${iso} failed"
    [[ "$(sha256sum "${CACHE}/${iso}.part" | cut -d' ' -f1)" == "${sha}" ]] || fail "${iso}: checksum mismatch"
    mv "${CACHE}/${iso}.part" "${CACHE}/${iso}"
    printf '%s\n' "${sha}" >"${CACHE}/${iso}.sha256"
    find "${CACHE}" -maxdepth 1 -name 'ubuntu-*.iso*' ! -name "${iso}*" -delete
  fi

  # The rig's ISO is named by what goes into it: the Ubuntu ISO, the builder and this
  # script's seed addition below.
  key="$(printf '%s\n' "${sha}" "$(sha256sum "${REPO}/bin/build-autoinstall-iso.sh")" "qemu-guest-agent v1" | sha256sum | cut -c1-12)"
  name="construct-rig-${key}"
  out="${CACHE}/${name}.iso"
  if [[ ! -f "${out}" ]]; then
    log "building ${name}.iso"
    tmp="${CACHE}/build.$$"; mkdir -p "${tmp}"
    VM_HOST="${GUEST_HOSTNAME}" VM_USER="${SEED_USER}" VM_PASS="${SEED_PASS}" \
      bash "${REPO}/bin/build-autoinstall-iso.sh" "${CACHE}/${iso}" "${tmp}/base.iso" >"${tmp}/build.log" 2>&1 ||
      { cat "${tmp}/build.log" >&2; rm -rf "${tmp}"; fail "build-autoinstall-iso.sh failed"; }
    # The one addition: QEMU's guest agent, through which a Proxmox host learns the
    # child's address (`construct vm addresses`). Everything else is the product's seed.
    xorriso -osirrox on -indev "${tmp}/base.iso" -extract /nocloud/user-data "${tmp}/user-data" >/dev/null 2>&1 ||
      { rm -rf "${tmp}"; fail "could not read the seed back"; }
    chmod u+w "${tmp}/user-data"
    printf '  packages:\n    - qemu-guest-agent\n' >>"${tmp}/user-data"
    xorriso -indev "${tmp}/base.iso" -outdev "${out}.part" -boot_image any replay \
      -map "${tmp}/user-data" /nocloud/user-data >/dev/null 2>&1 ||
      { rm -rf "${tmp}" "${out}.part"; fail "could not repack the ISO"; }
    rm -rf "${tmp}"
    mv "${out}.part" "${out}"
    find "${CACHE}" -maxdepth 1 -name 'construct-rig-*.iso' ! -name "${name}.iso" -delete
  fi

  id="$(construct vm media list --json | jq -r --arg n "${name}" '[.[] | select(.name == $n and .state == "ready") | .id][0] // empty')"
  if [[ -z "${id}" ]]; then
    log "uploading ${name}.iso to the host"
    id="$(construct vm media upload "${out}" --role install --name "${name}" --json | jq -r '.media.id // .id // empty')"
    [[ -n "${id}" ]] || id="$(construct vm media list --json | jq -r --arg n "${name}" '[.[] | select(.name == $n and .state == "ready") | .id][0] // empty')"
    [[ -n "${id}" ]] || fail "upload of ${name}.iso failed"
  fi
  printf '%s\n' "${id}"
}

# --- provisioning settings, as Provision-AgentVM.ps1 passes them ------------------
provision_env() {
  local commit
  commit="$(git -C "${REPO}" rev-parse HEAD)"
  local -a env=(
    "PROJECTS=default" "SSH_USER=${SEED_USER}" "AGENT_NAME=${GUEST_HOSTNAME}-agent" "CLAUDE_USER=root"
    "GIT_USER_NAME_B64=" "GIT_USER_EMAIL_B64=" "GIT_CREDENTIAL_STORE=" "GIT_CLONE_CREDENTIALS_B64="
    "GIT_CLONE_SKIP_HOSTS_B64=" "CHECKOUT_PROJECTS=false" "SETUP_ROOT_SSH_KEY=true"
    "VSCODE_TUNNEL=false" "VSCODE_SERVE_WEB_TOKEN_B64=" "VSCODE_CLIENT_COMMIT="
    "CONSTRUCT_VERSION=${commit}" "CLAUDE_PARTIAL_STREAMING=true" "MIC_PASSTHROUGH=false"
    "OPENCODE_BACKGROUND_WATCHER=" "T3CODE_CHANNEL=" "T3CODE_BUILD_SOURCE=" "T3CODE_PATCHED="
    "T3CODE_AUTO_APPROVE=" "T3CODE_HTTPS=" "DOCKER_REGISTRY_MIRRORS=" "T3CODE_BUILD_MODE=server"
    "REBOOT_AFTER_PROVISION=true"
  )
  if [[ "${variant}" == quick ]]; then
    env+=("AI_TOOLS=claude-code" "VSCODE_SERVER=false" "VSCODE_SERVE_WEB=false" "SMB_SHARE=false" "T3CODE=false")
  else
    env+=("AI_TOOLS=opencode,claude-code,codex" "VSCODE_SERVER=true" "VSCODE_SERVE_WEB=true" "SMB_SHARE=true" "T3CODE=true")
  fi
  local item out=""
  for item in "${env[@]}"; do out+=" ${item%%=*}='${item#*=}'"; done
  printf '%s' "${out# }"
}

# ==============================================================================
stage "media"
media_id="$(ensure_media)" || exit 1
log "install media ${media_id}"

stage "create"
primary="$(construct vm identity --json | jq -r '.vmName // empty')" || true
[[ -n "${primary}" ]] || incomplete "this machine is not a primary with a host service"
child="${primary}-rig-$(date +%m%d%H%M)"
run_dir="${RUNS}/${child}"
mkdir -p "${run_dir}"
log "creating ${child} (${variant}), run directory ${run_dir}"
construct vm create --name "${child}" --media "${media_id}" --preset linux \
  --ram-gb 3 --cpus 2 --disk-gb 25 --lifetime "${LIFETIME}" \
  --boot-order disk,installMedia --json >"${run_dir}/create.json" 2>"${run_dir}/create.err"
rc=$?
if [[ ${rc} -ne 0 ]]; then
  cat "${run_dir}/create.err" >&2
  case "${rc}" in
    4|5|9|10|11) incomplete "the host refused the child (construct vm create exit ${rc})" ;;
    *) fail "construct vm create exited ${rc}" ;;
  esac
fi

stage "install"
# The disk is empty on the first boot, so the firmware falls through to the ISO; the
# installer's own reboot then boots the installed system from disk.
wait_for_ssh 3600 || fail "no SSH on ${child} within 60 minutes (address: ${address:-none reported})"
log "${child} answers at ${address}"
construct vm media release "${child}" --json >/dev/null || log "media release failed (retried at teardown)"

stage "copy source"
# This checkout's source files (tracked and untracked, not ignored), packed and
# unpacked into /opt/construct/repo as Provision-AgentVM.ps1 does.
( cd "${REPO}" && git ls-files -z --cached --others --exclude-standard |
    grep -zv -e '\.iso$' -e '^\.construct-settings\.json$' -e '^\.construct-backup/' |
    xargs -0 tar -czf "${run_dir}/construct-repo.tar.gz" --no-recursion -- ) ||
  fail "could not pack the checkout"
scp "${SSH_OPTS[@]}" -q "${run_dir}/construct-repo.tar.gz" "${SEED_USER}@${address}:/tmp/construct-repo.tar.gz" ||
  fail "could not copy the source to the guest"
printf '%s\n' "${SEED_PASS}" | gssh "sudo -S -p '' bash -c 'mkdir -p /opt/construct && rm -rf /opt/construct/repo && mkdir -p /opt/construct/repo && tar -xzf /tmp/construct-repo.tar.gz -C /opt/construct/repo && chown -R ${SEED_USER}:${SEED_USER} /opt/construct'" ||
  fail "could not unpack the source on the guest"

stage "provision"
command="$(provision_env) bash /opt/construct/repo/bin/provision.sh"
printf '%s\n' "${SEED_PASS}" |
  gssh "sudo -S -p '' env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive bash -lc \"${command}\"" \
  >"${run_dir}/provision.log" 2>&1
provision_rc=$?
log "provision.sh exited ${provision_rc}; log: ${run_dir}/provision.log"

# The result block (bin/provision.sh _print_machine_result), colour codes stripped.
result="$(sed 's/\x1b\[[0-9;]*[A-Za-z]//g; s/\r$//' "${run_dir}/provision.log" |
  sed -n '/^===CONSTRUCT-PROVISION-RESULT===$/,/^===END-CONSTRUCT-PROVISION-RESULT===$/p')"
printf '%s\n' "${result}" >"${run_dir}/result.txt"
errors="$(sed -n 's/^errors=//p' <<<"${result}")"
grep '^error=' <<<"${result}" >&2
[[ -n "${result}" && "${errors}" =~ ^[0-9]+$ ]] || fail "no valid result sentinel (exit ${provision_rc})"
[[ ${provision_rc} -eq 0 && ${errors} -eq 0 ]] || fail "provisioning reported ${errors} failed step(s), exit ${provision_rc}"

stage "reboot"
# The bootstrap path of Provision-AgentVM.ps1 ends with a reboot; the manifest is
# taken after it so unit states are those of a booted, provisioned guest.
gssh "sudo -n systemctl reboot" >/dev/null 2>&1 || true
sleep 20
wait_for_ssh 900 || fail "no SSH on ${child} after the reboot"

stage "manifest"
gssh "sudo -n bash -s" <"${REPO}/bin/construct-manifest.sh" >"${run_dir}/manifest.txt" 2>"${run_dir}/manifest.err" ||
  fail "manifest failed: $(tail -3 "${run_dir}/manifest.err")"
log "manifest: ${run_dir}/manifest.txt ($(wc -l <"${run_dir}/manifest.txt") lines)"
if [[ -n "${compare}" ]]; then
  diff -u "${compare}" "${run_dir}/manifest.txt" >"${run_dir}/manifest.diff"
  log "manifest diff against ${compare}: $(grep -c '^[-+][^-+]' "${run_dir}/manifest.diff") changed line(s)"
  cat "${run_dir}/manifest.diff"
fi

log "PASSED (${variant})"
exit 0
