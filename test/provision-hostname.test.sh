#!/usr/bin/env bash
# Tests for bin/provision.sh's belt-and-braces hostname adoption (plan section 4.10).
#
# A VM installed from GENERIC media boots as 'construct-seed' and is supposed to adopt
# the name the hypervisor gave it at first boot. When that did not happen, provisioning
# is the last place that knows the name the host asked for -- and a VM whose hostname
# stays the placeholder is unreachable as <name>.mshome.net, because the virtual
# switch's DNS publishes the guest's own hostname.
#
# The default path (a VM whose hostname is already its own) must be untouched, which is
# asserted two ways: the decision function refuses, and the shipped call site is gated
# on the placeholder.
#
# Run: bash test/provision-hostname.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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
is() {
  [[ "$1" == "$2" ]] || { printf '        expected: %s\n        actual:   %s\n' "$1" "$2" >&2; return 1; }
}
fails() { ! "$@" >/dev/null 2>&1; }

# ── The function under test, taken from the shipped script ────────────────────
# Extracted rather than reimplemented: provision.sh's body provisions a machine, so it
# cannot be sourced, but the decision has to be the one that ships.
lib="${tmp}/adopt.sh"
{
  printf 'SEED_PLACEHOLDER_HOSTNAME="construct-seed"\n'
  sed -n '/^adopt_seed_hostname() {$/,/^}$/p' "${PROVISION}"
} >"${lib}"
# shellcheck source=/dev/null
. "${lib}"

if ! declare -f adopt_seed_hostname >/dev/null; then
  printf '  FAIL  adopt_seed_hostname could not be extracted from bin/provision.sh\n'
  exit 1
fi

# The real hostnamectl must NEVER be reached: it would rename the machine running the
# tests. Stubs shadow it (and `hostname`) for this whole file; the second stub directory
# stands in for a host where setting the name that way does not work.
mkdir -p "${tmp}/stubs" "${tmp}/stubs-broken"
cat >"${tmp}/stubs/hostnamectl" <<STUB
#!/usr/bin/env bash
printf '%s\n' "\$*" >>"${tmp}/hostnamectl.calls"
exit 0
STUB
cat >"${tmp}/stubs/hostname" <<STUB
#!/usr/bin/env bash
printf '%s\n' "\$*" >>"${tmp}/hostname.calls"
exit 0
STUB
printf '#!/usr/bin/env bash\nexit 1\n' >"${tmp}/stubs-broken/hostnamectl"
chmod +x "${tmp}/stubs/hostnamectl" "${tmp}/stubs/hostname" "${tmp}/stubs-broken/hostnamectl"
PATH="${tmp}/stubs:${PATH}"

new_etc() {
  # A throwaway /etc with the hosts file a stock Ubuntu install writes.
  local dir="${tmp}/etc-$1"
  rm -rf "${dir}"
  mkdir -p "${dir}"
  {
    printf '127.0.0.1\tlocalhost\n'
    printf '127.0.1.1\tconstruct-seed\n'
    printf '::1\tip6-localhost ip6-loopback\n'
  } >"${dir}/hosts"
  printf 'construct-seed\n' >"${dir}/hostname"
  printf '%s' "${dir}"
}

adopt() {
  # adopt <etc dir> <current hostname> <wanted name>
  PROVISION_ETC_DIR="$1" adopt_seed_hostname "$2" "$3"
}

# ── The seed case ─────────────────────────────────────────────────────────────
echo ""
echo "=== A VM still carrying the placeholder ==="

etc="$(new_etc adopt)"
: >"${tmp}/hostnamectl.calls"
ok "the wanted name is applied and echoed back" \
  is "work-vm" "$(adopt "${etc}" construct-seed work-vm)"
ok "hostnamectl is the way it is set" \
  is "set-hostname work-vm" "$(cat "${tmp}/hostnamectl.calls")"
ok "127.0.1.1 now resolves to the new name" \
  is "$(printf '127.0.1.1\twork-vm')" "$(grep '^127\.0\.1\.1' "${etc}/hosts")"
ok "the rest of /etc/hosts is left alone" \
  is "127.0.0.1	localhost
127.0.1.1	work-vm
::1	ip6-localhost ip6-loopback" "$(cat "${etc}/hosts")"

etc="$(new_etc upper)"
ok "a name from the host is lowercased (a hostname is a DNS label)" \
  is "build-box" "$(adopt "${etc}" construct-seed Build-Box)"

etc="$(new_etc nohosts)"
printf '127.0.0.1\tlocalhost\n' >"${etc}/hosts"
ok "a hosts file without a 127.0.1.1 line gets one" \
  is "work-vm" "$(adopt "${etc}" construct-seed work-vm)"
