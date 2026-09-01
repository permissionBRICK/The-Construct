#!/usr/bin/env bash
# Tests for CONSTRUCT_EXTERNAL_HOST / CONSTRUCT_EXTERNAL_SSH_PORT identity
# resolution in the guest scripts that previously hardcoded $(hostname).mshome.net.
#
# Covered scripts:
#   bin/print-connection-info.sh  -- full banner (no root/systemd deps)
#   bin/setup-root-ssh-key.sh     -- SSH config snippet via --snippet flag
#
# Run: bash test/external-host.test.sh

set -u

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

# ── Sandbox setup ─────────────────────────────────────────────────────────────

# Stub hostname to return a predictable value for both base and new scripts.
mkdir -p "${tmp}/stubs"
printf '#!/bin/sh\necho testvm\n' >"${tmp}/stubs/hostname"
chmod +x "${tmp}/stubs/hostname"
stub_path="${tmp}/stubs:${PATH}"
# Make the Codex and VS Code banner sections render (they gate on the commands
# existing) so their descriptive SSH-target lines are covered; the default-vs-base
# diff below runs both scripts in this same sandbox, so it stays a fair comparison.
for _cmd in codex code; do
  printf '#!/usr/bin/env bash\nexit 0\n' >"${tmp}/stubs/${_cmd}"
  chmod +x "${tmp}/stubs/${_cmd}"
done

# Default config.env: both keys absent (simulates a fresh install or env cleared).
default_cfg="${tmp}/config_default.env"
printf '' >"${default_cfg}"

# Override config.env: both keys set to non-default values.
override_cfg="${tmp}/config_override.env"
printf 'CONSTRUCT_EXTERNAL_HOST=myhost.example.com\n' >"${override_cfg}"
printf 'CONSTRUCT_EXTERNAL_SSH_PORT=2201\n'           >>"${override_cfg}"

# Extract base script from the base commit for regression comparison.
base_pci="${tmp}/base_print_connection_info.sh"
git show b78787e:bin/print-connection-info.sh >"${base_pci}" 2>/dev/null \
  || { printf 'WARN: could not extract base commit script; skipping byte-diff\n'; base_pci=""; }

# ── print-connection-info.sh: DEFAULT case ────────────────────────────────────
# Run new script with no external-host vars; output must match the base commit.

new_pci_default="${tmp}/new_pci_default.out"
PATH="${stub_path}" CONFIG_FILE="${default_cfg}" \
  bash "${ROOT}/bin/print-connection-info.sh" >"${new_pci_default}" 2>/dev/null || true

if [[ -n "${base_pci}" ]]; then
  base_pci_out="${tmp}/base_pci.out"
  PATH="${stub_path}" CONFIG_FILE="${default_cfg}" \
    bash "${base_pci}" >"${base_pci_out}" 2>/dev/null || true

  ok "print-connection-info: DEFAULT output byte-identical to base commit" \
    diff -q "${base_pci_out}" "${new_pci_default}"
fi

ok "print-connection-info: DEFAULT contains mshome fallback" \
  grep -q 'testvm\.mshome\.net' "${new_pci_default}"

ok "print-connection-info: DEFAULT SSH line has no -p flag" \
  sh -c "grep -E '^  ssh [^-]' '${new_pci_default}' | grep -q 'testvm\.mshome\.net'"

# ── print-connection-info.sh: OVERRIDE case ───────────────────────────────────
# Run new script with external host + non-standard SSH port.

new_pci_override="${tmp}/new_pci_override.out"
PATH="${stub_path}" CONFIG_FILE="${override_cfg}" \
  bash "${ROOT}/bin/print-connection-info.sh" >"${new_pci_override}" 2>/dev/null || true

ok "print-connection-info: OVERRIDE uses external host" \
  grep -q 'myhost\.example\.com' "${new_pci_override}"

ok "print-connection-info: OVERRIDE does not contain mshome fallback" \
  sh -c "! grep -q 'testvm\.mshome\.net' '${new_pci_override}'"

ok "print-connection-info: OVERRIDE SSH line includes -p port flag" \
  grep -q -- '-p 2201' "${new_pci_override}"

ok "print-connection-info: OVERRIDE SSH line uses external host with port" \
  sh -c "grep -E '^  ssh ' '${new_pci_override}' | grep -q 'myhost\.example\.com' && \
         grep -E '^  ssh ' '${new_pci_override}' | grep -q -- '-p 2201'"

ok "print-connection-info: OVERRIDE DNS line uses external host" \
  grep -q 'DNS:.*myhost\.example\.com' "${new_pci_override}"

# HTTP service URLs keep their service ports and are not affected by ssh_port.
# The opencode/codex sections only appear when those tools are installed, so we
# assert absence of the SSH port on any http:// lines that do appear.
ok "print-connection-info: OVERRIDE no SSH port on HTTP URLs" \
  sh -c "! grep -oP 'http://[^:]+:\K[0-9]+' '${new_pci_override}' | grep -qx '2201'"

