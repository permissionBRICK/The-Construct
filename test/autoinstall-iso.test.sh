#!/usr/bin/env bash
# Tests for bin/build-autoinstall-iso.sh -- the two identity modes it renders
# (plan section 4.10/4.11) and the first-boot pool parser it installs.
#
#   default (VM_HOST set, no VM_HOSTNAME_SOURCE) : the hostname is baked in, and the
#       rendered user-data / grub.cfg / xorriso argv must be BYTE-IDENTICAL to what
#       the script produced before this batch -- every local install depends on it.
#   VM_HOSTNAME_SOURCE=hyperv-kvp                : generic media. The seed carries the
#       placeholder hostname, the KVP package, and the first-boot unit that adopts the
#       Hyper-V VM name.
#
# xorriso is not needed: a stub on PATH answers the two calls the script makes and
# captures what would have been written into the ISO.
#
# Run: bash test/autoinstall-iso.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/build-autoinstall-iso.sh"
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
is() {
  # is <expected> <actual>
  [[ "$1" == "$2" ]] || { printf '        expected: %s\n        actual:   %s\n' "$1" "$2" >&2; return 1; }
}
contains() { grep -qF -- "$2" <<<"$1"; }
fails()    { ! "$@" >/dev/null 2>&1; }

# ── Sandbox: a stub xorriso that captures instead of repacking ────────────────

mkdir -p "${tmp}/stubs"
cat >"${tmp}/stubs/xorriso" <<'STUB'
#!/usr/bin/env bash
# Stand-in for xorriso. Two calls to answer:
#   1. -osirrox on -indev <iso> -extract /boot/grub/grub.cfg <out>   -> write a menu
#   2. -indev <iso> -outdev <out> ... -map <dir> /nocloud ...        -> capture
set -u
cap="${XORRISO_CAPTURE_DIR}"

