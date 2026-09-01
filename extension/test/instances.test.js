"use strict";
// Plain-node unit tests for the B3 instance registry (src/instances.js), the
// instance-aware ssh argv (src/ssh.js), the per-instance lifecycle invocation
// (src/lifecycle.js) and the instance-aware scripts-dir resolution (src/host.js).
//
// The bar these tests exist to hold: with NO registry file / NO settings / NO
// instance passed, every output is byte-identical to what shipped before instances
// existed. Run: node --test test/instances.test.js  (or: node test/instances.test.js)

const fs = require("fs");
const os = require("os");
const path = require("path");
const inst = require("../src/instances");
const ssh = require("../src/ssh");
const life = require("../src/lifecycle");
const drivers = require("../src/drivers");
const host = require("../src/host");
const notify = require("../src/notify");
const audio = require("../src/audio");
const usage = require("../src/usage");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, actual, expected) =>
  ok(name, actual === expected, `got ${JSON.stringify(actual)}, want ${JSON.stringify(expected)}`);
const deepEq = (name, a, b) =>
  ok(name, JSON.stringify(a) === JSON.stringify(b), `got ${JSON.stringify(a)}, want ${JSON.stringify(b)}`);

// A scratch tree for the fs-touching cases (never the real %LOCALAPPDATA%).
const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), "construct-instances-"));
function writeRegistry(text) {
  const dir = fs.mkdtempSync(path.join(tmpRoot, "reg-"));
  const p = path.join(dir, "instances.json");
  fs.writeFileSync(p, text, "utf8");
  return p;
}

// ── Name validation ──────────────────────────────────────────────────────────
console.log("\n=== instance names ===");
ok("name: agent-vm ok", inst.isValidName("agent-vm"));
ok("name: single char ok", inst.isValidName("a"));
ok("name: leading digit ok", inst.isValidName("9lives"));
ok("name: 40 chars ok", inst.isValidName("a".repeat(40)));
ok("name: 41 chars rejected", !inst.isValidName("a".repeat(41)));
ok("name: uppercase rejected", !inst.isValidName("Work-VM"));
ok("name: underscore rejected", !inst.isValidName("work_vm"));
ok("name: dot rejected", !inst.isValidName("work.vm"));
ok("name: slash rejected", !inst.isValidName("work/vm"));
ok("name: leading dash rejected", !inst.isValidName("-work"));
ok("name: empty rejected", !inst.isValidName(""));
ok("name: non-string rejected", !inst.isValidName(null) && !inst.isValidName(7));

// ── The synthesized default: THE ZERO-CHANGE BAR ─────────────────────────────
console.log("\n=== default instance synthesis (zero-change path) ===");
const empty = inst.load({ path: path.join(tmpRoot, "nope", "instances.json") });
eq("no registry: exactly one instance", empty.instances.length, 1);
eq("no registry: default is agent-vm", empty.defaultInstance, "agent-vm");
eq("no registry: exists=false", empty.exists, false);
deepEq("no registry: no problems (absence is normal)", empty.problems, []);
ok("no registry: nothing was written", !fs.existsSync(path.join(tmpRoot, "nope", "instances.json")));
const d = inst.resolve(empty, null);
eq("default: name", d.name, "agent-vm");
eq("default: backend", d.backend, "hyperv-local");
eq("default: vmName", d.vmName, "Agent-VM");
eq("default: vmHost", d.vmHost, "agent-vm.mshome.net");
eq("default: sshPort", d.sshPort, 22);
eq("default: hostAlias", d.hostAlias, "agent-vm");
eq("default: keyName", d.keyName, "agent_vm_ed25519");
eq("default: configBranch", d.configBranch, "vm");
eq("default: scriptsDir", d.scriptsDir, null);
ok("default: isDefaultInstance", inst.isDefaultInstance(d));
ok("undefined instance counts as the default", inst.isDefaultInstance(undefined));
deepEq("default cfg == ssh.DEFAULTS identity", inst.toSshCfg(d), {
  vmHost: ssh.DEFAULTS.vmHost, hostAlias: ssh.DEFAULTS.hostAlias,
  keyName: ssh.DEFAULTS.keyName, sshPort: ssh.DEFAULTS.sshPort,
});
deepEq("no instance -> empty cfg (ssh defaults stand)", inst.toSshCfg(null), {});

// ── Derivation for a non-default instance ────────────────────────────────────
console.log("\n=== derived defaults (non-default instance) ===");
const w = inst.deriveDefaults("work-vm", {});
eq("derived: backend", w.backend, "hyperv-local");
eq("derived: vmName", w.vmName, "work-vm");
eq("derived: vmHost", w.vmHost, "work-vm.mshome.net");
eq("derived: sshPort", w.sshPort, 22);
eq("derived: hostAlias is the BARE name", w.hostAlias, "work-vm");
eq("derived: keyName", w.keyName, "construct_work-vm_ed25519");
eq("derived: configBranch", w.configBranch, "vm-work-vm");
ok("derived: configBranch has no slash (refs/heads/vm would collide)", !w.configBranch.includes("/"));
eq("derived: scriptsDir", w.scriptsDir, null);
ok("derived: NOT the default instance", !inst.isDefaultInstance(w));

const explicit = inst.deriveDefaults("work-vm", {
  backend: "hyperv-remote", vmName: "BuildBox-3", sshHost: "buildbox.example.local", sshPort: 2201,
  hostAlias: "custom-alias", keyName: "custom_key", configBranch: "branch-x",
  scriptsDir: "C:\\tools\\construct",
  service: { url: "https://buildbox:7462", auth: "token" }, owner: "DOMAIN\\christoph",
});
eq("explicit: backend", explicit.backend, "hyperv-remote");
eq("explicit: vmName", explicit.vmName, "BuildBox-3");
eq("explicit: sshHost -> vmHost", explicit.vmHost, "buildbox.example.local");
eq("explicit: sshPort", explicit.sshPort, 2201);
eq("explicit: hostAlias", explicit.hostAlias, "custom-alias");
eq("explicit: keyName", explicit.keyName, "custom_key");
eq("explicit: configBranch", explicit.configBranch, "branch-x");
eq("explicit: scriptsDir", explicit.scriptsDir, "C:\\tools\\construct");
eq("explicit: service auth", explicit.service.auth, "token");
eq("explicit: owner", explicit.owner, "DOMAIN\\christoph");

// A spelled-out agent-vm with today's values still IS the default...
ok("spelled-out agent-vm is still the default",
  inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { backend: "hyperv-local", vmName: "Agent-VM", sshHost: "agent-vm.mshome.net", sshPort: 22 })));
// ...but any targeting change makes it non-default.
ok("agent-vm on another port is NOT the default",
  !inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { sshPort: 2222 })));
ok("agent-vm with another key is NOT the default",
  !inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { keyName: "other_key" })));

// ── Parsing a real registry ──────────────────────────────────────────────────
console.log("\n=== registry parsing ===");
const regFile = writeRegistry(JSON.stringify({
  version: 1,
  defaultInstance: "work-vm",
  instances: {
    "agent-vm": { backend: "hyperv-local", vmName: "Agent-VM", sshHost: "agent-vm.mshome.net", sshPort: 22 },
    "work-vm": {
      backend: "hyperv-remote", vmName: "work-vm", sshHost: "buildbox.example.local", sshPort: 2201,
      service: { url: "https://buildbox.example.local:7462", auth: "negotiate" }, owner: "DOMAIN\\christoph",
    },
  },
}, null, 2));
const reg = inst.load({ path: regFile });
eq("parse: two instances", reg.instances.length, 2);
deepEq("parse: no problems", reg.problems, []);
eq("parse: exists", reg.exists, true);
eq("parse: defaultInstance honoured", reg.defaultInstance, "work-vm");
eq("parse: default sorts first", reg.instances[0].name, "work-vm");
const wv = inst.resolve(reg, "work-vm");
eq("parse: work-vm host", wv.vmHost, "buildbox.example.local");
eq("parse: work-vm port", wv.sshPort, 2201);
eq("parse: work-vm alias derived as the bare name", wv.hostAlias, "work-vm");
eq("parse: work-vm key derived", wv.keyName, "construct_work-vm_ed25519");
eq("parse: work-vm branch derived", wv.configBranch, "vm-work-vm");
eq("parse: unknown name falls back to the registry default", inst.resolve(reg, "nope").name, "work-vm");
eq("parse: agent-vm still resolvable and unchanged", inst.resolve(reg, "agent-vm").keyName, "agent_vm_ed25519");
ok("parse: the listed agent-vm still reads as the default instance",
  inst.isDefaultInstance(inst.resolve(reg, "agent-vm")));

