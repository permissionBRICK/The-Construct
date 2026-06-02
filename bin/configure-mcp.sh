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
# plus two optional fields on either form:
#   "agents":  subset of [claude|claude-code, codex, opencode] -- only configure
#              this server into those agents (default: all). The same name may
#              appear more than once with different "agents" to give an agent a
#              different config.
#   "enabled": set false to add the server flagged DISABLED (default true). The
#              server is still DEFINED in each agent so you can toggle it on in
#              the UI: Codex `enabled = false`, Opencode `enabled: false`, and
#              Claude via projects.<dir>.disabledMcpServers (its per-project
#              mechanism; there is no global disable) for the workspace and the
#              project's repo dirs.
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
# Where project repos are checked out. Used to scope Claude's per-directory MCP
# disable (projects.<dir>.disabledMcpServers) to the workspace + each repo dir.
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

has_agent() { case ",${AI_TOOLS}," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

if [[ ! -f "${GENERATED_JSON}" ]]; then
  note "No runtime config at ${GENERATED_JSON}; nothing to configure."
  exit 0
fi

count="$(jq '(.mcpServers // []) | length' "${GENERATED_JSON}" 2>/dev/null || echo 0)"
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
jq -r '.mcpServers[]
  | "    - " + .name
    + (if .agents then " (agents: " + (.agents | join(",")) + ")" else "" end)
    + (if .enabled == false then " [disabled]" else "" end)' "${GENERATED_JSON}"

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

# Every server name declared by the selected projects is "managed": for each
# agent we (re)write the entries that apply to it AND remove its entries for
# declared names that don't apply (disabled, or not in this server's `agents`),
# so toggling enabled:false or narrowing `agents` deconfigures it. Names that are
# NOT declared at all (unrelated / hand-added servers) are left untouched.
declared_names="$(jq -c '[.mcpServers[].name] | unique' "${GENERATED_JSON}")"

# ── Claude Code: ~/.claude.json ──────────────────────────────────────────────
# Claude keeps every targeted server DEFINED in top-level .mcpServers (so it is
# present/toggleable) and records "disabled" PER DIRECTORY in
# projects.<dir>.disabledMcpServers -- its actual mechanism; there is no global
# disable and it does NOT cascade to subdirectories. So we manage that list for
# WORKSPACE_ROOT and each of the project's repo clone dirs (derived from
# generated.json .repos, so it also covers repos not checked out yet).
if has_agent "claude-code"; then
  cf="${home_dir}/.claude.json"
  install -d -m 0755 "$(dirname "${cf}")"
  [[ -s "${cf}" ]] || echo '{}' >"${cf}"
  # Definitions for every claude-targeted server (enabled OR disabled).
  claude_defs="$(jq -c '
    def targets($a): (.agents == null) or (.agents | any(. == $a));
    [ .mcpServers[]? | select(targets("claude") or targets("claude-code"))
      | { (.name): (
          if (.url // "") != "" then
            ({ type: "http", url: .url }
             + (if (.headers // {}) != {} then { headers: .headers } else {} end))
          else
            ({ command: .command, args: (.args // []) }
             + (if (.env // {}) != {} then { env: .env } else {} end))
          end
        ) } ] | add // {}' "${GENERATED_JSON}")"
  # Names of claude-targeted servers that are disabled.
  claude_disabled="$(jq -c '
    def targets($a): (.agents == null) or (.agents | any(. == $a));
    [ .mcpServers[]? | select((targets("claude") or targets("claude-code")) and (.enabled == false)) | .name ] | unique' "${GENERATED_JSON}")"
  # Directories to apply the per-directory disable to: the workspace + each repo
  # clone dir (directory override, else the repo basename without .git).
  claude_dirs="$(jq -c --arg ws "${WORKSPACE_ROOT}" '
    [ $ws ] + ((.repos // []) | map($ws + "/" + (.directory // (.url | sub("\\.git$"; "") | split("/") | last)))) | unique' "${GENERATED_JSON}")"
  # Define targeted servers (and drop declared names no longer targeting Claude),
  # then for each managed dir set disabledMcpServers = (existing minus all our
  # declared names) + the currently-disabled ones (so re-enabling/de-targeting is
  # reflected, and unrelated hand-disabled servers are preserved).
  write_json "${cf}" '
    def mng($d): .projects[$d] = ((.projects[$d] // {})
      | .disabledMcpServers = ((((.disabledMcpServers // []) - $declared) + $disabled) | unique));
    .mcpServers = ((reduce ($declared - ($defs | keys))[] as $k ((.mcpServers // {}); del(.[$k]))) + $defs)
    | reduce $dirs[] as $d (.; mng($d))
  ' --argjson defs "${claude_defs}" --argjson declared "${declared_names}" \
    --argjson disabled "${claude_disabled}" --argjson dirs "${claude_dirs}"
  chown "${CLAUDE_USER}:${CLAUDE_USER}" "${cf}" 2>/dev/null || true
  ok "  Claude: wrote .mcpServers (+ per-dir disable) to ${cf}"
fi

# ── Opencode: ~/.config/opencode/opencode.json .mcp ──────────────────────────
if has_agent "opencode"; then
  cf="${home_dir}/.config/opencode/opencode.json"
  install -d -m 0755 "$(dirname "${cf}")"
  [[ -s "${cf}" ]] || echo '{}' >"${cf}"
  # Opencode has a native per-server enabled flag, so a disabled server is still
  # written (targeting only) and flagged enabled:false -- toggle it in the UI.
  oc_map="$(jq -c '
    def targets($a): (.agents == null) or (.agents | any(. == $a));
    [ .mcpServers[]? | select(targets("opencode"))
      | { (.name): (
          (if (.url // "") != "" then
            ({ type: "remote", url: .url }
             + (if (.headers // {}) != {} then { headers: .headers } else {} end))
          else
            ({ type: "local", command: ([.command] + (.args // [])) }
             + (if (.env // {}) != {} then { environment: .env } else {} end))
          end)
          + { enabled: (.enabled != false) }
        ) } ] | add // {}' "${GENERATED_JSON}")"
  # Drop declared names that don't apply to Opencode, then merge the ones that do.
  rm_names="$(jq -cn --argjson d "${declared_names}" --argjson m "${oc_map}" '$d - ($m | keys)')"
  write_json "${cf}" \
    '.mcp = ((reduce $rm[] as $k ((.mcp // {}); del(.[$k]))) + $m)' \
    --argjson rm "${rm_names}" --argjson m "${oc_map}"
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
    ( if $s.enabled == false then "enabled = false" else empty end ),
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
  # Strip the table for EVERY declared name first (managed): names that don't
  # apply to codex (disabled, or agents excludes codex) are thereby removed; the
  # applicable ones are re-appended fresh below. Unrelated tables are untouched.
  while IFS= read -r name; do
    [[ -n "${name}" ]] || continue
    strip_codex_server "${name}" "${cf}"
  done < <(jq -r '.mcpServers[].name' "${GENERATED_JSON}" | sort -u)
  while IFS= read -r def; do
    [[ -n "${def}" ]] || continue
    name="$(jq -r '.name' <<<"${def}")"
    if [[ -n "$(jq -r 'select((.url // "") != "" and (.headers // {}) != {}) | .name' <<<"${def}")" ]]; then
      note "    codex: '${name}' has http headers -- codex supports only a bearer-token env var; headers skipped for codex"
    fi
    gen_codex_block "${name}" "${def}" >>"${cf}"
    # Dedupe by name (last wins, matching Claude/Opencode's map merge) so duplicate
    # names across projects / overlapping agents don't emit two tables for one name
    # (which would be invalid TOML).
  done < <(jq -c '
    def targets($a): (.agents == null) or (.agents | any(. == $a));
    [ .mcpServers[]? | select(targets("codex")) ] | map({ (.name): . }) | add // {} | .[]' "${GENERATED_JSON}")
  # Collapse runs of blank lines to one and trim leading/trailing blanks, so the
  # strip+append above stays byte-idempotent across reprovisions (TOML-safe).
  awk '
    /^[[:space:]]*$/ { pending = 1; next }
    { if (pending && emitted) print ""; pending = 0; emitted = 1; print }
  ' "${cf}" >"${cf}.norm" && mv "${cf}.norm" "${cf}"
  chown "${CLAUDE_USER}:${CLAUDE_USER}" "${cf}" 2>/dev/null || true
  ok "  Codex: wrote [mcp_servers.*] to ${cf}"
fi

ok "MCP configuration complete"
