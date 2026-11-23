#!/usr/bin/env bash
# Plain-Bash tests for bin/install-sdks.sh's apt calls and Python versions: apt waits for its
# locks instead of failing when another apt run holds one, every requested Python version is
# installed side by side, python3 stays the distro's, and the python3 link earlier versions made
# is removed. Run: bash test/install-sdks.test.sh
#
# apt-get, apt-cache, dpkg-query, add-apt-repository and the system python are stubs; nothing is
# installed, and the old python3 link lives in the scratch directory.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-sdks.sh"
if [[ "${EUID}" -ne 0 ]]; then
  echo "  SKIP  install-sdks tests need root (the script refuses to run without it)"
  exit 0
fi
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

mkdir -p "${tmp}/stub" "${tmp}/pybin" "${tmp}/sources"
# `update` fails ${LOCKED_UPDATES} times on a held package-list lock (or with ${UPDATE_ERROR}
# instead), then succeeds; `install` succeeds, records its packages as installed and puts each
# python3.X it installs into the packaged interpreters' directory.
cat >"${tmp}/stub/apt-get" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${CALLS}"
if [[ " $* " == *" install "* ]]; then
  for arg in "$@"; do
    case "${arg}" in -*|install|DPkg::*) continue ;; esac
    printf '%s\n' "${arg}" >>"${STATE}"
    if [[ "${arg}" =~ ^python3\.[0-9]+$ ]]; then printf '#!/bin/sh\n' >"${PYBIN}/${arg}"; chmod +x "${PYBIN}/${arg}"; fi
  done
fi
case " $* " in
  *" update "*)
    n=$(( $(cat "${COUNT}" 2>/dev/null || echo 0) + 1 )); echo "${n}" >"${COUNT}"
    if [[ -n "${UPDATE_ERROR:-}" ]]; then echo "${UPDATE_ERROR}" >&2; exit 100; fi
    if (( n <= LOCKED_UPDATES )); then
      # apt translates its messages; only the C locale says "Could not get lock".
      if [[ "${LC_ALL:-}" == C ]]; then
        echo "E: Could not get lock /var/lib/apt/lists/lock. It is held by process 4242 (apt-get)" >&2
      else
        echo "E: Sperre /var/lib/apt/lists/lock konnte nicht erlangt werden. Sie wird von Prozess 4242 (apt-get) gehalten." >&2
      fi
      exit 100
    fi ;;
esac
exit 0
STUB
# Installed: the packages in ${DEBS} and what the apt-get stub installed.
cat >"${tmp}/stub/dpkg-query" <<'STUB'
#!/usr/bin/env bash
pkg="${!#}"
if [[ " ${DEBS:-} " == *" ${pkg} "* ]] || grep -qxF -- "${pkg}" "${STATE}" 2>/dev/null; then printf 'install ok installed'; else exit 1; fi
STUB
# Offered: the packages in ${MIRROR}, and every package once the PPA is configured.
cat >"${tmp}/stub/apt-cache" <<'STUB'
#!/usr/bin/env bash
pkg="${!#}"
printf '%s:\n  Installed: (none)\n' "${pkg}"
if [[ " ${MIRROR:-} " == *" ${pkg} "* ]] || compgen -G "${SOURCES}/deadsnakes*" >/dev/null; then echo '  Candidate: 1.0'; else echo '  Candidate: (none)'; fi
STUB
cat >"${tmp}/stub/add-apt-repository" <<'STUB'
#!/usr/bin/env bash
printf 'add-apt-repository %s\n' "$*" >>"${CALLS}"
printf 'deb https://ppa.example/deadsnakes/ppa/ubuntu noble main\n' >"${SOURCES}/deadsnakes.sources"
STUB
# shellcheck disable=SC2016  # expanded when the stub runs
printf '#!/bin/sh\necho "${DISTRO_PY:-3.12}"\n' >"${tmp}/system-python"
chmod +x "${tmp}/stub/apt-get" "${tmp}/stub/apt-cache" "${tmp}/stub/dpkg-query" "${tmp}/stub/add-apt-repository" "${tmp}/system-python"
printf '{"sdks":{"python":"3"}}\n' >"${tmp}/generated.json"
link="${tmp}/local-bin/python3"

