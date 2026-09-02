"use strict";
// Plain-node unit tests for the SSH arg builder and the probe parser. No deps.
// Run: node probe.test.js
const ssh = require("../src/ssh");
const probe = require("../src/probe");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// ── buildSshArgs ──────────────────────────────────────────────────────────────
const withKey = ssh.buildSshArgs(ssh.DEFAULTS, "true", true);
ok("withKey uses -i identity", withKey.includes("-i") && withKey.some((a) => a.includes(ssh.DEFAULTS.keyName)));
ok("withKey sets IdentitiesOnly", withKey.includes("IdentitiesOnly=yes"));
ok("withKey targets root@vmHost", withKey.includes(`${ssh.DEFAULTS.user}@${ssh.DEFAULTS.vmHost}`));
ok("withKey command is last arg", withKey[withKey.length - 1] === "true");
ok("withKey BatchMode set", withKey.includes("BatchMode=yes"));

const noKey = ssh.buildSshArgs(ssh.DEFAULTS, "true", false);
ok("noKey falls back to host alias", noKey.includes(ssh.DEFAULTS.hostAlias) && !noKey.includes("-i"));
ok("noKey command is last arg", noKey[noKey.length - 1] === "true");

// ── extractVersion ────────────────────────────────────────────────────────────
ok("version: claude", probe.extractVersion("2.1.196 (Claude Code)") === "2.1.196");
ok("version: codex", probe.extractVersion("codex-cli 0.142.4") === "0.142.4");
ok("version: opencode", probe.extractVersion("1.17.11") === "1.17.11");
ok("version: empty", probe.extractVersion("") === "");
ok("version: prerelease", probe.extractVersion("v2.1.196-beta.1") === "2.1.196-beta.1");

// ── formatMarker ──────────────────────────────────────────────────────────────
ok("marker: ISO -> date", probe.formatMarker("2026-07-01T03:44:06Z") === "2026-07-01");
ok("marker: ISO w/o Z still yields date", probe.formatMarker("2026-12-31T23:59:59") === "2026-12-31");
ok("marker: empty -> empty", probe.formatMarker("") === "");
ok("marker: null -> empty", probe.formatMarker(null) === "");
ok("marker: whitespace -> empty", probe.formatMarker("   ") === "");
ok("marker: unparseable passed through (trimmed)", probe.formatMarker("  never  ") === "never");

// ── parseDiskPct ──────────────────────────────────────────────────────────────
ok("disk%: df Use% column", probe.parseDiskPct("94%") === 94);
ok("disk%: bare number", probe.parseDiskPct("7") === 7);
ok("disk%: whitespace trimmed", probe.parseDiskPct("  100% ") === 100);
ok("disk%: missing -> null", probe.parseDiskPct("") === null);
ok("disk%: null -> null", probe.parseDiskPct(null) === null);
ok("disk%: garbage -> null", probe.parseDiskPct("-") === null);
ok("disk%: out of range -> null", probe.parseDiskPct("120%") === null);

// ── parseProbe + toState ──────────────────────────────────────────────────────
const sample = [
  "HOSTNAME\tagent-vm",
  "UBUNTU\tUbuntu 24.04.4 LTS",
  "MEM_GB\t20",
  "DISK_SIZE\t58G",
  "DISK_USED\t24G",
  "DISK_PCT\t43%",
  "AGENT_NAME\tagent-vm-01",
  "PROJECTS\tdefault,customer-portal",
  "AI_TOOLS\topencode,claude-code,codex",
  "V_CLAUDE\t2.1.196 (Claude Code)",
  "V_CODEX\tcodex-cli 0.142.4",
  "V_OPENCODE\t1.17.11",
  "INSTALLED_AT\t2026-06-01T10:15:00Z",
  "REPROVISIONED_AT\t2026-07-01T03:44:06Z",
  "",
].join("\n");

const st = probe.toState(probe.parseProbe(sample));
ok("state: vmName", st.vmName === "agent-vm-01");
ok("state: ubuntu", st.ubuntu === "Ubuntu 24.04.4 LTS");
ok("state: resources", /20 GB RAM/.test(st.resources) && /24G \/ 58G disk/.test(st.resources), st.resources);
ok("state: diskPct", st.diskPct === 43, String(st.diskPct));
ok("state: no diskPct when the VM didn't report it", !("diskPct" in probe.toState(probe.parseProbe("MEM_GB\t8"))));
ok("state: 3 agents", st.agents.length === 3);
ok("state: claude version", st.agents.find((a) => a.id === "claude-code").version === "2.1.196");
ok("state: codex version", st.agents.find((a) => a.id === "codex").version === "0.142.4");
ok("state: opencode version", st.agents.find((a) => a.id === "opencode").version === "1.17.11");
ok("state: 2 selected projects", st.projects.length === 2 && st.projects.every((p) => p.selected));
ok("state: installed marker mapped + formatted", st.installed === "2026-06-01", st.installed);
ok("state: reprovisioned marker mapped + formatted", st.reprovisioned === "2026-07-01", st.reprovisioned);

