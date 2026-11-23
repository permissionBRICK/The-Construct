#!/usr/bin/env bash
#
# Print a guest manifest: what provisioning left on this VM, one sorted record per
# line, so two manifests compare with plain `diff`. Read-only; run as root so every
# file can be hashed. Sections (docs/plans/construct-platform-design.md, 13.3):
#
#   packages     dpkg packages and versions
#   npm-globals  globally installed npm packages
#   files        /usr/local/bin, /etc/construct, /etc/systemd/system/construct*,
#                /opt/construct (without repo/), the agents' and T3's install folders
#                under /root and /home/*, and the installed T3 build
#   config       config.env and provisioned.env, secret values masked
#   units        state of the Construct, T3, VS Code, Docker, SSH and Samba units
#   docker       Docker images
#
# File records: path, type, mode, owner:group, then the symlink target or the
# content hash. Files whose name says they hold a credential get a masked hash.
# Runtime state of the tools (sessions, histories, logs, caches, databases) is left
# out: it changes on every run and is not part of what provisioning installs.
#
#   sudo bash bin/construct-manifest.sh >manifest.txt
#
set -uo pipefail
export LC_ALL=C

section() { printf '## %s\n' "$1"; }

# File names that say they hold a credential (lower-cased basename, awk regex).
# Their hash is masked: a hash of a secret is not the secret, but it has no
# business in a saved manifest either.
SECRET_NAME_RE='(token|secret|password|passwd|credential|private|auth\\.json$|\\.key$|key\\.pem$|_ed25519$|_rsa$|_ecdsa$|^backup-recipients$)'

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

