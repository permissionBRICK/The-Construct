"use strict";
// Gather live VM status over SSH and shape it into the state the webview renders.
// One round-trip: a small bash probe prints TAB-separated key/value lines that we
// parse here (no jq dependency on the VM).

const ssh = require("./ssh");

const REMOTE_PROBE = `set -u
emit(){ printf '%s\\t%s\\n' "$1" "$2"; }
emit HOSTNAME "$(hostname 2>/dev/null)"
if [ -r /etc/os-release ]; then . /etc/os-release 2>/dev/null; emit UBUNTU "\${PRETTY_NAME:-}"; fi
emit MEM_GB "$(awk '/MemTotal/{printf "%.0f",$2/1024/1024}' /proc/meminfo 2>/dev/null)"
emit DISK_SIZE "$(df -BG / 2>/dev/null | awk 'NR==2{print $2}')"
emit DISK_USED "$(df -BG / 2>/dev/null | awk 'NR==2{print $3}')"
cfg=/etc/construct/config.env
if [ -r "$cfg" ]; then
  emit AGENT_NAME "$(sed -n 's/^AGENT_NAME=//p' "$cfg" | head -1)"
  emit PROJECTS "$(sed -n 's/^PROJECTS=//p' "$cfg" | head -1)"
  emit AI_TOOLS "$(sed -n 's/^AI_TOOLS=//p' "$cfg" | head -1)"
fi
command -v claude   >/dev/null 2>&1 && emit V_CLAUDE   "$(claude --version 2>/dev/null | head -1)"
command -v codex    >/dev/null 2>&1 && emit V_CODEX    "$(codex --version 2>/dev/null | head -1)"
command -v opencode >/dev/null 2>&1 && emit V_OPENCODE "$(opencode --version 2>/dev/null | head -1)"
`;

/** Pull the first semver out of a version string, e.g. "codex-cli 0.142.4" -> "0.142.4". */
function extractVersion(s) {
  if (!s) return "";
  const m = String(s).match(/\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?/);
  return m ? m[0] : String(s).trim();
}

/** Parse TAB-separated KEY\tVALUE lines into a map. */
function parseProbe(stdout) {
  const map = {};
  for (const line of String(stdout).split("\n")) {
    const i = line.indexOf("\t");
    if (i > 0) map[line.slice(0, i)] = line.slice(i + 1).trim();
  }
  return map;
}

function toState(map) {
  const tools = (map.AI_TOOLS || "").split(",").map((s) => s.trim()).filter(Boolean);
  const agents = [];
  const add = (id, name, detail, vkey) => {
    if (tools.includes(id) || map[vkey]) {
      agents.push({ id, name, detail, version: extractVersion(map[vkey]) || "—", updateAvailable: false });
    }
  };
  add("claude-code", "Claude Code", "CLI + VS Code extension", "V_CLAUDE");
  add("codex", "Codex", "app-server :4500", "V_CODEX");
  add("opencode", "OpenCode", "serve :4096", "V_OPENCODE");

  const projects = (map.PROJECTS || "").split(",").map((s) => s.trim()).filter(Boolean)
    .map((name) => ({ name, selected: true }));

  const mem = map.MEM_GB ? `${map.MEM_GB} GB RAM` : "";
  const disk = (map.DISK_USED && map.DISK_SIZE) ? `${map.DISK_USED} / ${map.DISK_SIZE} disk` : "";
  const resources = [mem, disk].filter(Boolean).join(" · ");

  return { vmName: map.AGENT_NAME || "", ubuntu: map.UBUNTU || "", resources, agents, projects };
}

/** Probe the VM. Resolves a partial state object suitable for postMessage({type:'state'}). */
async function probe(opts = {}) {
  const cfg = ssh.resolveCfg(opts);
  const host = cfg.vmHost;
  const hostShort = String(host).split(".")[0];
  const reachable = await ssh.isReachable(opts);
  if (!reachable) return { online: false, host, hostShort };
  const r = await ssh.runRemoteScript(REMOTE_PROBE, { ...opts, timeoutMs: opts.timeoutMs || 25000 });
  if (r.code !== 0) return { online: true, host, hostShort, probeError: (r.stderr || "").slice(0, 300) };
  return { online: true, host, hostShort, ...toState(parseProbe(r.stdout)) };
}

module.exports = { REMOTE_PROBE, extractVersion, parseProbe, toState, probe };
