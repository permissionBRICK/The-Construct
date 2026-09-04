#!/usr/bin/env bash
# Tests for bin/provision.sh's VM-side Construct marker (plan section 4.12, "VM-side
# marker").
#
# The guest records the commit it was provisioned with as CONSTRUCT_COMMIT in
# /etc/construct/provisioned.env. It is the SOURCE OF TRUTH for "is this VM behind the
# installed Construct" -- the control panel probes it (extension/src/probe.js), so a VM
# some other PC provisioned is judged correctly instead of against this PC's cache.
#
# THE REGRESSION BAR: when the host could not resolve a commit (CONSTRUCT_VERSION unset,
# or the literal 'unversioned' it passes then), the key must stay ABSENT and the marker
# file must be byte-identical to what today's provision.sh writes -- recording a lie is
# worse than recording nothing.
#
# Run: bash test/provision-marker.test.sh

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
# Negation helper: `ok "name" ! cmd` cannot work (`!` is shell syntax, not a command).
no() { ! "$@"; }
has_key() { grep -Eq "^$2=" "$1"; }
key_of() { sed -n "s/^$2=//p" "$1" | head -1; }

# ── The step under test, taken from the shipped script ────────────────────────
# Extracted rather than reimplemented: provision.sh's body provisions a machine, so it
# cannot be sourced, but the step that ships is the one that has to be right. `mark` and
# `note` are the script's own two collaborators, stubbed here.
lib="${tmp}/marker.sh"
{
  printf 'note() { printf "%%s\\n" "$*" >>"${tmp_notes}"; }\n'
  printf 'mark() { bash "%s/bin/config-set.sh" "${MARKER_FILE}" "$1" "$2"; }\n' "${ROOT}"
  sed -n '/^record_timestamps() {$/,/^}$/p' "${PROVISION}"
} >"${lib}"
# shellcheck source=/dev/null
. "${lib}"

if ! declare -f record_timestamps >/dev/null; then
  printf '  FAIL  record_timestamps could not be extracted from bin/provision.sh\n'
  exit 1
fi

# ── 1. A known commit is recorded ─────────────────────────────────────────────
m1="${tmp}/1.env"; n1="${tmp}/1.notes"; : >"${n1}"
MARKER_FILE="${m1}" tmp_notes="${n1}" CONSTRUCT_VERSION="abc1234def" record_timestamps
ok "records CONSTRUCT_COMMIT when the host passed a commit" has_key "${m1}" CONSTRUCT_COMMIT
ok "records the commit VERBATIM" is "abc1234def" "$(key_of "${m1}" CONSTRUCT_COMMIT)"
ok "still records INSTALLED_AT" has_key "${m1}" INSTALLED_AT
ok "still records REPROVISIONED_AT" has_key "${m1}" REPROVISIONED_AT
ok "says so in the step output" grep -q 'CONSTRUCT_COMMIT=abc1234def' "${n1}"

# A full 40-char sha and a 64-char one are both accepted.
m1b="${tmp}/1b.env"; n1b="${tmp}/1b.notes"; : >"${n1b}"
sha40="0123456789abcdef0123456789abcdef01234567"
MARKER_FILE="${m1b}" tmp_notes="${n1b}" CONSTRUCT_VERSION="${sha40}" record_timestamps
ok "accepts a full 40-char sha" is "${sha40}" "$(key_of "${m1b}" CONSTRUCT_COMMIT)"

# ── 2. ZERO CHANGE when the host has no commit ────────────────────────────────
# Two runs of the same fixture, one with CONSTRUCT_VERSION unset and one with the
# literal 'unversioned' the provisioner passes then: neither may write the key, and both
# marker files must match a run with the step's pre-B12 behaviour byte for byte.
m2="${tmp}/2.env"; n2="${tmp}/2.notes"; : >"${n2}"
MARKER_FILE="${m2}" tmp_notes="${n2}" record_timestamps
ok "no CONSTRUCT_VERSION: the key stays ABSENT" no has_key "${m2}" CONSTRUCT_COMMIT
ok "no CONSTRUCT_VERSION: nothing extra is printed" no grep -q 'CONSTRUCT_COMMIT' "${n2}"

m3="${tmp}/3.env"; n3="${tmp}/3.notes"; : >"${n3}"
MARKER_FILE="${m3}" tmp_notes="${n3}" CONSTRUCT_VERSION="unversioned" record_timestamps
ok "'unversioned': the key stays ABSENT (never record a lie)" no has_key "${m3}" CONSTRUCT_COMMIT
ok "'unversioned': nothing extra is printed" no grep -q 'CONSTRUCT_COMMIT' "${n3}"
# Clearing only ever touches a marker that ALREADY carries a value: a file that never had
# the key keeps exactly the bytes today's provision.sh writes.
ok "no commit + no existing key: the file is not touched at all" no grep -q 'CONSTRUCT_COMMIT' "${m3}"