// ── Malformed registries NEVER throw and always yield the default ────────────
console.log("\n=== malformed registries degrade to the default ===");
for (const [label, text] of [
  ["not JSON", "definitely { not json"],
  ["a JSON array", "[1,2,3]"],
  ["a JSON string", '"hello"'],
  ["empty file", ""],
  ["instances not an object", '{"version":1,"instances":"nope"}'],
  ["entry not an object", '{"version":1,"instances":{"work-vm":"nope"}}'],
]) {
  let r = null, threw = false;
  try { r = inst.load({ path: writeRegistry(text) }); } catch (_) { threw = true; }
  ok(`malformed (${label}): does not throw`, !threw);
  if (!threw) {
    ok(`malformed (${label}): default instance available`, inst.isDefaultInstance(inst.resolve(r, null)));
    if (label !== "empty file") ok(`malformed (${label}): reports a problem`, r.problems.length >= 1);
  }
}
const messy = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, defaultInstance: "ghost",
  instances: {
    "Work_VM": { backend: "hyperv-local" },
    "good-vm": { backend: "martian", sshPort: 99999 },
    "remote-vm": { backend: "hyperv-remote" },
  },
})) });
ok("messy: invalid name skipped", !messy.byName["Work_VM"]);
ok("messy: valid sibling survives", !!messy.byName["good-vm"]);
eq("messy: unknown backend falls back", messy.byName["good-vm"].backend, "hyperv-local");
eq("messy: invalid port falls back to 22", messy.byName["good-vm"].sshPort, 22);
eq("messy: dangling defaultInstance -> agent-vm", messy.defaultInstance, "agent-vm");
ok("messy: agent-vm synthesized alongside", !!messy.byName["agent-vm"]);
ok("messy: invalid name reported", messy.problems.some((p) => p.includes("Work_VM")));
ok("messy: unknown backend reported", messy.problems.some((p) => p.includes("martian")));
ok("messy: invalid sshPort reported", messy.problems.some((p) => p.includes("sshPort")));
ok("messy: dangling default reported", messy.problems.some((p) => p.includes("ghost")));
ok("messy: hyperv-remote without sshHost reported", messy.problems.some((p) => p.includes("no sshHost")));

// A foreign schema version is REFUSED, not partially read: a later version may redefine
// what a field MEANS, so acting on a misread entry could target the wrong machine.
const future = inst.load({ path: writeRegistry('{"version":99,"instances":{"later-vm":{"backend":"hyperv-local"}}}') });
ok("future schema: entries are NOT consumed", !future.byName["later-vm"]);
eq("future schema: only the default remains", future.instances.length, 1);
ok("future schema: the default is byte-identical", inst.isDefaultInstance(inst.resolve(future, null)));
ok("future schema: reported as a problem", future.problems.some((p) => p.includes("version")));
const futureDefault = inst.load({ path: writeRegistry('{"version":2,"defaultInstance":"later-vm","instances":{"later-vm":{}}}') });
eq("future schema: defaultInstance pointer ignored too", futureDefault.defaultInstance, "agent-vm");
// An ABSENT version is still read as v1 (hand-written files routinely omit it).
ok("absent version: still read as v1",
  !!inst.load({ path: writeRegistry('{"instances":{"later-vm":{"backend":"hyperv-local"}}}') }).byName["later-vm"]);

// ── JS/PS NORMALIZATION PARITY MATRIX ────────────────────────────────────────
// Both readers must normalize the SAME malformed input to the SAME instance and the
// SAME problems. test/instances.test.ps1 runs this identical matrix; the two lists are
// kept in step by hand, so change them together.
console.log("\n=== normalization parity (mirrored in test/instances.test.ps1) ===");
const mx = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1,
  instances: {
    "typed-vm": {
      backend: "HYPERV-REMOTE", sshHost: 123, sshPort: "2201",
      hostAlias: true, keyName: 42, configBranch: ["x"], owner: 7,
      service: { url: "https://x", auth: "TOKEN" },
    },
    "svc-vm": { service: "not-an-object" },
    "port-vm": { sshPort: "+2201" },
  },
})) });
const tv = mx.byName["typed-vm"];
eq("parity: uppercase backend rejected (case-sensitive)", tv.backend, "hyperv-local");
eq("parity: numeric sshHost NOT stringified", tv.vmHost, "typed-vm.mshome.net");
eq("parity: digit-string sshPort accepted", tv.sshPort, 2201);
eq("parity: boolean hostAlias -> derived bare name", tv.hostAlias, "typed-vm");
eq("parity: numeric keyName -> derived", tv.keyName, "construct_typed-vm_ed25519");
eq("parity: array configBranch -> derived", tv.configBranch, "vm-typed-vm");
eq("parity: numeric owner -> null", tv.owner, null);
eq("parity: uppercase service auth -> negotiate", tv.service.auth, "negotiate");
eq("parity: scalar service ignored", mx.byName["svc-vm"].service, null);
eq("parity: '+2201' rejected -> 22", mx.byName["port-vm"].sshPort, 22);
for (const f of ["sshHost", "hostAlias", "keyName", "configBranch", "owner"]) {
  ok(`parity: type problem reported for "${f}"`, mx.problems.some((p) => p.includes(`"${f}" must be a string`)));
}
ok("parity: uppercase backend reported", mx.problems.some((p) => p.includes("HYPERV-REMOTE")));
ok("parity: uppercase auth reported", mx.problems.some((p) => p.includes("service auth")));
ok("parity: scalar service reported", mx.problems.some((p) => p.includes('"service" must be an object')));
ok("parity: bad port reported", mx.problems.some((p) => p.includes("invalid sshPort")));

// ── IDENTITY-FIELD FORMAT RULES (mirrored in test/instances.test.ps1) ────────
// A field of the right TYPE can still be unusable — or hostile — once it reaches a
// PowerShell command line, an ssh argv, a key path or a git ref. Such an entry is
// SKIPPED WHOLE (never partially used) and reported.
console.log("\n=== identity-field format rules ===");
const badIdentity = [
  ["vmHost-injection", { sshHost: "-x; Start-Process calc; #" }, "sshHost"],
  ["vmHost-space", { sshHost: "buildbox local" }, "sshHost"],
  ["vmHost-empty-label", { sshHost: "buildbox..local" }, "sshHost"],
  // An EMBEDDED newline is the JS/PS parity trap: .NET's `$` matches just before a
  // final newline where JavaScript's does not, so the PS rules anchor with \A..\z.
  // (A value is trimmed first by both readers, so only an INNER newline survives.)
  ["vmHost-newline", { sshHost: "buildbox\nlocal" }, "sshHost"],
  ["alias-newline", { hostAlias: "work\nvm" }, "hostAlias"],
  ["branch-newline", { configBranch: "vm\nwork" }, "configBranch"],
  ["alias-path", { hostAlias: "../../etc/passwd" }, "hostAlias"],
  ["alias-space", { hostAlias: "work vm" }, "hostAlias"],
  ["key-path", { keyName: "..\\..\\id_rsa" }, "keyName"],
  ["key-slash", { keyName: "sub/dir_ed25519" }, "keyName"],
  // keyName is a WINDOWS FILE NAME (~\.ssh\<keyName>), not just a token: Win32 strips
  // a trailing dot (so this would write over the DEFAULT instance's key file), and a
  // device stem is not a creatable file at all — provisioning would fail with the VM
  // already built. hostAlias keeps the plain token rule; an ssh alias is not a path.
  ["key-trailing-dot", { keyName: "agent_vm_ed25519." }, "keyName"],
  ["key-device-con", { keyName: "CON" }, "keyName"],
  ["key-device-lowercase", { keyName: "con" }, "keyName"],
  ["key-device-nul", { keyName: "NUL" }, "keyName"],
  ["key-device-com1", { keyName: "COM1" }, "keyName"],
  ["key-device-lpt9", { keyName: "lpt9" }, "keyName"],
  ["key-device-with-extension", { keyName: "CON.txt" }, "keyName"],
  ["key-device-two-extensions", { keyName: "con.key.txt" }, "keyName"],
  ["vmname-dot", { vmName: "work.vm" }, "vmName"],
  ["vmname-space", { vmName: "Work VM" }, "vmName"],
  ["vmname-dash-start", { vmName: "-work" }, "vmName"],
  ["branch-reserved", { configBranch: "main" }, "configBranch"],
  ["branch-dotdot", { configBranch: "vm..x" }, "configBranch"],
  ["branch-lock", { configBranch: "vm-x.lock" }, "configBranch"],
  ["branch-case-hijack", { configBranch: "VM" }, "configBranch"],
];
for (const [label, entry, field] of badIdentity) {
  const r = inst.load({ path: writeRegistry(JSON.stringify({ version: 1, instances: { "bad-vm": entry } })) });
  ok(`identity(${label}): the instance is SKIPPED`, !r.byName["bad-vm"]);
  ok(`identity(${label}): the problem names "${field}"`, r.problems.some((p) => p.includes(`"${field}"`)));
  ok(`identity(${label}): the problem says skipped`, r.problems.some((p) => p.includes("skipped")));
}
// A skipped entry never takes the default instance with it.
const skipReg = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "bad-vm": { sshHost: "no spaces allowed" }, "good-vm": {} },
})) });
ok("identity: a valid sibling still loads", !!skipReg.byName["good-vm"]);
ok("identity: the default instance is still synthesized", inst.isDefaultInstance(skipReg.byName["agent-vm"]));
// An explicit default-instance entry that is broken degrades to today's literals
// rather than to a half-usable one.
const brokenDefault = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "agent-vm": { sshHost: "-oProxyCommand=calc" } },
})) });
ok("identity: a broken agent-vm entry falls back to the synthesized default",
  inst.isDefaultInstance(brokenDefault.byName["agent-vm"]));
