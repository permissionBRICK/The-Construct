#!/usr/bin/env bash
# Plain-Bash regression tests for the Codex installer helpers in
# bin/install-ai-tools.sh. Run: bash test/codex-install.test.sh
#
# The failures these guard against:
# - The official installer treats the first `codex` on PATH as a second,
#   npm-managed install whenever that file contains "#!/usr/bin/env node", which
#   the native binary does. Our /usr/local/bin/codex link comes first on systemd's
#   PATH, so every reprovision warned about "Multiple managed Codex installs".
# - The installer keeps every release it ever unpacked (~430 MB each), so a VM
#   collected gigabytes of old Codex versions.
#
# Everything here runs against a stubbed curl and a scratch release tree -- no
# network, no installs.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="${ROOT}/bin/install-ai-tools.sh"
tmp="$(mktemp -d)"
sleeper=""
trap '[[ -n "${sleeper}" ]] && kill "${sleeper}" 2>/dev/null; rm -rf "${tmp}"' EXIT

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

# Source the helpers (installs nothing) and run one expression.
run_case() {
  local name="$1" body="$2"
  shift 2
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" CASE_BODY="${body}" \
    env "$@" bash -c 'source "${SCRIPT_PATH}"; eval "${CASE_BODY}"' \
    >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

# ── codex_official_installer: the installer sees its own entry first ──────────
home="${tmp}/home"
mkdir -p "${home}/.local/bin" "${tmp}/sysbin" "${tmp}/stub"
printf '#!/bin/sh\n# native binary that happens to embed: #!/usr/bin/env node\n' >"${home}/.local/bin/codex"
chmod +x "${home}/.local/bin/codex"
ln -s "${home}/.local/bin/codex" "${tmp}/sysbin/codex"

# The stub curl writes an installer that records what the real one checks: the
# first codex on PATH, its stdin answer and CI.
cat >"${tmp}/stub/curl" <<'EOF'
#!/usr/bin/env bash
[[ "${STUB_CURL_RC:-0}" == 0 ]] || exit "${STUB_CURL_RC}"
while [[ $# -gt 0 ]]; do [[ "$1" == -o ]] && out="$2"; shift; done
cat >"${out}" <<SCRIPT
printf 'codex=%s\n' "\$(command -v codex)" >"${STUB_LOG}"
read -r answer; printf 'answer=%s\nci=%s\n' "\${answer}" "\${CI:-}" >>"${STUB_LOG}"
printf 'installer=%s\n' "\$0" >>"${STUB_LOG}"
exit ${STUB_INSTALLER_RC:-0}
SCRIPT
EOF
chmod +x "${tmp}/stub/curl"

run_case install 'codex_official_installer' HOME="${home}" STUB_LOG="${tmp}/installer.log" \
  PATH="${tmp}/stub:${tmp}/sysbin:/usr/bin:/bin"
ok "the installer succeeds" test "$(cat "${tmp}/install.rc")" = 0
ok "the installer finds its own entry, not the /usr/local/bin link" \
  grep -qx "codex=${home}/.local/bin/codex" "${tmp}/installer.log"
ok "the installer's launch prompt is answered no" grep -qx 'answer=n' "${tmp}/installer.log"
ok "the installer runs with CI=1" grep -qx 'ci=1' "${tmp}/installer.log"
installer_file="$(sed -n 's/^installer=//p' "${tmp}/installer.log")"
ok "the downloaded installer is removed" test -n "${installer_file}" -a ! -e "${installer_file}"

run_case install_fail 'codex_official_installer' HOME="${home}" STUB_LOG="${tmp}/installer.log" \
  STUB_INSTALLER_RC=3 PATH="${tmp}/stub:${tmp}/sysbin:/usr/bin:/bin"
ok "a failing installer is reported" test "$(cat "${tmp}/install_fail.rc")" = 3

rm -f "${tmp}/installer.log"
run_case fetch_fail 'codex_official_installer' HOME="${home}" STUB_LOG="${tmp}/installer.log" \
  STUB_CURL_RC=22 PATH="${tmp}/stub:${tmp}/sysbin:/usr/bin:/bin"
ok "a failed download is reported" test "$(cat "${tmp}/fetch_fail.rc")" = 22
ok "a failed download runs no installer" test ! -e "${tmp}/installer.log"

# ── prune_codex_releases: keep current and running releases ─────────────────
standalone="${tmp}/standalone"
for v in 0.1.0 0.2.0 0.3.0 0.4.0; do mkdir -p "${standalone}/releases/${v}-x86_64/bin"; done
mkdir -p "${standalone}/releases/.staging.0.5.0-x86_64.123"
ln -s "${standalone}/releases/0.4.0-x86_64" "${standalone}/current"
cp "$(command -v sleep)" "${standalone}/releases/0.2.0-x86_64/bin/codex"
"${standalone}/releases/0.2.0-x86_64/bin/codex" 60 &
sleeper=$!
for _ in $(seq 50); do
  [[ "$(readlink "/proc/${sleeper}/exe" 2>/dev/null)" == */0.2.0-x86_64/bin/codex ]] && break
  sleep 0.1
done

run_case prune "prune_codex_releases '${standalone}'"
ok "pruning succeeds" test "$(cat "${tmp}/prune.rc")" = 0
ok "the current release is kept" test -d "${standalone}/releases/0.4.0-x86_64"
ok "a release a running process executes from is kept" test -d "${standalone}/releases/0.2.0-x86_64"
ok "unused old releases are removed" test ! -e "${standalone}/releases/0.1.0-x86_64" -a ! -e "${standalone}/releases/0.3.0-x86_64"
ok "the installer's staging dirs are left to the installer" test -d "${standalone}/releases/.staging.0.5.0-x86_64.123"
ok "each removal is reported" test "$(grep -c 'Removing old Codex release' "${tmp}/prune.out")" = 2
kill "${sleeper}" 2>/dev/null
wait "${sleeper}" 2>/dev/null
sleeper=""

rm "${standalone}/current"
run_case prune_nocurrent "prune_codex_releases '${standalone}'"
ok "without a current link nothing is removed" test -d "${standalone}/releases/0.2.0-x86_64" -a -d "${standalone}/releases/0.4.0-x86_64"

run_case prune_npm "prune_codex_releases '${tmp}/missing'"
ok "an npm install without a standalone dir is a no-op" test "$(cat "${tmp}/prune_npm.rc")" = 0

printf '\n%s passed, %s failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