if [[ "${1:-}" == "-osirrox" ]]; then
  out=""
  args=("$@")
  for ((i = 0; i < ${#args[@]}; i++)); do
    if [[ "${args[i]}" == "-extract" ]]; then out="${args[i + 2]}"; fi
  done
  cat >"${out}" <<'GRUB'
set timeout=30
menuentry "Try or Install Ubuntu Server" {
    set gfxpayload=keep
    linux   /casper/vmlinuz  ---
    initrd  /casper/initrd
}
GRUB
  exit 0
fi

printf '%s\n' "$@" >"${cap}/xorriso.argv"
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  if [[ "${args[i]}" == "-map" ]]; then
    src="${args[i + 1]}"
    dst="${args[i + 2]}"
    case "${dst}" in
      /nocloud) cp -r "${src}" "${cap}/nocloud" ;;
      /boot/grub/grub.cfg) cp "${src}" "${cap}/grub.cfg" ;;
    esac
  fi
  if [[ "${args[i]}" == "-outdev" ]]; then : >"${args[i + 1]}"; fi
done
exit 0
STUB
chmod +x "${tmp}/stubs/xorriso"

# A source ISO only has to exist; the stub never reads it.
printf 'not really an iso\n' >"${tmp}/ubuntu-24.04-live-server-amd64.iso"
printf 'ssh-ed25519 AAAATESTKEYAAAA bootstrap@construct\n' >"${tmp}/bootstrap_ed25519.pub"

run_build() {
  # run_build <capture-dir> <script> [env assignments...]
  local capture="$1" script="$2"
  shift 2
  mkdir -p "${capture}"
  PATH="${tmp}/stubs:${PATH}" \
    XORRISO_CAPTURE_DIR="${capture}" \
    BOOTSTRAP_PUBKEY_FILE="${tmp}/bootstrap_ed25519.pub" \
    env "$@" bash "${script}" \
      "${tmp}/ubuntu-24.04-live-server-amd64.iso" "${capture}/out.iso" >"${capture}/stdout.txt" 2>&1
}

# The password hash is salted, so it differs on every run; so does the mktemp work
# directory that shows up in the xorriso argv. Both are normalized before any
# comparison, and the hash's SHAPE is asserted separately.
normalize() {
  sed -E \
    -e 's/^(    password: ").*(")$/\1<HASH>\2/' \
    -e 's#/tmp/[A-Za-z0-9._-]+/(nocloud|grub\.cfg\.new)#<WORK>/\1#' \
    -e "s#${tmp}#<TMP>#g" \
    -e 's#<TMP>/(now|base|kvp)/#<TMP>/<CAPTURE>/#g'
}

# ── Default mode: byte-identical to what the script rendered before ───────────
echo ""
echo "=== Default mode (hostname baked in) ==="

now="${tmp}/now"
run_build "${now}" "${SCRIPT}" VM_HOST=testvm
ok "default build succeeds" test -f "${now}/nocloud/user-data"

head_script="${tmp}/head-build-autoinstall-iso.sh"
if git -C "${ROOT}" show HEAD:bin/build-autoinstall-iso.sh >"${head_script}" 2>/dev/null; then
  base="${tmp}/base"
  run_build "${base}" "${head_script}" VM_HOST=testvm

  ok "default user-data is byte-identical to HEAD's" \
    is "$(normalize <"${base}/nocloud/user-data")" "$(normalize <"${now}/nocloud/user-data")"
  ok "default meta-data is byte-identical to HEAD's" \
    is "$(normalize <"${base}/nocloud/meta-data")" "$(normalize <"${now}/nocloud/meta-data")"
  ok "the grub.cfg written into the ISO is byte-identical to HEAD's" \
    is "$(normalize <"${base}/grub.cfg")" "$(normalize <"${now}/grub.cfg")"
  ok "the xorriso argv is byte-identical to HEAD's" \
    is "$(normalize <"${base}/xorriso.argv")" "$(normalize <"${now}/xorriso.argv")"
  ok "the default build's console output is unchanged" \
    is "$(normalize <"${base}/stdout.txt")" "$(normalize <"${now}/stdout.txt")"
else
  printf 'WARN: could not read HEAD:bin/build-autoinstall-iso.sh; skipping the HEAD diff\n'
fi

# Pinned too, so the default rendering stays what it is even after HEAD moves on.
user_data="$(normalize <"${now}/nocloud/user-data")"
banner_b64="$(sed -n 's/^    - echo \(.*\) | base64 -d > \/target\/etc\/issue\.d\/construct\.issue$/\1/p' <<<"${user_data}")"
pubkey="$(cat "${tmp}/bootstrap_ed25519.pub")"
expected="$(cat <<EOF
#cloud-config
autoinstall:
  version: 1
  locale: en_US.UTF-8
  keyboard:
    layout: us
  source:
    id: ubuntu-server-minimal
    search_drivers: false
  storage:
    layout:
      name: direct
  identity:
    realname: "The Construct"
    hostname: testvm
    username: agent
    password: "<HASH>"
  ssh:
    install-server: true
    allow-pw: true
    authorized-keys:
      - ${pubkey}
  late-commands:
    - mkdir -p /target/etc/issue.d
    - echo ${banner_b64} | base64 -d > /target/etc/issue.d/construct.issue
    - chmod 0644 /target/etc/issue.d/construct.issue
EOF
)"
ok "default user-data matches the pinned fixture" is "${expected}" "${user_data}"
# shellcheck disable=SC2016  # the $6$ is a crypt prefix, not a variable.
ok "the seed password is still a sha-512 crypt hash" \
  grep -Eq '^    password: "\$6\$' "${now}/nocloud/user-data"
ok "the banner still names the VM's own mshome address" \
  contains "$(base64 -d <<<"${banner_b64}")" "Target : testvm.mshome.net"
ok "default meta-data carries the real hostname" \
  is "instance-id: testvm
local-hostname: testvm" "$(cat "${now}/nocloud/meta-data")"
ok "default media installs no first-boot unit" \
  fails grep -q construct-hostname "${now}/nocloud/user-data"
ok "default media pulls in no extra package" \
  fails grep -q '^  packages:' "${now}/nocloud/user-data"

# ── hyperv-kvp mode: generic media ────────────────────────────────────────────
echo ""
echo "=== VM_HOSTNAME_SOURCE=hyperv-kvp (generic media) ==="