# A direct caller override must win over a persisted value.
ok "print-connection-info: OVERRIDE descriptive SSH target carries the port note" \
  grep -q 'SSH target: .*@myhost\.example\.com (port 2201)$' "${new_pci_override}"

ok "print-connection-info: OVERRIDE Remote-SSH hint carries the port note" \
  grep -q 'Remote Explorer -> SSH -> myhost\.example\.com (port 2201)$' "${new_pci_override}"

new_pci_env_override="${tmp}/new_pci_env_override.out"
PATH="${stub_path}" CONFIG_FILE="${override_cfg}" \
  CONSTRUCT_EXTERNAL_HOST=env.example.net CONSTRUCT_EXTERNAL_SSH_PORT=2299 \
  bash "${ROOT}/bin/print-connection-info.sh" >"${new_pci_env_override}" 2>/dev/null || true

ok "print-connection-info: environment host overrides config.env" \
  sh -c "grep -q 'env\\.example\\.net' '${new_pci_env_override}' && ! grep -q 'myhost\\.example\\.com' '${new_pci_env_override}'"

ok "print-connection-info: environment SSH port overrides config.env" \
  sh -c "grep -q -- '-p 2299' '${new_pci_env_override}' && ! grep -q -- '-p 2201' '${new_pci_env_override}'"

# ── setup-root-ssh-key.sh: DEFAULT case (snippet-only mode) ──────────────────
# SETUP_SSH_KEY_SNIPPET_ONLY=true prints just the SSH config snippet without
# generating keys, touching authorized_keys, or managing sshd.

snippet_default="${tmp}/snippet_default.out"
PATH="${stub_path}" CONFIG_FILE="${default_cfg}" bash "${ROOT}/bin/setup-root-ssh-key.sh" --snippet >"${snippet_default}" 2>/dev/null

ok "setup-root-ssh-key: DEFAULT snippet host alias uses hostname" \
  grep -q '^Host testvm-root$' "${snippet_default}"

ok "setup-root-ssh-key: DEFAULT snippet HostName is mshome fallback" \
  grep -q '  HostName testvm\.mshome\.net$' "${snippet_default}"

ok "setup-root-ssh-key: DEFAULT snippet has no Port line" \
  sh -c "! grep -q '^  Port' '${snippet_default}'"

ok "setup-root-ssh-key: DEFAULT snippet has User root" \
  grep -q '  User root' "${snippet_default}"

# Verify DEFAULT snippet content matches what the base-commit script would produce.
# The base-commit snippet (from setup-root-ssh-key.sh line ~99-103) always emits:
#   HostName <hostname>.mshome.net  with no Port line.
ok "setup-root-ssh-key: DEFAULT snippet matches base-commit expectation" \
  sh -c "grep -q 'HostName testvm\.mshome\.net' '${snippet_default}' && \
         grep -q 'User root' '${snippet_default}' && \
         ! grep -q 'Port' '${snippet_default}'"

# ── setup-root-ssh-key.sh: OVERRIDE case (snippet-only mode) ─────────────────

snippet_override="${tmp}/snippet_override.out"
PATH="${stub_path}" CONFIG_FILE="${override_cfg}" bash "${ROOT}/bin/setup-root-ssh-key.sh" --snippet >"${snippet_override}" 2>/dev/null

ok "setup-root-ssh-key: OVERRIDE snippet uses external host" \
  grep -q '  HostName myhost\.example\.com$' "${snippet_override}"

ok "setup-root-ssh-key: OVERRIDE snippet does not contain mshome fallback" \
  sh -c "! grep -q 'mshome\.net' '${snippet_override}'"

ok "setup-root-ssh-key: OVERRIDE snippet has Port line" \
  grep -q '  Port 2201$' "${snippet_override}"

ok "setup-root-ssh-key: OVERRIDE snippet host alias still uses local hostname" \
  grep -q '^Host testvm-root$' "${snippet_override}"

snippet_env_override="${tmp}/snippet_env_override.out"
PATH="${stub_path}" CONFIG_FILE="${override_cfg}" \
  CONSTRUCT_EXTERNAL_HOST=env.example.net CONSTRUCT_EXTERNAL_SSH_PORT=2299 \
  bash "${ROOT}/bin/setup-root-ssh-key.sh" --snippet >"${snippet_env_override}" 2>/dev/null

ok "setup-root-ssh-key: environment host overrides config.env" \
  sh -c "grep -q '  HostName env\\.example\\.net$' '${snippet_env_override}' && ! grep -q 'myhost\\.example\\.com' '${snippet_env_override}'"

ok "setup-root-ssh-key: environment SSH port overrides config.env" \
  sh -c "grep -q '^  Port 2299$' '${snippet_env_override}' && ! grep -q '^  Port 2201$' '${snippet_env_override}'"

# --snippet must be side-effect free: stub every privileged tool the real path uses
# so any call is recorded, then assert nothing was recorded. (Without --snippet this
# script generates keys, edits /root/.ssh/authorized_keys and restarts sshd -- a test
# must never take that path.)
guard_stubs="${tmp}/guard-stubs"
mkdir -p "${guard_stubs}"
for _tool in ssh-keygen systemctl sshd install; do
  printf '#!/usr/bin/env bash\nprintf "%%s %%s\\n" "%s" "$*" >> "%s/guard-calls"\nexit 0\n' "${_tool}" "${tmp}" >"${guard_stubs}/${_tool}"
  chmod +x "${guard_stubs}/${_tool}"
