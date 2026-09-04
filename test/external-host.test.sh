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

# HERMETIC ENVIRONMENT. The scripts under test resolve their identity
# "environment > config.env > default", and this suite is usually run ON a provisioned
# Construct VM -- whose login environment exports T3CODE, T3CODE_HTTPS,
# T3CODE_PUBLIC_BASE_URL and friends. Inherited, they answer the very questions under
# test: the byte-diff below would compare the base commit's banner (which had no T3
# HTTPS block) against a banner rendered from the HOST's own T3 origin. Drop the whole
# family; every case that needs a value sets it explicitly.
while IFS='=' read -r _leaked _; do
  [[ -n "${_leaked}" ]] && unset "${_leaked}"
done < <(env | grep -E '^(T3CODE|CONSTRUCT|OPENCODE)[A-Z0-9_]*=' || true)
unset _leaked

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

# ── bin/provision.sh: the host-forward status file (plan §4.12) ───────────────
# A SERVICE-MANAGED VM asks the host service to forward its web services and records the
# result where the host provisioner reads it back. A LOCAL VM must do none of that: no
# request, no file, no output — which is the same zero-change bar the banner cases above
# hold. Extracted from the shipped script (its body provisions a machine, so it cannot be
# sourced), exactly like test/provision-hostname.test.sh does.

PROVISION="${ROOT}/bin/provision.sh"

# Both halves of the shipped gate, asserted on the source: the step only runs for a
# service-managed VM, and a VM that is not one has its stale file removed silently.
ok "service ca: provision.sh persists CONSTRUCT_SERVICE_CA_B64 as /etc/construct/service-ca.pem" \
  bash -c "grep -q 'CONSTRUCT_SERVICE_CA_B64' '${PROVISION}' && grep -q 'cfg CONSTRUCT_SERVICE_CA_FILE /etc/construct/service-ca.pem' '${PROVISION}'"
ok "service ca: ...only inside the service-managed block" \
  bash -c "awk '/if \\[\\[ -n \"\\\$\\{CONSTRUCT_SERVICE_URL\\}\" \\]\\]; then/{f=1} f && /CONSTRUCT_SERVICE_CA_B64/{print \"inside\"; exit}' '${PROVISION}' | grep -q inside"
ok "host forwards: the step is gated on CONSTRUCT_SERVICE_URL" \
  sh -c "grep -A2 'run_step optional \"Requesting host port forwards' '${PROVISION}' >/dev/null && \
         grep -B4 'run_step optional \"Requesting host port forwards' '${PROVISION}' | grep -q 'if \[\[ -n \"\${CONSTRUCT_SERVICE_URL}\" \]\]'"
ok "host forwards: a local VM removes any stale status file, silently" \
  sh -c "grep -A7 'run_step optional \"Requesting host port forwards' '${PROVISION}' | grep -q 'rm -f \"\${HOST_FORWARDS_FILE}\"'"
ok "host forwards: the request goes through construct expose --to host --reuse" \
  grep -q -- '--to host --reuse' "${PROVISION}"

hf_lib="${tmp}/hostforwards.sh"
{
  sed -n '/^_cfg_unquote() {$/,/^}$/p' "${PROVISION}"
  sed -n '/^host_forward_authority() {$/,/^}$/p' "${PROVISION}"
  sed -n '/^setup_host_forwards() {$/,/^}$/p' "${PROVISION}"
  sed -n '/^record_t3_forward_url() {$/,/^}$/p' "${PROVISION}"
} >"${hf_lib}"
# A stub repo whose construct-expose.sh records its argv and answers from a script the
# test writes — so nothing here talks to a service, and the `--reuse` contract is
# asserted at the boundary provision.sh actually uses.
hf_repo="${tmp}/hf-repo"
mkdir -p "${hf_repo}/bin"
cat >"${hf_repo}/bin/construct-expose.sh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"${EXPOSE_LOG}"
port="$1"
answer="${EXPOSE_DIR}/answer-${port}"
[[ -f "${answer}" ]] || answer="${EXPOSE_DIR}/answer"
code_file="${EXPOSE_DIR}/code-${port}"
[[ -f "${code_file}" ]] || code_file="${EXPOSE_DIR}/code"
[[ -f "${answer}" ]] && cat "${answer}"
if [[ -f "${code_file}" ]]; then exit "$(cat "${code_file}")"; fi
exit 0
STUB
chmod +x "${hf_repo}/bin/construct-expose.sh"

