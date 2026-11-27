#!/usr/bin/env bash
# Point the guest's Ubuntu apt sources at the primary archive (archive.ubuntu.com) instead of a country mirror.
#
# The Ubuntu installer picks a country mirror by GeoIP (at.archive.ubuntu.com for a VM in Austria), and Construct's
# autoinstall config never overrides it. Country mirrors are run by third parties and can break on their own: on
# 2026-11-27 at.archive.ubuntu.com answered 404 for the noble Release files, so every `apt-get update` failed and with
# it construct.service at boot and every provision step that installs a package, while archive.ubuntu.com was fine.
#
# Only <cc>.archive.ubuntu.com hosts are rewritten, keeping scheme and path; security.ubuntu.com, ports.ubuntu.com,
# PPAs and third-party repos are left alone. Only the files apt reads are touched (sources.list and *.list /
# *.sources in sources.list.d), so the installer's *.orig copies stay as they were. Files that already point at the
# primary archive are not rewritten, so a re-provision changes nothing. APT_ETC_DIR overrides /etc/apt for tests.
set -euo pipefail

APT_ETC_DIR="${APT_ETC_DIR:-/etc/apt}"
COUNTRY_MIRROR_RE='(https?://)[a-z]{2,3}\.archive\.ubuntu\.com/'

shopt -s nullglob
for f in "${APT_ETC_DIR}/sources.list" "${APT_ETC_DIR}"/sources.list.d/*.list "${APT_ETC_DIR}"/sources.list.d/*.sources; do
  [[ -f "${f}" ]] || continue
  grep -Eq "${COUNTRY_MIRROR_RE}" "${f}" || continue
  mirrors="$(grep -Eo "${COUNTRY_MIRROR_RE}" "${f}" | sed -E 's#^https?://##; s#/$##' | sort -u | paste -sd, -)"
  sed -i -E "s#${COUNTRY_MIRROR_RE}#\\1archive.ubuntu.com/#g" "${f}"
  echo "apt: ${f} now uses archive.ubuntu.com instead of ${mirrors}"
done
