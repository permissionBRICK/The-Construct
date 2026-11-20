#!/usr/bin/env bash
# Plain-Bash regression tests for the opencode installer helpers in
# bin/install-ai-tools.sh. Run: bash test/opencode-install.test.sh
#
# OpenCode 2 ships as the npm package @opencode/cli. Its installer
# (opencode.ai/v2/install) downloads that package's platform build from the npm
# registry, so the registry's latest tag is the version to pin (via the
# installer's VERSION env var), with npm itself as the fallback. A VM that ran
# OpenCode 1 keeps one copy of its data folder before the first OpenCode 2 start,
# because OpenCode 2 converts the database in place.
#
# Everything here runs against stubbed curl/npm/opencode/systemctl -- no network, no
# installs, and never the machine's own services.

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

# A stub bin dir shadowing the real curl/npm/opencode/systemctl. Each stub records its argv
# (and the env vars we care about) into ${tmp}/<name>.log and returns ${name}_RC.
make_stubs() {
  local dir="${tmp}/bin"
  rm -rf "${dir}"
  mkdir -p "${dir}"

  cat >"${dir}/curl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_LOG_DIR}/curl.log"
case "$*" in
  # The npm registry's latest document for @opencode/cli
  *registry.npmjs.org/@opencode%2fcli/latest*)
    [[ "${STUB_REGISTRY_RC:-0}" == "0" ]] || exit "${STUB_REGISTRY_RC}"
    printf '%s' "${STUB_REGISTRY_BODY:-}"; exit 0 ;;
  # The piped installer body: emit a script the caller's bash will run.
  *opencode.ai/v2/install*)
    [[ "${STUB_INSTALL_FETCH_RC:-0}" == "0" ]] || exit "${STUB_INSTALL_FETCH_RC}"
    printf 'printf "%%s\\n" "VERSION=${VERSION:-}" >>"%s/installer.log"\nexit ${STUB_INSTALLER_RC:-0}\n' "${STUB_LOG_DIR}"
    exit 0 ;;
  *) exit "${STUB_CURL_RC:-0}" ;;
esac
EOF

  cat >"${dir}/npm" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_LOG_DIR}/npm.log"
case "$*" in
  "ls -g --depth=0 opencode-ai") exit "${STUB_NPM_HAS_V1:-1}" ;;
  "uninstall -g opencode-ai") exit "${STUB_NPM_UNINSTALL_RC:-0}" ;;
esac
exit "${STUB_NPM_RC:-0}"
EOF

  cat >"${dir}/opencode" <<'EOF'
#!/usr/bin/env bash
[[ "$*" == "--version" ]] && printf '%s\n' "${STUB_OPENCODE_VERSION:-}"
EOF

  # systemctl: opencode-serve is active only when the case says so.
  cat >"${dir}/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${STUB_LOG_DIR}/systemctl.log"
case "$*" in
  *is-active*) exit "${STUB_SERVE_ACTIVE:-3}" ;;
esac
exit 0
EOF

  chmod +x "${dir}/curl" "${dir}/npm" "${dir}/opencode" "${dir}/systemctl"
  printf '%s' "${dir}"
}

