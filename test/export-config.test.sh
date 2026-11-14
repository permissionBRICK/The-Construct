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
  VSCODE_SERVE_WEB_TOKEN_FILE="${tmp}/absent-token" T3CODE_TLS_DIR="${tmp}/absent-tls"
  SSH_HOST_KEY_DIR="${tmp}/absent-ssh")

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
resumed_sid='33333333-3333-4333-8333-333333333333'
mkdir -p "${slug}/memory" "${slug}/${old_sid}/subagents" "${slug}/${new_sid}/subagents" \
  "${hist}/.codex/sessions/2020/01/01" "${hist}/.codex/sessions/2026/01/01" \
  "${hist}/.codex/archived_sessions"
printf '{}\n' >"${slug}/${old_sid}.jsonl"
printf '{}\n' >"${slug}/${new_sid}.jsonl"
printf '{}\n' >"${slug}/${old_sid}/subagents/agent-a.jsonl"
printf '{}\n' >"${slug}/${new_sid}/subagents/agent-b.jsonl"
# A resumed session: recent transcript, subagent files from long ago.
mkdir -p "${slug}/${resumed_sid}/subagents"
printf '{}\n' >"${slug}/${resumed_sid}.jsonl"
printf '{}\n' >"${slug}/${resumed_sid}/subagents/agent-c.jsonl"
touch -d '100 days ago' "${slug}/${resumed_sid}/subagents/agent-c.jsonl"
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
ok 'retention default: old session dir of a kept transcript kept' \
  has retain-default "${cslug}/${resumed_sid}/subagents/agent-c.jsonl"
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

# The T3 Code proxy address lives only in config.env; the backup carries it, only when set.
ok 'proxy address: absent from backup-info.json when not set' \
  test "$(jq -r 'has("t3codeProxyUrl")' "${tmp}/retain-default.info")" = false
printf "T3CODE_PROXY_URL='https://[2001:db8::7]:8443'\n" >>"${tmp}/config.env"
export_history proxy-set
ok 'proxy address: recorded in backup-info.json' \
  test "$(jq -r .t3codeProxyUrl "${tmp}/proxy-set.info")" = 'https://[2001:db8::7]:8443'

ok 'fixtures: no service operations requested' test ! -e "${tmp}/service-calls"

# ── T3 Code event store: only the file the server opens ──────────────────────
# T3 Code 0.1.0 seeds statev2.sqlite from state.sqlite once and leaves the old
# file behind unused. Asking systemctl about t3code-serve is expected here.
export_t3() { # <name> <home> [INCLUDE_AUTH]
  "${fixture_env[@]}" SERVICE_CALLS="${tmp}/t3-service-calls" EXPORT_HOME="$2" \
    INCLUDE_AUTH="${3:-true}" INCLUDE_HISTORY=false OUT="${tmp}/$1.tar.gz" \
    bash "${ROOT}/bin/export-config.sh" >"${tmp}/$1.log" 2>&1
  tar -tzf "${tmp}/$1.tar.gz" >"${tmp}/$1.members"
}
t3_files() { # <dir> <content> <file>...
  local dir="$1" content="$2"
  shift 2
  mkdir -p "${dir}"
  for f in "$@"; do printf '%s\n' "${content}" >"${dir}/${f}"; done
}
t3_files "${tmp}/t3-v2/.t3/userdata" saved \
  state.sqlite state.sqlite-shm statev2.sqlite statev2.sqlite-wal statev2.sqlite-shm
export_t3 t3-v2 "${tmp}/t3-v2"
for f in statev2.sqlite statev2.sqlite-wal statev2.sqlite-shm; do
  ok "T3 0.1.0: ${f} saved" has t3-v2 ".t3/userdata/${f}"
done
ok 'T3 0.1.0: the unused state.sqlite is left out' lacks t3-v2 '.t3/userdata/state.sqlite'
t3_files "${tmp}/t3-v1/.t3/userdata" saved state.sqlite state.sqlite-wal
export_t3 t3-v1 "${tmp}/t3-v1"
ok 'T3 before 0.1.0: state.sqlite saved' has t3-v1 '.t3/userdata/state.sqlite'
ok 'T3 before 0.1.0: state.sqlite-wal saved' has t3-v1 '.t3/userdata/state.sqlite-wal'
export_t3 t3-v2-noauth "${tmp}/t3-v2" false
ok 'T3 auth=false: no event store saved' lacks t3-v2-noauth '.t3/userdata/state'