ok "...appended, not replacing what was there" \
  is "127.0.0.1	localhost
127.0.1.1	work-vm" "$(cat "${etc}/hosts")"

# When hostnamectl cannot do it (no systemd, a container-ish image) the file is written
# directly -- otherwise the name would silently not stick.
etc="$(new_etc fallback)"
ok "when hostnamectl fails the name is written to /etc/hostname" \
  is "work-vm" "$(PATH="${tmp}/stubs-broken:${PATH}" PROVISION_ETC_DIR="${etc}" adopt_seed_hostname construct-seed work-vm)"
ok "...and /etc/hostname really holds it" is "work-vm" "$(cat "${etc}/hostname")"

# ── Everything that must change nothing ───────────────────────────────────────
echo ""
echo "=== The default path is untouched ==="

etc="$(new_etc keep)"
ok "a VM whose hostname is already its own is refused" fails adopt "${etc}" agent-vm work-vm
ok "...and nothing was written" \
  is "127.0.0.1	localhost
127.0.1.1	construct-seed
::1	ip6-localhost ip6-loopback" "$(cat "${etc}/hosts")"

ok "an empty instance name is refused" fails adopt "${etc}" construct-seed ""
ok "the placeholder as a wanted name is refused" fails adopt "${etc}" construct-seed construct-seed
ok "a name with a space is refused" fails adopt "${etc}" construct-seed "work vm"
ok "a name with a dot is refused (a label, not an FQDN)" fails adopt "${etc}" construct-seed work.example.com
ok "a name with an underscore is refused" fails adopt "${etc}" construct-seed work_vm
ok "a leading hyphen is refused" fails adopt "${etc}" construct-seed -work
# shellcheck disable=SC2016  # the point is that it stays literal.
ok "a shell metacharacter is refused" fails adopt "${etc}" construct-seed 'x$(id)'
ok "a slash is refused" fails adopt "${etc}" construct-seed "../../etc/passwd"
ok "an over-long name is refused" fails adopt "${etc}" construct-seed "$(printf 'a%.0s' {1..64})"
ok "63 characters are still allowed" \
  is "$(printf 'a%.0s' {1..63})" "$(adopt "$(new_etc long)" construct-seed "$(printf 'a%.0s' {1..63})")"
ok "nothing was written by any of the refusals" \
  is "127.0.0.1	localhost
127.0.1.1	construct-seed
::1	ip6-localhost ip6-loopback" "$(cat "${etc}/hosts")"

# ── The call site in the shipped script ───────────────────────────────────────
# The greps below match literal $-references in the shipped script.
echo ""
echo "=== The call site ==="

# shellcheck disable=SC2016
ok "the call site is gated on the placeholder hostname" \
  grep -q 'if \[\[ "\${_current_hostname}" == "\${SEED_PLACEHOLDER_HOSTNAME}" \]\]; then' "${PROVISION}"
ok "the placeholder is the one the ISO builder writes" \
  grep -q 'SEED_PLACEHOLDER_HOSTNAME="construct-seed"' "${PROVISION}"
ok "the ISO builder uses the same placeholder" \
  grep -q 'SEED_HOSTNAME="construct-seed"' "${ROOT}/bin/build-autoinstall-iso.sh"
# shellcheck disable=SC2016
ok "it adopts the instance name the host asked for" \
  grep -q 'adopt_seed_hostname "\${_current_hostname}" "\${CONSTRUCT_INSTANCE_NAME}"' "${PROVISION}"
# shellcheck disable=SC2016
ok "a name that cannot be used warns instead of failing the run" \
  grep -q 'warn "hostname is still \${SEED_PLACEHOLDER_HOSTNAME}' "${PROVISION}"

# ── The default path renders the same script as before ────────────────────────
if git -C "${ROOT}" show HEAD:bin/provision.sh >"${tmp}/base_provision.sh" 2>/dev/null; then
  base_count="$(shellcheck -f gcc "${tmp}/base_provision.sh" 2>/dev/null | wc -l)"
  now_count="$(shellcheck -f gcc "${PROVISION}" 2>/dev/null | wc -l)"
  if command -v shellcheck >/dev/null 2>&1; then
    ok "shellcheck: bin/provision.sh has no new diagnostics (${now_count} now, ${base_count} at HEAD)" \
      test "${now_count}" -le "${base_count}"
  fi
fi

echo ""
echo "=== ${pass} passed, ${fail} failed ==="
[[ "${fail}" -eq 0 ]]
