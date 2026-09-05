#!/usr/bin/env bash
# Isolated export/auth-gating and real export -> restore fixtures.
# Run: bash test/export-config.test.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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

mkdir -p "${tmp}/source/.config/glab-cli" "${tmp}/source/.config/gh" \
  "${tmp}/bin" "${tmp}/repo" "${tmp}/projects" "${tmp}/workspace"
cat >"${tmp}/source/.config/glab-cli/config.yml" <<'CONFIG'
git_protocol: ssh
hosts:
  gitlab.example.invalid:
    token: dummy-glab-token-not-a-credential
    api_protocol: https
CONFIG
printf 'editor: vim\n' >"${tmp}/source/.config/glab-cli/extra.yml"
chmod 600 "${tmp}/source/.config/glab-cli/config.yml"
printf 'github.com:\n  oauth_token: dummy-gh-token-not-a-credential\n' \
  >"${tmp}/source/.config/gh/hosts.yml"

# No real service calls, live config, profiles, TLS keys or serve-web token.
cat >"${tmp}/bin/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SERVICE_CALLS}"
exit 1
STUB
chmod +x "${tmp}/bin/systemctl"
fixture_env=(env -i PATH="${tmp}/bin:${PATH}" SERVICE_CALLS="${tmp}/service-calls"
  CONFIG_FILE="${tmp}/config.env" REPO_DIR="${tmp}/repo"
  PROJECTS_STORE="${tmp}/projects" WORKSPACE_ROOT="${tmp}/workspace"
  VSCODE_SERVE_WEB_TOKEN_FILE="${tmp}/absent-token" T3CODE_TLS_DIR="${tmp}/absent-tls")

for auth in true false; do
  "${fixture_env[@]}" EXPORT_HOME="${tmp}/source" INCLUDE_AUTH="${auth}" \
    INCLUDE_HISTORY=false OUT="${tmp}/${auth}.tar.gz" \
    bash "${ROOT}/bin/export-config.sh" >"${tmp}/export-${auth}.log" 2>&1
  tar -tzf "${tmp}/${auth}.tar.gz" >"${tmp}/${auth}.members"
  tar -xOf "${tmp}/${auth}.tar.gz" ./MANIFEST.txt >"${tmp}/${auth}.manifest"
  ok "auth=${auth}: existing GitHub CLI credentials remain included" \
    grep -qxF './home/.config/gh/hosts.yml' "${tmp}/${auth}.members"
done

ok 'auth=true: GitLab CLI credentials included in archive' \
  grep -qxF './home/.config/glab-cli/config.yml' "${tmp}/true.members"
ok 'auth=true: whole GitLab CLI config directory included' \
  grep -qxF './home/.config/glab-cli/extra.yml' "${tmp}/true.members"
ok 'auth=true: GitLab CLI recorded in manifest' \
  grep -qxF '.config/glab-cli' "${tmp}/true.manifest"
ok 'auth=false: entire GitLab CLI directory omitted from archive' \
  test -z "$(grep -F './home/.config/glab-cli' "${tmp}/false.members" || true)"
ok 'auth=false: GitLab CLI omitted from manifest' \
  test -z "$(grep -F '.config/glab-cli' "${tmp}/false.manifest" || true)"

for auth in true false; do
  "${fixture_env[@]}" BACKUP_TGZ="${tmp}/${auth}.tar.gz" \
    EXPORT_HOME="${tmp}/restored-${auth}" \
    bash "${ROOT}/bin/restore-config.sh" >"${tmp}/restore-${auth}.log" 2>&1
done
ok 'round trip: dummy GitLab credentials and settings restored byte-for-byte' \
  cmp -s "${tmp}/source/.config/glab-cli/config.yml" \
    "${tmp}/restored-true/.config/glab-cli/config.yml"
ok 'round trip: additional GitLab CLI config file restored' \
  cmp -s "${tmp}/source/.config/glab-cli/extra.yml" \
    "${tmp}/restored-true/.config/glab-cli/extra.yml"
ok 'round trip: private credential file mode preserved' \
  test "$(stat -c %a "${tmp}/restored-true/.config/glab-cli/config.yml" 2>/dev/null || true)" = 600
ok 'round trip: auth=false restores no GitLab CLI directory' \
  test ! -e "${tmp}/restored-false/.config/glab-cli"
ok 'fixtures: no service operations requested' test ! -e "${tmp}/service-calls"

printf '\n  export-config fixture tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