kvp="${tmp}/kvp"
run_build "${kvp}" "${SCRIPT}" VM_HOSTNAME_SOURCE=hyperv-kvp
ok "kvp build succeeds" test -f "${kvp}/nocloud/user-data"

kvp_user_data="$(cat "${kvp}/nocloud/user-data")"
ok "the seed hostname is the placeholder" contains "${kvp_user_data}" "hostname: construct-seed"
ok "meta-data uses the placeholder too" \
  is "instance-id: construct-seed
local-hostname: construct-seed" "$(cat "${kvp}/nocloud/meta-data")"
ok "the KVP daemon package is installed" contains "${kvp_user_data}" "- linux-cloud-tools-virtual"
ok "the first-boot unit is written into the target" \
  contains "${kvp_user_data}" "/target/etc/systemd/system/construct-hostname.service"
ok "the unit is enabled by symlink (no in-target execution)" \
  contains "${kvp_user_data}" "/target/etc/systemd/system/multi-user.target.wants/construct-hostname.service"
ok "the identity source is delivered as a config file, not baked into the script" \
  contains "${kvp_user_data}" "/target/etc/default/construct-hostname"
ok "the seed user gets passwordless sudo on generic media (its password is minted and discarded)" \
  contains "${kvp_user_data}" "NOPASSWD:ALL' > /target/etc/sudoers.d/90-construct-seed"
ok "default media does not grant it (its seed password is known)" \
  fails grep -q 'sudoers.d/90-construct-seed' "${now}/nocloud/user-data"
ok "the banner does not promise a hostname the media does not have" \
  fails grep -q 'agent-vm.mshome.net' "${kvp}/nocloud/user-data"
ok "grub.cfg is the same in both modes" is "$(cat "${now}/grub.cfg")" "$(cat "${kvp}/grub.cfg")"

decode_late_command() {
  # decode_late_command <target path> -- the base64 the late-command pipes into it
  sed -n "s#^    - echo \(.*\) | base64 -d > $1\$#\1#p" "${kvp}/nocloud/user-data" | base64 -d
}

unit="$(decode_late_command "/target/etc/systemd/system/construct-hostname.service")"
ok "unit: oneshot" contains "${unit}" "Type=oneshot"
ok "unit: wants the KVP daemon" contains "${unit}" "Wants=hv-kvp-daemon.service"
ok "unit: does not re-run once the name was adopted" \
  contains "${unit}" "ConditionPathExists=!/var/lib/construct/hostname-adopted"
ok "unit: takes its source from the config file" \
  contains "${unit}" "EnvironmentFile=-/etc/default/construct-hostname"
ok "unit: is wanted by multi-user.target" contains "${unit}" "WantedBy=multi-user.target"

defaults="$(decode_late_command "/target/etc/default/construct-hostname")"
ok "the config file selects the hyperv-kvp source" \
  is "CONSTRUCT_HOSTNAME_SOURCE=hyperv-kvp" "$(printf '%s' "${defaults}")"

guest="${tmp}/construct-hostname.sh"
decode_late_command "/target/usr/local/sbin/construct-hostname.sh" >"${guest}"
ok "the guest script is a POSIX shell script" \
  is '#!/bin/sh' "$(head -1 "${guest}")"
ok "the guest script parses under dash" sh -n "${guest}"
ok "the guest script uses no python" fails grep -q python "${guest}"
ok "the guest script keeps its source in one function" grep -q "read_identity()" "${guest}"
ok "the guest script names the planned second source" grep -q "cloud-init-metadata" "${guest}"
ok "the guest script fixes 127.0.1.1" grep -q "127\.0\.1\.1" "${guest}"
ok "the guest script renews the DHCP lease" grep -q "networkctl renew" "${guest}"
ok "the guest script falls back to netplan apply" grep -q "netplan apply" "${guest}"
ok "the guest script disables itself" grep -q "systemctl disable construct-hostname.service" "${guest}"
ok "the guest script waits with a bound" grep -q "CONSTRUCT_HOSTNAME_WAIT" "${guest}"

# ── The KVP pool parser, against a synthetic pool ─────────────────────────────
echo ""
echo "=== Hyper-V KVP pool parser ==="