// The shapes that MUST keep working.
for (const good of [
  { sshHost: "buildbox.example.local" }, { sshHost: "10.0.0.7" }, { sshHost: "host" },
  { sshHost: "fe80::1" }, { sshHost: "2001:db8::8a2e:370:7334" },
  { keyName: "construct_work-vm_ed25519" }, { hostAlias: "work-vm.local" },
  { vmName: "Work-VM" }, { configBranch: "vm-work" }, { configBranch: "feature.x_1" },
  // Both readers TRIM a string field first, so surrounding whitespace is not a problem.
  { sshHost: " buildbox.local\n" },
]) {
  const r = inst.load({ path: writeRegistry(JSON.stringify({ version: 1, instances: { "work-vm": good } })) });
  ok(`identity: ${JSON.stringify(good)} is accepted`, !!r.byName["work-vm"]);
}
// IPv6: a character-class regex would wave through `::::`, `1::2::3` and friends, so
// the rule shape-filters and then PARSES. The same matrix runs in test/instances.test.ps1
// (.NET's IPAddress.TryParse there) — the two readers must agree address for address.
const IPV6_MATRIX = [
  ["::", true], ["::1", true], ["fe80::1", true], ["2001:db8::8a2e:370:7334", true],
  ["1:2:3:4:5:6:7:8", true], ["0:0:0:0:0:0:0:0", true], ["1::", true], ["::2", true],
  ["::ffff:10.0.0.1", true],
  ["::::", false], ["1::2::3", false], ["1:2:3:4:5:6:7:8:9", false], ["1.2.3:4", false],
  ["....:", false], [":::", false], [":1", false], ["1:", false], ["12345::1", false],
  ["::ffff:999.1.1.1", false], ["::ffff:1.2.3.004", false],
  // Rejected by SHAPE on both sides, precisely because the two parsers disagree about
  // them: Node accepts the zone id, .NET accepts the brackets.
  ["fe80::1%eth0", false], ["[::1]", false],
];
for (const [v, want] of IPV6_MATRIX) {
  eq(`ipv6: ${JSON.stringify(v)} -> ${want}`, inst.isIpv6Literal(v), want);
  eq(`ipv6: ${JSON.stringify(v)} as an endpoint -> ${want}`, inst.isHostEndpoint(v), want);
}
for (const bad of ["::::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1.2.3:4", "....:", "fe80::1%eth0"]) {
  const r = inst.load({ path: writeRegistry(JSON.stringify({ version: 1, instances: { "bad-vm": { sshHost: bad } } })) });
  ok(`ipv6: a bogus literal (${bad}) skips the instance`, !r.byName["bad-vm"]);
  ok(`ipv6: ...and is reported (${bad})`, r.problems.some((p) => p.includes("is not a host name or IP address")));
}

// BOTH host spellings are validated, not only the one that wins normalization:
// deriveDefaults prefers sshHost, so an invalid vmHost would otherwise sit unnoticed in
// the file for whatever reads that field next.
const dualHost = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "work-vm": { sshHost: "good.local", vmHost: "-x; calc" } },
})) });
ok("identity: an invalid LOSING vmHost still skips the instance", !dualHost.byName["work-vm"]);
ok("identity: ...and names the field that is wrong", dualHost.problems.some((p) => p.includes('"vmHost"')));
const dualHost2 = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "work-vm": { sshHost: "-x; calc", vmHost: "good.local" } },
})) });
ok("identity: an invalid WINNING sshHost skips it too", !dualHost2.byName["work-vm"]);
ok("identity: ...reported once, not twice",
  dualHost2.problems.filter((p) => p.includes("is not a host name or IP address")).length === 1,
  JSON.stringify(dualHost2.problems));
const dualOk = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "work-vm": { sshHost: "good.local", vmHost: "other.local" } },
})) });
eq("identity: two VALID spellings still load, sshHost winning", dualOk.byName["work-vm"].vmHost, "good.local");

// The key-file rule is STRICTER than the alias rule, and only for keyName.
for (const v of ["CON", "con", "NUL", "COM1", "lpt9", "CON.txt", "con.key.txt", "agent_vm_ed25519."]) {
  ok(`keyfile: ${JSON.stringify(v)} is refused as a key file name`, !inst.isKeyFileName(v));
  ok(`keyfile: ${JSON.stringify(v)} is still a fine ssh alias`, inst.isSafeToken(v));
  const r = inst.load({ path: writeRegistry(JSON.stringify({ version: 1, instances: { "work-vm": { hostAlias: v } } })) });
  ok(`keyfile: ...so hostAlias ${JSON.stringify(v)} still loads`, !!r.byName["work-vm"]);
}
for (const v of ["construct_work-vm_ed25519", "agent_vm_ed25519", "com10_key", "console_key", "a", "nul_key", "con-1"]) {
  ok(`keyfile: ${JSON.stringify(v)} is accepted`, inst.isKeyFileName(v));
}
eq("keyfile: the DEFAULT key name is unaffected", inst.isKeyFileName(inst.DEFAULT_INSTANCE.keyName), true);
eq("keyfile: a derived key name is unaffected", inst.isKeyFileName(inst.deriveDefaults("work-vm", {}).keyName), true);

deepEq("identity: every derived default passes its own rules",
  inst.identityProblems(inst.deriveDefaults("work-vm", {})), []);
deepEq("identity: today's literals pass", inst.identityProblems(inst.DEFAULT_INSTANCE), []);

// Port boundaries + numbers no Int32 can hold. A huge sshPort must be REPORTED and
// fall back to 22, never crash the reader (PowerShell's [int] cast throws on these,
// which would have escaped Read-ConstructInstances and broken "never throws").
const portCases = [
  ["999999999999", 22, true], ["-999999999999", 22, true],
  ["1e20", 22, true], ["1", 1, false], ["65535", 65535, false],
  ["0", 22, true], ["65536", 22, true], ["2201.5", 22, true],
  ["true", 22, true], ['"2201"', 2201, false], ['"+2201"', 22, true],
  ['" 2201 "', 2201, false], ['"0"', 22, true], ['"99999"', 22, true],
];
for (const [literal, want, wantProblem] of portCases) {
  let r = null, threw = false;
  try { r = inst.load({ path: writeRegistry('{"version":1,"instances":{"p-vm":{"sshPort":' + literal + '}}}') }); }
  catch (_) { threw = true; }
  ok(`port ${literal}: does not throw`, !threw);
  if (!threw) {
    eq(`port ${literal}: normalizes to ${want}`, r.byName["p-vm"].sshPort, want);
    eq(`port ${literal}: problem reported = ${wantProblem}`,
      r.problems.some((p) => p.includes("invalid sshPort")), wantProblem);
  }
}

// isDefaultInstance is case-SENSITIVE (=== in JS, -ceq in PowerShell): an explicitly
// cased vmName is a DIFFERENT instance, and the two readers must agree or one side
// would emit target arguments the other omits.
ok("case: vmName 'agent-vm' is NOT the default (Agent-VM is)",
  !inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { vmName: "agent-vm" })));
ok("case: vmHost 'AGENT-VM.mshome.net' is NOT the default",
  !inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { sshHost: "AGENT-VM.mshome.net" })));
ok("case: keyName 'Agent_VM_ed25519' is NOT the default",
  !inst.isDefaultInstance(inst.deriveDefaults("agent-vm", { keyName: "Agent_VM_ed25519" })));

// Scalar top levels are malformed files, not empty registries — they MUST report.
for (const scalar of ["0", "false", "null", '"hello"', "123"]) {
  const r = inst.load({ path: writeRegistry(scalar) });
  ok(`parity: top-level ${scalar} reports a problem`, r.problems.length >= 1);
  ok(`parity: top-level ${scalar} yields the default`, inst.isDefaultInstance(inst.resolve(r, null)));
}

