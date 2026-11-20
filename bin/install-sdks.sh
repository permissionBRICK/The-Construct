#!/usr/bin/env bash
#
# Install the language runtimes/SDKs that the selected project profiles declare,
# directly on the VM host (so claude/codex can build & test in the SSH session).
#
# Reads the merged `.sdks` object from runtime/generated.json (produced by
# generate-runtime-config.sh) and installs the runtimes it recognizes:
#   - node    -> NodeSource apt repo for the HIGHEST requested major version. Only one
#                nodejs deb can be installed at a time and every agent shell shares one
#                PATH, so one version has to win; the highest builds the projects that
#                pinned an older major, the reverse broke them (the generator warns when
#                profiles disagree on the major).
#   - python  -> EVERY requested minor version side by side, like .NET: the distro's own
#                python3 (+ venv, pip) from Ubuntu, any other minor version from the
#                deadsnakes PPA (python3.X + python3.X-venv). The newest requested version
#                becomes `python3` for every shell through /usr/local/bin/python3, which PATH
#                puts ahead of /usr/bin; the distro's own tools call /usr/bin/python3 and keep
#                theirs. A request names a minor version ("3.14"); "3" is the distro's.
#   - dotnet  -> .NET SDKs via Microsoft's dotnet-install.sh, EVERY requested
#                channel/version: SDKs are side-by-side by design in one install dir,
#                and `dotnet` picks per project (global.json, else latest). A project
#                pinning 8.0 in global.json fails on a VM that only has 10.0, so
#                picking one version is not an option here.
#
# Idempotent; safe to re-run. Run as root.
#
set -euo pipefail

# Colourised logging helpers. Emit ANSI colour when either stream is a terminal
# or the caller forces it (the SSH provisioning stream sets FORCE_COLOR/
# CLICOLOR_FORCE, which child processes inherit); otherwise stay plain so
# redirected/piped logs aren't littered with escape codes.
if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_STEP=$'\033[1;36m'   # bold cyan - step headers
  _C_OK=$'\033[32m'       # green     - completion / success
  _C_WARN=$'\033[33m'     # yellow    - warnings (run continues)
  _C_ERR=$'\033[31m'      # red       - fatal errors (before exit)
  _C_DIM=$'\033[2m'       # dim       - idempotent "nothing to do" / detail
  _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_ERR=''; _C_DIM=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n'     "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n'     "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
