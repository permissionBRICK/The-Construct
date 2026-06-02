#!/usr/bin/env bash
#
# Configure the MCP servers declared by the selected projects into each installed
# coding agent's native config. Reads the merged server map from the runtime
# config (generated.json -> .mcpServers, produced by generate-runtime-config.sh)
# and writes it directly into the agents' config files -- no agent CLI invoked:
#
#   Claude Code  ~/.claude.json        .mcpServers[name] = { command,args,env | type:http,url,headers }
#   Codex        ~/.codex/config.toml  [mcp_servers."name"] command/args/[.env] | url/bearer_token_env_var
#   Opencode     ~/.config/opencode/opencode.json  .mcp[name] = { type:local,command[],environment | type:remote,url,headers }
#
# Upsert by name: an existing server of the same name is replaced; other servers
# (and unrelated config) are left untouched. Servers are NOT removed when they
# disappear from the project config.
#
# Each server object (see projects/project.schema.json) is one of:
#   stdio:  { "name", "command", "args"?, "env"? }
#   http:   { "name", "url", "headers"?, "bearerTokenEnvVar"? }
# Codex http supports only the URL + an optional bearer-token env var (no
# arbitrary headers); the headers are applied to Claude and Opencode.
#
# Inputs (env, with defaults):
#   AI_TOOLS        which agents to configure   (default claude-code,codex,opencode)
#   CLAUDE_USER     target user                 (default root)
#   TARGET_HOME     override the target home     (default: CLAUDE_USER's home; for tests)
#   GENERATED_JSON  runtime config              (default ${AGENT_HOME}/runtime/generated.json)
#   AGENT_HOME                                   (default /opt/construct)
#
set -euo pipefail

if [[ -t 1 || -t 2 || -n "${FORCE_COLOR:-}" || -n "${CLICOLOR_FORCE:-}" ]]; then
  _C_STEP=$'\033[1;36m'; _C_OK=$'\033[32m'; _C_WARN=$'\033[33m'; _C_DIM=$'\033[2m'; _C_RESET=$'\033[0m'
else
  _C_STEP=''; _C_OK=''; _C_WARN=''; _C_DIM=''; _C_RESET=''
fi
step() { printf '%s==> %s%s\n' "${_C_STEP}" "$*" "${_C_RESET}"; }
ok()   { printf '%s%s%s\n'     "${_C_OK}"   "$*" "${_C_RESET}"; }
warn() { printf '%s%s%s\n'     "${_C_WARN}" "$*" "${_C_RESET}" >&2; }
note() { printf '%s%s%s\n'     "${_C_DIM}"  "$*" "${_C_RESET}"; }

AI_TOOLS="${AI_TOOLS:-claude-code,codex,opencode}"
CLAUDE_USER="${CLAUDE_USER:-root}"
AGENT_HOME="${AGENT_HOME:-/opt/construct}"
GENERATED_JSON="${GENERATED_JSON:-${AGENT_HOME}/runtime/generated.json}"

