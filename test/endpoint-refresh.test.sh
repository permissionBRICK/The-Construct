#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -r "${work}"' EXIT
export CONFIG_FILE="${work}/config.env" CONSTRUCT_VM_TOKEN_FILE="${work}/token"
export CONSTRUCT_CURL="${work}/curl" CONSTRUCT_CLI="${work}/construct" REFRESH_TEST_WORK="${work}"
printf 'fixture-token\n' >"${CONSTRUCT_VM_TOKEN_FILE}"
cat >"${CONSTRUCT_CURL}" <<'STUB'
#!/usr/bin/env python3
import os, pathlib, sys
w = pathlib.Path(os.environ['REFRESH_TEST_WORK'])
args = sys.argv[1:]
assert 'fixture-token' not in ' '.join(args)
assert pathlib.Path(args[args.index('-H')+1][1:]).stat().st_mode & 0o777 == 0o600
assert 'fixture-token' in pathlib.Path(args[args.index('-H')+1][1:]).read_text()
assert args[-1] == 'https://fixture.invalid/api/v1/vms/work-vm/endpoint'
assert args[args.index('--cacert')+1] == '/fixture/ca.pem'
if (w/'fail').exists(): sys.exit(22)
pathlib.Path(args[args.index('-o')+1]).write_text((w/'response').read_text())
STUB
cat >"${CONSTRUCT_CLI}" <<'STUB'
#!/usr/bin/env bash
[[ "$1" == systemprompt ]]
printf '%s\n' "${CONSTRUCT_EXTERNAL_HOST}" >>"${REFRESH_TEST_WORK}/renders"
STUB
chmod +x "${CONSTRUCT_CURL}" "${CONSTRUCT_CLI}"
: >"${CONFIG_FILE}"
bash "${repo}/bin/construct-endpoint-refresh.sh"
[[ ! -f "${work}/renders" ]]
cat >"${CONFIG_FILE}" <<'CFG'
CONSTRUCT_SERVICE_URL='https://fixture.invalid'
CONSTRUCT_INSTANCE_NAME='work-vm'
CONSTRUCT_SERVICE_CA_FILE='/fixture/ca.pem'
CONSTRUCT_EXTERNAL_HOST='old.invalid'
CONSTRUCT_EXTERNAL_SSH_PORT='2201'
KEEP='untouched value'
CFG
printf '{"sshHost":"203.0.113.50","sshPort":22,"publicHost":"203.0.113.50"}' >"${work}/response"
bash "${repo}/bin/construct-endpoint-refresh.sh"
grep -qx 'CONSTRUCT_EXTERNAL_HOST=203.0.113.50' "${CONFIG_FILE}"
grep -qx 'CONSTRUCT_EXTERNAL_SSH_PORT=22' "${CONFIG_FILE}"
grep -qx "KEEP='untouched value'" "${CONFIG_FILE}"
grep -qx '203.0.113.50' "${work}/renders"
before="$(stat -c '%y' "${CONFIG_FILE}")"
bash "${repo}/bin/construct-endpoint-refresh.sh"
[[ "$(stat -c '%y' "${CONFIG_FILE}")" == "${before}" ]]
cp "${CONFIG_FILE}" "${work}/before"
touch "${work}/fail"
if bash "${repo}/bin/construct-endpoint-refresh.sh"; then exit 1; fi
cmp "${CONFIG_FILE}" "${work}/before"
rm "${work}/fail"
printf '{"sshHost":"bad;command","sshPort":22}' >"${work}/response"
if bash "${repo}/bin/construct-endpoint-refresh.sh"; then exit 1; fi
cmp "${CONFIG_FILE}" "${work}/before"
printf '{"sshHost":"node.invalid","sshPort":2201,"publicHost":"vm.invalid"}' >"${work}/response"
bash "${repo}/bin/construct-endpoint-refresh.sh"
grep -qx 'CONSTRUCT_EXTERNAL_HOST=vm.invalid' "${CONFIG_FILE}"
grep -qx 'CONSTRUCT_EXTERNAL_SSH_PORT=2201' "${CONFIG_FILE}"
CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${repo}" CONSTRUCT_EXTERNAL_HOST=203.0.113.50 \
  bash -c 'source "$1/bin/install-ai-tools.sh"; render_agent_system_prompt' _ "${repo}" >"${work}/prompt"
grep -q 'under the address:' "${work}/prompt"
CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true REPO_DIR="${repo}" CONSTRUCT_EXTERNAL_HOST=vm.invalid \
  bash -c 'source "$1/bin/install-ai-tools.sh"; render_agent_system_prompt' _ "${repo}" >"${work}/prompt"
grep -q 'under the DNS name:' "${work}/prompt"
grep -qx 'Restart=on-failure' "${repo}/systemd/construct-endpoint-refresh.service"
echo 'Endpoint refresh: local no-op, direct/relayed updates, idempotence, failures, token handling and prompt rendering passed.'
