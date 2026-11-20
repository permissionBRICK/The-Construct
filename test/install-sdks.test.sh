#!/usr/bin/env bash
# Plain-Bash tests for bin/install-sdks.sh's apt calls and Python versions: apt waits for its
# locks instead of failing when another apt run holds one, every requested Python version is
# installed side by side, and the newest one becomes python3. Run: bash test/install-sdks.test.sh
#
# apt-get, dpkg-query, add-apt-repository and the system python are stubs; nothing is installed,
# and the python3 link is made in the scratch directory.

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
# instead), then succeeds; `install` succeeds and puts each python3.X it installs on PATH.
cat >"${tmp}/stub/apt-get" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${CALLS}"
for arg in "$@"; do
  if [[ "${arg}" =~ ^python3\.[0-9]+$ ]]; then printf '#!/bin/sh\n' >"${PYBIN}/${arg}"; chmod +x "${PYBIN}/${arg}"; fi
done
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
# The Debian packages listed in ${DEBS} count as installed.
cat >"${tmp}/stub/dpkg-query" <<'STUB'
#!/usr/bin/env bash
[[ " ${DEBS:-} " == *" ${!#} "* ]] || exit 1
printf 'install ok installed'
STUB
cat >"${tmp}/stub/add-apt-repository" <<'STUB'
#!/usr/bin/env bash
printf 'add-apt-repository %s\n' "$*" >>"${CALLS}"
printf 'deb https://ppa.example/deadsnakes/ppa/ubuntu noble main\n' >"${SOURCES}/deadsnakes.sources"
STUB
# shellcheck disable=SC2016  # expanded when the stub runs
printf '#!/bin/sh\necho "${DISTRO_PY:-3.12}"\n' >"${tmp}/system-python"
chmod +x "${tmp}/stub/apt-get" "${tmp}/stub/dpkg-query" "${tmp}/stub/add-apt-repository" "${tmp}/system-python"
printf '{"sdks":{"python":"3"}}\n' >"${tmp}/generated.json"
link="${tmp}/local-bin/python3"

# PATH has no system bin directory with other python3.X in it beyond the distro's own: only the
# stubs and what the apt-get stub installs.
run() {
  rm -f "${tmp}/calls" "${tmp}/count"
  env PATH="${tmp}/stub:${tmp}/pybin:/usr/sbin:/usr/bin:/sbin:/bin" CALLS="${tmp}/calls" COUNT="${tmp}/count" \
    GENERATED_JSON="${tmp}/generated.json" PYBIN="${tmp}/pybin" SOURCES="${tmp}/sources" APT_SOURCES_DIR="${tmp}/sources" \
    SYSTEM_PYTHON="${tmp}/system-python" PYTHON_DEFAULT_LINK="${link}" PYTHON_PPA=ppa:example/deadsnakes \
    APT_LOCK_RETRY_SECONDS=0 "$@" bash "${SCRIPT}" >"${tmp}/out" 2>&1
  echo $? >"${tmp}/rc"
}
rc_is() { [[ "$(cat "${tmp}/rc")" == "$1" ]]; }
updates() { grep -c ' update$' "${tmp}/calls"; }
installs() { grep -c ' install -y python3 python3-venv python3-pip$' "${tmp}/calls"; }
all_wait_for_dpkg() { ! grep -v -e '-o DPkg::Lock::Timeout=900 ' -e '^add-apt-repository ' "${tmp}/calls"; }
sdks() { printf '{"sdks":{"python":%s}}\n' "$1" >"${tmp}/generated.json"; }
reset_python() { rm -rf "${tmp}/pybin" "${tmp}/local-bin" "${tmp}/sources"; mkdir -p "${tmp}/pybin" "${tmp}/sources"; }
called() { grep -qxF -- "$1" "${tmp}/calls"; }
not_called() { ! grep -q -- "$1" "${tmp}/calls" 2>/dev/null; }
no_apt() { [[ ! -s "${tmp}/calls" ]]; }
links_to() { [[ "$(readlink "${link}")" == "${tmp}/pybin/$1" ]]; }
not_a_link() { [[ ! -L "${link}" ]]; }
gone() { [[ ! -e "${link}" && ! -L "${link}" ]]; }

# ── apt locks (the distro's python3 toolchain is not installed yet) ──────────
run LOCKED_UPDATES=2
ok "a held package-list lock is waited out" rc_is 0
ok "  update is retried until the lock is free" test "$(updates)" = 3
ok "  then the toolchain is installed once" test "$(installs)" = 1
ok "  every apt-get call waits for the dpkg lock too" all_wait_for_dpkg
ok "  and the wait is reported" grep -q 'apt is busy' "${tmp}/out"

run LOCKED_UPDATES=2 LANG=de_DE.UTF-8 LC_ALL=de_DE.UTF-8 LANGUAGE=de
ok "a caller in another language still waits for the lock" rc_is 0
ok "  with the same retries" test "$(updates)" = 3

run LOCKED_UPDATES=0 UPDATE_ERROR="E: Failed to fetch http://archive.example/dists/noble/InRelease"
ok "any other apt failure is not retried" test "$(updates)" = 1
ok "  and fails the runtime" rc_is 1
ok "  with apt's own error shown" grep -q 'Failed to fetch' "${tmp}/out"

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
ok "  python3 is the newest requested version" links_to python3.14
ok "  and the run says so" grep -q 'python3 is Python 3.14' "${tmp}/out"

run LOCKED_UPDATES=0
ok "a second run with everything present runs no apt at all" no_apt
ok "  and keeps python3 on the newest" links_to python3.14

sdks '["3.14", "3.13", "3.11"]'
run LOCKED_UPDATES=0
ok "a newly requested version is added next to the others" \
  called "-o DPkg::Lock::Timeout=900 install -y python3.13 python3.13-venv"
ok "  without adding the PPA again" not_called add-apt-repository

sdks '["3.12"]'
run LOCKED_UPDATES=0
ok "when the newest request is the distro's own version" rc_is 0
ok "  Construct's python3 link is removed again" gone

reset_python; sdks '["3.10"]'
run LOCKED_UPDATES=0
ok "the newest request wins also when it is older than the distro's" links_to python3.10

reset_python; sdks '["3.14"]'; mkdir -p "${tmp}/local-bin"; printf '#!/bin/sh\n' >"${link}"
run LOCKED_UPDATES=0
ok "a python3 someone else put there is left alone" not_a_link
ok "  with a warning" grep -q 'was not made by Construct' "${tmp}/out"

reset_python; sdks '["2.7", "3.14.2"]'
run LOCKED_UPDATES=0
ok "a Python 2 request is skipped with a warning" grep -q 'Python 2.7 is not a Python 3 version' "${tmp}/out"
ok "  a patch version installs its minor version" called "-o DPkg::Lock::Timeout=900 install -y python3.14 python3.14-venv"
ok "  says that the patch is not pinned" grep -q 'patch versions are not pinned' "${tmp}/out"
ok "  and python3 is that minor version" links_to python3.14

echo ""
echo "  install-sdks tests - ${pass}/$((pass + fail)) passed"
[[ "${fail}" -eq 0 ]]