// An unreadable file (not ENOENT) degrades with a problem rather than throwing.
const unreadable = inst.load({
  path: "C:\\nope\\instances.json",
  readFile: () => { const e = new Error("EACCES"); e.code = "EACCES"; throw e; },
});
ok("unreadable file: default instance stands", inst.isDefaultInstance(inst.resolve(unreadable, null)));
ok("unreadable file: reports a problem", unreadable.problems.length === 1);

// ── Active-instance precedence: setting > workspaceState > registry default ──
console.log("\n=== active-instance precedence ===");
const three = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, defaultInstance: "b-vm",
  instances: { "a-vm": {}, "b-vm": {}, "c-vm": {} },
})) });
eq("precedence: setting wins",
  inst.resolveActive({ registry: three, setting: "a-vm", workspaceValue: "c-vm" }).name, "a-vm");
eq("precedence: setting wins (source)",
  inst.resolveActive({ registry: three, setting: "a-vm", workspaceValue: "c-vm" }).source, "setting");
eq("precedence: workspaceState next",
  inst.resolveActive({ registry: three, setting: "", workspaceValue: "c-vm" }).name, "c-vm");
eq("precedence: registry default last",
  inst.resolveActive({ registry: three, setting: "", workspaceValue: "" }).name, "b-vm");
eq("precedence: no inputs at all -> registry default",
  inst.resolveActive({ registry: three }).name, "b-vm");
// An INVALID higher-precedence candidate is skipped, not promoted into a fallback: a
// stale global `construct.instance` must not drag a window that has its own valid
// selection back to the registry default.
const goneSetting = inst.resolveActive({ registry: three, setting: "deleted-vm", workspaceValue: "c-vm" });
eq("precedence: unknown setting yields to the still-valid workspace selection", goneSetting.name, "c-vm");
eq("precedence: ...and reports it as the workspace source", goneSetting.source, "workspace");
ok("precedence: unknown setting still reports a problem", !!goneSetting.problem);
ok("precedence: the problem names the skipped value", goneSetting.problem.includes("deleted-vm"));
const goneBoth = inst.resolveActive({ registry: three, setting: "deleted-vm", workspaceValue: "also-gone" });
eq("precedence: only when EVERY candidate is invalid does the registry default win", goneBoth.name, "b-vm");
eq("precedence: ...reported as the default source", goneBoth.source, "default");
eq("precedence: ...with one problem per skipped candidate", goneBoth.problems.length, 2);
const goneWorkspace = inst.resolveActive({ registry: three, setting: "", workspaceValue: "deleted-vm" });
eq("precedence: unknown workspace value falls back", goneWorkspace.name, "b-vm");
ok("precedence: a valid selection reports no problem",
  !inst.resolveActive({ registry: three, setting: "a-vm" }).problem);
eq("precedence: with NO registry, everything lands on agent-vm",
  inst.resolveActive({ registry: empty, setting: "", workspaceValue: "" }).name, "agent-vm");

// ── Remote-SSH authority matching (a window auto-adopts its VM) ───────────────
console.log("\n=== remote authority matching ===");
eq("match: alias", inst.matchByRemoteHost(reg, "work-vm").name, "work-vm");
eq("match: hostname", inst.matchByRemoteHost(reg, "buildbox.example.local").name, "work-vm");
eq("match: case-insensitive", inst.matchByRemoteHost(reg, "BuildBox.Example.Local").name, "work-vm");
eq("match: the default instance's own alias", inst.matchByRemoteHost(reg, "agent-vm").name, "agent-vm");
eq("match: unknown host -> null", inst.matchByRemoteHost(reg, "somewhere-else"), null);
eq("match: empty -> null", inst.matchByRemoteHost(reg, ""), null);

// ── Mutation + atomic save round-trip ────────────────────────────────────────
console.log("\n=== add / update / remove + atomic save ===");
const base = inst.load({ path: path.join(tmpRoot, "fresh", "instances.json") });
const added = inst.addInstance(base, "work-vm", { sshHost: "buildbox.local", sshPort: 2201 });
eq("add: instance present", added.byName["work-vm"].vmHost, "buildbox.local");
ok("add: the source registry is not mutated", !base.byName["work-vm"]);
ok("add: rejects an invalid name", (() => { try { inst.addInstance(base, "Work_VM", {}); return false; } catch (_) { return true; } })());
ok("add: rejects a duplicate", (() => { try { inst.addInstance(added, "work-vm", {}); return false; } catch (_) { return true; } })());
// agent-vm is ALWAYS present (synthesized), so "adding" it would silently replace the
// default instance rather than create anything — that is updateInstance's job.
ok("add: rejects the synthesized default as a duplicate",
  (() => { try { inst.addInstance(base, "agent-vm", { sshPort: 2222 }); return false; } catch (_) { return true; } })());
ok("add: the default instance is untouched after a rejected add",
  inst.isDefaultInstance(base.byName["agent-vm"]));
const updated = inst.updateInstance(added, "work-vm", { sshPort: 2299 });
eq("update: field changed", updated.byName["work-vm"].sshPort, 2299);
eq("update: untouched field preserved", updated.byName["work-vm"].vmHost, "buildbox.local");
ok("update: rejects an unknown name", (() => { try { inst.updateInstance(added, "ghost", {}); return false; } catch (_) { return true; } })());
ok("remove: refuses the default instance", (() => { try { inst.removeInstance(updated, "agent-vm"); return false; } catch (_) { return true; } })());
const removed = inst.removeInstance(updated, "work-vm");
ok("remove: gone", !removed.byName["work-vm"]);
ok("remove: agent-vm survives", !!removed.byName["agent-vm"]);
const defaulted = inst.setDefaultInstance(updated, "work-vm");
eq("setDefault: pointer moved", defaulted.defaultInstance, "work-vm");
eq("setDefault: removing that instance resets the pointer",
  inst.removeInstance(defaulted, "work-vm").defaultInstance, "agent-vm");

const savePath = path.join(tmpRoot, "saved", "deep", "instances.json");
inst.save(savePath, updated);
ok("save: created the containing directory", fs.existsSync(savePath));
deepEq("save: left no temp file behind",
  fs.readdirSync(path.dirname(savePath)).filter((f) => f.includes(".tmp.")), []);
const back = inst.load({ path: savePath });
eq("round-trip: two instances", back.instances.length, 2);
eq("round-trip: port preserved", back.byName["work-vm"].sshPort, 2299);
ok("round-trip: the default instance is untouched", inst.isDefaultInstance(back.byName["agent-vm"]));
deepEq("round-trip: no problems", back.problems, []);
ok("save: schema version 1 on disk", JSON.parse(fs.readFileSync(savePath, "utf8")).version === 1);

// ── ssh argv: the default is byte-identical; a port only appears when != 22 ───
console.log("\n=== ssh argv ===");
const defCfg = ssh.resolveCfg({ cfg: inst.toSshCfg(d) });
deepEq("ssh(default, key): argv unchanged from the pre-instance build",
  ssh.buildSshArgs(defCfg, "true", true),
  ["-i", ssh.keyPath(defCfg), "-o", "IdentitiesOnly=yes",
    "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=12",
    "root@agent-vm.mshome.net", "true"]);
deepEq("ssh(default, no key): argv unchanged",
  ssh.buildSshArgs(defCfg, "true", false),
  ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=12",
    "agent-vm", "true"]);
deepEq("ssh(DEFAULTS as-is) == ssh(default instance cfg)",
  ssh.buildSshArgs(ssh.DEFAULTS, "true", true), ssh.buildSshArgs(defCfg, "true", true));
ok("ssh: no -p for the default port", ssh.buildSshArgs(defCfg, "true", true).indexOf("-p") < 0);
ok("ssh: no -p when sshPort is missing entirely",
  ssh.buildSshArgs(ssh.resolveCfg({ cfg: { vmHost: "x", hostAlias: "y", keyName: "k", sshPort: undefined } }), "true", false).indexOf("-p") < 0);

const remoteCfg = ssh.resolveCfg({ cfg: inst.toSshCfg(inst.deriveDefaults("work-vm", { sshHost: "buildbox.local", sshPort: 2201 })) });
const remoteArgs = ssh.buildSshArgs(remoteCfg, "true", true);
ok("ssh(instance): -p 2201 emitted", remoteArgs.includes("-p") && remoteArgs[remoteArgs.indexOf("-p") + 1] === "2201");
ok("ssh(instance): dials the instance host", remoteArgs.includes("root@buildbox.local"));
ok("ssh(instance): uses the instance key", remoteArgs.some((a) => String(a).includes("construct_work-vm_ed25519")));
deepEq("ssh(instance, no key): falls back to the instance alias",
  ssh.buildSshArgs(remoteCfg, "true", false).slice(-2), ["work-vm", "true"]);