# Restore onto a freshly provisioned T3 Code 0.1.0, which already made its own
# statev2.sqlite: the fresh files must go, or T3 would keep them and never
# import a state.sqlite from an older backup.
for v in v2 v1; do
  t3_files "${tmp}/t3-fresh-${v}/.t3/userdata" fresh statev2.sqlite statev2.sqlite-wal statev2.sqlite-shm
  "${fixture_env[@]}" SERVICE_CALLS="${tmp}/t3-service-calls" BACKUP_TGZ="${tmp}/t3-${v}.tar.gz" \
    EXPORT_HOME="${tmp}/t3-fresh-${v}" \
    bash "${ROOT}/bin/restore-config.sh" >"${tmp}/t3-restore-${v}.log" 2>&1
done
fresh_v2="${tmp}/t3-fresh-v2/.t3/userdata"
ok 'T3 0.1.0 restore: saved statev2.sqlite replaces the fresh one' grep -qx saved "${fresh_v2}/statev2.sqlite"
ok 'T3 0.1.0 restore: saved statev2.sqlite-wal replaces the fresh one' grep -qx saved "${fresh_v2}/statev2.sqlite-wal"
ok 'T3 0.1.0 restore: logged as restored chat history' \
  grep -qF 'restored chat history: .t3/userdata/statev2.sqlite' "${tmp}/t3-restore-v2.log"
fresh_v1="${tmp}/t3-fresh-v1/.t3/userdata"
ok 'T3 older backup: saved state.sqlite restored' grep -qx saved "${fresh_v1}/state.sqlite"
ok 'T3 older backup: fresh statev2 files removed so T3 imports it' \
  test ! -e "${fresh_v1}/statev2.sqlite" -a ! -e "${fresh_v1}/statev2.sqlite-wal" -a ! -e "${fresh_v1}/statev2.sqlite-shm"

# ── STAGE_DIR and EXPORT_REASON (construct backup) ───────────────────────────
# A T3 Code server that is running: this systemctl says so and logs every call.
mkdir -p "${tmp}/t3bin"
cat >"${tmp}/t3bin/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SERVICE_CALLS}"
if [[ "$1" == is-active && "$*" == *t3code-serve* ]]; then exit 0; fi
exit 0
STUB
chmod +x "${tmp}/t3bin/systemctl"
live="${tmp}/live-home"
mkdir -p "${live}/.t3/userdata" "${live}/.local/share/opencode" "${live}/.codex" "${live}/.claude"
printf '# instructions\n' >"${live}/.claude/CLAUDE.md"
# SQLite stores in WAL mode with a write still in the -wal (the writer never
# closed): the online snapshot must carry it, without the side files.
live_db() { # <path> <table>
  python3 -I - "$1" "$2" <<'PY'
import os, sqlite3, sys
c = sqlite3.connect(sys.argv[1])
c.execute("pragma journal_mode=wal")
c.execute("pragma wal_autocheckpoint=0")
c.execute(f"create table {sys.argv[2]}(v text)")
c.execute(f"insert into {sys.argv[2]} values ('checkpointed')")
c.commit()
c.execute("pragma wal_checkpoint(truncate)")
c.execute(f"insert into {sys.argv[2]} values ('only in the wal')")
c.commit()
os._exit(0)
PY
}
live_db "${live}/.t3/userdata/statev2.sqlite" threads
live_db "${live}/.local/share/opencode/opencode.db" sessions
live_db "${live}/.codex/memories_1.sqlite" memories
chmod 0600 "${live}/.t3/userdata/statev2.sqlite"
rows() { python3 -I -c 'import sqlite3, sys; print(sqlite3.connect(sys.argv[1]).execute("select count(*) from " + sys.argv[2]).fetchone()[0])' "$1" "$2" 2>/dev/null; }
t3_env=(env -i PATH="${tmp}/t3bin:${PATH}" SERVICE_CALLS="${tmp}/stage-calls"
  CONFIG_FILE="${tmp}/config.env" REPO_DIR="${tmp}/repo" T3_RESUME_HELPER="${tmp}/no-resume-helper"
  PROJECTS_STORE="${tmp}/projects" WORKSPACE_ROOT="${tmp}/workspace"
  VSCODE_SERVE_WEB_TOKEN_FILE="${tmp}/absent-token" T3CODE_TLS_DIR="${tmp}/absent-tls" SSH_HOST_KEY_DIR="${tmp}/absent-ssh"
  EXPORT_HOME="${live}" INCLUDE_AUTH=true INCLUDE_HISTORY=true)