err()  { printf '%s%s%s\n'     "${_C_ERR}"  "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n'     "${_C_DIM}"  "$*" "${_C_RESET}"; }

AGENT_HOME="${AGENT_HOME:-/opt/construct}"
GENERATED_JSON="${GENERATED_JSON:-${AGENT_HOME}/runtime/generated.json}"

if [[ "${EUID}" -ne 0 ]]; then
  err "Run with sudo: sudo bash ${AGENT_HOME}/repo/bin/install-sdks.sh"
  exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
  err "jq is required (installed by bootstrap.sh)."
  exit 1
fi

if [[ ! -f "${GENERATED_JSON}" ]]; then
  note "No ${GENERATED_JSON}; run generate-runtime-config.sh first. Nothing to install."
  exit 0
fi

# Does the merged config request runtime <key>?
want() { jq -e --arg k "$1" '(.sdks // {}) | has($k)' "${GENERATED_JSON}" >/dev/null 2>&1; }

# Highest requested version for <key> (handles string or array), else empty. The
# generator already orders merged lists highest first, but a generated.json written
# by an older generator sorts lexically ("24" before "26"), so pick the numeric
# maximum here too rather than trusting element 0 — a runtime that satisfies the
# highest request builds every profile, the lowest breaks the newer ones.
pick_ver() {
  jq -r --arg k "$1" '
    def version_rank: tostring | split(".") | map(tonumber? // 0);
    (.sdks // {})[$k] // empty
    | if type == "array" then ((sort_by(version_rank) | last) // "") else tostring end
  ' "${GENERATED_JSON}"
}

# Every requested version for <key>, one per line, highest first (for runtimes that
# install side by side).
all_vers() {
  jq -r --arg k "$1" '
    def version_rank: tostring | split(".") | map(tonumber? // 0);
    (.sdks // {})[$k] // empty
    | if type == "array" then (sort_by(version_rank) | reverse | .[]) else tostring end
  ' "${GENERATED_JSON}"
}

# Another apt run (unattended-upgrades, a parallel project group) may hold apt's locks.
# DPkg::Lock::Timeout makes apt-get wait for the dpkg lock, but `apt-get update` does not
# apply it to the package-list lock, so a run that fails on a held lock is retried until
# the same timeout has passed. Its error output is shown once the attempt has ended. apt runs
# in the C locale so that the lock error is recognized whatever language the caller uses.
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
      note "apt is busy (another apt run holds its lock); retrying in ${APT_LOCK_RETRY_SECONDS} s"
      sleep "${APT_LOCK_RETRY_SECONDS}"
      continue
    fi
    rm -f "${errors}"
    return "${rc}"
  done
}

install_node() {
  local ver="$1" major
  major="${ver%%.*}"; major="${major:-22}"
  if command -v node >/dev/null 2>&1 \
     && [[ "$(node -v 2>/dev/null | sed 's/^v\([0-9]*\).*/\1/')" == "${major}" ]]; then
    note "Node.js ${major}.x already installed"
    return
  fi
  step "Installing Node.js ${major}.x (NodeSource)"
  curl -fsSL "https://deb.nodesource.com/setup_${major}.x" | bash -
  apt_get install -y nodejs
}

SYSTEM_PYTHON="${SYSTEM_PYTHON:-/usr/bin/python3}"
# Where the Debian packages (Ubuntu's and deadsnakes') put their python3.X.
PYTHON_BIN_DIR="${PYTHON_BIN_DIR:-/usr/bin}"
PYTHON_DEFAULT_LINK="${PYTHON_DEFAULT_LINK:-/usr/local/bin/python3}"
PYTHON_PPA="${PYTHON_PPA:-ppa:deadsnakes/ppa}"
APT_SOURCES_DIR="${APT_SOURCES_DIR:-/etc/apt/sources.list.d}"

deb_installed() { [[ "$(dpkg-query -W -f='${Status}' "$1" 2>/dev/null)" == "install ok installed" ]]; }
# Whether the configured repositories (a mirror included) offer a package.
apt_offers() { local c; c="$(LC_ALL=C apt-cache policy "$1" 2>/dev/null | sed -n 's/^ *Candidate: *//p' | head -1)"; [[ -n "${c}" && "${c}" != "(none)" ]]; }
# A link at PYTHON_DEFAULT_LINK to a packaged interpreter (PYTHON_BIN_DIR/python3.X) is
# Construct's to change; anything else there was put by someone else and stays.
python_link_ours() {
  local target
  [[ -L "${PYTHON_DEFAULT_LINK}" ]] || return 1
  target="$(readlink "${PYTHON_DEFAULT_LINK}")"
  [[ "${target%/*}" == "${PYTHON_BIN_DIR}" && "${target##*/}" =~ ^python3\.[0-9]+$ ]]
}

install_python() {
  local requested=("$@") distro v p minor newest="" seen=" " missing=() extra=() updated=false need_ppa=false target
  [[ ${#requested[@]} -gt 0 ]] || requested=(3)
  step "Installing Python (requested: ${requested[*]})"
  for p in python3 python3-venv python3-pip; do deb_installed "${p}" || missing+=("${p}"); done
  if [[ ${#missing[@]} -gt 0 ]]; then
    apt_get update
    updated=true
    apt_get install -y "${missing[@]}"
  fi
  distro="$("${SYSTEM_PYTHON}" -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
  for v in "${requested[@]}"; do
    if [[ ! "${v}" =~ ^3(\.([0-9]+))?(\.[0-9]+)?$ ]]; then
      warn "Python ${v} is not a Python 3 version; skipped"
      continue
    fi
    minor="${BASH_REMATCH[2]:+3.${BASH_REMATCH[2]}}"
    minor="${minor:-${distro}}"
    [[ -z "${BASH_REMATCH[3]}" ]] || warn "Python ${v}: patch versions are not pinned; the newest ${minor}.x is installed"
    if [[ -z "${newest}" || "$(printf '%s\n%s\n' "${newest}" "${minor}" | sort -V | tail -1)" == "${minor}" ]]; then
      newest="${minor}"
    fi
    [[ "${seen}" != *" ${minor} "* ]] || continue
    seen+="${minor} "
    # The distro's own version is the python3 packages above. Any other one needs both its
    # interpreter and its venv package (without it, `python3 -m venv` cannot set up pip).
    local before=${#extra[@]}
    if [[ "${minor}" != "${distro}" ]]; then
      for p in "python${minor}" "python${minor}-venv"; do deb_installed "${p}" || extra+=("${p}"); done
    fi
    (( ${#extra[@]} > before )) || note "Python ${minor} already installed"
  done
  if [[ ${#extra[@]} -gt 0 ]]; then
    [[ "${updated}" == true ]] || apt_get update
    # A configured repository (a mirror) may offer them already; the PPA only for what it does not.
    for p in "${extra[@]}"; do apt_offers "${p}" || need_ppa=true; done
    if [[ "${need_ppa}" == true ]] && ! grep -rqs deadsnakes "${APT_SOURCES_DIR}"; then
      if ! command -v add-apt-repository >/dev/null 2>&1; then
        apt_get install -y software-properties-common
      fi
      # --no-update: the update below waits for apt's locks, add-apt-repository's own does not.
      LC_ALL=C add-apt-repository -y -n "${PYTHON_PPA}"
      apt_get update
    fi
    apt_get install -y "${extra[@]}"
  fi
  [[ -n "${newest}" ]] || return 0
  if [[ "${newest}" == "${distro}" ]]; then
    if python_link_ours; then rm -f "${PYTHON_DEFAULT_LINK}"; fi
    ok "python3 is the distro's Python ${distro}"
    return 0
  fi
  # The packaged interpreter, not whatever python3.X comes first on PATH (a user's shim).
  target="${PYTHON_BIN_DIR}/python${newest}"
  [[ -x "${target}" ]] || { err "${target} is missing after its install"; return 1; }
  if [[ -e "${PYTHON_DEFAULT_LINK}" || -L "${PYTHON_DEFAULT_LINK}" ]] && ! python_link_ours; then
    warn "${PYTHON_DEFAULT_LINK} was not made by Construct; leaving it, so python3 is not switched to ${newest}"
    return 0
  fi
  mkdir -p "$(dirname "${PYTHON_DEFAULT_LINK}")"
  ln -sfn "${target}" "${PYTHON_DEFAULT_LINK}"
  ok "python3 is Python ${newest} (${PYTHON_DEFAULT_LINK} -> ${target})"
}

install_dotnet() {
  local ver="${1:-10.0}" channel="" exact=""
  # dotnet-install.sh distinguishes CHANNELS ("10.0", "LTS") from exact SDK
  # VERSIONS ("10.0.301"): a three-part value passed as --channel fails with
  # "Failed to resolve the exact version number". Normalize: bare major ("10")
  # -> channel "10.0"; two-part -> channel; three-or-more-part -> exact version.
  case "${ver}" in
    *.*.*) exact="${ver}" ;;
    *.*)   channel="${ver}" ;;
    *)     channel="${ver}.0" ;;
  esac
  local major="${ver%%.*}"
  if command -v dotnet >/dev/null 2>&1; then
    if [[ -n "${exact}" ]]; then
      # An exact pin is only satisfied by that exact SDK version.
      if dotnet --list-sdks 2>/dev/null | grep -q "^${exact} "; then
        note ".NET SDK ${exact} already installed"
        return
      fi
    elif dotnet --list-sdks 2>/dev/null | grep -q "^${major}\."; then
      note ".NET SDK ${major}.x already installed"
      return
    fi
  fi
  step "Installing .NET SDK ${exact:+version ${exact}}${channel:+channel ${channel}}"
  local script=/tmp/dotnet-install.sh
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${script}"
  if [[ -n "${exact}" ]]; then
    bash "${script}" --version "${exact}" --install-dir /usr/lib/dotnet
  else
    bash "${script}" --channel "${channel}" --install-dir /usr/lib/dotnet
  fi
  rm -f "${script}"
  ln -sf /usr/lib/dotnet/dotnet /usr/local/bin/dotnet
  cat >/etc/profile.d/dotnet.sh <<'EOF'
export DOTNET_ROOT=/usr/lib/dotnet
case ":$PATH:" in *":/usr/lib/dotnet:"*) ;; *) export PATH="$PATH:/usr/lib/dotnet" ;; esac
EOF
  chmod 0644 /etc/profile.d/dotnet.sh
}

installed_any=false
failed=0
run_runtime() {
  local title="$1" fn="$2" rc
  shift 2
  set +e
  ( set -e; "${fn}" "$@" )
  rc=$?
  set -e
  if [[ "${rc}" -ne 0 ]]; then
    warn "${title} failed (exit ${rc}); continuing with the remaining runtimes"
    failed=$((failed + 1))
  fi
}

if want node; then
  installed_any=true
  run_runtime "Node.js runtime installation" install_node "$(pick_ver node)"
fi
if want python; then
  installed_any=true
  # Side by side as well: every requested version, the newest one as `python3`.
  mapfile -t python_requested < <(all_vers python)
  run_runtime "Python runtime installation" install_python ${python_requested[@]+"${python_requested[@]}"}
fi
if want dotnet; then
  installed_any=true
  # Side-by-side: every requested SDK, so a project whose global.json pins an older
  # channel still builds next to one that needs the newest.
  dotnet_requested="$(all_vers dotnet)"
  if [[ -z "${dotnet_requested}" ]]; then
    run_runtime ".NET runtime installation" install_dotnet ""
  else
    while IFS= read -r dotnet_ver; do
      [[ -n "${dotnet_ver}" ]] || continue
      run_runtime ".NET runtime installation (${dotnet_ver})" install_dotnet "${dotnet_ver}"
    done <<<"${dotnet_requested}"
  fi
fi

if [[ "${installed_any}" != "true" ]]; then
  note "No node/python/dotnet runtimes requested by the selected projects."
fi

if [[ "${failed}" -gt 0 ]]; then
  err "${failed} requested runtime(s) failed to install"
  exit 1
fi