// agent listed even if version missing but tool selected
const partial = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\n"));
ok("state: selected tool without version still listed", partial.agents.length === 1 && partial.agents[0].version === "—");

// No marker (older VM / unreadable provisioned.env): the fields are OMITTED so the
// webview keeps its "installed —" / "reprovisioned —" placeholders.
const noMarker = probe.toState(probe.parseProbe("AGENT_NAME\tagent-vm-01\n"));
ok("state: installed omitted when no marker", !("installed" in noMarker));
ok("state: reprovisioned omitted when no marker", !("reprovisioned" in noMarker));

// A reprovision-only marker (installed empty for whatever reason) still surfaces
// reprovisioned, and omits installed rather than showing a bogus value.
const reOnly = probe.toState(probe.parseProbe("INSTALLED_AT\t\nREPROVISIONED_AT\t2026-07-01T03:44:06Z\n"));
ok("state: reprovisioned surfaced when installed blank", reOnly.reprovisioned === "2026-07-01" && !("installed" in reOnly));

// The REMOTE_PROBE script emits the marker keys from /etc/construct/provisioned.env.
ok("probe script reads provisioned.env", /provisioned\.env/.test(probe.REMOTE_PROBE));
ok("probe script emits INSTALLED_AT", /emit INSTALLED_AT/.test(probe.REMOTE_PROBE));
ok("probe script emits REPROVISIONED_AT", /emit REPROVISIONED_AT/.test(probe.REMOTE_PROBE));