has_agent() { case ",${AI_TOOLS}," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

if [[ ! -f "${GENERATED_JSON}" ]]; then
  note "No runtime config at ${GENERATED_JSON}; nothing to configure."
  exit 0
fi

count="$(jq '(.mcpServers // {}) | length' "${GENERATED_JSON}" 2>/dev/null || echo 0)"
if [[ "${count}" == "0" ]]; then
  note "No MCP servers declared by the selected projects."
  exit 0
fi

if [[ -n "${TARGET_HOME:-}" ]]; then
  home_dir="${TARGET_HOME}"
else
  home_dir="$(getent passwd "${CLAUDE_USER}" | cut -d: -f6)"
  [[ -n "${home_dir}" ]] || home_dir="/root"
fi

step "Configuring ${count} MCP server(s) for: ${AI_TOOLS}"
jq -r '.mcpServers | keys[]' "${GENERATED_JSON}" | sed 's/^/    - /'

# Write the touched file back atomically (jq can't edit in place).
write_json() { # <file> <jq filter> [jq args...]
  local file="$1"; shift
  local filter="$1"; shift
  local tmp; tmp="$(mktemp)"
  if jq "$@" "${filter}" "${file}" >"${tmp}" 2>/dev/null; then
    cat "${tmp}" >"${file}"
  else
    warn "  WARNING: ${file} was not valid JSON; leaving it unchanged"
  fi
  rm -f "${tmp}"
}

# ── Claude Code: ~/.claude.json .mcpServers ──────────────────────────────────
if has_agent "claude-code"; then
  cf="${home_dir}/.claude.json"
  install -d -m 0755 "$(dirname "${cf}")"
  [[ -s "${cf}" ]] || echo '{}' >"${cf}"
  claude_map="$(jq -c '
    (.mcpServers // {}) | with_entries(
      .value as $s | .value = (
        if ($s.url // "") != "" then
          ({ type: "http", url: $s.url }
           + (if ($s.headers // {}) != {} then { headers: $s.headers } else {} end))
        else
          ({ command: $s.command, args: ($s.args // []) }
           + (if ($s.env // {}) != {} then { env: $s.env } else {} end))
        end
      )
    )' "${GENERATED_JSON}")"
  write_json "${cf}" '.mcpServers = ((.mcpServers // {}) + $m)' --argjson m "${claude_map}"
  chown "${CLAUDE_USER}:${CLAUDE_USER}" "${cf}" 2>/dev/null || true
  ok "  Claude: wrote .mcpServers to ${cf}"
fi

# ── Opencode: ~/.config/opencode/opencode.json .mcp ──────────────────────────
if has_agent "opencode"; then
  cf="${home_dir}/.config/opencode/opencode.json"
  install -d -m 0755 "$(dirname "${cf}")"
  [[ -s "${cf}" ]] || echo '{}' >"${cf}"
  oc_map="$(jq -c '
    (.mcpServers // {}) | with_entries(
      .value as $s | .value = (
        if ($s.url // "") != "" then
          ({ type: "remote", url: $s.url, enabled: true }
           + (if ($s.headers // {}) != {} then { headers: $s.headers } else {} end))
        else
          ({ type: "local", command: ([$s.command] + ($s.args // [])), enabled: true }
           + (if ($s.env // {}) != {} then { environment: $s.env } else {} end))
        end
      )
    )' "${GENERATED_JSON}")"
  write_json "${cf}" '.mcp = ((.mcp // {}) + $m)' --argjson m "${oc_map}"
  chown "${CLAUDE_USER}:${CLAUDE_USER}" "${cf}" 2>/dev/null || true
  ok "  Opencode: wrote .mcp to ${cf}"
fi

# ── Codex: ~/.codex/config.toml [mcp_servers."name"] ─────────────────────────
# Direct TOML edit: strip any existing block for each managed name (its table and
# its .env subtable), then append a freshly generated block. Leaves other tables
# (top-level keys, [projects.*], other [mcp_servers.*]) untouched.
strip_codex_server() {
  # Remove the server's table and any of its subtables (e.g. .env), matching BOTH
  # the quoted header `[mcp_servers."name"]` we write and the bare header
  # `[mcp_servers.name]` that `codex mcp add` writes for simple names -- otherwise
  # re-appending the quoted form would declare the same table twice (a TOML error).
  local name="$1" file="$2" tmp
  tmp="$(mktemp)"
  awk -v name="${name}" '
    BEGIN {
      q = "[mcp_servers.\"" name "\""   # quoted form, incl. closing quote
      b = "[mcp_servers." name          # bare form, name as a bare key
    }
    /^[[:space:]]*\[/ {
      line = $0; sub(/^[[:space:]]+/, "", line)
      if (index(line, q "]") == 1 || index(line, q ".") == 1 ||
          index(line, b "]") == 1 || index(line, b ".") == 1) { skip = 1 } else { skip = 0 }
    }
    { if (!skip) print }
  ' "${file}" >"${tmp}" && mv "${tmp}" "${file}"
}

gen_codex_block() { # <name> <def-json>
  jq -rn --arg name "$1" --argjson s "$2" '
    "",
    "[mcp_servers." + ($name | @json) + "]",
    ( if ($s.url // "") != "" then
        ( "url = " + ($s.url | @json) ),
        ( if ($s.bearerTokenEnvVar // "") != "" then "bearer_token_env_var = " + ($s.bearerTokenEnvVar | @json) else empty end )
      else
        ( "command = " + (($s.command // "") | @json) ),
        ( if (($s.args // []) | length) > 0 then "args = " + ($s.args | @json) else empty end )
      end ),
    ( if (($s.url // "") == "") and (($s.env // {}) != {}) then
        ( "[mcp_servers." + ($name | @json) + ".env]" ),
        ( $s.env | to_entries[] | (.key | @json) + " = " + (.value | @json) )
      else empty end )
  '
}

if has_agent "codex"; then
  cf="${home_dir}/.codex/config.toml"
  install -d -m 0700 "$(dirname "${cf}")"
  [[ -f "${cf}" ]] || : >"${cf}"
  while IFS= read -r name; do
    [[ -n "${name}" ]] || continue
    def="$(jq -c --arg n "${name}" '.mcpServers[$n]' "${GENERATED_JSON}")"
    if [[ -n "$(jq -r --arg n "${name}" '.mcpServers[$n] | select((.url // "") != "" and (.headers // {}) != {})' "${GENERATED_JSON}")" ]]; then
      note "    codex: '${name}' has http headers -- codex supports only a bearer-token env var; headers skipped for codex"
    fi
    strip_codex_server "${name}" "${cf}"
    gen_codex_block "${name}" "${def}" >>"${cf}"
  done < <(jq -r '.mcpServers | keys[]' "${GENERATED_JSON}")
  chown "${CLAUDE_USER}:${CLAUDE_USER}" "${cf}" 2>/dev/null || true
  ok "  Codex: wrote [mcp_servers.*] to ${cf}"
fi

ok "MCP configuration complete"
