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
emit DISK_PCT "$(df -P / 2>/dev/null | awk 'NR==2{print $5}')"
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

/**
 * `df`'s Use% column ("94%") as a number, or null when the VM didn't report it
 * (older VM), or reported something unparseable/out of range. Pure.
 */
function parseDiskPct(s) {
  const m = String(s == null ? "" : s).trim().match(/^(\d{1,3})%?$/);
  if (!m) return null;
  const n = Number(m[1]);
  return n >= 0 && n <= 100 ? n : null;
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

/** Host as it appears inside a URL: IPv6 literals get bracketed, names and IPv4
 *  pass through (same rule the VM's print-connection-info.sh applies). */
function urlHost(host) {
  const h = String(host == null ? "" : host);
  return h.includes(":") ? `[${h}]` : h;
}

/** A T3CODE_PUBLIC_BASE_URL from the VM's config.env is only accepted when it is
 *  a plain http(s) origin. config.env is root-owned but user-editable, and this
 *  string ends up in vscode.env.openExternal — so it is validated, never trusted. */
function isSafeOrigin(s) {
  return /^https?:\/\/[A-Za-z0-9._-]+(?::\d{1,5})?$/.test(String(s || "")) ||
    /^https?:\/\/\[[0-9A-Fa-f:.]+\](?::\d{1,5})?$/.test(String(s || ""));
}

/**
 * Shape a probe map into panel state. `opts.host` is the client-reachable name of
 * the VM (cfg.vmHost) and is only needed to build the T3 web URL; omit it and the
 * agent entry simply carries no `url`.
 */
function toState(map, opts = {}) {
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
    const t3ch = (map.T3CODE_CHANNEL || "").trim();
    // With Construct's TLS proxy on (bin/setup-t3-https.sh) the web GUI is
    // reached over https on its own port — that origin is what the panel shows
    // and opens, because browser microphone capture needs a secure context and
    // the pairing token is bound to the origin that minted it. Plain http on
    // T3CODE_PORT keeps working for local tooling; it just isn't advertised.
    const t3https = (map.T3CODE_HTTPS || "").trim() === "true";
    const t3httpsPort = (map.T3CODE_HTTPS_PORT || "").trim() || "5178";
    const t3base = (map.T3CODE_PUBLIC_BASE_URL || "").trim();
    const t3detail = "web GUI :" + (t3https ? t3httpsPort : t3port) +
      (t3https ? " · https" : "") + (t3ch === "nightly" ? " · nightly" : "");
    const entry = {
      id: "t3code", name: "T3 Code", detail: t3detail,
      version: extractVersion(map.V_T3) || "—", updateAvailable: false,
      webui: map.T3_ACTIVE === "active",
      channel: t3ch === "nightly" ? "nightly" : "stable",
    };
    if (t3https && isSafeOrigin(t3base)) {
      entry.url = t3base;
    } else if (opts.host) {
      entry.url = (t3https ? `https://${urlHost(opts.host)}:${t3httpsPort}`
        : `http://${urlHost(opts.host)}:${t3port}`);
    }
    agents.push(entry);
  }

  const projects = (map.PROJECTS || "").split(",").map((s) => s.trim()).filter(Boolean)
    .map((name) => ({ name, selected: true }));

  const mem = map.MEM_GB ? `${map.MEM_GB} GB RAM` : "";
  const disk = (map.DISK_USED && map.DISK_SIZE) ? `${map.DISK_USED} / ${map.DISK_SIZE} disk` : "";
  const resources = [mem, disk].filter(Boolean).join(" · ");
  // Disk fill percentage, for the panel's over-90% warning. A full VM disk breaks
  // provisioning and agent work in confusing ways (ext4's 5% root reserve lets
  // root keep writing while every other user's writes fail), so it is worth
  // flagging BEFORE it bites. Absent/unparseable on an older VM -> no verdict.
  const diskPct = parseDiskPct(map.DISK_PCT);

  const installed = formatMarker(map.INSTALLED_AT);
  const reprovisioned = formatMarker(map.REPROVISIONED_AT);

  const out = { vmName: map.AGENT_NAME || "", ubuntu: map.UBUNTU || "", resources, agents, projects };
  // Only emit these when the VM actually reported a marker — the webview shows the
  // "installed —" / "reprovisioned —" placeholder for an absent/unknown value.
  if (installed) out.installed = installed;
  if (reprovisioned) out.reprovisioned = reprovisioned;
  // null (unknown) is deliberately NOT sent: the webview then leaves the warning
  // as-is rather than claiming a healthy disk it has no reading for.
  if (diskPct !== null) out.diskPct = diskPct;
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
  return { online: true, host, hostShort, ...toState(parseProbe(r.stdout), { host }) };
}

module.exports = { REMOTE_PROBE, extractVersion, formatMarker, parseDiskPct, parseProbe, toState, probe, isSafeOrigin };