# PATH has no system bin directory with other python3.X in it beyond the distro's own: only the
# stubs and what the apt-get stub installs.
run() {
  rm -f "${tmp}/calls" "${tmp}/count"
  env PATH="${tmp}/stub:${tmp}/pybin:/usr/sbin:/usr/bin:/sbin:/bin" CALLS="${tmp}/calls" COUNT="${tmp}/count" \
    STATE="${tmp}/state" GENERATED_JSON="${tmp}/generated.json" PYBIN="${tmp}/pybin" SOURCES="${tmp}/sources" \
    APT_SOURCES_DIR="${tmp}/sources" SYSTEM_PYTHON="${tmp}/system-python" PYTHON_BIN_DIR="${tmp}/pybin" \
    PYTHON_DEFAULT_LINK="${link}" PYTHON_PPA=ppa:example/deadsnakes APT_LOCK_RETRY_SECONDS=0 "$@" bash "${SCRIPT}" >"${tmp}/out" 2>&1
  echo $? >"${tmp}/rc"
}
rc_is() { [[ "$(cat "${tmp}/rc")" == "$1" ]]; }
updates() { grep -c ' update$' "${tmp}/calls"; }
installs() { grep -c ' install -y python3 python3-venv python3-pip$' "${tmp}/calls"; }
all_wait_for_dpkg() { ! grep -v -e '-o DPkg::Lock::Timeout=900 ' -e '^add-apt-repository ' "${tmp}/calls"; }
sdks() { printf '{"sdks":{"python":%s}}\n' "$1" >"${tmp}/generated.json"; }
reset_python() { rm -rf "${tmp}/pybin" "${tmp}/local-bin" "${tmp}/sources" "${tmp}/state"; mkdir -p "${tmp}/pybin" "${tmp}/sources"; }
called() { grep -qxF -- "$1" "${tmp}/calls"; }
not_called() { ! grep -q -- "$1" "${tmp}/calls" 2>/dev/null; }
no_apt() { [[ ! -s "${tmp}/calls" ]]; }
links_to() { [[ "$(readlink "${link}")" == "$1" ]]; }
gone() { [[ ! -e "${link}" && ! -L "${link}" ]]; }
installed() { mkdir -p "${tmp}/pybin"; for p in "$@"; do printf '%s\n' "${p}" >>"${tmp}/state"; [[ "${p}" =~ ^python3\.[0-9]+$ ]] && printf '#!/bin/sh\n' >"${tmp}/pybin/${p}" && chmod +x "${tmp}/pybin/${p}"; done; return 0; }

# ── apt locks (the distro's python3 toolchain is not installed yet) ──────────
run LOCKED_UPDATES=2
ok "a held package-list lock is waited out" rc_is 0
ok "  update is retried until the lock is free" test "$(updates)" = 3
ok "  then the toolchain is installed once" test "$(installs)" = 1
ok "  every apt-get call waits for the dpkg lock too" all_wait_for_dpkg
ok "  and the wait is reported" grep -q 'apt is busy' "${tmp}/out"

reset_python
run LOCKED_UPDATES=2 LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 LANGUAGE=de
ok "a caller in another language still waits for the lock" rc_is 0
ok "  with the same retries" test "$(updates)" = 3

reset_python
run LOCKED_UPDATES=0 UPDATE_ERROR="E: Failed to fetch http://archive.example/dists/noble/InRelease"
ok "any other apt failure is not retried" test "$(updates)" = 1
ok "  and fails the runtime" rc_is 1
ok "  with apt's own error shown" grep -q 'Failed to fetch' "${tmp}/out"

reset_python
run LOCKED_UPDATES=100 APT_LOCK_TIMEOUT=0
ok "a lock still held when the timeout is over fails the runtime" rc_is 1
ok "  after one attempt with a zero timeout" test "$(updates)" = 1