# Source the helpers (installs nothing) and run one expression against the stubs.
run_case() {
  local name="$1" body="$2"
  shift 2
  rm -f "${tmp}/curl.log" "${tmp}/npm.log" "${tmp}/installer.log" "${tmp}/systemctl.log"
  local stub_dir
  stub_dir="$(make_stubs)"
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" CASE_BODY="${body}" \
    STUB_LOG_DIR="${tmp}" \
    env "$@" PATH="${stub_dir}:${PATH}" \
    bash -c 'source "${SCRIPT_PATH}"; eval "${CASE_BODY}"' \
    >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

# ── opencode_latest_version: the npm registry's latest @opencode/cli ──────────
run_case ver 'opencode_latest_version' \
  STUB_REGISTRY_BODY='{"name":"@opencode/cli","version":"2.3.2","_nodeVersion":"24.1.0"}'
ok "version comes from the npm registry's latest tag" grep -qx '2.3.2' "${tmp}/ver.out"
ok "GitHub is never queried" sh -c "! grep -q 'github.com' '${tmp}/curl.log'"

run_case ver_down 'opencode_latest_version' STUB_REGISTRY_RC=22
ok "an unreachable registry yields no version" test ! -s "${tmp}/ver_down.out"

run_case ver_junk 'opencode_latest_version' STUB_REGISTRY_BODY='<html>blocked by proxy</html>'
ok "a proxy interception page yields no version" test ! -s "${tmp}/ver_junk.out"

# ── opencode_installed_version: both generations' --version output ────────────
run_case inst_v2 'opencode_installed_version' STUB_OPENCODE_VERSION='opencode v2.3.2'
ok "OpenCode 2's 'opencode v2.3.2' reads as 2.3.2" grep -qx '2.3.2' "${tmp}/inst_v2.out"
run_case inst_v1 'opencode_installed_version' STUB_OPENCODE_VERSION='1.19.6'
ok "OpenCode 1's '1.19.6' reads as 1.19.6" grep -qx '1.19.6' "${tmp}/inst_v1.out"

# ── opencode_official_installer: OpenCode 2's installer, version pinned ───────
run_case pin 'opencode_official_installer 2.3.2'
ok "the OpenCode 2 installer is used" grep -q 'opencode.ai/v2/install' "${tmp}/curl.log"
ok "a known version is pinned for the installer" grep -qx 'VERSION=2.3.2' "${tmp}/installer.log"

run_case nopin 'opencode_official_installer'
ok "an unknown version leaves the installer to decide" grep -qx 'VERSION=' "${tmp}/installer.log"

# ── opencode_npm_fallback: the same release, from the npm registry ────────────
run_case npm_pin 'opencode_npm_fallback 2.3.2'
ok "the npm fallback installs the pinned @opencode/cli" grep -q 'install -g @opencode/cli@2.3.2' "${tmp}/npm.log"
ok "the npm fallback allows the postinstall script" grep -q -- '--allow-scripts=@opencode/cli' "${tmp}/npm.log"
ok "the npm fallback succeeds when npm does" test "$(cat "${tmp}/npm_pin.rc")" = 0
ok "without OpenCode 1 from npm nothing is uninstalled" sh -c "! grep -q 'uninstall' '${tmp}/npm.log'"

run_case npm_latest 'opencode_npm_fallback'
ok "without a version the npm fallback takes latest" grep -q 'install -g @opencode/cli@latest' "${tmp}/npm.log"

run_case npm_v1 'opencode_npm_fallback 2.3.2' STUB_NPM_HAS_V1=0
ok "OpenCode 1 from npm (opencode-ai) is removed first" sh -c \
  "grep -n 'uninstall -g opencode-ai' '${tmp}/npm.log' | cut -d: -f1 | head -1 | xargs test 1 -le && \
   test \"\$(grep -n 'uninstall -g opencode-ai' '${tmp}/npm.log' | cut -d: -f1)\" -lt \"\$(grep -n 'install -g @opencode/cli' '${tmp}/npm.log' | cut -d: -f1)\""

run_case npm_fail 'opencode_npm_fallback 2.3.2' STUB_NPM_RC=1
ok "a failing npm is reported as a failure" test "$(cat "${tmp}/npm_fail.rc")" = 1

# ── run_installer_with_retries: retries pass the version through ─────────────
run_case retry 'run_installer_with_retries opencode opencode_official_installer 9.9.9' STUB_INSTALLER_RC=1
ok "a failing installer is retried three times" test "$(grep -c . "${tmp}/installer.log")" = 3
ok "every retry keeps the pinned version" test "$(grep -cx 'VERSION=9.9.9' "${tmp}/installer.log")" = 3
ok "exhausted retries fail" test "$(cat "${tmp}/retry.rc")" = 1

# ── opencode_keep_v1_data: one copy of OpenCode 1's data, once ────────────────
v1_home="${tmp}/v1-home"
mkdir -p "${v1_home}/.local/share/opencode/storage"
printf 'v1-db\n' >"${v1_home}/.local/share/opencode/opencode.db"
printf 'v1-session\n' >"${v1_home}/.local/share/opencode/storage/s1.json"
run_case keep 'opencode_keep_v1_data "$KEEP_HOME" "$KEEP_HOME/no-such-home"' KEEP_HOME="${v1_home}" STUB_SERVE_ACTIVE=0
ok "a running opencode-serve is stopped before the copy" grep -qx 'stop opencode-serve' "${tmp}/systemctl.log"
copy="${v1_home}/.local/share/opencode-v1-copy"
ok "the data folder is copied before the move" test "$(cat "${copy}/opencode.db" 2>/dev/null)" = v1-db
ok "the copy is complete" test -f "${copy}/storage/s1.json"
ok "no partial copy is left" test ! -e "${copy}.partial"
ok "a home without OpenCode data is skipped" test "$(cat "${tmp}/keep.rc")" = 0

printf 'converted-by-v2\n' >"${v1_home}/.local/share/opencode/opencode.db"
run_case keep_again 'opencode_keep_v1_data "$KEEP_HOME"' KEEP_HOME="${v1_home}"
ok "the copy is made once: a later run keeps the first copy" test "$(cat "${copy}/opencode.db")" = v1-db

fail_home="${tmp}/fail-home"
mkdir -p "${fail_home}/.local/share/opencode" "${tmp}/ro"
printf 'db\n' >"${fail_home}/.local/share/opencode/opencode.db"
cat >"${tmp}/ro/cp" <<'EOF'
#!/usr/bin/env bash
mkdir -p "${@: -1}"; exit 1
EOF
chmod +x "${tmp}/ro/cp"
run_case keep_fail 'PATH="$RO_BIN:$PATH" opencode_keep_v1_data "$KEEP_HOME"' KEEP_HOME="${fail_home}" RO_BIN="${tmp}/ro"
ok "a failed copy fails the move" test "$(cat "${tmp}/keep_fail.rc")" != 0
ok "a failed copy leaves neither a copy nor a partial one" sh -c \
  "test ! -e '${fail_home}/.local/share/opencode-v1-copy' && test ! -e '${fail_home}/.local/share/opencode-v1-copy.partial'"

# ── configure_opencode_background_watcher: managed, reversible, isolated ─────
watcher_home="${tmp}/watcher-home"
mkdir -p "${watcher_home}"
CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${ROOT}" OPENCODE_BACKGROUND_WATCHER=true \
  bash -c 'source "$1"; configure_opencode_background_watcher "$2" root' _ "${SCRIPT}" "${watcher_home}" \
  >"${tmp}/watcher-enable.out" 2>&1
watcher_target="${watcher_home}/.config/opencode/plugins/background.js"
ok "watcher enable installs the Construct-managed plugin" test -f "${watcher_target}"
ok "watcher plugin contains all three requested tools" sh -c \
  "grep -q 'name: \"background\"' '${watcher_target}' && grep -q 'name: \"background_output\"' '${watcher_target}' && grep -q 'name: \"background_kill\"' '${watcher_target}'"
ok "watcher plugin is an OpenCode 2 plugin (default export with id and setup)" sh -c \
  "grep -q '^export default {' '${watcher_target}' && grep -q 'id: \"construct-background\"' '${watcher_target}'"
ok "watcher plugin tools are direct tools that clients see" test "$(grep -c 'options: { codemode: false }' "${watcher_target}")" = 3
ok "watcher plugin excludes the Cortecs request hook" sh -c \
  "! grep -Eq 'enable_model_fallback|chat\\.params|providerID' '${watcher_target}'"

CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${ROOT}" OPENCODE_BACKGROUND_WATCHER=false \
  bash -c 'source "$1"; configure_opencode_background_watcher "$2" root' _ "${SCRIPT}" "${watcher_home}" \
  >"${tmp}/watcher-disable.out" 2>&1
ok "watcher disable removes the managed plugin" test ! -e "${watcher_target}"

mkdir -p "$(dirname "${watcher_target}")"
printf '%s\n' '// user-owned plugin' >"${watcher_target}"
if CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${ROOT}" OPENCODE_BACKGROUND_WATCHER=true \
  bash -c 'source "$1"; configure_opencode_background_watcher "$2" root' _ "${SCRIPT}" "${watcher_home}" \
  >"${tmp}/watcher-collision.out" 2>&1; then
  watcher_collision_rc=0
else
  watcher_collision_rc=$?
fi
ok "watcher enable refuses an unmanaged background.js" test "${watcher_collision_rc}" -ne 0
ok "watcher collision preserves the unmanaged file" grep -qx '// user-owned plugin' "${watcher_target}"

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
