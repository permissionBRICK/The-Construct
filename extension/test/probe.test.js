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

// ── parseProbe + toState ──────────────────────────────────────────────────────
const sample = [
  "HOSTNAME\tagent-vm",
  "UBUNTU\tUbuntu 24.04.4 LTS",
  "MEM_GB\t20",
  "DISK_SIZE\t58G",
  "DISK_USED\t24G",
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
ok("probe script emits T3CODE flag + port + t3 version + service state",
  /emit T3CODE /.test(probe.REMOTE_PROBE) && /emit T3CODE_PORT /.test(probe.REMOTE_PROBE) && /ver t3/.test(probe.REMOTE_PROBE) && /emit T3_ACTIVE /.test(probe.REMOTE_PROBE));
const t3on = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\nT3CODE\ttrue\nT3CODE_PORT\t5177\nV_T3\tt3 v0.0.28\nT3_ACTIVE\tactive\n"));
const t3agent = t3on.agents.find((a) => a.id === "t3code");
ok("state: t3code listed when enabled, version extracted", !!t3agent && t3agent.version === "0.0.28");
ok("state: t3code webui true when the service is running", !!t3agent && t3agent.webui === true && /:5177/.test(t3agent.detail), t3agent && t3agent.detail);
// Installed but toggled off / service stopped: still listed (show/update a
// leftover install), but NO webui button — nothing is listening to open.
const t3inst = probe.toState(probe.parseProbe("T3CODE\tfalse\nV_T3\t0.0.28\nT3_ACTIVE\tinactive\n"));
const t3instAgent = t3inst.agents.find((a) => a.id === "t3code");
ok("state: t3code listed when installed but flag off", !!t3instAgent);
ok("state: t3code webui false when the service is stopped", !!t3instAgent && t3instAgent.webui === false);
// Enabled but binary missing (install pending): listed with the "—" placeholder.
const t3pending = probe.toState(probe.parseProbe("T3CODE\ttrue\n"));
ok("state: t3code enabled without version shows placeholder + default port, no webui",
  t3pending.agents.some((a) => a.id === "t3code" && a.version === "—" && /:5177/.test(a.detail) && a.webui === false));
const t3absent = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\nT3_ACTIVE\tinactive\n"));
ok("state: no t3code when neither enabled nor installed", !t3absent.agents.some((a) => a.id === "t3code"));

console.log(`\n  probe/ssh unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
