#!/usr/bin/env bash
# Plain-Bash fixture tests for bin/restore-config.sh's T3 Code channel handling.
# Run: bash test/restore-config.test.sh

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESTORE="${ROOT}/bin/restore-config.sh"

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

setup_fixture() {
  local case_name="$1" t3code_val="$2" channel_val="$3"
  local d="${tmp}/${case_name}"
  local backup="${d}/backup" export_home="${d}/export_home"
  local repo="${d}/repo" config_dir="${d}/config"
  mkdir -p "${backup}/home" "${export_home}" "${repo}/bin" "${config_dir}"

  # Minimal backup-info.json
  printf '{"t3code":%s,"t3codeChannel":"%s"}\n' "${t3code_val}" "${channel_val}" \
    > "${backup}/backup-info.json"

  # Provide a real config-set.sh
  cp "${ROOT}/bin/config-set.sh" "${repo}/bin/config-set.sh"
  # Instrumented stub: records invocation count + T3CODE_CHANNEL into a marker file.
  local marker="${d}/install-invocations"
  cat > "${repo}/bin/install-ai-tools.sh" <<STUB
#!/bin/bash
echo "\${T3CODE_CHANNEL:-UNSET}" >> "${marker}"
exit 0
STUB
  chmod +x "${repo}/bin/install-ai-tools.sh"

  # Empty config.env
  touch "${config_dir}/config.env"

  printf '%s' "${d}"
}

read_config_key() {
  local config="$1" key="$2"
  sed -n "s/^${key}=//p" "${config}" 2>/dev/null | head -1
}

run_restore() {
  local d="$1"
  local backup="${d}/backup" export_home="${d}/export_home"
  local repo="${d}/repo" config="${d}/config/config.env"
  # restore-config.sh sources /opt/construct/repo/bin/config-set.sh and reads
  # BACKUP_DIR, EXPORT_HOME, CONFIG_FILE, REPO_DIR from the environment.
  # We also stub out systemctl to avoid real service checks.
  env BACKUP_DIR="${backup}" \
      EXPORT_HOME="${export_home}" \
      CONFIG_FILE="${config}" \
      REPO_DIR="${repo}" \
      PATH="${d}/bin:${PATH}" \
      bash "${RESTORE}" 2>&1
}

# Stub systemctl so it reports t3code-serve as not enabled (triggers reinstall path).
make_systemctl_stub() {
  local d="$1"
  mkdir -p "${d}/bin"
  cat > "${d}/bin/systemctl" <<'STUB'
#!/bin/bash
# Stub: report not-enabled for t3code-serve, succeed otherwise
if [[ "$*" == *"is-enabled"*"t3code-serve"* ]]; then exit 1; fi
exit 0
STUB
  chmod +x "${d}/bin/systemctl"
}

# ── disabled + nightly: channel preference preserved, no install ─────────────
d="$(setup_fixture "disabled-nightly" "false" "nightly")"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "disabled+nightly: T3CODE_CHANNEL set to nightly in config.env" \
  test "$(read_config_key "${d}/config/config.env" T3CODE_CHANNEL)" = "nightly"
ok "disabled+nightly: T3CODE flag NOT set (not installed)" \
  test -z "$(read_config_key "${d}/config/config.env" T3CODE)"
ok "disabled+nightly: install-ai-tools.sh NOT invoked (disabled = skip install)" \
  test ! -f "${d}/install-invocations"

# ── enabled + nightly: channel preference + install ──────────────────────────
d="$(setup_fixture "enabled-nightly" "true" "nightly")"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "enabled+nightly: T3CODE_CHANNEL set to nightly" \
  test "$(read_config_key "${d}/config/config.env" T3CODE_CHANNEL)" = "nightly"
ok "enabled+nightly: T3CODE set to true (installed)" \
  test "$(read_config_key "${d}/config/config.env" T3CODE)" = "true"
ok "enabled+nightly: install-ai-tools.sh invoked exactly once" \
  test "$(wc -l < "${d}/install-invocations" 2>/dev/null)" -eq 1
ok "enabled+nightly: installer received T3CODE_CHANNEL=nightly" \
  test "$(cat "${d}/install-invocations" 2>/dev/null)" = "nightly"

# ── enabled + stable: defaults to stable ────────────────────────────────────
d="$(setup_fixture "enabled-stable" "true" "stable")"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "enabled+stable: T3CODE_CHANNEL set to stable" \
  test "$(read_config_key "${d}/config/config.env" T3CODE_CHANNEL)" = "stable"
ok "enabled+stable: installer received T3CODE_CHANNEL=stable" \
  test "$(cat "${d}/install-invocations" 2>/dev/null)" = "stable"

# ── missing channel in backup: defaults to stable ───────────────────────────
d="$(setup_fixture "no-channel" "true" "")"
# Overwrite backup-info.json without t3codeChannel
printf '{"t3code":true}\n' > "${d}/backup/backup-info.json"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "missing channel: T3CODE_CHANNEL defaults to stable" \
  test "$(read_config_key "${d}/config/config.env" T3CODE_CHANNEL)" = "stable"

# ── hostile channel value: normalized to stable ────────────────────────────
d="$(setup_fixture "hostile-channel" "false" "nightly'; rm -rf /")"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "hostile channel: normalized to stable (not nightly)" \
  test "$(read_config_key "${d}/config/config.env" T3CODE_CHANNEL)" = "stable"