run_host_forwards() { # <ai_tools> <t3code> <t3code_https>
  (
    set -u
    # The provisioner's reporting helpers, silenced: the assertions are about the FILE.
    ok()   { :; }
    warn() { printf '%s\n' "$*" >>"${EXPOSE_DIR}/warnings"; }
    REPO_DIR="${hf_repo}"
    CONFIG_FILE="${tmp}/hf-config.env"
    HOST_FORWARDS_FILE="${hf_out}"
    AI_TOOLS="$1"; T3CODE="$2"; T3CODE_HTTPS="$3"
    OPENCODE_PORT=4096; T3CODE_PORT=5177; T3CODE_HTTPS_PORT=5178
    # shellcheck source=/dev/null
    . "${hf_lib}"
    setup_host_forwards
  )
}

export EXPOSE_DIR="${tmp}/expose"
export EXPOSE_LOG="${EXPOSE_DIR}/argv"
mkdir -p "${EXPOSE_DIR}"
: >"${tmp}/hf-config.env"
hf_out="${tmp}/host-forwards"

printf 'http://work-vm.vpn.example:2301/\n' >"${EXPOSE_DIR}/answer-4096"
printf 'http://work-vm.vpn.example:2302/\n' >"${EXPOSE_DIR}/answer-5178"
run_host_forwards "opencode,claude-code" "true" "true"

ok "host forwards: OPENCODE is recorded as host:port" \
  grep -qx 'OPENCODE=work-vm.vpn.example:2301' "${hf_out}"
ok "host forwards: T3 is recorded as host:port" \
  grep -qx 'T3=work-vm.vpn.example:2302' "${hf_out}"
ok "host forwards: the T3 request is for the HTTPS listener" \
  grep -q '^5178 --to host --reuse' "${EXPOSE_LOG}"
ok "host forwards: every request is get-or-create" \
  sh -c "! grep -v -- '--reuse' '${EXPOSE_LOG}' | grep -q ."
ok "host forwards: the file is only these two lines" test "$(wc -l <"${hf_out}")" = 2

# Re-running provisioning must produce the SAME file, not a second forward: the
# idempotence lives in --reuse, which the CLI's own suite pins.
cp "${hf_out}" "${tmp}/host-forwards.first"
run_host_forwards "opencode,claude-code" "true" "true"
ok "host forwards: a second provision writes an identical file" \
  cmp -s "${tmp}/host-forwards.first" "${hf_out}"

# Plain HTTP T3 asks for the plain listener instead.
: >"${EXPOSE_LOG}"
printf 'http://work-vm.vpn.example:2305/\n' >"${EXPOSE_DIR}/answer-5177"
run_host_forwards "opencode" "true" "false"
ok "host forwards: T3 without HTTPS asks for the plain port" \
  grep -q '^5177 --to host --reuse' "${EXPOSE_LOG}"

# A service the VM does not run asks for nothing, and its key is simply absent.
run_host_forwards "claude-code,codex" "false" "true"
ok "host forwards: no opencode, no T3 -> an empty file" test ! -s "${hf_out}"

# Refused (exit 7) is recorded as such: the host provisioner prints the manual path
# instead of a URL that cannot work.
printf '7' >"${EXPOSE_DIR}/code-4096"
: >"${EXPOSE_DIR}/answer-4096"
run_host_forwards "opencode" "false" "true"
ok "host forwards: a refusal is recorded as 'denied'" grep -qx 'OPENCODE=denied' "${hf_out}"
ok "host forwards: ...and reads back as no URL" \
  sh -c "test \"\$(sed -n 's/^OPENCODE=//p' '${hf_out}')\" = denied"

# Any other failure is 'error' — NOT 'denied': the user is not the reason.
printf '8' >"${EXPOSE_DIR}/code-4096"
run_host_forwards "opencode" "false" "true"
ok "host forwards: a service failure is recorded as 'error'" grep -qx 'OPENCODE=error' "${hf_out}"
rm -f "${EXPOSE_DIR}/code-4096"

# host_forward_authority: what provision.sh itself reads back to build T3's public port.
printf 'T3=work-vm.vpn.example:2302\nOPENCODE=denied\n' >"${hf_out}"
ok "host forwards: the authority of a recorded key is read back" \
  bash -c "HOST_FORWARDS_FILE='${hf_out}'; . '${hf_lib}'; test \"\$(host_forward_authority T3)\" = 'work-vm.vpn.example:2302'"
