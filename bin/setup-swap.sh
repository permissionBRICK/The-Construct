#!/usr/bin/env bash
# The guest's swap file: /swap.img at the size SWAP_SIZE_GB asks for, with zswap in front.
#
#   setup-swap.sh <auto|0|N>
#
#   auto  half the guest's RAM, rounded to whole GiB, at least 1 and at most 8
#   0     no swap file: /swap.img is switched off and removed
#   N     N GiB (1-128)
#
# /swap.img is the name Ubuntu's installer gives its own swap file, so a guest that already
# has one (an ISO install) gets that file resized instead of a second file. Other swap
# (partitions, zram, other files) is left alone. The file is listed in /etc/fstab so it comes
# back at boot. zswap keeps swapped pages compressed in RAM before they reach the disk; it is
# switched on together with the file and kept on across reboots by a tmpfiles.d entry.
#
# A resize switches the old file off first, which moves its pages back into RAM. When that
# fails (not enough free memory) the old file stays and the next provision tries again. A file
# is only created or grown while the disk keeps at least 2 GiB and 10 % of its size free.
#
# Exit 0 when the swap file is as asked or was deliberately left alone (with a warning);
# 1 when making or switching on the file failed.
#
# Test overrides: CONSTRUCT_SWAP_FILE, CONSTRUCT_FSTAB, CONSTRUCT_MEMINFO, CONSTRUCT_PROC_SWAPS,
# CONSTRUCT_ZSWAP_ENABLED, CONSTRUCT_TMPFILES_DIR, CONSTRUCT_SWAP_UNIT_BYTES (bytes per "GiB"),
# CONSTRUCT_SWAPON, CONSTRUCT_SWAPOFF, CONSTRUCT_MKSWAP, CONSTRUCT_DF.

set -uo pipefail

SWAP_FILE="${CONSTRUCT_SWAP_FILE:-/swap.img}"
FSTAB="${CONSTRUCT_FSTAB:-/etc/fstab}"
MEMINFO="${CONSTRUCT_MEMINFO:-/proc/meminfo}"
PROC_SWAPS="${CONSTRUCT_PROC_SWAPS:-/proc/swaps}"
ZSWAP_ENABLED="${CONSTRUCT_ZSWAP_ENABLED:-/sys/module/zswap/parameters/enabled}"
TMPFILES_CONF="${CONSTRUCT_TMPFILES_DIR:-/etc/tmpfiles.d}/construct-zswap.conf"
UNIT_BYTES="${CONSTRUCT_SWAP_UNIT_BYTES:-1073741824}"
SWAPON="${CONSTRUCT_SWAPON:-swapon}"
SWAPOFF="${CONSTRUCT_SWAPOFF:-swapoff}"
MKSWAP="${CONSTRUCT_MKSWAP:-mkswap}"
DF="${CONSTRUCT_DF:-df}"

AUTO_MAX_GIB=8
MAX_GIB=128
MIN_FREE_KB=2097152   # 2 GiB

warn() { printf 'WARNING: %s\n' "$*"; }

mem_total_kb() { awk '/^MemTotal:/ { print $2; exit }' "${MEMINFO}" 2>/dev/null; }

# auto: half the RAM rounded to whole GiB, clamped to 1..AUTO_MAX_GIB.
auto_gib() {
  local kb gib
  kb="$(mem_total_kb)"
  [[ "${kb}" =~ ^[0-9]+$ ]] || { echo 1; return; }
  gib=$(( (kb / 2 + 524288) / 1048576 ))
  (( gib < 1 )) && gib=1
  (( gib > AUTO_MAX_GIB )) && gib=${AUTO_MAX_GIB}
  echo "${gib}"
}

is_active() { awk -v f="${SWAP_FILE}" '$1 == f { found = 1 } END { exit !found }' "${PROC_SWAPS}" 2>/dev/null; }

file_bytes() { [[ -f "${SWAP_FILE}" ]] && stat -c %s "${SWAP_FILE}" 2>/dev/null || echo 0; }

# Rewrite fstab without our line ($1 = keep|drop decides whether to add it back once).
fstab_set() {
  local want="$1" tmp
  [[ -f "${FSTAB}" ]] || { [[ "${want}" == keep ]] || return 0; : >"${FSTAB}" || return 1; }
  if [[ "${want}" == keep ]] && awk -v f="${SWAP_FILE}" '$1 == f && $3 == "swap" { found = 1 } END { exit !found }' "${FSTAB}"; then
    return 0
  fi
  tmp="$(mktemp)" || return 1
  awk -v f="${SWAP_FILE}" '!($1 == f && $3 == "swap")' "${FSTAB}" >"${tmp}" || { rm -f "${tmp}"; return 1; }
  [[ "${want}" == keep ]] && printf '%s\tnone\tswap\tsw\t0\t0\n' "${SWAP_FILE}" >>"${tmp}"
  cat "${tmp}" >"${FSTAB}" || { rm -f "${tmp}"; return 1; }   # keeps fstab's owner and mode
  rm -f "${tmp}"
}