# ── OpenCode watcher metadata: regenerated, not copied from backup ───────────
d="$(setup_fixture "opencode-watcher" "false" "stable")"
printf '{"t3code":false,"t3codeChannel":"stable","opencodeBackgroundWatcher":true}\n' \
  > "${d}/backup/backup-info.json"
cp "${ROOT}/bin/install-ai-tools.sh" "${d}/repo/bin/install-ai-tools.sh"
mkdir -p "${d}/repo/extension/vm"
cp "${ROOT}/extension/vm/opencode-background.js" "${d}/repo/extension/vm/opencode-background.js"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
watcher_target="${d}/export_home/.config/opencode/plugins/background.js"
ok "watcher metadata: preference restored into config.env" \
  test "$(read_config_key "${d}/config/config.env" OPENCODE_BACKGROUND_WATCHER)" = "true"
ok "watcher metadata: managed plugin regenerated from current Construct" test -f "${watcher_target}"
ok "watcher metadata: regenerated plugin excludes Cortecs behavior" sh -c \
  "! grep -Eq 'enable_model_fallback|chat\\.params|providerID' '${watcher_target}'"

# Backward compatibility: an old backup without the key must not override a
# preference already selected for the fresh VM by the panel/rebuild command.
d="$(setup_fixture "old-backup-keeps-watcher" "false" "stable")"
printf '%s\n' 'OPENCODE_BACKGROUND_WATCHER=true' > "${d}/config/config.env"
make_systemctl_stub "${d}"
run_restore "${d}" >/dev/null 2>&1
ok "old backup: absent watcher key leaves the current preference untouched" \
  test "$(read_config_key "${d}/config/config.env" OPENCODE_BACKGROUND_WATCHER)" = "true"

# Tarball restores stream home/ directly into the destination. This avoids the
# old extract-then-copy path and its second full uncompressed history tree.
d="$(setup_fixture "streamed-tarball" "false" "stable")"
mkdir -p "${d}/backup/home/.codex"
printf 'streamed\n' >"${d}/backup/home/.codex/AGENTS.md"
tar -czf "${d}/backup.tar.gz" -C "${d}/backup" .
make_systemctl_stub "${d}"
env BACKUP_TGZ="${d}/backup.tar.gz" \
    EXPORT_HOME="${d}/export_home" \
    CONFIG_FILE="${d}/config/config.env" \
    REPO_DIR="${d}/repo" \
    PATH="${d}/bin:${PATH}" \
    bash "${RESTORE}" >/dev/null 2>&1
ok "tarball: streams home tree into EXPORT_HOME" \
  grep -qx streamed "${d}/export_home/.codex/AGENTS.md"
ok "tarball: does not nest restored files under home/" \
  test ! -e "${d}/export_home/home"

# Any failure after services are paused must restore their prior state. The old
# script exited under set -e and stranded both services stopped.
d="$(setup_fixture "failure-restarts-services" "false" "stable")"
mkdir -p "${d}/backup/home/.codex/sessions" "${d}/backup/home/.t3/userdata" "${d}/bin"
touch "${d}/backup/home/.t3/userdata/state.sqlite"
cat >"${d}/bin/systemctl" <<STUB
#!/bin/bash
if [[ "\$1" == "is-active" ]]; then echo active; exit 0; fi
if [[ "\$1" == "start" ]]; then echo "\$2" >>"${d}/service-starts"; fi
exit 0
STUB
cat >"${d}/bin/cp" <<'STUB'
#!/bin/bash
exit 7
STUB
chmod +x "${d}/bin/systemctl" "${d}/bin/cp"
if run_restore "${d}" >/dev/null 2>&1; then restore_failed=false; else restore_failed=true; fi
ok "failure cleanup: restore still reports the overlay failure" test "${restore_failed}" = true
ok "failure cleanup: T3 service is restarted" grep -qx t3code-serve "${d}/service-starts"
ok "failure cleanup: Codex service is restarted" grep -qx codex-app-server "${d}/service-starts"

# A bare command failure under `set -e` must identify the restore-script line;
# otherwise the Windows wrapper can only report an unhelpful remote exit code.
d="$(setup_fixture "diagnostic-on-error" "false" "stable")"
printf '{not-json}\n' >"${d}/backup/backup-info.json"
make_systemctl_stub "${d}"
if diagnostic_output="$(CONSTRUCT_VERSION=abc1234 run_restore "${d}" 2>&1)"; then
  diagnostic_failed=false
else
  diagnostic_failed=true
fi
ok "diagnostics: malformed metadata still fails the restore" test "${diagnostic_failed}" = true
ok "diagnostics: failure includes the restore-script line" sh -c \
  "printf '%s' \"\$1\" | grep -Eq 'Restore failed at restore-config.sh line [0-9]+ \\(exit [0-9]+\\)'" _ "${diagnostic_output}"
ok "diagnostics: output identifies the Construct revision" sh -c \
  "printf '%s' \"\$1\" | grep -Fq 'Construct restore revision: abc1234'" _ "${diagnostic_output}"

printf '\n  restore-config fixture tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[ "${fail}" -eq 0 ] || exit 1
exit 0
