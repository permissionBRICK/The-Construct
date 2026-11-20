#!/usr/bin/env bash
# Plain-Bash tests for the coding agents a VM is given (plan 11.8): the Antigravity
# CLI's installer helpers and settings in bin/install-ai-tools.sh, its MCP file in
# bin/configure-mcp.sh, and T3 Code's providers turned on and off by the VM's agents.
# Run: bash test/coding-agents.test.sh
#
# Everything runs against stubs and scratch homes -- no network, no installs.

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
jq_ok() { jq -e "$1" "$2" >/dev/null 2>&1; }

# Source the helpers (installs nothing) and run one expression.
run_case() {
  local name="$1" body="$2"
  shift 2
  CONSTRUCT_AI_TOOLS_FUNCS_ONLY=true SCRIPT_PATH="${SCRIPT}" CASE_BODY="${body}" \
    env "$@" bash -c 'source "${SCRIPT_PATH}"; eval "${CASE_BODY}"' \
    >"${tmp}/${name}.out" 2>&1
  printf '%s' "$?" >"${tmp}/${name}.rc"
}

# ── install_antigravity: Google's installer once, then agy update ─────────────
home="${tmp}/root"
mkdir -p "${home}" "${tmp}/stub" "${tmp}/link"
# The stub curl writes an installer that does what Google's does: put agy into
# $HOME/.local/bin. The agy it writes records how it was called.
cat >"${tmp}/stub/curl" <<'EOF'
#!/usr/bin/env bash
[[ "${STUB_CURL_RC:-0}" == 0 ]] || exit "${STUB_CURL_RC}"
while [[ $# -gt 0 ]]; do [[ "$1" == -o ]] && out="$2"; shift; done
printf 'curl\n' >>"${STUB_LOG}"
cat >"${out}" <<'SCRIPT'
[[ "${STUB_INSTALLER_RC:-0}" == 0 ]] || exit "${STUB_INSTALLER_RC}"
printf 'installer home=%s\n' "${HOME}" >>"${STUB_LOG}"
mkdir -p "${HOME}/.local/bin"
printf '#!/usr/bin/env bash\nprintf "agy %%s home=%%s\\n" "$*" "${HOME}" >>"%s"\n' "${STUB_LOG}" >"${HOME}/.local/bin/agy"
chmod +x "${HOME}/.local/bin/agy"
SCRIPT
EOF
chmod +x "${tmp}/stub/curl"
agy_env=(TARGET_USER=root AGENT_SYSTEM_PROMPT_ROOT_HOME="${home}" REPO_DIR="${ROOT}"
  AGENT_SYSTEM_PROMPT_CUSTOM="${tmp}/none.md" CONSTRUCT_EXTERNAL_HOST=vm.example.test AGY_LINK="${tmp}/link/agy"
  STUB_LOG="${tmp}/agy.log" PATH="${tmp}/stub:/usr/bin:/bin")

run_case agy_fresh 'install_antigravity' "${agy_env[@]}"
ok "a fresh install succeeds" test "$(cat "${tmp}/agy_fresh.rc")" = 0
ok "Google's installer runs in the agent user's home" grep -qx "installer home=${home}" "${tmp}/agy.log"
ok "every shell and T3 Code find the one copy" test "$(readlink "${tmp}/link/agy")" = "${home}/.local/bin/agy"
ok "Antigravity runs without per-tool prompts" \
  jq_ok '.toolPermission == "always-proceed"' "${home}/.gemini/antigravity-cli/settings.json"
ok "its global rules are Construct's instructions" grep -q 'vm.example.test' "${home}/.gemini/AGENTS.md"
ok "nothing is updated on a fresh install" sh -c "! grep -q '^agy update' '${tmp}/agy.log'"

# Settings a user added stay; a second run updates in place and downloads nothing.
jq '. + {theme: "dark"}' "${home}/.gemini/antigravity-cli/settings.json" >"${tmp}/s.json" && cat "${tmp}/s.json" >"${home}/.gemini/antigravity-cli/settings.json"
: >"${tmp}/agy.log"
run_case agy_again 'install_antigravity' "${agy_env[@]}"
ok "a reprovision succeeds" test "$(cat "${tmp}/agy_again.rc")" = 0
ok "a reprovision runs agy update" grep -qx "agy update home=${home}" "${tmp}/agy.log"
ok "a reprovision downloads no installer" sh -c "! grep -q '^curl' '${tmp}/agy.log'"
ok "other Antigravity settings are kept" \
  jq_ok '.theme == "dark" and .toolPermission == "always-proceed"' "${home}/.gemini/antigravity-cli/settings.json"

# A failing installer is retried, then reported (the retry pause is stubbed).
rm -rf "${home}/.local" "${tmp}/link/agy"
: >"${tmp}/agy.log"
run_case agy_fail 'sleep() { :; }; install_antigravity' "${agy_env[@]}" STUB_INSTALLER_RC=7
ok "a failing installer fails the install" test "$(cat "${tmp}/agy_fail.rc")" = 1
ok "the installer is tried three times" test "$(grep -c '^curl' "${tmp}/agy.log")" = 3
ok "no link is left to a missing agy" test ! -e "${tmp}/link/agy" -a ! -L "${tmp}/link/agy"

: >"${tmp}/agy.log"
run_case agy_fetch_fail 'sleep() { :; }; antigravity_official_installer root "${HOME_UNDER_TEST}"' "${agy_env[@]}" \
  STUB_CURL_RC=22 HOME_UNDER_TEST="${home}"
ok "a failed download is reported" test "$(cat "${tmp}/agy_fetch_fail.rc")" = 22
ok "a failed download runs no installer" sh -c "! grep -q '^installer' '${tmp}/agy.log'"

# ── configure_t3_providers: T3 Code's providers follow the VM's agents ────────
t3="${tmp}/t3"
settings="${t3}/userdata/settings.json"
run_case t3_fresh 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=opencode,claude-code,codex
ok "settings are written where T3 Code has none yet" test "$(cat "${tmp}/t3_fresh.rc")" = 0
ok "the default agents are on and Antigravity is off" jq_ok '.providerInstances
  | .claudeAgent.enabled and .codex.enabled and .opencode.enabled and (.antigravity.enabled == false)
  and .claudeAgent.driver == "claudeAgent" and .antigravity.driver == "antigravity"' "${settings}"

cat >"${settings}" <<'JSON'
{
  "autoResumeLimitedThreads": true,
  "providerInstances": {
    "opencode": { "driver": "opencode", "enabled": true, "config": { "serverUrl": "", "binaryPath": "opencode" } },
    "codex": { "driver": "codex", "config": { "enabled": false, "homePath": "/root/.codex" } },
    "antigravity": { "driver": "antigravity", "enabled": false, "config": { "authMethod": "oauth-personal" } },
    "work": { "driver": "claudeAgent", "enabled": true }
  }
}
JSON
run_case t3_switch 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=codex,antigravity
ok "a switched selection is written" test "$(cat "${tmp}/t3_switch.rc")" = 0
ok "deselected agents' providers are off, chosen ones on" jq_ok '.providerInstances
  | (.opencode.enabled == false) and (.claudeAgent.enabled == false) and .codex.enabled and .antigravity.enabled' "${settings}"
ok "a provider's own off flag in its config no longer wins" jq_ok '.providerInstances.codex.config | has("enabled") | not' "${settings}"
ok "Antigravity points at the installed CLI" \
  jq_ok '.providerInstances.antigravity.config == {authMethod: "oauth-personal", binaryPath: "/usr/local/bin/agy"}' "${settings}"
ok "other settings, provider config and other instances are kept" jq_ok '.autoResumeLimitedThreads
  and .providerInstances.opencode.config.binaryPath == "opencode" and .providerInstances.codex.config.homePath == "/root/.codex"
  and .providerInstances.work == {driver: "claudeAgent", enabled: true}' "${settings}"

jq '.providerInstances.antigravity.config.binaryPath = "/opt/agy/agy"' "${settings}" >"${tmp}/t.json" && cat "${tmp}/t.json" >"${settings}"
touch -d '2001-01-01' "${settings}"
run_case t3_same 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=codex,antigravity
ok "a path the user set for Antigravity is kept" jq_ok '.providerInstances.antigravity.config.binaryPath == "/opt/agy/agy"' "${settings}"
ok "an unchanged selection leaves the file alone" test "$(date -r "${settings}" +%Y)" = 2001

run_case t3_none 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=none
ok "every agent off turns every provider off" jq_ok '.providerInstances
  | [.claudeAgent, .codex, .opencode, .antigravity] | map(.enabled) == [false, false, false, false]' "${settings}"

cat >"${settings}" <<'JSON'
{
  "providers": {
    "codex": { "enabled": true, "homePath": "/srv/codex-home" },
    "opencode": { "serverUrl": "http://127.0.0.1:4096", "serverPassword": "x" }
  }
}
JSON
run_case t3_legacy 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=codex,opencode
ok "an older providers entry moves into the new instance with its configuration" jq_ok '.providerInstances
  | .codex == {driver: "codex", enabled: true, config: {homePath: "/srv/codex-home"}}
  and .opencode == {driver: "opencode", enabled: true, config: {serverUrl: "http://127.0.0.1:4096", serverPassword: "x"}}
  and .claudeAgent == {driver: "claudeAgent", enabled: false}' "${settings}"

printf '{ not json' >"${settings}"
run_case t3_bad 'configure_t3_providers' T3CODE_HOME="${t3}" AI_TOOLS=codex
ok "invalid T3 settings are left alone without failing the install" test "$(cat "${tmp}/t3_bad.rc")" = 0 -a "$(cat "${settings}")" = '{ not json'

# ── configure-mcp.sh: Antigravity's global MCP file ──────────────────────────
mcp_home="${tmp}/mcp-home"
generated="${tmp}/generated.json"
mkdir -p "${mcp_home}/.gemini/config"
cat >"${mcp_home}/.gemini/config/mcp_config.json" <<'JSON'
{ "mcpServers": { "mine": { "command": "own", "disabled": false }, "scoped": { "command": "old" } }, "other": 1 }
JSON
cat >"${generated}" <<'JSON'
{ "mcpServers": [
  { "name": "files", "command": "npx", "args": ["-y", "fs"], "env": { "ROOT": "/work" } },
  { "name": "api", "url": "https://mcp.example.test/mcp", "headers": { "X-Key": "v1" } },
  { "name": "off", "command": "srv", "enabled": false },
  { "name": "scoped", "command": "claude-only", "agents": ["claude"] },
  { "name": "both", "command": "for-ag", "agents": ["antigravity", "codex"] }
] }
JSON
mcp() { env AI_TOOLS="$1" TARGET_HOME="${mcp_home}" GENERATED_JSON="${generated}" CLAUDE_USER="$(id -un)" \
  bash "${ROOT}/bin/configure-mcp.sh" >"${tmp}/mcp.out" 2>&1; }
mcp antigravity
mcp_file="${mcp_home}/.gemini/config/mcp_config.json"
ok "configure-mcp succeeds for Antigravity" test $? = 0
ok "a stdio server is written as Antigravity reads it" \
  jq_ok '.mcpServers.files == {command: "npx", args: ["-y", "fs"], env: {ROOT: "/work"}, disabled: false}' "${mcp_file}"
ok "a remote server takes serverUrl and its headers" \
  jq_ok '.mcpServers.api == {serverUrl: "https://mcp.example.test/mcp", headers: {"X-Key": "v1"}, disabled: false}' "${mcp_file}"
ok "a disabled server is written flagged" jq_ok '.mcpServers.off.disabled == true' "${mcp_file}"
ok "a server for other agents only is removed" jq_ok '.mcpServers | has("scoped") | not' "${mcp_file}"
ok "a server scoped to Antigravity is written" jq_ok '.mcpServers.both.command == "for-ag"' "${mcp_file}"
ok "servers and keys Construct does not manage stay" jq_ok '.mcpServers.mine.command == "own" and .other == 1' "${mcp_file}"

rm -rf "${mcp_home}"
mkdir -p "${mcp_home}"
mcp claude-code,codex
ok "a VM without Antigravity gets no Antigravity file" test ! -e "${mcp_file}"

printf '\n%d passed, %d failed\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
