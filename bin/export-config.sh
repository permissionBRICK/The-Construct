#!/usr/bin/env bash
#
# Export the current agent configuration from this VM into a single tarball so
# the host can save it and restore it after a reinstall.
#
# What it captures (all under the target user's home, default /root):
#   - Coding-agent instruction files : ~/.claude/CLAUDE.md, ~/.codex/AGENTS.md,
#                                       ~/.config/opencode/AGENTS.md (+ any *.md)
#   - User-level memory + skills      : ~/.claude/projects/<slug>/{memory,MEMORY.md},
#                                       ~/.codex/{memories,memories_*.sqlite*,skills},
#                                       NOT anything inside the project repos.
#   - Subscription auth (INCLUDE_AUTH): ~/.claude/.credentials.json, ~/.claude.json,
#                                       ~/.codex/auth.json,
#                                       ~/.local/share/opencode/auth.json
#   - MCP server auth (INCLUDE_AUTH)  : OAuth tokens/state each agent saves after
#                                       authenticating to a remote MCP server:
#                                         Claude  ~/.claude/.credentials.json holds
#                                                 the tokens (captured above) +
#                                                 ~/.claude/mcp-needs-auth-cache.json
#                                         Codex   ~/.codex/.credentials.json
#                                         Opencode ~/.local/share/opencode/mcp-auth.json
#   - npm registry auth (INCLUDE_AUTH): ~/.npmrc (registry _authToken / _auth)
#   - Agent settings/config           : ~/.claude/settings.json, ~/.codex/config.toml,
#                                       ~/.config/opencode/opencode.json
#   - Global git config + credentials : ~/.gitconfig, ~/.git-credentials
#   - Generated project profiles      : one JSON per cloned repo under
#                                       WORKSPACE_ROOT whose remote isn't already
#                                       covered by an existing project profile.
#
# One captured secret lives OUTSIDE home: the VS Code serve-web connection token
# (INCLUDE_AUTH) at ${VSCODE_SERVE_WEB_TOKEN_FILE} (default
# /etc/construct/vscode-serve-web.token). It rides in the tarball at
# etc/construct/vscode-serve-web.token so a reinstall can keep the same ?tkn= URL;
# the host reads it back and threads it into install-vscode.sh before serve-web
# starts (restore-config.sh runs too late -- serve-web is already up by then).
#
# The tarball root contains:
#   home/      mirrors the target home -- restore copies it back with `cp -a`
#   projects/  generated project profiles -- the host merges these into projects/
#   etc/       a few captured files from outside home (e.g. the serve-web token)
#   backup-info.json / MANIFEST.txt  metadata for the host
#
# Inputs (all via environment, with defaults):
#   EXPORT_HOME    home to export from         (default /root)
#   INCLUDE_AUTH   include subscription auth    (default true)
#   OUT            output tarball path          (default /tmp/construct-config-backup.tar.gz)
#   CONFIG_FILE    construct config.env         (default /etc/construct/config.env)
#   WORKSPACE_ROOT where repos are cloned       (default from config / /root/repos)
#   REPO_DIR       uploaded repo                (default /opt/construct/repo)
#   PROJECTS_STORE persisted project profiles   (default /opt/construct/projects)
#
set -euo pipefail

EXPORT_HOME="${EXPORT_HOME:-/root}"
INCLUDE_AUTH="${INCLUDE_AUTH:-true}"
OUT="${OUT:-/tmp/construct-config-backup.tar.gz}"
CONFIG_FILE="${CONFIG_FILE:-/etc/construct/config.env}"
REPO_DIR="${REPO_DIR:-/opt/construct/repo}"
PROJECTS_STORE="${PROJECTS_STORE:-/opt/construct/projects}"

# Pull WORKSPACE_ROOT (and anything else useful) from config.env when present.
if [[ -f "${CONFIG_FILE}" ]]; then
  # shellcheck disable=SC1090
  set -a; . "${CONFIG_FILE}"; set +a
fi
WORKSPACE_ROOT="${WORKSPACE_ROOT:-/root/repos}"

STAGE="$(mktemp -d /tmp/construct-export.XXXXXX)"
HOMEROOT="${STAGE}/home"
PROJOUT="${STAGE}/projects"
mkdir -p "${HOMEROOT}" "${PROJOUT}"
MANIFEST="${STAGE}/MANIFEST.txt"
: >"${MANIFEST}"

log()  { printf '  %s\n' "$*"; }
note() { printf '  %s\n' "$*" >&2; }

# Copy a file or directory (given relative to EXPORT_HOME) into the staging
# home tree, preserving its relative path. No-op when the source is absent.
add() {
  local rel="$1" src dst
  src="${EXPORT_HOME}/${rel}"
  [[ -e "${src}" ]] || return 0
  dst="${HOMEROOT}/${rel}"
  mkdir -p "$(dirname "${dst}")"
  cp -a "${src}" "${dst}"
  printf '%s\n' "${rel}" >>"${MANIFEST}"
  log "+ ${rel}"
}