eq("ssh: normalizeSshPort(garbage) -> 22", ssh.normalizeSshPort("nope"), 22);
eq("ssh: normalizeSshPort(0) -> 22", ssh.normalizeSshPort(0), 22);
eq("ssh: normalizeSshPort(70000) -> 22", ssh.normalizeSshPort(70000), 22);
eq("ssh: normalizeSshPort('2201') -> 2201", ssh.normalizeSshPort("2201"), 2201);

// The two OTHER argv builders (notify watcher, mic tunnel) share the rule.
const watchDefault = notify.buildWatchArgs(ssh, inst.toSshCfg(d), true);
ok("notify watcher (default): no -p", watchDefault.indexOf("-p") < 0);
const watchRemote = notify.buildWatchArgs(ssh, inst.toSshCfg(inst.deriveDefaults("work-vm", { sshPort: 2201 })), true);
ok("notify watcher (instance): -p 2201", watchRemote[watchRemote.indexOf("-p") + 1] === "2201");
const tunDefault = audio.buildTunnelArgs(ssh, inst.toSshCfg(d), 8767, 50000, true);
ok("mic tunnel (default): no -p", tunDefault.indexOf("-p") < 0);
const tunRemote = audio.buildTunnelArgs(ssh, inst.toSshCfg(inst.deriveDefaults("work-vm", { sshPort: 2201 })), 8767, 50000, true);
ok("mic tunnel (instance): -p 2201", tunRemote[tunRemote.indexOf("-p") + 1] === "2201");

// HostAudio's DEFAULT key-existence probe must look up the INSTANCE's key file. When
// it checked the module defaults instead, the enable script (which resolves the cfg
// properly) would install the shim over construct_<name>_ed25519 while the persistent
// tunnel decided "no key", fell back to the ~/.ssh/config alias a direct-cfg instance
// need not have, and failed with the shim already in place.
const probedKeyPaths = [];
new audio.HostAudio({
  cfg: inst.toSshCfg(inst.deriveDefaults("work-vm", { sshHost: "buildbox.local", sshPort: 2201 })),
  _ssh: {
    keyPath: (c) => { probedKeyPaths.push(c.keyName); return "/fake/" + c.keyName; },
    resolveCfg: ssh.resolveCfg,
  },
})._hasKey();
deepEq("HostAudio: the key lookup receives the INSTANCE cfg", probedKeyPaths, ["construct_work-vm_ed25519"]);
const defaultKeyPaths = [];
new audio.HostAudio({
  cfg: inst.toSshCfg(d),
  _ssh: { keyPath: (c) => { defaultKeyPaths.push(c.keyName); return "/fake"; }, resolveCfg: ssh.resolveCfg },
})._hasKey();
deepEq("HostAudio: the default instance still probes agent_vm_ed25519", defaultKeyPaths, ["agent_vm_ed25519"]);
const noCfgKeyPaths = [];
new audio.HostAudio({
  _ssh: { keyPath: (c) => { noCfgKeyPaths.push(c.keyName); return "/fake"; }, resolveCfg: ssh.resolveCfg },
})._hasKey();
deepEq("HostAudio: no cfg at all still probes the module default", noCfgKeyPaths, ["agent_vm_ed25519"]);

// ── lifecycle.buildInvocation: default argv byte-identical, instance args gated ─
console.log("\n=== lifecycle invocation ===");
const SETTINGS = { gitName: "Neo", gitEmail: "neo@zion.io", serveWeb: true, mic: false };
const ALL = ["VmHost", "HostAlias", "SshPort", "LocalKeyName", "VmName", "ConfigBranch"];
for (const action of ["reprovision", "exportConfig", "reinstall", "redownload"]) {
  const bare = life.buildInvocation(action, { settings: SETTINGS, backupDir: "C:\\b", enabled: true });
  const withDefault = life.buildInvocation(action, {
    settings: SETTINGS, backupDir: "C:\\b", enabled: true, instance: d, instanceParams: ALL,
  });
  const withUndefined = life.buildInvocation(action, {
    settings: SETTINGS, backupDir: "C:\\b", enabled: true, instance: undefined,
  });
  deepEq(`${action}: default instance argv == no-instance argv`, withDefault.args, bare.args);
  deepEq(`${action}: undefined instance argv == no-instance argv`, withUndefined.args, bare.args);
  ok(`${action}: no target args leak into the default path`,
    !withDefault.args.some((a) => ["-VmHost", "-HostAlias", "-SshPort", "-LocalKeyName", "-VmName"].includes(a)));
}
const chkDefault = life.buildInvocation("setCheckpoints", { enabled: true, instance: d, instanceParams: ALL });
deepEq("setCheckpoints: default instance argv == no-instance argv",
  chkDefault.args, life.buildInvocation("setCheckpoints", { enabled: true }).args);

const workInst = inst.deriveDefaults("work-vm", { sshHost: "buildbox.local", sshPort: 2201 });
const pair = (args, flag) => { const i = args.indexOf(flag); return i < 0 ? null : args[i + 1]; };

const reproI = life.buildInvocation("reprovision", { settings: SETTINGS, instance: workInst, instanceParams: ALL });
eq("reprovision(instance): -VmHost", pair(reproI.args, "-VmHost"), "buildbox.local");
eq("reprovision(instance): -HostAlias", pair(reproI.args, "-HostAlias"), "work-vm");
eq("reprovision(instance): -SshPort", pair(reproI.args, "-SshPort"), "2201");
eq("reprovision(instance): -LocalKeyName", pair(reproI.args, "-LocalKeyName"), "construct_work-vm_ed25519");
ok("reprovision(instance): no -VmName (Provision-AgentVM.ps1 has none)", reproI.args.indexOf("-VmName") < 0);
eq("reprovision(instance): still the Provision script", reproI.script, life.PROVISION);
ok("reprovision(instance): the normal settings args survive", pair(reproI.args, "-GitUserName") === "Neo");

const exportI = life.buildInvocation("exportConfig", { backupDir: "C:\\b", instance: workInst, instanceParams: ALL });
eq("exportConfig(instance): -VmHost", pair(exportI.args, "-VmHost"), "buildbox.local");
eq("exportConfig(instance): -BackupDir preserved", pair(exportI.args, "-BackupDir"), "C:\\b");

for (const action of ["reinstall", "redownload"]) {
  const a = life.buildInvocation(action, { settings: SETTINGS, instance: workInst, instanceParams: ALL });
  eq(`${action}(instance): -VmName`, pair(a.args, "-VmName"), "work-vm");
  // Auto-Install.ps1 DERIVES the guest hostname/alias/key from -VmName and THROWS on a
  // -VmHost that disagrees, so those must never be emitted on this path.
  ok(`${action}(instance): no -VmHost (Auto-Install derives it and rejects a conflict)`, a.args.indexOf("-VmHost") < 0);
  ok(`${action}(instance): no -HostAlias`, a.args.indexOf("-HostAlias") < 0);
  ok(`${action}(instance): no -SshPort`, a.args.indexOf("-SshPort") < 0);
  ok(`${action}(instance): no -LocalKeyName`, a.args.indexOf("-LocalKeyName") < 0);
  eq(`${action}(instance): still the Auto-Install script`, a.script, life.AUTO_INSTALL);
}
const chkI = life.buildInvocation("setCheckpoints", { enabled: false, instance: workInst, instanceParams: ALL });
eq("setCheckpoints(instance): -VmName", pair(chkI.args, "-VmName"), "work-vm");
eq("setCheckpoints(instance): -Enabled preserved", pair(chkI.args, "-Enabled"), "false");

// ── Version skew FAILS CLOSED for a non-default instance ────────────────────
// Dropping an identity parameter the script doesn't declare does not "degrade
// gracefully" — it RETARGETS the action at whatever the script defaults to, i.e. the
// DEFAULT VM. Reinstall would then delete the wrong VM, reprovision would re-key it.
// So an install that can't take the full identity is refused, loudly.
const partial = life.buildInvocation("reprovision", { settings: {}, instance: workInst, instanceParams: ["VmHost", "HostAlias"] });
ok("skew: a partially-declaring Provision-AgentVM.ps1 is REFUSED", partial.blocked === true);
ok("skew: the refusal names the missing parameters",
  /-SshPort/.test(partial.reason) && /-LocalKeyName/.test(partial.reason));