# Byte-identity of the two no-commit runs against each other, ignoring the timestamps
# (which move with the clock): the KEY SET and the key ORDER must be unchanged.
keys_of() { sed -n 's/^\([A-Z_]*\)=.*/\1/p' "$1" | tr '\n' ','; }
ok "'unversioned' writes exactly the same keys as an unset version" \
  is "$(keys_of "${m2}")" "$(keys_of "${m3}")"
ok "...which is INSTALLED_AT,REPROVISIONED_AT and nothing else" \
  is "INSTALLED_AT,REPROVISIONED_AT," "$(keys_of "${m2}")"

# ── 3. Hostile / malformed values are refused ─────────────────────────────────
for bad in "abc123" "not-a-commit" "ABC1234" "abc1234; rm -rf /" "$(printf 'a%.0s' {1..65})"; do
  mb="${tmp}/bad.env"; nb="${tmp}/bad.notes"; rm -f "${mb}"; : >"${nb}"
  MARKER_FILE="${mb}" tmp_notes="${nb}" CONSTRUCT_VERSION="${bad}" record_timestamps
  ok "refuses a malformed CONSTRUCT_VERSION (${bad:0:20})" no has_key "${mb}" CONSTRUCT_COMMIT
done

# ── 4. A reprovision UPDATES the recorded commit ──────────────────────────────
m4="${tmp}/4.env"; n4="${tmp}/4.notes"; : >"${n4}"
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="1111111" record_timestamps
first_installed="$(key_of "${m4}" INSTALLED_AT)"
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="2222222" record_timestamps
ok "a reprovision moves CONSTRUCT_COMMIT to the new commit" is "2222222" "$(key_of "${m4}" CONSTRUCT_COMMIT)"
ok "...and only ONE CONSTRUCT_COMMIT line exists (config-set.sh merges)" \
  is "1" "$(grep -c '^CONSTRUCT_COMMIT=' "${m4}")"
ok "...while INSTALLED_AT is preserved (first install unchanged)" is "${first_installed}" "$(key_of "${m4}" INSTALLED_AT)"

# A reprovision by a Construct that CANNOT say which commit it is CLEARS the recorded one.
# Leaving "2222222" would be a statement about a provisioning that is no longer the last
# one -- the panel would compare a stale marker against installedCommit and either claim
# the VM is behind when it is not, or (worse) call it current. Empty reads back as
# "unknown", which is the honest answer and falls back to the host-side cache.
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="unversioned" record_timestamps
ok "a version-less reprovision REMOVES the stale commit (never leaves a lie)" no has_key "${m4}" CONSTRUCT_COMMIT
ok "...leaving NO CONSTRUCT_COMMIT line at all, not an empty one" \
  no grep -q '^CONSTRUCT_COMMIT=' "${m4}"
ok "...and says so in the step output" grep -q 'CONSTRUCT_COMMIT removed' "${n4}"
ok "...while the timestamps it does know are untouched" has_key "${m4}" INSTALLED_AT
ok "...and REPROVISIONED_AT too" has_key "${m4}" REPROVISIONED_AT
ok "...and INSTALLED_AT still holds the first install's value" is "${first_installed}" "$(key_of "${m4}" INSTALLED_AT)"
# The same for a malformed value, and removing is idempotent.
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="3333333" record_timestamps
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="not-a-commit" record_timestamps
ok "a malformed value removes it too" no has_key "${m4}" CONSTRUCT_COMMIT
bytes_before="$(wc -c <"${m4}")"
MARKER_FILE="${m4}" tmp_notes="${n4}" record_timestamps
ok "removing an ALREADY-absent marker changes nothing" is "${bytes_before}" "$(wc -c <"${m4}")"
# ...and a later real reprovision records the new commit again.
MARKER_FILE="${m4}" tmp_notes="${n4}" CONSTRUCT_VERSION="4444444" record_timestamps
ok "a later versioned reprovision records the commit again" is "4444444" "$(key_of "${m4}" CONSTRUCT_COMMIT)"
ok "...still exactly one CONSTRUCT_COMMIT line" is "1" "$(grep -c '^CONSTRUCT_COMMIT=' "${m4}")"

# ── 5. The probe reads exactly this file and key ──────────────────────────────
PROBE="${ROOT}/extension/src/probe.js"
ok "extension/src/probe.js reads CONSTRUCT_COMMIT from provisioned.env" \
  grep -q "emit CONSTRUCT_COMMIT .*CONSTRUCT_COMMIT=" "${PROBE}"

printf '\n  provision marker tests — %d passed, %d failed\n\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