stage="${tmp}/stage-backup"
rm -f "${tmp}/stage-calls"
rc=0
"${t3_env[@]}" STAGE_DIR="${stage}" EXPORT_REASON=backup OUT="${tmp}/never.tar.gz" \
  bash "${ROOT}/bin/export-config.sh" >"${tmp}/stage-backup.log" 2>&1 || rc=$?
ok 'STAGE_DIR: exits 0' test "${rc}" = 0
ok 'STAGE_DIR: no tarball is packed' test ! -e "${tmp}/never.tar.gz"
ok 'STAGE_DIR: the stage holds the export layout' \
  test -f "${stage}/home/.claude/CLAUDE.md" -a -f "${stage}/backup-info.json" -a -f "${stage}/MANIFEST.txt"
ok 'STAGE_DIR: the stage stays with the caller' test -d "${stage}"
ok 'backup: T3 Code is neither asked about nor stopped' test ! -e "${tmp}/stage-calls"
ok 'backup: no t3-resume.json (nothing was interrupted)' test ! -e "${stage}/t3-resume.json"
ok 'backup: T3 store snapshot carries the -wal rows' test "$(rows "${stage}/home/.t3/userdata/statev2.sqlite" threads)" = 2
ok 'backup: OpenCode snapshot carries the -wal rows' test "$(rows "${stage}/home/.local/share/opencode/opencode.db" sessions)" = 2
ok 'backup: Codex memories snapshot carries the -wal rows' test "$(rows "${stage}/home/.codex/memories_1.sqlite" memories)" = 2
ok 'backup: no -wal/-shm side files are staged' \
  test -z "$(find "${stage}" \( -name '*-wal' -o -name '*-shm' \) -print -quit)"
ok 'backup: the snapshot keeps the database file mode' \
  test "$(stat -c %a "${stage}/home/.t3/userdata/statev2.sqlite")" = 600
ok 'backup: snapshots are listed in the manifest' \
  sh -c "grep -qx '.t3/userdata/statev2.sqlite' '${stage}/MANIFEST.txt' && grep -qx '.local/share/opencode/opencode.db' '${stage}/MANIFEST.txt' && grep -qx '.codex/memories_1.sqlite' '${stage}/MANIFEST.txt'"
ok 'backup: the live databases are untouched' \
  sh -c "test -s '${live}/.t3/userdata/statev2.sqlite-wal' && test \"\$(stat -c %a '${live}/.t3/userdata/statev2.sqlite')\" = 600"

# Default reason (reinstall): today's behaviour, T3 stopped across the copy.
rm -f "${tmp}/stage-calls"
"${t3_env[@]}" OUT="${tmp}/reinstall.tar.gz" bash "${ROOT}/bin/export-config.sh" >"${tmp}/reinstall.log" 2>&1
ok 'reinstall (default): packs the tarball' test -s "${tmp}/reinstall.tar.gz"
ok 'reinstall (default): T3 Code is stopped, then started again' \
  test "$(grep -E '^(stop|start) ' "${tmp}/stage-calls" 2>/dev/null | tr '\n' '|')" = 'stop t3code-serve|start t3code-serve|'
tar -tzf "${tmp}/reinstall.tar.gz" >"${tmp}/reinstall.members"
ok 'reinstall (default): the database files are copied with their side files' \
  sh -c "grep -qx './home/.t3/userdata/statev2.sqlite-wal' '${tmp}/reinstall.members' && grep -qx './home/.local/share/opencode/opencode.db-wal' '${tmp}/reinstall.members'"
rm -f "${tmp}/stage-calls"
"${t3_env[@]}" EXPORT_REASON=reinstall STAGE_DIR="${tmp}/stage-reinstall" bash "${ROOT}/bin/export-config.sh" >/dev/null 2>&1
ok 'reinstall + STAGE_DIR: T3 Code is still stopped and started' \
  test "$(grep -cE '^(stop|start) t3code-serve$' "${tmp}/stage-calls" 2>/dev/null)" = 2

rc=0
"${t3_env[@]}" EXPORT_REASON=sometimes OUT="${tmp}/bad-reason.tar.gz" bash "${ROOT}/bin/export-config.sh" >/dev/null 2>"${tmp}/bad-reason.err" || rc=$?
ok 'an unknown EXPORT_REASON exits 2' test "${rc}" = 2
ok '... and packs nothing' test ! -e "${tmp}/bad-reason.tar.gz"