ok("skew: the refusal says what to do", /Update the Construct scripts/i.test(partial.reason));
ok("skew: a refusal carries NO args to launch", partial.args === undefined && partial.script === undefined);
const none = life.buildInvocation("reprovision", { settings: {}, instance: workInst, instanceParams: [] });
ok("skew: an old scripts dir is refused rather than run against the default VM", none.blocked === true);
for (const action of ["reinstall", "redownload", "setCheckpoints"]) {
  const oldAi = life.buildInvocation(action, { settings: {}, enabled: true, instance: workInst, instanceParams: [] });
  ok(`skew: ${action} without -VmName is refused (it would rebuild the DEFAULT VM)`, oldAi.blocked === true);
  ok(`skew: ${action} refusal names -VmName`, /-VmName/.test(oldAi.reason));
}
const exportOld = life.buildInvocation("exportConfig", { backupDir: "C:\\b", instance: workInst, instanceParams: ["VmHost"] });
ok("skew: exportConfig against an old provisioner is refused too", exportOld.blocked === true);
// The DEFAULT instance is never blocked — that path needs no targeting at all.
for (const action of ["reprovision", "exportConfig", "reinstall", "redownload", "setCheckpoints"]) {
  const dflt = life.buildInvocation(action, { settings: SETTINGS, backupDir: "C:\\b", enabled: true, instance: d, instanceParams: [] });
  ok(`skew: the default instance is never blocked (${action})`, !dflt.blocked && !!dflt.script);
  ok(`skew: no instance at all is never blocked (${action})`,
    !life.buildInvocation(action, { settings: SETTINGS, backupDir: "C:\\b", enabled: true, instanceParams: [] }).blocked);
}
deepEq("checkInstanceSupport: default instance -> null", life.checkInstanceSupport("reinstall", d, []), null);
deepEq("checkInstanceSupport: no instance -> null", life.checkInstanceSupport("reinstall", null, []), null);
deepEq("checkInstanceSupport: unprobed (undefined) params -> null",
  life.checkInstanceSupport("reinstall", workInst, undefined), null);
deepEq("instanceArgs: default instance -> []", life.instanceArgs("reprovision", d, ALL), []);
deepEq("instanceArgs: unknown action -> []", life.instanceArgs("nonsense", workInst, ALL), []);

// ── Backend capability gate: only hyperv-local is driven by the host scripts ─
// The host PowerShell scripts speak to the LOCAL Hyper-V, so a rebuild of a remote
// instance would create/delete a LOCAL VM that merely shares the name.
console.log("\n=== backend capability gate ===");
const remoteInst = inst.deriveDefaults("work-vm", { backend: "hyperv-remote", sshHost: "buildbox.local", sshPort: 2201 });
eq("gate: the fixture really is hyperv-remote", remoteInst.backend, "hyperv-remote");
for (const action of ["reinstall", "redownload", "setCheckpoints"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, enabled: true, instance: remoteInst, instanceParams: ALL });
  ok(`gate: ${action} is refused for a hyperv-remote instance`, r.blocked === true);
  ok(`gate: ${action} explains the remote driver is missing`, /remote driver/i.test(r.reason));
}
for (const action of ["reprovision", "exportConfig"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, backupDir: "C:\\b", instance: remoteInst, instanceParams: ALL });
  ok(`gate: ${action} stays ALLOWED for hyperv-remote (pure SSH to the VM)`, !r.blocked && !!r.script);
  ok(`gate: ${action} still targets the remote endpoint`, r.args.includes("buildbox.local"));
}
// An unknown backend (a registry written by a newer Construct) is refused the same way.
const alienInst = inst.deriveDefaults("work-vm", { sshHost: "buildbox.local" });
alienInst.backend = "proxmox";
ok("gate: an unknown backend is refused the hypervisor actions",
  life.buildInvocation("reinstall", { settings: {}, instance: alienInst, instanceParams: ALL }).blocked === true);
ok("gate: drivers.lifecycleSupport is the single source of truth",
  drivers.lifecycleSupport("hyperv-local", "reinstall").ok === true &&
  drivers.lifecycleSupport("hyperv-remote", "reinstall").ok === false &&
  drivers.lifecycleSupport("hyperv-remote", "reprovision").ok === true);
ok("gate: the local driver declares the capability the gate reads",
  drivers.getDriver("hyperv-local").capabilities.hostLifecycle === true);

// ── -ConfigBranch: emitted only when the provisioner would derive another ref ─
console.log("\n=== config-sync branch threading ===");
const repoRootScripts = path.join(__dirname, "..", "..");
eq("branch: the default alias derives 'vm'", life.derivedConfigBranch("agent-vm"), "vm");
eq("branch: an empty alias derives 'vm'", life.derivedConfigBranch(""), "vm");
eq("branch: 'work' derives 'vm-work'", life.derivedConfigBranch("work"), "vm-work");
eq("branch: a 'construct-' prefix is stripped", life.derivedConfigBranch("construct-work"), "vm-work");
eq("branch: the alias is lowercased+trimmed", life.derivedConfigBranch("  Work-2  "), "vm-work-2");
eq("branch: an unusable alias falls back to 'vm'", life.derivedConfigBranch("work/one"), "vm");
eq("branch: an instance whose branch matches the derivation needs no override",
  life.configBranchOverride(workInst), null);
eq("branch: the default instance never overrides", life.configBranchOverride(d), null);
const teamInst = inst.deriveDefaults("work-vm", { sshHost: "buildbox.local", configBranch: "vm-team" });
eq("branch: a disagreeing configBranch IS an override", life.configBranchOverride(teamInst), "vm-team");
for (const action of ["reprovision", "reinstall", "redownload"]) {
  const same = life.buildInvocation(action, { settings: SETTINGS, instance: workInst, instanceParams: ALL });
  ok(`branch: ${action} emits nothing when the derivation agrees`, same.args.indexOf("-ConfigBranch") < 0);
  const over = life.buildInvocation(action, { settings: SETTINGS, instance: teamInst, instanceParams: ALL });
  eq(`branch: ${action} carries the explicit branch`, pair(over.args, "-ConfigBranch"), "vm-team");
  // ...and refuses rather than letting the VM be initialised on the derived ref.
  const skew = life.buildInvocation(action, {
    settings: SETTINGS, instance: teamInst,
    instanceParams: ALL.filter((p) => p !== "ConfigBranch"),
  });
  ok(`branch: ${action} fails closed when -ConfigBranch can't be passed`, skew.blocked === true);
  ok(`branch: ${action} refusal names the branch`, /vm-team/.test(skew.reason));
}
ok("branch: exportConfig never carries -ConfigBranch (it initialises no store)",
  life.buildInvocation("exportConfig", { backupDir: "C:\\b", instance: teamInst, instanceParams: ALL })
    .args.indexOf("-ConfigBranch") < 0);
ok("branch: exportConfig is NOT blocked by a branch override either",
  !life.buildInvocation("exportConfig", { backupDir: "C:\\b", instance: teamInst, instanceParams: ALL.filter((p) => p !== "ConfigBranch") }).blocked);
ok("branch: setCheckpoints never carries -ConfigBranch",
  life.buildInvocation("setCheckpoints", { enabled: true, instance: teamInst, instanceParams: ALL })
    .args.indexOf("-ConfigBranch") < 0);
// The PowerShell side must be able to receive it all the way down.
if (fs.existsSync(path.join(repoRootScripts, "Auto-Install.ps1"))) {
  for (const [file, want] of [["Auto-Install.ps1", true], ["Create-AgentVM.ps1", true], ["Provision-AgentVM.ps1", true]]) {
    eq(`branch: ${file} declares -ConfigBranch`, life.scriptSupportsParam(repoRootScripts, file, "ConfigBranch"), want);
  }
}

// ── buildCallCommand quotes VALUES, whatever they start with ────────────────
// Registry fields are hand-editable, and the non-elevated launch embeds them in a
// PowerShell command string: a value beginning with '-' must be data, not syntax.
console.log("\n=== non-elevated command quoting ===");
const hostile = inst.deriveDefaults("work-vm", { sshHost: "buildbox.local" });
hostile.vmHost = "-x; Start-Process calc; #";
const hostileInv = life.buildInvocation("reprovision", { settings: {}, instance: hostile, instanceParams: ALL });
const hostileCmd = life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", hostileInv.args, hostileInv.argSpec);
ok("quoting: a leading-dash VALUE is single-quoted, not emitted as code",
  hostileCmd.includes("-VmHost '-x; Start-Process calc; #'"));