pool="${tmp}/kvp_pool_3"
# Three NUL-filled records: 512-byte key + 2048-byte value each.
head -c $((3 * 2560)) /dev/zero >"${pool}"
write_record() {
  # write_record <index> <key> <value>
  printf '%s' "$2" | dd of="${pool}" bs=512 seek=$(( $1 * 5 )) conv=notrunc status=none
  printf '%s' "$3" | dd of="${pool}" bs=512 seek=$(( $1 * 5 + 1 )) conv=notrunc status=none
}
write_record 0 FullyQualifiedDomainName construct-seed.mshome.net
write_record 1 VirtualMachineName Work-VM
write_record 2 OSName "Ubuntu 24.04"

# Sourced as a library, the script is just its functions -- main() does not run.
lib() {
  CONSTRUCT_HOSTNAME_LIB=1 CONSTRUCT_KVP_POOL="$1" sh -c '. "$0"; "$@"' "${guest}" "${@:2}"
}

ok "the VM name is read out of pool 3" is "Work-VM" "$(lib "${pool}" kvp_lookup VirtualMachineName)"
ok "a record after the first one is found (the scan does not stop at record 0)" \
  is "Ubuntu 24.04" "$(lib "${pool}" kvp_lookup OSName)"
ok "the first record is found too" \
  is "construct-seed.mshome.net" "$(lib "${pool}" kvp_lookup FullyQualifiedDomainName)"
ok "an absent key fails rather than returning something" \
  fails lib "${pool}" kvp_lookup NoSuchKey

: >"${tmp}/empty_pool"
ok "an empty pool file fails" fails lib "${tmp}/empty_pool" kvp_lookup VirtualMachineName
ok "a missing pool file fails" fails lib "${tmp}/no_such_pool" kvp_lookup VirtualMachineName
# shellcheck disable=SC2016  # $0 is expanded by the inner sh, on purpose.
ok "an unknown identity source fails instead of guessing" \
  fails env CONSTRUCT_HOSTNAME_SOURCE=nonsense sh -c 'CONSTRUCT_HOSTNAME_LIB=1 . "$0"; read_identity' "${guest}"

ok "the name is lowercased" is "work-vm" "$(lib "${pool}" normalize_name "Work-VM")"
ok "trailing NULs and CRs are stripped" is "work-vm" "$(lib "${pool}" normalize_name "$(printf 'Work-VM\r')")"

check_label() {
  # check_label <name> <expected: yes|no>
  local verdict="no"
  if lib "${pool}" valid_label "$1"; then verdict="yes"; fi
  is "$2" "${verdict}"
}
ok "a plain name is a valid DNS label" check_label "work-vm" yes
ok "a single character is a valid label" check_label "a" yes
ok "a name with a space is refused" check_label "work vm" no
ok "a name with an underscore is refused" check_label "work_vm" no
ok "a leading hyphen is refused" check_label "-work" no
ok "a trailing hyphen is refused" check_label "work-" no
ok "an empty name is refused" check_label "" no
ok "an over-long label is refused" check_label "$(printf 'a%.0s' {1..64})" no
ok "63 characters are still allowed" check_label "$(printf 'a%.0s' {1..63})" yes
ok "an uppercase name is refused before normalization" check_label "Work-VM" no

# ── shellcheck: the touched script gets no worse ──────────────────────────────
echo ""
echo "=== shellcheck ==="
if command -v shellcheck >/dev/null 2>&1; then
  now_count="$(shellcheck -f gcc "${SCRIPT}" 2>/dev/null | wc -l)"
  base_count=0
  if git -C "${ROOT}" show "HEAD:bin/build-autoinstall-iso.sh" >"${tmp}/sc_base.sh" 2>/dev/null; then
    base_count="$(shellcheck -f gcc "${tmp}/sc_base.sh" 2>/dev/null | wc -l)"
  fi
  ok "shellcheck: bin/build-autoinstall-iso.sh has no new diagnostics (${now_count} now, ${base_count} at HEAD)" \
    test "${now_count}" -le "${base_count}"
else
  printf 'WARN: shellcheck not installed; skipping\n'
fi

echo ""
echo "=== ${pass} passed, ${fail} failed ==="
[[ "${fail}" -eq 0 ]]
