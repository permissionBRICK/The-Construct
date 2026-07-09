#!/usr/bin/env bash
#
# Install the language runtimes/SDKs that the selected project profiles declare,
# directly on the VM host (so claude/codex can build & test in the SSH session).
#
# Reads the merged `.sdks` object from runtime/generated.json (produced by
# generate-runtime-config.sh) and installs the runtimes it recognizes:
#   - node    -> NodeSource apt repo for the requested major version
#   - python  -> python3 + venv + pip (version is best-effort)
#   - dotnet  -> .NET SDK via Microsoft's dotnet-install.sh (channel = version)
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

# First requested version for <key> (handles string or array), else empty.
first_ver() {
  jq -r --arg k "$1" '
    (.sdks // {})[$k] // empty
    | if type == "array" then (.[0] // "") else tostring end
  ' "${GENERATED_JSON}"
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
  apt-get install -y nodejs
}

install_python() {
  local ver="${1:-3}"
  step "Installing Python 3 toolchain (requested: ${ver})"
  apt-get update
  apt-get install -y python3 python3-venv python3-pip
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
  local title="$1" fn="$2" ver="$3" rc
  set +e
  ( set -e; "${fn}" "${ver}" )
  rc=$?
  set -e
  if [[ "${rc}" -ne 0 ]]; then
    warn "${title} failed (exit ${rc}); continuing with the remaining runtimes"
    failed=$((failed + 1))
  fi
}

if want node; then
  installed_any=true
  run_runtime "Node.js runtime installation" install_node "$(first_ver node)"
fi
if want python; then
  installed_any=true
  run_runtime "Python runtime installation" install_python "$(first_ver python)"
fi
if want dotnet; then
  installed_any=true
  run_runtime ".NET runtime installation" install_dotnet "$(first_ver dotnet)"
fi

if [[ "${installed_any}" != "true" ]]; then
  note "No node/python/dotnet runtimes requested by the selected projects."
fi

if [[ "${failed}" -gt 0 ]]; then
  err "${failed} requested runtime(s) failed to install"
  exit 1
fi