ok "host forwards: a denied key reads back as nothing" \
  bash -c "HOST_FORWARDS_FILE='${hf_out}'; . '${hf_lib}'; test -z \"\$(host_forward_authority OPENCODE)\""
ok "host forwards: an absent file reads back as nothing" \
  bash -c "HOST_FORWARDS_FILE='${tmp}/no-such-file'; . '${hf_lib}'; test -z \"\$(host_forward_authority T3)\""

# ── The EFFECTIVE forwarded T3 origin ─────────────────────────────────────────
# The allocated forward alone is AMBIGUOUS: it is requested for the TLS port whenever
# HTTPS is wanted, BEFORE setup-t3-https.sh runs, and every failure path in that script
# clears the advertised origin while the forward stays. So the guest records what T3 was
# really told to advertise, and the host provisioner prints only that.

run_record_t3() { # <T3CODE_PUBLIC_BASE_URL line, or "" for none>
  (
    set -u
    ok()   { :; }
    warn() { printf '%s\n' "$*" >>"${EXPOSE_DIR}/warnings"; }
    CONFIG_FILE="${tmp}/hf-t3-config.env"
    HOST_FORWARDS_FILE="${hf_out}"
    : >"${CONFIG_FILE}"
    [[ -n "$1" ]] && printf 'T3CODE_PUBLIC_BASE_URL=%s\n' "$1" >>"${CONFIG_FILE}"
    # shellcheck source=/dev/null
    . "${hf_lib}"
    record_t3_forward_url
  )
}
t3url() { sed -n 's/^T3_URL=//p' "${hf_out}" | head -1; }

# HTTPS came up: the origin names the forwarded port, so it IS the answer.
printf 'T3=work-vm.vpn.example:2302\n' >"${hf_out}"
run_record_t3 "https://work-vm.vpn.example:2302"
ok "t3 origin: an https origin on the forwarded port is recorded" \
  test "$(t3url)" = "https://work-vm.vpn.example:2302"
ok "t3 origin: the forward line survives the rewrite" grep -qx 'T3=work-vm.vpn.example:2302' "${hf_out}"

# HTTPS deliberately OFF: the plain listener is forwarded and really serves it.
printf 'T3=work-vm.vpn.example:2305\n' >"${hf_out}"
run_record_t3 "http://work-vm.vpn.example:2305"
ok "t3 origin: a plain-http origin on the forwarded port is recorded too" \
  test "$(t3url)" = "http://work-vm.vpn.example:2305"

# HTTPS REQUESTED BUT THE SETUP FAILED: setup-t3-https.sh cleared the origin, the forward
# for the TLS port stays. Nothing may be advertised -- this is the case that would
# otherwise print an http:// URL for a TLS port nothing serves.
printf 'T3=work-vm.vpn.example:2302\n' >"${hf_out}"
run_record_t3 ""
ok "t3 origin: a failed HTTPS setup records NO url" test -z "$(t3url)"
ok "t3 origin: ...while the allocated forward is still recorded" \
  grep -qx 'T3=work-vm.vpn.example:2302' "${hf_out}"

# An origin on the VM's OWN port is not something a client can reach.
printf 'T3=work-vm.vpn.example:2302\n' >"${hf_out}"
run_record_t3 "https://work-vm.vpn.example:5178"
ok "t3 origin: an origin on the VM-internal port is not advertised" test -z "$(t3url)"

# A stale T3_URL from an earlier provision must not survive a run that has no origin.
printf 'T3=work-vm.vpn.example:2302\nT3_URL=https://work-vm.vpn.example:2302\n' >"${hf_out}"
run_record_t3 ""
ok "t3 origin: a stale url from an earlier provision is dropped" test -z "$(t3url)"

# Denied / no forward at all: nothing to record, and the file is left as it stands.
printf 'T3=denied\n' >"${hf_out}"
run_record_t3 "https://work-vm.vpn.example:2302"
ok "t3 origin: a denied forward records no url" test -z "$(t3url)"
ok "t3 origin: ...and the denial is preserved" grep -qx 'T3=denied' "${hf_out}"

# ── Summary ───────────────────────────────────────────────────────────────────
printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
