#!/usr/bin/env bash
# Plain-Bash regression tests for the opencode installer helpers in
# bin/install-ai-tools.sh. Run: bash test/opencode-install.test.sh
#
# The failure these guard against: the official opencode installer resolves its
# version through api.github.com, which is rate-limited to 60 requests/hour per
# SOURCE IP. Behind a corporate NAT that budget is shared by every machine on the
# network, so the installer reliably dies with "Failed to fetch version
# information" and all three retries hit the same wall. We therefore resolve the
# version from the releases/latest REDIRECT (github.com, no API quota), pin it
# via the installer's VERSION env var, and fall back to the npm package when
# GitHub is unreachable altogether.
#
# Everything here runs against stubbed curl/npm/bash -- no network, no installs.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
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

# A stub bin dir shadowing the real curl/npm/bash. Each stub records its argv (and
# the env vars we care about) into ${tmp}/<name>.log and returns ${name}_RC.
make_stubs() {
  local dir="${tmp}/bin"
  rm -rf "${dir}"
  mkdir -p "${dir}"

  cat >"${dir}/curl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_LOG_DIR}/curl.log"
case "$*" in
  # HEAD on releases/latest -> what the real redirect_url writer prints
  *releases/latest*) printf '%s' "${STUB_REDIRECT_URL}"; exit "${STUB_REDIRECT_RC:-0}" ;;
  # The piped installer body: emit a script the caller's bash will run.
  *opencode.ai/install*)
    [[ "${STUB_INSTALL_FETCH_RC:-0}" == "0" ]] || exit "${STUB_INSTALL_FETCH_RC}"
    printf 'printf "%%s\\n" "VERSION=${VERSION:-}" >>"%s/installer.log"\nexit ${STUB_INSTALLER_RC:-0}\n' "${STUB_LOG_DIR}"
    exit 0 ;;
  *) exit "${STUB_CURL_RC:-0}" ;;
esac
EOF

  cat >"${dir}/npm" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_LOG_DIR}/npm.log"
exit "${STUB_NPM_RC:-0}"
EOF

  chmod +x "${dir}/curl" "${dir}/npm"
  printf '%s' "${dir}"
}

# Source the helpers (installs nothing) and run one expression against the stubs.
run_case() {
  local name="$1" body="$2"
  shift 2
  rm -f "${tmp}/curl.log" "${tmp}/npm.log" "${tmp}/installer.log"
  local stub_dir
  stub_dir="$(make_stubs)"
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" CASE_BODY="${body}" \
    STUB_LOG_DIR="${tmp}" \
    env "$@" PATH="${stub_dir}:${PATH}" \
    bash -c 'source "${SCRIPT_PATH}"; eval "${CASE_BODY}"' \
    >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

# ── opencode_latest_version: parse the releases/latest redirect ───────────────
run_case ver 'opencode_latest_version' \
  STUB_REDIRECT_URL=https://github.com/anomalyco/opencode/releases/tag/v1.18.18
ok "version comes from the redirect target" grep -qx '1.18.18' "${tmp}/ver.out"
ok "the rate-limited GitHub API is never queried" sh -c "! grep -q 'api.github.com' '${tmp}/curl.log'"

run_case ver_empty 'opencode_latest_version' STUB_REDIRECT_URL=""
ok "an unresolvable redirect yields no version" test ! -s "${tmp}/ver_empty.out"

run_case ver_junk 'opencode_latest_version' STUB_REDIRECT_URL="https://example.invalid/blocked-by-proxy"
ok "a proxy interception page yields no version" test ! -s "${tmp}/ver_junk.out"

# ── opencode_official_installer: the version is pinned via VERSION= ───────────
run_case pin 'opencode_official_installer 1.2.3'
ok "a known version is pinned for the installer" grep -qx 'VERSION=1.2.3' "${tmp}/installer.log"

run_case nopin 'opencode_official_installer'
ok "an unknown version leaves the installer to decide" grep -qx 'VERSION=' "${tmp}/installer.log"

# ── opencode_npm_fallback: the same release, from the npm registry ────────────
run_case npm_pin 'opencode_npm_fallback 1.2.3'
ok "the npm fallback installs the pinned version" grep -q 'install -g opencode-ai@1.2.3' "${tmp}/npm.log"
ok "the npm fallback allows the postinstall script" grep -q -- '--allow-scripts=opencode-ai' "${tmp}/npm.log"
ok "the npm fallback succeeds when npm does" test "$(cat "${tmp}/npm_pin.rc")" = 0

run_case npm_latest 'opencode_npm_fallback'
ok "without a version the npm fallback takes latest" grep -q 'install -g opencode-ai@latest' "${tmp}/npm.log"

run_case npm_fail 'opencode_npm_fallback 1.2.3' STUB_NPM_RC=1
ok "a failing npm is reported as a failure" test "$(cat "${tmp}/npm_fail.rc")" = 1

# ── run_installer_with_retries: retries pass the version through ─────────────
run_case retry 'run_installer_with_retries opencode opencode_official_installer 9.9.9' STUB_INSTALLER_RC=1
ok "a failing installer is retried three times" test "$(grep -c . "${tmp}/installer.log")" = 3
ok "every retry keeps the pinned version" test "$(grep -cx 'VERSION=9.9.9' "${tmp}/installer.log")" = 3
ok "exhausted retries fail" test "$(cat "${tmp}/retry.rc")" = 1

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
