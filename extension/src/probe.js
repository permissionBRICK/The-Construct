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
  emit T3CODE "$(sed -n 's/^T3CODE=//p' "$cfg" | head -1)"
  emit T3CODE_PORT "$(sed -n 's/^T3CODE_PORT=//p' "$cfg" | head -1)"
fi
mark=/etc/construct/provisioned.env
if [ -r "$mark" ]; then
  emit INSTALLED_AT "$(sed -n 's/^INSTALLED_AT=//p' "$mark" | head -1)"
  emit REPROVISIONED_AT "$(sed -n 's/^REPROVISIONED_AT=//p' "$mark" | head -1)"
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
`;

/** Pull the first semver out of a version string, e.g. "codex-cli 0.142.4" -> "0.142.4". */
function extractVersion(s) {
  if (!s) return "";
  const m = String(s).match(/\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?/);
  return m ? m[0] : String(s).trim();
}

/**
 * Format a provisioning marker timestamp for the status pills. The VM records it
 * as ISO-8601 UTC (e.g. "2026-07-01T03:44:06Z"); we surface the date part
 * ("2026-07-01"), which is the useful signal ("when was this VM last set up"). A
 * value we can't parse is passed through trimmed (so a hand-edited marker still
 * shows *something*); an empty/missing marker yields "" so the caller omits the
 * field and the pill keeps its "—" placeholder. Pure, no timezone surprises: we
 * slice the ISO date rather than construct a Date (whose local-time rendering
 * would drift the day near midnight).
 */
function formatMarker(s) {
  const v = String(s == null ? "" : s).trim();
  if (!v) return "";
  const m = v.match(/^(\d{4}-\d{2}-\d{2})T/);
  return m ? m[1] : v;
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
  // T3 Code has its own opt-in (config.env T3CODE, the panel's settings toggle),
  // not an AI_TOOLS entry. Listed when enabled or when the CLI is present (a
  // disabled-but-installed t3 stays visible/updatable). webui — which renders
  // the panel's open-in-browser ▷ button — only when the serve unit is actually
  // RUNNING: a stopped service would mint a pairing token and then open a URL
  // where nothing listens.
  if (map.T3CODE === "true" || map.V_T3) {
    const t3port = (map.T3CODE_PORT || "").trim() || "5177";
    agents.push({
      id: "t3code", name: "T3 Code", detail: "web GUI :" + t3port,
      version: extractVersion(map.V_T3) || "—", updateAvailable: false,
      webui: map.T3_ACTIVE === "active",
    });
  }

  const projects = (map.PROJECTS || "").split(",").map((s) => s.trim()).filter(Boolean)
    .map((name) => ({ name, selected: true }));

  const mem = map.MEM_GB ? `${map.MEM_GB} GB RAM` : "";
  const disk = (map.DISK_USED && map.DISK_SIZE) ? `${map.DISK_USED} / ${map.DISK_SIZE} disk` : "";
  const resources = [mem, disk].filter(Boolean).join(" · ");

  const installed = formatMarker(map.INSTALLED_AT);
  const reprovisioned = formatMarker(map.REPROVISIONED_AT);

  const out = { vmName: map.AGENT_NAME || "", ubuntu: map.UBUNTU || "", resources, agents, projects };
  // Only emit these when the VM actually reported a marker — the webview shows the
  // "installed —" / "reprovisioned —" placeholder for an absent/unknown value.
  if (installed) out.installed = installed;
  if (reprovisioned) out.reprovisioned = reprovisioned;
  return out;
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

module.exports = { REMOTE_PROBE, extractVersion, formatMarker, parseProbe, toState, probe };