zswap_on() {
  if [[ ! -e "${ZSWAP_ENABLED}" ]]; then
    echo "zswap: not available in this kernel"
    return 0
  fi
  echo Y >"${ZSWAP_ENABLED}" 2>/dev/null || warn "could not switch zswap on"
  mkdir -p "$(dirname "${TMPFILES_CONF}")" &&
    printf '# Installed by The Construct (bin/setup-swap.sh): zswap in front of %s.\nw /sys/module/zswap/parameters/enabled - - - - Y\n' \
      "${SWAP_FILE}" >"${TMPFILES_CONF}" || warn "could not write ${TMPFILES_CONF}"
}

zswap_off() {
  [[ -f "${TMPFILES_CONF}" ]] || return 0
  rm -f "${TMPFILES_CONF}"
  [[ -e "${ZSWAP_ENABLED}" ]] && echo N >"${ZSWAP_ENABLED}" 2>/dev/null
  return 0
}

# Whether the file system holding the swap file keeps enough free space after it holds
# $1 bytes, counting the $2 bytes of the old file that is removed first.
disk_allows() {
  local need_kb="$(( $1 / 1024 ))" freed_kb="$(( $2 / 1024 ))" line total_kb avail_kb keep_kb
  line="$("${DF}" -P -k "$(dirname "${SWAP_FILE}")" 2>/dev/null | awk 'NR == 2 { print $2, $4 }')"
  read -r total_kb avail_kb <<<"${line}"
  [[ "${total_kb:-}" =~ ^[0-9]+$ && "${avail_kb:-}" =~ ^[0-9]+$ ]] || return 0   # unknown: try anyway
  keep_kb=$(( total_kb / 10 ))
  (( keep_kb < MIN_FREE_KB )) && keep_kb=${MIN_FREE_KB}
  (( avail_kb + freed_kb - need_kb >= keep_kb ))
}

# fallocate, or dd where the file system refuses it, then mkswap and swapon.
make_and_enable() {
  local bytes="$1" how
  for how in fallocate dd; do
    rm -f "${SWAP_FILE}"
    if [[ "${how}" == fallocate ]]; then
      fallocate -l "${bytes}" "${SWAP_FILE}" 2>/dev/null || continue
    else
      dd if=/dev/zero of="${SWAP_FILE}" bs=1M count="$(( bytes / 1048576 ))" status=none 2>/dev/null || continue
      (( bytes % 1048576 == 0 )) || truncate -s "${bytes}" "${SWAP_FILE}" 2>/dev/null || continue
    fi
    chmod 600 "${SWAP_FILE}" &&
      "${MKSWAP}" "${SWAP_FILE}" >/dev/null 2>&1 &&
      "${SWAPON}" "${SWAP_FILE}" 2>/dev/null && return 0
  done
  rm -f "${SWAP_FILE}"
  return 1
}

# ---- size ----------------------------------------------------------------------
requested="${1:-auto}"
case "${requested}" in
  ""|auto) gib="$(auto_gib)"; how_sized="auto: half of the RAM, at most ${AUTO_MAX_GIB} GiB" ;;
  *)
    if [[ "${requested}" =~ ^[0-9]+$ ]] && (( 10#${requested} <= MAX_GIB )); then
      gib=$(( 10#${requested} )); how_sized="set to ${gib} GiB"
    else
      warn "SWAP_SIZE_GB=${requested} is not auto, 0 or a whole number of GiB up to ${MAX_GIB}; using auto"
      gib="$(auto_gib)"; how_sized="auto: half of the RAM, at most ${AUTO_MAX_GIB} GiB"
    fi
    ;;
esac

if [[ ! -r "${PROC_SWAPS}" ]]; then
  echo "swap: this system has no swap support (no ${PROC_SWAPS}); nothing to do"
  exit 0
fi

# ---- off -----------------------------------------------------------------------
if (( gib == 0 )); then
  if is_active && ! "${SWAPOFF}" "${SWAP_FILE}" 2>/dev/null; then
    warn "could not switch ${SWAP_FILE} off (its pages do not fit in free RAM); it stays until the next provision"
    exit 0
  fi
  rm -f "${SWAP_FILE}" || exit 1
  fstab_set drop || exit 1
  zswap_off
  echo "swap file: off (SWAP_SIZE_GB=0)"
  exit 0
fi

# ---- on ------------------------------------------------------------------------
want_bytes=$(( gib * UNIT_BYTES ))
have_bytes="$(file_bytes)"

if (( have_bytes == want_bytes )) && { is_active || "${SWAPON}" "${SWAP_FILE}" 2>/dev/null; }; then
  fstab_set keep || exit 1
  zswap_on
  echo "swap file: ${SWAP_FILE}, ${gib} GiB (${how_sized}), unchanged"
  exit 0
fi

if ! disk_allows "${want_bytes}" "${have_bytes}"; then
  warn "not enough free disk for a ${gib} GiB swap file (the disk keeps at least 2 GiB and 10 % free); ${SWAP_FILE} left as it is"
  exit 0
fi

if is_active && ! "${SWAPOFF}" "${SWAP_FILE}" 2>/dev/null; then
  warn "could not switch the old ${SWAP_FILE} off to resize it (its pages do not fit in free RAM); it stays as it is until the next provision"
  exit 0
fi

if ! make_and_enable "${want_bytes}"; then
  echo "ERROR: could not create and switch on a ${gib} GiB swap file at ${SWAP_FILE}" >&2
  fstab_set drop
  exit 1
fi
fstab_set keep || exit 1
zswap_on
echo "swap file: ${SWAP_FILE}, ${gib} GiB (${how_sized})"
