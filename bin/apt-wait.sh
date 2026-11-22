#!/usr/bin/env bash
# Sourced by the guest's install scripts: `apt_get` runs apt-get and waits out apt's locks.
#
# Another apt run may hold apt's locks: Ubuntu's apt-daily timer shortly after a boot (FatPC 2026-11-22: it held the
# package-list lock for five seconds and a provision step failed at once), unattended-upgrades, a parallel project
# group. DPkg::Lock::Timeout makes apt-get wait for the dpkg lock, but `apt-get update` does not apply it to the
# package-list lock, so a run that fails on a held lock is retried until the same timeout has passed. Its error
# output is shown once the attempt has ended. apt runs in the C locale so that the lock error is recognized whatever
# language the caller uses.
APT_LOCK_TIMEOUT="${APT_LOCK_TIMEOUT:-900}"
APT_LOCK_RETRY_SECONDS="${APT_LOCK_RETRY_SECONDS:-5}"
apt_get() {
  local deadline=$((SECONDS + APT_LOCK_TIMEOUT)) errors rc
  errors="$(mktemp)"
  while :; do
    rc=0
    LC_ALL=C apt-get -o DPkg::Lock::Timeout="${APT_LOCK_TIMEOUT}" "$@" 2>"${errors}" || rc=$?
    cat "${errors}" >&2
    if (( rc != 0 )) && grep -q 'Could not get lock' "${errors}" && (( SECONDS < deadline )); then
      echo "apt is busy (another apt run holds its lock); retrying in ${APT_LOCK_RETRY_SECONDS} s" >&2
      sleep "${APT_LOCK_RETRY_SECONDS}"
      continue
    fi
    rm -f "${errors}"
    return "${rc}"
  done
}