# Failures: the script's own stage is removed; a STAGE_DIR is left to the caller;
# a T3 Code server stopped for the copy is started again.
mkdir -p "${tmp}/failcp"
cat >"${tmp}/failcp/cp" <<'STUB'
#!/usr/bin/env bash
dest="${!#}"
printf '%s\n' "${dest}" >>"${CP_LOG}"
if [[ "${dest}" == *"${CP_FAIL_ON}"* ]]; then exit 1; fi
exec /bin/cp "$@"
STUB
chmod +x "${tmp}/failcp/cp"
rm -f "${tmp}/cp.log" "${tmp}/stage-calls"
rc=0
"${t3_env[@]}" PATH="${tmp}/failcp:${tmp}/t3bin:${PATH}" CP_LOG="${tmp}/cp.log" CP_FAIL_ON=statev2.sqlite \
  OUT="${tmp}/failed.tar.gz" bash "${ROOT}/bin/export-config.sh" >/dev/null 2>&1 || rc=$?
ok 'a failing copy fails the export' test "${rc}" -ne 0
own_stage="$(head -n 1 "${tmp}/cp.log" 2>/dev/null)"
own_stage="${own_stage%%/home/*}"
ok '... its own stage was under /tmp/construct-export.*' sh -c "case '${own_stage}' in /tmp/construct-export.*) exit 0;; esac; exit 1"
ok '... and is removed' test ! -e "${own_stage}"
ok '... no tarball is left' test ! -e "${tmp}/failed.tar.gz"
ok '... and the T3 Code server it stopped is started again' \
  test "$(grep -E '^(stop|start) ' "${tmp}/stage-calls" 2>/dev/null | tr '\n' '|')" = 'stop t3code-serve|start t3code-serve|'
rm -f "${tmp}/cp.log"
rc=0
"${t3_env[@]}" PATH="${tmp}/failcp:${tmp}/t3bin:${PATH}" CP_LOG="${tmp}/cp.log" CP_FAIL_ON=CLAUDE.md \
  STAGE_DIR="${tmp}/stage-failed" EXPORT_REASON=backup bash "${ROOT}/bin/export-config.sh" >/dev/null 2>&1 || rc=$?
ok 'a failing export with STAGE_DIR fails too' test "${rc}" -ne 0
ok '... and leaves the STAGE_DIR to its caller' test -d "${tmp}/stage-failed"

# ── SSH host keys: export, restore through the tarball, sshd restarted once ──
keys="${tmp}/ssh-src"
mkdir -p "${keys}" "${tmp}/sshbin"
for t in ed25519 rsa; do
  printf 'saved %s private\n' "${t}" >"${keys}/ssh_host_${t}_key"
  printf 'saved %s public\n' "${t}" >"${keys}/ssh_host_${t}_key.pub"
  chmod 600 "${keys}/ssh_host_${t}_key"
done
for auth in true false; do
  "${fixture_env[@]}" SSH_HOST_KEY_DIR="${keys}" EXPORT_HOME="${tmp}/source" INCLUDE_AUTH="${auth}" \
    INCLUDE_HISTORY=false OUT="${tmp}/ssh-${auth}.tar.gz" \
    bash "${ROOT}/bin/export-config.sh" >/dev/null 2>&1
  tar -tzf "${tmp}/ssh-${auth}.tar.gz" >"${tmp}/ssh-${auth}.members"
done
ok 'host keys: auth=true saves every ssh_host_* file' \
  test "$(grep -c '^\./etc/ssh/ssh_host_' "${tmp}/ssh-true.members")" = 4
ok 'host keys: auth=false saves none' sh -c "! grep -q 'etc/ssh' '${tmp}/ssh-false.members'"

# The fresh VM: other keys, one type the old VM did not have.
target="${tmp}/ssh-target"
mkdir -p "${target}"
for t in ed25519 ecdsa; do
  printf 'fresh %s private\n' "${t}" >"${target}/ssh_host_${t}_key"
  printf 'fresh %s public\n' "${t}" >"${target}/ssh_host_${t}_key.pub"
done
printf '#!/usr/bin/env bash\nexit 0\n' >"${tmp}/sshbin/sshd"
# ssh.service is unknown here, sshd.service restarts (SSHD_UNIT_RC=1: that fails too).
cat >"${tmp}/sshbin/systemctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${SERVICE_CALLS}"
case "$*" in
  "try-restart sshd.service") exit "${SSHD_UNIT_RC:-0}" ;;