done
PATH="${guard_stubs}:${stub_path}" CONFIG_FILE="${override_cfg}" \
  bash "${ROOT}/bin/setup-root-ssh-key.sh" --snippet >/dev/null 2>&1
ok "setup-root-ssh-key: --snippet invokes no privileged tool" \
  sh -c "! test -e '${tmp}/guard-calls'"

# A value config-set.sh had to single-quote (here a scoped IPv6 address with '%')
# must come back verbatim from the narrow reader -- no quote characters leak.
quoted_cfg="${tmp}/config_quoted.env"
bash "${ROOT}/bin/config-set.sh" "${quoted_cfg}" CONSTRUCT_EXTERNAL_HOST "fe80::1%12"
bash "${ROOT}/bin/config-set.sh" "${quoted_cfg}" CONSTRUCT_EXTERNAL_SSH_PORT "2222"
ok "config-set.sh quoted the scoped IPv6 value (test precondition)" \
  grep -q "^CONSTRUCT_EXTERNAL_HOST='fe80::1%12'$" "${quoted_cfg}"
snippet_quoted="${tmp}/snippet_quoted.out"
PATH="${stub_path}" CONFIG_FILE="${quoted_cfg}" bash "${ROOT}/bin/setup-root-ssh-key.sh" --snippet >"${snippet_quoted}" 2>/dev/null
ok "setup-root-ssh-key: quoted saved host is decoded (no quote characters)" \
  sh -c "grep -q '^  HostName fe80::1%12$' '${snippet_quoted}' && ! grep -q \"'\" '${snippet_quoted}'"
ok "setup-root-ssh-key: quoted saved port is decoded" \
  grep -q '^  Port 2222$' "${snippet_quoted}"

# config.env must not be able to redirect the key path or sshd drop-in (the file is
# read with a narrow key lookup, not sourced).
poison_cfg="${tmp}/config_poison.env"
printf 'CONSTRUCT_EXTERNAL_HOST=poison.example.net\nKEY_PATH=/tmp/evil\nSSHD_DROPIN=/tmp/evil.conf\n' >"${poison_cfg}"
poison_out="${tmp}/poison.out"
PATH="${stub_path}" CONFIG_FILE="${poison_cfg}" bash -c '
  set -euo pipefail
  # shellcheck disable=SC1090
  source <(sed -n "1,/^_snippet_only=false/p" "$1" | grep -v "^if \[\[ \"\${EUID}\"" )
  printf "%s|%s|%s\n" "${KEY_PATH}" "${SSHD_DROPIN}" "${external_host}"
' _ "${ROOT}/bin/setup-root-ssh-key.sh" >"${poison_out}" 2>/dev/null || true
ok "setup-root-ssh-key: config.env cannot override KEY_PATH/SSHD_DROPIN" \
  sh -c "grep -q '^/root/.ssh/codex_app_ed25519|/etc/ssh/sshd_config.d/99-construct-root-pubkey.conf|poison.example.net$' '${poison_out}'"

# ── print-connection-info.sh: IPv6 OVERRIDE case ────────────────────────────
# When CONSTRUCT_EXTERNAL_HOST is an IPv6 address, HTTP/WS URLs must bracket it
# (e.g. http://[2001:db8::1]:4096) while SSH/DNS contexts use the raw address.

ipv6_cfg="${tmp}/config_ipv6.env"
printf 'CONSTRUCT_EXTERNAL_HOST=2001:db8::1\n' >"${ipv6_cfg}"
printf 'CONSTRUCT_EXTERNAL_SSH_PORT=2201\n'     >>"${ipv6_cfg}"

new_pci_ipv6="${tmp}/new_pci_ipv6.out"
PATH="${stub_path}" CONFIG_FILE="${ipv6_cfg}" \
  bash "${ROOT}/bin/print-connection-info.sh" >"${new_pci_ipv6}" 2>/dev/null || true

ok "print-connection-info: IPv6 HTTP URLs are bracketed" \
  grep -q 'http://\[2001:db8::1\]:' "${new_pci_ipv6}"

ok "print-connection-info: IPv6 DNS line uses raw address (no brackets)" \
  grep -q 'DNS:.*2001:db8::1' "${new_pci_ipv6}"

ok "print-connection-info: IPv6 SSH line uses raw address" \
  sh -c "grep -E '^  ssh ' '${new_pci_ipv6}' | head -1 | grep -q '2001:db8::1' && \
         grep -E '^  ssh ' '${new_pci_ipv6}' | head -1 | grep -qv '\[2001:db8::1\]'"

ok "print-connection-info: IPv6 SSH line includes -p port flag" \
  sh -c "grep -E '^  ssh ' '${new_pci_ipv6}' | grep -q -- '-p 2201'"

# ── Summary ───────────────────────────────────────────────────────────────────
printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
