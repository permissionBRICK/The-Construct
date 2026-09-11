set -u
emit(){ printf '%s\t%s\n' "$1" "$2"; }
emit HOSTNAME "$(hostname 2>/dev/null)"
if [ -r /etc/os-release ]; then . /etc/os-release 2>/dev/null; emit UBUNTU "${PRETTY_NAME:-}"; fi
emit MEM_GB "$(awk '/MemTotal/{printf "%.0f",$2/1024/1024}' /proc/meminfo 2>/dev/null)"
emit DISK_SIZE "$(df -BG / 2>/dev/null | awk 'NR==2{print $2}')"
emit DISK_USED "$(df -BG / 2>/dev/null | awk 'NR==2{print $3}')"
emit DISK_PCT "$(df -P / 2>/dev/null | awk 'NR==2{print $5}')"
emit VM_CPUS "$(nproc 2>/dev/null)"
emit DISK_DEV_BYTES "$(lsblk -b -d -n -o SIZE,TYPE 2>/dev/null | awk '$2=="disk"{print $1; exit}')"
cfg=/etc/construct/config.env
if [ -r "$cfg" ]; then
  emit AGENT_NAME "$(sed -n 's/^AGENT_NAME=//p' "$cfg" | head -1)"
  emit PROJECTS "$(sed -n 's/^PROJECTS=//p' "$cfg" | head -1)"
  emit AI_TOOLS "$(sed -n 's/^AI_TOOLS=//p' "$cfg" | head -1)"
  emit T3CODE "$(sed -n 's/^T3CODE=//p' "$cfg" | head -1)"
  emit T3CODE_PORT "$(sed -n 's/^T3CODE_PORT=//p' "$cfg" | head -1)"
  emit T3CODE_CHANNEL "$(sed -n 's/^T3CODE_CHANNEL=//p' "$cfg" | head -1)"
  emit T3CODE_HTTPS "$(sed -n 's/^T3CODE_HTTPS=//p' "$cfg" | head -1)"
  emit T3CODE_HTTPS_PORT "$(sed -n 's/^T3CODE_HTTPS_PORT=//p' "$cfg" | head -1)"
  emit T3CODE_PUBLIC_BASE_URL "$(sed -n 's/^T3CODE_PUBLIC_BASE_URL=//p' "$cfg" | head -1)"
  emit T3CODE_LIMIT_RESUME "$(sed -n 's/^T3CODE_LIMIT_RESUME=//p' "$cfg" | head -1)"
  emit OPENCODE_BACKGROUND_WATCHER "$(sed -n 's/^OPENCODE_BACKGROUND_WATCHER=//p' "$cfg" | head -1)"
fi
mark=/etc/construct/provisioned.env
if [ -r "$mark" ]; then
  emit INSTALLED_AT "$(sed -n 's/^INSTALLED_AT=//p' "$mark" | head -1)"
  emit REPROVISIONED_AT "$(sed -n 's/^REPROVISIONED_AT=//p' "$mark" | head -1)"
  emit CONSTRUCT_COMMIT "$(sed -n 's/^CONSTRUCT_COMMIT=//p' "$mark" | head -1)"
fi
# Version detection. Capture BOTH stdout and stderr (some CLIs -- e.g. codex -- print
# --version to stderr, which the old '2>/dev/null | head -1' dropped, showing "-") and
# pull the first semver from ANYWHERE in the output, so a leading banner or a stderr-only
# version still resolves. '[.]' avoids a backslash in this JS template literal.
ver(){ "$1" --version 2>&1 | grep -oE '[0-9]+[.][0-9]+[.][0-9]+([-.][0-9A-Za-z.]+)?' | head -1; }
command -v claude   >/dev/null 2>&1 && emit V_CLAUDE   "$(ver claude)"
command -v codex    >/dev/null 2>&1 && emit V_CODEX    "$(ver codex)"
command -v opencode >/dev/null 2>&1 && emit V_OPENCODE "$(ver opencode)"
command -v t3       >/dev/null 2>&1 && emit V_T3       "$(ver t3)"
emit T3_ACTIVE "$(systemctl is-active t3code-serve 2>/dev/null)"
emit T3_INSTALLATION_MODE "$(sed -n 's/^T3CODE_INSTALLATION_MODE=//p' /etc/construct/t3code-desktop-status 2>/dev/null | head -1)"
emit T3_BUILD_HASH "$(sed -n 's/^T3CODE_BUILD_KEY=//p' /etc/construct/t3code-desktop-status 2>/dev/null | head -1)"