ok("quoting: no bare Start-Process leaks into the command",
  !/ Start-Process calc/.test(hostileCmd.replace(/'[^']*'/g, "")));
ok("quoting: parameter names stay bare", hostileCmd.includes("-Action 'provision'"));
ok("quoting: a targeted invocation carries the pair spec", Array.isArray(hostileInv.argSpec));
// ...and the DEFAULT path keeps the LEGACY builder, so its command string is
// byte-identical to the pre-instances one even for a value that starts with '-'.
// (Pinned as a literal in extension/test/lifecycle.test.js too.)
const defInv = life.buildInvocation("reprovision", { settings: SETTINGS, projects: ["-NoProfile", "p1"] });
ok("quoting: the default path gets NO spec", defInv.argSpec === undefined);
eq("quoting: ...so a leading-dash value is still emitted bare, as before",
  life.buildCallCommand("C:\\s\\Provision-AgentVM.ps1", defInv.args, defInv.argSpec),
  "& 'C:\\s\\Provision-AgentVM.ps1' -FromPanel -Action 'provision' -Projects -NoProfile,p1" +
  " -GitUserName 'Neo' -GitEmail 'neo@zion.io' -VsCodeServeWeb 'true' -MicPassthrough 'false' -NonInteractive");

// The parameter probe reads the REAL repo scripts (this worktree is a scripts dir).
console.log("\n=== parameter probing against the real scripts ===");
const repoRoot = path.join(__dirname, "..", "..");
if (fs.existsSync(path.join(repoRoot, life.PROVISION))) {
  const provParams = life.instanceParamSupport(repoRoot, "reprovision");
  ok("probe: Provision-AgentVM.ps1 declares -VmHost", provParams.includes("VmHost"));
  ok("probe: Provision-AgentVM.ps1 declares -HostAlias", provParams.includes("HostAlias"));
  ok("probe: Provision-AgentVM.ps1 declares -SshPort", provParams.includes("SshPort"));
  ok("probe: Provision-AgentVM.ps1 declares -LocalKeyName", provParams.includes("LocalKeyName"));
  ok("probe: Provision-AgentVM.ps1 declares -ConfigBranch", provParams.includes("ConfigBranch"));
  // Auto-Install DERIVES the guest hostname/alias/key from -VmName and throws on a
  // conflicting -VmHost, so identity there is -VmName only (+ the optional branch).
  const aiParams = life.instanceParamSupport(repoRoot, "reinstall");
  deepEq("probe: reinstall considers -VmName and -ConfigBranch only", aiParams, ["VmName", "ConfigBranch"]);
  deepEq("probe: setCheckpoints only ever considers -VmName",
    life.instanceParamSupport(repoRoot, "setCheckpoints"), ["VmName"]);
  ok("probe: a made-up parameter is not found",
    !life.scriptSupportsParam(repoRoot, life.PROVISION, "TotallyNotAParameter"));
} else {
  ok("probe: repo scripts not present here (skipped)", true);
}
deepEq("probe: an absent scripts dir yields no params", life.instanceParamSupport(null, "reprovision"), []);
deepEq("probe: an empty dir yields no params", life.instanceParamSupport(tmpRoot, "reprovision"), []);

// ── host.resolveScriptsDir honours the instance's pinned dir ─────────────────
console.log("\n=== scripts-dir resolution ===");
const pinned = path.join(tmpRoot, "pinned-scripts");
const setting = path.join(tmpRoot, "setting-scripts");
const detected = path.join(tmpRoot, "lad", host.CONTAINER, "slug", "repo-ref");
for (const dir of [pinned, setting, detected]) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, host.MARKER), "param()\n", "utf8");
}
const lad = path.join(tmpRoot, "lad");
eq("scriptsDir: no overrides -> detection (today's behaviour)",
  host.resolveScriptsDir({ localAppData: lad }), detected);
eq("scriptsDir: setting override wins over detection",
  host.resolveScriptsDir({ scriptsDir: setting, localAppData: lad }), setting);
eq("scriptsDir: instance pin wins over the setting",
  host.resolveScriptsDir({ instanceScriptsDir: pinned, scriptsDir: setting, localAppData: lad }), pinned);
eq("scriptsDir: a null instance pin changes nothing (the default instance)",
  host.resolveScriptsDir({ instanceScriptsDir: null, scriptsDir: setting, localAppData: lad }), setting);
eq("scriptsDir: a stale instance pin degrades to the next source",
  host.resolveScriptsDir({ instanceScriptsDir: path.join(tmpRoot, "gone"), scriptsDir: setting, localAppData: lad }), setting);
eq("scriptsDir: an empty instance pin changes nothing",
  host.resolveScriptsDir({ instanceScriptsDir: "", localAppData: lad }), detected);

// ── usage cache is keyed per instance (no cross-VM bleed) ────────────────────
console.log("\n=== usage cache keying ===");
eq("usage: no cfg -> the bare report key (pre-instance behaviour)",
  usage.cacheKeyFor("daily", undefined), "daily");
ok("usage: two instances get different keys",
  usage.cacheKeyFor("daily", inst.toSshCfg(d)) !== usage.cacheKeyFor("daily", inst.toSshCfg(workInst)));
ok("usage: same instance, two periods get different keys",
  usage.cacheKeyFor("daily", inst.toSshCfg(d)) !== usage.cacheKeyFor("monthly", inst.toSshCfg(d)));
eq("usage: the same instance is stable",
  usage.cacheKeyFor("daily", inst.toSshCfg(d)), usage.cacheKeyFor("daily", inst.toSshCfg(d)));

// ── The async cases: generation gate + Remote-SSH adoption ───────────────────
// These are the two ordering bugs a live window would only show intermittently, so
// they are driven here with DEFERRED promises: nothing resolves until the test says so.
function deferred() {
  let resolve;
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
}