// Version detection captures stderr (codex prints --version there) and greps the semver
// from anywhere in the output — not just stdout line 1 (which showed "—" for codex).
ok("probe script captures stderr for --version (2>&1)", /--version 2>&1/.test(probe.REMOTE_PROBE));
ok("probe script greps a semver from version output", /grep -oE '\[0-9\]/.test(probe.REMOTE_PROBE));
ok("probe script detects all three agents' versions", /ver claude/.test(probe.REMOTE_PROBE) && /ver codex/.test(probe.REMOTE_PROBE) && /ver opencode/.test(probe.REMOTE_PROBE));

// ── T3 Code (opt-in web GUI, not part of AI_TOOLS) ───────────────────────────
ok("probe script emits T3CODE flag + port + channel + t3 version + service state",
  /emit T3CODE /.test(probe.REMOTE_PROBE) && /emit T3CODE_PORT /.test(probe.REMOTE_PROBE) && /emit T3CODE_CHANNEL /.test(probe.REMOTE_PROBE) && /ver t3/.test(probe.REMOTE_PROBE) && /emit T3_ACTIVE /.test(probe.REMOTE_PROBE));
const t3on = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\nT3CODE\ttrue\nT3CODE_PORT\t5177\nT3CODE_CHANNEL\tstable\nV_T3\tt3 v0.0.28\nT3_ACTIVE\tactive\n"));
const t3agent = t3on.agents.find((a) => a.id === "t3code");
ok("state: t3code listed when enabled, version extracted", !!t3agent && t3agent.version === "0.0.28");
ok("state: t3code webui true when the service is running", !!t3agent && t3agent.webui === true && /:5177/.test(t3agent.detail), t3agent && t3agent.detail);
ok("state: t3code channel=stable, detail without nightly suffix", !!t3agent && t3agent.channel === "stable" && !/nightly/.test(t3agent.detail));
// Nightly channel: detail must include "· nightly" so it's visually distinguishable.
const t3night = probe.toState(probe.parseProbe("T3CODE\ttrue\nT3CODE_PORT\t5177\nT3CODE_CHANNEL\tnightly\nV_T3\t0.0.30-nightly.20260728\nT3_ACTIVE\tactive\n"));
const t3nAgent = t3night.agents.find((a) => a.id === "t3code");
ok("state: t3code nightly detail includes · nightly", !!t3nAgent && /· nightly/.test(t3nAgent.detail));
ok("state: t3code nightly channel=nightly + version extracted", !!t3nAgent && t3nAgent.channel === "nightly" && t3nAgent.version === "0.0.30-nightly.20260728");
// Installed but toggled off / service stopped: still listed (show/update a
// leftover install), but NO webui button — nothing is listening to open.
const t3inst = probe.toState(probe.parseProbe("T3CODE\tfalse\nV_T3\t0.0.28\nT3_ACTIVE\tinactive\n"));
const t3instAgent = t3inst.agents.find((a) => a.id === "t3code");
ok("state: t3code listed when installed but flag off", !!t3instAgent);
ok("state: t3code webui false when the service is stopped", !!t3instAgent && t3instAgent.webui === false);
ok("state: t3code defaults to channel=stable when absent", !!t3instAgent && t3instAgent.channel === "stable");
// Enabled but binary missing (install pending): listed with the "—" placeholder.
const t3pending = probe.toState(probe.parseProbe("T3CODE\ttrue\n"));
ok("state: t3code enabled without version shows placeholder + default port, no webui",
  t3pending.agents.some((a) => a.id === "t3code" && a.version === "—" && /:5177/.test(a.detail) && a.webui === false));
const t3absent = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\nT3_ACTIVE\tinactive\n"));
ok("state: no t3code when neither enabled nor installed", !t3absent.agents.some((a) => a.id === "t3code"));

// ── T3 Code over HTTPS (bin/setup-t3-https.sh) ───────────────────────────────
ok("probe script emits the HTTPS keys",
  /emit T3CODE_HTTPS /.test(probe.REMOTE_PROBE) && /emit T3CODE_HTTPS_PORT /.test(probe.REMOTE_PROBE) &&
  /emit T3CODE_PUBLIC_BASE_URL /.test(probe.REMOTE_PROBE));
// `^T3CODE_HTTPS=` must not also swallow the T3CODE_HTTPS_PORT line.
ok("probe script reads T3CODE_HTTPS with an anchored, '='-terminated match",
  /s\/\^T3CODE_HTTPS=\/\/p/.test(probe.REMOTE_PROBE));
const t3agentOf = (map, opts) => probe.toState(probe.parseProbe(map), opts).agents.find((a) => a.id === "t3code");
// Off (or absent): today's http origin and port, unchanged.
const httpAgent = t3agentOf("T3CODE\ttrue\nT3CODE_PORT\t5177\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: without HTTPS the url + detail stay on the http port",
  !!httpAgent && httpAgent.url === "http://agent-vm.mshome.net:5177" && /:5177/.test(httpAgent.detail) &&
  !/https/.test(httpAgent.detail));
// READINESS IS THE RECORDED ORIGIN, NOT THE PREFERENCE. setup-t3-https.sh writes
// T3CODE_PUBLIC_BASE_URL only when the proxy actually came up, and DPoP proofs are
// bound to that exact origin.
const httpsAgent = t3agentOf("T3CODE\ttrue\nT3CODE_PORT\t5177\nT3CODE_HTTPS\ttrue\nT3CODE_HTTPS_PORT\t5178\nT3CODE_PUBLIC_BASE_URL\thttps://agent-vm.mshome.net:5178\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: a recorded https origin is the url the panel opens",
  !!httpsAgent && httpsAgent.url === "https://agent-vm.mshome.net:5178");
ok("state: HTTPS on -> detail names the https port and marks the scheme",
  !!httpsAgent && /:5178/.test(httpsAgent.detail) && /https/.test(httpsAgent.detail), httpsAgent && httpsAgent.detail);
const publicAgent = t3agentOf("T3CODE\ttrue\nT3CODE_HTTPS\ttrue\nT3CODE_PUBLIC_BASE_URL\thttps://vm.example.com:6443\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: the recorded origin wins over the probed host, port included",
  !!publicAgent && publicAgent.url === "https://vm.example.com:6443" && /:6443/.test(publicAgent.detail));
// THE FAILED-SETUP CASE: preference kept for a retry, origin cleared. The panel
// must show/open the working http URL, not a dead https listener.
const prefOnlyAgent = t3agentOf("T3CODE\ttrue\nT3CODE_PORT\t5177\nT3CODE_HTTPS\ttrue\nT3CODE_HTTPS_PORT\t5178\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: T3CODE_HTTPS=true with NO recorded origin -> http url (failed setup)",
  !!prefOnlyAgent && prefOnlyAgent.url === "http://agent-vm.mshome.net:5177");
ok("state: ...and the detail does not claim https either",
  !!prefOnlyAgent && !/https/.test(prefOnlyAgent.detail) && /:5177/.test(prefOnlyAgent.detail),
  prefOnlyAgent && prefOnlyAgent.detail);
// A value that is not an origin comes from a user-editable file and would end up
// in openExternal: rejected, and the http fallback is used.
const poisonAgent = t3agentOf("T3CODE\ttrue\nT3CODE_HTTPS\ttrue\nT3CODE_PUBLIC_BASE_URL\tjavascript:alert(1)\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: a non-origin T3CODE_PUBLIC_BASE_URL is rejected, http fallback used",
  !!poisonAgent && poisonAgent.url === "http://agent-vm.mshome.net:5177");
const httpPublicAgent = t3agentOf("T3CODE\ttrue\nT3CODE_PUBLIC_BASE_URL\thttp://vm.example.com:5177\nT3_ACTIVE\tactive\n", { host: "agent-vm.mshome.net" });
ok("state: an http public origin is not treated as HTTPS-ready",
  !!httpPublicAgent && !/https/.test(httpPublicAgent.detail));
ok("isSafeOrigin: accepts http/https origins with and without a port, brackets IPv6",
  probe.isSafeOrigin("https://vm.example.com:5178") && probe.isSafeOrigin("http://vm") &&
  probe.isSafeOrigin("https://[2001:db8::1]:5178"));
ok("isSafeOrigin: rejects paths, other schemes, spaces and empties",
  !probe.isSafeOrigin("https://vm/pair") && !probe.isSafeOrigin("javascript:alert(1)") &&
  !probe.isSafeOrigin("https://vm example") && !probe.isSafeOrigin("") && !probe.isSafeOrigin(null));
// config-set.sh single-quotes any value outside its safe charset -- which an IPv6
// origin (brackets) always is. The probe reads the RAW config.env line, so it has
// to decode that rendering; the fixture below is produced by the real script.
(() => {
  const cp = require("child_process");
  const os = require("os");
  const fsx = require("fs");
  const path = require("path");
  const dir = fsx.mkdtempSync(path.join(os.tmpdir(), "t3probe-"));
  const cfg = path.join(dir, "config.env");
  fsx.writeFileSync(cfg, "");
  const configSet = path.join(__dirname, "..", "..", "bin", "config-set.sh");
  cp.spawnSync("bash", [configSet, cfg, "T3CODE_PUBLIC_BASE_URL", "https://[2001:db8::1]:5178"], { encoding: "utf8" });
  // Exactly what the remote probe emits: the raw text after the '=' .
  const raw = String(fsx.readFileSync(cfg, "utf8")).split("\n")
    .find((l) => l.startsWith("T3CODE_PUBLIC_BASE_URL=")).slice("T3CODE_PUBLIC_BASE_URL=".length);
  fsx.rmSync(dir, { recursive: true, force: true });
  ok("test precondition: config-set.sh really quotes an IPv6 origin", raw.startsWith("'") && raw.endsWith("'"), raw);
  const v6Agent = t3agentOf(`T3CODE\ttrue\nT3CODE_HTTPS\ttrue\nT3CODE_PUBLIC_BASE_URL\t${raw}\nT3_ACTIVE\tactive\n`,
    { host: "agent-vm.mshome.net" });
  ok("state: a config-set.sh-quoted IPv6 origin is decoded and accepted",
    !!v6Agent && v6Agent.url === "https://[2001:db8::1]:5178", v6Agent && v6Agent.url);
  ok("state: ...and its port shows in the detail", !!v6Agent && /:5178/.test(v6Agent.detail), v6Agent && v6Agent.detail);
})();
ok("cfgUnquote: decodes config-set.sh's rendering, including escaped apostrophes",
  probe.cfgUnquote("'https://[2001:db8::1]:5178'") === "https://[2001:db8::1]:5178" &&
  probe.cfgUnquote("'it'\\''s'") === "it's" &&
  probe.cfgUnquote("https://vm:5178") === "https://vm:5178" &&
  probe.cfgUnquote("") === "" && probe.cfgUnquote(null) === "");
ok("originPort: reads the port, ignoring the colons inside an IPv6 literal",
  probe.originPort("https://vm:5178") === "5178" && probe.originPort("https://[2001:db8::1]:6443") === "6443" &&
  probe.originPort("https://vm") === "" && probe.originPort("https://[2001:db8::1]") === "");
// Without a host (toState called with one argument, as the older callers do) the
// entry simply carries no url -- never a half-built one.
const noHostAgent = t3agentOf("T3CODE\ttrue\nT3CODE_HTTPS\ttrue\n");
ok("state: no host -> no url field", !!noHostAgent && !("url" in noHostAgent));
// ...but a recorded origin needs no host at all.
const noHostReady = t3agentOf("T3CODE\ttrue\nT3CODE_PUBLIC_BASE_URL\thttps://vm.example.com:5178\n");
ok("state: a recorded origin yields a url even without a probed host",
  !!noHostReady && noHostReady.url === "https://vm.example.com:5178");

console.log(`\n  probe/ssh unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