# Copy every match of a shell glob (relative to EXPORT_HOME).
add_glob() {
  local pat="$1" match rel
  shopt -s nullglob dotglob
  for match in "${EXPORT_HOME}"/${pat}; do
    rel="${match#"${EXPORT_HOME}/"}"
    add "${rel}"
  done
  shopt -u nullglob dotglob
}

agents="${AI_TOOLS:-claude-code,codex,opencode}"
has_agent() { case ",${agents}," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

printf '==> Exporting agent config from %s\n' "${EXPORT_HOME}"

# ── Global git config + credentials, GitHub CLI login ────────────────────────
add ".gitconfig"
add ".git-credentials"
# GitHub CLI: hosts.yml holds the login/OAuth token, config.yml the settings.
add ".config/gh"

# ── npm registry auth ────────────────────────────────────────────────────────
# ~/.npmrc is npm's user config; it also carries registry auth tokens
# (//registry/:_authToken=, _auth=, :_password=). It's pure auth, so gate it on
# INCLUDE_AUTH -- a sanitized backup must omit the token. (Unlike git creds,
# nothing in provisioning needs it before restore, so it doesn't go unconditional.)
if [[ "${INCLUDE_AUTH}" == "true" ]]; then
  add ".npmrc"
fi

# ── VS Code serve-web connection token ───────────────────────────────────────
# The ?tkn= secret that gates browser VS Code. It lives OUTSIDE home (default
# /etc/construct/vscode-serve-web.token), so the home-relative `add` can't reach
# it -- copy it into an etc/ tree in the tarball. It's pure auth, so gate it on
# INCLUDE_AUTH. install-vscode.sh only mints a token when none exists, so getting
# this file back in place before serve-web starts keeps the URL stable; the host
# does that by threading it into install-vscode.sh (restore-config.sh runs too
# late). VSCODE_SERVE_WEB_TOKEN_FILE comes from config.env, sourced above.
if [[ "${INCLUDE_AUTH}" == "true" ]]; then
  sw_token_file="${VSCODE_SERVE_WEB_TOKEN_FILE:-/etc/construct/vscode-serve-web.token}"
  if [[ -s "${sw_token_file}" ]]; then
    mkdir -p "${STAGE}/etc/construct"
    cp -a "${sw_token_file}" "${STAGE}/etc/construct/vscode-serve-web.token"
    printf 'etc/construct/vscode-serve-web.token\n' >>"${MANIFEST}"
    log "+ etc/construct/vscode-serve-web.token"
  fi
fi

# ── Claude Code ──────────────────────────────────────────────────────────────
if has_agent "claude-code" || [[ -d "${EXPORT_HOME}/.claude" ]]; then
  add ".claude/CLAUDE.md"
  add ".claude/settings.json"
  add ".claude/skills"
  if [[ "${INCLUDE_AUTH}" == "true" ]]; then
    add ".claude.json"
    add ".claude/.credentials.json"
    # MCP server OAuth tokens for user-added remote servers live INSIDE
    # .credentials.json (captured just above). This cache records which MCP
    # servers still await an interactive /mcp auth -- keep it so the restored VM
    # doesn't forget. (claude.ai connectors are auth'd server-side, nothing local.)
    add ".claude/mcp-needs-auth-cache.json"
  fi
  # Per-project memory only -- never the session transcripts, caches, or the
  # cloned project repos themselves. Memory lives under
  # ~/.claude/projects/<slug>/{memory/,MEMORY.md}.
  if [[ -d "${EXPORT_HOME}/.claude/projects" ]]; then
    shopt -s nullglob
    for slug in "${EXPORT_HOME}/.claude/projects"/*/; do
      rel=".claude/projects/$(basename "${slug}")"
      [[ -d "${slug}memory" ]]   && add "${rel}/memory"
      [[ -f "${slug}MEMORY.md" ]] && add "${rel}/MEMORY.md"
    done
    shopt -u nullglob
  fi
fi

# ── Codex ────────────────────────────────────────────────────────────────────
if has_agent "codex" || [[ -d "${EXPORT_HOME}/.codex" ]]; then
  add ".codex/AGENTS.md"
  add ".codex/config.toml"
  add ".codex/memories"
  add_glob ".codex/memories_*.sqlite*"
  if [[ "${INCLUDE_AUTH}" == "true" ]]; then
    add ".codex/auth.json"
    # Per-MCP-server OAuth tokens (access/refresh, keyed by server). Codex keeps
    # these in the OS keyring when available, else this file-backed fallback --
    # which is what a headless VM uses, so it's the file to carry across.
    add ".codex/.credentials.json"
  fi
  # Skills, minus the bundled system skills (re-created on install).
  add ".codex/skills"
  rm -rf "${HOMEROOT}/.codex/skills/.system" 2>/dev/null || true
fi

# ── Opencode ─────────────────────────────────────────────────────────────────
if has_agent "opencode" || [[ -d "${EXPORT_HOME}/.config/opencode" ]]; then
  add ".config/opencode/opencode.json"
  # Instruction files (AGENTS.md, soul.md, ...) but not node_modules / lockfiles.
  add_glob ".config/opencode/*.md"
  if [[ "${INCLUDE_AUTH}" == "true" ]]; then
    add ".local/share/opencode/auth.json"
    # Per-MCP-server OAuth state (client registration + tokens) for remote servers.
    add ".local/share/opencode/mcp-auth.json"
  fi
fi

# ── Back up stored project profiles + generate profiles for loose repos ──────
# The VM's persisted project profiles (PROJECTS_STORE) carry the real config the
# user added -- notably MCP servers (which live in the project JSON) -- so copy
# them into the backup. On restore the host merges them into its projects/ dir,
# keeping any it already has. Then, for cloned repos not covered by ANY profile,
# generate a minimal profile so the repo is re-cloned after a reinstall.

# 1. Copy the stored profiles verbatim (skip the schema file).
if [[ -d "${PROJECTS_STORE}" ]]; then
  shopt -s nullglob
  for pj in "${PROJECTS_STORE}"/*.json; do
    base="$(basename "${pj}")"
    [[ "${base}" == "project.schema.json" ]] && continue
    cp -f "${pj}" "${PROJOUT}/${base}"
    log "+ project profile (stored): ${base%.json}"
  done
  shopt -u nullglob
fi

# 2. Set of repo URLs already covered by a profile (repo copy + persisted store).
covered="$(mktemp)"
: >"${covered}"
for dir in "${REPO_DIR}/projects" "${PROJECTS_STORE}"; do
  [[ -d "${dir}" ]] || continue
  shopt -s nullglob
  for pj in "${dir}"/*.json; do
    [[ "$(basename "${pj}")" == "project.schema.json" ]] && continue
    jq -r '.repos[]?.url // empty' "${pj}" 2>/dev/null >>"${covered}" || true
  done
  shopt -u nullglob
done

# 3. Generate a profile for each cloned repo whose remote isn't covered yet.
if [[ -d "${WORKSPACE_ROOT}" ]]; then
  shopt -s nullglob
  for repo in "${WORKSPACE_ROOT}"/*/; do
    [[ -d "${repo}.git" ]] || continue
    name="$(basename "${repo}")"
    url="$(git -C "${repo}" remote get-url origin 2>/dev/null || true)"
    [[ -n "${url}" ]] || { note "skip ${name}: no 'origin' remote"; continue; }
    if grep -qxF "${url}" "${covered}" 2>/dev/null; then
      log "= ${name}: already covered by a project profile"
      continue
    fi
    [[ -e "${PROJOUT}/${name}.json" ]] && { log "= ${name}: profile already captured"; continue; }
    jq -n --arg name "${name}" --arg url "${url}" --arg dir "${name}" '
      { name: $name,
        repos: [ { url: $url, directory: $dir } ],
        sdks: {}, mcp: [], hostPackages: [], tests: {} }' \
      >"${PROJOUT}/${name}.json"
    log "* generated project profile: ${name} -> ${url}"
  done
  shopt -u nullglob
fi
rm -f "${covered}"

# ── Metadata for the host ────────────────────────────────────────────────────
# addedProjects = every captured profile except the builtin default, so the host
# can union them into PROJECTS on restore and re-provision them (re-cloning their
# repos and reconfiguring their MCP servers).
added_list=()
shopt -s nullglob
for pj in "${PROJOUT}"/*.json; do
  b="$(basename "${pj}" .json)"
  [[ "${b}" == "default" ]] && continue
  added_list+=("${b}")
done
shopt -u nullglob
gen_json='[]'
if [[ "${#added_list[@]}" -gt 0 ]]; then
  gen_json="$(printf '%s\n' "${added_list[@]}" | jq -R . | jq -cs 'unique')"
fi
jq -n \
  --arg created "$(date -Iseconds 2>/dev/null || true)" \
  --arg host "$(hostname 2>/dev/null || true)" \
  --arg agents "${agents}" \
  --argjson includeAuth "$([[ "${INCLUDE_AUTH}" == "true" ]] && echo true || echo false)" \
  --argjson addedProjects "${gen_json}" '
  { created: $created, host: $host, agents: ($agents | split(",")),
    includeAuth: $includeAuth, addedProjects: $addedProjects }' \
  >"${STAGE}/backup-info.json"

# ── Pack ─────────────────────────────────────────────────────────────────────
rm -f "${OUT}"
mkdir -p "$(dirname "${OUT}")"
tar -czf "${OUT}" -C "${STAGE}" .
rm -rf "${STAGE}"

printf '==> Export complete: %s (%s)\n' "${OUT}" "$(du -h "${OUT}" 2>/dev/null | cut -f1)"
if [[ "${#added_list[@]}" -gt 0 ]]; then
  printf '    Captured %s project profile(s): %s\n' "${#added_list[@]}" "${added_list[*]}"
fi
