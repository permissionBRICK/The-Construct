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

// agent listed even if version missing but tool selected
const partial = probe.toState(probe.parseProbe("AI_TOOLS\tclaude-code\n"));
ok("state: selected tool without version still listed", partial.agents.length === 1 && partial.agents[0].version === "—");

console.log(`\n  probe/ssh unit tests — ${pass}/${pass + fail} passed\n`);
process.exit(fail ? 1 : 0);
