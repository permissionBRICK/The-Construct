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
# ── Chat history retention ───────────────────────────────────────────────────
hist="${tmp}/history-home"
slug="${hist}/.claude/projects/-root-repos-demo"
old_sid='11111111-1111-4111-8111-111111111111'
new_sid='22222222-2222-4222-8222-222222222222'
mkdir -p "${slug}/memory" "${slug}/${old_sid}/subagents" "${slug}/${new_sid}/subagents" \
  "${hist}/.codex/sessions/2020/01/01" "${hist}/.codex/sessions/2026/01/01" \
  "${hist}/.codex/archived_sessions"
printf '{}\n' >"${slug}/${old_sid}.jsonl"
printf '{}\n' >"${slug}/${new_sid}.jsonl"
printf '{}\n' >"${slug}/${old_sid}/subagents/agent-a.jsonl"
printf '{}\n' >"${slug}/${new_sid}/subagents/agent-b.jsonl"
printf 'note\n' >"${slug}/memory/note.md"
printf '# memory\n' >"${slug}/MEMORY.md"
printf '{}\n' >"${hist}/.claude/history.jsonl"
printf '{}\n' >"${hist}/.codex/sessions/2020/01/01/rollout-old.jsonl"
printf '{}\n' >"${hist}/.codex/sessions/2026/01/01/rollout-new.jsonl"
printf '{}\n' >"${hist}/.codex/archived_sessions/rollout-archived-old.jsonl"
printf '{}\n' >"${hist}/.codex/session_index.jsonl"
# Age the old session, its subagent dir, all memory and the index files; the
# new session's subagent file stays fresh.
touch -d '100 days ago' "${slug}/${old_sid}.jsonl" "${slug}/${old_sid}/subagents/agent-a.jsonl" \
  "${slug}/memory/note.md" "${slug}/MEMORY.md" "${hist}/.claude/history.jsonl" \
  "${hist}/.codex/sessions/2020/01/01/rollout-old.jsonl" \
  "${hist}/.codex/archived_sessions/rollout-archived-old.jsonl" \
  "${hist}/.codex/session_index.jsonl"
touch -d '5 days ago' "${slug}/${new_sid}.jsonl" "${hist}/.codex/sessions/2026/01/01/rollout-new.jsonl"

# export_history <name> [VAR=value...]: export the history fixture and list it.
export_history() {
  local name="$1"
  shift
  "${fixture_env[@]}" EXPORT_HOME="${hist}" INCLUDE_AUTH=false INCLUDE_HISTORY=true \
    AI_TOOLS=claude-code,codex OUT="${tmp}/${name}.tar.gz" "$@" \
    bash "${ROOT}/bin/export-config.sh" >"${tmp}/${name}.log" 2>"${tmp}/${name}.err"
  tar -tzf "${tmp}/${name}.tar.gz" >"${tmp}/${name}.members"
  tar -xOzf "${tmp}/${name}.tar.gz" ./backup-info.json >"${tmp}/${name}.info"
}
has() { grep -qxF "./home/$2" "${tmp}/$1.members"; }
lacks() { ! grep -qF "./home/$2" "${tmp}/$1.members"; }
cslug='.claude/projects/-root-repos-demo'

export_history retain-default
ok 'retention default: old Claude transcript dropped' lacks retain-default "${cslug}/${old_sid}.jsonl"
ok 'retention default: old Claude session dir dropped' lacks retain-default "${cslug}/${old_sid}/"
ok 'retention default: recent Claude transcript kept' has retain-default "${cslug}/${new_sid}.jsonl"
ok 'retention default: session dir with a recent file kept' \
  has retain-default "${cslug}/${new_sid}/subagents/agent-b.jsonl"
ok 'retention default: old memory kept' has retain-default "${cslug}/memory/note.md"
ok 'retention default: old MEMORY.md kept' has retain-default "${cslug}/MEMORY.md"
ok 'retention default: Claude prompt history kept' has retain-default '.claude/history.jsonl'
ok 'retention default: old Codex session dropped' \
  lacks retain-default '.codex/sessions/2020/01/01/rollout-old.jsonl'
ok 'retention default: emptied Codex session dirs removed' lacks retain-default '.codex/sessions/2020/'
ok 'retention default: old archived Codex session dropped' \
  lacks retain-default '.codex/archived_sessions/rollout-archived-old.jsonl'
ok 'retention default: recent Codex session kept' \
  has retain-default '.codex/sessions/2026/01/01/rollout-new.jsonl'
ok 'retention default: Codex session index kept' has retain-default '.codex/session_index.jsonl'
ok 'retention default: summary logged per agent' \
  bash -c 'grep -q "Claude history older than 30 days: dropped 2 file" "$1" &&
    grep -q "Codex history older than 30 days: dropped 2 file" "$1"' _ "${tmp}/retain-default.log"
ok 'retention default: recorded in backup-info.json' \
  test "$(jq -r .historyRetentionDays "${tmp}/retain-default.info")" = 30
ok 'retention: live home untouched' test -f "${slug}/${old_sid}.jsonl" -a \
  -f "${hist}/.codex/sessions/2020/01/01/rollout-old.jsonl"

export_history retain-zero HISTORY_RETENTION_DAYS=0
ok 'retention 0: old Claude transcript kept' has retain-zero "${cslug}/${old_sid}.jsonl"
ok 'retention 0: old Claude session dir kept' has retain-zero "${cslug}/${old_sid}/subagents/agent-a.jsonl"
ok 'retention 0: old Codex session kept' has retain-zero '.codex/sessions/2020/01/01/rollout-old.jsonl'
ok 'retention 0: recorded in backup-info.json' \
  test "$(jq -r .historyRetentionDays "${tmp}/retain-zero.info")" = 0

export_history retain-wide HISTORY_RETENTION_DAYS=365
ok 'retention 365: 100-day-old transcript kept' has retain-wide "${cslug}/${old_sid}.jsonl"

export_history retain-invalid HISTORY_RETENTION_DAYS=abc
ok 'retention invalid: warns on stderr' grep -q 'HISTORY_RETENTION_DAYS' "${tmp}/retain-invalid.err"
ok 'retention invalid: falls back to 30 days' lacks retain-invalid "${cslug}/${old_sid}.jsonl"
ok 'retention invalid: recent transcript kept' has retain-invalid "${cslug}/${new_sid}.jsonl"
ok 'retention invalid: 30 recorded in backup-info.json' \
  test "$(jq -r .historyRetentionDays "${tmp}/retain-invalid.info")" = 30

ok 'fixtures: no service operations requested' test ! -e "${tmp}/service-calls"

printf '\n  export-config fixture tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
