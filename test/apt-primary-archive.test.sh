#!/usr/bin/env bash
# Plain-Bash tests for bin/apt-primary-archive.sh: the installer's GeoIP country mirror (at.archive.ubuntu.com
# answered 404 for noble on 2026-11-27 and failed construct.service at boot) is replaced by archive.ubuntu.com in the
# files apt reads, other sources and the installer's *.orig copies are left alone, a second run changes nothing, and
# bootstrap.sh runs it before its first `apt-get update`.
# Run: bash test/apt-primary-archive.test.sh. Works on a temporary APT_ETC_DIR; /etc/apt is never touched.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/apt-primary-archive.sh"
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

apt="${tmp}/apt"
mkdir -p "${apt}/sources.list.d"
# What a Construct guest installed in Austria carries (home-pc, 2026-11-27).
cat >"${apt}/sources.list.d/ubuntu.sources" <<'EOF'
Types: deb
URIs: http://at.archive.ubuntu.com/ubuntu/
Suites: noble noble-updates noble-backports
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg

Types: deb
URIs: http://security.ubuntu.com/ubuntu/
Suites: noble-security
Components: main restricted universe multiverse
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
EOF
cp "${apt}/sources.list.d/ubuntu.sources" "${apt}/sources.list.d/ubuntu.sources.curtin.orig"
printf 'deb https://de.archive.ubuntu.com/ubuntu jammy main\ndeb http://ppa.launchpadcontent.net/x/ppa/ubuntu noble main\n' \
  >"${apt}/sources.list"
echo 'deb [arch=amd64] https://download.docker.com/linux/ubuntu noble stable' >"${apt}/sources.list.d/docker.list"
cp "${apt}/sources.list.d/docker.list" "${tmp}/docker.list.before"

out="$(APT_ETC_DIR="${apt}" bash "${SCRIPT}" 2>&1)"
rc=$?
ok "exits 0" test "${rc}" -eq 0
ok "deb822 country mirror -> archive.ubuntu.com" \
  grep -qx 'URIs: http://archive.ubuntu.com/ubuntu/' "${apt}/sources.list.d/ubuntu.sources"
ok "security.ubuntu.com kept" grep -qx 'URIs: http://security.ubuntu.com/ubuntu/' "${apt}/sources.list.d/ubuntu.sources"
ok "one-line country mirror -> archive.ubuntu.com, scheme kept" \
  grep -qx 'deb https://archive.ubuntu.com/ubuntu jammy main' "${apt}/sources.list"
ok "PPA line kept" grep -q 'ppa.launchpadcontent.net' "${apt}/sources.list"
ok "third-party list untouched" cmp -s "${tmp}/docker.list.before" "${apt}/sources.list.d/docker.list"
ok "installer *.orig copy untouched" grep -q 'at.archive.ubuntu.com' "${apt}/sources.list.d/ubuntu.sources.curtin.orig"
ok "reports the replaced mirror" grep -q 'instead of at.archive.ubuntu.com' <<<"${out}"

touch -d '2020-01-01' "${apt}/sources.list.d/ubuntu.sources"
before="$(stat -c %Y "${apt}/sources.list.d/ubuntu.sources")"
out="$(APT_ETC_DIR="${apt}" bash "${SCRIPT}" 2>&1)"
ok "second run reports nothing" test -z "${out}"
ok "second run leaves the file alone" test "$(stat -c %Y "${apt}/sources.list.d/ubuntu.sources")" = "${before}"

empty="${tmp}/empty"
mkdir -p "${empty}"
ok "no sources at all is fine" env APT_ETC_DIR="${empty}" bash "${SCRIPT}"

# bootstrap.sh runs it before its first apt-get update.
fix_line="$(grep -n 'apt-primary-archive.sh' "${ROOT}/bootstrap.sh" | head -1 | cut -d: -f1)"
update_line="$(grep -n '^apt_get update' "${ROOT}/bootstrap.sh" | head -1 | cut -d: -f1)"
ok "bootstrap.sh calls it before apt_get update" test -n "${fix_line}" -a -n "${update_line}" -a "${fix_line:-0}" -lt "${update_line:-0}"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
(( fail == 0 ))
