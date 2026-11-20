#!/usr/bin/env bash
# Regression tests for bin/setup-swap.sh (the guest's /swap.img and zswap) and for how
# bin/provision.sh resolves SWAP_SIZE_GB. Everything runs against files in a temp dir:
# swapon/swapoff/mkswap/df are stubs, /proc/swaps is a plain file the stubs edit, and one
# "GiB" is 1 MiB (CONSTRUCT_SWAP_UNIT_BYTES), so no real swap is touched.
# Run: bash test/setup-swap.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/setup-swap.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

pass=0
fail=0
check() {
  local name="$1"; shift
  if "$@"; then pass=$((pass + 1)); else fail=$((fail + 1)); echo "FAIL: ${name}"; fi
}

MIB=1048576
stubs="${tmp}/stubs"
mkdir -p "${stubs}"
# swapon/swapoff keep the fake /proc/swaps in step and log every call.
cat >"${stubs}/swapon" <<'EOF'
#!/usr/bin/env bash
echo "swapon $*" >>"${STUB_LOG}"
[[ -n "${STUB_SWAPON_FAIL:-}" ]] && exit 1
[[ -f "$1" ]] || exit 1
grep -q "^$1 " "${CONSTRUCT_PROC_SWAPS}" && exit 1
printf '%s file %d 0 -2\n' "$1" "$(( $(stat -c %s "$1") / 1024 ))" >>"${CONSTRUCT_PROC_SWAPS}"
EOF
cat >"${stubs}/swapoff" <<'EOF'
#!/usr/bin/env bash
echo "swapoff $*" >>"${STUB_LOG}"
[[ -n "${STUB_SWAPOFF_FAIL:-}" ]] && exit 1
grep -v "^$1 " "${CONSTRUCT_PROC_SWAPS}" >"${CONSTRUCT_PROC_SWAPS}.new"; mv "${CONSTRUCT_PROC_SWAPS}.new" "${CONSTRUCT_PROC_SWAPS}"
EOF
cat >"${stubs}/mkswap" <<'EOF'
#!/usr/bin/env bash
echo "mkswap $*" >>"${STUB_LOG}"
EOF
# df -P -k: total and available KiB from STUB_DF_TOTAL / STUB_DF_AVAIL.
cat >"${stubs}/df" <<'EOF'
#!/usr/bin/env bash
printf 'Filesystem 1024-blocks Used Available Capacity Mounted\n/dev/sda1 %s 0 %s 1%% /\n' "${STUB_DF_TOTAL}" "${STUB_DF_AVAIL}"
EOF
chmod +x "${stubs}"/*

# A fresh fake machine: $1 = MemTotal in kB.
machine() {
  local m="${tmp}/m$((++machines))"
  mkdir -p "${m}/tmpfiles"
  printf 'MemTotal:       %s kB\nMemFree:        1000 kB\n' "$1" >"${m}/meminfo"
  printf 'Filename\t\t\t\tType\t\tSize\t\tUsed\t\tPriority\n' >"${m}/swaps"
  printf 'UUID=abc / xfs defaults 0 1\n/dev/sdb2 none swap sw 0 0\n' >"${m}/fstab"
  echo N >"${m}/zswap"
  : >"${m}/log"
  export CONSTRUCT_SWAP_FILE="${m}/swap.img" CONSTRUCT_FSTAB="${m}/fstab" CONSTRUCT_MEMINFO="${m}/meminfo"
  export CONSTRUCT_PROC_SWAPS="${m}/swaps" CONSTRUCT_ZSWAP_ENABLED="${m}/zswap" CONSTRUCT_TMPFILES_DIR="${m}/tmpfiles"
  export CONSTRUCT_SWAP_UNIT_BYTES="${MIB}" STUB_LOG="${m}/log"
  export CONSTRUCT_SWAPON="${stubs}/swapon" CONSTRUCT_SWAPOFF="${stubs}/swapoff" CONSTRUCT_MKSWAP="${stubs}/mkswap" CONSTRUCT_DF="${stubs}/df"
  export STUB_DF_TOTAL=104857600 STUB_DF_AVAIL=52428800   # 100 GiB disk, 50 GiB free
  unset STUB_SWAPON_FAIL STUB_SWAPOFF_FAIL
  M="${m}"
}
machines=0
size_units() { [[ -f "${M}/swap.img" ]] && echo $(( $(stat -c %s "${M}/swap.img") / MIB )) || echo none; }
active() { grep -q "^${M}/swap.img " "${M}/swaps"; }
fstab_lines() { grep -c "^${M}/swap.img[[:space:]].*swap" "${M}/fstab"; }
run() { out="$(bash "${SCRIPT}" "$@" 2>&1)"; rc=$?; }

# --- auto on a 12 GiB guest: 6 GiB, active, in fstab once, zswap on ----------------
machine 12246828
run auto
check "auto exits 0" test "${rc}" -eq 0
check "auto on ~11.7 GiB RAM makes 6" test "$(size_units)" = 6
check "auto file is active" active
check "auto adds one fstab line" test "$(fstab_lines)" -eq 1
check "other fstab swap kept" grep -q '^/dev/sdb2 none swap' "${M}/fstab"
check "file mode 600" test "$(stat -c %a "${M}/swap.img")" = 600
check "zswap switched on" test "$(cat "${M}/zswap")" = Y
check "zswap kept on at boot" grep -q '^w /sys/module/zswap/parameters/enabled - - - - Y$' "${M}/tmpfiles/construct-zswap.conf"
check "auto reports the size" grep -q '6 GiB (auto' <<<"${out}"

# --- the same again changes nothing ----------------------------------------------
: >"${M}/log"
run auto
check "rerun exits 0" test "${rc}" -eq 0
check "rerun says unchanged" grep -q unchanged <<<"${out}"
check "rerun neither switches off nor recreates" test ! -s "${M}/log"
check "rerun keeps one fstab line" test "$(fstab_lines)" -eq 1

# --- an explicit size resizes ------------------------------------------------------
run 2
check "resize exits 0" test "${rc}" -eq 0
check "resize to 2" test "$(size_units)" = 2
check "resize switched the old file off" grep -q "^swapoff ${M}/swap.img" "${M}/log"
check "resized file active" active
check "resize keeps one fstab line" test "$(fstab_lines)" -eq 1
check "empty means auto" bash -c 'out="$(bash "$0" "")"; grep -q "6 GiB (auto" <<<"$out"' "${SCRIPT}"

# --- 0 removes file, fstab line and the zswap entry -------------------------------
run 0
check "off exits 0" test "${rc}" -eq 0
check "off removes the file" test "$(size_units)" = none
check "off switches it off" bash -c '! grep -q "swap.img " "$0"' "${M}/swaps"
check "off removes the fstab line" test "$(fstab_lines)" -eq 0
check "off keeps other fstab lines" grep -q '^/dev/sdb2 none swap' "${M}/fstab"
check "off removes the zswap boot entry" test ! -e "${M}/tmpfiles/construct-zswap.conf"
check "off switches zswap off" test "$(cat "${M}/zswap")" = N
run 0
check "off twice is fine" test "${rc}" -eq 0

# --- auto clamps to 1..8 ------------------------------------------------------------
machine 33554432; run auto
check "auto on 32 GiB caps at 8" test "$(size_units)" = 8
machine 1003520; run auto
check "auto on ~1 GiB gives 1" test "$(size_units)" = 1
machine 4012000; run auto
check "auto on ~3.8 GiB gives 2" test "$(size_units)" = 2

# --- invalid input falls back to auto with a warning ------------------------------
machine 12246828; run abc
check "invalid exits 0" test "${rc}" -eq 0
check "invalid warns" grep -q 'WARNING: SWAP_SIZE_GB=abc' <<<"${out}"
check "invalid uses auto" test "$(size_units)" = 6
machine 12246828; run 129
check "above 128 falls back to auto" test "$(size_units)" = 6
machine 12246828; run 008
check "leading zeros are decimal" test "$(size_units)" = 8

# --- the installer's own /swap.img (other size, inactive, already in fstab) is adopted ---
machine 12246828
head -c $((3 * MIB)) /dev/zero >"${M}/swap.img"
printf '%s\tnone\tswap\tsw\t0\t0\n' "${M}/swap.img" >>"${M}/fstab"
run auto
check "adopt exits 0" test "${rc}" -eq 0
check "adopted file resized" test "$(size_units)" = 6
check "adopted file active" active
check "adopted fstab line not duplicated" test "$(fstab_lines)" -eq 1

# --- a right-sized but unusable file is recreated ---------------------------------
machine 12246828
head -c $((6 * MIB)) /dev/zero >"${M}/swap.img"
export STUB_SWAPON_FAIL=1
run auto
check "unusable file and failing swapon exits 1" test "${rc}" -eq 1
check "failed create leaves no file" test "$(size_units)" = none
check "failed create leaves no fstab line" test "$(fstab_lines)" -eq 0
unset STUB_SWAPON_FAIL

# --- too little disk: nothing created, warning, exit 0 ----------------------------
machine 12246828
# 25 GiB disk keeps max(2 GiB, 10 %) = 2621440 KiB free; the file is 6 "GiB" = 6144 KiB here.
export STUB_DF_TOTAL=26214400 STUB_DF_AVAIL=2627000
run auto
check "low disk exits 0" test "${rc}" -eq 0
check "low disk warns" grep -q 'WARNING: not enough free disk' <<<"${out}"
check "low disk creates nothing" test "$(size_units)" = none
export STUB_DF_AVAIL=2627584
run auto
check "enough disk creates the file" test "$(size_units)" = 6
# A full disk still lets the file shrink: that gives disk back.
export STUB_DF_AVAIL=0
run 2
check "full disk shrink exits 0" test "${rc}" -eq 0
check "full disk still shrinks" test "$(size_units)" = 2
check "shrunk file active" active
run 4
check "full disk does not grow" test "$(size_units)" = 2

# --- swapoff refused (pages don't fit in RAM): old file kept --------------------------
machine 12246828
run 4
export STUB_SWAPOFF_FAIL=1
run 2
check "refused swapoff exits 0" test "${rc}" -eq 0
check "refused swapoff warns" grep -q 'WARNING: could not switch the old' <<<"${out}"
check "refused swapoff keeps the old size" test "$(size_units)" = 4
check "refused swapoff keeps it active" active
run 0
check "refused swapoff on 0 keeps the file" test "$(size_units)" = 4
unset STUB_SWAPOFF_FAIL

# --- no zswap in the kernel: swap still made --------------------------------------
machine 12246828
rm -f "${M}/zswap"
run auto
check "no zswap exits 0" test "${rc}" -eq 0
check "no zswap says so" grep -q 'zswap: not available' <<<"${out}"
check "no zswap writes no boot entry" test ! -e "${M}/tmpfiles/construct-zswap.conf"

# --- no /proc/swaps: nothing to do ------------------------------------------------
machine 12246828
rm -f "${M}/swaps"
run auto
check "no swap support exits 0" test "${rc}" -eq 0
check "no swap support creates nothing" test "$(size_units)" = none

# --- provision.sh: env > saved config.env > auto, persisted only when it says something ---
resolve() {   # $1 = env value (or "-" for unset), $2 = saved line (or "")
  local cfg
  cfg="$(mktemp "${tmp}/cfg.XXXXXX")" && rm -f "${cfg}"
  [[ -n "$2" ]] && printf '%s\n' "$2" >"${cfg}"
  # The resolution block, cut out of provision.sh by its markers.
  local block
  block="$(sed -n '/^# >>> SWAP_SIZE_GB resolution/,/^# <<< SWAP_SIZE_GB resolution/p' "${ROOT}/bin/provision.sh")"
  if [[ "$1" == "-" ]]; then
    env -u SWAP_SIZE_GB CONFIG_FILE="${cfg}" bash -c "${block}"$'\necho "${SWAP_SIZE_GB}|${_swap_size_saved}"'
  else
    env SWAP_SIZE_GB="$1" CONFIG_FILE="${cfg}" bash -c "${block}"$'\necho "${SWAP_SIZE_GB}|${_swap_size_saved}"'
  fi
}
check "provision block present" grep -q '^# >>> SWAP_SIZE_GB resolution' "${ROOT}/bin/provision.sh"
check "unset, nothing saved -> auto" test "$(resolve - "")" = "auto|"
check "empty keeps the saved value" test "$(resolve "" "SWAP_SIZE_GB=4")" = "4|4"
check "env wins over saved" test "$(resolve 0 "SWAP_SIZE_GB=4")" = "0|4"
check "env without saved" test "$(resolve 3 "")" = "3|"
check "provision runs the step" grep -q 'bin/setup-swap.sh" "${SWAP_SIZE_GB}"' "${ROOT}/bin/provision.sh"
check "provision persists the setting" grep -q 'cfg SWAP_SIZE_GB "${SWAP_SIZE_GB}"' "${ROOT}/bin/provision.sh"

echo "setup-swap: ${pass} passed, ${fail} failed"
(( fail == 0 ))
