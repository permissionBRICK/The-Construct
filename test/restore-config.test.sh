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

printf '\n  restore-config fixture tests — %d/%d passed\n\n' "${pass}" "$((pass + fail))"
[ "${fail}" -eq 0 ] || exit 1
exit 0