# One record per filesystem entry under the given roots: path, type, mode,
# owner:group, then "-> target" for a symlink or the content hash for a file.
# Arguments after `--` are full-path patterns pruned from the walk.
list_files() {
  local -a roots=() expr=()
  local p
  while [[ $# -gt 0 && "$1" != "--" ]]; do
    [[ -e "$1" || -L "$1" ]] && roots+=("$1")
    shift
  done
  [[ "${1:-}" == "--" ]] && shift
  [[ ${#roots[@]} -gt 0 ]] || return 0
  if [[ $# -gt 0 ]]; then
    expr+=("(")
    for p in "$@"; do
      [[ ${#expr[@]} -gt 1 ]] && expr+=(-o)
      expr+=(-path "${p}")
    done
    expr+=(")" -prune -o)
  fi
  find "${roots[@]}" -xdev "${expr[@]}" -printf '%p\t%y\t%m\t%u:%g\t%l\n' 2>/dev/null >"${WORK}/records"
  find "${roots[@]}" -xdev "${expr[@]}" -type f -print0 2>/dev/null |
    xargs -0 -r sha256sum 2>/dev/null >"${WORK}/hashes"
  awk -F'\t' -v secret="${SECRET_NAME_RE}" '
    FNR == NR { h = substr($0, 1, 64); f = substr($0, 67); hash[f] = h; next }
    {
      path = $1; type = $2; detail = ""
      if (type == "l") detail = "-> " $5
      else if (type == "f") {
        base = path; sub(/.*\//, "", base)
        detail = (tolower(base) ~ secret) ? "sha256=<masked>" : "sha256=" ((path in hash) ? hash[path] : "?")
      }
      print path "\t" type "\t" $3 "\t" $4 "\t" detail
    }' "${WORK}/hashes" "${WORK}/records"
}

# --- packages ----------------------------------------------------------------
section packages
dpkg-query -W -f '${db:Status-Abbrev}\t${Package}\t${Version}\t${Architecture}\n' 2>/dev/null |
  awk -F'\t' '$1 ~ /^ii/ { print $2 "\t" $3 "\t" $4 }' | sort

# --- npm globals -------------------------------------------------------------
section npm-globals
if command -v npm >/dev/null 2>&1; then
  npm ls -g --depth=0 --parseable --long 2>/dev/null | tail -n +2 |
    awk -F: '{ print $2 }' | sort
fi

# --- files -------------------------------------------------------------------
section files
{
  list_files /usr/local/bin /etc/construct /opt/construct -- /opt/construct/repo
  shopt -s nullglob
  list_files /etc/systemd/system/construct*
  shopt -u nullglob

  # Install folders of the agents and of T3 in every home. Their runtime state is
  # pruned; what is left is what the installers and provisioning put there.
  homes=(/root)
  for h in /home/*; do [[ -d "${h}" ]] && homes+=("${h}"); done
  for h in "${homes[@]}"; do
    list_files "${h}/.local/bin" "${h}/.local/share/claude" "${h}/.claude" "${h}/.codex" \
               "${h}/.opencode" "${h}/.config/opencode" "${h}/.t3" -- \
      "${h}/.claude/projects" "${h}/.claude/sessions" "${h}/.claude/session-env" \
      "${h}/.claude/shell-snapshots" "${h}/.claude/history.jsonl" "${h}/.claude/backups" \
      "${h}/.claude/cache" "${h}/.claude/downloads" "${h}/.claude/ide" "${h}/.claude/state" \
      "${h}/.claude/statsig" "${h}/.claude/todos" "${h}/.claude/.last-cleanup" "${h}/.claude/*.synced-at" \
      "${h}/.claude.json*" \
      "${h}/.codex/sessions" "${h}/.codex/log" "${h}/.codex/tmp" "${h}/.codex/.tmp" "${h}/.codex/cache" \
      "${h}/.codex/ipc" "${h}/.codex/shell_snapshots" "${h}/.codex/thread-writer-locks" \
      "${h}/.codex/app-server-*" "${h}/.codex/*.sqlite*" "${h}/.codex/*.lock" "${h}/.codex/*.jsonl" \
      "${h}/.codex/installation_id" "${h}/.codex/models_cache.json" \
      "${h}/.t3/userdata" "${h}/.t3/worktrees" "${h}/.t3/caches" "${h}/.t3/logs"
  done

  # The installed T3 build: one aggregate record (thousands of files), named by its
  # directory and summarised by file count and a hash over its per-file hashes.
  t3_bin="$(readlink -f /usr/local/bin/t3 2>/dev/null || true)"
  if [[ -n "${t3_bin}" && -e "${t3_bin}" ]]; then
    t3_dir="$(dirname "$(dirname "${t3_bin}")")"
    t3_count="$(find "${t3_dir}" -type f | wc -l)"
    t3_tree="$(cd "${t3_dir}" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)"
    printf '%s\tt3-build\t-\t-\tfiles=%s tree-sha256=%s\n' "${t3_dir}" "${t3_count}" "${t3_tree}"
  fi
} | sort

# --- config ------------------------------------------------------------------
section config
for f in /etc/construct/config.env /etc/construct/provisioned.env; do
  [[ -f "${f}" ]] || continue
  # KEY=VALUE lines only; a key that names a secret has its value masked, and so
  # has any base64 payload (*_B64), which is how secrets travel in these files.
  grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "${f}" |
    awk -v file="$(basename "${f}")" '{
      key = substr($0, 1, index($0, "=") - 1); value = substr($0, index($0, "=") + 1)
      if (toupper(key) ~ /(TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|PRIVATE|_KEY$|APIKEY|API_KEY|_B64$)/ && value != "" && value != "\"\"" && value != "\047\047")
        value = "<masked>"
      print file "\t" key "\t" value
    }' | sort
done

# --- units -------------------------------------------------------------------
section units
systemctl list-unit-files --no-legend --no-pager 'construct*' 't3*' 'vscode*' 'code-*' \
  'opencode*' 'codex*' 'docker*' 'containerd*' 'ssh*' 'smbd*' 'nmbd*' 2>/dev/null |
  while read -r unit state _; do
    [[ -n "${unit}" && "${state}" != transient ]] || continue
    printf '%s\t%s\t%s\n' "${unit}" "${state}" "$(systemctl is-active "${unit}" 2>/dev/null)"
  done | sort

# --- docker ------------------------------------------------------------------
section docker
if command -v docker >/dev/null 2>&1; then
  docker image ls --no-trunc --format '{{.Repository}}:{{.Tag}}\t{{.ID}}' 2>/dev/null | sort
fi
exit 0