# ── Python versions (the distro's own python3 3.12 is installed) ─────────────
DEBS="python3 python3-venv python3-pip"; export DEBS

reset_python; sdks '["3.14", "3.11"]'
run LOCKED_UPDATES=0
ok "every requested version is installed" rc_is 0
ok "  from the PPA, added once and without its own apt update" \
  called "add-apt-repository -y -n ppa:example/deadsnakes"
ok "  with their venv packages, in one install" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.14 python3.14-venv python3.11 python3.11-venv"
ok "  the distro's own python3 is there already and is not installed again" not_called " install -y python3 "
ok "  python3 stays the distro's: no python3 link is made" gone
ok "  and the run says so, naming the other versions" \
  grep -q "python3 is the distro's Python 3.12; also installed: python3.14 python3.11" "${tmp}/out"

run LOCKED_UPDATES=0
ok "a second run with everything present runs no apt at all" no_apt
ok "  and makes no python3 link" gone

sdks '["3.14", "3.13", "3.11"]'
run LOCKED_UPDATES=0
ok "a newly requested version is added next to the others" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.13 python3.13-venv"
ok "  without adding the PPA again" not_called add-apt-repository

reset_python; sdks '["3.10"]'
run LOCKED_UPDATES=0
ok "a version older than the distro's is installed too" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.10 python3.10-venv"
ok "  and python3 stays the distro's" gone

reset_python; sdks '["3.13"]'
run LOCKED_UPDATES=0 MIRROR="python3.13 python3.13-venv"
ok "packages a configured mirror offers are installed from it" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.13 python3.13-venv"
ok "  without adding the PPA" not_called add-apt-repository

reset_python; installed python3.14; sdks '["3.14"]'
run LOCKED_UPDATES=0
ok "an interpreter without its venv package gets the venv package" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.14-venv"

# ── the python3 link earlier versions made for every shell ───────────────────
for request in '["3.14"]' '["3.12"]'; do
  reset_python; installed python3.14 python3.14-venv; sdks "${request}"
  mkdir -p "${tmp}/local-bin"; ln -s "${tmp}/pybin/python3.14" "${link}"
  run LOCKED_UPDATES=0
  ok "Construct's old python3 link is removed (request ${request})" gone
  ok "  and the run says so" grep -q "Removed Construct's old link" "${tmp}/out"
  ok "  without failing" rc_is 0
done

reset_python; printf '{"sdks":{}}\n' >"${tmp}/generated.json"
mkdir -p "${tmp}/local-bin"; ln -s "${tmp}/pybin/python3.14" "${link}"
run LOCKED_UPDATES=0
ok "the old link is removed also when no profile requests Python any more" gone
ok "  without installing anything" no_apt

reset_python; sdks '["3.14"]'; mkdir -p "${tmp}/local-bin"; printf '#!/bin/sh\n' >"${link}"
run LOCKED_UPDATES=0
ok "a python3 file someone else put there is left alone" test ! -L "${link}" -a -f "${link}"
ok "  with a warning" grep -q 'was not made by Construct' "${tmp}/out"
ok "  which does not fail the run" rc_is 0

for request in '["3.12"]' '["3.14"]'; do
  reset_python; sdks "${request}"; mkdir -p "${tmp}/local-bin"; ln -s /opt/python/bin/python3.11 "${link}"
  run LOCKED_UPDATES=0
  ok "a link to an interpreter Construct did not install stays (request ${request})" links_to /opt/python/bin/python3.11
  ok "  with a warning" grep -q 'was not made by Construct' "${tmp}/out"
done

reset_python; sdks '["2.7", "3.14.2"]'
run LOCKED_UPDATES=0
ok "a Python 2 request is skipped with a warning" grep -q 'Python 2.7 is not a Python 3 version' "${tmp}/out"
ok "  a patch version installs its minor version" called "-o DPkg::Lock::Timeout=900 install -y python3.14 python3.14-venv"
ok "  says that the patch is not pinned" grep -q 'patch versions are not pinned' "${tmp}/out"
ok "  and python3 stays the distro's" gone

echo ""
echo "  install-sdks tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