esac
exit 1
STUB
chmod +x "${tmp}/sshbin/sshd" "${tmp}/sshbin/systemctl"
ssh_restore() { # <tarball> <log>
  "${fixture_env[@]}" PATH="${tmp}/sshbin:${tmp}/bin:${PATH}" SERVICE_CALLS="${tmp}/ssh-calls" SSHD_UNIT_RC="${SSHD_UNIT_RC:-0}" \
    SSH_HOST_KEY_DIR="${target}" BACKUP_TGZ="$1" EXPORT_HOME="${tmp}/ssh-home" \
    bash "${ROOT}/bin/restore-config.sh" >"$2" 2>&1
}
rm -f "${tmp}/ssh-calls"
ssh_restore "${tmp}/ssh-true.tar.gz" "${tmp}/ssh-restore.log"
ok 'host keys: the saved keys replace the fresh ones' \
  sh -c "cmp -s '${keys}/ssh_host_ed25519_key' '${target}/ssh_host_ed25519_key' && cmp -s '${keys}/ssh_host_rsa_key.pub' '${target}/ssh_host_rsa_key.pub'"
ok 'host keys: a key type the backup lacks is removed' test ! -e "${target}/ssh_host_ecdsa_key"
ok 'host keys: private keys 0600, public keys 0644' \
  test "$(stat -c '%a' "${target}/ssh_host_rsa_key" "${target}/ssh_host_rsa_key.pub" | tr '\n' ' ')" = '600 644 '
ok 'host keys: sshd is restarted once' test "$(grep -cx 'try-restart ssh.service' "${tmp}/ssh-calls")" = 1

ok 'host keys: the restart tries sshd.service when ssh.service fails' grep -qx 'try-restart sshd.service' "${tmp}/ssh-calls"
ok 'host keys: the restore says it restored them' grep -q 'restored SSH host keys' "${tmp}/ssh-restore.log"

rm -f "${tmp}/ssh-calls"
ssh_restore "${tmp}/ssh-true.tar.gz" "${tmp}/ssh-again.log"
ok 'host keys: the same keys again restart nothing' sh -c "! grep -q 'try-restart' '${tmp}/ssh-calls' 2>/dev/null"

rm -f "${tmp}/ssh-calls"
ssh_restore "${tmp}/ssh-false.tar.gz" "${tmp}/ssh-old.log"
ok 'host keys: a backup without host keys changes none and restarts nothing' \
  sh -c "cmp -s '${keys}/ssh_host_ed25519_key' '${target}/ssh_host_ed25519_key' && ! grep -q 'try-restart' '${tmp}/ssh-calls' 2>/dev/null"

# sshd cannot be restarted: the keys it runs with are put back, and nothing claims a restore.
for t in ed25519 ecdsa; do printf 'fresh %s private\n' "${t}" >"${target}/ssh_host_${t}_key"; done
rm -f "${target}/ssh_host_rsa_key" "${target}/ssh_host_rsa_key.pub"
rc=0
SSHD_UNIT_RC=1 ssh_restore "${tmp}/ssh-true.tar.gz" "${tmp}/ssh-norestart.log" || rc=$?
ok 'host keys: a failed restart does not fail the restore' test "${rc}" = 0
ok '... the previous keys are back' \
  sh -c "grep -qx 'fresh ecdsa private' '${target}/ssh_host_ecdsa_key' && test ! -e '${target}/ssh_host_rsa_key'"
ok '... and no restore is claimed' sh -c "! grep -q 'restored SSH host keys' '${tmp}/ssh-norestart.log'"

# sshd refusing the saved keys: the VM keeps the keys it had, sshd is left alone.
for t in ed25519 ecdsa; do printf 'fresh %s private\n' "${t}" >"${target}/ssh_host_${t}_key"; done
rm -f "${target}/ssh_host_rsa_key" "${target}/ssh_host_rsa_key.pub"
printf '#!/usr/bin/env bash\nexit 1\n' >"${tmp}/sshbin/sshd"
rm -f "${tmp}/ssh-calls"
rc=0
ssh_restore "${tmp}/ssh-true.tar.gz" "${tmp}/ssh-refused.log" || rc=$?
ok 'host keys: a refused key set does not fail the restore' test "${rc}" = 0
ok '... the previous keys are back' \
  sh -c "grep -qx 'fresh ecdsa private' '${target}/ssh_host_ecdsa_key' && grep -qx 'fresh ed25519 private' '${target}/ssh_host_ed25519_key' && test ! -e '${target}/ssh_host_rsa_key'"
ok '... sshd is not restarted' sh -c "! grep -q 'try-restart' '${tmp}/ssh-calls' 2>/dev/null"
ok '... and it is reported' grep -q 'sshd refused them' "${tmp}/ssh-refused.log"

printf '\n  export-config fixture tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[[ "${fail}" -eq 0 ]]