async function asyncTests() {
  console.log("\n=== generation gate (stale refresh after a switch) ===");
  const gate = inst.createGate("agent-vm");
  eq("gate: starts at generation 0", gate.generation, 0);
  eq("gate: tracks the starting name", gate.name, "agent-vm");
  const t0 = gate.token();
  ok("gate: a fresh token is valid", gate.valid(t0));
  ok("gate: re-setting the SAME name is not a change", gate.set("agent-vm") === false);
  ok("gate: ...so the token stays valid", gate.valid(t0));
  ok("gate: an empty name is ignored", gate.set("") === false && gate.valid(t0));
  ok("gate: switching is a change", gate.set("work-vm") === true);
  ok("gate: the old token is now invalid", !gate.valid(t0));
  ok("gate: a token issued after the switch is valid", gate.valid(gate.token()));
  ok("gate: a missing token is never valid", !gate.valid(null) && !gate.valid(undefined));

  // The real failure: instance A's slow probe resolves AFTER the user switched to B.
  // A refresh pipeline written the way extension.js writes it must drop A entirely —
  // no post, and no cache mutation — instead of stamping B's name onto A's payload.
  const posts = [];
  const cache = { value: null };
  const g = inst.createGate("agent-vm");
  // One pipeline per instance, each capturing its identity before its first await.
  const refresh = async (instanceName, probePromise) => {
    const token = g.token();
    const payload = await probePromise;              // stage 1: the SSH probe
    if (!g.valid(token)) return "discarded";
    cache.value = payload;                            // cache mutation is guarded too
    posts.push({ instance: token.name, payload });    // ...and so is the post
    return "posted";
  };

  const slowA = deferred();   // instance A's probe — resolves LAST
  const fastB = deferred();   // instance B's probe — resolves first
  const runA = refresh("agent-vm", slowA.promise);
  // The user switches to B while A is still in flight.
  g.set("work-vm");
  const runB = refresh("work-vm", fastB.promise);
  fastB.resolve({ from: "work-vm", agents: ["b"] });
  eq("gate: B's refresh posts", await runB, "posted");
  slowA.resolve({ from: "agent-vm", agents: ["a"] });
  eq("gate: A's late refresh is discarded", await runA, "discarded");
  eq("gate: exactly one post landed", posts.length, 1);
  eq("gate: the post is B's payload", posts[0].payload.from, "work-vm");
  eq("gate: the post is labelled B", posts[0].instance, "work-vm");
  eq("gate: A's late result did NOT overwrite the cache", cache.value.from, "work-vm");
  ok("gate: A's payload never reached a post", !posts.some((p) => p.payload.from === "agent-vm"));

  // A multi-stage pipeline: the switch can land between ANY two stages. `stopAfter` is
  // how many stages had already resolved when the user switched; 3 (= all of them) is
  // the positive control — that refresh finished BEFORE the switch and must still post.
  for (const stopAfter of [1, 2, 3]) {
    const expected = stopAfter === 3 ? "posted" : "discarded";
    const g2 = inst.createGate("agent-vm");
    const token = g2.token();
    const stages = [deferred(), deferred(), deferred()];
    const landed = [];
    const run = (async () => {
      for (let i = 0; i < stages.length; i++) {
        await stages[i].promise;
        if (!g2.valid(token)) return "discarded";
        landed.push(i);
      }
      return "posted";
    })();
    for (let i = 0; i < stopAfter; i++) stages[i].resolve(i);
    await new Promise((r) => setImmediate(r));
    g2.set("work-vm");
    for (let i = stopAfter; i < stages.length; i++) stages[i].resolve(i);
    eq(`gate: a switch with ${stopAfter} stage(s) already done -> ${expected}`, await run, expected);
    eq(`gate: no stage past ${stopAfter} landed`, landed.length, stopAfter);
  }

  console.log("\n=== user actions are bound to the instance they started on ===");
  const actReg = inst.load({ path: writeRegistry(JSON.stringify({
    version: 1, defaultInstance: "agent-vm",
    instances: { "agent-vm": {}, "work-vm": { sshHost: "buildbox.example.local", sshPort: 2201 } },
  })) });
  const A = inst.resolve(actReg, "agent-vm");
  const B = inst.resolve(actReg, "work-vm");

  // captureTarget freezes the cfg AND the identity at the entry point.
  const gA = inst.createGate("agent-vm");
  const capturedA = inst.captureTarget(gA, A);
  eq("target: captures the name", capturedA.name, "agent-vm");
  eq("target: captures the cfg host", capturedA.cfg.vmHost, "agent-vm.mshome.net");
  ok("target: not superseded while nothing changed", !inst.targetSuperseded(gA, capturedA));
  gA.set("work-vm");
  ok("target: superseded after a switch", inst.targetSuperseded(gA, capturedA));
  eq("target: the captured cfg is UNCHANGED by the switch", capturedA.cfg.vmHost, "agent-vm.mshome.net");
  ok("target: a fresh capture tracks the new instance", inst.captureTarget(gA, B).cfg.vmHost === "buildbox.example.local");

  // THE DESTRUCTIVE CASE: Shutdown is invoked on A, the user switches to B while the
  // confirm modal is open, then clicks "Shut down". The command must NOT power off B,
  // and (because we can no longer tell which VM the answer was about) must not power
  // off A either — it aborts and says so, mirroring runShutdown().
  const poweredOff = [];
  const warned = [];
  const gS = inst.createGate("agent-vm");
  const modal = deferred();
  const shutdown = (async () => {
    const t = inst.captureTarget(gS, A);          // captured BEFORE the modal opens
    const pick = await modal.promise;             // the modal can sit open for ever
    if (pick !== "Shut down") return "cancelled";
    if (inst.targetSuperseded(gS, t)) { warned.push(t.name); return "aborted"; }
    poweredOff.push(t.cfg.vmHost);
    return "sent";
  })();
  gS.set("work-vm");                              // the user switches while it is open
  modal.resolve("Shut down");
  eq("shutdown: aborts when the window switched during the confirm", await shutdown, "aborted");
  deepEq("shutdown: NOTHING was powered off", poweredOff, []);
  deepEq("shutdown: the user is told which instance it was for", warned, ["agent-vm"]);

  // The positive control: no switch => the poweroff goes to the instance that was
  // confirmed, over that instance's own cfg (not the module default).
  const gS2 = inst.createGate("work-vm");
  const modal2 = deferred();
  const sent = [];
  const shutdown2 = (async () => {
    const t = inst.captureTarget(gS2, B);
    if (await modal2.promise !== "Shut down") return "cancelled";
    if (inst.targetSuperseded(gS2, t)) return "aborted";
    sent.push({ host: t.cfg.vmHost, port: t.cfg.sshPort });
    return "sent";
  })();
  modal2.resolve("Shut down");
  eq("shutdown: with no switch it is sent", await shutdown2, "sent");
  deepEq("shutdown: sent to the confirmed instance's endpoint",
    sent, [{ host: "buildbox.example.local", port: 2201 }]);

  // THE LONG-RUNNING CASE: a clone runs for minutes on A, then opens the cloned path.
  // The folder opened must be on the VM the clone actually landed on — even though the
  // window switched to B while it ran. (runAddProject binds both to one target.)
  const gC = inst.createGate("agent-vm");
  const clone = deferred();
  const opened = [];
  const addProject = (async () => {
    const t = inst.captureTarget(gC, A);
    const ranOn = t.cfg.vmHost;                   // the clone dials the captured cfg
    await clone.promise;
    opened.push(t.cfg.vmHost);                    // ...and so does the open
    return ranOn;
  })();
  gC.set("work-vm");
  clone.resolve({ code: 0 });
  eq("clone: ran on the instance it started on", await addProject, "agent-vm.mshome.net");
  deepEq("clone: the opened folder is on the SAME VM, not the switched-to one",
    opened, ["agent-vm.mshome.net"]);

  // A rebuild is destructive AND has a slow project probe in the middle: it must abort
  // rather than delete whichever VM happens to be active when the probe returns.
  const gR = inst.createGate("agent-vm");
  const probeProjects = deferred();
  const launched = [];
  const rebuild = (async () => {
    const t = inst.captureTarget(gR, A);
    const projects = await probeProjects.promise;
    if (inst.targetSuperseded(gR, t)) return "aborted";
    launched.push({ instance: t.instance.name, projects });
    return "launched";
  })();
  gR.set("work-vm");
  probeProjects.resolve(["default"]);
  eq("rebuild: aborts when the window switched during the project probe", await rebuild, "aborted");
  deepEq("rebuild: no VM-deleting lifecycle run was launched", launched, []);

  console.log("\n=== Remote-SSH adoption ===");
  const adoptReg = inst.load({ path: writeRegistry(JSON.stringify({
    version: 1, defaultInstance: "agent-vm",
    instances: { "agent-vm": {}, "work-vm": { sshHost: "buildbox.example.local", sshPort: 2201 } },
  })) });
  const plan = (auth, setting, current) => inst.planRemoteAdoption(adoptReg, auth, setting, current);
  ok("adopt: a local window does not adopt", !plan(undefined, "", "agent-vm").adopt);
  ok("adopt: a non-ssh authority does not adopt", !plan("wsl+Ubuntu", "", "agent-vm").adopt);
  ok("adopt: an unknown host does not adopt", !plan("ssh-remote+somewhere", "", "agent-vm").adopt);
  ok("adopt: an explicit setting pin wins", !plan("ssh-remote+work-vm", "agent-vm", "agent-vm").adopt);
  ok("adopt: already-active is a no-op", !plan("ssh-remote+work-vm", "", "work-vm").adopt);
  eq("adopt: attached to work-vm adopts it", plan("ssh-remote+work-vm", "", "agent-vm").name, "work-vm");
  eq("adopt: matches on the hostname too",
    plan("ssh-remote+buildbox.example.local", "", "agent-vm").name, "work-vm");

  // The ordering bug: workspaceState.update is a Thenable. A DELAYED memento must be
  // awaited, or activation carries on with the old selection and probes the wrong VM.
  const slowMemento = deferred();
  let stored = "agent-vm";
  let settled = false;
  const adoption = inst.adoptRemoteInstance({
    registry: adoptReg, remoteAuthority: "ssh-remote+work-vm", setting: "", currentName: "agent-vm",
    setActive: (name) => slowMemento.promise.then(() => { stored = name; }),
  }).then((r) => { settled = true; return r; });
  await new Promise((r) => setImmediate(r));
  ok("adopt: does NOT resolve while the memento write is pending", !settled);
  eq("adopt: the selection is still the old one meanwhile", stored, "agent-vm");
  slowMemento.resolve();
  const res = await adoption;
  ok("adopt: resolves only after the write landed", res.adopt && res.persisted);
  eq("adopt: the selection really changed before the caller continued", stored, "work-vm");
  // A memento that rejects is reported, never thrown (activation must not die).
  const failed = await inst.adoptRemoteInstance({
    registry: adoptReg, remoteAuthority: "ssh-remote+work-vm", setting: "", currentName: "agent-vm",
    setActive: () => Promise.reject(new Error("memento is read-only")),
  });
  ok("adopt: a failed persist is reported, not thrown", failed.adopt && !failed.persisted && !!failed.error);
  // No setActive at all must not throw either.
  ok("adopt: a missing setActive is tolerated",
    (await inst.adoptRemoteInstance({ registry: adoptReg, remoteAuthority: "ssh-remote+work-vm", setting: "", currentName: "agent-vm" })).adopt);
}

asyncTests().then(() => {
  try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (_) {}
  console.log(`\ninstance-registry unit tests — ${pass}/${pass + fail} passed`);
  if (fail) process.exit(1);
}, (e) => {
  console.log("  FAIL  async tests threw: " + (e && e.stack ? e.stack : e));
  process.exit(1);
});
